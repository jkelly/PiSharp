using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAICompletions;

public enum CompletionsCacheRetention { None, Short, Long }
public enum CompletionsReasoningFormat { OpenAI, OpenRouter }
public sealed record CompletionsKeyAuthRequestOptions(double? MaxTokens = null, double? Temperature = null,
    string MaxTokensField = "max_completion_tokens", bool SupportsStore = true, bool SupportsUsageInStreaming = true,
    bool SupportsReasoningEffort = true, string? ReasoningEffort = null, CompletionsCacheRetention CacheRetention = CompletionsCacheRetention.Short,
    bool SupportsLongCacheRetention = true, string? SessionId = null, bool SendSessionAffinityHeaders = false,
    string SessionAffinityFormat = "openai", JsonData? ToolChoice = null, JsonData? ModelHeaders = null, JsonData? Headers = null,
    int MaximumKeyCharacters = 4096, int MaximumEndpointCharacters = 4096, int MaximumModelCharacters = 1024,
    int MaximumPayloadBytes = 1_048_576, int MaximumPayloadDepth = 32, int MaximumHeaders = 128,
    int MaximumHeaderCharacters = 8192, int MaximumTotalHeaderCharacters = 32_768, double MaximumTokenMagnitude = 1_000_000,
    double MaximumTemperatureMagnitude = 2)
{
    /// <summary>Explicit provider mode; null detects OpenRouter from provider identity or endpoint.</summary>
    public CompletionsReasoningFormat? ReasoningFormat { get; init; }
    /// <summary>Owned source model mappings for off/minimal/low/medium/high/xhigh/max; string or null values.</summary>
    public JsonData? ThinkingLevelMap { get; init; }
    /// <summary>Opt-in immutable Source model metadata, including existing FrozenCatalogModel.Raw. Model compatibility is authoritative.</summary>
    public JsonData? ModelMetadata { get; init; }
    /// <summary>Source per-request sampling fields; merged after model sampling defaults and named fields.</summary>
    public JsonData? SamplingParams { get; init; }
    /// <summary>Source minimal/low/medium/high token budgets; xhigh and max use the high budget.</summary>
    public JsonData? ThinkingBudgets { get; init; }
}

/// <summary>Pure configured key request construction. No send, environment, clock, credential discovery or retry.</summary>
public sealed class CompletionsKeyAuthRequestFactory
{
    private readonly Uri _endpoint;
    private readonly ModelDescriptor _model;
    private readonly CompletionsTranscriptProjectionOptions _projectionOptions;
    private readonly CompletionsKeyAuthRequestOptions _options;
    private readonly CompletionsTranscriptProjector _transcript;
    private readonly CompletionsToolDeclarationProjector _tools;
    private readonly ImmutableArray<KeyValuePair<string, string?>> _headers;
    private readonly JsonData? _toolChoice;
    private readonly string? _cacheKey;
    private readonly string _thinkingFormat;
    private readonly ImmutableDictionary<string, string?> _thinkingLevelMap;
    private readonly ImmutableDictionary<string, double> _thinkingBudgets;
    private readonly string? _thinkingTokenBudgetField;
    private readonly double? _modelMaxTokens;
    private readonly JsonData? _chatTemplateKwargs, _chatTemplateArgs;
    private readonly bool _zaiToolStream;
    private readonly CompletionsToolDeclarationProjectionOptions _toolProjectionOptions;
    private readonly string? _cacheControlFormat;
    private readonly JsonData? _openRouterRouting, _modelSamplingParams, _samplingParams, _priority;
    /// <summary>Resolved existing wire configuration for this bound model. Callers may clone it to set stream resource limits.</summary>
    public OpenAICompletionsWireOptions ResolvedWireOptions { get; }

    public CompletionsKeyAuthRequestFactory(Uri endpoint, ModelDescriptor expectedModel,
        CompletionsTranscriptProjectionOptions? projectionOptions = null, CompletionsKeyAuthRequestOptions? options = null)
    {
        _options = options ?? new(); _projectionOptions = projectionOptions ?? new();
        if (endpoint is null || expectedModel is null || !endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
            endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0 ||
            expectedModel.Api != "openai-completions" || !Identity(expectedModel.Id) || !Identity(expectedModel.Provider) ||
            _options.MaximumKeyCharacters <= 0 || _options.MaximumEndpointCharacters <= 0 || _options.MaximumModelCharacters <= 0 ||
            _options.MaximumPayloadBytes < 2 || _options.MaximumPayloadDepth is < 1 or > 64 || _options.MaximumHeaders <= 0 ||
            _options.MaximumHeaderCharacters <= 0 || _options.MaximumTotalHeaderCharacters <= 0 ||
            !double.IsFinite(_options.MaximumTokenMagnitude) || _options.MaximumTokenMagnitude <= 0 ||
            !double.IsFinite(_options.MaximumTemperatureMagnitude) || _options.MaximumTemperatureMagnitude <= 0 ||
            _options.MaxTokens is { } max && !double.IsFinite(max) || _options.Temperature is { } temperature && !double.IsFinite(temperature) ||
            !Enum.IsDefined(_options.CacheRetention) || _options.MaxTokensField is not ("max_tokens" or "max_completion_tokens") ||
            _options.SessionAffinityFormat is not ("openai" or "openai-nosession" or "openrouter") ||
            _options.ReasoningFormat is { } format && !Enum.IsDefined(format)) throw Fail(CompletionsRequestFailure.InvalidConfiguration);
        if (endpoint.AbsoluteUri.Length > _options.MaximumEndpointCharacters || expectedModel.Id.Length > _options.MaximumModelCharacters ||
            expectedModel.Provider.Length > _options.MaximumModelCharacters || Math.Abs(_options.MaxTokens ?? 0) > _options.MaximumTokenMagnitude ||
            Math.Abs(_options.Temperature ?? 0) > _options.MaximumTemperatureMagnitude) throw Fail(CompletionsRequestFailure.ResourceLimit);
        CompletionsJson.Unicode(expectedModel.Id); CompletionsJson.Unicode(expectedModel.Provider);
        if (_options.ReasoningEffort is not (null or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
            throw Fail(CompletionsRequestFailure.UnsupportedContent);
        _endpoint = endpoint; _model = expectedModel;
        _thinkingFormat = (_options.ReasoningFormat ?? (expectedModel.Provider == "openrouter" ||
            endpoint.AbsoluteUri.Contains("openrouter.ai", StringComparison.Ordinal) ? CompletionsReasoningFormat.OpenRouter : CompletionsReasoningFormat.OpenAI)) ==
            CompletionsReasoningFormat.OpenRouter ? "openrouter" : "openai";
        _cacheControlFormat = expectedModel.Provider == "openrouter" && expectedModel.Id.StartsWith("anthropic/", StringComparison.Ordinal) ? "anthropic" : null;
        ResolvedWireOptions = new() { SupportsOpenAIGrammarTools = _projectionOptions.ToolDeclarations?.SupportsOpenAIGrammarTools == true };
        if (_options.ModelMetadata is { } metadata)
        {
            var binding = BindModelMetadata(metadata);
            _options = binding.Options; _projectionOptions = binding.Projection;
            _cacheControlFormat = binding.CacheControlFormat; _openRouterRouting = binding.Routing;
            _modelSamplingParams = binding.Sampling; _priority = binding.Priority; ResolvedWireOptions = binding.Wire;
            _thinkingFormat = binding.ThinkingFormat; _thinkingTokenBudgetField = binding.BudgetField;
            _modelMaxTokens = binding.MaxTokens; _chatTemplateKwargs = binding.TemplateKwargs;
            _chatTemplateArgs = binding.TemplateArgs; _zaiToolStream = binding.ToolStream;
        }
        _samplingParams = ReadOwnedObject(_options.SamplingParams);
        _thinkingLevelMap = ReadThinkingLevelMap(_options.ThinkingLevelMap);
        _thinkingBudgets = ReadThinkingBudgets(_options.ThinkingBudgets);
        ValidateTemplate(_thinkingFormat == "chat-template" ? _chatTemplateKwargs : _thinkingFormat == "baseten" ? _chatTemplateArgs : null);
        var depth = Math.Min(_projectionOptions.MaximumJsonDepth, _options.MaximumPayloadDepth);
        _projectionOptions = _projectionOptions with { MaximumJsonDepth = depth,
            MaximumOutputBytes = Math.Min(_projectionOptions.MaximumOutputBytes, _options.MaximumPayloadBytes),
            MaximumOutputCharacters = Math.Min(_projectionOptions.MaximumOutputCharacters, _options.MaximumPayloadBytes) };
        _transcript = new(_projectionOptions);
        var declarations = _projectionOptions.ToolDeclarations ?? new();
        _toolProjectionOptions = declarations with { MaximumMessages = Math.Min(declarations.MaximumMessages, _projectionOptions.MaximumMessages),
            MaximumEntryCharacters = Math.Min(declarations.MaximumEntryCharacters, _projectionOptions.MaximumEntryCharacters),
            MaximumInputCharacters = Math.Min(declarations.MaximumInputCharacters, _projectionOptions.MaximumInputCharacters),
            MaximumJsonDepth = Math.Min(declarations.MaximumJsonDepth, depth),
            MaximumOutputCharacters = Math.Min(declarations.MaximumOutputCharacters, _options.MaximumPayloadBytes),
            MaximumOutputBytes = Math.Min(declarations.MaximumOutputBytes, _options.MaximumPayloadBytes) };
        _tools = new(_toolProjectionOptions);
        ResolvedWireOptions = ResolvedWireOptions with { ToolDeclarations = _toolProjectionOptions };
        if (_options.ToolChoice is { } choice)
        {
            try
            {
                if (choice.ToString().Length > _options.MaximumPayloadBytes) throw Fail(CompletionsRequestFailure.ResourceLimit);
                _toolChoice = CompletionsJson.Source(CompletionsJson.Strict(choice, depth, CancellationToken.None),
                    _options.MaximumPayloadBytes, _options.MaximumPayloadBytes, depth, CancellationToken.None);
                var value = _toolChoice.Value;
                if (value.ValueKind == JsonValueKind.Null) _toolChoice = null;
                else if (value.ValueKind == JsonValueKind.String)
                { if (CompletionsJson.Text(value) is not ("none" or "auto" or "required")) throw Fail(CompletionsRequestFailure.UnsupportedContent); }
                else if (value.ValueKind != JsonValueKind.Object) throw Fail(CompletionsRequestFailure.UnsupportedContent);
                else
                {
                    var kind = CompletionsJson.String(value, "type");
                    if (kind == "function" || kind == "custom" && _toolProjectionOptions.SupportsOpenAIGrammarTools)
                    {
                        if (!Identity(CompletionsJson.String(value.GetProperty(kind), "name"))) throw Fail(CompletionsRequestFailure.UnsupportedContent);
                    }
                    else if (kind == "allowed_tools" && _toolProjectionOptions.SupportsOpenAIGrammarTools)
                    {
                        var allowed = value.GetProperty("allowed_tools");
                        if (CompletionsJson.String(allowed, "mode") is not ("auto" or "required") ||
                            !allowed.TryGetProperty("tools", out var allowedTools) || allowedTools.ValueKind != JsonValueKind.Array ||
                            allowedTools.EnumerateArray().Any(tool => tool.ValueKind != JsonValueKind.Object)) throw Fail(CompletionsRequestFailure.UnsupportedContent);
                    }
                    else throw Fail(CompletionsRequestFailure.UnsupportedContent);
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
            { throw Fail(CompletionsRequestFailure.InvalidConfiguration); }
        }
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        { ["accept"] = "application/json", ["user-agent"] = "PiSharp", ["x-stainless-retry-count"] = "0" };
        long supplied = 0;
        ReadHeaders(_options.ModelHeaders, headers, ref supplied);
        if (_options.SessionId is { } session)
        {
            CompletionsJson.Unicode(session);
            if (session.Length > _options.MaximumHeaderCharacters) throw Fail(CompletionsRequestFailure.ResourceLimit);
            _cacheKey = ClampCacheKey(session);
            if (_options.CacheRetention != CompletionsCacheRetention.None && _options.SendSessionAffinityHeaders && session.Length != 0)
            {
                HeaderValue(session);
                if (_options.SessionAffinityFormat == "openrouter") headers["x-session-id"] = session;
                else
                {
                    if (_options.SessionAffinityFormat == "openai") headers["session_id"] = session;
                    headers["x-client-request-id"] = session; headers["x-session-affinity"] = session;
                }
            }
        }
        ReadHeaders(_options.Headers, headers, ref supplied); CheckHeaders(headers, includeAuthorization: false);
        _headers = headers.ToImmutableArray();
    }

    public HttpRequestMessage Create(ChatRequest request, string explicitApiKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || request.Model != _model) throw Fail(CompletionsRequestFailure.InvalidRequest);
        if (string.IsNullOrEmpty(explicitApiKey)) throw Fail(CompletionsRequestFailure.InvalidKey);
        if (explicitApiKey.Length > _options.MaximumKeyCharacters) throw Fail(CompletionsRequestFailure.ResourceLimit);
        foreach (var character in explicitApiKey)
        { cancellationToken.ThrowIfCancellationRequested(); if (character is < '!' or > '~') throw Fail(CompletionsRequestFailure.InvalidKey); }
        var messages = _transcript.Project(request, cancellationToken);
        var tools = _tools.ProjectRequestTools(request, _projectionOptions.SupportsMidConversationSystemMessages &&
            _projectionOptions.SupportsMidConversationToolAdditions, cancellationToken);
        if (_cacheControlFormat == "anthropic" &&
            _options.CacheRetention != CompletionsCacheRetention.None)
            (messages, tools) = ApplyAnthropicCacheControl(messages, tools, cancellationToken);
        var fields = new List<KeyValuePair<string, JsonData>>(); var fieldIndices = new Dictionary<string, int>(StringComparer.Ordinal); long bytes = 2;
        void Add(string name, JsonData value)
        {
            cancellationToken.ThrowIfCancellationRequested(); CompletionsJson.Check(value.Value, 1, _options.MaximumPayloadDepth, cancellationToken);
            var replaces = fieldIndices.TryGetValue(name, out var index);
            var overhead = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(name)) + 1L + ((replaces ? index : fields.Count) == 0 ? 0 : 1);
            var previous = replaces ? overhead + Encoding.UTF8.GetByteCount(fields[index].Value.ToString()) : 0;
            var size = overhead + Encoding.UTF8.GetByteCount(value.ToString());
            if (size > _options.MaximumPayloadBytes - (bytes - previous)) throw Fail(CompletionsRequestFailure.ResourceLimit);
            bytes += size - previous;
            if (replaces) fields[index] = new(name, value);
            else { fieldIndices.Add(name, fields.Count); fields.Add(new(name, value)); }
        }
        JsonData Text(string text) => JsonData.Parse(JsonSerializer.Serialize(text, CompletionsJson.Output));
        Add("model", Text(_model.Id)); Add("messages", messages); Add("stream", JsonData.Parse("true"));
        if (_cacheKey is not null && (_endpoint.AbsoluteUri.Contains("api.openai.com", StringComparison.Ordinal) && _options.CacheRetention != CompletionsCacheRetention.None ||
            _options.CacheRetention == CompletionsCacheRetention.Long && _options.SupportsLongCacheRetention)) Add("prompt_cache_key", Text(_cacheKey));
        if (_options.CacheRetention == CompletionsCacheRetention.Long && _options.SupportsLongCacheRetention) Add("prompt_cache_retention", Text("24h"));
        if (_options.SupportsUsageInStreaming) Add("stream_options", JsonData.Parse("{\"include_usage\":true}"));
        if (_options.SupportsStore) Add("store", JsonData.Parse("false"));
        // Direct stream truthiness omits zero, but retains finite negative/fractional supplied values.
        if (_options.MaxTokens is { } count && count != 0) Add(_options.MaxTokensField, Number(count, cancellationToken));
        if (_options.Temperature is { } temperature) Add("temperature", Number(temperature, cancellationToken));
        if (tools.Value.GetArrayLength() != 0 || HasToolHistory(request)) Add("tools", tools);
        if (_zaiToolStream && tools.Value.GetArrayLength() != 0) Add("tool_stream", JsonData.Parse("true"));
        if (_priority is not null) Add("priority", _priority);
        if (_toolChoice is not null) Add("tool_choice", _toolChoice);
        AddThinkingFields(Add, cancellationToken);
        if (_openRouterRouting is not null) Add("provider", _openRouterRouting);
        foreach (var sampling in new[] { _modelSamplingParams, _samplingParams })
            if (sampling is not null) foreach (var property in CompletionsJson.Properties(sampling.Value))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Source Object.assign invokes Object.prototype.__proto__'s setter. It creates no own JSON field.
                if (property.Name != "__proto__") Add(property.Name, JsonData.FromElement(property.Value));
            }
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        { ["authorization"] = "Bearer " + explicitApiKey, ["content-type"] = "application/json" };
        foreach (var pair in _headers) headers[pair.Key] = pair.Value?.Trim(' ', '\t');
        CheckHeaders(headers, includeAuthorization: true);
        using var buffer = new MemoryStream((int)bytes);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = CompletionsJson.Output.Encoder }))
        {
            writer.WriteStartObject();
            foreach (var field in fields) { cancellationToken.ThrowIfCancellationRequested(); writer.WritePropertyName(field.Key); writer.WriteRawValue(field.Value.ToString()); }
            writer.WriteEndObject();
        }
        if (buffer.Length > _options.MaximumPayloadBytes) throw Fail(CompletionsRequestFailure.ResourceLimit);
        cancellationToken.ThrowIfCancellationRequested(); HttpRequestMessage? result = null; HttpContent? content = null;
        try
        {
            content = new ByteArrayContent(buffer.ToArray()); content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            result = new(HttpMethod.Post, _endpoint) { Content = content }; content = null;
            foreach (var header in headers)
                if (header.Value is not null && !header.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase) && !result.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    throw Fail(CompletionsRequestFailure.UnsupportedContent);
            cancellationToken.ThrowIfCancellationRequested(); return result;
        }
        catch { result?.Dispose(); content?.Dispose(); throw; }
    }

    /// <summary>Awaits an owned payload callback before transfer/send. Null retains the payload; an owned object replaces it.</summary>
    public async ValueTask<HttpRequestMessage> CreateAsync(ChatRequest request, string explicitApiKey,
        CompletionsLifecycleHooks hooks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        var owned = Create(request, explicitApiKey, cancellationToken);
        try
        {
            if (hooks.OnPayload is not { } callback) return owned;
            var bytes = await owned.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length > _options.MaximumPayloadBytes) throw Fail(CompletionsRequestFailure.ResourceLimit);
            var payload = JsonData.Parse(new UTF8Encoding(false, true).GetString(bytes));
            var undefined = new[] { "prompt_cache_key", "prompt_cache_retention" }
                .Where(name => !payload.Value.TryGetProperty(name, out _)).Select(name => "/" + name).ToImmutableArray();
            var replacement = await callback(new(payload, undefined), _model, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (replacement is not null)
            {
                var raw = replacement.ToString();
                if (raw.Length > _options.MaximumPayloadBytes || Encoding.UTF8.GetByteCount(raw) > _options.MaximumPayloadBytes)
                    throw Fail(CompletionsRequestFailure.ResourceLimit);
                JsonData admitted;
                try
                {
                    admitted = CompletionsJson.Strict(replacement, _options.MaximumPayloadDepth, cancellationToken);
                    if (admitted.Value.ValueKind != JsonValueKind.Object) throw Fail(CompletionsRequestFailure.InvalidRequest);
                    FinitePayload(admitted.Value, cancellationToken);
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
                { throw Fail(CompletionsRequestFailure.InvalidRequest); }
                var serialized = CompletionsJson.Source(admitted, _options.MaximumPayloadBytes, _options.MaximumPayloadBytes,
                    _options.MaximumPayloadDepth, cancellationToken);
                var content = new ByteArrayContent(Encoding.UTF8.GetBytes(serialized.ToString()));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                var previous = owned.Content; owned.Content = content; previous!.Dispose();
            }
            cancellationToken.ThrowIfCancellationRequested(); return owned;
        }
        catch { owned.Dispose(); throw; }
    }

    private sealed record BoundModel(CompletionsKeyAuthRequestOptions Options, CompletionsTranscriptProjectionOptions Projection,
        string? CacheControlFormat, JsonData? Routing, JsonData? Sampling, JsonData? Priority, OpenAICompletionsWireOptions Wire,
        string ThinkingFormat, string? BudgetField, double? MaxTokens, JsonData? TemplateKwargs, JsonData? TemplateArgs, bool ToolStream);

    private JsonData? ReadOwnedObject(JsonData? source)
    {
        if (source is null || source.Value.ValueKind == JsonValueKind.Null) return null;
        var raw = source.ToString();
        if (raw.Length > _options.MaximumPayloadBytes || Encoding.UTF8.GetByteCount(raw) > _options.MaximumPayloadBytes)
            throw Fail(CompletionsRequestFailure.ResourceLimit);
        try
        {
            var owned = CompletionsJson.Strict(source, _options.MaximumPayloadDepth, CancellationToken.None);
            if (owned.Value.ValueKind != JsonValueKind.Object) throw Fail(CompletionsRequestFailure.InvalidConfiguration);
            FinitePayload(owned.Value, CancellationToken.None);
            return CompletionsJson.Source(owned, _options.MaximumPayloadBytes, _options.MaximumPayloadBytes, _options.MaximumPayloadDepth, CancellationToken.None);
        }
        catch (CompletionsRequestException error) when (error.Failure is CompletionsRequestFailure.InvalidTranscript or CompletionsRequestFailure.InvalidRequest)
        { throw Fail(CompletionsRequestFailure.InvalidConfiguration); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw Fail(CompletionsRequestFailure.InvalidConfiguration); }
    }

    private BoundModel BindModelMetadata(JsonData source)
    {
        try
        {
            var model = ReadOwnedObject(source)?.Value ?? throw Fail(CompletionsRequestFailure.InvalidConfiguration);
            string Required(string name)
            {
                if (!model.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                    throw Fail(CompletionsRequestFailure.InvalidConfiguration);
                return value.GetString()!;
            }
            var provider = Required("provider"); var id = Required("id"); var baseUrl = Required("baseUrl");
            if (provider != _model.Provider || id != _model.Id || Required("api") != _model.Api ||
                !Uri.TryCreate(baseUrl.TrimEnd('/') + "/chat/completions", UriKind.Absolute, out var endpoint) || endpoint != _endpoint)
                throw Fail(CompletionsRequestFailure.InvalidConfiguration);
            if (model.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "chat"))
                throw Fail(CompletionsRequestFailure.UnsupportedContent);
            if (!model.TryGetProperty("reasoning", out var reasoning) || reasoning.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !model.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array ||
                input.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || item.GetString() is not ("text" or "image")))
                throw Fail(CompletionsRequestFailure.InvalidConfiguration);
            var compat = model.TryGetProperty("compat", out var declared) && declared.ValueKind != JsonValueKind.Null ? declared : JsonData.Parse("{}").Value;
            if (compat.ValueKind != JsonValueKind.Object) throw Fail(CompletionsRequestFailure.InvalidConfiguration);
            bool Flag(string name, bool fallback)
            {
                if (!compat.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Fail(CompletionsRequestFailure.InvalidConfiguration);
                return value.GetBoolean();
            }
            string? Text(string name, string? fallback)
            {
                if (!compat.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
                if (value.ValueKind != JsonValueKind.String) throw Fail(CompletionsRequestFailure.InvalidConfiguration);
                return value.GetString();
            }
            JsonData? Object(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) ? ReadOwnedObject(JsonData.FromElement(value)) : null;
            // Pin-compatible detection; explicit model compatibility remains authoritative.
            var isZai = provider is "zai" or "zai-coding-cn" || baseUrl.Contains("api.z.ai", StringComparison.Ordinal) || baseUrl.Contains("open.bigmodel.cn", StringComparison.Ordinal);
            var isTogether = provider == "together" || baseUrl.Contains("api.together.ai", StringComparison.Ordinal) || baseUrl.Contains("api.together.xyz", StringComparison.Ordinal);
            var isMoonshot = provider is "moonshotai" or "moonshotai-cn" || baseUrl.Contains("api.moonshot.", StringComparison.Ordinal);
            var isOpenRouter = provider == "openrouter" || baseUrl.Contains("openrouter.ai", StringComparison.Ordinal);
            var isWorkers = provider == "cloudflare-workers-ai" || baseUrl.Contains("api.cloudflare.com", StringComparison.Ordinal);
            var isGateway = provider == "cloudflare-ai-gateway" || baseUrl.Contains("gateway.ai.cloudflare.com", StringComparison.Ordinal);
            var isNvidia = provider == "nvidia" || baseUrl.Contains("integrate.api.nvidia.com", StringComparison.Ordinal);
            var isAntLing = provider == "ant-ling" || baseUrl.Contains("api.ant-ling.com", StringComparison.Ordinal);
            var isCerebras = provider == "cerebras" || baseUrl.Contains("cerebras.ai", StringComparison.Ordinal);
            var isDeepSeek = provider == "deepseek" || baseUrl.Contains("deepseek.com", StringComparison.OrdinalIgnoreCase);
            var isGrok = provider == "xai" || baseUrl.Contains("api.x.ai", StringComparison.Ordinal);
            var nonStandard = isNvidia || isCerebras || isGrok || isTogether || baseUrl.Contains("chutes.ai", StringComparison.Ordinal) || isDeepSeek || isZai || isMoonshot ||
                provider == "opencode" || baseUrl.Contains("opencode.ai", StringComparison.Ordinal) || isWorkers || isGateway || isAntLing;
            var useMaxTokens = baseUrl.Contains("chutes.ai", StringComparison.Ordinal) || isDeepSeek || isMoonshot || isGateway || isTogether || isNvidia || isAntLing || isZai;
            var detectedFormat = isDeepSeek ? "deepseek" : isZai ? "zai" : isTogether ? "together" : isAntLing ? "ant-ling" : isOpenRouter ? "openrouter" : "openai";
            var thinkingFormat = Text("thinkingFormat", detectedFormat)!;
            if (thinkingFormat is not ("openai" or "openrouter" or "zai" or "qwen" or "qwen-chat-template" or
                "chat-template" or "baseten" or "deepseek" or "ant-ling" or "together" or "string-thinking"))
                throw Fail(CompletionsRequestFailure.UnsupportedContent);
            var grammarTools = Flag("supportsOpenAIGrammarTools", false);
            var budgetField = Text("thinkingTokenBudgetField", null);
            var supportsBudget = Flag("supportsThinkingTokenBudget", false);
            if (string.IsNullOrEmpty(budgetField)) budgetField = supportsBudget ? "thinking_token_budget" : null;
            if (budgetField is not (null or "thinking_token_budget" or "thinking_budget" or "thinking_budget_tokens"))
                throw Fail(CompletionsRequestFailure.UnsupportedContent);
            double? modelMaxTokens = null;
            if (model.TryGetProperty("maxTokens", out var ceiling))
            {
                if (ceiling.ValueKind != JsonValueKind.Number || !ceiling.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw Fail(CompletionsRequestFailure.InvalidConfiguration);
                modelMaxTokens = number;
            }
            var affinityFormat = Text("sessionAffinityFormat", isOpenRouter ? "openrouter" : "openai");
            var maxTokensField = Text("maxTokensField", useMaxTokens ? "max_tokens" : "max_completion_tokens");
            var cacheFormat = Text("cacheControlFormat", provider == "openrouter" && id.StartsWith("anthropic/", StringComparison.Ordinal) ? "anthropic" : null);
            if (affinityFormat is not ("openai" or "openai-nosession" or "openrouter") || maxTokensField is not ("max_tokens" or "max_completion_tokens") || cacheFormat is not (null or "anthropic"))
                throw Fail(CompletionsRequestFailure.InvalidConfiguration);
            var options = _options with
            {
                SupportsStore = Flag("supportsStore", !nonStandard), SupportsUsageInStreaming = Flag("supportsUsageInStreaming", true),
                SupportsReasoningEffort = Flag("supportsReasoningEffort", !isGrok && !isZai && !isMoonshot && !isTogether && !isGateway && !isNvidia && !isAntLing),
                MaxTokensField = maxTokensField, SendSessionAffinityHeaders = Flag("sendSessionAffinityHeaders", isOpenRouter), SessionAffinityFormat = affinityFormat,
                SupportsLongCacheRetention = Flag("supportsLongCacheRetention", !isTogether && !isWorkers && !isGateway && !isNvidia && !isAntLing),
                ReasoningFormat = thinkingFormat == "openrouter" ? CompletionsReasoningFormat.OpenRouter : CompletionsReasoningFormat.OpenAI,
                ThinkingLevelMap = Object(model, "thinkingLevelMap"), ModelHeaders = Object(model, "headers")
            };
            var projection = _projectionOptions with
            {
                Reasoning = reasoning.GetBoolean(), ModelSupportsImages = input.EnumerateArray().Any(item => item.GetString() == "image"),
                SupportsDeveloperRole = Flag("supportsDeveloperRole", isOpenRouter && (id.StartsWith("anthropic/", StringComparison.Ordinal) || id.StartsWith("openai/", StringComparison.Ordinal)) || !nonStandard && !isOpenRouter),
              SupportsMidConversationSystemMessages = Flag("supportsMidConvoSystemMessages", false), RequiresToolResultName = Flag("requiresToolResultName", false),
              SupportsMidConversationToolAdditions = Flag("supportsMidConvoToolAdditions", false),
                RequiresAssistantAfterToolResult = Flag("requiresAssistantAfterToolResult", false), RequiresThinkingAsText = Flag("requiresThinkingAsText", false),
                RequiresReasoningContentOnAssistantMessages = Flag("requiresReasoningContentOnAssistantMessages", isDeepSeek),
                ToolDeclarations = (_projectionOptions.ToolDeclarations ?? new()) with
                    { SupportsStrictMode = Flag("supportsStrictMode", false), SupportsOpenAIGrammarTools = grammarTools }
            };
            if (!model.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object) throw Fail(CompletionsRequestFailure.InvalidConfiguration);
            decimal Rate(string name)
            {
                if (!cost.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0)
                    throw Fail(CompletionsRequestFailure.InvalidConfiguration);
                if (!value.TryGetDecimal(out var rate) || (double)rate != number) throw Fail(CompletionsRequestFailure.UnsupportedContent);
                return rate;
            }
            JsonData? priority = null;
            if (compat.TryGetProperty("vllmPriority", out var priorityValue))
            {
                if (priorityValue.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null)) throw Fail(CompletionsRequestFailure.InvalidConfiguration);
                priority = JsonData.FromElement(priorityValue);
            }
            return new(options, projection, cacheFormat, Object(compat, "openRouterRouting"), Object(model, "samplingParams"), priority,
                new(SupportsFinishReason: Flag("supportsFinishReason", true), Rates: new(Rate("input"), Rate("output"), Rate("cacheRead"), Rate("cacheWrite")))
                    { SupportsOpenAIGrammarTools = grammarTools }, thinkingFormat, budgetField, modelMaxTokens,
                Object(compat, "chatTemplateKwargs"), Object(compat, "chatTemplateArgs"), Flag("zaiToolStream", false));
        }
        catch (CompletionsRequestException error) when (error.Failure is CompletionsRequestFailure.InvalidTranscript or CompletionsRequestFailure.InvalidRequest)
        { throw Fail(CompletionsRequestFailure.InvalidConfiguration); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        { throw Fail(CompletionsRequestFailure.InvalidConfiguration); }
    }

    private (JsonData Messages, JsonData Tools) ApplyAnthropicCacheControl(JsonData messages, JsonData tools, CancellationToken token)
    {
        // Only owned request projections are changed. Source canonical history and declarations stay borrowed.
        token.ThrowIfCancellationRequested();
        var projectedMessages = JsonNode.Parse(messages.ToString())!.AsArray();
        var projectedTools = JsonNode.Parse(tools.ToString())!.AsArray();
        JsonObject CacheControl() => _options.CacheRetention == CompletionsCacheRetention.Long && _options.SupportsLongCacheRetention
            ? new() { ["type"] = "ephemeral", ["ttl"] = "1h" } : new() { ["type"] = "ephemeral" };
        bool Mark(JsonObject message)
        {
            token.ThrowIfCancellationRequested();
            if (message["content"] is JsonValue value && value.TryGetValue<string>(out var text))
            {
                if (text.Length == 0) return false;
                message["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text, ["cache_control"] = CacheControl() });
                return true;
            }
            if (message["content"] is not JsonArray parts) return false;
            for (var index = parts.Count - 1; index >= 0; index--)
            {
                token.ThrowIfCancellationRequested();
                if (parts[index] is JsonObject part && part["type"]?.GetValue<string>() == "text")
                { part["cache_control"] = CacheControl(); return true; }
            }
            return false;
        }
        foreach (var node in projectedMessages)
        {
            token.ThrowIfCancellationRequested(); var message = node!.AsObject();
            if (message["role"]!.GetValue<string>() is "system" or "developer") { Mark(message); break; }
        }
        if (projectedTools.Count != 0) projectedTools[^1]!.AsObject()["cache_control"] = CacheControl();
        for (var index = projectedMessages.Count - 1; index >= 0; index--)
        {
            token.ThrowIfCancellationRequested(); var message = projectedMessages[index]!.AsObject();
            if (message["role"]!.GetValue<string>() is "user" or "assistant" or "tool" && Mark(message)) break;
        }
        // Added content nesting and marker bytes go through the existing projection and total payload bounds.
        return (CompletionsJson.Source(JsonData.Parse(projectedMessages.ToJsonString(CompletionsJson.Output)),
                _projectionOptions.MaximumOutputCharacters, _projectionOptions.MaximumOutputBytes, _projectionOptions.MaximumJsonDepth, token),
            CompletionsJson.Source(JsonData.Parse(projectedTools.ToJsonString(CompletionsJson.Output)),
                _toolProjectionOptions.MaximumOutputCharacters, _toolProjectionOptions.MaximumOutputBytes, _toolProjectionOptions.MaximumJsonDepth, token));
    }

    private void AddThinkingFields(Action<string, JsonData> add, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var requested = _options.ReasoningEffort;
        var enabled = requested is not null;
        var budget = ResolveThinkingBudget();
        JsonData Text(string value) => JsonData.Parse(JsonSerializer.Serialize(value, CompletionsJson.Output));
        JsonData Effort(string value) => JsonData.Parse("{\"effort\":" + Text(value) + "}");
        void RequestedEffort(bool preserveNull = false)
        {
            if (!enabled || !_options.SupportsReasoningEffort) return;
            var present = _thinkingLevelMap.TryGetValue(requested!, out var mapped);
            var value = preserveNull && present ? mapped : mapped ?? requested;
            if (value is not null) add("reasoning_effort", Text(value));
        }
        void OffEffort()
        {
            if (_options.SupportsReasoningEffort && _thinkingLevelMap.TryGetValue("off", out var off) && off is not null)
                add("reasoning_effort", Text(off));
        }
        void Template(string field, JsonData? values)
        {
            if (ResolveTemplate(values, budget, token) is { } resolved) add(field, resolved);
        }
        if (_projectionOptions.Reasoning)
        {
            switch (_thinkingFormat)
            {
                case "zai":
                    add("thinking", JsonData.Parse(enabled ? "{\"type\":\"enabled\",\"clear_thinking\":false}" : "{\"type\":\"disabled\"}"));
                    RequestedEffort(preserveNull: true);
                    break;
                case "qwen":
                    add("enable_thinking", JsonData.Parse(enabled ? "true" : "false"));
                    RequestedEffort();
                    break;
                case "qwen-chat-template":
                    add("chat_template_kwargs", JsonData.Parse("{\"enable_thinking\":" + (enabled ? "true" : "false") + ",\"preserve_thinking\":true}"));
                    break;
                case "chat-template":
                    Template("chat_template_kwargs", _chatTemplateKwargs);
                    break;
                case "baseten":
                    Template("chat_template_args", _chatTemplateArgs);
                    if (enabled) RequestedEffort(preserveNull: true); else OffEffort();
                    break;
                case "deepseek":
                    if (enabled) add("thinking", JsonData.Parse("{\"type\":\"enabled\"}"));
                    else if (!_thinkingLevelMap.TryGetValue("off", out var off) || off is not null)
                        add("thinking", JsonData.Parse("{\"type\":\"disabled\"}"));
                    RequestedEffort();
                    break;
                case "openrouter":
                    if (enabled) add("reasoning", Effort(_thinkingLevelMap.GetValueOrDefault(requested!) ?? requested!));
                    else if (!_thinkingLevelMap.TryGetValue("off", out var off) || off is not null)
                        add("reasoning", Effort(off ?? "none"));
                    break;
                case "ant-ling":
                    if (enabled)
                    {
                        if (_thinkingLevelMap.TryGetValue(requested!, out var effort) && effort is not null)
                            add("reasoning", Effort(effort));
                    }
                    else OffEffort(); // Source falls through to its generic off branch.
                    break;
                case "together":
                    add("reasoning", JsonData.Parse("{\"enabled\":" + (enabled ? "true" : "false") + "}"));
                    RequestedEffort();
                    break;
                case "string-thinking":
                    if (enabled) add("thinking", Text(_thinkingLevelMap.GetValueOrDefault(requested!) ?? requested!));
                    else if (!_thinkingLevelMap.TryGetValue("off", out var off) || off is not null)
                        add("thinking", Text(off ?? "none"));
                    break;
                default:
                    if (enabled) RequestedEffort(); else OffEffort();
                    break;
            }
        }
        if (_thinkingTokenBudgetField is { } field && budget is { } count) add(field, Number(count, token));
    }

    private double? ResolveThinkingBudget()
    {
        if (!_projectionOptions.Reasoning || _options.ReasoningEffort is not { } level) return null;
        var ceiling = _options.MaxTokens is { } maximum && maximum != 0 ? maximum : _modelMaxTokens;
        if (ceiling is null) return null;
        var budget = _thinkingBudgets[level is "xhigh" or "max" ? "high" : level];
        var clamped = Math.Min(budget, Math.Max(0, ceiling.Value - 1024));
        return clamped > 0 ? clamped : null;
    }

    private ImmutableDictionary<string, double> ReadThinkingBudgets(JsonData? source)
    {
        var result = ImmutableDictionary.CreateBuilder<string, double>(StringComparer.Ordinal);
        result.Add("minimal", 1024); result.Add("low", 2048); result.Add("medium", 8192); result.Add("high", 16384);
        if (ReadOwnedObject(source) is { } owned)
            foreach (var property in owned.Value.EnumerateObject())
            {
                if (property.Name is not ("minimal" or "low" or "medium" or "high") ||
                    property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw Fail(CompletionsRequestFailure.InvalidConfiguration);
                result[property.Name] = number;
            }
        return result.ToImmutable();
    }

    private static void ValidateTemplate(JsonData? source)
    {
        if (source is null) return;
        foreach (var property in source.Value.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("$var", out var variable) || variable.ValueKind != JsonValueKind.String ||
                variable.GetString() is not ("thinking.enabled" or "thinking.effort" or "thinking.budget") ||
                value.TryGetProperty("omitWhenOff", out var omit) && omit.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Fail(CompletionsRequestFailure.InvalidConfiguration);
        }
    }

    private JsonData? ResolveTemplate(JsonData? source, double? budget, CancellationToken token)
    {
        if (source is null) return null;
        var result = new JsonObject(); var requested = _options.ReasoningEffort;
        foreach (var property in source.Value.EnumerateObject())
        {
            token.ThrowIfCancellationRequested(); var value = property.Value;
            // Assignment to the ordinary Source object invokes its prototype setter.
            // Every supported resolved value is primitive, so this key is never an own field.
            if (property.Name == "__proto__") continue;
            if (value.ValueKind != JsonValueKind.Object) { result[property.Name] = JsonNode.Parse(value.GetRawText()); continue; }
            if (requested is null && value.TryGetProperty("omitWhenOff", out var omit) && omit.GetBoolean()) continue;
            switch (value.GetProperty("$var").GetString())
            {
                case "thinking.enabled": result[property.Name] = requested is not null; break;
                case "thinking.budget": if (budget is { } count) result[property.Name] = count; break;
                case "thinking.effort":
                    var present = _thinkingLevelMap.TryGetValue(requested ?? "off", out var mapped);
                    var effort = present ? mapped : requested;
                    if (effort is not null) result[property.Name] = effort;
                    break;
            }
        }
        return result.Count == 0 ? null : CompletionsJson.Source(JsonData.Parse(result.ToJsonString(CompletionsJson.Output)),
            _options.MaximumPayloadBytes, _options.MaximumPayloadBytes, _options.MaximumPayloadDepth, token);
    }

    private static void FinitePayload(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var number) || !double.IsFinite(number)))
            throw Fail(CompletionsRequestFailure.InvalidRequest);
        if (value.ValueKind == JsonValueKind.Object) foreach (var item in value.EnumerateObject()) FinitePayload(item.Value, token);
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) FinitePayload(item, token);
    }
    private static JsonData Number(double value, CancellationToken token) => CompletionsJson.Source(
        JsonData.Parse(JsonSerializer.Serialize(value)), 4096, 4096, 1, token);
    private ImmutableDictionary<string, string?> ReadThinkingLevelMap(JsonData? source)
    {
        if (source is null) return ImmutableDictionary<string, string?>.Empty;
        if (source.ToString().Length > _options.MaximumPayloadBytes || Encoding.UTF8.GetByteCount(source.ToString()) > _options.MaximumPayloadBytes)
            throw Fail(CompletionsRequestFailure.ResourceLimit);
        try
        {
            var owned = CompletionsJson.Strict(source, _options.MaximumPayloadDepth, CancellationToken.None);
            if (owned.Value.ValueKind != JsonValueKind.Object) throw Fail(CompletionsRequestFailure.InvalidConfiguration);
            var result = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
            foreach (var property in owned.Value.EnumerateObject())
            {
                if (property.Name is not ("off" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max") ||
                    property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw Fail(CompletionsRequestFailure.InvalidConfiguration);
                var text = property.Value.GetString();
                if (text is not null) CompletionsJson.Unicode(text);
                result.Add(property.Name, text);
            }
            return result.ToImmutable();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw Fail(CompletionsRequestFailure.InvalidConfiguration); }
    }
    private static bool Identity(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Contains('\0');
    private static string ClampCacheKey(string text)
    {
        var end = 0; var count = 0;
        while (end < text.Length && count++ < 64) { if (char.IsHighSurrogate(text[end])) end++; end++; }
        return text[..end];
    }
    private static bool HasToolHistory(ChatRequest request) => request.Messages.Any(message => message.Role == "toolResult" ||
        message.Role == "assistant" && message.WireBody.Value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array &&
        content.EnumerateArray().Any(part => part.ValueKind == JsonValueKind.Object && part.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "toolCall"));
    private void ReadHeaders(JsonData? source, Dictionary<string, string?> headers, ref long supplied)
    {
        if (source is null) return;
        supplied += source.ToString().Length;
        if (supplied > _options.MaximumTotalHeaderCharacters) throw Fail(CompletionsRequestFailure.ResourceLimit);
        try
        {
            var owned = CompletionsJson.Strict(source, _options.MaximumPayloadDepth, CancellationToken.None);
            if (owned.Value.ValueKind != JsonValueKind.Object) throw Fail(CompletionsRequestFailure.InvalidConfiguration);
            foreach (var property in owned.Value.EnumerateObject())
            {
                HeaderName(property.Name);
                if (property.Name.Equals("host", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("content-length", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("connection", StringComparison.OrdinalIgnoreCase))
                    throw Fail(CompletionsRequestFailure.UnsupportedContent);
                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw Fail(CompletionsRequestFailure.InvalidConfiguration);
                var text = property.Value.GetString(); if (text is not null) HeaderValue(text);
                if (property.Name.Length > _options.MaximumHeaderCharacters || text?.Length > _options.MaximumHeaderCharacters) throw Fail(CompletionsRequestFailure.ResourceLimit);
                headers[property.Name] = text; if (headers.Count > _options.MaximumHeaders) throw Fail(CompletionsRequestFailure.ResourceLimit);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw Fail(CompletionsRequestFailure.InvalidConfiguration); }
    }
    private void CheckHeaders(Dictionary<string, string?> headers, bool includeAuthorization)
    {
        long total = 0;
        foreach (var header in headers)
        {
            HeaderName(header.Key); if (header.Value is null) continue; HeaderValue(header.Value); total += header.Key.Length + (long)header.Value.Length;
            if (header.Key.Length > _options.MaximumHeaderCharacters || header.Value.Length > _options.MaximumHeaderCharacters ||
                total > _options.MaximumTotalHeaderCharacters) throw Fail(CompletionsRequestFailure.ResourceLimit);
        }
        if (headers.Count > _options.MaximumHeaders || includeAuthorization && !headers.ContainsKey("authorization")) throw Fail(CompletionsRequestFailure.ResourceLimit);
    }
    private static void HeaderName(string value)
    { if (value.Length == 0 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || "!#$%&'*+-.^_`|~".Contains(character)))) throw Fail(CompletionsRequestFailure.InvalidConfiguration); }
    private static void HeaderValue(string value)
    { if (value.Any(character => character is not '\t' && (character is < ' ' or > '~'))) throw Fail(CompletionsRequestFailure.InvalidConfiguration); }
    private static CompletionsRequestException Fail(CompletionsRequestFailure failure) => CompletionsJson.Fail(failure);
}
