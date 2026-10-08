using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

internal static class CompletionsToolImageFlowTests
{
    private const string Pin = "116d44f85e49b774c394530b5124727b8650d791bc899125a09c0f59fde7d46e";
    private const string Wire = "data: {\"choices\":[{\"delta\":{\"content\":\"Observed image\"}}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("completions-tool-image-flow.all-72-complete-source-bodies-at-http-and-canonical-completion", Corpus);
        yield return ("completions-tool-image-flow.owned-cleanup-gates-canonical-completion", Cleanup);
        yield return ("completions-tool-image-flow.malformed-bounded-and-cancelled-history-before-http-effects", Admission);
    }
    private static async Task Corpus()
    {
        using var source = Fixture();
        foreach (var captured in source.RootElement.GetProperty("observations").EnumerateArray())
        {
            var request = Request(captured); var original = request.Messages.Select(entry => entry.WireBody.ToString()).ToArray();
            var body = new OwnedBody(); var content = new BodyContent(body); JsonData? actual = null; Exception? observed = null; HttpRequestMessage? sent = null; var sends = 0; var builds = 0;
            using var handler = new Handler(async (http, token) =>
            {
                sends++; sent = http;
                try
                {
                    Equal("POST", http.Method.Method); Equal(captured.GetProperty("requests")[0].GetProperty("url").GetString(), http.RequestUri!.AbsoluteUri);
                    actual = JsonData.Parse(await http.Content!.ReadAsStringAsync(token));
                    Check(JsonElement.DeepEquals(captured.GetProperty("requests")[0].GetProperty("bodyJson"), actual.Value), "Complete actual HTTP image request differs: " + captured.GetProperty("id").GetString());
                    return new(HttpStatusCode.OK) { Content = content };
                }
                catch (Exception error) { observed = error; throw; }
            });
            using var client = new HttpClient(handler); var factory = Factory(captured, request.Model);
            var transport = new CompletionsHttpSseTransport(client, (value, token) => { builds++; return factory.Create(value, "authored-inert-image-key", token); });
            var result = await new ChatClient(transport).CompleteAsync(request);
            if (observed is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(observed).Throw();
            Check(result.Failure is null && result.Message.StopReason == StopReason.Stop, "Image history failed canonical completion: " + captured.GetProperty("id").GetString());
            Equal("Observed image", ((TextContent)result.Message.Content.Single()).Text); Equal(1, sends); Equal(1, builds);
            Check(actual is not null && body.Disposed && content.Disposed, "Actual image flow retained owned response work."); Equal(1, body.Closes);
            Check(original.SequenceEqual(request.Messages.Select(entry => entry.WireBody.ToString())), "Actual HTTP flow rewrote canonical image history.");
            try { _ = await sent!.Content!.ReadAsStringAsync(); throw new InvalidOperationException("Request ownership retained after completion."); } catch (ObjectDisposedException) { }
            Check(!handler.Disposed, "Flow disposed the borrowed HTTP client.");
        }
    }
    private static async Task Cleanup()
    {
        using var source = Fixture(); var captured = source.RootElement.GetProperty("observations").EnumerateArray().Single(value => value.GetProperty("id").GetString() == "two-consecutive-image-results-vision-bridge");
        var request = Request(captured); var body = new OwnedBody(gated: true); var content = new BodyContent(body); var sends = 0;
        using var handler = new Handler((_, _) => { sends++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); });
        using var client = new HttpClient(handler); var factory = Factory(captured, request.Model);
        var transport = new CompletionsHttpSseTransport(client, (value, token) => factory.Create(value, "authored-inert-image-key", token));
        using var cancellation = new CancellationTokenSource(); var completing = new ChatClient(transport).CompleteAsync(request, cancellation.Token);
        try
        {
            await Task.WhenAny(body.Entered.Task, completing).WaitAsync(TimeSpan.FromSeconds(5));
            Check(body.Entered.Task.IsCompleted, "Image replay failed before reaching HTTP cleanup: " + (completing.IsCompleted ? (await completing).Failure?.Kind.ToString() : "pending"));
            Check(!completing.IsCompleted && !content.Disposed, "Image-flow canonical authority preceded owned HTTP cleanup."); Equal(1, sends);
            body.Release.TrySetResult(); var result = await completing.WaitAsync(TimeSpan.FromSeconds(5));
            Check(result.Failure is null && result.Message.StopReason == StopReason.Stop && content.Disposed, "Image-flow completion did not join owned cleanup."); Equal(1, body.Closes);
        }
        finally { body.Release.TrySetResult(); cancellation.Cancel(); try { await completing.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
    }
    private static async Task Admission()
    {
        using var source = Fixture(); var captured = source.RootElement.GetProperty("observations")[0]; var request = Request(captured);
        var malformed = request with { Messages = request.Messages.SetItem(1, new("toolResult", JsonData.Parse("""{"role":"toolResult","toolCallId":"call-a","toolName":"inspect","content":[{"type":"image","data":"PRIVATE_TOOL_IMAGE"}],"timestamp":123}"""))) };
        foreach (var (value, maximumMessages) in new[] { (malformed, 1024), (request, 1) })
        {
            var sends = 0; using var handler = new Handler((_, _) => { sends++; throw new InvalidOperationException("Unexpected HTTP effect."); }); using var client = new HttpClient(handler);
            var model = value.Model; var factory = new CompletionsKeyAuthRequestFactory(new(captured.GetProperty("requests")[0].GetProperty("url").GetString()!), model,
                new(MaximumOutputMessages: maximumMessages) { ModelSupportsImages = true });
            var result = await new ChatClient(new CompletionsHttpSseTransport(client, (input, token) => factory.Create(input, "authored-inert-image-key", token))).CompleteAsync(value);
            Equal(0, sends); Check(result.Failure is not null && result.Message.StopReason == StopReason.Error, "Invalid image history acquired successful authority.");
            Check(!PiWireJson.WriteMessage(result.Message).ToString().Contains("PRIVATE_TOOL_IMAGE", StringComparison.Ordinal), "Private tool image leaked into failure message.");
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var builds = 0;
        using var cancelledHandler = new Handler((_, _) => throw new InvalidOperationException("Cancelled HTTP effect.")); using var cancelledClient = new HttpClient(cancelledHandler);
        var validFactory = Factory(captured, request.Model);
        try
        {
            var result = await new ChatClient(new CompletionsHttpSseTransport(cancelledClient, (value, token) => { builds++; return validFactory.Create(value, "authored-inert-image-key", token); })).CompleteAsync(request, cancelled.Token);
            Check(result.Message.StopReason == StopReason.Aborted && result.Failure is not null, "Cancelled image history acquired successful authority.");
        }
        catch (OperationCanceledException) { }
        Equal(0, builds);
    }
    private static ChatRequest Request(JsonElement source)
    {
        var model = source.GetProperty("model");
        return new(new(model.GetProperty("id").GetString()!, model.GetProperty("api").GetString()!, model.GetProperty("provider").GetString()!),
            source.GetProperty("context").GetProperty("messages").EnumerateArray().Select(value => new TranscriptEntry(value.GetProperty("role").GetString()!, JsonData.FromElement(value))).ToImmutableArray(), 123);
    }
    private static CompletionsKeyAuthRequestFactory Factory(JsonElement source, ModelDescriptor model) => new(new(source.GetProperty("requests")[0].GetProperty("url").GetString()!), model,
        new(RequiresAssistantAfterToolResult: source.GetProperty("projection").GetProperty("requiresAssistantAfterToolResult").GetBoolean(), RequiresToolResultName: source.GetProperty("projection").GetProperty("requiresToolResultName").GetBoolean())
            { ModelSupportsImages = source.GetProperty("model").GetProperty("input").EnumerateArray().Any(value => value.GetString() == "image") },
        new(MaxTokens: 64, Temperature: 0));
    private static JsonDocument Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Repository missing.");
        var bytes = File.ReadAllBytes(Path.Combine(directory.FullName, "fixtures/native/completions-tool-images-source.json")); Equal(Pin, Convert.ToHexStringLower(SHA256.HashData(bytes))); return JsonDocument.Parse(bytes);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    { public bool Disposed; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request, token); protected override void Dispose(bool disposing) { Disposed |= disposing; base.Dispose(disposing); } }
    private sealed class BodyContent(OwnedBody body) : HttpContent
    {
        public bool Disposed;
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult<Stream>(body);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("Unexpected body buffering.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed |= disposing; base.Dispose(disposing); }
    }
    private sealed class OwnedBody(bool gated = false) : MemoryStream(Encoding.UTF8.GetBytes(Wire), writable: false)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed; public int Closes; private Task? _closing;
        public override ValueTask DisposeAsync() => new(_closing ??= CloseCore());
        private async Task CloseCore() { Closes++; Entered.TrySetResult(); if (gated) await Release.Task; Disposed = true; base.Dispose(true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
