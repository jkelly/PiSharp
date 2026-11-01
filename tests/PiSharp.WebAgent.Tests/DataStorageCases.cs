// Decision 0011 (run W4, unit R9): Settings › Data & storage. DataHost composes the core, the credentials, custom providers, runs and
// workspaces areas and this one over a temporary data folder, a fake environment (never this machine's), a folder opener that records
// instead of starting the file manager, and a fake workspace activity; the app cases start the built app with --faux, a temporary data
// folder and DataStorage:OpenFolders=false. No provider is called and no key is real (planted values). The cases: sizes, paths and the
// folder walk; the page's facts are this machine's; run history's retention saves and applies at once; runs are counted as History counts
// them (an agent compare run is one run, its records shown apart), in process and on both pages in the app; Remove and Clean up remove
// workspaces (and keep those in use); Open folder takes targets, never paths; the export holds no plaintext secret; an import is reviewed
// item by item and applies only what was kept; the danger zone deletes only what it names, after DELETE (presets and compare workspaces,
// which no action names, survive each one); the archive purge; and in the app: the page in its default state, its API, workspaces, and
// chats exported and deleted (the chat's recorded call, settings, credentials and presets kept).
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Controllers;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.CustomProviders;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.DataStorage;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Presets;
using PiSharp.WebAgent.Runs;

internal static partial class Program
{
    /// <summary>Records the folders Open folder would show.</summary>
    private sealed class DataOpenerFake : IFolderOpener
    {
        public List<string> Opened { get; } = [];

        public string Name => "the test opener";

        public void Open(string folder)
        {
            lock (Opened) Opened.Add(folder);
        }
    }

    /// <summary>Says which runs are in use.</summary>
    private sealed class DataActivityFake : IWorkspaceActivity
    {
        public HashSet<string> Active { get; } = new(StringComparer.Ordinal);

        public bool IsActive(string run) => Active.Contains(run);
    }

    /// <summary>The areas Settings › Data &amp; storage reports on, and the area itself, in process.</summary>
    private sealed class DataHost : IAsyncDisposable
    {
        private readonly ServiceProvider provider;

        public DataHost(string name, IReadOnlyDictionary<string, string>? environment = null, bool openFolders = true, bool presets = false)
        {
            Root = Temp("data-" + name);
            Options = new WebAgentOptions { DataDirectory = Path.Join(Root, "data"), Faux = true };
            Options.EnsureDirectories();
            var variables = environment ?? new Dictionary<string, string>();
            var gitConfig = Path.Join(Root, "gitconfig");
            File.WriteAllText(gitConfig, "");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(new CredentialsOptions
            {
                Environment = variable => variables.TryGetValue(variable, out var value) ? value : null,
                FileExists = _ => false,
                Home = Root,
                Offline = true,
            });
            services.AddSingleton<IFolderOpener>(Opener);
            services.AddSingleton(new DataStorageOptions { OpenFolders = openFolders });
            services.AddSingleton<IWorkspaceActivity>(Activity);
            services.AddSingleton(new WorkspaceManagerOptions
            {
                Root = Path.Join(Options.DataDirectory, WorkspaceManagerOptions.FolderName),
                GitEnvironment = new Dictionary<string, string?> { ["GIT_CONFIG_GLOBAL"] = gitConfig, ["GIT_CONFIG_NOSYSTEM"] = "1" },
            });
            services.AddWebAgentCore(Options);
            services.AddCredentialsArea(Options);
            services.AddCustomProvidersArea(Options);
            services.AddWebAgentFallbacks(Options);
            services.AddRunsArea(Options);
            WorkspacesModule.AddAgentCompareWorkspaces(services, Options);
            if (presets) services.AddPresetsArea(Options);
            services.AddDataStorageArea(Options);
            provider = services.BuildServiceProvider();
            provider.GetRequiredService<WebAgentDb>().Migrate();
        }

        public string Root { get; }

        public WebAgentOptions Options { get; }

        public string Data => Options.DataDirectory;

        public DataOpenerFake Opener { get; } = new();

        public DataActivityFake Activity { get; } = new();

        public IServiceProvider Services => provider;

        public DataStorageService Storage => provider.GetRequiredService<DataStorageService>();

        public DataExport Export => provider.GetRequiredService<DataExport>();

        public ISettingsStore Settings => provider.GetRequiredService<ISettingsStore>();

        public IAccountStore Accounts => provider.GetRequiredService<IAccountStore>();

        public CustomProviderService Custom => provider.GetRequiredService<CustomProviderService>();

        public RunStore Runs => provider.GetRequiredService<RunStore>();

        public WorkspaceManager Workspaces => provider.GetRequiredService<WorkspaceManager>();

        /// <summary>The presets (only with <c>presets: true</c>).</summary>
        public PresetStore Presets => provider.GetRequiredService<PresetStore>();

        /// <summary>A project folder with two files, outside the data folder.</summary>
        public string Project()
        {
            var project = Path.Join(Root, "project");
            Directory.CreateDirectory(Path.Join(project, "src"));
            File.WriteAllText(Path.Join(project, "README.md"), "# Project\n");
            File.WriteAllText(Path.Join(project, "src", "app.cs"), "class App { }\n");
            return project;
        }

        /// <summary>A run's copies, one per slot.</summary>
        public Task<WorkspaceRun> WorkspaceAsync(string run, params string[] slots) =>
            Workspaces.CreateAsync(new WorkspaceRequest { Run = run, ProjectPath = Project(), Slots = WsSlots(slots), Isolation = WorkspaceIsolation.Copy });

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            SqliteClearPools();
            DeleteTemp(Root);
        }
    }

    // A custom provider whose key is written into the file (the import moves it into the vault), with a header reference.
    private static string DataModelsJson(string key) => $$"""
        { "providers": {
            "gateway": { "name": "Gateway", "baseUrl": "https://gw.example.test/v1", "api": "openai-completions", "apiKey": "{{key}}",
                         "headers": { "X-Org": "${ORG_ID}" }, "models": [{ "id": "g1", "name": "G One" }] },
            "openai": { "modelOverrides": { "gpt-4.1": { "contextWindow": 300000 } } } } }
        """;

    private static RunOutcome DataOutcome(DateTimeOffset at, bool raw) => new()
    {
        Status = RunStatus.Completed,
        Finished = at,
        Events = [new RunEventRecord(1, 5, new JsonObject { ["type"] = "start" }), new RunEventRecord(2, 9, new JsonObject { ["type"] = "done" })],
        Raw = raw ? [new RunRawItem(1, "request", 0, "{\"model\":\"echo\",\"messages\":[]}"), new RunRawItem(2, "sse", 4, "data: {}")] : [],
    };

    private static void DataRun(RunStore store, string id, DateTimeOffset created, bool raw = false, bool pinned = false)
    {
        store.Insert(RunsRow(id, created, RunStatus.Running));
        store.Finish(id, DataOutcome(created, raw));
        if (pinned) store.Pin(id, true);
    }

    // History's rows and runs.db's records: a pinned agent compare run with models a and b and two calls each (seven records, one run), a
    // request compare of two columns (two runs in one group) and a playground run. Four runs, ten records.
    private static void DataRunTree(RunStore store, DateTimeOffset at)
    {
        RunRecord Grouping(string id, string? parent) =>
            RunsRow(id, at, kind: RunKind.Parent) with { Source = "agent-compare", ParentId = parent, Provider = "", Model = "" };
        store.Insert(Grouping("cmp_count", null));
        foreach (var slot in new[] { "a", "b" })
        {
            store.Insert(Grouping("cmp_count-" + slot, "cmp_count"));
            for (var call = 1; call <= 2; call++)
                store.Insert(RunsRow($"cmp_count-{slot}-call{call}", at) with { Source = "agent-compare", ParentId = "cmp_count-" + slot });
        }
        Check(store.Pin("cmp_count", true), "the agent compare run is pinned");
        foreach (var column in new[] { "col1", "col2" }) store.Insert(RunsRow(column, at) with { Source = "compare", GroupId = "grp_count" });
        DataRun(store, "solo", at);
    }

    private static string DataIds(RunStore store) =>
        string.Join(",", store.List(new RunQuery { Limit = RunQuery.MaxLimit }).Items.Select(item => item.Id).Order(StringComparer.Ordinal));

    private static string DataJson(object value) => JsonSerializer.Serialize(value, SettingsStore.JsonOptions);

    private static string ArchiveName(string session, DateTimeOffset at) => $"{session}-{at.UtcDateTime:yyyyMMddHHmmss}-{Guid.NewGuid():N}";

    internal static IEnumerable<(string, Func<Task>)> DataStorageCases() =>
    [
        Case("data.folders.sizes-paths-and-the-walk", () =>
        {
            Equal("1 byte", DataFolders.Size(1), "one byte");
            Equal("300 bytes", DataFolders.Size(300), "bytes");
            Equal("1 KB", DataFolders.Size(1024), "a kilobyte");
            Equal("512 KB", DataFolders.Size(512 * 1024), "kilobytes");
            Equal("18.2 MB", DataFolders.Size((long)(18.2 * 1024 * 1024)), "one decimal below 100");
            Equal("212 MB", DataFolders.Size(212L * 1024 * 1024), "none above");
            Equal("1.5 GB", DataFolders.Size(1536L * 1024 * 1024), "gigabytes");

            var root = Temp("data-walk");
            try
            {
                File.WriteAllBytes(Path.Join(root, "a.bin"), new byte[10]);
                Directory.CreateDirectory(Path.Join(root, "sub"));
                File.WriteAllBytes(Path.Join(root, "sub", "b.bin"), new byte[20]);
                File.WriteAllBytes(Path.Join(root, "db.sqlite"), new byte[5]);
                File.WriteAllBytes(Path.Join(root, "db.sqlite-wal"), new byte[7]);
                Equal(42L, DataFolders.Measure(root), "every file");
                Equal(22L, DataFolders.Measure(root, [Path.Join(root, "sub")]), "a folder measured on its own is skipped");
                Equal(12L, DataFolders.DatabaseBytes(Path.Join(root, "db.sqlite")), "a database with its WAL");
                Equal(0L, DataFolders.Measure(Path.Join(root, "missing")), "a missing folder");
                Check(DataFolders.IsWithin(root, Path.Join(root, "sub", "x")), "below");
                Check(DataFolders.IsWithin(root, root + Path.DirectorySeparatorChar), "itself");
                Check(!DataFolders.IsWithin(Path.Join(root, "sub"), Path.Join(root, "subway")), "a sibling with the same prefix is not inside");
                Check(!DataFolders.IsWithin(root, Path.Join(root, "..", "elsewhere")), "a path out of it is not inside");

                // A link inside the folder is not followed, and deleting the folder removes the link, never its target.
                var target = Temp("data-walk-target");
                try
                {
                    File.WriteAllBytes(Path.Join(target, "big.bin"), new byte[1000]);
                    var linked = false;
                    try
                    {
                        Directory.CreateSymbolicLink(Path.Join(root, "sub", "link"), target);
                        linked = true;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* No symlink privilege here: skip. */ }
                    if (linked)
                    {
                        Equal(42L, DataFolders.Measure(root), "the link is not followed");
                        DataFolders.Delete(Path.Join(root, "sub"));
                        Check(!Directory.Exists(Path.Join(root, "sub")), "the folder is gone");
                        Check(File.Exists(Path.Join(target, "big.bin")), "the link's target is untouched");
                    }
                }
                finally { DeleteTemp(target); }
            }
            finally { DeleteTemp(root); }

            if (OperatingSystem.IsWindows())
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                Equal(@"%LOCALAPPDATA%\PiSharpWebAgent\", DataFolders.Display(Path.Join(local, "PiSharpWebAgent"), folder: true), "the mockups' path");
                Equal(@"%LOCALAPPDATA%\PiSharpWebAgent\runs.db", DataFolders.Display(Path.Join(local, "PiSharpWebAgent", "runs.db"), folder: false), "a file");
            }
            Equal(new DateTimeOffset(2026, 10, 1, 12, 30, 5, TimeSpan.Zero), ChatArchiveJanitor.ArchivedAt("01JABC-20261001123005-" + Guid.NewGuid().ToString("N")), "an archived chat's time");
            Equal<DateTimeOffset?>(null, ChatArchiveJanitor.ArchivedAt("notes"), "a folder the device did not name");
            Equal<DateTimeOffset?>(null, ChatArchiveJanitor.ArchivedAt("x-20261301000000-" + Guid.NewGuid().ToString("N")), "not a date");
        }),

        Case("data.facts.are-this-machines", async () =>
        {
            var envKey = Planted("sk-env-");
            var stored = Planted("sk-ant-");
            await using var host = new DataHost("facts", new Dictionary<string, string> { ["OPENAI_API_KEY"] = envKey });
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("anthropic", stored));
            var session = Path.Join(host.Options.DeviceDirectory, "sessions", "s1");
            Directory.CreateDirectory(session);
            File.WriteAllBytes(Path.Join(session, "agent.sqlite"), new byte[1000]);
            var archived = Path.Join(host.Options.DeviceDirectory, "archive", ArchiveName("s0", DateTimeOffset.UtcNow.AddDays(-2)));
            Directory.CreateDirectory(archived);
            File.WriteAllBytes(Path.Join(archived, "agent.sqlite"), new byte[500]);
            var now = DateTimeOffset.UtcNow;
            DataRun(host.Runs, "r1", now.AddHours(-1), raw: true);
            DataRun(host.Runs, "r2", now.AddDays(-40), pinned: true);

            var page = await host.Storage.PageAsync();
            Equal(DataFolders.Measure(host.Data), page.Usage.Total, "the total is the data folder's walk");
            Equal(page.Usage.Total, page.Folder.Bytes, "the data folder's chip");
            Equal(page.Usage.Total, page.Usage.Segments.Sum(segment => segment.Bytes), "the meter's parts add up to it");
            Check(page.Usage.Segments.Any(segment => segment.Label == "run history" && segment.Bytes == host.Runs.SizeBytes()), "run history is runs.db");
            Check(page.Usage.Segments.Any(segment => segment.Label == "chats" && segment.Bytes >= 1500), "chats are the device folder");
            Equal(DataFolderSource.CommandLine, page.Folder.Source, "a folder that is neither the default nor the variable's");
            Equal(1, page.Chats.Archived, "one archived chat");
            Equal(500L, page.Chats.ArchiveBytes, "its size");
            Equal("device" + Path.DirectorySeparatorChar + "archive" + Path.DirectorySeparatorChar, page.Chats.ArchiveDisplay, "shown in the data folder");
            Equal(2L, page.Runs.Count, "two runs");
            Equal(1L, page.Runs.Pinned, "one pinned");
            Equal(1L, page.Runs.RawRuns, "one with raw payloads");
            Check(page.Runs.RawBytes > 0 && page.Runs.EventBytes > 0, "raw payload and event sizes");
            Equal(false, page.Runs.Settings.StoreRawPayloads, "raw payloads are off by default (R0)");
            Equal(30, page.Runs.Settings.RetentionDays, "runs are kept 30 days by default");
            Equal(0L, page.Runs.OlderThan[30], "a pinned run is never past the retention");
            Check(page.Keys.Count >= 1 && page.Keys.ActiveExpires > now, "the key ring has an active key: " + page.Keys.Count);
            Equal(1, page.Credentials.Stored, "one stored credential");
            Equal("anthropic", string.Join(",", page.Credentials.ApiKeys), "by provider");
            Equal("OPENAI_API_KEY", string.Join(",", page.Credentials.Environment), "the environment variable by name");
            Check(page.Existing.Contains(DataOpenTargets.Archive) && page.Existing.Contains(DataOpenTargets.Keys), "the folders that exist");
            Check(!page.Workspaces.Exists || page.Workspaces.Rows.Count == 0, "no workspaces");
            NoSecret(DataJson(page), "the page's facts", envKey, stored);
        }),

        Case("data.history.retention-saves-and-applies", async () =>
        {
            await using var host = new DataHost("history");
            var now = DateTimeOffset.UtcNow;
            DataRun(host.Runs, "old", now.AddDays(-40));
            DataRun(host.Runs, "old-pinned", now.AddDays(-40), pinned: true);
            DataRun(host.Runs, "mid", now.AddDays(-20), raw: true);
            DataRun(host.Runs, "recent", now.AddDays(-2));
            var facts = host.Storage.RunFacts();
            Equal(2L, facts.OlderThan[7], "a 7-day retention would delete two runs now");
            Equal(1L, facts.OlderThan[30], "30 days, one");
            Equal(0L, facts.OlderThan[90], "90 days, none");

            var saved = await host.Storage.SaveHistoryAsync(new RunHistorySettings(7, StoreRawPayloads: true, StoreSseEvents: false));
            Equal(2, saved.RemovedRuns, "saving applies the retention at once");
            Equal(new RunHistorySettings(7, true, false), host.Settings.Get<RunHistorySettings>(), "saved");
            Equal("old-pinned,recent", DataIds(host.Runs), "pinned and recent runs stay");
            Equal(0, (await host.Storage.SaveHistoryAsync(new RunHistorySettings(null))).RemovedRuns, "forever deletes nothing");
            Equal<int?>(null, host.Settings.Get<RunHistorySettings>().RetentionDays, "forever is saved");
            await ThrowsAsync<ArgumentException>(() => host.Storage.SaveHistoryAsync(new RunHistorySettings(0)), "no retention under a day");
            await ThrowsAsync<ArgumentException>(() => host.Storage.SaveHistoryAsync(new RunHistorySettings(RunHistorySettings.MaxRetentionDays + 1)), "nor over ten years");
            Equal<int?>(null, host.Settings.Get<RunHistorySettings>().RetentionDays, "a refused value is not saved");
        }),

        Case("data.runs.counted-as-history-counts-them", async () =>
        {
            await using var host = new DataHost("run-count");
            DataRunTree(host.Runs, DateTimeOffset.UtcNow);
            var history = new RunHistoryPages(host.Services).Page(new RunHistoryFilter());
            Equal(4L, history.TotalAll, "History lists four runs: the agent compare run, two compare columns and a playground run");
            var facts = host.Storage.RunFacts();
            Equal((history.TotalAll, history.PinnedCount), (facts.Count, facts.Pinned), "Settings › Data counts runs, and pinned runs, as History does");
            Equal(10L, facts.Records, "runs.db holds ten records: the agent compare run is seven of them");
            Equal("10 records", facts.RecordsText, "shown apart from the runs");
            var cleared = await host.Storage.ClearHistoryAsync("DELETE");
            Equal(4, cleared.Count, "Clear history deletes the four runs the danger zone named");
            Check(cleared.Message.StartsWith("Deleted 4 runs (10 records) ", StringComparison.Ordinal), "and says so: " + cleared.Message);
            Equal(0L, host.Runs.Count(), "every record is gone");
            facts = host.Storage.RunFacts();
            Equal((0L, 0L), (facts.Count, facts.Records), "nothing left");
            Check(facts.RecordsText is null, "no records line when each record is a run");
        }),

        Case("data.page.counts-runs-as-history-does", async () =>
        {
            var root = Temp("data-app-runs");
            try
            {
                var data = Path.Join(root, "data");
                Directory.CreateDirectory(data);
                DataRunTree(new RunStore(new RunsDb(Path.Join(data, RunsDb.FileName))), DateTimeOffset.UtcNow);
                SqliteClearPools();
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = data, Arguments = ["--" + DataStorageOptions.OpenFoldersKey + "=false"] });
                string RunsChip(string text) =>
                    Regex.Match(text, "data-location=\"runs\".*?<span class=\"ps-chip\">([^<]+)</span>", RegexOptions.Singleline).Groups[1].Value;

                var history = await app.Get("/history");
                Equal(200, history.Status, "History: " + Cut(history.Text));
                var total = Regex.Match(history.Text, "data-run-pagination>.*?Showing [^<]*? of ([0-9,]+) runs<", RegexOptions.Singleline);
                Check(total.Success, "History's count of runs: " + Cut(history.Text));
                Equal("4", total.Groups[1].Value, "History counts four runs");
                Check(history.Text.Contains("All 4 runs and their stream events", StringComparison.Ordinal), "History's own Clear history names four");
                var filtered = Regex.Match((await app.Get("/history?source=compare")).Text, "data-run-count>([0-9,]+) of ([0-9,]+) runs<");
                Equal(("2", "4"), (filtered.Groups[1].Value, filtered.Groups[2].Value), "a filtered History counts its two compare columns of the same four");
                var page = await app.Get(DataController.Route);
                Equal(200, page.Status, "Settings › Data: " + Cut(page.Text));
                Equal("4 runs", RunsChip(page.Text), "Settings › Data's run history line counts the same four");
                Check(page.Text.Contains("in 10 records (an agent compare run keeps one for each model and call)", StringComparison.Ordinal), "the records are shown apart");
                Check(page.Text.Contains("Deletes all 4 runs (10 records),", StringComparison.Ordinal), "the danger zone names the same four");
                Check(page.Text.Contains("data-confirm-body=\"All 4 runs (10 records),", StringComparison.Ordinal), "and so does its confirmation");
                var facts = (await app.Get("/api/data/facts")).Body!["runs"]!;
                Equal((4L, 10L, 1L), (facts["count"]!.GetValue<long>(), facts["records"]!.GetValue<long>(), facts["pinned"]!.GetValue<long>()), "the facts as JSON");

                var cleared = await app.SendAsync(HttpMethod.Post, "/api/data/danger/history", new JsonObject { ["confirm"] = "DELETE" }, origin: app.Origin);
                Equal(200, cleared.Status, "cleared: " + cleared.Text);
                Equal(4, cleared.Body!["count"]!.GetValue<int>(), "the four runs it named");
                page = await app.Get(DataController.Route);
                Equal("0 runs", RunsChip(page.Text), "none left");
                Check(Regex.IsMatch(page.Text, "<button[^>]*data-danger=\"history\"[^>]*disabled=\"disabled\""), "nothing more to clear");
            }
            finally
            {
                DeleteTemp(root);
            }
        }),

        Case("data.workspaces.remove-and-clean-up-remove-workspaces", async () =>
        {
            await using var host = new DataHost("workspaces");
            var one = await host.WorkspaceAsync("cmp_one", "a", "b");
            var two = await host.WorkspaceAsync("cmp_two", "a");
            var facts = await host.Storage.WorkspaceFactsAsync();
            Equal(3, facts.Rows.Count, "one row per workspace");
            Check(facts.Bytes > 0 && facts.Rows.All(row => row.Bytes > 0), "measured");
            Check(facts.Rows.All(row => row.Busy is null && !row.RunKnown && row.Kind == "temporary copy"), "copies not in use, unknown to Compare");
            Equal("model A", facts.Rows.First(row => row.Run == "cmp_one" && row.Slot == "a").Model, "a slot without a model");
            Check(facts.CleanupBody.Contains("cmp_one · a", StringComparison.Ordinal), "Clean up names what it removes");
            var row = facts.Rows.First(item => item.Name == "cmp_one · a");
            Check(row.RemoveBody.Contains("not applied", StringComparison.Ordinal) && row.RemoveDetail.StartsWith("folder delete", StringComparison.Ordinal), "Remove says what goes");

            var removed = await host.Storage.RemoveWorkspaceAsync("cmp_one", "a");
            Equal("cmp_one · a", string.Join(",", removed.Removed), "one workspace");
            Check(removed.Freed > 0, "freed");
            Check(!Directory.Exists(one.Slots[0].Path) && Directory.Exists(one.Slots[1].Path), "only that workspace is gone");
            await ThrowsAsync<KeyNotFoundException>(() => host.Storage.RemoveWorkspaceAsync("cmp_one", "a"), "removing it again");
            await ThrowsAsync<ArgumentException>(() => host.Storage.RemoveWorkspaceAsync("..", null), "not a run name");
            await ThrowsAsync<ArgumentException>(() => host.Storage.RemoveWorkspaceAsync("cmp_one", "../b"), "not a slot name");

            host.Activity.Active.Add("cmp_two");
            await ThrowsAsync<InvalidOperationException>(() => host.Storage.RemoveWorkspaceAsync("cmp_two", "a"), "a run in use keeps its workspaces");
            Equal("in use", (await host.Storage.WorkspaceFactsAsync()).Rows.Single(item => item.Run == "cmp_two").Busy, "and says so");
            var cleaned = await host.Storage.CleanUpWorkspacesAsync();
            Equal("cmp_one · b", string.Join(",", cleaned.Removed), "Clean up removes what is not in use");
            Equal("cmp_two · a: in use", string.Join(",", cleaned.Kept), "and keeps the rest, with why");
            Check(!Directory.Exists(one.Directory) && Directory.Exists(two.Slots[0].Path), "the first run's folder went with its last workspace");

            host.Activity.Active.Clear();
            cleaned = await host.Storage.CleanUpWorkspacesAsync();
            Equal("cmp_two · a", string.Join(",", cleaned.Removed), "then the last one");
            Equal(0, (await host.Storage.WorkspaceFactsAsync()).Rows.Count, "none left");
            Check(File.Exists(Path.Join(one.ProjectPath, "src", "app.cs")), "the project is untouched");
        }),

        Case("data.open.takes-targets-never-paths", async () =>
        {
            await using var host = new DataHost("open");
            Equal(DataFolders.Display(host.Data, folder: true), await host.Storage.OpenAsync(DataOpenTargets.Data, null, null), "the data folder");
            Equal(Path.GetFullPath(host.Data), host.Opener.Opened[^1], "opened");
            await host.Storage.OpenAsync(DataOpenTargets.Keys, null, null);
            Equal(Path.GetFullPath(host.Options.KeysDirectory), host.Opener.Opened[^1], "the key ring");
            await ThrowsAsync<KeyNotFoundException>(() => host.Storage.OpenAsync(DataOpenTargets.Archive, null, null), "no archive yet");
            await ThrowsAsync<ArgumentException>(() => host.Storage.OpenAsync(@"C:\Windows", null, null), "a path is not a target");
            await ThrowsAsync<ArgumentException>(() => host.Storage.OpenAsync(null, null, null), "no target");
            await ThrowsAsync<ArgumentException>(() => host.Storage.OpenAsync(DataOpenTargets.Workspace, "..", "a"), "a workspace needs a run name");
            await ThrowsAsync<KeyNotFoundException>(() => host.Storage.OpenAsync(DataOpenTargets.Workspace, "cmp_none", "a"), "an unknown workspace");
            var run = await host.WorkspaceAsync("cmp_open", "a");
            await host.Storage.OpenAsync(DataOpenTargets.Workspace, "cmp_open", "a");
            Equal(Path.GetFullPath(run.Slots[0].Path), host.Opener.Opened[^1], "a workspace's folder");
            Equal(3, host.Opener.Opened.Count, "nothing else was opened");

            await using var off = new DataHost("open-off", openFolders: false);
            await ThrowsAsync<InvalidOperationException>(() => off.Storage.OpenAsync(DataOpenTargets.Data, null, null), "turned off");
            Equal(0, off.Opener.Opened.Count, "and nothing opened");
            Check((await off.Storage.PageAsync()).Open(DataOpenTargets.Data).Disabled is not null, "the button says so");
        }),

        Case("data.export.holds-no-plaintext-secret", async () =>
        {
            var envKey = Planted("sk-env-");
            var stored = Planted("sk-ant-");
            var literal = Planted("sk-gw-");
            var handWritten = Planted("sk-hand-");
            await using var host = new DataHost("export", new Dictionary<string, string> { ["OPENAI_API_KEY"] = envKey });
            Equal(0, JsonNode.Parse(host.Export.Build().Text)!["settings"]!.AsObject().Count, "a fresh app exports no settings key");
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("anthropic", stored));
            await host.Custom.ImportAsync(DataModelsJson(literal));
            await host.Settings.SaveAsync(new ToolsSettings(host.Root, ToolApprovalMode.AllowList, [new ToolAllowRule("bash", "dotnet test *")]));
            await host.Settings.SaveAsync(new RunHistorySettings(90));

            var file = host.Export.Build();
            var json = JsonNode.Parse(file.Text)!.AsObject();
            Equal(DataExport.Format, json["format"]!.GetValue<string>(), "the format");
            Equal(DataExport.Version, json["version"]!.GetValue<int>(), "the version");
            Check(json["note"]!.GetValue<string>().Contains("No credential", StringComparison.Ordinal), "the file says no credential is in it");
            Check(Regex.IsMatch(file.FileName, @"^pisharp-webagent-settings-\d{4}-\d{2}-\d{2}\.json$"), "the file name: " + file.FileName);
            Equal("allowList", json["settings"]!["tools"]!["approvalMode"]!.GetValue<string>(), "a changed setting");
            Equal(90, json["settings"]!["history"]!["retentionDays"]!.GetValue<int>(), "run history");
            Check(json["settings"]!["history"]!["storeRawPayloads"] is null, "a key at its default is not exported");
            Check(json["settings"]!["setup"] is null, "the setup wizard's state stays on this machine");
            Equal("${ORG_ID}", json["customProviders"]!["gateway"]!["headers"]!["X-Org"]!.GetValue<string>(), "a header reference is kept");
            Check(json["customProviders"]!["gateway"]!["apiKey"] is null, "the provider's key is not in the file");
            Check(json["customProviders"]!["openai"]!["modelOverrides"] is not null, "built-in overrides are");
            Check(json["credentials"] is null && json["accounts"] is null, "no credentials part");
            Equal("settings,custom providers", string.Join(",", file.Parts), "what it holds");

            // A key written into models.json by hand (the app never writes one) is left out too.
            var text = File.ReadAllText(host.Custom.Store.Path).Replace("\"name\": \"Gateway\"", $"\"name\": \"Gateway\", \"apiKey\": \"{handWritten}\"", StringComparison.Ordinal);
            Check(text.Contains(handWritten, StringComparison.Ordinal), "the hand-written key is in the file");
            File.WriteAllText(host.Custom.Store.Path, text);
            host.Custom.Store.Refresh();
            file = host.Export.Build();
            NoSecret(file.Text, "the export", envKey, stored, literal, handWritten);
            NoSecret(DataJson(await host.Storage.PageAsync()), "the page's facts", envKey, stored, literal, handWritten);
            NoSecret(DataJson(host.Export.Plan(JsonNode.Parse(file.Text))), "the import review", envKey, stored, literal, handWritten);
        }),

        Case("data.import.reviews-every-change-and-applies-only-what-was-kept", async () =>
        {
            var literal = Planted("sk-gw-");
            string text;
            await using (var source = new DataHost("import-source"))
            {
                await source.Custom.ImportAsync(DataModelsJson(literal));
                await source.Settings.SaveAsync(new ToolsSettings(source.Root, ToolApprovalMode.AllowList, [new ToolAllowRule("bash", "git status")]));
                await source.Settings.SaveAsync(new RunHistorySettings(90, StoreRawPayloads: true));
                text = source.Export.Build().Text;
            }
            await using var host = new DataHost("import");
            var file = JsonNode.Parse(text);
            var plan = host.Export.Plan(file);
            Equal("customProviders/gateway,customProviders/openai,settings/history,settings/tools", string.Join(",", plan.Items.Select(item => item.Id).Order(StringComparer.Ordinal)),
                "each changed section and provider is an item");
            var history = plan.Items.Single(item => item.Id == "settings/history");
            Equal("retentionDays: 30 -> 90 | storeRawPayloads: false -> true", string.Join(" | ", history.Changes.Select(change => $"{change.Key}: {change.Before} -> {change.After}")),
                "every changed key with this machine's value and the file's");
            Check(plan.Items.All(item => item.Problem is null), "nothing refused: " + string.Join(" | ", plan.Items.Select(item => item.Problem)));
            Check(plan.Notes.Any(note => note.Contains("no folder", StringComparison.Ordinal)), "the source's working folder (gone with it) is left out: " + string.Join(" | ", plan.Notes));
            NoSecret(DataJson(plan), "the review", literal);

            var result = await host.Export.ApplyAsync(file, ["settings/history", "customProviders/gateway"]);
            Equal("customProviders/gateway,settings/history", string.Join(",", result.Applied.Order(StringComparer.Ordinal)), "only the kept items");
            Equal(new RunHistorySettings(90, true), host.Settings.Get<RunHistorySettings>(), "run history imported");
            Equal(ToolApprovalMode.Ask, host.Settings.Get<ToolsSettings>().ApprovalMode, "Tools left as it was");
            Check(host.Custom.Current.Find("gateway") is not null, "the custom provider imported");
            Check(host.Custom.Current.Overrides.Count == 0, "the built-in overrides left out");
            Check(host.Accounts.List().All(account => account.ReadOnly), "an import brings no credential");
            Equal(0, host.Export.Plan(file).Items.Count(item => item.Id is "settings/history" or "customProviders/gateway"), "imported items are now unchanged");
            await ThrowsAsync<ArgumentException>(() => host.Export.ApplyAsync(file, ["settings/nope"]), "an id the file does not change");

            // What this app does not import is listed, never applied.
            var odd = new JsonObject
            {
                ["format"] = DataExport.Format,
                ["version"] = 1,
                ["credentials"] = new JsonObject { ["openai"] = Planted("sk-") },
                ["settings"] = new JsonObject
                {
                    ["nope"] = new JsonObject(),
                    ["setup"] = new JsonObject { ["completed"] = true },
                    ["tools"] = new JsonObject { ["workingDirectory"] = Path.Join(host.Root, "no", "such", "folder"), ["approvalMode"] = "auto" },
                    ["history"] = new JsonObject { ["retentionDays"] = 0 },
                },
            };
            plan = host.Export.Plan(odd);
            Check(plan.Notes.Any(note => note.Contains("“credentials”", StringComparison.Ordinal)), "a credentials part is left out: " + string.Join(" | ", plan.Notes));
            Check(plan.Notes.Any(note => note.Contains("“nope”", StringComparison.Ordinal)), "an unknown section is left out");
            Check(plan.Notes.Any(note => note.Contains("Setup wizard", StringComparison.Ordinal)), "the setup wizard's state stays");
            Check(plan.Notes.Any(note => note.Contains("no folder", StringComparison.Ordinal)), "a folder this machine doesn't have stays as it is");
            Equal("approvalMode", string.Join(",", plan.Items.Single(item => item.Id == "settings/tools").Changes.Select(change => change.Key)), "the rest of Tools is imported");
            var refused = plan.Items.Single(item => item.Id == "settings/history");
            Check(refused.Problem is not null, "an invalid value is refused with why");
            result = await host.Export.ApplyAsync(odd, ["settings/history", "settings/tools"]);
            Equal("settings/tools", string.Join(",", result.Applied), "the valid item is imported");
            Equal("settings/history", string.Join(",", result.Failed.Select(item => item.Id)), "the invalid one is not");
            Equal(ToolApprovalMode.Auto, host.Settings.Get<ToolsSettings>().ApprovalMode, "approval mode imported");
            Equal(90, host.Settings.Get<RunHistorySettings>().RetentionDays, "run history kept");

            Throws<ArgumentException>(() => host.Export.Plan(new JsonObject { ["format"] = "something-else", ["version"] = 1 }), "another format");
            Throws<ArgumentException>(() => host.Export.Plan(new JsonObject { ["format"] = DataExport.Format, ["version"] = DataExport.Version + 1 }), "a newer version");
            Throws<ArgumentException>(() => host.Export.Plan(JsonNode.Parse("[1,2]")), "not an object");
        }),

        Case("data.danger.deletes-only-what-it-names", async () =>
        {
            var envKey = Planted("sk-env-");
            var stored = Planted("sk-ant-");
            var literal = Planted("sk-gw-");
            await using var host = new DataHost("danger", new Dictionary<string, string> { ["OPENAI_API_KEY"] = envKey }, presets: true);
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("anthropic", stored));
            await host.Custom.ImportAsync(DataModelsJson(literal));
            await host.Settings.SaveAsync(new RunHistorySettings(90));
            await host.Settings.SaveAsync(new ToolsSettings(host.Root, ToolApprovalMode.AllowList, []));
            DataRun(host.Runs, "r1", DateTimeOffset.UtcNow.AddDays(-1), raw: true);
            DataRun(host.Runs, "r2", DateTimeOffset.UtcNow.AddDays(-50), pinned: true);
            int Stored() => host.Accounts.List().Count(account => !account.ReadOnly);
            Equal(2, Stored(), "an API key and the custom provider's key");
            var keyFiles = Directory.GetFiles(host.Options.KeysDirectory).Length;
            // What no action names: a preset (a record, not a setting, so no IDataResetPart resets it) and an agent compare workspace.
            var preset = host.Presets.Add(new Preset { Name = "Keep me", Kind = PresetPages.Playground, Provider = "faux", Model = "echo" });
            var workspace = await host.WorkspaceAsync("cmp_keep", "a");
            async Task UnnamedKeptAsync(string after)
            {
                Equal((1, preset.Name), (host.Presets.Count(), host.Presets.Get(preset.Id)?.Name), after + " keeps the preset");
                Check(workspace.Slots.Count == 1 && workspace.Slots.All(slot => Directory.Exists(slot.Path)), after + " keeps the compare workspace's folder");
                Equal(1, (await host.Workspaces.GetAsync("cmp_keep"))?.Slots.Count(slot => slot.State == WorkspaceSlotState.Ready), after + " keeps the workspace in the manager");
                Equal(1, (await host.Storage.PageAsync()).Workspaces.Rows.Count, after + ": Settings › Data still lists the workspace");
            }
            await UnnamedKeptAsync("the start");

            foreach (var typed in new[] { null, "", "delete", "DELETE ", "Delete" })
            {
                await ThrowsAsync<ArgumentException>(() => host.Storage.DeleteCredentialsAsync(typed), "credentials need DELETE: " + typed);
                await ThrowsAsync<ArgumentException>(() => host.Storage.ClearHistoryAsync(typed), "history needs DELETE");
                await ThrowsAsync<ArgumentException>(() => host.Storage.DeleteChatsAsync(typed), "chats need DELETE");
                await ThrowsAsync<ArgumentException>(() => host.Storage.ResetSettingsAsync(typed), "settings need DELETE");
            }
            Equal((2, 2L, 90), (Stored(), host.Runs.Count(), host.Settings.Get<RunHistorySettings>().RetentionDays!.Value), "nothing changed without DELETE");
            await UnnamedKeptAsync("an action without DELETE");

            var credentials = await host.Storage.DeleteCredentialsAsync("DELETE");
            Equal(2, credentials.Count, "both stored credentials");
            Equal(0, Stored(), "none left");
            Check(host.Accounts.List().Any(account => account.Kind == AccountKind.Environment && account.Source == "OPENAI_API_KEY"), "the environment variable still works");
            Check(credentials.Message.Contains("environment variable", StringComparison.Ordinal), "and the result says so: " + credentials.Message);
            Equal((2L, 90), (host.Runs.Count(), host.Settings.Get<RunHistorySettings>().RetentionDays!.Value), "history and settings kept");
            Check(host.Custom.Current.Find("gateway") is not null, "custom providers kept");
            Equal(keyFiles, Directory.GetFiles(host.Options.KeysDirectory).Length, "the key ring kept");
            await UnnamedKeptAsync("Delete credentials");

            var again = Planted("sk-ant-");
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("anthropic", again));
            var history = await host.Storage.ClearHistoryAsync("DELETE");
            Equal(2, history.Count, "every run, the pinned one too");
            Equal(0L, host.Runs.Count(), "runs.db is empty");
            Equal((1, 90), (Stored(), host.Settings.Get<RunHistorySettings>().RetentionDays!.Value), "credentials and settings kept");
            Equal(0L, ReadDb(host.Runs.Db.Path, "SELECT COUNT(*) FROM run_raw;", reader => reader.GetInt64(0))[0], "raw payloads gone");
            await UnnamedKeptAsync("Clear history");

            DataRun(host.Runs, "r3", DateTimeOffset.UtcNow);
            var reset = await host.Storage.ResetSettingsAsync("DELETE");
            Equal(RunHistorySettings.CreateDefault(), host.Settings.Get<RunHistorySettings>(), "run history back to its defaults");
            Equal(ToolApprovalMode.Ask, host.Settings.Get<ToolsSettings>().ApprovalMode, "Tools back to its defaults");
            Equal(0, host.Custom.Current.Providers.Count + host.Custom.Current.Overrides.Count, "custom providers reset");
            Check(File.Exists(host.Custom.Store.BackupPath), "models.json kept as models.json.bak");
            Equal((1, 1L), (Stored(), host.Runs.Count()), "credentials and history kept");
            await UnnamedKeptAsync("Reset settings");
            var backup = host.Storage.Backups().Single();
            Check(reset.Message.Contains(Path.GetFileName(backup), StringComparison.Ordinal), "the result names the copy");
            var copy = JsonNode.Parse(File.ReadAllText(backup));
            DataExport.Read(copy);
            Equal(90, copy!["settings"]!["history"]!["retentionDays"]!.GetValue<int>(), "the copy holds the old settings");
            Check(host.Export.Plan(copy).Items.Any(item => item.Id == "settings/history"), "and can be imported to undo");
            await ThrowsAsync<InvalidOperationException>(() => host.Storage.DeleteChatsAsync("DELETE"), "no chats area here");
            foreach (var secret in new[] { envKey, stored, literal, again })
                Check(FilesHolding(host.Data, secret).Count == 0, "a planted key is on disk in plain text: " + string.Join(", ", FilesHolding(host.Data, secret)));
        }),

        Case("data.archive.purges-deleted-chats-after-30-days", async () =>
        {
            await using var host = new DataHost("archive");
            var archive = Path.Join(host.Options.DeviceDirectory, "archive");
            var old = Path.Join(archive, ArchiveName("s-old", DateTimeOffset.UtcNow.AddDays(-31)));
            var recent = Path.Join(archive, ArchiveName("s-new", DateTimeOffset.UtcNow.AddDays(-29)));
            var other = Path.Join(archive, "notes");
            foreach (var folder in new[] { old, recent, other })
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Join(folder, "agent.sqlite"), "x");
            }
            File.SetAttributes(Path.Join(old, "agent.sqlite"), FileAttributes.ReadOnly);
            var janitor = host.Services.GetRequiredService<ChatArchiveJanitor>();
            Equal(1, janitor.PurgeNow(), "one chat is past 30 days");
            Check(!Directory.Exists(old), "it is purged (read-only files too)");
            Check(Directory.Exists(recent) && Directory.Exists(other), "a newer chat and a folder the device did not name stay");
            Equal(0, janitor.PurgeNow(), "nothing more");
            Equal(2, host.Storage.ArchivedCount(), "the page counts what is left");
        }),

        Case("data.page.renders-the-default-state-and-its-api", async () =>
        {
            var root = Temp("data-app");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart
                {
                    DataDirectory = Path.Join(root, "data"),
                    Arguments = ["--" + DataStorageOptions.OpenFoldersKey + "=false"],
                });
                var page = await app.Get(DataController.Route);
                Equal(200, page.Status, "the page: " + Cut(page.Text));
                CheckShell(DataController.Route, page.Text);
                Check(page.Text.Contains("<title>Data &amp; storage", StringComparison.Ordinal), "its title");
                Check(!page.Text.Contains("Coming in a later milestone", StringComparison.Ordinal), "no placeholder");
                Check(string.Join("", page.Response.Headers.CacheControl?.ToString() ?? "").Contains("no-store", StringComparison.Ordinal), "no-store");
                foreach (var id in new[] { "locations", "chats", "compare-workspaces", "run-history", "export-import", "danger-zone" })
                    Check(page.Text.Contains($"id=\"{id}\"", StringComparison.Ordinal), "section " + id);
                foreach (var part in new[] { "data-usage", "data-location=\"data\"", "data-location=\"chats\"", "data-location=\"runs\"", "data-location=\"keys\"",
                             "data-location=\"credentials\"", "data-archive", "data-pane=\"ac-ws-empty\"", "data-history-form", "data-savebar hidden",
                             "data-import-file", "id=\"modal-data-confirm\"", "data-confirm-input", "id=\"modal-data-import\"", "Credentials are always excluded",
                             "Set with <span class=\"ps-mono\">--data</span>", "Data Protection key ring", "/js/data.js" })
                    Check(page.Text.Contains(part, StringComparison.Ordinal), "the page has " + part);
                foreach (var action in DataEndpoints.DangerActions)
                    Check(Regex.IsMatch(page.Text, $"<button[^>]*data-danger=\"{action}\"[^>]*disabled=\"disabled\""), "a fresh app has nothing to delete: " + action);
                // The default state of the dirty tracking: no row modified, no retention note, the save bar hidden (data.js shows them).
                Equal(3, Regex.Matches(page.Text, "data-modified-chip hidden").Count, "no row is modified");
                Check(page.Text.Contains("data-retention-note hidden", StringComparison.Ordinal), "no retention note");
                Equal(200, (await app.Get("/js/data.js")).Status, "data.js is served");
                var raw = Regex.Match(page.Text, "<input[^>]*id=\"storeRaw\"[^>]*>").Value;
                Check(raw.Length > 0 && !raw.Contains("checked", StringComparison.Ordinal), "raw payloads are off by default: " + raw);
                Check(Regex.IsMatch(page.Text, "<option value=\"30\" selected=\"selected\""), "30 days is chosen");
                Check(Regex.IsMatch(page.Text, "data-open-folder=\"data\"[^>]*disabled"), "Open folder is turned off here");

                Equal(409, (await app.SendAsync(HttpMethod.Post, "/api/data/open", new JsonObject { ["target"] = "data" }, origin: app.Origin)).Status, "opening is turned off");
                Equal(403, (await app.SendAsync(HttpMethod.Post, "/api/data/history", new JsonObject { ["retentionDays"] = 7 }, origin: "http://evil.example")).Status, "cross-origin");
                Equal(415, (await app.SendAsync(HttpMethod.Post, "/api/data/history", text: "retentionDays=7", contentType: "application/x-www-form-urlencoded", origin: app.Origin)).Status, "JSON only");
                Equal(400, (await app.SendAsync(HttpMethod.Post, "/api/data/history", new JsonObject { ["retentionDays"] = 0 }, origin: app.Origin)).Status, "a refused retention");
                var saved = await app.SendAsync(HttpMethod.Post, "/api/data/history",
                    new JsonObject { ["retentionDays"] = 7, ["storeRawPayloads"] = false, ["storeSseEvents"] = true }, origin: app.Origin);
                Equal(200, saved.Status, "saved: " + saved.Text);
                Equal(7, saved.Body!["settings"]!["retentionDays"]!.GetValue<int>(), "the saved retention");
                Check(Regex.IsMatch((await app.Get(DataController.Route)).Text, "<option value=\"7\" selected=\"selected\""), "the page shows it");

                Equal(400, (await app.SendAsync(HttpMethod.Post, "/api/data/danger/history", new JsonObject { ["confirm"] = "delete" }, origin: app.Origin)).Status, "DELETE typed exactly");
                Equal(404, (await app.SendAsync(HttpMethod.Post, "/api/data/danger/everything", new JsonObject { ["confirm"] = "DELETE" }, origin: app.Origin)).Status, "an unknown action");

                var export = await app.Get("/api/data/export");
                Equal(200, export.Status, "the export");
                Check(export.Response.Content.Headers.ContentDisposition?.FileName?.Trim('"').StartsWith("pisharp-webagent-settings-", StringComparison.Ordinal) == true, "a download");
                Equal(7, export.Body!["settings"]!["history"]!["retentionDays"]!.GetValue<int>(), "with the saved retention");
                var preview = await app.SendAsync(HttpMethod.Post, "/api/data/import/preview", new JsonObject { ["file"] = export.Body.DeepClone() }, origin: app.Origin);
                Equal(200, preview.Status, "the review: " + preview.Text);
                Equal(0, preview.Body!["items"]!.AsArray().Count, "this machine has everything in its own export");
                var applied = await app.SendAsync(HttpMethod.Post, "/api/data/import", new JsonObject { ["file"] = export.Body.DeepClone(), ["accepted"] = new JsonArray() }, origin: app.Origin);
                Equal(400, applied.Status, "an import needs a kept change");
                Equal(400, (await app.SendAsync(HttpMethod.Post, "/api/data/import/preview", new JsonObject { ["file"] = new JsonObject { ["format"] = "x" } }, origin: app.Origin)).Status,
                    "not a settings export");
                var facts = await app.Get("/api/data/facts");
                Equal(200, facts.Status, "the facts");
                Equal(7, facts.Body!["runs"]!["settings"]!["retentionDays"]!.GetValue<int>(), "as JSON");
                Equal(404, (await app.Get("/api/data/export/chats")).Status, "no chats to export");
            }
            finally
            {
                DeleteTemp(root);
            }
        }),

        Case("data.page.workspaces-remove-and-clean-up-in-the-app", async () =>
        {
            var root = Temp("data-app-ws");
            try
            {
                var data = Path.Join(root, "data");
                var project = Path.Join(root, "project");
                Directory.CreateDirectory(project);
                File.WriteAllText(Path.Join(project, "a.txt"), "a\n");
                var config = Path.Join(root, "gitconfig");
                File.WriteAllText(config, "");
                var manager = new WorkspaceManager(new WorkspaceManagerOptions
                {
                    Root = Path.Join(data, WorkspaceManagerOptions.FolderName),
                    GitEnvironment = new Dictionary<string, string?> { ["GIT_CONFIG_GLOBAL"] = config, ["GIT_CONFIG_NOSYSTEM"] = "1" },
                });
                var one = await manager.CreateAsync(new WorkspaceRequest { Run = "cmp_app1", ProjectPath = project, Slots = WsSlots("a", "b"), Isolation = WorkspaceIsolation.Copy });
                await manager.CreateAsync(new WorkspaceRequest { Run = "cmp_app2", ProjectPath = project, Slots = WsSlots("a"), Isolation = WorkspaceIsolation.Copy });
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = data, Arguments = ["--" + DataStorageOptions.OpenFoldersKey + "=false"] });
                var page = (await app.Get(DataController.Route)).Text;
                Check(page.Contains("data-pane=\"ac-ws-list\"", StringComparison.Ordinal), "the list pane");
                Equal(3, Regex.Matches(page, "data-ws-row ").Count, "one row per workspace");
                Check(page.Contains("data-ws-cleanup", StringComparison.Ordinal), "Clean up with more than one workspace");
                Check(page.Contains(Html("cmp_app1 · a"), StringComparison.Ordinal) && page.Contains("temporary copy", StringComparison.Ordinal), "named, with its kind");
                CheckShell(DataController.Route, page);

                var removed = await app.SendAsync(HttpMethod.Post, "/api/data/workspaces/remove", new JsonObject { ["run"] = "cmp_app1", ["slot"] = "a" }, origin: app.Origin);
                Equal(200, removed.Status, "Remove: " + removed.Text);
                Check(!Directory.Exists(one.Slots[0].Path) && Directory.Exists(one.Slots[1].Path), "that workspace only");
                Equal(404, (await app.SendAsync(HttpMethod.Post, "/api/data/workspaces/remove", new JsonObject { ["run"] = "cmp_app1", ["slot"] = "a" }, origin: app.Origin)).Status, "gone");
                Equal(400, (await app.SendAsync(HttpMethod.Post, "/api/data/workspaces/remove", new JsonObject { ["run"] = "../x" }, origin: app.Origin)).Status, "not a run");
                var cleaned = await app.SendAsync(HttpMethod.Post, "/api/data/workspaces/cleanup", new JsonObject(), origin: app.Origin);
                Equal(200, cleaned.Status, "Clean up: " + cleaned.Text);
                Equal(2, cleaned.Body!["removed"]!.AsArray().Count, "the other two");
                page = (await app.Get(DataController.Route)).Text;
                Check(page.Contains("data-pane=\"ac-ws-empty\"", StringComparison.Ordinal) && !page.Contains("data-ws-cleanup", StringComparison.Ordinal), "the empty pane");
                Check(File.Exists(Path.Join(project, "a.txt")), "the project is untouched");
            }
            finally
            {
                DeleteTemp(root);
            }
        }),

        Case("data.chats.export-all-and-delete-all", async () =>
        {
            await using var chat = await ChatApp.StartAsync("data-chats");
            var id = await chat.CreateAsync("Remember the data page", "req-data-1");
            // "running" can still read false right after the create, so wait for the answer itself and then for its recorded call.
            await chat.WaitForAsync(id, "Echo: Remember the data page");
            var runs = Path.Join(chat.Data, RunsDb.FileName);
            long RunRows() => ReadDb(runs, "SELECT COUNT(*) FROM runs;", reader => reader.GetInt64(0))[0];
            long OpenRows() => ReadDb(runs, "SELECT COUNT(*) FROM runs WHERE status = 'running';", reader => reader.GetInt64(0))[0];
            await WaitUntilAsync(() => Task.FromResult(File.Exists(runs) && RunRows() >= 1 && OpenRows() == 0), "the chat's call is recorded and ended",
                TimeSpan.FromSeconds(30));
            var before = RunRows();
            Check(before >= 1, "the chat's model call is recorded in run history, so keeping it means something: " + before);
            var page = (await chat.App.Get(DataController.Route)).Text;
            Check(page.Contains("1 chat", StringComparison.Ordinal), "the page counts the chat");

            // What Delete chats does not name: a setting, a stored credential and a preset.
            var key = Planted("sk-ant-");
            Equal(200, (await chat.PostAsync("/api/data/history", new JsonObject { ["retentionDays"] = 90 })).Status, "a setting saved");
            var account = await chat.PostAsync("/api/credentials/accounts", new JsonObject { ["provider"] = "anthropic", ["apiKey"] = key, ["label"] = "Kept key" });
            Equal(200, account.Status, "a credential stored: " + Cut(account.Text));
            var preset = await chat.PostAsync("/api/presets", new JsonObject
            {
                ["page"] = "presets", ["capture"] = new JsonObject { ["provider"] = "faux", ["model"] = "echo", ["systemPrompt"] = "Be kind." }, ["name"] = "Kept preset",
            });
            Equal(200, preset.Status, "a preset saved: " + Cut(preset.Text));
            async Task UnnamedKeptAsync(string when)
            {
                var facts = (await chat.App.Get("/api/data/facts")).Body!;
                Equal(90, facts["runs"]!["settings"]!["retentionDays"]!.GetValue<int>(), when + ": the setting is kept");
                Equal(1, facts["credentials"]!["stored"]!.GetValue<int>(), when + ": the credential is kept");
                var presets = (await chat.App.Get("/api/presets")).Body!["presets"]!.AsArray();
                Equal("Kept preset", string.Join(",", presets.Select(item => item!["name"]!.GetValue<string>())), when + ": the preset is kept");
            }
            await UnnamedKeptAsync("before");

            var zip = await chat.App.Http.GetAsync(new Uri(chat.App.Base, "/api/data/export/chats"));
            Equal(System.Net.HttpStatusCode.OK, zip.StatusCode, "the zip");
            await using (var stream = await zip.Content.ReadAsStreamAsync())
            using (var zipped = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var entry = zipped.Entries.Single(item => item.Name.EndsWith(".md", StringComparison.Ordinal));
                using var reader = new StreamReader(entry.Open());
                Check((await reader.ReadToEndAsync()).Contains("Remember the data page", StringComparison.Ordinal), "a Markdown transcript of the chat");
            }

            Equal(400, (await chat.PostAsync("/api/data/danger/chats", new JsonObject { ["confirm"] = "nope" })).Status, "DELETE first");
            var deleted = await chat.PostAsync("/api/data/danger/chats", new JsonObject { ["confirm"] = "DELETE" });
            Equal(200, deleted.Status, "deleted: " + deleted.Text);
            Equal(1, deleted.Body!["count"]!.GetValue<int>(), "one chat");
            var archivedAt = ReadDb(Path.Join(chat.Data, "webagent.db"), "SELECT archived_at FROM chats WHERE session_id = '" + id + "';", reader => reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0));
            Check(archivedAt.Single() is not null, "the chat is archived");
            var archive = Path.Join(chat.Data, "device", "archive");
            Equal(1, Directory.GetDirectories(archive).Length, "its session is in the archive");
            Check(ChatArchiveJanitor.ArchivedAt(Path.GetFileName(Directory.GetDirectories(archive)[0])) is not null, "named so the purge can date it");
            Equal(before, RunRows(), "run history is kept");
            await UnnamedKeptAsync("Delete chats");
            Check(!(await chat.App.Get("/api/data/facts")).Text.Contains(key, StringComparison.Ordinal), "the facts never hold the key");
            page = (await chat.App.Get(DataController.Route)).Text;
            Check(page.Contains("0 chats", StringComparison.Ordinal) && page.Contains("1 archived", StringComparison.Ordinal), "the page shows the archive");
        }),
    ];
}
