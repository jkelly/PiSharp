using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.Contracts;

// Source-only controls: coordinator owns Program registration and execution.
internal static class AnthropicAuthenticatedProviderTests
{
    private static readonly ModelDescriptor Model = new("authored-model", "anthropic-messages", "anthropic");
    private static readonly Uri Endpoint = new("https://api.anthropic.com/");
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Authenticated Anthropic assertion failed."); }
    private sealed class ProviderOwner(NativeHttpModelProvider provider) : IAsyncDisposable
    {
        internal int Calls;
        public ValueTask DisposeAsync() { Calls++; provider.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class Handler(string tool = "Bash", bool malformed = false) : HttpMessageHandler
    {
        internal int Calls;
        internal bool Disposed;
        internal string? Body;
        internal Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            Headers = request.Headers.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
            Body = await request.Content!.ReadAsStringAsync(token);
            string Frame(string type, object value) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";
            var stream = Frame("message_start", new { type = "message_start", message = new { id = "authored-response", role = "assistant", model = Model.Id, content = Array.Empty<object>(), usage = new { input_tokens = 2, output_tokens = 0 } } });
            stream += Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id = "authored-call", name = tool, input = new { } } });
            if (!malformed)
            {
                stream += Frame("content_block_stop", new { type = "content_block_stop", index = 0 });
                stream += Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "tool_use" }, usage = new { output_tokens = 1 } });
                stream += Frame("message_stop", new { type = "message_stop" });
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private static ChatRequest Request(string name = "bash", bool remove = false) => new(Model,
        [new("system", JsonData.Parse("{\"role\":\"system\",\"content\":[{\"type\":\"text\",\"text\":\"Authored system\"}],\"toolsAdded\":[{\"name\":" + JsonSerializer.Serialize(name) + ",\"description\":\"Authored inert tool\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}]}")),
         new("system", JsonData.Parse(remove ? "{\"role\":\"system\",\"content\":[],\"toolsRemoved\":[{\"name\":" + JsonSerializer.Serialize(name) + "}]}" : "{\"role\":\"system\",\"content\":[]}")),
         new("user", JsonData.Parse("{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Authored input\"}]}"))]);
    private static async Task<(AnthropicInjectedTransportLease Lease, ProviderOwner Owner)> Open(string environmentName, string secret,
        Handler handler, AnthropicMessagesKeyAuthRequestOptions? headers = null)
    {
        var resolution = await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(
            new ProviderEnvironmentSnapshot([KeyValuePair.Create<string, string?>(environmentName, secret)]),
            (_, _) => ValueTask.FromResult<StoredApiKeyCredential?>(null));
        ProviderOwner? owner = null;
        var lease = await AnthropicInjectedTransportAdapter.AcquireAsync(Model, resolution, (model, binding, _) =>
        {
            var provider = AnthropicResolvedProviderFactory.Create(model, Endpoint, binding, new(2048), headers, handler);
            owner = new(provider); return ValueTask.FromResult(new AnthropicTransportAdmission(model, provider.Transport, owner));
        });
        return (lease, owner!);
    }
    private static async Task<List<StreamEvent>> Drain(AnthropicInjectedTransportLease lease, ChatRequest request)
    { var frames = new List<StreamEvent>(); await foreach (var frame in lease.Transport.StreamAsync(request)) frames.Add(frame); return frames; }
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("anthropic-authenticated.actual-key-bearer-OAuth-wire-and-roundtrip", Channels);
        await check("anthropic-authenticated.exact-property-header-precedence-and-null-removal", Headers);
        await check("anthropic-authenticated.OAuth-beta-null-removes-default-and-no-affinity", Betas);
        await check("anthropic-authenticated.unknown-and-withdrawn-tool-name-remain-wire-exact", Unknown);
        await check("anthropic-authenticated.identity-header-bounds-before-send", Admission);
        await check("anthropic-authenticated.malformed-stream-settles-original-provider-owner", Malformed);
        await check("anthropic-authenticated.canceled-enumeration-before-send-keeps-owner", Canceled);
    }
    private static async Task Channels()
    {
        foreach (var (variable, secret, oauth, bearer) in new[] {
            ("ANTHROPIC_API_KEY", "AUTHORED_KEY", false, false),
            ("ANTHROPIC_AUTH_TOKEN", "AUTHORED_sk-ant-oat_BEARER", false, true),
            ("ANTHROPIC_OAUTH_TOKEN", "AUTHORED_sk-ant-oat_KEY", true, true) })
        {
            using var handler = new Handler(); var (lease, owner) = await Open(variable, secret, handler);
            await using (lease)
            {
                Check(handler.Calls == 0 && owner.Calls == 0);
                var frames = await Drain(lease, Request());
                Check(handler.Calls == 1 && handler.Headers.ContainsKey("Authorization") == bearer && handler.Headers.ContainsKey("x-api-key") == !bearer);
                Check(handler.Headers[bearer ? "Authorization" : "x-api-key"] == (bearer ? "Bearer " : "") + secret);
                using var body = JsonDocument.Parse(handler.Body!);
                Check(body.RootElement.GetProperty("tools")[0].GetProperty("name").GetString() == (oauth ? "Bash" : "bash"));
                Check(body.RootElement.GetProperty("system")[0].GetProperty("text").GetString()!.StartsWith(oauth ? "You are Claude Code" : "Authored system", StringComparison.Ordinal));
                Check(frames.OfType<ToolCallStarted>().Single().ToolCall.Name == (oauth ? "bash" : "Bash"));
                Check(frames.Last() is StreamTerminalEvent { Reason: StopReason.ToolUse } && owner.Calls == 0);
                if (oauth) Check(handler.Headers["user-agent"] == "claude-cli/2.1.280" && handler.Headers["anthropic-beta"].Contains("oauth-2025-04-20", StringComparison.Ordinal));
            }
            Check(owner.Calls == 1 && !handler.Disposed);
            await lease.DisposeAsync(); Check(owner.Calls == 1);
        }
    }
    private static async Task Headers()
    {
        using var handler = new Handler();
        var (lease, owner) = await Open("ANTHROPIC_AUTH_TOKEN", "AUTHORED_BEARER", handler,
            new(ModelHeaders: JsonData.Parse("{\"user-agent\":\"MODEL\",\"Authorization\":\"Bearer MODEL\"}"),
                Headers: JsonData.Parse("{\"User-Agent\":\"OPTION\",\"authorization\":null}")));
        await using (lease)
        {
            await Drain(lease, Request());
            Check(handler.Headers["user-agent"] == "MODEL" && !handler.Headers.ContainsKey("authorization") && !handler.Headers.ContainsKey("x-api-key"));
        }
        Check(owner.Calls == 1 && !handler.Disposed);
        using var empty = new Handler(); var (emptyLease, _) = await Open("ANTHROPIC_AUTH_TOKEN", "AUTHORED_BEARER", empty,
            new(Headers: JsonData.Parse("{\"Authorization\":\"\"}")));
        await using (emptyLease) { var frames = await Drain(emptyLease, Request()); Check(empty.Calls == 0 && frames.Last() is StreamTerminalEvent { Reason: StopReason.Error } && !frames.OfType<ToolCallStarted>().Any()); }
    }
    private static async Task Betas()
    {
        using var handler = new Handler(); var (lease, _) = await Open("ANTHROPIC_OAUTH_TOKEN", "AUTHORED_sk-ant-oat_KEY", handler,
            new(Headers: JsonData.Parse("{\"anthropic-beta\":null}"), SessionId: "AUTHORED_SESSION", SendSessionAffinityHeaders: true));
        await using (lease) { await Drain(lease, Request()); Check(!handler.Headers.ContainsKey("anthropic-beta") && !handler.Headers.ContainsKey("x-session-affinity")); }
    }
    private static async Task Unknown()
    {
        foreach (var (wire, remove) in new[] { ("AuthoredUnknown", false), ("Bash", true) })
        {
            using var handler = new Handler(wire); var (lease, _) = await Open("ANTHROPIC_OAUTH_TOKEN", "AUTHORED_sk-ant-oat_KEY", handler);
            await using (lease) { var frames = await Drain(lease, Request(remove: remove)); Check(frames.OfType<ToolCallStarted>().Single().ToolCall.Name == wire); }
        }
        using var folded = new Handler("AUTHOREDNAME");
        var (foldedLease, _) = await Open("ANTHROPIC_OAUTH_TOKEN", "AUTHORED_sk-ant-oat_KEY", folded);
        await using (foldedLease)
        {
            var frames = await Drain(foldedLease, Request("authoredName"));
            Check(frames.OfType<ToolCallStarted>().Single().ToolCall.Name == "authoredName");
        }
    }
    private static async Task Admission()
    {
        using var handler = new Handler(); var (lease, owner) = await Open("ANTHROPIC_API_KEY", "AUTHORED_KEY", handler);
        await using (lease)
        {
            var rejected = false; try { _ = lease.Transport.StreamAsync(Request() with { Model = Model with { Id = "foreign" } }); } catch (ArgumentException) { rejected = true; }
            Check(rejected && handler.Calls == 0 && owner.Calls == 0);
        }
        var fault = false;
        try { await Open("ANTHROPIC_AUTH_TOKEN", "AUTHORED_BEARER", handler, new(Headers: JsonData.Parse("{\"host\":\"authored.invalid\"}"))); }
        catch (AnthropicAuthenticationOriginalFailure error) { fault = error.InnerException is AnthropicMessagesKeyAuthRequestException; }
        Check(fault && handler.Calls == 0 && !handler.Disposed);
    }
    private static async Task Malformed()
    {
        using var handler = new Handler(malformed: true); var (lease, owner) = await Open("ANTHROPIC_OAUTH_TOKEN", "AUTHORED_sk-ant-oat_KEY", handler);
        await using (lease) { var frames = await Drain(lease, Request()); Check(handler.Calls == 1 && frames.Last() is StreamTerminalEvent { Reason: StopReason.Error } && owner.Calls == 0); }
        Check(owner.Calls == 1 && !handler.Disposed);
    }
    private static async Task Canceled()
    {
        using var handler = new Handler(); var (lease, owner) = await Open("ANTHROPIC_AUTH_TOKEN", "AUTHORED_BEARER", handler);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await using (lease)
        {
            var canceled = false; try { await foreach (var _ in lease.Transport.StreamAsync(Request(), stop.Token)) { } } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && handler.Calls == 0 && owner.Calls == 0);
        }
        Check(owner.Calls == 1 && !handler.Disposed);
    }
}
