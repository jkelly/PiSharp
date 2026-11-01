// Decision 0011 (run W4, R7): bench sessions in webagent.db (migration 540, in the compare range 500-599: R7 uses 540-559). One row per
// session holds the whole session as JSON (setup, conversation, calls), with its id, state and times as columns. The store keeps every
// session it has read in memory and changes one session at a time (read, change, write under the session's lock), bumping its version and
// raising Changed outside the lock. It keeps the newest sessions only (MaxSessions; never one that is running). A session that was running
// when the app stopped is read back stopped, with a note: its turn's run is marked interrupted by the run engine and is never run again.
using System.Collections.Concurrent;
using System.Text.Json;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Bench;

/// <summary>Bench sessions in webagent.db. A singleton; safe to call from many threads.</summary>
public sealed class BenchStore
{
    /// <summary>The most sessions kept.</summary>
    public const int MaxSessions = 50;

    /// <summary>The note of a session that was running when the app stopped.</summary>
    public const string InterruptedNote = "Interrupted by a restart: the turn that was running was not run again. Run continues the conversation.";

    /// <summary>The table.</summary>
    public static SqlMigration Migration { get; } = new(540, "bench: sessions", """
        CREATE TABLE bench_sessions (
            id TEXT PRIMARY KEY,
            state TEXT NOT NULL,
            data TEXT NOT NULL,
            created INTEGER NOT NULL,
            updated INTEGER NOT NULL
        );
        CREATE INDEX bench_sessions_updated ON bench_sessions (updated);
        """);

    private readonly WebAgentDb db;
    private readonly ConcurrentDictionary<string, BenchSession> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lock> locks = new(StringComparer.Ordinal);
    private readonly Lock loading = new();
    private readonly Lock adding = new();
    private volatile bool loaded;

    /// <summary>A store over <paramref name="db"/>.</summary>
    public BenchStore(WebAgentDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        this.db = db;
    }

    /// <summary>Raised after a session changed (its id and version), outside any lock. A handler must not throw.</summary>
    public event Action<string, long>? Changed;

    /// <summary>Reads every session into memory (once).</summary>
    public void Load()
    {
        if (loaded) return;
        lock (loading)
        {
            if (loaded) return;
            foreach (var session in db.Query("SELECT data FROM bench_sessions;", reader => Parse(reader.GetString(0))))
            {
                if (session is null) continue;
                var current = session;
                if (current.State == BenchStates.Running || current.ActiveRun is not null)
                {
                    current = Interrupted(current);
                    Write(current, insert: false);
                }
                sessions[current.Id] = current;
            }
            loaded = true;
        }
    }

    /// <summary>The session <paramref name="id"/>, or null.</summary>
    public BenchSession? Get(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        Load();
        return sessions.TryGetValue(id, out var session) ? session : null;
    }

    /// <summary>The most recently changed session, or null.</summary>
    public BenchSession? Latest()
    {
        Load();
        return sessions.Values.OrderByDescending(session => session.Updated).ThenByDescending(session => session.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>Sessions, most recently changed first.</summary>
    public IReadOnlyList<BenchSession> List(int limit = MaxSessions)
    {
        Load();
        return [.. sessions.Values.OrderByDescending(session => session.Updated).Take(Math.Clamp(limit, 1, MaxSessions))];
    }

    /// <summary>Stores a new session (and drops the oldest beyond <see cref="MaxSessions"/>).</summary>
    public BenchSession Add(BenchSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Load();
        var now = DateTimeOffset.UtcNow;
        var stored = session with { Created = session.Created == default ? now : session.Created, Updated = now, Version = Math.Max(1, session.Version) };
        lock (adding)
        {
            Write(stored, insert: true);
            sessions[stored.Id] = stored;
            Prune();
        }
        Changed?.Invoke(stored.Id, stored.Version);
        return stored;
    }

    /// <summary>Changes session <paramref name="id"/> under its lock and stores it (a new version) when <paramref name="change"/> returns a
    /// different instance.</summary>
    /// <returns>The session as stored, or null when there is none.</returns>
    public BenchSession? Update(string id, Func<BenchSession, BenchSession> change)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(change);
        Load();
        BenchSession updated;
        lock (locks.GetOrAdd(id, _ => new Lock()))
        {
            if (!sessions.TryGetValue(id, out var current)) return null;
            var changed = change(current);
            if (ReferenceEquals(changed, current)) return current;
            updated = changed with { Id = current.Id, Created = current.Created, Updated = DateTimeOffset.UtcNow, Version = current.Version + 1 };
            Write(updated, insert: false);
            sessions[id] = updated;
        }
        Changed?.Invoke(id, updated.Version);
        return updated;
    }

    /// <summary>Deletes session <paramref name="id"/>.</summary>
    public bool Delete(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        Load();
        if (!sessions.TryRemove(id, out _)) return false;
        db.Execute("DELETE FROM bench_sessions WHERE id = $id;", ("$id", id));
        return true;
    }

    /// <summary>A session that was running when the app stopped, as it reads back.</summary>
    internal static BenchSession Interrupted(BenchSession session)
    {
        var entries = session.Entries.Select(entry => entry.Turn is { Status: Runs.RunStatus.Running } turn
            ? entry with { Turn = turn with { Status = Runs.RunStatus.Interrupted, Error = Runs.RunStore.InterruptedMessage } }
            : entry).ToList();
        return session with
        {
            Entries = entries,
            State = BenchStates.Stopped,
            ActiveRun = null,
            Note = InterruptedNote,
            Version = session.Version + 1,
            Updated = DateTimeOffset.UtcNow,
        };
    }

    private void Prune()
    {
        var extra = sessions.Values.Where(session => session.State != BenchStates.Running)
            .OrderByDescending(session => session.Updated).Skip(MaxSessions).Select(session => session.Id).ToList();
        foreach (var id in extra) Delete(id);
    }

    private void Write(BenchSession session, bool insert)
    {
        var data = JsonSerializer.Serialize(session, SettingsStore.JsonOptions);
        if (insert)
            db.Execute("INSERT INTO bench_sessions (id, state, data, created, updated) VALUES ($id, $state, $data, $created, $updated);",
                ("$id", session.Id), ("$state", session.State), ("$data", data),
                ("$created", session.Created.ToUnixTimeMilliseconds()), ("$updated", session.Updated.ToUnixTimeMilliseconds()));
        else
            db.Execute("UPDATE bench_sessions SET state = $state, data = $data, updated = $updated WHERE id = $id;",
                ("$id", session.Id), ("$state", session.State), ("$data", data), ("$updated", session.Updated.ToUnixTimeMilliseconds()));
    }

    private static BenchSession? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<BenchSession>(json, SettingsStore.JsonOptions); }
        catch (JsonException) { return null; }
    }
}
