// Decision 0011 (run W3, S3): where the Spend Guard keeps the user's one-time approvals and the calls it stopped (webagent.db, migrations
// 1100-1102). An approval answers an Ask once: "proceed once" or "cap the output at N tokens". It belongs to a run (the chat id) and, when the
// confirm modal answered one stopped call, to that call's op; it lives Lifetime (10 minutes) and is used up by the first call of its run
// that the guard stops, whether or not that call can then be sent, so one answer never lets two requests through. A newer approval for the
// same run and op replaces an unused older one (the user changed their mind). Taking one is a single BEGIN IMMEDIATE transaction, so two
// calls of one run that are stopped at the same time cannot both use it. A used approval remembers every ledger op it let through
// (spend_approval_uses: the one reservation of a single call, or every column of a Compare group; used_by keeps the first), so a recovered
// durable request that reuses any of those reservations is sent with the same output cap. An approval whose call still could not be sent
// (Refused) let nothing through and keeps no use. The stops are the decisions the guard made about calls it did not send, for the confirm
// modal of a call stopped inside a run. Old rows are pruned as new ones are written.
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Spend;

/// <summary>A one-time approval as stored.</summary>
/// <param name="Id">The store's id.</param>
/// <param name="Approval">What the user chose, for which run (and op).</param>
/// <param name="Created">When it was given.</param>
/// <param name="Expires">When it stops applying, if no call used it.</param>
public sealed record StoredApproval(long Id, SpendApproval Approval, DateTimeOffset Created, DateTimeOffset Expires)
{
    /// <summary>When a stopped call used it up (or a newer approval replaced it); null while it waits.</summary>
    public DateTimeOffset? Used { get; init; }

    /// <summary>The ledger op of the (first) reservation it let through, <see cref="SpendApprovalStore.Replaced"/> when a newer approval
    /// replaced it, or <see cref="SpendApprovalStore.Refused"/> when the call it answered still could not be sent; null while it waits. Every
    /// reservation it let through finds it through <see cref="SpendApprovalStore.UsedByAsync"/>.</summary>
    public string? UsedBy { get; init; }
}

/// <summary>A call the guard stopped, and why.</summary>
/// <param name="Op">The call's op (the durable request's <c>metadata.pisharp.op</c>, or the caller's).</param>
/// <param name="Call">The call as the guard saw it (provider, model, account, source, run, size, maxTokens).</param>
/// <param name="Decision">What the guard decided (Ask or Block, with the causes).</param>
/// <param name="Created">When.</param>
public sealed record SpendStop(string Op, SpendCall Call, SpendDecision Decision, DateTimeOffset Created)
{
    /// <summary>The error code the call ended with: <see cref="SpendErrors.Blocked"/>, <see cref="SpendErrors.Ask"/> or
    /// <see cref="SpendErrors.UnknownPrice"/>.</summary>
    public string Code => Decision.ErrorCode ?? SpendErrors.Ask;
}

/// <summary>The Spend Guard's approvals and stops in webagent.db (which must hold <see cref="SpendGuardMigrations.All"/>). A singleton;
/// safe to call from many threads.</summary>
public sealed class SpendApprovalStore
{
    /// <summary>How long an approval waits for a call to use it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long used approvals and stops are kept (a recovered request may need its approval's output cap).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>The <see cref="StoredApproval.UsedBy"/> of an approval a newer one replaced.</summary>
    public const string Replaced = "replaced";

    /// <summary>The <see cref="StoredApproval.UsedBy"/> of an approval whose call still could not be sent (a Block cap).</summary>
    public const string Refused = "refused";

    private const string ApprovalColumns = "id, run, op, choice, max_output_tokens, created_at, expires_at, used_at, used_by";

    private readonly WebAgentDb db;
    private readonly TimeProvider clock;

    /// <summary>The store in <paramref name="db"/>, on <paramref name="time"/> (the system clock when null).</summary>
    public SpendApprovalStore(WebAgentDb db, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        this.db = db;
        clock = time ?? TimeProvider.System;
    }

    /// <summary>Stores <paramref name="approval"/> (<see cref="SpendApproval.Normalize"/>); an unused approval for the same run and op is
    /// replaced. Returns it as stored.</summary>
    /// <exception cref="ArgumentException">The approval is not allowed, or its run or op is too long.</exception>
    public Task<StoredApproval> AddAsync(SpendApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var normalized = approval.Normalize();
        if (normalized.Run.Length > SpendRequest.MaxKeyLength || normalized.Op?.Length > SpendRequest.MaxKeyLength)
            throw new ArgumentException($"A run key or op is at most {SpendRequest.MaxKeyLength} characters.");
        cancellationToken.ThrowIfCancellationRequested();
        var now = Now();
        var stored = db.Transaction((connection, transaction) =>
        {
            Prune(connection, transaction, now);
            WebAgentDb.Execute(connection, transaction, "UPDATE spend_approvals SET used_at = $at, used_by = $replaced WHERE run = $run AND op IS $op AND used_at IS NULL;",
                ("$at", now.ToUnixTimeMilliseconds()), ("$replaced", Replaced), ("$run", normalized.Run), ("$op", normalized.Op));
            var expires = now + Lifetime;
            WebAgentDb.Execute(connection, transaction, """
                INSERT INTO spend_approvals (run, op, choice, max_output_tokens, created_at, expires_at) VALUES ($run, $op, $choice, $max, $at, $expires);
                """, ("$run", normalized.Run), ("$op", normalized.Op), ("$choice", Wire(normalized.Choice)), ("$max", (long?)normalized.MaxOutputTokens),
                ("$at", now.ToUnixTimeMilliseconds()), ("$expires", expires.ToUnixTimeMilliseconds()));
            var id = Convert.ToInt64(WebAgentDb.Scalar(connection, transaction, "SELECT last_insert_rowid();"), CultureInfo.InvariantCulture);
            return new StoredApproval(id, normalized, now, expires);
        });
        return Task.FromResult(stored);
    }

    /// <summary>The approval a stopped call of <paramref name="run"/> with <paramref name="op"/> would use now (one for that op first, else
    /// the run's), without using it; null when there is none (or no run).</summary>
    public Task<StoredApproval?> PeekAsync(string? run, string? op, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Blank(run) is not { } key) return Task.FromResult<StoredApproval?>(null);
        var now = Now();
        using var connection = db.Open();
        return Task.FromResult(Waiting(connection, null, key, Blank(op), now));
    }

    /// <summary>Uses up the approval a stopped call of <paramref name="run"/> with <paramref name="op"/> answers to (as
    /// <see cref="PeekAsync"/> finds it), recording it against <paramref name="usedBy"/> (the ledger op of the reservation it lets through);
    /// null when there is none. Only one caller gets it.</summary>
    public Task<StoredApproval?> TakeAsync(string? run, string? op, string usedBy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(usedBy);
        return TakeAsync(run, op, [usedBy], cancellationToken);
    }

    /// <summary>Uses up the approval a stopped group of <paramref name="run"/> with <paramref name="op"/> answers to (as
    /// <see cref="PeekAsync"/> finds it), recording it against every ledger op of <paramref name="ledgerOps"/> (the reservations it lets
    /// through: every new column of a Compare group), so <see cref="UsedByAsync"/> finds it for each; <see cref="StoredApproval.UsedBy"/> is
    /// the first. Null when there is none. Only one caller gets it.</summary>
    /// <exception cref="ArgumentException"><paramref name="ledgerOps"/> is empty, or one of them is blank or too long.</exception>
    public Task<StoredApproval?> TakeAsync(string? run, string? op, IReadOnlyList<string> ledgerOps, CancellationToken cancellationToken = default)
    {
        var uses = LedgerOps(ledgerOps);
        cancellationToken.ThrowIfCancellationRequested();
        if (Blank(run) is not { } key) return Task.FromResult<StoredApproval?>(null);
        var now = Now();
        return Task.FromResult(db.Transaction((connection, transaction) =>
        {
            if (Waiting(connection, transaction, key, Blank(op), now) is not { } found) return null;
            WebAgentDb.Execute(connection, transaction, "UPDATE spend_approvals SET used_at = $at, used_by = $by WHERE id = $id AND used_at IS NULL;",
                ("$at", now.ToUnixTimeMilliseconds()), ("$by", uses[0]), ("$id", found.Id));
            RecordUses(connection, transaction, found.Id, uses, now);
            return found with { Used = now, UsedBy = uses[0] };
        }));
    }

    /// <summary>Changes what a used approval says it was used by. <see cref="Refused"/> (its call or group still could not be sent) or
    /// <see cref="Replaced"/> forgets every reservation it was recorded against, since it let none through; a ledger op records it against
    /// that op alone.</summary>
    public Task MarkUsedByAsync(long id, string usedBy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(usedBy);
        cancellationToken.ThrowIfCancellationRequested();
        var by = usedBy.Trim();
        IReadOnlyList<string> uses = by is Refused or Replaced ? [] : LedgerOps([by]);
        var now = Now();
        db.Transaction((connection, transaction) =>
        {
            if (WebAgentDb.Execute(connection, transaction, "UPDATE spend_approvals SET used_by = $by WHERE id = $id AND used_at IS NOT NULL;", ("$by", by), ("$id", id)) == 0)
                return 0;
            WebAgentDb.Execute(connection, transaction, "DELETE FROM spend_approval_uses WHERE approval_id = $id;", ("$id", id));
            RecordUses(connection, transaction, id, uses, now);
            return 1;
        });
        return Task.CompletedTask;
    }

    /// <summary>The approval that let the reservation <paramref name="ledgerOp"/> through (a single call's, or any column of a Compare group
    /// it answered), or null.</summary>
    public Task<StoredApproval?> UsedByAsync(string ledgerOp, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Blank(ledgerOp) is not { } key) return Task.FromResult<StoredApproval?>(null);
        using var connection = db.Open();
        return Task.FromResult(Approvals(connection, null,
            $"SELECT {ApprovalColumns} FROM spend_approvals WHERE id = (SELECT approval_id FROM spend_approval_uses WHERE ledger_op = $by);",
            ("$by", key)).FirstOrDefault());
    }

    /// <summary>Every approval of <paramref name="run"/> that still waits for a call, newest first.</summary>
    public Task<IReadOnlyList<StoredApproval>> WaitingAsync(string run, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Blank(run) is not { } key) return Task.FromResult<IReadOnlyList<StoredApproval>>([]);
        using var connection = db.Open();
        return Task.FromResult<IReadOnlyList<StoredApproval>>(Approvals(connection, null,
            $"SELECT {ApprovalColumns} FROM spend_approvals WHERE run = $run AND used_at IS NULL AND expires_at > $now ORDER BY id DESC;",
            ("$run", key), ("$now", Now().ToUnixTimeMilliseconds())));
    }

    /// <summary>Records <paramref name="stop"/> (a later stop of the same op replaces it).</summary>
    public Task RecordStopAsync(SpendStop stop, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentException.ThrowIfNullOrWhiteSpace(stop.Op);
        cancellationToken.ThrowIfCancellationRequested();
        var call = stop.Call;
        var json = JsonSerializer.Serialize(stop.Decision, SettingsStore.JsonOptions);
        db.Transaction((connection, transaction) =>
        {
            Prune(connection, transaction, Now());
            return WebAgentDb.Execute(connection, transaction, """
                INSERT INTO spend_stops (op, run, source, provider, model, account, code, decision, input_bytes, max_tokens, created_at)
                VALUES ($op, $run, $source, $provider, $model, $account, $code, $decision, $bytes, $max, $at)
                ON CONFLICT (op) DO UPDATE SET run = excluded.run, source = excluded.source, provider = excluded.provider, model = excluded.model,
                  account = excluded.account, code = excluded.code, decision = excluded.decision, input_bytes = excluded.input_bytes,
                  max_tokens = excluded.max_tokens, created_at = excluded.created_at;
                """, ("$op", stop.Op.Trim()), ("$run", call.Run), ("$source", call.Source.Wire()), ("$provider", call.Provider), ("$model", call.Model),
                ("$account", call.Account), ("$code", stop.Code), ("$decision", json), ("$bytes", call.InputBytes), ("$max", (long?)call.MaxTokens),
                ("$at", stop.Created.ToUnixTimeMilliseconds()));
        });
        return Task.CompletedTask;
    }

    /// <summary>The stop of <paramref name="op"/>, or null.</summary>
    public Task<SpendStop?> StopAsync(string op, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Blank(op) is not { } key) return Task.FromResult<SpendStop?>(null);
        return Task.FromResult(Stops("WHERE op = $op", ("$op", key)).FirstOrDefault());
    }

    /// <summary>The latest <paramref name="count"/> stops of <paramref name="run"/>, newest first.</summary>
    public Task<IReadOnlyList<SpendStop>> StopsAsync(string run, int count = 20, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 1000);
        cancellationToken.ThrowIfCancellationRequested();
        if (Blank(run) is not { } key) return Task.FromResult<IReadOnlyList<SpendStop>>([]);
        return Task.FromResult<IReadOnlyList<SpendStop>>(Stops("WHERE run = $run ORDER BY created_at DESC, rowid DESC LIMIT $count", ("$run", key), ("$count", (long)count)));
    }

    /// <summary>The wire name of <paramref name="choice"/>: <c>proceed-once</c>, <c>cap-output</c>.</summary>
    public static string Wire(SpendChoice choice) => choice == SpendChoice.CapOutput ? "cap-output" : "proceed-once";

    /// <summary>Records <paramref name="approvalId"/> as the approval that let each of <paramref name="ledgerOps"/> through (a later
    /// approval of the same ledger op replaces it).</summary>
    private static void RecordUses(SqliteConnection connection, SqliteTransaction transaction, long approvalId, IReadOnlyList<string> ledgerOps, DateTimeOffset now)
    {
        foreach (var ledgerOp in ledgerOps)
            WebAgentDb.Execute(connection, transaction, """
                INSERT INTO spend_approval_uses (ledger_op, approval_id, recorded_at) VALUES ($op, $id, $at)
                ON CONFLICT (ledger_op) DO UPDATE SET approval_id = excluded.approval_id, recorded_at = excluded.recorded_at;
                """, ("$op", ledgerOp), ("$id", approvalId), ("$at", now.ToUnixTimeMilliseconds()));
    }

    /// <summary><paramref name="ledgerOps"/> trimmed, checked and without repeats, in order.</summary>
    /// <exception cref="ArgumentException">There is none, or one is blank or too long.</exception>
    private static List<string> LedgerOps(IReadOnlyList<string> ledgerOps)
    {
        ArgumentNullException.ThrowIfNull(ledgerOps);
        var found = new List<string>();
        foreach (var ledgerOp in ledgerOps)
        {
            if (Blank(ledgerOp) is not { } key) throw new ArgumentException("A ledger op is never blank.", nameof(ledgerOps));
            if (key.Length > SpendRequest.MaxKeyLength) throw new ArgumentException($"A ledger op is at most {SpendRequest.MaxKeyLength} characters.", nameof(ledgerOps));
            if (!found.Contains(key, StringComparer.Ordinal)) found.Add(key);
        }
        return found.Count > 0 ? found : throw new ArgumentException("Name at least one ledger op the approval lets through.", nameof(ledgerOps));
    }

    private StoredApproval? Waiting(SqliteConnection connection, SqliteTransaction? transaction, string run, string? op, DateTimeOffset now) =>
        Approvals(connection, transaction, $"""
            SELECT {ApprovalColumns} FROM spend_approvals
            WHERE run = $run AND used_at IS NULL AND expires_at > $now AND (op IS NULL OR op = $op)
            ORDER BY (op IS NULL), id DESC LIMIT 1;
            """, ("$run", run), ("$now", now.ToUnixTimeMilliseconds()), ("$op", op)).FirstOrDefault();

    private static List<StoredApproval> Approvals(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string, object?)[] parameters)
    {
        using var command = WebAgentDb.Command(connection, transaction, sql, parameters);
        using var reader = command.ExecuteReader();
        var found = new List<StoredApproval>();
        while (reader.Read())
        {
            var approval = new SpendApproval(reader.GetString(1), reader.GetString(3) == "cap-output" ? SpendChoice.CapOutput : SpendChoice.ProceedOnce)
            {
                Op = reader.IsDBNull(2) ? null : reader.GetString(2),
                MaxOutputTokens = reader.IsDBNull(4) ? null : (int)reader.GetInt64(4),
            };
            found.Add(new StoredApproval(reader.GetInt64(0), approval, Time(reader.GetInt64(5)), Time(reader.GetInt64(6)))
            {
                Used = reader.IsDBNull(7) ? null : Time(reader.GetInt64(7)),
                UsedBy = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }
        return found;
    }

    private List<SpendStop> Stops(string where, params (string, object?)[] parameters) =>
        [.. db.Query($"SELECT op, run, source, provider, model, account, decision, input_bytes, max_tokens, created_at FROM spend_stops {where};", reader =>
        {
            var call = new SpendCall
            {
                Op = reader.GetString(0),
                Run = reader.IsDBNull(1) ? null : reader.GetString(1),
                Source = SpendSources.Parse(reader.GetString(2)),
                Provider = reader.GetString(3),
                Model = reader.GetString(4),
                Account = reader.IsDBNull(5) ? null : reader.GetString(5),
                InputBytes = reader.GetInt64(7),
                MaxTokens = reader.IsDBNull(8) ? null : (int)reader.GetInt64(8),
            };
            var decision = JsonSerializer.Deserialize<SpendDecision>(reader.GetString(6), SettingsStore.JsonOptions)
                ?? throw new InvalidDataException($"The stop of {call.Op} has no decision.");
            return new SpendStop(call.Op, call, decision, Time(reader.GetInt64(9)));
        }, parameters)];

    /// <summary>Drops approvals that expired unused a day ago, used approvals and stops older than <see cref="Retention"/>, and the uses of
    /// approvals no longer kept.</summary>
    private static void Prune(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
    {
        var old = (now - Retention).ToUnixTimeMilliseconds();
        WebAgentDb.Execute(connection, transaction, "DELETE FROM spend_approvals WHERE (used_at IS NULL AND expires_at < $day) OR (used_at IS NOT NULL AND used_at < $old);",
            ("$day", (now - TimeSpan.FromDays(1)).ToUnixTimeMilliseconds()), ("$old", old));
        WebAgentDb.Execute(connection, transaction, "DELETE FROM spend_approval_uses WHERE approval_id NOT IN (SELECT id FROM spend_approvals);");
        WebAgentDb.Execute(connection, transaction, "DELETE FROM spend_stops WHERE created_at < $old;", ("$old", old));
    }

    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());

    private static DateTimeOffset Time(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
