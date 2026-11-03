// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): coding-agent/src/utils/clipboard-command.ts.
// A clipboard command is spawned without a shell. Its result is the captured standard output, or null when it could not start,
// exited non-zero, timed out or exceeded the output limit (the source's undefined). An empty array is a successful empty result.
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using PiSharp.Tools.Processes.Unix;

namespace PiSharp.Cli.Interactive.Mode.Utilities;

/// <summary>The source's <c>{ input?, timeoutMs?, maxBufferBytes? }</c>. Record equality lets tests compare recorded calls.</summary>
internal sealed record ClipboardCommandOptions(string? Input = null, int? TimeoutMs = null, long? MaxBufferBytes = null);

/// <summary><c>runClipboardCommand</c>: null means the command failed; an empty array is a successful result.</summary>
internal delegate Task<byte[]?> ClipboardCommandRunner(string command, IReadOnlyList<string> args, ClipboardCommandOptions? options);

internal static class ClipboardCommand
{
    private const int DefaultTimeoutMs = 3000;
    private const long DefaultMaxBufferBytes = 50L * 1024 * 1024;

    /// <summary><c>cwd: "/"</c>: the root, which Node resolves to the current drive's root on Windows. Clipboard tools ignore the cwd,
    /// and WSL cannot start Windows executables (powershell.exe) when the cwd's \\wsl.localhost\... path exceeds MAX_PATH (#11592).
    /// This also keeps daemons from pinning pi's cwd.</summary>
    internal static string HelperWorkingDirectory()
    {
        if (!OperatingSystem.IsWindows()) return "/";
        try { return Path.GetPathRoot(Environment.CurrentDirectory) is { Length: > 0 } root ? root : Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\"; }
    }

    /// <summary>The default <see cref="ClipboardCommandRunner"/>: spawns the process directly (no shell, no window) from
    /// <see cref="HelperWorkingDirectory"/>. On Linux and macOS it is detached (a new process group), so a clipboard owner it leaves
    /// behind (wl-copy, xclip, xsel) is not stopped by Ctrl+Z, which suspends pi's group (#11179).</summary>
    public static Task<byte[]?> RunClipboardCommand(string command, IReadOnlyList<string> args, ClipboardCommandOptions? options = null) =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? RunDetachedAsync(command, args, options) : RunProcessAsync(command, args, options);

    private static async Task<byte[]?> RunDetachedAsync(string command, IReadOnlyList<string> args, ClipboardCommandOptions? options)
    {
        var input = options?.Input;
        var timeoutMs = options?.TimeoutMs ?? DefaultTimeoutMs;
        var maxBufferBytes = options?.MaxBufferBytes ?? DefaultMaxBufferBytes;
        PosixSpawnProcessAdmission.DetachedHelper child;
        // Clipboard writers can daemonize: a writer (input given) gets no output pipe to retain.
        try { child = PosixSpawnProcessAdmission.SpawnDetachedHelper(command, args, HelperWorkingDirectory(), captureOutput: input is null); }
        catch (Exception error) when (error is Win32Exception or ArgumentException or PlatformNotSupportedException or IOException) { return null; }
        using var timeout = new CancellationTokenSource(timeoutMs);
        var chunks = new MemoryStream();
        var overflow = false;
        Task reader = Task.CompletedTask;
        if (child.StandardOutput is { } stdout)
        {
            reader = Task.Run(async () =>
            {
                var buffer = new byte[81920];
                while (true)
                {
                    int read;
                    try { read = await stdout.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
                    catch (Exception) { return; }
                    if (read == 0) return;
                    if (chunks.Length + read > maxBufferBytes) { overflow = true; return; }
                    chunks.Write(buffer, 0, read);
                }
            });
        }
        // stdin.end(input): written on its own thread, so a command that never reads cannot hold the call past the timeout (killing it
        // breaks the pipe and ends the write).
        _ = Task.Factory.StartNew(() =>
        {
            try { if (input is not null) child.StandardInput.Write(new UTF8Encoding(false).GetBytes(input)); }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // A writer may exit before consuming all input.
            }
            finally { try { child.StandardInput.Dispose(); } catch (IOException) { } }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await reader.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (overflow) { child.Kill(); return null; }
            var code = await child.Exit.WaitAsync(timeout.Token).ConfigureAwait(false);
            return code == 0 ? (input is null ? chunks.ToArray() : []) : null;
        }
        catch (OperationCanceledException)
        {
            child.Kill();
            return null;
        }
        finally { child.StandardOutput?.Dispose(); }
    }

    private static async Task<byte[]?> RunProcessAsync(string command, IReadOnlyList<string> args, ClipboardCommandOptions? options)
    {
        var input = options?.Input;
        var timeoutMs = options?.TimeoutMs ?? DefaultTimeoutMs;
        var maxBufferBytes = options?.MaxBufferBytes ?? DefaultMaxBufferBytes;
        var info = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = HelperWorkingDirectory(),
            RedirectStandardInput = true,
            // Clipboard writers can daemonize; their output pipes are never read (the source passes "ignore") and are closed when the
            // process object is disposed, so a daemon that keeps them cannot hold the call open.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("Process did not start.");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            return null;
        }
        using (process)
        using (var timeout = new CancellationTokenSource(timeoutMs))
        {
            var chunks = new MemoryStream();
            var overflow = false;
            Task? reader = null;
            if (input is null)
            {
                reader = Task.Run(async () =>
                {
                    var buffer = new byte[81920];
                    var stream = process.StandardOutput.BaseStream;
                    while (true)
                    {
                        int read;
                        try { read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
                        catch (Exception) { return; }
                        if (read == 0) return;
                        if (chunks.Length + read > maxBufferBytes) { overflow = true; return; }
                        chunks.Write(buffer, 0, read);
                    }
                });
            }
            try
            {
                var stdin = process.StandardInput.BaseStream;
                if (input is not null) await stdin.WriteAsync(new UTF8Encoding(false).GetBytes(input), timeout.Token).ConfigureAwait(false);
                stdin.Close();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // A writer may exit before consuming all input.
            }
            try
            {
                if (reader is not null)
                {
                    await reader.WaitAsync(timeout.Token).ConfigureAwait(false);
                    if (overflow) { Kill(process); return null; }
                }
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                return null;
            }
            if (overflow) return null;
            return process.ExitCode == 0 ? (input is null ? chunks.ToArray() : []) : null;
        }
    }

    private static void Kill(Process process)
    {
        try { process.Kill(); } catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }
}
