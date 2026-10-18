// PiSharp-only (decision 0008, PLAN.md §7 and Appendix B). One Server-Sent Events stream per browser tab, after durable-pi's StreamMux
// (apps/dpi-playground/apps/dpi-playground-hub/src/mux.ts, MIT): the tab names the streams it wants (subscription keys) and the hub opens
// one authenticated upstream per key, tagging every frame `{sub, frame}`. The first frame is `{sub: "hello", frame: {type: "stream",
// streamId}}`; `network` frames carry every device and the tree's HTML whenever a device changes. PiSharp's difference: an agent
// subscription is reduced here (PiSharp.Durable.Client's AgentModel and SessionModel) and rendered with the page's own Razor partials, so the
// browser receives `view` frames that upsert, remove and order transcript items and replace the queue, waiting and task panels. Device and
// session subscriptions pass the device's frames through. Upstream failures arrive as `disconnected` (retried with backoff) or `fault`
// (final). A tab that falls 4 MB behind is cut off and reconnects.
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using PiSharp.Durable.Client;
using PiSharp.Durable.Web.Models;

namespace PiSharp.Durable.Web.Hub;

/// <summary>The per-tab stream multiplexer.</summary>
public sealed partial class StreamMux(DeviceRegistry registry, DeviceMonitor monitor, FragmentRenderer renderer, MarkdownRenderer markdown,
    ILogger<StreamMux> logger)
{
    /// <summary>The most subscriptions one tab may hold.</summary>
    public const int MaxSubscriptions = 20;

    private readonly ConcurrentDictionary<string, BrowserStream> streams = new();

    [GeneratedRegex(@"^([a-z0-9][a-z0-9-]{0,31})/(device|session|agent)(?:/([^/?]+))?(?:/([^/?]+))?(?:\?docs=([a-z0-9.,-]*))?$")]
    private static partial Regex KeyPattern();

    /// <summary>Open streams.</summary>
    public int Count => streams.Count;

    /// <summary>Serves one tab's stream until it closes.</summary>
    public async Task OpenAsync(HttpContext http)
    {
        http.Response.StatusCode = 200;
        http.Response.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
        await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        using var stream = new BrowserStream(id, http.RequestAborted);
        streams[id] = stream;
        stream.Send("hello", new JsonObject { ["type"] = "stream", ["streamId"] = id });
        var network = new Throttle(TimeSpan.FromMilliseconds(50));
        async Task PushNetwork()
        {
            var devices = monitor.Network();
            var html = await renderer.RenderAsync("~/Views/Network/_NetworkTree.cshtml", new NetworkPage(devices, Selection.None)).ConfigureAwait(false);
            stream.Send("network", new JsonObject
            {
                ["type"] = "network",
                ["devices"] = DurableWire.ToNode(devices),
                ["html"] = html,
            });
        }
        void OnChanged() => network.Trigger();
        monitor.Changed += OnChanged;
        try
        {
            await PushNetwork().ConfigureAwait(false);
            var pushing = network.RunAsync(PushNetwork, failure => logger.LogWarning(failure, "Rendering the network failed."), stream.Closed);
            await stream.PumpAsync(http.Response).ConfigureAwait(false);
            await pushing.ConfigureAwait(false);
        }
        finally
        {
            monitor.Changed -= OnChanged;
            streams.TryRemove(id, out _);
            stream.CloseSubscriptions();
        }
    }

    /// <summary>Replaces a tab's subscription set: opens new upstreams and closes dropped ones.</summary>
    /// <exception cref="KeyNotFoundException">The stream closed; the tab reconnects.</exception>
    /// <exception cref="ArgumentException">An invalid key, an unknown device, or too many keys.</exception>
    public IReadOnlyList<string> Subscribe(string streamId, IReadOnlyList<string> keys)
    {
        if (!streams.TryGetValue(streamId, out var stream)) throw new KeyNotFoundException("The stream closed; reconnect.");
        if (keys.Count > MaxSubscriptions) throw new ArgumentException($"At most {MaxSubscriptions} subscriptions.");
        var wanted = new Dictionary<string, Key>(StringComparer.Ordinal);
        foreach (var key in keys.Where(key => key != "network"))
        {
            var parsed = Parse(key);
            if (registry.Get(parsed.DeviceId) is null) throw new ArgumentException($"No device {parsed.DeviceId}.");
            wanted[key] = parsed;
        }
        stream.Replace(wanted, key => key.Scope == "agent"
            ? new AgentSubscription(this, stream, key)
            : new PassThroughSubscription(this, stream, key));
        return ["network", .. wanted.Keys];
    }

    /// <summary>A parsed subscription key.</summary>
    internal sealed record Key(string Text, string DeviceId, string Scope, string? SessionId, string? AgentId, string? Docs)
    {
        public string Path => Scope switch
        {
            "device" => "/v1/events" + (Docs is null ? "" : "?docs=" + Docs),
            "session" => $"/v1/sessions/{Uri.EscapeDataString(SessionId!)}/events",
            _ => $"/v1/sessions/{Uri.EscapeDataString(SessionId!)}/agents/{Uri.EscapeDataString(AgentId!)}/events?docs=" +
                string.Join(",", new[] { "pisharp.job-runs" }.Concat((Docs ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)).Distinct()),
        };
    }

    internal static Key Parse(string key)
    {
        var match = KeyPattern().Match(key);
        if (!match.Success) throw new ArgumentException($"Invalid subscription key: {key}");
        var (device, scope, a, b, docs) = (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3], match.Groups[4], match.Groups[5]);
        if (scope == "device" && !a.Success) return new Key(key, device, scope, null, null, docs.Success && docs.Value.Length > 0 ? docs.Value : null);
        if (scope == "session" && a.Success && !b.Success) return new Key(key, device, scope, Uri.UnescapeDataString(a.Value), null, null);
        if (scope == "agent" && a.Success && b.Success)
            return new Key(key, device, scope, Uri.UnescapeDataString(a.Value), Uri.UnescapeDataString(b.Value), docs.Success && docs.Value.Length > 0 ? docs.Value : null);
        throw new ArgumentException($"Invalid subscription key: {key}");
    }

    /// <summary>Follows one upstream path across reconnects, as long as the subscription lives: waits for the device to be online, sends
    /// <c>disconnected</c> between connections and <c>fault</c> for errors a retry cannot fix.</summary>
    internal async Task FollowAsync(BrowserStream stream, string sub, string deviceId, string path, Func<JsonObject, Task> onFrame, CancellationToken token)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!token.IsCancellationRequested)
        {
            await monitor.WhenOnlineAsync(deviceId, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            var device = registry.Get(deviceId);
            var secret = registry.Token(deviceId);
            if (device is null || secret is null) return;
            try
            {
                using var client = new DurableDeviceClient(new Uri(device.Url), secret);
                await foreach (var frame in client.StreamAsync(path, cancellationToken: token).ConfigureAwait(false))
                {
                    if (frame.Type == "hello") backoff = TimeSpan.FromSeconds(1);
                    await onFrame(frame.Data).ConfigureAwait(false);
                }
                if (token.IsCancellationRequested) return;
                stream.Send(sub, new JsonObject { ["type"] = "disconnected", ["reason"] = "The stream ended." });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (DurableApiException failure) when (failure.Code is DurableErrorCodes.NotFound or DurableErrorCodes.Invalid or DurableErrorCodes.Ambiguous)
            {
                stream.Send(sub, new JsonObject { ["type"] = "fault", ["error"] = new JsonObject { ["code"] = failure.Code, ["message"] = failure.Message } });
                return;
            }
            catch (Exception failure) when (failure is DurableApiException or HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException)
            {
                stream.Send(sub, new JsonObject { ["type"] = "disconnected", ["reason"] = failure.Message });
            }
            catch (Exception failure)
            {
                logger.LogWarning(failure, "The {Sub} stream failed.", sub);
                stream.Send(sub, new JsonObject { ["type"] = "disconnected", ["reason"] = failure.Message });
            }
            try { await Task.Delay(backoff, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(15).Ticks));
        }
    }

    internal NetworkDevice? Device(string id) => monitor.Network().FirstOrDefault(device => device.Id == id);

    internal FragmentRenderer Renderer => renderer;

    internal MarkdownRenderer Markdown => markdown;

    internal ILogger Logger => logger;

    // ─── Streams and subscriptions ──────────────────────────────────────────

    /// <summary>One tab's stream: a queue of frames that one writer drains, and the tab's subscriptions.</summary>
    internal sealed class BrowserStream(string id, CancellationToken aborted) : IDisposable
    {
        private const long MaxBacklog = 4_000_000;
        private readonly Channel<string> queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private readonly CancellationTokenSource closed = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        private readonly Dictionary<string, Subscription> subscriptions = new(StringComparer.Ordinal);
        private readonly Lock gate = new();
        private long backlog;

        public string Id => id;

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

        public void Replace(Dictionary<string, Key> wanted, Func<Key, Subscription> create)
        {
            lock (gate)
            {
                foreach (var key in subscriptions.Keys.Where(key => !wanted.ContainsKey(key)).ToList())
                {
                    subscriptions[key].Dispose();
                    subscriptions.Remove(key);
                }
                foreach (var (text, key) in wanted)
                    if (!subscriptions.ContainsKey(text))
                    {
                        var subscription = create(key);
                        subscriptions[text] = subscription;
                        subscription.Start();
                    }
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
            try { closed.Cancel(); } catch (ObjectDisposedException) { /* Disposed. */ }
        }

        public void Dispose()
        {
            Close();
            CloseSubscriptions();
            closed.Dispose();
        }
    }

    /// <summary>A subscription's upstreams, stopped when the tab drops the key.</summary>
    internal abstract class Subscription(StreamMux mux, BrowserStream stream, Key key) : IDisposable
    {
        private readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(stream.Closed);

        protected StreamMux Mux => mux;
        protected BrowserStream Stream => stream;
        protected Key Key => key;
        protected CancellationToken Token => stop.Token;

        public abstract void Start();

        public void Dispose()
        {
            try { stop.Cancel(); } catch (ObjectDisposedException) { /* Disposed. */ }
        }
    }

    /// <summary>Device and session subscriptions: the device's frames as they are.</summary>
    internal sealed class PassThroughSubscription(StreamMux mux, BrowserStream stream, Key key) : Subscription(mux, stream, key)
    {
        public override void Start() => _ = Task.Run(() => Mux.FollowAsync(Stream, Key.Text, Key.DeviceId, Key.Path, frame =>
        {
            Stream.Send(Key.Text, frame);
            return Task.CompletedTask;
        }, Token));
    }

    /// <summary>An agent subscription: the agent's and its session's streams, reduced and rendered into <c>view</c> frames at most every
    /// 100 ms.</summary>
    internal sealed class AgentSubscription(StreamMux mux, BrowserStream stream, Key key) : Subscription(mux, stream, key)
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly Throttle render = new(TimeSpan.FromMilliseconds(100));
        private readonly Dictionary<string, (string Print, string Html)> items = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> panels = new(StringComparer.Ordinal);
        private AgentModel agent = new();
        private SessionModel session = new();
        private bool reset = true;
        private long AgentId => long.TryParse(Key.AgentId, out var id) ? id : 1;

        public override void Start()
        {
            _ = Task.Run(() => Mux.FollowAsync(Stream, Key.Text, Key.DeviceId, Key.Path, frame => ApplyAsync(frame, toAgent: true), Token));
            _ = Task.Run(() => Mux.FollowAsync(Stream, Key.Text + "#session", Key.DeviceId, $"/v1/sessions/{Uri.EscapeDataString(Key.SessionId!)}/events",
                frame => ApplyAsync(frame, toAgent: false), Token));
            _ = render.RunAsync(RenderAsync, failure => Mux.Logger.LogWarning(failure, "Rendering {Key} failed.", Key.Text), Token);
        }

        private async Task ApplyAsync(JsonObject frame, bool toAgent)
        {
            await gate.WaitAsync(Token).ConfigureAwait(false);
            try
            {
                var type = Display.Str(frame, "type");
                if (toAgent && type == "snapshot")
                {
                    agent = new AgentModel();
                    reset = true;
                }
                if (toAgent && type is "disconnected" or "fault") Stream.Send(Key.Text, frame);
                var changed = toAgent ? agent.Apply(frame) : session.Apply(frame);
                if (changed) render.Trigger();
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task RenderAsync()
        {
            await gate.WaitAsync(Token).ConfigureAwait(false);
            try
            {
                if (!agent.Ready) return;
                var device = Mux.Device(Key.DeviceId);
                var jobs = (device?.Info?.Jobs ?? []).OfType<JsonObject>().Where(job => Display.Str(job, "id") is not null)
                    .ToDictionary(job => Display.Str(job, "id")!, StringComparer.Ordinal);
                var context = new AgentRenderContext
                {
                    DeviceId = Key.DeviceId,
                    SessionId = Key.SessionId!,
                    AgentId = AgentId,
                    Markdown = Mux.Markdown,
                    JobRuns = agent.Docs.TryGetValue("pisharp.job-runs", out var runs) ? runs as JsonObject : null,
                    Jobs = jobs,
                    Agents = session.Agents.ToDictionary(item => item.Id),
                };
                if (reset) items.Clear();
                var upsert = new JsonArray();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in agent.Items)
                {
                    keys.Add(item.Key);
                    var print = Print(item, context);
                    if (items.TryGetValue(item.Key, out var known) && known.Print == print) continue;
                    var html = await Mux.Renderer.RenderAsync("~/Views/Fragments/_TranscriptItem.cshtml", new TranscriptItemModel(item, context)).ConfigureAwait(false);
                    items[item.Key] = (print, html);
                    upsert.Add(new JsonObject { ["id"] = item.Key, ["html"] = html });
                }
                var remove = new JsonArray([.. items.Keys.Where(item => !keys.Contains(item)).Select(item => (JsonNode)item)]);
                foreach (var gone in remove) items.Remove(gone!.GetValue<string>());

                var agentName = session.Agents.FirstOrDefault(item => item.Id == AgentId)?.Name ?? $"Agent #{AgentId}";
                var waiting = Display.Objects(context.JobRuns, "items").Where(run => Display.Str(run, "status") == "waiting").ToList();
                var changedPanels = new JsonObject();
                async Task Panel(string name, string path, object model)
                {
                    var html = await Mux.Renderer.RenderAsync(path, model).ConfigureAwait(false);
                    if (!reset && panels.TryGetValue(name, out var previous) && previous == html) return;
                    panels[name] = html;
                    changedPanels[name] = html;
                }
                await Panel("pending", "~/Views/Fragments/_PendingPanel.cshtml", new PendingModel(agent.Pending, waiting, agentName, device?.Name ?? Key.DeviceId)).ConfigureAwait(false);
                await Panel("queue", "~/Views/Fragments/_QueueChips.cshtml", agent.Inbox).ConfigureAwait(false);
                var rows = TaskRows(session, AgentId);
                await Panel("tasks", "~/Views/Fragments/_TaskTree.cshtml", new TasksModel(rows, AgentId, context.Agents)).ConfigureAwait(false);

                var usage = Transcript.Sum(DurableWire.Read<UsageTotals>(agent.Usage));
                var busy = agent.Running || session.Tasks.Any(task => task.ConversationId == AgentId && !InBackground(task, session.Tasks));
                Stream.Send(Key.Text, new JsonObject
                {
                    ["type"] = "view",
                    ["reset"] = reset,
                    ["items"] = new JsonObject { ["upsert"] = upsert, ["remove"] = remove, ["order"] = new JsonArray([.. agent.Items.Select(item => (JsonNode)item.Key)]) },
                    ["panels"] = changedPanels,
                    ["state"] = new JsonObject
                    {
                        ["running"] = agent.Running,
                        ["busy"] = busy,
                        ["generating"] = agent.Generating,
                        ["waiting"] = agent.Pending.Count + waiting.Count,
                        ["queued"] = agent.Inbox.Count,
                        ["tasks"] = rows.Count,
                        ["contextTokens"] = agent.ContextTokens,
                        ["compacting"] = agent.Compactions.Count > 0,
                        ["compactionFailure"] = agent.CompactionFailure,
                        ["retry"] = agent.Retry is { } retry ? new JsonObject { ["at"] = retry.At, ["error"] = retry.Error } : null,
                        ["notices"] = new JsonArray([.. agent.Notices.Select(notice => (JsonNode)notice)]),
                        ["model"] = agent.Agent["model"]?.DeepClone(),
                        ["thinkingLevel"] = agent.Agent["thinkingLevel"]?.DeepClone(),
                        ["usage"] = DurableWire.ToNode(usage),
                        ["agents"] = DurableWire.ToNode(session.Agents),
                    },
                });
                reset = false;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>Whether a task is background work: it is, or a task that owns it is.</summary>
        private static bool InBackground(TaskNode task, IReadOnlyList<TaskNode> tasks)
        {
            for (var (current, guard) = (task, 0); current is not null && guard < 64; guard++)
            {
                if (current.Background) return true;
                var owner = current.Owner;
                current = owner is null ? null! : tasks.FirstOrDefault(other => other.Id == owner)!;
            }
            return false;
        }

        /// <summary>What an item's HTML depends on: its JSON, and for a job card its run.</summary>
        private static string Print(TranscriptItem item, AgentRenderContext context)
        {
            var json = Transcript.ToJson([item]).ToJsonString();
            if (item is EventItem { EntryKind: "pisharp.job" } job && job.Data is JsonObject data && Display.Long(data, "taskId") is { } task)
                json += context.Run(task)?.ToJsonString();
            if (item is AssistantItem assistant && assistant.Blocks.OfType<ToolBlockView>().Any(block => block.Call.Name == "subagent"))
                json += string.Join(",", context.Agents.Values.Select(agent => agent.Id + agent.Name));
            return json;
        }

        /// <summary>The agent's live tasks and those of the subagents it owns, each under its owner task.</summary>
        private static List<(TaskNode Task, int Depth)> TaskRows(SessionModel session, long agentId)
        {
            var conversations = new HashSet<long> { agentId };
            bool grew;
            do
            {
                grew = false;
                foreach (var owned in session.Agents)
                    if (owned.OwnerAgentId is { } owner && conversations.Contains(owner) && conversations.Add(owned.Id)) grew = true;
            } while (grew);
            var tasks = session.Tasks.Where(task => conversations.Contains(task.ConversationId)).ToList();
            var byId = tasks.ToDictionary(task => task.Id);
            var rows = new List<(TaskNode, int)>();
            void Add(TaskNode task, int depth)
            {
                rows.Add((task, depth));
                foreach (var child in tasks.Where(other => other.Owner == task.Id)) Add(child, depth + 1);
            }
            foreach (var root in tasks.Where(task => task.Owner is null || !byId.ContainsKey(task.Owner.Value))) Add(root, 0);
            return rows;
        }
    }
}

/// <summary>Runs work at most once per interval, never concurrently, and always catches up with the newest trigger.</summary>
internal sealed class Throttle(TimeSpan interval)
{
    private readonly SemaphoreSlim signal = new(0, 1);

    public void Trigger()
    {
        try { signal.Release(); }
        catch (SemaphoreFullException) { /* Already triggered. */ }
    }

    public async Task RunAsync(Func<Task> work, Action<Exception> report, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await signal.WaitAsync(token).ConfigureAwait(false);
                await Task.Delay(interval, token).ConfigureAwait(false);
                try { await work().ConfigureAwait(false); }
                catch (Exception failure) when (failure is not OperationCanceledException) { report(failure); }
            }
        }
        catch (OperationCanceledException) { /* Closed. */ }
    }
}
