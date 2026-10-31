// Decision 0011 (run W2, WU9): the mockups' _FolderBrowser, server-side: the browser cannot open a native folder dialog for the host, so
// Settings › Tools & workspace lists the host's folders through GET /api/settings/folders. A listing is the folder's subfolders (hidden,
// system and dot folders left out), each marked when it is a git repository or holds AGENTS.md, and the context files of the folder itself
// (the candidates PiSharp's CLI reads: AGENTS.override.md, AGENTS.md, CLAUDE.md, and .pi/settings.json). Without a path it lists the
// drives (Windows) or the root. The app binds loopback only and runs as the user, so this shows the user nothing the user cannot open.
// A network or device path (\\server\share, \\?\, \\.\) is refused before anything touches it (W4 retest): listing one reaches the host it
// names over SMB or WebDAV and offers it the user's Windows credentials, and another website can send this GET (SettingsEndpoints refuses
// a request the browser marks as cross-site too). A network folder can still be typed as the working directory.
// The drive list waits at most DriveWait for the drives to say whether they are ready (after the W5 live test): a disconnected network drive
// takes seconds to say it is not, and asking each drive in turn held This PC until every one had. Fixed disks answer at once and are asked
// inline; the others (network, removable, optical) are asked all at once, and one that has not answered by then is left out, as one that is
// not ready is. Its folders can still be typed as the working directory.
using PiSharp.WebAgent.AgentCompare.Workspaces;

namespace PiSharp.WebAgent.Settings;

/// <summary>A subfolder in a <see cref="FolderListing"/>.</summary>
/// <param name="Name">Its name (a drive's letter and colon at the top level).</param>
/// <param name="Path">Its full path.</param>
/// <param name="Git">Whether it is a git repository (has <c>.git</c>).</param>
/// <param name="Agents">Whether it holds an AGENTS.md (or CLAUDE.md) context file.</param>
public sealed record FolderEntry(string Name, string Path, bool Git, bool Agents);

/// <summary>A step of a <see cref="FolderListing"/>'s breadcrumb.</summary>
/// <param name="Name">The text: <c>This PC</c>, <c>E:</c>, <c>AI</c>.</param>
/// <param name="Path">The folder it opens, or null for the top level.</param>
public sealed record FolderCrumb(string Name, string? Path);

/// <summary>One folder's subfolders.</summary>
/// <param name="Path">The folder, or null for the top level (drives or root).</param>
/// <param name="Parent">The folder above it, or null at the top.</param>
/// <param name="Crumbs">The breadcrumb, top level first.</param>
/// <param name="Folders">Its subfolders, by name; at most <see cref="FolderBrowser.MaximumFolders"/>.</param>
/// <param name="ContextFiles">The context files in the folder itself, such as <c>AGENTS.md</c> and <c>.pi/settings.json</c>.</param>
/// <param name="Truncated">Whether more subfolders exist than are listed.</param>
public sealed record FolderListing(string? Path, string? Parent, IReadOnlyList<FolderCrumb> Crumbs, IReadOnlyList<FolderEntry> Folders,
    IReadOnlyList<string> ContextFiles, bool Truncated);

/// <summary>Lists the host's folders for the working-directory picker.</summary>
public static class FolderBrowser
{
    /// <summary>The most subfolders one listing holds.</summary>
    public const int MaximumFolders = 500;

    /// <summary>How long the drive list waits for drives other than fixed disks to say whether they are ready.</summary>
    public static readonly TimeSpan DriveWait = TimeSpan.FromSeconds(2);

    /// <summary>The top level's name in the breadcrumb.</summary>
    public const string TopName = "This PC";

    private static readonly string[] AgentFiles = ["AGENTS.override.md", "AGENTS.md", "CLAUDE.md"];

    /// <summary>Why a network or device path is not listed.</summary>
    public const string NetworkPathMessage =
        @"Only folders on this computer are listed: a network path (\\server\share) or a device path (\\?\, \\.\) isn't opened here. Type it as the working directory instead.";

    /// <summary>The subfolders of <paramref name="path"/>, or the drives (the root outside Windows) when it is blank.</summary>
    /// <exception cref="ArgumentException">The path is not a full path, is a network or device path, or the folder does not exist.</exception>
    /// <exception cref="InvalidOperationException">The folder cannot be read (access denied, a device error).</exception>
    public static FolderListing List(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (!OperatingSystem.IsWindows()) return List("/");
            var ready = ReadyDrives([.. DriveInfo.GetDrives().Select(drive => (drive.Name, drive.DriveType == DriveType.Fixed, (Func<bool>)(() => Ready(drive))))], DriveWait);
            var drives = ready.Select(name => new FolderEntry(name.TrimEnd('\\', '/'), name, false, false)).ToList();
            return new FolderListing(null, null, [new FolderCrumb(TopName, null)], drives, [], false);
        }
        // Before anything touches the path, and again once it is resolved.
        if (WorkspaceManager.IsNetworkOrDevicePath(path)) throw new ArgumentException(NetworkPathMessage);
        var folder = Full(path);
        if (WorkspaceManager.IsNetworkOrDevicePath(folder)) throw new ArgumentException(NetworkPathMessage);
        if (!Directory.Exists(folder)) throw new ArgumentException($"There is no folder {folder}.");
        try
        {
            var entries = new List<FolderEntry>();
            var truncated = false;
            foreach (var child in new DirectoryInfo(folder).EnumerateDirectories().OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (child.Name.StartsWith('.') || (child.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if (entries.Count == MaximumFolders)
                {
                    truncated = true;
                    break;
                }
                entries.Add(new FolderEntry(child.Name, child.FullName, IsGit(child.FullName), HasAgents(child.FullName)));
            }
            var parent = Directory.GetParent(folder)?.FullName;
            return new FolderListing(folder, parent ?? (OperatingSystem.IsWindows() ? "" : null), Crumbs(folder), entries, ContextFiles(folder), truncated);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            throw new InvalidOperationException($"{folder} can't be opened: {error.GetType().Name.Replace("Exception", "", StringComparison.Ordinal)}.", error);
        }
    }

    /// <summary>The context files in <paramref name="directory"/>: the first of AGENTS.override.md and AGENTS.md, CLAUDE.md, and
    /// .pi/settings.json, by the names they have. Empty when the folder does not exist or cannot be read.</summary>
    public static IReadOnlyList<string> ContextFiles(string? directory)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) return found;
        try
        {
            if (!Directory.Exists(directory)) return found;
            if (AgentFiles.Take(2).FirstOrDefault(name => File.Exists(Path.Join(directory, name))) is { } agents) found.Add(agents);
            if (File.Exists(Path.Join(directory, "CLAUDE.md"))) found.Add("CLAUDE.md");
            if (File.Exists(Path.Join(directory, ".pi", "settings.json"))) found.Add(".pi/settings.json");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException) { /* Nothing to show. */ }
        return found;
    }

    /// <summary>Whether <paramref name="directory"/> is an existing folder given as a full path.</summary>
    public static bool Exists(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory.Trim())) return false;
        try { return Directory.Exists(directory.Trim()); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return false; }
    }

    private static string Full(string path)
    {
        var trimmed = path.Trim();
        if (!Path.IsPathFullyQualified(trimmed)) throw new ArgumentException("Enter a full path, such as " + ToolsSettingsDefault() + ".");
        var full = Path.GetFullPath(trimmed);
        // Keep a drive root's separator (C:\), drop any other trailing one.
        return Path.GetPathRoot(full) == full ? full : Path.TrimEndingDirectorySeparator(full);
    }

    private static string ToolsSettingsDefault() => Contracts.ToolsSettings.DefaultWorkingDirectory;

    private static List<FolderCrumb> Crumbs(string folder)
    {
        var crumbs = new List<FolderCrumb>();
        for (var current = new DirectoryInfo(folder); current is not null; current = current.Parent)
        {
            var name = current.Parent is null ? current.FullName.TrimEnd('\\', '/') : current.Name;
            crumbs.Insert(0, new FolderCrumb(name.Length == 0 ? "/" : name, current.FullName));
        }
        if (OperatingSystem.IsWindows()) crumbs.Insert(0, new FolderCrumb(TopName, null));
        return crumbs;
    }

    /// <summary>The names of <paramref name="drives"/> that say they are ready, in their order: a fixed disk is asked inline, the others all at
    /// once, and one of those that has not answered within <paramref name="wait"/> is left out.</summary>
    internal static IReadOnlyList<string> ReadyDrives(IReadOnlyList<(string Name, bool Fixed, Func<bool> Ready)> drives, TimeSpan wait)
    {
        var checks = drives.Select(drive => drive.Fixed ? Task.FromResult(drive.Ready()) : Task.Run(drive.Ready)).ToArray();
        try { Task.WaitAll(checks, wait); }
        catch (AggregateException) { /* A drive whose check failed is left out below. */ }
        return [.. drives.Where((_, index) => checks[index].IsCompletedSuccessfully && checks[index].Result).Select(drive => drive.Name)];
    }

    private static bool Ready(DriveInfo drive)
    {
        try { return drive.IsReady; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsGit(string directory) => Directory.Exists(Path.Join(directory, ".git")) || File.Exists(Path.Join(directory, ".git"));

    private static bool HasAgents(string directory) => AgentFiles.Any(name => File.Exists(Path.Join(directory, name)));
}
