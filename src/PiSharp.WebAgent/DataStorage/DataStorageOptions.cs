// Decision 0011 (run W4, unit R9): Settings › Data & storage's fixed settings. Opening a folder starts the file manager of the machine that
// runs the app (the app binds loopback only, so that is the user's own desktop); a headless host, or a test that drives the real app, turns
// it off with the configuration key DataStorage:OpenFolders=false (for example --DataStorage:OpenFolders=false on the command line), and the
// Open folder buttons then say so instead of starting anything. Deleted chats are kept in the device's archive for 30 days.
namespace PiSharp.WebAgent.DataStorage;

/// <summary>What Settings › Data &amp; storage may do on this machine.</summary>
public sealed class DataStorageOptions
{
    /// <summary>The configuration key of <see cref="OpenFolders"/>.</summary>
    public const string OpenFoldersKey = "DataStorage:OpenFolders";

    /// <summary>How long a deleted chat stays in the archive: 30 days.</summary>
    public const int ArchiveRetentionDays = 30;

    /// <summary>The largest settings file an import reads: 4 MB.</summary>
    public const int MaxImportBytes = 4 * 1024 * 1024;

    /// <summary>Whether Open folder starts the file manager (default true).</summary>
    public bool OpenFolders { get; init; } = true;

    /// <summary>How long a deleted chat stays in <c>device\archive\</c> before the cleanup removes it.</summary>
    public TimeSpan ArchiveRetention { get; init; } = TimeSpan.FromDays(ArchiveRetentionDays);
}
