using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts.Compatibility;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAIResponses;

/// <summary>Caller-supplied rates per million tokens; no provider catalog or service-tier adjustment.</summary>
public sealed record ResponsesTokenRates(decimal Input = 0, decimal Output = 0, decimal CacheRead = 0, decimal CacheWrite = 0);

public sealed record ResponsesTextToolOptions(
    int MaximumEvents = 4096, int MaximumEventCharacters = 65_536,
    int MaximumInputCharacters = 1_048_576, int MaximumContentSlots = 64,
    int MaximumContentCharacters = 1_048_576, int MaximumJsonDepth = 32,
    ResponsesTokenRates? Rates = null, string? ServiceTier = null);

/// <summary>A bounded parsed-DTO adapter. Owns the returned source enumerator, never performs HTTP or executes tools.</summary>
public sealed class ResponsesTextToolTransport : IChatTransport
{
    private readonly Func<ChatRequest, CancellationToken, IAsyncEnumerable<JsonData>> _source;
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
        if (_rates.Input < 0 || _rates.Output < 0 || _rates.CacheRead < 0 || _rates.CacheWrite < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Responses token rates must be nonnegative.");
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var state = new State(request, _options, _rates);
        yield return state.Start;
        cancellationToken.ThrowIfCancellationRequested();
        var source = _source(request, cancellationToken) ?? throw Protocol();
        Exception? sourceFailure = null;
        // Cleanup completes before a successful terminal is exposed. Early disposal also awaits this scope.
        await using (var enumerator = source.GetAsyncEnumerator(cancellationToken))
        {
            while (true)
            {
                List<StreamEvent> events;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    events = state.Process(enumerator.Current);
                }
                catch (Exception failure) { sourceFailure = failure; break; }
                foreach (var progress in events)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return progress;
                }
            }
            // Once input is exhausted or failed, no later terminal can backfill these
            // valid done items. Preserve their authoritative state before rethrowing.
            if (!cancellationToken.IsCancellationRequested)
                foreach (var progress in state.EndPendingThinking())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return progress;
                }
        }
        if (sourceFailure is not null) ExceptionDispatchInfo.Capture(sourceFailure).Throw();
        cancellationToken.ThrowIfCancellationRequested();
        yield return state.Finish();
    }

    private static StreamProtocolException Protocol() => new("Invalid or unsupported Responses text/tool stream.");
    private static StreamLimitException Limit() => new("Responses text/tool stream exceeds configured limits.");
    private static JsonData StringData(string value) => JsonData.Parse(JsonSerializer.Serialize(value, SignatureJson));

    // JSON.stringify retains all valid non-control Unicode, unlike the framework encoder's blocklist.
    // DTO admission rejects unpaired surrogates before this helper is called.
    private static string Signature(string id, string? phase)
    {
        var builder = new StringBuilder("{\"v\":1,\"id\":");
        Quote(id);
        if (!string.IsNullOrEmpty(phase)) { builder.Append(",\"phase\":"); Quote(phase); }
        return builder.Append('}').ToString();

        void Quote(string value)
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
    }

    private sealed class Slot(int index, string kind, string itemId, string? callId = null, string? name = null)
    {
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
        private readonly AssistantStreamReducer _reducer;
        private readonly Dictionary<int, Slot> _slots = [];
        private readonly Dictionary<string, Slot> _reasoningById = new(StringComparer.Ordinal);
        private JsonFields _properties = JsonFields.Empty;
        private TokenUsage _usage = TokenUsage.Zero;
        private int _events;
        private long _inputCharacters;
        private bool _completed;
        private bool _incomplete;
        private StopReason _stopReason = StopReason.Stop;
        public StreamStarted Start { get; }

        public State(ChatRequest request, ResponsesTextToolOptions options, ResponsesTokenRates rates)
        {
            _options = options;
            _modelId = request.Model.Id;
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
                    else
                    {
                        if (String(item, "call_id") != slot.CallId || String(item, "name") != slot.Name) throw Protocol();
                        var raw = OptionalString(item, "arguments");
                        if (string.IsNullOrEmpty(raw)) raw = _reducer.GetToolJsonPreview(slot.Index);
                        if (raw.Length == 0) raw = "{}";
                        JsonData final;
                        try { final = FinalToolArguments.ParseStrict(raw).Json; }
                        catch (Exception exception) when (exception is JsonException or StreamProtocolException) { throw Protocol(); }
                        CheckJson(final.Value, 0);
                        var original = (ToolCallContent)_reducer.Snapshot().Content[slot.Index];
                        var fields = original.ExtraProperties ?? JsonFields.Empty;
                        if (OptionalString(item, "namespace") is { } ns) fields = fields.Set("namespace", StringData(ns));
                        Emit(new ToolCallEnded(slot.Index, new(original.Id, original.Name, final, fields)));
                    }
                    slot.Ended = true;
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
                            else if (kind is not ("message" or "function_call")) throw Protocol();
                        }
                    }
                    if (OptionalString(response, "id") is { Length: > 0 } id) _properties = _properties.Set("responseId", StringData(id));
                    _properties = _properties.Set("rawStopReason", StringData(status + (string.IsNullOrEmpty(incompleteReason) ? "" : "." + incompleteReason)));
                    _stopReason = !_incomplete ? StopReason.Stop : incompleteReason == "max_output_tokens" ? StopReason.Length : StopReason.Error;
                    if (_stopReason == StopReason.Error)
                        _properties = _properties.Set("errorMessage", StringData(incompleteReason == "content_filter" ?
                            "Response incomplete: content_filter" : "Response incomplete without a provider reason"));
                    if (response.TryGetProperty("usage", out var usage)) _usage = Usage(Object(usage));
                    ApplyServiceTier(OptionalStringOrNull(response, "service_tier") ?? _options.ServiceTier);
                    foreach (var finished in _slots.Values.Where(s => s.Kind == "reasoning" && s.Ended && !s.ThinkingEnded))
                        EndThinking(finished, Emit);
                    _completed = true;
                    break;
                default: throw Protocol();
            }
            return events;
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
        // Handle the pinned token-limit and content-filter outcomes. Keep unknown
        // untyped reasons outside this bounded profile rather than copying them to diagnostics.
        private static string? IncompleteReason(JsonElement response)
        {
            if (!response.TryGetProperty("incomplete_details", out var details) || details.ValueKind == JsonValueKind.Null) return null;
            Object(details);
            var reason = OptionalStringOrNull(details, "reason");
            if (reason is not (null or "" or "max_output_tokens" or "content_filter")) throw Protocol();
            return reason;
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
            var slot = new Slot(_slots.Count, kind, itemId,
                kind == "function_call" ? String(item, "call_id") : null,
                kind == "function_call" ? String(item, "name") : null);
            if (kind == "reasoning") emit(new ThinkingStarted(slot.Index, new ThinkingContent("")));
            else if (kind == "message") emit(new TextStarted(slot.Index, new TextContent("")));
            else if (kind == "function_call")
            {
                var fields = JsonFields.Empty;
                if (OptionalString(item, "namespace") is { } ns) fields = fields.Set("namespace", StringData(ns));
                emit(new ToolCallStarted(slot.Index, new(slot.CallId + "|" + itemId, slot.Name!, JsonData.EmptyObject, fields)));
                if (OptionalString(item, "arguments") is { Length: > 0 } seed) emit(new ToolCallCheckpoint(slot.Index, seed));
            }
            else throw Protocol();
            _slots.Add(outputIndex, slot);
            return slot;
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
            if (!_incomplete && unfinished)
                throw new StreamProtocolException("Responses stream completed with unfinished content.");
            if (_incomplete && _stopReason == StopReason.Length && unfinished)
            {
                // Preserve usage and partial content, but never invent authoritative ends.
                _stopReason = StopReason.Error;
                _properties = _properties.Set("errorMessage", StringData("Response incomplete with unfinished content."));
            }
            var message = _reducer.Snapshot() with
            {
                Usage = _usage, ExtraProperties = _properties,
                StopReason = _stopReason == StopReason.Stop && _slots.Values.Any(slot => slot.Kind == "function_call") ? StopReason.ToolUse : _stopReason
            };
            StreamTerminalEvent terminal = message.StopReason == StopReason.Error ?
                new StreamError(StopReason.Error, message) : new StreamDone(message.StopReason, message);
            _reducer.Apply(terminal);
            return terminal;
        }

        private void ApplyServiceTier(string? tier)
        {
            var multiplier = ResponsesServiceTier.Multiplier(_modelId, tier);
            if (multiplier == 1) return;
            var cost = _usage.Cost;
            var input = ComputedCost(checked(cost.Input * multiplier));
            var output = ComputedCost(checked(cost.Output * multiplier));
            var read = ComputedCost(checked(cost.CacheRead * multiplier));
            var write = ComputedCost(checked(cost.CacheWrite * multiplier));
            _usage = _usage with { Cost = cost with { Input = input, Output = output, CacheRead = read, CacheWrite = write,
                Total = ComputedCost(checked(input + output + read + write)) } };
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
            var inputCost = ComputedCost(checked(_rates.Input / 1_000_000m * uncached));
            var outputCost = ComputedCost(checked(_rates.Output / 1_000_000m * output));
            var cachedCost = ComputedCost(checked(_rates.CacheRead / 1_000_000m * cached));
            var writtenCost = ComputedCost(checked(_rates.CacheWrite / 1_000_000m * written));
            return new(uncached, output, cached, written, total,
                new(inputCost, outputCost, cachedCost, writtenCost, ComputedCost(checked(inputCost + outputCost + cachedCost + writtenCost))),
                JsonFields.Empty.Set("reasoning", JsonData.Parse(reasoning.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        // Only derived costs acquire a canonical decimal scale; raw provider/tool JSON is never rewritten.
        private static decimal ComputedCost(decimal value) => decimal.Parse(
            value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);

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
