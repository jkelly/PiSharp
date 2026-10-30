// Decision 0011 (run W3, S1): the worst case of a request before it is sent, as the approved plan defines it. Input tokens = UTF-8 bytes ÷ 3
// × 1.1, rounded up to a whole token (bytes × 11 / 30 in decimal). The input rate is the higher of the input and cache-write rates (any of
// it may be written to the cache), of the higher price tier when the input is within 10% of a tier boundary (ModelPricing.WorstRates).
// Output tokens = the request's maxTokens (the user's "cap the output" choice is marked Chosen), else the default reservation cap
// (SpendSettings.ReservationOutputTokens, 8,192), and never more than the model's maximum. Worst case = input tokens × input rate + output
// tokens × output rate, rounded up once to the micro-USD (SpendPrice.WorstMicros): 12,000 × $2.00/M + 8,192 × $10.00/M = $0.1059. The
// billing mode is IBillingModes', made exact per model: a metered model without a price (catalog or entered) is Unknown (no worst case,
// so the unknown-price behaviour applies), a subscription keeps its list price (never counted toward caps) or none, and Free costs $0.
// "Typical" is the median of the last 20 actual costs of the provider and model from the ledger (ISpendLedger.RecentActualsAsync,
// resolved on use: S2 may not be registered, and then it is null), shown and never reserved; it is left out for Free, $0 and Unknown
// requests.
// Cheaper models are only suggestions: the estimator never changes a request's model or account.
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using ChordJson = PiSharp.Durable.Chord.JsonValue;

namespace PiSharp.WebAgent.Spend;

/// <summary>Prices requests before they are sent (<see cref="ICostEstimator"/>). A singleton; safe to call from many threads.</summary>
public sealed class CostEstimator : ICostEstimator
{
    /// <summary>How many recent actual costs "typical" is the median of.</summary>
    public const int TypicalSampleSize = 20;

    /// <summary>At most this many cheaper models of one provider are suggested.</summary>
    public const int SuggestionsPerProvider = 2;

    private readonly IModelCatalog catalog;
    private readonly ISettingsStore settings;
    private readonly IBillingModes billing;
    private readonly ModelPrices prices;
    private readonly IAccountStore? accounts;
    private readonly Func<ISpendLedger?> ledger;
    private readonly ILogger<CostEstimator>? log;

    /// <summary>An estimator over <paramref name="catalog"/> (model maxima, the models to suggest), <paramref name="prices"/>,
    /// <paramref name="billing"/>, <paramref name="settings"/> (the reservation cap, hidden models), <paramref name="accounts"/> (the account a
    /// suggestion of another provider would use; null: only providers that need no account are suggested) and <paramref name="ledger"/>
    /// (resolved on each use; null or returning null: no typical cost).</summary>
    public CostEstimator(IModelCatalog catalog, ISettingsStore settings, IBillingModes billing, ModelPrices prices, IAccountStore? accounts = null,
        Func<ISpendLedger?>? ledger = null, ILogger<CostEstimator>? log = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(billing);
        ArgumentNullException.ThrowIfNull(prices);
        this.catalog = catalog;
        this.settings = settings;
        this.billing = billing;
        this.prices = prices;
        this.accounts = accounts;
        this.ledger = ledger ?? (() => null);
        this.log = log;
    }

    /// <inheritdoc/>
    public async Task<SpendEstimate> EstimateAsync(SpendEstimateInput input, CancellationToken cancellationToken = default)
    {
        var estimate = Estimate(input);
        if (estimate.Billing is BillingKind.Free or BillingKind.Unknown || estimate.WorstMicros is null or 0) return estimate;
        return estimate with { TypicalMicros = await TypicalAsync(estimate.Provider, estimate.Model, cancellationToken).ConfigureAwait(false) };
    }

    /// <summary>The estimate of <paramref name="input"/> without the typical cost (no ledger read): what <see cref="EstimateAsync"/> returns
    /// with <see cref="SpendEstimate.TypicalMicros"/> null.</summary>
    /// <exception cref="ArgumentException"><paramref name="input"/> is not allowed (<see cref="SpendEstimateInput.Normalize"/>).</exception>
    public SpendEstimate Estimate(SpendEstimateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = input.Normalize();
        return Estimate(normalized, billing.Resolve(normalized.Provider, normalized.Account), settings.Get<SpendSettings>());
    }

    /// <summary>Models that would cost less for the same request (the same size and output cap), as suggestions only: the confirm modal's
    /// "cheaper models". Each is a model the pickers show (not hidden in Settings › Models) of a provider that needs no account or has a
    /// connected active one (the request's own account for its own provider), with a known price, and either costs less than this request's
    /// worst case or is not counted toward money caps (free, or a subscription at list price). With <paramref name="withinMicros"/>, a
    /// counted one must also cost at most that (the room left in the tightest cap). Same provider first, then the catalog's provider order;
    /// within a provider the dearest first (the closest to the request's model), at most <see cref="SuggestionsPerProvider"/> each. Empty
    /// when this request is free or a subscription with a known price (nothing counted to save).</summary>
    /// <exception cref="ArgumentException"><paramref name="input"/> is not allowed.</exception>
    public IReadOnlyList<SpendEstimate> Cheaper(SpendEstimateInput input, long? withinMicros = null, int count = 3)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var normalized = input.Normalize();
        var spend = settings.Get<SpendSettings>();
        var current = Estimate(normalized, billing.Resolve(normalized.Provider, normalized.Account), spend);
        if (count == 0 || (current.PriceKnown && !current.CountsTowardCaps)) return [];
        var hidden = settings.Get<ModelsSettings>();
        var order = catalog.Providers.Select(provider => provider.Id).ToList();
        order.Remove(current.Provider);
        if (catalog.FindProvider(current.Provider) is not null) order.Insert(0, current.Provider);
        var found = new List<SpendEstimate>();
        foreach (var providerId in order)
        {
            if (!Usable(providerId, current, out var account)) continue;
            var resolution = billing.Resolve(providerId, account);
            var candidates = new List<SpendEstimate>();
            foreach (var model in catalog.Models(providerId))
            {
                if ((providerId == current.Provider && model.Id == current.Model) || hidden.IsHidden(providerId, model.Id)) continue;
                var estimate = Estimate(normalized with { Provider = providerId, Model = model.Id, Account = account }, resolution, spend);
                if (!estimate.PriceKnown) continue;
                if (estimate.CountsTowardCaps && ((current.WorstMicros is { } worst && estimate.WorstMicros >= worst) || estimate.WorstMicros > withinMicros)) continue;
                candidates.Add(estimate);
            }
            found.AddRange(candidates.OrderByDescending(estimate => estimate.WorstMicros).Take(SuggestionsPerProvider));
            if (found.Count >= count) break;
        }
        return [.. found.Take(count)];
    }

    /// <summary>The input tokens a request of <paramref name="inputBytes"/> UTF-8 bytes is assumed to have: bytes ÷ 3 × 1.1, rounded up to a
    /// whole token (32,727 bytes are 12,000 tokens).</summary>
    public static double InputTokens(long inputBytes) => inputBytes <= 0 ? 0 : (double)decimal.Ceiling(inputBytes * 11m / 30m);

    /// <summary>The UTF-8 size of a request's messages (pi-ai wire JSON; the system prompt and tools ride as positional system messages), the
    /// <see cref="SpendEstimateInput.InputBytes"/> of a durable call.</summary>
    public static long InputBytes(IEnumerable<ChordJson> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        long bytes = 0;
        foreach (var message in messages) bytes += System.Text.Encoding.UTF8.GetByteCount(message.ToJson());
        return bytes;
    }

    /// <summary>The output tokens a worst case assumes, and why: <paramref name="maxTokens"/> (the request's own, or
    /// <see cref="OutputTokenBasis.Chosen"/> when <paramref name="chosen"/>), else <paramref name="reservationCap"/>; never more than
    /// <paramref name="modelMaximum"/> (when above 0), which is then the basis.</summary>
    public static (int Tokens, OutputTokenBasis Basis) OutputTokens(int? maxTokens, bool chosen, int modelMaximum, int reservationCap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reservationCap, 1);
        var maximum = modelMaximum > 0 ? modelMaximum : int.MaxValue;
        if (maxTokens is { } requested)
            return requested > maximum ? (maximum, OutputTokenBasis.ModelMaximum) : (requested, chosen ? OutputTokenBasis.Chosen : OutputTokenBasis.Request);
        return reservationCap > maximum ? (maximum, OutputTokenBasis.ModelMaximum) : (reservationCap, OutputTokenBasis.ReservationCap);
    }

    /// <summary>The median of <paramref name="costs"/> (the first <see cref="TypicalSampleSize"/>, newest first), in micro-USD: the middle one,
    /// or the mean of the middle two rounded to the nearest (halves away from zero); null when there are none.</summary>
    public static long? Median(IReadOnlyList<long> costs)
    {
        ArgumentNullException.ThrowIfNull(costs);
        if (costs.Count == 0) return null;
        var sorted = costs.Take(TypicalSampleSize).Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : SpendMoney.Round(((decimal)sorted[middle - 1] + sorted[middle]) / 2);
    }

    private SpendEstimate Estimate(SpendEstimateInput input, BillingResolution resolution, SpendSettings spend)
    {
        var listed = catalog.FindModel(input.Provider, input.Model);
        var inputTokens = InputTokens(input.InputBytes);
        var (outputTokens, basis) = OutputTokens(input.MaxTokens, input.ChosenMaxTokens, listed?.MaxTokens ?? 0, spend.ReservationOutputTokens);
        var mode = resolution.Mode;
        SpendPrice? price = null;
        long? worst = null;
        if (mode == BillingKind.Free)
        {
            price = new SpendPrice(0, 0);
            worst = 0;
        }
        else if (mode != BillingKind.Unknown)
        {
            if (prices.Find(input.Provider, input.Model) is { } pricing)
            {
                price = pricing.WorstRates(inputTokens);
                worst = price.WorstMicros(inputTokens, outputTokens);
            }
            else if (mode == BillingKind.Metered) mode = BillingKind.Unknown;
        }
        return new SpendEstimate
        {
            Provider = input.Provider, Model = input.Model, Account = input.Account, Billing = mode, Price = price, InputTokens = inputTokens,
            OutputTokens = outputTokens, OutputBasis = basis, WorstMicros = worst,
        };
    }

    /// <summary>Whether a suggestion of <paramref name="provider"/> can be sent, and through which account: the request's own for its own
    /// provider, none for a provider that needs none, else the provider's connected active account.</summary>
    private bool Usable(string provider, SpendEstimate current, out string? account)
    {
        account = null;
        if (provider == current.Provider) { account = current.Account; return true; }
        var listed = catalog.FindProvider(provider);
        if (listed is null) return false;
        if (listed.AuthKinds.Count == 0 || listed.AuthKinds.All(kind => kind == ProviderAuthKind.None)) return true;
        if (accounts?.Active(provider) is not { Status: AccountStatus.Connected } active) return false;
        account = active.Id;
        return true;
    }

    private async Task<long?> TypicalAsync(string provider, string model, CancellationToken cancellationToken)
    {
        try
        {
            if (ledger() is not { } recent) return null;
            return Median(await recent.RecentActualsAsync(provider, model, TypicalSampleSize, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Typical is shown, never reserved: a ledger that cannot be read leaves it out rather than failing the estimate.
            log?.LogWarning(error, "The recent costs of {Provider}/{Model} could not be read; the estimate has no typical cost.", provider, model);
            return null;
        }
    }
}
