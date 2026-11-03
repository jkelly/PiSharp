using PiSharp.Cli.Models;

// Pi 1.5.0 sync, cli rows (f9a26365568127461c47a07a37dcb42c140a3ac5): core/model-resolver.ts resolveModelReference (one provider
// selection policy, #10913; provider ids containing "/", #11397), resolveModelScopeFromModels and resolveCliModel. The reference table,
// scope and CLI cases follow test/model-resolver.test.ts at v1.5.0; their expectations are authored from it, never captured from an
// upstream run.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150CliCases() =>
    [
        ("sync150.model-references-never-follow-catalog-order", Sync(ModelReferenceTable)),
        ("sync150.model-scope-adds-every-provider-of-an-ambiguous-pattern", Sync(ModelScopeAmbiguity)),
        ("sync150.cli-model-duplicate-raw-ids-behind-a-provider-prefix", Sync(CliDuplicateRawIds)),
    ];

    private static RegistryModel Ref(string provider, string id) => M(provider, id, api: "openai-completions", reasoning: true);

    private static readonly RegistryModel[] ReferenceCatalog =
    [
        Ref("anthropic", "claude-sonnet-4-5"),
        Ref("anthropic", "claude-sonnet-4-5-20250929"),
        Ref("anthropic", "claude-opus-4-8"),
        Ref("anthropic", "exact"),
        // Fuzzy-matches "shared"; an ambiguous exact "shared" must not fall through to it.
        Ref("anthropic", "shared-pro"),
        Ref("openai", "gpt-4o"),
        Ref("openrouter", "qwen/qwen3-coder:exacto"),
        Ref("openrouter", "openai/gpt-4o:extended"),
        Ref("gw-a", "shared"),
        Ref("gw-a", "anthropic/future"),
        Ref("gw-a", "anthropic/exact"),
        Ref("gw-a", "anthropic/claude-opus-4.8"),
        Ref("gw-a", "anthropic/claude-opus"),
        Ref("gw-b", "shared"),
        Ref("gw-b", "anthropic/future"),
        Ref("gw-b", "claude-opus-4-8"),
        Ref("gw-b", "gw-b/free"),
        Ref("gw-b", "zz:free"),
        // URL-derived provider id containing "/" (#11394)
        Ref("llama=http://host:10800", "unsloth/qwen-gguf:BF16"),
    ];

    /// <summary>A reference case: <paramref name="Expected"/> is "provider/id", "ambiguous:a,b|usableCount" or "not-found".</summary>
    private sealed record ReferenceCase(string Name, string Text, string Expected, string[]? Usable = null, string? Provider = null,
        ThinkingSuffixMode ThinkingSuffix = ThinkingSuffixMode.Strict, bool Fuzzy = true, string? ThinkingLevel = null, string? Warning = null);

    private static readonly ReferenceCase[] ReferenceCases =
    [
        // Reference syntax
        new("exact id", "claude-sonnet-4-5", "anthropic/claude-sonnet-4-5"),
        new("exact id is case-insensitive", "GPT-4O", "openai/gpt-4o"),
        new("fuzzy prefers aliases over dated ids", "sonnet", "anthropic/claude-sonnet-4-5"),
        new("thinking suffix", "sonnet:high", "anthropic/claude-sonnet-4-5", ThinkingLevel: "high"),
        new("colon in a literal id is not a thinking suffix", "qwen/qwen3-coder:exacto", "openrouter/qwen/qwen3-coder:exacto"),
        new("provider prefix, colon id, and thinking suffix", "openrouter/qwen/qwen3-coder:exacto:high", "openrouter/qwen/qwen3-coder:exacto", ThinkingLevel: "high"),
        new("provider id containing a slash", "llama=http://host:10800/unsloth/qwen-gguf:BF16", "llama=http://host:10800/unsloth/qwen-gguf:BF16"),
        new("provider id containing a slash, with thinking suffix", "llama=http://host:10800/unsloth/qwen-gguf:BF16:high",
            "llama=http://host:10800/unsloth/qwen-gguf:BF16", ThinkingLevel: "high"),
        new("raw id wins when the provider prefix names no matching model", "openai/gpt-4o:extended", "openrouter/openai/gpt-4o:extended"),
        new("fuzzy searches the prefixed provider first", "openrouter/qwen", "openrouter/qwen/qwen3-coder:exacto"),
        new("strict keeps an invalid suffix in the id", "gpt-4o:random", "not-found"),
        new("lenient drops an invalid suffix with a warning", "gpt-4o:random", "openai/gpt-4o", ThinkingSuffix: ThinkingSuffixMode.Lenient,
            Warning: "Invalid thinking level \"random\""),
        new("lenient invalid outer suffix discards the inner thinking level", "qwen/qwen3-coder:exacto:high:random", "openrouter/qwen/qwen3-coder:exacto",
            ThinkingSuffix: ThinkingSuffixMode.Lenient, Warning: "Invalid thinking level \"random\""),
        new("none keeps the suffix in the id", "gpt-4o:high", "not-found", ThinkingSuffix: ThinkingSuffixMode.None),
        new("exact-only skips fuzzy matches", "sonnet", "not-found", Fuzzy: false),
        new("empty text", "  ", "not-found"),

        // Exact matches across providers (#7327)
        new("sole usable provider wins", "shared", "gw-b/shared", Usable: ["gw-b"]),
        new("no usable provider is ambiguous instead of falling through to fuzzy", "shared", "ambiguous:gw-a/shared,gw-b/shared|0"),
        new("several usable providers are ambiguous", "shared", "ambiguous:gw-a/shared,gw-b/shared|2", Usable: ["gw-a", "gw-b"]),
        new("thinking suffix does not change provider selection", "shared:high", "gw-b/shared", Usable: ["gw-b"], ThinkingLevel: "high"),
        new("thinking suffix does not bypass ambiguity", "shared:high", "ambiguous:gw-a/shared,gw-b/shared|2", Usable: ["gw-a", "gw-b"]),
        // #10160
        new("provider prefix without a direct model selects the usable raw id", "anthropic/future", "gw-b/anthropic/future", Usable: ["gw-b"]),
        // #5823
        new("usable qualified match beats a usable raw id", "anthropic/exact", "anthropic/exact", Usable: ["anthropic", "gw-a"]),
        // #5643
        new("usable raw id beats an unusable qualified match", "anthropic/exact", "gw-a/anthropic/exact", Usable: ["gw-a"]),
        new("unusable qualified match is kept when nothing is usable", "anthropic/exact", "anthropic/exact"),
        // Like `mistral/codestral` -> mistral/codestral-latest despite gateway id `mistral/codestral`
        new("named provider's usable fuzzy match beats a raw id elsewhere", "anthropic/claude-opus", "anthropic/claude-opus-4-8", Usable: ["anthropic"]),
        new("usable raw id beats the named provider's unusable fuzzy match", "anthropic/claude-opus", "gw-a/anthropic/claude-opus", Usable: ["gw-a"]),
        new("named provider's fuzzy match is kept when nothing is usable", "anthropic/claude-opus", "anthropic/claude-opus-4-8"),
        new("named provider's literal id beats its fuzzy matches", "gw-b/free", "gw-b/gw-b/free"),

        // Fuzzy matches across providers
        new("fuzzy prefers usable providers", "opus", "gw-a/anthropic/claude-opus-4.8", Usable: ["gw-a"]),
        new("fuzzy id tie across usable providers is ambiguous", "opus", "ambiguous:anthropic/claude-opus-4-8,gw-b/claude-opus-4-8|2",
            Usable: ["anthropic", "gw-a", "gw-b"]),
        new("fuzzy id tie resolves to the sole usable provider", "opus", "anthropic/claude-opus-4-8", Usable: ["anthropic"]),

        // Explicit provider constraint
        new("explicit provider ignores usable providers elsewhere", "shared", "gw-a/shared", Usable: ["gw-b"], Provider: "gw-a"),
        new("explicit provider accepts its own prefix", "GW-B/shared", "gw-b/shared", Provider: "gw-b"),
        new("explicit provider allows fuzzy matches", "4o", "openai/gpt-4o", Provider: "openai"),
        new("explicit provider never leaves the provider", "gpt-4o", "not-found", Provider: "gw-a"),
    ];

    // resolveModelReference: every case, against the catalog and its reverse (catalog order must never decide the result).
    private static void ModelReferenceTable()
    {
        foreach (var test in ReferenceCases)
        {
            var usable = new HashSet<string>(test.Usable ?? [], StringComparer.Ordinal);
            var request = new ModelReferenceRequest(test.Text, test.Provider, test.ThinkingSuffix, test.Fuzzy);
            foreach (var models in new[] { ReferenceCatalog, ReferenceCatalog.Reverse().ToArray() })
            {
                var result = ModelResolver.ResolveModelReference(request, models, usable.Contains);
                var what = test.Name + (ReferenceEquals(models, ReferenceCatalog) ? "" : " (reversed)");
                if (test.Expected == "not-found") { Equal(ModelReferenceStatus.NotFound, result.Status, what); continue; }
                if (test.Expected.StartsWith("ambiguous:", StringComparison.Ordinal))
                {
                    Equal(ModelReferenceStatus.Ambiguous, result.Status, what);
                    var (candidates, count) = (test.Expected["ambiguous:".Length..].Split('|')[0], test.Expected.Split('|')[1]);
                    Equal(candidates, string.Join(",", result.Candidates.Select(model => model.Reference).Order(StringComparer.Ordinal)), what + " candidates");
                    Equal(int.Parse(count, System.Globalization.CultureInfo.InvariantCulture), result.UsableCount, what + " usable count");
                    continue;
                }
                Equal(ModelReferenceStatus.Resolved, result.Status, what);
                Equal(test.Expected, result.Model?.Reference, what + " model");
                Equal(test.ThinkingLevel, result.ThinkingLevel, what + " thinking level");
                if (test.Warning is null) Equal<string?>(null, result.Warning, what + " warning");
                else Check(result.Warning?.Contains(test.Warning, StringComparison.Ordinal) == true, what + " warning: " + result.Warning);
            }
        }
    }

    // resolveModelScopeWithDiagnostics: a pattern matching the same id on several providers adds each of them (every scoped model is
    // usable, so credentials cannot choose) and warns once.
    private static void ModelScopeAmbiguity()
    {
        RegistryModel[] models = [.. AllModels, M("azure", "gpt-4o", "GPT-4o", input: ["text", "image"], maxTokens: 4096),
            M("openai-codex", "gpt-4o-mini", "GPT-4o", input: ["text", "image"], maxTokens: 4096)];
        foreach (var (pattern, level) in new[] { ("gpt-4o:high", "high"), ("GPT-4o", (string?)null) })
        {
            var (scoped, diagnostics) = ModelResolver.ResolveModelScope([pattern], models);
            Names(["azure/gpt-4o|" + level, "openai/gpt-4o|" + level], scoped.Select(entry => entry.Model.Reference + "|" + entry.ThinkingLevel).Order(StringComparer.Ordinal),
                pattern + " scoped");
            Names([$"ambiguous|Pattern \"{pattern}\" matches several providers: azure, openai. Use provider/id to choose one.|{pattern}"],
                diagnostics.Select(diagnostic => diagnostic.Code + "|" + diagnostic.Message + "|" + diagnostic.Pattern), pattern + " diagnostics");
        }
        // A glob whose text is an exact but ambiguous reference adds both too.
        var glob = ModelResolver.ResolveModelScope(["gpt-4o[x]"], [M("gw-a", "gpt-4o[x]"), M("gw-b", "gpt-4o[x]")]);
        Names(["gw-a/gpt-4o[x]", "gw-b/gpt-4o[x]"], glob.Scoped.Select(entry => entry.Model.Reference).Order(StringComparer.Ordinal), "ambiguous exact glob");
        Equal("ambiguous", glob.Diagnostics.Single().Code, "ambiguous exact glob diagnostic");
    }

    // resolveCliModel (#10160): the same raw id behind a known provider prefix on two gateways selects the authenticated one, with or
    // without a thinking suffix; with no credentials it is ambiguous; with --api-key stored credentials do not choose.
    private static void CliDuplicateRawIds()
    {
        RegistryModel[] models = [M("anthropic", "claude-sonnet-4-5", reasoning: true), M("openrouter", "anthropic/claude-opus-5.5", reasoning: true),
            M("vercel-ai-gateway", "anthropic/claude-opus-5.5", reasoning: true)];
        foreach (var (cliModel, level) in new[] { ("anthropic/claude-opus-5.5", (string?)null), ("anthropic/claude-opus-5.5:high", "high") })
        {
            var result = ModelResolver.ResolveCliModel(null, cliModel, null, models, provider => provider == "vercel-ai-gateway");
            Check(result.Error is null && result.Model?.Provider == "vercel-ai-gateway", cliModel + ": " + result.Error);
            Equal(level, result.ThinkingLevel, cliModel + " level");
        }
        var none = ModelResolver.ResolveCliModel(null, "anthropic/claude-opus-5.5", null, models, NoAuth);
        Check(none.Model is null && none.Error!.Contains("openrouter/anthropic/claude-opus-5.5, vercel-ai-gateway/anthropic/claude-opus-5.5", StringComparison.Ordinal) &&
            none.Error.Contains("No matching provider is authenticated. Use --provider or provider/model.", StringComparison.Ordinal), "ambiguous: " + none.Error);
        var inline = ModelResolver.ResolveCliModel(null, "anthropic/claude-opus-5.5", null, models, provider => provider == "vercel-ai-gateway", inlineApiKey: true);
        Check(inline.Model is null && inline.Error!.Contains("--api-key does not choose a provider.", StringComparison.Ordinal), "--api-key: " + inline.Error);
        // A provider id with "/" (#11397) takes the custom-model fallback for an unknown id under it.
        var llama = ModelResolver.ResolveCliModel(null, "llama=http://host:10800/other-model:low", null, ReferenceCatalog, NoAuth);
        Check(llama.Error is null && llama.Model?.Reference == "llama=http://host:10800/other-model" && llama.ThinkingLevel == "low", "slash provider fallback: " + llama.Error);
        Equal("Model \"other-model\" not found for provider \"llama=http://host:10800\". Using custom model id.", llama.Warning, "slash provider fallback warning");
    }
}
