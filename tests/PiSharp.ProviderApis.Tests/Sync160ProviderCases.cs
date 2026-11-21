// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/ai/src/api/openai-codex-responses.ts, api/openai-responses-shared.ts,
// api/simple-options.ts and utils/retry.ts.
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICodexResponses;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;

// Pi 1.6.0 sync (ai-providers). Authored expectations derived by reading Pi v1.6.0 source and test text (packages/ai/test/
// openai-codex-stream.test.ts, openai-responses-input-transformations.test.ts and retry.test.ts). Nothing here executes upstream code or
// is an upstream capture; every HTTP peer is an in-process fake or a loopback listener. The model rows are authored: Pi 1.6.0's catalog
// sets compat.supportsMidConvoEffort on the Codex GPT-6 models, which the 1.5.2 catalog data this branch carries does not have yet.
// Register with: cases = [.. cases, .. Sync160ProviderCases].
internal static partial class Program
{
    internal static readonly (string Id, Func<Task> Run)[] Sync160ProviderCases =
    [
        ("sync160.codex-system-prompt-is-the-leading-developer-item-without-instructions", Sync160CodexSystemPrompt),
        ("sync160.codex-thinking-level-as-configuration-update-items-with-a-fixed-effort", Sync160CodexConfigurationUpdates),
        ("sync160.codex-max-tokens-as-max-output-tokens", Sync160CodexMaxOutputTokens),
        ("sync160.codex-ws-thinking-level-change-keeps-input-deltas", Sync160CodexWebSocketEffortDeltas),
        ("sync160.codex-ws-connection-limit-after-stream-start-is-retried-on-a-fresh-connection", Sync160CodexConnectionLimitAfterStart),
        ("sync160.codex-response-done-records-input-transformations", Sync160CodexInputTransformations),
        ("sync160.retry-live-loopback-connection-failures-are-retryable", Sync160LiveConnectionFailures),
    ];

    private const string Sync160CodexReply = """{"type":"message","id":"msg_1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Hello","annotations":[]}]}""";

    /// <summary>A short completed response with one assistant message.</summary>
    private static string[] Sync160CodexEvents(string responseId = "resp_1", string? terminal = null) =>
    [
        "{\"type\":\"response.created\",\"response\":{\"id\":\"" + responseId + "\",\"status\":\"in_progress\"}}",
        """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"msg_1","role":"assistant","status":"in_progress","content":[]}}""",
        """{"type":"response.output_text.delta","output_index":0,"item_id":"msg_1","delta":"Hello"}""",
        """{"type":"response.output_item.done","output_index":0,"item":""" + Sync160CodexReply + "}",
        terminal ?? "{\"type\":\"response.completed\",\"response\":{\"id\":\"" + responseId + "\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":5,\"output_tokens\":1,\"total_tokens\":6}}}"
    ];

    private static JsonData Sync160CodexRow(string id, string? compat = null, string? thinkingLevelMap = null, bool reasoning = true, int contextWindow = 400000, int maxTokens = 128000, string? baseUrl = null) =>
        JsonData.Parse("{\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"api\":\"openai-codex-responses\",\"provider\":\"openai-codex\",\"baseUrl\":\"" +
            (baseUrl ?? "https://chatgpt.com/backend-api") + "\",\"reasoning\":" + (reasoning ? "true" : "false") +
            ",\"input\":[\"text\"],\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0},\"contextWindow\":" + contextWindow + ",\"maxTokens\":" + maxTokens +
            (compat is null ? "" : ",\"compat\":" + compat) + (thinkingLevelMap is null ? "" : ",\"thinkingLevelMap\":" + thinkingLevelMap) + "}");

    /// <summary>A Codex transport over SSE for an authored model row; every request is answered with a short completed response.</summary>
    private static (OpenAICodexResponsesTransport Transport, FakeHttp Http, ModelDescriptor Model) Sync160Codex(JsonData row, OpenAICodexResponsesOptions? options = null)
    {
        var model = new ModelDescriptor(row.Value.GetProperty("id").GetString()!, "openai-codex-responses", "openai-codex");
        var http = new FakeHttp();
        http.OnUrl("https://chatgpt.com/", _ => Sse(Sync160CodexEvents()));
        options = (options ?? new()) with { Time = new FixedTime(1_800_000_000_000), Transport = () => "sse" };
        return (new OpenAICodexResponsesTransport(new HttpClient(http, disposeHandler: false), model, row, options, _ => ValueTask.FromResult(CodexToken)), http, model);
    }

    private static TranscriptEntry Sync160User(string text, int timestamp) => Entry("{\"role\":\"user\",\"content\":\"" + text + "\",\"timestamp\":" + timestamp + "}");
    private static TranscriptEntry Sync160Reply(AssistantMessage message) => new("assistant", PiWireJson.WriteMessage(message));

    /// <summary>Input items as roles, with configuration_update items shown with their effort.</summary>
    private static string Sync160Input(JsonNode? body) => string.Join(",", (body?["input"] as JsonArray ?? []).Select(item =>
        (string?)item!["type"] == "configuration_update" ? "configuration_update:" + (string?)item["reasoning"]?["effort"] : (string?)item["role"] ?? (string?)item["type"]));

    private static string? Sync160Level(AssistantMessage message) =>
        message.ExtraProperties?.TryGet("providerThinkingLevel", out var value) == true ? value!.Value.GetString() : null;

    private static async Task<(JsonNode Body, AssistantMessage Message)> Sync160CodexTurn(OpenAICodexResponsesTransport transport, FakeHttp http, ChatRequest request)
    {
        var sent = http.All.Count;
        var terminal = (await Collect(transport, request))[^1];
        var done = terminal as StreamDone ?? throw new CheckException("done: " + ErrorMessage(terminal));
        Equal(sent + 1, http.All.Count, "one request per turn");
        return (JsonNode.Parse(http.All[^1].Body)!, done.Message);
    }

    // #12273: GPT-6.2 places top-level instructions after the conversation, so each new turn invalidated the prompt cache for the system
    // prompt. It is the first developer input item, as in Codex, and no instructions field is sent.
    private static async Task Sync160CodexSystemPrompt()
    {
        const string user = """{"role":"user","content":[{"type":"input_text","text":"Say hello"}]}""";
        var (transport, http, model) = Sync160Codex(Sync160CodexRow("gpt-6.2-sol"));
        var withPrompt = await Sync160CodexTurn(transport, http, new(model, [Entry("""{"role":"system","content":"Be terse.","timestamp":0}"""), Sync160User("Say hello", 1)], 2));
        Check(withPrompt.Body["instructions"] is null && !withPrompt.Body.AsObject().ContainsKey("instructions"), "instructions was sent");
        JsonEqual("""[{"role":"developer","content":"Be terse."},""" + user + "]", withPrompt.Body["input"]!.ToJsonString(), "leading developer item");
        // Without a system prompt there is no item and no default instructions either.
        var without = await Sync160CodexTurn(transport, http, new(model, [Sync160User("Say hello", 1)], 2));
        Check(!without.Body.AsObject().ContainsKey("instructions"), "default instructions were sent");
        JsonEqual("[" + user + "]", without.Body["input"]!.ToJsonString(), "no item without a system prompt");
        // Later system messages fold into the item for a model without mid-conversation system messages: content, then sections.
        var folded = await Sync160CodexTurn(transport, http, new(model, [Entry("""{"role":"system","content":"Be terse.","sections":{"policy":"Policy"},"timestamp":0}"""),
            Sync160User("Say hello", 1), Entry("""{"role":"system","content":"Answer in German.","timestamp":2}""")], 3));
        Equal("developer,user", Sync160Input(folded.Body), "folded system messages");
        Equal("Be terse.\n\nAnswer in German.\n\nPolicy", (string?)folded.Body["input"]![0]!["content"], "folded prompt text");
        // The item's role follows the Responses rule: developer for a reasoning model unless compat.supportsDeveloperRole is false.
        var system = Sync160Codex(Sync160CodexRow("gpt-6.2-sol", """{"supportsDeveloperRole":false}"""));
        Equal("system", (string?)system.Transport.BuildBody(new(system.Model, [Entry("""{"role":"system","content":"Be terse.","timestamp":0}"""), Sync160User("Say hello", 1)], 2))["input"]![0]!["role"], "system role");
        var plain = Sync160Codex(Sync160CodexRow("codex-plain", reasoning: false));
        var plainBody = plain.Transport.BuildBody(new(plain.Model, [Entry("""{"role":"system","content":"Be terse.","timestamp":0}"""), Sync160User("Say hello", 1)], 2));
        Check((string?)plainBody["input"]![0]!["role"] == "system" && plainBody["reasoning"] is null && !plainBody.ContainsKey("instructions"), "a model without reasoning: " + plainBody.ToJsonString());
        // The WebSocket frame carries the same body: the prompt item and no instructions.
        await using var server = new CodexServer();
        server.OnFrame = (connection, _) => connection.SendAllAsync(CodexCompleted);
        var (socket, socketModel, _) = CodexOver(server, "websocket");
        Check((await Collect(socket, new(socketModel, [Entry("""{"role":"system","content":"Be terse.","timestamp":0}"""), Sync160User("Say hello", 1)], 2)))[^1] is StreamDone, "websocket request");
        var frame = JsonNode.Parse(server.Frames.Single().Text)!;
        Check(!frame.AsObject().ContainsKey("instructions") && frame["input"]![0]!.ToJsonString() == """{"role":"developer","content":"Be terse."}""", "websocket frame: " + frame.ToJsonString());
    }

    // #12269: changing the thinking level invalidated the prompt cache on GPT-6 models. Models with compat.supportsMidConvoEffort take
    // level changes, switching to off included, as configuration_update input items under a fixed reasoning.effort.
    private static async Task Sync160CodexConfigurationUpdates()
    {
        const string managed = """{"supportsMidConvoEffort":true}""", fixedEffort = """{"effort":"medium","summary":"auto"}""";
        var (transport, http, model) = Sync160Codex(Sync160CodexRow("gpt-6.2-sol", managed));
        var first = await Sync160CodexTurn(transport, http, new(model, [Sync160User("Say hello", 1)], 2) { ThinkingLevel = "low" });
        var second = await Sync160CodexTurn(transport, http, new(model, [Sync160User("Say hello", 1), Sync160Reply(first.Message), Sync160User("Again", 3)], 4) { ThinkingLevel = "high" });
        Equal("low", Sync160Level(first.Message), "first providerThinkingLevel"); Equal("high", Sync160Level(second.Message), "second providerThinkingLevel");
        Equal(fixedEffort, first.Body["reasoning"]!.ToJsonString(), "first reasoning"); Equal(fixedEffort, second.Body["reasoning"]!.ToJsonString(), "second reasoning");
        Equal("user,configuration_update:low", Sync160Input(first.Body), "first input");
        // The earlier reply replays its recorded level before its items; the new level ends the input.
        Equal("user,configuration_update:low,assistant,user,configuration_update:high", Sync160Input(second.Body), "second input");
        JsonEqual("""{"type":"configuration_update","reasoning":{"effort":"high"}}""", second.Body["input"]![4]!.ToJsonString(), "configuration_update item");
        // The same level again changes nothing before the new turn: the cached prefix is the first request's input and its reply.
        var third = await Sync160CodexTurn(transport, http, new(model, [Sync160User("Say hello", 1), Sync160Reply(first.Message), Sync160User("Again", 3),
            Sync160Reply(second.Message), Sync160User("More", 5)], 6) { ThinkingLevel = "high" });
        Equal("user,configuration_update:low,assistant,user,configuration_update:high,assistant,user,configuration_update:high", Sync160Input(third.Body), "third input");

        // The off level, an omitted reasoning option included, is sent as a configuration_update item with effort none.
        var off = Sync160Codex(Sync160CodexRow("gpt-6-sol", managed, """{"off":"none"}"""));
        foreach (var level in new[] { null, "off" })
        {
            var turn = await Sync160CodexTurn(off.Transport, off.Http, new(off.Model, [Sync160User("Say hello", 1)], 2) { ThinkingLevel = level });
            Equal("none", Sync160Level(turn.Message), "off providerThinkingLevel");
            Equal(fixedEffort, turn.Body["reasoning"]!.ToJsonString(), "off reasoning");
            Equal("user,configuration_update:none", Sync160Input(turn.Body), "off input");
        }
        // The item carries the provider's effort of the level (thinkingLevelMap), and a model that cannot switch off sends neither an
        // item nor a reasoning field for an omitted level.
        var mapped = Sync160Codex(Sync160CodexRow("gpt-6.2-astra", managed, """{"off":null,"minimal":"low","xhigh":"xhigh"}"""));
        var minimal = await Sync160CodexTurn(mapped.Transport, mapped.Http, new(mapped.Model, [Sync160User("Say hello", 1)], 2) { ThinkingLevel = "minimal" });
        Check(Sync160Level(minimal.Message) == "low" && Sync160Input(minimal.Body) == "user,configuration_update:low" && minimal.Body["reasoning"]!.ToJsonString() == fixedEffort, "mapped level: " + minimal.Body.ToJsonString());
        var omitted = await Sync160CodexTurn(mapped.Transport, mapped.Http, new(mapped.Model, [Sync160User("Say hello", 1)], 2));
        Check(Sync160Level(omitted.Message) is null && Sync160Input(omitted.Body) == "user" && omitted.Body["reasoning"] is null, "no off level: " + omitted.Body.ToJsonString());
        // max-thinking.test.ts: the max level is the last input item on a managed model, and reasoning.effort on any other.
        const string maxLevels = """{"xhigh":"xhigh","max":"max"}""";
        var managedMax = Sync160Codex(Sync160CodexRow("gpt-6.1-terra", managed, maxLevels));
        var max = managedMax.Transport.BuildBody(new(managedMax.Model, [Entry("""{"role":"system","content":"You are a helpful assistant.","timestamp":0}"""), Sync160User("Hello", 1)], 2) { ThinkingLevel = "max" });
        JsonEqual("""{"type":"configuration_update","reasoning":{"effort":"max"}}""", max["input"]!.AsArray()[^1]!.ToJsonString(), "max as a configuration_update item");
        Equal(fixedEffort, max["reasoning"]!.ToJsonString(), "max under the fixed effort");
        var directMax = Sync160Codex(Sync160CodexRow("gpt-5.6-sol", thinkingLevelMap: maxLevels));
        Equal("""{"effort":"max","summary":"auto"}""", directMax.Transport.BuildBody(new(directMax.Model, [Sync160User("Hello", 1)], 2) { ThinkingLevel = "max" })["reasoning"]!.ToJsonString(), "max as reasoning.effort");

        // Without compat.supportsMidConvoEffort the level travels as reasoning.effort; off is effort none with the summary setting.
        var direct = Sync160Codex(Sync160CodexRow("gpt-5.6-sol"));
        var high = await Sync160CodexTurn(direct.Transport, direct.Http, new(direct.Model, [Sync160User("Say hello", 1)], 2) { ThinkingLevel = "high" });
        Check(Sync160Level(high.Message) is null, "an unmanaged reply recorded a providerThinkingLevel");
        Equal("""{"effort":"high","summary":"auto"}""", high.Body["reasoning"]!.ToJsonString(), "direct reasoning"); Equal("user", Sync160Input(high.Body), "direct input");
        foreach (var level in new[] { null, "off" })
        {
            var none = await Sync160CodexTurn(direct.Transport, direct.Http, new(direct.Model, [Sync160User("Say hello", 1)], 2) { ThinkingLevel = level });
            Equal("""{"effort":"none","summary":"auto"}""", none.Body["reasoning"]!.ToJsonString(), "direct off reasoning");
            Check(Sync160Level(none.Message) is null && Sync160Input(none.Body) == "user", "direct off input");
        }
        // A replayed reply of an unmanaged request, or of another model, contributes no item.
        var replayed = await Sync160CodexTurn(transport, http, new(model, [Sync160User("Say hello", 1), Sync160Reply(high.Message with { Model = "gpt-6.2-sol" }), Sync160User("Again", 3)], 4) { ThinkingLevel = "low" });
        Equal("user,assistant,user,configuration_update:low", Sync160Input(replayed.Body), "a reply without a recorded level");
    }

    // The Codex provider ignored maxTokens; it is sent as max_output_tokens (at least 16) unless compat.supportsMaxOutputTokens is false.
    private static Task Sync160CodexMaxOutputTokens()
    {
        var request = new Func<ModelDescriptor, ChatRequest>(model => new(model, [Sync160User("Say hello", 1)], 2));
        JsonNode? Limit(JsonData row, double? maxTokens)
        {
            var (transport, _, model) = Sync160Codex(row, new() { MaxTokens = maxTokens });
            return transport.BuildBody(request(model))["max_output_tokens"];
        }
        Equal("5000", Limit(Sync160CodexRow("gpt-6.2-sol"), 5000)!.ToJsonString(), "maxTokens");
        Equal("16", Limit(Sync160CodexRow("gpt-6.2-sol"), 4)!.ToJsonString(), "the Responses minimum");
        Check(Limit(Sync160CodexRow("gpt-6.2-sol", """{"supportsMaxOutputTokens":false}"""), 5000) is null, "compat.supportsMaxOutputTokens false sent max_output_tokens");
        // streamSimple's buildBaseOptions: no maxTokens means the model's, and either is fitted into the context window
        // (the input estimate of "Say hello" is 3 tokens; a 10000 token window leaves 10000 - 3 - 4096).
        Equal("128000", Limit(Sync160CodexRow("gpt-6.2-sol"), null)!.ToJsonString(), "the model's maxTokens");
        Equal("5901", Limit(Sync160CodexRow("small-window", contextWindow: 10000, maxTokens: 8000), null)!.ToJsonString(), "fitted into the context window");
        Equal("5901", Limit(Sync160CodexRow("small-window", contextWindow: 10000, maxTokens: 8000), 9000)!.ToJsonString(), "an explicit limit is fitted too");
        // The member follows parallel_tool_calls, before temperature.
        var (ordered, _, orderedModel) = Sync160Codex(Sync160CodexRow("gpt-6.2-sol"), new() { MaxTokens = 5000, Temperature = 0.2 });
        var names = ordered.BuildBody(request(orderedModel)).Select(pair => pair.Key).ToList();
        Check(names.IndexOf("max_output_tokens") == names.IndexOf("parallel_tool_calls") + 1 && names.IndexOf("temperature") == names.IndexOf("max_output_tokens") + 1,
            "member order: " + string.Join(",", names));
        return Task.CompletedTask;
    }

    // #12269 over the WebSocket: with the level in configuration_update items the request body outside input stays the same, so a
    // level change keeps sending only the new input with previous_response_id.
    private static async Task Sync160CodexWebSocketEffortDeltas()
    {
        await using var server = new CodexServer();
        var frames = 0;
        server.OnFrame = (connection, _) =>
        {
            var number = Interlocked.Increment(ref frames);
            return connection.SendAllAsync(Sync160CodexEvents("resp_" + number).Select(data => data.Replace("msg_1", "msg_" + number, StringComparison.Ordinal)));
        };
        const string session = "sess-ws-effort-deltas";
        var row = Sync160CodexRow("gpt-6.2-sol", """{"supportsMidConvoEffort":true}""", baseUrl: server.BaseUrl);
        var model = new ModelDescriptor("gpt-6.2-sol", "openai-codex-responses", "openai-codex");
        var transport = new OpenAICodexResponsesTransport(new HttpClient(new SocketsHttpHandler { UseProxy = false }), model, row,
            new() { SessionId = session, Transport = () => "websocket-cached", Time = new FixedTime(1_800_000_000_000) }, _ => ValueTask.FromResult(CodexToken));
        try
        {
            var messages = ImmutableArray<TranscriptEntry>.Empty; var turn = 0;
            foreach (var level in new[] { "low", "high", "high" })
            {
                messages = messages.Add(Sync160User("Turn " + ++turn, turn));
                var terminal = (await Collect(transport, new(model, messages, 9) { ThinkingLevel = level }))[^1];
                messages = messages.Add(Sync160Reply((terminal as StreamDone ?? throw new CheckException("done: " + ErrorMessage(terminal))).Message));
            }
            var sent = server.Frames.Select(frame => JsonNode.Parse(frame.Text)!).ToList();
            Equal(3, sent.Count, "one frame per turn");
            Check(sent.All(body => body["reasoning"]!.ToJsonString() == """{"effort":"medium","summary":"auto"}"""), "a frame changed reasoning.effort");
            Equal(",resp_1,resp_2", string.Join(",", sent.Select(body => (string?)body["previous_response_id"])), "previous_response_id");
            Equal("user,configuration_update:low|user,configuration_update:high|user,configuration_update:high", string.Join("|", sent.Select(Sync160Input)), "input deltas");
            Check(OpenAICodexWebSockets.GetDebugStats(session) is { FullContextRequests: 1, DeltaRequests: 2, ConnectionsCreated: 1 }, "stats: " + OpenAICodexWebSockets.GetDebugStats(session));
            Equal(1, server.Upgrades.Count, "one connection"); Check(server.Posts.IsEmpty, "no SSE request");
        }
        finally { OpenAICodexWebSockets.CloseSessions(session); OpenAICodexWebSockets.ResetDebugStats(session); }
    }

    // #12124: the Codex backend closes a Responses WebSocket after 60 minutes, even mid-response. Once anything has streamed (response
    // metadata, rate-limit metadata or thinking) the provider cannot reconnect in place; the message ends as an error that the shared
    // retry classifier recognizes, and the retry opens a fresh connection and sends the full context.
    private static async Task Sync160CodexConnectionLimitAfterStart()
    {
        const string limit = """{"type":"error","error":{"code":"websocket_connection_limit_reached","message":"Responses websocket connection limit reached (60 minutes). Create a new websocket connection to continue."}}""";
        const string text = "Codex error: Responses websocket connection limit reached (60 minutes). Create a new websocket connection to continue.";
        foreach (var (name, started) in new (string, string[])[]
        {
            ("response-metadata", ["""{"type":"response.created","response":{"id":"resp_expired"}}"""]),
            ("rate-limit-metadata", ["""{"type":"codex.rate_limits","rate_limits":{"allowed":true,"limit_reached":false}}"""]),
            ("partial-thinking", ["""{"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[]}}""",
                """{"type":"response.reasoning_summary_part.added","output_index":0,"summary_index":0,"part":{"type":"summary_text","text":""}}""",
                """{"type":"response.reasoning_summary_text.delta","output_index":0,"summary_index":0,"delta":"unfinished thinking"}"""]),
        })
        {
            await using var server = new CodexServer();
            var expire = 0;
            server.OnFrame = (connection, _) => connection.SendAllAsync(Interlocked.Exchange(ref expire, 0) == 1 ? (string[])[.. started, limit] : CodexCompleted);
            server.OnPost = _ => CodexCompleted;
            var session = "sess-ws-connection-limit-" + name;
            var (transport, model, _) = CodexOver(server, "websocket-cached", session);
            try
            {
                // An earlier turn leaves a cached connection with a continuation, as in a long session.
                var first = (StreamDone)(await Collect(transport, Hi(model)))[^1];
                Volatile.Write(ref expire, 1);
                var next = new ChatRequest(model, [Entry("""{"role":"user","content":"Hi","timestamp":2}"""), new TranscriptEntry("assistant", PiWireJson.WriteMessage(first.Message)),
                    Entry("""{"role":"user","content":"More","timestamp":4}""")], 5);
                var failed = (await Collect(transport, next))[^1];
                Check(failed is StreamError { Reason: StopReason.Error }, name + ": the limit did not end the message as an error");
                Equal(text, ErrorMessage(failed), name + ": error text");
                Check(AgentRetryPolicy.IsRetryableError(StopReason.Error, ErrorMessage(failed)), name + ": the limit is not retryable");
                if (name == "partial-thinking")
                    Check(((StreamError)failed).Message.Content.Single() is ThinkingContent { Thinking: "unfinished thinking" }, name + ": partial thinking lost");
                // A Codex error is not a transport failure: no diagnostic, no SSE fallback for the session.
                Check(Diagnostics(failed) is null && server.Posts.IsEmpty && OpenAICodexWebSockets.GetDebugStats(session) is { WebSocketFailures: 0, SseFallbacks: 0 }, name + ": fell back to SSE");
                Check(JsonNode.Parse(server.Frames[1].Text)!["previous_response_id"] is not null, name + ": the failed request was not a delta");
                Check(await server.WaitAsync(() => server.Closes.Contains("1000 done")), name + ": the expired connection was not closed");
                // The retry of the same request: a second connection, the full context, and it completes.
                var retried = (await Collect(transport, next))[^1];
                Check(retried is StreamDone done && done.Message.Content.Single() is TextContent { Text: "Hello" }, name + ": the retry did not recover: " + ErrorMessage(retried));
                Equal(2, server.Upgrades.Count, name + ": connections");
                Equal(3, server.Frames.Count, name + ": frames");
                var body = JsonNode.Parse(server.Frames[2].Text)!;
                Check(body["previous_response_id"] is null && body["input"]!.AsArray().Count == 3 && server.Frames[2].Connection != server.Frames[1].Connection,
                    name + ": the retry did not send the full context on a fresh connection");
                Check(server.Posts.IsEmpty, name + ": the retry used SSE");
            }
            finally { OpenAICodexWebSockets.CloseSessions(session); OpenAICodexWebSockets.ResetDebugStats(session); }
        }
    }

    // openai_input_transformations on the Codex provider: Codex reports the removals in its response.done event.
    private static async Task Sync160CodexInputTransformations()
    {
        const string prefix = """{"type":"reasoning","id":"rs_edited","action":"removed","reason":"prefix_mismatch"}""";
        const string turn = """{"type":"reasoning","id":"rs_old_turn","action":"removed","reason":"turn_boundary"}""";
        const string terminal = """{"type":"response.done","response":{"id":"resp_codex","status":"completed","output":[],"input_transformations":[""" + prefix + "," + turn + "]}}";
        const string diagnostic = """{"type":"openai_input_transformations","timestamp":1800000000000,"details":{"transformations":[""" + prefix + "]}}";
        var (transport, http, model, _) = Codex(id: "gpt-5.6-sol");
        http.OnUrl("https://chatgpt.com/", _ => Sse(terminal));
        var done = (await Collect(transport, Hi(model)))[^1];
        Check(done is StreamDone { Reason: StopReason.Stop }, "done: " + ErrorMessage(done));
        JsonEqual("[" + diagnostic + "]", Diagnostics(done)!.ToJsonString(), "codex diagnostics");
        // A terminal without removals leaves no diagnostic.
        var (plain, plainHttp, plainModel, _) = Codex(id: "gpt-5.6-sol");
        plainHttp.OnUrl("https://chatgpt.com/", _ => Sse("""{"type":"response.done","response":{"id":"resp_codex","status":"completed","output":[],"input_transformations":[""" + turn + "]}}"));
        Check((await Collect(plain, Hi(plainModel)))[^1] is StreamDone quiet && Diagnostics(quiet) is null, "a turn boundary left a diagnostic");
        // After a WebSocket failure that fell back to SSE, the transport diagnostic recorded first stays first.
        await using var server = new CodexServer { RejectUpgrade = true };
        server.OnPost = _ => [terminal];
        var (fallback, fallbackModel, _) = CodexOver(server, "auto");
        var fallen = (await Collect(fallback, Hi(fallbackModel)))[^1];
        var diagnostics = Diagnostics(fallen as StreamDone ?? throw new CheckException("fallback: " + ErrorMessage(fallen)))!;
        Equal("provider_transport_failure,openai_input_transformations", string.Join(",", diagnostics.Select(item => (string?)item!["type"])), "diagnostic order");
        JsonEqual(diagnostic, diagnostics[1]!.ToJsonString(), "codex diagnostic after a fallback");
    }

    // #12128 and #12064 for the native transports that show the failure's own text (OpenAI Responses and Codex): what a real connection
    // failure reads on this platform is classified as retryable, where Node would report "read ECONNRESET" or "Connection closed.".
    // A loopback listener resets or drops the connection where the response head or the rest of the event stream would be.
    private static async Task Sync160LiveConnectionFailures()
    {
        const string head = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n";
        static string Chunk(string data) => Encoding.UTF8.GetByteCount(data).ToString("x") + "\r\n" + data + "\r\n";
        var started = Chunk("data: {\"type\":\"response.created\",\"response\":{\"id\":\"r1\"}}\n\n");
        foreach (var codex in new[] { false, true })
        foreach (var (scenario, respond, reset) in new (string, string?, bool)[]
        {
            ("reset-before-the-response", null, true), ("closed-before-the-response", null, false),
            ("reset-mid-stream", head + started, true), ("closed-mid-stream", head + started, false),
        })
        {
            var name = (codex ? "codex " : "responses ") + scenario;
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serve = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync();
                var stream = peer.GetStream();
                _ = await HttpHead.ReadAsync(stream, CancellationToken.None);
                if (respond is not null) { await stream.WriteAsync(Encoding.ASCII.GetBytes(respond)); await stream.FlushAsync(); await Task.Delay(100); }
                // SO_LINGER 0 turns the close into a reset (ECONNRESET at the client); otherwise the peer just goes away.
                if (reset) peer.LingerState = new LingerOption(true, 0);
                peer.Close();
            });
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            var model = codex ? new ModelDescriptor("gpt-live", "openai-codex-responses", "openai-codex") : new ModelDescriptor("gpt-live", "openai-responses", "loopback");
            IChatTransport transport = codex
                ? new OpenAICodexResponsesTransport(client, model, Sync160CodexRow("gpt-live", baseUrl: $"http://127.0.0.1:{port}/backend-api"), new() { Transport = () => "sse" }, _ => ValueTask.FromResult(CodexToken))
                : new ResponsesHttpSseTransport(client, new ResponsesKeyAuthRequestFactory(new($"http://127.0.0.1:{port}/v1/responses"), model, new(false)), "inert-loopback-key");
            var terminal = (await Collect(transport, new(model, [Sync160User("Hi", 1)], 2)))[^1];
            await serve;
            Check(terminal is StreamError { Reason: StopReason.Error }, name + ": the dropped connection did not end as an error");
            var error = ErrorMessage(terminal);
            Check(AgentRetryPolicy.IsRetryableError(StopReason.Error, error), name + ": not retryable: " + error);
        }
    }
}
