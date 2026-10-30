using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.WebAgent.AgentCompare;
using PiSharp.WebAgent.AgentCompare.Pages;
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Settings;
using PiSharp.WebAgent.Spend;

// Decision 0011 (run W4, R6): agent compare's views (setup, run, diff, keep, the workspace modal) and the Settings › Tools agent compare
// defaults. In-process cases cover the settings body check, the text helpers and the run states that are hard to reach live (a run that
// failed before any model ran, a budget Ask, the warnings of Allow-list, Auto and a command allowed in every workspace) through
// FragmentRenderer; the app cases run the built app (--faux: offline, no provider is called) over a temporary data folder and project and
// read the pages and fragments a browser reads, driving the run only through R5's /api/compare/agent.
internal static partial class Program
{
    // Razor writes "word@if (...)" as text (it reads like an e-mail address), so a page must never show a transition that did not run.
    private static void NoRazorText(string html, string what) =>
        Check(!Regex.IsMatch(html, "@(if|foreach|Model|\\()"), what + " shows no Razor code: " + Regex.Match(html, ".{0,60}@(if|foreach|Model|\\().{0,60}").Value);

    // The setup's project row, as compare-agent-setup.js asks for it: a same-origin JSON POST.
    private static Task<(int Status, JsonNode? Body, string Text, HttpResponseMessage Response)> ProjectFragmentAsync(CompareApp app, string path,
        string? isolation = null, int? models = null) =>
        app.App.SendAsync(HttpMethod.Post, "/compare/agent/project", new JsonObject { ["path"] = path, ["isolation"] = isolation, ["models"] = models });

    // A marker a planted repository command would write: a path the Git for Windows shell (and any POSIX shell) reads.
    private static string ShellPath(string path) => path.Replace('\\', '/');

    private static IEnumerable<(string, Func<Task>)> AgentComparePageCases() =>
    [
        Case("agent-compare-pages.settings-body-is-checked", () =>
        {
            var current = AgentCompareSettings.CreateDefault();
            var same = SettingsEndpoints.CheckAgentCompare(new AgentCompareBody(), current);
            Equal(current.MaxTurns, same.MaxTurns, "a field left out keeps its value");
            Equal(current.Approval, same.Approval, "the approval too");

            var changed = SettingsEndpoints.CheckAgentCompare(new AgentCompareBody(Isolation: "copy", ExtraExcludes: ["dist, out", "logs\nDIST", null],
                CopyWarnGb: 2.5, MaxTurns: 12, MaxMinutes: 30, Approval: "allowList", CheckTimeoutMinutes: 5), current);
            Equal(WorkspaceIsolation.Copy, changed.Isolation, "copy");
            Equal("dist|out|logs", string.Join("|", changed.ExtraExcludes!), "excludes split on commas and lines, without duplicates");
            Equal(2.5, changed.CopyWarnGb, "the size warning");
            Equal(12, changed.MaxTurns, "turns");
            Equal(30, changed.MaxMinutes, "minutes");
            Equal(5, changed.CheckTimeoutMinutes, "the check timeout");
            Equal(ToolApprovalMode.AllowList, changed.Approval, "Allow-list");
            Equal(WorkspaceIsolation.Automatic, SettingsEndpoints.CheckAgentCompare(new AgentCompareBody(Isolation: "auto"), changed).Isolation, "auto is automatic");
            Equal(ToolApprovalMode.Auto, SettingsEndpoints.CheckAgentCompare(new AgentCompareBody(Approval: "auto"), current).Approval, "Auto");

            Throws<ArgumentException>(() => SettingsEndpoints.CheckAgentCompare(new AgentCompareBody(Isolation: "sandbox"), current), "an unknown isolation");
            Throws<ArgumentException>(() => SettingsEndpoints.CheckAgentCompare(new AgentCompareBody(Approval: "always"), current), "an unknown approval");
            Throws<ArgumentException>(() => SettingsEndpoints.CheckAgentCompare(new AgentCompareBody(CopyWarnGb: 0.01), current), "a tiny size warning");
            Throws<ArgumentException>(() => SettingsEndpoints.CheckAgentCompare(new AgentCompareBody(CopyWarnGb: double.NaN), current), "NaN");
            Throws<ArgumentException>(() => SettingsEndpoints.CheckAgentCompare(new AgentCompareBody(MaxTurns: 0), current), "0 turns");
            Throws<ArgumentException>(() => SettingsEndpoints.CheckAgentCompare(new AgentCompareBody(MaxMinutes: 500), current), "500 minutes");
            Throws<ArgumentException>(() => SettingsEndpoints.CheckAgentCompare(new AgentCompareBody(ExtraExcludes: ["a|b"]), current), "a pipe is not a pattern");
        }),

        Case("agent-compare-pages.text-and-thinking-chips", () =>
        {
            Equal("45s", AgentText.Duration(45_400), "seconds");
            Equal("1m 30s", AgentText.Duration(90_000), "minutes and seconds");
            Equal("1h 05m", AgentText.Duration(3_900_000), "hours and minutes");
            Equal("0s", AgentText.Duration(-5), "never negative");
            Equal("1 file", AgentText.Count(1, "file"), "one");
            Equal("1,200 files", AgentText.Count(1200, "file"), "many");
            Equal("A, B and C", AgentText.Join(["A", "B", "C"]), "a list");
            Equal("A and B", AgentText.Join(["A", "B"]), "two");

            AgentSetupColumnView Column(string model, IReadOnlyList<string> levels, string shared, string? thinking = null, bool known = true) =>
                new() { Slot = "b", Provider = "faux", Model = model, Name = model, Known = known, Levels = levels, SharedThinking = shared, Thinking = thinking };
            var thinker = Column("echo-thinking", ["off", "minimal", "low", "medium", "high"], "xhigh");
            Equal("high", thinker.Effective, "xhigh is clamped to the model's highest level");
            Equal("thinking xhigh → high", thinker.Chip!.Text, "the chip says so");
            Check(Column("echo-thinking", ["off", "low", "high"], "high").Chip is null, "a level the model takes needs no chip");
            Equal("thinking n/a", Column("echo", ["off"], "high").Chip!.Text, "a model that doesn't think");
            Equal("off", Column("echo", ["off"], "high").Effective, "runs without thinking");
            Equal("thinking low", Column("echo-thinking", ["off", "low", "high"], "high", thinking: "low").Chip!.Text, "an override");
            Equal("low", Column("echo-thinking", ["off", "low", "high"], "high", thinking: "low").Effective, "is what the run sends");
            Equal("not available", Column("gone", ["off"], "high", known: false).Chip!.Text, "a model the catalog lost");
            Equal("B", thinker.Letter, "letters are positions");

            Equal("+10 −7", AgentColumnView.Delta(new AgentDiffFile("a.cs", WorkspaceFileChange.Modified, 10, 7, false)), "a modified file");
            Equal("binary", AgentColumnView.Delta(new AgentDiffFile("a.png", WorkspaceFileChange.Modified, 0, 0, true)), "a binary file");
            Equal("new · binary", AgentColumnView.Delta(new AgentDiffFile("b.png", WorkspaceFileChange.Added, 0, 0, true)), "a new binary file");
            Equal("new · +15", AgentColumnView.Delta(new AgentDiffFile("c.cs", WorkspaceFileChange.Added, 15, 0, false)), "a new file");
            Equal("deleted · −4", AgentColumnView.Delta(new AgentDiffFile("d.cs", WorkspaceFileChange.Deleted, 0, 4, false)), "a deleted file");
            Equal(WorkspaceIsolation.Copy, AgentComparePages.ParseIsolation(" COPY "), "isolation words");
            Equal(WorkspaceIsolation.Automatic, AgentComparePages.ParseIsolation("auto"), "auto");
            Check(AgentComparePages.ParseIsolation("sandbox") is null, "an unknown word");
        }),

        Case("agent-compare-pages.render-failed-budget-approval-and-warnings", async () =>
        {
            await using var render = RenderHost.Create("acp-render", (_, _) => { });
            var pages = new AgentComparePages(render.Services);
            var now = DateTimeOffset.UtcNow;
            AgentCompareModelSpec Spec(string slot, string model, BillingKind billing = BillingKind.Free, long? share = null) =>
                new() { Slot = slot, Provider = "faux", Model = model, Name = model, Thinking = "off", Billing = billing, ShareMicros = share };
            var spec = new AgentCompareSpec
            {
                Task = "Fix <b>the</b> test",
                ProjectPath = render.Root,
                Instructions = "Work in the workspace.",
                Models = [Spec("a", "echo"), Spec("b", "echo-thinking", BillingKind.Metered, 300_000)],
                Approval = ToolApprovalMode.AllowList,
                CapMicros = 600_000,
            };
            Task<string> Render(AgentCompareRun run, IReadOnlyList<AgentPendingApproval>? pending = null) =>
                render.Renderer.RenderAsync(AgentComparePages.RunPartial, AgentRunView.Build(run, new Dictionary<string, int>(), new Dictionary<string, SpendTotal?>(),
                    pending ?? [], pages.RunWorkspace(run), now));

            var failed = new AgentCompareRun
            {
                Id = "acr-failed", RequestId = "r1", State = AgentRunState.Failed, Error = "The project folder is gone.", Spec = spec, Order = ["b", "a"],
                Created = now, Models = [new AgentCompareModelState { Slot = "a" }, new AgentCompareModelState { Slot = "b" }],
            };
            var html = await Render(failed);
            Check(html.TrimStart().StartsWith("<div class=\"cmp-page ac-page\" data-agent-mode data-agent-compare=\"run\"", StringComparison.Ordinal), "one root: " + Cut(html));
            Check(html.Contains("data-ac-failed", StringComparison.Ordinal) && html.Contains("The project folder is gone.", StringComparison.Ordinal), "why it failed");
            Check(html.Contains("No model ran and nothing was spent.", StringComparison.Ordinal), "nothing ran");
            Check(!html.Contains("data-ac-stop-all", StringComparison.Ordinal) && html.Contains("data-ac-new-run", StringComparison.Ordinal), "New run, not Stop all");
            Check(html.Contains("Fix &lt;b&gt;the&lt;/b&gt; test", StringComparison.Ordinal) && !html.Contains("<b>the</b>", StringComparison.Ordinal), "the task is text");
            Check(html.Contains("data-ac-sandbox>", StringComparison.Ordinal) && html.Contains("Tool approval is Allow-list", StringComparison.Ordinal)
                && html.Contains("not a sandbox", StringComparison.Ordinal), "Allow-list shows the not-a-sandbox line");
            Check(!html.Contains("data-ac-stop=", StringComparison.Ordinal), "no model can be stopped");
            Check(!html.Contains("data-ac-elapsed", StringComparison.Ordinal), "no clock ticks");
            NoRazorText(html, "a failed run");
            var workspace = await render.Renderer.RenderAsync(AgentComparePages.WorkspacePartial, pages.RunWorkspace(failed));
            NoRazorText(workspace, "the workspace modal");
            Check(workspace.Contains("data-readonly=\"true\"", StringComparison.Ordinal) && workspace.Contains("The workspaces were not made", StringComparison.Ordinal),
                "the read-only workspace modal says why: " + Cut(workspace));
            Check(workspace.Contains("Workspaces are copies, not sandboxes.", StringComparison.Ordinal), "with the warning");

            var running = failed with
            {
                Id = "acr-running", State = AgentRunState.Running, Error = null, Started = now.AddSeconds(-40),
                Spec = spec with { Approval = ToolApprovalMode.Auto },
                Models =
                [
                    new AgentCompareModelState
                    {
                        Slot = "a", Phase = AgentModelPhase.Running, StartedAt = now.AddSeconds(-40), WorkspacePath = render.Root,
                        LatestTool = new AgentToolCall("bash", "dotnet test", now), Touched = ["src/App.cs"],
                    },
                    new AgentCompareModelState
                    {
                        Slot = "b", Phase = AgentModelPhase.Running, StartedAt = now.AddSeconds(-30), WorkspacePath = render.Root,
                        BudgetAsk = new AgentBudgetAsk(100_000, 490_000, 300_000, now),
                    },
                ],
            };
            html = await Render(running);
            Check(html.Contains("data-ac-stop-all", StringComparison.Ordinal) && !html.Contains("data-ac-new-run", StringComparison.Ordinal), "Stop all while it runs");
            Check(Regex.IsMatch(html, "data-ac-col data-slot=\"a\" data-view=\"running\""), "A runs: " + Cut(html));
            Check(Regex.IsMatch(html, "data-ac-col data-slot=\"b\" data-view=\"budget\""), "B waits for a budget answer");
            Check(html.Contains("data-ac-continue=\"turn\" data-slot=\"b\"", StringComparison.Ordinal) && html.Contains("data-ac-continue=\"run\" data-slot=\"b\"", StringComparison.Ordinal)
                && html.Contains("data-ac-stop=\"b\"", StringComparison.Ordinal), "Continue one turn, for the run, or Stop");
            Check(WebUtility.HtmlDecode(html).Contains("over its $0.30 share of the run cap", StringComparison.Ordinal), "the ask names the share");
            Check(html.Contains($"data-since=\"{now.AddSeconds(-40).ToUnixTimeMilliseconds()}\"", StringComparison.Ordinal), "A's clock ticks from its start");
            Check(html.Contains("Tool approval is Auto in workspaces", StringComparison.Ordinal) && html.Contains("not a sandbox", StringComparison.Ordinal), "Auto warns");
            Check(html.Contains("dotnet test", StringComparison.Ordinal) && html.Contains("src/App.cs", StringComparison.Ordinal), "the latest call and the files so far");

            var asking = running with
            {
                Id = "acr-asking",
                Spec = spec with { Approval = ToolApprovalMode.Ask },
                Models = [running.Models[0], running.Models[1] with { BudgetAsk = null }],
            };
            var call = new ApprovalRequest("session-a", 1, 7, "call-1", "bash", new JsonObject { ["command"] = "rm -rf build <x>" });
            html = await Render(asking, [new AgentPendingApproval("a", new PendingApproval(call, now.AddSeconds(-5)))]);
            Check(Regex.IsMatch(html, "data-ac-col data-slot=\"a\" data-view=\"approval\""), "A waits for its approval");
            NoRazorText(html, "a waiting call");
            Check(html.Contains("Allow this command for the rest of the run", StringComparison.Ordinal) || html.Contains("Allow this command in all 2 workspaces", StringComparison.Ordinal),
                "the grant names the workspaces still running");
            Check(html.Contains("data-ac-approval-banner", StringComparison.Ordinal) && html.Contains("1 waiting", StringComparison.Ordinal), "the approval banner");
            Check(html.Contains("data-ac-approve=\"allow\" data-slot=\"a\" data-task-id=\"7\"", StringComparison.Ordinal)
                && html.Contains("data-ac-approve=\"deny\" data-slot=\"a\" data-task-id=\"7\"", StringComparison.Ordinal), "Allow and Deny name the call");
            Check(html.Contains("rm -rf build &lt;x&gt;", StringComparison.Ordinal) && !html.Contains("<x>", StringComparison.Ordinal), "the command is text");
            Check(html.Contains("data-ac-approve-command", StringComparison.Ordinal) && html.Contains("id=\"acAllowAllWarn\"", StringComparison.Ordinal),
                "Allow this command in all workspaces, with its warning");
            var warning = Regex.Match(html, "<div[^>]*id=\"acAllowAllWarn\"[^>]*>(.*?)</div>", RegexOptions.Singleline);
            Check(warning.Success && warning.Value.Contains("not a sandbox", StringComparison.Ordinal) && !warning.Value.Contains("hidden", StringComparison.Ordinal),
                "the grant's warning is visible text: " + warning.Value);
            Check(!html.Contains("data-ac-sandbox>", StringComparison.Ordinal), "Ask with no grant has no not-a-sandbox line");

            var granted = asking with { Grants = [new ToolAllowRule("bash", "rm -rf build <x>")] };
            html = await Render(granted);
            Check(html.Contains("data-ac-sandbox>", StringComparison.Ordinal) && html.Contains("Allowed in every workspace for this run: rm -rf build &lt;x&gt;.", StringComparison.Ordinal),
                "a command allowed in every workspace shows the line: " + Cut(html));

            // Keep: a conflict found by the preview.
            var done = granted with
            {
                Id = "acr-done", State = AgentRunState.Finished, Finished = now,
                Models =
                [
                    running.Models[0] with { Phase = AgentModelPhase.Done, Outcome = AgentModelOutcome.Finished, EndedAt = now, Answer = "Fixed **it**. <script>alert(1)</script>" },
                    running.Models[1] with { Phase = AgentModelPhase.Done, Outcome = AgentModelOutcome.Stopped, EndedAt = now, BudgetAsk = null },
                ],
            };
            var view = AgentRunView.Build(done, new Dictionary<string, int>(), new Dictionary<string, SpendTotal?>(), [], pages.RunWorkspace(done), now);
            html = await render.Renderer.RenderAsync(AgentComparePages.RunPartial, view);
            Check(html.Contains("<strong>it</strong>", StringComparison.Ordinal) && !html.Contains("<script>alert", StringComparison.Ordinal), "the answer is sanitized Markdown");
            var preview = new KeepPreview
            {
                Run = done.Id, Slot = "a", Kind = WorkspaceKind.Worktree, Files = ["src/App.cs"], Branch = "pisharp/compare/acr-done-a",
                Conflicts = [new UncommittedChange("src/App.cs", "M")], OtherUncommitted = [new UncommittedChange("notes.md", "M")],
            };
            var keep = WebUtility.HtmlDecode(await render.Renderer.RenderAsync(AgentComparePages.KeepPartial,
                new AgentKeepView { Run = done, Column = view.Columns[0], Preview = preview, RetentionDays = 7 }));
            Check(keep.Contains("data-pane=\"conflict\"", StringComparison.Ordinal) && keep.Contains("Can't apply A", StringComparison.Ordinal), "the conflict pane: " + Cut(keep));
            NoRazorText(keep, "the conflict pane");
            Check(keep.Contains("Commit or stash your change and try again, or keep A's changes on the branch <span class=\"ac-path\">pisharp/compare/acr-done-a</span> instead.", StringComparison.Ordinal),
                "what to do instead: " + Regex.Match(keep, "Commit or stash.{0,200}", RegexOptions.Singleline).Value);
            Check(keep.Contains("changed in both", StringComparison.Ordinal) && keep.Contains("data-ac-keep-retry=\"a\"", StringComparison.Ordinal)
                && keep.Contains("data-ac-keep-branch-instead", StringComparison.Ordinal), "Try again, or Keep the branch instead");
            Check(Regex.IsMatch(keep, "<div data-ac-keep-pane=\"options\" hidden"), "the options wait behind it");
            var options = await render.Renderer.RenderAsync(AgentComparePages.KeepPartial,
                new AgentKeepView { Run = done, Column = view.Columns[0], Preview = preview with { Conflicts = [] }, RetentionDays = 7 });
            Check(options.Contains("data-pane=\"options\"", StringComparison.Ordinal) && Regex.IsMatch(options, "id=\"acKeepApply\"[^>]*checked"), "Apply is chosen");
            var discard = Regex.Match(options, "<input[^>]*id=\"acKeepDiscard\"[^>]*>");
            Check(discard.Success && !discard.Value.Contains("checked", StringComparison.Ordinal), "Discard the others is not chosen for the user: " + discard.Value);
            Check(options.Contains("notes.md", StringComparison.Ordinal) && options.Contains("left alone", StringComparison.Ordinal), "the project's other changes are left alone");
            Check(options.Contains("pisharp/compare/acr-done-a", StringComparison.Ordinal), "the branch it would keep");
            var gone = await render.Renderer.RenderAsync(AgentComparePages.KeepPartial,
                new AgentKeepView { Run = done, Column = view.Columns[0], Error = "Workspace a was discarded." });
            Check(gone.Contains("data-pane=\"error\"", StringComparison.Ordinal) && gone.Contains("Workspace a was discarded.", StringComparison.Ordinal), "nothing to keep");
        }),

        Case("agent-compare-pages.app.setup-page-and-fragments", async () =>
        {
            await using var app = await CompareApp.StartAsync("acp-setup", ToolApprovalMode.Ask);
            var page = await app.App.Get("/compare?mode=agent");
            Equal(200, page.Status, "the setup page: " + Cut(page.Text));
            var html = page.Text;
            Check(html.Contains("data-agent-compare=\"setup\"", StringComparison.Ordinal), "the setup");
            NoRazorText(html, "the setup page");
            Check(Regex.IsMatch(html, "id=\"acApprovalAsk\"[^>]*>\\s*<label[^>]*>Ask<span"), "the Ask label: " + Regex.Match(html, "id=\"acApprovalAsk\".{0,200}", RegexOptions.Singleline).Value);
            Check(!html.Contains("<script>", StringComparison.Ordinal) && html.Contains("/js/compare-agent.js", StringComparison.Ordinal)
                && html.Contains("/css/compare-agent.css", StringComparison.Ordinal), "its module and styles, no inline script");
            Check(Regex.Matches(html, "data-ac-setup-col ").Count >= 1, "at least one model column");
            Check(html.Contains("id=\"modal-agent-workspace\"", StringComparison.Ordinal) && html.Contains("id=\"modal-model-picker\"", StringComparison.Ordinal), "its modals");
            Check(html.Contains("data-ac-iso-host", StringComparison.Ordinal) && html.Contains("Checking the folder", StringComparison.Ordinal), "the folder is checked after load");
            Check(Regex.IsMatch(html, "id=\"acAllowWarn\">[^<]*<a ") && Regex.Matches(html, "not a sandbox").Count >= 2, "Allow-list and Auto say not a sandbox");
            Check(WebUtility.HtmlDecode(html).Contains(Path.Join(app.Root, "work"), StringComparison.Ordinal), "the default folder is Settings › Tools' working directory");
            Check(html.Contains("value=\"agent\"", StringComparison.Ordinal), "the Request · Agent switch");
            // agent-setup and agent-dirty (the same page; the dirty banner comes with the project fragment) show the shared System prompt
            // beside the Task label, collapsed, as request mode's _ComparePrompt does.
            var toggle = WebUtility.HtmlDecode(Regex.Match(html, "<button[^>]*data-ac-system-toggle[^>]*>.*?</button>", RegexOptions.Singleline).Value);
            Check(toggle.Contains("aria-expanded=\"false\"", StringComparison.Ordinal) && toggle.Contains("data-bs-target=\"#acSystem\"", StringComparison.Ordinal)
                && toggle.Contains("System prompt", StringComparison.Ordinal) && toggle.Contains("· none", StringComparison.Ordinal), "the System prompt toggle, collapsed: " + toggle);
            Check(html.Contains("class=\"collapse\" id=\"acSystem\"", StringComparison.Ordinal) && Regex.IsMatch(html, "<textarea[^>]*id=\"acSystemText\"[^>]*data-ac-system></textarea>"),
                "its empty textarea, collapsed: " + Regex.Match(html, "id=\"acSystem\".{0,400}", RegexOptions.Singleline).Value);
            Check(html.IndexOf("data-ac-system-toggle", StringComparison.Ordinal) < html.IndexOf("id=\"acTask\"", StringComparison.Ordinal), "above the task");

            var project = await ProjectFragmentAsync(app, app.Project, models: 2);
            Equal(200, project.Status, "the project fragment");
            Check(project.Text.TrimStart().StartsWith("<div data-ac-project-fragment", StringComparison.Ordinal), "one root: " + Cut(project.Text));
            Check(project.Text.Contains("data-kind=\"copy\"", StringComparison.Ordinal) && project.Text.Contains("data-ok=\"true\"", StringComparison.Ordinal), "a folder outside git is copied");
            Check(project.Text.Contains("data-ac-workspace-content data-pane=\"copy\" data-readonly=\"false\"", StringComparison.Ordinal), "the editable workspace modal");
            Check(Regex.IsMatch(project.Text, "id=\"acIsoWorktree\"[^>]*disabled"), "no worktree for it");

            var missing = await ProjectFragmentAsync(app, Path.Join(app.Root, "missing"));
            Check(missing.Text.Contains("data-ok=\"false\"", StringComparison.Ordinal) && WebUtility.HtmlDecode(missing.Text).Contains("Can't use this folder", StringComparison.Ordinal),
                "a missing folder: " + Cut(missing.Text));

            await using var lab = WsLab.Create("acp-dirty");
            var repo = await lab.RepoAsync("repo", ("a.txt", "one\n"));
            File.WriteAllText(Path.Join(repo, "a.txt"), "two\n");
            var dirty = await ProjectFragmentAsync(app, repo);
            Check(dirty.Text.Contains("data-kind=\"worktree\"", StringComparison.Ordinal) && dirty.Text.Contains("data-ac-banner=\"dirty\"", StringComparison.Ordinal),
                "a git repo with an uncommitted change: " + Cut(dirty.Text));
            Check(dirty.Text.Contains("a.txt", StringComparison.Ordinal) && dirty.Text.Contains("data-ac-use-copy", StringComparison.Ordinal)
                && dirty.Text.Contains("data-ac-dismiss-banner", StringComparison.Ordinal), "the change, Use a temporary copy, Continue without it");
            var copied = await ProjectFragmentAsync(app, repo, isolation: "copy");
            Check(copied.Text.Contains("data-kind=\"copy\"", StringComparison.Ordinal) && !copied.Text.Contains("data-ac-banner=\"dirty\"", StringComparison.Ordinal),
                "a copy includes the change, so no banner");

            // Inspecting a folder runs git in it, so another website must not be able to ask for it: the project row is a same-origin
            // JSON POST, and no fragment answers a request the browser marks as cross-site.
            Equal(405, (await app.App.Get("/compare/agent/project?path=" + Uri.EscapeDataString(repo))).Status, "no GET inspects a folder (an <img> could send one)");
            Equal(403, (await app.App.SendAsync(HttpMethod.Post, "/compare/agent/project", new JsonObject { ["path"] = repo }, origin: "https://evil.example")).Status,
                "a POST from another origin is refused");
            Equal(415, (await app.App.SendAsync(HttpMethod.Post, "/compare/agent/project", text: "path=" + repo, contentType: "text/plain")).Status,
                "a body a cross-site form or no-cors fetch can send is refused");
            async Task<int> FetchSiteAsync(string path, string site)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(app.App.Base, path));
                request.Headers.Add("Sec-Fetch-Site", site);
                using var response = await app.App.Http.SendAsync(request);
                return (int)response.StatusCode;
            }
            var column = "/compare/agent/column?slot=a&provider=faux&model=echo&count=1";
            Equal(403, await FetchSiteAsync(column, "cross-site"), "a fragment refuses a cross-site request");
            Equal(403, await FetchSiteAsync(column, "same-site"), "and one from another port of localhost");
            Equal(200, await FetchSiteAsync(column, "same-origin"), "the app's own pages are answered");
            Equal(200, await FetchSiteAsync(column, "none"), "and a typed address");
            if (OperatingSystem.IsWindows())
            {
                // A UNC path would reach that host over SMB or WebDAV (offering the user's credentials) before any check of the folder.
                foreach (var path in new[] { @"\\pisharp-test.invalid\share\project", "//pisharp-test.invalid/share", @"\\?\UNC\pisharp-test.invalid\share", @"\\.\C:\" })
                {
                    var unc = await ProjectFragmentAsync(app, path);
                    Check(unc.Text.Contains("data-ok=\"false\"", StringComparison.Ordinal) && WebUtility.HtmlDecode(unc.Text).Contains("network path", StringComparison.Ordinal),
                        $"{path} is refused before it is touched: " + Cut(unc.Text));
                }
                var inspect = await app.PostAsync("/api/compare/agent/inspect", new JsonObject { ["projectPath"] = @"\\pisharp-test.invalid\share" });
                Equal(400, inspect.Status, "R5's inspect refuses a UNC path too: " + inspect.Text);
                Check(inspect.Text.Contains("network path", StringComparison.Ordinal), "and says why");
            }

            var thinker = await app.App.Get("/compare/agent/column?slot=b&provider=faux&model=echo-thinking&sharedThinking=xhigh&count=2&maxTurns=9");
            Equal(200, thinker.Status, "a column");
            var text = WebUtility.HtmlDecode(thinker.Text);
            Check(thinker.Text.TrimStart().StartsWith("<section class=\"compare-col\"", StringComparison.Ordinal), "one root");
            Check(text.Contains("thinking xhigh → high", StringComparison.Ordinal) && thinker.Text.Contains("data-effective=\"high\"", StringComparison.Ordinal), "clamped: " + Cut(text));
            Check(thinker.Text.Contains("id=\"ac-b-thinking\"", StringComparison.Ordinal) && text.Contains("Stops at 9 turns", StringComparison.Ordinal), "its slot and limits");
            var echo = WebUtility.HtmlDecode((await app.App.Get("/compare/agent/column?slot=a&provider=faux&model=echo&sharedThinking=high&count=1")).Text);
            Check(echo.Contains("thinking n/a", StringComparison.Ordinal) && echo.Contains("data-effective=\"off\"", StringComparison.Ordinal), "a model that doesn't think");
            Check(Regex.IsMatch(echo, "data-ac-col-remove disabled"), "the only column can't be removed");
            var unknown = WebUtility.HtmlDecode((await app.App.Get("/compare/agent/column?slot=a&provider=faux&model=nope&count=2")).Text);
            Check(unknown.Contains("data-known=\"false\"", StringComparison.Ordinal) && unknown.Contains("not available", StringComparison.Ordinal), "an unknown model");
            Equal(400, (await app.App.Get("/compare/agent/column?slot=z&provider=faux&model=echo")).Status, "a bad slot");
            Equal(400, (await app.App.Get("/compare/agent/column?slot=a&provider=faux")).Status, "no model");
        }),

        Case("agent-compare-pages.app.run-approval-diff-keep-discard", async () =>
        {
            await using var app = await CompareApp.StartAsync("acp-run", ToolApprovalMode.Auto);
            var run = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "acp-run-1",
                ["task"] = "!echo done > result.txt",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo", "echo-small"),
                ["isolation"] = "copy",
                ["approval"] = "ask",
                ["checkCommand"] = "echo checked",
            });
            var id = run.Id();
            var waiting = await app.WaitAsync(id, status => status.Models().All(model => model.Str("waiting") == "approval"), "both models ask");

            var page = await app.App.Get("/compare?mode=agent&run=" + id);
            Equal(200, page.Status, "the run page");
            var html = page.Text;
            Check(html.Contains($"data-agent-compare=\"run\" data-run=\"{id}\"", StringComparison.Ordinal) && html.Contains("data-active=\"true\"", StringComparison.Ordinal), "the run, live");
            NoRazorText(html, "the run page");
            Check(!html.Contains("<script>", StringComparison.Ordinal) && html.Contains("/js/compare-agent.js", StringComparison.Ordinal), "its module, no inline script");
            Check(html.Contains("id=\"modal-agent-diff\"", StringComparison.Ordinal) && html.Contains("id=\"modal-keep-result\"", StringComparison.Ordinal)
                && html.Contains("id=\"modal-agent-workspace\"", StringComparison.Ordinal), "its modals");
            Equal(2, Regex.Matches(html, "data-view=\"approval\"").Count, "both columns wait for an approval");
            Check(html.Contains("data-ac-approval-banner", StringComparison.Ordinal) && html.Contains("2 waiting", StringComparison.Ordinal), "the approval banner");
            Check(html.Contains("echo done &gt; result.txt", StringComparison.Ordinal), "the waiting command, as text");
            Check(html.Contains("data-ac-approve-command", StringComparison.Ordinal) && html.Contains("Allow this command in all 2 workspaces", StringComparison.Ordinal)
                && html.Contains("id=\"acAllowAllWarn\"", StringComparison.Ordinal), "Allow this command in all workspaces, with its warning");
            Check(html.Contains("data-ac-approve-all=\"allow\"", StringComparison.Ordinal) && html.Contains("data-ac-approve-all=\"deny\"", StringComparison.Ordinal), "Allow all and Deny all");
            // Allow all and Deny all name the calls they answer (slot:taskId), the ones the banner counted, so a later call is never answered.
            var shown = waiting.Models().SelectMany(model => model["approvals"]!.AsArray()
                .Select(call => model.Str("slot") + ":" + call!["taskId"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture))).Order(StringComparer.Ordinal).ToList();
            foreach (var decision in new[] { "allow", "deny" })
            {
                var listed = Regex.Match(html, $"data-ac-approve-all=\"{decision}\" data-ac-items=\"([^\"]*)\"").Groups[1].Value;
                Check(listed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).SequenceEqual(shown),
                    $"{decision} all lists the waiting calls ({string.Join(' ', shown)}): '{listed}'");
            }
            Check(html.Contains("data-ac-stop-all", StringComparison.Ordinal) && !html.Contains("data-ac-sandbox>", StringComparison.Ordinal), "Stop all; Ask has no not-a-sandbox line");

            var fragment = await app.App.Get("/compare/agent/runs/" + id);
            Equal(200, fragment.Status, "the live fragment");
            Check(fragment.Text.TrimStart().StartsWith("<div class=\"cmp-page ac-page\" data-agent-mode data-agent-compare=\"run\"", StringComparison.Ordinal), "one root: " + Cut(fragment.Text));
            foreach (var region in new[] { "project", "details", "controls", "banners", "status" })
                Check(fragment.Text.Contains($"data-ac-region=\"{region}\"", StringComparison.Ordinal), "the region " + region);
            var workspace = await app.App.Get($"/compare/agent/runs/{id}/workspace");
            Check(workspace.Text.Contains("data-readonly=\"true\"", StringComparison.Ordinal) && workspace.Text.Contains("data-pane=\"copy\"", StringComparison.Ordinal),
                "the run's workspaces, read-only: " + Cut(workspace.Text));
            Equal(404, (await app.App.Get("/compare/agent/runs/nope")).Status, "an unknown run");
            Equal(404, (await app.App.Get("/compare/agent/runs/nope/workspace")).Status, "its workspaces");

            var callA = waiting.Models().Single(model => model.Str("slot") == "a")["approvals"]!.AsArray().Single()!["taskId"]!.GetValue<long>();
            var granted = await app.PostAsync($"/api/compare/agent/{id}/approve", new JsonObject { ["scope"] = "command", ["slot"] = "a", ["taskId"] = callA, ["decision"] = "allow" });
            Equal(200, granted.Status, "allowed in every workspace: " + granted.Text);
            await app.WaitAsync(id, status => status.Str("state") == "finished", "the run finished");

            html = (await app.App.Get("/compare/agent/runs/" + id)).Text;
            Equal(2, Regex.Matches(html, "data-view=\"done\"").Count, "both done");
            Check(html.Contains("data-active=\"false\"", StringComparison.Ordinal) && html.Contains("data-ac-new-run", StringComparison.Ordinal)
                && !html.Contains("data-ac-stop-all", StringComparison.Ordinal), "New run instead of Stop all");
            Check(html.Contains("data-ac-sandbox>", StringComparison.Ordinal) && html.Contains("Allowed in every workspace for this run: echo done &gt; result.txt.", StringComparison.Ordinal),
                "the granted command shows the not-a-sandbox line");
            Check(html.Contains("data-ac-check-chip", StringComparison.Ordinal) && html.Contains("result.txt", StringComparison.Ordinal), "the check and the changed file");
            Check(html.Contains("data-ac-diff=\"a\"", StringComparison.Ordinal) && html.Contains("data-ac-keep=\"a\"", StringComparison.Ordinal)
                && html.Contains("data-ac-open-chat=\"a\"", StringComparison.Ordinal), "View diff, Keep, Open as chat");
            Check(!html.Contains("data-ac-elapsed data-since", StringComparison.Ordinal), "nothing ticks once it is done");

            var diff = await app.App.Get($"/compare/agent/runs/{id}/diff?slot=a");
            Equal(200, diff.Status, "the diff");
            Check(diff.Text.TrimStart().StartsWith("<div class=\"modal-content\" data-ac-diff-content", StringComparison.Ordinal), "one root: " + Cut(diff.Text));
            NoRazorText(diff.Text, "the diff");
            Check(diff.Text.Contains("result.txt", StringComparison.Ordinal) && diff.Text.Contains("class=\"ln add\"", StringComparison.Ordinal), "the added file's lines");
            Check(diff.Text.Contains("data-ac-copy-patch=\"a\"", StringComparison.Ordinal) && diff.Text.Contains("data-ac-diff=\"b\"", StringComparison.Ordinal), "Copy patch and the A/B switch");
            Equal(400, (await app.App.Get($"/compare/agent/runs/{id}/diff?slot=q")).Status, "a bad slot");

            var keep = await app.App.Get($"/compare/agent/runs/{id}/keep?slot=a");
            Equal(200, keep.Status, "the keep modal");
            Check(keep.Text.Contains("data-pane=\"options\"", StringComparison.Ordinal) && Regex.IsMatch(keep.Text, "id=\"acKeepApply\"[^>]*checked"), "Apply, chosen");
            NoRazorText(keep.Text, "the keep modal");
            Check(Regex.IsMatch(keep.Text, "id=\"acKeepBranch\"[^>]*disabled"), "no branch from a copy");
            var discardBox = Regex.Match(keep.Text, "<input[^>]*id=\"acKeepDiscard\"[^>]*>");
            Check(discardBox.Success && !discardBox.Value.Contains("checked", StringComparison.Ordinal), "Discard the others is the user's choice: " + discardBox.Value);
            Equal(400, (await app.App.Get($"/compare/agent/runs/{id}/keep")).Status, "no slot");

            var applied = await app.PostAsync($"/api/compare/agent/{id}/apply", new JsonObject { ["slot"] = "a" });
            Equal("applied", applied.Body!["result"]!.Str("outcome"), "applied: " + applied.Text);
            html = WebUtility.HtmlDecode((await app.App.Get("/compare/agent/runs/" + id)).Text);
            Check(html.Contains("data-ac-kept=\"a\"", StringComparison.Ordinal) && html.Contains("A's changes are in", StringComparison.Ordinal), "what Keep did: " + Cut(html));
            Check(html.Contains("Applied to project", StringComparison.Ordinal), "A's chip");

            var discarded = await app.PostAsync($"/api/compare/agent/{id}/discard", new JsonObject { ["slot"] = "b" });
            Equal(200, discarded.Status, "B discarded: " + discarded.Text);
            var gone = WebUtility.HtmlDecode((await app.App.Get($"/compare/agent/runs/{id}/diff?slot=b")).Text);
            // W4 fix 2: the patch is kept with the run before the discard, so a discarded workspace still shows its lines and Copy patch.
            Check(gone.Contains("result.txt", StringComparison.Ordinal) && gone.Contains("class=\"ln add\"", StringComparison.Ordinal)
                && gone.Contains("data-ac-copy-patch=\"b\"", StringComparison.Ordinal) && gone.Contains("data-ac-diff-stored", StringComparison.Ordinal),
                "a discarded workspace shows the diff kept with the run: " + Cut(gone));
            Check(WebUtility.HtmlDecode((await app.App.Get("/compare/agent/runs/" + id)).Text).Contains("Workspace discarded", StringComparison.Ordinal), "B's chip");

            var again = WebUtility.HtmlDecode((await app.App.Get("/compare?mode=agent&from=" + id)).Text);
            Check(again.Contains("data-agent-compare=\"setup\"", StringComparison.Ordinal) && again.Contains("Copied from run", StringComparison.Ordinal)
                && again.Contains("!echo done > result.txt", StringComparison.Ordinal) && again.Contains("value=\"echo checked\"", StringComparison.Ordinal), "New run copies the run: " + Cut(again));
            Equal(2, Regex.Matches(again, "data-ac-setup-col ").Count, "with its two models");
        }),

        Case("agent-compare-pages.app.settings-defaults-save-read-reset", async () =>
        {
            await using var app = await CompareApp.StartAsync("acp-settings", ToolApprovalMode.Ask);
            var tools = await app.App.Get("/settings/tools");
            Equal(200, tools.Status, "Settings › Tools");
            Check(tools.Text.Contains("data-agent-compare-defaults", StringComparison.Ordinal) && tools.Text.Contains("id=\"agent-compare\"", StringComparison.Ordinal), "the section");
            Check(tools.Text.Contains("/js/compare-agent-defaults.js", StringComparison.Ordinal) && !tools.Text.Contains("<script>", StringComparison.Ordinal), "its module, no inline script");
            Check(tools.Text.Contains("Coming in a later milestone", StringComparison.Ordinal), "built-in tools are still later");
            NoRazorText(tools.Text, "Settings › Tools");
            Check(Regex.IsMatch(tools.Text, "data-ac-approval-pane=\"auto\" hidden"), "Ask is shown, Auto's text waits");
            Check(Regex.IsMatch(tools.Text, "data-ac-approval-pane=\"ask\">"), "Ask's text");

            var saved = await app.PostAsync("/api/settings/agent-compare", new JsonObject
            {
                ["maxTurns"] = 12, ["approval"] = "auto", ["extraExcludes"] = new JsonArray("dist, out"), ["copyWarnGb"] = 2.5,
            });
            Equal(200, saved.Status, "saved: " + saved.Text);
            Equal(12, saved.Body!["maxTurns"]!.GetValue<int>(), "turns");
            Equal("auto", saved.Body!.Str("approval"), "Auto");
            Equal("dist|out", string.Join("|", saved.Body!["extraExcludes"]!.AsArray().Select(item => item!.GetValue<string>())), "excludes");
            var read = await app.App.Get("/api/settings/agent-compare");
            Equal(12, read.Body!["maxTurns"]!.GetValue<int>(), "read back");
            Equal(20, read.Body!["maxMinutes"]!.GetValue<int>(), "a field left out kept its value");

            tools = await app.App.Get("/settings/tools");
            Check(Regex.IsMatch(tools.Text, "id=\"acDefMaxTurns\"[^>]*value=\"12\""), "the form shows the saved turns");
            Check(Regex.IsMatch(tools.Text, "data-ac-approval-pane=\"auto\">"), "Auto's text is shown");
            Check(Regex.IsMatch(tools.Text, "<option value=\"auto\" selected"), "Auto is chosen");
            var autoText = Regex.Match(tools.Text, "data-ac-approval-pane=\"auto\">(.*?)</div>", RegexOptions.Singleline).Groups[1].Value;
            Check(autoText.Contains("not a sandbox", StringComparison.Ordinal) && autoText.Contains("without asking", StringComparison.Ordinal), "it says what Auto means: " + autoText);

            var setup = (await app.App.Get("/compare?mode=agent")).Text;
            Check(Regex.IsMatch(setup, "id=\"acMaxTurns\"[^>]*value=\"12\""), "a new run starts with the saved turns");
            Check(Regex.IsMatch(setup, "id=\"acApprovalAuto\"[^>]*checked"), "and Auto");
            Check(setup.Contains("class=\"collapse show\" id=\"acLimits\"", StringComparison.Ordinal), "Limits and approvals open when tools run without asking");

            var bad = await app.PostAsync("/api/settings/agent-compare", new JsonObject { ["isolation"] = "sandbox" });
            Equal(400, bad.Status, "an unknown isolation");
            Check(bad.Body?["error"] is JsonValue, "with the reason: " + bad.Text);
            Equal(400, (await app.PostAsync("/api/settings/agent-compare", new JsonObject { ["maxTurns"] = 0 })).Status, "0 turns");
            Equal(12, (await app.App.Get("/api/settings/agent-compare")).Body!["maxTurns"]!.GetValue<int>(), "a refused save changed nothing");

            var reset = await app.PostAsync("/api/settings/agent-compare/reset", new JsonObject());
            Equal(200, reset.Status, "reset: " + reset.Text);
            Equal(AgentCompareSettings.DefaultMaxTurns, reset.Body!["maxTurns"]!.GetValue<int>(), "the default turns");
            Equal("ask", reset.Body!.Str("approval"), "Ask");
        }),

        // W4 fix (agent-ui): looking at a folder must not run what its repository names. An extracted download can carry a .git/config
        // with core.fsmonitor and filter drivers, and hooks; git status, worktree add and update-ref would run them.
        Case("agent-compare-pages.looking-at-a-folder-runs-none-of-its-repository-commands", async () =>
        {
            await using var lab = WsLab.Create("acp-planted");
            var repo = await lab.RepoAsync("planted", ("a.txt", "one\n"), ("b.md", "bee\n"));
            var markers = lab.Folder("markers");
            string Marker(string name) => ShellPath(Path.Join(markers, name));
            string[] Ran() => [.. Directory.EnumerateFiles(markers).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal)];
            // Planted after the commits above, as a download would bring them.
            await lab.GitAsync(repo, "config", "core.fsmonitor", $"echo ran > '{Marker("fsmonitor")}'; false");
            await lab.GitAsync(repo, "config", "filter.evil.clean", $"echo ran > '{Marker("clean")}'; cat");
            var included = Path.Join(lab.Folder("included"), "filters.cfg");
            await lab.GitAsync(repo, "config", "--file", included, "filter.inc.clean", $"echo ran > '{Marker("include-clean")}'; cat");
            await lab.GitAsync(repo, "config", "include.path", ShellPath(included));
            Directory.CreateDirectory(Path.Join(repo, ".git", "info"));
            File.WriteAllText(Path.Join(repo, ".git", "info", "attributes"), "*.txt filter=evil\n*.md filter=inc\n");
            var hooks = Path.Join(repo, ".git", "hooks");
            Directory.CreateDirectory(hooks);
            foreach (var hook in new[] { "post-checkout", "reference-transaction", "pre-commit", "post-commit", "post-index-change" })
            {
                var file = Path.Join(hooks, hook);
                File.WriteAllText(file, $"#!/bin/sh\necho ran > '{Marker(hook)}'\n");
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var touched = 0;
            void StatDirty()
            {
                // Same content, a new time: git status must compare the content, which is when a clean filter runs.
                touched++;
                foreach (var name in new[] { "a.txt", "b.md" })
                {
                    var file = Path.Join(repo, name);
                    File.WriteAllText(file, File.ReadAllText(file));
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5 * touched));
                }
            }

            // The trap works: a plain git status runs the planted fsmonitor and both filters.
            StatDirty();
            await lab.TryGitAsync(repo, "status", "--porcelain");
            Check(Ran().Contains("fsmonitor") && Ran().Contains("clean") && Ran().Contains("include-clean"), "a plain git status runs them: " + string.Join(", ", Ran()));
            foreach (var file in Directory.EnumerateFiles(markers)) File.Delete(file);
            StatDirty();

            var inspection = await lab.Manager.InspectAsync(repo, new WorkspaceInspectOptions { EstimateCopy = true });
            Equal(WorkspaceKind.Worktree, inspection.Kind, "a git repo");
            Equal("", string.Join(", ", Ran()), "inspecting it ran none of its commands");

            // Making a worktree (post-checkout) and Keep the branch (update-ref: reference-transaction) run none of its hooks either.
            var run = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_planted", ProjectPath = repo, Slots = WsSlots("a") });
            File.WriteAllText(Path.Join(run.Slots[0].Path, "new.dat"), "new\n");
            var kept = await lab.Manager.KeepBranchAsync("cmp_planted", "a");
            Equal(KeepOutcome.BranchCreated, kept.Outcome, "kept: " + kept.Message);
            var ran = Ran();
            Check(!ran.Any(name => name is "fsmonitor" or "post-checkout" or "reference-transaction" or "pre-commit" or "post-commit" or "post-index-change"),
                "no hook and no fsmonitor ran: " + string.Join(", ", ran));

            // A network or device path is refused before anything touches it (Windows has them; elsewhere they are ordinary paths).
            Check(!WorkspaceManager.IsNetworkOrDevicePath(repo), "a local folder is fine");
            if (OperatingSystem.IsWindows())
            {
                foreach (var path in new[] { @"\\pisharp-test.invalid\share", "//pisharp-test.invalid/share", @"\\?\UNC\pisharp-test.invalid\share", @"\\.\C:\x", @"\??\C:\x" })
                {
                    Check(WorkspaceManager.IsNetworkOrDevicePath(path), path + " is a network or device path");
                    var refused = await ThrowsAsync<WorkspaceException>(() => lab.Manager.InspectAsync(path), path + " is refused");
                    Equal(WorkspaceException.Invalid, refused.Code, path + ": " + refused.Message);
                }
                var create = await ThrowsAsync<WorkspaceException>(() => lab.Manager.CreateAsync(new WorkspaceRequest
                    { Run = "cmp_unc", ProjectPath = @"\\pisharp-test.invalid\share", Slots = WsSlots("a") }), "a run on a UNC path");
                Equal(WorkspaceManager.NetworkPathMessage, create.Message, "says why");
            }
        }),

        // W4 fix (agent-ui): the setup's shared System prompt reaches every model as the same instructions, and New run copies it.
        Case("agent-compare-pages.app.system-prompt-reaches-every-model-and-new-run", async () =>
        {
            const string Prompt = "You are a careful <reviewer>. Answer in English.";
            var instructions = AgentCompareRunner.Instructions("echo checked", Prompt);
            Check(instructions.StartsWith(Prompt + "\n\n", StringComparison.Ordinal) && instructions.EndsWith(AgentCompareRunner.Instructions("echo checked"), StringComparison.Ordinal),
                "the system prompt, then the workspace rules: " + instructions);
            Equal(AgentCompareRunner.Instructions(null), AgentCompareRunner.Instructions(null, "  "), "a blank system prompt adds nothing");
            Equal(Prompt, AgentCompareEndpoints.CreateRequest(new JsonObject { ["systemPrompt"] = "  " + Prompt + "\n" }).SystemPrompt, "the wire field, trimmed");

            await using var app = await CompareApp.StartAsync("acp-system", ToolApprovalMode.Auto);
            var tooLong = await app.PostAsync("/api/compare/agent", new JsonObject
            {
                ["requestId"] = "acp-system-long", ["task"] = "!echo hi", ["projectPath"] = app.Project, ["models"] = Models("echo"),
                ["systemPrompt"] = new string('x', AgentCompareRunner.MaxSystemPromptLength + 1),
            });
            Equal(400, tooLong.Status, "a system prompt over the limit: " + tooLong.Text);

            var run = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "acp-system-1",
                ["task"] = "!echo done > result.txt",
                ["systemPrompt"] = "  " + Prompt + "\n",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo", "echo-small"),
                ["isolation"] = "copy",
                ["approval"] = "auto",
                ["checkCommand"] = "echo checked",
            });
            var id = run.Id();
            Equal(Prompt, run.Str("systemPrompt"), "the run keeps the shared system prompt");
            await app.WaitAsync(id, status => status.Str("state") == "finished", "the run finished");

            // Each model's durable session has the same instructions (its chat's session details show them as the system prompt).
            foreach (var slot in new[] { "a", "b" })
            {
                var opened = await app.PostAsync($"/api/compare/agent/{id}/open-chat", new JsonObject { ["slot"] = slot });
                Equal(200, opened.Status, $"{slot} opened as a chat: " + opened.Text);
                var chat = await app.App.Get(opened.Body!.Str("url")!);
                Equal(200, chat.Status, "its page");
                var shown = Regex.Match(chat.Text, "<h6>System prompt</h6>\\s*<div[^>]*>(.*?)</div>", RegexOptions.Singleline);
                Check(shown.Success, $"{slot}'s session details show a system prompt: " + Cut(chat.Text));
                Equal(instructions, WebUtility.HtmlDecode(shown.Groups[1].Value), $"{slot} got the run's instructions");
            }

            // New run copies it into the setup, open, with its preview.
            var again = (await app.App.Get("/compare?mode=agent&from=" + id)).Text;
            NoRazorText(again, "New run");
            Check(Regex.IsMatch(again, "<textarea[^>]*id=\"acSystemText\"[^>]*data-ac-system>You are a careful &lt;reviewer&gt;. Answer in English.</textarea>"),
                "the system prompt is copied, as text: " + Regex.Match(again, "id=\"acSystemText\".{0,300}", RegexOptions.Singleline).Value);
            Check(again.Contains("class=\"collapse show\" id=\"acSystem\"", StringComparison.Ordinal), "and shown, since it holds text");
            var toggle = WebUtility.HtmlDecode(Regex.Match(again, "<button[^>]*data-ac-system-toggle[^>]*>.*?</button>", RegexOptions.Singleline).Value);
            Check(toggle.Contains("aria-expanded=\"true\"", StringComparison.Ordinal) && toggle.Contains("· You are a careful <reviewer>. Answer in English.", StringComparison.Ordinal),
                "the toggle's preview: " + toggle);
        }),
    ];
}
