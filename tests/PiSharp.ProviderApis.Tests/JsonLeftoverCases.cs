// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/bedrock-converse-stream.ts (convertMessages,
// sanitizeBedrockDocument) and packages/ai/src/api/openai-codex-responses.ts.
using PiSharp.AI;
using PiSharp.Contracts;

internal static partial class Program
{
    private static readonly (string Id, Func<Task> Run)[] JsonLeftoverCases =
    [
        ("bedrock.tool-arguments-keep-lone-surrogates-and-deep-values-escaped", BedrockLoneSurrogateArguments),
        ("codex.events-and-arguments-hold-a-thousand-levels", CodexDeepValues),
    ];

    // bedrock-converse-stream.ts sends a tool call's arguments through sanitizeBedrockDocument (empty keys dropped, nothing else changed)
    // and @aws-sdk/client-bedrock-runtime 3.1127.0 serializes the document with JSON.stringify: a lone surrogate of a name or a string
    // travels as its lowercase escape (captured from the SDK's serializer in Node 22: {"k\udc00":"x\ud800y","n":[1,"\udfff"]}).
    // Formerly the lone surrogates were dropped, and arguments deeper than 64 levels failed the request.
    private static async Task BedrockLoneSurrogateArguments()
    {
        var deep = new string('[', 899) + "1" + new string(']', 899);
        var transcript = System.Collections.Immutable.ImmutableArray.Create(
            Entry("""{"role":"user","content":"Go","timestamp":1}"""),
            Entry("{\"role\":\"assistant\",\"content\":[{\"type\":\"toolCall\",\"id\":\"toolu_1\",\"name\":\"read\",\"arguments\":{\"path\":\"a\\ud800\",\"k\\udc00\":[\"\\udfff\",{\"\":1,\"n\":2}],\"deep\":" + deep + "}}],\"api\":\"bedrock-converse-stream\",\"provider\":\"amazon-bedrock\",\"model\":\"amazon.nova-lite-v1:0\",\"usage\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":0,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}},\"stopReason\":\"toolUse\",\"timestamp\":2}"),
            Entry("""{"role":"toolResult","toolCallId":"toolu_1","toolName":"read","content":[{"type":"text","text":"ok"}],"isError":false,"timestamp":3}"""));
        var nova = Bedrock(NovaRow);
        using var request = await nova.Transport.CreateRequestAsync(new(nova.Model, transcript, 4));
        // Members in the SDK's schema order (messages, then inferenceConfig; role, then content; toolUseId, name, input).
        Equal("{\"messages\":[{\"role\":\"user\",\"content\":[{\"text\":\"Go\"}]}," +
            "{\"role\":\"assistant\",\"content\":[{\"toolUse\":{\"toolUseId\":\"toolu_1\",\"name\":\"read\",\"input\":{\"path\":\"a\\ud800\",\"k\\udc00\":[\"\\udfff\",{\"n\":2}],\"deep\":" + deep + "}}}]}," +
            "{\"role\":\"user\",\"content\":[{\"toolResult\":{\"toolUseId\":\"toolu_1\",\"content\":[{\"text\":\"ok\"}],\"status\":\"success\"}}]}],\"inferenceConfig\":{\"maxTokens\":10000}}",
            await request.Content!.ReadAsStringAsync(), "bedrock body with lone surrogates and deep arguments");
    }

    // openai-codex-responses.ts JSON.parse reads every SSE event, whatever its depth (formerly a 64-level bound failed the stream), and
    // the function call's arguments are parseStreamingJson's value at any depth Pi holds.
    private static async Task CodexDeepValues()
    {
        var (transport, http, model, _) = Codex();
        var deep = new string('[', 899) + "1" + new string(']', 899);
        var arguments = System.Text.Json.JsonSerializer.Serialize("{\"path\":\"a\",\"deep\":" + deep + "}");
        http.OnUrl("https://chatgpt.com/", _ => Sse(
            """{"type":"response.created","response":{"id":"r1","status":"in_progress"}}""",
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"read","arguments":""}}""",
            "{\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"item_id\":\"fc_1\",\"delta\":" + arguments + "}",
            "{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_1\",\"name\":\"read\",\"arguments\":" + arguments + "}}",
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"r1\",\"status\":\"completed\",\"output\":[],\"metadata\":{\"deep\":" + new string('[', 200) + new string(']', 200) + "}}}"));
        var events = await Collect(transport, new(model, [Entry("""{"role":"user","content":"Hi","timestamp":2}""")], 9));
        Check(events[^1] is StreamDone, "deep events complete: " + (events[^1] is StreamError ? ErrorMessage(events[^1]) : events[^1].GetType().Name));
        var call = ((StreamDone)events[^1]).Message.Content.OfType<ToolCallContent>().Single();
        Equal("{\"path\":\"a\",\"deep\":" + deep + "}", call.Arguments.ToString(), "deep arguments");
    }
}
