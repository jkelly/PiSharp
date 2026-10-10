// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/rpc/rpc-mode.ts, packages/coding-agent/src/core/session-manager.ts,
// packages/ai/src/utils/json-parse.ts, packages/ai/src/utils/sanitize-unicode.ts, packages/ai/src/api/anthropic-messages.ts and
// packages/coding-agent/src/core/tools/write.ts.
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Pi;

// JSON values travel through Pi as JavaScript values: JSON.parse keeps an escaped lone surrogate as that UTF-16 code unit and JSON.stringify
// writes it back as a lowercase \uXXXX escape, so an RPC prompt, streamed text, a streamed tool call's arguments and the session file all
// keep it. Only the places Pi itself changes it do: anthropic-messages.ts sanitizeSurrogates drops it from request text (not from tool_use
// input), and Node's UTF-8 encoder writes it to a file as U+FFFD. The expected request messages, session lines and file bytes were captured
// by running the installed @earendil-works/pi-coding-agent@1.1.0 CLI (`pi --mode rpc`, then `pi -p --session`) against a local fake
// Anthropic endpoint with the same responses.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> JsonFidelityCases() =>
    [
        // mcp/index.ts: a tools/call result's text is whatever JSON.parse read, a lone surrogate included; the session keeps it and
        // anthropic-messages.ts sanitizeSurrogates drops it from the tool_result text it sends.
        ("json-fidelity.mcp-tool-result-keeps-a-lone-surrogate", async () =>
        {
            using var sandbox = new Sandbox("json-fidelity-mcp");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "mcp.json"), """{"mcpServers":{"docs":{"command":"docs-server","exposure":"direct"}}}""");
            sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCall("mcp__docs__lookup", new { }) : AnthropicText("done");
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var host = sandbox.Host(stdout, stderr, null) with
            {
                CreateMcpHost = agentDir => new PiSharp.Cli.Mcp.McpSessionHost(agentDir, sandbox.Home, () => [])
                {
                    CreateChannel = entry => (actual, token) => ValueTask.FromResult<PiSharp.Extensions.Mcp.Runtime.IMcpAdmittedRequestChannel>(
                        new FakeMcpChannel("lookup", "{\"content\":[{\"type\":\"text\",\"text\":\"a\\udc00b\"}]}"))
                }
            };
            Equal(0, await PiCommand.RunAsync(["-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "look"], host, CancellationToken.None), "exit; " + stderr);
            Equal(2, sandbox.Requests.Count, "the tool result reached the model");
            Check(RequestMessages(sandbox.Requests[1]).Contains("\"content\":\"ab\",\"is_error\":false", StringComparison.Ordinal),
                "sanitized tool_result: " + RequestMessages(sandbox.Requests[1]));
            Check(File.ReadAllText(sandbox.SessionFiles().Single()).Contains("a\\udc00b", StringComparison.Ordinal), "the session keeps the lone surrogate");
        }),
        ("json-fidelity.lone-surrogates-travel-through-rpc-prompt-tool-arguments-and-session", async () =>
        {
            using var sandbox = new Sandbox("json-fidelity-surrogates");
            // The tool call's partial_json carries the escape as JSON text.
            sandbox.Respond = (_, index) => index == 0 ? AnthropicStream(ToolUseBlock(0, "toolu_01", "{\"path\":\"a.txt\",\"content\":\"x\\ud800y\"}"), "tool_use") : AnthropicText("done");
            var (exit, frames, stderr) = await RunRpcPrompt(sandbox, "{\"id\":\"p\",\"type\":\"prompt\",\"message\":\"hi \\ud800 there\"}");
            Equal(0, exit, "rpc exit; " + stderr + "\n" + string.Join("\n", frames));
            Check(frames.Any(frame => frame.StartsWith("{\"id\":\"p\",\"type\":\"response\",\"command\":\"prompt\",\"success\":true", StringComparison.Ordinal)),
                "prompt accepted: " + string.Join("\n", frames));
            // Events echo the strings as JSON.stringify writes them (formerly the prompt text arrived as U+FFFD).
            Check(frames.Contains("{\"type\":\"tool_execution_start\",\"toolCallId\":\"toolu_01\",\"toolName\":\"write\",\"args\":{\"path\":\"a.txt\",\"content\":\"x\\ud800y\"}}"),
                "tool_execution_start: " + string.Join("\n", frames));
            Check(frames.Any(frame => frame.StartsWith("{\"type\":\"message_start\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi \\ud800 there\"}]", StringComparison.Ordinal)),
                "user message event: " + string.Join("\n", frames));
            Equal(2, sandbox.Requests.Count, "requests; " + string.Join("\n", frames));
            Equal("""[{"role":"user","content":[{"type":"text","text":"hi  there","cache_control":{"type":"ephemeral"}}]}]""", RequestMessages(sandbox.Requests[0]), "first request");
            Equal("""[{"role":"user","content":[{"type":"text","text":"hi  there"}]},{"role":"assistant","content":[{"type":"tool_use","id":"toolu_01","name":"write","input":{"path":"a.txt","content":"x\ud800y"}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_01","content":"Successfully wrote to a.txt","is_error":false,"cache_control":{"type":"ephemeral"}}]}]""",
                RequestMessages(sandbox.Requests[1]), "second request");
            Equal("78-EF-BF-BD-79", BitConverter.ToString(File.ReadAllBytes(Path.Combine(sandbox.Cwd, "a.txt"))), "written bytes");
            var session = File.ReadAllText(sandbox.SessionFiles().Single());
            Check(session.Contains("\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi \\ud800 there\"}],", StringComparison.Ordinal), "user line: " + session);
            Check(session.Contains("\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"toolCall\",\"id\":\"toolu_01\",\"name\":\"write\",\"arguments\":{\"path\":\"a.txt\",\"content\":\"x\\ud800y\"}}],", StringComparison.Ordinal),
                "tool call line: " + session);
            // The session reopens (formerly a line with an escaped lone surrogate stopped the file from opening) and replays both values.
            await Reopen(sandbox, session,
                """[{"role":"user","content":[{"type":"text","text":"hi  there"}]},{"role":"assistant","content":[{"type":"tool_use","id":"toolu_01","name":"write","input":{"path":"a.txt","content":"x\ud800y"}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_01","content":"Successfully wrote to a.txt","is_error":false}]},{"role":"assistant","content":[{"type":"text","text":"done"}]},{"role":"user","content":[{"type":"text","text":"more","cache_control":{"type":"ephemeral"}}]}]""");
        }),
        ("json-fidelity.lone-surrogates-in-streamed-text-and-arguments", async () =>
        {
            using var sandbox = new Sandbox("json-fidelity-stream");
            // The SDK's JSON.parse of each event yields a lone surrogate code unit in the text delta and in the partial_json text.
            sandbox.Respond = (_, index) => index == 0
                ? AnthropicStream(TextBlock(0, "a\\ud800b") + ToolUseBlock(1, "toolu_02", "{\\\"path\\\":\\\"b.txt\\\",\\\"content\\\":\\\"x\\ud800y\\\"}", escaped: true), "tool_use")
                : AnthropicText("done");
            var (code, stdout, stderr) = await sandbox.Run(["-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tools", "write", "hello"]);
            Equal(0, code, "exit; " + stderr);
            Equal("done\n", stdout, "final text");
            Equal("""[{"role":"user","content":[{"type":"text","text":"hello"}]},{"role":"assistant","content":[{"type":"text","text":"ab"},{"type":"tool_use","id":"toolu_02","name":"write","input":{"path":"b.txt","content":"x\ud800y"}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_02","content":"Successfully wrote to b.txt","is_error":false,"cache_control":{"type":"ephemeral"}}]}]""",
                RequestMessages(sandbox.Requests[1]), "second request");
            Equal("78-EF-BF-BD-79", BitConverter.ToString(File.ReadAllBytes(Path.Combine(sandbox.Cwd, "b.txt"))), "written bytes");
            var session = File.ReadAllText(sandbox.SessionFiles().Single());
            Check(session.Contains("\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"a\\ud800b\"},{\"type\":\"toolCall\",\"id\":\"toolu_02\",\"name\":\"write\",\"arguments\":{\"path\":\"b.txt\",\"content\":\"x\\ud800y\"}}],",
                StringComparison.Ordinal), "assistant line: " + session);
            await Reopen(sandbox, session,
                """[{"role":"user","content":[{"type":"text","text":"hello"}]},{"role":"assistant","content":[{"type":"text","text":"ab"},{"type":"tool_use","id":"toolu_02","name":"write","input":{"path":"b.txt","content":"x\ud800y"}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_02","content":"Successfully wrote to b.txt","is_error":false}]},{"role":"assistant","content":[{"type":"text","text":"done"}]},{"role":"user","content":[{"type":"text","text":"more","cache_control":{"type":"ephemeral"}}]}]""");
        }),
        // Each API's converter replays a session holding lone surrogates: text through sanitizeSurrogates (dropped), a tool call's arguments
        // as JSON.stringify writes them (kept as escapes), whether stringified into a string (openai-completions, openai-responses,
        // mistral-conversations) or sent as an object (google-generative-ai, anthropic-messages). Captured with `pi -p --session` per API.
        ("json-fidelity.each-api-replays-lone-surrogates-as-pi-does", async () =>
        {
            using var sandbox = new Sandbox("json-fidelity-replay");
            (string Api, Func<JsonElement, string> Pick, string Expected)[] apis =
            [
                ("openai-completions", body => Skip(body.GetProperty("messages")),
                    """[{"role":"user","content":[{"type":"text","text":"hi  there"}]},{"role":"assistant","content":"ab","tool_calls":[{"id":"call_1","type":"function","function":{"name":"write","arguments":"{\"path\":\"a.txt\",\"content\":\"x\\ud800y\",\"n\":[1,\"\\udfff\"]}"}}]},{"role":"tool","content":"ok ","tool_call_id":"call_1"},{"role":"user","content":[{"type":"text","text":"more"}]}]"""),
                ("openai-responses", body => Skip(body.GetProperty("input")),
                    """[{"role":"user","content":[{"type":"input_text","text":"hi  there"}]},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ab","annotations":[]}],"status":"completed","id":"msg_pi_1"},{"type":"function_call","call_id":"call_1","name":"write","arguments":"{\"path\":\"a.txt\",\"content\":\"x\\ud800y\",\"n\":[1,\"\\udfff\"]}"},{"type":"function_call_output","call_id":"call_1","output":"ok "},{"role":"user","content":[{"type":"input_text","text":"more"}]}]"""),
                ("google-generative-ai", body => PiSharp.AI.StreamingJson.JsonReformat(body.GetProperty("contents").GetRawText()),
                    """[{"parts":[{"text":"hi  there"}],"role":"user"},{"parts":[{"text":"ab"},{"functionCall":{"args":{"path":"a.txt","content":"x\ud800y","n":[1,"\udfff"]},"name":"write"}}],"role":"model"},{"parts":[{"functionResponse":{"name":"write","response":{"output":"ok "}}}],"role":"user"},{"parts":[{"text":"more"}],"role":"user"}]"""),
                ("mistral-conversations", body => Skip(body.GetProperty("messages")),
                    """[{"role":"user","content":[{"type":"text","text":"hi  there"}]},{"role":"assistant","prefix":false,"content":[{"type":"text","text":"ab"}],"tool_calls":[{"id":"call_1","type":"function","function":{"name":"write","arguments":"{\"path\":\"a.txt\",\"content\":\"x\\ud800y\",\"n\":[1,\"\\udfff\"]}"},"index":0}]},{"role":"tool","name":"write","content":[{"type":"text","text":"ok"}],"tool_call_id":"call_1"},{"role":"user","content":[{"type":"text","text":"more"}]}]"""),
                ("anthropic-messages", body => PiSharp.AI.StreamingJson.JsonReformat(body.GetProperty("messages").GetRawText()),
                    """[{"role":"user","content":[{"type":"text","text":"hi  there"}]},{"role":"assistant","content":[{"type":"text","text":"ab"},{"type":"tool_use","id":"call_1","name":"write","input":{"path":"a.txt","content":"x\ud800y","n":[1,"\udfff"]}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"call_1","content":"ok ","is_error":false}]},{"role":"user","content":[{"type":"text","text":"more","cache_control":{"type":"ephemeral"}}]}]"""),
            ];
            sandbox.Write(Path.Combine(sandbox.AgentDir, "models.json"), "{\"providers\":{" + string.Join(",", apis.Select(api =>
                "\"" + Provider(api.Api) + "\":{\"baseUrl\":\"https://replay.test\",\"api\":\"" + api.Api + "\",\"apiKey\":\"k\",\"models\":[{\"id\":\"m\"}]}")) + "}}");
            sandbox.Respond = (_, _) => new(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"fixture\"}}", Encoding.UTF8, "application/json") };
            const string usage = "{\"input\":1,\"output\":1,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":2,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}}";
            foreach (var (api, pick, expected) in apis)
            {
                var file = sandbox.Write("replay-" + api + ".jsonl", string.Join("\n",
                    "{\"type\":\"session\",\"version\":3,\"id\":\"seed\",\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"cwd\":" + JsonSerializer.Serialize(sandbox.Cwd) + "}",
                    "{\"type\":\"message\",\"id\":\"u1\",\"parentId\":null,\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi \\ud800 there\"}],\"timestamp\":1}}",
                    "{\"type\":\"message\",\"id\":\"a1\",\"parentId\":\"u1\",\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"a\\udc00b\"},{\"type\":\"toolCall\",\"id\":\"call_1\",\"name\":\"write\",\"arguments\":{\"path\":\"a.txt\",\"content\":\"x\\ud800y\",\"n\":[1,\"\\udfff\"]}}],\"api\":\"" + api + "\",\"provider\":\"" + Provider(api) + "\",\"model\":\"m\",\"usage\":" + usage + ",\"stopReason\":\"toolUse\",\"timestamp\":2}}",
                    "{\"type\":\"message\",\"id\":\"r1\",\"parentId\":\"a1\",\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"message\":{\"role\":\"toolResult\",\"toolCallId\":\"call_1\",\"toolName\":\"write\",\"content\":[{\"type\":\"text\",\"text\":\"ok \\ud800\"}],\"isError\":false,\"timestamp\":3}}") + "\n");
                lock (sandbox.Requests) sandbox.Requests.Clear();
                var (_, _, stderr) = await sandbox.Run(["-p", "--provider", Provider(api), "--model", "m", "--tools", "write", "--session", file, "more"]);
                Check(sandbox.Requests.Count >= 1, api + ": no request; " + stderr);
                Equal(expected, pick(sandbox.Requests[0].Json), api + " request");
            }
        }),
        // JSON.parse keeps a lone surrogate in an object name too, and JSON.stringify writes it back escaped (formerly it became U+FFFD):
        // a streamed tool call's argument names reach the tool events, the session line and the replay unchanged. The write tool's
        // TypeBox schema admits the extra property.
        ("json-fidelity.lone-surrogate-names-travel-through-rpc-tool-arguments-and-session", async () =>
        {
            using var sandbox = new Sandbox("json-fidelity-names");
            const string arguments = "{\"path\":\"a.txt\",\"content\":\"x\",\"k\\ud800\":[\"v\\udc00\",{\"\\udfff\":1}]}";
            sandbox.Respond = (_, index) => index == 0 ? AnthropicStream(ToolUseBlock(0, "toolu_n", arguments), "tool_use") : AnthropicText("done");
            var (exit, frames, stderr) = await RunRpcPrompt(sandbox, "{\"id\":\"p\",\"type\":\"prompt\",\"message\":\"go\",\"x\\ud800\":1}");
            Equal(0, exit, "rpc exit; " + stderr + "\n" + string.Join("\n", frames));
            Check(frames.Contains("{\"type\":\"tool_execution_start\",\"toolCallId\":\"toolu_n\",\"toolName\":\"write\",\"args\":" + arguments + "}"),
                "tool_execution_start: " + string.Join("\n", frames));
            Equal(2, sandbox.Requests.Count, "requests; " + string.Join("\n", frames));
            Equal("x", File.ReadAllText(Path.Combine(sandbox.Cwd, "a.txt")), "written");
            Equal("""[{"role":"user","content":[{"type":"text","text":"go"}]},{"role":"assistant","content":[{"type":"tool_use","id":"toolu_n","name":"write","input":{"path":"a.txt","content":"x","k\ud800":["v\udc00",{"\udfff":1}]}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_n","content":"Successfully wrote to a.txt","is_error":false,"cache_control":{"type":"ephemeral"}}]}]""",
                RequestMessages(sandbox.Requests[1]), "second request");
            var session = File.ReadAllText(sandbox.SessionFiles().Single());
            Check(session.Contains("\"arguments\":" + arguments + "}],", StringComparison.Ordinal), "tool call line: " + session);
            await Reopen(sandbox, session,
                """[{"role":"user","content":[{"type":"text","text":"go"}]},{"role":"assistant","content":[{"type":"tool_use","id":"toolu_n","name":"write","input":{"path":"a.txt","content":"x","k\ud800":["v\udc00",{"\udfff":1}]}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_n","content":"Successfully wrote to a.txt","is_error":false}]},{"role":"assistant","content":[{"type":"text","text":"done"}]},{"role":"user","content":[{"type":"text","text":"more","cache_control":{"type":"ephemeral"}}]}]""");
        }),
        // Each API's converter replays argument names holding lone surrogates as JSON.stringify writes them, whether stringified into a
        // string (openai-completions, openai-responses, mistral-conversations) or sent as an object (google-generative-ai, anthropic-messages).
        ("json-fidelity.each-api-replays-lone-surrogate-names-as-pi-does", async () =>
        {
            using var sandbox = new Sandbox("json-fidelity-name-replay");
            const string stringified = "\"{\\\"path\\\":\\\"a.txt\\\",\\\"k\\\\ud800\\\":[\\\"v\\\\udc00\\\",{\\\"\\\\udfff\\\":1}]}\"";
            const string objectForm = "{\"path\":\"a.txt\",\"k\\ud800\":[\"v\\udc00\",{\"\\udfff\":1}]}";
            (string Api, Func<JsonElement, string> Pick, string Expected)[] apis =
            [
                ("openai-completions", body => Skip(body.GetProperty("messages")),
                    "\"function\":{\"name\":\"write\",\"arguments\":" + stringified + "}"),
                ("openai-responses", body => Skip(body.GetProperty("input")),
                    "{\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"write\",\"arguments\":" + stringified + "}"),
                ("google-generative-ai", body => PiSharp.AI.StreamingJson.JsonReformat(body.GetProperty("contents").GetRawText()),
                    "{\"functionCall\":{\"args\":" + objectForm + ",\"name\":\"write\"}}"),
                ("mistral-conversations", body => Skip(body.GetProperty("messages")),
                    "\"function\":{\"name\":\"write\",\"arguments\":" + stringified + "}"),
                ("anthropic-messages", body => PiSharp.AI.StreamingJson.JsonReformat(body.GetProperty("messages").GetRawText()),
                    "{\"type\":\"tool_use\",\"id\":\"call_1\",\"name\":\"write\",\"input\":" + objectForm + "}"),
            ];
            sandbox.Write(Path.Combine(sandbox.AgentDir, "models.json"), "{\"providers\":{" + string.Join(",", apis.Select(api =>
                "\"" + Provider(api.Api) + "\":{\"baseUrl\":\"https://replay.test\",\"api\":\"" + api.Api + "\",\"apiKey\":\"k\",\"models\":[{\"id\":\"m\"}]}")) + "}}");
            sandbox.Respond = (_, _) => new(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"fixture\"}}", Encoding.UTF8, "application/json") };
            const string usage = "{\"input\":1,\"output\":1,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":2,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}}";
            foreach (var (api, pick, expected) in apis)
            {
                var file = sandbox.Write("names-" + api + ".jsonl", string.Join("\n",
                    "{\"type\":\"session\",\"version\":3,\"id\":\"seed\",\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"cwd\":" + JsonSerializer.Serialize(sandbox.Cwd) + "}",
                    "{\"type\":\"message\",\"id\":\"u1\",\"parentId\":null,\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}],\"timestamp\":1}}",
                    "{\"type\":\"message\",\"id\":\"a1\",\"parentId\":\"u1\",\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"toolCall\",\"id\":\"call_1\",\"name\":\"write\",\"arguments\":" + objectForm + "}],\"api\":\"" + api + "\",\"provider\":\"" + Provider(api) + "\",\"model\":\"m\",\"usage\":" + usage + ",\"stopReason\":\"toolUse\",\"timestamp\":2}}",
                    "{\"type\":\"message\",\"id\":\"r1\",\"parentId\":\"a1\",\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"message\":{\"role\":\"toolResult\",\"toolCallId\":\"call_1\",\"toolName\":\"write\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"isError\":false,\"timestamp\":3}}") + "\n");
                lock (sandbox.Requests) sandbox.Requests.Clear();
                var (_, _, stderr) = await sandbox.Run(["-p", "--provider", Provider(api), "--model", "m", "--tools", "write", "--session", file, "more"]);
                Check(sandbox.Requests.Count >= 1, api + ": no request; " + stderr);
                var actual = pick(sandbox.Requests[0].Json);
                Check(actual.Contains(expected, StringComparison.Ordinal), api + " request: " + actual);
            }
        }),
        // A provider may report fractional token counts: Pi keeps each as the JavaScript number it is, prices it with calculateCost in
        // binary64 and writes it as JSON.stringify does (formerly the response failed, and Bedrock read it as 0). Captured with `pi -p` per
        // API against the same fake responses and rates.
        ("json-fidelity.fractional-usage-counts-are-kept-and-priced-as-pi-does", async () =>
        {
            using var sandbox = new Sandbox("json-fidelity-fraction");
            const string cost = "{\"input\":3,\"output\":15,\"cacheRead\":0.3,\"cacheWrite\":3.75}";
            string[] apis = ["anthropic-messages", "openai-completions", "openai-responses"];
            sandbox.Write(Path.Combine(sandbox.AgentDir, "models.json"), "{\"providers\":{" + string.Join(",", apis.Select(api =>
                "\"" + api + "\":{\"baseUrl\":\"https://fraction.test/v1\",\"api\":\"" + api + "\",\"apiKey\":\"k\",\"models\":[{\"id\":\"m\",\"cost\":" + cost + "}]}")) + "}}");
            var current = "";
            string Data(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
            sandbox.Respond = (_, _) => current switch
            {
                "anthropic-messages" => Sse(
                    RawFrame("message_start", "{\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"role\":\"assistant\",\"model\":\"m\",\"content\":[],\"usage\":{\"input_tokens\":3.5,\"output_tokens\":0,\"cache_read_input_tokens\":1.25}}}")
                    + TextBlock(0, "done")
                    + RawFrame("message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":2.75}}")
                    + RawFrame("message_stop", "{\"type\":\"message_stop\"}")),
                "openai-completions" => Sse(
                    Data(new { id = "c1", @object = "chat.completion.chunk", created = 1, model = "m", choices = new[] { new { index = 0, delta = new { role = "assistant", content = "done" }, finish_reason = (string?)null } } })
                    + Data(new { id = "c1", @object = "chat.completion.chunk", created = 1, model = "m", choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } } })
                    + Data(new { id = "c1", @object = "chat.completion.chunk", created = 1, model = "m", choices = Array.Empty<object>(), usage = new { prompt_tokens = 10.5, completion_tokens = 2.25,
                        total_tokens = 12.75, prompt_tokens_details = new { cached_tokens = 1.5 }, completion_tokens_details = new { reasoning_tokens = 0.5 } } })
                    + "data: [DONE]\n\n"),
                _ => Sse(string.Concat(new object[]
                {
                    new { type = "response.created", sequence_number = 0, response = new { id = "r1", status = "in_progress", output = Array.Empty<object>() } },
                    new { type = "response.output_item.added", sequence_number = 1, output_index = 0, item = new { type = "message", id = "m1", role = "assistant", status = "in_progress", content = Array.Empty<object>() } },
                    new { type = "response.content_part.added", sequence_number = 2, output_index = 0, item_id = "m1", content_index = 0, part = new { type = "output_text", text = "", annotations = Array.Empty<object>() } },
                    new { type = "response.output_text.delta", sequence_number = 3, output_index = 0, item_id = "m1", content_index = 0, delta = "done" },
                    new { type = "response.output_item.done", sequence_number = 4, output_index = 0, item = new { type = "message", id = "m1", role = "assistant", status = "completed",
                        content = new[] { new { type = "output_text", text = "done", annotations = Array.Empty<object>() } } } },
                    new { type = "response.completed", sequence_number = 5, response = new { id = "r1", status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 10.5,
                        output_tokens = 2.25, total_tokens = 12.75, input_tokens_details = new { cached_tokens = 1.5 }, output_tokens_details = new { reasoning_tokens = 0.5 } } } },
                }.Select(item => "event: " + JsonSerializer.SerializeToElement(item).GetProperty("type").GetString() + "\n" + Data(item))))
            };
            var expected = new Dictionary<string, string>
            {
                ["anthropic-messages"] = """{"input":3.5,"output":2.75,"cacheRead":1.25,"cacheWrite":0,"totalTokens":7.5,"cost":{"input":0.000010500000000000001,"output":0.00004125,"cacheRead":3.75e-7,"cacheWrite":0,"total":0.000052125},"cacheWrite1h":0}""",
                ["openai-completions"] = """{"input":9,"output":2.25,"cacheRead":1.5,"cacheWrite":0,"reasoning":0.5,"totalTokens":12.75,"cost":{"input":0.000027,"output":0.00003375,"cacheRead":4.5e-7,"cacheWrite":0,"total":0.0000612}}""",
                ["openai-responses"] = """{"input":9,"output":2.25,"cacheRead":1.5,"cacheWrite":0,"reasoning":0.5,"totalTokens":12.75,"cost":{"input":0.000027,"output":0.00003375,"cacheRead":4.5e-7,"cacheWrite":0,"total":0.0000612}}""",
            };
            foreach (var api in apis)
            {
                current = api;
                var before = sandbox.SessionFiles().ToHashSet(StringComparer.Ordinal);
                var (code, stdout, stderr) = await sandbox.Run(["-p", "--provider", api, "--model", "m", "hello"]);
                Equal(0, code, api + " exit; " + stderr);
                Equal("done\n", stdout, api + " answer");
                var file = sandbox.SessionFiles().Single(path => !before.Contains(path));
                var line = File.ReadLines(file).Single(text => text.Contains("\"role\":\"assistant\"", StringComparison.Ordinal));
                Equal(expected[api], PiSharp.AI.StreamingJson.JsonReformat(JsonDocument.Parse(line).RootElement.GetProperty("message").GetProperty("usage").GetRawText()), api + " usage");
                Check(line.Contains("\"usage\":" + expected[api], StringComparison.Ordinal), api + " session line: " + line);
                // The session reopens with its fractional usage (the context estimate reads it) and continues.
                var (again, _, againError) = await sandbox.Run(["-p", "--provider", api, "--model", "m", "--session", file, "more"]);
                Equal(0, again, api + " reopen; " + againError);
            }
        }),
        // JSON.parse has no depth limit and V8's JSON.stringify recurses about 1,700 objects deep, so Pi reads, stores and replays values far
        // deeper than the former 64 levels. PiSharp now holds JsonData.MaximumDepth (1,000) levels: an RPC command, a streamed tool call's
        // arguments and the session line keep a value 900 levels deep; a command deeper than 1,000 levels is answered as a parse failure and
        // the stream goes on (formerly 65 levels ended the stream).
        ("json-fidelity.deep-values-pass-rpc-tool-arguments-and-sessions", async () =>
        {
            using var sandbox = new Sandbox("json-fidelity-deep");
            static string Deep(int levels) => new string('[', levels - 1) + "{\"leaf\":1}" + new string(']', levels - 1);
            var deep = Deep(900);
            var partial = "{\"path\":\"deep.txt\",\"content\":\"ok\",\"deep\":" + deep + "}";
            sandbox.Respond = (_, index) => index == 0 ? AnthropicStream(ToolUseBlock(0, "toolu_d", partial), "tool_use") : AnthropicText("done");
            var input = new ScriptedInput();
            input.Send("{\"id\":\"b\",\"type\":\"bogus\",\"x\":" + Deep(990) + "}");
            input.Send("{\"id\":\"t\",\"type\":\"bogus\",\"x\":" + Deep(1001) + "}");
            input.Send("{\"id\":\"p\",\"type\":\"prompt\",\"message\":\"go\"}");
            using var output = new LineOutput(frame => { if (frame["type"]?.GetValue<string>() == "agent_end" || frame["command"]?.GetValue<string>() == "prompt" && frame["success"]?.GetValue<bool>() == false) input.Complete(); });
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            using var registration = deadline.Token.Register(input.Complete);
            var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
            var exit = await PiCommand.RunAsync(["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tools", "write"], host, CancellationToken.None);
            var frames = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Equal(0, exit, "rpc exit; " + stderr);
            Check(frames.Contains("""{"id":"b","type":"response","command":"bogus","success":false,"error":"Unknown command: bogus"}"""), "990 levels: " + string.Join("\n", frames.Take(3)));
            Check(frames.Contains("""{"type":"response","command":"parse","success":false,"error":"Failed to parse command: JSON nests deeper than 1000 levels"}"""),
                "1,001 levels: " + string.Join("\n", frames.Take(3)));
            Equal(2, sandbox.Requests.Count, "requests; " + string.Join("\n", frames.Where(frame => frame.Length < 2000)));
            Equal("ok", File.ReadAllText(Path.Combine(sandbox.Cwd, "deep.txt")), "written");
            Check(sandbox.Requests[1].Body!.Contains("\"input\":{\"path\":\"deep.txt\",\"content\":\"ok\",\"deep\":" + deep + "}", StringComparison.Ordinal), "replayed deep input");
            var session = File.ReadAllText(sandbox.SessionFiles().Single());
            Check(session.Contains("\"arguments\":{\"path\":\"deep.txt\",\"content\":\"ok\",\"deep\":" + deep + "}", StringComparison.Ordinal), "stored deep arguments");
            lock (sandbox.Requests) sandbox.Requests.Clear();
            sandbox.Respond = (_, _) => AnthropicText("again");
            var (code, again, error) = await sandbox.Run(["-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tools", "write", "--session", sandbox.SessionFiles().Single(), "more"]);
            Equal(0, code, "reopen exit; " + error);
            Equal("again\n", again, "reopened answer");
            Check(sandbox.Requests.Single().Body!.Contains(deep, StringComparison.Ordinal), "reopened replay keeps the deep input");
        }),
        // Every API replays a stored tool call whose arguments nest 900 levels (Pi's converters pass the object to JSON.stringify).
        ("json-fidelity.each-api-replays-deep-arguments", async () =>
        {
            using var sandbox = new Sandbox("json-fidelity-deep-replay");
            string[] apis = ["openai-completions", "openai-responses", "google-generative-ai", "mistral-conversations", "anthropic-messages"];
            sandbox.Write(Path.Combine(sandbox.AgentDir, "models.json"), "{\"providers\":{" + string.Join(",", apis.Select(api =>
                "\"" + Provider(api) + "\":{\"baseUrl\":\"https://replay.test\",\"api\":\"" + api + "\",\"apiKey\":\"k\",\"models\":[{\"id\":\"m\"}]}")) + "}}");
            sandbox.Respond = (_, _) => new(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"fixture\"}}", Encoding.UTF8, "application/json") };
            const string usage = "{\"input\":1,\"output\":1,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":2,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}}";
            var deep = new string('[', 899) + "1" + new string(']', 899);
            foreach (var api in apis)
            {
                var file = sandbox.Write("deep-" + api + ".jsonl", string.Join("\n",
                    "{\"type\":\"session\",\"version\":3,\"id\":\"seed\",\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"cwd\":" + JsonSerializer.Serialize(sandbox.Cwd) + "}",
                    "{\"type\":\"message\",\"id\":\"u1\",\"parentId\":null,\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}],\"timestamp\":1}}",
                    "{\"type\":\"message\",\"id\":\"a1\",\"parentId\":\"u1\",\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"toolCall\",\"id\":\"call_1\",\"name\":\"write\",\"arguments\":{\"path\":\"a.txt\",\"deep\":" + deep + "}}],\"api\":\"" + api + "\",\"provider\":\"" + Provider(api) + "\",\"model\":\"m\",\"usage\":" + usage + ",\"stopReason\":\"toolUse\",\"timestamp\":2}}",
                    "{\"type\":\"message\",\"id\":\"r1\",\"parentId\":\"a1\",\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"message\":{\"role\":\"toolResult\",\"toolCallId\":\"call_1\",\"toolName\":\"write\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"isError\":false,\"timestamp\":3}}") + "\n");
                lock (sandbox.Requests) sandbox.Requests.Clear();
                var (_, _, stderr) = await sandbox.Run(["-p", "--provider", Provider(api), "--model", "m", "--tools", "write", "--session", file, "more"]);
                Check(sandbox.Requests.Count >= 1, api + ": no request; " + stderr);
                Check(sandbox.Requests[0].Body!.Contains(deep, StringComparison.Ordinal), api + ": the request lacks the deep arguments");
            }
        }),
        // Pi bounds no record itself; each is one JavaScript string, so V8's longest string (536,870,888 UTF-16 code units) is what it can
        // process. The Pi entry now admits that much per session line and file, RPC line, output line, streamed event and tool result
        // (formerly 64, 64, 32, 32, 16 MiB and 8 MiB); a response keeps a 2 GiB ceiling; the explicit verbs keep their bounds.
        ("json-fidelity.pi-entry-bounds-follow-the-javascript-string-limit", Sync(() =>
        {
            const int js = 536_870_888;
            Equal(js, PiSharp.AI.PiRequestBudget.JavaScriptStringLength, "V8 string limit");
            Equal(js, PiSharp.AI.PiRequestBudget.StreamCharacters, "streamed event");
            Equal(2 * js, PiSharp.AI.PiRequestBudget.StreamTotalCharacters, "response ceiling");
            var pi = RpcSessionCommand.ReaderOptions(pi: true);
            Equal(js, pi.MaximumInputBytes, "Pi session file"); Equal(js, pi.MaximumLineBytes, "Pi session line");
            Equal(js, pi.CodecOptions!.MaximumUtf8Bytes, "Pi session record");
            var explicitVerb = RpcSessionCommand.ReaderOptions(pi: false);
            Equal(64 * 1024 * 1024, explicitVerb.MaximumInputBytes, "explicit session file"); Equal(64 * 1024 * 1024, explicitVerb.MaximumLineBytes, "explicit line");
            var results = RpcSessionCommand.AgentOptions(pi: true).ResultValues;
            Equal(js, results.MaximumCharacters, "Pi tool result");
            Equal(8 * 1024 * 1024, RpcSessionCommand.AgentOptions(pi: false).ResultValues.MaximumCharacters, "explicit tool result");
            Equal(PiSharp.Contracts.JsonData.MaximumDepth, pi.CodecOptions.MaximumJsonDepth, "session record depth");
        })),
        // A 66 MiB session record (a custom entry) opens and is returned whole over RPC (formerly over the 64 MiB line and file bounds and
        // the 32 MiB output line bound), and a 40 MiB RPC line is read (formerly over the 32 MiB input bound).
        ("json-fidelity.session-and-rpc-records-beyond-the-former-mib-bounds", async () =>
        {
            using var sandbox = new Sandbox("json-fidelity-bounds");
            var big = new string('x', 66 * 1024 * 1024);
            var file = sandbox.Write("big.jsonl", string.Join("\n",
                "{\"type\":\"session\",\"version\":3,\"id\":\"big\",\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"cwd\":" + JsonSerializer.Serialize(sandbox.Cwd) + "}",
                "{\"type\":\"custom\",\"id\":\"c1\",\"parentId\":null,\"timestamp\":\"2026-01-01T00:00:00.000Z\",\"customType\":\"blob\",\"data\":\"" + big + "\"}") + "\n");
            var input = new ScriptedInput();
            input.Send("{\"id\":\"b\",\"type\":\"bogus\",\"pad\":\"" + new string('y', 40 * 1024 * 1024) + "\"}");
            input.Send("{\"id\":\"e\",\"type\":\"get_entries\"}");
            using var output = new LineOutput(frame => { if (frame["id"]?.GetValue<string>() == "e") input.Complete(); });
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            using var registration = deadline.Token.Register(input.Complete);
            var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
            var exit = await PiCommand.RunAsync(["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "--session", file], host, CancellationToken.None);
            var frames = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Equal(0, exit, "rpc exit; " + stderr);
            Check(frames.Contains("""{"id":"b","type":"response","command":"bogus","success":false,"error":"Unknown command: bogus"}"""), "40 MiB command answered");
            var reply = frames.Single(frame => frame.StartsWith("{\"id\":\"e\",", StringComparison.Ordinal));
            Check(reply.Length > big.Length && reply.Contains("\"success\":true", StringComparison.Ordinal) && reply.Contains(big, StringComparison.Ordinal), "entries returned whole: " + reply.Length);
        }),
    ];

    /// <summary>The models.json provider serving an API: PiSharp's Mistral transport is bound to the provider named mistral (the request
    /// messages do not name the provider).</summary>
    private static string Provider(string api) => api == "mistral-conversations" ? "mistral" : api;

    /// <summary>JSON.stringify(array.slice(1)): the request messages after the system prompt (which names the sandbox paths).</summary>
    private static string Skip(JsonElement array) => "[" + string.Join(",", array.EnumerateArray().Skip(1).Select(item => PiSharp.AI.StreamingJson.JsonReformat(item.GetRawText()))) + "]";

    /// <summary>`pi -p --session file more`: the file opens, the request replays the expected messages, and earlier lines stay as written.</summary>
    private static async Task Reopen(Sandbox sandbox, string session, string expectedMessages)
    {
        lock (sandbox.Requests) sandbox.Requests.Clear();
        sandbox.Respond = (_, _) => AnthropicText("again");
        var (code, stdout, stderr) = await sandbox.Run(["-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tools", "write", "--session", sandbox.SessionFiles().Single(), "more"]);
        Equal(0, code, "reopen exit; " + stderr);
        Equal("again\n", stdout, "reopened answer");
        Equal(expectedMessages, RequestMessages(sandbox.Requests.Single()), "reopened request");
        Check(File.ReadAllText(sandbox.SessionFiles().Single()).StartsWith(session, StringComparison.Ordinal), "earlier lines unchanged");
    }

    /// <summary>JSON.stringify(body.messages) of a request, as the oracle printed it.</summary>
    private static string RequestMessages(Seen request) =>
        PiSharp.AI.StreamingJson.JsonReformat(JsonDocument.Parse(request.Body!).RootElement.GetProperty("messages").GetRawText());

    /// <summary>`pi --mode rpc` with one command line; input ends after agent_end (or a refusal).</summary>
    private static async Task<(int Exit, string[] Frames, string Stderr)> RunRpcPrompt(Sandbox sandbox, string command)
    {
        var input = new ScriptedInput();
        input.Send(command);
        using var output = new LineOutput(frame =>
        {
            if (frame["type"]?.GetValue<string>() == "agent_end" || frame["success"]?.GetValue<bool>() == false) input.Complete();
        });
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var registration = deadline.Token.Register(input.Complete);
        var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
        var exit = await PiCommand.RunAsync(["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tools", "write"], host, CancellationToken.None);
        return (exit, Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries), stderr.ToString());
    }

    // Raw SSE frames: System.Text.Json would write a lone surrogate as U+FFFD, so the event data is written as JSON text.
    private static string RawFrame(string type, string json) => "event: " + type + "\ndata: " + json + "\n\n";
    private static string TextBlock(int index, string jsonText) =>
        RawFrame("content_block_start", "{\"type\":\"content_block_start\",\"index\":" + index + ",\"content_block\":{\"type\":\"text\",\"text\":\"\"}}")
        + RawFrame("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":" + index + ",\"delta\":{\"type\":\"text_delta\",\"text\":\"" + jsonText + "\"}}")
        + RawFrame("content_block_stop", "{\"type\":\"content_block_stop\",\"index\":" + index + "}");
    /// <summary>A tool_use block; <paramref name="partialJson"/> is the partial_json text, or with <paramref name="escaped"/> already its JSON string body.</summary>
    private static string ToolUseBlock(int index, string id, string partialJson, bool escaped = false) =>
        RawFrame("content_block_start", "{\"type\":\"content_block_start\",\"index\":" + index + ",\"content_block\":{\"type\":\"tool_use\",\"id\":\"" + id + "\",\"name\":\"write\",\"input\":{}}}")
        + RawFrame("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":" + index + ",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" +
            (escaped ? "\"" + partialJson + "\"" : JsonSerializer.Serialize(partialJson)) + "}}")
        + RawFrame("content_block_stop", "{\"type\":\"content_block_stop\",\"index\":" + index + "}");
    private static HttpResponseMessage AnthropicStream(string blocks, string stopReason) => Sse(
        RawFrame("message_start", "{\"type\":\"message_start\",\"message\":{\"id\":\"msg_s\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[],\"usage\":{\"input_tokens\":3,\"output_tokens\":0}}}")
        + blocks
        + RawFrame("message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"" + stopReason + "\"},\"usage\":{\"output_tokens\":2}}")
        + RawFrame("message_stop", "{\"type\":\"message_stop\"}"));
}
