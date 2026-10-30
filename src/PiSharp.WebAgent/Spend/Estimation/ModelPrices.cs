// Decision 0011 (run W3, S1): what one model costs, as the Spend Guard prices it. The base rates are the model catalog's (IModelCatalog's
// CatalogCost); price tiers (pi-ai's cost.tiers, such as Claude Haiku 5.5 above 100K input tokens or GPT-5.5 above 272K) come from the
// model's catalog JSON, which IDurableModels.GetModel carries for every model the catalog lists, read with PiSharp.AI's public ModelCost.
// A tier is chosen exactly as ModelCost.Calculate (pi-ai calculateCost) chooses it: the tier with the greatest threshold strictly below the
// request's input + cache-read + cache-write tokens prices the whole request, the first in source order among equal thresholds. A worst case
// moves that line 10% lower (the input is only estimated, so near a boundary the higher tier is assumed). Rates are compared and multiplied
// in decimal (SpendPrice), never through Calculate's binary64 sums, so a worst case is exact to the micro-USD. A model the catalog lists
// without a cost has an unknown price, unless the user entered one in Settings › Spend (SpendSettings.EnteredPrices, which never replace a
// catalog price); a cost of all zeros is a known price of $0 (free models of metered providers), except under a provider the catalog marks
// Unknown (a custom provider: zeros are only models.json's default there). IDurableModels is resolved on first use,
// not injected, so the guard (S3), which decorates IDurableModels and depends on the estimator, does not make a dependency cycle.
using System.Text.Json;
using PiSharp.AI.ModelOperations;
using PiSharp.Durable;
using PiSharp.WebAgent.Contracts;
using ChordObject = PiSharp.Durable.Chord.JsonObjectValue;

namespace PiSharp.WebAgent.Spend;

/// <summary>The rates of one model: its base price and its price tiers (from the catalog), or the price the user entered (no tiers).</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Model">The model id.</param>
/// <param name="Base">The base rates, for requests below every tier.</param>
/// <param name="Tiers">The price tiers in catalog order, each with <see cref="SpendPrice.TierInputTokensAbove"/> set; empty when there are
/// none.</param>
public sealed record ModelPricing(string Provider, string Model, SpendPrice Base, IReadOnlyList<SpendPrice> Tiers)
{
    /// <summary>How far below a tier's threshold a worst case already uses the tier: 10% (an input of 90,000 tokens is priced at a
    /// 100,000-token tier).</summary>
    public const double TierMargin = 0.10;

    /// <summary>Where the rates came from.</summary>
    public SpendPriceSource Source => Base.Source;

    /// <summary>Whether every rate of every tier is 0.</summary>
    public bool IsFree => Base.IsFree && Tiers.All(tier => tier.IsFree);

    /// <summary>The rates a request of <paramref name="inputTokens"/> input tokens (input + cache read + cache write) is billed at, as
    /// <c>ModelCost.Calculate</c> chooses them: the tier with the greatest threshold strictly below the input, else <see cref="Base"/>.</summary>
    public SpendPrice ActualRates(double inputTokens) => Select(inputTokens, worst: false);

    /// <summary>The rates a worst case of <paramref name="inputTokens"/> estimated input tokens uses: as <see cref="ActualRates"/>, except a
    /// tier already applies from 90% of its threshold (<see cref="TierMargin"/>), so near a boundary the higher tier is assumed.</summary>
    public SpendPrice WorstRates(double inputTokens) => Select(inputTokens, worst: true);

    /// <summary>What <paramref name="usage"/> costs at these rates, in micro-USD rounded to the nearest: the tier is chosen from its input,
    /// cache-read and cache-write tokens as pi-ai's calculateCost does. For a price the user entered (pi-ai reports $0 for a model without a
    /// catalog cost) or a provider that reported tokens without a cost.</summary>
    public long CostMicros(SpendUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return ActualRates(usage.InputTokens + usage.CacheReadTokens + usage.CacheWriteTokens).CostMicros(usage);
    }

    /// <summary>The pricing of a catalog model: base rates from <paramref name="cost"/> (else from <paramref name="declared"/>) and the
    /// tiers of <paramref name="declared"/> (the model JSON's <c>cost</c>); null when neither has a price or a base rate is not allowed. A
    /// tier with a threshold of -1 or below is left out (it never applies); a tier with a rate or threshold that is not allowed drops every
    /// tier (the base rates still apply).</summary>
    public static ModelPricing? FromCatalog(string provider, string model, CatalogCost? cost, ModelCost? declared)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var basePrice = cost is not null ? SpendPrice.From(cost)
            : declared is not null ? new SpendPrice(declared.Input, declared.Output, declared.CacheRead, declared.CacheWrite)
            : null;
        if (basePrice is null) return null;
        try { basePrice = basePrice.Normalize(); }
        catch (ArgumentException) { return null; }
        var tiers = new List<SpendPrice>();
        try
        {
            // calculateCost never applies a threshold of -1 or below, so such a tier is left out rather than refused.
            foreach (var tier in declared is null || declared.Tiers.IsDefault ? [] : declared.Tiers)
                if (tier.InputTokensAbove > -1)
                    tiers.Add(new SpendPrice(tier.Input, tier.Output, tier.CacheRead, tier.CacheWrite) { TierInputTokensAbove = tier.InputTokensAbove }.Normalize());
        }
        catch (ArgumentException) { tiers.Clear(); }
        return new ModelPricing(provider, model, basePrice, tiers);
    }

    private SpendPrice Select(double inputTokens, bool worst)
    {
        // calculateCost: matched starts at -1, so a threshold of -1 or below never applies; ">" keeps the first of equal thresholds. The
        // worst case's line is 90% of the threshold, compared as tokens x 10 >= threshold x 9 (exact for whole token counts).
        var tokens = double.IsFinite(inputTokens) ? Math.Max(0, inputTokens) : 0;
        var chosen = Base;
        var matched = -1d;
        foreach (var tier in Tiers)
        {
            if (tier.TierInputTokensAbove is not { } above) continue;
            var applies = worst ? tokens * 10 >= above * 9 : tokens > above;
            if (applies && above > matched) { chosen = tier; matched = above; }
        }
        return chosen;
    }
}

/// <summary>A model the catalog lists without a price (Settings › Spend › Unknown prices).</summary>
/// <param name="Model">The catalog model.</param>
/// <param name="Entered">The price the user entered for it, or null while it has none.</param>
public sealed record UnpricedModel(CatalogModel Model, EnteredPrice? Entered);

/// <summary>Finds the price of a model: the catalog's (with its tiers), else the one the user entered, else none. A singleton; safe to call
/// from many threads.</summary>
public sealed class ModelPrices
{
    private readonly IModelCatalog catalog;
    private readonly ISettingsStore settings;
    private readonly Func<IDurableModels?> models;

    /// <summary>Prices from <paramref name="catalog"/>, entered prices from <paramref name="settings"/>' <see cref="SpendSettings"/>, and price
    /// tiers from the model JSON of <paramref name="models"/> (resolved on each use; null or returning null: no tiers).</summary>
    public ModelPrices(IModelCatalog catalog, ISettingsStore settings, Func<IDurableModels?>? models = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        this.catalog = catalog;
        this.settings = settings;
        this.models = models ?? (() => null);
    }

    /// <summary>The price of <paramref name="model"/> of <paramref name="provider"/>: the catalog's base rates with the tiers its model JSON
    /// declares; for a model the catalog lists without a price, the price the user entered; for a model the catalog does not list, the cost
    /// in its model JSON. Null when the price is unknown.</summary>
    public ModelPricing? Find(string provider, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var listed = catalog.FindModel(provider, model);
        var declared = DeclaredCost(provider, model);
        if (listed is not null && Priced(catalog.FindProvider(provider), listed))
            return ModelPricing.FromCatalog(provider, model, listed.Cost, declared) ?? Entered(provider, model);
        if (listed is null && declared is not null && ModelPricing.FromCatalog(provider, model, null, declared) is { } fromJson) return fromJson;
        return Entered(provider, model);
    }

    /// <summary>Whether any model of <paramref name="provider"/> has a known price (in the catalog, or entered).</summary>
    public bool AnyPriced(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var listed = catalog.FindProvider(provider);
        return catalog.Models(provider).Any(model => Priced(listed, model))
            || settings.Get<SpendSettings>().EnteredPrices?.Any(price => price.Provider == provider) == true;
    }

    /// <summary>The models of <paramref name="provider"/> the catalog lists without a price, in catalog order, with any price the user
    /// entered.</summary>
    public IReadOnlyList<UnpricedModel> Unpriced(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var spend = settings.Get<SpendSettings>();
        var listed = catalog.FindProvider(provider);
        return [.. catalog.Models(provider).Where(model => !Priced(listed, model)).Select(model => new UnpricedModel(model, spend.PriceFor(provider, model.Id)))];
    }

    /// <summary>Whether the catalog prices <paramref name="model"/>: it has a cost, and under a provider the catalog marks
    /// <see cref="BillingKind.Unknown"/> (a custom provider without prices) that cost is not all zeros, which is only models.json's
    /// default. Elsewhere all zeros is a real $0 price.</summary>
    private static bool Priced(CatalogProvider? provider, CatalogModel model) =>
        model.Cost is { } cost && (provider?.Billing != BillingKind.Unknown || !SpendPrice.From(cost).IsFree);

    private ModelPricing? Entered(string provider, string model) =>
        settings.Get<SpendSettings>().PriceFor(provider, model) is { } entered ? new ModelPricing(provider, model, entered.ToPrice(), []) : null;

    /// <summary>The <c>cost</c> of the model's catalog JSON (<c>DurableModel.Wire</c>), or null when it has none or it cannot be read.</summary>
    private ModelCost? DeclaredCost(string provider, string model)
    {
        if (models() is not { } durable || durable.GetModel(provider, model)?.Wire is not ChordObject wire) return null;
        if (!wire.TryGetValue("cost", out var cost) || cost is not ChordObject) return null;
        try
        {
            using var document = JsonDocument.Parse(cost.ToJson());
            return ModelCost.FromJson(document.RootElement);
        }
        catch (Exception error) when (error is FormatException or JsonException) { return null; }
    }
}
