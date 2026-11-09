// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): packages/env/daemon/src/frame.rs and docs/protocol.md.
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Env.Protocol;

public enum FrameType : byte { Request = 1, Result = 2, Error = 3, Event = 4, Cancel = 5, Ping = 6 }

/// <summary>One protocol-1 frame. Id zero is reserved for connection-wide messages.</summary>
public sealed record ProtocolFrame(FrameType Type, uint Id, JsonObject Json, ReadOnlyMemory<byte> Payload = default);

/// <summary>A complete frame had malformed JSON. A daemon can reply EINVAL and continue at the next frame.</summary>
public sealed class ProtocolJsonException(FrameType type, uint id, string message, Exception? inner = null)
    : IOException(message, inner)
{
    public FrameType Type { get; } = type;
    public uint Id { get; } = id;
}

/// <summary>Bounded, big-endian pi-env framing. The caller serializes concurrent writes.</summary>
public static class FrameCodec
{
    public const int MaxFrameBytes = 16 * 1024 * 1024;
    public const int MaxPayloadBytes = MaxFrameBytes - 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(ProtocolFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(frame.Json);
        var json = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(json)) { frame.Json.WriteTo(writer); writer.Flush(); }
        var length = 9L + json.WrittenCount + frame.Payload.Length;
        if (length > MaxFrameBytes) throw new InvalidDataException("pi-env frame exceeds 16 MiB.");
        var bytes = new byte[checked((int)length + 4)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)length);
        bytes[4] = (byte)frame.Type;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(5), frame.Id);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(9), (uint)json.WrittenCount);
        json.WrittenSpan.CopyTo(bytes.AsSpan(13));
        frame.Payload.Span.CopyTo(bytes.AsSpan(13 + json.WrittenCount));
        return bytes;
    }

    /// <summary>Null only for clean EOF before a frame; partial or malformed frames throw.</summary>
    public static async ValueTask<ProtocolFrame?> ReadAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var header = new byte[4];
        var first = await input.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await input.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (size is < 9 or > MaxFrameBytes) throw new InvalidDataException("Invalid pi-env frame length.");
        var body = new byte[(int)size];
        await input.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return DecodeBody(body);
    }

    public static async ValueTask WriteAsync(Stream output, ProtocolFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        await output.WriteAsync(Encode(frame), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ProtocolFrame DecodeBody(byte[] body)
    {
        var jsonLength = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(5));
        if (jsonLength > body.Length - 9) throw new InvalidDataException("Invalid pi-env JSON length.");
        JsonObject json;
        try
        {
            // Decode strictly before parsing: malformed UTF-8 must not silently alter a path or operation.
            json = JsonNode.Parse(StrictUtf8.GetString(body, 9, (int)jsonLength)) as JsonObject
                ?? throw new ProtocolJsonException((FrameType)body[0], BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1)), "pi-env frame JSON must be an object.");
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        {
            throw new ProtocolJsonException((FrameType)body[0], BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1)), "Invalid pi-env JSON.", error);
        }
        return new((FrameType)body[0], BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1)), json,
            body.AsMemory(9 + (int)jsonLength).ToArray());
    }
}
