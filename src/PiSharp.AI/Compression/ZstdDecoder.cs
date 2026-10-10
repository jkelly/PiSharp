// RFC 8878 frame decoding: the checker for PiSharp's zstd encoder (Zstd.Compress) and the reader for tests of compressed request bodies.
using System.Buffers.Binary;
using System.Numerics;

namespace PiSharp.AI.Compression;

/// <summary>Decodes zstd frames (RFC 8878): raw, RLE and compressed blocks; raw, RLE, Huffman and treeless literals; predefined, RLE,
/// FSE-compressed and repeated sequence tables; repeat offsets; concatenated and skippable frames. Dictionaries are not supported.
/// Malformed input throws <see cref="InvalidDataException"/>.</summary>
public static class ZstdDecoder
{
    private const uint Magic = 0xFD2FB528;
    private static readonly int[] LiteralBaseline = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536];
    private static readonly int[] LiteralBits = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    private static readonly int[] MatchBaseline = [3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
        35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051, 4099, 8195, 16387, 32771, 65539];
    private static readonly int[] MatchBits = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    private static readonly short[] LiteralDefault = [4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1, -1, -1, -1, -1];
    private static readonly short[] MatchDefault = [1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1, -1, -1];
    private static readonly short[] OffsetDefault = [1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1];

    /// <summary>The concatenated content of every frame in <paramref name="input"/>, at most <paramref name="maximumSize"/> bytes.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> input, int maximumSize = 256 * 1024 * 1024)
    {
        var output = new Sink();
        var position = 0;
        while (position < input.Length)
        {
            var magic = Read32(input, ref position);
            if ((magic & 0xFFFFFFF0) == 0x184D2A50) { var skip = (int)Read32(input, ref position); Need(input, position, skip); position += skip; continue; }
            if (magic != Magic) throw Bad("magic");
            new Frame(output, maximumSize).Decode(input, ref position);
        }
        return output.ToArray();
    }

    private static InvalidDataException Bad(string what) => new("Invalid zstd data: " + what + ".");
    private static void Need(ReadOnlySpan<byte> input, int position, int count) { if (count < 0 || position + count > input.Length) throw Bad("truncated"); }
    private static uint Read32(ReadOnlySpan<byte> input, ref int position)
    {
        Need(input, position, 4); var value = BinaryPrimitives.ReadUInt32LittleEndian(input[position..]); position += 4; return value;
    }

    private sealed class FseTable
    {
        public int TableLog;
        public byte[] Symbol = [];
        public byte[] Bits = [];
        public int[] Baseline = [];
    }

    private sealed class Frame(Sink output, int maximumSize)
    {
        private readonly int _start = output.Count;
        private long _windowSize;
        private readonly int[] _repeat = [1, 4, 8];
        private FseTable? _literal, _offset, _match;
        private int[]? _huffmanSymbols; private byte[]? _huffmanBits; private int _huffmanLog;

        public void Decode(ReadOnlySpan<byte> input, ref int position)
        {
            Need(input, position, 1);
            var descriptor = input[position++];
            var fcsFlag = descriptor >> 6; var single = (descriptor & 0x20) != 0; var checksum = (descriptor & 4) != 0; var dictionary = descriptor & 3;
            if ((descriptor & 8) != 0) throw Bad("reserved bit");
            if (!single)
            {
                Need(input, position, 1);
                var window = input[position++];
                var exponent = window >> 3; var mantissa = window & 7;
                var baseSize = 1L << (10 + exponent);
                _windowSize = baseSize + baseSize / 8 * mantissa;
            }
            if (dictionary != 0) { var bytes = dictionary == 3 ? 4 : dictionary; Need(input, position, bytes); position += bytes; throw Bad("dictionary"); }
            var fcsBytes = fcsFlag switch { 0 => single ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
            Need(input, position, fcsBytes);
            long contentSize = -1;
            if (fcsBytes == 1) contentSize = input[position];
            else if (fcsBytes == 2) contentSize = BinaryPrimitives.ReadUInt16LittleEndian(input[position..]) + 256;
            else if (fcsBytes == 4) contentSize = BinaryPrimitives.ReadUInt32LittleEndian(input[position..]);
            else if (fcsBytes == 8) contentSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(input[position..]);
            position += fcsBytes;
            if (single) _windowSize = contentSize;
            var maxBlock = (int)Math.Min(128 * 1024, Math.Max(_windowSize, 0));
            while (true)
            {
                Need(input, position, 3);
                var header = input[position] | input[position + 1] << 8 | input[position + 2] << 16; position += 3;
                var last = (header & 1) != 0; var type = (header >> 1) & 3; var size = header >> 3;
                if (type == 3) throw Bad("reserved block type");
                var before = output.Count;
                switch (type)
                {
                    case 0: Need(input, position, size); Emit(input.Slice(position, size)); position += size; break;
                    case 1: Need(input, position, 1); for (var index = 0; index < size; index++) Emit(input[position]); position += 1; break;
                    default:
                        Need(input, position, size);
                        if (size > maxBlock) throw Bad("block size");
                        Compressed(input.Slice(position, size)); position += size; break;
                }
                if (output.Count - before > maxBlock && type != 0) throw Bad("block content size");
                if (last) break;
            }
            if (checksum) { Need(input, position, 4); position += 4; }
            if (contentSize >= 0 && output.Count - _start != contentSize) throw Bad("content size");
        }

        private void Emit(byte value) { if (output.Count >= maximumSize) throw Bad("output limit"); output.Add(value); }
        private void Emit(ReadOnlySpan<byte> values) { if (output.Count + values.Length > maximumSize) throw Bad("output limit"); output.Add(values); }

        private void Compressed(ReadOnlySpan<byte> block)
        {
            var position = 0;
            var literals = Literals(block, ref position);
            Sequences(block, ref position, literals);
        }

        // ------------------------------------------------------------------ literals

        private byte[] Literals(ReadOnlySpan<byte> block, ref int position)
        {
            Need(block, position, 1);
            var first = block[position];
            var type = first & 3; var format = (first >> 2) & 3;
            if (type is 0 or 1)
            {
                int size;
                if ((format & 1) == 0) { size = first >> 3; position += 1; }
                else if (format == 1) { Need(block, position, 2); size = (first >> 4) | block[position + 1] << 4; position += 2; }
                else { Need(block, position, 3); size = (first >> 4) | block[position + 1] << 4 | block[position + 2] << 12; position += 3; }
                if (type == 0) { Need(block, position, size); var raw = block.Slice(position, size).ToArray(); position += size; return raw; }
                Need(block, position, 1); var rle = new byte[size]; Array.Fill(rle, block[position]); position += 1; return rle;
            }
            int regenerated, compressed, headerSize; var streams = format == 0 ? 1 : 4;
            switch (format)
            {
                case 0 or 1:
                {
                    Need(block, position, 3); var value = first | block[position + 1] << 8 | block[position + 2] << 16;
                    regenerated = (value >> 4) & 0x3FF; compressed = (value >> 14) & 0x3FF; headerSize = 3; break;
                }
                case 2:
                {
                    Need(block, position, 4); var value = BinaryPrimitives.ReadUInt32LittleEndian(block[position..]);
                    regenerated = (int)((value >> 4) & 0x3FFF); compressed = (int)((value >> 18) & 0x3FFF); headerSize = 4; break;
                }
                default:
                {
                    Need(block, position, 5); var value = 0UL;
                    for (var index = 0; index < 5; index++) value |= (ulong)block[position + index] << (8 * index);
                    regenerated = (int)((value >> 4) & 0x3FFFF); compressed = (int)((value >> 22) & 0x3FFFF); headerSize = 5; break;
                }
            }
            position += headerSize;
            Need(block, position, compressed);
            var section = block.Slice(position, compressed); position += compressed;
            var offset = 0;
            if (type == 2) ReadHuffmanTable(section, ref offset);
            else if (_huffmanSymbols is null) throw Bad("treeless literals without a table");
            var result = new byte[regenerated];
            if (streams == 1) DecodeHuffman(section[offset..], result);
            else
            {
                Need(section, offset, 6);
                var sizes = new int[4];
                sizes[0] = BinaryPrimitives.ReadUInt16LittleEndian(section[offset..]); sizes[1] = BinaryPrimitives.ReadUInt16LittleEndian(section[(offset + 2)..]);
                sizes[2] = BinaryPrimitives.ReadUInt16LittleEndian(section[(offset + 4)..]);
                offset += 6;
                sizes[3] = section.Length - offset - sizes[0] - sizes[1] - sizes[2];
                if (sizes[3] < 0) throw Bad("jump table");
                var segment = (regenerated + 3) / 4;
                for (var index = 0; index < 4; index++)
                {
                    var from = Math.Min(regenerated, index * segment); var to = index == 3 ? regenerated : Math.Min(regenerated, from + segment);
                    DecodeHuffman(section.Slice(offset, sizes[index]), result.AsSpan(from, to - from)); offset += sizes[index];
                }
            }
            return result;
        }

        private void ReadHuffmanTable(ReadOnlySpan<byte> section, ref int offset)
        {
            Need(section, offset, 1);
            var header = section[offset++];
            var weights = new List<byte>();
            if (header >= 128)
            {
                var count = header - 127; Need(section, offset, (count + 1) / 2);
                for (var index = 0; index < count; index++) weights.Add((byte)(index % 2 == 0 ? section[offset + index / 2] >> 4 : section[offset + index / 2] & 15));
                offset += (count + 1) / 2;
            }
            else
            {
                Need(section, offset, header);
                var data = section.Slice(offset, header); offset += header;
                var position = 0;
                var table = ReadFseTable(data, ref position, 6, 12);
                var reader = new BackwardReader(data[position..]);
                var state1 = reader.Read(table.TableLog); var state2 = reader.Read(table.TableLog);
                while (true)
                {
                    weights.Add(table.Symbol[state1]);
                    state1 = table.Baseline[state1] + reader.Read(table.Bits[state1]);
                    if (reader.Overflowed) { weights.Add(table.Symbol[state2]); break; }
                    weights.Add(table.Symbol[state2]);
                    state2 = table.Baseline[state2] + reader.Read(table.Bits[state2]);
                    if (reader.Overflowed) { weights.Add(table.Symbol[state1]); break; }
                    if (weights.Count > 255) throw Bad("weights");
                }
            }
            var total = 0;
            foreach (var weight in weights) { if (weight > 11) throw Bad("weight"); if (weight > 0) total += 1 << (weight - 1); }
            if (total == 0) throw Bad("weights");
            var maxBits = BitOperations.Log2((uint)total) + 1;
            var rest = (1 << maxBits) - total;
            if (!BitOperations.IsPow2(rest) || maxBits > 11) throw Bad("weights");
            weights.Add((byte)(BitOperations.Log2((uint)rest) + 1));
            _huffmanLog = maxBits;
            _huffmanSymbols = new int[1 << maxBits]; _huffmanBits = new byte[1 << maxBits];
            var next = 0;
            for (var weight = 1; weight <= maxBits; weight++)
                for (var symbol = 0; symbol < weights.Count; symbol++)
                {
                    if (weights[symbol] != weight) continue;
                    var span = 1 << (weight - 1);
                    for (var index = 0; index < span; index++) { _huffmanSymbols[next + index] = symbol; _huffmanBits[next + index] = (byte)(maxBits + 1 - weight); }
                    next += span;
                }
        }

        private void DecodeHuffman(ReadOnlySpan<byte> stream, Span<byte> output)
        {
            var reader = new BackwardReader(stream);
            for (var index = 0; index < output.Length; index++)
            {
                var peek = reader.Peek(_huffmanLog);
                output[index] = (byte)_huffmanSymbols![peek];
                reader.Consume(_huffmanBits![peek]);
            }
            if (!reader.Finished) throw Bad("huffman stream");
        }

        // ------------------------------------------------------------------ sequences

        private void Sequences(ReadOnlySpan<byte> block, ref int position, byte[] literals)
        {
            Need(block, position, 1);
            int count = block[position++];
            if (count >= 128 && count < 255) { Need(block, position, 1); count = ((count - 128) << 8) + block[position++]; }
            else if (count == 255) { Need(block, position, 2); count = BinaryPrimitives.ReadUInt16LittleEndian(block[position..]) + 0x7F00; position += 2; }
            if (count == 0) { if (position != block.Length) throw Bad("trailing bytes"); Emit(literals); return; }
            Need(block, position, 1);
            var modes = block[position++];
            if ((modes & 3) != 0) throw Bad("reserved modes");
            _literal = Table(block, ref position, modes >> 6, LiteralDefault, 6, 9, 35, _literal);
            _offset = Table(block, ref position, (modes >> 4) & 3, OffsetDefault, 5, 8, 31, _offset);
            _match = Table(block, ref position, (modes >> 2) & 3, MatchDefault, 6, 9, 52, _match);
            var reader = new BackwardReader(block[position..]);
            var literalState = reader.Read(_literal.TableLog); var offsetState = reader.Read(_offset.TableLog); var matchState = reader.Read(_match.TableLog);
            var literalPosition = 0;
            for (var index = 0; index < count; index++)
            {
                int offsetCode = _offset.Symbol[offsetState], matchCode = _match.Symbol[matchState], literalCode = _literal.Symbol[literalState];
                if (offsetCode > 31 || matchCode > 52 || literalCode > 35) throw Bad("code");
                var offsetValue = (1L << offsetCode) + reader.Read(offsetCode);
                var matchLength = MatchBaseline[matchCode] + reader.Read(MatchBits[matchCode]);
                var literalLength = LiteralBaseline[literalCode] + reader.Read(LiteralBits[literalCode]);
                long offset;
                if (offsetValue > 3) { offset = offsetValue - 3; _repeat[2] = _repeat[1]; _repeat[1] = _repeat[0]; _repeat[0] = (int)offset; }
                else
                {
                    var which = (int)offsetValue - 1 + (literalLength == 0 ? 1 : 0);
                    if (which == 0) offset = _repeat[0];
                    else
                    {
                        offset = which == 3 ? _repeat[0] - 1 : _repeat[which];
                        if (which != 1) _repeat[2] = _repeat[1];
                        _repeat[1] = _repeat[0]; _repeat[0] = (int)offset;
                    }
                }
                if (literalPosition + literalLength > literals.Length) throw Bad("literal length");
                Emit(literals.AsSpan(literalPosition, literalLength)); literalPosition += literalLength;
                var produced = output.Count - _start;
                if (offset <= 0 || offset > produced || offset > Math.Max(_windowSize, 0) && _windowSize > 0) throw Bad("offset");
                if (output.Count + matchLength > maximumSize) throw Bad("output limit");
                var from = output.Count - (int)offset;
                output.Copy(from, matchLength);
                if (index + 1 < count)
                {
                    literalState = _literal.Baseline[literalState] + reader.Read(_literal.Bits[literalState]);
                    matchState = _match.Baseline[matchState] + reader.Read(_match.Bits[matchState]);
                    offsetState = _offset.Baseline[offsetState] + reader.Read(_offset.Bits[offsetState]);
                }
            }
            if (!reader.Finished) throw Bad("sequence stream");
            Emit(literals.AsSpan(literalPosition));
        }

        private static FseTable Table(ReadOnlySpan<byte> block, ref int position, int mode, short[] defaults, int defaultLog, int maxLog, int maxSymbol,
            FseTable? previous)
        {
            switch (mode)
            {
                case 0: return Build(defaults, defaultLog);
                case 1:
                {
                    Need(block, position, 1);
                    var symbol = block[position++];
                    if (symbol > maxSymbol) throw Bad("rle symbol");
                    return new FseTable { TableLog = 0, Symbol = [symbol], Bits = [0], Baseline = [0] };
                }
                case 2: return ReadFseTable(block, ref position, maxLog, maxSymbol);
                default: return previous ?? throw Bad("repeat without a table");
            }
        }
    }

    /// <summary>RFC 8878 4.1.1: an FSE table description, then the decoding table.</summary>
    private static FseTable ReadFseTable(ReadOnlySpan<byte> data, ref int position, int maxLog, int maxSymbol)
    {
        var reader = new ForwardReader(data, position);
        var tableLog = (int)reader.Read(4) + 5;
        if (tableLog > maxLog) throw Bad("accuracy log");
        var remaining = (1 << tableLog) + 1; var threshold = 1 << tableLog; var nbBits = tableLog + 1;
        var counts = new List<short>();
        var previousIsZero = false;
        while (remaining > 1)
        {
            if (previousIsZero)
            {
                while (true)
                {
                    var repeat = (int)reader.Read(2);
                    for (var index = 0; index < repeat; index++) counts.Add(0);
                    if (repeat != 3) break;
                }
            }
            var max = 2 * threshold - 1 - remaining;
            int count;
            var low = (int)reader.Peek(nbBits - 1);
            if (low < max) { count = low; reader.Skip(nbBits - 1); }
            else { count = (int)reader.Read(nbBits); if (count >= threshold) count -= max; }
            count--;
            remaining -= count < 0 ? -count : count;
            counts.Add((short)count);
            previousIsZero = count == 0;
            while (remaining < threshold) { nbBits--; threshold >>= 1; }
            if (counts.Count > maxSymbol + 1) throw Bad("too many symbols");
        }
        if (remaining != 1) throw Bad("distribution");
        position = reader.BytePosition;
        return Build([.. counts], tableLog);
    }

    private static FseTable Build(short[] normalized, int tableLog)
    {
        var size = 1 << tableLog; var mask = size - 1; var step = (size >> 1) + (size >> 3) + 3;
        var table = new FseTable { TableLog = tableLog, Symbol = new byte[size], Bits = new byte[size], Baseline = new int[size] };
        var high = size - 1;
        var next = new int[normalized.Length];
        for (var symbol = 0; symbol < normalized.Length; symbol++)
        {
            if (normalized[symbol] == -1) { table.Symbol[high--] = (byte)symbol; next[symbol] = 1; }
            else next[symbol] = normalized[symbol];
        }
        var position = 0;
        for (var symbol = 0; symbol < normalized.Length; symbol++)
            for (var occurrence = 0; occurrence < normalized[symbol]; occurrence++)
            {
                table.Symbol[position] = (byte)symbol;
                do position = (position + step) & mask; while (position > high);
            }
        if (position != 0) throw Bad("table spread");
        for (var state = 0; state < size; state++)
        {
            var symbol = table.Symbol[state];
            var nextState = next[symbol]++;
            var bits = tableLog - BitOperations.Log2((uint)nextState);
            table.Bits[state] = (byte)bits;
            table.Baseline[state] = (nextState << bits) - size;
        }
        return table;
    }

    /// <summary>Reads a backward bitstream: from the end, after the highest set bit of the last byte.</summary>
    private ref struct BackwardReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private long _bitsLeft;
        public bool Overflowed;

        public BackwardReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            if (data.Length == 0 || data[^1] == 0) throw Bad("bitstream end");
            _bitsLeft = data.Length * 8L - (8 - BitOperations.Log2(data[^1]));
        }

        public readonly bool Finished => _bitsLeft == 0;

        /// <summary>The next <paramref name="count"/> (at most 31) bits as an integer, zeros past the stream start.</summary>
        public readonly int Peek(int count)
        {
            if (count == 0) return 0;
            var start = _bitsLeft - count;
            if (start >= 0) return (int)(Load(start) & ((1u << count) - 1));
            var available = count + (int)start;
            return available <= 0 ? 0 : (int)(Load(0) & ((1u << available) - 1)) << (int)-start;
        }

        private readonly ulong Load(long bit)
        {
            var index = (int)(bit >> 3); ulong value = 0;
            for (var offset = 0; offset < 8 && index + offset < _data.Length; offset++) value |= (ulong)_data[index + offset] << (8 * offset);
            return value >> (int)(bit & 7);
        }

        public void Consume(int count)
        {
            _bitsLeft -= count;
            if (_bitsLeft < 0) { Overflowed = true; if (_bitsLeft < -64) throw Bad("bitstream overrun"); }
        }

        public int Read(int count)
        {
            if (count == 0) return 0;
            var value = Peek(count); Consume(count); return value;
        }
    }

    private sealed class Sink
    {
        private byte[] _buffer = new byte[4096];
        public int Count { get; private set; }
        private void Ensure(int extra) { if (Count + extra > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Count + extra)); }
        public void Add(byte value) { Ensure(1); _buffer[Count++] = value; }
        public void Add(ReadOnlySpan<byte> values) { Ensure(values.Length); values.CopyTo(_buffer.AsSpan(Count)); Count += values.Length; }
        /// <summary>An LZ77 copy from <paramref name="from"/>; overlapping copies repeat the pattern byte by byte.</summary>
        public void Copy(int from, int length)
        {
            Ensure(length);
            for (var index = 0; index < length; index++) _buffer[Count + index] = _buffer[from + index];
            Count += length;
        }
        public byte[] ToArray() => _buffer.AsSpan(0, Count).ToArray();
    }

    private ref struct ForwardReader(ReadOnlySpan<byte> data, int bytePosition)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private long _bit = bytePosition * 8L;
        public readonly int BytePosition => (int)((_bit + 7) >> 3);

        public readonly uint Peek(int count)
        {
            uint value = 0;
            for (var index = 0; index < count; index++)
            {
                var bit = _bit + index;
                if (bit >> 3 >= _data.Length) throw Bad("table description");
                value |= (uint)((_data[(int)(bit >> 3)] >> (int)(bit & 7)) & 1) << index;
            }
            return value;
        }
        public void Skip(int count) => _bit += count;
        public uint Read(int count) { var value = Peek(count); _bit += count; return value; }
    }
}
