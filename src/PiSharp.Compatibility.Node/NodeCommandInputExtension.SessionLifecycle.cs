using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;

namespace PiSharp.Compatibility.Node;

public sealed partial class NodeCommandInputExtension
{
    private void RegisterSessionLifecycleHandlers(IExtensionRegistry registry, JsonElement source)
    {
        if (!source.TryGetProperty("sessionHandlers", out var handlers)) return;
        foreach (var handler in handlers.EnumerateArray())
        {
            var id = handler.GetProperty("callbackId").GetString()!;
            var topic = handler.GetProperty("topic").GetString()!;
            registry.Observe(new(id, topic, (observation, context, token) =>
                InvokeSessionEventAsync(id, topic, observation, context, token)));
        }
    }
    private async ValueTask InvokeSessionEventAsync(string callbackId, string topic, JsonData observation,
        IExtensionContext context, CancellationToken token)
    {
        if (topic is not ("session_start" or "session_tree") ||
            observation.Value.ValueKind != JsonValueKind.Object ||
            !observation.Value.TryGetProperty("type", out var type) || type.GetString() != topic)
            throw new InvalidOperationException("Actual native lifecycle topic and observation must agree.");
        var operation = Borrow(context);
        Exception? primary = null;
        try
        {
            // Registry observation admission owns this original and its context until settlement.
            var original = InvokeAsync("command-input.session-event", operation, new()
            {
                ["callbackId"] = callbackId, ["eventJson"] = observation.ToString(),
                ["uiCapabilities"] = Capabilities(context)
            }, context, token);
            JsonData result;
            try { result = await original.ConfigureAwait(false); }
            catch (Exception direct) when (original.IsFaulted)
            { throw new NodeSessionLifecycleOriginalException(original, original.Exception!, direct); }
            RequirePublication(result);
            if (result.Value.GetProperty("resultPresence").GetString() != "undefined")
                throw new InvalidOperationException("Original session observation returned an unsupported replacement.");
        }
        catch (Exception error) { primary = error; throw; }
        finally { await RetireContextAsync(operation, primary).ConfigureAwait(false); }
    }
}

public sealed class NodeSessionLifecycleOriginalException(Task original, AggregateException evidence, Exception direct)
    : IOException("Actual Node lifecycle callback original failed.", evidence)
{
    public Task Original { get; } = original;
    public AggregateException Evidence { get; } = evidence;
    public Exception Direct { get; } = direct;
}
