// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/bedrock-converse-stream.ts consumes the ConverseStream
// event stream through @smithy/eventstream-codec. Native port of the application/vnd.amazon.eventstream binary framing: a 12-byte
// prelude (total length, headers length, prelude CRC32), typed headers, payload and a message CRC32 over everything before it.
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;

namespace PiSharp.AI.Protocols.Bedrock;

public enum AwsEventStreamFailure { PreludeChecksum, MessageChecksum, InvalidLength, InvalidHeader, TruncatedMessage, MessageTooLarge }

public sealed class AwsEventStreamException(AwsEventStreamFailure failure, string message) : Exception(message)
{
    public AwsEventStreamFailure Failure { get; } = failure;
}

/// <summary>One decoded message. Header values keep their wire type as a CLR value (bool, byte, short, int, long, byte[],
/// string, DateTimeOffset, Guid).</summary>
public sealed record AwsEventStreamMessage(ImmutableDictionary<string, object> Headers, byte[] Payload)
{
    public string? HeaderString(string name) => Headers.TryGetValue(name, out var value) ? value as string : null;
}

/// <summary>CRC-32 (IEEE 802.3, reflected 0xEDB88320), as the event stream codec uses.</summary>
public static class Crc32
{
    private static readonly uint[] Table = Build();
    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint index = 0; index < 256; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }
    public static uint Compute(ReadOnlySpan<byte> data, uint seed = 0)
    {
        var crc = ~seed;
        foreach (var unit in data) crc = Table[(crc ^ unit) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}

public static class AwsEventStream
{
    public const int PreludeLength = 12;
    public const int MinimumMessageLength = 16;
    /// <summary>The codec's limit for one message (16 MiB).</summary>
    public const int MaximumMessageLength = 16 * 1024 * 1024;

    /// <summary>Decodes complete messages from a stream that may deliver them in arbitrary fragments.</summary>
    public static async IAsyncEnumerable<AwsEventStreamMessage> DecodeAsync(Stream stream, int maximumMessageLength = MaximumMessageLength,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[64 * 1024]; var length = 0; var read = new byte[16 * 1024];
        while (true)
        {
            while (length >= PreludeLength)
            {
                var total = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0, 4)), int.MaxValue);
                ValidatePrelude(buffer.AsSpan(0, PreludeLength), maximumMessageLength);
                if (length < total) { if (buffer.Length < total) Array.Resize(ref buffer, total); break; }
                yield return Decode(buffer.AsSpan(0, total), maximumMessageLength);
                Buffer.BlockCopy(buffer, total, buffer, 0, length - total); length -= total;
            }
            var count = await stream.ReadAsync(read.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (length != 0) throw new AwsEventStreamException(AwsEventStreamFailure.TruncatedMessage, "Event stream ended inside a message.");
                yield break;
            }
            if (buffer.Length - length < count) Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + count));
            Buffer.BlockCopy(read, 0, buffer, length, count); length += count;
        }
    }

    private static void ValidatePrelude(ReadOnlySpan<byte> prelude, int maximum)
    {
        var total = BinaryPrimitives.ReadUInt32BigEndian(prelude);
        var headers = BinaryPrimitives.ReadUInt32BigEndian(prelude[4..]);
        var expected = BinaryPrimitives.ReadUInt32BigEndian(prelude[8..]);
        if (Crc32.Compute(prelude[..8]) != expected)
            throw new AwsEventStreamException(AwsEventStreamFailure.PreludeChecksum, "The prelude checksum specified in the message does not match the calculated CRC32 checksum.");
        if (total < MinimumMessageLength || headers > total - MinimumMessageLength)
            throw new AwsEventStreamException(AwsEventStreamFailure.InvalidLength, "Invalid event stream message length.");
        if (total > (uint)maximum) throw new AwsEventStreamException(AwsEventStreamFailure.MessageTooLarge, "Event stream message exceeds the size limit.");
    }

    /// <summary>Decodes one complete message (prelude through message CRC).</summary>
    public static AwsEventStreamMessage Decode(ReadOnlySpan<byte> message, int maximumMessageLength = MaximumMessageLength)
    {
        if (message.Length < MinimumMessageLength) throw new AwsEventStreamException(AwsEventStreamFailure.InvalidLength, "Event stream message is too short.");
        ValidatePrelude(message[..PreludeLength], maximumMessageLength);
        var total = (int)BinaryPrimitives.ReadUInt32BigEndian(message);
        var headersLength = (int)BinaryPrimitives.ReadUInt32BigEndian(message[4..]);
        if (total != message.Length) throw new AwsEventStreamException(AwsEventStreamFailure.InvalidLength, "Event stream message length mismatch.");
        var expected = BinaryPrimitives.ReadUInt32BigEndian(message[(total - 4)..]);
        if (Crc32.Compute(message[..(total - 4)]) != expected)
            throw new AwsEventStreamException(AwsEventStreamFailure.MessageChecksum, "The message checksum did not match the expected value.");
        var headers = ParseHeaders(message.Slice(PreludeLength, headersLength));
        var payload = message.Slice(PreludeLength + headersLength, total - PreludeLength - headersLength - 4).ToArray();
        return new(headers, payload);
    }

    private static ImmutableDictionary<string, object> ParseHeaders(ReadOnlySpan<byte> source)
    {
        var span = source.ToArray();
        var headers = ImmutableDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);
        var position = 0;
        byte Byte() { if (position >= span.Length) throw Invalid(); return span[position++]; }
        byte[] Take(int count)
        {
            if (count < 0 || position + count > span.Length) throw Invalid();
            var slice = span.AsSpan(position, count).ToArray(); position += count; return slice;
        }
        while (position < span.Length)
        {
            var nameLength = Byte();
            var name = Encoding.UTF8.GetString(Take(nameLength));
            object value = Byte() switch
            {
                0 => true,
                1 => false,
                2 => (sbyte)Take(1)[0],
                3 => BinaryPrimitives.ReadInt16BigEndian(Take(2)),
                4 => BinaryPrimitives.ReadInt32BigEndian(Take(4)),
                5 => BinaryPrimitives.ReadInt64BigEndian(Take(8)),
                6 => Take(BinaryPrimitives.ReadUInt16BigEndian(Take(2))),
                7 => Encoding.UTF8.GetString(Take(BinaryPrimitives.ReadUInt16BigEndian(Take(2)))),
                8 => DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(Take(8))),
                9 => new Guid(Take(16), bigEndian: true),
                _ => throw Invalid()
            };
            headers[name] = value;
        }
        return headers.ToImmutable();
        static AwsEventStreamException Invalid() => new(AwsEventStreamFailure.InvalidHeader, "Invalid event stream header.");
    }

    /// <summary>Encodes a message (string headers only), for fakes and tests.</summary>
    public static byte[] Encode(IEnumerable<KeyValuePair<string, string>> headers, ReadOnlySpan<byte> payload)
    {
        using var headerBytes = new MemoryStream();
        var size = new byte[2];
        foreach (var (name, value) in headers)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name); var valueBytes = Encoding.UTF8.GetBytes(value);
            headerBytes.WriteByte((byte)nameBytes.Length); headerBytes.Write(nameBytes); headerBytes.WriteByte(7);
            BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)valueBytes.Length);
            headerBytes.Write(size); headerBytes.Write(valueBytes);
        }
        var headerArray = headerBytes.ToArray();
        var total = PreludeLength + headerArray.Length + payload.Length + 4;
        var message = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(message, (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), (uint)headerArray.Length);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(8), Crc32.Compute(message.AsSpan(0, 8)));
        headerArray.CopyTo(message, PreludeLength);
        payload.CopyTo(message.AsSpan(PreludeLength + headerArray.Length));
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(total - 4), Crc32.Compute(message.AsSpan(0, total - 4)));
        return message;
    }
}
