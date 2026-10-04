// Pi d86654abb8862e201933517d6f1fce9f88dd117f (MIT): google-generative-ai.ts:streamSimple/getGoogleBudget,
// google-shared.ts:resolveGoogleThinkingLevel/usesGoogleThinkingLevel, models.ts and simple-options.ts.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.GoogleGenerativeAI;

/// <summary>Google Simple resolution and binding to the actual borrowed-client HTTP/SSE transport.</summary>
public sealed class GoogleSimpleRequestFactory : IChatTransport
{
    private static readonly ImmutableArray<string> Levels = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];
    private readonly HttpClient _client;
    private readonly ModelDescriptor _model;
    private readonly GoogleSimpleOptions _options;
    private readonly JsonElement _metadata, _levelMap;
    private readonly bool _reasoning;
    private readonly double _contextWindow, _modelMaxTokens;

    public GoogleSimpleRequestFactory(HttpClient client, ModelDescriptor model, GoogleSimpleOptions options)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(options);
        if (options.DirectOptions is null) throw GoogleData.Fail(GoogleFailure.Configuration);
        options.DirectOptions.Validate();
        if (options.MaximumContextMessages is < 1 or > 65_536 || options.MaximumContextCharacters is < 1 or > 8_388_608)
            throw GoogleData.Fail(GoogleFailure.Configuration);
        if (options.Reasoning is { } requested && !Levels.Contains(requested, StringComparer.Ordinal))
            throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        _client = client; _model = model; _options = options; _metadata = options.DirectOptions.ModelMetadata.Value;
        if (_metadata.ValueKind != JsonValueKind.Object || model.Api != "google-generative-ai" ||
            GoogleData.String(_metadata, "api") != model.Api || GoogleData.String(_metadata, "provider") != model.Provider ||
            GoogleData.String(_metadata, "id") != model.Id) throw GoogleData.Fail(GoogleFailure.Configuration);
        _contextWindow = GoogleSimpleContextEstimator.Number(_metadata, "contextWindow");
        _modelMaxTokens = GoogleSimpleContextEstimator.Number(_metadata, "maxTokens");
        if (_metadata.TryGetProperty("reasoning", out var reasoning))
        {
            if (reasoning.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw GoogleData.Fail(GoogleFailure.Configuration);
            _reasoning = reasoning.GetBoolean();
        }
        if (_metadata.TryGetProperty("thinkingLevelMap", out var map) && map.ValueKind != JsonValueKind.Null)
        {
            if (map.ValueKind != JsonValueKind.Object) throw GoogleData.Fail(GoogleFailure.Configuration);
            foreach (var field in map.EnumerateObject())
                if (!Levels.Contains(field.Name, StringComparer.Ordinal) || field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
            _levelMap = map;
        }
        if (options.ThinkingBudgets is { } budgets)
        {
            var value = GoogleData.Admit(budgets, options.DirectOptions).Value;
            if (value.ValueKind != JsonValueKind.Object) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
            foreach (var field in value.EnumerateObject())
                if (field.Name is not ("minimal" or "low" or "medium" or "high") || field.Value.ValueKind != JsonValueKind.Number ||
                    !field.Value.TryGetDouble(out var number) || !double.IsFinite(number)) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        }
    }
    public ImmutableArray<string> GetSupportedThinkingLevels()
    {
        if (!_reasoning) return ["off"];
        return Levels.Where(level =>
        {
            var exists = _levelMap.ValueKind == JsonValueKind.Object && _levelMap.TryGetProperty(level, out _);
            return (!exists || _levelMap.GetProperty(level).ValueKind != JsonValueKind.Null) &&
                (level is not ("xhigh" or "max") || exists);
        }).ToImmutableArray();
    }
    public string ClampThinkingLevel(string level)
    {
        var index = Levels.IndexOf(level);
        if (index < 0) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        var available = GetSupportedThinkingLevels();
        if (available.Contains(level, StringComparer.Ordinal)) return level;
        for (var i = index; i < Levels.Length; i++) if (available.Contains(Levels[i], StringComparer.Ordinal)) return Levels[i];
        for (var i = index - 1; i >= 0; i--) if (available.Contains(Levels[i], StringComparer.Ordinal)) return Levels[i];
        return available.Length == 0 ? "off" : available[0];
    }
    public GoogleSimpleResolution Resolve(ChatRequest request)
    {
        if (request is null || request.Model != _model || request.Messages.IsDefault) throw GoogleData.Fail(GoogleFailure.Configuration);
        var estimate = GoogleSimpleContextEstimator.Estimate(request.Messages, _options);
        var maximum = _options.DirectOptions.MaxTokens ?? _modelMaxTokens;
        maximum = _contextWindow <= 0 ? Math.Max(1, maximum) : Math.Min(maximum, Math.Max(1, _contextWindow - estimate.Tokens - 4096));
        var clamped = _options.Reasoning is { } level ? ClampThinkingLevel(level) : null;
        string? resolved = null; var thinking = new GoogleThinkingOptions(false);
        if (clamped is not (null or "off"))
        {
            var mapped = _levelMap.ValueKind == JsonValueKind.Object && _levelMap.TryGetProperty(clamped, out var value) &&
                value.ValueKind == JsonValueKind.String ? value.GetString()!.ToLowerInvariant() : clamped;
            if (mapped is not ("minimal" or "low" or "medium" or "high")) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
            resolved = mapped;
            thinking = UsesThinkingLevel(_model.Id) ? new(true, Level: mapped.ToUpperInvariant()) : new(true, BudgetTokens: Budget(mapped));
        }
        return new(estimate, maximum, GetSupportedThinkingLevels(), clamped, resolved, thinking);
    }
    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        // Source Simple admits the explicit key before base resolution or payload effects.
        if (string.IsNullOrEmpty(_options.DirectOptions.ApiKey)) throw GoogleData.Fail(GoogleFailure.MissingKey);
        var resolution = Resolve(request);
        var direct = _options.DirectOptions with { MaxTokens = resolution.MaxTokens, Thinking = resolution.Thinking };
        return new GoogleGenerativeAIHttpTransport(_client, _model, direct).StreamAsync(request, cancellationToken);
    }
    private double Budget(string level)
    {
        if (_options.ThinkingBudgets is { } budgets && budgets.Value.TryGetProperty(level, out var custom)) return custom.GetDouble();
        if (_model.Id.Contains("2.5-pro", StringComparison.Ordinal)) return level switch { "minimal" => 128, "low" => 2048, "medium" => 8192, _ => 32768 };
        if (_model.Id.Contains("2.5-flash-lite", StringComparison.Ordinal)) return level switch { "minimal" => 512, "low" => 2048, "medium" => 8192, _ => 24576 };
        if (_model.Id.Contains("2.5-flash", StringComparison.Ordinal)) return level switch { "minimal" => 128, "low" => 2048, "medium" => 8192, _ => 24576 };
        return -1;
    }
    private static bool UsesThinkingLevel(string id)
    {
        var normalized = id.ToLowerInvariant();
        return Regex.IsMatch(normalized, @"gemini-3(?:\.[0-9]+)?-(?:pro|flash)|gemma-?4") ||
            normalized is "gemini-flash-latest" or "gemini-flash-lite-latest";
    }
}
