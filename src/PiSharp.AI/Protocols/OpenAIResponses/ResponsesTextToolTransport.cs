using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts.Compatibility;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAIResponses;

internal sealed class ResponsesFailureContext
{
    internal Exception? SourceException;
    internal Task? SourceTask;
    internal string? DisplayMessage;
    internal readonly List<Exception> CleanupExceptions = [];
    internal readonly List<Task> CleanupTasks = [];
    internal async ValueTask<T> Source<T>(Func<ValueTask<T>> invoke)
    {
        Task<T>? original = null;
        try { original = invoke().AsTask(); return await original.ConfigureAwait(false); }
        catch (Exception error) { SourceException ??= error; SourceTask ??= original; throw; }
    }
    internal async ValueTask Source(Func<ValueTask> invoke)
    {
        Task? original = null;
        try { original = invoke().AsTask(); await original.ConfigureAwait(false); }
        catch (Exception error) { SourceException ??= error; SourceTask ??= original; throw; }
    }
    internal async ValueTask Cleanup(Func<ValueTask> invoke)
    {
        Task? original = null;
        try { original = invoke().AsTask(); await original.ConfigureAwait(false); }
        catch (Exception error) { CleanupExceptions.Add(error); if (original is not null) CleanupTasks.Add(original); }
    }
    internal void Cleanup(Action invoke)
    {
        try { invoke(); } catch (Exception error) { CleanupExceptions.Add(error); }
    }
}

/// <summary>Caller-supplied rates per million tokens; no provider catalog or service-tier adjustment.</summary>
public sealed record ResponsesTokenRateTier(decimal InputTokensAbove, decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite);
public sealed record ResponsesTokenRates(decimal Input = 0, decimal Output = 0, decimal CacheRead = 0, decimal CacheWrite = 0)
{ public ImmutableArray<ResponsesTokenRateTier> Tiers { get; init; } = []; }

public sealed record ResponsesTextToolOptions(
    int MaximumEvents = 4096, int MaximumEventCharacters = 65_536,
    int MaximumInputCharacters = PiRequestBudget.StreamCharacters, int MaximumContentSlots = 64,
    int MaximumContentCharacters = PiRequestBudget.StreamCharacters, int MaximumJsonDepth = 32,
    ResponsesTokenRates? Rates = null, string? ServiceTier = null)
{
    /// <summary>Model compat <c>supportsOpenAIGrammarTools</c>: selects each custom tool call's grammar input property.</summary>
    public bool SupportsOpenAIGrammarTools { get; init; }
}

/// <summary>A bounded parsed-DTO adapter. Owns the returned source enumerator, never performs HTTP or executes tools.</summary>
public sealed class ResponsesTextToolTransport : IChatTransport
{
    private readonly Func<ChatRequest, CancellationToken, IAsyncEnumerable<JsonData>> _source;
    private readonly Func<ChatRequest, CancellationToken, ResponsesFailureContext, ValueTask<IAsyncEnumerator<JsonData>>>? _prepareSource;
    private readonly ResponsesTextToolOptions _options;
    private readonly ResponsesTokenRates _rates;
    private static readonly JsonSerializerOptions SignatureJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public ResponsesTextToolTransport(Func<ChatRequest, CancellationToken, IAsyncEnumerable<JsonData>> source,
        ResponsesTextToolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _options = options ?? new();
        _rates = _options.Rates ?? new();
        if (!ResponsesServiceTier.Supported(_options.ServiceTier))
            throw new ArgumentException("Unsupported Responses service tier.", nameof(options));
        if (_options.MaximumEvents <= 0 || _options.MaximumEventCharacters <= 0 || _options.MaximumInputCharacters <= 0 ||
            _options.MaximumContentSlots <= 0 || _options.MaximumContentCharacters <= 0 || _options.MaximumJsonDepth is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options), "Responses limits must be positive; JSON depth must be at most 64.");
        if (_rates.Input < 0 || _rates.Output < 0 || _rates.CacheRead < 0 || _rates.CacheWrite < 0 ||
            _rates.Tiers.IsDefault || _rates.Tiers.Any(tier => tier is null || tier.Input < 0 || tier.Output < 0 || tier.CacheRead < 0 || tier.CacheWrite < 0))
            throw new ArgumentOutOfRangeException(nameof(options), "Responses token rates must be nonnegative.");
    }

    // HTTP acquisition is distinct from the first DTO pull: Start must not wait for SSE bytes.
    internal ResponsesTextToolTransport(Func<ChatRequest, CancellationToken, ResponsesFailureContext, ValueTask<IAsyncEnumerator<JsonData>>> prepareSource,
        ResponsesTextToolOptions? options)
        : this((Func<ChatRequest, CancellationToken, IAsyncEnumerable<JsonData>>)((_, _) => throw Protocol()), options)
    {
        ArgumentNullException.ThrowIfNull(prepareSource);
        _prepareSource = prepareSource;
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = new State(request, _options, _rates);
        var context = new ResponsesFailureContext();
        Exception? sourceFailure = null;
        IAsyncEnumerator<JsonData>? enumerator = null;
        var drained = false;
        // No terminal is exposed until all original callback, pull and cleanup operations settle.
        try
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                enumerator = _prepareSource is { } prepare
                    ? await context.Source(() => prepare(request, cancellationToken, context)).ConfigureAwait(false)
                    : (_source(request, cancellationToken) ?? throw Protocol()).GetAsyncEnumerator(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception failure) { sourceFailure = failure; context.SourceException ??= failure; }
            if (sourceFailure is null) yield return state.Start;
            while (sourceFailure is null)
            {
                List<StreamEvent> events;
                try
                {
                    if (!await context.Source(() => enumerator!.MoveNextAsync()).ConfigureAwait(false)) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    events = state.Process(enumerator!.Current);
                }
                catch (Exception failure) { sourceFailure = failure; context.SourceException ??= failure; break; }
                foreach (var progress in events)
                {
                    yield return progress;
                }
                if (state.ProviderFailed) break;
            }
            // Once input is exhausted or failed, no later terminal can backfill these
            // valid done items. Preserve their authoritative state before reporting failure.
            if (!cancellationToken.IsCancellationRequested)
            {
                List<StreamEvent> pending = [];
                try { pending = state.EndPendingThinking(); }
                catch (Exception failure) { sourceFailure ??= failure; context.SourceException ??= failure; }
                foreach (var progress in pending)
                {
                    yield return progress;
                }
            }
            drained = true;
        }
        finally
        {
            if (enumerator is not null) await context.Cleanup(() => enumerator.DisposeAsync()).ConfigureAwait(false);
            if (!drained && context.CleanupExceptions.Count != 0)
                throw new AggregateException("Responses early disposal failed.", context.CleanupExceptions);
        }
        StreamTerminalEvent terminal;
        if (sourceFailure is null && context.CleanupExceptions.Count == 0 && !cancellationToken.IsCancellationRequested)
        {
            try { terminal = state.Finish(); }
            catch (Exception failure) { context.SourceException ??= failure; terminal = state.Error(StopReason.Error, FailureMessage(request, failure, context)); }
        }
        else terminal = state.Error(cancellationToken.IsCancellationRequested ? StopReason.Aborted : StopReason.Error,
            cancellationToken.IsCancellationRequested && sourceFailure is null && context.CleanupExceptions.Count == 0 ? "Request was aborted" :
                FailureMessage(request, sourceFailure ?? context.CleanupExceptions[0], context));
        yield return terminal with { NativeSourceException = context.SourceException, NativeSourceTask = context.SourceTask,
            NativeCleanupExceptions = context.CleanupExceptions.Count == 0 ? null : context.CleanupExceptions.ToArray(),
            NativeCleanupTasks = context.CleanupTasks.Count == 0 ? null : context.CleanupTasks.ToArray() };
    }

    private static string FailureMessage(ChatRequest request, Exception failure, ResponsesFailureContext context)
    {
        var message = context.DisplayMessage ?? (failure is HttpRequestException { StatusCode: { } status }
            ? $"{(request.Model.Provider == "openai" ? "OpenAI" : request.Model.Provider)} API error ({(int)status}): {failure.Message}" : failure.Message);
        return DisplayError(message);
    }
    private static string DisplayError(string message) => message.Contains("subscription_sharing_usage_limit_exceeded", StringComparison.Ordinal)
        ? message + "\nCheck your ChatGPT usage: https://chatgpt.com/settings/usage" : message;

    private static StreamProtocolException Protocol() => new("Invalid or unsupported Responses text/tool stream.");
    private static StreamLimitException Limit() => new("Responses text/tool stream exceeds configured limits.");
    private static JsonData StringData(string value) => JsonData.Parse(JsonSerializer.Serialize(value, SignatureJson));

    // JSON.stringify retains all valid non-control Unicode, unlike the framework encoder's blocklist.
    // DTO admission rejects unpaired surrogates before this helper is called.
    private static string Signature(string id, string? phase)
    {
        var builder = new StringBuilder("{\"v\":1,\"id\":");
        Quote(builder, id);
        if (!string.IsNullOrEmpty(phase)) { builder.Append(",\"phase\":"); Quote(builder, phase); }
        return builder.Append('}').ToString();
    }
    private static string Quoted(string value) { var builder = new StringBuilder(); Quote(builder, value); return builder.ToString(); }
    private static void Quote(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < 0x20) builder.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else builder.Append(character);
                    break;
            }
        builder.Append('"');
    }

    private sealed class Slot(int index, string kind, string itemId, string? callId = null, string? name = null)
    {
        // constrained-sampling.ts GrammarToolInputJsonBuffer for custom_tool_call input.
        public string? CustomProperty;
        public string CustomInput = "";
        public string BufferInput = "";
        public bool BufferStarted, BufferClosed;
        public readonly int Index = index;
        public readonly string Kind = kind;
        public readonly string ItemId = itemId;
        public readonly string? CallId = callId;
        public readonly string? Name = name;
        public bool Ended;
        public bool ThinkingEnded;
        public JsonData? ReasoningItem;
        public string? FinalThinking;
    }

    private sealed class State
    {
        private readonly ResponsesTextToolOptions _options;
        private readonly ResponsesTokenRates _rates;
        private readonly string _modelId;
        private readonly ChatRequest _request;
        private Dictionary<string, string>? _grammarInputs;
        private readonly AssistantStreamReducer _reducer;
        private readonly Dictionary<int, Slot> _slots = [];
        private readonly Dictionary<string, Slot> _reasoningById = new(StringComparer.Ordinal);
        private JsonFields _properties = JsonFields.Empty;
        private TokenUsage _usage = TokenUsage.Zero;
        private int _events;
        private long _inputCharacters;
        private bool _completed;
        private bool _incomplete;
        public bool ProviderFailed { get; private set; }
        private StopReason _stopReason = StopReason.Stop;
        public StreamStarted Start { get; }

        public State(ChatRequest request, ResponsesTextToolOptions options, ResponsesTokenRates rates)
        {
            _options = options;
            _modelId = request.Model.Id;
            _request = request;
            _rates = rates;
            var message = new AssistantMessage(request.Model.Api, request.Model.Provider, request.Model.Id,
                request.Timestamp, [], TokenUsage.Zero, StopReason.Pending);
            Start = new(message);
            _reducer = new(message, new(options.MaximumContentSlots, options.MaximumContentCharacters));
            _reducer.Apply(Start);
        }

        public List<StreamEvent> Process(JsonData data)
        {
            try { return ProcessCore(data); }
            catch (JsonException) { throw Protocol(); }
            catch (InvalidOperationException) { throw Protocol(); }
            catch (KeyNotFoundException) { throw Protocol(); }
            catch (OverflowException) { throw Protocol(); }
            catch (FormatException) { throw Protocol(); }
            catch (EcmaScriptJsonProjectionException error)
            {
                if (error.Failure == EcmaScriptJsonProjectionFailure.ResourceLimit) throw Limit();
                throw Protocol();
            }
        }

        private List<StreamEvent> ProcessCore(JsonData data)
        {
            if (data is null) throw Protocol();
            var value = data.Value;
            var length = value.GetRawText().Length;
            if (++_events > _options.MaximumEvents || length > _options.MaximumEventCharacters ||
                length > _options.MaximumInputCharacters - _inputCharacters) throw Limit();
            _inputCharacters += length;
            CheckJson(value, 0);
            Object(value);
            var type = String(value, "type");
            var events = new List<StreamEvent>(2);
            void Emit(StreamEvent progress) { _reducer.Apply(progress); events.Add(progress); }
            if (_completed) throw Protocol();
            switch (type)
            {
                case "response.created":
                    var created = Object(value.GetProperty("response"));
                    _properties = _properties.Set("responseId", StringData(String(created, "id")));
                    break;
                case "response.in_progress":
                case "response.queued":
                    // Only lifecycle bookkeeping with no embedded output is in this ignored subset.
                    var metadata = Object(value.GetProperty("response"));
                    if (metadata.TryGetProperty("output", out var output) &&
                        (output.ValueKind != JsonValueKind.Array || output.GetArrayLength() != 0)) throw Protocol();
                    break;
                case "response.output_item.added":
                    Add(Index(value), Object(value.GetProperty("item")), Emit);
                    break;
                case "response.output_text.delta":
                case "response.refusal.delta":
                    var text = Active(value, "message");
                    Emit(new TextDelta(text.Index, String(value, "delta", allowEmpty: true)));
                    break;
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    var thinking = Active(value, "reasoning");
                    Emit(new ThinkingDelta(thinking.Index, String(value, "delta", allowEmpty: true)));
                    break;
                case "response.reasoning_summary_part.done":
                    var summary = Active(value, "reasoning");
                    Emit(new ThinkingDelta(summary.Index, "\n\n"));
                    break;
                case "response.reasoning_summary_part.added":
                case "response.reasoning_summary_text.done":
                case "response.reasoning_text.done":
                    // The pinned mapper obtains authoritative text from output_item.done.
                    break;
                case "response.function_call_arguments.delta":
                    var tool = Active(value, "function_call");
                    Emit(new ToolCallDelta(tool.Index, String(value, "delta", allowEmpty: true)));
                    break;
                case "response.function_call_arguments.done":
                    var argsSlot = Active(value, "function_call");
                    var arguments = String(value, "arguments", allowEmpty: true);
                    var previous = _reducer.GetToolJsonPreview(argsSlot.Index);
                    if (arguments.StartsWith(previous, StringComparison.Ordinal))
                    {
                        if (arguments.Length > previous.Length) Emit(new ToolCallDelta(argsSlot.Index, arguments[previous.Length..]));
                    }
                    else Emit(new ToolCallCheckpoint(argsSlot.Index, arguments));
                    break;
                case "response.custom_tool_call_input.delta":
                    var custom = Active(value, "custom_tool_call");
                    CustomInput(custom, custom.CustomInput + String(value, "delta", allowEmpty: true), false, Emit);
                    break;
                case "response.custom_tool_call_input.done":
                    var customDone = Active(value, "custom_tool_call");
                    CustomInput(customDone, String(value, "input", allowEmpty: true), true, Emit);
                    break;
                case "response.output_item.done":
                    var item = Object(value.GetProperty("item"));
                    var outputIndex = Index(value);
                    if (!_slots.TryGetValue(outputIndex, out var slot)) slot = Add(outputIndex, item, Emit);
                    if (slot.Ended || String(item, "type") != slot.Kind || String(item, "id") != slot.ItemId) throw Protocol();
                    if (slot.Kind == "reasoning")
                    {
                        var summaryText = ReasoningText(item, "summary");
                        var contentText = ReasoningText(item, "content");
                        slot.FinalThinking = summaryText.Length > 0 ? summaryText : contentText.Length > 0 ? contentText :
                            ((ThinkingContent)_reducer.Snapshot().Content[slot.Index]).Thinking;
                        slot.ReasoningItem = JsonData.FromElement(item);
                        _reasoningById[slot.ItemId] = slot;
                        // Nonempty encryption is final: Source never replaces it during backfill.
                        // Only incomplete signatures must wait for the immutable native end.
                        if (!string.IsNullOrEmpty(OptionalStringOrNull(item, "encrypted_content"))) EndThinking(slot, Emit);
                        else Emit(new ThinkingCheckpoint(slot.Index, slot.FinalThinking!, ReasoningProperties(slot)));
                    }
                    else if (slot.Kind == "message")
                    {
                        var contents = item.GetProperty("content");
                        if (contents.ValueKind != JsonValueKind.Array) throw Protocol();
                        var pieces = new List<string>();
                        foreach (var part in contents.EnumerateArray())
                        {
                            var partType = String(Object(part), "type");
                            pieces.Add(partType switch
                            {
                                "output_text" => String(part, "text", allowEmpty: true),
                                "refusal" => String(part, "refusal", allowEmpty: true),
                                _ => throw Protocol()
                            });
                        }
                        var signature = Signature(slot.ItemId, OptionalString(item, "phase"));
                        Emit(new TextEnded(slot.Index, string.Concat(pieces), JsonFields.Empty.Set("textSignature", StringData(signature))));
                    }
                    else if (slot.Kind == "custom_tool_call")
                    {
                        if (String(item, "call_id") != slot.CallId || String(item, "name") != slot.Name) throw Protocol();
                        CustomInput(slot, OptionalStringOrNull(item, "input") ?? slot.CustomInput, true, Emit);
                        var started = (ToolCallContent)_reducer.Snapshot().Content[slot.Index];
                        var customFields = started.ExtraProperties ?? JsonFields.Empty;
                        if (OptionalString(item, "namespace") is { } customNamespace) customFields = customFields.Set("namespace", StringData(customNamespace));
                        var input = new JsonObject { [slot.CustomProperty!] = slot.CustomInput };
                        Emit(new ToolCallEnded(slot.Index, new(started.Id, started.Name, JsonData.Parse(input.ToJsonString(SignatureJson)), customFields)));
                    }
                    else
                    {
                        if (String(item, "call_id") != slot.CallId || String(item, "name") != slot.Name) throw Protocol();
                        var raw = OptionalString(item, "arguments");
                        if (string.IsNullOrEmpty(raw)) raw = _reducer.GetToolJsonPreview(slot.Index);
                        if (raw.Length == 0) raw = "{}";
                        // openai-responses-shared.ts output_item.done: parseStreamingJson(item.arguments || partialJson || "{}").
                        JsonData final;
                        try { final = StreamingJson.Parse(raw); }
                        catch (JsonException) { throw Protocol(); }
                        var original = (ToolCallContent)_reducer.Snapshot().Content[slot.Index];
                        var fields = original.ExtraProperties ?? JsonFields.Empty;
                        if (OptionalString(item, "namespace") is { } ns) fields = fields.Set("namespace", StringData(ns));
                        Emit(new ToolCallEnded(slot.Index, new(original.Id, original.Name, final, fields)));
                    }
                    slot.Ended = true;
                    break;
                case "error":
                    ProviderFailure("Error Code " + String(value, "code", allowEmpty: true) + ": " + String(value, "message", allowEmpty: true));
                    break;
                case "response.failed":
                    var failed = Object(value.GetProperty("response"));
                    if (OptionalStringOrNull(failed, "status") is { } failedStatus)
                        _properties = _properties.Set("rawStopReason", StringData(failedStatus));
                    string failureMessage;
                    if (failed.TryGetProperty("error", out var providerError) && providerError.ValueKind != JsonValueKind.Null)
                    {
                        Object(providerError);
                        var code = OptionalStringOrNull(providerError, "code");
                        var message = OptionalStringOrNull(providerError, "message");
                        failureMessage = (string.IsNullOrEmpty(code) ? "unknown" : code) + ": " +
                            (string.IsNullOrEmpty(message) ? "no message" : message);
                    }
                    else if (failed.TryGetProperty("incomplete_details", out var failedDetails) && failedDetails.ValueKind != JsonValueKind.Null &&
                        OptionalStringOrNull(Object(failedDetails), "reason") is { Length: > 0 } failedReason)
                        failureMessage = "incomplete: " + failedReason;
                    else failureMessage = "Unknown error (no error details in response)";
                    ProviderFailure(failureMessage);
                    break;
                case "response.completed":
                case "response.incomplete":
                    var response = Object(value.GetProperty("response"));
                    _incomplete = type == "response.incomplete";
                    var status = _incomplete ? "incomplete" : "completed";
                    if (String(response, "status") != status) throw Protocol();
                    var incompleteReason = _incomplete ? IncompleteReason(response) : null;
                    if (response.TryGetProperty("output", out var finalOutput) &&
                        (finalOutput.ValueKind != JsonValueKind.Array || finalOutput.GetArrayLength() != 0))
                    {
                        foreach (var finalItem in finalOutput.EnumerateArray())
                        {
                            var kind = String(Object(finalItem), "type");
                            if (kind == "reasoning") BackfillReasoning(finalItem);
                            else if (kind is not ("message" or "function_call" or "custom_tool_call")) throw Protocol();
                        }
                    }
                    if (OptionalString(response, "id") is { Length: > 0 } id) _properties = _properties.Set("responseId", StringData(id));
                    _properties = _properties.Set("rawStopReason", StringData(status + (string.IsNullOrEmpty(incompleteReason) ? "" : "." + incompleteReason)));
                    _stopReason = !_incomplete ? StopReason.Stop : incompleteReason == "max_output_tokens" ? StopReason.Length : StopReason.Error;
                    if (_stopReason == StopReason.Error)
                        _properties = _properties.Set("errorMessage", StringData(!string.IsNullOrEmpty(incompleteReason) ?
                            "Response incomplete: " + incompleteReason : "Response incomplete without a provider reason"));
                    if (response.TryGetProperty("usage", out var usage)) _usage = Usage(Object(usage));
                    ApplyServiceTier(OptionalStringOrNull(response, "service_tier") ?? _options.ServiceTier);
                    foreach (var finished in _slots.Values.Where(s => s.Kind == "reasoning" && s.Ended && !s.ThinkingEnded))
                        EndThinking(finished, Emit);
                    _completed = true;
                    break;
                // openai-responses-shared.ts processResponsesStream: other event types (response.content_part.added,
                // response.output_text.done, response.content_part.done, ...) fall through its if/else chain and are ignored.
                default: break;
            }
            return events;
        }

        private void ProviderFailure(string message)
        {
            _properties = _properties.Set("errorMessage", StringData(DisplayError(message)));
            _stopReason = StopReason.Error;
            ProviderFailed = true;
            _completed = true;
        }

        private static void EndThinking(Slot slot, Action<StreamEvent> emit)
        {
            emit(new ThinkingEnded(slot.Index, slot.FinalThinking!, ReasoningProperties(slot)));

            slot.ThinkingEnded = true;
        }
        private static JsonFields ReasoningProperties(Slot slot)
        {
            // Finalization also runs after EOF/read failure, outside Process's translation.
            try { return JsonFields.Empty.Set("thinkingSignature", StringData(EcmaScriptJsonProjection.Project(slot.ReasoningItem!))); }
            catch (EcmaScriptJsonProjectionException error)
            {
                if (error.Failure == EcmaScriptJsonProjectionFailure.ResourceLimit) throw Limit();
                throw Protocol();
            }
        }
        // Pi preserves any provider string reason; normal DTO limits still apply.
        private static string? IncompleteReason(JsonElement response)
        {
            if (!response.TryGetProperty("incomplete_details", out var details) || details.ValueKind == JsonValueKind.Null) return null;
            Object(details);
            return OptionalStringOrNull(details, "reason");
        }
        private static string ReasoningText(JsonElement item, string field)
        {
            if (!item.TryGetProperty(field, out var values) || values.ValueKind == JsonValueKind.Null) return "";
            if (values.ValueKind != JsonValueKind.Array) throw Protocol();
            return string.Join("\n\n", values.EnumerateArray().Select(part => String(Object(part), "text", allowEmpty: true)));
        }

        private void BackfillReasoning(JsonElement item)
        {
            var id = String(item, "id");
            var encrypted = OptionalStringOrNull(item, "encrypted_content");
            if (string.IsNullOrEmpty(encrypted) || !_reasoningById.TryGetValue(id, out var slot) || slot.ReasoningItem is null) return;
            if (!string.IsNullOrEmpty(OptionalStringOrNull(slot.ReasoningItem.Value, "encrypted_content"))) return;
            var merged = JsonNode.Parse(slot.ReasoningItem.ToString())!.AsObject();
            merged["encrypted_content"] = encrypted;
            slot.ReasoningItem = JsonData.Parse(merged.ToJsonString());
        }

        private static string? OptionalStringOrNull(JsonElement value, string name) =>
            value.TryGetProperty(name, out var field) && field.ValueKind != JsonValueKind.Null ? String(value, name, allowEmpty: true) : null;
        private Slot Add(int outputIndex, JsonElement item, Action<StreamEvent> emit)
        {
            if (_slots.ContainsKey(outputIndex)) throw Protocol();
            if (_slots.Count >= _options.MaximumContentSlots) throw Limit();
            var kind = String(item, "type");
            var itemId = String(item, "id");
            var call = kind is "function_call" or "custom_tool_call";
            var slot = new Slot(_slots.Count, kind, itemId, call ? String(item, "call_id") : null, call ? String(item, "name") : null);
            if (kind == "reasoning") emit(new ThinkingStarted(slot.Index, new ThinkingContent("")));
            else if (kind == "message") emit(new TextStarted(slot.Index, new TextContent("")));
            else if (kind == "function_call")
            {
                var fields = JsonFields.Empty;
                if (OptionalString(item, "namespace") is { } ns) fields = fields.Set("namespace", StringData(ns));
                emit(new ToolCallStarted(slot.Index, new(slot.CallId + "|" + itemId, slot.Name!, JsonData.EmptyObject, fields)));
                if (OptionalString(item, "arguments") is { Length: > 0 } seed) emit(new ToolCallCheckpoint(slot.Index, seed));
            }
            else if (kind == "custom_tool_call")
            {
                // Pi abe508 openai-responses-shared.ts: the input property of the declared grammar tool, else "input".
                _grammarInputs ??= ResponsesGrammar.InputProperties(_request, _options.SupportsOpenAIGrammarTools, CancellationToken.None);
                slot.CustomProperty = _grammarInputs.TryGetValue(slot.Name!, out var property) ? property : "input";
                slot.CustomInput = OptionalStringOrNull(item, "input") ?? "";
                var fields = JsonFields.Empty;
                if (OptionalString(item, "namespace") is { } ns) fields = fields.Set("namespace", StringData(ns));
                emit(new ToolCallStarted(slot.Index, new(slot.CallId + "|" + itemId, slot.Name!, JsonData.EmptyObject, fields)));
            }
            else throw Protocol();
            _slots.Add(outputIndex, slot);
            return slot;
        }

        // constrained-sampling.ts appendGrammarToolInputJsonDelta: stream the raw input as the JSON text {"<property>":"<input>"}.
        private static void CustomInput(Slot slot, string next, bool close, Action<StreamEvent> emit)
        {
            if (slot.BufferClosed)
            {
                if (close && next == slot.BufferInput) return;
                throw Protocol();
            }
            if (!next.StartsWith(slot.BufferInput, StringComparison.Ordinal)) throw Protocol();
            var inputDelta = next[slot.BufferInput.Length..];
            slot.CustomInput = next;
            if (!close && inputDelta.Length == 0) return;
            var delta = new StringBuilder();
            if (!slot.BufferStarted) { delta.Append('{').Append(Quoted(slot.CustomProperty!)).Append(":\""); slot.BufferStarted = true; }
            delta.Append(Quoted(inputDelta)[1..^1]);
            slot.BufferInput = next;
            if (close) { delta.Append("\"}"); slot.BufferClosed = true; }
            emit(new ToolCallDelta(slot.Index, delta.ToString()));
        }

        private Slot Active(JsonElement value, string kind)
        {
            if (!_slots.TryGetValue(Index(value), out var slot) || slot.Ended || slot.Kind != kind) throw Protocol();
            if (value.TryGetProperty("item_id", out _) && String(value, "item_id") != slot.ItemId) throw Protocol();
            return slot;
        }

        public List<StreamEvent> EndPendingThinking()
        {
            var events = new List<StreamEvent>();
            foreach (var slot in _slots.Values.Where(s => s.Kind == "reasoning" && s.Ended && !s.ThinkingEnded))
                EndThinking(slot, progress => { _reducer.Apply(progress); events.Add(progress); });
            return events;
        }
        public StreamTerminalEvent Finish()
        {
            if (!_completed) throw new StreamProtocolException("Responses stream ended before a supported terminal response.");
            var unfinished = _slots.Values.Any(slot => !slot.Ended);
            if (!_incomplete && !ProviderFailed && unfinished)
                throw new StreamProtocolException("Responses stream completed with unfinished content.");
            if (_incomplete && _stopReason == StopReason.Length &&
                _slots.Values.Any(slot => !slot.Ended && slot.Kind != "message"))
            {
                // Preserve usage and partial content, but never invent authoritative ends.
                _stopReason = StopReason.Error;
                _properties = _properties.Set("errorMessage", StringData("Response incomplete with unfinished content."));
            }
            var message = _reducer.Snapshot() with
            {
                Usage = _usage, ExtraProperties = _properties,
                StopReason = _stopReason == StopReason.Stop && _slots.Values.Any(slot => slot.Kind is "function_call" or "custom_tool_call") ? StopReason.ToolUse : _stopReason
            };
            StreamTerminalEvent terminal = message.StopReason == StopReason.Error ?
                new StreamError(StopReason.Error, message) : new StreamDone(message.StopReason, message);
            _reducer.Apply(terminal);
            return terminal;
        }

        public StreamError Error(StopReason reason, string message)
        {
            _properties = _properties.Set("errorMessage", StringData(message));
            var snapshot = _reducer.Snapshot() with { Usage = _usage, ExtraProperties = _properties, StopReason = reason };
            return new(reason, snapshot);
        }
        private void ApplyServiceTier(string? tier)
        {
            var multiplier = ResponsesServiceTier.Multiplier(_modelId, tier);
            if (multiplier == 1) return;
            // openai-responses.ts applyServiceTierPricing multiplies the binary64 costs and sums them again.
            var cost = _usage.Cost; var factor = Number(multiplier);
            double input = Source(cost, "input") * factor, output = Source(cost, "output") * factor;
            double read = Source(cost, "cacheRead") * factor, write = Source(cost, "cacheWrite") * factor;
            _usage = _usage with { Cost = Binary64Cost(input, output, read, write, input + output + read + write) };
        }
        private TokenUsage Usage(JsonElement usage)
        {
            var input = Number(usage, "input_tokens");
            var output = Number(usage, "output_tokens");
            var total = Number(usage, "total_tokens");
            long cached = 0, written = 0, reasoning = 0;
            if (usage.TryGetProperty("input_tokens_details", out var details))
            {
                Object(details); cached = Number(details, "cached_tokens"); written = Number(details, "cache_write_tokens");
            }
            if (usage.TryGetProperty("output_tokens_details", out var outputDetails))
                reasoning = Number(Object(outputDetails), "reasoning_tokens");
            var uncached = Math.Max(0, checked(input - cached - written));
            // Pi abe508 models.ts calculateCost through the shared tier selection.
            var rates = PromptLengthPricing.TrySelect(_rates.Tiers, candidate => candidate.InputTokensAbove, (decimal)uncached, cached, written, out var tier)
                ? new ResponsesTokenRates(tier.Input, tier.Output, tier.CacheRead, tier.CacheWrite) : _rates;
            // models.ts calculateCost in binary64 Numbers: three divide-then-multiply terms, the cache write term multiplies
            // before dividing (no 1h writes here), and the total is left associative.
            var inputCost = Number(rates.Input) / 1_000_000d * uncached;
            var outputCost = Number(rates.Output) / 1_000_000d * output;
            var cachedCost = Number(rates.CacheRead) / 1_000_000d * cached;
            var writtenCost = (Number(rates.CacheWrite) * written + Number(rates.Input) * 2d * 0d) / 1_000_000d;
            return new(uncached, output, cached, written, total,
                Binary64Cost(inputCost, outputCost, cachedCost, writtenCost, ((inputCost + outputCost) + cachedCost) + writtenCost),
                JsonFields.Empty.Set("reasoning", JsonData.Parse(reasoning.ToString(System.Globalization.CultureInfo.InvariantCulture)))) { ExtrasBeforeTotal = true };
        }

        // A decimal rate as the Number JSON.parse reads from its text.
        private static double Number(decimal value) => double.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
        private static double Source(UsageCost cost, string name) => cost.SourceBinary64Cost is { } source
            ? source.Value.GetProperty(name).GetDouble()
            : Number(name switch { "input" => cost.Input, "output" => cost.Output, "cacheRead" => cost.CacheRead, _ => cost.CacheWrite });
        // The typed decimals are the binary64 values' shortest decimal text; the wire writes the Numbers themselves.
        private UsageCost Binary64Cost(double input, double output, double read, double write, double total)
        {
            if (!double.IsFinite(input) || !double.IsFinite(output) || !double.IsFinite(read) || !double.IsFinite(write) || !double.IsFinite(total))
                throw Protocol();
            var data = JsonData.Parse(Contracts.Compatibility.EcmaScriptJsonProjection.Project(JsonSerializer.Serialize(new { input, output, cacheRead = read, cacheWrite = write, total })));
            var value = data.Value;
            return new(value.GetProperty("input").GetDecimal(), value.GetProperty("output").GetDecimal(), value.GetProperty("cacheRead").GetDecimal(),
                value.GetProperty("cacheWrite").GetDecimal(), value.GetProperty("total").GetDecimal(), SourceBinary64Cost: data);
        }

        private void CheckJson(JsonElement value, int depth)
        {
            if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                if (depth >= _options.MaximumJsonDepth) throw Limit();
                if (value.ValueKind == JsonValueKind.Object)
                    foreach (var property in value.EnumerateObject()) { CheckString(property.Name); CheckJson(property.Value, depth + 1); }
                else foreach (var child in value.EnumerateArray()) CheckJson(child, depth + 1);
            }
            else if (value.ValueKind == JsonValueKind.String) CheckString(value.GetString()!);
        }

        private static void CheckString(string value)
        {
            for (var index = 0; index < value.Length; index++)
                if (char.IsSurrogate(value[index]))
                {
                    if (!char.IsHighSurrogate(value[index]) || index + 1 >= value.Length || !char.IsLowSurrogate(value[++index])) throw Protocol();
                }
        }
        private static JsonElement Object(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value : throw Protocol();
        private static string String(JsonElement value, string name, bool allowEmpty = false)
        {
            if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) throw Protocol();
            var result = field.GetString()!;
            if (!allowEmpty && result.Length == 0) throw Protocol();
            return result;
        }
        private static string? OptionalString(JsonElement value, string name)
            => value.TryGetProperty(name, out _) ? String(value, name, allowEmpty: true) : null;
        private static int Index(JsonElement value)
        {
            if (!value.TryGetProperty("output_index", out var index) || !index.TryGetInt32(out var result) || result < 0) throw Protocol();
            return result;
        }
        private static long Number(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out var field)) return 0;
            if (!field.TryGetInt64(out var result) || result < 0) throw Protocol();
            return result;
        }
    }
}
