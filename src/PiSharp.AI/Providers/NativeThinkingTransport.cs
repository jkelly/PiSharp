// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/models.ts (getSupportedThinkingLevels) and
// packages/ai/src/api/anthropic-messages.ts (mapThinkingLevelToEffort, compat.supportsMidConvoEffort).
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI.Providers;

/// <summary>Explicit metadata admission and per-request selection; the bound client remains the sole HTTP owner.</summary>
internal sealed class NativeThinkingTransport : IChatTransport, IThinkingLevelTransport
{
    private readonly ModelDescriptor _model;
    private readonly ImmutableArray<string> _levels;
    private readonly IChatTransport _fallback;
    private readonly ImmutableDictionary<string, IChatTransport> _transports;
    internal NativeThinkingTransport(ModelDescriptor model, NativeThinkingProfile profile, Func<string?, IChatTransport> resolve)
        : this(model, profile.Levels, resolve) { }
    internal NativeThinkingTransport(ModelDescriptor model, ImmutableArray<string> levels, Func<string?, IChatTransport> resolve)
    {
        _model = model; _levels = levels; _fallback = resolve(null);
        // Validate every advertised wire profile before registration, without acquiring any HTTP request.
        _transports = levels.ToImmutableDictionary(level => level, level => resolve(level), StringComparer.Ordinal);
    }
    public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor selected) => selected == _model
        ? _levels : throw new ArgumentException("Unknown native thinking model.");

    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (request.Model != _model) throw new ArgumentException("Unknown native thinking model.");
        if (request.ThinkingLevel is { } level) ThinkingLevels.Validate(this, _model, level);
        return (request.ThinkingLevel is null ? _fallback : _transports[request.ThinkingLevel]).StreamAsync(request, cancellationToken);
    }
}

internal sealed class NativeThinkingProfile
{
    public ImmutableArray<string> Levels { get; private set; }
    public JsonData? Map { get; }
    public bool Adaptive { get; }
    public bool SupportsOff { get; }
    /// <summary>compat.supportsMidConvoEffort: per-turn effort markers with always-adaptive thinking (Pi has no cap restriction).</summary>
    public bool MidConversationEffort { get; }
    public int ModelMaxTokens { get; }

    public NativeThinkingProfile(ModelDescriptor model, JsonData metadata, bool reasoning, int? anthropicCap = null)
    {
        try
        {
            if (metadata.ToString().Length > 65_536) throw new ArgumentException();
            var raw = metadata.Value;
            if (raw.GetProperty("id").GetString() != model.Id || raw.GetProperty("api").GetString() != model.Api ||
                raw.GetProperty("provider").GetString() != model.Provider || raw.GetProperty("reasoning").GetBoolean() != reasoning)
                throw new ArgumentException();
            ModelMaxTokens = raw.GetProperty("maxTokens").GetInt32();
            if (ModelMaxTokens <= 0 || raw.GetProperty("contextWindow").GetInt32() <= 0) throw new ArgumentException();
            var map = default(JsonElement);
            if (raw.TryGetProperty("thinkingLevelMap", out map) && map.ValueKind != JsonValueKind.Null)
            {
                if (map.ValueKind != JsonValueKind.Object) throw new ArgumentException();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in map.EnumerateObject())
                    if (!ThinkingLevels.Ordered.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name) ||
                        property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new ArgumentException();
                Map = JsonData.Parse(map.GetRawText());
            }
            SupportsOff = !(map.ValueKind == JsonValueKind.Object && map.TryGetProperty("off", out var off) && off.ValueKind == JsonValueKind.Null);
            if (raw.TryGetProperty("compat", out var compat) && compat.ValueKind != JsonValueKind.Null)
            {
                if (compat.ValueKind != JsonValueKind.Object) throw new ArgumentException();
                Adaptive = compat.TryGetProperty("forceAdaptiveThinking", out var adaptive) && adaptive.GetBoolean();
                MidConversationEffort = compat.TryGetProperty("supportsMidConvoEffort", out var mid) && mid.GetBoolean();
            }
            Levels = !reasoning ? ["off"] : ThinkingLevels.Ordered.Where(level =>
                !(map.ValueKind == JsonValueKind.Object && map.TryGetProperty(level, out var entry) && entry.ValueKind == JsonValueKind.Null) &&
                (level is not ("xhigh" or "max") || map.ValueKind == JsonValueKind.Object && map.TryGetProperty(level, out _)))
                .ToImmutableArray();
            // The explicit native cap is never enlarged. Nonadaptive enabled thinking needs 1024 thinking + 1024 answer tokens;
            // adaptive (including per-turn effort) levels carry no budget, so upstream's level list stands.
            if (anthropicCap is { } cap)
            {
                if (cap <= 0 || cap > ModelMaxTokens) throw new ArgumentException();
                if (!Adaptive && cap < 2048) Levels = Levels.Where(level => level == "off").ToImmutableArray();
                if (map.ValueKind == JsonValueKind.Object)
                    foreach (var entry in map.EnumerateObject())
                        if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is not ("low" or "medium" or "high" or "xhigh" or "max"))
                            throw new ArgumentException();
            }
            if (Levels.IsDefaultOrEmpty) throw new ArgumentException();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or JsonException)
        { throw new ArgumentException("Unsupported native thinking metadata."); }
    }

    internal void ConstrainCompletions(PiSharp.AI.Protocols.OpenAICompletions.CompletionsKeyAuthRequestFactory factory)
    {
        Levels = Levels.Where(level => level == "off" || factory.SupportsNativeThinkingControl(level)).ToImmutableArray();
        if (Levels.IsDefaultOrEmpty) throw new ArgumentException("Unsupported native thinking metadata.");
    }

    public string AnthropicEffort(string level) => Map is { } map && map.Value.TryGetProperty(level, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : level switch { "minimal" or "low" => "low", "medium" => "medium", _ => "high" };
    public static int AnthropicBudget(string level, int cap) => Math.Min(cap - 1024,
        level switch { "minimal" => 1024, "low" => 2048, "medium" => 8192, _ => 16384 });
}
