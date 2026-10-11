using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Tui.Pi;
using static Expect;

internal static class StreamingPerformanceCases
{
    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        yield return ("msg.assistant-message.streaming-coalesces-before-render", Sync(Coalesce));
        yield return ("msg.assistant-message.streaming-final-and-settings-use-latest", Sync(FinalAndSettings));
        yield return ("markdown.inline-long-text-allocation-budget", Sync(InlineAllocations));
    }
    private static JsonObject Message(string text) => new()
    {
        ["role"] = "assistant", ["stopReason"] = "pending",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "thinking", ["thinking"] = "reasoning" },
            new JsonObject { ["type"] = "text", ["text"] = text })
    };
    private static void Coalesce()
    {
        var messages = Enumerable.Range(1, 1000).Select(i => Message(new string('x', i * 32))).ToArray();
        (long Bytes, double Ms, string Lines) Run(bool queued, Action? sample = null)
        {
            var before = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
            var component = new AssistantMessageComponent();
            foreach (var message in messages)
            {
                if (queued) component.QueueStreamingContent(message); else component.UpdateContent(message, true);
                sample?.Invoke();
            }
            var rendered = string.Join("\n", component.Render(80)); timer.Stop();
            sample?.Invoke();
            return (GC.GetAllocatedBytesForCurrentThread() - before, timer.Elapsed.TotalMilliseconds, rendered);
        }
        Run(false); Run(true);
        var eager = Run(false); var coalesced = Run(true);
        Equal(eager.Lines, coalesced.Lines, "coalesced visual output");
        Console.WriteLine(JsonSerializer.Serialize(new { benchmark = "ui-1000-updates-one-render", before_bytes = eager.Bytes,
            after_bytes = coalesced.Bytes, before_ms = eager.Ms, after_ms = coalesced.Ms }));
        Check(coalesced.Bytes < eager.Bytes, $"Intermediate UI updates still build components: {eager.Bytes} -> {coalesced.Bytes} bytes.");
        if (Environment.GetEnvironmentVariable("PISHARP_PI_COMPARISON") == "1")
        {
            for (var i = 0; i < 5; i++) Run(true);
            var runs = new List<double>();
            for (var i = 0; i < 7; i++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                runs.Add(Run(true).Ms);
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var baseline = GC.GetTotalMemory(false); var peak = baseline;
            var memory = Run(true, () => peak = Math.Max(peak, GC.GetTotalMemory(false)));
            Console.WriteLine(JsonSerializer.Serialize(new { name = "ui-1000-updates-one-render", size = 1000,
                median_ms = runs.Order().ElementAt(3), min_ms = runs.Min(), max_ms = runs.Max(), allocated_bytes = memory.Bytes,
                sampled_peak_heap_growth_bytes = peak - baseline }));
        }
    }
    private static void FinalAndSettings()
    {
        var component = new AssistantMessageComponent(Message("old"));
        component.QueueStreamingContent(Message("new")); component.SetHideThinkingBlock(true);
        var reference = new AssistantMessageComponent(Message("new"), true); reference.UpdateContent(Message("new"), true);
        Equal(string.Join("\n", reference.Render(80)), string.Join("\n", component.Render(80)), "settings use latest update");
        component.QueueStreamingContent(Message("obsolete"));
        var final = Message("final"); final["stopReason"] = "length";
        component.UpdateContent(final, false);
        Contains(Strip(string.Join("\n", component.Render(80))), "Response was truncated before completion.", "final state flushes pending content");
        Check(!Strip(string.Join("\n", component.Render(80))).Contains("obsolete", StringComparison.Ordinal), "Pending update replaced final content.");
    }
    private static void InlineAllocations()
    {
        var text = string.Concat(Enumerable.Repeat("Plain text with π and emoji 😀. ", 1000));
        MarkdownLexer.InlineTokens(text);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var tokens = MarkdownLexer.InlineTokens(text);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Equal(1, tokens.Count, "one plain text token"); Equal(text, tokens[0].Text, "unchanged plain text");
        Check(allocated < 2_000_000, $"Inline plain text allocated {allocated} bytes.");
    }
}
