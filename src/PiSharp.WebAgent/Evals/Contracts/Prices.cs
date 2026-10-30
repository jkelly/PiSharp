// Decision 0011 (run W5, E0): catalog price snapshots and changes (mockup evals-drift: _PriceChanges, "Catalog price changes · last 30 days ·
// per million tokens"). Price Watch (E3) takes a snapshot of a provider's catalog costs whenever FrozenModelCatalog.ProviderJsonSha256
// changes ("openai.json changed"), and lists each rate that moved as a PriceChange. Rates are US dollars per million tokens, as the
// catalog declares them (CatalogCost). A model added to or removed from the catalog is not a price change, but a price appearing or going
// away for a listed model is. Tiered rates are not compared (the web agent's CatalogCost has none). What a change does to suites (worst
// case against the cap, paused schedules) is worked out by the scheduler (E4) and shown beside it; it is not stored here.
using System.Globalization;
using System.Text.Json.Serialization;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Evals;

/// <summary>One rate of a model's price. Sent as <c>input</c>, <c>output</c>, <c>cache-read</c>, <c>cache-write</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PriceField>))]
public enum PriceField
{
    /// <summary>Input tokens.</summary>
    [JsonStringEnumMemberName("input")] Input,

    /// <summary>Output tokens.</summary>
    [JsonStringEnumMemberName("output")] Output,

    /// <summary>Cached input tokens read.</summary>
    [JsonStringEnumMemberName("cache-read")] CacheRead,

    /// <summary>Input tokens written to the cache.</summary>
    [JsonStringEnumMemberName("cache-write")] CacheWrite,
}

/// <summary>A model's price in a snapshot.</summary>
/// <param name="Model">The model id.</param>
/// <param name="Cost">Its rates in US dollars per million tokens, or null when the catalog has none.</param>
public sealed record ModelPrice(string Model, CatalogCost? Cost)
{
    /// <summary>The rate of <paramref name="field"/>, or null when there is no price.</summary>
    public double? Rate(PriceField field) => Cost is null ? null : field switch
    {
        PriceField.Input => Cost.Input,
        PriceField.Output => Cost.Output,
        PriceField.CacheRead => Cost.CacheRead,
        PriceField.CacheWrite => Cost.CacheWrite,
        _ => null,
    };
}

/// <summary>A provider's catalog prices when its catalog file had one hash.</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="CatalogSha256">The provider catalog's <c>ProviderJsonSha256</c> (lower hex).</param>
/// <param name="Taken">When it was taken.</param>
/// <param name="Models">Its models' prices, by model id.</param>
public sealed record PriceSnapshot(string Provider, string CatalogSha256, DateTimeOffset Taken, IReadOnlyList<ModelPrice> Models)
{
    /// <summary>The snapshot's id in the store, or 0 before it is saved.</summary>
    public long Id { get; init; }

    /// <summary>The price of <paramref name="model"/>, or null when the snapshot does not list it.</summary>
    public ModelPrice? Price(string model) => Models.FirstOrDefault(price => string.Equals(price.Model, model, StringComparison.Ordinal));

    /// <summary>The rates that differ from <paramref name="before"/> to this snapshot (same provider), by model then field; empty when
    /// <paramref name="before"/> is null (the first snapshot is not a change). Only models in both snapshots count: a model added to or
    /// removed from the catalog is not a price change. A rate that goes between none and $0 is not a change either.</summary>
    public IReadOnlyList<PriceChange> ChangesSince(PriceSnapshot? before)
    {
        if (before is null) return [];
        if (!string.Equals(before.Provider, Provider, StringComparison.Ordinal)) throw new ArgumentException("Price snapshots of different providers.");
        var changes = new List<PriceChange>();
        var models = before.Models.Select(price => price.Model).Intersect(Models.Select(price => price.Model), StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var old = before.Price(model)!;
            var now = Price(model)!;
            foreach (var field in Enum.GetValues<PriceField>())
            {
                var from = old.Rate(field);
                var to = now.Rate(field);
                if (from == to || (from ?? 0) == 0 && (to ?? 0) == 0) continue;
                changes.Add(new PriceChange(Provider, model, field, from, to, Taken, before.CatalogSha256, CatalogSha256));
            }
        }
        return changes;
    }
}

/// <summary>One rate that changed between two snapshots of a provider's catalog.</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Model">The model id.</param>
/// <param name="Field">Which rate.</param>
/// <param name="Before">The old rate in US dollars per million tokens, or null when the model had no price.</param>
/// <param name="After">The new rate, or null when the model has no price now.</param>
/// <param name="Detected">When the new snapshot was taken.</param>
/// <param name="BeforeSha256">The old catalog hash.</param>
/// <param name="AfterSha256">The new catalog hash.</param>
public sealed record PriceChange(string Provider, string Model, PriceField Field, double? Before, double? After, DateTimeOffset Detected,
    string BeforeSha256, string AfterSha256)
{
    /// <summary>Whether the rate went up (or a price appeared).</summary>
    [JsonIgnore]
    public bool Rise => (Before, After) switch
    {
        (null, not null) => true,
        ({ } from, { } to) => to > from,
        _ => false,
    };

    /// <summary>How the page says it: <c>output $12.00 → $14.00</c>, <c>cache read $0.20 → $0.10</c>, <c>input none → $1.25</c>.</summary>
    [JsonIgnore]
    public string Label => $"{FieldLabel(Field)} {Money(Before)} → {Money(After)}";

    /// <summary>How the pages name a rate.</summary>
    public static string FieldLabel(PriceField field) => field switch
    {
        PriceField.CacheRead => "cache read",
        PriceField.CacheWrite => "cache write",
        PriceField.Output => "output",
        _ => "input",
    };

    private static string Money(double? rate) => rate is { } value ? "$" + value.ToString("0.00##", CultureInfo.InvariantCulture) : "none";
}
