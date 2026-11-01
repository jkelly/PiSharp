// PiSharp-only (decision 0008). One loop per registered device, after durable-pi's DeviceMonitor
// (apps/dpi-playground/apps/dpi-playground-hub/src/monitor.ts, MIT): probe /v1/device, classify the answer, then follow /v1/events for the
// device's tree. The status is checking, then online, offline, unauthorized (no token, or a rejected one), mismatch (another device answers
// at the address) or incompatible (another API version). Offline retries back off from 1 s to 30 s; states only a person can fix retry
// every 30 s or on Check. The last status, description and tree go to the registry, so an offline device keeps its stale tree.
using System.Collections.Concurrent;
using PiSharp.Durable.Client;

namespace PiSharp.Durable.Web.Hub;

/// <summary>A device's live state.</summary>
public sealed record LiveState(string Status, string? Error = null, DeviceInfo? Info = null, DeviceTree? Tree = null, long? LastSeenAt = null);

/// <summary>Watches every registered device.</summary>
public sealed class DeviceMonitor : BackgroundService
{
    private readonly DeviceRegistry registry;
    private readonly LocalDevice local;
    private readonly ILogger<DeviceMonitor> logger;
    private readonly ConcurrentDictionary<string, LiveState> live = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> loops = new();
    private readonly ConcurrentDictionary<string, long> persisted = new();
    private readonly Lock emitGate = new();
    private CancellationToken stopping;
    private Timer? emitTimer;

    public DeviceMonitor(DeviceRegistry registry, LocalDevice local, ILogger<DeviceMonitor> logger)
    {
        this.registry = registry;
        this.local = local;
        this.logger = logger;
        registry.Changed += id =>
        {
            if (registry.Get(id) is null) Forget(id);
            else Watch(id);
        };
    }

    /// <summary>Raised (at most every 50 ms) when any device's state changes.</summary>
    public event Action? Changed;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stopping = stoppingToken;
        local.Refresh();
        foreach (var device in registry.List()) Watch(device.Id);
        return Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { foreach (var loop in loops.Values) loop.Cancel(); }, TaskScheduler.Default);
    }

    /// <summary>(Re)starts a device's loop: its status goes back to checking.</summary>
    public void Watch(string id)
    {
        if (stopping.IsCancellationRequested) return;
        var loop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        if (loops.TryGetValue(id, out var previous)) previous.Cancel();
        loops[id] = loop;
        live[id] = (live.TryGetValue(id, out var state) ? state : new LiveState("checking")) with { Status = "checking", Error = null };
        Emit();
        _ = Task.Run(() => LoopAsync(id, loop.Token), CancellationToken.None);
    }

    /// <summary>Checks again now (after a token edit or a manual refresh).</summary>
    public void Check(string id)
    {
        if (id == LocalDevice.Id) local.Refresh();
        Watch(id);
    }

    public void Forget(string id)
    {
        if (loops.TryRemove(id, out var loop)) loop.Cancel();
        live.TryRemove(id, out _);
        Emit();
    }

    public LiveState? State(string id) => live.TryGetValue(id, out var state) ? state : null;

    /// <summary>Every device with its live state, or its last known one.</summary>
    public IReadOnlyList<NetworkDevice> Network() => [.. registry.List().Select(device =>
    {
        var state = State(device.Id);
        return new NetworkDevice
        {
            Id = device.Id,
            Name = device.Name,
            Kind = device.Kind,
            Url = device.Url,
            Source = device.Source == "ui" ? "ui" : "config",
            HasToken = device.HasToken,
            Status = state?.Status ?? device.Status ?? "checking",
            Error = state is null ? device.Error : state.Error,
            LastSeenAt = state?.LastSeenAt ?? device.SeenAt,
            Info = state?.Info ?? device.Info,
            Tree = state?.Tree ?? device.Tree,
        };
    })];

    /// <summary>Completes once the device is online, or the token is cancelled.</summary>
    public async Task WhenOnlineAsync(string id, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && State(id)?.Status != "online")
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler() => changed.TrySetResult();
            Changed += Handler;
            try { await Task.WhenAny(changed.Task, Task.Delay(5000, cancellationToken)).ConfigureAwait(false); }
            finally { Changed -= Handler; }
        }
    }

    /// <summary>The device once it leaves checking, waiting at most <paramref name="limit"/>.</summary>
    public async Task<NetworkDevice?> SettledAsync(string id, TimeSpan limit)
    {
        var end = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < end && State(id)?.Status == "checking") await Task.Delay(100).ConfigureAwait(false);
        return Network().FirstOrDefault(device => device.Id == id);
    }

    private void Emit()
    {
        lock (emitGate)
        {
            if (emitTimer is not null) return;
            emitTimer = new Timer(_ =>
            {
                lock (emitGate)
                {
                    emitTimer?.Dispose();
                    emitTimer = null;
                }
                try { Changed?.Invoke(); }
                catch (Exception failure) { logger.LogWarning(failure, "A network listener failed."); }
            }, null, 50, Timeout.Infinite);
        }
    }

    private void Update(string id, CancellationToken token, Func<LiveState, LiveState> change)
    {
        if (token.IsCancellationRequested) return;
        var previous = State(id) ?? new LiveState("checking");
        var next = change(previous);
        live[id] = next;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (previous.Status != next.Status || previous.Error != next.Error || now - persisted.GetValueOrDefault(id) > 10_000)
        {
            persisted[id] = now;
            try { registry.RecordStatus(id, next.Status, next.Error, next.LastSeenAt, next.Info, next.Tree); }
            catch (Exception failure) when (failure is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                logger.LogWarning(failure, "Could not record the status of {Device}.", id);
            }
        }
        Emit();
    }

    private async Task LoopAsync(string id, CancellationToken token)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!token.IsCancellationRequested)
        {
            var device = registry.Get(id);
            if (device is null) return;
            var wait = backoff;
            var secret = registry.Token(id);
            if (secret is null)
            {
                Update(id, token, state => state with { Status = "unauthorized", Error = $"No token for {id}. Enter the token that pisharp durable token prints on the device." });
                await Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                continue;
            }
            if (State(id)?.Status != "online") Update(id, token, state => state with { Status = "checking" });
            using var client = new DurableDeviceClient(new Uri(device.Url), secret);
            try
            {
                var info = await client.DeviceAsync(TimeSpan.FromSeconds(6), token).ConfigureAwait(false);
                if (info.ApiVersion != DurableWire.ApiVersion)
                {
                    Update(id, token, state => state with { Status = "incompatible", Error = $"{device.Url} speaks API version {info.ApiVersion}; this hub speaks version {DurableWire.ApiVersion}.", Info = info });
                    await Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                    continue;
                }
                if (device.DeviceUid is { } known && known != info.Uid)
                {
                    Update(id, token, state => state with
                    {
                        Status = "mismatch",
                        Error = $"{device.Url} now answers as \"{info.Name}\" (device {Short(info.Uid)}), not the device first seen there (device {Short(known)}).",
                    });
                    await Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                    continue;
                }
                registry.LearnUid(id, info.Uid);
                Update(id, token, state => state with { Status = "online", Error = null, Info = info, LastSeenAt = Now() });
                backoff = TimeSpan.FromSeconds(1);
                await foreach (var frame in client.StreamAsync("/v1/events", cancellationToken: token).ConfigureAwait(false))
                    if (frame.Type == "tree" && DurableWire.Read<DeviceTree>(frame.Data["tree"]) is { } tree)
                        Update(id, token, state => state with { Status = "online", Error = null, Tree = tree, LastSeenAt = Now() });
                if (token.IsCancellationRequested) return;
                Update(id, token, state => state with { Status = "offline", Error = "The event stream ended (is the device restarting?)", LastSeenAt = Now() });
                wait = TimeSpan.FromMilliseconds(500);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (DurableApiException failure) when (failure.Code == DurableErrorCodes.Unauthorized)
            {
                Update(id, token, state => state with { Status = "unauthorized", Error = "The device rejected its token." });
                wait = TimeSpan.FromSeconds(30);
            }
            catch (Exception failure) when (failure is DurableApiException or HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException)
            {
                var wasOnline = State(id)?.Status == "online";
                Update(id, token, state => state with { Status = "offline", Error = failure.Message, LastSeenAt = wasOnline ? Now() : state.LastSeenAt });
            }
            await Delay(wait, token).ConfigureAwait(false);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
        }
    }

    private static string Short(string uid) => uid.Length > 8 ? uid[..8] : uid;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static async Task Delay(TimeSpan delay, CancellationToken token)
    {
        try { await Task.Delay(delay, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* Stopped or rechecked. */ }
    }

    public override void Dispose()
    {
        foreach (var loop in loops.Values) loop.Cancel();
        emitTimer?.Dispose();
        base.Dispose();
    }
}
