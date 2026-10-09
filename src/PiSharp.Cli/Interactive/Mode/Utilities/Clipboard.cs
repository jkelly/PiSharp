// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/utils/clipboard.ts.
// The native clipboard (getNativeClipboard) is the command-backed replacement in NativeClipboard.cs; see the deviation there.
using System.Text;

namespace PiSharp.Cli.Interactive.Mode.Utilities;

internal static class Clipboard
{
    private const int MaxOsc52EncodedLength = 100_000;

    private static bool IsRemoteSession(ClipboardEnvironment environment) =>
        environment.HasEnv("SSH_CONNECTION") || environment.HasEnv("SSH_CLIENT") || environment.HasEnv("MOSH_CONNECTION");

    private static bool EmitOsc52(ClipboardEnvironment environment, string text)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        if (encoded.Length > MaxOsc52EncodedLength) return false;
        environment.WriteOutput($"\u001b]52;c;{encoded}\u0007");
        return true;
    }

    /// <summary>WSL without WSLg has no Linux display, so the Windows clipboard is written through interop. PowerShell reads the
    /// text from a file because <c>clip.exe</c> and PowerShell stdin decode piped bytes with the console code page, which mangles
    /// non-ASCII UTF-8.</summary>
    private static async Task<bool> CopyViaWindowsClipboard(ClipboardEnvironment environment, string text)
    {
        var tmpFile = Path.Combine(environment.TempDirectory, $"pi-wsl-clip-{Guid.NewGuid()}.txt");
        try
        {
            await File.WriteAllTextAsync(tmpFile, text, new UTF8Encoding(false)).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(tmpFile, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (IOException) { }
            }
            var winPathBytes = await environment.Run("wslpath", ["-w", tmpFile], new(TimeoutMs: 1000)).ConfigureAwait(false);
            var winPath = winPathBytes is null ? null : PiSharp.Tui.Pi.TextUtils.JsTrim(Encoding.UTF8.GetString(winPathBytes));
            if (string.IsNullOrEmpty(winPath)) return false;
            var script = $"Set-Clipboard -Value ([System.IO.File]::ReadAllText('{winPath.Replace("'", "''", StringComparison.Ordinal)}', [System.Text.Encoding]::UTF8))";
            var result = await environment.Run("powershell.exe", ["-NoProfile", "-Command", script], new(TimeoutMs: 5000)).ConfigureAwait(false);
            return result is not null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(tmpFile); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Read plain text from the system clipboard.</summary>
    public static async Task<string?> ReadClipboardText(ClipboardEnvironment? environment = null)
    {
        environment ??= ClipboardEnvironment.Default;
        var commands = new List<(string Command, string[] Args)>();
        // Termux reports platform "android", not "linux" (#10391).
        if (environment.HasEnv("TERMUX_VERSION")) commands.Add(("termux-clipboard-get", []));
        if (environment.Platform == "linux")
        {
            if (environment.HasEnv("WAYLAND_DISPLAY")) commands.Add(("wl-paste", ["--no-newline", "--type", "text"]));
            if (environment.HasEnv("DISPLAY"))
            {
                commands.Add(("xclip", ["-selection", "clipboard", "-out"]));
                commands.Add(("xsel", ["--clipboard", "--output"]));
            }
        }
        foreach (var (command, args) in commands)
        {
            var bytes = await environment.Run(command, args, new(TimeoutMs: 5000)).ConfigureAwait(false);
            if (bytes is not null) return NullIfEmpty(Encoding.UTF8.GetString(bytes));
        }
        try
        {
            var native = environment.GetNativeClipboard();
            return native is null ? null : NullIfEmpty(await native.GetTextAsync().ConfigureAwait(false));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Read file paths, such as Finder file copies, from the native clipboard. Native read failures propagate.</summary>
    public static async Task<IReadOnlyList<string>?> ReadClipboardFilePaths(ClipboardEnvironment? environment = null)
    {
        environment ??= ClipboardEnvironment.Default;
        var native = environment.GetNativeClipboard();
        if (native is null || !native.CanGetFilePaths) return null;
        var paths = await native.GetFilePathsAsync().ConfigureAwait(false);
        return paths is { Count: > 0 } ? paths : null;
    }

    public static async Task CopyToClipboard(string text, ClipboardEnvironment? environment = null)
    {
        environment ??= ClipboardEnvironment.Default;
        var p = environment.Platform;
        var copied = false;
        // Direct writes precede OSC 52 so the terminal cannot race the native writer.
        // Linux tools retain clipboard selection ownership after this call returns.
        if (p != "linux")
        {
            try
            {
                var clipboard = environment.GetNativeClipboard();
                if (clipboard is { CanSetText: true })
                {
                    await clipboard.SetTextAsync(text).ConfigureAwait(false);
                    copied = true;
                }
            }
            catch (Exception)
            {
                // Try platform commands next.
            }
        }
        if (!copied)
        {
            var commands = new List<(string Command, string[] Args)>();
            if (p == "darwin") commands.Add(("pbcopy", []));
            else if (p == "win32") commands.Add(("clip", []));
            else
            {
                if (environment.HasEnv("TERMUX_VERSION")) commands.Add(("termux-clipboard-set", []));
                if (environment.HasEnv("WAYLAND_DISPLAY")) commands.Add(("wl-copy", []));
                if (environment.HasEnv("DISPLAY"))
                {
                    commands.Add(("xclip", ["-selection", "clipboard"]));
                    commands.Add(("xsel", ["--clipboard", "--input"]));
                }
            }
            foreach (var (command, args) in commands)
            {
                if (await environment.Run(command, args, new(Input: text, TimeoutMs: 5000)).ConfigureAwait(false) is not null)
                {
                    copied = true;
                    break;
                }
            }
        }
        var osc52Emitted = false;
        if (!copied && p == "linux" && Wsl.IsWSL(environment.Env, environment.ReadProcVersion))
        {
            // Windows Terminal supports OSC 52; prefer it over the slower PowerShell round trip.
            if (environment.HasEnv("WT_SESSION")) osc52Emitted = EmitOsc52(environment, text);
            copied = osc52Emitted || await CopyViaWindowsClipboard(environment, text).ConfigureAwait(false);
        }
        // OSC 52 cannot be verified, so a desktop session with a display reports the failure
        // instead (#9618). Without a display the terminal is the only clipboard route (containers,
        // WSL without WSLg), and remote sessions always emit it to reach the client clipboard.
        var headless = p == "linux" && !environment.HasEnv("DISPLAY") && !environment.HasEnv("WAYLAND_DISPLAY") && !environment.HasEnv("TERMUX_VERSION");
        var oversized = false;
        if (!osc52Emitted && (IsRemoteSession(environment) || (!copied && headless)))
        {
            if (EmitOsc52(environment, text)) copied = true;
            else oversized = true;
        }
        if (copied) return;
        if (oversized) throw new InvalidOperationException("Clipboard unavailable: text exceeds the OSC 52 size limit");
        if (environment.HasEnv("TERMUX_VERSION"))
            throw new InvalidOperationException("Clipboard unavailable: install the Termux:API app and `termux-api` package");
        if (p == "linux")
        {
            if (environment.HasEnv("WAYLAND_DISPLAY"))
                throw new InvalidOperationException("Clipboard unavailable: install `wl-clipboard` (`wl-copy`) or check Wayland access");
            if (environment.HasEnv("DISPLAY"))
                throw new InvalidOperationException("Clipboard unavailable: install `xclip` or `xsel`, or check X11 access");
        }
        throw new InvalidOperationException("Clipboard unavailable");
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
