// Decision 0011 (run W3, S0): one priced provider request and what the Spend Guard decides about it. A request is identified by its op (the
// idempotent operation key: metadata.pisharp.op for a durable call, so a recovered request reuses its reservation) and carries its source,
// run key and estimate. The decision is Allow, Ask (with the causes the user answers: proceed once, or cap the output so it fits) or Block
// (with the cap that blocks; Block takes priority over Ask). The guard never switches the model or the account: a cheaper model is a
// suggestion the user picks, and "cap the output" lowers maxTokens only when the user chose it. A request the user approved once
// (Approved) passes Ask causes; Block causes still stop it.
using System.Text.Json.Serialization;

namespace PiSharp.WebAgent.Spend;

/// <summary>One provider request to check or reserve.</summary>
public sealed record SpendRequest
{
    /// <summary>The longest op, run key or note accepted.</summary>
    public const int MaxKeyLength = 200;

    /// <summary>The idempotent operation key, the reservation's id: reserving the same op again returns the existing reservation.</summary>
    public required string Op { get; init; }

    /// <summary>Where the request was started.</summary>
    public required SpendSource Source { get; init; }

    /// <summary>The run key: the chat id for a chat, the run id for a Playground, Compare, Bench or eval run; null when it belongs to no run
    /// (then no <see cref="CapPeriod.Run"/> cap applies to it).</summary>
    public string? Run { get; init; }

    /// <summary>What it may cost (provider, model, account, billing and worst case).</summary>
    public required SpendEstimate Estimate { get; init; }

    /// <summary>Whether the user approved this request once (proceed once, or a chosen output cap): Ask causes let it through, Block causes
    /// still stop it.</summary>
    public bool Approved { get; init; }

    /// <summary>A short note for the ledger, such as <c>maxTokens 4,096</c> or <c>asked · sent once</c>; null for none.</summary>
    public string? Note { get; init; }

    /// <summary>The provider id (<see cref="SpendEstimate.Provider"/>).</summary>
    [JsonIgnore]
    public string Provider => Estimate.Provider;

    /// <summary>The model id (<see cref="SpendEstimate.Model"/>).</summary>
    [JsonIgnore]
    public string Model => Estimate.Model;

    /// <summary>The account id (<see cref="SpendEstimate.Account"/>).</summary>
    [JsonIgnore]
    public string? Account => Estimate.Account;

    /// <summary>The same request, trimmed and checked.</summary>
    /// <exception cref="ArgumentException">A value is not allowed; the message says which.</exception>
    public SpendRequest Normalize()
    {
        ArgumentNullException.ThrowIfNull(Estimate);
        var op = Op?.Trim();
        if (string.IsNullOrEmpty(op)) throw new ArgumentException("A spend request needs an op (its operation key).");
        if (op.Length > MaxKeyLength) throw new ArgumentException($"An op is at most {MaxKeyLength} characters.");
        if (!Enum.IsDefined(Source)) throw new ArgumentException($"Unknown source {(int)Source}.");
        var run = string.IsNullOrWhiteSpace(Run) ? null : Run.Trim();
        if (run?.Length > MaxKeyLength) throw new ArgumentException($"A run key is at most {MaxKeyLength} characters.");
        if (string.IsNullOrWhiteSpace(Estimate.Provider) || string.IsNullOrWhiteSpace(Estimate.Model)) throw new ArgumentException("A spend request needs a provider and a model.");
        if (!Enum.IsDefined(Estimate.Billing)) throw new ArgumentException($"Unknown billing mode {(int)Estimate.Billing}.");
        if (Estimate.WorstMicros is < 0) throw new ArgumentException("A worst case is never negative.");
        var note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim();
        if (note?.Length > MaxKeyLength) note = note[..MaxKeyLength];
        return this with
        {
            Op = op,
            Run = run,
            Note = note,
            Estimate = Estimate with
            {
                Provider = Estimate.Provider.Trim(),
                Model = Estimate.Model.Trim(),
                Account = string.IsNullOrWhiteSpace(Estimate.Account) ? null : Estimate.Account.Trim(),
                Price = Estimate.Price?.Normalize(),
            },
        };
    }
}

/// <summary>The guard's answer. Sent as <c>allow</c>, <c>ask</c>, <c>block</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SpendVerdict>))]
public enum SpendVerdict
{
    /// <summary>Send it (and reserve its worst case).</summary>
    [JsonStringEnumMemberName("allow")] Allow,

    /// <summary>Ask the user first; nothing is reserved or sent until they answer.</summary>
    [JsonStringEnumMemberName("ask")] Ask,

    /// <summary>Do not send it; the user can only raise or remove the cap in Settings › Spend.</summary>
    [JsonStringEnumMemberName("block")] Block,
}

/// <summary>Why a request is asked about or blocked. Sent as <c>cap</c>, <c>unknown-price</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SpendCauseKind>))]
public enum SpendCauseKind
{
    /// <summary>It does not fit a cap (<see cref="SpendCause.Cap"/>).</summary>
    [JsonStringEnumMemberName("cap")] Cap,

    /// <summary>Its price is unknown, so it cannot be checked against caps, and the provider's unknown-price behaviour is Ask or Block
    /// (<see cref="SpendSettings.UnknownPriceFor"/>).</summary>
    [JsonStringEnumMemberName("unknown-price")] UnknownPrice,
}

/// <summary>One reason a request needs the user's answer or is blocked.</summary>
public sealed record SpendCause
{
    /// <summary>What kind of reason.</summary>
    public required SpendCauseKind Kind { get; init; }

    /// <summary>Block or Ask: the cap's action, or the unknown-price behaviour.</summary>
    public required CapAction Action { get; init; }

    /// <summary>The cap it does not fit, for <see cref="SpendCauseKind.Cap"/>; null for an unknown price.</summary>
    public SpendCap? Cap { get; init; }

    /// <summary>What already counts toward the cap in its window, in micro-USD (0 for a request cap and for an unknown price). In a group
    /// (a Compare run's columns), it includes the group's earlier requests that fit (<see cref="GroupMicros"/>).</summary>
    public long SpentMicros { get; init; }

    /// <summary>The part of <see cref="SpentMicros"/> that the group's earlier requests add, which are not reserved yet (a group's
    /// reservation or preflight: <see cref="ISpendLedger.ReserveGroupAsync"/>, <see cref="ISpendLedger.CheckGroupAsync"/>), in micro-USD;
    /// null outside a group and when they add nothing to this cap. A request whose own worst case fits what is left without them
    /// (<see cref="SpentMicros"/> - this + the worst case &lt;= the limit) is refused only because of the group's total.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? GroupMicros { get; init; }

    /// <summary>What is left in the cap (never below 0), in micro-USD; null for an unknown price.</summary>
    public long? LeftMicros { get; init; }

    /// <summary>The most output tokens with which the request fits this cap (<see cref="SpendPrice.OutputTokensWithin"/>); null when no
    /// output cap makes it fit, and for an unknown price.</summary>
    public int? FitOutputTokens { get; init; }

    /// <summary>The confirm modal's pane for this cause (<c>_SpendConfirmModal</c>): <c>sc-request</c> (a request cap), <c>sc-chat</c> (a run
    /// cap: a chat's, or a run's), <c>sc-period</c> (a day or month cap) or <c>sc-unknown</c> (an unknown price). There is no allowance pane:
    /// subscription allowance windows are not measured.</summary>
    [JsonIgnore]
    public string Pane => Kind == SpendCauseKind.UnknownPrice ? "sc-unknown" : Cap?.Period switch
    {
        CapPeriod.Request => "sc-request",
        CapPeriod.Run => "sc-chat",
        _ => "sc-period",
    };
}

/// <summary>What the guard decided about one request.</summary>
public sealed record SpendDecision
{
    /// <summary>Allow, Ask or Block.</summary>
    public required SpendVerdict Verdict { get; init; }

    /// <summary>The estimate the decision was made on.</summary>
    public required SpendEstimate Estimate { get; init; }

    /// <summary>Every cause, Block causes first. A request the user approved keeps its Ask causes here, with <see cref="Verdict"/> Allow.</summary>
    public IReadOnlyList<SpendCause> Causes { get; init; } = [];

    /// <summary>Caps that would be at or above the warning threshold with this request counted (the amber chip); they do not stop it.</summary>
    public IReadOnlyList<SpendCapStatus> Warnings { get; init; } = [];

    /// <summary>Whether the request may be sent.</summary>
    [JsonIgnore]
    public bool Allowed => Verdict == SpendVerdict.Allow;

    /// <summary>The first cap that blocks it, or null.</summary>
    [JsonIgnore]
    public SpendCap? BlockingCap => Causes.FirstOrDefault(cause => cause.Action == CapAction.Block && cause.Cap is not null)?.Cap;

    /// <summary>The output cap with which the request fits every cap it is over (the smallest <see cref="SpendCause.FitOutputTokens"/>);
    /// null when there is no cap cause, or one of them cannot be made to fit.</summary>
    [JsonIgnore]
    public int? FitOutputTokens
    {
        get
        {
            var caps = Causes.Where(cause => cause.Kind == SpendCauseKind.Cap).ToList();
            if (caps.Count == 0 || caps.Any(cause => cause.FitOutputTokens is null)) return null;
            return caps.Min(cause => cause.FitOutputTokens);
        }
    }

    /// <summary>The error code of a request that is not allowed: <see cref="SpendErrors.Blocked"/>, <see cref="SpendErrors.UnknownPrice"/>
    /// (asked only because the price is unknown) or <see cref="SpendErrors.Ask"/>; null when it is allowed.</summary>
    [JsonIgnore]
    public string? ErrorCode => Verdict switch
    {
        SpendVerdict.Allow => null,
        SpendVerdict.Block => SpendErrors.Blocked,
        _ => Causes.Count > 0 && Causes.All(cause => cause.Kind == SpendCauseKind.UnknownPrice) ? SpendErrors.UnknownPrice : SpendErrors.Ask,
    };

    /// <summary>An allowed request with no cause.</summary>
    public static SpendDecision Allow(SpendEstimate estimate, IReadOnlyList<SpendCapStatus>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return new SpendDecision { Verdict = SpendVerdict.Allow, Estimate = estimate, Warnings = warnings ?? [] };
    }

    /// <summary>The decision for <paramref name="causes"/>: Block when any cause blocks, else Ask when any asks and the request was not
    /// <paramref name="approved"/>, else Allow. Causes are ordered Block first, then as given.</summary>
    public static SpendDecision From(SpendEstimate estimate, IEnumerable<SpendCause> causes, bool approved = false,
        IReadOnlyList<SpendCapStatus>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        ArgumentNullException.ThrowIfNull(causes);
        var ordered = causes.Select((cause, index) => (cause, index)).OrderBy(pair => pair.cause.Action == CapAction.Block ? 0 : 1).ThenBy(pair => pair.index)
            .Select(pair => pair.cause).ToList();
        var verdict = ordered.Any(cause => cause.Action == CapAction.Block) ? SpendVerdict.Block
            : ordered.Count > 0 && !approved ? SpendVerdict.Ask : SpendVerdict.Allow;
        return new SpendDecision { Verdict = verdict, Estimate = estimate, Causes = ordered, Warnings = warnings ?? [] };
    }
}

/// <summary>The result of reserving one request.</summary>
/// <param name="Decision">The decision the ledger made under its lock.</param>
/// <param name="Reservation">The reservation, when the request was allowed (or the op was already reserved); null when it was not.</param>
/// <param name="Existing">Whether the op already had a reservation, which is returned unchanged (nothing is reserved twice).</param>
public sealed record SpendReservation(SpendDecision Decision, Reservation? Reservation, bool Existing = false)
{
    /// <summary>Whether the request holds a reservation and may be sent.</summary>
    [JsonIgnore]
    public bool Reserved => Reservation is not null;
}

/// <summary>The result of reserving a group of requests, all or nothing (a Compare run's columns).</summary>
/// <param name="Items">One result per request, in order. When the group was refused, no item holds a new reservation, and the items that did
/// not fit carry the decision that refused them.</param>
public sealed record SpendGroupReservation(IReadOnlyList<SpendReservation> Items)
{
    /// <summary>Whether every request holds a reservation.</summary>
    [JsonIgnore]
    public bool Reserved => Items.Count > 0 && Items.All(item => item.Reserved);

    /// <summary>The first decision that refused the group, or null.</summary>
    [JsonIgnore]
    public SpendDecision? Refused => Items.Select(item => item.Decision).FirstOrDefault(decision => !decision.Allowed);
}

/// <summary>What the guard would decide about a group of requests (a Compare run's columns) if it were reserved now, all or none
/// (<see cref="ISpendLedger.CheckGroupAsync"/>, <see cref="ISpendGuard.PreflightGroupAsync"/>): nothing is reserved. Each request is checked
/// as the group reservation checks it, with the group's earlier requests that fit counted, so a run, day or month cap that only the group's
/// total crosses refuses the request that crosses it (its cause's <see cref="SpendCause.GroupMicros"/> says how much the others add).</summary>
/// <param name="Items">One decision per request, in order.</param>
public sealed record SpendGroupDecision(IReadOnlyList<SpendDecision> Items)
{
    /// <summary>Whether the group would be reserved: every request fits.</summary>
    [JsonIgnore]
    public bool Allowed => Items.Count > 0 && Items.All(item => item.Allowed);

    /// <summary>The decision that would refuse the group: the first that blocks, else the first that asks; null when it fits.</summary>
    [JsonIgnore]
    public SpendDecision? Refused => Items.FirstOrDefault(item => item.Verdict == SpendVerdict.Block) ?? Items.FirstOrDefault(item => !item.Allowed);
}
