using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Reloading;
using PiSharp.Extensions.Abstractions.Reloading;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

// Authored source controls; no shared runner edits or execution credit.
internal static class OriginalPromptSectionAcknowledgmentTests
{
    private sealed record Raw(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);
    private static readonly object gate = new(); private static readonly List<Raw> rows = [];
    internal static (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] CapturedOriginals
    { get { lock (gate) return rows.Select(row => (row.Phase, row.Original, row.Aggregate, row.Direct)).ToArray(); } }
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("original base sections publish at the same held storage acknowledgment and unchanged input is write free", HeldAcknowledgment),
        ("original changed prompt with no pending tool selection publishes before the genuine request", RequestBoundary),
        ("original stale prompt revision and cancellation refuse before append", BeforeAppendRefusal),
        ("original append and physical disposal faults retain separate cached originals", FaultOriginals),
        ("original actual typed profile reload inherits selected tools and commits changed base sections at its owning boundary", ProfileReload)
    ];
    private static readonly ModelDescriptor Model = new("prompt-ack", "prompt-ack-api", "prompt-ack-provider");
    private static void Check(bool value) { if (!value) throw new IOException("Prompt section acknowledgment control failed."); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Admission
    {
        internal object Revision = new(); internal string Text = "old base"; internal bool Stale;
        internal SessionPromptSectionPreparation Prepare(SessionPromptSectionRequest request)
        {
            var captured = Revision;
            return new(captured, [KeyValuePair.Create("preamble", Text)], () =>
            { if (Stale || !ReferenceEquals(captured, Revision)) throw new InvalidOperationException("Stale supplied prompt source."); });
        }
    }
    private static async Task HeldAcknowledgment()
    {
        var source = new Admission(); var storage = new Storage(); PersistentAgentSession? session = null;
        var failures = new List<Exception>(); Task? configure = null;
        using var canceledAfterWrite = new CancellationTokenSource();
        try
        {
            session = await Own("held:create", Open(source, storage, new Transport()));
            await Own("held:initial-base", session.SetActiveToolsAsync([]));
            var old = session.Snapshot; var length = old.Log.Entries.Length;
            source.Text = "new base"; source.Revision = new(); storage.Hold = true;
            configure = session.SetActiveToolsAsync(["ack-tool"], canceledAfterWrite.Token);
            await CheckpointGate(storage.Entered.Task, configure);
            Check(!configure.IsCompleted && session.Snapshot.Agent.Tools.IsEmpty && ReferenceEquals(session.Snapshot.Context, old.Context) &&
                Base(session.Snapshot) == "old base" && session.Snapshot.Log.Entries.Length == length);
            canceledAfterWrite.Cancel(); // Physical write already completed; the held original checkpoint must retain its ACK.
            storage.Release.TrySetResult(); await Own("held:actual-configure", configure);
            Check(Base(session.Snapshot) == "new base" && session.Snapshot.Log.Entries.Length == length + 1 &&
                session.Snapshot.Agent.Tools.Select(tool => tool.Name).SequenceEqual(new[] { "ack-tool" }) &&
                new SessionSystemReplay().Replay(session.Snapshot.Context.Messages).Tools.Single().Value.GetProperty("name").GetString() == "ack-tool");
            await Own("held:unchanged-configure", session.SetActiveToolsAsync(["ack-tool"]));
            Check(session.Snapshot.Log.Entries.Length == length + 1);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            storage.Release.TrySetResult();
            if (configure is not null) try { await Own("held:configure-final-join", configure); } catch (Exception error) { failures.Add(error); }
            if (session is not null) try { await Own("held:close", session.DisposeAsync().AsTask()); } catch (Exception error) { failures.Add(error); }
        }
        Finish(failures);
    }
    private static async Task RequestBoundary()
    {
        var source = new Admission(); var storage = new Storage(); var transport = new Transport();
        PersistentAgentSession? session = null; var failures = new List<Exception>();
        try
        {
            session = await Own("request:create", Open(source, storage, transport));
            await Own("request:initial-base", session.SetActiveToolsAsync([]));
            source.Text = "reloaded base without pending selection"; source.Revision = new();
            transport.Observe = request =>
            { var replay = new SessionSystemReplay().Replay(request.Messages); Check(replay.CurrentMessage!.WireBody.Value.GetProperty("sections").GetProperty("preamble").GetString() == source.Text); };
            await Own("request:actual-prompt", session.PromptAsync([new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"go\",\"timestamp\":1000}"))]));
            Check(transport.Calls == 1 && Base(session.Snapshot) == source.Text);
        }
        catch (Exception error) { failures.Add(error); }
        finally { if (session is not null) try { await Own("request:close", session.DisposeAsync().AsTask()); } catch (Exception error) { failures.Add(error); } }
        Finish(failures);
    }
    private static async Task CheckpointGate(Task entered, Task actualConfigure)
    {
        using var diagnosticCancellation = new CancellationTokenSource();
        var deadline = Task.Delay(TimeSpan.FromSeconds(10), diagnosticCancellation.Token);
        var failures = new List<Exception>();
        try
        {
            var race = Task.WhenAny(entered, actualConfigure, deadline);
            var first = await Own("held:checkpoint-diagnostic-race", race);
            if (ReferenceEquals(first, actualConfigure))
            {
                await Own("held:configure-settled-before-gate", actualConfigure);
                if (!entered.IsCompleted) throw new IOException("Actual Configure settled without reaching its checkpoint.");
            }
            if (ReferenceEquals(first, deadline))
            {
                await Own("held:checkpoint-deadline-expired", deadline);
                throw new TimeoutException("Actual Configure did not reach its held checkpoint within the diagnostic bound.");
            }
            await Own("held:checkpoint-entered", entered);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try { diagnosticCancellation.Cancel(); } catch (Exception error) { failures.Add(error); }
            try { await Own("held:checkpoint-deadline-final-join", deadline); }
            catch (OperationCanceledException error) when (deadline.IsCanceled &&
                diagnosticCancellation.IsCancellationRequested && error.CancellationToken == diagnosticCancellation.Token) { }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new FixtureFailure(CapturedOriginals, failures.ToArray());
    }
    private static async Task BeforeAppendRefusal()
    {
        var source = new Admission(); var storage = new Storage(); PersistentAgentSession? session = null; var failures = new List<Exception>();
        try
        {
            session = await Own("refusal:create", Open(source, storage, new Transport()));
            await Own("refusal:initial-base", session.SetActiveToolsAsync([])); var writes = storage.Writes;
            source.Text = "unacknowledged"; source.Revision = new(); source.Stale = true;
            var stale = session.SetActiveToolsAsync([]); Exception? direct = null;
            try { await Own("refusal:stale-original", stale); } catch (InvalidOperationException error) { direct = error; }
            Check(direct is not null && stale.IsFaulted && storage.Writes == writes && Base(session.Snapshot) == "old base");
            source.Stale = false; using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await Own("refusal:canceled-original-if-acquired", session.SetActiveToolsAsync([], canceled.Token)); throw new IOException("Canceled mutation was admitted."); }
            catch (OperationCanceledException error) { Check(error.CancellationToken == canceled.Token); }
            Check(storage.Writes == writes && Base(session.Snapshot) == "old base");
        }
        catch (Exception error) { failures.Add(error); }
        finally { if (session is not null) try { await Own("refusal:close", session.DisposeAsync().AsTask()); } catch (Exception error) { failures.Add(error); } }
        Finish(failures);
    }
    private static async Task FaultOriginals()
    {
        var source = new Admission(); var storage = new Storage(); PersistentAgentSession? session = null; var failures = new List<Exception>();
        var appendLeaf = new IOException("exact supplied physical checkpoint fault"); var disposeLeaf = new IOException("exact supplied physical disposal fault");
        try
        {
            session = await Own("fault:create", Open(source, storage, new Transport()));
            await Own("fault:initial-base", session.SetActiveToolsAsync([])); source.Text = "rejected checkpoint"; source.Revision = new();
            storage.CheckpointFault = appendLeaf; storage.DisposeFault = disposeLeaf;
            var configure = session.SetActiveToolsAsync([]); Exception? error = null;
            try { await Own("fault:actual-configure", configure); } catch (PersistentAgentSessionException caught) { error = caught; }
            Check(error is not null && configure.IsFaulted && Base(session.Snapshot) == "old base");
            Exception? closed = null;
            try { await Own("fault:actual-close", session.DisposeAsync().AsTask()); } catch (Exception caught) { closed = caught; }
            Check(closed is PersistentAgentSessionException { Fault.Failure: PersistentAgentSessionFailure.CleanupFailed } && OnlyCleanup(closed));
            Check(!OnlyCleanup(new AggregateException(closed!, new IOException("foreign cleanup sibling"))) &&
                !OnlyCleanup(new IOException("unknown cleanup wrapper", closed)) && !OnlyCleanup(new AggregateException()));
            session = null;
            var checkpoint = CapturedOriginals.Single(row => row.Phase == "physical:checkpoint-fault");
            var disposal = CapturedOriginals.Single(row => row.Phase == "physical:dispose-fault");
            Check(checkpoint.Original.IsFaulted && ReferenceEquals(checkpoint.Direct, appendLeaf) &&
                checkpoint.Aggregate is { InnerExceptions.Count: 1 } && ReferenceEquals(checkpoint.Aggregate.InnerExceptions[0], appendLeaf));
            Check(disposal.Original.IsFaulted && ReferenceEquals(disposal.Direct, disposeLeaf) &&
                disposal.Aggregate is { InnerExceptions.Count: 1 } && ReferenceEquals(disposal.Aggregate.InnerExceptions[0], disposeLeaf));
        }
        catch (Exception error) { failures.Add(error); }
        finally { if (session is not null) try { await Own("fault:close-final", session.DisposeAsync().AsTask()); } catch (Exception error) { failures.Add(error); } }
        Finish(failures);
    }
    private static string? Base(PersistentAgentSessionSnapshot snapshot) => new SessionSystemReplay().Replay(snapshot.Context.Messages)
        .CurrentMessage?.WireBody.Value.GetProperty("sections").GetProperty("preamble").GetString();
    private static async Task ProfileReload()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-prompt-ack-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); OfflineSessionProfile? profile = null; var failures = new List<Exception>();
        var commits = 0; var disposed = 0;
        try
        {
            profile = await Own("profile-ack:create", OfflineSessionProfile.CreateAsync(root, Path.Combine(root, "session.jsonl"), null, [], [], [], default,
                originalSystemPrompt: new() { CustomPrompt = "profile old base", ForceSystemPrompt = "never persist forced request" }));
            var active = profile; var id = 0; var lifecycle = active.CreateLifecycle(() => 1000, () => "prompt-profile-" + ++id);
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "prompt-profile", timestamp = "2026-10-07T00:00:00.000Z", cwd = root }));
            var session = await Own("profile-ack:session-create", lifecycle.CreateAsync(Path.Combine(root, "session.jsonl"), header, active.SelectedModel));
            await Own("profile-ack:attach", active.AttachOwnerAsync(session, lifecycle: lifecycle));
            await Own("profile-ack:initial-boundary", session.SetActiveToolsAsync([]));
            Check(Base(session.Snapshot) == "profile old base");
            var previous = active.Sessions!.Current;
            active.ConfigureReload(new(new(new object(), null), [], false, new([], []), new()
            {
                StageSettingsAsync = (_, _) =>
                {
                    var payload = new NativeHostReloadPayload(new object(), null);
                    active.AdmitOriginalSystemPromptReload(payload, new() { CustomPrompt = "profile reloaded base", ForceSystemPrompt = "still request only" });
                    return ValueTask.FromResult(payload);
                },
                SyncQueueModesAsync = (_, _) => ValueTask.CompletedTask,
                ResetApiProvidersAsync = (_, _) => ValueTask.CompletedTask,
                ReloadResourcesAsync = (_, _) => ValueTask.CompletedTask,
                DescribeRuntimeAsync = (_, _) => ValueTask.FromResult(new HostReloadRuntime([], [])),
                BuildRuntimeAsync = (_, _, _) => ValueTask.FromResult(new PreparedNativeHostReload(
                    new SessionRuntimeLease(active.Registry, new Resource(() => disposed++)), () => commits++)),
                SessionShutdownAsync = (_, _, _) => ValueTask.CompletedTask,
                CleanupPreparationAsync = _ => ValueTask.CompletedTask,
                BeforeSessionStartAsync = (_, _) => ValueTask.CompletedTask,
                SessionStartAsync = (_, _, _) => ValueTask.CompletedTask,
                ReportUnhandledMcpServersAsync = (_, _) => ValueTask.CompletedTask,
                ExtendResourcesAsync = (_, _, _) => ValueTask.CompletedTask
            }));
            await Own("profile-ack:actual-typed-reload", active.ReloadAsync(previous));
            var current = active.Sessions.Current;
            Check(current.Generation > previous.Generation && commits == 1 &&
                active.CaptureOriginalSystemPrompt(current).Input.CustomPrompt == "profile reloaded base");
            var selected = current.Session.Snapshot.Agent.Tools.Select(tool => tool.Name).ToImmutableArray();
            await Own("profile-ack:unchanged-selection-commit", current.Session.SetActiveToolsAsync(selected));
            Check(Base(current.Session.Snapshot) == "profile reloaded base" &&
                !new SessionSystemReplay().Replay(current.Session.Snapshot.Context.Messages).Prompt.Contains("still request only", StringComparison.Ordinal));
            var count = current.Session.Snapshot.Log.Entries.Length;
            await Own("profile-ack:no-duplicate-append", current.Session.SetActiveToolsAsync(selected));
            Check(current.Session.Snapshot.Log.Entries.Length == count);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            if (profile is not null) try { await Own("profile-ack:close", profile.DisposeAsync().AsTask()); } catch (Exception error) { failures.Add(error); }
            try
            {
                Check(Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                    Path.GetFileName(root).StartsWith("pisharp-prompt-ack-reload-", StringComparison.Ordinal));
                Directory.Delete(root, true);
            }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count == 0 && disposed != 1) failures.Add(new IOException("Actual generation resource was not closed exactly once."));
        Finish(failures);
    }
    private sealed class Resource(Action dispose) : IAsyncDisposable
    { public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; } }
    private static Task<PersistentAgentSession> Open(Admission source, Storage storage, Transport transport)
    {
        var cwd = Path.GetFullPath(Path.GetTempPath()); var id = 0;
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "prompt-ack", timestamp = "2026-10-07T00:00:00.000Z", cwd }));
        var declaration = JsonData.Parse("{\"name\":\"ack-tool\",\"description\":\"Admitted inert control\",\"parameters\":{\"type\":\"object\"}}");
        var registry = new SessionRuntimeRegistry([new(Model, transport)], [new(declaration, new Adapter())], new Deny(), new() { PreparePromptSections = source.Prepare });
        return PersistentAgentSession.CreateAsync(Path.Combine(cwd, "prompt-ack-supplied-memory.jsonl"), header, registry, Model,
            () => 1000, () => "prompt-ack-" + ++id, new(SessionLogStoreOptions: new(StorageFactory: new Factory(storage))));
    }
    private sealed class Factory(Storage storage) : ISessionLogStorageFactory
    { public ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) => ValueTask.FromResult<ISessionLogStorage>(storage); }
    private sealed class Storage : ISessionLogStorage
    {
        private readonly MemoryStream bytes = new(); internal bool Hold; internal int Writes;
        internal readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        internal Exception? CheckpointFault, DisposeFault;
        public Stream ReadStream => bytes; public long Length => bytes.Length;
        public SessionLogStorageDurability Durability => SessionLogStorageDurability.VolatileMemory;
        public void PositionForAppend(long length) { Check(bytes.Length == length); bytes.Position = length; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> value) { Writes++; return new(Own("physical:write", bytes.WriteAsync(value).AsTask())); }
        public ValueTask FlushAsync() => ValueTask.CompletedTask;
        public void FlushToDisk() { }
        public ValueTask BeforeCheckpointAsync()
        {
            if (CheckpointFault is { } fault) return new(Own("physical:checkpoint-fault", Task.FromException(fault)));
            if (!Hold) return ValueTask.CompletedTask;
            Hold = false; Entered.TrySetResult(); return new(Own("physical:held-checkpoint", Release.Task));
        }
        public ValueTask DisposeAsync()
        { bytes.Dispose(); return DisposeFault is { } fault ? new(Own("physical:dispose-fault", Task.FromException(fault))) : ValueTask.CompletedTask; }
    }
    private sealed class Transport : IChatTransport
    {
        internal Action<ChatRequest>? Observe; internal int Calls;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Calls++; Observe?.Invoke(request); await Task.CompletedTask;
            var message = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 1000, [new TextContent("done")], TokenUsage.Zero, StopReason.Stop);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
            yield return new TextStarted(0, new("")); yield return new TextDelta(0, "done"); yield return new TextEnded(0, "done"); yield return new StreamDone(StopReason.Stop, message);
        }
    }
    private sealed class Deny : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class Adapter : IPreparedToolAdapter
    {
        public string Name => "ack-tool";
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => throw new IOException("No tool execution admitted.");
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(false);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => throw new IOException("No tool execution admitted.");
    }
    private static async Task Own(string phase, Task task)
    { Exception? direct = null; try { await task.ConfigureAwait(false); } catch (Exception error) { direct = error; throw; } finally { Record(phase, task, direct); } }
    private static async Task<T> Own<T>(string phase, Task<T> task)
    { Exception? direct = null; try { return await task.ConfigureAwait(false); } catch (Exception error) { direct = error; throw; } finally { Record(phase, task, direct); } }
    private static void Record(string phase, Task task, Exception? direct)
    { lock (gate) { var cached = rows.FirstOrDefault(row => ReferenceEquals(row.Original, task)); rows.Add(new(phase, task, cached is null ? task.Exception : cached.Aggregate, cached is null ? direct : cached.Direct)); } }
    private static bool OnlyCleanup(Exception error)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var leaves = 0;
        bool Visit(Exception current)
        {
            if (!seen.Add(current)) return true;
            if (seen.Count > 64) return false;
            if (current is SessionLogStoreException { Failure: SessionLogStoreFailure.CleanupFailed, InnerException: null }) { leaves++; return true; }
            if (current is PersistentAgentSessionException { Fault.Failure: PersistentAgentSessionFailure.CleanupFailed, InnerException: { } inner }) return Visit(inner);
            return current is AggregateException all && all.InnerExceptions.Count != 0 && all.InnerExceptions.All(Visit);
        }
        return Visit(error) && leaves == 1;
    }
    private static void Finish(List<Exception> failures)
    { if (failures.Count != 0) throw new FixtureFailure(CapturedOriginals, failures.ToArray()); }
    private sealed class FixtureFailure((string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] originals, Exception[] failures)
        : IOException("Prompt section ACK source controls failed.", new AggregateException(failures))
    { internal (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] Originals { get; } = originals; internal Exception[] Failures { get; } = failures; }
}
