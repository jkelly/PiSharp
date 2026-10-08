// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/config.ts (APP_NAME, CONFIG_DIR_NAME, ENV_AGENT_DIR,
// ENV_SESSION_DIR, getAgentDir, expandTildePath) and packages/coding-agent/src/utils/paths.ts (normalizePath, resolvePath,
// canonicalizePath, isLocalPath, normalizeWindowsShellPath).
using System.Reflection;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Pi;

/// <summary>Source config.ts constants. <see cref="AppName"/> keeps Pi's file and variable names (PiSharp shares Pi's agent
/// directory); <see cref="DisplayName"/> is the command name PiSharp prints in its help.</summary>
internal static class PiConfig
{
    internal const string AppName = "pi";
    internal const string DisplayName = "pisharp";
    internal const string ConfigDirName = ".pi";
    internal const string EnvAgentDir = "PI_CODING_AGENT_DIR";
    internal const string EnvSessionDir = "PI_CODING_AGENT_SESSION_DIR";

    /// <summary>The PiSharp product version (source VERSION is the package version).</summary>
    internal static string Version
    {
        get
        {
            var informational = typeof(PiConfig).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (informational is { Length: > 0 }) { var plus = informational.IndexOf('+'); return plus < 0 ? informational : informational[..plus]; }
            return typeof(PiConfig).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        }
    }
}

/// <summary>Source path helpers over an explicit home directory and platform.</summary>
internal static partial class PiPaths
{
    internal static bool Windows => OperatingSystem.IsWindows();
    internal static StringComparer Comparer => Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static StringComparison Comparison => Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [GeneratedRegex(@"^/(?:mnt/|cygdrive/)?([a-z])(?:/(.*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShellDrive();

    /// <summary>Source normalizeWindowsShellPath: Git Bash, MSYS, Cygwin and WSL drive paths to native Windows paths.</summary>
    internal static string NormalizeWindowsShellPath(string path)
    {
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\')) return path;
        var match = ShellDrive().Match(path);
        if (!match.Success) return path;
        var suffix = match.Groups[2].Success ? match.Groups[2].Value.Replace('/', '\\') : null;
        return match.Groups[1].Value.ToUpperInvariant() + ":\\" + (suffix ?? "");
    }

    /// <summary>Source normalizePath with its defaults (tilde expansion on; no trim, no @ stripping).</summary>
    internal static string NormalizePath(string input, string home, bool trim = false, bool stripAt = false)
    {
        var normalized = trim ? PiArgs.JsTrim(input) : input;
        if (stripAt && normalized.StartsWith('@')) normalized = normalized[1..];
        if (Windows) normalized = NormalizeWindowsShellPath(normalized);
        if (normalized == "~") return home;
        if (normalized.StartsWith("~/", StringComparison.Ordinal) || Windows && normalized.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Join(home, normalized[2..]);
        if (normalized.StartsWith("file://", StringComparison.Ordinal))
        {
            try { return new Uri(normalized).LocalPath; } catch (UriFormatException) { }
        }
        return normalized;
    }

    /// <summary>Source resolvePath: a normalized absolute input is resolved alone, a relative one against the base directory.</summary>
    internal static string ResolvePath(string input, string baseDirectory, string home, bool trim = false)
    {
        var normalized = NormalizePath(input, home, trim);
        var normalizedBase = NormalizePath(baseDirectory, home);
        return Path.IsPathRooted(normalized) && (!Windows || Path.IsPathFullyQualified(normalized) || normalized.StartsWith('\\'))
            ? Path.GetFullPath(normalized) : Path.GetFullPath(Path.Combine(normalizedBase, normalized));
    }

    /// <summary>Source canonicalizePath (realpath, else the input): symbolic links of every existing component are resolved.</summary>
    internal static string Canonicalize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? "";
            var current = root;
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Path.Combine(current, part);
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (!info.Exists) return path;
                // fs.realpathSync (the JavaScript implementation) resolves links only; other names keep their spelling.
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target) next = target.FullName;
                current = next;
            }
            return current;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return path; }
    }

    /// <summary>Source isLocalPath: not a package source, built-in extension or remote URL.</summary>
    internal static bool IsLocalPath(string value)
    {
        var trimmed = PiArgs.JsTrim(value);
        return !(trimmed.StartsWith("npm:", StringComparison.Ordinal) || trimmed.StartsWith("git:", StringComparison.Ordinal) ||
            trimmed.StartsWith("github:", StringComparison.Ordinal) || trimmed.StartsWith("http:", StringComparison.Ordinal) ||
            trimmed.StartsWith("https:", StringComparison.Ordinal) || trimmed.StartsWith("ssh:", StringComparison.Ordinal) ||
            trimmed.StartsWith("builtin:", StringComparison.Ordinal));
    }

    /// <summary>Source getAgentDir: <c>PI_CODING_AGENT_DIR</c> (tilde expanded), else <c>~/.pi/agent</c>.</summary>
    internal static string AgentDirectory(Func<string, string?> environment, string home)
    {
        var configured = environment(PiConfig.EnvAgentDir);
        return string.IsNullOrEmpty(configured) ? Path.Join(home, PiConfig.ConfigDirName, "agent") : NormalizePath(configured, home);
    }

    internal static bool Within(string root, string target) =>
        target.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, Comparison);
    internal static bool SameOrWithin(string root, string target) =>
        string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(target), Comparison) || Within(root, target);

    /// <summary>Strips one leading UTF-8 byte order mark (source stripBom).</summary>
    internal static string StripBom(string text) => text.Length > 0 && text[0] == '\ufeff' ? text[1..] : text;

    internal static string ReadText(string path) => StripBom(File.ReadAllText(path, new System.Text.UTF8Encoding(false, false)));
}
