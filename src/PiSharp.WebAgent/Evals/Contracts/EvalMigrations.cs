// Decision 0011 (run W5, E0): the evals tables in webagent.db, in the evals range of migration ids (2000-2999, MigrationArea.Evals). The
// range is split so the W5 units never collide: 2000-2099 the contracts (this file), 2100 rater, 2200 checks, 2300 drift, 2400 suites
// (store, scheduler, runner), 2500 arena, 2600 pages, 2700 pins, 2800 tests and docs (EvalMigrations.Ranges). A unit that needs another
// column or table adds its own migration in its range; nobody edits these once shipped.
//
// Each table keeps the whole record as JSON in `data` (SettingsStore.JsonOptions, the contract types in this folder) beside the columns
// queries and constraints need, which the store rewrites from the record on every change. Times are UTC Unix milliseconds; amounts are
// micro-USD integers; enums are their wire names. Foreign keys are on (WebAgentDb): deleting a suite deletes its cases, runs, results,
// slots, baselines and signals; deleting a battle deletes its vote. Results keep their case id without a foreign key, so a run's results
// outlive a case deleted later. The constraints carry decision 0009's and the plan's rules: one vote per battle, one run per suite and slot,
// one slot decision per suite and slot, run numbers unique per suite, and one current baseline per suite and target.
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Evals;

/// <summary>The evals area's webagent.db migrations (ids 2000-2099 of the evals range) and how the rest of the range is shared.</summary>
public static class EvalMigrations
{
    /// <summary>The evals range split by unit: each W5 unit adds its migrations only inside its own block.</summary>
    public static IReadOnlyList<MigrationArea> Ranges { get; } =
    [
        new("evals: contracts", 2000, 2099),
        new("evals: rater", 2100, 2199),
        new("evals: checks", 2200, 2299),
        new("evals: drift", 2300, 2399),
        new("evals: suites", 2400, 2499),
        new("evals: arena", 2500, 2599),
        new("evals: pages", 2600, 2699),
        new("evals: pins", 2700, 2799),
        new("evals: tests", 2800, 2899),
    ];

    /// <summary>2000: <c>eval_battles</c> (blind Compare and agent battles) and <c>eval_votes</c> (one vote per battle).</summary>
    public static SqlMigration Arena { get; } = new(2000, "evals: battles and votes", """
        CREATE TABLE IF NOT EXISTS eval_battles (
          id TEXT PRIMARY KEY,
          mode TEXT NOT NULL CHECK (mode IN ('request', 'agent')),
          tag TEXT,
          status TEXT NOT NULL CHECK (status IN ('held', 'ready', 'voted', 'void')),
          seed INTEGER NOT NULL,
          group_id TEXT,
          data TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          voted_at INTEGER
        );
        CREATE INDEX IF NOT EXISTS eval_battles_status ON eval_battles (status, created_at);
        CREATE INDEX IF NOT EXISTS eval_battles_group ON eval_battles (group_id);
        CREATE TABLE IF NOT EXISTS eval_votes (
          battle_id TEXT PRIMARY KEY REFERENCES eval_battles (id) ON DELETE CASCADE,
          mode TEXT NOT NULL CHECK (mode IN ('request', 'agent')),
          tag TEXT NOT NULL,
          kind TEXT NOT NULL CHECK (kind IN ('pick', 'tie', 'all-bad', 'rank')),
          data TEXT NOT NULL,
          created_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS eval_votes_tag ON eval_votes (tag, created_at);
        CREATE INDEX IF NOT EXISTS eval_votes_mode ON eval_votes (mode, created_at);
        """);

    /// <summary>2001: <c>eval_suites</c> and <c>eval_cases</c> (a case belongs to one suite, in the suite's order).</summary>
    public static SqlMigration Suites { get; } = new(2001, "evals: suites and cases", """
        CREATE TABLE IF NOT EXISTS eval_suites (
          id TEXT PRIMARY KEY,
          name TEXT NOT NULL,
          schedule_enabled INTEGER NOT NULL DEFAULT 0,
          cap_micros INTEGER CHECK (cap_micros IS NULL OR cap_micros > 0),
          data TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          updated_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS eval_suites_schedule ON eval_suites (schedule_enabled);
        CREATE TABLE IF NOT EXISTS eval_cases (
          id TEXT PRIMARY KEY,
          suite_id TEXT NOT NULL REFERENCES eval_suites (id) ON DELETE CASCADE,
          name TEXT NOT NULL,
          position INTEGER NOT NULL DEFAULT 0,
          origin TEXT NOT NULL,
          data TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          updated_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS eval_cases_suite ON eval_cases (suite_id, position, created_at);
        """);

    /// <summary>2002: <c>eval_runs</c> (numbered per suite, one per slot), <c>eval_results</c> (one per case, target and repetition) and
    /// <c>eval_schedule_slots</c> (one decision per suite and slot).</summary>
    public static SqlMigration Runs { get; } = new(2002, "evals: runs, results and slots", """
        CREATE TABLE IF NOT EXISTS eval_runs (
          id TEXT PRIMARY KEY,
          suite_id TEXT NOT NULL REFERENCES eval_suites (id) ON DELETE CASCADE,
          number INTEGER NOT NULL CHECK (number > 0),
          run_trigger TEXT NOT NULL CHECK (run_trigger IN ('manual', 'schedule')),
          slot INTEGER,
          status TEXT NOT NULL CHECK (status IN ('queued', 'running', 'complete', 'cancelled', 'failed', 'interrupted')),
          group_id TEXT,
          reserved_micros INTEGER NOT NULL DEFAULT 0,
          cost_micros INTEGER NOT NULL DEFAULT 0,
          list_micros INTEGER NOT NULL DEFAULT 0,
          data TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          finished_at INTEGER,
          CHECK ((run_trigger = 'schedule') = (slot IS NOT NULL))
        );
        CREATE UNIQUE INDEX IF NOT EXISTS eval_runs_number ON eval_runs (suite_id, number);
        CREATE UNIQUE INDEX IF NOT EXISTS eval_runs_slot ON eval_runs (suite_id, slot) WHERE slot IS NOT NULL;
        CREATE INDEX IF NOT EXISTS eval_runs_status ON eval_runs (status);
        CREATE INDEX IF NOT EXISTS eval_runs_created ON eval_runs (suite_id, created_at);
        CREATE TABLE IF NOT EXISTS eval_results (
          run_id TEXT NOT NULL REFERENCES eval_runs (id) ON DELETE CASCADE,
          case_id TEXT NOT NULL,
          target_id TEXT NOT NULL,
          repetition INTEGER NOT NULL CHECK (repetition >= 0),
          status TEXT NOT NULL CHECK (status IN ('queued', 'running', 'done', 'error', 'cancelled')),
          call_run_id TEXT,
          verdict TEXT CHECK (verdict IS NULL OR verdict IN ('passed', 'failed', 'review', 'error')),
          score REAL,
          response_model TEXT,
          fingerprint INTEGER,
          cost_micros INTEGER,
          list_price INTEGER NOT NULL DEFAULT 0,
          duration_ms INTEGER,
          data TEXT NOT NULL,
          finished_at INTEGER,
          PRIMARY KEY (run_id, case_id, target_id, repetition)
        );
        CREATE INDEX IF NOT EXISTS eval_results_case ON eval_results (case_id, target_id);
        CREATE INDEX IF NOT EXISTS eval_results_call ON eval_results (call_run_id);
        CREATE TABLE IF NOT EXISTS eval_schedule_slots (
          suite_id TEXT NOT NULL REFERENCES eval_suites (id) ON DELETE CASCADE,
          slot INTEGER NOT NULL,
          outcome TEXT NOT NULL CHECK (outcome IN ('started', 'skipped-running', 'skipped-over-cap', 'skipped-unknown-price', 'skipped-refused')),
          run_id TEXT,
          detail TEXT,
          decided_at INTEGER NOT NULL,
          PRIMARY KEY (suite_id, slot),
          CHECK ((outcome = 'started') = (run_id IS NOT NULL))
        );
        """);

    /// <summary>2003: <c>eval_baselines</c> (one current per suite and target) and <c>eval_drift_signals</c>.</summary>
    public static SqlMigration Drift { get; } = new(2003, "evals: baselines and drift signals", """
        CREATE TABLE IF NOT EXISTS eval_baselines (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          suite_id TEXT NOT NULL REFERENCES eval_suites (id) ON DELETE CASCADE,
          target_id TEXT NOT NULL,
          run_id TEXT NOT NULL REFERENCES eval_runs (id) ON DELETE CASCADE,
          identity_key TEXT NOT NULL,
          data TEXT NOT NULL,
          accepted_at INTEGER NOT NULL,
          replaced_at INTEGER
        );
        CREATE UNIQUE INDEX IF NOT EXISTS eval_baselines_current ON eval_baselines (suite_id, target_id) WHERE replaced_at IS NULL;
        CREATE INDEX IF NOT EXISTS eval_baselines_run ON eval_baselines (run_id);
        CREATE TABLE IF NOT EXISTS eval_drift_signals (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          suite_id TEXT NOT NULL REFERENCES eval_suites (id) ON DELETE CASCADE,
          target_id TEXT NOT NULL,
          run_id TEXT NOT NULL REFERENCES eval_runs (id) ON DELETE CASCADE,
          baseline_run_id TEXT NOT NULL,
          first_run_id TEXT,
          rule TEXT NOT NULL CHECK (rule IN ('broken-case', 'pass-rate', 'judge-score', 'latency', 'tokens-per-second', 'cost', 'response-model', 'fingerprint')),
          level TEXT NOT NULL CHECK (level IN ('ok', 'info', 'suspect', 'drift')),
          data TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          cleared_at INTEGER
        );
        CREATE INDEX IF NOT EXISTS eval_drift_signals_open ON eval_drift_signals (suite_id, target_id, cleared_at);
        CREATE INDEX IF NOT EXISTS eval_drift_signals_run ON eval_drift_signals (run_id);
        """);

    /// <summary>2004: <c>eval_price_snapshots</c> (one per provider catalog hash seen) and <c>eval_price_changes</c>.</summary>
    public static SqlMigration Prices { get; } = new(2004, "evals: price snapshots and changes", """
        CREATE TABLE IF NOT EXISTS eval_price_snapshots (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          provider TEXT NOT NULL,
          catalog_sha256 TEXT NOT NULL,
          data TEXT NOT NULL,
          taken_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS eval_price_snapshots_provider ON eval_price_snapshots (provider, taken_at);
        CREATE TABLE IF NOT EXISTS eval_price_changes (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          snapshot_id INTEGER NOT NULL REFERENCES eval_price_snapshots (id) ON DELETE CASCADE,
          provider TEXT NOT NULL,
          model TEXT NOT NULL,
          field TEXT NOT NULL CHECK (field IN ('input', 'output', 'cache-read', 'cache-write')),
          before_rate REAL,
          after_rate REAL,
          detected_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS eval_price_changes_detected ON eval_price_changes (detected_at);
        CREATE INDEX IF NOT EXISTS eval_price_changes_model ON eval_price_changes (provider, model, detected_at);
        """);

    /// <summary>Every contracts migration, lowest id first.</summary>
    public static IReadOnlyList<IWebAgentMigration> All { get; } = [Arena, Suites, Runs, Drift, Prices];

    /// <summary>The block of the evals range <paramref name="id"/> belongs to, or null when it is outside the range.</summary>
    public static MigrationArea? RangeOf(int id) => Ranges.FirstOrDefault(range => id >= range.First && id <= range.Last);
}
