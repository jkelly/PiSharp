// Decision 0011 (run W4, R0): reads and writes runs.db. A run row is made when its call starts (status running, with the request as sent)
// and finished once, in one transaction with its stream events and raw payload items, when the call ends. A chat call is keyed by its op
// (BeginCall): a durable request recovered after a restart carries the same op, so it updates its row (back to running, its earlier events
// dropped) instead of adding a second one. Each store instance has a runner id; the first use in a process marks every row still running
// under another runner interrupted (the app stopped while it ran: it is never retried by itself; a recovered chat call sets it running
// again). Cleanup deletes unpinned runs older than the retention (never a running one, the parent of a pinned run or a run whose parent is
// pinned) and raw payloads older than RawRetentionDays, whatever the run's own retention. Every method is synchronous (SQLite on the
// caller's thread) and safe from many threads.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Runs;

/// <summary>How a run ended, as <see cref="RunStore.Finish"/> stores it.</summary>
public sealed record RunOutcome
{
    /// <summary><see cref="RunStatus"/> (not running).</summary>
    public required string Status { get; init; }

    /// <summary>The final message (Pi wire JSON), or null.</summary>
    public JsonNode? Output { get; init; }

    /// <summary>Its text.</summary>
    public string? OutputText { get; init; }

    /// <summary>Its stop reason (Pi wire).</summary>
    public string? StopReason { get; init; }

    /// <summary>The error, or null.</summary>
    public string? Error { get; init; }

    /// <summary>Notes, or null (they replace the row's).</summary>
    public string? Notes { get; init; }

    /// <summary>The usage.</summary>
    public RunUsage Usage { get; init; } = RunUsage.Zero;

    /// <summary>The cost in micro-dollars, or null.</summary>
    public long? CostMicros { get; init; }

    /// <summary>How the cost was found (<see cref="RunRecord.CostKind"/>).</summary>
    public string? CostKind { get; init; }

    /// <summary>The billing mode.</summary>
    public string? Billing { get; init; }

    /// <summary>The ledger row.</summary>
    public string? SpendOp { get; init; }

    /// <summary>Time to first token.</summary>
    public long? TtftMs { get; init; }

    /// <summary>Duration.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Output tokens per second.</summary>
    public double? TokensPerSecond { get; init; }

    /// <summary>When it ended.</summary>
    public required DateTimeOffset Finished { get; init; }

    /// <summary>The stream events to store (empty when event storage is off).</summary>
    public IReadOnlyList<RunEventRecord> Events { get; init; } = [];

    /// <summary>The raw payload items to store (empty unless raw payloads are on).</summary>
    public IReadOnlyList<RunRawItem> Raw { get; init; } = [];
}

/// <summary>runs.db's rows. A singleton.</summary>
public sealed class RunStore
{
    /// <summary>The error of a run the app stopped during.</summary>
    public const string InterruptedMessage = "The app stopped while this run was running. It was not retried; run it again if you need its answer.";

    /// <summary>The most ids one delete takes.</summary>
    public const int MaxDelete = 1_000;

    private const string ListColumns = "id, op, kind, source, status, provider, model, api, account, group_id, parent_id, session, title, messages_ref, " +
        "output_text, stop_reason, error, notes, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, total_tokens, cost_micros, cost_kind, " +
        "billing, spend_op, ttft_ms, duration_ms, tokens_per_second, pinned, raw_state, created, started, finished, updated";

    private const string FullColumns = ListColumns + ", options, messages, output";

    private readonly Lock gate = new();
    private readonly ILogger log;
    private volatile bool ready;

    /// <summary>A store over <paramref name="db"/>.</summary>
    public RunStore(RunsDb db, ILogger<RunStore>? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        Db = db;
        this.log = (ILogger?)log ?? NullLogger.Instance;
    }

    /// <summary>The database.</summary>
    public RunsDb Db { get; }

    /// <summary>This process's runner id (stored with every running row it starts).</summary>
    public string Runner { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Raised after rows change (outside any transaction). A handler must not throw.</summary>
    public event EventHandler<RunChange>? Changed;

    /// <summary>Inserts <paramref name="run"/> (its <see cref="RunRecord.Status"/> as given; a running row is this process's).</summary>
    /// <exception cref="SqliteException">The id or op is already stored.</exception>
    public void Insert(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        EnsureReady();
        Db.Db.Transaction((connection, transaction) => WebAgentDb.Execute(connection, transaction, InsertSql, Parameters(run)));
        Raise("started", [run.Id]);
    }

    /// <summary>Starts the call <paramref name="run"/> keyed by its op: inserts it, or, when a row with that op exists (a recovered durable
    /// request), sets that row running again with the new request, drops its earlier events and raw payloads, and keeps its id, title and pin.</summary>
    /// <returns>The row's id (the existing one's for a recovered request).</returns>
    public string BeginCall(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        EnsureReady();
        var id = Db.Db.Transaction((connection, transaction) =>
        {
            var existing = WebAgentDb.Scalar(connection, transaction, "SELECT id FROM runs WHERE op = $op;", ("$op", run.Op)) as string;
            if (existing is null)
            {
                WebAgentDb.Execute(connection, transaction, InsertSql, Parameters(run));
                return run.Id;
            }
            WebAgentDb.Execute(connection, transaction, """
                UPDATE runs SET status = 'running', provider = $provider, model = $model, api = $api, account = $account, options = $options,
                    output = NULL, output_text = NULL, stop_reason = NULL, error = NULL, notes = NULL, input_tokens = 0, output_tokens = 0,
                    cache_read_tokens = 0, cache_write_tokens = 0, total_tokens = 0, cost_micros = NULL, cost_kind = NULL, spend_op = NULL,
                    ttft_ms = NULL, duration_ms = NULL, tokens_per_second = NULL, raw_state = 'none', runner = $runner, started = $started,
                    finished = NULL, updated = $updated
                WHERE id = $id;
                DELETE FROM run_events WHERE run_id = $id;
                DELETE FROM run_raw WHERE run_id = $id;
                """,
                ("$id", existing), ("$provider", run.Provider), ("$model", run.Model), ("$api", run.Api), ("$account", run.Account),
                ("$options", run.Options?.ToJsonString()), ("$runner", Runner), ("$started", Ms(run.Started ?? run.Created)), ("$updated", Ms(run.Created)));
            return existing;
        });
        Raise("started", [id]);
        return id;
    }

    /// <summary>Stores how the run <paramref name="id"/> ended, with its events and raw payload items, in one transaction.</summary>
    /// <returns>Whether the row exists.</returns>
    public bool Finish(string id, RunOutcome outcome)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(outcome);
        EnsureReady();
        var found = Db.Db.Transaction((connection, transaction) =>
        {
            var changed = WebAgentDb.Execute(connection, transaction, """
                UPDATE runs SET status = $status, output = $output, output_text = $text, stop_reason = $stop, error = $error,
                    notes = COALESCE($notes, notes), input_tokens = $input, output_tokens = $outputTokens, cache_read_tokens = $cacheRead,
                    cache_write_tokens = $cacheWrite, total_tokens = $total, cost_micros = $cost, cost_kind = $costKind, billing = COALESCE($billing, billing),
                    spend_op = COALESCE($spendOp, spend_op), ttft_ms = $ttft, duration_ms = $duration, tokens_per_second = $rate,
                    raw_state = CASE WHEN $raw > 0 THEN 'stored' ELSE raw_state END, finished = $finished, updated = $finished
                WHERE id = $id;
                """,
                ("$id", id), ("$status", outcome.Status), ("$output", outcome.Output?.ToJsonString()), ("$text", outcome.OutputText),
                ("$stop", outcome.StopReason), ("$error", outcome.Error), ("$notes", outcome.Notes), ("$input", outcome.Usage.Input),
                ("$outputTokens", outcome.Usage.Output), ("$cacheRead", outcome.Usage.CacheRead), ("$cacheWrite", outcome.Usage.CacheWrite),
                ("$total", outcome.Usage.Total), ("$cost", outcome.CostMicros), ("$costKind", outcome.CostKind), ("$billing", outcome.Billing),
                ("$spendOp", outcome.SpendOp), ("$ttft", outcome.TtftMs), ("$duration", outcome.DurationMs), ("$rate", Finite(outcome.TokensPerSecond)),
                ("$raw", outcome.Raw.Count), ("$finished", Ms(outcome.Finished)));
            if (changed == 0) return false;
            if (outcome.Events.Count > 0)
            {
                using var command = WebAgentDb.Command(connection, transaction,
                    "INSERT OR REPLACE INTO run_events (run_id, seq, at_ms, event) VALUES ($id, $seq, $at, $event);", ("$id", id), ("$seq", 0L), ("$at", 0L), ("$event", ""));
                foreach (var item in outcome.Events)
                {
                    command.Parameters["$seq"].Value = item.Seq;
                    command.Parameters["$at"].Value = item.AtMs;
                    command.Parameters["$event"].Value = item.Event.ToJsonString();
                    command.ExecuteNonQuery();
                }
            }
            if (outcome.Raw.Count > 0)
            {
                using var command = WebAgentDb.Command(connection, transaction,
                    "INSERT OR REPLACE INTO run_raw (run_id, seq, kind, at_ms, data, created) VALUES ($id, $seq, $kind, $at, $data, $created);",
                    ("$id", id), ("$seq", 0L), ("$kind", ""), ("$at", 0L), ("$data", ""), ("$created", Ms(outcome.Finished)));
                foreach (var item in outcome.Raw)
                {
                    command.Parameters["$seq"].Value = item.Seq;
                    command.Parameters["$kind"].Value = item.Kind;
                    command.Parameters["$at"].Value = item.AtMs;
                    command.Parameters["$data"].Value = item.Data;
                    command.ExecuteNonQuery();
                }
            }
            return true;
        });
        if (found) Raise("finished", [id]);
        return found;
    }

    /// <summary>The run <paramref name="id"/> with its options, messages and output, or null.</summary>
    public RunRecord? Get(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        EnsureReady();
        return Db.Db.Query($"SELECT {FullColumns} FROM runs WHERE id = $id;", reader => Read(reader, full: true), ("$id", id)).FirstOrDefault();
    }

    /// <summary>The run whose op is <paramref name="op"/>, or null.</summary>
    public RunRecord? GetByOp(string op)
    {
        ArgumentNullException.ThrowIfNull(op);
        EnsureReady();
        return Db.Db.Query($"SELECT {FullColumns} FROM runs WHERE op = $op;", reader => Read(reader, full: true), ("$op", op)).FirstOrDefault();
    }

    /// <summary>One page of the runs <paramref name="query"/> matches, newest first, without their options, messages and output.</summary>
    /// <exception cref="ArgumentException">The page size or offset is out of range.</exception>
    public RunPage List(RunQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > RunQuery.MaxLimit) throw new ArgumentException($"Ask for 1 to {RunQuery.MaxLimit} runs at a time.");
        if (query.Offset < 0) throw new ArgumentException("The offset is never negative.");
        EnsureReady();
        var (where, parameters) = Where(query);
        var total = Convert.ToInt64(Db.Db.Scalar($"SELECT COUNT(*) FROM runs{where};", [.. parameters]) ?? 0L, CultureInfo.InvariantCulture);
        var items = Db.Db.Query($"SELECT {ListColumns} FROM runs{where} ORDER BY created DESC, id DESC LIMIT $limit OFFSET $offset;",
            reader => Read(reader, full: false), [.. parameters, ("$limit", query.Limit), ("$offset", query.Offset)]);
        return new RunPage(items, total);
    }

    /// <summary>Totals over every run <paramref name="query"/> matches (its paging is ignored).</summary>
    public RunTotals Totals(RunQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        EnsureReady();
        var (where, parameters) = Where(query);
        return Db.Db.Query($"""
            SELECT COUNT(*), COALESCE(SUM(CASE WHEN status = 'error' THEN 1 ELSE 0 END), 0), COALESCE(SUM(cost_micros), 0),
                COALESCE(SUM(input_tokens), 0), COALESCE(SUM(output_tokens), 0), AVG(ttft_ms), AVG(tokens_per_second)
            FROM runs{where};
            """,
            reader => new RunTotals(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetDouble(3), reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetDouble(6)), [.. parameters]).Single();
    }

    /// <summary>The stored events of <paramref name="id"/> after sequence number <paramref name="after"/>, in order.</summary>
    public IReadOnlyList<RunEventRecord> Events(string id, long after = 0, int limit = 100_000)
    {
        ArgumentNullException.ThrowIfNull(id);
        EnsureReady();
        return Db.Db.Query("SELECT seq, at_ms, event FROM run_events WHERE run_id = $id AND seq > $after ORDER BY seq LIMIT $limit;",
            reader => new RunEventRecord(reader.GetInt64(0), reader.GetInt64(1), JsonNode.Parse(reader.GetString(2))!),
            ("$id", id), ("$after", after), ("$limit", Math.Max(1, limit)));
    }

    /// <summary>The stored raw payload items of <paramref name="id"/>, in order (empty when none were stored or they expired).</summary>
    public IReadOnlyList<RunRawItem> Raw(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        EnsureReady();
        return Db.Db.Query("SELECT seq, kind, at_ms, data FROM run_raw WHERE run_id = $id ORDER BY seq;",
            reader => new RunRawItem(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3)), ("$id", id));
    }

    /// <summary>Pins or unpins <paramref name="id"/>.</summary>
    /// <returns>Whether the run exists.</returns>
    public bool Pin(string id, bool pinned)
    {
        ArgumentNullException.ThrowIfNull(id);
        EnsureReady();
        var found = Db.Db.Execute("UPDATE runs SET pinned = $pinned, updated = $now WHERE id = $id;", ("$id", id), ("$pinned", pinned ? 1 : 0),
            ("$now", Ms(DateTimeOffset.UtcNow))) > 0;
        if (found) Raise("pinned", [id]);
        return found;
    }

    /// <summary>Deletes the runs <paramref name="ids"/> and their children (with their events and raw payloads). A running run is deleted
    /// too: its end then finds no row.</summary>
    /// <returns>How many rows were deleted.</returns>
    /// <exception cref="ArgumentException">More than <see cref="MaxDelete"/> ids.</exception>
    public int Delete(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var list = ids.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) return 0;
        if (list.Count > MaxDelete) throw new ArgumentException($"Delete at most {MaxDelete:N0} runs at a time.");
        EnsureReady();
        var names = list.Select((_, index) => "$p" + index.ToString(CultureInfo.InvariantCulture)).ToList();
        var set = string.Join(", ", names);
        var deleted = Db.Db.Execute($"DELETE FROM runs WHERE id IN ({set}) OR parent_id IN ({set});",
            [.. names.Zip(list, (name, id) => (name, (object?)id))]);
        if (deleted > 0) Raise("deleted", list);
        return deleted;
    }

    /// <summary>Deletes every run (or every unpinned one), with its events and raw payloads.</summary>
    public int Clear(bool keepPinned)
    {
        EnsureReady();
        var deleted = Db.Db.Execute(keepPinned ? "DELETE FROM runs WHERE pinned = 0 AND id NOT IN (SELECT parent_id FROM runs WHERE pinned = 1 AND parent_id IS NOT NULL);"
            : "DELETE FROM runs;");
        if (deleted > 0) Raise("deleted", []);
        return deleted;
    }

    /// <summary>Applies <paramref name="settings"/>' retention at <paramref name="now"/>: deletes unpinned runs older than the retention (never
    /// a running one, a parent with a pinned run, or a run whose parent is pinned) and raw payloads older than
    /// <see cref="RunHistorySettings.RawRetentionDays"/> (their runs' raw state becomes expired).</summary>
    public RunCleanup Cleanup(RunHistorySettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureReady();
        var result = Db.Db.Transaction((connection, transaction) =>
        {
            var runs = 0;
            if (settings.Retention is { } retention)
                runs = WebAgentDb.Execute(connection, transaction, """
                    DELETE FROM runs WHERE pinned = 0 AND created < $cutoff AND status <> 'running'
                        AND (parent_id IS NULL OR parent_id NOT IN (SELECT id FROM runs WHERE pinned = 1))
                        AND id NOT IN (SELECT parent_id FROM runs WHERE pinned = 1 AND parent_id IS NOT NULL);
                    """, ("$cutoff", Ms(now - retention)));
            WebAgentDb.Execute(connection, transaction, "DELETE FROM run_raw WHERE created < $cutoff;",
                ("$cutoff", Ms(now - TimeSpan.FromDays(RunHistorySettings.RawRetentionDays))));
            var raw = WebAgentDb.Execute(connection, transaction,
                "UPDATE runs SET raw_state = 'expired' WHERE raw_state = 'stored' AND NOT EXISTS (SELECT 1 FROM run_raw WHERE run_raw.run_id = runs.id);");
            return new RunCleanup(runs, raw);
        });
        if (result.Runs > 0 || result.RawRuns > 0)
        {
            log.LogInformation("runs.db: retention removed {Runs} runs and the raw payloads of {Raw}.", result.Runs, result.RawRuns);
            Raise("cleaned", []);
        }
        return result;
    }

    /// <summary>The size of runs.db on disk in bytes (with its WAL and shared-memory files).</summary>
    public long SizeBytes()
    {
        long size = 0;
        foreach (var path in new[] { Db.Path, Db.Path + "-wal", Db.Path + "-shm" })
        {
            try { if (File.Exists(path)) size += new FileInfo(path).Length; }
            catch (IOException) { /* Being written; counted next time. */ }
        }
        return size;
    }

    /// <summary>How many runs are stored.</summary>
    public long Count()
    {
        EnsureReady();
        return Convert.ToInt64(Db.Db.Scalar("SELECT COUNT(*) FROM runs;") ?? 0L, CultureInfo.InvariantCulture);
    }

    /// <summary>Marks every running row of another runner interrupted (the app stopped while it ran). Runs once per store before its first
    /// use; safe to call again.</summary>
    /// <returns>How many runs it marked.</returns>
    public int InterruptStale()
    {
        Db.Db.Migrate();
        var now = Ms(DateTimeOffset.UtcNow);
        var marked = Db.Db.Execute("""
            UPDATE runs SET status = 'interrupted', error = COALESCE(error, $message), finished = COALESCE(finished, $now), updated = $now
            WHERE status = 'running' AND (runner IS NULL OR runner <> $runner);
            """, ("$message", InterruptedMessage), ("$now", now), ("$runner", Runner));
        if (marked > 0) log.LogInformation("runs.db: {Count} runs were running when the app stopped; they are marked interrupted.", marked);
        return marked;
    }

    private void EnsureReady()
    {
        if (ready) return;
        lock (gate)
        {
            if (ready) return;
            InterruptStale();
            ready = true;
        }
    }

    private void Raise(string kind, IReadOnlyList<string> ids)
    {
        try { Changed?.Invoke(this, new RunChange(kind, ids)); }
        catch (Exception error) { log.LogWarning(error, "A runs.db change handler failed."); }
    }

    private const string InsertSql = """
        INSERT INTO runs (id, op, kind, source, status, provider, model, api, account, group_id, parent_id, session, title, options, messages,
            messages_ref, output, output_text, stop_reason, error, notes, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
            total_tokens, cost_micros, cost_kind, billing, spend_op, ttft_ms, duration_ms, tokens_per_second, pinned, raw_state, runner, created,
            started, finished, updated)
        VALUES ($id, $op, $kind, $source, $status, $provider, $model, $api, $account, $group, $parent, $session, $title, $options, $messages,
            $messagesRef, $output, $outputText, $stop, $error, $notes, $input, $outputTokens, $cacheRead, $cacheWrite, $total, $cost, $costKind,
            $billing, $spendOp, $ttft, $duration, $rate, $pinned, $raw, $runner, $created, $started, $finished, $updated);
        """;

    private (string, object?)[] Parameters(RunRecord run) =>
    [
        ("$id", run.Id), ("$op", run.Op), ("$kind", run.Kind), ("$source", run.Source), ("$status", run.Status), ("$provider", run.Provider),
        ("$model", run.Model), ("$api", run.Api), ("$account", run.Account), ("$group", run.GroupId), ("$parent", run.ParentId), ("$session", run.Session),
        ("$title", run.Title), ("$options", run.Options?.ToJsonString()), ("$messages", run.Messages?.ToJsonString()), ("$messagesRef", run.MessagesRef),
        ("$output", run.Output?.ToJsonString()), ("$outputText", run.OutputText), ("$stop", run.StopReason), ("$error", run.Error), ("$notes", run.Notes),
        ("$input", run.Usage.Input), ("$outputTokens", run.Usage.Output), ("$cacheRead", run.Usage.CacheRead), ("$cacheWrite", run.Usage.CacheWrite),
        ("$total", run.Usage.Total), ("$cost", run.CostMicros), ("$costKind", run.CostKind), ("$billing", run.Billing), ("$spendOp", run.SpendOp),
        ("$ttft", run.TtftMs), ("$duration", run.DurationMs), ("$rate", Finite(run.TokensPerSecond)), ("$pinned", run.Pinned ? 1 : 0), ("$raw", run.RawState),
        ("$runner", run.Status == RunStatus.Running ? Runner : null), ("$created", Ms(run.Created)), ("$started", run.Started is { } started ? Ms(started) : null),
        ("$finished", run.Finished is { } finished ? Ms(finished) : null), ("$updated", Ms(run.Updated == default ? run.Created : run.Updated)),
    ];

    private static (string Where, List<(string, object?)> Parameters) Where(RunQuery query)
    {
        var clauses = new List<string>();
        var parameters = new List<(string, object?)>();
        void Equal(string column, string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            clauses.Add($"{column} = {name}");
            parameters.Add((name, value.Trim()));
        }
        Equal("source", "$source", query.Source);
        Equal("provider", "$provider", query.Provider);
        Equal("model", "$model", query.Model);
        Equal("status", "$status", query.Status);
        Equal("kind", "$kind", query.Kind);
        Equal("group_id", "$group", query.GroupId);
        Equal("parent_id", "$parent", query.ParentId);
        if (query.Pinned is { } pinned)
        {
            clauses.Add("pinned = $pinned");
            parameters.Add(("$pinned", pinned ? 1 : 0));
        }
        if (query.From is { } from)
        {
            clauses.Add("created >= $from");
            parameters.Add(("$from", Ms(from)));
        }
        if (query.To is { } to)
        {
            clauses.Add("created < $to");
            parameters.Add(("$to", Ms(to)));
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            clauses.Add("(title LIKE $q ESCAPE '\\' OR output_text LIKE $q ESCAPE '\\' OR error LIKE $q ESCAPE '\\' OR model LIKE $q ESCAPE '\\' " +
                "OR provider LIKE $q ESCAPE '\\' OR id = $exact OR op = $exact)");
            var text = query.Search.Trim();
            parameters.Add(("$q", "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal) + "%"));
            parameters.Add(("$exact", text));
        }
        return (clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses), parameters);
    }

    private static RunRecord Read(SqliteDataReader reader, bool full)
    {
        string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        long? Long(int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index);
        DateTimeOffset? Time(int index) => reader.IsDBNull(index) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(index));
        JsonNode? Json(int index)
        {
            var text = Text(index);
            if (text is null) return null;
            try { return JsonNode.Parse(text); }
            catch (System.Text.Json.JsonException) { return null; }
        }
        return new RunRecord
        {
            Id = reader.GetString(0),
            Op = reader.GetString(1),
            Kind = reader.GetString(2),
            Source = reader.GetString(3),
            Status = reader.GetString(4),
            Provider = reader.GetString(5),
            Model = reader.GetString(6),
            Api = Text(7),
            Account = Text(8),
            GroupId = Text(9),
            ParentId = Text(10),
            Session = Text(11),
            Title = Text(12),
            MessagesRef = Text(13),
            OutputText = Text(14),
            StopReason = Text(15),
            Error = Text(16),
            Notes = Text(17),
            Usage = new RunUsage(reader.GetDouble(18), reader.GetDouble(19), reader.GetDouble(20), reader.GetDouble(21), reader.GetDouble(22)),
            CostMicros = Long(23),
            CostKind = Text(24),
            Billing = Text(25),
            SpendOp = Text(26),
            TtftMs = Long(27),
            DurationMs = Long(28),
            TokensPerSecond = reader.IsDBNull(29) ? null : reader.GetDouble(29),
            Pinned = reader.GetInt64(30) != 0,
            RawState = reader.GetString(31),
            Created = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(32)),
            Started = Time(33),
            Finished = Time(34),
            Updated = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(35)),
            Options = full ? Json(36) as JsonObject : null,
            Messages = full ? Json(37) : null,
            Output = full ? Json(38) : null,
        };
    }

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;

    internal static long Ms(DateTimeOffset time) => time.ToUnixTimeMilliseconds();
}

/// <summary>Text helpers shared by the run area.</summary>
internal static class RunText
{
    /// <summary><paramref name="text"/> cut to <paramref name="maximum"/> characters (with an ellipsis), or null.</summary>
    public static string? Cut(string? text, int maximum)
    {
        if (text is null) return null;
        return text.Length <= maximum ? text : text[..(maximum - 1)] + "…";
    }

    /// <summary>The first line of <paramref name="text"/>, folded and cut to 120 characters, for a title.</summary>
    public static string? Title(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var builder = new StringBuilder();
        foreach (var character in text.Trim())
        {
            if (character is '\r' or '\n') break;
            builder.Append(char.IsWhiteSpace(character) ? ' ' : character);
            if (builder.Length > 200) break;
        }
        return Cut(builder.ToString().Trim(), 120);
    }
}
