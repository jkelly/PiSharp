// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/surfaces/client.ts.
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
    public required Func<ScriptEntry, CancellationToken, Task<RegisteredScript>> FetchScript { get; init; }
    public required Func<CancellationToken, Task<string?>> ServerBase { get; init; }
    public required Action Outdated { get; init; }
    public Func<Task<bool>>? CanRetry { get; init; }
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
    private ConnectionLoop? loop;
    private bool outdated;
    public ChordHost Host { get; }

    private Client(ChordHost host, LoadedFacets loaded, ClientOptions options)
    {
        Host = host; this.loaded = loaded; this.options = options;
        RefreshAccepted(options.Initial);
    }

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
                    try { host.Connect(link, new LinkOptions { Principal = new() { ["kind"] = "server" } }, ChordContext.Background); }
                    catch { link.Close(); throw; }
                    return link;
                },
                OnState = state => { options.OnConnection?.Invoke(state); if (state == ConnectionState.Connected) client.Reconnected(); },
                CanRetry = async () =>
                {
                    if (client.abort.IsCancellationRequested) return false;
                    var serverBase = await options.ServerBase(client.abort.Token).ConfigureAwait(false);
                    if (serverBase is not null && serverBase != options.Base) { client.MarkOutdated(); return false; }
                    return options.CanRetry is null || await options.CanRetry().ConfigureAwait(false);
                },
            });
            await client.FollowAsync(context).ConfigureAwait(false);
            return client;
        }
        catch
        {
            if (client is not null) await client.StopAsync(ChordContext.Background).ConfigureAwait(false);
            else await host.CloseAsync(ChordContext.Background).ConfigureAwait(false);
            throw;
        }
    }

    private async Task FollowAsync(ChordContext context)
    {
        var ctx = ChordContext.WithAbortSignal(abort.Token, context);
        // Native member watches require a ready reference; await the initial binding before acquiring one.
        held = await Host.GetAsync(options.Scripts, new(), ctx).ConfigureAwait(false);
        var watch = await held.Member("set").WatchAsync(ctx).ConfigureAwait(false);
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
        loop?.Stop(); abort.Cancel(); cancelRetry?.Invoke(); cancelRetry = null; held?.Release(); held = null;
        await applying.ConfigureAwait(false);
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
