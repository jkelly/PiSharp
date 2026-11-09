using PiSharp.AI.Catalogs;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Models;

// The built-in catalog (W2 brief, WU2): PiSharp.ModelRuntime embeds every pi-ai 1.5.0 shard under the historical CLI resource names.
// The registry composes these verified shards and explicit reviewed additions without the CLI's models.json.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CatalogCases() =>
    [
        Case("catalog.every-embedded-shard-loads", CatalogEveryShard),
        Case("catalog.registry-composes-built-ins-without-models-json", CatalogRegistry),
        Case("catalog.auth-catalog-lists-built-in-providers", CatalogAuth),
    ];

    private const string ShardPrefix = "PiSharp.Cli.Models.";

    private static void CatalogEveryShard()
    {
        var resources = ModelRuntime.GetManifestResourceNames().Where(name => name.StartsWith(ShardPrefix, StringComparison.Ordinal) &&
            name.EndsWith(".json", StringComparison.Ordinal)).Select(name => name[ShardPrefix.Length..^".json".Length]).Order(StringComparer.Ordinal).ToList();
        Equal(BuiltinModelCatalog.ShardHashes.Count, resources.Count, "embedded shards: " + string.Join(",", resources));
        Check(resources.SequenceEqual(BuiltinModelCatalog.ShardHashes.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal), "embedded shards match the pinned hashes");
        foreach (var provider in resources)
        {
            Check(BuiltinModelCatalog.Has(provider), provider + " has no pinned hash");
            _ = BuiltinModelCatalog.ReadShard(provider); // hash-verified
            var catalog = BuiltinModelCatalog.Get(provider);
            Check(catalog.Models.Length > 0, provider + " has no models");
        }
        // Reviewed providers can be newer than the frozen package; every other provider still needs its original shard.
        var reviewedOnly = BuiltinProviders.All.Where(provider => !BuiltinModelCatalog.Has(provider.Id)).Select(provider => provider.Id).Order(StringComparer.Ordinal);
        Check(reviewedOnly.SequenceEqual(ReviewedModelCatalog.AdditionalProviders.Order(StringComparer.Ordinal), StringComparer.Ordinal),
            "only explicitly reviewed providers lack historical shards");
        foreach (var provider in ReviewedModelCatalog.AdditionalProviders)
        {
            Check(!resources.Contains(provider, StringComparer.Ordinal), provider + " must not invent a historical resource");
            var unavailable = Throws<BuiltinCatalogException>(() => BuiltinModelCatalog.Get(provider), provider + " has no historical shard");
            Equal("LiveCatalogUnavailable", unavailable.Code, provider + " frozen catalog failure");
            var catalog = ReviewedModelCatalog.Apply(provider, null);
            Check(catalog is not null && catalog.Models.Length > 0, provider + " has no reviewed models");
        }
        // CopilotCatalog reads the github-copilot shard from the same assembly.
        var copilot = BuiltinModelCatalog.Get("github-copilot").Models.First(model => model.Type == CatalogModelType.Chat);
        Check(CopilotCatalog.Contains(copilot.Id), "the Copilot catalog reads its shard");
    }

    private static void CatalogRegistry()
    {
        var refusing = new RefusingHandler();
        var registry = ModelRegistry.Create(new() { Environment = Env(), CreateCatalogClient = () => new HttpClient(refusing), FileExists = _ => false, Home = Path.GetTempPath() });
        Equal(null, registry.GetError(), "no models.json error");
        var ids = registry.GetProviderIds();
        Check(BuiltinProviders.All.Select(provider => provider.Id).SequenceEqual(ids, StringComparer.Ordinal), "providers in pi-ai order: " + string.Join(",", ids));
        foreach (var provider in BuiltinProviders.All)
        {
            var catalog = ReviewedModelCatalog.Apply(provider.Id, BuiltinModelCatalog.Has(provider.Id) ? BuiltinModelCatalog.Get(provider.Id) : null)
                ?? throw new InvalidOperationException(provider.Id + " has neither historical nor reviewed models");
            Check(registry.GetAllOfType(CatalogModelType.Chat, provider.Id).Count > 0 || catalog.Models.All(model => model.Type != CatalogModelType.Chat),
                provider.Id + " composes its chat models");
            if (!BuiltinModelCatalog.Has(provider.Id))
                foreach (var model in catalog.Models)
                    Check(registry.GetAllOfType(model.Type, provider.Id).Any(item => item.Id == model.Id), provider.Id + "/" + model.Id + " reviewed model composes without a historical shard");
        }
        var openai = registry.Find("openai", ModelResolver.DefaultModelFor("openai")!);
        Check(openai is { Provider: "openai" } && openai.Pinned is not null, "the pinned default OpenAI model");
        Equal(null, registry.CheckAuth("openai"), "nothing configured");
        Equal(0, refusing.Requests, "no catalog request");
    }

    private static void CatalogAuth()
    {
        var ids = ProviderAuthCatalog.Entries().Select(entry => entry.Id).ToList();
        Check(BuiltinProviders.All.Select(provider => provider.Id).All(ids.Contains), "every built-in provider has auth: " + string.Join(",", ids));
        Check(ProviderAuthCatalog.Find("openai-codex") is { OAuth: not null }, "Codex signs in with OAuth");
        Check(ProviderAuthCatalog.Find("github-copilot") is { OAuth: not null }, "Copilot signs in with OAuth");
        Check(ProviderAuthCatalog.LoginOptions("api_key").Any(option => option.Provider.Id == "openai"), "OpenAI takes an API key");
    }
}
