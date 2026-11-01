// Decision 0011 (run W3, S0): what a request may cost before it is sent, and how an account is billed: the contracts of the Estimation unit
// (CostEstimator, BillingModes) that the Ledger, Guard, pages and chat code against. The worst case is input tokens x input rate + output
// tokens x output rate (SpendPrice.WorstMicros), rounded up; how the tokens and rates are chosen (UTF-8 bytes / 3 x 1.1, the higher tier near
// a boundary, the higher of the input and cache-write rates, maxTokens or the reservation cap or the model's maximum) is the estimator's.
// "Typical" is shown, never reserved. A subscription is estimated at list price but never counts toward money caps; free costs nothing;
// an unknown price has no worst case at all (null), so it cannot be checked against caps and the provider's unknown-price behaviour applies.
using System.Text.Json.Serialization;
using PiSharp.Contracts;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Spend;

/// <summary>Where a price came from. Sent as <c>catalog</c>, <c>entered</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SpendPriceSource>))]
public enum SpendPriceSource
{
    /// <summary>The model catalog (<c>CatalogCost</c>, or a price tier of the model's <c>ModelCost</c>).</summary>
    [JsonStringEnumMemberName("catalog")] Catalog,

    /// <summary>A price the user entered in Settings › Spend (<see cref="EnteredPrice"/>).</summary>
    [JsonStringEnumMemberName("entered")] Entered,
}

/// <summary>A model's rates, in US dollars per million tokens (which is micro-USD per token).</summary>
/// <param name="Input">Input tokens.</param>
/// <param name="Output">Output tokens.</param>
/// <param name="CacheRead">Cached input tokens read.</param>
/// <param name="CacheWrite">Input tokens written to the cache.</param>
public sealed record SpendPrice(double Input, double Output, double CacheRead = 0, double CacheWrite = 0)
{
    /// <summary>Where the rates came from.</summary>
    public SpendPriceSource Source { get; init; } = SpendPriceSource.Catalog;

    /// <summary>When the rates are a price tier's: the tier's <c>inputTokensAbove</c>; null for the base rates.</summary>
    public double? TierInputTokensAbove { get; init; }

    /// <summary>The input rate of a worst case: the higher of <see cref="Input"/> and <see cref="CacheWrite"/> (any input may be written to the
    /// cache).</summary>
    [JsonIgnore]
    public double WorstInput => Math.Max(Input, CacheWrite);

    /// <summary>Whether every rate is 0.</summary>
    [JsonIgnore]
    public bool IsFree => Input == 0 && Output == 0 && CacheRead == 0 && CacheWrite == 0;

    /// <summary>The rates of <paramref name="cost"/>.</summary>
    public static SpendPrice From(CatalogCost cost, SpendPriceSource source = SpendPriceSource.Catalog)
    {
        ArgumentNullException.ThrowIfNull(cost);
        return new SpendPrice(cost.Input, cost.Output, cost.CacheRead, cost.CacheWrite) { Source = source };
    }

    /// <summary>The same rates as a catalog cost.</summary>
    public CatalogCost ToCatalogCost() => new(Input, Output, CacheRead, CacheWrite);

    /// <summary>The same price, checked.</summary>
    /// <exception cref="ArgumentException">A rate is negative or not a finite number.</exception>
    public SpendPrice Normalize()
    {
        foreach (var (name, rate) in new[] { ("input", Input), ("output", Output), ("cache read", CacheRead), ("cache write", CacheWrite) })
            if (!double.IsFinite(rate) || rate < 0) throw new ArgumentException($"The {name} price must be $0 or more per million tokens.");
        if (TierInputTokensAbove is { } above && (!double.IsFinite(above) || above < 0)) throw new ArgumentException("A price tier's threshold must be 0 or more tokens.");
        if (!Enum.IsDefined(Source)) throw new ArgumentException($"Unknown price source {(int)Source}.");
        return this;
    }

    /// <summary>The worst-case cost of <paramref name="inputTokens"/> input tokens at <see cref="WorstInput"/>, in micro-USD, rounded up.</summary>
    public long InputMicros(double inputTokens) => SpendMoney.Ceiling(SpendMoney.Cost(inputTokens, WorstInput));

    /// <summary>The worst-case cost of <paramref name="outputTokens"/> output tokens, in micro-USD, rounded up.</summary>
    public long OutputMicros(double outputTokens) => SpendMoney.Ceiling(SpendMoney.Cost(outputTokens, Output));

    /// <summary>The worst case of a request, in micro-USD, rounded up once: <paramref name="inputTokens"/> x <see cref="WorstInput"/> +
    /// <paramref name="outputTokens"/> x <see cref="Output"/>. For example 12,000 x $2.00/M + 8,192 x $10.00/M = 105,920 micro-USD
    /// ($0.1059).</summary>
    public long WorstMicros(double inputTokens, double outputTokens) =>
        SpendMoney.Ceiling(SpendMoney.Cost(inputTokens, WorstInput) + SpendMoney.Cost(outputTokens, Output));

    /// <summary>What <paramref name="usage"/> costs at these rates, in micro-USD, rounded to the nearest (an entered price, or a provider that
    /// reported tokens without a cost).</summary>
    public long CostMicros(SpendUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return SpendMoney.Round(SpendMoney.Cost(usage.InputTokens, Input) + SpendMoney.Cost(usage.OutputTokens, Output) +
            SpendMoney.Cost(usage.CacheReadTokens, CacheRead) + SpendMoney.Cost(usage.CacheWriteTokens, CacheWrite));
    }

    /// <summary>What reading <paramref name="cacheReadTokens"/> from the cache saved against paying the input rate, in micro-USD (the cache
    /// meter: cache-read tokens x (input rate - cache-read rate)); never below 0.</summary>
    public long CacheSavedMicros(double cacheReadTokens) => Math.Max(0, SpendMoney.Round(SpendMoney.Cost(cacheReadTokens, Input - CacheRead)));

    /// <summary>The most output tokens for which a request with <paramref name="inputTokens"/> input tokens costs at most
    /// <paramref name="budgetMicros"/> (the "cap the output so it fits" choice); null when not even one output token fits, or when output is
    /// free (then the output does not change the cost).</summary>
    public int? OutputTokensWithin(double inputTokens, long budgetMicros)
    {
        if (Output <= 0 || !double.IsFinite(Output)) return null;
        var left = budgetMicros - SpendMoney.Cost(inputTokens, WorstInput);
        if (left <= 0) return null;
        var tokens = decimal.Floor(left / (decimal)Output);
        return tokens < 1 ? null : tokens >= int.MaxValue ? int.MaxValue : (int)tokens;
    }
}

/// <summary>Where an estimate's output token count came from. Sent as <c>request</c>, <c>chosen</c>, <c>reservation-cap</c>,
/// <c>model-maximum</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OutputTokenBasis>))]
public enum OutputTokenBasis
{
    /// <summary>The request's own <c>maxTokens</c>.</summary>
    [JsonStringEnumMemberName("request")] Request,

    /// <summary>The output cap the user chose for this request ("cap the output so it fits").</summary>
    [JsonStringEnumMemberName("chosen")] Chosen,

    /// <summary>The default reservation output cap (<see cref="SpendSettings.ReservationOutputTokens"/>, 8,192 unless the user raised it).</summary>
    [JsonStringEnumMemberName("reservation-cap")] ReservationCap,

    /// <summary>The model's maximum output (lower than the reservation cap, or no cap applies).</summary>
    [JsonStringEnumMemberName("model-maximum")] ModelMaximum,
}

/// <summary>What the estimator is asked to price.</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Model">The model id.</param>
/// <param name="Account">The account id the request goes through, or null when its provider needs none.</param>
/// <param name="InputBytes">The request's size in UTF-8 bytes: everything sent as input (system prompt, messages, tools).</param>
/// <param name="MaxTokens">The request's <c>maxTokens</c>, or null when it sets none.</param>
public sealed record SpendEstimateInput(string Provider, string Model, string? Account, long InputBytes, int? MaxTokens = null)
{
    /// <summary>Whether <paramref name="MaxTokens"/> is the user's "cap the output" choice rather than the request's own.</summary>
    public bool ChosenMaxTokens { get; init; }

    /// <summary>The same input, trimmed and checked.</summary>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public SpendEstimateInput Normalize()
    {
        if (string.IsNullOrWhiteSpace(Provider) || string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("An estimate needs a provider and a model.");
        if (InputBytes < 0) throw new ArgumentException("A request's size is never negative.");
        if (MaxTokens is < 1) throw new ArgumentException("maxTokens must be at least 1.");
        return this with { Provider = Provider.Trim(), Model = Model.Trim(), Account = string.IsNullOrWhiteSpace(Account) ? null : Account.Trim() };
    }
}

/// <summary>What a request may cost.</summary>
public sealed record SpendEstimate
{
    /// <summary>The provider id.</summary>
    public required string Provider { get; init; }

    /// <summary>The model id.</summary>
    public required string Model { get; init; }

    /// <summary>The account id, or null when the provider needs none.</summary>
    public string? Account { get; init; }

    /// <summary>How the request is billed (<see cref="IBillingModes"/>). <see cref="BillingKind"/> has no JSON converter of its own: write it
    /// with <c>SettingsStore.JsonOptions</c> (camelCase strings: <c>metered</c>, <c>subscription</c>, <c>free</c>, <c>unknown</c>).</summary>
    public required BillingKind Billing { get; init; }

    /// <summary>The rates used, or null when the price is unknown (then <see cref="WorstMicros"/> is null too).</summary>
    public SpendPrice? Price { get; init; }

    /// <summary>The input tokens assumed (UTF-8 bytes / 3 x 1.1).</summary>
    public double InputTokens { get; init; }

    /// <summary>The output tokens assumed: the most the request may produce.</summary>
    public int OutputTokens { get; init; }

    /// <summary>Where <see cref="OutputTokens"/> came from.</summary>
    public OutputTokenBasis OutputBasis { get; init; } = OutputTokenBasis.ReservationCap;

    /// <summary>The worst case in micro-USD: at list price for a subscription, 0 for free, null when the price is unknown.</summary>
    public long? WorstMicros { get; init; }

    /// <summary>The median of the last 20 actual costs of this provider and model, in micro-USD; null until there are any. Shown, never
    /// reserved.</summary>
    public long? TypicalMicros { get; init; }

    /// <summary>Whether the price is known.</summary>
    [JsonIgnore]
    public bool PriceKnown => Price is not null && WorstMicros is not null;

    /// <summary>Whether this request counts toward money caps: metered with a known price. Subscription, free and unknown-price requests
    /// never do.</summary>
    [JsonIgnore]
    public bool CountsTowardCaps => Billing == BillingKind.Metered && WorstMicros is not null;

    /// <summary>The same estimate with at most <paramref name="outputTokens"/> output tokens (the user's "cap the output" choice): the worst
    /// case is worked out again from the same input and rates.</summary>
    public SpendEstimate WithOutputTokens(int outputTokens, OutputTokenBasis basis = OutputTokenBasis.Chosen)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(outputTokens, 1);
        long? worst = Price is null || WorstMicros is null ? null : Billing == BillingKind.Free ? 0 : Price.WorstMicros(InputTokens, outputTokens);
        return this with { OutputTokens = outputTokens, OutputBasis = basis, WorstMicros = worst };
    }
}

/// <summary>Prices a request before it is sent (the Estimation unit's <c>CostEstimator</c>). A singleton; safe to call from many threads.</summary>
public interface ICostEstimator
{
    /// <summary>The worst case (and typical cost) of <paramref name="input"/>: its billing mode, the rates used (catalog, tier or entered
    /// price), the tokens assumed. Never reserves anything.</summary>
    /// <exception cref="ArgumentException"><paramref name="input"/> is not allowed (<see cref="SpendEstimateInput.Normalize"/>).</exception>
    Task<SpendEstimate> EstimateAsync(SpendEstimateInput input, CancellationToken cancellationToken = default);
}

/// <summary>How an account of a provider is billed, and why.</summary>
/// <param name="Mode">The mode that applies: the user's override, else <paramref name="Detected"/>.</param>
/// <param name="Detected">The mode worked out from how the account connects.</param>
/// <param name="Overridden">Whether a <see cref="BillingOverride"/> chose <paramref name="Mode"/>.</param>
/// <param name="Basis">Why, for the pages, without any secret: <c>Stored API key</c>, <c>Environment · OPENAI_API_KEY</c>,
/// <c>ChatGPT sign-in</c>, <c>localhost:1234 · runs on this machine</c>, <c>No price in the catalog</c>.</param>
public sealed record BillingResolution(BillingKind Mode, BillingKind Detected, bool Overridden, string Basis);

/// <summary>Resolves billing modes (the Estimation unit's <c>BillingModes</c>): API keys and environment keys are Metered, OAuth subscriptions
/// (<c>openai-codex</c>, <c>github-copilot</c>) Subscription, loopback custom providers and the plain faux provider Free, a provider without
/// a price Unknown (Metered once a price is entered); a user override wins. A singleton; safe to call from many threads.</summary>
public interface IBillingModes
{
    /// <summary>The billing of <paramref name="provider"/> through <paramref name="account"/> (null when the provider needs none).</summary>
    BillingResolution Resolve(string provider, string? account);
}

/// <summary>What a provider reported a request used.</summary>
/// <param name="InputTokens">Input tokens (not cached).</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="CacheReadTokens">Cached input tokens read.</param>
/// <param name="CacheWriteTokens">Input tokens written to the cache.</param>
public sealed record SpendUsage(double InputTokens, double OutputTokens, double CacheReadTokens = 0, double CacheWriteTokens = 0)
{
    /// <summary>The cost the provider layer reported (pi-ai's <c>usage.cost.total</c>), in micro-USD; null when none was reported.</summary>
    public long? CostMicros { get; init; }

    /// <summary>The usage of a finished message (<c>StreamDone</c>'s <see cref="AssistantMessage.Usage"/>).</summary>
    public static SpendUsage From(TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return new SpendUsage(usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite) { CostMicros = SpendMoney.Round(usage.Cost.Total * SpendMoney.MicrosPerDollar) };
    }

    /// <summary>Whether any tokens were reported.</summary>
    [JsonIgnore]
    public bool HasTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens > 0;
}
