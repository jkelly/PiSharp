// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/tools/bash.ts (BashToolOptions,
// BashSpawnContext, resolveSpawnContext).
using System.Collections.Immutable;

namespace PiSharp.Tools.Processes;

/// <summary>Trusted explicit configuration. Environment is the complete child environment, not an overlay.</summary>
public sealed record BashToolOptions(string Executable, string WorkingDirectory,
    ImmutableDictionary<string, string> Environment, string SpillDirectory,
    int MaximumCommandCharacters = 12_000, int MaximumArgumentCharacters = 96_000)
{
    /// <summary>Source ShellConfig.args: the arguments before the command. Default <c>-c</c>.</summary>
    public ImmutableArray<string> ShellArguments { get; init; } = ["-c"];
    /// <summary>Source ShellConfig.commandTransport. Stdin writes the command to the shell's standard input (legacy WSL bash).</summary>
    public ShellCommandTransport CommandTransport { get; init; } = ShellCommandTransport.Argv;
    /// <summary>Source commandPrefix (settings shellCommandPrefix): the shell runs <c>prefix + "\n" + command</c>.</summary>
    public string? CommandPrefix { get; init; }
    /// <summary>Source exposeSessionEnvironment (default true): the PI_* variables and the PI_* prompt guideline.</summary>
    public bool ExposeSessionEnvironment { get; init; } = true;
    /// <summary>The current session's PI_* values, read for each command; null when there is no session context.</summary>
    public Func<BashSessionEnvironment?>? SessionEnvironment { get; init; }
    /// <summary>Source spawnHook: adjusts the command, working directory or environment before execution. The final
    /// action, including any change the hook makes, is what the host policy authorizes.</summary>
    public Func<BashSpawnContext, BashSpawnContext>? SpawnHook { get; init; }

    /// <summary>Options for a resolved <see cref="ShellConfiguration"/>.</summary>
    public static BashToolOptions FromShell(ShellConfiguration shell, string workingDirectory,
        ImmutableDictionary<string, string> environment, string spillDirectory)
    {
        ArgumentNullException.ThrowIfNull(shell);
        return new(shell.Shell, workingDirectory, environment, spillDirectory)
        { ShellArguments = shell.Arguments, CommandTransport = shell.CommandTransport };
    }
}

/// <summary>Source PI_* session variables: PI_SESSION_ID, PI_SESSION_FILE, PI_PROVIDER, PI_MODEL and PI_REASONING_LEVEL.</summary>
public sealed record BashSessionEnvironment(string SessionId, string? SessionFile = null, string? Provider = null,
    string? Model = null, string? ReasoningLevel = null)
{
    public static ImmutableArray<string> VariableNames { get; } =
        ["PI_SESSION_ID", "PI_SESSION_FILE", "PI_PROVIDER", "PI_MODEL", "PI_REASONING_LEVEL"];

    /// <summary>Source resolveSpawnContext: remove inherited PI_* session variables, then set the current session's values.</summary>
    public static ImmutableDictionary<string, string> Apply(ImmutableDictionary<string, string> environment,
        BashSessionEnvironment? session)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var result = environment.RemoveRange(environment.Keys.Where(key => VariableNames.Contains(key, StringComparer.Ordinal)));
        if (session is null) return result;
        result = result.SetItem("PI_SESSION_ID", session.SessionId);
        if (!string.IsNullOrEmpty(session.SessionFile)) result = result.SetItem("PI_SESSION_FILE", session.SessionFile);
        if (session.Provider is not null && session.Model is not null)
            result = result.SetItem("PI_PROVIDER", session.Provider).SetItem("PI_MODEL", session.Model);
        if (!string.IsNullOrEmpty(session.ReasoningLevel)) result = result.SetItem("PI_REASONING_LEVEL", session.ReasoningLevel);
        return result;
    }
}

/// <summary>Source BashSpawnContext.</summary>
public sealed record BashSpawnContext(string Command, string WorkingDirectory, ImmutableDictionary<string, string> Environment);
