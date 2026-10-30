// Decision 0011 (run W5, E4): the evals store over webagent.db's evals tables (E0's migrations 2001-2003): suites, test cases, suite runs,
// their results, schedule slot decisions, baselines and drift signals. Each row keeps its whole contract record as JSON in `data`
// (EvalJson.Options) and the columns queries and constraints need, rewritten from the record on every change, so E2's CheckRecords (which
// reads `data`) and the pages read the same thing. Arena battles and votes are E5's (Evals/Arena), and price snapshots and changes are E3's
// IPriceSnapshotStore; this store does not write them.
//
// The constraints carry decision 0009's slot rules: a run and its slot's "started" decision are inserted in one transaction, and the unique
// slot indexes (eval_runs (suite_id, slot), eval_schedule_slots (suite_id, slot)) make a second run for the same slot impossible even when two
// schedulers race. Run numbers are the suite's next number inside the same transaction, so they have no gaps (a run the Spend Guard refused
// is never inserted). Writes are short IMMEDIATE transactions (WebAgentDb.Transaction); the store is a singleton, safe from many threads.
using Microsoft.Data.Sqlite;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Evals.Store;

/// <summary>Suites, cases, runs, results, slots, baselines and drift signals in webagent.db.</summary>
public sealed class EvalStore
{
    private const string Active = "('queued', 'running')";

    /// <summary>A store over <paramref name="db"/> (migrated by the app).</summary>
    public EvalStore(WebAgentDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        Db = db;
    }

    /// <summary>The database.</summary>
    public WebAgentDb Db { get; }

    // ----- suites -----

    /// <summary>Every suite, by name.</summary>
    public IReadOnlyList<Suite> Suites() =>
        Records<Suite>("SELECT data FROM eval_suites ORDER BY name COLLATE NOCASE, created_at, id;");

    /// <summary>The suites whose schedule is on (paused ones included).</summary>
    public IReadOnlyList<Suite> ScheduledSuites() =>
        Records<Suite>("SELECT data FROM eval_suites WHERE schedule_enabled = 1 ORDER BY created_at, id;");

    /// <summary>The suite <paramref name="id"/>, or null.</summary>
    public Suite? Suite(string id) => Records<Suite>("SELECT data FROM eval_suites WHERE id = $id;", ("$id", id)).FirstOrDefault();

    /// <summary>Inserts <paramref name="suite"/> unless a suite with its id exists (a retried create).</summary>
    /// <returns>Whether it was inserted.</returns>
    public bool TryInsertSuite(Suite suite)
    {
        ArgumentNullException.ThrowIfNull(suite);
        return Db.Execute("""
            INSERT OR IGNORE INTO eval_suites (id, name, schedule_enabled, cap_micros, data, created_at, updated_at)
            VALUES ($id, $name, $enabled, $cap, $data, $created, $updated);
            """, SuiteParameters(suite)) > 0;
    }

    /// <summary>Saves <paramref name="suite"/> over the stored one.</summary>
    /// <returns>Whether the suite exists.</returns>
    public bool UpdateSuite(Suite suite)
    {
        ArgumentNullException.ThrowIfNull(suite);
        return Db.Execute("""
            UPDATE eval_suites SET name = $name, schedule_enabled = $enabled, cap_micros = $cap, data = $data, updated_at = $updated WHERE id = $id;
            """, SuiteParameters(suite)) > 0;
    }

    /// <summary>Deletes the suite <paramref name="id"/> with its cases, runs, results, slots, baselines and signals.</summary>
    public bool DeleteSuite(string id) => Db.Execute("DELETE FROM eval_suites WHERE id = $id;", ("$id", id)) > 0;

    // ----- cases -----

    /// <summary>The cases of <paramref name="suiteId"/>, in the suite's order.</summary>
    public IReadOnlyList<TestCase> Cases(string suiteId) =>
        Records<TestCase>("SELECT data FROM eval_cases WHERE suite_id = $suite ORDER BY position, created_at, id;", ("$suite", suiteId));

    /// <summary>The case <paramref name="id"/>, or null.</summary>
    public TestCase? Case(string id) => Records<TestCase>("SELECT data FROM eval_cases WHERE id = $id;", ("$id", id)).FirstOrDefault();

    /// <summary>How many cases <paramref name="suiteId"/> has.</summary>
    public int CaseCount(string suiteId) =>
        Convert.ToInt32(Db.Scalar("SELECT COUNT(*) FROM eval_cases WHERE suite_id = $suite;", ("$suite", suiteId)) ?? 0L);

    /// <summary>Inserts <paramref name="testCase"/> last in its suite unless a case with its id exists (a retried create).</summary>
    /// <returns>Whether it was inserted.</returns>
    /// <exception cref="SqliteException">Its suite does not exist.</exception>
    public bool TryInsertCase(TestCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        return Db.Execute("""
            INSERT OR IGNORE INTO eval_cases (id, suite_id, name, position, origin, data, created_at, updated_at)
            VALUES ($id, $suite, $name, (SELECT COALESCE(MAX(position) + 1, 0) FROM eval_cases WHERE suite_id = $suite), $origin, $data, $created, $updated);
            """, CaseParameters(testCase)) > 0;
    }

    /// <summary>Saves <paramref name="testCase"/> over the stored one (its suite and place are kept).</summary>
    /// <returns>Whether the case exists.</returns>
    public bool UpdateCase(TestCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        return Db.Execute("UPDATE eval_cases SET name = $name, origin = $origin, data = $data, updated_at = $updated WHERE id = $id AND suite_id = $suite;",
            CaseParameters(testCase)) > 0;
    }

    /// <summary>Deletes the case <paramref name="id"/> (its results in earlier runs stay).</summary>
    public bool DeleteCase(string id) => Db.Execute("DELETE FROM eval_cases WHERE id = $id;", ("$id", id)) > 0;

    /// <summary>Moves the case <paramref name="id"/> to <paramref name="index"/> (0 first; clamped) in its suite's order.</summary>
    /// <returns>Whether the case exists.</returns>
    public bool MoveCase(string id, int index) => Db.Transaction((connection, transaction) =>
    {
        if (WebAgentDb.Scalar(connection, transaction, "SELECT suite_id FROM eval_cases WHERE id = $id;", ("$id", id)) is not string suite) return false;
        var ids = new List<string>();
        using (var command = WebAgentDb.Command(connection, transaction, "SELECT id FROM eval_cases WHERE suite_id = $suite ORDER BY position, created_at, id;",
            ("$suite", suite)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) ids.Add(reader.GetString(0));
        ids.Remove(id);
        ids.Insert(Math.Clamp(index, 0, ids.Count), id);
        for (var position = 0; position < ids.Count; position++)
            WebAgentDb.Execute(connection, transaction, "UPDATE eval_cases SET position = $position WHERE id = $id;", ("$position", position), ("$id", ids[position]));
        return true;
    });

    // ----- runs and results -----

    /// <summary>The run <paramref name="id"/>, or null.</summary>
    public SuiteRun? Run(string id) => Records<SuiteRun>("SELECT data FROM eval_runs WHERE id = $id;", ("$id", id)).FirstOrDefault();

    /// <summary>The runs of <paramref name="suiteId"/>, newest (highest number) first.</summary>
    public IReadOnlyList<SuiteRun> Runs(string suiteId, int limit = 50, int offset = 0) =>
        Records<SuiteRun>("SELECT data FROM eval_runs WHERE suite_id = $suite ORDER BY number DESC LIMIT $limit OFFSET $offset;",
            ("$suite", suiteId), ("$limit", Math.Clamp(limit, 1, 1000)), ("$offset", Math.Max(0, offset)));

    /// <summary>How many runs <paramref name="suiteId"/> has.</summary>
    public int RunCount(string suiteId) => Convert.ToInt32(Db.Scalar("SELECT COUNT(*) FROM eval_runs WHERE suite_id = $suite;", ("$suite", suiteId)) ?? 0L);

    /// <summary>Every run still queued or running.</summary>
    public IReadOnlyList<SuiteRun> ActiveRuns() => Records<SuiteRun>($"SELECT data FROM eval_runs WHERE status IN {Active} ORDER BY created_at, id;");

    /// <summary>The run of <paramref name="suiteId"/> still queued or running, or null.</summary>
    public SuiteRun? ActiveRun(string suiteId) =>
        Records<SuiteRun>($"SELECT data FROM eval_runs WHERE suite_id = $suite AND status IN {Active} ORDER BY number DESC LIMIT 1;", ("$suite", suiteId))
            .FirstOrDefault();

    /// <summary>The metered costs of the last <paramref name="count"/> complete runs of <paramref name="suiteId"/>, newest first (for the
    /// typical cost).</summary>
    public IReadOnlyList<long> RecentCosts(string suiteId, int count = 20) =>
        Db.Query("SELECT cost_micros FROM eval_runs WHERE suite_id = $suite AND status = 'complete' ORDER BY number DESC LIMIT $count;",
            reader => reader.GetInt64(0), ("$suite", suiteId), ("$count", Math.Clamp(count, 1, 1000)));

    /// <summary>Inserts <paramref name="run"/> as its suite's next run, with its <paramref name="results"/> and, for a scheduled run, its slot's
    /// <see cref="SlotOutcome.Started"/> decision, in one transaction.</summary>
    /// <returns>The run with its number; null when its slot was already decided (nothing is inserted).</returns>
    /// <exception cref="SqliteException">Its suite does not exist.</exception>
    public SuiteRun? InsertRun(SuiteRun run, IReadOnlyList<SuiteResult> results, DateTimeOffset decidedAt)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(results);
        return Db.Transaction((connection, transaction) =>
        {
            if (run.Slot is { } slot && WebAgentDb.Scalar(connection, transaction, "SELECT 1 FROM eval_schedule_slots WHERE suite_id = $suite AND slot = $slot;",
                    ("$suite", run.SuiteId), ("$slot", EvalJson.ToColumn(slot))) is not null)
                return null;
            var number = Convert.ToInt32(WebAgentDb.Scalar(connection, transaction, "SELECT COALESCE(MAX(number), 0) + 1 FROM eval_runs WHERE suite_id = $suite;",
                ("$suite", run.SuiteId)) ?? 1L);
            var numbered = run with { Number = number };
            WebAgentDb.Execute(connection, transaction, """
                INSERT INTO eval_runs (id, suite_id, number, run_trigger, slot, status, group_id, reserved_micros, cost_micros, list_micros, data, created_at, finished_at)
                VALUES ($id, $suite, $number, $trigger, $slot, $status, $group, $reserved, $cost, $list, $data, $created, $finished);
                """, RunParameters(numbered));
            if (numbered.Slot is { } at)
                WebAgentDb.Execute(connection, transaction, """
                    INSERT INTO eval_schedule_slots (suite_id, slot, outcome, run_id, detail, decided_at) VALUES ($suite, $slot, 'started', $run, NULL, $at);
                    """, ("$suite", numbered.SuiteId), ("$slot", EvalJson.ToColumn(at)), ("$run", numbered.Id), ("$at", EvalJson.ToColumn(decidedAt)));
            foreach (var result in results) WebAgentDb.Execute(connection, transaction, ResultUpsert, ResultParameters(result));
            return numbered;
        });
    }

    /// <summary>Saves <paramref name="run"/> over the stored one (its number, trigger and slot are kept).</summary>
    /// <returns>Whether the run exists.</returns>
    public bool UpdateRun(SuiteRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Db.Execute("""
            UPDATE eval_runs SET status = $status, group_id = $group, reserved_micros = $reserved, cost_micros = $cost, list_micros = $list, data = $data,
                finished_at = $finished
            WHERE id = $id;
            """, RunParameters(run)) > 0;
    }

    /// <summary>The results of the run <paramref name="runId"/>, in the order they were made (case, then target, then repetition).</summary>
    public IReadOnlyList<SuiteResult> Results(string runId) =>
        Records<SuiteResult>("SELECT data FROM eval_results WHERE run_id = $run ORDER BY rowid;", ("$run", runId));

    /// <summary>Inserts or replaces <paramref name="result"/>.</summary>
    public void SaveResult(SuiteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Db.Execute(ResultUpsert, ResultParameters(result));
    }

    /// <summary>Marks the run's queued and running results cancelled (never answered), at <paramref name="now"/>.</summary>
    /// <returns>How many it changed.</returns>
    public int CancelUnfinished(string runId, DateTimeOffset now) => Db.Transaction((connection, transaction) =>
    {
        var open = new List<SuiteResult>();
        using (var command = WebAgentDb.Command(connection, transaction, "SELECT data FROM eval_results WHERE run_id = $run AND status IN ('queued', 'running');",
            ("$run", runId)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (EvalJson.TryDeserialize<SuiteResult>(reader.GetString(0)) is { } result) open.Add(result);
        foreach (var result in open)
            WebAgentDb.Execute(connection, transaction, ResultUpsert, ResultParameters(result with { Status = ResultStatus.Cancelled, Finished = now }));
        return open.Count;
    });

    // ----- schedule slots -----

    /// <summary>Records a skipped slot decision unless the slot was already decided.</summary>
    /// <returns>Whether it was recorded.</returns>
    public bool TryRecordSlot(ScheduleSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.Outcome == SlotOutcome.Started) throw new ArgumentException("A started slot is recorded with its run (InsertRun).", nameof(slot));
        return Db.Execute("""
            INSERT OR IGNORE INTO eval_schedule_slots (suite_id, slot, outcome, run_id, detail, decided_at) VALUES ($suite, $slot, $outcome, NULL, $detail, $at);
            """, ("$suite", slot.SuiteId), ("$slot", EvalJson.ToColumn(slot.Slot)), ("$outcome", EvalJson.Wire(slot.Outcome)), ("$detail", slot.Detail),
            ("$at", EvalJson.ToColumn(slot.At))) > 0;
    }

    /// <summary>The decision on <paramref name="slot"/> of <paramref name="suiteId"/>, or null when it is not decided.</summary>
    public ScheduleSlot? Slot(string suiteId, DateTimeOffset slot) =>
        Db.Query("SELECT suite_id, slot, outcome, run_id, detail, decided_at FROM eval_schedule_slots WHERE suite_id = $suite AND slot = $slot;", ReadSlot,
            ("$suite", suiteId), ("$slot", EvalJson.ToColumn(slot))).FirstOrDefault();

    /// <summary>The slot decisions of <paramref name="suiteId"/>, newest slot first.</summary>
    public IReadOnlyList<ScheduleSlot> Slots(string suiteId, int limit = 50) =>
        Db.Query("SELECT suite_id, slot, outcome, run_id, detail, decided_at FROM eval_schedule_slots WHERE suite_id = $suite ORDER BY slot DESC LIMIT $limit;",
            ReadSlot, ("$suite", suiteId), ("$limit", Math.Clamp(limit, 1, 1000)));

    // ----- baselines -----

    /// <summary>The baselines of <paramref name="suiteId"/>: the current one per target, or every one (newest first) when
    /// <paramref name="currentOnly"/> is false.</summary>
    public IReadOnlyList<Baseline> Baselines(string suiteId, bool currentOnly = true) =>
        Records<Baseline>($"SELECT data FROM eval_baselines WHERE suite_id = $suite{(currentOnly ? " AND replaced_at IS NULL" : "")} ORDER BY accepted_at DESC, id DESC;",
            ("$suite", suiteId));

    /// <summary>The current baseline of <paramref name="targetId"/> in <paramref name="suiteId"/>, or null.</summary>
    public Baseline? CurrentBaseline(string suiteId, string targetId) =>
        Records<Baseline>("SELECT data FROM eval_baselines WHERE suite_id = $suite AND target_id = $target AND replaced_at IS NULL;",
            ("$suite", suiteId), ("$target", targetId)).FirstOrDefault();

    /// <summary>Makes each of <paramref name="accepted"/> its target's current baseline (the one it replaces gets
    /// <see cref="Baseline.Replaced"/>) and saves the signals it closes, in one transaction.</summary>
    public void AcceptBaselines(IReadOnlyList<Baseline> accepted, IReadOnlyList<DriftSignal> cleared)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(cleared);
        Db.Transaction((connection, transaction) =>
        {
            foreach (var baseline in accepted)
            {
                var current = new List<(long Id, Baseline Baseline)>();
                using (var command = WebAgentDb.Command(connection, transaction,
                    "SELECT id, data FROM eval_baselines WHERE suite_id = $suite AND target_id = $target AND replaced_at IS NULL;",
                    ("$suite", baseline.SuiteId), ("$target", baseline.TargetId)))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        if (EvalJson.TryDeserialize<Baseline>(reader.GetString(1)) is { } found) current.Add((reader.GetInt64(0), found));
                foreach (var (id, old) in current)
                    WebAgentDb.Execute(connection, transaction, "UPDATE eval_baselines SET replaced_at = $at, data = $data WHERE id = $id;",
                        ("$id", id), ("$at", EvalJson.ToColumn(baseline.Accepted)), ("$data", EvalJson.Serialize(old with { Replaced = baseline.Accepted })));
                WebAgentDb.Execute(connection, transaction, """
                    INSERT INTO eval_baselines (suite_id, target_id, run_id, identity_key, data, accepted_at, replaced_at)
                    VALUES ($suite, $target, $run, $identity, $data, $at, NULL);
                    """, ("$suite", baseline.SuiteId), ("$target", baseline.TargetId), ("$run", baseline.RunId), ("$identity", baseline.Identity.Key),
                    ("$data", EvalJson.Serialize(baseline with { Replaced = null })), ("$at", EvalJson.ToColumn(baseline.Accepted)));
            }
            foreach (var signal in cleared) UpdateSignal(connection, transaction, signal);
            return 0;
        });
    }

    // ----- drift signals -----

    /// <summary>The signals of <paramref name="suiteId"/> (of one target when <paramref name="targetId"/> is given), open ones only unless
    /// <paramref name="openOnly"/> is false, oldest first.</summary>
    public IReadOnlyList<DriftSignal> Signals(string suiteId, string? targetId = null, bool openOnly = true) =>
        Db.Query($"SELECT id, data FROM eval_drift_signals WHERE suite_id = $suite{(targetId is null ? "" : " AND target_id = $target")}" +
                 $"{(openOnly ? " AND cleared_at IS NULL" : "")} ORDER BY created_at, id;", ReadSignal, ("$suite", suiteId), ("$target", targetId))
            .OfType<DriftSignal>().ToList();

    /// <summary>Every open signal of every suite, oldest first.</summary>
    public IReadOnlyList<DriftSignal> OpenSignals() =>
        Db.Query("SELECT id, data FROM eval_drift_signals WHERE cleared_at IS NULL ORDER BY created_at, id;", ReadSignal).OfType<DriftSignal>().ToList();

    /// <summary>The signals the run <paramref name="runId"/> raised (open or closed since), oldest first.</summary>
    public IReadOnlyList<DriftSignal> RunSignals(string runId) =>
        Db.Query("SELECT id, data FROM eval_drift_signals WHERE run_id = $run ORDER BY created_at, id;", ReadSignal, ("$run", runId)).OfType<DriftSignal>().ToList();

    /// <summary>Stores one analysis: inserts <paramref name="raised"/> and saves <paramref name="closed"/> by id, in one transaction.</summary>
    /// <returns>The raised signals with their ids.</returns>
    public IReadOnlyList<DriftSignal> ApplyDrift(IReadOnlyList<DriftSignal> raised, IReadOnlyList<DriftSignal> closed)
    {
        ArgumentNullException.ThrowIfNull(raised);
        ArgumentNullException.ThrowIfNull(closed);
        return Db.Transaction((connection, transaction) =>
        {
            var saved = new List<DriftSignal>();
            foreach (var signal in closed) UpdateSignal(connection, transaction, signal);
            foreach (var signal in raised)
            {
                var id = Convert.ToInt64(WebAgentDb.Scalar(connection, transaction, """
                    INSERT INTO eval_drift_signals (suite_id, target_id, run_id, baseline_run_id, first_run_id, rule, level, data, created_at, cleared_at)
                    VALUES ($suite, $target, $run, $baseline, $first, $rule, $level, $data, $created, $cleared) RETURNING id;
                    """, ("$suite", signal.SuiteId), ("$target", signal.TargetId), ("$run", signal.RunId), ("$baseline", signal.BaselineRunId),
                    ("$first", signal.FirstRunId), ("$rule", EvalJson.Wire(signal.Rule)), ("$level", EvalJson.Wire(signal.Level)),
                    ("$data", EvalJson.Serialize(signal with { Id = 0 })), ("$created", EvalJson.ToColumn(signal.Created)),
                    ("$cleared", signal.Cleared is { } at ? EvalJson.ToColumn(at) : null)));
                saved.Add(signal with { Id = id });
            }
            return (IReadOnlyList<DriftSignal>)saved;
        });
    }

    // ----- helpers -----

    private const string ResultUpsert = """
        INSERT INTO eval_results (run_id, case_id, target_id, repetition, status, call_run_id, verdict, score, response_model, fingerprint, cost_micros,
            list_price, duration_ms, data, finished_at)
        VALUES ($run, $case, $target, $repetition, $status, $call, $verdict, $score, $responseModel, $fingerprint, $cost, $list, $duration, $data, $finished)
        ON CONFLICT (run_id, case_id, target_id, repetition) DO UPDATE SET status = excluded.status, call_run_id = excluded.call_run_id,
            verdict = excluded.verdict, score = excluded.score, response_model = excluded.response_model, fingerprint = excluded.fingerprint,
            cost_micros = excluded.cost_micros, list_price = excluded.list_price, duration_ms = excluded.duration_ms, data = excluded.data,
            finished_at = excluded.finished_at;
        """;

    private static void UpdateSignal(SqliteConnection connection, SqliteTransaction transaction, DriftSignal signal)
    {
        if (signal.Id <= 0) throw new ArgumentException("A signal is updated by its id.");
        WebAgentDb.Execute(connection, transaction, """
            UPDATE eval_drift_signals SET level = $level, first_run_id = $first, data = $data, cleared_at = $cleared WHERE id = $id;
            """, ("$id", signal.Id), ("$level", EvalJson.Wire(signal.Level)), ("$first", signal.FirstRunId), ("$data", EvalJson.Serialize(signal with { Id = 0 })),
            ("$cleared", signal.Cleared is { } at ? EvalJson.ToColumn(at) : null));
    }

    private List<T> Records<T>(string sql, params (string Name, object? Value)[] parameters) where T : class =>
        [.. Db.Query(sql, reader => EvalJson.TryDeserialize<T>(reader.GetString(0)), parameters).OfType<T>()];

    private static DriftSignal? ReadSignal(SqliteDataReader reader) =>
        EvalJson.TryDeserialize<DriftSignal>(reader.GetString(1)) is { } signal ? signal with { Id = reader.GetInt64(0) } : null;

    private static ScheduleSlot ReadSlot(SqliteDataReader reader) => new(
        reader.GetString(0),
        EvalJson.FromColumn(reader.GetInt64(1)),
        EvalJson.FromWire<SlotOutcome>(reader.GetString(2)) ?? SlotOutcome.SkippedRefused,
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        EvalJson.FromColumn(reader.GetInt64(5)));

    private static (string, object?)[] SuiteParameters(Suite suite) =>
    [
        ("$id", suite.Id), ("$name", suite.Name), ("$enabled", suite.Schedule is { Enabled: true } ? 1 : 0), ("$cap", suite.CapMicros),
        ("$data", EvalJson.Serialize(suite)), ("$created", EvalJson.ToColumn(suite.Created)), ("$updated", EvalJson.ToColumn(suite.Updated)),
    ];

    private static (string, object?)[] CaseParameters(TestCase testCase) =>
    [
        ("$id", testCase.Id), ("$suite", testCase.SuiteId), ("$name", testCase.Name), ("$origin", EvalJson.Wire(testCase.Origin.Source)),
        ("$data", EvalJson.Serialize(testCase)), ("$created", EvalJson.ToColumn(testCase.Created)), ("$updated", EvalJson.ToColumn(testCase.Updated)),
    ];

    private static (string, object?)[] RunParameters(SuiteRun run) =>
    [
        ("$id", run.Id), ("$suite", run.SuiteId), ("$number", run.Number), ("$trigger", EvalJson.Wire(run.Trigger)),
        ("$slot", run.Slot is { } slot ? EvalJson.ToColumn(slot) : null), ("$status", EvalJson.Wire(run.Status)), ("$group", run.GroupId),
        ("$reserved", run.ReservedMicros), ("$cost", run.CostMicros), ("$list", run.ListMicros), ("$data", EvalJson.Serialize(run)),
        ("$created", EvalJson.ToColumn(run.Created)), ("$finished", run.Finished is { } finished ? EvalJson.ToColumn(finished) : null),
    ];

    private static (string, object?)[] ResultParameters(SuiteResult result) =>
    [
        ("$run", result.RunId), ("$case", result.CaseId), ("$target", result.TargetId), ("$repetition", result.Repetition),
        ("$status", EvalJson.Wire(result.Status)), ("$call", result.CallRunId), ("$verdict", result.Verdict is { } verdict ? EvalJson.Wire(verdict) : null),
        ("$score", result.Score is { } score && double.IsFinite(score) ? score : null), ("$responseModel", result.ResponseModel), ("$fingerprint", result.Fingerprint),
        ("$cost", result.CostMicros), ("$list", result.ListPrice ? 1 : 0), ("$duration", result.DurationMs), ("$data", EvalJson.Serialize(result)),
        ("$finished", result.Finished is { } finished ? EvalJson.ToColumn(finished) : null),
    ];
}
