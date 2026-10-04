using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Protocol;
using PiSharp.ExtensionHost.Supervision;
using PiSharp.Extensions;

namespace PiSharp.Compatibility.Node;

/// <summary>One actual source tool, pure initial preparation and five-argument execution under native owner leases.</summary>
public sealed class NodeHelloExtension(NodeHelloWorkerLaunch launch, string workspace) : IPiSharpExtension
{
    private readonly object gate = new();
    private readonly Dictionary<string, IExtensionToolInvocationContext> contexts = new(StringComparer.Ordinal);
    private readonly List<JsonData> observations = [];
    private NodeWorkerSupervisor? worker; private WorkerCallbackHandle? updateHandle;
    private string? ownerId; private long generation, next; private long retainedBytes;
    private bool initialized; private Task? cleanup;
    public JsonData? SourceLoadReport { get; private set; }
    public JsonData? SourceFinalizationReport { get; private set; }
    public JsonData[] SourceOperations { get { lock (gate) return observations.ToArray(); } }
    public int ActiveContexts { get { lock (gate) return contexts.Count; } }
    public WorkerProtocolSnapshot? WorkerSnapshot => worker?.Snapshot;
    public Task<NodeWorkerTermination>? Termination => worker?.Termination;

    public async ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(launch);
        if (!Path.IsPathFullyQualified(workspace) || !Directory.Exists(workspace)) throw new IOException("Explicit Hello workspace required.");
        lock (gate) { if (initialized || cleanup is not null) throw new InvalidOperationException("Hello owner already initialized/closed."); initialized = true; }
        ownerId = registry.OwnerId; generation = registry.OwnerGeneration;
        worker = await NodeWorkerSupervisor.StartHelloAsync(launch, cancellationToken).ConfigureAwait(false);
        SourceLoadReport = Own(await worker.RequestAsync("hello.load", Json(new { ownerId, ownerGeneration = generation, cwd = workspace }), cancellationToken: cancellationToken).ConfigureAwait(false));
        var source = SourceLoadReport.Value;
        if (source.TryGetProperty("status", out var status) && status.GetString() == "rejected") throw new InvalidOperationException("Original Hello source activation failed; exact source diagnostics retained.");
        var descriptor = source.GetProperty("descriptor");
        if (source.GetProperty("sourceCommit").GetString() != "d86654abb8862e201933517d6f1fce9f88dd117f" ||
            source.GetProperty("callbackId").GetString() != "hello-callback-1" || !source.GetProperty("factoryAwaited").GetBoolean() ||
            !source.GetProperty("sourceFunctionRemainsInNode").GetBoolean() || descriptor.GetProperty("name").GetString() != "hello" ||
            descriptor.GetProperty("executeLength").GetInt32() != 5 || source.GetProperty("preparationReference").GetProperty("expectedSha256").GetString() != NodeHelloWorkerLaunch.PreparationExpectedSha256)
            throw new InvalidOperationException("Original Hello descriptor/provenance differs.");
        updateHandle = worker.RegisterCallback(ownerId, generation, PublishUpdateAsync);
        registry.RegisterTool(new("hello", descriptor.GetProperty("name").GetString()!, descriptor.GetProperty("description").GetString()!,
            JsonData.Parse(descriptor.GetProperty("parametersJson").GetString()!), ExecuteAsync) { PrepareInitialArgumentsAsync = PrepareAsync });
    }
    private async ValueTask<JsonData> PrepareAsync(JsonData arguments, CancellationToken cancellationToken)
    {
        var operation = NextOperation();
        var result = await InvokeAsync("hello.prepare", operation, arguments, null, cancellationToken).ConfigureAwait(false);
        if (result.Value.GetProperty("status").GetString() != "fulfilled") throw new InvalidOperationException("Original Hello argument validation rejected; exact observations retained.");
        return JsonData.Parse(result.Value.GetProperty("preparedJson").GetString()!);
    }
    private async ValueTask<JsonData> ExecuteAsync(JsonData arguments, IExtensionToolContext context, CancellationToken cancellationToken)
    {
        if (context is not IExtensionToolInvocationContext actual || context.OwnerId != ownerId || context.OwnerGeneration != generation)
            throw new InvalidOperationException("Actual owner-bound Hello invocation required.");
        var operation = NextOperation();
        lock (gate) { if (cleanup is not null || contexts.Count >= 8 || !contexts.TryAdd(operation, actual)) throw new InvalidOperationException("Hello invocation admission limit."); }
        try
        {
            var result = await InvokeAsync("hello.execute", operation, arguments, actual, cancellationToken).ConfigureAwait(false);
            if (result.Value.GetProperty("status").GetString() != "fulfilled" || !result.Value.GetProperty("updateDeliveryJoined").GetBoolean())
                throw new InvalidOperationException("Original Hello execution/publication failed; exact observations retained.");
            return JsonData.Parse(result.Value.GetProperty("resultJson").GetString()!);
        }
        finally { lock (gate) contexts.Remove(operation); }
    }
    private async Task<JsonData> InvokeAsync(string method, string operation, JsonData arguments,
        IExtensionToolInvocationContext? context, CancellationToken cancellationToken)
    {
        var current = worker ?? throw new InvalidOperationException("Hello worker unavailable.");
        var payload = new Dictionary<string, object?> { ["ownerId"] = ownerId, ["ownerGeneration"] = generation,
            ["callbackId"] = "hello-callback-1", ["operationId"] = operation, ["argumentsJson"] = arguments.ToString() };
        if (context is not null) payload.Add("toolCallId", context.ToolCallId);
        var request = Json(payload); if (Encoding.UTF8.GetByteCount(request.Json!.ToString()) > 262_144) throw new IOException("Hello request byte limit.");
        Exception? primary = null; WorkerValue? response = null; var admitted = false;
        try { var call = current.StartRequest(method, request, context is null ? null : updateHandle, cancellationToken); admitted = true; response = await call.Result.ConfigureAwait(false); }
        catch (Exception error) { primary = error; }
        try
        {
            if (admitted && primary is not WorkerProtocolException { Outcome: WorkerOutcome.NotSent })
            {
                if (!current.Snapshot.Stopped)
                {
                    var settlement = Own(await current.RequestAsync("hello.settle", Json(new { operationId = operation })).ConfigureAwait(false));
                    Retain(settlement);
                    if (!settlement.Value.GetProperty("settled").GetBoolean()) throw new InvalidOperationException("Original Hello settlement fence missing.");
                }
                else await current.Completion.ConfigureAwait(false);
            }
        }
        catch (Exception settlement) { if (primary is not null) throw new AggregateException(primary, settlement); throw; }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        cancellationToken.ThrowIfCancellationRequested(); return Own(response!);
    }
    private async ValueTask<WorkerValue> PublishUpdateAsync(WorkerRequestContext request, CancellationToken cancellationToken)
    {
        if (request.Method != "tool.update" || request.Value.Presence != WorkerValuePresence.Json) throw new InvalidOperationException("Unsupported Hello host callback.");
        var value = request.Value.Json!.Value;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 3) throw new InvalidOperationException("Malformed Hello progress callback.");
        var operation = value.GetProperty("operationId").GetString()!; IExtensionToolInvocationContext context;
        lock (gate) { if (cleanup is not null || !contexts.TryGetValue(operation, out context!)) throw new InvalidOperationException("Stale Hello progress context."); }
        if (value.GetProperty("toolCallId").GetString() != context.ToolCallId) throw new InvalidOperationException("Foreign Hello progress identity.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.OperationCancellationToken,
            context.SessionCancellationToken, context.ExtensionLifetimeCancellationToken);
        await context.ReportUpdateAsync(JsonData.Parse(value.GetProperty("partialResultJson").GetString()!), stop.Token).ConfigureAwait(false);
        stop.Token.ThrowIfCancellationRequested(); return Json(new { delivered = true });
    }
    private string NextOperation()
    { lock (gate) { if (cleanup is not null || next == long.MaxValue) throw new InvalidOperationException("Closed Hello operation admission."); return "hello-op-" + (++next).ToString(CultureInfo.InvariantCulture); } }
    private void Retain(JsonData value)
    { var bytes = Encoding.UTF8.GetByteCount(value.ToString()); lock (gate) { if (observations.Count >= 32 || bytes > 8_388_608 - retainedBytes) throw new IOException("Hello retained operation limit."); retainedBytes += bytes; observations.Add(value); } }
    private static WorkerValue Json<T>(T value) => WorkerValue.FromJson(JsonData.Parse(JsonSerializer.Serialize(value)));
    private static JsonData Own(WorkerValue value)
    { if (value.Presence != WorkerValuePresence.Json || value.Json is null || Encoding.UTF8.GetByteCount(value.Json.ToString()) > 524_288) throw new IOException("Hello response limit."); return JsonData.FromElement(value.Json.Value); }
    public ValueTask DisposeAsync() { lock (gate) return new(cleanup ??= CloseAsync()); }
    private async Task CloseAsync()
    {
        if (worker is null) return; var failures = new List<Exception>();
        try
        {
            lock (gate) { if (contexts.Count != 0) throw new InvalidOperationException("Hello execution contexts have not joined."); }
            worker.RevokeOwner(ownerId!, generation);
            if (!worker.Snapshot.Stopped) SourceFinalizationReport = Own(await worker.RequestAsync("hello.finalize", WorkerValue.FromJson(JsonData.EmptyObject)).ConfigureAwait(false));
        }
        catch (Exception error) { failures.Add(error); }
        try { await worker.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Hello source/worker cleanup failed.", failures);
    }
}
