using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Legacy;
using PiSharp.Chord.Hosting.Plugins;
using PiSharp.Chord.Link;
using PiSharp.Tools.Processes;

namespace PiSharp.Chord.Hosting.Workers;

public sealed class ProcessWorkerOptions
{
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    public string? WorkingDirectory { get; init; }
    public string? ApplicationDirectory { get; init; }
    public string? BaseDirectory { get; init; }
    public required Func<Address, ChordContext, Task<PluginBuildSet>> Plugins { get; init; }
    public required Func<JsonObject, Address, bool> Authorize { get; init; }
    public required Func<Address, JsonObject> Principal { get; init; }
    public JsonObject ParentPrincipal { get; init; } = new() { ["kind"] = "server" };
    public Func<Address, JsonNode?>? Data { get; init; }
    public Func<Address, ChordContext, Task<JsonNode?>>? StartData { get; init; }
    public required string LockDirectory { get; init; }
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public Action<Address, string, string>? OnOutput { get; init; }
    public required Action<Address, Exception> OnError { get; init; }
}
public sealed record ProcessWorkerState(string Status, int? ProcessId = null, string? Error = null, DateTimeOffset? StartedAt = null);
public sealed class WorkerProcessException(string message, string standardErrorTail, Exception? inner = null) : IOException(message, inner)
{
    public string StandardErrorTail { get; } = standardErrorTail;
    public string Kind { get; init; } = "worker_failed";
}

/// <summary>Starts one native child on first routed use. Crashes stay failed until explicit Retry; stops are bounded.</summary>
public sealed class ProcessWorkers : IAsyncDisposable, IPluginRolloutStep
{
    private sealed class Entry(Address address)
    {
        internal Address Address { get; } = address;
        internal Task<Child>? Starting;
        internal Child? Child;
        internal string? Failure;
        internal Task? Previous;
        internal DateTimeOffset? Recovered;
        internal TaskCompletionSource? IdleLease;
    }
    private sealed class OutputTail
    {
        private readonly object sync = new();
        private string value = "";
        internal string Value { get { lock (sync) return value; } }
        internal void Add(string text) { lock (sync) { value += text; if (value.Length > 64 * 1024) value = value[^65536..]; } }
    }
    private sealed class IdleLock(ProcessAddressLock addressLock, Action release) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { addressLock.Dispose(); } finally { release(); }
        }
    }
    private sealed class Child(OwnedChildProcess process, NamedPipeServerStream data, WorkerControl control, ObservedLink link, JsonObject principal, OutputTail tail, PrivatePipeDirectory addresses)
    {
        internal OwnedChildProcess Process { get; } = process;
        internal NamedPipeServerStream Data { get; } = data;
        internal WorkerControl Control { get; } = control;
        internal ObservedLink Link { get; } = link;
        internal JsonObject Principal { get; } = principal;
        internal OutputTail Tail { get; } = tail;
        internal DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        internal SemaphoreSlim Reload { get; } = new(1, 1);
        internal ConcurrentDictionary<long, TaskCompletionSource<WorkerMessage>> Replies { get; } = new();
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsReady;
        internal DateTimeOffset? IdleSince;
        internal bool Stopping;
        internal bool Faulted;
        internal int ErrorReported;
        internal int RolloutOwners;
        internal bool LinkClosed;
        internal bool Lost;
        internal Task? StoppingTask;
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object handleGate = new();
        private Task? handlesClosing;
        internal Task? ControlReader;
        internal Task CloseHandlesAsync() { lock (handleGate) return handlesClosing ??= CloseHandlesCoreAsync(); }
        private async Task CloseHandlesCoreAsync()
        {
            await Task.Yield();
            var failures = new List<Exception>();
            try { Link.Close(); } catch (Exception error) { failures.Add(error); }
            try { Data.Dispose(); } catch (Exception error) { failures.Add(error); }
            try { await Control.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            if (ControlReader is { } reader) try { await reader.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            try { await Process.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            try { addresses.Dispose(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0) throw new AggregateException(failures);
        }
    }
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = [];
    private readonly ProcessWorkerOptions options;
    private readonly string lockDirectory;
    private readonly CancellationTokenSource closing = new();
    private long nextReload;
    private Task? closeTask;
    private PluginBuildSet? currentBuild;
    private Task<WorkersBuilt>? building;
    private WorkersBuilt? built;
    private WorkerBase? workerBase;
    private readonly HashSet<Child> children = [];
    private readonly HashSet<Task> idleLeases = [];
    public string Name { get; }
    public RouteHandle Route { get; }
    public Facet Facet { get; }
    public WorkersBuilt? Built { get { lock (sync) return built; } }
    /// <summary>Captures the worker deployment once. Failure is an outcome and affects only this component's starts.</summary>
    public Task<WorkersBuilt> BuildAsync() { lock (sync) return building ??= BuildCoreAsync(); }
    private async Task<WorkersBuilt> BuildCoreAsync()
    {
        await Task.Yield();
        WorkersBuilt outcome;
        try
        {
            var snapshot = await WorkerBase.BuildAsync(new() { Executable = options.Executable, Arguments = options.Arguments,
                ApplicationDirectory = options.ApplicationDirectory, WorkingDirectory = options.WorkingDirectory, BaseDirectory = options.BaseDirectory }).ConfigureAwait(false);
            lock (sync)
            {
                if (closing.IsCancellationRequested) snapshot.Dispose(); else workerBase = snapshot;
            }
            outcome = new("built", snapshot.Inputs, Runtime: snapshot.Runtime);
        }
        catch (Exception error) { outcome = new("failed", Error: error); }
        lock (sync) built = outcome;
        return outcome;
    }
    public ProcessWorkers(string name, ChordHost host, HostKind kind, ProcessWorkerOptions options, ChordContext context)
    {
        Name = name; this.options = options; lockDirectory = Path.GetFullPath(options.LockDirectory);
        Route = host.Route(kind, new()
        {
            Authorize = (principal, address) => { try { return options.Authorize(principal, address); } catch (Exception error) { Report(address, error); return false; } },
            Resolve = ResolveAsync,
        }, context);
        var lifetime = Contract.Local(name + ".workers", new() { Version = 1, Params = [], Members = [] });
        Facet = PiSharp.Chord.Host.Facet.Define((facet, _) => facet.Serve(lifetime, (_, _, self) =>
        { self.Open(_ => Task.FromResult(this), async (_, _, _) => await CloseAsync().ConfigureAwait(false)); return new(new Dictionary<string, object?>()); }));
        if (context.AbortSignal is { } token) token.Register(() => _ = CloseAsync());
        _ = CheckIdleAsync();
    }
    public IReadOnlyDictionary<string, ProcessWorkerState> State
    {
        get { lock (sync) return entries.ToDictionary(x => x.Key, x => x.Value.Failure is { } error ? new ProcessWorkerState("failed", Error: error) : new ProcessWorkerState(x.Value.Child is null ? "starting" : x.Value.Child.Stopping ? "stopping" : x.Value.Child.IsReady ? "running" : "starting", x.Value.Child?.Process.Id, StartedAt: x.Value.Child?.StartedAt)); }
    }
    private async Task<ResolvedLink> ResolveAsync(Address address, ChordContext context)
    {
        Entry entry; Task<Child> start;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closing.IsCancellationRequested, this);
            if (!entries.TryGetValue(address.Key, out entry!)) entries.Add(address.Key, entry = new(address));
            if (entry.Failure is not null) throw new IOException(entry.Failure);
            start = entry.Starting ??= StartAsync(entry);
        }
        // The child remains admitted if the reference which initiated startup has disappeared.
        var child = await start.ConfigureAwait(false);
        return new(child.Link, new LinkOptions { Principal = child.Principal, Trusted = true });
    }
    private async Task<Child> StartAsync(Entry entry)
    {
        await Task.Yield();
        OwnedChildProcess? process = null; NamedPipeServerStream? data = null; WorkerControl? control = null; NamedPipeServerStream? controlPipe = null;
        PrivatePipeDirectory? addresses = null;
        var tail = new OutputTail();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        try
        {
            // A disconnected child's process and original cleanup finish before its successor starts.
            if (entry.IdleLease is { } lease) await lease.Task.WaitAsync(closing.Token).ConfigureAwait(false);
            if (entry.Previous is { } previous) await previous.WaitAsync(closing.Token).ConfigureAwait(false);
            lock (sync) if (entry.Failure is { } failure) throw new IOException(failure);
            var baseOutcome = await BuildAsync().WaitAsync(closing.Token).ConfigureAwait(false);
            if (baseOutcome.Kind != "built") throw new IOException("worker base build failed", baseOutcome.Error);
            timeout.CancelAfter(options.StartTimeout);
            PluginBuildSet? shipped; lock (sync) shipped = currentBuild;
            var build = shipped ?? await options.Plugins(entry.Address, ChordContext.WithAbortSignal(timeout.Token, ChordContext.Background)).WaitAsync(timeout.Token).ConfigureAwait(false);
            var dataValue = options.StartData is { } makeData ? await makeData(entry.Address, ChordContext.WithAbortSignal(timeout.Token, ChordContext.Background)).WaitAsync(timeout.Token).ConfigureAwait(false) : options.Data?.Invoke(entry.Address);
            addresses = PrivatePipeDirectory.Create();
            var dataName = addresses.Name("data"); var controlName = addresses.Name("control");
            data = new(dataName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            controlPipe = new(controlName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            WorkerLaunch launch;
            lock (sync) { closing.Token.ThrowIfCancellationRequested(); launch = workerBase!.PrepareLaunch(); }
            var start = new ProcessStartInfo(launch.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = launch.WorkingDirectory };
            foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
            foreach (var (key, value) in options.Environment) { if (value is null) start.Environment.Remove(key); else start.Environment[key] = value; }
            foreach (var key in new[] { "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_STARTUP_HOOKS" })
                if (start.Environment.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                    throw new InvalidOperationException($"{key} can load code outside the worker snapshot");
            start.Environment[WorkerMain.DataPipe] = dataName; start.Environment[WorkerMain.ControlPipe] = controlName;
            process = await OwnedChildProcess.StartAsync(start, timeout.Token).ConfigureAwait(false);
            process.OwnReader(ReadOutput(process.StandardOutput, entry.Address, "stdout", tail)); process.OwnReader(ReadOutput(process.StandardError, entry.Address, "stderr", tail));
            var connected = Task.WhenAll(data.WaitForConnectionAsync(timeout.Token), controlPipe.WaitForConnectionAsync(timeout.Token));
            var exited = process.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited) throw new IOException("worker exited before connecting");
            await connected.ConfigureAwait(false); control = new(controlPipe);
            var principal = options.Principal(entry.Address);
            await control.SendAsync(new("start", new(new(entry.Address.Kind, entry.Address.Params, entry.Address.Key), principal, options.ParentPrincipal, build, lockDirectory, dataValue)), timeout.Token).ConfigureAwait(false);
            if ((await control.ReceiveAsync(timeout.Token).ConfigureAwait(false)).Kind != "started") throw new IOException("worker did not start");
            var child = new Child(process, data, control, new(new FramedPipeLink(data, data, leaveOpen: true)), principal, tail, addresses);
            lock (sync) { entry.Child = child; children.Add(child); }
            child.Link.Observe(local => _ = LinkLostAsync(entry, child, local));
            child.ControlReader = ReadControlsAsync(entry, child);
            _ = WatchExit(entry, child);
            return child;
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            if (process is not null)
            {
                try { await KillAsync(process).ConfigureAwait(false); } catch (Exception cleanup) { failures.Add(cleanup); }
                try { await process.DisposeAsync().ConfigureAwait(false); } catch (Exception cleanup) { failures.Add(cleanup); }
            }
            try { if (control is not null) await control.DisposeAsync().ConfigureAwait(false); else controlPipe?.Dispose(); } catch (Exception cleanup) { failures.Add(cleanup); }
            try { data?.Dispose(); } catch (Exception cleanup) { failures.Add(cleanup); }
            try { addresses?.Dispose(); } catch (Exception cleanup) { failures.Add(cleanup); }
            string failure;
            lock (sync)
            {
                failure = entry.Failure ?? "worker failed to start";
                if (closing.IsCancellationRequested) entries.Remove(entry.Address.Key); else entry.Failure = failure;
            }
            if (!closing.IsCancellationRequested) Report(entry.Address, new WorkerProcessException(error.Message, tail.Value, failures.Count == 1 ? error : new AggregateException(failures))); throw new IOException(failure);
        }
    }
    private async Task ReadOutput(StreamReader reader, Address address, string stream, OutputTail tail)
    {
        var buffer = new char[4096];
        try
        {
            int count; while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
                var text = new string(buffer, 0, count); if (stream == "stderr") tail.Add(text);
                try { if (options.OnOutput is { } output) output(address, stream, text); else (stream == "stderr" ? Console.Error : Console.Out).Write(text); } catch { }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }
    private async Task WatchExit(Entry entry, Child child)
    {
        Exception? cleanupFailure = null;
        try { await child.Process.WaitForExitAsync().ConfigureAwait(false); } catch (Exception error) { cleanupFailure = error; }
        bool current, recovered = false;
        lock (sync)
        {
            current = ReferenceEquals(entry.Child, child);
            if (current)
            {
                entry.Child = null;
                if (!child.LinkClosed) entry.Starting = null;
                if (child.Lost && !closing.IsCancellationRequested)
                {
                    var now = options.TimeProvider.GetUtcNow();
                    recovered = cleanupFailure is null && (entry.Recovered is null || now - entry.Recovered >= TimeSpan.FromMinutes(1));
                    if (recovered) { entry.Recovered = now; entry.Failure = null; }
                    else entry.Failure = "worker lost its link";
                }
                else if (child.Stopping && !child.Faulted || closing.IsCancellationRequested)
                { if (entries.TryGetValue(entry.Address.Key, out var registered) && ReferenceEquals(registered, entry)) entries.Remove(entry.Address.Key); }
                else entry.Failure ??= cleanupFailure is null ? $"worker exited (code {child.Process.ExitCode})" : "worker cleanup failed";
            }
        }
        child.Link.Close();
        if (current && !closing.IsCancellationRequested)
        {
            if (recovered) Report(entry.Address, new WorkerProcessException("worker: link lost", child.Tail.Value) { Kind = "worker_link_lost" });
            else if (!child.Stopping || child.Faulted) ReportChild(entry.Address, child, cleanupFailure ?? new IOException(entry.Failure));
        }
        foreach (var reply in child.Replies.Values) reply.TrySetException(new IOException("worker exited"));
        child.Ready.TrySetException(new IOException("worker exited before becoming ready"));
        try { await child.CloseHandlesAsync().ConfigureAwait(false); } catch (Exception error) { if (current) ReportChild(entry.Address, child, error); }
        finally
        {
            lock (sync)
            {
                children.Remove(child);
                if (entry.Child is null && entry.Starting is null && entry.Failure is null && entry.IdleLease is null && entries.TryGetValue(entry.Address.Key, out var registered) && ReferenceEquals(registered, entry)) entries.Remove(entry.Address.Key);
            }
            child.Finished.TrySetResult();
        }
    }
    private async Task LinkLostAsync(Entry entry, Child child, bool local)
    {
        lock (sync)
        {
            if (child.Stopping || closing.IsCancellationRequested || !ReferenceEquals(entry.Child, child)) return;
            child.LinkClosed = true; entry.Starting = null; entry.Previous = child.Finished.Task;
        }
        // EOF often precedes the OS exit notification. Give voluntary exit a grace period so a crash remains
        // failed; a protocol/heartbeat closure is already known to be a lost link.
        if (!local && await Task.WhenAny(child.Process.LeaderExit, Task.Delay(options.CloseTimeout)).ConfigureAwait(false) == child.Process.LeaderExit) return;
        lock (sync)
        {
            if (child.Stopping || closing.IsCancellationRequested || child.Process.LeaderExit.IsCompleted || !ReferenceEquals(entry.Child, child)) return;
            child.Faulted = true; child.Lost = child.IsReady;
            if (!child.Lost) entry.Failure = "worker failed to start";
        }
        foreach (var reply in child.Replies.Values) reply.TrySetException(new IOException("worker: its link is gone"));
        child.Ready.TrySetException(new IOException("worker: its link is gone"));
        await StopChildAsync(child).ConfigureAwait(false);
    }
    private async Task ReadControlsAsync(Entry entry, Child child)
    {
        try
        {
            while (!closing.IsCancellationRequested)
            {
                var message = await child.Control.ReceiveAsync(closing.Token).ConfigureAwait(false);
                if (message.Kind == "ready")
                { lock (sync) { child.IsReady = true; child.IdleSince = message.Idle == true ? DateTimeOffset.UtcNow : null; } child.Ready.TrySetResult(); }
                else if (message.Kind == "idle") { lock (sync) child.IdleSince = message.Idle == true ? child.IdleSince ?? DateTimeOffset.UtcNow : null; }
                else if (message.Kind == "reloaded" && child.Replies.TryRemove(message.Id, out var reply)) reply.TrySetResult(message);
                else if (message.Kind == "fatal")
                {
                    lock (sync) { child.Faulted = true; entry.Failure = message.Error ?? "worker failed"; }
                    throw new IOException(entry.Failure);
                }
                else throw new IOException("unexpected worker control message");
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or JsonException)
        {
            foreach (var reply in child.Replies.Values) reply.TrySetException(new IOException("worker control disconnected", error));
            child.Ready.TrySetException(new IOException("worker control disconnected", error));
            if (!child.Stopping && !closing.IsCancellationRequested && !child.LinkClosed)
            {
                lock (sync) { child.Faulted = true; entry.Failure ??= "worker control disconnected"; }
                ReportChild(entry.Address, child, error); await KillAsync(child.Process).ConfigureAwait(false);
            }
        }
    }
    private async Task CheckIdleAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(closing.Token).ConfigureAwait(false))
            {
                Child[] idle;
                lock (sync) { idle = entries.Values.Select(x => x.Child).Where(x => x is { Stopping: false, IsReady: true, IdleSince: not null, RolloutOwners: 0 } && DateTimeOffset.UtcNow - x.IdleSince >= options.IdleTimeout).Cast<Child>().ToArray(); foreach (var child in idle) child.Stopping = true; }
                foreach (var child in idle) await StopChildAsync(child).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
    public void Retry(Address address)
    {
        lock (sync) if (entries.TryGetValue(address.Key, out var entry) && entry.Failure is not null)
        { entry.Failure = null; entry.Starting = null; }
        Route.Retry(address);
    }
    /// <summary>Reserves an idle address while an archive operation holds its cross-process lock.
    /// Returns null if a worker is already starting, running, or stopping. Routed starts wait until disposal.</summary>
    public async Task<IDisposable?> TryLockIdleAsync(Address address, ChordContext context)
    {
        Entry entry; TaskCompletionSource gate;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closing.IsCancellationRequested, this);
            if (!entries.TryGetValue(address.Key, out entry!)) entries.Add(address.Key, entry = new(address));
            if (entry.Child is not null || entry.Starting is not null || entry.IdleLease is not null) return null;
            entry.IdleLease = gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            idleLeases.Add(gate.Task);
        }
        void Release()
        {
            lock (sync)
            {
                if (ReferenceEquals(entry.IdleLease, gate)) entry.IdleLease = null;
                gate.TrySetResult();
                idleLeases.Remove(gate.Task);
                if (entry.Child is null && entry.Starting is null && entry.Failure is null && entries.TryGetValue(address.Key, out var current) && ReferenceEquals(current, entry)) entries.Remove(address.Key);
            }
        }
        using var waited = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(closing.Token, context.AbortSignal ?? CancellationToken.None, waited.Token);
            var held = await ProcessAddressLock.AcquireAsync(address.Key, lockDirectory, cancelled.Token).ConfigureAwait(false);
            if (closing.IsCancellationRequested) { held.Dispose(); throw new ObjectDisposedException(nameof(ProcessWorkers)); }
            return new IdleLock(held, Release);
        }
        catch (OperationCanceledException) when (waited.IsCancellationRequested && !closing.IsCancellationRequested && context.AbortSignal?.IsCancellationRequested != true)
        { Release(); return null; }
        catch { Release(); throw; }
    }
    public async Task StopAsync(Address address)
    {
        Route.Suspend(address); Entry? entry; lock (sync) entries.TryGetValue(address.Key, out entry);
        if (entry?.Starting is not { } start) return;
        Child child; try { child = await start.ConfigureAwait(false); } catch (IOException) { return; }
        await StopChildAsync(child).ConfigureAwait(false);
        lock (sync) if (entries.TryGetValue(address.Key, out var current) && ReferenceEquals(current, entry)) entries.Remove(address.Key);
    }
    public Task StopAsync(Address address, ChordContext context) => StopAsync(address).WaitAsync(context.AbortSignal ?? CancellationToken.None);
    private Task StopChildAsync(Child child)
    { lock (sync) return child.StoppingTask ??= StopChildCoreAsync(child); }
    private async Task StopChildCoreAsync(Child child)
    {
        child.Stopping = true;
        using var deadline = new CancellationTokenSource(options.CloseTimeout);
        try { await child.Control.SendAsync(new("shutdown"), deadline.Token).ConfigureAwait(false); await child.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) { await KillAsync(child.Process).ConfigureAwait(false); }
        await child.CloseHandlesAsync().ConfigureAwait(false);
    }
    public async Task<PluginRolloutStepReport> RolloutAsync(PluginBuildSet build, ChordContext context)
    {
        Address[] failed;
        lock (sync) { currentBuild = build; failed = entries.Values.Where(entry => entry.Failure is not null).Select(entry => entry.Address).ToArray(); }
        foreach (var address in failed) Retry(address);
        Entry[] targets; lock (sync) targets = entries.Values.Where(entry => entry.Starting is not null).ToArray();
        var hosts = await Task.WhenAll(targets.Select(async entry =>
        {
            // The underlying reply still owns its slot when the rollout caller stops waiting.
            try { return await ShipAsync(entry).WaitAsync(context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { return new PluginRolloutHost(entry.Address.Key, Error: error.Message); }
        })).ConfigureAwait(false);
        return new(Name, hosts);

        async Task<PluginRolloutHost> ShipAsync(Entry entry)
        {
            Child? child = null;
            try
            {
                child = await entry.Starting!.ConfigureAwait(false);
                Interlocked.Increment(ref child.RolloutOwners);
                try
                {
                    await child.Reload.WaitAsync(closing.Token).ConfigureAwait(false);
                    try
                    {
                        await child.Ready.Task.WaitAsync(closing.Token).ConfigureAwait(false);
                        if (child.Stopping && !child.Faulted) return new(entry.Address.Key, Stopped: true);
                        var id = Interlocked.Increment(ref nextReload);
                        var reply = new TaskCompletionSource<WorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                        child.Replies[id] = reply;
                        await child.Control.SendAsync(new("reload", Plugins: build, Id: id), closing.Token).ConfigureAwait(false);
                        var response = await reply.Task.ConfigureAwait(false);
                        if (response.Kind != "reloaded" || response.Id != id || response.Error is not null) throw new IOException(response.Error ?? "worker rejected rollout");
                        return new(entry.Address.Key, Error: response.Failures > 0 && build.Plugins.Count == 0 ? "worker plugin reload was refused" : null,
                            FailedPlugins: response.Failures > 0 ? build.Plugins.Select(plugin => plugin.Plugin.Manifest.Name).ToArray() : []);
                    }
                    finally { child.Reload.Release(); }
                }
                finally { Interlocked.Decrement(ref child.RolloutOwners); }
            }
            catch (Exception error)
            { return child is { Stopping: true, Faulted: false } ? new(entry.Address.Key, Stopped: true) : new(entry.Address.Key, Error: error.Message); }
        }
    }
    public Task CloseAsync()
    {
        lock (sync)
        {
            if (closeTask is not null) return closeTask;
            closing.Cancel();
            return closeTask = CloseCoreAsync();
        }
    }
    public Task CloseAsync(ChordContext context) => CloseAsync().WaitAsync(context.AbortSignal ?? CancellationToken.None);
    private async Task CloseCoreAsync()
    {
        await Task.Yield(); Task<Child>[] starts; Task[] leases; var failures = new List<Exception>();
        lock (sync)
        {
            starts = entries.Values.Where(x => x.Starting is not null).Select(x => x.Starting!).ToArray();
            leases = idleLeases.ToArray();
        }
        foreach (var start in starts)
        {
            Child child; try { child = await start.ConfigureAwait(false); } catch (IOException) { continue; }
            try { await StopChildAsync(child).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        }
        Child[] remaining; lock (sync) remaining = children.ToArray();
        foreach (var child in remaining) try { await StopChildAsync(child).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        await Task.WhenAll(remaining.Select(child => child.Finished.Task)).ConfigureAwait(false);
        await Task.WhenAll(leases).ConfigureAwait(false);
        if (failures.Count > 0) throw new AggregateException(failures);
        lock (sync) workerBase?.Dispose();
    }
    public ValueTask DisposeAsync() => new(CloseAsync());
    private void Report(Address address, Exception error) { try { options.OnError(address, error); } catch { } }
    private void ReportChild(Address address, Child child, Exception error) { if (Interlocked.Exchange(ref child.ErrorReported, 1) == 0) Report(address, new WorkerProcessException(error.Message, child.Tail.Value, error)); }
    private static async Task KillAsync(OwnedChildProcess process)
    {
        await process.StopAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
    }
}
