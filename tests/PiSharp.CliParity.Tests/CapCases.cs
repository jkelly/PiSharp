// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/index.ts and packages/mcp/src/client.ts.
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Pi;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;

// Counts and sizes upstream Pi does not cap (owner decision 0004, "follow what Pi does"): agent.ts holds every tool, subscriber and
// queued message; extensions/mcp connects every configured server and registers every tool it lists (packages/mcp/src/client.ts reads
// up to MAX_LIST_PAGES = 1,000 pages of messages of up to DEFAULT_MAX_MESSAGE_BYTES = 16 MiB, transports/transport.ts); tools.ts cuts
// only the model-facing text of a result (MCP_OUTPUT_MAX_BYTES = 20 KiB) and saves the whole text.
internal static partial class Program
{
    /// <summary>A server named s0 lists 200 tools over two pages plus one with a 300 KB schema; every other server lists one tool.
    /// Every call answers with 2,000,000 characters of text.</summary>
    private sealed class CapMcpChannel(string server) : IMcpAdmittedRequestChannel
    {
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask ConfigureRootsAsync(JsonData roots, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            static string Tool(string name, string description = "Fixture tool.") =>
                System.Text.Json.JsonSerializer.Serialize(new { name, description = "Fixture tool.", inputSchema = new { type = "object", description } });
            return ValueTask.FromResult(JsonData.Parse(method switch
            {
                "initialize" => "{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"" + server + "\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}}",
                "tools/list" when server != "s0" => "{\"tools\":[" + Tool("get") + "]}",
                "tools/list" when parameters is null => "{\"tools\":[" + string.Join(",", Enumerable.Range(0, 100).Select(index => Tool("t" + index))) + "],\"nextCursor\":\"p2\"}",
                "tools/list" => "{\"tools\":[" + string.Join(",", Enumerable.Range(100, 100).Select(index => Tool("t" + index))) + "," +
                    Tool("big", new string('d', 300_000)) + "]}",
                "tools/call" => "{\"content\":[{\"type\":\"text\",\"text\":\"" + new string('a', 2_000_000) + "\"}]}",
                _ => throw new IOException("Unexpected MCP method " + method)
            }));
        }
        public Task CloseAsync() => Task.CompletedTask;
    }

    private static IEnumerable<(string, Func<Task>)> CapCases() =>
    [
        // agent.ts: no tool, subscriber, queue or progress count bound on the Pi entry; the explicit verbs keep the profile defaults.
        ("caps.pi-entry-agent-options-have-no-count-bounds", Sync(() =>
        {
            var pi = RpcSessionCommand.AgentOptions(pi: true);
            Equal(int.MaxValue, pi.MaximumTools, "tools"); Equal(int.MaxValue, pi.MaximumSubscribers, "subscribers");
            Equal(int.MaxValue, pi.Queue!.MaximumMessagesPerQueue, "queued messages"); Equal(int.MaxValue, pi.Queue.MaximumMessageCharacters, "queued message size");
            Equal(int.MaxValue, pi.ProgressDelivery!.MaximumUpdates, "progress updates"); Equal(int.MaxValue, pi.ResultValues.MaximumContentBlocks, "result blocks");
            Equal(int.MaxValue, pi.Loop!.MaximumTurns, "turns");
            var explicitVerb = RpcSessionCommand.AgentOptions(pi: false);
            Equal(128, explicitVerb.MaximumTools, "explicit tools"); Check(explicitVerb.Queue is null, "explicit queue defaults");
            Equal(64, explicitVerb.Loop!.MaximumTurns, "explicit turns");
        })),
        // print-mode.ts session.prompt and skills.ts: 150 skills (formerly 128 files), one of 200 KB (formerly 64 KiB), expanded into a
        // print prompt of 100,000 characters (formerly 65,536) through the skill, template and dispatcher admission.
        ("caps.print-prompt-and-skills-have-no-pi-limit", async () =>
        {
            using var sandbox = new Sandbox("caps-print-skills");
            for (var index = 0; index < 150; index++)
                sandbox.Write(Path.Combine(sandbox.AgentDir, "skills", "s" + index, "SKILL.md"), "---\nname: s" + index + "\ndescription: Skill " + index + "\n---\nbody " + index + "\n");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "skills", "big", "SKILL.md"), "---\nname: big\ndescription: Big skill\n---\n" + new string('b', 200_000) + "\n");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "prompts", "hello.md"), "Hello $1");
            var arguments = new string('y', 100_000);
            var (code, stdout, stderr) = await sandbox.Run(["-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "/skill:big " + arguments]);
            Equal(0, code, "exit; " + stderr);
            Equal("ok\n", stdout, "final text");
            var system = string.Join("\n\n", sandbox.Requests[0].Json.GetProperty("system").EnumerateArray().Select(block => block.GetProperty("text").GetString()));
            Check(Enumerable.Range(0, 150).All(index => system.Contains("<name>s" + index + "</name>", StringComparison.Ordinal)), "every skill listed");
            var user = sandbox.Requests[0].Json.GetProperty("messages").EnumerateArray().Last().GetProperty("content");
            var text = user.ValueKind == System.Text.Json.JsonValueKind.String ? user.GetString()! : string.Concat(user.EnumerateArray().Select(block => block.GetProperty("text").GetString()));
            Check(text.StartsWith("<skill name=\"big\"", StringComparison.Ordinal) && text.Contains(new string('b', 200_000), StringComparison.Ordinal) &&
                text.EndsWith("</skill>\n\n" + arguments, StringComparison.Ordinal), "the whole skill and the whole argument text: " + text.Length);
        }),
        // anthropic-messages.ts and agent-loop.ts: a response of 80 tool_use blocks (formerly 64 stream content slots) runs every call.
        ("caps.response-with-many-parallel-tool-calls", async () =>
        {
            using var sandbox = new Sandbox("caps-parallel-calls");
            const int calls = 80;
            var body = Frame("message_start", new { type = "message_start", message = new { id = "msg_m", role = "assistant", model = "claude-sonnet-4-5", content = Array.Empty<object>(), usage = new { input_tokens = 3, output_tokens = 0 } } });
            for (var index = 0; index < calls; index++)
                body += Frame("content_block_start", new { type = "content_block_start", index, content_block = new { type = "tool_use", id = "toolu_" + index, name = "ls", input = new { } } })
                    + Frame("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "input_json_delta", partial_json = "{\"path\":\".\"}" } })
                    + Frame("content_block_stop", new { type = "content_block_stop", index });
            body += Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "tool_use" }, usage = new { output_tokens = 2 } })
                + Frame("message_stop", new { type = "message_stop" });
            sandbox.Respond = (_, index) => index == 0 ? Sse(body) : AnthropicText("done");
            var (code, stdout, stderr) = await sandbox.Run(["-p", "--tools", "ls", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "list"]);
            Equal(0, code, "exit; " + stderr);
            Equal("done\n", stdout, "final text");
            var results = sandbox.Requests[1].Json.GetProperty("messages").EnumerateArray().Last().GetProperty("content").EnumerateArray()
                .Where(item => item.GetProperty("type").GetString() == "tool_result").ToList();
            Equal(calls, results.Count, "every call has its result");
            Check(results.All(result => !result.TryGetProperty("is_error", out var error) || !error.GetBoolean()), "no call failed");
        }),
        // mcp/index.ts and runtime.ts: 130 servers (formerly 128), one listing 201 tools over two pages (formerly 128 per binding and
        // per registry owner) with a 300 KB schema (formerly 256 KiB per schema and 64 KiB per registered schema), and a 2,000,000-
        // character result (formerly 1 MiB per response) cut to 20 KiB for the model with the whole text saved.
        ("caps.many-mcp-servers-tools-large-schema-and-response", async () =>
        {
            using var sandbox = new Sandbox("caps-mcp");
            var servers = string.Join(",", Enumerable.Range(0, 130).Select(index => "\"s" + index + "\":{\"command\":\"fixture\",\"exposure\":\"direct\"}"));
            sandbox.Write(Path.Combine(sandbox.AgentDir, "mcp.json"), "{\"mcpServers\":{" + servers + "}}");
            sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("mcp__s0__t199", new { }) : AnthropicText("done");
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var host = sandbox.Host(stdout, stderr, null) with
            {
                CreateMcpHost = agentDir => new McpSessionHost(agentDir, sandbox.Home, () => [])
                { CreateChannel = entry => (actual, token) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(new CapMcpChannel(entry.Name)) }
            };
            var code = await PiCommand.RunAsync(["-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "call it"], host, CancellationToken.None);
            Equal(0, code, "exit; " + stderr);
            var tools = sandbox.Requests[0].Json.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToHashSet();
            Check(Enumerable.Range(0, 200).All(index => tools.Contains("mcp__s0__t" + index)) && tools.Contains("mcp__s0__big") &&
                Enumerable.Range(1, 129).All(index => tools.Contains("mcp__s" + index + "__get")), "every server's tools declared: " + tools.Count + " missing=" + string.Join(",", Enumerable.Range(0, 200).Select(i => "mcp__s0__t" + i).Append("mcp__s0__big").Concat(Enumerable.Range(1, 129).Select(i => "mcp__s" + i + "__get")).Where(n => !tools.Contains(n))) + " stderr=" + stderr);
            var result = sandbox.Requests[1].Json.GetProperty("messages").EnumerateArray().Last().GetProperty("content").EnumerateArray()
                .First(item => item.GetProperty("type").GetString() == "tool_result").GetProperty("content").GetString()!;
            Check(result.StartsWith("Warning: truncated output (original token count: 500000)\nTotal output lines: 1\n\n", StringComparison.Ordinal), result[..Math.Min(200, result.Length)]);
            var saved = System.Text.RegularExpressions.Regex.Match(result, @"\[Full output: (.*) \(read it with offset/limit\)\]$").Groups[1].Value;
            Equal(2_000_000, File.ReadAllText(saved).Length, "the whole text is saved");
            File.Delete(saved);
        }),
    ];
}
