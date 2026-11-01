// Decision 0011 (run W4, R5): agent compare runs in webagent.db (migration ids 580-599 of the compare range 500-599; R2 and R7 use the ids
// below). One row per run holds the whole run as JSON (spec, start order, run grants, workspaces, and each model's state), with its id,
// request id (unique: a repeated create finds its run), state and times as columns; agent_compare_turns records each model request a model
// was let through, keyed by its op (metadata.pisharp.op, the same when a request is recovered), so turns are counted exactly across restarts.
// The store keeps every run it has read in memory and changes one run at a time (read, change, write under the run's lock), then raises
// Changed outside the lock. It also indexes runs by session id, which the model gate and the approval override look up on every call.
// Fix 2 (W4): a run being deleted (AgentCompareRunner.DeleteAsync marks it first) is no longer answered by Get or List; Delete removes its
// rows (the run and its turns) and drops it from memory. Times come from the app's TimeProvider.
// Changed is raised from Add, Update and Touch, which the gate (every admitted model request) and the runner (every state change) call, so
// each handler is invoked on its own and a handler that throws is logged and skipped: a subscriber (such as an event stream whose tab just
// closed) can never fail a model's request or a state change.
// W4 fix 2 (workspaces): agent_compare_patches (migration 585) keeps each model's patch with its run (the runner saves it when the model is
// done and again just before its workspace is discarded), so the diff modal can still show a model's lines once its workspace is gone. It is
// a table of its own, read only when a diff is asked for, so a large patch never rides along in the run's JSON, in memory or in the status
// frames. A patch above MaxPatchBytes is cut at whole files (WorkspacePatch.Cut) and records how many files it left out.
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.AgentCompare.Runner;

/// <summary>A model's patch as kept with its run (<see cref="AgentCompareStore.SavePatch"/>).</summary>
/// <param name="Patch">The patch text (whole files only; cut at <see cref="AgentCompareStore.MaxPatchBytes"/>).</param>
/// <param name="Files">How many changed files the patch had.</param>
/// <param name="KeptFiles">How many of them <paramref name="Patch"/> holds.</param>
/// <param name="Saved">When it was kept.</param>
public sealed record AgentKeptPatch(string Patch, int Files, int KeptFiles, DateTimeOffset Saved)
{
    /// <summary>Changed files the kept patch has no room for.</summary>
    public int OmittedFiles => Math.Max(0, Files - KeptFiles);
}

/// <summary>Agent compare runs in webagent.db. A singleton; safe to call from many threads.</summary>
public sealed class AgentCompareStore
{
    /// <summary>The tables.</summary>
    public static SqlMigration Migration { get; } = new(580, "agent compare: runs and turns", """
        CREATE TABLE agent_compare_runs (
            id TEXT PRIMARY KEY,
            request_id TEXT NOT NULL UNIQUE,
            state TEXT NOT NULL,
            data TEXT NOT NULL,
            created INTEGER NOT NULL,
            updated INTEGER NOT NULL
        );
        CREATE INDEX agent_compare_runs_state ON agent_compare_runs (state);
        CREATE INDEX agent_compare_runs_created ON agent_compare_runs (created);
        CREATE TABLE agent_compare_turns (
            model_key TEXT NOT NULL,
            op TEXT NOT NULL,
            at INTEGER NOT NULL,
            PRIMARY KEY (model_key, op)
        );
        """);

    /// <summary>The kept patches (W4 fix 2): one row per model, replaced when it is kept again.</summary>
    public static SqlMigration PatchMigration { get; } = new(585, "agent compare: kept patches", """
        CREATE TABLE agent_compare_patches (
            run_id TEXT NOT NULL,
            slot TEXT NOT NULL,
            patch TEXT NOT NULL,
            files INTEGER NOT NULL,
            kept_files INTEGER NOT NULL,
            saved INTEGER NOT NULL,
            PRIMARY KEY (run_id, slot)
        );
        """);

    /// <summary>The most UTF-8 bytes of one model's patch kept with its run: 1 MiB; a larger patch keeps its first whole files.</summary>
    public const int MaxPatchBytes = 1 << 20;

    private readonly WebAgentDb db;
    private readonly ILogger log;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<string, AgentCompareRun> runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Run, string Slot)> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lock> locks = new(StringComparer.Ordinal);
    private readonly Lock adding = new();
    private readonly Lock loading = new();
    private volatile bool loaded;

    /// <summary>A store over <paramref name="db"/>; <paramref name="logger"/> gets the failures of <see cref="Changed"/> handlers;
    /// <paramref name="time"/> (default: the system's) dates rows and turns.</summary>
    public AgentCompareStore(WebAgentDb db, ILogger<AgentCompareStore>? logger = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        this.db = db;
        log = (ILogger?)logger ?? NullLogger.Instance;
        clock = time ?? TimeProvider.System;
    }

    /// <summary>Raised after a run changed (its id), outside any lock. Each handler is called on its own; one that throws is logged and the
    /// others are still called (the change itself has happened).</summary>
    public event Action<string>? Changed;

    /// <summary>Reads every run into memory (once; later calls do nothing).</summary>
    public void Load()
    {
        if (loaded) return;
        lock (loading)
        {
            if (loaded) return;
            foreach (var run in db.Query("SELECT data FROM agent_compare_runs;", reader => Parse(reader.GetString(0))))
                if (run is not null) Remember(run);
            loaded = true;
        }
    }

    /// <summary>The run <paramref name="id"/>, or null: also null while it is being deleted (<see cref="Find"/> answers it then).</summary>
    public AgentCompareRun? Get(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        Load();
        return runs.TryGetValue(id, out var run) && !run.Deleting ? run : null;
    }

    /// <summary>The run <paramref name="id"/> as stored, also while it is being deleted, or null.</summary>
    public AgentCompareRun? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        Load();
        return runs.TryGetValue(id, out var run) ? run : null;
    }

    /// <summary>Every stored run, also those being deleted (the runner's prune and its start).</summary>
    public IReadOnlyList<AgentCompareRun> All()
    {
        Load();
        return [.. runs.Values];
    }

    /// <summary>The run a create request made (also while it is being deleted), or null.</summary>
    public AgentCompareRun? FindByRequest(string requestId)
    {
        Load();
        return runs.Values.FirstOrDefault(run => string.Equals(run.RequestId, requestId, StringComparison.Ordinal));
    }

    /// <summary>The run and slot of durable session <paramref name="sessionId"/>, or null when it belongs to no run.</summary>
    public (AgentCompareRun Run, AgentCompareModelState Model)? FindSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        Load();
        if (!sessions.TryGetValue(sessionId, out var key) || !runs.TryGetValue(key.Run, out var run) || run.Model(key.Slot) is not { } model) return null;
        return (run, model);
    }

    /// <summary>Runs, newest first (not those being deleted).</summary>
    public IReadOnlyList<AgentCompareRun> List(int limit = 50)
    {
        Load();
        return [.. runs.Values.Where(run => !run.Deleting).OrderByDescending(run => run.Created).ThenByDescending(run => run.Id, StringComparer.Ordinal).Take(Math.Clamp(limit, 1, 500))];
    }

    /// <summary>Stores a new run; when a run of the same request id exists, returns that one instead (the first create wins).</summary>
    public AgentCompareRun Add(AgentCompareRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Load();
        lock (adding)
        {
            if (FindByRequest(run.RequestId) is { } existing) return existing;
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            db.Execute("INSERT INTO agent_compare_runs (id, request_id, state, data, created, updated) VALUES ($id, $request, $state, $data, $created, $updated);",
                ("$id", run.Id), ("$request", run.RequestId), ("$state", run.State), ("$data", JsonSerializer.Serialize(run, SettingsStore.JsonOptions)),
                ("$created", run.Created.ToUnixTimeMilliseconds()), ("$updated", now));
            Remember(run);
        }
        Raise(run.Id);
        return run;
    }

    /// <summary>Changes run <paramref name="id"/> with <paramref name="change"/> under its lock and stores the result.</summary>
    /// <returns>The run as stored, or null when there is no such run.</returns>
    public AgentCompareRun? Update(string id, Func<AgentCompareRun, AgentCompareRun> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Load();
        AgentCompareRun updated;
        lock (locks.GetOrAdd(id, _ => new Lock()))
        {
            if (!runs.TryGetValue(id, out var current)) return null;
            updated = change(current);
            if (ReferenceEquals(updated, current)) return current;
            db.Execute("UPDATE agent_compare_runs SET state = $state, data = $data, updated = $updated WHERE id = $id;",
                ("$id", id), ("$state", updated.State), ("$data", JsonSerializer.Serialize(updated, SettingsStore.JsonOptions)),
                ("$updated", clock.GetUtcNow().ToUnixTimeMilliseconds()));
            Remember(updated);
        }
        Raise(id);
        return updated;
    }

    /// <summary>Changes the model of <paramref name="slot"/> in run <paramref name="id"/> (<see cref="Update"/>).</summary>
    public AgentCompareRun? UpdateModel(string id, string slot, Func<AgentCompareModelState, AgentCompareModelState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Update(id, run =>
        {
            var models = run.Models.ToList();
            var index = models.FindIndex(model => model.Slot == slot);
            if (index < 0) return run;
            var updated = change(models[index]);
            if (ReferenceEquals(updated, models[index])) return run;
            models[index] = updated;
            return run with { Models = models };
        });
    }

    /// <summary>Marks run <paramref name="id"/> as being deleted: from now on <see cref="Get"/> and <see cref="List"/> leave it out.</summary>
    /// <returns>The run as stored, or null when there is no such run.</returns>
    public AgentCompareRun? MarkDeleting(string id) => Update(id, run => run.Deleting ? run : run with { Deleting = true });

    /// <summary>Removes run <paramref name="id"/>: its row, its models' turns, and the run from memory (with its sessions' index entries).</summary>
    /// <returns>The run that was removed, or null when there was none.</returns>
    public AgentCompareRun? Delete(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        Load();
        AgentCompareRun? removed;
        lock (locks.GetOrAdd(id, _ => new Lock()))
        {
            if (!runs.TryGetValue(id, out removed)) return null;
            var keys = removed.Models.Select(model => AgentCompareRun.ModelKey(id, model.Slot)).ToList();
            db.Transaction((connection, transaction) =>
            {
                foreach (var key in keys) WebAgentDb.Execute(connection, transaction, "DELETE FROM agent_compare_turns WHERE model_key = $key;", ("$key", key));
                return WebAgentDb.Execute(connection, transaction, "DELETE FROM agent_compare_runs WHERE id = $id;", ("$id", id));
            });
            runs.TryRemove(id, out _);
            foreach (var model in removed.Models)
                if (model.SessionId is { Length: > 0 } session && sessions.TryGetValue(session, out var owner) && owner.Run == id)
                    sessions.TryRemove(session, out _);
        }
        locks.TryRemove(id, out _);
        Raise(id);
        return removed;
    }

    /// <summary>Raises <see cref="Changed"/> for <paramref name="id"/> (something the run's status shows changed outside the store: an
    /// approval, a turn).</summary>
    public void Touch(string id) => Raise(id);

    /// <summary>Whether <paramref name="op"/> is a turn of <paramref name="modelKey"/> already (a recovered request).</summary>
    public bool HasTurn(string modelKey, string op) =>
        db.Scalar("SELECT 1 FROM agent_compare_turns WHERE model_key = $key AND op = $op;", ("$key", modelKey), ("$op", op)) is not null;

    /// <summary>How many turns <paramref name="modelKey"/> was let through.</summary>
    public int Turns(string modelKey) =>
        Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM agent_compare_turns WHERE model_key = $key;", ("$key", modelKey)) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Records a turn of <paramref name="modelKey"/> (once per op).</summary>
    public void AddTurn(string modelKey, string op) =>
        db.Execute("INSERT OR IGNORE INTO agent_compare_turns (model_key, op, at) VALUES ($key, $op, $at);",
            ("$key", modelKey), ("$op", op), ("$at", clock.GetUtcNow().ToUnixTimeMilliseconds()));

    /// <summary>Keeps <paramref name="patch"/> as the patch of <paramref name="slot"/> in run <paramref name="run"/>, replacing one kept
    /// before; above <see cref="MaxPatchBytes"/> only its first whole files are kept.</summary>
    /// <returns>What was kept.</returns>
    public AgentKeptPatch SavePatch(string run, string slot, string patch)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(patch);
        var (text, files, kept) = WorkspacePatch.Cut(patch, MaxPatchBytes);
        var saved = DateTimeOffset.UtcNow;
        db.Execute("""
            INSERT OR REPLACE INTO agent_compare_patches (run_id, slot, patch, files, kept_files, saved)
            VALUES ($run, $slot, $patch, $files, $kept, $saved);
            """, ("$run", run), ("$slot", slot), ("$patch", text), ("$files", files), ("$kept", kept), ("$saved", saved.ToUnixTimeMilliseconds()));
        return new AgentKeptPatch(text, files, kept, DateTimeOffset.FromUnixTimeMilliseconds(saved.ToUnixTimeMilliseconds()));
    }

    /// <summary>The patch kept for <paramref name="slot"/> of run <paramref name="run"/>, or null when none was.</summary>
    public AgentKeptPatch? KeptPatch(string run, string slot)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(slot);
        return db.Query("SELECT patch, files, kept_files, saved FROM agent_compare_patches WHERE run_id = $run AND slot = $slot;",
            reader => new AgentKeptPatch(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))),
            ("$run", run), ("$slot", slot)).FirstOrDefault();
    }

    // Calls each Changed handler on its own: a handler that throws is logged, never passed to the caller (a model request, a state change).
    private void Raise(string id)
    {
        if (Changed is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                handler(id);
            }
            catch (Exception error)
            {
                log.LogWarning(error, "Agent compare run {Run}: a change listener failed; the change itself was stored.", id);
            }
        }
    }

    private void Remember(AgentCompareRun run)
    {
        runs[run.Id] = run;
        foreach (var model in run.Models)
            if (model.SessionId is { Length: > 0 } session) sessions[session] = (run.Id, model.Slot);
    }

    private static AgentCompareRun? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentCompareRun>(json, SettingsStore.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
