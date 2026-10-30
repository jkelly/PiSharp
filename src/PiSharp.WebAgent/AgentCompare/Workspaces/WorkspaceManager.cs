// Decision 0011 (run W4, unit R4): WorkspaceManager, agent compare's workspace per model (plan "Agent compare: a workspace per model").
//  - Git projects: `git worktree add --detach` of the base commit, one per model, under <data>\workspaces\<run>\<slot> (the data folder,
//    so --data moves it). The project's uncommitted changes are listed (ProjectInspection.Uncommitted, WorkspaceRun.NotCopied) and are not
//    copied: every model starts from the same commit.
//  - Other folders (or Isolation = Copy): a pristine base copy (<run>\base) plus one working copy per model, skipping bin, obj,
//    node_modules, .vs, .git, the extra excludes and (unless the run turns UseGitignore off, W4 fix 2) anything .gitignore ignores; a copy
//    above 1 GB needs the user's go-ahead. The run's manifest records the switch, so its diffs and Apply walk the copies the same way.
//  - Diff (WorkspaceManager.Diff.cs), Apply and Keep the branch (WorkspaceManager.Keep.cs), Discard and the cleanup (here): leftovers of an
//    unfinished run are removed at start, and every workspace older than the retention (7 days) is removed at start and once a day
//    (WorkspaceJanitor).
//  - The root is the app's only when it carries MarkerFile, and the marker is written only into a root the manager creates or finds
//    empty. A <data>\workspaces that already holds other files (--data pointed at an existing folder) is refused with RootInUse, so
//    the cleanup, which removes every unknown folder of a marked root, never deletes the user's own folders.
// Workspaces are copies, not sandboxes: agents run tools with the user's rights. Nothing here commits to the user's branches or pushes;
// Keep the branch makes one commit on a new branch without switching to it. Git always runs with an argument list (GitCommand), and every
// path is checked to stay inside its root (the workspaces root, or the project for Apply). Looking at a project runs none of its
// repository's own commands (GitCommand: no fsmonitor, no hooks; StatusAsync: no repo-local filter drivers), and a network or device
// path (\\server\share, \\?\) is refused before anything touches it (W4 fix, agent-ui).
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PiSharp.WebAgent.AgentCompare.Workspaces;

/// <summary>Makes, compares, keeps and removes agent compare workspaces. One per app (a singleton); safe to call from many requests: a
/// run's operations are serialized, and operations that write to a project's repository take one shared lock.</summary>
public sealed partial class WorkspaceManager
{
    /// <summary>The base copy's folder name inside a copy run's folder (never a slot name).</summary>
    public const string BaseFolder = "base";

    /// <summary>The file that marks the workspaces root as this app's; the cleanup removes nothing from a root without it. It is written
    /// only into a root the manager creates or finds empty, never into a folder that already holds other files.</summary>
    public const string MarkerFile = ".pisharp-workspaces";

    /// <summary>The prefix of kept branches: <c>pisharp/compare/&lt;run&gt;-&lt;slot&gt;</c>.</summary>
    public const string BranchPrefix = "pisharp/compare/";

    private readonly WorkspaceManagerOptions options;
    private readonly GitCommand git;
    private readonly TimeProvider time;
    private readonly ILogger log;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim projects = new(1, 1);
    private readonly Lock rootGate = new();

    /// <summary>Creates the manager.</summary>
    /// <param name="options">The root and limits.</param>
    /// <param name="time">The clock (creation and expiry times); default the system clock.</param>
    /// <param name="logger">Where cleanup failures are logged.</param>
    public WorkspaceManager(WorkspaceManagerOptions options, TimeProvider? time = null, ILogger<WorkspaceManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Root) || !Path.IsPathFullyQualified(options.Root))
            throw new ArgumentException("The workspaces root must be an absolute path.", nameof(options));
        this.options = options;
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Root));
        git = new GitCommand(options);
        this.time = time ?? TimeProvider.System;
        log = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>The workspaces root (<c>&lt;data&gt;\workspaces</c>).</summary>
    public string Root { get; }

    /// <summary>Whether the root is this app's: it carries <see cref="MarkerFile"/>. Only then does the cleanup remove anything from it.</summary>
    public bool OwnsRoot => File.Exists(Path.Join(Root, MarkerFile));

    /// <summary>Why no workspace can be made under the root, or null when one can: the root exists without <see cref="MarkerFile"/> and
    /// already holds other files (it was not made by this app), or it is a file.</summary>
    public string? RootProblem()
    {
        lock (rootGate) return UnlockedRootProblem();
    }

    // RootProblem without the root lock (EnsureRoot holds it).
    private string? UnlockedRootProblem()
    {
        try
        {
            if (File.Exists(Root))
                return $"{Root} is a file, so agent compare can't keep its workspaces there. Move it, or start the app with another data folder (--data).";
            if (!Directory.Exists(Root) || OwnsRoot || !Directory.EnumerateFileSystemEntries(Root).Any()) return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* unreadable: not ours either */ }
        return $"{Root} already holds other files, and agent compare keeps its workspaces only in a folder of its own (it removes what it "
            + "doesn't recognise there). Move those files elsewhere, or start the app with another data folder (--data).";
    }

    private void RequireUsableRoot()
    {
        if (RootProblem() is { } problem) throw new WorkspaceException(WorkspaceException.RootInUse, problem);
    }

    /// <summary>The fixed settings.</summary>
    public WorkspaceManagerOptions Options => options;

    /// <summary>The branch Keep the branch creates for <paramref name="slot"/> of <paramref name="run"/>.</summary>
    public static string BranchName(string run, string slot) => BranchPrefix + run + "-" + slot;

    /// <summary>Whether <paramref name="run"/> is a valid run id: a letter or digit, then letters, digits, <c>-</c> and <c>_</c>, at most
    /// 64 characters, and not a Windows device name.</summary>
    public static bool IsValidRun(string? run) => run is not null && RunId().IsMatch(run) && !IsDeviceName(run);

    /// <summary>Whether <paramref name="slot"/> is a valid slot name: a lowercase letter or digit, then lowercase letters, digits, <c>-</c>
    /// and <c>_</c>, at most 32 characters, not <see cref="BaseFolder"/> and not a Windows device name.</summary>
    public static bool IsValidSlot(string? slot) => slot is not null && SlotName().IsMatch(slot) && slot != BaseFolder && !IsDeviceName(slot);

    /// <summary>The folder of <paramref name="run"/>: <c>&lt;root&gt;\&lt;run&gt;</c>.</summary>
    /// <exception cref="WorkspaceException"><see cref="WorkspaceException.Invalid"/> for an invalid run id.</exception>
    public string RunDirectory(string run)
    {
        ValidateRun(run);
        return WorkspaceTree.Inside(Root, Path.Join(Root, run));
    }

    /// <summary>The workspace folder of <paramref name="slot"/>: <c>&lt;root&gt;\&lt;run&gt;\&lt;slot&gt;</c>.</summary>
    /// <exception cref="WorkspaceException"><see cref="WorkspaceException.Invalid"/> for an invalid run id or slot.</exception>
    public string SlotDirectory(string run, string slot)
    {
        ValidateSlot(slot);
        return WorkspaceTree.Inside(Root, Path.Join(RunDirectory(run), slot));
    }

    /// <summary>Looks at a project folder for agent compare's setup: whether it is a git repo, its branch, HEAD and local branches, its
    /// uncommitted changes, the size of a worktree and of a copy, and which isolation <paramref name="inspect"/>'s choice comes to.</summary>
    /// <exception cref="WorkspaceException"><see cref="WorkspaceException.ProjectMissing"/>, <see cref="WorkspaceException.Invalid"/>, or
    /// <see cref="WorkspaceException.RootInUse"/> when the workspaces folder already holds other files (so a run would be refused).</exception>
    public async Task<ProjectInspection> InspectAsync(string projectPath, WorkspaceInspectOptions? inspect = null, CancellationToken cancellationToken = default)
    {
        inspect ??= new WorkspaceInspectOptions();
        var project = Project(projectPath);
        RequireUsableRoot();
        var excludes = ExcludeList(inspect.ExtraExcludes);
        var facts = await GitFactsAsync(project, withDetails: true, cancellationToken).ConfigureAwait(false);
        var kind = inspect.Isolation switch
        {
            WorkspaceIsolation.Copy => WorkspaceKind.Copy,
            _ => facts.Unavailable is null ? WorkspaceKind.Worktree : WorkspaceKind.Copy,
        };
        TreeListing? listing = null;
        if (inspect.EstimateCopy || kind == WorkspaceKind.Copy)
        {
            var rules = CopyRules(excludes, inspect.UseGitignore ? OuterIgnores(facts.RepoRoot, facts.Subpath) : [], rulesRoot: null, inspect.UseGitignore);
            listing = await Task.Run(() => WorkspaceTree.Walk(project, rules, options.EstimateFileLimit, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        return new ProjectInspection
        {
            ProjectPath = project,
            GitAvailable = facts.GitAvailable,
            IsGitRepo = facts.Unavailable is null,
            RepoRoot = facts.RepoRoot,
            ProjectSubpath = facts.Subpath,
            Kind = kind,
            WorktreeUnavailable = facts.Unavailable,
            CurrentBranch = facts.Branch,
            HeadCommit = facts.Head,
            ShortCommit = facts.Short,
            Branches = facts.Branches,
            Uncommitted = facts.Uncommitted,
            CheckoutBytes = facts.CheckoutBytes,
            CopyBytes = listing?.Bytes,
            CopyFiles = listing?.Files.Count,
            CopyEstimateComplete = listing?.Complete ?? true,
            SkippedLinks = listing?.Links ?? [],
            Unreadable = listing?.Unreadable ?? [],
            Excludes = excludes,
            UseGitignore = inspect.UseGitignore,
            WarnAboveBytes = inspect.WarnAboveBytes ?? options.WarnAboveBytes,
            WorkspacesRoot = Root,
        };
    }

    /// <summary>Makes a run's workspaces: one detached worktree per slot at the base commit, or a base copy plus one working copy per slot.
    /// Idempotent: asking again for a ready run with the same project and slots answers it as it is (a restart resumes it). A run folder
    /// left unfinished by a crash is removed first. When any step fails, everything made so far is removed and the error is thrown.</summary>
    /// <exception cref="WorkspaceException">An invalid request (<see cref="WorkspaceException.Invalid"/>), a missing project, worktrees
    /// asked for outside a git repo (<see cref="WorkspaceException.NotGit"/>), a copy too large without
    /// <see cref="WorkspaceRequest.ConfirmLargeCopy"/> (<see cref="WorkspaceException.TooLarge"/>), the run existing with other
    /// workspaces (<see cref="WorkspaceException.Exists"/>), a workspaces folder that already holds other files
    /// (<see cref="WorkspaceException.RootInUse"/>: nothing is written or removed there), or a git failure.</exception>
    public async Task<WorkspaceRun> CreateAsync(WorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRun(request.Run);
        var slots = ValidateSlots(request.Slots);
        var project = Project(request.ProjectPath);
        var excludes = ExcludeList(request.ExtraExcludes);
        var folder = RunDirectory(request.Run);
        var gate = Gate(request.Run);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = WorkspaceManifest.Read(folder);
            if (existing is { State: WorkspaceManifest.Ready })
            {
                if (WorkspaceTree.SamePath(existing.ProjectPath, project) &&
                    existing.Slots.Select(slot => slot.Slot).SequenceEqual(slots.Select(slot => slot.Slot), StringComparer.OrdinalIgnoreCase))
                    return ToRun(existing, folder, measure: false, CancellationToken.None);
                throw new WorkspaceException(WorkspaceException.Exists, $"Run {request.Run} already has workspaces for another project or other models.");
            }
            RequireUsableRoot(); // before anything in the root is removed: a folder of the user's own named like the run stays
            if (existing is not null || Directory.Exists(folder) || WorkspaceTree.IsLink(folder))
                await RemoveRunFolderAsync(folder, existing, cancellationToken).ConfigureAwait(false);

            var facts = await GitFactsAsync(project, withDetails: false, cancellationToken).ConfigureAwait(false);
            var kind = request.Isolation switch
            {
                WorkspaceIsolation.Copy => WorkspaceKind.Copy,
                WorkspaceIsolation.Worktree when facts.Unavailable is not null => throw new WorkspaceException(WorkspaceException.NotGit,
                    facts.Unavailable + " Use a temporary copy instead."),
                _ => facts.Unavailable is null ? WorkspaceKind.Worktree : WorkspaceKind.Copy,
            };
            var now = time.GetUtcNow();
            var manifest = new WorkspaceManifest
            {
                Run = request.Run,
                Kind = kind,
                ProjectPath = project,
                RepoRoot = facts.RepoRoot,
                ProjectSubpath = facts.Subpath,
                CreatedAt = now,
                ExpiresAt = now + options.Retention,
                Excludes = [.. excludes],
                UseGitignore = request.UseGitignore,
                Slots = [.. slots.Select(slot => new ManifestSlot { Slot = slot.Slot, Label = slot.Label })],
            };

            // A copy is measured before anything is written, so a refused copy leaves nothing behind.
            TreeListing? listing = null;
            if (kind == WorkspaceKind.Copy)
            {
                manifest.OuterIgnores = request.UseGitignore ? OuterIgnores(facts.RepoRoot, facts.Subpath) : [];
                var rules = CopyRules(excludes, manifest.OuterIgnores, rulesRoot: null, manifest.UseGitignore);
                listing = await Task.Run(() => WorkspaceTree.Walk(project, rules, null, cancellationToken), cancellationToken).ConfigureAwait(false);
                var limit = request.WarnAboveBytes ?? options.WarnAboveBytes;
                if (listing.Bytes > limit && !request.ConfirmLargeCopy)
                    throw new WorkspaceException(WorkspaceException.TooLarge,
                        $"A copy of this folder is {WorkspaceText.Size(listing.Bytes)}, more than {WorkspaceText.Size(limit)}. Confirm to copy it anyway, or add excludes.");
            }

            EnsureRoot();
            Directory.CreateDirectory(folder);
            manifest.Write(folder);
            try
            {
                if (kind == WorkspaceKind.Worktree) await CreateWorktreesAsync(manifest, folder, request.BaseRef, facts, cancellationToken).ConfigureAwait(false);
                else await CreateCopiesAsync(manifest, folder, listing!, cancellationToken).ConfigureAwait(false);
                manifest.State = WorkspaceManifest.Ready;
                manifest.Write(folder);
            }
            catch (Exception failure)
            {
                try { await RemoveRunFolderAsync(folder, manifest, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception cleanup) when (cleanup is not OperationCanceledException)
                {
                    log.LogWarning(cleanup, "Agent compare: the unfinished workspaces of run {Run} could not be removed after {Failure}; the next cleanup removes them.",
                        request.Run, failure.GetType().Name);
                }
                throw;
            }
            return ToRun(manifest, folder, measure: false, CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A run's workspaces, or null when the run has none (never made, unfinished, or all discarded).</summary>
    /// <param name="run">The run id.</param>
    /// <param name="measure">Also measure each workspace's disk use (a walk of each folder).</param>
    /// <param name="cancellationToken">Stops the measuring.</param>
    public async Task<WorkspaceRun?> GetAsync(string run, bool measure = false, CancellationToken cancellationToken = default)
    {
        var folder = RunDirectory(run);
        var manifest = WorkspaceManifest.Read(folder);
        if (manifest is not { State: WorkspaceManifest.Ready }) return null;
        return measure ? await Task.Run(() => ToRun(manifest, folder, measure: true, cancellationToken), cancellationToken).ConfigureAwait(false)
            : ToRun(manifest, folder, measure: false, cancellationToken);
    }

    /// <summary>Every ready run's workspaces, newest first (Settings › Data).</summary>
    /// <param name="measure">Also measure each workspace's disk use.</param>
    /// <param name="cancellationToken">Stops the measuring.</param>
    public async Task<IReadOnlyList<WorkspaceRun>> ListAsync(bool measure = false, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Root)) return [];
        var found = new List<WorkspaceRun>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(folder);
            if (!IsValidRun(name) || WorkspaceTree.IsLink(folder)) continue;
            if (await GetAsync(name, measure, cancellationToken).ConfigureAwait(false) is { } run) found.Add(run);
        }
        return [.. found.OrderByDescending(run => run.CreatedAt).ThenBy(run => run.Run, StringComparer.Ordinal)];
    }

    /// <summary>The disk use of the whole workspaces root (0 when it does not exist).</summary>
    public Task<long> MeasureRootAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => WorkspaceTree.Measure(Root, cancellationToken), cancellationToken);

    /// <summary>Removes one workspace (<c>git worktree remove</c>, or a folder delete for a copy); when it was the run's last, the run
    /// folder goes too. Never follows a link out of the workspace. The workspace's diff is not kept here: read it with
    /// <see cref="DiffAsync"/> first and keep its patch with the run (the agent compare runner does, and <see cref="WorkspacePatch.Read"/>
    /// reads it back).</summary>
    /// <returns>Whether there was a workspace to remove.</returns>
    public async Task<bool> DiscardAsync(string run, string slot, CancellationToken cancellationToken = default)
    {
        ValidateSlot(slot);
        var folder = RunDirectory(run);
        var gate = Gate(run);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifest = WorkspaceManifest.Read(folder);
            if (manifest?.Find(slot) is not { } entry) return false;
            var removed = entry.State == WorkspaceSlotState.Ready || Directory.Exists(SlotDirectory(run, entry.Slot));
            await RemoveSlotAsync(manifest, folder, entry, cancellationToken).ConfigureAwait(false);
            entry.State = WorkspaceSlotState.Discarded;
            if (manifest.Slots.All(other => other.State == WorkspaceSlotState.Discarded))
                await RemoveRunFolderAsync(folder, manifest, cancellationToken).ConfigureAwait(false);
            else manifest.Write(folder);
            return removed;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Removes all of a run's workspaces and its folder. In a root that isn't this app's (no <see cref="MarkerFile"/>), only a
    /// folder with a run manifest is removed: a folder of the user's own named like the run stays.</summary>
    /// <returns>Whether there was anything to remove.</returns>
    public async Task<bool> DiscardRunAsync(string run, CancellationToken cancellationToken = default)
    {
        var folder = RunDirectory(run);
        var gate = Gate(run);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(folder) && !WorkspaceTree.IsLink(folder)) return false;
            var manifest = WorkspaceManifest.Read(folder);
            if (manifest is null && !OwnsRoot) return false;
            await RemoveRunFolderAsync(folder, manifest, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Removes every run's workspaces except the active ones (Settings › Data's Clean up).</summary>
    /// <param name="isActive">Says whether a run is still in use (running, or continued as a chat); null when none is.</param>
    /// <param name="cancellationToken">Stops between runs.</param>
    public async Task<WorkspaceCleanupReport> DiscardAllAsync(Func<string, bool>? isActive = null, CancellationToken cancellationToken = default) =>
        await SweepAsync(isActive, removeAll: true, cancellationToken).ConfigureAwait(false);

    /// <summary>The startup and daily cleanup: removes leftovers (run folders without a finished manifest, unknown folders), workspaces
    /// marked discarded that are still on disk, and runs older than the retention unless <paramref name="isActive"/> says they are in use.
    /// Runs busy with another operation are skipped. Removes nothing unless the root carries <see cref="MarkerFile"/>.</summary>
    public async Task<WorkspaceCleanupReport> CleanupAsync(Func<string, bool>? isActive = null, CancellationToken cancellationToken = default) =>
        await SweepAsync(isActive, removeAll: false, cancellationToken).ConfigureAwait(false);

    private async Task<WorkspaceCleanupReport> SweepAsync(Func<string, bool>? isActive, bool removeAll, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Root) || !File.Exists(Path.Join(Root, MarkerFile))) return new WorkspaceCleanupReport();
        var now = time.GetUtcNow();
        var removed = new List<WorkspaceRemoval>();
        var failed = new List<WorkspaceFailure>();
        var kept = 0;
        foreach (var folder in Directory.EnumerateDirectories(Root).Order(StringComparer.Ordinal).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(folder);
            if (name.StartsWith('.')) continue;
            if (!IsValidRun(name) || WorkspaceTree.IsLink(folder))
            {
                await Attempt(name, () => RemoveRunFolderAsync(folder, null, cancellationToken), new WorkspaceRemoval(name, null, "leftover")).ConfigureAwait(false);
                continue;
            }
            var gate = Gate(name);
            if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                kept++;
                continue;
            }
            try
            {
                var manifest = WorkspaceManifest.Read(folder);
                var active = isActive?.Invoke(name) ?? false;
                if (manifest is null || manifest.State != WorkspaceManifest.Ready || !string.Equals(manifest.Run, name, StringComparison.OrdinalIgnoreCase))
                    await Attempt(name, () => RemoveRunFolderAsync(folder, manifest, cancellationToken), new WorkspaceRemoval(name, null, "leftover")).ConfigureAwait(false);
                else if (!active && removeAll)
                    await Attempt(name, () => RemoveRunFolderAsync(folder, manifest, cancellationToken), new WorkspaceRemoval(name, null, "removed")).ConfigureAwait(false);
                else if (!active && manifest.ExpiresAt <= now)
                    await Attempt(name, () => RemoveRunFolderAsync(folder, manifest, cancellationToken), new WorkspaceRemoval(name, null, "expired")).ConfigureAwait(false);
                else if (manifest.Slots.All(slot => slot.State == WorkspaceSlotState.Discarded))
                    await Attempt(name, () => RemoveRunFolderAsync(folder, manifest, cancellationToken), new WorkspaceRemoval(name, null, "discarded")).ConfigureAwait(false);
                else
                {
                    kept++;
                    foreach (var slot in manifest.Slots.Where(slot => slot.State == WorkspaceSlotState.Discarded && Directory.Exists(Path.Join(folder, slot.Slot))))
                        await Attempt(name, () => RemoveSlotAsync(manifest, folder, slot, cancellationToken), new WorkspaceRemoval(name, slot.Slot, "discarded")).ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }
        }
        return new WorkspaceCleanupReport { Removed = removed, Failed = failed, Kept = kept };

        async Task Attempt(string run, Func<Task> remove, WorkspaceRemoval removal)
        {
            try
            {
                await remove().ConfigureAwait(false);
                removed.Add(removal);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failed.Add(new WorkspaceFailure(run, error is WorkspaceException ? error.Message : "A file in it is in use or can't be deleted."));
                log.LogWarning(error, "Agent compare: workspaces of run {Run} could not be removed; the next cleanup tries again.", run);
            }
        }
    }

    private async Task CreateWorktreesAsync(WorkspaceManifest manifest, string folder, string? baseRef, GitFacts facts, CancellationToken cancellationToken)
    {
        var repo = facts.RepoRoot!;
        var baseCommit = await ResolveCommitAsync(repo, baseRef ?? "HEAD", cancellationToken).ConfigureAwait(false);
        manifest.BaseCommit = baseCommit;
        manifest.ShortCommit = (await git.RequireAsync(repo, ["rev-parse", "--short", baseCommit], cancellationToken: cancellationToken).ConfigureAwait(false)).Line;
        manifest.BaseBranch = baseRef is null ? facts.Branch
            : (await git.RunAsync(repo, ["rev-parse", "--verify", "--quiet", "refs/heads/" + baseRef], cancellationToken: cancellationToken).ConfigureAwait(false)).Ok ? baseRef : null;
        manifest.NotCopied = (await StatusAsync(repo, inspecting: false, cancellationToken).ConfigureAwait(false)).ToList();
        manifest.Write(folder);
        await projects.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var slot in manifest.Slots)
            {
                var path = SlotDirectory(manifest.Run, slot.Slot);
                await git.RequireAsync(repo, ["worktree", "add", "--detach", "--quiet", path, baseCommit], timeout: options.CheckoutTimeout,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                // The worktree's own repository folder, read now (before any agent can change its .git file) and used from then on.
                slot.GitDir = Path.GetFullPath((await git.RequireAsync(path, ["rev-parse", "--absolute-git-dir"], cancellationToken: cancellationToken)
                    .ConfigureAwait(false)).Line);
                await SeedIndexAsync(folder, slot.Slot, path, cancellationToken).ConfigureAwait(false);
                if (!Directory.Exists(WorkingDirectory(manifest, path)))
                    throw new WorkspaceException(WorkspaceException.Invalid,
                        $"The project folder {manifest.ProjectSubpath} isn't in commit {manifest.ShortCommit}, so a worktree can't start there. Use a temporary copy instead.");
            }
        }
        finally
        {
            projects.Release();
        }
    }

    // The fresh worktree's own index is the base commit's tree with the checkout's file times: copied as the slot's private index, the
    // first diff only re-reads files whose times changed instead of hashing the whole tree. Without it, the first diff starts from
    // `git read-tree <base>` instead.
    private async Task SeedIndexAsync(string folder, string slot, string path, CancellationToken cancellationToken)
    {
        try
        {
            var index = await git.RunAsync(path, ["rev-parse", "--git-path", "index"], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!index.Ok || index.Line.Length == 0) return;
            var source = Path.GetFullPath(index.Line, path);
            if (!File.Exists(source)) return;
            var target = IndexFile(folder, slot);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(error, "Agent compare: the worktree's index could not be copied; the first diff reads the base tree instead.");
        }
    }

    private Task CreateCopiesAsync(WorkspaceManifest manifest, string folder, TreeListing listing, CancellationToken cancellationToken)
    {
        manifest.SkippedLinks = listing.Links;
        manifest.Unreadable = listing.Unreadable;
        manifest.CopyBytes = listing.Bytes;
        return Task.Run(() =>
        {
            var basePath = WorkspaceTree.Inside(Root, Path.Join(folder, BaseFolder));
            var copied = WorkspaceTree.Copy(manifest.ProjectPath, basePath, listing, tolerateMissing: true, cancellationToken);
            manifest.CopyBytes = copied.Bytes;
            foreach (var slot in manifest.Slots)
                WorkspaceTree.Copy(basePath, SlotDirectory(manifest.Run, slot.Slot), copied, tolerateMissing: false, cancellationToken);
        }, cancellationToken);
    }

    private async Task RemoveSlotAsync(WorkspaceManifest manifest, string folder, ManifestSlot slot, CancellationToken cancellationToken)
    {
        var path = SlotDirectory(manifest.Run, slot.Slot);
        if ((File.Exists(Path.Join(path, ".git")) || slot.GitDir is not null) && !WorkspaceTree.IsLink(path))
            await RemoveWorktreeAsync(manifest.RepoRoot, path, cancellationToken, slot.GitDir).ConfigureAwait(false);
        else await Task.Run(() => WorkspaceTree.Delete(path), cancellationToken).ConfigureAwait(false);
        foreach (var file in new[] { IndexFile(folder, slot.Slot), IndexFile(folder, slot.Slot) + ".lock" })
            if (File.Exists(file)) File.Delete(file);
    }

    // Every worktree in the run folder first (so its registration in the project's repository goes too), then the folder itself.
    private async Task RemoveRunFolderAsync(string folder, WorkspaceManifest? manifest, CancellationToken cancellationToken)
    {
        if (WorkspaceTree.IsLink(folder))
        {
            WorkspaceTree.Delete(folder); // a link where a run folder should be: remove the link, never its target
            return;
        }
        if (Directory.Exists(folder))
        {
            foreach (var child in Directory.EnumerateDirectories(folder).ToList())
            {
                var recorded = manifest?.Find(Path.GetFileName(child))?.GitDir;
                if (!WorkspaceTree.IsLink(child) && (File.Exists(Path.Join(child, ".git")) || recorded is not null))
                    await RemoveWorktreeAsync(manifest?.RepoRoot, child, cancellationToken, recorded).ConfigureAwait(false);
            }
        }
        await Task.Run(() => WorkspaceTree.Delete(folder), cancellationToken).ConfigureAwait(false);
    }

    // Deletes the worktree's files ourselves (never following a link), then lets git remove what is left and the worktree's registration
    // (`git worktree remove --force`). Git is only asked when the repository's registration really points at this folder; if git can't do
    // it (the repository moved, a file is locked), the folder is deleted and that one registration is removed directly. The registration
    // is the one recorded when the worktree was made (an agent may have rewritten .git), else the one .git names; either way only when
    // it points back at this worktree.
    private async Task RemoveWorktreeAsync(string? repoRoot, string path, CancellationToken cancellationToken, string? recorded = null)
    {
        WorkspaceTree.Inside(Root, path);
        var dotGit = Path.Join(path, ".git");
        var admin = Registration(dotGit, recorded);
        await Task.Run(() => WorkspaceTree.Delete(path, keepTopLevel: ".git"), cancellationToken).ConfigureAwait(false);
        if (admin is not null)
        {
            var cwd = repoRoot is not null && Directory.Exists(repoRoot) ? repoRoot : Path.GetDirectoryName(Path.GetDirectoryName(admin));
            // Never from inside the workspaces (a registration an agent planted in its workspace would make git read its config).
            if (cwd is not null && Directory.Exists(cwd) && !WorkspaceTree.IsWithin(Root, cwd))
            {
                await projects.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var removed = await git.RunAsync(cwd, ["worktree", "remove", "--force", path], cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (!removed.Ok) log.LogInformation("Agent compare: git worktree remove failed ({Error}); deleting the folder instead.", GitCommand.FirstLine(removed.Error));
                }
                catch (WorkspaceException error) when (error.Code is WorkspaceException.GitMissing or WorkspaceException.GitTimeout)
                {
                    log.LogInformation("Agent compare: {Error} Deleting the worktree folder instead.", error.Message);
                }
                finally
                {
                    projects.Release();
                }
            }
        }
        if (Directory.Exists(path)) await Task.Run(() => WorkspaceTree.Delete(path), cancellationToken).ConfigureAwait(false);
        if (admin is not null && Directory.Exists(admin)) await Task.Run(() => WorkspaceTree.Delete(admin), cancellationToken).ConfigureAwait(false);
    }

    // The worktree's registration folder (<repo>\.git\worktrees\<name>): the recorded one, else the one .git names; only when its gitdir
    // file points back at this worktree's .git.
    private static string? Registration(string dotGit, string? recorded = null)
    {
        try
        {
            string admin;
            if (recorded is not null) admin = Path.GetFullPath(recorded);
            else
            {
                if (!File.Exists(dotGit)) return null;
                var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim();
                if (line is null || !line.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
                admin = Path.GetFullPath(line["gitdir:".Length..].Trim(), Path.GetDirectoryName(dotGit)!);
            }
            if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(admin)), "worktrees", StringComparison.OrdinalIgnoreCase)) return null;
            var back = Path.Join(admin, "gitdir");
            if (!File.Exists(back)) return null;
            var target = File.ReadLines(back).FirstOrDefault()?.Trim();
            return target is { Length: > 0 } && WorkspaceTree.SamePath(Path.GetFullPath(target, admin), dotGit) ? admin : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private WorkspaceRun ToRun(WorkspaceManifest manifest, string folder, bool measure, CancellationToken cancellationToken)
    {
        var slots = manifest.Slots.Select(slot =>
        {
            var path = Path.Join(folder, slot.Slot);
            return new WorkspaceSlot
            {
                Slot = slot.Slot,
                Label = slot.Label,
                Path = path,
                WorkingDirectory = WorkingDirectory(manifest, path),
                State = slot.State,
                AppliedAt = slot.AppliedAt,
                Branch = slot.Branch,
                BranchCommit = slot.BranchCommit,
                Bytes = measure && slot.State == WorkspaceSlotState.Ready ? WorkspaceTree.Measure(path, cancellationToken) : null,
            };
        }).ToList();
        return new WorkspaceRun
        {
            Run = manifest.Run,
            Kind = manifest.Kind,
            ProjectPath = manifest.ProjectPath,
            RepoRoot = manifest.RepoRoot,
            ProjectSubpath = manifest.ProjectSubpath,
            BaseCommit = manifest.BaseCommit,
            ShortCommit = manifest.ShortCommit,
            BaseBranch = manifest.BaseBranch,
            CreatedAt = manifest.CreatedAt,
            ExpiresAt = manifest.ExpiresAt,
            Directory = folder,
            Excludes = manifest.Excludes,
            UseGitignore = manifest.UseGitignore,
            NotCopied = manifest.NotCopied,
            SkippedLinks = manifest.SkippedLinks,
            Unreadable = manifest.Unreadable,
            CopyBytes = manifest.CopyBytes,
            BaseBytes = measure && manifest.Kind == WorkspaceKind.Copy ? WorkspaceTree.Measure(Path.Join(folder, BaseFolder), cancellationToken) : null,
            Slots = slots,
        };
    }

    private static string WorkingDirectory(WorkspaceManifest manifest, string slotPath) =>
        manifest.Kind == WorkspaceKind.Worktree && manifest.ProjectSubpath.Length > 0 ? WorkspaceTree.Join(slotPath, manifest.ProjectSubpath) : slotPath;

    private static string IndexFile(string folder, string slot) => Path.Join(folder, WorkspaceManifest.MetaFolder, slot + ".index");

    private SemaphoreSlim Gate(string run) => runs.GetOrAdd(run, _ => new SemaphoreSlim(1, 1));

    // Claims the root for this app: creates it with the marker, or writes the marker into a root that exists but is empty. A root that
    // exists without the marker and already holds anything is someone else's folder, so the run is refused (RootInUse) and the marker is
    // never written there: otherwise the cleanup would remove the user's folders in it as leftovers.
    private void EnsureRoot()
    {
        lock (rootGate)
        {
            if (UnlockedRootProblem() is { } problem) throw new WorkspaceException(WorkspaceException.RootInUse, problem);
            if (OwnsRoot) return;
            Directory.CreateDirectory(Root);
            using var marker = new FileStream(Path.Join(Root, MarkerFile), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            marker.Write(System.Text.Encoding.UTF8.GetBytes("PiSharp Web Agent: agent compare workspaces. Each folder is one run. The app removes "
                + "a run's folder on Discard and after " + options.Retention.TotalDays.ToString("0", CultureInfo.InvariantCulture)
                + " days, and any other folder here when it starts, so keep nothing else in this folder.\n"));
        }
    }

    /// <summary>Why a network or device path is refused (<see cref="IsNetworkOrDevicePath"/>).</summary>
    public const string NetworkPathMessage =
        @"The project folder must be on this computer: a network path (\\server\share) or a device path (\\?\, \\.\) isn't looked at.";

    /// <summary>Whether <paramref name="path"/> is a Windows network path (UNC: <c>\\server\share</c>, <c>//server/share</c>) or device path
    /// (<c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>). Agent compare never looks at one: <c>Directory.Exists</c> or git on a UNC path reaches the
    /// named host over SMB or WebDAV and offers it the user's Windows (NTLM) credentials. Always false off Windows, which has neither.</summary>
    public static bool IsNetworkOrDevicePath(string? path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path)) return false;
        var text = path.TrimStart();
        return (text.Length >= 2 && text[0] is ('\\' or '/') && text[1] is ('\\' or '/')) || text.StartsWith(@"\??\", StringComparison.Ordinal);
    }

    private string Project(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || projectPath.Any(char.IsControl) || !Path.IsPathFullyQualified(projectPath))
            throw new WorkspaceException(WorkspaceException.Invalid, "The project folder must be an absolute path.");
        // Before anything touches the path: a UNC path would send the user's credentials to the host it names.
        if (IsNetworkOrDevicePath(projectPath)) throw new WorkspaceException(WorkspaceException.Invalid, NetworkPathMessage);
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));
        if (IsNetworkOrDevicePath(project)) throw new WorkspaceException(WorkspaceException.Invalid, NetworkPathMessage);
        if (!Directory.Exists(project)) throw new WorkspaceException(WorkspaceException.ProjectMissing, "The project folder doesn't exist.");
        if (WorkspaceTree.IsWithin(Root, project))
            throw new WorkspaceException(WorkspaceException.Invalid, "The project folder is inside the workspaces folder; choose the project itself.");
        return project;
    }

    private List<WorkspaceSlotRequest> ValidateSlots(IReadOnlyList<WorkspaceSlotRequest>? slots)
    {
        if (slots is null || slots.Count == 0 || slots.Count > options.MaxSlots)
            throw new WorkspaceException(WorkspaceException.Invalid, $"A run needs 1 to {options.MaxSlots} workspaces.");
        foreach (var slot in slots)
        {
            ValidateSlot(slot.Slot);
            if (slot.Label is { } label && (label.Length > 200 || label.Any(char.IsControl)))
                throw new WorkspaceException(WorkspaceException.Invalid, "A workspace label is too long or holds control characters.");
        }
        if (slots.Select(slot => slot.Slot).Distinct(StringComparer.OrdinalIgnoreCase).Count() != slots.Count)
            throw new WorkspaceException(WorkspaceException.Invalid, "Each workspace needs its own slot name.");
        return [.. slots];
    }

    private static void ValidateRun(string run)
    {
        if (!IsValidRun(run)) throw new WorkspaceException(WorkspaceException.Invalid, "That isn't a valid run id.");
    }

    private static void ValidateSlot(string slot)
    {
        if (!IsValidSlot(slot)) throw new WorkspaceException(WorkspaceException.Invalid, "That isn't a valid workspace slot.");
    }

    private static bool IsDeviceName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
            stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3]));
    }

    // The default excludes, then the extra ones (trimmed, blank lines and comments dropped).
    private static List<string> ExcludeList(IReadOnlyList<string>? extra)
    {
        var list = new List<string>(WorkspaceManagerOptions.DefaultExcludes);
        foreach (var raw in extra ?? [])
        {
            var pattern = raw?.Trim() ?? "";
            if (pattern.Length == 0 || pattern.StartsWith('#')) continue;
            if (pattern.Length > 200 || pattern.Any(char.IsControl))
                throw new WorkspaceException(WorkspaceException.Invalid, "An exclude pattern is too long or holds control characters.");
            if (!list.Contains(pattern, StringComparer.Ordinal)) list.Add(pattern);
        }
        if (list.Count > 100) throw new WorkspaceException(WorkspaceException.Invalid, "Too many exclude patterns (at most 100).");
        return list;
    }

    // The exclude list as gitignore lines: the default names are folders (".git" is a file in a worktree, so it is either).
    private static IgnoreList ExcludePatterns(IEnumerable<string> excludes) => IgnoreList.Parse(excludes.Select(pattern =>
        WorkspaceManagerOptions.DefaultExcludes.Contains(pattern, StringComparer.Ordinal) && pattern != ".git" ? pattern + "/" : pattern));

    // The rules a copy is walked with: the excludes always; with useGitignore, the gitignore sources above the project (outer) and the
    // walked tree's own .gitignore files too.
    private TreeRules CopyRules(IEnumerable<string> excludes, IEnumerable<ManifestIgnore> outer, string? rulesRoot, bool useGitignore)
    {
        var stack = IgnoreStack.Empty;
        if (useGitignore)
            foreach (var source in outer) stack = stack.Push(IgnoreList.Parse(source.Lines, "", source.Prefix));
        return new TreeRules(ExcludePatterns(excludes), stack, rulesRoot, [Root], useGitignore);
    }

    // The gitignore sources above the project inside its repository, outermost first: info/exclude, then each parent's .gitignore.
    private static List<ManifestIgnore> OuterIgnores(string? repoRoot, string subpath)
    {
        var sources = new List<ManifestIgnore>();
        if (repoRoot is null) return sources;
        var projectPrefix = subpath.Length == 0 ? "" : subpath + "/";
        var exclude = Path.Join(repoRoot, ".git", "info", "exclude");
        if (File.Exists(exclude)) Add(exclude, projectPrefix);
        if (subpath.Length == 0) return sources;
        var parts = subpath.Split('/');
        for (var depth = 0; depth < parts.Length; depth++)
            Add(Path.Join(repoRoot, string.Join(Path.DirectorySeparatorChar, parts.Take(depth)), ".gitignore"), string.Join('/', parts.Skip(depth)) + "/");
        return sources;

        void Add(string file, string prefix)
        {
            try
            {
                if (!File.Exists(file)) return;
                var lines = File.ReadAllLines(file).Where(line => line.Trim().Length > 0 && !line.StartsWith('#')).ToList();
                if (lines.Count > 0) sources.Add(new ManifestIgnore { Prefix = prefix, Lines = lines });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record GitFacts(bool GitAvailable, string? RepoRoot, string Subpath, string? Head, string? Short, string? Branch,
        IReadOnlyList<string> Branches, IReadOnlyList<UncommittedChange> Uncommitted, long? CheckoutBytes, string? Unavailable);

    // What git says about the project. Unavailable is null when worktrees can be used.
    private async Task<GitFacts> GitFactsAsync(string project, bool withDetails, CancellationToken cancellationToken)
    {
        GitResult top;
        try { top = await git.RunAsync(project, ["rev-parse", "--show-toplevel", "--show-prefix"], cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (WorkspaceException error) when (error.Code == WorkspaceException.GitMissing)
        {
            return new GitFacts(false, null, "", null, null, null, [], [], null, "Git isn't installed, so worktrees can't be used.");
        }
        if (!top.Ok) return new GitFacts(true, null, "", null, null, null, [], [], null, "This folder isn't in a git repository.");
        var lines = top.Text.Split('\n');
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(lines[0].Trim()));
        var subpath = lines.Length > 1 ? lines[1].Trim().TrimEnd('/') : "";
        var branchResult = await git.RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken: cancellationToken).ConfigureAwait(false);
        var branch = branchResult.Ok && branchResult.Line.Length > 0 ? branchResult.Line : null;
        var head = await git.RunAsync(root, ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], cancellationToken: cancellationToken).ConfigureAwait(false);
        IReadOnlyList<UncommittedChange> uncommitted = withDetails ? await StatusAsync(root, inspecting: true, cancellationToken).ConfigureAwait(false) : [];
        if (!head.Ok)
            return new GitFacts(true, root, subpath, null, null, branch, [], uncommitted, null, "The repository has no commits yet, so a worktree has nothing to start from.");
        var commit = head.Line;
        var shortCommit = (await git.RequireAsync(root, ["rev-parse", "--short", commit], cancellationToken: cancellationToken).ConfigureAwait(false)).Line;
        // A sub-folder that is new or ignored isn't in the commit, so a worktree would have no such folder to start in.
        string? unavailable = null;
        if (subpath.Length > 0 && !(await git.RunAsync(root, ["cat-file", "-e", commit + ":" + subpath], cancellationToken: cancellationToken).ConfigureAwait(false)).Ok)
            unavailable = "The project folder isn't in the repository's last commit (it's new or ignored), so a worktree has nothing to start from.";
        if (!withDetails) return new GitFacts(true, root, subpath, commit, shortCommit, branch, [], [], null, unavailable);

        var branches = await git.RunAsync(root, ["for-each-ref", "--count=200", "--format=%(refname:short)", "refs/heads"], cancellationToken: cancellationToken).ConfigureAwait(false);
        long? checkout = null;
        var tree = await git.RunAsync(root, ["ls-tree", "-r", "-l", "-z", commit], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (tree.Ok)
        {
            long total = 0;
            foreach (var entry in tree.Text.Split('\0'))
            {
                // "<mode> blob <id> <size>\t<path>"
                var tab = entry.IndexOf('\t');
                if (tab < 0) continue;
                var fields = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 4 && fields[1] == "blob" && long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var size)) total += size;
            }
            checkout = total;
        }
        return new GitFacts(true, root, subpath, commit, shortCommit, branch,
            branches.Ok ? [.. branches.Text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)] : [],
            uncommitted, checkout, unavailable);
    }

    // inspecting: the folder is only being looked at (the setup's project row), so the repository's own filter drivers are switched off.
    // Making the workspaces, Apply and Keep the branch run them as git normally does: the user chose to run agents on this project, a
    // checkout runs its smudge filters anyway, and a filter such as git-crypt's must clean what Keep commits.
    private async Task<IReadOnlyList<UncommittedChange>> StatusAsync(string repoRoot, bool inspecting, CancellationToken cancellationToken)
    {
        List<string> arguments = inspecting ? [.. await NeutralFiltersAsync(repoRoot, cancellationToken).ConfigureAwait(false), .. GitStatus.Arguments]
            : [.. GitStatus.Arguments];
        var status = await git.RequireAsync(repoRoot, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        return GitStatus.Parse(status.Output);
    }

    // `git status` runs a filter driver's clean command on a file whose stat no longer matches the index, so a folder whose own .git/config
    // names a filter (an extracted download, say) would run that command just by being looked at. This switches off (-c
    // filter.<driver>.clean= and so on) every driver the repository's own config defines (local or worktree scope, includes too); the
    // drivers of the user's global and system config (git-lfs) keep working. A file such a driver would have cleaned is compared as it is,
    // so it may be listed as changed when it isn't.
    private async Task<IReadOnlyList<string>> NeutralFiltersAsync(string repoRoot, CancellationToken cancellationToken)
    {
        string[] arguments = ["config", "--show-scope", "--name-only", "-z", "--get-regexp", @"^filter\."];
        var listed = await git.RunAsync(repoRoot, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (listed.ExitCode == 1) return []; // no filter at all
        if (!listed.Ok) throw GitCommand.Failed(arguments, listed);
        var fields = listed.Text.Split('\0');
        var drivers = new SortedSet<string>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < fields.Length; index += 2)
        {
            var (scope, name) = (fields[index].Trim(), fields[index + 1]);
            if (scope is not ("local" or "worktree")) continue;
            var last = name.LastIndexOf('.');
            if (!name.StartsWith("filter.", StringComparison.OrdinalIgnoreCase) || last <= "filter.".Length) continue;
            var driver = name["filter.".Length..last];
            // "-c name=value" splits at the first '=', so such a driver can't be switched off on the command line.
            if (driver.Contains('=') || driver.Any(char.IsControl))
                throw new WorkspaceException(WorkspaceException.Invalid, "This repository's git config names a filter that can't be switched off safely, so its changes aren't listed.");
            drivers.Add(driver);
        }
        return [.. drivers.SelectMany(driver => new[]
        {
            "-c", $"filter.{driver}.clean=", "-c", $"filter.{driver}.smudge=", "-c", $"filter.{driver}.process=", "-c", $"filter.{driver}.required=false",
        })];
    }

    private async Task<string> ResolveCommitAsync(string repoRoot, string reference, CancellationToken cancellationToken)
    {
        if (reference.Length == 0 || reference.Length > 256 || reference.StartsWith('-') || reference.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new WorkspaceException(WorkspaceException.Invalid, "The base must be a branch, tag or commit.");
        var result = await git.RunAsync(repoRoot, ["rev-parse", "--verify", "--quiet", reference + "^{commit}"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Ok || !CommitId().IsMatch(result.Line))
            throw new WorkspaceException(WorkspaceException.Invalid, $"\"{reference}\" isn't a commit in this repository.");
        return result.Line;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex RunId();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlotName();

    [GeneratedRegex("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitId();
}
