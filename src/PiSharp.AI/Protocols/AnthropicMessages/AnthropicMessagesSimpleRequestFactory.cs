// Pi v0.99.1 d86654abb8862e201933517d6f1fce9f88dd117f (MIT):
// api/anthropic-messages.ts:streamSimple/mapThinkingLevelToEffort and api/simple-options.ts.
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

/// <summary>Simple resolution composed with the existing explicit-key request factory and HTTP/SSE owners.</summary>
public sealed class AnthropicMessagesSimpleRequestFactory : IChatTransport
{
    private static readonly string[] Levels = ["minimal", "low", "medium", "high", "xhigh", "max"];
    private static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
    private readonly HttpClient _client;
    private readonly Uri _baseUri;
    private readonly ModelDescriptor _model;
    private readonly AnthropicMessagesSimpleOptions _options;
    private readonly int _contextWindow, _modelMaxTokens;
    private readonly bool _reasoning, _adaptive, _supportsOff;
    private readonly JsonElement _levelMap;

    public AnthropicMessagesSimpleRequestFactory(HttpClient client, Uri baseUri, ModelDescriptor model,
        AnthropicMessagesSimpleOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (baseUri is null || model is null || options is null || options.ModelMetadata is null || options.ProjectionOptions is null ||
            options.KeyAuthOptions is null || options.HttpOptions is null || model.Api != "anthropic-messages" ||
            options.MaximumContextMessages is < 1 or > 65_536 || options.MaximumContextCharacters is < 2 or > 8_388_608 ||
            options.ModelMetadata.ToString().Length > options.MaximumContextCharacters ||
            options.MaxTokens is <= 0 || options.Reasoning is { } level && !Levels.Contains(level, StringComparer.Ordinal))
            throw Fail(AnthropicMessagesSimpleFailure.InvalidConfiguration);
        _client = client; _baseUri = baseUri; _model = model; _options = options;
        try
        {
            var metadata = options.ModelMetadata.Value;
            if (metadata.GetProperty("id").GetString() != model.Id || metadata.GetProperty("api").GetString() != model.Api ||
                metadata.GetProperty("provider").GetString() != model.Provider) throw Fail(AnthropicMessagesSimpleFailure.InvalidConfiguration);
            _contextWindow = metadata.GetProperty("contextWindow").GetInt32();
            _modelMaxTokens = metadata.GetProperty("maxTokens").GetInt32();
            _reasoning = metadata.GetProperty("reasoning").GetBoolean();
            if (_modelMaxTokens <= 0) throw Fail(AnthropicMessagesSimpleFailure.InvalidConfiguration);
            _adaptive = metadata.TryGetProperty("compat", out var compat) && compat.ValueKind != JsonValueKind.Null &&
                compat.TryGetProperty("forceAdaptiveThinking", out var adaptive) && adaptive.GetBoolean();
            if (compat.ValueKind == JsonValueKind.Object && compat.TryGetProperty("supportsMidConvoEffort", out var midEffort) && midEffort.GetBoolean())
                throw Fail(AnthropicMessagesSimpleFailure.InvalidConfiguration);
            if (metadata.TryGetProperty("thinkingLevelMap", out var map) && map.ValueKind != JsonValueKind.Null)
            {
                if (map.ValueKind != JsonValueKind.Object) throw Fail(AnthropicMessagesSimpleFailure.InvalidConfiguration);
                foreach (var entry in map.EnumerateObject())
                    if (entry.Value.ValueKind != JsonValueKind.Null && (entry.Value.ValueKind != JsonValueKind.String ||
                        !Efforts.Contains(entry.Value.GetString()!, StringComparer.Ordinal))) throw Fail(AnthropicMessagesSimpleFailure.InvalidConfiguration);
                _levelMap = map;
            }
            _supportsOff = !(_levelMap.ValueKind == JsonValueKind.Object && _levelMap.TryGetProperty("off", out var off) && off.ValueKind == JsonValueKind.Null);
            if (options.ThinkingBudgets is { } budgets)
            {
                if (budgets.Value.ValueKind != JsonValueKind.Object || budgets.ToString().Length > 4096)
                    throw Fail(AnthropicMessagesSimpleFailure.InvalidConfiguration);
                foreach (var budget in budgets.Value.EnumerateObject())
                    if (budget.Name is not ("minimal" or "low" or "medium" or "high") || !budget.Value.TryGetInt32(out var count) || count < 0)
                        throw Fail(AnthropicMessagesSimpleFailure.InvalidConfiguration);
            }
            // Reuse the direct factory's endpoint/header/limit admission without acquiring a request or sending.
            _ = Bind(new(new(0, 0, 0, null), _modelMaxTokens, false, 0, null));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw Fail(AnthropicMessagesSimpleFailure.InvalidConfiguration); }
    }

    public AnthropicMessagesSimpleResolution Resolve(ChatRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || request.Model != _model || request.Messages.IsDefault) throw Fail(AnthropicMessagesSimpleFailure.InvalidTranscript);
        var estimate = AnthropicMessagesSimpleContextEstimator.Estimate(request.Messages, _options, cancellationToken);
        int Clamp(int maximum) => _contextWindow <= 0 ? Math.Max(1, maximum) :
            (int)Math.Min(maximum, Math.Max(1L, (long)_contextWindow - estimate.Tokens - 4096));
        var maximum = Clamp(_options.MaxTokens ?? _modelMaxTokens);
        if (_options.Reasoning is not { } level) return new(estimate, maximum, false, 0, null);
        if (_adaptive)
        {
            var effort = _levelMap.ValueKind == JsonValueKind.Object && _levelMap.TryGetProperty(level, out var mapped) && mapped.ValueKind == JsonValueKind.String
                ? mapped.GetString()! : level switch { "minimal" or "low" => "low", "medium" => "medium", _ => "high" };
            return new(estimate, maximum, true, 0, effort);
        }
        var budgetLevel = level is "xhigh" or "max" ? "high" : level;
        var budget = _options.ThinkingBudgets is { } custom && custom.Value.TryGetProperty(budgetLevel, out var value) ? value.GetInt32() :
            budgetLevel switch { "minimal" => 1024, "low" => 2048, "medium" => 8192, _ => 16384 };
        // buildBaseOptions always supplies a context-clamped cap, even for omitted maxTokens.
        maximum = (int)Math.Min((long)maximum + budget, _modelMaxTokens);
        if (maximum <= budget) budget = Math.Min(budget, Math.Max(0, maximum - 1024));
        maximum = Clamp(maximum);
        budget = Math.Min(budget, Math.Max(0, maximum - 1024));
        return new(estimate, maximum, true, budget, null);
    }

    public HttpRequestMessage Create(ChatRequest request, string explicitApiKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); AdmitKey(explicitApiKey);
        return Bind(Resolve(request, cancellationToken)).Create(request, explicitApiKey, cancellationToken);
    }

    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); AdmitKey(_options.ApiKey);
        var factory = Bind(Resolve(request, cancellationToken));
        return new AnthropicMessagesHttpSseTransport(_client, (current, token) => factory.Create(current, _options.ApiKey!, token),
            _options.HttpOptions, _options.MessagesOptions).StreamAsync(request, cancellationToken);
    }
    private AnthropicMessagesKeyAuthRequestFactory Bind(AnthropicMessagesSimpleResolution resolved) => new(_baseUri, _model,
        _options.ProjectionOptions with
        {
            MaximumTokens = resolved.MaxTokens, ModelReasoning = _reasoning, ThinkingEnabled = resolved.ThinkingEnabled,
            ForceAdaptiveThinking = _adaptive, SupportsThinkingOff = _supportsOff, ThinkingBudgetTokens = resolved.ThinkingBudgetTokens,
            Effort = resolved.Effort, ToolChoice = _options.ToolChoice ?? _options.ProjectionOptions.ToolChoice
        }, _options.KeyAuthOptions with { MaxTokens = resolved.MaxTokens });
    private void AdmitKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Any(character => character is < '!' or > '~'))
            throw new AnthropicMessagesKeyAuthRequestException(AnthropicMessagesKeyAuthRequestFailure.InvalidKey);
        if (key.Length > _options.KeyAuthOptions.MaximumKeyCharacters)
            throw new AnthropicMessagesKeyAuthRequestException(AnthropicMessagesKeyAuthRequestFailure.ResourceLimit);
        if (key.Contains("sk-ant-oat", StringComparison.Ordinal))
            throw new AnthropicMessagesKeyAuthRequestException(AnthropicMessagesKeyAuthRequestFailure.UnsupportedOptions);
    }
    private static AnthropicMessagesSimpleException Fail(AnthropicMessagesSimpleFailure failure) => new(failure);
}
