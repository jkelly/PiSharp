// PiSharp-only (decision 0010, M6): retention of the catalog's application records. The catalog domain only: settled budget reservations
// and judgment-cache entries after 90 days; fetch receipts, source item bodies and retrieval validators after 30 days; then unreferenced
// blobs. A purge never touches what is still needed: unresolved reservations, the fetch receipts of a run that is not terminal (a rerun
// replays them), blobs any evidence or other owner still references, operation and App State receipts, feedback events, policies,
// sources and schedules. Session records (judgments, artifacts, evidence, executions) live in each session's durable store, which has no
// deletion; archiving or deleting the session removes them. A dry run counts; a purge removes at most a batch per category, so a large
// backlog clears over several calls without holding the catalog lock for long.
using System.Globalization;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>How long the device keeps settled application records.</summary>
public sealed record RetentionPolicy
{
    /// <summary>Settled or released budget reservations and judgment-cache entries: 90 days by default.</summary>
    public TimeSpan Metadata { get; init; } = TimeSpan.FromDays(90);

    /// <summary>Fetched source bodies, their fetch receipts and retrieval validators: 30 days by default.</summary>
    public TimeSpan SourceBodies { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How long a blob no record references stays before it is collected.</summary>
    public TimeSpan BlobGrace { get; init; } = TimeSpan.FromDays(1);
}

/// <summary>One kind of record a retention pass looked at.</summary>
/// <param name="Id">The category's ID.</param>
/// <param name="Eligible">Records past their retention.</param>
/// <param name="Purged">Records this pass removed; 0 in a dry run.</param>
/// <param name="Kept">Records past their retention that are still needed and stay.</param>
public sealed record RetentionCategory(string Id, int Eligible, int Purged, int Kept);

/// <summary>What a retention pass found or did.</summary>
/// <param name="DryRun">Nothing was removed.</param>
/// <param name="At">When the pass ran, Unix milliseconds.</param>
/// <param name="Categories">Per category.</param>
/// <param name="Blobs">The blob collection that followed (counted in a dry run).</param>
/// <param name="More">Eligible records remain after this batch; run the purge again.</param>
public sealed record RetentionReport(bool DryRun, long At, IReadOnlyList<RetentionCategory> Categories, BlobCollection Blobs, bool More)
{
    /// <summary>What a retention pass never removes, for people who read the report.</summary>
    public static IReadOnlyList<string> AlwaysKept { get; } =
    [
        "unresolved (reserved or uncertain) budget reservations",
        "fetch receipts of runs that are not terminal",
        "blobs referenced by evidence or any other record",
        "operation, App State and execution-start receipts",
        "feedback events (until deleted) and preference profiles",
        "policies, sources, schedules and trigger credentials",
        "session records (judgments, artifacts, evidence, executions): archive or delete the session to remove them",
    ];

    /// <summary>The report as JSON.</summary>
    public JsonObjectValue ToJson() => JsonValue.Object(
    [
        new("dryRun", DryRun), new("at", At), new("more", More),
        new("categories", JsonValue.Array(Categories.Select(category => (JsonValue)JsonValue.Object(
        [
            new("id", category.Id), new("eligible", category.Eligible), new("purged", category.Purged), new("kept", category.Kept),
        ])))),
        new("blobs", JsonValue.Object(
        [
            new("blobs", Blobs.Blobs), new("bytes", Blobs.Bytes), new("orphanFiles", Blobs.OrphanFiles), new("temporaryFiles", Blobs.TemporaryFiles),
        ])),
        new("alwaysKept", JsonValue.Array(AlwaysKept.Select(item => (JsonValue)item))),
    ]);
}

/// <summary>Applies the device's <see cref="RetentionPolicy"/> to the catalog.</summary>
public sealed class DeviceRetention
{
    /// <summary>The default and largest batch.</summary>
    public const int DefaultBatch = 500, MaxBatch = 5000;

    private readonly DurableDeviceHost host;

    internal DeviceRetention(DurableDeviceHost host) => this.host = host;

    /// <summary>The policy in force.</summary>
    public RetentionPolicy Policy => host.Options.Retention;

    /// <summary>Counts (<paramref name="dryRun"/>) or removes up to <paramref name="batch"/> records per category, then collects unreferenced
    /// blobs.</summary>
    public async Task<RetentionReport> RunAsync(bool dryRun, int batch = DefaultBatch, CancellationToken cancellationToken = default)
    {
        if (batch is < 1 or > MaxBatch) throw new HostInvalidException($"A retention batch is 1 to {MaxBatch} records.");
        var catalog = host.Catalog;
        var now = host.Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var metadata = now - (long)Policy.Metadata.TotalMilliseconds;
        var bodies = now - (long)Policy.SourceBodies.TotalMilliseconds;
        var categories = new List<RetentionCategory>();
        var more = false;

        RetentionCategory Simple(string id, string where, string table, string key, (string, object?)[] parameters, string? keptWhere = null)
        {
            var eligible = Count(table, where, parameters);
            var kept = keptWhere is null ? 0 : Count(table, keptWhere, parameters);
            var purged = 0;
            if (!dryRun && eligible > 0)
                purged = catalog.Transact(transaction =>
                {
                    var keys = catalog.Rows(transaction, $"SELECT {key} FROM {table} WHERE {where} ORDER BY {key} LIMIT $batch;", reader => reader.GetString(0),
                        [.. parameters, ("$batch", (long)batch)]);
                    foreach (var item in keys) catalog.Run(transaction, $"DELETE FROM {table} WHERE {key} = $key;", ("$key", item));
                    return keys.Count;
                });
            more |= purged < eligible && !dryRun;
            return new RetentionCategory(id, eligible, purged, kept);
        }

        int Count(string table, string where, (string, object?)[] parameters) => catalog.Read(_ =>
            Convert.ToInt32(catalog.Value(null, $"SELECT COUNT(*) FROM {table} WHERE {where};", parameters), CultureInfo.InvariantCulture));

        categories.Add(Simple("budget-reservations", "status IN ('settled', 'released') AND updated_at < $cutoff", "budget_reservations", "operation_id",
            [("$cutoff", metadata)], keptWhere: "status IN ('reserved', 'uncertain') AND updated_at < $cutoff"));
        categories.Add(Simple("judgment-cache", "expires_at < $now OR created_at < $cutoff", "judgment_cache", "key", [("$now", now), ("$cutoff", metadata)]));
        categories.Add(await FetchReceiptsAsync(bodies, dryRun, batch, cancellationToken).ConfigureAwait(false));
        if (!dryRun && categories[^1].Eligible - categories[^1].Kept > categories[^1].Purged) more = true;
        categories.Add(SourceItemBodies(bodies, dryRun, batch, ref more));
        categories.Add(Simple("retrieval-cache", "fetched_at < $cutoff", "retrieval_cache", "key", [("$cutoff", bodies)]));
        var blobs = host.Blobs.Collect(Policy.BlobGrace, dryRun);
        return new RetentionReport(dryRun, now, categories, blobs, more);
    }

    /// <summary>Fetch receipts past <paramref name="cutoff"/> whose run is terminal, with their <c>fetch:</c> blob references.</summary>
    private async Task<RetentionCategory> FetchReceiptsAsync(long cutoff, bool dryRun, int batch, CancellationToken cancellationToken)
    {
        var catalog = host.Catalog;
        var rows = catalog.Read(_ => catalog.Rows(null, "SELECT operation_id, run_key FROM fetch_receipts WHERE created_at < $cutoff ORDER BY created_at, operation_id;",
            reader => (Operation: reader.GetString(0), Run: reader.GetString(1)), ("$cutoff", cutoff)));
        var active = new Dictionary<string, bool>(StringComparer.Ordinal);
        var purgeable = new List<string>();
        var kept = 0;
        foreach (var (operation, run) in rows)
        {
            if (!active.TryGetValue(run, out var running)) active[run] = running = await RunActiveAsync(run, cancellationToken).ConfigureAwait(false);
            if (running) kept++;
            else purgeable.Add(operation);
        }
        var purged = 0;
        if (!dryRun)
            purged = catalog.Transact(transaction =>
            {
                var removed = 0;
                foreach (var operation in purgeable.Take(batch))
                {
                    catalog.Run(transaction, "DELETE FROM fetch_receipts WHERE operation_id = $id;", ("$id", operation));
                    catalog.Run(transaction, "DELETE FROM blob_refs WHERE owner = $owner;", ("$owner", "fetch:" + operation));
                    removed++;
                }
                return removed;
            });
        return new RetentionCategory("fetch-receipts", rows.Count, purged, kept);
    }

    /// <summary>The text bodies of source items last seen before <paramref name="cutoff"/>: their <c>source-item:</c> references go and the
    /// item keeps its hashes (change detection) without a body.</summary>
    private RetentionCategory SourceItemBodies(long cutoff, bool dryRun, int batch, ref bool more)
    {
        var catalog = host.Catalog;
        const string Where = "text_hash IS NOT NULL AND last_seen_at < $cutoff";
        var eligible = catalog.Read(_ => Convert.ToInt32(catalog.Value(null, $"SELECT COUNT(*) FROM source_items WHERE {Where};", ("$cutoff", cutoff)),
            CultureInfo.InvariantCulture));
        var purged = 0;
        if (!dryRun && eligible > 0)
            purged = catalog.Transact(transaction =>
            {
                var items = catalog.Rows(transaction, $"SELECT source_id, item_id FROM source_items WHERE {Where} ORDER BY last_seen_at, source_id, item_id LIMIT $batch;",
                    reader => (Source: reader.GetString(0), Item: reader.GetString(1)), ("$cutoff", cutoff), ("$batch", (long)batch));
                foreach (var (source, item) in items)
                {
                    catalog.Run(transaction, "DELETE FROM blob_refs WHERE owner = $owner;", ("$owner", $"source-item:{source}:{item}"));
                    catalog.Run(transaction, "UPDATE source_items SET text_hash = NULL WHERE source_id = $source AND item_id = $item;", ("$source", source), ("$item", item));
                }
                return items.Count;
            });
        more |= !dryRun && purged < eligible;
        return new RetentionCategory("source-item-bodies", eligible, purged, 0);
    }

    /// <summary>Whether the run <c>session/task</c> may still replay its receipts. A run whose session is gone is not; one whose session
    /// cannot be read now, or whose key is not a run key, is treated as running.</summary>
    private async Task<bool> RunActiveAsync(string runKey, CancellationToken cancellationToken)
    {
        var slash = runKey.LastIndexOf('/');
        if (slash <= 0 || !long.TryParse(runKey.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var taskId)) return true;
        var sessionId = runKey[..slash];
        var session = host.Sessions.FirstOrDefault(item => string.Equals(item.Id, sessionId, StringComparison.OrdinalIgnoreCase));
        if (session is null) return false;
        try
        {
            return await session.TaskActiveAsync(taskId, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionUnavailableException)
        {
            return true;
        }
    }
}

public sealed partial class HostedSession
{
    /// <summary>Whether the task exists and is not terminal.</summary>
    internal async Task<bool> TaskActiveAsync(long taskId, CancellationToken cancellationToken)
    {
        var task = await GetHarness().CommitAsync(tx => tx.GetTaskAsync(new TaskId(taskId)), Context(cancellationToken)).ConfigureAwait(false);
        return task is not null && task.State is not TerminalTaskState;
    }
}
