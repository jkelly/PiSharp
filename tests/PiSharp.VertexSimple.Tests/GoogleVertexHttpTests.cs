using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.GoogleVertex;
using PiSharp.Contracts;

internal static class GoogleVertexHttpTests
{
    private static readonly ModelDescriptor Model = new("gemini-2.5-flash", "google-vertex", "google-vertex");
    private static readonly Uri Endpoint = new("https://us-central1-aiplatform.googleapis.com/v1/projects/authored-project/locations/us-central1/publishers/google/models/gemini-2.5-flash:streamGenerateContent?alt=sse");
    private static JsonData Metadata => JsonData.Parse("{\"id\":\"gemini-2.5-flash\",\"api\":\"google-vertex\",\"provider\":\"google-vertex\",\"reasoning\":true,\"input\":[\"text\"],\"cost\":{\"input\":1,\"output\":2,\"cacheRead\":0.5,\"cacheWrite\":0},\"maxTokens\":8192,\"contextWindow\":1000000}");
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Vertex native HTTP assertion failed."); }
    private static ChatRequest Request(params TranscriptEntry[] prior) => new(Model,
        [new("system", JsonData.Parse("{\"role\":\"system\",\"content\":\"vertex system\",\"toolsAdded\":[{\"name\":\"read\",\"description\":\"inert\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}]}")),
         ..prior, new("user", JsonData.Parse("{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"vertex input\"}]}"))], 71);
    private const string TextFrame = "data: {\"responseId\":\"vertex-response\",\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"vertex text\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":10,\"cachedContentTokenCount\":3,\"candidatesTokenCount\":4,\"thoughtsTokenCount\":2,\"totalTokenCount\":16}}\n\n";
    private sealed class Body(byte[] bytes) : MemoryStream(bytes)
    {
        internal int Closes;
        private int closed;
        protected override void Dispose(bool disposing) { if (disposing && Interlocked.Exchange(ref closed, 1) == 0) Closes++; base.Dispose(disposing); }
    }
    private sealed class Handler(string data = TextFrame, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal readonly List<HttpRequestMessage> Requests = [];
        internal JsonData? Wire;
        internal readonly List<Body> Bodies = [];
        internal bool Disposed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request); Wire = JsonData.Parse(await request.Content!.ReadAsStringAsync(token));
            var body = new Body(Encoding.UTF8.GetBytes(data)); Bodies.Add(body);
            return new(status) { Content = new StreamContent(body) };
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private static GoogleVertexHttpTransport Transport(HttpClient client, GoogleGenerativeAIOptions? projection = null) =>
        new(client, Model, new(Endpoint, "AUTHORED_VERTEX_TOKEN", projection ?? new(Metadata) { MaxTokens = 51 }));
    private static async Task<List<StreamEvent>> Drain(IChatTransport transport, ChatRequest request, CancellationToken token = default)
    { var frames = new List<StreamEvent>(); await foreach (var frame in transport.StreamAsync(request, token)) frames.Add(frame); return frames; }
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("vertex-http.actual-admitted-token-body-response-identity-and-usage", Wire);
        await check("vertex-http.genuine-history-signature-and-Google-public-guard", History);
        await check("vertex-http.current-tools-and-function-call-response", Tools);
        await check("vertex-http.held-payload-original-cancellation-before-send", Payload);
        await check("vertex-http.actual-provider-error-and-malformed-response-release", Errors);
        await check("vertex-http.early-enumerator-disposal-joins-physical-body", Early);
        await check("vertex-http.bounds-identity-auth-and-endpoint-refusal-before-send", Admission);
        await check("vertex-http.held-body-original-cancellation-and-preterminal-close", Held);
    }
    private static async Task Wire()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler, disposeHandler: false);
        var terminalSeen = false;
        var options = new GoogleGenerativeAIOptions(Metadata) { MaxTokens = 51,
            Hooks = new() { OnEventPublished = frame => { if (frame is StreamTerminalEvent) { terminalSeen = true; Check(handler.Bodies.Single().Closes == 1); } } } };
        var frames = await Drain(Transport(client, options), Request());
        var sent = handler.Requests.Single(); Check(sent.RequestUri == Endpoint && sent.Method == HttpMethod.Post);
        Check(sent.Headers.Authorization!.ToString() == "Bearer AUTHORED_VERTEX_TOKEN" && !sent.Headers.Contains("x-goog-api-key"));
        Check(handler.Wire!.Value.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32() == 51);
        Check(handler.Wire.Value.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString() == "vertex system");
        var final = (StreamTerminalEvent)frames.Last();
        Check(final.Reason == StopReason.Stop && final.Message.Api == Model.Api && final.Message.Provider == Model.Provider && final.Message.Model == Model.Id);
        Check(final.Message.Usage.Input == 7 && final.Message.Usage.Output == 6 && final.Message.Usage.CacheRead == 3 && final.Message.Usage.TotalTokens == 16);
        Check(terminalSeen && !handler.Disposed && frames.OfType<TextDelta>().Single().Delta == "vertex text");
    }
    private static async Task History()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler, false);
        TranscriptEntry Previous(string api) => new("assistant", JsonData.Parse("{\"role\":\"assistant\",\"api\":" + JsonSerializer.Serialize(api) + ",\"provider\":\"google-vertex\",\"model\":\"gemini-2.5-flash\",\"stopReason\":\"stop\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"retained thought\",\"thinkingSignature\":\"YWJj\"}]}"));
        var transport = Transport(client); await Drain(transport, Request(Previous("google-vertex")));
        Check(handler.Wire!.ToString().Contains("\"thoughtSignature\":\"YWJj\"", StringComparison.Ordinal));
        await Drain(transport, Request(Previous("google-generative-ai")));
        Check(!handler.Wire!.ToString().Contains("thoughtSignature", StringComparison.Ordinal));
        try { GoogleRequestProjector.Project(Request(), new(Metadata)); throw new InvalidOperationException("Public Google guard broadened."); }
        catch (GoogleGenerativeAIException error) { Check(error.Failure == GoogleFailure.Configuration); }
    }
    private static async Task Tools()
    {
        using var handler = new Handler("data: {\"candidates\":[{\"content\":{\"parts\":[{\"functionCall\":{\"id\":\"vertex-call\",\"name\":\"read\",\"args\":{\"path\":\"inert\"}},\"thoughtSignature\":\"YWJj\"}]},\"finishReason\":\"STOP\"}]}\n\n");
        using var client = new HttpClient(handler, false); var frames = await Drain(Transport(client), Request());
        Check(frames.OfType<ToolCallStarted>().Single().ToolCall.Id == "vertex-call");
        Check(frames.OfType<ToolCallEnded>().Single().ToolCall.Arguments.Value.GetProperty("path").GetString() == "inert");
        Check(frames.Last() is StreamTerminalEvent { Reason: StopReason.ToolUse });
        Check(handler.Wire!.Value.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("name").GetString() == "read");
        await Drain(Transport(client), Request(new TranscriptEntry("system", JsonData.Parse("{\"role\":\"system\",\"content\":\"\",\"toolsRemoved\":[{\"name\":\"read\"}]}"))));
        Check(!handler.Wire!.Value.TryGetProperty("tools", out _));
    }
    private static async Task Payload()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler, false); using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<JsonData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var projection = new GoogleGenerativeAIOptions(Metadata) { Hooks = new() { OnPayload = (parameters, model, _) =>
        { Check(model.Identity == Model && parameters.Value.GetProperty("model").GetString() == Model.Id); entered.TrySetResult(); return new(release.Task); } } };
        var running = Drain(Transport(client, projection), Request(), stop.Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); stop.Cancel(); Check(!running.IsCompleted && handler.Requests.Count == 0); }
        finally { release.TrySetResult(null); await running; }
        Check(running.Result.Last() is StreamTerminalEvent { Reason: StopReason.Aborted } && handler.Requests.Count == 0);
    }
    private static async Task Errors()
    {
        foreach (var (data, status) in new[] { ("{\"error\":\"inert\"}", HttpStatusCode.Forbidden), ("data: {broken}\n\n", HttpStatusCode.OK), ("data: {\"candidates\":[]}\n\n", HttpStatusCode.OK) })
        {
            using var handler = new Handler(data, status); using var client = new HttpClient(handler, false);
            Check((await Drain(Transport(client), Request())).Last() is StreamTerminalEvent { Reason: StopReason.Error });
            Check(handler.Bodies.Single().Closes == 1 && !handler.Disposed);
        }
    }
    private static async Task Early()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler, false);
        var enumerator = Transport(client).StreamAsync(Request()).GetAsyncEnumerator();
        try { Check(await enumerator.MoveNextAsync() && enumerator.Current is StreamStarted); Check(handler.Bodies.Single().Closes == 0); }
        finally { await enumerator.DisposeAsync(); }
        Check(handler.Bodies.Single().Closes == 1 && !handler.Disposed);
    }
    private static async Task Admission()
    {
        using var handler = new Handler(); using var client = new HttpClient(handler, false);
        foreach (var invalid in new[] { new GoogleVertexOptions(new("http://inert.invalid"), "AUTHORED_TOKEN", new(Metadata)),
            new(Endpoint, "TOKEN\r\n", new(Metadata)), new(Endpoint, "AUTHORED_TOKEN", new(Metadata, "KEY")),
            new(Endpoint, "AUTHORED_TOKEN", new(Metadata) { MaxRetries = 1 }) })
        { try { _ = new GoogleVertexHttpTransport(client, Model, invalid); throw new InvalidOperationException("Invalid admission succeeded."); } catch (GoogleGenerativeAIException) { } }
        Check((await Drain(Transport(client), Request() with { Model = Model with { Api = "google-generative-ai" } })).Last() is StreamTerminalEvent { Reason: StopReason.Error });
        var headers = new GoogleGenerativeAIOptions(Metadata) { Headers = JsonData.Parse("{\"authorization\":null}") };
        Check((await Drain(Transport(client, headers), Request())).Last() is StreamTerminalEvent { Reason: StopReason.Error });
        Check(handler.Requests.Count == 0);
    }
    private sealed class HeldBody : MemoryStream
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Active, Closes; private int closed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Active++; try { Entered.TrySetResult(); await Release.Task; return await base.ReadAsync(buffer, CancellationToken.None); }
            finally { Active--; }
        }
        protected override void Dispose(bool disposing) { if (disposing && Interlocked.Exchange(ref closed, 1) == 0) Closes++; base.Dispose(disposing); }
    }
    private sealed class HeldHandler(HeldBody body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
    }
    private static async Task Held()
    {
        using var body = new HeldBody(); using var handler = new HeldHandler(body); using var client = new HttpClient(handler, false); using var stop = new CancellationTokenSource();
        var terminalSeen = false;
        var projection = new GoogleGenerativeAIOptions(Metadata) { Hooks = new() { OnEventPublished = frame =>
        { if (frame is StreamTerminalEvent) { terminalSeen = true; Check(body.Active == 0 && body.Closes == 1); } } } };
        var running = Drain(Transport(client, projection), Request(), stop.Token);
        try { await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); stop.Cancel(); Check(!running.IsCompleted && body.Active == 1 && body.Closes == 0 && !terminalSeen); }
        finally { body.Release.TrySetResult(); await running; }
        Check(running.Result.Last() is StreamTerminalEvent { Reason: StopReason.Aborted } && terminalSeen && body.Active == 0 && body.Closes == 1);
    }
}
