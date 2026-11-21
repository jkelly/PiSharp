// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/src/core/package-manager.ts (PackageCommandOutput,
// spawnCommand, spawnCaptureCommand, runCommandCapture, runCommand, runCommandCaptured, getNonInteractiveGitEnv, runCommandSync) and
// packages/coding-agent/src/utils/child-process.ts (spawnProcess, spawnProcessSync over cross-spawn 7 on Windows: PATH/PATHEXT
// resolution and cmd.exe argument escaping; waitForChildProcess for captured commands).
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Packages;

/// <summary>Source PackageCommandOutput: where git and npm output goes while packages are installed, updated or refreshed.
/// <see cref="Inherit"/>: the host's streams, live (<c>install</c>, <c>update</c>, and the print, JSON and RPC modes).
/// <see cref="Capture"/>: nothing is written and git and ssh never prompt, for a host that draws on the terminal (the interactive
/// TUI); a failure carries the end of the output.</summary>
internal enum PiPackageCommandOutput { Inherit, Capture }

/// <summary>The child processes the package manager runs (the user's own <c>npm</c> and <c>git</c>). Tests substitute it to record
/// argv; production uses this class. Output of inherited-stdio commands goes to <see cref="Output"/>/<see cref="ErrorOutput"/> when
/// set (the host's streams), else to this process's console.</summary>
internal partial class PiPackageProcesses
{
    /// <summary>Source CAPTURED_OUTPUT_TAIL_CHARS: the end of a captured command's output kept for its error message.</summary>
    internal const int CapturedOutputTailChars = 8000;
    /// <summary>Source EXIT_STDIO_GRACE_MS (waitForChildProcess): how long a captured command's pipes may stay quiet after it exited.</summary>
    private static readonly TimeSpan ExitStdioGrace = TimeSpan.FromMilliseconds(100);
    /// <summary>Source GIT_CREDENTIALS_HINT.</summary>
    internal const string GitCredentialsHint = "git cannot ask for a password or passphrase while " + Pi.PiConfig.DisplayName +
        " runs. Set up a git credential helper or an ssh agent, or install the package once from the shell with `" + Pi.PiConfig.DisplayName + " install <source>`.";

    /// <summary>Source GIT_PROMPT_FAILURE: output of git, or of ssh run by git, that failed because it could not ask for credentials.</summary>
    [GeneratedRegex(@"terminal prompts disabled|could not read (Username|Password)|Permission denied \(publickey|Host key verification failed|passphrase",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitPromptFailure();

    /// <summary>Source PackageManagerOptions.commandOutput (default inherit): how <see cref="RunAsync"/> runs its commands.</summary>
    internal PiPackageCommandOutput CommandOutput { get; set; }

    /// <summary>The processes of one package manager: the host's substitute (tests) or the user's own tools writing to the given
    /// streams, running commands as <paramref name="commandOutput"/> says.</summary>
    internal static PiPackageProcesses For(Pi.PiHost host, TextWriter output, TextWriter errorOutput,
        PiPackageCommandOutput commandOutput = PiPackageCommandOutput.Inherit)
    {
        var processes = host.PackageProcesses?.Invoke(output, errorOutput) ?? new PiPackageProcesses { Output = output, ErrorOutput = errorOutput };
        processes.CommandOutput = commandOutput;
        return processes;
    }

    /// <summary>Environment changes for every child (null removes a variable), applied over this process's environment.</summary>
    internal IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    internal TextWriter? Output { get; init; }
    internal TextWriter? ErrorOutput { get; init; }

    /// <summary>Source runCommand: stdio inherited; resolves on exit code 0, else <c>"&lt;command&gt; &lt;args&gt; failed with code N"</c>.
    /// With <see cref="PiPackageCommandOutput.Capture"/> the command runs as source runCommandCaptured.</summary>
    internal virtual async Task RunAsync(string command, IReadOnlyList<string> args, string? cwd, CancellationToken cancellationToken)
    {
        if (CommandOutput == PiPackageCommandOutput.Capture) { await RunCapturedAsync(command, args, cwd, cancellationToken).ConfigureAwait(false); return; }
        var exitCode = await RunInheritedAsync(command, args, cwd, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0) throw new PiPackageException($"{command} {string.Join(' ', args)} failed with code {exitCode}");
    }

    /// <summary>The end of a captured command's output and when the command last wrote.</summary>
    private sealed class CapturedTail
    {
        private readonly StringBuilder _text = new();
        private long _lastActivity = Stopwatch.GetTimestamp();
        internal void Append(char[] buffer, int count)
        {
            lock (_text)
            {
                _text.Append(buffer, 0, count);
                if (_text.Length > CapturedOutputTailChars) _text.Remove(0, _text.Length - CapturedOutputTailChars);
                _lastActivity = Stopwatch.GetTimestamp();
            }
        }
        internal void Touch() { lock (_text) _lastActivity = Stopwatch.GetTimestamp(); }
        internal TimeSpan Idle { get { lock (_text) return Stopwatch.GetElapsedTime(_lastActivity); } }
        public override string ToString() { lock (_text) return _text.ToString(); }
    }

    /// <summary>Source runCommandCaptured: runs a command without touching the terminal. Output is piped, standard input is closed and
    /// git and ssh cannot prompt; only the end of the output is kept, for the error message
    /// <c>"&lt;command&gt; &lt;args&gt; failed with code N:\n&lt;tail&gt;"</c>, with <see cref="GitCredentialsHint"/> when git could not ask
    /// for credentials.</summary>
    private async Task RunCapturedAsync(string command, IReadOnlyList<string> args, string? cwd, CancellationToken cancellationToken)
    {
        var process = Start(command, args, cwd, null, capture: true, nonInteractive: true);
        var tail = new CapturedTail();
        // Blocking reads on threads of their own: a read stays blocked for as long as any process holds the pipe open.
        void Read(StreamReader reader)
        {
            var buffer = new char[4096];
            try { for (int read; (read = reader.Read(buffer, 0, buffer.Length)) > 0;) tail.Append(buffer, read); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
        var pumps = Task.WhenAll(
            Task.Factory.StartNew(() => Read(process.StandardOutput), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default),
            Task.Factory.StartNew(() => Read(process.StandardError), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
        int exitCode;
        try
        {
            try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { Kill(process); throw; }
            exitCode = process.ExitCode;
            // Source waitForChildProcess: waits for the pipes too, but not for a background process an npm script left holding them.
            // After the exit the pipes are read until they end or stay quiet for the grace period (every chunk starts it again).
            tail.Touch();
            while (!pumps.IsCompleted && ExitStdioGrace - tail.Idle is var remaining && remaining > TimeSpan.Zero)
                await Task.WhenAny(pumps, Task.Delay(remaining, CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            // The pipes close with the process object; readers still blocked behind a background process finish first.
            if (pumps.IsCompleted) process.Dispose();
            else _ = pumps.ContinueWith(_ => process.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        if (exitCode == 0) return;
        var output = Pi.PiArgs.JsTrim(tail.ToString());
        var hint = command == "git" && GitPromptFailure().IsMatch(output) ? "\n" + GitCredentialsHint : "";
        throw new PiPackageException($"{command} {string.Join(' ', args)} failed with code {exitCode}{(output.Length > 0 ? ":\n" + output : "")}{hint}");
    }

    /// <summary>Pi 1.5.0 package-manager-cli.ts runSelfUpdate's spawnProcess: stdio inherited, no shell (cross-spawn on Windows),
    /// the exit code returned; a command that cannot start throws <c>"spawn &lt;command&gt; ENOENT"</c>.</summary>
    internal virtual async Task<int> RunInheritedAsync(string command, IReadOnlyList<string> args, string? cwd, CancellationToken cancellationToken)
    {
        using var process = Start(command, args, cwd, null, capture: Output is not null || ErrorOutput is not null);
        var pumps = new List<Task>();
        if (process.StartInfo.RedirectStandardOutput) pumps.Add(Pump(process.StandardOutput, Output ?? Console.Out));
        if (process.StartInfo.RedirectStandardError) pumps.Add(Pump(process.StandardError, ErrorOutput ?? Console.Error));
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { Kill(process); throw; }
        await Task.WhenAll(pumps).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>Source runCommandCapture: stdout and stderr captured; the trimmed stdout on success, else the failure with
    /// <c>stderr || stdout</c>; killed after <paramref name="timeoutMs"/>.</summary>
    internal virtual async Task<string> CaptureAsync(string command, IReadOnlyList<string> args, string? cwd, int? timeoutMs,
        IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        using var process = Start(command, args, cwd, environment, capture: true);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = timeoutMs is { } milliseconds ? new CancellationTokenSource(milliseconds) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new PiPackageException($"{command} {string.Join(' ', args)} timed out after {timeoutMs}ms");
        }
        var output = await stdout.ConfigureAwait(false); var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode == 0) return Pi.PiArgs.JsTrim(output);
        throw new PiPackageException($"{command} {string.Join(' ', args)} failed with code {process.ExitCode}: {(error.Length > 0 ? error : output)}");
    }

    /// <summary>Source runCommandSync: the trimmed output (stdout, else stderr), or <c>"Failed to run …"</c>.</summary>
    internal virtual string RunSync(string command, IReadOnlyList<string> args)
    {
        string stdout, stderr; int code;
        try
        {
            using var process = Start(command, args, null, null, capture: true);
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            stdout = output.GetAwaiter().GetResult(); stderr = error.GetAwaiter().GetResult(); code = process.ExitCode;
        }
        catch (PiPackageException error) { throw new PiPackageException($"Failed to run {command} {string.Join(' ', args)}: {error.Message}"); }
        if (code != 0) throw new PiPackageException($"Failed to run {command} {string.Join(' ', args)}: {(stderr.Length > 0 ? stderr : stdout)}");
        return Pi.PiArgs.JsTrim(stdout.Length > 0 ? stdout : stderr);
    }

    private static async Task Pump(StreamReader reader, TextWriter writer)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
        {
            await writer.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None).ConfigureAwait(false);
            await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>execvp lookup: a command containing a slash resolves against the working directory; a bare name is the first executable
    /// file on PATH (an empty PATH entry is the current directory).</summary>
    internal static string? UnixWhich(string command, string cwd, string? path)
    {
        static bool Executable(string candidate)
        {
            if (!File.Exists(candidate)) return false;
            if (OperatingSystem.IsWindows()) return true;
            const UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(candidate) & execute) != 0;
        }
        if (command.Contains('/')) return Path.GetFullPath(command, cwd);
        foreach (var directory in (path ?? "/usr/local/bin:/usr/bin:/bin").Split(':'))
        {
            var candidate = Path.Combine(directory.Length == 0 ? cwd : Path.GetFullPath(directory, cwd), command);
            if (Executable(candidate)) return candidate;
        }
        return null;
    }

    /// <param name="nonInteractive">Source runCommandCaptured's spawn: standard input is closed (stdio "ignore") and the environment is
    /// source getNonInteractiveGitEnv's, so git and ssh fail instead of prompting on the terminal, which a captured command cannot
    /// answer.</param>
    private Process Start(string command, IReadOnlyList<string> args, string? cwd, IReadOnlyDictionary<string, string>? extra, bool capture,
        bool nonInteractive = false)
    {
        var info = new ProcessStartInfo
        { UseShellExecute = false, RedirectStandardOutput = capture, RedirectStandardError = capture, RedirectStandardInput = nonInteractive };
        if (capture) { info.StandardOutputEncoding = Encoding.UTF8; info.StandardErrorEncoding = Encoding.UTF8; }
        if (cwd is not null) info.WorkingDirectory = cwd;
        foreach (var (name, value) in Environment)
            if (value is null) info.Environment.Remove(name); else info.Environment[name] = value;
        if (extra is not null) foreach (var (name, value) in extra) info.Environment[name] = value;
        if (nonInteractive)
        {
            info.Environment["GIT_TERMINAL_PROMPT"] = "0";
            // A user-defined GIT_SSH_COMMAND is kept: it may be needed to reach the host at all.
            if (!info.Environment.ContainsKey("GIT_SSH_COMMAND")) info.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        }
        if (OperatingSystem.IsWindows())
        {
            // cross-spawn (shared with the MCP host): .exe/.com run directly, batch files and npm .cmd shims through cmd.exe with
            // cmd escaping. A command that does not resolve fails as cross-spawn reports it (ENOENT).
            var environment = info.Environment.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
            var directory = cwd ?? System.Environment.CurrentDirectory;
            if (PiSharp.Cli.Mcp.WindowsCommand.Which(command, directory, environment) is null) throw new PiPackageException($"spawn {command} ENOENT");
            var admission = PiSharp.Cli.Mcp.WindowsCommand.Resolve(command, [.. args], directory, environment);
            info.FileName = admission.Executable;
            if (admission.VerbatimArguments is { } verbatim) info.Arguments = verbatim;
            else foreach (var argument in admission.Arguments) info.ArgumentList.Add(argument);
        }
        else
        {
            // child_process.spawn on Unix: a bare command is looked up on the child environment's PATH (execvp), a path is used as is.
            info.FileName = UnixWhich(command, cwd ?? System.Environment.CurrentDirectory,
                info.Environment.TryGetValue("PATH", out var path) ? path : null) ?? throw new PiPackageException($"spawn {command} ENOENT");
            foreach (var argument in args) info.ArgumentList.Add(argument);
        }
        Process process;
        try { process = Process.Start(info) ?? throw new PiPackageException($"spawn {command} ENOENT"); }
        catch (System.ComponentModel.Win32Exception) { throw new PiPackageException($"spawn {command} ENOENT"); }
        if (nonInteractive)
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { } // the command exited already
        }
        return process;
    }
}
