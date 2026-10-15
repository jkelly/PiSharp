// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/env/node.ts (spawn, killProcessTree and the spawn errors Node
// reports before a process runs). The process primitives are PiSharp.Tools': on Windows the job-object lifetime the coding agent's bash tool
// uses (WindowsProcessLifetime: the process starts suspended inside a private kill-on-close job, so killing the job kills every descendant,
// where upstream runs `taskkill /F /T`, which can miss descendants whose parent already exited); on Linux and macOS the process-group
// admission (PosixSpawnProcessAdmission: the command runs in a fresh process group, so SIGKILL to the group kills its descendants, as upstream's
// `process.kill(-pid)` on its detached group does). Program lookup follows libuv: on Windows the working directory and then PATH, trying the
// name as given (when it has an extension) and with .com and .exe; on Unix PATH for a bare name. The spawn checks (missing working
// directory, NUL bytes, command-line limits, missing or non-executable programs) are PiSharp.Tools' ShellSpawnPreflight, with Node's texts.
using System.Collections.Immutable;
using PiSharp.Tools;
using PiSharp.Tools.Processes;
using PiSharp.Tools.Processes.Unix;

namespace PiSharp.Durable.Tools.Env;

/// <summary>A process this environment started: its output streams, its exit, and a kill of its whole tree.</summary>
internal abstract class LocalProcess : IAsyncDisposable
{
    public abstract Stream StandardOutput { get; }
    public abstract Stream StandardError { get; }

    /// <summary>The exit code (128 plus the signal for a process a signal killed), or null when it could not be read.</summary>
    public abstract Task<int?> Exit { get; }

    /// <summary>Kills the process and every descendant still in its job or group; idempotent.</summary>
    public abstract Task KillAsync();

    /// <summary>Kills what is left of the tree and releases the pipes.</summary>
    public abstract ValueTask DisposeAsync();

    /// <summary>Starts <paramref name="file"/> (already resolved and checked) in <paramref name="workingDirectory"/> with exactly
    /// <paramref name="environment"/>, writing <paramref name="standardInput"/> to its input and then closing it.</summary>
    /// <exception cref="LocalSpawnException">The operating system refused to start it.</exception>
    public static async Task<LocalProcess> StartAsync(string file, ImmutableArray<string> arguments, string workingDirectory,
        ImmutableDictionary<string, string> environment, byte[]? standardInput)
    {
        var request = new ProcessRequest(file, arguments, workingDirectory, environment, SpillPath: "") { StandardInput = standardInput };
        if (OperatingSystem.IsWindows())
        {
            try { return new WindowsProcess(await WindowsProcessLifetime.StartAsync(request, null, CancellationToken.None).ConfigureAwait(false)); }
            catch (ProcessLaunchException error) when (error.NativeError is { } code)
            {
                throw new LocalSpawnException(NodeArgumentErrors.SpawnFailure(file, NodeArgumentErrors.WindowsErrorCode(code)), error);
            }
            catch (Exception error) { throw new LocalSpawnException(error.Message, error); }
        }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try { return new UnixProcess(await new PosixSpawnProcessAdmission().LaunchAsync(request, CancellationToken.None).ConfigureAwait(false)); }
            catch (Exception error) { throw new LocalSpawnException(error.Message, error); }
        }
        throw new LocalSpawnException("spawn ENOSYS: processes are not supported on this platform", null);
    }

    /// <summary>
    /// The file Node's spawn would run for <paramref name="program"/>, checked as libuv checks it before running anything; throws
    /// <see cref="LocalSpawnException"/> with Node's error text when it would refuse. <paramref name="arguments"/> are checked for NUL bytes
    /// and the command-line limit too.
    /// </summary>
    public static string Resolve(string program, ImmutableArray<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        var path = environment.FirstOrDefault(pair => pair.Key.Equals("PATH", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Value
            ?? System.Environment.GetEnvironmentVariable("PATH") ?? "";
        var file = Search(program, workingDirectory, path) ?? throw new LocalSpawnException(NodeArgumentErrors.SpawnFailure(program, "ENOENT"), null);
        try
        {
            // The command transport "stdin" makes CommandArguments the arguments as given.
            return ShellSpawnPreflight.Check(new ShellConfiguration(file, arguments, ShellCommandTransport.Stdin), "", workingDirectory, environment);
        }
        catch (PiSharp.Agent.ToolSourceErrorException error) { throw new LocalSpawnException(error.Message, error); }
    }

    private static string? Search(string program, string workingDirectory, string path)
    {
        if (program.Length == 0 || program.Contains('\0')) return program;
        if (OperatingSystem.IsWindows())
        {
            if (program.IndexOfAny(['/', '\\', ':']) >= 0) return Path.GetFullPath(program, workingDirectory);
            foreach (var directory in path.Split(';').Select(entry => entry.Trim().Trim('"')).Where(entry => entry.Length > 0).Prepend(workingDirectory))
            {
                string candidate;
                try { candidate = Path.GetFullPath(Path.Join(directory, program), workingDirectory); }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (ShellSpawnPreflight.WindowsSearch(candidate) is not null) return candidate;
            }
            return null;
        }
        // execvp: a name with a slash is used as is (relative to the child's working directory); a bare name is searched on PATH.
        if (program.Contains('/')) return Path.GetFullPath(program, workingDirectory);
        foreach (var directory in path.Split(':'))
        {
            var candidate = Path.GetFullPath(Path.Join(directory.Length == 0 ? "." : directory, program), workingDirectory);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class WindowsProcess(WindowsProcessLifetime lifetime) : LocalProcess
    {
        public override Stream StandardOutput => lifetime.StandardOutput;
        public override Stream StandardError => lifetime.StandardError;
        public override Task<int?> Exit => lifetime.Exit;
        public override Task KillAsync() => lifetime.TerminateAndConfirmAsync();
        public override ValueTask DisposeAsync() => lifetime.DisposeAsync();
    }

    private sealed class UnixProcess(IUnixProcessLease lease) : LocalProcess
    {
        private readonly Task<int?> exit = lease.Exit.ContinueWith(task => task.IsCompletedSuccessfully && task.Result >= 0 ? task.Result : (int?)null,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        public override Stream StandardOutput => lease.StandardOutput;
        public override Stream StandardError => lease.StandardError;
        public override Task<int?> Exit => exit;
        public override Task KillAsync() => lease.StopGroupAsync().AsTask();
        public override async ValueTask DisposeAsync()
        {
            try { await lease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { } // Best effort, as upstream ignores kill failures.
        }
    }
}

/// <summary>The operating system or Node's spawn checks refused to start a program.</summary>
internal sealed class LocalSpawnException(string message, Exception? cause) : Exception(message, cause);
