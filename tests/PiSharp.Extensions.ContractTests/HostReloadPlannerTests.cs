using PiSharp.Extensions.Abstractions.Reloading;
using PiSharp.Extensions.Runtime.Reloading;

internal static class HostReloadPlannerTests
{
    internal const string Prefix = "host-reload.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "captured-flags-tool-policy-and-lifecycle", Success),
        (Prefix + "explicit-allowlist-exclusions-and-no-bindings", Allowlist),
        (Prefix + "held-stage-failure-joins-candidate-cleanup", StageFailure),
        (Prefix + "stage-and-cleanup-retain-both-original-faults", StageCleanupFailure),
        (Prefix + "held-publication-keeps-original-join-and-stale-context", HeldPublication),
        (Prefix + "stale-publication-receipt-retains-candidate", StaleReceipt),
        (Prefix + "held-retirement-prevents-start-and-keeps-new-authority", RetirementFailure),
        (Prefix + "startup-fault-stops-following-hooks", StartupFailure),
        (Prefix + "rejected-publication-cleans-both", RejectedPublication),
        (Prefix + "candidate-cannot-reuse-current-payload-or-id", InvalidCandidate),
        (Prefix + "held-stage-retains-completed-original-task-faults", HeldStageOriginalFaults),
        (Prefix + "held-retirement-retains-completed-original-task-faults", HeldRetirementOriginalFaults),
        (Prefix + "original-task-cancellation-versus-faulted-oce", CancellationClassification),
        (Prefix + "rejects-all-multicast-slots-before-invocation", MulticastRejection)
    ];

    private static async Task Success()
    {
        using var f = new Fixture(); var running = f.Planner().ReloadAsync(); var receipt = await running;
        Equal(ResourceReloadAuthority.New, receipt.Authority); Equal(0, receipt.Failures.Length);
        Equal(false, receipt.Candidate!.Flags["boolean"].Boolean!.Value);
        Equal("", receipt.Candidate.Flags["text"].Text!); Equal("new", receipt.Candidate.Flags["new"].Text!);
        Equal("retained", receipt.Candidate.Flags["removed-definition"].Text!);
        Sequence(receipt.Candidate.ActiveTools, "read", "opt-in", "default", "model-only");
        Sequence(f.Log, "settings", "queue", "providers", "resources", "describe", "build", "shutdown:reload",
            "invalidate", "publish", "cleanup:old", "before-start", "start:reload", "report", "extend:reload");
        False(f.Old.Active); Throws<InvalidOperationException>(f.Old.Admit);
    }

    private static async Task Allowlist()
    {
        using var f = new Fixture(false); var allowed = new List<string> { "off", "read", "hidden", "base", "default" };
        var excluded = new List<string> { "default" }; var planner = f.Planner(allowed, excluded);
        allowed.Clear(); excluded.Clear();
        var receipt = await planner.ReloadAsync();
        Sequence(receipt.Candidate!.ActiveTools, "read", "off", "base");
        False(f.Log.Contains("before-start")); False(f.Log.Contains("start:reload"));
        False(f.Log.Contains("report")); False(f.Log.Contains("extend:reload"));
    }

    private static async Task StageFailure()
    {
        using var f = new Fixture(); var entered = Gate(); var release = Gate(); var cause = new Exception("resources");
        f.Resources = _ => throw cause;
        f.Cleanup = async payload => { f.Log.Add("cleanup:" + payload.Name); entered.SetResult(); await release.Task; };
        var planner = f.Planner(); var running = planner.ReloadAsync(); await entered.Task;
        try { False(running.IsCompleted); Same(running, planner.ReloadAsync()); True(f.Old.Active); False(f.Log.Contains("shutdown:reload")); }
        finally { release.SetResult(); }
        var receipt = await running; Equal(ResourceReloadAuthority.Old, receipt.Authority);
        Same(cause, receipt.Failures.Single().Cause); Equal(ResourceReloadPhase.Stage, receipt.Failures.Single().Phase);
        True(receipt.Candidate is null); False(f.Log.Contains("cleanup:old"));
    }

    private static async Task StageCleanupFailure()
    {
        using var f = new Fixture(); var cause = new Exception("build"); var cleanup = new Exception("cleanup");
        f.Build = _ => throw cause; f.Cleanup = _ => throw cleanup;
        var receipt = await f.Planner().ReloadAsync();
        var both = receipt.Failures.Single().Cause as AggregateException ?? throw new Exception("Missing original faults.");
        Same(cause, both.InnerExceptions[0]); Same(cleanup, both.InnerExceptions[1]); True(f.Old.Active);
    }

    private static async Task HeldPublication()
    {
        using var f = new Fixture(); var entered = Gate(); var release = Gate();
        f.Publish = async (old, candidate) => { entered.SetResult(); await release.Task; return new(old.Id, candidate.Id, HostReloadPublicationAuthority.New); };
        var planner = f.Planner(); var running = planner.ReloadAsync(); await entered.Task;
        try
        {
            False(running.IsCompleted); Same(running, planner.ReloadAsync()); Throws<InvalidOperationException>(f.Old.Admit);
            False(f.Log.Contains("cleanup:old")); False(f.Log.Contains("before-start"));
            f.Lifetime.Cancel(); False(running.IsCompleted);
        }
        finally { release.SetResult(); }
        var receipt = await running; Equal(ResourceReloadAuthority.New, receipt.Authority);
        True(receipt.OldInvalidatedAndDrained); True(receipt.OldCleanupAttempted);
    }

    private static async Task StaleReceipt()
    {
        using var f = new Fixture(); f.Publish = (old, candidate) => ValueTask.FromResult(new HostReloadPublicationReceipt(old.Id - 1, candidate.Id, HostReloadPublicationAuthority.New));
        var receipt = await f.Planner().ReloadAsync();
        Equal(ResourceReloadAuthority.Unknown, receipt.Authority); True(receipt.Failures.Single().Cause is InvalidOperationException);
        False(receipt.CandidateCleanupAttempted); True(f.Log.Contains("cleanup:old")); False(f.Log.Contains("before-start"));
        Throws<InvalidOperationException>(f.Old.Admit);
    }

    private static async Task RetirementFailure()
    {
        using var f = new Fixture(); var entered = Gate(); var release = Gate(); var cause = new Exception("retire");
        f.Cleanup = async payload => { f.Log.Add("cleanup:" + payload.Name); entered.SetResult(); await release.Task; throw cause; };
        var running = f.Planner().ReloadAsync(); await entered.Task;
        try { False(running.IsCompleted); False(f.Log.Contains("before-start")); }
        finally { release.SetResult(); }
        var receipt = await running; Equal(ResourceReloadAuthority.New, receipt.Authority);
        Same(cause, ((HostReloadCallbackFailure)receipt.Failures.Single().Cause).AwaitedCause);
        Equal(ResourceReloadPhase.RetireOld, receipt.Failures.Single().Phase);
        False(f.Log.Contains("before-start")); False(receipt.CandidateCleanupAttempted);
    }

    private static async Task StartupFailure()
    {
        using var f = new Fixture(); var cause = new Exception("start"); f.Start = _ => throw cause;
        var receipt = await f.Planner().ReloadAsync();
        Equal(ResourceReloadAuthority.New, receipt.Authority); Same(cause, receipt.Failures.Single().Cause);
        Equal(ResourceReloadPhase.StartAndExtend, receipt.Failures.Single().Phase);
        True(f.Log.Contains("before-start")); False(f.Log.Contains("report")); False(f.Log.Contains("extend:reload"));
    }

    private static async Task RejectedPublication()
    {
        using var f = new Fixture(); var cause = new Exception("generation changed");
        f.Publish = (old, candidate) => ValueTask.FromResult(new HostReloadPublicationReceipt(old.Id, candidate.Id, HostReloadPublicationAuthority.None, cause));
        var receipt = await f.Planner().ReloadAsync(); Equal(ResourceReloadAuthority.None, receipt.Authority);
        Same(cause, receipt.Failures.Single().Cause); True(receipt.OldCleanupAttempted && receipt.CandidateCleanupAttempted);
        Sequence(f.Log.TakeLast(2), "cleanup:old", "cleanup:new"); False(f.Log.Contains("before-start"));
    }

    private static async Task InvalidCandidate()
    {
        using var f = new Fixture(); Throws<ArgumentOutOfRangeException>(() => f.Planner(candidateId: 1));
        f.Settings = _ => ValueTask.FromResult(f.Old);
        var receipt = await f.Planner().ReloadAsync(); Equal(ResourceReloadAuthority.Old, receipt.Authority);
        True(receipt.Candidate is null); True(f.Old.Active); False(f.Log.Any(item => item.StartsWith("cleanup:")));
    }

    private static async Task HeldStageOriginalFaults()
    {
        using var f = new Fixture(); var release = new TaskCompletionSource(); var sibling = new TaskCompletionSource();
        var original = Task.WhenAll(release.Task, sibling.Task); var one = new Exception("one"); var two = new Exception("two");
        f.Build = _ => new ValueTask(original);
        var planner = f.Planner(); var running = planner.ReloadAsync();
        False(running.IsCompleted); Same(running, planner.ReloadAsync()); True(f.Old.Active);
        sibling.SetException(two); False(running.IsCompleted); False(f.Log.Any(item => item.StartsWith("cleanup:")));
        release.SetException(one); var receipt = await running;
        var failure = (HostReloadCallbackFailure)receipt.Failures.Single().Cause;
        Equal(original.Exception!.InnerExceptions.Count, failure.OriginalTaskException!.InnerExceptions.Count); False(failure.OriginalTaskIsCanceled);
        True(failure.OriginalTaskException!.InnerExceptions.Any(error => ReferenceEquals(error, one)));
        True(failure.OriginalTaskException.InnerExceptions.Any(error => ReferenceEquals(error, two)));
        Equal(ResourceReloadAuthority.Old, receipt.Authority); Sequence(f.Log.TakeLast(1), "cleanup:new");
    }

    private static async Task HeldRetirementOriginalFaults()
    {
        using var f = new Fixture(); var release = new TaskCompletionSource(); var sibling = new TaskCompletionSource();
        var original = Task.WhenAll(release.Task, sibling.Task); var one = new Exception("retire-one"); var two = new Exception("retire-two");
        f.Cleanup = _ => new ValueTask(original);
        var planner = f.Planner(); var running = planner.ReloadAsync();
        False(running.IsCompleted); Same(running, planner.ReloadAsync()); Throws<InvalidOperationException>(f.Old.Admit);
        f.Lifetime.Cancel(); sibling.SetException(two); False(running.IsCompleted); False(f.Log.Contains("before-start"));
        release.SetException(one); var receipt = await running;
        var failure = (HostReloadCallbackFailure)receipt.Failures.Single().Cause;
        Equal(original.Exception!.InnerExceptions.Count, failure.OriginalTaskException!.InnerExceptions.Count); Equal(2, failure.OriginalTaskException.InnerExceptions.Count);
        True(failure.OriginalTaskException.InnerExceptions.Any(error => ReferenceEquals(error, one)));
        True(failure.OriginalTaskException.InnerExceptions.Any(error => ReferenceEquals(error, two)));
        Equal(ResourceReloadAuthority.New, receipt.Authority); False(receipt.CandidateCleanupAttempted); False(f.Log.Contains("before-start"));
    }

    private static async Task CancellationClassification()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var f = new Fixture(); var fault = new OperationCanceledException(canceled.Token);
        var faultedOriginal = Task.FromException(fault); f.Build = _ => new ValueTask(faultedOriginal);
        var faultReceipt = await f.Planner().ReloadAsync(); var faultFailure = (HostReloadCallbackFailure)faultReceipt.Failures.Single().Cause;
        False(faultFailure.OriginalTaskIsCanceled); Equal(faultedOriginal.Exception!.InnerExceptions.Count, faultFailure.OriginalTaskException!.InnerExceptions.Count);
        Same(fault, faultFailure.OriginalTaskException!.InnerExceptions.Single());
        using var second = new Fixture(); var canceledOriginal = Task.FromCanceled(canceled.Token); second.Build = _ => new ValueTask(canceledOriginal);
        var canceledReceipt = await second.Planner().ReloadAsync(); var canceledFailure = (HostReloadCallbackFailure)canceledReceipt.Failures.Single().Cause;
        True(canceledFailure.OriginalTaskIsCanceled); True(canceledFailure.OriginalTaskException is null);
        True(canceledFailure.AwaitedCause is OperationCanceledException);
    }

    private static Task MulticastRejection()
    {
        using var f = new Fixture();
        for (var slot = 0; slot < 14; slot++)
        {
            var selected = slot;
            Throws<ArgumentException>(() => f.Planner(transform: operations => MulticastAt(operations, selected)));
            Equal(0, f.Log.Count); True(f.Old.Active);
        }
        return Task.CompletedTask;
    }

    private static HostReloadOperations<Payload> MulticastAt(HostReloadOperations<Payload> o, int slot) => new()
    {
        StageSettingsAsync = slot == 0 ? o.StageSettingsAsync + o.StageSettingsAsync : o.StageSettingsAsync,
        SyncQueueModesAsync = slot == 1 ? o.SyncQueueModesAsync + o.SyncQueueModesAsync : o.SyncQueueModesAsync,
        ResetApiProvidersAsync = slot == 2 ? o.ResetApiProvidersAsync + o.ResetApiProvidersAsync : o.ResetApiProvidersAsync,
        ReloadResourcesAsync = slot == 3 ? o.ReloadResourcesAsync + o.ReloadResourcesAsync : o.ReloadResourcesAsync,
        DescribeRuntimeAsync = slot == 4 ? o.DescribeRuntimeAsync + o.DescribeRuntimeAsync : o.DescribeRuntimeAsync,
        BuildRuntimeAsync = slot == 5 ? o.BuildRuntimeAsync + o.BuildRuntimeAsync : o.BuildRuntimeAsync,
        SessionShutdownAsync = slot == 6 ? o.SessionShutdownAsync + o.SessionShutdownAsync : o.SessionShutdownAsync,
        InvalidateAndDrainAsync = slot == 7 ? o.InvalidateAndDrainAsync + o.InvalidateAndDrainAsync : o.InvalidateAndDrainAsync,
        PublishAsync = slot == 8 ? o.PublishAsync + o.PublishAsync : o.PublishAsync,
        CleanupAsync = slot == 9 ? o.CleanupAsync + o.CleanupAsync : o.CleanupAsync,
        BeforeSessionStartAsync = slot == 10 ? o.BeforeSessionStartAsync + o.BeforeSessionStartAsync : o.BeforeSessionStartAsync,
        SessionStartAsync = slot == 11 ? o.SessionStartAsync + o.SessionStartAsync : o.SessionStartAsync,
        ReportUnhandledMcpServersAsync = slot == 12 ? o.ReportUnhandledMcpServersAsync + o.ReportUnhandledMcpServersAsync : o.ReportUnhandledMcpServersAsync,
        ExtendResourcesAsync = slot == 13 ? o.ExtendResourcesAsync + o.ExtendResourcesAsync : o.ExtendResourcesAsync
    };

    private sealed class Payload(string name)
    {
        internal string Name { get; } = name;
        internal bool Active { get; set; } = true;
        internal void Admit() { if (!Active) throw new InvalidOperationException("Stale context."); }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Payload Old = new("old"), New = new("new");
        internal readonly CancellationTokenSource Lifetime = new();
        internal readonly List<string> Log = [];
        private readonly HostReloadGeneration<Payload> current;
        internal Func<CancellationToken, ValueTask<Payload>> Settings;
        internal Func<CancellationToken, ValueTask> Resources = _ => ValueTask.CompletedTask;
        internal Func<HostReloadGeneration<Payload>, ValueTask> Build = _ => ValueTask.CompletedTask;
        internal Func<HostReloadGeneration<Payload>, HostReloadGeneration<Payload>, ValueTask<HostReloadPublicationReceipt>> Publish =
            (old, candidate) => ValueTask.FromResult(new HostReloadPublicationReceipt(old.Id, candidate.Id, HostReloadPublicationAuthority.New));
        internal Func<Payload, ValueTask> Cleanup;
        internal Func<CancellationToken, ValueTask> Start = _ => ValueTask.CompletedTask;
        internal Fixture(bool bindings = true)
        {
            var flags = new Dictionary<string, HostReloadFlagValue>
            { ["boolean"] = new(false), ["text"] = new(""), ["removed-definition"] = new("retained") };
            var active = new List<string> { "read", "opt-in", "removed", "hidden", "codemode", "deferred" };
            current = new(1, Old, flags, active, bindings); flags.Clear(); active.Clear();
            Settings = _ => ValueTask.FromResult(New);
            Cleanup = payload => { Log.Add("cleanup:" + payload.Name); return ValueTask.CompletedTask; };
        }
        internal HostReloadPlanner<Payload> Planner(IEnumerable<string>? allowed = null, IEnumerable<string>? excluded = null,
            long candidateId = 2, Func<HostReloadOperations<Payload>, HostReloadOperations<Payload>>? transform = null)
        {
            var operations = new HostReloadOperations<Payload>()
            {
                StageSettingsAsync = token => { Log.Add("settings"); return Settings(token); },
                SyncQueueModesAsync = (_, _) => Record("queue"), ResetApiProvidersAsync = (_, _) => Record("providers"),
                ReloadResourcesAsync = (_, token) => { Log.Add("resources"); return Resources(token); },
                DescribeRuntimeAsync = (_, _) =>
                {
                    Log.Add("describe");
                    return ValueTask.FromResult(new HostReloadRuntime(new Dictionary<string, HostReloadFlagValue>
                    { ["boolean"] = new(true), ["text"] = new("default"), ["new"] = new("new") },
                    [new("read", false, HostReloadToolExposure.Direct), new("opt-in", true, HostReloadToolExposure.Direct, false),
                     new("default", true, HostReloadToolExposure.Direct), new("model-only", true, HostReloadToolExposure.ModelOnly),
                     new("off", true, HostReloadToolExposure.Direct, false), new("hidden", true, HostReloadToolExposure.Hidden),
                     new("codemode", true, HostReloadToolExposure.Codemode), new("deferred", true, HostReloadToolExposure.Deferred),
                     new("base", false, HostReloadToolExposure.Direct)]));
                },
                BuildRuntimeAsync = (candidate, _) => { Log.Add("build"); return Build(candidate); },
                SessionShutdownAsync = (_, reason, _) => Record("shutdown:" + reason),
                InvalidateAndDrainAsync = old => { Log.Add("invalidate"); old.Payload.Active = false; return ValueTask.CompletedTask; },
                PublishAsync = (old, candidate) => { Log.Add("publish"); return Publish(old, candidate); },
                CleanupAsync = Cleanup, BeforeSessionStartAsync = (_, _) => Record("before-start"),
                SessionStartAsync = (_, reason, token) => { Log.Add("start:" + reason); return Start(token); },
                ReportUnhandledMcpServersAsync = (_, _) => Record("report"), ExtendResourcesAsync = (_, reason, _) => Record("extend:" + reason)
            };
            return new(current, candidateId, new([], []), transform is null ? operations : transform(operations), Lifetime.Token, allowed, excluded);
        }
        private ValueTask Record(string value) { Log.Add(value); return ValueTask.CompletedTask; }
        public void Dispose() => Lifetime.Dispose();
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void True(bool value) { if (!value) throw new Exception("Expected true."); }
    private static void False(bool value) => True(!value);
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}."); }
    private static void Same(object expected, object actual) => True(ReferenceEquals(expected, actual));
    private static void Sequence<T>(IEnumerable<T> actual, params T[] expected) => True(actual.SequenceEqual(expected));
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
