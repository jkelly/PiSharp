// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/bug-report.ts.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent.Diagnostics;

/// <summary>bug-report.ts redaction: secret-looking keys and URL credentials never leave the machine.</summary>
public static class BugReportRedaction
{
    public const string Redacted = "<redacted>";
    private static readonly Regex SensitiveKey = new(
        @"(?:^|[-_])(api[-_]?key|secret|token|password|passwd|credential|authorization|cookie)(?:$|[-_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CamelBoundary = new("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant);
    private static readonly Regex NestedScheme = new(@"^([a-z][a-z0-9+.-]*:)([a-z][a-z0-9+.-]*://[^\n\r\p{Zl}\p{Zp}]*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A key is sensitive when, after splitting camelCase with <c>_</c>, a delimited word names a credential
    /// (<c>apiKey</c>, <c>x-api-key</c>, <c>Authorization</c>, <c>sessionToken</c>; not <c>reserveTokens</c>).</summary>
    public static bool IsSensitiveKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return SensitiveKey.IsMatch(CamelBoundary.Replace(key, "$1_$2"));
    }

    /// <summary>Strip credentials and secret-looking query parameters from a URL. A <c>scheme:</c> prefix in front of a URL
    /// (<c>git:https://…</c>) is kept and the inner URL redacted. Values that are not URLs, or need no change, come back
    /// unchanged; changed URLs are re-serialized as WHATWG <c>URL.toString()</c> does (see <see cref="WhatwgUrl"/>).</summary>
    public static string RedactUrl(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var nested = NestedScheme.Match(value);
        if (nested.Success) return nested.Groups[1].Value + RedactUrl(nested.Groups[2].Value);
        var url = WhatwgUrl.Parse(value);
        if (url is null) return value;
        var changed = false;
        if (url.Username.Length != 0 || url.Password.Length != 0) { url.Username = ""; url.Password = ""; changed = true; }
        var parameters = url.SearchParams();
        // URLSearchParams.keys() is a live iterator: set() removes later duplicates of the key as it goes.
        for (var index = 0; index < parameters.Count; index++)
        {
            var key = parameters[index].Name;
            if (!IsSensitiveKey(key)) continue;
            var first = parameters.FindIndex(pair => pair.Name == key);
            parameters[first] = (key, Redacted);
            for (var later = parameters.Count - 1; later > first; later--) if (parameters[later].Name == key) parameters.RemoveAt(later);
            url.SetSearchParams(parameters);
            changed = true;
        }
        return changed ? url.ToString() : value;
    }

    /// <summary>Copy a JSON value while removing values that may contain credentials: every non-null value under a sensitive
    /// key becomes <c>"&lt;redacted&gt;"</c> and every string passes through <see cref="RedactUrl"/>, as the source's
    /// <c>JSON.stringify</c> replacer does (array indices are keys too).</summary>
    public static JsonNode? RedactJsonValue(JsonNode? value) => Replace("", value?.DeepClone());

    /// <inheritdoc cref="RedactJsonValue(JsonNode?)"/>
    public static JsonData RedactJsonValue(JsonData value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonData.Parse(JsJson.Stringify(RedactJsonValue(JsonNode.Parse(value.ToString()))));
    }

    private static JsonNode? Replace(string key, JsonNode? child)
    {
        if (child is not null && IsSensitiveKey(key)) return JsonValue.Create(Redacted);
        switch (child)
        {
            case JsonValue text when text.GetValueKind() == JsonValueKind.String: return JsonValue.Create(RedactUrl((string)text!));
            case JsonObject node:
                foreach (var name in node.Select(property => property.Key).ToList())
                {
                    var current = node[name]; node[name] = null; node[name] = Replace(name, current?.Parent is null ? current : current.DeepClone());
                }
                return node;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    var current = array[index]; array[index] = null;
                    array[index] = Replace(index.ToString(CultureInfo.InvariantCulture), current?.Parent is null ? current : current.DeepClone());
                }
                return array;
            default: return child;
        }
    }
}

/// <summary>
/// The part of the WHATWG URL Standard that <c>redactUrl</c> relies on: <c>new URL(value)</c> validity for absolute URLs, the
/// <c>username</c>/<c>password</c> setters, <c>searchParams</c> (application/x-www-form-urlencoded parse, <c>set</c> and
/// serialization) and <c>toString()</c> normalization: lower-case scheme and special-scheme host (IDNA to ASCII, IPv4
/// numbers canonicalized), default ports dropped, backslashes and dot segments in special paths, an empty special path as
/// <c>/</c>, and the path, query and fragment percent-encode sets. IPv6 literals are kept as written (lower-cased) rather than
/// compressed, and file URLs follow the generic host rules without Windows drive-letter quirks.
/// </summary>
internal sealed class WhatwgUrl
{
    private static readonly Dictionary<string, int?> Special = new(StringComparer.Ordinal)
    { ["ftp"] = 21, ["file"] = null, ["http"] = 80, ["https"] = 443, ["ws"] = 80, ["wss"] = 443 };

    private string _scheme = "";
    private string? _host;
    private int? _port;
    private string _path = "";
    private string? _query;
    private string? _fragment;

    internal string Username { get; set; } = "";
    internal string Password { get; set; } = "";

    private bool IsSpecial => Special.ContainsKey(_scheme);

    internal static WhatwgUrl? Parse(string input)
    {
        // Leading and trailing C0 control or space are stripped, and ASCII tab or newline removed anywhere.
        var value = new string(input.Trim(Enumerable.Range(0, 0x21).Select(code => (char)code).ToArray()).Where(c => c is not ('\t' or '\n' or '\r')).ToArray());
        var colon = value.IndexOf(':');
        if (colon < 1 || !char.IsAsciiLetter(value[0]) || !value[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')) return null;
        var url = new WhatwgUrl { _scheme = value[..colon].ToLowerInvariant() };
        var rest = value[(colon + 1)..];
        var hash = rest.IndexOf('#');
        if (hash >= 0) { url._fragment = Encode(rest[(hash + 1)..], FragmentSet); rest = rest[..hash]; }
        var question = rest.IndexOf('?');
        if (question >= 0) { url._query = Encode(rest[(question + 1)..], c => QuerySet(c) || (url.IsSpecial && c == '\'')); rest = rest[..question]; }
        if (url.IsSpecial)
        {
            if (url._scheme != "file")
            {
                rest = rest.TrimStart('/', '\\');
                var end = rest.IndexOfAny(['/', '\\']); var authority = end < 0 ? rest : rest[..end]; rest = end < 0 ? "" : rest[end..];
                if (!url.ParseAuthority(authority, special: true) || url._host!.Length == 0) return null;
            }
            else if (rest.StartsWith("//", StringComparison.Ordinal) || rest.StartsWith(@"\\", StringComparison.Ordinal) ||
                rest.StartsWith(@"/\", StringComparison.Ordinal) || rest.StartsWith(@"\/", StringComparison.Ordinal))
            {
                rest = rest[2..];
                var end = rest.IndexOfAny(['/', '\\']); var host = end < 0 ? rest : rest[..end]; rest = end < 0 ? "" : rest[end..];
                if (host.Contains('@') || host.Contains(':')) return null;
                if (!url.ParseAuthority(host, special: true)) return null;
                if (url._host == "localhost") url._host = "";
            }
            else url._host = "";
            var segments = new List<string>();
            var parts = rest.Replace('\\', '/').Split('/');
            for (var index = rest.StartsWith('/') || rest.StartsWith('\\') ? 1 : 0; index < parts.Length; index++)
            {
                var segment = parts[index]; var last = index == parts.Length - 1;
                var lower = segment.ToLowerInvariant();
                if (lower is ".." or ".%2e" or "%2e." or "%2e%2e") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); if (last) segments.Add(""); }
                else if (lower is "." or "%2e") { if (last) segments.Add(""); }
                else segments.Add(Encode(segment, PathSet));
            }
            url._path = "/" + string.Join('/', segments);
        }
        else if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest[2..];
            var end = rest.IndexOf('/'); var authority = end < 0 ? rest : rest[..end]; rest = end < 0 ? "" : rest[end..];
            if (!url.ParseAuthority(authority, special: false)) return null;
            url._path = Encode(rest, PathSet);
        }
        else url._path = Encode(rest, rest.StartsWith('/') ? PathSet : C0ControlSet);
        return url;
    }

    private bool ParseAuthority(string authority, bool special)
    {
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            var userinfo = authority[..at]; var separator = userinfo.IndexOf(':');
            Username = Encode(separator < 0 ? userinfo : userinfo[..separator], UserinfoSet);
            Password = separator < 0 ? "" : Encode(userinfo[(separator + 1)..], UserinfoSet);
            authority = authority[(at + 1)..];
        }
        string host; string? port = null;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) return false;
            host = authority[..(close + 1)].ToLowerInvariant();
            var after = authority[(close + 1)..];
            if (after.Length != 0) { if (after[0] != ':') return false; port = after[1..]; }
            if (!host[1..^1].All(c => Uri.IsHexDigit(c) || c is ':' or '.')) return false;
        }
        else
        {
            var colon = authority.IndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            if (colon >= 0) port = authority[(colon + 1)..];
            if (special)
            {
                if (host.Length == 0) { if (Username.Length != 0 || Password.Length != 0 || port is not null) return false; }
                else if (DomainToAscii(host) is { } ascii) host = ascii; else return false;
            }
            else
            {
                if (host.Any(c => c is '\0' or '\t' or '\n' or '\r' or ' ' or '#' or '/' or ':' or '<' or '>' or '?' or '@' or '[' or '\\' or ']' or '^' or '|')) return false;
                host = Encode(host, C0ControlSet);
            }
        }
        if (!string.IsNullOrEmpty(port))
        {
            if (!port.All(char.IsAsciiDigit) || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number > 65535) return false;
            _port = Special.TryGetValue(_scheme, out var defaultPort) && defaultPort == number ? null : number;
        }
        _host = host;
        return true;
    }

    private static string? DomainToAscii(string host)
    {
        string decoded;
        try { decoded = Uri.UnescapeDataString(host); } catch (UriFormatException) { return null; }
        string ascii;
        try { ascii = new IdnMapping { AllowUnassigned = true, UseStd3AsciiRules = false }.GetAscii(decoded).ToLowerInvariant(); }
        catch (ArgumentException) { if (decoded.All(char.IsAscii)) ascii = decoded.ToLowerInvariant(); else return null; }
        if (ascii.Any(c => c <= 0x20 || c is '#' or '%' or '/' or ':' or '<' or '>' or '?' or '@' or '[' or '\\' or ']' or '^' or '|' or '\u007f')) return null;
        return EndsInNumber(ascii) ? Ipv4(ascii) : ascii;
    }

    private static bool EndsInNumber(string host)
    {
        var labels = host.Split('.').ToList();
        if (labels[^1].Length == 0) { if (labels.Count == 1) return false; labels.RemoveAt(labels.Count - 1); }
        var last = labels[^1];
        return last.Length != 0 && (last.All(char.IsAsciiDigit) || ParseIpv4Number(last) is not null);
    }

    private static long? ParseIpv4Number(string text)
    {
        if (text.Length == 0) return null;
        var radix = 10;
        if (text.Length >= 2 && text[0] == '0' && text[1] is 'x' or 'X') { text = text[2..]; radix = 16; }
        else if (text.Length >= 2 && text[0] == '0') { text = text[1..]; radix = 8; }
        if (text.Length == 0) return 0;
        long result = 0;
        foreach (var c in text)
        {
            var digit = radix == 16 && Uri.IsHexDigit(c) ? Convert.ToInt32(c.ToString(), 16) : char.IsAsciiDigit(c) ? c - '0' : -1;
            if (digit < 0 || digit >= radix) return null;
            result = result * radix + digit;
            if (result > uint.MaxValue * 256L) return null;
        }
        return result;
    }

    private static string? Ipv4(string host)
    {
        var parts = host.Split('.').ToList();
        if (parts[^1].Length == 0 && parts.Count > 1) parts.RemoveAt(parts.Count - 1);
        if (parts.Count > 4) return null;
        var numbers = new List<long>();
        foreach (var part in parts) { if (ParseIpv4Number(part) is not { } number) return null; numbers.Add(number); }
        if (numbers.Take(numbers.Count - 1).Any(number => number > 255) || numbers[^1] >= Math.Pow(256, 5 - numbers.Count)) return null;
        var address = numbers[^1];
        for (var index = 0; index < numbers.Count - 1; index++) address += numbers[index] * (long)Math.Pow(256, 3 - index);
        return string.Join('.', Enumerable.Range(0, 4).Select(index => ((address >> (8 * (3 - index))) & 0xFF).ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary><c>url.searchParams</c>: the query parsed as application/x-www-form-urlencoded.</summary>
    internal List<(string Name, string Value)> SearchParams() =>
        _query is null ? [] : [.. _query.Split('&').Where(pair => pair.Length != 0).Select(pair =>
        {
            var equals = pair.IndexOf('=');
            return (FormDecode(equals < 0 ? pair : pair[..equals]), FormDecode(equals < 0 ? "" : pair[(equals + 1)..]));
        })];

    /// <summary>The URLSearchParams update steps: the query becomes the urlencoded serialization, or null when empty.</summary>
    internal void SetSearchParams(IEnumerable<(string Name, string Value)> parameters)
    {
        var serialized = string.Join('&', parameters.Select(pair => FormEncode(pair.Name) + "=" + FormEncode(pair.Value)));
        _query = serialized.Length == 0 ? null : serialized;
    }

    public override string ToString()
    {
        var output = new StringBuilder(_scheme).Append(':');
        if (_host is not null)
        {
            output.Append("//");
            if (Username.Length != 0 || Password.Length != 0)
            {
                output.Append(Username);
                if (Password.Length != 0) output.Append(':').Append(Password);
                output.Append('@');
            }
            output.Append(_host);
            if (_port is { } port) output.Append(':').Append(port.ToString(CultureInfo.InvariantCulture));
        }
        else if (_path.StartsWith("//", StringComparison.Ordinal)) output.Append("/.");
        output.Append(_path);
        if (_query is not null) output.Append('?').Append(_query);
        if (_fragment is not null) output.Append('#').Append(_fragment);
        return output.ToString();
    }

    private static bool C0ControlSet(int c) => c < 0x20 || c > 0x7E;
    private static bool FragmentSet(int c) => C0ControlSet(c) || c is ' ' or '"' or '<' or '>' or '`';
    private static bool QuerySet(int c) => C0ControlSet(c) || c is ' ' or '"' or '#' or '<' or '>';
    private static bool PathSet(int c) => QuerySet(c) || c is '?' or '^' or '`' or '{' or '}';
    private static bool UserinfoSet(int c) => PathSet(c) || c is '/' or ':' or ';' or '=' or '@' or '[' or '\\' or ']' or '|';

    private static string Encode(string value, Func<int, bool> set)
    {
        var output = new StringBuilder(value.Length); Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            if (!set(rune.Value)) { output.Append((char)rune.Value); continue; }
            var length = rune.EncodeToUtf8(bytes);
            for (var index = 0; index < length; index++) output.Append('%').Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
        }
        return output.ToString();
    }

    private static string FormDecode(string value)
    {
        var bytes = new List<byte>(value.Length);
        var raw = Encoding.UTF8.GetBytes(value.Replace('+', ' '));
        for (var index = 0; index < raw.Length; index++)
        {
            if (raw[index] == '%' && index + 2 < raw.Length && Uri.IsHexDigit((char)raw[index + 1]) && Uri.IsHexDigit((char)raw[index + 2]))
            { bytes.Add(Convert.ToByte(Encoding.ASCII.GetString(raw, index + 1, 2), 16)); index += 2; }
            else bytes.Add(raw[index]);
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static string FormEncode(string value)
    {
        var output = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b == ' ') output.Append('+');
            else if (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_') output.Append((char)b);
            else output.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return output.ToString();
    }
}
