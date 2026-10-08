using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Reloading;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;
using PiSharp.Extensions.Abstractions.Reloading;
using PiSharp.Extensions.Runtime.Reloading;
using PiSharp.Sessions.Serialization;

internal static class EffectiveSettingsReloadPublicationTests
{
    private sealed record Raw(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);
    private static readonly object evidenceGate = new();
    private static readonly List<Raw> evidence = [];
    internal static (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] CapturedOriginals
    { get { lock (evidenceGate) return evidence.Select(x => (x.Phase, x.Original, x.Aggregate, x.Direct)).ToArray(); } }
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("effective settings actual reload stages invisibly and commits before held session start", () => Run("published", false, false, false, false)),
        ("effective settings original registry commit refusal never publishes candidate snapshot", () => Run("commit-refused", true, false, false, false)),
        ("effective settings stage failure leaves acknowledged attachment snapshot unchanged", () => Run("stage-refused", false, true, false, false)),
        ("effective settings acknowledged publication survives subsequent start failure", () => Run("start-refused", false, false, true, false)),
        ("effective settings omitted typed payload inherits capture and rejects equal-generation foreign attachment", () => Run("omitted", false, false, false, true)),
        ("effective settings actual tree selection and session replacement preserve committed view snapshot", () => Run("navigation", false, false, false, false, true))
    ];

    private static async Task Run(string phase, bool refuseCommit, bool refuseStage, bool refuseStart, bool omit, bool navigate = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-typed-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var allowedFailures = new List<Exception>(); var errors = new List<Exception>();
        OfflineSessionProfile? profile = null;
        var buildEntered = Gate(); var buildRelease = Gate(); var startEntered = Gate(); var startRelease = Gate();
        Task<NativeHostReloadReceipt>? reload = null;
        var failure = new IOException("supplied " + phase);
        var commits = 0; var disposed = 0; AgentSessionAttachment? candidate = null;
        try
        {
            var initial = await Own("startup-merge", StartupSettings.LoadAsync(new(Overrides: JsonData.Parse("{\"theme\":\"old\",\"nested\":{\"actual\":1}}"))));
            var next = await Own("candidate-merge", StartupSettings.LoadAsync(new(Overrides: JsonData.Parse("{\"theme\":\"new\",\"nested\":{\"actual\":2}}"))));
            var path = Path.Combine(root, "session.jsonl");
            profile = await Own("actual-profile-create", OfflineSessionProfile.CreateAsync(root, path, null, [], [], [], default));
            var active = profile;
            active.ConfigureEffectiveSettings(initial);
            Check(ReferenceEquals(active.CaptureStartupEffectiveSettings(), initial));
            Refused(() => active.ConfigureEffectiveSettings(next));
            var id = 0; var lifecycle = active.CreateLifecycle(() => 1, () => "settings-" + ++id);
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
                { type = "session", version = 3, id = phase, timestamp = "2026-10-07T00:00:00.000Z", cwd = root }));
            var session = await Own("actual-session-create", lifecycle.CreateAsync(path, header, active.SelectedModel));
            await Join("actual-owner-attach", active.AttachOwnerAsync(session, lifecycle: lifecycle));
            var owner = active.Sessions!; var previous = owner.Current;
            Check(ReferenceEquals(active.CaptureEffectiveSettings(previous), initial));
            var foreign = new AgentSessionAttachment(previous.Session, previous.Generation, previous.LifetimeToken);
            Refused(() => active.CaptureEffectiveSettings(foreign));
            Refused(() => active.CaptureStartupEffectiveSettings());
            active.ConfigureReload(new(new(new object(), initial), [], false, new([], []), new()
            {
                StageSettingsAsync = (_, _) => new(Own("actual-settings-stage", refuseStage
                    ? Task.FromException<NativeHostReloadPayload>(failure)
                    : Task.FromResult(new NativeHostReloadPayload(new object(), omit ? null : next)))),
                SyncQueueModesAsync = (_, _) => ValueTask.CompletedTask,
                ResetApiProvidersAsync = (_, _) => ValueTask.CompletedTask,
                ReloadResourcesAsync = (_, _) => ValueTask.CompletedTask,
                DescribeRuntimeAsync = (_, _) => ValueTask.FromResult(new HostReloadRuntime([], [])),
                BuildRuntimeAsync = (attachment, _, _) => new(Own("actual-build-runtime", Build(attachment))),
                SessionShutdownAsync = (_, _, _) => ValueTask.CompletedTask,
                CleanupPreparationAsync = _ => ValueTask.CompletedTask,
                BeforeSessionStartAsync = (_, _) => ValueTask.CompletedTask,
                SessionStartAsync = (_, _, _) => new(Join("actual-session-start", Start())),
                ReportUnhandledMcpServersAsync = (_, _) => ValueTask.CompletedTask,
                ExtendResourcesAsync = (_, _, _) => ValueTask.CompletedTask
            }));
            async Task<PreparedNativeHostReload> Build(AgentSessionAttachment attachment)
                {
                    candidate = attachment; buildEntered.TrySetResult();
                    await Join("held-build-original", buildRelease.Task);
                    var runtime = new SessionRuntimeLease(active.Registry, new Resource(() => disposed++));
                    return new PreparedNativeHostReload(runtime, () =>
                    {
                        // This callback is the genuine engine transaction's registry commit.
                        Check(ReferenceEquals(owner.Current, previous));
                        Check(ReferenceEquals(active.CaptureEffectiveSettings(previous), initial));
                        Refused(() => active.CaptureEffectiveSettings(attachment));
                        commits++;
                        if (refuseCommit) throw failure;
                    });
                }
            async Task Start()
                {
                    Check(ReferenceEquals(owner.Current, candidate));
                    Check(ReferenceEquals(active.CaptureEffectiveSettings(owner.Current), omit ? initial : next));
                    startEntered.TrySetResult(); await Join("held-start-original", startRelease.Task);
                    if (refuseStart) throw failure;
                }
            reload = active.ReloadAsync(previous);
            Check(ReferenceEquals(reload, active.ReloadAsync(previous)));
            if (refuseStage)
            {
                var stagedReceipt = await Own("actual-reload", reload);
                Check(stagedReceipt.Workflow.Authority == ResourceReloadAuthority.Old &&
                    stagedReceipt.Workflow.Failures.Any(x => Contains(x.Cause, failure)));
                Check(commits == 0 && candidate is null && ReferenceEquals(owner.Current, previous));
                Check(ReferenceEquals(active.CaptureEffectiveSettings(previous), initial));
            }
            else
            {
                await Join("build-entered", buildEntered.Task);
                Check(ReferenceEquals(owner.Current, previous) && ReferenceEquals(active.CaptureEffectiveSettings(previous), initial));
                Check(candidate is not null && !reload.IsCompleted);
                Refused(() => active.CaptureEffectiveSettings(candidate!));
                buildRelease.TrySetResult();
                if (refuseCommit)
                {
                    var caught = await Expected("actual-reload", reload, failure); allowedFailures.Add(caught);
                    Check(commits == 1 && ReferenceEquals(owner.Current, previous));
                    Refused(() => active.CaptureEffectiveSettings(previous));
                    Refused(() => active.CaptureEffectiveSettings(candidate!));
                }
                else
                {
                    await Join("start-entered", startEntered.Task);
                    Check(commits == 1 && !reload.IsCompleted && ReferenceEquals(owner.Current, candidate));
                    Check(ReferenceEquals(active.CaptureEffectiveSettings(owner.Current), omit ? initial : next));
                    Refused(() => active.CaptureEffectiveSettings(previous));
                    startRelease.TrySetResult();
                    var result = await Own("actual-reload", reload);
                    Check(result.Workflow.Authority == ResourceReloadAuthority.New);
                    if (refuseStart)
                    {
                        Check(result.Workflow.Failures.Any(x => Contains(x.Cause, failure)));
                        Check(ReferenceEquals(active.CaptureEffectiveSettings(owner.Current), next));
                    }
                    else Check(result.Workflow.Failures.IsEmpty);
                    if (navigate)
                    {
                        var from = owner.Current;
                        var appended = await Own("actual-tree-marker-append", owner.AppendExtensionEntryAsync(from,
                            new("settings-fixture", "settings-marker", 1, JsonData.EmptyObject)));
                        Check(appended.Append.CheckpointAcknowledged);
                        var tree = owner.CaptureTree(from); Check(tree.LeafId is not null);
                        var selected = await Own("actual-tree-selection", owner.NavigateTreeAsync(from, new(null, tree.Revision)));
                        Check(selected.Disposition == SessionTreeNavigationDisposition.Selected && selected.LeafId is null &&
                            ReferenceEquals(selected.View.Attachment, from) && ReferenceEquals(owner.Current, from));
                        Check(ReferenceEquals(active.CaptureEffectiveSettings(from), next));
                        var afterRetiredBeforeRead = false;
                        owner.AfterReplacement = replacement =>
                        {
                            Check(ReferenceEquals(replacement.Previous, from) && disposed == 1);
                            Refused(() => active.CaptureEffectiveSettings(from));
                            Check(ReferenceEquals(active.CaptureEffectiveSettings(replacement.Current), next));
                            afterRetiredBeforeRead = true; return ValueTask.CompletedTask;
                        };
                        var navigation = await Own("actual-navigation-new-session", owner.CreateAsync(from, new(AgentSessionCreationKind.New)));
                        Check(navigation is not null && ReferenceEquals(owner.Current, navigation.Current) && afterRetiredBeforeRead);
                        Check(!ReferenceEquals(owner.Current, from) && owner.Current.Generation > from.Generation);
                        Check(ReferenceEquals(active.CaptureEffectiveSettings(owner.Current), next));
                        Refused(() => active.CaptureEffectiveSettings(from));
                    }
                }
            }
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            buildRelease.TrySetResult(); startRelease.TrySetResult();
            if (reload is not null)
            {
                try { await Join("reload-final-join", reload); }
                catch (Exception error) { if (!allowedFailures.Any(x => SameLeaves(error, x))) errors.Add(error); }
            }
            if (profile is not null)
            {
                try { await Join("actual-profile-close", profile.DisposeAsync().AsTask()); }
                catch (Exception error) { if (!allowedFailures.Any(x => SameLeaves(error, x))) errors.Add(error); }
            }
            if (!refuseStage && disposed != 1) errors.Add(new IOException("Actual candidate resource was not closed exactly once."));
            try
            {
                if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                    !Path.GetFileName(root).StartsWith("pisharp-typed-settings-", StringComparison.Ordinal))
                    throw new IOException("Fixture workspace boundary changed.");
                Directory.Delete(root, true);
            }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new SettingsFixtureFailure(CapturedOriginals, errors.ToArray());

        async Task Join(string role, Task task)
        {
            Exception? direct = null;
            try { await task.ConfigureAwait(false); }
            catch (Exception error) { direct = error; Record(phase + ":" + role, task, direct); throw CachedAggregate(task) ?? error; }
            finally { if (direct is null) Record(phase + ":" + role, task, null); }
        }
        async Task<T> Own<T>(string role, Task<T> task)
        {
            Exception? direct = null;
            try { return await task.ConfigureAwait(false); }
            catch (Exception error) { direct = error; Record(phase + ":" + role, task, direct); throw CachedAggregate(task) ?? error; }
            finally { if (direct is null) Record(phase + ":" + role, task, null); }
        }
        async Task<Exception> Expected(string role, Task task, Exception leaf)
        {
            try { await Join(role, task); }
            catch (Exception error) { Check(task.IsFaulted && !task.IsCanceled && Contains(error, leaf)); return error; }
            throw new IOException("Actual reload unexpectedly succeeded.");
        }
    }

    private static void Record(string phase, Task original, Exception? direct)
    {
        lock (evidenceGate)
        {
            var cached = evidence.FirstOrDefault(x => ReferenceEquals(x.Original, original));
            evidence.Add(new(phase, original, cached is null ? original.Exception : cached.Aggregate,
                cached is null ? direct : cached.Direct));
        }
    }
    private static AggregateException? CachedAggregate(Task original)
    { lock (evidenceGate) return evidence.First(x => ReferenceEquals(x.Original, original)).Aggregate; }
    private sealed class SettingsFixtureFailure((string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] originals,
        Exception[] faults) : IOException("Effective settings fixture failed.", new AggregateException(faults))
    {
        internal (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] Originals { get; } = originals;
        internal Exception[] Faults { get; } = faults;
    }
    private sealed class Resource(Action closed) : IAsyncDisposable
    { public ValueTask DisposeAsync() { closed(); return ValueTask.CompletedTask; } }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Refused(Action action)
    { try { action(); } catch (InvalidOperationException) { return; } throw new IOException("Expected exact attachment/settings refusal."); }
    private static void Check(bool value) { if (!value) throw new IOException("Actual effective settings publication contract failed."); }
    private static bool Contains(Exception value, Exception expected) => ReferenceEquals(value, expected) ||
        (value is AggregateException all ? all.InnerExceptions.Any(x => Contains(x, expected)) : value.InnerException is { } inner && Contains(inner, expected));
    private static bool SameLeaves(Exception value, Exception expected)
    {
        var allowed = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        Add(expected, allowed);
        return CheckTree(value);
        bool CheckTree(Exception x) => allowed.Contains(x) || (x is AggregateException a ?
            a.InnerExceptions.Count != 0 && a.InnerExceptions.All(CheckTree) : x.InnerException is { } inner && CheckTree(inner));
        static void Add(Exception x, HashSet<Exception> set)
        { if (!set.Add(x)) return; if (x is AggregateException a) foreach (var child in a.InnerExceptions) Add(child, set); else if (x.InnerException is { } inner) Add(inner, set); }
    }
}