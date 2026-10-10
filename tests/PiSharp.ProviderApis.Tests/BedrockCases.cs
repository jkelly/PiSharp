using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.Bedrock;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

internal static partial class Program
{
    private const string SonnetRow = """{"id":"anthropic.claude-sonnet-4-5-20250929-v1:0","name":"Claude Sonnet 4.5","api":"bedrock-converse-stream","provider":"amazon-bedrock","baseUrl":"https://bedrock-runtime.us-east-1.amazonaws.com","reasoning":true,"input":["text","image"],"cost":{"input":3,"output":15,"cacheRead":0.3,"cacheWrite":3.75},"contextWindow":200000,"maxTokens":64000,"compat":{"supportsStrictMode":true},"type":"chat"}""";
    private const string OpusRow = """{"id":"global.anthropic.claude-opus-4-8","name":"Claude Opus 4.8 (Global)","api":"bedrock-converse-stream","provider":"amazon-bedrock","baseUrl":"https://bedrock-runtime.us-east-1.amazonaws.com","reasoning":true,"input":["text","image"],"cost":{"input":5,"output":25,"cacheRead":0.5,"cacheWrite":6.25},"contextWindow":1000000,"maxTokens":128000,"thinkingLevelMap":{"xhigh":"xhigh","max":"max"},"type":"chat"}""";
    private const string GptOssRow = """{"id":"openai.gpt-oss-120b-1:0","name":"gpt-oss-120b","api":"bedrock-converse-stream","provider":"amazon-bedrock","baseUrl":"https://bedrock-runtime.us-east-1.amazonaws.com","reasoning":true,"input":["text"],"cost":{"input":0.15,"output":0.6,"cacheRead":0,"cacheWrite":0},"contextWindow":131072,"maxTokens":128000,"compat":{"supportsStrictMode":true},"type":"chat"}""";
    private const string NovaRow = """{"id":"amazon.nova-lite-v1:0","name":"Nova Lite","api":"bedrock-converse-stream","provider":"amazon-bedrock","baseUrl":"https://bedrock-runtime.us-east-1.amazonaws.com","reasoning":false,"input":["text","image"],"cost":{"input":0.06,"output":0.24,"cacheRead":0.015,"cacheWrite":0.06},"contextWindow":300000,"maxTokens":10000,"type":"chat"}""";
    // Authored fixture row in the catalog's shape for a GPT model on Bedrock (thinkingLevelMap minimal -> low).
    private const string GptRow = """{"id":"openai.gpt-5.5","name":"GPT-5.5","api":"bedrock-converse-stream","provider":"amazon-bedrock","baseUrl":"https://bedrock-runtime.us-east-1.amazonaws.com","reasoning":true,"input":["text"],"cost":{"input":1,"output":2,"cacheRead":0,"cacheWrite":0},"contextWindow":400000,"maxTokens":128000,"thinkingLevelMap":{"minimal":"low"},"type":"chat"}""";

    private static readonly DateTimeOffset BedrockTime = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed record BedrockFixture(BedrockConverseStreamTransport Transport, FakeHttp Http, ModelDescriptor Model, List<TimeSpan> Delays);

    private static BedrockFixture Bedrock(string row, Dictionary<string, string?>? env = null, BedrockConverseOptions? options = null, FakeHttp? http = null,
        string? home = null, string? defaultLevel = null)
    {
        var data = JsonData.Parse(row);
        var model = new ModelDescriptor(data.Value.GetProperty("id").GetString()!, "bedrock-converse-stream", "amazon-bedrock");
        http ??= new FakeHttp();
        var variables = env ?? new Dictionary<string, string?> { ["AWS_ACCESS_KEY_ID"] = "AKIDEXAMPLE", ["AWS_SECRET_ACCESS_KEY"] = "secret-example" };
        var delays = new List<TimeSpan>();
        var environment = new AwsEnvironment(name => variables.GetValueOrDefault(name), home ?? Temp("bedrock-home"),
            new HttpMessageInvoker(http, disposeHandler: false), new FixedTime(BedrockTime.ToUnixTimeMilliseconds()));
        options = (options ?? new BedrockConverseOptions()) with { Delay = (wait, _) => { delays.Add(wait); return Task.CompletedTask; }, Random = () => 0.5 };
        var transport = new BedrockConverseStreamTransport(new HttpClient(http, disposeHandler: false), model, data, options, environment, defaultLevel);
        return new(transport, http, model, delays);
    }

    private static string ClaudeTranscriptModel => "anthropic.claude-sonnet-4-5-20250929-v1:0";

    private static ImmutableArray<TranscriptEntry> ClaudeTranscript() =>
    [
        Entry("""{"role":"system","content":"You are helpful.","sections":{"b":"Section B","1":"Section one"},"toolsAdded":[{"name":"read","description":"Read a file","parameters":{"type":"object","properties":{"path":{"type":"string"},"limit":{"type":"number"}},"required":["path"]},"constrainedSampling":{"type":"json_schema","strict":"prefer"}}],"timestamp":1}"""),
        Entry("""{"role":"user","content":[{"type":"text","text":"Hi"},{"type":"image","data":"AAEC","mimeType":"image/png"}],"timestamp":2}"""),
        Entry($$$"""{"role":"assistant","content":[{"type":"thinking","thinking":"Let me think","thinkingSignature":"sig-1"},{"type":"text","text":"Calling a tool"},{"type":"toolCall","id":"toolu_1","name":"read","arguments":{"path":"a.txt"}}],"api":"bedrock-converse-stream","provider":"amazon-bedrock","model":"{{{ClaudeTranscriptModel}}}","usage":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}},"stopReason":"toolUse","timestamp":3}"""),
        Entry("""{"role":"toolResult","toolCallId":"toolu_1","toolName":"read","content":[{"type":"text","text":"file body"},{"type":"image","data":"AAEC","mimeType":"image/jpeg"}],"isError":false,"timestamp":4}"""),
        Entry("""{"role":"user","content":"Next","timestamp":5}""")
    ];

    private static async Task BedrockClaudeRequest()
    {
        var fixture = Bedrock(SonnetRow);
        using var request = await fixture.Transport.CreateRequestAsync(new(fixture.Model, ClaudeTranscript(), 10) { ThinkingLevel = "medium" });
        Equal("https://bedrock-runtime.us-east-1.amazonaws.com/model/anthropic.claude-sonnet-4-5-20250929-v1%3A0/converse-stream", request.RequestUri!.AbsoluteUri, "url");
        var body = await request.Content!.ReadAsStringAsync();
        JsonEqual("""
            {"additionalModelRequestFields":{"thinking":{"type":"enabled","budget_tokens":8192,"display":"summarized"},"anthropic_beta":["interleaved-thinking-2025-05-14"]},
             "inferenceConfig":{"maxTokens":64000},
             "messages":[
               {"content":[{"text":"Hi"},{"image":{"format":"png","source":{"bytes":"AAEC"}}}],"role":"user"},
               {"content":[{"reasoningContent":{"reasoningText":{"text":"Let me think","signature":"sig-1"}}},{"text":"Calling a tool"},{"toolUse":{"input":{"path":"a.txt"},"name":"read","toolUseId":"toolu_1"}}],"role":"assistant"},
               {"content":[{"toolResult":{"content":[{"text":"file body"},{"image":{"format":"jpeg","source":{"bytes":"AAEC"}}}],"status":"success","toolUseId":"toolu_1"}}],"role":"user"},
               {"content":[{"text":"Next"},{"cachePoint":{"type":"default"}}],"role":"user"}],
             "system":[{"text":"You are helpful.\n\nSection one\n\nSection B"},{"cachePoint":{"type":"default"}}],
             "toolConfig":{"tools":[{"toolSpec":{"description":"Read a file","inputSchema":{"json":{"type":"object","properties":{"path":{"type":"string"},"limit":{"anyOf":[{"type":"number"},{"type":"null"}]}},"required":["path","limit"],"additionalProperties":false}},"name":"read","strict":true}}]}}
            """, body, "claude budget request");
        // SigV4 headers over the exact body; the date and payload hash travel as x-amz-* headers.
        var bytes = Encoding.UTF8.GetBytes(body);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        Equal("20261008T120000Z", request.Headers.GetValues("x-amz-date").Single(), "x-amz-date");
        Equal(hash, request.Headers.GetValues("x-amz-content-sha256").Single(), "payload hash");
        var expected = AwsSigV4.Sign(new("POST", "bedrock-runtime.us-east-1.amazonaws.com", "/model/anthropic.claude-sonnet-4-5-20250929-v1%3A0/converse-stream", [],
            [new("content-type", "application/json")], bytes), new("AKIDEXAMPLE", "secret-example"), "us-east-1", "bedrock", BedrockTime);
        Equal(expected.Authorization, request.Headers.GetValues("Authorization").Single(), "authorization");
        Check(expected.CanonicalRequest.Contains("/model/anthropic.claude-sonnet-4-5-20250929-v1%253A0/converse-stream\n", StringComparison.Ordinal) &&
            expected.Authorization.Contains("SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date,", StringComparison.Ordinal), "canonical path and signed headers");

        // Long retention: 1h cache points; cross-model tool ids are normalized; "none" tool choice drops the tools.
        var crossModel = ImmutableArray.Create(
            Entry("""{"role":"user","content":"Go","timestamp":1}"""),
            Entry("""{"role":"assistant","content":[{"type":"thinking","thinking":"plan"},{"type":"toolCall","id":"call|id.with:bad chars","name":"read","arguments":{"":1,"path":"x"},"thoughtSignature":"t"}],"api":"openai-responses","provider":"openai","model":"gpt-5","usage":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}},"stopReason":"toolUse","timestamp":2}"""),
            Entry("""{"role":"user","content":"   ","timestamp":3}"""));
        var longFixture = Bedrock(SonnetRow, options: new() { CacheRetention = "long", ToolChoice = JsonData.Parse("\"none\""), Temperature = 0.5, RequestMetadata = ImmutableDictionary<string, string>.Empty.Add("team", "a") });
        using var longRequest = await longFixture.Transport.CreateRequestAsync(new(longFixture.Model, crossModel, 10));
        JsonEqual("""
            {"inferenceConfig":{"maxTokens":64000,"temperature":0.5},
             "messages":[
               {"content":[{"text":"Go"}],"role":"user"},
               {"content":[{"text":"plan"},{"toolUse":{"input":{"path":"x"},"name":"read","toolUseId":"call_id_with_bad_chars"}}],"role":"assistant"},
               {"content":[{"toolResult":{"content":[{"text":"No result provided"}],"status":"error","toolUseId":"call_id_with_bad_chars"}}],"role":"user"},
               {"content":[{"text":"<empty>"},{"cachePoint":{"type":"default","ttl":"1h"}}],"role":"user"}],
             "requestMetadata":{"team":"a"}}
            """, await longRequest.Content!.ReadAsStringAsync(), "long retention cross-model request");
        // Nova: no thinking fields, no cache points, the model cap clamped to the context.
        var nova = Bedrock(NovaRow);
        using var novaRequest = await nova.Transport.CreateRequestAsync(new(nova.Model, [Entry("""{"role":"user","content":"Hi","timestamp":1}""")], 1));
        JsonEqual("""{"inferenceConfig":{"maxTokens":10000},"messages":[{"content":[{"text":"Hi"}],"role":"user"}]}""", await novaRequest.Content!.ReadAsStringAsync(), "nova request");
    }

    private static async Task<string> BodyFor(string row, string? level, Dictionary<string, string?>? env = null, BedrockConverseOptions? options = null)
    {
        var fixture = Bedrock(row, env, options);
        using var request = await fixture.Transport.CreateRequestAsync(new(fixture.Model, [Entry("""{"role":"user","content":"Hi","timestamp":1}""")], 1) { ThinkingLevel = level });
        return await request.Content!.ReadAsStringAsync();
    }

    private static async Task BedrockReasoningFields()
    {
        JsonEqual("""{"additionalModelRequestFields":{"thinking":{"type":"adaptive","display":"summarized","block_binding":{"prefix_mismatch_behavior":"drop_block"}},"output_config":{"effort":"xhigh"},"anthropic_beta":["thinking-binding-controls-2026-08-01"]},"inferenceConfig":{"maxTokens":128000},"messages":[{"content":[{"text":"Hi"},{"cachePoint":{"type":"default"}}],"role":"user"}]}""",
            await BodyFor(OpusRow, "xhigh"), "opus 4.8 xhigh");
        JsonEqual("""{"additionalModelRequestFields":{"thinking":{"type":"adaptive","display":"omitted","block_binding":{"prefix_mismatch_behavior":"drop_block"}},"output_config":{"effort":"low"},"anthropic_beta":["thinking-binding-controls-2026-08-01"]},"inferenceConfig":{"maxTokens":128000},"messages":[{"content":[{"text":"Hi"},{"cachePoint":{"type":"default"}}],"role":"user"}]}""",
            await BodyFor(OpusRow, "minimal", options: new() { ThinkingDisplay = "omitted" }), "opus 4.8 minimal omitted display");
        // GovCloud: no display and no block binding; budget thinking keeps the interleaved beta.
        var gov = new Dictionary<string, string?> { ["AWS_ACCESS_KEY_ID"] = "AKIDEXAMPLE", ["AWS_SECRET_ACCESS_KEY"] = "secret-example", ["AWS_REGION"] = "us-gov-west-1" };
        JsonEqual("""{"additionalModelRequestFields":{"thinking":{"type":"enabled","budget_tokens":16384},"anthropic_beta":["interleaved-thinking-2025-05-14"]},"inferenceConfig":{"maxTokens":64000},"messages":[{"content":[{"text":"Hi"},{"cachePoint":{"type":"default"}}],"role":"user"}]}""",
            await BodyFor(SonnetRow, "high", gov), "govcloud sonnet high");
        // An explicit output cap: the thinking budget is added to it (adjustMaxTokensForThinking).
        JsonEqual("""{"additionalModelRequestFields":{"thinking":{"type":"enabled","budget_tokens":2048,"display":"summarized"},"anthropic_beta":["interleaved-thinking-2025-05-14"]},"inferenceConfig":{"maxTokens":4120},"messages":[{"content":[{"text":"Hi"},{"cachePoint":{"type":"default"}}],"role":"user"}]}""",
            await BodyFor(SonnetRow, "low", options: new() { MaxTokens = 2072 }), "sonnet low with a 2072 cap");
        // A cap below the budget: the budget leaves 1024 answer tokens.
        JsonEqual("""{"additionalModelRequestFields":{"thinking":{"type":"enabled","budget_tokens":976,"display":"summarized"},"anthropic_beta":["interleaved-thinking-2025-05-14"]},"inferenceConfig":{"maxTokens":2000},"messages":[{"content":[{"text":"Hi"},{"cachePoint":{"type":"default"}}],"role":"user"}]}""",
            await BodyFor(SonnetRow.Replace("\"maxTokens\":64000", "\"maxTokens\":2000", StringComparison.Ordinal), "medium"), "budget clamped to answer room");
        JsonEqual("""{"additionalModelRequestFields":{"reasoning_effort":"high"},"inferenceConfig":{"maxTokens":126975},"messages":[{"content":[{"text":"Hi"}],"role":"user"}]}""",
            await BodyFor(GptOssRow, "high"), "gpt-oss high");
        JsonEqual("""{"additionalModelRequestFields":{"reasoning":{"effort":"low"}},"inferenceConfig":{"maxTokens":128000},"messages":[{"content":[{"text":"Hi"}],"role":"user"}]}""",
            await BodyFor(GptRow, "minimal"), "gpt minimal mapped");
        JsonEqual("""{"inferenceConfig":{"maxTokens":64000},"messages":[{"content":[{"text":"Hi"},{"cachePoint":{"type":"default"}}],"role":"user"}]}""",
            await BodyFor(SonnetRow, "off"), "thinking off");
        var levels = Bedrock(SonnetRow).Transport.GetSupportedThinkingLevels(new("anthropic.claude-sonnet-4-5-20250929-v1:0", "bedrock-converse-stream", "amazon-bedrock"));
        Equal("off,minimal,low,medium,high", string.Join(",", levels), "sonnet levels");
    }

    private static HttpResponseMessage EventStream(HttpStatusCode status = HttpStatusCode.OK, string requestId = "req-123", params byte[][] messages)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([.. messages.SelectMany(message => message)]) };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/vnd.amazon.eventstream");
        response.Headers.TryAddWithoutValidation("x-amzn-RequestId", requestId);
        return response;
    }

    private static byte[] Exception(string type, string message) => AwsEventStream.Encode(
        [new(":exception-type", type), new(":content-type", "application/json"), new(":message-type", "exception")], Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { message })));

    private static async Task BedrockStreamEvents()
    {
        var fixture = Bedrock(SonnetRow);
        fixture.Http.OnUrl("https://bedrock-runtime.us-east-1.amazonaws.com/", _ => EventStream(messages:
        [
            EventMessage("messageStart", """{"p":"abc","role":"assistant"}"""),
            EventMessage("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"text":"think"}},"p":"a"}"""),
            EventMessage("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"signature":"sig"}},"p":"a"}"""),
            EventMessage("contentBlockStop", """{"contentBlockIndex":0,"p":"a"}"""),
            EventMessage("contentBlockDelta", """{"contentBlockIndex":1,"delta":{"text":"Hel"},"p":"a"}"""),
            EventMessage("contentBlockDelta", """{"contentBlockIndex":1,"delta":{"text":"lo"},"p":"a"}"""),
            EventMessage("contentBlockStop", """{"contentBlockIndex":1,"p":"a"}"""),
            EventMessage("contentBlockStart", """{"contentBlockIndex":2,"start":{"toolUse":{"toolUseId":"t1","name":"read"}},"p":"a"}"""),
            EventMessage("contentBlockDelta", """{"contentBlockIndex":2,"delta":{"toolUse":{"input":"{\"path\":"}},"p":"a"}"""),
            EventMessage("contentBlockDelta", """{"contentBlockIndex":2,"delta":{"toolUse":{"input":"\"a.txt\"}"}},"p":"a"}"""),
            EventMessage("contentBlockStop", """{"contentBlockIndex":2,"p":"a"}"""),
            EventMessage("messageStop", """{"stopReason":"tool_use","p":"a"}"""),
            EventMessage("metadata", """{"metrics":{"latencyMs":10},"usage":{"inputTokens":100,"outputTokens":50,"cacheReadInputTokens":10,"cacheWriteInputTokens":20,"cacheDetails":[{"inputTokens":20,"ttl":"1h"}],"totalTokens":180},"p":"a"}""")
        ]));
        var events = await Collect(fixture.Transport, new(fixture.Model, [Entry("""{"role":"user","content":"Hi","timestamp":1}""")], 77));
        Equal("StreamStarted,ThinkingStarted,ThinkingDelta,ThinkingEnded,TextStarted,TextDelta,TextDelta,TextEnded,ToolCallStarted,ToolCallDelta,ToolCallDelta,ToolCallEnded,StreamDone",
            string.Join(",", events.Select(frame => frame.GetType().Name)), "event sequence");
        var done = (StreamDone)events[^1];
        Equal(StopReason.ToolUse, done.Reason, "stop reason");
        JsonSame("""
            {"role":"assistant","content":[{"type":"thinking","thinking":"think","thinkingSignature":"sig"},{"type":"text","text":"Hello"},{"type":"toolCall","id":"t1","name":"read","arguments":{"path":"a.txt"}}],
             "api":"bedrock-converse-stream","provider":"amazon-bedrock","model":"anthropic.claude-sonnet-4-5-20250929-v1:0",
             "usage":{"input":100,"output":50,"cacheRead":10,"cacheWrite":20,"cacheWrite1h":20,"totalTokens":180,"cost":{"input":0.00030000000000000003,"output":0.00075,"cacheRead":0.000003,"cacheWrite":0.00012,"total":0.0011730000000000002}},
             "stopReason":"toolUse","timestamp":77,"rawStopReason":"tool_use"}
            """, Wire(done.Message), "done message");
        // models.ts calculateCost runs in binary64: (3 / 1000000) * 100 is 0.00030000000000000003 and the total sums those Numbers
        // (formerly decimal arithmetic wrote 0.0003 and 0.001173). Expected costs computed by calculateCost in Node 22.
        Check(Wire(done.Message).Contains("\"cost\":{\"input\":0.00030000000000000003,\"output\":0.00075,\"cacheRead\":0.000003,\"cacheWrite\":0.00012,\"total\":0.0011730000000000002}", StringComparison.Ordinal),
            "binary64 cost text: " + Wire(done.Message));
        // Fractional counts with a 1h share of the cache writes, priced in binary64 as Pi does.
        var fraction = Bedrock(SonnetRow);
        fraction.Http.OnUrl("https://", _ => EventStream(messages: [EventMessage("messageStart", """{"role":"assistant"}"""),
            EventMessage("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"x"}}"""), EventMessage("contentBlockStop", """{"contentBlockIndex":0}"""),
            EventMessage("messageStop", """{"stopReason":"end_turn"}"""),
            EventMessage("metadata", """{"usage":{"inputTokens":3.5,"outputTokens":2.75,"cacheReadInputTokens":1.25,"cacheWriteInputTokens":7,"cacheDetails":[{"inputTokens":2.5,"ttl":"1h"}],"totalTokens":14.5}}""")]));
        var fractional = (StreamDone)(await Collect(fraction.Transport, new(fraction.Model, [Entry("""{"role":"user","content":"Hi","timestamp":1}""")], 1)))[^1];
        Check(Wire(fractional.Message).Contains("\"usage\":{\"input\":3.5,\"output\":2.75,\"cacheRead\":1.25,\"cacheWrite\":7,\"totalTokens\":14.5,\"cost\":{\"input\":0.000010500000000000001,\"output\":0.00004125,\"cacheRead\":3.75e-7,\"cacheWrite\":0.000031875,\"total\":0.00008400000000000001},\"cacheWrite1h\":2.5}", StringComparison.Ordinal),
            "fractional binary64 cost: " + Wire(fractional.Message));
        // Stop reasons: end_turn/stop_sequence stop, max_tokens/model_context_window_exceeded length.
        foreach (var (raw, expected) in new[] { ("end_turn", StopReason.Stop), ("stop_sequence", StopReason.Stop), ("max_tokens", StopReason.Length), ("model_context_window_exceeded", StopReason.Length) })
        {
            var stop = Bedrock(NovaRow);
            stop.Http.OnUrl("https://", _ => EventStream(messages: [EventMessage("messageStart", """{"role":"assistant"}"""),
                EventMessage("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"x"}}"""), EventMessage("contentBlockStop", """{"contentBlockIndex":0}"""),
                EventMessage("messageStop", $$"""{"stopReason":"{{raw}}"}""")]));
            var result = await Collect(stop.Transport, new(stop.Model, [Entry("""{"role":"user","content":"Hi","timestamp":1}""")], 1));
            Equal(expected, ((StreamDone)result[^1]).Reason, raw);
        }
    }

    private static async Task BedrockRedactedReasoning()
    {
        var fixture = Bedrock(GptRow);
        var first = Convert.ToBase64String([1, 2, 3]); var second = Convert.ToBase64String([4, 5]);
        fixture.Http.OnUrl("https://", _ => EventStream(messages:
        [
            EventMessage("messageStart", """{"role":"assistant"}"""),
            EventMessage("contentBlockDelta", "{\"contentBlockIndex\":0,\"delta\":{\"reasoningContent\":{\"redactedContent\":\"" + first + "\"}}}"),
            EventMessage("contentBlockDelta", "{\"contentBlockIndex\":0,\"delta\":{\"reasoningContent\":{\"redactedContent\":\"" + second + "\",\"signature\":\"ignored\"}}}"),
            EventMessage("contentBlockDelta", """{"contentBlockIndex":1,"delta":{"text":"answer"}}"""),
            // Neither block is stopped before messageStop.
            EventMessage("messageStop", """{"stopReason":"end_turn"}""")
        ]));
        var events = await Collect(fixture.Transport, new(fixture.Model, [Entry("""{"role":"user","content":"Hi","timestamp":1}""")], 5));
        // Source finalizeStreamingBlock: the unstopped blocks are finalized without thinking_end/text_end (native-only finalization).
        Equal("StreamStarted,ThinkingStarted,ThinkingDelta,TextStarted,TextDelta,ContentBlockFinalized,ContentBlockFinalized,StreamDone", string.Join(",", events.Select(frame => frame.GetType().Name)), "events");
        var done = (StreamDone)events[^1];
        JsonSame($$"""[{"type":"thinking","thinking":"[Reasoning redacted]","thinkingSignature":"{{Convert.ToBase64String([1, 2, 3, 4, 5])}}","redacted":true},{"type":"text","text":"answer"}]""",
            JsonDocument.Parse(Wire(done.Message)).RootElement.GetProperty("content").GetRawText(), "redacted content");
        // Replay: the same model sends the payload back as redactedContent; another model drops it.
        var replay = ImmutableArray.Create(Entry("""{"role":"user","content":"Hi","timestamp":1}"""), new TranscriptEntry("assistant", PiWireJson.WriteMessage(done.Message)),
            Entry("""{"role":"user","content":"More","timestamp":9}"""));
        using var request = await fixture.Transport.CreateRequestAsync(new(fixture.Model, replay, 10));
        var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.GetProperty("messages")[1].GetRawText();
        JsonEqual("""{"content":[{"reasoningContent":{"redactedContent":"AQIDBAU="}},{"text":"answer"}],"role":"assistant"}""", body, "redacted replay");
        var other = Bedrock(GptOssRow);
        using var otherRequest = await other.Transport.CreateRequestAsync(new(other.Model, replay, 10));
        JsonEqual("""{"content":[{"text":"answer"}],"role":"assistant"}""", JsonDocument.Parse(await otherRequest.Content!.ReadAsStringAsync()).RootElement.GetProperty("messages")[1].GetRawText(), "cross-model drop");
        // An unstopped tool call keeps the arguments streamed so far (parseStreamingJson of the partial JSON) and emits no toolcall_end.
        var tool = Bedrock(SonnetRow);
        tool.Http.OnUrl("https://", _ => EventStream(messages:
        [
            EventMessage("messageStart", """{"role":"assistant"}"""),
            EventMessage("contentBlockStart", """{"contentBlockIndex":0,"start":{"toolUse":{"toolUseId":"t9","name":"read"}}}"""),
            EventMessage("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"toolUse":{"input":"{\"path\":\"b.txt\""}}}"""),
            EventMessage("messageStop", """{"stopReason":"tool_use"}""")
        ]));
        var toolEvents = await Collect(tool.Transport, new(tool.Model, [Entry("""{"role":"user","content":"Hi","timestamp":1}""")], 6));
        Equal("StreamStarted,ToolCallStarted,ToolCallDelta,ContentBlockFinalized,StreamDone", string.Join(",", toolEvents.Select(frame => frame.GetType().Name)), "tool events");
        JsonSame("""[{"type":"toolCall","id":"t9","name":"read","arguments":{"path":"b.txt"}}]""",
            JsonDocument.Parse(Wire(((StreamDone)toolEvents[^1]).Message)).RootElement.GetProperty("content").GetRawText(), "unstopped tool arguments");
    }

    private static async Task BedrockToolArguments()
    {
        // bedrock-converse-stream.ts finalizes every tool call, stopped or not, with block.arguments = parseStreamingJson(block.partialJson):
        // malformed input never fails the turn and any JSON value is kept.
        foreach (var (input, expected) in new[]
        {
            ("{\"path\":\"a\",\"path\":\"b\",\"n\":1.50}", "{\"path\":\"b\",\"n\":1.5}"), ("[1,2", "[1,2]"), ("\"text\"", "\"text\""), ("not json", "{}"),
            ("{\"p\":\"C:\\x\\y\"", "{}"), ("{\"p\":\"C:\\x\\y\"}", "{\"p\":\"C:\\\\x\\\\y\"}"), ("{\"a\":\"x\u0001", "{}"),
            ("{\"a\":\"x\u0001\"}", "{\"a\":\"x\\u0001\"}"), ("null", "null"), ("", "{}"),
        })
            foreach (var stopped in new[] { true, false })
            {
                var tool = Bedrock(SonnetRow);
                var messages = new List<byte[]>
                {
                    EventMessage("messageStart", """{"role":"assistant"}"""),
                    EventMessage("contentBlockStart", """{"contentBlockIndex":0,"start":{"toolUse":{"toolUseId":"t1","name":"read"}}}"""),
                };
                if (input.Length > 0) messages.Add(EventMessage("contentBlockDelta", "{\"contentBlockIndex\":0,\"delta\":{\"toolUse\":{\"input\":" + JsonSerializer.Serialize(input) + "}}}"));
                if (stopped) messages.Add(EventMessage("contentBlockStop", """{"contentBlockIndex":0}"""));
                messages.Add(EventMessage("messageStop", """{"stopReason":"tool_use"}"""));
                tool.Http.OnUrl("https://", _ => EventStream(messages: [.. messages]));
                var events = await Collect(tool.Transport, new(tool.Model, [Entry("""{"role":"user","content":"Hi","timestamp":1}""")], 6));
                var done = (StreamDone)events[^1];
                Equal(StopReason.ToolUse, done.Reason, input);
                Equal(expected, done.Message.Content.OfType<ToolCallContent>().Single().Arguments.ToString(), (stopped ? "stopped " : "unstopped ") + input);
            }
    }

    // Owner decision 13. bedrock-converse-stream.ts handleContentBlockStart: id start.toolUse.toolUseId || "", name start.toolUse.name || "".
    // Captured from @earendil-works/pi-ai@1.1.0 stream() against a local HTTP/2 event-stream server: every case finalizes as toolUse with
    // these identities, two id-less calls sharing the id "". convertMessages replays them with toolUseId c.id and name c.name.
    private static async Task BedrockNamelessToolCalls()
    {
        foreach (var (label, starts, expected) in new (string, string[], string)[]
        {
            ("both-missing", ["{}"], """[{"type":"toolCall","id":"","name":"","arguments":{}}]"""),
            ("both-empty", ["""{"toolUseId":"","name":""}"""], """[{"type":"toolCall","id":"","name":"","arguments":{}}]"""),
            ("nameless", ["""{"toolUseId":"t1"}"""], """[{"type":"toolCall","id":"t1","name":"","arguments":{}}]"""),
            ("two-id-less", ["""{"name":"read"}""", "{}"], """[{"type":"toolCall","id":"","name":"read","arguments":{}},{"type":"toolCall","id":"","name":"","arguments":{}}]"""),
        })
        {
            var tool = Bedrock(SonnetRow);
            var messages = new List<byte[]> { EventMessage("messageStart", """{"role":"assistant"}""") };
            for (var index = 0; index < starts.Length; index++)
            {
                messages.Add(EventMessage("contentBlockStart", "{\"contentBlockIndex\":" + index + ",\"start\":{\"toolUse\":" + starts[index] + "}}"));
                messages.Add(EventMessage("contentBlockDelta", "{\"contentBlockIndex\":" + index + ",\"delta\":{\"toolUse\":{\"input\":\"{}\"}}}"));
                messages.Add(EventMessage("contentBlockStop", "{\"contentBlockIndex\":" + index + "}"));
            }
            messages.Add(EventMessage("messageStop", """{"stopReason":"tool_use"}"""));
            tool.Http.OnUrl("https://", _ => EventStream(messages: [.. messages]));
            var events = await Collect(tool.Transport, new(tool.Model, [Entry("""{"role":"user","content":"Hi","timestamp":1}""")], 6));
            var done = events[^1] as StreamDone ?? throw new InvalidOperationException(label + ": " + events[^1]);
            Equal(StopReason.ToolUse, done.Reason, label);
            JsonSame(expected, JsonDocument.Parse(Wire(done.Message)).RootElement.GetProperty("content").GetRawText(), label);
        }
        var replayed = Bedrock(SonnetRow);
        var history = ImmutableArray.Create(Entry("""{"role":"user","content":"Hi","timestamp":1}"""),
            Entry($$$"""{"role":"assistant","content":[{"type":"toolCall","id":"","name":"","arguments":{}}],"api":"bedrock-converse-stream","provider":"amazon-bedrock","model":"{{{ClaudeTranscriptModel}}}","usage":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}},"stopReason":"toolUse","timestamp":3}"""),
            Entry("""{"role":"toolResult","toolCallId":"","toolName":"","content":[{"type":"text","text":"Tool  not found"}],"details":{},"isError":true,"timestamp":4}"""));
        using var request = await replayed.Transport.CreateRequestAsync(new(replayed.Model, history, 10));
        var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.GetProperty("messages");
        JsonEqual("""{"content":[{"toolUse":{"input":{},"name":"","toolUseId":""}}],"role":"assistant"}""", body[1].GetRawText(), "nameless replay");
        JsonEqual("""{"content":[{"toolResult":{"content":[{"text":"Tool  not found"}],"status":"error","toolUseId":""}},{"cachePoint":{"type":"default"}}],"role":"user"}""", body[2].GetRawText(), "nameless result replay");
    }

    private static async Task BedrockErrors()
    {
        var hi = ImmutableArray.Create(Entry("""{"role":"user","content":"Hi","timestamp":1}"""));
        // Throttled twice, then served: the standard retry strategy waits floor(0.5 * 2^n * 500ms).
        var retried = Bedrock(NovaRow);
        var attempts = 0;
        retried.Http.OnUrl("https://", _ => ++attempts <= 2
            ? new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("""{"message":"Too many requests"}"""), Headers = { { "x-amzn-ErrorType", "ThrottlingException:http://internal.amazon.com/coral/" } } }
            : EventStream(messages: [EventMessage("messageStart", """{"role":"assistant"}"""), EventMessage("messageStop", """{"stopReason":"end_turn"}""")]));
        var ok = await Collect(retried.Transport, new(retried.Model, hi, 1));
        Check(ok[^1] is StreamDone && attempts == 3, "throttled requests retried to success");
        Equal("00:00:00.2500000,00:00:00.5000000", string.Join(",", retried.Delays), "retry delays");
        Check(retried.Http.All.Select(request => request.Header("authorization")).Distinct().Count() == 1, "every attempt is signed");
        // Validation errors are not retried; the modeled prefix and diagnostics are reported.
        var invalid = Bedrock(NovaRow);
        invalid.Http.OnUrl("https://", _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"message":"The model returned the following errors: data retention mode 'default' is not available for this model"}"""),
            Headers = { { "x-amzn-ErrorType", "ValidationException:http://internal.amazon.com/coral/com.amazon.bedrock/" }, { "x-amzn-RequestId", "rid-400" } }
        });
        var failed = (StreamError)(await Collect(invalid.Transport, new(invalid.Model, hi, 8)))[^1];
        Equal(1, invalid.Http.All.Count, "validation not retried");
        Equal("Validation error: The model returned the following errors: data retention mode 'default' is not available for this model See https://docs.aws.amazon.com/bedrock/latest/userguide/data-retention.html for supported data retention modes.",
            ErrorMessage(failed), "validation message");
        JsonEqual("""[{"type":"bedrock_response_failure","timestamp":8,"details":{"status":400,"errorCode":"ValidationException","requestId":"rid-400"}}]""",
            failed.Message.ExtraProperties!.Values["diagnostics"].ToString(), "validation diagnostics");
        // A gateway page is not a modeled error: "status: body".
        var gateway = Bedrock(NovaRow, options: new() { MaxAttempts = 1 });
        gateway.Http.OnUrl("https://", _ => Text("<html>forbidden</html>", HttpStatusCode.Forbidden, "text/html"));
        var forbidden = (StreamError)(await Collect(gateway.Transport, new(gateway.Model, hi, 8)))[^1];
        Equal("403: <html>forbidden</html>", ErrorMessage(forbidden), "gateway body");
        // A mid-stream modeled exception keeps the partial content and correlates the response request id.
        var midStream = Bedrock(NovaRow);
        midStream.Http.OnUrl("https://", _ => EventStream(requestId: "rid-stream", messages: [EventMessage("messageStart", """{"role":"assistant"}"""),
            EventMessage("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"part"}}"""), Exception("throttlingException", "Too many tokens")]));
        var streamEvents = await Collect(midStream.Transport, new(midStream.Model, hi, 9));
        var streamError = (StreamError)streamEvents[^1];
        Equal("Throttling error: Too many tokens", ErrorMessage(streamError), "stream exception message");
        JsonEqual("""[{"type":"text","text":"part"}]""", JsonDocument.Parse(Wire(streamError.Message)).RootElement.GetProperty("content").GetRawText(), "partial content kept");
        JsonEqual("""[{"type":"bedrock_response_failure","timestamp":9,"details":{"errorCode":"ThrottlingException","requestId":"rid-stream"}}]""",
            streamError.Message.ExtraProperties!.Values["diagnostics"].ToString(), "stream diagnostics");
        // Provider stop reasons outside the mapping, a missing stop and a protocol error message.
        var guarded = Bedrock(NovaRow);
        guarded.Http.OnUrl("https://", _ => EventStream(messages: [EventMessage("messageStart", """{"role":"assistant"}"""), EventMessage("messageStop", """{"stopReason":"guardrail_intervened"}""")]));
        var guard = (StreamError)(await Collect(guarded.Transport, new(guarded.Model, hi, 1)))[^1];
        Equal("Provider stopped with: guardrail_intervened", ErrorMessage(guard), "guardrail stop");
        Equal("guardrail_intervened", guard.Message.ExtraProperties!.Values["rawStopReason"].Value.GetString(), "raw stop reason");
        var unfinished = Bedrock(NovaRow);
        unfinished.Http.OnUrl("https://", _ => EventStream(messages: [EventMessage("messageStart", """{"role":"assistant"}""")]));
        Equal("Bedrock stream ended without a stop reason", ErrorMessage((await Collect(unfinished.Transport, new(unfinished.Model, hi, 1)))[^1]), "missing stop");
        var protocol = Bedrock(NovaRow);
        protocol.Http.OnUrl("https://", _ => EventStream(messages: [AwsEventStream.Encode([new(":message-type", "error"), new(":error-code", "InternalFailure"), new(":error-message", "boom")], [])]));
        Equal("boom", ErrorMessage((await Collect(protocol.Transport, new(protocol.Model, hi, 1)))[^1]), "protocol error message");
        // Cancellation settles as aborted.
        var cancelled = Bedrock(NovaRow);
        using var cancellation = new CancellationTokenSource();
        cancelled.Http.OnUrl("https://", _ => { cancellation.Cancel(); return EventStream(messages: [EventMessage("messageStart", """{"role":"assistant"}""")]); });
        var aborted = (StreamError)(await Collect(cancelled.Transport, new(cancelled.Model, hi, 1), cancellation.Token))[^1];
        Check(aborted.Reason == StopReason.Aborted && ErrorMessage(aborted) == "Request was aborted", "aborted: " + ErrorMessage(aborted));
    }

    private static async Task BedrockEndpoints()
    {
        var hi = ImmutableArray.Create(Entry("""{"role":"user","content":"Hi","timestamp":1}"""));
        async Task<HttpRequestMessage> Request(string row, Dictionary<string, string?> env, BedrockConverseOptions? options = null, string? home = null)
        {
            var fixture = Bedrock(row, env, options, home: home);
            return await fixture.Transport.CreateRequestAsync(new(fixture.Model, hi, 1));
        }
        // A bearer token (AWS_BEARER_TOKEN_BEDROCK or a stored apiKey) replaces SigV4.
        using (var bearer = await Request(NovaRow, new() { ["AWS_BEARER_TOKEN_BEDROCK"] = "bedrock-token" }))
            Check(bearer.Headers.GetValues("Authorization").Single() == "Bearer bedrock-token" && !bearer.Headers.Contains("x-amz-date"), "bearer auth");
        using (var stored = await Request(NovaRow, new(), new() { ApiKey = "stored-token" }))
            Equal("Bearer stored-token", stored.Headers.GetValues("Authorization").Single(), "stored bearer");
        // A configured region selects the SDK's regional endpoint over the catalog default.
        using (var regional = await Request(NovaRow, new() { ["AWS_ACCESS_KEY_ID"] = "k", ["AWS_SECRET_ACCESS_KEY"] = "s", ["AWS_REGION"] = "eu-west-1" }))
        {
            Equal("https://bedrock-runtime.eu-west-1.amazonaws.com/model/amazon.nova-lite-v1%3A0/converse-stream", regional.RequestUri!.AbsoluteUri, "regional endpoint");
            Check(regional.Headers.GetValues("Authorization").Single().Contains("/eu-west-1/bedrock/aws4_request", StringComparison.Ordinal), "regional scope");
        }
        using (var china = await Request(NovaRow, new() { ["AWS_ACCESS_KEY_ID"] = "k", ["AWS_SECRET_ACCESS_KEY"] = "s", ["AWS_DEFAULT_REGION"] = "cn-north-1" }))
            Equal("https://bedrock-runtime.cn-north-1.amazonaws.com.cn/model/amazon.nova-lite-v1%3A0/converse-stream", china.RequestUri!.AbsoluteUri, "china endpoint");
        // An inference-profile ARN carries its region; a custom (proxy) base URL is always used.
        var arnRow = NovaRow.Replace("amazon.nova-lite-v1:0", "arn:aws:bedrock:ap-south-1:123456789012:application-inference-profile/abc", StringComparison.Ordinal)
            .Replace("https://bedrock-runtime.us-east-1.amazonaws.com", "https://proxy.example.com/bedrock", StringComparison.Ordinal);
        using (var arn = await Request(arnRow, new() { ["AWS_ACCESS_KEY_ID"] = "k", ["AWS_SECRET_ACCESS_KEY"] = "s", ["AWS_REGION"] = "us-west-2" }))
        {
            Equal("https://proxy.example.com/bedrock/model/arn%3Aaws%3Abedrock%3Aap-south-1%3A123456789012%3Aapplication-inference-profile%2Fabc/converse-stream", arn.RequestUri!.AbsoluteUri, "arn url");
            Check(arn.Headers.GetValues("Authorization").Single().Contains("/ap-south-1/bedrock/aws4_request", StringComparison.Ordinal), "arn region");
        }
        // An ambient AWS_PROFILE without a region reads the profile's region from the shared config file.
        var home = Temp("aws-home"); Directory.CreateDirectory(Path.Combine(home, ".aws"));
        File.WriteAllText(Path.Combine(home, ".aws", "config"), "[profile work]\nregion = eu-central-1\n");
        File.WriteAllText(Path.Combine(home, ".aws", "credentials"), "[work]\naws_access_key_id = AKIDWORK\naws_secret_access_key = work-secret\n");
        using (var profile = await Request(NovaRow, new() { ["AWS_PROFILE"] = "work" }, home: home))
        {
            Equal("https://bedrock-runtime.eu-central-1.amazonaws.com/model/amazon.nova-lite-v1%3A0/converse-stream", profile.RequestUri!.AbsoluteUri, "profile region endpoint");
            Check(profile.Headers.GetValues("Authorization").Single().StartsWith("AWS4-HMAC-SHA256 Credential=AKIDWORK/20261008/eu-central-1/bedrock/aws4_request", StringComparison.Ordinal), "profile credentials");
        }
        // A pi-configured profile (scoped env) wins over ambient keys (#6957).
        using (var scoped = await Request(NovaRow, new() { ["AWS_ACCESS_KEY_ID"] = "AKIDENV", ["AWS_SECRET_ACCESS_KEY"] = "env" },
            new() { Environment = ImmutableDictionary<string, string>.Empty.Add("AWS_PROFILE", "work"), Region = "us-east-1" }, home))
            Check(scoped.Headers.GetValues("Authorization").Single().Contains("Credential=AKIDWORK/", StringComparison.Ordinal), "scoped profile wins");
        // Skip-auth proxies sign with dummy keys; caller headers are signed but never replace x-amz-* or authorization.
        using (var skip = await Request(NovaRow, new() { ["AWS_BEDROCK_SKIP_AUTH"] = "1", ["AWS_BEARER_TOKEN_BEDROCK"] = "ignored" },
            new() { Headers = ImmutableDictionary<string, string?>.Empty.Add("x-team", "a").Add("X-Amz-Date", "bad").Add("authorization", "bad").Add("x-null", null) }))
        {
            var authorization = skip.Headers.GetValues("Authorization").Single();
            Check(authorization.Contains("Credential=dummy-access-key/", StringComparison.Ordinal) && authorization.Contains("SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date;x-team,", StringComparison.Ordinal),
                "skip auth signing: " + authorization);
            Check(skip.Headers.GetValues("x-team").Single() == "a" && skip.Headers.GetValues("x-amz-date").Single() == "20261008T120000Z" && !skip.Headers.Contains("x-null"), "caller headers");
        }
    }
}
