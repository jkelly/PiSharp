using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using PiSharp.CodingAgent;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Rpc.Protocol;

// Coordinator prepares the actual profile with Probe initialized on its existing scope
// before binding snapshot capture, then attaches the actual owner and constructs RPC.
// No second ActivateAsync, manual session_tree dispatch, reflection or empty-profile proof.
internal static class NativeRpcSelectedTreeHostTests
{
    internal sealed record ActualOwnerConsumer(ExtensionRegistry Registry, RegistrationScope Scope,
        ReplaceableAgentSession Owner, MemoryStream Output, RpcSessionDispatcher Dispatcher,
        string TargetEntryId, Func<Task> CloseOwnedProfile, NodeCommandInputExtension? OriginalTodo = null,
        string? OriginalTodoSourcePath = null);
    internal sealed record OriginalEvidence(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct);
    private static readonly ConcurrentQueue<OriginalEvidence> originals = new();
    internal static OriginalEvidence[] CapturedOriginals => originals.ToArray();
    private static readonly ConcurrentQueue<RpcSessionTreePublicationException> publicationCarriers = new();
    internal static RpcSessionTreePublicationException[] CapturedPublicationCarriers => publicationCarriers.ToArray();
    internal sealed record Evidence(string SessionId, long Generation, string? OldLeafId, string? NewLeafId,
        int TreeObservations, bool ResponseJoinedPublication, bool RetiredViewRefused, bool NoOpSuppressed,
        bool DispatcherRetirementJoined, RpcSessionTreePublicationException? PublicationFailure = null,
        JsonData? OriginalRunnerReceipt = null, JsonData? OriginalTodoList = null);
    private enum Mode { Response, Retirement, Failure, OriginalTodo }
    private sealed class Original(Task task, string phase)
    {
        internal readonly Task Task = task; internal readonly string Phase = phase;
        internal AggregateException? Aggregate; internal Exception? Direct; internal bool Captured;
    }
    internal static Task<Evidence> SelectedResponseAsync(Func<IPiSharpExtension, Task<ActualOwnerConsumer>> prepareActualOwner) => RunAsync(prepareActualOwner, Mode.Response);
    internal static Task<Evidence> SelectedRetirementAsync(Func<IPiSharpExtension, Task<ActualOwnerConsumer>> prepareActualOwner) => RunAsync(prepareActualOwner, Mode.Retirement);
    internal static Task<Evidence> SelectedPublicationFailureAsync(Func<IPiSharpExtension, Task<ActualOwnerConsumer>> prepareActualOwner) => RunAsync(prepareActualOwner, Mode.Failure);
    internal static Task<Evidence> OriginalTodoAutomaticRpcAsync(Func<IPiSharpExtension, Task<ActualOwnerConsumer>> prepareActualOwner) => RunAsync(prepareActualOwner, Mode.OriginalTodo);

    private static async Task<Evidence> RunAsync(Func<IPiSharpExtension, Task<ActualOwnerConsumer>> prepareActualOwner, Mode mode)
    {
        ArgumentNullException.ThrowIfNull(prepareActualOwner);
        ActualOwnerConsumer? consumer = null; RpcSessionDispatcher? dispatcher = null;
        var marker = new IOException("Actual selected observer failure control.");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rows = new ConcurrentQueue<(JsonData Event, ExtensionSessionSnapshot? Snapshot)>();
        var tracked = new Dictionary<Task, Original>(ReferenceEqualityComparer.Instance); var gate = new object();
        var expectedFaults = new HashSet<Task>(ReferenceEqualityComparer.Instance); var faults = new List<Exception>();
        var inventoriedCarriers = new HashSet<RpcSessionTreePublicationException>(ReferenceEqualityComparer.Instance);
        Task? callback = null, selection = null, retirement = null; Evidence? evidence = null;
        var probe = new Probe(async (observation, context, _) =>
        {
            var host = dispatcher ?? throw new IOException("Actual callback host absent.");
            Check(Refused(() => host.DisposeAsync()) && Refused(() => host.WaitForIdleAsync()), "Callback self disposal/settlement was admitted");
            Check(consumer is not null && context.OwnerId == consumer.Scope.OwnerId && context.OwnerGeneration == consumer.Scope.OwnerGeneration,
                "Observation did not run on the actual profile owner");
            rows.Enqueue((observation, (context as IExtensionSessionContext)?.SessionSnapshot));
            entered.TrySetResult(); await Observe(release.Task, "observer-release");
            if (mode == Mode.Failure) throw marker;
        }, original => { callback = original; Track(original, "actual-observer"); });
        try
        {
            consumer = await Own(prepareActualOwner(probe), "prepare-actual-owner-with-probe");
            dispatcher = consumer.Dispatcher;
            Check(ReferenceEquals(probe.AdmittedScope, consumer.Scope), "Probe must initialize once on the actual profile scope");
            var registrations = consumer.Registry.CaptureSnapshot();
            Check(registrations.Registrations.Length > 1 && registrations.Registrations.All(row =>
                row.OwnerId == consumer.Scope.OwnerId && row.OwnerGeneration == consumer.Scope.OwnerGeneration),
                "Control requires one actual registered extension owner, with more than the probe alone");
            var owner = consumer.Owner; var attached = owner.Current; owner.ValidateAttachment(attached);
            var captured = owner.CaptureTree(attached); var targetId = consumer.TargetEntryId; var oldLeaf = captured.LeafId;
            Check(targetId != oldLeaf && captured.Tree.ById.ContainsKey(targetId), "Distinct actual target required");
            var target = captured.Tree.ById[targetId].Entry;
            Check(target.Type != "custom_message" && !(target.Type == "message" && target.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "user"),
                "Control requires a non-user target whose selected leaf is its own ID");
            var log = attached.Session.Snapshot.Log; var node = consumer.OriginalTodo;
            var sourceOffset = node?.SourceOperations.Length ?? 0; JsonData? oldTodo = null;
            if (mode == Mode.OriginalTodo)
            {
                // Supplied by the actual profile factory from its verified launch,
                // independently of the worker's observed SourceLoadReport.
                Check(consumer.OriginalTodoSourcePath is { } expectedSource && Path.IsPathFullyQualified(expectedSource),
                    "Admitted absolute original Todo source is required");
                Check(node?.SourceLoadReport is { } load && load.Value.GetProperty("sourceCommit").GetString() == "d86654abb8862e201933517d6f1fce9f88dd117f" &&
                    load.Value.GetProperty("factoryAwaited").GetBoolean() && load.Value.GetProperty("sourceFunctionsRemainInNode").GetBoolean() &&
                    load.Value.GetProperty("sourcePins").EnumerateArray().Any(pin => pin.GetProperty("path").GetString() == NodeTierAAdmission.TodoSource &&
                        pin.GetProperty("bytes").GetInt32() == 8848 && pin.GetProperty("sha256").GetString() == "e46824d00217e25242c186d41837cc84ca81b23f978500323448502a9a424ee2"),
                    "Pinned genuine original Todo source is required");
                Check(registrations.Tools.Any(tool => tool.Name == "todo" && tool.OwnerId == consumer.Scope.OwnerId &&
                    tool.OwnerGeneration == consumer.Scope.OwnerGeneration), "Original Todo must share the actual profile owner");
                oldTodo = LatestTodo(attached.Session.Snapshot.Context.Ancestry.Select(entry => entry.WireBody));
            }
            await Observe(Submit("capture-tree", "pisharp_capture_navigation", null), "rpc-capture");
            var viewId = Response(consumer.Output, "capture-tree").Value.GetProperty("data").GetProperty("viewId").GetString()!;
            selection = Submit("select-tree", "pisharp_select_navigation", viewId); Track(selection, "rpc-selection");
            var first = await Task.WhenAny(entered.Task, selection).WaitAsync(TimeSpan.FromSeconds(10));
            Check(ReferenceEquals(first, entered.Task) && !selection.IsCompleted && !HasResponse(consumer.Output, "select-tree"),
                "Actual RPC response overtook its publication original");
            var state = attached.Session.Snapshot; owner.ValidateAttachment(attached);
            Check(state.Context.LeafId == targetId && ReferenceEquals(state.Log, log), "Actual no-append selection absent");
            var observed = rows.Single(); var snapshot = observed.Snapshot ?? throw new IOException("Actual observation session snapshot absent");
            Check(observed.Event.Value.GetProperty("oldLeafId").GetString() == oldLeaf && observed.Event.Value.GetProperty("newLeafId").GetString() == targetId &&
                snapshot.SessionId == captured.SessionId && snapshot.Generation == attached.Generation && snapshot.SelectedLeafId == targetId,
                "Observation context differs from committed selected branch");
            if (mode == Mode.Retirement)
            {
                retirement = dispatcher.DisposeAsync().AsTask(); Track(retirement, "dispatcher-retirement");
                Check(!retirement.IsCompleted, "Dispatcher retirement overtook held publication");
            }
            release.TrySetResult(); await Observe(selection, "rpc-selection");
            InventoryPublicationFailures();
            if (retirement is not null) await Observe(retirement, "dispatcher-retirement");
            RpcSessionTreePublicationException? failure = null; JsonData? runnerReceipt = null, todoList = null;
            if (mode == Mode.Failure)
            {
                failure = dispatcher.SessionTreePublicationFailures.Single();
                Check(failure.CommittedReceipt.LeafId == targetId && failure.Original is { IsFaulted: true } &&
                    failure.Evidence is AggregateException aggregate && aggregate.InnerExceptions.Any(error => ReferenceEquals(error, failure.Direct)),
                    "Captured aggregate must retain the awaited direct/native carrier provenance");
                Check(callback is { IsFaulted: true }, "Actual failing callback absent"); expectedFaults.Add(callback!);
                await Join(callback!, "actual-observer");
                var callbackRecord = Track(callback!, "actual-observer");
                Check(ReferenceEquals(callbackRecord.Direct, marker) && callbackRecord.Aggregate!.InnerExceptions.Any(error => ReferenceEquals(error, marker)),
                    "Cached callback aggregate lost the original source failure");
                var wire = Response(consumer.Output, "select-tree").Value;
                Check(!wire.GetProperty("success").GetBoolean() && wire.GetProperty("committed").GetBoolean() &&
                    wire.GetProperty("error").GetString() == "Session tree selection committed; lifecycle publication failed." &&
                    wire.GetProperty("data").GetProperty("sessionId").GetString() == failure.CommittedReceipt.SessionId &&
                    wire.GetProperty("data").GetProperty("generation").GetInt64() == attached.Generation &&
                    wire.GetProperty("data").GetProperty("leafId").GetString() == targetId &&
                    wire.GetProperty("data").GetProperty("disposition").GetString() == "Selected" &&
                    Fields(wire, "id", "type", "command", "success", "error", "committed", "data") &&
                    Fields(wire.GetProperty("data"), "disposition", "sessionId", "generation", "leafId") && !wire.GetRawText().Contains(marker.Message, StringComparison.Ordinal),
                    "Wire must preserve only fixed diagnostic and committed identity");
                Check(attached.Session.Snapshot.Context.LeafId == targetId && ReferenceEquals(attached.Session.Snapshot.Log, log), "Failure rolled back committed selection");
            }
            else if (mode != Mode.Retirement)
                Check(Response(consumer.Output, "select-tree").Value.GetProperty("data").GetProperty("disposition").GetString() == "Selected", "Committed response absent");
            if (mode == Mode.OriginalTodo)
            {
                var originalTodo = node ?? throw new IOException("Genuine original Todo worker absent.");
                var receipts = originalTodo.SourceOperations.Skip(sourceOffset).Where(row => row.Value.GetProperty("kind").GetString() == "session_tree").ToArray();
                Check(receipts.Length == 1, "Exactly one genuine original Runner session_tree receipt required"); runnerReceipt = receipts[0];
                var seam = runnerReceipt.Value.GetProperty("context").GetProperty("sessionManagerSeam");
                var sourceEvent = JsonData.Parse(runnerReceipt.Value.GetProperty("suppliedBefore").GetProperty("serializedJson").GetString()!);
                var callbackId = originalTodo.SourceLoadReport!.Value.GetProperty("sessionHandlers").EnumerateArray().Single(row =>
                    row.GetProperty("sourcePath").GetString() == consumer.OriginalTodoSourcePath && row.GetProperty("topic").GetString() == "session_tree").GetProperty("callbackId").GetString();
                Check(runnerReceipt.Value.GetProperty("status").GetString() == "fulfilled" && runnerReceipt.Value.GetProperty("publicationJoined").GetBoolean() &&
                    runnerReceipt.Value.GetProperty("callbackId").GetString() == callbackId && !runnerReceipt.Value.TryGetProperty("sourceRunnerErrors", out _) &&
                    sourceEvent.Value.GetProperty("type").GetString() == "session_tree" && sourceEvent.Value.GetProperty("oldLeafId").GetString() == oldLeaf &&
                    sourceEvent.Value.GetProperty("newLeafId").GetString() == targetId &&
                    runnerReceipt.Value.GetProperty("context").GetProperty("owner").GetString() == "genuine ExtensionRunner.createContext" &&
                    seam.GetProperty("presence").GetString() == "json" && seam.GetProperty("selectedLeafId").GetString() == targetId &&
                    seam.GetProperty("sessionId").GetString() == captured.SessionId && seam.GetProperty("generation").GetInt64() == attached.Generation,
                    "Actual original Runner receipt does not describe committed RPC branch");
                var expected = LatestTodo(state.Context.Ancestry.Select(entry => entry.WireBody));
                Check(expected.ToString() != oldTodo!.ToString() && LatestTodo(snapshot.BranchEntries).ToString() == expected.ToString(), "Different acknowledged Todo branch states required");
                var prepared = await Own(consumer.Registry.PrepareToolArgumentsAsync(registrations, "todo", JsonData.Parse("{\"action\":\"list\"}")).AsTask(), "original-todo-prepare");
                todoList = await Own(consumer.Registry.InvokeToolAsync(registrations, "todo", prepared, "rpc-original-todo-list",
                    (_, _) => ValueTask.CompletedTask).AsTask(), "original-todo-list");
                Check(TodoState(todoList.Value.GetProperty("details")).ToString() == expected.ToString() && originalTodo.ActiveContexts == 0 &&
                    originalTodo.WorkerSnapshot is { ActiveCallbacks: 0, PendingCalls: 0 }, "Genuine original Todo failed selected ancestry reconstruction/settlement");
            }
            var retired = false; var noOp = false;
            if (mode != Mode.Retirement)
            {
                await Observe(Submit("stale-view", "pisharp_select_navigation", viewId), "rpc-retired-view");
                retired = !Response(consumer.Output, "stale-view").Value.GetProperty("success").GetBoolean() && rows.Count == 1; Check(retired, "Retired chooser replayed publication");
                if (mode != Mode.Failure)
                {
                    await Observe(Submit("capture-noop", "pisharp_capture_navigation", null), "rpc-noop-capture");
                    var noOpId = Response(consumer.Output, "capture-noop").Value.GetProperty("data").GetProperty("viewId").GetString()!;
                    await Observe(Submit("select-noop", "pisharp_select_navigation", noOpId), "rpc-noop-selection");
                    noOp = Response(consumer.Output, "select-noop").Value.GetProperty("data").GetProperty("disposition").GetString() == "NoOp" && rows.Count == 1; Check(noOp, "NoOp published session_tree");
                }
            }
            evidence = new(captured.SessionId, attached.Generation, oldLeaf, targetId, rows.Count, true, retired, noOp,
                mode == Mode.Retirement, failure, runnerReceipt, todoList);
            Task Submit(string id, string type, string? view) => dispatcher.SubmitAsync(JsonData.Parse(JsonSerializer.Serialize(
                new { id, type, mode = "tree", generation = attached.Generation, viewId = view, targetId })));
        }
        catch (Exception error) { faults.Add(error); }
        finally
        {
            release.TrySetResult();
            if (selection is not null) await Join(selection, "rpc-selection");
            // Even a preassertion/output failure must retain every actual postcommit
            // carrier and its cached evidence before either owning close is acquired.
            InventoryPublicationFailures();
            if (retirement is not null) await Join(retirement, "dispatcher-retirement");
            if (dispatcher is not null) await Acquire(() => dispatcher.DisposeAsync().AsTask(), "dispatcher-close");
            // The actual profile owns its existing scope/plugin, worker and engine cleanup.
            if (consumer is not null) await Acquire(consumer.CloseOwnedProfile, "actual-profile-close");
            InventoryPublicationFailures();
            Original[] remaining; lock (gate) remaining = tracked.Values.ToArray();
            foreach (var record in remaining) await Join(record.Task, record.Phase);
        }
        if (faults.Count != 0) throw new AggregateException("Actual owner RPC selected publication control failed.",
            faults.Concat(inventoriedCarriers));
        return evidence ?? throw new IOException("Actual owner publication evidence absent.");
        Original Track(Task task, string phase) { lock (gate) { if (!tracked.TryGetValue(task, out var row)) tracked.Add(task, row = new(task, phase)); return row; } }
        void InventoryPublicationFailures()
        {
            if (dispatcher is null) return;
            foreach (var carrier in dispatcher.SessionTreePublicationFailures)
            {
                if (!inventoriedCarriers.Add(carrier)) continue;
                publicationCarriers.Enqueue(carrier);
                if (carrier.Original is { } original)
                {
                    if (mode == Mode.Failure) expectedFaults.Add(original);
                    // Preserve the carrier's cached aggregate wrapper and direct/native
                    // carrier references before any fallible diagnostic assertion.
                    Capture(Track(original, "actual-publication-original"), carrier.Direct, carrier.Evidence as AggregateException);
                }
            }
            if (mode == Mode.Failure && callback is not null) expectedFaults.Add(callback);
        }
        void Capture(Original row, Exception? direct, AggregateException? cached = null)
        { lock (gate) { if (row.Captured) return; row.Aggregate = cached ?? (row.Task.IsFaulted ? row.Task.Exception : null); row.Direct = direct; row.Captured = true; originals.Enqueue(new(row.Phase, row.Task, row.Aggregate, direct)); } }
        async Task Observe(Task task, string phase)
        { var row = Track(task, phase); try { await task.ConfigureAwait(false); } catch (Exception error) { Capture(row, error); throw; } finally { Capture(row, null); } }
        async Task<T> Own<T>(Task<T> task, string phase) { await Observe(task, phase); return task.GetAwaiter().GetResult(); }
        async Task Join(Task task, string phase)
        { try { await Observe(task, phase); } catch (Exception error) { if (!expectedFaults.Contains(task)) { var row = Track(task, phase); if (row.Aggregate is { } aggregate) faults.Add(aggregate); faults.Add(error); } } }
        async Task Acquire(Func<Task> acquire, string phase) { try { await Join(acquire(), phase); } catch (Exception error) { faults.Add(error); } }
    }
    internal static async Task MulticastRejectedBeforeEffectsAsync(Func<Func<SessionTreeNavigationReceipt, string?, ValueTask>, RpcSessionDispatcher> constructActualDispatcher)
    {
        var firstCalls = 0; var secondCalls = 0; var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<SessionTreeNavigationReceipt, string?, ValueTask> first = (_, _) => { firstCalls++; return new(held.Task); };
        Func<SessionTreeNavigationReceipt, string?, ValueTask> second = (_, _) => { secondCalls++; return ValueTask.CompletedTask; };
        var rejected = false; RpcSessionDispatcher? unexpected = null;
        try { unexpected = constructActualDispatcher((first + second)!); }
        catch (ArgumentException error) when (error.ParamName == "selectedTreePublisher") { rejected = true; }
        finally
        {
            held.TrySetResult(); await held.Task;
            originals.Enqueue(new("multicast-uninvoked-held-control-release", held.Task, null, null));
            if (unexpected is not null)
            {
                var original = unexpected.DisposeAsync().AsTask(); AggregateException? aggregate = null; Exception? direct = null;
                try { await original; } catch (Exception error) { direct = error; aggregate = original.IsFaulted ? original.Exception : null; throw; }
                finally { originals.Enqueue(new("unexpected-multicast-host-close", original, aggregate, direct)); }
            }
        }
        Check(rejected && firstCalls == 0 && secondCalls == 0, "Multicast must be rejected before either original callback/effect");
    }
    private static JsonData LatestTodo(IEnumerable<JsonData> entries)
    {
        var result = entries.LastOrDefault(entry => entry.Value.TryGetProperty("message", out var message) &&
            message.TryGetProperty("role", out var role) && role.GetString() == "toolResult" && message.TryGetProperty("toolName", out var name) &&
            name.GetString() == "todo" && message.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.False);
        Check(result is not null, "Acknowledged original Todo tool result required");
        return TodoState(result!.Value.GetProperty("message").GetProperty("details"));
    }
    private static JsonData TodoState(JsonElement details) => JsonData.Parse(JsonSerializer.Serialize(new
        { todos = details.GetProperty("todos"), nextId = details.GetProperty("nextId") }));
    private static bool Fields(JsonElement value, params string[] names) => value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal));
    private static bool Refused(Action operation) { try { operation(); return false; } catch (InvalidOperationException) { return true; } }
    private static void Check(bool value, string message) { if (!value) throw new IOException(message); }
    private static bool HasResponse(MemoryStream output, string id) => Frames(output).Any(frame => frame.Value.TryGetProperty("id", out var value) && value.GetString() == id);
    private static JsonData Response(MemoryStream output, string id) => Frames(output).Single(frame => frame.Value.TryGetProperty("id", out var value) && value.GetString() == id);
    private static JsonData[] Frames(MemoryStream output) => Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(JsonData.Parse).ToArray();
    private sealed class Probe(Func<JsonData, IExtensionContext, CancellationToken, Task> handler, Action<Task> capture) : IPiSharpExtension
    {
        internal RegistrationScope? AdmittedScope { get; private set; }
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
        {
            Check(AdmittedScope is null && registry is RegistrationScope, "Probe requires one actual native scope initialization"); AdmittedScope = (RegistrationScope)registry;
            registry.Observe(new("actual-selected-tree-probe", "session_tree", (value, context, cancellation) =>
                { var original = handler(value, context, cancellation); capture(original); return new(original); }));
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
