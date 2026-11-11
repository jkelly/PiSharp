// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/tar.ts.
using System.Runtime.CompilerServices;
using System.Text;

namespace PiSharp.Chord.Hosting.App;

public sealed record TarEntry(string Path, long Size, Stream Data);
public sealed record TarRead(string Path, long Size, bool IsDirectory, Stream Data);

/// <summary>The app archive's deliberately small ustar dialect. PAX, links and special files are refused.</summary>
public static class Tar
{
    private const int Block = 512;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task WriteAsync(Stream target, IAsyncEnumerable<TarEntry> entries, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[65536];
        await foreach (var entry in entries.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            await target.WriteAsync(Header(entry.Path, entry.Size), cancellationToken).ConfigureAwait(false);
            long remaining = entry.Size;
            while (remaining != 0)
            {
                var read = await entry.Data.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new InvalidDataException($"tar: {entry.Path} is shorter than its declared size");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
            if (await entry.Data.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
                throw new InvalidDataException($"tar: {entry.Path} is larger than its declared size");
            await target.WriteAsync(new byte[(Block - entry.Size % Block) % Block], cancellationToken).ConfigureAwait(false);
        }
        await target.WriteAsync(new byte[Block * 2], cancellationToken).ConfigureAwait(false);
    }

    public static async IAsyncEnumerable<TarRead> ReadAsync(Stream source, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var block = new byte[Block];
        while (true)
        {
            await ReadExactlyAsync(source, block, cancellationToken).ConfigureAwait(false);
            if (block.All(value => value == 0)) yield break;
            var checksum = Octal(block.AsSpan(148, 8));
            long sum = 0;
            for (var i = 0; i < Block; i++) sum += i is >= 148 and < 156 ? 32 : block[i];
            if (sum != checksum) throw new InvalidDataException("tar: a header's checksum does not hold");
            var directory = block[156] == (byte)'5';
            if (!directory && block[156] is not (0 or (byte)'0')) throw new InvalidDataException("tar: only regular files and directories are permitted");
            var name = Text(block.AsSpan(0, 100));
            var prefix = Text(block.AsSpan(345, 155));
            var path = prefix.Length == 0 ? name : prefix + "/" + name;
            if (path.EndsWith('/')) path = path[..^1];
            ValidatePath(path);
            var size = Octal(block.AsSpan(124, 12));
            if (directory && size != 0) throw new InvalidDataException("tar: a directory has data");
            var data = new EntryStream(source, size);
            yield return new TarRead(path, size, directory, data);
            await data.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
            await ReadExactlyAsync(source, new byte[(Block - size % Block) % Block], cancellationToken).ConfigureAwait(false);
        }
    }

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains('\0') || path.StartsWith('/') || path.Contains(':'))
            throw new InvalidDataException($"tar: refused path {path}");
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or "." or "..") throw new InvalidDataException($"tar: refused path {path}");
            // Windows collapses trailing dots/spaces and has device names even below ordinary directories.
            if (OperatingSystem.IsWindows())
            {
                var stem = segment.Split('.')[0];
                if (segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || segment.Any(char.IsControl)
                    || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                    || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                    || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9'))
                    throw new InvalidDataException($"tar: refused Windows path {path}");
            }
        }
    }

    private static byte[] Header(string path, long size)
    {
        ValidatePath(path);
        if (size < 0 || size > 0x1ffffffffL) throw new InvalidDataException("tar: invalid file size");
        var block = new byte[Block];
        var name = Utf8.GetBytes(path);
        if (name.Length > 100)
        {
            var slash = path.LastIndexOf('/');
            if (slash < 0 || Utf8.GetByteCount(path[..slash]) > 155 || Utf8.GetByteCount(path[(slash + 1)..]) > 100)
                throw new InvalidDataException($"tar: path is too long: {path}");
            Utf8.GetBytes(path[..slash]).CopyTo(block, 345);
            name = Utf8.GetBytes(path[(slash + 1)..]);
        }
        name.CopyTo(block, 0);
        Field(block, 100, "0000600"); Field(block, 108, "0000000"); Field(block, 116, "0000000");
        Field(block, 124, Convert.ToString(size, 8).PadLeft(11, '0'));
        Field(block, 136, Convert.ToString(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 8).PadLeft(11, '0'));
        Array.Fill(block, (byte)' ', 148, 8);
        block[156] = (byte)'0'; Field(block, 257, "ustar\0" + "00");
        Field(block, 148, Convert.ToString(block.Sum(value => (int)value), 8).PadLeft(6, '0') + "\0 ");
        return block;
    }

    private static void Field(byte[] block, int offset, string value) => Encoding.ASCII.GetBytes(value).CopyTo(block, offset);
    private static string Text(ReadOnlySpan<byte> field)
    {
        var zero = field.IndexOf((byte)0);
        if (zero >= 0)
        {
            if (field[(zero + 1)..].ContainsAnyExcept((byte)0)) throw new InvalidDataException("tar: embedded NUL in header path");
            field = field[..zero];
        }
        try { return Utf8.GetString(field); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("tar: path is not UTF-8", error); }
    }
    private static long Octal(ReadOnlySpan<byte> field)
    {
        long result = 0;
        var text = Encoding.ASCII.GetString(field).Trim('\0', ' ');
        if (text.Length == 0) return 0;
        foreach (var digit in text)
        {
            if (digit is < '0' or > '7') throw new InvalidDataException("tar: malformed octal field");
            try { result = checked(result * 8 + digit - '0'); }
            catch (OverflowException error) { throw new InvalidDataException("tar: numeric field overflow", error); }
        }
        return result;
    }
    private static async Task ReadExactlyAsync(Stream source, Memory<byte> bytes, CancellationToken token)
    {
        try { await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false); }
        catch (EndOfStreamException error) { throw new InvalidDataException("tar: archive ends early", error); }
    }

    private sealed class EntryStream(Stream source, long remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (remaining == 0 || buffer.Length == 0) return 0;
            var read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("tar: archive ends inside an entry");
            remaining -= read; return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
