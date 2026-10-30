// Decision 0011 (run W4, R5): the check command of an agent compare run ("dotnet test", "npm test"), run in one model's working directory
// when the model stops. It runs through the platform's shell (cmd.exe /d /s /c on Windows, /bin/sh -c elsewhere) with stdin closed, its
// stdout and stderr read together; the last part of the output (TailCharacters) is kept with the exit code. After the timeout the command
// and every process it started are killed (Process.Kill with the whole tree), and the result says it timed out. It inherits the app's
// environment minus variables named like credentials (*_API_KEY, *_TOKEN, *_SECRET, *_PASSWORD, *_KEY, PISHARP_WEBAGENT_DATA), so a test the
// model wrote can't read the app's provider keys; workspaces are still copies, not sandboxes. A test summary in the output (dotnet test's
// "Passed: 38" or "succeeded: 38", "38 passed", "Tests: 2 failed, 36 passed") becomes "38 passed" for the column.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.WebAgent.AgentCompare.Runner;

/// <summary>Runs a check command (see the file comment).</summary>
public static partial class AgentCheckCommand
{
    /// <summary>How much of the end of the output is kept.</summary>
    public const int TailCharacters = 4000;

    /// <summary>The longest check command.</summary>
    public const int MaxCommandLength = 2000;

    /// <summary>How long the output may still be read after the command was killed.</summary>
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(5);

    /// <summary>Runs <paramref name="command"/> in <paramref name="directory"/> for at most <paramref name="timeout"/>.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled (the process tree is killed first).</exception>
    public static async Task<AgentCheckResult> RunAsync(string command, string directory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var tail = new Tail(TailCharacters);
        var timeoutSeconds = (int)Math.Ceiling(timeout.TotalSeconds);
        AgentCheckResult Result(string state, int? exit, string? extra = null)
        {
            if (extra is not null) tail.Append(extra);
            var text = tail.ToString();
            return new AgentCheckResult
            {
                State = state,
                Command = command,
                ExitCode = exit,
                Tail = text,
                Summary = Summarize(text),
                TimeoutSeconds = timeoutSeconds,
                DurationMs = clock.ElapsedMilliseconds,
                Started = started,
                Finished = DateTimeOffset.UtcNow,
            };
        }

        Process process;
        try
        {
            process = Process.Start(StartInfo(command, directory)) ?? throw new InvalidOperationException("The shell did not start.");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return Result(AgentCheckState.Error, null, "The check command could not be started: " + error.Message);
        }
        using (process)
        {
            var output = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var errors = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, e) => { if (e.Data is null) output.TrySetResult(); else tail.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is null) errors.TrySetResult(); else tail.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try { process.StandardInput.Close(); } catch (IOException) { /* It exited already. */ }

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                await Drain(output.Task, errors.Task).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return Result(AgentCheckState.TimedOut, null,
                    $"[Stopped after {Duration(timeout)}: the check command ran longer than its timeout, so it and every process it started were ended.]");
            }
            await Drain(output.Task, errors.Task).ConfigureAwait(false);
            var exit = process.ExitCode;
            return Result(exit == 0 ? AgentCheckState.Passed : AgentCheckState.Failed, exit);
        }
    }

    /// <summary>A test summary in <paramref name="output"/>, or null.</summary>
    public static string? Summarize(string output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        int? passed = null, failed = null;
        static int Sum(MatchCollection matches) => matches.Sum(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        // dotnet test (VSTest): "Failed!  - Failed: 2, Passed: 36, Skipped: 0, Total: 38" per project; (MTP) "succeeded: 36", "failed: 2".
        if (DotnetPassed().Matches(output) is { Count: > 0 } dp) passed = Sum(dp);
        if (DotnetFailed().Matches(output) is { Count: > 0 } df) failed = Sum(df);
        // Jest, Vitest, pytest, cargo and others: "36 passed", "2 failed".
        if (passed is null && WordPassed().Matches(output) is { Count: > 0 } wp) passed = int.Parse(wp[^1].Groups[1].Value, CultureInfo.InvariantCulture);
        if (failed is null && WordFailed().Matches(output) is { Count: > 0 } wf) failed = int.Parse(wf[^1].Groups[1].Value, CultureInfo.InvariantCulture);
        if (passed is null && failed is null) return null;
        var parts = new List<string>();
        if (failed is > 0) parts.Add(failed.Value.ToString("N0", CultureInfo.InvariantCulture) + " failed");
        if (passed is not null) parts.Add(passed.Value.ToString("N0", CultureInfo.InvariantCulture) + " passed");
        return parts.Count == 0 ? "0 failed" : string.Join(" · ", parts);
    }

    private static ProcessStartInfo StartInfo(string command, string directory)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            // /s with the whole command in one pair of quotes: cmd removes those quotes and runs the rest as it is.
            info = new ProcessStartInfo(Path.Join(Environment.SystemDirectory, "cmd.exe")) { Arguments = "/d /s /c \"" + command + "\"" };
        }
        else
        {
            info = new ProcessStartInfo("/bin/sh");
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(command);
        }
        info.WorkingDirectory = directory;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;
        foreach (var name in info.Environment.Keys.ToList())
            if (IsCredentialName(name)) info.Environment.Remove(name);
        info.Environment["CI"] = info.Environment.TryGetValue("CI", out var ci) && !string.IsNullOrEmpty(ci) ? ci : "1";
        return info;
    }

    /// <summary>Whether an environment variable is left out of the check's environment.</summary>
    public static bool IsCredentialName(string name) =>
        name.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_PASSWORD", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase) || name.Equals("PISHARP_WEBAGENT_DATA", StringComparison.OrdinalIgnoreCase);

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* Gone. */ }
        try { process.WaitForExit(5000); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { /* Gone. */ }
    }

    private static async Task Drain(Task output, Task errors)
    {
        try { await Task.WhenAll(output, errors).WaitAsync(DrainLimit).ConfigureAwait(false); }
        catch (TimeoutException) { /* A process the kill missed still holds the pipe; the tail has what arrived. */ }
    }

    private static string Duration(TimeSpan timeout) => timeout.TotalSeconds < 120
        ? ((int)Math.Ceiling(timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " s"
        : ((int)Math.Round(timeout.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " min";

    [GeneratedRegex(@"(?:\bPassed:|\bsucceeded:)\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex DotnetPassed();

    [GeneratedRegex(@"(?:\bFailed:|\bfailed:)\s*(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex DotnetFailed();

    [GeneratedRegex(@"\b(\d+)\s+passed\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WordPassed();

    [GeneratedRegex(@"\b(\d+)\s+failed\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WordFailed();

    /// <summary>The last <c>capacity</c> characters appended, thread-safe.</summary>
    private sealed class Tail(int capacity)
    {
        private readonly Lock gate = new();
        private readonly StringBuilder text = new();

        public void AppendLine(string line)
        {
            lock (gate)
            {
                text.Append(line).Append('\n');
                Trim();
            }
        }

        public void Append(string extra)
        {
            lock (gate)
            {
                if (text.Length > 0 && text[^1] != '\n') text.Append('\n');
                text.Append(extra);
                Trim();
            }
        }

        public override string ToString()
        {
            lock (gate) return text.Length > capacity ? "…" + text.ToString(text.Length - capacity + 1, capacity - 1) : text.ToString();
        }

        // Kept between capacity and twice capacity, so appending stays cheap; ToString cuts to capacity.
        private void Trim()
        {
            if (text.Length > capacity * 2) text.Remove(0, text.Length - capacity);
        }
    }
}
