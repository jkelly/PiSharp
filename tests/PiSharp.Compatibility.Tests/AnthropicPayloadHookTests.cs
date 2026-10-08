using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.Contracts;

internal static class AnthropicPayloadHookTests
{
    private const string Key = "authored-inert-noncredential";
    private static readonly ModelDescriptor Model = new("fixture", "anthropic-messages", "anthropic");
    private static readonly Uri Endpoint = new("https://anthropic.invalid/base");
    private static ChatRequest Request() => new(Model, [new("user", JsonData.Parse("""{"role":"user","content":[{"type":"text","text":"ask"}],"timestamp":123}"""))], 123);
    private static AnthropicMessagesKeyAuthRequestFactory Factory(AnthropicMessagesKeyAuthRequestOptions? options = null) => new(Endpoint, Model,
        new(100, CacheRetention: AnthropicCacheRetention.None, SupportsEagerToolInputStreaming: false), options);

    internal static async Task InspectOnlyOrderAndLegacyBody()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler);
        var factory = Factory(new(MaxTokens: 42));
        using var legacy = factory.Create(Request(), Key);
        var expected = await legacy.Content!.ReadAsStringAsync();
        var seen = 0; var responseSeen = 0;
        var hooks = new AnthropicMessagesHooks(OnResponse: (_, _, _) => { responseSeen++; Check(seen == 1 && handler.Sends == 1, "Payload precedes send/response"); return Task.CompletedTask; })
        {
            OnPayload = (payload, model, token) =>
            {
                seen++; Check(handler.Sends == 0 && model == Model && !token.IsCancellationRequested, "Owned callback context before send");
                Check(payload.Value.GetProperty("max_tokens").GetInt32() == 42, "Override visible before hook");
                Check(!payload.ToString().Contains(Key, StringComparison.Ordinal), "No authentication data in payload");
                return Task.FromResult<JsonData?>(null);
            }
        };
        var frames = await Collect(AnthropicMessagesHttpSseTransport.FromPrepared(client, (r,t) => factory.Prepare(r,Key,t), hooks: hooks));
        Check(frames[^1] is StreamDone && seen == 1 && responseSeen == 1 && handler.Body == expected, "Inspect-only preserves exact body");
        Check(handler.ContentDisposed, "Response content settled before terminal");
        Check(AnthropicSourceEventProjection.WriteTerminal((StreamDone)frames[^1]).ToString() == PiWireJson.WriteSourceEvent(frames[^1]).ToString(), "Done projection remains exact generic source shape");
    }

    internal static async Task ReplaceForceStreamAndBetaExtraction()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler);
        var replacement = JsonData.Parse("""{"model":"fixture","messages":[],"max_tokens":7,"stream":false,"betas":["authored-beta"],"opaque":null}""");
        var before = replacement.ToString(); var factory = Factory(new(MaxTokens: 42));
        var hooks = new AnthropicMessagesHooks { OnPayload = (_,_,_) => Task.FromResult<JsonData?>(replacement) };
        var frames = await Collect(AnthropicMessagesHttpSseTransport.FromPrepared(client, (r,t) => factory.Prepare(r,Key,t), hooks: hooks));
        var body = JsonData.Parse(handler.Body!).Value;
        Check(frames[^1] is StreamDone && body.GetProperty("stream").GetBoolean(), "Replacement stream forced true");
        Check(body.GetProperty("max_tokens").GetInt32() == 7 && !body.TryGetProperty("betas",out _) && body.GetProperty("opaque").ValueKind == JsonValueKind.Null, "Replacement cap survives; opaque null and beta extraction retained");
        Check(handler.Beta == "authored-beta" && handler.Auth == Key && handler.Address == Endpoint.AbsoluteUri + "/v1/messages?beta=true", "Auth/endpoint ownership unchanged");
        Check(replacement.ToString() == before, "Caller-owned replacement unchanged");
    }

    internal static async Task HeldCallbackAndCancellationJoin()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler); using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new TaskCompletionSource<JsonData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = Factory();
        var hooks = new AnthropicMessagesHooks { OnPayload = (_,_,token) => { Check(token == cancellation.Token, "Same token"); entered.SetResult(); return original.Task; } };
        var pending = Collect(AnthropicMessagesHttpSseTransport.FromPrepared(client, (r,t) => factory.Prepare(r,Key,t), hooks: hooks), cancellation.Token);
        await entered.Task;
        Check(!pending.IsCompleted && handler.Sends == 0, "Await actual callback before send/start");
        cancellation.Cancel();
        Check(!pending.IsCompleted, "Cancellation does not abandon callback original");
        original.SetResult(null);
        var frames = await pending;
        Check(original.Task.IsCompletedSuccessfully && handler.Sends == 0 && frames.Single() is StreamError { Reason: StopReason.Aborted }, "Joined callback then sanitized canceled terminal; no send");
    }

    internal static async Task ActualFaultAndCancellationInventory()
    {
        var ordinary = new InvalidOperationException("private callback data");
        var faultedOce = new OperationCanceledException("private faulted cancellation");
        var emptyAggregate = new AggregateException();
        Task<JsonData?>[] originals = [Task.FromException<JsonData?>(ordinary), Task.FromException<JsonData?>(faultedOce),
            Task.FromException<JsonData?>(emptyAggregate), Task.FromCanceled<JsonData?>(new CancellationToken(true))];
        foreach (var original in originals)
        {
            using var handler = new Handler(); using var client = new HttpClient(handler); var factory = Factory();
            var hooks = new AnthropicMessagesHooks { OnPayload = (_,_,_) => original };
            var frames = await Collect(AnthropicMessagesHttpSseTransport.FromPrepared(client, (r,t) => factory.Prepare(r,Key,t), hooks: hooks));
            var terminal = (StreamError)frames.Single();
            Check(ReferenceEquals(terminal.NativeSourceTask, original) && handler.Sends == 0 && original.IsCompleted, "Exact callback original retained and settled");
            if (original.IsFaulted) Check(ReferenceEquals(terminal.NativeSourceException, original.Exception!.InnerExceptions[0]), "Direct fault object identity retained, including empty aggregate/faulted OCE");
            Check(!PiWireJson.WriteEvent(terminal).ToString().Contains("private", StringComparison.Ordinal), "Private fault not projected into diagnostics");
            var nativeBefore = PiWireJson.WriteEvent(terminal).ToString();
            var projected = AnthropicSourceEventProjection.WriteTerminal(terminal).Value.GetProperty("error");
            var generic = PiWireJson.WriteSourceEvent(terminal).Value.GetProperty("error");
            Check(!projected.TryGetProperty("anthropicFailure", out _) && generic.TryGetProperty("anthropicFailure", out _), "HTTP fault/cancel fallback omits only native classification");
            Check(projected.GetProperty("errorMessage").GetString() == generic.GetProperty("errorMessage").GetString(), "HTTP source projection preserves sanitized message");
            Check(PiWireJson.WriteEvent(terminal).ToString() == nativeBefore && ReferenceEquals(terminal.NativeSourceTask, original), "Projection preserves native compact bytes and original Task");
            var neighboring = terminal with { Message = terminal.Message with { ExtraProperties = terminal.Message.ExtraProperties!.Set("neighbor", JsonData.Parse("""{"anthropicFailure":"nested-user-value","value":null}""")) } };
            var neighborSource = AnthropicSourceEventProjection.WriteTerminal(neighboring).Value.GetProperty("error");
            Check(neighborSource.GetProperty("neighbor").GetProperty("anthropicFailure").GetString() == "nested-user-value" && neighborSource.GetProperty("neighbor").GetProperty("value").ValueKind == JsonValueKind.Null, "Nested and neighboring user fields are not filtered");
        }
        using var syncHandler = new Handler(); using var syncClient = new HttpClient(syncHandler); var syncFactory = Factory();
        var cause = new OperationCanceledException("sync-private");
        var syncHooks = new AnthropicMessagesHooks { OnPayload = (_,_,_) => throw cause };
        var sync = (StreamError)(await Collect(AnthropicMessagesHttpSseTransport.FromPrepared(syncClient, (r,t) => syncFactory.Prepare(r,Key,t), hooks: syncHooks))).Single();
        Check(sync.NativeSourceTask is null && sync.NativeSourceException is AnthropicMessagesHookInvocationException wrapper && ReferenceEquals(wrapper.InnerException,cause), "Synchronous throw has no fabricated original Task");
    }

    internal static async Task RejectedReplacementAndPriorAdmission()
    {
        JsonData[] invalid = [JsonData.Null, JsonData.Parse("[]"), JsonData.Parse("""{"max_tokens":1000001}"""),
            JsonData.Parse("""{"number":1e999}"""), JsonData.Parse("""{"betas":["bad\nheader"]}"""),
            JsonData.Parse("{\"large\":" + JsonSerializer.Serialize(new string('x', 5000)) + "}"),
            JsonData.Parse("""{"a":{"b":{"c":{"d":{"e":{"f":0}}}}}}""")];
        foreach (var replacement in invalid)
        {
            using var handler = new Handler(); using var client = new HttpClient(handler); var factory = Factory(new(MaximumPayloadBytes: 4096, MaximumPayloadDepth: 5));
            var hooks = new AnthropicMessagesHooks { OnPayload = (_,_,_) => Task.FromResult<JsonData?>(replacement) };
            Check((await Collect(AnthropicMessagesHttpSseTransport.FromPrepared(client, (r,t) => factory.Prepare(r,Key,t), hooks: hooks))).Single() is StreamError && handler.Sends == 0, "Invalid replacement rejected without send");
        }
        using var priorHandler = new Handler(); using var priorClient = new HttpClient(priorHandler); var calls = 0;
        var priorHooks = new AnthropicMessagesHooks { OnPayload = (_,_,_) => { calls++; return Task.FromResult<JsonData?>(null); } };
        var priorFactory = Factory(new(Headers: JsonData.Parse("""{"x-api-key":null}""")));
        Check((await Collect(AnthropicMessagesHttpSseTransport.FromPrepared(priorClient, (r,t) => priorFactory.Prepare(r,Key,t), hooks: priorHooks))).Single() is StreamError && calls == 0 && priorHandler.Sends == 0, "Prior auth admission fails before hook");
    }

    internal static async Task SimpleAndAuthenticatedForwarding()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler); var calls = 0;
        var hooks = new AnthropicMessagesHooks { OnPayload = (_,_,_) => { calls++; return Task.FromResult<JsonData?>(null); } };
        var metadata = JsonData.Parse("""{"id":"fixture","api":"anthropic-messages","provider":"anthropic","maxTokens":1024,"contextWindow":100000,"reasoning":false}""");
        var options = new AnthropicMessagesSimpleOptions(metadata, new(100)) { ApiKey = Key, Hooks = hooks };
        var simple = new AnthropicMessagesSimpleRequestFactory(client, Endpoint, Model, options);
        var simpleFrames = new List<StreamEvent>(); await foreach (var item in simple.StreamAsync(Request())) simpleFrames.Add(item);
        Check(simpleFrames[^1] is StreamDone && calls == 1, "Simple forwards callback through structured path");
        var resolution = await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(
            new ProviderEnvironmentSnapshot([KeyValuePair.Create<string,string?>("ANTHROPIC_API_KEY",Key)]),
            (_,_) => ValueTask.FromResult<StoredApiKeyCredential?>(null));
        AnthropicMessagesAuthenticatedRequestFactory? authenticated = null;
        await using var lease = await AnthropicInjectedTransportAdapter.AcquireAsync(Model, resolution, (model,binding,_) =>
        {
            authenticated = new(Endpoint, model, new(100), binding);
            var transport = AnthropicMessagesHttpSseTransport.FromPrepared(client, authenticated.Prepare, hooks: hooks);
            return ValueTask.FromResult(new AnthropicTransportAdmission(model, transport, new EmptyOwner()));
        });
        Check(authenticated is not null, "Already-injected authenticated factory bound");
        var authTransport = AnthropicMessagesHttpSseTransport.FromPrepared(client, authenticated!.Prepare, hooks: hooks);
        Check((await Collect(authTransport))[^1] is StreamDone && calls == 2 && handler.Auth == Key, "Authenticated route forwards without exposing secret");
    }

    internal static async Task LegacyMulticastAndPreparationReplay()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler); var factory = Factory();
        var callbacks = 0;
        Func<JsonData,ModelDescriptor,CancellationToken,Task<JsonData?>> callback = (_,_,_) => { callbacks++; return Task.FromResult<JsonData?>(null); };
        Reject(() => new AnthropicMessagesHttpSseTransport(client, (r,t) => factory.Create(r,Key,t), hooks: new() { OnPayload = callback }));
        Reject(() => AnthropicMessagesHttpSseTransport.FromPrepared(client, (r,t) => factory.Prepare(r,Key,t), hooks: new() { OnPayload = callback + callback }));
        var prepared = factory.Prepare(Request(),Key);
        var transport = AnthropicMessagesHttpSseTransport.FromPrepared(client, (_,_) => prepared, hooks: new() { OnPayload = callback });
        Check((await Collect(transport))[^1] is StreamDone, "First preparation consumption succeeds");
        Check((await Collect(transport)).Single() is StreamError && handler.Sends == 1 && callbacks == 1, "Preparation replay invokes neither callback nor second HTTP owner");
        Check(typeof(AnthropicMessagesHooks).GetConstructors().Single().GetParameters().Length == 2 &&
            typeof(AnthropicMessagesHooks).GetMethod("Deconstruct")!.GetParameters().Length == 2, "Hooks original constructor ABI retained");
    }

    private static async Task<List<StreamEvent>> Collect(AnthropicMessagesHttpSseTransport transport, CancellationToken token = default)
    { var frames = new List<StreamEvent>(); await foreach(var item in transport.StreamAsync(Request(),token)) frames.Add(item); return frames; }
    private sealed class EmptyOwner : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Handler : HttpMessageHandler
    {
        internal int Sends; internal string? Body, Beta, Auth, Address; internal bool ContentDisposed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sends++; Body = await request.Content!.ReadAsStringAsync(token); Address = request.RequestUri!.AbsoluteUri;
            Beta = request.Headers.TryGetValues("anthropic-beta",out var beta) ? string.Join(',',beta) : null;
            Auth = request.Headers.TryGetValues("x-api-key",out var auth) ? string.Join(',',auth) : null;
            var stream = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"response\",\"model\":\"fixture\",\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\n" +
                "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}\n\n" +
                "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
            return new(HttpStatusCode.OK) { Content = new Content(stream, () => ContentDisposed = true) };
        }
    }
    private sealed class Content(string value, Action disposed) : StringContent(value, Encoding.UTF8, "text/event-stream")
    { protected override void Dispose(bool disposing) { disposed(); base.Dispose(disposing); } }
    private static void Reject(Action action) { try { action(); } catch(ArgumentException) { return; } throw new InvalidOperationException("Expected admission rejection"); }
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
}
