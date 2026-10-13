using System.Text.RegularExpressions;
using PiSharp.Durable.Examples;

// Milestone M0 (decision 0005): samples/Durable.Examples ports Pi v1.1.0 packages/durable/test/examples 00-05; P8 adds 14-chat and
// 15-system-prompt, with a scripted model in place of pi-ai's faux provider. The printed values are the ones the pinned upstream prints
// under Node (node --conditions=source --experimental-strip-types test/examples/<name>.ts), as JSON in place of util.inspect; message
// timestamps come from the clock, so both sides compare them as <time>. The file backends also reopen each example's storage in a new
// Session and read it back. P9 adds 17-coding-tools, 29-sandbox-per-conversation and 30-tool-override, whose tools run over LocalExecutionEnv
// in temporary directories (Git Bash on Windows); 17's timing of `cat /tmp/1gb.txt` compares as <ms> and its storage directory as <dir>.
// P10 adds 20-inbox, whose held first answer is a ScriptedReply.Held. P11 adds 06-harness through 13-recovery (11's faux provider is
// ScriptedModels; 13 keeps its storage in a temporary SQLite file on memory, as upstream does, and reopens the runner's storage otherwise),
// and 31-reload-and-restart, which restarts on its own storage the same way, so it has no separate read-back.
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
        "== 17-coding-tools",
        "cat /tmp/1gb.txt took <ms> ms",
        "status: done",
        .. CodingToolsTranscript,
        "answer: [{\"type\":\"text\",\"text\":\"The file now greets durable.\"}]",
        "file: \"hello durable\\n\"",
        CodingToolsStorage,
        "== 20-inbox",
        "rejected: Conversation 1 is busy",
        "withdraw: aborted",
        """inbox: ["10 followUp","11 steer","12 write"]""",
        // Upstream prints an empty reason after a settled done.
        "first: done ",
        "follow-up: done ",
        "steer: done ",
        "note: done ",
        "withdrawn: unanswered aborted",
        InboxTranscript,
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
    private const string InboxTranscript = """transcript: ["pi.user","pi.assistant","app.note","pi.user","pi.user","pi.assistant"]""";
    private const string BobsSandbox = "bob's sandbox: from bob";
    private const string PythonConversation = "python conversation: venv: <project>/.venv";
    private const string TimedBashCalls = "timed bash calls: 2";

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
        [BobsSandbox] = ["alice's write: Successfully wrote to note.txt", "bob's write: Successfully wrote to note.txt"],
        [TimedBashCalls] = [PythonConversation],
    };

    private static IEnumerable<(string, Func<Task>)> ExamplesCases()
    {
        yield return Case("examples.memory.upstream-output", async () => Lines(await RunExamplesAsync(ExampleStorage.Memory, null), ExamplesUpstreamOutput));
        foreach (var storage in new[] { ExampleStorage.Jsonl, ExampleStorage.Sqlite })
            yield return Case($"examples.{storage.ToString().ToLowerInvariant()}.reopen", async () =>
            {
                var expected = new List<string>();
                foreach (var line in ExamplesUpstreamOutput)
                {
                    expected.Add(line);
                    if (ExamplesReopened.TryGetValue(line, out var reopened)) { expected.Add("-- reopened"); expected.AddRange(reopened); }
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
        await Examples.RunAsync(storage, root, output, only);
        return output.ToString();
    }

    private static void Lines(string output, IReadOnlyList<string> expected)
    {
        var actual = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Regex.Replace(line.TrimEnd('\r'), "\"timestamp\":[0-9]+", "\"timestamp\":<time>"))
            .Select(line => Regex.Replace(line, "^cat /tmp/1gb.txt took [0-9]+ ms$", "cat /tmp/1gb.txt took <ms> ms"))
            .Select(line => Regex.Replace(line, "^storage: .+$", "storage: <dir>")).ToArray();
        for (var index = 0; index < Math.Max(actual.Length, expected.Count); index++)
            Equal(index < expected.Count ? expected[index] : "<end>", index < actual.Length ? actual[index] : "<end>", "line " + (index + 1));
    }
}
