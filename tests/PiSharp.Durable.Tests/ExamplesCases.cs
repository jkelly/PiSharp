using System.Text.RegularExpressions;
using PiSharp.Durable.Examples;

// Milestone M0 (decision 0005): samples/Durable.Examples ports Pi v1.1.0 packages/durable/test/examples 00-05; P8 adds 14-chat and
// 15-system-prompt, with a scripted model in place of pi-ai's faux provider. The printed values are the ones the pinned upstream prints
// under Node (node --conditions=source --experimental-strip-types test/examples/<name>.ts), as JSON in place of util.inspect; message
// timestamps come from the clock, so both sides compare them as <time>. The file backends also reopen each example's storage in a new
// Session and read it back. P9 adds 17-coding-tools, 29-sandbox-per-conversation and 30-tool-override, whose tools run over LocalExecutionEnv
// in temporary directories (Git Bash on Windows); 17's timing of `cat /tmp/1gb.txt` compares as <ms> and its storage directory as <dir>.
// P10 adds 20-inbox, whose held first answer is a ScriptedReply.Held (its lines are Pi v1.2.0's: the data-only note is appended at once). P12 adds 22-subagent-foreground, whose held child and parent answers
// keep Node's order of the UI attaching to the child and printing; its file backends read back the child conversation and both transcripts. P11 adds 06-harness through 13-recovery (11's faux provider is
// ScriptedModels; 13 keeps its storage in a temporary SQLite file on memory, as upstream does, and reopens the runner's storage otherwise),
// 25-compaction, whose model reports the faux provider's usage estimate (its file backends read back the stored entries and summaries),
// and 31-reload-and-restart, which restarts on its own storage the same way as 13, so it has no separate read-back.
// P13 adds the rest. 16-real-model runs with an environment without OPENAI_API_KEY and prints upstream's skip line. 18-print, 26-coding-agent
// (pwd compares as <workspace>), 27-plan-mode and 28-reviewer use ScriptedModels. 21-late-join, 23-subagent-background and 24-child-tasks run
// on an ExampleClock driven like Node's event loop (24 and 23 restart on their own storage, as 13 does). 19-json prints both modes; its
// lines are upstream's captured output (Fixtures/examples-19-json-*.upstream.jsonl, from one run of each mode under Node) and compare as
// text, key order included, with timestamps, durations, the faux API name and the provider session ID as placeholders: those are random
// or clock values. Both modes compare with Pi v1.2.0's capture (Fixtures/v1.2.0), which carries tool task v2's additions (the snapshot's
// `nestedTools`, the tool events' `taskId`) and read's description naming images, with the token counts it changes (work list area e).
// That capture's run committed the streaming answer's partial message once, because a progress tick (partialIntervalMs, 100 ms) fell
// inside its 200 tokens/s stream, which the v1.1.0 run and PiSharp's paced faux model do not: its ops have one line more (the
// docs.pi.live.generation.message progress commit), and its events' message_start carries the partial message (stopReason "pending").
// The comparison leaves out such ops lines (JsonModeOpsLines checks that the rest has as many lines as the v1.1.0 capture) and expects
// the message_start of a pending message to carry the message its message_end carries, as every completed message_start of both captures
// does (JsonModeEventsLines checks that identity on the capture's other assistant message_start lines).
internal static partial class Program
{
    private static readonly string[] CodingToolsTranscript =
    [
        "pi.user",
        "pi.system",
        "pi.assistant",
        "pi.tool-result read: \"hello world\\n\"",
        "pi.assistant",
        "pi.tool-result edit: \"Successfully replaced 1 block(s) in notes.txt.\"",
        "pi.assistant",
        "pi.tool-result bash: \"hello durable\\n\"",
        "pi.assistant",
        "pi.tool-result bash: \"cat: /tmp/1gb.txt: No such file or directory\\n<harness>\\n[error] Command exited with code 1\\n</harness>\"",
        "pi.assistant",
    ];

    // Declared before ExamplesUpstreamOutput, which spreads them.
    private static readonly string[] JsonModeEvents = JsonModeEventsLines(JsonModeFixture("events", "v1.2.0"));
    private static readonly string[] JsonModeOps = JsonModeOpsLines(JsonModeFixture("ops", "v1.2.0"), JsonModeFixture("ops"));
    private static readonly string[] ReviewerTranscript =
    [
        "> Review user.ts.",
        "reviewer: 1. `name` does not handle a missing user.",
        "> Look again for anything you missed. Say \"No further findings.\" when there is nothing left.",
        "reviewer: 2. `user` has no type. No further findings.",
    ];

    private static readonly string[] ExamplesUpstreamOutput =
    [
        "== 00-conversation",
        "standalone conversation: {\"id\":2}",
        "== 01-documents",
        "latest notes: {\"text\":\"after goodbye\"}",
        "notes at first entry: {\"text\":\"after hello\"}",
        "notes at second entry: {\"text\":\"after goodbye\"}",
        "== 02-forks",
        "fork transcript: [\"hello\"]",
        "fork notes: {\"text\":\"after hello\"}",
        "fork notes after edit: {\"text\":\"changed only in the fork\"}",
        "parent notes after edit: {\"text\":\"after goodbye\"}",
        "== 03-owned-conversations",
        "supervisor task: 3",
        "child conversation: {\"id\":4,\"owner\":{\"conversationId\":2,\"taskId\":3}}",
        "registry: {\"agents\":{\"researcher\":{\"conversationId\":4,\"requestId\":\"researcher:first-message:3\"}}}",
        "== 04-chord-state",
        "Chord notes: hydrate 0 {\"text\":\"first\"}",
        "Chord notes: update 1 {\"text\":\"published through Chord\"}",
        "== 05-watches",
        "watch baseline: {\"text\":\"first\"}",
        "watch update: {\"text\":\"observed asynchronously\"}",
        "== 06-harness",
        "root: 1 {\"text\":\"root notes\"}",
        "stored agent: {\"thinkingLevel\":\"low\"}",
        HarnessResolved,
        "== 07-configuration",
        """default tools: ["read","write","grep"]""",
        ConfigurationStored,
        """model: {"provider":"anthropic","modelId":"claude-sonnet-4-5"} thinking: high tools: ["write","read"]""",
        """without search: ["read","write"]""",
        """files uninstalled: ["grep"]""",
        ConfigurationReinstalled,
        "== 08-harness-conversations",
        "typed entry: true example",
        "helper thinking: minimal",
        "fork thinking: high",
        "lookup: true",
        "== 09-context",
        """raw active entries: ["message","message","message","message","pi.system","message","message","edit","note"]""",
        """request messages: ["user: read files a and b","assistant: reading call(a) call(b)","result(a)","result(b)","system: {\"cwd\":\"<cwd>/repo</cwd>\"}","assistant: a and b look fine"]""",
        """fork messages: ["user: read a and b","assistant: reading call(a) call(b)","result(a) error","result(b) error"]""",
        """after summary: summary ["user: Summary: a and b are fine."]""",
        """newest stored entries: ["summary","note","edit"] more: true""",
        "== 10-registry-reload",
        """after reload: ["read: Read a file","grep: Search files, faster"]""",
        """with audit: ["read: Read a file (audited)","grep: Search files, faster"]""",
        """after uninstall: ["read: Read a file","grep: Search files, faster"]""",
        "== 11-extension-state",
        ExtensionStateTodos,
        ExtensionStateSystem,
        "== 12-tasks",
        PaymentOutcome,
        "== 13-recovery",
        "tick 1",
        "tick 2",
        """closed; saved checkpoint: {"status":"pending","checkpoint":{"phase":"tick","n":2}} memos: {"printed-1":true,"printed-2":true}""",
        "tick 3",
        "tick 4",
        "tick 5",
        """after reopen: {"status":"completed","result":"counted to 5"}""",
        "== 14-chat",
        ChatAnswer,
        ChatTranscript,
        "== 15-system-prompt",
        "root system prompt: [" + RootPrompt + "]",
        """subagent system prompt: [{"role":"system","content":"","sections":{"preamble":"You are a coding agent. Be terse.","cwd":"<cwd>\n/repo\n</cwd>","instructions":"<instructions>\nOnly read; never edit files.\n</instructions>"},"timestamp":<time>}]""",
        RootAfterCwdChange,
        "== 16-real-model",
        // The tests run the examples with an environment that has no OPENAI_API_KEY.
        "skipped: OPENAI_API_KEY is not set",
        "== 17-coding-tools",
        "cat /tmp/1gb.txt took <ms> ms",
        "status: done",
        .. CodingToolsTranscript,
        "answer: [{\"type\":\"text\",\"text\":\"The file now greets durable.\"}]",
        "file: \"hello durable\\n\"",
        CodingToolsStorage,
        "== 18-print",
        PrintAnswer,
        "== 19-json",
        "mode: events",
        .. JsonModeEvents,
        "mode: ops",
        .. JsonModeOps,
        "== 20-inbox",
        "rejected: Conversation 1 is busy",
        "withdraw: aborted",
        // Pi v1.2.0 (#10407): the data-only note is appended at once, not queued.
        """inbox: ["10 followUp","11 steer"]""",
        // Upstream prints an empty reason after a settled done.
        "first: done ",
        "follow-up: done ",
        "steer: done ",
        "note: done ",
        "withdrawn: unanswered aborted",
        InboxTranscript,
        "== 21-late-join",
        // Upstream's util.inspect prints [ 'pi.user', 'pi.system', 'pi.assistant' ] and [ 'count running' ].
        """view entries: ["pi.user","pi.system","pi.assistant"]""",
        @"view tool slot: running ""1\n2\n3\n4\n5\n""",
        @"view output now: ""1\n2\n3\n4\n5\n""",
        """snapshot tools: ["count running"]""",
        .. LateJoinOutput(6, "view output now: ", "event output now: "),
        "event: tool_execution_end",
        "event: message_start",
        "event: message_end",
        "event: turn_end",
        "event: turn_start",
        "event: message_start",
        "event text delta: \"en, and this ans\"",
        "event text delta: \"wer streams slow\"",
        "event: message_end",
        "event: turn_end",
        "event: run_end",
        "event: submission",
        LateJoinLastLine,
        "== 22-subagent-foreground",
        "tool subagent({\"task\":\"Name three prime numbers.\"})",
        "  assistant: 2, 3, and 5.",
        SubagentAnswer,
        "== 23-subagent-background",
        // Upstream prints a blank line before each message and the restart; the comparison skips blank lines.
        "> Start a subagent named reader that summarizes Moby Dick.",
        "  subagent spawn reader \"Summarize the plot of Moby Dick.\"",
        "  → Started reader.",
        "OK. Started reader.",
        "> reader: A whale, a captain, an obsession.",
        "Noted.",
        "> Ask reader to summarize every chapter.",
        "  subagent send reader \"Now go through all chapters in detail.\"",
        "  → Sent to reader.",
        "OK. Sent to reader.",
        "> Stop reader.",
        "  subagent stop reader",
        "  → Stopped reader.",
        "OK. Stopped reader.",
        "> What are my subagents doing?",
        "  subagent status",
        "  → reader: idle",
        "OK. reader: idle",
        "> Ask reader for the whale's name.",
        "  subagent send reader \"What is the whale called?\"",
        "  → Sent to reader.",
        "  (process restarts)",
        "OK. Sent to reader.",
        "> reader: Moby Dick.",
        "Noted.",
        "== 24-child-tasks",
        "One card is declined:",
        "  payment visa-1 aborted, refunded",
        "  payment visa-3 aborted, refunded",
        "  payment visa-4 aborted, refunded",
        "  payments: aborted, failed, aborted, aborted",
        "  checkout: failed",
        "The customer cancels:",
        "  example.checkout 12: waiting on 13, 14, 15, 16",
        "    example.payment 13: running",
        "    example.payment 14: running",
        "    example.payment 15: running",
        "    example.payment 16: running",
        "  payment visa-5 aborted, refunded",
        "  payment visa-6 aborted, refunded",
        "  payment visa-7 aborted, refunded",
        "  payment visa-8 aborted, refunded",
        "  checkout aborted",
        "  checkout: aborted",
        "The process stops while the payments run, and a new one continues:",
        "  payments: completed, completed, completed, completed",
        "  checkout: completed",
        "== 25-compaction",
        "after \"Where should we stay?\" (answered): 2 messages in context, 2 entries stored",
        "  user      Where should we stay?",
        "  assistant A detailed answer to \"Where should we stay?\": details details details ",
        "after \"What should we eat?\" (answered): 4 messages in context, 4 entries stored",
        "  user      Where should we stay?",
        "  assistant A detailed answer to \"Where should we stay?\": details details details ",
        "  user      What should we eat?",
        "  assistant A detailed answer to \"What should we eat?\": details details details de",
        "after \"Which day trips?\" (answered): 4 messages in context, 7 entries stored",
        "  (threshold compaction summary first)",
        "  user      The conversation history before this point was compacted into the foll",
        "  assistant A detailed answer to \"What should we eat?\": details details details de",
        "  user      Which day trips?",
        "  assistant A detailed answer to \"Which day trips?\": details details details detai",
        "after \"Any museums?\" (answered): 7 messages in context, 10 entries stored",
        "  (threshold compaction summary first)",
        "  user      The conversation history before this point was compacted into the foll",
        "  assistant A detailed answer to \"What should we eat?\": details details details de",
        "  user      Which day trips?",
        "  assistant A detailed answer to \"Which day trips?\": details details details detai",
        "  user      Any museums?",
        "  system    (system prompt)",
        "  assistant A detailed answer to \"Any museums?\": details details details details d",
        "after \"Nightlife?\" (answered): 5 messages in context, 14 entries stored",
        "  (threshold compaction summary first)",
        "  user      The conversation history before this point was compacted into the foll",
        "  assistant A detailed answer to \"Any museums?\": details details details details d",
        "  user      Nightlife?",
        "  system    (system prompt)",
        "  assistant A detailed answer to \"Nightlife?\": details details details details det",
        "manual compaction finished; its summary is queued",
        "after the answer, the summary is done",
        "after compact(): 4 messages in context, 17 entries stored",
        "  (manual compaction summary first)",
        "  user      The conversation history before this point was compacted into the foll",
        "  assistant A detailed answer to \"Nightlife?\": details details details details det",
        "  user      How do we get around?",
        "  assistant A detailed answer to \"How do we get around?\": details details details ",
        "after \"What should we pack?\" (answered): 7 messages in context, 20 entries stored",
        "  (manual compaction summary first)",
        "  user      The conversation history before this point was compacted into the foll",
        "  assistant A detailed answer to \"Nightlife?\": details details details details det",
        "  user      How do we get around?",
        "  assistant A detailed answer to \"How do we get around?\": details details details ",
        "  user      What should we pack?",
        "  system    (system prompt)",
        "  assistant A detailed answer to \"What should we pack?\": details details details d",
        "after \"Summarize the plan for my partner\" (model_error): 4 messages in context, 24 entries stored",
        "  (threshold compaction summary first)",
        "  user      The conversation history before this point was compacted into the foll",
        "  assistant A detailed answer to \"What should we pack?\": details details details d",
        "  user      Summarize the plan for my partner",
        CompactionLastLine,
        "== 26-coding-agent",
        // pwd prints the temporary workspace; both sides compare it as <workspace>.
        "bash ran in: <workspace>",
        CodingAgentLastLine,
        "== 27-plan-mode",
        """tools: ["read","write","edit","bash"]""",
        """plan mode tools: ["read","submit_plan"]""",
        PlanSteps,
        """tools again: ["read","write","edit","bash"]""",
        // Upstream's util.inspect prints `removed: undefined`; JSON drops the undefined field.
        """system: {"sections":{"plan_mode":"<plan_mode>\nYou are in plan mode. Read the code, then call submit_plan. Change nothing.\n</plan_mode>"},"added":["read","submit_plan"]}""",
        PlanModeLastLine,
        "== 28-reviewer",
        """reviewer: small ["coding-tools","reviewer"] ["read"] true""",
        .. ReviewerTranscript,
        "== 29-sandbox-per-conversation",
        "alice's sandbox: from alice",
        BobsSandbox,
        "== 30-tool-override",
        "plain conversation: venv:",
        PythonConversation,
        TimedBashCalls,
        "== 31-reload-and-restart",
        "call running during the reload: v1",
        "next call: v2",
        "after restart, before install: []",
        // Upstream's util.inspect prints [ 'version' ].
        "after install: [\"version\"]",
    ];

    private const string HarnessResolved = """resolved: low ["files"] ["read"]""";
    private const string ConfigurationStored =
        """stored: {"model":{"provider":"anthropic","modelId":"claude-sonnet-4-5"},"thinkingLevel":"high","tools":["write","read"]}""";
    private const string ConfigurationReinstalled = """files reinstalled: ["read","write","grep"]""";
    private const string ExtensionStateTodos = """todos: {"items":["fix the build"]}""";
    private const string ExtensionStateSystem =
        """system messages: [{"toolsAdded":["todo"]},{"sections":{"todos":"<todos>\nfix the build\n</todos>"}}]""";
    private const string PaymentOutcome = """payment outcome: {"status":"completed","result":{"receipt":500}}""";
    private const string CodingToolsStorage = "storage: <dir>";
    private const string SubagentAnswer = "assistant: The subagent says: 2, 3, and 5.";
    private const string InboxTranscript = """transcript: ["pi.user","app.note","pi.assistant","pi.user","pi.user","pi.assistant"]""";
    // Repeated within 25-compaction's output; the read-back follows its last occurrence, the example's last line.
    private const string CompactionLastLine = "  system    (system prompt)";
    private const string BobsSandbox = "bob's sandbox: from bob";
    private const string PythonConversation = "python conversation: venv: <project>/.venv";
    private const string TimedBashCalls = "timed bash calls: 2";

    private static string[] JsonModeFixture(string mode, string? version = null) => File.ReadAllLines(Path.Combine(
        [RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", .. version is null ? Array.Empty<string>() : [version],
            $"examples-19-json-{mode}.upstream.jsonl"]));

    /// <summary>The v1.2.0 events of 19 with a pending message_start carrying its final message (see the file header).</summary>
    private static string[] JsonModeEventsLines(string[] v120)
    {
        var lines = v120.ToArray();
        for (var index = 0; index < lines.Length; index++)
        {
            using var start = System.Text.Json.JsonDocument.Parse(lines[index]);
            if (start.RootElement.GetProperty("type").GetString() != "message_start" ||
                !start.RootElement.GetProperty("message").TryGetProperty("stopReason", out var stopReason)) continue;
            var end = lines.Skip(index + 1).First(line => line.StartsWith("{\"type\":\"message_end\",", StringComparison.Ordinal));
            using var endDocument = System.Text.Json.JsonDocument.Parse(end);
            var final = "{\"type\":\"message_start\",\"message\":" + endDocument.RootElement.GetProperty("entry").GetProperty("model")[0].GetRawText() + "}";
            if (stopReason.GetString() == "pending") lines[index] = final;
            else Equal(lines[index], final, "example 19 events: a completed message_start carries its message_end's message");
        }
        return lines;
    }

    /// <summary>The v1.2.0 ops of 19 without the progress commits of a partial streaming message (see the file header); what remains has as many
    /// lines as the v1.1.0 capture, which had none.</summary>
    private static string[] JsonModeOpsLines(string[] v120, string[] v110)
    {
        static bool StreamingProgress(string line)
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("ops", out var ops) && ops.EnumerateArray().All(op =>
                op[0].GetString() == "s" && op[1].GetRawText() == "[\"docs\",\"pi.live\",\"generation\",\"message\"]");
        }
        var lines = v120.Where(line => !StreamingProgress(line)).ToArray();
        Equal(v110.Length, lines.Length, "example 19 ops: lines apart from streaming progress");
        return lines;
    }

    private const string LateJoinLastLine = "event: usage_changed";

    // The output lines 21-late-join prints once the client joined: each committed line, first from the view, then from the event stream.
    private static IEnumerable<string> LateJoinOutput(int from, string view, string events)
    {
        for (var n = from; n <= 10; n++)
        {
            var text = string.Concat(Enumerable.Range(1, n).Select(line => line + @"\n"));
            yield return $"{view}\"{text}\"";
            yield return $"{events}\"{text}\"";
        }
    }

    private const string PrintAnswer = "This directory holds the durable package sources, tests, and docs.";
    private const string CodingAgentLastLine = "bash ran in: <workspace>/app";
    private const string PlanSteps = """plan: ["Read PORT from the environment","Default to 3000"]""";
    private const string PlanModeLastLine = """system: {"sections":{"plan_mode":null},"added":["write","edit","bash"],"removed":["submit_plan"]}""";

    private const string ChatAnswer = """answer: [{"type":"text","text":"Paris."}]""";
    private const string ChatTranscript = """transcript: ["pi.user","pi.system","pi.assistant"]""";
    private const string RootPrompt =
        """{"role":"system","content":"","sections":{"preamble":"You are a coding agent. Be terse.","cwd":"<cwd>\n/repo\n</cwd>","agents_md":"<agents_md>\nRun npm run check after changes.\n</agents_md>"},"timestamp":<time>}""";
    private const string RootAfterCwdChange = "root system entries after cwd change: [" + RootPrompt +
        """,{"role":"system","content":"","sections":{"cwd":"<cwd>\n/repo/packages\n</cwd>"},"timestamp":<time>}]""";

    // What each example reads back after reopen, keyed by the example's last upstream line.
    private static readonly Dictionary<string, string[]> ExamplesReopened = new(StringComparer.Ordinal)
    {
        ["standalone conversation: {\"id\":2}"] = ["standalone conversation: {\"id\":2}"],
        ["notes at second entry: {\"text\":\"after goodbye\"}"] =
        [
            "latest notes: {\"text\":\"after goodbye\"}",
            "notes at first entry: {\"text\":\"after hello\"}",
            "notes at second entry: {\"text\":\"after goodbye\"}",
        ],
        ["parent notes after edit: {\"text\":\"after goodbye\"}"] =
        [
            "fork transcript: [\"hello\"]",
            "fork notes after edit: {\"text\":\"changed only in the fork\"}",
            "parent notes after edit: {\"text\":\"after goodbye\"}",
        ],
        ["registry: {\"agents\":{\"researcher\":{\"conversationId\":4,\"requestId\":\"researcher:first-message:3\"}}}"] =
        [
            "supervisor task: 3 example.supervisor",
            "child conversation: {\"id\":4,\"owner\":{\"conversationId\":2,\"taskId\":3}}",
            "registry: {\"agents\":{\"researcher\":{\"conversationId\":4,\"requestId\":\"researcher:first-message:3\"}}}",
        ],
        ["Chord notes: update 1 {\"text\":\"published through Chord\"}"] = ["Chord notes: hydrate 0 {\"text\":\"published through Chord\"}"],
        ["watch update: {\"text\":\"observed asynchronously\"}"] = ["watch baseline: {\"text\":\"observed asynchronously\"}"],
        [HarnessResolved] = ["root: 1 {\"text\":\"root notes\"}", "stored agent: {\"thinkingLevel\":\"low\"}"],
        // The last configure call selected exactly files and search, and cleared the tool selection.
        [ConfigurationReinstalled] =
            ["""stored: {"model":{"provider":"anthropic","modelId":"claude-sonnet-4-5"},"thinkingLevel":"high","extensions":["files","search"]}"""],
        [ExtensionStateSystem] = [ExtensionStateTodos],
        [PaymentOutcome] = [PaymentOutcome],
        [ChatTranscript] = [ChatTranscript, ChatAnswer],
        [RootAfterCwdChange] = [RootAfterCwdChange],
        [CodingToolsStorage] = CodingToolsTranscript,
        [InboxTranscript] = [InboxTranscript, "inbox: []"],
        [SubagentAnswer] =
        [
            """child conversation: {"id":13,"owner":{"conversationId":1,"taskId":12}}""",
            """parent transcript: ["pi.user","pi.system","pi.assistant","pi.tool-result","pi.assistant"]""",
            """child transcript: ["pi.user","pi.assistant"]""",
        ],
        [CompactionLastLine] = ["24 entries stored, summaries: [\"threshold\",\"threshold\",\"manual\",\"threshold\"]"],
        [BobsSandbox] = ["alice's write: Successfully wrote to note.txt", "bob's write: Successfully wrote to note.txt"],
        [TimedBashCalls] = [PythonConversation],
        [PrintAnswer] = [PrintAnswer],
        [JsonModeOps[^1]] = ["""transcript: ["pi.user","pi.system","pi.assistant","pi.tool-result","pi.assistant"]"""],
        [LateJoinLastLine] = ["""transcript: ["pi.user","pi.system","pi.assistant","pi.tool-result","pi.assistant"]"""],
        [CodingAgentLastLine] = ["bash ran in: <workspace>", CodingAgentLastLine],
        [PlanModeLastLine] = [PlanSteps],
        [ReviewerTranscript[^1]] = ReviewerTranscript,
    };

    private static IEnumerable<(string, Func<Task>)> ExamplesCases()
    {
        yield return Case("examples.memory.upstream-output", async () => Lines(await RunExamplesAsync(ExampleStorage.Memory, null), ExamplesUpstreamOutput));
        foreach (var storage in new[] { ExampleStorage.Jsonl, ExampleStorage.Sqlite })
            yield return Case($"examples.{storage.ToString().ToLowerInvariant()}.reopen", async () =>
            {
                var expected = new List<string>();
                for (var index = 0; index < ExamplesUpstreamOutput.Length; index++)
                {
                    var line = ExamplesUpstreamOutput[index];
                    expected.Add(line);
                    // An example reads back after its last line.
                    var last = index + 1 == ExamplesUpstreamOutput.Length || ExamplesUpstreamOutput[index + 1].StartsWith("== ", StringComparison.Ordinal);
                    if (last && ExamplesReopened.TryGetValue(line, out var reopened)) { expected.Add("-- reopened"); expected.AddRange(reopened); }
                }
                Lines(await RunExamplesAsync(storage, Temp("examples-" + storage)), expected);
            });
        yield return Case("examples.select-by-number", async () =>
        {
            Lines(await RunExamplesAsync(ExampleStorage.Memory, null, "02"), ExamplesUpstreamOutput[6..11]);
            await ThrowsAsync<ArgumentException>(() => RunExamplesAsync(ExampleStorage.Memory, null, "99"), "unknown example");
        });
    }

    private static async Task<string> RunExamplesAsync(ExampleStorage storage, string? root, params string[] only)
    {
        var output = new StringWriter();
        await Examples.RunAsync(storage, root, output, only, environment: _ => null);
        return output.ToString();
    }

    private static void Lines(string output, IReadOnlyList<string> expected)
    {
        var actual = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Normalize).ToArray();
        for (var index = 0; index < Math.Max(actual.Length, expected.Count); index++)
            Equal(index < expected.Count ? Normalize(expected[index]) : "<end>", index < actual.Length ? actual[index] : "<end>", "line " + (index + 1));
    }

    // Clock and random values as placeholders. Expected lines go through the same steps, which leave the placeholders already in them alone.
    private static string Normalize(string line)
    {
        line = line.TrimEnd('\r');
        line = Regex.Replace(line, "\"timestamp\":[0-9]+", "\"timestamp\":<time>");
        line = Regex.Replace(line, "\"durationMs\":[0-9]+", "\"durationMs\":<ms>");
        line = Regex.Replace(line, "\"faux:[0-9]+:[a-z0-9]+\"", "\"faux:<api>\"");
        line = Regex.Replace(line, "\"sessionId\":\"[^\"]+\"", "\"sessionId\":\"<session>\"");
        line = Regex.Replace(line, "^cat /tmp/1gb.txt took [0-9]+ ms$", "cat /tmp/1gb.txt took <ms> ms");
        line = Regex.Replace(line, "^storage: .+$", "storage: <dir>");
        return Regex.Replace(line, "^bash ran in: .*/pi-durable-agent-[A-Za-z0-9]+", "bash ran in: <workspace>");
    }
}
