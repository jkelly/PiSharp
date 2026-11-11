// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/workers/base.ts, packages/chord/src/node/workers/workers.ts.
// Authored native regression; not an upstream execution capture.
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Plugins;
using PiSharp.Chord.Hosting.Workers;
using PiSharp.Chord.Link;

internal static class WorkerSnapshotCases
{
    private static readonly ChordContext Context = ChordContext.Background;
    private static readonly HostKind Kind = new("snapshot.worker", new() { ["id"] = Schemas.Str() });
    private static readonly Contract Probe = Contract.Remote("snapshot.probe", new()
    {
        Version = 1, Params = new() { ["id"] = Schemas.Str() }, HostAddress = p => Kind.Address(p),
        Variants = new() { ["reader"] = new() { Methods = new() { ["info"] = Schemas.Method(null, Schemas.Str()), ["lazy"] = Schemas.Method(null, Schemas.Str()) } } },
    });
    private static string Sdk => Environment.GetEnvironmentVariable("PISHARP_TEST_DOTNET") ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
        (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase) ? Environment.ProcessPath! : throw new InvalidOperationException("Run with pinned dotnet host"));

    internal static Task<int> RunChildAsync() => WorkerMain.RunAsync(async worker =>
    {
        var host = Hosts.Create(new() { Address = worker.Address(Kind), Principal = worker.Start.Principal, OnError = _ => { } });
        string LoadLazy() => Assembly.LoadFile(Path.Combine(AppContext.BaseDirectory, "lazy", "Closer.dll")).GetName().Name!;
        var facet = Facet.Define((h, _) => h.Serve(Probe, (_, _, self) =>
        {
            self.Open(_ => Task.FromResult(true), async (_, _, _) =>
            {
                if (worker.Start.Data?["closeStarted"]?.GetValue<string>() is not { } started) return;
                await File.WriteAllTextAsync(started, "started");
                var release = worker.Start.Data!["closeRelease"]!.GetValue<string>();
                while (!File.Exists(release)) await Task.Delay(20);
                await File.WriteAllTextAsync(worker.Start.Data!["closeResult"]!.GetValue<string>(), LoadLazy());
            }, new NativeOptions(CloseTimeout: TimeSpan.FromMilliseconds(100)));
            return new(new Dictionary<string, object?>
            {
                ["info"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create(Environment.ProcessId + "|" + AppContext.BaseDirectory))),
                ["lazy"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create(LoadLazy()))),
            }, "reader");
        }));
        await worker.LoadPluginsAsync(host, [new("snapshot.core", facet)]);
        return host;
    });

    public static async Task CapturesDeployment()
    {
        var root = Temp(); var source = Path.Combine(root, "source"); var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(Path.Combine(source, "private"));
        var executable = Path.Combine(source, "worker.exe"); var dependency = Path.Combine(source, "private", "lazy.dll");
        await File.WriteAllTextAsync(executable, "executable-v1"); await File.WriteAllTextAsync(dependency, "dependency-v1");
        using var snapshot = await WorkerBase.BuildAsync(new() { Executable = executable, Arguments = ["--flag"], BaseDirectory = cache });
        Check(!Directory.Exists(cache), "base build wrote a cache before first launch");
        Check(!snapshot.Inputs.HasChanged(), "fresh input hashes differ");
        await File.WriteAllTextAsync(executable, "executable-v2"); File.Delete(dependency);
        var launch = snapshot.PrepareLaunch();
        Check(await File.ReadAllTextAsync(launch.Executable) == "executable-v1", "executable was reread after build");
        Check(await File.ReadAllTextAsync(Path.Combine(launch.WorkingDirectory, "private", "lazy.dll")) == "dependency-v1", "lazy dependency was not retained");
        Check(snapshot.Inputs.HasChanged(), "deleted/changed snapshot input was not dirty");
        await File.WriteAllTextAsync(executable, "executable-v1"); await File.WriteAllTextAsync(dependency, "dependency-v1");
        Check(!snapshot.Inputs.HasChanged(), "identical restored input did not clear dirty state");
        var other = Path.Combine(cache, "successor"); Directory.CreateDirectory(other); File.WriteAllText(Path.Combine(other, "keep"), "keep");
        var own = snapshot.DirectoryPath!; snapshot.Dispose();
        Check(!Directory.Exists(own) && File.Exists(Path.Combine(other, "keep")), "snapshot removed another component's files");
        try { snapshot.PrepareLaunch(); throw new Exception("closed base materialized"); } catch (ObjectDisposedException) { }
        Directory.Delete(root, true);
    }

    public static async Task FailedBuildAndClose()
    {
        var root = Temp(); var cache = Path.Combine(root, "cache"); var starts = 0;
        await using var host = NewHost();
        await using var workers = new ProcessWorkers("snapshot.failed", host, Kind, new()
        {
            Executable = Path.Combine(root, "missing.exe"), BaseDirectory = cache, LockDirectory = root,
            Authorize = (_, _) => true, Principal = _ => new() { ["kind"] = "worker" }, OnError = (_, _) => { },
            Plugins = (_, _) => { Interlocked.Increment(ref starts); return Task.FromResult(new PluginBuildSet([], [])); },
        }, Context);
        Check(workers.Built is null, "constructor eagerly built base");
        var first = workers.BuildAsync(); Check(ReferenceEquals(first, workers.BuildAsync()), "build task was not cached");
        Check((await first).Kind == "failed" && workers.Built?.Error is FileNotFoundException, "build failure did not become a stable outcome");
        var health = Contract.Local("snapshot.health", new() { Version = 1, Params = [], Members = new() { ["read"] = Schemas.Method(null, Schemas.Int()) } });
        await host.LoadAsync([new("snapshot.health", Facet.Define((h, _) => h.Serve(health, (_, _, _) => new(new Dictionary<string, object?>
            { ["read"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create(1))) }))))], Context);
        using var reference = host.Hold(Probe, new() { ["id"] = "failed" }, Context);
        await Until(() => workers.State.Values.Any(state => state.Status == "failed"));
        Check(starts == 0, "failed base called per-address plugin/start callback");
        using var local = await host.GetAsync(health, new(), Context);
        Check(((JsonNode)(await local.CallAsync("read", null, Context))!).GetValue<int>() == 1, "failed worker base disrupted the server host");
        await workers.CloseAsync(); Check(!Directory.Exists(cache), "failed/closed build wrote cache files");
        Directory.Delete(root, true);
    }

    public static async Task CloseDuringBuild()
    {
        var root = Temp(); var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var executable = Path.Combine(source, "worker.exe"); await File.WriteAllBytesAsync(executable, new byte[1024 * 1024]);
        var cache = Path.Combine(root, "cache"); await using var host = NewHost();
        await using var workers = new ProcessWorkers("snapshot.closing", host, Kind, new()
        {
            Executable = executable, BaseDirectory = cache, LockDirectory = root, Authorize = (_, _) => true,
            Principal = _ => new() { ["kind"] = "worker" }, OnError = (_, _) => { }, Plugins = (_, _) => Task.FromResult(new PluginBuildSet([], [])),
        }, Context);
        var build = workers.BuildAsync(); await workers.CloseAsync();
        Check((await build).Kind == "built", "closing changed the cached build outcome");
        Check(!Directory.Exists(cache), "build completing after close wrote files");
        Check(ReferenceEquals(build, workers.BuildAsync()), "closed component started another build");
        Directory.Delete(root, true);
    }

    public static async Task LaterWorkerAndCancelledClose()
    {
        var root = Temp(); var source = Deploy(root); var cache = Path.Combine(root, "cache");
        var closeStarted = Path.Combine(root, "close.started"); var closeRelease = Path.Combine(root, "close.release"); var closeResult = Path.Combine(root, "close.result");
        await using var host = NewHost();
        await using var workers = Fleet(host, source, cache, root, data: address => address.Params["id"]!.GetValue<string>() == "late" ?
            new JsonObject { ["closeStarted"] = closeStarted, ["closeRelease"] = closeRelease, ["closeResult"] = closeResult } : null);
        Check((await workers.BuildAsync()).Kind == "built", "base failed to capture deployed fixture");
        await host.LoadAsync([], Context);
        using (var first = await host.GetAsync(Probe, new() { ["id"] = "first" }, Context))
            Check((await Text(first, "lazy")) == "PiSharp.Tools", "first worker could not load private DLL");
        await workers.StopAsync(Kind.Address(("id", JsonValue.Create("first"))));
        Directory.Delete(source, true);
        using var later = await host.GetAsync(Probe, new() { ["id"] = "late" }, Context);
        var info = (await Text(later, "info")).Split('|'); var snapshotDirectory = info[1];
        using var abort = new CancellationTokenSource();
        var close = workers.CloseAsync(ChordContext.WithAbortSignal(abort.Token, Context));
        await Until(() => File.Exists(closeStarted)); abort.Cancel();
        try { await close; throw new Exception("close waiter did not cancel"); } catch (OperationCanceledException) { }
        await Task.Delay(250); // Cross the native's public close deadline while its original is still pending.
        Check(Directory.Exists(snapshotDirectory), "cancelled close removed snapshot while closer was running");
        await File.WriteAllTextAsync(closeRelease, "release");
        await workers.CloseAsync();
        Check(await File.ReadAllTextAsync(closeResult) == "PiSharp.Tools", "late closer lost its private DLL");
        Check(!Directory.Exists(snapshotDirectory), "snapshot retained after process and original cleanup joined");
        later.Release(); Directory.Delete(root, true);
    }

    public static async Task LinkRecovery()
    {
        var root = Temp(); var source = Deploy(root); var errors = new ConcurrentQueue<Exception>(); var time = new Clock();
        await using var host = NewHost(); await using var workers = Fleet(host, source, Path.Combine(root, "cache"), root, errors: errors, time: time);
        await host.LoadAsync([], Context); using var held = await host.GetAsync(Probe, new() { ["id"] = "a" }, Context);
        var first = Pid(await Text(held, "info")); await Until(() => workers.State.Values.Any(state => state.Status == "running"));
        LoseLink(workers);
        await Until(() => workers.State.Values.Any(state => state.Status == "running" && state.ProcessId != first));
        await Until(() => held.State.Value.Kind == "live"); var second = Pid(await Text(held, "info"));
        Check(second != first && errors.OfType<WorkerProcessException>().Count(e => e.Kind == "worker_link_lost") == 1, "first lost link did not recover exactly once");
        time.Advance(TimeSpan.FromMinutes(1)); LoseLink(workers);
        await Until(() => workers.State.Values.Any(state => state.Status == "running" && state.ProcessId != second));
        await Until(() => held.State.Value.Kind == "live"); var third = Pid(await Text(held, "info"));
        Check(third != second, "recovery did not re-open at minute boundary");
        LoseLink(workers);
        await Until(() => workers.State.Values.Any(state => state.Status == "failed" && state.Error == "worker lost its link"));
        await Until(() => held.State.Value.Kind == "failed");
        Check(errors.OfType<WorkerProcessException>().Count(e => e.Kind == "worker_link_lost") == 2, "second link within window was recovered");
        held.Release(); await workers.CloseAsync(); Directory.Delete(root, true);
    }

    public static async Task IdleLockSerializesStart()
    {
        var root = Temp(); var source = Deploy(root); await using var host = NewHost();
        await using var workers = Fleet(host, source, Path.Combine(root, "cache"), root);
        var address = Kind.Address(("id", JsonValue.Create("locked")));
        using (var other = await ProcessAddressLock.AcquireAsync(address.Key, root))
            Check(await workers.TryLockIdleAsync(address, Context) is null, "external owner did not time out as busy");
        using var lease = await workers.TryLockIdleAsync(address, Context) ?? throw new Exception("idle address was not lockable");
        await host.LoadAsync([], Context); using var held = host.Hold(Probe, new() { ["id"] = "locked" }, Context);
        await Task.Delay(100); Check(workers.State.Values.All(state => state.ProcessId is null), "worker started while idle archive held its lock");
        lease.Dispose(); await Until(() => held.State.Value.Kind == "live");
        Check(await workers.TryLockIdleAsync(address, Context) is null, "running worker was misclassified as idle");
        held.Release(); await workers.CloseAsync(); Directory.Delete(root, true);
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan value) => Interlocked.Add(ref ticks, value.Ticks);
    }
    private static ChordHost NewHost() => Hosts.Create(new() { Address = new HostKind("snapshot.server").Address(), Principal = new() { ["kind"] = "server" }, OnError = _ => { }, ReadyTimeout = TimeSpan.FromSeconds(20) });
    private static ProcessWorkers Fleet(ChordHost host, string source, string cache, string locks,
        Func<Address, JsonNode?>? data = null, ConcurrentQueue<Exception>? errors = null, TimeProvider? time = null) => new("snapshot", host, Kind, new()
        {
            Executable = Sdk, Arguments = [Path.Combine(source, Path.GetFileName(Assembly.GetExecutingAssembly().Location)), "--worker-snapshot-fixture"],
            BaseDirectory = cache, LockDirectory = locks, Authorize = (_, _) => true, Principal = _ => new() { ["kind"] = "worker" },
            Plugins = (_, _) => Task.FromResult(new PluginBuildSet([], [])), Data = data,
            OnError = (_, error) => errors?.Enqueue(error), CloseTimeout = TimeSpan.FromSeconds(8), TimeProvider = time ?? TimeProvider.System,
        }, Context);
    private static string Deploy(string root)
    {
        var source = Path.Combine(root, "deployment"); var original = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        foreach (var path in Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(source, Path.GetRelativePath(original, path)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination);
        }
        Directory.CreateDirectory(Path.Combine(source, "lazy")); File.Copy(Path.Combine(source, "PiSharp.Tools.dll"), Path.Combine(source, "lazy", "Closer.dll"));
        return source;
    }
    private static void LoseLink(ProcessWorkers workers)
    {
        // Exactly the supervisor notification used when the host's heartbeat/protocol closes its link.
        var entries = (IDictionary)typeof(ProcessWorkers).GetField("entries", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(workers)!;
        var entry = entries.Values.Cast<object>().Single(); var child = entry.GetType().GetField("Child", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(entry)!;
        ((ILink)child.GetType().GetProperty("Link", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(child)!).Close();
    }
    private static int Pid(string info) => int.Parse(info.Split('|')[0], System.Globalization.CultureInfo.InvariantCulture);
    private static async Task<string> Text(IReference reference, string method) => ((JsonNode)(await reference.CallAsync(method, null, Context))!).GetValue<string>();
    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "chord-snapshot-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void Check(bool test, string error) { if (!test) throw new Exception(error); }
    private static async Task Until(Func<bool> test) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); while (!test()) await Task.Delay(20, timeout.Token); }
}
