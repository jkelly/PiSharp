using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PiSharp.WebAgent.AgentCompare.Workspaces;
using PiSharp.WebAgent.Hosting;

// Decision 0011 (run W4, unit R4): agent compare's WorkspaceManager, on temporary git repositories and folders only (git runs with an
// empty global config and no system config, so the user's settings can't change what is checked; nothing touches the user's data folder,
// repositories or branches). Each git worktree starts from the base commit; the project's uncommitted changes are listed and left out;
// copies skip the excluded folders and .gitignore; a file written in one workspace never appears in another; diffs include new, deleted and
// binary files; Apply refuses when the project has uncommitted changes in the same files; Keep the branch creates the branch and leaves the
// current one; Discard leaves `git worktree list` clean; the cleanup removes leftovers and expired runs; paths stay inside their roots; git
// runs with argument lists.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> WorkspaceCases() =>
    [
        Case("workspaces.worktrees-start-from-the-base-commit", async () =>
        {
            await using var lab = WsLab.Create("base");
            var repo = await lab.RepoAsync("project", ("a.txt", "one\n"), ("src/app.cs", "class App {}\n"));
            var first = await lab.GitAsync(repo, "rev-parse", "HEAD");
            File.WriteAllText(Path.Join(repo, "a.txt"), "two\n");
            File.WriteAllText(Path.Join(repo, "b.txt"), "bee\n");
            await lab.CommitAllAsync(repo, "second");
            var second = await lab.GitAsync(repo, "rev-parse", "HEAD");

            var inspection = await lab.Manager.InspectAsync(repo);
            Check(inspection.GitAvailable && inspection.IsGitRepo && inspection.WorktreeUnavailable is null, "a git repository with a commit");
            Equal(WorkspaceKind.Worktree, inspection.Kind, "automatic isolation picks worktrees for a git repo");
            Equal("main", inspection.CurrentBranch, "the current branch");
            Equal(second, inspection.HeadCommit, "the default base is HEAD");
            Check(inspection.ShortCommit is { Length: >= 7 } shortId && second.StartsWith(shortId, StringComparison.Ordinal), "the abbreviated commit");
            Equal("git worktree from main @ " + inspection.ShortCommit, inspection.Label, "the isolation chip");
            Check(inspection.Branches.Contains("main"), "the local branches");
            Equal((long?)("two\n".Length + "class App {}\n".Length + "bee\n".Length), inspection.CheckoutBytes, "one worktree's size, from git's tree");
            Equal(0, inspection.Uncommitted.Count, "a clean project");
            Equal(lab.Root, inspection.WorkspacesRoot, "where workspaces live");

            var request = new WorkspaceRequest { Run = "cmp_head", ProjectPath = repo, Slots = WsSlots("a", "b", "c") };
            var run = await lab.Manager.CreateAsync(request);
            Equal(WorkspaceKind.Worktree, run.Kind, "worktrees");
            Equal(second, run.BaseCommit, "the base commit");
            Equal("main", run.BaseBranch, "the base branch");
            Equal("main @ " + run.ShortCommit, run.BaseLabel, "what diffs are against");
            Equal(run.CreatedAt + TimeSpan.FromDays(7), run.ExpiresAt, "kept for 7 days");
            foreach (var slot in run.Slots)
            {
                Equal(Path.Join(lab.Root, "cmp_head", slot.Slot), slot.Path, "the workspace is <root>\\<run>\\<slot>");
                Equal(slot.Path, slot.WorkingDirectory, "the agent works at the workspace's root");
                Equal(second, await lab.GitAsync(slot.Path, "rev-parse", "HEAD"), slot.Slot + " starts at the base commit");
                Check(!(await lab.TryGitAsync(slot.Path, "symbolic-ref", "-q", "HEAD")).Ok, slot.Slot + " is detached (no branch of the user's is checked out)");
                Equal("two\n", File.ReadAllText(Path.Join(slot.Path, "a.txt")), slot.Slot + " has the base content");
            }
            Equal("main", await lab.GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD"), "the project stays on its branch");

            var older = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_first", ProjectPath = repo, Slots = WsSlots("a"), BaseRef = first });
            Equal(first, await lab.GitAsync(older.Slots[0].Path, "rev-parse", "HEAD"), "an older base commit");
            Check(!File.Exists(Path.Join(older.Slots[0].Path, "b.txt")), "the older commit's files only");
            Equal<string?>(null, older.BaseBranch, "a commit as the base names no branch");
            var named = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_named", ProjectPath = repo, Slots = WsSlots("a"), BaseRef = "main" });
            Equal("main", named.BaseBranch, "a branch as the base");

            var again = await lab.Manager.CreateAsync(request);
            Equal(run.CreatedAt, again.CreatedAt, "asking again for a ready run answers it as it is");
            var clash = await ThrowsAsync<WorkspaceException>(() => lab.Manager.CreateAsync(request with { Slots = WsSlots("a", "b") }), "other slots for the same run");
            Equal(WorkspaceException.Exists, clash.Code, "the run exists");
            var unknown = await ThrowsAsync<WorkspaceException>(() => lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_bad", ProjectPath = repo, Slots = WsSlots("a"), BaseRef = "no-such-branch" }), "an unknown base");
            Equal(WorkspaceException.Invalid, unknown.Code, "an unknown base is refused");
            Check(!Directory.Exists(Path.Join(lab.Root, "cmp_bad")), "a refused run leaves nothing behind");

            var sub = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_sub", ProjectPath = Path.Join(repo, "src"), Slots = WsSlots("a") });
            Equal("src", sub.ProjectSubpath, "a project in a sub-folder of the repository");
            Equal(Path.Join(sub.Slots[0].Path, "src"), sub.Slots[0].WorkingDirectory, "the agent works in the same sub-folder of its worktree");
            Check(File.Exists(Path.Join(sub.Slots[0].WorkingDirectory, "app.cs")), "the sub-folder is checked out");

            // A sub-folder that isn't in the last commit (new, or ignored) has nothing to check out, so automatic isolation copies it.
            var fresh = Path.Join(repo, "fresh");
            WsWrite(fresh, ("new.txt", "not committed\n"));
            var freshInspection = await lab.Manager.InspectAsync(fresh);
            Check(freshInspection.Kind == WorkspaceKind.Copy && freshInspection.WorktreeUnavailable is { Length: > 0 }, "a new sub-folder gets a copy");
            var freshRun = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_fresh", ProjectPath = fresh, Slots = WsSlots("a") });
            Equal(WorkspaceKind.Copy, freshRun.Kind, "copied");
            Equal("new.txt", string.Join(",", WsFiles(freshRun.Slots[0].Path)), "with its files");
        }),

        Case("workspaces.uncommitted-changes-are-listed-and-left-out", async () =>
        {
            await using var lab = WsLab.Create("dirty");
            var repo = await lab.RepoAsync("project", ("a.txt", "committed\n"), ("d.txt", "doomed\n"), ("keep.txt", "keep\n"));
            File.WriteAllText(Path.Join(repo, "a.txt"), "dirty\n");
            File.WriteAllText(Path.Join(repo, "u.txt"), "untracked\n");
            File.WriteAllText(Path.Join(repo, "s.txt"), "staged\n");
            await lab.GitAsync(repo, "add", "s.txt");
            File.Delete(Path.Join(repo, "d.txt"));
            var before = await lab.GitAsync(repo, "status", "--porcelain");

            var inspection = await lab.Manager.InspectAsync(repo);
            var listed = inspection.Uncommitted.ToDictionary(change => change.Path, change => change.Status);
            Equal("modified", listed.GetValueOrDefault("a.txt"), "a modified file is listed");
            Equal("untracked", listed.GetValueOrDefault("u.txt"), "an untracked file is listed");
            Equal("added", listed.GetValueOrDefault("s.txt"), "a staged new file is listed");
            Equal("deleted", listed.GetValueOrDefault("d.txt"), "a deleted file is listed");
            Equal(4, listed.Count, "nothing else");

            var run = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_dirty", ProjectPath = repo, Slots = WsSlots("a", "b") });
            Equal(4, run.NotCopied.Count, "the run records what it did not copy");
            Check(run.NotCopied.Any(change => change.Path == "a.txt" && change.Status == "modified"), "the modified file is not copied");
            foreach (var slot in run.Slots)
            {
                Equal("committed\n", File.ReadAllText(Path.Join(slot.Path, "a.txt")), slot.Slot + " has the committed version, not the dirty one");
                Check(!File.Exists(Path.Join(slot.Path, "u.txt")), slot.Slot + " has no untracked file");
                Check(!File.Exists(Path.Join(slot.Path, "s.txt")), slot.Slot + " has no staged file");
                Check(File.Exists(Path.Join(slot.Path, "d.txt")), slot.Slot + " still has the file deleted in the project");
            }
            Equal(before, await lab.GitAsync(repo, "status", "--porcelain"), "the project's own changes are untouched");
            Equal("dirty\n", File.ReadAllText(Path.Join(repo, "a.txt")), "the project keeps its edit");
        }),

        Case("workspaces.copies-skip-excluded-folders", async () =>
        {
            await using var lab = WsLab.Create("copy");
            var project = lab.Folder("plain project");
            WsWrite(project,
                (".gitignore", "*.log\n!keep.log\nsecret/\n/rootonly.txt\n# a comment\n"),
                ("src/app.cs", "class App {}\n"),
                ("src/bin/tool.exe", "binary"),
                ("bin/x.dll", "dll"),
                ("obj/project.assets.json", "{}"),
                ("node_modules/pkg/index.js", "module.exports = 1;"),
                (".vs/state", "editor"),
                (".git/HEAD", "ref: refs/heads/main\n"),
                ("logs/run.log", "log"),
                ("keep.log", "kept\n"),
                ("secret/key.txt", "not copied"),
                ("rootonly.txt", "ignored at the root"),
                ("deep/sub/rootonly.txt", "anchored patterns only match at their folder\n"),
                ("sub/.gitignore", "local.txt\n"),
                ("sub/local.txt", "ignored by the nested file"),
                ("sub/kept.txt", "kept\n"),
                ("artifacts/out.txt", "an extra exclude"));
            string[] expected = [".gitignore", "deep/sub/rootonly.txt", "keep.log", "src/app.cs", "sub/.gitignore", "sub/kept.txt"];
            var expectedBytes = expected.Sum(path => new FileInfo(Path.Join(project, path)).Length);

            var inspection = await lab.Manager.InspectAsync(project, new WorkspaceInspectOptions { ExtraExcludes = ["artifacts", "  ", "# note"] });
            Check(!inspection.IsGitRepo && inspection.WorktreeUnavailable is { Length: > 0 }, "not a git repository");
            Equal(WorkspaceKind.Copy, inspection.Kind, "automatic isolation picks a copy");
            Equal(expected.Length, inspection.CopyFiles, "the files a copy holds");
            Equal((long?)expectedBytes, inspection.CopyBytes, "the copy's size counts only what is copied");
            Equal("bin,obj,node_modules,.vs,.git,artifacts", string.Join(",", inspection.Excludes), "the excludes, defaults first");
            Check(inspection.Label.StartsWith("temporary copy · ", StringComparison.Ordinal), "the copy chip");
            Check(!inspection.OverWarnLimit, "small");

            var run = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_copy", ProjectPath = project, Slots = WsSlots("a", "b"), ExtraExcludes = ["artifacts"] });
            Equal(WorkspaceKind.Copy, run.Kind, "copies");
            Equal((long?)expectedBytes, run.CopyBytes, "the copy's size");
            Equal(string.Join(",", expected), string.Join(",", WsFiles(Path.Join(run.Directory, WorkspaceManager.BaseFolder))), "the base copy skips the excluded and ignored files");
            foreach (var slot in run.Slots)
                Equal(string.Join(",", expected), string.Join(",", WsFiles(slot.Path)), slot.Slot + " skips the excluded and ignored files");
            Equal(0, run.NotCopied.Count, "a copy includes uncommitted changes");

            var worktrees = await ThrowsAsync<WorkspaceException>(() => lab.Manager.CreateAsync(new WorkspaceRequest
            { Run = "cmp_wt", ProjectPath = project, Slots = WsSlots("a"), Isolation = WorkspaceIsolation.Worktree }), "worktrees of a plain folder");
            Equal(WorkspaceException.NotGit, worktrees.Code, "worktrees need a git repository");

            // Above the warning size a copy needs the user's go-ahead; refused, it leaves nothing behind.
            var big = await lab.Manager.InspectAsync(project, new WorkspaceInspectOptions { WarnAboveBytes = 10 });
            Check(big.OverWarnLimit, "the inspection warns above the limit");
            var refused = await ThrowsAsync<WorkspaceException>(() => lab.Manager.CreateAsync(new WorkspaceRequest
            { Run = "cmp_big", ProjectPath = project, Slots = WsSlots("a"), WarnAboveBytes = 10 }), "a large copy without the go-ahead");
            Equal(WorkspaceException.TooLarge, refused.Code, "too large");
            Check(!Directory.Exists(Path.Join(lab.Root, "cmp_big")), "a refused copy leaves nothing behind");
            var confirmed = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_big", ProjectPath = project, Slots = WsSlots("a"), WarnAboveBytes = 10, ConfirmLargeCopy = true });
            Check(Directory.Exists(confirmed.Slots[0].Path), "a confirmed large copy is made");

            // A git repository copied on request: uncommitted changes included, .git and ignored files not.
            var repo = await lab.RepoAsync("repo", (".gitignore", "*.tmp\n"), ("a.txt", "committed\n"));
            File.WriteAllText(Path.Join(repo, "a.txt"), "dirty\n");
            File.WriteAllText(Path.Join(repo, "scratch.tmp"), "ignored");
            File.WriteAllText(Path.Join(repo, ".git", "info", "exclude"), "private.txt\n");
            File.WriteAllText(Path.Join(repo, "private.txt"), "excluded by info/exclude");
            var copied = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_repo_copy", ProjectPath = repo, Slots = WsSlots("a"), Isolation = WorkspaceIsolation.Copy });
            Equal(WorkspaceKind.Copy, copied.Kind, "a copy when asked for");
            Equal(".gitignore,a.txt", string.Join(",", WsFiles(copied.Slots[0].Path)), "no .git, ignored or excluded files");
            Equal("dirty\n", File.ReadAllText(Path.Join(copied.Slots[0].Path, "a.txt")), "the copy has the uncommitted change");
        }),

        Case("workspaces.a-file-written-in-one-workspace-never-appears-in-another", async () =>
        {
            await using var lab = WsLab.Create("apart");
            var repo = await lab.RepoAsync("repo", ("shared.txt", "base\n"));
            var plain = lab.Folder("plain");
            WsWrite(plain, ("shared.txt", "base\n"));
            foreach (var (name, project) in new[] { ("cmp_wt", repo), ("cmp_cp", plain) })
            {
                var run = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = name, ProjectPath = project, Slots = WsSlots("a", "b", "c") });
                var a = run.Slots[0].Path;
                File.WriteAllText(Path.Join(a, "new.txt"), "only in a\n");
                File.WriteAllText(Path.Join(a, "shared.txt"), "changed in a\n");
                Directory.CreateDirectory(Path.Join(a, "made"));
                File.WriteAllText(Path.Join(a, "made", "deep.txt"), "only in a\n");
                foreach (var other in run.Slots.Skip(1))
                {
                    Check(!File.Exists(Path.Join(other.Path, "new.txt")), $"{name}: {other.Slot} has no file written in a");
                    Check(!Directory.Exists(Path.Join(other.Path, "made")), $"{name}: {other.Slot} has no folder made in a");
                    Equal("base\n", File.ReadAllText(Path.Join(other.Path, "shared.txt")), $"{name}: {other.Slot} keeps its own copy of a file a changed");
                    Equal(0, (await lab.Manager.DiffAsync(name, other.Slot)).Files.Count, $"{name}: {other.Slot}'s diff is empty");
                }
                Equal("base\n", File.ReadAllText(Path.Join(project, "shared.txt")), name + ": the project is untouched");
                Check(!File.Exists(Path.Join(project, "new.txt")), name + ": nothing appears in the project");
                if (run.Kind == WorkspaceKind.Copy)
                    Equal("base\n", File.ReadAllText(Path.Join(run.Directory, WorkspaceManager.BaseFolder, "shared.txt")), "the base copy is untouched");
                Equal(3, (await lab.Manager.DiffAsync(name, "a")).Files.Count, name + ": a's own diff has its three files");
            }
            Equal("", await lab.GitAsync(repo, "status", "--porcelain"), "the repository is clean");
        }),

        Case("workspaces.diffs-include-new-deleted-and-binary-files", async () =>
        {
            await using var lab = WsLab.Create("diff");
            var repo = await lab.RepoAsync("repo", (".gitignore", "*.tmp\n"), ("a.txt", "one\ntwo\nthree\n"), ("b.txt", "bee\n"), ("old.bin", "\0old\0"));
            var plain = lab.Folder("plain");
            WsWrite(plain, (".gitignore", "*.tmp\n"), ("a.txt", "one\ntwo\nthree\n"), ("b.txt", "bee\n"), ("old.bin", "\0old\0"));
            foreach (var (name, project) in new[] { ("cmp_wt", repo), ("cmp_cp", plain) })
            {
                var run = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = name, ProjectPath = project, Slots = WsSlots("a", "b") });
                var a = run.Slots[0].Path;
                File.WriteAllText(Path.Join(a, "a.txt"), "one\nTWO\nthree\n");
                File.Delete(Path.Join(a, "b.txt"));
                File.WriteAllText(Path.Join(a, "n.txt"), "new\nfile\n");
                File.WriteAllBytes(Path.Join(a, "pic.bin"), [0, 1, 2, 0, 255, 0]);
                File.WriteAllBytes(Path.Join(a, "old.bin"), [0, 9, 9, 0]);
                WsWrite(a, ("bin/Debug/app.dll", "build output"), ("obj/x.json", "{}"), ("scratch.tmp", "ignored"));
                if (run.Kind == WorkspaceKind.Worktree)
                {
                    // An agent's own commit in its worktree is still part of its changes against the base.
                    File.WriteAllText(Path.Join(a, "c.txt"), "committed by the agent\n");
                    await lab.GitAsync(a, "add", "c.txt");
                    await lab.GitAsync(a, "commit", "-q", "-m", "agent commit");
                }

                var diff = await lab.Manager.DiffAsync(name, "a");
                var files = diff.Files.ToDictionary(file => file.Path);
                var expected = run.Kind == WorkspaceKind.Worktree ? "a.txt,b.txt,c.txt,n.txt,old.bin,pic.bin" : "a.txt,b.txt,n.txt,old.bin,pic.bin";
                Equal(expected, string.Join(",", diff.Files.Select(file => file.Path)), name + ": changed files in path order, without build output or ignored files");
                Equal(WorkspaceFileChange.Modified, files["a.txt"].Change, name + ": modified");
                Equal((1, 1), (files["a.txt"].Added, files["a.txt"].Removed), name + ": one line changed");
                Check(files["a.txt"].Hunks.Single().Lines.Any(line => line.Kind == WorkspaceDiffLineKind.Added && line.Text == "TWO"), name + ": the added line");
                Check(files["a.txt"].Hunks.Single().Lines.Any(line => line.Kind == WorkspaceDiffLineKind.Removed && line.Text == "two"), name + ": the removed line");
                Equal(WorkspaceFileChange.Deleted, files["b.txt"].Change, name + ": deleted");
                Equal(1, files["b.txt"].Removed, name + ": a deleted file's lines are removed");
                Equal(WorkspaceFileChange.Added, files["n.txt"].Change, name + ": new");
                Equal(2, files["n.txt"].Added, name + ": a new file's lines are added");
                Check(files["pic.bin"].Binary && files["pic.bin"].Change == WorkspaceFileChange.Added && files["pic.bin"].Hunks.Count == 0, name + ": a new binary file");
                Check(files["old.bin"].Binary && files["old.bin"].Change == WorkspaceFileChange.Modified, name + ": a changed binary file");
                Check(diff.Patch.Contains("GIT binary patch", StringComparison.Ordinal), name + ": binary files come as git binary patches");
                Check(diff.Patch.Contains("diff --git a/n.txt b/n.txt", StringComparison.Ordinal), name + ": headers name the relative path");
                Check(!diff.Patch.Contains(run.Directory, StringComparison.OrdinalIgnoreCase) && !diff.Patch.Contains("base/", StringComparison.Ordinal),
                    name + ": no workspace path in the patch");
                Equal(run.Kind == WorkspaceKind.Worktree ? "6 files · +4 \u22122" : "5 files · +3 \u22122", diff.Summary, name + ": the summary");
                Equal(run.Kind == WorkspaceKind.Worktree ? "main @ " + run.ShortCommit : "the base copy", diff.BaseLabel, name + ": against the base");
                Equal("no changes", (await lab.Manager.DiffAsync(name, "b")).Summary, name + ": an untouched workspace has no changes");
                if (run.Kind == WorkspaceKind.Worktree)
                    Check((await lab.GitAsync(a, "status", "--porcelain")).Contains("?? n.txt", StringComparison.Ordinal),
                        "the worktree's own index is untouched (the new file is still untracked there)");
            }
        }),

        Case("workspaces.apply-refuses-on-conflicting-dirty-files", async () =>
        {
            await using var lab = WsLab.Create("apply");
            var repo = await lab.RepoAsync("repo", ("a.txt", "one\n"), ("other.txt", "other\n"), ("gone.txt", "bye\n"));
            var head = await lab.GitAsync(repo, "rev-parse", "HEAD");
            var run = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_apply", ProjectPath = repo, Slots = WsSlots("a", "b") });
            File.WriteAllText(Path.Join(run.Slots[0].Path, "a.txt"), "aaa\n");
            File.WriteAllText(Path.Join(run.Slots[0].Path, "n.txt"), "new\n");
            File.Delete(Path.Join(run.Slots[0].Path, "gone.txt"));
            File.WriteAllText(Path.Join(run.Slots[1].Path, "a.txt"), "bbb\n");

            File.WriteAllText(Path.Join(repo, "a.txt"), "the user's edit\n");
            var preview = await lab.Manager.PreviewKeepAsync("cmp_apply", "a");
            Equal("a.txt,gone.txt,n.txt", string.Join(",", preview.Files), "the slot's files");
            Equal("a.txt", string.Join(",", preview.Conflicts.Select(change => change.Path)), "the same file has uncommitted changes");
            Check(!preview.CanApply && preview.CanKeepBranch, "Apply is not offered; Keep the branch is");
            Equal("pisharp/compare/cmp_apply-a", preview.Branch, "the branch it would keep");
            var refused = await lab.Manager.ApplyAsync("cmp_apply", "a");
            Equal(KeepOutcome.Refused, refused.Outcome, "refused");
            Equal(KeepResult.Refusals.Conflict, refused.Refusal, "because of the conflict");
            Equal("a.txt", string.Join(",", refused.Conflicts.Select(change => change.Path)), "naming the file");
            Equal("the user's edit\n", File.ReadAllText(Path.Join(repo, "a.txt")), "the user's edit is untouched");
            Check(!File.Exists(Path.Join(repo, "n.txt")) && File.Exists(Path.Join(repo, "gone.txt")), "nothing was applied");

            await lab.GitAsync(repo, "checkout", "--", "a.txt");
            File.WriteAllText(Path.Join(repo, "other.txt"), "the user's other edit\n");
            preview = await lab.Manager.PreviewKeepAsync("cmp_apply", "a");
            Check(preview.CanApply && preview.Conflicts.Count == 0, "no conflict now");
            Equal("other.txt", string.Join(",", preview.OtherUncommitted.Select(change => change.Path)), "the other uncommitted file is listed as left alone");
            var applied = await lab.Manager.ApplyAsync("cmp_apply", "a");
            Equal(KeepOutcome.Applied, applied.Outcome, "applied: " + applied.Message);
            Equal("aaa\n", File.ReadAllText(Path.Join(repo, "a.txt")), "the change is in the project");
            Equal("new\n", File.ReadAllText(Path.Join(repo, "n.txt")), "the new file is in the project");
            Check(!File.Exists(Path.Join(repo, "gone.txt")), "the deleted file is gone from the project");
            Equal("the user's other edit\n", File.ReadAllText(Path.Join(repo, "other.txt")), "the other edit is left alone");
            Equal(head, await lab.GitAsync(repo, "rev-parse", "HEAD"), "nothing was committed");
            Equal("main", await lab.GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD"), "the branch is the same");
            Equal("", await lab.GitAsync(repo, "diff", "--cached", "--name-only"), "nothing was staged: the changes are uncommitted");
            Check((await lab.Manager.GetAsync("cmp_apply"))!.Find("a")!.AppliedAt is not null, "the run records the apply");
            Check(Directory.Exists(run.Slots[0].Path), "the workspace is kept after Apply");

            // b changed the same file differently; once the user commits a's version, b's patch no longer applies.
            Equal(KeepResult.Refusals.Conflict, (await lab.Manager.ApplyAsync("cmp_apply", "b")).Refusal, "b's file now has uncommitted changes");
            await lab.CommitAllAsync(repo, "took a's change");
            var stale = await lab.Manager.ApplyAsync("cmp_apply", "b");
            Equal(KeepResult.Refusals.DoesNotApply, stale.Refusal, "a patch that no longer applies is refused");
            Equal("aaa\n", File.ReadAllText(Path.Join(repo, "a.txt")), "and changes nothing");

            // Line endings: a repository with core.autocrlf=true (Windows' default) applies cleanly too.
            var crlf = await lab.RepoAsync("crlf", ("lines.txt", "line1\nline2\nline3\n"));
            await lab.GitAsync(crlf, "config", "core.autocrlf", "true");
            File.Delete(Path.Join(crlf, "lines.txt"));
            await lab.GitAsync(crlf, "checkout", "--", "lines.txt");
            var crlfRun = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_crlf", ProjectPath = crlf, Slots = WsSlots("a") });
            File.WriteAllText(Path.Join(crlfRun.Slots[0].Path, "lines.txt"), "line1\r\nline2 changed\r\nline3\r\n");
            var crlfDiff = await lab.Manager.DiffAsync("cmp_crlf", "a");
            Equal((1, 1), (crlfDiff.Files.Single().Added, crlfDiff.Files.Single().Removed), "line endings don't make every line change");
            Equal(KeepOutcome.Applied, (await lab.Manager.ApplyAsync("cmp_crlf", "a")).Outcome, "applied to a CRLF working tree");
            Check(File.ReadAllText(Path.Join(crlf, "lines.txt")).Replace("\r\n", "\n", StringComparison.Ordinal) == "line1\nline2 changed\nline3\n", "the change is in the project");

            // A copy: "uncommitted" means changed in the project since the copy was made.
            var plain = lab.Folder("plain");
            WsWrite(plain, ("f.txt", "original\n"), ("old.txt", "old\n"));
            var copy = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_copy", ProjectPath = plain, Slots = WsSlots("a") });
            WsWrite(copy.Slots[0].Path, ("f.txt", "from a\n"), ("dir/new.txt", "new\n"));
            File.Delete(Path.Join(copy.Slots[0].Path, "old.txt"));
            File.WriteAllText(Path.Join(plain, "f.txt"), "edited in the project\n");
            var copyRefused = await lab.Manager.ApplyAsync("cmp_copy", "a");
            Equal(KeepResult.Refusals.Conflict, copyRefused.Refusal, "a copy's apply refuses a file changed in the project since the copy");
            Equal("changed since the copy", copyRefused.Conflicts.Single().Status, "and says why");
            Check(!File.Exists(Path.Join(plain, "dir", "new.txt")) && File.Exists(Path.Join(plain, "old.txt")), "nothing was applied");
            File.WriteAllText(Path.Join(plain, "f.txt"), "original\n");
            Equal(KeepOutcome.Applied, (await lab.Manager.ApplyAsync("cmp_copy", "a")).Outcome, "applied once the project matches the copy again");
            Equal("from a\n", File.ReadAllText(Path.Join(plain, "f.txt")), "the copy's change");
            Equal("new\n", File.ReadAllText(Path.Join(plain, "dir", "new.txt")), "the copy's new file");
            Check(!File.Exists(Path.Join(plain, "old.txt")), "the copy's deletion");
            Equal(KeepResult.Refusals.NotGit, (await lab.Manager.KeepBranchAsync("cmp_copy", "a")).Refusal, "Keep the branch needs worktrees");
        }),

        Case("workspaces.keep-branch-creates-the-branch-and-leaves-the-current-one", async () =>
        {
            await using var lab = WsLab.Create("keep");
            var repo = await lab.RepoAsync("repo", ("a.txt", "one\n"), ("b.txt", "bee\n"));
            var head = await lab.GitAsync(repo, "rev-parse", "HEAD");
            File.WriteAllText(Path.Join(repo, "x.txt"), "the user's untracked file\n");
            var statusBefore = await lab.GitAsync(repo, "status", "--porcelain");
            var run = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_keep", ProjectPath = repo, Slots = [new("a", "anthropic/claude-sonnet-5-5"), new("b"), new("c")] });
            var a = run.Slots[0].Path;
            File.WriteAllText(Path.Join(a, "a.txt"), "changed\n");
            File.WriteAllText(Path.Join(a, "n.txt"), "new\n");
            File.Delete(Path.Join(a, "b.txt"));

            var kept = await lab.Manager.KeepBranchAsync("cmp_keep", "a");
            Equal(KeepOutcome.BranchCreated, kept.Outcome, "kept: " + kept.Message);
            Equal("pisharp/compare/cmp_keep-a", kept.Branch, "the branch name");
            Equal(kept.Commit, await lab.GitAsync(repo, "rev-parse", "refs/heads/pisharp/compare/cmp_keep-a"), "the branch points at the new commit");
            Equal(head, await lab.GitAsync(repo, "rev-parse", kept.Commit + "^1"), "its parent is the base commit");
            Equal("1", await lab.GitAsync(repo, "rev-list", "--count", head + ".." + kept.Commit), "one commit");
            Equal("changed", await lab.GitAsync(repo, "show", kept.Commit + ":a.txt"), "the changed file");
            Equal("new", await lab.GitAsync(repo, "show", kept.Commit + ":n.txt"), "the new file");
            Equal("", await lab.GitAsync(repo, "ls-tree", "--name-only", kept.Commit!, "b.txt"), "the deleted file");
            Check((await lab.GitAsync(repo, "log", "-1", "--format=%B", kept.Commit!)).Contains("anthropic/claude-sonnet-5-5", StringComparison.Ordinal), "the message names the model");
            Equal("main", await lab.GitAsync(repo, "rev-parse", "--abbrev-ref", "HEAD"), "the user's branch is not switched");
            Equal(head, await lab.GitAsync(repo, "rev-parse", "HEAD"), "the user's branch has no new commit");
            Equal(statusBefore, await lab.GitAsync(repo, "status", "--porcelain"), "the project's working tree and index are untouched");
            Equal("one\n", File.ReadAllText(Path.Join(repo, "a.txt")), "the project's files are untouched");
            Equal(head, await lab.GitAsync(a, "rev-parse", "HEAD"), "the worktree stays at the base");
            Equal("", await lab.GitAsync(repo, "remote"), "no remote was added (nothing to push to)");
            Equal("pisharp/compare/cmp_keep-a", (await lab.Manager.GetAsync("cmp_keep"))!.Find("a")!.Branch, "the run records the branch");

            var again = await lab.Manager.KeepBranchAsync("cmp_keep", "a");
            Check(again.Outcome == KeepOutcome.BranchCreated && again.AlreadyExisted && again.Commit == kept.Commit, "keeping again answers the same branch");
            Equal(KeepResult.Refusals.NothingToKeep, (await lab.Manager.KeepBranchAsync("cmp_keep", "b")).Refusal, "an unchanged workspace has nothing to keep");

            File.WriteAllText(Path.Join(run.Slots[2].Path, "a.txt"), "c's change\n");
            await lab.GitAsync(repo, "branch", "pisharp/compare/cmp_keep-c", head);
            var exists = await lab.Manager.KeepBranchAsync("cmp_keep", "c");
            Equal(KeepResult.Refusals.BranchExists, exists.Refusal, "an existing branch is never moved");
            Equal(head, await lab.GitAsync(repo, "rev-parse", "refs/heads/pisharp/compare/cmp_keep-c"), "the existing branch is unchanged");
        }),

        Case("workspaces.discard-leaves-git-worktree-list-clean", async () =>
        {
            await using var lab = WsLab.Create("discard");
            var repo = await lab.RepoAsync("repo", ("a.txt", "one\n"));
            var mainOnly = await lab.GitAsync(repo, "worktree", "list", "--porcelain");
            var run = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_disc", ProjectPath = repo, Slots = WsSlots("a", "b", "c") });
            foreach (var slot in run.Slots) WsWrite(slot.Path, ("bin/Debug/out.dll", "build"), ("notes.txt", "untracked"));
            File.SetAttributes(Path.Join(run.Slots[0].Path, "notes.txt"), FileAttributes.ReadOnly);
            Equal(4, WsWorktrees(await lab.GitAsync(repo, "worktree", "list", "--porcelain")), "the project and three worktrees");
            var measured = await lab.Manager.ListAsync(measure: true);
            Check(measured.Single().Slots.All(slot => slot.Bytes > 0), "Settings › Data can show each workspace's disk use");

            Check(await lab.Manager.DiscardAsync("cmp_disc", "a"), "a is discarded");
            Check(!Directory.Exists(run.Slots[0].Path), "a's folder is gone (read-only files too)");
            Equal(3, WsWorktrees(await lab.GitAsync(repo, "worktree", "list", "--porcelain")), "a's worktree is unregistered");
            Equal(WorkspaceSlotState.Discarded, (await lab.Manager.GetAsync("cmp_disc"))!.Find("a")!.State, "the run records the discard");
            Equal(WorkspaceException.NotFound, (await ThrowsAsync<WorkspaceException>(() => lab.Manager.DiffAsync("cmp_disc", "a"), "a discarded diff")).Code, "a discarded workspace has no diff");
            Check(!await lab.Manager.DiscardAsync("cmp_disc", "a"), "discarding again does nothing");
            Check(await lab.Manager.DiscardAsync("cmp_disc", "b") && await lab.Manager.DiscardAsync("cmp_disc", "c"), "b and c are discarded");
            Check(!Directory.Exists(run.Directory), "the run folder goes with its last workspace");
            Check(await lab.Manager.GetAsync("cmp_disc") is null, "the run has no workspaces left");
            Equal(mainOnly, await lab.GitAsync(repo, "worktree", "list", "--porcelain"), "git worktree list is clean");
            Equal("", await lab.GitAsync(repo, "worktree", "prune", "--dry-run", "--verbose"), "no stale registration is left");

            var whole = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_whole", ProjectPath = repo, Slots = WsSlots("a", "b") });
            Check(await lab.Manager.DiscardRunAsync("cmp_whole"), "a whole run is discarded");
            Check(!Directory.Exists(whole.Directory), "its folder is gone");
            Equal(mainOnly, await lab.GitAsync(repo, "worktree", "list", "--porcelain"), "git worktree list is clean again");
            Check(!await lab.Manager.DiscardRunAsync("cmp_whole"), "discarding a missing run does nothing");

            var plain = lab.Folder("plain");
            WsWrite(plain, ("f.txt", "x\n"));
            var copy = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_cp", ProjectPath = plain, Slots = WsSlots("a") });
            Check(await lab.Manager.DiscardAsync("cmp_cp", "a") && !Directory.Exists(copy.Directory), "a copy run's folder is deleted, base copy included");
            Equal(0, (await lab.Manager.ListAsync()).Count, "nothing is left to list");
        }),

        Case("workspaces.cleanup-removes-leftovers-and-expired-runs", async () =>
        {
            await using var lab = WsLab.Create("cleanup");
            var repo = await lab.RepoAsync("repo", ("a.txt", "one\n"));
            var start = lab.Clock.Now;
            await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_old", ProjectPath = repo, Slots = WsSlots("a") });
            await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_active", ProjectPath = repo, Slots = WsSlots("a") });
            await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_crash", ProjectPath = repo, Slots = WsSlots("a", "b") });
            await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_nometa", ProjectPath = repo, Slots = WsSlots("a") });
            lab.Clock.Now = start + TimeSpan.FromDays(6);
            await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_new", ProjectPath = repo, Slots = WsSlots("a") });
            await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_part", ProjectPath = repo, Slots = WsSlots("a", "b") });
            await lab.Manager.DiscardAsync("cmp_part", "a");

            // A crash mid-create leaves a manifest still "creating"; a folder can lose its manifest; a discarded workspace can come back
            // (a process still writing into it); and anything else in the root is a leftover too.
            var crashFolder = Path.Join(lab.Root, "cmp_crash");
            var manifest = WorkspaceManifest.Read(crashFolder)!;
            manifest.State = WorkspaceManifest.Creating;
            manifest.Write(crashFolder);
            WorkspaceTree.Delete(Path.Join(lab.Root, "cmp_nometa", WorkspaceManifest.MetaFolder));
            WsWrite(Path.Join(lab.Root, "cmp_part", "a"), ("late.txt", "written after the discard"));
            WsWrite(Path.Join(lab.Root, "junk folder"), ("x.txt", "not a run"));
            Equal(8, WsWorktrees(await lab.GitAsync(repo, "worktree", "list", "--porcelain")), "the project and seven worktrees before the cleanup");

            lab.Clock.Now = start + TimeSpan.FromDays(7) + TimeSpan.FromHours(1);
            var report = await lab.Manager.CleanupAsync(run => run == "cmp_active");
            var removed = report.Removed.Select(removal => $"{removal.Run}{(removal.Slot is null ? "" : "/" + removal.Slot)}:{removal.Reason}").Order(StringComparer.Ordinal);
            Equal("cmp_crash:leftover,cmp_nometa:leftover,cmp_old:expired,cmp_part/a:discarded,junk folder:leftover", string.Join(",", removed), "what the cleanup removed");
            Equal(0, report.Failed.Count, "nothing failed");
            Equal(3, report.Kept, "cmp_active (in use), cmp_new and cmp_part are kept");
            Equal("cmp_active,cmp_new,cmp_part", string.Join(",", Directory.EnumerateDirectories(lab.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal)), "what is left in the root");
            var list = await lab.GitAsync(repo, "worktree", "list", "--porcelain");
            Equal(4, WsWorktrees(list), "the removed runs' worktrees are unregistered");
            Check(!list.Contains("cmp_crash", StringComparison.Ordinal) && !list.Contains("cmp_nometa", StringComparison.Ordinal) && !list.Contains("cmp_old", StringComparison.Ordinal),
                "no registration of a removed run is left");
            Equal("", await lab.GitAsync(repo, "worktree", "prune", "--dry-run", "--verbose"), "no stale registration is left");

            // The janitor runs the same cleanup when the app starts.
            lab.Clock.Now = start + TimeSpan.FromDays(14);
            var janitor = new WorkspaceJanitor(lab.Manager, [], lab.Clock);
            await janitor.StartAsync(CancellationToken.None);
            await janitor.FirstRun.WaitAsync(TimeSpan.FromSeconds(60));
            await janitor.StopAsync(CancellationToken.None);
            Equal(3, janitor.LastReport!.Removed.Count, "the janitor removed the runs that expired since");
            Equal(0, Directory.EnumerateDirectories(lab.Root).Count(), "nothing is left");
            Equal(1, WsWorktrees(await lab.GitAsync(repo, "worktree", "list", "--porcelain")), "only the project's own worktree is registered");

            // Without the marker file the root isn't the app's, so nothing in it is removed.
            await using var foreign = WsLab.Create("foreign");
            WsWrite(Path.Join(foreign.Root, "junk"), ("x.txt", "someone else's"));
            Equal(0, (await foreign.Manager.CleanupAsync()).Removed.Count, "an unmarked root is left alone");
            Check(File.Exists(Path.Join(foreign.Root, "junk", "x.txt")), "its folder is still there");
        }),

        Case("workspaces.paths-stay-inside-the-workspace-root", async () =>
        {
            await using var lab = WsLab.Create("paths");
            foreach (var run in new[] { "", "..", ".", "a/b", "a\\b", "C:\\x", "-x", "_x", "con", "NUL", "com1", new string('a', 65), "a b", "a.b" })
                Equal(WorkspaceException.Invalid, Throws<WorkspaceException>(() => lab.Manager.RunDirectory(run), "run id " + run).Code, "an invalid run id: " + run);
            foreach (var slot in new[] { "", "base", "A", "../a", "a/b", "nul", "lpt1", "a.b", new string('a', 33) })
                Equal(WorkspaceException.Invalid, Throws<WorkspaceException>(() => lab.Manager.SlotDirectory("cmp_ok", slot), "slot " + slot).Code, "an invalid slot: " + slot);
            Check(WorkspaceManager.IsValidRun("cmp_01JBQ7") && WorkspaceManager.IsValidSlot("b"), "the usual names are valid");

            var plain = lab.Folder("plain");
            WsWrite(plain, ("f.txt", "x\n"));
            Equal(WorkspaceException.Invalid, (await ThrowsAsync<WorkspaceException>(() => lab.Manager.CreateAsync(new WorkspaceRequest
            { Run = "cmp_dup", ProjectPath = plain, Slots = WsSlots("a", "a") }), "duplicate slots")).Code, "each workspace needs its own slot");
            Equal(WorkspaceException.Invalid, (await ThrowsAsync<WorkspaceException>(() => lab.Manager.CreateAsync(new WorkspaceRequest
            { Run = "cmp_rel", ProjectPath = "relative\\folder", Slots = WsSlots("a") }), "a relative project")).Code, "the project must be an absolute path");
            Equal(WorkspaceException.ProjectMissing, (await ThrowsAsync<WorkspaceException>(() => lab.Manager.CreateAsync(new WorkspaceRequest
            { Run = "cmp_none", ProjectPath = Path.Join(plain, "missing"), Slots = WsSlots("a") }), "a missing project")).Code, "the project must exist");
            Directory.CreateDirectory(lab.Root);
            Equal(WorkspaceException.Invalid, (await ThrowsAsync<WorkspaceException>(() => lab.Manager.InspectAsync(lab.Root), "the root as the project")).Code,
                "the workspaces folder can't be a project");
            var repo = await lab.RepoAsync("repo", ("a.txt", "one\n"));
            Equal(WorkspaceException.Invalid, (await ThrowsAsync<WorkspaceException>(() => lab.Manager.CreateAsync(new WorkspaceRequest
            { Run = "cmp_opt", ProjectPath = repo, Slots = WsSlots("a"), BaseRef = "--output=x" }), "an option as the base")).Code, "an option is never passed as a base");
            Equal(WorkspaceException.Invalid, (await ThrowsAsync<WorkspaceException>(() => lab.Manager.CreateAsync(new WorkspaceRequest
            { Run = "cmp_ex", ProjectPath = plain, Slots = WsSlots("a"), ExtraExcludes = ["bad\npattern"] }), "a control character in an exclude")).Code, "excludes are single lines");

            // A link an agent makes to another folder is never followed: not by a diff, a measure or a discard.
            var outside = lab.Folder("outside");
            WsWrite(outside, ("precious.txt", "must survive\n"));
            var copy = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_link", ProjectPath = plain, Slots = WsSlots("a") });
            var linked = WsTryLink(Path.Join(copy.Slots[0].Path, "escape"), outside);
            Check(linked, "a folder link can be made for the check");
            Check(!(await lab.Manager.DiffAsync("cmp_link", "a")).Files.Any(file => file.Path.StartsWith("escape", StringComparison.Ordinal)), "a diff does not follow the link");
            Check(await lab.Manager.DiscardAsync("cmp_link", "a"), "the workspace is discarded");
            Check(File.Exists(Path.Join(outside, "precious.txt")), "the link's target and its files survive the discard");

            // Apply never writes through a link in the project.
            var project = lab.Folder("linked project");
            WsWrite(project, ("f.txt", "x\n"));
            var target = lab.Folder("link target");
            Check(WsTryLink(Path.Join(project, "through"), target), "a folder link in the project");
            var through = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_through", ProjectPath = project, Slots = WsSlots("a") });
            Equal("through", string.Join(",", through.SkippedLinks), "the copy lists the link it left out");
            Equal("f.txt", string.Join(",", WsFiles(through.Slots[0].Path)), "the copy leaves the link out");
            WsWrite(through.Slots[0].Path, ("through/evil.txt", "should not land outside\n"));
            Equal(WorkspaceException.Invalid, (await ThrowsAsync<WorkspaceException>(() => lab.Manager.ApplyAsync("cmp_through", "a"), "apply through a link")).Code,
                "Apply refuses a path through a link");
            Check(!File.Exists(Path.Join(target, "evil.txt")), "nothing was written through the link");
        }),

        Case("workspaces.git-runs-with-argument-lists", async () =>
        {
            await using var lab = WsLab.Create("args");
            // Characters a shell would read: spaces, quotes, &, $, %, ^, parentheses and !.
            var repo = await lab.RepoAsync("proj & 'quoted' $HOME %PATH% ^(x) !bang", ("a file.txt", "one\n"));
            var run = await lab.Manager.CreateAsync(new WorkspaceRequest { Run = "cmp_args", ProjectPath = repo, Slots = WsSlots("a") });
            Equal(WorkspaceKind.Worktree, run.Kind, "a worktree of a project whose path a shell would mangle");
            WsWrite(run.Slots[0].Path, ("new file \u00fc & $x.txt", "new\n"));
            var diff = await lab.Manager.DiffAsync("cmp_args", "a");
            Equal("new file \u00fc & $x.txt", diff.Files.Single().Path, "a file name with spaces, non-ASCII letters and shell characters");
            Equal(KeepOutcome.Applied, (await lab.Manager.ApplyAsync("cmp_args", "a")).Outcome, "applied");
            Check(File.Exists(Path.Join(repo, "new file \u00fc & $x.txt")), "the file is in the project");
            Check(await lab.Manager.DiscardAsync("cmp_args", "a"), "discarded");
            Equal(1, WsWorktrees(await lab.GitAsync(repo, "worktree", "list", "--porcelain")), "and unregistered");

            foreach (var file in Directory.EnumerateFiles(WebProjectFile("AgentCompare", "Workspaces"), "*.cs"))
            {
                var source = File.ReadAllText(file);
                Check(!source.Contains("UseShellExecute = true", StringComparison.Ordinal) && !source.Contains("cmd.exe", StringComparison.Ordinal) &&
                    !source.Contains("/bin/sh", StringComparison.Ordinal) && !source.Contains(".Arguments =", StringComparison.Ordinal), Path.GetFileName(file) + " runs no shell");
            }
            Check(File.ReadAllText(WebProjectFile("AgentCompare", "Workspaces", "GitCommand.cs")).Contains("info.ArgumentList.Add(argument)", StringComparison.Ordinal),
                "git gets an argument list");
        }),

        Case("workspaces.module-registers-the-manager-under-the-data-folder", async () =>
        {
            var data = Temp("ws-module");
            try
            {
                var services = new ServiceCollection();
                services.AddLogging();
                var options = new WebAgentOptions { DataDirectory = data };
                services.AddAgentCompareWorkspaces(options);
                services.AddAgentCompareWorkspaces(options);
                await using var provider = services.BuildServiceProvider();
                var manager = provider.GetRequiredService<WorkspaceManager>();
                Equal(Path.Join(data, "workspaces"), manager.Root, "workspaces live under the data folder, so --data moves them");
                Equal(manager.Root, WorkspacesModule.RootFor(options), "the module names the root");
                var janitors = provider.GetServices<IHostedService>().OfType<WorkspaceJanitor>().ToList();
                Equal(1, janitors.Count, "one janitor, however often the module is added");
                await janitors[0].StartAsync(CancellationToken.None);
                await janitors[0].FirstRun.WaitAsync(TimeSpan.FromSeconds(30));
                await janitors[0].StopAsync(CancellationToken.None);
                Check(!Directory.Exists(manager.Root), "the cleanup doesn't create the workspaces folder");
                Check(File.ReadAllText(WebProjectFile("Program.cs")).Contains("WorkspacesModule.AddAgentCompareWorkspaces(builder.Services, options);", StringComparison.Ordinal),
                    "the app registers the workspaces");
            }
            finally
            {
                DeleteTemp(data);
            }
        }),

        Case("workspaces.gitignore-rules-diff-parser-and-status-follow-git", () =>
        {
            static bool Ignored(string patterns, string path, bool folder = false) => IgnoreList.Parse(patterns.Split('\n')).Match(path, folder) == IgnoreMatch.Ignore;
            Check(Ignored("*.log", "a/b/x.log"), "a name pattern matches at any depth");
            Check(!Ignored("*.log\n!keep.log", "keep.log"), "a later negation wins");
            Check(Ignored("/root.txt", "root.txt") && !Ignored("/root.txt", "sub/root.txt"), "a leading slash anchors");
            Check(Ignored("doc/", "doc", folder: true) && !Ignored("doc/", "doc"), "a trailing slash matches folders only");
            Check(Ignored("a/**/b", "a/b") && Ignored("a/**/b", "a/x/y/b") && !Ignored("a/**/b", "c/a/b"), "/**/ spans folders");
            Check(Ignored("**/foo", "foo") && Ignored("**/foo", "x/foo"), "a leading **/ matches in every folder");
            Check(Ignored("abc/**", "abc/x/y") && !Ignored("abc/**", "abc", folder: true), "a trailing /** matches what is inside");
            Check(Ignored("[ab].txt", "a.txt") && !Ignored("[ab].txt", "c.txt") && Ignored("[!ab].txt", "c.txt"), "classes");
            Check(Ignored("\\#hash", "#hash") && !Ignored("#hash", "#hash"), "an escaped # is a pattern, a bare one a comment");
            Check(Ignored("a?c", "abc") && !Ignored("a?c", "a/c"), "? never matches a slash");
            Check(Ignored("name   ", "name"), "trailing spaces are dropped");
            Check(IgnoreList.Parse(["bin/"]).IsExcluded("src/bin/app.dll") && !IgnoreList.Parse(["bin/"]).IsExcluded("src/bin"), "a path below an excluded folder");
            var nested = IgnoreList.Parse(["x.txt"], "sub/");
            Check(nested.Match("sub/x.txt", false) == IgnoreMatch.Ignore && nested.Match("x.txt", false) == IgnoreMatch.None, "a nested file applies below its folder only");
            var outer = IgnoreList.Parse(["/proj/gen/"], "", "proj/");
            Check(outer.Match("gen", true) == IgnoreMatch.Ignore, "a parent's pattern is matched with the project's path in front");

            var patch = Encoding.UTF8.GetBytes(
                "diff --git a/x y.txt b/x y.txt\nindex 1..2 100644\n--- a/x y.txt\n+++ b/x y.txt\n@@ -1,2 +1,2 @@ heading\n keep\n-old\n+new\n\\ No newline at end of file\n" +
                "diff --git \"a/q\\\"t\\303\\274.txt\" \"b/q\\\"t\\303\\274.txt\"\nnew file mode 100644\n--- /dev/null\n+++ \"b/q\\\"t\\303\\274.txt\"\n@@ -0,0 +1 @@\n+diff --git looks like a header\n" +
                "diff --git a/img.png b/img.png\ndeleted file mode 100644\nindex 3..0\nGIT binary patch\nliteral 0\nHcmV?d00001\n\nliteral 4\nLcmZ?wbhEHbKAP\n\n" +
                "diff --git a/run.sh b/run.sh\nold mode 100644\nnew mode 100755\n");
            var files = UnifiedDiff.Parse(patch);
            Equal("x y.txt|q\"t\u00fc.txt|img.png|run.sh", string.Join("|", files.Select(file => file.Path)), "paths, quoted ones unquoted");
            Equal("@@ -1,2 +1,2 @@ heading", files[0].Hunks.Single().Header, "the hunk header");
            Equal("Context:keep,Removed:old,Added:new,Note:No newline at end of file",
                string.Join(",", files[0].Hunks.Single().Lines.Select(line => line.Kind + ":" + line.Text)), "the hunk's lines");
            Check(files[1].Change == WorkspaceFileChange.Added && files[1].Added == 1 && files[1].Hunks.Single().Lines.Single().Text == "diff --git looks like a header",
                "a content line is never a header");
            Check(files[2].Change == WorkspaceFileChange.Deleted && files[2].Binary, "a deleted binary file");
            Check(files[3].OldMode == "100644" && files[3].NewMode == "100755" && files[3].Hunks.Count == 0, "a mode change");
            Equal("\"a/q\\\"t\\\\.txt\"", UnifiedDiff.Quote("a/q\"t\\.txt"), "quoting");
            Equal("a/plain \u00fc.txt", UnifiedDiff.Quote("a/plain \u00fc.txt"), "nothing to quote");

            var status = GitStatus.Parse(Encoding.UTF8.GetBytes(" M a.txt\0R  new.txt\0old.txt\0?? u v.txt\0A  s.txt\0 D d.txt\0UU c.txt\0"));
            Equal("a.txt:modified,new.txt:renamed<old.txt,u v.txt:untracked,s.txt:added,d.txt:deleted,c.txt:unmerged",
                string.Join(",", status.Select(change => change.Path + ":" + change.Status + (change.OriginalPath is { } from ? "<" + from : ""))), "git status entries");
            Equal("84 MB", WorkspaceText.Size(84L * 1024 * 1024), "sizes");
            Equal("1.2 GB", WorkspaceText.Size(1288490189L), "sizes in GB");
            Equal("3 files · +31 \u22129", WorkspaceText.Summary(3, 31, 9), "the diff summary");
        }),
    ];

    private static IReadOnlyList<WorkspaceSlotRequest> WsSlots(params string[] slots) => [.. slots.Select(slot => new WorkspaceSlotRequest(slot))];

    private static void WsWrite(string folder, params (string Path, string Text)[] files)
    {
        foreach (var (path, text) in files)
        {
            var full = Path.Join(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
    }

    // Every file below a folder (hidden ones too), relative with '/', a worktree's .git file left out.
    private static List<string> WsFiles(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.None })
            .Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/')).Where(file => file != ".git").Order(StringComparer.Ordinal)];

    private static int WsWorktrees(string porcelain) => porcelain.Split('\n').Count(line => line.StartsWith("worktree ", StringComparison.Ordinal));

    // A folder link: a symbolic link where the system allows one, else (Windows without the privilege) a junction.
    private static bool WsTryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        if (!OperatingSystem.IsWindows()) return false;
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.WaitForExit(15000);
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    private sealed class WsClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // A temporary folder with a workspace manager rooted in it, and git that ignores the user's own configuration.
    private sealed class WsLab : IAsyncDisposable
    {
        private readonly string folder;
        private readonly Dictionary<string, string?> environment;

        private WsLab(string folder)
        {
            this.folder = folder;
            var config = Path.Join(folder, "gitconfig");
            File.WriteAllText(config, "");
            environment = new Dictionary<string, string?> { ["GIT_CONFIG_GLOBAL"] = config, ["GIT_CONFIG_NOSYSTEM"] = "1" };
            Clock = new WsClock(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
            Root = Path.Join(folder, "data", "workspaces");
            Manager = new WorkspaceManager(new WorkspaceManagerOptions { Root = Root, GitEnvironment = environment }, Clock);
        }

        public string Root { get; }

        public WorkspaceManager Manager { get; }

        public WsClock Clock { get; }

        public static WsLab Create(string name) => new(Temp("ws-" + name));

        public string Folder(string name)
        {
            var path = Path.Join(folder, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public async Task<string> RepoAsync(string name, params (string Path, string Text)[] files)
        {
            var repo = Folder(name);
            await GitAsync(repo, "init", "-q", "-b", "main");
            WsWrite(repo, files);
            await CommitAllAsync(repo, "first");
            return repo;
        }

        public async Task CommitAllAsync(string repo, string message)
        {
            await GitAsync(repo, "add", "-A");
            await GitAsync(repo, "commit", "-q", "-m", message);
        }

        public async Task<string> GitAsync(string cwd, params string[] arguments)
        {
            var (ok, output, error) = await TryGitAsync(cwd, arguments);
            if (!ok) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
            return output;
        }

        public async Task<(bool Ok, string Output, string Error)> TryGitAsync(string cwd, params string[] arguments)
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var argument in new[] { "-c", "user.name=PiSharp Tests", "-c", "user.email=tests@pisharp.invalid", "-c", "core.quotepath=false" })
                info.ArgumentList.Add(argument);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            foreach (var (name, value) in environment) info.Environment[name] = value;
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode == 0, (await output).Trim(), await error);
        }

        public ValueTask DisposeAsync()
        {
            try { WorkspaceTree.Delete(folder); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* a temporary folder */ }
            return ValueTask.CompletedTask;
        }
    }
}
