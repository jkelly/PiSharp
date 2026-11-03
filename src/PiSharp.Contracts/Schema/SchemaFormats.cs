// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/src/formats/index.ts, packages/schema/src/formats/typebox
// (adapted there from TypeBox 1.3.27, MIT, Copyright (c) 2017-2026 Haydn Paterson; see THIRD-PARTY-NOTICES.md)
//
// The format checks of JSON Schema 2020-12 plus url and json-pointer-uri-fragment. The JavaScript regular expressions are ASCII patterns,
// some with the i flag: they compile here with explicit letter cases (no culture-dependent case folding) and \z for $. URL.canParse is
// JsUrl. Unicode scripts, which .NET regular expressions lack, are code point tables; only letters and marks need to be exact, because a
// label with any other character outside the permitted categories fails either way.
#nullable enable

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Contracts.Schema;

internal static class SchemaFormats
{
    private const string V4 = @"(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)";

    private const string H6 =
        @"(?:(?:(?:[\da-f]{1,4}:){6}(?:[\da-f]{1,4}:[\da-f]{1,4}|" + V4 + @")|::(?:[\da-f]{1,4}:){5}(?:[\da-f]{1,4}:[\da-f]{1,4}|" + V4 +
        @")|(?:[\da-f]{1,4})?::(?:[\da-f]{1,4}:){4}(?:[\da-f]{1,4}:[\da-f]{1,4}|" + V4 + @")|(?:(?:[\da-f]{1,4}:){0,1}[\da-f]{1,4})?::(?:[\da-f]{1,4}:){3}(?:[\da-f]{1,4}:[\da-f]{1,4}|" + V4 +
        @")|(?:(?:[\da-f]{1,4}:){0,2}[\da-f]{1,4})?::(?:[\da-f]{1,4}:){2}(?:[\da-f]{1,4}:[\da-f]{1,4}|" + V4 + @")|(?:(?:[\da-f]{1,4}:){0,3}[\da-f]{1,4})?::[\da-f]{1,4}:(?:[\da-f]{1,4}:[\da-f]{1,4}|" + V4 +
        @")|(?:(?:[\da-f]{1,4}:){0,4}[\da-f]{1,4})?::(?:[\da-f]{1,4}:[\da-f]{1,4}|" + V4 + @")|(?:(?:[\da-f]{1,4}:){0,5}[\da-f]{1,4})?::[\da-f]{1,4}|(?:(?:[\da-f]{1,4}:){0,6}[\da-f]{1,4})?::)|v[0-9a-f]+\.[-a-z0-9._~!$&'()*+,;=:]+)";

    private const string Authority = @"(?:(?:[-a-z0-9._~!$&'()*+,;=:]|%[0-9a-f]{2})*@)?(?:\[" + H6 + @"\]|" + V4 + @"|(?:[-a-z0-9._~!$&'()*+,;=]|%[0-9a-f]{2})*)(?::\d*)?";
    private const string Segment = @"(?:[-a-z0-9._~!$&'()*+,;=:@]|%[0-9a-f]{2})";
    private const string Hier = @"\/\/" + Authority + @"(?:\/" + Segment + @"*)*|\/(?:" + Segment + @"+(?:\/" + Segment + @"*)*)?";
    private const string QueryFragment = @"(?:\?(?:[-a-z0-9._~!$&'()*+,;=:@/?]|%[0-9a-f]{2})*)?(?:#(?:[-a-z0-9._~!$&'()*+,;=:@/?]|%[0-9a-f]{2})*)?";

    private static readonly Regex Date = JsPattern(@"^(\d\d\d\d)-(\d\d)-(\d\d)$");
    private static readonly Regex Time = JsPattern(@"^(\d\d):(\d\d):(\d\d)(?:\.\d+)?(?:([Zz])|([+-])(\d\d):(\d\d))?$");
    private static readonly Regex Duration = JsPattern(@"^P((\d+Y(\d+M(\d+D)?)?|\d+M(\d+D)?|\d+D)(T(\d+H(\d+M(\d+S)?)?|\d+M(\d+S)?|\d+S))?|T(\d+H(\d+M(\d+S)?)?|\d+M(\d+S)?|\d+S)|\d+W)$");
    private static readonly Regex Email = JsPattern(
        @"^(?:[a-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[a-z0-9!#$%&'*+/=?^_`{|}~-]+)*|""(?:[^""\\]|\\[\x20-\x7e])*"")@(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*|\[(?:IPv6:[a-f0-9:]+|(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])(?:\.(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])){3})\])$",
        ignoreCase: true);
    private static readonly Regex IPv4 = JsPattern("^" + V4 + "$");
    private static readonly Regex IPv6 = JsPattern(
        @"^(?:(?:(?:[0-9a-f]{1,4}:){6}|::(?:[0-9a-f]{1,4}:){5}|(?:[0-9a-f]{1,4})?::(?:[0-9a-f]{1,4}:){4}|(?:(?:[0-9a-f]{1,4}:)?[0-9a-f]{1,4})?::(?:[0-9a-f]{1,4}:){3}|(?:(?:[0-9a-f]{1,4}:){0,2}[0-9a-f]{1,4})?::(?:[0-9a-f]{1,4}:){2}|(?:(?:[0-9a-f]{1,4}:){0,3}[0-9a-f]{1,4})?::[0-9a-f]{1,4}:|(?:(?:[0-9a-f]{1,4}:){0,4}[0-9a-f]{1,4})?::)(?:[0-9a-f]{1,4}:[0-9a-f]{1,4}|" + V4 + @")|(?:(?:[0-9a-f]{1,4}:){0,5}[0-9a-f]{1,4})?::[0-9a-f]{1,4}|(?:(?:[0-9a-f]{1,4}:){0,6}[0-9a-f]{1,4})?::)$",
        ignoreCase: true);
    private static readonly Regex JsonPointer = JsPattern(@"^(?:\/(?:[^~/]|~0|~1)*)*$");
    private static readonly Regex JsonPointerUriFragment = JsPattern(@"^#(?:\/(?:[a-z0-9_\-.!$&'()*+,;:=@]|%[0-9a-f]{2}|~0|~1)*)*$", ignoreCase: true);
    private static readonly Regex RelativeJsonPointer = JsPattern(@"^(?:0|[1-9][0-9]*)(?:#|(?:\/(?:[^~/]|~0|~1)*)*)$");
    private static readonly Regex Uri = JsPattern(@"^[a-z][a-z0-9+\-.]*:(?:" + Hier + "|" + Segment + @"+(?:\/" + Segment + @"*)*)?" + QueryFragment + "$", ignoreCase: true);
    private static readonly Regex UriReference = JsPattern(
        @"^(?:[a-z][a-z0-9+\-.]*:(?:" + Hier + "|" + Segment + @"+(?:\/" + Segment + @"*)*)?|(?:" + Hier + @"|(?:[-a-z0-9._~!$&'()*+,;=@]|%[0-9a-f]{2})+(?:\/" + Segment + @"*)*)?)" + QueryFragment + "$",
        ignoreCase: true);
    private static readonly Regex UriTemplate = JsPattern(
        @"^(?:(?:[^\x00-\x20""<>%\\^`{|}\x7f]|%[0-9a-f]{2})|\{[+#./;?&=,!@|]?(?:[a-z0-9_]|%[0-9a-f]{2})+(?:\.(?:[a-z0-9_]|%[0-9a-f]{2})+)*(?::[1-9]\d{0,3}|\*)?(?:,(?:[a-z0-9_]|%[0-9a-f]{2})+(?:\.(?:[a-z0-9_]|%[0-9a-f]{2})+)*(?::[1-9]\d{0,3}|\*)?)*\})*$",
        ignoreCase: true);
    private static readonly Regex Uuid = JsPattern(@"^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$", ignoreCase: true);
    private static readonly Regex IpvFutureMatch = JsPattern(@"\[[vV][0-9a-fA-F]+\.[^\]]+\]");
    private static readonly Regex InvalidIriChars = JsPattern(@"[\x00-\x20<>^`{|}\\]");
    private static readonly Regex InvalidPercentEncoding = JsPattern("%(?![0-9a-fA-F]{2})");
    private static readonly Regex InvalidIriReferenceChars = JsPattern(@"[\x00-\x20\x7F\\]|%(?![0-9a-fA-F]{2})");
    private static readonly Regex MalformedScheme = JsPattern(@"^[a-zA-Z][a-zA-Z0-9+\-.]*\/\/");
    private static readonly int[] Days = [0, 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    /// <summary>Every format JSON Schema 2020-12 defines, by name, plus <c>url</c> and <c>json-pointer-uri-fragment</c>.</summary>
    public static readonly IReadOnlyDictionary<string, Func<string, bool>> All = new Dictionary<string, Func<string, bool>>(StringComparer.Ordinal)
    {
        ["date"] = IsDate,
        ["date-time"] = IsDateTime,
        ["duration"] = v => Duration.IsMatch(v),
        ["email"] = v => Email.IsMatch(v),
        ["hostname"] = Idna.IsHostname,
        ["idn-email"] = IsIdnEmail,
        ["idn-hostname"] = Idna.IsIdnHostname,
        ["ipv4"] = v => IPv4.IsMatch(v),
        ["ipv6"] = v => IPv6.IsMatch(v),
        ["iri"] = IsIri,
        ["iri-reference"] = IsIriReference,
        ["json-pointer"] = v => JsonPointer.IsMatch(v),
        ["json-pointer-uri-fragment"] = v => JsonPointerUriFragment.IsMatch(v),
        ["regex"] = v => JsRegex.TryCreate(v, true, out _),
        ["relative-json-pointer"] = v => RelativeJsonPointer.IsMatch(v),
        ["time"] = v => IsTime(v),
        ["uri"] = v => Uri.IsMatch(v),
        ["uri-reference"] = v => UriReference.IsMatch(v),
        ["uri-template"] = v => UriTemplate.IsMatch(v),
        ["url"] = v => JsUrl.CanParse(v),
        ["uuid"] = v => Uuid.IsMatch(v),
    };

    /// <summary>
    /// A JavaScript regular expression over ASCII (no u flag) as a .NET one: \d is [0-9], $ is the end of the input, and with
    /// <paramref name="ignoreCase"/> every ASCII letter also matches its other case, and only that.
    /// </summary>
    public static Regex JsPattern(string pattern, bool ignoreCase = false)
    {
        var sb = new StringBuilder(pattern.Length * 2);
        var inClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                var d = pattern[++i];
                switch (d)
                {
                    case 'd':
                        sb.Append(inClass ? "0-9" : "[0-9]");
                        break;
                    case 'x':
                        sb.Append(@"\x").Append(pattern, i + 1, 2);
                        i += 2;
                        break;
                    case 'u':
                        sb.Append(@"\u").Append(pattern, i + 1, 4);
                        i += 4;
                        break;
                    default:
                        sb.Append('\\').Append(d);
                        break;
                }

                continue;
            }

            if (inClass)
            {
                if (c == ']') inClass = false;
                if (ignoreCase && char.IsAsciiLetter(c))
                {
                    if (i + 2 < pattern.Length && pattern[i + 1] == '-' && char.IsAsciiLetter(pattern[i + 2]))
                    {
                        var end = pattern[i + 2];
                        sb.Append(c).Append('-').Append(end).Append(OtherCase(c)).Append('-').Append(OtherCase(end));
                        i += 2;
                    }
                    else sb.Append(c).Append(OtherCase(c));

                    continue;
                }

                sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '[':
                    inClass = true;
                    sb.Append(c);
                    if (i + 1 < pattern.Length && pattern[i + 1] == '^') sb.Append(pattern[++i]);
                    break;
                case '$':
                    sb.Append(@"\z");
                    break;
                default:
                    if (ignoreCase && char.IsAsciiLetter(c)) sb.Append('[').Append(c).Append(OtherCase(c)).Append(']');
                    else sb.Append(c);
                    break;
            }
        }

        return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
    }

    private static char OtherCase(char c) => char.IsAsciiLetterUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c);

    private static bool IsLeapYear(int year) => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

    /// <summary>An RFC 3339 full-date.</summary>
    public static bool IsDate(string value)
    {
        var m = Date.Match(value);
        if (!m.Success) return false;
        var year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return month >= 1 && month <= 12 && day >= 1 && day <= (month == 2 && IsLeapYear(year) ? 29 : Days[month]);
    }

    /// <summary>An RFC 3339 full-time; a leap second must be 23:59:60 UTC.</summary>
    public static bool IsTime(string value, bool strictTimeZone = true)
    {
        var m = Time.Match(value);
        if (!m.Success) return false;
        if (strictTimeZone && !m.Groups[4].Success && !m.Groups[5].Success) return false;
        var hr = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var sec = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (hr > 23 || min > 59 || sec > 60) return false;
        if (m.Groups[5].Success)
        {
            if (int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) > 23 || int.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture) > 59) return false;
        }

        if (sec < 60) return true;
        var tzSign = m.Groups[5].Value == "-" ? -1 : 1;
        var tzH = m.Groups[6].Success ? int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) : 0;
        var tzM = m.Groups[7].Success ? int.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture) : 0;
        var totalUtcMin = hr * 60 + min - tzSign * (tzH * 60 + tzM);
        return (totalUtcMin % 1440 + 1440) % 1440 == 1439;
    }

    /// <summary>An RFC 3339 date-time: <c>value.split(/T/i)</c> into a date and a time.</summary>
    public static bool IsDateTime(string value)
    {
        var parts = value.Split('T', 't');
        return parts.Length == 2 && IsDate(parts[0]) && IsTime(parts[1]);
    }

    /// <summary>An RFC 3987 IRI: no unencoded whitespace or controls, no malformed percent-encoding, and URL.canParse (IPvFuture narrowed).</summary>
    public static bool IsIri(string value)
    {
        if (InvalidIriChars.IsMatch(value)) return false;
        if (InvalidPercentEncoding.IsMatch(value)) return false;
        // URL.canParse rejects IPvFuture literals, which RFC 3987 allows: they become [::1] (only below 2048 characters).
        var narrowed = value.Length < 2048 ? IpvFutureMatch.Replace(value, "[::1]", 1) : value;
        return JsUrl.CanParse(narrowed);
    }

    /// <summary>An RFC 3987 IRI reference.</summary>
    public static bool IsIriReference(string value) =>
        !InvalidIriReferenceChars.IsMatch(value) && !MalformedScheme.IsMatch(value) && JsUrl.CanParse(value, "http://example.com");

    /// <summary>
    /// An RFC 4952 internationalized email address, after NFC: a dot-atom of ASCII atext and non-ASCII characters or a quoted string,
    /// then labels of letters, digits and hyphens (one to 63 code points, not ending in a hyphen). The JavaScript pattern uses the u and
    /// i flags; it is followed here in code, by code points.
    /// </summary>
    public static bool IsIdnEmail(string value)
    {
        var s = Idna.NormalizeNfc(value);
        int at;
        if (s.Length > 0 && s[0] == '"')
        {
            var i = 1;
            while (true)
            {
                if (i >= s.Length) return false;
                var c = s[i];
                if (c == '"') break;
                if (c == '\\')
                {
                    // \\. : any code point but a line terminator.
                    if (i + 1 >= s.Length || s[i + 1] is '\n' or '\r' or (char)0x2028 or (char)0x2029) return false;
                    i += char.IsHighSurrogate(s[i + 1]) && i + 2 < s.Length && char.IsLowSurrogate(s[i + 2]) ? 3 : 2;
                    continue;
                }

                i++;
            }

            at = i + 1;
            if (at >= s.Length || s[at] != '@') return false;
        }
        else
        {
            at = s.IndexOf('@');
            if (at <= 0) return false;
            foreach (var atom in s[..at].Split('.'))
            {
                if (atom.Length == 0) return false;
                foreach (var c in atom)
                {
                    if (c >= 0x80) continue;
                    if (!char.IsAsciiLetterOrDigit(c) && "!#$%&'*+/=?^_`{|}~-".IndexOf(c) < 0) return false;
                }
            }
        }

        foreach (var label in s[(at + 1)..].Split('.'))
        {
            var points = Idna.CodePoints(label);
            if (points.Count is 0 or > 63) return false;
            if (!IsLetterOrNumber(points[0]) || points[^1] == '-') return false;
            if (points.Any(p => p != '-' && !IsLetterOrNumber(p))) return false;
        }

        return true;
    }

    private static bool IsLetterOrNumber(int codePoint) => CharUnicodeInfo.GetUnicodeCategory(codePoint) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };
}

/// <summary>TypeBox's IDNA checks (formats/typebox/idna): hostname and idn-hostname labels, Punycode (RFC 3492) and the Bidi Rule (RFC 5893).</summary>
internal static class Idna
{
    private const int PunyBase = 36, PunyTMin = 1, PunyTMax = 26, PunySkew = 38, PunyDamp = 700, PunyInitialBias = 72, PunyInitialN = 128;

    private static readonly HashSet<int> Virama =
    [
        0x094d, 0x09cd, 0x0a4d, 0x0acd, 0x0b4d, 0x0bcd, 0x0c4d, 0x0ccd, 0x0d3b, 0x0d3c, 0x0d4d, 0x0dca, 0x1b44, 0x1baa, 0x1bab, 0xa9c0,
        0x11046, 0x1107f, 0x110b9, 0x11133, 0x11134, 0x111c0, 0x11235, 0x1134d, 0x11442, 0x114c2, 0x115bf, 0x1163f, 0x116b6, 0x11c3f, 0x11d44, 0x11d45,
    ];

    private static readonly HashSet<int> Rfc5892Disallowed = [0x0640, 0x07fa, 0x302e, 0x302f, 0x3031, 0x3032, 0x3033, 0x3034, 0x3035, 0x303b];
    private static readonly HashSet<int> ContextOExceptions = [0x00b7, 0x0375, 0x05f3, 0x05f4, 0x200c, 0x200d, 0x30fb];
    private static readonly HashSet<int> PValidExceptions = [0x00df, 0x03c2, 0x06fd, 0x06fe, 0x0f0b, 0x3007];

    // Script=Greek (Scripts.txt), without the Common characters of the Greek block (U+0374, U+037E, U+0385, U+0387).
    private static readonly int[] Greek =
    [
        0x0370, 0x0373, 0x0375, 0x0377, 0x037A, 0x037D, 0x037F, 0x037F, 0x0384, 0x0384, 0x0386, 0x0386, 0x0388, 0x038A, 0x038C, 0x038C,
        0x038E, 0x03A1, 0x03A3, 0x03E1, 0x03F0, 0x03FF, 0x1D26, 0x1D2A, 0x1D5D, 0x1D61, 0x1D66, 0x1D6A, 0x1DBF, 0x1DBF, 0x1F00, 0x1F15,
        0x1F18, 0x1F1D, 0x1F20, 0x1F45, 0x1F48, 0x1F4D, 0x1F50, 0x1F57, 0x1F59, 0x1F59, 0x1F5B, 0x1F5B, 0x1F5D, 0x1F5D, 0x1F5F, 0x1F7D,
        0x1F80, 0x1FB4, 0x1FB6, 0x1FC4, 0x1FC6, 0x1FD3, 0x1FD6, 0x1FDB, 0x1FDD, 0x1FEF, 0x1FF2, 0x1FF4, 0x1FF6, 0x1FFE, 0x2126, 0x2126,
        0xAB65, 0xAB65, 0x10140, 0x1018E, 0x101A0, 0x101A0, 0x1D200, 0x1D245,
    ];

    // Script=Hebrew.
    private static readonly int[] Hebrew =
    [
        0x0591, 0x05C7, 0x05D0, 0x05EA, 0x05EF, 0x05F4, 0xFB1D, 0xFB36, 0xFB38, 0xFB3C, 0xFB3E, 0xFB3E, 0xFB40, 0xFB41, 0xFB43, 0xFB44,
        0xFB46, 0xFB4F,
    ];

    // Script=Hiragana, Katakana and Han; the kana prolonged sound marks and voicing marks (U+30FC, U+FF70, U+FF9E, U+FF9F, U+309B,
    // U+309C) and the ideographic closing mark (U+3006) are Common, the middle dot U+30FB too.
    private static readonly int[] Japanese =
    [
        0x2E80, 0x2E99, 0x2E9B, 0x2EF3, 0x2F00, 0x2FD5, 0x3005, 0x3005, 0x3007, 0x3007, 0x3021, 0x3029, 0x3038, 0x303B, 0x3041, 0x3096,
        0x309D, 0x309F, 0x30A1, 0x30FA, 0x30FD, 0x30FF, 0x31F0, 0x31FF, 0x32D0, 0x32FE, 0x3300, 0x3357, 0x3400, 0x4DBF, 0x4E00, 0x9FFF,
        0xF900, 0xFA6D, 0xFA70, 0xFAD9, 0xFF66, 0xFF6F, 0xFF71, 0xFF9D, 0x16FE2, 0x16FE3, 0x16FF0, 0x16FF1, 0x1AFF0, 0x1AFF3, 0x1AFF5, 0x1AFFB,
        0x1AFFD, 0x1AFFE, 0x1B000, 0x1B122, 0x1B132, 0x1B132, 0x1B150, 0x1B152, 0x1B155, 0x1B155, 0x1B164, 0x1B167, 0x1F200, 0x1F200,
        0x20000, 0x2A6DF, 0x2A700, 0x2B739, 0x2B740, 0x2B81D, 0x2B820, 0x2CEA1, 0x2CEB0, 0x2EBE0, 0x2EBF0, 0x2EE5D, 0x2F800, 0x2FA1D,
        0x30000, 0x3134A, 0x31350, 0x323AF,
    ];

    // Script=Arabic, Syriac, Thaana and Mandaic (the Arabic letter scripts of the Bidi Rule), by block, without the Common and Inherited
    // characters of the Arabic blocks (U+060C, U+061B, U+061F, U+0640, U+064B-U+0655, U+0670, U+06DD, U+0605, U+08E2, U+FD3E, U+FD3F).
    private static readonly int[] ArabicLetter =
    [
        0x0600, 0x0604, 0x0606, 0x060B, 0x060D, 0x061A, 0x061C, 0x061E, 0x0620, 0x063F, 0x0641, 0x064A, 0x0656, 0x066F, 0x0671, 0x06DC,
        0x06DE, 0x06FF, 0x0700, 0x074F, 0x0750, 0x077F, 0x0780, 0x07BF, 0x0840, 0x085F, 0x0860, 0x086F, 0x0870, 0x08E1, 0x08E3, 0x08FF,
        0xFB50, 0xFD3D, 0xFD40, 0xFDFF, 0xFE70, 0xFEFF, 0x10E60, 0x10EFF, 0x1EE00, 0x1EEFF,
    ];

    private static bool InRanges(int[] ranges, int cp)
    {
        for (var i = 0; i < ranges.Length; i += 2)
        {
            if (cp < ranges[i]) return false;
            if (cp <= ranges[i + 1]) return true;
        }

        return false;
    }

    private static UnicodeCategory Category(int cp) => CharUnicodeInfo.GetUnicodeCategory(cp);

    private static bool IsLetter(int cp) => Category(cp) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
        or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;

    private static bool IsNonSpacingMark(int cp) => Category(cp) == UnicodeCategory.NonSpacingMark;

    private static bool IsCombiningMark(int cp) => Category(cp) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    // RE_PERMITTED_CATEGORY: letters, [-+], [.,:/], Nd, Mn, Mc and the CONTEXTO and PVALID exceptions.
    private static bool IsPermitted(int cp) => IsLetter(cp) || cp is '-' or '+' or '.' or ',' or ':' or '/' ||
        Category(cp) is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark ||
        ContextOExceptions.Contains(cp) || PValidExceptions.Contains(cp);

    /// <summary>The code points of <paramref name="s"/> (<c>[...s]</c>: an unpaired surrogate is one).</summary>
    public static List<int> CodePoints(string s)
    {
        var list = new List<int>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                list.Add(char.ConvertToUtf32(s[i], s[i + 1]));
                i++;
            }
            else list.Add(s[i]);
        }

        return list;
    }

    /// <summary><c>String.prototype.normalize("NFC")</c>, which keeps unpaired surrogates (.NET's Normalize rejects them).</summary>
    public static string NormalizeNfc(string s)
    {
        try
        {
            return s.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            var sb = new StringBuilder(s.Length);
            var start = 0;
            for (var i = 0; i < s.Length; i++)
            {
                var paired = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
                if (paired)
                {
                    i++;
                    continue;
                }

                if (!char.IsSurrogate(s[i])) continue;
                sb.Append(s[start..i].Normalize(NormalizationForm.FormC)).Append(s[i]);
                start = i + 1;
            }

            return sb.Append(s[start..].Normalize(NormalizationForm.FormC)).ToString();
        }
    }

    private static bool IsAcePrefixed(string value) =>
        value.Length >= 4 && (value[0] | 0x20) == 'x' && (value[1] | 0x20) == 'n' && value[2] == '-' && value[3] == '-';

    // String.prototype.toLowerCase (U+0130 lowercases to "i" and a combining dot).
    private static string LowerCase(string s) => s.Replace(char.ConvertFromUtf32(0x130), "i" + char.ConvertFromUtf32(0x307), StringComparison.Ordinal).ToLowerInvariant();

    private static int Adapt(double delta, double numPoints, bool firstTime)
    {
        delta = firstTime ? Math.Floor(delta / PunyDamp) : ToInt32(delta) >> 1;
        delta += Math.Floor(delta / numPoints);
        var k = 0;
        while (delta > ((PunyBase - PunyTMin) * PunyTMax) >> 1)
        {
            delta = Math.Floor(delta / (PunyBase - PunyTMin));
            k += PunyBase;
        }

        return (int)(k + Math.Floor((PunyBase - PunyTMin + 1) * delta / (delta + PunySkew)));
    }

    // ECMAScript ToInt32.
    private static int ToInt32(double d)
    {
        if (!double.IsFinite(d)) return 0;
        var t = Math.Truncate(d) % 4294967296.0;
        if (t < 0) t += 4294967296.0;
        return unchecked((int)(uint)t);
    }

    /// <summary>Punycode Decode; throws <see cref="FormatException"/> for malformed input (TypeBox throws null).</summary>
    public static string Decode(string value)
    {
        var output = new List<double>();
        double n = PunyInitialN, i = 0;
        double bias = PunyInitialBias;
        var delimIdx = value.LastIndexOf('-');
        if (delimIdx > 0)
        {
            for (var j = 0; j < delimIdx; j++)
            {
                if (value[j] >= 128) throw new FormatException();
                output.Add(value[j]);
            }
        }

        var inIdx = delimIdx < 0 ? 0 : delimIdx + 1;
        while (inIdx < value.Length)
        {
            var oldi = i;
            double w = 1;
            for (double k = PunyBase; ; k += PunyBase)
            {
                if (inIdx >= value.Length) throw new FormatException();
                var ch = value[inIdx++];
                var digit = ch is >= 'a' and <= 'z' ? ch - 'a' : ch is >= '0' and <= '9' ? ch - '0' + 26 : throw new FormatException();
                i += digit * w;
                var t = k <= bias ? PunyTMin : k >= bias + PunyTMax ? PunyTMax : k - bias;
                if (digit < t) break;
                w *= PunyBase - t;
            }

            var outLen = output.Count + 1;
            bias = Adapt(i - oldi, outLen, oldi == 0);
            n += Math.Floor(i / outLen);
            i %= outLen;
            output.Insert((int)i, n);
            i++;
        }

        // String.fromCodePoint: a RangeError for anything but an integer code point.
        var sb = new StringBuilder(output.Count);
        foreach (var cp in output)
        {
            if (cp is < 0 or > 0x10FFFF || Math.Floor(cp) != cp) throw new FormatException();
            if (cp is >= 0xD800 and <= 0xDFFF) sb.Append((char)cp);
            else sb.Append(char.ConvertFromUtf32((int)cp));
        }

        return sb.ToString();
    }

    private static char DigitToChar(long digit) => digit < 26 ? (char)(digit + 'a') : (char)(digit - 26 + '0');

    /// <summary>The Punycode payload of a Unicode label (after the <c>xn--</c> prefix), for the A-label length.</summary>
    public static string Encode(string input)
    {
        var basic = new StringBuilder();
        var codePoints = CodePoints(input);
        foreach (var cp in codePoints) if (cp < 0x80) basic.Append((char)cp);
        var basicLength = basic.Length;
        var result = new StringBuilder();
        if (basicLength > 0) result.Append(basic).Append('-');
        long n = PunyInitialN, delta = 0;
        var bias = PunyInitialBias;
        var handled = basicLength;
        while (handled < codePoints.Count)
        {
            long m = long.MaxValue;
            foreach (var cp in codePoints) if (cp >= n && cp < m) m = cp;
            delta += (m - n) * (handled + 1);
            n = m;
            foreach (var cp in codePoints)
            {
                if (cp < n) delta++;
                if (cp != n) continue;
                var q = delta;
                for (long k = PunyBase; ; k += PunyBase)
                {
                    long t = k <= bias ? PunyTMin : k >= bias + PunyTMax ? PunyTMax : k - bias;
                    if (q < t) break;
                    result.Append(DigitToChar(t + (q - t) % (PunyBase - t)));
                    q = (q - t) / (PunyBase - t);
                }

                result.Append(DigitToChar(q));
                bias = Adapt(delta, handled + 1, handled == basicLength);
                delta = 0;
                handled++;
            }

            delta++;
            n++;
        }

        return result.ToString();
    }

    private static string BidiClass(int cp)
    {
        if (cp is >= '0' and <= '9' or >= 0x06F0 and <= 0x06F9) return "EN";
        if (cp is >= 0x0660 and <= 0x0669) return "AN";
        if (IsNonSpacingMark(cp)) return "NSM";
        if (InRanges(Hebrew, cp)) return "R";
        if (InRanges(ArabicLetter, cp)) return "AL";
        return IsLetter(cp) ? "L" : "ON";
    }

    /// <summary>Whether a label (decoded when it is Punycode) is an RTL label per RFC 5893 §1.4.</summary>
    private static bool HasBidiChars(string value)
    {
        if (!IsAcePrefixed(value)) return HasRightToLeftCharacters(value);
        try
        {
            return HasRightToLeftCharacters(Decode(LowerCase(value[4..])));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool HasRightToLeftCharacters(string value) => CodePoints(value).Any(cp => BidiClass(cp) is "R" or "AL" or "AN");

    private static bool SatisfiesBidiRule(string value)
    {
        var isRtl = false;
        var sawEn = false;
        var sawAn = false;
        var isFirst = true;
        foreach (var cp in CodePoints(value))
        {
            var bidiClass = BidiClass(cp);
            if (isFirst)
            {
                // 1. The first character must be a character with Bidi property L, R, or AL.
                if (bidiClass is not ("L" or "R" or "AL")) return false;
                isRtl = bidiClass is "R" or "AL";
                isFirst = false;
            }

            // 2 / 5. Only characters in the allowed set for this direction may appear.
            var allowed = isRtl
                ? bidiClass is "R" or "AL" or "AN" or "EN" or "ES" or "CS" or "ET" or "ON" or "BN" or "NSM"
                : bidiClass is "L" or "EN" or "ES" or "CS" or "ET" or "ON" or "BN" or "NSM";
            if (!allowed) return false;
            if (bidiClass == "EN") sawEn = true;
            else if (bidiClass == "AN") sawAn = true;
        }

        return !(isRtl && sawEn && sawAn);
    }

    private static bool IsAsciiLabel(string value)
    {
        if (value.Length > 0 && (value[0] == '-' || value[^1] == '-')) return false;
        if (value.Length >= 4 && value[2] == '-' && value[3] == '-') return false;
        return value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    private static bool IsPunyLabel(string value)
    {
        if (!IsAcePrefixed(value)) return false;
        try
        {
            var body = LowerCase(value[4..]);
            // A payload of only a hyphen, or starting with the delimiter, has no basic characters before it and is malformed.
            if (body.LastIndexOf('-') == 0) return false;
            var decoded = Decode(body);
            // RFC 5890 §2.3.2.1: a U-label contains at least one non-ASCII character.
            if (!decoded.Any(c => c >= 0x80)) return false;
            return IsUnicodeLabel(decoded);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsUnicodeLabel(string value)
    {
        // RFC 5890 §2.3.2.1: an A-label must not exceed 63 octets.
        if (value.Any(c => c >= 0x80) && Encode(value).Length + 4 > 63) return false;
        // RFC 5893 Bidi Rule, whenever this label itself is RTL.
        if (HasRightToLeftCharacters(value) && !SatisfiesBidiRule(value)) return false;
        var points = CodePoints(value);
        // RFC 5891 §4.2.3.2: no hyphen at the start or end, none in the 3rd and 4th positions.
        if (points.Count == 0 || points[0] == '-' || points[^1] == '-') return false;
        if (points.Count >= 4 && points[2] == '-' && points[3] == '-') return false;
        // RFC 5891 §4.2.3.2: must not begin with a combining mark.
        if (IsCombiningMark(points[0])) return false;
        var hasJapanese = false;
        for (var i = 0; i < points.Count; i++)
        {
            var cp = points[i];
            if (Rfc5892Disallowed.Contains(cp)) return false;
            if (!IsPermitted(cp)) return false;
            if (InRanges(Japanese, cp)) hasJapanese = true;
            var prev = i > 0 ? points[i - 1] : 0;
            var next = i + 1 < points.Count ? points[i + 1] : 0;
            switch (cp)
            {
                case 0x00b7: // MIDDLE DOT (Catalan)
                    if (prev != 0x006c || next != 0x006c) return false;
                    break;
                case 0x0375: // Greek KERAIA
                    if (next == 0 || !InRanges(Greek, next)) return false;
                    break;
                case 0x05f3:
                case 0x05f4: // Hebrew GERESH and GERSHAYIM
                    if (prev == 0 || !InRanges(Hebrew, prev)) return false;
                    break;
                case 0x200c: // ZWNJ: rejected only after ASCII, which can never satisfy the Virama or joining rules
                    if (prev == 0 || (prev < 0x0080 && !Virama.Contains(prev))) return false;
                    break;
                case 0x200d: // ZWJ
                    if (prev == 0 || !Virama.Contains(prev)) return false;
                    break;
            }
        }

        // RFC 5892 Appendix A.7: KATAKANA MIDDLE DOT needs a Hiragana, Katakana or Han character.
        return !value.Contains((char)0x30FB) || hasJapanese;
    }

    /// <summary>A hostname of ASCII LDH labels and Punycode labels.</summary>
    public static bool IsHostname(string value)
    {
        if (value.Length is 0 or > 253) return false;
        if (value[^1] == '.') return false;
        return value.Split('.').All(label => label.Length is > 0 and <= 63 && (IsPunyLabel(label) || IsAsciiLabel(label)));
    }

    /// <summary>An internationalized hostname (RFC 5890-5893).</summary>
    public static bool IsIdnHostname(string value)
    {
        if (value.Length == 0 || value.Contains(' ')) return false;
        var normalized = NormalizeHostname(value);
        if (normalized.Length > 253) return false;
        var labels = normalized.Split('.');
        var hasBidiChars = labels.Any(HasBidiChars);
        return labels.All(label => label.Length is > 0 and <= 63 && (IsPunyLabel(label) || IsUnicodeLabel(label)) &&
                                   (!hasBidiChars || SatisfiesBidiRule(label)));
    }

    // Fullwidth ASCII to ASCII, NFC, the code points IDNA mapping ignores removed, and the full stop variants to '.'.
    private static string NormalizeHostname(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value) sb.Append(c is >= (char)0xFF01 and <= (char)0xFF5E ? (char)(c - 0xfee0) : c);
        var normalized = NormalizeNfc(sb.ToString());
        sb.Clear();
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (char.IsHighSurrogate(c) && i + 1 < normalized.Length && char.IsLowSurrogate(normalized[i + 1]))
            {
                var cp = char.ConvertToUtf32(c, normalized[i + 1]);
                if (cp is < 0xE0100 or > 0xE01EF) sb.Append(c).Append(normalized[i + 1]);
                i++;
                continue;
            }

            if (c is (char)0x00AD or (char)0x034F or (>= (char)0x180B and <= (char)0x180D) or (char)0x200B or (>= (char)0xFE00 and <= (char)0xFE0F)) continue;
            sb.Append(c is (char)0x3002 or (char)0xFF0E or (char)0xFF61 ? '.' : c);
        }

        return sb.ToString();
    }
}
