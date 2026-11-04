// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/examples/31-transcript.ts (added in f3255de76). Here it is
// 32-transcript: PiSharp's examples keep their Pi v1.2.0 numbers, and 31 is 31-reload-and-restart. Upstream waits one setTimeout(0) for
// the watch to deliver the 15 live appends; here the example waits until the transcript holds the newest entry it committed, which is the
// same point without a timer. It prints what upstream's console.log prints. On a file backend the storage is reopened in a plain Session
// and the stored entries are counted (PiSharp-only, as every example's read-back).
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    // 32: a client's transcript over the windowed conversation view. ConversationTranscript merges every value of a watch into the client's
    // whole transcript, marks entries a stall skipped as a gap, and loads them, and earlier pages, on demand. A client that drops resumes
    // after the newest entry it has.
    private static async Task TranscriptExampleAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // No model is asked anything: the example only appends entries.
        var models = new ScriptedModels(Array.Empty<string>());
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = models, Registry = Registry.Create() }, Context);
        var root = await harness.RootAsync(Context);
        var count = 0;
        EntryRecord? newest = null;
        // Commit `n` chat lines, one entry each.
        async Task Chat(int n)
        {
            for (var index = 0; index < n; index++)
            {
                var line = $"line {++count}";
                newest = await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "chat", Data = line }), Context);
            }
        }

        // One line per item: ranges of entries, gaps, and the "load earlier" marker.
        async Task Render(string label, ConversationTranscriptView shown)
        {
            var rows = new List<string>();
            (long First, long Last)? run = null;
            void Flush()
            {
                if (run is { } range) rows.Add(range.First == range.Last ? $"{range.First}" : $"{range.First}..{range.Last}");
                run = null;
            }
            foreach (var item in shown.Items)
            {
                if (item is ConversationTranscriptEntry { Entry: var entry })
                {
                    var line = long.Parse(((JsonStringValue)entry.Data!).Value["line ".Length..], System.Globalization.CultureInfo.InvariantCulture);
                    run = run is { } range ? (range.First, line) : (line, line);
                    continue;
                }
                Flush();
                rows.Add(item is ConversationTranscriptGap ? "[load more]" : "[load earlier]");
            }
            Flush();
            await output.WriteLineAsync($"{label.PadRight(28)} {string.Join(" ", rows)}");
        }

        static ConversationTranscriptGap? FirstGap(ConversationTranscriptView shown) => shown.Items.OfType<ConversationTranscriptGap>().FirstOrDefault();

        await Chat(200);

        // Attach: the window holds the newest `min` entries; the transcript shows "load earlier" above them.
        var transcript = new ConversationTranscript((query, limit, ctx) => root.EntriesAsync(query, limit, null, ctx),
            new ConversationTranscriptOptions { PageSize = 20 });
        var window = new ConversationWindow { Min = 10, Max = 20 };
        var watch = await root.WatchAsync(Context, window);
        var view = transcript.Merge(watch.Value);
        await Render("attach", view);
        Task Follow(WatchCall<ConversationView> call, DurableContext _)
        {
            if (call is WatchValueCall<ConversationView> { Value: var value }) Volatile.Write(ref view, transcript.Merge(value));
            return Task.CompletedTask;
        }
        watch.Start(Follow);

        // Live: appends. The window drops its oldest past `max`; the transcript keeps them.
        await Chat(15);
        await UntilAsync(() => transcript.Newest == newest!.Id);
        await Render("15 more, live", Volatile.Read(ref view));

        // The user scrolls up: one page.
        view = await transcript.LoadEarlierAsync(Context);
        await Render("load earlier", view);

        // A drop: the client misses 40 entries, more than `max`, and resumes after the newest it has. The window starts at the newest `min`
        // again, and `before` shows the transcript a gap.
        await watch.StopAsync();
        await Chat(40);
        watch = await root.WatchAsync(Context, window with { From = transcript.Resume?.From });
        view = transcript.Merge(watch.Value);
        await Render("resume after 40 missed", view);
        watch.Start(Follow);

        // "load more" fills the gap a page at a time.
        for (var gap = FirstGap(view); gap is not null; gap = FirstGap(view))
        {
            view = await transcript.FillGapAsync(gap, Context);
            await Render("fill gap", view);
        }

        // A short drop resumes without a gap: the window starts right after the newest entry.
        await watch.StopAsync();
        await Chat(3);
        watch = await root.WatchAsync(Context, window with { From = transcript.Resume?.From });
        await output.WriteLineAsync($"{"resume after 3 missed".PadRight(28)} window: {watch.Value.Entries.Length} entries");
        view = transcript.Merge(watch.Value);
        await Render("", view);

        await watch.StopAsync();
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
        {
            var stored = await TranscriptAsync(reopened, root.Id, 1000);
            await output.WriteLineAsync($"stored: {stored.Count(entry => entry.Kind == "chat")} chat entries, the newest {Json(stored[^1].Data)}");
        });
    }

    // Waits for a condition the example's own work makes true, checking every millisecond for at most ten seconds.
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; !condition(); attempt++)
        {
            if (attempt == 10_000) throw new TimeoutException("The example's watch did not deliver.");
            await Task.Delay(1);
        }
    }
}
