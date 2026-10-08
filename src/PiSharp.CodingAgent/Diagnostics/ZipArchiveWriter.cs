// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/zip.ts.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PiSharp.CodingAgent.Diagnostics;

/// <summary>One archive member: a UTF-8 name and its text (UTF-8) or bytes.</summary>
public sealed record ZipEntry(string Name, ReadOnlyMemory<byte> Data)
{
    public ZipEntry(string name, string text) : this(name, Encoding.UTF8.GetBytes(text)) { }
}

/// <summary>
/// zip.ts: the small, classic ZIP archives used by bug reports, laid out byte for byte as the source writes them: entries in
/// order, each a 30-byte local header (version 20, flag 0x0800 UTF-8 names, method 8 deflate, one shared DOS time and date
/// from the local clock with the year clamped to 1980, CRC-32, sizes) followed by the name and the raw deflate data, then the
/// 46-byte central records and a 22-byte end record; no extra fields, comments or data descriptors. The deflate stream comes
/// from <see cref="DeflateStream"/> at <see cref="CompressionLevel.Optimal"/>, so compressed bytes can differ from Node's zlib
/// (level 6) while every header field other than the compressed size matches.
/// </summary>
public static class ZipArchiveWriter
{
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(index =>
    {
        var value = (uint)index;
        for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
        return value;
    }).ToArray();

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    internal static (ushort Time, ushort Day) DosDateTime(DateTime local) => (
        (ushort)((local.Hour << 11) | (local.Minute << 5) | (local.Second >> 1)),
        (ushort)(((Math.Max(1980, local.Year) - 1980) << 9) | (local.Month << 5) | local.Day));

    /// <summary>The archive bytes; <paramref name="timeProvider"/> supplies the local time stamped on every entry.</summary>
    public static byte[] CreateZipArchive(IReadOnlyList<ZipEntry> entries, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var (time, day) = DosDateTime((timeProvider ?? TimeProvider.System).GetLocalNow().DateTime);
        using var files = new MemoryStream(); using var directory = new MemoryStream();
        uint offset = 0; var localBuffer = new byte[30]; var centralBuffer = new byte[46];
        foreach (var entry in entries)
        {
            var name = Encoding.UTF8.GetBytes(entry.Name); var data = entry.Data.Span;
            byte[] compressed;
            using (var buffer = new MemoryStream())
            {
                using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(data);
                compressed = buffer.ToArray();
            }
            var checksum = Crc32(data);

            Span<byte> local = localBuffer; local.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(local, 0x04034b50);
            BinaryPrimitives.WriteUInt16LittleEndian(local[4..], 20);
            BinaryPrimitives.WriteUInt16LittleEndian(local[6..], 0x0800);
            BinaryPrimitives.WriteUInt16LittleEndian(local[8..], 8);
            BinaryPrimitives.WriteUInt16LittleEndian(local[10..], time);
            BinaryPrimitives.WriteUInt16LittleEndian(local[12..], day);
            BinaryPrimitives.WriteUInt32LittleEndian(local[14..], checksum);
            BinaryPrimitives.WriteUInt32LittleEndian(local[18..], (uint)compressed.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(local[22..], (uint)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(local[26..], (ushort)name.Length);

            Span<byte> central = centralBuffer; central.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(central, 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(central[4..], 20);
            BinaryPrimitives.WriteUInt16LittleEndian(central[6..], 20);
            BinaryPrimitives.WriteUInt16LittleEndian(central[8..], 0x0800);
            BinaryPrimitives.WriteUInt16LittleEndian(central[10..], 8);
            BinaryPrimitives.WriteUInt16LittleEndian(central[12..], time);
            BinaryPrimitives.WriteUInt16LittleEndian(central[14..], day);
            BinaryPrimitives.WriteUInt32LittleEndian(central[16..], checksum);
            BinaryPrimitives.WriteUInt32LittleEndian(central[20..], (uint)compressed.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(central[24..], (uint)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(central[28..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(central[42..], offset);

            files.Write(local); files.Write(name); files.Write(compressed);
            directory.Write(central); directory.Write(name);
            offset += (uint)(local.Length + name.Length + compressed.Length);
        }
        Span<byte> end = stackalloc byte[22]; end.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], (ushort)entries.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], (ushort)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], (uint)directory.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], offset);
        directory.WriteTo(files); files.Write(end);
        return files.ToArray();
    }

    public static Task WriteZipArchiveAsync(string filePath, IReadOnlyList<ZipEntry> entries, TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default) =>
        File.WriteAllBytesAsync(filePath, CreateZipArchive(entries, timeProvider), cancellationToken);
}
