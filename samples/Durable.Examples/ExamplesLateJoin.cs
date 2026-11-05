// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/examples/20-late-join.ts. Upstream's timing comes from Node's
// timers: the tool pauses 100 ms between lines, pi-ai's faux provider streams the answer at 40 tokens per second, and the client joins after
// 500 ms. Here all of it runs on an ExampleClock: the Harness clock and its throttles (HarnessOptions.TimeProvider), the tool's pauses, and
// PacedModels' streaming (faux pacing, token size 1). The example drives the clock like Node's event loop, firing one timer at a time and
// letting everything it started settle before the next, so the joins, throttled commits and partials land as they do under Node. It prints
// what upstream's console.log prints, with JSON in place of util.inspect. On a file backend the storage is reopened and the transcript read
// back.
// Pi v1.5.0 sync (area events, f3255de76): the client is the conversation view alone, as in v1.5.0's 20-late-join.ts: the event client of
// watchEvents (removed) is gone, and the view's listener also prints the streamed answer's partial when its text changes and the view's
// entries once the run ended. Area tools-schema: the count tool's obj({}) is ToolRegistration's default empty parameters. Area vocabulary
// (5f312dac4): the view's listener takes the tagged call and skips one that is not a value. Since the closeout the example carries
// v1.5.0's name (b9fb2b854).
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    // 20: a client attaches while a run is already underway. It gets the current state first, the conversation view, and then only what
    // changes after that.
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

        // The view holds the newest entries and the documents, including the running tool's output and, later, the answer being streamed.
        var view = await root.WatchAsync(Context);
        static JsonObjectValue? FirstSlot(ConversationView value) =>
            value.Doc("pi.live") is { } live && live.TryGetValue("tools", out var tools) && tools is JsonArrayValue { Count: > 0 } slots
                ? slots[0] as JsonObjectValue
                : null;
        static string Field(JsonObjectValue? slot, string name) =>
            slot is not null && slot.TryGetValue(name, out var value) && value is JsonStringValue text ? text.Value : "";
        // The text of the first text block of the streamed answer's partial, "" without one.
        static string Partial(JsonObjectValue live) =>
            live.TryGetValue("generation", out var generation) && generation is JsonObjectValue state && state.TryGetValue("message", out var message) &&
            message is JsonObjectValue partial && partial.TryGetValue("content", out var content) && content is JsonArrayValue blocks &&
            blocks.OfType<JsonObjectValue>().FirstOrDefault(block => Field(block, "type") == "text") is { } text ? Field(text, "text") : "";
        await output.WriteLineAsync("view entries: " + JsonNames(view.Value.Entries.Select(entry => entry.Kind)));
        var joined = FirstSlot(view.Value);
        await output.WriteLineAsync($"view tool slot: {Field(joined, "status")} {Json(JsonValue.String(Field(joined, "output")))}");
        var shown = "";
        view.Start((call, _) =>
        {
            if (call is not WatchValueCall<ConversationView> { Value: var value }) return Task.CompletedTask;
            var state = value.Doc("pi.live") ?? JsonObjectValue.Empty;
            if (FirstSlot(value) is var slot && Field(slot, "status") == "running")
                output.WriteLine("view output now: " + Json(JsonValue.String(Field(slot, "output"))));
            var partial = Partial(state);
            if (partial != shown && partial != "") output.WriteLine("view answer now: " + Json(JsonValue.String(partial)));
            shown = partial;
            if (!state.ContainsKey("run")) output.WriteLine("view entries: " + JsonNames(value.Entries.Select(entry => entry.Kind)));
            return Task.CompletedTask;
        });

        await clock.RunUntilAsync(submission.WaitAsync(Context));
        await clock.RunUntilAsync(harness.WaitForIdleAsync(Context));
        await clock.SettleAsync();
        await view.StopAsync();
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("transcript: " + Kinds(await TranscriptAsync(reopened, root.Id, 20))));
    }
}
