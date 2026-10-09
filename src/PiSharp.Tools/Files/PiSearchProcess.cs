// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/tools/grep.ts and find.ts (spawn with
// stdio ["ignore", "pipe", "pipe"], readline over stdout, stderr collected, child.kill()).
using System.Diagnostics;
using System.Text;

namespace PiSharp.Tools.Files;

/// <summary>The search process of the Pi grep and find tools: stdout is read line by line while the process runs, and the reader can
/// stop it early (grep's match limit). The <c>pi</c> tool policy runs it; the explicit policy keeps its admitted executors.</summary>
public static class PiSearchProcess
{
    /// <summary>How a search process ended. <see cref="ExitCode"/> is null when it was stopped (source: code null after kill).</summary>
    public sealed record Outcome(int? ExitCode, string StandardError, bool Stopped);

    /// <summary>Runs <paramref name="executable"/> with <paramref name="arguments"/>; <paramref name="onLine"/> returns true to stop the
    /// process. Cancellation stops it and throws <see cref="OperationCanceledException"/>. A start failure throws
    /// <see cref="IOException"/> with the system's message.</summary>
    public static async Task<Outcome> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment, Func<string, bool> onLine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executable); ArgumentNullException.ThrowIfNull(arguments); ArgumentNullException.ThrowIfNull(onLine);
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, false), StandardErrorEncoding = new UTF8Encoding(false, false)
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
        {
            start.Environment.Clear();
            foreach (var (name, value) in environment) start.Environment[name] = value;
        }
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new IOException("The process did not start."); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { throw new IOException(error.Message, error); }
        // Source stdio "ignore": the child gets no input.
        try { process.StandardInput.Close(); } catch (IOException) { }
        var stopped = false;
        void Stop()
        {
            stopped = true;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        }
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using (cancellationToken.Register(Stop))
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                    if (!stopped && onLine(line)) Stop();
            }
            catch (IOException) when (stopped) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        var standardError = await stderr.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(stopped ? null : process.ExitCode, standardError, stopped);
    }
}
