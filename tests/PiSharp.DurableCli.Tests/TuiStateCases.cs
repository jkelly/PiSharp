// tui.ts's pure helpers (contextTokens, userText, totalUsage, describeTask) and runtime.ts's agentOf, as DurableTuiState ports them.
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Cli.Durable;
using PiSharp.Durable;
using static TuiTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> TuiStateCases() =>
    [
        Case("tui.state.context-tokens", () =>
        {
            Equal<double?>(null, DurableTuiState.ContextTokens([UserEntry(1, "hi")]), "unknown before an answer");
            Equal<double?>(1540, DurableTuiState.ContextTokens([UserEntry(1, "hi"), AssistantEntry(2, "a", Usage(1500, 40))]), "totalTokens");
            // Kept entries follow the summary but are older than it; aborted and failed answers do not measure the context.
            var compacted = new List<EntryRecord>
            {
                Entry(5, "pi.compaction", UserWire("summary")),
                AssistantEntry(3, "kept", Usage(90_000, 10)),
                UserEntry(6, "next"),
                Entry(7, "pi.assistant", AssistantWire($"[{TextBlock("cut")}]", "aborted", Usage(10, 10))),
                Entry(8, "pi.assistant", AssistantWire($"[{TextBlock("failed")}]", "error", Usage(10, 10))),
            };
            Equal<double?>(null, DurableTuiState.ContextTokens(compacted), "unknown after compaction until an answer");
            compacted.Add(Entry(9, "pi.assistant", AssistantWire($"[{TextBlock("ok")}]", usage:
                """{"input":10,"output":5,"cacheRead":2,"cacheWrite":1,"totalTokens":0,"cost":{"total":0}}""")));
            Equal<double?>(18, DurableTuiState.ContextTokens(compacted), "the sum when totalTokens is 0");
        }),

        Case("tui.state.user-text-and-usage", () =>
        {
            Equal("plain", DurableTuiState.UserText(JsonValue.Create("plain")), "string content");
            Equal("ab", DurableTuiState.UserText(JsonNode.Parse("""[{"type":"text","text":"a"},{"type":"image","data":"x"},{"type":"text","text":"b"}]""")),
                "text blocks joined");
            var usage = DurableTuiState.TotalUsage((JsonObject)JsonNode.Parse("""{"models":{"a/b":""" + Usage(1, 2, 3, 4, 0.5) + """},"tools":{"bash":""" +
                Usage(10, 20, 30, 40, 0.25) + "}}")!);
            Equal((11.0, 22.0, 33.0, 44.0, 0.75), usage, "summed buckets");
        }),

        Case("tui.state.agent-of", () =>
        {
            var view = Conversation([], $$"""{"pi.agent":{{AgentDoc}}}""");
            Equal(new ModelRef("faux", "faux-1"), DurableTuiState.AgentOf(view).Model, "model");
            Equal("medium", DurableTuiState.AgentOf(view).ThinkingLevel, "thinking level");
            Equal<(ModelRef?, string?)>((null, null), DurableTuiState.AgentOf(Conversation([])), "no pi.agent");
        }),

        Case("tui.state.describe-task", () =>
        {
            Equal("pi.run #3: waiting on 4, 5", DurableTuiState.DescribeTask(Node(3, "pi.run", RootId,
                State(DurableTaskStatus.Waiting, "tools", ImmutableArray.Create(new TaskId(4), new TaskId(5)), JoinPolicy.AllSettled))), "waiting");
            Equal("pi.tool #4: completing (aborted) [aborting] owns conversation 2, 3", DurableTuiState.DescribeTask(Node(4, "pi.tool", RootId,
                State(DurableTaskStatus.Completing, null, outcome: TaskOutcomeStatus.Aborted), abortRequested: true, conversations: [2, 3])), "completing");
            Equal("bg #9: running poll [background]", DurableTuiState.DescribeTask(Node(9, "bg", RootId, State(DurableTaskStatus.Running, "poll"),
                background: true)), "running");
            // Upstream interpolates an absent phase as JavaScript does.
            Equal("pi.compaction #5: pending undefined", DurableTuiState.DescribeTask(Node(5, "pi.compaction", RootId, State(DurableTaskStatus.Pending, null))),
                "absent phase");
        }),
    ];
}
