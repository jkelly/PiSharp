// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (_emitExtensionEvent,
// _dispatchTurnEndBoundary, _buildBoundaryContext, setThinkingLevel, _emitModelSelect, _emitSessionCompactFailed) and
// packages/coding-agent/src/core/extensions/types.ts (AgentStartEvent ... ToolExecutionEndEvent, ModelSelectEvent,
// ThinkingLevelSelectEvent, SessionCompactFailedEvent).
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.AI;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions;

/// <summary>Delivers the agent loop and session events Pi emits to extensions (agent_start/end, turn_start/end,
/// message_start/update/end, tool_execution_start/update/end, model_select, thinking_level_select, session_compact_failed)
/// to native observers of one captured registration revision, on each actual attachment generation. Handlers run before
/// the public (RPC/JSON) listeners, as in Pi; a handler error is reported and the remaining handlers still run.</summary>
internal sealed class NativeSessionEventBinding(ExtensionRegistry registry, ExtensionRegistrySnapshot captured,
    Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? report = null, Func<ModelDescriptor, JsonData?>? modelWire = null)
{
    internal static readonly ImmutableArray<string> AgentTopics = ["agent_start", "agent_end", "turn_start", "turn_end", "message_start",
        "message_update", "message_end", "tool_execution_start", "tool_execution_update", "tool_execution_end"];
    private readonly object _gate = new();
    private IDisposable? _agentSubscription, _operationSubscription;

    internal void Attach(ReplaceableAgentSession owner, AgentSessionAttachment attached)
    {
        owner.ValidateAttachment(attached);
        if (!AgentTopics.Concat(["model_select", "thinking_level_select", "session_compact_failed"]).Any(topic => registry.HasObservers(captured, topic)))
        { Detach(); return; }
        var sink = new Sink(this, owner, attached);
        var agent = attached.Session.Subscribe(sink); IDisposable operation;
        try { operation = attached.Session.SubscribeOperationEvents(sink); }
        catch { agent.Dispose(); throw; }
        IDisposable? previousAgent, previousOperation;
        lock (_gate) { previousAgent = _agentSubscription; previousOperation = _operationSubscription; _agentSubscription = agent; _operationSubscription = operation; }
        previousAgent?.Dispose(); previousOperation?.Dispose();
    }
    private void Detach()
    {
        IDisposable? agent, operation;
        lock (_gate) { agent = _agentSubscription; operation = _operationSubscription; _agentSubscription = _operationSubscription = null; }
        agent?.Dispose(); operation?.Dispose();
    }

    private async ValueTask PublishAsync(ReplaceableAgentSession owner, AgentSessionAttachment attached, string topic, Func<JsonData> observation)
    {
        // Never retarget an old publisher to the replacement attachment, and build no payload without observers.
        if (!ReferenceEquals(owner.Current, attached) || !registry.HasObservers(captured, topic)) return;
        // Pi reports extension failures and never fails the run for an observation event.
        try { await registry.DispatchObservationsReportingAsync(captured, topic, observation(), report, CancellationToken.None, attached.LifetimeToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (attached.LifetimeToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (report is not null)
                try { await report(new(topic, "native-host", attached.Generation, "publish", ExtensionEventFailure.InvalidResult) { Message = error.Message },
                    CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { }
        }
    }

    internal static JsonData Json(Action<Utf8JsonWriter> write)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        { writer.WriteStartObject(); write(writer); writer.WriteEndObject(); }
        return JsonData.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
    }
    private static void Raw(Utf8JsonWriter writer, string name, JsonData value) { writer.WritePropertyName(name); writer.WriteRawValue(value.ToString()); }
    private static void Array(Utf8JsonWriter writer, string name, IEnumerable<JsonData> values)
    { writer.WritePropertyName(name); writer.WriteStartArray(); foreach (var value in values) writer.WriteRawValue(value.ToString()); writer.WriteEndArray(); }

    /// <summary>Per-attachment state: the run's turn index and history start, and the streamed assistant partial.</summary>
    private sealed class Sink(NativeSessionEventBinding binding, ReplaceableAgentSession owner, AgentSessionAttachment attached)
        : IAgentEventSink, ISessionOperationEventSink
    {
        private int _turnIndex, _historyLength;
        private AssistantStreamReducer? _partial;
        private readonly Dictionary<string, ToolResultMessage> _toolStarts = new(StringComparer.Ordinal);
        private PersistentAgentSession Session => attached.Session;

        public async ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken)
        {
            ValueTask Publish(string topic, Func<JsonData> value) => binding.PublishAsync(owner, attached, topic, value);
            switch (observation)
            {
                case AgentLoopStarted:
                    _turnIndex = 0; _historyLength = Session.Snapshot.Agent.Messages.Length; _toolStarts.Clear();
                    await Publish("agent_start", () => Json(writer => writer.WriteString("type", "agent_start"))).ConfigureAwait(false); break;
                case AgentLoopEnded end:
                    await Publish("agent_end", () => Json(writer =>
                    {
                        writer.WriteString("type", "agent_end");
                        Array(writer, "messages", end.Result.Transcript.Skip(Math.Min(_historyLength, end.Result.Transcript.Length)).Select(message => message.WireBody));
                    })).ConfigureAwait(false); break;
                case AgentLoopTurnStarted:
                    await Publish("turn_start", () => Json(writer =>
                    {
                        writer.WriteString("type", "turn_start"); writer.WriteNumber("turnIndex", _turnIndex);
                        writer.WriteNumber("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    })).ConfigureAwait(false); break;
                case AgentLoopTurnEnded turn:
                    await Publish("turn_end", () => TurnEnd(turn.Turn)).ConfigureAwait(false);
                    _turnIndex++; break;
                case AgentLoopInputMessageStarted input:
                    await Publish("message_start", () => Message("message_start", input.Message.WireBody)).ConfigureAwait(false); break;
                case AgentLoopInputMessageEnded input:
                    await Publish("message_end", () => Message("message_end", Acknowledged(input.Message.Role))).ConfigureAwait(false); break;
                case AssistantMessageStarted assistant:
                    _partial = new AssistantStreamReducer(assistant.Message);
                    await Publish("message_start", () => Message("message_start", PiWireJson.WriteMessage(assistant.Message))).ConfigureAwait(false); break;
                case AssistantMessageEnded:
                    _partial = null;
                    await Publish("message_end", () => Message("message_end", Acknowledged("assistant"))).ConfigureAwait(false); break;
                case TurnStreamObserved stream:
                    await UpdateAsync(stream.Event).ConfigureAwait(false); break;
                case ToolResultMessageStarted start:
                    _toolStarts[start.Message.ToolCallId] = start.Message; break;
                case ToolResultMessageEnded end:
                    // The native start value has no timestamp; both records use the acknowledged body, as the RPC projection does.
                    _toolStarts.Remove(end.Message.ToolCallId);
                    await Publish("message_start", () => Message("message_start", Acknowledged("toolResult"))).ConfigureAwait(false);
                    await Publish("message_end", () => Message("message_end", Acknowledged("toolResult"))).ConfigureAwait(false); break;
                case ToolExecutionStarted tool:
                    await Publish("tool_execution_start", () => Json(writer =>
                    {
                        writer.WriteString("type", "tool_execution_start"); writer.WriteString("toolCallId", tool.Invocation.Call.Id);
                        writer.WriteString("toolName", tool.Invocation.Call.Name); Raw(writer, "args", tool.Invocation.Call.Arguments);
                        if (tool.Invocation.ParentToolCallId is { } parent) writer.WriteString("parentToolCallId", parent);
                    })).ConfigureAwait(false); break;
                case ToolExecutionUpdated tool:
                    await Publish("tool_execution_update", () => Json(writer =>
                    {
                        writer.WriteString("type", "tool_execution_update"); writer.WriteString("toolCallId", tool.Invocation.Call.Id);
                        writer.WriteString("toolName", tool.Invocation.Call.Name); Raw(writer, "args", tool.Invocation.Call.Arguments);
                        Raw(writer, "partialResult", Result(tool.PartialResult));
                        if (tool.Invocation.ParentToolCallId is { } parent) writer.WriteString("parentToolCallId", parent);
                    })).ConfigureAwait(false); break;
                case ToolExecutionEnded tool:
                    await Publish("tool_execution_end", () => Json(writer =>
                    {
                        writer.WriteString("type", "tool_execution_end"); writer.WriteString("toolCallId", tool.Outcome.Invocation.Call.Id);
                        writer.WriteString("toolName", tool.Outcome.Invocation.Call.Name); Raw(writer, "result", Result(tool.Outcome.Result));
                        writer.WriteBoolean("isError", tool.Outcome.IsError);
                        if (tool.Outcome.DurationMs is { } duration) writer.WriteNumber("durationMs", duration);
                        if (tool.Outcome.Invocation.ParentToolCallId is { } parent) writer.WriteString("parentToolCallId", parent);
                    })).ConfigureAwait(false); break;
            }
        }

        public async ValueTask EmitAsync(SessionOperationEvent observation, CancellationToken cancellationToken)
        {
            switch (observation)
            {
                case SessionThinkingLevelChanged changed:
                    await binding.PublishAsync(owner, attached, "thinking_level_select", changed.ToExtensionJson).ConfigureAwait(false); break;
                case SessionModelSelected selected:
                    await binding.PublishAsync(owner, attached, "model_select", () => Json(writer =>
                    {
                        writer.WriteString("type", "model_select"); Raw(writer, "model", binding.Model(selected.Model));
                        if (selected.PreviousModel is { } previous) Raw(writer, "previousModel", binding.Model(previous));
                        writer.WriteString("source", selected.Source);
                    })).ConfigureAwait(false); break;
                case SessionCompactionEnded { Result: null } failed:
                    await binding.PublishAsync(owner, attached, "session_compact_failed", () => Json(writer =>
                    {
                        writer.WriteString("type", "session_compact_failed");
                        writer.WriteString("reason", SessionSummarizationRetryAttemptStarted.ReasonText(failed.Reason));
                        if (failed.ErrorMessage is not null) writer.WriteString("errorMessage", failed.ErrorMessage);
                        writer.WriteBoolean("aborted", failed.Aborted); writer.WriteBoolean("willRetry", failed.WillRetry);
                        writer.WriteBoolean("fromExtension", false);
                    })).ConfigureAwait(false); break;
            }
        }

        private async ValueTask UpdateAsync(StreamEvent value)
        {
            if (_partial is null || value is ThinkingCheckpoint or ToolCallCheckpoint or ToolCallHeaderUpdated) return;
            try { _partial.Apply(value); } catch (Exception) { _partial = null; return; }
            if (value is StreamStarted or StreamTerminalEvent) return;
            var partial = _partial.Snapshot();
            await binding.PublishAsync(owner, attached, "message_update", () => Json(writer =>
            {
                writer.WriteString("type", "message_update"); Raw(writer, "message", PiWireJson.WriteMessage(partial));
                Raw(writer, "assistantMessageEvent", value.SourceEmissionSnapshot ?? PiWireJson.WriteSourceEvent(value, partial));
            })).ConfigureAwait(false);
        }

        private static JsonData Message(string type, JsonData message) => Json(writer => { writer.WriteString("type", type); Raw(writer, "message", message); });
        private JsonData Acknowledged(string role)
        {
            var messages = Session.Snapshot.Context.Messages;
            if (messages.IsEmpty || messages[^1].Role != role) throw new InvalidOperationException("The acknowledged message is not the last context message.");
            return messages[^1].WireBody;
        }
        private static JsonData Result(ToolResult result) => Json(writer => ToolResultValueCodec.WriteProperties(writer, result));

        /// <summary>Source turn_end boundary event: the persisted entry ids of the turn's assistant message and tool results,
        /// the outcome, no drafted entries, and the projected context preview.</summary>
        private JsonData TurnEnd(AgentLoopTurn turn)
        {
            var snapshot = Session.Snapshot; var context = snapshot.Context;
            var assistantIndex = turn.Transcript.Length - turn.ToolResults.Length - 1;
            if (assistantIndex < 0 || turn.Transcript[assistantIndex].Role != "assistant") throw new InvalidOperationException("The turn has no assistant message.");
            var assistant = turn.Transcript[assistantIndex].WireBody;
            string? EntryId(JsonData message) => context.ContextEntries.IsDefault ? null : context.ContextEntries.LastOrDefault(entry =>
                entry.Messages.Any(candidate => JsonElement.DeepEquals(candidate.WireBody.Value, message.Value)))?.SourceEntry.Id;
            var stopReason = assistant.Value.TryGetProperty("stopReason", out var reason) ? reason.GetString() : null;
            var queue = Session.GetPendingInputQueueSnapshot();
            var pending = queue.SteeringMessages.Concat(queue.FollowUpMessages).ToImmutableArray();
            var finalRole = context.LlmMessages.IsEmpty ? null : context.LlmMessages[^1].Role;
            var canContinue = context.LlmMessages.Any(message => message.Role != "system") && finalRole != "assistant" || !pending.IsEmpty;
            return Json(writer =>
            {
                writer.WriteString("type", "turn_end"); writer.WriteNumber("turnIndex", _turnIndex); Raw(writer, "message", assistant);
                Array(writer, "toolResults", turn.ToolResults.Select(message => message.WireBody));
                if (EntryId(assistant) is { } messageEntryId) writer.WriteString("messageEntryId", messageEntryId);
                writer.WritePropertyName("toolResultEntryIds"); writer.WriteStartArray();
                foreach (var result in turn.ToolResults) if (EntryId(result.WireBody) is { } id) writer.WriteStringValue(id);
                writer.WriteEndArray();
                writer.WriteString("outcome", stopReason == "aborted" ? "aborted" : stopReason == "error" ? "error" : "completed");
                writer.WritePropertyName("entries"); writer.WriteStartArray(); writer.WriteEndArray();
                writer.WriteBoolean("continue", false);
                writer.WritePropertyName("context"); writer.WriteStartObject();
                writer.WritePropertyName("contextEntries"); writer.WriteStartArray();
                foreach (var entry in context.ContextEntries.IsDefault ? [] : context.ContextEntries)
                {
                    writer.WriteStartObject(); Raw(writer, "sourceEntry", entry.SourceEntry.WireBody);
                    Array(writer, "messages", entry.Messages.Select(message => message.WireBody)); writer.WriteEndObject();
                }
                writer.WriteEndArray();
                Array(writer, "contextMessages", context.Messages.Select(message => message.WireBody));
                Array(writer, "llmMessages", context.LlmMessages.Select(message => message.WireBody));
                Array(writer, "pendingMessages", pending.Select(message => message.WireBody));
                writer.WriteBoolean("canContinue", canContinue); writer.WriteEndObject();
            });
        }
    }

    /// <summary>The selected model's catalog object when the host knows it; otherwise the descriptor fields.</summary>
    private JsonData Model(ModelDescriptor model) => modelWire?.Invoke(model) ??
        Json(writer => { writer.WriteString("id", model.Id); writer.WriteString("api", model.Api); writer.WriteString("provider", model.Provider); });
}
