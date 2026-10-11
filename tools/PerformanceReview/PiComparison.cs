using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

internal static class PiComparison
{
    private static bool memoryOnly;
    private static int profileRepetitions;
    private static int retentionRepetitions;
    public static async Task Run(string[] args)
    {
        var memoryArgument = Array.IndexOf(args, "--memory-case");
        memoryOnly = memoryArgument >= 0;
        var profileArgument = Array.IndexOf(args, "--profile-repetitions");
        profileRepetitions = profileArgument < 0 ? 0 : int.Parse(args[profileArgument + 1]);
        var retentionArgument = Array.IndexOf(args, "--retention-repetitions");
        retentionRepetitions = retentionArgument < 0 ? 0 : int.Parse(args[retentionArgument + 1]);
        var selectedName = memoryOnly ? args[memoryArgument + 1] : null;
        var selectedSize = memoryOnly ? int.Parse(args[memoryArgument + 2]) : 0;
        Console.WriteLine(JsonSerializer.Serialize(new { implementation = "PiSharp", runtime = Environment.Version.ToString(), warmups = !memoryOnly || retentionRepetitions > 0 ? 5 : 0, repetitions = retentionRepetitions > 0 ? retentionRepetitions : memoryOnly ? 1 : 7 }));
        foreach (var count in new[] { 500, 1000, 2000 })
        {
            if (memoryOnly && count != selectedSize) continue;
            var chunk = "data: " + JsonSerializer.Serialize(new { id = "performance", choices = new[] { new { index = 0, delta = new { content = new string('x', 32) } } } }) + "\n\n";
            var wire = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(chunk, count)) + "data: {\"id\":\"performance\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
            Console.WriteLine(JsonSerializer.Serialize(new { wire_size = count, wire_bytes = wire.Length, sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(wire)) }));
            foreach (var api in new[] { "openai-completions", "mistral-conversations" })
            {
                if (memoryOnly && selectedName != api + "-http-stream") continue;
                var model = new ModelDescriptor("offline", api, api == "openai-completions" ? "offline" : "mistral");
                using var handler = new Handler(wire);
                using var client = new HttpClient(handler);
                IChatTransport transport = api == "openai-completions"
                    ? new CompletionsHttpSseTransport(client, (_, _) => new(HttpMethod.Post, "https://offline.invalid/v1/chat/completions"))
                    : new MistralTextHttpSseTransport(client, model, new(new Uri("https://offline.invalid/"), true, new(0, 0, 0, 0), "offline") { ApiKey = "offline" });
                var request = new ChatRequest(model, [], 0);
                await Measure(api + "-http-stream", count, async sample =>
                {
                    AssistantStreamReducer? reducer = null;
                    StreamTerminalEvent? terminal = null;
                    var deltas = 0;
                    await foreach (var frame in transport.StreamAsync(request))
                    {
                        if (frame is StreamStarted start) reducer = new(start.Partial);
                        reducer?.Apply(frame);
                        if (frame is TextDelta) deltas++;
                        if (frame is StreamTerminalEvent end) terminal = end;
                        sample?.Invoke();
                    }
                    if (terminal is not StreamDone || deltas != count || ((TextContent)terminal.Message.Content.Single()).Text != new string('x', count * 32))
                        throw new Exception($"HTTP stream output mismatch: api={api}, count={count}, deltas={deltas}, terminal={terminal}");
                });
            }
        }
        var codec = new SessionEntryCodec();
        foreach (var count in new[] { 100, 500, 1000, 2000 })
        {
            if (memoryOnly) continue;
            var entries = Enumerable.Range(0, count).Select(i => codec.Parse(JsonSerializer.Serialize(new { type = "message", id = "e" + i, parentId = i == 0 ? null : "e" + (i - 1), timestamp = "2024-01-01T00:00:00.000Z", message = new { role = "user", content = new string('x', 256), timestamp = 0 } }))).ToImmutableArray();
            await Measure("session-full-projection", count, sample =>
            {
                if (new SessionContextProjector().ProjectLatest(entries).Messages.Length != count) throw new Exception("Full context mismatch");
                sample?.Invoke(); return Task.CompletedTask;
            });
            var prefix = entries.RemoveAt(count - 1);
            var projector = new SessionContextProjector();
            await Measure("session-append-projection", count, sample =>
            {
                if (projector.ProjectLatest(entries).Messages.Length != count) throw new Exception("Append context mismatch");
                sample?.Invoke(); return Task.CompletedTask;
            }, () => { projector = new(); projector.ProjectLatest(prefix); });
        }
    }

    private static async Task Measure(string name, int size, Func<Action?, Task> action, Action? prepare = null)
    {
        if (retentionRepetitions > 0)
        {
            for (var i = 0; i < 5; i++) { prepare?.Invoke(); await action(null); }
            static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Collect();
            var baseline = GC.GetTotalMemory(false);
            var retained = new List<object>();
            using var retainedProcess = Process.GetCurrentProcess();
            retainedProcess.Refresh();
            var baselineRetainedRss = retainedProcess.WorkingSet64;
            var baselineCommitted = GC.GetGCMemoryInfo().TotalCommittedBytes;
            for (var i = 1; i <= retentionRepetitions; i++)
            {
                prepare?.Invoke(); await action(null);
                if (i % 10 == 0 || i == retentionRepetitions)
                {
                    Collect(); retainedProcess.Refresh();
                    var memory = GC.GetGCMemoryInfo();
                    retained.Add(new { completed_streams = i, post_gc_heap_bytes = GC.GetTotalMemory(false),
                        post_gc_rss_bytes = retainedProcess.WorkingSet64, gc_committed_bytes = memory.TotalCommittedBytes,
                        gc_heap_size_bytes = memory.HeapSizeBytes, gc_fragmented_bytes = memory.FragmentedBytes });
                }
            }
            Console.WriteLine(JsonSerializer.Serialize(new { name, size, retention_repetitions = retentionRepetitions,
                warmups = 5, baseline_post_gc_heap_bytes = baseline, baseline_post_gc_rss_bytes = baselineRetainedRss,
                baseline_gc_committed_bytes = baselineCommitted, checkpoints = retained }));
            return;
        }
        if (profileRepetitions > 0)
        {
            for (var i = 0; i < profileRepetitions; i++) { prepare?.Invoke(); await action(null); }
            Console.WriteLine(JsonSerializer.Serialize(new { name, size, profile_repetitions = profileRepetitions }));
            return;
        }
        var timings = new List<double>();
        if (!memoryOnly)
        {
            for (var i = 0; i < 5; i++) { prepare?.Invoke(); await action(null); }
            for (var i = 0; i < 7; i++)
            {
                prepare?.Invoke(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var timer = Stopwatch.StartNew(); await action(null); timer.Stop();
                timings.Add(timer.Elapsed.TotalMilliseconds);
            }
        }
        prepare?.Invoke(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var baselineRss = process.WorkingSet64; var baselinePeakRss = process.PeakWorkingSet64;
        var baselineHeap = GC.GetTotalMemory(false);
        var peakHeap = baselineHeap;
        var baselineAllocated = GC.GetTotalAllocatedBytes(true);
        await action(() =>
        {
            peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
        });
        var allocated = GC.GetTotalAllocatedBytes(true) - baselineAllocated;
        if (memoryOnly)
        {
            process.Refresh();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                name,
                size,
                cold_process = true,
                baseline_rss_bytes = baselineRss,
                baseline_process_peak_rss_bytes = baselinePeakRss,
                process_peak_rss_bytes = process.PeakWorkingSet64,
                process_peak_rss_increase_bytes = process.PeakWorkingSet64 - baselinePeakRss,
                sampled_peak_heap_growth_bytes = peakHeap - baselineHeap
            }));
        }
        else
            Console.WriteLine(JsonSerializer.Serialize(new { name, size, median_ms = timings.Order().ElementAt(3), min_ms = timings.Min(), max_ms = timings.Max(), allocated_bytes = allocated, sampled_peak_heap_growth_bytes = peakHeap - baselineHeap }));
    }

    private sealed class Handler(byte[] wire) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wire) });
    }
}
