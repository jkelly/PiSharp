// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/surfaces/client.ts.
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Surfaces;

public sealed record ClientError(string Kind, Exception? Error = null, HostError? Host = null,
    IReadOnlyList<PluginFailure>? Failed = null, MessagesReport? Messages = null);

/// <summary>The actual platform stylesheet adapter: prepare without applying, settle only accepted plugin versions.</summary>
public interface IClientStyles
{
    Task PrepareAsync(ScriptSet set, CancellationToken cancellationToken);
    void Settle(ScriptSet set, IReadOnlySet<string> kept);
}

public sealed class ClientOptions
{
    public required Contract Scripts { get; init; }
    public required string Base { get; init; }
    public IReadOnlyList<RegisteredScript> Initial { get; init; } = [];
    public IReadOnlyDictionary<string, object?> Modules { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyList<PluginFacet> Core { get; init; } = [];
    public required Func<ISocketEvents, ILink> Open { get; init; }
    /// <summary>The limits per part of what arrives from the server (default: an untrusted link's, 16 MiB on the wire).</summary>
    public PartLimits? Limits { get; init; }
    public required Func<ScriptEntry, CancellationToken, Task<RegisteredScript>> FetchScript { get; init; }
    /// <summary>The server's current base bundle hash, asked at each reconnect beside the next attempt (<c>null</c>: unknown, try again):
    /// another one means this client is outdated.</summary>
    public required Func<CancellationToken, Task<string?>> ServerBase { get; init; }
    /// <summary>This client's code is outdated: the app starts it over; the loop has stopped.</summary>
    public required Action Outdated { get; init; }
    /// <summary>Asked at each reconnect, beside the next attempt; <c>false</c> stops the loop (a guest's ended share).</summary>
    public Func<Task<bool>>? CanRetry { get; init; }
    /// <summary>Whether the client follows the scripts object (new sets applied, another base <c>Outdated</c>, the server's base asked at
    /// each reconnect): yes by default; <c>false</c> for a static client (built offline and served elsewhere).</summary>
    public bool Follow { get; init; } = true;
    public Action<ConnectionState>? OnConnection { get; init; }
    public IClientStyles? Styles { get; init; }
    public required ILoopEnvironment Environment { get; init; }
    public required Action<ClientError> OnError { get; init; }
    public ITelemetryContext? Telemetry { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>A real client Chord host whose admitted native scripts follow a remote script-set contract.</summary>
public sealed class Client : IAsyncDisposable
{
    private readonly ClientOptions options;
    private readonly LoadedFacets loaded;
    private readonly Dictionary<string, RegisteredScript> scripts = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource abort = new();
    private readonly object gate = new();
    private Task applying = Task.CompletedTask;
    private Task? stopping;
    private ScriptSet? latest;
    private Action? cancelRetry;
    private double retryDelay;
    private IReference? held;
    private Task following = Task.CompletedTask;
    private ConnectionLoop? loop;
    private bool outdated;
    public ChordHost Host { get; }

    private Client(ChordHost host, LoadedFacets loaded, ClientOptions options)
    {
        Host = host; this.loaded = loaded; this.options = options;
        RefreshAccepted(options.Initial);
    }

    /// <summary>Starts a client: its host loaded with the initial scripts, its link kept, its plugins kept current from the scripts object
    /// once the server answers. It completes before that: a client starts offline. A failure to follow is reported <c>update_failed</c>.</summary>
    public static async Task<Client> StartAsync(ClientOptions options, ChordContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        context ??= ChordContext.Background;
        var host = Hosts.Create(new HostOptions { OnError = error => Report(options, new("host", Host: error)),
            Telemetry = options.Telemetry, TimeProvider = options.TimeProvider });
        Client? client = null;
        try
        {
            var result = await LoadedFacets.LoadAsync(host, options.Core,
                options.Initial.Select(script => Registered.Entry(script, options.Modules)).ToArray(), context).ConfigureAwait(false);
            if (result.Report.Failed.Count > 0) Report(options, new("plugins", Failed: result.Report.Failed));
            client = new(host, result.Loaded, options);
            client.loop = ConnectionLoop.KeepConnected(new ConnectionOptions
            {
                Environment = options.Environment,
                Open = events =>
                {
                    var link = new SharedClose(options.Open(events));
                    // A client's parent is the server; the principal only labels the link (a client serves no remote types).
                    try { host.Connect(link, new LinkOptions { Principal = new() { ["kind"] = "server" }, Limits = options.Limits }, ChordContext.Background); }
                    catch { link.Close(); throw; }
                    return link;
                },
                OnState = state => { options.OnConnection?.Invoke(state); if (options.Follow && state == ConnectionState.Connected) client.Reconnected(); },
                CanRetry = async () =>
                {
                    if (client.abort.IsCancellationRequested) return false;
                    // A static client asks no base: it never learns it is outdated.
                    var serverBase = options.Follow ? await options.ServerBase(client.abort.Token).ConfigureAwait(false) : null;
                    // Stopped first, as Outdated promises: an attempt made meanwhile ends with it (MarkOutdated stops the loop).
                    if (serverBase is not null && serverBase != options.Base) { client.MarkOutdated(); return false; }
                    return options.CanRetry is null || await options.CanRetry().ConfigureAwait(false);
                },
            });
            // In the background: the scripts object is the server's, and a client starts (a page renders) without it, offline too.
            if (options.Follow) client.following = client.FollowInBackgroundAsync(context);
            return client;
        }
        catch
        {
            if (client is not null) await client.StopAsync(ChordContext.Background).ConfigureAwait(false);
            else await host.CloseAsync(ChordContext.Background).ConfigureAwait(false);
            throw;
        }
    }

    private async Task FollowInBackgroundAsync(ChordContext context)
    {
        try { await FollowAsync(context).ConfigureAwait(false); }
        catch (Exception error) { if (!abort.IsCancellationRequested) Report(options, new("update_failed", error)); }
    }

    private async Task FollowAsync(ChordContext context)
    {
        var ctx = ChordContext.WithAbortSignal(abort.Token, context);
        // Held, not got, as upstream holds the scripts object: a hold waits for the server however long it takes (a client starts
        // offline and follows once a link opens), where a get gives up at the host's ready timeout.
        var acquired = Host.Hold(options.Scripts, new(), ctx);
        lock (gate)
        {
            // Stopped meanwhile: StopCoreAsync found nothing to release.
            if (abort.IsCancellationRequested) { acquired.Release(); return; }
            held = acquired;
        }
        // Native member watches require a bound reference; await the hold's live state before acquiring one. A hold that lost its
        // binding between the two (the link dropped) waits for the next live state, as upstream's watch of a held member does.
        IWatchHandle<System.Text.Json.Nodes.JsonNode?> watch;
        while (true)
        {
            await LiveAsync(acquired, ctx).ConfigureAwait(false);
            try { watch = await acquired.Member("set").WatchAsync(ctx).ConfigureAwait(false); break; }
            catch (PiSharp.Chord.Data.ChordError) when (!abort.IsCancellationRequested && acquired.State.Value.Kind is "loading" or "failed") { }
        }
        // Native watches expose their acquisition value separately; Start delivers subsequent frames only.
        // Queue that value before installing the listener, so a newer queued frame can never be overwritten by it.
        var initial = UpdateAsync(ScriptSet.FromJson(watch.Value), ctx);
        watch.Start(async (call, frame) =>
        {
            if (call is WatchValueCall<System.Text.Json.Nodes.JsonNode?> value)
            {
                try { await UpdateAsync(ScriptSet.FromJson(value.Value), frame).ConfigureAwait(false); }
                catch (Exception error) { if (!abort.IsCancellationRequested) Report(options, new("update_failed", error)); }
            }
        });
        await initial.ConfigureAwait(false);
    }

    // Completes once `reference` is live; fails when it ends (released at stop) or the context is aborted.
    private static async Task LiveAsync(IReference reference, ChordContext context)
    {
        var live = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = await reference.State.WatchAsync(context).ConfigureAwait(false);
        try
        {
            watch.Start((call, _) =>
            {
                if (call is NativeWatchValueCall<RefState> { Value.Kind: "live" }) live.TrySetResult();
                return Task.CompletedTask;
            });
            // The acquisition value is no frame: a state reached before the listener was installed is read here.
            if (reference.State.Value.Kind == "live") live.TrySetResult();
            var first = await Task.WhenAny(live.Task, watch.Closed).ConfigureAwait(false);
            if (!ReferenceEquals(first, live.Task) && !live.Task.IsCompletedSuccessfully) throw new OperationCanceledException("the scripts object was released before it was live");
        }
        finally { _ = watch.StopAsync(); }
    }

    /// <summary>Completes once the client has begun following the scripts object (the set shown then was applied or superseded), or at once
    /// for a static client. PiSharp-only: upstream's tests poll.</summary>
    internal Task Followed => following;

    /// <summary>Supplies a newly published full set. Older queued sets are superseded; committed sets remain adopted.</summary>
    public Task UpdateAsync(ScriptSet set, ChordContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        PendingUpdate update;
        lock (gate)
        {
            if (abort.IsCancellationRequested || outdated) return Task.CompletedTask;
            latest = set; cancelRetry?.Invoke(); cancelRetry = null; retryDelay = 0;
            update = Enqueue(set, context ?? ChordContext.Background);
        }
        return Run(update);
    }

    // Only queue bookkeeping runs under gate. A completed predecessor or synchronous fetch must never
    // take user code or Dispatcher.Invoke into that lock: a dispatcher watch can enqueue concurrently.
    private PendingUpdate Enqueue(ScriptSet set, ChordContext context)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new PendingUpdate(set, context, applying, completion);
        applying = completion.Task;
        return queued;
    }
    private Task Run(PendingUpdate update)
    {
        _ = RunAsync();
        return update.Completion.Task;
        async Task RunAsync()
        {
            try
            {
                await update.Before.ConfigureAwait(false);
                lock (gate) if (!ReferenceEquals(latest, update.Set) || abort.IsCancellationRequested || outdated) return;
                if (!await ApplyAsync(update.Set, update.Context).ConfigureAwait(false)) RetryLater(update.Set, update.Context);
            }
            catch (Exception error) { if (!abort.IsCancellationRequested) Report(options, new("update_failed", error)); }
            finally { update.Completion.TrySetResult(); }
        }
    }
    private sealed record PendingUpdate(ScriptSet Set, ChordContext Context, Task Before, TaskCompletionSource Completion);

    private async Task<bool> ApplyAsync(ScriptSet set, ChordContext context)
    {
        if (set.Base != options.Base) { MarkOutdated(); return true; }
        var changed = !RunsAll(set);
        var fetched = new List<RegisteredScript>();
        try
        {
            if (changed)
            {
                foreach (var entry in set.Plugins)
                {
                    RegisteredScript? known;
                    lock (gate) scripts.TryGetValue(entry.Name, out known);
                    var script = known?.Digest == entry.Digest ? known : await options.FetchScript(entry, abort.Token).ConfigureAwait(false);
                    if (script.Plugin != entry.Name || script.Digest != entry.Digest) throw new InvalidOperationException($"Fetched script does not match {entry.Name} ({entry.Digest})");
                    fetched.Add(script);
                }
            }
            if (options.Styles is { } styles) await styles.PrepareAsync(set, abort.Token).ConfigureAwait(false);
            if (abort.IsCancellationRequested) return true;
            IReadOnlySet<string> kept = new HashSet<string>(StringComparer.Ordinal);
            if (changed)
            {
                ReloadReport report;
                try { report = await loaded.ReloadAsync(Host, fetched.Select(script => Registered.Entry(script, options.Modules)).ToArray(), context).ConfigureAwait(false); }
                finally { RefreshAccepted(fetched); }
                kept = report.Failed.Select(failure => failure.Plugin).ToHashSet(StringComparer.Ordinal);
                if (report.Failed.Count > 0) Report(options, new("plugins", Failed: report.Failed));
            }
            options.Styles?.Settle(set, kept);
            return true;
        }
        catch (Exception error)
        {
            if (!abort.IsCancellationRequested) Report(options, new("update_failed", error));
            return abort.IsCancellationRequested;
        }
    }

    private void RefreshAccepted(IReadOnlyList<RegisteredScript> candidates)
    {
        var accepted = loaded.Digests;
        lock (gate)
        {
            var previous = scripts.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            scripts.Clear();
            foreach (var (name, digest) in accepted)
            {
                var script = candidates.FirstOrDefault(candidate => candidate.Plugin == name && candidate.Digest == digest) ?? previous.GetValueOrDefault(name);
                if (script is not null && script.Digest == digest) scripts.Add(name, script);
            }
        }
    }
    private bool RunsAll(ScriptSet set)
    { lock (gate) return set.Plugins.Count == scripts.Count && set.Plugins.All(entry => scripts.TryGetValue(entry.Name, out var script) && script.Digest == entry.Digest); }
    private void Reconnected()
    {
        PendingUpdate? update = null;
        lock (gate) if (latest is { } set && !RunsAll(set) && !outdated) update = Enqueue(set, ChordContext.Background);
        // ConnectionLoop invokes state listeners while coordinating its own current attempt. Return
        // that notification before applying a reload, whose dispatcher may itself close the link.
        if (update is not null) _ = Task.Run(() => Run(update));
    }
    private void RetryLater(ScriptSet set, ChordContext context)
    {
        lock (gate)
        {
            if (abort.IsCancellationRequested || !ReferenceEquals(latest, set)) return;
            retryDelay = retryDelay == 0 ? 1000 : Math.Min(retryDelay * 2, 30000);
            cancelRetry?.Invoke();
            cancelRetry = options.Environment.After(retryDelay, () =>
            {
                PendingUpdate? update = null;
                lock (gate)
                {
                    cancelRetry = null;
                    if (!abort.IsCancellationRequested && ReferenceEquals(latest, set)) update = Enqueue(set, context);
                }
                if (update is not null) _ = Run(update);
            });
        }
    }
    private void MarkOutdated()
    {
        lock (gate)
        {
            if (outdated) return;
            outdated = true; cancelRetry?.Invoke(); cancelRetry = null;
        }
        loop?.Stop();
        options.Outdated();
    }
    public Task StopAsync(ChordContext? context = null)
    { lock (gate) return stopping ??= Task.Run(() => StopCoreAsync(context ?? ChordContext.Background)); }
    private async Task StopCoreAsync(ChordContext context)
    {
        loop?.Stop(); abort.Cancel();
        IReference? holding;
        lock (gate) { cancelRetry?.Invoke(); cancelRetry = null; holding = held; held = null; }
        holding?.Release();
        // The background follow ends with the abort (it reports nothing once stopped); its queued set is joined below.
        await following.ConfigureAwait(false);
        Task queued; lock (gate) queued = applying;
        await queued.ConfigureAwait(false);
        await Host.CloseAsync(context).ConfigureAwait(false);
        abort.Dispose();
    }
    public ValueTask DisposeAsync() => new(StopAsync());
    private static void Report(ClientOptions options, ClientError error) { try { options.OnError(error); } catch { } }

    private sealed class SharedClose : ILink, ILoopLink
    {
        private readonly ILink inner;
        private readonly object gate = new();
        private readonly List<Action> listeners = [];
        private bool closed;
        public SharedClose(ILink inner) { this.inner = inner; inner.OnClose(Closed); }
        public void Send(ReadOnlyMemory<byte> message) => inner.Send(message);
        public void OnMessage(Action<ReadOnlyMemory<byte>> listener) => inner.OnMessage(listener);
        public void Close() { inner.Close(); Closed(); }
        public void OnClose(Action listener)
        {
            lock (gate) { if (!closed) { listeners.Add(listener); return; } }
            listener();
        }
        private void Closed()
        {
            Action[] callbacks;
            lock (gate) { if (closed) return; closed = true; callbacks = [.. listeners]; listeners.Clear(); }
            foreach (var callback in callbacks) try { callback(); } catch { }
        }
    }
}
