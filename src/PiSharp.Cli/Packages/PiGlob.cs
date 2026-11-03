// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/core/package-manager.ts (the minimatch,
// fs.globSync and ignore calls of matchesAnyPattern, expandPackageGlob, addIgnoreRules and collect*Entries).
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Packages;

/// <summary>The minimatch 10 subset the package manager uses (default options): <c>*</c>, <c>**</c>, <c>?</c>, character classes,
/// brace expansion, leading <c>!</c> negation and <c>#</c> comments. Dot segments only match patterns that spell the dot; extglobs are
/// matched literally. Like minimatch 11 (with brace-expansion 5 and nonrecursive-glob-match), amplified patterns stay bounded: brace
/// expansion is capped, a portion is matched without backtracking and <c>**</c> without a recursive walk per split.</summary>
internal static class PiMinimatch
{
    private sealed class Segment
    {
        internal bool GlobStar;
        internal string? Literal;
        internal Regex? Pattern;
        internal Token[]? Tokens;
        internal bool ExplicitDot, IgnoreCase;
        internal bool Test(string file) =>
            Literal is not null ? file == Literal : Tokens is not null ? (ExplicitDot || !file.StartsWith('.')) && Wildcard(file) : Pattern!.IsMatch(file);

        // A portion is one chain of single-character tokens and stars, so a greedy scan that returns to the last star on a mismatch
        // decides it in pattern x name steps (nonrecursive-glob-match's linear chain), where a backtracking regex takes exponential
        // time on "*a*a*a*b". A portion never holds a '/'.
        private bool Wildcard(string file)
        {
            var tokens = Tokens!;
            int fi = 0, ti = 0, star = -1, resume = 0;
            while (fi < file.Length)
            {
                if (ti < tokens.Length && tokens[ti].Kind == '*') { star = ti++; resume = fi; continue; }
                if (ti < tokens.Length && Accepts(tokens[ti], file, fi)) { ti++; fi++; continue; }
                if (star < 0) return false;
                ti = star + 1; fi = ++resume;
            }
            while (ti < tokens.Length && tokens[ti].Kind == '*') ti++;
            return ti == tokens.Length;
        }

        private bool Accepts(Token token, string file, int index) => token.Kind switch
        {
            '?' => true,
            '[' => token.Class!.IsMatch(file.AsSpan(index, 1)),
            _ => token.Char == file[index] || IgnoreCase && (char.ToLowerInvariant(token.Char) == char.ToLowerInvariant(file[index]) ||
                char.ToUpperInvariant(token.Char) == char.ToUpperInvariant(file[index]))
        };
    }

    /// <summary>A portion token: a literal character, <c>?</c>, a <c>[...]</c> class (one-character regex) or <c>*</c>.</summary>
    private readonly record struct Token(char Kind, char Char, Regex? Class);

    private static readonly Dictionary<string, List<Segment[]>> Cache = new(StringComparer.Ordinal);
    // Class regexes by source, shared by the portions of one brace expansion (filled under the Cache lock).
    private static readonly Dictionary<string, Regex> ClassCache = new(StringComparer.Ordinal);

    /// <summary>minimatch(path, pattern).</summary>
    internal static bool Match(string path, string pattern, bool ignoreCase = false)
    {
        if (pattern.StartsWith('#')) return false;
        var negate = false; var offset = 0;
        while (offset < pattern.Length && pattern[offset] == '!') { negate = !negate; offset++; }
        var body = pattern[offset..];
        if (body.Length == 0) return (path.Length == 0) != negate;
        var file = Split(path.Replace('\\', '/'));
        foreach (var compiled in Compile(body, ignoreCase))
            if (MatchOne(file, compiled)) return !negate;
        return negate;
    }

    internal static bool HasMagic(string pattern) => BraceExpand(pattern).Count > 1 || pattern.IndexOfAny(['*', '?', '[']) >= 0;

    private static string[] Split(string path)
    {
        var parts = path.Split('/');
        // Repeated slashes collapse (preserveMultipleSlashes false); a leading and a trailing empty part stay.
        var list = new List<string>(parts.Length);
        for (var index = 0; index < parts.Length; index++)
            if (parts[index].Length > 0 || index == 0 || index == parts.Length - 1) list.Add(parts[index]);
        return [.. list];
    }

    private static List<Segment[]> Compile(string pattern, bool ignoreCase)
    {
        var key = (ignoreCase ? "i:" : "s:") + pattern;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var result = BraceExpand(pattern).Select(expanded => Split(expanded).Select(part => CompileSegment(part, ignoreCase)).ToArray())
                .Select(Collapse).ToList();
            Cache[key] = result;
            return result;
        }
    }

    private static Segment[] Collapse(Segment[] segments)
    {
        var list = new List<Segment>();
        foreach (var segment in segments)
            if (!(segment.GlobStar && list.Count > 0 && list[^1].GlobStar)) list.Add(segment);
        return [.. list];
    }

    private static Segment CompileSegment(string part, bool ignoreCase)
    {
        if (part == "**") return new() { GlobStar = true };
        var regex = new StringBuilder();
        var tokens = new List<Token>();
        var magic = false;
        for (var index = 0; index < part.Length; index++)
        {
            var c = part[index];
            switch (c)
            {
                case '\\' when index + 1 < part.Length:
                    regex.Append(Regex.Escape(part[++index].ToString())); tokens.Add(new('c', part[index], null));
                    break;
                case '*':
                    magic = true;
                    while (index + 1 < part.Length && part[index + 1] == '*') index++;
                    tokens.Add(new('*', c, null));
                    break;
                case '?':
                    magic = true; tokens.Add(new('?', c, null));
                    break;
                case '[':
                    var end = ClassEnd(part, index);
                    if (end < 0) { regex.Append(@"\["); tokens.Add(new('c', c, null)); break; }
                    magic = true; tokens.Add(new('[', c, ClassPattern(ClassRegex(part[(index + 1)..end]), ignoreCase))); index = end;
                    break;
                default:
                    regex.Append(Regex.Escape(c.ToString())); tokens.Add(new('c', c, null));
                    break;
            }
        }
        if (!magic)
            return ignoreCase ? new() { Pattern = new Regex("^" + regex + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) } : new() { Literal = Unescape(part) };
        // A leading wildcard never matches a leading dot; a pattern that spells the dot (".x", "[.]x") may.
        var explicitDot = part.StartsWith('.') || part.StartsWith('[') && ClassEnd(part, 0) is var close && close > 0 && part[1..close].Contains('.') && part[1] is not ('!' or '^');
        return new() { Tokens = [.. tokens], ExplicitDot = explicitDot, IgnoreCase = ignoreCase };
    }

    private static Regex ClassPattern(string source, bool ignoreCase)
    {
        var key = (ignoreCase ? "i:" : "s:") + source;
        if (!ClassCache.TryGetValue(key, out var regex))
            ClassCache[key] = regex = new Regex(@"\A" + source + @"\z", RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None));
        return regex;
    }

    private static string Unescape(string part)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < part.Length; index++)
            builder.Append(part[index] == '\\' && index + 1 < part.Length ? part[++index] : part[index]);
        return builder.ToString();
    }

    private static int ClassEnd(string part, int start)
    {
        var index = start + 1;
        if (index < part.Length && (part[index] == '!' || part[index] == '^')) index++;
        if (index < part.Length && part[index] == ']') index++;
        for (; index < part.Length; index++)
        {
            if (part[index] == '\\') { index++; continue; }
            if (part[index] == '[' && index + 1 < part.Length && part[index + 1] == ':')
            {
                var close = part.IndexOf(":]", index + 2, StringComparison.Ordinal);
                if (close > 0) { index = close + 1; continue; }
            }
            if (part[index] == ']') return index;
        }
        return -1;
    }

    private static readonly Dictionary<string, string> PosixClasses = new(StringComparer.Ordinal)
    {
        ["alnum"] = @"\p{L}\p{Nl}\p{Nd}", ["alpha"] = @"\p{L}\p{Nl}", ["ascii"] = @"\x00-\x7f", ["blank"] = @"\p{Zs}\t",
        ["cntrl"] = @"\p{Cc}", ["digit"] = @"\p{Nd}", ["graph"] = @"\p{L}\p{M}\p{N}\p{P}\p{S}", ["lower"] = @"\p{Ll}",
        ["print"] = @"\p{L}\p{M}\p{N}\p{P}\p{S}\p{Zs}", ["punct"] = @"\p{P}", ["space"] = @"\p{Z}\t\r\n\v\f", ["upper"] = @"\p{Lu}",
        ["word"] = @"\p{L}\p{Nl}\p{Nd}\p{Pc}", ["xdigit"] = "A-Fa-f0-9"
    };

    private static string ClassRegex(string body)
    {
        var builder = new StringBuilder("[");
        var index = 0;
        if (body.Length > 0 && (body[0] == '!' || body[0] == '^')) { builder.Append('^'); index = 1; }
        for (; index < body.Length; index++)
        {
            var c = body[index];
            if (c == '[' && index + 1 < body.Length && body[index + 1] == ':')
            {
                var close = body.IndexOf(":]", index + 2, StringComparison.Ordinal);
                if (close > 0 && PosixClasses.TryGetValue(body[(index + 2)..close], out var posix)) { builder.Append(posix); index = close + 1; continue; }
            }
            if (c == '\\' && index + 1 < body.Length) { builder.Append(Regex.Escape(body[++index].ToString())); continue; }
            if (c == '-' && index > 0 && index + 1 < body.Length) { builder.Append('-'); continue; }
            builder.Append(c is '[' or ']' or '^' or '\\' or '-' ? "\\" + c : c.ToString());
        }
        return builder.Append(']').ToString();
    }

    private static bool MatchOne(string[] file, Segment[] pattern)
    {
        // match[fi, pi]: file[fi..] matches pattern[pi..], filled from the end, so a pattern with many ** costs file x pattern steps
        // instead of a recursive walk over every split of the path (minimatch 11 bounds the same walk). A ** never swallows a dot
        // segment; a trailing empty part (a directory's slash) matches the end of the pattern.
        int files = file.Length, width = pattern.Length + 1;
        var match = new bool[(files + 1) * width];
        for (var fi = files; fi >= 0; fi--)
            for (var pi = pattern.Length; pi >= 0; pi--)
            {
                bool value;
                if (fi == files) value = pi == pattern.Length;
                else if (pi == pattern.Length) value = fi == files - 1 && file[fi].Length == 0;
                else if (pattern[pi].GlobStar)
                    value = pi + 1 == pattern.Length
                        ? !file[fi].StartsWith('.') && (fi + 1 == files || match[(fi + 1) * width + pi])
                        : match[fi * width + pi + 1] || !file[fi].StartsWith('.') && match[(fi + 1) * width + pi];
                else value = match[(fi + 1) * width + pi + 1] && pattern[pi].Test(file[fi]);
                match[fi * width + pi] = value;
            }
        return match[0];
    }

    // brace-expansion 5 (minimatch 11) bounds an expansion: EXPANSION_MAX results and EXPANSION_MAX_LENGTH characters at any point
    // (truncated, not thrown), chained sets combined iteratively, and nesting followed EXPANSION_MAX_DEPTH levels (deeper stays literal).
    private const int ExpansionMax = 100_000, ExpansionMaxLength = 4_000_000, ExpansionMaxDepth = 1_000;

    /// <summary>brace-expansion: comma lists and numeric or letter sequences, nested; unbalanced braces stay literal.</summary>
    internal static List<string> BraceExpand(string pattern) => BraceExpand(pattern, 0);

    private static List<string> BraceExpand(string pattern, int depth)
    {
        if (depth > ExpansionMaxDepth) return [pattern];
        List<string> expansions = [""];
        var rest = pattern;
        while (FindBraceSet(rest) is (var open, var close))
        {
            var pre = rest[..open]; var body = rest[(open + 1)..close];
            rest = rest[(close + 1)..];
            var options = SplitTopLevel(body);
            List<string> alternatives;
            if (options.Count > 1)
            {
                alternatives = [];
                var length = 0L; var full = false;
                foreach (var option in options)
                {
                    foreach (var alternative in BraceExpand(option, depth + 1))
                    {
                        if (full = alternatives.Count >= ExpansionMax || length + alternative.Length > ExpansionMaxLength) break;
                        alternatives.Add(alternative); length += alternative.Length;
                    }
                    if (full) break;
                }
            }
            // A brace set without a comma or sequence stays literal; expansion continues after it.
            else alternatives = Sequence(body) ?? ["{" + body + "}"];
            expansions = Combine(expansions, pre, alternatives);
        }
        return Combine(expansions, rest, [""]);
    }

    private static (int Open, int Close)? FindBraceSet(string pattern)
    {
        var open = -1; var depth = 0;
        for (var index = 0; index < pattern.Length; index++)
        {
            var c = pattern[index];
            if (c == '\\') { index++; continue; }
            if (c == '$' && index + 1 < pattern.Length && pattern[index + 1] == '{') { index++; depth++; continue; }
            if (c == '{') { if (depth == 0) open = index; depth++; continue; }
            if (c != '}' || depth == 0) continue;
            depth--;
            if (depth == 0 && open >= 0) return (open, index);
        }
        return null;
    }

    private static List<string> Combine(List<string> prefixes, string pre, List<string> alternatives)
    {
        var result = new List<string>(); var length = 0L;
        foreach (var prefix in prefixes)
            foreach (var alternative in alternatives)
            {
                var size = prefix.Length + pre.Length + alternative.Length;
                if (result.Count >= ExpansionMax || length + size > ExpansionMaxLength) return result;
                result.Add(prefix + pre + alternative); length += size;
            }
        return result;
    }

    private static List<string> SplitTopLevel(string body)
    {
        var parts = new List<string>(); var depth = 0; var start = 0;
        for (var index = 0; index < body.Length; index++)
        {
            var c = body[index];
            if (c == '\\') { index++; continue; }
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == ',' && depth == 0) { parts.Add(body[start..index]); start = index + 1; }
        }
        parts.Add(body[start..]);
        return parts;
    }

    private static List<string>? Sequence(string body)
    {
        var match = Regex.Match(body, @"^(-?\d+|[a-zA-Z])\.\.(-?\d+|[a-zA-Z])(?:\.\.(-?\d+))?$", RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        // The step is at most the widest range, so the counter cannot overflow.
        var step = !match.Groups[3].Success ? 1L
            : long.TryParse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > long.MinValue
                ? Math.Clamp(Math.Abs(parsed), 1L, 1L << 33) : 1L << 33;
        string a = match.Groups[1].Value, b = match.Groups[2].Value;
        var numeric = int.TryParse(a, out var from) & int.TryParse(b, out var to);
        if (!numeric)
        {
            if (a.Length != 1 || b.Length != 1 || char.IsAsciiDigit(a[0]) || char.IsAsciiDigit(b[0])) return null;
            from = a[0]; to = b[0];
        }
        var width = numeric && (a.StartsWith('0') && a.Length > 1 || b.StartsWith('0') && b.Length > 1 || a.StartsWith("-0", StringComparison.Ordinal) || b.StartsWith("-0", StringComparison.Ordinal))
            ? Math.Max(a.Length, b.Length) : 0;
        var result = new List<string>(); var length = 0L;
        for (long value = from; from <= to ? value <= to : value >= to; value += from <= to ? step : -step)
        {
            string item;
            if (!numeric) item = ((char)value).ToString();
            else
            {
                var text = Math.Abs(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (width > 0) text = text.PadLeft(width - (value < 0 ? 1 : 0), '0');
                item = value < 0 ? "-" + text : text;
            }
            if (result.Count >= ExpansionMax || length + item.Length > ExpansionMaxLength) break;
            result.Add(item); length += item.Length;
        }
        return result;
    }
}

/// <summary>A gitignore matcher with the semantics of the <c>ignore</c> package (7.x) for the rules the package manager feeds it:
/// relative POSIX paths, a trailing <c>/</c> marking directories, later rules winning, <c>!</c> re-including, and a path ignored when
/// one of its parent directories is.</summary>
internal sealed class PiIgnore
{
    private readonly List<(Regex Regex, bool Negative)> rules = [];
    private readonly Dictionary<string, bool> cache = new(StringComparer.Ordinal);

    /// <summary>ignore().add(patterns).</summary>
    internal void Add(IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
            if (Compile(pattern) is { } rule) rules.Add(rule);
        cache.Clear();
    }

    /// <summary>ignore().ignores(path).</summary>
    internal bool Ignores(string path)
    {
        if (rules.Count == 0) return false;
        var trimmedSlash = path.EndsWith('/') ? path[..^1] : path;
        var parts = trimmedSlash.Split('/');
        // Parents first: a child of an ignored directory is ignored.
        var prefix = "";
        for (var index = 0; index < parts.Length - 1; index++)
        {
            prefix += parts[index] + "/";
            if (Test(prefix)) return true;
        }
        return Test(path);
    }

    private bool Test(string path)
    {
        if (cache.TryGetValue(path, out var known)) return known;
        var ignored = false;
        foreach (var (regex, negative) in rules)
        {
            if (negative == !ignored) continue;
            if (regex.IsMatch(path)) ignored = !negative;
        }
        return cache[path] = ignored;
    }

    private static (Regex, bool)? Compile(string raw)
    {
        var pattern = raw;
        // Trailing spaces are ignored unless escaped.
        pattern = Regex.Replace(pattern, @"((?:\\\\)*?)(\\?\s+)$", match => match.Groups[1].Value + (match.Groups[2].Value.StartsWith('\\') ? " " : ""));
        if (pattern.Length == 0 || pattern.StartsWith('#')) return null;
        var negative = false;
        if (pattern.StartsWith('!')) { negative = true; pattern = pattern[1..]; }
        pattern = pattern.Replace(@"\!", "!").Replace(@"\#", "#");
        if (pattern.Length == 0) return null;
        var directoryOnly = pattern.EndsWith('/');
        if (directoryOnly) pattern = pattern.TrimEnd('/');
        if (pattern.Length == 0) return null;
        var anchored = pattern.Contains('/');
        if (pattern.StartsWith('/')) pattern = pattern[1..];
        var body = new StringBuilder();
        var parts = pattern.Split('/');
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index]; var last = index == parts.Length - 1;
            if (part == "**")
            {
                if (index == 0 && last) { body.Append(".*"); continue; }
                if (index == 0) { body.Append("(?:.*/)?"); continue; }
                // A trailing "/**" matches everything inside, not the directory itself; "/**/" matches zero or more directories.
                if (last) { body.Append(".+"); continue; }
                body.Append("(?:[^/]+/)*");
                continue;
            }
            body.Append(SegmentRegex(part));
            if (!last) body.Append('/');
        }
        var text = (anchored ? "^" : "^(?:.*/)?") + body + (directoryOnly ? "/$" : "/?$");
        if (parts[^1] == "**") text = (anchored ? "^" : "^(?:.*/)?") + body + "$";
        return (new Regex(text, RegexOptions.CultureInvariant), negative);
    }

    private static string SegmentRegex(string part)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < part.Length; index++)
        {
            var c = part[index];
            switch (c)
            {
                case '\\' when index + 1 < part.Length: builder.Append(Regex.Escape(part[++index].ToString())); break;
                case '*': builder.Append("[^/]*"); break;
                case '?': builder.Append("[^/]"); break;
                case '[':
                    var end = part.IndexOf(']', index + 1);
                    if (end < 0) { builder.Append(@"\["); break; }
                    var content = part[(index + 1)..end];
                    if (content.StartsWith('!')) content = "^" + content[1..];
                    builder.Append('[').Append(content.Replace(@"\", @"\\")).Append(']');
                    index = end;
                    break;
                default: builder.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return builder.ToString();
    }
}

/// <summary>Node's <c>fs.globSync(pattern, { cwd })</c> for manifest entries: segment-wise matching from the root with minimatch
/// segments (case-insensitive on Windows and macOS), <c>**</c> descending through real directories only, relative results.</summary>
internal static class PiGlobExpand
{
    /// <summary>The absolute paths of the matches, in discovery order (callers sort).</summary>
    internal static List<string> Expand(string pattern, string root)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(PiSharp.Cli.Pi.PiPaths.Comparer);
        var ignoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        foreach (var expanded in PiMinimatch.BraceExpand(pattern.Replace('\\', '/')))
        {
            var directoryOnly = expanded.EndsWith('/');
            var start = root; var text = expanded;
            if (Path.IsPathRooted(expanded) && Path.GetPathRoot(expanded) is { Length: > 0 } drive) { start = drive; text = expanded[drive.Length..]; }
            var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".").ToArray();
            Walk(start, parts, 0, directoryOnly, ignoreCase, results, seen);
        }
        return results;
    }

    private static void Walk(string directory, string[] parts, int index, bool directoryOnly, bool ignoreCase, List<string> results, HashSet<string> seen)
    {
        if (index == parts.Length)
        {
            if (!Path.Exists(directory) || directoryOnly && !Directory.Exists(directory)) return;
            var full = Path.GetFullPath(directory);
            if (seen.Add(full)) results.Add(full);
            return;
        }
        var part = parts[index];
        if (part == "**")
        {
            Walk(directory, parts, index + 1, directoryOnly, ignoreCase, results, seen);
            foreach (var entry in Entries(directory))
                if (entry is DirectoryInfo { LinkTarget: null } && !entry.Name.StartsWith('.'))
                    Walk(entry.FullName, parts, index, directoryOnly, ignoreCase, results, seen);
            return;
        }
        if (!PiMinimatch.HasMagic(part))
        {
            var next = Path.Combine(directory, part);
            if (index + 1 < parts.Length ? Directory.Exists(next) : Path.Exists(next)) Walk(next, parts, index + 1, directoryOnly, ignoreCase, results, seen);
            return;
        }
        foreach (var entry in Entries(directory))
        {
            if (!PiMinimatch.Match(entry.Name, part, ignoreCase)) continue;
            if (index + 1 < parts.Length && !Directory.Exists(entry.FullName)) continue;
            Walk(entry.FullName, parts, index + 1, directoryOnly, ignoreCase, results, seen);
        }
    }

    private static List<FileSystemInfo> Entries(string directory)
    {
        try { return Directory.Exists(directory) ? [.. PiSharp.Contracts.Compatibility.NodeDirectoryOrder.Order(new DirectoryInfo(directory).EnumerateFileSystemInfos())] : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }
}
