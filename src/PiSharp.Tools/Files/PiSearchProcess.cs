// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/tools/grep.ts and find.ts (spawn with
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
        cancellationToken.ThrowIfCancellationRequested();
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
        var stderr = ReadErrorAsync(process.StandardError);
        IOException? readError = null;
        using (cancellationToken.Register(Stop))
        {
            try
            {
                await foreach (var line in ReadLinesAsync(process.StandardOutput))
                    if (!stopped && onLine(line)) Stop();
            }
            catch (IOException error) { if (!stopped) readError = error; Stop(); }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        var standardError = await stderr.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (readError is not null) throw readError;
        return new(stopped ? null : process.ExitCode, standardError, stopped);
    }
    private static async IAsyncEnumerable<string> ReadLinesAsync(StreamReader reader)
    {
        var buffer = new char[8192]; var line = new StringBuilder(); int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n') { yield return line.ToString().TrimEnd('\r'); line.Clear(); }
                else
                {
                    if (line.Length >= 4 * 1024 * 1024) throw new IOException("Search output line exceeds the supported input limit.");
                    line.Append(buffer[i]);
                }
            }
        }
        if (line.Length != 0) yield return line.ToString().TrimEnd('\r');
    }
    private static async Task<string> ReadErrorAsync(StreamReader reader)
    {
        var buffer = new char[8192]; var result = new StringBuilder(); int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
            if (result.Length < 65536) result.Append(buffer, 0, Math.Min(count, 65536 - result.Length));
        return result.ToString();
    }

}
