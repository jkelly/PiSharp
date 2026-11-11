// PiSharp-only: Bounded .NET stream adapters for the source-derived sync batch/line codec; see sync-1.5.2-chord-sync-codec.md.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace PiSharp.Chord.Sync;

/// <summary>Bounded gzip adapters. The producer owns/disposes its source; abandoning output cancels and joins that producer.</summary>
internal static class BodyStreams
{
    private const int ChunkSize = 64 * 1024;
    // Header, final empty deflate block, then zero CRC32/ISIZE. An unused GZipStream emits no member on disposal.
    private static readonly byte[] EmptyGzip = [0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 0xff, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    internal static async IAsyncEnumerable<ReadOnlyMemory<byte>> Gzip(IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var output = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        async Task Produce()
        {
            try
            {
                await using (var sink = new ChunkWriter(output.Writer, stop.Token))
                {
                    GZipStream? gzip = null;
                    try
                    {
                        await foreach (var chunk in chunks.WithCancellation(stop.Token).ConfigureAwait(false))
                        {
                            if (chunk.IsEmpty) continue;
                            gzip ??= new GZipStream(sink, CompressionLevel.Optimal, leaveOpen: true);
                            await gzip.WriteAsync(chunk, stop.Token).ConfigureAwait(false);
                        }
                        if (gzip is null) await sink.WriteAsync(EmptyGzip, stop.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (gzip is not null) await gzip.DisposeAsync().ConfigureAwait(false);
                    }
                }
                output.Writer.TryComplete();
            }
            catch (Exception error) { output.Writer.TryComplete(error); }
        }
        var producing = Produce();
        try
        {
            await foreach (var chunk in output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return chunk;
        }
        finally
        {
            stop.Cancel();
            await producing.ConfigureAwait(false);
        }
    }

    internal static async IAsyncEnumerable<ReadOnlyMemory<byte>> Gunzip(IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        await using var source = new ChunkReader(chunks.GetAsyncEnumerator(cancellationToken));
        await using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
        var crc = uint.MaxValue;
        uint length = 0;
        while (true)
        {
            var bytes = new byte[ChunkSize];
            var read = await gzip.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            crc = UpdateCrc(crc, bytes.AsSpan(0, read));
            length = unchecked(length + (uint)read);
            yield return bytes.AsMemory(0, read);
        }
        // GZipStream can treat premature EOF as successful completion. Check the wire trailer ourselves too.
        await source.DrainAsync(cancellationToken).ConfigureAwait(false);
        source.CheckTrailer(~crc, length);
    }

    private static readonly uint[] CrcTable = MakeCrcTable();
    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320u);
            table[i] = value;
        }
        return table;
    }
    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        return crc;
    }

    private abstract class BodyStream : Stream
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ChunkWriter(ChannelWriter<ReadOnlyMemory<byte>> writer, CancellationToken stop) : BodyStream
    {
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            stop.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            while (!buffer.IsEmpty)
            {
                var count = Math.Min(buffer.Length, ChunkSize);
                // Compression owns/reuses its input buffer, so ownership transfers only after a bounded copy.
                await writer.WriteAsync(buffer[..count].ToArray(), stop).ConfigureAwait(false);
                buffer = buffer[count..];
            }
        }
    }

    private sealed class ChunkReader(IAsyncEnumerator<ReadOnlyMemory<byte>> source) : BodyStream
    {
        private ReadOnlyMemory<byte> pending;
        private readonly byte[] tail = new byte[8];
        private long received;
        private bool ended;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty) return 0;
            while (pending.IsEmpty && !ended)
            {
                ended = !await source.MoveNextAsync().ConfigureAwait(false);
                if (!ended) pending = source.Current;
            }
            var count = Math.Min(buffer.Length, pending.Length);
            pending[..count].CopyTo(buffer);
            Remember(pending.Span[..count]);
            pending = pending[count..];
            return count;
        }
        private void Remember(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length >= tail.Length) bytes[^8..].CopyTo(tail);
            else if (!bytes.IsEmpty)
            {
                tail.AsSpan(bytes.Length).CopyTo(tail);
                bytes.CopyTo(tail.AsSpan(tail.Length - bytes.Length));
            }
            received += bytes.Length;
        }
        internal async Task DrainAsync(CancellationToken cancellationToken)
        {
            var scratch = new byte[ChunkSize];
            while (await ReadAsync(scratch, cancellationToken).ConfigureAwait(false) != 0) { }
        }
        internal void CheckTrailer(uint crc, uint length)
        {
            if (received < 20 || BinaryPrimitives.ReadUInt32LittleEndian(tail) != crc ||
                BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(4)) != length)
                throw new InvalidDataException("The gzip body is truncated or has an invalid trailer.");
        }
        public override async ValueTask DisposeAsync()
        {
            await source.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
