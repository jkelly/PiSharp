// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/utils/wsl.ts.
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Interactive.Mode.Utilities;

internal static partial class Wsl
{
    [GeneratedRegex("microsoft|wsl", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleasePattern();

    /// <summary>Windows Subsystem for Linux, where Windows executables are reachable through interop.
    /// <paramref name="readProcVersion"/> returns <c>/proc/version</c> or null when it cannot be read.</summary>
    public static bool IsWSL(Func<string, string?>? env = null, Func<string?>? readProcVersion = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (!string.IsNullOrEmpty(env("WSL_DISTRO_NAME")) || !string.IsNullOrEmpty(env("WSLENV"))) return true;
        var release = (readProcVersion ?? ReadProcVersion)();
        return release is not null && ReleasePattern().IsMatch(release);
    }

    public static string? ReadProcVersion()
    {
        try { return File.ReadAllText("/proc/version"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }
}
