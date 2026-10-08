using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Protocol;
using PiSharp.ExtensionHost.Supervision;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;

namespace PiSharp.Compatibility.Node;

/// <summary>Whole original Commands/Input functions; native registry, catalog, UI and cancellation own authority.</summary>
public sealed partial class NodeCommandInputExtension(NodeCommandInputWorkerLaunch launch, string workspace,
    int inputInstances = 1, string? controlledClock = null, ImmutableArray<string> sourcePaths = default,
    OriginalUiQualificationSourceLease? qualificationSource = null) : IPiSharpExtension
{
    private readonly object gate = new();
    private readonly Dictionary<string, BorrowedOperation> contexts = new(StringComparer.Ordinal);
    private readonly List<JsonData> observations = [];
    private NodeWorkerSupervisor? worker; private WorkerCallbackHandle? uiHandle;
    private string? ownerId; private long generation, next, retainedBytes;
    private readonly ConditionalWeakTable<ToolLoadout, SnapshotIdentity> loadouts = new();
    private long nextLoadout;
    private sealed record SnapshotIdentity(string Id);
    private bool initialized; private Task? cleanup;
    public JsonData? SourceLoadReport { get; private set; }
    public JsonData? SourceFinalizationReport { get; private set; }
    public JsonData[] SourceOperations { get { lock (gate) return observations.ToArray(); } }
    public int ActiveContexts { get { lock (gate) return contexts.Count; } }
    public WorkerProtocolSnapshot? WorkerSnapshot => worker?.Snapshot;
    public Task<NodeWorkerTermination>? Termination => worker?.Termination;
    private sealed class BorrowedOperation(IExtensionContext context, NodeToolProgressDelivery? progress = null,
        NativeComponentTransport? transport = null, NativeDialogOperation? dialogs = null)
    {
        internal IExtensionContext Context { get; } = context;
        internal NodeToolProgressDelivery? Progress { get; } = progress;
        internal NativeComponentTransport? Transport { get; } = transport;
        internal NativeDialogOperation? Dialogs { get; } = dialogs;
        internal TaskCompletionSource<WorkerCall> CallReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<object> CancellationWrites { get; } = [];
        internal Exception? CancellationWriteFailure;
    }

    public async ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(launch);
        if (!Path.IsPathFullyQualified(workspace) || !Directory.Exists(workspace) || inputInstances is < 1 or > 2 ||
            controlledClock is not null and not "2026-10-01T12:00:00.000Z")
            throw new IOException("Explicit Commands/Input workspace, instance count and optional exact corpus clock required.");
        lock (gate) { if (initialized || cleanup is not null) throw new InvalidOperationException("Commands/Input owner already initialized/closed."); initialized = true; }
        if (!sourcePaths.IsDefault) NodeTierAAdmission.ValidateSelection(sourcePaths, inputInstances);
        ownerId = registry.OwnerId; generation = registry.OwnerGeneration;
        Dictionary<string, object?>? qualification = null;
        if (qualificationSource is not null)
        {
            if (!sourcePaths.IsDefault || inputInstances != 1 || controlledClock is not null)
                throw new InvalidOperationException("Private qualification cannot combine canonical selection or clock overrides.");
            qualification = qualificationSource.Bind(ownerId, generation);
        }
        worker = await NodeWorkerSupervisor.StartCommandsAndInputAsync(launch, cancellationToken).ConfigureAwait(false);
        var load = new Dictionary<string, object?> { ["ownerId"] = ownerId, ["ownerGeneration"] = generation,
            ["cwd"] = workspace, ["inputInstances"] = inputInstances, ["controlledClock"] = controlledClock };
        if (!sourcePaths.IsDefault) load.Add("sourcePaths", sourcePaths.ToArray());
        if (qualification is not null) load.Add("qualificationLease", qualification);
        SourceLoadReport = Own(await worker.RequestAsync(qualification is null ? "command-input.load" : "command-input.load-qualification", Json(load), cancellationToken: cancellationToken).ConfigureAwait(false));
        var source = SourceLoadReport.Value;
        if (source.TryGetProperty("status", out var status) && status.GetString() == "rejected")
            throw new InvalidOperationException("Original Commands/Input activation failed; exact source diagnostics retained.");
        if (qualificationSource is null) NodeTierAAdmission.ValidateReceipt(source, sourcePaths, inputInstances, launch.OracleRoot);
        else qualificationSource.ValidateReceipt(source);
        uiHandle = worker.RegisterCallback(ownerId, generation, DispatchUiAsync);
        foreach (var command in source.GetProperty("commands").EnumerateArray())
        {
            var id = command.GetProperty("callbackId").GetString()!;
            registry.RegisterCommand(new(id, command.GetProperty("name").GetString()!, command.GetProperty("description").GetString()!,
                (arguments, context, token) => InvokeCommandAsync(id, arguments, context, token))
            {
                GetArgumentCompletionsAsync = command.GetProperty("hasCompletion").GetBoolean() ? (prefix, token) => CompleteAsync(id, prefix, token) : null,
                SourcePath = command.GetProperty("sourcePath").GetString()!
            });
        }
        foreach (var input in source.GetProperty("inputHandlers").EnumerateArray())
        {
            var id = input.GetProperty("callbackId").GetString()!;
            registry.RegisterInputHandler(new(id, (snapshot, context, token) => InvokeInputAsync(id, snapshot, context, token)));
        }
        foreach (var hook in source.GetProperty("beforeAgentStartHandlers").EnumerateArray())
        {
            var id = hook.GetProperty("callbackId").GetString()!;
            registry.RegisterBeforeAgentStartHandler(new(id, (snapshot, context, token) => InvokeBeforeAgentStartAsync(id, snapshot, context, token)));
        }
        foreach (var tool in source.GetProperty("tools").EnumerateArray())
        {
            var id = tool.GetProperty("callbackId").GetString()!;
            RetainOriginalRenderers(tool);
            RegisterNativeToolRenderers(registry, tool);
            registry.RegisterTool(new(id, tool.GetProperty("name").GetString()!, tool.GetProperty("description").GetString()!,
                JsonData.Parse(tool.GetProperty("parametersJson").GetString()!), (arguments, context, token) => InvokeToolAsync(id, arguments, context, token))
            {
                PrepareInitialArgumentsAsync = (arguments, token) => PrepareToolAsync(id, arguments, token),
                Namespace = NodeToolLoadoutMetadata.ReadNamespace(tool),
                PrepareLoadout = tool.GetProperty("hasLoadoutPreparation").GetBoolean() ? loadout => PrepareLoadout(id, loadout) : null
            });
        }
        RegisterSessionLifecycleHandlers(registry, source);
    }
    private async ValueTask<JsonData> CompleteAsync(string callbackId, string prefix, CancellationToken cancellationToken)
    {
        var operation = NextOperation();
        var result = await InvokeAsync("command-input.completion", operation, new()
        { ["callbackId"] = callbackId, ["prefix"] = prefix }, null, cancellationToken).ConfigureAwait(false);
        if (result.Value.GetProperty("status").GetString() != "fulfilled") throw new InvalidOperationException("Original completion callback failed; source receipt retained.");
        var value = JsonData.Parse(result.Value.GetProperty("resultJson").GetString()!);
        if (value.Value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null)) throw new InvalidOperationException("Original completion result shape differs.");
        return value;
    }
    private async ValueTask InvokeCommandAsync(string callbackId, JsonData arguments, IExtensionCommandContext context, CancellationToken cancellationToken)
    {
        if (arguments.Value.ValueKind != JsonValueKind.String || context is not IExtensionCommandCatalogContext catalog)
            throw new InvalidOperationException("Original command requires its exact argument string and actual native catalog context.");
        var operation = Borrow(context);
        Exception? primary = null;
        try
        {
            var result = await InvokeAsync("command-input.command", operation, new()
            {
                ["callbackId"] = callbackId, ["argumentsJson"] = arguments.ToString(),
                ["catalogJson"] = catalog.CommandCatalog.ToString(), ["catalogRevision"] = catalog.CommandCatalogRevision,
                ["uiCapabilities"] = Capabilities(context)
            }, context, cancellationToken).ConfigureAwait(false);
            RequirePublication(result);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await RetireContextAsync(operation, primary).ConfigureAwait(false); }
    }
    private async ValueTask<ExtensionInputPatch?> InvokeInputAsync(string callbackId, ExtensionInputEvent input,
        IExtensionContext context, CancellationToken cancellationToken)
    {
        var operation = Borrow(context);
        Exception? primary = null;
        try
        {
            var value = new Dictionary<string, object?>
            {
                ["type"] = "input", ["text"] = input.Text, ["source"] = input.Source switch
                {
                    ExtensionInputSource.Interactive => "interactive", ExtensionInputSource.Rpc => "rpc", ExtensionInputSource.Extension => "extension",
                    _ => throw new InvalidOperationException("Unknown actual input source.")
                }
            };
            if (input.Images is not null) value.Add("images", input.Images.Value);
            if (input.StreamingBehavior is not null) value.Add("streamingBehavior", input.StreamingBehavior);
            var result = await InvokeAsync("command-input.input", operation, new()
            {
                ["callbackId"] = callbackId, ["eventJson"] = JsonSerializer.Serialize(value), ["uiCapabilities"] = Capabilities(context)
            }, context, cancellationToken).ConfigureAwait(false);
            RequirePublication(result);
            if (result.Value.GetProperty("resultPresence").GetString() == "undefined") return null;
            var patch = JsonData.Parse(result.Value.GetProperty("resultJson").GetString()!).Value;
            if (patch.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Original Input patch shape differs.");
            return patch.GetProperty("action").GetString() switch
            {
                "continue" => new(ExtensionInputAction.Continue), "handled" => new(ExtensionInputAction.Handled),
                "transform" when patch.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String =>
                    new(ExtensionInputAction.Transform, text.GetString(), patch.TryGetProperty("images", out var images) ? JsonData.FromElement(images) : null),
                _ => throw new InvalidOperationException("Original Input decision is invalid; complete receipt retained.")
            };
        }
        catch (Exception error) { primary = error; throw; }
        finally { await RetireContextAsync(operation, primary).ConfigureAwait(false); }
    }
    private async ValueTask<ExtensionBeforeAgentStartPatch?> InvokeBeforeAgentStartAsync(string callbackId,
        ExtensionBeforeAgentStartEvent snapshot, IExtensionContext context, CancellationToken cancellationToken)
    {
        var operation = Borrow(context);
        Exception? primary = null;
        try
        {
            var supplied = new Dictionary<string, object?> { ["type"] = "before_agent_start", ["prompt"] = snapshot.Prompt, ["systemPrompt"] = snapshot.SystemPrompt };
            if (snapshot.Images is not null) supplied.Add("images", snapshot.Images.Value);
            var result = await InvokeAsync("command-input.before-agent-start", operation, new()
            { ["callbackId"] = callbackId, ["eventJson"] = JsonSerializer.Serialize(supplied), ["uiCapabilities"] = Capabilities(context) }, context, cancellationToken).ConfigureAwait(false);
            RequirePublication(result);
            if (result.Value.GetProperty("resultPresence").GetString() == "undefined") return null;
            var patch = JsonData.Parse(result.Value.GetProperty("resultJson").GetString()!).Value;
            // This bounded profile translates the system prompt only; never silently discard a message or richer prompt options.
            if (patch.ValueKind != JsonValueKind.Object || patch.EnumerateObject().Any(property => property.Name != "systemPrompt") ||
                !patch.TryGetProperty("systemPrompt", out var prompt) || prompt.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Unsupported Node before_agent_start result; exact receipt retained.");
            return new(SystemPrompt: prompt.GetString());
        }
        catch (Exception error) { primary = error; throw; }
        finally { await RetireContextAsync(operation, primary).ConfigureAwait(false); }
    }
    private async ValueTask<JsonData> InvokeToolAsync(string callbackId, JsonData arguments,
        IExtensionToolContext context, CancellationToken cancellationToken)
    {
        if (context is not IExtensionToolInvocationContext actual) throw new InvalidOperationException("Actual native tool invocation identity required.");
        var operation = Borrow(context, callbackId);
        Exception? primary = null;
        try
        {
            var result = await InvokeAsync("command-input.tool", operation, new()
            { ["callbackId"] = callbackId, ["eventJson"] = arguments.ToString(), ["toolCallId"] = actual.ToolCallId,
                ["uiCapabilities"] = Capabilities(context) }, context, cancellationToken).ConfigureAwait(false);
            RequirePublication(result);
            if (!result.Value.GetProperty("updateDeliveryJoined").GetBoolean()) throw new InvalidOperationException("Node tool progress delivery not joined.");
            if (result.Value.GetProperty("resultPresence").GetString() != "json") throw new InvalidOperationException("Node tool returned no result; exact receipt retained.");
            return JsonData.Parse(result.Value.GetProperty("resultJson").GetString()!);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await RetireContextAsync(operation, primary).ConfigureAwait(false); }
    }
    private async ValueTask<JsonData> PrepareToolAsync(string callbackId, JsonData arguments, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync("command-input.prepare", NextOperation(), new()
        { ["callbackId"] = callbackId, ["argumentsJson"] = arguments.ToString() }, null, cancellationToken).ConfigureAwait(false);
        if (result.Value.GetProperty("status").GetString() != "fulfilled" || result.Value.GetProperty("hostCapabilitiesGranted").GetBoolean())
            throw new InvalidOperationException("Original initial tool validation rejected; exact source receipt retained.");
        var prepared = JsonData.Parse(result.Value.GetProperty("preparedJson").GetString()!);
        if (prepared.Value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Original tool preparation returned a non-object.");
        return prepared;
    }
    private ToolLoadoutChanges? PrepareLoadout(string callbackId, ToolLoadout loadout)
    {
        SnapshotIdentity identity;
        lock (gate)
        {
            if (!loadouts.TryGetValue(loadout, out identity!))
            {
                if (nextLoadout >= 128) throw new IOException("Node loadout snapshot identity budget.");
                identity = new("loadout-" + (++nextLoadout).ToString(CultureInfo.InvariantCulture));
                loadouts.Add(loadout, identity);
            }
        }
        var metadata = NodeToolLoadoutMetadata.Write(loadout, identity.Id);
        // Existing SDK preparation is synchronous and registry-leased. The pure request
        // grants no host handle; join its original reply/settlement before returning.
        var result = InvokeAsync("command-input.prepare", NextOperation(), new()
        { ["callbackId"] = callbackId, ["preparationKind"] = "loadout", ["loadoutJson"] = metadata.ToString() },
            null, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
        if (result.Value.GetProperty("status").GetString() != "fulfilled" ||
            result.Value.GetProperty("hostCapabilitiesGranted").GetBoolean() ||
            result.Value.GetProperty("preparationKind").GetString() != "loadout")
            throw new InvalidOperationException("Original loadout preparation rejected; exact source receipt retained.");
        return result.Value.GetProperty("resultPresence").GetString() switch
        {
            "undefined" when !result.Value.TryGetProperty("resultJson", out _) => null,
            "json" => NodeToolLoadoutMetadata.ReadChanges(JsonData.Parse(result.Value.GetProperty("resultJson").GetString()!)),
            _ => throw new InvalidOperationException("Unsupported Node loadout result presence.")
        };
    }
    private static void RequirePublication(JsonData result)
    {
        if (result.Value.GetProperty("status").GetString() != "fulfilled" || !result.Value.GetProperty("publicationJoined").GetBoolean())
            throw new InvalidOperationException("Original Commands/Input callback or actual UI publication failed; exact receipt retained.");
    }
    private string Borrow(IExtensionContext context, string? toolCallbackId = null, NativeComponentTransport? transport = null)
    {
        if (context.OwnerId != ownerId || context.OwnerGeneration != generation) throw new InvalidOperationException("Foreign Commands/Input native context.");
        var rendererOnly = transport is not null;
        transport ??= ComponentTransport(context);
        var id = NextOperation();
        var dialogs = rendererOnly ? null : new NativeDialogOperation(context, id);
        try
        {
            lock (gate)
            {
                var progress = toolCallbackId is null ? null : new NodeToolProgressDelivery(id, toolCallbackId,
                    context as IExtensionToolInvocationContext ?? throw new InvalidOperationException("Actual tool context required."));
                if (contexts.Count >= 8 || !contexts.TryAdd(id, new(context, progress, transport, dialogs)))
                    throw new InvalidOperationException("Commands/Input context admission limit.");
                return id;
            }
        }
        catch (Exception acquisition)
        {
            if (dialogs is null) throw;
            var original = dialogs.CloseAsync();
            try { original.GetAwaiter().GetResult(); }
            catch (Exception error) { throw new AggregateException(acquisition, original.Exception ?? error); }
            throw;
        }
    }
    private string NextOperation()
    { lock (gate) { if (cleanup is not null || next == long.MaxValue) throw new InvalidOperationException("Closed Commands/Input admission."); return "command-input-op-" + (++next).ToString(CultureInfo.InvariantCulture); } }
    private void RetireContext(string operation)
    {
        lock (gate)
        {
            if (contexts.TryGetValue(operation, out var borrowed)) borrowed.Progress?.Close();
            contexts.Remove(operation);
        }
    }
    private static object Capabilities(IExtensionContext context)
    {
        var offered = context is IExtensionUiContext ui ? ui.Ui.Capabilities : ExtensionUiCapabilities.NoUi;
        return new { mode = offered.Mode.ToString().ToLowerInvariant(), connectionGeneration = offered.ConnectionGeneration,
            sessionGeneration = offered.SessionGeneration, features = offered.Features.IsDefault ? Array.Empty<string>() : offered.Features.Select(feature =>
                feature.ToString().ToLowerInvariant()).ToArray() };
    }
    private async Task<JsonData> InvokeAsync(string method, string operation, Dictionary<string, object?> payload,
        IExtensionContext? context, CancellationToken cancellationToken, bool offerHostCallback = true)
    {
        var current = worker ?? throw new InvalidOperationException("Commands/Input worker unavailable.");
        if (context is not null) NodeSessionSnapshotMetadata.AddTo(payload, context);
        payload.Add("ownerId", ownerId); payload.Add("ownerGeneration", generation); payload.Add("operationId", operation);
        var request = Json(payload); if (Encoding.UTF8.GetByteCount(request.Json!.ToString()) > 262_144) throw new IOException("Commands/Input request byte budget.");
        BorrowedOperation? borrowed = null;
        if (context is not null) lock (gate)
        {
            if (!contexts.TryGetValue(operation, out borrowed) || !ReferenceEquals(borrowed.Context, context))
                throw new InvalidOperationException("Commands/Input worker call lost its owning context.");
        }
        if (borrowed?.Transport is { } componentTransport)
        {
            if (!payload.ContainsKey("scopeId")) payload.Add("scopeId", componentTransport.ScopeId);
            if (!payload.ContainsKey("sessionGeneration")) payload.Add("sessionGeneration", launch.SessionGeneration);
            if (!payload.ContainsKey("nativeSessionGeneration")) payload.Add("nativeSessionGeneration", componentTransport.SessionGeneration);
        }
        // Payload identity fields must be serialized after all admitted scope fields are added.
        request = Json(payload); if (Encoding.UTF8.GetByteCount(request.Json!.ToString()) > 262_144) throw new IOException("Commands/Input request byte budget.");
        Exception? primary = null; WorkerValue? response = null; WorkerCall? call = null; var admitted = false;
        try
        {
            call = current.StartRequest(method, request, context is null || !offerHostCallback ? null : uiHandle, cancellationToken); admitted = true;
            if (borrowed is not null && !borrowed.CallReady.TrySetResult(call)) throw new InvalidOperationException("Commands/Input worker call already bound.");
            response = await call.Result.ConfigureAwait(false);
        }
        catch (Exception error) { if (!admitted) borrowed?.CallReady.TrySetException(error); primary = error; }
        if (primary is not null && current.Snapshot.Stopped) StopNativeOpenOriginals();
        try
        {
            if (admitted && primary is not WorkerProtocolException { Outcome: WorkerOutcome.NotSent })
            {
                if (!current.Snapshot.Stopped)
                {
                    var settlement = Own(await current.RequestAsync("command-input.settle", Json(new { operationId = operation })).ConfigureAwait(false));
                    if (!settlement.Value.GetProperty("settled").GetBoolean()) throw new InvalidOperationException("Commands/Input actual source settlement fence missing.");
                    var written = await call!.CancellationWrite.ConfigureAwait(false);
                    object[] writes; Exception? writeFailure;
                    lock (gate) { writes = borrowed?.CancellationWrites.ToArray() ?? []; writeFailure = borrowed?.CancellationWriteFailure; }
                    var native = JsonSerializer.Serialize(new { callerCancellationRequested = cancellationToken.IsCancellationRequested,
                        cancellationWrite = written.ToString(), uiCancellationWrites = writes, primary = primary?.ToString(), cancellationWriteFailure = writeFailure?.ToString() });
                    if (Encoding.UTF8.GetByteCount(native) > 262_144) throw new IOException("Commands/Input native cancellation receipt budget.");
                    Retain(JsonData.Parse(settlement.ToString()[..^1] + ",\"nativeAdmission\":" + native + "}"));
                    if (writeFailure is not null) throw new IOException("Commands/Input UI cancellation write failed after source settlement.", writeFailure);
                }
                // A component request can be the original render/input/dispose awaited by an
                // actual native open callback owned by worker completion. Release that original
                // first; the external Node owner close joins the same completion/stop inventory.
                else if (method is not ("command-input.component-render" or "command-input.component-input" or "command-input.component-dispose"))
                    await current.Completion.ConfigureAwait(false);
            }
        }
        catch (Exception settlement) { primary = primary is null ? settlement : new AggregateException(primary, settlement); }
        Exception? dialogRetirementFault = null;
        if (borrowed?.Dialogs is { } dialogs)
        {
            Task? retirement = null;
            try { retirement = dialogs.CloseAsync(); await retirement.ConfigureAwait(false); }
            catch (Exception error)
            { dialogRetirementFault = retirement is null ? error : dialogs.CaptureRetirementFailure(retirement, error); }
        }
        if (dialogRetirementFault is not null)
            throw primary is null ? new AggregateException("Dialog retirement original failed.", dialogRetirementFault)
                : new AggregateException("Source and dialog retirement originals failed.", primary, dialogRetirementFault);
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException("Commands/Input caller cancelled after joined source settlement.", primary, cancellationToken);
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        cancellationToken.ThrowIfCancellationRequested(); return Own(response!);
    }
    private async ValueTask<WorkerValue> DispatchUiAsync(WorkerRequestContext request, CancellationToken cancellationToken)
    {
        if (request.Method is not ("ui.notify" or "ui.select" or "ui.confirm" or "ui.input" or "ui.editor" or
            "ui.dialog.reserve" or "ui.dialog.cancel" or "ui.dialog.retire" or "tool.update" or
            "ui.custom.open" or "ui.custom.done" or "ui.component.invalidate") || request.Value.Presence != WorkerValuePresence.Json)
            throw new InvalidOperationException("Unsupported Commands/Input host callback.");
        var value = request.Value.Json!.Value; BorrowedOperation borrowed;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("operationId", out var id) || id.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Malformed Commands/Input UI callback.");
        PersistentToolParent? persistent;
        lock (gate)
        {
            if (cleanup is not null) throw new InvalidOperationException("Closed Commands/Input owner.");
            persistentToolParents.TryGetValue(id.GetString()!, out persistent);
        }
        if (persistent is not null)
            return await DispatchPersistentToolSignalAsync(request.Method, value, persistent, cancellationToken).ConfigureAwait(false);
        lock (gate) { if (!contexts.TryGetValue(id.GetString()!, out borrowed!)) throw new InvalidOperationException("Stale Commands/Input UI context."); }
        var context = borrowed.Context;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.OperationCancellationToken,
            context.SessionCancellationToken, context.ExtensionLifetimeCancellationToken);
        try
        {
            if (request.Method is "ui.dialog.reserve" or "ui.dialog.cancel" or "ui.dialog.retire" ||
                (request.Method is "ui.select" or "ui.confirm" or "ui.input") && value.TryGetProperty("dialogId", out _))
                return await DispatchDialogAsync(request.Method, value, borrowed, stop.Token).ConfigureAwait(false);
            if (request.Method is "ui.custom.open" or "ui.custom.done" or "ui.component.invalidate")
                return await DispatchComponentUiAsync(request.Method, value, borrowed, stop.Token).ConfigureAwait(false);
            if (request.Method == "tool.update")
            {
                var progress = borrowed.Progress ?? throw new InvalidOperationException("Progress is unavailable outside its tool invocation.");
                var sequence = await progress.DeliverAsync(value, stop.Token).ConfigureAwait(false);
                return Json(new { delivered = true, sequence });
            }
            return await DispatchOwnedUiAsync(request.Method, value, context, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancellation) when (context.OperationCancellationToken.IsCancellationRequested ||
            context.SessionCancellationToken.IsCancellationRequested || context.ExtensionLifetimeCancellationToken.IsCancellationRequested)
        {
            try
            {
                // Real UI cancellation cannot retire the source request before its own physical Cancel write.
                var call = await borrowed.CallReady.Task.ConfigureAwait(false);
                var disposition = await call.CancellationWrite.ConfigureAwait(false);
                lock (gate)
                {
                    if (borrowed.CancellationWrites.Count >= 16) throw new IOException("Commands/Input cancellation write receipt budget.");
                    borrowed.CancellationWrites.Add(new { method = request.Method, disposition = disposition.ToString(),
                        operationCancellationRequested = context.OperationCancellationToken.IsCancellationRequested,
                        sessionCancellationRequested = context.SessionCancellationToken.IsCancellationRequested,
                        extensionCancellationRequested = context.ExtensionLifetimeCancellationToken.IsCancellationRequested });
                }
                if (disposition != WorkerCancellationWriteDisposition.Written)
                    throw new IOException("Active Commands/Input UI callback has no physically written owning Cancel.");
            }
            catch (Exception failure)
            {
                lock (gate) borrowed.CancellationWriteFailure ??= failure;
                throw new AggregateException("Commands/Input cancellation and its owning write fence failed.", cancellation, failure);
            }
            throw;
        }
    }
    private static async ValueTask<WorkerValue> DispatchOwnedUiAsync(string method, JsonElement value,
        IExtensionContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var ui = (context as IExtensionUiContext)?.Ui;
        if (method == "ui.notify")
        {
            Exact(value, "operationId", "message", "kind");
            var message = value.GetProperty("message").GetString()!; if (message.Length > 65_536 || ui is null ||
                !ui.Capabilities.Supports(ExtensionUiFeature.Notify) && ui.Capabilities.Mode is not (ExtensionUiMode.Print or ExtensionUiMode.Json))
                throw new InvalidOperationException("Native notify capability unavailable.");
            var kind = value.GetProperty("kind").GetString() switch
            { "info" => ExtensionUiNotifyKind.Info, "warning" => ExtensionUiNotifyKind.Warning, "error" => ExtensionUiNotifyKind.Error, _ => throw new InvalidOperationException("Invalid notification kind.") };
            var outcome = await ui.PublishAsync(new ExtensionUiNotify(message, kind), token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (outcome.Kind == ExtensionUiOutcomeKind.Unavailable && outcome.UnavailableReason == ExtensionUiUnavailableReason.NoUi &&
                ui.Capabilities.Mode is ExtensionUiMode.Print or ExtensionUiMode.Json)
                return Json(new { published = false, outcome = "unavailable", reason = "NoUi" });
            if (outcome.Kind != ExtensionUiOutcomeKind.Value || outcome.Value != ExtensionUiPublication.Published) throw new InvalidOperationException("Actual native notification did not publish.");
            return Json(new { published = true });
        }
        Exact(value, "operationId", "suppliedArgumentsJson", "optionsPresent");
        return await OriginalUiDialogBroker.InvokeAsync(method,
            JsonData.Parse(value.GetProperty("suppliedArgumentsJson").GetString()!),
            value.GetProperty("optionsPresent").GetBoolean(), ui, token).ConfigureAwait(false);
    }
    private static WorkerValue Outcome<T>(ExtensionUiOutcome<T> outcome)
    {
        var value = new Dictionary<string, object?>
        {
            ["outcome"] = outcome.Kind switch { ExtensionUiOutcomeKind.Value => "value", ExtensionUiOutcomeKind.Cancelled => "cancelled", ExtensionUiOutcomeKind.TimedOut => "timedOut", _ => "unavailable" },
            ["presence"] = outcome.Kind == ExtensionUiOutcomeKind.Value ? "json" : "undefined"
        };
        if (outcome.Kind == ExtensionUiOutcomeKind.Value) value.Add("value", outcome.Value);
        if (outcome.UnavailableReason is { } reason) value.Add("reason", reason.ToString()); return Json(value);
    }
    private static void Exact(JsonElement value, params string[] names)
    { if (!value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal))) throw new InvalidOperationException("UI callback fields differ."); }
    private void Retain(JsonData value)
    { var bytes = Encoding.UTF8.GetByteCount(value.ToString()); lock (gate) { if (observations.Count >= 128 || bytes > 8_388_608 - retainedBytes) throw new IOException("Commands/Input retained operation budget."); retainedBytes += bytes; observations.Add(value); } }
    private static WorkerValue Json<T>(T value) => WorkerValue.FromJson(JsonData.Parse(JsonSerializer.Serialize(value)));
    private static JsonData Own(WorkerValue value)
    { if (value.Presence != WorkerValuePresence.Json || value.Json is null || Encoding.UTF8.GetByteCount(value.Json.ToString()) > 524_288) throw new IOException("Commands/Input response budget."); return JsonData.FromElement(value.Json.Value); }
    public ValueTask DisposeAsync()
    {
        NativeDialogOperation[] dialogs;
        lock (gate) dialogs = contexts.Values.Select(value => value.Dialogs).OfType<NativeDialogOperation>().ToArray();
        foreach (var dialog in dialogs) dialog.AssertExternalObservation();
        lock (gate) return new(cleanup ??= CloseAsync());
    }
    private async Task CloseAsync()
    {
        if (worker is null) return; var failures = new List<Exception>();
        NativeDialogOperation[] dialogs;
        lock (gate) dialogs = contexts.Values.Select(value => value.Dialogs).OfType<NativeDialogOperation>().ToArray();
        var retirements = new List<Task>();
        foreach (var dialog in dialogs)
            try { retirements.Add(dialog.CloseAsync()); } catch (Exception error) { failures.Add(error); }
        foreach (var original in retirements)
            try { await original.ConfigureAwait(false); } catch (Exception error) { failures.Add(original.Exception ?? error); }
        try { await JoinNativeOpenOriginalsAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try
        {
            lock (gate) { if (contexts.Count != 0) throw new InvalidOperationException("Commands/Input callbacks have not joined."); }
            worker.RevokeOwner(ownerId!, generation);
            if (!worker.Snapshot.Stopped) SourceFinalizationReport = Own(await worker.RequestAsync("command-input.finalize", WorkerValue.FromJson(JsonData.EmptyObject)).ConfigureAwait(false));
        }
        catch (Exception error) { failures.Add(error); }
        try { await worker.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { qualificationSource?.VerifyImmutable(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Commands/Input source/worker cleanup failed.", failures);
    }
}
