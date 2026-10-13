// tui.ts's live rendering from the pi.live document: the streaming partial (#syncStreaming), tool slots (apply's tool loop), and the
// working status in the editor's top border (#syncStatus).
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode.Components;
using static TuiTests;

internal static partial class Program
{
    private static string Partial(string contentJson) =>
        """{"run":{"taskId":7,"inputs":[1]},"generation":{"attempt":0,"message":""" + AssistantWire(contentJson) + "}}";

    private static IEnumerable<(string, Func<Task>)> TuiStreamingCases() =>
    [
        Case("tui.streaming.partial-updates", async () =>
        {
            await using var run = await TuiRun.Start(View([UserEntry(1, "say hello")]));
            run.Source.Publish(View([UserEntry(1, "say hello")], live: Partial($"[{TextBlock("Hel")}]")));
            var screen = await run.WaitFor("Working... (esc to abort)");
            await run.WaitUntil(text => text.Split('\n').Any(line => line.Trim() == "Hel"), "the first partial");
            run.Source.Publish(View([UserEntry(1, "say hello")], live: Partial($"[{TextBlock("Hello wor")}]")));
            await run.WaitFor("Hello wor");
            // The answer's entry takes over the streamed component; the run ends.
            run.Source.Publish(View([UserEntry(1, "say hello"), AssistantEntry(2, "Hello world!")], live: "{}"));
            screen = await run.WaitUntil(text => text.Contains("Hello world!", StringComparison.Ordinal) &&
                !text.Contains("Working...", StringComparison.Ordinal), "the final answer without the working status");
            Equal(1, Count(screen, "Hello wor"), "one assistant message");
            ShowsInOrder(screen, PlainLines(new AssistantMessageComponent((JsonObject)JsonNode.Parse(AssistantWire($"[{TextBlock("Hello world!")}]"))!), 120),
                "final answer as pi draws it");
        }),

        Case("tui.streaming.dropped-partial-rebuilds", async () =>
        {
            await using var run = await TuiRun.Start(View([UserEntry(1, "question")]));
            run.Source.Publish(View([UserEntry(1, "question")], live: Partial($"[{TextBlock("a doomed partial")}]")));
            await run.WaitFor("a doomed partial");
            // A retry dropped the partial without an entry: the transcript renders again without it.
            run.Source.Publish(View([UserEntry(1, "question")], live: """{"run":{"taskId":7,"inputs":[1]}}"""));
            var screen = await run.WaitUntil(text => !text.Contains("a doomed partial", StringComparison.Ordinal), "the partial gone");
            Check(screen.Contains("question", StringComparison.Ordinal), "transcript kept");
        }),

        Case("tui.streaming.tool-call-cards", async () =>
        {
            var call = ToolCallBlock("call_1", "read", """{"path":"notes.md"}""");
            await using var run = await TuiRun.Start(View([UserEntry(1, "read my notes")]));
            run.Source.Publish(View([UserEntry(1, "read my notes")], live: Partial($"[{TextBlock("Looking.")},{call}]")));
            await run.WaitFor("read notes.md");
            // The tool-calling answer's entry takes the streamed card over; its slot runs.
            var answer = Entry(2, "pi.assistant", AssistantWire($"[{TextBlock("Looking.")},{call}]", "toolUse"));
            run.Source.Publish(View([UserEntry(1, "read my notes"), answer],
                live: """{"run":{"taskId":7,"inputs":[1]},"tools":[{"callId":"call_1","name":"read","taskId":9,"status":"running"}]}"""));
            var screen = await run.WaitFor("Running read... (esc to abort)");
            Equal(1, Count(screen, "read notes.md"), "one card");
            run.Source.Publish(View([UserEntry(1, "read my notes"), answer, Entry(3, "pi.tool-result", ToolResultWire("call_1", "read", "remember the milk"))],
                live: "{}"));
            screen = await run.WaitUntil(text => !text.Contains("Running read", StringComparison.Ordinal), "the tool done");
            Equal(1, Count(screen, "read notes.md"), "still one card");
            // An interrupted answer never runs its calls: a streamed card says so; a call the stream never showed gets no card.
            var streamed = ToolCallBlock("call_2", "ls", """{"path":"src"}""");
            var unseen = ToolCallBlock("call_3", "grep", """{"pattern":"never"}""");
            var history = new[] { UserEntry(1, "read my notes"), answer, Entry(3, "pi.tool-result", ToolResultWire("call_1", "read", "remember the milk")) };
            run.Source.Publish(View(history, live: Partial($"[{streamed}]")));
            await run.WaitFor("ls src");
            run.Source.Publish(View([.. history, Entry(4, "pi.assistant", AssistantWire($"[{streamed},{unseen}]", "aborted"))], live: "{}"));
            screen = await run.WaitFor("Not run: the answer was interrupted.");
            Check(!screen.Contains("never", StringComparison.Ordinal), "no card for the unseen call:\n" + screen);
        }),

        Case("tui.tool.running-output-and-result", async () =>
        {
            var call = ToolCallBlock("call_5", "bash", """{"command":"make"}""");
            var history = new[] { UserEntry(1, "build it"), Entry(2, "pi.assistant", AssistantWire($"[{call}]", "toolUse")) };
            await using var run = await TuiRun.Start(View(history,
                live: """{"run":{"taskId":7,"inputs":[1]},"tools":[{"callId":"call_5","name":"bash","taskId":9,"status":"running","output":"compiling a.c\ncompiling b.c"}]}"""));
            var screen = await run.WaitFor("compiling b.c");
            Check(screen.Contains("Running bash... (esc to abort)", StringComparison.Ordinal), "status names the running tool:\n" + screen);
            Check(screen.Contains("$ make", StringComparison.Ordinal), "bash renderer's call line:\n" + screen);
            run.Source.Publish(View([.. history, Entry(3, "pi.tool-result", ToolResultWire("call_5", "bash", "build ok"))], live: "{}"));
            screen = await run.WaitFor("build ok");
            Check(!screen.Contains("compiling b.c", StringComparison.Ordinal), "the result replaces the running output");
            Check(!screen.Contains("Running bash", StringComparison.Ordinal), "status cleared");
        }),

        Case("tui.tool.subagent-working", async () =>
        {
            var call = ToolCallBlock("call_7", "subagent", """{"task":"Summarize the README"}""");
            await using var run = await TuiRun.Start(View([UserEntry(1, "delegate"), Entry(2, "pi.assistant", AssistantWire($"[{call}]", "toolUse"))],
                live: """{"run":{"taskId":7,"inputs":[1]},"tools":[{"callId":"call_7","name":"subagent","taskId":9,"status":"running","details":{"conversationId":2}}]}"""));
            var screen = await run.WaitFor("Subagent 2 is working. /agents switches to it.");
            Check(screen.Contains("Running subagent... (esc to abort)", StringComparison.Ordinal), "status:\n" + screen);
        }),

        Case("tui.status.retry-deferred-compaction", async () =>
        {
            await using var run = await TuiRun.Start(View([UserEntry(1, "q")]));
            run.Source.Publish(View([UserEntry(1, "q")], live: """{"run":{"taskId":7,"inputs":[1]},"generation":{"attempt":1,"retry":{"at":0,"error":"overloaded"}}}"""));
            await run.WaitFor("Retrying (attempt 2): overloaded");
            run.Source.Publish(View([UserEntry(1, "q")], live: """{"run":{"taskId":7,"inputs":[1]},"generation":{"attempt":0,"deferred":{"pollAt":0}}}"""));
            await run.WaitFor("Waiting for deferred response...");
            run.Source.Publish(View([UserEntry(1, "q")], live: """{"compactions":[{"taskId":3,"reason":"threshold","blocking":false,"attempt":0}]}"""));
            await run.WaitFor("Compacting (threshold)...");
            run.Source.Publish(View([UserEntry(1, "q")], live: """{"compactions":[{"taskId":3,"reason":"manual","blocking":false,"attempt":2,"retry":{"at":0,"error":"x"}}]}"""));
            await run.WaitFor("Retrying manual compaction (attempt 3)...");
            run.Source.Publish(View([UserEntry(1, "q")], live: "{}"));
            await run.WaitUntil(text => !text.Contains("Retrying", StringComparison.Ordinal), "status cleared when idle");
        }),
    ];
}
