// Decision 0011 (run W3, S2): the Spend Guard's ledger and caps over webagent.db (ISpendLedger), copying BudgetLedger's rules
// (src/PiSharp.Durable.Hosting/Storage/BudgetLedger.cs), not the class. Every write takes one SemaphoreSlim and one SQLite BEGIN IMMEDIATE
// transaction, so the checks and the write cannot interleave with another request, in this process or another; reads use a deferred
// transaction (one snapshot) and no lock. Reserve checks every enabled cap the request falls under (request, run, UTC day, UTC month; for
// everything metered, a provider, an account or a source) against what already counts in its window plus the request's worst case, and
// writes the row only when it fits every Block cap and every Ask cap was approved; reaching the limit exactly fits. A group reserves all
// or nothing, each request checked with the group's earlier ones counted; CheckGroupAsync answers what that would decide by the same rule,
// writing nothing (W4 fix: Compare's estimate by Run all sees a cap only the group's total crosses; a cause names what the group's earlier
// requests add, GroupMicros). An op already in the ledger is returned unchanged, whatever its state, so nothing is reserved twice. Settle,
// settle-at-estimate, release and mark-uncertain change only a reserved row (the first ending wins); an uncertain row keeps counting until
// the user resolves it (W3 fix: a recovered request's resend may attach the usage it reported, which leaves it uncertain and lets Resolve
// settle it at that cost). Subscription rows are recorded at list price, free rows at $0 and
// unknown-price rows without a cost; none of them counts toward a money cap, and a request whose price is unknown gets the provider's
// unknown-price behaviour instead (Ask by default). Caps at the warning threshold (SpendSettings.WarnPercent) come back as warnings.
//
// Startup recovery: rows a previous process left reserved can never be settled (that process's streams are gone), so the first call of a
// new ledger, or SpendRecovery at start if it comes first, marks every row still reserved as uncertain ("stream dropped, no usage
// reported") before anything else runs, never settled. Because it runs before this process's first reservation, a recovered durable
// request that reuses its op finds the uncertain row, and the hosted services' start order does not matter. Every row still reserved belongs
// to a process that is gone because the app registers this ledger only behind its data folder's lock (WebAgentDataLock, W3 fix 2): no
// other live app can share the folder.
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Spend;

/// <summary>The Spend Guard's ledger and caps in webagent.db. A singleton; safe to call from many threads, and from several processes on
/// one database (the BEGIN IMMEDIATE transaction serializes writers).</summary>
public sealed partial class SpendLedger : ISpendLedger
{
    /// <summary>The reason startup recovery gives a row a previous process left reserved.</summary>
    public const string RecoveryReason = "stream dropped, no usage reported";

    /// <summary>The longest reason kept; longer ones are cut.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>The most settled costs <see cref="RecentActualsAsync"/> returns.</summary>
    public const int MaxRecentActuals = 1000;

    private readonly WebAgentDb db;
    private readonly ISettingsStore settings;
    private readonly TimeProvider clock;
    private readonly ILogger log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile bool started;
    private int startupRecovered;

    /// <summary>The ledger in <paramref name="db"/> (which must hold <see cref="SpendLedgerMigrations.All"/>), with the warning threshold
    /// and unknown-price behaviour of <paramref name="settings"/>' <see cref="SpendSettings"/>.</summary>
    /// <param name="db">webagent.db.</param>
    /// <param name="settings">Where <see cref="SpendSettings"/> is read, at each check.</param>
    /// <param name="time">The clock of UTC days and months; the system clock when null.</param>
    /// <param name="log">Where startup recovery is reported, or null.</param>
    public SpendLedger(WebAgentDb db, ISettingsStore settings, TimeProvider? time = null, ILogger<SpendLedger>? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(settings);
        this.db = db;
        this.settings = settings;
        clock = time ?? TimeProvider.System;
        this.log = log ?? NullLogger<SpendLedger>.Instance;
    }

    /// <summary>Whether startup recovery has run in this ledger.</summary>
    public bool Started => started;

    /// <summary>Raised after a row's ending changed (settled, released, made uncertain, resolved, or given the usage a resend reported), with
    /// the row as it is now, outside the lock (W3 fix: the chat pages refresh that run's message footers, so a Resolve on History › Spend
    /// shows in an open chat). Startup recovery raises nothing (no page is open yet). A handler must not throw.</summary>
    public event Action<Reservation>? Changed;

    /// <summary>Runs startup recovery if it has not run yet (<see cref="SpendRecovery"/> calls it at start; any other call runs it first
    /// too) and returns how many rows it made uncertain. Calling it again changes nothing and returns the same count.</summary>
    public async Task<int> RecoverAtStartAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return startupRecovered;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SpendCap>> CapsAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<SpendCap>>((connection, transaction) => ReadCaps(connection, transaction), cancellationToken);

    /// <inheritdoc/>
    /// <exception cref="SpendException">No cap has that id (404), or a new cap's target already has one (409: edit that cap instead).</exception>
    public Task<SpendCap> SaveCapAsync(SpendCap cap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cap);
        var normalized = cap.Normalize();
        return WriteAsync((connection, transaction, now) =>
        {
            var at = now.ToUnixTimeMilliseconds();
            if (normalized.Id == 0)
            {
                if (ReadCaps(connection, transaction).FirstOrDefault(other => other.SameTarget(normalized)) is { } same)
                    throw SpendException.Conflict($"{CapName(same)} already exists (cap {same.Id}): edit it instead.");
                WebAgentDb.Execute(connection, transaction, """
                    INSERT INTO spend_caps (scope, key, period, limit_micros, action, enabled, created_at, updated_at)
                    VALUES ($scope, $key, $period, $limit, $action, $enabled, $at, $at);
                    """, ("$scope", Wire(normalized.Scope)), ("$key", normalized.Key), ("$period", Wire(normalized.Period)),
                    ("$limit", normalized.LimitMicros), ("$action", Wire(normalized.Action)), ("$enabled", normalized.Enabled ? 1L : 0L), ("$at", at));
                var id = Convert.ToInt64(WebAgentDb.Scalar(connection, transaction, "SELECT last_insert_rowid();"), CultureInfo.InvariantCulture);
                return normalized with { Id = id };
            }
            var stored = ReadCaps(connection, transaction).FirstOrDefault(other => other.Id == normalized.Id)
                ?? throw SpendException.NotFound($"There is no cap {normalized.Id}.");
            if (!stored.SameTarget(normalized))
                throw new ArgumentException("A cap's scope and window never change: add a new cap instead.");
            WebAgentDb.Execute(connection, transaction, "UPDATE spend_caps SET limit_micros = $limit, action = $action, enabled = $enabled, updated_at = $at WHERE id = $id;",
                ("$limit", normalized.LimitMicros), ("$action", Wire(normalized.Action)), ("$enabled", normalized.Enabled ? 1L : 0L), ("$at", at), ("$id", normalized.Id));
            return stored with { LimitMicros = normalized.LimitMicros, Action = normalized.Action, Enabled = normalized.Enabled };
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> RemoveCapAsync(long id, CancellationToken cancellationToken = default) =>
        WriteAsync((connection, transaction, _) => WebAgentDb.Execute(connection, transaction, "DELETE FROM spend_caps WHERE id = $id;", ("$id", id)) > 0,
            cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<SpendCapStatus>> CapStatusAsync(string? run = null, CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(run) ? null : run.Trim();
        return ReadAsync<IReadOnlyList<SpendCapStatus>>((connection, transaction) =>
        {
            var now = Now();
            var warn = settings.Get<SpendSettings>().WarnPercent;
            var statuses = new List<SpendCapStatus>();
            foreach (var cap in ReadCaps(connection, transaction))
            {
                var window = SpendWindows.Of(cap.Period, now);
                long? spent = cap.Period switch
                {
                    CapPeriod.Request => null,
                    CapPeriod.Run => key is null ? null : Spent(connection, transaction, cap, key, null),
                    _ => Spent(connection, transaction, cap, null, window),
                };
                statuses.Add(new SpendCapStatus(cap, spent, window?.Start, window?.End, warn));
            }
            return statuses;
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<SpendDecision> CheckAsync(SpendRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = request.Normalize();
        return ReadAsync((connection, transaction) =>
        {
            if (Find(connection, transaction, normalized.Op) is not null) return SpendDecision.Allow(normalized.Estimate);
            return Decide(connection, transaction, normalized, ReadCaps(connection, transaction), settings.Get<SpendSettings>(), Now(), []);
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<SpendReservation> ReserveAsync(SpendRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = request.Normalize();
        return WriteAsync((connection, transaction, now) =>
        {
            if (Find(connection, transaction, normalized.Op) is { } existing)
                return new SpendReservation(SpendDecision.Allow(normalized.Estimate), existing, Existing: true);
            var decision = Decide(connection, transaction, normalized, ReadCaps(connection, transaction), settings.Get<SpendSettings>(), now, []);
            if (!decision.Allowed) return new SpendReservation(decision, null);
            var row = NewRow(normalized, now);
            Insert(connection, transaction, row);
            return new SpendReservation(decision, row);
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<SpendGroupReservation> ReserveGroupAsync(IReadOnlyList<SpendRequest> requests, CancellationToken cancellationToken = default)
    {
        var normalized = Group(requests);
        return WriteAsync((connection, transaction, now) =>
        {
            var (items, pending) = DecideGroup(connection, transaction, normalized, now);
            // All or nothing: when one request does not fit, nothing is written and no item keeps a new reservation.
            if (items.Any(item => !item.Decision.Allowed))
                return new SpendGroupReservation([.. items.Select(item => item.Existing ? item : item with { Reservation = null })]);
            foreach (var row in pending) Insert(connection, transaction, row);
            return new SpendGroupReservation(items);
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<SpendGroupDecision> CheckGroupAsync(IReadOnlyList<SpendRequest> requests, CancellationToken cancellationToken = default)
    {
        var normalized = Group(requests);
        return ReadAsync((connection, transaction) =>
            new SpendGroupDecision([.. DecideGroup(connection, transaction, normalized, Now()).Items.Select(item => item.Decision)]), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<Reservation?> GetAsync(string op, CancellationToken cancellationToken = default)
    {
        var key = Key(op);
        return ReadAsync((connection, transaction) => Find(connection, transaction, key), cancellationToken);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">A token count or cost is negative or not a finite number.</exception>
    public Task<Reservation> SettleAsync(string op, SpendUsage usage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usage);
        CheckUsage(usage);
        return ChangeAsync(op, (row, now) => row.State != ReservationState.Reserved ? null
            : row with { State = ReservationState.Settled, ActualMicros = ActualCost(row, usage), Settlement = SettlementKind.Actual, Usage = usage, Ended = now },
            cancellationToken);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="estimateMicros"/> is negative.</exception>
    public Task<Reservation> SettleAtEstimateAsync(string op, long estimateMicros, string? reason = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimateMicros);
        return ChangeAsync(op, (row, now) => row.State != ReservationState.Reserved ? null : row with
        {
            State = ReservationState.Settled,
            ActualMicros = CostOrNone(row, estimateMicros),
            EstimateMicros = CostOrNone(row, estimateMicros),
            Settlement = SettlementKind.Estimate,
            Reason = Reason(reason) ?? row.Reason,
            Ended = now,
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<Reservation> ReleaseAsync(string op, string? reason = null, CancellationToken cancellationToken = default) =>
        ChangeAsync(op, (row, now) => row.State != ReservationState.Reserved ? null
            : row with { State = ReservationState.Released, Reason = Reason(reason) ?? row.Reason, Ended = now }, cancellationToken);

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is blank, or <paramref name="estimateMicros"/> is negative.</exception>
    public Task<Reservation> MarkUncertainAsync(string op, string reason, long? estimateMicros = null, CancellationToken cancellationToken = default)
    {
        var why = Reason(reason) ?? throw new ArgumentException("Say why the reservation is uncertain.", nameof(reason));
        if (estimateMicros is < 0) throw new ArgumentOutOfRangeException(nameof(estimateMicros), "An estimate is never negative.");
        return ChangeAsync(op, (row, now) => row.State != ReservationState.Reserved ? null : row with
        {
            State = ReservationState.Uncertain,
            EstimateMicros = estimateMicros is { } estimate ? CostOrNone(row, estimate) : row.EstimateMicros,
            Reason = why,
            Ended = now,
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<Reservation> ResolveAsync(string op, SpendResolution resolution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var choice = resolution.Normalize();
        return ChangeAsync(op, (row, now) =>
        {
            if (row.State != ReservationState.Uncertain)
                throw SpendException.Conflict($"Reservation {row.Op} is {Wire(row.Status)}, not uncertain, so there is nothing to resolve.");
            var resolved = choice.Choice switch
            {
                ResolveChoice.Estimate => row with
                {
                    State = ReservationState.Settled,
                    ActualMicros = row.EstimateMicros ?? row.ReservedMicros,
                    Settlement = SettlementKind.Estimate,
                },
                ResolveChoice.Amount => row with { State = ReservationState.Settled, ActualMicros = choice.AmountMicros, Settlement = SettlementKind.Entered },
                ResolveChoice.Reported => row.Usage is { } usage
                    ? row with { State = ReservationState.Settled, ActualMicros = row.CostOf(usage), Settlement = SettlementKind.Actual }
                    : throw new ArgumentException($"Nothing was reported for reservation {row.Op}: settle it at the estimate or an amount instead."),
                _ => row with { State = ReservationState.Released },
            };
            return resolved with { ResolvedByUser = true, Ended = now };
        }, cancellationToken);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">A token count or cost is negative or not a finite number.</exception>
    public Task<Reservation> ReportUsageAsync(string op, SpendUsage usage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usage);
        CheckUsage(usage);
        return ChangeAsync(op, (row, _) => row.State != ReservationState.Uncertain ? null : row with { Usage = usage }, cancellationToken);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is blank.</exception>
    public Task<int> RecoverAsync(string reason, CancellationToken cancellationToken = default)
    {
        var why = Reason(reason) ?? throw new ArgumentException("Say why the reservations are uncertain.", nameof(reason));
        return WriteAsync((connection, transaction, now) => Recover(connection, transaction, why, now), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<SpendLedgerPage> QueryAsync(SpendLedgerQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.Normalize();
        return ReadAsync((connection, transaction) =>
        {
            var (where, parameters) = Filter(normalized);
            var total = Convert.ToInt32(WebAgentDb.Scalar(connection, transaction, "SELECT COUNT(*) FROM spend_reservations" + where + ";", parameters) ?? 0L);
            var all = Convert.ToInt32(WebAgentDb.Scalar(connection, transaction, "SELECT COUNT(*) FROM spend_reservations;") ?? 0L);
            var items = Rows(connection, transaction, $"SELECT {Columns} FROM spend_reservations{where} ORDER BY created_at DESC, rowid DESC LIMIT $limit OFFSET $offset;",
                [.. parameters, ("$limit", (long)normalized.Limit), ("$offset", (long)normalized.Offset)]);
            return new SpendLedgerPage(items, total, all);
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<SpendTotal> TotalAsync(SpendLedgerQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.Normalize();
        return ReadAsync((connection, transaction) =>
        {
            var (where, parameters) = Filter(normalized);
            using var command = WebAgentDb.Command(connection, transaction, """
                SELECT COALESCE(SUM(counted_micros), 0), COALESCE(SUM(CASE WHEN state = 'uncertain' THEN counted_micros ELSE 0 END), 0),
                  COALESCE(SUM(list_micros), 0), COUNT(*),
                  COALESCE(SUM(CASE WHEN billing = 'unknown' OR (billing = 'metered' AND reserved_micros IS NULL) THEN 1 ELSE 0 END), 0),
                  COALESCE(SUM(CASE WHEN billing = 'free' THEN 1 ELSE 0 END), 0)
                FROM spend_reservations
                """ + where + ";", parameters);
            using var reader = command.ExecuteReader();
            reader.Read();
            return new SpendTotal(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5));
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> ProvidersAsync(SpendLedgerQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.Normalize();
        return ReadAsync<IReadOnlyList<string>>((connection, transaction) =>
        {
            var (where, parameters) = Filter(normalized);
            using var command = WebAgentDb.Command(connection, transaction, "SELECT DISTINCT provider FROM spend_reservations" + where + " ORDER BY provider;", parameters);
            using var reader = command.ExecuteReader();
            var providers = new List<string>();
            while (reader.Read()) if (!reader.IsDBNull(0)) providers.Add(reader.GetString(0));
            return providers;
        }, cancellationToken);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">The provider or model is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not 1 to <see cref="MaxRecentActuals"/>.</exception>
    public Task<IReadOnlyList<long>> RecentActualsAsync(string provider, string model, int count = 20, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Name a provider and a model.");
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxRecentActuals);
        return ReadAsync<IReadOnlyList<long>>((connection, transaction) =>
        {
            using var command = WebAgentDb.Command(connection, transaction, """
                SELECT actual_micros FROM spend_reservations
                WHERE provider = $provider AND model = $model AND state = 'settled' AND settlement = 'actual' AND actual_micros IS NOT NULL
                ORDER BY created_at DESC, rowid DESC LIMIT $count;
                """, ("$provider", provider.Trim()), ("$model", model.Trim()), ("$count", (long)count));
            using var reader = command.ExecuteReader();
            var costs = new List<long>();
            while (reader.Read()) costs.Add(reader.GetInt64(0));
            return costs;
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<SpendCacheMeter> CacheMeterAsync(DateTimeOffset month, string? provider = null, CancellationToken cancellationToken = default)
    {
        var (start, end) = SpendWindows.Of(CapPeriod.Month, month)!.Value;
        var key = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim();
        return ReadAsync((connection, transaction) =>
        {
            // Metered rows with a known price only (money actually paid); a cache-warm request's whole spend is waste.
            using var command = WebAgentDb.Command(connection, transaction, """
                SELECT COALESCE(SUM(cache_saved_micros), 0),
                  COALESCE(SUM(cache_missed_micros), 0) + COALESCE(SUM(CASE WHEN source = 'cache-warm' THEN counted_micros ELSE 0 END), 0)
                FROM spend_reservations
                WHERE counts = 1 AND created_at >= $from AND created_at < $to AND ($provider IS NULL OR provider = $provider);
                """, ("$from", start.ToUnixTimeMilliseconds()), ("$to", end.ToUnixTimeMilliseconds()), ("$provider", key));
            using var reader = command.ExecuteReader();
            reader.Read();
            return new SpendCacheMeter(reader.GetInt64(0), reader.GetInt64(1));
        }, cancellationToken);
    }

    /// <summary>The requests of a group, normalized and checked.</summary>
    /// <exception cref="ArgumentException">The group is empty, a request is missing or not allowed, or two share an op.</exception>
    private static List<SpendRequest> Group(IReadOnlyList<SpendRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0) throw new ArgumentException("A group needs at least one request.", nameof(requests));
        var normalized = requests.Select(request => (request ?? throw new ArgumentException("A request of the group is missing.", nameof(requests))).Normalize()).ToList();
        if (normalized.GroupBy(request => request.Op, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } shared)
            throw new ArgumentException($"Two requests of the group share the op {shared.Key}.", nameof(requests));
        return normalized;
    }

    /// <summary>The group's decisions, the one rule of <see cref="ReserveGroupAsync"/> and <see cref="CheckGroupAsync"/>: an op already in the
    /// ledger is returned unchanged (it fits); every other request is checked with the group's earlier requests that fit counted, and the
    /// rows those would be (not written here).</summary>
    private (List<SpendReservation> Items, List<Reservation> Pending) DecideGroup(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<SpendRequest> requests, DateTimeOffset now)
    {
        var caps = ReadCaps(connection, transaction);
        var spend = settings.Get<SpendSettings>();
        var pending = new List<Reservation>();
        var items = new List<SpendReservation>();
        foreach (var request in requests)
        {
            if (Find(connection, transaction, request.Op) is { } existing)
            {
                items.Add(new SpendReservation(SpendDecision.Allow(request.Estimate), existing, Existing: true));
                continue;
            }
            var decision = Decide(connection, transaction, request, caps, spend, now, pending);
            if (!decision.Allowed)
            {
                items.Add(new SpendReservation(decision, null));
                continue;
            }
            var row = NewRow(request, now);
            pending.Add(row);
            items.Add(new SpendReservation(decision, row));
        }
        return (items, pending);
    }

    /// <summary>The decision on <paramref name="request"/> with what counts now plus <paramref name="pending"/> (a group's earlier
    /// requests, not written yet; what they add to a cap the request does not fit is its cause's <see cref="SpendCause.GroupMicros"/>).</summary>
    private static SpendDecision Decide(SqliteConnection connection, SqliteTransaction transaction, SpendRequest request, IReadOnlyList<SpendCap> caps,
        SpendSettings spend, DateTimeOffset now, IReadOnlyList<Reservation> pending)
    {
        var estimate = request.Estimate;
        var causes = new List<SpendCause>();
        var warnings = new List<SpendCapStatus>();
        if (UnknownPrice(estimate))
        {
            // Nothing to check a cap against: the provider's unknown-price behaviour decides instead.
            var behavior = spend.UnknownPriceFor(estimate.Provider);
            if (behavior != UnknownPriceBehavior.Allow)
                causes.Add(new SpendCause { Kind = SpendCauseKind.UnknownPrice, Action = behavior == UnknownPriceBehavior.Block ? CapAction.Block : CapAction.Ask });
            return SpendDecision.From(estimate, causes, request.Approved);
        }
        if (!estimate.CountsTowardCaps) return SpendDecision.Allow(estimate);
        var worst = estimate.WorstMicros!.Value;
        foreach (var cap in caps)
        {
            if (!cap.Enabled || !cap.AppliesTo(request) || (cap.Period == CapPeriod.Run && request.Run is null)) continue;
            var window = SpendWindows.Of(cap.Period, now);
            var group = cap.Period == CapPeriod.Request ? 0 : pending.Where(row => CountsFor(cap, row, request.Run, window)).Sum(row => row.CountedMicros);
            var spent = cap.Period == CapPeriod.Request ? 0 : Spent(connection, transaction, cap, request.Run, window) + group;
            if (spent + worst > cap.LimitMicros)
            {
                var left = Math.Max(0, cap.LimitMicros - spent);
                causes.Add(new SpendCause
                {
                    Kind = SpendCauseKind.Cap, Action = cap.Action, Cap = cap, SpentMicros = spent, LeftMicros = left, FitOutputTokens = Fit(estimate, left),
                    GroupMicros = group > 0 ? group : null,
                });
                continue;
            }
            var status = new SpendCapStatus(cap, spent + worst, window?.Start, window?.End, spend.WarnPercent);
            if (status.Warning) warnings.Add(status);
        }
        return SpendDecision.From(estimate, causes, request.Approved, warnings);
    }

    /// <summary>Whether <paramref name="estimate"/> has no price to check caps against: billed as Unknown, or Metered without a known price.
    /// Subscription and free requests never are (they are not paid per token).</summary>
    private static bool UnknownPrice(SpendEstimate estimate) =>
        estimate.Billing == BillingKind.Unknown || (estimate.Billing == BillingKind.Metered && !estimate.PriceKnown);

    /// <summary>Whether the not-yet-written <paramref name="row"/> counts toward <paramref name="cap"/> in the window being checked.</summary>
    private static bool CountsFor(SpendCap cap, Reservation row, string? run, (DateTimeOffset Start, DateTimeOffset End)? window) =>
        row.CountsTowardCaps && cap.AppliesTo(row.Provider, row.Account, row.Source) && cap.Period switch
        {
            CapPeriod.Run => row.Run == run,
            CapPeriod.Day or CapPeriod.Month => window is { } span && row.Created >= span.Start && row.Created < span.End,
            _ => false,
        };

    /// <summary>The most output tokens with which <paramref name="estimate"/> fits <paramref name="left"/>, always fewer than it assumed
    /// (so capping the output really changes the request); null when none fits.</summary>
    private static int? Fit(SpendEstimate estimate, long left)
    {
        if (estimate.Price is not { } price || left <= 0 || price.OutputTokensWithin(estimate.InputTokens, left) is not { } tokens) return null;
        if (estimate.OutputTokens > 0 && tokens >= estimate.OutputTokens) tokens = estimate.OutputTokens - 1;
        return tokens >= 1 ? tokens : null;
    }

    /// <summary>The new reserved row of <paramref name="request"/>: its worst case (list price for a subscription, $0 for free, none for an
    /// unknown price).</summary>
    private static Reservation NewRow(SpendRequest request, DateTimeOffset now)
    {
        var estimate = request.Estimate;
        var unknown = UnknownPrice(estimate);
        return new Reservation
        {
            Op = request.Op,
            Provider = estimate.Provider,
            Model = estimate.Model,
            Account = estimate.Account,
            Source = request.Source,
            Run = request.Run,
            Billing = estimate.Billing,
            State = ReservationState.Reserved,
            ReservedMicros = unknown ? null : estimate.WorstMicros,
            InputTokens = estimate.InputTokens,
            OutputTokens = estimate.OutputTokens,
            Price = unknown ? null : estimate.Price,
            Note = request.Note,
            Created = now,
        };
    }

    /// <summary>What <paramref name="usage"/> cost on <paramref name="row"/>: $0 when free; none when the price is unknown; at the entered
    /// price for an entered one; else the cost the provider layer reported, or the row's rates x its tokens when it reported none.</summary>
    private static long? ActualCost(Reservation row, SpendUsage usage) => row.CostOf(usage);

    /// <summary><paramref name="micros"/>, $0 for a free row, or none for a row without a price.</summary>
    private static long? CostOrNone(Reservation row, long micros) =>
        row.Billing == BillingKind.Free ? 0 : row.Price is null || row.ReservedMicros is null ? null : micros;

    /// <summary>A settled row's share of the cache meter: saved from its cache reads, and a miss against the run's previous request
    /// (<see cref="SpendCacheMiss"/>). A cache-warm request has neither (its whole spend is waste); a row without usage or price has none.</summary>
    private static (long Saved, long Missed) CacheNumbers(SqliteConnection connection, SqliteTransaction transaction, Reservation row)
    {
        if (row.Usage is not { } usage || row.Price is not { } price || row.ReservedMicros is null || row.Source == SpendSource.CacheWarm) return default;
        var saved = price.CacheSavedMicros(usage.CacheReadTokens);
        if (row.Run is not { } run) return (saved, 0);
        var created = row.Created.ToUnixTimeMilliseconds();
        (string, object?)[] parameters = [("$run", run), ("$op", row.Op), ("$created", created)];
        const string Before = "run = $run AND op <> $op AND usage_input IS NOT NULL AND created_at <= $created AND ended_at IS NOT NULL AND ended_at <= $created";
        var previous = WebAgentDb.Scalar(connection, transaction, $"""
            SELECT usage_input + usage_cache_read + usage_cache_write FROM spend_reservations
            WHERE {Before} AND usage_input + usage_cache_read + usage_cache_write > 0 ORDER BY created_at DESC, rowid DESC LIMIT 1;
            """, parameters) is { } prompt ? Convert.ToDouble(prompt, CultureInfo.InvariantCulture) : (double?)null;
        var reported = WebAgentDb.Scalar(connection, transaction, $"""
            SELECT 1 FROM spend_reservations WHERE {Before} AND (usage_cache_read > 0 OR usage_cache_write > 0 OR source = 'cache-warm') LIMIT 1;
            """, parameters) is not null;
        return (saved, SpendCacheMiss.MissedMicros(previous, usage, price, reported));
    }

    private static void CheckUsage(SpendUsage usage)
    {
        foreach (var (name, tokens) in new[] { ("input", usage.InputTokens), ("output", usage.OutputTokens), ("cache read", usage.CacheReadTokens), ("cache write", usage.CacheWriteTokens) })
            if (!double.IsFinite(tokens) || tokens < 0) throw new ArgumentException($"The {name} tokens must be 0 or more.", nameof(usage));
        if (usage.CostMicros is < 0) throw new ArgumentException("A reported cost is never negative.", nameof(usage));
    }

    private static string Key(string op)
    {
        var key = op?.Trim();
        return string.IsNullOrEmpty(key) ? throw new ArgumentException("Name the reservation's op.", nameof(op)) : key;
    }

    private static string? Reason(string? reason)
    {
        var text = reason?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > MaxReasonLength ? text[..MaxReasonLength] : text;
    }

    /// <summary>A cap's name for a message: <c>A daily cap for everything metered</c>.</summary>
    private static string CapName(SpendCap cap)
    {
        var window = cap.Period switch { CapPeriod.Request => "per-request", CapPeriod.Run => "per-run", CapPeriod.Day => "daily", _ => "monthly" };
        var scope = cap.Scope switch
        {
            CapScope.Everything => "everything metered",
            CapScope.Provider => cap.Key,
            CapScope.Account => "account " + cap.Key,
            _ => cap.ScopeText(),
        };
        return $"A {window} cap for {scope}";
    }

    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());

    private async ValueTask EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (started) return;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { StartLocked(); }
        finally { gate.Release(); }
    }

    /// <summary>Startup recovery, once, under <see cref="gate"/>.</summary>
    private void StartLocked()
    {
        if (started) return;
        var count = db.Transaction((connection, transaction) => Recover(connection, transaction, RecoveryReason, Now()));
        startupRecovered = count;
        started = true;
        if (count > 0)
            log.LogWarning("Spend Guard: {Count} reservation(s) left by an earlier run are now uncertain ({Reason}); they keep counting until resolved in History › Spend.",
                count, RecoveryReason);
    }

    /// <summary>Marks every reserved row uncertain. A reserved and an uncertain row count the same (their reserved amount), so the
    /// denormalized counted and list amounts stay as they are.</summary>
    private static int Recover(SqliteConnection connection, SqliteTransaction transaction, string reason, DateTimeOffset now) =>
        WebAgentDb.Execute(connection, transaction, "UPDATE spend_reservations SET state = 'uncertain', reason = $reason, ended_at = $at WHERE state = 'reserved';",
            ("$reason", reason), ("$at", now.ToUnixTimeMilliseconds()));

    /// <summary>Runs <paramref name="work"/> under the lock in one BEGIN IMMEDIATE transaction, after startup recovery.</summary>
    private async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, DateTimeOffset, T> work, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StartLocked();
            var now = Now();
            return db.Transaction((connection, transaction) => work(connection, transaction, now));
        }
        finally { gate.Release(); }
    }

    /// <summary>Runs <paramref name="work"/> in one deferred (read) transaction, after startup recovery, without the lock.</summary>
    private async Task<T> ReadAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        using var connection = db.Open();
        RegisterFunctions(connection);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = work(connection, transaction);
        transaction.Commit();
        return result;
    }

    /// <summary>Changes the row of <paramref name="op"/> as <paramref name="change"/> says (null: leave it as it is) and returns it; a row
    /// that settles with usage gets its share of the cache meter.</summary>
    private async Task<Reservation> ChangeAsync(string op, Func<Reservation, DateTimeOffset, Reservation?> change, CancellationToken cancellationToken)
    {
        var key = Key(op);
        var changed = false;
        var result = await WriteAsync((connection, transaction, now) =>
        {
            changed = false;
            var row = Find(connection, transaction, key) ?? throw SpendException.NotFound($"There is no reservation {key}.");
            if (change(row, now) is not { } next) return row;
            var (saved, missed) = CacheNumbers(connection, transaction, next);
            Update(connection, transaction, next, saved, missed);
            changed = true;
            return next;
        }, cancellationToken).ConfigureAwait(false);
        if (changed && Changed is { } handlers)
        {
            try
            {
                handlers(result);
            }
            catch (Exception error)
            {
                log.LogWarning(error, "A handler of the ledger's change of {Op} failed.", key);
            }
        }
        return result;
    }
}
