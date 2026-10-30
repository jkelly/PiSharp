// Decision 0011 (run W4, R0): runs.db, the run history, next to webagent.db in the data folder. It is its own SQLite file (the same package
// pins, WAL, unpooled connections and its own schema_migrations, through the same WebAgentDb class) so that a busy history never slows the
// settings, accounts and ledger, and so Settings › Data can size, clear or delete it on its own. Its migrations are listed here, not in DI:
// nothing else adds tables to it. Ids start at 1 (WebAgentDb checks ids against the core range; runs.db has no other areas).
using Microsoft.Extensions.Logging;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Runs;

/// <summary>The runs.db file and its connections. A singleton.</summary>
public sealed class RunsDb
{
    /// <summary>The file name: <c>runs.db</c>.</summary>
    public const string FileName = "runs.db";

    /// <summary>runs.db in <paramref name="options"/>' data folder.</summary>
    public RunsDb(WebAgentOptions options, ILogger<RunsDb>? log = null) : this(System.IO.Path.Join(options.DataDirectory, FileName), log) { }

    /// <summary>runs.db at <paramref name="path"/>.</summary>
    public RunsDb(string path, ILogger? log = null) => Db = new WebAgentDb(path, RunsMigrations.All, log);

    /// <summary>The database (connections, migrations, transactions).</summary>
    public WebAgentDb Db { get; }

    /// <summary>The file.</summary>
    public string Path => Db.Path;
}

/// <summary>runs.db's schema steps.</summary>
public static class RunsMigrations
{
    /// <summary>1: runs, their stream events and their raw payloads.</summary>
    public static SqlMigration Runs { get; } = new(1, "runs: runs, events, raw payloads", """
        CREATE TABLE runs (
            id TEXT PRIMARY KEY,
            op TEXT NOT NULL UNIQUE,
            kind TEXT NOT NULL DEFAULT 'call',
            source TEXT NOT NULL,
            status TEXT NOT NULL,
            provider TEXT NOT NULL DEFAULT '',
            model TEXT NOT NULL DEFAULT '',
            api TEXT,
            account TEXT,
            group_id TEXT,
            parent_id TEXT,
            session TEXT,
            title TEXT,
            options TEXT,
            messages TEXT,
            messages_ref TEXT,
            output TEXT,
            output_text TEXT,
            stop_reason TEXT,
            error TEXT,
            notes TEXT,
            input_tokens REAL NOT NULL DEFAULT 0,
            output_tokens REAL NOT NULL DEFAULT 0,
            cache_read_tokens REAL NOT NULL DEFAULT 0,
            cache_write_tokens REAL NOT NULL DEFAULT 0,
            total_tokens REAL NOT NULL DEFAULT 0,
            cost_micros INTEGER,
            cost_kind TEXT,
            billing TEXT,
            spend_op TEXT,
            ttft_ms INTEGER,
            duration_ms INTEGER,
            tokens_per_second REAL,
            pinned INTEGER NOT NULL DEFAULT 0,
            raw_state TEXT NOT NULL DEFAULT 'none',
            runner TEXT,
            created INTEGER NOT NULL,
            started INTEGER,
            finished INTEGER,
            updated INTEGER NOT NULL
        );
        CREATE INDEX runs_created ON runs (created DESC);
        CREATE INDEX runs_source ON runs (source, created DESC);
        CREATE INDEX runs_status ON runs (status);
        CREATE INDEX runs_group ON runs (group_id) WHERE group_id IS NOT NULL;
        CREATE INDEX runs_parent ON runs (parent_id) WHERE parent_id IS NOT NULL;
        CREATE TABLE run_events (
            run_id TEXT NOT NULL REFERENCES runs (id) ON DELETE CASCADE,
            seq INTEGER NOT NULL,
            at_ms INTEGER NOT NULL,
            event TEXT NOT NULL,
            PRIMARY KEY (run_id, seq)
        ) WITHOUT ROWID;
        CREATE TABLE run_raw (
            run_id TEXT NOT NULL REFERENCES runs (id) ON DELETE CASCADE,
            seq INTEGER NOT NULL,
            kind TEXT NOT NULL,
            at_ms INTEGER NOT NULL,
            data TEXT NOT NULL,
            created INTEGER NOT NULL,
            PRIMARY KEY (run_id, seq)
        ) WITHOUT ROWID;
        CREATE INDEX run_raw_created ON run_raw (created);
        """);

    /// <summary>Every step, lowest id first.</summary>
    public static IReadOnlyList<IWebAgentMigration> All { get; } = [Runs];
}
