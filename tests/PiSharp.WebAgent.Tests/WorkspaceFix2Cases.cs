using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.WebAgent.AgentCompare.Pages;
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Spend;

// Decision 0011 (run W4, fix 2 workspaces): AgentCompareSettings.UseGitignore is applied, and a discarded column keeps its patch.
// - The switch reaches the workspace manager (WorkspaceInspectOptions / WorkspaceRequest.UseGitignore, recorded in the run's manifest): off,
//   a temporary copy and its diff include what .gitignore lists, while bin, obj, node_modules, .vs, .git and the extra excludes stay out;
//   a manifest written before the switch honours .gitignore as it did; git worktrees are not affected (they hold tracked files only).
// - Settings › Tools' "Also skip .gitignore patterns" is a working switch, a new run (and setup's inspection) follows it, and a run may
//   override it.
// - A workspace's patch is kept with the run (AgentCompareStore, cut at whole files above 1 MiB) when its model is done and again just
//   before a discard, so after Discard (or the 7-day cleanup) the diff modal and GET /api/compare/agent/{run}/diff still show the lines.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> WorkspaceFix2Cases() =>
    [
        Case("workspaces.fix2.gitignore-switch-off-copies-ignored-files", async () =>
        {
            await using var lab = WsLab.Create("fix2-gitignore");
            var project = lab.Folder("project");
            WsWrite(project,
                (".gitignore", "*.log\nsecret/\n"),
                ("src/app.cs", "class App {}\n"),
                ("bin/x.dll", "dll"),
                ("obj/project.assets.json", "{}"),
                ("node_modules/pkg/index.js", "module.exports = 1;"),
                ("logs/run.log", "log\n"),
                ("secret/key.txt", "local settings\n"),
                ("sub/.gitignore", "local.txt\n"),
                ("sub/local.txt", "ignored by the nested file\n"),
                ("artifacts/out.txt", "an extra exclude"));
            string[] honoured = [".gitignore", "src/app.cs", "sub/.gitignore"];
            string[] copiedToo = [".gitignore", "logs/run.log", "secret/key.txt", "src/app.cs", "sub/.gitignore", "sub/local.txt"];

            Check(new WorkspaceInspectOptions().UseGitignore, "inspecting honours .gitignore by default");
            Check(new WorkspaceRequest { Run = "cmp_default", ProjectPath = project, Slots = WsSlots("a") }.UseGitignore, "a run honours .gitignore by default");

            var on = await lab.Manager.InspectAsync(project, new WorkspaceInspectOptions { ExtraExcludes = ["artifacts"] });
            Check(on.UseGitignore, "the inspection says .gitignore is honoured");
            Equal((int?)honoured.Length, on.CopyFiles, "on: a copy leaves out what .gitignore lists");
            var off = await lab.Manager.InspectAsync(project, new WorkspaceInspectOptions { ExtraExcludes = ["artifacts"], UseGitignore = false });
            Check(!off.UseGitignore, "the inspection says .gitignore is not honoured");
            Equal((int?)copiedToo.Length, off.CopyFiles, "off: what .gitignore lists is counted; the excluded folders still aren't");
            Check(off.CopyBytes > on.CopyBytes, $"so the copy is larger ({off.CopyBytes} > {on.CopyBytes})");
            Equal("bin,obj,node_modules,.vs,.git,__pycache__,.pytest_cache,.mypy_cache,.ruff_cache,artifacts", string.Join(",", off.Excludes), "the excludes apply either way");

            var run = await lab.Manager.CreateAsync(new WorkspaceRequest
            { Run = "cmp_gi_off", ProjectPath = project, Slots = WsSlots("a"), ExtraExcludes = ["artifacts"], UseGitignore = false });
            Equal(WorkspaceKind.Copy, run.Kind, "a copy");
            Check(!run.UseGitignore, "the run records the switch");
            Equal(string.Join(",", copiedToo), string.Join(",", WsFiles(Path.Join(run.Directory, WorkspaceManager.BaseFolder))),
                "the base copy has the ignored files, never bin, obj, node_modules or the extra exclude");
            Equal(string.Join(",", copiedToo), string.Join(",", WsFiles(run.Slots[0].Path)), "so has the workspace");
            Check(!WorkspaceManifest.Read(run.Directory)!.UseGitignore, "the manifest keeps the switch");
            Check(!(await lab.Manager.GetAsync("cmp_gi_off"))!.UseGitignore, "read back from the manifest");

            (string, string)[] edits = [("debug.log", "new and ignored\n"), ("secret/new.txt", "new and ignored\n"), ("src/app.cs", "class App { int x; }\n"), ("bin/new.dll", "always skipped")];
            async Task<string> ChangedAsync(string name) =>
                string.Join(",", (await lab.Manager.DiffAsync(name, "a")).Files.Select(file => file.Path).Order(StringComparer.Ordinal));
            WsWrite(run.Slots[0].Path, edits);
            Equal("debug.log,secret/new.txt,src/app.cs", await ChangedAsync("cmp_gi_off"), "off: the diff shows the new ignored files too, never bin");

            var honouring = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_gi_on", ProjectPath = project, Slots = WsSlots("a"), ExtraExcludes = ["artifacts"] });
            Check(honouring.UseGitignore && WorkspaceManifest.Read(honouring.Directory)!.UseGitignore, "on is recorded too");
            Equal(string.Join(",", honoured), string.Join(",", WsFiles(honouring.Slots[0].Path)), "on: the ignored files are left out");
            WsWrite(honouring.Slots[0].Path, edits);
            Equal("src/app.cs", await ChangedAsync("cmp_gi_on"), "on: the diff leaves out what .gitignore lists");

            // A manifest written before the switch existed has no useGitignore: it honours .gitignore, as every copy did then.
            var manifestPath = WorkspaceManifest.PathIn(run.Directory);
            var json = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            Check(json.Remove("useGitignore"), "the manifest names the switch: " + Cut(json.ToJsonString()));
            File.WriteAllText(manifestPath, json.ToJsonString());
            Check(WorkspaceManifest.Read(run.Directory)!.UseGitignore, "an older manifest honours .gitignore");
            Equal("src/app.cs", await ChangedAsync("cmp_gi_off"), "and so does its diff");

            // Git worktrees hold tracked files only; their diff lists untracked files git doesn't ignore, whatever the switch says.
            var repo = await lab.RepoAsync("repo", (".gitignore", "*.tmp\n"), ("a.txt", "one\n"));
            var tree = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_gi_wt", ProjectPath = repo, Slots = WsSlots("a"), UseGitignore = false });
            Equal(WorkspaceKind.Worktree, tree.Kind, "a git repository still gets worktrees");
            WsWrite(tree.Slots[0].Path, ("scratch.tmp", "ignored"), ("b.txt", "bee\n"));
            Equal("b.txt", await ChangedAsync("cmp_gi_wt"), "a worktree's diff leaves out ignored files: the switch is for copies");
        }),

        Case("workspaces.fix2.kept-patch-is-cut-at-whole-files-and-read-back", async () =>
        {
            await using var lab = WsLab.Create("fix2-patch");
            var project = lab.Folder("project");
            WsWrite(project, ("a.txt", "one\n"), ("b.txt", "bee\n"), ("c.txt", "sea\n"));
            File.WriteAllBytes(Path.Join(project, "logo.bin"), [0, 1, 2, 0, 255]);
            var run = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_patch", ProjectPath = project, Slots = WsSlots("a") });
            var slot = run.Slots[0].Path;
            WsWrite(slot, ("a.txt", "one, then café\n"), ("b.txt", "bee\nand more\n"), ("new.txt", "new\n"));
            File.Delete(Path.Join(slot, "c.txt"));
            File.WriteAllBytes(Path.Join(slot, "logo.bin"), [0, 9, 9, 0, 255, 7]);
            var live = await lab.Manager.DiffAsync("cmp_patch", "a");
            Equal("a.txt,b.txt,c.txt,logo.bin,new.txt", string.Join(",", live.Files.Select(file => file.Path)), "five changed files, in path order");
            Equal<DateTimeOffset?>(null, live.StoredAt, "a live diff is not a kept one");

            var whole = WorkspacePatch.Cut(live.Patch, int.MaxValue);
            Equal(live.Patch, whole.Patch, "a patch that fits is kept as it is");
            Equal(5, whole.Files, "it has five files");
            Equal(5, whole.KeptFiles, "all kept");
            var second = live.Patch.IndexOf("\ndiff --git ", StringComparison.Ordinal) + 1;
            Check(second > 0, "the patch has a second file");
            var firstBytes = Encoding.UTF8.GetByteCount(live.Patch[..second]);
            Check(firstBytes > second, "the first file holds a character UTF-8 writes as two bytes");
            var one = WorkspacePatch.Cut(live.Patch, firstBytes);
            Equal(live.Patch[..second], one.Patch, "cut after the first whole file");
            Equal(5, one.Files, "it still counts every file");
            Equal(1, one.KeptFiles, "one kept");
            var under = WorkspacePatch.Cut(live.Patch, second);
            Equal("", under.Patch, "the cap counts UTF-8 bytes, and a file that doesn't fit is left out whole, never cut inside");
            Equal(0, under.KeptFiles, "nothing kept");
            Equal(0, WorkspacePatch.Cut("", 10).Files, "an empty patch has no files");

            var at = new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
            static string Lines(WorkspaceFileDiff file) =>
                file.Path + ":" + file.Change + ":" + file.Binary + ":" + string.Join("|", file.Hunks.SelectMany(hunk => hunk.Lines.Select(line => line.Kind + line.Text)));
            var all = WorkspacePatch.Read("cmp_patch", "a", WorkspaceKind.Copy, live.BaseLabel, whole.Patch, at);
            Equal(string.Join("\n", live.Files.Select(Lines)), string.Join("\n", all.Files.Select(Lines)), "read back: the same files, changes and lines (the binary file too)");
            Equal(live.Summary, all.Summary, "the same summary");
            Equal<DateTimeOffset?>(at, all.StoredAt, "when it was kept");
            Equal(0, all.OmittedFiles, "nothing left out");
            Equal(live.BaseLabel, all.BaseLabel, "what it is against");
            var cut = WorkspacePatch.Read("cmp_patch", "a", WorkspaceKind.Copy, live.BaseLabel, one.Patch, at, one.Files - one.KeptFiles);
            Equal(Lines(live.Files[0]), string.Join("\n", cut.Files.Select(Lines)), "a cut patch reads back as its whole first file");
            Equal(4, cut.OmittedFiles, "and says how many files it left out");
        }),

        Case("workspaces.fix2.kept-patch-store-keeps-replaces-and-caps", () =>
        {
            var root = Temp("fix2-patch-store");
            try
            {
                SqlMigration[] migrations = [CoreMigrations.AppInfo, AgentCompareStore.Migration, AgentCompareStore.PatchMigration];
                var db = new WebAgentDb(Path.Join(root, "webagent.db"), migrations);
                db.Migrate();
                var store = new AgentCompareStore(db);
                Equal<AgentKeptPatch?>(null, store.KeptPatch("cmp_x", "a"), "nothing kept yet");

                var small = Fix2Section("one.txt", 10) + Fix2Section("two.txt", 10);
                var kept = store.SavePatch("cmp_x", "a", small);
                Equal(small, kept.Patch, "a small patch is kept whole");
                Equal(2, kept.Files, "two files");
                Equal(2, kept.KeptFiles, "both kept");
                Equal(0, kept.OmittedFiles, "none left out");
                var back = store.KeptPatch("cmp_x", "a")!;
                Equal(small, back.Patch, "read back");
                Equal(kept.Saved, back.Saved, "with when it was kept");
                Equal<AgentKeptPatch?>(null, store.KeptPatch("cmp_x", "b"), "kept per slot");
                Equal<AgentKeptPatch?>(null, store.KeptPatch("cmp_y", "a"), "and per run");

                var large = Fix2Section("big.txt", 700_000) + Fix2Section("huge.txt", 700_000) + Fix2Section("tail.txt", 10);
                var capped = store.SavePatch("cmp_x", "a", large);
                Equal(3, capped.Files, "three files");
                Equal(1, capped.KeptFiles, "only the first fits in 1 MiB; the ones after the first that doesn't fit are left out too");
                Equal(2, capped.OmittedFiles, "two left out");
                Equal(Fix2Section("big.txt", 700_000), store.KeptPatch("cmp_x", "a")!.Patch, "a new patch replaces the one kept before");
                Check(Encoding.UTF8.GetByteCount(capped.Patch) <= AgentCompareStore.MaxPatchBytes, "within the cap");

                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var reopened = new WebAgentDb(Path.Join(root, "webagent.db"), migrations);
                reopened.Migrate();
                Equal(2, new AgentCompareStore(reopened).KeptPatch("cmp_x", "a")!.OmittedFiles, "kept on disk");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),

        Case("workspaces.fix2.diff-modal-shows-a-kept-patch-and-what-it-left-out", async () =>
        {
            await using var render = RenderHost.Create("fix2-render", (_, _) => { });
            var pages = new AgentComparePages(render.Services);
            var now = DateTimeOffset.UtcNow;
            AgentCompareModelSpec Spec(string slot, string model) => new() { Slot = slot, Provider = "faux", Model = model, Name = model, Thinking = "off", Billing = BillingKind.Free };
            var listed = new AgentDiffSummary
            {
                Files = 3, Added = 3, Removed = 0, Summary = WorkspaceText.Summary(3, 3, 0),
                Items = [new("one.txt", WorkspaceFileChange.Added, 1, 0, false), new("three.txt", WorkspaceFileChange.Added, 1, 0, false), new("two.txt", WorkspaceFileChange.Added, 1, 0, false)],
            };
            AgentCompareModelState Done(string slot, bool discarded) =>
                new() { Slot = slot, Phase = AgentModelPhase.Done, Outcome = AgentModelOutcome.Finished, EndedAt = now, Discarded = discarded, Diff = listed };
            var run = new AgentCompareRun
            {
                Id = "acr-kept", RequestId = "r-kept", State = AgentRunState.Finished, Created = now, Finished = now, Order = ["a", "b"],
                Spec = new AgentCompareSpec { Task = "Add three files", ProjectPath = render.Root, Instructions = "Work in the workspace.", Models = [Spec("a", "echo"), Spec("b", "echo-small")] },
                Models = [Done("a", discarded: false), Done("b", discarded: true)],
            };
            var view = AgentRunView.Build(run, new Dictionary<string, int>(), new Dictionary<string, SpendTotal?>(), [], pages.RunWorkspace(run), now);
            var patch = Fix2Section("one.txt", 5) + Fix2Section("three.txt", 5) + Fix2Section("two.txt", 5);
            Task<string> RenderAsync(string slot, WorkspaceDiff? diff, string? error = null) =>
                render.Renderer.RenderAsync(AgentComparePages.DiffPartial, new AgentDiffView { Run = run, Column = view.Columns.Single(column => column.Slot == slot), Diff = diff, Error = error });

            // B was discarded, and its kept patch was cut after one file: its lines, then the two files it has no room for, and no Copy patch.
            var oneFile = WorkspacePatch.Cut(patch, Encoding.UTF8.GetByteCount(Fix2Section("one.txt", 5)));
            var html = WebUtility.HtmlDecode(await RenderAsync("b", WorkspacePatch.Read(run.Id, "b", WorkspaceKind.Copy, "the base copy", oneFile.Patch, now, oneFile.Files - oneFile.KeptFiles)));
            NoRazorText(html, "a cut kept diff");
            var note = Regex.Match(html, "<div[^>]*data-ac-diff-stored>(.*?)</div>", RegexOptions.Singleline).Groups[1].Value;
            Equal($"This workspace was discarded; this is its diff as kept with the run. The kept patch was cut at {WorkspaceText.Size(AgentCompareStore.MaxPatchBytes)}, so 2 more changed files are listed below without lines.",
                note, "the note says what this is and what was left out");
            Check(html.Contains("class=\"ln add\"", StringComparison.Ordinal) && html.Contains("href=\"#acd-b-0\"", StringComparison.Ordinal), "the kept file's lines: " + Cut(html));
            var omitted = Regex.Match(html, "<ul class=\"ac-files\" data-ac-diff-omitted>(.*?)</ul>", RegexOptions.Singleline).Groups[1].Value;
            Check(omitted.Contains("three.txt", StringComparison.Ordinal) && omitted.Contains("two.txt", StringComparison.Ordinal) && !omitted.Contains("one.txt", StringComparison.Ordinal),
                "the files without lines are listed: " + omitted);
            Check(!html.Contains("data-ac-copy-patch", StringComparison.Ordinal), "a cut patch is not offered as Copy patch");
            Check(html.Contains("3 files · +3 −0 · against the base copy · workspace b (discarded)", StringComparison.Ordinal), "the subtitle counts every file the run lists: " + Cut(html));

            // A, removed by the cleanup, with its whole patch kept: the lines and Copy patch.
            html = WebUtility.HtmlDecode(await RenderAsync("a", WorkspacePatch.Read(run.Id, "a", WorkspaceKind.Copy, "the base copy", patch, now)));
            Check(html.Contains("This workspace isn't there any more (the cleanup removes workspaces after a while); this is its diff as kept with the run.", StringComparison.Ordinal), "the note for a removed workspace");
            Equal(3, Regex.Matches(html, "class=\"ln add\"").Count, "every file's lines");
            Check(html.Contains("data-ac-copy-patch=\"a\"", StringComparison.Ordinal) && !html.Contains("data-ac-diff-omitted", StringComparison.Ordinal), "Copy patch, nothing left out");
            Check(html.Contains("workspace a (removed)", StringComparison.Ordinal), "the subtitle says it is removed");

            // With no patch kept (a run from before this fix), the files the run lists, as before.
            html = WebUtility.HtmlDecode(await RenderAsync("b", null, "The workspace of model B was discarded, so its line-by-line diff is gone. The files it changed are kept with the run."));
            Check(html.Contains("two.txt", StringComparison.Ordinal) && !html.Contains("class=\"ln add\"", StringComparison.Ordinal) && !html.Contains("data-ac-copy-patch", StringComparison.Ordinal)
                && !html.Contains("data-ac-diff-stored", StringComparison.Ordinal), "the listed files only: " + Cut(html));
        }),

        Case("workspaces.fix2.app-gitignore-default-and-diff-after-discard", async () =>
        {
            await using var app = await CompareApp.StartAsync("fix2-ws-app", ToolApprovalMode.Auto);
            static Match Switch(string html) => Regex.Match(html, "<input[^>]*id=\"acDefGitignore\"[^>]*>");
            var tools = await app.App.Get("/settings/tools");
            var toggle = Switch(tools.Text);
            Check(toggle.Success && !toggle.Value.Contains("disabled", StringComparison.Ordinal) && toggle.Value.Contains("checked", StringComparison.Ordinal)
                && toggle.Value.Contains("data-ac-default=\"useGitignore\"", StringComparison.Ordinal) && toggle.Value.Contains("name=\"useGitignore\"", StringComparison.Ordinal),
                "Also skip .gitignore patterns is a working switch, on by default: " + toggle.Value);
            NoRazorText(tools.Text, "Settings › Tools");

            var saved = await app.PostAsync("/api/settings/agent-compare", new JsonObject { ["useGitignore"] = false });
            Equal(200, saved.Status, "saved: " + saved.Text);
            Equal(false, saved.Body!["useGitignore"]!.GetValue<bool>(), "off");
            toggle = Switch((await app.App.Get("/settings/tools")).Text);
            Check(toggle.Success && !toggle.Value.Contains("disabled", StringComparison.Ordinal) && !toggle.Value.Contains("checked", StringComparison.Ordinal),
                "the switch shows the saved value: " + toggle.Value);

            WsWrite(app.Project, (".gitignore", "*.log\n"), ("debug.log", "a local log\n"));
            var inspected = await app.PostAsync("/api/compare/agent/inspect", new JsonObject { ["projectPath"] = app.Project, ["isolation"] = "copy" });
            Equal(200, inspected.Status, "inspected: " + inspected.Text);
            Equal(false, inspected.Body!["useGitignore"]!.GetValue<bool>(), "setup's inspection follows the default");
            Equal(4, inspected.Body!["copyFiles"]!.GetValue<int>(), "README.md, src/app.txt, .gitignore and debug.log");
            var honoured = await app.PostAsync("/api/compare/agent/inspect", new JsonObject { ["projectPath"] = app.Project, ["isolation"] = "copy", ["useGitignore"] = true });
            Equal(3, honoured.Body!["copyFiles"]!.GetValue<int>(), "a run may honour .gitignore anyway: " + honoured.Text);

            var run = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "fix2-ws-1",
                ["task"] = "!echo done > result.txt",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo", "echo-small"),
                ["isolation"] = "copy",
                ["approval"] = "auto",
            });
            var id = run.Id();
            var finished = await app.WaitAsync(id, status => status.Str("state") == "finished", "the run finished");
            string Cwd(string slot) => finished.Models().Single(model => model.Str("slot") == slot).Str("workingDirectory")!;
            Check(File.Exists(Path.Join(Cwd("a"), "debug.log")) && File.Exists(Path.Join(Cwd("b"), "debug.log")), "with the switch off, the ignored file is copied");
            Check(File.Exists(Path.Join(Cwd("b"), "result.txt")), "b ran its command");
            var workspace = await app.App.Get($"/compare/agent/runs/{id}/workspace");
            Check(workspace.Text.Contains("data-ac-ws-gitignore=\"false\"", StringComparison.Ordinal) && !workspace.Text.Contains("anything in .gitignore", StringComparison.Ordinal),
                "the workspace modal says .gitignore was not skipped: " + Cut(workspace.Text));

            var discarded = await app.PostAsync($"/api/compare/agent/{id}/discard", new JsonObject { ["slot"] = "b" });
            Equal(200, discarded.Status, "B discarded: " + discarded.Text);
            Check(!Directory.Exists(Cwd("b")), "its workspace is gone");
            var page = WebUtility.HtmlDecode((await app.App.Get($"/compare/agent/runs/{id}/diff?slot=b")).Text);
            NoRazorText(page, "a discarded diff");
            Check(page.Contains("data-ac-diff-stored", StringComparison.Ordinal) && page.Contains("This workspace was discarded; this is its diff as kept with the run.", StringComparison.Ordinal),
                "the modal says it shows the kept diff: " + Cut(page));
            Check(page.Contains("result.txt", StringComparison.Ordinal) && page.Contains("class=\"ln add\"", StringComparison.Ordinal) && page.Contains("data-ac-copy-patch=\"b\"", StringComparison.Ordinal),
                "with the lines and Copy patch, not just the file list: " + Cut(page));

            async Task<JsonNode> StoredAsync(string slot, string what)
            {
                var diff = await app.App.Get($"/api/compare/agent/{id}/diff?slot={slot}");
                Equal(200, diff.Status, what + ": " + diff.Text);
                Check(diff.Body!["storedAt"] is JsonValue, what + " says when it was kept: " + Cut(diff.Text));
                Equal("result.txt", string.Join(",", diff.Body!["files"]!.AsArray().Select(file => file.Str("path"))), what + ": the changed file only");
                Check(diff.Body!.Str("patch")!.Contains("+done", StringComparison.Ordinal), what + ": the patch has the line");
                return diff.Body!;
            }
            await StoredAsync("b", "the discarded workspace's diff");

            // The cleanup removed A's folder after its 7 days: the patch kept when A was done answers.
            WorkspaceTree.Delete(Cwd("a"));
            await StoredAsync("a", "a removed workspace's diff");
            Check(WebUtility.HtmlDecode((await app.App.Get($"/compare/agent/runs/{id}/diff?slot=a")).Text).Contains("This workspace isn't there any more", StringComparison.Ordinal), "its modal says so");

            await app.KillAndRestartAsync();
            await StoredAsync("b", "after a restart");

            // A run's own useGitignore beats the default.
            var own = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "fix2-ws-2",
                ["task"] = "!echo hi > hi.txt",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo"),
                ["isolation"] = "copy",
                ["approval"] = "auto",
                ["useGitignore"] = true,
            });
            var ownDone = await app.WaitAsync(own.Id(), status => status.Str("state") == "finished", "the second run finished");
            var ownCwd = ownDone.Models().Single().Str("workingDirectory")!;
            Check(File.Exists(Path.Join(ownCwd, "hi.txt")) && !File.Exists(Path.Join(ownCwd, "debug.log")), "the run honours .gitignore as it asked");
            Check((await app.App.Get($"/compare/agent/runs/{own.Id()}/workspace")).Text.Contains("data-ac-ws-gitignore=\"true\"", StringComparison.Ordinal), "and says so");
        }),
    ];

    // One new file's section of a git patch, with one added line of `size` characters.
    private static string Fix2Section(string name, int size) =>
        $"diff --git a/{name} b/{name}\nnew file mode 100644\n--- /dev/null\n+++ b/{name}\n@@ -0,0 +1 @@\n+{new string('x', size)}\n";
}
