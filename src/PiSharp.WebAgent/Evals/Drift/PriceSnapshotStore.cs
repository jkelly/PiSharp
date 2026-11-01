// Decision 0011 (run W5, E3): Price Watch's rows in webagent.db, on E0's tables (migration 2004): eval_price_snapshots (one row per provider
// and catalog hash seen, the whole PriceSnapshot as JSON in `data`) and eval_price_changes (one row per rate that moved, beside the snapshot
// that saw it). Record is the only writer and is atomic: in one BEGIN IMMEDIATE transaction it reads the provider's latest snapshot, saves the
// new one only when the hash differs, and saves its changes against that latest one, so two checks at once can never save the same hash
// twice. Rows are kept: snapshots are small and few (a new hash means an app update or an edited models.json), and the drift page lists the
// last 30 days of changes. PriceAt answers "what did this model cost then" for the cost rule's price/verbosity split: the provider's latest
// snapshot taken at or before the time, or its earliest when the time is before every snapshot (prices are assumed unchanged before Price
// Watch first looked).
using Microsoft.Data.Sqlite;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Evals.Drift;

/// <summary>A stored price change: its row id and the snapshot that saw it.</summary>
/// <param name="Id">The eval_price_changes row id.</param>
/// <param name="SnapshotId">The snapshot that saw it (its <see cref="PriceChange.AfterSha256"/>).</param>
/// <param name="Change">The change.</param>
public sealed record PriceChangeRow(long Id, long SnapshotId, PriceChange Change);

/// <summary>A snapshot saved because its hash was new, with the changes against the provider's previous snapshot (none for the first).</summary>
/// <param name="Snapshot">The saved snapshot (its <see cref="PriceSnapshot.Id"/> set).</param>
/// <param name="Previous">The provider's snapshot before it, or null for the first.</param>
/// <param name="Changes">The rates that moved.</param>
public sealed record PriceRecorded(PriceSnapshot Snapshot, PriceSnapshot? Previous, IReadOnlyList<PriceChangeRow> Changes);

/// <summary>Price snapshots and changes (webagent.db).</summary>
public interface IPriceSnapshotStore
{
    /// <summary>Saves <paramref name="snapshot"/> and its changes when its hash differs from the provider's latest snapshot; null when it is
    /// the same (nothing saved).</summary>
    PriceRecorded? Record(PriceSnapshot snapshot);

    /// <summary>The provider's latest snapshot, or null before the first.</summary>
    PriceSnapshot? Latest(string provider);

    /// <summary>The snapshot with id <paramref name="id"/>, or null.</summary>
    PriceSnapshot? Get(long id);

    /// <summary>The provider's snapshot before <paramref name="snapshot"/>, or null.</summary>
    PriceSnapshot? Previous(PriceSnapshot snapshot);

    /// <summary>The provider's snapshot in effect at <paramref name="at"/>: the latest taken at or before it, else the earliest; null when
    /// there are none.</summary>
    PriceSnapshot? At(string provider, DateTimeOffset at);

    /// <summary>The changes detected at or after <paramref name="since"/>, newest first.</summary>
    IReadOnlyList<PriceChangeRow> Changes(DateTimeOffset since);
}

/// <summary><see cref="IPriceSnapshotStore"/> on webagent.db. A singleton; safe to call from many threads.</summary>
/// <param name="db">webagent.db (with the evals migrations registered).</param>
public sealed class PriceSnapshotStore(WebAgentDb db) : IPriceSnapshotStore
{
    private const string Columns = "id, provider, catalog_sha256, data, taken_at";

    /// <inheritdoc/>
    public PriceRecorded? Record(PriceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.Provider) || string.IsNullOrWhiteSpace(snapshot.CatalogSha256))
            throw new ArgumentException("A price snapshot names its provider and catalog hash.");
        return db.Transaction((connection, transaction) =>
        {
            // The hash is compared from its column, so a row whose JSON cannot be read still stops the same hash being saved again.
            var latestHash = WebAgentDb.Scalar(connection, transaction,
                "SELECT catalog_sha256 FROM eval_price_snapshots WHERE provider = $provider ORDER BY id DESC LIMIT 1;", ("$provider", snapshot.Provider)) as string;
            if (string.Equals(latestHash, snapshot.CatalogSha256, StringComparison.Ordinal)) return null;
            var latest = latestHash is null ? null
                : One(connection, transaction, $"SELECT {Columns} FROM eval_price_snapshots WHERE provider = $provider ORDER BY id DESC LIMIT 1;", ("$provider", snapshot.Provider));
            var changes = snapshot.ChangesSince(latest);
            WebAgentDb.Execute(connection, transaction,
                "INSERT INTO eval_price_snapshots (provider, catalog_sha256, data, taken_at) VALUES ($provider, $sha, $data, $at);",
                ("$provider", snapshot.Provider), ("$sha", snapshot.CatalogSha256), ("$data", EvalJson.Serialize(snapshot with { Id = 0 })),
                ("$at", EvalJson.ToColumn(snapshot.Taken)));
            var id = LastId(connection, transaction);
            var rows = new List<PriceChangeRow>();
            foreach (var change in changes)
            {
                WebAgentDb.Execute(connection, transaction,
                    "INSERT INTO eval_price_changes (snapshot_id, provider, model, field, before_rate, after_rate, detected_at) " +
                    "VALUES ($snapshot, $provider, $model, $field, $before, $after, $at);",
                    ("$snapshot", id), ("$provider", change.Provider), ("$model", change.Model), ("$field", EvalJson.Wire(change.Field)),
                    ("$before", change.Before), ("$after", change.After), ("$at", EvalJson.ToColumn(change.Detected)));
                rows.Add(new PriceChangeRow(LastId(connection, transaction), id, change));
            }
            return new PriceRecorded(snapshot with { Id = id }, latest, rows);
        });
    }

    /// <inheritdoc/>
    public PriceSnapshot? Latest(string provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return First($"SELECT {Columns} FROM eval_price_snapshots WHERE provider = $provider ORDER BY id DESC LIMIT 1;", ("$provider", provider));
    }

    /// <inheritdoc/>
    public PriceSnapshot? Get(long id) => First($"SELECT {Columns} FROM eval_price_snapshots WHERE id = $id;", ("$id", id));

    /// <inheritdoc/>
    public PriceSnapshot? Previous(PriceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return First($"SELECT {Columns} FROM eval_price_snapshots WHERE provider = $provider AND id < $id ORDER BY id DESC LIMIT 1;",
            ("$provider", snapshot.Provider), ("$id", snapshot.Id));
    }

    /// <inheritdoc/>
    public PriceSnapshot? At(string provider, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return First($"SELECT {Columns} FROM eval_price_snapshots WHERE provider = $provider AND taken_at <= $at ORDER BY taken_at DESC, id DESC LIMIT 1;",
                ("$provider", provider), ("$at", EvalJson.ToColumn(at)))
            ?? First($"SELECT {Columns} FROM eval_price_snapshots WHERE provider = $provider ORDER BY taken_at, id LIMIT 1;", ("$provider", provider));
    }

    /// <inheritdoc/>
    public IReadOnlyList<PriceChangeRow> Changes(DateTimeOffset since)
    {
        var rows = db.Query("""
            SELECT c.id, c.snapshot_id, c.provider, c.model, c.field, c.before_rate, c.after_rate, c.detected_at, s.catalog_sha256,
              (SELECT p.catalog_sha256 FROM eval_price_snapshots p WHERE p.provider = c.provider AND p.id < c.snapshot_id ORDER BY p.id DESC LIMIT 1)
            FROM eval_price_changes c JOIN eval_price_snapshots s ON s.id = c.snapshot_id
            WHERE c.detected_at >= $since
            ORDER BY c.detected_at DESC, c.id;
            """, reader => EvalJson.FromWire<PriceField>(reader.GetString(4)) is { } field
                ? new PriceChangeRow(reader.GetInt64(0), reader.GetInt64(1), new PriceChange(reader.GetString(2), reader.GetString(3), field,
                    reader.IsDBNull(5) ? null : reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetDouble(6), EvalJson.FromColumn(reader.GetInt64(7)),
                    reader.IsDBNull(9) ? "" : reader.GetString(9), reader.GetString(8)))
                : null,
            ("$since", EvalJson.ToColumn(since)));
        return [.. rows.OfType<PriceChangeRow>()];
    }

    private static long LastId(SqliteConnection connection, SqliteTransaction transaction) =>
        Convert.ToInt64(WebAgentDb.Scalar(connection, transaction, "SELECT last_insert_rowid();"), System.Globalization.CultureInfo.InvariantCulture);

    private PriceSnapshot? First(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = db.Open();
        return One(connection, null, sql, parameters);
    }

    private static PriceSnapshot? One(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = WebAgentDb.Command(connection, transaction, sql, parameters);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // A row whose JSON cannot be read is skipped, as every evals store does; its hash still counts as seen through the next row.
            if (EvalJson.TryDeserialize<PriceSnapshot>(reader.GetString(3)) is not { } stored) continue;
            return stored with
            {
                Id = reader.GetInt64(0),
                Provider = reader.GetString(1),
                CatalogSha256 = reader.GetString(2),
                Taken = EvalJson.FromColumn(reader.GetInt64(4)),
                Models = stored.Models ?? [],
            };
        }
        return null;
    }
}
