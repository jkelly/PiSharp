using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.Rpc;

public enum JsonlStreamOwnership { Borrowed, Owned }
/// <param name="JavaScriptInput">Read each frame as rpc-mode.ts handleInputLine does: StringDecoder("utf8") (invalid bytes become U+FFFD)
/// and <c>JSON.parse</c>, so duplicate names keep the last value, escaped lone surrogates are kept (as their escapes; see JsonUtf16) and any JSON
/// value, not only an object, is a record. Only a JSON.parse SyntaxError rejects a frame.</param>
/// <param name="JavaScriptOutput">Write each record as jsonl.ts serializeJsonLine does, <c>JSON.stringify(value)</c>: non-ASCII and
/// <c>'</c> raw, control characters and lone surrogates as lowercase <c>\uXXXX</c> (or their short escapes), JavaScript number text
/// and property order.</param>
public sealed record JsonlTransportOptions(int ReadBufferBytes = 4096, int MaximumFrameBytes = 1_048_576,
    int MaximumJsonDepth = 32, int MaximumPendingWrites = 16, bool JavaScriptInput = false, bool JavaScriptOutput = false)
{
    /// <summary>Strict frames may carry escaped lone surrogates in string values, as a JavaScript string holds them (the Pi entry's
    /// in-process connections); otherwise such a frame is refused.</summary>
    public bool KeepsLoneSurrogates { get; init; }
    internal void Validate(JsonlStreamOwnership ownership)
    {
        if (ReadBufferBytes is < 1 or > 65_536 || MaximumFrameBytes is < 1 or > int.MaxValue - 1 ||
            MaximumJsonDepth is < 1 or > PiSharp.Contracts.JsonData.MaximumDepth || MaximumPendingWrites <= 0 || !Enum.IsDefined(ownership))
            throw new ArgumentOutOfRangeException(nameof(JsonlTransportOptions), "Invalid JSONL transport limits or ownership.");
    }
}
public enum JsonlTransportFailure
{
    FrameLimit, DepthLimit, InvalidUtf8, InvalidUnicode, MalformedJson, PartialFinalFrame,
    DuplicateProperty, InvalidRecord, PendingWriteLimit
}
public sealed class JsonlTransportException : IOException
{
    public JsonlTransportFailure Failure { get; }
    internal JsonlTransportException(JsonlTransportFailure failure) : base(failure switch
    {
        JsonlTransportFailure.FrameLimit => "JSONL frame exceeds the byte limit.",
        JsonlTransportFailure.DepthLimit => "JSONL frame exceeds the JSON depth limit.",
        JsonlTransportFailure.InvalidUtf8 => "JSONL frame contains invalid UTF-8.",
        JsonlTransportFailure.InvalidUnicode => "JSONL frame contains unsupported unpaired Unicode.",
        JsonlTransportFailure.PartialFinalFrame => "JSONL input ended with an incomplete or malformed final frame.",
        JsonlTransportFailure.DuplicateProperty => "JSONL frame contains duplicate decoded object names.",
        JsonlTransportFailure.InvalidRecord => "JSONL protocol record must be a JSON object.",
        JsonlTransportFailure.PendingWriteLimit => "JSONL writer pending-call limit exceeded.",
        _ => "JSONL frame is not complete strict JSON."
    }) => Failure = failure;
    /// <summary>JSON.parse's SyntaxError message for a malformed frame (rpc-mode.ts reports it as "Failed to parse command: ...").</summary>
    public string? SyntaxError { get; }
    internal JsonlTransportException(JsonlTransportFailure failure, string? syntaxError) : this(failure) => SyntaxError = syntaxError;
}

internal static class JsonlRecordCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly UTF8Encoding ReplacingUtf8 = new(false, false);
    internal static JsonData Parse(ReadOnlySpan<byte> bytes, JsonlTransportOptions options, bool final)
    {
        if (options.JavaScriptInput)
        {
            var text = ReplacingUtf8.GetString(bytes);
            // JSON.parse has no depth limit; a frame nested deeper than an owned value holds is answered as a parse failure (the stream
            // goes on) rather than ending the stream.
            try { CheckDepth(text, options.MaximumJsonDepth); }
            catch (JsonlTransportException)
            {
                throw new JsonlTransportException(final ? JsonlTransportFailure.PartialFinalFrame : JsonlTransportFailure.MalformedJson,
                    $"JSON nests deeper than {options.MaximumJsonDepth} levels");
            }
            try
            {
                var record = PiSharp.AI.StreamingJson.JsonParse(text, out var exact, out var nonFinite);
                // The owned record keeps lone surrogates of string values (a lone surrogate of a name becomes U+FFFD); a type of 1e999
                // still reads as String(Infinity).
                if (exact is not null) ExactMembers.AddOrUpdate(record, exact);
                if (nonFinite is not null) NonFiniteMembers.AddOrUpdate(record, nonFinite);
                return record;
            }
            catch (JsonException)
            {
                throw new JsonlTransportException(final ? JsonlTransportFailure.PartialFinalFrame : JsonlTransportFailure.MalformedJson,
                    PiSharp.Contracts.Compatibility.JsJsonSyntax.Error(text));
            }
        }
        string raw;
        try { raw = Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw Failure(JsonlTransportFailure.InvalidUtf8); }
        CheckDepth(raw, options.MaximumJsonDepth);
        try
        {
            using var document = JsonDocument.Parse(raw, PiSharp.Contracts.JsonData.DocumentOptions);
            Validate(document.RootElement, raw, options.KeepsLoneSurrogates);
            return JsonData.FromElement(document.RootElement);
        }
        catch (JsonException)
        {
            throw new JsonlTransportException(final ? JsonlTransportFailure.PartialFinalFrame : JsonlTransportFailure.MalformedJson,
                PiSharp.Contracts.Compatibility.JsJsonSyntax.Error(raw));
        }
    }

    /// <summary>Exact top-level string members (with lone surrogates) of records read with <see cref="JsonlTransportOptions.JavaScriptInput"/>.</summary>
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonData, IReadOnlyDictionary<string, string>> ExactMembers = new();
    /// <summary>Top-level numbers beyond binary64's range (owned as null) of records read with <see cref="JsonlTransportOptions.JavaScriptInput"/>.</summary>
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonData, IReadOnlyDictionary<string, double>> NonFiniteMembers = new();

    internal static byte[] Encode(JsonData record, JsonlTransportOptions options)
    {
        var raw = record.ToString(); CheckDepth(raw, options.MaximumJsonDepth);
        if (options.JavaScriptOutput)
        {
            string stringified;
            try { stringified = PiSharp.AI.StreamingJson.JsonReformat(raw); }
            catch (JsonException) { throw Failure(JsonlTransportFailure.MalformedJson); }
            int length;
            try { length = Utf8.GetByteCount(stringified); }
            catch (EncoderFallbackException) { throw Failure(JsonlTransportFailure.InvalidUnicode); }
            if (length > options.MaximumFrameBytes) throw Failure(JsonlTransportFailure.FrameLimit);
            var line = new byte[length + 1]; Utf8.GetBytes(stringified, line); line[^1] = (byte)'\n'; return line;
        }
        // JsonData may originate from a document parsed with comments/trailing commas enabled.
        // Owned structural validation does not establish strict retained wire syntax.
        using var strict = ReparseStrict(raw); Validate(strict.RootElement, raw, options.KeepsLoneSurrogates);
        var compact = new StringBuilder(Math.Min(raw.Length, options.MaximumFrameBytes));
        var quoted = false; var escaped = false;
        foreach (var character in raw)
        {
            if (quoted)
            {
                compact.Append(character);
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') quoted = false;
            }
            else if (character == '"') { quoted = true; compact.Append(character); }
            else if (character is not (' ' or '\t' or '\r' or '\n')) compact.Append(character);
            if (compact.Length > options.MaximumFrameBytes) throw Failure(JsonlTransportFailure.FrameLimit);
        }
        var text = compact.ToString(); int count;
        try { count = Utf8.GetByteCount(text); }
        catch (EncoderFallbackException) { throw Failure(JsonlTransportFailure.InvalidUnicode); }
        if (count > options.MaximumFrameBytes) throw Failure(JsonlTransportFailure.FrameLimit);
        var result = new byte[count + 1]; Utf8.GetBytes(text, result); result[^1] = (byte)'\n'; return result;
    }

    private static JsonDocument ReparseStrict(string raw)
    {
        try { return JsonDocument.Parse(raw, PiSharp.Contracts.JsonData.DocumentOptions); }
        catch (JsonException) { throw Failure(JsonlTransportFailure.MalformedJson); }
    }

    private static void CheckDepth(string raw, int maximum)
    {
        var quoted = false; var escaped = false; var depth = 0;
        foreach (var character in raw)
        {
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') quoted = false;
            }
            else if (character == '"') quoted = true;
            else if (character is '{' or '[') { if (++depth > maximum) throw Failure(JsonlTransportFailure.DepthLimit); }
            else if (character is '}' or ']') depth--;
        }
    }

    private static void Validate(JsonElement value, string raw, bool keepsLoneSurrogates)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Failure(JsonlTransportFailure.InvalidRecord);
        // Strict UTF-8 cannot contain literal unpaired surrogates; escaped surrogates need their own check.
        for (var index = 0; !keepsLoneSurrogates && index < raw.Length; index++)
        {
            if (raw[index] != '\\') continue;
            if (++index >= raw.Length || raw[index] != 'u') continue;
            var code = int.Parse(raw.AsSpan(index + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture); index += 4;
            if (code is >= 0xD800 and <= 0xDBFF)
            {
                if (index + 6 >= raw.Length || raw[index + 1] != '\\' || raw[index + 2] != 'u' ||
                    int.Parse(raw.AsSpan(index + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture) is not (>= 0xDC00 and <= 0xDFFF))
                    throw Failure(JsonlTransportFailure.InvalidUnicode);
                index += 6;
            }
            else if (code is >= 0xDC00 and <= 0xDFFF) throw Failure(JsonlTransportFailure.InvalidUnicode);
        }
        CheckDuplicates(value);
    }
    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                // Only a record that keeps lone surrogates reaches here with one in a name (the others were refused above).
                if (!names.Add(PiSharp.Contracts.JsonUtf16.GetName(property))) throw Failure(JsonlTransportFailure.DuplicateProperty);
                CheckDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
    }
    private static JsonlTransportException Failure(JsonlTransportFailure failure) => new(failure);
}
