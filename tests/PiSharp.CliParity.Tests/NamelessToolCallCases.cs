// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/agent/src/agent-loop.ts.
using System.Text.Json;
using System.Text.Json.Nodes;

// Owner decision 13: a nameless tool call is accepted exactly as far as Pi accepts it. agent-loop.ts prepareToolCall finds no tool named ""
// and answers with createErrorToolResult(`Tool ${toolCall.name} not found`), i.e. "Tool  not found"; the call and its result are persisted
// and replayed with their empty identities. Expected values were captured by running pi-agent-core@1.1.0 agentLoop over
// pi-ai@1.1.0 anthropic-messages against a local HTTP server (turn 1 the nameless call, turn 2 text): the result message, and the
// replayed tool_use/tool_result of the second request. No real tool runs.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> NamelessToolCallCases() =>
    [
        ("tools.nameless-call-gets-tool-not-found-persisted-and-replayed", () => NamelessCall(
            [("toolu_1", "")],
            """[{"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"","input":{"path":"a.txt"}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"Tool  not found","is_error":true,"cache_control":{"type":"ephemeral"}}]}]""")),
        // Two calls without an id or a name share the id "": each gets its own result.
        ("tools.id-less-nameless-calls-share-the-empty-id", () => NamelessCall(
            [("", ""), ("", "")],
            """[{"role":"assistant","content":[{"type":"tool_use","id":"","name":"","input":{"path":"a.txt"}},{"type":"tool_use","id":"","name":"","input":{"path":"a.txt"}}]},{"role":"user","content":[{"type":"tool_result","tool_use_id":"","content":"Tool  not found","is_error":true},{"type":"tool_result","tool_use_id":"","content":"Tool  not found","is_error":true,"cache_control":{"type":"ephemeral"}}]}]""")),
    ];

    private static HttpResponseMessage AnthropicToolCalls((string Id, string Name)[] calls)
    {
        var body = Frame("message_start", new { type = "message_start", message = new { id = "msg_t", role = "assistant", model = "claude-sonnet-4-5", content = Array.Empty<object>(), usage = new { input_tokens = 3, output_tokens = 0 } } });
        for (var index = 0; index < calls.Length; index++)
            body += Frame("content_block_start", new { type = "content_block_start", index, content_block = new { type = "tool_use", id = calls[index].Id, name = calls[index].Name, input = new { } } })
                + Frame("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "input_json_delta", partial_json = "{\"path\":\"a.txt\"}" } })
                + Frame("content_block_stop", new { type = "content_block_stop", index });
        return Sse(body + Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "tool_use" }, usage = new { output_tokens = 2 } })
            + Frame("message_stop", new { type = "message_stop" }));
    }

    private static async Task NamelessCall((string Id, string Name)[] calls, string replay)
    {
        using var sandbox = new Sandbox("nameless-call");
        sandbox.Write("project/a.txt", "secret contents");
        sandbox.Respond = (_, index) => index == 0 ? AnthropicToolCalls(calls) : AnthropicText("done");
        var (code, stdout, stderr) = await sandbox.Run(["-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tools", "read", "go"]);
        Equal(0, code, "exit; " + stderr);
        Equal("done\n", stdout, "final text");
        Equal(2, sandbox.Requests.Count, "requests");
        // The second request replays the nameless calls and their error results.
        var messages = sandbox.Requests[1].Json.GetProperty("messages").EnumerateArray().Skip(1).Select(message => JsonNode.Parse(message.GetRawText())).ToArray();
        Check(JsonNode.DeepEquals(JsonNode.Parse(replay), new JsonArray(messages)), "replay: " + new JsonArray(messages.Select(m => m?.DeepClone()).ToArray()).ToJsonString());
        Check(!sandbox.Requests[1].Body!.Contains("secret contents", StringComparison.Ordinal), "no tool ran");
        // The session persists the assistant's nameless calls and one "Tool  not found" result for each.
        var entries = File.ReadLines(sandbox.SessionFiles().Single()).Select(line => JsonNode.Parse(line)!.AsObject())
            .Where(entry => entry["type"]?.GetValue<string>() == "message").Select(entry => entry["message"]!.AsObject()).ToArray();
        var assistant = entries.First(message => message["role"]!.GetValue<string>() == "assistant");
        var persistedCalls = assistant["content"]!.AsArray().Select(block => (block!["id"]!.GetValue<string>(), block["name"]!.GetValue<string>(), block["arguments"]!.ToJsonString())).ToArray();
        Check(persistedCalls.SequenceEqual(calls.Select(call => (call.Id, call.Name, "{\"path\":\"a.txt\"}"))), "persisted calls: " + assistant.ToJsonString());
        var results = entries.Where(message => message["role"]!.GetValue<string>() == "toolResult").ToArray();
        Equal(calls.Length, results.Length, "results");
        for (var index = 0; index < calls.Length; index++)
        {
            var result = results[index].DeepClone().AsObject();
            Check(result["timestamp"]?.GetValueKind() == JsonValueKind.Number, "result timestamp");
            result["timestamp"] = 0;
            Equal("""{"role":"toolResult","toolCallId":"ID","toolName":"","content":[{"type":"text","text":"Tool  not found"}],"details":{},"isError":true,"timestamp":0}""".Replace("ID", calls[index].Id, StringComparison.Ordinal),
                result.ToJsonString(), "persisted result " + index);
        }
    }
}
