// Decision 0011 (run W5, E0): suites (mockups evals-suites _SuiteCard, evals-suite _SuiteHeader, _SuiteTargets, _SuiteCases). A suite runs
// its cases on each of its targets k times (default 3), now or on a schedule (SuiteSchedule), within a suite-run cap. A target is "its
// provider, model, account and settings" (evals-suite): the settings (thinking, temperature, service tier) replace a case's own when it runs
// on that target (Apply), so every case of a run is asked the same way, and the target's ModelIdentity (without the account) is what
// drift history and the leaderboard group by. A target's id is derived from what it is, so removing a target and adding it again finds its
// history. The approved plan says 2 to 6 targets; a suite made from a pinned case starts with that request's model as its only target
// (modal-test-case's "New suite"), so a suite holds 0 to 6 and needs at least one target and one case to run (RunProblem).
//
// The cap is the most one run of the suite may reserve, in micro-USD. Scheduled runs cannot ask, so the cap blocks: a schedule is refused
// when the suite's worst case does not fit it (SuiteEstimate.Fits), and paused when a price change makes it stop fitting. Costs are metered
// spend; subscription targets are counted at list price beside it and never toward the cap (the Spend Guard's rule).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Evals;

/// <summary>One target of a suite: a provider, a model, an account and the settings that change answers.</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Model">The model id.</param>
/// <param name="Account">The account id, or null for the provider's active account when the run starts.</param>
/// <param name="Thinking">The thinking level, or null for none.</param>
/// <param name="Temperature">The temperature (0 to 2), or null for the provider's default.</param>
/// <param name="ServiceTier">The service tier, or null for the provider's default.</param>
public sealed record SuiteTarget(string Provider, string Model, string? Account = null, string? Thinking = null, double? Temperature = null,
    string? ServiceTier = null)
{
    /// <summary>The target's id within its suite: <c>t</c> and 12 hex digits of the SHA-256 of its identity key and account, so the same
    /// target always has the same id.</summary>
    public string Id
    {
        get
        {
            var key = Identity.Key + "|" + (string.IsNullOrWhiteSpace(Account) ? "" : Account.Trim());
            return "t" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
        }
    }

    /// <summary>The target without its account: the leaderboard's and drift's identity.</summary>
    [JsonIgnore]
    public ModelIdentity Identity => new ModelIdentity(Provider, Model, Thinking, Temperature, ServiceTier).Normalize();

    /// <summary>The same target, trimmed and checked (thinking <c>off</c> as null, the temperature to two decimals).</summary>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public SuiteTarget Normalize()
    {
        var identity = Identity;
        var account = string.IsNullOrWhiteSpace(Account) ? null : Account.Trim();
        if (account is not null && (account.Length > 200 || account.Any(char.IsControl))) throw new ArgumentException("The account id is not allowed.");
        return new SuiteTarget(identity.Provider, identity.Model, account, identity.Thinking, identity.Temperature, identity.ServiceTier);
    }

    /// <summary><paramref name="caseSpec"/> as this target sends it: the target's provider, model, account, thinking, temperature and service
    /// tier (each null one sends none), with source <c>drift</c>, and the rest of the case's request as pinned.</summary>
    public RunSpec Apply(RunSpec caseSpec)
    {
        ArgumentNullException.ThrowIfNull(caseSpec);
        var target = Normalize();
        return caseSpec with
        {
            Provider = target.Provider,
            Model = target.Model,
            Account = target.Account,
            Thinking = target.Thinking,
            Temperature = target.Temperature,
            ServiceTier = target.ServiceTier,
            Source = SpendSource.Drift.Wire(),
        };
    }
}

/// <summary>A suite: its targets, how often each case runs on each, its schedule and its cap. Its cases are the <see cref="TestCase"/>s
/// whose <see cref="TestCase.SuiteId"/> is its id.</summary>
public sealed record Suite
{
    /// <summary>The longest name.</summary>
    public const int MaxName = 80;

    /// <summary>The longest description.</summary>
    public const int MaxDescription = 500;

    /// <summary>The most targets.</summary>
    public const int MaxTargets = 6;

    /// <summary>The default repetitions per case and target.</summary>
    public const int DefaultK = 3;

    /// <summary>The most repetitions per case and target.</summary>
    public const int MaxK = 10;

    /// <summary>The suite id (<see cref="EvalIds.Suite"/>).</summary>
    public required string Id { get; init; }

    /// <summary>Its name (1 to <see cref="MaxName"/>), such as <c>Regex edge cases</c>.</summary>
    public required string Name { get; init; }

    /// <summary>A description, or null.</summary>
    public string? Description { get; init; }

    /// <summary>Its targets (0 to <see cref="MaxTargets"/>, distinct).</summary>
    public IReadOnlyList<SuiteTarget> Targets { get; init; } = [];

    /// <summary>Repetitions of each case on each target (1 to <see cref="MaxK"/>; default 3).</summary>
    public int K { get; init; } = DefaultK;

    /// <summary>The schedule, or null for runs started by hand only.</summary>
    public SuiteSchedule? Schedule { get; init; }

    /// <summary>The suite-run cap: the most one run may reserve (metered), in micro-USD; null for none. An enabled schedule needs one.</summary>
    public long? CapMicros { get; init; }

    /// <summary>When it was made.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset Updated { get; init; }

    /// <summary>Answers per run with <paramref name="cases"/> cases: cases × targets × k (mockup "12 cases × 3 targets × k 3 · 108 answers
    /// per run").</summary>
    public int AnswersPerRun(int cases) => Math.Max(0, cases) * Targets.Count * K;

    /// <summary>Why a run of the suite with <paramref name="cases"/> cases cannot start, or null when it can.</summary>
    public string? RunProblem(int cases) =>
        cases <= 0 ? "Add a test case first." : Targets.Count == 0 ? "Add a target first." : null;

    /// <summary>The target with id <paramref name="targetId"/>, or null.</summary>
    public SuiteTarget? Target(string? targetId) => Targets.FirstOrDefault(target => target.Id == targetId);

    /// <summary>The same suite, trimmed and checked: targets normalized and distinct, k in range, the schedule normalized, and an enabled
    /// schedule only with a cap.</summary>
    /// <exception cref="ArgumentException">Something is not allowed; the message says what, for the page to show.</exception>
    public Suite Normalize()
    {
        var id = EvalIds.Require(EvalIds.Suite, Id, "suite");
        var name = Name?.Trim() ?? "";
        if (name.Length == 0) throw new ArgumentException("Give the suite a name.");
        if (name.Length > MaxName || name.Any(char.IsControl)) throw new ArgumentException($"A suite's name is at most {MaxName} characters on one line.");
        var description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        if (description is { Length: > MaxDescription }) throw new ArgumentException($"A suite's description is at most {MaxDescription} characters.");
        var targets = (Targets ?? []).ToList();
        if (targets.Any(target => target is null)) throw new ArgumentException("A target is empty.");
        if (targets.Count > MaxTargets) throw new ArgumentException($"A suite has at most {MaxTargets} targets.");
        targets = [.. targets.Select(target => target.Normalize())];
        if (targets.Select(target => target.Id).Distinct(StringComparer.Ordinal).Count() != targets.Count)
            throw new ArgumentException("Two targets are the same provider, model, account and settings.");
        if (K is < 1 or > MaxK) throw new ArgumentException($"k is 1 to {MaxK}.");
        if (CapMicros is { } cap && (cap <= 0 || cap > SpendMoney.MaxMicros)) throw new ArgumentException("A suite-run cap is more than $0.00.");
        var schedule = Schedule?.Normalize();
        if (schedule is { Enabled: true } && CapMicros is null)
            throw new ArgumentException("A schedule needs a suite-run cap: scheduled runs can't ask, so the cap blocks.");
        return this with { Id = id, Name = name, Description = description, Targets = targets, Schedule = schedule };
    }
}

/// <summary>What one run of a suite would cost (mockup modal-suite-schedule's "How the worst case is worked out").</summary>
/// <param name="WorstMicros">The metered worst case: every call's worst case (input tokens × input rate + max output tokens × output rate)
/// of every metered target, plus the judge calls; what the run reserves.</param>
/// <param name="TypicalMicros">The median metered cost of the suite's last 20 complete full runs (every case, target and repeat of the suite
/// as it is now; "Run this case" subset runs left out), or null before there are any.</param>
/// <param name="ListMicros">The worst case of the subscription targets at list price (never counted toward money caps).</param>
/// <param name="Lines">The calls per run by model, metered first.</param>
public sealed record SuiteEstimate(long WorstMicros, long? TypicalMicros, long ListMicros, IReadOnlyList<SuiteEstimateLine> Lines)
{
    /// <summary>Whether a run fits a suite-run cap of <paramref name="capMicros"/>: its metered worst case is at most the cap.</summary>
    public bool Fits(long capMicros) => WorstMicros <= capMicros;

    /// <summary>Whether some calls have no known price (a schedule cannot ask, so such a run is skipped until a price is entered).</summary>
    [JsonIgnore]
    public bool HasUnknownPrice => Lines.Any(line => line.Billing == BillingKind.Unknown);
}

/// <summary>One line of a <see cref="SuiteEstimate"/>: the calls a run makes to one model.</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Model">The model id.</param>
/// <param name="Calls">How many calls per run (cases × k for a target; judgements for a judge).</param>
/// <param name="EachMicros">One call's worst case.</param>
/// <param name="RunMicros">All of them: <paramref name="Calls"/> × <paramref name="EachMicros"/>.</param>
/// <param name="Billing">How the model is paid for (subscription lines are list price).</param>
/// <param name="Judge">Whether these are judge calls.</param>
public sealed record SuiteEstimateLine(string Provider, string Model, int Calls, long EachMicros, long RunMicros, BillingKind Billing, bool Judge = false);
