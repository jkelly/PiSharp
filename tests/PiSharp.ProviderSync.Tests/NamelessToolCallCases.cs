// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/anthropic-messages.ts, openai-completions.ts,
// openai-responses-shared.ts, google-generative-ai.ts, google-vertex.ts, mistral-conversations.ts, pi-messages.ts.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.AI;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.AI.Protocols.PiMessages;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

// Owner decision 13: nameless and id-less tool calls are accepted exactly as far as Pi accepts them. Expected identities were captured by
// running @earendil-works/pi-ai@1.1.0 stream() for each API against a local HTTP server that returned these bodies, recording each
// toolcall_end toolCall and the done message. Every API finalizes the turn as toolUse with the id/name strings below (empty included);
// Google numbers its generated ids with Date.now(), so those are matched by shape. An identity upstream leaves undefined (a missing
// Anthropic id/name, Responses or Mistral name, pi-messages toolCall fields) has no string to keep and is not covered here.
internal static partial class Program
{
    private static void ToolIdentities(ChatResult result, string label, params (string Id, string Name, string Arguments)[] expected)
    {
        if (result.Message.StopReason != StopReason.ToolUse)
            throw new InvalidOperationException($"{label}: stop {result.Message.StopReason} " + (result.Message.ExtraProperties?.TryGet("errorMessage", out var error) == true ? error!.ToString() : ""));
        var actual = result.Message.Content.OfType<ToolCallContent>().Select(call => (call.Id, call.Name, call.Arguments.ToString())).ToArray();
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException($"{label}{Environment.NewLine}Expected {string.Join(";", expected)}{Environment.NewLine}Actual   {string.Join(";", actual)}");
    }

    private static async Task AnthropicNamelessToolCalls()
    {
        static string Body(object block)
        {
            var body = new StringBuilder();
            void Event(string type, object value) => body.Append("event: ").Append(type).Append('\n').Append(Data(value));
            Event("message_start", new { type = "message_start", message = new { id = "m1", type = "message", role = "assistant", model = "fixture-haiku", content = Array.Empty<object>(), stop_reason = (string?)null, usage = new { input_tokens = 1, output_tokens = 1 } } });
            Event("content_block_start", new { type = "content_block_start", index = 0, content_block = block });
            Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = "{\"a\":1}" } });
            Event("content_block_stop", new { type = "content_block_stop", index = 0 });
            Event("message_delta", new { type = "message_delta", delta = new { stop_reason = "tool_use" }, usage = new { output_tokens = 5 } });
            Event("message_stop", new { type = "message_stop" });
            return body.ToString();
        }
        foreach (var (label, block, id, name) in new (string, object, string, string)[]
        {
            ("name-empty", new { type = "tool_use", id = "toolu_1", name = "", input = new { } }, "toolu_1", ""),
            ("id-empty", new { type = "tool_use", id = "", name = "read", input = new { } }, "", "read"),
            ("both-empty", new { type = "tool_use", id = "", name = "", input = new { } }, "", ""),
        })
        {
            using var handler = new Handler(_ => Task.FromResult(EventStream(Body(block))));
            using var provider = NativeProviderFactory.CreateAnthropic(Haiku, new("https://api.anthropic.com/"), Key, new(MaximumTokens: 4096, ModelReasoning: false), null, handler);
            ToolIdentities(await new ChatClient(provider).CompleteAsync(new(Haiku, [Ask], 1)).WaitAsync(Deadline), "anthropic " + label, (id, name, "{\"a\":1}"));
        }
    }

    private static async Task CompletionsNamelessToolCalls()
    {
        var model = new ModelDescriptor("fixture", "openai-completions", "openrouter");
        static object Chunk(object delta, string? finish = null) => new { id = "c1", @object = "chat.completion.chunk", created = 1, model = "fixture", choices = new[] { new { index = 0, delta, finish_reason = finish } } };
        static string Body(params object[] calls) =>
            string.Concat(calls.Select(call => Data(Chunk(new { tool_calls = new[] { call } })))) + Data(Chunk(new { }, "tool_calls")) + "data: [DONE]\n\n";
        foreach (var (label, body, expected) in new (string, string, (string, string, string)[])[]
        {
            // A call that never gets an id keeps id: "" (ensureToolCallBlock id: toolCall.id || "").
            ("id-never", Body(new { index = 0, type = "function", function = new { name = "read", arguments = "{\"a\":1}" } }), [("", "read", "{\"a\":1}")]),
            // name = toolCall.function?.name ?? toolCall.custom?.name ?? "".
            ("name-missing", Body(new { index = 0, id = "call_1", type = "function", function = new { arguments = "{\"a\":1}" } }), [("call_1", "", "{\"a\":1}")]),
            ("name-empty", Body(new { index = 0, id = "call_1", type = "function", function = new { name = "", arguments = "{}" } }), [("call_1", "", "{}")]),
            ("both-missing", Body(new { index = 0, function = new { arguments = "{}" } }), [("", "", "{}")]),
            ("function-missing", Body(new { index = 0, id = "call_1", type = "function" }), [("call_1", "", "{}")]),
            // Two id-less calls share the id "".
            ("two-nameless", Body(new { index = 0, function = new { arguments = "{}" } }, new { index = 1, function = new { arguments = "{\"b\":2}" } }),
                [("", "", "{}"), ("", "", "{\"b\":2}")]),
            // A delta with neither an index nor an id matches no block: each opens its own.
            ("no-index-no-id", Body(new { function = new { name = "read", arguments = "{\"a\"" } }, new { function = new { arguments = ":1}" } }),
                [("", "read", "{}"), ("", "", "{}")]),
        })
        {
            using var handler = new Handler(_ => Task.FromResult(EventStream(body)));
            using var provider = NativeProviderFactory.CreateCompletions(model, new("https://openrouter.ai/api/v1/chat/completions"), Key, new(Reasoning: false),
                new(MaxTokensField: "max_tokens", SupportsStore: false), handler);
            ToolIdentities(await new ChatClient(provider).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline), "completions " + label, expected);
        }
    }

    private static async Task ResponsesNamelessToolCalls()
    {
        var model = new ModelDescriptor("gpt-fixture", "openai-responses", "openai");
        foreach (var (label, item, id, name) in new (string, Dictionary<string, object>, string, string)[]
        {
            ("name-empty", new() { ["id"] = "fc1", ["call_id"] = "call_1", ["name"] = "" }, "call_1|fc1", ""),
            // id: `${item.call_id}|${item.id}`: a missing part reads "undefined".
            ("call-id-missing", new() { ["id"] = "fc1", ["name"] = "read" }, "undefined|fc1", "read"),
            ("item-id-missing", new() { ["call_id"] = "call_1", ["name"] = "read" }, "call_1|undefined", "read"),
            ("call-id-empty", new() { ["id"] = "fc1", ["call_id"] = "", ["name"] = "read" }, "|fc1", "read"),
            ("all-empty", new() { ["id"] = "", ["call_id"] = "", ["name"] = "" }, "|", ""),
        })
        {
            Dictionary<string, object> Item(string arguments) => new(item) { ["type"] = "function_call", ["arguments"] = arguments };
            var delta = new Dictionary<string, object> { ["type"] = "response.function_call_arguments.delta", ["output_index"] = 0, ["delta"] = "{\"a\":1}" };
            if (item.TryGetValue("id", out var itemId)) delta["item_id"] = itemId;
            var body = Data(new { type = "response.created", response = new { id = "r1" } }) +
                Data(new { type = "response.output_item.added", output_index = 0, item = Item("") }) + Data(delta) +
                Data(new { type = "response.output_item.done", output_index = 0, item = Item("{\"a\":1}") }) +
                Data(new { type = "response.completed", response = new { id = "r1", status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 1, output_tokens = 1, total_tokens = 2 } } });
            using var handler = new Handler(_ => Task.FromResult(EventStream(body)));
            using var provider = NativeProviderFactory.CreateResponses(model, new("https://api.openai.com/v1/responses"), Key, new(false), null, handler);
            ToolIdentities(await new ChatClient(provider).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline), "responses " + label, (id, name, "{\"a\":1}"));
        }
    }

    private static async Task GoogleNamelessToolCalls()
    {
        // google-generative-ai.ts and google-vertex.ts: name = functionCall.name || "", and an id missing or already used is generated as
        // `${functionCall.name}_${Date.now()}_${++counter}`, so a nameless call's id reads "_<ms>_<n>" or "undefined_<ms>_<n>".
        foreach (var vertex in new[] { false, true })
            foreach (var (label, parts, ids) in new (string, string, string[])[]
            {
                ("name-empty", """{"functionCall":{"name":"","args":{}}}""", ["^_\\d+_\\d+$"]),
                ("name-missing", """{"functionCall":{"args":{"a":1}}}""", ["^undefined_\\d+_\\d+$"]),
                ("non-object-call", """{"functionCall":"x"}""", ["^undefined_\\d+_\\d+$"]),
                ("name-zero", """{"functionCall":{"name":0,"args":{}}}""", ["^0_\\d+_\\d+$"]),
                ("two-nameless", """{"functionCall":{"name":"","args":{}}},{"functionCall":{"name":"","args":{}}}""", ["^_\\d+_\\d+$", "^_\\d+_\\d+$"]),
            })
            {
                var body = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[" + parts + "]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":1,\"candidatesTokenCount\":1,\"totalTokenCount\":2}}\n\n";
                var message = await GoogleSegments([body], vertex);
                var where = (vertex ? "vertex " : "google ") + label;
                if (message.StopReason != StopReason.ToolUse) throw new InvalidOperationException(where + ": stop " + message.StopReason);
                var calls = message.Content.OfType<ToolCallContent>().ToArray();
                Equal(ids.Length, calls.Length);
                for (var index = 0; index < calls.Length; index++)
                {
                    Equal("", calls[index].Name);
                    if (!Regex.IsMatch(calls[index].Id, ids[index])) throw new InvalidOperationException(where + ": id " + calls[index].Id);
                }
                if (calls.Select(call => call.Id).Distinct().Count() != calls.Length) throw new InvalidOperationException(where + ": generated ids repeat");
                Equal(label == "name-missing" ? "{\"a\":1}" : "{}", calls[0].Arguments.ToString());
            }
    }

    private static async Task MistralNamelessToolCalls()
    {
        var model = new ModelDescriptor("mistral-fixture", "mistral-conversations", "mistral");
        var endpoint = new Uri("https://api.mistral.ai/");
        foreach (var (label, call, id, name) in new[]
        {
            ("name-empty", "{\"id\":\"abcdefghi\",\"index\":0,\"function\":{\"name\":\"\",\"arguments\":\"{}\"}}", "abcdefghi", ""),
            // A missing, empty or "null" id is derived from the index: deriveMistralToolCallId("toolcall:0", 0).
            ("id-empty", "{\"id\":\"\",\"index\":0,\"function\":{\"name\":\"read\",\"arguments\":\"{}\"}}", "toolcall0", "read"),
        })
        {
            var body = "data: {\"id\":\"x\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[" + call + "]},\"finish_reason\":null}]}\n\n" +
                "data: {\"id\":\"x\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n";
            using var handler = new Handler(_ => Task.FromResult(EventStream(body)));
            using var provider = NativeProviderFactory.CreateMistral(model, endpoint, Key, new MistralTextOptions(endpoint, true, new(0, 0, 0, 0), "offline-fixture"), handler);
            ToolIdentities(await new ChatClient(provider).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline), "mistral " + label, (id, name, "{}"));
        }
    }

    private static async Task PiMessagesNamelessToolCalls()
    {
        var model = new ModelDescriptor("inert", "pi-messages", "authored");
        var metadata = JsonData.Parse("""{"id":"inert","api":"pi-messages","provider":"authored","baseUrl":"https://pi-messages.invalid/v1"}""");
        const string usage = """{"input":1,"output":1,"cacheRead":0,"cacheWrite":0,"totalTokens":2,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}}""";
        foreach (var (label, start, end, id, name) in new[]
        {
            ("empty", "\"id\":\"\",\"toolName\":\"\"", "\"id\":\"\",\"name\":\"\"", "", ""),
            // toolcall_end Object.assign's the final toolCall over the partial block.
            ("filled-at-end", "\"id\":\"\",\"toolName\":\"\"", "\"id\":\"c1\",\"name\":\"read\"", "c1", "read"),
            ("nameless-kept", "\"id\":\"c1\",\"toolName\":\"\"", "", "c1", ""),
        })
        {
            var toolCall = "{\"type\":\"toolCall\"" + (end.Length == 0 ? "" : "," + end) + ",\"arguments\":{}}";
            var body = string.Concat(new[]
            {
                """{"type":"start"}""", "{\"type\":\"toolcall_start\",\"contentIndex\":0," + start + "}", """{"type":"toolcall_delta","contentIndex":0,"delta":"{}"}""",
                "{\"type\":\"toolcall_end\",\"contentIndex\":0,\"toolCall\":" + toolCall + "}", "{\"type\":\"done\",\"reason\":\"toolUse\",\"usage\":" + usage + "}",
            }.Select(frame => "data: " + frame + "\n\n"));
            using var handler = new Handler(_ => Task.FromResult(EventStream(body)));
            using var client = new HttpClient(handler);
            var transport = new PiMessagesHttpSseTransport(client, model, new PiMessagesOptions(metadata, Key) { EnvironmentLookup = _ => null });
            ToolIdentities(await new ChatClient(transport).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline), "pi-messages " + label, (id, name, "{}"));
        }
    }

    // The next request replays a same-model assistant turn of nameless / id-less calls and their "Tool  not found" results. Expected
    // bodies were captured by running pi-ai@1.1.0 stream() with that history against a local HTTP server and reading the request.
    private static async Task NamelessToolCallReplays()
    {
        static ReplayHistory History(ModelDescriptor model, params (string Id, string Name)[] calls)
        {
            var assistant = new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(calls.Select(call => (JsonNode)new JsonObject
                { ["type"] = "toolCall", ["id"] = call.Id, ["name"] = call.Name, ["arguments"] = new JsonObject() }).ToArray()),
                ["api"] = model.Api, ["provider"] = model.Provider, ["model"] = model.Id,
                ["usage"] = JsonNode.Parse("""{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}}"""),
                ["stopReason"] = "toolUse", ["timestamp"] = 2 };
            var entries = new List<TranscriptEntry> { Ask, Entry(assistant.ToJsonString()) };
            foreach (var call in calls)
                entries.Add(Entry(new JsonObject { ["role"] = "toolResult", ["toolCallId"] = call.Id, ["toolName"] = call.Name,
                    ["content"] = JsonNode.Parse("""[{"type":"text","text":"Tool  not found"}]"""), ["details"] = new JsonObject(), ["isError"] = true, ["timestamp"] = 3 }.ToJsonString()));
            return new([.. entries]);
        }
        static async Task Replay(ChatClient chat, ModelDescriptor model, ReplayHistory history, Func<string?> body, string field, string expected, string label)
        {
            _ = await chat.CompleteAsync(new(model, history.Entries, 4)).WaitAsync(Deadline);
            var sent = body() ?? throw new InvalidOperationException(label + ": no request");
            var actual = new JsonArray(JsonNode.Parse(sent)![field]!.AsArray().Skip(1).Select(node => node?.DeepClone()).ToArray());
            if (!JsonNode.DeepEquals(JsonNode.Parse(expected), actual))
                throw new InvalidOperationException($"{label}{Environment.NewLine}Expected {expected}{Environment.NewLine}Actual   {actual.ToJsonString()}");
        }
        string? captured = null;
        Handler Capture() => new(async request => { captured = await request.Content!.ReadAsStringAsync(); return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("{}") }; });

        var completions = new ModelDescriptor("fixture", "openai-completions", "openrouter");
        using (var handler = Capture())
        using (var provider = NativeProviderFactory.CreateCompletions(completions, new("https://openrouter.ai/api/v1/chat/completions"), Key, new(Reasoning: false),
            new(MaxTokensField: "max_tokens", SupportsStore: false), handler))
            await Replay(new ChatClient(provider), completions, History(completions, ("", ""), ("", "")), () => captured, "messages",
                """[{"role":"assistant","content":null,"tool_calls":[{"id":"","type":"function","function":{"name":"","arguments":"{}"}},{"id":"","type":"function","function":{"name":"","arguments":"{}"}}]},{"role":"tool","content":"Tool  not found","tool_call_id":""},{"role":"tool","content":"Tool  not found","tool_call_id":""}]""",
                "completions");

        captured = null;
        var responses = new ModelDescriptor("gpt-fixture", "openai-responses", "openai");
        using (var handler = Capture())
        using (var provider = NativeProviderFactory.CreateResponses(responses, new("https://api.openai.com/v1/responses"), Key, new(false), null, handler))
            await Replay(new ChatClient(provider), responses, History(responses, ("call_1|fc_1", ""), ("|", "")), () => captured, "input",
                """[{"type":"function_call","id":"fc_1","call_id":"call_1","name":"","arguments":"{}"},{"type":"function_call","call_id":"","name":"","arguments":"{}"},{"type":"function_call_output","call_id":"call_1","output":"Tool  not found"},{"type":"function_call_output","call_id":"","output":"Tool  not found"}]""",
                "responses");

        captured = null;
        using (var handler = Capture())
        using (var client = new HttpClient(handler))
        {
            var transport = new GoogleGenerativeAIHttpTransport(client, Gemini, new GoogleGenerativeAIOptions(GoogleMetadata(), Key));
            await Replay(new ChatClient(transport), Gemini, History(Gemini, ("_1_1", "")), () => captured, "contents",
                """[{"parts":[{"functionCall":{"args":{},"id":"_1_1","name":""}}],"role":"model"},{"parts":[{"functionResponse":{"name":"","response":{"error":"Tool  not found"},"id":"_1_1"}}],"role":"user"}]""",
                "google");
        }

        captured = null;
        var mistral = new ModelDescriptor("mistral-fixture", "mistral-conversations", "mistral");
        var endpoint = new Uri("https://api.mistral.ai/");
        using (var handler = Capture())
        using (var provider = NativeProviderFactory.CreateMistral(mistral, endpoint, Key, new MistralTextOptions(endpoint, true, new(0, 0, 0, 0), "offline-fixture"), handler))
            await Replay(new ChatClient(provider), mistral, History(mistral, ("abcdefghi", "")), () => captured, "messages",
                """[{"role":"assistant","prefix":false,"tool_calls":[{"id":"abcdefghi","type":"function","function":{"name":"","arguments":"{}"},"index":0}]},{"role":"tool","name":"","content":[{"type":"text","text":"[tool error] Tool  not found"}],"tool_call_id":"abcdefghi"}]""",
                "mistral");
    }

    private sealed record ReplayHistory(System.Collections.Immutable.ImmutableArray<TranscriptEntry> Entries);
}
