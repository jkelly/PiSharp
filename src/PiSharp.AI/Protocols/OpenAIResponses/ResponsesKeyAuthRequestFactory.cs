using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAIResponses;

public sealed record ResponsesKeyAuthRequestOptions(
    bool SupportsMaxOutputTokens = false, int? MaxOutputTokens = null,
    JsonData? PayloadOverrides = null,
    int MaximumOutputTokens = 1_000_000, int MaximumKeyCharacters = 4096,
    int MaximumEndpointCharacters = 4096, int MaximumModelCharacters = 1024,
    int MaximumPayloadBytes = 1_048_576, int MaximumPayloadDepth = 32,
    string? SessionId = null, double? Temperature = null,
    int MaximumSessionIdCharacters = 4096, double MaximumTemperatureMagnitude = 2,
    string? ReasoningEffort = null, string? ReasoningSummary = null, JsonData? ThinkingLevelMap = null,
    JsonData? ToolChoice = null, string? ServiceTier = null);

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
    internal sealed record ServiceTierBinding(string? RequestedTier);
    internal static readonly HttpRequestOptionsKey<ServiceTierBinding> RequestedServiceTierKey = new("PiSharp.Responses.RequestedServiceTier");
    internal string? RequestedServiceTier => _options.ServiceTier;
    private const string Prefix = "{\"model\":";
    private const string InputField = ",\"input\":";

    public ResponsesKeyAuthRequestFactory(Uri endpoint, ModelDescriptor expectedModel,
        ResponsesTranscriptProjectionOptions projectionOptions, ResponsesKeyAuthRequestOptions? options = null)
    {
        _options = options ?? new();
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
        if (!ResponsesServiceTier.Supported(_options.ServiceTier) || _options.PayloadOverrides is not null ||
            (_options.MaxOutputTokens is not null && !_options.SupportsMaxOutputTokens))
            throw Failure(ResponsesKeyAuthRequestFailure.UnsupportedOptions);
        if (_options.Temperature is { } temperature && !double.IsFinite(temperature))
            throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration);
        if ((_options.MaxOutputTokens is { } requested && requested != 0 &&
                Math.Max(requested, 16) > _options.MaximumOutputTokens) ||
            (_options.Temperature is { } boundedTemperature && Math.Abs(boundedTemperature) > _options.MaximumTemperatureMagnitude))
            throw Failure(ResponsesKeyAuthRequestFailure.ResourceLimit);
        _toolChoiceSuffix = ToolChoiceField();
        _reasoningSuffix = ReasoningFields(projectionOptions.Reasoning, expectedModel.Provider);
        var session = ClampSessionId(_options.SessionId);
        try { _projector = new(projectionOptions); }
        catch (ArgumentException) { throw Failure(ResponsesKeyAuthRequestFailure.InvalidConfiguration); }
        _endpoint = endpoint;
        _model = expectedModel;
        _modelJson = JsonSerializer.Serialize(expectedModel.Id);
        _suffix = ",\"stream\":true" +
            (session is not null ? ",\"prompt_cache_key\":" + JsonSerializer.Serialize(session) : "") + ",\"store\":false" +
            (_options.MaxOutputTokens is { } count && count != 0
                ? ",\"max_output_tokens\":" + Math.Max(count, 16).ToString(CultureInfo.InvariantCulture) : "") +
            (_options.Temperature is { } value ? ",\"temperature\":" + (value == 0 ? "0" : JsonSerializer.Serialize(value)) : "") +
            (_options.ServiceTier is { } tier ? ",\"service_tier\":" + JsonSerializer.Serialize(tier) : "");
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
        HttpRequestMessage? result = null;
        HttpContent? content = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            result = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
            content = null; // Request now owns content.
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
