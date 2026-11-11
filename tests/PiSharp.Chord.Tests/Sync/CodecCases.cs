// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/sync/batch.ts, packages/chord/src/sync/lines.ts.
// Authored native regression; not an upstream execution capture.
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Chord.Store;
using PiSharp.Chord.Sync;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SyncCodecCases()
    {
        yield return Case("sync.codec.envelope", () =>
        {
            var commit = new OutboxCommit(1, -2, new JsonArray(new JsonObject { ["text"] = "😀", ["2"] = 2, ["1"] = 1 }));
            const string expected = "{\"v\":2,\"seq\":1,\"nextId\":-2,\"writes\":[{\"1\":1,\"2\":2,\"text\":\"😀\"}]}\n";
            Equal(expected, SyncCodec.EncodeLogLine(commit), "wire order and JavaScript stringification");
            var decoded = SyncCodec.DecodeLogLine(expected.TrimEnd('\n'), 7);
            Equal(commit.Seq, decoded.Seq, "sequence");
            Equal(commit.NextId, decoded.NextId, "negative next id remains permitted");
            Json("[{\"text\":\"😀\",\"1\":1,\"2\":2}]", decoded.Writes, "writes");
            Equal(2L, SyncCodec.DecodeLogLine("{\"v\":1,\"v\":2,\"seq\":1,\"seq\":2,\"nextId\":0,\"writes\":[]}", 1).Seq,
                "duplicate fields keep their last value");
        });
        yield return Case("sync.codec.invalid_envelopes", () =>
        {
            foreach (var text in new[]
            {
                "no", "null", "[]", "{}", "{\"v\":1}", "{\"v\":\"2\"}",
                "{\"v\":2,\"seq\":0,\"nextId\":0,\"writes\":[]}",
                "{\"v\":2,\"seq\":1.5,\"nextId\":0,\"writes\":[]}",
                "{\"v\":2,\"seq\":9007199254740992,\"nextId\":0,\"writes\":[]}",
                "{\"v\":2,\"seq\":true,\"nextId\":0,\"writes\":[]}",
                "{\"v\":2,\"seq\":1,\"writes\":[]}",
                "{\"v\":2,\"seq\":1,\"nextId\":9007199254740992,\"writes\":[]}",
                "{\"v\":2,\"seq\":1,\"nextId\":0,\"writes\":{}}",
            }) Check(Throws<LogFormatError>(() => SyncCodec.DecodeLogLine(text, 7), text).Message.Contains("7"), "error identifies line");
            Json("[null,1,{}]", SyncCodec.DecodeLogLine("{\"v\":2,\"seq\":1,\"nextId\":0,\"writes\":[null,1,{}]}", 1).Writes,
                "write validation is deferred to apply");
        });
        yield return Case("sync.codec.roundtrip_chunks", async () =>
        {
            var commits = new[] { new OutboxCommit(1, 2, new JsonArray("😀")), new OutboxCommit(3, 4, new JsonArray("é")) };
            var bytes = await SyncCodecBytes(SyncCodec.EncodeBatch(commits));
            Equal((byte)0x1f, bytes[0], "gzip magic");
            Equal((byte)0x8b, bytes[1], "gzip magic");
            foreach (var size in new[] { 1, 7, bytes.Length })
            {
                var decoded = await SyncCodecCollect(SyncCodec.DecodeBatchLines(SyncCodecChunks(bytes, size)));
                Sequence(new long[] { 1, 3 }, decoded.Select(line => line.Commit.Seq), "sequence order");
                Equal(SyncCodec.EncodeLogLine(commits[0]).Length, decoded[0].Bytes, "size uses UTF-16 units including newline");
                Json("[\"😀\"]", decoded[0].Commit.Writes, "split UTF8");
            }
            Equal(0, (await SyncCodecCollect(SyncCodec.DecodeBatch(SyncCodec.EncodeBatch(Array.Empty<OutboxCommit>())))).Count, "empty gzip batch");
        });
        yield return Case("sync.codec.trust_boundaries", async () =>
        {
            var line = SyncCodec.EncodeLogLine(new OutboxCommit(3, 4, new JsonArray()));
            await SyncCodecExpect<LogFormatError>(() => SyncCodecCollect(SyncCodec.DecodeBatch(SyncCodecRaw(line + line[..5]))), "ends inside log line 2");
            await SyncCodecExpect<LogFormatError>(() => SyncCodecCollect(SyncCodec.DecodeBatch(SyncCodecRaw(line + line))), "does not increase");
            await SyncCodecExpect<LogFormatError>(() => SyncCodecCollect(SyncCodec.DecodeBatch(SyncCodecRaw("\n"))), "not JSON");
            await SyncCodecExpect<LogFormatError>(() => SyncCodecCollect(SyncCodec.DecodeBatch(SyncCodecRawBytes(new byte[] { 0xff, 10 }))), "not UTF-8");
            await SyncCodecExpect<LogFormatError>(() => SyncCodecCollect(SyncCodec.DecodeBatch(SyncCodecRawBytes(new byte[] { 0xe2 }))), "not UTF-8");
            Equal(1, (await SyncCodecCollect(SyncCodec.DecodeBatch(SyncCodecRaw("\uFEFF" + line)))).Count, "initial BOM removed");
            var bytes = await SyncCodecBytes(SyncCodecRaw(line));
            await SyncCodecExpect<InvalidDataException>(() => SyncCodecCollect(SyncCodec.DecodeBatch(SyncCodecChunks(bytes[..^1], 7))), "");
            bytes[^8] ^= 1;
            await SyncCodecExpect<InvalidDataException>(() => SyncCodecCollect(SyncCodec.DecodeBatch(SyncCodecChunks(bytes, 7))), "");
            await SyncCodecExpect<InvalidDataException>(() => SyncCodecCollect(SyncCodec.DecodeBatch(SyncCodecChunks(new byte[] { 1, 2, 3 }, 1))), "");
        });
        yield return Case("sync.codec.empty_bodies", async () =>
        {
            async IAsyncEnumerable<JsonObject> NoParts() { await Task.CompletedTask; yield break; }
            async IAsyncEnumerable<ReadOnlyMemory<byte>> EmptyChunks()
            {
                yield return ReadOnlyMemory<byte>.Empty;
                await Task.CompletedTask;
                yield return ReadOnlyMemory<byte>.Empty;
            }
            var emptyBatch = await SyncCodecBytes(SyncCodec.EncodeBatch(Array.Empty<OutboxCommit>()));
            var emptySnapshot = await SyncCodecBytes(SyncCodec.EncodeSnapshot(NoParts()));
            var emptyChunks = await SyncCodecBytes(BodyStreams.Gzip(EmptyChunks()));
            foreach (var bytes in new[] { emptyBatch, emptySnapshot, emptyChunks })
            {
                Check(bytes.Length >= 20, "empty source emits a complete gzip member");
                Equal((byte)0x1f, bytes[0], "empty member magic");
                Equal((byte)0x8b, bytes[1], "empty member magic");
                Equal(0, (await SyncCodecCollect(SyncCodec.DecodeBatch(SyncCodecChunks(bytes, 1)))).Count, "empty batch member decodes");
                Equal(0, (await SyncCodecCollect(SyncCodec.DecodeSnapshot(SyncCodecChunks(bytes, 1)))).Count, "empty snapshot member decodes");
            }
            await SyncCodecExpect<InvalidDataException>(() => SyncCodecCollect(SyncCodec.DecodeBatch(SyncCodecChunks([], 1))), "");
        });
        yield return Case("sync.codec.lazy_decode_and_release", async () =>
        {
            var bytes = await SyncCodecBytes(SyncCodecRaw(SyncCodec.EncodeLogLine(new OutboxCommit(1, 2, new JsonArray())) + "not json\n"));
            var released = false;
            async IAsyncEnumerable<ReadOnlyMemory<byte>> Tracked()
            {
                try { await foreach (var chunk in SyncCodecChunks(bytes, 1)) yield return chunk; }
                finally { released = true; }
            }
            await using (var iterator = SyncCodec.DecodeBatch(Tracked()).GetAsyncEnumerator())
            {
                Check(await iterator.MoveNextAsync(), "first commit available before malformed later line");
                Equal(1L, iterator.Current.Seq, "first commit");
            }
            Check(released, "early decode disposal releases body");
        });
        yield return Case("sync.codec.bounded_encode_and_release", async () =>
        {
            var released = false;
            var consumed = 0;
            var random = new Random(42);
            var raw = new byte[128 * 1024];
            random.NextBytes(raw);
            var payload = Convert.ToBase64String(raw);
            async IAsyncEnumerable<OutboxCommit> Source([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                try
                {
                    for (var seq = 1; seq <= 100; seq++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        consumed++;
                        yield return new(seq, seq + 1, new JsonArray(payload));
                        await Task.CompletedTask;
                    }
                }
                finally { released = true; }
            }
            await using (var iterator = SyncCodec.EncodeBatch(Source()).GetAsyncEnumerator())
            {
                Check(await iterator.MoveNextAsync(), "compressed chunk available");
                Check(consumed < 100, "first output does not buffer the whole source");
            }
            Check(released, "early encoding disposal joins and releases source");
        });
        yield return Case("sync.codec.source_failure_and_cancellation", async () =>
        {
            var released = false;
            async IAsyncEnumerable<OutboxCommit> Failing()
            {
                try { yield return new(1, 2, new JsonArray()); await Task.CompletedTask; throw new InvalidOperationException("source failed"); }
                finally { released = true; }
            }
            await SyncCodecExpect<InvalidOperationException>(() => SyncCodecCollect(SyncCodec.EncodeBatch(Failing())), "source failed");
            Check(released, "failed source released");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await SyncCodecExpect<OperationCanceledException>(() => SyncCodecCollect(SyncCodec.EncodeBatch(new[] { new OutboxCommit(1, 2, new JsonArray()) }, cancellation.Token)), "");
        });
        yield return Case("sync.codec.snapshot_roundtrip", async () =>
        {
            var parts = new[] { new JsonObject { ["kind"] = "manifest", ["lastSeq"] = 2 }, new JsonObject { ["kind"] = "row", ["value"] = "😀" } };
            async IAsyncEnumerable<JsonObject> Parts() { foreach (var part in parts) yield return part; await Task.CompletedTask; }
            var actual = await SyncCodecCollect(SyncCodec.DecodeSnapshot(SyncCodec.EncodeSnapshot(Parts())));
            Equal(2, actual.Count, "snapshot parts");
            Json(parts[0].ToJsonString(), actual[0], "manifest");
            Json(parts[1].ToJsonString(), actual[1], "row");
        });
        yield return Case("sync.codec.snapshot_early_release", async () =>
        {
            var released = false;
            var random = new Random(17);
            var bytes = new byte[128 * 1024];
            random.NextBytes(bytes);
            var payload = Convert.ToBase64String(bytes);
            async IAsyncEnumerable<JsonObject> Parts([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                try
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return new JsonObject { ["value"] = payload };
                        await Task.CompletedTask;
                    }
                }
                finally { released = true; }
            }
            await using (var iterator = SyncCodec.EncodeSnapshot(Parts()).GetAsyncEnumerator())
                Check(await iterator.MoveNextAsync(), "snapshot body starts streaming");
            Check(released, "abandoning snapshot returns the export enumerator");
        });
    }

    private static async Task<List<T>> SyncCodecCollect<T>(IAsyncEnumerable<T> source)
    {
        var values = new List<T>();
        await foreach (var value in source) values.Add(value);
        return values;
    }
    private static async Task<byte[]> SyncCodecBytes(IAsyncEnumerable<ReadOnlyMemory<byte>> source)
    {
        var bytes = new List<byte>();
        await foreach (var chunk in source) bytes.AddRange(chunk.ToArray());
        return bytes.ToArray();
    }
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SyncCodecChunks(byte[] bytes, int size)
    {
        for (var offset = 0; offset < bytes.Length; offset += size) yield return bytes.AsMemory(offset, Math.Min(size, bytes.Length - offset));
        await Task.CompletedTask;
    }
    private static IAsyncEnumerable<ReadOnlyMemory<byte>> SyncCodecRaw(string text) => SyncCodecRawBytes(Encoding.UTF8.GetBytes(text));
    private static IAsyncEnumerable<ReadOnlyMemory<byte>> SyncCodecRawBytes(byte[] bytes) => BodyStreams.Gzip(SyncCodecChunks(bytes, 7));
    private static async Task SyncCodecExpect<T>(Func<Task> run, string message) where T : Exception
    {
        try { await run(); }
        catch (T error) { Check(error.Message.Contains(message, StringComparison.Ordinal), "expected error text: " + message); return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
