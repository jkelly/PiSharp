// Decision 0011 (run W2, WU7): one view feed per agent, shared by every tab that shows it. The first subscriber starts one
// AgentFrames.ReadAsync watch (a snapshot, then one batch per commit; the device commits model progress about every 150 ms, so only
// committed text streams); the last one to leave stops it. Frames reduce into PiSharp.Durable.Client's AgentModel, whose items render with
// the chat page's own partial (_ChatItem) through Web.Common's FragmentRenderer, at most every 150 ms, and only items whose content changed
// render again. Every subscriber gets `view` frames, as the hub's StreamMux sends them: a new one first gets the whole view (reset), the
// others the changes. Approvals and question forms render into the item's tool card or the "Waiting for you" panel. A view frame says which
// upstream frames it follows (`source`: snapshot, batch, inbox), so a client can tell a catch-up from a live change. WU8 adds the items'
// facts (ChatFacts: time, usage, failures, restarts, the running tail) to the render context and to each item's fingerprint, and three more
// panels: `notices` (compacting, a compaction failure, an automatic retry's wait), `banners` (no providers, an expired sign-in) and `info`
// (the session details). The state's busy flag also reads the device's running flag (DeviceHostService), which changes outside the
// agent's frames, so every feed renders again when that flag changes (ActivityChanged); otherwise a page could keep showing busy after a run.
// W3 (S5): the render context carries the answers' cost marks from the spend ledger (ChatSpend, read at most every second or when the
// entries or the running state change), and an item's fingerprint includes its mark, so a footer changes when its request settles.
using System.Text.Json.Nodes;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Server;
using PiSharp.Web.Common;

namespace PiSharp.WebAgent.Chat;

/// <summary>The shared, reference-counted agent view feeds. A singleton.</summary>
public sealed class AgentViewHub : IAsyncDisposable
{
    /// <summary>How long changes gather before a view is rendered.</summary>
    public static TimeSpan Coalesce { get; } = TimeSpan.FromMilliseconds(150);

    private readonly DeviceHostService device;
    private readonly ChatService chats;
    private readonly ChatBindings bindings;
    private readonly ApprovalBroker broker;
    private readonly FragmentRenderer renderer;
    private readonly ILogger<AgentViewHub> logger;
    private readonly Lock gate = new();
    private readonly Dictionary<(string Session, long Agent), Feed> feeds = [];
    private int started;

    /// <summary>The hub over the device's sessions.</summary>
    public AgentViewHub(DeviceHostService device, ChatService chats, ChatBindings bindings, ApprovalBroker broker, FragmentRenderer renderer,
        ILogger<AgentViewHub> logger)
    {
        this.device = device;
        this.chats = chats;
        this.bindings = bindings;
        this.broker = broker;
        this.renderer = renderer;
        this.logger = logger;
        broker.Changed += Refresh;
        bindings.Changed += Refresh;
        device.ActivityChanged += RefreshAll;
    }

    /// <summary>The feeds open now (one per watched agent, however many tabs watch it).</summary>
    public int FeedCount
    {
        get { lock (gate) return feeds.Count; }
    }

    /// <summary>How many agent watches were started since the hub was made.</summary>
    public int FeedsStarted => Volatile.Read(ref started);

    /// <summary>Subscribes <paramref name="send"/> to the view of agent <paramref name="agentId"/> of chat <paramref name="sessionId"/>:
    /// the whole view first, then changes. Dispose to leave; the last subscriber stops the watch (never the agent's work).</summary>
    public IDisposable Subscribe(string sessionId, long agentId, Action<JsonObject> send)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(send);
        var viewer = new Viewer(send);
        Feed feed;
        var created = false;
        lock (gate)
        {
            if (!feeds.TryGetValue((sessionId, agentId), out var existing))
            {
                existing = new Feed(this, sessionId, agentId);
                feeds[(sessionId, agentId)] = existing;
                created = true;
                Interlocked.Increment(ref started);
            }
            feed = existing;
            feed.Add(viewer);
        }
        if (created) feed.Start();
        else _ = feed.SendWholeAsync(viewer);
        return new Leave(() => Unsubscribe(feed, viewer));
    }

    private void Unsubscribe(Feed feed, Viewer viewer)
    {
        bool last;
        lock (gate)
        {
            last = feed.Remove(viewer) == 0;
            if (last && feeds.TryGetValue(feed.Key, out var current) && ReferenceEquals(current, feed)) feeds.Remove(feed.Key);
        }
        if (last) feed.Stop();
    }

    private void Refresh(string sessionId)
    {
        List<Feed> matching;
        lock (gate) matching = [.. feeds.Values.Where(feed => feed.Key.Session == sessionId)];
        foreach (var feed in matching) feed.Trigger();
    }

    /// <summary>Renders every feed again: some chat's running flag changed (the event does not say which).</summary>
    private void RefreshAll()
    {
        List<Feed> all;
        lock (gate) all = [.. feeds.Values];
        foreach (var feed in all) feed.Trigger();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        broker.Changed -= Refresh;
        bindings.Changed -= Refresh;
        device.ActivityChanged -= RefreshAll;
        List<Feed> all;
        lock (gate)
        {
            all = [.. feeds.Values];
            feeds.Clear();
        }
        foreach (var feed in all) feed.Stop();
        return ValueTask.CompletedTask;
    }

    /// <summary>The ids of the tool calls an item shows.</summary>
    internal static IEnumerable<string> CallIds(TranscriptItem item) => item switch
    {
        AssistantItem assistant => assistant.Blocks.OfType<ToolBlockView>().Select(block => block.Call.CallId),
        ToolItem tool => [tool.Call.CallId],
        _ => [],
    };

    /// <summary>What an item's HTML depends on: its JSON, its facts (time, usage, failure, restarts, the running tail), its calls' stored
    /// results and waiting approvals, the account label and billing, and its cost mark.</summary>
    internal static string Print(TranscriptItem item, ChatRenderContext context)
    {
        var json = Transcript.ToJson([item]).ToJsonString() + "|" + context.FactsOf(item);
        foreach (var callId in CallIds(item))
        {
            if (context.Approval(callId) is { } approval) json += "|approval:" + approval.Request.TaskId;
            if (context.Result(callId) is { } result) json += "|result:" + result;
        }
        return json + "|" + context.AccountLabel + "|" + context.Billing + "|" + context.Spend.Print(item);
    }

    private sealed class Viewer(Action<JsonObject> send)
    {
        public bool Fresh { get; set; } = true;

        public void Send(JsonObject frame)
        {
            try { send(frame); }
            catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException) { /* The tab went away. */ }
        }
    }

    private sealed class Leave(Action leave) : IDisposable
    {
        private Action? leave = leave;

        public void Dispose() => Interlocked.Exchange(ref leave, null)?.Invoke();
    }

    /// <summary>One agent's watch, model and rendered view.</summary>
    private sealed class Feed(AgentViewHub hub, string sessionId, long agentId)
    {
        private readonly CancellationTokenSource stop = new();
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly ViewThrottle render = new(Coalesce);
        private readonly Lock viewersGate = new();
        private readonly List<Viewer> viewers = [];
        private readonly Dictionary<string, (string Print, string Html)> items = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> panels = new(StringComparer.Ordinal);
        private readonly HashSet<string> sources = new(StringComparer.Ordinal);
        private List<string> order = [];
        private AgentModel model = new();
        private JsonObject state = [];
        private string? accountLabel;
        private string? accountFor;
        private bool accountRead;
        private bool rendered;
        private long sequence;

        public (string Session, long Agent) Key => (sessionId, agentId);

        public void Add(Viewer viewer)
        {
            lock (viewersGate) viewers.Add(viewer);
        }

        public int Remove(Viewer viewer)
        {
            lock (viewersGate)
            {
                viewers.Remove(viewer);
                return viewers.Count;
            }
        }

        public void Start()
        {
            _ = Task.Run(() => FollowAsync(stop.Token));
            _ = render.RunAsync(RenderAsync, failure => hub.logger.LogWarning(failure, "Rendering chat {Chat} agent {Agent} failed.", sessionId, agentId), stop.Token);
        }

        public void Trigger() => render.Trigger();

        public void Stop()
        {
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { /* Stopped. */ }
        }

        /// <summary>Sends the whole view to a viewer that joined a running feed (if the feed has rendered; else its first render does).</summary>
        public async Task SendWholeAsync(Viewer viewer)
        {
            try { await gate.WaitAsync(stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (!rendered || !viewer.Fresh) return;
                viewer.Send(Whole(new JsonArray("snapshot")));
                viewer.Fresh = false;
            }
            finally
            {
                gate.Release();
            }
        }

        private void Broadcast(JsonObject frame)
        {
            Viewer[] targets;
            lock (viewersGate) targets = [.. viewers];
            foreach (var viewer in targets) viewer.Send((JsonObject)frame.DeepClone());
        }

        private async Task FollowAsync(CancellationToken token)
        {
            var backoff = TimeSpan.FromSeconds(1);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!hub.device.IsOpen) await hub.device.WhenOpen.WaitAsync(token).ConfigureAwait(false);
                    var session = hub.device.Host.Sessions.FirstOrDefault(item => item.Id == sessionId);
                    if (session is null)
                    {
                        Broadcast(Fault(ChatErrors.NotFound, "This chat is no longer on this device."));
                        return;
                    }
                    await foreach (var frame in AgentFrames.ReadAsync(session, agentId, null, token).ConfigureAwait(false))
                    {
                        await gate.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            var type = ChatDisplay.Str(frame, "type") ?? "";
                            if (type == "snapshot") model = new AgentModel();
                            if (model.Apply(frame)) sources.Add(type);
                        }
                        finally
                        {
                            gate.Release();
                        }
                        render.Trigger();
                        backoff = TimeSpan.FromSeconds(1);
                    }
                    if (token.IsCancellationRequested) return;
                    Broadcast(new JsonObject { ["type"] = "disconnected", ["reason"] = "The chat closed." });
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (HostNotFoundException missing)
                {
                    Broadcast(Fault(ChatErrors.NotFound, missing.Message));
                    return;
                }
                catch (Exception failure)
                {
                    if (failure is not (DurableHostException or InvalidOperationException or ObjectDisposedException))
                        hub.logger.LogWarning(failure, "The view of chat {Chat} agent {Agent} failed.", sessionId, agentId);
                    Broadcast(new JsonObject { ["type"] = "disconnected", ["reason"] = failure.Message });
                }
                try { await Task.Delay(backoff, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(15).Ticks));
            }
        }

        private static JsonObject Fault(string code, string message) =>
            new() { ["type"] = "fault", ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

        private async Task RenderAsync()
        {
            await gate.WaitAsync(stop.Token).ConfigureAwait(false);
            try
            {
                if (!model.Ready || hub.bindings.Get(sessionId) is not { } binding) return;
                if (!accountRead || accountFor != binding.Provider + "/" + binding.AccountId)
                {
                    accountLabel = await hub.chats.AccountLabelAsync(binding, stop.Token).ConfigureAwait(false);
                    accountFor = binding.Provider + "/" + binding.AccountId;
                    accountRead = true;
                }
                var approvals = hub.broker.Pending(sessionId);
                var entries = model.Entries;
                var run = new ChatRunState(model.Running || model.Generating, model.Generating,
                    approvals.Any(approval => approval.Request.AgentId == agentId), model.Pending.Count > 0);
                var spend = await hub.chats.SpendFactsAsync(binding, entries, run.Running, accountLabel, stop.Token).ConfigureAwait(false);
                var context = hub.chats.RenderContext(binding, agentId, accountLabel, entries, model.Items, run, spend);
                var upsert = new JsonArray();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                var inline = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in model.Items)
                {
                    keys.Add(item.Key);
                    foreach (var callId in CallIds(item)) inline.Add(callId);
                    var print = Print(item, context);
                    if (items.TryGetValue(item.Key, out var known) && known.Print == print) continue;
                    var html = await hub.renderer.RenderAsync("~/Views/Chat/_ChatItem.cshtml", new ChatItemView(item, context)).ConfigureAwait(false);
                    items[item.Key] = (print, html);
                    upsert.Add(new JsonObject { ["id"] = item.Key, ["html"] = html });
                }
                var remove = new JsonArray();
                foreach (var gone in items.Keys.Where(key => !keys.Contains(key)).ToList())
                {
                    items.Remove(gone);
                    remove.Add(gone);
                }
                var newOrder = model.Items.Select(item => item.Key).ToList();
                var reordered = !newOrder.SequenceEqual(order, StringComparer.Ordinal);
                order = newOrder;

                var outside = approvals.Where(approval => !(approval.Request.AgentId == agentId && approval.Request.ParentCallId is null &&
                    inline.Contains(approval.Request.CallId))).ToList();
                var usage = Transcript.Sum(DurableWire.Read<UsageTotals>(model.Usage));
                var changedPanels = new JsonObject();
                await PanelAsync(changedPanels, "pending", "~/Views/Chat/_ChatPending.cshtml", new ChatPendingView(model.Pending, outside, agentId)).ConfigureAwait(false);
                await PanelAsync(changedPanels, "queue", "~/Views/Chat/_ChatQueue.cshtml", model.Inbox).ConfigureAwait(false);
                await PanelAsync(changedPanels, "notices", "~/Views/Chat/_ChatNotices.cshtml", new ChatNoticesView(model.Compactions.Count > 0,
                    model.CompactionFailure, model.Retry?.At, model.Retry?.Error)).ConfigureAwait(false);
                await PanelAsync(changedPanels, "banners", "~/Views/Chat/_ChatBanners.cshtml", hub.chats.Banners(binding, accountLabel, entries)).ConfigureAwait(false);
                await PanelAsync(changedPanels, "info", "~/Views/Chat/_ChatInfo.cshtml",
                    await hub.chats.InfoAsync(binding, agentId, entries, model.ContextTokens, usage, accountLabel, stop.Token).ConfigureAwait(false)).ConfigureAwait(false);

                var waiting = model.Pending.Count + approvals.Count;
                var nextState = new JsonObject
                {
                    ["running"] = model.Running,
                    ["busy"] = model.Running || model.Generating || waiting > 0 || hub.device.IsBusy(sessionId),
                    ["generating"] = model.Generating,
                    ["waiting"] = waiting,
                    ["approvals"] = approvals.Count,
                    ["queued"] = model.Inbox.Count,
                    ["compacting"] = model.Compactions.Count > 0,
                    ["compactionFailure"] = model.CompactionFailure,
                    ["contextTokens"] = model.ContextTokens,
                    ["retry"] = model.Retry is { } retry ? new JsonObject { ["at"] = retry.At, ["error"] = retry.Error } : null,
                    ["notices"] = new JsonArray([.. model.Notices.Select(notice => (JsonNode)notice)]),
                    ["title"] = binding.Title,
                    ["provider"] = binding.Provider,
                    ["model"] = binding.Model,
                    ["thinking"] = binding.Thinking,
                    ["tier"] = binding.Tier,
                    ["account"] = accountLabel,
                    ["usage"] = new JsonObject { ["input"] = usage.Input, ["output"] = usage.Output, ["cacheRead"] = usage.CacheRead, ["cost"] = usage.Cost },
                };
                var stateChanged = !JsonNode.DeepEquals(nextState, state);
                state = nextState;
                var source = new JsonArray([.. sources.Order(StringComparer.Ordinal).Select(type => (JsonNode)type)]);
                sources.Clear();
                sequence++;

                var delta = new JsonObject
                {
                    ["type"] = "view",
                    ["reset"] = false,
                    ["seq"] = sequence,
                    ["source"] = source.DeepClone(),
                    ["items"] = new JsonObject { ["upsert"] = upsert, ["remove"] = remove, ["order"] = new JsonArray([.. order.Select(key => (JsonNode)key)]) },
                    ["panels"] = changedPanels,
                    ["state"] = state.DeepClone(),
                };
                var changed = upsert.Count > 0 || remove.Count > 0 || reordered || changedPanels.Count > 0 || stateChanged || !rendered;
                Viewer[] targets;
                lock (viewersGate) targets = [.. viewers];
                JsonObject? whole = null;
                foreach (var viewer in targets)
                {
                    if (viewer.Fresh)
                    {
                        whole ??= Whole(source);
                        viewer.Send((JsonObject)whole.DeepClone());
                        viewer.Fresh = false;
                    }
                    else if (changed) viewer.Send((JsonObject)delta.DeepClone());
                }
                rendered = true;
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task PanelAsync(JsonObject changed, string name, string path, object model)
        {
            var html = await hub.renderer.RenderAsync(path, model).ConfigureAwait(false);
            if (panels.TryGetValue(name, out var previous) && previous == html) return;
            panels[name] = html;
            changed[name] = html;
        }

        /// <summary>The whole view, for a subscriber that has nothing yet.</summary>
        private JsonObject Whole(JsonArray source) => new()
        {
            ["type"] = "view",
            ["reset"] = true,
            ["seq"] = sequence,
            ["source"] = source.DeepClone(),
            ["items"] = new JsonObject
            {
                ["upsert"] = new JsonArray([.. order.Where(items.ContainsKey).Select(key => (JsonNode)new JsonObject { ["id"] = key, ["html"] = items[key].Html })]),
                ["remove"] = new JsonArray(),
                ["order"] = new JsonArray([.. order.Select(key => (JsonNode)key)]),
            },
            ["panels"] = new JsonObject([.. panels.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value))]),
            ["state"] = state.DeepClone(),
        };
    }
}

/// <summary>Runs work at most once per interval, never concurrently, and always catches up with the newest trigger (the hub's Throttle).</summary>
internal sealed class ViewThrottle(TimeSpan interval)
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
