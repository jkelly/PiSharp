// Decision 0011 (run W3, S0): the Spend Guard's ledger, the contract of the Ledger unit (webagent.db, migrations 1000-1999), which copies
// BudgetLedger's rules (src/PiSharp.Durable.Hosting/Storage/BudgetLedger.cs), not the class. One row per provider request, keyed by its op:
// reserved (the worst case is held against every cap it falls under) -> settled (the actual cost; or the estimate, when it was cancelled
// after output) | released (nothing counts) | uncertain (the stream dropped without usage: the reserved amount keeps counting until the user
// resolves it, never automatically). Reserve checks every applicable cap and writes under one lock and one SQLite BEGIN IMMEDIATE
// transaction, so concurrent requests can never overshoot a cap; a group reserves all or nothing; the same op never reserves twice. Settle,
// settle-at-estimate, release and mark-uncertain change only a reserved row (the first ending wins; later ones return the row as it is);
// an uncertain row changes only by Resolve, except that the usage a recovered request's resend reported may be attached to it (W3 fix:
// ReportUsageAsync), so Resolve can offer "settle at the reported cost"; it stays uncertain and counted until the user chooses. Subscription
// rows are recorded at list price and free rows at 0, and neither counts toward money caps; an unknown-price row records tokens without a
// cost.
using System.Text.Json.Serialization;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Spend;

/// <summary>A reservation's state. Sent as <c>reserved</c>, <c>settled</c>, <c>released</c>, <c>uncertain</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReservationState>))]
public enum ReservationState
{
    /// <summary>The worst case is held while the request runs.</summary>
    [JsonStringEnumMemberName("reserved")] Reserved,

    /// <summary>Ended at a known amount (<see cref="Reservation.Settlement"/> says how it was found).</summary>
    [JsonStringEnumMemberName("settled")] Settled,

    /// <summary>Ended without cost: the request failed or was cancelled before any output, or the user released it.</summary>
    [JsonStringEnumMemberName("released")] Released,

    /// <summary>The stream ended without reporting usage; the reserved amount counts until the user resolves it.</summary>
    [JsonStringEnumMemberName("uncertain")] Uncertain,
}

/// <summary>How a settled amount was found. Sent as <c>actual</c>, <c>estimate</c>, <c>entered</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettlementKind>))]
public enum SettlementKind
{
    /// <summary>The cost the provider reported (<see cref="SpendUsage"/>).</summary>
    [JsonStringEnumMemberName("actual")] Actual,

    /// <summary>An estimate from what was received (input estimate + output tokens seen): a request cancelled after output, or an uncertain
    /// one the user settled at the estimate.</summary>
    [JsonStringEnumMemberName("estimate")] Estimate,

    /// <summary>An amount the user entered when resolving an uncertain reservation.</summary>
    [JsonStringEnumMemberName("entered")] Entered,
}

/// <summary>The status words of the ledger's Status column and filter. Sent as <c>reserved</c>, <c>settled</c>, <c>estimate</c>,
/// <c>uncertain</c>, <c>released</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LedgerStatus>))]
public enum LedgerStatus
{
    /// <summary>Still running.</summary>
    [JsonStringEnumMemberName("reserved")] Reserved,

    /// <summary>Settled at the actual cost or an entered amount.</summary>
    [JsonStringEnumMemberName("settled")] Settled,

    /// <summary>Settled at an estimate.</summary>
    [JsonStringEnumMemberName("estimate")] Estimate,

    /// <summary>Uncertain, waiting for the user.</summary>
    [JsonStringEnumMemberName("uncertain")] Uncertain,

    /// <summary>Released.</summary>
    [JsonStringEnumMemberName("released")] Released,
}

/// <summary>One ledger row: a provider request's reservation and how it ended.</summary>
public sealed record Reservation
{
    /// <summary>The op: the request's idempotent operation key and the row's id.</summary>
    public required string Op { get; init; }

    /// <summary>The provider id.</summary>
    public required string Provider { get; init; }

    /// <summary>The model id.</summary>
    public required string Model { get; init; }

    /// <summary>The account id, or null when the provider needs none.</summary>
    public string? Account { get; init; }

    /// <summary>Where the request was started.</summary>
    public required SpendSource Source { get; init; }

    /// <summary>The run key (a chat id, a run id), or null.</summary>
    public string? Run { get; init; }

    /// <summary>How it was billed when it was reserved (written with <c>SettingsStore.JsonOptions</c>: <c>metered</c>, ...).</summary>
    public required BillingKind Billing { get; init; }

    /// <summary>The state.</summary>
    public required ReservationState State { get; init; }

    /// <summary>The worst case reserved, in micro-USD (list price for a subscription, 0 for free); null when the price was unknown.</summary>
    public long? ReservedMicros { get; init; }

    /// <summary>The settled amount, in micro-USD; null until settled, when released, and when the price is unknown.</summary>
    public long? ActualMicros { get; init; }

    /// <summary>An estimate from what was received before the request ended (for settle-at-estimate), in micro-USD; null when none was made.</summary>
    public long? EstimateMicros { get; init; }

    /// <summary>How <see cref="ActualMicros"/> was found; null unless settled.</summary>
    public SettlementKind? Settlement { get; init; }

    /// <summary>Whether the user resolved it (from uncertain).</summary>
    public bool ResolvedByUser { get; init; }

    /// <summary>The input tokens the reservation assumed.</summary>
    public double InputTokens { get; init; }

    /// <summary>The output tokens the reservation assumed.</summary>
    public int OutputTokens { get; init; }

    /// <summary>The rates the reservation used; null when the price was unknown.</summary>
    public SpendPrice? Price { get; init; }

    /// <summary>What the provider reported, once it did; null otherwise.</summary>
    public SpendUsage? Usage { get; init; }

    /// <summary>The note given when it was reserved (<see cref="SpendRequest.Note"/>), or null.</summary>
    public string? Note { get; init; }

    /// <summary>Why it became uncertain or was released (<c>stream dropped, no usage reported</c>, <c>HTTP 429 · 3 attempts</c>), or null.</summary>
    public string? Reason { get; init; }

    /// <summary>When it was reserved.</summary>
    public required DateTimeOffset Created { get; init; }

    /// <summary>When it left <see cref="ReservationState.Reserved"/> (and when it was resolved, for a resolved one); null while reserved.</summary>
    public DateTimeOffset? Ended { get; init; }

    /// <summary>Whether it counts toward money caps: metered with a known price. Subscription, free and unknown-price rows never do.</summary>
    [JsonIgnore]
    public bool CountsTowardCaps => Billing == BillingKind.Metered && ReservedMicros is not null;

    /// <summary>What counts toward money caps now, in micro-USD: the reserved amount while reserved or uncertain, the settled amount once
    /// settled, nothing once released; always 0 for a row that does not count.</summary>
    [JsonIgnore]
    public long CountedMicros => !CountsTowardCaps ? 0 : State switch
    {
        ReservationState.Reserved or ReservationState.Uncertain => ReservedMicros ?? 0,
        ReservationState.Settled => ActualMicros ?? ReservedMicros ?? 0,
        _ => 0,
    };

    /// <summary>The part of <see cref="CountedMicros"/> that is uncertain.</summary>
    [JsonIgnore]
    public long UncertainMicros => State == ReservationState.Uncertain ? CountedMicros : 0;

    /// <summary>A subscription row's list price, shown as "≈ $x list" and never counted: settled, else reserved; 0 for other rows and once
    /// released.</summary>
    [JsonIgnore]
    public long ListMicros => Billing != BillingKind.Subscription ? 0 : State switch
    {
        ReservationState.Settled => ActualMicros ?? 0,
        ReservationState.Released => 0,
        _ => ReservedMicros ?? 0,
    };

    /// <summary>What the usage a provider reported for an uncertain row (a recovered request's resend: <see cref="ISpendLedger.ReportUsageAsync"/>)
    /// would settle it at, in micro-USD: the cost "settle at the reported cost" offers; null when nothing was reported, the row is not
    /// uncertain, or its price is unknown.</summary>
    [JsonIgnore]
    public long? ReportedMicros => State == ReservationState.Uncertain && Usage is { } usage ? CostOf(usage) : null;

    /// <summary>What <paramref name="usage"/> costs on this row: $0 when free; none when the price is unknown; at the entered price for an
    /// entered one; else the cost the provider layer reported, or the row's rates x its tokens when it reported none.</summary>
    public long? CostOf(SpendUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (Billing == BillingKind.Free) return 0;
        if (Price is not { } price || ReservedMicros is null) return null;
        if (price.Source == SpendPriceSource.Entered) return price.CostMicros(usage);
        return usage.CostMicros is > 0 ? usage.CostMicros : price.CostMicros(usage);
    }

    /// <summary>The ledger's status word: <see cref="LedgerStatus.Estimate"/> for a row settled at an estimate, else its state.</summary>
    [JsonIgnore]
    public LedgerStatus Status => State switch
    {
        ReservationState.Reserved => LedgerStatus.Reserved,
        ReservationState.Uncertain => LedgerStatus.Uncertain,
        ReservationState.Released => LedgerStatus.Released,
        _ => Settlement == SettlementKind.Estimate ? LedgerStatus.Estimate : LedgerStatus.Settled,
    };
}

/// <summary>How the user resolves an uncertain reservation. Sent as <c>estimate</c>, <c>amount</c>, <c>release</c>, <c>reported</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResolveChoice>))]
public enum ResolveChoice
{
    /// <summary>Settle at the estimate (<see cref="Reservation.EstimateMicros"/>, else the reserved amount).</summary>
    [JsonStringEnumMemberName("estimate")] Estimate,

    /// <summary>Settle at an amount the user entered (from the provider's usage or billing page).</summary>
    [JsonStringEnumMemberName("amount")] Amount,

    /// <summary>Stop counting it.</summary>
    [JsonStringEnumMemberName("release")] Release,

    /// <summary>Settle at the cost of the usage the provider reported for it (<see cref="Reservation.ReportedMicros"/>: a recovered
    /// request's resend), as an actual settlement.</summary>
    [JsonStringEnumMemberName("reported")] Reported,
}

/// <summary>The user's resolution of an uncertain reservation.</summary>
/// <param name="Choice">Settle at the estimate, at an entered amount, or release.</param>
/// <param name="AmountMicros">The entered amount, for <see cref="ResolveChoice.Amount"/>.</param>
public sealed record SpendResolution(ResolveChoice Choice, long? AmountMicros = null)
{
    /// <summary>The same resolution, checked.</summary>
    /// <exception cref="ArgumentException">An amount is missing, negative or too large, or given with another choice.</exception>
    public SpendResolution Normalize()
    {
        if (!Enum.IsDefined(Choice)) throw new ArgumentException($"Unknown resolution {(int)Choice}.");
        if (Choice != ResolveChoice.Amount) return AmountMicros is null ? this : throw new ArgumentException("Only an entered amount takes an amount.");
        return AmountMicros switch
        {
            null => throw new ArgumentException("Enter the actual amount."),
            < 0 => throw new ArgumentException("The amount can't be negative."),
            > SpendMoney.MaxMicros => throw new ArgumentException($"The amount can be at most {SpendMoney.Format(SpendMoney.MaxMicros)}."),
            _ => this,
        };
    }
}

/// <summary>Which ledger rows to list or total. Every filter is optional; rows are newest first.</summary>
public sealed record SpendLedgerQuery
{
    /// <summary>The most rows one page may hold.</summary>
    public const int MaxLimit = 1000;

    /// <summary>Rows reserved at or after this time (UTC).</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Rows reserved before this time (UTC).</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>One provider.</summary>
    public string? Provider { get; init; }

    /// <summary>One model (of <see cref="Provider"/>).</summary>
    public string? Model { get; init; }

    /// <summary>One account id.</summary>
    public string? Account { get; init; }

    /// <summary>One source.</summary>
    public SpendSource? Source { get; init; }

    /// <summary>One status.</summary>
    public LedgerStatus? Status { get; init; }

    /// <summary>One billing mode.</summary>
    public BillingKind? Billing { get; init; }

    /// <summary>One run key (a chat's rows).</summary>
    public string? Run { get; init; }

    /// <summary>Text found (ignoring case) in the provider, model, account, op, run, source, note or reason.</summary>
    public string? Search { get; init; }

    /// <summary>Leaves out the rows of these run keys, and the rows whose op is <c>run-&lt;key&gt;</c> (W5: the keys of blind Arena battles,
    /// which a filter that could single out one model must not match before the vote), or none when null.</summary>
    public IReadOnlyCollection<string>? ExceptRuns { get; init; }

    /// <summary>Rows to skip.</summary>
    public int Offset { get; init; }

    /// <summary>Rows per page (the pages offer 25, 50 and 100), 1 to <see cref="MaxLimit"/>.</summary>
    public int Limit { get; init; } = 25;

    /// <summary>The same query, trimmed and checked.</summary>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public SpendLedgerQuery Normalize()
    {
        if (Offset < 0) throw new ArgumentException("The offset is never negative.");
        if (Limit is < 1 or > MaxLimit) throw new ArgumentException($"Show 1 to {MaxLimit} rows per page.");
        if (From is { } from && To is { } to && to < from) throw new ArgumentException("The end of the date range is before its start.");
        if (Source is { } source && !Enum.IsDefined(source)) throw new ArgumentException($"Unknown source {(int)source}.");
        if (Status is { } status && !Enum.IsDefined(status)) throw new ArgumentException($"Unknown status {(int)status}.");
        return this with
        {
            Provider = Blank(Provider), Model = Blank(Model), Account = Blank(Account), Run = Blank(Run), Search = Blank(Search),
            From = From?.ToUniversalTime(), To = To?.ToUniversalTime(),
        };
    }

    /// <summary>Whether <paramref name="row"/> matches every filter (paging aside): the reference rule a store's SQL must agree with.</summary>
    public bool Matches(Reservation row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (From is { } from && row.Created < from) return false;
        if (To is { } to && row.Created >= to) return false;
        if (Provider is not null && row.Provider != Provider) return false;
        if (Model is not null && row.Model != Model) return false;
        if (Account is not null && row.Account != Account) return false;
        if (Source is { } source && row.Source != source) return false;
        if (Status is { } status && row.Status != status) return false;
        if (Billing is { } billing && row.Billing != billing) return false;
        if (Run is not null && row.Run != Run) return false;
        if (ExceptRuns is { Count: > 0 } except && except.Any(key => key == row.Run || row.Op == "run-" + key || row.Op.StartsWith("run-" + key + "~", StringComparison.Ordinal)))
            return false;
        if (Search is { } search)
        {
            string?[] fields = [row.Provider, row.Model, row.Account, row.Op, row.Run, row.Source.Wire(), row.Note, row.Reason];
            if (!fields.Any(field => field?.Contains(search, StringComparison.OrdinalIgnoreCase) == true)) return false;
        }
        return true;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One page of ledger rows.</summary>
/// <param name="Items">The rows, newest first.</param>
/// <param name="Total">How many rows match the query.</param>
/// <param name="TotalAll">How many rows the ledger holds ("143 of 1,206 requests").</param>
public sealed record SpendLedgerPage(IReadOnlyList<Reservation> Items, int Total, int TotalAll);

/// <summary>What a set of ledger rows adds up to.</summary>
/// <param name="CountedMicros">What counts toward money caps (metered, known price), uncertain included.</param>
/// <param name="UncertainMicros">The uncertain part of <paramref name="CountedMicros"/>.</param>
/// <param name="ListMicros">Subscription use at list price, never counted.</param>
/// <param name="Requests">Rows.</param>
/// <param name="UnknownPriceRequests">Rows with an unknown price (tokens only, no cost).</param>
/// <param name="FreeRequests">Rows of free providers.</param>
public sealed record SpendTotal(long CountedMicros, long UncertainMicros, long ListMicros, int Requests, int UnknownPriceRequests, int FreeRequests)
{
    /// <summary>Nothing.</summary>
    public static SpendTotal Zero { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>The total of <paramref name="rows"/>: the reference rule a store's SQL must agree with.</summary>
    public static SpendTotal Of(IEnumerable<Reservation> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        long counted = 0, uncertain = 0, list = 0;
        int requests = 0, unknown = 0, free = 0;
        foreach (var row in rows)
        {
            counted += row.CountedMicros;
            uncertain += row.UncertainMicros;
            list += row.ListMicros;
            requests++;
            if (row.Billing == BillingKind.Unknown || (row.Billing == BillingKind.Metered && row.ReservedMicros is null)) unknown++;
            if (row.Billing == BillingKind.Free) free++;
        }
        return new SpendTotal(counted, uncertain, list, requests, unknown, free);
    }
}

/// <summary>The prompt cache meter of a UTC month: saved = cache-read tokens x (input rate - cache-read rate); wasted = <c>CacheStats</c>'s
/// miss cost plus cache-warm spend.</summary>
/// <param name="SavedMicros">What reading the cache saved, in micro-USD.</param>
/// <param name="WastedMicros">What cache writes that were never read, and cache-warm requests, cost, in micro-USD.</param>
public sealed record SpendCacheMeter(long SavedMicros, long WastedMicros);

/// <summary>The Spend Guard's ledger and caps (the Ledger unit). A singleton; safe to call from many threads and processes' restarts.</summary>
public interface ISpendLedger
{
    /// <summary>Every cap, enabled or not, in the ledger's order.</summary>
    Task<IReadOnlyList<SpendCap>> CapsAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds <paramref name="cap"/> (when its <see cref="SpendCap.Id"/> is 0) or changes the stored cap's limit, action and enabled
    /// flag (its scope, key and window never change: add a new cap instead). Returns the stored cap.</summary>
    /// <exception cref="ArgumentException">A value is not allowed (<see cref="SpendCap.Normalize"/>), or an edit changes the target.</exception>
    /// <exception cref="SpendException">No cap has that id (404).</exception>
    Task<SpendCap> SaveCapAsync(SpendCap cap, CancellationToken cancellationToken = default);

    /// <summary>Removes cap <paramref name="id"/>; false when there was none. The rows it counted stay in the ledger.</summary>
    Task<bool> RemoveCapAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Every cap with what counts toward it now: day and month caps over their current UTC window, run caps over
    /// <paramref name="run"/> (no amount when it is null), request caps with no amount.</summary>
    Task<IReadOnlyList<SpendCapStatus>> CapStatusAsync(string? run = null, CancellationToken cancellationToken = default);

    /// <summary>What <see cref="ReserveAsync"/> would decide now, without reserving anything (the preflight).</summary>
    Task<SpendDecision> CheckAsync(SpendRequest request, CancellationToken cancellationToken = default);

    /// <summary>Checks every enabled cap <paramref name="request"/> falls under and, when it fits (or the user approved its Ask causes),
    /// reserves its worst case, all under one lock and transaction. An op that already has a reservation returns it unchanged
    /// (<see cref="SpendReservation.Existing"/>), whatever its state.</summary>
    /// <exception cref="ArgumentException"><paramref name="request"/> is not allowed (<see cref="SpendRequest.Normalize"/>).</exception>
    Task<SpendReservation> ReserveAsync(SpendRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reserves every request of <paramref name="requests"/> or none: each is checked with the others already counted. Ops already
    /// reserved are returned unchanged and count as fitting.</summary>
    /// <exception cref="ArgumentException">A request is not allowed, or two share an op.</exception>
    Task<SpendGroupReservation> ReserveGroupAsync(IReadOnlyList<SpendRequest> requests, CancellationToken cancellationToken = default);

    /// <summary>What <see cref="ReserveGroupAsync"/> would decide now, without reserving anything (a group's preflight): each request checked
    /// with the group's earlier requests that fit counted, so a run, day or month cap that only the group's total crosses refuses the request
    /// that crosses it. Ops already reserved count as fitting. A ledger that does not count a group (this default, for test fakes) checks each
    /// request alone, as <see cref="CheckAsync"/>.</summary>
    /// <exception cref="ArgumentException">A request is not allowed, or two share an op.</exception>
    async Task<SpendGroupDecision> CheckGroupAsync(IReadOnlyList<SpendRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0) throw new ArgumentException("A group needs at least one request.", nameof(requests));
        var decisions = new List<SpendDecision>();
        foreach (var request in requests) decisions.Add(await CheckAsync(request, cancellationToken).ConfigureAwait(false));
        return new SpendGroupDecision(decisions);
    }

    /// <summary>The row of <paramref name="op"/>, or null.</summary>
    Task<Reservation?> GetAsync(string op, CancellationToken cancellationToken = default);

    /// <summary>Settles a reserved row at what the provider reported (its cost; or the entered price x its tokens; no cost for an unknown
    /// price). A row that is not reserved is returned as it is.</summary>
    /// <exception cref="SpendException">No row has that op (404).</exception>
    Task<Reservation> SettleAsync(string op, SpendUsage usage, CancellationToken cancellationToken = default);

    /// <summary>Settles a reserved row at <paramref name="estimateMicros"/> (a request cancelled after output). A row that is not reserved
    /// is returned as it is.</summary>
    /// <exception cref="SpendException">No row has that op (404).</exception>
    Task<Reservation> SettleAtEstimateAsync(string op, long estimateMicros, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>Releases a reserved row (an error or cancel before any output). A row that is not reserved is returned as it is.</summary>
    /// <exception cref="SpendException">No row has that op (404).</exception>
    Task<Reservation> ReleaseAsync(string op, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>Marks a reserved row uncertain with <paramref name="reason"/> (and the estimate from what was received, if any); it keeps
    /// counting. A row that is not reserved is returned as it is.</summary>
    /// <exception cref="SpendException">No row has that op (404).</exception>
    Task<Reservation> MarkUncertainAsync(string op, string reason, long? estimateMicros = null, CancellationToken cancellationToken = default);

    /// <summary>Resolves an uncertain row as the user chose: settled at the estimate, an entered amount or the reported cost, or released.</summary>
    /// <exception cref="ArgumentException"><paramref name="resolution"/> is not allowed (<see cref="SpendResolution.Normalize"/>), or it
    /// settles at the reported cost of a row nothing was reported for.</exception>
    /// <exception cref="SpendException">No row has that op (404), or it is not uncertain (409).</exception>
    Task<Reservation> ResolveAsync(string op, SpendResolution resolution, CancellationToken cancellationToken = default);

    /// <summary>Attaches what the provider reported (<paramref name="usage"/>) to an uncertain row: a recovered request's resend that reused
    /// it. The row stays uncertain and keeps counting at its reserved amount until the user resolves it (Resolve then offers the reported
    /// cost, <see cref="Reservation.ReportedMicros"/>). A row that is not uncertain is returned as it is; so is every row of a ledger that keeps
    /// no reported usage (this default).</summary>
    /// <exception cref="SpendException">No row has that op (404).</exception>
    async Task<Reservation> ReportUsageAsync(string op, SpendUsage usage, CancellationToken cancellationToken = default) =>
        await GetAsync(op, cancellationToken).ConfigureAwait(false) ?? throw SpendException.NotFound($"There is no reservation {op}.");

    /// <summary>Marks every row still reserved as uncertain with <paramref name="reason"/> (at start: those requests' process is gone);
    /// returns how many. Never settles one.</summary>
    Task<int> RecoverAsync(string reason, CancellationToken cancellationToken = default);

    /// <summary>One page of the rows <paramref name="query"/> matches, newest first.</summary>
    Task<SpendLedgerPage> QueryAsync(SpendLedgerQuery query, CancellationToken cancellationToken = default);

    /// <summary>The total of every row <paramref name="query"/> matches (paging ignored; <see cref="SpendTotal.Of"/>).</summary>
    Task<SpendTotal> TotalAsync(SpendLedgerQuery query, CancellationToken cancellationToken = default);

    /// <summary>The settled costs (actual settlements only, newest first) of the last <paramref name="count"/> requests of a provider and
    /// model, in micro-USD: what "typical" is the median of.</summary>
    Task<IReadOnlyList<long>> RecentActualsAsync(string provider, string model, int count = 20, CancellationToken cancellationToken = default);

    /// <summary>The providers of the rows that match <paramref name="query"/> (its paging left out), by id: what History › Spend's
    /// Provider filter offers.</summary>
    Task<IReadOnlyList<string>> ProvidersAsync(SpendLedgerQuery query, CancellationToken cancellationToken = default);

    /// <summary>The prompt cache meter of the UTC month that holds <paramref name="month"/>, for every provider or one.</summary>
    Task<SpendCacheMeter> CacheMeterAsync(DateTimeOffset month, string? provider = null, CancellationToken cancellationToken = default);
}
