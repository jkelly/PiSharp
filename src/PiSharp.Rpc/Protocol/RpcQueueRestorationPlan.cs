using System.Text.Json;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;

namespace PiSharp.Rpc.Protocol;

// Preflight contains no mutation. Only the core revision-bound compare-and-clear
// can turn this plan into a removal receipt; a mismatch is never an unconditional clear.
internal sealed record RpcQueueRestorationPlan(AgentPendingInputQueueSnapshot Expected,
    AgentPendingInputQueueSnapshot Cleared, JsonData QueueEvent, JsonData Response)
{
    internal static RpcQueueRestorationPlan Capture(PersistentAgentSession session,
        RpcCommandEnvelope command, long generation, RpcDispatchOptions options, CancellationToken token)
    {
        var expected = session.GetPendingInputQueueSnapshot(token);
        var currentText = command.Message!;
        ValidateText(currentText);
        var count = checked(expected.SteeringMessages.Length + expected.FollowUpMessages.Length);
        if (count > 512) throw Error("Queue restoration exceeds the bounded message count.");
        var texts = new List<string>(count); var length = 0;
        foreach (var entry in expected.SteeringMessages.Concat(expected.FollowUpMessages))
        {
            token.ThrowIfCancellationRequested();
            if (entry.Role != "user" || !entry.WireBody.Value.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String && (content.ValueKind != JsonValueKind.Array ||
                content.EnumerateArray().Any(block => block.ValueKind != JsonValueKind.Object ||
                    !block.TryGetProperty("type", out var type) || type.GetString() != "text" ||
                    !block.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String)))
                throw Error("Only text-only user messages can be restored into this editor.");
            var text = RpcCommandCodec.Text(entry); ValidateText(text);
            var separator = texts.Count == 0 ? 0 : 2;
            if (text.Length > 65_536 - length - separator)
                throw Error("Queued text exceeds the editor capacity; no messages were removed.");
            length += separator + text.Length; texts.Add(text);
        }
        var queuedText = string.Join("\n\n", texts);
        var hasQueueText = RpcCommandCodec.TrimSource(queuedText).Length != 0;
        var hasCurrentText = RpcCommandCodec.TrimSource(currentText).Length != 0;
        var combinedLength = (hasQueueText ? queuedText.Length : 0) + (hasCurrentText ? currentText.Length : 0) +
            (hasQueueText && hasCurrentText ? 2 : 0);
        if (combinedLength > 65_536) throw Error("Queued text and draft exceed the editor capacity; no messages were removed.");
        // Source leaves an empty queue's editor untouched, including all whitespace and cursor state.
        var combined = count == 0 ? currentText : string.Join("\n\n",
            new[] { queuedText, currentText }.Where(text => RpcCommandCodec.TrimSource(text).Length != 0));
        var data = RpcCommandCodec.Build(writer =>
        {
            writer.WriteNumber("generation", generation); writer.WriteNumber("count", count); writer.WriteString("text", combined);
        }, options.MaximumOutputBytes);
        var response = RpcCommandCodec.Success(command, data, options);
        var cleared = new AgentPendingInputQueueSnapshot([], [], expected.SteeringMode, expected.FollowUpMode);
        var queueEvent = RpcCommandCodec.Event("queue_update", writer => RpcCommandCodec.Queue(writer, cleared), options);
        token.ThrowIfCancellationRequested();
        return new(expected, cleared, queueEvent, response);

        RpcCommandException Error(string message) => new(command.Id, command.Type, message);
        void ValidateText(string text)
        {
            if (text.Length > 65_536) throw Error("Queue restoration exceeds the editor capacity.");
            for (var i = 0; i < text.Length; i++)
                if (char.IsHighSurrogate(text[i]))
                { if (++i == text.Length || !char.IsLowSurrogate(text[i])) throw Error("Queue text contains invalid UTF-16."); }
                else if (char.IsLowSurrogate(text[i])) throw Error("Queue text contains invalid UTF-16.");
        }
    }

    internal bool TryCommit(PersistentAgentSession session,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AgentPendingInputQueueSnapshot? removed, CancellationToken token) =>
        session.TryClearPendingInputQueues(Expected, out removed, token);
}
