// Decision 0011 (run W3, S2): how SpendLedger's rows and caps are stored (SpendLedgerMigrations). Enums are stored by their JSON wire names
// (the contracts' JsonStringEnumMemberName names; BillingKind's camelCase names through SettingsStore.JsonOptions), sources by
// SpendSources.Wire, rates as SpendPrice JSON, usage as columns. The ledger page's filters are SQL that agrees with
// SpendLedgerQuery.Matches, the contract's reference rule: times compared at the stored millisecond, the status word mapped to state and
// settlement, and the search done by a spend_contains function with the same comparison as Matches (ordinal, ignoring case).
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Spend;

public sealed partial class SpendLedger
{
    private const string Columns = "op, provider, model, account, source, run, billing, state, reserved_micros, actual_micros, estimate_micros, settlement, " +
        "resolved_by_user, input_tokens, output_tokens, price, usage_input, usage_output, usage_cache_read, usage_cache_write, usage_cost_micros, note, reason, " +
        "created_at, ended_at";

    /// <summary>Every cap, by window (request, run, day, month), then scope (everything, provider, account, source), key and id.</summary>
    private static List<SpendCap> ReadCaps(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = WebAgentDb.Command(connection, transaction, "SELECT id, scope, key, period, limit_micros, action, enabled FROM spend_caps;");
        using var reader = command.ExecuteReader();
        var caps = new List<SpendCap>();
        while (reader.Read())
            caps.Add(new SpendCap(reader.GetInt64(0), Parse<CapScope>(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetString(2),
                Parse<CapPeriod>(reader.GetString(3)), reader.GetInt64(4), Parse<CapAction>(reader.GetString(5)), reader.GetInt64(6) != 0));
        return [.. caps.OrderBy(cap => cap.Period).ThenBy(cap => cap.Scope).ThenBy(cap => cap.Key, StringComparer.Ordinal).ThenBy(cap => cap.Id)];
    }

    /// <summary>What counts toward <paramref name="cap"/> now: rows that count toward caps, in its scope, of <paramref name="run"/> for a
    /// run cap or reserved inside <paramref name="window"/> for a day or month cap.</summary>
    private static long Spent(SqliteConnection connection, SqliteTransaction transaction, SpendCap cap, string? run, (DateTimeOffset Start, DateTimeOffset End)? window)
    {
        var clauses = new List<string> { "counts = 1" };
        var parameters = new List<(string Name, object? Value)>();
        var column = cap.Scope switch { CapScope.Provider => "provider", CapScope.Account => "account", CapScope.Source => "source", _ => null };
        if (column is not null)
        {
            clauses.Add(column + " = $key");
            parameters.Add(("$key", cap.Key));
        }
        if (cap.Period == CapPeriod.Run)
        {
            clauses.Add("run = $run");
            parameters.Add(("$run", run));
        }
        else if (window is { } span)
        {
            clauses.Add("created_at >= $from AND created_at < $to");
            parameters.Add(("$from", span.Start.ToUnixTimeMilliseconds()));
            parameters.Add(("$to", span.End.ToUnixTimeMilliseconds()));
        }
        return Convert.ToInt64(WebAgentDb.Scalar(connection, transaction,
            $"SELECT COALESCE(SUM(counted_micros), 0) FROM spend_reservations WHERE {string.Join(" AND ", clauses)};", [.. parameters]) ?? 0L, CultureInfo.InvariantCulture);
    }

    private static Reservation? Find(SqliteConnection connection, SqliteTransaction? transaction, string op) =>
        Rows(connection, transaction, $"SELECT {Columns} FROM spend_reservations WHERE op = $op;", [("$op", op)]).FirstOrDefault();

    private static List<Reservation> Rows(SqliteConnection connection, SqliteTransaction? transaction, string sql, (string Name, object? Value)[] parameters)
    {
        using var command = WebAgentDb.Command(connection, transaction, sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<Reservation>();
        while (reader.Read()) rows.Add(Map(reader));
        return rows;
    }

    private static Reservation Map(SqliteDataReader reader) => new()
    {
        Op = reader.GetString(0),
        Provider = reader.GetString(1),
        Model = reader.GetString(2),
        Account = Text(reader, 3),
        Source = SpendSources.TryParse(reader.GetString(4), out var source) ? source
            : throw new InvalidDataException($"webagent.db holds an unknown spend source \"{reader.GetString(4)}\"."),
        Run = Text(reader, 5),
        Billing = Parse<BillingKind>(reader.GetString(6)),
        State = Parse<ReservationState>(reader.GetString(7)),
        ReservedMicros = Number(reader, 8),
        ActualMicros = Number(reader, 9),
        EstimateMicros = Number(reader, 10),
        Settlement = reader.IsDBNull(11) ? null : Parse<SettlementKind>(reader.GetString(11)),
        ResolvedByUser = reader.GetInt64(12) != 0,
        InputTokens = reader.GetDouble(13),
        OutputTokens = reader.GetInt32(14),
        Price = reader.IsDBNull(15) ? null : JsonSerializer.Deserialize<SpendPrice>(reader.GetString(15), SettingsStore.JsonOptions),
        Usage = reader.IsDBNull(16) ? null
            : new SpendUsage(reader.GetDouble(16), reader.GetDouble(17), reader.GetDouble(18), reader.GetDouble(19)) { CostMicros = Number(reader, 20) },
        Note = Text(reader, 21),
        Reason = Text(reader, 22),
        Created = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(23)),
        Ended = reader.IsDBNull(24) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(24)),
    };

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, Reservation row) =>
        WebAgentDb.Execute(connection, transaction, $"""
            INSERT INTO spend_reservations ({Columns}, counts, counted_micros, list_micros, cache_saved_micros, cache_missed_micros)
            VALUES ($op, $provider, $model, $account, $source, $run, $billing, $state, $reserved, $actual, $estimate, $settlement, $resolved, $input_tokens,
              $output_tokens, $price, $usage_input, $usage_output, $usage_cache_read, $usage_cache_write, $usage_cost, $note, $reason, $created, $ended,
              $counts, $counted, $list, $saved, $missed);
            """, Parameters(row, 0, 0));

    /// <summary>Writes every column a reservation's ending changes (and the counted, list and cache amounts worked out from it).</summary>
    private static void Update(SqliteConnection connection, SqliteTransaction transaction, Reservation row, long savedMicros, long missedMicros) =>
        WebAgentDb.Execute(connection, transaction, """
            UPDATE spend_reservations SET state = $state, actual_micros = $actual, estimate_micros = $estimate, settlement = $settlement,
              resolved_by_user = $resolved, usage_input = $usage_input, usage_output = $usage_output, usage_cache_read = $usage_cache_read,
              usage_cache_write = $usage_cache_write, usage_cost_micros = $usage_cost, reason = $reason, ended_at = $ended, counts = $counts,
              counted_micros = $counted, list_micros = $list, cache_saved_micros = $saved, cache_missed_micros = $missed
            WHERE op = $op;
            """, Parameters(row, savedMicros, missedMicros));

    private static (string Name, object? Value)[] Parameters(Reservation row, long savedMicros, long missedMicros) =>
    [
        ("$op", row.Op), ("$provider", row.Provider), ("$model", row.Model), ("$account", row.Account), ("$source", row.Source.Wire()), ("$run", row.Run),
        ("$billing", Wire(row.Billing)), ("$state", Wire(row.State)), ("$reserved", row.ReservedMicros), ("$actual", row.ActualMicros),
        ("$estimate", row.EstimateMicros), ("$settlement", row.Settlement is { } settlement ? Wire(settlement) : null), ("$resolved", row.ResolvedByUser ? 1L : 0L),
        ("$input_tokens", row.InputTokens), ("$output_tokens", (long)row.OutputTokens),
        ("$price", row.Price is null ? null : JsonSerializer.Serialize(row.Price, SettingsStore.JsonOptions)),
        ("$usage_input", row.Usage?.InputTokens), ("$usage_output", row.Usage?.OutputTokens), ("$usage_cache_read", row.Usage?.CacheReadTokens),
        ("$usage_cache_write", row.Usage?.CacheWriteTokens), ("$usage_cost", row.Usage?.CostMicros), ("$note", row.Note), ("$reason", row.Reason),
        ("$created", row.Created.ToUnixTimeMilliseconds()), ("$ended", row.Ended?.ToUnixTimeMilliseconds()),
        ("$counts", row.CountsTowardCaps ? 1L : 0L), ("$counted", row.CountedMicros), ("$list", row.ListMicros), ("$saved", savedMicros), ("$missed", missedMicros),
    ];

    /// <summary>The WHERE clause and parameters of <paramref name="query"/>'s filters (paging aside).</summary>
    private static (string Where, (string Name, object? Value)[] Parameters) Filter(SpendLedgerQuery query)
    {
        var clauses = new List<string>();
        var parameters = new List<(string Name, object? Value)>();
        void Add(string clause, string name, object? value)
        {
            clauses.Add(clause);
            parameters.Add((name, value));
        }
        // Rows keep milliseconds: a row is at or after From when its millisecond is at or after From rounded up, and before To likewise.
        if (query.From is { } from) Add("created_at >= $from", "$from", CeilingMilliseconds(from));
        if (query.To is { } to) Add("created_at < $to", "$to", CeilingMilliseconds(to));
        if (query.Provider is { } provider) Add("provider = $provider", "$provider", provider);
        if (query.Model is { } model) Add("model = $model", "$model", model);
        if (query.Account is { } account) Add("account = $account", "$account", account);
        if (query.Source is { } source) Add("source = $source", "$source", source.Wire());
        if (query.Billing is { } billing) Add("billing = $billing", "$billing", Wire(billing));
        if (query.Run is { } run) Add("run = $run", "$run", run);
        if (query.Status is { } status)
            clauses.Add(status switch
            {
                LedgerStatus.Reserved => "state = 'reserved'",
                LedgerStatus.Uncertain => "state = 'uncertain'",
                LedgerStatus.Released => "state = 'released'",
                LedgerStatus.Estimate => "(state = 'settled' AND settlement = 'estimate')",
                _ => "(state = 'settled' AND (settlement IS NULL OR settlement <> 'estimate'))",
            });
        if (query.Search is { } search)
            Add("(" + string.Join(" OR ", new[] { "provider", "model", "account", "op", "run", "source", "note", "reason" }.Select(field => $"spend_contains({field}, $search)")) + ")",
                "$search", search);
        return (clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses), [.. parameters]);
    }

    /// <summary>The functions the filters use: <c>spend_contains(text, search)</c>, as <see cref="SpendLedgerQuery.Matches"/> compares.</summary>
    private static void RegisterFunctions(SqliteConnection connection) =>
        connection.CreateFunction<string?, string?, bool>("spend_contains",
            (text, search) => text is not null && search is not null && text.Contains(search, StringComparison.OrdinalIgnoreCase), isDeterministic: true);

    /// <summary><paramref name="at"/> as Unix milliseconds, rounded up.</summary>
    private static long CeilingMilliseconds(DateTimeOffset at)
    {
        var ticks = at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        return ticks >= 0 ? (ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond : -(-ticks / TimeSpan.TicksPerMillisecond);
    }

    private static string? Text(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);

    private static long? Number(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetInt64(column);

    /// <summary>The wire name of <paramref name="value"/>, as the contracts' JSON writes it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not a defined value.</exception>
    private static string Wire<T>(T value) where T : struct, Enum =>
        WireNames<T>.Names.TryGetValue(value, out var name) ? name : throw new ArgumentOutOfRangeException(nameof(value), value, $"Not a {typeof(T).Name}.");

    private static T Parse<T>(string wire) where T : struct, Enum =>
        WireNames<T>.Values.TryGetValue(wire, out var value) ? value : throw new InvalidDataException($"webagent.db holds an unknown {typeof(T).Name} \"{wire}\".");

    private static class WireNames<T> where T : struct, Enum
    {
        public static readonly Dictionary<T, string> Names = Enum.GetValues<T>().ToDictionary(value => value, value => JsonSerializer.Serialize(value, SettingsStore.JsonOptions).Trim('"'));

        public static readonly Dictionary<string, T> Values = Names.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
    }
}
