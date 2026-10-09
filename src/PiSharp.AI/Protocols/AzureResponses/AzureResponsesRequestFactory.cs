// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/azure-openai-responses.ts and packages/ai/src/api/azure-openai-config.ts.
using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.AI.Protocols.AzureResponses;

/// <summary>Azure-owned route/headers/options over the existing Responses transcript projection.</summary>
public sealed class AzureResponsesRequestFactory
{
    private static readonly string[] Efforts = ["minimal", "low", "medium", "high", "xhigh", "max"];
    private static readonly string[] ConfigurationKeys = ["AZURE_OPENAI_BASE_URL", "AZURE_OPENAI_RESOURCE_NAME", "AZURE_OPENAI_API_VERSION", "AZURE_OPENAI_DEPLOYMENT_NAME_MAP"];
    private readonly ModelDescriptor _model;
    private readonly AzureResponsesOptions _options;
    private readonly ResponsesTranscriptProjector _projector;
    private readonly JsonElement _metadata;
    private readonly ImmutableDictionary<string, string?> _headers;
    public Uri Endpoint { get; }
    public string DeploymentName { get; }
    internal AzureResponsesOptions Options => _options;
    internal ModelDescriptor Model => _model;

    public AzureResponsesRequestFactory(ModelDescriptor model, AzureResponsesOptions options)
    {
        if (model is null || options is null || options.ModelMetadata is null || options.Projection is null || options.Hooks is null ||
            options.ConfigurationValues is null || options.HttpOptions is null || options.StreamOptions is null || model.Api != "azure-openai-responses" ||
            string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.Provider) ||
            options.MaximumPayloadBytes <= 0 || options.MaximumJsonDepth is < 1 or > 64 || options.MaximumConfigurationCharacters <= 0 ||
            options.MaximumHeaderCharacters <= 0 || options.MaximumHeaders <= 0 || options.MaximumKeyCharacters <= 0 || options.MaximumErrorBodyBytes <= 0 ||
            options.TimeoutMilliseconds is <= 0 || options.MaxTokens is { } max && !double.IsFinite(max) ||
            options.Temperature is { } temperature && !double.IsFinite(temperature) ||
            options.ReasoningEffort is { } effort && !Efforts.Contains(effort, StringComparer.Ordinal) ||
            options.ReasoningSummary is not (null or "auto" or "detailed" or "concise") || options.StreamOptions.ServiceTier is not null)
            throw Fail(AzureResponsesFailure.Configuration);
        _model = model; _options = options; _metadata = options.ModelMetadata.Value;
        try
        {
            if (_metadata.GetProperty("id").GetString() != model.Id || _metadata.GetProperty("api").GetString() != model.Api ||
                _metadata.GetProperty("provider").GetString() != model.Provider) throw Fail(AzureResponsesFailure.Configuration);
            Bound(options.ModelMetadata.ToString());
            if (options.ConfigurationValues.Keys.Any(key => !ConfigurationKeys.Contains(key, StringComparer.Ordinal)))
                throw Fail(AzureResponsesFailure.UnsupportedOptions);
            foreach (var configured in options.ConfigurationValues.Values) if (configured is not null) Bound(configured);
            var compat = _metadata.TryGetProperty("compat", out var supplied) ? supplied : default;
            if (Flag(compat, "supportsAdditionalTools", false) || Flag(compat, "supportsToolSearch", false) || Flag(compat, "supportsOpenAIGrammarTools", false))
                throw Fail(AzureResponsesFailure.UnsupportedOptions);
            _projector = new(options.Projection with
            {
                Reasoning = _metadata.GetProperty("reasoning").GetBoolean(),
                SupportsDeveloperRole = Flag(compat, "supportsDeveloperRole", true),
                SupportsMidConversationSystemMessages = Flag(compat, "supportsMidConvoSystemMessages", false),
                ToolDeclarations = (options.Projection.ToolDeclarations ?? new()) with { SupportsStrictMode = Flag(compat, "supportsStrictMode", true) },
                // Pi 1.0.3 renamed the provider to "azure"; a model still declaring "azure-openai-responses" normalizes ids like any other provider.
                AllowedToolCallProviders = ImmutableHashSet.Create(StringComparer.Ordinal, "openai", "openai-codex", "opencode", "azure")
            });
            DeploymentName = Truthy(options.AzureDeploymentName) ?? DeploymentMap(Value("AZURE_OPENAI_DEPLOYMENT_NAME_MAP")) ?? model.Id;
            Bound(DeploymentName); ValidateUnicode(DeploymentName);
            var version = Truthy(options.AzureApiVersion) ?? Truthy(Value("AZURE_OPENAI_API_VERSION")) ?? "v1"; Bound(version); ValidateUnicode(version);
            var baseUrl = Truthy(options.AzureBaseUrl?.Trim()) ?? Truthy(Value("AZURE_OPENAI_BASE_URL")?.Trim());
            var resource = Truthy(options.AzureResourceName) ?? Truthy(Value("AZURE_OPENAI_RESOURCE_NAME"));
            if (baseUrl is null && resource is not null && (resource.Length > 63 || resource.Length == 0 ||
                !char.IsAsciiLetterOrDigit(resource[0]) || !char.IsAsciiLetterOrDigit(resource[^1]) ||
                resource.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))) throw Fail(AzureResponsesFailure.UnsupportedOptions);
            baseUrl ??= resource is null ? String(_metadata, "baseUrl") : "https://" + resource + ".openai.azure.com/openai/v1";
            if (baseUrl is null) throw Fail(AzureResponsesFailure.Configuration);
            Bound(baseUrl); var normalized = NormalizeBaseUrl(baseUrl);
            // SDK 7.19.0's deployment endpoint set excludes /responses. Deployment remains in the body.
            Endpoint = new(normalized.TrimEnd('/') + "/responses?api-version=" + Uri.EscapeDataString(version));
            var headers = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.OrdinalIgnoreCase);
            headers["user-agent"] = "PiSharp";
            AddHeaders(_metadata.TryGetProperty("headers", out var modelHeaders) ? modelHeaders : default, headers);
            AddHeaders(options.Headers?.Value ?? default, headers);
            _headers = headers.ToImmutable();
            _ = ProjectPayload(new(model, [])); // Validate named option/map/sampling syntax before request effects.
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { throw Fail(AzureResponsesFailure.Configuration); }
    }

    public JsonData ProjectPayload(ChatRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (request is null || request.Model != _model) throw Fail(AzureResponsesFailure.Request);
        var input = _projector.ProjectInput(request, token); var tools = _projector.ProjectTools(request, token);
        var root = new JsonObject { ["model"] = DeploymentName, ["input"] = JsonNode.Parse(input.ToString()), ["stream"] = true };
        if ((_options.SessionId ?? request.SessionId) is { } session) { Bound(session); ValidateUnicode(session); root["prompt_cache_key"] = string.Concat(session.EnumerateRunes().Take(64).Select(rune => rune.ToString())); }
        root["store"] = false;
        if (_options.MaxTokens is { } cap && cap != 0) root["max_output_tokens"] = Math.Max(cap, 16);
        if (_options.Temperature is { } temperature) root["temperature"] = temperature;
        if (tools.Value.GetArrayLength() > 0) root["tools"] = JsonNode.Parse(tools.ToString());
        if (_options.ToolChoice is { } choice) root["tool_choice"] = JsonNode.Parse(choice.ToString());
        if (_metadata.GetProperty("reasoning").GetBoolean())
        {
            var map = _metadata.TryGetProperty("thinkingLevelMap", out var mapped) ? mapped : default;
            if (map.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object)) throw Fail(AzureResponsesFailure.Configuration);
            string Map(string level, string fallback) => map.ValueKind == JsonValueKind.Object && map.TryGetProperty(level, out var field) && field.ValueKind != JsonValueKind.Null ? field.GetString()! : fallback;
            if (_options.ReasoningEffort is not null || _options.ReasoningSummary is not null)
            {
                var effort = _options.ReasoningEffort is { } level ? Map(level, level) : "medium";
                root["reasoning"] = new JsonObject { ["effort"] = effort, ["summary"] = _options.ReasoningSummary ?? "auto" };
                root["include"] = new JsonArray("reasoning.encrypted_content");
            }
            else if (!(map.ValueKind == JsonValueKind.Object && map.TryGetProperty("off", out var off) && off.ValueKind == JsonValueKind.Null))
                root["reasoning"] = new JsonObject { ["effort"] = Map("off", "none") };
        }
        // Last so model and request sampling parameters override named fields: model defaults, then the effective
        // thinking level's overrides (a summary without an effort requests "medium"), then request keys.
        MergeSampling(root, _metadata.TryGetProperty("samplingParams", out var modelSampling) ? modelSampling : default);
        if (_metadata.TryGetProperty("samplingParamsByThinkingLevel", out var byLevel))
        {
            if (!ThinkingLevelSampling.TrySelect(byLevel, _metadata.GetProperty("reasoning").GetBoolean(),
                _metadata.TryGetProperty("thinkingLevelMap", out var levelMap) ? levelMap : default,
                _options.ReasoningEffort ?? (_options.ReasoningSummary is not null ? "medium" : "off"), out var levelSampling))
                throw Fail(AzureResponsesFailure.Configuration);
            MergeSampling(root, levelSampling);
        }
        MergeSampling(root, _options.SamplingParams?.Value ?? default);
        return AdmitPayload(JsonData.Parse(root.ToJsonString()), token);
    }

    public async ValueTask<HttpRequestMessage> CreateAsync(ChatRequest request, string explicitApiKey, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); AdmitKey(explicitApiKey);
        var payload = ProjectPayload(request, token);
        if (_options.Hooks.OnPayload is { } hook)
        { var replacement = await hook(payload, _model, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); if (replacement is not null) payload = AdmitPayload(replacement, token); }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["api-key"] = explicitApiKey, ["accept"] = "application/json" };
        foreach (var field in _headers) if (field.Value is null) headers.Remove(field.Key); else headers[field.Key] = field.Value;
        if (!headers.TryGetValue("api-key", out var admitted) || string.IsNullOrEmpty(admitted)) throw Fail(AzureResponsesFailure.UnsupportedOptions);
        AdmitKey(admitted); headers["content-type"] = "application/json";
        var bytes = Encoding.UTF8.GetBytes(payload.ToString()); HttpRequestMessage? result = null;
        try
        {
            token.ThrowIfCancellationRequested(); result = new(HttpMethod.Post, Endpoint) { Content = new ByteArrayContent(bytes) };
            result.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            foreach (var header in headers) if (header.Key != "content-type" && !result.Headers.TryAddWithoutValidation(header.Key, header.Value)) throw Fail(AzureResponsesFailure.UnsupportedOptions);
            token.ThrowIfCancellationRequested(); return result;
        }
        catch { result?.Dispose(); throw; }
    }
    private JsonData AdmitPayload(JsonData payload, CancellationToken token)
    {
        if (payload.Value.ValueKind != JsonValueKind.Object) throw Fail(AzureResponsesFailure.UnsupportedOptions);
        try
        {
            var projected = EcmaScriptJsonProjection.Project(payload, new(MaximumInputCharacters: _options.MaximumPayloadBytes,
                MaximumInputBytes: _options.MaximumPayloadBytes, MaximumOutputCharacters: _options.MaximumPayloadBytes, MaximumOutputBytes: _options.MaximumPayloadBytes,
                MaximumDepth: _options.MaximumJsonDepth, MaximumStringCharacters: _options.MaximumPayloadBytes), token);
            return JsonData.Parse(projected);
        }
        catch (EcmaScriptJsonProjectionException error)
        { throw Fail(error.Failure == EcmaScriptJsonProjectionFailure.ResourceLimit ? AzureResponsesFailure.ResourceLimit : AzureResponsesFailure.Request); }
    }
    private void MergeSampling(JsonObject body, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.Object) throw Fail(AzureResponsesFailure.UnsupportedOptions);
        foreach (var property in value.EnumerateObject()) body[property.Name] = JsonNode.Parse(property.Value.GetRawText());
    }
    private void AddHeaders(JsonElement value, ImmutableDictionary<string, string?>.Builder headers)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.Object) throw Fail(AzureResponsesFailure.Configuration);
        foreach (var field in value.EnumerateObject())
        {
            if (field.Name.Length == 0 || field.Name.Length > _options.MaximumHeaderCharacters ||
                field.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c))) ||
                field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw Fail(AzureResponsesFailure.Configuration);
            if (field.Name.Equals("authorization", StringComparison.OrdinalIgnoreCase) || field.Name.Equals("host", StringComparison.OrdinalIgnoreCase) ||
                field.Name.Equals("content-length", StringComparison.OrdinalIgnoreCase) || field.Name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase) ||
                field.Name.Equals("connection", StringComparison.OrdinalIgnoreCase)) throw Fail(AzureResponsesFailure.UnsupportedOptions);
            var text = field.Value.GetString();
            if (text is not null && (text.Length > _options.MaximumHeaderCharacters || text.Any(c => c is < ' ' or > '~'))) throw Fail(AzureResponsesFailure.Configuration);
            headers[field.Name.ToLowerInvariant()] = text;
            if (headers.Count > _options.MaximumHeaders) throw Fail(AzureResponsesFailure.ResourceLimit);
        }
    }
    private static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw Fail(AzureResponsesFailure.Configuration);
        var azure = uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".ai.azure.com", StringComparison.OrdinalIgnoreCase);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (azure && path is "" or "/openai" or "/openai/v1/responses") return new UriBuilder(uri) { Path = "/openai/v1", Query = "" }.Uri.AbsoluteUri.TrimEnd('/');
        if (uri.Query.Length != 0) throw Fail(AzureResponsesFailure.UnsupportedOptions);
        return uri.AbsoluteUri.TrimEnd('/');
    }
    private string? DeploymentMap(string? map)
    {
        string? deployment = null;
        foreach (var entry in (map ?? "").Split(','))
        { var parts = entry.Trim().Split('='); if (parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0 && parts[0].Trim() == _model.Id) deployment = Truthy(parts[1].Trim()); }
        return deployment;
    }
    private void AdmitKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Any(c => c is < '!' or > '~')) throw Fail(AzureResponsesFailure.Credential);
        if (key.Length > _options.MaximumKeyCharacters) throw Fail(AzureResponsesFailure.ResourceLimit);
    }
    private void Bound(string text) { if (text.Length > _options.MaximumConfigurationCharacters) throw Fail(AzureResponsesFailure.ResourceLimit); }
    private static void ValidateUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsHighSurrogate(value[index]))
            { if (index + 1 >= value.Length || !char.IsLowSurrogate(value[++index])) throw Fail(AzureResponsesFailure.Configuration); }
            else if (char.IsLowSurrogate(value[index])) throw Fail(AzureResponsesFailure.Configuration);
    }
    private string? Value(string key) => _options.ConfigurationValues.TryGetValue(key, out var value) ? value : null;
    private static string? String(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind != JsonValueKind.Null ? field.GetString() : null;
    private static bool Flag(JsonElement value, string key, bool fallback) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var flag) ? flag.GetBoolean() : fallback;
    private static string? Truthy(string? value) => string.IsNullOrEmpty(value) ? null : value;
    private static AzureResponsesException Fail(AzureResponsesFailure failure) => new(failure);
}
