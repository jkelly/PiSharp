// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/shell.ts (getShellConfig, getShellEnv) and
// packages/coding-agent/src/core/tools/bash.ts (the bash tool runs any command in the session cwd, spilling to os.tmpdir()).
using System.Collections;
using System.Collections.Immutable;
using PiSharp.Tools;
using PiSharp.Tools.Processes;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    /// <summary>The bash tool and user bash of the <c>pi</c> tool policy (owner decision 0004): the discovered shell (settings
    /// <c>shellPath</c>, then platform discovery), the full environment with Pi's bin directory first on PATH, output spilled to the
    /// temporary directory as upstream does. No shell found leaves the bash tool unregistered; user commands then fail with the discovery
    /// error, as upstream's exec does.</summary>
    private static (BashTool? Bash, UserBashHost? UserBash, OwnedProcessCleanup? Cleanup, string? Shell) PiBash(
        PiSharp.Cli.Pi.PiToolPolicy policy, BuiltinToolSettings settings, string workspace, Func<BashSessionEnvironment?> session)
    {
        // Windows runs the native job-object runner; Linux and macOS the POSIX process-group admission (posix_spawn into a fresh group).
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return (null, null, null, null);
        ShellConfiguration shell;
        var spill = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var environment = ShellDiscovery.ShellEnvironment(policy.Environment ?? ProcessEnvironment(), ShellDiscovery.BinDirectory(), OperatingSystem.IsWindows());
        var unboundedOutput = new ProcessRunnerOptions(MaximumRawBytes: int.MaxValue);
        try { shell = ShellDiscovery.Resolve(settings.ShellPath); }
        catch (ShellDiscoveryException error)
        {
            // createLocalBashOperations resolves the shell inside exec: a user command fails with getShellConfig's error, or, for a
            // shellPath that is a directory (existsSync accepts it), with spawn's error for it.
            IShellOperations unavailable;
            try
            {
                var directory = ShellDiscovery.Resolve(settings.ShellPath, ShellHost.Current with { DirectoryExists = Directory.Exists });
                unavailable = OperatingSystem.IsWindows() ? new NativeShellOperations(directory, environment, spill, unboundedOutput)
                    : new PiSharp.Tools.Processes.Unix.PosixShellOperations(directory, environment, spill);
            }
            catch (ShellDiscoveryException) { unavailable = new UnavailableShellOperations(error.Message); }
            return (null, new UserBashHost(new(unavailable, spill), settings.ShellCommandPrefix), null, null);
        }
        var cleanup = new OwnedProcessCleanup(OperatingSystem.IsWindows() ? new NativeProcessRunner(unboundedOutput)
            : new PiSharp.Tools.Processes.Unix.UnixProcessRunner(new PiSharp.Tools.Processes.Unix.PosixSpawnProcessAdmission(), unboundedOutput));
        try
        {
            var bash = new BashTool(cleanup, BashToolOptions.FromShell(shell, workspace, environment, spill) with
            { CommandPrefix = settings.ShellCommandPrefix, SessionEnvironment = session });
            IShellOperations operations = OperatingSystem.IsWindows() ? new NativeShellOperations(shell, environment, spill, unboundedOutput)
                : new PiSharp.Tools.Processes.Unix.PosixShellOperations(shell, environment, spill);
            var user = new UserBashHost(new(operations, spill), settings.ShellCommandPrefix);
            return (bash, user, cleanup, shell.Shell);
        }
        catch (ArgumentException) { return (null, null, null, null); }
    }

    /// <summary>Source createLocalShellOperations exec when getShellConfig throws ("Custom shell path not found", "No bash shell found"):
    /// the command fails with that message before anything runs.</summary>
    private sealed class UnavailableShellOperations(string message) : IShellOperations
    {
        public ValueTask<int?> ExecuteAsync(string command, string workingDirectory, ProcessRawOutputCallback onData, CancellationToken cancellationToken) =>
            throw new PiSharp.Agent.ToolSourceErrorException(message);
    }

    /// <summary>The separated process runner of the search tools: the native runner on Windows, the POSIX process-group runner on Linux
    /// and macOS.</summary>
    internal static ISeparatedProcessRunner PiProcessRunner() => OperatingSystem.IsWindows() ? new NativeProcessRunner()
        : new PiSharp.Tools.Processes.Unix.UnixProcessRunner(new PiSharp.Tools.Processes.Unix.PosixSpawnProcessAdmission());

    /// <summary>The process environment (source process.env), one entry per name.</summary>
    internal static IReadOnlyDictionary<string, string> ProcessEnvironment()
    {
        var result = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && key.Length > 0 && !key.Contains('=') && entry.Value is string value) result.TryAdd(key, value);
        return result;
    }
}
