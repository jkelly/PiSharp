using System.Text.Json;
using PiSharp.Cli.Prompts;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Reloading;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;
using PiSharp.Extensions.Abstractions.Reloading;
using PiSharp.Extensions.Runtime.Reloading;
using PiSharp.Sessions.Serialization;

internal static class OriginalSystemPromptReloadPublicationTests
{
    private sealed record Raw(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);
    private static readonly object evidenceGate = new();
    private static readonly List<Raw> evidence = [];
    internal static (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] CapturedOriginals
    { get { lock (evidenceGate) return evidence.Select(x => (x.Phase, x.Original, x.Aggregate, x.Direct)).ToArray(); } }
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("original system prompt actual reload stages invisibly and commits before held session start", () => Run("published", false, false, false, false)),
        ("original system prompt original registry commit refusal never publishes candidate snapshot", () => Run("commit-refused", true, false, false, false)),
        ("original system prompt stage failure leaves acknowledged attachment snapshot unchanged", () => Run("stage-refused", false, true, false, false)),
        ("original system prompt acknowledged publication survives subsequent start failure", () => Run("start-refused", false, false, true, false)),
        ("original system prompt omitted typed payload inherits capture and rejects equal-generation foreign attachment", () => Run("omitted", false, false, false, true)),
        ("original system prompt actual tree selection and session replacement preserve committed view snapshot", () => Run("navigation", false, false, false, false, true))
    ];

    private static async Task Run(string phase, bool refuseCommit, bool refuseStage, bool refuseStart, bool omit, bool navigate = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-original-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var allowedFailures = new List<Exception>(); var errors = new List<Exception>();
        OfflineSessionProfile? profile = null;
        var buildEntered = Gate(); var buildRelease = Gate(); var startEntered = Gate(); var startRelease = Gate();
        Task<NativeHostReloadReceipt>? reload = null;
        var failure = new IOException("supplied " + phase);
        Check(OnlyInjected(new AggregateException(failure), failure));
        Check(!OnlyInjected(new AggregateException(failure, new IOException("foreign sibling")), failure));
        Check(!OnlyInjected(new IOException("unknown wrapper", failure), failure));
        Check(!OnlyInjected(new AggregateException(), failure));
        Check(!SameLeaves(new IOException("unknown cleanup wrapper", failure), failure));
        Check(!SameLeaves(new AggregateException(), failure));
        var commits = 0; var disposed = 0; AgentSessionAttachment? candidate = null;
        try
        {
            var initial = await Own("startup-merge", StartupSettings.LoadAsync(new(Overrides: JsonData.Parse("{\"theme\":\"old\",\"nested\":{\"actual\":1}}"))));
            var next = await Own("candidate-merge", StartupSettings.LoadAsync(new(Overrides: JsonData.Parse("{\"theme\":\"new\",\"nested\":{\"actual\":2}}"))));
            var path = Path.Combine(root, "session.jsonl");
            profile = await Own("actual-profile-create", OfflineSessionProfile.CreateAsync(root, path, null, [], [], [], default, originalSystemPrompt: new() { CustomPrompt = "old admitted prompt" }));
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
            Check(active.CaptureOriginalSystemPrompt(previous).Input.CustomPrompt == "old admitted prompt");
            var foreign = new AgentSessionAttachment(previous.Session, previous.Generation, previous.LifetimeToken);
            Refused(() => active.CaptureEffectiveSettings(foreign));
            Refused(() => active.CaptureOriginalSystemPrompt(foreign));
            Refused(() => active.CaptureStartupEffectiveSettings());
            active.ConfigureReload(new(new(new object(), initial), [], false, new([], []), new()
            {
                StageSettingsAsync = (_, _) => new(Own("actual-settings-stage", refuseStage
                    ? Task.FromException<NativeHostReloadPayload>(failure)
                    : Task.FromResult(StagePayload()))),
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
            NativeHostReloadPayload StagePayload()
            {
                var payload = new NativeHostReloadPayload(new object(), omit ? null : next);
                if (!omit) active.AdmitOriginalSystemPromptReload(payload, new() { CustomPrompt = "new admitted prompt" });
                return payload;
            }
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
            Check(active.CaptureOriginalSystemPrompt(previous).Input.CustomPrompt == "old admitted prompt");
                        Refused(() => active.CaptureEffectiveSettings(attachment));
                        Refused(() => active.CaptureOriginalSystemPrompt(attachment));
                        commits++;
                        if (refuseCommit) throw failure;
                    });
                }
            async Task Start()
                {
                    Check(ReferenceEquals(owner.Current, candidate));
                    Check(ReferenceEquals(active.CaptureEffectiveSettings(owner.Current), omit ? initial : next));
                    Check(active.CaptureOriginalSystemPrompt(owner.Current).Input.CustomPrompt == (omit ? "old admitted prompt" : "new admitted prompt"));
                    startEntered.TrySetResult(); await Join("held-start-original", startRelease.Task);
                    if (refuseStart) throw failure;
                }
            reload = active.ReloadAsync(previous);
            Check(ReferenceEquals(reload, active.ReloadAsync(previous)));
            if (refuseStage)
            {
                var stagedReceipt = await Own("actual-reload", reload);
                Check(stagedReceipt.Workflow.Authority == ResourceReloadAuthority.Old &&
                    !stagedReceipt.Workflow.Failures.IsEmpty && stagedReceipt.Workflow.Failures.All(x => OnlyInjected(x.Cause, failure)));
                Check(commits == 0 && candidate is null && ReferenceEquals(owner.Current, previous));
                Check(ReferenceEquals(active.CaptureEffectiveSettings(previous), initial));
            Check(active.CaptureOriginalSystemPrompt(previous).Input.CustomPrompt == "old admitted prompt");
            }
            else
            {
                await Join("build-entered", buildEntered.Task);
                Check(ReferenceEquals(owner.Current, previous) && ReferenceEquals(active.CaptureEffectiveSettings(previous), initial));
                Check(candidate is not null && !reload.IsCompleted);
                Refused(() => active.CaptureEffectiveSettings(candidate!));
                Refused(() => active.CaptureOriginalSystemPrompt(candidate!));
                buildRelease.TrySetResult();
                if (refuseCommit)
                {
                    var caught = await Expected("actual-reload", reload, failure); allowedFailures.Add(caught);
                    Check(commits == 1 && ReferenceEquals(owner.Current, previous));
                    Refused(() => active.CaptureEffectiveSettings(previous));
                    Refused(() => active.CaptureOriginalSystemPrompt(previous));
                    Refused(() => active.CaptureEffectiveSettings(candidate!));
                Refused(() => active.CaptureOriginalSystemPrompt(candidate!));
                }
                else
                {
                    await Join("start-entered", startEntered.Task);
                    Check(commits == 1 && !reload.IsCompleted && ReferenceEquals(owner.Current, candidate));
                    Check(ReferenceEquals(active.CaptureEffectiveSettings(owner.Current), omit ? initial : next));
                    Check(active.CaptureOriginalSystemPrompt(owner.Current).Input.CustomPrompt == (omit ? "old admitted prompt" : "new admitted prompt"));
                    Refused(() => active.CaptureEffectiveSettings(previous));
                    Refused(() => active.CaptureOriginalSystemPrompt(previous));
                    startRelease.TrySetResult();
                    var result = await Own("actual-reload", reload);
                    Check(result.Workflow.Authority == ResourceReloadAuthority.New);
                    if (refuseStart)
                    {
                        Check(!result.Workflow.Failures.IsEmpty && result.Workflow.Failures.All(x => OnlyInjected(x.Cause, failure)));
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
                            Refused(() => active.CaptureOriginalSystemPrompt(from));
                            Check(ReferenceEquals(active.CaptureEffectiveSettings(replacement.Current), next));
                            Check(active.CaptureOriginalSystemPrompt(replacement.Current).Input.CustomPrompt == "new admitted prompt");
                            afterRetiredBeforeRead = true; return ValueTask.CompletedTask;
                        };
                        var navigation = await Own("actual-navigation-new-session", owner.CreateAsync(from, new(AgentSessionCreationKind.New)));
                        Check(navigation is not null && ReferenceEquals(owner.Current, navigation.Current) && afterRetiredBeforeRead);
                        Check(!ReferenceEquals(owner.Current, from) && owner.Current.Generation > from.Generation);
                        Check(ReferenceEquals(active.CaptureEffectiveSettings(owner.Current), next));
                        Refused(() => active.CaptureEffectiveSettings(from));
                            Refused(() => active.CaptureOriginalSystemPrompt(from));
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
                    !Path.GetFileName(root).StartsWith("pisharp-original-prompt-", StringComparison.Ordinal))
                    throw new IOException("Fixture workspace boundary changed.");
                Directory.Delete(root, true);
            }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new PromptFixtureFailure(CapturedOriginals, errors.ToArray());

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
            catch (Exception error) { Check(task.IsFaulted && !task.IsCanceled && OnlyInjected(error, leaf)); return error; }
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
    private sealed class PromptFixtureFailure((string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] originals,
        Exception[] faults) : IOException("Original system prompt fixture failed.", new AggregateException(faults))
    {
        internal (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] Originals { get; } = originals;
        internal Exception[] Faults { get; } = faults;
    }
    private sealed class Resource(Action closed) : IAsyncDisposable
    { public ValueTask DisposeAsync() { closed(); return ValueTask.CompletedTask; } }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Refused(Action action)
    { try { action(); } catch (InvalidOperationException) { return; } throw new IOException("Expected exact attachment/settings refusal."); }
    private static void Check(bool value) { if (!value) throw new IOException("Actual original system prompt publication contract failed."); }
    private static bool OnlyInjected(Exception root, Exception expected)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var edges = 0; var found = false;
        bool Visit(Exception value)
        {
            if (!seen.Add(value)) return true;
            if (seen.Count > 1024) return false;
            if (ReferenceEquals(value, expected)) { found = true; return true; }
            if (value is AggregateException all)
            { edges += all.InnerExceptions.Count; return edges <= 4096 && all.InnerExceptions.Count != 0 && all.InnerExceptions.All(Visit); }
            if (value is HostReloadCallbackFailure host)
                return !host.OriginalTaskIsCanceled && Visit(host.AwaitedCause) &&
                    (host.OriginalTaskException is null || Visit(host.OriginalTaskException)) && host.InnerException is { } hostInner && Visit(hostInner);
            if (value is AgentSessionReloadCallbackException session)
                return !session.OriginalTaskIsCanceled && Visit(session.AwaitedCause) &&
                    (session.OriginalTaskException is null || Visit(session.OriginalTaskException)) && session.InnerException is { } sessionInner && Visit(sessionInner);
            return false; // Unknown wrappers and all additional leaves remain failures.
        }
        return Visit(root) && found;
    }
    private static bool SameLeaves(Exception value, Exception acknowledged)
    {
        var allowed = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var pending = new Queue<Exception>(); pending.Enqueue(acknowledged);
        var edges = 0;
        while (pending.TryDequeue(out var next))
        {
            if (!allowed.Add(next)) continue; if (allowed.Count > 1024) return false;
            if (next is AggregateException all) { edges += all.InnerExceptions.Count; foreach (var child in all.InnerExceptions) pending.Enqueue(child); }
            else if (next.InnerException is { } inner) { edges++; pending.Enqueue(inner); }
            if (edges > 4096) return false;
        }
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); edges = 0;
        bool CheckTree(Exception next)
        {
            if (!seen.Add(next)) return true; if (seen.Count > 1024) return false;
            if (allowed.Contains(next)) return true;
            if (next is AggregateException all)
            { edges += all.InnerExceptions.Count; return edges <= 4096 && all.InnerExceptions.Count != 0 && all.InnerExceptions.All(CheckTree); }
            // This exact typed native close envelope is source-proven; its entire
            // underlying graph must still consist of acknowledged object refs.
            if (next is NativeExtensionException { Failure: NativeExtensionFailure.CleanupFailed, InnerException: { } inner })
            { edges++; return edges <= 4096 && CheckTree(inner); }
            return false;
        }
        return CheckTree(value);
    }
}