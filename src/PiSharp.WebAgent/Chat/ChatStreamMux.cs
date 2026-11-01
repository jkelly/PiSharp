// Decision 0011 (run W2, WU7): the Web Agent's one Server-Sent Events stream per tab, after PiSharp Durable Web's StreamMux and Web.Common's
// stream.js (MuxStream): GET /api/stream answers `{sub, frame}` events, the first `{sub: "hello", frame: {type: "stream", streamId}}`; the
// tab then PUTs the keys it wants to /api/stream/{streamId} as {subs}. Keys: `chat/{session}/{agent}` (AgentKey) is one agent's rendered
// view (AgentViewHub's `view` frames, then `fault` or `disconnected`), shared with every other tab on that agent; `chats` is the sidebar's
// list (`{type: "chats", items: [{id, title, running, updated}]}`), sent at once and again when a chat starts or stops running, is renamed,
// created or archived. Frames carry rendered, sanitized HTML and the chat's display state, never a credential. A tab that falls 4 MB behind is
// cut off and reconnects. W4 fix 2: `session/{session}/{agent}` (SessionKey) is the read-only view of an agent of any session SessionViews
// knows, with or without a chat binding (an agent compare model's session before it is opened as a chat): the same `view` frames, rendered
// without actions or interactive panels.
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using PiSharp.Durable.Client;
using PiSharp.WebAgent.Shell;

namespace PiSharp.WebAgent.Chat;

/// <summary>The per-tab stream multiplexer of the chat pages. A singleton.</summary>
public sealed partial class ChatStreamMux(AgentViewHub hub, ChatBindings bindings, SessionViews views, DeviceHostService device, ILogger<ChatStreamMux> logger)
{
    /// <summary>The most subscriptions one tab may hold.</summary>
    public const int MaxSubscriptions = 20;

    /// <summary>The sidebar list's key.</summary>
    public const string ChatsKey = "chats";

    /// <summary>The first frame's key.</summary>
    public const string HelloKey = "hello";

    private readonly ConcurrentDictionary<string, TabStream> streams = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^chat/([^/?#]{1,200})/([0-9]{1,18})$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^session/([^/?#]{1,200})/([0-9]{1,18})$")]
    private static partial Regex SessionKeyPattern();

    /// <summary>The key of the view of agent <paramref name="agentId"/> of chat <paramref name="sessionId"/>: <c>chat/{session}/{agent}</c>.</summary>
    public static string AgentKey(string sessionId, long agentId) => $"chat/{Uri.EscapeDataString(sessionId)}/{agentId}";

    /// <summary>The key of the read-only view of agent <paramref name="agentId"/> of session <paramref name="sessionId"/>, which need not be
    /// a chat: <c>session/{session}/{agent}</c>.</summary>
    public static string SessionKey(string sessionId, long agentId) => $"session/{Uri.EscapeDataString(sessionId)}/{agentId}";

    /// <summary>Open streams.</summary>
    public int Count => streams.Count;

    private DeviceHostService Device => device;

    private ChatBindings Bindings => bindings;

    private ILogger Logger => logger;

    /// <summary>The session and agent of an agent key, or null when <paramref name="key"/> is not one.</summary>
    public static (string SessionId, long AgentId)? ParseAgentKey(string key) => Parse(KeyPattern().Match(key ?? ""));

    /// <summary>The session and agent of a read-only session key, or null when <paramref name="key"/> is not one.</summary>
    public static (string SessionId, long AgentId)? ParseSessionKey(string key) => Parse(SessionKeyPattern().Match(key ?? ""));

    private static (string SessionId, long AgentId)? Parse(Match match) =>
        match.Success && long.TryParse(match.Groups[2].Value, out var agent) && agent > 0
            ? (Uri.UnescapeDataString(match.Groups[1].Value), agent)
            : null;

    /// <summary>Serves one tab's stream until it closes.</summary>
    public async Task OpenAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.StatusCode = 200;
        http.Response.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
        await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        using var stream = new TabStream(http.RequestAborted);
        streams[id] = stream;
        stream.Send(HelloKey, new JsonObject { ["type"] = "stream", ["streamId"] = id });
        try
        {
            await stream.PumpAsync(http.Response).ConfigureAwait(false);
        }
        finally
        {
            streams.TryRemove(id, out _);
            stream.CloseSubscriptions();
        }
    }

    /// <summary>Replaces a tab's subscription set: opens new ones and closes dropped ones. Returns the keys now held.</summary>
    /// <exception cref="KeyNotFoundException">The stream closed; the tab reconnects.</exception>
    /// <exception cref="ArgumentException">An invalid key, an unknown chat, or too many keys.</exception>
    public IReadOnlyList<string> Subscribe(string streamId, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (!streams.TryGetValue(streamId ?? "", out var stream)) throw new KeyNotFoundException("The stream closed; reconnect.");
        var distinct = keys.Where(key => !string.IsNullOrEmpty(key)).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > MaxSubscriptions) throw new ArgumentException($"At most {MaxSubscriptions} subscriptions.", nameof(keys));
        var wanted = new Dictionary<string, Func<IDisposable>>(StringComparer.Ordinal);
        foreach (var key in distinct)
        {
            if (key == ChatsKey)
            {
                wanted[key] = () => new ChatsSubscription(this, stream);
                continue;
            }
            if (ParseSessionKey(key) is { } viewed)
            {
                if (!views.Knows(viewed.SessionId)) throw new ArgumentException($"No session {viewed.SessionId} to show.", nameof(keys));
                wanted[key] = () => hub.Subscribe(viewed.SessionId, viewed.AgentId, frame => stream.Send(key, frame), readOnly: true);
                continue;
            }
            if (ParseAgentKey(key) is not { } agent) throw new ArgumentException($"Invalid subscription key: {key}", nameof(keys));
            if (bindings.Get(agent.SessionId) is null) throw new ArgumentException($"No chat {agent.SessionId}.", nameof(keys));
            wanted[key] = () => hub.Subscribe(agent.SessionId, agent.AgentId, frame => stream.Send(key, frame));
        }
        stream.Replace(wanted);
        return [.. wanted.Keys];
    }

    /// <summary>The sidebar list frame.</summary>
    internal JsonObject ChatsFrame() => new()
    {
        ["type"] = "chats",
        ["items"] = new JsonArray([.. bindings.List().Take(ShellNav.RecentChatLimit).Select(binding => (JsonNode)new JsonObject
        {
            ["id"] = binding.SessionId,
            ["title"] = binding.Title,
            ["running"] = device.IsBusy(binding.SessionId),
            ["updated"] = binding.Updated.ToUnixTimeMilliseconds(),
        })]),
    };

    /// <summary>The sidebar list subscription of one tab.</summary>
    private sealed class ChatsSubscription : IDisposable
    {
        private readonly ChatStreamMux mux;
        private readonly TabStream stream;
        private readonly CancellationTokenSource stop;
        private readonly ViewThrottle throttle = new(TimeSpan.FromMilliseconds(100));
        private string? last;

        public ChatsSubscription(ChatStreamMux mux, TabStream stream)
        {
            this.mux = mux;
            this.stream = stream;
            stop = CancellationTokenSource.CreateLinkedTokenSource(stream.Closed);
            mux.Device.ActivityChanged += Changed;
            mux.Bindings.Changed += OnBinding;
            Push();
            _ = throttle.RunAsync(() =>
            {
                Push();
                return Task.CompletedTask;
            }, failure => mux.Logger.LogWarning(failure, "Sending the chat list failed."), stop.Token);
        }

        private void OnBinding(string sessionId) => Changed();

        private void Changed() => throttle.Trigger();

        private void Push()
        {
            var frame = mux.ChatsFrame();
            var text = frame.ToJsonString();
            if (text == last) return;
            last = text;
            stream.Send(ChatsKey, frame);
        }

        public void Dispose()
        {
            mux.Device.ActivityChanged -= Changed;
            mux.Bindings.Changed -= OnBinding;
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { /* Disposed. */ }
            stop.Dispose();
        }
    }

    /// <summary>One tab's stream: a queue of frames that one writer drains, and the tab's subscriptions.</summary>
    private sealed class TabStream(CancellationToken aborted) : IDisposable
    {
        private const long MaxBacklog = 4_000_000;
        private readonly Channel<string> queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource closed = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        private readonly Dictionary<string, IDisposable> subscriptions = new(StringComparer.Ordinal);
        private readonly Lock gate = new();
        private long backlog;

        public CancellationToken Closed => closed.Token;

        public void Send(string sub, JsonObject frame)
        {
            if (closed.IsCancellationRequested) return;
            var text = "data: " + new JsonObject { ["sub"] = sub, ["frame"] = frame }.ToJsonString(DurableWire.Options) + "\n\n";
            if (Interlocked.Add(ref backlog, Encoding.UTF8.GetByteCount(text)) > MaxBacklog)
            {
                Close();
                return;
            }
            queue.Writer.TryWrite(text);
        }

        public void Replace(Dictionary<string, Func<IDisposable>> wanted)
        {
            lock (gate)
            {
                if (closed.IsCancellationRequested) return;
                foreach (var key in subscriptions.Keys.Where(key => !wanted.ContainsKey(key)).ToList())
                {
                    subscriptions[key].Dispose();
                    subscriptions.Remove(key);
                }
                foreach (var (key, create) in wanted)
                    if (!subscriptions.ContainsKey(key)) subscriptions[key] = create();
            }
        }

        public void CloseSubscriptions()
        {
            lock (gate)
            {
                foreach (var subscription in subscriptions.Values) subscription.Dispose();
                subscriptions.Clear();
            }
        }

        public async Task PumpAsync(HttpResponse response)
        {
            using var ping = new PeriodicTimer(TimeSpan.FromSeconds(15));
            var pinging = Task.Run(async () =>
            {
                try
                {
                    while (await ping.WaitForNextTickAsync(closed.Token).ConfigureAwait(false)) queue.Writer.TryWrite(": ping\n\n");
                }
                catch (OperationCanceledException) { /* Closed. */ }
            });
            try
            {
                await foreach (var text in queue.Reader.ReadAllAsync(closed.Token).ConfigureAwait(false))
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    await response.Body.WriteAsync(bytes, closed.Token).ConfigureAwait(false);
                    await response.Body.FlushAsync(closed.Token).ConfigureAwait(false);
                    Interlocked.Add(ref backlog, -bytes.Length);
                }
            }
            catch (Exception failure) when (failure is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The tab went away.
            }
            finally
            {
                Close();
                await pinging.ConfigureAwait(false);
            }
        }

        public void Close()
        {
            queue.Writer.TryComplete();
            try { closed.Cancel(); }
            catch (ObjectDisposedException) { /* Disposed. */ }
        }

        public void Dispose()
        {
            Close();
            CloseSubscriptions();
            closed.Dispose();
        }
    }
}

/// <summary>The body of <c>PUT /api/stream/{streamId}</c>.</summary>
/// <param name="Subs">The keys the tab wants.</param>
public sealed record ChatStreamSubscriptions(IReadOnlyList<string>? Subs);
