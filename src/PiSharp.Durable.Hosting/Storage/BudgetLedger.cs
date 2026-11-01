// PiSharp-only (decision 0010): the reservation and usage ledger. Every paid or limited operation reserves its conservative maximum in
// one catalog transaction that checks both the run's frozen limits and the device's UTC-day limits, then settles its actual usage once.
// A crash between a remote call and its settlement leaves the reservation uncertain, still counted, until someone reconciles it: a local
// cap cannot undo a charge a provider already accepted, so it never pretends the charge did not happen.
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>The device's per-UTC-day limits and their revision.</summary>
public sealed record BudgetLimits(long Revision, IReadOnlyDictionary<string, long> PerDay)
{
    /// <summary>The limits as JSON: <c>{revision, perDay: {meter: amount}}</c>.</summary>
    public JsonObjectValue ToJson() => JsonValue.Object([new("revision", Revision), new("perDay", BudgetLedger.ToJson(PerDay))]);
}

/// <summary>One reservation of the ledger.</summary>
/// <param name="Status"><c>reserved</c>, <c>settled</c>, <c>released</c> or <c>uncertain</c>.</param>
/// <param name="CostKnown">False when the provider did not report what a settled operation cost.</param>
public sealed record BudgetReservation(string OperationId, string RunKey, string Provider, string Day, string Status,
    IReadOnlyDictionary<string, long> Requested, IReadOnlyDictionary<string, long>? Actual, bool CostKnown, string? Detail, long CreatedAt, long UpdatedAt)
{
    /// <summary>Whether the reservation still counts its requested amounts.</summary>
    public bool Active => Status is "reserved" or "uncertain";

    /// <summary>The reservation as JSON.</summary>
    public JsonObjectValue ToJson() => JsonValue.Object(
    [
        new("operationId", OperationId),
        new("runKey", RunKey),
        new("provider", Provider),
        new("day", Day),
        new("status", Status),
        new("requested", BudgetLedger.ToJson(Requested)),
        new("actual", Actual is null ? JsonValue.Null : BudgetLedger.ToJson(Actual)),
        new("costKnown", CostKnown),
        new("detail", Detail is null ? JsonValue.Null : Detail),
        new("createdAt", CreatedAt),
        new("updatedAt", UpdatedAt),
    ]);
}

/// <summary>
/// Reservations and usage by meter: <c>requests</c>, <c>tokens</c>, <c>credits:&lt;provider&gt;</c> and <c>money:&lt;CURRENCY&gt;</c>
/// (integer micro-units). Requests and tokens add across providers; credits and currencies never mix.
/// </summary>
public sealed partial class BudgetLedger
{
    private readonly SessionCatalog catalog;
    private readonly TimeProvider time;

    internal BudgetLedger(SessionCatalog catalog, TimeProvider time)
    {
        this.catalog = catalog;
        this.time = time;
    }

    /// <summary>Whether <paramref name="meter"/> names a meter.</summary>
    public static bool IsMeter(string meter) => MeterPattern().IsMatch(meter);

    /// <summary>Reads meter amounts from JSON: whole, non-negative numbers by meter.</summary>
    /// <exception cref="HostValidationException">An unknown meter or an invalid amount.</exception>
    public static IReadOnlyDictionary<string, long> Amounts(JsonObjectValue? json, string path = "")
    {
        var amounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var errors = new List<FieldError>();
        foreach (var (meter, value) in json ?? JsonObjectValue.Empty)
        {
            if (!IsMeter(meter)) errors.Add(new FieldError($"{path}/{meter}", "is not a meter (requests, tokens, credits:<provider>, money:<CURRENCY>)"));
            else if (value is not JsonNumberValue number || !double.IsFinite(number.Value) || number.Value < 0 || number.Value != Math.Floor(number.Value) ||
                number.Value > 9e15)
                errors.Add(new FieldError($"{path}/{meter}", "must be a whole number of at least 0"));
            else amounts[meter] = (long)number.Value;
        }
        if (errors.Count > 0) throw HostValidationException.Of("Invalid budget", errors);
        return amounts;
    }

    /// <summary>Meter amounts as JSON.</summary>
    public static JsonObjectValue ToJson(IReadOnlyDictionary<string, long> amounts) =>
        JsonValue.Object(amounts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new KeyValuePair<string, JsonValue>(pair.Key, pair.Value)));

    /// <summary>The device's per-day limits.</summary>
    public BudgetLimits Limits() => catalog.Read(_ => ReadLimits(null));

    /// <summary>Changes the per-day limits: a null amount removes a meter's limit. A stale <paramref name="expectedRevision"/> is refused.</summary>
    /// <exception cref="HostConflictException">The limits changed since <paramref name="expectedRevision"/>.</exception>
    public BudgetLimits SetLimits(IReadOnlyDictionary<string, long?> changes, long expectedRevision)
    {
        foreach (var (meter, amount) in changes)
            if (!IsMeter(meter) || amount < 0) throw HostValidationException.Of("Invalid budget", [new FieldError("/perDay/" + meter, "must be a meter with a whole amount of at least 0")]);
        return catalog.Transact(transaction =>
        {
            var current = ReadLimits(transaction);
            if (current.Revision != expectedRevision)
                throw new HostConflictException($"The budget limits are at revision {current.Revision}, not {expectedRevision}.");
            foreach (var (meter, amount) in changes)
            {
                if (amount is { } value)
                    catalog.Run(transaction, "INSERT INTO budget_limits (meter, per_day) VALUES ($meter, $amount) ON CONFLICT (meter) DO UPDATE SET per_day = excluded.per_day;",
                        ("$meter", meter), ("$amount", value));
                else catalog.Run(transaction, "DELETE FROM budget_limits WHERE meter = $meter;", ("$meter", meter));
            }
            catalog.SetMeta(transaction, "budget_revision", (current.Revision + 1).ToString(CultureInfo.InvariantCulture));
            return ReadLimits(transaction);
        });
    }

    /// <summary>
    /// Reserves <paramref name="requested"/> for one logical operation. A repeat with the same operation, run, provider and amounts returns
    /// the existing reservation in whatever state it is; different values conflict.
    /// </summary>
    /// <exception cref="HostLimitException">The run's or the day's limit would be exceeded, or a money limit applies and the request has no
    /// money amount (an unknown price).</exception>
    public BudgetReservation Reserve(string operationId, string runKey, string provider, IReadOnlyDictionary<string, long> requested,
        IReadOnlyDictionary<string, long>? runLimits = null)
    {
        CheckId(operationId);
        if (string.IsNullOrWhiteSpace(runKey) || runKey.Length > 300) throw new HostInvalidException("A reservation needs a run key.");
        if (string.IsNullOrWhiteSpace(provider) || provider.Length > 100) throw new HostInvalidException("A reservation needs a provider.");
        foreach (var meter in requested.Keys) if (!IsMeter(meter)) throw new HostInvalidException($"Unknown meter {meter}.");
        var now = time.GetUtcNow();
        var day = Day(now);
        return catalog.Transact(transaction =>
        {
            if (Find(transaction, operationId) is { } existing)
            {
                if (existing.RunKey == runKey && existing.Provider == provider && Same(existing.Requested, requested)) return existing;
                throw new HostConflictException($"Operation {operationId} already reserved different amounts.");
            }
            var dayLimits = ReadLimits(transaction).PerDay;
            foreach (var (limits, scope) in new[] { (runLimits ?? new Dictionary<string, long>(), "run"), (dayLimits, "day") })
            {
                foreach (var meter in limits.Keys.Where(meter => meter.StartsWith("money:", StringComparison.Ordinal)))
                    if (!requested.ContainsKey(meter))
                        throw new HostLimitException($"The {scope} has a {meter} limit and the price of this {provider} operation is unknown.");
                if (limits.Count == 0) continue;
                var used = Used(transaction, scope == "run" ? "run_key = $key" : "day = $key", scope == "run" ? runKey : day);
                foreach (var (meter, limit) in limits)
                {
                    var add = requested.TryGetValue(meter, out var amount) ? amount : 0;
                    if (add > 0 && used.GetValueOrDefault(meter) + add > limit)
                        throw new HostLimitException($"The {scope} limit of {limit} {meter} would be exceeded ({used.GetValueOrDefault(meter)} used or reserved, {add} requested).");
                }
            }
            var at = now.ToUnixTimeMilliseconds();
            catalog.Run(transaction, """
                INSERT INTO budget_reservations (operation_id, run_key, provider, day, status, requested, actual, cost_known, detail, created_at, updated_at)
                VALUES ($id, $run, $provider, $day, 'reserved', $requested, NULL, 1, NULL, $at, $at);
                """, ("$id", operationId), ("$run", runKey), ("$provider", provider), ("$day", day), ("$requested", CanonicalJson.Serialize(ToJson(requested))),
                ("$at", at));
            return Find(transaction, operationId)!;
        });
    }

    /// <summary>Settles a reservation with its actual usage. Repeating the same values does nothing; different values conflict.</summary>
    /// <exception cref="HostConflictException">The reservation was settled differently, released, or is uncertain (use
    /// <see cref="Resolve"/>).</exception>
    public BudgetReservation Settle(string operationId, IReadOnlyDictionary<string, long> actual, bool costKnown = true) =>
        Change(operationId, existing => existing.Status switch
        {
            "reserved" => ("settled", actual, costKnown, existing.Detail),
            "settled" when existing.Actual is { } done && Same(done, actual) && existing.CostKnown == costKnown => null,
            _ => throw new HostConflictException($"Operation {operationId} is {existing.Status} and cannot be settled this way."),
        });

    /// <summary>Releases a reservation that used nothing. Repeating it does nothing.</summary>
    public BudgetReservation Release(string operationId) =>
        Change(operationId, existing => existing.Status switch
        {
            "reserved" => ("released", null, true, existing.Detail),
            "released" => null,
            _ => throw new HostConflictException($"Operation {operationId} is {existing.Status} and cannot be released."),
        });

    /// <summary>Marks a reservation whose remote outcome is unknown: it stays counted until it is resolved.</summary>
    public BudgetReservation MarkUncertain(string operationId, string detail) =>
        Change(operationId, existing => existing.Status == "reserved" ? ("uncertain", null, false, Truncate(detail)) : null);

    /// <summary>Explicit reconciliation of an uncertain (or still reserved) operation: <c>settled</c> with its actual usage, or
    /// <c>released</c>.</summary>
    public BudgetReservation Resolve(string operationId, string outcome, IReadOnlyDictionary<string, long>? actual, string? detail = null) =>
        Change(operationId, existing =>
        {
            if (outcome is not ("settled" or "released")) throw new HostInvalidException("A resolution is settled or released.");
            if (outcome == "settled" && actual is null) throw new HostInvalidException("A settled resolution needs the actual usage.");
            if (existing.Status == outcome && (outcome == "released" || (existing.Actual is { } done && Same(done, actual!)))) return null;
            if (existing.Status is not ("reserved" or "uncertain"))
                throw new HostConflictException($"Operation {operationId} is already {existing.Status}.");
            return (outcome, outcome == "settled" ? actual : null, true, Truncate(detail ?? existing.Detail));
        });

    /// <summary>The reservation of an operation; null when there is none.</summary>
    public BudgetReservation? Get(string operationId) => catalog.Read(_ => Find(null, operationId));

    /// <summary>A run's reservations, oldest first.</summary>
    public IReadOnlyList<BudgetReservation> ForRun(string runKey) => catalog.Read(_ =>
        catalog.Rows(null, Select + " WHERE run_key = $key ORDER BY created_at, operation_id;", Map, ("$key", runKey)));

    /// <summary>Reservations still counting their requested amounts, oldest first.</summary>
    public IReadOnlyList<BudgetReservation> Active(int limit = 100) => catalog.Read(_ =>
        catalog.Rows(null, Select + " WHERE status IN ('reserved', 'uncertain') ORDER BY created_at, operation_id LIMIT $limit;", Map, ("$limit", (long)limit)));

    /// <summary>A run's usage: <c>{runKey, reservations, totals, costKnown, uncertain}</c>.</summary>
    public JsonObjectValue Usage(string runKey)
    {
        var reservations = ForRun(runKey);
        var totals = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var reservation in reservations)
            foreach (var (meter, amount) in Counted(reservation)) totals[meter] = totals.GetValueOrDefault(meter) + amount;
        return JsonValue.Object(
        [
            new("runKey", runKey),
            new("reservations", JsonValue.Array(reservations.Select(item => (JsonValue)item.ToJson()))),
            new("totals", ToJson(totals)),
            new("costKnown", reservations.All(item => item.CostKnown && item.Status != "uncertain")),
            new("uncertain", reservations.Count(item => item.Status == "uncertain")),
        ]);
    }

    /// <summary>Today's limits and use: <c>{day, limits, used, active, uncertain}</c>.</summary>
    public JsonObjectValue Today()
    {
        var day = Day(time.GetUtcNow());
        return catalog.Read(_ =>
        {
            var used = Used(null, "day = $key", day);
            return JsonValue.Object(
            [
                new("day", day),
                new("limits", ReadLimits(null).ToJson()),
                new("used", ToJson(used)),
                new("active", Convert.ToInt64(catalog.Value(null, "SELECT COUNT(*) FROM budget_reservations WHERE status IN ('reserved', 'uncertain');"), CultureInfo.InvariantCulture)),
                new("uncertain", Convert.ToInt64(catalog.Value(null, "SELECT COUNT(*) FROM budget_reservations WHERE status = 'uncertain';"), CultureInfo.InvariantCulture)),
            ]);
        });
    }

    /// <summary>The UTC day of <paramref name="at"/>, <c>yyyy-MM-dd</c>.</summary>
    internal static string Day(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private const string Select = "SELECT operation_id, run_key, provider, day, status, requested, actual, cost_known, detail, created_at, updated_at FROM budget_reservations";

    private BudgetReservation Change(string operationId, Func<BudgetReservation, (string Status, IReadOnlyDictionary<string, long>? Actual, bool CostKnown, string? Detail)?> change)
    {
        CheckId(operationId);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        return catalog.Transact(transaction =>
        {
            var existing = Find(transaction, operationId) ?? throw new HostNotFoundException($"No reservation {operationId}.");
            if (change(existing) is not { } next) return existing;
            catalog.Run(transaction, "UPDATE budget_reservations SET status = $status, actual = $actual, cost_known = $known, detail = $detail, updated_at = $at WHERE operation_id = $id;",
                ("$status", next.Status), ("$actual", next.Actual is null ? null : CanonicalJson.Serialize(ToJson(next.Actual))), ("$known", next.CostKnown ? 1L : 0L),
                ("$detail", next.Detail), ("$at", now), ("$id", operationId));
            return Find(transaction, operationId)!;
        });
    }

    private Dictionary<string, long> Used(SqliteTransaction? transaction, string where, string key)
    {
        var used = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var reservation in catalog.Rows(transaction, Select + " WHERE " + where + ";", Map, ("$key", key)))
            foreach (var (meter, amount) in Counted(reservation)) used[meter] = used.GetValueOrDefault(meter) + amount;
        return used;
    }

    private static IReadOnlyDictionary<string, long> Counted(BudgetReservation reservation) => reservation.Status switch
    {
        "reserved" or "uncertain" => reservation.Requested,
        "settled" => reservation.Actual ?? reservation.Requested,
        _ => new Dictionary<string, long>(),
    };

    private BudgetLimits ReadLimits(SqliteTransaction? transaction)
    {
        var revision = catalog.Meta(transaction, "budget_revision") is { } text ? long.Parse(text, CultureInfo.InvariantCulture) : 0;
        var limits = catalog.Rows(transaction, "SELECT meter, per_day FROM budget_limits ORDER BY meter;", reader => (reader.GetString(0), reader.GetInt64(1)))
            .ToDictionary(row => row.Item1, row => row.Item2, StringComparer.Ordinal);
        return new BudgetLimits(revision, limits);
    }

    private BudgetReservation? Find(SqliteTransaction? transaction, string operationId) =>
        catalog.Rows(transaction, Select + " WHERE operation_id = $id;", Map, ("$id", operationId)).FirstOrDefault();

    private static BudgetReservation Map(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), Parse(reader.GetString(5)), reader.IsDBNull(6) ? null : Parse(reader.GetString(6)), reader.GetInt64(7) != 0,
        reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt64(9), reader.GetInt64(10));

    private static IReadOnlyDictionary<string, long> Parse(string json) => Amounts(JsonValue.Parse(json) as JsonObjectValue);

    private static bool Same(IReadOnlyDictionary<string, long> left, IReadOnlyDictionary<string, long> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var other) && other == pair.Value);

    private static void CheckId(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 200) throw new HostInvalidException("An operation ID must have 1 to 200 characters.");
    }

    private static string? Truncate(string? text) => text is { Length: > 2000 } ? text[..2000] : text;

    [GeneratedRegex("^(requests|tokens|credits:[a-z0-9._-]{1,40}|money:[A-Z]{3})$", RegexOptions.CultureInvariant)]
    private static partial Regex MeterPattern();
}
