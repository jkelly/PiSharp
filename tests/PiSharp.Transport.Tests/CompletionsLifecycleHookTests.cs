using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

internal static class CompletionsLifecycleHookTests
{
    private static readonly ModelDescriptor Model = new("hook-model", "openai-completions", "openai");
    private static readonly Uri Endpoint = new("https://owned-hooks.invalid/v1/chat/completions");
    private const string Key = "authored-inert-noncredential";
    private const string Finish = """{"choices":[{"delta":{},"finish_reason":"stop"}]}""";
    private const string Text = """{ "error": -0, "opaque": { "n":1.0,"wide":9007199254740993 }, "choices":[{"delta":{"content":"abc"}}] }""";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("completions-hooks.legacy-constructor-and-options-deconstruct-control", LegacyControls);
        yield return ("completions-hooks.await-response-before-start-and-provider-before-state-mutation", AwaitedOrdering);
        yield return ("completions-hooks.owned-payload-replacement-strict-admission-and-inclusive-byte-budget", PayloadAdmission);
        yield return ("completions-hooks.actual-response-fault-no-start-and-acquisition-fault-after-start", FaultOrdering);
        yield return ("completions-hooks.cancellation-and-callback-fault-join-owned-cleanup", CancellationAndCleanup);
        yield return ("completions-hooks.response-id-own-undefined-null-empty-valid-chronology", ResponseIdPresence);
    }

    private static Task LegacyControls()
    {
        var options = new CompletionsHttpSseOptions();
        var (events, data, total, depth, framing) = options;
        Check(events == 4096 && data == 65_536 && total == 1_048_576 && depth == 32 && framing is null, "Existing positional options changed.");
        using var client = new HttpClient(new FakeHttpHandler((_, _) => throw new InvalidOperationException("Legacy control never sends.")));
        _ = new CompletionsHttpSseTransport(client, (_, _) => new(HttpMethod.Post, Endpoint), new());
        using var request = new CompletionsKeyAuthRequestFactory(Endpoint, Model).Create(Request(), Key);
        return Task.CompletedTask;
    }

    private static async Task AwaitedOrdering()
    {
        var responseEntered = Gate(); var releaseResponse = Gate(); var providerEntered = Gate(); var releaseProvider = Gate(); var startDrained = Gate();
        var trace = new List<string>(); JsonData? payload = null, dto = null; CompletionsResponseObservation? metadata = null;
        ImmutableArray<string> undefined = [];
        var hooks = new CompletionsLifecycleHooks
        {
            OnPayload = (observation, model, _) =>
            { Check(model == Model, "Payload model identity changed."); payload = observation.Value; undefined = observation.OwnUndefinedPaths; trace.Add("payload"); return ValueTask.FromResult<JsonData?>(null); },
            OnResponse = async (observation, model, token) =>
            { Check(model == Model, "Response model identity changed."); metadata = observation; trace.Add("response"); responseEntered.TrySetResult(); await releaseResponse.Task.WaitAsync(token); },
            OnProviderStreamEvent = async (observation, model, token) =>
            {
                Check(model == Model, "Provider model identity changed."); trace.Add("provider");
                if (observation.Value.TryGetProperty("opaque", out _)) { dto = observation; providerEntered.TrySetResult(); await releaseProvider.Task.WaitAsync(token); }
            }
        };
        using var fixture = new Fixture(new ProbeStream(Wire(Text, Finish)), hooks, sent: () => trace.Add("send"));
        await using var run = await new ChatClient(fixture.Transport, capacity: 1).StartAsync(Request());
        var frames = new List<StreamEvent>(); var reading = Drain(run, frames, frame => { if (frame is StreamStarted) startDrained.TrySetResult(); });
        try
        {
            await responseEntered.Task.WaitAsync(Deadline);
            Check(frames.Count == 0 && !run.Completion.IsCompleted && fixture.Content.AcquireCalls == 0, "Start or body acquisition overtook awaited response observation.");
            Check(trace.SequenceEqual(["payload", "send", "response"]), "Actual payload/send/header order changed.");
            var actualMetadata = metadata ?? throw new InvalidOperationException("Actual response hook was not observed.");
            Check(actualMetadata.Status == 200 && actualMetadata.Headers.Value.GetProperty("x-fixture").GetString() == "actual-owned-response", "Hook metadata was not the actual response.");
            Check(undefined.SequenceEqual(["/prompt_cache_key", "/prompt_cache_retention"]), "Source payload own-undefined cache fields were erased.");
            releaseResponse.TrySetResult(); await providerEntered.Task.WaitAsync(Deadline); await startDrained.Task.WaitAsync(Deadline);
            Check(frames.Count == 1 && frames[0] is StreamStarted, "Mapper mutation overtook the awaited DTO callback.");
            var actualDto = dto ?? throw new InvalidOperationException("Actual provider hook was not observed.");
            Check(actualDto.ToString() == Text && actualDto.Value.GetProperty("error").GetRawText() == "-0" &&
                actualDto.Value.GetProperty("opaque").GetProperty("n").GetRawText() == "1.0" &&
                actualDto.Value.GetProperty("opaque").GetProperty("wide").GetRawText() == "9007199254740993", "Raw callback JSON/numeric ownership changed.");
            releaseProvider.TrySetResult(); await reading.WaitAsync(Deadline); var result = await run.Completion;
            Check(result.Failure is null && result.Message.Content.OfType<TextContent>().Single().Text == "abc", "Awaited hooks changed complete native output.");
            Check(payload!.Value.GetProperty("model").GetString() == Model.Id && actualDto.ToString() == Text, "Owned hook payloads changed after source document disposal.");
            Check(fixture.Content.Disposed && !fixture.Handler.Disposed, "Hook composition changed borrowed-client ownership.");
        }
        finally { releaseResponse.TrySetResult(); releaseProvider.TrySetResult(); run.Cancel(); await run.DisposeAsync(); await Observe(reading); }
    }

    private static async Task PayloadAdmission()
    {
        const int limit = 512;
        var entered = Gate(); var release = Gate();
        using (var fixture = new Fixture(new ProbeStream(Wire(Finish)), new() { OnPayload = async (_, _, token) =>
            { entered.TrySetResult(); await release.Task.WaitAsync(token); return null; } }))
        {
            await using var run = await new ChatClient(fixture.Transport).StartAsync(Request()); var frames = new List<StreamEvent>(); var reading = Drain(run, frames);
            try
            {
                await entered.Task.WaitAsync(Deadline);
                Check(fixture.Handler.SendCalls == 0 && frames.Count == 0 && !run.Completion.IsCompleted, "Send/Start overtook awaited payload admission.");
                run.Cancel(); await reading.WaitAsync(Deadline);
                Check((await run.Completion).Failure!.Kind == ChatFailureKind.Cancelled && fixture.Handler.SendCalls == 0 && fixture.Content.AcquireCalls == 0,
                    "Canceled payload callback gained effects or failed settlement.");
            }
            finally { release.TrySetResult(); await run.DisposeAsync(); await Observe(reading); }
        }
        var inclusive = JsonData.Parse("{\"opaque\":\"" + new string('x', limit - 13) + "\"}");
        var original = inclusive.ToString();
        var hooks = new CompletionsLifecycleHooks { OnPayload = (_, _, _) => ValueTask.FromResult<JsonData?>(inclusive) };
        using (var fixture = new Fixture(new ProbeStream(Wire(Finish)), hooks, requestOptions: new(MaximumPayloadBytes: limit)))
        {
            var result = await new ChatClient(fixture.Transport).CompleteAsync(Request());
            var sentBytes = fixture.SentBytes ?? throw new InvalidOperationException("Inclusive admitted payload was not sent.");
            Check(result.Failure is null && sentBytes.Length == limit && new UTF8Encoding(false, true).GetString(sentBytes) == original, "Inclusive payload byte admission changed.");
            Check(inclusive.ToString() == original, "Factory mutated callback-owned raw replacement.");
        }
        var numeric = JsonData.Parse("""{"opaque":{"n":1.0,"wide":9007199254740993,"text":"\u03c0\ud83d\ude00","null":null}}""");
        using (var fixture = new Fixture(new ProbeStream(Wire(Finish)), new() { OnPayload = (_, _, _) => ValueTask.FromResult<JsonData?>(numeric) }))
        {
            var before = numeric.ToString(); Check((await new ChatClient(fixture.Transport).CompleteAsync(Request())).Failure is null, "Admitted owned replacement failed.");
            using var body = JsonDocument.Parse(fixture.SentBytes!); var opaque = body.RootElement.GetProperty("opaque");
            Check(opaque.GetProperty("n").GetRawText() == "1" && opaque.GetProperty("wide").GetRawText() == "9007199254740992" &&
                opaque.GetProperty("text").GetString() == "\u03c0\U0001f600" && opaque.GetProperty("null").ValueKind == JsonValueKind.Null && !opaque.TryGetProperty("missing", out _), "Actual production ECMAScript payload serialization changed.");
            Check(before == numeric.ToString(), "Serialized payload replaced original numeric tokens.");
        }
        using var permissive = JsonDocument.Parse("{ /* retained */ \"opaque\":1, }", new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var bad = new[] { JsonData.FromElement(permissive.RootElement), JsonData.Null, JsonData.Parse("{\"n\":1e999}"),
            JsonData.Parse("{\"text\":\"\\uD800\"}"), JsonData.Parse("{\"opaque\":\"" + new string('x', limit - 12) + "\"}"), JsonData.Parse("{\"a\":{\"b\":{\"c\":{\"d\":{}}}}}") };
        foreach (var replacement in bad)
        {
            using var fixture = new Fixture(new ProbeStream(Wire(Finish)), new() { OnPayload = (_, _, _) => ValueTask.FromResult<JsonData?>(replacement) },
                requestOptions: new(MaximumPayloadBytes: limit, MaximumPayloadDepth: 4));
            var result = await new ChatClient(fixture.Transport).CompleteAsync(Request());
            Check(result.Failure is not null && fixture.Handler.SendCalls == 0 && fixture.Content.AcquireCalls == 0, "Malformed/nonfinite/Unicode/depth/over-budget replacement gained effects.");
        }
        using (var fixture = new Fixture(new ProbeStream(Wire(Finish)), new() { OnPayload = (_, _, _) =>
            ValueTask.FromResult<JsonData?>(JsonData.Parse("{\"x\":1,\"\\u0078\":2}")) }))
            Check((await new ChatClient(fixture.Transport).CompleteAsync(Request())).Failure is not null && fixture.Handler.SendCalls == 0, "Decoded duplicate callback keys gained send authority.");
    }

    private static async Task FaultOrdering()
    {
        foreach (var stage in new[] { "response", "acquire" })
        {
            var hooks = new CompletionsLifecycleHooks { OnResponse = (_, _, _) => stage == "response" ? throw new IOException("private-hook-marker") : ValueTask.CompletedTask };
            using var fixture = new Fixture(new ProbeStream(Wire(Text, Finish)), hooks, failAcquire: stage == "acquire");
            var frames = await Collect(fixture.Transport.StreamAsync(Request())); var end = (StreamError)frames[^1];
            Check(frames.OfType<StreamStarted>().Count() == (stage == "response" ? 0 : 1), "Response/acquisition fault Start ordering changed.");
            Check(fixture.Content.AcquireCalls == (stage == "response" ? 0 : 1) && fixture.Content.Disposed && !fixture.Handler.Disposed, "Fault skipped actual response ownership.");
            Check(!PiWireJson.WriteMessage(end.Message).ToString().Contains("private-hook-marker", StringComparison.Ordinal), "Callback/acquisition exception leaked into native message.");
            await ThrowsDisposed(fixture.OwnedRequest!);
        }
        var calls = 0;
        using (var fixture = new Fixture(new ProbeStream(Wire(Finish)), new() { OnResponse = (_, _, _) => { calls++; return ValueTask.CompletedTask; } },
            httpOptions: new() { MaximumResponseHeaderCharacters = 4 }))
            Check((await new ChatClient(fixture.Transport).CompleteAsync(Request())).Failure is not null && calls == 0 && fixture.Content.AcquireCalls == 0 && fixture.Content.Disposed,
                "Oversized actual response metadata reached hook/body acquisition.");
        var constructed = 0;
        using var client = new HttpClient(new FakeHttpHandler((_, _) => throw new InvalidOperationException("Bounded Start admission must precede send.")));
        var limited = CompletionsHttpSseTransport.FromAsyncRequestFactory(client, (_, _) =>
        { constructed++; return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, Endpoint)); },
            completionsOptions: CompletionsSourceEventProjection.CaptureOwnedSnapshots(new(MaximumContentCharacters: 64)));
        var rejected = await Collect(limited.StreamAsync(Request()));
        Check(constructed == 0 && rejected.Count == 1 && rejected[0] is StreamError &&
            ((StreamError)rejected[0]).NativeDiagnostic?.Code == NativeChatFailureCode.ResourceLimit,
            "Bounded owned Start-copy admission gained request/send effects or fabricated Start.");
    }

    private static async Task CancellationAndCleanup()
    {
        var entered = Gate(); var release = Gate();
        using (var fixture = new Fixture(new ProbeStream(Wire(Finish)), new() { OnResponse = async (_, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); } }))
        {
            await using var run = await new ChatClient(fixture.Transport).StartAsync(Request()); var frames = new List<StreamEvent>(); var reading = Drain(run, frames);
            try
            {
                await entered.Task.WaitAsync(Deadline); var first = run.DisposeAsync().AsTask(); var second = run.DisposeAsync().AsTask();
                await Task.WhenAll(first, second, reading).WaitAsync(Deadline);
                Check((await run.Completion).Failure!.Kind == ChatFailureKind.Cancelled && !frames.OfType<StreamStarted>().Any() && fixture.Content.AcquireCalls == 0 && fixture.Content.Disposed,
                    "Cancellation during header callback failed to join its owned response without Start.");
            }
            finally { release.TrySetResult(); await run.DisposeAsync(); await Observe(reading); }
        }
        var body = new CleanupGateBody(Wire(Text, Finish));
        using (var fixture = new Fixture(body, new() { OnProviderStreamEvent = (_, _, _) => throw new IOException("private-hook-marker") }))
        {
            await using var run = await new ChatClient(fixture.Transport).StartAsync(Request()); var frames = new List<StreamEvent>(); var reading = Drain(run, frames);
            try
            {
                await body.Entered.Task.WaitAsync(Deadline);
                Check(!run.Completion.IsCompleted && !frames.OfType<StreamTerminalEvent>().Any() && !fixture.Content.Disposed, "Callback fault published a terminal/result before actual cleanup joined.");
                body.Release.TrySetResult(); await reading.WaitAsync(Deadline); var result = await run.Completion;
                Check(result.Failure is not null && body.AsyncCloses == 1 && !body.SyncBeforeAsync && fixture.Content.Disposed,
                    "Callback fault changed cleanup ordering or skipped joined disposal.");
                Check(!PiWireJson.WriteMessage(result.Message).ToString().Contains("private-hook-marker", StringComparison.Ordinal), "Private callback fault escaped native sanitation.");
            }
            finally { body.Release.TrySetResult(); run.Cancel(); await run.DisposeAsync(); await Observe(reading); }
        }
    }

    private static async Task ResponseIdPresence()
    {
        var chunks = new[] { "{\"choices\":[{\"delta\":{\"content\":\"A\"}}]}", "{\"id\":null,\"choices\":[{\"delta\":{\"content\":\"B\"}}]}",
            "{\"id\":\"\",\"choices\":[{\"delta\":{\"content\":\"C\"}}]}", "{\"id\":\"first\",\"choices\":[{\"delta\":{\"content\":\"D\"}}]}",
            "{\"id\":\"later\",\"choices\":[{\"delta\":{\"content\":\"E\"}}]}", Finish };
        var frames = await Collect(new OpenAICompletionsWireSource((_, token) => Chunks(chunks, token), CompletionsSourceEventProjection.CaptureOwnedSnapshots()).StreamAsync(Request()));
        Check(CompletionsSourceEventProjection.ReadEmission(frames[0])!.Value.GetProperty("ownUndefinedPaths").GetArrayLength() == 0, "Initial Start fabricated an undefined responseId.");
        var deltas = frames.OfType<TextDelta>().ToArray(); Check(deltas.Length == 5, "Actual response-id chronology lost a delta.");
        var views = deltas.Select(frame => CompletionsSourceEventProjection.ReadEmission(frame)!.Value).ToArray();
        Check(views[0].GetProperty("ownUndefinedPaths")[0].GetString() == "/partial/responseId" && !views[0].GetProperty("value").GetProperty("partial").TryGetProperty("responseId", out _), "Missing ID was not owned undefined.");
        Check(views[1].GetProperty("value").GetProperty("partial").GetProperty("responseId").ValueKind == JsonValueKind.Null && views[1].GetProperty("ownUndefinedPaths").GetArrayLength() == 0, "Explicit null ID was replaced by missing.");
        Check(views[2].GetProperty("value").GetProperty("partial").GetProperty("responseId").GetString() == "" && views[3].GetProperty("value").GetProperty("partial").GetProperty("responseId").GetString() == "first" &&
            views[4].GetProperty("value").GetProperty("partial").GetProperty("responseId").GetString() == "first", "Falsy ID replacement or first truthy ID retention changed.");
        var terminal = (StreamDone)frames[^1]; Check(CompletionsSourceEventProjection.ReadFinalObservation(terminal)!.Value.GetProperty("ownUndefinedPaths").GetArrayLength() == 0, "Valid final fabricated undefined presence.");
        var missing = await Collect(new OpenAICompletionsWireSource((_, token) => Chunks([chunks[0], Finish], token), CompletionsSourceEventProjection.CaptureOwnedSnapshots()).StreamAsync(Request()));
        var last = (StreamDone)missing[^1]; var before = PiWireJson.WriteMessage(last.Message).ToString(); var final = CompletionsSourceEventProjection.ReadFinalObservation(last)!;
        Check(final.Value.GetProperty("ownUndefinedPaths")[0].GetString() == "/responseId" && !final.Value.GetProperty("value").TryGetProperty("responseId", out _), "Final source presence was silently dropped or changed to null.");
        Check(PiWireJson.WriteMessage(last.Message).ToString() == before && !CompletionsSourceEventProjection.ReadEmission(missing[0])!.Value.GetProperty("value").GetProperty("partial").TryGetProperty("responseId", out _), "Later presence tracking mutated canonical/earlier ownership.");
        var rejected = false; try { _ = CompletionsSourceEventProjection.ReadFinalObservation(last, 1); } catch (StreamLimitException) { rejected = true; }
        Check(rejected, "Final source view ignored its own output bound.");
        var retained = CompletionsSourceEventProjection.ReadEmission(last)!.ToString();
        using var permissive = JsonDocument.Parse("{ /* retained root syntax */" + retained[1..], new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        rejected = false;
        try { _ = CompletionsSourceEventProjection.ReadFinalObservation(last with { SourceEmissionSnapshot = JsonData.FromElement(permissive.RootElement) }); }
        catch (JsonException) { rejected = true; }
        Check(rejected, "Public final extraction erased malformed retained root syntax.");
    }

    private sealed class Fixture : IDisposable
    {
        public FakeHttpHandler Handler { get; }
        public HttpClient Client { get; }
        public StreamProbeContent Content { get; }
        public CompletionsHttpSseTransport Transport { get; }
        public HttpRequestMessage? OwnedRequest;
        public byte[]? SentBytes;
        private readonly Stream _body;
        public Fixture(Stream body, CompletionsLifecycleHooks hooks, bool failAcquire = false, CompletionsKeyAuthRequestOptions? requestOptions = null,
            CompletionsHttpSseOptions? httpOptions = null, Action? sent = null)
        {
            _body = body; Content = new(body, token => failAcquire ? throw new IOException("private-hook-marker") : Task.FromResult(body));
            Handler = new(async (message, token) =>
            {
                SentBytes = await message.Content!.ReadAsByteArrayAsync(token); sent?.Invoke();
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Content };
                Check(response.Headers.TryAddWithoutValidation("x-fixture", "actual-owned-response"), "Actual owned fixture header could not be added."); return response;
            });
            Client = new(Handler); var factory = new CompletionsKeyAuthRequestFactory(Endpoint, Model, options: requestOptions);
            Transport = CompletionsHttpSseTransport.FromAsyncRequestFactory(Client, async (request, token) =>
                OwnedRequest = await factory.CreateAsync(request, Key, hooks, token), (httpOptions ?? new()) with { Hooks = hooks }, CompletionsSourceEventProjection.CaptureOwnedSnapshots());
        }
        public void Dispose() { Client.Dispose(); Content.Dispose(); _body.Dispose(); }
    }

    private sealed class CleanupGateBody(byte[] bytes) : ProbeStream(bytes)
    {
        public TaskCompletionSource Entered { get; } = Gate();
        public TaskCompletionSource Release { get; } = Gate();
        public int AsyncCloses; public bool SyncBeforeAsync;
        public override async ValueTask DisposeAsync() { AsyncCloses++; Entered.TrySetResult(); await Release.Task; Disposed = true; }
        protected override void Dispose(bool disposing) { if (disposing) SyncBeforeAsync |= !Disposed; base.Dispose(disposing); }
    }
    private static ChatRequest Request() => new(Model, [new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"owned\",\"timestamp\":1}"))], 1700000000000);
    private static byte[] Wire(params string[] chunks) => Encoding.UTF8.GetBytes(string.Concat(chunks.Select(chunk => "data: " + chunk + "\n\n")) + "data: [DONE]\n\n");
    private static async IAsyncEnumerable<JsonData> Chunks(string[] chunks, [EnumeratorCancellation] CancellationToken token)
    { await Task.CompletedTask; foreach (var chunk in chunks) { token.ThrowIfCancellationRequested(); yield return JsonData.Parse(chunk); } }
    private static async Task<List<StreamEvent>> Collect(IAsyncEnumerable<StreamEvent> source) { var frames = new List<StreamEvent>(); await foreach (var frame in source) frames.Add(frame); return frames; }
    private static async Task Drain(ChatRun run, List<StreamEvent> frames, Action<StreamEvent>? observe = null)
    { await foreach (var frame in run.ReadEventsAsync()) { frames.Add(frame); observe?.Invoke(frame); } }
    private static async Task ThrowsDisposed(HttpRequestMessage request)
    { var rejected = false; try { _ = await request.Content!.ReadAsStringAsync(); } catch (ObjectDisposedException) { rejected = true; } Check(rejected, "Owned request was retained after hook failure."); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Observe(Task task) { try { await task; } catch { } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
