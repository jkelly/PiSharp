// Decision 0011 (run W5, E3): where Price Watch reads catalog prices. A snapshot is one provider's model prices under one catalog hash
// (PriceSnapshot). For a built-in provider whose prices are exactly its shard's, the hash is the shard's FrozenModelCatalog.ProviderJsonSha256
// (so a new app version's openai.json is "openai.json changed"). When models.json overrides a built-in price, or for a custom or faux
// provider, there is no shard to name, so the hash is the SHA-256 of the prices themselves (PriceSnapshots.Sha256: the provider, then each
// model by id with its four rates), which changes exactly when a price does. Either way a snapshot is taken whenever the hash changes, and
// only then. The prices are what the app charges by: IModelCatalog's CatalogCost (the routed catalog, models.json included); a model listed
// without a price is listed with none.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Models;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Evals.Drift;

/// <summary>Where Price Watch reads the providers' current prices.</summary>
public interface IPriceSource
{
    /// <summary>One snapshot per provider of its current prices, taken at <paramref name="now"/> (not saved).</summary>
    IReadOnlyList<PriceSnapshot> Snapshots(DateTimeOffset now);
}

/// <summary>Price snapshots from catalogs.</summary>
public static class PriceSnapshots
{
    /// <summary>The snapshot of a provider catalog file: its chat models' costs, under its <see cref="FrozenModelCatalog.ProviderJsonSha256"/>.</summary>
    public static PriceSnapshot From(FrozenModelCatalog catalog, DateTimeOffset taken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var models = catalog.Models.Where(model => model.Type == CatalogModelType.Chat)
            .Select(model => new ModelPrice(model.Id, CostOf(model)))
            .OrderBy(price => price.Model, StringComparer.Ordinal).ToList();
        return new PriceSnapshot(catalog.Provider, catalog.ProviderJsonSha256, taken, models);
    }

    /// <summary>The SHA-256 (lower hex) of <paramref name="provider"/>'s prices: the provider id, then each model by id with its input,
    /// output, cache-read and cache-write rates (<c>-</c> for none), one per line.</summary>
    public static string Sha256(string provider, IEnumerable<ModelPrice> models)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(models);
        var text = new StringBuilder("pisharp-prices\n").Append(provider).Append('\n');
        foreach (var price in models.OrderBy(price => price.Model, StringComparer.Ordinal))
        {
            text.Append(price.Model);
            foreach (var field in Enum.GetValues<PriceField>())
                text.Append('\t').Append(price.Rate(field) is { } rate ? rate.ToString("R", CultureInfo.InvariantCulture) : "-");
            text.Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>The file a snapshot's hash names, for the drift page's "openai.json changed": the provider's built-in catalog file
    /// (<c>openai.json</c>) when the hash is a shard's, else <c>models.json</c> (the hash of the prices themselves: an override in models.json,
    /// or a provider models.json adds).</summary>
    public static string FileName(PriceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return string.Equals(Sha256(snapshot.Provider, snapshot.Models), snapshot.CatalogSha256, StringComparison.OrdinalIgnoreCase)
            ? "models.json"
            : snapshot.Provider + ".json";
    }

    /// <summary>A catalog row's cost (input, output, cacheRead, cacheWrite), or null when it cannot be read.</summary>
    internal static CatalogCost? CostOf(FrozenCatalogModel model)
    {
        var cost = model.Cost.Value;
        if (cost.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        double Rate(string name) => cost.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : 0;
        return new CatalogCost(Rate("input"), Rate("output"), Rate("cacheRead"), Rate("cacheWrite"));
    }
}

/// <summary>Prices from the app's <see cref="IModelCatalog"/> (built-in shards, models.json, custom providers, or the faux catalog). A
/// singleton; safe to call from many threads.</summary>
/// <param name="catalog">The catalog.</param>
public sealed class CatalogPriceSource(IModelCatalog catalog) : IPriceSource
{
    /// <inheritdoc/>
    public IReadOnlyList<PriceSnapshot> Snapshots(DateTimeOffset now)
    {
        var snapshots = new List<PriceSnapshot>();
        foreach (var provider in catalog.Providers)
        {
            var models = catalog.Models(provider.Id).Select(model => new ModelPrice(model.Id, model.Cost))
                .GroupBy(price => price.Model, StringComparer.Ordinal).Select(group => group.First())
                .OrderBy(price => price.Model, StringComparer.Ordinal).ToList();
            if (models.Count == 0) continue;
            snapshots.Add(new PriceSnapshot(provider.Id, ShardHash(provider.Id, models) ?? PriceSnapshots.Sha256(provider.Id, models), now, models));
        }
        return snapshots;
    }

    /// <summary>The built-in shard's <see cref="FrozenModelCatalog.ProviderJsonSha256"/> when <paramref name="provider"/> has one and every
    /// listed price is the shard's own; null otherwise (an override, a custom provider, or a shard that cannot be read).</summary>
    internal static string? ShardHash(string provider, IReadOnlyList<ModelPrice> models)
    {
        if (!BuiltinModelCatalog.Has(provider)) return null;
        FrozenModelCatalog shard;
        try { shard = BuiltinModelCatalog.Get(provider); }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
        foreach (var price in models)
        {
            if (!shard.TryGetModel(CatalogModelType.Chat, price.Model, out var row)) return null;
            if (!Equal(PriceSnapshots.CostOf(row), price.Cost)) return null;
        }
        return shard.ProviderJsonSha256;
    }

    private static bool Equal(CatalogCost? first, CatalogCost? second) => first is null || second is null
        ? first is null && second is null
        : first.Input == second.Input && first.Output == second.Output && first.CacheRead == second.CacheRead && first.CacheWrite == second.CacheWrite;
}
