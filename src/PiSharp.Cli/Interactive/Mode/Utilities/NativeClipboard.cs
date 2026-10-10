// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): tui/src/native-platform.ts (NativeClipboard, getNativeClipboard).
// DEVIATION: upstream loads prebuilt N-API helpers (AppKit on macOS, user32 on Windows, an X11 worker on Linux). PiSharp ships no
// native addon and adds no packages, so the macOS and Windows helpers are replaced by the platform commands below (pbpaste,
// osascript/JXA, PowerShell Get-Clipboard/Set-Clipboard and the System.Windows.Forms clipboard inside powershell.exe). The Linux
// X11 helper is ported over the same libxcb.so.1 through P/Invoke (X11Clipboard), read-only as upstream.
// The process runner, environment, temp directory and stdout writer are injectable through ClipboardEnvironment.
using System.Text;
using PiSharp.Tools.Images;

namespace PiSharp.Cli.Interactive.Mode.Utilities;

/// <summary>The source <c>NativeClipboard</c>. Text: null means no text (or unavailable, which every caller treats alike).
/// Image: <c>Available = false</c> is the source's undefined (unavailable), <c>Bytes = null</c> its null (no image); transfer
/// failures throw. File paths and text writes are optional, as in the source.</summary>
internal interface INativeClipboard
{
    Task<string?> GetTextAsync();
    Task<(bool Available, byte[]? Bytes)> GetImageAsync();
    bool CanGetFilePaths => false;
    /// <summary>POSIX paths of file URLs on the clipboard; null means none.</summary>
    Task<IReadOnlyList<string>?> GetFilePathsAsync() => Task.FromResult<IReadOnlyList<string>?>(null);
    bool CanSetText => false;
    Task SetTextAsync(string text) => throw new NotSupportedException("The native clipboard is read-only.");
}

/// <summary>Everything the clipboard utilities read from the process: platform (<c>process.platform</c> names), environment,
/// command runner, native clipboard, stdout (OSC 52), <c>/proc/version</c>, temp directory and the PNG converter (Photon).</summary>
internal sealed class ClipboardEnvironment
{
    public static string CurrentPlatform =>
        OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsAndroid() ? "android" : "linux";

    public string Platform { get; init; } = CurrentPlatform;
    public Func<string, string?> Env { get; init; } = Environment.GetEnvironmentVariable;
    public ClipboardCommandRunner Run { get; init; } = ClipboardCommand.RunClipboardCommand;
    /// <summary>Replaces <c>getNativeClipboard()</c> when set (a factory returning null means no helper).</summary>
    public Func<INativeClipboard?>? NativeClipboardFactory { get; init; }
    public Action<string> WriteOutput { get; init; } = text => { Console.Out.Write(text); Console.Out.Flush(); };
    public Func<string?> ReadProcVersion { get; init; } = Wsl.ReadProcVersion;
    public string TempDirectory { get; init; } = Path.GetTempPath();
    /// <summary>The Photon replacement used to convert unsupported clipboard images to PNG.</summary>
    public IImageCodec ImageCodec { get; init; } = PiSharp.Tools.Skia.SkiaImageCodec.Instance;

    public static ClipboardEnvironment Default { get; } = new();

    internal bool HasEnv(string name) => !string.IsNullOrEmpty(Env(name));

    /// <summary>Source <c>getNativeClipboard</c>: the macOS/Windows helper, or the X11 helper on Linux when DISPLAY is set.</summary>
    public INativeClipboard? GetNativeClipboard() => NativeClipboardFactory is { } factory ? factory() : NativeClipboard.Create(this);
}

internal static class NativeClipboard
{
    private const int TimeoutMs = 5000;

    /// <summary>Command-backed helpers for darwin and win32, the libxcb reader on Linux with DISPLAY; other platforms have none.</summary>
    public static INativeClipboard? Create(ClipboardEnvironment environment) => environment.Platform switch
    {
        "darwin" => new DarwinCommandClipboard(environment),
        "win32" => new WindowsCommandClipboard(environment),
        "linux" => X11Clipboard.Create(environment),
        _ => null,
    };

    internal static string PowerShellQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string NewTempFile(ClipboardEnvironment environment, string extension) =>
        Path.Combine(environment.TempDirectory, $"pi-clip-{Guid.NewGuid()}.{extension}");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static (bool Available, byte[]? Bytes) ReadImageFile(string output, string file)
    {
        var status = output.Trim();
        if (status == "empty") return (true, null);
        if (status != "ok") throw new InvalidOperationException("Native clipboard operation failed");
        var bytes = File.Exists(file) ? File.ReadAllBytes(file) : [];
        return (true, bytes.Length == 0 ? null : bytes);
    }

    /// <summary>macOS: pbpaste for text, AppleScript «class PNGf» for images, JXA NSPasteboard file URLs for copied files.
    /// Text writes are left to the pbcopy fallback the caller already runs.</summary>
    private sealed class DarwinCommandClipboard(ClipboardEnvironment environment) : INativeClipboard
    {
        public async Task<string?> GetTextAsync()
        {
            var bytes = await environment.Run("pbpaste", [], new(TimeoutMs: TimeoutMs)).ConfigureAwait(false);
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }

        public async Task<(bool Available, byte[]? Bytes)> GetImageAsync()
        {
            var file = NewTempFile(environment, "png");
            try
            {
                string[] script =
                [
                    "on run argv",
                    "try",
                    "set imageData to (the clipboard as «class PNGf»)",
                    "on error",
                    "return \"empty\"",
                    "end try",
                    "set outFile to open for access (POSIX file (item 1 of argv)) with write permission",
                    "set eof outFile to 0",
                    "write imageData to outFile",
                    "close access outFile",
                    "return \"ok\"",
                    "end run",
                ];
                var args = new List<string>();
                foreach (var line in script) { args.Add("-e"); args.Add(line); }
                args.Add(file);
                var output = await environment.Run("osascript", args, new(TimeoutMs: TimeoutMs)).ConfigureAwait(false);
                return output is null ? (false, null) : ReadImageFile(Encoding.UTF8.GetString(output), file);
            }
            finally { TryDelete(file); }
        }

        public bool CanGetFilePaths => true;

        public async Task<IReadOnlyList<string>?> GetFilePathsAsync()
        {
            const string script =
                "ObjC.import('AppKit');" +
                "var urls = $.NSPasteboard.generalPasteboard.readObjectsForClassesOptions($([$.NSURL]), $({NSPasteboardURLReadingFileURLsOnlyKey: true}));" +
                "var out = [];" +
                "if (urls) { for (var i = 0; i < urls.count; i++) { var p = urls.objectAtIndex(i).path; if (p) out.push(p.js); } }" +
                "out.join('\\n');";
            var output = await environment.Run("osascript", ["-l", "JavaScript", "-e", script], new(TimeoutMs: TimeoutMs)).ConfigureAwait(false);
            if (output is null) return null;
            var paths = Encoding.UTF8.GetString(output).TrimEnd('\n').Split('\n').Where(path => path.Length > 0).ToList();
            return paths.Count == 0 ? null : paths;
        }
    }

    /// <summary>Windows: PowerShell Get-Clipboard (UTF-8 output), the PNG or bitmap clipboard formats via System.Windows.Forms
    /// inside powershell.exe, and Set-Clipboard from a UTF-8 file (console code pages would mangle piped non-ASCII text).</summary>
    private sealed class WindowsCommandClipboard(ClipboardEnvironment environment) : INativeClipboard
    {
        public async Task<string?> GetTextAsync()
        {
            const string script =
                "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false; " +
                "$text = Get-Clipboard -Raw; if ($null -ne $text) { [Console]::Out.Write($text) }";
            var bytes = await environment.Run("powershell.exe", ["-NoProfile", "-Command", script], new(TimeoutMs: TimeoutMs)).ConfigureAwait(false);
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }

        public async Task<(bool Available, byte[]? Bytes)> GetImageAsync()
        {
            var file = NewTempFile(environment, "png");
            try
            {
                var script = string.Join("; ",
                    "Add-Type -AssemblyName System.Windows.Forms",
                    "Add-Type -AssemblyName System.Drawing",
                    $"$path = {PowerShellQuote(file)}",
                    "$data = [System.Windows.Forms.Clipboard]::GetDataObject()",
                    "$png = if ($data -and $data.GetDataPresent('PNG')) { $data.GetData('PNG') } else { $null }",
                    "if ($png -is [System.IO.MemoryStream]) { [System.IO.File]::WriteAllBytes($path, $png.ToArray()); Write-Output 'ok' } " +
                    "else { $img = [System.Windows.Forms.Clipboard]::GetImage(); " +
                    "if ($img) { $img.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); Write-Output 'ok' } else { Write-Output 'empty' } }");
                var output = await environment.Run("powershell.exe", ["-NoProfile", "-STA", "-Command", script], new(TimeoutMs: TimeoutMs)).ConfigureAwait(false);
                return output is null ? (false, null) : ReadImageFile(Encoding.UTF8.GetString(output), file);
            }
            finally { TryDelete(file); }
        }

        public bool CanSetText => true;

        public async Task SetTextAsync(string text)
        {
            var file = NewTempFile(environment, "txt");
            try
            {
                await File.WriteAllTextAsync(file, text, new UTF8Encoding(false)).ConfigureAwait(false);
                var script = $"Set-Clipboard -Value ([System.IO.File]::ReadAllText({PowerShellQuote(file)}, [System.Text.Encoding]::UTF8))";
                var result = await environment.Run("powershell.exe", ["-NoProfile", "-Command", script], new(TimeoutMs: TimeoutMs)).ConfigureAwait(false);
                if (result is null) throw new InvalidOperationException("Could not set clipboard text");
            }
            finally { TryDelete(file); }
        }
    }
}
