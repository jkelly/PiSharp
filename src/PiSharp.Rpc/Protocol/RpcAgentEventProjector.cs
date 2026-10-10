// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/docs/json.md.
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;

namespace PiSharp.Rpc.Protocol;

/// <summary>Canonical delta-only wire projection for the admitted native event profile.</summary>
public sealed class RpcAgentEventProjector
{
    private readonly RpcDispatchOptions options;
    private readonly Dictionary<string, ToolResultMessage> _toolStarts = new(StringComparer.Ordinal);
    private TokenUsage _usage = TokenUsage.Zero;
    private int _projecting;

    /// <summary>Owns projection state only; the immutable configuration and session snapshots are borrowed.</summary>
    public RpcAgentEventProjector(RpcDispatchOptions? options = null)
    {
        this.options = options ?? new();
        this.options.Validate(RpcSessionOwnership.Borrowed);
    }

    /// <summary>Projects one sequential observation. End messages must follow the session's durable primary sink.</summary>
    /// <param name="willRetry">Source agent_end willRetry, decided by the session (<see cref="PersistentAgentSession.WillRetryAfterAgentEnd"/>).</param>
    public ImmutableArray<JsonData> Project(AgentEvent observation, PersistentAgentSessionSnapshot snapshot, int historyLength, bool willRetry = false)
    {
        ArgumentNullException.ThrowIfNull(observation); ArgumentNullException.ThrowIfNull(snapshot);
        if (historyLength < 0) throw new ArgumentOutOfRangeException(nameof(historyLength));
        if (Interlocked.CompareExchange(ref _projecting, 1, 0) != 0)
            throw new InvalidOperationException("Concurrent event projection is unsupported.");
        try { return ProjectCore(observation, snapshot, historyLength, willRetry); }
        finally { Volatile.Write(ref _projecting, 0); }
    }

    private ImmutableArray<JsonData> ProjectCore(AgentEvent observation, PersistentAgentSessionSnapshot snapshot, int historyLength, bool willRetry)
    {
        JsonData Event(string kind, Action<Utf8JsonWriter>? fields = null) => RpcCommandCodec.Event(kind, fields, options);
        JsonData Message(string kind, JsonData body) => Event(kind, writer => RpcCommandCodec.Raw(writer, "message", body));
        JsonData End(string role)
        {
            if (snapshot.Context.Messages.IsEmpty || snapshot.Context.Messages[^1].Role != role)
                throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed);
            return snapshot.Context.Messages[^1].WireBody;
        }
        switch (observation)
        {
            case AgentLoopStarted:
                if (_toolStarts.Count != 0) throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed);
                return [Event("agent_start")];
            case AgentLoopTurnStarted: return [Event("turn_start")];
            case AgentLoopInputMessageStarted input: return [Message("message_start", input.Message.WireBody)];
            case AgentLoopInputMessageEnded input: return [Message("message_end", End(input.Message.Role))];
            case AssistantMessageStarted assistant:
                _usage = assistant.Message.Usage;
                return [Message("message_start", PiWireJson.WriteMessage(assistant.Message))];
            case AssistantMessageEnded: return [Message("message_end", End("assistant"))];
            case ToolResultMessageStarted start:
                if (_toolStarts.Count >= options.MaximumPendingToolMessages || !_toolStarts.TryAdd(start.Message.ToolCallId, start.Message))
                    throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed);
                return [];
            case ToolResultMessageEnded end:
            {
                if (!_toolStarts.Remove(end.Message.ToolCallId, out var pendingStart) || pendingStart != end.Message)
                    throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed);
                var body = End("toolResult");
                if (body.Value.GetProperty("toolCallId").GetString() != end.Message.ToolCallId ||
                    body.Value.GetProperty("toolName").GetString() != end.Message.ToolName)
                    throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed);
                // The native start value has no timestamp. Retain its position, then use the exact acknowledged body for both records.
                return [Message("message_start", body), Message("message_end", body)];
            }
            case ToolExecutionStarted tool: return [Event("tool_execution_start", writer =>
            {
                writer.WriteString("toolCallId", tool.Invocation.Call.Id); writer.WriteString("toolName", tool.Invocation.Call.Name);
                RpcCommandCodec.Raw(writer, "args", tool.Invocation.Call.Arguments);
            })];
            case ToolExecutionUpdated tool: return [Event("tool_execution_update", writer =>
            {
                writer.WriteString("toolCallId", tool.Invocation.Call.Id); writer.WriteString("toolName", tool.Invocation.Call.Name);
                RpcCommandCodec.Raw(writer, "args", tool.Invocation.Call.Arguments);
                RpcCommandCodec.Raw(writer, "partialResult", Result(tool.PartialResult));
            })];
            case ToolExecutionEnded tool: return [Event("tool_execution_end", writer =>
            {
                writer.WriteString("toolCallId", tool.Outcome.Invocation.Call.Id); writer.WriteString("toolName", tool.Outcome.Invocation.Call.Name);
                RpcCommandCodec.Raw(writer, "result", Result(tool.Outcome.Result)); writer.WriteBoolean("isError", tool.Outcome.IsError);
                if (tool.Outcome.DurationMs is { } duration) writer.WriteNumber("durationMs", duration);
            })];
            case AgentLoopTurnEnded turn:
            {
                var assistantIndex = turn.Turn.Transcript.Length - turn.Turn.ToolResults.Length - 1;
                if (assistantIndex < 0 || turn.Turn.Transcript[assistantIndex].Role != "assistant")
                    throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed);
                var ended=Event("turn_end", writer =>
                {
                    RpcCommandCodec.Raw(writer, "message", turn.Turn.Transcript[assistantIndex].WireBody);
                    RpcCommandCodec.Messages(writer, "toolResults", turn.Turn.ToolResults, options.MaximumReturnedMessages);
                });
                var chat=turn.Turn.Result.Chat;var primary=chat.NativeDiagnostic??chat.Failure?.NativeDiagnostic;
                if(primary is null&&chat.NativeCleanupDiagnostic is null)return [ended];
                var diagnostic=NativeSessionDiagnosticProjector.Project(snapshot.Context).Entries.LastOrDefault(row=>
                    row.Provenance==NativeSessionDiagnosticProvenance.DeclaredNativeRecord&&row.RecordEntryId==snapshot.Context.LeafId&&
                    row.OperationGeneration==snapshot.OperationGeneration&&row.Diagnostic==primary&&row.CleanupDiagnostic==chat.NativeCleanupDiagnostic);
                if(diagnostic is null)throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed);
                return [ended,Event("pisharp_chat_diagnostic",writer=>
                {
                    writer.WriteNumber("schemaVersion",NativeSessionDiagnosticProjector.SchemaVersion);
                    writer.WritePropertyName("data");NativeSessionDiagnosticProjector.WriteObservation(writer,diagnostic);
                })];
            }
            case AgentLoopEnded end:
                // Source agent_end messages are the run's own messages (newMessages), kept exact when a turn boundary replaced the context.
                if (end.Result.RunMessages.IsDefault && (historyLength < 0 || historyLength > end.Result.Transcript.Length) || _toolStarts.Count != 0)
                    throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed);
                return [Event("agent_end", writer =>
                {
                    if (end.Result.RunMessages.IsDefault) RpcCommandCodec.Messages(writer, "messages", end.Result.Transcript, options.MaximumReturnedMessages, historyLength);
                    else RpcCommandCodec.Messages(writer, "messages", end.Result.RunMessages, options.MaximumReturnedMessages);
                    writer.WriteBoolean("willRetry", willRetry);
                })];
            case TurnStreamObserved stream:
                var delta = Delta(stream.Event);
                return delta is null ? [] : [Event("message_update", writer =>
                { RpcCommandCodec.Raw(writer, "usage", Usage(_usage)); RpcCommandCodec.Raw(writer, "assistantMessageEvent", delta); })];
            default: throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed);
        }
    }

    private JsonData Result(ToolResult result)
    {
        try
        {
            return RpcCommandCodec.Build(writer =>
            {
                // The result was admitted by the invoker: a lone surrogate it kept is written as its escape.
                ToolResultValueCodec.WriteProperties(writer, result, new(options.MaximumOutputBytes, options.MaximumOutputBytes, 1024,
                    options.MaximumJsonDepth, options.MaximumOutputBytes, options.MaximumOutputBytes) { KeepsLoneSurrogates = true });
            }, options.MaximumOutputBytes);
        }
        catch (InvalidOperationException)
        {
            // Execution already admitted the owned result. This stricter output profile can
            // reject its size/depth; retain the existing RPC resource-limit classification.
            throw new RpcDispatchException(RpcDispatchFailure.ResourceLimit);
        }
    }
    private static JsonData Usage(TokenUsage usage) => JsonData.FromElement(PiWireJson.WriteMessage(
        new("", "", "", 0, [], usage, StopReason.Pending)).Value.GetProperty("usage"));

    private JsonData? Delta(StreamEvent observation)
    {
        if (observation is StreamStarted start) { _usage = start.Partial.Usage; return null; }
        if (observation is StreamTerminalEvent terminal) { _usage = terminal.Message.Usage; return null; }
        // Native thinking/argument checkpoints and identity fills update reducer state
        // without a source push. Only the later public end is projected; no RPC event
        // is invented for a checkpoint. Final/aborted message bodies retain its state.
        if (observation is ThinkingCheckpoint or ToolCallCheckpoint or ToolCallHeaderUpdated or ContentBlockFinalized) return null;
        return RpcCommandCodec.Build(writer =>
        {
            var known = new HashSet<string>(StringComparer.Ordinal) { "type", "partial", "contentIndex" };
            void Indexed(string type, int index) { writer.WriteString("type", type); writer.WriteNumber("contentIndex", index); }
            switch (observation)
            {
                case TextStarted value: Indexed("text_start", value.ContentIndex); known.Add("content"); break;
                case TextDelta value: Indexed("text_delta", value.ContentIndex); writer.WriteString("delta", value.Delta); known.Add("delta"); break;
                case TextEnded value: Indexed("text_end", value.ContentIndex); writer.WriteString("content", value.Content); known.Add("content"); break;
                case ThinkingStarted value: Indexed("thinking_start", value.ContentIndex); known.Add("content"); break;
                case ThinkingDelta value: Indexed("thinking_delta", value.ContentIndex); writer.WriteString("delta", value.Delta); known.Add("delta"); break;
                case ThinkingEnded value: Indexed("thinking_end", value.ContentIndex); writer.WriteString("content", value.Content); known.Add("content"); break;
                case ToolCallStarted value:
                    Indexed("toolcall_start", value.ContentIndex); writer.WriteString("id", value.ToolCall.Id); writer.WriteString("toolName", value.ToolCall.Name);
                    known.UnionWith(["toolCall", "id", "toolName"]); break;
                case ToolCallProvisionalStarted value:
                    Indexed("toolcall_start", value.ContentIndex); writer.WriteString("id", value.ToolCall.Id); writer.WriteString("toolName", value.ToolCall.Name);
                    known.UnionWith(["toolCall", "id", "toolName"]); break;
                case ToolCallDelta value: Indexed("toolcall_delta", value.ContentIndex); writer.WriteString("delta", value.Delta); known.Add("delta"); break;
                case ToolCallEnded value:
                    Indexed("toolcall_end", value.ContentIndex); RpcCommandCodec.Raw(writer, "toolCall", PiWireJson.WriteContent(value.ToolCall));
                    known.UnionWith(["toolCall", "id", "name", "arguments"]); break;
                default: throw new RpcDispatchException(RpcDispatchFailure.SessionRunFailed);
            }
            foreach (var property in (observation.ExtraProperties ?? JsonFields.Empty).Values)
                if (!known.Contains(property.Key)) RpcCommandCodec.Raw(writer, property.Key, property.Value);
        }, options.MaximumOutputBytes);
    }
}
