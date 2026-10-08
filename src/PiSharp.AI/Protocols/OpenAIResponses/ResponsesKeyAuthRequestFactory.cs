using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Authentication;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAIResponses;

public sealed record ResponsesKeyAuthRequestOptions(
    bool SupportsMaxOutputTokens = true, int? MaxOutputTokens = null,
    JsonData? PayloadOverrides = null,
    int MaximumOutputTokens = 1_000_000, int MaximumKeyCharacters = 4096,
    int MaximumEndpointCharacters = 4096, int MaximumModelCharacters = 1024,
    int MaximumPayloadBytes = 1_048_576, int MaximumPayloadDepth = 32,
    string? SessionId = null, double? Temperature = null,
    int MaximumSessionIdCharacters = 4096, double MaximumTemperatureMagnitude = 2,
    string? ReasoningEffort = null, string? ReasoningSummary = null, JsonData? ThinkingLevelMap = null,
    JsonData? ToolChoice = null, string? ServiceTier = null,
    string? CacheRetention = null, bool SupportsLongCacheRetention = true,
    bool SupportsExplicitPromptCacheMode = false, ProviderEnvironmentSnapshot? Environment = null)
{
    public JsonData? ModelHeaders { get; init; }
    public JsonData? Headers { get; init; }
    public JsonData? ModelSamplingParams { get; init; }
    public JsonData? SamplingParams { get; init; }
    public string SessionAffinityFormat { get; init; } = "openai";
    public int MaximumHeaders { get; init; } = 128;
    public int MaximumHeaderCharacters { get; init; } = 4096;
    public int MaximumTotalHeaderCharacters { get; init; } = 65_536;
    [System.Text.Json.Serialization.JsonIgnore] public Func<JsonData, ModelDescriptor, CancellationToken, ValueTask<JsonData?>>? OnPayload { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public Func<JsonData, ModelDescriptor, CancellationToken, ValueTask>? OnResponse { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public Func<JsonData, ModelDescriptor, CancellationToken, ValueTask>? OnProviderStreamEvent { get; init; }
}

public enum ResponsesKeyAuthRequestFailure
{
    InvalidConfiguration, UnsupportedOptions, InvalidRequest, InvalidKey, ResourceLimit
}

public sealed class ResponsesKeyAuthRequestException : Exception
{
    public ResponsesKeyAuthRequestFailure Failure { get; }
    internal ResponsesKeyAuthRequestException(ResponsesKeyAuthRequestFailure failure) : base(failure switch
    {
        ResponsesKeyAuthRequestFailure.UnsupportedOptions => "Unsupported Responses key request options.",
        ResponsesKeyAuthRequestFailure.InvalidRequest => "Responses request model does not match the configured profile.",
        ResponsesKeyAuthRequestFailure.InvalidKey => "Invalid explicit Responses API key.",
        ResponsesKeyAuthRequestFailure.ResourceLimit => "Responses key request exceeds configured limits.",
        _ => "Invalid Responses key request configuration."
    }) => Failure = failure;
}

/// <summary>
/// Pure explicit-key request construction for bounded Responses text/tool/reasoning profiles.
/// No client, environment, credential store, request hook, retry or network operation is acquired.
/// The caller owns each returned request and its content.
/// </summary>
public sealed class ResponsesKeyAuthRequestFactory
{
    private readonly Uri _endpoint;
    private readonly ModelDescriptor _model;
    private readonly ResponsesKeyAuthRequestOptions _options;
    private readonly ResponsesTranscriptProjector _projector;
    private readonly string _modelJson;
    private readonly string _suffix;
    private readonly string _reasoningSuffix;
    private readonly string _toolChoiceSuffix;
    private readonly string _cacheRetention;
    internal sealed record ServiceTierBinding(string? RequestedTier);
    internal static readonly HttpRequestOptionsKey<ServiceTierBinding> RequestedServiceTierKey = new("PiSharp.Responses.RequestedServiceTier");
    internal string? RequestedServiceTier => _options.ServiceTier;
    internal ResponsesKeyAuthRequestOptions Policy => _options;
    private const string Prefix = "{\"model\":";
    private const string InputField = ",\"input\":";

    public ResponsesKeyAuthRequestFactory(Uri endpoint, ModelDescriptor expectedModel,
        ResponsesTranscriptProjectionOptions projectionOptions, ResponsesKeyAuthRequestOptions? options = null)
    {
        _options = options ?? new();
        if (_options.OnPayload?.GetInvocationList().Length > 1 || _options.OnResponse?.GetInvocationList().Length > 1 ||
            _options.OnProviderStreamEvent?.GetInvocationList().Length > 1)
            throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
        if (endpoint is null || expectedModel is null || projectionOptions is null ||
            _options.MaximumOutputTokens < 16 || _options.MaximumKeyCharacters <= 0 ||
            _options.MaximumEndpointCharacters <= 0 || _options.MaximumModelCharacters <= 0 ||
            _options.MaximumPayloadBytes <= 0 || _options.MaximumPayloadDepth is < 1 or > 64 ||
            _options.MaximumSessionIdCharacters <= 0 || !double.IsFinite(_options.MaximumTemperatureMagnitude) ||
            _options.MaximumTemperatureMagnitude <= 0)
            throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
            endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0 ||
            expectedModel.Api != "openai-responses" || !ValidIdentity(expectedModel.Id) ||
            !ValidIdentity(expectedModel.Provider))
            throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
        if (endpoint.AbsoluteUri.Length > _options.MaximumEndpointCharacters ||
            expectedModel.Id.Length > _options.MaximumModelCharacters ||
            expectedModel.Provider.Length > _options.MaximumModelCharacters)
            throw Failure(ResponsesKeyAuthRequestFailure.ResourceLimit);
        if (_options.CacheRetention is not (null or "none" or "short" or "long") ||
            !ResponsesServiceTier.Supported(_options.ServiceTier) || _options.PayloadOverrides is not null)
            throw Failure(ResponsesKeyAuthRequestFailure.UnsupportedOptions);
        if (_options.Temperature is { } temperature && !double.IsFinite(temperature))
            throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
        if ((_options.MaxOutputTokens is { } requested && requested != 0 &&
                Math.Max(requested, 16) > _options.MaximumOutputTokens) ||
            (_options.Temperature is { } boundedTemperature && Math.Abs(boundedTemperature) > _options.MaximumTemperatureMagnitude))
            throw Failure(ResponsesKeyAuthRequestFailure.ResourceLimit);
        if (_options.SessionAffinityFormat is not ("openai" or "openrouter" or "none") ||
            _options.MaximumHeaders <= 0 || _options.MaximumHeaderCharacters <= 0 || _options.MaximumTotalHeaderCharacters <= 0)
            throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
        _toolChoiceSuffix = ToolChoiceField();
        _reasoningSuffix = ReasoningFields(projectionOptions.Reasoning, expectedModel.Provider);
        _cacheRetention = _options.CacheRetention ?? (_options.Environment?.GetValue("PI_CACHE_RETENTION") == "long" ? "long" : "short");
        var session = _cacheRetention == "none" ? null : ClampSessionId(_options.SessionId);
        try { _projector = new(projectionOptions); }
        catch (ArgumentException) { throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration); }
        _endpoint = endpoint;
        _model = expectedModel;
        _modelJson = JsonSerializer.Serialize(expectedModel.Id);
        _suffix = ",\"stream\":true" +
            (session is not null ? ",\"prompt_cache_key\":" + JsonSerializer.Serialize(session) : "") + CacheFields() + ",\"store\":false" +
            (_options.SupportsMaxOutputTokens && _options.MaxOutputTokens is { } count && count != 0
                ? ",\"max_output_tokens\":" + Math.Max(count, 16).ToString(CultureInfo.InvariantCulture) : "") +
            (_options.Temperature is { } value ? ",\"temperature\":" + (value == 0 ? "0" : JsonSerializer.Serialize(value)) : "") +
            (_options.ServiceTier is { } tier ? ",\"service_tier\":" + JsonSerializer.Serialize(tier) : "");
    }

    // Pi d86654abb8862e201933517d6f1fce9f88dd117f, openai-responses.ts:
    // getPromptCacheRetention/getPromptCacheOptions. These explicit profile controls
    // require no ambient environment lookup or endpoint/provider-name inference.
    private string CacheFields()
    {
        if (_options.SupportsExplicitPromptCacheMode)
        {
            if (_cacheRetention == "none") return ",\"prompt_cache_options\":{\"mode\":\"explicit\"}";
            if (_cacheRetention == "long" && _options.SupportsLongCacheRetention)
                return ",\"prompt_cache_options\":{\"ttl\":\"30m\"}";
            return "";
        }
        return _cacheRetention == "long" && _options.SupportsLongCacheRetention
            ? ",\"prompt_cache_retention\":\"24h\"" : "";
    }

    public HttpRequestMessage Create(ChatRequest request, string explicitApiKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || request.Model != _model) throw Failure(ResponsesKeyAuthRequestFailure.InvalidRequest);
        if (explicitApiKey is null || explicitApiKey.Length == 0)
            throw Failure(ResponsesKeyAuthRequestFailure.InvalidKey);
        if (explicitApiKey.Length > _options.MaximumKeyCharacters)
            throw Failure(ResponsesKeyAuthRequestFailure.ResourceLimit);
        // Printable, non-whitespace ASCII only. No provider-specific prefix is inferred.
        foreach (var character in explicitApiKey)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (character is < '!' or > '~') throw Failure(ResponsesKeyAuthRequestFailure.InvalidKey);
        }
        var projected = _projector.ProjectInput(request, cancellationToken);
        var tools = _projector.ProjectTools(request, cancellationToken);
        CheckDepth(projected.Value, 1, cancellationToken); // Include the new payload object.
        CheckDepth(tools.Value, 1, cancellationToken);
        var input = projected.ToString();
        var toolField = tools.Value.GetArrayLength() == 0 ? "" : ",\"tools\":" + tools.ToString();
        var bytes = (long)Encoding.UTF8.GetByteCount(Prefix) + Encoding.UTF8.GetByteCount(_modelJson) +
            Encoding.UTF8.GetByteCount(InputField) + Encoding.UTF8.GetByteCount(input) + Encoding.UTF8.GetByteCount(_suffix) +
            Encoding.UTF8.GetByteCount(toolField) + Encoding.UTF8.GetByteCount(_toolChoiceSuffix) + Encoding.UTF8.GetByteCount(_reasoningSuffix) + 1;
        if (bytes > _options.MaximumPayloadBytes) throw Failure(ResponsesKeyAuthRequestFailure.ResourceLimit);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = Encoding.UTF8.GetBytes(Prefix + _modelJson + InputField + input + _suffix + toolField + _toolChoiceSuffix + _reasoningSuffix + "}");
        if (_options.ModelSamplingParams is not null || _options.SamplingParams is not null)
        {
            var root = JsonNode.Parse(payload)!.AsObject();
            foreach (var sampling in new[] { _options.ModelSamplingParams, _options.SamplingParams })
            {
                if (sampling is null || sampling.Value.ValueKind == JsonValueKind.Null) continue;
                if (sampling.Value.ValueKind != JsonValueKind.Object) throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
                AdmitHookPayload(sampling, cancellationToken);
                foreach (var property in sampling.Value.EnumerateObject()) root[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
            var merged = JsonData.Parse(root.ToJsonString());
            AdmitHookPayload(merged, cancellationToken);
            payload = Encoding.UTF8.GetBytes(merged.ToString());
        }
        var headers = RequestHeaders(cancellationToken);
        HttpRequestMessage? result = null;
        HttpContent? content = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            result = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
            content = null; // Request now owns content.
            foreach (var header in headers)
                if (header.Value is not null && !header.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase) &&
                    !result.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    throw Failure(ResponsesKeyAuthRequestFailure.UnsupportedOptions);
            // Preserve this explicit-key profile's existing authorization authority.
            result.Headers.Authorization = new AuthenticationHeaderValue("Bearer", explicitApiKey);
            result.Options.Set(RequestedServiceTierKey, new ServiceTierBinding(_options.ServiceTier));
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            result?.Dispose();
            content?.Dispose();
            throw;
        }
    }

    // Pi createClient: model headers, session affinity, then request headers.
    private Dictionary<string, string?> RequestHeaders(CancellationToken token)
    {
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        long supplied = 0;
        void Merge(JsonData? source)
        {
            if (source is null || source.Value.ValueKind == JsonValueKind.Null) return;
            supplied += source.ToString().Length;
            if (supplied > _options.MaximumTotalHeaderCharacters) throw Failure(ResponsesKeyAuthRequestFailure.ResourceLimit);
            if (source.Value.ValueKind != JsonValueKind.Object) throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
            foreach (var property in source.Value.EnumerateObject())
            {
                token.ThrowIfCancellationRequested();
                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
                headers[property.Name] = property.Value.GetString();
            }
        }
        Merge(_options.ModelHeaders);
        if (_options.SessionId is { Length: > 0 } session)
        {
            _ = ClampSessionId(session); // Headers use original session; body cache key is clamped separately.
            if (_options.SessionAffinityFormat == "openrouter") headers["x-session-id"] = session;
            else
            {
                if (_options.SessionAffinityFormat == "openai") headers["session_id"] = session;
                headers["x-client-request-id"] = session;
            }
        }
        Merge(_options.Headers);
        long total = 0;
        if (headers.Count > _options.MaximumHeaders) throw Failure(ResponsesKeyAuthRequestFailure.ResourceLimit);
        foreach (var header in headers)
        {
            token.ThrowIfCancellationRequested();
            if (header.Key.Length == 0 || header.Key.Any(character => !(char.IsAsciiLetterOrDigit(character) || "!#$%&'*+-.^_`|~".Contains(character))) ||
                header.Value?.Any(character => character != '\t' && (character < ' ' || character > '~')) == true)
                throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
            if (header.Key.Equals("host", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("connection", StringComparison.OrdinalIgnoreCase))
                throw Failure(ResponsesKeyAuthRequestFailure.UnsupportedOptions);
            total += header.Key.Length + (long)(header.Value?.Length ?? 0);
            if (header.Key.Length > _options.MaximumHeaderCharacters || header.Value?.Length > _options.MaximumHeaderCharacters ||
                total > _options.MaximumTotalHeaderCharacters) throw Failure(ResponsesKeyAuthRequestFailure.ResourceLimit);
        }
        return headers;
    }

    private string ToolChoiceField()
    {
        try { return ToolChoiceFieldCore(); }
        catch (Exception error) when (error is InvalidOperationException or JsonException or DecoderFallbackException)
        { throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration); }
    }
    private string ToolChoiceFieldCore()
    {
        if (_options.ToolChoice is not { } choice) return "";
        var json = choice.ToString();
        if (Encoding.UTF8.GetByteCount(json) > _options.MaximumPayloadBytes)
            throw Failure(ResponsesKeyAuthRequestFailure.ResourceLimit);
        var value = choice.Value;
        if (value.ValueKind == JsonValueKind.String)
        {
            if (value.GetString() is not ("auto" or "none" or "required"))
                throw Failure(ResponsesKeyAuthRequestFailure.UnsupportedOptions);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.EnumerateObject().Count() != 2 ||
                !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "function" ||
                !value.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
                throw Failure(ResponsesKeyAuthRequestFailure.UnsupportedOptions);
            if (!ValidText(name.GetString()!)) throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
        }
        else throw Failure(ResponsesKeyAuthRequestFailure.UnsupportedOptions);
        CheckDepth(value, 1, CancellationToken.None);
        // Pinned buildParams forwards an explicit choice even with no active declarations.
        // This is a request hint, not validation or authority to execute a tool.
        return ",\"tool_choice\":" + json;
    }
    // Pi d86654abb8862e201933517d6f1fce9f88dd117f, openai-responses.ts:buildParams.
    // Model capability comes from the same explicit profile as transcript projection.
    private string ReasoningFields(bool reasoning, string provider)
    {
        if (_options.ReasoningEffort is not (null or "minimal" or "low" or "medium" or "high" or "xhigh" or "max") ||
            _options.ReasoningSummary is not (null or "auto" or "detailed" or "concise"))
            throw Failure(ResponsesKeyAuthRequestFailure.UnsupportedOptions);
        var map = _options.ThinkingLevelMap?.Value ?? default;
        if (_options.ThinkingLevelMap is { } raw)
        {
            if (Encoding.UTF8.GetByteCount(raw.ToString()) > _options.MaximumPayloadBytes)
                throw Failure(ResponsesKeyAuthRequestFailure.ResourceLimit);
            if (map.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                throw Failure(ResponsesKeyAuthRequestFailure.UnsupportedOptions);
            if (map.ValueKind == JsonValueKind.Object)
                foreach (var field in map.EnumerateObject())
                {
                    if (field.Name is not ("off" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max") ||
                        field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        throw Failure(ResponsesKeyAuthRequestFailure.UnsupportedOptions);
                    if (field.Value.ValueKind == JsonValueKind.String && !ValidText(field.Value.GetString()!))
                        throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
                }
        }
        if (!reasoning) return "";
        bool Mapped(string level, out JsonElement value)
        {
            value = default;
            return map.ValueKind == JsonValueKind.Object && map.TryGetProperty(level, out value);
        }
        var fields = "";
        var include = provider == "xai";
        if (_options.ReasoningEffort is not null || _options.ReasoningSummary is not null)
        {
            var effort = _options.ReasoningEffort is { } requested ?
                Mapped(requested, out var mapped) && mapped.ValueKind == JsonValueKind.String ? mapped.GetString()! : requested : "medium";
            fields = ",\"reasoning\":{\"effort\":" + JsonSerializer.Serialize(effort) +
                ",\"summary\":" + JsonSerializer.Serialize(_options.ReasoningSummary ?? "auto") + "}";
            include = true;
        }
        else if (provider != "github-copilot" && !(Mapped("off", out var disabled) && disabled.ValueKind == JsonValueKind.Null))
        {
            var effort = Mapped("off", out var mapped) ? mapped.GetString()! : "none";
            fields = ",\"reasoning\":{\"effort\":" + JsonSerializer.Serialize(effort) + "}";
        }
        if (include) fields += ",\"include\":[\"reasoning.encrypted_content\"]";
        return fields;
    }

    internal void AdmitHookPayload(JsonData payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.Value.ValueKind != JsonValueKind.Object) throw Failure(ResponsesKeyAuthRequestFailure.InvalidRequest);
        if (Encoding.UTF8.GetByteCount(payload.ToString()) > _options.MaximumPayloadBytes) throw Failure(ResponsesKeyAuthRequestFailure.ResourceLimit);
        CheckDepth(payload.Value, 0, cancellationToken);
        if (payload.Value.TryGetProperty("max_output_tokens", out var maximum) && maximum.ValueKind == JsonValueKind.Number &&
            (!maximum.TryGetDouble(out var tokens) || !double.IsFinite(tokens) || tokens > _options.MaximumOutputTokens))
            throw Failure(ResponsesKeyAuthRequestFailure.ResourceLimit);
    }
    private static bool ValidText(string value)
    {
        for (var i = 0; i < value.Length; i++)
            if (char.IsSurrogate(value[i]) &&
                (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i]))) return false;
        return true;
    }
    private void CheckDepth(JsonElement value, int parentDepth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return;
        var depth = parentDepth + 1;
        if (depth > _options.MaximumPayloadDepth) throw Failure(ResponsesKeyAuthRequestFailure.ResourceLimit);
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) CheckDepth(property.Value, depth, cancellationToken);
        else
            foreach (var child in value.EnumerateArray()) CheckDepth(child, depth, cancellationToken);
    }

    private static bool ValidIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[++index])) return false;
            }
            else if (char.IsLowSurrogate(value[index]) || char.IsControl(value[index])) return false;
        }
        return true;
    }
    private string? ClampSessionId(string? value)
    {
        if (value is null) return null;
        if (value.Length > _options.MaximumSessionIdCharacters)
            throw Failure(ResponsesKeyAuthRequestFailure.ResourceLimit);
        var codePoints = 0;
        var end = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[++index]))
                    throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
            }
            else if (char.IsLowSurrogate(value[index]))
                throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
            // Match Array.from(...).slice(0, 64), preserving complete surrogate pairs.
            if (++codePoints <= 64) end = index + 1;
        }
        return value[..end];
    }
    private static ResponsesKeyAuthRequestException Failure(ResponsesKeyAuthRequestFailure failure) => new(failure);
}
