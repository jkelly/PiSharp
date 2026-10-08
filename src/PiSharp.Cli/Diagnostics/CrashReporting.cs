// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/crash-log.ts (crashLogPath) and
// src/modes/interactive/interactive-mode.ts (recordCrash, uncaughtCrash, startup crash notice).
using PiSharp.Cli.Interactive;
using PiSharp.CodingAgent.Diagnostics;

namespace PiSharp.Cli.Diagnostics;

/// <summary>Host glue for <see cref="CrashLog"/>: the default <c>crashes.json</c> under Pi's agent directory
/// (<c>PI_CODING_AGENT_DIR</c>, expanded as config.ts <c>getAgentDir</c> does, else <c>~/.pi/agent</c>) and the
/// top-level crash recording the interactive mode performs.</summary>
public static class CrashReporting
{
    /// <summary><c>crashLogPath()</c> for this process, or for the given environment and home directory.</summary>
    public static string DefaultCrashLogPath(IReadOnlyDictionary<string, string?>? environment = null, string? home = null) =>
        CrashLog.CrashLogPath(TerminalKeybindingConfigurationLoader.ResolveAgentDirectory(
            home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OperatingSystem.IsWindows() ? "win32" : "linux",
            environment ?? new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") }));

    /// <summary>interactive-mode.ts <c>recordCrash</c>: persist a crash so the next start can point the user at <c>/bug</c>.
    /// <paramref name="kind"/> is <see cref="CrashRecord.UncaughtException"/> or <see cref="CrashRecord.FatalError"/>. Returns
    /// false when nothing was written; never throws.</summary>
    public static bool RecordCrash(string kind, object? error, string? sessionFile, string cwd, string? path = null)
    {
        try { return CrashLog.RecordCrash(kind, error, sessionFile, cwd, path ?? DefaultCrashLogPath()) is not null; }
        catch (Exception) { return false; }
    }

    /// <summary>The lines interactive-mode.ts <c>uncaughtCrash</c> writes to standard error after the terminal is restored,
    /// recording the crash on the way: the exit banner, the error, the extension hint when frames came from loaded
    /// extensions, and the <c>/bug</c> instructions when the crash was recorded. The host then exits with code 1.</summary>
    public static void ReportUncaughtException(Exception error, TextWriter standardError, IEnumerable<DiagnosticExtensionInfo> extensions,
        string? sessionFile, string cwd, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(error); ArgumentNullException.ThrowIfNull(standardError);
        standardError.Write($"{CrashLog.AppName} exiting due to uncaughtException:\n");
        standardError.Write(error + "\n");
        string? hint;
        try { hint = CrashLog.FormatCrashExtensionHint(CrashLog.FindExtensionStackMatches(error.ToString(), extensions)); }
        catch (Exception) { hint = null; }
        if (hint is not null) standardError.Write($"\n{hint}\n");
        if (RecordCrash(CrashRecord.UncaughtException, error, sessionFile, cwd, path))
            standardError.Write($"\n{CrashLog.CrashReportInstructions(sessionFile)}\n");
        standardError.Flush();
    }
}
