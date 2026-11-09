using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
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
    public Action<Address, string, string>? OnOutput { get; init; }
    public required Action<Address, Exception> OnError { get; init; }
}
public sealed record ProcessWorkerState(string Status, int? ProcessId = null, string? Error = null, DateTimeOffset? StartedAt = null);
public sealed class WorkerProcessException(string message, string standardErrorTail, Exception? inner = null) : IOException(message, inner)
{ public string StandardErrorTail { get; } = standardErrorTail; }

/// <summary>Starts one native child on first routed use. Crashes stay failed until explicit Retry; stops are bounded.</summary>
public sealed class ProcessWorkers : IAsyncDisposable, IPluginRolloutStep
{
    private sealed class Entry(Address address)
    {
        internal Address Address { get; } = address;
        internal Task<Child>? Starting;
        internal Child? Child;
        internal string? Failure;
    }
    private sealed class OutputTail
    {
        private readonly object sync = new();
        private string value = "";
        internal string Value { get { lock (sync) return value; } }
        internal void Add(string text) { lock (sync) { value += text; if (value.Length > 64 * 1024) value = value[^65536..]; } }
    }
    private sealed class Child(OwnedChildProcess process, NamedPipeServerStream data, WorkerControl control, ObservedLink link, JsonObject principal, OutputTail tail)
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
            if (failures.Count > 0) throw new AggregateException(failures);
        }
    }
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = [];
    private readonly ProcessWorkerOptions options;
    private readonly CancellationTokenSource closing = new();
    private long nextReload;
    private Task? closeTask;
    private PluginBuildSet? currentBuild;
    public string Name { get; }
    public RouteHandle Route { get; }
    public Facet Facet { get; }
    public ProcessWorkers(string name, ChordHost host, HostKind kind, ProcessWorkerOptions options, ChordContext context)
    {
        Name = name; this.options = options;
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
        var tail = new OutputTail();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closing.Token); timeout.CancelAfter(options.StartTimeout);
        try
        {
            PluginBuildSet? shipped; lock (sync) shipped = currentBuild;
            var build = shipped ?? await options.Plugins(entry.Address, ChordContext.WithAbortSignal(timeout.Token, ChordContext.Background)).WaitAsync(timeout.Token).ConfigureAwait(false);
            var dataValue = options.StartData is { } makeData ? await makeData(entry.Address, ChordContext.WithAbortSignal(timeout.Token, ChordContext.Background)).WaitAsync(timeout.Token).ConfigureAwait(false) : options.Data?.Invoke(entry.Address);
            var dataName = "pisharp-chord-" + Guid.NewGuid().ToString("N"); var controlName = dataName + "-control";
            data = new(dataName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            controlPipe = new(controlName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(options.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            if (options.WorkingDirectory is { } workingDirectory) start.WorkingDirectory = workingDirectory;
            foreach (var argument in options.Arguments) start.ArgumentList.Add(argument);
            foreach (var (key, value) in options.Environment) { if (value is null) start.Environment.Remove(key); else start.Environment[key] = value; }
            start.Environment[WorkerMain.DataPipe] = dataName; start.Environment[WorkerMain.ControlPipe] = controlName;
            process = await OwnedChildProcess.StartAsync(start, timeout.Token).ConfigureAwait(false);
            process.OwnReader(ReadOutput(process.StandardOutput, entry.Address, "stdout", tail)); process.OwnReader(ReadOutput(process.StandardError, entry.Address, "stderr", tail));
            var connected = Task.WhenAll(data.WaitForConnectionAsync(timeout.Token), controlPipe.WaitForConnectionAsync(timeout.Token));
            var exited = process.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited) throw new IOException("worker exited before connecting");
            await connected.ConfigureAwait(false); control = new(controlPipe);
            var principal = options.Principal(entry.Address);
            await control.SendAsync(new("start", new(new(entry.Address.Kind, entry.Address.Params, entry.Address.Key), principal, options.ParentPrincipal, build, options.LockDirectory, dataValue)), timeout.Token).ConfigureAwait(false);
            if ((await control.ReceiveAsync(timeout.Token).ConfigureAwait(false)).Kind != "started") throw new IOException("worker did not start");
            var child = new Child(process, data, control, new(new FramedPipeLink(data, data, leaveOpen: true)), principal, tail);
            lock (sync) entry.Child = child;
            child.Link.Observe(() => _ = LinkLostAsync(entry, child));
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
            lock (sync) { if (closing.IsCancellationRequested) entries.Remove(entry.Address.Key); else entry.Failure = "worker failed to start"; }
            if (!closing.IsCancellationRequested) Report(entry.Address, new WorkerProcessException(error.Message, tail.Value, failures.Count == 1 ? error : new AggregateException(failures))); throw new IOException("worker failed to start");
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
        bool current;
        lock (sync)
        {
            current = ReferenceEquals(entry.Child, child);
            if (current)
            {
                entry.Child = null; entry.Starting = null;
                if (child.Stopping && !child.Faulted || closing.IsCancellationRequested)
                { if (entries.TryGetValue(entry.Address.Key, out var registered) && ReferenceEquals(registered, entry)) entries.Remove(entry.Address.Key); }
                else entry.Failure ??= cleanupFailure is null ? $"worker exited (code {child.Process.ExitCode})" : "worker cleanup failed";
            }
        }
        child.Link.Close();
        if (current && !child.Stopping && !closing.IsCancellationRequested) ReportChild(entry.Address, child, cleanupFailure ?? new IOException(entry.Failure));
        foreach (var reply in child.Replies.Values) reply.TrySetException(new IOException("worker exited"));
        child.Ready.TrySetException(new IOException("worker exited before becoming ready"));
        try { await child.CloseHandlesAsync().ConfigureAwait(false); } catch (Exception error) { if (current) ReportChild(entry.Address, child, error); }
    }
    private async Task LinkLostAsync(Entry entry, Child child)
    {
        lock (sync)
        {
            if (child.Stopping || closing.IsCancellationRequested || child.Process.LeaderExit.IsCompleted || !ReferenceEquals(entry.Child, child)) return;
            child.Faulted = true; entry.Failure = "worker link lost";
        }
        ReportChild(entry.Address, child, new IOException("worker link lost"));
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
                else if (message.Kind == "fatal") throw new IOException(message.Error ?? "worker failed");
                else throw new IOException("unexpected worker control message");
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            foreach (var reply in child.Replies.Values) reply.TrySetException(new IOException("worker control disconnected", error));
            child.Ready.TrySetException(new IOException("worker control disconnected", error));
            if (!child.Stopping && !closing.IsCancellationRequested) { ReportChild(entry.Address, child, error); await KillAsync(child.Process).ConfigureAwait(false); }
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
        lock (sync) if (entries.TryGetValue(address.Key, out var entry) && entry.Failure is not null) entries.Remove(address.Key);
        Route.Retry(address);
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
    private async Task StopChildAsync(Child child)
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
    public Task CloseAsync() { lock (sync) return closeTask ??= CloseCoreAsync(); }
    public Task CloseAsync(ChordContext context) => CloseAsync().WaitAsync(context.AbortSignal ?? CancellationToken.None);
    private async Task CloseCoreAsync()
    {
        await Task.Yield(); closing.Cancel(); Task<Child>[] starts;
        lock (sync) starts = entries.Values.Where(x => x.Starting is not null).Select(x => x.Starting!).ToArray();
        foreach (var start in starts) { try { await StopChildAsync(await start.ConfigureAwait(false)).ConfigureAwait(false); } catch (IOException) { } }
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
