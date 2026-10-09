// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/utils/changelog.ts (and config.ts getChangelogPath/getPackageDir).
// PiSharp's changelog is CHANGELOG.md next to the executable (AppContext.BaseDirectory), or under PI_PACKAGE_DIR when set.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Interactive.Mode.Utilities;

/// <summary>Source <c>ChangelogEntry</c>.</summary>
internal sealed record ChangelogEntry(int Major, int Minor, int Patch, string Content);

internal static partial class Changelog
{
    private const string GitHubRepo = "earendil-works/pi";
    private const string ChangelogLinkBasePath = "packages/coding-agent";

    [GeneratedRegex(@"^https://github\.com/(?:badlogic|earendil-works)/pi-mono(?=/|$)")]
    private static partial Regex LegacyRepo();

    [GeneratedRegex("^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlScheme();

    [GeneratedRegex(@"(!?\[[^\]\n]+\]\()([^\s)]+)((?:\s+[^)]*)?\))")]
    private static partial Regex InlineMarkdownLink();

    [GeneratedRegex(@"##\s+\[?([0-9]+)\.([0-9]+)\.([0-9]+)\]?")]
    private static partial Regex VersionHeader();

    private static string EntryVersion(ChangelogEntry entry) => $"{entry.Major}.{entry.Minor}.{entry.Patch}";

    private static string NormalizeTag(string version) => version.StartsWith('v') ? version : "v" + version;

    private static (string Fragment, string PathPart, string Query) SplitLocalTarget(string target)
    {
        var hashIndex = target.IndexOf('#', StringComparison.Ordinal);
        var beforeHash = hashIndex == -1 ? target : target[..hashIndex];
        var fragment = hashIndex == -1 ? "" : target[hashIndex..];
        var queryIndex = beforeHash.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex == -1) return (fragment, beforeHash, "");
        return (fragment, beforeHash[..queryIndex], beforeHash[queryIndex..]);
    }

    private static string? ResolveRepositoryPath(string targetPath)
    {
        var normalizedTarget = targetPath.Replace('\\', '/');
        var joined = normalizedTarget.StartsWith('/')
            ? PosixPath.Normalize(normalizedTarget.TrimStart('/'))
            : PosixPath.Normalize(PosixPath.Join(ChangelogLinkBasePath, normalizedTarget));
        if (joined == "." || joined.StartsWith("../", StringComparison.Ordinal) || joined == "..") return null;
        return joined;
    }

    private static bool IsDirectoryTarget(string originalPath, string repositoryPath) =>
        originalPath.EndsWith('/') || !PosixPath.Basename(repositoryPath).Contains('.', StringComparison.Ordinal);

    private static string NormalizeChangelogLinkTarget(string target, string tag)
    {
        var repoUrl = $"https://github.com/{GitHubRepo}";
        var canonicalTarget = LegacyRepo().Replace(target, repoUrl, 1);
        foreach (var route in new[] { "blob", "tree" })
        {
            foreach (var branch in new[] { "main", "master" })
            {
                var floatingRefPrefix = $"{repoUrl}/{route}/{branch}/";
                if (canonicalTarget.StartsWith(floatingRefPrefix, StringComparison.Ordinal))
                    canonicalTarget = $"{repoUrl}/{route}/{tag}/{canonicalTarget[floatingRefPrefix.Length..]}";
            }
        }
        if (canonicalTarget.StartsWith('#') || canonicalTarget.StartsWith("//", StringComparison.Ordinal) || UrlScheme().IsMatch(canonicalTarget))
            return canonicalTarget;
        var (fragment, pathPart, query) = SplitLocalTarget(canonicalTarget);
        if (pathPart.Length == 0) return canonicalTarget;
        var repositoryPath = ResolveRepositoryPath(pathPart);
        if (repositoryPath is null) return canonicalTarget;
        var routeName = IsDirectoryTarget(pathPart, repositoryPath) ? "tree" : "blob";
        return $"https://github.com/{GitHubRepo}/{routeName}/{tag}/{EncodeURI(repositoryPath)}{query}{fragment}";
    }

    public static string NormalizeChangelogLinks(string markdown, string version) => NormalizeLinks(markdown, NormalizeTag(version));

    public static string NormalizeChangelogLinks(string markdown, ChangelogEntry entry) => NormalizeLinks(markdown, NormalizeTag(EntryVersion(entry)));

    private static string NormalizeLinks(string markdown, string tag) =>
        InlineMarkdownLink().Replace(markdown, match =>
            match.Groups[1].Value + NormalizeChangelogLinkTarget(match.Groups[2].Value, tag) + match.Groups[3].Value);

    /// <summary>Parse changelog entries from CHANGELOG.md: scans for <c>## </c> lines and collects content until the next one or EOF.</summary>
    public static List<ChangelogEntry> ParseChangelog(string changelogPath, Action<string>? warn = null)
    {
        if (!File.Exists(changelogPath)) return [];
        try
        {
            var content = File.ReadAllText(changelogPath, new UTF8Encoding(false));
            var entries = new List<ChangelogEntry>();
            var currentLines = new List<string>();
            (int Major, int Minor, int Patch)? currentVersion = null;
            foreach (var line in content.Split('\n'))
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    if (currentVersion is { } previous && currentLines.Count > 0)
                        entries.Add(new(previous.Major, previous.Minor, previous.Patch, PiSharp.Tui.Pi.TextUtils.JsTrim(string.Join("\n", currentLines))));
                    var versionMatch = VersionHeader().Match(line);
                    if (versionMatch.Success)
                    {
                        currentVersion = (ParseInt(versionMatch.Groups[1].Value), ParseInt(versionMatch.Groups[2].Value), ParseInt(versionMatch.Groups[3].Value));
                        currentLines = [line];
                    }
                    else
                    {
                        currentVersion = null;
                        currentLines = [];
                    }
                }
                else if (currentVersion is not null)
                {
                    currentLines.Add(line);
                }
            }
            if (currentVersion is { } last && currentLines.Count > 0)
                entries.Add(new(last.Major, last.Minor, last.Patch, PiSharp.Tui.Pi.TextUtils.JsTrim(string.Join("\n", currentLines))));
            return entries;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            (warn ?? Console.Error.WriteLine)($"Warning: Could not parse changelog: {error.Message}");
            return [];
        }
    }

    private static int ParseInt(string digits) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : int.MaxValue;

    /// <summary>Compare versions: negative if v1 &lt; v2, 0 if equal, positive if v1 &gt; v2.</summary>
    public static int CompareVersions(ChangelogEntry v1, ChangelogEntry v2)
    {
        if (v1.Major != v2.Major) return v1.Major.CompareTo(v2.Major);
        if (v1.Minor != v2.Minor) return v1.Minor.CompareTo(v2.Minor);
        return v1.Patch.CompareTo(v2.Patch);
    }

    /// <summary>Entries newer than <paramref name="lastVersion"/> (missing or non-numeric parts count as 0).</summary>
    public static List<ChangelogEntry> GetNewEntries(IEnumerable<ChangelogEntry> entries, string lastVersion)
    {
        var parts = lastVersion.Split('.').Select(JsNumberToInt).ToArray();
        var last = new ChangelogEntry(parts.Length > 0 ? parts[0] : 0, parts.Length > 1 ? parts[1] : 0, parts.Length > 2 ? parts[2] : 0, "");
        return [.. entries.Where(entry => CompareVersions(entry, last) > 0)];
    }

    /// <summary><c>Number(part) || 0</c> for the integer parts of a version.</summary>
    private static int JsNumberToInt(string part)
    {
        var trimmed = PiSharp.Tui.Pi.TextUtils.JsTrim(part);
        if (trimmed.Length == 0) return 0;
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && !double.IsNaN(value) && !double.IsInfinity(value)
            ? (int)Math.Clamp(Math.Truncate(value), int.MinValue, int.MaxValue) : 0;
    }

    /// <summary>Source <c>getChangelogPath</c>: <c>CHANGELOG.md</c> in the package directory, which is <c>PI_PACKAGE_DIR</c>
    /// (normalized) when set, else the directory of the PiSharp executable.</summary>
    public static string GetChangelogPath(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var envDir = env("PI_PACKAGE_DIR");
        var packageDir = !string.IsNullOrEmpty(envDir) ? Paths.NormalizePath(envDir) : AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(packageDir, "CHANGELOG.md"));
    }

    /// <summary>JavaScript <c>encodeURI</c>: everything but the URI reserved and unreserved characters and <c>#</c> is UTF-8
    /// percent-encoded (lone surrogates are encoded as U+FFFD instead of throwing).</summary>
    internal static string EncodeURI(string value)
    {
        const string unescaped = ";,/?:@&=+$-_.!~*'()#";
        var output = new StringBuilder(value.Length);
        Span<byte> buffer = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.IsAscii && (char.IsAsciiLetterOrDigit((char)rune.Value) || unescaped.Contains((char)rune.Value, StringComparison.Ordinal)))
            {
                output.Append((char)rune.Value);
                continue;
            }
            var length = rune.EncodeToUtf8(buffer);
            for (var index = 0; index < length; index++) output.Append('%').Append(buffer[index].ToString("X2", CultureInfo.InvariantCulture));
        }
        return output.ToString();
    }
}

/// <summary>Node's <c>path.posix</c> normalize, join and basename.</summary>
internal static class PosixPath
{
    public static string Normalize(string path)
    {
        if (path.Length == 0) return ".";
        var isAbsolute = path.StartsWith('/');
        var trailingSeparator = path.EndsWith('/');
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0 && segments[^1] != "..") segments.RemoveAt(segments.Count - 1);
                else if (!isAbsolute) segments.Add("..");
                continue;
            }
            segments.Add(segment);
        }
        var normalized = string.Join('/', segments);
        if (normalized.Length == 0)
        {
            if (isAbsolute) return "/";
            return trailingSeparator ? "./" : ".";
        }
        if (trailingSeparator) normalized += "/";
        return isAbsolute ? "/" + normalized : normalized;
    }

    public static string Join(params string[] parts)
    {
        var joined = string.Join('/', parts.Where(part => part.Length > 0));
        return joined.Length == 0 ? "." : Normalize(joined);
    }

    public static string Basename(string path)
    {
        var end = path.Length;
        while (end > 0 && path[end - 1] == '/') end--;
        if (end == 0) return "";
        var start = path.LastIndexOf('/', end - 1) + 1;
        return path[start..end];
    }
}
