using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

internal static class NativeProviderFactoryTests
{
    private const string Key = "offline-synthetic-key";

    public static async Task ExplicitRoutesAndOwnership()
    {
        foreach (var route in new[] { "responses", "completions", "openrouter", "anthropic" })
        {
            using var handler = new CaptureHandler();
            var model = Model(route);
            using var provider = Create(route, model, handler);
            var registry = new ModelTransportRegistry([provider]);
            Check(registry.Models.Single() == model, "Registry lost explicit model identity.");
            // Offline redirect response: no Location is followed and no body is included in diagnostics.
            try { await foreach (var _ in registry.StreamAsync(new(model, []))) { } }
            catch (HttpRequestException error)
            {
                Check(error.StatusCode == HttpStatusCode.TemporaryRedirect && !error.Message.Contains("private-response-body"),
                    "HTTP rejection diagnostics included the body or lost the status.");
            }
            Check(handler.Calls == 1 && handler.Address == Address(route), "Wrong route or repeated HTTP send.");
            Check(handler.Key == Key, "Explicit key was not bound to the selected authentication header.");
            using var json = JsonDocument.Parse(handler.Payload!);
            Check(json.RootElement.GetProperty("model").GetString() == model.Id, "Model selection was changed.");
            Check(json.RootElement.GetProperty("stream").GetBoolean(), "Streaming was not selected.");
            if (route == "anthropic")
                Check(json.RootElement.GetProperty("max_tokens").GetInt32() == 4096, "Anthropic token limit was lost.");
            Check(handler.Content.Disposed, "Rejected response was not disposed.");
            provider.Dispose();
            Check(!handler.Disposed, "Caller-owned injected handler was disposed.");
            Expect<ObjectDisposedException>(() => provider.Transport.StreamAsync(new(model, [])));
        }
    }

    public static Task AdmissionAndCancellation()
    {
        using var handler = new CaptureHandler();
        var model = Model("responses");
        foreach (var endpoint in new[] { "http://api.openai.com/v1/responses", "https://example.invalid/v1/responses",
            "https://api.openai.com/v1/responses?secret=value", "https://api.openai.com/v1/responses#fragment",
            "https://user@api.openai.com/v1/responses", "https://api.openai.com:444/v1/responses" })
            Expect<ArgumentException>(() => NativeProviderFactory.CreateResponses(model, new(endpoint), Key, new(false), handler: handler));
        Expect<ArgumentException>(() => NativeProviderFactory.CreateResponses(model with { Provider = "openrouter" },
            new(Address("responses")), Key, new(false), handler: handler));
        Expect<ArgumentException>(() => NativeProviderFactory.CreateResponses(model, new(Address("responses")),
            "secret\r\nvalue", new(false), handler: handler));
        using var provider = Create("responses", model, handler);
        Expect<ArgumentException>(() => provider.Transport.StreamAsync(new(model with { Id = "other" }, [])));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Expect<OperationCanceledException>(() => provider.Transport.StreamAsync(new(model, []), cancelled.Token));
        Check(handler.Calls == 0, "Rejected selection or cancellation invoked HTTP.");
        return Task.CompletedTask;
    }

    public static async Task ResponsesEarlyDisposal()
    {
        using var handler = new CaptureHandler(success: true);
        var model = Model("responses");
        using var provider = Create("responses", model, handler);
        await using (var events = provider.Transport.StreamAsync(new(model, [])).GetAsyncEnumerator())
        {
            while (await events.MoveNextAsync())
                if (events.Current is TextDelta) break;
            Check(handler.Calls == 1, "Responses factory did not admit HTTP stream.");
            Check(!handler.Content.Disposed, "Fixture did not stop before response completion.");
        }
        Check(handler.Content.Disposed, "Early stream disposal left the response owned.");
        Check(!handler.Disposed, "Early stream disposal disposed the injected handler.");
    }

    public static async Task ResponsesSendCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new CaptureHandler(cancelOnSend: cancellation);
        var model = Model("responses");
        using var provider = Create("responses", model, handler);
        try
        {
            await foreach (var _ in provider.Transport.StreamAsync(new(model, []), cancellation.Token)) { }
            throw new InvalidOperationException("Cancellation did not interrupt Responses send.");
        }
        catch (OperationCanceledException)
        {
            Check(handler.Calls == 1 && handler.SendCancelled, "Caller cancellation did not reach the injected handler.");
        }
    }

    private static ModelDescriptor Model(string route) => new("offline-model",
        route == "responses" ? "openai-responses" : route == "anthropic" ? "anthropic-messages" : "openai-completions",
        route == "anthropic" ? "anthropic" : route == "openrouter" ? "openrouter" : "openai");
    private static string Address(string route) => route switch
    {
        "responses" => "https://api.openai.com/v1/responses",
        "anthropic" => "https://api.anthropic.com/v1/messages?beta=true",
        "openrouter" => "https://openrouter.ai/api/v1/chat/completions",
        _ => "https://api.openai.com/v1/chat/completions"
    };
    private static NativeHttpModelProvider Create(string route, ModelDescriptor model, HttpMessageHandler handler) => route switch
    {
        "responses" => NativeProviderFactory.CreateResponses(model, new(Address(route)), Key, new(false), handler: handler),
        "anthropic" => NativeProviderFactory.CreateAnthropic(model, new("https://api.anthropic.com/"), Key, new(4096), handler: handler),
        _ => NativeProviderFactory.CreateCompletions(model, new(Address(route)), Key, handler: handler)
    };

    private sealed class CaptureHandler(bool success = false, CancellationTokenSource? cancelOnSend = null) : HttpMessageHandler
    {
        public int Calls;
        public bool Disposed;
        public bool SendCancelled;
        public string? Address, Key, Payload;
        public TrackingContent Content { get; } = new(success
            ? "data: {\"type\":\"response.created\",\"response\":{\"id\":\"offline-response\"}}\n\n" +
              "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"offline-message\",\"role\":\"assistant\",\"content\":[]}}\n\n" +
              "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"hello\"}\n\n" +
              "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"offline-response\",\"status\":\"completed\",\"output\":[]}}\n\n"
            : "private-response-body");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            if (cancelOnSend is not null)
            {
                cancelOnSend.Cancel();
                SendCancelled = token.IsCancellationRequested;
                token.ThrowIfCancellationRequested();
            }
            Address = request.RequestUri!.AbsoluteUri;
            Key = request.Headers.Authorization?.Parameter ?? request.Headers.GetValues("x-api-key").Single();
            Payload = await request.Content!.ReadAsStringAsync(token);
            var response = new HttpResponseMessage(success ? HttpStatusCode.OK : HttpStatusCode.TemporaryRedirect) { Content = Content };
            response.Headers.Location = new("https://example.invalid/credential-sink");
            return response;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class TrackingContent(string body) : ByteArrayContent(Encoding.UTF8.GetBytes(body))
    {
        public bool Disposed;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }
}
