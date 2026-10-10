// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/shell.ts (getShellConfig, getShellEnv)
// and utils/paths.ts (normalizePath for the shellPath setting).
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace PiSharp.Tools.Processes;

/// <summary>Source ShellConfig.commandTransport: the command as the last argument, or written to standard input.</summary>
public enum ShellCommandTransport { Argv, Stdin }

/// <summary>Source ShellConfig: the shell, its leading arguments and how the command reaches it.</summary>
public sealed record ShellConfiguration(string Shell, ImmutableArray<string> Arguments,
    ShellCommandTransport CommandTransport = ShellCommandTransport.Argv)
{
    /// <summary>The full argv after the shell for one command (source spawn arguments).</summary>
    public ImmutableArray<string> CommandArguments(string command) =>
        CommandTransport == ShellCommandTransport.Stdin ? Arguments : Arguments.Add(command);
}

/// <summary>Source getShellConfig failures ("Custom shell path not found", "No bash shell found").</summary>
public sealed class ShellDiscoveryException(string message) : Exception(message);

/// <summary>Host facts shell discovery reads. <see cref="Current"/> is the running process; tests supply their own.</summary>
public sealed record ShellHost(bool IsWindows, Func<string, string?> GetEnvironmentVariable, Func<string, bool> FileExists,
    string HomeDirectory)
{
    public static ShellHost Current { get; } = new(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable, File.Exists,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Source existsSync also accepts a directory as the custom shell path (spawn then refuses it). Null (the default, also for
    /// <see cref="Current"/>): a directory is not found; the pi policy then retries with it set and spawns the directory, as upstream does.</summary>
    public Func<string, bool>? DirectoryExists { get; init; }
}

/// <summary>Source shell resolution, shell environment and the shellPath setting normalization.</summary>
public static class ShellDiscovery
{
    private static readonly Regex LegacyWsl = new(@"^[a-z]:\\windows\\(?:system32|sysnative)\\bash\.exe$", RegexOptions.CultureInvariant);

    /// <summary>Source isLegacyWslBashPath: the inbox WSL launcher, which receives the command over standard input.</summary>
    public static bool IsLegacyWslBashPath(string path) => LegacyWsl.IsMatch(path.Replace('/', '\\').ToLowerInvariant());

    /// <summary>Source getBashShellConfig: <c>-s</c> over standard input for legacy WSL bash, otherwise <c>-c</c>.</summary>
    public static ShellConfiguration ForBash(string shell) => IsLegacyWslBashPath(shell)
        ? new(shell, ["-s"], ShellCommandTransport.Stdin) : new(shell, ["-c"]);

    /// <summary>
    /// Source getShellConfig: 1. the configured shellPath, 2. on Windows Git Bash under ProgramFiles and
    /// ProgramFiles(x86), then bash.exe on PATH, 3. on Unix /bin/bash, bash on PATH, then sh.
    /// </summary>
    public static ShellConfiguration Resolve(string? customShellPath = null, ShellHost? host = null)
    {
        host ??= ShellHost.Current;
        if (!string.IsNullOrEmpty(customShellPath))
        {
            if (host.FileExists(customShellPath) || host.DirectoryExists?.Invoke(customShellPath) == true) return ForBash(customShellPath);
            throw new ShellDiscoveryException($"Custom shell path not found: {customShellPath}");
        }
        if (host.IsWindows)
        {
            var paths = new List<string>();
            if (host.GetEnvironmentVariable("ProgramFiles") is { Length: > 0 } programFiles) paths.Add($"{programFiles}\\Git\\bin\\bash.exe");
            if (host.GetEnvironmentVariable("ProgramFiles(x86)") is { Length: > 0 } programFilesX86) paths.Add($"{programFilesX86}\\Git\\bin\\bash.exe");
            foreach (var path in paths) if (host.FileExists(path)) return ForBash(path);
            if (FindExecutableOnPath("bash.exe", host) is { } onPath) return ForBash(onPath);
            throw new ShellDiscoveryException("No bash shell found. Options:\n" +
                "  1. Install Git for Windows: https://git-scm.com/download/win\n" +
                "  2. Add your bash to PATH (Cygwin, MSYS2, etc.)\n" +
                "  3. Set shellPath in settings.json\n\n" +
                "Searched Git Bash in:\n" + string.Join("\n", paths.Select(path => "  " + path)));
        }
        if (host.FileExists("/bin/bash")) return ForBash("/bin/bash");
        if (FindExecutableOnPath("bash", host) is { } bash) return ForBash(bash);
        // Source { shell: "sh" } relies on spawn's PATH lookup; native launches need the absolute file.
        return new(FindExecutableOnPath("sh", host) ?? "/bin/sh", ["-c"]);
    }

    /// <summary>
    /// Source findExecutableOnPath (<c>where</c> on Windows, <c>which</c> on Unix): the first PATH entry holding the file.
    /// Unlike <c>where</c>, the current directory is not searched first, so a workspace file cannot shadow the shell.
    /// </summary>
    public static string? FindExecutableOnPath(string executable, ShellHost? host = null)
    {
        host ??= ShellHost.Current;
        var path = host.GetEnvironmentVariable("PATH") ?? host.GetEnvironmentVariable("Path");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var entry in path.Split(host.IsWindows ? ';' : ':'))
        {
            var directory = entry.Trim().Trim('"');
            // Joined with the host's own separator rules, so discovery is the same whichever OS evaluates it.
            if (host.IsWindows ? !Regex.IsMatch(directory, @"^(?:[A-Za-z]:[\\/]|\\\\[^\\/])", RegexOptions.CultureInvariant) : !directory.StartsWith('/')) continue;
            var candidate = directory.TrimEnd(host.IsWindows ? ['\\', '/'] : ['/']) + (host.IsWindows ? "\\" : "/") + executable;
            if (host.FileExists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Source SettingsManager.getShellPath: normalizePath expands a leading ~ and, on Windows, MSYS/Cygwin/WSL drive paths.</summary>
    public static string NormalizeShellPath(string path, ShellHost? host = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        host ??= ShellHost.Current;
        var normalized = path;
        if (host.IsWindows && normalized.StartsWith('/') && !normalized.StartsWith("//", StringComparison.Ordinal) && !normalized.Contains('\\'))
        {
            var drive = Regex.Match(normalized, "^/(?:mnt/|cygdrive/)?([a-z])(?:/(.*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (drive.Success) normalized = char.ToUpperInvariant(drive.Groups[1].Value[0]) + ":\\" + drive.Groups[2].Value.Replace('/', '\\');
        }
        if (normalized == "~") return host.HomeDirectory;
        if (normalized.StartsWith("~/", StringComparison.Ordinal) || host.IsWindows && normalized.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Join(host.HomeDirectory, normalized[2..]);
        if (normalized.StartsWith("file://", StringComparison.Ordinal) && Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.IsFile)
            return uri.LocalPath;
        return normalized;
    }

    /// <summary>
    /// Source getShellEnv: the given environment with Pi's bin directory (<c>$PI_CODING_AGENT_DIR/bin</c>, default
    /// <c>~/.pi/agent/bin</c>) prepended to PATH unless already present. The PATH key keeps its existing spelling.
    /// </summary>
    public static ImmutableDictionary<string, string> ShellEnvironment(IReadOnlyDictionary<string, string> environment, string binDirectory,
        bool windows)
    {
        ArgumentNullException.ThrowIfNull(environment); ArgumentException.ThrowIfNullOrEmpty(binDirectory);
        var key = environment.Keys.FirstOrDefault(name => name.Equals("path", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
        var current = environment.TryGetValue(key, out var value) ? value : "";
        var delimiter = windows ? ';' : ':';
        var entries = current.Split(delimiter).Where(entry => entry.Length != 0);
        var updated = entries.Contains(binDirectory, StringComparer.Ordinal) ? current
            : string.Join(delimiter, new[] { binDirectory, current }.Where(part => part.Length != 0));
        return environment.ToImmutableDictionary(StringComparer.Ordinal).SetItem(key, updated);
    }

    /// <summary>Source getBinDir: <c>$PI_CODING_AGENT_DIR/bin</c> (with ~ expansion), else <c>~/.pi/agent/bin</c>.</summary>
    public static string BinDirectory(ShellHost? host = null)
    {
        host ??= ShellHost.Current;
        var configured = host.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        var agent = string.IsNullOrEmpty(configured) ? Path.Join(host.HomeDirectory, ".pi", "agent") : NormalizeShellPath(configured, host);
        return Path.Join(agent, "bin");
    }
}
