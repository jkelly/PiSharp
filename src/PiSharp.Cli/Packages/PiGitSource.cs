// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/git.ts (GitSource, splitRef,
// hasUnsafeGitInstallPart, buildGitSource, parseGenericGitUrl, parseGitUrl) and the hosted-git-info 9 fromUrl it calls
// (lib/from-url.js, lib/parse-url.js, lib/hosts.js), over a WHATWG URL subset.
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Packages;

/// <summary>Source GitSource: the clone URL without a ref, the host and repository path used for the install path, and the ref.</summary>
internal sealed record PiGitSource(string Repo, string Host, string Path, string? Ref) : PiPackageSource
{
    internal bool Pinned => Ref is not null;
}

/// <summary>The WHATWG URL fields hosted-git-info and git.ts read (protocol, credentials, hostname, pathname, hash) and the
/// serialization <c>URL.toString()</c> produces. IDNA and IPv4 normalization are not performed.</summary>
internal sealed class PiUrl
{
    private static readonly HashSet<string> SpecialSchemes = ["http:", "https:", "ws:", "wss:", "ftp:", "file:"];
    private static readonly Dictionary<string, string> DefaultPorts = new(StringComparer.Ordinal) { ["http:"] = "80", ["https:"] = "443", ["ws:"] = "80", ["wss:"] = "443", ["ftp:"] = "21" };
    internal string Protocol { get; private init; } = "";
    internal string Username { get; private init; } = "";
    internal string Password { get; private init; } = "";
    internal string Hostname { get; private init; } = "";
    internal string Port { get; private init; } = "";
    internal string Pathname { get; set; } = "";
    internal string? Query { get; private init; }
    internal string? Fragment { get; private init; }
    internal bool HasAuthority { get; private init; }
    internal string Hash => string.IsNullOrEmpty(Fragment) ? "" : "#" + Fragment;

    public override string ToString()
    {
        var builder = new StringBuilder(Protocol);
        if (HasAuthority)
        {
            builder.Append("//");
            if (Username.Length > 0 || Password.Length > 0) builder.Append(Username).Append(Password.Length > 0 ? ":" + Password : "").Append('@');
            builder.Append(Hostname);
            if (Port.Length > 0) builder.Append(':').Append(Port);
        }
        builder.Append(Pathname);
        if (Query is not null) builder.Append('?').Append(Query);
        if (Fragment is not null) builder.Append('#').Append(Fragment);
        return builder.ToString();
    }

    /// <summary><c>new URL(input)</c>, or null where it throws.</summary>
    internal static PiUrl? Parse(string input)
    {
        var text = input.Trim('\u0000', '\u0001', '\u0002', '\u0003', '\u0004', '\u0005', '\u0006', '\u0007', '\u0008', '\t', '\n', '\u000b', '\u000c', '\r',
            '\u000e', '\u000f', '\u0010', '\u0011', '\u0012', '\u0013', '\u0014', '\u0015', '\u0016', '\u0017', '\u0018', '\u0019', '\u001a', '\u001b', '\u001c',
            '\u001d', '\u001e', '\u001f', ' ').Replace("\t", "").Replace("\n", "").Replace("\r", "");
        var colon = text.IndexOf(':');
        if (colon <= 0 || !char.IsAsciiLetter(text[0]) || !text[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')) return null;
        var protocol = text[..(colon + 1)].ToLowerInvariant();
        var special = SpecialSchemes.Contains(protocol);
        var rest = text[(colon + 1)..];
        string? fragment = null, query = null;
        var hashIndex = rest.IndexOf('#');
        if (hashIndex >= 0) { fragment = EncodeSet(rest[(hashIndex + 1)..], " \"<>`"); rest = rest[..hashIndex]; }
        var queryIndex = rest.IndexOf('?');
        if (queryIndex >= 0) { query = EncodeSet(rest[(queryIndex + 1)..], special ? " \"#<>'" : " \"#<>"); rest = rest[..queryIndex]; }
        if (special) rest = rest.Replace('\\', '/');
        string username = "", password = "", hostname = "", port = "";
        var hasAuthority = false;
        if (special && protocol != "file:" || rest.StartsWith("//", StringComparison.Ordinal))
        {
            hasAuthority = true;
            rest = special ? rest.TrimStart('/') : rest[2..];
            var slash = rest.IndexOf('/');
            var authority = slash < 0 ? rest : rest[..slash];
            rest = slash < 0 ? "" : rest[slash..];
            var at = authority.LastIndexOf('@');
            if (at >= 0)
            {
                var userInfo = authority[..at]; authority = authority[(at + 1)..];
                var separator = userInfo.IndexOf(':');
                username = EncodeSet(separator < 0 ? userInfo : userInfo[..separator], " \"#<>?`{}/:;=@[\\]^|");
                password = separator < 0 ? "" : EncodeSet(userInfo[(separator + 1)..], " \"#<>?`{}/:;=@[\\]^|");
            }
            var portIndex = authority.StartsWith('[') ? authority.IndexOf(':', Math.Max(0, authority.IndexOf(']'))) : authority.LastIndexOf(':');
            if (portIndex >= 0)
            {
                port = authority[(portIndex + 1)..]; authority = authority[..portIndex];
                if (!port.All(char.IsAsciiDigit)) return null;
                if (port.Length > 0)
                {
                    if (!int.TryParse(port.TrimStart('0').PadLeft(1, '0'), out var number) || number > 65535) return null;
                    port = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (DefaultPorts.TryGetValue(protocol, out var defaultPort) && defaultPort == port) port = "";
                }
            }
            if (special)
            {
                string decoded;
                try { decoded = PiUri.DecodeComponent(authority); } catch (FormatException) { return null; }
                if (decoded.Any(c => c < 0x20 || c == 0x7f || " #%/:<>?@[\\]^|".Contains(c))) return null;
                hostname = decoded.ToLowerInvariant();
                if (hostname.Length == 0 && protocol != "file:") return null;
            }
            else
            {
                if (authority.Any(c => c < 0x20 || c == 0x7f || " #/:<>?@[\\]^|".Contains(c))) return null;
                hostname = EncodeSet(authority, "");
                if (hostname.Length == 0 && (username.Length > 0 || password.Length > 0 || port.Length > 0)) return null;
            }
        }
        string pathname;
        if (hasAuthority || special || rest.StartsWith('/'))
        {
            var segments = new List<string>();
            var parts = rest.Split('/');
            for (var index = rest.StartsWith('/') ? 1 : 0; index < parts.Length; index++)
            {
                var segment = parts[index]; var last = index == parts.Length - 1;
                var lower = segment.ToLowerInvariant();
                if (lower is ".." or ".%2e" or "%2e." or "%2e%2e") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); if (last) segments.Add(""); continue; }
                if (lower is "." or "%2e") { if (last) segments.Add(""); continue; }
                segments.Add(EncodeSet(segment, " \"#<>?`{}"));
            }
            pathname = segments.Count == 0 ? (special || rest.Length > 0 ? "/" : "") : "/" + string.Join('/', segments);
        }
        else pathname = EncodeSet(rest, "");
        return new() { Protocol = protocol, Username = username, Password = password, Hostname = hostname, Port = port, Pathname = pathname, Query = query, Fragment = fragment, HasAuthority = hasAuthority };
    }

    private static string EncodeSet(string value, string set)
    {
        var builder = new StringBuilder();
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value < 0x20 || rune.Value > 0x7e || rune.IsAscii && set.Contains((char)rune.Value))
            {
                var count = rune.EncodeToUtf8(bytes);
                for (var index = 0; index < count; index++) builder.Append('%').Append(bytes[index].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
            else builder.Append(rune.ToString());
        }
        return builder.ToString();
    }
}

internal static class PiUri
{
    /// <summary>JavaScript <c>decodeURIComponent</c>: throws <see cref="FormatException"/> (URIError) for malformed escapes or UTF-8.</summary>
    internal static string DecodeComponent(string value)
    {
        if (!value.Contains('%')) return value;
        var builder = new StringBuilder();
        var bytes = new List<byte>();
        var strict = new UTF8Encoding(false, true);
        for (var index = 0; index < value.Length;)
        {
            if (value[index] != '%') { builder.Append(value[index]); index++; continue; }
            bytes.Clear();
            while (index < value.Length && value[index] == '%')
            {
                if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2])) throw new FormatException("URI malformed");
                bytes.Add(Convert.ToByte(value.Substring(index + 1, 2), 16));
                index += 3;
            }
            try { builder.Append(strict.GetString([.. bytes])); }
            catch (DecoderFallbackException) { throw new FormatException("URI malformed"); }
        }
        return builder.ToString();
    }
}

/// <summary>hosted-git-info 9 <c>fromUrl</c> for the fields git.ts reads: the host's domain, user, project and committish.</summary>
internal static class PiHostedGitInfo
{
    internal sealed record Info(string Domain, string? User, string Project, string? Committish);
    private sealed record Host(string Name, string Domain, string[] Protocols, Func<PiUrl, (string? User, string Project, string? Committish)?> Extract);

    private static readonly Host[] Hosts =
    [
        new("github", "github.com", ["git:", "http:", "git+ssh:", "git+https:", "ssh:", "https:"], url =>
        {
            var parts = SplitLimit(url.Pathname, 5);
            string? user = At(parts, 1), project = At(parts, 2), type = At(parts, 3), committish = At(parts, 4);
            if (type is { Length: > 0 } && type != "tree") return null;
            if (string.IsNullOrEmpty(type)) committish = url.Hash.Length > 0 ? url.Hash[1..] : "";
            if (project is not null && project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(project)) return null;
            return (user, project, committish);
        }),
        new("bitbucket", "bitbucket.org", ["git+ssh:", "git+https:", "ssh:", "https:"], url => UserProject(url, aux => aux == "get")),
        new("gitlab", "gitlab.com", ["git+ssh:", "git+https:", "ssh:", "https:"], url =>
        {
            var path = url.Pathname.Length > 0 ? url.Pathname[1..] : "";
            if (path.Contains("/-/", StringComparison.Ordinal) || path.Contains("/archive.tar.gz", StringComparison.Ordinal)) return null;
            var segments = path.Split('/').ToList();
            var project = segments[^1]; segments.RemoveAt(segments.Count - 1);
            if (project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
            var user = string.Join('/', segments);
            if (user.Length == 0 || project.Length == 0) return null;
            return (user, project, url.Hash.Length > 0 ? url.Hash[1..] : "");
        }),
        new("gist", "gist.github.com", ["git:", "git+ssh:", "git+https:", "ssh:", "https:"], url =>
        {
            var parts = SplitLimit(url.Pathname, 4);
            string? user = At(parts, 1), project = At(parts, 2), aux = At(parts, 3);
            if (aux == "raw") return null;
            if (string.IsNullOrEmpty(project)) { if (string.IsNullOrEmpty(user)) return null; project = user; user = null; }
            if (project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
            return (user, project, url.Hash.Length > 0 ? url.Hash[1..] : "");
        }),
        new("sourcehut", "git.sr.ht", ["git+ssh:", "https:"], url => UserProject(url, aux => aux == "archive")),
    ];

    private static readonly HashSet<string> Protocols = ["git+ssh:", "ssh:", "git+https:", "git:", "http:", "https:", "git+http:", "github:", "bitbucket:", "gitlab:", "gist:", "sourcehut:"];

    private static string[] SplitLimit(string value, int limit) => [.. value.Split('/').Take(limit)];
    private static string? At(string[] parts, int index) => index < parts.Length ? parts[index] : null;

    private static (string?, string, string?)? UserProject(PiUrl url, Func<string?, bool> reject)
    {
        var parts = SplitLimit(url.Pathname, 4);
        string? user = At(parts, 1), project = At(parts, 2);
        if (reject(At(parts, 3))) return null;
        if (project is not null && project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(project)) return null;
        return (user, project, url.Hash.Length > 0 ? url.Hash[1..] : "");
    }

    private static bool IsGitHubShorthand(string arg)
    {
        var firstHash = arg.IndexOf('#'); var firstSlash = arg.IndexOf('/');
        var secondSlash = firstSlash < 0 ? -1 : arg.IndexOf('/', firstSlash + 1);
        var firstColon = arg.IndexOf(':');
        var firstSpace = -1;
        for (var index = 0; index < arg.Length; index++) if (char.IsWhiteSpace(arg[index])) { firstSpace = index; break; }
        var firstAt = arg.IndexOf('@');
        var spaceOnlyAfterHash = firstSpace < 0 || firstHash > -1 && firstSpace > firstHash;
        var atOnlyAfterHash = firstAt == -1 || firstHash > -1 && firstAt > firstHash;
        var colonOnlyAfterHash = firstColon == -1 || firstHash > -1 && firstColon > firstHash;
        var secondSlashOnlyAfterHash = secondSlash == -1 || firstHash > -1 && secondSlash > firstHash;
        var hasSlash = firstSlash > 0;
        var doesNotEndWithSlash = firstHash > -1 ? arg[firstHash - 1 < 0 ? 0 : firstHash - 1] != '/' || firstHash == 0 : !arg.EndsWith('/');
        var doesNotStartWithDot = !arg.StartsWith('.');
        return spaceOnlyAfterHash && hasSlash && doesNotEndWithSlash && doesNotStartWithDot && atOnlyAfterHash && colonOnlyAfterHash && secondSlashOnlyAfterHash;
    }

    private static int LastIndexOfBefore(string value, char c, char before)
    {
        if (value.Length == 0) return -1;
        var start = value.IndexOf(before);
        return value.LastIndexOf(c, start > -1 ? start : value.Length - 1);
    }

    private static string CorrectProtocol(string arg)
    {
        var firstColon = arg.IndexOf(':');
        var proto = arg[..(firstColon + 1)];
        if (Protocols.Contains(proto)) return arg;
        if (firstColon >= 0 && string.CompareOrdinal(arg, firstColon, "://", 0, 3) == 0) return arg;
        var firstAt = arg.IndexOf('@');
        if (firstAt > -1) return firstAt > firstColon ? "git+ssh://" + arg : arg;
        return arg[..(firstColon + 1)] + "//" + arg[(firstColon + 1)..];
    }

    private static string CorrectUrl(string url)
    {
        var firstAt = LastIndexOfBefore(url, '@', '#');
        var lastColonBeforeHash = LastIndexOfBefore(url, ':', '#');
        if (lastColonBeforeHash > firstAt) url = url[..lastColonBeforeHash] + "/" + url[(lastColonBeforeHash + 1)..];
        if (LastIndexOfBefore(url, ':', '#') == -1 && !url.Contains("//", StringComparison.Ordinal)) url = "git+ssh://" + url;
        return url;
    }

    /// <summary>hosted-git-info fromUrl, or null.</summary>
    internal static Info? FromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var corrected = IsGitHubShorthand(url) ? "github:" + url : url;
        var withProtocol = CorrectProtocol(corrected);
        var parsed = PiUrl.Parse(withProtocol) ?? PiUrl.Parse(CorrectUrl(withProtocol));
        if (parsed is null) return null;
        var shortcut = Hosts.FirstOrDefault(host => host.Name + ":" == parsed.Protocol);
        var hostname = parsed.Hostname.StartsWith("www.", StringComparison.Ordinal) ? parsed.Hostname[4..] : parsed.Hostname;
        var host = shortcut ?? Hosts.FirstOrDefault(candidate => candidate.Domain == hostname);
        if (host is null) return null;
        try
        {
            if (shortcut is not null)
            {
                var pathname = parsed.Pathname.StartsWith('/') ? parsed.Pathname[1..] : parsed.Pathname;
                var firstAt = pathname.IndexOf('@');
                if (firstAt > -1) pathname = pathname[(firstAt + 1)..];
                string? user = null; string project;
                var lastSlash = pathname.LastIndexOf('/');
                if (lastSlash > -1)
                {
                    user = PiUri.DecodeComponent(pathname[..lastSlash]);
                    if (user.Length == 0) user = null;
                    project = PiUri.DecodeComponent(pathname[(lastSlash + 1)..]);
                }
                else project = PiUri.DecodeComponent(pathname);
                if (project.EndsWith(".git", StringComparison.Ordinal)) project = project[..^4];
                var committish = parsed.Hash.Length > 0 ? PiUri.DecodeComponent(parsed.Hash[1..]) : null;
                return new(host.Domain, user, project, committish);
            }
            if (!host.Protocols.Contains(parsed.Protocol)) return null;
            if (host.Extract(parsed) is not { } segments) return null;
            return new(host.Domain, segments.User is { Length: > 0 } user2 ? PiUri.DecodeComponent(user2) : segments.User,
                PiUri.DecodeComponent(segments.Project), PiUri.DecodeComponent(segments.Committish ?? "undefined"));
        }
        catch (FormatException) { return null; }
    }
}

/// <summary>Source utils/git.ts.</summary>
internal static partial class PiGitUrl
{
    [GeneratedRegex(@"^git@([^:]+):(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpLike();
    [GeneratedRegex(@"^(https?|ssh|git)://", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ProtocolUrl();

    private static (string Repo, string? Ref) SplitRef(string url)
    {
        var scp = ScpLike().Match(url);
        if (scp.Success)
        {
            var pathWithMaybeRef = scp.Groups[2].Value;
            var separator = pathWithMaybeRef.IndexOf('@');
            if (separator < 0) return (url, null);
            var repoPath = pathWithMaybeRef[..separator]; var reference = pathWithMaybeRef[(separator + 1)..];
            if (repoPath.Length == 0 || reference.Length == 0) return (url, null);
            return ($"git@{scp.Groups[1].Value}:{repoPath}", reference);
        }
        if (url.Contains("://", StringComparison.Ordinal))
        {
            var parsed = PiUrl.Parse(url);
            if (parsed is null) return (url, null);
            var pathWithMaybeRef = parsed.Pathname.TrimStart('/');
            var separator = pathWithMaybeRef.IndexOf('@');
            if (separator < 0) return (url, null);
            var repoPath = pathWithMaybeRef[..separator]; var reference = pathWithMaybeRef[(separator + 1)..];
            if (repoPath.Length == 0 || reference.Length == 0) return (url, null);
            parsed.Pathname = "/" + repoPath;
            var text = parsed.ToString();
            return (text.EndsWith('/') ? text[..^1] : text, reference);
        }
        var slashIndex = url.IndexOf('/');
        if (slashIndex < 0) return (url, null);
        var host = url[..slashIndex]; var rest = url[(slashIndex + 1)..];
        var at = rest.IndexOf('@');
        if (at < 0) return (url, null);
        var path = rest[..at]; var refName = rest[(at + 1)..];
        if (path.Length == 0 || refName.Length == 0) return (url, null);
        return ($"{host}/{path}", refName);
    }

    private static bool HasUnsafeGitInstallPart(string value, bool allowSlash)
    {
        string decoded;
        try { decoded = PiUri.DecodeComponent(value); } catch (FormatException) { return true; }
        foreach (var candidate in new[] { value, decoded })
        {
            if (candidate.Contains('\0') || candidate.Contains('\\') || candidate.StartsWith('/')) return true;
            if (!allowSlash && candidate.Contains('/')) return true;
            if (candidate.Split('/').Contains("..")) return true;
        }
        return false;
    }

    private static PiGitSource? Build(string repo, string host, string path, string? reference)
    {
        if (path.StartsWith('/')) return null;
        var normalized = path.EndsWith(".git", StringComparison.Ordinal) ? path[..^4] : path;
        normalized = normalized.TrimStart('/');
        if (host.Length == 0 || normalized.Length == 0 || normalized.Split('/').Length < 2) return null;
        if (HasUnsafeGitInstallPart(host, false) || HasUnsafeGitInstallPart(normalized, true)) return null;
        return new(repo, host, normalized, string.IsNullOrEmpty(reference) ? null : reference);
    }

    private static PiGitSource? ParseGeneric(string url)
    {
        var (repoWithoutRef, reference) = SplitRef(url);
        var repo = repoWithoutRef; string host, path;
        var scp = ScpLike().Match(repoWithoutRef);
        if (scp.Success) { host = scp.Groups[1].Value; path = scp.Groups[2].Value; }
        else if (repoWithoutRef.StartsWith("https://", StringComparison.Ordinal) || repoWithoutRef.StartsWith("http://", StringComparison.Ordinal) ||
            repoWithoutRef.StartsWith("ssh://", StringComparison.Ordinal) || repoWithoutRef.StartsWith("git://", StringComparison.Ordinal))
        {
            var parsed = PiUrl.Parse(repoWithoutRef);
            if (parsed is null) return null;
            host = parsed.Hostname; path = parsed.Pathname.TrimStart('/');
        }
        else
        {
            var slashIndex = repoWithoutRef.IndexOf('/');
            if (slashIndex < 0) return null;
            host = repoWithoutRef[..slashIndex]; path = repoWithoutRef[(slashIndex + 1)..];
            if (!host.Contains('.') && host != "localhost") return null;
            repo = "https://" + repoWithoutRef;
        }
        return Build(repo, host, path, reference);
    }

    /// <summary>Source parseGitUrl: with the <c>git:</c> prefix every historical shorthand, without it only protocol URLs.</summary>
    internal static PiGitSource? Parse(string source)
    {
        var trimmed = Pi.PiArgs.JsTrim(source);
        var hasGitPrefix = trimmed.StartsWith("git:", StringComparison.Ordinal);
        var url = hasGitPrefix ? Pi.PiArgs.JsTrim(trimmed[4..]) : trimmed;
        if (!hasGitPrefix && !ProtocolUrl().IsMatch(url)) return null;
        var split = SplitRef(url);
        (bool Matched, PiGitSource? Source) Hosted(IEnumerable<string?> candidates, Func<string> repo)
        {
            foreach (var candidate in candidates.OfType<string>())
            {
                if (PiHostedGitInfo.FromUrl(candidate) is not { } info) continue;
                if (split.Ref is not null && info.Project.Contains('@')) continue;
                return (true, Build(repo(), info.Domain, $"{info.User ?? "null"}/{info.Project}", string.IsNullOrEmpty(info.Committish) ? split.Ref : info.Committish));
            }
            return (false, null);
        }
        var useHttpsPrefix = !split.Repo.StartsWith("http://", StringComparison.Ordinal) && !split.Repo.StartsWith("https://", StringComparison.Ordinal) &&
            !split.Repo.StartsWith("ssh://", StringComparison.Ordinal) && !split.Repo.StartsWith("git://", StringComparison.Ordinal) && !split.Repo.StartsWith("git@", StringComparison.Ordinal);
        var hosted = Hosted([split.Ref is not null ? $"{split.Repo}#{split.Ref}" : null, url], () => useHttpsPrefix ? "https://" + split.Repo : split.Repo);
        if (hosted.Matched) return hosted.Source;
        var viaHttps = Hosted([split.Ref is not null ? $"https://{split.Repo}#{split.Ref}" : null, "https://" + url], () => "https://" + split.Repo);
        return viaHttps.Matched ? viaHttps.Source : ParseGeneric(url);
    }
}
