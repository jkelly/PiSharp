// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/error-body.ts.
using System.Text;
using System.Text.Json;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.AI.Protocols.ProviderShared;

/// <summary>A provider failure whose message upstream shows verbatim as <c>errorMessage</c>. <see cref="InStream"/> marks an
/// error the provider signalled inside an accepted stream rather than with the response status.</summary>
internal sealed class ProviderDisplayException(string message, bool inStream = false) : Exception(message)
{
    internal bool InStream { get; } = inStream;
}

/// <summary>
/// The provider error texts upstream shows for non-2xx responses and in-stream error events:
/// <c>formatProviderError(normalizeProviderError(error), prefix?)</c> over the message the pinned SDK builds.
/// The SDK sources are not vendored in the upstream tree; their message construction is stated here from the
/// official SDKs (openai 7.19.0, @anthropic-ai/sdk 0.129.0, @google/genai 2.21.0):
/// <list type="bullet">
/// <item>openai and @anthropic-ai/sdk <c>makeRequest</c>, on <c>!response.ok</c>: <c>errText = await response.text()</c>,
/// <c>errJSON = safeJSON(errText)</c> (<c>JSON.parse</c> or <c>undefined</c>), <c>errMessage = errJSON ? undefined : errText</c>,
/// then <c>APIError.generate(status, errJSON, errMessage, headers)</c>. openai keeps <c>error = errJSON?.error</c>, after
/// <c>makeStatusError</c> wraps an object body whose <c>error</c> is null or undefined as <c>{ error: body }</c>;
/// Anthropic keeps the whole parsed body as <c>error</c>. Both build <c>message</c> with <c>APIError.makeMessage</c>
/// (<see cref="MakeMessage"/>) and store <c>this.error = error</c>, which is what <c>normalizeProviderError</c> reads as the body.</item>
/// <item>openai <c>Stream</c>: a non-<c>thread.</c> <c>error</c> event throws <c>new APIError(undefined, data?.error ?? data)</c>;
/// any other non-<c>thread.</c> chunk whose <c>data.error</c> is truthy throws <c>new APIError(undefined, data.error)</c>.</item>
/// <item>@google/genai <c>throwErrorIfNotOK</c>: a body whose <c>content-type</c> includes <c>application/json</c> is read with
/// <c>response.json()</c>; any other body becomes <c>{error:{message: await response.text(), code: status, status: statusText}}</c>.
/// The <c>ApiError</c> message is <c>JSON.stringify(errorBody)</c>, with <c>status</c> and no body field.</item>
/// </list>
/// <c>response.text()</c> decodes UTF-8 with replacement and drops a leading byte-order mark.
/// </summary>
internal static class ProviderErrorText
{
    /// <summary><c>MAX_PROVIDER_ERROR_BODY_CHARS</c>.</summary>
    internal const int MaxProviderErrorBodyChars = 4000;

    private static readonly EcmaScriptJsonProjectionOptions Unbounded = new(MaximumInputCharacters: int.MaxValue,
        MaximumInputBytes: int.MaxValue, MaximumOutputCharacters: int.MaxValue, MaximumOutputBytes: int.MaxValue, MaximumDepth: 64,
        MaximumNodes: int.MaxValue, MaximumPropertiesPerObject: int.MaxValue, MaximumNumbers: int.MaxValue,
        MaximumNumberCharacters: 16_384, MaximumTotalNumberCharacters: int.MaxValue, MaximumStringCharacters: int.MaxValue);

    // ECMAScript WhiteSpace and LineTerminator code points (String.prototype.trim).
    private static readonly char[] EcmaWhitespace = "\u0009\u000a\u000b\u000c\u000d\u0020\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff".ToCharArray();

    /// <summary>A status error as the SDK shapes it: <c>error.status</c>, <c>error.message</c>, and the body
    /// <c>normalizeProviderError</c> extracts (<c>JSON.stringify(error.error)</c> for a plain non-empty object).</summary>
    internal readonly record struct SdkError(int? Status, string Message, string? Body);

    /// <summary><c>formatProviderError(normalizeProviderError(error), prefix)</c>.</summary>
    internal static string Format(SdkError error, string? prefix = null) => Format(error.Status, error.Body, error.Message, prefix);

    /// <summary><c>formatProviderError(normalizeProviderError(error), prefix)</c> for a status, a raw body reason and a message.</summary>
    internal static string Format(int? status, string? bodyText, string message, string? prefix = null)
    {
        string? body = null;
        if (bodyText is not null && bodyText.Trim(EcmaWhitespace) is { Length: > 0 } trimmed) body = Truncate(trimmed, MaxProviderErrorBodyChars);
        var carriesBody = body is null || message.Contains(body, StringComparison.Ordinal);
        if (carriesBody || status is null || body is null)
            return prefix is not null && status is not null ? $"{prefix} ({status}): {message}" : message;
        return prefix is not null ? $"{prefix} ({status}): {body}" : $"{status}: {body}";
    }

    /// <summary><c>truncateErrorText</c>.</summary>
    internal static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : $"{text[..maxChars]}... [truncated {text.Length - maxChars} chars]";

    /// <summary>ECMAScript <c>String.prototype.trim</c>.</summary>
    internal static string EcmaTrim(string text) => text.Trim(EcmaWhitespace);

    /// <summary>openai SDK <c>APIError</c> for a non-2xx response with body <paramref name="text"/>.</summary>
    internal static SdkError OpenAIStatus(int status, string text)
    {
        var parsed = SafeJson(text);
        // makeStatusError: an object body whose `error` is null or undefined is itself the error ({ error: body }).
        JsonElement? error = parsed is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } body
            ? body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var member) && member.ValueKind != JsonValueKind.Null ? member : body
            : null;
        var message = MakeMessage(status, error, parsed is { } value && Truthy(value) ? null : text);
        return new(status, message, PlainNonEmptyObject(error) ? error!.Value.GetRawText() : null);
    }

    /// <summary>openai SDK <c>Stream</c> for a non-<c>thread.</c> frame: an <c>error</c> event throws
    /// <c>new APIError(undefined, data?.error ?? data, undefined)</c>; any other frame with a truthy <c>data.error</c> throws
    /// <c>new APIError(undefined, data.error, undefined)</c>. Null when the frame is not an error. The message is also the
    /// display text (<c>normalizeProviderError</c> sees no status); <c>ErrorJson</c> is the SDK's <c>APIError.error</c>.</summary>
    internal static (string Message, string? ErrorJson)? OpenAIStreamError(string dataJson, bool errorEvent)
    {
        if (SafeJson(dataJson) is not { } data) return null;
        var member = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("error", out var found) ? found : (JsonElement?)null;
        JsonElement error;
        if (errorEvent) error = member is { ValueKind: not JsonValueKind.Null } present ? present : data;
        else if (member is { } truthy && Truthy(truthy)) error = truthy;
        else return null;
        return (MakeMessage(null, error, null), error.ValueKind == JsonValueKind.Object ? error.GetRawText() : null);
    }

    /// <summary>@anthropic-ai/sdk <c>APIError.message</c> for a non-2xx response with body <paramref name="text"/>
    /// (the whole parsed body is the SDK's <c>error</c>).</summary>
    internal static string AnthropicStatus(int status, string text)
    {
        var parsed = SafeJson(text);
        return MakeMessage(status, parsed, parsed is { } value && Truthy(value) ? null : text);
    }

    /// <summary>@google/genai <c>ApiError.message</c> for a non-2xx response, or null when <c>response.json()</c> would
    /// reject the body (the engine's <c>SyntaxError</c> text is not reproduced).</summary>
    internal static string? GoogleStatus(HttpResponseMessage response, byte[] body)
    {
        var text = FetchText(body);
        var contentType = response.Content.Headers.NonValidated.TryGetValues("Content-Type", out var values) ? string.Join(", ", values) : null;
        if (contentType?.Contains("application/json", StringComparison.Ordinal) == true) return SafeJson(text)?.GetRawText();
        var status = (int)response.StatusCode;
        return "{\"error\":{\"message\":" + StringLiteral(text) + ",\"code\":" + status.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"status\":" + StringLiteral(StatusText(response)) + "}}";
    }

    /// <summary>@google/genai 2.21.0 <c>processStreamResponse</c>: every decoded read chunk is tried as a whole with
    /// <c>JSON.parse(chunkString)</c>; an object with an <c>error</c> member whose <c>error.code</c> compares
    /// <c>code &gt;= 400 &amp;&amp; code &lt; 600</c> (JavaScript coercion) throws <c>ApiError</c> with the message
    /// <c>`got status: ${error.status}. ${JSON.stringify(chunkJson)}`</c>, which Pi shows verbatim. Any other chunk (not JSON, not an
    /// object, a null error, a code outside the range) is ignored here and buffered for SSE framing. Null when nothing is thrown.</summary>
    internal static string? GoogleStreamChunkError(string chunk)
    {
        if (SafeJson(chunk) is not { ValueKind: JsonValueKind.Object } data || !data.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.Object) return null;
        JsonElement? Member(string name)
        {
            JsonElement? found = null;
            foreach (var property in error.EnumerateObject()) if (property.Name == name) found = property.Value;
            return found;
        }
        var code = Member("code") is { } value ? JsToNumber(value) : double.NaN;
        if (!(code >= 400 && code < 600)) return null;
        var status = Member("status") is { } shown ? JsString(shown) : "undefined";
        return "got status: " + status + ". " + data.GetRawText();
    }

    /// <summary>ECMAScript <c>ToNumber</c> of a parsed JSON value (an array or object through <c>ToPrimitive</c>).</summary>
    private static double JsToNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => 1,
        JsonValueKind.False or JsonValueKind.Null => 0,
        JsonValueKind.String or JsonValueKind.Array => StringToNumber(JsString(value)),
        _ => double.NaN
    };

    /// <summary>ECMAScript <c>StringToNumber</c>: trimmed; empty is 0; 0x/0o/0b integers; Infinity; a decimal literal; else NaN.</summary>
    private static double StringToNumber(string text)
    {
        text = EcmaTrim(text);
        if (text.Length == 0) return 0;
        if (text.Length > 2 && text[0] == '0' && (text[1] | 0x20) is 'x' or 'o' or 'b')
        {
            var radix = (text[1] | 0x20) switch { 'x' => 16, 'o' => 8, _ => 2 }; double result = 0;
            foreach (var c in text.AsSpan(2))
            {
                var digit = c is >= '0' and <= '9' ? c - '0' : (c | 0x20) is >= 'a' and <= 'f' ? (c | 0x20) - 'a' + 10 : 99;
                if (digit >= radix) return double.NaN;
                result = result * radix + digit;
            }
            return result;
        }
        var body = text[0] is '+' or '-' ? text[1..] : text;
        if (body == "Infinity") return text[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        if (!System.Text.RegularExpressions.Regex.IsMatch(body, @"^(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return double.NaN;
        return double.Parse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a rejected response's body, or null when it exceeds <paramref name="maximumBytes"/>.</summary>
    internal static async ValueTask<byte[]?> ReadBodyAsync(HttpResponseMessage response, long maximumBytes, CancellationToken token)
    {
        var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false); if (read == 0) break;
            if (read > maximumBytes - bytes.Length) return null;
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }

    /// <summary>fetch <c>response.statusText</c>: the HTTP/1.1 reason phrase the server sent (empty when it sent none).</summary>
    internal static string StatusText(HttpResponseMessage response) => response.ReasonPhrase ?? "";

    /// <summary>fetch <c>response.text()</c>: UTF-8 with replacement, a leading byte-order mark removed.</summary>
    internal static string FetchText(byte[] body) => FetchText(new UTF8Encoding(false, false).GetString(body));

    /// <summary>fetch <c>response.text()</c> of already UTF-8-decoded text: a leading byte-order mark removed.</summary>
    internal static string FetchText(string text) => text.Length > 0 && text[0] == (char)0xFEFF ? text[1..] : text;

    /// <summary><c>String(error?.error?.metadata?.raw)</c> when that value is truthy, for an openai SDK error object's JSON.</summary>
    internal static string? MetadataRaw(string? errorJson)
    {
        if (errorJson is null || SafeJson(errorJson) is not { ValueKind: JsonValueKind.Object } error ||
            !error.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("raw", out var raw) || !Truthy(raw)) return null;
        return JsString(raw);
    }

    /// <summary>openai / Anthropic <c>APIError.makeMessage(status, error, message)</c>.</summary>
    internal static string MakeMessage(int? status, JsonElement? error, string? message)
    {
        string? msg;
        if (error is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("message", out var nested) && Truthy(nested))
            msg = nested.ValueKind == JsonValueKind.String ? Decode(nested.GetRawText()) : nested.GetRawText();
        else if (error is { } present && Truthy(present)) msg = present.GetRawText();
        else msg = message;
        var hasStatus = status is { } code && code != 0;
        if (hasStatus && !string.IsNullOrEmpty(msg)) return $"{status} {msg}";
        if (hasStatus) return $"{status} status code (no body)";
        if (!string.IsNullOrEmpty(msg)) return msg;
        return "(no status code or body)";
    }

    /// <summary><c>JSON.stringify(value)</c> of a string.</summary>
    internal static string StringLiteral(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else if (char.IsHighSurrogate(c) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                    { builder.Append(c).Append(value[++index]); }
                    else if (char.IsSurrogate(c)) builder.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    /// <summary><c>safeJSON(text)</c> as the canonical <c>JSON.stringify(JSON.parse(text))</c> tree: duplicate names keep the
    /// last value at the first position, numbers and strings are in JavaScript form. Null when <c>JSON.parse</c> throws.
    /// Bodies nested deeper than JsonData.MaximumDepth levels are treated as unparsed.</summary>
    private static JsonElement? SafeJson(string text)
    {
        try
        {
            var canonical = EcmaScriptJsonProjection.Project(text, Unbounded);
            using var document = JsonDocument.Parse(canonical, PiSharp.Contracts.JsonData.DocumentOptions);
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is EcmaScriptJsonProjectionException or JsonException) { return null; }
    }

    private static bool PlainNonEmptyObject(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Object } element && element.EnumerateObject().Any();

    /// <summary>JavaScript truthiness of a parsed JSON value.</summary>
    private static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => false,
        JsonValueKind.String => value.GetRawText() != "\"\"",
        JsonValueKind.Number => value.GetRawText() != "0",
        _ => true
    };

    /// <summary><c>String(value)</c> of a parsed JSON value.</summary>
    internal static string JsString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Decode(value.GetRawText()),
        JsonValueKind.Object => "[object Object]",
        JsonValueKind.Array => string.Join(",", value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Null ? "" : JsString(item))),
        _ => value.GetRawText()
    };

    /// <summary>Decodes a canonical JSON string literal, keeping lone surrogates as JavaScript does.</summary>
    private static string Decode(string literal)
    {
        var builder = new StringBuilder(literal.Length);
        for (var index = 1; index < literal.Length - 1; index++)
        {
            var c = literal[index];
            if (c != '\\') { builder.Append(c); continue; }
            var escape = literal[++index];
            switch (escape)
            {
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'u':
                    builder.Append((char)int.Parse(literal.AsSpan(index + 1, 4), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture));
                    index += 4; break;
                default: builder.Append(escape); break;
            }
        }
        return builder.ToString();
    }
}
