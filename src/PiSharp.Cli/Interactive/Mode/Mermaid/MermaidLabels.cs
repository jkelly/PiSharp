// grok-mermaid 0.2.3 (Apache-2.0): src/labels.ts — used by Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/components/mermaid.ts.
using System.Globalization;
using System.Text;

namespace PiSharp.Cli.Interactive.Mode.Mermaid;

internal static class MermaidLabels
{
    /// <summary>Node labels wrap to at most this many display columns per line ...</summary>
    public const int WrapWidth = 24;
    /// <summary>... and at most this many lines; overflow is truncated with an ellipsis.</summary>
    public const int MaxLines = 4;
    /// <summary>Edge labels are truncated to this many columns.</summary>
    public const int MaxLabel = 28;

    /// <summary>Identifier-boundary characters preferred as break points when a single word is too wide to fit.</summary>
    public static readonly char[] LabelBreakChars = ['_', '-', '.', '/'];

    /// <summary>ASCII-only case folding, matching Rust's <c>to_ascii_lowercase</c>.</summary>
    public static string AsciiLower(string s) => string.Create(s.Length, s, static (span, src) =>
    {
        for (var i = 0; i < src.Length; i++) span[i] = src[i] is >= 'A' and <= 'Z' ? (char)(src[i] + 32) : src[i];
    });

    public static string AsciiUpper(string s) => string.Create(s.Length, s, static (span, src) =>
    {
        for (var i = 0; i < src.Length; i++) span[i] = src[i] is >= 'a' and <= 'z' ? (char)(src[i] - 32) : src[i];
    });

    /// <summary>C0 and C1 controls, less the <c>\t\n\r</c> the parsers and <see cref="SrcLines"/> read.</summary>
    private static bool IsStrippedControl(char c) => c is <= (char)0x08 or (char)0x0b or (char)0x0c or (>= (char)0x0e and <= (char)0x1f) or (>= (char)0x7f and <= (char)0x9f);

    /// <summary>Applied by every public entry point that takes untrusted source.</summary>
    public static string StripControls(string src)
    {
        var i = 0;
        while (i < src.Length && !IsStrippedControl(src[i])) i++;
        if (i == src.Length) return src;
        var sb = new StringBuilder(src.Length);
        foreach (var c in src) if (!IsStrippedControl(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Split source into lines the way Rust's <c>str::lines()</c> does.</summary>
    public static List<string> SrcLines(string src)
    {
        var lines = src.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>Matches Rust's <c>char::is_alphanumeric</c>: <c>/[\p{Alphabetic}\p{N}]/u.test(c)</c>.</summary>
    public static bool IsAlphanumeric(string c)
    {
        for (var i = 0; i < c.Length;)
        {
            var cp = MermaidJs.CodePointAt(c, i);
            i += cp > 0xffff ? 2 : 1;
            if (cp is >= 0xd800 and <= 0xdfff) continue;
            var t = MermaidUnicodeData.Alphanumeric;
            var lo = 0;
            var hi = t.Length / 2 - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                if (cp < t[mid * 2]) hi = mid - 1;
                else if (cp > t[mid * 2 + 1]) lo = mid + 1;
                else return true;
            }
        }
        return false;
    }

    /// <summary>Characters allowed in a bare node/state/class identifier.</summary>
    public static bool IsIdChar(string c) => IsAlphanumeric(c) || c == "_";

    private const int EntityLookahead = 10;

    private static string? NamedEntity(string body) => body switch
    {
        "lt" => "<",
        "gt" => ">",
        "amp" => "&",
        "quot" => "\"",
        "apos" => "'",
        // NAMED_ENTITIES is a plain object, so inherited Object.prototype members also "decode", stringified. Only
        // these fit the lookahead window (a body of at most nine code points).
        "toString" => "function toString() { [native code] }",
        "valueOf" => "function valueOf() { [native code] }",
        "__proto__" => "[object Object]",
        _ => null,
    };

    private static bool AllDigits(string s, bool hex)
    {
        if (s.Length == 0) return false;
        foreach (var c in s) if (!(char.IsAsciiDigit(c) || hex && char.IsAsciiHexDigit(c))) return false;
        return true;
    }

    private static string? DecodeEntityBody(string body)
    {
        var named = NamedEntity(body);
        if (named is not null) return named;
        if (!body.StartsWith('#')) return null;
        var num = body[1..];
        var hex = num.Length > 0 && num[0] is 'x' or 'X';
        var digits = hex ? num[1..] : num;
        if (!AllDigits(digits, hex)) return null;
        // At most nine code points fit the lookahead window, so the value always fits a long.
        var code = long.Parse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture);
        // Surrogates and out-of-range values are not characters at all.
        if (code > 0x10ffff || code is >= 0xd800 and <= 0xdfff) return null;
        // Reject control chars: NUL collides with the CONT sentinel and ESC would inject ANSI into scrollback.
        if (code < 0x20 || code is >= 0x7f and <= 0x9f) return null;
        return char.ConvertFromUtf32((int)code);
    }

    /// <summary>Decode HTML entities in label text.</summary>
    public static string DecodeHtmlEntities(string s)
    {
        if (!s.Contains('&')) return s;
        var chars = MermaidJs.CodePoints(s);
        var sb = new StringBuilder();
        var i = 0;
        while (i < chars.Length)
        {
            if (chars[i] != "&")
            {
                sb.Append(chars[i]);
                i++;
                continue;
            }
            var hi = Math.Min(i + 1 + EntityLookahead, chars.Length);
            var semi = -1;
            for (var j = i + 1; j < hi; j++)
            {
                if (chars[j] == ";")
                {
                    semi = j;
                    break;
                }
            }
            var decoded = semi == -1 ? null : DecodeEntityBody(MermaidJs.Join(chars, i + 1, semi));
            if (decoded is null)
            {
                sb.Append('&');
                i++;
            }
            else
            {
                sb.Append(decoded);
                i = semi + 1;
            }
        }
        return sb.ToString();
    }

    /// <summary>Strip markdown emphasis from a <c>`backtick`</c> label string.</summary>
    public static string StripMarkdown(string s)
    {
        var noCode = string.Concat(MermaidJs.CodePoints(s).Where(c => c != "`"));
        var noStrong = noCode.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal);
        var chars = MermaidJs.CodePoints(noStrong);
        var sb = new StringBuilder();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            // Keep `*`/`_` only when they sit inside a word, so snake_case survives.
            var inWord = i > 0 && IsAlphanumeric(chars[i - 1]) && i + 1 < chars.Length && IsAlphanumeric(chars[i + 1]);
            if ((c == "*" || c == "_") && !inWord) continue;
            sb.Append(c);
        }
        return MermaidJs.Trim(sb.ToString());
    }

    /// <summary>Inline formatting tags that carry no meaning in a terminal.</summary>
    private static readonly HashSet<string> HtmlFormatTags =
    [
        "b", "strong", "i", "em", "u", "s", "strike", "del", "ins", "mark", "small", "big", "sub", "sup", "code", "kbd", "samp",
        "var", "tt", "span", "font", "q", "abbr", "cite", "pre",
    ];

    private static bool IsAsciiAlnum(string? c) => c is { Length: 1 } && char.IsAsciiLetterOrDigit(c[0]);

    /// <summary>Read a tag starting at <paramref name="start"/>, returning its name and the index after <c>&gt;</c>.</summary>
    private static (string Name, int End)? HtmlTagAt(string[] chars, int start)
    {
        var i = start + 1;
        if (MermaidJs.At(chars, i) == "/") i++;
        var nameStart = i;
        while (i < chars.Length && IsAsciiAlnum(chars[i])) i++;
        if (i == nameStart) return null;
        var name = MermaidJs.Join(chars, nameStart, i);
        while (i < chars.Length && chars[i] != ">")
        {
            if (chars[i] == "<") return null;
            i++;
        }
        return MermaidJs.At(chars, i) == ">" ? (name, i + 1) : null;
    }

    public static string StripHtmlTags(string s)
    {
        var chars = MermaidJs.CodePoints(s);
        var sb = new StringBuilder();
        var i = 0;
        while (i < chars.Length)
        {
            if (chars[i] == "<" && HtmlTagAt(chars, i) is { } tag)
            {
                var lower = tag.Name.ToLowerInvariant();
                if (lower == "br")
                {
                    sb.Append(' ');
                    i = tag.End;
                    continue;
                }
                if (HtmlFormatTags.Contains(lower))
                {
                    i = tag.End;
                    continue;
                }
            }
            sb.Append(chars[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Strip one matching pair of wrapping delimiters, if present.</summary>
    private static string? Unwrap(string s, string open, string close) =>
        s.Length >= open.Length + close.Length && s.StartsWith(open, StringComparison.Ordinal) && s.EndsWith(close, StringComparison.Ordinal)
            ? s[open.Length..(s.Length - close.Length)]
            : null;

    /// <summary>Normalise raw label text: strip markup, unquote, and decode entities.</summary>
    public static string CleanLabel(string raw)
    {
        var trimmed = MermaidJs.Trim(StripHtmlTags(MermaidJs.Trim(raw)));
        var unquoted = MermaidJs.Trim(Unwrap(trimmed, "\"", "\"") ?? Unwrap(trimmed, "'", "'") ?? trimmed);
        var md = Unwrap(unquoted, "`", "`");
        return DecodeHtmlEntities(md is null ? unquoted : StripMarkdown(MermaidJs.Trim(md)));
    }

    /// <summary>Index of the last identifier-boundary character, or -1.</summary>
    private static int LastBreak(string s)
    {
        var best = -1;
        foreach (var c in LabelBreakChars) best = Math.Max(best, s.LastIndexOf(c));
        return best;
    }

    /// <summary>Wrap a label to <paramref name="width"/> columns over at most <paramref name="maxLines"/> lines.</summary>
    public static List<string> WrapLabel(string label, int width, int maxLines)
    {
        width = Math.Max(1, width);
        var lines = new List<string>();
        var cur = "";
        var curW = 0;

        foreach (var word in MermaidJs.Words(label))
        {
            var ww = MermaidWidth.StringWidth(word);
            if (ww > width)
            {
                if (cur != "")
                {
                    lines.Add(cur);
                    cur = "";
                }
                var chunk = "";
                var chunkW = 0;
                foreach (var (ch, cw) in MermaidWidth.Measured(word))
                {
                    if (chunkW + cw > width && chunk != "")
                    {
                        var p = LastBreak(chunk);
                        var carry = p == -1 ? "" : chunk[(p + 1)..];
                        lines.Add(p == -1 ? chunk : chunk[..(p + 1)]);
                        chunk = carry;
                        chunkW = MermaidWidth.StringWidth(carry);
                    }
                    chunk += ch;
                    chunkW += cw;
                }
                cur = chunk;
                curW = chunkW;
            }
            else if (cur == "")
            {
                cur = word;
                curW = ww;
            }
            else if (curW + 1 + ww <= width)
            {
                cur += " " + word;
                curW += 1 + ww;
            }
            else
            {
                lines.Add(cur);
                cur = word;
                curW = ww;
            }
        }
        if (cur != "") lines.Add(cur);
        if (lines.Count == 0) lines.Add("");

        if (lines.Count > maxLines)
        {
            lines.RemoveRange(maxLines, lines.Count - maxLines);
            var target = Math.Max(1, width - 1);
            var s = new StringBuilder();
            var sw = 0;
            foreach (var (ch, cw) in MermaidWidth.Measured(lines[^1]))
            {
                if (sw + cw > target) break;
                s.Append(ch);
                sw += cw;
            }
            lines[^1] = s + "…";
        }
        return lines;
    }

    /// <summary>Truncate to <paramref name="inner"/> columns, leaving room for the ellipsis.</summary>
    public static string FitLabel(string label, int inner)
    {
        if (MermaidWidth.StringWidth(label) <= inner) return label;
        var sb = new StringBuilder();
        var used = 0;
        foreach (var (c, cw) in MermaidWidth.Measured(label))
        {
            if (used + cw + 1 > inner) break;
            sb.Append(c);
            used += cw;
        }
        return sb + "…";
    }
}
