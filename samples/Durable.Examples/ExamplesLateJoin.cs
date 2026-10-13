// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/examples/21-late-join.ts. Upstream's timing comes from Node's
// timers: the tool pauses 100 ms between lines, pi-ai's faux provider streams the answer at 40 tokens per second, and the client joins after
// 500 ms. Here all of it runs on an ExampleClock: the Harness clock and its throttles (HarnessOptions.TimeProvider), the tool's pauses, and
// PacedModels' streaming (faux pacing, token size 1). The example drives the clock like Node's event loop, firing one timer at a time and
// letting everything it started settle before the next, so the joins, throttled commits and partials land as they do under Node. It prints
// what upstream's console.log prints, with JSON in place of util.inspect. On a file backend the storage is reopened and the transcript read
// back.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    // 21: a client attaches while a run is already underway. It gets the current state first, the conversation view or the snapshot event,
    // and then only what changes after that.
    private static async Task LateJoinAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        var clock = new ExampleClock();

        // A tool that prints a line every 100 ms, then a slowly streamed answer.
        var registry = Registry.Create();
        registry.Install(new Extension
        {
            Name = "count",
            Tools =
            [
                Define.Tool(new ToolRegistration
                {
                    Name = "count",
                    Description = "Counts to ten",
                    Execute = async (_, api, callContext) =>
                    {
                        for (var n = 1; n <= 10; n++)
                        {
                            api.Output($"{n}\n");
                            await clock.Delay(100, callContext.CancellationToken);
                        }
                        return new ToolExecutionResult();
                    },
                }),
            ],
        });
        var models = new PacedModels(clock, 40, 1,
            ScriptedReply.ToolCall("count", JsonObjectValue.Empty, "call-1"),
            "Counted to ten, and this answer streams slowly.");
        var harness = await Harness.OpenAsync(clock.Observe(await open()),
            new HarnessOptions { Models = models, Registry = registry, TimeProvider = clock }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });

        var submission = await root.SubmitAsync(SubmissionDraft.Input("Count to ten, then tell me."), Context);
        // Join while the tool is halfway through.
        await clock.RunForAsync(500);

        // Structural client: the view holds the committed transcript and documents, including the running tool's output.
        var view = await root.ViewStateAsync(Context);
        static JsonObjectValue? FirstSlot(ConversationView value) =>
            value.Doc("pi.live") is { } live && live.TryGetValue("tools", out var tools) && tools is JsonArrayValue { Count: > 0 } slots
                ? slots[0] as JsonObjectValue
                : null;
        static string Field(JsonObjectValue? slot, string name) =>
            slot is not null && slot.TryGetValue(name, out var value) && value is JsonStringValue text ? text.Value : "";
        await output.WriteLineAsync("view entries: " + JsonNames(view.Value.Entries.Select(entry => entry.Kind)));
        var joined = FirstSlot(view.Value);
        await output.WriteLineAsync($"view tool slot: {Field(joined, "status")} {Json(JsonValue.String(Field(joined, "output")))}");
        var subscription = view.Subscribe((value, _, _) =>
        {
            var slot = FirstSlot(value);
            if (Field(slot, "status") == "running") output.WriteLine("view output now: " + Json(JsonValue.String(Field(slot, "output"))));
        });
        await clock.SettleAsync();

        // Event client: the snapshot event carries the same state; later events apply on top of it.
        var stream = await harness.WatchEventsAsync(root.Id, Context);
        await output.WriteLineAsync("snapshot tools: " + JsonNames(stream.Snapshot.Tools.OfType<JsonObjectValue>()
            .Select(slot => $"{Field(slot, "name")} {Field(slot, "status")}")));
        var text = stream.Snapshot.Tools.Count > 0 ? Field(stream.Snapshot.Tools[0] as JsonObjectValue, "output") : "";
        stream.Start(async (events, _) =>
        {
            foreach (var agentEvent in events)
            {
                if (agentEvent is ToolExecutionUpdateEvent { Output: { } change })
                {
                    text = change.Apply(text);
                    await output.WriteLineAsync("event output now: " + Json(JsonValue.String(text)));
                }
                else if (agentEvent is MessageUpdateEvent update)
                {
                    var deltas = string.Concat(update.Changes.OfType<TextDeltaChange>().Where(change => change.Kind == "text_delta").Select(change => change.Delta));
                    await output.WriteLineAsync("event text delta: " + Json(JsonValue.String(deltas)));
                }
                else await output.WriteLineAsync("event: " + agentEvent.Type);
            }
        });

        await clock.RunUntilAsync(submission.WaitAsync(Context));
        await clock.RunUntilAsync(harness.WaitForIdleAsync(Context));
        await clock.SettleAsync();
        await stream.StopAsync();
        subscription.Dispose();
        view.Dispose();
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("transcript: " + Kinds(await TranscriptAsync(reopened, root.Id, 20))));
    }
}
