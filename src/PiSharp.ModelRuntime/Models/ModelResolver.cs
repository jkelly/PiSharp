// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/model-resolver.ts,
// packages/coding-agent/src/cli/args.ts (isValidThinkingLevel, --models splitting) and packages/coding-agent/src/main.ts
// (buildSessionOptions model and scoped-model selection).
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Models;

/// <summary>Source ScopedModel: a model and the thinking level a pattern named explicitly.</summary>
internal sealed record ScopedModel(RegistryModel Model, string? ThinkingLevel);

/// <summary>Source ParsedModelResult.</summary>
internal sealed record ParsedModelResult(RegistryModel? Model, string? ThinkingLevel, string? Warning);

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

    /// <summary>Source findExactModelReferenceMatch: canonical provider/id, then a provider/id split, then a unique bare id.</summary>
    internal static RegistryModel? FindExactModelReferenceMatch(string reference, IReadOnlyList<RegistryModel> models)
    {
        var trimmed = reference.Trim();
        if (trimmed.Length == 0) return null;
        var normalized = Lower(trimmed);
        var canonical = models.Where(model => Lower(model.Provider + "/" + model.Id) == normalized).ToList();
        if (canonical.Count == 1) return canonical[0];
        if (canonical.Count > 1) return null;
        var slash = trimmed.IndexOf('/');
        if (slash >= 0)
        {
            var provider = trimmed[..slash].Trim(); var id = trimmed[(slash + 1)..].Trim();
            if (provider.Length > 0 && id.Length > 0)
            {
                var matches = models.Where(model => Lower(model.Provider) == Lower(provider) && Lower(model.Id) == Lower(id)).ToList();
                if (matches.Count == 1) return matches[0];
                if (matches.Count > 1) return null;
            }
        }
        var ids = models.Where(model => Lower(model.Id) == normalized).ToList();
        return ids.Count == 1 ? ids[0] : null;
    }

    /// <summary>Source tryMatchModel: an exact reference, else partial id/name matches preferring the highest-sorting alias, else the
    /// latest dated id.</summary>
    private static RegistryModel? TryMatchModel(string pattern, IReadOnlyList<RegistryModel> models)
    {
        if (FindExactModelReferenceMatch(pattern, models) is { } exact) return exact;
        var lower = Lower(pattern);
        var matches = models.Where(model => Lower(model.Id).Contains(lower, StringComparison.Ordinal) ||
            model.Name is not null && Lower(model.Name).Contains(lower, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0) return null;
        var aliases = matches.Where(model => IsAlias(model.Id)).ToList();
        var pool = aliases.Count > 0 ? aliases : matches.Where(model => !IsAlias(model.Id)).ToList();
        // Array.prototype.sort is stable: equal ids keep catalog order.
        return pool.Select((model, index) => (model, index)).OrderBy(entry => entry.model.Id, Comparer<string>.Create((a, b) => LocaleCompare(b, a)))
            .ThenBy(entry => entry.index).First().model;
    }

    /// <summary>Source parseModelPattern: the whole pattern first, then the last <c>:suffix</c> as a thinking level. In strict mode
    /// (CLI --model) an invalid suffix stays part of the id.</summary>
    internal static ParsedModelResult ParseModelPattern(string pattern, IReadOnlyList<RegistryModel> models, bool allowInvalidThinkingLevelFallback = true)
    {
        if (TryMatchModel(pattern, models) is { } exact) return new(exact, null, null);
        var colon = pattern.LastIndexOf(':');
        if (colon < 0) return new(null, null, null);
        var prefix = pattern[..colon]; var suffix = pattern[(colon + 1)..];
        if (IsValidThinkingLevel(suffix))
        {
            var result = ParseModelPattern(prefix, models, allowInvalidThinkingLevelFallback);
            return result.Model is not null ? new(result.Model, result.Warning is not null ? null : suffix, result.Warning) : result;
        }
        if (!allowInvalidThinkingLevelFallback) return new(null, null, null);
        var inner = ParseModelPattern(prefix, models, allowInvalidThinkingLevelFallback);
        return inner.Model is not null
            ? new(inner.Model, null, $"Invalid thinking level \"{suffix}\" in pattern \"{pattern}\". Using default instead.")
            : inner;
    }

    /// <summary>Source resolveModelScopeFromModels: globs (with an optional <c>:level</c>) match provider/id or id case-insensitively after an
    /// exact-reference check; other patterns go through parseModelPattern. Duplicates are skipped.</summary>
    internal static (ImmutableArray<ScopedModel> Scoped, ImmutableArray<ModelScopeDiagnostic> Diagnostics) ResolveModelScope(
        IReadOnlyList<string> patterns, IReadOnlyList<RegistryModel> models)
    {
        var scoped = new List<ScopedModel>(); var diagnostics = new List<ModelScopeDiagnostic>();
        void Add(RegistryModel model, string? level) { if (!scoped.Any(entry => entry.Model.SameIdentity(model))) scoped.Add(new(model, level)); }
        foreach (var pattern in patterns)
        {
            if (pattern.Contains('*') || pattern.Contains('?') || pattern.Contains('['))
            {
                var glob = pattern; string? level = null;
                var colon = pattern.LastIndexOf(':');
                if (colon >= 0 && IsValidThinkingLevel(pattern[(colon + 1)..])) { level = pattern[(colon + 1)..]; glob = pattern[..colon]; }
                if (FindExactModelReferenceMatch(glob, models) is { } exact) { Add(exact, level); continue; }
                var matching = models.Where(model => Minimatch.IsMatch(model.Provider + "/" + model.Id, glob, true) || Minimatch.IsMatch(model.Id, glob, true)).ToList();
                if (matching.Count == 0) { diagnostics.Add(new("no-match", $"No models match pattern \"{pattern}\"", pattern)); continue; }
                foreach (var model in matching) Add(model, level);
                continue;
            }
            var parsed = ParseModelPattern(pattern, models);
            if (parsed.Warning is not null) diagnostics.Add(new("invalid-thinking-level", parsed.Warning, pattern));
            if (parsed.Model is null) { diagnostics.Add(new("no-match", $"No models match pattern \"{pattern}\"", pattern)); continue; }
            Add(parsed.Model, parsed.ThinkingLevel);
        }
        return ([.. scoped], [.. diagnostics]);
    }

    /// <summary>Source buildFallbackModel: the provider's default (or first) model under a custom id and name.</summary>
    private static RegistryModel? BuildFallbackModel(string provider, string modelId, IReadOnlyList<RegistryModel> models)
    {
        var providerModels = models.Where(model => model.Provider == provider).ToList();
        if (providerModels.Count == 0) return null;
        var defaultId = DefaultModelFor(provider);
        var baseModel = defaultId is null ? providerModels[0] : providerModels.FirstOrDefault(model => model.Id == defaultId) ?? providerModels[0];
        return baseModel.With(json => { json["id"] = modelId; json["name"] = modelId; });
    }

    /// <summary>Source resolveCliModel over all chat models (not only authenticated ones).</summary>
    internal static ResolveCliModelResult ResolveCliModel(string? cliProvider, string? cliModel, string? cliThinking,
        IReadOnlyList<RegistryModel> models, Func<string, bool> hasConfiguredAuth)
    {
        if (string.IsNullOrEmpty(cliModel)) return new(null, null, null, null);
        if (models.Count == 0) return new(null, null, null, "No models available. Check your installation or add models to models.json.");
        var providers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var model in models) providers[Lower(model.Provider)] = model.Provider;
        string? provider = null;
        if (cliProvider is not null && !providers.TryGetValue(Lower(cliProvider), out provider))
            return new(null, null, null, $"Unknown provider \"{cliProvider}\". Use --list-models to see available providers/models.");
        var pattern = cliModel; var inferred = false;
        if (provider is null)
        {
            var slash = cliModel.IndexOf('/');
            if (slash >= 0 && providers.TryGetValue(Lower(cliModel[..slash]), out var canonical))
            { provider = canonical; pattern = cliModel[(slash + 1)..]; inferred = true; }
        }
        if (provider is null)
        {
            var lower = Lower(cliModel);
            var exact = models.Where(model => Lower(model.Id) == lower || Lower(model.Provider + "/" + model.Id) == lower).ToList();
            if (exact.Count == 1) return new(exact[0], null, null, null);
            if (exact.Count > 1)
            {
                var authenticated = exact.Where(model => hasConfiguredAuth(model.Provider)).ToList();
                if (authenticated.Count == 1) return new(authenticated[0], null, null, null);
                var names = exact.Select(model => model.Provider + "/" + model.Id).Order(Comparer<string>.Create(LocaleCompare));
                var hint = authenticated.Count == 0 ? "No matching provider is authenticated." : "More than one matching provider is authenticated.";
                return new(null, null, null, $"Model \"{cliModel}\" is ambiguous across providers: {string.Join(", ", names)}. {hint} Use --provider or provider/model.");
            }
        }
        if (cliProvider is not null && provider is not null && Lower(cliModel).StartsWith(Lower(provider + "/"), StringComparison.Ordinal))
            pattern = cliModel[(provider.Length + 1)..];
        var candidates = provider is null ? models : models.Where(model => model.Provider == provider).ToList();
        var parsed = ParseModelPattern(pattern, candidates, allowInvalidThinkingLevelFallback: false);
        if (parsed.Model is { } found)
        {
            if (inferred)
            {
                var raw = models.Where(model => Lower(model.Id) == Lower(cliModel) && !model.SameIdentity(found)).ToList();
                if (raw.Count > 0 && !hasConfiguredAuth(found.Provider))
                {
                    var authenticated = raw.Where(model => hasConfiguredAuth(model.Provider)).ToList();
                    if (authenticated.Count == 1) return new(authenticated[0], null, null, null);
                }
            }
            return new(found, parsed.ThinkingLevel, parsed.Warning, null);
        }
        if (inferred)
        {
            var lower = Lower(cliModel);
            if (models.FirstOrDefault(model => Lower(model.Id) == lower || Lower(model.Provider + "/" + model.Id) == lower) is { } exact)
                return new(exact, null, null, null);
            var fallback = ParseModelPattern(cliModel, models, allowInvalidThinkingLevelFallback: false);
            if (fallback.Model is not null) return new(fallback.Model, fallback.ThinkingLevel, fallback.Warning, null);
        }
        if (provider is not null)
        {
            var fallbackPattern = pattern; string? fallbackThinking = null;
            if (cliThinking is null)
            {
                var colon = pattern.LastIndexOf(':');
                if (colon >= 0 && IsValidThinkingLevel(pattern[(colon + 1)..])) { fallbackThinking = pattern[(colon + 1)..]; fallbackPattern = pattern[..colon]; }
            }
            if (BuildFallbackModel(provider, fallbackPattern, models) is { } fallbackModel)
            {
                var requested = cliThinking ?? fallbackThinking;
                var model = requested is not null && requested != "off" ? fallbackModel.With(json => json["reasoning"] = true) : fallbackModel;
                var message = $"Model \"{fallbackPattern}\" not found for provider \"{provider}\". Using custom model id.";
                return new(model, fallbackThinking, parsed.Warning is null ? message : parsed.Warning + " " + message, null);
            }
        }
        var display = provider is not null ? provider + "/" + pattern : cliModel;
        return new(null, null, parsed.Warning, $"Model \"{display}\" not found. Use --list-models to see available models.");
    }

    /// <summary>Source findInitialModel: CLI provider+model, the first scoped model (not when continuing), the authenticated saved
    /// default, then the first available provider default, then the first available model.</summary>
    internal static InitialModelResult FindInitialModel(string? cliProvider, string? cliModel, IReadOnlyList<ScopedModel> scopedModels,
        bool isContinuing, string? defaultProvider, string? defaultModelId, string? defaultThinkingLevel,
        IReadOnlyDictionary<string, string>? modelThinkingLevels, ModelRegistry registry) =>
        FindInitialModel(cliProvider, cliModel, scopedModels, isContinuing, defaultProvider, defaultModelId, defaultThinkingLevel, modelThinkingLevels,
            registry.GetAll, registry.Find, registry.HasConfiguredAuth, registry.GetAvailable);

    internal static InitialModelResult FindInitialModel(string? cliProvider, string? cliModel, IReadOnlyList<ScopedModel> scopedModels,
        bool isContinuing, string? defaultProvider, string? defaultModelId, string? defaultThinkingLevel,
        IReadOnlyDictionary<string, string>? modelThinkingLevels, Func<IReadOnlyList<RegistryModel>> all, Func<string, string, RegistryModel?> find,
        Func<string, bool> hasConfiguredAuth, Func<IReadOnlyList<RegistryModel>> available)
    {
        if (cliProvider is not null && cliModel is not null)
        {
            var resolved = ResolveCliModel(cliProvider, cliModel, null, all(), hasConfiguredAuth);
            if (resolved.Error is not null) throw new ModelSelectionException(resolved.Error);
            if (resolved.Model is not null) return new(resolved.Model, DefaultThinkingLevel, null);
        }
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
