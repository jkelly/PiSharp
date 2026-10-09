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
    /// temporary directory as upstream does. No shell found leaves bash unregistered, as upstream fails to create it.</summary>
    private static (BashTool? Bash, UserBashHost? UserBash, OwnedProcessCleanup? Cleanup, string? Shell) PiBash(
        PiSharp.Cli.Pi.PiToolPolicy policy, BuiltinToolSettings settings, string workspace, Func<BashSessionEnvironment?> session)
    {
        // Windows runs the native job-object runner; Linux and macOS the POSIX process-group admission (posix_spawn into a fresh group).
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return (null, null, null, null);
        ShellConfiguration shell;
        try { shell = ShellDiscovery.Resolve(settings.ShellPath); }
        catch (ShellDiscoveryException) { return (null, null, null, null); }
        var environment = ShellDiscovery.ShellEnvironment(policy.Environment ?? ProcessEnvironment(), ShellDiscovery.BinDirectory(), OperatingSystem.IsWindows());
        var spill = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var unboundedOutput = new ProcessRunnerOptions(MaximumRawBytes: int.MaxValue);
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
