// PiSharp-only (decision 0010): catalog version 5, the device-wide application records of M2. Rows are written in single transactions under
// the catalog lock; a session commit never shares one with them, so every cross-store step goes through a stable operation ID.
using Microsoft.Data.Sqlite;

namespace PiSharp.Durable.Hosting;

/// <summary>A stored operation result and whether it was a replay.</summary>
internal sealed record OperationResult(string Result, bool Replayed);

internal sealed partial class SessionCatalog
{
    private const string SchemaV5 = """
        CREATE TABLE operations (key TEXT PRIMARY KEY, kind TEXT NOT NULL, request_hash TEXT NOT NULL, result TEXT NOT NULL, created_at INTEGER NOT NULL);
        CREATE TABLE blobs (hash TEXT PRIMARY KEY, size INTEGER NOT NULL, content_type TEXT NOT NULL, created_at INTEGER NOT NULL);
        CREATE TABLE blob_refs (hash TEXT NOT NULL, owner TEXT NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY (hash, owner));
        CREATE INDEX blob_refs_owner ON blob_refs(owner);
        CREATE TABLE budget_limits (meter TEXT PRIMARY KEY, per_day INTEGER NOT NULL);
        CREATE TABLE budget_reservations (
          operation_id TEXT PRIMARY KEY, run_key TEXT NOT NULL, provider TEXT NOT NULL, day TEXT NOT NULL, status TEXT NOT NULL,
          requested TEXT NOT NULL, actual TEXT, cost_known INTEGER NOT NULL, detail TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL
        );
        CREATE INDEX budget_reservations_run ON budget_reservations(run_key);
        CREATE INDEX budget_reservations_day ON budget_reservations(day);
        CREATE TABLE policies (
          id TEXT PRIMARY KEY, active_version INTEGER, revision INTEGER NOT NULL, latest_version INTEGER NOT NULL,
          created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL
        );
        CREATE TABLE policy_versions (
          policy_id TEXT NOT NULL, version INTEGER NOT NULL, spec TEXT NOT NULL, hash TEXT NOT NULL, created_at INTEGER NOT NULL,
          PRIMARY KEY (policy_id, version)
        );
        CREATE TABLE judgment_cache (key TEXT PRIMARY KEY, value TEXT NOT NULL, created_at INTEGER NOT NULL, expires_at INTEGER NOT NULL);
        """;

    /// <summary>
    /// Runs a catalog mutation once per key: a key already used with the same request hash returns its stored result, with a different hash
    /// throws <see cref="HostConflictException"/>. <paramref name="apply"/> runs in the transaction that stores the receipt.
    /// </summary>
    public OperationResult RunOperation(string key, string kind, string requestHash, long now, Func<SqliteTransaction, string> apply)
    {
        if (key.Length is 0 or > 200) throw new HostInvalidException("An Idempotency-Key must have 1 to 200 characters.");
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            using (var existing = Command(connection, transaction, "SELECT kind, request_hash, result FROM operations WHERE key = $key;", [("$key", key)]))
            using (var reader = existing.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.GetString(0) != kind || reader.GetString(1) != requestHash)
                        throw new HostConflictException("This Idempotency-Key was already used for a different request.");
                    return new OperationResult(reader.GetString(2), true);
                }
            }
            var result = apply(transaction);
            Execute(connection, transaction, "INSERT INTO operations (key, kind, request_hash, result, created_at) VALUES ($key, $kind, $hash, $result, $at);",
                ("$key", key), ("$kind", kind), ("$hash", requestHash), ("$result", result), ("$at", now));
            transaction.Commit();
            return new OperationResult(result, false);
        }
    }

    /// <summary>Runs <paramref name="change"/> in one catalog transaction under the lock.</summary>
    internal T Transact<T>(Func<SqliteTransaction, T> change)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            var result = change(transaction);
            transaction.Commit();
            return result;
        }
    }

    /// <summary>Runs <paramref name="read"/> under the lock without a transaction.</summary>
    internal T Read<T>(Func<SqliteConnection, T> read)
    {
        lock (gate) return read(connection);
    }

    internal void Run(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters) =>
        Execute(connection, transaction, sql, parameters);

    internal object? Value(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters) =>
        Scalar(connection, transaction, sql, parameters);

    internal List<T> Rows<T>(SqliteTransaction? transaction, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    /// <summary>A meta value; null when absent.</summary>
    internal string? Meta(SqliteTransaction? transaction, string key) => Scalar(connection, transaction, "SELECT value FROM meta WHERE key = $key;", ("$key", key)) as string;

    /// <summary>Sets a meta value.</summary>
    internal void SetMeta(SqliteTransaction? transaction, string key, string value) =>
        Execute(connection, transaction, "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            ("$key", key), ("$value", value));
}
