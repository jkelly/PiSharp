// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/src/resolver.ts (resolveUri: `new URL(ref, base).href`) and
// packages/schema/src/formats/typebox/{iri,iri_reference,url}.ts (`URL.canParse`), which rely on the WHATWG URL Standard through Node's URL.
#nullable enable

using System.Globalization;
using System.Numerics;
using System.Text;

namespace PiSharp.Contracts.Schema;

/// <summary>
/// The WHATWG URL Standard's basic URL parser and serializer, as far as pi-schema uses them: <c>new URL(input, base).href</c>,
/// <c>URL.canParse</c> and <c>decodeURIComponent</c>. Special and non-special schemes, opaque paths, relative resolution, dot segments,
/// the percent-encode sets, IPv4 and IPv6 hosts and opaque hosts are followed. Domains with non-ASCII code points go through
/// <see cref="IdnMapping"/>, which approximates UTS #46 processing; file URLs follow the standard without platform quirks.
/// </summary>
internal sealed class JsUrl
{
    private static readonly Dictionary<string, int?> Special = new(StringComparer.Ordinal)
    { ["ftp"] = 21, ["file"] = null, ["http"] = 80, ["https"] = 443, ["ws"] = 80, ["wss"] = 443 };

    private string _scheme = "";
    private string _username = "";
    private string _password = "";
    private string? _host;
    private int? _port;
    private List<string> _path = [];
    private string? _opaquePath;
    private string? _query;
    private string? _fragment;

    private bool IsSpecial => Special.ContainsKey(_scheme);

    /// <summary><c>new URL(input, base).href</c>, or null where the constructor throws.</summary>
    public static string? Href(string input, string? baseUrl = null)
    {
        JsUrl? parsedBase = null;
        if (baseUrl is not null && (parsedBase = Parse(baseUrl, null)) is null) return null;
        return Parse(input, parsedBase)?.Serialize();
    }

    /// <summary><c>URL.canParse(input, base)</c>.</summary>
    public static bool CanParse(string input, string? baseUrl = null) => Href(input, baseUrl) is not null;

    /// <summary><c>decodeURIComponent(value)</c>; throws <see cref="FormatException"/> ("URI malformed") for a malformed sequence.</summary>
    public static string DecodeUriComponent(string value)
    {
        if (value.IndexOf('%') < 0) return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%') { sb.Append(value[i]); continue; }
            var first = HexByte(value, i);
            if (first < 0) throw new FormatException("URI malformed");
            if (first < 0x80) { sb.Append((char)first); i += 2; continue; }
            var count = first >= 0xF0 && first <= 0xF4 ? 4 : first >= 0xE0 ? 3 : first >= 0xC2 && first < 0xE0 ? 2 : 0;
            if (count == 0 || first > 0xF4) throw new FormatException("URI malformed");
            var bytes = new byte[count];
            bytes[0] = (byte)first;
            for (var k = 1; k < count; k++)
            {
                var at = i + 3 * k;
                var next = at < value.Length && value[at] == '%' ? HexByte(value, at) : -1;
                if (next < 0x80 || next > 0xBF) throw new FormatException("URI malformed");
                bytes[k] = (byte)next;
            }
            string decoded;
            try { decoded = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { throw new FormatException("URI malformed"); }
            sb.Append(decoded);
            i += 3 * count - 1;
        }
        return sb.ToString();
    }

    private static int HexByte(string s, int percent)
    {
        if (percent + 2 >= s.Length + 0 && percent + 2 > s.Length - 1) return -1;
        var hi = HexValue(s[percent + 1]);
        var lo = HexValue(s[percent + 2]);
        return hi < 0 || lo < 0 ? -1 : hi * 16 + lo;
    }

    private static int HexValue(char c) => c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;

    // -----------------------------------------------------------------------------------------------------------------
    // The basic URL parser
    // -----------------------------------------------------------------------------------------------------------------

    private enum State
    {
        SchemeStart, Scheme, NoScheme, SpecialRelativeOrAuthority, PathOrAuthority, Relative, RelativeSlash, SpecialAuthoritySlashes,
        SpecialAuthorityIgnoreSlashes, Authority, Host, Port, File, FileSlash, FileHost, PathStart, Path, OpaquePath, Query, Fragment,
    }

    private static JsUrl? Parse(string raw, JsUrl? baseUrl)
    {
        var trimmed = TrimControlAndSpace(raw);
        var cleaned = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed) if (ch is not ('\t' or '\n' or '\r')) cleaned.Append(ch);
        var input = ToCodePoints(cleaned.ToString());
        var url = new JsUrl();
        var state = State.SchemeStart;
        var buffer = new StringBuilder();
        bool atSignSeen = false, insideBrackets = false, passwordTokenSeen = false;
        const int Eof = -1;
        for (var pointer = 0; pointer <= input.Length; pointer++)
        {
            var c = pointer < input.Length ? input[pointer] : Eof;
            switch (state)
            {
                case State.SchemeStart:
                    if (IsAsciiAlpha(c)) { buffer.Append(char.ToLowerInvariant((char)c)); state = State.Scheme; }
                    else { state = State.NoScheme; pointer--; }
                    break;
                case State.Scheme:
                    if (IsAsciiAlphanumeric(c) || c is '+' or '-' or '.') buffer.Append(char.ToLowerInvariant((char)c));
                    else if (c == ':')
                    {
                        url._scheme = buffer.ToString();
                        buffer.Clear();
                        if (url._scheme == "file") state = State.File;
                        else if (url.IsSpecial && baseUrl is not null && baseUrl._scheme == url._scheme) state = State.SpecialRelativeOrAuthority;
                        else if (url.IsSpecial) state = State.SpecialAuthoritySlashes;
                        else if (pointer + 1 < input.Length && input[pointer + 1] == '/') { state = State.PathOrAuthority; pointer++; }
                        else { url._opaquePath = ""; state = State.OpaquePath; }
                    }
                    else { buffer.Clear(); state = State.NoScheme; pointer = -1; }
                    break;
                case State.NoScheme:
                    if (baseUrl is null || (baseUrl._opaquePath is not null && c != '#')) return null;
                    if (baseUrl._opaquePath is not null && c == '#')
                    {
                        url._scheme = baseUrl._scheme; url._opaquePath = baseUrl._opaquePath; url._query = baseUrl._query; url._fragment = "";
                        state = State.Fragment;
                    }
                    else if (baseUrl._scheme != "file") { state = State.Relative; pointer--; }
                    else { state = State.File; pointer--; }
                    break;
                case State.SpecialRelativeOrAuthority:
                    if (c == '/' && pointer + 1 < input.Length && input[pointer + 1] == '/') { state = State.SpecialAuthorityIgnoreSlashes; pointer++; }
                    else { state = State.Relative; pointer--; }
                    break;
                case State.PathOrAuthority:
                    if (c == '/') state = State.Authority;
                    else { state = State.Path; pointer--; }
                    break;
                case State.Relative:
                    url._scheme = baseUrl!._scheme;
                    if (c == '/') state = State.RelativeSlash;
                    else if (url.IsSpecial && c == '\\') state = State.RelativeSlash;
                    else
                    {
                        url.CopyAuthority(baseUrl);
                        url._path = [.. baseUrl._path];
                        url._query = baseUrl._query;
                        if (c == '?') { url._query = ""; state = State.Query; }
                        else if (c == '#') { url._fragment = ""; state = State.Fragment; }
                        else if (c != Eof) { url._query = null; url.ShortenPath(); state = State.Path; pointer--; }
                    }
                    break;
                case State.RelativeSlash:
                    if (url.IsSpecial && c is '/' or '\\') state = State.SpecialAuthorityIgnoreSlashes;
                    else if (c == '/') state = State.Authority;
                    else { url.CopyAuthority(baseUrl!); state = State.Path; pointer--; }
                    break;
                case State.SpecialAuthoritySlashes:
                    if (c == '/' && pointer + 1 < input.Length && input[pointer + 1] == '/') { state = State.SpecialAuthorityIgnoreSlashes; pointer++; }
                    else { state = State.SpecialAuthorityIgnoreSlashes; pointer--; }
                    break;
                case State.SpecialAuthorityIgnoreSlashes:
                    if (c is not ('/' or '\\')) { state = State.Authority; pointer--; }
                    break;
                case State.Authority:
                    if (c == '@')
                    {
                        if (atSignSeen) buffer.Insert(0, "%40");
                        atSignSeen = true;
                        foreach (var cp in ToCodePoints(buffer.ToString()))
                        {
                            if (cp == ':' && !passwordTokenSeen) { passwordTokenSeen = true; continue; }
                            var encoded = PercentEncode(cp, UserinfoSet);
                            if (passwordTokenSeen) url._password += encoded; else url._username += encoded;
                        }
                        buffer.Clear();
                    }
                    else if (c is Eof or '/' or '?' or '#' || (url.IsSpecial && c == '\\'))
                    {
                        if (atSignSeen && buffer.Length == 0) return null;
                        pointer -= ToCodePoints(buffer.ToString()).Length + 1;
                        buffer.Clear();
                        state = State.Host;
                    }
                    else AppendCodePoint(buffer, c);
                    break;
                case State.Host:
                    if (c == ':' && !insideBrackets)
                    {
                        if (buffer.Length == 0) return null;
                        var host = ParseHost(buffer.ToString(), !url.IsSpecial);
                        if (host is null) return null;
                        url._host = host; buffer.Clear(); state = State.Port;
                    }
                    else if (c is Eof or '/' or '?' or '#' || (url.IsSpecial && c == '\\'))
                    {
                        pointer--;
                        if (url.IsSpecial && buffer.Length == 0) return null;
                        var host = ParseHost(buffer.ToString(), !url.IsSpecial);
                        if (host is null) return null;
                        url._host = host; buffer.Clear(); state = State.PathStart;
                    }
                    else
                    {
                        if (c == '[') insideBrackets = true;
                        if (c == ']') insideBrackets = false;
                        AppendCodePoint(buffer, c);
                    }
                    break;
                case State.Port:
                    if (c is >= '0' and <= '9') buffer.Append((char)c);
                    else if (c is Eof or '/' or '?' or '#' || (url.IsSpecial && c == '\\'))
                    {
                        if (buffer.Length != 0)
                        {
                            var digits = buffer.ToString().TrimStart('0');
                            if (digits.Length > 5) return null;
                            var port = digits.Length == 0 ? 0 : int.Parse(digits, CultureInfo.InvariantCulture);
                            if (port > 65535) return null;
                            url._port = Special.TryGetValue(url._scheme, out var standard) && standard == port ? null : port;
                            buffer.Clear();
                        }
                        state = State.PathStart; pointer--;
                    }
                    else return null;
                    break;
                case State.File:
                    url._scheme = "file";
                    url._host = "";
                    if (c is '/' or '\\') state = State.FileSlash;
                    else if (baseUrl is not null && baseUrl._scheme == "file")
                    {
                        url._host = baseUrl._host;
                        url._path = [.. baseUrl._path];
                        url._query = baseUrl._query;
                        if (c == '?') { url._query = ""; state = State.Query; }
                        else if (c == '#') { url._fragment = ""; state = State.Fragment; }
                        else if (c != Eof)
                        {
                            url._query = null;
                            if (!StartsWithWindowsDriveLetter(input, pointer)) url.ShortenPath();
                            else url._path = [];
                            state = State.Path; pointer--;
                        }
                    }
                    else { state = State.Path; pointer--; }
                    break;
                case State.FileSlash:
                    if (c is '/' or '\\') state = State.FileHost;
                    else
                    {
                        if (baseUrl is not null && baseUrl._scheme == "file")
                        {
                            url._host = baseUrl._host;
                            if (!StartsWithWindowsDriveLetter(input, pointer) && baseUrl._path.Count > 0 && IsNormalizedWindowsDriveLetter(baseUrl._path[0]))
                                url._path.Add(baseUrl._path[0]);
                        }
                        state = State.Path; pointer--;
                    }
                    break;
                case State.FileHost:
                    if (c is Eof or '/' or '\\' or '?' or '#')
                    {
                        pointer--;
                        var text = buffer.ToString();
                        if (IsWindowsDriveLetter(text)) state = State.Path;
                        else if (text.Length == 0) { url._host = ""; state = State.PathStart; }
                        else
                        {
                            var host = ParseHost(text, false);
                            if (host is null) return null;
                            url._host = host == "localhost" ? "" : host;
                            buffer.Clear(); state = State.PathStart;
                        }
                    }
                    else AppendCodePoint(buffer, c);
                    break;
                case State.PathStart:
                    if (url.IsSpecial) { state = State.Path; if (c is not ('/' or '\\')) pointer--; }
                    else if (c == '?') { url._query = ""; state = State.Query; }
                    else if (c == '#') { url._fragment = ""; state = State.Fragment; }
                    else if (c != Eof) { state = State.Path; if (c != '/') pointer--; }
                    break;
                case State.Path:
                    if (c is Eof or '/' || (url.IsSpecial && c == '\\') || c is '?' or '#')
                    {
                        var segment = buffer.ToString();
                        var slash = c == '/' || (url.IsSpecial && c == '\\');
                        if (IsDoubleDot(segment))
                        {
                            url.ShortenPath();
                            if (!slash) url._path.Add("");
                        }
                        else if (IsSingleDot(segment) && !slash) url._path.Add("");
                        else if (!IsSingleDot(segment))
                        {
                            if (url._scheme == "file" && url._path.Count == 0 && IsWindowsDriveLetter(segment)) segment = segment[..1] + ":" + segment[2..];
                            url._path.Add(segment);
                        }
                        buffer.Clear();
                        if (c == '?') { url._query = ""; state = State.Query; }
                        if (c == '#') { url._fragment = ""; state = State.Fragment; }
                    }
                    else buffer.Append(PercentEncode(c, PathSet));
                    break;
                case State.OpaquePath:
                    if (c == '?') { url._query = ""; state = State.Query; }
                    else if (c == '#') { url._fragment = ""; state = State.Fragment; }
                    else if (c != Eof) url._opaquePath += PercentEncode(c, C0ControlSet);
                    break;
                case State.Query:
                    if (c is '#' or Eof)
                    {
                        Func<int, bool> set = url.IsSpecial ? SpecialQuerySet : QuerySet;
                        foreach (var cp in ToCodePoints(buffer.ToString())) url._query += PercentEncode(cp, set);
                        buffer.Clear();
                        if (c == '#') { url._fragment = ""; state = State.Fragment; }
                    }
                    else AppendCodePoint(buffer, c);
                    break;
                case State.Fragment:
                    if (c != Eof) url._fragment += PercentEncode(c, FragmentSet);
                    break;
            }
        }
        return url;
    }

    private void CopyAuthority(JsUrl from)
    {
        _username = from._username; _password = from._password; _host = from._host; _port = from._port;
    }

    private void ShortenPath()
    {
        if (_scheme == "file" && _path.Count == 1 && IsNormalizedWindowsDriveLetter(_path[0])) return;
        if (_path.Count > 0) _path.RemoveAt(_path.Count - 1);
    }

    private string Serialize()
    {
        var sb = new StringBuilder(_scheme).Append(':');
        if (_host is not null)
        {
            sb.Append("//");
            if (_username.Length > 0 || _password.Length > 0)
            {
                sb.Append(_username);
                if (_password.Length > 0) sb.Append(':').Append(_password);
                sb.Append('@');
            }
            sb.Append(_host);
            if (_port is { } port) sb.Append(':').Append(port.ToString(CultureInfo.InvariantCulture));
        }
        if (_opaquePath is not null) sb.Append(_opaquePath);
        else
        {
            if (_host is null && _path.Count > 1 && _path[0].Length == 0) sb.Append("/.");
            foreach (var segment in _path) sb.Append('/').Append(segment);
        }
        if (_query is not null) sb.Append('?').Append(_query);
        if (_fragment is not null) sb.Append('#').Append(_fragment);
        return sb.ToString();
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Hosts
    // -----------------------------------------------------------------------------------------------------------------

    private static string? ParseHost(string input, bool isOpaque)
    {
        if (input.StartsWith('['))
        {
            if (!input.EndsWith(']')) return null;
            var address = ParseIpv6(input[1..^1]);
            return address is null ? null : "[" + SerializeIpv6(address) + "]";
        }
        if (isOpaque)
        {
            foreach (var ch in input) if (IsForbiddenHost(ch)) return null;
            var sb = new StringBuilder();
            foreach (var cp in ToCodePoints(input)) sb.Append(PercentEncode(cp, C0ControlSet));
            return sb.ToString();
        }
        string domain;
        try { domain = new UTF8Encoding(false, false).GetString(PercentDecode(input)); }
        catch (ArgumentException) { return null; }
        var ascii = DomainToAscii(domain);
        if (ascii is null || ascii.Length == 0) return null;
        foreach (var ch in ascii) if (IsForbiddenDomain(ch)) return null;
        if (EndsInNumber(ascii)) return ParseIpv4(ascii);
        return ascii;
    }

    private static string? DomainToAscii(string domain)
    {
        var asciiOnly = true;
        foreach (var ch in domain) if (ch >= 0x80) { asciiOnly = false; break; }
        if (asciiOnly)
        {
            var lowered = domain.ToLowerInvariant();
            var punycode = false;
            foreach (var label in lowered.Split('.')) if (label.StartsWith("xn--", StringComparison.Ordinal)) punycode = true;
            if (!punycode) return lowered;
            try { _ = new IdnMapping { AllowUnassigned = true }.GetUnicode(lowered); return lowered; }
            catch (ArgumentException) { return null; }
        }
        try
        {
            // UTS #46 maps full stops and case before ToASCII; IdnMapping does both.
            return new IdnMapping { AllowUnassigned = true, UseStd3AsciiRules = false }.GetAscii(domain.TrimEnd('.')) + (domain.EndsWith('.') ? "." : "");
        }
        catch (ArgumentException) { return null; }
    }

    private static bool EndsInNumber(string input)
    {
        var parts = input.Split('.').ToList();
        if (parts[^1].Length == 0)
        {
            if (parts.Count == 1) return false;
            parts.RemoveAt(parts.Count - 1);
        }
        var last = parts[^1];
        if (last.Length > 0 && last.All(char.IsAsciiDigit)) return true;
        return ParseIpv4Number(last) is not null;
    }

    private static string? ParseIpv4(string input)
    {
        var parts = input.Split('.').ToList();
        if (parts[^1].Length == 0 && parts.Count > 1) parts.RemoveAt(parts.Count - 1);
        if (parts.Count > 4) return null;
        var numbers = new List<BigInteger>();
        foreach (var part in parts)
        {
            if (ParseIpv4Number(part) is not { } n) return null;
            numbers.Add(n);
        }
        for (var i = 0; i < numbers.Count - 1; i++) if (numbers[i] > 255) return null;
        if (numbers[^1] >= BigInteger.Pow(256, 5 - numbers.Count)) return null;
        var ipv4 = numbers[^1];
        for (var i = 0; i < numbers.Count - 1; i++) ipv4 += numbers[i] * BigInteger.Pow(256, 3 - i);
        var value = (uint)ipv4;
        return string.Join('.', new[] { value >> 24, (value >> 16) & 255, (value >> 8) & 255, value & 255 }.Select(b => b.ToString(CultureInfo.InvariantCulture)));
    }

    private static BigInteger? ParseIpv4Number(string input)
    {
        if (input.Length == 0) return null;
        var radix = 10;
        if (input.Length >= 2 && input[0] == '0' && input[1] is 'x' or 'X') { input = input[2..]; radix = 16; }
        else if (input.Length >= 2 && input[0] == '0') { input = input[1..]; radix = 8; }
        if (input.Length == 0) return BigInteger.Zero;
        BigInteger value = 0;
        foreach (var ch in input)
        {
            var digit = HexValue(ch);
            if (digit < 0 || digit >= radix) return null;
            value = value * radix + digit;
        }
        return value;
    }

    private static ushort[]? ParseIpv6(string text)
    {
        var address = new ushort[8];
        var pieceIndex = 0;
        int? compress = null;
        var p = 0;
        char At(int i) => i < text.Length ? text[i] : '\0';
        if (At(p) == ':')
        {
            if (At(p + 1) != ':') return null;
            p += 2; pieceIndex++; compress = pieceIndex;
        }
        while (p < text.Length)
        {
            if (pieceIndex == 8) return null;
            if (At(p) == ':')
            {
                if (compress is not null) return null;
                p++; pieceIndex++; compress = pieceIndex;
                continue;
            }
            int value = 0, length = 0;
            while (length < 4 && p < text.Length && HexValue(At(p)) >= 0) { value = value * 16 + HexValue(At(p)); p++; length++; }
            if (At(p) == '.' && p < text.Length)
            {
                if (length == 0) return null;
                p -= length;
                if (pieceIndex > 6) return null;
                var numbersSeen = 0;
                while (p < text.Length)
                {
                    int? ipv4Piece = null;
                    if (numbersSeen > 0)
                    {
                        if (At(p) == '.' && numbersSeen < 4) p++;
                        else return null;
                    }
                    if (!char.IsAsciiDigit(At(p))) return null;
                    while (p < text.Length && char.IsAsciiDigit(At(p)))
                    {
                        var number = At(p) - '0';
                        if (ipv4Piece is null) ipv4Piece = number;
                        else if (ipv4Piece == 0) return null;
                        else ipv4Piece = ipv4Piece * 10 + number;
                        if (ipv4Piece > 255) return null;
                        p++;
                    }
                    address[pieceIndex] = (ushort)(address[pieceIndex] * 0x100 + ipv4Piece!.Value);
                    numbersSeen++;
                    if (numbersSeen is 2 or 4) pieceIndex++;
                }
                if (numbersSeen != 4) return null;
                break;
            }
            if (At(p) == ':' && p < text.Length)
            {
                p++;
                if (p >= text.Length) return null;
            }
            else if (p < text.Length) return null;
            address[pieceIndex] = (ushort)value;
            pieceIndex++;
        }
        if (compress is { } start)
        {
            var swaps = pieceIndex - start;
            pieceIndex = 7;
            while (pieceIndex != 0 && swaps > 0)
            {
                (address[pieceIndex], address[start + swaps - 1]) = (address[start + swaps - 1], address[pieceIndex]);
                pieceIndex--; swaps--;
            }
        }
        else if (pieceIndex != 8) return null;
        return address;
    }

    private static string SerializeIpv6(ushort[] address)
    {
        int bestStart = -1, bestLength = 1;
        for (var i = 0; i < 8;)
        {
            if (address[i] != 0) { i++; continue; }
            var j = i;
            while (j < 8 && address[j] == 0) j++;
            if (j - i > bestLength) { bestStart = i; bestLength = j - i; }
            i = j;
        }
        var sb = new StringBuilder();
        var ignore0 = false;
        for (var i = 0; i < 8; i++)
        {
            if (ignore0 && address[i] == 0) continue;
            ignore0 = false;
            if (bestStart == i)
            {
                sb.Append(i == 0 ? "::" : ":");
                ignore0 = true;
                continue;
            }
            sb.Append(address[i].ToString("x", CultureInfo.InvariantCulture));
            if (i != 7) sb.Append(':');
        }
        return sb.ToString();
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Code points and percent-encoding
    // -----------------------------------------------------------------------------------------------------------------

    private static bool C0ControlSet(int c) => c < 0x20 || c > 0x7E;
    private static bool FragmentSet(int c) => C0ControlSet(c) || c is ' ' or '"' or '<' or '>' or '`';
    private static bool QuerySet(int c) => C0ControlSet(c) || c is ' ' or '"' or '#' or '<' or '>';
    private static bool SpecialQuerySet(int c) => QuerySet(c) || c == '\'';
    private static bool PathSet(int c) => QuerySet(c) || c is '?' or '^' or '`' or '{' or '}';
    private static bool UserinfoSet(int c) => PathSet(c) || c is '/' or ':' or ';' or '=' or '@' or (>= '[' and <= '^') or '|';

    private static string PercentEncode(int codePoint, Func<int, bool> set)
    {
        if (!set(codePoint)) return char.ConvertFromUtf32(codePoint);
        var text = codePoint is >= 0xD800 and <= 0xDFFF ? char.ConvertFromUtf32(0xFFFD) : char.ConvertFromUtf32(codePoint);
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text)) sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static byte[] PercentDecode(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var output = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == '%' && i + 2 < bytes.Length && HexValue((char)bytes[i + 1]) >= 0 && HexValue((char)bytes[i + 2]) >= 0)
            {
                output.Add((byte)(HexValue((char)bytes[i + 1]) * 16 + HexValue((char)bytes[i + 2])));
                i += 2;
            }
            else output.Add(bytes[i]);
        }
        return [.. output];
    }

    private static bool IsForbiddenHost(char c) => c is '\0' or '\t' or '\n' or '\r' or ' ' or '#' or '/' or ':' or '<' or '>' or '?' or '@' or '[' or '\\' or ']' or '^' or '|';
    private static bool IsForbiddenDomain(char c) => IsForbiddenHost(c) || c < 0x20 || c is '%' or '\x7F';

    private static bool IsAsciiAlpha(int c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    private static bool IsAsciiAlphanumeric(int c) => IsAsciiAlpha(c) || c is >= '0' and <= '9';

    private static bool IsWindowsDriveLetter(string s) => s.Length == 2 && IsAsciiAlpha(s[0]) && s[1] is ':' or '|';
    private static bool IsNormalizedWindowsDriveLetter(string s) => s.Length == 2 && IsAsciiAlpha(s[0]) && s[1] == ':';

    private static bool StartsWithWindowsDriveLetter(int[] input, int pointer)
    {
        if (input.Length - pointer < 2 || !IsAsciiAlpha(input[pointer]) || input[pointer + 1] is not (':' or '|')) return false;
        return input.Length - pointer == 2 || input[pointer + 2] is '/' or '\\' or '?' or '#';
    }

    private static bool IsSingleDot(string s) => s == "." || s.Equals("%2e", StringComparison.OrdinalIgnoreCase);

    private static bool IsDoubleDot(string s) => s is ".." || s.Equals(".%2e", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("%2e.", StringComparison.OrdinalIgnoreCase) || s.Equals("%2e%2e", StringComparison.OrdinalIgnoreCase);

    private static string TrimControlAndSpace(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && s[start] <= ' ') start++;
        while (end > start && s[end - 1] <= ' ') end--;
        return s[start..end];
    }

    private static int[] ToCodePoints(string s)
    {
        var list = new List<int>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { list.Add(char.ConvertToUtf32(s[i], s[i + 1])); i++; }
            else list.Add(s[i]);
        }
        return [.. list];
    }

    private static void AppendCodePoint(StringBuilder sb, int c)
    {
        if (c is >= 0xD800 and <= 0xDFFF) sb.Append((char)c);
        else sb.Append(char.ConvertFromUtf32(c));
    }
}
