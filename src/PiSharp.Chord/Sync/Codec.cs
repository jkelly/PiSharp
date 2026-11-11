// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/sync/{batch,lines}.ts.
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Store;
using PiSharp.Contracts;

namespace PiSharp.Chord.Sync;

public sealed class LogFormatError : Exception
{
    public LogFormatError(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>A decoded commit and its newline-terminated UTF-16 line length (JavaScript's string length).</summary>
public sealed record BatchLine(OutboxCommit Commit, int Bytes);

/// <summary>The versioned gzip/JSONL format shared by session sync peers.</summary>
public static class SyncCodec
{
    public const int LogVersion = 2;
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    public static string EncodeLogLine(OutboxCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return ChordJson.Stringify(new JsonObject
        {
            ["v"] = LogVersion, ["seq"] = commit.Seq, ["nextId"] = commit.NextId, ["writes"] = commit.Writes.DeepClone(),
        }) + "\n";
    }

    public static OutboxCommit DecodeLogLine(string text, long number)
    {
        JsonNode? value;
        try
        {
            using var document = JsonDocument.Parse(text, JsonData.DocumentOptions);
            value = JsonUtf16.MutableNode(document.RootElement);
        }
        catch (JsonException error) { throw new LogFormatError($"Log line {number} is not JSON", error); }
        if (value is not JsonObject line) throw new LogFormatError($"Log line {number} is not an object");
        if (ChordJson.AsNumber(line["v"]) != LogVersion)
            throw new LogFormatError($"Log line {number} has unknown version {(line.ContainsKey("v") ? ChordJson.ToJavaScriptString(line["v"]) : "undefined")}");
        if (!SafeInteger(line["seq"], out var seq) || seq < 1)
            throw new LogFormatError($"Log line {number} has no valid sequence");
        if (!SafeInteger(line["nextId"], out var nextId))
            throw new LogFormatError($"Log line {number} has no valid next id");
        if (line["writes"] is not JsonArray writes) throw new LogFormatError($"Log line {number} has no writes");
        // Applying the batch, not reading the envelope, validates individual writes.
        return new(seq, nextId, writes);
    }

    public static IAsyncEnumerable<ReadOnlyMemory<byte>> EncodeBatch(IAsyncEnumerable<OutboxCommit> commits,
        CancellationToken cancellationToken = default) => BodyStreams.Gzip(EncodeLines(commits, cancellationToken), cancellationToken);

    public static IAsyncEnumerable<ReadOnlyMemory<byte>> EncodeBatch(IEnumerable<OutboxCommit> commits,
        CancellationToken cancellationToken = default) => EncodeBatch(AsAsync(commits, cancellationToken), cancellationToken);

    public static async IAsyncEnumerable<OutboxCommit> DecodeBatch(IAsyncEnumerable<ReadOnlyMemory<byte>> body,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var line in DecodeBatchLines(body, cancellationToken).ConfigureAwait(false)) yield return line.Commit;
    }

    public static async IAsyncEnumerable<BatchLine> DecodeBatchLines(IAsyncEnumerable<ReadOnlyMemory<byte>> body,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long previous = 0;
        await foreach (var (text, number) in Lines(BodyStreams.Gunzip(body, cancellationToken), cancellationToken).ConfigureAwait(false))
        {
            var commit = DecodeLogLine(text, number);
            if (commit.Seq <= previous) throw new LogFormatError($"Log line {number} does not increase the sequence");
            previous = commit.Seq;
            yield return new(commit, checked(text.Length + 1));
        }
    }

    public static IAsyncEnumerable<ReadOnlyMemory<byte>> EncodeSnapshot(IAsyncEnumerable<JsonObject> parts,
        CancellationToken cancellationToken = default) => BodyStreams.Gzip(Tables.EncodeJsonl(parts, cancellationToken), cancellationToken);

    public static IAsyncEnumerable<JsonNode?> DecodeSnapshot(IAsyncEnumerable<ReadOnlyMemory<byte>> body,
        CancellationToken cancellationToken = default) => Tables.DecodeJsonl(BodyStreams.Gunzip(body, cancellationToken), cancellationToken);

    private static bool SafeInteger(JsonNode? value, out long integer)
    {
        var number = ChordJson.AsNumber(value);
        integer = 0;
        if (number is not { } n || !double.IsFinite(n) || Math.Truncate(n) != n || Math.Abs(n) > MaxSafeInteger) return false;
        integer = (long)n;
        return true;
    }

    private static async IAsyncEnumerable<OutboxCommit> AsAsync(IEnumerable<OutboxCommit> commits,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commits);
        foreach (var commit in commits) { cancellationToken.ThrowIfCancellationRequested(); yield return commit; }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> EncodeLines(IAsyncEnumerable<OutboxCommit> commits,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commits);
        var pending = new StringBuilder();
        await foreach (var commit in commits.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            pending.Append(EncodeLogLine(commit));
            if (pending.Length < 64 * 1024) continue;
            yield return Encoding.UTF8.GetBytes(pending.ToString());
            pending.Clear();
        }
        if (pending.Length > 0) yield return Encoding.UTF8.GetBytes(pending.ToString());
    }

    private static async IAsyncEnumerable<(string Text, long Number)> Lines(IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        var rest = "";
        long number = 0;
        var started = false;
        string Decode(ReadOnlySpan<byte> chunk, bool flush)
        {
            string text;
            try
            {
                var chars = new char[decoder.GetCharCount(chunk, flush)];
                text = new string(chars, 0, decoder.GetChars(chunk, chars, flush));
            }
            catch (DecoderFallbackException error) { throw new LogFormatError($"Log line {number + 1} is not UTF-8", error); }
            if (!started && text.Length > 0)
            {
                started = true;
                if (text[0] == '\uFEFF') text = text[1..];
            }
            return text;
        }
        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            rest += Decode(chunk.Span, false);
            var start = 0;
            for (var end = rest.IndexOf('\n'); end >= 0; end = rest.IndexOf('\n', start))
            {
                yield return (rest[start..end], ++number);
                start = end + 1;
            }
            rest = rest[start..];
        }
        rest += Decode([], true);
        if (rest.Length > 0) throw new LogFormatError($"The body ends inside log line {number + 1}");
    }
}
