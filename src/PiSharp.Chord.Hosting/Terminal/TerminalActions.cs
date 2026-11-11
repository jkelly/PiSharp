// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/tui/actions.ts.
// Native adapters retain explicit trusted-code and terminal ownership boundaries.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PiSharp.Chord.Hosting.Terminal;

public interface ITerminalPrompt
{
    int Columns { get; }
    Task<bool> AskAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken);
    void WriteTrusted(string data);
}

/// <summary>An already opened regular file. Its path identifies the actual open handle, and reads use that handle.</summary>
public interface ITerminalAttachment : IAsyncDisposable
{
    string Path { get; }
    long Length { get; }
    ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken);
}

/// <summary>Trusted action implementation; no lookup of an attachment occurs until the textual path is confirmed.</summary>
public sealed class TerminalActions
{
    public const int MaximumAttachmentBytes = 16 * 1024 * 1024;
    private readonly Func<string, CancellationToken, Task> _openUrl;
    private readonly Func<string, CancellationToken, Task<ITerminalAttachment>> _openFile;
    public TerminalActions(Func<string, CancellationToken, Task>? openUrl = null,
        Func<string, CancellationToken, Task<ITerminalAttachment>>? openFile = null)
    { _openUrl = openUrl ?? OpenUrlAsync; _openFile = openFile ?? OpenFileAsync; }

    public async Task<bool> ActAsync(TerminalRequest request, ITerminalPrompt terminal, CancellationToken cancellationToken = default)
    {
        if (request is TerminalRequest.Copy copy)
        {
            if (copy.Text.Any(c => c is <= '\x08' or >= '\x0b' and <= '\x1f' or >= '\x7f' and <= '\x9f' or >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069'))
                throw new IOException("copy: the text holds control characters");
            var lines = copy.Text.Split('\n');
            var rows = lines.SelectMany(line => Wrap(line, Math.Max(8, terminal.Columns - 4))).ToArray();
            var shown = rows.Take(5).Select(row => "  " + row).ToList();
            var hidden = rows.Skip(5).Sum(row => row.Length);
            if (hidden != 0) shown.Add($"  … and {hidden} more characters");
            shown.Add($"Copy these {copy.Text.Length} characters ({lines.Length} line{(lines.Length == 1 ? "" : "s")})? [y/N]");
            if (!await terminal.AskAsync(shown, cancellationToken).ConfigureAwait(false)) return false;
            terminal.WriteTrusted("\u001b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(copy.Text)) + "\a"); return true;
        }
        if (request is not TerminalRequest.Open open) throw new ArgumentException("An action must be copy or open.", nameof(request));
        if (!Uri.TryCreate(open.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            open.Url.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format))
            throw new IOException("open: only http and https URLs without user info open");
        if (!await terminal.AskAsync(["  " + uri.AbsoluteUri, $"Open {uri.Authority} in the browser? [y/N]"], cancellationToken).ConfigureAwait(false)) return false;
        await _openUrl(uri.AbsoluteUri, cancellationToken).ConfigureAwait(false); return true;
    }

    public async Task<TerminalAttachment?> AttachAsync(string path, ITerminalPrompt terminal, CancellationToken cancellationToken = default)
    {
        if (!Attachable(path, OperatingSystem.IsWindows())) throw CannotAttach();
        var home = path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length == 1 ? "" : path[2..]) : path;
        string named;
        try { named = System.IO.Path.GetFullPath(home); } catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { throw CannotAttach(); }
        if (!await terminal.AskAsync(FileLines(named, "Attach this file? [y/N]"), cancellationToken).ConfigureAwait(false)) return null;
        try
        {
            // The descriptor is held across the second prompt and read directly. Replacing a symlink or parent
            // directory after either prompt cannot change the file whose actual path is shown or read.
            await using var file = await _openFile(named, cancellationToken).ConfigureAwait(false);
            if (!Attachable(file.Path, OperatingSystem.IsWindows()) || file.Length is < 0 or > MaximumAttachmentBytes) throw CannotAttach();
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(named, file.Path, comparison) &&
                !await terminal.AskAsync(FileLines(file.Path, "It links here. Attach this file? [y/N]"), cancellationToken).ConfigureAwait(false)) return null;
            var bytes = new byte[(int)file.Length]; var read = 0;
            while (read < bytes.Length)
            {
                var count = await file.ReadAsync(bytes.AsMemory(read), read, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                if (count < 0 || count > bytes.Length - read) throw CannotAttach();
                read += count;
            }
            return new(System.IO.Path.GetFileName(file.Path), bytes.AsMemory(0, read));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw CannotAttach(); }
    }

    public static bool Attachable(string path, bool windows)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith(' ') || path.EndsWith(' ') || path.Contains("  ", StringComparison.Ordinal)) return false;
        foreach (var rune in path.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator || Rune.IsWhiteSpace(rune) && rune.Value != ' ') return false;
        }
        if (!windows) return true;
        if (path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/') return false;
        foreach (var part in path.Split(['/', '\\']))
        {
            var stem = part.Split('.')[0].TrimEnd(' ', ':').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && (char.IsDigit(stem[3]) || stem[3] is '¹' or '²' or '³')) return false;
            // Alternate data streams can address device aliases and do not denote the confirmed ordinary file.
            if (part.Contains(':') && !(part.Length == 2 && char.IsAsciiLetter(part[0]) && part[1] == ':')) return false;
        }
        return true;
    }

    public static IReadOnlyList<string> PromptLines(IEnumerable<string> lines, int columns) => lines.SelectMany(line => Wrap(line, Math.Max(10, columns - 2))).ToArray();
    public static IReadOnlyList<string> Wrap(string line, int width)
    {
        width = Math.Max(1, width); var rows = new List<string>(); var row = new StringBuilder(); var used = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            var cells = rune.Value is >= 32 and <= 126 ? 1 : 2;
            if (used + cells > width && row.Length > 0) { rows.Add(row.ToString()); row.Clear(); used = 0; }
            row.Append(rune.ToString()); used += cells;
        }
        rows.Add(row.ToString()); return rows;
    }
    private static string[] FileLines(string path, string question) => [$"File: {System.IO.Path.GetFileName(path)}", $"In:   {System.IO.Path.GetDirectoryName(path)}", question];
    private static IOException CannotAttach() => new("attach: that file cannot be attached");
    private static Task OpenUrlAsync(string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "rundll32" : OperatingSystem.IsMacOS() ? "open" : "xdg-open")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = System.IO.Path.GetPathRoot(Environment.CurrentDirectory)! };
        if (OperatingSystem.IsWindows()) start.ArgumentList.Add("url.dll,FileProtocolHandler");
        start.ArgumentList.Add(url);
        using var process = Process.Start(start); return Task.CompletedTask;
    }
    private static Task<ITerminalAttachment> OpenFileAsync(string path, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<ITerminalAttachment>(NativeAttachment.Open(path)); }

    private sealed class NativeAttachment(SafeFileHandle handle, string path, long length) : ITerminalAttachment
    {
        public string Path { get; } = path;
        public long Length { get; } = length;
        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken) => RandomAccess.ReadAsync(handle, buffer, offset, cancellationToken);
        public ValueTask DisposeAsync() { handle.Dispose(); return ValueTask.CompletedTask; }
        internal static NativeAttachment Open(string path)
        {
            SafeFileHandle handle;
            if (OperatingSystem.IsWindows())
                handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
                    throw new PlatformNotSupportedException("Safe Unix attachments require x64 or arm64 file identity layouts.");
                // Non-blocking acquisition prevents FIFOs/devices from waiting before their type is inspected.
                var fd = UnixOpen(path, OperatingSystem.IsMacOS() ? 0x0004 | 0x01000000 : 0x0800 | 0x080000);
                if (fd < 0) throw CannotAttach();
                handle = new SafeFileHandle(fd, ownsHandle: true);
            }
            else throw new PlatformNotSupportedException("Safe attachments are supported on Windows, Linux and macOS.");
            try
            {
                string actual;
                if (OperatingSystem.IsWindows())
                {
                    if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x10) != 0) throw CannotAttach();
                    var pathBuffer = new StringBuilder(32768); var count = GetFinalPathNameByHandle(handle, pathBuffer, pathBuffer.Capacity, 0);
                    if (count == 0 || count >= pathBuffer.Capacity) throw CannotAttach();
                    actual = pathBuffer.ToString();
                    if (actual.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) throw CannotAttach();
                    if (actual.StartsWith("\\\\?\\", StringComparison.Ordinal)) actual = actual[4..];
                }
                else
                {
                    var stat = Marshal.AllocHGlobal(256);
                    try
                    {
                        if (UnixFStat(handle.DangerousGetHandle().ToInt32(), stat) != 0) throw CannotAttach();
                        var mode = OperatingSystem.IsMacOS() ? (uint)(ushort)Marshal.ReadInt16(stat, 4) :
                            (uint)Marshal.ReadInt32(stat, RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 16 : 24);
                        if ((mode & 0xf000) != 0x8000) throw CannotAttach();
                    }
                    finally { Marshal.FreeHGlobal(stat); }
                    if (OperatingSystem.IsLinux())
                        actual = new FileInfo($"/proc/self/fd/{handle.DangerousGetHandle().ToInt32()}").ResolveLinkTarget(true)?.FullName ?? throw CannotAttach();
                    else
                    {
                        var buffer = new byte[4096];
                        if (UnixGetPath(handle.DangerousGetHandle().ToInt32(), 50, buffer) != 0) throw CannotAttach();
                        actual = Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0));
                    }
                }
                var length = RandomAccess.GetLength(handle);
                if (length > MaximumAttachmentBytes) throw CannotAttach();
                return new(handle, actual, length);
            }
            catch { handle.Dispose(); throw; }
        }
        [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int UnixOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int UnixFStat(int fd, nint stat);
        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int UnixGetPath(int fd, int command, [Out] byte[] path);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle handle);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int size, uint flags);
        [StructLayout(LayoutKind.Sequential)] private struct FileInformation
        { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint Volume, HighSize, LowSize, Links, HighIndex, LowIndex; }
    }
}
