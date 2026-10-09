// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/anthropic-messages.ts, openai-completions.ts,
// openai-responses-shared.ts, mistral-conversations.ts, google-generative-ai.ts, pi-messages.ts.
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.AI.Protocols.PiMessages;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

// Expected arguments were captured by running @earendil-works/pi-ai@1.1.0 stream() for each API against a local HTTP server that
// returned these SSE bodies, recording JSON.stringify(toolCall.arguments) at toolcall_end. Malformed arguments never fail the turn:
// each API finalizes with parseStreamingJson (or, for Google and pi-messages, takes the JSON value the chunk carried).
internal static partial class Program
{
    // (name, first fragment, second fragment, final arguments as JSON.stringify writes them)
    private static readonly (string Name, string First, string Second, string Expected)[] FinalArgumentScenarios =
    [
        ("duplicates", "{\"path\":\"a.txt\",\"limit\":1.0,", "\"path\":\"b.txt\",\"2\":0,\"1\":1}", "{\"1\":1,\"2\":0,\"path\":\"b.txt\",\"limit\":1}"),
        ("bad-escape-truncated", "{\"path\":\"C:\\Users\\x\",", "\"n\":5", "{}"),
        ("array", "[1,", "2", "[1,2]"),
        ("string", "\"just ", "text\"", "\"just text\""),
        ("garbage", "not ", "json", "{}"),
        ("null-literal", "nu", "ll", "null"),
        ("partial-null", "nu", "l", "{}"),
        ("control-character", "{\"a\":\"x\ny", "\"}", "{\"a\":\"x\\ny\"}"),
        ("number", "4", "2", "42"),
        ("proto-assignment", "{\"__proto__\":{\"x\":1},", "\"b\":2", "{\"b\":2}"),
        ("empty", "", "", "{}"),
        ("nan", "Na", "N", "null"),
    ];

    private static HttpResponseMessage EventStream(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    private static string Data(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";

    private static void ToolArguments(ChatResult result, string expected, string label)
    {
        if (result.Message.StopReason != StopReason.ToolUse)
            throw new InvalidOperationException($"{label}: stop {result.Message.StopReason} " + (result.Message.ExtraProperties?.TryGet("errorMessage", out var error) == true ? error!.ToString() : ""));
        var actual = result.Message.Content.OfType<ToolCallContent>().Single().Arguments.ToString();
        if (actual != expected) throw new InvalidOperationException($"{label}{Environment.NewLine}Expected {expected}{Environment.NewLine}Actual   {actual}");
    }

    private static async Task AnthropicFinalArguments()
    {
        foreach (var (name, first, second, expected) in FinalArgumentScenarios)
        {
            var body = new StringBuilder();
            void Event(string type, object value) => body.Append("event: ").Append(type).Append('\n').Append(Data(value));
            Event("message_start", new { type = "message_start", message = new { id = "m1", type = "message", role = "assistant", model = "fixture-haiku", content = Array.Empty<object>(), stop_reason = (string?)null, usage = new { input_tokens = 1, output_tokens = 1 } } });
            Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id = "toolu_1", name = "read", input = new { } } });
            foreach (var piece in new[] { first, second })
                if (piece.Length > 0 || name == "empty") Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = piece } });
            Event("content_block_stop", new { type = "content_block_stop", index = 0 });
            Event("message_delta", new { type = "message_delta", delta = new { stop_reason = "tool_use" }, usage = new { output_tokens = 5 } });
            Event("message_stop", new { type = "message_stop" });
            using var handler = new Handler(_ => Task.FromResult(EventStream(body.ToString())));
            using var provider = NativeProviderFactory.CreateAnthropic(Haiku, new("https://api.anthropic.com/"), Key, new(MaximumTokens: 4096, ModelReasoning: false), null, handler);
            ToolArguments(await new ChatClient(provider).CompleteAsync(new(Haiku, [Ask], 1)).WaitAsync(Deadline), expected, "anthropic " + name);
        }
    }

    private static async Task CompletionsFinalArguments()
    {
        var model = new ModelDescriptor("fixture", "openai-completions", "openrouter");
        foreach (var (name, first, second, expected) in FinalArgumentScenarios)
        {
            static object Chunk(object delta, string? finish = null) => new { id = "c1", @object = "chat.completion.chunk", created = 1, model = "fixture", choices = new[] { new { index = 0, delta, finish_reason = finish } } };
            var body = Data(Chunk(new { role = "assistant", tool_calls = new[] { new { index = 0, id = "call_1", type = "function", function = new { name = "read", arguments = first } } } })) +
                Data(Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = second } } } })) + Data(Chunk(new { }, "tool_calls")) + "data: [DONE]\n\n";
            using var handler = new Handler(_ => Task.FromResult(EventStream(body)));
            using var provider = NativeProviderFactory.CreateCompletions(model, new("https://openrouter.ai/api/v1/chat/completions"), Key, new(Reasoning: false),
                new(MaxTokensField: "max_tokens", SupportsStore: false), handler);
            ToolArguments(await new ChatClient(provider).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline), expected, "completions " + name);
        }
    }

    private static async Task ResponsesFinalArguments()
    {
        var model = new ModelDescriptor("gpt-fixture", "openai-responses", "openai");
        // output_item.done: parseStreamingJson(item.arguments || partialJson || "{}"), with the full text and with "".
        foreach (var itemArguments in new[] { true, false })
            foreach (var (name, first, second, expected) in FinalArgumentScenarios)
            {
                var body = Data(new { type = "response.created", response = new { id = "r1" } }) +
                    Data(new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id = "fc1", call_id = "call_1", name = "read", arguments = "" } }) +
                    Data(new { type = "response.function_call_arguments.delta", output_index = 0, item_id = "fc1", delta = first }) +
                    Data(new { type = "response.function_call_arguments.delta", output_index = 0, item_id = "fc1", delta = second }) +
                    Data(new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", id = "fc1", call_id = "call_1", name = "read", arguments = itemArguments ? first + second : "" } }) +
                    Data(new { type = "response.completed", response = new { id = "r1", status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 1, output_tokens = 1, total_tokens = 2 } } });
                using var handler = new Handler(_ => Task.FromResult(EventStream(body)));
                using var provider = NativeProviderFactory.CreateResponses(model, new("https://api.openai.com/v1/responses"), Key, new(false), null, handler);
                ToolArguments(await new ChatClient(provider).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline), expected, $"responses {(itemArguments ? "item" : "partial")} {name}");
            }
    }

    private static async Task MistralFinalArguments()
    {
        var model = new ModelDescriptor("mistral-fixture", "mistral-conversations", "mistral");
        var endpoint = new Uri("https://api.mistral.ai/");
        static string Body(params string[] functions) =>
            string.Concat(functions.Select(function => "data: {\"id\":\"x\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"abcdefghi\",\"index\":0,\"function\":" + function + "}]},\"finish_reason\":null}]}\n\n")) +
            "data: {\"id\":\"x\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n";
        async Task Run(string body, string expected, string label)
        {
            using var handler = new Handler(_ => Task.FromResult(EventStream(body)));
            using var provider = NativeProviderFactory.CreateMistral(model, endpoint, Key, new MistralTextOptions(endpoint, true, new(0, 0, 0, 0), "offline-fixture"), handler);
            ToolArguments(await new ChatClient(provider).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline), expected, "mistral " + label);
        }
        foreach (var (name, first, second, expected) in FinalArgumentScenarios)
            await Run(Body("{\"name\":\"read\",\"arguments\":" + JsonSerializer.Serialize(first) + "}", "{\"name\":\"read\",\"arguments\":" + JsonSerializer.Serialize(second) + "}"), expected, name);
        // typeof arguments === "string" ? arguments : JSON.stringify(arguments || {})
        foreach (var (name, value, expected) in new[]
        {
            ("number", "5", "5"), ("array", "[1,\"a\"]", "[1,\"a\"]"), ("false", "false", "{}"), ("zero", "0", "{}"), ("null", "null", "{}"),
            ("object", "{\"b\":1,\"a\":[2],\"1\":3}", "{\"1\":3,\"b\":1,\"a\":[2]}"), ("json-string", "\"{\\\"q\\\":1}\"", "{\"q\":1}"),
        })
            await Run(Body("{\"name\":\"read\",\"arguments\":" + value + "}"), expected, "value " + name);
        await Run(Body("{\"name\":\"read\"}"), "{}", "value missing");
    }

    private static async Task GoogleFinalArguments()
    {
        // arguments = part.functionCall.args ?? {}: whatever JSON value the chunk carried.
        foreach (var (name, args, expected) in new (string, string?, string)[]
        {
            ("array", "[1,2]", "[1,2]"), ("string", "\"s\"", "\"s\""), ("number", "5", "5"), ("null", "null", "{}"), ("missing", null, "{}"),
            ("false", "false", "false"), ("object", "{\"x\":1.50,\"2\":1}", "{\"2\":1,\"x\":1.5}"),
        })
        {
            var call = args is null ? "{\"name\":\"read\"}" : "{\"name\":\"read\",\"args\":" + args + "}";
            var body = "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"functionCall\":" + call + "}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":1,\"candidatesTokenCount\":1,\"totalTokenCount\":2}}\n\n";
            using var handler = new Handler(_ => Task.FromResult(EventStream(body)));
            using var client = new HttpClient(handler);
            var transport = new GoogleGenerativeAIHttpTransport(client, Gemini, new GoogleGenerativeAIOptions(GoogleMetadata(), Key));
            ToolArguments(await new ChatClient(transport).CompleteAsync(new(Gemini, [Ask], 1)).WaitAsync(Deadline), expected, "google " + name);
        }
    }

    private static async Task PiMessagesFinalArguments()
    {
        // toolcall_end: Object.assign(partial block, event.toolCall). Arguments it carries replace the streamed parseStreamingJson value
        // whatever their kind; absent arguments keep it.
        var model = new ModelDescriptor("inert", "pi-messages", "authored");
        var metadata = JsonData.Parse("""{"id":"inert","api":"pi-messages","provider":"authored","baseUrl":"https://pi-messages.invalid/v1"}""");
        const string usage = """{"input":1,"output":1,"cacheRead":0,"cacheWrite":0,"totalTokens":2,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}}""";
        foreach (var (name, final, expected) in new (string, string?, string)[]
        {
            ("array", "[1]", "[1]"), ("string", "\"s\"", "\"s\""), ("null", "null", "null"), ("number", "7", "7"), ("absent", null, "{\"v\":1,\"w\":[2]}"),
            ("object", "{\"v\":1}", "{\"v\":1}"),
        })
        {
            var toolCall = "{\"type\":\"toolCall\",\"id\":\"c1\",\"name\":\"read\"" + (final is null ? "" : ",\"arguments\":" + final) + "}";
            var body = string.Concat(new[]
            {
                """{"type":"start"}""", """{"type":"toolcall_start","contentIndex":0,"id":"c1","toolName":"read"}""",
                """{"type":"toolcall_delta","contentIndex":0,"delta":"{\"v\":1,\"w\":[2"}""",
                "{\"type\":\"toolcall_end\",\"contentIndex\":0,\"toolCall\":" + toolCall + "}", "{\"type\":\"done\",\"reason\":\"toolUse\",\"usage\":" + usage + "}",
            }.Select(frame => "data: " + frame + "\n\n"));
            using var handler = new Handler(_ => Task.FromResult(EventStream(body)));
            using var client = new HttpClient(handler);
            var transport = new PiMessagesHttpSseTransport(client, model, new PiMessagesOptions(metadata, Key) { EnvironmentLookup = _ => null });
            ToolArguments(await new ChatClient(transport).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline), expected, "pi-messages " + name);
        }
    }
}
