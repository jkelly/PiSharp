// Decision 0011 (run W4, unit R4): keeping one model's result.
//  - Apply to project: the slot's diff becomes uncommitted changes in the project. It is refused, with nothing changed, when the project
//    has uncommitted changes in any of the same files (for a worktree, `git status`; for a copy, the project file no longer matching the
//    base copy), and when the patch no longer applies (`git apply --check`). A worktree's patch goes through `git apply` (working tree only,
//    no --index, so nothing is staged or committed); a copy's files are copied over, each target checked to stay inside the project and not
//    to pass through a link.
//  - Keep the branch (worktrees only): one commit with exactly the reviewed changes (the slot's private index, written as a tree), whose
//    parent is the base commit, on the new branch pisharp/compare/<run>-<slot>, created with `git update-ref <ref> <commit> ""` so an
//    existing branch is never moved. The user's current branch, HEAD, index and working tree are not touched, and nothing is pushed.
// Both keep the workspace (it is removed on Discard or after 7 days).
using System.Text;

namespace PiSharp.WebAgent.AgentCompare.Workspaces;

public sealed partial class WorkspaceManager
{
    // Used for Keep the branch only when git has no identity configured (user.name / user.email).
    private static readonly Dictionary<string, string?> FallbackIdentity = new()
    {
        ["GIT_AUTHOR_NAME"] = "PiSharp Web Agent",
        ["GIT_AUTHOR_EMAIL"] = "webagent@pisharp.invalid",
        ["GIT_COMMITTER_NAME"] = "PiSharp Web Agent",
        ["GIT_COMMITTER_EMAIL"] = "webagent@pisharp.invalid",
    };

    /// <summary>What Keep would do for <paramref name="slot"/> (the Keep modal): its files, the project's uncommitted changes in the same
    /// files and its other uncommitted changes. Changes nothing.</summary>
    public async Task<KeepPreview> PreviewKeepAsync(string run, string slot, CancellationToken cancellationToken = default)
    {
        var folder = RunDirectory(run);
        ValidateSlot(slot);
        var gate = Gate(run);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (manifest, entry) = Load(folder, slot);
            var files = UnifiedDiff.Parse(await PatchAsync(manifest, folder, entry, cancellationToken).ConfigureAwait(false));
            var (conflicts, others) = await ProjectChangesAsync(manifest, folder, files, cancellationToken).ConfigureAwait(false);
            return new KeepPreview
            {
                Run = manifest.Run,
                Slot = entry.Slot,
                Kind = manifest.Kind,
                Files = [.. files.Select(file => file.Path)],
                Conflicts = conflicts,
                OtherUncommitted = others,
                Branch = manifest.Kind == WorkspaceKind.Worktree ? BranchName(manifest.Run, entry.Slot) : null,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Applies <paramref name="slot"/>'s changes to the project as uncommitted changes, or refuses with nothing changed.</summary>
    /// <returns><see cref="KeepOutcome.Applied"/>, or <see cref="KeepOutcome.Refused"/> with <see cref="KeepResult.Refusals.Conflict"/>
    /// (and the conflicting files), <see cref="KeepResult.Refusals.DoesNotApply"/> or <see cref="KeepResult.Refusals.NothingToKeep"/>.</returns>
    public async Task<KeepResult> ApplyAsync(string run, string slot, CancellationToken cancellationToken = default)
    {
        var folder = RunDirectory(run);
        ValidateSlot(slot);
        var gate = Gate(run);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (manifest, entry) = Load(folder, slot);
            var patch = await PatchAsync(manifest, folder, entry, cancellationToken).ConfigureAwait(false);
            var files = UnifiedDiff.Parse(patch);
            var paths = files.Select(file => file.Path).ToList();
            if (files.Count == 0) return Refused(manifest, entry, KeepResult.Refusals.NothingToKeep, "This workspace has no changes to apply.", paths);
            await projects.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var (conflicts, _) = await ProjectChangesAsync(manifest, folder, files, cancellationToken).ConfigureAwait(false);
                if (conflicts.Count > 0)
                    return Refused(manifest, entry, KeepResult.Refusals.Conflict,
                        $"Your project has uncommitted changes in {(conflicts.Count == 1 ? "a file" : conflicts.Count + " files")} this workspace also changed, so nothing was applied.",
                        paths) with { Conflicts = conflicts };
                if (manifest.Kind == WorkspaceKind.Worktree)
                {
                    var repo = manifest.RepoRoot!;
                    var check = await git.RunAsync(repo, ["apply", "--check", "--whitespace=nowarn", "-"], input: patch, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (!check.Ok)
                        return Refused(manifest, entry, KeepResult.Refusals.DoesNotApply,
                            "These changes no longer apply to your project, so nothing was applied" + Reason(check) + " Keep the branch instead.", paths);
                    var applied = await git.RunAsync(repo, ["apply", "--whitespace=nowarn", "-"], input: patch, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (!applied.Ok)
                        return Refused(manifest, entry, KeepResult.Refusals.DoesNotApply, "git couldn't apply these changes" + Reason(applied), paths);
                }
                else
                {
                    await Task.Run(() => CopyIntoProject(manifest, entry, files), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                projects.Release();
            }
            entry.AppliedAt = time.GetUtcNow();
            manifest.Write(folder);
            return new KeepResult
            {
                Run = manifest.Run,
                Slot = entry.Slot,
                Outcome = KeepOutcome.Applied,
                Message = $"Applied {WorkspaceText.Summary(files.Count, files.Sum(file => file.Added), files.Sum(file => file.Removed))} to your project as uncommitted changes.",
                Files = paths,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Commits <paramref name="slot"/>'s changes as one commit on the new branch <see cref="BranchName"/>, without switching to
    /// it (worktrees only). Repeating it for the same changes answers the existing branch.</summary>
    /// <returns><see cref="KeepOutcome.BranchCreated"/>, or <see cref="KeepOutcome.Refused"/> with
    /// <see cref="KeepResult.Refusals.NotGit"/>, <see cref="KeepResult.Refusals.NothingToKeep"/> or
    /// <see cref="KeepResult.Refusals.BranchExists"/>.</returns>
    public async Task<KeepResult> KeepBranchAsync(string run, string slot, CancellationToken cancellationToken = default)
    {
        var folder = RunDirectory(run);
        ValidateSlot(slot);
        var gate = Gate(run);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (manifest, entry) = Load(folder, slot);
            if (manifest.Kind != WorkspaceKind.Worktree)
                return Refused(manifest, entry, KeepResult.Refusals.NotGit, "Keep the branch needs git worktrees; apply the changes to your project instead.", []);
            var path = SlotDirectory(manifest.Run, entry.Slot);
            var repo = manifest.RepoRoot!;
            var patch = await PatchAsync(manifest, folder, entry, cancellationToken).ConfigureAwait(false);
            var files = UnifiedDiff.Parse(patch);
            var paths = files.Select(file => file.Path).ToList();
            var branch = BranchName(manifest.Run, entry.Slot);
            if (files.Count == 0) return Refused(manifest, entry, KeepResult.Refusals.NothingToKeep, "This workspace has no changes to keep.", paths) with { Branch = branch };
            await git.RequireAsync(repo, ["check-ref-format", "--branch", branch], cancellationToken: cancellationToken).ConfigureAwait(false);

            // The private index holds exactly what the diff showed: write it as a tree (in the slot's own repository, never one its .git
            // file names).
            var index = await SlotGitAsync(manifest, folder, entry, cancellationToken).ConfigureAwait(false);
            var tree = (await git.RequireAsync(path, ["write-tree"], environment: index, cancellationToken: cancellationToken).ConfigureAwait(false)).Line;
            var reference = "refs/heads/" + branch;
            if (await ExistingKeepAsync(repo, reference, tree, manifest.BaseCommit!, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                if (existing.Same) return Kept(manifest, folder, entry, branch, existing.Commit, paths, alreadyExisted: true);
                return Refused(manifest, entry, KeepResult.Refusals.BranchExists, $"The branch {branch} already exists with other changes, so it was left as it is.", paths)
                    with { Branch = branch, Commit = existing.Commit };
            }

            var identity = (await git.RunAsync(repo, ["var", "GIT_COMMITTER_IDENT"], cancellationToken: cancellationToken).ConfigureAwait(false)).Ok
                && (await git.RunAsync(repo, ["var", "GIT_AUTHOR_IDENT"], cancellationToken: cancellationToken).ConfigureAwait(false)).Ok ? null : FallbackIdentity;
            var message = new StringBuilder($"Agent compare {manifest.Run}: workspace {entry.Slot}");
            if (entry.Label is { Length: > 0 } label) message.Append(" (").Append(label).Append(')');
            message.Append("\n\nKept from PiSharp Web Agent's agent compare. Base: ").Append(manifest.BaseCommit).Append('\n');
            var commit = (await git.RequireAsync(repo, ["commit-tree", tree, "-p", manifest.BaseCommit!, "-F", "-"], input: Encoding.UTF8.GetBytes(message.ToString()),
                environment: identity, cancellationToken: cancellationToken).ConfigureAwait(false)).Line;

            await projects.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var created = await git.RunAsync(repo, ["update-ref", "-m", "pisharp agent compare: keep " + manifest.Run + "-" + entry.Slot, reference, commit, ""],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!created.Ok)
                {
                    if (await ExistingKeepAsync(repo, reference, tree, manifest.BaseCommit!, cancellationToken).ConfigureAwait(false) is { } raced)
                        return raced.Same ? Kept(manifest, folder, entry, branch, raced.Commit, paths, alreadyExisted: true)
                            : Refused(manifest, entry, KeepResult.Refusals.BranchExists, $"The branch {branch} already exists with other changes, so it was left as it is.", paths)
                                with { Branch = branch, Commit = raced.Commit };
                    throw GitCommand.Failed(["update-ref"], created);
                }
            }
            finally
            {
                projects.Release();
            }
            return Kept(manifest, folder, entry, branch, commit, paths, alreadyExisted: false);
        }
        finally
        {
            gate.Release();
        }
    }

    // An existing branch: its commit, and whether it already holds exactly these changes on the base.
    private async Task<(string Commit, bool Same)?> ExistingKeepAsync(string repo, string reference, string tree, string baseCommit, CancellationToken cancellationToken)
    {
        var existing = await git.RunAsync(repo, ["rev-parse", "--verify", "--quiet", reference], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!existing.Ok) return null;
        var commit = existing.Line;
        var existingTree = await git.RunAsync(repo, ["rev-parse", "--verify", "--quiet", commit + "^{tree}"], cancellationToken: cancellationToken).ConfigureAwait(false);
        var parent = await git.RunAsync(repo, ["rev-parse", "--verify", "--quiet", commit + "^1"], cancellationToken: cancellationToken).ConfigureAwait(false);
        return (commit, existingTree.Ok && existingTree.Line == tree && parent.Ok && parent.Line == baseCommit);
    }

    private KeepResult Kept(WorkspaceManifest manifest, string folder, ManifestSlot entry, string branch, string commit, List<string> paths, bool alreadyExisted)
    {
        entry.Branch = branch;
        entry.BranchCommit = commit;
        manifest.Write(folder);
        return new KeepResult
        {
            Run = manifest.Run,
            Slot = entry.Slot,
            Outcome = KeepOutcome.BranchCreated,
            Message = $"Kept the changes as one commit on {branch}. Your current branch wasn't switched, and nothing was pushed.",
            Files = paths,
            Branch = branch,
            Commit = commit,
            AlreadyExisted = alreadyExisted,
        };
    }

    private static KeepResult Refused(WorkspaceManifest manifest, ManifestSlot entry, string refusal, string message, List<string> paths) => new()
    {
        Run = manifest.Run,
        Slot = entry.Slot,
        Outcome = KeepOutcome.Refused,
        Refusal = refusal,
        Message = message,
        Files = paths,
    };

    private static string Reason(GitResult result) => GitCommand.FirstLine(result.Error) is { Length: > 0 } line ? " (" + line + ")." : ".";

    // The project's uncommitted changes in the slot's files, and its others. For a worktree they come from `git status`; for a copy, a file
    // of the slot's that no longer matches the base copy in the project counts (the copy's own "uncommitted" changes), and the others are
    // not listed.
    private async Task<(IReadOnlyList<UncommittedChange> Conflicts, IReadOnlyList<UncommittedChange> Others)> ProjectChangesAsync(WorkspaceManifest manifest,
        string folder, IReadOnlyList<WorkspaceFileDiff> files, CancellationToken cancellationToken)
    {
        var touched = new HashSet<string>(files.Select(file => file.Path), WorkspaceTree.PathComparer);
        if (manifest.Kind == WorkspaceKind.Worktree)
        {
            if (!Directory.Exists(manifest.RepoRoot)) throw new WorkspaceException(WorkspaceException.ProjectMissing, "The project's repository isn't there any more.");
            var status = await StatusAsync(manifest.RepoRoot!, cancellationToken).ConfigureAwait(false);
            var conflicts = status.Where(change => GitStatus.Paths(change).Any(touched.Contains)).ToList();
            return (conflicts, [.. status.Except(conflicts)]);
        }
        if (!Directory.Exists(manifest.ProjectPath)) throw new WorkspaceException(WorkspaceException.ProjectMissing, "The project folder isn't there any more.");
        return await Task.Run(() =>
        {
            var basePath = Path.Join(folder, BaseFolder);
            var conflicts = new List<UncommittedChange>();
            foreach (var file in files)
            {
                var original = WorkspaceTree.Join(basePath, file.Path);
                var current = WorkspaceTree.Join(manifest.ProjectPath, file.Path);
                if (WorkspaceTree.SameContent(original, current)) continue;
                var status = !File.Exists(current) ? "deleted since the copy" : !File.Exists(original) ? "added since the copy" : "changed since the copy";
                conflicts.Add(new UncommittedChange(file.Path, status));
            }
            return ((IReadOnlyList<UncommittedChange>)conflicts, (IReadOnlyList<UncommittedChange>)[]);
        }, cancellationToken).ConfigureAwait(false);
    }

    // A copy's Apply: the slot's version of each changed file replaces the project's (or the project's is deleted). Every target is checked
    // first, so a refused path changes nothing.
    private void CopyIntoProject(WorkspaceManifest manifest, ManifestSlot entry, IReadOnlyList<WorkspaceFileDiff> files)
    {
        var project = manifest.ProjectPath;
        var slotPath = SlotDirectory(manifest.Run, entry.Slot);
        var plan = files.Select(file =>
        {
            var target = WorkspaceTree.Inside(project, WorkspaceTree.Join(project, file.Path));
            WorkspaceTree.NoLinksBetween(project, target);
            var source = WorkspaceTree.Inside(slotPath, WorkspaceTree.Join(slotPath, file.Path));
            return (file.Change, Source: source, Target: target);
        }).ToList();
        foreach (var (change, source, target) in plan)
        {
            if (change == WorkspaceFileChange.Deleted)
            {
                if (File.Exists(target))
                {
                    var info = new FileInfo(target);
                    if (info.Attributes.HasFlag(FileAttributes.ReadOnly)) info.Attributes &= ~FileAttributes.ReadOnly;
                    info.Delete();
                }
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
    }
}
