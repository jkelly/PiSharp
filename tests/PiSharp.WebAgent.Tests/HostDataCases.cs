using Microsoft.Data.Sqlite;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;

// Decision 0011 (run W2, WU4): webagent.db in process: migrations registered by id inside their area's range apply once, in id order, each
// with its record in one transaction (so a failed one leaves nothing), a lower id added later still runs, PRAGMA user_version follows the
// highest id, and concurrent starts apply each migration once. The settings store: defaults, round trips, change notification,
// validation, an unreadable stored section and concurrent updates.
internal static partial class Program
{
    private static SqlMigration TestTable(int id) => new(id, "test: table " + id, $"CREATE TABLE t{id} (x INTEGER);");

    internal static IEnumerable<(string, Func<Task>)> HostDataCases() =>
    [
        Case("host.migrations-apply-once-in-id-order", () =>
        {
            var root = Temp("migrate-once");
            try
            {
                var path = Path.Join(root, "webagent.db");
                var db = new WebAgentDb(path, [TestTable(50), CoreMigrations.AppInfo, TestTable(2500), TestTable(150)]);
                Equal("1,50,150,2500", string.Join(",", db.Migrate()), "the first run applies everything, lowest id first");
                Equal("", string.Join(",", db.Migrate()), "a second run applies nothing");
                Equal("", string.Join(",", new WebAgentDb(path, db.Migrations).Migrate()), "nor does another instance");
                Equal("1,50,150,2500", string.Join(",", db.AppliedMigrations()), "schema_migrations");
                Equal(2500, db.UserVersion(), "user_version is the highest id");
                Equal("wal", db.Scalar("PRAGMA journal_mode;") as string, "WAL");
                Equal("1", db.Scalar("SELECT value FROM app_info WHERE key = 'data_layout';") as string, "the core migration's row");
                Equal(4L, db.Scalar("SELECT COUNT(*) FROM schema_migrations WHERE name LIKE 'test:%' OR id = 1;"), "names are recorded");
            }
            finally { DeleteTemp(root); }
        }),

        Case("host.migrations-run-a-lower-id-added-later", () =>
        {
            var root = Temp("migrate-later");
            try
            {
                var path = Path.Join(root, "webagent.db");
                Equal("1,300", string.Join(",", new WebAgentDb(path, [CoreMigrations.AppInfo, SettingsStore.Migration]).Migrate()), "the first release");
                var later = new WebAgentDb(path, [CoreMigrations.AppInfo, SettingsStore.Migration, TestTable(20), TestTable(100), TestTable(1000)]);
                Equal("20,100,1000", string.Join(",", later.Migrate()), "ids below the highest applied still run");
                Equal("1,20,100,300,1000", string.Join(",", later.AppliedMigrations()), "all recorded");
                Equal(1000, later.UserVersion(), "user_version");
                Equal(0L, later.Scalar("SELECT COUNT(*) FROM t20;"), "the late migration's table");
                // A migration that is no longer registered stays recorded and is not undone.
                Equal("", string.Join(",", new WebAgentDb(path, [CoreMigrations.AppInfo]).Migrate()), "a smaller set applies nothing");
                Equal("1,20,100,300,1000", string.Join(",", later.AppliedMigrations()), "and forgets nothing");
            }
            finally { DeleteTemp(root); }
        }),

        Case("host.migrations-refuse-ids-outside-areas-and-duplicates", () =>
        {
            var path = Path.Join(Path.GetTempPath(), "pisharp-webagent", "never-created.db");
            foreach (var id in new[] { 0, -1, 400, 999, 3000 })
                Throws<ArgumentException>(() => new WebAgentDb(path, [TestTable(id)]), "id " + id);
            var duplicate = Throws<ArgumentException>(() => new WebAgentDb(path, [TestTable(301), new SqlMigration(301, "other", "SELECT 1;")]), "a duplicate id");
            Check(duplicate.Message.Contains("301", StringComparison.Ordinal), "the message names the id");
            Check(!File.Exists(path), "nothing is created by a refused set");
            Equal("core,accounts,chats,settings,spend,evals", string.Join(",", MigrationArea.All.Select(area => area.Name)), "the areas");
            Equal("1-99,100-199,200-299,300-399,1000-1999,2000-2999", string.Join(",", MigrationArea.All.Select(area => $"{area.First}-{area.Last}")), "their ranges");
            Equal(MigrationArea.Chats, MigrationArea.Of(250), "Of");
            Equal(null, MigrationArea.Of(500), "a gap");
        }),

        Case("host.migrations-a-failed-migration-leaves-nothing", () =>
        {
            var root = Temp("migrate-fail");
            try
            {
                var path = Path.Join(root, "webagent.db");
                var broken = new SqlMigration(60, "test: half done", "CREATE TABLE half (x INTEGER); INSERT INTO missing_table VALUES (1);");
                var db = new WebAgentDb(path, [CoreMigrations.AppInfo, broken, TestTable(70)]);
                Throws<SqliteException>(() => db.Migrate(), "the broken migration fails the run");
                Equal("1", string.Join(",", ReadDb(path, "SELECT id FROM schema_migrations ORDER BY id;", r => r.GetInt32(0))),
                    "only what ran fully is recorded; nothing after the failure ran");
                Equal(0L, ReadDb(path, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'half';", r => r.GetInt64(0)).Single(), "its first statement was rolled back");
                Throws<SqliteException>(() => db.Scalar("SELECT 1;"), "the database is not used until its migrations apply");
                Equal("60,70", string.Join(",", new WebAgentDb(path, [CoreMigrations.AppInfo, broken with { Sql = "CREATE TABLE half (x INTEGER);" }, TestTable(70)]).Migrate()),
                    "a fixed migration runs on the next start");
            }
            finally { DeleteTemp(root); }
        }),

        Case("host.migrations-concurrent-starts-apply-each-once", async () =>
        {
            var root = Temp("migrate-race");
            try
            {
                var path = Path.Join(root, "webagent.db");
                IWebAgentMigration[] migrations = [CoreMigrations.AppInfo, SettingsStore.Migration, .. Enumerable.Range(2, 30).Select(TestTable)];
                var runs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => new WebAgentDb(path, migrations).Migrate())));
                var applied = runs.SelectMany(run => run).ToList();
                Equal(migrations.Length, applied.Count, "each migration applied once across the starts");
                Equal(migrations.Length, applied.Distinct().Count(), "no migration twice");
                Equal(migrations.Length, new WebAgentDb(path, migrations).AppliedMigrations().Count, "all recorded");
            }
            finally { DeleteTemp(root); }
        }),

        Case("host.settings-defaults-round-trip-and-notify", async () =>
        {
            var root = Temp("settings");
            try
            {
                var path = Path.Join(root, "webagent.db");
                var store = new SettingsStore(new WebAgentDb(path, [CoreMigrations.AppInfo, SettingsStore.Migration]));
                Equal(new DefaultsSettings(), store.Get<DefaultsSettings>(), "Defaults when nothing is stored");
                var tools = store.Get<ToolsSettings>();
                Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), tools.WorkingDirectory, "the working directory defaults to the user's home");
                Equal(ToolApprovalMode.Ask, tools.ApprovalMode, "ask by default");
                Equal(0, tools.AllowList!.Count, "an empty allow-list");
                Equal(0, store.Get<ModelsSettings>().Hidden!.Count, "no hidden models");

                var changes = new List<string>();
                store.Changed += (_, e) => changes.Add(e.Section);
                var toolChanges = new List<ToolsSettings>();
                var subscription = store.OnChanged<ToolsSettings>(toolChanges.Add);
                var saved = await store.SaveAsync(new DefaultsSettings(" faux ", "echo-thinking ", "high"));
                Equal(new DefaultsSettings("faux", "echo-thinking", "high"), saved, "saved values are trimmed");
                var work = Path.Join(root, "work");
                await store.SaveAsync(new ToolsSettings(work, ToolApprovalMode.AllowList,
                    [new ToolAllowRule("bash", "dotnet test *"), new ToolAllowRule(" bash ", " dotnet test * "), new ToolAllowRule("read")]));
                await store.SaveAsync(new ModelsSettings(["faux/echo-small", "faux/echo-small"]));
                Equal("defaults,tools,models", string.Join(",", changes), "every save notifies its section");
                Equal(1, toolChanges.Count, "OnChanged<T> sees only its section");
                subscription.Dispose();
                await store.UpdateAsync<ToolsSettings>(current => current with { ApprovalMode = ToolApprovalMode.Auto });
                Equal(1, toolChanges.Count, "a disposed subscription is quiet");

                var reopened = new SettingsStore(new WebAgentDb(path, [CoreMigrations.AppInfo, SettingsStore.Migration]));
                Equal(new DefaultsSettings("faux", "echo-thinking", "high"), reopened.Get<DefaultsSettings>(), "Defaults persist");
                var reread = reopened.Get<ToolsSettings>();
                Equal(work, reread.WorkingDirectory, "the working directory persists");
                Equal(ToolApprovalMode.Auto, reread.ApprovalMode, "the mode persists");
                Equal("bash:dotnet test *|read:", string.Join("|", reread.AllowList!.Select(rule => rule.Tool + ":" + rule.Command)), "rules are trimmed and deduplicated");
                Check(reopened.Get<ModelsSettings>().IsHidden("faux", "echo-small"), "hidden models persist");
                Equal(1, reopened.Get<ModelsSettings>().Hidden!.Count, "without duplicates");

                var json = ReadDb(path, "SELECT value FROM settings WHERE section = 'tools';", r => r.GetString(0)).Single();
                Check(json.Contains("\"approvalMode\":\"auto\"", StringComparison.Ordinal), "camelCase JSON with the mode by name: " + json);
                Equal("defaults,models,tools", string.Join(",", ReadDb(path, "SELECT section FROM settings ORDER BY section;", r => r.GetString(0))), "one row per section");

                await reopened.ResetAsync<ToolsSettings>();
                Equal(ToolApprovalMode.Ask, reopened.Get<ToolsSettings>().ApprovalMode, "reset gives the defaults");
                Equal("defaults,models", string.Join(",", ReadDb(path, "SELECT section FROM settings ORDER BY section;", r => r.GetString(0))), "and removes the row");
            }
            finally { DeleteTemp(root); }
        }),

        Case("host.settings-validation-and-unreadable-sections", async () =>
        {
            var root = Temp("settings-invalid");
            try
            {
                var path = Path.Join(root, "webagent.db");
                var db = new WebAgentDb(path, [CoreMigrations.AppInfo, SettingsStore.Migration]);
                var store = new SettingsStore(db);
                var notified = 0;
                store.Changed += (_, _) => notified++;
                await ThrowsAsync<ArgumentException>(() => store.SaveAsync(new DefaultsSettings("faux", null)), "a provider without a model");
                await ThrowsAsync<ArgumentException>(() => store.SaveAsync(new DefaultsSettings(null, "echo")), "a model without a provider");
                await ThrowsAsync<ArgumentException>(() => store.SaveAsync(new DefaultsSettings(Thinking: "turbo")), "an unknown thinking level");
                await ThrowsAsync<ArgumentException>(() => store.SaveAsync(new ToolsSettings("relative\\folder")), "a relative working directory");
                await ThrowsAsync<ArgumentException>(() => store.SaveAsync(new ToolsSettings(Path.Join(root, "w"), (ToolApprovalMode)7)), "an unknown mode");
                await ThrowsAsync<ArgumentException>(() => store.SaveAsync(new ToolsSettings(Path.Join(root, "w"), AllowList: [new ToolAllowRule(" ")])), "a rule without a tool");
                await ThrowsAsync<ArgumentException>(() => store.SaveAsync(new ModelsSettings(["no-slash"])), "a hidden key without provider/model");
                await ThrowsAsync<ArgumentException>(() => store.UpdateAsync<ModelsSettings>(_ => new ModelsSettings(["/x"])), "an update is validated too");
                Equal(0, notified, "nothing refused is announced");
                Equal(0L, db.Scalar("SELECT COUNT(*) FROM settings;"), "nor stored");
                foreach (var level in PiSharp.AI.ThinkingLevels.Ordered)
                    Equal(level, (await store.SaveAsync(new DefaultsSettings(Thinking: level))).Thinking, "thinking level " + level);

                db.Execute("INSERT INTO settings (section, value, updated_at) VALUES ('tools', '{not json', 0);");
                db.Execute("INSERT INTO settings (section, value, updated_at) VALUES ('models', '{\"hidden\":[\"bad\"]}', 0);");
                var fresh = new SettingsStore(new WebAgentDb(path, [CoreMigrations.AppInfo, SettingsStore.Migration]));
                Equal(ToolApprovalMode.Ask, fresh.Get<ToolsSettings>().ApprovalMode, "unreadable JSON reads as the defaults");
                Equal(0, fresh.Get<ModelsSettings>().Hidden!.Count, "an invalid stored value reads as the defaults");
                Equal("max", fresh.Get<DefaultsSettings>().Thinking, "other sections are unaffected");
            }
            finally { DeleteTemp(root); }
        }),

        Case("host.settings-concurrent-updates-lose-nothing", async () =>
        {
            var root = Temp("settings-race");
            try
            {
                var store = new SettingsStore(new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, SettingsStore.Migration]));
                await Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(() =>
                    store.UpdateAsync<ModelsSettings>(current => new ModelsSettings([.. current.Hidden ?? [], "faux/m" + index])))));
                Equal(20, store.Get<ModelsSettings>().Hidden!.Count, "every update kept");
                var reopened = new SettingsStore(new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, SettingsStore.Migration]));
                Equal(20, reopened.Get<ModelsSettings>().Hidden!.Count, "and stored");
            }
            finally { DeleteTemp(root); }
        }),
    ];
}
