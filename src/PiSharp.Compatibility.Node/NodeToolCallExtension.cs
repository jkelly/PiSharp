using System.Runtime.ExceptionServices;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Protocol;
using PiSharp.ExtensionHost.Supervision;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;

namespace PiSharp.Compatibility.Node;

/// <summary>Fixed authored control branches are explicit test inputs; None loads only the real pinned module.</summary>
public enum NodeHookStartupControl { None, HoldPublication, AuthoredAsyncFailure, AuthoredUnsupportedRegistration }

/// <summary>One unchanged tool_call hook. The registry owns publication and callback leases; Node owns the JS function.</summary>
public sealed class NodeToolCallExtension : IPiSharpExtension
{
    private readonly NodeExtensionWorkerLaunch launch; private readonly NodeHookStartupControl startup;
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new(); private Task? cleanup;
    private NodeWorkerSupervisor? worker; private NodeExtensionUiBroker? broker;
    private string? ownerId; private long generation; private bool initialized;
    public Task InitializationEntered => entered.Task;
    public JsonData? SourceLoadReport { get; private set; }
    public JsonData? LastSourceObservation { get; private set; }
    public JsonData? LastSourceSettlement { get; private set; }
    public JsonData? SourceFinalizationReport { get; private set; }
    public int ActiveBrokerContexts => broker?.ActiveContexts ?? 0;
    public Task<NodeWorkerTermination>? Termination => worker?.Termination;
    public WorkerProtocolSnapshot? WorkerSnapshot => worker?.Snapshot;
    public NodeToolCallExtension(NodeExtensionWorkerLaunch launch, NodeHookStartupControl startup = NodeHookStartupControl.None)
    { this.launch = launch ?? throw new ArgumentNullException(nameof(launch)); if (!Enum.IsDefined(startup)) throw new ArgumentOutOfRangeException(nameof(startup)); this.startup = startup; }
    public void ReleaseInitialization()
    { if (startup is not (NodeHookStartupControl.HoldPublication or NodeHookStartupControl.AuthoredAsyncFailure)) throw new InvalidOperationException("No held initialization."); release.TrySetResult(); }
    public async ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry); lock (gate) { if (initialized || cleanup is not null) throw new InvalidOperationException("Extension already initialized/closed."); initialized = true; }
        ownerId = registry.OwnerId; generation = registry.OwnerGeneration;
        worker = await NodeWorkerSupervisor.StartProtectedPathsAsync(launch, cancellationToken).ConfigureAwait(false);
        var mode = startup switch { NodeHookStartupControl.AuthoredAsyncFailure => "async-failure", NodeHookStartupControl.AuthoredUnsupportedRegistration => "unsupported", _ => "actual" };
        var metadata = await worker.RequestAsync("extension.load", Json(new { ownerId, ownerGeneration = generation, mode }), cancellationToken: cancellationToken).ConfigureAwait(false);
        SourceLoadReport = Own(metadata, 524_288);
        if (startup == NodeHookStartupControl.AuthoredAsyncFailure)
        {
            entered.TrySetResult(); await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var failure = await worker.RequestAsync("extension.release-factory", WorkerValue.FromJson(JsonData.EmptyObject), cancellationToken: cancellationToken).ConfigureAwait(false);
            LastSourceSettlement = Own(failure, 524_288);
            if (failure.Json?.Value.GetProperty("rejected").GetBoolean() != true) throw new InvalidOperationException("Authored failure did not reject.");
            throw new InvalidOperationException("Authored asynchronous factory rejected.");
        }
        var value = SourceLoadReport.Value;
        if (value.TryGetProperty("status", out var status) && status.GetString() == "rejected")
            throw new InvalidOperationException(value.GetProperty("code").GetString() == "UnsupportedRegistration"
                ? "Unsupported Node extension registration; complete source diagnostics are retained in SourceLoadReport."
                : "Node extension factory rejected; complete source diagnostics are retained in SourceLoadReport.");
        if (value.GetProperty("event").GetString() != "tool_call" || value.GetProperty("callbackId").GetString() != "protected-paths-1" ||
            value.GetProperty("sourceCommit").GetString() != "d86654abb8862e201933517d6f1fce9f88dd117f" ||
            value.GetProperty("referenceManifestSha256").GetString() != NodeExtensionWorkerLaunch.ReferenceManifestSha256 ||
            !value.GetProperty("factoryAwaited").GetBoolean() || !value.GetProperty("sourceFunctionRemainsInNode").GetBoolean())
            throw new InvalidOperationException("Source hook admission metadata differs.");
        broker = new(worker, ownerId, generation);
        registry.RegisterToolCallHandler(new("protected-paths-1", InvokeAsync));
        entered.TrySetResult(); if (startup == NodeHookStartupControl.HoldPublication) await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    private async ValueTask<ExtensionToolCallPatch?> InvokeAsync(ExtensionToolCallEvent input,
        IExtensionContext context, CancellationToken cancellationToken)
    {
        var current = worker ?? throw new InvalidOperationException("Worker unavailable.");
        using var lease = broker!.Borrow(context); Exception? primary = null; WorkerValue? observation = null; var admitted = false;
        try
        {
            var payload = JsonData.Parse("{\"ownerId\":" + JsonSerializer.Serialize(ownerId) + ",\"ownerGeneration\":" + generation.ToString(CultureInfo.InvariantCulture) +
                ",\"callbackId\":\"protected-paths-1\",\"operationId\":" + JsonSerializer.Serialize(lease.OperationId) +
                ",\"hasUI\":" + (lease.HasUi ? "true" : "false") + ",\"event\":{\"type\":\"tool_call\",\"toolCallId\":" +
                JsonSerializer.Serialize(input.ToolCallId) + ",\"toolName\":" + JsonSerializer.Serialize(input.ToolName) + ",\"input\":" + input.Arguments +
                (input.ParentToolCallId is null ? "" : ",\"parentToolCallId\":" + JsonSerializer.Serialize(input.ParentToolCallId)) + "}}");
            if (Encoding.UTF8.GetByteCount(payload.ToString()) > 262_144) throw new InvalidOperationException("Bridge invocation byte limit.");
            var call = current.StartRequest("extension.invoke", WorkerValue.FromJson(payload), broker.Handle, cancellationToken);
            admitted = true; observation = await call.Result.ConfigureAwait(false);
        }
        catch (Exception error) { primary = error; }
        // Cancellation detaches the observer before physical/source settlement. The fence awaits the actual JS finally.
        try
        {
            if (!admitted || primary is WorkerProtocolException { Outcome: WorkerOutcome.NotSent }) { }
            else if (!current.Snapshot.Stopped)
            {
                var fence = await current.RequestAsync("extension.settle", Json(new { operationId = lease.OperationId })).ConfigureAwait(false);
                LastSourceSettlement = Own(fence, 524_288);
                if (fence.Json?.Value.GetProperty("settled").GetBoolean() != true) throw new InvalidOperationException("Worker settlement fence missing.");
            }
            else await current.Completion.ConfigureAwait(false);
        }
        catch (Exception settlement) { if (primary is not null) throw new AggregateException(primary, settlement); throw; }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        cancellationToken.ThrowIfCancellationRequested(); var ownedObservation = Own(observation!, 524_288);
        LastSourceObservation = ownedObservation; // Diagnostic last record may change as other callbacks settle.
        var result = ownedObservation.Value;
        if (!result.GetProperty("publicationJoined").GetBoolean()) throw new InvalidOperationException("Native publication did not join.");
        var after = result.GetProperty("inputAfter").GetProperty("serializedJson").GetString()!;
        var before = result.GetProperty("inputBefore").GetProperty("serializedJson").GetString()!;
        using var sourceEvent = JsonDocument.Parse(after);
        var arguments = JsonData.FromElement(sourceEvent.RootElement.GetProperty("input"));
        // Compare source-owned observations to one another, retaining original native raw tokens for an unchanged input.
        JsonData? replacement = after == before ? null : arguments;
        return result.GetProperty("resultPresence").GetString() switch
        {
            "undefined" when !result.TryGetProperty("result", out _) => replacement is null ? null : new(replacement),
            "json" when result.TryGetProperty("result", out var decision) && decision.ValueKind == JsonValueKind.Object => new(replacement, JsonData.FromElement(decision)),
            _ => throw new InvalidOperationException("Unsupported source hook result.")
        };
    }
    private static WorkerValue Json<T>(T value) => WorkerValue.FromJson(JsonData.Parse(JsonSerializer.Serialize(value)));
    private static JsonData Own(WorkerValue value, int cap)
    { if (value.Presence != WorkerValuePresence.Json || value.Json is null || Encoding.UTF8.GetByteCount(value.Json.ToString()) > cap)
            throw new InvalidOperationException("Bridge retained JSON limit."); return JsonData.FromElement(value.Json.Value); }
    public ValueTask DisposeAsync() { lock (gate) return new(cleanup ??= CleanupAsync()); }
    private async Task CleanupAsync()
    {
        if (worker is null) return;
        var failures = new List<Exception>();
        try { broker?.Dispose(); } catch (Exception error) { failures.Add(error); }
        try
        {
            if (!worker.Snapshot.Stopped)
            {
                SourceFinalizationReport = Own(await worker.RequestAsync("extension.finalize", WorkerValue.FromJson(JsonData.EmptyObject)).ConfigureAwait(false), 524_288);
                if (SourceFinalizationReport.Value.TryGetProperty("status", out var status) && status.GetString() == "failed")
                    throw new InvalidOperationException("Node source finalization failed; actual bounded stage/error diagnostics are retained in SourceFinalizationReport.");
            }
        }
        catch (Exception error) { failures.Add(error); }
        try { await worker.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        // Bounded last observations remain reviewable after failure/cleanup. No progress history is retained.
        if (failures.Count != 0) throw new AggregateException("Node bridge cleanup failed.", failures);
    }
}
