// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/package-manager.ts (spawnCommand,
// spawnCaptureCommand, runCommandCapture, runCommand, runCommandSync) and packages/coding-agent/src/utils/child-process.ts
// (spawnProcess, spawnProcessSync over cross-spawn 7 on Windows: PATH/PATHEXT resolution and cmd.exe argument escaping).
using System.Diagnostics;
using System.Text;

namespace PiSharp.Cli.Packages;

/// <summary>The child processes the package manager runs (the user's own <c>npm</c> and <c>git</c>). Tests substitute it to record
/// argv; production uses this class. Output of inherited-stdio commands goes to <see cref="Output"/>/<see cref="ErrorOutput"/> when
/// set (the host's streams), else to this process's console.</summary>
internal class PiPackageProcesses
{
    /// <summary>Environment changes for every child (null removes a variable), applied over this process's environment.</summary>
    internal IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    internal TextWriter? Output { get; init; }
    internal TextWriter? ErrorOutput { get; init; }

    /// <summary>Source runCommand: stdio inherited; resolves on exit code 0, else <c>"&lt;command&gt; &lt;args&gt; failed with code N"</c>.</summary>
    internal virtual async Task RunAsync(string command, IReadOnlyList<string> args, string? cwd, CancellationToken cancellationToken)
    {
        using var process = Start(command, args, cwd, null, capture: Output is not null || ErrorOutput is not null);
        var pumps = new List<Task>();
        if (process.StartInfo.RedirectStandardOutput) pumps.Add(Pump(process.StandardOutput, Output ?? Console.Out));
        if (process.StartInfo.RedirectStandardError) pumps.Add(Pump(process.StandardError, ErrorOutput ?? Console.Error));
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { Kill(process); throw; }
        await Task.WhenAll(pumps).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new PiPackageException($"{command} {string.Join(' ', args)} failed with code {process.ExitCode}");
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

    private Process Start(string command, IReadOnlyList<string> args, string? cwd, IReadOnlyDictionary<string, string>? extra, bool capture)
    {
        var info = new ProcessStartInfo { UseShellExecute = false, RedirectStandardOutput = capture, RedirectStandardError = capture };
        if (capture) { info.StandardOutputEncoding = Encoding.UTF8; info.StandardErrorEncoding = Encoding.UTF8; }
        if (cwd is not null) info.WorkingDirectory = cwd;
        foreach (var (name, value) in Environment)
            if (value is null) info.Environment.Remove(name); else info.Environment[name] = value;
        if (extra is not null) foreach (var (name, value) in extra) info.Environment[name] = value;
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
        try { return Process.Start(info) ?? throw new PiPackageException($"spawn {command} ENOENT"); }
        catch (System.ComponentModel.Win32Exception) { throw new PiPackageException($"spawn {command} ENOENT"); }
    }
}
