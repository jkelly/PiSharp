// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 next/link/part.ts, values.ts and next-node/compression.ts.
using System.Collections;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Contracts;

namespace PiSharp.Chord.Link;

/// <summary>Per-part limits, in bytes. Null means the default for the link's trust level.</summary>
public sealed record PartLimits(long? Part = null, long? Decoded = null);
/// <summary>The bounded outcome of raw DEFLATE decompression.</summary>
public abstract record InflateResult
{
    public sealed record Bytes(ReadOnlyMemory<byte> Value) : InflateResult;
    public sealed record TooLarge : InflateResult;
    public sealed record Corrupt(Exception Error) : InflateResult;
}
/// <summary>Raw DEFLATE compression. Inflation must stop before allocating beyond the supplied output limit.</summary>
public interface ICompression
{
    byte[] Deflate(ReadOnlyMemory<byte> bytes);
    InflateResult Inflate(ReadOnlyMemory<byte> bytes, long limit);
}
/// <summary>Native raw DEFLATE, using the fast compression level and bounded incremental inflation.</summary>
public sealed class NativeCompression : ICompression
{
    public static NativeCompression Instance { get; } = new();
    public byte[] Deflate(ReadOnlyMemory<byte> bytes)
    {
        // DeflateStream can emit nothing when no nonempty Write occurred; raw DEFLATE still needs a final block.
        if (bytes.IsEmpty) return [0x03, 0x00];
        using var output = new MemoryStream();
        using (var stream = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true)) stream.Write(bytes.Span);
        return output.ToArray();
    }
    public InflateResult Inflate(ReadOnlyMemory<byte> bytes, long limit)
    {
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        try
        {
            if (RawDeflate.Measure(bytes, limit) > limit) return new InflateResult.TooLarge();
            using var input = new MemoryStream(bytes.ToArray(), writable: false);
            using var stream = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, Math.Min(int.MaxValue, limit - output.Length + (limit == long.MaxValue ? 0 : 1))));
                if (count == 0) return new InflateResult.Bytes(output.ToArray());
                if (count > limit - output.Length) return new InflateResult.TooLarge();
                output.Write(buffer, 0, count);
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException) { return new InflateResult.Corrupt(e); }
    }
}

internal readonly record struct Limits(long Part, long Decoded)
{
    public static Limits ForLink(bool trusted, PartLimits? limits = null)
    {
        var wire = limits?.Part ?? (trusted ? long.MaxValue : 16L * 1024 * 1024);
        var decoded = limits?.Decoded ?? (trusted ? long.MaxValue : 64L * 1024 * 1024);
        if (wire < 0 || decoded < 0) throw new ArgumentOutOfRangeException(nameof(limits));
        return new(wire, decoded);
    }
}

internal abstract record Part
{
    public sealed record Text(string Value) : Part;
    public sealed record Bytes(ReadOnlyMemory<byte> Value) : Part;
    public sealed record Deflated(ReadOnlyMemory<byte> Value) : Part { public InflateResult? Inflated { get; set; } }
    public static implicit operator Part(string text) => new Text(text);
    public static implicit operator Part(byte[] bytes) => new Bytes(bytes);
}

internal static class Parts
{
    internal const int CompressAbove = 16 * 1024;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    // TextEncoder replaces an isolated UTF-16 surrogate, unlike strict received UTF-8.
    internal static readonly UTF8Encoding OutgoingUtf8 = new(false, false);
    internal static Part Origin(string json, ICompression? compression, ChordContext? context = null)
    {
        if (compression is null || (long)json.Length * 3 <= CompressAbove) return new Part.Text(json);
        var bytes = OutgoingUtf8.GetBytes(json);
        if (bytes.Length <= CompressAbove) return new Part.Bytes(bytes);
        context?.Count("chord.link.deflates");
        var zipped = compression.Deflate(bytes);
        return zipped.Length < bytes.Length ? new Part.Deflated(zipped) : new Part.Bytes(bytes);
    }
    internal static long Size(Part part) => part switch
    {
        Part.Text x => OutgoingUtf8.GetByteCount(x.Value), Part.Bytes x => x.Value.Length,
        Part.Deflated x => x.Value.Length, _ => throw new ArgumentException("part"),
    };
    internal static ChordError? Oversized(Part part, long limit) => Size(part) is var size && size > limit ? TooLarge(size, limit, "wire") : null;
    internal static Part Open(Part part, Limits limits, ICompression? compression = null, ChordContext? context = null)
    {
        if (Oversized(part, limits.Part) is { } error) throw error;
        if (part is not Part.Deflated zipped)
        {
            if (Size(part) > limits.Decoded) throw TooLarge(Size(part), limits.Decoded, "decoded");
            return part;
        }
        var result = zipped.Inflated;
        if (result is null)
        {
            if (compression is null) throw new ArgumentException("part: a deflated part this host did not inflate");
            context?.Count("chord.link.inflates");
            result = compression.Inflate(zipped.Value, limits.Decoded);
        }
        return result switch
        {
            InflateResult.Bytes x when x.Value.Length <= limits.Decoded => new Part.Bytes(x.Value),
            InflateResult.Bytes => throw TooLarge(null, limits.Decoded, "decoded"),
            InflateResult.TooLarge => throw TooLarge(null, limits.Decoded, "decoded"),
            InflateResult.Corrupt x => throw new ArgumentException("part: no raw deflate stream", x.Error),
            _ => throw new ArgumentException("part: unknown inflate outcome"),
        };
    }
    internal static async Task InflateAsync(Part.Deflated part, long limit, CancellationToken cancellationToken, ChordContext? context = null)
    {
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        context?.Count("chord.link.inflates");
        // MemoryStream/DeflateStream often complete every ReadAsync synchronously. Run the CPU work away from
        // the host dispatcher so that link closure can cancel it while the channel holds subsequent messages.
        var outcome = await Task.Run<InflateResult?>(() =>
        {
            try
            {
                if (RawDeflate.Measure(part.Value, limit, cancellationToken) > limit) return new InflateResult.TooLarge();
                using var input = new MemoryStream(part.Value.ToArray(), writable: false);
                using var inflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var readSize = (int)Math.Min(buffer.Length, Math.Min(int.MaxValue, limit - output.Length + (limit == long.MaxValue ? 0 : 1)));
                    var count = inflate.Read(buffer, 0, readSize);
                    if (count == 0) return new InflateResult.Bytes(output.ToArray());
                    if (count > limit - output.Length) return new InflateResult.TooLarge();
                    output.Write(buffer, 0, count);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
            catch (Exception e) when (e is InvalidDataException or IOException) { return new InflateResult.Corrupt(e); }
        }).ConfigureAwait(false);
        if (!cancellationToken.IsCancellationRequested) part.Inflated = outcome;
    }
    private static ChordError TooLarge(long? size, long limit, string measure) =>
        new("too_large", $"{(size is null ? "a part" : $"a part of {size} bytes")} is over the {measure} limit of {limit} bytes", "part");
    internal static string Text(Part part) => part switch
    {
        Part.Text x => x.Value, Part.Bytes x => Utf8.GetString(x.Value.Span),
        _ => throw new ArgumentException("part: deflated data must be opened first"),
    };
    internal static JsonNode? Parse(Part part) => ParseJson(Text(part));
    internal static JsonNode? ParseJson(string json)
    {
        // Keep strings as JsonElements. JsonUtf16.MutableNode uses write-only RawJson wrappers for isolated
        // surrogates, while these nodes must be readable by ChordJson.Primitive/AsString at the wire boundary.
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
        return Mutable(document.RootElement);
        static JsonNode? Mutable(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var result = new JsonObject();
                foreach (var property in value.EnumerateObject()) result[JsonUtf16.GetName(property)] = Mutable(property.Value);
                return result;
            }
            if (value.ValueKind == JsonValueKind.Array) return new JsonArray(value.EnumerateArray().Select(Mutable).ToArray());
            return value.ValueKind == JsonValueKind.Null ? null : JsonValue.Create(value.Clone());
        }
    }
    internal static JsonNode? ParseJson(ReadOnlyMemory<byte> bytes) => ParseJson(Utf8.GetString(bytes.Span));
    internal static string Tagged(Part part) => part is Part.Deflated x ? "[1," + ChordJson.Quote(Convert.ToBase64String(x.Value.Span)) + "]" : "[0," + Text(part) + "]";
    internal static Part FromTagged(JsonNode? raw)
    {
        if (raw is not JsonArray { Count: 2 } pair) throw new ArgumentException("text: a part is no [codec, payload] pair");
        if (ChordJson.AsNumber(pair[0]) == 0) return new Part.Text(ChordJson.Stringify(pair[1]));
        if (ChordJson.AsNumber(pair[0]) == 1 && ChordJson.AsString(pair[1]) is { } b64)
        {
            try
            {
                // WHATWG atob accepts omitted padding and ASCII whitespace, as the portable text codec does.
                var compact = string.Concat(b64.Where(c => c is not (' ' or '\t' or '\r' or '\n' or '\f')));
                if (compact.Length % 4 == 1 || compact.Length % 4 != 0 && compact.Contains('=')) throw new FormatException();
                compact = compact.PadRight((compact.Length + 3) / 4 * 4, '=');
                return new Part.Deflated(Convert.FromBase64String(compact));
            }
            catch (FormatException e) { throw new ArgumentException("text: invalid base64", e); }
        }
        throw new ArgumentException("text: a part of an unknown codec");
    }
    internal static (string Json, IReadOnlyList<ReadOnlyMemory<byte>>? Segments) Payload(object? value, bool bytes)
    {
        var segments = bytes ? new List<ReadOnlyMemory<byte>>() : null;
        JsonNode? ConvertValue(object? obj)
        {
            if (obj is null) return null;
            if (segments is not null && obj is byte[] or ReadOnlyMemory<byte> or Memory<byte>)
            {
                segments.Add(obj switch { byte[] b => b, ReadOnlyMemory<byte> b => b, Memory<byte> b => b, _ => default });
                return new JsonObject { ["$bytes"] = segments.Count - 1 };
            }
            if (segments is not null && obj is JsonValue byteValue)
            {
                ReadOnlyMemory<byte>? content = byteValue.TryGetValue<byte[]>(out var arrayBytes) ? arrayBytes :
                    byteValue.TryGetValue<ReadOnlyMemory<byte>>(out var memoryBytes) ? memoryBytes :
                    byteValue.TryGetValue<Memory<byte>>(out var mutableBytes) ? mutableBytes : null;
                if (content is { } memory)
                {
                    segments.Add(memory);
                    return new JsonObject { ["$bytes"] = segments.Count - 1 };
                }
            }
            if (obj is JsonObject jo)
            {
                if (segments is not null && jo.ContainsKey("$bytes")) throw DataInternal.Invalid("args", "\"$bytes\" is reserved in values crossing a link");
                var result = new JsonObject(); foreach(var p in jo) result[p.Key] = ConvertValue(p.Value); return result;
            }
            if (obj is JsonArray ja) return new JsonArray(ja.Select(v => ConvertValue(v)).ToArray());
            if (obj is JsonNode jn) return jn.DeepClone();
            if (obj is IDictionary dictionary)
            {
                var result = new JsonObject(); foreach(DictionaryEntry p in dictionary)
                {
                    var key = p.Key.ToString()!;
                    if (segments is not null && key == "$bytes") throw DataInternal.Invalid("args", "\"$bytes\" is reserved in values crossing a link");
                    result[key] = ConvertValue(p.Value);
                }
                return result;
            }
            if (obj is IEnumerable items and not string) { var result = new JsonArray(); foreach (var item in items) result.Add(ConvertValue(item)); return result; }
            return JsonSerializer.SerializeToNode(obj);
        }
        return (ChordJson.Stringify(ConvertValue(value)), segments);
    }
    internal static object? ParsePayload(Part opened, IReadOnlyList<ReadOnlyMemory<byte>>? segments)
    {
        var node = Parse(opened);
        if (segments is null) return node;
        object? Resolve(JsonNode? item)
        {
            if (item is JsonObject obj)
            {
                if (obj.TryGetPropertyValue("$bytes", out var raw))
                {
                    var index = ChordJson.AsNumber(raw);
                    if (obj.Count != 1 || index is null || index < 0 || index != Math.Floor(index.Value) || index >= segments.Count)
                        throw new ArgumentException("payload: a malformed byte segment reference");
                    return segments[(int)index.Value];
                }
                return obj.ToDictionary(p => p.Key, p => Resolve(p.Value), StringComparer.Ordinal);
            }
            if (item is JsonArray arr) return arr.Select(Resolve).ToArray();
            return item;
        }
        return Resolve(node);
    }
}
