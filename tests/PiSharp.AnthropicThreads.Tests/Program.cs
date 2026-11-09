using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.Contracts;

internal static class Program
{
    private static readonly ModelDescriptor Model = new("claude-test", "anthropic-messages", "anthropic");
    private static TranscriptEntry Entry(string text) { var data = JsonData.Parse(text); return new(data.Value.GetProperty("role").GetString()!, data); }
    private static TranscriptEntry User(string text) => Entry("{\"role\":\"user\",\"content\":\"" + text + "\",\"timestamp\":1}");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<int> Main()
    {
        Definitions();
        Console.WriteLine("PASS Anthropic root definitions: ordinary, prefer, fallback, inline; required strict rejection");
        await Continuation();
        await OAuthToolContinuation();
        await PreparedUtf16Parity();
        await SurrogateKeyHistoryContinuation();
        await Fallbacks();
        await EligibilityAndIsolation();
        await ConcurrentAndCleanup();
        await FailedAndAbandonedStreams();
        WarmPolicy();
        await FailedToolFragmentAllocation();
        Console.WriteLine("PASS Anthropic continuation: normalization, OAuth tool roundtrip, mutation, fixed settings, hooks, retries, scope, warm isolation, cancellation, bounds, cleanup and concurrency");
        return 0;
    }
    private static async Task FailedToolFragmentAllocation()
    {
        // A compact stream appends raw tool fragments until the block ends; a failed stream must
        // not reconstruct an ever-growing display snapshot after each fragment.
        static async Task<long> Measure(int bytes)
        {
            var start = JsonData.Parse("{\"type\":\"message_start\",\"message\":{\"id\":\"failed\",\"model\":\"claude-test\",\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}");
            var tool = JsonData.Parse("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"call\",\"name\":\"read\",\"input\":{\"initial\":true}}}");
            var opening = JsonData.Parse("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"value\\\":\\\"\"}}");
            var delta = JsonData.Parse("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"" + new string('x', 512) + "\"}}");
            async IAsyncEnumerable<JsonData> Source([EnumeratorCancellation] CancellationToken token)
            {
                await Task.CompletedTask;
                yield return start; yield return tool; yield return opening;
                for (var index = 0; index < bytes / 512; index++) { token.ThrowIfCancellationRequested(); yield return delta; }
                // EOF with an unfinished tool is the failure path under assessment.
            }
            var transport = new AnthropicMessagesTransport((_, token) => Source(token));
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var thread = Environment.CurrentManagedThreadId;
            ToolCallStarted? retained = null; StreamTerminalEvent? terminal = null; var fragments = 0;
            await foreach (var frame in transport.StreamAsync(new(Model, [])))
            {
                Check(frame.SourceEmissionSnapshot is null, "Default failed stream materialized source snapshots");
                if (frame is ToolCallStarted started) retained = started;
                if (frame is ToolCallDelta) fragments++;
                if (frame is StreamTerminalEvent done) terminal = done;
            }
            var result = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Check(Environment.CurrentManagedThreadId == thread, "Allocation test unexpectedly yielded to another thread");
            Check(fragments == bytes / 512 + 1 && terminal is StreamError { Reason: StopReason.Error }, "Failed fragment stream changed its events");
            Check(retained!.ToolCall.Arguments.Value.GetProperty("initial").GetBoolean() &&
                terminal!.Message.ExtraProperties!.TryGet("anthropicFailure", out var classification) && classification!.Value.GetString() == "UnexpectedEof", "Failed fragment stream changed retained state or diagnostics");
            return result;
        }
        await Measure(4096); // Warm the same append and failure path before comparing allocations.
        var small = await Measure(64 * 1024);
        var large = await Measure(256 * 1024);
        Check(large <= small * 6 + 2 * 1024 * 1024, $"Failed tool-fragment allocations grew superlinearly: {small} => {large}");
        Console.WriteLine($"PASS Anthropic compact failed tool fragments: 64KiB={small}B; 256KiB={large}B allocated");
    }
    private sealed class Endpoint : HttpMessageHandler
    {
        internal readonly List<JsonObject> Bodies = [];
        internal Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            int index;
            var body = JsonUtf16.MutableNode(JsonData.Parse(await request.Content!.ReadAsStringAsync(token)))!.AsObject();
            lock (Bodies) { Bodies.Add(body); index = Bodies.Count; }
            Check(request.Headers.TryGetValues("anthropic-beta", out var betas) && betas.Single().Contains("message-threads-2026-08-12"), "Thread beta absent");
            return Respond is null ? Success(index) : await Respond(index, request, token);
        }
    }
    private static HttpResponseMessage Success(int id, string model = "claude-test") => new(HttpStatusCode.OK)
    {
        Content = new StringContent("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_" + id + "\",\"model\":\"" + model + "\",\"usage\":{\"input_tokens\":10,\"output_tokens\":0}}}\n\n" +
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"ok\"}}\n\n" +
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n" +
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", Encoding.UTF8, "text/event-stream")
    };
    private static HttpResponseMessage Rejection(string code) => new(HttpStatusCode.BadRequest)
    { Content = new StringContent("{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"thread rejected\",\"details\":{\"error_code\":\"" + code + "\"}}}", Encoding.UTF8, "application/json") };
    private static AnthropicMessagesHttpSseTransport Transport(HttpClient client, AnthropicMessageThreadStore store,
        string host = "api.anthropic.com", bool? supports = null, AnthropicMessagesHooks? hooks = null, int max = 100)
    {
        var factory = new AnthropicMessagesKeyAuthRequestFactory(new("https://" + host), Model,
            new(max) { SupportsMessageThreads = supports }, new() { ThreadStore = store });
        return AnthropicMessagesHttpSseTransport.FromPrepared(client, (request, token) => factory.Prepare(request, "inert-test-key", token), hooks: hooks);
    }
    private static async Task<AssistantMessage> Run(IChatTransport transport, ImmutableArray<TranscriptEntry> entries, bool disabled = false, string session = "test", CancellationToken token = default)
    {
        StreamTerminalEvent? terminal = null;
        await foreach (var item in transport.StreamAsync(new(Model, entries) { SessionId = session, DisableContinuation = disabled }, token))
            if (item is StreamTerminalEvent done) terminal = done;
        return terminal?.Message ?? throw new InvalidOperationException("No terminal");
    }
    private static TranscriptEntry Reply(AssistantMessage message) => new("assistant", PiWireJson.WriteMessage(message));
    private static string? Thread(JsonObject body) => body["thread"]?["type"]?.GetValue<string>();
    private static async Task SurrogateKeyHistoryContinuation()
    {
        var store = new AnthropicMessageThreadStore(); using var endpoint = new Endpoint(); using var client = new HttpClient(endpoint);
        var transport = Transport(client, store);
        static TranscriptEntry HistoricalTool(string arguments) => Reply(new(Model.Api, Model.Provider, Model.Id, 0,
            [new ToolCallContent("old-call", "read", JsonData.Parse(arguments))], TokenUsage.Zero, StopReason.ToolUse));
        var result = Entry("{\"role\":\"toolResult\",\"toolCallId\":\"old-call\",\"toolName\":\"read\",\"content\":[{\"type\":\"text\",\"text\":\"old result\"}],\"isError\":false,\"timestamp\":1}");
        ImmutableArray<TranscriptEntry> initial = [User("old prompt"), HistoricalTool("{\"\\udc00\":\"value\"}"), result, User("next prompt")];
        var first = await Run(transport, initial, session: "surrogate");
        Check(first.StopReason == StopReason.Stop, "Historical-key thread create failed before send/body check: " + PiWireJson.WriteMessage(first));
        Check(endpoint.Bodies.Count == 1 && endpoint.Bodies[0]["messages"]![1]!["content"]![0]!["input"]!.AsObject().Single().Key == "\uDC00", "Thread create replaced historical surrogate key");
        var second = await Run(transport, [..initial, Reply(first), User("more")], session: "surrogate");
        Check(second.StopReason == StopReason.Stop && Thread(endpoint.Bodies[1]) == "continue", "Surrogate-key history failed to continue");
        ImmutableArray<TranscriptEntry> edited = [User("old prompt"), HistoricalTool("{\"\\ufffd\":\"value\"}"), result, User("next prompt"), Reply(first), User("more"), Reply(second), User("last")];
        var third = await Run(transport, edited, session: "surrogate");
        Check(third.StopReason == StopReason.Stop && Thread(endpoint.Bodies[2]) == "create", "Thread hash conflated surrogate key with replacement character");
    }
    private sealed class ProviderOwner(NativeHttpModelProvider provider) : IAsyncDisposable
    { public ValueTask DisposeAsync() { provider.Dispose(); return ValueTask.CompletedTask; } }
    private static async Task OAuthToolContinuation()
    {
        const string fakeOAuth = "inert-sk-ant-oat-test";
        var store = new AnthropicMessageThreadStore();
        using var endpoint = new Endpoint
        {
            Respond = (index, request, _) =>
            {
                Check(request.Headers.Authorization?.ToString() == "Bearer " + fakeOAuth, "OAuth credential channel changed");
                return Task.FromResult(index == 1 ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"model\":\"claude-test\",\"content\":[],\"usage\":{\"input_tokens\":10,\"output_tokens\":0}}}\n\n" +
                        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"oauth_read_1\",\"name\":\"Read\",\"input\":{}}}\n\n" +
                        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
                        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":1}}\n\n" +
                        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", Encoding.UTF8, "text/event-stream")
                } : Success(index));
            }
        };
        var auth = await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(
            new ProviderEnvironmentSnapshot([KeyValuePair.Create<string, string?>("ANTHROPIC_OAUTH_TOKEN", fakeOAuth)]),
            (_, _) => ValueTask.FromResult<StoredApiKeyCredential?>(null));
        await using var lease = await AnthropicInjectedTransportAdapter.AcquireAsync(Model, auth, (model, binding, _) =>
        {
            // OAuthProjection intentionally uses its default false: authentication resolves the effective projection.
            var provider = AnthropicResolvedProviderFactory.Create(model, new("https://api.anthropic.com"), binding,
                new(100), new() { ThreadStore = store }, endpoint);
            return ValueTask.FromResult(new AnthropicTransportAdmission(model, provider.Transport, new ProviderOwner(provider)));
        });
        var declaration = Entry("{\"role\":\"system\",\"content\":\"Use read.\",\"toolsAdded\":[{\"name\":\"read\",\"description\":\"Read a file\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}],\"timestamp\":0}");
        ImmutableArray<TranscriptEntry> initial = [declaration, User("read file")];
        var reply = await Run(lease.Transport, initial, session: "oauth");
        Check(reply.StopReason == StopReason.ToolUse && reply.Content.OfType<ToolCallContent>().Single().Name == "read", "OAuth reply did not map Read to canonical read");
        Check(endpoint.Bodies[0]["tools"]![0]!["name"]!.GetValue<string>() == "Read", "OAuth declaration did not map read to Read");
        var result = Entry("{\"role\":\"toolResult\",\"toolCallId\":\"oauth_read_1\",\"toolName\":\"read\",\"content\":[{\"type\":\"text\",\"text\":\"file content\"}],\"isError\":false,\"timestamp\":2}");
        var final = await Run(lease.Transport, [..initial, Reply(reply), result], session: "oauth");
        var sent = endpoint.Bodies[1];
        Check(final.StopReason == StopReason.Stop && Thread(sent) == "continue" && sent["thread"]!["previous_message_id"]!.GetValue<string>() == "msg_1", "OAuth tool reply prefix failed to continue");
        Check(sent["system"] is null && sent["tools"] is null && sent["messages"]!.AsArray().Count == 1 &&
            sent["messages"]![0]!["content"]![0]!["type"]!.GetValue<string>() == "tool_result", "OAuth continuation did not send only the new tool result");
    }
    private static async Task Continuation()
    {
        var store = new AnthropicMessageThreadStore(); using var endpoint = new Endpoint(); using var client = new HttpClient(endpoint);
        var transport = Transport(client, store); ImmutableArray<TranscriptEntry> entries = [User("hello")];
        var first = await Run(transport, entries); Check(first.StopReason == StopReason.Stop, "First request failed");
        entries = [..entries, Reply(first), User("next")]; var second = await Run(transport, entries);
        Check(Thread(endpoint.Bodies[0]) == "create" && Thread(endpoint.Bodies[1]) == "continue", "Did not continue normalized cached string message");
        Check(endpoint.Bodies[1]["messages"]!.AsArray().Count == 1 && endpoint.Bodies[1]["thread"]!["previous_message_id"]!.GetValue<string>() == "msg_1", "Wrong continuation suffix/id");
        entries = [User("edited"), Reply(first), User("next"), Reply(second), User("again")];
        await Run(transport, entries); Check(Thread(endpoint.Bodies[2]) == "create", "Edited prefix incorrectly continued");
        var changed = Transport(client, store, max: 200);
        var third = await Run(transport, [User("reset")], session: "settings");
        await Run(changed, [User("reset"), Reply(third), User("more")], session: "settings");
        Check(Thread(endpoint.Bodies[^1]) == "continue", "Output cap unnecessarily resets thread");
        var marker = "a";
        var hooked = Transport(client, store, hooks: new AnthropicMessagesHooks { OnPayload = (payload, _, _) =>
        { var root = JsonNode.Parse(payload.ToString())!.AsObject(); root["system"] = marker; return Task.FromResult<JsonData?>(JsonData.Parse(root.ToJsonString())); } });
        var hookFirst = await Run(hooked, [User("hook")], session: "hook"); marker = "b";
        await Run(hooked, [User("hook"), Reply(hookFirst), User("more")], session: "hook");
        Check(Thread(endpoint.Bodies[^1]) == "create", "Hook settings change incorrectly continued");
    }
    private static async Task Fallbacks()
    {
        foreach (var code in new[] { "thread_not_found", "thread_already_continued", "thread_continue_in_flight", "thread_fingerprint_mismatch", "thread_unsupported_request" })
        {
            var store = new AnthropicMessageThreadStore(); using var endpoint = new Endpoint(); using var client = new HttpClient(endpoint);
            var transport = Transport(client, store); var first = await Run(transport, [User("hello")]);
            endpoint.Respond = (index, _, _) => Task.FromResult(index == 2 ? Rejection(code) : Success(index));
            var reply = await Run(transport, [User("hello"), Reply(first), User("next")]);
            Check(reply.StopReason == StopReason.Stop && endpoint.Bodies.Count == 3, "Retry did not succeed");
            var unsupported = code == "thread_unsupported_request";
            Check(Thread(endpoint.Bodies[2]) == (unsupported ? null : "create") && endpoint.Bodies[2]["messages"]!.AsArray().Count == 3, "Retry did not send full transcript");
            Check(reply.ExtraProperties!.TryGet("diagnostics", out var diagnostics) && diagnostics!.ToString().Contains("anthropic_thread_fallback"), "No fallback diagnostic");
            await Run(transport, [User("hello"), Reply(first), User("next"), Reply(reply), User("last")]);
            Check(Thread(endpoint.Bodies[^1]) == (unsupported ? null : "continue"), "Retry state not retained");
        }
        using var failing = new Endpoint { Respond = (_, _, _) => Task.FromResult(Rejection("arbitrary_failure")) }; using var failClient = new HttpClient(failing);
        var failed = await Run(Transport(failClient, new()), [User("fail")]);
        Check(failed.StopReason == StopReason.Error && failing.Bodies.Count == 1, "Unrelated failure retried");
    }
    private static async Task EligibilityAndIsolation()
    {
        var store = new AnthropicMessageThreadStore(); using var endpoint = new Endpoint(); using var client = new HttpClient(endpoint);
        var foreground = Transport(client, store); var first = await Run(foreground, [User("hello")]);
        await Run(foreground, [User("hello")], disabled: true);
        Check(Thread(endpoint.Bodies[1]) is null, "Warm request used thread");
        await Run(foreground, [User("hello"), Reply(first), User("next")]);
        Check(Thread(endpoint.Bodies[2]) == "continue" && endpoint.Bodies[2]["thread"]!["previous_message_id"]!.GetValue<string>() == "msg_1", "Warm request consumed or overwrote foreground reply");
        // A no-thread endpoint deliberately does not receive the beta, so inspect it with a plain handler.
        using var plain = new PlainEndpoint(); using var plainClient = new HttpClient(plain);
        await Run(Transport(plainClient, new(), host: "gateway.example"), [User("gateway")]);
        Check(plain.Body!["thread"] is null && !plain.Beta, "Gateway default allowed continuation");
        await Run(Transport(client, new(), host: "gateway.example", supports: true), [User("gateway")]);
        Check(Thread(endpoint.Bodies[^1]) == "create", "Explicit gateway opt-in ignored");
        await Run(Transport(plainClient, new(), supports: false), [User("disabled")]);
        Check(plain.Body!["thread"] is null, "Explicit direct-endpoint disable ignored");
    }
    private sealed class PlainEndpoint : HttpMessageHandler
    {
        internal JsonObject? Body; internal bool Beta; internal int Calls; internal string? Raw;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; Raw = await request.Content!.ReadAsStringAsync(token); Body = JsonUtf16.MutableNode(JsonData.Parse(Raw))!.AsObject(); Beta = request.Headers.TryGetValues("anthropic-beta", out var values) && values.Single().Contains("message-threads"); return Success(1); }
    }
    private static async Task PreparedUtf16Parity()
    {
        var history = Reply(new(Model.Api, Model.Provider, Model.Id, 0,
            [new ToolCallContent("old-call", "read", JsonData.Parse("{\"\\udc00\":\"\\ud800\"}"))], TokenUsage.Zero, StopReason.ToolUse));
        var result = Entry("{\"role\":\"toolResult\",\"toolCallId\":\"old-call\",\"toolName\":\"read\",\"content\":[{\"type\":\"text\",\"text\":\"result\"}],\"isError\":false,\"timestamp\":1}");
        ImmutableArray<TranscriptEntry> entries = [User("read"), history, result, User("continue")];
        var request = new ChatRequest(Model, entries);
        var factory = new AnthropicMessagesKeyAuthRequestFactory(new("https://anthropic.invalid"), Model, new(100));
        using var direct = factory.Create(request, "inert-test-key"); var expected = JsonData.Parse(await direct.Content!.ReadAsStringAsync());
        foreach (var echo in new[] { false, true })
        {
            using var endpoint = new PlainEndpoint(); using var client = new HttpClient(endpoint);
            var hooks = !echo ? null : new AnthropicMessagesHooks { OnPayload = (payload, _, _) => Task.FromResult<JsonData?>(payload) };
            var transport = AnthropicMessagesHttpSseTransport.FromPrepared(client, (current, token) => factory.Prepare(current, "inert-test-key", token), hooks: hooks);
            var final = await Run(transport, entries);
            Check(final.StopReason == StopReason.Stop && endpoint.Calls == 1, "UTF16 prepared request failed: " + PiWireJson.WriteMessage(final));
            Check(JsonUtf16.DeepEquals(expected.Value, JsonData.Parse(endpoint.Raw!).Value), "Direct and prepared JSON differ for surrogate key/value");
        }
        using var extraEndpoint = new PlainEndpoint(); using var extraClient = new HttpClient(extraEndpoint);
        var extraHooks = new AnthropicMessagesHooks { OnPayload = (payload, _, _) =>
        { var root = JsonUtf16.MutableNode(payload)!.AsObject(); root["\uDC00"] = JsonUtf16.StringNode("\uD800"); return Task.FromResult<JsonData?>(JsonData.Parse(JsonUtf16.ToJsonString(root))); } };
        var extra = AnthropicMessagesHttpSseTransport.FromPrepared(extraClient, (current, token) => factory.Prepare(current, "inert-test-key", token), hooks: extraHooks);
        StreamTerminalEvent? extraTerminal = null;
        await foreach (var frame in extra.StreamAsync(new(Model, entries))) if (frame is StreamTerminalEvent terminal) extraTerminal = terminal;
        Check(extraTerminal is StreamDone && extraEndpoint.Calls == 1, "Hook root surrogate name/value failed; sends=" + extraEndpoint.Calls +
            "; message=" + (extraTerminal is null ? "missing" : PiWireJson.WriteMessage(extraTerminal.Message).ToString()) + "; original=" + extraTerminal?.NativeSourceException);
        var property = JsonData.Parse(extraEndpoint.Raw!).Value.EnumerateObject().Single(p => JsonUtf16.GetName(p) == "\uDC00");
        Check(JsonUtf16.GetString(property.Value) == "\uD800", "Hook copy replaced root name/value");
        foreach (var invalid in new[] { "{\"max_tokens\":\"bad\"}", "{\"betas\":[\"\\ud800\"]}", "[]",
            "{\"max_tokens\":\"bad\",\"\\udc00\":null}", "{\"betas\":[1],\"\\udc00\":null}", "{\"max_tokens\":1,\"max_tokens\":2,\"\\udc00\":null}", "{\"\\udc00\":1,\"\\uDC00\":2}" })
        {
            using var endpoint = new PlainEndpoint(); using var client = new HttpClient(endpoint);
            var hooks = new AnthropicMessagesHooks { OnPayload = (_, _, _) => Task.FromResult<JsonData?>(JsonData.Parse(invalid)) };
            var transport = AnthropicMessagesHttpSseTransport.FromPrepared(client, (current, token) => factory.Prepare(current, "inert-test-key", token), hooks: hooks);
            var failed = await Run(transport, entries);
            Check(failed.StopReason == StopReason.Error && endpoint.Calls == 0, "Invalid prepared payload reached HTTP");
        }
        var metadata = JsonData.Parse("{\"id\":\"claude-test\",\"api\":\"anthropic-messages\",\"provider\":\"anthropic\",\"maxTokens\":1000,\"contextWindow\":100000,\"reasoning\":false}");
        var simple = new AnthropicMessagesSimpleRequestFactory(extraClient, new("https://anthropic.invalid"), Model, new(metadata, new(100)));
        var text = Entry("{\"role\":\"user\",\"content\":\"hi\\ud800\",\"timestamp\":1}");
        Check(simple.Resolve(new(Model, [text])).ContextEstimate.Tokens == 1, "Simple estimator rejected an admitted UTF16 text value");
        Console.WriteLine("PASS Anthropic direct/prepared UTF16 key/value parity, hook copy and invalid-payload no-send");
    }
    private static async Task ConcurrentAndCleanup()
    {
        var store = new AnthropicMessageThreadStore(2); using var endpoint = new Endpoint(); using var client = new HttpClient(endpoint);
        var transport = Transport(client, store); var first = await Run(transport, [User("hello")]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        endpoint.Respond = async (index, _, token) => { if (index == 2) { entered.SetResult(); await release.Task.WaitAsync(token); } return Success(index); };
        ImmutableArray<TranscriptEntry> next = [User("hello"), Reply(first), User("next")];
        var held = Run(transport, next); await entered.Task;
        var concurrent = await Run(transport, next); release.SetResult(); await held;
        Check(Thread(endpoint.Bodies[1]) == "continue" && Thread(endpoint.Bodies[2]) == "create", "Concurrent requests reused reply ID");
        await Run(transport, [..next, Reply(concurrent), User("last")]);
        Check(endpoint.Bodies[^1]["thread"]?["previous_message_id"]?.GetValue<string>() == "msg_3", "Stale concurrent completion overwrote newer reply");
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously); release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        endpoint.Respond = async (index, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Success(index); };
        var pending = Run(transport, [User("cleanup")], session: "cleanup"); await entered.Task; store.Cleanup("cleanup"); release.SetResult(); await pending;
        Check(store.Count == 1, "Late reply resurrected cleaned session");
        endpoint.Respond = null;
        foreach (var id in new[] { "one", "two", "three" }) await Run(transport, [User(id)], session: id);
        Check(store.Count == 2, "Session store unbounded"); store.Cleanup(); Check(store.Count == 0, "Cleanup all failed");
        endpoint.Respond = (_, _, token) => Task.FromCanceled<HttpResponseMessage>(token.IsCancellationRequested ? token : new CancellationToken(true));
        var cancelled = await Run(transport, [User("cancel")]);
        Check(cancelled.StopReason is StopReason.Error or StopReason.Aborted, "Cancelled send accepted");
        endpoint.Respond = null; await Run(transport, [User("cancel"), User("again")]);
        Check(Thread(endpoint.Bodies[^1]) == "create", "Cancelled send recorded reply");
    }
    private static async Task FailedAndAbandonedStreams()
    {
        var store = new AnthropicMessageThreadStore(); using var endpoint = new Endpoint(); using var client = new HttpClient(endpoint);
        var transport = Transport(client, store);
        endpoint.Respond = (index, _, _) => Task.FromResult(Success(index, "other-model"));
        var fallback = await Run(transport, [User("first")]); endpoint.Respond = null;
        await Run(transport, [User("first"), Reply(fallback), User("second")]);
        Check(Thread(endpoint.Bodies[^1]) == "create", "Fallback-model reply became continuation state");
        var iterator = transport.StreamAsync(new(Model, [User("abandon")]) { SessionId = "abandon" }).GetAsyncEnumerator();
        while (await iterator.MoveNextAsync()) if (iterator.Current is TextStarted) break;
        await iterator.DisposeAsync();
        await Run(transport, [User("abandon"), User("retry")], session: "abandon");
        Check(Thread(endpoint.Bodies[^1]) == "create", "Abandoned stream became continuation state");
        endpoint.Respond = (_, _, _) => Task.FromResult(Rejection("thread_not_found"));
        var failure = await Run(transport, [User("failed retry")], session: "failed retry");
        Check(failure.StopReason == StopReason.Error && failure.ExtraProperties!.TryGet("diagnostics", out var diagnostics) && diagnostics!.ToString().Contains("anthropic_thread_fallback"), "Failed resend lost diagnostic");
    }
    private static void WarmPolicy()
    {
        var request = new ChatRequest(Model, [User("warm")]) { SessionId = "foreground" };
        var replay = PiSharp.AI.ModelOperations.CacheWarmingPolicy.PrepareReplay(request);
        Check(replay.DisableContinuation && !request.DisableContinuation && replay.SessionId == request.SessionId, "Warm policy mutated or dropped request context");
        Check(PiSharp.AI.ModelOperations.CacheWarmingPolicy.ReplayMaximumTokens == 1 && PiSharp.AI.ModelOperations.CacheWarmingPolicy.ReplayMaximumRetries == 0, "Warm replay limits changed");
        Check(PiSharp.AI.ModelOperations.CacheWarmingPolicy.TtlMs(300, 3600, environment: new Dictionary<string, string?> { ["PI_CACHE_RETENTION"] = "long" }) == 3_600_000, "Warm TTL scope ignored");
    }
    private static void Definitions()
    {
        foreach (var keyword in new[] { "$defs", "definitions" })
        foreach (var strict in new[] { "", ",\"constrainedSampling\":{\"type\":\"json_schema\",\"strict\":\"prefer\"}" })
        foreach (var fallback in new[] { false, true })
        foreach (var inline in new[] { false, true })
        {
            var schema = "{\"type\":\"object\",\"properties\":{\"item\":{\"$ref\":\"#/" + keyword + "/Item\"}},\"required\":[\"item\"],\"" + keyword + "\":{\"Item\":{\"type\":\"string\",\"enum\":[\"kept\"]}}}";
            var tool = "{\"name\":\"lookup\",\"description\":\"Lookup\",\"parameters\":" + schema + strict + "}";
            var update = Entry("{\"role\":\"system\",\"content\":\"\",\"toolsAdded\":[" + tool + "],\"timestamp\":0}");
            var anchor = Entry("{\"role\":\"system\",\"content\":\"\",\"toolsAdded\":[{\"name\":\"anchor\",\"description\":\"anchor\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}],\"timestamp\":0}");
            ImmutableArray<TranscriptEntry> transcript = inline ? [anchor, User("first"), update, User("second")] : [update, User("first")];
            var options = new AnthropicMessagesRequestOptions(100, SupportsStrictTools: true, CacheRetention: AnthropicCacheRetention.None,
                SupportsMidConversationSystemMessages: inline, SupportsMidConversationToolChanges: inline) { DisablePreferredStrictTools = fallback };
            var body = JsonNode.Parse(new AnthropicMessagesRequestProjector(options).Project(new(Model, transcript)).ToString())!;
            // Inline declaration shape is intentionally inspected recursively, so this also catches any converted inline schema.
            var schemas = FindSchemas(body).Where(s => s["properties"]?["item"] is not null).ToArray();
            Check(schemas.Length > 0, "No declared schema found");
            foreach (var projected in schemas)
            {
                Check(JsonNode.DeepEquals(projected[keyword], JsonNode.Parse(schema)![keyword]), "Root definitions lost: " + body);
                Check(projected["properties"]!["item"]!["$ref"]!.GetValue<string>() == "#/" + keyword + "/Item", "Reference altered");
            }
        }
        var required = Entry("{\"role\":\"system\",\"content\":\"\",\"toolsAdded\":[{\"name\":\"required\",\"description\":\"required\",\"parameters\":{\"type\":\"object\",\"properties\":{},\"$defs\":{}},\"constrainedSampling\":{\"type\":\"json_schema\",\"strict\":\"require\"}}],\"timestamp\":0}");
        try { new AnthropicMessagesRequestProjector(new(100, SupportsStrictTools: true)).Project(new(Model, [required, User("ask")])); }
        catch (AnthropicRequestException error) when (error.Failure == AnthropicRequestFailure.UnsupportedStrictSchema) { return; }
        throw new InvalidOperationException("Required strict schema restriction lost");
    }
    private static IEnumerable<JsonObject> FindSchemas(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["input_schema"] is JsonObject schema) yield return schema;
            foreach (var pair in obj) foreach (var found in FindSchemas(pair.Value)) yield return found;
        }
        else if (node is JsonArray array) foreach (var child in array) foreach (var found in FindSchemas(child)) yield return found;
    }
}
