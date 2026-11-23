using System.Collections.Immutable;

namespace PiSharp.Tools.Processes;

/// <summary>Discovers PowerShell (<c>pwsh</c> 7+ first, falling back to Windows PowerShell 5.1 <c>powershell.exe</c>).</summary>
public static class PowerShellDiscovery
{
    /// <summary>Standard non-interactive PowerShell arguments so user profiles and execution policies never interfere.</summary>
    public static ImmutableArray<string> DefaultArguments { get; } =
        ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"];

    /// <summary>Forces UTF-8 stdout/stderr encoding regardless of the Windows console code page.</summary>
    public const string Utf8OutputPreamble = "try { [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 } catch {}";

    /// <summary>Constructs a <see cref="ShellConfiguration"/> for an explicit PowerShell executable path.</summary>
    public static ShellConfiguration ForPowerShell(string executable)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        return new(executable, DefaultArguments);
    }

    /// <summary>
    /// Resolves PowerShell:
    /// 1. Explicit <paramref name="customPowerShellPath"/> when provided.
    /// 2. On Windows: <c>pwsh.exe</c> on PATH, standard PowerShell 7 installation paths, <c>powershell.exe</c> on PATH,
    ///    then <c>%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe</c>.
    /// 3. On Unix: <c>pwsh</c> on PATH, then standard install paths (<c>/usr/bin/pwsh</c>, <c>/usr/local/bin/pwsh</c>, <c>/opt/microsoft/powershell/7/pwsh</c>).
    /// </summary>
    public static ShellConfiguration Resolve(string? customPowerShellPath = null, ShellHost? host = null)
    {
        host ??= ShellHost.Current;
        if (!string.IsNullOrEmpty(customPowerShellPath))
        {
            if (host.FileExists(customPowerShellPath) || host.DirectoryExists?.Invoke(customPowerShellPath) == true)
                return ForPowerShell(customPowerShellPath);
            throw new ShellDiscoveryException($"Custom PowerShell path not found: {customPowerShellPath}");
        }

        if (host.IsWindows)
        {
            if (ShellDiscovery.FindExecutableOnPath("pwsh.exe", host) is { } pwshOnPath)
                return ForPowerShell(pwshOnPath);

            var pwshCandidates = new List<string>();
            if (host.GetEnvironmentVariable("ProgramFiles") is { Length: > 0 } programFiles)
                pwshCandidates.Add($"{programFiles.TrimEnd('\\', '/')}\\PowerShell\\7\\pwsh.exe");
            if (host.GetEnvironmentVariable("ProgramFiles(x86)") is { Length: > 0 } programFilesX86)
                pwshCandidates.Add($"{programFilesX86.TrimEnd('\\', '/')}\\PowerShell\\7\\pwsh.exe");
            foreach (var candidate in pwshCandidates)
                if (host.FileExists(candidate)) return ForPowerShell(candidate);

            if (ShellDiscovery.FindExecutableOnPath("powershell.exe", host) is { } winPsOnPath)
                return ForPowerShell(winPsOnPath);

            var systemRoot = host.GetEnvironmentVariable("SystemRoot");
            var inbox = !string.IsNullOrEmpty(systemRoot)
                ? $"{systemRoot.TrimEnd('\\', '/')}\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"
                : @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
            if (host.FileExists(inbox)) return ForPowerShell(inbox);

            throw new ShellDiscoveryException(
                "No PowerShell executable found. Install PowerShell 7 (pwsh) or ensure Windows PowerShell (powershell.exe) is available on PATH.");
        }

        if (ShellDiscovery.FindExecutableOnPath("pwsh", host) is { } unixPwsh)
            return ForPowerShell(unixPwsh);
        foreach (var candidate in new[] { "/usr/bin/pwsh", "/usr/local/bin/pwsh", "/opt/microsoft/powershell/7/pwsh" })
            if (host.FileExists(candidate)) return ForPowerShell(candidate);

        throw new ShellDiscoveryException("No PowerShell executable (pwsh) found on PATH.");
    }
}
