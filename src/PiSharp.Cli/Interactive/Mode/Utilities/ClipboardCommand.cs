// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/utils/clipboard-command.ts.
// A clipboard command is spawned without a shell. Its result is the captured standard output, or null when it could not start,
// exited non-zero, timed out or exceeded the output limit (the source's undefined). An empty array is a successful empty result.
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PiSharp.Cli.Interactive.Mode.Utilities;

/// <summary>The source's <c>{ input?, timeoutMs?, maxBufferBytes? }</c>. Record equality lets tests compare recorded calls.</summary>
internal sealed record ClipboardCommandOptions(string? Input = null, int? TimeoutMs = null, long? MaxBufferBytes = null);

/// <summary><c>runClipboardCommand</c>: null means the command failed; an empty array is a successful result.</summary>
internal delegate Task<byte[]?> ClipboardCommandRunner(string command, IReadOnlyList<string> args, ClipboardCommandOptions? options);

internal static class ClipboardCommand
{
    private const int DefaultTimeoutMs = 3000;
    private const long DefaultMaxBufferBytes = 50L * 1024 * 1024;

    /// <summary>The default <see cref="ClipboardCommandRunner"/>: spawns the process directly (no shell, no window).</summary>
    public static async Task<byte[]?> RunClipboardCommand(string command, IReadOnlyList<string> args, ClipboardCommandOptions? options = null)
    {
        var input = options?.Input;
        var timeoutMs = options?.TimeoutMs ?? DefaultTimeoutMs;
        var maxBufferBytes = options?.MaxBufferBytes ?? DefaultMaxBufferBytes;
        var info = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
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
