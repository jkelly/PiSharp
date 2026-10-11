using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

internal static class CompletionsStreamingPerformanceTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("completions-performance.compact-text-allocation-budget", AllocationBudget);
        yield return ("completions-performance.compact-and-observed-progress-match", EquivalentProgress);
    }

    private static async Task AllocationBudget()
    {
        var wire = Wire(new string('x', 32), 1000);
        await Read(wire, null); // Warm code paths before measuring process-wide async allocations.
        var before = GC.GetTotalAllocatedBytes(true);
        var frames = await Read(wire, null);
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        Check(frames.OfType<TextDelta>().Count() == 1000 && frames[^1] is StreamDone,
            "Compact streaming lost progress or successful settlement.");
        Check(((TextContent)((StreamDone)frames[^1]).Message.Content.Single()).Text == new string('x', 32_000),
            "Compact streaming changed authoritative text.");
        // Covers HTTP/SSE and owned events, including final source admission. The
        // redundant JSON conversion path still exceeded 15 MB after compact deltas.
        Check(allocated < 12_000_000, $"Compact streaming allocated {allocated} bytes for 32,000 characters.");
    }

    private static async Task EquivalentProgress()
    {
        foreach (var limit in new[] { 8_388_608, 8192 })
        foreach (var text in new[] { "plain text", "\"\\\n\tπ😀" })
        {
            var wire = Wire(text, 200, thinking: true);
            var compact = await Read(wire, new() { MaximumSourceValueCharacters = limit, MaximumSourceValueBytes = limit });
            var publications = 0;
            var observed = await Read(wire, new()
            {
                MaximumSourceValueCharacters = limit, MaximumSourceValueBytes = limit,
                Hooks = new() { OnSourcePublished = _ => publications++ }
            });
            Check(publications > 0 && compact.Count == observed.Count, "Source hook or event count changed.");
            for (var i = 0; i < compact.Count; i++)
                Check(JsonSerializer.Serialize(compact[i], compact[i].GetType()) ==
                    JsonSerializer.Serialize(observed[i], observed[i].GetType()), "Observed and compact frames differ.");
        }
    }

    private static string Wire(string text, int count, bool thinking = false)
    {
        var value = new Dictionary<string, object?> { [thinking ? "reasoning_content" : "content"] = text };
        var chunk = JsonSerializer.Serialize(new { id = "performance", choices = new[] { new { delta = value } } });
        var wire = new StringBuilder();
        for (var i = 0; i < count; i++) wire.Append("data: ").Append(chunk).Append("\n\n");
        return wire.Append("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n").ToString();
    }

    private static async Task<List<StreamEvent>> Read(string wire, CompletionsHttpSseOptions? options)
    {
        using var handler = new Handler(wire);
        using var client = new HttpClient(handler);
        var transport = new CompletionsHttpSseTransport(client,
            (_, _) => new(HttpMethod.Post, "https://offline.invalid/completions"), options);
        var frames = new List<StreamEvent>();
        await foreach (var frame in transport.StreamAsync(new(new("offline", "openai-completions", "offline"), [], 0)))
            frames.Add(frame);
        return frames;
    }

    private sealed class Handler(string wire) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(wire) });
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
