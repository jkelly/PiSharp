// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/utils/git.ts.
// hosted-git-info 9.0.3 (ISC, npm) fromUrl, parse-url and the github/bitbucket/gitlab/gist/sourcehut host extractors are ported
// below (HostedGitInfo) since parseGitUrl depends on them; WHATWG URL parsing is a local subset (UrlRecord) with the parts
// hosted-git-info and parseGitUrl read: protocol, userinfo, hostname, port validation, pathname (dot segments, percent-encoding,
// special-scheme backslashes), hash and serialization.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Interactive.Mode.Utilities;

/// <summary>Source <c>GitSource</c>: <c>type</c> is always "git".</summary>
internal sealed record GitSource(string Repo, string Host, string Path, string? Ref, bool Pinned)
{
    public string Type => "git";
}

internal static partial class Git
{
    [GeneratedRegex("^git@([^:]+):(.+)$", RegexOptions.Singleline)]
    private static partial Regex ScpLike();

    [GeneratedRegex(@"^(https?|ssh|git)://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProtocolUrl();

    private static (string Repo, string? Ref) SplitRef(string url)
    {
        var scpLikeMatch = ScpLike().Match(url);
        if (scpLikeMatch.Success)
        {
            var pathWithMaybeRef = scpLikeMatch.Groups[2].Value;
            var refSeparator = pathWithMaybeRef.IndexOf('@', StringComparison.Ordinal);
            if (refSeparator < 0) return (url, null);
            var repoPath = pathWithMaybeRef[..refSeparator];
            var gitRef = pathWithMaybeRef[(refSeparator + 1)..];
            if (repoPath.Length == 0 || gitRef.Length == 0) return (url, null);
            return ($"git@{scpLikeMatch.Groups[1].Value}:{repoPath}", gitRef);
        }

        if (url.Contains("://", StringComparison.Ordinal))
        {
            var parsed = UrlRecord.Parse(url);
            if (parsed is null) return (url, null);
            var pathWithMaybeRef = parsed.Pathname.TrimStart('/');
            var refSeparator = pathWithMaybeRef.IndexOf('@', StringComparison.Ordinal);
            if (refSeparator < 0) return (url, null);
            var repoPath = pathWithMaybeRef[..refSeparator];
            var gitRef = pathWithMaybeRef[(refSeparator + 1)..];
            if (repoPath.Length == 0 || gitRef.Length == 0) return (url, null);
            parsed.SetPathname("/" + repoPath);
            var serialized = parsed.ToString();
            return (serialized.EndsWith('/') ? serialized[..^1] : serialized, gitRef);
        }

        var slashIndex = url.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex < 0) return (url, null);
        var host = url[..slashIndex];
        var rest = url[(slashIndex + 1)..];
        var separator = rest.IndexOf('@', StringComparison.Ordinal);
        if (separator < 0) return (url, null);
        var path = rest[..separator];
        var reference = rest[(separator + 1)..];
        if (path.Length == 0 || reference.Length == 0) return (url, null);
        return ($"{host}/{path}", reference);
    }

    private static bool HasUnsafeGitInstallPart(string value, bool allowSlash)
    {
        var decoded = Paths.TryDecodeURIComponent(value);
        if (decoded is null) return true;
        foreach (var candidate in new[] { value, decoded })
        {
            if (candidate.Contains('\0', StringComparison.Ordinal) || candidate.Contains('\\', StringComparison.Ordinal) || candidate.StartsWith('/')) return true;
            if (!allowSlash && candidate.Contains('/', StringComparison.Ordinal)) return true;
            if (candidate.Split('/').Contains("..")) return true;
        }
        return false;
    }

    private static GitSource? BuildGitSource(string repo, string host, string path, string? gitRef)
    {
        if (path.StartsWith('/')) return null;
        var normalizedPath = path.EndsWith(".git", StringComparison.Ordinal) ? path[..^4] : path;
        normalizedPath = normalizedPath.TrimStart('/');
        if (host.Length == 0 || normalizedPath.Length == 0 || normalizedPath.Split('/').Length < 2) return null;
        if (HasUnsafeGitInstallPart(host, false) || HasUnsafeGitInstallPart(normalizedPath, true)) return null;
        return new(repo, host, normalizedPath, gitRef, !string.IsNullOrEmpty(gitRef));
    }

    private static GitSource? ParseGenericGitUrl(string url)
    {
        var (repoWithoutRef, gitRef) = SplitRef(url);
        var repo = repoWithoutRef;
        string host;
        string path;
        var scpLikeMatch = ScpLike().Match(repoWithoutRef);
        if (scpLikeMatch.Success)
        {
            host = scpLikeMatch.Groups[1].Value;
            path = scpLikeMatch.Groups[2].Value;
        }
        else if (repoWithoutRef.StartsWith("https://", StringComparison.Ordinal) || repoWithoutRef.StartsWith("http://", StringComparison.Ordinal) ||
                 repoWithoutRef.StartsWith("ssh://", StringComparison.Ordinal) || repoWithoutRef.StartsWith("git://", StringComparison.Ordinal))
        {
            var parsed = UrlRecord.Parse(repoWithoutRef);
            if (parsed is null) return null;
            host = parsed.Hostname;
            path = parsed.Pathname.TrimStart('/');
        }
        else
        {
            var slashIndex = repoWithoutRef.IndexOf('/', StringComparison.Ordinal);
            if (slashIndex < 0) return null;
            host = repoWithoutRef[..slashIndex];
            path = repoWithoutRef[(slashIndex + 1)..];
            if (!host.Contains('.', StringComparison.Ordinal) && host != "localhost") return null;
            repo = $"https://{repoWithoutRef}";
        }
        return BuildGitSource(repo, host, path, gitRef);
    }

    /// <summary>Parse a git source. With the <c>git:</c> prefix every historical shorthand form is accepted; without it only
    /// explicit protocol URLs (https, http, ssh, git).</summary>
    public static GitSource? ParseGitUrl(string source)
    {
        var trimmed = PiSharp.Tui.Pi.TextUtils.JsTrim(source);
        var hasGitPrefix = trimmed.StartsWith("git:", StringComparison.Ordinal);
        var url = hasGitPrefix ? PiSharp.Tui.Pi.TextUtils.JsTrim(trimmed[4..]) : trimmed;
        if (!hasGitPrefix && !ProtocolUrl().IsMatch(url)) return null;

        var split = SplitRef(url);
        var hostedCandidates = new List<string>();
        if (split.Ref is not null) hostedCandidates.Add($"{split.Repo}#{split.Ref}");
        if (url.Length > 0) hostedCandidates.Add(url);
        foreach (var candidate in hostedCandidates)
        {
            var info = HostedGitInfo.FromUrl(candidate);
            if (info is null) continue;
            if (split.Ref is not null && info.Project.Contains('@', StringComparison.Ordinal)) continue;
            var useHttpsPrefix = !split.Repo.StartsWith("http://", StringComparison.Ordinal) && !split.Repo.StartsWith("https://", StringComparison.Ordinal) &&
                !split.Repo.StartsWith("ssh://", StringComparison.Ordinal) && !split.Repo.StartsWith("git://", StringComparison.Ordinal) &&
                !split.Repo.StartsWith("git@", StringComparison.Ordinal);
            return BuildGitSource(useHttpsPrefix ? $"https://{split.Repo}" : split.Repo, info.Domain, $"{info.User ?? "null"}/{info.Project}",
                NullIfEmpty(info.Committish) ?? NullIfEmpty(split.Ref));
        }

        var httpsCandidates = new List<string>();
        if (split.Ref is not null) httpsCandidates.Add($"https://{split.Repo}#{split.Ref}");
        httpsCandidates.Add($"https://{url}");
        foreach (var candidate in httpsCandidates)
        {
            var info = HostedGitInfo.FromUrl(candidate);
            if (info is null) continue;
            if (split.Ref is not null && info.Project.Contains('@', StringComparison.Ordinal)) continue;
            return BuildGitSource($"https://{split.Repo}", info.Domain, $"{info.User ?? "null"}/{info.Project}",
                NullIfEmpty(info.Committish) ?? NullIfEmpty(split.Ref));
        }

        return ParseGenericGitUrl(url);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>hosted-git-info <c>fromUrl</c> (the fields parseGitUrl reads).</summary>
internal static class HostedGitInfo
{
    internal sealed record Info(string Type, string Domain, string? User, string Project, string? Committish, string DefaultRepresentation);

    private sealed record Host(string Name, string Domain, string[] Protocols, Func<UrlRecord, (string? User, string Project, string? Committish)?> Extract);

    private static readonly Host[] Hosts =
    [
        new("github", "github.com", ["git:", "http:", "git+ssh:", "git+https:", "ssh:", "https:"], url =>
        {
            var parts = Split(url.Pathname, 5);
            string? user = At(parts, 1), project = At(parts, 2), type = At(parts, 3), committish = At(parts, 4);
            if (!string.IsNullOrEmpty(type) && type != "tree") return null;
            if (string.IsNullOrEmpty(type)) committish = url.Hash.Length > 0 ? url.Hash[1..] : "";
            if (project is not null && project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(project)) return null;
            return (user, project, committish);
        }),
        new("bitbucket", "bitbucket.org", ["git+ssh:", "git+https:", "ssh:", "https:"], url =>
        {
            var parts = Split(url.Pathname, 4);
            string? user = At(parts, 1), project = At(parts, 2), aux = At(parts, 3);
            if (aux == "get") return null;
            if (project is not null && project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(project)) return null;
            return (user, project, HashValue(url));
        }),
        new("gitlab", "gitlab.com", ["git+ssh:", "git+https:", "ssh:", "https:"], url =>
        {
            var path = url.Pathname.Length > 0 ? url.Pathname[1..] : "";
            if (path.Contains("/-/", StringComparison.Ordinal) || path.Contains("/archive.tar.gz", StringComparison.Ordinal)) return null;
            var segments = path.Split('/').ToList();
            var project = segments[^1];
            segments.RemoveAt(segments.Count - 1);
            if (project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
            var user = string.Join('/', segments);
            if (user.Length == 0 || project.Length == 0) return null;
            return (user, project, HashValue(url));
        }),
        new("gist", "gist.github.com", ["git:", "git+ssh:", "git+https:", "ssh:", "https:"], url =>
        {
            var parts = Split(url.Pathname, 4);
            string? user = At(parts, 1), project = At(parts, 2), aux = At(parts, 3);
            if (aux == "raw") return null;
            if (string.IsNullOrEmpty(project))
            {
                if (string.IsNullOrEmpty(user)) return null;
                project = user;
                user = null;
            }
            if (project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
            return (user, project, HashValue(url));
        }),
        new("sourcehut", "git.sr.ht", ["git+ssh:", "https:"], url =>
        {
            var parts = Split(url.Pathname, 4);
            string? user = At(parts, 1), project = At(parts, 2), aux = At(parts, 3);
            if (aux == "archive") return null;
            if (project is not null && project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(project)) return null;
            return (user, project, HashValue(url));
        }),
    ];

    /// <summary>The protocols map: the URL protocols plus one shortcut protocol per host.</summary>
    private static readonly HashSet<string> Protocols =
        ["git+ssh:", "ssh:", "git+https:", "git:", "http:", "https:", "git+http:", .. Hosts.Select(host => host.Name + ":")];

    private static readonly Dictionary<string, string> ProtocolNames = new(StringComparer.Ordinal)
    { ["git+ssh:"] = "sshurl", ["ssh:"] = "sshurl", ["git+https:"] = "https" };

    private static string[] Split(string value, int limit) => [.. value.Split('/').Take(limit)];
    private static string? At(string[] parts, int index) => index < parts.Length ? parts[index] : null;
    private static string HashValue(UrlRecord url) => url.Hash.Length > 0 ? url.Hash[1..] : "";

    /// <summary>Look for GitHub shorthand inputs such as <c>npm/cli</c>.</summary>
    private static bool IsGitHubShorthand(string arg)
    {
        var firstHash = arg.IndexOf('#', StringComparison.Ordinal);
        var firstSlash = arg.IndexOf('/', StringComparison.Ordinal);
        var secondSlash = arg.IndexOf('/', firstSlash + 1);
        var firstColon = arg.IndexOf(':', StringComparison.Ordinal);
        var firstSpace = -1;
        for (var index = 0; index < arg.Length; index++) if (PiSharp.Tui.Pi.TextUtils.IsJsWhitespace(arg[index])) { firstSpace = index; break; }
        var firstAt = arg.IndexOf('@', StringComparison.Ordinal);
        var spaceOnlyAfterHash = firstSpace < 0 || (firstHash > -1 && firstSpace > firstHash);
        var atOnlyAfterHash = firstAt == -1 || (firstHash > -1 && firstAt > firstHash);
        var colonOnlyAfterHash = firstColon == -1 || (firstHash > -1 && firstColon > firstHash);
        var secondSlashOnlyAfterHash = secondSlash == -1 || (firstHash > -1 && secondSlash > firstHash);
        var hasSlash = firstSlash > 0;
        var doesNotEndWithSlash = firstHash > -1 ? (firstHash == 0 || arg[firstHash - 1] != '/') : !arg.EndsWith('/');
        var doesNotStartWithDot = !arg.StartsWith('.');
        return spaceOnlyAfterHash && hasSlash && doesNotEndWithSlash && doesNotStartWithDot && atOnlyAfterHash && colonOnlyAfterHash &&
            secondSlashOnlyAfterHash;
    }

    private static int LastIndexOfBefore(string value, char c, char before)
    {
        if (value.Length == 0) return -1;
        var startPosition = value.IndexOf(before, StringComparison.Ordinal);
        return startPosition > -1 ? value.LastIndexOf(c, startPosition) : value.LastIndexOf(c);
    }

    /// <summary>Accepts input like <c>git:github.com:user/repo</c> and inserts the <c>//</c> after the first colon.</summary>
    private static string CorrectProtocol(string arg)
    {
        var firstColon = arg.IndexOf(':', StringComparison.Ordinal);
        var proto = arg[..(firstColon + 1)];
        if (Protocols.Contains(proto)) return arg;
        if (JsSubstr(arg, firstColon, 3) == "://") return arg;
        var firstAt = arg.IndexOf('@', StringComparison.Ordinal);
        if (firstAt > -1) return firstAt > firstColon ? $"git+ssh://{arg}" : arg;
        return $"{arg[..(firstColon + 1)]}//{arg[(firstColon + 1)..]}";
    }

    /// <summary>String.prototype.substr: a negative start counts from the end.</summary>
    private static string JsSubstr(string value, int start, int length)
    {
        if (start < 0) start = Math.Max(value.Length + start, 0);
        if (start >= value.Length) return "";
        return value.Substring(start, Math.Min(length, value.Length - start));
    }

    /// <summary>Attempt to correct an scp-style URL so that it parses as a URL.</summary>
    private static string CorrectUrl(string giturl)
    {
        var firstAt = LastIndexOfBefore(giturl, '@', '#');
        var lastColonBeforeHash = LastIndexOfBefore(giturl, ':', '#');
        if (lastColonBeforeHash > firstAt) giturl = giturl[..lastColonBeforeHash] + "/" + giturl[(lastColonBeforeHash + 1)..];
        if (LastIndexOfBefore(giturl, ':', '#') == -1 && !giturl.Contains("//", StringComparison.Ordinal)) giturl = $"git+ssh://{giturl}";
        return giturl;
    }

    private static UrlRecord? ParseUrl(string giturl)
    {
        var withProtocol = CorrectProtocol(giturl);
        return UrlRecord.Parse(withProtocol) ?? UrlRecord.Parse(CorrectUrl(withProtocol));
    }

    public static Info? FromUrl(string giturl)
    {
        if (string.IsNullOrEmpty(giturl)) return null;
        var correctedUrl = IsGitHubShorthand(giturl) ? $"github:{giturl}" : giturl;
        var parsed = ParseUrl(correctedUrl);
        if (parsed is null) return null;
        var shortcut = Hosts.FirstOrDefault(host => host.Name + ":" == parsed.Protocol);
        var hostname = parsed.Hostname.StartsWith("www.", StringComparison.Ordinal) ? parsed.Hostname[4..] : parsed.Hostname;
        var byDomain = Hosts.FirstOrDefault(host => host.Domain == hostname);
        var gitHost = shortcut ?? byDomain;
        if (gitHost is null) return null;
        try
        {
            if (shortcut is not null)
            {
                var pathname = parsed.Pathname.StartsWith('/') ? parsed.Pathname[1..] : parsed.Pathname;
                var firstAt = pathname.IndexOf('@', StringComparison.Ordinal);
                // Auth is ignored for shortcuts, so trim it out.
                if (firstAt > -1) pathname = pathname[(firstAt + 1)..];
                string? user = null;
                string project;
                var lastSlash = pathname.LastIndexOf('/');
                if (lastSlash > -1)
                {
                    user = Paths.DecodeURIComponent(pathname[..lastSlash]);
                    if (user.Length == 0) user = null;
                    project = Paths.DecodeURIComponent(pathname[(lastSlash + 1)..]);
                }
                else project = Paths.DecodeURIComponent(pathname);
                if (project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
                var committish = parsed.Hash.Length > 0 ? Paths.DecodeURIComponent(parsed.Hash[1..]) : null;
                return new(gitHost.Name, gitHost.Domain, user, project, committish, "shortcut");
            }
            if (!gitHost.Protocols.Contains(parsed.Protocol)) return null;
            if (gitHost.Extract(parsed) is not { } segments) return null;
            return new(gitHost.Name, gitHost.Domain,
                string.IsNullOrEmpty(segments.User) ? segments.User : Paths.DecodeURIComponent(segments.User),
                Paths.DecodeURIComponent(segments.Project),
                // decodeURIComponent(undefined) is the string "undefined" upstream.
                segments.Committish is null ? "undefined" : Paths.DecodeURIComponent(segments.Committish),
                ProtocolNames.TryGetValue(parsed.Protocol, out var name) ? name : parsed.Protocol[..^1]);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>A WHATWG URL subset: scheme, userinfo, host (special hosts lower-cased and IDNA-mapped, opaque hosts percent-encoded),
/// validated port, path (dot segments resolved, special backslashes), query and fragment, and serialization.</summary>
internal sealed class UrlRecord
{
    private static readonly Dictionary<string, int?> Special = new(StringComparer.Ordinal)
    { ["ftp"] = 21, ["file"] = null, ["http"] = 80, ["https"] = 443, ["ws"] = 80, ["wss"] = 443 };

    private string _scheme = "";
    private string? _host;
    private int? _port;
    private string _path = "";
    private bool _opaquePath;
    private string? _query;
    private string? _fragment;
    private string _username = "";
    private string _password = "";

    public string Protocol => _scheme + ":";
    public string Username => _username;
    public string Password => _password;
    public string Hostname => _host ?? "";
    public string Pathname => _path;
    public string Hash => string.IsNullOrEmpty(_fragment) ? "" : "#" + _fragment;
    private bool IsSpecial => Special.ContainsKey(_scheme);

    public static UrlRecord? Parse(string input)
    {
        var value = new string(input.Trim(Enumerable.Range(0, 0x21).Select(code => (char)code).ToArray()).Where(c => c is not ('\t' or '\n' or '\r')).ToArray());
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon < 1 || !char.IsAsciiLetter(value[0]) || !value[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')) return null;
        var url = new UrlRecord { _scheme = value[..colon].ToLowerInvariant() };
        var rest = value[(colon + 1)..];
        var hash = rest.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0) { url._fragment = Encode(rest[(hash + 1)..], FragmentSet); rest = rest[..hash]; }
        var question = rest.IndexOf('?', StringComparison.Ordinal);
        if (question >= 0) { url._query = Encode(rest[(question + 1)..], c => QuerySet(c) || (url.IsSpecial && c == '\'')); rest = rest[..question]; }
        if (url.IsSpecial)
        {
            if (url._scheme == "file")
            {
                if (rest.StartsWith("//", StringComparison.Ordinal) || rest.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    rest = rest[2..];
                    var end = rest.IndexOfAny(['/', '\\']);
                    var host = end < 0 ? rest : rest[..end];
                    rest = end < 0 ? "" : rest[end..];
                    if (!url.ParseAuthority(host, special: true, allowUserinfo: false)) return null;
                    if (url._host == "localhost") url._host = "";
                }
                else url._host = "";
            }
            else
            {
                rest = rest.TrimStart('/', '\\');
                var end = rest.IndexOfAny(['/', '\\']);
                var authority = end < 0 ? rest : rest[..end];
                rest = end < 0 ? "" : rest[end..];
                if (!url.ParseAuthority(authority, special: true, allowUserinfo: true) || url._host!.Length == 0) return null;
            }
            url._path = ParsePath(rest.Replace('\\', '/'), special: true);
        }
        else if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest[2..];
            var end = rest.IndexOf('/', StringComparison.Ordinal);
            var authority = end < 0 ? rest : rest[..end];
            rest = end < 0 ? "" : rest[end..];
            if (!url.ParseAuthority(authority, special: false, allowUserinfo: true)) return null;
            url._path = rest.Length == 0 ? "" : ParsePath(rest, special: false);
        }
        else if (rest.StartsWith('/'))
        {
            url._path = ParsePath(rest, special: false);
        }
        else
        {
            url._opaquePath = true;
            url._path = Encode(rest, C0ControlSet);
        }
        return url;
    }

    /// <summary>The <c>pathname</c> setter (ignored for opaque paths).</summary>
    public void SetPathname(string value)
    {
        if (_opaquePath) return;
        _path = ParsePath(IsSpecial ? value.Replace('\\', '/') : value, IsSpecial);
    }

    private static string ParsePath(string rest, bool special)
    {
        var segments = new List<string>();
        var parts = rest.Split('/');
        for (var index = rest.StartsWith('/') ? 1 : 0; index < parts.Length; index++)
        {
            var segment = parts[index];
            var last = index == parts.Length - 1;
            var lower = segment.ToLowerInvariant();
            if (lower is ".." or ".%2e" or "%2e." or "%2e%2e")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                if (last) segments.Add("");
            }
            else if (lower is "." or "%2e") { if (last) segments.Add(""); }
            else segments.Add(Encode(segment, PathSet));
        }
        return special || segments.Count > 0 || rest.Length > 0 ? "/" + string.Join('/', segments) : "";
    }

    private bool ParseAuthority(string authority, bool special, bool allowUserinfo)
    {
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            if (!allowUserinfo) return false;
            var userinfo = authority[..at];
            var separator = userinfo.IndexOf(':', StringComparison.Ordinal);
            _username = Encode(separator < 0 ? userinfo : userinfo[..separator], UserinfoSet);
            _password = separator < 0 ? "" : Encode(userinfo[(separator + 1)..], UserinfoSet);
            authority = authority[(at + 1)..];
        }
        string host;
        string? port = null;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0) return false;
            host = authority[..(close + 1)].ToLowerInvariant();
            var after = authority[(close + 1)..];
            if (after.Length != 0) { if (after[0] != ':') return false; port = after[1..]; }
            if (!host[1..^1].All(c => Uri.IsHexDigit(c) || c is ':' or '.')) return false;
        }
        else
        {
            var colon = authority.IndexOf(':', StringComparison.Ordinal);
            host = colon < 0 ? authority : authority[..colon];
            if (colon >= 0) port = authority[(colon + 1)..];
            if (special)
            {
                if (host.Length == 0) { if (_username.Length != 0 || _password.Length != 0 || port is not null) return false; }
                else if (DomainToAscii(host) is { } ascii) host = ascii;
                else return false;
            }
            else
            {
                if (host.Any(c => c is '\0' or '\t' or '\n' or '\r' or ' ' or '#' or '/' or ':' or '<' or '>' or '?' or '@' or '[' or '\\' or ']' or '^' or '|')) return false;
                host = Encode(host, C0ControlSet);
            }
        }
        if (!string.IsNullOrEmpty(port))
        {
            if (!port.All(char.IsAsciiDigit) || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number > 65535) return false;
            _port = Special.TryGetValue(_scheme, out var defaultPort) && defaultPort == number ? null : number;
        }
        _host = host;
        return true;
    }

    private static string? DomainToAscii(string host)
    {
        string? decoded;
        try { decoded = Uri.UnescapeDataString(host); } catch (UriFormatException) { return null; }
        string ascii;
        try { ascii = new IdnMapping { AllowUnassigned = true, UseStd3AsciiRules = false }.GetAscii(decoded).ToLowerInvariant(); }
        catch (ArgumentException) { if (decoded.All(char.IsAscii)) ascii = decoded.ToLowerInvariant(); else return null; }
        if (ascii.Any(c => c <= 0x20 || c is '#' or '%' or '/' or ':' or '<' or '>' or '?' or '@' or '[' or '\\' or ']' or '^' or '|' or '\u007f')) return null;
        return ascii;
    }

    public override string ToString()
    {
        var output = new StringBuilder(_scheme).Append(':');
        if (_host is not null)
        {
            output.Append("//");
            if (_username.Length != 0 || _password.Length != 0)
            {
                output.Append(_username);
                if (_password.Length != 0) output.Append(':').Append(_password);
                output.Append('@');
            }
            output.Append(_host);
            if (_port is { } port) output.Append(':').Append(port.ToString(CultureInfo.InvariantCulture));
        }
        else if (!_opaquePath && _path.StartsWith("//", StringComparison.Ordinal)) output.Append("/.");
        output.Append(_path);
        if (_query is not null) output.Append('?').Append(_query);
        if (_fragment is not null) output.Append('#').Append(_fragment);
        return output.ToString();
    }

    private static bool C0ControlSet(int c) => c < 0x20 || c > 0x7E;
    private static bool FragmentSet(int c) => C0ControlSet(c) || c is ' ' or '"' or '<' or '>' or '`';
    private static bool QuerySet(int c) => C0ControlSet(c) || c is ' ' or '"' or '#' or '<' or '>';
    private static bool PathSet(int c) => QuerySet(c) || c is '?' or '^' or '`' or '{' or '}';
    private static bool UserinfoSet(int c) => PathSet(c) || c is '/' or ':' or ';' or '=' or '@' or '[' or '\\' or ']' or '|';

    private static string Encode(string value, Func<int, bool> set)
    {
        var output = new StringBuilder(value.Length);
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            if (!set(rune.Value)) { output.Append((char)rune.Value); continue; }
            var length = rune.EncodeToUtf8(bytes);
            for (var index = 0; index < length; index++) output.Append('%').Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
        }
        return output.ToString();
    }
}
