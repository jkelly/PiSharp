// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (navigateTree: the leaf moves
// and the tools are restored; agent.state.model and thinkingLevel are untouched) and packages/coding-agent/src/core/session-manager.ts.
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

// Tree navigation keeps the session's model and thinking level even where the target branch recorded others; nothing is recorded for
// them until a response (or a change) names them.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> NavigationModelCases() =>
    [
        ("navigation.tree-keeps-the-model-and-thinking-level", async () =>
        {
            using var sandbox = new Sandbox("tree-keeps-model");
            Equal(0, (await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "--thinking", "low", "first")).Code, "first run");
            var file = sandbox.SessionFiles().Single();
            Equal(0, (await sandbox.Run("-p", "-c", "--provider", "anthropic", "--model", "claude-haiku-4-5", "--thinking", "high", "second")).Code, "second run");
            var firstAnswer = File.ReadLines(file).Select(line => JsonNode.Parse(line)!)
                .First(entry => entry["type"]?.GetValue<string>() == "message" && entry["message"]?["role"]?.GetValue<string>() == "assistant")["id"]!.GetValue<string>();
            var linesBefore = File.ReadAllLines(file).Length;
            // sdk.ts: --thinking on a continued session sets the level in memory without a thinking_level_change, so the branch still
            // records "low" and only this run's --thinking makes the session's level "high".
            var responses = await RpcDialog(sandbox, ["--mode", "rpc", "-c", "--thinking", "high"], seen => seen.Count switch
            {
                0 => """{"id":"1","type":"get_state"}""",
                1 => """{"id":"2","type":"pisharp_capture_navigation","mode":"tree","generation":""" + seen[0]["data"]!["pisharpGeneration"]!.ToJsonString() + "}",
                2 => """{"id":"3","type":"pisharp_select_navigation","mode":"tree","viewId":""" + seen[1]["data"]!["viewId"]!.ToJsonString() +
                    ""","targetId":""" + JsonValue.Create(firstAnswer)!.ToJsonString() + ""","generation":""" + seen[1]["data"]!["generation"]!.ToJsonString() + "}",
                3 => """{"id":"4","type":"get_state"}""",
                _ => null
            });
            Equal("claude-haiku-4-5", responses[0]["data"]!["model"]!["id"]!.GetValue<string>(), "continued on the last answer's model");
            Equal("high", responses[0]["data"]!["thinkingLevel"]!.GetValue<string>(), "continued thinking level");
            Equal("Selected", responses[2]["data"]!["disposition"]?.GetValue<string>(), "navigated: " + responses[2].ToJsonString());
            Equal("claude-haiku-4-5", responses[3]["data"]!["model"]!["id"]!.GetValue<string>(), "navigation keeps the model");
            Equal("high", responses[3]["data"]!["thinkingLevel"]!.GetValue<string>(), "navigation keeps the thinking level");
            Equal(linesBefore, File.ReadAllLines(file).Length, "navigation records nothing for the kept model and level");
        }),
    ];

    /// <summary>RPC commands chosen one at a time from the responses so far (null ends the input); the responses in order.</summary>
    private static async Task<List<JsonNode>> RpcDialog(Sandbox sandbox, string[] args, Func<IReadOnlyList<JsonNode>, string?> next)
    {
        var input = new ScriptedInput(); var responses = new List<JsonNode>();
        void Advance() { if (next(responses) is { } command) input.Send(command); else input.Complete(); }
        using var output = new LineOutput(line =>
        {
            if (line["type"]?.GetValue<string>() != "response") return;
            lock (responses) { responses.Add(line); Advance(); }
        });
        lock (responses) Advance();
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
        Equal(0, await PiCommand.RunAsync(args, host, CancellationToken.None), "rpc exit; " + stderr);
        return responses;
    }
}
