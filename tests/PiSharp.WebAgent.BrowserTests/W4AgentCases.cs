using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W4, T4): agent compare and its workspaces in a browser (faux: the models answer "!command" with a bash call, so each
// edits its own workspace; failing on any page or console error). End to end on a temporary git repository (BrowseGit: the app's git and
// the case's see an empty global config and no system config): Compare's agent mode takes the task and the folder (Change › Another folder;
// the isolation chip names a worktree from main), keeps Ask as the approval while Limits and approvals shows the not-a-sandbox warning on
// the choice that runs tools without asking, runs two models that each ask before their command, "Allow all" lets both through, each column ends with its diff (the diff dialog shows the new file), and Keep › Apply puts A's
// change in the project as an uncommitted file: no commit, HEAD and the branch unchanged, no branch made, B's workspace left alone. History
// then expands the run into its two models. Data & storage lists the kept workspaces of a three-model run on a plain folder (copies): Remove
// takes one after its confirmation, and Clean up the rest; the project is untouched.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> W4AgentCases() =>
    [
        BrowseGit("browser.w4.agent-compare.git-repo-setup-run-diff-and-keep-by-apply", AgentMode.Faux, async (app, window, git) =>
        {
            var repo = await git.RepoAsync("repo", ("README.md", "# Repo\n"), ("src/app.txt", "one\n"));
            var head = await git.GitAsync(repo, "rev-parse", "HEAD");
            var page = window.Page;
            await page.GotoAsync("/compare?mode=agent");
            var root = page.Locator("[data-agent-compare]");
            await Expect(root).ToHaveAttributeAsync("data-agent-compare", "setup");
            await Expect(page.Locator("[data-ac-setup-col]")).ToHaveCountAsync(2);

            // The folder (Change › Another folder): a git repository, so each model gets a worktree from main.
            await page.Locator("[aria-label='Change project folder']").ClickAsync();
            await Expect(page.Locator("[data-ac-folder-menu]")).ToBeVisibleAsync();
            await page.FitsPhoneAsync("the folder menu", "dark");
            await page.Locator("#acFolder").FillAsync(repo);
            await page.Locator("[data-ac-folder-use]").ClickAsync();
            await Expect(page.Locator("[data-ac-path]")).ToContainTextAsync("repo");
            await Expect(page.Locator("[data-ac-iso-host]")).ToContainTextAsync("git worktree from main @ " + head[..7]);

            // Limits and approvals: Ask is the default, and the choices that run tools without asking say, visibly, that workspaces are
            // copies, not sandboxes.
            await page.Locator(".ac-limits-toggle").ClickAsync();
            await Expect(page.Locator("#acLimits")).ToBeVisibleAsync();
            await Expect(page.Locator("#acApprovalAsk")).ToBeCheckedAsync();
            await Expect(page.Locator("#acAutoWarn")).ToBeVisibleAsync();
            await Expect(page.Locator("#acAutoWarn")).ToContainTextAsync("not a sandbox");

            await page.Locator("#acTask").FillAsync("!echo kept > kept.txt");
            await page.Locator("[data-ac-run]").ClickAsync();
            var run = await page.QueryAsync("run");
            await Expect(root).ToHaveAttributeAsync("data-agent-compare", "run");

            // Both models ask; Allow all lets both commands through.
            var allowAll = page.Locator("[data-ac-approve-all='allow'] >> visible=true").First;
            await Expect(allowAll).ToBeVisibleAsync(new() { Timeout = 30_000 });
            await Expect(page.Locator("[data-ac-col][data-view='approval']")).ToHaveCountAsync(2);
            await allowAll.ClickAsync();
            foreach (var slot in new[] { "a", "b" })
            {
                var column = page.Locator($"[data-ac-col][data-slot='{slot}']");
                await Expect(column).ToHaveAttributeAsync("data-view", "done", new() { Timeout = 30_000 });
                await Expect(column).ToContainTextAsync("kept.txt");
            }
            await Expect(root).ToHaveAttributeAsync("data-state", "finished");
            var status = (await app.Get("/api/compare/agent/" + run)).Body!;
            var workspaceB = status["models"]!.AsArray().Single(model => model!["slot"]!.GetValue<string>() == "b")!["workingDirectory"]!.GetValue<string>();
            Check(File.Exists(Path.Join(workspaceB, "kept.txt")), "B's change is in its own worktree");
            Check(!File.Exists(Path.Join(repo, "kept.txt")), "nothing reached the project before Keep");

            // A's diff.
            var diff = await page.OpenModalAsync(page.Locator("[data-ac-col][data-slot='a'] [data-ac-diff] >> visible=true").First, "modal-agent-diff");
            await Expect(diff).ToContainTextAsync("kept.txt");
            await Expect(diff).ToContainTextAsync("+kept");
            await page.DismissModalAsync("modal-agent-diff");

            // Keep A by applying it to the project.
            var keep = await page.OpenModalAsync(page.Locator("[data-ac-col][data-slot='a'] [data-ac-keep] >> visible=true").First, "modal-keep-result");
            await Expect(keep.Locator("#acKeepApply")).ToBeCheckedAsync();
            await keep.Locator("[data-ac-keep-submit] >> visible=true").First.ClickAsync();
            await Expect(keep).ToBeHiddenAsync(new() { Timeout = 30_000 });
            var banners = page.Locator("[data-ac-region='banners']");
            await Expect(banners).ToContainTextAsync("Applied");
            await Expect(banners).ToContainTextAsync("Nothing was committed or pushed");

            Equal("kept", File.ReadAllText(Path.Join(repo, "kept.txt")).Trim(), "A's file is in the project");
            Equal("?? kept.txt", await git.GitAsync(repo, "status", "--porcelain"), "as an uncommitted change, and nothing else changed");
            Equal(head, await git.GitAsync(repo, "rev-parse", "HEAD"), "no commit");
            Equal("main", await git.GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD"), "the project stays on its branch");
            Equal("main", await git.GitAsync(repo, "branch", "--format=%(refname:short)"), "no branch was made");
            Check(File.Exists(Path.Join(workspaceB, "kept.txt")), "B's workspace is left as it was");

            // History expands the run into its models.
            await page.GotoAsync("/history");
            var row = page.Locator($"tr#run-{run}");
            await Expect(row).ToHaveAttributeAsync("data-run-kind", "parent");
            await Expect(row).ToContainTextAsync("Agent compare");
            var models = page.Locator($"[data-run-models='{run}']");
            await Expect(models).ToBeHiddenAsync();
            await row.Locator("[data-bs-toggle='collapse']").ClickAsync();
            await Expect(models).ToBeVisibleAsync();
            await Expect(models).ToContainTextAsync("echo-small");
            await Expect(models.Locator("a[title='Inspect']")).ToHaveCountAsync(2);
        }),

        Browse("browser.w4.data.compare-workspaces-remove-one-then-clean-up", async (app, window) =>
        {
            var project = app.W4Project();
            var status = await app.AgentCompareAsync("data-ws-1", "!echo hi > hi.txt", project, "copy", "echo", "echo-small", "echo-thinking");
            var run = status["id"]!.GetValue<string>();
            var workspaces = status["models"]!.AsArray().ToDictionary(model => model!["slot"]!.GetValue<string>(), model => model!["workspacePath"]!.GetValue<string>());
            Check(workspaces.Values.All(Directory.Exists), "three workspaces on disk");

            var page = window.Page;
            await page.GotoAsync("/settings/data#compare-workspaces");
            var rows = page.Locator("[data-ws-row]");
            await Expect(rows).ToHaveCountAsync(3);
            await Expect(page.Locator("[data-ws-cleanup]")).ToBeEnabledAsync();

            // Remove one, after its confirmation.
            var rowA = page.Locator($"[data-ws-row][data-run='{run}'][data-slot='a']");
            await rowA.Locator("[data-bs-toggle='dropdown']").ClickAsync();
            var confirm = await page.OpenModalAsync(rowA.Locator("[data-ws-remove]"), "modal-data-confirm");
            await Expect(confirm.Locator("[data-confirm-ok]")).ToHaveTextAsync("Remove workspace");
            await confirm.Locator("[data-confirm-ok]").ClickAsync();
            await page.ToastAsync("Workspace removed");
            await Expect(rows).ToHaveCountAsync(2);
            await Expect(rowA).ToHaveCountAsync(0);
            Check(!Directory.Exists(workspaces["a"]), "A's workspace is gone");
            Check(Directory.Exists(workspaces["b"]) && Directory.Exists(workspaces["c"]), "the others stay");

            // Clean up the rest.
            confirm = await page.OpenModalAsync(page.Locator("[data-ws-cleanup]"), "modal-data-confirm");
            await Expect(confirm.Locator("[data-confirm-ok]")).ToHaveTextAsync("Remove workspaces");
            await confirm.Locator("[data-confirm-ok]").ClickAsync();
            await page.ToastAsync("2 workspaces removed");
            await Expect(rows).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-pane='ac-ws-empty']")).ToBeVisibleAsync();
            Check(workspaces.Values.All(path => !Directory.Exists(path)), "every workspace is gone");
            Check(File.Exists(Path.Join(project, "README.md")) && !File.Exists(Path.Join(project, "hi.txt")), "the project is as it was");
            Equal(200, (await app.Get("/api/runs/" + run)).Status, "the run stays in History");
        }),
    ];
}
