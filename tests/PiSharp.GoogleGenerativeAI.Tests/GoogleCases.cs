using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Providers;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

/// <summary>Authored expected values derived from pinned Source reads; not genuine SDK/Source captures.</summary>
internal static class GoogleCases
{
    private static readonly ModelDescriptor Model = new("gemini-3-flash-preview", "google-generative-ai", "google");
    private const string Key = "inert-authored-google-key";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private const string ToolChunk = """{"responseId":"first","candidates":[{"content":{"parts":[{"functionCall":{"id":"call-1","name":"inspect","args":{"value":7,"keep":null}},"thoughtSignature":"c2ln"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":11,"cachedContentTokenCount":3,"candidatesTokenCount":2,"thoughtsTokenCount":4,"totalTokenCount":17}}""";
    private const string TextChunk = """{"responseId":"first","candidates":[{"content":{"parts":[{"text":"π"}]},"finishReason":"STOP"}]}""";
    private const string Declaration = """{"role":"system","content":"base","toolsAdded":[{"name":"inspect","description":"inert","parameters":{"type":"object","properties":{"value":{"type":"number"},"keep":{"type":"null"}},"required":["value"]}}],"timestamp":123}""";
    public static IEnumerable<(string Id, Func<Task> Run)> Cases()
    {
        yield return ("google.request-projection-and-auth", Requests);
        yield return ("google.history-signatures-images-tools", History);
        yield return ("google.history-id-normalization-before-filtering", GoogleHistoryIdCases.RunAsync);
        yield return ("google.thinking-and-strict-schema", ThinkingAndStrict);
        yield return ("google.strict-union-nullability-nested-schema", GoogleStrictSchemaCases.RunAsync);
        yield return ("google.fragmentation-text-thinking-tool-usage", Fragmentation);
        yield return ("google.finish-and-invalid-tool-authority", FinishAndInvalidTools);
        yield return ("google.payload-provider-hooks-and-faults", HooksAndFaults);
        yield return ("google.cancel-joins-send-hook-read-cleanup", CancellationOwnership);
        yield return ("google.primary-error-survives-held-cleanup", PrimaryAndCleanup);
        yield return ("google.limits-before-effects-and-during-stream", Limits);
        yield return ("google.provider-binding-and-actual-chat", ProviderBinding);
        yield return ("google.released-catalog-all-22-row-routing", ReleasedCatalog);
        yield return ("google.agent-held-cleanup-tool-result-next-request", AgentRoundTrip);
        yield return ("google.agent-errors-and-length-have-no-authority", AgentFailures);
        yield return ("google.iterator-disposal-single-reader-borrowed-client", IteratorOwnership);
        foreach (var diagnosticCase in GoogleNativeDiagnosticCases.Cases()) yield return diagnosticCase;
        foreach (var correctionCase in GooglePreterminalDiagnosticCases.Cases()) yield return correctionCase;
        foreach (var retryCase in GoogleRetryCases.Cases()) yield return retryCase;
        foreach (var simpleCase in GoogleSimpleCases.Cases()) yield return simpleCase;
    }
    private static FrozenModelCatalog Catalog(string id = "gemini-3-flash-preview", bool image = true, string? map = null)
    {
        var row = new JsonObject { ["type"] = "chat", ["id"] = id, ["api"] = Model.Api, ["provider"] = "google",
            ["name"] = "authored", ["baseUrl"] = "https://google.invalid/v1beta", ["reasoning"] = true,
            ["input"] = image ? new JsonArray("text", "image") : new JsonArray("text"), ["contextWindow"] = 100000, ["maxTokens"] = 1000,
            ["cost"] = new JsonObject { ["input"] = 2.0, ["output"] = 3.0, ["cacheRead"] = 0.5, ["cacheWrite"] = 0 },
            ["opaque"] = new JsonObject { ["owned"] = null }, ["headers"] = new JsonObject { ["x-model"] = "preserved" } };
        if (map is not null) row["thinkingLevelMap"] = JsonNode.Parse(map);
        return FrozenModelCatalog.ReadProviderJson("google", Encoding.UTF8.GetBytes(
            new JsonObject { [Model.Api] = new JsonObject { ["chat:" + id] = row } }.ToJsonString()));
    }
    private static GoogleGenerativeAIOptions Options() => new(Catalog().Models[0].Raw, Key);
    private static ImmutableArray<TranscriptEntry> Inputs() => [new("system", JsonData.Parse(Declaration)),
        new("user", JsonData.Parse("""{"role":"user","content":"hello π","timestamp":123}"""))];
    private static ChatRequest Request() => new(Model, Inputs(), 123);
    private static string Wire(params string[] chunks) => string.Concat(chunks.Select(x => "data: " + x + "\n\n"));
    private static GoogleGenerativeAIHttpTransport Transport(HttpClient client, GoogleGenerativeAIOptions? options = null) => new(client, Model, options ?? Options());
    private static HttpResponseMessage Response(OwnedBody body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new BodyContent(body) };
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");
    private static void Same(JsonElement expected, JsonElement actual) => Check(JsonElement.DeepEquals(expected, actual), "Owned JSON differs.");
    private static string Error(ChatResult result) => result.Message.ExtraProperties!.Values["errorMessage"].Value.GetString()!;
    private static async Task Join(Task original) { try { await original; } catch { } }
    private static async Task<ChatResult> Complete(string wire, GoogleGenerativeAIOptions? options = null, bool cleanupFault = false)
    {
        var body = new OwnedBody(wire) { FailCleanup = cleanupFault };
        using var handler = new Handler((_, _) => Task.FromResult(Response(body)));
        using var client = new HttpClient(handler);
        var result = await new ChatClient(Transport(client, options), capacity: 1).CompleteAsync(Request());
        Check(body.Disposed && !handler.Disposed, "Ownership not settled or borrowed client disposed."); return result;
    }
    private static async Task Requests()
    {
        var sourceRequest = Request();
        var untouched = sourceRequest.Messages.Select(x => x.WireBody.ToString()).ToArray();
        var options = Options() with { Temperature = 0.25, MaxTokens = 12.5,
            ToolChoice = "any", Thinking = new(true, Level: "LOW"), Headers = JsonData.Parse("""{"x-model":"override","content-type":null}""") };
        var sdk = GoogleRequestProjector.Project(sourceRequest, options).Value;
        Equal(Model.Id, sdk.GetProperty("model").GetString()); Equal("base", sdk.GetProperty("config").GetProperty("systemInstruction").GetString());
        Equal("ANY", sdk.GetProperty("config").GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString());
        using var actual = await new GoogleKeyAuthRequestFactory(Model, options).CreateAsync(sourceRequest);
        Equal("https://google.invalid/v1beta/models/gemini-3-flash-preview:streamGenerateContent?alt=sse", actual.RequestUri!.AbsoluteUri);
        Equal(Key, actual.Headers.GetValues("x-goog-api-key").Single()); Equal("override", actual.Headers.GetValues("x-model").Single());
        Check(!actual.RequestUri.AbsoluteUri.Contains(Key, StringComparison.Ordinal) && !actual.Content!.Headers.Contains("content-type"), "Key leaked into URL or null header was ignored.");
        var body = JsonData.Parse(await actual.Content!.ReadAsStringAsync()).Value;
        Check(!body.TryGetProperty("config", out _) && !body.TryGetProperty("model", out _), "SDK parameters were sent without REST projection.");
        Equal("base", body.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Equal(12.5, body.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetDouble());
        for (var i = 0; i < untouched.Length; i++) Equal(untouched[i], sourceRequest.Messages[i].WireBody.ToString());
        Equal(nameof(GoogleGenerativeAIOptions), options.ToString()); Check(!options.ToString().Contains(Key, StringComparison.Ordinal), "Options status leaked key.");
        var sends = 0; var payloads = 0;
        using var handler = new Handler((_, _) => { sends++; return Task.FromResult(Response(new OwnedBody(Wire(TextChunk)))); });
        using var client = new HttpClient(handler);
        var missing = Options() with { ApiKey = "", Hooks = new() { OnPayload = (_, _, _) => { payloads++; return ValueTask.FromResult<JsonData?>(null); } } };
        var failure = await new ChatClient(Transport(client, missing)).CompleteAsync(Request());
        Check(failure.Failure is not null && sends == 0 && payloads == 0, "Missing key admitted callbacks or HTTP.");
        var wrong = await new ChatClient(Transport(client)).CompleteAsync(Request() with { Model = Model with { Id = "wrong" } });
        Check(wrong.Failure is not null && sends == 0, "Mismatched direct identity sent HTTP.");
    }
    private static Task History()
    {
        var messages = new List<TranscriptEntry> {
            new("system", JsonData.Parse("""{"role":"system","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}],"sections":{"one":"old","two":"keep"},"toolsAdded":[{"name":"old","parameters":{"type":"object"}}]}""")),
            new("user", JsonData.Parse("""{"role":"user","content":[{"type":"image","mimeType":"image/png","data":"aW0="}]}""")),
            new("assistant", JsonData.Parse("""{"role":"assistant","provider":"google","api":"google-generative-ai","model":"gemini-3-flash-preview","stopReason":"toolUse","content":[{"type":"thinking","thinking":"","thinkingSignature":"c2ln"},{"type":"text","text":"","textSignature":"dGV4dA=="},{"type":"toolCall","id":"call|owned","name":"inspect","arguments":{"value":7},"thoughtSignature":"c2ln"}]}""")),
            new("toolResult", JsonData.Parse("""{"role":"toolResult","toolCallId":"call|owned","toolName":"inspect","content":[{"type":"image","mimeType":"image/png","data":"aW0="}],"isError":false}""")),
            new("system", JsonData.Parse("""{"role":"system","content":"later","sections":{"one":null,"two":"new","three":""},"toolsRemoved":[{"name":"old"}],"toolsAdded":[{"name":"inspect","parameters":{"type":"object"}}]}""")) };
        var request = Request() with { Messages = messages.ToImmutableArray() };
        var payload = GoogleRequestProjector.Project(request, Options()).Value;
        Equal("a\nb\n\nlater\n\nnew", payload.GetProperty("config").GetProperty("systemInstruction").GetString());
        var contents = payload.GetProperty("contents"); Equal(3, contents.GetArrayLength());
        var parts = contents[1].GetProperty("parts"); Equal(3, parts.GetArrayLength());
        Equal(true, parts[0].GetProperty("thought").GetBoolean()); Equal("c2ln", parts[0].GetProperty("thoughtSignature").GetString());
        Equal("call|owned", parts[2].GetProperty("functionCall").GetProperty("id").GetString());
        var response = contents[2].GetProperty("parts")[0].GetProperty("functionResponse");
        Equal("(see attached image)", response.GetProperty("response").GetProperty("output").GetString());
        Equal("aW0=", response.GetProperty("parts")[0].GetProperty("inlineData").GetProperty("data").GetString());
        Equal("inspect", payload.GetProperty("config").GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("name").GetString());
        var cross = messages.ToArray(); cross[2] = new("assistant", JsonData.Parse(cross[2].WireBody.ToString().Replace("\"google\"", "\"foreign\"", StringComparison.Ordinal)));
        var crossContents = GoogleRequestProjector.Project(request with { Messages = cross.ToImmutableArray() }, Options()).Value.GetProperty("contents");
        Equal(1, crossContents[1].GetProperty("parts").GetArrayLength());
        var call = crossContents[1].GetProperty("parts")[0]; Check(!call.TryGetProperty("thoughtSignature", out _), "Foreign signature retained.");
        Equal("call_owned", call.GetProperty("functionCall").GetProperty("id").GetString());
        Equal("call_owned", crossContents[2].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("id").GetString());
        // Every Gemini <3 row moves tool images to a separate user turn.
        var oldCatalog = Catalog("gemini-2.5-flash"); var oldOptions = new GoogleGenerativeAIOptions(oldCatalog.Models[0].Raw, Key);
        var oldContents = GoogleRequestProjector.Project(request with { Model = Model with { Id = "gemini-2.5-flash" } }, oldOptions).Value.GetProperty("contents");
        Equal(4, oldContents.GetArrayLength()); Check(!oldContents[2].GetProperty("parts")[0].GetProperty("functionResponse").TryGetProperty("parts", out _), "Pre3 image nested in function response.");
        Equal("Tool result image:", oldContents[3].GetProperty("parts")[0].GetProperty("text").GetString());
        // Missing results are synthesized; invalid assistant history is skipped.
        var orphan = request with { Messages = [messages[2]] };
        var orphaned = GoogleRequestProjector.Project(orphan, Options()).Value.GetProperty("contents");
        Equal("No result provided", orphaned[1].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("response").GetProperty("error").GetString());
        var noVision = new GoogleGenerativeAIOptions(Catalog(image: false).Models[0].Raw, Key);
        var downgraded = GoogleRequestProjector.Project(request, noVision).Value.GetProperty("contents");
        Equal("(image omitted: model does not support images)", downgraded[0].GetProperty("parts")[0].GetProperty("text").GetString());
        Equal("(tool image omitted: model does not support images)", downgraded[2].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("response").GetProperty("output").GetString());
        var numericSections = Request() with { Messages = [new("system", JsonData.Parse("""{"role":"system","content":"","sections":{"10":"ten","2":"two","owned":"last"}}"""))] };
        Equal("two\n\nten\n\nlast", GoogleRequestProjector.Project(numericSections, Options()).Value.GetProperty("config").GetProperty("systemInstruction").GetString());
        var blankBlocks = Request() with { Messages = [new("assistant", JsonData.Parse("""{"role":"assistant","provider":"google","api":"google-generative-ai","model":"gemini-3-flash-preview","stopReason":"stop","content":[{"type":"text","text":"\uFEFF"},{"type":"text","text":"\u0085"}]}"""))] };
        var blankParts = GoogleRequestProjector.Project(blankBlocks, Options()).Value.GetProperty("contents")[0].GetProperty("parts");
        Equal(1, blankParts.GetArrayLength()); Equal("\u0085", blankParts[0].GetProperty("text").GetString());
        return Task.CompletedTask;
    }
    private static async Task ThinkingAndStrict()
    {
        foreach (var (id, map, expected) in new[] {
            ("gemini-2.5-pro", (string?)null, """{"thinkingBudget":0}"""),
            ("gemini-3-flash-preview", """{"off":null,"minimal":null,"low":"LOW","medium":"MEDIUM","high":"HIGH"}""", """{"thinkingLevel":"LOW"}"""),
            ("gemini-flash-latest", (string?)null, """{"thinkingBudget":0}""") })
        {
            var row = Catalog(id, map: map).Models[0]; var model = Model with { Id = id };
            var actual = GoogleRequestProjector.Project(Request() with { Model = model }, new(row.Raw, Key) { Thinking = new(false) }).Value;
            Same(JsonData.Parse(expected).Value, actual.GetProperty("config").GetProperty("thinkingConfig"));
        }
        var strictDeclaration = JsonData.Parse("""{"role":"system","content":"","toolsAdded":[{"name":"inspect","constrainedSampling":{"type":"json_schema","strict":"require"},"parameters":{"type":"object","properties":{"optional":{"type":"string"},"required":{"type":"number"}},"required":["required"]}}]}""");
        var request = Request() with { Messages = [new("system", strictDeclaration)] };
        var config = GoogleRequestProjector.Project(request, Options()).Value.GetProperty("config");
        var schema = config.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("parametersJsonSchema");
        Equal(false, schema.GetProperty("additionalProperties").GetBoolean()); Equal(2, schema.GetProperty("required").GetArrayLength());
        Equal("null", schema.GetProperty("properties").GetProperty("optional").GetProperty("anyOf")[1].GetProperty("type").GetString());
        Equal("VALIDATED", config.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString());
        var unsupported = new TranscriptEntry("system", JsonData.Parse(strictDeclaration.ToString().Replace("\"type\":\"object\",\"properties\"", "\"$ref\":\"#/owned\",\"type\":\"object\",\"properties\"", StringComparison.Ordinal)));
        var sends = 0;
        using var handler = new Handler((_, _) => { sends++; return Task.FromResult(Response(new OwnedBody(Wire(TextChunk)))); });
        using var client = new HttpClient(handler);
        var result = await new ChatClient(Transport(client)).CompleteAsync(request with { Messages = [unsupported] });
        Check(result.Failure is not null && sends == 0, "Required unsupported strict schema admitted HTTP.");
        var preferred = new TranscriptEntry("system", JsonData.Parse(unsupported.WireBody.ToString().Replace("\"require\"", "\"prefer\"", StringComparison.Ordinal)));
        var fallback = GoogleRequestProjector.Project(request with { Messages = [preferred] }, Options()).Value.GetProperty("config");
        Check(!fallback.TryGetProperty("toolConfig", out _) && fallback.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("parametersJsonSchema").TryGetProperty("$ref", out _), "Prefer did not retain unchanged schema.");
    }
    private static async Task Fragmentation()
    {
        var first = """{"responseId":"first","candidates":[{"content":{"parts":[{"text":"","thought":true,"thoughtSignature":"c2ln"},{"text":"reason","thought":true},{"text":"π","thoughtSignature":"dGV4dA=="}]}}]}""";
        var second = """{"responseId":"later","candidates":[{"content":{"parts":[{"text":"tail"},{"functionCall":{"id":"call-1","name":"inspect","args":{"value":1.0,"keep":null}},"thoughtSignature":"c2ln"},{"functionCall":{"id":"call-1","name":"inspect","args":null}}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":11,"cachedContentTokenCount":3,"candidatesTokenCount":2,"thoughtsTokenCount":4,"totalTokenCount":17}}""";
        foreach (var fragment in new[] { 1, 2, 3, 7, 4096 })
        foreach (var ending in new[] { "\n", "\r\n", "\r" })
        {
            var wire = Wire(first, second).Replace("\n", ending, StringComparison.Ordinal);
            var body = new OwnedBody(wire) { Fragment = fragment };
            using var handler = new Handler((_, _) => Task.FromResult(Response(body)));
            using var client = new HttpClient(handler);
            var events = new List<StreamEvent>(); var reducer = new AssistantStreamReducer(new(Model.Api, Model.Provider, Model.Id, 123, [], TokenUsage.Zero, StopReason.Pending));
            await foreach (var frame in Transport(client).StreamAsync(Request())) { events.Add(frame); reducer.Apply(frame); }
            var terminal = (StreamDone)events[^1];
            Equal(StopReason.ToolUse, terminal.Reason); Equal(4, terminal.Message.Content.Length);
            var thinking = (ThinkingContent)terminal.Message.Content[0];
            Equal("reason", thinking.Thinking); Equal("c2ln", thinking.ExtraProperties!.Values["thinkingSignature"].Value.GetString());
            var text = (TextContent)terminal.Message.Content[1];
            Equal("πtail", text.Text); Equal("dGV4dA==", text.ExtraProperties!.Values["textSignature"].Value.GetString());
            var tools = terminal.Message.Content.OfType<ToolCallContent>().ToArray(); Equal("call-1", tools[0].Id);
            Check(tools[1].Id != "call-1" && tools[1].Id.StartsWith("inspect_123_", StringComparison.Ordinal), "Duplicate ID was not regenerated.");
            Equal("{}", tools[1].Arguments.ToString());
            var toolDelta = events.OfType<ToolCallDelta>().First().Delta; Equal("""{"value":1,"keep":null}""", toolDelta);
            Equal("first", terminal.Message.ExtraProperties!.Values["responseId"].Value.GetString());
            Equal("STOP", terminal.Message.ExtraProperties.Values["rawStopReason"].Value.GetString());
            var usage = terminal.Message.Usage; Equal(8L, usage.Input); Equal(6L, usage.Output); Equal(3L, usage.CacheRead); Equal(17L, usage.TotalTokens);
            Equal(4L, usage.ExtraProperties!.Values["reasoning"].Value.GetInt64());
            Equal(0.000016m, usage.Cost.Input); Equal(0.000018m, usage.Cost.Output); Equal(0.0000015m, usage.Cost.CacheRead);
            Check(usage.Cost.SourceBinary64Cost is not null && events.OfType<ThinkingEnded>().Count() == 1 &&
                events.OfType<TextEnded>().Count() == 1 && events.OfType<ToolCallEnded>().Count() == 2, "Block/usage lifecycle differs.");
            Same(PiWireJson.WriteMessage(terminal.Message).Value, PiWireJson.WriteMessage(reducer.Snapshot()).Value);
            Check(body.Disposed && !handler.Disposed, "Fragmented read ownership leaked.");
        }
        // Only first candidate is mapped, and signatures alone do not mark thinking.
        var ignored = await Complete(Wire("""{"candidates":[{"content":{"parts":[{"text":"visible","thoughtSignature":"c2ln"}]},"finishReason":"STOP"},{"content":{"parts":[{"text":"ignored"}]}}]}"""));
        Equal("visible", ((TextContent)ignored.Message.Content.Single()).Text);
        // @google/genai processStreamResponse framing: events split at \n\n, \r\r or \r\n\r\n; non-data events are skipped.
        foreach (var framing in new[] { "\uFEFF: ignored comment\n\ndata: " + TextChunk + "\n\n", "data: " + TextChunk + "\r\r",
            "data: " + TextChunk + "\r\n\r\n", "event: ignored\n\n  data:" + TextChunk + "  \n\n\n" })
        {
            var framed = await Complete(framing, Options() with { ReadBufferBytes = 1 });
            Check(framed.Failure is null, "Authored native framing failed."); Equal("π", ((TextContent)framed.Message.Content.Single()).Text);
        }
        // Multi-line data is one JSON text, an unterminated frame is an incomplete segment and [DONE] is not JSON.
        foreach (var (framing, error) in new[] {
            ("data: {\"candidates\":\ndata: [{\"content\":{\"parts\":[{\"text\":\"π\"}]},\"finishReason\":\"STOP\"}]}\n\n", "Unexpected token 'd', ...\"didates\":\ndata: [{\"c\"... is not valid JSON"),
            ("data: " + TextChunk, "Incomplete JSON segment at the end"),
            (Wire(TextChunk) + "data: [DONE]\n\n", "Unexpected token 'D', \"[DONE]\" is not valid JSON") })
        {
            var framed = await Complete(framing, Options() with { ReadBufferBytes = 1 });
            Check(framed.Failure is not null, "Upstream-rejected framing accepted."); Equal(error, Error(framed));
        }
        var raw = JsonNode.Parse(Options().ModelMetadata.ToString())!;
        raw["cost"]!["tiers"] = JsonNode.Parse("""[{"inputTokensAbove":20,"input":9,"output":9,"cacheRead":9,"cacheWrite":0},{"inputTokensAbove":10,"input":4,"output":5,"cacheRead":1,"cacheWrite":0}]""");
        var tiered = await Complete(Wire(ToolChunk), Options() with { ModelMetadata = JsonData.Parse(raw.ToJsonString()) });
        Equal(0.000032m, tiered.Message.Usage.Cost.Input); Equal(0.000030000000000000004m, tiered.Message.Usage.Cost.Output);
        var boundary = await Complete(Wire(ToolChunk.Replace("\"promptTokenCount\":11", "\"promptTokenCount\":10", StringComparison.Ordinal)),
            Options() with { ModelMetadata = JsonData.Parse(raw.ToJsonString()) });
        Equal(0.000014m, boundary.Message.Usage.Cost.Input);
    }
    private static async Task FinishAndInvalidTools()
    {
        foreach (var finish in new[] { "STOP", "MAX_TOKENS", "SAFETY", "RECITATION", "BLOCKLIST", "PROHIBITED_CONTENT",
            "SPII", "IMAGE_SAFETY", "IMAGE_PROHIBITED_CONTENT", "IMAGE_RECITATION", "IMAGE_OTHER", "FINISH_REASON_UNSPECIFIED",
            "OTHER", "LANGUAGE", "MALFORMED_FUNCTION_CALL", "UNEXPECTED_TOOL_CALL", "TOO_MANY_TOOL_CALLS", "NO_IMAGE", "UNKNOWN_AUTHORED" })
        {
            var result = await Complete(Wire(ToolChunk.Replace("\"STOP\"", "\"" + finish + "\"", StringComparison.Ordinal)));
            Equal(finish == "STOP" ? StopReason.ToolUse : finish == "MAX_TOKENS" ? StopReason.Length : StopReason.Error, result.Message.StopReason);
            Equal(finish, result.Message.ExtraProperties!.Values["rawStopReason"].Value.GetString());
            Check((result.Failure is null) == (finish is "STOP" or "MAX_TOKENS"), "Finish authority differs.");
        }
        // Captured from the installed pi-ai 1.1.0 google-generative-ai stream (@google/genai 2.21.0): google-generative-ai.ts takes
        // functionCall.name || "", functionCall.args ?? {} (any JSON value; JSON.parse keeps the last duplicate name), id
        // `${functionCall.name}_${Date.now()}_${n}` when none is given, and joins any `text` value as a JavaScript string.
        foreach (var (chunk, toolName, idPrefix, arguments, text) in new (string, string?, string?, string?, string?)[] {
            ("""{"candidates":[{"content":{"parts":[{"functionCall":{"name":"inspect","args":[]}}]},"finishReason":"STOP"}]}""", "inspect", "inspect_", "[]", null),
            ("""{"candidates":[{"content":{"parts":[{"functionCall":{"name":"inspect","args":"x"}}]},"finishReason":"STOP"}]}""", "inspect", "inspect_", "\"x\"", null),
            ("""{"candidates":[{"content":{"parts":[{"functionCall":{"name":"inspect","args":{"x":1,"x":2}}}]},"finishReason":"STOP"}]}""", "inspect", "inspect_", """{"x":2}""", null),
            ("""{"candidates":[{"content":{"parts":[{"text":false}]},"finishReason":"STOP"}]}""", null, null, null, "false"),
            ("""{"candidates":[{"content":{"parts":[{"text":null},{"text":1.5},{"text":{"a":1}}]},"finishReason":"STOP"}]}""", null, null, null, "null1.5[object Object]"),
            ("""{"candidates":[{"content":{"parts":["x"]},"finishReason":"STOP"}]}""", null, null, null, null) })
        {
            var result = await Complete(Wire(chunk)); Check(result.Failure is null, "Upstream-accepted chunk failed: " + chunk);
            Equal(toolName is null ? StopReason.Stop : StopReason.ToolUse, result.Message.StopReason);
            if (toolName is not null)
            {
                var call = (ToolCallContent)result.Message.Content.Single();
                Equal(toolName, call.Name); Check(call.Id.StartsWith(idPrefix!, StringComparison.Ordinal), "Generated tool id differs: " + call.Id);
                Equal(arguments, call.Arguments.ToString());
            }
            else Equal(text, text is null ? (result.Message.Content.IsEmpty ? null : "content") : ((TextContent)result.Message.Content.Single()).Text);
        }
        // Rejected as upstream rejects them: JSON.parse's SyntaxError, the TypeErrors of iterating a non-iterable parts value and of
        // reading `.text` on a null part, and a candidates value without a first candidate (no finish reason).
        foreach (var (chunk, error) in new[] {
            ("{bad}", "Expected property name or '}' in JSON at position 1 (line 1 column 2)"),
            ("""{"candidates":[{"content":{"parts":{}},"finishReason":"STOP"}]}""", "candidate.content.parts is not iterable"),
            ("""{"candidates":[{"content":{"parts":[null]},"finishReason":"STOP"}]}""", "Cannot read properties of null (reading 'text')"),
            ("""{"candidates":{}}""", "Google stream ended without a finish reason") })
        {
            var failed = await Complete(Wire(chunk)); Check(failed.Failure is not null && failed.Message.StopReason == StopReason.Error, "Invalid chunk accepted: " + chunk);
            Equal(error, Error(failed));
        }
        // Owner decision 13: upstream pushes a nameless tool call with name "" and an id "_<ms>_<n>" (name "") or "undefined_<ms>_<n>" (name
        // missing, or a non-object functionCall), and the turn ends as toolUse (captured from @earendil-works/pi-ai@1.1.0).
        foreach (var (chunk, prefix) in new[] {
            ("""{"candidates":[{"content":{"parts":[{"functionCall":{"name":"","args":{}}}]},"finishReason":"STOP"}]}""", "_"),
            ("""{"candidates":[{"content":{"parts":[{"functionCall":{"args":{"a":1}}}]},"finishReason":"STOP"}]}""", "undefined_"),
            ("""{"candidates":[{"content":{"parts":[{"functionCall":"x"}]},"finishReason":"STOP"}]}""", "undefined_") })
        {
            var nameless = await Complete(Wire(chunk));
            Check(nameless.Failure is null && nameless.Message.StopReason == StopReason.ToolUse, "Nameless tool call not pushed: " + chunk);
            var call = nameless.Message.Content.OfType<ToolCallContent>().Single();
            Check(call.Name.Length == 0 && System.Text.RegularExpressions.Regex.IsMatch(call.Id, "^" + prefix + "\\d+_\\d+$"), "Nameless identity: " + call.Id + "/" + call.Name);
        }
        var eof = await Complete(Wire(ToolChunk.Replace(",\"finishReason\":\"STOP\"", "", StringComparison.Ordinal)));
        Check(eof.Failure is not null && Error(eof).Contains("without a finish reason", StringComparison.Ordinal), "Missing finish accepted.");
        var late = await Complete(Wire(ToolChunk) + "data: {bad}\n\n");
        Check(late.Failure is not null && late.Message.Content.OfType<ToolCallContent>().Any(), "Late fault lost partial or granted authority.");
        var lastWins = await Complete(Wire(TextChunk, """{"candidates":[{"finishReason":"MAX_TOKENS"}]}"""));
        Equal(StopReason.Length, lastWins.Message.StopReason);
    }
    private static async Task HooksAndFaults()
    {
        var seen = new List<string>(); var providerEntered = Gate(); var providerRelease = Gate();
        JsonData? wireBody = null; var body = new OwnedBody(Wire(TextChunk));
        using var handler = new Handler(async (request, token) =>
        { seen.Add("send"); wireBody = JsonData.Parse(await request.Content!.ReadAsStringAsync(token)); return Response(body); });
        using var client = new HttpClient(handler);
        var options = Options() with { Hooks = new()
        {
            OnPayload = (parameters, model, _) => {
                Equal(Model, model.Identity); Same(Options().ModelMetadata.Value, model.Raw.Value);
                seen.Add("payload"); var root = JsonNode.Parse(parameters.ToString())!;
                root["config"]!["temperature"] = 0.75; return ValueTask.FromResult<JsonData?>(JsonData.Parse(root.ToJsonString())); },
            OnNativeResponse = (_, _, _) => { seen.Add("native-response"); return ValueTask.CompletedTask; },
            OnProviderStreamEvent = async (chunk, model, _) =>
            { Equal(Model, model.Identity); Same(Options().ModelMetadata.Value, model.Raw.Value);
                Same(JsonData.Parse(TextChunk).Value, chunk.Value); seen.Add("provider-entered"); providerEntered.TrySetResult(); await providerRelease.Task; seen.Add("provider-returned"); },
            OnEventPublished = frame => seen.Add(frame.GetType().Name)
        } };
        Task<ChatResult>? original = null;
        try
        {
            original = new ChatClient(Transport(client, options), capacity: 1).CompleteAsync(Request());
            await providerEntered.Task.WaitAsync(Deadline);
            Check(!original.IsCompleted && !seen.Contains(nameof(TextDelta)), "Held provider hook leaked mapping.");
            providerRelease.TrySetResult(); var result = await original; Check(result.Failure is null, "Hooked request failed.");
            Equal(0.75, wireBody!.Value.GetProperty("generationConfig").GetProperty("temperature").GetDouble());
            Check(seen.IndexOf("payload") < seen.IndexOf("send") && seen.IndexOf(nameof(StreamStarted)) < seen.IndexOf("provider-entered") &&
                seen.IndexOf("provider-returned") < seen.IndexOf(nameof(TextDelta)), "Hook ordering differs.");
        }
        finally { providerRelease.TrySetResult(); if (original is not null) await Join(original); }
        foreach (var site in new[] { "payload", "send", "acquire", "provider", "publish", "read", "http" })
        {
            var faultBody = new OwnedBody(site == "http" ? """{"error":{"message":"do not log"}}""" : Wire(TextChunk)) { FailRead = site == "read" };
            using var faultHandler = new Handler((_, _) => site == "send" ? Task.FromException<HttpResponseMessage>(new IOException(Key)) :
                Task.FromResult(Response(faultBody, site == "http" ? HttpStatusCode.BadRequest : HttpStatusCode.OK)));
            using var faultClient = new HttpClient(faultHandler);
            var faultOptions = Options() with {
                BodyReaderFactory = site == "acquire" ? (_, _) => ValueTask.FromException<Stream?>(new IOException(Key)) : null,
                Hooks = new() {
                    OnPayload = site == "payload" ? (_, _, _) => ValueTask.FromException<JsonData?>(new IOException(Key)) : null,
                    OnProviderStreamEvent = site == "provider" ? (_, _, _) => ValueTask.FromException(new IOException(Key)) : null,
                    OnEventPublished = site == "publish" ? frame => { if (frame is TextDelta) throw new IOException(Key); } : null } };
            var result = await new ChatClient(Transport(faultClient, faultOptions)).CompleteAsync(Request());
            Check(result.Failure is not null && !Error(result).Contains(Key, StringComparison.Ordinal) && !faultHandler.Disposed, "Fault exposed key or leaked borrowed ownership.");
            if (site is not ("payload" or "send")) Check(faultBody.Disposed, "Fault response not disposed.");
        }
    }
    private static async Task CancellationOwnership()
    {
        foreach (var site in new[] { "send", "acquire", "provider", "read", "cleanup" })
        {
            var entered = Gate(); var release = Gate(); var body = new OwnedBody(Wire(ToolChunk));
            if (site == "read") { body.ReadEntered = entered; body.ReleaseRead = release; }
            if (site == "cleanup") { body.HoldCleanup = true; entered = body.CleanupEntered; release = body.ReleaseCleanup; }
            var sends = 0;
            using var handler = new Handler(async (_, _) => { sends++; if (site == "send") { entered.TrySetResult(); await release.Task; } return Response(body); });
            using var client = new HttpClient(handler); using var cancellation = new CancellationTokenSource();
            var options = Options() with {
                BodyReaderFactory = site == "acquire" ? async (_, _) => { entered.TrySetResult(); await release.Task; return body; } : null,
                Hooks = new() { OnProviderStreamEvent = site == "provider" ? async (_, _, _) => { entered.TrySetResult(); await release.Task; } : null } };
            Task<ChatResult>? original = null;
            try
            {
                original = new ChatClient(Transport(client, options), capacity: 1).CompleteAsync(Request(), cancellation.Token);
                await entered.Task.WaitAsync(Deadline); cancellation.Cancel();
                Check(!original.IsCompleted, "Cancellation abandoned admitted " + site + " work.");
                release.TrySetResult(); var result = await original;
                Equal(StopReason.Aborted, result.Message.StopReason); Check(result.Failure is not null && body.Disposed, "Canceled ownership not joined.");
                Equal(1, sends); Check(!handler.Disposed, "Cancellation disposed borrowed client.");
            }
            finally { release.TrySetResult(); cancellation.Cancel(); if (original is not null) await Join(original); }
        }
        using var already = new CancellationTokenSource(); already.Cancel(); var noSend = 0;
        using var earlyHandler = new Handler((_, _) => { noSend++; return Task.FromResult(Response(new OwnedBody(Wire(TextChunk)))); });
        using var earlyClient = new HttpClient(earlyHandler);
        var preaborted = await new ChatClient(Transport(earlyClient)).CompleteAsync(Request(), already.Token);
        Equal(StopReason.Aborted, preaborted.Message.StopReason); Equal(0, noSend);
    }
    private static async Task PrimaryAndCleanup()
    {
        foreach (var scenario in new[] { "provider", "malformed", "eof", "success" })
        foreach (var cleanupFault in new[] { false, true })
        {
            var wire = scenario switch { "provider" => Wire(ToolChunk.Replace("\"STOP\"", "\"SAFETY\"", StringComparison.Ordinal)),
                "malformed" => Wire(ToolChunk) + "data: {bad}\n\n", "eof" => Wire(ToolChunk.Replace(",\"finishReason\":\"STOP\"", "", StringComparison.Ordinal)), _ => Wire(ToolChunk) };
            var body = new OwnedBody(wire) { HoldCleanup = true, FailCleanup = cleanupFault };
            GoogleSettlementObservation? observation = null;
            var options = Options() with { Hooks = new() { OnNativeSettlement = value => observation = value } };
            using var handler = new Handler((_, _) => Task.FromResult(Response(body)));
            using var client = new HttpClient(handler); Task<ChatResult>? original = null;
            try
            {
                original = new ChatClient(Transport(client, options), capacity: 1).CompleteAsync(Request());
                await body.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!original.IsCompleted && observation is null, "Terminal preceded held cleanup.");
                body.ReleaseCleanup.TrySetResult(); var result = await original;
                Equal(cleanupFault, observation!.CleanupFailed);
                Equal(scenario == "success" && !cleanupFault ? StopReason.ToolUse : StopReason.Error, result.Message.StopReason);
                Check(result.Message.Content.OfType<ToolCallContent>().Any(), "Cleanup replaced valid partial tool content.");
                if (scenario == "provider") {
                    Equal(GoogleFailure.ProviderError, observation.PrimaryFailure);
                    Equal("Provider stopped with: SAFETY", Error(result)); Equal(17L, result.Message.Usage.TotalTokens); }
                if (scenario == "malformed") Equal(GoogleFailure.MalformedStream, observation.PrimaryFailure);
                if (scenario == "eof") Equal(GoogleFailure.UnexpectedEof, observation.PrimaryFailure);
                if (scenario == "success" && cleanupFault) Equal(GoogleFailure.CleanupFailed, observation.PrimaryFailure);
            }
            finally { body.ReleaseCleanup.TrySetResult(); if (original is not null) await Join(original); }
        }
    }
    private static async Task Limits()
    {
        foreach (var options in new[] { Options() with { MaximumPayloadBytes = 8 },
            Options() with { MaximumHeaders = 1 }, Options() with { MaximumHeaderCharacters = 4 },
            Options() with { MaxTokens = double.PositiveInfinity } })
        {
            var sends = 0;
            using var handler = new Handler((_, _) => { sends++; return Task.FromResult(Response(new OwnedBody(Wire(TextChunk)))); });
            using var client = new HttpClient(handler);
            try { var result = await new ChatClient(Transport(client, options)).CompleteAsync(Request()); Check(result.Failure is not null, "Budget accepted."); }
            catch (GoogleGenerativeAIException) { }
            Equal(0, sends);
        }
        foreach (var options in new[] { Options() with { MaximumFrameCharacters = 8 },
            Options() with { MaximumEvents = 1 }, Options() with { MaximumStreamCharacters = 8 },
            Options() with { MaximumContentSlots = 1 }, Options() with { MaximumContentCharacters = 1 } })
        {
            var result = await Complete(Wire(TextChunk, ToolChunk), options);
            Check(result.Failure is not null, "Streaming budget accepted.");
        }
        var errorBody = new OwnedBody(new string('x', 32));
        using var errorHandler = new Handler((_, _) => Task.FromResult(Response(errorBody, HttpStatusCode.BadRequest)));
        using var errorClient = new HttpClient(errorHandler);
        var failed = await new ChatClient(Transport(errorClient, Options() with { MaximumErrorBytes = 8 })).CompleteAsync(Request());
        Check(failed.Failure is not null && errorBody.Disposed, "Error byte limit leaked reader.");
    }
    private static async Task ProviderBinding()
    {
        var bodies = new List<JsonData>();
        using var handler = new Handler(async (request, token) => { bodies.Add(JsonData.Parse(await request.Content!.ReadAsStringAsync(token))); return Response(new OwnedBody(Wire(TextChunk))); });
        using var client = new HttpClient(handler); var catalog = Catalog();
        var provider = new GoogleGenerativeAIModelProvider(catalog, client, Key);
        var registry = new ModelTransportRegistry([provider]); Equal(1, registry.Models.Length);
        Check(ReferenceEquals(catalog.Models[0], provider.GetCatalogModel(Model)) && provider.GetCatalogModel(Model).Raw.Value.GetProperty("opaque").TryGetProperty("owned", out _), "Complete raw metadata was narrowed.");
        foreach (var model in new[] { Model with { Id = "unknown" }, Model with { Provider = "other" }, Model with { Api = "google-vertex" } })
        {
            try { _ = provider.Transport.StreamAsync(Request() with { Model = model }); throw new InvalidOperationException("Wrong identity admitted."); }
            catch (ArgumentException) { }
        }
        Equal(0, bodies.Count);
        var result = await new ChatClient(registry).CompleteAsync(Request());
        Check(result.Failure is null && result.Message.Model == Model.Id && !handler.Disposed, "Bound provider failed actual consumer.");
        Equal(1, bodies.Count);
    }
    private static async Task ReleasedCatalog()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "pinned-google-catalog.json"));
        Equal("c0f5a633e5e5a5044432db3e537f4bc674d4de18df09325c6349547fcc13cabe",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        var catalog = FrozenModelCatalog.ReadProviderJson("google", bytes);
        Equal(22, catalog.Models.Length);
        var requests = new List<(string Url, JsonData Body)>();
        using var handler = new Handler(async (request, token) =>
        {
            requests.Add((request.RequestUri!.AbsoluteUri, JsonData.Parse(await request.Content!.ReadAsStringAsync(token))));
            return Response(new OwnedBody(Wire(TextChunk)));
        });
        using var client = new HttpClient(handler);
        var provider = new GoogleGenerativeAIModelProvider(catalog, client, Key); var registry = new ModelTransportRegistry([provider]);
        Equal(22, provider.Models.Count); Equal(22, registry.Models.Length);
        foreach (var row in catalog.Models)
        {
            var model = new ModelDescriptor(row.Id, row.DeclaredApi, row.Provider);
            Check(ReferenceEquals(row, provider.GetCatalogModel(model)) && row.Raw.ToString() == provider.GetCatalogModel(model).Raw.ToString(), "Released raw row replaced.");
            var result = await new ChatClient(registry).CompleteAsync(new(model, Inputs(), 123));
            Check(result.Failure is null && result.Message.Model == row.Id && result.Message.Provider == row.Provider, "Released model routed another identity.");
            Equal(row.BaseUrl.TrimEnd('/') + "/models/" + Uri.EscapeDataString(row.Id) + ":streamGenerateContent?alt=sse", requests[^1].Url);
            Equal("hello π", requests[^1].Body.Value.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        }
        Equal(22, requests.Count); Check(!handler.Disposed, "All-row routing disposed borrowed HTTP client.");
        // Authored fake responses through real adapters; catalog bytes alone establish no runtime/SDK/provider parity.
    }
    private static async Task AgentRoundTrip()
    {
        var first = new OwnedBody(Wire(ToolChunk)) { HoldCleanup = true };
        var bodies = new List<JsonData>(); var requestOwners = new List<RequestContent>(); var contents = new List<BodyContent>();
        var assistantEntered = Gate(); var releaseAssistant = Gate(); var resultEntered = Gate(); var releaseResult = Gate();
        var executor = new Executor();
        using var handler = new Handler(async (request, token) =>
        {
            bodies.Add(JsonData.Parse(await request.Content!.ReadAsStringAsync(token)));
            var requestOwner = new RequestContent(request.Content!); request.Content = requestOwner; requestOwners.Add(requestOwner);
            var content = new BodyContent(bodies.Count == 1 ? first : new OwnedBody(Wire(TextChunk))); contents.Add(content);
            return new(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);
        var registry = new ModelTransportRegistry([new GoogleGenerativeAIModelProvider(Catalog(), client, Key)]);
        await using var agent = new NativeAgent(new(Model, registry, [new("inspect", executor)]), () => 123,
            new Sink(async (observation, _) =>
            {
                if (observation is AssistantMessageEnded assistant && assistant.Message.StopReason == StopReason.ToolUse)
                {
                    Check(first.Disposed && contents[0].Disposed && requestOwners[0].Disposed, "Assistant committed before real HTTP cleanup.");
                    assistantEntered.TrySetResult(); await releaseAssistant.Task;
                }
                if (observation is ToolResultMessageEnded) { resultEntered.TrySetResult(); await releaseResult.Task; }
            }), new(StreamCapacity: 1));
        Task? original = null;
        try
        {
            original = agent.PromptAsync(Inputs());
            await first.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!original.IsCompleted && executor.Executions == 0 && bodies.Count == 1 && !assistantEntered.Task.IsCompleted, "Held cleanup granted tool authority.");
            first.ReleaseCleanup.TrySetResult(); await assistantEntered.Task.WaitAsync(Deadline);
            Check(executor.Executions == 0 && bodies.Count == 1, "Held assistant barrier granted execution.");
            releaseAssistant.TrySetResult(); await resultEntered.Task.WaitAsync(Deadline);
            Equal(1, executor.Executions); Equal(1, bodies.Count); Check(!original.IsCompleted, "Held result barrier granted next request.");
            releaseResult.TrySetResult(); await original; Equal(2, bodies.Count);
            var next = bodies[1].Value.GetProperty("contents");
            var call = next[1].GetProperty("parts")[0];
            Equal("c2ln", call.GetProperty("thoughtSignature").GetString()); Equal("call-1", call.GetProperty("functionCall").GetProperty("id").GetString());
            Same(JsonData.Parse("""{"value":7,"keep":null}""").Value, executor.Last!.Call.Arguments.Value);
            var response = next[2].GetProperty("parts")[0].GetProperty("functionResponse");
            Equal("call-1", response.GetProperty("id").GetString()); Equal("tool-owned", response.GetProperty("response").GetProperty("output").GetString());
            Check(agent.Snapshot.CompletedToolOutcomes.Length == 1 && !agent.Snapshot.IsRunning && requestOwners.All(x => x.Disposed) &&
                contents.All(x => x.Disposed) && !handler.Disposed, "High-level Agent retained ownership.");
        }
        finally
        {
            first.ReleaseCleanup.TrySetResult(); releaseAssistant.TrySetResult(); releaseResult.TrySetResult(); agent.Abort();
            if (original is not null) await Join(original); await agent.WaitForIdleAsync();
        }
    }
    private static async Task AgentFailures()
    {
        foreach (var scenario in new[] { "provider", "malformed", "eof", "length", "cleanup" })
        {
            var wire = scenario switch {
                "provider" => Wire(ToolChunk.Replace("\"STOP\"", "\"SAFETY\"", StringComparison.Ordinal)),
                "malformed" => Wire(ToolChunk) + "data: {bad}\n\n",
                "eof" => Wire(ToolChunk.Replace(",\"finishReason\":\"STOP\"", "", StringComparison.Ordinal)),
                "length" => Wire(ToolChunk.Replace("\"STOP\"", "\"MAX_TOKENS\"", StringComparison.Ordinal)), _ => Wire(ToolChunk) };
            var body = new OwnedBody(wire) { HoldCleanup = true, FailCleanup = scenario == "cleanup" };
            var executor = new Executor(); var sends = 0;
            using var handler = new Handler((_, _) => { sends++; return Task.FromResult(Response(body)); });
            using var client = new HttpClient(handler);
            var registry = new ModelTransportRegistry([new GoogleGenerativeAIModelProvider(Catalog(), client, Key)]);
            await using var agent = new NativeAgent(new(Model, registry, [new("inspect", executor)],
                Hooks: new(FinishTurnDecision: (_, _) => ValueTask.FromResult(AgentLoopFinishAction.End))), () => 123,
                new Sink((_, _) => ValueTask.CompletedTask));
            Task<AgentLoopResult>? original = null;
            try
            {
                original = agent.PromptAsync(Inputs()); await body.CleanupEntered.Task.WaitAsync(Deadline);
                Check(!original.IsCompleted && executor.Executions == 0, "Invalid terminal leaked before cleanup.");
                body.ReleaseCleanup.TrySetResult(); var result = await original;
                Equal(0, executor.Executions); Equal(1, sends); Check(body.Disposed && !agent.Snapshot.IsRunning, "Failed turn not settled.");
                if (scenario == "length") Equal(ToolFailureKind.Truncated, result.Turns.Single().Result.Tools.Outcomes.Single().Result.Failure!.Kind);
                else Check(result.Turns.Single().Result.Chat.Failure is not null, "Failed turn became successful.");
            }
            finally { body.ReleaseCleanup.TrySetResult(); agent.Abort(); if (original is not null) await Join(original); await agent.WaitForIdleAsync(); }
        }
    }
    private static async Task IteratorOwnership()
    {
        var body = new OwnedBody(Wire(TextChunk)) { HoldCleanup = true };
        var requests = 0;
        using var handler = new Handler((_, _) => Task.FromResult(Response(++requests == 1 ? body : new OwnedBody(Wire(TextChunk)))));
        using var client = new HttpClient(handler);
        var invocation = Transport(client).StreamAsync(Request()); var reader = invocation.GetAsyncEnumerator();
        Task? disposal = null;
        try
        {
            Check(await reader.MoveNextAsync() && reader.Current is StreamStarted, "Missing start.");
            try { _ = invocation.GetAsyncEnumerator(); throw new InvalidOperationException("Second reader admitted."); } catch (InvalidOperationException error) when (error.Message.Contains("one reader", StringComparison.Ordinal)) { }
            disposal = reader.DisposeAsync().AsTask(); await body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!disposal.IsCompleted, "Iterator disposal detached cleanup.");
            body.ReleaseCleanup.TrySetResult(); await disposal; Check(body.Disposed && !handler.Disposed, "Early return retained body/client ownership.");
        }
        finally { body.ReleaseCleanup.TrySetResult(); if (disposal is not null) await Join(disposal); else await reader.DisposeAsync(); }
        // Fresh provider calls are admitted after earlier iterator settlement.
        var result = await new ChatClient(Transport(client)).CompleteAsync(Request()); Check(result.Failure is null, "Borrowed client not reusable.");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request, token);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class BodyContent(OwnedBody body) : HttpContent
    {
        internal bool Disposed;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(body);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult<Stream>(body);
        protected override void Dispose(bool disposing) { Disposed = true; body.Dispose(); base.Dispose(disposing); }
    }
    private sealed class RequestContent(HttpContent original) : HttpContent
    {
        internal bool Disposed;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => original.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; original.Dispose(); base.Dispose(disposing); }
    }
    private sealed class OwnedBody(string wire) : Stream
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(wire);
        private int _offset; private bool _readHeld;
        internal int Fragment = 4096;
        internal bool FailRead, FailCleanup, HoldCleanup, Disposed;
        internal TaskCompletionSource? ReadEntered, ReleaseRead;
        internal readonly TaskCompletionSource CleanupEntered = Gate(), ReleaseCleanup = Gate();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _bytes.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!_readHeld && ReadEntered is not null) { _readHeld = true; ReadEntered.TrySetResult(); await ReleaseRead!.Task; }
            if (FailRead) throw new IOException(Key);
            var count = Math.Min(Math.Min(Fragment, buffer.Length), _bytes.Length - _offset);
            _bytes.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count;
        }
        public override async ValueTask DisposeAsync()
        {
            CleanupEntered.TrySetResult(); if (HoldCleanup) await ReleaseCleanup.Task;
            Disposed = true; if (FailCleanup) throw new IOException(Key);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> action) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => action(observation, token); }
    private sealed class Executor : IToolExecutor
    {
        internal int Executions;
        internal ToolInvocation? Last;
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
        { Executions++; Last = invocation; return ValueTask.FromResult(ToolResult.Success("tool-owned")); }
    }
}
