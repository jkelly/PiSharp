// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/core/model-resolver.ts,
// packages/coding-agent/src/cli/args.ts (isValidThinkingLevel, --models splitting) and packages/coding-agent/src/main.ts
// (buildSessionOptions model and scoped-model selection).
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Models;

/// <summary>Source ScopedModel: a model and the thinking level a pattern named explicitly.</summary>
internal sealed record ScopedModel(RegistryModel Model, string? ThinkingLevel);

/// <summary>Source ModelReferenceRequest thinkingSuffix: a trailing <c>:suffix</c> is only considered when the full text matches no
/// model, since ids such as OpenRouter's <c>qwen/qwen3-coder:exacto</c> contain colons.</summary>
internal enum ThinkingSuffixMode
{
    /// <summary>The suffix stays part of the id.</summary>
    None,
    /// <summary>A valid thinking level is parsed; any other suffix stays part of the id.</summary>
    Strict,
    /// <summary>Like <see cref="Strict"/>, but an invalid suffix is dropped with a warning.</summary>
    Lenient,
}

/// <summary>Source ModelReferenceRequest: a bare model id or <c>provider/id</c>, optionally followed by <c>:&lt;thinking&gt;</c>;
/// <paramref name="Provider"/> only considers that provider's models; <paramref name="Fuzzy"/> matches id and name substrings when no
/// exact match exists.</summary>
internal sealed record ModelReferenceRequest(string Text, string? Provider, ThinkingSuffixMode ThinkingSuffix, bool Fuzzy);

internal enum ModelReferenceStatus { Resolved, Ambiguous, NotFound }

/// <summary>Source ModelReferenceTarget: set on a not-found result when a provider is explicit or named by a <c>provider/</c> prefix, for
/// building a custom model id. <paramref name="ModelText"/> is the text after the provider prefix as typed (with any <c>:suffix</c>);
/// <paramref name="ModelId"/> drops a valid thinking suffix.</summary>
internal sealed record ModelReferenceTarget(string Provider, ImmutableArray<RegistryModel> ProviderModels, string ModelText, string ModelId, string? ThinkingLevel);

/// <summary>Source ModelReferenceResult: resolved (<paramref name="Model"/>), ambiguous (<paramref name="Candidates"/>, of which
/// <paramref name="UsableCount"/> are usable) or not found (with an optional <paramref name="Target"/>).</summary>
internal sealed record ModelReferenceResult(ModelReferenceStatus Status, RegistryModel? Model = null, ImmutableArray<RegistryModel> Candidates = default,
    int UsableCount = 0, string? ThinkingLevel = null, string? Warning = null, ModelReferenceTarget? Target = null)
{
    public ImmutableArray<RegistryModel> Candidates { get; init; } = Candidates.IsDefault ? [] : Candidates;
}

/// <summary>Source ModelScopeDiagnostic.</summary>
internal sealed record ModelScopeDiagnostic(string Code, string Message, string Pattern)
{
    internal const string Type = "warning";
}

/// <summary>Source ResolveCliModelResult.</summary>
internal sealed record ResolveCliModelResult(RegistryModel? Model, string? ThinkingLevel, string? Warning, string? Error);

/// <summary>Source InitialModelResult.</summary>
internal sealed record InitialModelResult(RegistryModel? Model, string ThinkingLevel, string? FallbackMessage);

internal static partial class ModelResolver
{
    /// <summary>Source DEFAULT_THINKING_LEVEL (core/defaults.ts).</summary>
    internal const string DefaultThinkingLevel = "medium";
    internal static readonly ImmutableArray<string> ThinkingLevels = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>Source defaultModelPerProvider, in declaration order (the fallback search order).</summary>
    internal static readonly ImmutableArray<(string Provider, string Model)> DefaultModelPerProvider =
    [
        ("amazon-bedrock", "us.anthropic.claude-opus-4-6-v1"), ("ant-ling", "Ring-2.6-1T"), ("anthropic", "claude-opus-4-8"),
        ("openai", "gpt-6.1-sol"), ("azure", "gpt-5.4"), ("openai-codex", "gpt-6.1-sol"), ("radius", "balanced"),
        ("nvidia", "nvidia/nemotron-3.5-super-vl-120b-a12b"), ("deepseek", "deepseek-v4-pro"), ("google", "gemini-3.1-pro-preview"),
        ("google-vertex", "gemini-3.1-pro-preview"), ("github-copilot", "gpt-6.1-sol"), ("openrouter", "moonshotai/kimi-k2.6"),
        ("vercel-ai-gateway", "zai/glm-5.1"), ("xai", "grok-4.7"), ("groq", "openai/gpt-oss-120b"), ("cerebras", "gpt-oss-120b"),
        ("zai", "glm-5.3"), ("zai-coding-cn", "glm-5.3"), ("mistral", "devstral-medium-latest"), ("minimax", "MiniMax-M2.7"),
        ("minimax-cn", "MiniMax-M2.7"), ("moonshotai", "kimi-k2.6"), ("moonshotai-cn", "kimi-k2.6"), ("huggingface", "moonshotai/Kimi-K2.6"),
        ("fireworks", "accounts/fireworks/models/kimi-k3"), ("together", "moonshotai/Kimi-K3"), ("baseten", "zai-org/GLM-5.2"),
        ("opencode", "kimi-k2.6"), ("opencode-go", "kimi-k3"), ("kimi-coding", "kimi-for-coding"), ("meta", "muse-spark-1.3"),
        ("cloudflare-workers-ai", "@cf/moonshotai/kimi-k2.6"), ("cloudflare-ai-gateway", "workers-ai/@cf/moonshotai/kimi-k2.6"),
        ("qwen-token-plan", "qwen3.7-max"), ("qwen-token-plan-cn", "qwen3.7-max"), ("qwen-token-plan-individual", "qwen3.8-max"),
        ("xiaomi", "mimo-v2.6-pro"), ("xiaomi-token-plan-cn", "mimo-v2.6-pro"), ("xiaomi-token-plan-ams", "mimo-v2.6-pro"),
        ("xiaomi-token-plan-sgp", "mimo-v2.6-pro"),
    ];

    internal static string? DefaultModelFor(string provider) =>
        DefaultModelPerProvider.FirstOrDefault(entry => entry.Provider == provider).Model;

    /// <summary>Source isValidThinkingLevel.</summary>
    internal static bool IsValidThinkingLevel(string level) => ThinkingLevels.Contains(level, StringComparer.Ordinal);

    /// <summary>Source --models: comma-separated, trimmed, empty entries dropped.</summary>
    internal static ImmutableArray<string> SplitPatterns(string value) =>
        [.. value.Split(',').Select(pattern => pattern.Trim()).Where(pattern => pattern.Length > 0)];

    private static readonly CompareInfo Collation = CultureInfo.GetCultureInfo("en-US").CompareInfo;
    /// <summary>JavaScript <c>String.prototype.localeCompare</c> under Node's default ICU collation.</summary>
    internal static int LocaleCompare(string left, string right) => Collation.Compare(left, right, CompareOptions.None);

    private static string Lower(string value) => value.ToLowerInvariant();

    /// <summary>Source isAlias: ends with -latest, or has no -YYYYMMDD suffix.</summary>
    private static bool IsAlias(string id) => id.EndsWith("-latest", StringComparison.Ordinal) || !DateSuffix().IsMatch(id);

    // Model reference resolution. Candidates are collected before a provider is chosen, so catalog order never decides the provider
    // and an ambiguous match is reported instead of falling through to a weaker one.

    private static readonly ModelReferenceResult NotFound = new(ModelReferenceStatus.NotFound);

    private static ModelReferenceResult Resolved(RegistryModel model) => new(ModelReferenceStatus.Resolved, model);

    /// <summary>Source splitThinkingSuffix: the text before the last <c>:</c> and the valid thinking level after it.</summary>
    private static (string Base, string ThinkingLevel)? SplitThinkingSuffix(string text)
    {
        var colon = text.LastIndexOf(':');
        if (colon < 0) return null;
        var suffix = text[(colon + 1)..];
        return IsValidThinkingLevel(suffix) ? (text[..colon], suffix) : null;
    }

    /// <summary>Source splitProviderPrefix: provider ids may contain "/" themselves (e.g. URL-derived ids), so the known ids are matched
    /// as prefixes, case-insensitively, followed by "/" and a non-empty model id; the longest match wins.</summary>
    private static (string Provider, string ModelText)? SplitProviderPrefix(string text, IReadOnlyList<RegistryModel> models)
    {
        var lowerText = Lower(text);
        (string Provider, string ModelText)? best = null;
        foreach (var model in models)
        {
            var provider = model.Provider;
            var prefix = Lower(provider.Trim());
            if (prefix.Length == 0 || (best is { } current && current.Provider.Length >= provider.Length)) continue;
            var slash = lowerText.IndexOf('/', Math.Min(prefix.Length, lowerText.Length));
            if (slash < 0 || slash >= text.Length || lowerText[..slash].Trim() != prefix) continue;
            var modelText = text[(slash + 1)..].Trim();
            if (modelText.Length > 0) best = (provider, modelText);
        }
        return best;
    }

    /// <summary>Source resolveModelReference. <paramref name="usable"/> only breaks ties between providers, so a unique match resolves
    /// even without credentials (<c>--api-key</c> first-time setup).</summary>
    internal static ModelReferenceResult ResolveModelReference(ModelReferenceRequest request, IReadOnlyList<RegistryModel> models, Func<string, bool> usable)
    {
        var text = request.Text.Trim();
        if (text.Length == 0) return NotFound;
        var constraint = string.IsNullOrEmpty(request.Provider) ? null : Lower(request.Provider);
        IReadOnlyList<RegistryModel> pool = constraint is null ? models : models.Where(model => Lower(model.Provider) == constraint).ToList();
        var result = ResolveText(text, request, pool, usable);
        if (result.Status != ModelReferenceStatus.NotFound) return result;

        var split = SplitProviderPrefix(text, pool);
        var anchor = constraint is not null ? pool.FirstOrDefault() : split is { } named ? pool.FirstOrDefault(model => model.Provider == named.Provider) : null;
        if (anchor is null) return result;
        var modelText = split?.ModelText ?? text;
        var suffix = request.ThinkingSuffix == ThinkingSuffixMode.None ? null : SplitThinkingSuffix(modelText);
        return new(ModelReferenceStatus.NotFound, Target: new(anchor.Provider,
            [anchor, .. pool.Where(model => model.Provider == anchor.Provider && !ReferenceEquals(model, anchor))],
            modelText, suffix?.Base ?? modelText, suffix?.ThinkingLevel));
    }

    /// <summary>Source findExactModelReferenceMatch: an exact <c>provider/id</c> or bare id lookup without thinking suffixes or fuzzy
    /// matching.</summary>
    internal static RegistryModel? FindExactModelReferenceMatch(string reference, IReadOnlyList<RegistryModel> models) =>
        ResolveModelReference(new(reference, null, ThinkingSuffixMode.None, false), models, static _ => true) is { Status: ModelReferenceStatus.Resolved } result
            ? result.Model : null;

    private static ModelReferenceResult ResolveText(string text, ModelReferenceRequest request, IReadOnlyList<RegistryModel> pool, Func<string, bool> usable)
    {
        if (SelectReference(text, pool, request.Fuzzy, usable) is { } reference) return reference;
        if (request.Fuzzy && SelectFuzzy(FuzzyMatches(text, pool), usable) is { } fuzzy) return fuzzy;

        if (request.ThinkingSuffix == ThinkingSuffixMode.None) return NotFound;
        if (SplitThinkingSuffix(text) is { } thinking)
        {
            var inner = ResolveText(thinking.Base, request, pool, usable);
            if (inner.Status == ModelReferenceStatus.NotFound) return inner;
            // Only use this thinking level if no warning from inner recursion
            return inner with { ThinkingLevel = inner.Warning is not null ? null : thinking.ThinkingLevel };
        }
        // Keep an invalid suffix as part of the id and fail, rather than resolve a different model
        if (request.ThinkingSuffix == ThinkingSuffixMode.Strict) return NotFound;
        var colon = text.LastIndexOf(':');
        if (colon < 0) return NotFound;

        var suffix = text[(colon + 1)..];
        var lenient = ResolveText(text[..colon], request, pool, usable);
        if (lenient.Status == ModelReferenceStatus.NotFound) return lenient;
        return lenient with { ThinkingLevel = null, Warning = $"Invalid thinking level \"{suffix}\" in pattern \"{text}\". Using default instead." };
    }

    /// <summary>Source selectReference: <c>provider/rest</c> is only a hint, since the whole text may also be a model id on another provider
    /// (gateway id <c>zai/glm-5</c>). The named provider wins when usable; otherwise a sole usable raw match wins; an unusable named match
    /// is still preferred over unusable raw matches, so <c>--api-key</c> can set up the named provider.</summary>
    private static ModelReferenceResult? SelectReference(string text, IReadOnlyList<RegistryModel> pool, bool fuzzy, Func<string, bool> usable)
    {
        var lowerText = Lower(text);
        var raw = pool.Where(model => Lower(model.Id) == lowerText).ToList();
        RegistryModel? named = null;
        if (SplitProviderPrefix(text, pool) is { } split)
        {
            var providerModels = pool.Where(model => model.Provider == split.Provider).ToList();
            var lowerModelText = Lower(split.ModelText);
            // The provider's own literal id (OpenRouter's `openrouter/free`) beats its fuzzy matches
            named = providerModels.FirstOrDefault(model => Lower(model.Id) == lowerModelText) ??
                raw.FirstOrDefault(model => model.Provider == split.Provider) ??
                (fuzzy ? BestFuzzyMatch(FuzzyMatches(split.ModelText, providerModels)) : null);
        }

        var others = raw.Where(model => !ReferenceEquals(model, named)).ToList();
        List<RegistryModel> candidates = named is not null ? [named, .. others] : others;
        if (candidates.Count == 0) return null;
        if (candidates.Select(model => model.Provider).Distinct(StringComparer.Ordinal).Count() == 1) return Resolved(candidates[0]);
        if (named is not null && usable(named.Provider)) return Resolved(named);
        var usableOthers = others.Where(model => usable(model.Provider)).ToList();
        if (usableOthers.Count == 1) return Resolved(usableOthers[0]);
        if (usableOthers.Count == 0 && named is not null) return Resolved(named);
        return new(ModelReferenceStatus.Ambiguous, Candidates: [.. candidates], UsableCount: usableOthers.Count);
    }

    private static List<RegistryModel> FuzzyMatches(string text, IReadOnlyList<RegistryModel> models)
    {
        var lowerText = Lower(text);
        return models.Where(model => Lower(model.Id).Contains(lowerText, StringComparison.Ordinal) ||
            (model.Name is not null && Lower(model.Name).Contains(lowerText, StringComparison.Ordinal))).ToList();
    }

    private static ModelReferenceResult? SelectFuzzy(List<RegistryModel> matches, Func<string, bool> usable)
    {
        if (matches.Count == 0) return null;
        var multipleProviders = matches.Select(model => model.Provider).Distinct(StringComparer.Ordinal).Count() > 1;
        var usableMatches = multipleProviders ? matches.Where(model => usable(model.Provider)).ToList() : [];
        var pool = usableMatches.Count > 0 ? usableMatches : matches;
        if (BestFuzzyMatch(pool) is not { } best) return null;
        // The same id on several providers would otherwise be decided by catalog order
        var tied = pool.Where(model => model.Id == best.Id).ToList();
        if (tied.Select(model => model.Provider).Distinct(StringComparer.Ordinal).Count() == 1) return Resolved(best);
        return new(ModelReferenceStatus.Ambiguous, Candidates: [.. tied], UsableCount: usableMatches.Count > 0 ? tied.Count : 0);
    }

    /// <summary>Source bestFuzzyMatch: aliases (<c>claude-sonnet-4-5</c>) over dated versions (<c>claude-sonnet-4-5-20250929</c>), then
    /// the highest-sorting id.</summary>
    private static RegistryModel? BestFuzzyMatch(IReadOnlyList<RegistryModel> matches)
    {
        var aliases = matches.Where(model => IsAlias(model.Id)).ToList();
        IReadOnlyList<RegistryModel> pool = aliases.Count > 0 ? aliases : matches;
        // Array.prototype.sort is stable: equal ids keep catalog order.
        return pool.Select((model, index) => (model, index)).OrderBy(entry => entry.model.Id, Comparer<string>.Create((a, b) => LocaleCompare(b, a)))
            .ThenBy(entry => entry.index).Select(entry => entry.model).FirstOrDefault();
    }

    /// <summary>Source resolveModelScopeFromModels: globs (with an optional <c>:level</c>) match provider/id or id case-insensitively after an
    /// exact-reference check; other patterns resolve as lenient fuzzy references. Every model here is usable, so credentials cannot choose
    /// between providers offering the same id: all of them are added, with a warning so the user can pick one with provider/id.
    /// Duplicates are skipped.</summary>
    internal static (ImmutableArray<ScopedModel> Scoped, ImmutableArray<ModelScopeDiagnostic> Diagnostics) ResolveModelScope(
        IReadOnlyList<string> patterns, IReadOnlyList<RegistryModel> models)
    {
        var scoped = new List<ScopedModel>(); var diagnostics = new List<ModelScopeDiagnostic>();
        void Add(RegistryModel model, string? level) { if (!scoped.Any(entry => entry.Model.SameIdentity(model))) scoped.Add(new(model, level)); }
        void NoMatch(string pattern) => diagnostics.Add(new("no-match", $"No models match pattern \"{pattern}\"", pattern));
        void Ambiguous(string pattern, IEnumerable<RegistryModel> candidates)
        {
            var providers = candidates.Select(model => model.Provider).Distinct(StringComparer.Ordinal).Order(Comparer<string>.Create(LocaleCompare));
            diagnostics.Add(new("ambiguous", $"Pattern \"{pattern}\" matches several providers: {string.Join(", ", providers)}. Use provider/id to choose one.", pattern));
        }
        foreach (var pattern in patterns)
        {
            if (pattern.Contains('*') || pattern.Contains('?') || pattern.Contains('['))
            {
                // Extract optional thinking level suffix (e.g., "provider/*:high")
                var thinking = SplitThinkingSuffix(pattern);
                var glob = thinking?.Base ?? pattern; var level = thinking?.ThinkingLevel;
                // Ids such as `bracketed-model[1m]` are not globs
                var exact = ResolveModelReference(new(glob, null, ThinkingSuffixMode.None, false), models, static _ => true);
                IReadOnlyList<RegistryModel> matches;
                if (exact.Status == ModelReferenceStatus.Resolved) matches = [exact.Model!];
                else if (exact.Status == ModelReferenceStatus.Ambiguous) { matches = exact.Candidates; Ambiguous(pattern, matches); }
                else
                    matches = models.Where(model => Minimatch.IsMatch(model.Provider + "/" + model.Id, glob, true) || Minimatch.IsMatch(model.Id, glob, true)).ToList();
                if (matches.Count == 0) NoMatch(pattern);
                foreach (var model in matches) Add(model, level);
                continue;
            }
            var result = ResolveModelReference(new(pattern, null, ThinkingSuffixMode.Lenient, true), models, static _ => true);
            if (result.Status == ModelReferenceStatus.NotFound) { NoMatch(pattern); continue; }
            if (result.Warning is not null) diagnostics.Add(new("invalid-thinking-level", result.Warning, pattern));
            if (result.Status == ModelReferenceStatus.Ambiguous) Ambiguous(pattern, result.Candidates);
            foreach (var model in result.Status == ModelReferenceStatus.Resolved ? [result.Model!] : result.Candidates) Add(model, result.ThinkingLevel);
        }
        return ([.. scoped], [.. diagnostics]);
    }

    /// <summary>Source buildFallbackModel: the provider's default (or first) model under a custom id and name.</summary>
    private static RegistryModel BuildFallbackModel(ImmutableArray<RegistryModel> providerModels, string modelId)
    {
        var defaultId = DefaultModelFor(providerModels[0].Provider);
        var baseModel = defaultId is null ? providerModels[0] : providerModels.FirstOrDefault(model => model.Id == defaultId) ?? providerModels[0];
        return baseModel.With(json => { json["id"] = modelId; json["name"] = modelId; });
    }

    /// <summary>Source resolveCliModel over all chat models (not only authenticated ones, so <c>--api-key</c> can set up a provider). With
    /// <paramref name="inlineApiKey"/> the key goes to whichever provider is selected, so stored credentials do not choose it.</summary>
    internal static ResolveCliModelResult ResolveCliModel(string? cliProvider, string? cliModel, string? cliThinking,
        IReadOnlyList<RegistryModel> models, Func<string, bool> hasConfiguredAuth, bool inlineApiKey = false)
    {
        if (string.IsNullOrEmpty(cliModel)) return new(null, null, null, null);
        if (models.Count == 0) return new(null, null, null, "No models available. Check your installation or add models to models.json.");
        string? provider = null;
        if (!string.IsNullOrEmpty(cliProvider))
        {
            provider = models.FirstOrDefault(model => Lower(model.Provider) == Lower(cliProvider))?.Provider;
            if (provider is null) return new(null, null, null, $"Unknown provider \"{cliProvider}\". Use --list-models to see available providers/models.");
        }

        var result = ResolveModelReference(new(cliModel, provider, ThinkingSuffixMode.Strict, true), models,
            inlineApiKey ? static _ => false : hasConfiguredAuth);
        if (result.Status == ModelReferenceStatus.Resolved) return new(result.Model, result.ThinkingLevel, result.Warning, null);
        if (result.Status == ModelReferenceStatus.Ambiguous)
        {
            var matches = string.Join(", ", result.Candidates.Select(model => model.Provider + "/" + model.Id).Order(Comparer<string>.Create(LocaleCompare)));
            var authHint = inlineApiKey ? "--api-key does not choose a provider."
                : result.UsableCount == 0 ? "No matching provider is authenticated." : "More than one matching provider is authenticated.";
            return new(null, null, null, $"Model \"{cliModel}\" is ambiguous across providers: {matches}. {authHint} Use --provider or provider/model.");
        }

        if (result.Target is not { } target) return new(null, null, null, $"Model \"{cliModel}\" not found. Use --list-models to see available models.");
        // Use the thinking level suffix only when --thinking is not explicitly provided; otherwise it stays part of the custom model id.
        // e.g. "zai-org/GLM-5.1-FP8:high" → modelId="zai-org/GLM-5.1-FP8", fallbackThinking="high"
        var explicitThinking = !string.IsNullOrEmpty(cliThinking);
        var modelId = explicitThinking ? target.ModelText : target.ModelId;
        var fallbackThinking = explicitThinking ? null : target.ThinkingLevel;
        var fallbackModel = BuildFallbackModel(target.ProviderModels, modelId);
        var requestedThinking = cliThinking ?? fallbackThinking;
        var model = !string.IsNullOrEmpty(requestedThinking) && requestedThinking != "off" ? fallbackModel.With(json => json["reasoning"] = true) : fallbackModel;
        return new(model, fallbackThinking, $"Model \"{modelId}\" not found for provider \"{target.Provider}\". Using custom model id.", null);
    }

    /// <summary>Source findInitialModel: the first scoped model (not when continuing), the authenticated saved default, then the first
    /// available provider default, then the first available model. The CLI model is resolved before (resolveCliModel).</summary>
    internal static InitialModelResult FindInitialModel(IReadOnlyList<ScopedModel> scopedModels,
        bool isContinuing, string? defaultProvider, string? defaultModelId, string? defaultThinkingLevel,
        IReadOnlyDictionary<string, string>? modelThinkingLevels, ModelRegistry registry) =>
        FindInitialModel(scopedModels, isContinuing, defaultProvider, defaultModelId, defaultThinkingLevel, modelThinkingLevels,
            registry.Find, registry.HasConfiguredAuth, registry.GetAvailable);

    internal static InitialModelResult FindInitialModel(IReadOnlyList<ScopedModel> scopedModels,
        bool isContinuing, string? defaultProvider, string? defaultModelId, string? defaultThinkingLevel,
        IReadOnlyDictionary<string, string>? modelThinkingLevels, Func<string, string, RegistryModel?> find,
        Func<string, bool> hasConfiguredAuth, Func<IReadOnlyList<RegistryModel>> available)
    {
        if (scopedModels.Count > 0 && !isContinuing)
        {
            var first = scopedModels[0];
            string? perModel = null; modelThinkingLevels?.TryGetValue(first.Model.Reference, out perModel);
            return new(first.Model, first.ThinkingLevel ?? perModel ?? defaultThinkingLevel ?? DefaultThinkingLevel, null);
        }
        if (defaultProvider is not null && defaultModelId is not null && find(defaultProvider, defaultModelId) is { } saved && hasConfiguredAuth(saved.Provider))
        {
            string? perModel = null; modelThinkingLevels?.TryGetValue(defaultProvider + "/" + defaultModelId, out perModel);
            return new(saved, perModel ?? defaultThinkingLevel ?? DefaultThinkingLevel, null);
        }
        var models = available();
        if (models.Count > 0) return new(FirstDefault(models) ?? models[0], DefaultThinkingLevel, null);
        return new(null, DefaultThinkingLevel, null);
    }

    private static RegistryModel? FirstDefault(IReadOnlyList<RegistryModel> available)
    {
        foreach (var (provider, id) in DefaultModelPerProvider)
            if (available.FirstOrDefault(model => model.Provider == provider && model.Id == id) is { } match) return match;
        return null;
    }

    /// <summary>Source restoreModelFromSession (without console output; the messages are returned).</summary>
    internal static (RegistryModel? Model, string? FallbackMessage) RestoreModelFromSession(string savedProvider, string savedModelId,
        RegistryModel? currentModel, ModelRegistry registry)
    {
        var restored = registry.Find(savedProvider, savedModelId);
        if (restored is not null && registry.HasConfiguredAuth(restored.Provider)) return (restored, null);
        var reason = restored is null ? "model no longer exists" : "no auth configured";
        if (currentModel is not null)
            return (currentModel, $"Could not restore model {savedProvider}/{savedModelId} ({reason}). Using {currentModel.Provider}/{currentModel.Id}.");
        var models = registry.GetAvailable();
        if (models.Count == 0) return (null, null);
        var fallback = FirstDefault(models) ?? models[0];
        return (fallback, $"Could not restore model {savedProvider}/{savedModelId} ({reason}). Using {fallback.Provider}/{fallback.Id}.");
    }

    [GeneratedRegex("-\\d{8}$")]
    private static partial Regex DateSuffix();
}

internal sealed class ModelSelectionException(string message, string code = "UnknownLiveModel") : Exception(message)
{ internal string Code { get; } = code; }

/// <summary>The minimatch subset model scoping uses: <c>*</c>, <c>?</c>, <c>[...]</c> classes, <c>**</c> segments, <c>{a,b}</c> braces,
/// leading <c>!</c> negation and the dot-file rule, matched per <c>/</c> segment; optionally case-insensitive. Like minimatch 11 (with
/// brace-expansion 5 and nonrecursive-glob-match), amplified patterns stay bounded: brace expansion is capped, a segment is matched
/// without backtracking and <c>**</c> without a recursive walk per split.</summary>
internal static class Minimatch
{
    // brace-expansion 5: EXPANSION_MAX results and EXPANSION_MAX_LENGTH characters at any point (truncated, not thrown), chained sets
    // combined iteratively, and nesting followed EXPANSION_MAX_DEPTH levels (deeper stays literal).
    private const int ExpansionMax = 100_000, ExpansionMaxLength = 4_000_000, ExpansionMaxDepth = 1_000;
    // One scope pattern is matched against every model, so its expansion is kept (minimatch's cache holds 256 patterns).
    private static readonly Dictionary<string, string[][]> Expanded = new(StringComparer.Ordinal);

    internal static bool IsMatch(string path, string pattern, bool noCase)
    {
        var negated = false;
        while (pattern.StartsWith('!')) { negated = !negated; pattern = pattern[1..]; }
        var segments = path.Split('/');
        var result = Rows(pattern).Any(row => MatchSegments(segments, row, noCase));
        return negated ? !result : result;
    }

    private static string[][] Rows(string pattern)
    {
        lock (Expanded)
        {
            if (Expanded.TryGetValue(pattern, out var rows)) return rows;
            if (Expanded.Count >= 256) Expanded.Clear();
            return Expanded[pattern] = [.. ExpandBraces(pattern, 0).Select(expanded => expanded.Split('/'))];
        }
    }

    private static List<string> ExpandBraces(string pattern, int depth)
    {
        if (depth > ExpansionMaxDepth) return [pattern];
        List<string> expansions = [""];
        var rest = pattern;
        while (FindBraceSet(rest) is (var open, var close, var options))
        {
            var head = rest[..open];
            rest = rest[(close + 1)..];
            var alternatives = new List<string>(); var length = 0L; var full = false;
            foreach (var option in options)
            {
                foreach (var alternative in ExpandBraces(option, depth + 1))
                {
                    if (full = alternatives.Count >= ExpansionMax || length + alternative.Length > ExpansionMaxLength) break;
                    alternatives.Add(alternative); length += alternative.Length;
                }
                if (full) break;
            }
            expansions = Combine(expansions, head, alternatives);
        }
        return Combine(expansions, rest, [""]);
    }

    // The first top-level brace set with a comma; sets without one stay literal and the scan goes on.
    private static (int Open, int Close, List<string> Options)? FindBraceSet(string pattern)
    {
        var depth = 0; var open = -1;
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\') { index++; continue; }
            if (pattern[index] == '{') { if (depth++ == 0) open = index; }
            else if (pattern[index] == '}' && depth > 0 && --depth == 0)
            {
                var options = SplitTop(pattern[(open + 1)..index]);
                if (options.Count >= 2) return (open, index, options);
            }
        }
        return null;
    }

    private static List<string> Combine(List<string> prefixes, string head, List<string> alternatives)
    {
        var result = new List<string>(); var length = 0L;
        foreach (var prefix in prefixes)
            foreach (var alternative in alternatives)
            {
                var size = prefix.Length + head.Length + alternative.Length;
                if (result.Count >= ExpansionMax || length + size > ExpansionMaxLength) return result;
                result.Add(prefix + head + alternative); length += size;
            }
        return result;
    }

    private static List<string> SplitTop(string body)
    {
        var parts = new List<string>(); var depth = 0; var start = 0;
        for (var index = 0; index < body.Length; index++)
        {
            if (body[index] == '\\') { index++; continue; }
            if (body[index] == '{') depth++;
            else if (body[index] == '}') depth--;
            else if (body[index] == ',' && depth == 0) { parts.Add(body[start..index]); start = index + 1; }
        }
        parts.Add(body[start..]);
        return parts;
    }

    private static bool MatchSegments(string[] path, string[] pattern, bool noCase)
    {
        // match[p, q]: path[p..] matches pattern[q..], filled from the end, so a pattern with many ** costs path x pattern steps instead
        // of a recursive walk over every split; a ** never swallows a dot segment.
        var width = pattern.Length + 1;
        var match = new bool[(path.Length + 1) * width];
        for (var p = path.Length; p >= 0; p--)
            for (var q = pattern.Length; q >= 0; q--)
                match[p * width + q] = q == pattern.Length ? p == path.Length
                    : pattern[q] == "**" ? match[p * width + q + 1] || p < path.Length && !path[p].StartsWith('.') && match[(p + 1) * width + q]
                    : p < path.Length && match[(p + 1) * width + q + 1] && MatchSegment(path[p], pattern[q], noCase);
        return match[0];
    }

    private static bool MatchSegment(string segment, string pattern, bool noCase)
    {
        if (segment.StartsWith('.') && !pattern.StartsWith('.') && pattern.Length > 0 && pattern[0] is '*' or '?' or '[') return false;
        // Tokens: a literal, '?' (any character), a class (a one-character regex) or '*' (any run).
        var tokens = new List<(char Kind, char Char, string? Class)>();
        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (current == '\\' && index + 1 < pattern.Length) { tokens.Add(('c', pattern[++index], null)); continue; }
            if (current is '*' or '?') { tokens.Add((current, current, null)); continue; }
            if (current == '[')
            {
                var close = pattern.IndexOf(']', index + 2 <= pattern.Length ? index + 2 : pattern.Length);
                if (close > index)
                {
                    var body = pattern[(index + 1)..close];
                    var negate = body.StartsWith('!') || body.StartsWith('^');
                    if (negate) body = body[1..];
                    tokens.Add(('[', current, "^[" + (negate ? "^" : "") + body.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal) + "]$"));
                    index = close; continue;
                }
            }
            tokens.Add(('c', current, null));
        }
        var options = (noCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant | RegexOptions.Singleline;
        bool Accepts((char Kind, char Char, string? Class) token, char c) => token.Kind switch
        {
            '?' => true,
            '[' => Regex.IsMatch(c.ToString(), token.Class!, options),
            _ => token.Char == c || noCase && (char.ToLowerInvariant(token.Char) == char.ToLowerInvariant(c) || char.ToUpperInvariant(token.Char) == char.ToUpperInvariant(c))
        };
        // A greedy scan that returns to the last star on a mismatch (nonrecursive-glob-match's linear chain) decides the segment in
        // pattern x segment steps, where a backtracking regex takes exponential time on "*a*a*a*b".
        int si = 0, ti = 0, star = -1, resume = 0;
        while (si < segment.Length)
        {
            if (ti < tokens.Count && tokens[ti].Kind == '*') { star = ti++; resume = si; continue; }
            if (ti < tokens.Count && Accepts(tokens[ti], segment[si])) { ti++; si++; continue; }
            if (star < 0) return false;
            ti = star + 1; si = ++resume;
        }
        while (ti < tokens.Count && tokens[ti].Kind == '*') ti++;
        return ti == tokens.Count;
    }
}
