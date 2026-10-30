// Decision 0011 (run W4, unit R4): a workspace's changes against its base.
//  - Worktree: `git diff` with untracked files. The worktree's own index is never touched (an agent may run git there): the manager keeps
//    a private index per slot (<run>\.meta\<slot>.index, through GIT_INDEX_FILE) that starts as the base commit's tree; each diff stages
//    the tracked changes (`git add --update`) and the untracked files that .gitignore and the excludes allow, then runs `git diff --cached
//    <base>`. So the diff covers edits, deletions, new files and commits the agent made, binary files come as git binary patches, and the
//    same index is what Keep the branch commits.
//  - Copy: the base copy and the working copy are walked with the copy's rules (the base copy's .gitignore files, so an agent's edit to one
//    does not hide its changes); a file is changed when its size, or its time and bytes, differ. Each changed file gets `git diff
//    --no-index` against the base (or /dev/null), with its header rewritten to name the relative path.
// Every diff is made with --no-renames and --no-ext-diff/--no-textconv, so the user's diff drivers can't change what is shown or applied.
// Run W4 (fix): a worktree slot's git commands name its repository folder and work tree (GIT_DIR, GIT_WORK_TREE; SlotGitAsync), recorded
// when the worktree was made, and refuse to run when the slot's .git file no longer names that folder: an agent that rewrites .git can't
// make git load a repository (and a config with filters or an fsmonitor command) of its own.
using System.Text;

namespace PiSharp.WebAgent.AgentCompare.Workspaces;

public sealed partial class WorkspaceManager
{
    // The options that make a patch git apply can read back, whatever the user's diff settings are.
    private static readonly string[] PatchOptions =
        ["--binary", "--full-index", "--no-renames", "--no-color", "--no-ext-diff", "--no-textconv", "-U3", "--src-prefix=a/", "--dst-prefix=b/"];

    /// <summary>The changes in <paramref name="slot"/>'s workspace against the run's base: new, deleted, modified and binary files, with
    /// hunks for the diff modal and the whole patch to keep with the run.</summary>
    /// <exception cref="WorkspaceException"><see cref="WorkspaceException.NotFound"/> when the workspace is not there, or a git failure.</exception>
    public async Task<WorkspaceDiff> DiffAsync(string run, string slot, CancellationToken cancellationToken = default)
    {
        var folder = RunDirectory(run);
        ValidateSlot(slot);
        var gate = Gate(run);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (manifest, entry) = Load(folder, slot);
            var patch = await PatchAsync(manifest, folder, entry, cancellationToken).ConfigureAwait(false);
            return ToDiff(manifest, entry, patch);
        }
        finally
        {
            gate.Release();
        }
    }

    private WorkspaceDiff ToDiff(WorkspaceManifest manifest, ManifestSlot entry, byte[] patch) => new()
    {
        Run = manifest.Run,
        Slot = entry.Slot,
        Kind = manifest.Kind,
        BaseLabel = manifest.Kind == WorkspaceKind.Worktree ? WorkspaceText.BaseLabel(manifest.BaseBranch, manifest.ShortCommit) : "the base copy",
        Files = UnifiedDiff.Parse(patch),
        Patch = Encoding.UTF8.GetString(patch),
    };

    // The manifest and the slot, which must be ready and on disk.
    private (WorkspaceManifest Manifest, ManifestSlot Slot) Load(string folder, string slot)
    {
        var manifest = WorkspaceManifest.Read(folder);
        if (manifest is not { State: WorkspaceManifest.Ready } || manifest.Find(slot) is not { State: WorkspaceSlotState.Ready } entry)
            throw new WorkspaceException(WorkspaceException.NotFound, "That workspace isn't there any more (discarded, or removed after 7 days).");
        if (!Directory.Exists(SlotDirectory(manifest.Run, entry.Slot)))
            throw new WorkspaceException(WorkspaceException.NotFound, "That workspace's folder is gone.");
        return (manifest, entry);
    }

    private Task<byte[]> PatchAsync(WorkspaceManifest manifest, string folder, ManifestSlot entry, CancellationToken cancellationToken) =>
        manifest.Kind == WorkspaceKind.Worktree ? WorktreePatchAsync(manifest, folder, entry, cancellationToken) : CopyPatchAsync(manifest, folder, entry, cancellationToken);

    // The variables of every git command in a worktree slot: its own repository folder (recorded when the worktree was made) and work
    // tree, so git never finds a repository through <slot>\.git, which the agent could rewrite to plant a config (a filter, an fsmonitor
    // command); and its private index. A .git file that no longer names that folder is refused: git does not run there at all.
    private async Task<Dictionary<string, string?>> SlotGitAsync(WorkspaceManifest manifest, string folder, ManifestSlot entry, CancellationToken cancellationToken)
    {
        var path = SlotDirectory(manifest.Run, entry.Slot);
        var admin = entry.GitDir ?? await FindGitDirAsync(manifest, folder, entry, path, cancellationToken).ConfigureAwait(false);
        if (!NamesGitDir(path, admin))
            throw new WorkspaceException(WorkspaceException.Tampered,
                $"Workspace {entry.Slot.ToUpperInvariant()}'s .git file was changed and no longer names its own repository, so git won't run there. Discard this workspace.");
        return new Dictionary<string, string?> { ["GIT_DIR"] = admin, ["GIT_WORK_TREE"] = path, ["GIT_INDEX_FILE"] = IndexFile(folder, entry.Slot) };
    }

    // Whether <slot>\.git is a plain file (not a link or a folder) whose gitdir line names <paramref name="admin"/>.
    private static bool NamesGitDir(string path, string admin)
    {
        var dotGit = Path.Join(path, ".git");
        try
        {
            var info = new FileInfo(dotGit);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 4096) return false;
            var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim();
            return line is not null && line.StartsWith("gitdir:", StringComparison.Ordinal) &&
                WorkspaceTree.SamePath(Path.GetFullPath(line["gitdir:".Length..].Trim(), path), admin);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // A worktree made before its repository folder was recorded: the project's repository names it (the registration whose gitdir file
    // points back at <slot>\.git, as git keeps it). Recorded from then on.
    private async Task<string> FindGitDirAsync(WorkspaceManifest manifest, string folder, ManifestSlot entry, string path, CancellationToken cancellationToken)
    {
        if (manifest.RepoRoot is { } repo && Directory.Exists(repo))
        {
            var common = await git.RunAsync(repo, ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken: cancellationToken).ConfigureAwait(false);
            var registrations = common.Ok ? Path.Join(Path.GetFullPath(common.Line), "worktrees") : null;
            if (registrations is not null && Directory.Exists(registrations))
                foreach (var candidate in Directory.EnumerateDirectories(registrations))
                {
                    var back = Path.Join(candidate, "gitdir");
                    var target = File.Exists(back) ? File.ReadLines(back).FirstOrDefault()?.Trim() : null;
                    if (target is not { Length: > 0 } || !WorkspaceTree.SamePath(Path.GetFullPath(target, candidate), Path.Join(path, ".git"))) continue;
                    entry.GitDir = Path.GetFullPath(candidate);
                    manifest.Write(folder);
                    return entry.GitDir;
                }
        }
        throw new WorkspaceException(WorkspaceException.Tampered,
            $"The project's repository no longer knows workspace {entry.Slot.ToUpperInvariant()} as a worktree, so git won't run there. Discard this workspace.");
    }

    // Brings the slot's private index up to date with its worktree (tracked changes, plus the untracked files the rules allow).
    private async Task<Dictionary<string, string?>> StageAsync(WorkspaceManifest manifest, string folder, ManifestSlot entry, CancellationToken cancellationToken)
    {
        var path = SlotDirectory(manifest.Run, entry.Slot);
        var index = IndexFile(folder, entry.Slot);
        var environment = await SlotGitAsync(manifest, folder, entry, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(index))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(index)!);
            await git.RequireAsync(path, ["read-tree", manifest.BaseCommit!], environment: environment, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        await git.RequireAsync(path, ["-c", "core.safecrlf=false", "add", "--update"], environment: environment, timeout: options.CheckoutTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var others = await git.RequireAsync(path, ["ls-files", "-z", "--others", "--exclude-standard"], environment: environment,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var excludes = ExcludePatterns(manifest.Excludes);
        // A trailing '/' is a nested repository, which git would add as a link to another repository; leave it out.
        var untracked = others.Text.Split('\0').Where(file => file.Length > 0 && !file.EndsWith('/') && !excludes.IsExcluded(file)).ToList();
        if (untracked.Count > 0)
        {
            var add = new Dictionary<string, string?>(environment) { ["GIT_LITERAL_PATHSPECS"] = "1" };
            await git.RequireAsync(path, ["-c", "core.safecrlf=false", "add", "--pathspec-from-file=-", "--pathspec-file-nul"],
                input: Encoding.UTF8.GetBytes(string.Join('\0', untracked) + "\0"), environment: add, timeout: options.CheckoutTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return environment;
    }

    private async Task<byte[]> WorktreePatchAsync(WorkspaceManifest manifest, string folder, ManifestSlot entry, CancellationToken cancellationToken)
    {
        var environment = await StageAsync(manifest, folder, entry, cancellationToken).ConfigureAwait(false);
        var diff = await git.RequireAsync(SlotDirectory(manifest.Run, entry.Slot), ["diff", "--cached", .. PatchOptions, manifest.BaseCommit!, "--"],
            environment: environment, cancellationToken: cancellationToken).ConfigureAwait(false);
        return diff.Output;
    }

    private async Task<byte[]> CopyPatchAsync(WorkspaceManifest manifest, string folder, ManifestSlot entry, CancellationToken cancellationToken)
    {
        var changes = await CopyChangesAsync(manifest, folder, entry, cancellationToken).ConfigureAwait(false);
        var patches = new byte[changes.Count][];
        await Parallel.ForEachAsync(Enumerable.Range(0, changes.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var (path, change) = changes[index];
                string[] arguments =
                [
                    "-c", "core.autocrlf=false", "-c", "core.safecrlf=false", "diff", "--no-index", .. PatchOptions.Where(option => option != "--no-renames"), "--",
                    change == WorkspaceFileChange.Added ? "/dev/null" : BaseFolder + "/" + path,
                    change == WorkspaceFileChange.Deleted ? "/dev/null" : entry.Slot + "/" + path,
                ];
                var result = await git.RunAsync(folder, arguments, cancellationToken: token).ConfigureAwait(false);
                if (result.ExitCode > 1) throw GitCommand.Failed(arguments, result);
                patches[index] = result.ExitCode == 0 ? [] : UnifiedDiff.Rename(result.Output, path, change);
            }).ConfigureAwait(false);
        var output = new MemoryStream();
        foreach (var patch in patches) output.Write(patch);
        return output.ToArray();
    }

    // The files that differ between the base copy and the slot's copy, in path order.
    private Task<List<(string Path, WorkspaceFileChange Change)>> CopyChangesAsync(WorkspaceManifest manifest, string folder, ManifestSlot entry,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        var basePath = WorkspaceTree.Inside(Root, Path.Join(folder, BaseFolder));
        var slotPath = SlotDirectory(manifest.Run, entry.Slot);
        var rules = CopyRules(manifest.Excludes, manifest.OuterIgnores, rulesRoot: basePath);
        var before = WorkspaceTree.Walk(basePath, rules, null, cancellationToken).Files.ToDictionary(file => file.Path, WorkspaceTree.PathComparer);
        var after = WorkspaceTree.Walk(slotPath, rules, null, cancellationToken).Files.ToDictionary(file => file.Path, WorkspaceTree.PathComparer);
        var changes = new List<(string Path, WorkspaceFileChange Change)>();
        foreach (var path in before.Keys.Concat(after.Keys).Distinct(WorkspaceTree.PathComparer).Order(StringComparer.Ordinal))
        {
            var had = before.TryGetValue(path, out var old);
            var has = after.TryGetValue(path, out var now);
            if (!had) changes.Add((now!.Path, WorkspaceFileChange.Added));
            else if (!has) changes.Add((old!.Path, WorkspaceFileChange.Deleted));
            else if (old!.Length != now!.Length ||
                (old.Modified != now.Modified && !WorkspaceTree.SameContent(WorkspaceTree.Join(basePath, old.Path), WorkspaceTree.Join(slotPath, now.Path))))
                changes.Add((now.Path, WorkspaceFileChange.Modified));
        }
        return changes;
    }, cancellationToken);
}
