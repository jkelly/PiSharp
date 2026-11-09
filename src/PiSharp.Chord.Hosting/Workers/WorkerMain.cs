using System.IO.Pipes;
using System.Runtime.InteropServices;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Plugins;
using PiSharp.Chord.Link;

namespace PiSharp.Chord.Hosting.Workers;

public sealed class WorkerContext
{
    private readonly ILink link;
    private readonly PluginRuntimeOptions runtimeOptions;
    private readonly Func<Task> started;
    internal PluginRuntime? Runtime;
    internal bool Connected;
    internal ChordHost? ConnectedHost;
    internal Task Started = Task.CompletedTask;
    internal WorkerContext(WorkerStart start, ILink link, ChordContext context, PluginRuntimeOptions runtimeOptions, Func<Task> started)
    { Start = start; this.link = link; Context = context; this.runtimeOptions = runtimeOptions; this.started = started; }
    public WorkerStart Start { get; }
    public ChordContext Context { get; }
    public Address Address(HostKind kind)
    {
        if (kind.Kind != Start.Address.Kind) throw new ArgumentException("worker address belongs to another host kind", nameof(kind));
        var address = kind.Address(Start.Address.Parameters);
        if (address.Key != Start.Address.Key) throw new InvalidDataException("worker address key mismatch");
        return address;
    }
    public void Connect(ChordHost host, LinkOptions? options = null)
    {
        if (Connected) throw new InvalidOperationException("worker already connected");
        ConnectedHost = host;
        host.Connect(link, options ?? new LinkOptions { Principal = Start.ParentPrincipal, Trusted = true }, Context); Connected = true; Started = started();
    }
    public async Task<LoadReport> LoadPluginsAsync(ChordHost host, IReadOnlyList<PluginFacet>? core = null)
    {
        if (Runtime is not null) throw new InvalidOperationException("worker plugins already loaded");
        if (!Connected) Connect(host);
        await Started.ConfigureAwait(false);
        Runtime = new(host, runtimeOptions);
        return await Runtime.LoadAsync(Start.Plugins, core ?? [], Context).ConfigureAwait(false);
    }
}

/// <summary>Native child entry point. Pipe credentials are inherited environment values, never command-line secrets.</summary>
public static class WorkerMain
{
    internal const string DataPipe = "PISHARP_CHORD_DATA_PIPE";
    internal const string ControlPipe = "PISHARP_CHORD_CONTROL_PIPE";
    public static async Task<int> RunAsync(Func<WorkerContext, Task<ChordHost>> initialize, PluginRuntimeOptions? plugins = null, CancellationToken cancellationToken = default)
    {
        var dataName = Environment.GetEnvironmentVariable(DataPipe) ?? throw new InvalidOperationException("not started as a Chord worker");
        var controlName = Environment.GetEnvironmentVariable(ControlPipe) ?? throw new InvalidOperationException("missing Chord control pipe");
        await using var data = new NamedPipeClientStream(".", dataName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using var controlPipe = new NamedPipeClientStream(".", controlName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var signals = new WorkerSignals(lifetime);
        await Task.WhenAll(data.ConnectAsync(lifetime.Token), controlPipe.ConnectAsync(lifetime.Token)).ConfigureAwait(false);
        await using var control = new WorkerControl(controlPipe);
        var first = await control.ReceiveAsync(lifetime.Token).ConfigureAwait(false);
        if (first.Kind != "start" || first.Start is null) throw new InvalidDataException("worker expected start");
        using var addressLock = await ProcessAddressLock.AcquireAsync(first.Start.Address.Key, first.Start.LockDirectory, lifetime.Token).ConfigureAwait(false);
        using var link = new FramedPipeLink(data, data, leaveOpen: true);
        var observed = new ObservedLink(link); observed.Observe(lifetime.Cancel);
        ChordHost? host = null; WorkerContext? worker = null;
        Task<ChordHost>? initialization = null;
        var shutdownRequested = false;
        var messages = System.Threading.Channels.Channel.CreateUnbounded<WorkerMessage>(new() { SingleReader = true, SingleWriter = true });
        async Task Pump()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var message = await control.ReceiveAsync(lifetime.Token).ConfigureAwait(false);
                    if (message.Kind == "shutdown") { shutdownRequested = true; lifetime.Cancel(); return; }
                    await messages.Writer.WriteAsync(message, lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { lifetime.Cancel(); }
            finally { messages.Writer.TryComplete(); }
        }
        var pump = Pump();
        try
        {
            worker = new(first.Start, observed, ChordContext.WithAbortSignal(lifetime.Token, ChordContext.Background), plugins ?? new() { Kind = "worker" }, () => control.SendAsync(new("started"), lifetime.Token));
            initialization = initialize(worker);
            host = await initialization.WaitAsync(lifetime.Token).ConfigureAwait(false);
            if (!worker.Connected) throw new InvalidOperationException("worker must connect its host");
            if (!ReferenceEquals(host, worker.ConnectedHost)) throw new InvalidOperationException("worker must return its connected host");
            await worker.Started.ConfigureAwait(false);
            bool Idle(ActivityCounts activity) => activity.References == 0 && activity.Jobs == 0 && activity.Lingering == 0;
            var reloadBusy = 0;
            var activity = await host.Activity.WatchAsync(worker.Context).ConfigureAwait(false);
            await control.SendAsync(new("ready", Idle: Idle(activity.Value)), lifetime.Token).ConfigureAwait(false);
            activity.Start(async (call, _) => { if (call is NativeWatchValueCall<ActivityCounts> value) await control.SendAsync(new("idle", Idle: Volatile.Read(ref reloadBusy) == 0 && Idle(value.Value)), lifetime.Token).ConfigureAwait(false); });
            while (!lifetime.IsCancellationRequested)
            {
                var message = await messages.Reader.ReadAsync(lifetime.Token).ConfigureAwait(false);
                if (message.Kind != "reload" || message.Plugins is null) throw new InvalidDataException("unknown worker control message");
                try
                {
                    if (worker.Runtime is null) throw new InvalidOperationException("worker did not load a plugin runtime");
                    Interlocked.Exchange(ref reloadBusy, 1);
                    await control.SendAsync(new("idle", Idle: false), lifetime.Token).ConfigureAwait(false);
                    var report = await worker.Runtime.ReloadAsync(message.Plugins, worker.Context).ConfigureAwait(false);
                    await worker.Runtime.DrainRetiredAsync(worker.Context).ConfigureAwait(false);
                    await control.SendAsync(new("reloaded", Id: message.Id, Failures: report.Failed.Count), lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception error) { await control.SendAsync(new("reloaded", Id: message.Id, Error: error.Message), lifetime.Token).ConfigureAwait(false); }
                finally { Interlocked.Exchange(ref reloadBusy, 0); if (!lifetime.IsCancellationRequested) await control.SendAsync(new("idle", Idle: Idle(host.Activity.Value)), lifetime.Token).ConfigureAwait(false); }
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException) { return shutdownRequested || cancellationToken.IsCancellationRequested ? 0 : 1; }
        catch (Exception)
        {
            // Detailed exceptions stay in the child; the parent receives a stable public failure.
            try { await control.SendAsync(new("fatal", Error: "worker initialization failed"), CancellationToken.None).ConfigureAwait(false); } catch (IOException) { }
            return 1;
        }
        finally
        {
            lifetime.Cancel();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            foreach (var connected in new[] { host, worker?.ConnectedHost }.OfType<ChordHost>().Distinct())
                try { await connected.CloseAsync(ChordContext.Background).WaitAsync(cleanup.Token).ConfigureAwait(false); } catch (Exception) { }
            if (initialization is not null) try { await initialization.WaitAsync(cleanup.Token).ConfigureAwait(false); } catch (Exception) { }
            // Noncooperative startup is owned by this process until its bounded exit; never unload underneath its original task.
            if (initialization?.IsCompleted != false && worker?.Runtime is { } runtime)
                try { await runtime.DisposeAsync().AsTask().WaitAsync(cleanup.Token).ConfigureAwait(false); } catch (Exception) { }
            await pump.ConfigureAwait(false);
        }
    }
    private sealed class WorkerSignals : IDisposable
    {
        private readonly ConsoleCancelEventHandler cancel;
        private readonly List<PosixSignalRegistration> registrations = [];
        internal WorkerSignals(CancellationTokenSource lifetime)
        {
            cancel = (_, e) => { e.Cancel = true; lifetime.Cancel(); };
            Console.CancelKeyPress += cancel;
            if (!OperatingSystem.IsWindows())
                foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT })
                    registrations.Add(PosixSignalRegistration.Create(signal, context => { context.Cancel = true; lifetime.Cancel(); }));
        }
        public void Dispose() { Console.CancelKeyPress -= cancel; foreach (var registration in registrations) registration.Dispose(); }
    }
}
