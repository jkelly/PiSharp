// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/env/node-watch.ts.
// Node's fs.watch is FileSystemWatcher here: per directory without subdirectories on Linux (as upstream installs one inotify watch per
// directory), one recursive watcher per target elsewhere. FileSystemWatcher cannot watch a single file, so a target that is a symbolic link
// to a file is watched through the directory of the file it resolves to, filtered to that name. Identities (device and inode) come from
// NativeFiles; where the platform reports none (a Linux libc without statx), a path's creation time stands in, which can make an identity
// change spuriously (allowed: calls may be spurious). Upstream's single-threaded timers become System.Threading timers and one flush task at
// a time; every callback runs from that task, so callbacks never overlap.
using System.Collections.Immutable;
using System.Security.Cryptography;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Env.Local;

/// <summary>How a local environment watches (upstream <c>NodeWatchOptions</c>).</summary>
public sealed record LocalWatchOptions
{
    /// <summary>Force a mode. By default <see cref="WatchMode.Polling"/> is chosen on Windows, where native watchers keep directories open
    /// and so block renaming their parents, and for file systems that do not report remote changes.</summary>
    public WatchMode? Mode { get; init; }

    /// <summary>Interval between snapshots in polling mode; default 2000 ms.</summary>
    public int? PollIntervalMs { get; init; }

    /// <summary>Most directories one watcher covers; default 10,000.</summary>
    public int? MaxDirectories { get; init; }
}

/// <summary>
/// Watches by snapshots (upstream <c>NodeFileWatcher</c>): native events only trigger a debounced rescan, and changes are the difference
/// between snapshots plus the event paths. A replaced file, a renamed or recreated ancestor, or a directory created with its contents
/// therefore never depends on which events an operating system sends. New directories get their watchers before they are scanned again, so
/// nothing written into them before the watcher existed is missed.
/// </summary>
internal sealed class LocalFileWatcher : IFileWatcher
{
    private const int DebounceMs = 50;
    /// <summary>On macOS a watcher returns before its FSEvents stream is live, and changes in between are never reported; a rescan this
    /// long after installing watchers catches them.</summary>
    private const int FsEventsSettleMs = 500;
    private const int DefaultPollMs = 2000;
    private const int DefaultMaxDirectories = 10_000;
    /// <summary>In polling mode, recently modified small files are also compared by content: a second write within the file system's
    /// timestamp granularity can keep size and modification time.</summary>
    private const long HashMaxBytes = 256 * 1024;
    private const double HashRecentMs = 5000;

    /// <summary>Linux <c>statfs</c> magic numbers of file systems that accept watches but do not report changes made elsewhere.</summary>
    private static readonly HashSet<uint> UnreliableFileSystems =
    [
        0x6969, // NFS
        0x517b, // SMB
        0xff534d42, // CIFS
        0xfe534d42, // SMB2
        0x65735546, // FUSE (sshfs, Android shared storage)
        0x01021997, // 9P (WSL2 Windows drives)
        0x0bd00bd0, // Lustre
        0x47504653, // GPFS
        0x00c36400, // Ceph
        0x5346414f, // OpenAFS
        0x6b414653, // kAFS
        0x5dca2df5, // sdcardfs
    ];

    private static readonly char Separator = Path.DirectorySeparatorChar;

    private sealed record ResolvedTarget(string Path, bool Recursive, bool Hidden, ImmutableHashSet<string> Names);

    private enum EntryKind { File, Directory, Symlink, Other }

    /// <summary>What a snapshot remembers of one path. Directories and ancestors are compared by identity only.</summary>
    private readonly record struct Entry(EntryKind Kind, long Dev, ulong Ino, long Size, double MtimeMs, string? Hash);

    /// <summary>A native watcher and the identity of what it was installed on: a path replaced by another file needs a new one.</summary>
    private sealed record Installed(FileSystemWatcher Watcher, long Dev, ulong Ino);

    /// <summary>A scan: the snapshot, and targets that are symbolic links to files, whose files need their own watchers.</summary>
    private sealed record Scan(Dictionary<string, Entry> Snapshot, HashSet<string> LinkedFiles);

    private sealed class BudgetExceededException(string message) : Exception(message);

    private readonly ImmutableArray<ResolvedTarget> targets;
    private readonly Action<WatchChange> onChange;
    private readonly int pollIntervalMs;
    private readonly int maxDirectories;
    private readonly object gate = new();
    private readonly Dictionary<string, Installed> watchers = new(StringComparer.Ordinal);
    private readonly HashSet<string> events = new(StringComparer.Ordinal);
    private volatile bool closed;
    private WatchMode mode;
    private Dictionary<string, Entry> snapshot = new(StringComparer.Ordinal);
    private Timer? timer;
    private Timer? settleTimer;
    private Task? running;
    private bool dirty;

    private LocalFileWatcher(ImmutableArray<ResolvedTarget> targets, Action<WatchChange> onChange, WatchMode mode, LocalWatchOptions options)
    {
        this.targets = targets;
        this.onChange = onChange;
        this.mode = mode;
        pollIntervalMs = options.PollIntervalMs ?? DefaultPollMs;
        maxDirectories = options.MaxDirectories ?? DefaultMaxDirectories;
    }

    /// <summary>Establishes coverage: watchers first, then the snapshot later changes are compared with.</summary>
    public static async Task<LocalFileWatcher> OpenAsync(IReadOnlyList<WatchTarget> targets, Func<string, string> resolvePath,
        Action<WatchChange> onChange, LocalWatchOptions options)
    {
        var resolved = targets.Select(target => new ResolvedTarget(resolvePath(target.Path), target.Recursive, target.Exclude?.Hidden == true,
            (target.Exclude?.Names ?? []).ToImmutableHashSet(StringComparer.Ordinal))).ToImmutableArray();
        // Windows refuses to rename a directory while another directory below it is open, and a native watcher keeps every watched directory
        // open: watching would break renames of their parents, so Windows polls.
        var mode = options.Mode ?? (OperatingSystem.IsWindows() || AnyUnreliable(resolved.Select(target => target.Path)) ? WatchMode.Polling : WatchMode.Native);
        var watcher = new LocalFileWatcher(resolved, onChange, mode, options);
        try
        {
            await Task.Run(() => watcher.Sync(report: false)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            watcher.Stop();
            if (error is BudgetExceededException) throw new FileError(FileErrorCode.Invalid, error.Message);
            throw;
        }
        watcher.SchedulePoll();
        return watcher;
    }

    public WatchMode Mode { get { lock (gate) return mode; } }

    public async Task CloseAsync(DurableContext context)
    {
        Task? pending;
        lock (gate)
        {
            if (closed) return;
            pending = running;
        }
        Stop();
        if (pending is not null) await pending.ConfigureAwait(false);
    }

    private void Stop()
    {
        List<Installed> installed;
        lock (gate)
        {
            closed = true;
            timer?.Dispose();
            timer = null;
            settleTimer?.Dispose();
            settleTimer = null;
            installed = [.. watchers.Values];
            watchers.Clear();
        }
        foreach (var entry in installed) entry.Watcher.Dispose();
    }

    private void Deliver(WatchChange change)
    {
        if (closed) return;
        try { onChange(change); }
        catch (Exception) { } // A throwing callback must not stop watching.
    }

    private void SchedulePoll()
    {
        lock (gate)
        {
            if (closed || mode != WatchMode.Polling) return;
            timer?.Dispose();
            timer = new Timer(_ =>
            {
                lock (gate) { timer?.Dispose(); timer = null; }
                FlushAsync().ContinueWith(_ => SchedulePoll(), TaskScheduler.Default);
            }, null, pollIntervalMs, Timeout.Infinite);
        }
    }

    private void ScheduleFlush()
    {
        lock (gate)
        {
            if (closed || timer is not null) return;
            timer = new Timer(_ =>
            {
                lock (gate) { timer?.Dispose(); timer = null; }
                _ = FlushAsync();
            }, null, DebounceMs, Timeout.Infinite);
        }
    }

    private void ScheduleSettle()
    {
        lock (gate)
        {
            if (closed) return;
            settleTimer?.Dispose();
            settleTimer = new Timer(_ =>
            {
                lock (gate) { settleTimer?.Dispose(); settleTimer = null; }
                _ = FlushAsync();
            }, null, FsEventsSettleMs, Timeout.Infinite);
        }
    }

    private Task FlushAsync()
    {
        lock (gate)
        {
            if (running is not null)
            {
                dirty = true;
                return running;
            }
            if (closed) return Task.CompletedTask;
            return running = Task.Run(RunFlush);
        }
    }

    private void RunFlush()
    {
        try
        {
            while (true)
            {
                string[] pending;
                lock (gate)
                {
                    dirty = false;
                    pending = [.. events];
                    events.Clear();
                }
                var changed = Sync(report: true);
                foreach (var path in pending) changed.Add(path);
                if (changed.Count > 0) Deliver(new WatchPathsChange([.. changed.Order(StringComparer.Ordinal)]));
                lock (gate)
                {
                    if (!dirty || closed)
                    {
                        running = null;
                        return;
                    }
                }
            }
        }
        catch (Exception error)
        {
            var fileError = error as FileError ?? new FileError(IsDenied(error) ? FileErrorCode.PermissionDenied : FileErrorCode.Invalid, error.Message);
            Deliver(new WatchErrorChange(fileError));
            Stop();
            lock (gate) running = null;
        }
    }

    /// <summary>Rescans, reports differences, and installs watchers for new directories, rescanning until none are new.</summary>
    private HashSet<string> Sync(bool report)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        for (var round = 0; round < 10 && !closed; round++)
        {
            var scan = ScanAll();
            // Closed during the scan: installing watchers now would leak them.
            if (closed) break;
            var next = scan.Snapshot;
            if (report) foreach (var path in Diff(snapshot, next)) changed.Add(path);
            snapshot = next;
            if (Mode == WatchMode.Polling || !ReconcileWatchers(scan)) break;
            if (OperatingSystem.IsMacOS()) ScheduleSettle();
            // Something written into a new directory before its watcher existed shows up in the next round.
            report = true;
        }
        return changed;
    }

    private List<string> Diff(Dictionary<string, Entry> previous, Dictionary<string, Entry> next)
    {
        var changed = new List<string>();
        foreach (var (path, entry) in next)
            if (!previous.TryGetValue(path, out var before) || before != entry) changed.Add(Reported(path));
        foreach (var path in previous.Keys)
            if (!next.ContainsKey(path)) changed.Add(Reported(path));
        return changed;
    }

    /// <summary>An ancestor that changed identity moved every target below it; report those targets.</summary>
    private string Reported(string path)
    {
        if (targets.Any(target => IsWithin(path, target.Path))) return path;
        return targets.FirstOrDefault(target => IsWithin(target.Path, path))?.Path ?? path;
    }

    private Scan ScanAll()
    {
        var next = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var linkedFiles = new HashSet<string>(StringComparer.Ordinal);
        // Directories are counted once, but traversed once per target: overlapping targets differ in recursion and exclusions, and an
        // entry recorded for one target must still be descended into for another.
        var counted = new HashSet<string>(StringComparer.Ordinal);
        var traversed = new HashSet<string>(StringComparer.Ordinal);
        // Kinds of listed entries, not following links: a target that links to a directory is recorded by following it, but symbolic links
        // below a recursive target are not followed.
        var listed = new Dictionary<string, EntryKind>(StringComparer.Ordinal);
        var polling = Mode == WatchMode.Polling;

        void CountDirectory(string path)
        {
            counted.Add(path);
            if (counted.Count > maxDirectories) throw new BudgetExceededException($"Watched paths exceed {maxDirectories} directories");
        }

        void ScanDirectory(int index, ResolvedTarget target, string directory)
        {
            if (!traversed.Add(index + "\0" + directory)) return;
            string[] names;
            try { names = [.. Directory.EnumerateFileSystemEntries(directory).Select(entry => Path.GetFileName(entry))]; }
            catch (Exception error)
            {
                // The watched directory itself must be readable; below it, unreadable directories are skipped.
                if (directory == target.Path && IsDenied(error)) throw;
                if (error is IOException or UnauthorizedAccessException) return;
                throw;
            }
            foreach (var name in names)
            {
                if (Excluded(target, name)) continue;
                var path = (directory.EndsWith(Separator) ? directory : directory + Separator) + name;
                if (!listed.TryGetValue(path, out var kind))
                {
                    if (Stat(path, follow: false, polling) is not { } entry) continue;
                    kind = entry.Kind;
                    listed[path] = kind;
                    // A target's own entry (following links) wins over its listing by another target.
                    next.TryAdd(path, entry);
                }
                if (target.Recursive && kind == EntryKind.Directory)
                {
                    CountDirectory(path);
                    ScanDirectory(index, target, path);
                }
            }
        }

        for (var index = 0; index < targets.Length; index++)
        {
            var target = targets[index];
            foreach (var ancestor in AncestorsOf(target.Path))
            {
                if (next.ContainsKey(ancestor)) continue;
                // Identity only: an ancestor's own timestamps change with every unrelated sibling.
                if (Stat(ancestor, follow: false, hash: false) is { } entry) next[ancestor] = entry with { Size = 0, MtimeMs = 0 };
            }
            // The target itself may be a symbolic link to what is watched; follow it. A missing target is watched for its creation; one that
            // cannot be reached for lack of permission fails.
            Entry followed;
            try
            {
                if (StatOrThrow(target.Path, follow: true, polling) is not { } entry) continue;
                followed = entry;
            }
            catch (Exception error) when (IsDenied(error)) { throw; }
            catch (Exception) { continue; }
            next[target.Path] = followed;
            if (followed.Kind == EntryKind.File && Stat(target.Path, follow: false, hash: false) is { Kind: EntryKind.Symlink }) linkedFiles.Add(target.Path);
            if (followed.Kind == EntryKind.Directory)
            {
                CountDirectory(target.Path);
                ScanDirectory(index, target, target.Path);
            }
        }
        return new Scan(next, linkedFiles);
    }

    /// <summary>
    /// Watches every existing ancestor of each target, each target directory, each target that is a symbolic link to a file (changes to that
    /// file are not events of the link's directory), and on Linux each directory below a recursive target (elsewhere one recursive watcher
    /// per target). Returns whether a watcher was added.
    /// </summary>
    private bool ReconcileWatchers(Scan scan)
    {
        var (current, linkedFiles) = scan;
        var wanted = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var path in linkedFiles) wanted[path] = false;
        var perDirectory = OperatingSystem.IsLinux() || OperatingSystem.IsAndroid();
        foreach (var target in targets)
        {
            foreach (var ancestor in AncestorsOf(target.Path))
                if (current.TryGetValue(ancestor, out var entry) && entry.Kind == EntryKind.Directory) wanted.TryAdd(ancestor, false);
            if (!current.TryGetValue(target.Path, out var own) || own.Kind != EntryKind.Directory) continue;
            wanted[target.Path] = target.Recursive && !perDirectory;
            if (target.Recursive && perDirectory)
            {
                foreach (var (path, entry) in current)
                    if (entry.Kind == EntryKind.Directory && path != target.Path && IsWithin(path, target.Path)) wanted[path] = false;
            }
        }
        var removed = new List<FileSystemWatcher>();
        var added = false;
        var overflow = false;
        lock (gate)
        {
            if (closed) return false;
            foreach (var (path, installed) in watchers.ToList())
            {
                // Gone, or replaced: a watcher follows the directory it was installed on, not the path.
                if (!wanted.ContainsKey(path) || !current.TryGetValue(path, out var entry) || entry.Dev != installed.Dev || entry.Ino != installed.Ino)
                {
                    removed.Add(installed.Watcher);
                    watchers.Remove(path);
                }
            }
            foreach (var (path, recursive) in wanted)
            {
                if (watchers.ContainsKey(path) || !current.TryGetValue(path, out var entry)) continue;
                var linked = linkedFiles.Contains(path);
                FileSystemWatcher? watcher = null;
                try
                {
                    watcher = linked ? LinkedFileWatcher(path) : DirectoryWatcher(path, recursive);
                    if (watcher is null) continue;
                    watchers[path] = new Installed(watcher, entry.Dev, entry.Ino);
                    added = true;
                }
                catch (Exception error)
                {
                    watcher?.Dispose();
                    // Out of watches or unsupported: compare snapshots from now on, and say coverage was uncertain.
                    if (error is not (FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or ArgumentException))
                    {
                        removed.AddRange(watchers.Values.Select(installed => installed.Watcher));
                        watchers.Clear();
                        mode = WatchMode.Polling;
                        timer?.Dispose();
                        timer = null;
                        overflow = true;
                        added = false;
                        break;
                    }
                }
            }
        }
        foreach (var watcher in removed) watcher.Dispose();
        if (overflow)
        {
            Deliver(new WatchOverflowChange());
            SchedulePoll();
        }
        return added;
    }

    private FileSystemWatcher DirectoryWatcher(string path, bool recursive)
    {
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size |
                NotifyFilters.CreationTime | NotifyFilters.Attributes,
        };
        void OnEvent(string? name) => this.OnEvent(path, string.IsNullOrEmpty(name) ? null : name);
        watcher.Created += (_, change) => OnEvent(change.Name);
        watcher.Changed += (_, change) => OnEvent(change.Name);
        watcher.Deleted += (_, change) => OnEvent(change.Name);
        watcher.Renamed += (_, change) =>
        {
            OnEvent(change.OldName);
            OnEvent(change.Name);
        };
        watcher.Error += (_, _) => OnWatcherError(path, watcher);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    /// <summary>A watcher on the directory of the file a target links to, filtered to that file.</summary>
    private FileSystemWatcher? LinkedFileWatcher(string target)
    {
        var info = new FileInfo(target).ResolveLinkTarget(returnFinalTarget: true);
        if (info is null || Path.GetDirectoryName(info.FullName) is not { } directory) return null;
        var watcher = new FileSystemWatcher(directory, info.Name)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.Attributes,
        };
        watcher.Created += (_, _) => OnLinkedFileEvent(target);
        watcher.Changed += (_, _) => OnLinkedFileEvent(target);
        watcher.Deleted += (_, _) => OnLinkedFileEvent(target);
        watcher.Renamed += (_, _) => OnLinkedFileEvent(target);
        watcher.Error += (_, _) => OnWatcherError(target, watcher);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    /// <summary>A watched directory that disappears or fails (or overflows its buffer): rescan, which also replaces the watcher.</summary>
    private void OnWatcherError(string path, FileSystemWatcher watcher)
    {
        var stale = false;
        lock (gate)
        {
            if (watchers.TryGetValue(path, out var installed) && installed.Watcher == watcher)
            {
                watchers.Remove(path);
                stale = true;
            }
        }
        if (stale) watcher.Dispose();
        ScheduleFlush();
    }

    private void OnEvent(string directory, string? name)
    {
        if (closed) return;
        var path = name is null ? directory : (directory.EndsWith(Separator) ? directory : directory + Separator) + name;
        // Events about unrelated siblings of an ancestor, or about excluded entries, are ignored.
        var relevant = InScope(path);
        if (relevant) lock (gate) events.Add(Reported(path));
        if (relevant || name is null) ScheduleFlush();
    }

    /// <summary>The file a target links to changed: report the target.</summary>
    private void OnLinkedFileEvent(string target)
    {
        if (closed) return;
        lock (gate) events.Add(target);
        ScheduleFlush();
    }

    private bool InScope(string path)
    {
        foreach (var target in targets)
        {
            if (IsWithin(target.Path, path)) return true;
            if (!IsWithin(path, target.Path) || path == target.Path) continue;
            var components = Path.GetRelativePath(target.Path, path).Split(Separator);
            if (!target.Recursive && components.Length > 1) continue;
            if (components.Any(name => Excluded(target, name))) continue;
            return true;
        }
        return false;
    }

    /// <summary>Upstream <c>lstat</c>/<c>stat</c> plus <c>entryOf</c>: null when the path is missing or cannot be examined.</summary>
    private static Entry? Stat(string path, bool follow, bool hash)
    {
        try { return StatOrThrow(path, follow, hash); }
        catch (Exception) { return null; }
    }

    private static Entry? StatOrThrow(string path, bool follow, bool hash)
    {
        var attributes = File.GetAttributes(path);
        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null && follow)
        {
            info = info.ResolveLinkTarget(returnFinalTarget: true) ?? throw new FileNotFoundException("Dangling link", path);
            if (!info.Exists) throw new FileNotFoundException("Dangling link", path);
        }
        var native = NativeFiles.TryGet(path, follow);
        var kind = info.LinkTarget is not null && !follow ? EntryKind.Symlink
            : info is DirectoryInfo ? EntryKind.Directory
            : native?.Type == NativeFileType.Other ? EntryKind.Other : EntryKind.File;
        long dev;
        ulong ino;
        if (native is { } status) (dev, ino) = (status.Device, status.Inode);
        else if (NativeFiles.Available && !OperatingSystem.IsWindows()) return null; // Removed between the listing and this call.
        else (dev, ino) = (0, (ulong)info.CreationTimeUtc.Ticks);
        if (kind == EntryKind.Directory) return new Entry(kind, dev, ino, 0, 0, null);
        var size = kind == EntryKind.File ? ((FileInfo)info).Length : 0;
        var mtimeMs = (info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalMilliseconds;
        string? digest = null;
        if (hash && kind == EntryKind.File && size <= HashMaxBytes && (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds - mtimeMs < HashRecentMs)
        {
            try { digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(info.FullName))); }
            catch (Exception) { }
        }
        return new Entry(kind, dev, ino, size, mtimeMs, digest);
    }

    private static bool IsDenied(Exception error) => error is UnauthorizedAccessException;

    /// <summary>Ancestors of <paramref name="path"/>, nearest first, up to the root.</summary>
    private static List<string> AncestorsOf(string path)
    {
        var result = new List<string>();
        for (var current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current)) result.Add(current);
        return result;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="ancestor"/> or below it.</summary>
    private static bool IsWithin(string path, string ancestor) =>
        path == ancestor || path.StartsWith(ancestor.EndsWith(Separator) ? ancestor : ancestor + Separator, StringComparison.Ordinal);

    private static bool Excluded(ResolvedTarget target, string name) => target.Hidden && name.StartsWith('.') || target.Names.Contains(name);

    /// <summary>Whether any path, or its nearest existing ancestor, is on a file system that does not report remote changes.</summary>
    private static bool AnyUnreliable(IEnumerable<string> paths)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid()) return false;
        foreach (var path in paths)
        {
            foreach (var candidate in AncestorsOf(path).Prepend(path))
            {
                if (NativeFiles.FileSystemType(candidate) is not { } type) continue;
                if (UnreliableFileSystems.Contains(type)) return true;
                break;
            }
        }
        return false;
    }
}
