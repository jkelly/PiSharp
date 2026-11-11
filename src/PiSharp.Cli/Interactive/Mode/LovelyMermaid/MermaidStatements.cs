// lovely-mermaid 0.3.3 (Apache-2.0): src/statements.ts.
using System.Text;
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidJs;

namespace PiSharp.Cli.Interactive.Mode.LovelyMermaid;

internal static class MermaidStatements
{
    public static void SplitStatements(string line, List<string> output)
    {
        var cur = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"') inQuotes = false;
                cur.Append(c);
            }
            else if (c == '"') { inQuotes = true; cur.Append(c); }
            else if (c == '%' && i + 1 < line.Length && line[i + 1] == '%') break;
            else if (c == ';') Flush();
            else cur.Append(c);
        }
        Flush();
        void Flush()
        {
            var trimmed = Trim(cur.ToString());
            if (trimmed != "") output.Add(trimmed);
            cur.Clear();
        }
    }

    public static int FrontmatterEnd(IReadOnlyList<string> lines)
    {
        var i = 0;
        while (i < lines.Count && Trim(lines[i]) == "") i++;
        if (i >= lines.Count || Trim(lines[i]) != "---") return 0;
        i++;
        while (i < lines.Count && Trim(lines[i]) != "---") i++;
        return i + 1;
    }

    public static List<string> StatementsOf(string src)
    {
        var lines = MermaidLabels.SrcLines(src);
        var output = new List<string>();
        for (var i = FrontmatterEnd(lines); i < lines.Count; i++) SplitStatements(lines[i], output);
        return output;
    }

    public static string? FrontmatterTitle(string src)
    {
        var lines = MermaidLabels.SrcLines(src);
        var end = FrontmatterEnd(lines);
        for (var i = 0; i < Math.Min(end, lines.Count); i++)
        {
            var kv = SplitOnce(lines[i], ":");
            if (kv is not { } pair || TrimEnd(pair.Item1) != "title") continue;
            var t = Trim(pair.Item2);
            var quoted = t.Length > 1 && ((t[0] == '"' && t[^1] == '"') || (t[0] == '\'' && t[^1] == '\''));
            var title = Trim(quoted ? t[1..^1] : t);
            return title == "" ? null : title;
        }
        return null;
    }

    public static byte[] QuoteMask(string[] chars)
    {
        var mask = new byte[chars.Length];
        var inQuotes = false;
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] == "\"") { mask[i] = 1; inQuotes = !inQuotes; }
            else if (inQuotes) mask[i] = 1;
        }
        return mask;
    }

    public static List<string> SplitTop(string s, Func<string, bool> isSep)
    {
        var output = new List<string>();
        var cur = new StringBuilder();
        var inQuotes = false;
        var depth = 0;
        foreach (var c in CodePoints(s))
        {
            if (c == "\"") inQuotes = !inQuotes;
            else if (!inQuotes && c == "(") depth++;
            else if (!inQuotes && c == ")" && depth > 0) depth--;
            if (!inQuotes && depth == 0 && isSep(c))
            {
                if (cur.Length > 0) output.Add(cur.ToString());
                cur.Clear();
            }
            else cur.Append(c);
        }
        if (cur.Length > 0) output.Add(cur.ToString());
        return output;
    }

    public static (string, string)? SplitColon(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != ':') continue;
            var run = i;
            while (run < s.Length && s[run] == ':') run++;
            if (run - i >= 3) { i = run - 1; continue; }
            return (s[..i], s[(i + 1)..]);
        }
        return null;
    }

    public static (string Id, List<string> Classes) TakeTags(string token)
    {
        var parts = token.Split(":::", StringSplitOptions.None);
        return parts.Length == 1 || parts[0] == ""
            ? (token, [])
            : (parts[0], parts.Skip(1).Where(c => c != "").ToList());
    }

    public static (List<string> Ids, List<string> Names)? ParseClassAssign(string rest)
    {
        var body = Trim(rest);
        var ws = -1;
        // /\s\S*$/ matches the final whitespace followed solely by non-whitespace.
        for (var i = body.Length - 1; i >= 0; i--)
            if (IsSpace(body[i])) { ws = i; break; }
        if (ws == -1) return null;
        static List<string> Split(string s) => s.Split(',').Select(Trim).Where(t => t != "").ToList();
        return (Split(body[..ws]), Split(body[ws..]));
    }

    public static (string Id, string Href)? ParseHref(string rest)
    {
        var words = Words(rest);
        if (words.Count == 0 || words.Count > 1 && words[1] is "call" or "callback") return null;
        var start = rest.IndexOf('"');
        while (start != -1)
        {
            var end = rest.IndexOf('"', start + 1);
            if (end == -1) return null;
            if (end > start + 1) return (words[0], rest[(start + 1)..end]);
            start = end;
        }
        return null;
    }

    public static string FirstWord(string s) => Words(s).FirstOrDefault() ?? "";
    public static List<string> Words(string s) => MermaidJs.Words(s);

    public static (string, string)? SplitOnce(string s, string sep)
    {
        var i = s.IndexOf(sep, StringComparison.Ordinal);
        return i == -1 ? null : (s[..i], s[(i + sep.Length)..]);
    }

    public static string? NonEmpty(string s) => s == "" ? null : s;

    public static string? HeaderKind(IReadOnlyList<string> statements)
        => statements.Count == 0 ? null : NonEmpty(MermaidLabels.AsciiLower(FirstWord(statements[0])));

    public static (List<string> Names, Dictionary<string, string> Props)? ParseClassDef(string rest)
    {
        var body = Trim(rest);
        var ws = -1;
        for (var i = 0; i < body.Length; i++) if (IsSpace(body[i])) { ws = i; break; }
        if (ws == -1) return null;
        var names = body[..ws].Split(',').Select(Trim).Where(s => s != "").ToList();
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in SplitTop(Trim(body[ws..]), c => c == ","))
        {
            if (SplitOnce(pair, ":") is not { } kv) continue;
            var k = Trim(kv.Item1);
            var v = Trim(kv.Item2);
            if (k != "" && v != "") props[k] = v;
        }
        return names.Count == 0 ? null : (names, props);
    }
}
