using PiSharp.AI.Catalogs;
using PiSharp.Cli.Models;

// Ported from packages/coding-agent/test/model-resolver.test.ts (v1.1.0): the same mock models and expectations.
internal static partial class Program
{
    private static readonly RegistryModel[] MockModels =
    [
        M("anthropic", "claude-sonnet-4-5", "Claude Sonnet 4.5", reasoning: true, input: ["text", "image"], contextWindow: 200000),
        M("openai", "gpt-4o", "GPT-4o", input: ["text", "image"], maxTokens: 4096),
    ];
    private static readonly RegistryModel[] OpenRouterModels =
    [
        M("openrouter", "qwen/qwen3-coder:exacto", "Qwen3 Coder Exacto", reasoning: true),
        M("openrouter", "openai/gpt-4o:extended", "GPT-4o Extended", input: ["text", "image"], maxTokens: 4096),
    ];
    private static RegistryModel[] AllModels => [.. MockModels, .. OpenRouterModels];

    private static IEnumerable<(string, Func<Task>)> ResolverCases() =>
    [
        ("resolver.parse-simple-patterns", Sync(ParseSimple)),
        ("resolver.parse-valid-and-invalid-thinking-levels", Sync(ParseThinking)),
        ("resolver.parse-openrouter-colon-ids", Sync(ParseColons)),
        ("resolver.parse-edge-cases", Sync(ParseEdges)),
        ("resolver.alias-and-dated-preference", Sync(AliasPreference)),
        ("resolver.scope-diagnostics-globs-and-brackets", Sync(Scope)),
        ("resolver.cli-provider-model-fuzzy-and-thinking", Sync(CliBasics)),
        ("resolver.cli-ambiguity-and-authenticated-preference", Sync(CliAmbiguity)),
        ("resolver.cli-provider-split-preference", Sync(CliProviderSplit)),
        ("resolver.cli-custom-fallback-thinking-suffix", Sync(CliFallback)),
        ("resolver.defaults-exist-in-generated-catalogs", Sync(DefaultsExist)),
        ("resolver.find-initial-model", Sync(InitialModel)),
        ("resolver.restore-model-from-session", Sync(RestoreModel)),
    ];

    private static void ParseSimple()
    {
        var exact = ModelResolver.ParseModelPattern("claude-sonnet-4-5", AllModels);
        Equal("claude-sonnet-4-5", exact.Model?.Id, "exact"); Equal<string?>(null, exact.ThinkingLevel, "exact level"); Equal<string?>(null, exact.Warning, "exact warning");
        Equal("claude-sonnet-4-5", ModelResolver.ParseModelPattern("sonnet", AllModels).Model?.Id, "partial");
        var none = ModelResolver.ParseModelPattern("nonexistent", AllModels);
        Check(none.Model is null && none.ThinkingLevel is null && none.Warning is null, "no match");
    }

    private static void ParseThinking()
    {
        var high = ModelResolver.ParseModelPattern("sonnet:high", AllModels);
        Equal("claude-sonnet-4-5", high.Model?.Id, "sonnet:high"); Equal("high", high.ThinkingLevel, "high");
        Equal("medium", ModelResolver.ParseModelPattern("gpt-4o:medium", AllModels).ThinkingLevel, "gpt-4o:medium");
        foreach (var level in new[] { "off", "minimal", "low", "medium", "high", "xhigh", "max" })
        {
            var parsed = ModelResolver.ParseModelPattern("sonnet:" + level, AllModels);
            Equal(level, parsed.ThinkingLevel, level); Equal<string?>(null, parsed.Warning, level + " warning");
        }
        var random = ModelResolver.ParseModelPattern("sonnet:random", AllModels);
        Equal("claude-sonnet-4-5", random.Model?.Id, "random model"); Equal<string?>(null, random.ThinkingLevel, "random level");
        Equal("Invalid thinking level \"random\" in pattern \"sonnet:random\". Using default instead.", random.Warning, "random warning");
        var invalid = ModelResolver.ParseModelPattern("gpt-4o:invalid", AllModels);
        Equal("gpt-4o", invalid.Model?.Id, "invalid model"); Check(invalid.Warning!.Contains("Invalid thinking level", StringComparison.Ordinal), "invalid warning");
    }

    private static void ParseColons()
    {
        Equal("qwen/qwen3-coder:exacto", ModelResolver.ParseModelPattern("qwen/qwen3-coder:exacto", AllModels).Model?.Id, "exacto");
        var prefixed = ModelResolver.ParseModelPattern("openrouter/qwen/qwen3-coder:exacto", AllModels);
        Equal("openrouter", prefixed.Model?.Provider, "prefixed provider"); Equal<string?>(null, prefixed.ThinkingLevel, "prefixed level");
        var high = ModelResolver.ParseModelPattern("qwen/qwen3-coder:exacto:high", AllModels);
        Equal("qwen/qwen3-coder:exacto", high.Model?.Id, "exacto:high"); Equal("high", high.ThinkingLevel, "exacto:high level");
        var both = ModelResolver.ParseModelPattern("openrouter/qwen/qwen3-coder:exacto:high", AllModels);
        Equal("openrouter", both.Model?.Provider, "both provider"); Equal("high", both.ThinkingLevel, "both level");
        Equal("openai/gpt-4o:extended", ModelResolver.ParseModelPattern("openai/gpt-4o:extended", AllModels).Model?.Id, "extended");
        var random = ModelResolver.ParseModelPattern("qwen/qwen3-coder:exacto:random", AllModels);
        Check(random.Model?.Id == "qwen/qwen3-coder:exacto" && random.ThinkingLevel is null && random.Warning!.Contains("random", StringComparison.Ordinal), "exacto:random");
        var chained = ModelResolver.ParseModelPattern("qwen/qwen3-coder:exacto:high:random", AllModels);
        Check(chained.Model?.Id == "qwen/qwen3-coder:exacto" && chained.ThinkingLevel is null && chained.Warning!.Contains("random", StringComparison.Ordinal), "exacto:high:random");
    }

    private static void ParseEdges()
    {
        var empty = ModelResolver.ParseModelPattern("", AllModels);
        Check(empty.Model is not null && empty.ThinkingLevel is null, "empty pattern matches via partial matching");
        var colon = ModelResolver.ParseModelPattern("sonnet:", AllModels);
        Equal("claude-sonnet-4-5", colon.Model?.Id, "trailing colon"); Check(colon.Warning!.Contains("Invalid thinking level", StringComparison.Ordinal), "trailing colon warning");
    }

    private static void AliasPreference()
    {
        RegistryModel[] models = [M("anthropic", "claude-sonnet-4-5-20250929"), M("anthropic", "claude-sonnet-4-5-20240101"), M("anthropic", "claude-sonnet-4-5")];
        Equal("claude-sonnet-4-5", ModelResolver.ParseModelPattern("sonnet-4", models).Model?.Id, "alias over dated");
        Equal("claude-sonnet-4-5-20250929", ModelResolver.ParseModelPattern("sonnet-4", models[..2]).Model?.Id, "latest dated");
        RegistryModel[] aliases = [M("x", "a-model"), M("x", "b-model-latest")];
        Equal("b-model-latest", ModelResolver.ParseModelPattern("model", aliases).Model?.Id, "highest-sorting alias");
    }

    private static void Scope()
    {
        var (scoped, diagnostics) = ModelResolver.ResolveModelScope(["sonnet:high", "gpt-4o:invalid", "missing"], AllModels);
        Names(["claude-sonnet-4-5", "gpt-4o"], scoped.Select(entry => entry.Model.Id), "scoped ids");
        Equal("high", scoped[0].ThinkingLevel, "scoped level"); Equal<string?>(null, scoped[1].ThinkingLevel, "unscoped level");
        Names(["invalid-thinking-level|Invalid thinking level \"invalid\" in pattern \"gpt-4o:invalid\". Using default instead.|gpt-4o:invalid",
            "no-match|No models match pattern \"missing\"|missing"], diagnostics.Select(d => d.Code + "|" + d.Message + "|" + d.Pattern), "diagnostics");
        var bracketed = M("custom", "bracketed-model[1m]", "Bracketed Model", reasoning: true);
        var exact = ModelResolver.ResolveModelScope(["custom/bracketed-model[1m]"], [.. AllModels, bracketed]);
        Names(["bracketed-model[1m]"], exact.Scoped.Select(entry => entry.Model.Id), "bracketed"); Equal(0, exact.Diagnostics.Length, "bracketed diagnostics");
        var withLevel = ModelResolver.ResolveModelScope(["custom/bracketed-model[1m]:high"], [.. AllModels, bracketed]);
        Equal("high", withLevel.Scoped.Single().ThinkingLevel, "bracketed level");
        // minimatch: `*` stays inside one segment, so slash-bearing OpenRouter ids need `**`; "openrouter/*" matches none of them.
        Equal("no-match", ModelResolver.ResolveModelScope(["openrouter/*"], AllModels).Diagnostics.Single().Code, "single star");
        var globs = ModelResolver.ResolveModelScope(["openrouter/**:low", "*SONNET*", "anthropic/claude-sonnet-4-5", "*nomatch*"], AllModels);
        Names(["openrouter/qwen/qwen3-coder:exacto", "openrouter/openai/gpt-4o:extended", "anthropic/claude-sonnet-4-5"],
            globs.Scoped.Select(entry => entry.Model.Reference), "glob scope");
        Check(globs.Scoped.Take(2).All(entry => entry.ThinkingLevel == "low"), "glob level");
        Names(["no-match|No models match pattern \"*nomatch*\""], globs.Diagnostics.Select(d => d.Code + "|" + d.Message), "glob diagnostics");
        Check(!Minimatch.IsMatch("anthropic/claude-sonnet", "*sonnet*", true), "star does not cross a slash");
        Check(Minimatch.IsMatch("a/b/c", "a/**", false) && Minimatch.IsMatch("gpt-4o", "gpt-{4o,5}", false) && !Minimatch.IsMatch(".hidden", "*", false),
            "globstar, braces and dot rule");
        Names(["claude-sonnet", "claude-haiku", "gpt-4o"], ModelResolver.SplitPatterns(" claude-sonnet,claude-haiku,, gpt-4o "), "--models split");
    }

    private static Func<string, bool> NoAuth => _ => false;

    private static void CliBasics()
    {
        var split = ModelResolver.ResolveCliModel(null, "openai/gpt-4o", null, AllModels, NoAuth);
        Check(split.Error is null && split.Model?.Provider == "openai" && split.Model.Id == "gpt-4o", "provider/id without --provider");
        var fuzzy = ModelResolver.ResolveCliModel("openai", "4o", null, AllModels, NoAuth);
        Check(fuzzy.Error is null && fuzzy.Model?.Reference == "openai/gpt-4o", "fuzzy within provider");
        var thinking = ModelResolver.ResolveCliModel(null, "sonnet:high", null, AllModels, NoAuth);
        Check(thinking.Model?.Id == "claude-sonnet-4-5" && thinking.ThinkingLevel == "high", ":thinking shorthand");
        var exactRaw = ModelResolver.ResolveCliModel(null, "openai/gpt-4o:extended", null, AllModels, NoAuth);
        Check(exactRaw.Error is null && exactRaw.Model?.Reference == "openrouter/openai/gpt-4o:extended", "exact raw id over provider inference");
        var rawSuffix = ModelResolver.ResolveCliModel("openai", "gpt-4o:extended", null, AllModels, NoAuth);
        Check(rawSuffix.Error is null && rawSuffix.Model?.Provider == "openai" && rawSuffix.Model.Id == "gpt-4o:extended", "invalid suffix stays in the id");
        Check(rawSuffix.Warning == "Model \"gpt-4o:extended\" not found for provider \"openai\". Using custom model id.", "custom id warning: " + rawSuffix.Warning);
        var ghost = ModelResolver.ResolveCliModel("openrouter", "openrouter/openai/ghost-model", null, AllModels, NoAuth);
        Check(ghost.Error is null && ghost.Model?.Provider == "openrouter" && ghost.Model.Id == "openai/ghost-model", "no double prefix");
        Equal("No models available. Check your installation or add models to models.json.", ModelResolver.ResolveCliModel("openai", "gpt-4o", null, [], NoAuth).Error, "no models");
        Equal("Unknown provider \"nope\". Use --list-models to see available providers/models.", ModelResolver.ResolveCliModel("nope", "x", null, AllModels, NoAuth).Error, "unknown provider");
        Equal("Model \"missing\" not found. Use --list-models to see available models.", ModelResolver.ResolveCliModel(null, "missing", null, AllModels, NoAuth).Error, "not found");
        var prefixedFuzzy = ModelResolver.ResolveCliModel(null, "openrouter/qwen", null, AllModels, NoAuth);
        Equal("openrouter/qwen/qwen3-coder:exacto", prefixedFuzzy.Model?.Reference, "provider-prefixed fuzzy");
        Check(ModelResolver.ResolveCliModel(null, null, null, AllModels, NoAuth) is { Model: null, Error: null }, "no --model");
    }

    private static void CliAmbiguity()
    {
        var azure = M("azure", "gpt-5.6-sol", "GPT 5.6 Sol"); var codex = M("openai-codex", "gpt-5.6-sol", "GPT 5.6 Sol");
        var sole = ModelResolver.ResolveCliModel(null, "gpt-5.6-sol", null, [azure, codex], provider => provider == "openai-codex");
        Check(sole.Error is null && sole.Model?.Provider == "openai-codex", "sole authenticated provider");
        var none = ModelResolver.ResolveCliModel(null, "gpt-5.6-sol", null, [azure, codex], NoAuth);
        Equal("Model \"gpt-5.6-sol\" is ambiguous across providers: azure/gpt-5.6-sol, openai-codex/gpt-5.6-sol. No matching provider is authenticated. Use --provider or provider/model.",
            none.Error, "ambiguous without auth");
        var both = ModelResolver.ResolveCliModel(null, "gpt-5.6-sol", null, [azure, codex], _ => true);
        Check(both.Error!.EndsWith("More than one matching provider is authenticated. Use --provider or provider/model.", StringComparison.Ordinal), "ambiguous with two");
    }

    private static void CliProviderSplit()
    {
        var zai = M("zai", "glm-5", "GLM-5", reasoning: true); var gateway = M("vercel-ai-gateway", "zai/glm-5", "GLM-5", reasoning: true);
        var split = ModelResolver.ResolveCliModel(null, "zai/glm-5", null, [.. AllModels, zai, gateway], _ => true);
        Equal("zai/glm-5", split.Model?.Reference, "provider/model split preferred");
        var commandcode = M("commandcode", "xiaomi/mimo-v2.5-pro", "Xiaomi MiMo via Commandcode"); var xiaomi = M("xiaomi", "mimo-v2.5-pro", "Xiaomi MiMo");
        var raw = ModelResolver.ResolveCliModel(null, "xiaomi/mimo-v2.5-pro", null, [.. AllModels, commandcode, xiaomi], provider => provider == "commandcode");
        Equal("commandcode/xiaomi/mimo-v2.5-pro", raw.Model?.Reference, "authenticated raw id over unauthenticated inference");
    }

    private static void CliFallback()
    {
        RegistryModel[] models = [.. AllModels, M("neuralwatt", "some-base-model", "Some Base Model", baseUrl: "https://api.neuralwatt.com")];
        var high = ModelResolver.ResolveCliModel(null, "neuralwatt/zai-org/GLM-5.1-FP8:high", null, models, NoAuth);
        Check(high.Error is null && high.Model?.Provider == "neuralwatt" && high.Model.Id == "zai-org/GLM-5.1-FP8" && high.Model.Reasoning && high.ThinkingLevel == "high",
            ":high stripped from the custom id");
        Equal("zai-org/GLM-5.1-FP8", high.Model!.Name, "fallback name"); Equal("https://api.neuralwatt.com", high.Model.BaseUrl, "fallback inherits base model");
        var plain = ModelResolver.ResolveCliModel(null, "neuralwatt/zai-org/GLM-5.1-FP8", null, models, NoAuth);
        Check(plain.Model?.Id == "zai-org/GLM-5.1-FP8" && plain.ThinkingLevel is null && !plain.Model.Reasoning, "plain fallback");
        foreach (var level in new[] { "off", "minimal", "low", "medium", "high", "xhigh", "max" })
        {
            var parsed = ModelResolver.ResolveCliModel(null, "neuralwatt/zai-org/GLM-5.1-FP8:" + level, null, models, NoAuth);
            Check(parsed.Model?.Id == "zai-org/GLM-5.1-FP8" && parsed.ThinkingLevel == level, "fallback level " + level);
        }
        Equal("zai-org/GLM-5.1-FP8:banana", ModelResolver.ResolveCliModel(null, "neuralwatt/zai-org/GLM-5.1-FP8:banana", null, models, NoAuth).Model?.Id, "invalid suffix kept");
        var explicitProvider = ModelResolver.ResolveCliModel("neuralwatt", "zai-org/GLM-5.1-FP8:high", null, models, NoAuth);
        Check(explicitProvider.Model?.Id == "zai-org/GLM-5.1-FP8" && explicitProvider.ThinkingLevel == "high", "explicit provider");
        var cliThinking = ModelResolver.ResolveCliModel(null, "neuralwatt/zai-org/GLM-5.1-FP8:high", "medium", models, NoAuth);
        Check(cliThinking.Model?.Id == "zai-org/GLM-5.1-FP8:high" && cliThinking.ThinkingLevel is null && cliThinking.Model.Reasoning, "explicit --thinking keeps the suffix");
    }

    private static void DefaultsExist()
    {
        Equal("gpt-5.5", ModelResolver.DefaultModelFor("openai"), "openai"); Equal("gpt-6.1-sol", ModelResolver.DefaultModelFor("openai-codex"), "codex");
        Equal("glm-5.3", ModelResolver.DefaultModelFor("zai"), "zai"); Equal("MiniMax-M2.7", ModelResolver.DefaultModelFor("minimax-cn"), "minimax-cn");
        Equal("gpt-oss-120b", ModelResolver.DefaultModelFor("cerebras"), "cerebras"); Equal("Ring-2.6-1T", ModelResolver.DefaultModelFor("ant-ling"), "ant-ling");
        Equal("zai/glm-5.1", ModelResolver.DefaultModelFor("vercel-ai-gateway"), "gateway"); Equal("grok-4.7", ModelResolver.DefaultModelFor("xai"), "xai");
        Equal("qwen3.8-max", ModelResolver.DefaultModelFor("qwen-token-plan-individual"), "qwen individual");
        foreach (var provider in BuiltinModelCatalog.ShardHashes.Keys)
        {
            var chat = BuiltinModelCatalog.Get(provider).Models.Where(model => model.Type == CatalogModelType.Chat).ToList();
            var id = ModelResolver.DefaultModelFor(provider);
            if (chat.Count == 0) { Check(id is null, provider + " has no chat models and should have no chat default"); continue; }
            Check(chat.Any(model => model.Id == id), $"{provider} default {id} should exist in its generated catalog");
        }
    }

    private static void InitialModel()
    {
        var ghost = ModelResolver.FindInitialModel("openrouter", "openrouter/openai/ghost-model", [], false, null, null, null, null,
            () => AllModels, (_, _) => null, NoAuth, () => []);
        Check(ghost.Model?.Provider == "openrouter" && ghost.Model.Id == "openai/ghost-model", "explicit provider custom id");
        var gateway = M("vercel-ai-gateway", "anthropic/claude-opus-4-6", "Claude Opus 4.6", reasoning: true);
        var available = ModelResolver.FindInitialModel(null, null, [], false, null, null, null, null, () => [], (_, _) => null, NoAuth, () => [gateway]);
        Equal("vercel-ai-gateway/anthropic/claude-opus-4-6", available.Model?.Reference, "first available"); Equal("medium", available.ThinkingLevel, "default level");
        var saved = M("deepseek", "deepseek-v4-flash", reasoning: true); var local = M("spark-two", "deepseek-v4-flash", reasoning: true);
        var unauthenticated = ModelResolver.FindInitialModel(null, null, [], false, "deepseek", "deepseek-v4-flash", null, null, () => [],
            (provider, id) => provider == "deepseek" && id == "deepseek-v4-flash" ? saved : null, provider => provider == "spark-two", () => [local]);
        Equal("spark-two/deepseek-v4-flash", unauthenticated.Model?.Reference, "unauthenticated saved default ignored");
        var defaults = ModelResolver.FindInitialModel(null, null, [], false, null, null, null, null, () => [], (_, _) => null, NoAuth,
            () => [M("groq", "zzz"), M("anthropic", "claude-opus-4-8"), M("groq", "openai/gpt-oss-120b")]);
        Equal("anthropic/claude-opus-4-8", defaults.Model?.Reference, "provider defaults in declaration order");
        var scoped = ModelResolver.FindInitialModel(null, null, [new(AllModels[1], null)], false, null, null, "low",
            new Dictionary<string, string> { ["openai/gpt-4o"] = "high" }, () => [], (_, _) => null, NoAuth, () => []);
        Check(scoped.Model?.Reference == "openai/gpt-4o" && scoped.ThinkingLevel == "high", "scoped first with per-model level");
        var continuing = ModelResolver.FindInitialModel(null, null, [new(AllModels[1], "low")], true, null, null, null, null, () => [], (_, _) => null, NoAuth, () => []);
        Check(continuing.Model is null, "scoped models skipped when continuing");
        Throws<ModelSelectionException>(() => ModelResolver.FindInitialModel("nope", "x", [], false, null, null, null, null, () => AllModels, (_, _) => null, NoAuth, () => []),
            "CLI error");
    }

    private static void RestoreModel()
    {
        Registry(out var registry, Env(("GROQ_API_KEY", "k")));
        var restored = ModelResolver.RestoreModelFromSession("groq", "openai/gpt-oss-120b", null, registry);
        Check(restored.Model?.Reference == "groq/openai/gpt-oss-120b" && restored.FallbackMessage is null, "restored");
        var missing = ModelResolver.RestoreModelFromSession("groq", "gone", null, registry);
        Equal("Could not restore model groq/gone (model no longer exists). Using groq/openai/gpt-oss-120b.", missing.FallbackMessage, "fallback to default");
        var noAuth = ModelResolver.RestoreModelFromSession("deepseek", "deepseek-v4-pro", restored.Model, registry);
        Equal("Could not restore model deepseek/deepseek-v4-pro (no auth configured). Using groq/openai/gpt-oss-120b.", noAuth.FallbackMessage, "current model fallback");
    }
}
