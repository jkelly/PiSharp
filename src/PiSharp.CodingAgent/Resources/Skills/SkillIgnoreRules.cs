using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.CodingAgent.Resources.Skills;

/// <summary>Bounded ordered skill-ignore rules. Applies Pi's prefixIgnorePattern before matching;
/// no filesystem, cache, task or trust ownership. Dependency's default is case-insensitive.</summary>
public sealed class SkillIgnoreRules(int maximumRules = 4096, int maximumPatternCharacters = 4096)
{
    private sealed record Rule(Regex Pattern, bool Negative, bool DirectoryOnly);
    private readonly List<Rule> rules = [];
    public int Count => rules.Count;
    public void AddFile(string content, string directoryPrefix = "")
    {
        ArgumentNullException.ThrowIfNull(content); ArgumentNullException.ThrowIfNull(directoryPrefix);
        if (maximumRules is < 1 or > 65_536 || maximumPatternCharacters is < 1 or > 4096 ||
            directoryPrefix.Length > 4096 || directoryPrefix.StartsWith('/') || directoryPrefix.Split('/').Any(piece => piece is "." or ".."))
            throw new ArgumentException("Invalid skill ignore limits/prefix.");
        var prefix = directoryPrefix.Length == 0 ? "" : directoryPrefix.TrimEnd('/') + "/";
        foreach (var line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = Trim(line); if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            if (line.Length + prefix.Length > maximumPatternCharacters) throw new ArgumentException("Skill ignore pattern limit.");
            // Preserve the source transform, including root / and escaped ! quirks.
            var pattern = line; var negated = pattern.StartsWith('!');
            if (negated || pattern.StartsWith("\\!", StringComparison.Ordinal)) pattern = pattern[1..];
            if (pattern.StartsWith('/')) pattern = pattern[1..];
            pattern = (negated ? "!" : "") + prefix + pattern;
            if (pattern.StartsWith('#')) continue;
            var negative = pattern.StartsWith('!'); if (negative) pattern = pattern[1..];
            if (pattern.StartsWith("\\!", StringComparison.Ordinal) || pattern.StartsWith("\\#", StringComparison.Ordinal)) pattern = pattern[1..];
            if (pattern.StartsWith('\uFEFF')) pattern = pattern[1..];
            pattern = pattern.TrimEnd('\r', '\n');
            int end = pattern.Length;
            while (end > 0 && pattern[end - 1] == ' ')
            { int slash = end - 2; while (slash >= 0 && pattern[slash] == '\\') slash--; if ((end - 2 - slash) % 2 == 1) break; end--; }
            pattern = pattern[..end]; if (pattern.Length == 0) continue;
            var trailing = 0; for (int i = pattern.Length - 1; i >= 0 && pattern[i] == '\\'; i--) trailing++;
            if (trailing % 2 == 1) continue;
            var directory = pattern.EndsWith('/'); if (directory) pattern = pattern[..^1];
            var anchored = pattern.StartsWith('/') || pattern.Contains('/'); if (pattern.StartsWith('/')) pattern = pattern[1..];
            var glob = CompileGlob(pattern); if (glob is null) continue; // Invalid bracket/range grammar matches nothing.
            if (rules.Count == maximumRules) throw new ArgumentException("Skill ignore rule limit.");
            rules.Add(new(new Regex("\\A" + (anchored ? "" : "(?:[^/]+/)*") + glob + "\\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking), negative, directory));
        }
    }
    public bool IsIgnored(string relativePosixPath, bool directory = false)
    {
        ArgumentNullException.ThrowIfNull(relativePosixPath);
        var path = relativePosixPath.TrimEnd('/');
        if (path.Length is < 1 or > 4096 || path.StartsWith('/') || path.Contains('\0') ||
            path.Split('/').Any(piece => piece is "" or "." or "..")) throw new ArgumentException("Skill ignore requires relative POSIX path.");
        var pieces = path.Split('/'); var current = "";
        for (var i = 0; i < pieces.Length; i++)
        {
            current = current.Length == 0 ? pieces[i] : current + "/" + pieces[i];
            var isDirectory = i < pieces.Length - 1 || directory; var ignored = false;
            foreach (var rule in rules)
                if ((!rule.DirectoryOnly || isDirectory) && rule.Pattern.IsMatch(current)) ignored = !rule.Negative;
            if (ignored) return true; // Negating a child cannot resurrect an excluded parent.
        }
        return false;
    }
    private static string? CompileGlob(string pattern)
    {
        var result = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\') { if (++i == pattern.Length) return null; result.Append(Regex.Escape(pattern[i].ToString())); }
            else if (c == '?') result.Append("[^/]");
            else if (c == '*')
            {
                var start = i; while (i + 1 < pattern.Length && pattern[i + 1] == '*') i++;
                var pair = i - start + 1 == 2 && (start == 0 || pattern[start - 1] == '/') && (i + 1 == pattern.Length || pattern[i + 1] == '/');
                if (pair && i + 1 < pattern.Length) { result.Append("(?:[^/]+/)*"); i++; }
                else if (pair && start > 0) result.Append("[\\s\\S]+");
                else result.Append("[^/]*");
            }
            else if (c == '[')
            {
                var members = Class(pattern, ref i); if (members is null) return null; result.Append(members);
            }
            else result.Append(Regex.Escape(c.ToString()));
        }
        return result.ToString();
    }
    private static string? Class(string pattern, ref int position)
    {
        var ranges = new List<(int Low, int High)>(); var i = position + 1;
        if (i == pattern.Length) return null;
        var negative = pattern[i] is '!' or '^'; if (negative) i++;
        var first = true; int? previous = null;
        while (i < pattern.Length)
        {
            if (pattern[i] == ']' && !first)
            {
                position = i; var body = new StringBuilder(negative ? "[^/" : "[");
                foreach (var range in ranges)
                {
                    if (range.Low < '/' && range.High >= '/') Append(range.Low, '/' - 1);
                    if (range.Low <= '/' && range.High > '/') Append('/' + 1, range.High);
                    if (range.High < '/' || range.Low > '/') Append(range.Low, range.High);
                }
                if (!negative && body.Length == 1) return null;
                return body.Append(']').ToString();
                void Append(int low, int high)
                { body.Append("\\u").Append(low.ToString("X4")); if (low != high) body.Append("-\\u").Append(high.ToString("X4")); }
            }
            first = false;
            if (pattern[i] == '[' && i + 1 < pattern.Length && pattern[i + 1] == ':')
            {
                var end = pattern.IndexOf(":]", i + 2, StringComparison.Ordinal); if (end < 0) return null;
                var named = Posix(pattern[(i + 2)..end]); if (named is null) return null;
                ranges.AddRange(named); previous = null; i = end + 2; continue;
            }
            if (pattern[i] == '-' && previous.HasValue && i + 1 < pattern.Length && pattern[i + 1] != ']')
            {
                i++; if (pattern[i] == '\\' && ++i == pattern.Length) return null;
                var upper = pattern[i++]; if (previous.Value <= upper) ranges.Add((previous.Value, upper)); previous = null; continue;
            }
            if (pattern[i] == '\\' && ++i == pattern.Length) return null;
            var member = pattern[i++]; ranges.Add((member, member)); previous = member;
        }
        return null;
    }
    private static (int Low, int High)[]? Posix(string name) => name switch
    {
        "alnum" => [('0','9'),('A','Z'),('a','z')], "alpha" => [('A','Z'),('a','z')],
        "blank" => [(' ',' '),('\t','\t')], "cntrl" => [(0,31),(127,127)], "digit" => [('0','9')],
        "graph" => [(33,126)], "lower" => [('a','z')], "print" => [(32,126)],
        "punct" => [(33,47),(58,64),(91,96),(123,126)], "space" => [(' ',' '),('\t','\t'),('\n','\n'),('\r','\r')],
        "upper" => [('A','Z')], "xdigit" => [('0','9'),('A','F'),('a','f')], _ => null
    };
    private static string Trim(string text)
    { int first = 0, last = text.Length; while (first < last && PromptTemplateParser.IsEcmaWhitespace(text[first])) first++;
        while (last > first && PromptTemplateParser.IsEcmaWhitespace(text[last - 1])) last--; return text[first..last]; }
}
