using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Protocol;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Compatibility.Node;

public sealed partial class NodeCommandInputExtension
{
    public sealed record OriginalToolRenderer(string ToolCallbackId, string ToolName, string SourcePath,
        bool HasRenderCall, bool HasRenderResult);
    private readonly Dictionary<string, OriginalToolRenderer> originalRenderers = new(StringComparer.Ordinal);
    private readonly Dictionary<long, NativeComponentTransport> componentTransports = [];
    private readonly Dictionary<string, OpenComponent> openComponents = new(StringComparer.Ordinal);
    private readonly List<Task> nativeOpenOriginals = [];
    private readonly List<Task> nativeOpenCancellationOriginals = [];
    private long nextTransport;
    private int transportReservations;
    private readonly Dictionary<string, RenderedComponent> liveToolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RenderedComponent> liveToolResults = new(StringComparer.Ordinal);
    private sealed record PersistentToolParent(NativeComponentTransport Transport, IExtensionToolComponentPresentation Presentation);
    private readonly Dictionary<string, PersistentToolParent> persistentToolParents = new(StringComparer.Ordinal);
    public ImmutableArray<OriginalToolRenderer> OriginalRenderers
    { get { lock (gate) return originalRenderers.Values.ToImmutableArray(); } }

    private void RetainOriginalRenderers(JsonElement tool)
    {
        bool Offered(string key) => tool.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
        var call = Offered("hasRenderCall"); var result = Offered("hasRenderResult");
        if (!call && !result) return;
        var row = new OriginalToolRenderer(tool.GetProperty("callbackId").GetString()!, tool.GetProperty("name").GetString()!,
            tool.GetProperty("sourcePath").GetString()!, call, result);
        lock (gate) { if (originalRenderers.Count >= 64 || !originalRenderers.TryAdd(row.ToolCallbackId, row)) throw new InvalidOperationException("Original renderer identity/capacity differs."); }
    }
    private NativeComponentTransport? ComponentTransport(IExtensionContext context)
    {
        var ui = (context as IExtensionUiContext)?.Ui;
        if (ui is not IExtensionCustomComponentUi || !ui.Capabilities.Supports(ExtensionUiFeature.CustomTerminalComponent)) return null;
        var actualGeneration = ui.Capabilities.SessionGeneration;
        if (actualGeneration <= 0 || ui.Capabilities.Mode != ExtensionUiMode.Tui)
            throw new InvalidOperationException("Actual native custom terminal session capability required.");
        string id;
        lock (gate)
        {
            if (componentTransports.TryGetValue(actualGeneration, out var existing)) return existing;
            if (componentTransports.Count + transportReservations >= 256) throw new InvalidOperationException("Native component transport session budget.");
            id = "node-ui-transport-" + checked(++nextTransport); transportReservations++;
        }
        var transport = new NativeComponentTransport(actualGeneration);
        try
        {
            // Bind can synchronously run actual owner cancellation and join cleanup. Never hold
            // the Node table gate while acquiring a real cancellation registration.
            transport.Participant = ExtensionRegistry.BindCustomComponentContext(context, id, actualGeneration,
                () => ui.Capabilities.SessionGeneration, () => Task.CompletedTask,
                fresh => CloseRenderedComponentsAsync(transport, fresh), context.SessionCancellationToken);
            NativeComponentTransport selected;
            lock (gate)
            {
                if (!componentTransports.TryGetValue(actualGeneration, out selected!))
                { componentTransports.Add(actualGeneration, transport); selected = transport; }
            }
            if (!ReferenceEquals(selected, transport))
            {
                var close = transport.Participant.DisposeAsync().AsTask();
                try { close.GetAwaiter().GetResult(); }
                catch (Exception error) { throw new AggregateException("Unpublished transport close failed.", close.Exception ?? error); }
            }
            return selected;
        }
        finally { lock (gate) transportReservations--; }
    }
    internal sealed class NativeComponentTransport(long sessionGeneration)
    {
        internal long SessionGeneration { get; } = sessionGeneration;
        internal ExtensionRegistry.RegisteredExtensionComponent Participant = null!;
        internal string ScopeId => Participant.ScopeId;
        internal List<RenderedComponent> Rendered { get; } = [];
    }
    private sealed class OpenComponent(ExtensionCustomComponentIdentity identity, NativeComponentTransport transport,
        IExtensionCustomComponentUi ui)
    {
        internal ExtensionCustomComponentIdentity Identity { get; } = identity;
        internal NativeComponentTransport Transport { get; } = transport;
        internal IExtensionCustomComponentUi Ui { get; } = ui;
        internal NativeComponentOpenLifetime Lifetime { get; } = new(transport.Participant);
    }
    private async Task<WorkerValue> DispatchComponentUiAsync(string method, JsonElement value,
        BorrowedOperation borrowed, CancellationToken token)
    {
        Exact(value, "ownerId", "ownerGeneration", "sessionGeneration", "nativeSessionGeneration", "operationId", "scopeId", "componentId");
        var transport = borrowed.Transport ?? throw new InvalidOperationException("No actual native component transport participant.");
        var componentId = value.GetProperty("componentId").GetString();
        if (value.GetProperty("ownerId").GetString() != ownerId || value.GetProperty("ownerGeneration").GetInt64() != generation ||
            value.GetProperty("scopeId").GetString() != transport.ScopeId ||
            value.GetProperty("sessionGeneration").GetInt64() != launch.SessionGeneration ||
            value.GetProperty("nativeSessionGeneration").GetInt64() != transport.SessionGeneration ||
            componentId is null || componentId.Length is < 1 or > 128)
            throw new InvalidOperationException("Component callback owner/generation/scope identity differs.");
        var identity = new ExtensionCustomComponentIdentity(ownerId!, generation, transport.SessionGeneration, componentId);
        var key = transport.ScopeId + ":" + componentId;
        // A fresh admission/frame owns the actual UI callback, not the captured context token alone.
        return await transport.Participant.InvokeAsync(async (fresh, actualToken) =>
        {
            if (method != "ui.custom.open")
            {
                OpenComponent signalOwner;
                lock (gate) if (!openComponents.TryGetValue(key, out signalOwner!) || signalOwner.Identity != identity ||
                    !ReferenceEquals(signalOwner.Transport, transport)) throw new InvalidOperationException("Stale custom component signal.");
                Task original = method == "ui.custom.done"
                    ? signalOwner.Ui.SignalCustomComponentDoneAsync(identity, actualToken)
                    : signalOwner.Ui.InvalidateCustomComponentAsync(identity, actualToken);
                try { await original.ConfigureAwait(false); }
                catch (Exception error) { throw new AggregateException("Native custom signal original failed.", original.Exception ?? error); }
                return Json(new { acknowledged = true });
            }
            var ui = (fresh as IExtensionUiContext)?.Ui;
            if (ui is not IExtensionCustomComponentUi custom || ui.Capabilities.Mode != ExtensionUiMode.Tui ||
                !ui.Capabilities.Supports(ExtensionUiFeature.CustomTerminalComponent))
                throw new InvalidOperationException("Actual native custom terminal broker unavailable.");
            var opened = new OpenComponent(identity, transport, custom);
            try
            {
                lock (gate)
                {
                    if (openComponents.Count >= 16 || nativeOpenOriginals.Count + openComponents.Count >= 960 || !openComponents.TryAdd(key, opened))
                        throw new InvalidOperationException("Duplicate/over-capacity custom component.");
                }
            }
            catch (Exception error)
            {
                Task? cleanup = null; try { cleanup = opened.Lifetime.RetireAndJoinStopAsync(); await cleanup.ConfigureAwait(false); } catch (Exception cleanupError) { throw new AggregateException(error, cleanup?.Exception ?? cleanupError); }
                throw;
            }
            Task? originalOpen = null;
            var openFaults = new List<Exception>();
            try
            {
                var callbacks = new ExtensionCustomComponentCallbacks(identity,
                    (width, renderContext, renderToken) => ComponentRowsAsync(transport, componentId, width, renderContext, renderToken),
                    (data, inputContext, inputToken) => ComponentInputAsync(transport, componentId, data, inputContext, inputToken),
                    disposeContext => ComponentDisposeAsync(transport, componentId, disposeContext));
                using var stopped = CancellationTokenSource.CreateLinkedTokenSource(actualToken, opened.Lifetime.Token);
                originalOpen = custom.OpenCustomComponentAsync(callbacks, stopped.Token);
                lock (gate) nativeOpenOriginals.Add(originalOpen);
                await originalOpen.ConfigureAwait(false);
            }
            catch (Exception error) { openFaults.Add(originalOpen?.Exception ?? error); }
            finally
            {
                lock (gate) openComponents.Remove(key);
                Task? retirement = null;
                try { retirement = opened.Lifetime.RetireAndJoinStopAsync(); await retirement.ConfigureAwait(false); }
                catch (Exception error) { openFaults.Add(retirement?.Exception ?? error); }

            }
            if (openFaults.Count != 0) throw new AggregateException("Native custom open/stop originals failed.", openFaults);
            return Json(new { closed = true });
        }, token).ConfigureAwait(false);
    }

    private void StopNativeOpenOriginals()
    {
        lock (gate)
        {
            foreach (var opened in openComponents.Values)
            {
                if (opened.Lifetime.StopOriginal is not null) continue;
                // RequestStop is a signal; the actual open finally joins its exact original.
                nativeOpenCancellationOriginals.Add(opened.Lifetime.RequestStop());
            }
        }
    }
    private async Task JoinNativeOpenOriginalsAsync()
    {
        StopNativeOpenOriginals();
        Task[] originals; lock (gate) originals = nativeOpenOriginals.Concat(nativeOpenCancellationOriginals).ToArray();
        var faults = new List<Exception>();
        foreach (var original in new HashSet<Task>(originals, ReferenceEqualityComparer.Instance))
            try { await original.ConfigureAwait(false); } catch (Exception error) { faults.Add(original.Exception ?? error); }
        if (faults.Count != 0) throw new AggregateException("Actual native custom open/stop originals failed.", faults);
    }

    private async Task<WorkerValue> DispatchPersistentToolSignalAsync(string method, JsonElement value,
        PersistentToolParent parent, CancellationToken token)
    {
        if (method != "ui.component.invalidate") throw new InvalidOperationException("Persistent tool parent grants only its original invalidation signal.");
        Exact(value, "ownerId", "ownerGeneration", "sessionGeneration", "nativeSessionGeneration", "operationId", "scopeId", "componentId");
        var transport = parent.Transport;
        var presentation = parent.Presentation;
        var expected = presentation.Identity;
        lock (gate)
            if (!transport.Rendered.Any(row => row.ComponentId == expected.ComponentId && ReferenceEquals(row.Presentation, presentation)))
                throw new InvalidOperationException("Retired original tool component signal.");
        if (value.GetProperty("ownerId").GetString() != expected.OwnerId || value.GetProperty("ownerGeneration").GetInt64() != expected.OwnerGeneration ||
            value.GetProperty("scopeId").GetString() != transport.ScopeId || value.GetProperty("sessionGeneration").GetInt64() != launch.SessionGeneration ||
            value.GetProperty("nativeSessionGeneration").GetInt64() != expected.SessionGeneration ||
            value.GetProperty("componentId").GetString() != expected.ComponentId)
            throw new InvalidOperationException("Original persistent tool signal identity differs.");
        // The persistent lease owns a live registry entry and admits a fresh callback frame. The
        // creating context/UI lease is already disposed and supplies no authority or token here.
        Task? original = null;
        try { original = presentation.InvalidateAsync(token); await original.ConfigureAwait(false); }
        catch (Exception error) { throw new AggregateException("Original persistent tool signal failed.", original?.Exception ?? error); }
        return Json(new { acknowledged = true });
    }

    private async Task<JsonData> ComponentCallAsync(string method, NativeComponentTransport transport,
        string componentId, IExtensionContext context, Dictionary<string, object?> payload, CancellationToken token)
    {
        var operation = Borrow(context, transport: transport);
        try
        {
            payload.Add("scopeId", transport.ScopeId); payload.Add("sessionGeneration", launch.SessionGeneration);
            payload.Add("nativeSessionGeneration", transport.SessionGeneration);
            payload.Add("componentId", componentId);
            return await InvokeAsync(method, operation, payload, context, token, offerHostCallback: false).ConfigureAwait(false);
        }
        finally { RetireContext(operation); }
    }
    private async Task<ExtensionCustomComponentRows> ComponentRowsAsync(NativeComponentTransport transport,
        string componentId, int width, IExtensionContext context, CancellationToken token)
    {
        if (width is < 1 or > 256) throw new InvalidOperationException("Native component width bound.");
        var result = await ComponentCallAsync("command-input.component-render", transport, componentId, context,
            new() { ["width"] = width }, token).ConfigureAwait(false);
        RequireComponentReceipt(result.Value, componentId);
        var source = result.Value.GetProperty("render");
        var rows = source.GetProperty("rows"); var widths = source.GetProperty("cellWidths");
        if (rows.ValueKind != JsonValueKind.Array || widths.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 256 ||
            rows.GetArrayLength() != widths.GetArrayLength()) throw new InvalidOperationException("Original component row/width projection differs.");
        var text = rows.EnumerateArray().Select(row => row.GetString() ?? throw new InvalidOperationException("Original render string required.")).ToImmutableArray();
        var cells = widths.EnumerateArray().Select(cell => cell.GetInt32()).ToImmutableArray();
        if (text.Sum(row => (long)row.Length) > 65536 || cells.Any(cell => cell < 0 || cell > width)) throw new InvalidOperationException("Original component projection bound.");
        return new(text, cells);
    }
    private async Task ComponentInputAsync(NativeComponentTransport transport, string componentId, string data,
        IExtensionContext context, CancellationToken token)
    {
        if (data.Length > 65536) throw new InvalidOperationException("Original component input bound.");
        var result = await ComponentCallAsync("command-input.component-input", transport, componentId, context,
            new() { ["data"] = data }, token).ConfigureAwait(false);
        RequireComponentReceipt(result.Value, componentId);
    }
    private async Task ComponentDisposeAsync(NativeComponentTransport transport, string componentId, IExtensionContext context)
    {
        var result = await ComponentCallAsync("command-input.component-dispose", transport, componentId, context, new(), CancellationToken.None).ConfigureAwait(false);
        RequireComponentReceipt(result.Value, componentId);
    }

    private static void RequireComponentReceipt(JsonElement value, string componentId)
    {
        if (value.GetProperty("status").GetString() != "fulfilled" || value.GetProperty("componentId").GetString() != componentId)
            throw new InvalidOperationException("Original component source observation/identity differs.");
    }

    public Task<RenderedComponent> RenderToolCallAsync(string toolCallbackId, string toolCallId, JsonData arguments,
        JsonData renderMetadata, IExtensionContext context, CancellationToken token = default) =>
        CreateRenderedComponentAsync(toolCallbackId, toolCallId, arguments, null, renderMetadata, context, false, token);
    public Task<RenderedComponent> RenderToolResultAsync(string toolCallbackId, string toolCallId, JsonData result,
        JsonData options, JsonData renderMetadata, IExtensionContext context, CancellationToken token = default) =>
        CreateRenderedComponentAsync(toolCallbackId, toolCallId, result, options, renderMetadata, context, true, token);
    private async Task<RenderedComponent> CreateRenderedComponentAsync(string toolId, string toolCallId, JsonData data,
        JsonData? options, JsonData metadata, IExtensionContext context, bool result, CancellationToken token)
    {
        OriginalToolRenderer descriptor;
        lock (gate) if (!originalRenderers.TryGetValue(toolId, out descriptor!)) throw new InvalidOperationException("No original retained tool renderer.");
        if (!(result ? descriptor.HasRenderResult : descriptor.HasRenderCall) || toolCallId.Length is < 1 or > 128 || metadata.Value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Original renderer callback/row identity required.");
        var transport = ComponentTransport(context) ?? throw new InvalidOperationException("Actual native renderer scope unavailable.");
        return await transport.Participant.InvokeAsync(async (fresh, actualToken) =>
        {
            var operation = Borrow(fresh, transport: transport);
            string? acquiredId = null;
            RenderedComponent? acquiredComponent = null;
            try
            {
                var renderContext = metadata.Value.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal);
                renderContext["toolCallId"] = toolCallId;
                var payload = new Dictionary<string, object?> { ["callbackId"] = descriptor.ToolCallbackId,
                    ["renderContextJson"] = JsonSerializer.Serialize(renderContext), ["sessionGeneration"] = launch.SessionGeneration,
                    ["nativeSessionGeneration"] = transport.SessionGeneration };
                payload.Add(result ? "resultJson" : "argumentsJson", data.ToString());
                if (result) payload.Add("optionsJson", options!.ToString());
                var receipt = await InvokeAsync(result ? "command-input.render-result" : "command-input.render-call",
                    operation, payload, fresh, actualToken).ConfigureAwait(false);
                if (receipt.Value.GetProperty("status").GetString() != "fulfilled") throw new InvalidOperationException("Original renderer creation rejected; source receipt retained.");
                var id = receipt.Value.GetProperty("componentId").GetString() ?? throw new InvalidOperationException("Original renderer component identity missing.");
                acquiredId = id;
                var component = new RenderedComponent(this, transport, id);
                acquiredComponent = component;
                component.Participant = ExtensionRegistry.BindCustomComponentContext(fresh, id, transport.SessionGeneration,
                    () => transport.Participant.CurrentSessionGeneration, () => Task.CompletedTask,
                    async disposeContext =>
                    {
                        Task? originalDisposal = null;
                        try
                        {
                            originalDisposal = component.Presentation is null ? ComponentDisposeAsync(transport, id, disposeContext)
                                : component.Presentation.DisposeAsync().AsTask();
                            await originalDisposal.ConfigureAwait(false);
                        }
                        catch (Exception error) { throw new AggregateException("Tool source/presentation disposal original failed.", originalDisposal?.Exception ?? error); }
                        finally
                        {
                            RetireContext(operation);
                            lock (gate)
                            {
                                persistentToolParents.Remove(operation);
                                transport.Rendered.Remove(component); foreach (var pair in liveToolCalls.Where(pair => ReferenceEquals(pair.Value, component)).ToArray()) liveToolCalls.Remove(pair.Key); foreach (var pair in liveToolResults.Where(pair => ReferenceEquals(pair.Value, component)).ToArray()) liveToolResults.Remove(pair.Key);
                            }
                        }
                    }, fresh.SessionCancellationToken);
                lock (gate) transport.Rendered.Add(component);
                var toolUi = (fresh as IExtensionUiContext)?.Ui as IExtensionToolComponentUi ??
                    throw new InvalidOperationException("Actual terminal tool row host required.");
                var identity = new ExtensionCustomComponentIdentity(ownerId!, generation, transport.SessionGeneration, id);
                component.Presentation = toolUi.AttachToolComponent(new(identity,
                    (width, renderContext, renderToken) => ComponentRowsAsync(transport, id, width, renderContext, renderToken),
                    (input, inputContext, inputToken) => ComponentInputAsync(transport, id, input, inputContext, inputToken),
                    disposeContext => ComponentDisposeAsync(transport, id, disposeContext)));
                // Publish the actual persistent lease before starting any original render that can
                // call context.invalidate(). Signal authority therefore survives create settlement.
                lock (gate)
                {
                    if (persistentToolParents.Count >= 16 || !persistentToolParents.TryAdd(operation, new(transport, component.Presentation)))
                        throw new InvalidOperationException("Persistent native tool signal capacity/identity differs.");
                }
                var originalShow = component.Presentation.ShowAsync(actualToken);
                try { await originalShow.ConfigureAwait(false); }
                catch (Exception error) { throw new AggregateException("Original tool initial presentation failed.", originalShow.Exception ?? error); }
                return component;
            }
            catch (Exception acquisitionError)
            {
                if (acquiredId is not null)
                {
                    Task? disposal = null;
                    try
                    {
                        disposal = acquiredComponent?.Participant is { } actualParticipant
                            ? actualParticipant.DisposeAsync().AsTask() : ComponentDisposeAsync(transport, acquiredId, fresh);
                        await disposal.ConfigureAwait(false);
                    }
                    catch (Exception cleanupError) { throw new AggregateException(acquisitionError, disposal?.Exception ?? cleanupError); }
                }
                throw;
            }
            finally { RetireContext(operation); }
        }, token).ConfigureAwait(false);
    }
    public sealed class RenderedComponent : IAsyncDisposable
    {
        private readonly NodeCommandInputExtension owner;
        private readonly NativeComponentTransport transport;
        public string ComponentId { get; }
        internal ExtensionRegistry.RegisteredExtensionComponent Participant = null!;
        internal IExtensionToolComponentPresentation? Presentation;
        internal RenderedComponent(NodeCommandInputExtension owner, NativeComponentTransport transport, string componentId)
        { this.owner = owner; this.transport = transport; ComponentId = componentId; }
        public Task<ExtensionCustomComponentRows> RenderAsync(int width, CancellationToken token = default) =>
            Participant.InvokeAsync((context, actual) => owner.ComponentRowsAsync(transport, ComponentId, width, context, actual), token);
        public ValueTask DisposeAsync() => Participant.DisposeAsync();
        public Task InvalidateAsync(CancellationToken token = default) =>
            Presentation?.InvalidateAsync(token) ?? throw new InvalidOperationException("No actual native tool presentation.");
        public Task JoinPaintsAsync() => Presentation?.JoinPaintsAsync() ?? throw new InvalidOperationException("No actual native tool presentation.");
    }
    private async Task CloseRenderedComponentsAsync(NativeComponentTransport transport, IExtensionContext context)
    {
        RenderedComponent[] rows; lock (gate) rows = transport.Rendered.ToArray();
        var originals = new List<Task>(); var faults = new List<Exception>();
        foreach (var row in rows)
            try { var close = row.DisposeAsync(); originals.Add(close.AsTask()); } catch (Exception error) { faults.Add(error); }
        foreach (var original in new HashSet<Task>(originals, ReferenceEqualityComparer.Instance))
            try { await original.ConfigureAwait(false); } catch (Exception error) { faults.Add(original.Exception ?? error); }
        lock (gate)
        {
            if (componentTransports.TryGetValue(transport.SessionGeneration, out var current) && ReferenceEquals(current, transport))
                componentTransports.Remove(transport.SessionGeneration);
        }
        if (faults.Count != 0) throw new AggregateException("Original renderer component retirement failed.", faults);
    }
}
