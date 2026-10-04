using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.ExtensionHost.Protocol;

/// <summary>Strict v1 envelope admission. Unknown top-level fields fail; opaque JSON values stay owned.</summary>
public sealed class WorkerFrameCodec
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal const long MaximumIdentity = 9_007_199_254_740_991;
    private readonly WorkerProtocolOptions _options;
    public WorkerFrameCodec(WorkerProtocolOptions? options = null) { _options = options ?? new(); _options.Validate(); }
    public WorkerMessage Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > _options.MaximumFrameBytes) throw Fail(WorkerProtocolFailure.FrameLimit);
        try { return Decode(Utf8.GetString(bytes)); }
        catch (DecoderFallbackException) { throw Fail(WorkerProtocolFailure.InvalidUtf8); }
    }
    public WorkerMessage Decode(string raw)
    {
        CheckBytes(raw);
        CheckDepth(raw);
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 64 });
            CheckEscapedUnicode(raw);
            var count = 0; ValidateValue(document.RootElement, ref count);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw Fail(WorkerProtocolFailure.InvalidEnvelope);
            if (!value.TryGetProperty("version", out var version) || !version.TryGetInt32(out var v) || v != 1)
                throw Fail(WorkerProtocolFailure.UnsupportedVersion);
            var kind = Text(value, "kind");
            var type = kind switch
            {
                "hello" => WorkerMessageKind.Hello, "request" => WorkerMessageKind.Request,
                "response" => WorkerMessageKind.Response, "error" => WorkerMessageKind.Error,
                "progress" => WorkerMessageKind.Progress, "cancel" => WorkerMessageKind.Cancel,
                "shutdown" => WorkerMessageKind.Shutdown, _ => throw Fail(WorkerProtocolFailure.InvalidEnvelope)
            };
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "version", "kind", "workerGeneration", "sessionGeneration" };
            if (type == WorkerMessageKind.Hello) allowed.Add("features");
            if (type is WorkerMessageKind.Request or WorkerMessageKind.Response or WorkerMessageKind.Error or WorkerMessageKind.Progress or WorkerMessageKind.Cancel)
                allowed.Add("id");
            if (type is WorkerMessageKind.Request or WorkerMessageKind.Response or WorkerMessageKind.Progress) allowed.Add("value");
            if (type == WorkerMessageKind.Request) { allowed.Add("method"); allowed.Add("handle"); }
            if (type == WorkerMessageKind.Error) allowed.Add("error");
            foreach (var field in value.EnumerateObject()) if (!allowed.Contains(field.Name)) throw Fail(WorkerProtocolFailure.InvalidEnvelope);
            var worker = Identity(value, "workerGeneration"); var session = Identity(value, "sessionGeneration");
            long? id = allowed.Contains("id") ? Identity(value, "id") : null;
            WorkerValue? data = allowed.Contains("value") ? ReadValue(value.GetProperty("value")) : null;
            WorkerCallbackHandle? handle = null;
            if (value.TryGetProperty("handle", out var h))
            {
                Fields(h, "ownerId", "ownerGeneration", "registrationId", "callbackId");
                handle = new(Text(h, "ownerId"), Identity(h, "ownerGeneration"), Text(h, "registrationId"), Text(h, "callbackId"));
            }
            var features = ImmutableArray<string>.Empty;
            if (type == WorkerMessageKind.Hello)
            {
                var f = value.GetProperty("features");
                if (f.ValueKind != JsonValueKind.Array || f.GetArrayLength() > 16) throw Fail(WorkerProtocolFailure.InvalidEnvelope);
                var builder = ImmutableArray.CreateBuilder<string>();
                foreach (var item in f.EnumerateArray()) builder.Add(Identifier(item.GetString()));
                features = builder.ToImmutable();
                if (features.Distinct(StringComparer.Ordinal).Count() != features.Length) throw Fail(WorkerProtocolFailure.InvalidEnvelope);
            }
            string? error = null; var outcome = WorkerOutcome.Unknown;
            if (type == WorkerMessageKind.Error)
            {
                var e = value.GetProperty("error"); Fields(e, "code", "outcome"); error = Text(e, "code");
                outcome = Text(e, "outcome") switch
                { "notSent" => WorkerOutcome.NotSent, "unknown" => WorkerOutcome.Unknown, _ => throw Fail(WorkerProtocolFailure.InvalidEnvelope) };
            }
            return new(type, worker, session, id, type == WorkerMessageKind.Request ? Text(value, "method") : null,
                data, handle, features, error, outcome);
        }
        catch (WorkerProtocolException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw Fail(WorkerProtocolFailure.MalformedFrame); }
    }
    public string Encode(WorkerMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if ((message.Kind != WorkerMessageKind.Hello && !message.Features.IsDefaultOrEmpty) ||
            (message.Kind != WorkerMessageKind.Error && (message.ErrorCode is not null || message.Outcome != WorkerOutcome.Unknown)) ||
            (message.Kind == WorkerMessageKind.Error && !Enum.IsDefined(message.Outcome)))
            throw Fail(WorkerProtocolFailure.InvalidEnvelope);
        CheckIdentity(message.WorkerGeneration); CheckIdentity(message.SessionGeneration);
        if (message.Id is { } identity) CheckIdentity(identity);
        if (message.Method is { } operation) Identifier(operation);
        if (message.ErrorCode is { } code) Identifier(code);
        if (message.Handle is { } callback)
        {
            Identifier(callback.OwnerId); CheckIdentity(callback.OwnerGeneration);
            Identifier(callback.RegistrationId); Identifier(callback.CallbackId);
        }
        if (!message.Features.IsDefault)
        {
            if (message.Features.Length > 16) throw Fail(WorkerProtocolFailure.InvalidEnvelope);
            foreach (var feature in message.Features) Identifier(feature);
        }
        if (message.Value?.Json is { } data) CheckBytes(data.ToString());
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", 1);
            writer.WriteString("kind", message.Kind switch
            {
                WorkerMessageKind.Hello => "hello", WorkerMessageKind.Request => "request",
                WorkerMessageKind.Response => "response", WorkerMessageKind.Error => "error",
                WorkerMessageKind.Progress => "progress", WorkerMessageKind.Cancel => "cancel",
                WorkerMessageKind.Shutdown => "shutdown", _ => throw Fail(WorkerProtocolFailure.InvalidEnvelope)
            });
            writer.WriteNumber("workerGeneration", message.WorkerGeneration); writer.WriteNumber("sessionGeneration", message.SessionGeneration);
            if (message.Id is { } id) writer.WriteNumber("id", id);
            if (message.Method is { } method) writer.WriteString("method", method);
            if (message.Value is { } value) { writer.WritePropertyName("value"); WriteValue(writer, value); }
            if (message.Handle is { } h)
            {
                writer.WriteStartObject("handle"); writer.WriteString("ownerId", h.OwnerId);
                writer.WriteNumber("ownerGeneration", h.OwnerGeneration); writer.WriteString("registrationId", h.RegistrationId);
                writer.WriteString("callbackId", h.CallbackId); writer.WriteEndObject();
            }
            if (message.Kind == WorkerMessageKind.Hello)
            {
                writer.WriteStartArray("features");
                if (!message.Features.IsDefault) foreach (var feature in message.Features) writer.WriteStringValue(feature);
                writer.WriteEndArray();
            }
            if (message.Kind == WorkerMessageKind.Error)
            {
                writer.WriteStartObject("error"); writer.WriteString("code", message.ErrorCode);
                writer.WriteString("outcome", message.Outcome == WorkerOutcome.NotSent ? "notSent" : "unknown"); writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        var raw = Compact(Utf8.GetString(output.ToArray()));
        _ = Decode(raw); // Strictly admit locally authored frames before any write effect.
        return raw + "\n";
    }
    internal static string Identifier(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 96 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':')))
            throw Fail(WorkerProtocolFailure.InvalidEnvelope);
        return text;
    }
    internal static void CheckIdentity(long value)
    { if (value is < 1 or > MaximumIdentity) throw Fail(WorkerProtocolFailure.InvalidEnvelope); }
    private static long Identity(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt64(out var n)) throw Fail(WorkerProtocolFailure.InvalidEnvelope);
        CheckIdentity(n); return n;
    }
    private static string Text(JsonElement root, string name) => Identifier(root.GetProperty(name).GetString());
    private static void Fields(JsonElement root, params string[] fields)
    {
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != fields.Length ||
            fields.Any(name => !root.TryGetProperty(name, out _))) throw Fail(WorkerProtocolFailure.InvalidEnvelope);
    }
    private static WorkerValue ReadValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Fail(WorkerProtocolFailure.InvalidEnvelope);
        var presence = Text(value, "presence");
        if (presence == "json") { Fields(value, "presence", "data"); return WorkerValue.FromJson(JsonData.FromElement(value.GetProperty("data"))); }
        Fields(value, "presence");
        return presence switch
        { "absent" => WorkerValue.Absent, "undefined" => WorkerValue.Undefined, _ => throw Fail(WorkerProtocolFailure.InvalidEnvelope) };
    }
    private static void WriteValue(Utf8JsonWriter writer, WorkerValue value)
    {
        writer.WriteStartObject();
        writer.WriteString("presence", value.Presence switch
        { WorkerValuePresence.Absent => "absent", WorkerValuePresence.Undefined => "undefined", WorkerValuePresence.Json => "json", _ => throw Fail(WorkerProtocolFailure.InvalidEnvelope) });
        if (value.Presence == WorkerValuePresence.Json)
        { writer.WritePropertyName("data"); writer.WriteRawValue(value.Json?.ToString() ?? throw Fail(WorkerProtocolFailure.InvalidEnvelope)); }
        writer.WriteEndObject();
    }
    private void CheckBytes(string raw)
    {
        if (raw.Length > _options.MaximumFrameBytes) throw Fail(WorkerProtocolFailure.FrameLimit);
        try { if (Utf8.GetByteCount(raw) > _options.MaximumFrameBytes) throw Fail(WorkerProtocolFailure.FrameLimit); }
        catch (EncoderFallbackException) { throw Fail(WorkerProtocolFailure.InvalidUnicode); }
    }
    private void CheckDepth(string raw)
    {
        var quoted = false; var escaped = false; var depth = 0;
        foreach (var c in raw)
        {
            if (quoted) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false; }
            else if (c == '"') quoted = true;
            else if (c is '{' or '[') { if (++depth > _options.MaximumJsonDepth) throw Fail(WorkerProtocolFailure.DepthLimit); }
            else if (c is '}' or ']') depth--;
        }
    }
    private void ValidateValue(JsonElement value, ref int count)
    {
        if (++count > _options.MaximumJsonValues) throw Fail(WorkerProtocolFailure.ValueCountLimit);
        if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var n) || !double.IsFinite(n)))
            throw Fail(WorkerProtocolFailure.NonFiniteNumber);
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw Fail(WorkerProtocolFailure.DuplicateProperty);
                ValidateValue(p.Value, ref count);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) ValidateValue(item, ref count);
    }
    private static void CheckEscapedUnicode(string raw)
    {
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\') continue;
            if (++i >= raw.Length || raw[i] != 'u') continue;
            var code = int.Parse(raw.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture); i += 4;
            if (code is >= 0xD800 and <= 0xDBFF)
            {
                if (i + 6 >= raw.Length || raw[i + 1] != '\\' || raw[i + 2] != 'u' ||
                    int.Parse(raw.AsSpan(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture) is not (>= 0xDC00 and <= 0xDFFF))
                    throw Fail(WorkerProtocolFailure.InvalidUnicode);
                i += 6;
            }
            else if (code is >= 0xDC00 and <= 0xDFFF) throw Fail(WorkerProtocolFailure.InvalidUnicode);
        }
    }
    private static string Compact(string raw)
    {
        var result = new StringBuilder(raw.Length); var quoted = false; var escaped = false;
        foreach (var c in raw)
        {
            if (quoted) { result.Append(c); if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false; }
            else if (c == '"') { quoted = true; result.Append(c); }
            else if (c is not (' ' or '\t' or '\r' or '\n')) result.Append(c);
        }
        return result.ToString();
    }
    private static WorkerProtocolException Fail(WorkerProtocolFailure code) => new(code);
}
