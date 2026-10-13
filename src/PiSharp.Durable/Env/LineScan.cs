// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/env/line-scan.ts.
// Upstream's line numbers are safe integers with `endLine` defaulting to Infinity; here they are longs with a null end.
using System.Text;

namespace PiSharp.Durable.Env;

/// <summary>
/// Computes a <see cref="LineScan"/> from a file's bytes fed in order, so an environment can scan any size in bounded memory (upstream
/// <c>LineScanner</c>). Decoded sizes use the same streaming decoder as decoding the whole file at once.
/// </summary>
public sealed class LineScanner
{
    private const byte Newline = 0x0a;
    private readonly long startLine;
    private readonly long? endLine;
    private long position;
    private long newlines;
    private long lineStart;
    private long? start;
    private long? end;
    private long? firstLineEnd;
    private long? lastLineStart;
    private long selectedBytes;
    private long firstLineBytes;
    private Decoder? selection;
    private Decoder? firstLine;
    /// <summary>The first bytes, held until it is known whether they are a byte-order mark.</summary>
    private List<byte>? head = [];
    private bool bom;

    /// <summary>Scans lines <c>[startLine, endLine)</c>; <paramref name="endLine"/> null: to the end.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is negative, empty or beyond safe integers (upstream <c>RangeError</c>,
    /// "Invalid line range").</exception>
    public LineScanner(long startLine, long? endLine = null)
    {
        if (startLine < 0 || startLine > Ids.MaxSafeInteger || endLine is { } last && (last <= startLine || last > Ids.MaxSafeInteger))
            throw new ArgumentOutOfRangeException(nameof(startLine), "Invalid line range");
        this.startLine = startLine;
        this.endLine = endLine;
        if (startLine == 0) Begin(0);
    }

    /// <summary>Feeds the next bytes of the file.</summary>
    public void Push(ReadOnlySpan<byte> chunk)
    {
        if (head is not null)
        {
            var take = Math.Min(3 - head.Count, chunk.Length);
            foreach (var value in chunk[..take]) head.Add(value);
            if (head.Count < 3) return;
            ReleaseHead();
            chunk = chunk[take..];
        }
        Process(chunk);
    }

    private void ReleaseHead()
    {
        var bytes = head!.ToArray();
        head = null;
        bom = Utf8Decoding.StartsWithBom(bytes);
        Process(bytes);
    }

    private bool IsLastLine(long line) => endLine is { } last && line == last - 1;

    private void Process(ReadOnlySpan<byte> chunk)
    {
        var @base = position;
        var from = 0;
        for (var index = chunk.IndexOf(Newline); index != -1;)
        {
            // The newline ends line `newlines`. It belongs to the selection between selected lines only.
            Feed(chunk, @base, from, index);
            var line = newlines;
            var at = @base + index;
            if (line == startLine) EndFirstLine(at);
            if (IsLastLine(line)) EndSelection(at);
            Feed(chunk, @base, index, index + 1);
            from = index + 1;
            newlines++;
            lineStart = at + 1;
            if (newlines == startLine) Begin(lineStart);
            if (IsLastLine(newlines)) lastLineStart = lineStart;
            var next = chunk[(index + 1)..].IndexOf(Newline);
            index = next == -1 ? -1 : index + 1 + next;
        }
        Feed(chunk, @base, from, chunk.Length);
        position += chunk.Length;
    }

    /// <summary>The scan of everything fed so far, as the whole file.</summary>
    public LineScan Finish()
    {
        if (head is not null) ReleaseHead();
        var size = position;
        if (start is null) return new LineScan(newlines, size, size, size, size, 0, 0);
        if (firstLineEnd is null) EndFirstLine(size);
        if (end is null) EndSelection(size);
        // A selection that reaches past the last line ends with the last line.
        return new LineScan(newlines, start.Value, end ?? size, firstLineEnd ?? size, lastLineStart ?? lineStart, selectedBytes, firstLineBytes);
    }

    private void Begin(long at)
    {
        start = at;
        if (IsLastLine(startLine)) lastLineStart = at;
        selection = Utf8Decoding.RangeDecoder();
        firstLine = Utf8Decoding.RangeDecoder();
    }

    private void EndFirstLine(long at)
    {
        firstLineEnd = at;
        if (firstLine is not null) firstLineBytes += Utf8Decoding.ByteCount(Utf8Decoding.Decode(firstLine, [], flush: true));
        firstLine = null;
    }

    private void EndSelection(long at)
    {
        end = at;
        if (selection is not null) selectedBytes += Utf8Decoding.ByteCount(Utf8Decoding.Decode(selection, [], flush: true));
        selection = null;
    }

    /// <summary>Feeds <c>chunk[from, to)</c>, which starts at file offset <paramref name="base"/>, to the decoders of the ranges still open.</summary>
    private void Feed(ReadOnlySpan<byte> chunk, long @base, int from, int to)
    {
        // Decoding the whole file drops a leading byte-order mark.
        if (bom && @base + from < 3) from = (int)Math.Min(to, 3 - @base);
        if (to <= from) return;
        var bytes = chunk[from..to];
        if (selection is not null) selectedBytes += Utf8Decoding.ByteCount(Utf8Decoding.Decode(selection, bytes, flush: false));
        if (firstLine is not null) firstLineBytes += Utf8Decoding.ByteCount(Utf8Decoding.Decode(firstLine, bytes, flush: false));
    }
}
