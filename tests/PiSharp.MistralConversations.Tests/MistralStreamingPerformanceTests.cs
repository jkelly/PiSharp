using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.Contracts;

internal static class MistralStreamingPerformanceTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("mistral lazy emission snapshots retain each historical value", Snapshots);
        yield return ("mistral deferred and materialized reducer admission agree at limits", Admission);
        yield return ("mistral compact progress allocation budget", Allocations);
        yield return ("mistral buffered framing matches fragmented reads and keeps frame limits", Framing);
    }
    private static readonly ChatRequest Request = new(new("offline", "mistral-conversations", "mistral"), [], 0);
    private static string Wire(string text, int count)
    {
        var chunk = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } }) + "\n\n";
        return string.Concat(Enumerable.Repeat(chunk, count)) + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
    }
    private static async Task<List<StreamEvent>> Read(string wire, bool reduce = false, ChatRequest? request = null,
        int? readSize = null, int? frameLimit = null)
    {
        using var client = new HttpClient(new Handler(wire, readSize));
        request ??= Request;
        var options = new MistralTextOptions(new("https://offline.invalid/"), true, new(0, 0, 0, 0), "offline") { ApiKey = "offline" };
        if (frameLimit is { } limit) options = options with { MaximumFrameCharacters = limit };
        var transport = new MistralTextHttpSseTransport(client, request.Model,
            options);
        var reducer = new AssistantStreamReducer(new(request.Model.Api, request.Model.Provider, request.Model.Id, 0, [], TokenUsage.Zero, StopReason.Pending));
        var events = new List<StreamEvent>();
        await foreach (var frame in transport.StreamAsync(request)) { if (reduce) reducer.Apply(frame); events.Add(frame); }
        return events;
    }
    private static async Task Snapshots()
    {
        const string text = "π\"\\\n😀";
        var events = await Read(Wire(text, 3));
        var deltas = events.OfType<TextDelta>().ToArray();
        for (var i = 0; i < deltas.Length; i++)
        {
            var snapshot = deltas[i].SourceEmissionSnapshot!;
            Check(snapshot.Value.GetProperty("content")[0].GetProperty("text").GetString() == string.Concat(Enumerable.Repeat(text, i + 1)), "Historical snapshot changed.");
            Check((deltas[i] with { SourceEmissionSnapshot = null }).SourceEmissionSnapshot is null, "Clearing a lazy snapshot did not clear its factory.");
        }
        Check(events[^1] is StreamDone, "Stream did not settle.");
    }
    private static async Task Admission()
    {
        foreach (var modelLength in new[] { 7, 1024 })
        foreach (var limit in new[] { 4096, 8192, 100_000 })
        {
            var request = Request with { Model = Request.Model with { Id = new string('m', modelLength) } };
            var events = await Read(Wire("\"\\\nπ😀", 200), request: request);
            var fallback = ((StreamStarted)events[0]).Partial;
            var lazy = new AssistantStreamReducer(fallback, new(MaximumCharacters: limit));
            var eager = new AssistantStreamReducer(fallback, new(MaximumCharacters: limit));
            foreach (var frame in events)
            {
                Exception? left = null, right = null;
                try { lazy.Apply(frame); } catch (Exception e) { left = e; }
                try { eager.Apply(frame with { SourceEmissionSnapshot = frame.SourceEmissionSnapshot }); } catch (Exception e) { right = e; }
                Check(left?.GetType() == right?.GetType(), "Deferred admission changed a limit outcome.");
                if (left is not null) break;
                Check(PiWireJson.WriteMessage(lazy.Snapshot()).ToString() == PiWireJson.WriteMessage(eager.Snapshot()).ToString(), "Deferred admission changed reducer state.");
            }
        }
    }
    private static async Task Allocations()
    {
        var wire = Wire(new string('x', 32), 1000);
        await Read(wire, true);
        var before = GC.GetTotalAllocatedBytes(true);
        var events = await Read(wire, true);
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        Check(events[^1] is StreamDone && events.OfType<TextDelta>().Count() == 1000, "Compact stream lost frames.");
        Check(allocated < 7_000_000, $"Compact Mistral stream allocated {allocated} bytes.");
    }
    private static async Task Framing()
    {
        var chunk = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = "π😀\\\"\n" } } } });
        var split = chunk.IndexOf("delta", StringComparison.Ordinal) - 1;
        var multiline = "event: ignored\r\ndata:\u00a0" + chunk[..split] + "\r\ndata:\t" + chunk[split..] + "\r\n\r\n";
        var wire = "\uFEFF" + string.Concat(Enumerable.Repeat(multiline, 40)) + Wire("tail", 40);
        var expected = (await Read(wire, true)).Select(Describe).ToArray();
        foreach (var readSize in new[] { 1, 2, 3, 7, 31, 127, 4096 })
        {
            var actual = (await Read(wire, true, readSize: readSize)).Select(Describe).ToArray();
            Check(expected.SequenceEqual(actual), $"Frame/snapshot changed with read size {readSize}.");
        }
        // A read can contain many individually small frames even when the whole
        // buffer exceeds the per-frame bound. An oversized individual frame fails.
        var small = await Read(Wire("x", 300), true, frameLimit: 160);
        Check(small[^1] is StreamDone && small.OfType<TextDelta>().Count() == 300, "Frame limit was applied to a whole read buffer.");
        var large = await Read("data: " + new string('x', 200) + "\n\n", frameLimit: 160);
        Check(large[^1] is StreamError { NativeDiagnostic.Code: NativeChatFailureCode.ResourceLimit }, "Oversized frame admission changed.");

        static string Describe(StreamEvent frame) => PiWireJson.WriteEvent(frame) + "|" + frame.SourceEmissionSnapshot;
    }
    private sealed class SplitStream(string wire, int readSize) : MemoryStream(Encoding.UTF8.GetBytes(wire))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            base.ReadAsync(buffer[..Math.Min(readSize, buffer.Length)], token);
    }
    private sealed class Handler(string wire, int? readSize = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = readSize is { } size
                ? new StreamContent(new SplitStream(wire, size)) : new StringContent(wire, Encoding.UTF8) });
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
