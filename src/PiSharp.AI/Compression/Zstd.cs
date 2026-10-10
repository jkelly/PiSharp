// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/openai-codex-responses.ts (compressRequestBodyZstd: Codex SSE
// request bodies are zstd frames, Node's zstdCompressSync at level 3). The format is RFC 8878; this encoder and decoder are written from
// the RFC (no libzstd code), so frames decode with any zstd decoder but are not byte-identical to libzstd's.
using System.Buffers.Binary;
using System.Numerics;

namespace PiSharp.AI.Compression;

/// <summary>
/// A dependency-free Zstandard (RFC 8878) frame encoder, about libzstd level 3 in ratio for JSON, and the decoder that checks it.
/// <see cref="Compress"/> writes one frame with the content size (a single segment up to the 2 MiB level-3 window, a 2 MiB window
/// descriptor above), no checksum and no dictionary; blocks of at most 128 KiB are raw, RLE or compressed: an LZ77 hash-chain match
/// finder with lazy matching inside the window, Huffman literals (one or four streams, direct or FSE weights) and FSE sequences with
/// the predefined or a fitted table per code type.
/// </summary>
public static class Zstd
{
    private const uint Magic = 0xFD2FB528;
    private const int WindowLog = 21;
    private const int MaxBlockSize = 128 * 1024;
    private const int MinMatch = 4;
    private const int HashLog = 17;
    private const int SearchDepth = 24;

    // RFC 8878 3.1.1.3.2.1.1 code tables: baselines and extra bits.
    private static readonly int[] LiteralBaseline = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536];
    private static readonly int[] LiteralBits = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    private static readonly int[] MatchBaseline = [3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
        35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051, 4099, 8195, 16387, 32771, 65539];
    private static readonly int[] MatchBits = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    // RFC 8878 3.1.1.3.2.2 default distributions.
    private static readonly short[] LiteralDefault = [4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1, -1, -1, -1, -1];
    private static readonly short[] MatchDefault = [1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1, -1, -1];
    private static readonly short[] OffsetDefault = [1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1];

    private readonly record struct Sequence(int LiteralLength, int OffsetValue, int MatchLength);

    /// <summary>One zstd frame holding <paramref name="source"/>.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> source)
    {
        var output = new ByteBuffer(source.Length / 3 + 64);
        var size = source.Length;
        var windowSize = 1 << WindowLog;
        var singleSegment = size <= windowSize;
        var fcsCode = singleSegment && size <= 255 ? 0 : size <= 65535 + 256 ? 1 : 2;
        output.WriteUInt32(Magic);
        output.Write((byte)((fcsCode << 6) | (singleSegment ? 0x20 : 0)));
        if (!singleSegment) output.Write((byte)((WindowLog - 10) << 3));
        switch (fcsCode)
        {
            case 0: output.Write((byte)size); break;
            case 1: output.WriteUInt16((ushort)(size - 256)); break;
            default: output.WriteUInt32((uint)size); break;
        }
        if (size == 0) { output.WriteBlockHeader(true, 0, 0); return output.ToArray(); }
        var finder = new MatchFinder(source.ToArray(), singleSegment ? size : windowSize);
        // The repeat offsets the decoder keeps across the frame's compressed blocks (RFC 8878 3.1.1.5), starting at 1, 4, 8.
        int[] repeat = [1, 4, 8];
        for (var start = 0; start < size; start += MaxBlockSize)
        {
            var length = Math.Min(MaxBlockSize, size - start);
            var last = start + length == size;
            var block = source.Slice(start, length);
            if (!block.ContainsAnyExcept(block[0]))
            {
                output.WriteBlockHeader(last, 1, length); output.Write(block[0]);
                finder.Skip(start, start + length);
                continue;
            }
            var working = (int[])repeat.Clone();
            var compressed = CompressBlock(finder, start, start + length, working);
            if (compressed is not null && compressed.Length < length)
            {
                output.WriteBlockHeader(last, 2, compressed.Length); output.Write(compressed);
                repeat = working;
            }
            else { output.WriteBlockHeader(last, 0, length); output.Write(block); }
        }
        return output.ToArray();
    }

    // ------------------------------------------------------------------ match finding

    /// <summary>A hash-chain LZ77 match finder over the whole input: 4-byte hashes and a chain ring as long as the window (or the
    /// input, when shorter).</summary>
    private sealed class MatchFinder
    {
        private readonly byte[] data;
        private readonly int maximumOffset, _chainMask;
        private readonly int[] _head = Fill(new int[1 << HashLog]);
        private readonly int[] _chain;
        private int _inserted;
        public byte[] Data => data;

        public MatchFinder(byte[] data, int window)
        {
            this.data = data;
            var chainSize = (int)Math.Min(BitOperations.RoundUpToPowerOf2((uint)Math.Max(data.Length, 16)), 1u << WindowLog);
            _chain = new int[chainSize]; _chainMask = chainSize - 1;
            maximumOffset = Math.Min(window, chainSize - 1);
        }

        /// <summary>The length of the match at <paramref name="position"/> with <paramref name="offset"/>, or 0 when the offset is out of
        /// reach.</summary>
        public int MatchLength(int position, int end, int offset) =>
            offset <= 0 || offset > position || offset > maximumOffset ? 0 : data.AsSpan(position - offset, end - position).CommonPrefixLength(data.AsSpan(position, end - position));

        private static int[] Fill(int[] table) { Array.Fill(table, -1); return table; }
        private uint Hash(int position) => (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position)) * 2654435761u) >> (32 - HashLog);

        /// <summary>Indexes every position before <paramref name="end"/> that has four bytes.</summary>
        private void InsertUpTo(int end)
        {
            var limit = Math.Min(end, data.Length - 3);
            for (; _inserted < limit; _inserted++)
            {
                var hash = Hash(_inserted);
                _chain[_inserted & _chainMask] = _head[hash];
                _head[hash] = _inserted;
            }
            if (_inserted < end) _inserted = end;
        }

        public void Skip(int start, int end) { InsertUpTo(start); InsertUpTo(end); }

        /// <summary>The longest match at <paramref name="position"/> ending before <paramref name="end"/>, offset within the window.</summary>
        public (int Length, int Offset) Find(int position, int end)
        {
            InsertUpTo(position);
            if (position + MinMatch > end || position + 4 > data.Length) return (0, 0);
            var best = 0; var bestOffset = 0;
            var candidate = _head[Hash(position)];
            var maxLength = end - position;
            for (var depth = 0; candidate >= 0 && depth < SearchDepth; depth++)
            {
                var offset = position - candidate;
                if (offset <= 0 || offset > maximumOffset) break;
                if (data[candidate + best] == data[position + best])
                {
                    var length = data.AsSpan(candidate, maxLength).CommonPrefixLength(data.AsSpan(position, maxLength));
                    if (length > best) { best = length; bestOffset = offset; if (length == maxLength) break; }
                }
                var previous = _chain[candidate & _chainMask];
                if (previous >= candidate) break; // The ring wrapped over an older position.
                candidate = previous;
            }
            return best >= MinMatch ? (best, bestOffset) : (0, 0);
        }
    }

    /// <summary>The best match at a position: the longest hash-chain match, unless a repeat offset matches nearly as long (it costs a
    /// few bits instead of the offset's).</summary>
    private static (int Length, int Offset, bool Repeat) BestMatch(MatchFinder finder, int position, int end, int[] repeat)
    {
        var (length, offset) = finder.Find(position, end);
        var repeatLength = 0; var repeatOffset = 0;
        foreach (var candidate in repeat)
        {
            var found = finder.MatchLength(position, end, candidate);
            if (found > repeatLength) { repeatLength = found; repeatOffset = candidate; }
        }
        if (repeatLength >= 3 && repeatLength + 1 >= length) return (repeatLength, repeatOffset, true);
        return length >= MinMatch ? (length, offset, false) : (0, 0, false);
    }

    /// <summary>RFC 8878 3.1.1.5: the Offset_Value for an offset (a repeat code when the history holds it, else offset + 3), with the
    /// decoder's history update applied to <paramref name="repeat"/>.</summary>
    private static uint OffsetValue(int offset, int literalLength, int[] repeat)
    {
        uint value;
        if (literalLength > 0)
            value = offset == repeat[0] ? 1u : offset == repeat[1] ? 2u : offset == repeat[2] ? 3u : (uint)offset + 3;
        else value = offset == repeat[1] ? 1u : offset == repeat[2] ? 2u : offset == repeat[0] - 1 ? 3u : (uint)offset + 3;
        var which = value > 3 ? -1 : (int)value - 1 + (literalLength == 0 ? 1 : 0);
        if (which == 0) return value;
        if (which != 1) repeat[2] = repeat[1];
        repeat[1] = repeat[0]; repeat[0] = offset;
        return value;
    }

    private static byte[]? CompressBlock(MatchFinder finder, int start, int end, int[] repeat)
    {
        var data = finder.Data;
        var sequences = new List<Sequence>();
        var literals = new ByteBuffer(end - start);
        var anchor = start; var position = start;
        while (position < end)
        {
            var (length, offset, isRepeat) = BestMatch(finder, position, end, repeat);
            if (length == 0) { position++; continue; }
            // Lazy matching: a longer match one byte later wins (by two bytes against a repeat offset).
            while (position + 1 < end)
            {
                var (next, nextOffset, nextRepeat) = BestMatch(finder, position + 1, end, repeat);
                if (next <= length + (isRepeat && !nextRepeat ? 1 : 0)) break;
                position++; length = next; offset = nextOffset; isRepeat = nextRepeat;
            }
            literals.Write(data.AsSpan(anchor, position - anchor));
            sequences.Add(new(position - anchor, (int)OffsetValue(offset, position - anchor, repeat), length));
            position += length; anchor = position;
        }
        finder.Skip(end, end);
        literals.Write(data.AsSpan(anchor, end - anchor));
        var block = new ByteBuffer(end - start);
        WriteLiterals(block, literals.Span);
        if (!WriteSequences(block, sequences)) return null;
        return block.Length > MaxBlockSize ? null : block.ToArray();
    }

    // ------------------------------------------------------------------ literals

    private static void WriteLiterals(ByteBuffer block, ReadOnlySpan<byte> literals)
    {
        var size = literals.Length;
        if (size > 0 && !literals.ContainsAnyExcept(literals[0])) { WriteRawHeader(block, 1, size); block.Write(literals[0]); return; }
        if (size >= 64 && HuffmanLiterals(literals) is { } huffman && huffman.Length < size) { block.Write(huffman); return; }
        WriteRawHeader(block, 0, size); block.Write(literals);
    }

    private static void WriteRawHeader(ByteBuffer block, int type, int size)
    {
        if (size < 32) block.Write((byte)(type | (size << 3)));
        else if (size < 4096) block.WriteUInt16((ushort)(type | (1 << 2) | (size << 4)));
        else { var value = type | (3 << 2) | (size << 4); block.Write((byte)value); block.Write((byte)(value >> 8)); block.Write((byte)(value >> 16)); }
    }

    /// <summary>A compressed literals section (header, tree description, streams), or null when Huffman does not apply.</summary>
    private static byte[]? HuffmanLiterals(ReadOnlySpan<byte> literals)
    {
        var counts = new int[256];
        foreach (var value in literals) counts[value]++;
        var lengths = HuffmanLengths(counts, 11);
        if (lengths is null) return null;
        var maxBits = lengths.Max();
        var maxSymbol = 255; while (lengths[maxSymbol] == 0) maxSymbol--;
        var weights = new byte[maxSymbol];
        for (var symbol = 0; symbol < maxSymbol; symbol++) weights[symbol] = (byte)(lengths[symbol] == 0 ? 0 : maxBits + 1 - lengths[symbol]);
        var tree = TreeDescription(weights);
        if (tree is null) return null;
        // Canonical codes (RFC 8878 4.2.1.3): by weight ascending, then symbol; codes count up from zero.
        var codes = new uint[256]; var next = 0u;
        for (var weight = 1; weight <= maxBits; weight++)
            for (var symbol = 0; symbol <= maxSymbol; symbol++)
            {
                if (lengths[symbol] == 0 || maxBits + 1 - lengths[symbol] != weight) continue;
                codes[symbol] = next >> (weight - 1); next += 1u << (weight - 1);
            }
        var size = literals.Length;
        var body = new ByteBuffer(size);
        body.Write(tree);
        bool four;
        if (size <= 1023)
        {
            four = false;
            body.Write(HuffmanStream(literals, codes, lengths));
        }
        else
        {
            four = true;
            var segment = (size + 3) / 4;
            var streams = new byte[4][];
            for (var index = 0; index < 4; index++)
            {
                var from = Math.Min(size, index * segment); var to = index == 3 ? size : Math.Min(size, from + segment);
                streams[index] = HuffmanStream(literals[from..to], codes, lengths);
            }
            if (streams[0].Length > 65535 || streams[1].Length > 65535 || streams[2].Length > 65535) return null;
            for (var index = 0; index < 3; index++) body.WriteUInt16((ushort)streams[index].Length);
            foreach (var stream in streams) body.Write(stream);
        }
        var compressed = body.Length;
        var section = new ByteBuffer(compressed + 5);
        var largest = Math.Max(size, compressed);
        if (!four && compressed <= 1023) { var value = 2 | (0 << 2) | (size << 4) | (compressed << 14); section.Write((byte)value); section.Write((byte)(value >> 8)); section.Write((byte)(value >> 16)); }
        else if (!four) return null;
        else if (largest <= 1023) { var value = 2 | (1 << 2) | (size << 4) | (compressed << 14); section.Write((byte)value); section.Write((byte)(value >> 8)); section.Write((byte)(value >> 16)); }
        else if (largest <= 16383) { var value = 2u | (2u << 2) | ((uint)size << 4) | ((uint)compressed << 18); section.WriteUInt32(value); }
        else if (largest <= 262143)
        {
            var value = 2ul | (3ul << 2) | ((ulong)size << 4) | ((ulong)compressed << 22);
            for (var index = 0; index < 5; index++) section.Write((byte)(value >> (8 * index)));
        }
        else return null;
        section.Write(body.Span);
        return section.ToArray();
    }

    /// <summary>One Huffman stream: the symbols written last to first, so the backward reader yields them in order.</summary>
    private static byte[] HuffmanStream(ReadOnlySpan<byte> symbols, uint[] codes, int[] lengths)
    {
        var writer = new BitWriter(symbols.Length);
        for (var index = symbols.Length - 1; index >= 0; index--) writer.Add(codes[symbols[index]], lengths[symbols[index]]);
        return writer.Close();
    }

    /// <summary>The Huffman tree description: FSE-compressed weights when smaller (and under 128 bytes), else 4-bit direct weights for
    /// at most 128 of them; null when neither fits.</summary>
    private static byte[]? TreeDescription(byte[] weights)
    {
        byte[]? direct = null;
        if (weights.Length <= 128)
        {
            direct = new byte[1 + (weights.Length + 1) / 2];
            direct[0] = (byte)(127 + weights.Length);
            for (var index = 0; index < weights.Length; index++)
                direct[1 + index / 2] |= (byte)(index % 2 == 0 ? weights[index] << 4 : weights[index]);
        }
        var fse = weights.Length >= 2 ? CompressWeights(weights) : null;
        if (fse is not null && fse.Length < 128 && (direct is null || fse.Length + 1 < direct.Length))
            return [(byte)fse.Length, .. fse];
        return direct;
    }

    /// <summary>FSE-compressed Huffman weights (accuracy log at most 6, two interleaved states), or null.</summary>
    private static byte[]? CompressWeights(byte[] weights)
    {
        var counts = new int[13];
        foreach (var weight in weights) counts[weight]++;
        var maxSymbol = 12; while (counts[maxSymbol] == 0) maxSymbol--;
        if (counts.Count(count => count > 0) < 2) return null;
        var tableLog = Math.Clamp(BitOperations.Log2((uint)weights.Length) + 1, 5, 6);
        var normalized = Normalize(counts.AsSpan(0, maxSymbol + 1), tableLog);
        if (normalized is null) return null;
        var output = new ByteBuffer(64);
        WriteNormalizedCounts(output, normalized, tableLog);
        var table = new FseEncoder(normalized, tableLog);
        var writer = new BitWriter(weights.Length);
        var position = weights.Length;
        FseState first, second;
        if ((weights.Length & 1) != 0)
        {
            first = table.Start(weights[--position]); second = table.Start(weights[--position]);
            table.Encode(writer, ref first, weights[--position]);
        }
        else { second = table.Start(weights[--position]); first = table.Start(weights[--position]); }
        while (position > 0)
        {
            table.Encode(writer, ref second, weights[--position]);
            table.Encode(writer, ref first, weights[--position]);
        }
        table.Flush(writer, second); table.Flush(writer, first);
        output.Write(writer.Close());
        return output.ToArray();
    }

    /// <summary>Length-limited Huffman code lengths (package-merge); null for fewer than two symbols.</summary>
    internal static int[]? HuffmanLengths(int[] counts, int limit)
    {
        var symbols = Enumerable.Range(0, counts.Length).Where(symbol => counts[symbol] > 0).OrderBy(symbol => counts[symbol]).ThenBy(symbol => symbol).ToArray();
        if (symbols.Length < 2) return null;
        var lengths = new int[counts.Length];
        // Package-merge: each level's list holds items (weight, symbols covered); the 2n-2 lightest of the final list set the lengths.
        var leaves = symbols.Select(symbol => (Weight: (long)counts[symbol], Symbols: new List<int> { symbol })).ToList();
        var current = leaves.ToList();
        for (var level = 1; level < limit; level++)
        {
            var packaged = new List<(long Weight, List<int> Symbols)>();
            for (var index = 0; index + 1 < current.Count; index += 2)
                packaged.Add((current[index].Weight + current[index + 1].Weight, [.. current[index].Symbols, .. current[index + 1].Symbols]));
            current = Merge(leaves, packaged);
        }
        foreach (var item in current.Take(2 * symbols.Length - 2))
            foreach (var symbol in item.Symbols) lengths[symbol]++;
        return lengths;

        static List<(long Weight, List<int> Symbols)> Merge(List<(long Weight, List<int> Symbols)> left, List<(long Weight, List<int> Symbols)> right)
        {
            var merged = new List<(long Weight, List<int> Symbols)>(left.Count + right.Count);
            int i = 0, j = 0;
            while (i < left.Count || j < right.Count)
                merged.Add(j >= right.Count || i < left.Count && left[i].Weight <= right[j].Weight ? left[i++] : right[j++]);
            return merged;
        }
    }

    // ------------------------------------------------------------------ sequences

    private static int LiteralCode(int length) => length < 16 ? length : FindCode(LiteralBaseline, length, 16);
    private static int MatchCode(int length) => length - 3 < 32 ? length - 3 : FindCode(MatchBaseline, length, 32);
    private static int FindCode(int[] baselines, int value, int from)
    {
        var code = from;
        while (code + 1 < baselines.Length && baselines[code + 1] <= value) code++;
        return code;
    }

    private static bool WriteSequences(ByteBuffer block, List<Sequence> sequences)
    {
        var count = sequences.Count;
        if (count < 128) block.Write((byte)count);
        else if (count < 0x7F00) { block.Write((byte)((count >> 8) + 128)); block.Write((byte)count); }
        else { block.Write(255); block.WriteUInt16((ushort)(count - 0x7F00)); }
        if (count == 0) return true;
        var literalCodes = new byte[count]; var matchCodes = new byte[count]; var offsetCodes = new byte[count]; var offsetValues = new uint[count];
        for (var index = 0; index < count; index++)
        {
            var sequence = sequences[index];
            literalCodes[index] = (byte)LiteralCode(sequence.LiteralLength);
            matchCodes[index] = (byte)MatchCode(sequence.MatchLength);
            offsetValues[index] = (uint)sequence.OffsetValue;
            offsetCodes[index] = (byte)BitOperations.Log2(offsetValues[index]);
            if (offsetCodes[index] > 28) return false;
        }
        var tables = new ByteBuffer(64);
        var literal = ChooseTable(literalCodes, LiteralDefault, 6, 9, 35, tables, out var literalMode);
        var offset = ChooseTable(offsetCodes, OffsetDefault, 5, 8, 31, tables, out var offsetMode);
        var match = ChooseTable(matchCodes, MatchDefault, 6, 9, 52, tables, out var matchMode);
        block.Write((byte)((literalMode << 6) | (offsetMode << 4) | (matchMode << 2)));
        block.Write(tables.Span);
        var writer = new BitWriter(count * 4);
        var last = count - 1;
        var matchState = match.Start(matchCodes[last]); var offsetState = offset.Start(offsetCodes[last]); var literalState = literal.Start(literalCodes[last]);
        AddExtras(last);
        for (var index = count - 2; index >= 0; index--)
        {
            offset.Encode(writer, ref offsetState, offsetCodes[index]);
            match.Encode(writer, ref matchState, matchCodes[index]);
            literal.Encode(writer, ref literalState, literalCodes[index]);
            AddExtras(index);
        }
        match.Flush(writer, matchState); offset.Flush(writer, offsetState); literal.Flush(writer, literalState);
        block.Write(writer.Close());
        return true;

        void AddExtras(int index)
        {
            var sequence = sequences[index];
            writer.Add((uint)(sequence.LiteralLength - LiteralBaseline[literalCodes[index]]), LiteralBits[literalCodes[index]]);
            writer.Add((uint)(sequence.MatchLength - MatchBaseline[matchCodes[index]]), MatchBits[matchCodes[index]]);
            writer.Add(offsetValues[index] - (1u << offsetCodes[index]), offsetCodes[index]);
        }
    }

    /// <summary>The predefined table (mode 0) or a fitted one (mode 2, its description appended to <paramref name="tables"/>), whichever
    /// costs fewer estimated bits.</summary>
    private static FseEncoder ChooseTable(byte[] codes, short[] defaults, int defaultLog, int maxLog, int maxCode, ByteBuffer tables, out int mode)
    {
        var counts = new int[maxCode + 1];
        foreach (var code in codes) counts[code]++;
        var predefined = new FseEncoder(defaults, defaultLog);
        double Cost(short[] normalized, int tableLog)
        {
            double bits = 0;
            for (var symbol = 0; symbol < counts.Length; symbol++)
                if (counts[symbol] > 0)
                {
                    var probability = symbol < normalized.Length ? normalized[symbol] == -1 ? 1 : normalized[symbol] : 0;
                    if (probability == 0) return double.PositiveInfinity;
                    bits += counts[symbol] * (tableLog - Math.Log2(probability));
                }
            return bits;
        }
        var defaultCost = Cost(defaults, defaultLog);
        var distinct = counts.Count(count => count > 0);
        if (distinct >= 2)
        {
            var tableLog = Math.Clamp(Math.Max(BitOperations.Log2((uint)codes.Length) + 1, BitOperations.Log2((uint)distinct) + 2), 5, maxLog);
            var trimmed = counts.AsSpan(0, Array.FindLastIndex(counts, count => count > 0) + 1);
            if (Normalize(trimmed, tableLog) is { } normalized)
            {
                var header = new ByteBuffer(16);
                WriteNormalizedCounts(header, normalized, tableLog);
                if (Cost(normalized, tableLog) + header.Length * 8 < defaultCost)
                {
                    tables.Write(header.Span); mode = 2;
                    return new FseEncoder(normalized, tableLog);
                }
            }
        }
        mode = 0;
        return predefined;
    }

    /// <summary>Counts scaled to sum to 2^tableLog, every present symbol at least 1; null when they cannot fit.</summary>
    internal static short[]? Normalize(ReadOnlySpan<int> counts, int tableLog)
    {
        var total = 0L; var present = 0;
        foreach (var count in counts) { total += count; if (count > 0) present++; }
        var size = 1 << tableLog;
        if (present == 0 || present > size) return null;
        var normalized = new short[counts.Length];
        var sum = 0;
        for (var symbol = 0; symbol < counts.Length; symbol++)
        {
            if (counts[symbol] == 0) continue;
            normalized[symbol] = (short)Math.Max(1, (int)Math.Round((double)counts[symbol] * size / total));
            sum += normalized[symbol];
        }
        while (sum != size)
        {
            // Adjust the symbol whose share is furthest from its count (the largest one first), never below 1.
            var best = -1; var bestError = double.NegativeInfinity;
            for (var symbol = 0; symbol < counts.Length; symbol++)
            {
                if (counts[symbol] == 0 || sum > size && normalized[symbol] <= 1) continue;
                var error = (sum > size ? 1 : -1) * (normalized[symbol] - (double)counts[symbol] * size / total);
                if (error > bestError) { bestError = error; best = symbol; }
            }
            if (best < 0) return null;
            var step = sum > size ? -1 : 1;
            normalized[best] += (short)step; sum += step;
        }
        return normalized;
    }

    /// <summary>RFC 8878 4.1.1 FSE table description: the accuracy log, then each count with the variable-width code and zero runs.</summary>
    internal static void WriteNormalizedCounts(ByteBuffer output, short[] normalized, int tableLog)
    {
        var writer = new ForwardBitWriter(output);
        var tableSize = 1 << tableLog;
        writer.Add((uint)(tableLog - 5), 4);
        var remaining = tableSize + 1; var threshold = tableSize; var nbBits = tableLog + 1;
        var symbol = 0; var previousIsZero = false;
        while (symbol < normalized.Length && remaining > 1)
        {
            if (previousIsZero)
            {
                var start = symbol;
                while (symbol < normalized.Length && normalized[symbol] == 0) symbol++;
                while (symbol >= start + 24) { start += 24; writer.Add(0xFFFF, 16); }
                while (symbol >= start + 3) { start += 3; writer.Add(3, 2); }
                writer.Add((uint)(symbol - start), 2);
            }
            var count = (int)normalized[symbol++];
            var max = 2 * threshold - 1 - remaining;
            remaining -= count < 0 ? -count : count;
            count++;
            if (count >= threshold) count += max;
            writer.Add((uint)count, count < max ? nbBits - 1 : nbBits);
            previousIsZero = count == 1;
            while (remaining < threshold) { nbBits--; threshold >>= 1; }
        }
        writer.Flush();
    }

    private struct FseState { public uint Value; }

    /// <summary>FSE encoding tables (the RFC's spread, then per-symbol state transforms).</summary>
    private sealed class FseEncoder
    {
        private readonly int _tableLog;
        private readonly ushort[] _states;
        private readonly int[] _deltaBits, _deltaFind;

        public FseEncoder(short[] normalized, int tableLog)
        {
            _tableLog = tableLog;
            var size = 1 << tableLog;
            var symbols = Spread(normalized, tableLog);
            var cumulative = new int[normalized.Length + 1];
            for (var symbol = 0; symbol < normalized.Length; symbol++) cumulative[symbol + 1] = cumulative[symbol] + (normalized[symbol] == -1 ? 1 : normalized[symbol]);
            _states = new ushort[size];
            var next = (int[])cumulative.Clone();
            for (var position = 0; position < size; position++) _states[next[symbols[position]]++] = (ushort)(size + position);
            _deltaBits = new int[normalized.Length]; _deltaFind = new int[normalized.Length];
            var total = 0;
            for (var symbol = 0; symbol < normalized.Length; symbol++)
            {
                int count = normalized[symbol];
                if (count == 0) { _deltaBits[symbol] = ((tableLog + 1) << 16) - size; continue; }
                if (count is -1 or 1) { _deltaBits[symbol] = (tableLog << 16) - size; _deltaFind[symbol] = total - 1; total++; continue; }
                var maxBitsOut = tableLog - BitOperations.Log2((uint)(count - 1));
                var minStatePlus = count << maxBitsOut;
                _deltaBits[symbol] = (maxBitsOut << 16) - minStatePlus;
                _deltaFind[symbol] = total - count; total += count;
            }
        }

        public FseState Start(int symbol)
        {
            var bits = (_deltaBits[symbol] + (1 << 15)) >> 16;
            var value = (uint)((bits << 16) - _deltaBits[symbol]);
            return new() { Value = _states[(value >> bits) + _deltaFind[symbol]] };
        }

        public void Encode(BitWriter writer, ref FseState state, int symbol)
        {
            var bits = (int)((state.Value + (uint)_deltaBits[symbol]) >> 16);
            writer.Add(state.Value, bits);
            state.Value = _states[(state.Value >> bits) + _deltaFind[symbol]];
        }

        public void Flush(BitWriter writer, FseState state) => writer.Add(state.Value, _tableLog);
    }

    /// <summary>RFC 8878 4.1.1 symbol spread: "less than 1" symbols at the end, the others stepped through the table.</summary>
    private static int[] Spread(short[] normalized, int tableLog)
    {
        var size = 1 << tableLog; var mask = size - 1; var step = (size >> 1) + (size >> 3) + 3;
        var symbols = new int[size]; var high = size - 1;
        for (var symbol = 0; symbol < normalized.Length; symbol++) if (normalized[symbol] == -1) symbols[high--] = symbol;
        var position = 0;
        for (var symbol = 0; symbol < normalized.Length; symbol++)
            for (var occurrence = 0; occurrence < normalized[symbol]; occurrence++)
            {
                symbols[position] = symbol;
                do position = (position + step) & mask; while (position > high);
            }
        return symbols;
    }

    // ------------------------------------------------------------------ bit and byte writers

    /// <summary>A bitstream for backward reading: bits packed from the least significant end, closed with a 1 marker.</summary>
    private sealed class BitWriter(int capacity)
    {
        private readonly ByteBuffer _bytes = new(capacity + 8);
        private ulong _container; private int _count;
        public void Add(uint value, int bits)
        {
            if (bits == 0) return;
            _container |= (ulong)(value & (uint)((1ul << bits) - 1)) << _count; _count += bits;
            while (_count >= 8) { _bytes.Write((byte)_container); _container >>= 8; _count -= 8; }
        }
        public byte[] Close() { Add(1, 1); if (_count > 0) _bytes.Write((byte)_container); return _bytes.ToArray(); }
    }

    /// <summary>A forward little-endian bitstream (FSE table descriptions), padded to a whole byte.</summary>
    private sealed class ForwardBitWriter(ByteBuffer output)
    {
        private ulong _container; private int _count;
        public void Add(uint value, int bits)
        {
            _container |= (ulong)(value & (uint)((1ul << bits) - 1)) << _count; _count += bits;
            while (_count >= 8) { output.Write((byte)_container); _container >>= 8; _count -= 8; }
        }
        public void Flush() { if (_count > 0) output.Write((byte)_container); _container = 0; _count = 0; }
    }

    internal sealed class ByteBuffer(int capacity)
    {
        private byte[] _buffer = new byte[Math.Max(16, capacity)];
        public int Length { get; private set; }
        public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, Length);
        private Span<byte> Reserve(int count)
        {
            if (Length + count > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + count));
            var span = _buffer.AsSpan(Length, count); Length += count; return span;
        }
        public void Write(byte value) => Reserve(1)[0] = value;
        public void Write(ReadOnlySpan<byte> values) => values.CopyTo(Reserve(values.Length));
        public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);
        public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);
        public void WriteBlockHeader(bool last, int type, int size)
        {
            var value = (last ? 1 : 0) | (type << 1) | (size << 3);
            Write((byte)value); Write((byte)(value >> 8)); Write((byte)(value >> 16));
        }
        public byte[] ToArray() => Span.ToArray();
    }
}
