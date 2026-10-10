// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/bug-report.ts.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.AI;
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
