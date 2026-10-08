// Pi d86654abb8862e201933517d6f1fce9f88dd117f (MIT): azure-openai-responses.ts:streamSimple,
// simple-options.ts:buildBaseOptions/clampMaxTokensToContext and models.ts:clampThinkingLevel.
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AzureResponses;

/// <summary>Pure Simple resolution followed by the existing actual Azure HTTP/SSE owner.
/// Borrows its HttpClient; the existing direct transport owns each invocation's physical resources.</summary>
public sealed class AzureResponsesSimpleTransport : IChatTransport
{
    private static readonly ImmutableArray<string> Levels = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];
    private readonly HttpClient client;
    private readonly ModelDescriptor model;
    private readonly AzureResponsesSimpleOptions options;
    private readonly JsonElement levelMap;
    private readonly bool reasoning;
    private readonly double contextWindow, modelMaxTokens;

    public AzureResponsesSimpleTransport(HttpClient client, ModelDescriptor model, AzureResponsesSimpleOptions options)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(options);
        if (options.DirectOptions is null || options.DirectOptions.ModelMetadata is null ||
            options.MaximumContextMessages is < 1 or > 65_536 || options.MaximumContextCharacters is < 1 or > PiRequestBudget.MaximumBound ||
            options.Reasoning is { } requested && !Levels.Contains(requested, StringComparer.Ordinal))
            throw new AzureResponsesException(AzureResponsesFailure.Configuration);
        // Existing Azure constructor remains the authority for exact identity/config/route admission.
        _ = new AzureResponsesRequestFactory(model, options.DirectOptions);
        this.client = client; this.model = model; this.options = options;
        var metadata = options.DirectOptions.ModelMetadata.Value;
        try
        {
            contextWindow = Number(metadata, "contextWindow"); modelMaxTokens = Number(metadata, "maxTokens");
            reasoning = metadata.GetProperty("reasoning").GetBoolean();
            if (metadata.TryGetProperty("thinkingLevelMap", out var map) && map.ValueKind != JsonValueKind.Null)
            {
                if (map.ValueKind != JsonValueKind.Object) throw new AzureResponsesException(AzureResponsesFailure.Configuration);
                foreach (var property in map.EnumerateObject())
                    if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        throw new AzureResponsesException(AzureResponsesFailure.Configuration);
                levelMap = map;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new AzureResponsesException(AzureResponsesFailure.Configuration); }
    }

    public ImmutableArray<string> GetSupportedThinkingLevels() => !reasoning ? ["off"] : Levels.Where(level =>
    {
        var exists = levelMap.ValueKind == JsonValueKind.Object && levelMap.TryGetProperty(level, out _);
        return (!exists || levelMap.GetProperty(level).ValueKind != JsonValueKind.Null) &&
            (level is not ("xhigh" or "max") || exists);
    }).ToImmutableArray();

    public string ClampThinkingLevel(string level)
    {
        var index = Levels.IndexOf(level);
        if (index < 0) throw new AzureResponsesException(AzureResponsesFailure.UnsupportedOptions);
        var available = GetSupportedThinkingLevels();
        if (available.Contains(level, StringComparer.Ordinal)) return level;
        for (var i = index; i < Levels.Length; i++) if (available.Contains(Levels[i], StringComparer.Ordinal)) return Levels[i];
        for (var i = index - 1; i >= 0; i--) if (available.Contains(Levels[i], StringComparer.Ordinal)) return Levels[i];
        return available.IsEmpty ? "off" : available[0];
    }

    public AzureResponsesSimpleResolution Resolve(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        if (request.Model != model || request.Messages.IsDefault) throw new AzureResponsesException(AzureResponsesFailure.Request);
        // This existing estimator implements the provider-independent pinned estimate.ts algorithm.
        // The real Azure metadata is retained unchanged; no Google identity, transport or key is invented.
        var estimateOptions = new GoogleSimpleOptions(new GoogleGenerativeAIOptions(options.DirectOptions.ModelMetadata)
        { MaximumPayloadBytes = checked(options.MaximumContextCharacters * 4) })
        { MaximumContextMessages = options.MaximumContextMessages, MaximumContextCharacters = options.MaximumContextCharacters };
        GoogleContextUsageEstimate estimate;
        try { estimate = GoogleSimpleContextEstimator.Estimate(request.Messages, estimateOptions); }
        catch (GoogleGenerativeAIException error)
        { throw new AzureResponsesException(error.Failure == GoogleFailure.ResourceLimit ? AzureResponsesFailure.ResourceLimit : AzureResponsesFailure.UnsupportedOptions); }
        var maximum = options.DirectOptions.MaxTokens ?? modelMaxTokens;
        var capped = contextWindow <= 0 ? Math.Max(1, maximum) : Math.Min(maximum, Math.Max(1, contextWindow - estimate.Tokens - 4096));
        if (!double.IsFinite(capped)) throw new AzureResponsesException(AzureResponsesFailure.UnsupportedOptions);
        var clamped = options.Reasoning is { } selected ? ClampThinkingLevel(selected) : null;
        cancellationToken.ThrowIfCancellationRequested();
        return new(new(estimate.Tokens, estimate.UsageTokens, estimate.TrailingTokens, estimate.LastUsageIndex),
            capped, GetSupportedThinkingLevels(), clamped, clamped is null or "off" ? null : clamped);
    }

    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        // Original Azure Simple checks an explicit key before base resolution or payload effects.
        cancellationToken.ThrowIfCancellationRequested();
        var key = options.ApiKey;
        if (string.IsNullOrEmpty(key) || key.Any(character => character is < '!' or > '~'))
            throw new AzureResponsesException(AzureResponsesFailure.Credential);
        if (key.Length > options.DirectOptions.MaximumKeyCharacters) throw new AzureResponsesException(AzureResponsesFailure.ResourceLimit);
        var resolved = Resolve(request, cancellationToken);
        var direct = options.DirectOptions with
        { MaxTokens = resolved.MaxTokens, ReasoningEffort = resolved.ReasoningEffort, ReasoningSummary = null };
        return new AzureResponsesTransport(client, new AzureResponsesRequestFactory(model, direct), key)
            .StreamAsync(request, cancellationToken);
    }

    private static double Number(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Number ||
            !field.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new AzureResponsesException(AzureResponsesFailure.Configuration);
        return number;
    }
}
