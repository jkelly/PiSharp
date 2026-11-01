// tui.ts's transcript (DurableTui.#syncTranscript, #addEntry, #rebuild, CompactionComponent) and footer (#syncFooter), on a
// VirtualTerminal. Where pi's interactive components draw an entry, the expectation is that same component rendered on its own at the
// screen's width, as pi's interactive mode would draw it.
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Durable;
using static TuiTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> TuiTranscriptCases() =>
    [
        Case("tui.transcript.initial-render", async () =>
        {
            var view = View([
                UserEntry(1, "Read the README please"),
                Entry(2, "pi.assistant", AssistantWire($"[{TextBlock("Reading it now.")},{ToolCallBlock("call_1", "read", """{"path":"README.md"}""")}]",
                    "toolUse", Usage(1200, 300))),
                Entry(3, "pi.tool-result", ToolResultWire("call_1", "read", "# Title\nline two")),
                AssistantEntry(4, "The README has a title.", Usage(1500, 40)),
            ], usage: """{"models":{"faux/faux-1":""" + Usage(2700, 340, 0, 0, 0.0123) + """},"tools":{}}""");
            await using var run = await TuiRun.Start(view);
            var screen = await run.WaitFor("The README has a title.");
            // Each entry as pi's components draw it.
            var ui = StandaloneTui();
            var expected = new List<string>();
            expected.AddRange(PlainLines(new UserMessageComponent("Read the README please"), 120));
            expected.AddRange(PlainLines(new AssistantMessageComponent((JsonObject)JsonNode.Parse(
                AssistantWire($"[{TextBlock("Reading it now.")}]", "toolUse"))!), 120));
            var card = new ToolExecutionComponent("read", "call_1", JsonNode.Parse("""{"path":"README.md"}"""), null,
                BuiltInToolRenderers.CreateAllToolRenderers()["read"], ui, Cwd);
            card.SetArgsComplete();
            card.UpdateResult((JsonObject)JsonNode.Parse(ToolResultWire("call_1", "read", "# Title\nline two"))!);
            expected.AddRange(PlainLines(card, 120));
            expected.AddRange(PlainLines(new AssistantMessageComponent((JsonObject)JsonNode.Parse(AssistantWire($"[{TextBlock("The README has a title.")}]"))!), 120));
            ShowsInOrder(screen, expected, "transcript");
            Equal(1, Count(screen, "read README.md"), "one card for the call");
            // Footer: usage, the context share of the newest answer (1540 of faux-1's 100k), the cwd, then the hints.
            Check(screen.Contains(" ↑2.7k ↓340 $0.012 1.5%/100k  /work/project", StringComparison.Ordinal), "footer stats:\n" + screen);
            var hints = $"main · faux/faux-1 · thinking:medium ({KeybindingHints.KeyText("app.thinking.cycle")}) · " +
                $"{KeybindingHints.KeyText("app.model.select")} or /model · /agents · /compact · /tasks · /plan · " +
                $"{KeybindingHints.KeyText("app.message.followUp")} follow-up · {KeybindingHints.KeyText("app.clear")} exit";
            Check(Flat(screen).Contains(hints, StringComparison.Ordinal), $"footer hints \"{hints}\":\n" + screen);
            Check(Flat(screen).Contains("main · faux/faux-1 · thinking:medium (shift+tab) · ctrl+l or /model", StringComparison.Ordinal),
                "pi's default keys in the hints");
        }),

        Case("tui.transcript.footer-states", async () =>
        {
            // No answer yet: unknown context share; no usage: only the cost.
            var view = View([UserEntry(1, "hi")]);
            await using var run = await TuiRun.Start(view);
            await run.WaitFor(" $0.000 ?%/100k  /work/project");
            // A subagent's conversation: its label in the accent color; no model; usage over every bucket, cache reads and writes.
            var subagent = Conversation([AssistantEntry(1, "near the limit", Usage(95_000, 10))], """{"pi.agent":{},"pi.usage":{"models":{"a/b":""" +
                Usage(100, 20, 3000, 400, 0.5) + """},"tools":{"bash":""" + Usage(1, 2, 0, 0, 0.25) + """}}}""", new ConversationId(2));
            run.Source.Publish(View(subagent));
            var screen = await run.WaitFor("subagent 2 · no model · thinking:off");
            Check(screen.Contains(" ↑101 ↓22 R3.0k W400 $0.750  /work/project", StringComparison.Ordinal), "stats without a context window:\n" + screen);
            // Over 90% of the context window: the share in the error color.
            var full = Conversation([AssistantEntry(1, "near the limit", Usage(95_000, 10))], $$"""{"pi.agent":{{AgentDoc}}}""");
            run.Source.Publish(View(full));
            await run.WaitFor("95.0%/100k");
            Check(run.Terminal.RawOutput.Contains(Themes.Current.Fg("error", "95.0%/100k"), StringComparison.Ordinal), "share in the error color");
        }),

        Case("tui.transcript.compaction-collapses-and-expands", async () =>
        {
            var view = View([
                Entry(5, "pi.compaction", UserWire("The user asked about **durable** sessions.")),
                UserEntry(6, "continue"),
                Entry(7, "pi.assistant", AssistantWire($"[{ToolCallBlock("call_9", "bash", """{"command":"seq 1 30"}""")}]", "toolUse")),
                Entry(8, "pi.tool-result", ToolResultWire("call_9", "bash", string.Join("\n", Enumerable.Range(1, 30)))),
            ]);
            await using var run = await TuiRun.Start(view, rows: 90);
            var expand = KeybindingHints.KeyText("app.tools.expand");
            var screen = await run.WaitFor($"Earlier context summarized ({expand} to expand)");
            Check(screen.Contains("[compaction]", StringComparison.Ordinal), "label");
            Check(!screen.Contains("durable sessions", StringComparison.Ordinal), "summary collapsed");
            ShowsInOrder(screen, PlainLines(BashCard(expanded: false), 120), "collapsed bash card");
            run.Send("\u000f");
            screen = await run.WaitFor("The user asked about durable sessions.");
            Check(!screen.Contains("Earlier context summarized", StringComparison.Ordinal), "expanded summary replaces the line");
            await run.WaitUntil(text => text.Contains("\n 30", StringComparison.Ordinal) || text.Contains("\n30", StringComparison.Ordinal), "every bash line");
            ShowsInOrder(run.Screen, PlainLines(BashCard(expanded: true), 120).TakeLast(5), "expanded bash card");
            run.Send("\u000f");
            await run.WaitFor("Earlier context summarized");
        }),

        Case("tui.transcript.reset-and-rebuild", async () =>
        {
            var view = View([UserEntry(1, "first question"), AssistantEntry(2, "first answer")]);
            await using var run = await TuiRun.Start(view);
            await run.WaitFor("first answer");
            // A reset appends a marker.
            run.Source.Publish(View([UserEntry(1, "first question"), AssistantEntry(2, "first answer"), Entry(3, "pi.reset")]));
            await run.WaitFor("[new context]");
            // Compaction replaced the head of the active transcript: the transcript renders again from the new entries.
            run.Source.Publish(View([Entry(9, "pi.compaction", UserWire("summary")), UserEntry(10, "after compaction"), AssistantEntry(11, "fresh answer")]));
            var screen = await run.WaitFor("fresh answer");
            Check(!screen.Contains("first question", StringComparison.Ordinal) && !screen.Contains("[new context]", StringComparison.Ordinal),
                "old entries gone:\n" + screen);
            Equal(1, Count(screen, "fresh answer"), "rendered once");
        }),
    ];

    /// <summary>The bash card of tui.transcript.compaction-collapses-and-expands as pi draws it.</summary>
    private static ToolExecutionComponent BashCard(bool expanded)
    {
        var card = new ToolExecutionComponent("bash", "call_9", JsonNode.Parse("""{"command":"seq 1 30"}"""), null,
            BuiltInToolRenderers.CreateAllToolRenderers()["bash"], StandaloneTui(), Cwd);
        card.SetExpanded(expanded);
        card.SetArgsComplete();
        card.UpdateResult((JsonObject)JsonNode.Parse(ToolResultWire("call_9", "bash", string.Join("\n", Enumerable.Range(1, 30))))!);
        return card;
    }
}
