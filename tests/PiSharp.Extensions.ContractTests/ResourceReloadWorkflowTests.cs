using PiSharp.Extensions.Runtime.Reloading;

internal static class ResourceReloadWorkflowTests
{
    internal const string Prefix = "resource-reload.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "bounded-immutable-extensible-catalog-diff", Catalog),
        (Prefix + "successful-reload-joins-retirement-before-start", Success),
        (Prefix + "held-invalidation-joins-despite-host-cancellation", HeldInvalidation),
        (Prefix + "stage-failure-preserves-old-authority", StageFailure),
        (Prefix + "shutdown-reporter-failure-joins-candidate-rollback", ShutdownFailure),
        (Prefix + "invalidation-failure-does-not-invent-old-authority", InvalidationFailure),
        (Prefix + "rejected-publication-cleans-both-with-original-causes", NoPublication),
        (Prefix + "uncertain-publication-retains-candidate-for-owner-recovery", UncertainPublication),
        (Prefix + "post-commit-failures-preserve-new-authority", PostCommitFailure),
        (Prefix + "admission-cancellation-original-join-and-self-wait", CancellationAndReentry)
    ];

    private static Task Catalog()
    {
        var current = new List<ResourceReloadEntry>
        { new("skills", "same", "a"), new("extensions", "removed", "b"), new("settings", "changed", "c") };
        var replacement = new List<ResourceReloadEntry>
        { new("skills", "same", "a"), new("settings", "changed", "d"), new("future-host-kind", "new", "e") };
        var plan = new ResourceReloadPlan(current, replacement, 3);
        current.Clear(); replacement.Clear();
        Equal(3, plan.Current.Length); Equal(3, plan.Replacement.Length);
        Sequence(plan.Changes.Select(change => change.Change), ResourceReloadChangeKind.Changed,
            ResourceReloadChangeKind.Added, ResourceReloadChangeKind.Removed);
        Equal("c", plan.Changes[0].Previous!.Fingerprint); Equal("d", plan.Changes[0].Replacement!.Fingerprint);
        Equal(0, new ResourceReloadPlan(plan.Current, plan.Current).Changes.Length);
        Throws<ArgumentException>(() => new ResourceReloadPlan(plan.Current, [], 2));
        Throws<ArgumentException>(() => new ResourceReloadPlan([new("x", "y", "z"), new("x", "y", "other")], []));
        Throws<ArgumentException>(() => new ResourceReloadPlan([new("x", "y", new string('a', 2049))], []));
        Equal(2, new ResourceReloadPlan([new("x", "y", "z"), new("X", "y", "z")], []).Current.Length);
        return Task.CompletedTask;
    }

    private static async Task Success()
    {
        using var f = new Fixture(); var entered = Gate(); var release = Gate();
        f.Cleanup = async generation =>
        {
            f.Log.Add("cleanup:" + generation.Name); entered.TrySetResult(); await release.Task;
            generation.Cleaned = true;
        };
        var workflow = f.Workflow(); var running = workflow.ReloadAsync();
        await entered.Task;
        try
        {
            False(running.IsCompleted); Same(running, workflow.ReloadAsync());
            False(f.Old.Active); Throws<InvalidOperationException>(f.Old.Admit);
            False(f.Old.Cleaned); False(f.Log.Contains("start:new"));
        }
        finally { release.TrySetResult(); await running; }
        var receipt = await running;
        Equal(ResourceReloadAuthority.New, receipt.Authority); Equal(0, receipt.Failures.Length);
        True(receipt.ShutdownCompleted && receipt.OldInvalidatedAndDrained && receipt.OldCleanupAttempted);
        False(receipt.CandidateCleanupAttempted); Same(f.New, receipt.Candidate!);
        True(f.Old.Cleaned); False(f.New.Cleaned);
        Sequence(f.Log, "stage", "shutdown:old", "invalidate:old", "publish", "cleanup:old", "start:new");
        Same(running, workflow.ReloadAsync());
    }

    private static async Task HeldInvalidation()
    {
        using var f = new Fixture(); var entered = Gate(); var release = Gate();
        var startCancellation = new OperationCanceledException(f.Lifetime.Token);
        ResourceReloadWorkflow<Generation>? workflow = null;
        f.Invalidate = async generation =>
        {
            generation.Active = false; entered.TrySetResult(); await release.Task;
            Throws<InvalidOperationException>(() => workflow!.ReloadAsync());
        };
        f.Cleanup = generation =>
        {
            Throws<InvalidOperationException>(() => workflow!.ReloadAsync());
            generation.Cleaned = true; return ValueTask.CompletedTask;
        };
        f.Start = (_, token) => { True(token.IsCancellationRequested); throw startCancellation; };
        workflow = f.Workflow(); var running = workflow.ReloadAsync(); await entered.Task;
        try
        {
            f.Lifetime.Cancel(); False(running.IsCompleted); Same(running, workflow.ReloadAsync());
            Throws<InvalidOperationException>(f.Old.Admit); False(f.Old.Cleaned);
        }
        finally { release.TrySetResult(); await running; }
        var result = await running;
        Equal(ResourceReloadAuthority.New, result.Authority); True(result.OldInvalidatedAndDrained && f.Old.Cleaned);
        Same(startCancellation, result.Failures.Single().Cause); False(result.CandidateCleanupAttempted);
    }

    private static async Task StageFailure()
    {
        using var f = new Fixture(); var cause = new InvalidOperationException("stage");
        f.Stage = (_, _) => throw cause;
        var result = await f.Workflow().ReloadAsync();
        Equal(ResourceReloadAuthority.Old, result.Authority); Same(cause, result.Failures.Single().Cause);
        Equal(ResourceReloadPhase.Stage, result.Failures.Single().Phase);
        True(f.Old.Active); False(result.ShutdownCompleted || result.OldCleanupAttempted || result.CandidateCleanupAttempted);
        using var alias = new Fixture(); alias.Stage = (_, _) => ValueTask.FromResult(alias.Old);
        var malformed = await alias.Workflow().ReloadAsync();
        Equal(ResourceReloadAuthority.Old, malformed.Authority); False(alias.Old.Cleaned);
        True(malformed.Candidate is null);
    }

    private static async Task ShutdownFailure()
    {
        using var f = new Fixture(); var reporter = new InvalidOperationException("trusted reporter");
        var rollback = new InvalidOperationException("candidate cleanup");
        var entered = Gate(); var release = Gate();
        f.Shutdown = (_, _) => throw reporter;
        f.Cleanup = async generation =>
        { Same(f.New, generation); entered.TrySetResult(); await release.Task; throw rollback; };
        var running = f.Workflow().ReloadAsync(); await entered.Task;
        try { False(running.IsCompleted); True(f.Old.Active); }
        finally { release.TrySetResult(); await running; }
        var result = await running;
        Equal(ResourceReloadAuthority.Old, result.Authority); True(result.CandidateCleanupAttempted);
        False(result.OldCleanupAttempted || result.ShutdownCompleted);
        Sequence(result.Failures.Select(failure => failure.Cause), reporter, rollback);
        Sequence(result.Failures.Select(failure => failure.Phase), ResourceReloadPhase.Shutdown, ResourceReloadPhase.RollbackCandidate);
    }

    private static async Task InvalidationFailure()
    {
        using var f = new Fixture(); var cause = new InvalidOperationException("revoked but drain failed");
        f.Invalidate = generation => { generation.Active = false; throw cause; };
        var result = await f.Workflow().ReloadAsync();
        Equal(ResourceReloadAuthority.Unknown, result.Authority); True(result.ShutdownCompleted);
        False(result.OldInvalidatedAndDrained || result.OldCleanupAttempted);
        True(result.CandidateCleanupAttempted && f.New.Cleaned); False(f.Old.Cleaned);
        Same(cause, result.Failures.Single().Cause); False(f.Log.Contains("publish"));
    }

    private static async Task NoPublication()
    {
        using var f = new Fixture(); var publication = new InvalidOperationException("not committed");
        var oldCleanup = new InvalidOperationException("old cleanup");
        var candidateCleanup = new InvalidOperationException("candidate cleanup");
        f.Publish = (_, _) => ValueTask.FromResult(new ResourceReloadPublication(ResourceReloadAuthority.None, publication));
        f.Cleanup = generation => throw (ReferenceEquals(generation, f.Old) ? oldCleanup : candidateCleanup);
        var result = await f.Workflow().ReloadAsync();
        Equal(ResourceReloadAuthority.None, result.Authority);
        True(result.OldInvalidatedAndDrained && result.OldCleanupAttempted && result.CandidateCleanupAttempted);
        Sequence(result.Failures.Select(failure => failure.Cause), publication, oldCleanup, candidateCleanup);
        False(f.Log.Contains("start:new")); False(f.Old.Active);
    }

    private static async Task UncertainPublication()
    {
        using var f = new Fixture(); var cause = new InvalidOperationException("commit acknowledgement lost");
        f.Publish = (_, _) => throw cause;
        var result = await f.Workflow().ReloadAsync();
        Equal(ResourceReloadAuthority.Unknown, result.Authority); Same(cause, result.Failures.Single().Cause);
        True(result.OldCleanupAttempted && f.Old.Cleaned); False(result.CandidateCleanupAttempted || f.New.Cleaned);
        Same(f.New, result.Candidate!); False(f.Log.Contains("start:new"));
        using var malformed = new Fixture();
        malformed.Publish = (_, _) => ValueTask.FromResult(new ResourceReloadPublication(ResourceReloadAuthority.Old));
        var rejected = await malformed.Workflow().ReloadAsync();
        Equal(ResourceReloadAuthority.Unknown, rejected.Authority); False(malformed.New.Cleaned);
    }

    private static async Task PostCommitFailure()
    {
        using var retirement = new Fixture(); var cleanup = new InvalidOperationException("retirement");
        retirement.Cleanup = _ => throw cleanup;
        var result = await retirement.Workflow().ReloadAsync();
        Equal(ResourceReloadAuthority.New, result.Authority); Same(cleanup, result.Failures.Single().Cause);
        False(result.CandidateCleanupAttempted); False(retirement.Log.Contains("start:new"));
        using var started = new Fixture(); var reporter = new InvalidOperationException("new reporter");
        started.Start = (_, _) => throw reporter;
        var second = await started.Workflow().ReloadAsync();
        Equal(ResourceReloadAuthority.New, second.Authority); Same(reporter, second.Failures.Single().Cause);
        Equal(ResourceReloadPhase.StartAndExtend, second.Failures.Single().Phase); True(started.Old.Cleaned);
        False(started.New.Cleaned);
        using var acknowledged = new Fixture();
        acknowledged.Publish = (_, _) => ValueTask.FromResult(new ResourceReloadPublication(ResourceReloadAuthority.New, reporter));
        var third = await acknowledged.Workflow().ReloadAsync();
        Equal(ResourceReloadAuthority.New, third.Authority); Same(reporter, third.Failures.Single().Cause);
        True(third.OldCleanupAttempted); False(third.CandidateCleanupAttempted);
    }

    private static async Task CancellationAndReentry()
    {
        using var f = new Fixture(); using var admission = new CancellationTokenSource(); admission.Cancel();
        var workflow = f.Workflow(); Throws<OperationCanceledException>(() => workflow.ReloadAsync(admission.Token));
        Equal(0, f.Log.Count);
        var entered = Gate(); var release = Gate(); using var caller = new CancellationTokenSource();
        f.Shutdown = async (_, token) =>
        {
            Equal(f.Lifetime.Token, token);
            Throws<InvalidOperationException>(() => workflow.ReloadAsync());
            entered.TrySetResult(); await release.Task;
        };
        var running = workflow.ReloadAsync(caller.Token); await entered.Task;
        try { caller.Cancel(); False(running.IsCompleted); Same(running, workflow.ReloadAsync()); }
        finally { release.TrySetResult(); await running; }
        Equal(ResourceReloadAuthority.New, (await running).Authority);
        using var foreign = new Fixture(); using var other = new CancellationTokenSource(); other.Cancel();
        var cancellation = new OperationCanceledException(other.Token);
        foreign.Shutdown = (_, _) => throw cancellation;
        var receipt = await foreign.Workflow().ReloadAsync();
        Same(cancellation, receipt.Failures.Single().Cause); Equal(ResourceReloadAuthority.Old, receipt.Authority);
        True(receipt.CandidateCleanupAttempted);
        using var cancelled = new Fixture(); cancelled.Lifetime.Cancel();
        var stopped = await cancelled.Workflow().ReloadAsync();
        Equal(ResourceReloadAuthority.Old, stopped.Authority); Equal(0, cancelled.Log.Count);
        using var afterStage = new Fixture();
        afterStage.Stage = (_, _) => { afterStage.Lifetime.Cancel(); return ValueTask.FromResult(afterStage.New); };
        var staged = await afterStage.Workflow().ReloadAsync();
        Equal(ResourceReloadAuthority.Old, staged.Authority); True(staged.CandidateCleanupAttempted && afterStage.New.Cleaned);
        False(staged.ShutdownCompleted || staged.OldCleanupAttempted);
    }

    private sealed class Generation(string name)
    {
        internal string Name { get; } = name;
        internal bool Active { get; set; } = true;
        internal bool Cleaned { get; set; }
        internal void Admit() { if (!Active) throw new InvalidOperationException("Stale generation."); }
    }

    private sealed class Fixture : IDisposable
    {
        internal Generation Old { get; } = new("old");
        internal Generation New { get; } = new("new");
        internal CancellationTokenSource Lifetime { get; } = new();
        internal List<string> Log { get; } = [];
        internal Func<ResourceReloadPlan, CancellationToken, ValueTask<Generation>> Stage;
        internal Func<Generation, CancellationToken, ValueTask> Shutdown;
        internal Func<Generation, ValueTask> Invalidate;
        internal Func<Generation, Generation, ValueTask<ResourceReloadPublication>> Publish;
        internal Func<Generation, ValueTask> Cleanup;
        internal Func<Generation, CancellationToken, ValueTask> Start;
        internal Fixture()
        {
            Stage = (_, _) => { Log.Add("stage"); return ValueTask.FromResult(New); };
            Shutdown = (generation, _) => { Log.Add("shutdown:" + generation.Name); return ValueTask.CompletedTask; };
            Invalidate = generation => { Log.Add("invalidate:" + generation.Name); generation.Active = false; return ValueTask.CompletedTask; };
            Publish = (_, _) => { Log.Add("publish"); return ValueTask.FromResult(new ResourceReloadPublication(ResourceReloadAuthority.New)); };
            Cleanup = generation => { Log.Add("cleanup:" + generation.Name); generation.Cleaned = true; return ValueTask.CompletedTask; };
            Start = (generation, _) => { Log.Add("start:" + generation.Name); return ValueTask.CompletedTask; };
        }
        internal ResourceReloadWorkflow<Generation> Workflow() => new(Old, new ResourceReloadPlan([], []), new()
        {
            StageAsync = (plan, token) => Stage(plan, token), ShutdownAsync = (generation, token) => Shutdown(generation, token),
            InvalidateAndDrainAsync = generation => Invalidate(generation), PublishAsync = (old, next) => Publish(old, next),
            CleanupAsync = generation => Cleanup(generation), StartAndExtendAsync = (generation, token) => Start(generation, token)
        }, Lifetime.Token);
        public void Dispose() => Lifetime.Dispose();
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    private static void False(bool value) => True(!value);
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
    private static void Same(object expected, object actual) { if (!ReferenceEquals(expected, actual)) throw new InvalidOperationException("Original identity not retained."); }
    private static void Sequence<T>(IEnumerable<T> actual, params T[] expected) => True(actual.SequenceEqual(expected));
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
