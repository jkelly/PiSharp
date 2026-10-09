// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/syntax-highlight.ts (renderHighlightedHtml) and packages/coding-agent/src/utils/html.ts (decodeHtmlEntityAt) — rendering of highlight.js 10.7.3 (BSD-3-Clause) HTML output.
using System.Text;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>Converts hljs HTML into themed text: scope lookup exact → prefix before '.' → prefix before '-',
/// innermost span first, then <c>default</c>; formatted text is split on '\n' and only non-empty lines are formatted.</summary>
internal static class HighlightRenderer
{
    const string SpanClose = "</span>";
    const string HighlightClassPrefix = "hljs-";

    public static string Render(string html, IReadOnlyDictionary<string, Func<string, string>>? theme = null)
    {
        theme ??= new Dictionary<string, Func<string, string>>();
        var output = new StringBuilder(html.Length);
        var textBuffer = new StringBuilder();
        var scopes = new List<string?>();

        void FlushText()
        {
            if (textBuffer.Length == 0) return;
            var formatter = GetActiveFormatter(scopes, theme);
            var text = textBuffer.ToString();
            if (formatter == null) output.Append(text);
            else
            {
                var lines = text.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i > 0) output.Append('\n');
                    output.Append(lines[i].Length > 0 ? formatter(lines[i]) : lines[i]);
                }
            }
            textBuffer.Clear();
        }

        var index = 0;
        while (index < html.Length)
        {
            if (IsSpanOpenTagStart(html, index))
            {
                var tagEndIndex = html.IndexOf('>', index + 5);
                if (tagEndIndex != -1)
                {
                    FlushText();
                    scopes.Add(GetScopeFromSpanTag(html.Substring(index, tagEndIndex + 1 - index)));
                    index = tagEndIndex + 1;
                    continue;
                }
            }
            if (string.CompareOrdinal(html, index, SpanClose, 0, SpanClose.Length) == 0)
            {
                FlushText();
                if (scopes.Count > 0) scopes.RemoveAt(scopes.Count - 1);
                index += SpanClose.Length;
                continue;
            }
            if (html[index] == '&' && DecodeHtmlEntityAt(html, index) is { } decoded)
            {
                textBuffer.Append(decoded.Text);
                index += decoded.Length;
                continue;
            }
            textBuffer.Append(html[index]);
            index++;
        }
        FlushText();
        return output.ToString();
    }

    static bool IsSpanOpenTagStart(string html, int index)
    {
        if (string.CompareOrdinal(html, index, "<span", 0, 5) != 0 || index + 5 > html.Length) return false;
        if (index + 5 >= html.Length) return false;
        var next = html[index + 5];
        return next is '>' or ' ' or '\t' or '\n' or '\r';
    }

    /// <summary><c>/\sclass\s*=\s*(?:"([^"]*)"|'([^']*)')/</c>, then the first class starting with <c>hljs-</c>.</summary>
    static string? GetScopeFromSpanTag(string tag)
    {
        for (var i = 0; i < tag.Length; i++)
        {
            if (!IsJsSpace(tag[i]) || string.CompareOrdinal(tag, i + 1, "class", 0, 5) != 0 || i + 6 > tag.Length) continue;
            var j = i + 6;
            while (j < tag.Length && IsJsSpace(tag[j])) j++;
            if (j >= tag.Length || tag[j] != '=') continue;
            j++;
            while (j < tag.Length && IsJsSpace(tag[j])) j++;
            if (j >= tag.Length || tag[j] is not ('"' or '\'')) continue;
            var close = tag.IndexOf(tag[j], j + 1);
            if (close < 0) continue;
            var classValue = tag.Substring(j + 1, close - j - 1);
            if (classValue.Length == 0) return null;
            foreach (var className in SplitJsWhitespace(classValue))
                if (className.StartsWith(HighlightClassPrefix, StringComparison.Ordinal)) return className[HighlightClassPrefix.Length..];
            return null;
        }
        return null;
    }

    /// <summary>JS <c>str.split(/\s+/)</c>.</summary>
    static IEnumerable<string> SplitJsWhitespace(string s)
    {
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (!IsJsSpace(s[i])) continue;
            var end = i;
            while (i + 1 < s.Length && IsJsSpace(s[i + 1])) i++;
            yield return s[start..end];
            start = i + 1;
        }
        yield return s[start..];
    }

    static bool IsJsSpace(char c) => c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00A0' or '\u1680' or (>= '\u2000' and <= '\u200A')
        or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF';

    static Func<string, string>? GetScopeFormatter(string scope, IReadOnlyDictionary<string, Func<string, string>> theme)
    {
        if (Lookup(theme, scope) is { } exact) return exact;
        var dot = scope.IndexOf('.');
        if (dot != -1 && Lookup(theme, scope[..dot]) is { } byDot) return byDot;
        var dash = scope.IndexOf('-');
        if (dash != -1 && Lookup(theme, scope[..dash]) is { } byDash) return byDash;
        return null;
    }

    /// <summary>
    /// JS <c>theme[key]</c> on Pi's theme, a plain object literal: keys it does not define fall through to
    /// <c>Object.prototype</c>. hljs emits <c>hljs-constructor</c> (reasonml), so <c>constructor</c> resolves to <c>Object</c>,
    /// which formats as the identity; the other members behave as they do when called as <c>formatter(line)</c>.
    /// </summary>
    static Func<string, string>? Lookup(IReadOnlyDictionary<string, Func<string, string>> theme, string key)
    {
        if (theme.TryGetValue(key, out var own)) return own;
        return key switch
        {
            "constructor" => static s => s,
            "toString" => static _ => "[object Undefined]",
            "isPrototypeOf" => static _ => "false",
            "toLocaleString" or "valueOf" or "hasOwnProperty" or "propertyIsEnumerable" or "__defineGetter__" or "__defineSetter__"
                or "__lookupGetter__" or "__lookupSetter__" => static _ => throw new InvalidOperationException("TypeError: Cannot convert undefined or null to object"),
            "__proto__" => static _ => throw new InvalidOperationException("TypeError: formatter is not a function"),
            _ => null,
        };
    }

    static Func<string, string>? GetActiveFormatter(List<string?> scopes, IReadOnlyDictionary<string, Func<string, string>> theme)
    {
        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            var scope = scopes[i];
            if (string.IsNullOrEmpty(scope)) continue;
            if (GetScopeFormatter(scope, theme) is { } f) return f;
        }
        return theme.TryGetValue("default", out var d) ? d : null;
    }

    /// <summary>html.ts <c>decodeHtmlEntityAt</c>.</summary>
    public static (string Text, int Length)? DecodeHtmlEntityAt(string html, int index)
    {
        var semicolonIndex = html.IndexOf(';', index + 1);
        if (semicolonIndex == -1 || semicolonIndex - index > 16) return null;
        var decoded = DecodeHtmlEntity(html.Substring(index + 1, semicolonIndex - index - 1));
        return decoded == null ? null : (decoded, semicolonIndex - index + 1);
    }

    /// <summary>html.ts <c>decodeHtmlEntity</c>.</summary>
    public static string? DecodeHtmlEntity(string entity)
    {
        switch (entity)
        {
            case "amp": return "&";
            case "lt": return "<";
            case "gt": return ">";
            case "quot": return "\"";
            case "apos": return "'";
        }
        if (entity.StartsWith("#x", StringComparison.Ordinal) || entity.StartsWith("#X", StringComparison.Ordinal))
            return DecodeCodePoint(JsParseInt(entity[2..], 16));
        if (entity.StartsWith('#')) return DecodeCodePoint(JsParseInt(entity[1..], 10));
        return null;
    }

    static string? DecodeCodePoint(double codePoint)
    {
        if (double.IsNaN(codePoint) || double.IsInfinity(codePoint) || codePoint != Math.Floor(codePoint) || codePoint < 0 || codePoint > 0x10FFFF)
            return null;
        var cp = (int)codePoint;
        return cp < 0x10000 ? ((char)cp).ToString() : char.ConvertFromUtf32(cp);
    }

    /// <summary>JS <c>Number.parseInt(s, radix)</c> for radix 10/16.</summary>
    static double JsParseInt(string s, int radix)
    {
        var i = 0;
        while (i < s.Length && IsJsSpace(s[i])) i++;
        var sign = 1;
        if (i < s.Length && s[i] is '+' or '-')
        {
            if (s[i] == '-') sign = -1;
            i++;
        }
        if (radix == 16 && i + 1 < s.Length && s[i] == '0' && (s[i + 1] | 0x20) == 'x') i += 2;
        double value = 0;
        var any = false;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            var d = char.IsAsciiDigit(c) ? c - '0' : char.IsAsciiLetter(c) ? (c | 0x20) - 'a' + 10 : 99;
            if (d >= radix) break;
            value = value * radix + d;
            any = true;
        }
        return any ? sign * value : double.NaN;
    }
}
