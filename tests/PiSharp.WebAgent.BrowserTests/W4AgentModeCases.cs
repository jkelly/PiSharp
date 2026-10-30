using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W4, fix 2, tests-ci): agent compare's run controls in a browser (compare-agent-setup.js and compare-agent-run.js), on
// faux (the models answer "!command" with a bash call in their own workspace), failing on any page or console error. T4's case covers Ask
// with Allow all and Keep by applying; these cover the rest:
// - a command grant: three models ask for the same command, the approval banner offers "Allow this command in all 3 workspaces" with its
//   not-a-sandbox warning, and one click lets every column through (the run's banner then says what was allowed everywhere);
// - Allow-list, chosen in Limits and approvals (its warning visible): a command on Settings › Tools' allow-list runs with no prompt;
// - Auto in workspaces with a one-turn limit: the command runs with no prompt, and the limit then stops each model ("Turn limit");
// - Keep on a git repository: the user changes the model's file after the Keep dialog opened, so Apply is refused (the refused pane names
//   the file, nothing in the project changes); Keep the branch instead commits the change on pisharp/compare/<run>-a without switching the
//   project's branch, committing to it or touching the user's change;
// - Stop: one column stops while both wait for approval, then Stop all (after its confirm) stops the other; no command ran.
// The repository cases run under BrowseGit (an empty global git config); the others copy a plain folder.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W4AgentModeCases() =>
    [
        Browse("browser.w4.agent-compare.allow-this-command-in-all-three-workspaces", async (app, window) =>
        {
            var project = app.W4Project();
            var page = window.Page;
            await W4AgentSetupAsync(page, project, "!echo granted > granted.txt");

            // A third model, through the model picker.
            var picker = await page.OpenModalAsync(page.Locator("[data-ac-add-model]"), "modal-model-picker");
            await picker.Locator("#mp-model-faux").SelectOptionAsync("echo-thinking");
            await picker.Locator("[type='submit']").ClickAsync();
            await Expect(picker).ToBeHiddenAsync();
            await Expect(page.Locator("[data-ac-setup-col]")).ToHaveCountAsync(3);
            await Expect(page.Locator("[data-ac-setup-status]")).ToContainTextAsync("3 agents");

            await page.Locator("[data-ac-run]").ClickAsync();
            var run = await page.QueryAsync("run");
            var root = page.Locator("[data-agent-compare]");
            await Expect(root).ToHaveAttributeAsync("data-agent-compare", "run");

            // The first call asks; the banner offers this command in every workspace, and says workspaces are not sandboxes.
            var grant = page.Locator("[data-ac-approve-command] >> visible=true").First;
            await Expect(grant).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Expect(grant).ToHaveTextAsync("Allow this command in all 3 workspaces");
            var warning = page.Locator("[data-ac-grant-warning]");
            await Expect(warning).ToBeVisibleAsync();
            await Expect(warning).ToContainTextAsync("not a sandbox");
            await Expect(page.Locator("[data-ac-approval-banner]")).ToContainTextAsync("echo granted > granted.txt");
            await grant.ClickAsync();

            // Every column runs it (the ones that asked later too) with no other prompt.
            foreach (var slot in new[] { "a", "b", "c" })
            {
                var column = page.Locator($"[data-ac-col][data-slot='{slot}']");
                await Expect(column).ToHaveAttributeAsync("data-view", "done", new() { Timeout = 30_000 });
                await Expect(column).ToContainTextAsync("granted.txt");
            }
            await Expect(root).ToHaveAttributeAsync("data-state", "finished");
            var sandbox = page.Locator("[data-ac-sandbox]");
            await Expect(sandbox).ToBeVisibleAsync();
            await Expect(sandbox).ToContainTextAsync("Allowed in every workspace for this run: echo granted > granted.txt");
            await Expect(sandbox).ToContainTextAsync("not a sandbox");
            var status = (await app.Get("/api/compare/agent/" + run)).Body!;
            Equal("ask", status["approval"]?.ToString().ToLowerInvariant(), "the run's approval stays Ask");
            Equal(1, status["grants"]?.AsArray().Count, "one grant: " + status["grants"]?.ToJsonString());
            foreach (var model in status["models"]!.AsArray())
                Check(File.Exists(Path.Join(model!["workspacePath"]!.GetValue<string>(), "granted.txt")), $"{model["slot"]}'s command ran in its workspace");
            Check(!File.Exists(Path.Join(project, "granted.txt")), "nothing reached the project");
        }),

        Browse("browser.w4.agent-compare.allow-list-and-auto-run-without-asking-and-a-turn-limit-stops-each-model", async (app, window) =>
        {
            var tools = await app.PostAsync("/api/settings/tools", new JsonObject
            {
                ["workingDirectory"] = app.Work,
                ["approvalMode"] = "ask",
                ["allowList"] = new JsonArray(new JsonObject { ["tool"] = "bash", ["command"] = "echo listed *" }),
            });
            Equal(200, tools.Status, "an allow-list rule: " + tools.Text);
            var project = app.W4Project();
            var page = window.Page;

            // Allow-list: the warning shows on the choice, and the listed command runs with no prompt.
            await W4AgentSetupAsync(page, project, "!echo listed > listed.txt");
            await W4AgentLimitsAsync(page);
            await page.Locator("#acApprovalList").CheckAsync();
            await Expect(page.Locator("#acAllowWarn")).ToBeVisibleAsync();
            await Expect(page.Locator("#acAllowWarn")).ToContainTextAsync("not a sandbox");
            await Expect(page.Locator("[data-ac-limits-summary]")).ToContainTextAsync("Allow-list");
            await page.Locator("[data-ac-run]").ClickAsync();
            var listed = await page.QueryAsync("run");
            await W4AgentDoneAsync(page, "listed.txt");
            await Expect(page.Locator("[data-ac-approval-banner]")).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-ac-sandbox]")).ToContainTextAsync("Tool approval is Allow-list");
            await W4AgentFilesAsync(app, listed, "listed.txt");

            // Auto in workspaces, one turn each: the command runs with no prompt, then the turn limit stops each model.
            await W4AgentSetupAsync(page, project, "!echo auto > auto.txt");
            await W4AgentLimitsAsync(page);
            await page.Locator("#acApprovalAuto").CheckAsync();
            await Expect(page.Locator("#acAutoWarn")).ToBeVisibleAsync();
            await Expect(page.Locator("#acAutoWarn")).ToContainTextAsync("not a sandbox");
            await page.Locator("#acMaxTurns").FillAsync("1");
            await Expect(page.Locator("[data-ac-limits-summary]")).ToContainTextAsync("· 1 turns ·");
            await Expect(page.Locator("[data-ac-limits-summary]")).ToContainTextAsync("Auto in workspaces");
            await page.Locator("[data-ac-run]").ClickAsync();
            var auto = await page.QueryAsync("run");
            Check(auto != listed, "a second run");
            await W4AgentDoneAsync(page, null);
            foreach (var slot in new[] { "a", "b" })
                await Expect(page.Locator($"[data-ac-col][data-slot='{slot}'] [data-ac-outcome-chip]")).ToHaveTextAsync("Turn limit");
            await Expect(page.Locator("[data-ac-approval-banner]")).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-ac-sandbox]")).ToContainTextAsync("Tool approval is Auto in workspaces");
            await page.Locator("[data-bs-target='#acRunDetails']").ClickAsync();
            await Expect(page.Locator("#acRunDetails")).ToContainTextAsync("1 turns and");
            await W4AgentFilesAsync(app, auto, "auto.txt");
            Check(!File.Exists(Path.Join(project, "listed.txt")) && !File.Exists(Path.Join(project, "auto.txt")), "nothing reached the project");
        }),

        BrowseGit("browser.w4.agent-compare.a-refused-apply-then-keep-the-branch", AgentMode.Faux, async (app, window, git) =>
        {
            var repo = await git.RepoAsync("repo", ("README.md", "# Repo\n"), ("src/app.txt", "one\n"));
            var head = await git.GitAsync(repo, "rev-parse", "HEAD");
            var status = await app.AgentCompareAsync("keep-branch-1", "!echo two > src/app.txt", repo, "worktree", "echo", "echo-small");
            var run = status["id"]!.GetValue<string>();
            var page = window.Page;
            await page.GotoAsync("/compare?mode=agent&run=" + run);
            await Expect(page.Locator("[data-agent-compare]")).ToHaveAttributeAsync("data-state", "finished");

            // The dialog opens on Apply; the user then changes the same file in the project, so the Apply is refused.
            var keep = await page.OpenModalAsync(page.Locator("[data-ac-col][data-slot='a'] [data-ac-keep] >> visible=true").First, "modal-keep-result");
            await Expect(keep.Locator("[data-ac-keep-content]")).ToHaveAttributeAsync("data-pane", "options");
            await Expect(keep.Locator("#acKeepApply")).ToBeCheckedAsync();
            await Expect(keep.Locator("#acKeepBranch")).ToBeEnabledAsync();
            await Expect(keep).ToContainTextAsync("pisharp/compare/" + run + "-a");
            await File.WriteAllTextAsync(Path.Join(repo, "src", "app.txt"), "mine\n");
            await keep.Locator("[data-ac-keep-submit] >> visible=true").First.ClickAsync();
            var refused = keep.Locator("[data-ac-keep-pane='refused']");
            await Expect(refused).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Expect(keep.Locator("[data-ac-keep-content]")).ToHaveAttributeAsync("data-pane", "refused");
            await Expect(refused.Locator("[data-ac-refused-message]")).Not.ToBeEmptyAsync();
            await Expect(refused.Locator("[data-ac-refused-files]")).ToContainTextAsync("src/app.txt");
            await Expect(refused.Locator("[data-ac-refused-files]")).ToContainTextAsync("changed in both");
            await Expect(keep.Locator(".modal-title")).ToContainTextAsync("Can't apply A");
            Equal("mine", File.ReadAllText(Path.Join(repo, "src", "app.txt")).Trim(), "the user's change is untouched");
            Equal(head, await git.GitAsync(repo, "rev-parse", "HEAD"), "no commit");

            // Try again finds the conflict before anything is sent.
            await keep.Locator("[data-ac-keep-retry] >> visible=true").First.ClickAsync();
            await Expect(keep.Locator("[data-ac-keep-content]")).ToHaveAttributeAsync("data-pane", "conflict", new() { Timeout = 15_000 });
            await Expect(keep.Locator("[data-ac-keep-pane='conflict']")).ToContainTextAsync("src/app.txt");

            // Keep the branch instead: it selects the branch (the click that keeps is still the user's), then keeps it.
            await keep.Locator("[data-ac-keep-branch-instead] >> visible=true").First.ClickAsync();
            await Expect(keep.Locator("[data-ac-keep-content]")).ToHaveAttributeAsync("data-pane", "options");
            await Expect(keep.Locator("#acKeepBranch")).ToBeCheckedAsync();
            Equal("main", await git.GitAsync(repo, "branch", "--format=%(refname:short)"), "nothing kept before the click");
            await keep.Locator("[data-ac-keep-submit] >> visible=true").First.ClickAsync();
            await Expect(keep).ToBeHiddenAsync(new() { Timeout = 30_000 });
            await page.ToastAsync("Branch kept");
            var banner = page.Locator("[data-ac-kept='a']");
            await Expect(banner).ToContainTextAsync("Branch kept");
            await Expect(banner).ToContainTextAsync("pisharp/compare/" + run + "-a");
            await Expect(banner).ToContainTextAsync("nothing was pushed");
            await Expect(page.Locator("[data-ac-col][data-slot='a']")).ToContainTextAsync("Branch kept");

            var branch = "pisharp/compare/" + run + "-a";
            Equal("main", await git.GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD"), "the project stays on its branch");
            Equal(head, await git.GitAsync(repo, "rev-parse", "HEAD"), "and its commit");
            Equal("mine", File.ReadAllText(Path.Join(repo, "src", "app.txt")).Trim(), "the user's change is still there");
            Equal("M src/app.txt", await git.GitAsync(repo, "status", "--porcelain"), "and still uncommitted, with nothing else changed");
            Equal("two", await git.GitAsync(repo, "show", branch + ":src/app.txt"), "the branch holds A's change");
            Equal(head, await git.GitAsync(repo, "rev-parse", branch + "~1"), "as one commit on the project's commit");
            Equal("", await git.GitAsync(repo, "remote"), "no remote, so nothing could be pushed");
        }),

        Browse("browser.w4.agent-compare.stop-one-model-then-stop-all", async (app, window) =>
        {
            var project = app.W4Project();
            var created = await app.PostAsync("/api/compare/agent", new JsonObject
            {
                ["requestId"] = "stop-1",
                ["task"] = "!echo never > never.txt",
                ["projectPath"] = project,
                ["models"] = new JsonArray(new JsonObject { ["provider"] = "faux", ["model"] = "echo" }, new JsonObject { ["provider"] = "faux", ["model"] = "echo-small" }),
                ["approval"] = "ask",
                ["isolation"] = "copy",
            });
            Equal(200, created.Status, "an agent compare run: " + created.Text);
            var run = created.Body!["id"]!.GetValue<string>();
            var page = window.Page;
            await page.GotoAsync("/compare?mode=agent&run=" + run);
            var root = page.Locator("[data-agent-compare]");
            await Expect(page.Locator("[data-ac-col][data-view='approval']")).ToHaveCountAsync(2, new() { Timeout = 30_000 });

            // Stop A from its column: B keeps waiting.
            await page.Locator("[data-ac-col][data-slot='a'] [data-ac-stop='a']").ClickAsync();
            var a = page.Locator("[data-ac-col][data-slot='a']");
            await Expect(a).ToHaveAttributeAsync("data-view", "done", new() { Timeout = 15_000 });
            await Expect(a.Locator("[data-ac-outcome-chip]")).ToHaveTextAsync("Stopped");
            await Expect(page.Locator("[data-ac-col][data-slot='b']")).ToHaveAttributeAsync("data-view", "approval");
            await Expect(root).ToHaveAttributeAsync("data-active", "true");

            // Stop all asks first, then stops B.
            await page.Locator("[data-ac-stop-all]").ClickAsync();
            await page.ModalShownAsync("modal-confirm");
            await Expect(page.Locator("#modal-confirm")).ToContainTextAsync("Stop all agents?");
            await page.Locator("#modal-confirm [data-confirm-ok]").ClickAsync();
            var b = page.Locator("[data-ac-col][data-slot='b']");
            await Expect(b).ToHaveAttributeAsync("data-view", "done", new() { Timeout = 15_000 });
            await Expect(b.Locator("[data-ac-outcome-chip]")).ToHaveTextAsync("Stopped");
            await Expect(root).ToHaveAttributeAsync("data-active", "false", new() { Timeout = 15_000 });
            await Expect(page.Locator("[data-ac-stop-all]")).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-ac-new-run]")).ToBeVisibleAsync();

            var status = (await app.Get("/api/compare/agent/" + run)).Body!;
            foreach (var model in status["models"]!.AsArray())
            {
                Equal("stopped", model!["outcome"]?.GetValue<string>(), $"{model["slot"]} was stopped: " + model.ToJsonString());
                Check(!File.Exists(Path.Join(model["workspacePath"]!.GetValue<string>(), "never.txt")), $"{model["slot"]}'s command never ran");
            }
        }),
    ];

    /// <summary>Opens a new agent run's setup on <paramref name="project"/> with <paramref name="task"/> (two faux models, the defaults).</summary>
    private static async Task W4AgentSetupAsync(IPage page, string project, string task)
    {
        await page.GotoAsync("/compare?mode=agent");
        await Expect(page.Locator("[data-agent-compare]")).ToHaveAttributeAsync("data-agent-compare", "setup");
        await Expect(page.Locator("[data-ac-setup-col]")).ToHaveCountAsync(2);
        await page.Locator("[aria-label='Change project folder']").ClickAsync();
        await Expect(page.Locator("[data-ac-folder-menu]")).ToBeVisibleAsync();
        await page.Locator("#acFolder").FillAsync(project);
        await page.Locator("[data-ac-folder-use]").ClickAsync();
        await Expect(page.Locator("[data-ac-path]")).ToHaveTextAsync(project);
        await Expect(page.Locator("[data-ac-iso-host]")).ToContainTextAsync(new System.Text.RegularExpressions.Regex("cop(y|ies)"), new() { Timeout = 15_000 });
        await page.Locator("#acTask").FillAsync(task);
    }

    /// <summary>Opens Limits and approvals.</summary>
    private static async Task W4AgentLimitsAsync(IPage page)
    {
        if (!await page.Locator("#acLimits").IsVisibleAsync()) await page.Locator(".ac-limits-toggle").ClickAsync();
        await Expect(page.Locator("#acLimits")).ToBeVisibleAsync();
    }

    /// <summary>Waits until both columns of the open run are done (each naming <paramref name="file"/> when given) and the run finished.</summary>
    private static async Task W4AgentDoneAsync(IPage page, string? file)
    {
        await Expect(page.Locator("[data-agent-compare]")).ToHaveAttributeAsync("data-agent-compare", "run");
        foreach (var slot in new[] { "a", "b" })
        {
            var column = page.Locator($"[data-ac-col][data-slot='{slot}']");
            await Expect(column).ToHaveAttributeAsync("data-view", "done", new() { Timeout = 30_000 });
            if (file is not null) await Expect(column).ToContainTextAsync(file);
        }
        await Expect(page.Locator("[data-agent-compare]")).ToHaveAttributeAsync("data-state", "finished", new() { Timeout = 15_000 });
    }

    /// <summary>Checks that every model of run <paramref name="run"/> wrote <paramref name="file"/> in its own workspace.</summary>
    private static async Task W4AgentFilesAsync(Agent app, string run, string file)
    {
        var status = (await app.Get("/api/compare/agent/" + run)).Body!;
        foreach (var model in status["models"]!.AsArray())
            Check(File.Exists(Path.Join(model!["workspacePath"]!.GetValue<string>(), file)), $"{model["slot"]}'s {file} is in its workspace: " + model.ToJsonString());
    }
}
