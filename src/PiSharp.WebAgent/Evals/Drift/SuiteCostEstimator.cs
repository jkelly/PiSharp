// Decision 0011 (run W5, E3): what one run of a suite would cost (SuiteEstimate; mockups evals-suites "worst $1.62 · typical $0.31 · cap
// $2.00", modal-suite-schedule's "How the worst case is worked out", evals-drift's "worst $0.49 → $0.54"). It is the Spend Guard's worst case
// (CostEstimator, decision 0011 W3) summed over every call a run reserves: each case on each target k times, as the target sends it
// (SuiteTarget.Apply), with input tokens from the request's UTF-8 size (bytes × 11 / 30, rounded up; the planned request's messages when the
// run engine can plan it, else the stored request) and output tokens from its maxTokens, else the reservation cap, never above the model's
// maximum; plus one judge call per answer for each judge check (JudgeSpec.MaxInputTokens in, MaxOutputTokens out, at the judge's price). The
// billing mode is IBillingModes': metered calls make WorstMicros (what the run reserves and the cap is compared with), subscription calls
// make ListMicros (never counted toward a cap), free calls cost $0, and a metered call without a known price makes an Unknown line (a
// schedule cannot ask, so it is skipped or paused). Price Watch passes prices: the base rates of chosen models replaced (a snapshot's,
// before or after a change), the catalog's tiers kept; the rest at the catalog's current prices. TypicalMicros is left null for the suites
// unit (E4), which has the past runs. Lines are per model and kind (target or judge), metered first; a line's EachMicros is its calls' mean
// rounded up, and its RunMicros the exact sum.
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Evals.Drift;

/// <summary>A suite with its cases, as Price Watch and the estimator read them.</summary>
/// <param name="Suite">The suite.</param>
/// <param name="Cases">Its cases (<see cref="TestCase.SuiteId"/> is the suite's id).</param>
public sealed record SuiteCases(Suite Suite, IReadOnlyList<TestCase> Cases);

/// <summary>Works out what one run of a suite reserves.</summary>
public interface ISuiteCostEstimator
{
    /// <summary>The worst case of one run of <paramref name="suite"/> with <paramref name="cases"/> (no typical cost). With
    /// <paramref name="prices"/>, the models it names (<see cref="SuiteCostEstimator.PriceKey"/>) are priced at those base rates (null: no
    /// price) instead of the catalog's.</summary>
    SuiteEstimate Estimate(Suite suite, IReadOnlyList<TestCase> cases, IReadOnlyDictionary<string, CatalogCost?>? prices = null);
}

/// <summary>The Spend Guard's worst case, per suite run. A singleton; safe to call from many threads.</summary>
public sealed class SuiteCostEstimator : ISuiteCostEstimator
{
    private readonly IModelCatalog catalog;
    private readonly ISettingsStore settings;
    private readonly IBillingModes billing;
    private readonly ModelPrices modelPrices;
    private readonly Func<RunSpec, long> inputBytes;

    /// <summary>An estimator over the Spend Guard's catalog, settings (the reservation cap), billing modes and prices. <paramref name="inputBytes"/>
    /// gives a request's UTF-8 size as the Spend Guard measures it (default: <see cref="RunSpec.RequestBytes"/>).</summary>
    public SuiteCostEstimator(IModelCatalog catalog, ISettingsStore settings, IBillingModes billing, ModelPrices prices, Func<RunSpec, long>? inputBytes = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(billing);
        ArgumentNullException.ThrowIfNull(prices);
        this.catalog = catalog;
        this.settings = settings;
        this.billing = billing;
        modelPrices = prices;
        this.inputBytes = inputBytes ?? (spec => spec.RequestBytes());
    }

    /// <summary>The key of a model in a price override: <c>provider/model</c>.</summary>
    public static string PriceKey(string provider, string model) => provider + "/" + model;

    /// <summary>A request's UTF-8 size as the run engine reserves it: the planned request's messages (system prompt and tools included), or
    /// <see cref="RunSpec.RequestBytes"/> when the engine cannot plan it (an unknown model).</summary>
    public static long PlannedBytes(RunEngine? engine, RunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (engine is not null)
        {
            try { return CostEstimator.InputBytes(engine.Plan(spec).Messages); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return spec.RequestBytes();
    }

    /// <inheritdoc/>
    public SuiteEstimate Estimate(Suite suite, IReadOnlyList<TestCase> cases, IReadOnlyDictionary<string, CatalogCost?>? prices = null)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(cases);
        var reservationCap = settings.Get<SpendSettings>().ReservationOutputTokens;
        var lines = new Dictionary<(string Provider, string Model, BillingKind Billing, bool Judge), (int Calls, long Micros)>();
        long worst = 0, list = 0;

        void Add(string provider, string model, string? account, double inputTokens, int? maxTokens, int calls, bool judge)
        {
            if (calls <= 0) return;
            var mode = billing.Resolve(provider, account).Mode;
            var pricing = Pricing(provider, model, prices);
            var (outputTokens, _) = CostEstimator.OutputTokens(maxTokens, false, catalog.FindModel(provider, model)?.MaxTokens ?? 0, reservationCap);
            long each = 0;
            if (mode == BillingKind.Free) each = 0;
            else if (mode == BillingKind.Unknown || pricing is null) mode = mode == BillingKind.Subscription ? BillingKind.Subscription : BillingKind.Unknown;
            else each = pricing.WorstRates(inputTokens).WorstMicros(inputTokens, outputTokens);
            var micros = checked(each * calls);
            if (mode == BillingKind.Metered) worst = checked(worst + micros);
            else if (mode == BillingKind.Subscription) list = checked(list + micros);
            var key = (provider, model, mode, judge);
            var (count, sum) = lines.GetValueOrDefault(key);
            lines[key] = (count + calls, checked(sum + micros));
        }

        var k = Math.Max(1, suite.K);
        foreach (var target in suite.Targets)
        {
            var normalized = target.Normalize();
            foreach (var testCase in cases)
            {
                var spec = normalized.Apply(testCase.Spec);
                Add(normalized.Provider, normalized.Model, normalized.Account, CostEstimator.InputTokens(inputBytes(spec)), spec.MaxTokens, k, judge: false);
            }
        }
        var answersPerCase = suite.Targets.Count * k;
        foreach (var testCase in cases)
            foreach (var check in testCase.Checks)
                if (check.Kind == CheckKind.Judge && check.Judge is { } judge)
                    Add(judge.Provider, judge.Model, judge.Account, JudgeSpec.MaxInputTokens, JudgeSpec.MaxOutputTokens, answersPerCase, judge: true);

        var ordered = lines.OrderBy(line => Rank(line.Key.Billing)).ThenBy(line => line.Key.Judge)
            .ThenBy(line => line.Key.Provider, StringComparer.Ordinal).ThenBy(line => line.Key.Model, StringComparer.Ordinal)
            .Select(line => new SuiteEstimateLine(line.Key.Provider, line.Key.Model, line.Value.Calls,
                line.Value.Calls == 0 ? 0 : (line.Value.Micros + line.Value.Calls - 1) / line.Value.Calls, line.Value.Micros, line.Key.Billing, line.Key.Judge))
            .ToList();
        return new SuiteEstimate(worst, null, list, ordered);
    }

    private ModelPricing? Pricing(string provider, string model, IReadOnlyDictionary<string, CatalogCost?>? prices)
    {
        var current = modelPrices.Find(provider, model);
        if (prices is null || !prices.TryGetValue(PriceKey(provider, model), out var cost)) return current;
        if (cost is null) return null;
        var rates = SpendPrice.From(cost);
        try { rates = rates.Normalize(); }
        catch (ArgumentException) { return null; }
        return current is null ? new ModelPricing(provider, model, rates, []) : current with { Base = rates };
    }

    private static int Rank(BillingKind kind) => kind switch
    {
        BillingKind.Metered => 0,
        BillingKind.Subscription => 1,
        BillingKind.Free => 2,
        _ => 3,
    };
}
