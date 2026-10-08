using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.Contracts;

internal static class ResolvedMainSummaryTests
{
    public static readonly List<(string Name, Task Original, Exception? Direct)> OriginalTasks = [];
    private static readonly ModelDescriptor Model = new("authored-model", "anthropic-messages", "anthropic");
    private static readonly Uri Endpoint = new("https://api.anthropic.com/");
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Resolved profile assertion failed."); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ChatRequest Request(string? thinking = null) => new(Model,
        [new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"synthetic\"}]}"))]) { ThinkingLevel = thinking };
    private static ValueTask<AuthenticationResolution> Resolve(string variable, string marker) =>
        InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(
            new ProviderEnvironmentSnapshot([KeyValuePair.Create<string, string?>(variable, marker)]),
            (_, _) => ValueTask.FromResult<StoredApiKeyCredential?>(null));
    private sealed class Handler(bool held = false) : HttpMessageHandler
    {
        public readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        public Task<HttpResponseMessage>? Original;
        public readonly List<(string Body, string? Key, string? Authorization)> Captures = [];
        public bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Original = Send(); return Original;
            async Task<HttpResponseMessage> Send()
            {
                var body = await request.Content!.ReadAsStringAsync();
                Captures.Add((body, request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null,
                    request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : null));
                Entered.TrySetResult(); if (held) await Release.Task;
                var frames = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"synthetic\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"authored-model\",\"content\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\n" +
                    "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":0}}\n\n" +
                    "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
                return new(HttpStatusCode.OK) { Content = new StringContent(frames, Encoding.UTF8, "text/event-stream") };
            }
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private static async Task Drain(AnthropicInjectedTransportLease lease, ChatRequest request, CancellationToken token = default)
    {
        var terminals = 0;
        await foreach (var frame in lease.Transport.StreamAsync(request, token)) if (frame is StreamTerminalEvent) terminals++;
        Check(terminals == 1);
    }
    public static async Task Channels()
    {
        foreach (var (variable, marker, bearer) in new[] {
            ("ANTHROPIC_API_KEY", "SYNTHETIC_KEY", false),
            ("ANTHROPIC_AUTH_TOKEN", "SYNTHETIC_sk-ant-oat_BEARER", true),
            ("ANTHROPIC_OAUTH_TOKEN", "SYNTHETIC_sk-ant-oat_KEY", true) })
        {
            using var handler = new Handler(); var auth = await Resolve(variable, marker);
            await using var main = await AnthropicResolvedTransports.AcquireMainAsync(Model, Endpoint, auth, new(2048), handler: handler);
            await using var summary = await AnthropicResolvedTransports.AcquireSummaryAsync(Model, Endpoint, auth, 256, new(2048), handler: handler);
            Check(!ReferenceEquals(main.Transport, summary.Transport) && handler.Captures.Count == 0);
            await Drain(main, Request()); await Drain(main, Request("off")); await Drain(summary, Request("off"));
            Check(handler.Captures.Count == 3 && !handler.Disposed);
            for (var i = 0; i < 3; i++)
            {
                var capture = handler.Captures[i];
                Check(capture.Key == (bearer ? null : marker) && capture.Authorization == (bearer ? "Bearer " + marker : null));
                using var body = JsonDocument.Parse(capture.Body);
                Check(body.RootElement.GetProperty("max_tokens").GetInt32() == (i == 2 ? 256 : 2048));
                if (i == 2) Check(!capture.Body.Contains("cache_control", StringComparison.Ordinal) && !body.RootElement.TryGetProperty("thinking", out _));
            }
            await main.DisposeAsync(); await summary.DisposeAsync(); Check(!handler.Disposed);
        }
    }
    public static async Task HeldOriginal()
    {
        using var handler = new Handler(held: true); using var stop = new CancellationTokenSource();
        var auth = await Resolve("ANTHROPIC_API_KEY", "SYNTHETIC_KEY");
        await using var summary = await AnthropicResolvedTransports.AcquireSummaryAsync(Model, Endpoint, auth, 256, new(2048), handler: handler);
        var originalDrain = Drain(summary, Request(), stop.Token); Exception? direct = null, sendDirect = null;
        Exception? control = null;
        try
        {
            if (await Task.WhenAny(handler.Entered.Task, originalDrain) == originalDrain && !handler.Entered.Task.IsCompleted)
            { await originalDrain; throw new InvalidOperationException("Original settled before held send."); }
            await handler.Entered.Task; var originalSend = handler.Original ?? throw new InvalidOperationException("Original send missing.");
            stop.Cancel(); Check(!originalSend.IsCompleted && !originalDrain.IsCompleted);
        }
        catch (Exception error) { control = error; }
        finally
        {
            handler.Release.TrySetResult();
            try { await originalDrain; } catch (Exception error) { direct = error; }
            var originalSend = handler.Original;
            if (originalSend is not null)
            {
                try { await originalSend; } catch (Exception error) { sendDirect = error; }
                OriginalTasks.Add(("held-summary-http-send", originalSend, sendDirect));
            }
            OriginalTasks.Add(("held-summary-drain", originalDrain, direct));
        }
        if (control is not null || sendDirect is not null)
            throw new AggregateException("Held summary originals/control failed.",
                new[] { control, direct, sendDirect }.OfType<Exception>());
        Check(originalDrain.IsCompleted && handler.Original is { IsCompletedSuccessfully: true } && handler.Captures.Count == 1);
        Check(direct is null or OperationCanceledException); await summary.DisposeAsync(); Check(!handler.Disposed);
    }
}
