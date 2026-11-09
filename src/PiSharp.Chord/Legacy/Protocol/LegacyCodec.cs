// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/link.ts.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.Chord.Legacy;

/// <summary>A WebSocket message: text JSON or [u32 BE envelope bytes][envelope JSON][raw DEFLATE].</summary>
public sealed record LegacyMessage(string? Text, byte[]? Binary)
{
    public bool IsBinary => Binary is not null;
    public long Size => Binary?.LongLength ?? Text?.Length ?? 0;
}

/// <summary>The legacy packed transport encoding. Compression uses JavaScript UTF-16 character counts.</summary>
public static class LegacyCodec
{
    public const int PackThreshold = 64 * 1024;
    internal static string? PayloadField(LegacyFrame frame) => frame.Type switch
    {
        "call" when frame.Has("args") => "args",
        "result" or "state" when frame.Has("value") => "value",
        _ => null,
    };
    private static byte[] Deflate(string json)
    {
        using var output = new MemoryStream();
        // Match upstream's numeric level 1 and default strategy. The raw DEFLATE bitstream is not canonical:
        // .NET's zlib-ng and Node's zlib may choose different Huffman blocks for the same input and options.
        using (var compressor = new DeflateStream(output, new ZLibCompressionOptions
        {
            CompressionLevel = 1,
            CompressionStrategy = ZLibCompressionStrategy.Default,
        }, leaveOpen: true))
            compressor.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }
    private static byte[]? PackedBytes(LegacyFrame frame, bool compress, string? json) => frame.Packed?.Bytes.ToArray() ??
        (compress && json is { Length: > PackThreshold } ? Deflate(json) : null);
    public static JsonObject PackObject(LegacyFrame frame, bool compress = true)
    {
        var json = frame.Json;
        var field = PayloadField(frame);
        if (field is null) return json;
        var bytes = PackedBytes(frame, compress, frame.Packed is null ? ChordJson.Stringify(json[field]) : null);
        if (bytes is null) return json;
        json.Remove(field);
        json["z"] = new JsonArray(field, Convert.ToBase64String(bytes));
        return json;
    }
    public static LegacyFrame UnpackObject(JsonObject message)
    {
        if (!message.TryGetPropertyValue("z", out var packed)) return new(message);
        if (packed is not JsonArray z || z.Count < 2 || ChordJson.AsString(z[0]) is not ("args" or "value") || ChordJson.AsString(z[1]) is not { } encoded)
            throw new FormatException("bad packed payload");
        var json = (JsonObject)message.DeepClone(); json.Remove("z");
        return new(json, new LegacyPacked(ChordJson.AsString(z[0])!, Convert.FromBase64String(encoded)));
    }
    public static LegacyMessage Encode(LegacyFrame frame, bool compress = false)
    {
        var envelope = frame.Json;
        var field = PayloadField(frame);
        if (field is null) return new(ChordJson.Stringify(envelope), null);
        var json = frame.Packed is null ? ChordJson.Stringify(envelope[field]) : null;
        var bytes = PackedBytes(frame, compress, json);
        envelope.Remove(field);
        if (bytes is null)
        {
            var head = ChordJson.Stringify(envelope);
            return new(head[..^1] + "," + ChordJson.Stringify(JsonValue.Create(field)) + ":" + json + "}", null);
        }
        envelope["z"] = field;
        var header = Encoding.UTF8.GetBytes(ChordJson.Stringify(envelope));
        var message = new byte[checked(4 + header.Length + bytes.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(message, (uint)header.Length);
        header.CopyTo(message, 4); bytes.CopyTo(message, 4 + header.Length);
        return new(null, message);
    }
    public static LegacyFrame Decode(LegacyMessage message, bool acceptPacked = true)
    {
        if (message.Binary is not { } data)
        {
            var parsed = Parse(message.Text ?? throw new FormatException("missing message"));
            var frame = UnpackObject(parsed);
            if (ChordJson.AsString(frame.Json["t"]) is null) throw new FormatException("not a frame");
            return frame;
        }
        if (!acceptPacked) throw new FormatException("packed frames are not accepted");
        if (data.Length < 4) throw new FormatException("short envelope");
        var count = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (count > data.Length - 4) throw new FormatException("short envelope");
        var envelope = Parse(Encoding.UTF8.GetString(data, 4, checked((int)count)));
        if (ChordJson.AsString(envelope["z"]) is not ("args" or "value")) throw new FormatException("bad packed payload");
        var field = ChordJson.AsString(envelope["z"])!; envelope.Remove("z");
        return new(envelope, new LegacyPacked(field, data.AsSpan(4 + (int)count)));
    }
    public static JsonNode? Unpack(LegacyPacked payload, int? maximumBytes = null) => ParseValue(InflateText(payload, maximumBytes));
    private static string InflateText(LegacyPacked payload, int? maximumBytes)
    {
        using var compressed = new MemoryStream(payload.Bytes.ToArray(), writable: false);
        using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = inflater.Read(buffer)) > 0)
        {
            if (maximumBytes is { } limit && output.Length + count > limit) throw new InvalidDataException("inflated payload exceeds limit");
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    public static string InflateMessage(ReadOnlySpan<byte> message, int? maximumBytes = null)
    {
        var frame = Decode(new(null, message.ToArray()));
        var payload = frame.Packed!;
        var json = frame.Json;
        var head = ChordJson.Stringify(json);
        return head[..^1] + "," + ChordJson.Stringify(JsonValue.Create(payload.Field)) + ":" + InflateText(payload, maximumBytes) + "}";
    }
    internal static JsonNode? ParseValue(string json)
    {
        using var document = JsonDocument.Parse(json);
        return MutableValue(document.RootElement);
    }
    // MutableNode's RawJson leaf supports writing a lone surrogate, but the runtime also needs to inspect it.
    // An owned JsonElement retains its exact escaped JSON and ChordJson reads its code units with JsonUtf16.GetString.
    private static JsonNode? MutableValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new JsonObject(element.EnumerateObject().Select(property =>
            new KeyValuePair<string, JsonNode?>(JsonUtf16.GetName(property), MutableValue(property.Value)))),
        JsonValueKind.Array => new JsonArray(element.EnumerateArray().Select(MutableValue).ToArray()),
        JsonValueKind.Null => null,
        _ => JsonValue.Create(element.Clone()),
    };
    internal static JsonObject Parse(string json) => ParseValue(json) as JsonObject ?? throw new FormatException("not a frame");
}
