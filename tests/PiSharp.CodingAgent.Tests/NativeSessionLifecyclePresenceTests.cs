using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

// Source-only actual-engine/registry controls. No shared runner registration or execution.
internal static class NativeSessionLifecyclePresenceTests
{
    internal sealed record OriginalEvidence(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);
    private static readonly ConcurrentQueue<OriginalEvidence> records = new();
    internal static OriginalEvidence[] CapturedOriginals => records.ToArray();
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [ ("native-lifecycle.actual-memory-replacement-omits-previous-file-and-selected-tree", () => Run(false)),
      ("native-lifecycle.actual-local-replacement-retains-exact-previous-file-and-selected-tree", () => Run(true)) ];
    private static void Check(bool condition) { if (!condition) throw new IOException("Native lifecycle presence control failed."); }
    private static async Task Run(bool persisted)
    {
        var folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pisharp-native-lifecycle-" + Guid.NewGuid().ToString("N")));
        var failures = new List<Exception>(); var joined = new HashSet<Task>(ReferenceEqualityComparer.Instance);
        PersistentAgentSession? initial = null, target = null; ReplaceableAgentSession? owner = null;
        ExtensionRegistry? registry = null; RegistrationScope? scope = null;
        var transport = new NoTransport(); var snapshots = new List<ExtensionSessionSnapshot?>(); var starts = new List<JsonData>(); var trees = new List<JsonData>();
        var diagnostics = new List<ExtensionEventDiagnostic>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AgentSessionReplacement?>? switching = null; Task? publisher = null;
        using var timerStop = new CancellationTokenSource(); Task? timer = null;
        try
        {
            Directory.CreateDirectory(folder);
            var memory = new SessionStorageBackend(folder, SessionStorageMode.InMemory);
            var ids = 0; var codec = new SessionEntryCodec(); var model = new ModelDescriptor("native-lifecycle", "openai-responses", "synthetic-offline");
            var config = new AgentConfiguration(model, transport, []);
            var header = codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "native-previous", timestamp = "2026-01-01T00:00:00Z", cwd = folder }));
            var actualInitial = await Own("create-initial", () => PersistentAgentSession.CreateAsync(Path.Combine(folder, "previous.jsonl"), header,
                config, () => 1, () => "native-initial-" + ++ids,
                options: persisted ? null : new(SessionLogStoreOptions: new(StorageFactory: memory))));
            initial = actualInitial;
            var targetHeader = codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "native-target", timestamp = "2026-01-01T00:00:00Z", cwd = folder }));
            var exactTarget = await Own("create-target", () => PersistentAgentSession.CreateAsync(Path.Combine(folder, "target.jsonl"), targetHeader,
                config, () => 1, () => "native-target-" + ++ids, options: new(SessionLogStoreOptions: new(StorageFactory: memory))));
            target = exactTarget;
            var activeOwner = new ReplaceableAgentSession(actualInitial, (_, _) => Task.FromResult(exactTarget));
            owner = activeOwner; var previous = activeOwner.Current;
            var views = new NativeSessionSnapshotProvider(); views.Attach(activeOwner); var activeRegistry = new ExtensionRegistry(null, null, views); registry = activeRegistry;
            scope = await Own("activate-native-observers", () => activeRegistry.ActivateAsync("native-lifecycle", new Plugin(entries =>
            {
                entries.Observe(new("actual-start", "session_start", async (value, context, _) =>
                {
                    starts.Add(value); snapshots.Add(((IExtensionSessionContext)context).SessionSnapshot);
                    entered.TrySetResult(); await OwnVoid("held-observer-release", () => release.Task);
                }));
                entries.Observe(new("actual-tree", "session_tree", (value, context, _) =>
                { trees.Add(value); snapshots.Add(((IExtensionSessionContext)context).SessionSnapshot); return ValueTask.CompletedTask; }));
            })));
            var captured = activeRegistry.CaptureSnapshot();
            var treeBinding = new NativeSessionTreeObservationBinding(activeRegistry, captured);
            var before = activeOwner.CaptureTree(previous); var oldLeaf = before.LeafId;
            var selected = await Own("actual-navigate-empty-root", () => activeOwner.NavigateTreeAsync(previous, new(null, before.Revision)));
            Check(selected.Disposition == SessionTreeNavigationDisposition.Selected);
            await OwnVoid("publish-actual-selected-tree", () => treeBinding.PublishAsync(activeOwner, selected, oldLeaf).AsTask());
            Check(trees.Count == 1 && trees[0].Value.GetProperty("oldLeafId").GetString() == oldLeaf &&
                trees[0].Value.GetProperty("newLeafId").ValueKind == JsonValueKind.Null && snapshots[0]?.SelectedLeafId is null &&
                snapshots[0]?.SessionId == "native-previous" && !trees[0].Value.TryGetProperty("summaryEntry", out _) && !trees[0].Value.TryGetProperty("fromExtension", out _));
            var unchanged = activeOwner.CaptureTree(previous);
            var noOp = await Own("actual-noop-selection", () => activeOwner.NavigateTreeAsync(previous, new(null, unchanged.Revision)));
            Check(noOp.Disposition == SessionTreeNavigationDisposition.NoOp);
            await OwnVoid("no-publication-on-noop", () => treeBinding.PublishAsync(activeOwner, noOp, null).AsTask()); Check(trees.Count == 1);
            var replacementBinding = new NativeReplacementSessionStartBinding(activeRegistry, captured,
                (value, _) => { diagnostics.Add(value); return ValueTask.CompletedTask; });
            activeOwner.AfterReplacement = replacement =>
            { publisher = replacementBinding.PublishAsync(activeOwner, replacement).AsTask(); return new(publisher); };
            switching = activeOwner.SwitchAsync(previous, new(exactTarget.Path));
            timer = Task.Delay(TimeSpan.FromSeconds(5), timerStop.Token);
            Check(ReferenceEquals(await Task.WhenAny(entered.Task, switching, timer), entered.Task));
            Check(!switching.IsCompleted && publisher is { IsCompleted: false });
            Check(starts.Count == 1 && starts[0].Value.GetProperty("type").GetString() == "session_start" && starts[0].Value.GetProperty("reason").GetString() == "resume");
            var hasPrevious = starts[0].Value.TryGetProperty("previousSessionFile", out var previousFile);
            Check(hasPrevious == persisted && (!persisted || previousFile.ValueKind == JsonValueKind.String && previousFile.GetString() == actualInitial.Path));
            Check(snapshots[1]?.SessionId == "native-target" && snapshots[1]?.Generation == 2 && activeOwner.Current.Session == exactTarget);
        }
        catch (Exception error) { Fail(error); }
        finally
        {
            release.TrySetResult();
            if (switching is not null) await OwnVoid("join-actual-switch", () => switching);
            if (publisher is not null) await OwnVoid("join-actual-publication", () => publisher);
            try { await OwnVoid("cancel-deadline", () => timerStop.CancelAsync()); } catch (Exception error) { Fail(error); }
            if (timer is not null) await JoinTimer(timer);
            if (scope is not null) await Cleanup("close-native-scope", scope.DisposeAsync);
            if (registry is not null) await Cleanup("close-registry", registry.DisposeAsync);
            if (owner is not null) await Cleanup("close-session-owner", owner.DisposeAsync);
            if (target is not null) await Cleanup("close-target-idempotent", target.DisposeAsync);
            if (initial is not null) await Cleanup("close-initial-idempotent", initial.DisposeAsync);
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch (Exception error) { Fail(error); }
        }
        try { Check(diagnostics.Count == 0 && starts.Count == 1 && trees.Count == 1 && transport.Calls == 0); } catch (Exception error) { Fail(error); }
        if (failures.Count != 0) throw new AggregateException("Actual native lifecycle originals and predicates.", failures);
        void Fail(Exception error) { lock (failures) failures.Add(error); }
        bool MarkJoined(Task original) { lock (joined) return joined.Add(original); }
        async Task<T> Own<T>(string phase, Func<Task<T>> acquire)
        {
            Task<T>? original = null; Exception? direct = null; AggregateException? aggregate = null;
            try { original = acquire(); MarkJoined(original); return await original; }
            catch (Exception error) { direct = error; aggregate = original is { IsFaulted: true } ? original.Exception : null; if (aggregate is not null) Fail(aggregate); throw; }
            finally { if (original is not null) records.Enqueue(new(phase, original, aggregate, direct)); }
        }
        async Task OwnVoid(string phase, Func<Task> acquire)
        {
            Task? original = null; Exception? direct = null; AggregateException? aggregate = null; var admitted = false;
            try { original = acquire(); if (!MarkJoined(original)) return; admitted = true; await original; }
            catch (Exception error) { direct = error; aggregate = original is { IsFaulted: true } ? original.Exception : null; if (aggregate is not null) Fail(aggregate); Fail(error); }
            finally { if (original is not null && admitted) records.Enqueue(new(phase, original, aggregate, direct)); }
        }
        async Task Cleanup(string phase, Func<ValueTask> acquire) => await OwnVoid(phase, () => acquire().AsTask());
        async Task JoinTimer(Task original)
        {
            Exception? direct = null; AggregateException? aggregate = null;
            try { MarkJoined(original); await original; }
            catch (OperationCanceledException error) when (timerStop.IsCancellationRequested && original.IsCanceled && error.CancellationToken == timerStop.Token) { direct = error; }
            catch (Exception error) { direct = error; aggregate = original.IsFaulted ? original.Exception : null; if (aggregate is not null) Fail(aggregate); Fail(error); }
            finally { records.Enqueue(new("join-deadline", original, aggregate, direct)); }
        }
    }
    private sealed class Plugin(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NoTransport : IChatTransport
    {
        internal int Calls;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { Calls++; await Task.FromException(new IOException("Lifecycle controls must not request a provider.")); yield break; }
    }
}
