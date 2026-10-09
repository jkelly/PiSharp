// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/anthropic-messages.ts (stream event mapping,
// providerThinkingLevel and the anthropic_input_transformations diagnostic).
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI.Protocols.ProviderShared;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

public sealed record AnthropicTokenRates(decimal Input = 0, decimal Output = 0, decimal CacheRead = 0, decimal CacheWrite = 0)
{ public ImmutableArray<TokenRateTier> Tiers { get; init; } = []; }
public sealed record AnthropicFallbackModel(string Provider, string Model, AnthropicTokenRates Rates);
public sealed record AnthropicMessagesOptions(int MaximumEvents = 4096, int MaximumEventCharacters = 65_536,
    int MaximumInputCharacters = PiRequestBudget.StreamCharacters, int MaximumContentSlots = 64, int MaximumContentCharacters = PiRequestBudget.StreamCharacters,
    int MaximumSignatureCharacters = PiRequestBudget.StreamCharacters, int MaximumJsonDepth = 32, AnthropicTokenRates? Rates = null,
    ImmutableArray<AnthropicFallbackModel> AllowedFallbackModels = default, bool OAuthToolNames = false,
    int MaximumToolDeclarations = 1024, int MaximumActiveTools = 128)
{
    public bool CaptureSourceEmissionSnapshots { get; init; }
    /// <summary>Exact native effort recorded on every message of a managed (compat.supportsMidConvoEffort) request; null when unmanaged.</summary>
    public string? ProviderThinkingLevel { get; init; }
}
public enum AnthropicMessagesFailure { SourceFailed, MalformedStream, UnexpectedEof, ResourceLimit, ProviderError, Cancelled, CleanupFailed }

/// <summary>Offline parsed Messages DTO adapter. Owns its source enumerator; never sends or executes tools.</summary>
public sealed partial class AnthropicMessagesTransport : IChatTransport
{
    private readonly Func<ChatRequest, CancellationToken, IAsyncEnumerable<JsonData>> _source;
    private readonly AnthropicMessagesOptions _options;
    public AnthropicMessagesTransport(Func<ChatRequest, CancellationToken, IAsyncEnumerable<JsonData>> source,
        AnthropicMessagesOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source); _source = source; _options = options ?? new();
        if (_options.MaximumEvents <= 0 || _options.MaximumEventCharacters <= 0 || _options.MaximumInputCharacters <= 0 ||
            _options.MaximumContentSlots <= 0 || _options.MaximumContentCharacters <= 0 || _options.MaximumSignatureCharacters <= 0 ||
            _options.MaximumJsonDepth is < 1 or > 64 || _options.MaximumToolDeclarations <= 0 || _options.MaximumActiveTools <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Invalid Anthropic stream limits.");
        if (_options.ProviderThinkingLevel is not (null or "low" or "medium" or "high" or "xhigh" or "max"))
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid Anthropic provider thinking level.");
        ValidateRates(_options.Rates ?? new());
        if (!_options.AllowedFallbackModels.IsDefaultOrEmpty)
        {
            if (_options.AllowedFallbackModels.Length > _options.MaximumContentSlots)
                throw new ArgumentOutOfRangeException(nameof(options), "Too many Anthropic fallback profiles.");
            var identities = new HashSet<(string Provider, string Model)>();
            foreach (var fallback in _options.AllowedFallbackModels)
            {
                if (fallback is null || string.IsNullOrWhiteSpace(fallback.Provider) || string.IsNullOrWhiteSpace(fallback.Model) ||
                    fallback.Rates is null || !identities.Add((fallback.Provider, fallback.Model)))
                    throw new ArgumentException("Invalid or duplicate Anthropic fallback profile.", nameof(options));
                ValidateIdentity(fallback.Provider); ValidateIdentity(fallback.Model); ValidateRates(fallback.Rates);
            }
        }
    }
    private static void ValidateRates(AnthropicTokenRates rates)
    {
        if (rates.Input < 0 || rates.Output < 0 || rates.CacheRead < 0 || rates.CacheWrite < 0 || !PromptLengthPricing.Valid(rates.Tiers))
            throw new ArgumentOutOfRangeException(nameof(rates), "Anthropic token rates must be nonnegative.");
    }
    private static void ValidateIdentity(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsHighSurrogate(value[index]))
            { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) throw new ArgumentException("Invalid Anthropic fallback identity."); }
            else if (char.IsLowSurrogate(value[index])) throw new ArgumentException("Invalid Anthropic fallback identity.");
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        var state = new State(request, _options);
        IAsyncEnumerator<JsonData>? enumerator = null; AnthropicMessagesFailure? failure = null;
        try { enumerator = (_source(request, cancellationToken) ?? throw Protocol()).GetAsyncEnumerator(cancellationToken); }
        catch (Exception) { failure = cancellationToken.IsCancellationRequested ? AnthropicMessagesFailure.Cancelled : AnthropicMessagesFailure.SourceFailed; }
        if (enumerator is null) { yield return state.Finish(failure ?? AnthropicMessagesFailure.SourceFailed); yield break; }
        try
        {
            yield return state.CaptureEmission(state.Start);
            while (failure is null)
            {
                if (cancellationToken.IsCancellationRequested) { failure = AnthropicMessagesFailure.Cancelled; break; }
                var moved = false;
                try { moved = await enumerator.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception error) { failure = SourceFailure(state, error, cancellationToken); }
                if (failure is not null || !moved) break;
                if (cancellationToken.IsCancellationRequested) { failure = AnthropicMessagesFailure.Cancelled; break; }
                // Source value acquisition has its own failure boundary: no DTO has
                // reached protocol processing when an enumerator's Current getter fails.
                JsonData? dto = null;
                try { dto = enumerator.Current; }
                catch (Exception) { failure = cancellationToken.IsCancellationRequested ? AnthropicMessagesFailure.Cancelled : AnthropicMessagesFailure.SourceFailed; }
                if (failure is not null) break;
                if (cancellationToken.IsCancellationRequested) { failure = AnthropicMessagesFailure.Cancelled; break; }
                List<StreamEvent> progress = [];
                try { progress = state.Process(dto!); }
                catch (Exception error) { failure = Classify(error, cancellationToken); }
                foreach (var item in progress)
                {
                    if (cancellationToken.IsCancellationRequested) { failure = AnthropicMessagesFailure.Cancelled; break; }
                    yield return item;
                }
            }
        }
        finally
        {
            // Early consumer disposal also awaits this cleanup. Terminal choice is made only afterward.
            try { await enumerator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { failure ??= AnthropicMessagesFailure.CleanupFailed; }
        }
        if (cancellationToken.IsCancellationRequested) failure = AnthropicMessagesFailure.Cancelled;
        yield return state.Finish(failure);
    }

    /// <summary>A source failure. Provider error text the source surfaces (a rejected response's SDK message, or a named
    /// error event's data) is what anthropic-messages.ts shows as errorMessage.</summary>
    private static AnthropicMessagesFailure SourceFailure(State state, Exception error, CancellationToken token)
    {
        if (token.IsCancellationRequested) return AnthropicMessagesFailure.Cancelled;
        if (error is not ProviderDisplayException shown) return AnthropicMessagesFailure.SourceFailed;
        state.SourceMessage ??= shown.Message;
        return shown.InStream ? AnthropicMessagesFailure.ProviderError : AnthropicMessagesFailure.SourceFailed;
    }

    private static AnthropicMessagesFailure Classify(Exception error, CancellationToken token) =>
        token.IsCancellationRequested ? AnthropicMessagesFailure.Cancelled : error switch
        {
            StreamLimitException => AnthropicMessagesFailure.ResourceLimit,
            StreamProtocolException or JsonException or InvalidOperationException or KeyNotFoundException or OverflowException or FormatException or ArgumentException =>
                AnthropicMessagesFailure.MalformedStream,
            ProviderSignalException => AnthropicMessagesFailure.ProviderError,
            _ => AnthropicMessagesFailure.SourceFailed
        };
    private static StreamProtocolException Protocol() => new("Invalid or unsupported Anthropic Messages stream.");
    private static StreamLimitException Limit() => new("Anthropic Messages stream exceeds configured limits.");
    private static JsonData TextData(string value) => JsonData.Parse(JsonSerializer.Serialize(value));
    private sealed class ProviderSignalException : Exception { }
    private sealed class Slot(int index, string kind, JsonFields properties, string signature = "")
    {
        public int Index { get; } = index; public string Kind { get; } = kind; public JsonFields Properties { get; } = properties;
        public StringBuilder Signature { get; } = new(signature); public bool Ended;
    }
    private sealed partial class State
    {
        private readonly AnthropicMessagesOptions _options;
        private readonly ImmutableArray<string> _currentToolNames;
        private AnthropicTokenRates _rates;
        private readonly AssistantStreamReducer _reducer;
        private readonly Dictionary<int, Slot> _slots = [];
        private readonly HashSet<int> _fallbackIndices = [];
        private readonly HashSet<int> _endedFallbackIndices = [];
        private JsonFields _properties = JsonFields.Empty;
        private JsonElement _transformations;
        private readonly long _timestamp;
        private TokenUsage _usage = TokenUsage.Zero;
        private long _cacheWrite1h;
        private long? _reasoning;
        private long _signatureCharacters;
        private int _events;
        private long _inputCharacters;
        private bool _messageStarted;
        private bool _messageStopped;
        private StopReason _reason = StopReason.Pending;
        private string? _stopErrorMessage;
        internal string? SourceMessage;
        public StreamStarted Start { get; }
        public State(ChatRequest request, AnthropicMessagesOptions options)
        {
            if (request.Model is null || request.Model.Api != "anthropic-messages" ||
                string.IsNullOrWhiteSpace(request.Model.Id) || string.IsNullOrWhiteSpace(request.Model.Provider)) throw Protocol();
            Unicode(request.Model.Id); Unicode(request.Model.Provider);
            _options = options; _rates = options.Rates ?? new();
            _currentToolNames = options.OAuthToolNames ? CurrentTools(request, options) : [];
            if (options.ProviderThinkingLevel is { } level) _properties = _properties.Set("providerThinkingLevel", TextData(level));
            _timestamp = request.Timestamp;
            Start = new(new(request.Model.Api, request.Model.Provider, request.Model.Id, request.Timestamp, [], TokenUsage.Zero, StopReason.Pending,
                options.ProviderThinkingLevel is null ? null : _properties));
            _reducer = new(Start.Partial, new(options.MaximumContentSlots, options.MaximumContentCharacters)); _reducer.Apply(Start);
        }
        private string ResponseToolName(string wireName)
        {
            // Original fromClaudeCodeName selects the first current declaration.
            var folded = wireName.ToLowerInvariant();
            return _currentToolNames.FirstOrDefault(name => name.ToLowerInvariant() == folded) ?? wireName;
        }
        private static ImmutableArray<string> CurrentTools(ChatRequest request, AnthropicMessagesOptions options)
        {
            var order = new List<string>(); var names = new HashSet<string>(StringComparer.Ordinal);
            long characters = 0; var declarations = 0;
            foreach (var entry in request.Messages)
            {
                if (entry is null || entry.WireBody is null) throw Protocol();
                var raw = entry.WireBody.ToString(); characters += raw.Length;
                if (characters > options.MaximumInputCharacters) throw Limit();
                if (entry.Role != "system") continue;
                var body = JsonData.Parse(raw).Value; Object(body);
                foreach (var property in new[] { "toolsRemoved", "toolsAdded" })
                {
                    if (!body.TryGetProperty(property, out var tools)) continue;
                    if (tools.ValueKind != JsonValueKind.Array) throw Protocol();
                    foreach (var tool in tools.EnumerateArray())
                    {
                        if (++declarations > options.MaximumToolDeclarations) throw Limit();
                        var name = String(Object(tool), "name"); Unicode(name);
                        if (property == "toolsRemoved") { if (names.Remove(name)) order.Remove(name); }
                        else if (names.Add(name))
                        {
                            if (names.Count > options.MaximumActiveTools) throw Limit();
                            order.Add(name);
                        }
                    }
                }
            }
            return order.ToImmutableArray();
        }
        public List<StreamEvent> Process(JsonData data)
        {
            if (data is null) throw Protocol(); var raw = data.ToString();
            if (++_events > _options.MaximumEvents || raw.Length > _options.MaximumEventCharacters ||
                raw.Length > _options.MaximumInputCharacters - _inputCharacters) throw Limit();
            _inputCharacters += raw.Length;
            // Reject permissive caller syntax and preserve opaque numeric tokens without numeric conversion.
            var owned = JsonData.Parse(raw); var value = owned.Value; CheckJson(value, 0); Object(value);
            var type = String(value, "type"); var progress = new List<StreamEvent>(2);
            void Emit(StreamEvent item) { _reducer.Apply(item); progress.Add(item); }
            if (type == "ping") return progress;
            if (_messageStopped) throw Protocol();
            switch (type)
            {
                case "message_start":
                    if (_messageStarted || _slots.Count != 0) throw Protocol();
                    var message = Object(value.GetProperty("message"));
                    if (message.TryGetProperty("content", out var embedded) && (embedded.ValueKind != JsonValueKind.Array || embedded.GetArrayLength() != 0)) throw Protocol();
                    if (message.TryGetProperty("role", out var role) && (role.ValueKind != JsonValueKind.String || role.GetString() != "assistant")) throw Protocol();
                    _properties = _properties.Set("responseId", TextData(String(message, "id")));
                    var actualModel = OptionalString(message, "model");
                    if (!string.IsNullOrEmpty(actualModel) && actualModel != Start.Partial.Model)
                    {
                        _properties = _properties.Set("responseModel", TextData(actualModel));
                        if (!_options.AllowedFallbackModels.IsDefaultOrEmpty)
                            foreach (var fallback in _options.AllowedFallbackModels)
                                if (fallback.Provider == Start.Partial.Provider && fallback.Model == actualModel)
                                { _rates = fallback.Rates; break; }
                    }
                    Transformations(message); ReadUsage(Object(message.GetProperty("usage")), initial: true); _messageStarted = true;
                    break;
                case "content_block_start":
                    RequireMessage(); var wireIndex = Index(value); var block = Object(value.GetProperty("content_block"));
                    if (_slots.ContainsKey(wireIndex) || _fallbackIndices.Contains(wireIndex)) throw Protocol();
                    var kind = String(block, "type");
                    if (kind == "fallback")
                    {
                        // A pre-output marker is bookkeeping. Only message_start.model selects prices.
                        if (_slots.Count != 0) throw Protocol();
                        if (_fallbackIndices.Count >= _options.MaximumContentSlots) throw Limit();
                        _fallbackIndices.Add(wireIndex); break;
                    }
                    var index = _slots.Count; Slot slot;
                    switch (kind)
                    {
                        case "text":
                            var textProperties = JsonFields.FromObjectExcept(block, "type", "text");
                            slot = new(index, kind, textProperties);
                            Emit(new TextStarted(index, new(OptionalString(block, "text") ?? "", textProperties))); break;
                        case "thinking":
                            var signature = OptionalString(block, "signature") ?? ""; ChargeSignature(signature.Length);
                            var thinkingProperties = JsonFields.FromObjectExcept(block, "type", "thinking", "signature").Set("thinkingSignature", TextData(signature));
                            slot = new(index, "thinking", thinkingProperties, signature);
                            Emit(new ThinkingStarted(index, new(OptionalString(block, "thinking") ?? "", thinkingProperties))); break;
                        case "redacted_thinking":
                            var dataSignature = String(block, "data", allowEmpty: true); ChargeSignature(dataSignature.Length);
                            var redactedProperties = JsonFields.FromObjectExcept(block, "type", "data")
                                .Set("thinkingSignature", TextData(dataSignature)).Set("redacted", JsonData.Parse("true"));
                            slot = new(index, "thinking", redactedProperties, dataSignature);
                            Emit(new ThinkingStarted(index, new("[Reasoning redacted]", redactedProperties))); break;
                        case "tool_use":
                            var toolProperties = JsonFields.FromObjectExcept(block, "type", "id", "name", "input");
                            var initialInput = block.TryGetProperty("input", out var input) && input.ValueKind != JsonValueKind.Null
                                ? JsonData.FromElement(Object(input)) : JsonData.EmptyObject;
                            slot = new(index, kind, toolProperties);
                            Emit(new ToolCallStarted(index, new(String(block, "id"), ResponseToolName(String(block, "name")), initialInput, toolProperties))); break;
                        default: throw Protocol();
                    }
                    _slots.Add(wireIndex, slot); break;
                case "content_block_delta":
                    RequireMessage(); var active = Active(Index(value)); var delta = Object(value.GetProperty("delta"));
                    switch (String(delta, "type"))
                    {
                        case "text_delta":
                            if (active.Kind != "text") throw Protocol();
                            Emit(new TextDelta(active.Index, String(delta, "text", allowEmpty: true))); break;
                        case "thinking_delta":
                            if (active.Kind != "thinking") throw Protocol();
                            Emit(new ThinkingDelta(active.Index, String(delta, "thinking", allowEmpty: true))); break;
                        case "signature_delta":
                            if (active.Kind != "thinking") throw Protocol(); var piece = String(delta, "signature", allowEmpty: true);
                            ChargeSignature(piece.Length); active.Signature.Append(piece); break;
                        case "input_json_delta":
                            if (active.Kind != "tool_use") throw Protocol();
                            Emit(new ToolCallDelta(active.Index, String(delta, "partial_json", allowEmpty: true)));
                            if (_options.CaptureSourceEmissionSnapshots) _sourceToolDeltaIndices.Add(active.Index); break;
                        default: throw Protocol();
                    }
                    break;
                case "content_block_stop":
                    RequireMessage(); var endedIndex = Index(value);
                    if (_fallbackIndices.Contains(endedIndex))
                    { if (!_endedFallbackIndices.Add(endedIndex)) throw Protocol(); break; }
                    var ended = Active(endedIndex); var content = _reducer.Snapshot().Content[ended.Index];
                    if (content is TextContent text) Emit(new TextEnded(ended.Index, text.Text, ended.Properties));
                    else if (content is ThinkingContent thinking) Emit(new ThinkingEnded(ended.Index, thinking.Thinking,
                        ended.Properties.Set("thinkingSignature", TextData(ended.Signature.ToString()))));
                    else if (content is ToolCallContent tool)
                    {
                        // anthropic-messages.ts content_block_stop: block.arguments = parseStreamingJson(block.partialJson).
                        var arguments = StreamingJson.Parse(_reducer.GetToolJsonPreview(ended.Index));
                        Emit(new ToolCallEnded(ended.Index, tool with { Arguments = arguments }));
                    }
                    else throw Protocol();
                    ended.Ended = true; break;
                case "message_delta":
                    RequireMessage(); var messageDelta = Object(value.GetProperty("delta")); Transformations(value);
                    var stop = OptionalString(messageDelta, "stop_reason");
                    if (!string.IsNullOrEmpty(stop))
                    {
                        _properties = _properties.Set("rawStopReason", TextData(stop)); _reason = Stop(stop);
                        if (stop == "refusal")
                        {
                            var explanation = messageDelta.TryGetProperty("stop_details", out var stopDetails) && stopDetails.ValueKind != JsonValueKind.Null
                                ? OptionalString(Object(stopDetails), "explanation") : null;
                            _stopErrorMessage = string.IsNullOrEmpty(explanation) ? "The model refused to complete the request" : explanation;
                        }
                        else if (stop == "sensitive") _stopErrorMessage = "Provider stopped with: sensitive";
                    }
                    if (value.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null) ReadUsage(Object(usage), initial: false);
                    break;
                case "message_stop":
                    RequireMessage(); _messageStopped = true; break;
                case "error":
                    _ = Object(value.GetProperty("error")); throw new ProviderSignalException();
                default: throw Protocol();
            }
            if (_options.CaptureSourceEmissionSnapshots)
                for (var index = 0; index < progress.Count; index++) progress[index] = CaptureEmission(progress[index]);
            return progress;
        }
        public StreamTerminalEvent Finish(AnthropicMessagesFailure? failure)
        {
            if (failure is null)
            {
                if (!_messageStarted || !_messageStopped || _reason == StopReason.Pending) failure = AnthropicMessagesFailure.UnexpectedEof;
                else if (_reason == StopReason.Error) failure = AnthropicMessagesFailure.ProviderError;
                else if (_slots.Values.Any(slot => !slot.Ended)) failure = AnthropicMessagesFailure.MalformedStream;
            }
            var snapshot = _reducer.Snapshot();
            // Signatures observed before an abort are retained in the failed message without inventing content ends.
            var content = snapshot.Content.ToBuilder();
            foreach (var slot in _slots.Values)
                if (content[slot.Index] is ThinkingContent thinking)
                    content[slot.Index] = new ThinkingContent(thinking.Thinking,
                        slot.Properties.Set("thinkingSignature", TextData(slot.Signature.ToString())));
            var reason = failure is null ? _reason : failure == AnthropicMessagesFailure.Cancelled ? StopReason.Aborted : StopReason.Error;
            var properties = _properties;
            // Diagnostics are appended only to a completed response, as upstream does after its error checks; the timestamp is the request's.
            if (failure is null && _transformations.ValueKind == JsonValueKind.Array && _transformations.GetArrayLength() > 0)
            {
                var transformations = new System.Text.Json.Nodes.JsonArray();
                foreach (var item in _transformations.EnumerateArray())
                {
                    var projected = new System.Text.Json.Nodes.JsonObject();
                    foreach (var name in new[] { "type", "path", "reason" }) if (OptionalString(item, name) is { } text) projected[name] = text;
                    transformations.Add(projected);
                }
                var diagnostics = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["type"] = "anthropic_input_transformations",
                    ["timestamp"] = _timestamp, ["details"] = new System.Text.Json.Nodes.JsonObject { ["transformations"] = transformations } });
                properties = properties.Set("diagnostics", JsonData.Parse(diagnostics.ToJsonString()));
            }
            if (failure is not null) properties = properties.Set("errorMessage", TextData(failure == AnthropicMessagesFailure.Cancelled
                ? "Anthropic stream was cancelled." : SourceMessage is not null ? SourceMessage : failure == AnthropicMessagesFailure.ProviderError && _reason == StopReason.Error && _stopErrorMessage is not null
                    ? _stopErrorMessage : "Anthropic stream did not complete."))
                .Set("anthropicFailure", TextData(failure.Value.ToString()));
            var final = snapshot with { Content = content.ToImmutable(), Usage = _usage, StopReason = reason, ExtraProperties = properties };
            StreamTerminalEvent terminal = failure is null ? new StreamDone(reason, final) : new StreamError(reason, final);
            _reducer.Apply(terminal); return (StreamTerminalEvent)CaptureEmission(terminal);
        }
        private void ReadUsage(JsonElement usage, bool initial)
        {
            var input = Count(usage, "input_tokens", initial ? 0 : _usage.Input);
            var output = Count(usage, "output_tokens", initial ? 0 : _usage.Output);
            var read = Count(usage, "cache_read_input_tokens", initial ? 0 : _usage.CacheRead);
            var write = Count(usage, "cache_creation_input_tokens", initial ? 0 : _usage.CacheWrite);
            if (initial) _cacheWrite1h = 0;
            if (usage.TryGetProperty("cache_creation", out var cache) && cache.ValueKind != JsonValueKind.Null)
                _cacheWrite1h = Count(Object(cache), "ephemeral_1h_input_tokens", _cacheWrite1h);
            if (!initial && usage.TryGetProperty("output_tokens_details", out var details) && details.ValueKind != JsonValueKind.Null &&
                Object(details).TryGetProperty("thinking_tokens", out var thinking) && thinking.ValueKind != JsonValueKind.Null)
                _reasoning = Number(thinking);
            if (_cacheWrite1h > write || _reasoning > output) throw Protocol();
            var total = checked(input + output + read + write);
            // Pi abe508 models.ts calculateCost: prompt-length tiers price the whole request, 1h writes at 2x the tier input.
            var rates = PromptLengthPricing.Select(_rates.Input, _rates.Output, _rates.CacheRead, _rates.CacheWrite, _rates.Tiers, input, read, write);
            var costInput = checked(rates.Input / 1_000_000m * input); var costOutput = checked(rates.Output / 1_000_000m * output);
            var costRead = checked(rates.CacheRead / 1_000_000m * read);
            var costWrite = checked((rates.CacheWrite * (write - _cacheWrite1h) + rates.Input * 2 * _cacheWrite1h) / 1_000_000m);
            var extras = JsonFields.Empty.Set("cacheWrite1h", JsonData.Parse(_cacheWrite1h.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (_reasoning is { } tokens) extras = extras.Set("reasoning", JsonData.Parse(tokens.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            _usage = new(input, output, read, write, total, new(costInput, costOutput, costRead, costWrite,
                checked(costInput + costOutput + costRead + costWrite)), extras);
        }
        private void Transformations(JsonElement value)
        {
            if (!value.TryGetProperty("input_transformations", out var transformations) || transformations.ValueKind == JsonValueKind.Null) return;
            if (transformations.ValueKind != JsonValueKind.Array) throw Protocol();
            if (transformations.GetArrayLength() > _options.MaximumContentSlots) throw Limit();
            // The last array seen (message_start, then message_delta) is reported; entries carry optional string type/path/reason.
            foreach (var item in transformations.EnumerateArray()) { Object(item); foreach (var name in new[] { "type", "path", "reason" }) _ = OptionalString(item, name); }
            _transformations = transformations.Clone();
        }
        private void RequireMessage() { if (!_messageStarted) throw Protocol(); }
        private Slot Active(int index) => _slots.TryGetValue(index, out var slot) && !slot.Ended ? slot : throw Protocol();
        private void ChargeSignature(int characters)
        { if (characters > _options.MaximumSignatureCharacters - _signatureCharacters) throw Limit(); _signatureCharacters += characters; }
        private void CheckJson(JsonElement value, int depth)
        {
            if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                if (++depth > _options.MaximumJsonDepth) throw Limit();
                if (value.ValueKind == JsonValueKind.Object)
                    foreach (var property in value.EnumerateObject()) { Unicode(property.Name); CheckJson(property.Value, depth); }
                else foreach (var item in value.EnumerateArray()) CheckJson(item, depth);
            }
            else if (value.ValueKind == JsonValueKind.String) Unicode(value.GetString()!);
        }
        private static StopReason Stop(string value) => value switch
        {
            "end_turn" or "pause_turn" or "stop_sequence" => StopReason.Stop,
            "max_tokens" => StopReason.Length, "tool_use" => StopReason.ToolUse,
            "refusal" or "sensitive" => StopReason.Error, _ => throw Protocol()
        };
        private static long Count(JsonElement value, string name, long fallback) => value.TryGetProperty(name, out var count) &&
            count.ValueKind != JsonValueKind.Null ? Number(count) : fallback;
        private static long Number(JsonElement value)
        { if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < 0) throw Protocol(); return number; }
        private static int Index(JsonElement value)
        { var number = Number(value.GetProperty("index")); return number <= int.MaxValue ? (int)number : throw Protocol(); }
        private static JsonElement Object(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value : throw Protocol();
        private static string String(JsonElement value, string name, bool allowEmpty = false)
        {
            var item = value.GetProperty(name);
            if (item.ValueKind != JsonValueKind.String) throw Protocol(); var text = item.GetString()!;
            if (!allowEmpty && text.Length == 0) throw Protocol(); return text;
        }
        private static string? OptionalString(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null) return null;
            return item.ValueKind == JsonValueKind.String ? item.GetString() : throw Protocol();
        }
        private static void Unicode(string value)
        {
            for (var index = 0; index < value.Length; index++)
                if (char.IsHighSurrogate(value[index]))
                { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) throw Protocol(); }
                else if (char.IsLowSurrogate(value[index])) throw Protocol();
        }
    }
}
