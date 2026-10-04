using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Contracts;

namespace PiSharp.Agent;

public sealed record AgentLoopOptions(int MaximumTurns = 16, int MaximumTranscriptMessages = 1024)
{
    /// <summary>Strict admission for retained canonical tool values, with the default execution/message boundary.</summary>
    public ToolResultValueOptions CanonicalToolResultLimits { get; init; } = ToolResultValueOptions.ExecutionBoundary;
}
public enum AgentLoopStopReason { Completed, ChatFailure, TurnLimit, TranscriptLimit }
public enum AgentLoopFinishAction { Default, End, Continue }
public sealed record AgentLoopSnapshot(int TurnIndex, ModelDescriptor Model, ImmutableArray<TranscriptEntry> Transcript);
public sealed record AgentLoopTurn(int TurnIndex, TurnResult Result, ImmutableArray<TranscriptEntry> Transcript,
    ImmutableArray<TranscriptEntry> ToolResults);
public sealed record AgentLoopResult(AgentLoopStopReason Reason, ImmutableArray<TranscriptEntry> Transcript,
    ImmutableArray<AgentLoopTurn> Turns, ImmutableArray<TranscriptEntry> PendingMessages,
    AssistantMessage? RejectedAssistant = null);

/// <summary>Low-level asynchronous callback seams, not high-level Agent queue implementations.</summary>
public sealed record AgentLoopCallbacks(
    Func<AgentLoopSnapshot, CancellationToken, ValueTask<ChatRequest>> PrepareRequest,
    Func<AgentLoopTurn, CancellationToken, ValueTask<ImmutableArray<TranscriptEntry>>>? PrepareNextTurn = null,
    Func<CancellationToken, ValueTask<ImmutableArray<TranscriptEntry>>>? GetSteeringMessages = null,
    Func<CancellationToken, ValueTask<ImmutableArray<TranscriptEntry>>>? GetFollowUpMessages = null,
    Func<AgentLoopTurn, CancellationToken, ValueTask>? FinishTurn = null,
    Func<AgentLoopTurn, CancellationToken, ValueTask<AgentLoopFinishAction>>? FinishTurnDecision = null)
{
    public Func<ImmutableArray<TranscriptEntry>, CancellationToken, ValueTask<ImmutableArray<TranscriptEntry>>>? TransformRequestMessages { get; init; }
    public Func<AgentRequestBoundary, CancellationToken, ValueTask<AgentLoopRequestPreparation?>>? PrepareRequestBoundary { get; init; }
}

public sealed record AgentLoopStarted : AgentEvent;
public sealed record AgentLoopTurnStarted(int TurnIndex) : AgentEvent;
public sealed record AgentLoopInputMessageStarted(TranscriptEntry Message) : AgentEvent;
public sealed record AgentLoopInputMessageEnded(TranscriptEntry Message) : AgentEvent;
public sealed record AgentLoopTurnEnded(AgentLoopTurn Turn) : AgentEvent;
public sealed record AgentLoopEnded(AgentLoopResult Result) : AgentEvent;

/// <summary>
/// A bounded append-only loop over accepted single turns. Request preparation is explicit;
/// no tool declarations, context replacement, session coordinator or high-level queue modes are supplied.
/// </summary>
public sealed class AgentLoopRunner
{
    private readonly TurnRunner _turnRunner;
    private readonly ModelDescriptor _model;
    private readonly Func<long> _clock;
    private readonly AgentLoopOptions _options;
    private readonly ToolResultValueOptions _canonicalToolResultLimits;

    public AgentLoopRunner(TurnRunner turnRunner, ModelDescriptor model, Func<long> clock, AgentLoopOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(turnRunner);
        ValidateModel(model);
        ArgumentNullException.ThrowIfNull(clock);
        _turnRunner = turnRunner;
        _model = model;
        _clock = clock;
        _options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumTurns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumTranscriptMessages);
        ArgumentNullException.ThrowIfNull(_options.CanonicalToolResultLimits);
        _canonicalToolResultLimits = ToolResultValueCodec.ValidateLimits(_options.CanonicalToolResultLimits);
    }

    public Task<AgentLoopResult> RunAsync(ImmutableArray<TranscriptEntry> initialMessages,
        AgentLoopCallbacks callbacks, IAgentEventSink sink, CancellationToken cancellationToken = default) =>
        RunCoreAsync([], initialMessages, callbacks, sink, false, false, cancellationToken);

    /// <summary>Retains prior canonical history and emits input events only for newly admitted system/user messages.</summary>
    public Task<AgentLoopResult> RunWithContextAsync(ImmutableArray<TranscriptEntry> canonicalHistory,
        ImmutableArray<TranscriptEntry> newInputMessages, AgentLoopCallbacks callbacks, IAgentEventSink sink,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(canonicalHistory, newInputMessages, callbacks, sink, true, false, cancellationToken);

    /// <summary>Uses canceled work for abort finalization while lifecycle delivery uses an uncanceled settlement token.</summary>
    public Task<AgentLoopResult> RunWithContextAndAbortSettlementAsync(ImmutableArray<TranscriptEntry> canonicalHistory,
        ImmutableArray<TranscriptEntry> newInputMessages, AgentLoopCallbacks callbacks, IAgentEventSink sink,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(canonicalHistory, newInputMessages, callbacks, sink, true, true, cancellationToken);

    private async Task<AgentLoopResult> RunCoreAsync(ImmutableArray<TranscriptEntry> canonicalHistory,
        ImmutableArray<TranscriptEntry> newInputMessages, AgentLoopCallbacks callbacks, IAgentEventSink sink,
        bool contextAdmission, bool settleAbort, CancellationToken cancellationToken)
    {
        if (contextAdmission) ValidateCanonicalHistory(canonicalHistory, _canonicalToolResultLimits);
        ValidateInputs(newInputMessages);
        ArgumentNullException.ThrowIfNull(callbacks);
        ArgumentNullException.ThrowIfNull(callbacks.PrepareRequest);
        ArgumentNullException.ThrowIfNull(sink);
        if ((long)canonicalHistory.Length + newInputMessages.Length > _options.MaximumTranscriptMessages)
            throw new ArgumentException(contextAdmission ? "Combined history and inputs exceed the transcript admission limit." :
                "Initial messages exceed the transcript admission limit.", contextAdmission ? nameof(canonicalHistory) : "initialMessages");
        if (contextAdmission && newInputMessages.IsEmpty &&
            (canonicalHistory.IsEmpty || canonicalHistory[^1].Role is not ("user" or "toolResult" or "custom")))
            throw new ArgumentException("Context-only continuation requires history ending in a user, toolResult or custom message.", nameof(canonicalHistory));
        if (!settleAbort) cancellationToken.ThrowIfCancellationRequested();
        var deliveryToken = settleAbort ? CancellationToken.None : cancellationToken;
        var transcript = canonicalHistory.IsEmpty ? newInputMessages : canonicalHistory.AddRange(newInputMessages);
        var turns = ImmutableArray.CreateBuilder<AgentLoopTurn>();
        var pending = ImmutableArray<TranscriptEntry>.Empty;
        AgentLoopTurn? lastTurn = null;
        var needsRequest = true;
        var requestRunner = _turnRunner;
        var explicitContinuation = false;
        await sink.EmitAsync(new AgentLoopStarted(), deliveryToken).ConfigureAwait(false);
        try
        {
            await sink.EmitAsync(new AgentLoopTurnStarted(0), deliveryToken).ConfigureAwait(false);
            newInputMessages = await PrepareBoundaryAsync(canonicalHistory, newInputMessages, initial: true).ConfigureAwait(false);
            foreach (var message in newInputMessages) await EmitInputAsync(message).ConfigureAwait(false);
            pending = await PollAsync(callbacks.GetSteeringMessages).ConfigureAwait(false);
            while (true)
            {
                while (needsRequest || pending.Length > 0)
                {
                    if (!settleAbort) cancellationToken.ThrowIfCancellationRequested();
                    if (turns.Count >= _options.MaximumTurns)
                        return await EndAsync(AgentLoopStopReason.TurnLimit).ConfigureAwait(false);
                    if (lastTurn is not null)
                    {
                        ImmutableArray<TranscriptEntry> prepared = [];
                        if (callbacks.PrepareNextTurn is not null && !(settleAbort && cancellationToken.IsCancellationRequested))
                        {
                            try { prepared = await callbacks.PrepareNextTurn(lastTurn, cancellationToken).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (settleAbort && cancellationToken.IsCancellationRequested) { }
                        }
                        if (!settleAbort) cancellationToken.ThrowIfCancellationRequested();
                        ValidateInputs(prepared);
                        // Preserve the source's one-poll delivery rule: only poll again when earlier steering was empty.
                        if (pending.Length == 0) pending = await PollAsync(callbacks.GetSteeringMessages).ConfigureAwait(false);
                        pending = prepared.AddRange(pending);
                        await sink.EmitAsync(new AgentLoopTurnStarted(turns.Count), deliveryToken).ConfigureAwait(false);
                        pending = await PrepareBoundaryAsync(transcript, pending, initial: false).ConfigureAwait(false);
                    }
                    if ((long)transcript.Length + pending.Length + 1 > _options.MaximumTranscriptMessages) throw new LoopLimit();
                    foreach (var message in pending)
                    {
                        await EmitInputAsync(message).ConfigureAwait(false);
                        transcript = transcript.Add(message);
                    }
                    pending = [];
                    var snapshot = new AgentLoopSnapshot(turns.Count, _model, transcript);
                    ChatRequest request;
                    if (settleAbort && cancellationToken.IsCancellationRequested) request = new(snapshot.Model, snapshot.Transcript);
                    else
                    {
                        try { request = await callbacks.PrepareRequest(snapshot, cancellationToken).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (settleAbort && cancellationToken.IsCancellationRequested)
                        { request = new(snapshot.Model, snapshot.Transcript); }
                    }
                    if (!settleAbort) cancellationToken.ThrowIfCancellationRequested();
                    ValidateRequest(request, snapshot);
                    if (!cancellationToken.IsCancellationRequested && callbacks.TransformRequestMessages is { } transform)
                    {
                        try
                        {
                            var messages = await transform(request.Messages, cancellationToken).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            ValidateRequestMessages(messages, _canonicalToolResultLimits, _options.MaximumTranscriptMessages);
                            request = request with { Messages = messages };
                        }
                        catch (OperationCanceledException) when (settleAbort && cancellationToken.IsCancellationRequested)
                        { request = new(snapshot.Model, snapshot.Transcript); }
                    }
                    // Source custom messages stay canonical until after extension context hooks.
                    request = request with { Messages = ProjectCustomMessages(request.Messages) };
                    // Sample before chat/effects so a failing clock cannot discard a successful tool batch.
                    var toolTimestamp = _clock();
                    if (!settleAbort) cancellationToken.ThrowIfCancellationRequested();
                    var assistantCommitted = false;
                    var turnSink = new CallbackSink(async (observation, token) =>
                    {
                        if (observation is AssistantMessageEnded assistant)
                        {
                            if (assistantCommitted) throw new InvalidOperationException("A turn committed its assistant twice.");
                            var resultSlots = assistant.Message.StopReason is StopReason.Error or StopReason.Aborted ? 0 :
                                assistant.Message.Content.Count(value => value is ToolCallContent);
                            // Reserve all potential results before scheduler admission; no successful batch loses a result to this bound.
                            if ((long)transcript.Length + 1 + resultSlots > _options.MaximumTranscriptMessages)
                                throw new LoopLimit(assistant.Message);
                            transcript = transcript.Add(new("assistant", PiWireJson.WriteMessage(assistant.Message)));
                            assistantCommitted = true;
                        }
                        await sink.EmitAsync(observation, token).ConfigureAwait(false);
                    });
                    var result = settleAbort ?
                        await requestRunner.RunWithAbortSettlementAsync(request, turnSink, cancellationToken, toolTimestamp).ConfigureAwait(false) :
                        await requestRunner.RunAsync(request, turnSink, cancellationToken).ConfigureAwait(false);
                    if (!assistantCommitted) throw new InvalidOperationException("The completed turn omitted its assistant commit.");
                    var toolResults = result.Tools.Messages.Select(message => ToolEntry(message, toolTimestamp)).ToImmutableArray();
                    transcript = transcript.AddRange(toolResults);
                    lastTurn = new(turns.Count, result, transcript, toolResults);
                    turns.Add(lastTurn);
                    if (callbacks.FinishTurn is not null)
                        await callbacks.FinishTurn(lastTurn, cancellationToken).ConfigureAwait(false);
                    if (!settleAbort) cancellationToken.ThrowIfCancellationRequested();
                    var decision = callbacks.FinishTurnDecision is null ? AgentLoopFinishAction.Default :
                        await callbacks.FinishTurnDecision(lastTurn, cancellationToken).ConfigureAwait(false);
                    if (!settleAbort) cancellationToken.ThrowIfCancellationRequested();
                    var failed = result.Chat.Failure is not null || result.Chat.Message.StopReason is StopReason.Error or StopReason.Aborted || result.Tools.IsCanceled ||
                        (settleAbort && cancellationToken.IsCancellationRequested);
                    // The source awaits finishTurn but ignores its decision on failed/aborted responses.
                    if (!failed && decision is not (AgentLoopFinishAction.Default or AgentLoopFinishAction.End or AgentLoopFinishAction.Continue))
                        throw new InvalidOperationException("Finish-turn decision is unsupported.");
                    await sink.EmitAsync(new AgentLoopTurnEnded(lastTurn), deliveryToken).ConfigureAwait(false);
                    if (failed)
                        return await EndAsync(AgentLoopStopReason.ChatFailure).ConfigureAwait(false);
                    if (decision == AgentLoopFinishAction.End)
                        return await EndAsync(AgentLoopStopReason.Completed).ConfigureAwait(false);
                    explicitContinuation = decision == AgentLoopFinishAction.Continue;
                    needsRequest = result.Tools.ShouldContinue;
                    pending = await PollAsync(callbacks.GetSteeringMessages).ConfigureAwait(false);
                    if (needsRequest || pending.Length > 0) explicitContinuation = false;
                }
                pending = await PollAsync(callbacks.GetFollowUpMessages).ConfigureAwait(false);
                if (pending.Length > 0)
                {
                    explicitContinuation = false;
                    continue;
                }
                if (explicitContinuation)
                {
                    // A naturally selected request satisfies Continue; only its absence adds one context-only turn.
                    explicitContinuation = false;
                    needsRequest = true;
                    continue;
                }
                return await EndAsync(AgentLoopStopReason.Completed).ConfigureAwait(false);
            }
        }
        catch (LoopLimit limit)
        {
            return await EndAsync(AgentLoopStopReason.TranscriptLimit, limit.Assistant).ConfigureAwait(false);
        }

        async ValueTask<ImmutableArray<TranscriptEntry>> PrepareBoundaryAsync(ImmutableArray<TranscriptEntry> history, ImmutableArray<TranscriptEntry> inputs, bool initial)
        {
            if (callbacks.PrepareRequestBoundary is not { } prepare || cancellationToken.IsCancellationRequested) return inputs;
            AgentLoopRequestPreparation? prepared;
            try { prepared = await prepare(new(new(turns.Count, _model, history), inputs), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (settleAbort && cancellationToken.IsCancellationRequested) { return inputs; }
            if (prepared is null) return inputs;
            requestRunner = prepared.Runner;
            transcript = initial ? history.AddRange(prepared.AdditionalSystemMessages).AddRange(prepared.PendingInputs)
                : history.AddRange(prepared.AdditionalSystemMessages);
            return prepared.PendingInputs;
        }

        async ValueTask EmitInputAsync(TranscriptEntry message)
        {
            await sink.EmitAsync(new AgentLoopInputMessageStarted(message), deliveryToken).ConfigureAwait(false);
            await sink.EmitAsync(new AgentLoopInputMessageEnded(message), deliveryToken).ConfigureAwait(false);
            if (!settleAbort) cancellationToken.ThrowIfCancellationRequested();
        }

        async ValueTask<ImmutableArray<TranscriptEntry>> PollAsync(
            Func<CancellationToken, ValueTask<ImmutableArray<TranscriptEntry>>>? callback)
        {
            if (settleAbort && cancellationToken.IsCancellationRequested) return [];
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<TranscriptEntry> messages;
            try { messages = callback is null ? [] : await callback(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (settleAbort && cancellationToken.IsCancellationRequested) { return []; }
            if (!settleAbort) cancellationToken.ThrowIfCancellationRequested();
            ValidateInputs(messages);
            return messages;
        }

        async Task<AgentLoopResult> EndAsync(AgentLoopStopReason reason, AssistantMessage? rejectedAssistant = null)
        {
            var result = new AgentLoopResult(reason, transcript, turns.ToImmutable(), pending, rejectedAssistant);
            await sink.EmitAsync(new AgentLoopEnded(result), deliveryToken).ConfigureAwait(false);
            if (!settleAbort) cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }

    private static TranscriptEntry ToolEntry(ToolResultMessage message, long timestamp) => ToolResultMessageMaterializer.ToTranscript(message, timestamp);

    private static void ValidateInputs(ImmutableArray<TranscriptEntry> messages)
    {
        if (messages.IsDefault) throw new ArgumentException("Input messages must be initialized.", nameof(messages));
        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (message.Role is not ("system" or "user" or "custom") || message.WireBody is null ||
                message.WireBody.Value.ValueKind != JsonValueKind.Object ||
                !message.WireBody.Value.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String || role.GetString() != message.Role)
                throw new ArgumentException("Inputs must be owned system/user/custom messages with matching roles.", nameof(messages));
            if (message.Role == "custom") ValidateCustomMessage(message);
        }
    }

    /// <summary>Validate the supported native request message envelope without modifying canonical history.</summary>
    public static void ValidateRequestMessages(ImmutableArray<TranscriptEntry> messages,
        ToolResultValueOptions? valueLimits = null, int maximumMessages = 1024)
    {
        if (maximumMessages <= 0 || messages.IsDefault || messages.Length > maximumMessages)
            throw new ArgumentException("Invalid request message count.", nameof(messages));
        ValidateCanonicalHistory(messages, ToolResultValueCodec.ValidateLimits(valueLimits ?? ToolResultValueOptions.ExecutionBoundary));
        foreach (var message in messages)
            if (message.Role is "system" or "user") ValidateInputContent(message);
    }

    private static void ValidateCustomMessage(TranscriptEntry message)
    {
        var body = message.WireBody.Value;
        if (!body.TryGetProperty("customType", out var customType) || customType.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(customType.GetString()) ||
            !body.TryGetProperty("display", out var display) || display.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !body.TryGetProperty("timestamp", out var timestamp) || !timestamp.TryGetInt64(out _))
            throw new ArgumentException("Invalid custom history message.");
        ValidateInputContent(message);
    }

    private static ImmutableArray<TranscriptEntry> ProjectCustomMessages(ImmutableArray<TranscriptEntry> messages) =>
        messages.Select(message => message.Role != "custom" ? message : new TranscriptEntry("user",
            JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = CustomContent(message.WireBody.Value.GetProperty("content")),
                timestamp = message.WireBody.Value.GetProperty("timestamp") })))).ToImmutableArray();

    private static JsonElement CustomContent(JsonElement content) => content.ValueKind == JsonValueKind.String
        ? JsonSerializer.SerializeToElement(new[] { new { type = "text", text = content.GetString() } }) : content;

    private static void ValidateInputContent(TranscriptEntry message)
    {
        var body = message.WireBody.Value;
        if (!body.TryGetProperty("content", out var content)) throw new ArgumentException("Request input content is required.");
        if (content.ValueKind == JsonValueKind.String) return;
        if (content.ValueKind != JsonValueKind.Array) throw new ArgumentException("Request input content must be text or content blocks.");
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Invalid request input content block.");
            if (type.GetString() == "text")
            {
                if (!block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Invalid request input text.");
            }
            else if (message.Role is ("user" or "custom") && type.GetString() == "image")
            {
                if (!block.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String ||
                    !block.TryGetProperty("mimeType", out var mime) || mime.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Invalid request input image.");
            }
            else throw new ArgumentException("Unsupported request input content block.");
        }
    }

    private static void ValidateCanonicalHistory(ImmutableArray<TranscriptEntry> messages, ToolResultValueOptions valueLimits)
    {
        if (messages.IsDefault) throw new ArgumentException("Canonical history must be initialized.", nameof(messages));
        try
        {
            foreach (var message in messages)
            {
                if (message is null || message.WireBody is null || message.WireBody.Value.ValueKind != JsonValueKind.Object ||
                    !message.WireBody.Value.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String ||
                    role.GetString() != message.Role) throw new JsonException();
                if (message.Role is "system" or "user") continue;
                if (message.Role == "custom") { ValidateCustomMessage(message); continue; }
                if (message.Role == "assistant")
                {
                    var assistant = PiWireJson.ReadMessage(message.WireBody.Value);
                    if (assistant.StopReason is StopReason.Pending or StopReason.Deferred ||
                        string.IsNullOrWhiteSpace(assistant.Api) || string.IsNullOrWhiteSpace(assistant.Provider) ||
                        string.IsNullOrWhiteSpace(assistant.Model)) throw new JsonException();
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var call in assistant.Content.OfType<ToolCallContent>())
                        if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name) || !ids.Add(call.Id))
                            throw new JsonException();
                }
                else if (message.Role == "toolResult")
                {
                    var body = message.WireBody.Value;
                    if (string.IsNullOrWhiteSpace(body.GetProperty("toolCallId").GetString()) ||
                        string.IsNullOrWhiteSpace(body.GetProperty("toolName").GetString())) throw new JsonException();
                    _ = body.GetProperty("timestamp").GetInt64();
                    _ = body.GetProperty("isError").GetBoolean();
                    if (body.GetProperty("content").ValueKind != JsonValueKind.Array) throw new JsonException();
                    // Fixed message identity fields do not narrow the admitted result quota.
                    var retained = body.EnumerateObject()
                        .Where(property => property.Name is not ("role" or "toolCallId" or "toolName" or "timestamp" or "isError"))
                        .ToImmutableDictionary(property => property.Name, property => (object?)JsonData.FromElement(property.Value), StringComparer.Ordinal);
                    ToolResultValueCodec.Validate(ToolResult.FromProperties(retained), valueLimits);
                }
                else throw new JsonException();
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new ArgumentException("Canonical history must contain owned system/user, finalized assistant or admitted text/image toolResult messages with matching roles.", nameof(messages));
        }
    }

    private static void ValidateRequest(ChatRequest request, AgentLoopSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateModel(request.Model);
        if (request.Model != snapshot.Model || request.Messages.IsDefault || request.Messages.Length != snapshot.Transcript.Length)
            throw new ArgumentException("Request preparation must retain the fixed model and complete canonical transcript.", nameof(request));
        for (var index = 0; index < request.Messages.Length; index++)
        {
            var candidate = request.Messages[index];
            var original = snapshot.Transcript[index];
            if (candidate is null || candidate.Role != original.Role || candidate.WireBody is null ||
                candidate.WireBody.Value.GetRawText() != original.WireBody.Value.GetRawText())
                throw new ArgumentException("Request preparation must not rewrite, reorder or omit canonical messages.", nameof(request));
        }
    }

    private static void ValidateModel(ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Api);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Provider);
    }

    private sealed class CallbackSink(Func<AgentEvent, CancellationToken, ValueTask> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken) => emit(observation, cancellationToken); }
    private sealed class LoopLimit(AssistantMessage? assistant = null) : Exception
    { public AssistantMessage? Assistant { get; } = assistant; }
}
