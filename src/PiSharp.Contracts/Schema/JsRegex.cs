// JavaScript regular expressions (with or without the u flag) translated to .NET: the patterns of JSON Schema `pattern` and
// `patternProperties` and of TypeBox's string checks. Moved here from PiSharp.Agent/ToolArgumentValidation.cs (Pi v1.5.0 sync, schema area).
#nullable enable

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Contracts.Schema;

internal static class JsRegex
{
    private static readonly Dictionary<(string, bool), Regex> Cache = new();
    private const string WordClass = "a-zA-Z0-9_";
    private const string AstralPair = @"[\uD800-\uDBFF][\uDC00-\uDFFF]";
    private const string NotLineTerminator = @"[^\n\r\u2028\u2029]";
    private const string SpaceClass = @"\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";

    /// <summary>new RegExp(pattern, unicode ? "u" : "") - throws ArgumentException for patterns that cannot be compiled.</summary>
    public static Regex Create(string pattern, bool unicode)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((pattern, unicode), out var cached)) return cached;
        }

        var regex = new Regex(Translate(pattern, unicode), RegexOptions.CultureInvariant);
        lock (Cache) Cache[(pattern, unicode)] = regex;
        return regex;
    }

    public static bool TryCreate(string pattern, bool unicode, out Regex? regex) => TryCreate(pattern, unicode, out regex, out _);

    /// <summary>As <see cref="TryCreate(string, bool, out Regex?)"/>; <paramref name="reason"/> is V8's SyntaxError reason when the
    /// translation found it (such as "Unterminated character class"), null when only .NET's parser rejected the pattern.</summary>
    public static bool TryCreate(string pattern, bool unicode, out Regex? regex, out string? reason)
    {
        try
        {
            regex = Create(pattern, unicode);
            reason = null;
            return true;
        }
        catch (RegexParseException)
        {
            regex = null;
            reason = null;
            return false;
        }
        catch (ArgumentException error)
        {
            regex = null;
            reason = error.Message.StartsWith("Unsupported", StringComparison.Ordinal) ? null : error.Message;
            return false;
        }
    }

    public static bool Test(string pattern, bool unicode, string input) => Create(pattern, unicode).IsMatch(input);

    private static string Translate(string p, bool unicode)
    {
        var sb = new StringBuilder();
        var inClass = false;
        var classStart = 0;
        var classAstral = false; // u-mode class that can match a whole astral code point (negated / negated shorthand)
        var groupDepth = 0;
        // In u-mode a negated set matches one code point: let it consume a surrogate pair as a unit.
        string Negated(string set)
        {
            if (inClass)
            {
                classAstral = true;
                return set;
            }

            return unicode ? "(?:" + AstralPair + "|" + set + ")" : set;
        }

        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (c == '\\')
            {
                if (i + 1 >= p.Length) throw new ArgumentException("\\ at end of pattern");
                var d = p[++i];
                switch (d)
                {
                    case 'd': sb.Append(inClass ? "0-9" : "[0-9]"); break;
                    case 'D': sb.Append(Negated(inClass ? @"\D" : "[^0-9]")); break;
                    case 'w': sb.Append(inClass ? WordClass : "[" + WordClass + "]"); break;
                    case 'W': sb.Append(Negated(inClass ? @"\W" : "[^" + WordClass + "]")); break;
                    case 's': sb.Append(inClass ? SpaceClass : "[" + SpaceClass + "]"); break;
                    case 'S': sb.Append(Negated(inClass ? @"\S" : "[^" + SpaceClass + "]")); break;
                    case 'b':
                        sb.Append(inClass ? @"\x08" : $"(?:(?<=[{WordClass}])(?![{WordClass}])|(?<![{WordClass}])(?=[{WordClass}]))");
                        break;
                    case 'B':
                        sb.Append(inClass ? "B" : $"(?:(?<=[{WordClass}])(?=[{WordClass}])|(?<![{WordClass}])(?![{WordClass}]))");
                        break;
                    case 'u':
                        if (unicode && i + 1 < p.Length && p[i + 1] == '{')
                        {
                            var close = p.IndexOf('}', i + 2);
                            if (close < 0) throw new ArgumentException("Invalid Unicode escape");
                            var cp = int.Parse(p.AsSpan(i + 2, close - i - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            if (cp > 0x10FFFF) throw new ArgumentException("Invalid Unicode escape");
                            AppendCodePoint(sb, cp, inClass);
                            i = close;
                        }
                        else if (IsHex4(p, i + 1))
                        {
                            sb.Append("\\u").Append(p, i + 1, 4);
                            i += 4;
                        }
                        else
                        {
                            if (unicode) throw new ArgumentException("Invalid Unicode escape");
                            sb.Append('u');
                        }

                        break;
                    case 'p':
                    case 'P':
                        if (!unicode)
                        {
                            sb.Append(d);
                            break;
                        }

                        if (i + 1 >= p.Length || p[i + 1] != '{') throw new ArgumentException("Invalid property name");
                        var end = p.IndexOf('}', i + 2);
                        if (end < 0) throw new ArgumentException("Invalid property name");
                        var property = "\\" + d + "{" + MapProperty(p.Substring(i + 2, end - i - 2)) + "}";
                        sb.Append(d == 'P' ? Negated(property) : property);
                        i = end;
                        break;
                    case 'x':
                        if (i + 2 < p.Length && Uri.IsHexDigit(p[i + 1]) && Uri.IsHexDigit(p[i + 2]))
                        {
                            sb.Append("\\x").Append(p, i + 1, 2);
                            i += 2;
                        }
                        else
                        {
                            if (unicode) throw new ArgumentException("Invalid escape");
                            sb.Append('x');
                        }

                        break;
                    case 'c':
                        if (i + 1 < p.Length && char.IsAsciiLetter(p[i + 1]))
                        {
                            sb.Append("\\c").Append(p[i + 1]);
                            i++;
                        }
                        else sb.Append(@"\\c");

                        break;
                    case 'k':
                        sb.Append("\\k");
                        break;
                    case 't': sb.Append("\\t"); break;
                    case 'n': sb.Append("\\n"); break;
                    case 'v': sb.Append("\\v"); break;
                    case 'f': sb.Append("\\f"); break;
                    case 'r': sb.Append("\\r"); break;
                    case '0':
                        if (i + 1 < p.Length && char.IsAsciiDigit(p[i + 1]))
                        {
                            if (unicode) throw new ArgumentException("Invalid decimal escape");
                            sb.Append("\\0");
                        }
                        else sb.Append(@"\x00");

                        break;
                    default:
                        if (char.IsAsciiDigit(d))
                        {
                            sb.Append('\\').Append(d);
                        }
                        else if (char.IsAsciiLetter(d) || d == '_')
                        {
                            if (unicode) throw new ArgumentException("Invalid escape");
                            sb.Append(Regex.Escape(d.ToString()));
                        }
                        else if (char.IsHighSurrogate(d) && i + 1 < p.Length && char.IsLowSurrogate(p[i + 1]))
                        {
                            AppendLiteralPair(sb, d, p[++i], inClass, unicode);
                        }
                        else
                        {
                            if (unicode && "^$\\.*+?()[]{}|/-".IndexOf(d) < 0) throw new ArgumentException("Invalid escape");
                            sb.Append(Regex.Escape(d.ToString()));
                        }

                        break;
                }

                continue;
            }

            if (inClass)
            {
                if (c == ']')
                {
                    inClass = false;
                    sb.Append(']');
                    if (unicode && classAstral)
                    {
                        sb.Insert(classStart, "(?:" + AstralPair + "|");
                        sb.Append(')');
                    }
                }
                else if (c == '[')
                {
                    sb.Append("\\[");
                }
                else if (char.IsHighSurrogate(c) && i + 1 < p.Length && char.IsLowSurrogate(p[i + 1]))
                {
                    sb.Append(c).Append(p[++i]);
                }
                else sb.Append(c);

                continue;
            }

            switch (c)
            {
                case '[':
                {
                    var negate = i + 1 < p.Length && p[i + 1] == '^';
                    var next = i + (negate ? 2 : 1);
                    if (next < p.Length && p[next] == ']')
                    {
                        sb.Append(negate ? unicode ? "(?:" + AstralPair + @"|[\s\S])" : @"[\s\S]" : "(?!)");
                        i = next;
                        break;
                    }

                    inClass = true;
                    classStart = sb.Length;
                    classAstral = negate;
                    sb.Append(negate ? "[^" : "[");
                    i = next - 1;
                    break;
                }
                case '.':
                    sb.Append(unicode ? "(?:" + AstralPair + "|" + NotLineTerminator + ")" : NotLineTerminator);
                    break;
                case '$':
                    sb.Append(@"\z");
                    break;
                case '(':
                    if (i + 1 < p.Length && p[i + 1] == '?') CheckGroupSyntax(p, i + 2);
                    groupDepth++;
                    sb.Append('(');
                    break;
                case ')':
                    if (groupDepth == 0) throw new ArgumentException("Unmatched ')'");
                    groupDepth--;
                    sb.Append(')');
                    break;
                case '{':
                {
                    var quantifier = QuantifierAt(p, i);
                    if (quantifier is not null)
                    {
                        sb.Append(quantifier);
                        i += quantifier.Length - 1;
                    }
                    else
                    {
                        if (unicode) throw new ArgumentException("Lone quantifier brackets");
                        sb.Append("\\{");
                    }

                    break;
                }
                case '}':
                    if (unicode) throw new ArgumentException("Lone quantifier brackets");
                    sb.Append("\\}");
                    break;
                case ']':
                    if (unicode) throw new ArgumentException("Lone ']'");
                    sb.Append("\\]");
                    break;
                case ' ':
                case '#':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < p.Length && char.IsLowSurrogate(p[i + 1]))
                    {
                        AppendLiteralPair(sb, c, p[++i], false, unicode);
                    }
                    else sb.Append(c);

                    break;
            }
        }

        if (inClass) throw new ArgumentException("Unterminated character class");
        if (groupDepth > 0) throw new ArgumentException("Unterminated group");
        return sb.ToString();
    }

    private static string? QuantifierAt(string p, int i)
    {
        var m = Regex.Match(p.Substring(i), @"^\{[0-9]+(,[0-9]*)?\}");
        return m.Success ? m.Value : null;
    }

    private static bool IsHex4(string p, int start)
    {
        if (start + 4 > p.Length) return false;
        for (var k = 0; k < 4; k++) if (!Uri.IsHexDigit(p[start + k])) return false;
        return true;
    }

    private static void AppendLiteralPair(StringBuilder sb, char hi, char lo, bool inClass, bool unicode)
    {
        if (unicode && !inClass) sb.Append("(?:").Append(hi).Append(lo).Append(')');
        else sb.Append(hi).Append(lo);
    }

    private static void AppendCodePoint(StringBuilder sb, int cp, bool inClass)
    {
        if (cp <= 0xFFFF)
        {
            sb.Append("\\u").Append(cp.ToString("X4", CultureInfo.InvariantCulture));
            return;
        }

        var s = char.ConvertFromUtf32(cp);
        var pair = $"\\u{(int)s[0]:X4}\\u{(int)s[1]:X4}";
        sb.Append(inClass ? pair : "(?:" + pair + ")");
    }

    private static readonly Dictionary<string, string> GeneralCategories = new(StringComparer.Ordinal)
    {
        ["L"] = "L", ["Letter"] = "L", ["Lu"] = "Lu", ["Uppercase_Letter"] = "Lu", ["Ll"] = "Ll", ["Lowercase_Letter"] = "Ll",
        ["Lt"] = "Lt", ["Titlecase_Letter"] = "Lt", ["Lm"] = "Lm", ["Modifier_Letter"] = "Lm", ["Lo"] = "Lo", ["Other_Letter"] = "Lo",
        ["M"] = "M", ["Mark"] = "M", ["Combining_Mark"] = "M", ["Mn"] = "Mn", ["Nonspacing_Mark"] = "Mn", ["Mc"] = "Mc",
        ["Spacing_Mark"] = "Mc", ["Me"] = "Me", ["Enclosing_Mark"] = "Me", ["N"] = "N", ["Number"] = "N", ["Nd"] = "Nd",
        ["Decimal_Number"] = "Nd", ["digit"] = "Nd", ["Nl"] = "Nl", ["Letter_Number"] = "Nl", ["No"] = "No", ["Other_Number"] = "No",
        ["P"] = "P", ["Punctuation"] = "P", ["punct"] = "P", ["Pc"] = "Pc", ["Connector_Punctuation"] = "Pc", ["Pd"] = "Pd",
        ["Dash_Punctuation"] = "Pd", ["Ps"] = "Ps", ["Open_Punctuation"] = "Ps", ["Pe"] = "Pe", ["Close_Punctuation"] = "Pe",
        ["Pi"] = "Pi", ["Initial_Punctuation"] = "Pi", ["Pf"] = "Pf", ["Final_Punctuation"] = "Pf", ["Po"] = "Po",
        ["Other_Punctuation"] = "Po", ["S"] = "S", ["Symbol"] = "S", ["Sm"] = "Sm", ["Math_Symbol"] = "Sm", ["Sc"] = "Sc",
        ["Currency_Symbol"] = "Sc", ["Sk"] = "Sk", ["Modifier_Symbol"] = "Sk", ["So"] = "So", ["Other_Symbol"] = "So",
        ["Z"] = "Z", ["Separator"] = "Z", ["Zs"] = "Zs", ["Space_Separator"] = "Zs", ["Zl"] = "Zl", ["Line_Separator"] = "Zl",
        ["Zp"] = "Zp", ["Paragraph_Separator"] = "Zp", ["C"] = "C", ["Other"] = "C", ["Cc"] = "Cc", ["Control"] = "Cc",
        ["cntrl"] = "Cc", ["Cf"] = "Cf", ["Format"] = "Cf", ["Cs"] = "Cs", ["Surrogate"] = "Cs", ["Co"] = "Co",
        ["Private_Use"] = "Co", ["Cn"] = "Cn", ["Unassigned"] = "Cn",
    };

    private static readonly Dictionary<string, string> Scripts = new(StringComparer.Ordinal)
    {
        ["Greek"] = "IsGreekandCoptic", ["Grek"] = "IsGreekandCoptic", ["Cyrillic"] = "IsCyrillic", ["Cyrl"] = "IsCyrillic",
        ["Arabic"] = "IsArabic", ["Arab"] = "IsArabic", ["Hebrew"] = "IsHebrew", ["Hebr"] = "IsHebrew", ["Han"] = "IsCJKUnifiedIdeographs",
        ["Hani"] = "IsCJKUnifiedIdeographs", ["Hiragana"] = "IsHiragana", ["Hira"] = "IsHiragana", ["Katakana"] = "IsKatakana",
        ["Kana"] = "IsKatakana", ["Thai"] = "IsThai", ["Hangul"] = "IsHangulSyllables", ["Hang"] = "IsHangulSyllables",
    };

    // After "(?" JavaScript allows only (?: (?= (?! (?<= (?<! (?<name> and a modifiers group such as (?i: or (?-s:; .NET also takes
    // inline options such as (?i), comments, atomic groups and conditionals, which are syntax errors in JavaScript.
    private static void CheckGroupSyntax(string p, int j)
    {
        if (j < p.Length && p[j] is ':' or '=' or '!' or '<') return;
        var start = j;
        while (j < p.Length && p[j] is 'i' or 'm' or 's') j++;
        if (j < p.Length && p[j] == '-')
        {
            j++;
            while (j < p.Length && p[j] is 'i' or 'm' or 's') j++;
        }

        if (j == start || j >= p.Length || p[j] != ':') throw new ArgumentException("Invalid group");
    }

    private static string MapProperty(string name)
    {
        var eq = name.IndexOf('=');
        if (eq >= 0)
        {
            var key = name[..eq];
            var value = name[(eq + 1)..];
            if (key is "General_Category" or "gc" && GeneralCategories.TryGetValue(value, out var gc)) return gc;
            if (key is "Script" or "sc" or "Script_Extensions" or "scx" && Scripts.TryGetValue(value, out var sc)) return sc;
            throw new ArgumentException($"Unsupported Unicode property {name}");
        }

        if (GeneralCategories.TryGetValue(name, out var cat)) return cat;
        throw new ArgumentException($"Unsupported Unicode property {name}");
    }
}
