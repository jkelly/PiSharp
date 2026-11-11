// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/worker.ts.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Plugins;
using PiSharp.Chord.Hosting.Workers;
using PiSharp.Chord.Store;
using PiSharp.Chord.Surfaces;
using PiSharp.Chord.TelemetryIngest;

namespace PiSharp.Chord.Hosting.App;

/// <summary>The native application worker entry. Credentials arrive only over the private parent control pipe.</summary>
public static class WorkerApp
{
    public static Task<int> StartWorkerAsync(WorkerAppOptions options, CancellationToken cancellationToken = default) => RunAsync(options, cancellationToken);
    public static async Task<int> RunAsync(WorkerAppOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(options.OnError);
        if (!options.Kinds.ContainsKey(options.Facet)) throw new ArgumentException("worker facet kind is not configured", nameof(options));
        IStore? store = null; FilesService? files = null; ChordHost? host = null;
        var callbacks = new CallbackOwnership(); IDisposable? callbackRegistration = null;
        WorkerTelemetryPump? telemetry = null; TelemetryRecorder? recorder = null;
        var initializationSettled = false;
        void Report(Exception error) { try { options.OnError(error); } catch { } }
        var runtime = new PluginRuntimeOptions
        {
            Kind = options.Facet, SharedAssemblies = options.SharedAssemblies,
            Policy = new() { AppApi = options.Api, Kind = options.Facet, Kinds = options.Kinds },
        };
        try
        {
            return await WorkerMain.RunAsync(async worker =>
            {
                worker.OnClosing(async _ =>
                {
                    if (telemetry is not null) await telemetry.DisposeAsync().ConfigureAwait(false);
                });
                worker.OnClosed(async _ =>
                {
                    await callbacks.Drained.ConfigureAwait(false);
                    callbackRegistration?.Dispose();
                    if (files is not null) await files.DisposeAsync().ConfigureAwait(false);
                    if (store is not null) await store.CloseAsync(ChordContext.Background).ConfigureAwait(false);
                });
                try
                {
                if (worker.Start.Data is not JsonObject payload || payload["kit"] is not JsonObject kit)
                    throw new InvalidDataException("application worker expects a kit start payload");
                var directory = kit["dir"]?.GetValue<string>() ?? throw new InvalidDataException("worker directory is required");
                if (!Path.IsPathFullyQualified(directory)) throw new InvalidDataException("worker directory must be absolute");
                if (kit["facet"]?.GetValue<string>() != options.Facet) throw new InvalidDataException("worker facet does not match its entry");
                var principal = AppPrincipals.Require(kit["principal"] as JsonObject ?? throw new InvalidDataException("worker principal is required"));
                var server = AppPrincipals.Require(kit["server"] as JsonObject ?? throw new InvalidDataException("server principal is required"));
                if (!AppPrincipals.Same(principal, worker.Start.Principal) || !AppPrincipals.Same(server, worker.Start.ParentPrincipal))
                    throw new InvalidDataException("worker credentials do not match the private start envelope");
                Layout.PrivateDirectory(directory);
                store = await Layout.OpenStoreAsync(Path.Combine(directory, "store.sqlite"), worker.Context).ConfigureAwait(false);
                var address = worker.Address(options.Kind);
                if (kit["telemetry"]?.GetValue<bool>() == true)
                {
                    var scope = Guid.NewGuid().ToString(); recorder = new(() => scope, 10_000);
                }
                host = Hosts.Create(new()
                {
                    Address = address, Principal = principal, Store = store, Telemetry = recorder,
                    PrincipalAttributes = options.PrincipalAttributes,
                    CloseTimeout = Timeout(kit, "close", 30_000), ReadyTimeout = Timeout(kit, "ready", 60_000),
                    ReloadDrainTimeout = Timeout(kit, "reloadDrain", 5_000),
                    OnError = error => Report(error.Error ?? new InvalidOperationException(error.Kind)),
                });
                callbackRegistration = host.RegisterCallbackLifetime((_, _) => callbacks.Acquire());
                // This precedes files and all plugin setup: parent references may now wait for publication.
                worker.Connect(host);
                var caps = kit["files"] as JsonObject;
                files = await FilesService.OpenAsync((ChordStore)store, Path.Combine(directory, "files"), worker.Context,
                    new(caps?["maxFile"]?.GetValue<long>() ?? 256L * 1024 * 1024, caps?["maxTotal"]?.GetValue<long>() ?? 4L * 1024 * 1024 * 1024),
                    kit["uploads"]?.GetValue<int>() ?? 64).ConfigureAwait(false);
                var core = new List<PluginFacet>
                {
                    new("chord.app.worker-info", Info.WorkerInfoFacet(new(address, payload["app"]?.DeepClone()))),
                    new("chord.app.files", FilesFacet.Create(files)), new("chord.app.data", HostData.CreateFacet(store, Path.Combine(directory, "files"), server)),
                    new("chord.translations", SurfaceTranslations.Facet()),
                };
                if (options.CoreFacets is { } extra) core.AddRange(extra(new(host, store, address, directory, payload["app"]?.DeepClone())));
                var loaded = await worker.LoadPluginsAsync(host, core).ConfigureAwait(false);
                var failure = loaded.Failed.FirstOrDefault(failed => core.Any(item => item.Name == failed.Plugin));
                if (failure is not null) throw new PluginError("application worker core failed: " + failure.Plugin);
                if (recorder is not null)
                {
                    telemetry = new(TelemetrySenders.SendWorkerTelemetry(host, recorder, worker.Context));
                }
                return host;
                }
                finally { initializationSettled = true; }
            }, runtime, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (telemetry is not null) await telemetry.DisposeAsync().ConfigureAwait(false);
            if (initializationSettled)
            {
                if (host is not null) try { await host.CloseAsync(ChordContext.Background).ConfigureAwait(false); } catch (Exception error) { Report(error); }
                await callbacks.Drained.ConfigureAwait(false); callbackRegistration?.Dispose();
                if (files is not null) try { await files.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { Report(error); }
                if (store is not null) try { await store.CloseAsync(ChordContext.Background).ConfigureAwait(false); } catch (Exception error) { Report(error); }
            }
        }
    }
    private static TimeSpan Timeout(JsonObject kit, string name, double fallback)
    {
        var milliseconds = (kit["timeouts"] as JsonObject)?[name]?.GetValue<double>() ?? fallback;
        if (!double.IsFinite(milliseconds) || milliseconds <= 0) throw new InvalidDataException("invalid worker timeout");
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}

/// <summary>Owns periodic worker telemetry and a bounded final flush, including an already pending send.</summary>
public sealed class WorkerTelemetryPump : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly TelemetrySender sender;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task loop;
    private readonly TimeSpan flushTimeout;
    private Task? closed;
    public WorkerTelemetryPump(TelemetrySender sender, TimeSpan? interval = null, TimeSpan? flushTimeout = null)
    {
        this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
        this.flushTimeout = flushTimeout ?? TimeSpan.FromSeconds(1);
        var sendInterval = interval ?? TimeSpan.FromSeconds(1);
        if (sendInterval <= TimeSpan.Zero || this.flushTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        loop = RunAsync(sendInterval);
    }
    private async Task RunAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
                // Cancelling the public wait does not abandon the sender's owned original call.
                await sender.SendAsync().WaitAsync(stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }
    private async Task CloseAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.ConfigureAwait(false);
            await sender.SendAsync(true).WaitAsync(flushTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        finally
        {
            // Releases the held Chord reference, cancelling any pending ingest before host/store cleanup.
            sender.Stop(); stopping.Dispose();
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate) return new(closed ??= CloseAsync());
    }
}
