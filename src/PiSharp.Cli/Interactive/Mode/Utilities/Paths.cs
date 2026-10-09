// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/utils/paths.ts (canonicalizePath, isLocalPath,
// normalizeWindowsShellPath, normalizePath, resolvePath, getCwdRelativePath, formatPathRelativeToCwdOrAbsolute).
// Reuses PiSharp.Cli.Pi.PiPaths for the shell-path regex, realpath walk and isLocalPath; normalizePath/resolvePath are ported here
// with the source's options and with fileURLToPath throwing on invalid file URLs, as Node does.
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Interactive.Mode.Utilities;

/// <summary>Source <c>PathInputOptions</c>.</summary>
internal sealed record PathInputOptions(bool Trim = false, bool ExpandTilde = true, string? HomeDir = null, bool StripAtPrefix = false,
    bool NormalizeUnicodeSpaces = false);

internal static partial class Paths
{
    [GeneratedRegex("[  -   　]")]
    private static partial Regex UnicodeSpaces();

    private static bool Windows => OperatingSystem.IsWindows();

    /// <summary>Resolve a path to its canonical (real) form, following symlinks; the raw path when resolution fails (a missing
    /// target or a dangling link).</summary>
    public static string CanonicalizePath(string path)
    {
        var canonical = PiPaths.Canonicalize(path);
        if (ReferenceEquals(canonical, path) || canonical == path) return path;
        return File.Exists(canonical) || Directory.Exists(canonical) ? canonical : path;
    }

    /// <summary>True unless the value is a package source (npm:, git:, ...), a built-in extension or a remote URL.</summary>
    public static bool IsLocalPath(string value) => PiPaths.IsLocalPath(value);

    /// <summary>Convert Git Bash, MSYS, Cygwin, and WSL drive paths to a form native Windows APIs accept.</summary>
    public static string NormalizeWindowsShellPath(string filePath) => PiPaths.NormalizeWindowsShellPath(filePath);

    public static string NormalizePath(string input, PathInputOptions? options = null)
    {
        options ??= new();
        var normalized = options.Trim ? PiSharp.Tui.Pi.TextUtils.JsTrim(input) : input;
        if (options.NormalizeUnicodeSpaces) normalized = UnicodeSpaces().Replace(normalized, " ");
        if (options.StripAtPrefix && normalized.StartsWith('@')) normalized = normalized[1..];
        if (Windows) normalized = NormalizeWindowsShellPath(normalized);
        if (options.ExpandTilde)
        {
            var home = options.HomeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (normalized == "~") return home;
            if (normalized.StartsWith("~/", StringComparison.Ordinal) || (Windows && normalized.StartsWith("~\\", StringComparison.Ordinal)))
                return NodeJoin(home, normalized[2..]);
        }
        if (normalized.StartsWith("file://", StringComparison.Ordinal)) return FileUrlToPath(normalized);
        return normalized;
    }

    public static string ResolvePath(string input, string? baseDir = null, PathInputOptions? options = null)
    {
        var normalized = NormalizePath(input, options);
        var normalizedBaseDir = NormalizePath(baseDir ?? Directory.GetCurrentDirectory());
        return IsAbsolute(normalized) ? NodeResolve(normalized) : NodeResolve(Path.Combine(NodeResolve(normalizedBaseDir), normalized));
    }

    public static string? GetCwdRelativePath(string filePath, string cwd)
    {
        var resolvedCwd = ResolvePath(cwd);
        var resolvedPath = ResolvePath(filePath, resolvedCwd);
        var relativePath = Path.GetRelativePath(resolvedCwd, resolvedPath);
        if (relativePath == ".") relativePath = "";
        var sep = Path.DirectorySeparatorChar.ToString();
        var isInsideCwd = relativePath.Length == 0 ||
            (relativePath != ".." && !relativePath.StartsWith(".." + sep, StringComparison.Ordinal) && !IsAbsolute(relativePath));
        return isInsideCwd ? (relativePath.Length == 0 ? "." : relativePath) : null;
    }

    public static string FormatPathRelativeToCwdOrAbsolute(string filePath, string cwd)
    {
        var absolutePath = ResolvePath(filePath, cwd);
        return (GetCwdRelativePath(absolutePath, cwd) ?? absolutePath).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Node <c>path.isAbsolute</c>: on Windows a leading separator or a drive followed by a separator.</summary>
    private static bool IsAbsolute(string path)
    {
        if (!Windows) return path.StartsWith('/');
        if (path.Length == 0) return false;
        if (path[0] is '/' or '\\') return true;
        return path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\';
    }

    /// <summary>Node <c>path.resolve</c> of one absolute path: normalized, without a trailing separator except at the root.</summary>
    private static string NodeResolve(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Node <c>path.join</c> for an absolute left side: joined and normalized (dot segments, separators).</summary>
    private static string NodeJoin(string left, string right)
    {
        var joined = Path.Join(left, right);
        return IsAbsolute(left) ? Path.GetFullPath(joined) : joined;
    }

    /// <summary>Node <c>url.fileURLToPath</c> for <c>file://</c> strings: a host is only accepted on Windows (UNC), encoded
    /// separators and malformed percent escapes throw, and Windows paths need a drive letter.</summary>
    internal static string FileUrlToPath(string url)
    {
        var rest = url["file://".Length..];
        var end = rest.IndexOfAny(['?', '#']);
        if (end >= 0) rest = rest[..end];
        if (Windows) rest = rest.Replace('\\', '/');
        var slash = rest.IndexOf('/');
        var host = slash < 0 ? rest : rest[..slash];
        var pathname = slash < 0 ? "/" : rest[slash..];
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) host = "";
        if (Windows)
        {
            if (pathname.Contains("%2f", StringComparison.OrdinalIgnoreCase) || pathname.Contains("%5c", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("File URL path must not include encoded \\ or / characters");
            var decoded = DecodeURIComponent(pathname.Replace('/', '\\'));
            if (host.Length > 0) return $"\\\\{host}{decoded}";
            if (decoded.Length < 3 || !char.IsAsciiLetter(decoded[1]) || decoded[2] != ':')
                throw new ArgumentException("File URL path must be absolute");
            return decoded[1..];
        }
        if (host.Length > 0) throw new ArgumentException($"File URL host must be \"localhost\" or empty on {ClipboardEnvironment.CurrentPlatform}");
        if (pathname.Contains("%2f", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("File URL path must not include encoded / characters");
        return DecodeURIComponent(pathname);
    }

    /// <summary>JavaScript <c>decodeURIComponent</c>: throws <see cref="FormatException"/> (URIError) on malformed escapes or
    /// invalid UTF-8.</summary>
    internal static string DecodeURIComponent(string value)
    {
        if (!value.Contains('%', StringComparison.Ordinal)) return value;
        var output = new StringBuilder(value.Length);
        var strict = new UTF8Encoding(false, true);
        var bytes = new List<byte>();
        for (var index = 0; index < value.Length;)
        {
            if (value[index] != '%') { output.Append(value[index]); index++; continue; }
            bytes.Clear();
            while (index < value.Length && value[index] == '%')
            {
                if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
                    throw new FormatException("URI malformed");
                bytes.Add(Convert.ToByte(value.Substring(index + 1, 2), 16));
                index += 3;
            }
            try { output.Append(strict.GetString([.. bytes])); }
            catch (DecoderFallbackException) { throw new FormatException("URI malformed"); }
        }
        return output.ToString();
    }

    /// <summary><c>decodeURIComponent</c> or null on a URIError.</summary>
    internal static string? TryDecodeURIComponent(string value)
    {
        try { return DecodeURIComponent(value); } catch (FormatException) { return null; }
    }
}
