// Decision 0011 (run W3, S0): the Spend Guard's settings section ("spend" in webagent.db; mockup settings-spend, _SpendReservations,
// _SpendUnknownPrices, _SpendAccounts): the warning threshold (80%), the default reservation output cap (8,192 tokens: requests that set no
// maxTokens reserve this many, not the model's maximum), what happens to a request whose price is unknown (Ask, by default and per
// provider), the prices the user entered for such models, and billing-mode overrides per provider or account. Caps are not here: they live
// in the ledger's tables. Nothing here is a secret.
using System.Text.Json.Serialization;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Spend;

/// <summary>What happens to a request whose price is unknown. Sent as <c>ask</c>, <c>block</c>, <c>allow</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UnknownPriceBehavior>))]
public enum UnknownPriceBehavior
{
    /// <summary>Ask before sending it (<see cref="SpendErrors.UnknownPrice"/>).</summary>
    [JsonStringEnumMemberName("ask")] Ask,

    /// <summary>Never send it (<see cref="SpendErrors.Blocked"/>).</summary>
    [JsonStringEnumMemberName("block")] Block,

    /// <summary>Send it and record its tokens without a cost.</summary>
    [JsonStringEnumMemberName("allow")] Allow,
}

/// <summary>The unknown-price behaviour of one provider.</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Behavior">What happens to its requests without a price.</param>
public sealed record UnknownPriceRule(string Provider, UnknownPriceBehavior Behavior);

/// <summary>A price the user entered for a model without one, in micro-USD per million tokens (so <c>$2.50</c> per million is 2,500,000).
/// The model then counts as metered.</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Model">The model id.</param>
/// <param name="InputMicrosPerMillion">The input price.</param>
/// <param name="OutputMicrosPerMillion">The output price.</param>
public sealed record EnteredPrice(string Provider, string Model, long InputMicrosPerMillion, long OutputMicrosPerMillion)
{
    /// <summary>The price as rates (<see cref="SpendPriceSource.Entered"/>).</summary>
    public SpendPrice ToPrice() =>
        new((double)SpendMoney.ToUsd(InputMicrosPerMillion), (double)SpendMoney.ToUsd(OutputMicrosPerMillion)) { Source = SpendPriceSource.Entered };
}

/// <summary>The user's billing mode for a provider, or for one account of it (an account's override wins over its provider's).</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Account">The account id, or null for every account of <paramref name="Provider"/>.</param>
/// <param name="Mode">The billing mode to use.</param>
public sealed record BillingOverride(string Provider, string? Account, BillingKind Mode);

/// <summary>The Spend Guard's settings (Settings › Spend).</summary>
/// <param name="WarnPercent">A cap turns amber at this share of its limit, 1 to 100; default 80.</param>
/// <param name="ReservationOutputTokens">The output tokens a request without <c>maxTokens</c> reserves, 1 to 10,000,000; default 8,192.</param>
/// <param name="UnknownPrice">What happens to a request without a price, unless its provider has a rule; default Ask.</param>
/// <param name="UnknownPriceRules">Per-provider unknown-price behaviour, one per provider.</param>
/// <param name="EnteredPrices">Prices the user entered, one per provider and model.</param>
/// <param name="BillingOverrides">Billing-mode overrides, one per provider and account.</param>
public sealed record SpendSettings(int WarnPercent = SpendSettings.DefaultWarnPercent, int ReservationOutputTokens = SpendSettings.DefaultReservationOutputTokens,
    UnknownPriceBehavior UnknownPrice = UnknownPriceBehavior.Ask, IReadOnlyList<UnknownPriceRule>? UnknownPriceRules = null,
    IReadOnlyList<EnteredPrice>? EnteredPrices = null, IReadOnlyList<BillingOverride>? BillingOverrides = null) : ISettingsSection<SpendSettings>
{
    /// <summary>The default warning threshold, in percent.</summary>
    public const int DefaultWarnPercent = 80;

    /// <summary>The default reservation output cap, in tokens.</summary>
    public const int DefaultReservationOutputTokens = 8192;

    /// <summary>The largest reservation output cap accepted.</summary>
    public const int MaxReservationOutputTokens = 10_000_000;

    /// <inheritdoc/>
    public static string SectionName => "spend";

    /// <inheritdoc/>
    public static SpendSettings CreateDefault() => new(DefaultWarnPercent, DefaultReservationOutputTokens, UnknownPriceBehavior.Ask, [], [], []);

    /// <summary>The unknown-price behaviour of <paramref name="provider"/>: its rule, else <see cref="UnknownPrice"/>.</summary>
    public UnknownPriceBehavior UnknownPriceFor(string provider) =>
        UnknownPriceRules?.FirstOrDefault(rule => rule.Provider == provider)?.Behavior ?? UnknownPrice;

    /// <summary>The price the user entered for <paramref name="model"/> of <paramref name="provider"/>, or null.</summary>
    public EnteredPrice? PriceFor(string provider, string model) =>
        EnteredPrices?.FirstOrDefault(price => price.Provider == provider && price.Model == model);

    /// <summary>The override for <paramref name="account"/> of <paramref name="provider"/>, else the provider's, else null.</summary>
    public BillingOverride? OverrideFor(string provider, string? account) =>
        (account is null ? null : BillingOverrides?.FirstOrDefault(rule => rule.Provider == provider && rule.Account == account))
        ?? BillingOverrides?.FirstOrDefault(rule => rule.Provider == provider && rule.Account is null);

    /// <inheritdoc/>
    public SpendSettings Normalize()
    {
        if (WarnPercent is < 1 or > 100) throw new ArgumentException("The warning threshold is a share of the limit from 1% to 100%.");
        if (ReservationOutputTokens is < 1 or > MaxReservationOutputTokens)
            throw new ArgumentException("The reservation output cap is 1 to 10,000,000 tokens.");
        if (!Enum.IsDefined(UnknownPrice)) throw new ArgumentException($"Unknown unknown-price behaviour {(int)UnknownPrice}.");
        // A later entry for the same provider (and model or account) replaces an earlier one, so a page can append a change.
        var rules = new List<UnknownPriceRule>();
        foreach (var rule in UnknownPriceRules ?? [])
        {
            if (rule is null) throw new ArgumentException("An unknown-price rule is missing.");
            var provider = Required(rule.Provider, "An unknown-price rule needs a provider.");
            if (!Enum.IsDefined(rule.Behavior)) throw new ArgumentException($"Unknown unknown-price behaviour {(int)rule.Behavior} for {provider}.");
            rules.RemoveAll(existing => existing.Provider == provider);
            rules.Add(new UnknownPriceRule(provider, rule.Behavior));
        }
        var prices = new List<EnteredPrice>();
        foreach (var price in EnteredPrices ?? [])
        {
            if (price is null) throw new ArgumentException("An entered price is missing.");
            var provider = Required(price.Provider, "An entered price needs a provider.");
            var model = Required(price.Model, "An entered price needs a model.");
            if (price.InputMicrosPerMillion < 0 || price.OutputMicrosPerMillion < 0) throw new ArgumentException($"The price of {provider}/{model} can't be negative.");
            if (price.InputMicrosPerMillion > SpendMoney.MaxMicros || price.OutputMicrosPerMillion > SpendMoney.MaxMicros)
                throw new ArgumentException($"The price of {provider}/{model} can be at most {SpendMoney.Format(SpendMoney.MaxMicros)} per million tokens.");
            prices.RemoveAll(existing => existing.Provider == provider && existing.Model == model);
            prices.Add(price with { Provider = provider, Model = model });
        }
        var overrides = new List<BillingOverride>();
        foreach (var rule in BillingOverrides ?? [])
        {
            if (rule is null) throw new ArgumentException("A billing override is missing.");
            var provider = Required(rule.Provider, "A billing override needs a provider.");
            var account = string.IsNullOrWhiteSpace(rule.Account) ? null : rule.Account.Trim();
            if (!Enum.IsDefined(rule.Mode)) throw new ArgumentException($"Unknown billing mode {(int)rule.Mode} for {provider}.");
            overrides.RemoveAll(existing => existing.Provider == provider && existing.Account == account);
            overrides.Add(new BillingOverride(provider, account, rule.Mode));
        }
        return new(WarnPercent, ReservationOutputTokens, UnknownPrice, rules, prices, overrides);
    }

    private static string Required(string? value, string message) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(message) : value.Trim();
}
