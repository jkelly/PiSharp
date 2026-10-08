using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

// Literal, static-source-informed expectations. AUTHORED_UNCOMPILED_UNEXECUTED.
internal static class ResponsesReasoningTests
{
    private static readonly Uri Endpoint = new("https://synthetic.invalid/v1/responses");
    private static readonly ModelDescriptor Model = new("reasoning-model", "openai-responses", "openai");
    private const string Key = "inert-reasoning-test-key";
    private const string Start = """{"type":"response.output_item.added","output_index":3,"item":{"type":"reasoning","id":"rs_1","summary":[]}}""";
    private const string Delta = """{"type":"response.reasoning_summary_text.delta","output_index":3,"item_id":"rs_1","delta":"partial"}""";
    private const string Item = """{"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"authoritative"}],"opaque":{"keep":null}}""";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static ChatRequest Request(ModelDescriptor? model = null) => new(model ?? Model,
        [new("user", JsonData.Parse("""{"role":"user","content":"hello","timestamp":1}"""))], 1);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("responses-reasoning.request-controls-and-provider-branches", RequestControls);
        yield return ("responses-reasoning.request-admission-and-payload-bounds", RequestAdmission);
        yield return ("responses-reasoning.stream-final-text-selection-and-signatures", FinalTextAndSignatures);
        yield return ("responses-reasoning.encrypted-backfill-and-two-request-replay", BackfillAndReplay);
        yield return ("responses-reasoning.interleaved-slots-and-missing-added-event", Interleaving);
        yield return ("responses-reasoning.bounds-malformed-and-cancellation-cleanup", FailureAndCleanup);
        yield return ("responses-reasoning.final-encryption-ends-before-terminal", EndTiming);
        yield return ("responses-reasoning.preterminal-interruption-retains-done-state", PreterminalFailure);
        yield return ("responses-reasoning.pending-signature-limits-and-cleanup", PendingSignatureLimits);
    }
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Same(string expected, JsonElement actual) => Check(JsonElement.DeepEquals(JsonData.Parse(expected).Value, actual), "JSON differs: " + actual);
    private static string Done(string item = Item, int index = 3) => "{\"type\":\"response.output_item.done\",\"output_index\":" + index + ",\"item\":" + item + "}";
    private static string Completed(string output = "[]") => "{\"type\":\"response.completed\",\"response\":{\"id\":\"response_1\",\"status\":\"completed\",\"output\":" + output + ",\"usage\":{\"input_tokens\":5,\"output_tokens\":7,\"total_tokens\":12,\"output_tokens_details\":{\"reasoning_tokens\":4}}}}";
    private static byte[] Wire(params string[] events) => Encoding.UTF8.GetBytes(string.Concat(events.Select(e => "data: " + e + "\n\n")));
    private static ResponsesKeyAuthRequestFactory Factory(ResponsesKeyAuthRequestOptions? options = null, ModelDescriptor? model = null, bool reasoning = true) =>
        new(Endpoint, model ?? Model, new(reasoning), options);
    private static async Task<JsonData> Payload(ResponsesKeyAuthRequestOptions options, ModelDescriptor? model = null, bool reasoning = true)
    {
        using var request = Factory(options, model, reasoning).Create(Request(model), Key);
        return JsonData.Parse(await request.Content!.ReadAsStringAsync());
    }
    private static async Task RequestControls()
    {
        var vectors = new (string Provider, bool Reasoning, ResponsesKeyAuthRequestOptions Options, string? Expected, bool Include)[]
        {
            ("openai", true, new(), """{"effort":"none"}""", false),
            ("openai", true, new(ReasoningEffort: "high"), """{"effort":"high","summary":"auto"}""", true),
            ("openai", true, new(ReasoningSummary: "concise"), """{"effort":"medium","summary":"concise"}""", true),
            ("openai", true, new(ReasoningSummary: "detailed", ThinkingLevelMap: JsonData.Parse("""{"medium":"high"}""")), """{"effort":"medium","summary":"detailed"}""", true),
            ("openai", true, new(ThinkingLevelMap: JsonData.Parse("""{"off":""}""")), """{"effort":""}""", false),
            ("openai", true, new(ReasoningEffort: "low", ReasoningSummary: "detailed", ThinkingLevelMap: JsonData.Parse("""{"low":"LOW"}""")), """{"effort":"LOW","summary":"detailed"}""", true),
            ("openai", true, new(ReasoningEffort: "high", ThinkingLevelMap: JsonData.Parse("""{"high":null}""")), """{"effort":"high","summary":"auto"}""", true),
            ("openai", true, new(ThinkingLevelMap: JsonData.Parse("""{"off":null}""")), null, false),
            ("openai", true, new(ThinkingLevelMap: JsonData.Parse("""{"off":"minimal"}""")), """{"effort":"minimal"}""", false),
            ("github-copilot", true, new(), null, false),
            ("github-copilot", true, new(ReasoningEffort: "medium"), """{"effort":"medium","summary":"auto"}""", true),
            ("xai", true, new(ThinkingLevelMap: JsonData.Parse("""{"off":null}""")), null, true),
            ("xai", true, new(), """{"effort":"none"}""", true),
            ("openai", false, new(ReasoningEffort: "high", ReasoningSummary: "auto"), null, false),
            ("xai", false, new(), null, false),
            ("openai", true, new(ThinkingLevelMap: JsonData.Parse("null")), """{"effort":"none"}""", false),
            ("openai", true, new(ReasoningEffort: "max", ThinkingLevelMap: JsonData.Parse("""{"max":""}""")), """{"effort":"","summary":"auto"}""", true)
        };
        foreach (var vector in vectors)
        {
            var model = Model with { Provider = vector.Provider };
            var mapBefore = vector.Options.ThinkingLevelMap?.ToString();
            var payload = (await Payload(vector.Options, model, vector.Reasoning)).Value;
            Check(mapBefore == vector.Options.ThinkingLevelMap?.ToString(), "Caller map mutated");
            Check(payload.TryGetProperty("reasoning", out var control) == (vector.Expected is not null), "Reasoning presence");
            if (vector.Expected is not null) Same(vector.Expected, control);
            Check(payload.TryGetProperty("include", out var include) == vector.Include, "Include presence");
            if (vector.Include) Same("""["reasoning.encrypted_content"]""", include);
            Check(!payload.GetProperty("store").GetBoolean() && payload.GetProperty("stream").GetBoolean(), "Envelope flags");
        }
        foreach (var effort in new[] { "minimal", "low", "medium", "high", "xhigh", "max" })
            Same("{\"effort\":\"" + effort + "\",\"summary\":\"auto\"}", (await Payload(new(ReasoningEffort: effort))).Value.GetProperty("reasoning"));
    }
    private static async Task RequestAdmission()
    {
        foreach (var options in new ResponsesKeyAuthRequestOptions[]
        {
            new(ReasoningEffort: "off"), new(ReasoningSummary: "invalid"),
            new(ThinkingLevelMap: JsonData.Parse("[]")), new(ThinkingLevelMap: JsonData.Parse("""{"high":1}""")),
            new(ThinkingLevelMap: JsonData.Parse("""{"unknown":"high"}"""))
        })
        {
            try { _ = Factory(options); throw new InvalidOperationException("Invalid options accepted"); }
            catch (ResponsesKeyAuthRequestException error) { Check(error.Failure == ResponsesKeyAuthRequestFailure.UnsupportedOptions, "Admission category"); }
        }
        var optionsWithReasoning = new ResponsesKeyAuthRequestOptions(ReasoningEffort: "high");
        using var baseline = Factory(optionsWithReasoning).Create(Request(), Key);
        var bytes = (await baseline.Content!.ReadAsByteArrayAsync()).Length;
        using var boundary = Factory(optionsWithReasoning with { MaximumPayloadBytes = bytes }).Create(Request(), Key);
        try { using var rejected = Factory(optionsWithReasoning with { MaximumPayloadBytes = bytes - 1 }).Create(Request(), Key); throw new InvalidOperationException("Payload budget ignored"); }
        catch (ResponsesKeyAuthRequestException error) { Check(error.Failure == ResponsesKeyAuthRequestFailure.ResourceLimit, "Budget category"); }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { using var rejected = Factory().Create(Request(), Key, cancel.Token); throw new InvalidOperationException("Precancel accepted"); }
        catch (OperationCanceledException) { }
    }
    private static async Task FinalTextAndSignatures()
    {
        foreach (var (item, expected) in new[]
        {
            (Item, "authoritative"),
            ("""{"type":"reasoning","id":"rs_1","summary":[],"content":[{"type":"reasoning_text","text":"raw one"},{"type":"reasoning_text","text":"raw two"}],"encrypted_content":"cipher"}""", "raw one\n\nraw two"),
            ("""{"type":"reasoning","id":"rs_1","summary":[],"content":[]}""", "partial\n\nraw"),
            ("""{"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"one"},{"type":"summary_text","text":"two"}],"content":[{"type":"reasoning_text","text":"ignored"}]}""", "one\n\ntwo")
        })
        {
            using var fixture = new Fixture(Wire(Start, Delta,
                """{"type":"response.reasoning_summary_part.done","output_index":3,"item_id":"rs_1"}""",
                """{"type":"response.reasoning_text.delta","output_index":3,"item_id":"rs_1","delta":"raw"}""", Done(item), Completed()));
            var result = await fixture.Complete();
            Check(result.Failure is null && result.Message.StopReason == StopReason.Stop, "Reasoning stream failed");
            var thinking = (ThinkingContent)result.Message.Content.Single();
            Check(thinking.Thinking == expected, "Final text precedence");
            Same(item, JsonData.Parse(thinking.ExtraProperties!.Values["thinkingSignature"].Value.GetString()!).Value);
            Check(result.Message.Usage.ExtraProperties!.Values["reasoning"].Value.GetInt32() == 4, "Reasoning usage");
        }
    }
    private static async Task BackfillAndReplay()
    {
        foreach (var existing in new string?[] { null, "", "original-cipher" })
        {
            var node = JsonNode.Parse(Item)!.AsObject();
            if (existing is not null) node["encrypted_content"] = existing;
            var item = node.ToJsonString();
            var terminal = """[{"type":"reasoning","id":"rs_1","encrypted_content":"terminal-cipher","summary":[{"text":"must not replace"}]},{"type":"reasoning","id":"unknown","encrypted_content":"unrelated"}]""";
            using var fixture = new Fixture(Wire(Start, Delta, Done(item), Completed(terminal)));
            var result = await fixture.Complete();
            Check(result.Failure is null, "Backfill failed");
            var block = (ThinkingContent)result.Message.Content.Single();
            var signature = JsonData.Parse(block.ExtraProperties!.Values["thinkingSignature"].Value.GetString()!);
            Check(signature.Value.GetProperty("encrypted_content").GetString() == (string.IsNullOrEmpty(existing) ? "terminal-cipher" : existing), "Backfill overwrite");
            Check(block.Thinking == "authoritative" && signature.Value.GetProperty("opaque").GetProperty("keep").ValueKind == JsonValueKind.Null, "Backfill changed opaque item or text");
            var next = Request() with { Messages = [.. Request().Messages, new("assistant", PiWireJson.WriteMessage(result.Message)), new("user", JsonData.Parse("""{"role":"user","content":"continue","timestamp":2}"""))] };
            using var replay = Factory(new(ReasoningEffort: "high")).Create(next, Key);
            var replayBody = JsonData.Parse(await replay.Content!.ReadAsStringAsync()).Value;
            var replayItem = replayBody.GetProperty("input").EnumerateArray().Single(e => e.TryGetProperty("type", out var type) && type.GetString() == "reasoning");
            Check(JsonElement.DeepEquals(signature.Value, replayItem), "Encrypted reasoning not replayed intact");
            var sends = 0;
            using var replayHandler = new Handler(async http =>
            {
                sends++;
                var body = JsonData.Parse(await http.Content!.ReadAsStringAsync()).Value;
                Check(JsonElement.DeepEquals(replayBody, body), "Actual replay transport body changed");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Wire(Done("""{"type":"message","id":"msg_2","content":[{"type":"output_text","text":"continued"}]}"""), Completed()))
                };
            });
            using var replayClient = new HttpClient(replayHandler);
            var replayFactory = Factory(new(ReasoningEffort: "high"));
            var second = await new ChatClient(new ResponsesHttpSseTransport(replayClient, request => replayFactory.Create(request, Key))).CompleteAsync(next);
            Check(sends == 1 && second.Failure is null && ((TextContent)second.Message.Content.Single()).Text == "continued", "Second production request failed");
            var foreign = Model with { Id = "other-model" };
            using var foreignRequest = Factory(new(ReasoningEffort: "high"), foreign).Create(next with { Model = foreign }, Key);
            var foreignBody = JsonData.Parse(await foreignRequest.Content!.ReadAsStringAsync()).Value;
            Check(!foreignBody.GetProperty("input").EnumerateArray().Any(e => e.TryGetProperty("type", out var type) && type.GetString() == "reasoning"), "Opaque reasoning crossed model identity");
        }
    }
    private static async Task Interleaving()
    {
        var other = """{"type":"reasoning","id":"rs_2","summary":[],"encrypted_content":"second"}""";
        var text = """{"type":"message","id":"msg_1","content":[{"type":"output_text","text":"answer"}]}""";
        using var fixture = new Fixture(Wire(Start,
            """{"type":"response.output_item.added","output_index":8,"item":{"type":"message","id":"msg_1","content":[]}}""",
            Delta, Done(text, 8), Done(other, 11), Done(),
            Done("""{"type":"function_call","id":"fc_1","call_id":"call_1","name":"inspect","arguments":"{\"value\":1}"}""", 14), Completed()));
        var frames = new List<StreamEvent>();
        await foreach (var frame in fixture.Transport.StreamAsync(Request())) frames.Add(frame);
        var final = frames.OfType<StreamDone>().Single();
        Check(final.Message.Content.Length == 4 && final.Reason == StopReason.ToolUse, "Interleaved block count/tool reason");
        Check(((ToolCallContent)final.Message.Content[3]).Arguments.Value.GetProperty("value").GetInt32() == 1, "Tool completion with reasoning");
        Check(((ThinkingContent)final.Message.Content[0]).Thinking == "authoritative", "First slot");
        Check(((TextContent)final.Message.Content[1]).Text == "answer", "Text slot");
        Check(((ThinkingContent)final.Message.Content[2]).Thinking == "", "Done-only encrypted reasoning");
        Check(frames.OfType<ThinkingStarted>().Count() == 2 && frames.OfType<ThinkingEnded>().Count() == 2, "Unbalanced thinking");
        Check(frames.IndexOf(frames.OfType<TextEnded>().Single()) < frames.IndexOf(frames.OfType<ThinkingEnded>().First()), "Native finalization timing");
    }
    private static async Task FailureAndCleanup()
    {
        foreach (var events in new[]
        {
            new[] { Start, Delta, Completed() },
            new[] { Start, """{"type":"response.reasoning_text.delta","output_index":3,"item_id":"wrong","delta":"x"}""" },
            new[] { Start, Done("""{"type":"reasoning","id":"rs_1","summary":false}""") },
            new[] { Start, Done(), Delta, Completed() }
        })
        {
            using var fixture = new Fixture(Wire(events));
            var result = await fixture.Complete();
            Check(result.Failure?.Kind == ChatFailureKind.MalformedStream, "Malformed reasoning accepted");
        }
        using (var limited = new Fixture(Wire(Start, Done(Item.Replace("authoritative", new string('x', 1000))), Completed()), new(MaximumContentCharacters: 512)))
            Check((await limited.Complete()).Failure?.Kind == ChatFailureKind.ResourceLimit, "Reasoning end escaped content bound");
        var largeSignature = JsonNode.Parse(Item)!.AsObject();
        largeSignature["encrypted_content"] = new string('s', 1000);
        using (var limited = new Fixture(Wire(Start, Done(largeSignature.ToJsonString()), Completed()), new(MaximumContentCharacters: 512)))
            Check((await limited.Complete()).Failure?.Kind == ChatFailureKind.ResourceLimit, "Reasoning signature escaped content bound");
        foreach (var cancelAtCleanup in new[] { false, true })
        {
            var body = new HttpCleanupGateStream(Wire(Start, Delta, Done(), Completed()));
            using var fixture = new Fixture(body);
            using var cancellation = new CancellationTokenSource();
            var run = fixture.Complete(cancellation.Token);
            try
            {
                await body.CleanupEntered.Task.WaitAsync(Deadline);
                if (cancelAtCleanup) cancellation.Cancel();
                Check(!run.IsCompleted && !fixture.Handler.Disposed, "Completion escaped original cleanup");
                body.ReleaseCleanup.TrySetResult();
                var result = await run;
                Check(result.Message.StopReason == (cancelAtCleanup ? StopReason.Aborted : StopReason.Stop), "Cleanup terminal reason");
                Check(body.AsyncDisposeCalls == 1 && body.Disposed && !fixture.Handler.Disposed, "Cleanup ownership");
            }
            finally { body.ReleaseCleanup.TrySetResult(); await run; }
        }
    }
    private static async Task PendingSignatureLimits()
    {
        var item = JsonNode.Parse(Item)!.AsObject();
        var opaque = new JsonObject();
        for (var index = 0; index < 4097; index++) opaque["p" + index] = false;
        item["opaque"] = opaque;
        var done = Done(item.ToJsonString());
        Check(done.Length < 65_536, "Fixture must pass DTO character admission");
        foreach (var ending in new[] { "eof", "read", "completed" })
        {
            var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanupRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleaned = false;
            async IAsyncEnumerable<JsonData> Source()
            {
                try
                {
                    yield return JsonData.Parse(Start);
                    yield return JsonData.Parse(done);
                    if (ending == "read") throw new IOException("inert source failure");
                    if (ending == "completed") yield return JsonData.Parse(Completed());
                }
                finally
                {
                    cleanupEntered.TrySetResult();
                    await cleanupRelease.Task;
                    cleaned = true;
                }
            }
            var client = new ChatClient(new ResponsesTextToolTransport((_, _) => Source()), capacity: 1);
            var task = client.CompleteAsync(Request());
            try
            {
                await cleanupEntered.Task.WaitAsync(Deadline);
                Check(!task.IsCompleted && !cleaned, "Signature failure bypassed original cleanup");
                cleanupRelease.TrySetResult();
                var result = await task;
                Check(cleaned && result.Failure?.Kind == ChatFailureKind.ResourceLimit, "Pending signature limit misclassified: " + ending);
                Check(result.Message.StopReason == StopReason.Error, "Oversized signature succeeded");
                Check(result.Failure!.Message == "Responses text/tool stream exceeds configured limits.", "Signature limit diagnostic changed or exposed data");
            }
            finally { cleanupRelease.TrySetResult(); await task; }
        }
    }
    private static async Task PreterminalFailure()
    {
        foreach (var failure in new[] { "eof", "read", "malformed", "cancel" })
        foreach (var (includeEncryption, encryption) in new (bool, string?)[] { (false, null), (true, null), (true, ""), (true, "final-cipher") })
        foreach (var doneOnly in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposed = false;
            var node = JsonNode.Parse(Item)!.AsObject();
            if (includeEncryption) node["encrypted_content"] = encryption;
            var item = node.ToJsonString();
            async IAsyncEnumerable<JsonData> Source([EnumeratorCancellation] CancellationToken token = default)
            {
                try
                {
                    if (!doneOnly) { yield return JsonData.Parse(Start); yield return JsonData.Parse(Delta); }
                    yield return JsonData.Parse(Done(item));
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                    if (failure == "read") throw new IOException("inert preterminal failure");
                    if (failure == "malformed") yield return JsonData.Parse("""{"type":"response.failed"}""");
                }
                finally { disposed = true; }
            }
            var client = new ChatClient(new ResponsesTextToolTransport((_, token) => Source(token)), capacity: 1);
            var task = client.CompleteAsync(Request(), cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(Deadline);
                Check(!task.IsCompleted, "Preterminal gate did not hold");
                if (failure == "cancel") cancellation.Cancel();
                else release.TrySetResult();
                var result = await task;
                Check(disposed, "Preterminal failure escaped source cleanup");
                Check(result.Failure?.Kind == (failure == "cancel" ? ChatFailureKind.Cancelled :
                    failure == "read" ? ChatFailureKind.Provider : ChatFailureKind.MalformedStream), "Failure classification changed");
                Check(result.Message.StopReason == (failure == "cancel" ? StopReason.Aborted : StopReason.Error), "Failure became success");
                var thinking = (ThinkingContent)result.Message.Content.Single();
                Check(thinking.Thinking == "authoritative", "Done-state text lost");
                var signature = thinking.ExtraProperties!.Values["thinkingSignature"].Value.GetString()!;
                Same(item, JsonData.Parse(signature).Value);
            }
            finally { release.TrySetResult(); await task; }
        }
    }
    private static async Task EndTiming()
    {
        foreach (var (includeField, encryption) in new (bool, string?)[] { (false, null), (true, null), (true, ""), (true, "final-cipher") })
        {
            var node = JsonNode.Parse(Item)!.AsObject();
            if (includeField) node["encrypted_content"] = encryption;
            var item = node.ToJsonString();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async IAsyncEnumerable<JsonData> Source([EnumeratorCancellation] CancellationToken token = default)
            {
                yield return JsonData.Parse(Start);
                yield return JsonData.Parse(Delta);
                yield return JsonData.Parse(Done(item));
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                yield return JsonData.Parse(Completed("""[{"type":"reasoning","id":"rs_1","encrypted_content":"late-cipher"}]"""));
            }
            var transport = new ResponsesTextToolTransport((_, token) => Source(token));
            var frames = new List<StreamEvent>();
            async Task Drain()
            {
                await foreach (var frame in transport.StreamAsync(Request())) frames.Add(frame);
            }
            var drain = Drain();
            try
            {
                await entered.Task.WaitAsync(Deadline);
                Check(!drain.IsCompleted, "Terminal gate did not hold");
                var early = frames.OfType<ThinkingEnded>().SingleOrDefault();
                Check((early is not null) == !string.IsNullOrEmpty(encryption), "Wrong preterminal thinking-end timing");
                var earlyWire = early is null ? null : early.ExtraProperties!.Values["thinkingSignature"].Value.GetString();
                release.TrySetResult();
                await drain;
                Check(frames.OfType<ThinkingEnded>().Count() == 1, "Thinking ended twice");
                var final = frames.OfType<StreamDone>().Single();
                var signature = ((ThinkingContent)final.Message.Content.Single()).ExtraProperties!.Values["thinkingSignature"].Value.GetString()!;
                Check(JsonData.Parse(signature).Value.GetProperty("encrypted_content").GetString() == (encryption == "final-cipher" ? encryption : "late-cipher"), "Terminal encryption changed incorrectly");
                if (earlyWire is not null) Check(earlyWire == signature, "Early end metadata was mutated");
            }
            finally { release.TrySetResult(); await drain; }
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly Handler Handler;
        private readonly HttpClient _client;
        internal readonly ResponsesHttpSseTransport Transport;
        internal Fixture(byte[] wire, ResponsesTextToolOptions? options = null) : this(new MemoryStream(wire), options) { }
        internal Fixture(Stream body, ResponsesTextToolOptions? options = null)
        {
            Handler = new(async request =>
            {
                var payload = JsonData.Parse(await request.Content!.ReadAsStringAsync()).Value;
                Same("""{"effort":"high","summary":"auto"}""", payload.GetProperty("reasoning"));
                Same("""["reasoning.encrypted_content"]""", payload.GetProperty("include"));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamProbeContent(body) };
            });
            _client = new(Handler);
            var factory = Factory(new(ReasoningEffort: "high"));
            Transport = new(_client, request => factory.Create(request, Key), responsesOptions: options);
        }
        internal Task<ChatResult> Complete(CancellationToken token = default) => new ChatClient(Transport, capacity: 1).CompleteAsync(Request(), token);
        public void Dispose() => _client.Dispose();
    }
    internal sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
