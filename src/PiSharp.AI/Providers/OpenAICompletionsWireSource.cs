using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;
using PiSharp.AI.Protocols;
using PiSharp.AI.Protocols.OpenAICompletions;

namespace PiSharp.AI.Providers;

/// <summary>Explicit synthetic rates per million tokens, independent of provider/catalog selection.</summary>
public sealed record OpenAICompletionsTokenRates(decimal Input = 0, decimal Output = 0,
    decimal CacheRead = 0, decimal CacheWrite = 0)
{ public ImmutableArray<TokenRateTier> Tiers { get; init; } = []; }

public sealed record OpenAICompletionsWireOptions(int MaximumChunks = 4096, int MaximumChunkCharacters = 65_536,
    int MaximumInputCharacters = PiRequestBudget.StreamCharacters, int MaximumContentSlots = int.MaxValue, int MaximumContentCharacters = PiRequestBudget.StreamCharacters,
    int MaximumJsonDepth = 32, bool SupportsFinishReason = true, OpenAICompletionsTokenRates? Rates = null)
{
    /// <summary>Explicit bounded source-view capture; ordinary native progress remains compact.</summary>
    public bool CaptureSourceEmissionSnapshots { get; init; }
    public bool SupportsOpenAIGrammarTools { get; init; }
    // Factory-resolved admission is immutable and survives ordinary record clones.
    // Mapping must retain its strict capability and configured history/output limits.
    internal CompletionsToolDeclarationProjectionOptions? ToolDeclarations { get; init; }
}

public enum OpenAICompletionsWireFailure
{
    SourceFailed, MalformedStream, UnexpectedEof, ResourceLimit, ProviderError, Cancelled, CleanupFailed, UnsupportedFeature
}

/// <summary>Parsed chunk source; owns one returned enumerator per run. Does not send requests or execute tools.</summary>
public sealed class OpenAICompletionsWireSource : IChatTransport
{
    private readonly Func<ChatRequest, CancellationToken, IAsyncEnumerable<JsonData>> _source;
    private readonly OpenAICompletionsWireOptions _options;

    public OpenAICompletionsWireSource(Func<ChatRequest, CancellationToken, IAsyncEnumerable<JsonData>> source,
        OpenAICompletionsWireOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source; _options = options ?? new();
        if (_options.MaximumChunks <= 0 || _options.MaximumChunkCharacters <= 0 || _options.MaximumInputCharacters <= 0 ||
            _options.MaximumContentSlots <= 0 || _options.MaximumContentCharacters <= 0 ||
            _options.MaximumJsonDepth is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid Completions stream limits.");
        var rates = _options.Rates ?? new();
        if (rates.Input < 0 || rates.Output < 0 || rates.CacheRead < 0 || rates.CacheWrite < 0 || !PromptLengthPricing.Valid(rates.Tiers))
            throw new ArgumentOutOfRangeException(nameof(options), "Completions token rates must be nonnegative.");
    }

    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        StreamCoreAsync(request, cancellationToken, null, false);

    internal IAsyncEnumerable<StreamEvent> StreamOwnedAsync(ChatRequest request, CompletionsProductionContext owner,
        CancellationToken cancellationToken, bool sourceView) => StreamCoreAsync(request, cancellationToken, owner, sourceView);

    private async IAsyncEnumerable<StreamEvent> StreamCoreAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken, CompletionsProductionContext? owner, bool sourceView)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var state = new State(request, sourceView ? _options with { CaptureSourceEmissionSnapshots = true } : _options, cancellationToken);
        owner?.Bind(state.CaptureLive, state.Finish, state.Current);
        IAsyncEnumerator<JsonData>? enumerator = null;
        OpenAICompletionsWireFailure? failure = null;
        NativeChatDiagnostic? cleanupDiagnostic = null;
        try { enumerator = (_source(request, cancellationToken) ?? throw Protocol()).GetAsyncEnumerator(cancellationToken); }
        catch (Exception error) { owner?.Admit(error); failure = cancellationToken.IsCancellationRequested
            ? OpenAICompletionsWireFailure.Cancelled : OpenAICompletionsWireFailure.SourceFailed; }
        if (enumerator is null)
        {
            foreach (var terminal in owner?.Finish(failure ?? OpenAICompletionsWireFailure.SourceFailed) ??
                state.Finish(failure ?? OpenAICompletionsWireFailure.SourceFailed)) yield return terminal;
            yield break;
        }
        var finishedReading = false;
        try
        {
            StreamStarted? start = null;
            // Admit the bounded owned Start copy before HTTP effects, but publish it only after preparation.
            if (!cancellationToken.IsCancellationRequested)
                try { start = state.CaptureStart(); }
                catch (Exception error) { failure = Classify(error, cancellationToken); }
            if (failure is null && enumerator is IPreparedCompletionsEnumerator prepared)
                try { prepared.BindStartup(owner?.Startup); await prepared.PrepareAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception error) { owner?.Admit(error); failure = cancellationToken.IsCancellationRequested
                    ? OpenAICompletionsWireFailure.Cancelled : OpenAICompletionsWireFailure.SourceFailed; }
            if (failure is null && !cancellationToken.IsCancellationRequested && start is not null) yield return start;
            while (failure is null)
            {
                if (cancellationToken.IsCancellationRequested) { failure = OpenAICompletionsWireFailure.Cancelled; break; }
                var moved = false;
                try { moved = await enumerator.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception error) { owner?.Admit(error); failure = cancellationToken.IsCancellationRequested
                    ? OpenAICompletionsWireFailure.Cancelled : OpenAICompletionsWireFailure.SourceFailed; }
                if (failure is not null || !moved) break;
                if (cancellationToken.IsCancellationRequested) { failure = OpenAICompletionsWireFailure.Cancelled; break; }
                JsonData? chunk = null;
                try { chunk = enumerator.Current; }
                catch (Exception error) { owner?.Admit(error); failure = cancellationToken.IsCancellationRequested
                    ? OpenAICompletionsWireFailure.Cancelled : OpenAICompletionsWireFailure.SourceFailed; }
                if (failure is not null) break;
                if (cancellationToken.IsCancellationRequested) { failure = OpenAICompletionsWireFailure.Cancelled; break; }
                List<StreamEvent> progress = [];
                try { state.Process(chunk!, progress); }
                catch (Exception error) { owner?.Admit(error); failure = Classify(error, cancellationToken); }
                foreach (var item in progress)
                {
                    if (cancellationToken.IsCancellationRequested) { failure = OpenAICompletionsWireFailure.Cancelled; break; }
                    yield return item;
                }
            }
            finishedReading = true;
        }
        finally
        {
            try
            {
                if (owner is null) await enumerator.DisposeAsync().ConfigureAwait(false);
                else await owner.CloseSourceAsync(enumerator, failure is not null || cancellationToken.IsCancellationRequested || !finishedReading).ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (!finishedReading && owner is null) throw new StreamProtocolException("Completions source cleanup failed.");
                cleanupDiagnostic = Diagnostic(OpenAICompletionsWireFailure.CleanupFailed);
                failure ??= owner is null ? OpenAICompletionsWireFailure.CleanupFailed : OpenAICompletionsWireFailure.SourceFailed;
            }
        }
        if (cancellationToken.IsCancellationRequested) failure = OpenAICompletionsWireFailure.Cancelled;
        foreach (var final in owner?.Finish(failure) ?? state.Finish(failure))
        {
            if (owner is null && cancellationToken.IsCancellationRequested)
            {
                var aborted = (StreamTerminalEvent)state.Finish(OpenAICompletionsWireFailure.Cancelled)[^1];
                yield return aborted with { NativeCleanupDiagnostic = cleanupDiagnostic };
                yield break;
            }
            yield return final is StreamTerminalEvent terminal && cleanupDiagnostic is not null
                ? terminal with { NativeCleanupDiagnostic = cleanupDiagnostic } : final;
        }
    }

    private static OpenAICompletionsWireFailure Classify(Exception error, CancellationToken token) =>
        token.IsCancellationRequested ? OpenAICompletionsWireFailure.Cancelled : error switch
        {
            StreamLimitException => OpenAICompletionsWireFailure.ResourceLimit,
            StreamingJsonPreviewException { Failure: StreamingJsonPreviewFailure.CharacterLimit or StreamingJsonPreviewFailure.DepthLimit } => OpenAICompletionsWireFailure.ResourceLimit,
            EcmaScriptJsonProjectionException { Failure: EcmaScriptJsonProjectionFailure.ResourceLimit } => OpenAICompletionsWireFailure.ResourceLimit,
            UnsupportedSignalException => OpenAICompletionsWireFailure.UnsupportedFeature,
            ProviderSignalException => OpenAICompletionsWireFailure.ProviderError,
            _ => OpenAICompletionsWireFailure.MalformedStream
        };
    private static StreamProtocolException Protocol() => new("Invalid Completions chunk stream.");
    internal static NativeChatDiagnostic Diagnostic(OpenAICompletionsWireFailure failure) =>
        new(NativeChatAdapter.OpenAICompletions, failure switch
        {
            OpenAICompletionsWireFailure.SourceFailed => NativeChatFailureCode.SourceFailed,
            OpenAICompletionsWireFailure.MalformedStream => NativeChatFailureCode.MalformedStream,
            OpenAICompletionsWireFailure.UnexpectedEof => NativeChatFailureCode.UnexpectedEof,
            OpenAICompletionsWireFailure.ResourceLimit => NativeChatFailureCode.ResourceLimit,
            OpenAICompletionsWireFailure.ProviderError => NativeChatFailureCode.ProviderError,
            OpenAICompletionsWireFailure.Cancelled => NativeChatFailureCode.Cancelled,
            OpenAICompletionsWireFailure.CleanupFailed => NativeChatFailureCode.CleanupFailed,
            OpenAICompletionsWireFailure.UnsupportedFeature => NativeChatFailureCode.UnsupportedFeature,
            _ => throw new StreamProtocolException("Invalid Completions failure classification.")
        });
    private static StreamLimitException Limit() => new("Completions chunk stream exceeds configured limits.");
    private sealed class UnsupportedSignalException : Exception { }
    private sealed class ProviderSignalException : Exception { }
    private static JsonData TextData(string value) => JsonData.Parse(JsonSerializer.Serialize(value));

    private sealed class ToolSlot(int contentIndex, string id, string name)
    {
        public int ContentIndex { get; } = contentIndex;
        public string Id { get; set; } = id;
        public string Name { get; set; } = name;
        public int? WireIndex { get; set; }
        public string RawArguments { get; set; } = "";
        public JsonData DisplayArguments { get; set; } = JsonData.EmptyObject;
        public bool Ended { get; set; }
        public string? CustomProperty { get; set; }
        public string CustomInput { get; set; } = "";
        public bool CustomJsonStarted { get; set; }
        public bool CustomJsonClosed { get; set; }
        public bool UndefinedPartialArguments { get; set; }
    }

    private sealed class State
    {
        private readonly OpenAICompletionsWireOptions _options;
        private readonly OpenAICompletionsTokenRates _rates;
        private readonly AssistantStreamReducer _reducer;
        private readonly Dictionary<int, ToolSlot> _toolsByIndex = [];
        private readonly Dictionary<string, ToolSlot> _toolsById = new(StringComparer.Ordinal);
        private readonly List<ToolSlot> _tools = [];
        private readonly JsonArray _reasoningDetails = new();
        private readonly IReadOnlyDictionary<string, string> _grammarInputs;
        private JsonFields _properties = JsonFields.Empty;
        private readonly List<string> _sourcePropertyOrder = [];
        private TokenUsage _usage = TokenUsage.Zero;
        private int? _textIndex, _thinkingIndex;
        private int _chunks;
        private long _inputCharacters, _contentCharacters, _usageCharacters, _costCharacters,
            _reasoningDetailCharacters, _sourceSnapshotCharacters;
        private bool _hasFinishReason;
        private bool _sourceResponseIdAssigned;
        private JsonData? _sourceResponseId;
        private JsonData? _errorThinkingSignature;
        private bool _errorThinkingSignatureAttempted;
        private StopReason _reason = StopReason.Pending;
        public StreamStarted Start { get; }

        public State(ChatRequest request, OpenAICompletionsWireOptions options, CancellationToken token)
        {
            if (request.Model is null || request.Model.Api != "openai-completions" ||
                string.IsNullOrWhiteSpace(request.Model.Provider) || string.IsNullOrWhiteSpace(request.Model.Id)) throw Protocol();
            Unicode(request.Model.Provider); Unicode(request.Model.Id);
            _options = options; _rates = options.Rates ?? new();
            _grammarInputs = options.SupportsOpenAIGrammarTools
                ? new CompletionsToolDeclarationProjector((options.ToolDeclarations ?? new()) with
                    { SupportsOpenAIGrammarTools = true }).GrammarInputProperties(request, token)
                : new Dictionary<string, string>();
            Charge(request.Model.Api.Length + (long)request.Model.Provider.Length + request.Model.Id.Length);
            Start = new(new(request.Model.Api, request.Model.Provider, request.Model.Id, request.Timestamp,
                [], TokenUsage.Zero, StopReason.Pending));
            // The reducer retains raw tool previews and strict final arguments separately.
            // Logical producer payload is charged once below; the fixed diagnostic reserve is not payload admission.
            var reducerCharacters = (int)Math.Min(int.MaxValue, 2L * options.MaximumContentCharacters + 1024);
            _reducer = new(Start.Partial, new(options.MaximumContentSlots, reducerCharacters));
            _reducer.Apply(Start);
        }

        public StreamStarted CaptureStart() => _options.CaptureSourceEmissionSnapshots
            ? Start with { SourceEmissionSnapshot = Capture(Start), SourceDrainSnapshot = Capture(Start) } : Start;

        private JsonData Capture(StreamEvent item)
        {
            var emission = CaptureLive(item);
            var size = emission.ToString().Length;
            if (size > _options.MaximumContentCharacters - _contentCharacters - _sourceSnapshotCharacters) throw Limit();
            _sourceSnapshotCharacters += size;
            return emission;
        }

        public JsonData CaptureLive(StreamEvent item)
        {
            var snapshot = _reducer.Snapshot() with { Usage = _usage, StopReason = _reason, ExtraProperties = _properties };
            if (item is StreamTerminalEvent terminal) snapshot = terminal.Message;
            return CompletionsSourceEventProjection.Capture(item is ToolCallHeaderUpdated ? Start : item, snapshot,
                _tools.Select(tool => new CompletionsToolSourceView(tool.ContentIndex, tool.WireIndex,
                    tool.RawArguments, tool.DisplayArguments, tool.Ended)
                    {
                        CustomInput = tool.CustomProperty is null ? null : JsonData.Parse(new JsonObject
                        {
                            ["property"] = tool.CustomProperty, ["jsonBuffer"] = new JsonObject
                            { ["input"] = tool.CustomInput, ["started"] = tool.CustomJsonStarted, ["closed"] = tool.CustomJsonClosed }
                        }.ToJsonString(CompletionsJson.Output)),
                        UndefinedPartialArguments = tool.UndefinedPartialArguments
                    }).ToArray(), _sourceResponseIdAssigned, _sourceResponseId, _sourcePropertyOrder);
        }

        public AssistantMessage Current() => _reducer.Snapshot() with { Usage = _usage, StopReason = _reason, ExtraProperties = _properties };

        private void Emit(StreamEvent item, List<StreamEvent> progress)
        {
            _reducer.Apply(item);
            progress.Add(_options.CaptureSourceEmissionSnapshots && item is not ToolCallHeaderUpdated
                ? item with { SourceEmissionSnapshot = Capture(item) } : item);
        }

        private void CaptureDrainBatch(List<StreamEvent> progress)
        {
            if (!_options.CaptureSourceEmissionSnapshots) return;
            // The qualified driver drains while the source awaits the next SDK
            // chunk. Pushes within this synchronous batch share its final partial.
            for (var index = 0; index < progress.Count; index++)
                if (progress[index] is not ToolCallHeaderUpdated)
                    progress[index] = progress[index] with { SourceDrainSnapshot = Capture(progress[index]) };
        }

        public void Process(JsonData data, List<StreamEvent> progress)
        {
            if (data is null) throw Protocol();
            var raw = data.ToString();
            if (++_chunks > _options.MaximumChunks || raw.Length > _options.MaximumChunkCharacters ||
                raw.Length > _options.MaximumInputCharacters - _inputCharacters) throw Limit();
            _inputCharacters += raw.Length;
            // Strictly reparse retained permissive JSON and validate every opaque field before dispatch.
            var value = JsonData.Parse(raw).Value;
            CheckJson(value, 0); Object(value);
            if (value.TryGetProperty("error", out var error) && IsTruthy(error))
                throw new ProviderSignalException();
            var objectKind = OptionalString(value, "object");
            if (!string.IsNullOrEmpty(objectKind) && objectKind != "chat.completion.chunk") throw new UnsupportedSignalException();
            var id = OptionalString(value, "id");
            if (_sourceResponseId is null || _sourceResponseId.Value.ValueKind == JsonValueKind.Null ||
                _sourceResponseId.Value.ValueKind == JsonValueKind.String && _sourceResponseId.Value.GetString()!.Length == 0)
            {
                if (!_sourceResponseIdAssigned) _sourcePropertyOrder.Add("responseId");
                _sourceResponseIdAssigned = true;
                _sourceResponseId = value.TryGetProperty("id", out var rawId) ? JsonData.FromElement(rawId) : null;
            }
            if (!string.IsNullOrEmpty(id) && !_properties.TryGet("responseId", out _)) Set("responseId", id);
            var model = OptionalString(value, "model");
            if (!string.IsNullOrEmpty(model) && model != Start.Partial.Model && !_properties.TryGet("responseModel", out _))
                Set("responseModel", model);
            var hasUsage = value.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null;
            if (hasUsage) ReadUsage(Object(usage));
            if (!value.TryGetProperty("choices", out var choices)) return;
            if (choices.ValueKind != JsonValueKind.Array) throw Protocol();
            if (choices.GetArrayLength() == 0 || choices[0].ValueKind == JsonValueKind.Null) return;
            var choice = Object(choices[0]);
            if (!hasUsage && choice.TryGetProperty("usage", out var choiceUsage) && choiceUsage.ValueKind != JsonValueKind.Null)
                ReadUsage(Object(choiceUsage));
            var finish = OptionalString(choice, "finish_reason");
            if (!string.IsNullOrEmpty(finish))
            {
                Set("rawStopReason", finish); _hasFinishReason = true;
                _reason = finish switch
                {
                    "stop" or "end" => StopReason.Stop,
                    "length" => StopReason.Length,
                    "function_call" or "tool_calls" => StopReason.ToolUse,
                    _ => StopReason.Error
                };
            }
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind == JsonValueKind.Null) return;
            Object(delta);
            foreach (var property in delta.EnumerateObject())
            {
                if (property.Name is "role" or "content" or "reasoning_content" or "reasoning" or "reasoning_text" or "tool_calls" or "reasoning_details")
                    continue;
                if (property.Name == "reasoning_details" && (property.Value.ValueKind == JsonValueKind.Null ||
                    property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() == 0)) continue;
                if ((property.Name is "audio" or "refusal" or "function_call") && property.Value.ValueKind == JsonValueKind.Null) continue;
                throw new UnsupportedSignalException();
            }
            var role = OptionalString(delta, "role");
            if (role is not null && role != "assistant") throw new UnsupportedSignalException();
            void Push(StreamEvent item) => Emit(item, progress);
            if (delta.TryGetProperty("content", out var contentValue) && contentValue.ValueKind == JsonValueKind.Array)
                throw new UnsupportedSignalException();
            var text = OptionalString(delta, "content");
            if (!string.IsNullOrEmpty(text))
            {
                Charge(text.Length);
                if (_textIndex is null)
                { _textIndex = NextIndex(); Push(new TextStarted(_textIndex.Value, new(""))); }
                Push(new TextDelta(_textIndex.Value, text));
            }
            foreach (var field in new[] { "reasoning_content", "reasoning", "reasoning_text" })
            {
                var reasoning = OptionalString(delta, field);
                if (string.IsNullOrEmpty(reasoning)) continue;
                Charge(reasoning.Length);
                if (_thinkingIndex is null)
                {
                    _thinkingIndex = NextIndex();
                    var signature = Start.Partial.Provider == "opencode-go" && field == "reasoning" ? "reasoning_content" : field;
                    var properties = JsonFields.Empty.Set("thinkingSignature", TextData(signature));
                    Charge("thinkingSignature".Length + properties.Values["thinkingSignature"].ToString().Length);
                    Push(new ThinkingStarted(_thinkingIndex.Value, new("", properties)));
                }
                Push(new ThinkingDelta(_thinkingIndex.Value, reasoning));
                break;
            }
            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind != JsonValueKind.Null)
            {
                if (toolCalls.ValueKind != JsonValueKind.Array) throw Protocol();
                if (toolCalls.GetArrayLength() > _options.MaximumContentSlots) throw Limit();
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    Object(toolCall);
                    var isCustom = toolCall.TryGetProperty("custom", out var custom) && custom.ValueKind != JsonValueKind.Null;
                    if (isCustom && !_options.SupportsOpenAIGrammarTools)
                        throw new UnsupportedSignalException();
                    if (isCustom) Object(custom);
                    var kind = OptionalString(toolCall, "type");
                    if (kind is not null && kind != "function" && !(kind == "custom" && _options.SupportsOpenAIGrammarTools)) throw new UnsupportedSignalException();
                    var callId = OptionalString(toolCall, "id");
                    int? wireIndex = null;
                    if (toolCall.TryGetProperty("index", out var index) && index.ValueKind != JsonValueKind.Null)
                    { var number = Number(index); wireIndex = number <= int.MaxValue ? (int)number : throw Protocol(); }
                    JsonElement function = default;
                    if (toolCall.TryGetProperty("function", out function) && function.ValueKind != JsonValueKind.Null) Object(function);
                    var name = function.ValueKind == JsonValueKind.Object ? OptionalString(function, "name") : null;
                    name ??= isCustom ? OptionalString(custom, "name") : null;
                    var arguments = function.ValueKind == JsonValueKind.Object ? OptionalString(function, "arguments") : null;
                    var customInput = isCustom ? OptionalString(custom, "input") : null;
                    ToolSlot? slot = null;
                    if (wireIndex is { } knownIndex) _toolsByIndex.TryGetValue(knownIndex, out slot);
                    if (!string.IsNullOrEmpty(callId) && _toolsById.TryGetValue(callId, out var byId))
                    {
                        if (slot is not null && !ReferenceEquals(slot, byId)) throw Protocol();
                        slot ??= byId;
                    }
                    if (slot is null)
                    {
                        if (wireIndex is null && string.IsNullOrEmpty(callId)) throw Protocol();
                        callId ??= ""; name ??= "";
                        if (callId.Contains('\0') || name.Contains('\0')) throw Protocol();
                        Charge(callId.Length + (long)name.Length + 2);
                        slot = new(NextIndex(), callId, name) { WireIndex = wireIndex };
                        if (isCustom && function.ValueKind != JsonValueKind.Object) InitializeCustom(slot, undefinedPartial: true);
                        _tools.Add(slot);
                        if (callId.Length > 0) _toolsById.Add(callId, slot);
                        if (wireIndex is { } firstIndex) _toolsByIndex.Add(firstIndex, slot);
                        var call = new ToolCallContent(callId, name, JsonData.EmptyObject);
                        if (string.IsNullOrEmpty(callId) || string.IsNullOrEmpty(name))
                            Push(new ToolCallProvisionalStarted(slot.ContentIndex, call));
                        else Push(new ToolCallStarted(slot.ContentIndex, call));
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(callId) && slot.Id.Length > 0 && callId != slot.Id ||
                            !string.IsNullOrEmpty(name) && slot.Name.Length > 0 && name != slot.Name)
                            throw Protocol();
                        if (wireIndex is { } laterIndex)
                        {
                            if (slot.WireIndex is { } originalIndex && originalIndex != laterIndex) throw Protocol();
                            if (slot.WireIndex is null) { slot.WireIndex = laterIndex; _toolsByIndex.Add(laterIndex, slot); }
                        }
                        var filled = false;
                        if (slot.Id.Length == 0 && !string.IsNullOrEmpty(callId))
                        { if (callId.Contains('\0')) throw Protocol(); Charge(callId.Length); slot.Id = callId; _toolsById.Add(callId, slot); filled = true; }
                        if (slot.Name.Length == 0 && !string.IsNullOrEmpty(name))
                        { if (name.Contains('\0')) throw Protocol(); Charge(name.Length); slot.Name = name; filled = true; }
                        if (filled) Push(new ToolCallHeaderUpdated(slot.ContentIndex, slot.Id, slot.Name));
                    }
                    if (wireIndex is null && string.IsNullOrEmpty(callId)) throw Protocol();
                    if (isCustom && slot.CustomProperty is null) InitializeCustom(slot, undefinedPartial: false);
                    if (!string.IsNullOrEmpty(arguments))
                    {
                        Charge(arguments.Length);
                        if (_options.CaptureSourceEmissionSnapshots)
                        {
                            slot.RawArguments += arguments;
                            // The partial carries block.arguments = parseStreamingJson(block.partialArgs).
                            slot.DisplayArguments = StreamingJson.Parse(slot.RawArguments);
                        }
                        Push(new ToolCallDelta(slot.ContentIndex, arguments));
                    }
                    else if (isCustom && !string.IsNullOrEmpty(customInput))
                        Push(new ToolCallDelta(slot.ContentIndex, AppendCustom(slot, customInput, close: false)));
                    else Push(new ToolCallDelta(slot.ContentIndex, ""));
                }
            }
            if (delta.TryGetProperty("reasoning_details", out var details) && details.ValueKind == JsonValueKind.Array)
                foreach (var detail in details.EnumerateArray())
                {
                    if (!ReasoningDetail(detail)) continue;
                    if (_thinkingIndex is null)
                    {
                        _thinkingIndex = NextIndex();
                        var properties = JsonFields.Empty.Set("thinkingSignature", TextData(""));
                        Charge("thinkingSignature".Length + 2);
                        Push(new ThinkingStarted(_thinkingIndex.Value, new("", properties)));
                    }
                    AppendDetail(detail);
                }
            CaptureDrainBatch(progress);
        }

        public List<StreamEvent> Finish(OpenAICompletionsWireFailure? failure)
        {
            if (failure is null && !_hasFinishReason)
            {
                if (_options.SupportsFinishReason) failure = OpenAICompletionsWireFailure.UnexpectedEof;
            }
            if (failure is null && _reason == StopReason.Error) failure = OpenAICompletionsWireFailure.ProviderError;
            var progress = new List<StreamEvent>();
            if (failure is null)
            {
                try
                {
                    // Parse every final before exposing any tool end.
                    var finals = new Dictionary<int, ToolCallContent>();
                    var snapshot = _reducer.Snapshot();
                    var identities = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var slot in _tools)
                    {
                        if (string.IsNullOrWhiteSpace(slot.Id) || string.IsNullOrWhiteSpace(slot.Name) || !identities.Add(slot.Id)) throw Protocol();
                        // openai-completions.ts finishCurrentBlock: block.arguments = parseStreamingJson(block.partialArgs); a custom tool
                        // call keeps the arguments its input built.
                        var parsed = slot.CustomProperty is null ? StreamingJson.Parse(_reducer.GetToolJsonPreview(slot.ContentIndex))
                            : JsonData.Parse(CustomArguments(slot).ToString());
                        finals.Add(slot.ContentIndex, new(slot.Id, slot.Name, parsed));
                    }
                    for (var index = 0; index < snapshot.Content.Length; index++)
                    {
                        JsonFields? thinkingProperties = snapshot.Content[index].ExtraProperties;
                        if (snapshot.Content[index] is ThinkingContent && _reasoningDetails.Count > 0)
                        {
                            var signature = EcmaScriptJsonProjection.Project(_reasoningDetails.ToJsonString(), ProjectionLimits());
                            var previous = 0;
                            if (thinkingProperties is not null && thinkingProperties.TryGet("thinkingSignature", out var old)) previous = old!.ToString().Length;
                            var value = TextData(signature); Charge(value.ToString().Length - previous);
                            thinkingProperties = (thinkingProperties ?? JsonFields.Empty).Set("thinkingSignature", value);
                        }
                        StreamEvent ended = snapshot.Content[index] switch
                        {
                            TextContent text => new TextEnded(index, text.Text, text.ExtraProperties),
                            ThinkingContent thinking => new ThinkingEnded(index, thinking.Thinking, thinkingProperties),
                            ToolCallContent => new ToolCallEnded(index, finals[index]),
                            _ => throw Protocol()
                        };
                        if (ended is ToolCallEnded)
                        {
                            var slot = _tools.Single(tool => tool.ContentIndex == index);
                            if (slot.CustomProperty is not null)
                                Emit(new ToolCallDelta(index, AppendCustom(slot, "", close: true)), progress);
                            slot.Ended = true;
                        }
                        Emit(ended, progress);
                    }
                    CaptureDrainBatch(progress);
                }
                catch (Exception error) { failure = Classify(error, CancellationToken.None); }
            }
            var finalSnapshot = _reducer.Snapshot();
            if (failure is not null) finalSnapshot = RetainErrorThinkingDetails(finalSnapshot);
            // Pi finalizes blocks while their publication partials are still pending, then infers the terminal reason.
            if (failure is null && !_hasFinishReason && !_options.SupportsFinishReason)
                _reason = _tools.Count > 0 ? StopReason.ToolUse : StopReason.Stop;
            var reason = failure is null ? _reason : failure == OpenAICompletionsWireFailure.Cancelled ? StopReason.Aborted : StopReason.Error;
            var properties = _properties;
            if (failure is { } rejected)
            {
                properties = properties.Set("errorMessage", TextData(rejected == OpenAICompletionsWireFailure.Cancelled
                        ? "Completions stream was cancelled." : "Completions stream did not complete."));
            }
            var final = finalSnapshot with { Usage = _usage, StopReason = reason, ExtraProperties = properties };
            StreamTerminalEvent terminal = failure is null ? new StreamDone(reason, final) : new StreamError(reason, final);
            if (failure is { } nativeFailure) terminal = terminal with { NativeDiagnostic = Diagnostic(nativeFailure) };
            // A resource failure may leave no space for another source-facing copy.
            // The native sanitized terminal still settles; no fabricated source trace
            // or successful tool permission substitutes for that missing snapshot.
            try
            {
                if (_options.CaptureSourceEmissionSnapshots)
                    terminal = terminal with { SourceEmissionSnapshot = Capture(terminal), SourceDrainSnapshot = Capture(terminal) };
            }
            catch (StreamLimitException)
            {
                if (failure is null)
                {
                    var limited = final with { StopReason = StopReason.Error, ExtraProperties = properties
                        .Set("errorMessage", TextData("Completions stream did not complete.")) };
                    terminal = new StreamError(StopReason.Error, limited) { NativeDiagnostic = Diagnostic(OpenAICompletionsWireFailure.ResourceLimit) };
                }
            }
            progress.Add(terminal); return progress;
        }

        private AssistantMessage RetainErrorThinkingDetails(AssistantMessage snapshot)
        {
            if (_reasoningDetails.Count == 0 || _thinkingIndex is not { } index ||
                snapshot.Content[index] is not ThinkingContent thinking) return snapshot;
            if (!_errorThinkingSignatureAttempted)
            {
                _errorThinkingSignatureAttempted = true;
                try
                {
                    // Pi's catch applies accumulated replay metadata without publishing thinking_end.
                    // Charge the owned terminal overlay once, including repeated cancellation Finish calls.
                    var signature = TextData(EcmaScriptJsonProjection.Project(_reasoningDetails.ToJsonString(), ProjectionLimits()));
                    var previous = thinking.ExtraProperties is { } properties && properties.TryGet("thinkingSignature", out var old)
                        ? old!.ToString().Length : 0;
                    Charge(signature.ToString().Length - previous);
                    _errorThinkingSignature = signature;
                }
                catch (StreamLimitException) { }
                catch (EcmaScriptJsonProjectionException error) when (error.Failure == EcmaScriptJsonProjectionFailure.ResourceLimit) { }
                // Resource exhaustion still settles the original sanitized error/abort terminal.
            }
            if (_errorThinkingSignature is not { } retained) return snapshot;
            var updated = new ThinkingContent(thinking.Thinking,
                (thinking.ExtraProperties ?? JsonFields.Empty).Set("thinkingSignature", retained));
            return snapshot with { Content = snapshot.Content.SetItem(index, updated) };
        }

        private EcmaScriptJsonProjectionOptions ProjectionLimits() => new(
            MaximumInputCharacters: _options.MaximumContentCharacters,
            MaximumInputBytes: (int)Math.Min(int.MaxValue, 4L * _options.MaximumContentCharacters),
            MaximumOutputCharacters: _options.MaximumContentCharacters,
            MaximumOutputBytes: (int)Math.Min(int.MaxValue, 4L * _options.MaximumContentCharacters),
            MaximumDepth: _options.MaximumJsonDepth, MaximumStringCharacters: _options.MaximumContentCharacters);

        private void InitializeCustom(ToolSlot slot, bool undefinedPartial)
        {
            slot.CustomProperty = _grammarInputs.GetValueOrDefault(slot.Name, "input");
            Charge(slot.CustomProperty.Length + 2L);
            slot.DisplayArguments = CustomArguments(slot);
            slot.RawArguments = "";
            slot.UndefinedPartialArguments = undefinedPartial;
        }

        private JsonData CustomArguments(ToolSlot slot) => JsonData.Parse(EcmaScriptJsonProjection.Project(
            new JsonObject { [slot.CustomProperty!] = slot.CustomInput }.ToJsonString(CompletionsJson.Output), ProjectionLimits()));

        private string JsonString(string value) => EcmaScriptJsonProjection.Project(JsonSerializer.Serialize(value), ProjectionLimits());

        private string AppendCustom(ToolSlot slot, string input, bool close)
        {
            if (slot.CustomProperty is null || slot.CustomJsonClosed) throw Protocol();
            Unicode(input);
            var encoded = JsonString(input);
            var delta = (slot.CustomJsonStarted ? "" : "{" + JsonString(slot.CustomProperty) + ":\"") + encoded[1..^1] + (close ? "\"}" : "");
            Charge(input.Length + (long)delta.Length);
            slot.CustomInput += input;
            slot.CustomJsonStarted = true; slot.CustomJsonClosed = close;
            slot.DisplayArguments = CustomArguments(slot);
            return delta;
        }

        private static bool ReasoningDetail(JsonElement detail)
        {
            if (detail.ValueKind != JsonValueKind.Object) return false;
            if (detail.TryGetProperty("id", out var id) && id.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
                detail.TryGetProperty("format", out var format) && format.ValueKind != JsonValueKind.String ||
                detail.TryGetProperty("index", out var index) && index.ValueKind != JsonValueKind.Number) return false;
            if (!detail.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return false;
            bool Text(string field) => detail.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String;
            return type.GetString() switch
            {
                "reasoning.summary" => Text("summary"), "reasoning.encrypted" => Text("data"),
                "reasoning.text" => Text("text") && (!detail.TryGetProperty("signature", out var signature) || signature.ValueKind is JsonValueKind.String or JsonValueKind.Null),
                _ => false
            };
        }

        private void AppendDetail(JsonElement detail)
        {
            var next = JsonNode.Parse(detail.GetRawText())!.AsObject();
            var type = next["type"]!.GetValue<string>();
            var last = _reasoningDetails.Count > 0 ? _reasoningDetails[^1]!.AsObject() : null;
            if (last is not null && last["type"]!.GetValue<string>() == type && (type is "reasoning.summary" or "reasoning.text"))
            {
                var field = type == "reasoning.summary" ? "summary" : "text";
                last[field] = last[field]!.GetValue<string>() + next[field]!.GetValue<string>();
                foreach (var common in type == "reasoning.text" ? new[] { "signature", "id", "format", "index" } : new[] { "id", "format", "index" })
                {
                    var current = last[common];
                    var empty = current is null || (common is "format" or "signature") && current.GetValue<string>().Length == 0;
                    if (!empty) continue;
                    if (next.ContainsKey(common)) last[common] = next[common]?.DeepClone();
                    else last.Remove(common); // Source assignment of undefined is omitted by JSON.stringify.
                }
            }
            else _reasoningDetails.Add(next);
            var size = _reasoningDetails.ToJsonString().Length;
            Charge(size - _reasoningDetailCharacters); _reasoningDetailCharacters = size;
        }

        private void ReadUsage(JsonElement usage)
        {
            var prompt = Count(usage, "prompt_tokens"); var output = Count(usage, "completion_tokens");
            long? read = null;
            var write = 0L; var reasoning = 0L;
            if (usage.TryGetProperty("prompt_tokens_details", out var promptDetails) && promptDetails.ValueKind != JsonValueKind.Null)
            {
                Object(promptDetails);
                read = OptionalCount(promptDetails, "cached_tokens"); write = Count(promptDetails, "cache_write_tokens");
            }
            // Nullish priority is short-circuited: ignored lower-priority values are not coerced into counts.
            var cacheRead = read ?? OptionalCount(usage, "prompt_cache_hit_tokens") ?? OptionalCount(usage, "cached_tokens") ?? 0;
            if (usage.TryGetProperty("completion_tokens_details", out var outputDetails) && outputDetails.ValueKind != JsonValueKind.Null)
                reasoning = Count(Object(outputDetails), "reasoning_tokens");
            if (reasoning > output) throw Protocol();
            var input = Math.Max(0, checked(prompt - cacheRead - write));
            var extras = JsonFields.Empty.Set("reasoning", JsonData.Parse(reasoning.ToString(CultureInfo.InvariantCulture)));
            var extraCharacters = "reasoning".Length + extras.Values["reasoning"].ToString().Length;
            Charge(extraCharacters - _usageCharacters); _usageCharacters = extraCharacters;
            // Pinned models.ts: three divide-then-multiply terms, cache write
            // multiplies before dividing, and the total is left associative.
            // Pi abe508 models.ts calculateCost: the prompt-length tier is chosen from Number token sums.
            var rates = PromptLengthPricing.TrySelect(_rates.Tiers, candidate => (double)candidate.InputTokensAbove, (double)input, cacheRead, write, out var tier)
                ? (Input: tier.Input, Output: tier.Output, CacheRead: tier.CacheRead, CacheWrite: tier.CacheWrite) : (_rates.Input, _rates.Output, _rates.CacheRead, _rates.CacheWrite);
            var costInput = (double)rates.Input / 1_000_000d * input;
            var costOutput = (double)rates.Output / 1_000_000d * output;
            var costRead = (double)rates.CacheRead / 1_000_000d * cacheRead;
            var costWrite = (double)rates.CacheWrite * write / 1_000_000d;
            var total = ((costInput + costOutput) + costRead) + costWrite;
            if (!double.IsFinite(total)) throw Protocol();
            var sourceCost = JsonData.Parse(EcmaScriptJsonProjection.Project(JsonSerializer.Serialize(new
            { input = costInput, output = costOutput, cacheRead = costRead, cacheWrite = costWrite, total }),
                new(MaximumInputCharacters: 4096, MaximumInputBytes: 16384, MaximumOutputCharacters: 4096,
                    MaximumOutputBytes: 16384, MaximumDepth: 2, MaximumNodes: 16, MaximumPropertiesPerObject: 5,
                    MaximumNumbers: 5, MaximumNumberCharacters: 64, MaximumTotalNumberCharacters: 320,
                    MaximumStringCharacters: 64)));
            var source = sourceCost.Value;
            var costCharacters = sourceCost.ToString().Length;
            Charge(costCharacters - _costCharacters); _costCharacters = costCharacters;
            _usage = new(input, output, cacheRead, write, checked(input + output + cacheRead + write),
                new(source.GetProperty("input").GetDecimal(), source.GetProperty("output").GetDecimal(),
                    source.GetProperty("cacheRead").GetDecimal(), source.GetProperty("cacheWrite").GetDecimal(),
                    source.GetProperty("total").GetDecimal(), SourceBinary64Cost: sourceCost), extras) { ExtrasBeforeTotal = true };
        }
        private void Set(string name, string text)
        {
            var value = TextData(text);
            var previous = _properties.TryGet(name, out var old) ? name.Length + (long)old!.ToString().Length : 0;
            Charge(name.Length + (long)value.ToString().Length - previous);
            if (!_sourcePropertyOrder.Contains(name)) _sourcePropertyOrder.Add(name);
            _properties = _properties.Set(name, value);
        }
        private void Charge(long characters)
        { if (characters > _options.MaximumContentCharacters - _contentCharacters - _sourceSnapshotCharacters) throw Limit(); _contentCharacters += characters; }
        private int NextIndex()
        { var next = _reducer.Snapshot().Content.Length; return next < _options.MaximumContentSlots ? next : throw Limit(); }
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
            else if (value.ValueKind == JsonValueKind.Number &&
                (!value.TryGetDouble(out var number) || !double.IsFinite(number))) throw Protocol();
        }
        // The SDK's error branch tests JavaScript truthiness after JSON.parse, including binary64 underflow.
        // CheckJson has already rejected malformed/nonfinite JSON; objects and arrays are truthy even when empty.
        private static bool IsTruthy(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
            JsonValueKind.Number => value.GetDouble() != 0d,
            JsonValueKind.String => value.GetString()!.Length != 0,
            _ => true
        };
        private static void Unicode(string value)
        {
            for (var index = 0; index < value.Length; index++)
                if (char.IsHighSurrogate(value[index]))
                { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) throw Protocol(); }
                else if (char.IsLowSurrogate(value[index])) throw Protocol();
        }
        private static JsonElement Object(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value : throw Protocol();
        private static string? OptionalString(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null) return null;
            return item.ValueKind == JsonValueKind.String ? item.GetString() : throw Protocol();
        }
        private static long Number(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) ||
                number < 0 || number > 9_007_199_254_740_991) throw Protocol();
            return number;
        }
        private static long? OptionalCount(JsonElement value, string name) =>
            value.TryGetProperty(name, out var item) && item.ValueKind != JsonValueKind.Null ? Number(item) : null;
        private static long Count(JsonElement value, string name) => OptionalCount(value, name) ?? 0;
    }
}
