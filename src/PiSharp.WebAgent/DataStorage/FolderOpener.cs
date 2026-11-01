// Decision 0011 (run W4, unit R9): "Open folder" on Settings › Data & storage. The page never names a path: it names a target (the data
// folder, the chats, the archive, the key ring, the workspaces, one workspace) and DataStorageService resolves it to a folder inside the data
// folder that exists. The opener then starts the platform's file manager with that one folder as an argument list (explorer.exe, open,
// xdg-open), never through a shell and never waiting for it. Tests register their own IFolderOpener first.
using System.Diagnostics;

namespace PiSharp.WebAgent.DataStorage;

/// <summary>Shows a folder in the file manager of the machine that runs the app.</summary>
public interface IFolderOpener
{
    /// <summary>Opens <paramref name="folder"/> (an existing, absolute folder).</summary>
    /// <exception cref="InvalidOperationException">The file manager could not be started.</exception>
    void Open(string folder);

    /// <summary>What the file manager is called here ("File Explorer", "Finder", "the file manager").</summary>
    string Name { get; }
}

/// <summary>Opens folders with explorer.exe (Windows), open (macOS) or xdg-open (elsewhere).</summary>
public sealed class SystemFolderOpener : IFolderOpener
{
    /// <inheritdoc/>
    public string Name => OperatingSystem.IsWindows() ? "File Explorer" : OperatingSystem.IsMacOS() ? "Finder" : "the file manager";

    /// <inheritdoc/>
    public void Open(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        // explorer.exe reads a trailing backslash before the closing quote as an escaped quote, so the folder goes without one.
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add(path);
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException($"{Name} could not be started.");
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new InvalidOperationException($"{Name} could not be started on this machine ({error.Message}).", error);
        }
    }
}
