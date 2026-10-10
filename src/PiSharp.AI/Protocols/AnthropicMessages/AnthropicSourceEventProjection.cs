using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

/// <summary>Explicit Anthropic source terminal shape; native diagnostic metadata remains on the original frame.</summary>
public static class AnthropicSourceEventProjection
{
    public static JsonData WriteTerminal(StreamTerminalEvent terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var source = PiWireJson.WriteSourceEvent(terminal);
        if (terminal is not StreamError) return source;
        // Native Anthropic failure producers own this exact message-level field.
        // Do not filter generic extension fields, nested data, or sanitized errorMessage.
        var node = JsonNode.Parse(source.ToString(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions)!;
        ((JsonObject)node["error"]!).Remove("anthropicFailure");
        return JsonData.Parse(node.ToJsonString());
    }
}
