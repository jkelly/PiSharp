// Decision 0011 (run W4, R2): Compare groups in webagent.db (migration 500 of the compare range 500-599; R5's agent compare uses 580-599, so
// request mode keeps 500-519 and R7 the ids between). One row per group (a Run all): its id is the run group id runs.db records on every
// column's row, and its data the shared request (prompt, system prompt, shared options), each column's model, overrides and chips with the
// run it shows, and the run marked best. runs.db stays the record of what was sent and what came back; this row only remembers the page:
// the column order, the overrides and the mark. A group whose runs are all gone from runs.db (deleted, or past retention) is removed: at the
// store's first use and whenever runs.db deletes or cleans up runs.
using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Compare;

/// <summary>A column of a stored Compare group: its model and overrides, the chips it was sent with and the run it shows.</summary>
public sealed record CompareGroupColumn
{
    /// <summary>The model and overrides.</summary>
    public required CompareColumnDraft Column { get; init; }

    /// <summary>The run of runs.db this column shows.</summary>
    public required string RunId { get; init; }

    /// <summary>The overrides and how the shared options were applied when it was sent.</summary>
    public IReadOnlyList<CompareChip> Chips { get; init; } = [];
}

/// <summary>A stored Compare group (one Run all, and the columns re-run in it since).</summary>
public sealed record CompareGroup
{
    /// <summary>The group id (runs.db's group id of every column's run).</summary>
    public required string Id { get; init; }

    /// <summary>The shared request: prompt, system prompt and shared options (no columns).</summary>
    public required CompareDraft Shared { get; init; }

    /// <summary>The columns, left to right.</summary>
    public IReadOnlyList<CompareGroupColumn> Columns { get; init; } = [];

    /// <summary>The run marked best, or null.</summary>
    public string? Best { get; init; }

    /// <summary>When Run all sent it.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset Updated { get; init; }

    /// <summary>The group as a draft: the shared request and the columns.</summary>
    [JsonIgnore]
    public CompareDraft Draft => Shared with { Columns = [.. Columns.Select(column => column.Column)] };

    /// <summary>The position of run <paramref name="runId"/>, or -1.</summary>
    public int IndexOf(string runId)
    {
        for (var index = 0; index < Columns.Count; index++)
            if (Columns[index].RunId == runId) return index;
        return -1;
    }
}

/// <summary>Compare groups in webagent.db. A singleton; safe to call from many threads.</summary>
public sealed class CompareStore
{
    /// <summary>The table.</summary>
    public static SqlMigration Migration { get; } = new(500, "compare: request-mode groups", """
        CREATE TABLE compare_groups (
            id TEXT PRIMARY KEY,
            data TEXT NOT NULL,
            created INTEGER NOT NULL,
            updated INTEGER NOT NULL
        );
        CREATE INDEX compare_groups_created ON compare_groups (created);
        """);

    private readonly WebAgentDb db;
    private readonly RunStore? runs;
    private readonly ILogger? log;
    private readonly Lock gate = new();
    private int pruned;

    /// <summary>A store over <paramref name="db"/>; with <paramref name="runs"/>, groups whose runs are all gone are removed.</summary>
    public CompareStore(WebAgentDb db, RunStore? runs = null, ILogger<CompareStore>? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        this.db = db;
        this.runs = runs;
        this.log = log;
        if (runs is not null) runs.Changed += OnRunsChanged;
    }

    /// <summary>Group <paramref name="id"/>, or null.</summary>
    public CompareGroup? Get(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        PruneOnce();
        return Read(id);
    }

    /// <summary>Groups, newest first.</summary>
    public IReadOnlyList<CompareGroup> List(int limit = 50)
    {
        PruneOnce();
        return [.. db.Query("SELECT data FROM compare_groups ORDER BY created DESC, id DESC LIMIT $limit;", reader => Parse(reader.GetString(0)),
            ("$limit", Math.Clamp(limit, 1, 500))).OfType<CompareGroup>()];
    }

    /// <summary>Stores <paramref name="group"/>, replacing a group of the same id.</summary>
    public CompareGroup Save(CompareGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        lock (gate) return Write(group);
    }

    /// <summary>Changes group <paramref name="id"/> with <paramref name="change"/> (read, change and write under one lock).</summary>
    /// <returns>The group as stored, or null when there is no such group.</returns>
    public CompareGroup? Update(string id, Func<CompareGroup, CompareGroup> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        PruneOnce();
        lock (gate)
        {
            if (Read(id) is not { } group) return null;
            return Write(change(group));
        }
    }

    /// <summary>Removes group <paramref name="id"/>; true when it was there.</summary>
    public bool Delete(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (gate) return db.Execute("DELETE FROM compare_groups WHERE id = $id;", ("$id", id)) > 0;
    }

    /// <summary>Removes the groups none of whose runs are in runs.db any more; returns how many.</summary>
    public int Prune()
    {
        if (runs is null) return 0;
        var removed = 0;
        foreach (var (id, data) in db.Query("SELECT id, data FROM compare_groups;", reader => (reader.GetString(0), reader.GetString(1))))
        {
            var group = Parse(data);
            if (group is not null && group.Columns.Any(column => runs.Get(column.RunId) is not null)) continue;
            if (Delete(id)) removed++;
        }
        return removed;
    }

    private CompareGroup Write(CompareGroup group)
    {
        var now = DateTimeOffset.UtcNow;
        var stored = group with { Created = group.Created == default ? now : group.Created, Updated = now };
        db.Execute("""
            INSERT INTO compare_groups (id, data, created, updated) VALUES ($id, $data, $created, $updated)
            ON CONFLICT (id) DO UPDATE SET data = excluded.data, updated = excluded.updated;
            """,
            ("$id", stored.Id), ("$data", JsonSerializer.Serialize(stored, SettingsStore.JsonOptions)),
            ("$created", stored.Created.ToUnixTimeMilliseconds()), ("$updated", stored.Updated.ToUnixTimeMilliseconds()));
        return stored;
    }

    private CompareGroup? Read(string id) =>
        db.Query("SELECT data FROM compare_groups WHERE id = $id;", reader => Parse(reader.GetString(0)), ("$id", id)).FirstOrDefault();

    private void PruneOnce()
    {
        if (Interlocked.Exchange(ref pruned, 1) != 0) return;
        try { Prune(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log?.LogWarning(error, "Compare groups whose runs are gone could not be removed.");
        }
    }

    private void OnRunsChanged(object? sender, RunChange change)
    {
        if (change.Kind is not ("deleted" or "cleaned")) return;
        try { Prune(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log?.LogWarning(error, "Compare groups whose runs are gone could not be removed.");
        }
    }

    private CompareGroup? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<CompareGroup>(json, SettingsStore.JsonOptions); }
        catch (JsonException error)
        {
            log?.LogWarning(error, "A Compare group could not be read.");
            return null;
        }
    }
}
