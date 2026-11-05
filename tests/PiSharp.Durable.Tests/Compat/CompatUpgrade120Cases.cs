// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/storage/sqlite/migrations.ts and schema.ts (schema version 2,
// the sleeping task status), packages/durable/src/harness/harness.ts (open and resume), packages/durable/src/harness/generation.ts (a
// charged retry's backoff sleeps, f75b3240a) and packages/durable/src/tools/read.ts (the read tool's pi-schema declaration). There is no
// upstream test file: the cases are PiSharp's own (authored, CompatTests.AuthoredOverCapture), closing out the Pi v1.5.0 sync.
// compat.upgrade-from-v1.2.0.* checks that v1.5.0 code opens and continues the sessions Pi v1.2.0 wrote (Fixtures/v1.2.0/upstream-sessions;
// the captures are not recaptured, decision 0013) on each backend that held a v1.2.0 session: SQLite and JSONL, which upstream wrote, and
// SQL Server, which PiSharp 1.2.0.2 wrote (upstream has no SQL Server storage). For each scenario of the v1.2.0 capture:
// - <scenario>.sqlite.migrates-the-schema-to-version-2: PiSharp's storage opens a copy written at schema version 1 and migrates it to
//   version 2, whose tasks table admits the sleeping status; every row of every table, the metadata and the indexes are kept.
// - <scenario>.mssql.opened, and .resumed for a resumable scenario (when PISHARP_DURABLE_MSSQL_CONNECTION_STRING names a server): PiSharp
//   1.2.0.2's SQL Server storage stored schema version 1 (SqlServerMigrations.Migrations[0], unchanged since) holding the record texts the
//   SQLite storage holds. The case writes the SQLite capture's rows into a fresh version 1 schema as one store, opens it (migrating to version
//   2), reads the captured records, opens and closes a Harness with the records the sqlite cases expect, and resumes as compat.*.resume does.
// And for the v1.5.0 behaviour the existing cases avoid:
// - follow-up.<backend>.sleeps-the-charged-retry-across-a-reopen-and-answers-both-inputs: the request the capture left in flight is charged
//   (area restarts), and its backoff now sleeps (area sleep): the generation task is stored sleeping, which a migrated SQLite or SQL Server
//   tasks table must admit (compat.follow-up.*.resume retries at once instead). Reopened after the backoff, the retry answers the first input
//   and the follow-up the second.
// - read-image.<backend>.re-adds-the-built-in-tool-declarations-once: v1.5.0 declares the read tool with pi-schema's key order, which
//   differs from v1.2.0's TypeBox order (area tools-schema), so the next input on a v1.2.0 session removes and re-adds the declaration
//   once, as prompt.ts's planTools does for a changed declaration; the new one differs only by key order, and the input after it changes
//   no tool. Upstream v1.5.0 does the same (judged from source; not run).
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.MSSQL;
using static ChatTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CompatUpgrade120Cases() => CompatTests.Upgrade120Cases();
}

internal static partial class CompatTests
{
    /// <summary>The backends of the upgrade cases: the capture's own, and SQL Server when a server is configured.</summary>
    private static string[] UpgradeBackends => MssqlServer.Configured ? ["sqlite", "jsonl", "mssql"] : ["sqlite", "jsonl"];

    /// <summary>The scenarios of the v1.2.0 capture with their resume (null for a read-only one) and the replacement of the resumed.json
    /// comparison some of them use (CompatScenario120.ResumeUpstream).</summary>
    private static IEnumerable<(string Name, Func<Func<Task<IStorage>>, Task>? Resume, Func<Func<Task<IStorage>>, Task>? Own)> Upgrade120Scenarios() =>
    [
        .. CompatScenarios.All.Select(scenario => (scenario.Name, scenario.Resume, (Func<Func<Task<IStorage>>, Task>?)null)),
        .. CompatScenarios120.All.Select(scenario => (scenario.Name,
            scenario.Resume is { } resume ? async open => { await resume(open); } : (Func<Func<Task<IStorage>>, Task>?)null, scenario.ResumeUpstream)),
    ];

    internal static IEnumerable<(string, Func<Task>)> Upgrade120Cases()
    {
        var captured = ((JsonArrayValue)Load("manifest.json", Fixtures120)["scenarios"]).Select(item => S(((JsonObjectValue)item)["name"])).ToHashSet();
        foreach (var (name, resume, own) in Upgrade120Scenarios())
        {
            Check(captured.Contains(name), $"Fixtures/v1.2.0 captures {name}");
            var prefix = $"compat.upgrade-from-v1.2.0.{name}.";
            yield return Case(AuthoredCase(prefix + "sqlite.migrates-the-schema-to-version-2"), () => MigratesSqlite(name));
            if (!MssqlServer.Configured) continue;
            yield return Case(AuthoredCase(prefix + "mssql.opened"), async () =>
            {
                var open = await ImportMssql(name);
                SameJson(Expected(name, "records.json", "sqlite"), await Dump(await open()), "records");
                await OpenOnly(open);
                // Pi v1.5.0 (area restarts): the open counts a restart of each task it finds running (Restarts150.cs).
                SameJson(Restarts150.Opened(Expected(name, "opened.json", "sqlite"), Expected(name, "records.json", "sqlite")), await Dump(await open()),
                    "opened records");
            });
            if (resume is null) continue;
            yield return Case(AuthoredCase(prefix + "mssql.resumed"), async () =>
            {
                var open = await ImportMssql(name);
                if (own is not null)
                {
                    await own(open);
                    return;
                }
                await resume(open);
                // As compat.<scenario>.<backend>.resume: (area model-requests) ModelRequests150, (area tool-tasks) ToolTasks150 and (area
                // live-turns) LiveV3150's forms of the sqlite capture's resumed records.
                var held = Expected(name, "records.json", "sqlite");
                SameJson(LiveV3150.Resumed(ToolTasks150.Resumed(ModelRequests150.Authored(Expected(name, "resumed.json", "sqlite"), held), held), held),
                    await Dump(await open()), "resumed records");
            });
        }
        foreach (var backend in UpgradeBackends)
        {
            yield return Case(AuthoredCase($"compat.upgrade-from-v1.2.0.follow-up.{backend}.sleeps-the-charged-retry-across-a-reopen-and-answers-both-inputs"),
                () => SleepsAcrossReopen(backend));
            yield return Case(AuthoredCase($"compat.upgrade-from-v1.2.0.read-image.{backend}.re-adds-the-built-in-tool-declarations-once"),
                () => RedeclaresOnce(backend));
        }
    }

    // ─── storage of a v1.2.0 session ────────────────────────────────────────────

    /// <summary>A copy of <paramref name="scenario"/>'s v1.2.0 session on <paramref name="backend"/> (written into SQL Server for mssql), and
    /// how to open its storage.</summary>
    private static async Task<Func<Task<IStorage>>> UpgradeSession(string scenario, string backend)
    {
        if (backend == "mssql") return await ImportMssql(scenario);
        var directory = CopyFixture(scenario, backend, Fixtures120);
        return () => OpenBackend(backend, directory);
    }

    private static async Task<IStorage> OpenMssql(string schema, string store) =>
        await MssqlServer.OpenAsync(store, new SqlServerStorageOptions { Schema = schema });

    /// <summary>What the v1.2.0 SQLite capture stores, as the statements of <see cref="Columns"/> read it, with its schema version and
    /// indexes.</summary>
    private static List<string> SqliteContents(string path)
    {
        using var connection = ReadOnly(path);
        var rows = new List<string>();
        foreach (var sql in Columns) rows.AddRange(Rows(connection, sql).Select(row => sql + ": " + string.Join(" | ", row)));
        rows.AddRange(Rows(connection, "SELECT name, tbl_name FROM sqlite_master WHERE type = 'index' ORDER BY name")
            .Select(row => "index: " + string.Join(" | ", row)));
        return rows;
    }

    private static string SqliteScalar(string path, string sql)
    {
        using var connection = ReadOnly(path);
        return Rows(connection, sql).Single()[0];
    }

    private static async Task MigratesSqlite(string scenario)
    {
        var path = Path.Combine(CopyFixture(scenario, "sqlite", Fixtures120), "session.sqlite");
        Equal("1", SqliteScalar(path, "SELECT version FROM durable_schema"), "the capture's schema version");
        var tasksTable = SqliteScalar(path, "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'tasks'");
        Check(!tasksTable.Contains("'sleeping'", StringComparison.Ordinal), "a version 1 tasks table refuses sleeping tasks: " + tasksTable);
        var before = SqliteContents(path);
        await (await OpenBackend("sqlite", Path.GetDirectoryName(path)!)).CloseAsync(Context);
        Equal("2", SqliteScalar(path, "SELECT version FROM durable_schema"), "the migrated schema version");
        var migratedTable = SqliteScalar(path, "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'tasks'");
        Check(migratedTable.Contains("'sleeping'", StringComparison.Ordinal), "the migrated tasks table admits sleeping tasks: " + migratedTable);
        Equal("0", SqliteScalar(path, "SELECT count(*) FROM sqlite_master WHERE name = 'tasks_v2'"), "no rebuilt table left");
        Equal("ok", SqliteScalar(path, "PRAGMA integrity_check"), "integrity");
        var after = SqliteContents(path);
        for (var index = 0; index < Math.Max(before.Count, after.Count); index++)
            Equal(index < before.Count ? before[index] : "<end>", index < after.Count ? after[index] : "<end>", $"row {index + 1}");
    }

    // ─── SQL Server ─────────────────────────────────────────────────────────────

    /// <summary>The SQLite tables of a session and their columns, which SQL Server's tables name with a <c>durable_</c> prefix and a leading
    /// <c>store_id</c> (its computed hash columns are left to the server).</summary>
    private static readonly (string Table, string Columns)[] ImportTables =
    [
        ("record_ids", "id, record_type"),
        ("conversations", "id, owner_conversation_id, owner_task_id, record"),
        ("entries", "id, conversation_id, head, commit_seq, record"),
        ("tasks", "id, conversation_id, kind, status, abort_requested, background, record"),
        ("submissions", "id, conversation_id, request_id, status, record"),
        ("documents", "id, kind, family, key_value, scope_kind, owner_id, created_at, retired_at, record"),
        ("document_revisions", "document_id, seq, kind, version, content"),
    ];

    private static SqlCommand MssqlCommand(SqlConnection connection, string sql, IReadOnlyList<object?> parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var index = 0; index < parameters.Count; index++) command.Parameters.AddWithValue("@p" + index, parameters[index] ?? DBNull.Value);
        return command;
    }

    /// <summary>Writes <paramref name="scenario"/>'s v1.2.0 SQLite capture into a fresh SQL Server schema at version 1, as one store: what
    /// PiSharp 1.2.0.2's SQL Server storage held for the same session. Returns the schema and the store's name.</summary>
    private static async Task<(string Schema, string Store)> ImportMssqlStore(string scenario)
    {
        var schema = MssqlServer.NewSchema();
        var quoted = MssqlServer.Quote(schema);
        var store = MssqlServer.NewStore();
        var database = await SqlServerDatabase.OpenAsync(await MssqlServer.ConnectionStringAsync());
        try
        {
            await SqlServerMigrations.ApplyAsync(database, schema, 5_000, [SqlServerMigrations.Migrations[0]]);
        }
        finally
        {
            await database.CloseAsync();
        }
        Equal(1L, await MssqlServer.ScalarAsync($"SELECT version FROM {quoted}.durable_schema"), "the version 1 schema");
        var path = Path.Combine(CopyFixture(scenario, "sqlite", Fixtures120), "session.sqlite");
        using var sqlite = ReadOnly(path);
        static List<object?[]> Values(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<object?[]>();
            while (reader.Read()) rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index))]);
            return rows;
        }
        await using var connection = new SqlConnection(await MssqlServer.ConnectionStringAsync());
        await connection.OpenAsync();
        var metadata = Values(sqlite, "SELECT next_id, next_seq FROM durable_metadata").Single();
        long storeId;
        await using (var insert = MssqlCommand(connection,
            $"INSERT INTO {quoted}.durable_stores (name, next_id, next_seq) OUTPUT inserted.store_id VALUES (@p0, @p1, @p2)",
            [JsonValue.String(store).ToJson(), long.Parse((string)metadata[0]!, CultureInfo.InvariantCulture), metadata[1]]))
            storeId = Convert.ToInt64(await insert.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        foreach (var (table, columns) in ImportTables)
        {
            var count = columns.Split(',').Length;
            var sql = $"INSERT INTO {quoted}.durable_{table} (store_id, {columns}) VALUES (@p0, " +
                string.Join(", ", Enumerable.Range(1, count).Select(index => "@p" + index)) + ")";
            foreach (var row in Values(sqlite, $"SELECT {columns} FROM {table}"))
            {
                await using var insert = MssqlCommand(connection, sql, [storeId, .. row]);
                await insert.ExecuteNonQueryAsync();
            }
        }
        return (schema, store);
    }

    private static async Task<Func<Task<IStorage>>> ImportMssql(string scenario)
    {
        var (schema, store) = await ImportMssqlStore(scenario);
        return async () =>
        {
            var storage = await OpenMssql(schema, store);
            Equal(2L, await MssqlServer.ScalarAsync($"SELECT version FROM {MssqlServer.Quote(schema)}.durable_schema"), "the migrated schema version");
            return storage;
        };
    }

    // ─── follow-up: a charged retry sleeps across a reopen ──────────────────────

    private static ChatSetup UpgradeChat(double now) => new(new FauxModels(new FauxOptions { Api = "faux" })) { Now = () => now };

    private static async Task SleepsAcrossReopen(string backend)
    {
        string? schema = null, store = null, directory = null;
        Func<Task<IStorage>> open;
        if (backend == "mssql")
        {
            (schema, store) = await ImportMssqlStore("follow-up");
            open = () => OpenMssql(schema, store);
        }
        else
        {
            var copy = directory = CopyFixture("follow-up", backend, Fixtures120);
            open = () => OpenBackend(backend, copy);
        }

        // The host stopped while "one"'s request was in flight; v1.5.0 charges that attempt and sleeps for the backoff. No host timer fires.
        var setup = UpgradeChat(CompatScenarios.Now);
        setup.Retry = new RetrySettings { BaseDelayMs = 1_000 };
        setup.Faux.SetResponses(FauxAssistantMessage("first", timestamp: 1000), FauxAssistantMessage("second", timestamp: 1000));
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions
        {
            Models = setup.Models, Registry = setup.Registry, Settings = setup.Settings, Now = () => CompatScenarios.Now, SetTimer = (_, _, _) => () => { },
        }, Context);
        TaskRecord sleeping;
        try
        {
            harness.Resume();
            await HarnessTests.Eventually(async () => (await harness.InspectAsync(Context)).Tasks.Any(task => task.Record.State.Status == DurableTaskStatus.Sleeping));
            sleeping = (await harness.InspectAsync(Context)).Tasks.Single(task => task.Record.State.Status == DurableTaskStatus.Sleeping).Record;
            Equal("pi.generation", sleeping.Kind, "the sleeping task");
            Equal(0, setup.Faux.State.CallCount, "requests before the backoff ends");
        }
        finally
        {
            await harness.CloseAsync(Context);
        }

        // Stored sleeping: the record, and the status column of a migrated tasks table.
        var storage = await open();
        try
        {
            var stored = (await storage.GetTaskAsync(sleeping.Id, Context))!;
            Check(stored.State is SleepingTaskState, "stored sleeping: " + stored.State.ToJson().ToJson());
            var until = ((SleepingTaskState)stored.State).Until;
            Check(until > CompatScenarios.Now && until <= CompatScenarios.Now + 60_000, $"sleeps until after its backoff: {until}");
        }
        finally
        {
            await storage.CloseAsync(Context);
        }
        switch (backend)
        {
            case "sqlite":
                var path = Path.Combine(directory!, "session.sqlite");
                Equal("2", SqliteScalar(path, "SELECT version FROM durable_schema"), "schema version");
                Equal("sleeping", SqliteScalar(path, $"SELECT status FROM tasks WHERE id = {sleeping.Id.Value}"), "status column");
                break;
            case "mssql":
                var quoted = MssqlServer.Quote(schema!);
                Equal(2L, await MssqlServer.ScalarAsync($"SELECT version FROM {quoted}.durable_schema"), "schema version");
                Equal(1L, await MssqlServer.ScalarAsync($"SELECT count(*) FROM {quoted}.durable_tasks WHERE store_id = @p0 AND id = @p1 AND status = N'sleeping'",
                    await MssqlServer.StoreIdAsync(schema!, store!), sleeping.Id.Value), "status column");
                break;
        }

        // A minute later the backoff is over: the retry answers "one", and the follow-up "two" after it.
        var later = UpgradeChat(CompatScenarios.Now + 60_000);
        later.Retry = new RetrySettings { BaseDelayMs = 1_000 };
        later.Faux.SetResponses(FauxAssistantMessage("first", timestamp: 1000), FauxAssistantMessage("second", timestamp: 1000));
        harness = await Harness.OpenAsync(await open(), new HarnessOptions
        {
            Models = later.Models, Registry = later.Registry, Settings = later.Settings, Now = () => CompatScenarios.Now + 60_000,
        }, Context);
        try
        {
            harness.Resume();
            var submissions = (await harness.InspectAsync(Context)).Submissions.Select(submission => submission.Id).ToList();
            Equal(2, submissions.Count, "the capture's two inputs are live");
            foreach (var id in submissions)
                Equal(SubmissionStatus.Done, (await (await harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, $"submission {id.Value}");
            await harness.WaitForIdleAsync(Context);
            var entries = await AllEntries((await harness.ConversationAsync(ConversationId.Root, Context))!);
            Equal("one,two", string.Join(",", entries.Where(entry => entry.Kind == "pi.user").Select(entry => S(((JsonObjectValue)entry.Model![0])["content"]))),
                "inputs");
            Equal("first,second", string.Join(",", entries.Where(entry => entry.Kind == "pi.assistant").Select(entry => TextOf(entry.Model![0]))), "answers");
            Equal(2, later.Faux.State.CallCount, "requests");
            Equal(0, (await harness.InspectAsync(Context)).Tasks.Length, "live tasks");
            var retried = (await harness.GetTaskAsync(sleeping.Id, Context))!;
            Equal(DurableTaskStatus.Terminal, retried.State.Status, "the retried generation");
        }
        finally
        {
            await harness.CloseAsync(Context);
        }
    }

    // ─── read-image: the built-in tool declarations ─────────────────────────────

    /// <summary><paramref name="value"/> with every object's keys in ordinal order.</summary>
    private static JsonValue SortedKeys(JsonValue value) => value switch
    {
        JsonObjectValue properties => JsonValue.Object(properties.OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => KeyValuePair.Create(property.Key, SortedKeys(property.Value)))),
        JsonArrayValue items => JsonValue.Array(items.Select(SortedKeys)),
        _ => value,
    };

    private static async Task RedeclaresOnce(string backend)
    {
        var open = await UpgradeSession("read-image", backend);
        var captured = (JsonObjectValue)Expected("read-image", "records.json", "sqlite");
        var capturedEntries = ((JsonArrayValue)((JsonObjectValue)captured["entries"])["1"]).Cast<JsonObjectValue>().ToList();
        var declared = capturedEntries.Where(entry => S(entry["kind"]) == "pi.system")
            .SelectMany(entry => (JsonArrayValue)((JsonObjectValue)((JsonArrayValue)entry["model"])[0])["toolsAdded"]).Cast<JsonObjectValue>().ToList();
        Equal("read", string.Join(",", declared.Select(tool => S(tool["name"]))), "the capture's declarations");
        var last = (long)N(capturedEntries[^1]["id"]);

        var setup = UpgradeChat(CompatScenarios.Now);
        setup.Registry.Install(Define.Extension(new Extension { Name = "coding", Tools = [PiSharp.Durable.Tools.CodingTools.CreateReadTool()] }));
        setup.Faux.SetResponses(FauxAssistantMessage("again", timestamp: 1000), FauxAssistantMessage("still", timestamp: 1000));
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions
        {
            Models = setup.Models, Registry = setup.Registry, Settings = setup.Settings, Now = CompatTimers.Clock(() => CompatScenarios.Now),
            SetTimer = CompatTimers.SetTimer,
        }, Context);
        try
        {
            var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "faux-1") });
            harness.Resume();
            async Task<List<EntryRecord>> Next(string input)
            {
                var before = (await AllEntries(root)).Max(entry => entry.Id.Value);
                Equal(SubmissionStatus.Done, (await (await root.SubmitAsync(SubmissionDraft.Input(input), Context)).WaitAsync(Context)).Status, input);
                return [.. (await AllEntries(root)).Where(entry => entry.Id.Value > before)];
            }
            Equal(last, (await AllEntries(root)).Max(entry => entry.Id.Value), "the capture's last entry");
            var first = await Next("again");
            string Describe(List<EntryRecord> entries) => string.Join("\n", entries.Select(entry => entry.ToJson().ToJson()));
            Equal("pi.user,pi.system,pi.assistant", string.Join(",", first.Select(entry => entry.Kind)), "the first input's entries:\n" + Describe(first));
            var system = (JsonObjectValue)first[1].Model![0];
            var added = ((JsonArrayValue)system["toolsAdded"]).Cast<JsonObjectValue>().ToList();
            Equal("read", string.Join(",", added.Select(tool => S(tool["name"]))), "the declarations added again");
            Check(added[0].ToJson() != declared[0].ToJson(), "the v1.5.0 declaration is written in another key order");
            Equal(SortedKeys(declared[0]).ToJson(), SortedKeys(added[0]).ToJson(), "the declaration, apart from key order");
            // prompt.ts planTools: a changed declaration is removed and re-added.
            Equal("""[{"name":"read"}]""", system["toolsRemoved"].ToJson(), "the declarations removed");
            var second = await Next("once more");
            Equal("pi.user,pi.assistant", string.Join(",", second.Select(entry => entry.Kind)), "the second input's entries:\n" + Describe(second));
        }
        finally
        {
            await harness.CloseAsync(Context);
        }
    }
}
