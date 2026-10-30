// Decision 0011 (run W4, unit R9): the file-system helpers of Settings › Data & storage. Sizes are measured, never estimated: a folder walk
// that adds up file lengths and never follows a symbolic link or junction out of the folder (a link inside the data folder counts as nothing),
// and a SQLite file counted with its -wal and -shm files. A delete clears read-only attributes and removes a link, never its target. Paths
// are shown as the mockups show them: under %LOCALAPPDATA% on Windows, as they are elsewhere.
using System.Globalization;

namespace PiSharp.WebAgent.DataStorage;

/// <summary>Measuring, showing and deleting the app's folders.</summary>
public static class DataFolders
{
    private const FileAttributes Skipped = FileAttributes.ReparsePoint;

    private static readonly EnumerationOptions Entries = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    /// <summary>The path comparison of this platform.</summary>
    public static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The bytes of every file under <paramref name="folder"/> (0 when it does not exist or is a link). Links are not followed.</summary>
    public static long Measure(string folder, CancellationToken cancellationToken = default) => Measure(folder, [], cancellationToken);

    /// <summary>The bytes of every file under <paramref name="folder"/> but those under <paramref name="skip"/> (folders measured on their
    /// own). Links are not followed.</summary>
    public static long Measure(string folder, IEnumerable<string> skip, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skip);
        if (!Directory.Exists(folder) || IsLink(folder)) return 0;
        var skipped = skip.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<FileSystemInfo> entries;
            try { entries = [.. new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos("*", Entries)]; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if ((entry.Attributes & Skipped) != 0) continue;
                if (entry is DirectoryInfo) { if (!skipped.Contains(Path.TrimEndingDirectorySeparator(entry.FullName))) pending.Push(entry.FullName); }
                else
                {
                    try { total += ((FileInfo)entry).Length; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Gone or locked; counted next time. */ }
                }
            }
        }
        return total;
    }

    /// <summary>The size of a file (0 when it does not exist).</summary>
    public static long FileBytes(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>A SQLite file with its <c>-wal</c> and <c>-shm</c> files.</summary>
    public static long DatabaseBytes(string path) => FileBytes(path) + FileBytes(path + "-wal") + FileBytes(path + "-shm");

    /// <summary>Whether <paramref name="path"/> is a symbolic link or junction.</summary>
    public static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or below it (full paths, compared as this platform does).</summary>
    public static bool IsWithin(string root, string path)
    {
        var outer = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var inner = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return string.Equals(outer, inner, PathComparison) ||
            inner.StartsWith(outer + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>Deletes <paramref name="path"/> and everything below it: read-only attributes are cleared, a link is removed (never its
    /// target). Nothing happens when it does not exist.</summary>
    /// <exception cref="IOException">Something is in use.</exception>
    public static void Delete(string path)
    {
        if (IsLink(path))
        {
            Directory.Delete(path);
            return;
        }
        if (!Directory.Exists(path)) return;
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", Entries))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (entry is DirectoryInfo) Directory.Delete(entry.FullName);
                else File.Delete(entry.FullName);
            }
            else if (entry is DirectoryInfo) Delete(entry.FullName);
            else
            {
                if ((entry.Attributes & FileAttributes.ReadOnly) != 0) entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete();
            }
        }
        Directory.Delete(path);
    }

    /// <summary><paramref name="path"/> as the page shows it: under <c>%LOCALAPPDATA%</c> when it is there (Windows), with a trailing separator
    /// for a folder.</summary>
    public static string Display(string path, bool folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (OperatingSystem.IsWindows() && local.Length > 0)
        {
            var root = Path.TrimEndingDirectorySeparator(local);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) full = "%LOCALAPPDATA%";
            else if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) full = "%LOCALAPPDATA%" + full[root.Length..];
        }
        return folder ? full + Path.DirectorySeparatorChar : full;
    }

    /// <summary>The drive or volume of <paramref name="path"/> and its free space, when the platform can tell.</summary>
    public static (string Name, long? FreeBytes) Drive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return ("", null);
            var drive = new DriveInfo(root);
            var name = OperatingSystem.IsWindows() ? root.TrimEnd('\\', '/') : drive.Name;
            return (name, drive.IsReady ? drive.AvailableFreeSpace : null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ("", null);
        }
    }

    /// <summary>"368.4 MB", "212 MB", "18.2 MB", "512 KB", "300 bytes", "1 byte": KB, MB, GB and TB of 1,024, one decimal below 100.</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024) return bytes == 1 ? "1 byte" : bytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes";
        string[] units = ["KB", "MB", "GB", "TB"];
        var value = bytes / 1024d;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(value < 100 ? "0.#" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
