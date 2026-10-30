// Decision 0011 (run W4, R5): agent compare runs in webagent.db (migration ids 580-599 of the compare range 500-599; R2 and R7 use the ids
// below). One row per run holds the whole run as JSON (spec, start order, run grants, workspaces, and each model's state), with its id,
// request id (unique: a repeated create finds its run), state and times as columns; agent_compare_turns records each model request a model
// was let through, keyed by its op (metadata.pisharp.op, the same when a request is recovered), so turns are counted exactly across restarts.
// The store keeps every run it has read in memory and changes one run at a time (read, change, write under the run's lock), then raises
// Changed outside the lock. It also indexes runs by session id, which the model gate and the approval override look up on every call.
using System.Collections.Concurrent;
using System.Text.Json;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.AgentCompare.Runner;

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

    private readonly WebAgentDb db;
    private readonly ConcurrentDictionary<string, AgentCompareRun> runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Run, string Slot)> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lock> locks = new(StringComparer.Ordinal);
    private readonly Lock adding = new();
    private readonly Lock loading = new();
    private volatile bool loaded;

    /// <summary>A store over <paramref name="db"/>.</summary>
    public AgentCompareStore(WebAgentDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        this.db = db;
    }

    /// <summary>Raised after a run changed (its id), outside any lock. A handler must not throw.</summary>
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

    /// <summary>The run <paramref name="id"/>, or null.</summary>
    public AgentCompareRun? Get(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        Load();
        return runs.TryGetValue(id, out var run) ? run : null;
    }

    /// <summary>The run a create request made, or null.</summary>
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

    /// <summary>Runs, newest first.</summary>
    public IReadOnlyList<AgentCompareRun> List(int limit = 50)
    {
        Load();
        return [.. runs.Values.OrderByDescending(run => run.Created).ThenByDescending(run => run.Id, StringComparer.Ordinal).Take(Math.Clamp(limit, 1, 500))];
    }

    /// <summary>Stores a new run; when a run of the same request id exists, returns that one instead (the first create wins).</summary>
    public AgentCompareRun Add(AgentCompareRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Load();
        lock (adding)
        {
            if (FindByRequest(run.RequestId) is { } existing) return existing;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            db.Execute("INSERT INTO agent_compare_runs (id, request_id, state, data, created, updated) VALUES ($id, $request, $state, $data, $created, $updated);",
                ("$id", run.Id), ("$request", run.RequestId), ("$state", run.State), ("$data", JsonSerializer.Serialize(run, SettingsStore.JsonOptions)),
                ("$created", run.Created.ToUnixTimeMilliseconds()), ("$updated", now));
            Remember(run);
        }
        Changed?.Invoke(run.Id);
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
                ("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Remember(updated);
        }
        Changed?.Invoke(id);
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

    /// <summary>Raises <see cref="Changed"/> for <paramref name="id"/> (something the run's status shows changed outside the store: an
    /// approval, a turn).</summary>
    public void Touch(string id) => Changed?.Invoke(id);

    /// <summary>Whether <paramref name="op"/> is a turn of <paramref name="modelKey"/> already (a recovered request).</summary>
    public bool HasTurn(string modelKey, string op) =>
        db.Scalar("SELECT 1 FROM agent_compare_turns WHERE model_key = $key AND op = $op;", ("$key", modelKey), ("$op", op)) is not null;

    /// <summary>How many turns <paramref name="modelKey"/> was let through.</summary>
    public int Turns(string modelKey) =>
        Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM agent_compare_turns WHERE model_key = $key;", ("$key", modelKey)) ?? 0L, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Records a turn of <paramref name="modelKey"/> (once per op).</summary>
    public void AddTurn(string modelKey, string op) =>
        db.Execute("INSERT OR IGNORE INTO agent_compare_turns (model_key, op, at) VALUES ($key, $op, $at);",
            ("$key", modelKey), ("$op", op), ("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

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
