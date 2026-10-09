// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/core/footer-data-provider.ts.
// Also ports closeWatcher, FS_WATCH_RETRY_DELAY_MS and watchWithErrorHandler (coding-agent/src/utils/fs-watch.ts). Filesystem,
// git process, timer and environment access go through FooterDataProviderHost so tests can drive watchers and the debounce clock.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>Source GitPaths.</summary>
internal sealed record GitPaths(string RepoDir, string CommonGitDir, string HeadPath);

/// <summary>The fields of fs.Stats that the HEAD and tables.list pollers compare.</summary>
internal readonly record struct FooterFileStat(double MtimeMs, double CtimeMs, long Size);

/// <summary>spawnSync's status (null when the process could not start) and stdout.</summary>
internal sealed record FooterSpawnResult(int? Status, string Stdout);

/// <summary>execFile's callback arguments: whether it failed (non-zero exit or spawn error) and stdout.</summary>
internal sealed record FooterExecResult(bool Error, string Stdout);

/// <summary>
/// The Node APIs footer-data-provider.ts uses (fs, child_process, setTimeout, process.env, process.platform). Override members in tests.
/// Watch and WatchFile listeners and SetTimeout callbacks may run on any thread; the provider synchronizes its own state.
/// </summary>
internal class FooterDataProviderHost
{
    public static FooterDataProviderHost Default { get; } = new();

    /// <summary>existsSync.</summary>
    public virtual bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    /// <summary>statSync(path).isFile(); throws when the path cannot be stat'ed.</summary>
    public virtual bool IsFile(string path) => File.Exists(path) ? true : Directory.Exists(path) ? false : throw new FileNotFoundException(path);
    /// <summary>statSync(path).isDirectory().</summary>
    public virtual bool IsDirectory(string path) => Directory.Exists(path);
    /// <summary>readFileSync(path, "utf8").</summary>
    public virtual string ReadFile(string path) => File.ReadAllText(path, new UTF8Encoding(false));

    /// <summary>
    /// Source watchWithErrorHandler: fs.watch with an error handler. The listener receives (eventType, filename). On a synchronous
    /// failure <paramref name="onError"/> runs and null is returned. Disposing closes the watcher (closeWatcher ignores close errors).
    /// </summary>
    public virtual IDisposable? Watch(string path, Action<string, string?> listener, Action onError)
    {
        try
        {
            FileSystemWatcher watcher;
            if (Directory.Exists(path)) watcher = new FileSystemWatcher(path);
            else if (File.Exists(path)) watcher = new FileSystemWatcher(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileName(path));
            else throw new FileNotFoundException(path);
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;
            watcher.Changed += (_, e) => listener("change", e.Name);
            watcher.Created += (_, e) => listener("rename", e.Name);
            watcher.Deleted += (_, e) => listener("rename", e.Name);
            watcher.Renamed += (_, e) => listener("rename", e.Name);
            watcher.Error += (_, _) => onError();
            watcher.EnableRaisingEvents = true;
            return new ClosingHandle(watcher);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            onError();
            return null;
        }
    }

    /// <summary>fs.watchFile: polls the file's stats every <paramref name="intervalMs"/> and reports (current, previous) on change.</summary>
    public virtual IDisposable WatchFile(string path, int intervalMs, Action<FooterFileStat, FooterFileStat> listener)
    {
        var gate = new object();
        var previous = StatOrEmpty(path);
        var timer = new Timer(_ =>
        {
            FooterFileStat current, before;
            lock (gate)
            {
                current = StatOrEmpty(path);
                before = previous;
                if (current == previous) return;
                previous = current;
            }
            listener(current, before);
        }, null, intervalMs, intervalMs);
        return new ClosingHandle(timer);
    }

    private static FooterFileStat StatOrEmpty(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return default;
            return new(info.LastWriteTimeUtc.Subtract(DateTime.UnixEpoch).TotalMilliseconds, info.CreationTimeUtc.Subtract(DateTime.UnixEpoch).TotalMilliseconds, info.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return default; }
    }

    /// <summary>spawnSync(command, args, { cwd, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).</summary>
    public virtual FooterSpawnResult SpawnSync(string command, IReadOnlyList<string> args, string cwd)
    {
        try
        {
            using var process = Process.Start(StartInfo(command, args, cwd));
            if (process is null) return new(null, "");
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return new(process.ExitCode, stdout);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return new(null, ""); }
    }

    /// <summary>execFile(command, args, { cwd, encoding: "utf8" }).</summary>
    public virtual async Task<FooterExecResult> ExecFile(string command, IReadOnlyList<string> args, string cwd)
    {
        try
        {
            using var process = Process.Start(StartInfo(command, args, cwd));
            if (process is null) return new(true, "");
            var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            return new(process.ExitCode != 0, stdout);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return new(true, ""); }
    }

    private static ProcessStartInfo StartInfo(string command, IReadOnlyList<string> args, string cwd)
    {
        var info = new ProcessStartInfo(command)
        {
            WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false), CreateNoWindow = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    /// <summary>setTimeout; disposing is clearTimeout.</summary>
    public virtual IDisposable SetTimeout(Action callback, int milliseconds)
    {
        var handle = new TimeoutHandle();
        handle.Timer = new Timer(_ => { if (handle.TryFire()) callback(); }, null, Math.Max(0, milliseconds), Timeout.Infinite);
        return handle;
    }

    /// <summary>process.env.</summary>
    public virtual string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);
    /// <summary>process.platform === "linux".</summary>
    public virtual bool IsLinux => OperatingSystem.IsLinux();

    private sealed class ClosingHandle(IDisposable inner) : IDisposable
    {
        public void Dispose()
        {
            try { inner.Dispose(); } catch (Exception) { /* Ignore watcher close errors */ }
        }
    }

    private sealed class TimeoutHandle : IDisposable
    {
        private int state;
        public Timer? Timer;
        public bool TryFire() { var fired = Interlocked.CompareExchange(ref state, 1, 0) == 0; if (fired) Timer?.Dispose(); return fired; }
        public void Dispose() { Interlocked.Exchange(ref state, 1); Timer?.Dispose(); }
    }
}

/// <summary>Read-only view for extensions - excludes SetExtensionStatus, SetAvailableProviderCount and Dispose.</summary>
internal interface IReadonlyFooterDataProvider
{
    /// <summary>Current git branch, null if not in repo, "detached" if detached HEAD.</summary>
    string? GetGitBranch();
    /// <summary>Extension status texts set via ctx.ui.setStatus().</summary>
    IReadOnlyDictionary<string, string> GetExtensionStatuses();
    /// <summary>Number of unique providers with available models (for footer display).</summary>
    int GetAvailableProviderCount();
    /// <summary>Subscribe to git branch changes. Returns unsubscribe function.</summary>
    Action OnBranchChange(Action callback);
}

/// <summary>
/// Provides git branch and extension statuses - data not otherwise accessible to extensions.
/// Context usage on ctx.getContextUsage(), token stats on ctx.sessionManager.getEntries(), model info on ctx.model.
/// </summary>
internal sealed partial class FooterDataProvider : IReadonlyFooterDataProvider, IDisposable
{
    /// <summary>Source FS_WATCH_RETRY_DELAY_MS.</summary>
    internal const int FsWatchRetryDelayMs = 5000;
    private const int WatchDebounceMs = 500;
    private static readonly string[] SymbolicRefArgs = ["--no-optional-locks", "symbolic-ref", "--quiet", "--short", "HEAD"];

    private readonly FooterDataProviderHost host;
    private readonly object gate = new();
    private string cwd;
    private readonly Dictionary<string, string> extensionStatuses = new(StringComparer.Ordinal);
    private bool branchResolved;
    private string? cachedBranch;
    private GitPaths? gitPaths;
    private IDisposable? headWatcher;
    private string? headWatchFilePath;
    private IDisposable? headWatchFilePoller;
    private IDisposable? reftableWatcher;
    private IDisposable? reftableTablesListWatcher;
    private string? reftableTablesListPath;
    private IDisposable? reftableTablesListPoller;
    private readonly List<Action> branchChangeCallbacks = [];
    private int availableProviderCount;
    private IDisposable? refreshTimer;
    private IDisposable? gitWatcherRetryTimer;
    private bool refreshInFlight;
    private bool refreshPending;
    private bool disposed;

    public FooterDataProvider(string cwd, FooterDataProviderHost? host = null)
    {
        this.host = host ?? FooterDataProviderHost.Default;
        this.cwd = cwd;
        gitPaths = FindGitPaths(cwd, this.host);
        SetupGitWatcher();
    }

    /// <summary>Test hooks: the watchers the source tests reach through private fields.</summary>
    internal IDisposable? HeadWatcher { get { lock (gate) return headWatcher; } }
    internal IDisposable? ReftableWatcher { get { lock (gate) return reftableWatcher; } }

    /// <summary>
    /// Source findGitPaths: walks up from cwd to the git metadata. Handles both regular git repos (.git is a directory) and worktrees
    /// (.git is a file).
    /// </summary>
    internal static GitPaths? FindGitPaths(string cwd, FooterDataProviderHost? host = null)
    {
        host ??= FooterDataProviderHost.Default;
        var dir = cwd;
        while (true)
        {
            var gitPath = Path.Join(dir, ".git");
            if (host.Exists(gitPath))
            {
                try
                {
                    if (host.IsFile(gitPath))
                    {
                        var content = TextUtils.JsTrim(host.ReadFile(gitPath));
                        if (content.StartsWith("gitdir: ", StringComparison.Ordinal))
                        {
                            var gitDir = Path.GetFullPath(Path.Combine(dir, TextUtils.JsTrim(content[8..])));
                            var headPath = Path.Join(gitDir, "HEAD");
                            if (!host.Exists(headPath)) return null;
                            var commonDirPath = Path.Join(gitDir, "commondir");
                            var commonGitDir = host.Exists(commonDirPath)
                                ? Path.GetFullPath(Path.Combine(gitDir, TextUtils.JsTrim(host.ReadFile(commonDirPath))))
                                : gitDir;
                            return new(dir, commonGitDir, headPath);
                        }
                    }
                    else if (host.IsDirectory(gitPath))
                    {
                        var headPath = Path.Join(gitPath, "HEAD");
                        if (!host.Exists(headPath)) return null;
                        return new(dir, gitPath, headPath);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    return null;
                }
            }
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) return null;
            dir = parent;
        }
    }

    /// <summary>Ask git for the current branch. Returns null on detached HEAD or if git is unavailable.</summary>
    private string? ResolveBranchWithGitSync(string repoDir)
    {
        var result = host.SpawnSync("git", SymbolicRefArgs, repoDir);
        var branch = result.Status == 0 ? TextUtils.JsTrim(result.Stdout) : "";
        return branch.Length > 0 ? branch : null;
    }

    /// <summary>Ask git for the current branch asynchronously. Returns null on detached HEAD or if git is unavailable.</summary>
    private async Task<string?> ResolveBranchWithGitAsync(string repoDir)
    {
        var result = await host.ExecFile("git", SymbolicRefArgs, repoDir).ConfigureAwait(false);
        if (result.Error) return null;
        var branch = TextUtils.JsTrim(result.Stdout);
        return branch.Length > 0 ? branch : null;
    }

    private bool IsWslEnvironment() =>
        host.IsLinux && (!string.IsNullOrEmpty(host.GetEnvironmentVariable("WSL_DISTRO_NAME")) || !string.IsNullOrEmpty(host.GetEnvironmentVariable("WSL_INTEROP")));

    [GeneratedRegex(@"^/mnt/[a-z](?:/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsMountedRepoPath();

    private bool ShouldPollGitHead(string repoDir) => IsWslEnvironment() && WindowsMountedRepoPath().IsMatch(repoDir);

    /// <summary>Current git branch, null if not in repo, "detached" if detached HEAD.</summary>
    public string? GetGitBranch()
    {
        lock (gate)
        {
            if (!branchResolved)
            {
                cachedBranch = ResolveGitBranchSync();
                branchResolved = true;
            }
            return cachedBranch;
        }
    }

    /// <summary>Extension status texts set via ctx.ui.setStatus().</summary>
    public IReadOnlyDictionary<string, string> GetExtensionStatuses()
    {
        lock (gate) return new Dictionary<string, string>(extensionStatuses, StringComparer.Ordinal);
    }

    /// <summary>Subscribe to git branch changes. Returns unsubscribe function.</summary>
    public Action OnBranchChange(Action callback)
    {
        lock (gate) if (!branchChangeCallbacks.Contains(callback)) branchChangeCallbacks.Add(callback);
        return () => { lock (gate) branchChangeCallbacks.Remove(callback); };
    }

    /// <summary>Internal: set extension status.</summary>
    public void SetExtensionStatus(string key, string? text)
    {
        lock (gate)
        {
            if (text is null) extensionStatuses.Remove(key);
            else extensionStatuses[key] = text;
        }
    }

    /// <summary>Internal: clear extension statuses.</summary>
    public void ClearExtensionStatuses() { lock (gate) extensionStatuses.Clear(); }

    /// <summary>Number of unique providers with available models (for footer display).</summary>
    public int GetAvailableProviderCount() { lock (gate) return availableProviderCount; }

    /// <summary>Internal: update available provider count.</summary>
    public void SetAvailableProviderCount(int count) { lock (gate) availableProviderCount = count; }

    public void SetCwd(string cwd)
    {
        lock (gate)
        {
            if (this.cwd == cwd) return;

            this.cwd = cwd;
            refreshTimer?.Dispose();
            refreshTimer = null;
            ClearGitWatchers();
            branchResolved = false;
            cachedBranch = null;
            gitPaths = FindGitPaths(cwd, host);
            SetupGitWatcher();
        }
        NotifyBranchChange();
    }

    /// <summary>Internal: cleanup.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            refreshTimer?.Dispose();
            refreshTimer = null;
            ClearGitWatchers();
            branchChangeCallbacks.Clear();
        }
    }

    private void NotifyBranchChange()
    {
        Action[] callbacks;
        lock (gate) callbacks = [.. branchChangeCallbacks];
        foreach (var cb in callbacks) cb();
    }

    private void ScheduleRefresh()
    {
        lock (gate)
        {
            if (disposed || refreshTimer is not null) return;
            if (refreshInFlight)
            {
                refreshPending = true;
                return;
            }
            IDisposable? timer = null;
            timer = host.SetTimeout(() =>
            {
                lock (gate) if (ReferenceEquals(refreshTimer, timer)) refreshTimer = null;
                _ = RefreshGitBranchAsync();
            }, WatchDebounceMs);
            refreshTimer = timer;
        }
    }

    private async Task RefreshGitBranchAsync()
    {
        lock (gate)
        {
            if (disposed) return;
            if (refreshInFlight)
            {
                refreshPending = true;
                return;
            }
            refreshInFlight = true;
        }

        var notify = false;
        try
        {
            var nextBranch = await ResolveGitBranchAsync().ConfigureAwait(false);
            lock (gate)
            {
                if (disposed) return;
                if (branchResolved && cachedBranch != nextBranch)
                {
                    cachedBranch = nextBranch;
                    notify = true;
                }
                else
                {
                    cachedBranch = nextBranch;
                    branchResolved = true;
                }
            }
        }
        finally
        {
            bool reschedule;
            lock (gate)
            {
                refreshInFlight = false;
                reschedule = refreshPending && !disposed;
                if (reschedule) refreshPending = false;
            }
            if (reschedule) ScheduleRefresh();
        }
        if (notify) NotifyBranchChange();
    }

    private string? ResolveGitBranchSync()
    {
        try
        {
            if (gitPaths is null) return null;
            var content = TextUtils.JsTrim(host.ReadFile(gitPaths.HeadPath));
            if (content.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
            {
                var branch = content[16..];
                return branch == ".invalid" ? ResolveBranchWithGitSync(gitPaths.RepoDir) ?? "detached" : branch;
            }
            return "detached";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<string?> ResolveGitBranchAsync()
    {
        GitPaths? paths;
        lock (gate) paths = gitPaths;
        try
        {
            if (paths is null) return null;
            var content = TextUtils.JsTrim(host.ReadFile(paths.HeadPath));
            if (content.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
            {
                var branch = content[16..];
                return branch == ".invalid" ? await ResolveBranchWithGitAsync(paths.RepoDir).ConfigureAwait(false) ?? "detached" : branch;
            }
            return "detached";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private void ClearGitWatchers()
    {
        lock (gate)
        {
            headWatcher?.Dispose();
            headWatcher = null;
            if (headWatchFilePath is not null && headWatchFilePoller is not null)
            {
                headWatchFilePoller.Dispose();
                headWatchFilePath = null;
                headWatchFilePoller = null;
            }
            reftableWatcher?.Dispose();
            reftableWatcher = null;
            reftableTablesListWatcher?.Dispose();
            reftableTablesListWatcher = null;
            if (reftableTablesListPath is not null)
            {
                reftableTablesListPoller?.Dispose();
                reftableTablesListPoller = null;
                reftableTablesListPath = null;
            }
            gitWatcherRetryTimer?.Dispose();
            gitWatcherRetryTimer = null;
        }
    }

    private void ScheduleGitWatcherRetry()
    {
        lock (gate)
        {
            if (disposed || gitWatcherRetryTimer is not null) return;
            IDisposable? timer = null;
            timer = host.SetTimeout(() =>
            {
                lock (gate) if (ReferenceEquals(gitWatcherRetryTimer, timer)) gitWatcherRetryTimer = null;
                SetupGitWatcher();
            }, FsWatchRetryDelayMs);
            gitWatcherRetryTimer = timer;
        }
    }

    private void HandleGitWatcherError()
    {
        ClearGitWatchers();
        ScheduleGitWatcherRetry();
    }

    private static bool StatChanged(FooterFileStat current, FooterFileStat previous) =>
        current.MtimeMs != previous.MtimeMs || current.CtimeMs != previous.CtimeMs || current.Size != previous.Size;

    private void SetupGitWatcher()
    {
        lock (gate)
        {
            ClearGitWatchers();
            if (gitPaths is null) return;
            var paths = gitPaths;

            var pollGitHead = ShouldPollGitHead(paths.RepoDir);

            // Watch the directory containing HEAD, not HEAD itself.
            // Git uses atomic writes (write temp, rename over HEAD), which changes the inode.
            // fs.watch on a file stops working after the inode changes.
            headWatcher = host.Watch(Path.GetDirectoryName(paths.HeadPath) ?? paths.HeadPath, (_, filename) =>
            {
                if (string.IsNullOrEmpty(filename) || filename == "HEAD") ScheduleRefresh();
            }, HandleGitWatcherError);
            if (pollGitHead)
            {
                headWatchFilePath = paths.HeadPath;
                headWatchFilePoller = host.WatchFile(headWatchFilePath, 1000, (current, previous) =>
                {
                    if (StatChanged(current, previous)) ScheduleRefresh();
                });
            }
            if (headWatcher is null && !pollGitHead) return;

            // In reftable repos, branch switches update files in the reftable directory
            // instead of HEAD. Watch it separately so the footer picks up those changes.
            var reftableDir = Path.Join(paths.CommonGitDir, "reftable");
            if (host.Exists(reftableDir))
            {
                reftableWatcher = host.Watch(reftableDir, (_, _) => ScheduleRefresh(), HandleGitWatcherError);
                if (reftableWatcher is null) return;

                var tablesListPath = Path.Join(reftableDir, "tables.list");
                if (host.Exists(tablesListPath))
                {
                    reftableTablesListPath = tablesListPath;
                    reftableTablesListWatcher = host.Watch(tablesListPath, (_, _) => ScheduleRefresh(), HandleGitWatcherError);
                    if (reftableTablesListWatcher is null) return;
                    reftableTablesListPoller = host.WatchFile(tablesListPath, 250, (current, previous) =>
                    {
                        if (StatChanged(current, previous)) ScheduleRefresh();
                    });
                }
            }
        }
    }
}
