// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (autoCompactionEnabled and
// setAutoCompactionEnabled over settingsManager compaction.enabled; _checkCompaction reads getCompactionSettings(this.model) and the
// model's contextWindow when it checks), packages/coding-agent/src/core/settings-manager.ts (getCompactionEnabled: enabled ?? true)
// and packages/coding-agent/src/modes/rpc/rpc-mode.ts (get_state autoCompactionEnabled, set_auto_compaction).
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

// Every mode's session (RPC, print and JSON as well as interactive) starts with auto-compaction set from compaction.enabled, and the
// threshold follows the model the session runs. Expectations were written from the pinned sources by reading.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> AutoCompactionCases() =>
    [
        ("auto-compaction.rpc-session-starts-with-the-setting-and-keeps-it-across-sessions", async () =>
        {
            using var sandbox = new Sandbox("auto-compaction-setting");
            var responses = await RpcSequence(sandbox, ["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5"],
                """{"id":"1","type":"get_state"}""",
                """{"id":"2","type":"new_session"}""",
                """{"id":"3","type":"get_state"}""",
                """{"id":"4","type":"set_auto_compaction","enabled":false}""",
                """{"id":"5","type":"get_state"}""",
                """{"id":"6","type":"new_session"}""",
                """{"id":"7","type":"get_state"}""",
                """{"id":"8","type":"set_auto_compaction","enabled":true}""",
                """{"id":"9","type":"get_state"}""");
            Check(responses.All(response => response["success"]!.GetValue<bool>()), "every command succeeds: " + string.Join("\n", responses.Select(r => r.ToJsonString())));
            foreach (var (index, expected, what) in new[] { (0, true, "startup (compaction.enabled defaults to true)"), (2, true, "a new session"),
                (4, false, "after set_auto_compaction false"), (6, false, "a new session after the toggle"), (8, true, "after set_auto_compaction true") })
                Equal(expected, responses[index]["data"]!["autoCompactionEnabled"]!.GetValue<bool>(), what);
            sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"compaction":{"enabled":false}}""");
            responses = await RpcSequence(sandbox, ["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5"], """{"id":"1","type":"get_state"}""");
            Equal(false, responses[0]["data"]!["autoCompactionEnabled"]!.GetValue<bool>(), "compaction.enabled false");
        }),
        // agent-session.ts setSteeringMode/setFollowUpMode/setAutoCompactionEnabled/setAutoRetryEnabled save the global settings
        // (settings-manager.ts: globalSettings.<field>, markModified, save), so RPC changes survive the process; other fields stay.
        ("auto-compaction.rpc-setters-save-the-global-settings", async () =>
        {
            using var sandbox = new Sandbox("rpc-setters-persist");
            var settingsPath = Path.Combine(sandbox.AgentDir, "settings.json");
            sandbox.Write(settingsPath, """{"theme":"light","compaction":{"keepRecentTokens":5}}""");
            var responses = await RpcSequence(sandbox, ["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5"],
                """{"id":"1","type":"set_auto_compaction","enabled":false}""",
                """{"id":"2","type":"set_auto_retry","enabled":false}""",
                """{"id":"3","type":"set_steering_mode","mode":"all"}""",
                """{"id":"4","type":"set_follow_up_mode","mode":"all"}""");
            Check(responses.All(response => response["success"]!.GetValue<bool>()), "every command succeeds: " + string.Join("\n", responses.Select(r => r.ToJsonString())));
            var saved = JsonNode.Parse(File.ReadAllText(settingsPath))!;
            Equal(false, saved["compaction"]!["enabled"]!.GetValue<bool>(), "compaction.enabled saved");
            Equal(5, saved["compaction"]!["keepRecentTokens"]!.GetValue<int>(), "other compaction fields kept");
            Equal(false, saved["retry"]!["enabled"]!.GetValue<bool>(), "retry.enabled saved");
            Equal("all", saved["steeringMode"]!.GetValue<string>(), "steeringMode saved");
            Equal("all", saved["followUpMode"]!.GetValue<string>(), "followUpMode saved");
            Equal("light", saved["theme"]!.GetValue<string>(), "unrelated settings kept");
            responses = await RpcSequence(sandbox, ["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5"], """{"id":"1","type":"get_state"}""");
            Equal(false, responses[0]["data"]!["autoCompactionEnabled"]!.GetValue<bool>(), "the next process reads the saved setting");
            Equal("all", responses[0]["data"]!["steeringMode"]!.GetValue<string>(), "the next process reads the saved steering mode");
        }),
        ("auto-compaction.rpc-threshold-follows-the-current-model-window", async () =>
        {
            using var sandbox = new Sandbox("auto-compaction-window");
            // keepRecentTokens 1: the first turn is summarized, as the interactive compaction cases do.
            sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"compaction":{"keepRecentTokens":1}}""");
            // 190,000 context tokens: below claude-sonnet-4-5's threshold (1,000,000 - 16,384), above claude-haiku-4-5's (200,000 - 16,384).
            sandbox.Respond = (seen, _) => seen.Body?.Contains("<conversation>", StringComparison.Ordinal) == true
                ? AnthropicUsageText("## Goal\nsummary", 10) : AnthropicUsageText("ok", 190_000);
            var events = new List<JsonNode>();
            var responses = await RpcSettled(sandbox, ["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5"], events,
                """{"id":"1","type":"prompt","message":"on sonnet"}""",
                """{"id":"2","type":"get_state"}""",
                """{"id":"3","type":"set_model","provider":"anthropic","modelId":"claude-haiku-4-5"}""",
                """{"id":"4","type":"prompt","message":"on haiku"}""",
                """{"id":"5","type":"get_state"}""");
            Check(responses.All(response => response["success"]!.GetValue<bool>()), "every command succeeds: " + string.Join("\n", responses.Select(r => r.ToJsonString())));
            var starts = events.Select((record, index) => (record, index)).Where(item => item.record["type"]?.GetValue<string>() == "compaction_start").ToList();
            Equal(1, starts.Count, "one automatic compaction: " + string.Join(",", events.Select(record => record["type"]?.GetValue<string>())));
            Equal("threshold", starts[0].record["reason"]!.GetValue<string>(), "threshold compaction");
            var settled = events.Select((record, index) => (record, index)).Where(item => item.record["type"]?.GetValue<string>() == "agent_settled").Select(item => item.index).ToList();
            Check(settled.Count == 2 && starts[0].index > settled[0] && starts[0].index < settled[1], "the compaction followed the turn on haiku, not the one on sonnet");
            var file = responses[4]["data"]!["sessionFile"]!.GetValue<string>();
            Check(File.ReadLines(file).Any(line => line.Contains("\"type\":\"compaction\"", StringComparison.Ordinal)), "compaction recorded");
            Equal("claude-haiku-4-5", sandbox.Requests[^1].Json.GetProperty("model").GetString(), "the summary ran on the current model");
        }),
        // print mode runs with auto-compaction on (compaction.enabled default): _checkCompaction treats "prompt is too long" as an
        // overflow, omits the failed attempt (_omitRecoveryAttempt: a context_edit) and finds nothing to compact (prepareCompaction:
        // one user message), so print-mode.ts sees no failed assistant message as the last one and exits 0 without printing.
        ("auto-compaction.print-overflow-omits-the-failed-attempt", async () =>
        {
            using var sandbox = new Sandbox("auto-compaction-print-overflow");
            sandbox.Vars["PI_OFFLINE"] = "1";
            sandbox.Respond = (_, _) => AnthropicError(400, "prompt is too long");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"retry":{"enabled":false}}""");
            var (code, stdout, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "x");
            Equal(0, code, "exit; " + stderr);
            Equal("", stdout, "nothing on stdout");
            Equal("", stderr, "nothing on stderr");
            Equal(1, sandbox.Requests.Count, "no retry");
            var entries = File.ReadLines(sandbox.SessionFiles().Single()).Skip(1).Select(line => JsonNode.Parse(line)!).ToList();
            var edit = entries.Single(entry => entry["type"]!.GetValue<string>() == "context_edit");
            var failed = entries.Single(entry => Kind(entry) == "message:assistant");
            Equal(failed["id"]!.GetValue<string>(), edit["targetId"]!.GetValue<string>(), "the failed attempt is omitted");
            Check(!entries.Any(entry => entry["type"]!.GetValue<string>() == "compaction"), "nothing compacted");
        }),
    ];

    /// <summary>An Anthropic Messages stream answering with one text block and <paramref name="input"/> input tokens.</summary>
    private static HttpResponseMessage AnthropicUsageText(string text, int input) => Sse(
        Frame("message_start", new { type = "message_start", message = new { id = "msg_u", role = "assistant", model = "claude-sonnet-4-5", content = Array.Empty<object>(), usage = new { input_tokens = input, output_tokens = 0 } } })
        + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })
        + Frame("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } })
        + Frame("content_block_stop", new { type = "content_block_stop", index = 0 })
        + Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 2 } })
        + Frame("message_stop", new { type = "message_stop" }));

    /// <summary>RPC commands one at a time; after a prompt's response the next command waits for that run's agent_settled (automatic
    /// compaction runs before it).</summary>
    private static async Task<List<JsonNode>> RpcSettled(Sandbox sandbox, string[] args, List<JsonNode> events, params string[] commands)
    {
        var input = new ScriptedInput(); var responses = new List<JsonNode>();
        bool answered = false, settled = false;
        void Next() { if (responses.Count < commands.Length) input.Send(commands[responses.Count]); else input.Complete(); }
        using var output = new LineOutput(line =>
        {
            lock (responses)
            {
                if (line["type"]?.GetValue<string>() != "response")
                {
                    events.Add(line);
                    if (line["type"]?.GetValue<string>() == "agent_settled") { settled = true; if (answered) { answered = settled = false; Next(); } }
                    return;
                }
                responses.Add(line);
                if (line["command"]?.GetValue<string>() == "prompt" && line["success"]?.GetValue<bool>() == true)
                { answered = true; if (settled) { answered = settled = false; Next(); } }
                else { settled = false; Next(); }
            }
        });
        lock (responses) Next();
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
        Equal(0, await PiCommand.RunAsync(args, host, CancellationToken.None), "rpc exit; " + stderr);
        return responses;
    }
}
