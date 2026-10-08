using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

internal static class ResponsesKeyAuthRequestFactoryTests
{
    private static readonly Uri Endpoint = new("https://synthetic.invalid/exact/v1/responses?mode=offline");
    private static readonly ModelDescriptor Model = new("synthetic-key-model", "openai-responses", "fixture-provider");
    private const string Key = "fake-ephemeral-key_123";
    private const string Text = "Final \u6587\U0001F642\u2028";

    public static void Register(List<(string Name, Func<Task> Run)> tests)
    {
        tests.Add(("responses-key.payload-headers-fresh-ownership-and-model-identity", PayloadAndOwnership));
        tests.Add(("responses-key.max-output-source-floor-and-explicit-capability", OutputTokenProfile));
        tests.Add(("responses-key.genuine-input-suffix-overlap-and-history-immutability", GenuineInputOverlap));
        tests.Add(("responses-key.unsupported-config-transcript-and-sanitized-key-admission", Admission));
        tests.Add(("responses-key.byte-depth-identity-key-boundaries-and-cancellation", LimitsAndCancellation));
        tests.Add(("responses-key.offline-text-roundtrip-awaits-owned-cleanup-and-repeats", OfflineRoundtrip));
        tests.Add(("responses-key.invalid-admission-and-cancel-prevent-http-acquisition", NoAcquisition));
        tests.Add(("responses-key.complete-parsed-payload-matches-three-genuine-sdk-bodies", GenuineSdkBodies));
        tests.Add(("responses-key.session-codepoint-clamp-finite-temperature-and-budgets", SessionAndTemperature));
    }

    public static async Task PayloadAndOwnership()
    {
        var factory = Factory();
        var request = Request();
        var history = request.Messages[0].WireBody.ToString();
        using var first = factory.Create(request, Key);
        using var second = factory.Create(request, "other-compatible-provider-key");
        Equal(HttpMethod.Post, first.Method);
        Equal(Endpoint, first.RequestUri);
        Equal("Bearer", first.Headers.Authorization!.Scheme);
        Equal(Key, first.Headers.Authorization.Parameter);
        Equal("other-compatible-provider-key", second.Headers.Authorization!.Parameter);
        Equal("application/json", first.Content!.Headers.ContentType!.MediaType);
        Equal("utf-8", first.Content.Headers.ContentType.CharSet);
        Check(!ReferenceEquals(first, second) && !ReferenceEquals(first.Content, second.Content), "Factory reused owned request/content.");
        var raw = await first.Content.ReadAsStringAsync();
        Equal(raw, await second.Content!.ReadAsStringAsync());
        using var payload = JsonDocument.Parse(raw);
        var root = payload.RootElement;
        Equal(4, root.EnumerateObject().Count());
        Equal(Model.Id, root.GetProperty("model").GetString());
        Check(root.GetProperty("stream").GetBoolean() && !root.GetProperty("store").GetBoolean(), "Source stream/store profile changed.");
        Check(Same(new ResponsesTranscriptProjector(new(false)).Project(request).Value, root.GetProperty("input")), "Projected input changed in payload envelope.");
        Check(!raw.Contains(Key, StringComparison.Ordinal) && !raw.Contains("other-compatible", StringComparison.Ordinal), "Credential entered JSON payload.");
        Equal(history, request.Messages[0].WireBody.ToString());
        first.Dispose();
        await Disposed(first.Content);
        Check((await second.Content.ReadAsByteArrayAsync()).Length != 0, "Disposing one request affected another.");
        foreach (var changed in new[] { Model with { Id = "other" }, Model with { Api = "other" }, Model with { Provider = "other" } })
            Equal(ResponsesKeyAuthRequestFailure.InvalidRequest, Failure(() => factory.Create(request with { Model = changed }, Key)).Failure);
        await ActiveToolDeclarations();
    }

    public static async Task OutputTokenProfile()
    {
        foreach (var (requested, expected) in new[] { (int.MinValue, 16), (-1, 16), (1, 16), (15, 16), (16, 16), (17, 17), (100, 100) })
        {
            using var request = Factory(new(SupportsMaxOutputTokens: true, MaxOutputTokens: requested, MaximumOutputTokens: 100)).Create(Request(), Key);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), payload.RootElement.GetProperty("max_output_tokens").GetRawText());
            Equal(5, payload.RootElement.EnumerateObject().Count());
        }
        foreach (var value in new int?[] { null, 0 })
        {
            using var request = Factory(new(SupportsMaxOutputTokens: true, MaxOutputTokens: value)).Create(Request(), Key);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(!payload.RootElement.TryGetProperty("max_output_tokens", out _), "Null/zero max output must be omitted.");
        }
        using (var largest = Factory(new(SupportsMaxOutputTokens: true, MaxOutputTokens: int.MaxValue, MaximumOutputTokens: int.MaxValue)).Create(Request(), Key))
        {
            using var payload = JsonDocument.Parse(await largest.Content!.ReadAsStringAsync());
            Equal("2147483647", payload.RootElement.GetProperty("max_output_tokens").GetRawText());
        }
        Equal(ResponsesKeyAuthRequestFailure.UnsupportedOptions, Failure(() => Factory(new(MaxOutputTokens: 16))).Failure);
        foreach (var value in new[] { 0, -1 })
            Equal(ResponsesKeyAuthRequestFailure.UnsupportedOptions, Failure(() => Factory(new(MaxOutputTokens: value))).Failure);
        Equal(ResponsesKeyAuthRequestFailure.ResourceLimit, Failure(() => Factory(new(SupportsMaxOutputTokens: true, MaxOutputTokens: 101, MaximumOutputTokens: 100))).Failure);
        Equal(ResponsesKeyAuthRequestFailure.InvalidConfiguration, Failure(() => Factory(new(MaximumOutputTokens: 15))).Failure);
    }

    public static async Task GenuineSdkBodies()
    {
        var repo = FindRepo();
        var inputBytes = await File.ReadAllBytesAsync(Path.Combine(repo, "fixtures/pi-v0.99.1/responses-sdk/core.input.json"));
        var goldenBytes = await File.ReadAllBytesAsync(Path.Combine(repo, "fixtures/pi-v0.99.1/responses-sdk/core.expected.json"));
        Equal("3d4431739bcce4b2218d13a71dba4bf273fe47aab8937f36d3177eb984bf9633", Convert.ToHexStringLower(SHA256.HashData(inputBytes)));
        Equal("ef37cbb6ee02d38642597339986b4d73de424ab4fcfd81d9eee4b0a8109bc098", Convert.ToHexStringLower(SHA256.HashData(goldenBytes)));
        using var input = JsonDocument.Parse(inputBytes);
        using var golden = JsonDocument.Parse(goldenBytes);
        var source = input.RootElement;
        var declaration = source.GetProperty("model");
        var model = new ModelDescriptor(declaration.GetProperty("id").GetString()!, declaration.GetProperty("api").GetString()!, declaration.GetProperty("provider").GetString()!);
        var messages = source.GetProperty("context").GetProperty("messages").EnumerateArray()
            .Select(value => new TranscriptEntry(value.GetProperty("role").GetString()!, JsonData.FromElement(value))).ToImmutableArray();
        var originals = messages.Select(message => message.WireBody.ToString()).ToArray();
        var common = source.GetProperty("commonOptions");
        var index = 0;
        foreach (var item in source.GetProperty("cases").EnumerateArray())
        {
            var oracle = golden.RootElement.GetProperty("observations").GetProperty("cases")[index++];
            Equal(item.GetProperty("caseId").GetString(), oracle.GetProperty("caseId").GetString());
            Equal(1, oracle.GetProperty("fetchRequests").GetArrayLength());
            var fetch = oracle.GetProperty("fetchRequests")[0];
            using var capturedRaw = JsonDocument.Parse(fetch.GetProperty("body").GetString()!);
            Check(Same(capturedRaw.RootElement, fetch.GetProperty("bodyJson")), "Captured raw SDK body and captured parsed body disagree.");
            var options = new ResponsesKeyAuthRequestOptions(SupportsMaxOutputTokens: true,
                MaxOutputTokens: item.GetProperty("options").GetProperty("maxTokens").GetInt32(),
                SessionId: common.GetProperty("sessionId").GetString(), Temperature: common.GetProperty("temperature").GetDouble());
            using var request = new ResponsesKeyAuthRequestFactory(new Uri(fetch.GetProperty("url").GetString()!), model,
                new(declaration.GetProperty("reasoning").GetBoolean()), options).Create(new(model, messages), common.GetProperty("apiKey").GetString()!);
            using var actual = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(Same(fetch.GetProperty("bodyJson"), actual.RootElement), "Complete parsed native request body differs from genuine SDK observation.");
            Equal(1, actual.RootElement.GetProperty("input").GetArrayLength());
            Equal("user", actual.RootElement.GetProperty("input")[0].GetProperty("role").GetString());
            Check(!actual.RootElement.TryGetProperty("prompt_cache_retention", out _) && !actual.RootElement.TryGetProperty("prompt_cache_options", out _), "Source undefined cache properties became native payload fields.");
            Equal("0", actual.RootElement.GetProperty("temperature").GetRawText());
            for (var entry = 0; entry < messages.Length; entry++) Equal(originals[entry], messages[entry].WireBody.ToString());
        }
        Equal(3, index);
        // Strict comparison may ignore only object-key order, never these request mutations.
        using var zero = JsonDocument.Parse("""{"temperature":0,"input":["a","b"]}""");
        foreach (var mutation in new[] { """{"temperature":0.0,"input":["a","b"]}""", """{"temperature":null,"input":["a","b"]}""",
            """{"input":["a","b"]}""", """{"temperature":0,"input":["b","a"]}""" })
        {
            using var changed = JsonDocument.Parse(mutation);
            Check(!Same(zero.RootElement, changed.RootElement), "Request comparison normalized a meaningful field/token/order change.");
        }
    }

    public static async Task SessionAndTemperature()
    {
        var supplementary = "\U0001F642";
        foreach (var (session, expected) in new[]
        {
            ("", ""), (new string('a', 64), new string('a', 64)), (new string('a', 65), new string('a', 64)),
            (new string('a', 63) + supplementary + "tail", new string('a', 63) + supplementary),
            (string.Concat(Enumerable.Repeat(supplementary, 65)), string.Concat(Enumerable.Repeat(supplementary, 64))),
            ("\u2028\uE000\\\r\n", "\u2028\uE000\\\r\n")
        })
        {
            using var request = Factory(new(SessionId: session)).Create(Request(), Key);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Equal(expected, payload.RootElement.GetProperty("prompt_cache_key").GetString());
        }
        foreach (var session in new[] { "\uD800", "\uDC00", new string('a', 64) + "\uD800" })
            Equal(ResponsesKeyAuthRequestFailure.InvalidConfiguration, Failure(() => Factory(new(SessionId: session))).Failure);
        Equal(ResponsesKeyAuthRequestFailure.ResourceLimit, Failure(() => Factory(new(SessionId: "12345", MaximumSessionIdCharacters: 4))).Failure);
        foreach (var (temperature, expected) in new[] { (0d, "0"), (BitConverter.Int64BitsToDouble(long.MinValue), "0"), (0.5, "0.5"), (-2d, "-2"), (2d, "2") })
        {
            using var request = Factory(new(Temperature: temperature)).Create(Request(), Key);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Equal(expected, payload.RootElement.GetProperty("temperature").GetRawText());
        }
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Equal(ResponsesKeyAuthRequestFailure.InvalidConfiguration, Failure(() => Factory(new(Temperature: value))).Failure);
        Equal(ResponsesKeyAuthRequestFailure.ResourceLimit, Failure(() => Factory(new(Temperature: 2.01))).Failure);
        Equal(ResponsesKeyAuthRequestFailure.ResourceLimit, Failure(() => Factory(new(Temperature: -2.01))).Failure);
        foreach (var options in new ResponsesKeyAuthRequestOptions[]
        {
            new(MaximumSessionIdCharacters: 0), new(MaximumTemperatureMagnitude: 0),
            new(MaximumTemperatureMagnitude: double.NaN), new(MaximumTemperatureMagnitude: double.PositiveInfinity)
        }) Equal(ResponsesKeyAuthRequestFailure.InvalidConfiguration, Failure(() => Factory(options)).Failure);
        var configured = new ResponsesKeyAuthRequestOptions(SessionId: supplementary, MaximumSessionIdCharacters: 2, Temperature: 0);
        using var baseline = Factory(configured).Create(Request(), Key);
        var bytes = (await baseline.Content!.ReadAsByteArrayAsync()).Length;
        using var exact = Factory(configured with { MaximumPayloadBytes = bytes }).Create(Request(), Key);
        Equal(bytes, (await exact.Content!.ReadAsByteArrayAsync()).Length);
        Equal(ResponsesKeyAuthRequestFailure.ResourceLimit, Failure(() => Factory(configured with { MaximumPayloadBytes = bytes - 1 }).Create(Request(), Key)).Failure);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Throws<OperationCanceledException>(() => Factory(configured).Create(Request(), Key, cancellation.Token));
    }

    public static async Task GenuineInputOverlap()
    {
        var repo = FindRepo();
        using var input = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repo, "fixtures/pi-v0.99.1/responses-replay/core.input.json")));
        using var expected = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repo, "fixtures/pi-v0.99.1/responses-replay/core.expected.json")));
        var index = 0;
        foreach (var item in input.RootElement.GetProperty("cases").EnumerateArray())
        {
            var oracle = expected.RootElement.GetProperty("observations").GetProperty("cases")[index];
            Equal(item.GetProperty("caseId").GetString(), oracle.GetProperty("caseId").GetString());
            var history = input.RootElement.GetProperty("contexts").GetProperty(item.GetProperty("contextRef").GetString()!).GetProperty("messages");
            var transcript = history.EnumerateArray().Select(value => new TranscriptEntry(value.GetProperty("role").GetString()!, JsonData.FromElement(value))).ToImmutableArray();
            var originals = transcript.Select(entry => entry.WireBody.ToString()).ToArray();
            var baseModel = input.RootElement.GetProperty("baseModel");
            var model = new ModelDescriptor(item.GetProperty("targetModel").GetProperty("id").GetString()!, baseModel.GetProperty("api").GetString()!, baseModel.GetProperty("provider").GetString()!);
            var projection = new ResponsesTranscriptProjectionOptions(false, IncludeInitialSystemPrompt: false,
                SupportsMidConversationSystemMessages: item.GetProperty("options").GetProperty("supportsMidConvoSystemMessages").GetBoolean(),
                AllowedToolCallProviders: item.GetProperty("allowedToolCallProviders").EnumerateArray().Select(value => value.GetString()!).ToImmutableHashSet(StringComparer.Ordinal));
            using var request = new ResponsesKeyAuthRequestFactory(Endpoint, model, projection).Create(new(model, transcript), Key);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var actual = payload.RootElement.GetProperty("input");
            var capturedSuffix = oracle.GetProperty("requestInput").EnumerateArray().Skip(1).ToArray();
            Equal(new[] { 5, 4, 6 }[index], actual.GetArrayLength());
            Equal(capturedSuffix.Length, actual.GetArrayLength());
            Check(actual.EnumerateArray().Zip(capturedSuffix).All(pair => Same(pair.First, pair.Second)), "Captured non-system input overlap changed.");
            for (var entry = 0; entry < transcript.Length; entry++) Equal(originals[entry], transcript[entry].WireBody.ToString());
            var call = actual.EnumerateArray().Single(value => value.TryGetProperty("type", out var type) && type.GetString() == "function_call");
            var output = actual.EnumerateArray().Single(value => value.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
            Equal(call.GetProperty("call_id").GetString(), output.GetProperty("call_id").GetString());
            index++;
        }
        Equal(3, index);
        using var one = JsonDocument.Parse("""{"n":1,"a":[null,"x"]}""");
        using var reordered = JsonDocument.Parse("""{"a":[null,"x"],"n":1}""");
        foreach (var mutation in new[] { """{"n":1.0,"a":[null,"x"]}""", """{"n":1,"a":["x",null]}""", """{"n":1,"a":["x"]}""" })
        {
            using var changed = JsonDocument.Parse(mutation);
            Check(!Same(one.RootElement, changed.RootElement), "Comparison hid numeric, order or null mutation.");
        }
        Check(Same(one.RootElement, reordered.RootElement), "Comparison treated object key order as significant.");
    }

    public static Task Admission()
    {
        Equal(ResponsesKeyAuthRequestFailure.UnsupportedOptions, Failure(() => new ResponsesKeyAuthRequestFactory(Endpoint, Model, new(true), new(ReasoningEffort: "unsupported"))).Failure);
        foreach (var raw in new[] { """{"stream":false}""", """{"reasoning":{"effort":"high"}}""", """{"tools":[]}""", """{"temperature":0}""", "{}" })
            Equal(ResponsesKeyAuthRequestFailure.UnsupportedOptions, Failure(() => Factory(new(PayloadOverrides: JsonData.Parse(raw)))).Failure);
        foreach (var endpoint in new[] { new Uri("relative", UriKind.Relative), new Uri("ftp://synthetic.invalid/"), new Uri("https://user:secret@synthetic.invalid/"), new Uri("https://synthetic.invalid/#secret") })
            Equal(ResponsesKeyAuthRequestFailure.InvalidConfiguration, Failure(() => new ResponsesKeyAuthRequestFactory(endpoint, Model, new(false))).Failure);
        foreach (var model in new[] { Model with { Api = "other" }, Model with { Id = "" }, Model with { Id = "\uD800" }, Model with { Provider = "\uDC00" } })
            Equal(ResponsesKeyAuthRequestFailure.InvalidConfiguration, Failure(() => new ResponsesKeyAuthRequestFactory(Endpoint, model, new(false))).Failure);
        foreach (var key in new[] { "", "private secret", "private\r\nInjected: key", "private\tkey", "private\u007f", "private\u00e9", null! })
        {
            var error = Failure(() => Factory().Create(Request(), key));
            Equal(ResponsesKeyAuthRequestFailure.InvalidKey, error.Failure);
            Check(!error.Message.Contains("private", StringComparison.Ordinal), "Key admission leaked credential.");
        }
        foreach (var raw in new[]
        {
            """{"role":"system","content":"","toolsAdded":[{"name":"private-tool"}]}""",
            """{"role":"user","content":[{"type":"image","data":"private-image","mimeType":"image/png"}]}"""
        })
        {
            var body = JsonData.Parse(raw);
            var error = Throws<ResponsesProjectionException>(() => Factory().Create(new(Model, [new(body.Value.GetProperty("role").GetString()!, body)]), Key));
            Equal(ResponsesProjectionFailure.UnsupportedContent, error.Failure);
            Check(!error.Message.Contains("private", StringComparison.Ordinal), "Unsupported payload leaked transcript.");
        }
        return Task.CompletedTask;
    }

    public static async Task LimitsAndCancellation()
    {
        var chat = Request();
        using var baseline = Factory().Create(chat, Key);
        var size = (await baseline.Content!.ReadAsByteArrayAsync()).Length;
        using var exact = Factory(new(MaximumPayloadBytes: size, MaximumKeyCharacters: Key.Length, MaximumModelCharacters: Model.Id.Length,
            MaximumEndpointCharacters: Endpoint.AbsoluteUri.Length, MaximumPayloadDepth: 5)).Create(chat, Key);
        Equal(size, (await exact.Content!.ReadAsByteArrayAsync()).Length);
        foreach (var options in new ResponsesKeyAuthRequestOptions[]
        {
            new(MaximumPayloadBytes: size - 1), new(MaximumKeyCharacters: Key.Length - 1),
            new(MaximumModelCharacters: Model.Id.Length - 1), new(MaximumEndpointCharacters: Endpoint.AbsoluteUri.Length - 1),
            new(MaximumPayloadDepth: 4)
        }) Equal(ResponsesKeyAuthRequestFailure.ResourceLimit, Failure(() => Factory(options).Create(chat, Key)).Failure);
        foreach (var options in new ResponsesKeyAuthRequestOptions[] { new(MaximumKeyCharacters: 0), new(MaximumPayloadBytes: 0), new(MaximumPayloadDepth: 65) })
            Equal(ResponsesKeyAuthRequestFailure.InvalidConfiguration, Failure(() => Factory(options)).Failure);
        Equal(ResponsesKeyAuthRequestFailure.InvalidConfiguration, Failure(() => new ResponsesKeyAuthRequestFactory(Endpoint, Model, new(false, MaximumMessages: 0))).Failure);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var error = Throws<OperationCanceledException>(() => Factory().Create(null!, null!, cancellation.Token));
        Equal(cancellation.Token, error.CancellationToken);
        await ToolDeclarationLimits();
    }

    public static async Task OfflineRoundtrip()
    {
        var bodies = new[] { new HttpCleanupGateStream(Encoding.UTF8.GetBytes(ResponseSse())), new HttpCleanupGateStream(Encoding.UTF8.GetBytes(ResponseSse())) };
        var responses = new List<StreamProbeContent>();
        var requests = new List<HttpRequestMessage>();
        using var handler = new FakeHttpHandler(async (request, cancellationToken) =>
        {
            Equal(Endpoint, request.RequestUri); Equal(Key, request.Headers.Authorization!.Parameter);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Equal(Model.Id, payload.RootElement.GetProperty("model").GetString());
            Equal(Text, payload.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
            var content = new StreamProbeContent(bodies[responses.Count]); responses.Add(content);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);
        var factory = Factory();
        var transport = new ResponsesHttpSseTransport(client, chat => { var request = factory.Create(chat, Key); requests.Add(request); return request; },
            new(Framing: new(ReadBufferBytes: 1, RejectInvalidUtf8: true)));
        for (var index = 0; index < bodies.Length; index++)
        {
            var run = new ChatClient(transport, capacity: 1).CompleteAsync(Request());
            try
            {
                await bodies[index].CleanupEntered.Task;
                Check(!run.IsCompleted && !responses[index].Disposed, "Completion or response disposal escaped before body cleanup.");
                Check((await requests[index].Content!.ReadAsByteArrayAsync()).Length > 0, "Owned request disposed before response cleanup.");
                bodies[index].ReleaseCleanup.TrySetResult();
                var result = await run;
                Check(result.Failure is null, "Offline factory/HTTP/mapper roundtrip failed.");
                Equal(StopReason.Stop, result.Message.StopReason);
                Equal(Text, ((TextContent)result.Message.Content.Single()).Text);
                Equal(1, bodies[index].AsyncDisposeCalls);
                Check(bodies[index].Disposed && responses[index].Disposed && !handler.Disposed, "Roundtrip retained resources or disposed borrowed client.");
                await Disposed(requests[index].Content!);
                Throws<ObjectDisposedException>(() => requests[index].Method = HttpMethod.Get);
            }
            finally { bodies[index].ReleaseCleanup.TrySetResult(); try { await run; } catch { } }
        }
        Equal(2, handler.SendCalls);
        Check(!ReferenceEquals(requests[0], requests[1]) && !ReferenceEquals(requests[0].Content, requests[1].Content), "Composition reused factory request/content.");
        await OfflineToolContinuation();
    }

    public static async Task NoAcquisition()
    {
        using var handler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("Unexpected acquisition."));
        using var client = new HttpClient(handler);
        var factories = new Func<ChatRequest, HttpRequestMessage>[]
        {
            chat => Factory().Create(chat, "private\nkey"),
            chat => Factory().Create(chat with { Model = Model with { Id = "wrong" } }, Key),
            chat => Factory(new(MaximumPayloadBytes: 1)).Create(chat, Key),
            chat => Factory(new(PayloadOverrides: JsonData.EmptyObject)).Create(chat, Key)
        };
        foreach (var factory in factories)
        {
            var result = await new ChatClient(new ResponsesHttpSseTransport(client, factory)).CompleteAsync(Request());
            Check(result.Failure is not null && result.Message.StopReason == StopReason.Error, "Invalid factory configuration succeeded.");
            Check(!result.Failure!.Message.Contains("private", StringComparison.Ordinal), "Factory failure leaked data through ChatClient.");
        }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var calls = 0;
        var transport = new ResponsesHttpSseTransport(client, chat => { calls++; return Factory().Create(chat, Key); });
        var cancelled = await new ChatClient(transport).CompleteAsync(Request(), cancellation.Token);
        Equal(ChatFailureKind.Cancelled, cancelled.Failure!.Kind); Equal(0, calls); Equal(0, handler.SendCalls);
        Check(!handler.Disposed, "Admission disposed borrowed client.");
    }

    private static async Task ActiveToolDeclarations()
    {
        // Pinned transcript.ts getCurrentTools/resolveTranscriptTools and convertResponsesTools:
        // removals precede additions, replacements retain position, removal/readd moves to the end.
        var history = ImmutableArray<TranscriptEntry>.Empty;
        var cases = new (string Delta, string Expected)[]
        {
            ("""{"toolsRemoved":[{"name":"undeclared"}],"toolsAdded":[{"name":"a","description":"first","parameters":{"type":"object"}},{"name":"b","description":"second","parameters":{"type":"object"}}]}""",
             """[{"type":"function","name":"a","description":"first","parameters":{"type":"object"},"strict":false},{"type":"function","name":"b","description":"second","parameters":{"type":"object"},"strict":false}]"""),
            ("""{"toolsAdded":[{"name":"a","description":"replacement","parameters":{"type":"object"}}]}""",
             """[{"type":"function","name":"a","description":"replacement","parameters":{"type":"object"},"strict":false},{"type":"function","name":"b","description":"second","parameters":{"type":"object"},"strict":false}]"""),
            ("""{"toolsRemoved":[{"name":"a"}],"toolsAdded":[{"name":"c","description":"third","parameters":{"type":"object"}}]}""",
             """[{"type":"function","name":"b","description":"second","parameters":{"type":"object"},"strict":false},{"type":"function","name":"c","description":"third","parameters":{"type":"object"},"strict":false}]"""),
            ("""{"toolsRemoved":[{"name":"c"}],"toolsAdded":[{"name":"a","description":"readded","parameters":{"type":"object"}}]}""",
             """[{"type":"function","name":"b","description":"second","parameters":{"type":"object"},"strict":false},{"type":"function","name":"a","description":"readded","parameters":{"type":"object"},"strict":false}]"""),
            ("""{"toolsRemoved":[{"name":"b"}],"toolsAdded":[{"name":"b","description":"same-delta","parameters":{"type":"object"}}]}""",
             """[{"type":"function","name":"a","description":"readded","parameters":{"type":"object"},"strict":false},{"type":"function","name":"b","description":"same-delta","parameters":{"type":"object"},"strict":false}]"""),
            ("""{"toolsRemoved":[{"name":"a"},{"name":"b"}]}""", "[]")
        };
        foreach (var (delta, expected) in cases)
        {
            history = history.Add(SystemDelta(delta));
            var originals = history.Select(message => message.WireBody.ToString()).ToArray();
            var chat = new ChatRequest(Model, history.Add(Request().Messages[0]));
            var tools = new ResponsesToolDeclarationProjector().Project(chat);
            Check(Same(JsonData.Parse(expected).Value, tools.Value), "Active tool replay differs from authored source-linked expectation.");
            using var request = Factory().Create(chat, Key);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (tools.Value.GetArrayLength() == 0) Check(!payload.RootElement.TryGetProperty("tools", out _), "Empty active tools were not omitted.");
            else Check(Same(JsonData.Parse(expected).Value, payload.RootElement.GetProperty("tools")), "Request omitted or rewrote active tool declarations.");
            Equal(1, new ResponsesTranscriptProjector(new(false)).Project(chat).Value.GetArrayLength());
            for (var index = 0; index < originals.Length; index++) Equal(originals[index], history[index].WireBody.ToString());
        }
        var unknownRemoval = new ChatRequest(Model, [SystemDelta("""{"toolsRemoved":[{"name":"never-declared"}]}""")]);
        Equal(0, new ResponsesTranscriptProjector(new(false)).Project(unknownRemoval).Value.GetArrayLength());

        var strictChat = new ChatRequest(Model, [SystemDelta("""{"toolsAdded":[{"name":"strict-tool","description":"strict","parameters":{"type":"object","properties":{"optional":{"type":"string"},"mandatory":{"type":"integer"},"nullable":{"type":["string","null"]}},"required":["mandatory"]},"constrainedSampling":{"type":"json_schema","strict":"require"}}]}""")]);
        const string strictExpected = """[{"type":"function","name":"strict-tool","description":"strict","parameters":{"type":"object","properties":{"optional":{"anyOf":[{"type":"string"},{"type":"null"}]},"mandatory":{"type":"integer"},"nullable":{"type":["string","null"]}},"required":["optional","mandatory","nullable"],"additionalProperties":false},"strict":true}]""";
        var strictOriginal = strictChat.Messages[0].WireBody.ToString();
        Check(Same(JsonData.Parse(strictExpected).Value, new ResponsesToolDeclarationProjector().Project(strictChat).Value), "Strict sampling conversion differs.");
        Equal(strictOriginal, strictChat.Messages[0].WireBody.ToString());
        using (var strictRequest = Factory().Create(strictChat, Key))
        {
            using var payload = JsonDocument.Parse(await strictRequest.Content!.ReadAsStringAsync());
            Check(Same(JsonData.Parse(strictExpected).Value, payload.RootElement.GetProperty("tools")), "Strict tools did not enter the payload.");
        }
        Equal(ResponsesProjectionFailure.UnsupportedContent, Throws<ResponsesProjectionException>(() =>
            new ResponsesToolDeclarationProjector(new(SupportsStrictMode: false)).Project(strictChat)).Failure);
        var ordinary = new ChatRequest(Model, [SystemDelta("""{"toolsAdded":[{"name":"a","description":"plain","parameters":{"type":"object","x-number":1.0,"x-null":null}}]}""")]);
        foreach (var options in new ResponsesToolDeclarationProjectionOptions[] { new(SupportsStrictMode: false), new(Strict: null) })
        {
            var projected = new ResponsesToolDeclarationProjector(options).Project(ordinary).Value[0];
            Equal("1.0", projected.GetProperty("parameters").GetProperty("x-number").GetRawText());
            Check(projected.GetProperty("parameters").TryGetProperty("x-null", out var opaque) && opaque.ValueKind == JsonValueKind.Null, "Opaque null lost.");
            if (options.SupportsStrictMode) Equal(JsonValueKind.Null, projected.GetProperty("strict").ValueKind);
            else Check(!projected.TryGetProperty("strict", out _), "Unsupported strict field was emitted.");
            using var request = new ResponsesKeyAuthRequestFactory(Endpoint, Model, new(false, ToolDeclarations: options)).Create(ordinary, Key);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(Same(projected, payload.RootElement.GetProperty("tools")[0]), "Factory ignored declared strict-mode capability.");
        }
        var fallback = new ChatRequest(Model, [SystemDelta("""{"toolsAdded":[{"name":"fallback","description":"prefer","parameters":{"type":"object","$ref":"#/private"},"constrainedSampling":{"type":"json_schema","strict":"prefer"}}]}""")]);
        Check(!new ResponsesToolDeclarationProjector().Project(fallback).Value[0].GetProperty("strict").GetBoolean(), "Prefer failed to fall back for unsupported strict schema.");
        foreach (var delta in new[]
        {
            """{"toolsAdded":[{"type":"custom","name":"private","description":"grammar","parameters":{}}]}""",
            """{"toolsAdded":[{"name":"private","description":"grammar","parameters":{"type":"object"},"constrainedSampling":{"type":"grammar","variants":{"openai_regex":".*"}}}]}""",
            """{"toolsAdded":[{"name":"private","description":"required","parameters":{"type":"object","$ref":"#/private"},"constrainedSampling":{"type":"json_schema","strict":"require"}}]}""",
            """{"toolsAdded":[{"name":"private","description":"unsupported sampling","parameters":{},"constrainedSampling":{"type":"future"}}]}""",
            """{"toolsRemoved":["private"]}"""
        })
        {
            var error = Throws<ResponsesProjectionException>(() => Factory().Create(new(Model, [SystemDelta(delta)]), Key));
            Equal(ResponsesProjectionFailure.UnsupportedContent, error.Failure);
            Check(!error.Message.Contains("private", StringComparison.Ordinal), "Declaration diagnostic leaked content.");
        }
        Throws<JsonException>(() => JsonData.Parse("""{"name":"a","\u006eame":"b"}"""));
        var unicode = new ChatRequest(Model, [SystemDelta("""{"toolsAdded":[{"name":"a","description":"\uD800","parameters":{}}]}""")]);
        Equal(ResponsesProjectionFailure.UnsupportedUnicode, Throws<ResponsesProjectionException>(() => Factory().Create(unicode, Key)).Failure);
        await SchemaNumbersAndDecodedDuplicates();
    }

    private static async Task SchemaNumbersAndDecodedDuplicates()
    {
        // getJsonSchemaToolParameters passes schemas through at strict:false; strict conversion
        // copies numeric constraints without applying the assistant argument-string integer profile.
        const string schema = """{"type":"object","properties":{"value":{"type":"number","minimum":0.5,"maximum":9007199254740993,"multipleOf":1e-3}},"required":["value"],"x-number":1.0,"x-null":null}""";
        var history = ImmutableArray.Create(SystemDelta("{\"toolsAdded\":[{\"name\":\"numeric\",\"description\":\"numeric constraints\",\"parameters\":" + schema + "}]}"));
        var chat = new ChatRequest(Model, history); var original = history[0].WireBody.ToString();
        foreach (var strict in new[] { false, true })
        {
            var options = new ResponsesToolDeclarationProjectionOptions(Strict: strict);
            var standalone = new ResponsesToolDeclarationProjector(options).Project(chat);
            Equal(0, new ResponsesTranscriptProjector(new(false, ToolDeclarations: options)).Project(chat).Value.GetArrayLength());
            using var request = new ResponsesKeyAuthRequestFactory(Endpoint, Model, new(false, ToolDeclarations: options)).Create(chat, Key);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var parameters = payload.RootElement.GetProperty("tools")[0].GetProperty("parameters");
            var expected = strict ? JsonData.Parse("""{"type":"object","properties":{"value":{"type":"number","minimum":0.5,"maximum":9007199254740993,"multipleOf":1e-3}},"required":["value"],"x-number":1.0,"x-null":null,"additionalProperties":false}""") : JsonData.Parse(schema);
            Check(Same(expected.Value, parameters), "Schema numeric tokens were rewritten or treated as tool arguments.");
            Check(Same(standalone.Value, payload.RootElement.GetProperty("tools")), "Standalone and request schema declarations differ.");
            Equal(original, history[0].WireBody.ToString());
        }
        foreach (var raw in new[]
        {
            """{"role":"system","content":"","toolsAdded":[{"name":"a","description":"duplicate schema","parameters":{"type":"object","\u0074ype":"string"}}]}""",
            """{"role":"system","content":"","toolsAdded":[{"name":"a","description":"duplicate property","parameters":{"type":"object","properties":{"value":{"type":"string"},"\u0076alue":{"type":"number"}}}}]}""",
            """{"role":"system","content":"","toolsAdded":[],"\u0074oolsAdded":[{"name":"a","description":"duplicate delta","parameters":{}}]}"""
        })
        {
            Throws<JsonException>(() => JsonData.Parse(raw));
            using var document = JsonDocument.Parse(raw);
            Throws<JsonException>(() => JsonData.FromElement(document.RootElement));
        }
    }

    private static async Task ToolDeclarationLimits()
    {
        var chat = new ChatRequest(Model, [SystemDelta("""{"toolsAdded":[{"name":"a","description":"文🙂","parameters":{"type":"object","properties":{"value":{"type":"integer"}}}}]}""")]);
        var raw = new ResponsesToolDeclarationProjector().Project(chat).ToString(); var bytes = Encoding.UTF8.GetByteCount(raw);
        var entryCharacters = chat.Messages[0].WireBody.ToString().Length;
        var exact = new ResponsesToolDeclarationProjectionOptions(MaximumMessages: 1, MaximumEntryCharacters: entryCharacters,
            MaximumInputCharacters: entryCharacters, MaximumDeclarations: 1, MaximumActiveTools: 1, MaximumJsonDepth: 6,
            MaximumOutputCharacters: raw.Length, MaximumOutputBytes: bytes);
        Equal(raw, new ResponsesToolDeclarationProjector(exact).Project(chat).ToString());
        foreach (var limits in new[] { exact with { MaximumEntryCharacters = entryCharacters - 1 }, exact with { MaximumInputCharacters = entryCharacters - 1 },
            exact with { MaximumJsonDepth = 5 }, exact with { MaximumOutputCharacters = raw.Length - 1 }, exact with { MaximumOutputBytes = bytes - 1 } })
            Equal(ResponsesProjectionFailure.ResourceLimit, Throws<ResponsesProjectionException>(() => new ResponsesToolDeclarationProjector(limits).Project(chat)).Failure);
        var two = chat with { Messages = chat.Messages.Add(SystemDelta("""{"toolsAdded":[{"name":"b","description":"second","parameters":{}}]}""")) };
        foreach (var limits in new ResponsesToolDeclarationProjectionOptions[] { new(MaximumMessages: 1), new(MaximumDeclarations: 1), new(MaximumActiveTools: 1) })
            Equal(ResponsesProjectionFailure.ResourceLimit, Throws<ResponsesProjectionException>(() => new ResponsesToolDeclarationProjector(limits).Project(two)).Failure);
        using var baseline = Factory().Create(chat, Key); var payloadBytes = (await baseline.Content!.ReadAsByteArrayAsync()).Length;
        using var admitted = Factory(new(MaximumPayloadBytes: payloadBytes)).Create(chat, Key);
        Equal(payloadBytes, (await admitted.Content!.ReadAsByteArrayAsync()).Length);
        Equal(ResponsesKeyAuthRequestFailure.ResourceLimit, Failure(() => Factory(new(MaximumPayloadBytes: payloadBytes - 1)).Create(chat, Key)).Failure);
        Equal(ResponsesKeyAuthRequestFailure.ResourceLimit, Failure(() => Factory(new(MaximumPayloadDepth: 5)).Create(chat, Key)).Failure);
        Throws<ArgumentOutOfRangeException>(() => new ResponsesToolDeclarationProjector(new(MaximumDeclarations: 0)));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Throws<OperationCanceledException>(() => new ResponsesToolDeclarationProjector().Project(chat, cancellation.Token));
        Equal(raw, new ResponsesToolDeclarationProjector().Project(chat).ToString());
    }

    private static async Task OfflineToolContinuation()
    {
        var sent = new List<JsonData>(); var responses = new List<StreamProbeContent>();
        using var handler = new FakeHttpHandler(async (request, token) =>
        {
            sent.Add(JsonData.Parse(await request.Content!.ReadAsStringAsync(token)));
            var sse = sent.Count == 1 ? string.Concat(new[]
            {
                """{"type":"response.output_item.added","output_index":9,"item":{"type":"function_call","id":"fc-key","call_id":"call-key","name":"lookup","arguments":""}}""",
                """{"type":"response.output_item.done","output_index":9,"item":{"type":"function_call","id":"fc-key","call_id":"call-key","name":"lookup","arguments":"{\"value\":7}"}}""",
                """{"type":"response.completed","response":{"id":"response-tool","status":"completed","output":[]}}"""
            }.Select(value => "data: " + value + "\n\n")) : ResponseSse();
            var content = new StreamProbeContent(new ProbeStream(Encoding.UTF8.GetBytes(sse))); responses.Add(content);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler); var factory = Factory();
        var chatClient = new ChatClient(new ResponsesHttpSseTransport(client, chat => factory.Create(chat, Key)), capacity: 1);
        var history = ImmutableArray.Create(SystemDelta("""{"toolsAdded":[{"name":"lookup","description":"Lookup a value","parameters":{"type":"object","properties":{"value":{"type":"integer"}},"required":["value"]}}]}"""), Request().Messages[0]);
        var first = await chatClient.CompleteAsync(new(Model, history));
        Check(first.Failure is null, "First declared-tool HTTP request failed.");
        var call = first.Message.Content.OfType<ToolCallContent>().Single(); Equal("lookup", call.Name); Equal(7, call.Arguments.Value.GetProperty("value").GetInt32());
        var completed = history.Add(new("assistant", PiWireJson.WriteMessage(first.Message))).Add(new("toolResult",
            JsonData.FromElement(JsonSerializer.SerializeToElement(new { role = "toolResult", toolCallId = call.Id, toolName = call.Name,
                content = new[] { new { type = "text", text = "looked up 7" } }, details = new { value = 7 }, isError = false, timestamp = 0 }))));
        var originals = completed.Select(entry => entry.WireBody.ToString()).ToArray();
        var second = await chatClient.CompleteAsync(new(Model, completed));
        Check(second.Failure is null && second.Message.StopReason == StopReason.Stop, "Declared tool/result continuation failed.");
        Equal(Text, second.Message.Content.OfType<TextContent>().Single().Text); Equal(2, handler.SendCalls);
        Check(Same(sent[0].Value.GetProperty("tools"), sent[1].Value.GetProperty("tools")), "Active declarations changed on continuation.");
        var items = sent[1].Value.GetProperty("input");
        var function = items.EnumerateArray().Single(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call");
        var result = items.EnumerateArray().Single(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
        Equal(function.GetProperty("call_id").GetString(), result.GetProperty("call_id").GetString());
        Equal("{\"value\":7}", function.GetProperty("arguments").GetString()); Equal("looked up 7", result.GetProperty("output").GetString());
        Check(responses.All(response => response.Disposed), "Tool continuation retained response content.");
        for (var index = 0; index < completed.Length; index++) Equal(originals[index], completed[index].WireBody.ToString());
    }

    private static TranscriptEntry SystemDelta(string delta) => new("system", JsonData.Parse(
        "{\"role\":\"system\",\"content\":\"\",\"timestamp\":0," + delta[1..]));

    private static ResponsesKeyAuthRequestFactory Factory(ResponsesKeyAuthRequestOptions? options = null) => new(Endpoint, Model, new(false), options);
    private static ChatRequest Request() => new(Model, [new("user", JsonData.FromElement(JsonSerializer.SerializeToElement(new { role = "user", content = Text, timestamp = 0 })))]);
    private static string ResponseSse()
    {
        var end = JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = 4,
            item = new { type = "message", id = "msg-key", content = new[] { new { type = "output_text", text = Text } } } });
        return string.Concat(new[]
        {
            """{"type":"response.output_item.added","output_index":4,"item":{"type":"message","id":"msg-key","content":[]}}""",
            """{"type":"response.output_text.delta","output_index":4,"item_id":"msg-key","delta":"preview"}""", end,
            """{"type":"response.completed","response":{"id":"response-key","status":"completed","output":[],"usage":{"input_tokens":1,"output_tokens":2,"total_tokens":3}}}"""
        }.Select(data => "data: " + data + "\n\n"));
    }
    private static async Task Disposed(HttpContent content)
    { try { await content.ReadAsByteArrayAsync(); } catch (ObjectDisposedException) { return; } throw new InvalidOperationException("Owned request content retained after cleanup."); }
    private static ResponsesKeyAuthRequestException Failure(Action action) => Throws<ResponsesKeyAuthRequestException>(action);
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
    private static bool Same(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        return left.ValueKind switch
        {
            JsonValueKind.Object => left.EnumerateObject().Count() == right.EnumerateObject().Count() &&
                left.EnumerateObject().All(property => right.TryGetProperty(property.Name, out var value) && Same(property.Value, value)),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Same(pair.First, pair.Second)),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.GetRawText() == right.GetRawText(),
            _ => left.GetRawText() == right.GetRawText()
        };
    }
    private static string FindRepo()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "fixtures/pi-v0.99.1/responses-replay/core.input.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Replay fixture root was not found.");
    }
}
