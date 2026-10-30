using System.Text.Json.Nodes;
using PiSharp.WebAgent.AgentCompare;
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Spend;

// Decision 0011 (run W4, R5): the agent compare runner. In-process cases cover its pieces (settings, the approval override's workspace
// rule, the check command, run ids, the create request); the app cases run the built app (--faux, or --faux-metered for the budget share:
// offline, no provider is called) over a temporary data folder and a temporary project, with git's global and system config switched off.
// Faux models answer "!command" with a bash call, so each model edits its own workspace; a run is driven only through /api/compare/agent
// (and /api/chats once a model is opened as a chat), as R6's pages will drive it.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> AgentCompareCases() =>
    [
        Case("agent-compare.settings-defaults-and-limits", () =>
        {
            var defaults = AgentCompareSettings.CreateDefault();
            Equal(WorkspaceIsolation.Automatic, defaults.Isolation, "isolation defaults to automatic");
            Equal(40, defaults.MaxTurns, "40 turns");
            Equal(20, defaults.MaxMinutes, "20 minutes");
            Equal(10, defaults.CheckTimeoutMinutes, "a 10 minute check");
            Equal(ToolApprovalMode.Ask, defaults.Approval, "approval asks");
            Equal(1L << 30, defaults.CopyWarnBytes, "warn above 1 GB");
            Check(defaults.UseGitignore, ".gitignore is honoured");
            Equal("agentCompare", AgentCompareSettings.SectionName, "the section name");
            Throws<ArgumentException>(() => (defaults with { MaxTurns = 0 }).Normalize(), "0 turns");
            Throws<ArgumentException>(() => (defaults with { MaxTurns = 201 }).Normalize(), "201 turns");
            Throws<ArgumentException>(() => (defaults with { MaxMinutes = 241 }).Normalize(), "241 minutes");
            Throws<ArgumentException>(() => (defaults with { CheckTimeoutMinutes = 0 }).Normalize(), "a 0 minute check");
            Throws<ArgumentException>(() => (defaults with { CopyWarnGb = 0 }).Normalize(), "warn above 0 GB");
            Equal("dist|out", string.Join("|", AgentCompareSettings.NormalizeExcludes([" dist ", "bin", "DIST", "", "out", "node_modules"])),
                "excludes are trimmed, without the defaults, blanks or duplicates");
            Throws<ArgumentException>(() => AgentCompareSettings.NormalizeExcludes(["a|b"]), "a pipe is not a pattern");
            Equal("dist", string.Join("|", (defaults with { ExtraExcludes = ["dist", " dist"] }).Normalize().ExtraExcludes!), "Normalize cleans excludes");
        }),

        Case("agent-compare.override-asks-outside-the-workspace-and-for-commands", () =>
        {
            var root = Temp("ac-override");
            try
            {
                var workspace = Path.Join(root, "ws", "a");
                var project = Path.Join(workspace, "project");
                Directory.CreateDirectory(Path.Join(project, "src"));
                var ask = new ApprovalOverride(ToolApprovalMode.Ask, workspace, project);
                var auto = new ToolsSettings(root, ToolApprovalMode.Auto, [new ToolAllowRule("bash", "echo hi")]);
                JsonObject Args(string name, string value) => new() { [name] = value };
                bool Needs(ApprovalOverride over, string tool, JsonObject args, ToolsSettings? settings = null, IReadOnlyList<ToolAllowRule>? grants = null) =>
                    WebApproval.NeedsApproval(over, settings ?? auto, grants ?? [], tool, args);

                Check(!Needs(ask, "write", Args("path", "src/a.txt")), "a write inside the workspace does not ask");
                Check(!Needs(ask, "edit", Args("path", "@src/a.txt")), "an edit inside it (with @) does not ask");
                Check(!Needs(ask, "write", Args("path", Path.Join(project, "b.txt"))), "an absolute path inside does not ask");
                Check(!Needs(ask, "write", Args("path", Path.Join(workspace, "c.txt"))), "the workspace root counts, not only the project");
                Check(Needs(ask, "write", Args("path", "../../../outside.txt")), "a write outside the workspace asks");
                Check(Needs(ask, "write", Args("path", "~/x.txt")), "a home path asks");
                Check(Needs(ask, "write", new JsonObject()), "a write without a path asks");
                Check(Needs(ask, "bash", Args("command", "echo hi")), "a command asks in Ask, whatever Settings › Tools says (Auto here)");
                Check(Needs(ask, "start_session", new JsonObject()), "another changing tool asks");
                Check(!Needs(ask with { Grants = [new ToolAllowRule("bash", "echo hi")] }, "bash", Args("command", "echo hi")), "a run grant lets it through");
                Check(Needs(ask with { Grants = [new ToolAllowRule("bash", "echo hi")] }, "bash", Args("command", "rm -rf x")), "only that command");
                Check(!Needs(ask, "bash", Args("command", "echo hi"), grants: [new ToolAllowRule("bash")]), "a session grant lets it through");
                var allowList = ask with { Mode = ToolApprovalMode.AllowList };
                Check(!Needs(allowList, "bash", Args("command", "echo hi")), "Allow-list runs a command on Settings › Tools' allow-list");
                Check(Needs(allowList, "bash", Args("command", "echo bye")), "and asks for another");
                Check(!Needs(allowList, "write", Args("path", "src/a.txt")), "Allow-list writes inside the workspace without asking");
                Check(!Needs(ask with { Mode = ToolApprovalMode.Auto }, "bash", Args("command", "rm -rf /")), "Auto never asks");
                Check(Needs(new ApprovalOverride(ToolApprovalMode.Ask, "", ""), "write", Args("path", "a.txt")), "an unknown workspace asks");

                var outside = Path.Join(root, "elsewhere");
                Directory.CreateDirectory(outside);
                var linked = false;
                try
                {
                    Directory.CreateSymbolicLink(Path.Join(project, "link"), outside);
                    linked = true;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    // Links need developer mode or elevation on Windows; the rule is still covered by the cases above.
                }
                if (linked)
                {
                    Check(Needs(ask, "write", Args("path", "link/x.txt")), "a write through a link inside the workspace asks");
                    Check(!WebApproval.IsInside(ask, "link"), "the link itself is outside");
                }
            }
            finally
            {
                DeleteTemp(root);
            }
        }),

        Case("agent-compare.override-asks-before-a-write-to-git-metadata", () =>
        {
            // A worktree's .git file names the repository git uses there; Ask must not let a model rewrite it (or any .git folder) unasked.
            var root = Temp("ac-dotgit");
            try
            {
                var workspace = Path.Join(root, "ws", "a");
                var project = Path.Join(workspace, "project");
                Directory.CreateDirectory(project);
                File.WriteAllText(Path.Join(workspace, ".git"), "gitdir: elsewhere\n");
                var ask = new ApprovalOverride(ToolApprovalMode.Ask, workspace, project);
                var settings = new ToolsSettings(root, ToolApprovalMode.Auto, []);
                JsonObject Path_(string path) => new() { ["path"] = path };
                bool Needs(ApprovalOverride over, string tool, string path) => WebApproval.NeedsApproval(over, settings, [], tool, Path_(path));

                Check(Needs(ask, "write", Path.Join(workspace, ".git")), "a write to the worktree's .git file asks");
                Check(Needs(ask, "write", "../.git"), "also by a relative path");
                Check(Needs(ask, "edit", ".git/config"), "an edit inside a .git folder asks");
                Check(Needs(ask, "write", "sub/.git/HEAD"), "a nested .git folder asks");
                Check(Needs(ask, "write", "../.GIT"), "in any case");
                Check(Needs(ask with { Mode = ToolApprovalMode.AllowList }, "write", "../.git"), "Allow-list asks too");
                Check(!Needs(ask, "write", ".gitignore") && !Needs(ask, "write", ".gitattributes") && !Needs(ask, "write", "git/notes.txt") &&
                    !Needs(ask, "write", "a.git"), "other files whose names start or end like it don't ask");
                if (OperatingSystem.IsWindows())
                {
                    Check(Needs(ask, "write", "../.git."), "a trailing dot (Windows drops it)");
                    Check(Needs(ask, "write", "../.git::$DATA"), "a stream of the file");
                    Check(Needs(ask, "write", "../GIT~1"), "an 8.3 short name that may be .git's");
                }
                Check(WebApproval.IsGitMetadata(".git") && !WebApproval.IsGitMetadata(".github"), "the component rule");
            }
            finally
            {
                DeleteTemp(root);
            }
        }),

        Case("agent-compare.planted-gitdir-is-never-used", async () =>
        {
            // A model (or a prompt injection) rewrites its worktree's .git to point at a repository of its own whose config runs a command
            // (a clean filter) when git adds files. The app's diff and keep must never load it, and must refuse while .git is changed. The
            // project's own hooks and fsmonitor don't run for the app's git commands either.
            await using var lab = WsLab.Create("ac-gitdir");
            var repo = await lab.RepoAsync("project", ("a.txt", "one\n"));
            var markers = lab.Folder("markers");
            var hooked = Path.Join(markers, "hook.txt");
            var monitored = Path.Join(markers, "fsmonitor.txt");
            var filtered = Path.Join(markers, "filter.txt");
            WriteScript(Path.Join(repo, ".git", "hooks", "post-checkout"), $"echo hooked > {QuotedShellPath(hooked)}");
            var monitor = Path.Join(markers, "fsmonitor.sh");
            WriteScript(monitor, $"echo ran > {QuotedShellPath(monitored)}");
            await lab.GitAsync(repo, "config", "core.fsmonitor", QuotedShellPath(monitor));

            await lab.Manager.InspectAsync(repo);
            var run = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_planted", ProjectPath = repo, Slots = WsSlots("a") });
            Check(!File.Exists(hooked), "the project's post-checkout hook did not run for worktree add");
            Check(!File.Exists(monitored), "the project's fsmonitor command did not run for git status");
            var folder = Path.Join(lab.Root, "cmp_planted");
            var admin = WorkspaceManifest.Read(folder)!.Find("a")!.GitDir;
            Check(admin is not null && WorkspaceTree.IsWithin(Path.Join(repo, ".git", "worktrees"), admin), "the worktree's repository folder is recorded: " + admin);

            var slot = run.Slots.Single().Path;
            var dotGit = Path.Join(slot, ".git");
            var original = File.ReadAllText(dotGit);
            File.SetAttributes(dotGit, FileAttributes.Normal); // git hides it on Windows, and a hidden file can't be overwritten in place
            var evil = Path.Join(slot, ".evil");
            Directory.CreateDirectory(Path.Join(evil, "objects", "info"));
            Directory.CreateDirectory(Path.Join(evil, "refs"));
            File.WriteAllText(Path.Join(evil, "HEAD"), "ref: refs/heads/main\n");
            File.WriteAllText(Path.Join(evil, "objects", "info", "alternates"), Path.Join(repo, ".git", "objects").Replace('\\', '/') + "\n");
            File.WriteAllText(Path.Join(evil, "config"), "[core]\n\trepositoryformatversion = 0\n[filter \"x\"]\n\tclean = tee "
                + QuotedShellPath(filtered).Replace("\"", "\\\"", StringComparison.Ordinal) + "\n");
            File.WriteAllText(Path.Join(slot, ".gitattributes"), "* filter=x\n");
            File.WriteAllText(Path.Join(slot, "a.txt"), "two\n");
            File.WriteAllText(Path.Join(slot, "b.txt"), "new\n");
            File.WriteAllText(dotGit, "gitdir: .evil\n");

            var refused = await ThrowsAsync<WorkspaceException>(() => lab.Manager.DiffAsync("cmp_planted", "a"), "a diff with a rewritten .git");
            Equal(WorkspaceException.Tampered, refused.Code, "is refused: " + refused.Message);
            Equal(WorkspaceException.Tampered, (await ThrowsAsync<WorkspaceException>(() => lab.Manager.PreviewKeepAsync("cmp_planted", "a"), "keep's preview")).Code, "refused");
            Equal(WorkspaceException.Tampered, (await ThrowsAsync<WorkspaceException>(() => lab.Manager.KeepBranchAsync("cmp_planted", "a"), "keep the branch")).Code, "refused");
            Equal(WorkspaceException.Tampered, (await ThrowsAsync<WorkspaceException>(() => lab.Manager.ApplyAsync("cmp_planted", "a"), "apply")).Code, "refused");
            Check(!File.Exists(filtered), "the planted filter never ran");

            // With .git put back, git uses the recorded repository: the planted one (still in the folder) is never loaded.
            File.WriteAllText(dotGit, original);
            var diff = await lab.Manager.DiffAsync("cmp_planted", "a");
            Check(diff.Files.Any(file => file.Path == "a.txt") && diff.Files.Any(file => file.Path == "b.txt"), "the diff has the model's changes: " + diff.Summary);
            Check(!File.Exists(filtered) && !File.Exists(monitored) && !File.Exists(hooked), "no planted filter, fsmonitor or hook ran");

            // A run made before the folder was recorded finds it in the project's repository (and records it).
            var manifest = WorkspaceManifest.Read(folder)!;
            manifest.Find("a")!.GitDir = null;
            manifest.Write(folder);
            Equal(diff.Patch, (await lab.Manager.DiffAsync("cmp_planted", "a")).Patch, "the same diff");
            Check(WorkspaceTree.SamePath(WorkspaceManifest.Read(folder)!.Find("a")!.GitDir!, admin!), "the folder is recorded again");
            Check(!File.Exists(filtered), "still no filter");

            await lab.GitAsync(repo, "config", "--unset", "core.fsmonitor");
            Check(await lab.Manager.DiscardRunAsync("cmp_planted"), "discarded");
            Check(!(await lab.GitAsync(repo, "worktree", "list", "--porcelain")).Contains("cmp_planted", StringComparison.OrdinalIgnoreCase), "and unregistered");
        }),

        Case("agent-compare.command-grants-are-exact", () =>
        {
            JsonObject Command(string command) => new() { ["command"] = command };
            var rules = ToolAllowList.RulesFor("bash", Command("rm -rf *"));
            Equal(@"rm -rf \*", rules.Single().Command, "a literal star is stored escaped");
            Check(ToolAllowList.Allows(rules, "bash", Command("rm -rf *")), "the approved command is allowed");
            Check(ToolAllowList.Allows(rules, "bash", Command("rm   -rf  *")), "with other spacing");
            Check(!ToolAllowList.Allows(rules, "bash", Command("rm -rf /tmp/x")), "granting rm -rf * does not allow rm -rf /tmp/x");
            Check(!ToolAllowList.Allows(rules, "bash", Command("rm -rf ~")) && !ToolAllowList.Allows(rules, "bash", Command("rm -rf")), "nor any other path, nor none");
            var grant = new ApprovalOverride(ToolApprovalMode.Ask, "", "") { Grants = rules };
            var settings = new ToolsSettings("x", ToolApprovalMode.Ask, []);
            Check(!WebApproval.NeedsApproval(grant, settings, [], "bash", Command("rm -rf *")), "the run grant lets the same command through");
            Check(WebApproval.NeedsApproval(grant, settings, [], "bash", Command("rm -rf /tmp/x")), "and asks for rm -rf /tmp/x");

            var powershell = ToolAllowList.RulesFor("powershell", Command("Remove-Item * -Recurse"));
            Check(ToolAllowList.Allows(powershell, "powershell", Command("Remove-Item * -Recurse")), "Remove-Item * -Recurse");
            Check(!ToolAllowList.Allows(powershell, "powershell", Command(@"Remove-Item C:\Users -Recurse")), "never Remove-Item <anything> -Recurse");
            var chained = ToolAllowList.RulesFor("bash", Command("ls *.txt && rm -rf *"));
            Check(ToolAllowList.Allows(chained, "bash", Command("ls *.txt && rm -rf *")), "a chained command, part by part");
            Check(!ToolAllowList.Allows(chained, "bash", Command("ls *.txt && rm -rf build")), "each part exactly");
            var escaped = ToolAllowList.RulesFor("bash", Command(@"find . -name \*.txt"));
            Check(ToolAllowList.Allows(escaped, "bash", Command(@"find . -name \*.txt")) && !ToolAllowList.Allows(escaped, "bash", Command("find . -name a.txt")),
                "a backslash before a star stays literal");

            Check(ToolAllowList.MatchesPattern("dotnet test *", "dotnet test --no-build") && ToolAllowList.MatchesPattern("dotnet test *", "dotnet test") &&
                ToolAllowList.MatchesPattern("git log*", "git log --oneline"), "patterns typed in Settings › Tools keep their wildcards");
            Check(ToolAllowList.MatchesPattern(@"echo \*", "echo *") && !ToolAllowList.MatchesPattern(@"echo \*", "echo hi"), @"\* is a literal star");
        }),

        Case("agent-compare.store-listeners-never-break-a-change", async () =>
        {
            // An event stream whose tab closed may still be called (the store raises from a snapshot of its handlers): whatever a listener
            // throws, Add, Update and Touch (a model's request, a state change) go on.
            var root = Temp("ac-store");
            try
            {
                var db = new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, AgentCompareStore.Migration]);
                db.Migrate();
                var store = new AgentCompareStore(db);
                var heard = new List<string>();
                store.Changed += _ => throw new ObjectDisposedException("a closed event stream");
                store.Changed += heard.Add;
                var run = store.Add(new AgentCompareRun
                {
                    Id = "cmp_store",
                    RequestId = "store-1",
                    Spec = new AgentCompareSpec { Task = "t", ProjectPath = root, Instructions = "i", Models = [] },
                    Order = [],
                    Created = DateTimeOffset.UtcNow,
                    Models = [],
                });
                store.Update(run.Id, current => current with { Error = "changed" });
                store.Touch(run.Id);
                Equal(3, heard.Count, "the other listener heard every change");
                Equal("changed", store.Get(run.Id)!.Error, "the change was stored");

                var signal = new ChangeSignal();
                signal.Set();
                signal.Set();
                Check(await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None), "a change wakes the stream");
                Check(!await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None), "two changes are one wake-up");
                signal.Close();
                signal.Set();
                Check(!await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None), "a closed signal is safe to set and never wakes");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),

        Case("agent-compare.check-command-passes-fails-and-times-out-cleanly", async () =>
        {
            var root = Temp("ac-check");
            var secret = "PISHARP_AC_TEST_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant() + "_API_KEY";
            Environment.SetEnvironmentVariable(secret, "planted-value");
            try
            {
                var windows = OperatingSystem.IsWindows();
                var passed = await AgentCheckCommand.RunAsync(windows ? $"echo checked [%{secret}%]" : $"echo \"checked [${secret}]\"", root,
                    TimeSpan.FromSeconds(30), CancellationToken.None);
                Equal(AgentCheckState.Passed, passed.State, "exit 0 passes: " + passed.Tail);
                Equal(0, passed.ExitCode, "its exit code");
                Check(passed.Tail.Contains("checked", StringComparison.Ordinal), "its output is kept: " + passed.Tail);
                Check(!passed.Tail.Contains("planted-value", StringComparison.Ordinal), "a credential-named variable is not passed on: " + passed.Tail);

                var failed = await AgentCheckCommand.RunAsync(windows ? "echo broken & exit /b 3" : "echo broken; exit 3", root, TimeSpan.FromSeconds(30), CancellationToken.None);
                Equal(AgentCheckState.Failed, failed.State, "another exit code fails");
                Equal(3, failed.ExitCode, "its exit code is kept");
                Check(failed.Tail.Contains("broken", StringComparison.Ordinal), "with the output");

                var clock = System.Diagnostics.Stopwatch.StartNew();
                var slow = await AgentCheckCommand.RunAsync(windows ? "ping -n 30 127.0.0.1 >nul" : "sleep 30", root, TimeSpan.FromSeconds(2), CancellationToken.None);
                Equal(AgentCheckState.TimedOut, slow.State, "a check over its timeout times out");
                Equal<int?>(null, slow.ExitCode, "it has no exit code");
                Equal(2, slow.TimeoutSeconds, "the timeout it had");
                Check(slow.Tail.Contains("[Stopped after 2 s", StringComparison.Ordinal), "the output says it was stopped: " + slow.Tail);
                Check(clock.Elapsed < TimeSpan.FromSeconds(15), "it was stopped promptly: " + clock.Elapsed);

                var missing = await AgentCheckCommand.RunAsync("echo x", Path.Join(root, "missing"), TimeSpan.FromSeconds(5), CancellationToken.None);
                Equal(AgentCheckState.Error, missing.State, "a command that can't start is an error");

                Check(AgentCheckCommand.IsCredentialName("OPENAI_API_KEY") && AgentCheckCommand.IsCredentialName("GH_TOKEN") &&
                    AgentCheckCommand.IsCredentialName("PISHARP_WEBAGENT_DATA") && !AgentCheckCommand.IsCredentialName("PATH"), "credential names");
            }
            finally
            {
                Environment.SetEnvironmentVariable(secret, null);
                DeleteTemp(root);
            }
        }),

        Case("agent-compare.check-summaries-run-ids-and-instructions", () =>
        {
            Equal("2 failed · 36 passed", AgentCheckCommand.Summarize("Failed!  - Failed:     2, Passed:    36, Skipped:     0, Total:    38"), "dotnet test (VSTest)");
            Equal("38 passed", AgentCheckCommand.Summarize("Passed!  - Failed: 0, Passed: 20\nPassed!  - Failed: 0, Passed: 18"), "summed over projects");
            Equal("36 passed", AgentCheckCommand.Summarize("Test run summary: Passed!\n  total: 36\n  failed: 0\n  succeeded: 36"), "dotnet test (MTP)");
            Equal("2 failed · 36 passed", AgentCheckCommand.Summarize("Tests:       2 failed, 36 passed, 38 total"), "Jest");
            Equal<string?>(null, AgentCheckCommand.Summarize("Build succeeded.\n"), "no summary");

            var early = AgentCompareRunner.NewRunId(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
            var later = AgentCompareRunner.NewRunId(new DateTimeOffset(2026, 10, 30, 12, 0, 1, TimeSpan.Zero));
            Check(System.Text.RegularExpressions.Regex.IsMatch(early, "^cmp_[0-9A-HJKMNP-TV-Z]{16}$"), "a run id: " + early);
            Check(string.CompareOrdinal(early[..14], later[..14]) < 0, "run ids sort by time");
            Equal("cmp_X-b", AgentCompareRun.ModelKey("cmp_X", "b"), "a model's key");

            var plain = AgentCompareRunner.Instructions(null);
            var check = AgentCompareRunner.Instructions("dotnet test");
            Check(plain.Contains("its own workspace", StringComparison.Ordinal) && !plain.Contains("check command", StringComparison.Ordinal), "instructions without a check");
            Check(check.StartsWith(plain, StringComparison.Ordinal) && check.Contains("`dotnet test`", StringComparison.Ordinal), "instructions with the check");
            Equal(check, AgentCompareRunner.Instructions("dotnet test"), "the same text every time");
        }),

        Case("agent-compare.create-request-parses-the-wire", () =>
        {
            var request = AgentCompareEndpoints.CreateRequest(new JsonObject
            {
                ["requestId"] = "r1",
                ["task"] = "Fix it",
                ["projectPath"] = "C:\\p",
                ["models"] = new JsonArray(new JsonObject { ["provider"] = "faux", ["model"] = "echo" },
                    new JsonObject { ["provider"] = "faux", ["model"] = "echo-thinking", ["thinking"] = "high", ["account"] = "acc_1" }),
                ["isolation"] = "copy",
                ["capUsd"] = "0.60",
                ["capAction"] = "block",
                ["approval"] = "allowList",
                ["checkCommand"] = "dotnet test",
                ["checkTimeoutSeconds"] = 90,
                ["extraExcludes"] = "dist, out",
                ["confirmLargeCopy"] = true,
            });
            Equal(2, request.Models.Count, "two models");
            Equal("acc_1", request.Models[1].Account, "an account");
            Equal("high", request.Models[1].Thinking, "a thinking level");
            Equal(WorkspaceIsolation.Copy, request.Isolation, "isolation");
            Equal<long?>(600_000, request.CapMicros, "the cap in micro-USD");
            Equal<CapAction?>(CapAction.Block, request.CapAction, "the cap action");
            Equal<ToolApprovalMode?>(ToolApprovalMode.AllowList, request.Approval, "the approval mode");
            Equal<int?>(90, request.CheckTimeoutSeconds, "the check timeout");
            Equal("dist|out", string.Join("|", request.ExtraExcludes!), "excludes from text");
            Check(request.ConfirmLargeCopy, "the confirmation");
            var bad = Throws<AgentCompareException>(() => AgentCompareEndpoints.CreateRequest(new JsonObject { ["approval"] = "never" }), "an unknown mode");
            Equal(400, bad.Status, "is a 400");
            Throws<AgentCompareException>(() => AgentCompareEndpoints.CreateRequest(new JsonObject { ["maxTurns"] = "many" }), "turns as text");
            Throws<AgentCompareException>(() => AgentCompareEndpoints.CreateRequest(new JsonObject { ["capUsd"] = "lots" }), "a cap that is no amount");
        }),

        Case("agent-compare.app.restart-mid-run-resumes-every-model", async () =>
        {
            await using var app = await CompareApp.StartAsync("ac-restart", ToolApprovalMode.Auto);
            var run = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "restart-1",
                ["task"] = "!echo done > result.txt",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo", "echo-small", "echo-thinking"),
                ["approval"] = "ask",
            });
            Equal("a|b|c", string.Join("|", run["order"]!.AsArray().Select(slot => slot!.GetValue<string>()).Order(StringComparer.Ordinal)), "every model is in the start order");
            var waiting = await app.WaitAsync(run.Id(), status => status.Models().All(model => model.Str("waiting") == "approval"),
                "every model waits for its bash call (the run asks, though Settings › Tools is Auto)");
            foreach (var model in waiting.Models())
            {
                var call = model["approvals"]!.AsArray().Single()!;
                Equal("bash", call.Str("tool"), "bash asks");
                Equal("echo done > result.txt", call.Str("command"), "with its command");
                Check(!File.Exists(Path.Join(model.Str("workingDirectory"), "result.txt")), "nothing ran yet");
                Check(model.Str("workingDirectory")!.StartsWith(Path.Join(app.Data, "workspaces"), StringComparison.OrdinalIgnoreCase), "each model works in its workspace");
            }
            Equal(3, waiting["models"]!.AsArray().Select(model => model.Str("workingDirectory")).Distinct().Count(), "three workspaces");
            Equal(3, waiting["waiting"]!.GetValue<int>(), "three calls wait");

            await app.KillAndRestartAsync();
            var resumed = await app.WaitAsync(run.Id(), status => status.Models().All(model => model.Str("waiting") == "approval"),
                "every model asks again after the restart");
            Equal("running", resumed.Str("state"), "the run is running");
            var answered = await app.PostAsync($"/api/compare/agent/{run.Id()}/approve", new JsonObject { ["scope"] = "all", ["items"] = Listed(resumed), ["decision"] = "allow" });
            Equal(200, answered.Status, "approved for all columns: " + answered.Text);
            Equal(3, answered.Body!["answers"]!.AsArray().Count, "three calls answered at once");

            var finished = await app.WaitAsync(run.Id(), status => status.Str("state") == "finished", "the run finished");
            foreach (var model in finished.Models())
            {
                Equal("done", model.Str("phase"), "done");
                Equal("finished", model.Str("outcome"), $"model {model.Str("slot")} finished: {model}");
                Equal("done", File.ReadAllText(Path.Join(model.Str("workingDirectory"), "result.txt")).Trim(), "its command ran in its workspace once");
                Equal(2, model["turns"]!.GetValue<int>(), "two turns: the call and the answer (none repeated by the restart)");
                Equal(1, model["diff"]!["files"]!.GetValue<int>(), "its diff has the file");
                Check(model.Str("answer")!.Contains("Tool finished: bash", StringComparison.Ordinal), "its answer");
            }
            Check(!File.Exists(Path.Join(app.Project, "result.txt")), "the project itself is untouched");

            var runsDb = Path.Join(app.Data, "runs.db");
            var parent = ReadDb(runsDb, $"SELECT kind, status, source FROM runs WHERE id = '{run.Id()}'", row => (row.GetString(0), row.GetString(1), row.GetString(2))).Single();
            Equal(("parent", "completed", "agent-compare"), parent, "one parent row for the run");
            var children = ReadDb(runsDb, $"SELECT id, kind, status FROM runs WHERE parent_id = '{run.Id()}' ORDER BY id", row => (row.GetString(0), row.GetString(1), row.GetString(2)));
            Equal(3, children.Count, "a child row per model");
            Check(children.All(child => child.Item2 == "parent" && child.Item3 == "completed"), "each completed: " + string.Join(", ", children));
            var calls = ReadDb(runsDb, $"SELECT COUNT(*) FROM runs WHERE parent_id = '{run.Id()}-a' AND kind = 'call' AND source = 'agent-compare'", row => row.GetInt64(0)).Single();
            Check(calls >= 2, "model A's provider calls are children of its row: " + calls);

            var kept = await app.PostAsync($"/api/compare/agent/{run.Id()}/apply", new JsonObject { ["slot"] = "a", ["discardOthers"] = true });
            Equal(200, kept.Status, "applied: " + kept.Text);
            Equal("applied", kept.Body!["result"]!.Str("outcome"), "A's changes were applied");
            Equal("done", File.ReadAllText(Path.Join(app.Project, "result.txt")).Trim(), "as uncommitted changes in the project");
            Equal("b|c", string.Join("|", kept.Body!["discarded"]!.AsArray().Select(slot => slot!.GetValue<string>()).Order(StringComparer.Ordinal)), "the others were discarded");
            var again = await app.PostAsync($"/api/compare/agent/{run.Id()}/apply", new JsonObject { ["slot"] = "a" });
            Equal(200, again.Status, "a repeated apply answers what the first did: " + again.Text);
            Equal("applied", again.Body!["result"]!.Str("outcome"), "still applied");
        }),

        Case("agent-compare.app.approvals-answer-every-column", async () =>
        {
            await using var app = await CompareApp.StartAsync("ac-approvals", ToolApprovalMode.Auto);
            var run = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "grant-1",
                ["task"] = "!echo one > one.txt",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo", "echo-small"),
                ["isolation"] = "copy",
            });
            var waiting = await app.WaitAsync(run.Id(), status => status.Models().All(model => model.Str("waiting") == "approval"), "both models ask");
            var callA = waiting.Models().Single(model => model.Str("slot") == "a")["approvals"]!.AsArray().Single()!["taskId"]!.GetValue<long>();
            var granted = await app.PostAsync($"/api/compare/agent/{run.Id()}/approve",
                new JsonObject { ["scope"] = "command", ["slot"] = "a", ["taskId"] = callA, ["decision"] = "allow" });
            Equal(200, granted.Status, "allowed in all workspaces: " + granted.Text);
            Equal(2, granted.Body!["answers"]!.AsArray().Count, "the grant answered both columns' calls");
            Equal("echo one > one.txt", granted.Body!["status"]!["grants"]!.AsArray().Single()!.Str("command"), "the run keeps the grant");
            var stale = await app.PostAsync($"/api/compare/agent/{run.Id()}/approve", new JsonObject { ["slot"] = "a", ["taskId"] = callA, ["decision"] = "allow" });
            Equal(409, stale.Status, "a call already answered no longer waits");
            Equal("not-pending", stale.Body!["error"]!.Str("code"), "its code");
            var finished = await app.WaitAsync(run.Id(), status => status.Str("state") == "finished", "the run finished");
            foreach (var model in finished.Models())
                Check(File.Exists(Path.Join(model.Str("workingDirectory"), "one.txt")), $"model {model.Str("slot")} ran the command");

            // Deny every column at once: no command runs, and each model gets the reason.
            var denied = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "deny-1",
                ["task"] = "!echo two > two.txt",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo", "echo-small"),
                ["isolation"] = "copy",
            });
            var asking = await app.WaitAsync(denied.Id(), status => status.Models().All(model => model.Str("waiting") == "approval"), "both models ask again");
            var no = await app.PostAsync($"/api/compare/agent/{denied.Id()}/approve",
                new JsonObject { ["scope"] = "all", ["items"] = Listed(asking), ["decision"] = "deny", ["reason"] = "not today" });
            Equal(200, no.Status, "denied for all: " + no.Text);
            Equal(2, no.Body!["answers"]!.AsArray().Count, "both calls");
            var done = await app.WaitAsync(denied.Id(), status => status.Str("state") == "finished", "the denied run finished");
            foreach (var model in done.Models())
            {
                Check(!File.Exists(Path.Join(model.Str("workingDirectory"), "two.txt")), "the denied command did not run");
                Check(model.Str("answer")!.Contains("not today", StringComparison.Ordinal), "the model got the reason: " + model.Str("answer"));
            }
        }),

        Case("agent-compare.app.allow-all-answers-only-the-calls-the-page-listed", async () =>
        {
            await using var app = await CompareApp.StartAsync("ac-allow-all", ToolApprovalMode.Auto);
            var run = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "listed-1",
                ["task"] = "!echo listed > listed.txt",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo", "echo-small"),
                ["isolation"] = "copy",
            });
            var waiting = await app.WaitAsync(run.Id(), status => status.Models().All(model => model.Str("waiting") == "approval"), "both models ask");
            var approve = $"/api/compare/agent/{run.Id()}/approve";
            var unlisted = await app.PostAsync(approve, new JsonObject { ["scope"] = "all", ["decision"] = "allow" });
            Equal(400, unlisted.Status, "Allow all must name the calls it answers: " + unlisted.Text);

            // The page showed only A's call: B's started waiting after it rendered, so Allow all leaves it waiting.
            var answered = await app.PostAsync(approve, new JsonObject { ["scope"] = "all", ["items"] = Listed(waiting, "a"), ["decision"] = "allow" });
            Equal(200, answered.Status, "allowed: " + answered.Text);
            Equal("a", answered.Body!["answers"]!.AsArray().Single()!.Str("slot"), "only the listed call was answered");
            var after = answered.Body!["status"]!;
            Equal("approval", after.Models().Single(model => model.Str("slot") == "b").Str("waiting"), "B's call still waits for its own answer");
            Equal(1, after["waiting"]!.GetValue<int>(), "one call waits (the button comes back with the new count)");

            // A listed call that no longer waits is skipped, not an error; B's call is answered once it is listed.
            var later = await app.PostAsync(approve, new JsonObject { ["scope"] = "all", ["items"] = Listed(waiting), ["decision"] = "deny", ["reason"] = "not this one" });
            Equal(200, later.Status, "denied: " + later.Text);
            Equal("b", later.Body!["answers"]!.AsArray().Single()!.Str("slot"), "only B's call was still waiting");
            var finished = await app.WaitAsync(run.Id(), status => status.Str("state") == "finished", "the run finished");
            Check(File.Exists(Path.Join(finished.Models().Single(model => model.Str("slot") == "a").Str("workingDirectory"), "listed.txt")), "A's allowed call ran");
            Check(!File.Exists(Path.Join(finished.Models().Single(model => model.Str("slot") == "b").Str("workingDirectory"), "listed.txt")), "B's call never ran unseen");
        }),

        Case("agent-compare.app.budget-share-stops-only-that-model", async () =>
        {
            // Priced faux models ($3 / $15 per million tokens) reserve up to 32,000 output tokens: echo can reserve at most 8,192 (about
            // $0.13 a turn), echo thinking 32,000 (about $0.49). A $0.60 cap gives each $0.30: echo works, echo thinking is stopped.
            await using var app = await CompareApp.StartAsync("ac-budget", ToolApprovalMode.Auto, metered: true,
                seed: store => store.SaveAsync(SpendSettings.CreateDefault() with { ReservationOutputTokens = 32_000 }));
            var run = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "budget-1",
                ["task"] = "!echo hi > hi.txt",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo", "echo-thinking"),
                ["capMicros"] = 600_000,
                ["capAction"] = "block",
                ["approval"] = "auto",
            });
            var finished = await app.WaitAsync(run.Id(), status => status.Str("state") == "finished", "the run finished");
            var echo = finished.Models().Single(model => model.Str("slot") == "a");
            var thinking = finished.Models().Single(model => model.Str("slot") == "b");
            Equal(300_000L, echo["shareMicros"]!.GetValue<long>(), "an equal share");
            Equal(300_000L, thinking["shareMicros"]!.GetValue<long>(), "for each metered model");
            Equal("finished", echo.Str("outcome"), "echo finished: " + echo);
            Check(File.Exists(Path.Join(echo.Str("workingDirectory"), "hi.txt")), "echo ran its command");
            Equal("budget", thinking.Str("outcome"), "echo thinking was stopped by its share: " + thinking);
            Check(thinking.Str("message")!.Contains("share of the run cap", StringComparison.Ordinal), "it says why: " + thinking.Str("message"));
            Check(!File.Exists(Path.Join(thinking.Str("workingDirectory"), "hi.txt")), "echo thinking ran nothing");
            Equal(0, thinking["turns"]!.GetValue<int>(), "its refused turn is not counted");
            Check(echo["spend"]!["requests"]!.GetValue<int>() >= 2, "echo's requests are under its own run key: " + echo["spend"]);
            Equal(0, thinking["spend"]!["requests"]!.GetValue<int>(), "nothing was spent for echo thinking");
            var runsDb = Path.Join(app.Data, "runs.db");
            var child = ReadDb(runsDb, $"SELECT status FROM runs WHERE id = '{run.Id()}-b'", row => row.GetString(0)).Single();
            Equal("aborted", child, "its runs.db row says it was stopped");
            Equal(0L, ReadDb(runsDb, $"SELECT COUNT(*) FROM runs WHERE parent_id = '{run.Id()}-b'", row => row.GetInt64(0)).Single(), "no call of it was made");

            // Ask: echo thinking pauses at its share; Continue lets one turn through, the next asks again, and Stop ends only it.
            var asked = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "budget-2",
                ["task"] = "!echo hi > hi.txt",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo", "echo-thinking"),
                ["capUsd"] = 0.6,
                ["capAction"] = "ask",
                ["approval"] = "auto",
            });
            var paused = await app.WaitAsync(asked.Id(), status => status.Models().Single(model => model.Str("slot") == "b").Str("waiting") == "budget",
                "echo thinking waits for a budget answer");
            var ask = paused.Models().Single(model => model.Str("slot") == "b")["budgetAsk"]!;
            Equal(300_000L, ask["shareMicros"]!.GetValue<long>(), "the ask names its share");
            Check(ask["nextMicros"]!.GetValue<long>() > 300_000, "and the next turn's worst case: " + ask);
            await app.WaitAsync(asked.Id(), status => status.Models().Single(model => model.Str("slot") == "a").Str("phase") == "done", "echo went on and finished");
            var go = await app.PostAsync($"/api/compare/agent/{asked.Id()}/continue", new JsonObject { ["slot"] = "b", ["scope"] = "turn" });
            Equal(200, go.Status, "continued: " + go.Text);
            await WaitUntilAsync(() => Task.FromResult(File.Exists(Path.Join(paused.Models().Single(model => model.Str("slot") == "b").Str("workingDirectory"), "hi.txt"))),
                "the turn let through ran its call", TimeSpan.FromSeconds(30));
            var again = await app.WaitAsync(asked.Id(), status => status.Models().Single(model => model.Str("slot") == "b").Str("waiting") == "budget",
                "the next turn asks again");
            Equal(1, again.Models().Single(model => model.Str("slot") == "b")["turns"]!.GetValue<int>(), "one turn went through");
            Equal(200, (await app.PostAsync($"/api/compare/agent/{asked.Id()}/stop", new JsonObject { ["slot"] = "b" })).Status, "stopped at the ask");
            var over = await app.WaitAsync(asked.Id(), status => status.Str("state") == "finished", "the run finished");
            Equal("finished", over.Models().Single(model => model.Str("slot") == "a").Str("outcome"), "echo finished");
            Equal("stopped", over.Models().Single(model => model.Str("slot") == "b").Str("outcome"), "echo thinking was stopped");
        }),

        Case("agent-compare.app.check-command-times-out-cleanly", async () =>
        {
            await using var app = await CompareApp.StartAsync("ac-timeout", ToolApprovalMode.Auto);
            var run = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "timeout-1",
                ["task"] = "!echo first > first.txt",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo"),
                ["approval"] = "auto",
                ["checkCommand"] = OperatingSystem.IsWindows() ? "echo started & ping -n 60 127.0.0.1 >nul" : "echo started; sleep 60",
                ["checkTimeoutSeconds"] = 2,
            });
            var finished = await app.WaitAsync(run.Id(), status => status.Str("state") == "finished", "the run finished");
            var model = finished.Models().Single();
            Equal("finished", model.Str("outcome"), "the model finished");
            var check = model["check"]!;
            Equal("timeout", check.Str("state"), "its check timed out: " + check);
            Check(check["exitCode"] is null, "with no exit code");
            Check(check.Str("tail")!.Contains("started", StringComparison.Ordinal) && check.Str("tail")!.Contains("[Stopped after 2 s", StringComparison.Ordinal),
                "its output and the stop are kept: " + check.Str("tail"));
            Check(check["durationMs"]!.GetValue<long>() < 20_000, "it was stopped promptly");
            Check(File.Exists(Path.Join(model.Str("workingDirectory"), "first.txt")), "the model's work is still there");
            var notes = ReadDb(Path.Join(app.Data, "runs.db"), $"SELECT notes FROM runs WHERE id = '{run.Id()}-a'", row => row.GetString(0)).Single();
            Check(notes.Contains("check timeout", StringComparison.Ordinal), "the runs.db row notes the check: " + notes);
        }),

        Case("agent-compare.app.turn-limit-and-stop-end-only-that-model", async () =>
        {
            await using var app = await CompareApp.StartAsync("ac-limits", ToolApprovalMode.Auto);
            var inspected = await app.PostAsync("/api/compare/agent/inspect", new JsonObject { ["projectPath"] = app.Project, ["models"] = 2 });
            Equal(200, inspected.Status, "inspected: " + inspected.Text);
            Equal("copy", inspected.Body!.Str("kind"), "a folder outside git gets temporary copies");

            // One turn each: the first request calls bash, the second (the answer) is refused.
            var limited = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "turns-1",
                ["task"] = "!echo x > x.txt",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo"),
                ["approval"] = "auto",
                ["maxTurns"] = 1,
                ["checkCommand"] = "echo checked",
            });
            var ended = await app.WaitAsync(limited.Id(), status => status.Str("state") == "finished", "the limited run finished");
            var only = ended.Models().Single();
            Equal("turn-limit", only.Str("outcome"), "the turn limit stopped it: " + only);
            Equal(1, only["turns"]!.GetValue<int>(), "after one turn");
            Check(File.Exists(Path.Join(only.Str("workingDirectory"), "x.txt")), "its first turn's call ran");
            Equal("passed", only["check"]!.Str("state"), "a model a limit stopped is still checked");

            // Stop one model while both wait: the other goes on.
            var run = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "stop-1",
                ["task"] = "!echo y > y.txt",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo", "echo-small"),
                ["approval"] = "ask",
                ["checkCommand"] = "echo checked",
            });
            await app.WaitAsync(run.Id(), status => status.Models().All(model => model.Str("waiting") == "approval"), "both models ask");
            var stopped = await app.PostAsync($"/api/compare/agent/{run.Id()}/stop", new JsonObject { ["slot"] = "b" });
            Equal(200, stopped.Status, "stopped: " + stopped.Text);
            await app.WaitAsync(run.Id(), status => status.Models().Single(model => model.Str("slot") == "b").Str("phase") == "done", "B is done");
            var during = await app.StatusAsync(run.Id());
            Equal("approval", during.Models().Single(model => model.Str("slot") == "a").Str("waiting"), "A still waits for its call");
            var taskA = during.Models().Single(model => model.Str("slot") == "a")["approvals"]!.AsArray().Single()!["taskId"]!.GetValue<long>();
            Equal(200, (await app.PostAsync($"/api/compare/agent/{run.Id()}/approve", new JsonObject { ["slot"] = "a", ["taskId"] = taskA })).Status, "A's call allowed");
            var finished = await app.WaitAsync(run.Id(), status => status.Str("state") == "finished", "the run finished");
            var a = finished.Models().Single(model => model.Str("slot") == "a");
            var b = finished.Models().Single(model => model.Str("slot") == "b");
            Equal("finished", a.Str("outcome"), "A finished");
            Check(File.Exists(Path.Join(a.Str("workingDirectory"), "y.txt")), "A ran its command");
            Equal("stopped", b.Str("outcome"), "B was stopped");
            Check(!File.Exists(Path.Join(b.Str("workingDirectory"), "y.txt")), "B's command never ran");
            Check(b["check"] is null, "a model the user stopped is not checked");
            Equal(200, (await app.PostAsync($"/api/compare/agent/{run.Id()}/stop", new JsonObject())).Status, "stopping a finished run changes nothing");
            var discarded = await app.PostAsync($"/api/compare/agent/{run.Id()}/discard", new JsonObject());
            Equal(200, discarded.Status, "every workspace discarded: " + discarded.Text);
            Check(!Directory.Exists(a.Str("workspacePath")), "A's workspace is gone");
            Check(File.Exists(Path.Join(app.Project, "README.md")) && !File.Exists(Path.Join(app.Project, "y.txt")), "the project is as it was");
        }),

        Case("agent-compare.app.open-as-chat-continues-in-its-workspace", async () =>
        {
            await using var app = await CompareApp.StartAsync("ac-chat", ToolApprovalMode.Auto);
            var invalid = await app.PostAsync("/api/compare/agent", new JsonObject { ["task"] = "x" });
            Equal(400, invalid.Status, "a create without a request id");
            Equal("invalid", invalid.Body!["error"]!.Str("code"), "its code");
            Equal(404, (await app.App.Get("/api/compare/agent/cmp_missing")).Status, "an unknown run");
            var body = new JsonObject
            {
                ["requestId"] = "chat-1",
                ["task"] = "!echo first > first.txt",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo"),
                ["approval"] = "auto",
                ["checkCommand"] = "echo checked",
            };
            var run = await app.CreateAsync(body);
            Equal(run.Id(), (await app.CreateAsync((JsonObject)body.DeepClone())).Id(), "a repeated create answers the same run");
            var finished = await app.WaitAsync(run.Id(), status => status.Str("state") == "finished", "the run finished");
            var model = finished.Models().Single();
            Equal("passed", model["check"]!.Str("state"), "its check passed");
            Equal(0, model["check"]!["exitCode"]!.GetValue<int>(), "exit 0");
            var cwd = model.Str("workingDirectory")!;
            var diff = await app.App.Get($"/api/compare/agent/{run.Id()}/diff?slot=a");
            Equal(200, diff.Status, "the diff: " + diff.Text);
            Check(diff.Text.Contains("first.txt", StringComparison.Ordinal), "names the file");
            var events = await app.App.Get($"/api/compare/agent/{run.Id()}/events");
            Check(events.Text.StartsWith("event: status\ndata: {", StringComparison.Ordinal), "the event stream sends the status: " + events.Text[..Math.Min(80, events.Text.Length)]);
            var list = await app.App.Get("/api/compare/agent");
            Check(list.Body!["items"]!.AsArray().Any(item => item.Str("id") == run.Id()), "the run is listed");

            var opened = await app.PostAsync($"/api/compare/agent/{run.Id()}/open-chat", new JsonObject { ["slot"] = "a" });
            Equal(200, opened.Status, "opened as a chat: " + opened.Text);
            var chat = opened.Body!.Str("chatId")!;
            Equal(model.Str("sessionId"), chat, "the chat is the model's session");
            Equal("/chat/" + chat, opened.Body!.Str("url"), "its page");
            Equal(chat, (await app.PostAsync($"/api/compare/agent/{run.Id()}/open-chat", new JsonObject { ["slot"] = "a" })).Body!.Str("chatId"), "opening again answers the same chat");
            Equal(200, (await app.App.Get($"/api/chats/{chat}")).Status, "the chat exists");

            var sent = await app.PostAsync($"/api/chats/{chat}/messages", new JsonObject { ["requestId"] = "more-1", ["text"] = "!echo more > more.txt" });
            Equal(200, sent.Status, "sent: " + sent.Text);
            await WaitUntilAsync(() => Task.FromResult(File.Exists(Path.Join(cwd, "more.txt"))), "the chat's command ran in the workspace", TimeSpan.FromSeconds(45));
            Check(!File.Exists(Path.Join(app.Project, "more.txt")), "not in the project");
            Check(File.Exists(Path.Join(cwd, "first.txt")), "next to the model's work");

            var discard = await app.PostAsync($"/api/compare/agent/{run.Id()}/discard", new JsonObject { ["slot"] = "a" });
            Equal(409, discard.Status, "a workspace a chat works in is not discarded");
            Equal("in-chat", discard.Body!["error"]!.Str("code"), "its code");
            var status = await app.StatusAsync(run.Id());
            Equal("/chat/" + chat, status.Models().Single().Str("chatUrl"), "the status links the chat");
        }),
    ];

    private static JsonArray Models(params string[] models) =>
        new([.. models.Select(model => (JsonNode)new JsonObject { ["provider"] = "faux", ["model"] = model })]);

    // The waiting calls a status shows (of the given slots, else all), as the page sends them with "Allow all": [{slot, taskId}].
    private static JsonArray Listed(JsonNode status, params string[] slots) =>
        new([.. status.Models().Where(model => slots.Length == 0 || slots.Contains(model.Str("slot") ?? ""))
            .SelectMany(model => (model["approvals"] as JsonArray ?? []).Select(call =>
                (JsonNode)new JsonObject { ["slot"] = model.Str("slot"), ["taskId"] = call!["taskId"]!.GetValue<long>() }))]);

    // A path for a shell or a git config value: forward slashes (sh on Windows reads them), quoted for the shell.
    private static string QuotedShellPath(string path) => "\"" + path.Replace('\\', '/') + "\"";

    // A hook or helper script git runs through sh; executable where that matters.
    private static void WriteScript(string path, string body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string Id(this JsonNode status) => status.Str("id")!;

    private static string? Str(this JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static IEnumerable<JsonNode> Models(this JsonNode status) => status["models"]!.AsArray().Select(model => model!);

    /// <summary>The app with Settings › Tools seeded (<c>work</c>, <paramref name="tools"/>) and a small project folder outside git.</summary>
    private sealed class CompareApp : IAsyncDisposable
    {
        private readonly AppStart start;

        private CompareApp(string root, AppStart start, WebAgentApp app)
        {
            Root = root;
            this.start = start;
            App = app;
        }

        public string Root { get; }

        public string Data => Path.Join(Root, "data");

        public string Project => Path.Join(Root, "project");

        public WebAgentApp App { get; private set; }

        public static async Task<CompareApp> StartAsync(string name, ToolApprovalMode tools, bool metered = false, Func<SettingsStore, Task>? seed = null)
        {
            var root = Temp(name);
            try
            {
                var data = Path.Join(root, "data");
                Directory.CreateDirectory(data);
                Directory.CreateDirectory(Path.Join(root, "work"));
                Directory.CreateDirectory(Path.Join(root, "project", "src"));
                File.WriteAllText(Path.Join(root, "project", "README.md"), "# Sample\n");
                File.WriteAllText(Path.Join(root, "project", "src", "app.txt"), "one\n");
                var gitConfig = Path.Join(root, "gitconfig");
                File.WriteAllText(gitConfig, "");
                var db = new WebAgentDb(Path.Join(data, "webagent.db"), [CoreMigrations.AppInfo, ChatBindings.Migration, SettingsStore.Migration]);
                db.Migrate();
                var store = new SettingsStore(db);
                await store.SaveAsync(new ToolsSettings(Path.Join(root, "work"), tools, []));
                if (seed is not null) await seed(store);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var start = new AppStart
                {
                    DataDirectory = data,
                    Arguments = metered ? ["--faux-metered"] : [],
                    Environment = new Dictionary<string, string> { ["GIT_CONFIG_GLOBAL"] = gitConfig, ["GIT_CONFIG_NOSYSTEM"] = "1" },
                };
                return new CompareApp(root, start, await WebAgentApp.StartAsync(root, start));
            }
            catch
            {
                DeleteTemp(root);
                throw;
            }
        }

        /// <summary>Kills the app (as a crash would) and starts it again on the same data.</summary>
        public async Task KillAndRestartAsync()
        {
            await App.DisposeAsync();
            App = await WebAgentApp.StartAsync(Root, start);
        }

        public Task<(int Status, JsonNode? Body, string Text, HttpResponseMessage Response)> PostAsync(string path, JsonObject body) =>
            App.SendAsync(HttpMethod.Post, path, body);

        /// <summary>Creates a run; returns its status.</summary>
        public async Task<JsonNode> CreateAsync(JsonObject body)
        {
            var created = await PostAsync("/api/compare/agent", body);
            Equal(200, created.Status, "created: " + created.Text);
            return created.Body!;
        }

        public async Task<JsonNode> StatusAsync(string id)
        {
            var status = await App.Get("/api/compare/agent/" + id);
            Equal(200, status.Status, "status: " + status.Text);
            return status.Body!;
        }

        /// <summary>Polls the run's status until <paramref name="condition"/> holds (60 s at most).</summary>
        public async Task<JsonNode> WaitAsync(string id, Func<JsonNode, bool> condition, string what)
        {
            JsonNode? last = null;
            try
            {
                await WaitUntilAsync(async () => condition(last = await StatusAsync(id)), what, TimeSpan.FromSeconds(60));
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException(error.Message + ". Last status: " + last?.ToJsonString() + "\nApp log: " + App.Log, error);
            }
            return last!;
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            DeleteTemp(Root);
        }
    }
}
