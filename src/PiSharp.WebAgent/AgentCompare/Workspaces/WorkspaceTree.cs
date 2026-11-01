// Decision 0011 (run W4, unit R4): file-system work for workspaces: walking a folder with the copy rules, copying it, measuring disk use,
// deleting a workspace without ever following a link out of it, and keeping every path inside its root. Symbolic links and junctions are
// never followed: a walk lists them as skipped, a copy leaves them out, a measure does not count their targets and a delete removes the
// link itself, so a link an agent made to another folder can't make a discard delete that folder.
namespace PiSharp.WebAgent.AgentCompare.Workspaces;

/// <summary>One file found by a walk.</summary>
/// <param name="Path">Relative to the walked root, '/' separators.</param>
/// <param name="Length">Its size in bytes.</param>
/// <param name="Modified">Its last write time (UTC); a copy keeps it, so equal sizes and times mean an unchanged file.</param>
internal sealed record TreeFile(string Path, long Length, DateTime Modified);

/// <summary>What a walk found.</summary>
internal sealed class TreeListing
{
    /// <summary>Every folder kept, parents first ('/' separators).</summary>
    public List<string> Folders { get; } = [];

    /// <summary>Every file kept.</summary>
    public List<TreeFile> Files { get; } = [];

    /// <summary>Symbolic links and junctions left out.</summary>
    public List<string> Links { get; } = [];

    /// <summary>Folders that could not be read (no access), left out.</summary>
    public List<string> Unreadable { get; } = [];

    /// <summary>The kept files' total size.</summary>
    public long Bytes { get; set; }

    /// <summary>False when the walk stopped at its file limit.</summary>
    public bool Complete { get; set; } = true;
}

/// <summary>The rules a walk applies.</summary>
/// <param name="Excludes">Always skipped (the default and extra excludes), whatever the gitignore files say.</param>
/// <param name="Outer">Gitignore sources above the walked root (its parents' .gitignore files and info/exclude).</param>
/// <param name="RulesRoot">Where the walked tree's own .gitignore files are read: the walked root itself, or for a working copy the
/// pristine base copy, so an agent's edit to a .gitignore file does not change what its diff shows.</param>
/// <param name="SkipFolders">Full paths never entered (the workspaces root when it lies inside the project).</param>
/// <param name="UseGitignore">Whether the walked tree's own .gitignore files are read (false: only <paramref name="Excludes"/> and
/// <paramref name="Outer"/> apply, and the copy rules pass no outer sources then either).</param>
internal sealed record TreeRules(IgnoreList Excludes, IgnoreStack Outer, string? RulesRoot, IReadOnlyList<string> SkipFolders, bool UseGitignore = true);

/// <summary>File-system helpers for workspaces.</summary>
internal static class WorkspaceTree
{
    private static readonly EnumerationOptions Entries = new()
    {
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>How paths compare on this system.</summary>
    public static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>A comparer for paths on this system.</summary>
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Walks <paramref name="root"/> with <paramref name="rules"/>, in name order.</summary>
    /// <param name="root">The folder to walk.</param>
    /// <param name="rules">What to skip.</param>
    /// <param name="fileLimit">Stop after this many files (the listing is then incomplete); null for no limit.</param>
    /// <param name="cancellationToken">Stops the walk.</param>
    public static TreeListing Walk(string root, TreeRules rules, int? fileLimit, CancellationToken cancellationToken)
    {
        var listing = new TreeListing();
        var rulesRoot = rules.RulesRoot ?? root;
        var pending = new Stack<(string Full, string Relative, IgnoreStack Ignore)>();
        IgnoreList Gitignore(string relative, string folder) =>
            rules.UseGitignore ? IgnoreList.Read(Path.Join(rulesRoot, relative, ".gitignore"), folder) : IgnoreList.Empty;
        pending.Push((root, "", rules.Outer.Push(Gitignore("", ""))));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (full, relative, ignore) = pending.Pop();
            var children = new List<(string Full, string Relative, IgnoreStack Ignore)>();
            List<FileSystemInfo> entries;
            try { entries = [.. new DirectoryInfo(full).EnumerateFileSystemInfos("*", Entries).OrderBy(entry => entry.Name, StringComparer.Ordinal)]; }
            catch (Exception error) when (relative.Length > 0 && error is UnauthorizedAccessException or IOException)
            {
                // A folder this user can't read (the walked root itself must be readable).
                listing.Unreadable.Add(relative.TrimEnd('/'));
                listing.Folders.Remove(relative.TrimEnd('/'));
                continue;
            }
            foreach (var entry in entries)
            {
                var path = relative + entry.Name;
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    listing.Links.Add(path);
                    continue;
                }
                var isFolder = entry is DirectoryInfo;
                if (isFolder && rules.SkipFolders.Any(skip => SamePath(skip, entry.FullName))) continue;
                if (rules.Excludes.Match(path, isFolder) == IgnoreMatch.Ignore || ignore.IsIgnored(path, isFolder)) continue;
                if (isFolder)
                {
                    listing.Folders.Add(path);
                    children.Add((entry.FullName, path + "/", ignore.Push(Gitignore(path, path + "/"))));
                }
                else
                {
                    var length = ((FileInfo)entry).Length;
                    listing.Files.Add(new TreeFile(path, length, entry.LastWriteTimeUtc));
                    listing.Bytes += length;
                    if (fileLimit is { } limit && listing.Files.Count >= limit)
                    {
                        listing.Complete = false;
                        return listing;
                    }
                }
            }
            // Depth first, in name order.
            for (var index = children.Count - 1; index >= 0; index--) pending.Push(children[index]);
        }
        return listing;
    }

    /// <summary>Copies what <paramref name="listing"/> holds from <paramref name="source"/> into the new folder
    /// <paramref name="destination"/>: independent files (no links, no shared storage), so a change in one copy never shows in another.</summary>
    /// <param name="source">The folder walked.</param>
    /// <param name="destination">The new folder.</param>
    /// <param name="listing">What to copy.</param>
    /// <param name="tolerateMissing">Skip a file that disappeared since the walk (copying a project that is being edited) instead of
    /// failing.</param>
    /// <param name="cancellationToken">Stops the copy.</param>
    /// <returns>What was copied.</returns>
    public static TreeListing Copy(string source, string destination, TreeListing listing, bool tolerateMissing, CancellationToken cancellationToken)
    {
        var copied = new TreeListing();
        copied.Links.AddRange(listing.Links);
        copied.Unreadable.AddRange(listing.Unreadable);
        Directory.CreateDirectory(destination);
        foreach (var folder in listing.Folders)
        {
            Directory.CreateDirectory(Join(destination, folder));
            copied.Folders.Add(folder);
        }
        foreach (var file in listing.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Copy(Join(source, file.Path), Join(destination, file.Path), overwrite: false);
            }
            catch (Exception error) when (tolerateMissing && error is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            copied.Files.Add(file);
            copied.Bytes += file.Length;
        }
        return copied;
    }

    /// <summary>The disk use of <paramref name="folder"/>'s files, without following links; 0 when it is missing.</summary>
    public static long Measure(string folder, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder) || IsLink(folder)) return 0;
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos("*", Entries).ToList(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (entry is DirectoryInfo) pending.Push(entry.FullName);
                else total += ((FileInfo)entry).Length;
            }
        }
        return total;
    }

    /// <summary>The files and links below <paramref name="folder"/>, as '/'-separated paths relative to it, without following links (a
    /// link is listed, not entered). Folders are not listed, so a folder holding only empty folders has none.</summary>
    public static IEnumerable<string> Contents(string folder)
    {
        var pending = new Stack<(string Full, string Relative)>();
        pending.Push((folder, ""));
        while (pending.Count > 0)
        {
            var (full, relative) = pending.Pop();
            foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos("*", Entries).ToList())
            {
                var path = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                if (entry is DirectoryInfo && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) pending.Push((entry.FullName, path));
                else yield return path;
            }
        }
    }

    /// <summary>Deletes <paramref name="path"/> and everything below it without following links (a link is removed, not its target), and
    /// clears read-only attributes on the way. With <paramref name="keepTopLevel"/>, that one entry of the folder is left (and the folder
    /// with it). Retries briefly when a file is in use.</summary>
    public static void Delete(string path, string? keepTopLevel = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                DeleteOnce(path, keepTopLevel);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < 4)
            {
                Thread.Sleep(150 * (attempt + 1));
            }
        }
    }

    private static void DeleteOnce(string path, string? keepTopLevel)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            if (File.Exists(path) || IsLink(path)) DeleteEntry(new FileInfo(path));
            return;
        }
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            if (keepTopLevel is null) DeleteLink(info);
            return;
        }
        foreach (var entry in info.EnumerateFileSystemInfos("*", Entries).ToList())
        {
            if (keepTopLevel is not null && string.Equals(entry.Name, keepTopLevel, PathComparison)) continue;
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) DeleteLink(entry);
            else if (entry is DirectoryInfo folder) DeleteOnce(folder.FullName, null);
            else DeleteEntry(entry);
        }
        if (keepTopLevel is not null) return;
        if (info.Attributes.HasFlag(FileAttributes.ReadOnly)) info.Attributes &= ~FileAttributes.ReadOnly;
        info.Delete();
    }

    private static void DeleteEntry(FileSystemInfo entry)
    {
        if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
        entry.Delete();
    }

    // A link to a folder is removed with RemoveDirectory on Windows and unlink elsewhere; neither touches the target.
    private static void DeleteLink(FileSystemInfo entry)
    {
        if (entry is DirectoryInfo && OperatingSystem.IsWindows()) Directory.Delete(entry.FullName, recursive: false);
        else File.Delete(entry.FullName);
    }

    /// <summary>Whether <paramref name="path"/> is a symbolic link or junction.</summary>
    public static bool IsLink(string path)
    {
        try { return new FileInfo(path).Attributes is var attributes && (int)attributes != -1 && attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary><paramref name="path"/> as a full path, after checking that it lies strictly inside <paramref name="root"/>.</summary>
    /// <exception cref="WorkspaceException"><see cref="WorkspaceException.Invalid"/> when it does not.</exception>
    public static string Inside(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison) || full.Length <= fullRoot.Length + 1)
            throw new WorkspaceException(WorkspaceException.Invalid, "A workspace path left its folder, so nothing was changed.");
        return full;
    }

    /// <summary>Checks that no folder between <paramref name="root"/> and <paramref name="full"/> (both included) is a link, so a write
    /// can't land outside the root through one.</summary>
    /// <exception cref="WorkspaceException"><see cref="WorkspaceException.Invalid"/> when one is.</exception>
    public static void NoLinksBetween(string root, string full)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var current = Path.GetDirectoryName(full); current is not null && current.Length >= fullRoot.Length; current = Path.GetDirectoryName(current))
        {
            if (IsLink(current))
                throw new WorkspaceException(WorkspaceException.Invalid, "A folder on the way to a changed file is a link, so nothing was written through it.");
            if (current.Length == fullRoot.Length) break;
        }
        if (IsLink(full)) throw new WorkspaceException(WorkspaceException.Invalid, "A changed file is a link in your project, so it was left alone.");
    }

    /// <summary>Whether two paths name the same place (full paths, this system's case rule).</summary>
    public static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), PathComparison);

    /// <summary>Whether <paramref name="inner"/> is <paramref name="outer"/> or lies inside it.</summary>
    public static bool IsWithin(string outer, string inner)
    {
        var fullOuter = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outer));
        var fullInner = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inner));
        return string.Equals(fullOuter, fullInner, PathComparison) || fullInner.StartsWith(fullOuter + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>Whether two files have the same bytes; a missing file equals only another missing file.</summary>
    public static bool SameContent(string left, string right)
    {
        var a = new FileInfo(left);
        var b = new FileInfo(right);
        if (!a.Exists || !b.Exists) return a.Exists == b.Exists;
        if (a.Length != b.Length) return false;
        using var first = a.OpenRead();
        using var second = b.OpenRead();
        var one = new byte[81920];
        var two = new byte[81920];
        while (true)
        {
            var read = first.ReadAtLeast(one, one.Length, throwOnEndOfStream: false);
            var other = second.ReadAtLeast(two, two.Length, throwOnEndOfStream: false);
            if (read != other || !one.AsSpan(0, read).SequenceEqual(two.AsSpan(0, other))) return false;
            if (read == 0) return true;
        }
    }

    /// <summary>A '/'-separated relative path as a path on this system below <paramref name="root"/>.</summary>
    public static string Join(string root, string relative) => Path.Join(root, relative.Replace('/', Path.DirectorySeparatorChar));
}
