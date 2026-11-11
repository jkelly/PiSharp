// PiSharp-only: Stream adapter replaying the sniffed prefix and bounding the native read tool to its opened-file snapshot.
namespace PiSharp.Tools.Files;

/// <summary>Replays the sniffed prefix and caps the remaining read at the opened file's snapshot length.</summary>
internal sealed class PrefixReadStream(ReadOnlyMemory<byte> prefix, Stream tail, long? remaining) : Stream
{
    private int _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_position < prefix.Length)
        {
            var count = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.Slice(_position, count).CopyTo(buffer); _position += count; return count;
        }
        if (remaining is { } left) buffer = buffer[..(int)Math.Min(buffer.Length, left)];
        if (buffer.Length == 0) return 0;
        var read = await tail.ReadAsync(buffer, token).ConfigureAwait(false);
        if (remaining is not null) remaining -= read;
        return read;
    }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
