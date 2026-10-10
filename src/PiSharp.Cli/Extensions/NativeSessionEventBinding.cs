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
        attached.Session.ConfigureBeforeCompaction(registry.HasEventHandlers(captured, "session_before_compact")
            ? (proposal, token) => BeforeCompactAsync(owner, attached, proposal, token) : null);
        var turnEnd = registry.HasEventHandlers(captured, "turn_end"); var beforeSettle = registry.HasEventHandlers(captured, "agent_before_settle");
        var messageEnd = registry.HasEventHandlers(captured, "message_end");
        attached.Session.ConfigureMessageEndHandler(messageEnd ? (message, token) => MessageEndAsync(owner, attached, message, token) : null,
            messageEnd ? error => ReportAsync(attached, "message_end", "Invalid message_end replacement: " + error.Message) : null);
        if (!turnEnd && !beforeSettle && !AgentTopics.Concat(["model_select", "thinking_level_select", "session_compact_failed"]).Any(topic => registry.HasObservers(captured, topic)))
        { attached.Session.ConfigureBoundaryHandlers(null, null); Detach(); return; }
        var sink = new Sink(this, owner, attached);
        attached.Session.ConfigureBoundaryHandlers(turnEnd ? sink.TurnEndBoundaryAsync : null,
            beforeSettle ? (request, token) => BoundaryAsync(owner, attached, "agent_before_settle",
                writer => { writer.WriteString("type", "agent_before_settle"); writer.WriteString("outcome", request.Outcome); }, request, token) : null,
            kind => ReportAsync(attached, kind == SessionBoundaryKind.TurnEnd ? "turn_end" : "agent_before_settle",
                (kind == SessionBoundaryKind.TurnEnd ? "turn_end" : "agent_before_settle") + " requested continuation without runnable model context"));
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

    private async ValueTask ReportAsync(AgentSessionAttachment attached, string topic, string message)
    {
        if (report is null) return;
        try { await report(new(topic, "<boundary>", attached.Generation, topic, ExtensionEventFailure.InvalidResult) { Message = message }, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { }
    }

    /// <summary>Source emitBoundary: each handler sees the drafted entries, the continue flag and the context preview so far; a result's
    /// entries or continue replaces them, and the preview is rebuilt after every handler. Entries the session would refuse are
    /// reported as invalid, and an invalid final state commits nothing and does not continue.</summary>
    private async ValueTask<SessionBoundaryDecision?> BoundaryAsync(ReplaceableAgentSession owner, AgentSessionAttachment attached, string topic,
        Action<Utf8JsonWriter> writeBase, SessionBoundaryRequest request, CancellationToken token)
    {
        if (!ReferenceEquals(owner.Current, attached)) return null;
        ImmutableArray<JsonData> entries = []; var shouldContinue = false; var valid = true; var invalid = new List<string>();
        var context = request.Preview(entries, token);
        JsonData Event() => Json(writer =>
        {
            writeBase(writer);
            writer.WritePropertyName("entries"); writer.WriteStartArray(); foreach (var entry in entries) writer.WriteRawValue(entry.ToString(), skipInputValidation: true); writer.WriteEndArray();
            writer.WriteBoolean("continue", shouldContinue); writer.WritePropertyName("context"); WriteContext(writer, context);
        });
        await registry.ReduceEventAsync(captured, topic, Event(), (_, result) =>
        {
            var value = result.Value; string? shape = null;
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("entries", out var drafted))
                {
                    if (drafted.ValueKind == JsonValueKind.Array) entries = [.. drafted.EnumerateArray().Select(item => JsonData.Parse(item.GetRawText()))];
                    else shape = "entries must be an array";
                }
                if (value.TryGetProperty("continue", out var next)) shouldContinue = Truthy(next);
            }
            try
            {
                if (shape is not null) throw new InvalidDataException(shape);
                context = request.Preview(entries, token); valid = true;
            }
            catch (Exception error) when (error is not OperationCanceledException) { valid = false; invalid.Add("Invalid boundary entries: " + error.Message); }
            return Event();
        }, report, token, attached.LifetimeToken).ConfigureAwait(false);
        foreach (var message in invalid) await ReportAsync(attached, topic, message).ConfigureAwait(false);
        return valid ? new(entries, shouldContinue) : new([], false);
    }

    /// <summary>Source emitMessageEnd: each handler sees the current message; a replacement with another role is reported and
    /// skipped. Null (no replacement) keeps the message.</summary>
    private async ValueTask<TranscriptEntry?> MessageEndAsync(ReplaceableAgentSession owner, AgentSessionAttachment attached, TranscriptEntry message,
        CancellationToken token)
    {
        if (!ReferenceEquals(owner.Current, attached)) return null;
        var current = message.WireBody; var modified = false; var invalid = 0;
        JsonData Event() => Json(writer => { writer.WriteString("type", "message_end"); Raw(writer, "message", current); });
        await registry.ReduceEventAsync(captured, "message_end", Event(), (_, result) =>
        {
            if (result.Value.ValueKind != JsonValueKind.Object || !result.Value.TryGetProperty("message", out var replacement) || !Truthy(replacement)) return null;
            if (replacement.ValueKind != JsonValueKind.Object || !replacement.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String ||
                role.GetString() != message.Role)
            { invalid++; return null; }
            // Untyped handlers can return null or missing content; it never enters session history.
            var node = PiSharp.Contracts.JsonUtf16.MutableNode(replacement.GetRawText())!.AsObject();
            if (message.Role is "user" or "assistant" or "toolResult" or "custom" && node["content"] is null) node["content"] = new System.Text.Json.Nodes.JsonArray();
            current = JsonData.Parse(node.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            modified = true; return Event();
        }, report, token, attached.LifetimeToken).ConfigureAwait(false);
        for (var index = 0; index < invalid; index++)
            await ReportAsync(attached, "message_end", "message_end handlers must return a message with the same role").ConfigureAwait(false);
        return modified ? new TranscriptEntry(message.Role, current) : null;
    }

    private static void WriteContext(Utf8JsonWriter writer, SessionBoundaryPreview preview)
    {
        var context = preview.Context;
        writer.WriteStartObject();
        writer.WritePropertyName("contextEntries"); writer.WriteStartArray();
        foreach (var entry in context.ContextEntries.IsDefault ? [] : context.ContextEntries)
        {
            writer.WriteStartObject(); Raw(writer, "sourceEntry", entry.SourceEntry.WireBody);
            Array(writer, "messages", entry.Messages.Select(message => message.WireBody)); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        Array(writer, "contextMessages", context.Messages.Select(message => message.WireBody));
        Array(writer, "llmMessages", context.LlmMessages.Select(message => message.WireBody));
        Array(writer, "pendingMessages", preview.PendingMessages.Select(message => message.WireBody));
        writer.WriteBoolean("canContinue", preview.CanContinue); writer.WriteEndObject();
    }

    /// <summary>Source emit for session_before_compact: handlers see the same event, the last truthy result wins, and a
    /// cancelling result ends the dispatch. A malformed compaction result is reported and ignored.</summary>
    private async ValueTask<SessionBeforeCompactDecision?> BeforeCompactAsync(ReplaceableAgentSession owner, AgentSessionAttachment attached,
        SessionBeforeCompactProposal proposal, CancellationToken token)
    {
        if (!ReferenceEquals(owner.Current, attached)) return null;
        JsonData? last = null;
        await registry.ReduceEventAsync(captured, "session_before_compact", proposal.ToJson(),
            (_, result) => { if (Truthy(result.Value)) last = result; return null; }, report, token, attached.LifetimeToken,
            result => Truthy(result.Value) && result.Value.ValueKind == JsonValueKind.Object &&
                result.Value.TryGetProperty("cancel", out var cancel) && Truthy(cancel)).ConfigureAwait(false);
        if (last is not { Value.ValueKind: JsonValueKind.Object } chosen) return null;
        if (chosen.Value.TryGetProperty("cancel", out var cancelled) && Truthy(cancelled)) return new(Cancel: true);
        if (!chosen.Value.TryGetProperty("compaction", out var compaction) || !Truthy(compaction)) return new();
        try { return new(Compaction: ExtensionCompaction(compaction)); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (report is not null)
                try { await report(new("session_before_compact", "native-host", attached.Generation, "result", ExtensionEventFailure.InvalidResult)
                    { Message = error.Message }, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { }
            return new();
        }
    }
    private static SessionExtensionCompaction ExtensionCompaction(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("firstKeptEntryId", out var firstKept) || firstKept.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("tokensBefore", out var tokens) || tokens.ValueKind != JsonValueKind.Number)
            throw new InvalidDataException("session_before_compact compaction requires summary, firstKeptEntryId and tokensBefore.");
        TokenUsage? usage = null;
        if (value.TryGetProperty("usage", out var usageValue) && usageValue.ValueKind != JsonValueKind.Undefined && usageValue.ValueKind != JsonValueKind.Null)
            usage = PiWireJson.ReadMessage(Json(writer =>
            {
                writer.WriteString("role", "assistant"); writer.WritePropertyName("content"); writer.WriteStartArray(); writer.WriteEndArray();
                writer.WriteString("api", "summary"); writer.WriteString("provider", "summary"); writer.WriteString("model", "summary");
                writer.WritePropertyName("usage"); writer.WriteRawValue(usageValue.GetRawText(), skipInputValidation: true);
                writer.WriteString("stopReason", "stop"); writer.WriteNumber("timestamp", 0);
            }).Value).Usage;
        JsonData? details = value.TryGetProperty("details", out var detailsValue) && detailsValue.ValueKind != JsonValueKind.Undefined
            ? JsonData.Parse(detailsValue.GetRawText()) : null;
        return new(summary.GetString()!, firstKept.GetString()!, tokens.GetDouble(), usage, details);
    }
    /// <summary>JavaScript truthiness of a handler result value.</summary>
    internal static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object or JsonValueKind.Array => true,
        JsonValueKind.True => true,
        JsonValueKind.String => value.GetString()!.Length != 0,
        JsonValueKind.Number => value.GetDouble() is var number && number != 0 && !double.IsNaN(number),
        _ => false
    };

    internal static JsonData Json(Action<Utf8JsonWriter> write)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        { writer.WriteStartObject(); write(writer); writer.WriteEndObject(); }
        return JsonData.Parse(Encoding.UTF8.GetString(bytes.ToArray()));
    }
    private static void Raw(Utf8JsonWriter writer, string name, JsonData value) { writer.WritePropertyName(name); writer.WriteRawValue(value.ToString(), skipInputValidation: true); }
    private static void Array(Utf8JsonWriter writer, string name, IEnumerable<JsonData> values)
    { writer.WritePropertyName(name); writer.WriteStartArray(); foreach (var value in values) writer.WriteRawValue(value.ToString(), skipInputValidation: true); writer.WriteEndArray(); }

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
                        Array(writer, "messages", (end.Result.RunMessages.IsDefault ? end.Result.Transcript.Skip(Math.Min(_historyLength, end.Result.Transcript.Length))
                            : end.Result.RunMessages).Select(message => message.WireBody));
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
                        writer.WriteBoolean("fromExtension", failed.FromExtension);
                    })).ConfigureAwait(false); break;
            }
        }

        private async ValueTask UpdateAsync(StreamEvent value)
        {
            if (_partial is null || value is ThinkingCheckpoint or ToolCallCheckpoint or ToolCallHeaderUpdated) return;
            try { _partial.Apply(value); } catch (Exception) { _partial = null; return; }
            // A block finalized without a source push (Bedrock) has no message_update of its own.
            if (value is StreamStarted or StreamTerminalEvent or ContentBlockFinalized) return;
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
        // The result was admitted by the invoker; its projection keeps what that admitted (Pi-sized, lone surrogates, any depth Pi holds).
        private static JsonData Result(ToolResult result) => Json(writer => ToolResultValueCodec.WriteProperties(writer, result, PiSharp.Cli.Commands.PiPayloadBudget.PiToolResults));

        /// <summary>The turn_end fields before the boundary state: the persisted assistant message and tool results and their entry ids.
        /// Null when the assistant entry cannot be resolved.</summary>
        private Action<Utf8JsonWriter>? TurnEndBase(AgentLoopTurn turn, string outcome)
        {
            var context = Session.Snapshot.Context;
            var assistantIndex = turn.Transcript.Length - turn.ToolResults.Length - 1;
            if (assistantIndex < 0 || turn.Transcript[assistantIndex].Role != "assistant") throw new InvalidOperationException("The turn has no assistant message.");
            var assistant = Session.PersistedWire(turn.Transcript[assistantIndex].WireBody);
            var toolResults = turn.ToolResults.Select(message => Session.PersistedWire(message.WireBody)).ToImmutableArray();
            string? EntryId(JsonData message) => context.ContextEntries.IsDefault ? null : context.ContextEntries.LastOrDefault(entry =>
                entry.Messages.Any(candidate => JsonUtf16.DeepEquals(candidate.WireBody.Value, message.Value)))?.SourceEntry.Id;
            var messageEntryId = EntryId(assistant);
            if (messageEntryId is null) return null;
            var turnIndex = _turnIndex;
            return writer =>
            {
                writer.WriteString("type", "turn_end"); writer.WriteNumber("turnIndex", turnIndex); Raw(writer, "message", assistant);
                Array(writer, "toolResults", toolResults);
                writer.WriteString("messageEntryId", messageEntryId);
                writer.WritePropertyName("toolResultEntryIds"); writer.WriteStartArray();
                foreach (var result in toolResults) if (EntryId(result) is { } id) writer.WriteStringValue(id);
                writer.WriteEndArray();
                writer.WriteString("outcome", outcome);
            };
        }
        private static string Outcome(AgentLoopTurn turn) => turn.Result.Chat.Message.StopReason switch
        { StopReason.Aborted => "aborted", StopReason.Error => "error", _ => "completed" };

        /// <summary>Source _dispatchTurnEndBoundary for the result-returning turn_end handlers.</summary>
        internal async ValueTask<SessionBoundaryDecision?> TurnEndBoundaryAsync(SessionBoundaryRequest request, CancellationToken token)
        {
            if (TurnEndBase(request.Turn!, request.Outcome) is not { } writeBase)
            {
                await binding.ReportAsync(attached, "turn_end", "turn_end could not resolve the persisted assistant entry ID").ConfigureAwait(false);
                return null;
            }
            return await binding.BoundaryAsync(owner, attached, "turn_end", writeBase, request, token).ConfigureAwait(false);
        }

        /// <summary>Source turn_end event for observers: the persisted entry ids of the turn's assistant message and tool results,
        /// the outcome, no drafted entries, and the projected context preview.</summary>
        private JsonData TurnEnd(AgentLoopTurn turn)
        {
            var writeBase = TurnEndBase(turn, Outcome(turn)) ?? throw new InvalidOperationException("The turn's assistant entry is not persisted.");
            var context = Session.Snapshot.Context;
            var queue = Session.GetPendingInputQueueSnapshot();
            var pending = queue.SteeringMessages.Concat(queue.FollowUpMessages).ToImmutableArray();
            var finalRole = context.LlmMessages.IsEmpty ? null : context.LlmMessages[^1].Role;
            var canContinue = context.LlmMessages.Any(message => message.Role != "system") && finalRole != "assistant" || !pending.IsEmpty;
            return Json(writer =>
            {
                writeBase(writer);
                writer.WritePropertyName("entries"); writer.WriteStartArray(); writer.WriteEndArray();
                writer.WriteBoolean("continue", false);
                writer.WritePropertyName("context"); WriteContext(writer, new(context, pending, canContinue));
            });
        }
    }

    /// <summary>The selected model's catalog object when the host knows it; otherwise the descriptor fields.</summary>
    private JsonData Model(ModelDescriptor model) => modelWire?.Invoke(model) ??
        Json(writer => { writer.WriteString("id", model.Id); writer.WriteString("api", model.Api); writer.WriteString("provider", model.Provider); });
}
