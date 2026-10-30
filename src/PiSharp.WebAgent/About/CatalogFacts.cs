// Decision 0011 (run W3, unit AP): Settings › About's model catalog numbers. What chats can use is IModelCatalog's: its providers, chat
// models, reasoning models and models that read images (the catalog the Routing area built, plus what other areas add to it, such as custom
// providers). With live providers, the About page also describes the built-in catalog behind it: PiSharp.ModelRuntime's embedded provider
// shards, each SHA-256 verified before it is parsed (BuiltinModelCatalog), with their chat, image and classifier models and the manifest's
// hash. That registry is credential-blind (no environment, no key, no file, no network), read once and kept. With --faux the catalog is the
// faux one and nothing of the built-in catalog is read.
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Models;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.About;

/// <summary>The catalog numbers of Settings › About.</summary>
/// <param name="Providers">Providers in <see cref="IModelCatalog"/>.</param>
/// <param name="ChatModels">Chat models in <see cref="IModelCatalog"/>.</param>
/// <param name="Reasoning">Of those, the models that reason (thinking).</param>
/// <param name="ImageInput">Of those, the models that read images.</param>
/// <param name="Hidden">Models hidden in Settings › Models.</param>
/// <param name="Builtin">The built-in catalog, or null with <c>--faux</c>.</param>
/// <param name="NotBuiltin">The ids of catalog providers that are not built in (custom providers; the faux provider).</param>
public sealed record CatalogStats(int Providers, int ChatModels, int Reasoning, int ImageInput, int Hidden, BuiltinCatalogStats? Builtin,
    IReadOnlyList<string> NotBuiltin);

/// <summary>PiSharp.ModelRuntime's embedded catalog.</summary>
/// <param name="Shards">The embedded provider files, each verified against its recorded SHA-256 before parsing.</param>
/// <param name="Providers">The providers they define.</param>
/// <param name="ChatModels">Their chat models.</param>
/// <param name="ImageModels">Their image models (not offered in chats).</param>
/// <param name="Classifiers">Their classifier models (not offered in chats).</param>
/// <param name="ManifestSha256">The SHA-256 of the upstream manifest the shard hashes come from.</param>
public sealed record BuiltinCatalogStats(int Shards, int Providers, int ChatModels, int ImageModels, int Classifiers, string ManifestSha256)
{
    /// <summary>The ids of the built-in providers.</summary>
    public IReadOnlySet<string> ProviderIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The manifest hash shortened for display, such as <c>a4c0a81f…81cfce9</c>.</summary>
    public string ShortManifest => ManifestSha256.Length > 16 ? ManifestSha256[..8] + "…" + ManifestSha256[^7..] : ManifestSha256;
}

/// <summary>Reads <see cref="CatalogStats"/>.</summary>
public static class CatalogFacts
{
    private static readonly Lazy<BuiltinCatalogStats> CachedBuiltin = new(ReadBuiltin);

    /// <summary>The numbers of <paramref name="catalog"/>, with the built-in catalog's unless <paramref name="options"/> is faux.</summary>
    public static CatalogStats Read(IModelCatalog catalog, ModelsSettings models, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(options);
        var all = catalog.Providers.SelectMany(provider => catalog.Models(provider.Id)).ToList();
        var builtin = options.Faux ? null : CachedBuiltin.Value;
        var notBuiltin = catalog.Providers.Select(provider => provider.Id)
            .Where(id => builtin is null || !builtin.ProviderIds.Contains(id)).ToList();
        return new CatalogStats(catalog.Providers.Count, all.Count, all.Count(model => model.Reasoning),
            all.Count(model => model.Input.Contains("image", StringComparer.Ordinal)), models.Hidden?.Count ?? 0, builtin, notBuiltin);
    }

    private static BuiltinCatalogStats ReadBuiltin()
    {
        var registry = ModelRegistry.Create(new ModelRegistryOptions());
        var entries = registry.GetAll();
        var providers = entries.Select(entry => entry.Provider).ToHashSet(StringComparer.Ordinal);
        return new BuiltinCatalogStats(BuiltinModelCatalog.ShardHashes.Count, providers.Count,
            entries.Count(entry => entry.Type == CatalogModelType.Chat), entries.Count(entry => entry.Type == CatalogModelType.Image),
            entries.Count(entry => entry.Type == CatalogModelType.Classifier), BuiltinModelCatalog.ManifestSha256)
        {
            ProviderIds = providers,
        };
    }
}
