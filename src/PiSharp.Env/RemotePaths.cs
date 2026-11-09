using System.Text.RegularExpressions;

namespace PiSharp.Env;

/// <summary>Remote path rules; never uses the client's operating system to interpret a path.</summary>
public static class RemotePaths
{
    public static bool IsWindows(RemoteInfo info) => info.Os == "windows" || info.Separator == "\\";
    public static string Join(RemoteInfo info, IEnumerable<string> parts)
    {
        var separator = IsWindows(info) ? "\\" : "/";
        var combined = string.Join(separator, parts.Where(p => p.Length != 0));
        var normalized = Normalize(combined, IsWindows(info));
        return combined.Length > 0 && (combined[^1] == '/' || IsWindows(info) && combined[^1] == '\\') && !normalized.EndsWith(separator, StringComparison.Ordinal)
            ? normalized + separator : normalized;
    }
    public static string Resolve(RemoteInfo info, string cwd, string path)
    {
        var win = IsWindows(info);
        if (path == "~") path = info.Home;
        else if (path.StartsWith("~/", StringComparison.Ordinal) || win && path.StartsWith("~\\", StringComparison.Ordinal))
            path = Join(info, [info.Home, path[2..]]);
        else if (path.StartsWith("file://", StringComparison.Ordinal) && TryFileUrl(path, win, out var filePath)) path = filePath;
        if (!win)
        {
            if (!cwd.StartsWith('/')) cwd = info.Cwd.TrimEnd('/') + "/" + cwd;
            return Normalize(path.StartsWith('/') ? path : cwd.TrimEnd('/') + "/" + path, false);
        }
        path = path.Replace('/', '\\');
        cwd = cwd.Replace('/', '\\');
        if (path.StartsWith("\\\\", StringComparison.Ordinal)) return Normalize(path, true);
        var drive = Regex.Match(path, "^[a-zA-Z]:");
        if (drive.Success)
        {
            if (path.Length > 2 && path[2] == '\\') return Normalize(path, true);
            var prefix = drive.Value;
            var basePath = cwd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? cwd
                : info.DriveCwds.FirstOrDefault(pair => pair.Key.Equals(prefix, StringComparison.OrdinalIgnoreCase)).Value ?? info.Cwd;
            if (!basePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) basePath = prefix + "\\";
            return Normalize(basePath.TrimEnd('\\') + "\\" + path[2..], true);
        }
        if (path.StartsWith('\\')) return Normalize((cwd.Length >= 2 && cwd[1] == ':' ? cwd[..2] : Root(cwd)) + path, true);
        return Normalize(cwd.TrimEnd('\\') + "\\" + path, true);
    }
    public static string Name(string path) => path.TrimEnd('/', '\\').Split('/', '\\').Last();
    private static bool TryFileUrl(string url, bool windows, out string path)
    {
        path = url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
        // System.Uri treats file:///C:/... as a DOS path and removes the leading slash from AbsolutePath, even when the
        // destination namespace is POSIX. Keep the URL pathname independently, as Node's fileURLToPath({ windows }) does.
        var normalized = url.Replace('\\', '/');
        var authorityEnd = normalized.IndexOf('/', 7);
        var pathname = authorityEnd < 0 ? "/" : normalized[authorityEnd..];
        var suffix = pathname.IndexOfAny(new[] { '?', '#' });
        if (suffix >= 0) pathname = pathname[..suffix];
        if (Regex.IsMatch(pathname, windows ? "%2[fF]|%5[cC]" : "%2[fF]") || Regex.IsMatch(pathname, "%(?![0-9a-fA-F]{2})")) return false;
        var decoded = Uri.UnescapeDataString(pathname);
        if (windows)
        {
            if (uri.Host.Length > 0 && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            { path = "\\\\" + uri.Host + decoded.Replace('/', '\\'); return true; }
            if (!Regex.IsMatch(decoded, "^/[a-zA-Z]:/")) return false;
            path = decoded[1..].Replace('/', '\\');
            return true;
        }
        if (uri.Host.Length > 0 && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        path = decoded;
        return true;
    }
    private static string Root(string path)
    {
        if (!path.StartsWith("\\\\", StringComparison.Ordinal)) return "";
        return "\\\\" + string.Join('\\', path[2..].Split('\\').Take(2));
    }
    private static string Normalize(string path, bool windows)
    {
        if (windows) path = path.Replace('/', '\\');
        var separator = windows ? '\\' : '/';
        var root = windows ? Root(path) : path.StartsWith('/') ? "/" : "";
        if (windows && root.Length == 0 && path.Length >= 2 && path[1] == ':') root = path[..2] + (path.Length > 2 && path[2] == '\\' ? "\\" : "");
        if (windows && root.Length == 0 && path.StartsWith('\\')) root = "\\";
        var segments = new List<string>();
        foreach (var part in path[root.Length..].Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == ".." && segments.Count > 0 && segments[^1] != "..") segments.RemoveAt(segments.Count - 1);
            else if (part != ".." || root.Length == 0) segments.Add(part);
        }
        var suffix = string.Join(separator, segments);
        var result = root + (root.Length > 0 && suffix.Length > 0 && root[^1] != separator && !(windows && root.Length == 2) ? separator : "") + suffix;
        return result.Length == 0 ? "." : result;
    }
}
