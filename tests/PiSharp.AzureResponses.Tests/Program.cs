using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.AzureResponses;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

// Authored source-text expectations. No upstream execution or genuine source capture credit.
internal static class Program
{
    private static readonly ModelDescriptor Model = new("catalog-model", "azure-openai-responses", "azure-openai-responses");
    private const string Key = "inert-azure-fixture-key";
    private const string DefaultBody = """{"model":"catalog-model","input":[{"role":"user","content":[{"type":"input_text","text":"ask"}]}],"stream":true,"store":false}""";
    private const string Completed = """{"type":"response.completed","response":{"id":"response-azure","status":"completed","output":[],"usage":{"input_tokens":5,"output_tokens":3,"total_tokens":8}}}""";
    private const string TextStart = """{"type":"response.output_item.added","output_index":4,"item":{"type":"message","id":"msg-azure","content":[]}}""";
    private const string TextDelta = """{"type":"response.output_text.delta","output_index":4,"item_id":"msg-azure","delta":"ok"}""";
    private const string TextEnd = """{"type":"response.output_item.done","output_index":4,"item":{"type":"message","id":"msg-azure","content":[{"type":"output_text","text":"ok"}]}}""";
    private const string ToolStart = """{"type":"response.output_item.added","output_index":9,"item":{"type":"function_call","id":"fc-azure","call_id":"call-azure","name":"inspect","arguments":""}}""";
    private const string ToolEnd = """{"type":"response.output_item.done","output_index":9,"item":{"type":"function_call","id":"fc-azure","call_id":"call-azure","name":"inspect","arguments":"{\"value\":7}"}}""";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static int bodyComparisons;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Use [--report <fresh path>].");
        var cases = new (string Id, Func<Task> Run)[]
        {
            ("azure.routes-version-resource-and-deployment-body", Routes),
            ("azure.full-bodies-named-options-sampling-reasoning-session", Bodies),
            ("azure.headers-callback-replacement-owned-input-and-admission", HeadersAndAdmission),
            ("azure.same-identity-transcript-replay-and-openai-isolation", ReplayAndIsolation),
            ("azure.direct-fragmented-http-original-cleanup-barrier", () => Integration("direct")),
            ("azure.chat-http-original-cleanup-barrier", () => Integration("chat")),
            ("azure.agent-http-original-cleanup-barrier", () => Integration("agent")),
            ("azure.tool-dtos-match-shared-mapper-and-canonical-identity", SharedMapper),
            ("azure.accepted-response-hook-before-start-and-hook-failure", ResponseHooks),
            ("azure.http-errors-bodies-status-limits-and-cleanup", Errors),
            ("azure.malformed-eof-sentinel-and-data-admission", Malformed),
            ("azure.cancellation-held-body-cleanup-original-join", Cancellation),
            ("azure.early-return-held-body-cleanup-original-join", EarlyReturn),
            ("azure.primary-failure-and-secondary-cleanup-retained", CleanupFailures),
            ("azure.pre-send-failures-and-pre-cancel-have-zero-http", NoSend)
        };
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            try { await test.Run(); results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" }); }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString() }); }
        }
        var report = new { sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f", status = "AUTHORED NATIVE; SOURCE QUALIFICATION OPEN", failures,
            bodyComparisons, genuineSourceCasesCaptured = 0, results };
        if (args.Length == 2)
        {
            await using var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(file, report, new JsonSerializerOptions { WriteIndented = true });
        }
        else Console.WriteLine(JsonSerializer.Serialize(report));
        return failures == 0 ? 0 : 1;
    }

    private static AzureResponsesOptions Options(bool reasoning = false, object? map = null, object? sampling = null, object? headers = null, object? compat = null) =>
        new(JsonData.FromElement(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["id"] = Model.Id, ["api"] = Model.Api, ["provider"] = Model.Provider,
            ["baseUrl"] = "https://azure.invalid/openai/v1", ["reasoning"] = reasoning,
            ["thinkingLevelMap"] = map, ["samplingParams"] = sampling, ["headers"] = headers, ["compat"] = compat
        })), new(reasoning));
    private static ChatRequest Request() => new(Model, [new("user", JsonData.Parse("""{"role":"user","content":"ask","timestamp":1}"""))], 123);
    private static AzureResponsesRequestFactory Factory(AzureResponsesOptions? options = null) => new(Model, options ?? Options());
    private static AzureResponsesTransport Transport(HttpClient client, AzureResponsesOptions? options = null, string key = Key) => new(client, Factory(options), key);
    private static string Sse(params string[] dtos) => string.Concat(dtos.Select(dto => "data: " + dto + "\n\n"));
    private static string Wire() => Sse(TextStart, TextDelta, TextEnd, Completed, "[DONE]");
    private static ImmutableDictionary<string, string?> Config(params (string Key, string? Value)[] entries) => entries.ToImmutableDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    private static void BodyEqual(string expected, string actual) { bodyComparisons++; Equal(expected, actual); }
    private static string Append(string fields) => DefaultBody[..^1] + "," + fields + "}";

    private static async Task Routes()
    {
        var cases = new (AzureResponsesOptions Options, string Endpoint, string Deployment)[]
        {
            (Options(), "https://azure.invalid/openai/v1/responses?api-version=v1", Model.Id),
            (Options() with { AzureResourceName = "fixture-r" }, "https://fixture-r.openai.azure.com/openai/v1/responses?api-version=v1", Model.Id),
            (Options() with { AzureBaseUrl = "  https://r.openai.azure.com/openai/v1/responses///  ", AzureApiVersion = "2025-04-01-preview" }, "https://r.openai.azure.com/openai/v1/responses?api-version=2025-04-01-preview", Model.Id),
            (Options() with { AzureBaseUrl = "https://r.cognitiveservices.azure.com/openai?drop=1" }, "https://r.cognitiveservices.azure.com/openai/v1/responses?api-version=v1", Model.Id),
            (Options() with { AzureBaseUrl = "https://r.ai.azure.com/" }, "https://r.ai.azure.com/openai/v1/responses?api-version=v1", Model.Id),
            (Options() with { AzureBaseUrl = "https://custom.invalid/base", AzureApiVersion = "version / x", AzureDeploymentName = "deployed" }, "https://custom.invalid/base/responses?api-version=version%20%2F%20x", "deployed"),
            (Options() with { ConfigurationValues = Config(("AZURE_OPENAI_BASE_URL", " https://config.invalid/root/ "), ("AZURE_OPENAI_API_VERSION", "config-v"), ("AZURE_OPENAI_DEPLOYMENT_NAME_MAP", "other=x,catalog-model=first,catalog-model=last=ignored")) }, "https://config.invalid/root/responses?api-version=config-v", "last"),
            (Options() with { ConfigurationValues = Config(("AZURE_OPENAI_DEPLOYMENT_NAME_MAP", "catalog-model=first,catalog-model=  =ignored")) }, "https://azure.invalid/openai/v1/responses?api-version=v1", Model.Id),
            (Options() with { ConfigurationValues = Config(("AZURE_OPENAI_DEPLOYMENT_NAME_MAP", "catalog-model=first,catalog-model=   ")) }, "https://azure.invalid/openai/v1/responses?api-version=v1", "first"),
            (Options() with { AzureBaseUrl = "https://option.invalid/path", AzureApiVersion = "option-v", AzureDeploymentName = "option-deployment", ConfigurationValues = Config(("AZURE_OPENAI_BASE_URL", "https://ignored.invalid/"), ("AZURE_OPENAI_API_VERSION", "ignored"), ("AZURE_OPENAI_DEPLOYMENT_NAME_MAP", "catalog-model=ignored")) }, "https://option.invalid/path/responses?api-version=option-v", "option-deployment"),
            (Options() with { AzureBaseUrl = " ", AzureApiVersion = "", ConfigurationValues = Config(("AZURE_OPENAI_RESOURCE_NAME", "injected-r"), ("AZURE_OPENAI_API_VERSION", "injected-v")) }, "https://injected-r.openai.azure.com/openai/v1/responses?api-version=injected-v", Model.Id)
        };
        foreach (var item in cases)
        {
            var factory = Factory(item.Options); Equal(item.Endpoint, factory.Endpoint.AbsoluteUri); Equal(item.Deployment, factory.DeploymentName);
            using var request = await factory.CreateAsync(Request(), Key);
            Equal(item.Endpoint, request.RequestUri!.AbsoluteUri);
            BodyEqual(DefaultBody.Replace("\"model\":\"catalog-model\"", "\"model\":\"" + item.Deployment + "\"", StringComparison.Ordinal), await request.Content!.ReadAsStringAsync());
            Check(!request.RequestUri.AbsolutePath.Contains("deployments", StringComparison.Ordinal), "Deployment incorrectly entered the Responses route.");
        }
    }

    private static Task Bodies()
    {
        foreach (var (cap, suffix) in new (double? Cap, string? Suffix)[] { (null, null), (0, null), (-1, "16"), (1, "16"), (16, "16"), (20.5, "20.5") })
            BodyEqual(suffix is null ? DefaultBody : Append("\"max_output_tokens\":" + suffix), Factory(Options() with { MaxTokens = cap }).ProjectPayload(Request()).ToString());
        BodyEqual(Append("\"temperature\":0,\"tool_choice\":\"required\""), Factory(Options() with { Temperature = 0, ToolChoice = JsonData.Parse("\"required\"") }).ProjectPayload(Request()).ToString());
        BodyEqual("""{"model":"override","input":[],"stream":true,"store":false,"max_output_tokens":77,"temperature":0.25,"new_option":true}""",
            Factory(Options(sampling: new { max_output_tokens = 44, temperature = 0.5 }) with { MaxTokens = 30, Temperature = 0.1,
                SamplingParams = JsonData.Parse("""{"model":"override","input":[],"max_output_tokens":77,"temperature":0.25,"new_option":true}""") }).ProjectPayload(Request()).ToString());
        foreach (var effort in new[] { "minimal", "low", "medium", "high", "xhigh", "max" })
            BodyEqual(Append("\"reasoning\":{\"effort\":\"" + effort + "\",\"summary\":\"auto\"},\"include\":[\"reasoning.encrypted_content\"]"),
                Factory(Options(true) with { ReasoningEffort = effort }).ProjectPayload(Request()).ToString());
        BodyEqual(Append("\"reasoning\":{\"effort\":\"medium\",\"summary\":\"concise\"},\"include\":[\"reasoning.encrypted_content\"]"),
            Factory(Options(true) with { ReasoningSummary = "concise" }).ProjectPayload(Request()).ToString());
        BodyEqual(Append("\"reasoning\":{\"effort\":\"none\"}"), Factory(Options(true)).ProjectPayload(Request()).ToString());
        BodyEqual(DefaultBody, Factory(Options(true, new { off = (string?)null })).ProjectPayload(Request()).ToString());
        BodyEqual(Append("\"reasoning\":{\"effort\":\"high\",\"summary\":\"auto\"},\"include\":[\"reasoning.encrypted_content\"]"),
            Factory(Options(true, new { low = "high" }) with { ReasoningEffort = "low" }).ProjectPayload(Request()).ToString());
        var session = string.Concat(Enumerable.Repeat("🙂", 65));
        BodyEqual(DefaultBody.Replace("\"store\":false", "\"prompt_cache_key\":\"" + string.Concat(Enumerable.Repeat("🙂", 64)) + "\",\"store\":false", StringComparison.Ordinal),
            Factory(Options() with { SessionId = session }).ProjectPayload(Request()).ToString());
        return Task.CompletedTask;
    }

    private static async Task HeadersAndAdmission()
    {
        var payloadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var payloadRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldOptions = Options() with { Hooks = new(OnPayload: async (payload, _, token) => { payloadEntered.TrySetResult(); await payloadRelease.Task.WaitAsync(token); return payload; }) };
        var creation = Factory(heldOptions).CreateAsync(Request(), Key).AsTask();
        try
        {
            await payloadEntered.Task.WaitAsync(Deadline); Check(!creation.IsCompleted, "Request creation did not await payload callback.");
            payloadRelease.TrySetResult(); using var heldRequest = await creation.WaitAsync(Deadline); BodyEqual(DefaultBody, await heldRequest.Content!.ReadAsStringAsync());
        }
        finally { payloadRelease.TrySetResult(); (await creation).Dispose(); }
        var request = Request(); var history = request.Messages[0].WireBody.ToString(); JsonData? observed = null;
        var options = Options(headers: new Dictionary<string, string?> { ["x-model"] = "model", ["api-key"] = "model-key", ["x-remove"] = "before" }) with
        {
            Headers = JsonData.Parse("""{"X-Model":"request","api-key":"override-key","x-remove":null,"user-agent":"fixture-agent"}"""),
            Hooks = new(OnPayload: (payload, model, ct) => { ct.ThrowIfCancellationRequested(); Equal(Model, model); observed = payload; return ValueTask.FromResult<JsonData?>(JsonData.Parse("""{"model":"replacement","input":[],"stream":true}""")); })
        };
        using var first = await Factory(options).CreateAsync(request, Key); using var second = await Factory().CreateAsync(request, "second-key");
        Equal("override-key", first.Headers.GetValues("api-key").Single()); Equal("request", first.Headers.GetValues("x-model").Single());
        Equal("fixture-agent", first.Headers.GetValues("user-agent").Single()); Check(!first.Headers.Contains("x-remove") && first.Headers.Authorization is null, "Header override/removal differs.");
        Equal("application/json", first.Content!.Headers.ContentType!.MediaType);
        BodyEqual(DefaultBody, observed!.ToString()); BodyEqual("""{"model":"replacement","input":[],"stream":true}""", await first.Content.ReadAsStringAsync());
        first.Dispose(); Check((await second.Content!.ReadAsByteArrayAsync()).Length > 0, "Fresh request ownership aliased.");
        Equal(history, request.Messages[0].WireBody.ToString());
        foreach (var invalid in new AzureResponsesOptions[]
        {
            Options() with { AzureBaseUrl = "https://custom.invalid/?query=1" }, Options() with { AzureBaseUrl = "https://custom.invalid/#fragment" },
            Options() with { AzureBaseUrl = "https://user:pass@custom.invalid/" }, Options() with { AzureBaseUrl = "ftp://custom.invalid/" },
            Options() with { AzureResourceName = "resource/path" }, Options() with { ConfigurationValues = Config(("AZURE_OPENAI_API_KEY", "unsupported")) },
            Options() with { MaxTokens = double.NaN }, Options() with { Temperature = double.PositiveInfinity }, Options() with { SessionId = "\uD800" },
            Options() with { AzureApiVersion = "\uD800" }, Options() with { TimeoutMilliseconds = 0 },
            Options() with { Headers = JsonData.Parse("""{"authorization":"ignored"}""") }, Options() with { Headers = JsonData.Parse("""{"x-bad":"a\nb"}""") },
            Options(compat: new { supportsAdditionalTools = true }), Options(compat: new { supportsToolSearch = true }), Options(compat: new { supportsOpenAIGrammarTools = true })
        }) Throws<AzureResponsesException>(() => Factory(invalid));
        await ThrowsAsync<AzureResponsesException>(async () => { using var _ = await Factory().CreateAsync(request with { Model = Model with { Id = "other" } }, Key); });
        await ThrowsAsync<AzureResponsesException>(async () => { using var _ = await Factory().CreateAsync(request, "bad\r\nkey"); });
        await ThrowsAsync<AzureResponsesException>(async () => { using var _ = await Factory(Options() with { MaximumKeyCharacters = 2 }).CreateAsync(request, Key); });
        Throws<AzureResponsesException>(() => Factory(Options() with { MaximumPayloadBytes = 1 }));
    }

    private static Task ReplayAndIsolation()
    {
        var assistant = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 2,
            [new ToolCallContent("call-prev|fc_prev", "inspect", JsonData.Parse("{\"x\":1}"))], TokenUsage.Zero, StopReason.ToolUse);
        var request = new ChatRequest(Model, [new("assistant", PiWireJson.WriteMessage(assistant)),
            new("toolResult", JsonData.Parse("""{"role":"toolResult","toolCallId":"call-prev|fc_prev","toolName":"inspect","content":[{"type":"text","text":"done"}],"isError":false,"timestamp":3}"""))]);
        BodyEqual("""{"model":"catalog-model","input":[{"type":"function_call","call_id":"call-prev","name":"inspect","arguments":"{\"x\":1}","id":"fc_prev"},{"type":"function_call_output","call_id":"call-prev","output":"done"}],"stream":true,"store":false}""",
            Factory().ProjectPayload(request).ToString());
        var openAiModel = Model with { Api = "openai-responses", Provider = "openai" };
        var openAiFactory = new ResponsesKeyAuthRequestFactory(new("https://openai.invalid/v1/responses"), openAiModel, new(false));
        using var native = openAiFactory.Create(Request() with { Model = openAiModel }, Key);
        Equal("Bearer", native.Headers.Authorization!.Scheme); Check(!native.Headers.Contains("api-key"), "Azure changed OpenAI auth.");
        return Task.CompletedTask;
    }

    private static async Task Integration(string consumer)
    {
        var body = new Body(Wire(), gated: true, fragment: 1); using var handler = new Handler(body); using var client = new HttpClient(handler);
        var transport = Transport(client, Options() with { AzureDeploymentName = "wire-deployment" });
        using var cancellation = new CancellationTokenSource(); NativeAgent? agent = null; AssistantMessage? final = null; var frames = new List<StreamEvent>(); var commits = 0;
        async Task Direct() { await foreach (var frame in transport.StreamAsync(Request(), cancellation.Token)) { frames.Add(frame); if (frame is StreamTerminalEvent terminal) final = terminal.Message; } }
        async Task Chat() { var result = await new ChatClient(transport, capacity: 1).CompleteAsync(Request(), cancellation.Token); Check(result.Failure is null, "Chat failed."); final = result.Message; }
        if (consumer == "agent") agent = new(new(Model, transport, []), () => 123, new Sink(observation => { if (observation is AssistantMessageEnded ended) { final = ended.Message; commits++; } }));
        var operation = agent is not null ? agent.PromptAsync(Request().Messages[0], cancellation.Token) : consumer == "direct" ? Direct() : Chat();
        var failures = new List<Exception>();
        try
        {
            await body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!operation.IsCompleted && final is null && commits == 0 && frames.All(frame => frame is not StreamTerminalEvent), "Settlement escaped held cleanup.");
            Check(!handler.ContentDisposed, "Response disposed before body cleanup joined.");
            BodyEqual(DefaultBody.Replace("catalog-model", "wire-deployment", StringComparison.Ordinal), handler.Bodies.Single());
            body.ReleaseCleanup.TrySetResult(); await operation.WaitAsync(Deadline);
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            body.ReleaseCleanup.TrySetResult(); if (!operation.IsCompleted) cancellation.Cancel();
            try { await operation; } catch (Exception error) { failures.Add(error); }
            try { if (agent is not null) await agent.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        }
        Check(body.Disposed && handler.ContentDisposed && body.AsyncDisposeCalls == 1 && !handler.Disposed, "Original response/body ownership differs.");
        await RequestDisposed(handler.Requests.Single());
        if (failures.Count > 0) throw new AggregateException("Integration assertions failed after original owners joined.", failures);
        Check(final is not null, "No assistant settlement."); Equal(Model.Id, final!.Model); Equal(Model.Api, final.Api); Equal(Model.Provider, final.Provider);
        Equal(StopReason.Stop, final.StopReason); Equal("ok", final.Content.OfType<TextContent>().Single().Text); Equal(5L, final.Usage.Input); Equal(3L, final.Usage.Output); Equal(8L, final.Usage.TotalTokens);
        Equal(1, handler.SendCalls); if (consumer == "agent") Equal(1, commits);
    }

    private static async Task SharedMapper()
    {
        var dtos = new[]
        {
            """{"type":"response.output_item.added","output_index":2,"item":{"type":"reasoning","id":"rs-azure","summary":[]}}""",
            """{"type":"response.reasoning_summary_text.delta","output_index":2,"item_id":"rs-azure","delta":"thought"}""",
            """{"type":"response.output_item.done","output_index":2,"item":{"type":"reasoning","id":"rs-azure","summary":[{"type":"summary_text","text":"thought"}],"encrypted_content":"inert-encrypted-signature"}}""",
            TextStart, TextDelta, TextEnd, ToolStart, ToolEnd, Completed
        };
        var body = new Body(Sse([.. dtos, "[DONE]", "{ignored"])); using var handler = new Handler(body); using var client = new HttpClient(handler);
        var actual = await Drain(Transport(client).StreamAsync(Request()));
        var expected = await Drain(new ResponsesTextToolTransport((_, ct) => Dtos(dtos, ct)).StreamAsync(Request()));
        Equal(string.Join("\n", expected.Select(PiWireJson.WriteEvent).Select(value => value.ToString())), string.Join("\n", actual.Select(PiWireJson.WriteEvent).Select(value => value.ToString())));
        var final = actual.OfType<StreamDone>().Single(); Equal(StopReason.ToolUse, final.Reason); Equal(Model.Id, final.Message.Model);
        Equal("thought", final.Message.Content.OfType<ThinkingContent>().Single().Thinking);
        var call = final.Message.Content.OfType<ToolCallContent>().Single(); Equal("call-azure|fc-azure", call.Id); Equal("{\"value\":7}", call.Arguments.ToString());
        Check(body.Disposed && handler.ContentDisposed, "Shared mapper comparison skipped cleanup.");
    }
    private static async IAsyncEnumerable<JsonData> Dtos(string[] values, [EnumeratorCancellation] CancellationToken token)
    { await Task.CompletedTask; foreach (var value in values) { token.ThrowIfCancellationRequested(); yield return JsonData.Parse(value); } }

    private static async Task ResponseHooks()
    {
        await AwaitDtoHook();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new Body(Wire()); using var handler = new Handler(body); using var client = new HttpClient(handler); var events = new List<StreamEvent>(); var dtos = 0;
        var options = Options() with { Hooks = new(OnResponse: async (info, model, token) => { Equal(200, info.Status); Equal(Model, model); entered.TrySetResult(); await release.Task.WaitAsync(token); },
            OnProviderStreamEvent: (_, model, token) => { token.ThrowIfCancellationRequested(); Equal(Model, model); dtos++; return ValueTask.CompletedTask; }) };
        var operation = DrainInto(Transport(client, options).StreamAsync(Request()), events);
        try { await entered.Task.WaitAsync(Deadline); Equal(0, events.Count); Equal(0, body.ReadCalls); release.TrySetResult(); await operation.WaitAsync(Deadline); }
        finally { release.TrySetResult(); await operation; }
        Equal(4, dtos); Check(events.Last() is StreamDone && body.Disposed, "Hooked stream failed.");
        foreach (var rejectResponse in new[] { true, false })
        {
            var failingBody = new Body(Wire()); using var failingHandler = new Handler(failingBody); using var failingClient = new HttpClient(failingHandler);
            var hooks = rejectResponse ? new AzureResponsesHooks(OnResponse: (_, _, _) => throw new InvalidOperationException("fixture private callback"))
                : new AzureResponsesHooks(OnProviderStreamEvent: (_, _, _) => throw new InvalidOperationException("fixture private callback"));
            var failed = await Drain(Transport(failingClient, Options() with { Hooks = hooks }).StreamAsync(Request()));
            Check(failed.Last() is StreamError && (rejectResponse ? failed.All(frame => frame is not StreamStarted) : failed[0] is StreamStarted), "Hook/start order differs.");
            Check(!Error(failed).Contains("private", StringComparison.Ordinal) && failingHandler.ContentDisposed, "Callback diagnostics/cleanup differ.");
            await RequestDisposed(failingHandler.Requests.Single());
        }
    }
    private static async Task AwaitDtoHook()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = new Body(Wire()); using var handler = new Handler(body); using var client = new HttpClient(handler); var frames = new List<StreamEvent>();
        var first = true;
        var options = Options() with { Hooks = new(OnProviderStreamEvent: async (_, _, token) => { if (first) { first = false; entered.TrySetResult(); await release.Task.WaitAsync(token); } }) };
        var operation = DrainInto(Transport(client, options).StreamAsync(Request()), frames);
        try { await entered.Task.WaitAsync(Deadline); Check(frames.Count == 1 && frames[0] is StreamStarted, "DTO processing preceded awaited callback."); release.TrySetResult(); await operation.WaitAsync(Deadline); }
        finally { release.TrySetResult(); await operation; }
        Check(frames.Last() is StreamDone && body.Disposed, "DTO callback owner did not settle.");
    }

    private static async Task Errors()
    {
        foreach (var (wire, expected) in new[]
        {
            ("{\"error\":{\"message\":\"denied\",\"code\":\"fixture\"}}", "Azure OpenAI API error (403): {\"message\":\"denied\",\"code\":\"fixture\"}"),
            ("{\"message\":\"denied\",\"code\":\"fixture\"}", "Azure OpenAI API error (403): {\"message\":\"denied\",\"code\":\"fixture\"}"),
            ("proxy denied", "Azure OpenAI API error (403): 403 proxy denied"),
            ("", "Azure OpenAI API error (403): 403 status code (no body)"),
            ("{\"error\":{\"code\":\"fixture\"}}", "Azure OpenAI API error (403): 403 {\"code\":\"fixture\"}")
        })
        {
            var body = new Body(wire); using var handler = new Handler(body, HttpStatusCode.Forbidden); using var client = new HttpClient(handler);
            var events = await Drain(Transport(client).StreamAsync(Request())); Equal(1, events.Count); Equal(expected, Error(events));
            Check(events[0] is StreamError && handler.ContentDisposed && body.Disposed && handler.SendCalls == 1, "HTTP rejection ownership/retry differs."); await RequestDisposed(handler.Requests.Single());
        }
        var longText = new string('x', 4100); var longBody = new Body(JsonSerializer.Serialize(new { error = new { message = longText } }));
        using (var handler = new Handler(longBody, HttpStatusCode.BadRequest)) using (var client = new HttpClient(handler))
        {
            var events = await Drain(Transport(client).StreamAsync(Request())); Check(Error(events).Contains("... [truncated ", StringComparison.Ordinal), "Normalized error body truncation omitted.");
        }
        var limited = new Body("12345"); using var limitedHandler = new Handler(limited, HttpStatusCode.BadRequest); using var limitedClient = new HttpClient(limitedHandler);
        var failure = await Drain(Transport(limitedClient, Options() with { MaximumErrorBodyBytes = 4 }).StreamAsync(Request()));
        Equal("Azure Responses input exceeds configured limits.", Error(failure)); Check(limited.Disposed, "Error-body cap skipped cleanup.");
    }

    private static async Task Malformed()
    {
        foreach (var wire in new[] { Sse("{invalid"), Sse("[]"), Sse("{\"type\":\"response.created\",\"type\":\"duplicate\"}"), Sse(TextStart, TextDelta), Sse(TextStart, TextDelta, "[DONE]", Completed), Sse(" [DONE] ") })
        {
            var body = new Body(wire); using var handler = new Handler(body); using var client = new HttpClient(handler);
            var events = await Drain(Transport(client).StreamAsync(Request())); Check(events.Last() is StreamError && events.All(frame => frame is not StreamDone), "Malformed/EOF succeeded.");
            if (wire.Contains(TextDelta, StringComparison.Ordinal)) Equal("ok", events.OfType<StreamError>().Single().Message.Content.OfType<TextContent>().Single().Text);
            Check(body.Disposed && handler.ContentDisposed, "Malformed body retained owner.");
        }
        foreach (var options in new ResponsesHttpSseOptions[] { new(MaximumDataEvents: 1), new(MaximumDataCharacters: 8), new(MaximumTotalDataCharacters: 8) })
        {
            var body = new Body(Wire()); using var handler = new Handler(body); using var client = new HttpClient(handler);
            Equal("Azure Responses input exceeds configured limits.", Error(await Drain(Transport(client, Options() with { HttpOptions = options }).StreamAsync(Request()))));
        }
        var valid = new Body(Sse(Completed)); using var validHandler = new Handler(valid); using var validClient = new HttpClient(validHandler);
        Check((await Drain(Transport(validClient).StreamAsync(Request()))).Last() is StreamDone, "Completed EOF rejected.");
    }

    private static async Task Cancellation()
    {
        await CancellationDuringCleanup();
        var body = new Body("", gated: true, blockRead: true); using var handler = new Handler(body); using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(); var frames = new List<StreamEvent>();
        var operation = DrainInto(Transport(client).StreamAsync(Request(), cancellation.Token), frames);
        try
        {
            await body.ReadEntered.Task.WaitAsync(Deadline); cancellation.Cancel(); await body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!operation.IsCompleted && frames.All(frame => frame is not StreamTerminalEvent) && !handler.ContentDisposed, "Cancelled stream escaped cleanup.");
            body.ReleaseCleanup.TrySetResult(); await operation.WaitAsync(Deadline);
        }
        finally { cancellation.Cancel(); body.ReleaseCleanup.TrySetResult(); await operation; }
        var final = frames.OfType<StreamError>().Single(); Equal(StopReason.Aborted, final.Reason); Equal("Request was aborted", Error(frames));
        Check(body.Disposed && body.AsyncDisposeCalls == 1 && handler.ContentDisposed, "Cancellation original owner not joined."); await RequestDisposed(handler.Requests.Single());
    }
    private static async Task CancellationDuringCleanup()
    {
        var body = new Body(Wire(), gated: true); using var handler = new Handler(body); using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(); var frames = new List<StreamEvent>();
        var operation = DrainInto(Transport(client).StreamAsync(Request(), cancellation.Token), frames);
        try
        {
            await body.CleanupEntered.Task.WaitAsync(Deadline); cancellation.Cancel();
            Check(!operation.IsCompleted && frames.All(frame => frame is not StreamTerminalEvent), "Completed DTO escaped cancel/cleanup barrier.");
            body.ReleaseCleanup.TrySetResult(); await operation.WaitAsync(Deadline);
        }
        finally { body.ReleaseCleanup.TrySetResult(); cancellation.Cancel(); await operation; }
        var final = frames.OfType<StreamError>().Single(); Equal(StopReason.Aborted, final.Reason); Equal(8L, final.Message.Usage.TotalTokens);
        await RequestDisposed(handler.Requests.Single());
    }

    private static async Task EarlyReturn()
    {
        var body = new Body(Wire(), gated: true); using var handler = new Handler(body); using var client = new HttpClient(handler);
        var iterator = Transport(client).StreamAsync(Request()).GetAsyncEnumerator(); Task? disposal = null;
        try
        {
            Check(await iterator.MoveNextAsync() && iterator.Current is StreamStarted, "Missing accepted start.");
            Check(await iterator.MoveNextAsync() && iterator.Current is TextStarted, "No body read before early return.");
            disposal = iterator.DisposeAsync().AsTask(); await body.CleanupEntered.Task.WaitAsync(Deadline);
            Check(!disposal.IsCompleted && !handler.ContentDisposed, "Early return escaped cleanup."); body.ReleaseCleanup.TrySetResult(); await disposal.WaitAsync(Deadline);
        }
        finally { body.ReleaseCleanup.TrySetResult(); if (disposal is not null) await disposal; else await iterator.DisposeAsync(); }
        Check(body.Disposed && body.AsyncDisposeCalls == 1 && handler.ContentDisposed, "Early return original owner not joined."); await RequestDisposed(handler.Requests.Single());
    }

    private static async Task CleanupFailures()
    {
        var semantic = new Body(Sse(TextStart, TextDelta, TextEnd,
            """{"type":"response.incomplete","response":{"status":"incomplete","output":[],"incomplete_details":{"reason":"content_filter"}}}"""), failCleanup: true);
        using (var semanticHandler = new Handler(semantic)) using (var semanticClient = new HttpClient(semanticHandler))
        {
            var frames = await Drain(Transport(semanticClient).StreamAsync(Request()));
            Equal("Response incomplete: content_filter", Error(frames));
            Check(frames.OfType<StreamError>().Single().Message.ExtraProperties!.TryGet("azureCleanupFailed", out _), "Semantic primary lost secondary cleanup.");
            await RequestDisposed(semanticHandler.Requests.Single());
        }
        foreach (var malformed in new[] { false, true })
        {
            var body = new Body(malformed ? Sse(TextStart, TextDelta, "{bad") : Wire(), failCleanup: true); using var handler = new Handler(body); using var client = new HttpClient(handler);
            var frames = await Drain(Transport(client).StreamAsync(Request())); var final = frames.OfType<StreamError>().Single();
            Equal(malformed ? "Invalid or incomplete Azure Responses stream." : "Azure Responses cleanup failed.", Error(frames));
            Check(final.Message.ExtraProperties!.TryGet("azureCleanupFailed", out var cleanup) && cleanup!.Value.GetBoolean(), "Secondary cleanup was lost.");
            Equal("ok", final.Message.Content.OfType<TextContent>().Single().Text); Check(handler.ContentDisposed && body.AsyncDisposeCalls == 1, "Failed cleanup retained response."); await RequestDisposed(handler.Requests.Single());
        }
    }

    private static async Task NoSend()
    {
        var body = new Body(Wire()); using var handler = new Handler(body); using var client = new HttpClient(handler);
        foreach (var options in new[] { Options() with { Hooks = new(OnPayload: (_, _, _) => throw new InvalidOperationException("private payload hook")) },
            Options() with { Hooks = new(OnPayload: (_, _, _) => ValueTask.FromResult<JsonData?>(JsonData.Parse("[]"))) } })
        {
            var frames = await Drain(Transport(client, options).StreamAsync(Request())); Check(frames.Single() is StreamError, "Pre-send failure emitted start.");
        }
        Check((await Drain(Transport(client, key: "").StreamAsync(Request()))).Single() is StreamError, "Missing key admitted.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Check((await Drain(Transport(client).StreamAsync(Request(), cancellation.Token))).Single() is StreamError { Reason: StopReason.Aborted }, "Pre-cancellation outcome differs.");
        Equal(0, handler.SendCalls);
        Throws<ArgumentException>(() => Transport(client, Options() with { HttpOptions = new(Framing: new(RejectInvalidUtf8: false)) }));
    }

    private static async Task<List<StreamEvent>> Drain(IAsyncEnumerable<StreamEvent> source) { var values = new List<StreamEvent>(); await DrainInto(source, values); return values; }
    private static async Task DrainInto(IAsyncEnumerable<StreamEvent> source, List<StreamEvent> values) { await foreach (var value in source) values.Add(value); }
    private static string Error(List<StreamEvent> values)
    { var final = values.OfType<StreamError>().Single(); Check(final.Message.ExtraProperties!.TryGet("errorMessage", out var text), "Missing error message."); return text!.Value.GetString()!; }
    private static async Task RequestDisposed(HttpRequestMessage request)
    { try { await request.Content!.ReadAsByteArrayAsync(); } catch (ObjectDisposedException) { return; } throw new InvalidOperationException("Original request content was not disposed."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class Handler(Body body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal readonly List<HttpRequestMessage> Requests = []; internal readonly List<string> Bodies = []; internal int SendCalls; internal bool ContentDisposed, Disposed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            SendCalls++; Requests.Add(request); Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            Equal(Key, request.Headers.GetValues("api-key").Single()); Check(request.Headers.Authorization is null, "Unexpected Azure bearer header.");
            return new(status) { Content = new Content(body, () => ContentDisposed = true) };
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }
    private sealed class Content(Body body, Action disposed) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult<Stream>(body);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("Unexpected response buffering.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) disposed(); base.Dispose(disposing); }
    }
    private sealed class Body(string wire, bool gated = false, int fragment = int.MaxValue, bool blockRead = false, bool failCleanup = false)
        : MemoryStream(Encoding.UTF8.GetBytes(wire), writable: false)
    {
        internal readonly TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), CleanupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed; internal int ReadCalls, AsyncDisposeCalls; private Task? cleanup;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { ReadCalls++; ReadEntered.TrySetResult(); if (blockRead) await Task.Delay(Timeout.InfiniteTimeSpan, token); return await base.ReadAsync(buffer[..Math.Min(buffer.Length, fragment)], token); }
        public override ValueTask DisposeAsync() => new(cleanup ??= DisposeBodyCoreAsync());
        private async Task DisposeBodyCoreAsync() { AsyncDisposeCalls++; CleanupEntered.TrySetResult(); if (gated) await ReleaseCleanup.Task; Disposed = true; base.Dispose(true); if (failCleanup) throw new IOException("private cleanup fixture"); }
    }
    private sealed class Sink(Action<AgentEvent> observe) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) { observe(observation); return ValueTask.CompletedTask; } }
}
