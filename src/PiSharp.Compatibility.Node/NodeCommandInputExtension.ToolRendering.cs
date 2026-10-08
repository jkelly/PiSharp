using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;

namespace PiSharp.Compatibility.Node;

public sealed partial class NodeCommandInputExtension
{
    public RenderedComponent? CaptureRenderedTool(string toolCallId, bool result)
    { lock (gate) return (result ? liveToolResults : liveToolCalls).GetValueOrDefault(toolCallId); }

    private void RegisterNativeToolRenderers(IExtensionRegistry registry, JsonElement descriptor)
    {
        var id = descriptor.GetProperty("callbackId").GetString()!;
        OriginalToolRenderer? renderer; lock (gate) originalRenderers.TryGetValue(id, out renderer);
        if (renderer is null) return;
        // These are host adapters for the retained original renderer, not fabricated source event
        // registrations. No source execute function or subprocess/filesystem effect is invoked.
        if (renderer.HasRenderCall)
            registry.RegisterToolCallHandler(new(id + "-native-render-call", async (snapshot, context, token) =>
            {
                if (snapshot.ToolName != renderer.ToolName || !HasActualToolRowHost(context)) return null;
                await ReplaceRenderedToolAsync(snapshot.ToolCallId, false, context, token,
                    () => RenderToolCallAsync(id, snapshot.ToolCallId, snapshot.Arguments, JsonData.EmptyObject, context, token)).ConfigureAwait(false);
                return null;
            }));
        if (renderer.HasRenderResult)
            registry.RegisterToolResultHandler(new(id + "-native-render-result", async (snapshot, context, token) =>
            {
                if (snapshot.ToolName != renderer.ToolName || !HasActualToolRowHost(context)) return null;
                await ReplaceRenderedToolAsync(snapshot.ToolCallId, true, context, token,
                    () => RenderToolResultAsync(id, snapshot.ToolCallId, snapshot.Result,
                        JsonData.Parse("{\"expanded\":false,\"isPartial\":false}"), JsonData.EmptyObject, context, token)).ConfigureAwait(false);
                return null;
            }));
    }
    private static bool HasActualToolRowHost(IExtensionContext context) => context is IExtensionUiContext ui &&
        ui.Ui is IExtensionToolComponentUi && ui.Ui.Capabilities.Mode == ExtensionUiMode.Tui &&
        ui.Ui.Capabilities.Supports(ExtensionUiFeature.CustomTerminalComponent);

    private async Task ReplaceRenderedToolAsync(string callId, bool result, IExtensionContext context,
        CancellationToken token, Func<Task<RenderedComponent>> create)
    {
        var table = result ? liveToolResults : liveToolCalls;
        RenderedComponent? previous; lock (gate) table.TryGetValue(callId, out previous);
        if (previous is not null)
        {
            var original = previous.DisposeAsync().AsTask();
            try { await original.ConfigureAwait(false); }
            catch (Exception error) { throw new AggregateException("Previous original tool row disposal failed.", original.Exception ?? error); }
            lock (gate) if (table.TryGetValue(callId, out var current) && ReferenceEquals(current, previous)) table.Remove(callId);
        }
        var acquired = create(); RenderedComponent? component = null;
        try
        {
            component = await acquired.ConfigureAwait(false);
            lock (gate)
            {
                if (table.Count >= 16 || !table.TryAdd(callId, component)) throw new InvalidOperationException("Native tool call row capacity/identity differs.");
            }
        }
        catch (Exception error)
        {
            if (component is null) throw new AggregateException("Original tool row acquisition failed.", acquired.Exception ?? error);
            Task? cleanup = null;
            try { cleanup = component.DisposeAsync().AsTask(); await cleanup.ConfigureAwait(false); }
            catch (Exception cleanupError) { throw new AggregateException(error, cleanup?.Exception ?? cleanupError); }
            throw;
        }
    }
}
