// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (_replaceMessageInPlace).
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Agent;

/// <summary>Source _replaceMessageInPlace: the primary event sink (message_end handlers) may replace a finalized message while its
/// end event is delivered. The running loop, the agent's history, later requests and the turn and agent end events then use the
/// replacement, as the source's in-place mutation does. Keyed by the end event's identity.</summary>
public static class AgentMessageReplacement
{
    private static readonly ConditionalWeakTable<AgentEvent, TranscriptEntry> Replacements = new();

    /// <summary>Records <paramref name="replacement"/> for an <see cref="AgentLoopInputMessageEnded"/>, <see cref="AssistantMessageEnded"/>
    /// or <see cref="ToolResultMessageEnded"/> event; the replacement keeps the message's role.</summary>
    public static void Set(AgentEvent ended, TranscriptEntry replacement)
    {
        ArgumentNullException.ThrowIfNull(ended); ArgumentNullException.ThrowIfNull(replacement);
        var role = Role(ended) ?? throw new ArgumentException("Only message end events carry a replacement.", nameof(ended));
        if (replacement.Role != role || replacement.WireBody is not { Value.ValueKind: JsonValueKind.Object } body ||
            !body.Value.TryGetProperty("role", out var wireRole) || wireRole.ValueKind != JsonValueKind.String || wireRole.GetString() != role)
            throw new ArgumentException("A replacement keeps the message's role.", nameof(replacement));
        if (role == "assistant") _ = PiWireJson.ReadMessage(body.Value);
        Replacements.AddOrUpdate(ended, replacement);
    }

    /// <summary>The replacement recorded for <paramref name="ended"/>, or null when the message was kept.</summary>
    public static TranscriptEntry? Get(AgentEvent ended) => Replacements.TryGetValue(ended, out var replacement) ? replacement : null;

    internal static string? Role(AgentEvent ended) => ended switch
    {
        AgentLoopInputMessageEnded input => input.Message.Role,
        AssistantMessageEnded => "assistant",
        ToolResultMessageEnded => "toolResult",
        _ => null
    };
}
