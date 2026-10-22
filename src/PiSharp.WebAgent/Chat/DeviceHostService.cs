// Decision 0011 (run W2, WU7): the app's durable device: one DurableDeviceHost on device\ in this process, opened at start and closed at
// stop, with the IDurableModels the app registered (faux, or WU6's routed models). Its start directory is Settings › Tools' working
// directory; each chat passes its own working directory when it is created. Every session's registry is the durable default (coding tools,
// user-interaction, plan-mode and the device apps) plus web-approval, which reads Settings › Tools at every call. Its SessionStream answers
// {account, tier} from the chat binding (the host adds op). Model progress is committed about every 150 ms, which is what the pages stream
// (committed text only). No default model: a session whose model the device no longer knows is never moved to another one. The web agent
// defines no durable jobs and runs no schedules. The service also keeps each chat's running flag for the sidebar, refreshed from the
// device's coalesced changes.
using System.Collections.Concurrent;
using PiSharp.Durable;
using PiSharp.Durable.Hosting;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Chat;

/// <summary>Hosts the app's durable sessions. A singleton and hosted service.</summary>
public sealed class DeviceHostService(WebAgentOptions options, IDurableModels models, ISettingsStore settings, ChatBindings bindings,
    ApprovalBroker broker, ILogger<DeviceHostService> logger) : IHostedService, IAsyncDisposable
{
    /// <summary>How often model progress (partial messages and tool output) is committed, and so streamed, in milliseconds.</summary>
    public const double ProgressIntervalMs = 150;

    private readonly TaskCompletionSource<DurableDeviceHost> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, bool> busy = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stop = new();
    private DurableDeviceHost? host;
    private Task? watching;
    private int disposed;

    /// <summary>Raised after the running flag of some chats changed (coalesced), outside any lock.</summary>
    public event Action? ActivityChanged;

    /// <summary>The open host.</summary>
    /// <exception cref="InvalidOperationException">The service has not started, or has stopped.</exception>
    public DurableDeviceHost Host => Volatile.Read(ref host) ?? throw new InvalidOperationException("The durable device is not open.");

    /// <summary>Whether the host is open.</summary>
    public bool IsOpen => Volatile.Read(ref host) is not null;

    /// <summary>Completes when the host is open.</summary>
    public Task<DurableDeviceHost> WhenOpen => opened.Task;

    /// <summary>The device options this service opens the host with (also used by tests to open a host the same way).</summary>
    public static DurableDeviceOptions CreateOptions(string dataDirectory, string startDirectory, IDurableModels models, ISettingsStore settings,
        ChatBindings bindings, ApprovalBroker broker, Action<string?, Exception>? onReport = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(broker);
        var effects = ToolEffects.Default;
        return new DurableDeviceOptions
        {
            DataDirectory = dataDirectory,
            StartDirectories = [startDirectory],
            Models = models,
            DeviceName = "PiSharp Web Agent",
            Settings = new HarnessSettings { Progress = new ProgressSettings { PartialIntervalMs = ProgressIntervalMs, OutputIntervalMs = ProgressIntervalMs } },
            SessionStream = bindings.StreamFields,
            ToolEffects = effects,
            CreateRegistry = context =>
            {
                var registry = DurableDeviceHost.DefaultRegistry(context);
                registry.Install(WebApproval.Create(context.SessionId, broker, () => settings.Get<ToolsSettings>(), effects));
                return registry;
            },
            Jobs = [],
            RunSchedules = false,
            OnReport = onReport,
        };
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var tools = settings.Get<ToolsSettings>();
        var device = await DurableDeviceHost.OpenAsync(CreateOptions(options.DeviceDirectory, tools.EffectiveWorkingDirectory, models, settings, bindings, broker,
            (session, error) => logger.LogWarning(error, "Durable session {Session}: {Message}", session ?? "-", error.Message)), cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref host, device);
        opened.TrySetResult(device);
        foreach (var session in device.Sessions) _ = RefreshAsync(session.Id);
        watching = WatchAsync(device, stop.Token);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        await stop.CancelAsync().ConfigureAwait(false);
        if (watching is { } loop)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* Stopped. */ }
        }
        if (Interlocked.Exchange(ref host, null) is { } device) await device.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Whether chat <paramref name="sessionId"/> has work running (any agent busy), as last seen.</summary>
    public bool IsBusy(string sessionId) => busy.TryGetValue(sessionId, out var running) && running;

    /// <summary>Reads the running flag of <paramref name="sessionId"/> now (and raises <see cref="ActivityChanged"/> when it changed).</summary>
    public async Task<bool> RefreshAsync(string sessionId)
    {
        var running = false;
        try
        {
            if (Volatile.Read(ref host) is { } device && device.Sessions.FirstOrDefault(session => session.Id == sessionId) is { IsAvailable: true } session)
                running = (await session.AgentsAsync().ConfigureAwait(false)).Any(agent => agent.Busy);
        }
        catch (Exception error) when (error is DurableHostException or ObjectDisposedException or InvalidOperationException)
        {
            running = false;
        }
        var changed = busy.TryGetValue(sessionId, out var before) ? before != running : running;
        busy[sessionId] = running;
        if (changed) ActivityChanged?.Invoke();
        return running;
    }

    private async Task WatchAsync(DurableDeviceHost device, CancellationToken token)
    {
        try
        {
            await foreach (var changes in device.WatchChangesAsync(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false))
                foreach (var id in changes.Select(change => change.SessionId).OfType<string>().Distinct(StringComparer.Ordinal))
                    await RefreshAsync(id).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { /* Stopped. */ }
        catch (Exception error) when (error is ObjectDisposedException) { /* Closed. */ }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (Interlocked.Exchange(ref disposed, 1) == 0) stop.Dispose();
    }
}
