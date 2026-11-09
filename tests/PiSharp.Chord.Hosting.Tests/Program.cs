using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Http;
using PiSharp.Chord.Hosting.Plugins;
using PiSharp.Chord.Hosting.Workers;
using PiSharp.Chord.Registries;
using PiSharp.Tools.Processes;

public static class PluginTestContracts
{
    public static TaskCompletionSource? Pause { get; set; }
    public static TaskCompletionSource? Entered { get; set; }
    public static TaskCompletionSource? HttpPause { get; set; }
    public static TaskCompletionSource? HttpEntered { get; set; }
    public static TaskCompletionSource<string>? HttpFinished { get; set; }
    public static TaskCompletionSource? Unloading { get; set; }
    public static TaskCompletionSource<string>? HostFinished { get; set; }
    public static TaskCompletionSource? NativeClosePause { get; set; }
    public static TaskCompletionSource? NativeCloseEntered { get; set; }
    public static TaskCompletionSource<string>? NativeCloseFinished { get; set; }
    public static TaskCompletionSource? NativeOpenPause { get; set; }
    public static TaskCompletionSource? NativeOpenEntered { get; set; }
    public static TaskCompletionSource<string>? NativeOpenFinished { get; set; }
    public static TaskCompletionSource? CrossPause { get; set; }
    public static TaskCompletionSource? CrossEntered { get; set; }
    public static TaskCompletionSource<string>? CrossFinished { get; set; }
    public static TaskCompletionSource? WrapperUnloading { get; set; }
    public static Registry<string, string[]> Tags { get; } = Registry.Define<string, string[]>("test.tags", new() { Version = 1 });
    public static Contract Counter { get; } = Contract.Local("test.counter", new() { Version = 1, Params = [], Members = new() { ["read"] = Schemas.Method(null, Schemas.Str()) } });
    public static Contract Native { get; } = Contract.Local("test.native", new() { Version = 1, Params = [], Members = [] });
    public static Contract CachedCounter { get; } = Contract.Local("test.cached.counter", new() { Version = 1, Params = [], Members = new() { ["read"] = Schemas.Method(null, Schemas.Str()) } });
    public static Facet CachedFacet { get; } = Facet.Define((host, _) => host.Serve(CachedCounter, (_, _, _) => new(new Dictionary<string, object?> { ["read"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create("cached"))) })));
    public static HostKind Worker { get; } = new("test.worker", new() { ["id"] = Schemas.Str() });
    public static HostKind Server { get; } = new("test.server");
    public static Contract ParentService { get; } = Contract.Remote("test.parent.ready", new()
    {
        Version = 1, Params = [], HostAddress = _ => Server.Address(),
        Variants = new() { ["reader"] = new() { Methods = new() { ["ready"] = Schemas.Method(null, Schemas.Int()) } } },
    });
    public static Contract Bootstrap { get; } = Contract.Local("test.worker.bootstrap", new() { Version = 1, Params = [], Members = [] });
    public static Contract Remote { get; } = Contract.Remote("test.worker.pid", new()
    {
        Version = 1, Params = new() { ["id"] = Schemas.Str() }, HostAddress = p => Worker.Address(p),
        Variants = new() { ["reader"] = new() { Methods = new() { ["pid"] = Schemas.Method(null, Schemas.Int()) } } },
    });
    // These declaration/native/wrapper delegates are deliberately compiled in the shared default assembly.
    public static Facet SharedPluginFacet(Action<IFacetHost, IFacetSelf> declare, Func<ChordContext, Task<string>> open,
        Func<string, NativeCloseReason, ChordContext, Task> close) => Facet.Define((host, self) =>
    {
        declare(host, self);
        host.Serve(Native, (_, _, owner) =>
        {
            owner.Open(context => open(context), (value, reason, context) => close(value, reason, context), new NativeOptions(CloseTimeout: TimeSpan.FromMilliseconds(50)));
            return new(new Dictionary<string, object?>());
        });
    });
    public static Facet SharedHttpWrapper(Func<Task<bool>> before, Action<bool, HttpResult> after) => Facet.Define((_, self) => self.Contribute(HttpRegistries.Http, new HttpContribution
    {
        Wraps = [new("held", async (_, next, _) => { var held = await before(); var result = await next(); after(held, result); return result; }, All: true)],
    }));
}

internal static class Program
{
    private static string Sdk => Environment.GetEnvironmentVariable("PISHARP_TEST_DOTNET") ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
        (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase) ? Environment.ProcessPath! : throw new InvalidOperationException("Run with the pinned dotnet host or set PISHARP_TEST_DOTNET"));
    private static readonly ChordContext Context = ChordContext.Background;
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--sleep-child")) { await Task.Delay(Timeout.InfiniteTimeSpan); return 0; }
        if (args.Length >= 2 && (args[0] == "--spawn-descendant" || args[0] == "build"))
        {
            var marker = args[0] == "build" ? await File.ReadAllTextAsync(args[1]) : args[1];
            var start = new ProcessStartInfo(Sdk) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add("--sleep-child");
            using var child = Process.Start(start) ?? throw new IOException("fixture descendant failed to start");
            await File.WriteAllTextAsync(marker, child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (args.Contains("--crash")) Process.GetCurrentProcess().Kill();
            return 0;
        }
        if (args.Contains("--worker")) return await WorkerMain.RunAsync(async worker =>
        {
            var host = Hosts.Create(new() { Address = worker.Address(PluginTestContracts.Worker), Principal = worker.Start.Principal, OnError = _ => { } });
            var bootstrapped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var facet = Facet.Define((h, _) =>
            {
                h.Serve(PluginTestContracts.Bootstrap, (_, _, self) =>
                {
                    self.Open(async context =>
                    {
                        using var parent = await host.GetAsync(PluginTestContracts.ParentService, new(), context);
                        var ready = await parent.CallAsync("ready", null, context);
                        bootstrapped.TrySetResult(); return ready;
                    }, async (_, _, _) => { if (worker.Start.Data?["closeMarker"]?.GetValue<string>() is { } marker) await File.WriteAllTextAsync(marker, "closed"); });
                    return new(new Dictionary<string, object?>());
                });
                h.Serve(PluginTestContracts.Remote, (_, _, self) =>
                {
                    self.Get(PluginTestContracts.ParentService, new());
                    return new(new Dictionary<string, object?> { ["pid"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create(Environment.ProcessId))) }, "reader");
                });
            });
            await worker.LoadPluginsAsync(host, [new("test.core", facet)]);
            await bootstrapped.Task.WaitAsync(worker.Context.AbortSignal ?? CancellationToken.None);
            if (args.Contains("--hold-startup")) await Task.Delay(Timeout.InfiniteTimeSpan);
            return host;
        });
        var cases = new (string Name, Func<Task> Run)[]
        {
            ("plugins.discovery-validates-and-orders", Discovery),
            ("plugins.rollout-continues-failures-and-stops-explicitly", RolloutOrder),
            ("plugins.new-failed-plugin-retains-dirty-mark", FailedAddedPlugin),
            ("plugins.builder-natural-exit-joins-descendant-pipes", BuilderDescendant),
            ("plugins.build-load-reload-failure-and-collectible-context", Plugins),
            ("http.routes-wraps-origin-and-anonymous-webhook", HttpRoutes),
            ("http.streaming-ranges-files-and-signed-cookies", HttpResponses),
            ("http.real-loopback-listener-and-websocket", HttpListener),
            ("http.close-drains-then-seals-active-request", HttpDrain),
            ("http.listener-failed-start-disposes-services", HttpFailedStart),
            ("workers.real-process-route-stop-restart", Workers),
            ("workers.startup-shutdown-closes-connected-host", WorkerStartupClose),
            ("processes.natural-and-crashed-leader-kill-descendants", Descendants),
            ("workers.address-lock-waits-and-cancels", AddressLock),
        };
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            var watch = Stopwatch.StartNew();
            Console.Error.WriteLine("START " + test.Name);
            var deadline = test.Name.StartsWith("plugins.build", StringComparison.Ordinal) ? TimeSpan.FromSeconds(150) : test.Name.StartsWith("workers.", StringComparison.Ordinal) ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(30);
            try { await test.Run().WaitAsync(deadline); results.Add(new { test.Name, status = "PASS_AUTHORED_NATIVE", ms = watch.ElapsedMilliseconds }); Console.Error.WriteLine($"PASS {test.Name} ({watch.ElapsedMilliseconds} ms)"); }
            catch (Exception error) { failures++; results.Add(new { test.Name, status = "FAIL", error = error.ToString() }); Console.Error.WriteLine($"FAIL {test.Name}: {error}"); }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { source = ChordNext.Commit, cases = cases.Length, failures, results }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }
    private static string Temp()
    { var path = Path.Combine(Environment.CurrentDirectory, "artifacts", "chord-hosting-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static ChordHost Host() => Hosts.Create(new() { OnError = _ => { }, Principal = new() { ["kind"] = "test" }, CloseTimeout = TimeSpan.FromSeconds(2), ReadyTimeout = TimeSpan.FromSeconds(10) });
    private static async Task Discovery()
    {
        var root = Temp();
        async Task Add(string path, string manifest) { Directory.CreateDirectory(Path.Combine(root, path)); await File.WriteAllTextAsync(Path.Combine(root, path, PluginScanner.ManifestFile), manifest); }
        await Add("a", "{\"name\":\"test.one\",\"assembly\":\"Plugin.dll\"}");
        await Add("b", "{\"name\":\"test.one\",\"assembly\":\"Plugin.dll\"}");
        await Add("c", "{\"name\":\"Bad Name\",\"assembly\":\"Plugin.dll\"}");
        await Add("d", "{\"name\":\"test.escape\",\"assembly\":\"../Escape.dll\"}");
        var result = await PluginScanner.DiscoverAsync([root]);
        Check(result.Plugins.Count == 1 && result.Problems.Count == 3, "discovery accepts first valid name and reports duplicates/escapes");
        try { PluginScanner.ContainedPath(root, "../outside"); throw new Exception("path escape accepted"); } catch (ArgumentException) { }
        if (!OperatingSystem.IsWindows())
        {
            var upper = Path.Combine(root, "Plugin"); var lower = Path.Combine(root, "plugin"); Directory.CreateDirectory(upper); Directory.CreateDirectory(lower);
            await File.WriteAllTextAsync(Path.Combine(lower, "Outside.dll"), "outside");
            try { PluginScanner.ContainedPath(upper, "../plugin/Outside.dll"); throw new Exception("case-sensitive sibling escape accepted"); } catch (ArgumentException) { }
        }
    }
    private static async Task Plugins()
    {
        var weak = await ExercisePlugins();
        for (var attempt = 0; attempt < 15 && weak.Any(context => context.IsAlive); attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(30); }
        Check(weak.Length >= 5 && weak.All(context => !context.IsAlive), "old collectible AssemblyLoadContexts still rooted after reload drain and close, including shared static facet ownership");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> ExercisePlugins()
    {
        var root = Temp(); var pluginDirectory = Path.Combine(root, "plugins", "counter"); Directory.CreateDirectory(pluginDirectory);
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var hostingPath = typeof(IChordPlugin).Assembly.Location; var chordPath = typeof(ChordHost).Assembly.Location;
        var contractPath = typeof(PiSharp.Contracts.Schema.JsonSchemaBuilder).Assembly.Location;
        var telemetryPath = typeof(PiSharp.Telemetry.ITelemetryContext).Assembly.Location;
        var project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>TestPlugin</AssemblyName><NoWarn>PISHARP_CHORD001;PISHARP_SCHEMA001;PISHARP_TELEMETRY001</NoWarn></PropertyGroup><ItemGroup>" +
            string.Join("", new[] { assemblyPath, hostingPath, chordPath, contractPath, telemetryPath }.Select(path => $"<Reference Include=\"{Path.GetFileNameWithoutExtension(path)}\"><HintPath>{System.Security.SecurityElement.Escape(path)}</HintPath></Reference>")) + "<Compile Remove=\"private/**/*.cs\"/><ProjectReference Include=\"private/Helper.csproj\"/><ProjectReference Include=\"private/late/Late.csproj\"/><FrameworkReference Include=\"Microsoft.AspNetCore.App\"/></ItemGroup></Project>";
        await File.WriteAllTextAsync(Path.Combine(pluginDirectory, "Plugin.csproj"), project);
        Directory.CreateDirectory(Path.Combine(pluginDirectory, "private", "leaf"));
        await File.WriteAllTextAsync(Path.Combine(pluginDirectory, "private", "Helper.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Remove=\"leaf/**/*.cs;late/**/*.cs\"/><ProjectReference Include=\"leaf/Leaf.csproj\"/></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(pluginDirectory, "private", "Helper.cs"), "public static class PrivateHelper { public static string Value => \"private:\" + PrivateLeaf.Value; }");
        await File.WriteAllTextAsync(Path.Combine(pluginDirectory, "private", "leaf", "Leaf.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Directory.CreateDirectory(Path.Combine(pluginDirectory, "private", "late"));
        await File.WriteAllTextAsync(Path.Combine(pluginDirectory, "private", "late", "Late.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(pluginDirectory, PluginScanner.ManifestFile), "{\"name\":\"test.plugin\",\"assembly\":\"TestPlugin.dll\",\"type\":\"TestPlugin\",\"project\":\"Plugin.csproj\"}");
        var builder = new PluginBuilder(new() { DotnetPath = Sdk, ArtifactDirectory = Path.Combine(root, "built"), PackageDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts", "nuget-cache"), Restore = true });
        await using var manager = new PluginManager([Path.Combine(root, "plugins")], builder);
        var host = Hosts.Create(new() { OnError = _ => { }, Principal = new() { ["kind"] = "test" }, CloseTimeout = TimeSpan.FromMilliseconds(50), ReadyTimeout = TimeSpan.FromSeconds(10) });
        var http = new ChordHttp(host, new() { OnError = _ => { } });
        var runtime = new PluginRuntime(host, new() { SharedAssemblies = [Assembly.GetExecutingAssembly()], CallbackDrainTimeout = TimeSpan.FromMilliseconds(150) });
        Exception? bodyFailure = null;
        try
        {
        Task<BuiltPlugin> BuildWrapper(string generation) => BuildPlainPlugin(Temp(), "HeldWrapper", """
            using PiSharp.Chord.Host; using PiSharp.Chord.Hosting.Plugins; using PiSharp.Chord.Hosting.Http;
            using System.Runtime.Loader;
            public sealed class HeldWrapper : IChordPlugin {
                public const string Generation = "GENERATION";
                public Facet CreateFacet(PluginContext _) {
                    AssemblyLoadContext.GetLoadContext(typeof(HeldWrapper).Assembly)!.Unloading += _ => PluginTestContracts.WrapperUnloading?.TrySetResult();
                    return PluginTestContracts.SharedHttpWrapper(async () => {
                        if (PluginTestContracts.CrossPause is {} pause) { PluginTestContracts.CrossEntered!.TrySetResult(); await pause.Task; AssemblyLoadContext.GetLoadContext(typeof(HeldWrapper).Assembly)!.LoadFromAssemblyPath(typeof(HeldWrapper).Assembly.Location); return true; }
                        return false;
                    }, (held,response) => { if (held) PluginTestContracts.CrossFinished!.TrySetResult(((HttpResult.Text)response).Body); });
                }
            }
            """.Replace("GENERATION", generation, StringComparison.Ordinal), "test.wrapper");
        var wrapper = await BuildWrapper("one");
        Task<BuiltPlugin> BuildCached(string generation) => BuildPlainPlugin(Temp(), "CachedEntry", "public sealed class CachedEntry : PiSharp.Chord.Hosting.Plugins.IChordPlugin { public const string Generation = \"" + generation + "\"; public PiSharp.Chord.Host.Facet CreateFacet(PiSharp.Chord.Hosting.Plugins.PluginContext _) => PluginTestContracts.CachedFacet; }", "test.cached");
        var cached = await BuildCached("one");
        PluginBuildSet WithWrapper(PluginBuildSet build) => new(build.Plugins.Append(wrapper).Append(cached).ToArray(), build.Problems);
        async Task Write(string value)
        {
            await File.WriteAllTextAsync(Path.Combine(pluginDirectory, "private", "leaf", "Leaf.cs"), "public static class PrivateLeaf { public static string Value => \"leaf:" + value + "\"; }");
            await File.WriteAllTextAsync(Path.Combine(pluginDirectory, "private", "late", "Late.cs"), "public static class PrivateLate { public static string Value => \"late:" + value + "\"; }");
            await File.WriteAllTextAsync(Path.Combine(pluginDirectory, "Plugin.cs"), """
            using System.Text.Json.Nodes;
            using PiSharp.Chord.Host;
            using PiSharp.Chord.Hosting.Plugins;
            using PiSharp.Chord.Hosting.Http;
            using System.Runtime.CompilerServices;
            using System.Runtime.Loader;
            public sealed class TestPlugin : IChordPlugin {
                public Facet CreateFacet(PluginContext context) {
                    AssemblyLoadContext.GetLoadContext(typeof(TestPlugin).Assembly)!.Unloading += _ => PluginTestContracts.Unloading?.TrySetResult();
                    return PluginTestContracts.SharedPluginFacet((h,s) => {
                    s.Contribute(PluginTestContracts.Tags, "VALUE");
                    s.Contribute(HttpRegistries.Http, new HttpContribution { Routes = new Dictionary<string,HttpRoute> {
                        ["GET /leased"] = new(async (_,_) => { PluginTestContracts.HttpEntered!.TrySetResult(); await PluginTestContracts.HttpPause!.Task; var late = Late(); PluginTestContracts.HttpFinished!.TrySetResult(late); return new HttpResult.Text(late); })
                    } });
                    h.Serve(PluginTestContracts.Counter, (_,_,_) => new(new Dictionary<string,object?> {
                        ["read"] = (HostMethod)(async (_,_) => { PluginTestContracts.Entered?.TrySetResult(); if (PluginTestContracts.Pause is { } pause) { await pause.Task; PluginTestContracts.HostFinished?.TrySetResult(Late()); } return JsonValue.Create(PrivateHelper.Value); })
                    }));
                    }, async _ => { var version = "VALUE"; if (version == "three" && PluginTestContracts.NativeOpenPause is {} pause) { PluginTestContracts.NativeOpenEntered!.TrySetResult(); await pause.Task; PluginTestContracts.NativeOpenFinished!.TrySetResult(Late()); } return version; }, async (_,_,_) => { if (PluginTestContracts.NativeClosePause is {} pause) { PluginTestContracts.NativeCloseEntered!.TrySetResult(); await pause.Task; PluginTestContracts.NativeCloseFinished!.TrySetResult(Late()); } });
                }
                [MethodImpl(MethodImplOptions.NoInlining)] private static string Late() => PrivateLate.Value;
            }
            """.Replace("VALUE", value, StringComparison.Ordinal));
        }
        await Write("one"); var built = await manager.BuildAsync();
        Check(built.Plugins.Count == 1 && built.Plugins[0].Error is null, "real plugin compilation failed: " + built.Plugins.FirstOrDefault()?.Error);
        var core = Facet.Define((h, _) => h.Serve(PluginTestContracts.Tags, new RegistryProvider<string, string[]> { Compose = (entries, _) => entries.Select(x => x.Value).ToArray() }));
        Check((await runtime.LoadAsync(WithWrapper(built), [new("test.core", core), new("chord.http", http.Facet)], Context)).Failed.Count == 0, "plugin load refused");
        using var reference = await host.GetAsync(PluginTestContracts.Counter, new(), Context);
        using var cachedReference = await host.GetAsync(PluginTestContracts.CachedCounter, new(), Context);
        Check(((JsonNode)(await cachedReference.CallAsync("read", null, Context))!).GetValue<string>() == "cached", "cached shared declaration did not load");
        Check(((JsonNode)(await reference.CallAsync("read", null, Context))!).GetValue<string>() == "private:leaf:one", "private/transitive dependency was not isolated in plugin generation");
        Check((await host.RegistryAsync(PluginTestContracts.Tags, Context)).Value.SequenceEqual(["one"]), "shared registry identity failed");
        var unchanged = await manager.BuildAsync();
        Check(unchanged.Plugins[0].Digest == built.Plugins[0].Digest, "unchanged plugin produced a different generation digest");
        Check((await runtime.ReloadAsync(WithWrapper(unchanged), Context)).Plugins.Changed.Count == 0, "unchanged facet was replaced");
        await Write("two"); var changed = await manager.BuildAsync();
        wrapper = await BuildWrapper("two");
        cached = await BuildCached("two");
        PluginTestContracts.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); PluginTestContracts.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PluginTestContracts.HostFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PluginTestContracts.NativeClosePause = new(TaskCreationOptions.RunContinuationsAsynchronously); PluginTestContracts.NativeCloseEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); PluginTestContracts.NativeCloseFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCall = reference.CallAsync("read", null, Context); await PluginTestContracts.Entered.Task;
        PluginTestContracts.HttpEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); PluginTestContracts.HttpPause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PluginTestContracts.HttpFinished = new(TaskCreationOptions.RunContinuationsAsynchronously); PluginTestContracts.Unloading = new(TaskCreationOptions.RunContinuationsAsynchronously); PluginTestContracts.WrapperUnloading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var requestCancellation = new CancellationTokenSource(); var leasedRequest = Request("GET", "/leased"); leasedRequest.RequestAborted = requestCancellation.Token;
        var httpOriginalWait = http.HandleAsync(leasedRequest); await PluginTestContracts.HttpEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        PluginTestContracts.CrossPause = new(TaskCreationOptions.RunContinuationsAsynchronously); PluginTestContracts.CrossEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); PluginTestContracts.CrossFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var crossCancellation = new CancellationTokenSource(); var crossRequest = Request("GET", "/leased"); crossRequest.RequestAborted = crossCancellation.Token;
        var crossWait = http.HandleAsync(crossRequest); await PluginTestContracts.CrossEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var reloaded = await runtime.ReloadAsync(WithWrapper(changed), Context); Check(reloaded.Failed.Count == 0, "reload refused");
        Check(((JsonNode)(await cachedReference.CallAsync("read", null, Context))!).GetValue<string>() == "cached", "digest-changing evaluation of shared static Facet altered factory semantics");
        await PluginTestContracts.NativeCloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try { await oldCall.WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("old request ignored bounded host sealing"); } catch (OperationCanceledException) { } catch (PiSharp.Chord.Data.ChordError) { }
        var retirement = runtime.DrainRetiredAsync(Context);
        await Task.Delay(30); Check(!retirement.IsCompleted && !PluginTestContracts.Unloading.Task.IsCompleted && !PluginTestContracts.WrapperUnloading.Task.IsCompleted, "ALC unloading began while old HTTP/shared-wrapper callback executes");
        requestCancellation.Cancel(); await httpOriginalWait.WaitAsync(TimeSpan.FromSeconds(3));
        crossCancellation.Cancel(); await crossWait.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(30); Check(!retirement.IsCompleted && !PluginTestContracts.Unloading.Task.IsCompleted && !PluginTestContracts.WrapperUnloading.Task.IsCompleted, "cancelled request abandoned its original plugin callback lease");
        try { await retirement; throw new InvalidOperationException("callback drain did not report its configured bound"); } catch (TimeoutException) { }
        Check(!PluginTestContracts.Unloading.Task.IsCompleted, "callback drain timeout forced ALC unload while code still runs");
        PluginTestContracts.HttpPause.TrySetResult();
        Check(await PluginTestContracts.HttpFinished.Task.WaitAsync(TimeSpan.FromSeconds(3)) == "late:one", "old HTTP callback could not load private dependency after request cancellation/reload");
        await Task.Delay(20); Check(!PluginTestContracts.Unloading.Task.IsCompleted, "HTTP settlement unloaded a still-running original host/native callback");
        PluginTestContracts.Pause.TrySetResult();
        Check(await PluginTestContracts.HostFinished.Task.WaitAsync(TimeSpan.FromSeconds(3)) == "late:one", "sealed original host method lost its old private assembly context");
        await Task.Delay(20); Check(!PluginTestContracts.Unloading.Task.IsCompleted, "original host method settlement unloaded a still-running native close");
        PluginTestContracts.NativeClosePause.TrySetResult();
        Check(await PluginTestContracts.NativeCloseFinished.Task.WaitAsync(TimeSpan.FromSeconds(3)) == "late:one", "bounded native close original lost its old context");
        await Task.Delay(20); Check(!PluginTestContracts.Unloading.Task.IsCompleted && !PluginTestContracts.WrapperUnloading.Task.IsCompleted, "composed shared wrapper lost its own or its not-yet-invoked inner route generation");
        PluginTestContracts.CrossPause.TrySetResult();
        Check(await PluginTestContracts.CrossFinished.Task.WaitAsync(TimeSpan.FromSeconds(3)) == "late:one", "cancelled wrapper could not invoke its captured old inner route");
        await runtime.DrainRetiredAsync(Context).WaitAsync(TimeSpan.FromSeconds(3));
        PluginTestContracts.HttpPause = null; PluginTestContracts.HttpEntered = null; PluginTestContracts.HttpFinished = null; PluginTestContracts.Unloading = null;
        PluginTestContracts.WrapperUnloading = null;
        PluginTestContracts.Pause = null; PluginTestContracts.Entered = null; PluginTestContracts.HostFinished = null; PluginTestContracts.NativeClosePause = null; PluginTestContracts.NativeCloseEntered = null; PluginTestContracts.NativeCloseFinished = null; PluginTestContracts.CrossPause = null; PluginTestContracts.CrossEntered = null; PluginTestContracts.CrossFinished = null;
        Check(((JsonNode)(await reference.CallAsync("read", null, Context))!).GetValue<string>() == "private:leaf:two", "live reference not updated by plugin reload");
        await File.WriteAllTextAsync(Path.Combine(pluginDirectory, "Plugin.cs"), "this does not compile");
        var failed = await manager.BuildAsync(); Check(failed.Plugins[0].Error is not null, "invalid source compiled");
        Check((await runtime.ReloadAsync(WithWrapper(failed), Context)).Failed.Count == 1, "compile failure absent from report");
        Check(((JsonNode)(await reference.CallAsync("read", null, Context))!).GetValue<string>() == "private:leaf:two", "failed compilation replaced successful generation");
        PluginTestContracts.NativeOpenPause = new(TaskCreationOptions.RunContinuationsAsynchronously); PluginTestContracts.NativeOpenEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); PluginTestContracts.NativeOpenFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Write("three"); var openingGeneration = await manager.BuildAsync();
        Check((await runtime.ReloadAsync(WithWrapper(openingGeneration), Context)).Failed.Count == 0, "native opening generation refused");
        await PluginTestContracts.NativeOpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Write("four"); var replacement = await manager.BuildAsync();
        Check((await runtime.ReloadAsync(WithWrapper(replacement), Context)).Failed.Count == 0, "replacement of opening generation refused");
        var openDrainWatch = Stopwatch.StartNew();
        try { await runtime.DrainRetiredAsync(Context).WaitAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("retirement abandoned an original native open"); } catch (TimeoutException) { }
        Check(openDrainWatch.Elapsed < TimeSpan.FromSeconds(1), "native open blocked the public host drain instead of its generation lease outliving that bound");
        PluginTestContracts.NativeOpenPause.TrySetResult();
        Check(await PluginTestContracts.NativeOpenFinished.Task.WaitAsync(TimeSpan.FromSeconds(3)) == "late:three", "abandoned native open could not load its private dependency for late cleanup");
        await runtime.DrainRetiredAsync(Context).WaitAsync(TimeSpan.FromSeconds(3));
        PluginTestContracts.NativeOpenPause = null; PluginTestContracts.NativeOpenEntered = null; PluginTestContracts.NativeOpenFinished = null;
        await Write("five");
        await File.WriteAllTextAsync(Path.Combine(pluginDirectory, PluginScanner.ManifestFile), "{\"name\":\"test.plugin\",\"assembly\":\"TestPlugin.dll\",\"type\":\"MissingEntry\",\"project\":\"Plugin.csproj\"}");
        var invalidEntry = await manager.BuildAsync(); Check(invalidEntry.Plugins[0].Error is null, "evaluation failure fixture did not compile");
        Check((await runtime.ReloadAsync(WithWrapper(invalidEntry), Context)).Failed.Count == 1, "evaluation failure absent from report");
        Check(((JsonNode)(await reference.CallAsync("read", null, Context))!).GetValue<string>() == "private:leaf:four", "failed evaluation replaced successful generation");
        var weak = runtime.RetiredContexts.ToArray();
        reference.Release(); cachedReference.Release();
        return weak;
        }
        catch (Exception error) { bodyFailure = error; throw; }
        finally
        {
            PluginTestContracts.Pause?.TrySetResult(); PluginTestContracts.HttpPause?.TrySetResult(); PluginTestContracts.NativeClosePause?.TrySetResult(); PluginTestContracts.NativeOpenPause?.TrySetResult(); PluginTestContracts.CrossPause?.TrySetResult();
            var cleanup = new List<Exception>();
            try { await http.CloseAsync(Context).WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception error) { cleanup.Add(error); }
            try { await host.CloseAsync(Context).WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception error) { cleanup.Add(error); }
            try { await runtime.DisposeAsync(); } catch (Exception error) { cleanup.Add(error); }
            if (cleanup.Count > 0)
            {
                if (bodyFailure is null) throw new AggregateException(cleanup);
                foreach (var error in cleanup) Console.Error.WriteLine("Plugin fixture cleanup after original failure: " + error);
            }
        }
    }
    private static DefaultHttpContext Request(string method, string path, int port = 8123)
    {
        var context = new DefaultHttpContext(); context.Request.Method = method; context.Request.Path = path; context.Request.Host = new("localhost", port);
        context.Connection.LocalIpAddress = IPAddress.Loopback; context.Connection.LocalPort = port; context.Response.Body = new MemoryStream(); return context;
    }
    private static string Body(DefaultHttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
    private static async Task HttpRoutes()
    {
        await using var host = Host(); await using var http = new ChordHttp(host, new() { OnError = _ => { } }); var authCalls = 0;
        var contribution = new HttpContribution
        {
            Routes = new Dictionary<string, HttpRoute>
            {
                ["GET /hello/:name"] = new((r, _) => Task.FromResult<HttpResult>(new HttpResult.Text(r.Params["name"]))),
                ["POST /hook"] = new((_, call) => Task.FromResult<HttpResult>(new HttpResult.Text(call.Principal is null ? "anonymous" : "authenticated")), CrossSite: true),
                ["POST /private"] = new((_, _) => Task.FromResult<HttpResult>(new HttpResult.Text("private"))),
                ["GET /files"] = new((_, _) => Task.FromResult<HttpResult>(new HttpResult.Text("exact"))),
                ["GET /files/*"] = new((_, _) => Task.FromResult<HttpResult>(new HttpResult.Text("wildcard"))),
                ["GET /page"] = new((_, call) => Task.FromResult<HttpResult>(new HttpResult.Text(call.Principal is null ? "anonymous" : "authenticated")), Page: true),
                ["GET /error"] = new((_, _) => Task.FromException<HttpResult>(new InvalidOperationException("route failed"))),
            },
            Wraps = [new("prefix", async (_, next, _) => { var result = (HttpResult.Text)await next(); return result with { Body = "wrapped:" + result.Body }; })],
            Relays = [new("test.relay", (_, _) => Task.FromResult<RelayAdmission?>(new("https://relay.example", "https://frame.example")))],
            Framers = ["https://shell.example"],
            Csp = new Dictionary<string, IReadOnlyList<string>> { ["img-src"] = ["https://img.example"] },
        };
        var facet = Facet.Define((_, self) => { self.Contribute(HttpRegistries.Http, contribution); self.Contribute(HttpRegistries.Authenticators, (HttpAuthenticator)((_, _) => { authCalls++; return Task.FromResult<JsonObject?>(new() { ["kind"] = "user" }); })); });
        await host.LoadAsync([new("chord.http", http.Facet), new("test.web", facet)], Context);
        var good = Request("GET", "/hello/Ada"); await http.HandleAsync(good); Check(good.Response.StatusCode == 200 && Body(good) == "wrapped:Ada", "route/wrapper pipeline");
        var before = authCalls; var hook = Request("POST", "/hook"); hook.Request.Headers.Origin = "https://foreign.example"; await http.HandleAsync(hook);
        Check(Body(hook) == "wrapped:anonymous" && authCalls == before, "cross-site hook must run anonymous without authentication");
        var denied = Request("POST", "/private"); denied.Request.Headers.Origin = "https://foreign.example"; await http.HandleAsync(denied); Check(denied.Response.StatusCode == 403, "foreign origin admitted");
        var hostHeader = Request("GET", "/hello/Ada"); hostHeader.Request.Host = new("attacker.example", 8123); await http.HandleAsync(hostHeader); Check(hostHeader.Response.StatusCode == 403, "unlisted host admitted");
        var escape = Request("GET", "/hello/%2fsecret"); await http.HandleAsync(escape); Check(escape.Response.StatusCode == 400, "encoded slash admitted");
        var exact = Request("GET", "/files"); await http.HandleAsync(exact); Check(Body(exact) == "wrapped:exact", "empty wildcard outranked exact route");
        before = authCalls;
        var frame = Request("GET", "/page"); frame.Request.Headers["Sec-Fetch-Site"] = "cross-site"; frame.Request.Headers["Sec-Fetch-Mode"] = "navigate"; frame.Request.Headers["Sec-Fetch-Dest"] = "iframe";
        await http.HandleAsync(frame); Check(Body(frame) == "wrapped:anonymous" && authCalls == before, "cross-site framed page was authenticated/refused instead of anonymous");
        var safeForeign = Request("GET", "/files"); safeForeign.Request.Headers["Sec-Fetch-Site"] = "cross-site"; await http.HandleAsync(safeForeign);
        Check(safeForeign.Response.StatusCode == 200 && authCalls == before, "safe foreign route did not run anonymously");
        var relay = Request("GET", "/files"); relay.Request.Headers["X-Forwarded-For"] = "192.0.2.1"; await http.HandleAsync(relay);
        Check(relay.Response.Headers.ContentSecurityPolicy.ToString().Contains("https://frame.example", StringComparison.Ordinal) && !relay.Response.Headers.ContainsKey("X-Frame-Options"), "verified relay framer was discarded");
        var noRelay = Request("GET", "/files"); await http.HandleAsync(noRelay); Check(!noRelay.Response.Headers.ContentSecurityPolicy.ToString().Contains("frame.example", StringComparison.Ordinal), "relay framer leaked into another request");
        foreach (var path in new[] { "/missing", "/error" })
        {
            var error = Request("GET", path); await http.HandleAsync(error);
            Check(error.Response.StatusCode == (path == "/missing" ? 404 : 500) && error.Response.Headers.ContentSecurityPolicy.ToString().Contains("https://shell.example", StringComparison.Ordinal) && error.Response.Headers.ContentSecurityPolicy.ToString().Contains("https://img.example", StringComparison.Ordinal) && !error.Response.Headers.ContainsKey("X-Frame-Options"), "error answer discarded resolved router security headers");
        }
        Check(good.Response.Headers.ContainsKey("Content-Security-Policy") && denied.Response.Headers.ContainsKey("X-Content-Type-Options"), "security defaults missing");
        await http.CloseAsync(Context); var closed = Request("GET", "/hello/Ada"); await http.HandleAsync(closed); Check(closed.Response.StatusCode == 503, "closed HTTP accepts requests");
    }
    private static async Task HttpResponses()
    {
        await using var host = Host(); await using var http = new ChordHttp(host, new() { OnError = _ => { } });
        var blocks = 0; var bytes = Encoding.ASCII.GetBytes("0123456789"); var root = Temp(); await File.WriteAllBytesAsync(Path.Combine(root, "safe.txt"), bytes);
        var contribution = new HttpContribution { Routes = new Dictionary<string, HttpRoute>
        {
            ["GET /bytes"] = new((_, _) => Task.FromResult<HttpResult>(new HttpResult.Bytes((offset, length, _) => { blocks++; return Task.FromResult<ReadOnlyMemory<byte>>(bytes.AsMemory((int)offset, length)); }, bytes.Length, Ranges: true))),
            ["GET /file"] = new((_, _) => Task.FromResult<HttpResult>(new HttpResult.File(Path.Combine(root, "safe.txt"), root, Ranges: true))),
            ["GET /escape"] = new((_, _) => Task.FromResult<HttpResult>(new HttpResult.File(Path.Combine(root, "..", "outside.txt"), root))),
        } };
        await host.LoadAsync([new("chord.http", http.Facet), new("test.web", Facet.Define((_, self) => self.Contribute(HttpRegistries.Http, contribution)))], Context);
        var range = Request("GET", "/bytes"); range.Request.Headers.Range = "bytes=2-5"; await http.HandleAsync(range);
        Check(range.Response.StatusCode == 206 && Body(range) == "2345" && blocks == 1, "bounded range response");
        var file = Request("HEAD", "/file"); await http.HandleAsync(file); Check(file.Response.ContentLength == 10 && Body(file) == "", "HEAD file reads/writes body");
        var escape = Request("GET", "/escape"); await http.HandleAsync(escape); Check(escape.Response.StatusCode == 403, "file escaped root");
        var secret = Encoding.UTF8.GetBytes("test-only-server-signing-key"); var cookie = SignedCookies.Create("session", "value", secret, true, TimeSpan.FromMinutes(5));
        Check(cookie.Contains("Secure") && cookie.Contains("HttpOnly") && SignedCookies.Read(cookie, "session", secret) == "value", "signed cookie roundtrip");
        Check(SignedCookies.Read(cookie.Replace("value", "other"), "session", secret) is null, "tampered cookie accepted");
        var expired = SignedCookies.Create("session", "value", secret, false, TimeSpan.FromSeconds(-1)); Check(SignedCookies.Read(expired, "session", secret) is null, "expired cookie accepted");
    }
    private static async Task HttpListener()
    {
        await using var host = Host(); await using var http = new ChordHttp(host, new() { Port = 0, OnError = _ => { } });
        var facet = Facet.Define((_, self) =>
        {
            self.Contribute(HttpRegistries.Authenticators, (HttpAuthenticator)((_, _) => Task.FromResult<JsonObject?>(new() { ["kind"] = "user" })));
            self.Contribute(HttpRegistries.Http, new HttpContribution { Routes = new Dictionary<string, HttpRoute> { ["GET /"] = new((_, _) => Task.FromResult<HttpResult>(new HttpResult.Text("listening"))) }, Sockets = new Dictionary<string, HttpSocket> { ["/echo"] = async (socket, _, call) => { var data = new byte[64]; var result = await socket.ReceiveAsync(data.AsMemory(), call.AbortSignal); await socket.SendAsync(data.AsMemory(0, result.Count), WebSocketMessageType.Binary, true, call.AbortSignal); } } });
        });
        await host.LoadAsync([new("chord.http", http.Facet), new("test.web", facet)], Context);
        var address = await http.ListeningAsync(Context); using var client = new HttpClient(); Check(await client.GetStringAsync(address) == "listening", "loopback listener request failed");
        using var socket = new ClientWebSocket(); await socket.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = "/echo" }.Uri, CancellationToken.None);
        await socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None); var echo = new byte[3]; var response = await socket.ReceiveAsync(echo, CancellationToken.None);
        Check(response.Count == 3 && echo.SequenceEqual(new byte[] { 1, 2, 3 }), "real WebSocket handler failed");
        await http.CloseAsync(Context); await host.CloseAsync(Context);
    }
    private static async Task Workers()
    {
        await using var host = Hosts.Create(new() { Address = PluginTestContracts.Server.Address(), OnError = _ => { }, Principal = new() { ["kind"] = "server" }, ReadyTimeout = TimeSpan.FromSeconds(15) }); var root = Temp();
        await using var workers = new ProcessWorkers("tests", host, PluginTestContracts.Worker, new()
        {
            Executable = Sdk, Arguments = [Assembly.GetExecutingAssembly().Location, "--worker"], Authorize = (_, _) => true,
            Principal = _ => new() { ["kind"] = "worker" }, Plugins = (_, _) => Task.FromResult(new PluginBuildSet([], [])), LockDirectory = root, OnError = (_, _) => { },
            StartTimeout = TimeSpan.FromSeconds(25), CloseTimeout = TimeSpan.FromSeconds(3), IdleTimeout = TimeSpan.FromMilliseconds(500),
        }, Context);
        var parent = Facet.Define((h, _) => h.Serve(PluginTestContracts.ParentService, (_, _, _) => new(new Dictionary<string, object?> { ["ready"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create(1))) }, "reader")));
        await host.LoadAsync([new("test.workers", workers.Facet), new("test.parent", parent)], Context);
        var address = PluginTestContracts.Worker.Address(("id", JsonValue.Create("a")));
        using var reference = await host.GetAsync(PluginTestContracts.Remote, new() { ["id"] = "a" }, Context);
        var first = ((JsonNode)(await reference.CallAsync("pid", null, Context))!).GetValue<int>(); Check(first != Environment.ProcessId, "worker method ran in parent");
        await workers.RolloutAsync(new([], []), Context);
        using (var crashed = Process.GetProcessById(first)) { crashed.Kill(true); await crashed.WaitForExitAsync(); }
        await Eventually(() => workers.State.Values.Any(state => state.Status == "failed"));
        await Eventually(() => reference.State.Value.Kind == "failed");
        var recovery = await workers.RolloutAsync(new([], []), Context);
        Check(recovery.Hosts.Count > 0 && recovery.Hosts.All(result => result.Error is null && !result.Stopped), "rollout did not retry failed held worker");
        await Eventually(() => reference.State.Value.Kind == "live");
        var recovered = ((JsonNode)(await reference.CallAsync("pid", null, Context))!).GetValue<int>(); Check(recovered != first, "crashed worker did not restart after Retry");
        reference.Release(); await workers.StopAsync(address); workers.Retry(address);
        using var second = await host.GetAsync(PluginTestContracts.Remote, new() { ["id"] = "a" }, Context);
        var next = ((JsonNode)(await second.CallAsync("pid", null, Context))!).GetValue<int>(); Check(next != recovered, "worker did not restart after stop");
        var slow = await BuildPlainPlugin(Temp(), "SlowPlugin", "public sealed class SlowPlugin : PiSharp.Chord.Hosting.Plugins.IChordPlugin { public PiSharp.Chord.Host.Facet CreateFacet(PiSharp.Chord.Hosting.Plugins.PluginContext _) => PiSharp.Chord.Host.Facet.Define((_,_) => System.Threading.Thread.Sleep(1800)); }");
        second.Release();
        var idleCrossing = await workers.RolloutAsync(new([slow], []), Context);
        Check(idleCrossing.Hosts.Count == 1 && idleCrossing.Hosts[0].Error is null && !idleCrossing.Hosts[0].Stopped, "idle deadline stopped a worker during admitted reload");
        await Eventually(() => workers.State.Count == 0);
        await workers.CloseAsync(); await host.CloseAsync(Context); Check(workers.State.Count == 0, "worker state retained stopped process");
    }
    private static async Task HttpDrain()
    {
        await using var host = Host(); await using var http = new ChordHttp(host, new() { CloseTimeout = TimeSpan.FromMilliseconds(80), OnError = _ => { } });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var facet = Facet.Define((_, self) => self.Contribute(HttpRegistries.Http, new HttpContribution
        {
            Routes = new Dictionary<string, HttpRoute> { ["GET /slow"] = new(async (_, call) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, call.AbortSignal); return new HttpResult.Empty(); }) },
        }));
        await host.LoadAsync([new("chord.http", http.Facet), new("test.web", facet)], Context);
        var request = Request("GET", "/slow"); var running = http.HandleAsync(request);
        await entered.Task; await http.CloseAsync(Context).WaitAsync(TimeSpan.FromSeconds(2)); await running.WaitAsync(TimeSpan.FromSeconds(2));
        Check(request.Response.StatusCode == 503, "drain bound did not cancel request root");
        var closed = Request("GET", "/slow"); await http.HandleAsync(closed); Check(closed.Response.StatusCode == 503, "closed component accepted new request");
    }
    private static async Task AddressLock()
    {
        var root = Temp(); using var first = await ProcessAddressLock.AcquireAsync("address", root); using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try { using var second = await ProcessAddressLock.AcquireAsync("address", root, cancellation.Token); throw new Exception("second process lock admitted"); } catch (OperationCanceledException) { }
        first.Dispose(); using var acquired = await ProcessAddressLock.AcquireAsync("address", root); Check(acquired is not null, "released address lock remained held");
    }
    private static async Task FailedAddedPlugin()
    {
        var root = Temp(); await using var manager = new PluginManager([root], new PluginBuilder(new() { DotnetPath = Sdk, ArtifactDirectory = Path.Combine(Temp(), "build") }));
        await manager.BuildAsync();
        var directory = Path.Combine(root, "new"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, PluginScanner.ManifestFile), "{\"name\":\"test.added\",\"assembly\":\"Absent.dll\",\"type\":\"Missing\"}");
        var build = await manager.BuildAsync(); Check(build.Plugins.Count == 1 && build.Plugins[0].Error is not null, "new failed plugin fixture");
        await manager.RolloutAsync(build, [new TestRolloutStep("host", () => Task.FromResult(new PluginRolloutStepReport("host", [new("host", FailedPlugins: ["test.added"])])))], Context);
        Check(manager.State.SetChanged && manager.State.Dirty.ContainsKey("test.added"), "failed newly discovered plugin lost dirty/set mark");
    }
    private static async Task<BuiltPlugin> BuildPlainPlugin(string root, string type, string source, string name = "test.plain")
    {
        var directory = Path.Combine(root, "plugin"); Directory.CreateDirectory(directory);
        var references = new[] { typeof(IChordPlugin).Assembly, typeof(ChordHost).Assembly, typeof(PiSharp.Contracts.Schema.JsonSchemaBuilder).Assembly, typeof(PiSharp.Telemetry.ITelemetryContext).Assembly, Assembly.GetExecutingAssembly() };
        var project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><AssemblyName>PlainPlugin</AssemblyName><NoWarn>PISHARP_CHORD001;PISHARP_SCHEMA001;PISHARP_TELEMETRY001</NoWarn></PropertyGroup><ItemGroup>" + string.Join("", references.Select(reference => $"<Reference Include=\"{reference.GetName().Name}\"><HintPath>{System.Security.SecurityElement.Escape(reference.Location)}</HintPath></Reference>")) + "<FrameworkReference Include=\"Microsoft.AspNetCore.App\"/></ItemGroup></Project>";
        await File.WriteAllTextAsync(Path.Combine(directory, "Plugin.csproj"), project); await File.WriteAllTextAsync(Path.Combine(directory, "Plugin.cs"), source);
        var plugin = new NativePlugin(new(name, "PlainPlugin.dll", type, "Plugin.csproj"), directory);
        var build = await new PluginBuilder(new() { DotnetPath = Sdk, ArtifactDirectory = Path.Combine(root, "built"), Restore = true }).BuildAsync(plugin);
        Check(build.Error is null, "plain plugin compilation: " + build.Error); return build;
    }
    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; }
    }
    private static async Task Descendants()
    {
        foreach (var crash in new[] { false, true })
        {
            var marker = Path.Combine(Temp(), "descendant.pid");
            var start = new ProcessStartInfo(Sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { Assembly.GetExecutingAssembly().Location, "--spawn-descendant", marker }) start.ArgumentList.Add(argument);
            if (crash) start.ArgumentList.Add("--crash");
            await using var process = await OwnedChildProcess.StartAsync(start);
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); process.OwnReader(stdout); process.OwnReader(stderr);
            await process.Exit.WaitAsync(TimeSpan.FromSeconds(12));
            Check(File.Exists(marker), "leader never spawned its descendant"); var descendant = int.Parse(await File.ReadAllTextAsync(marker), System.Globalization.CultureInfo.InvariantCulture);
            await Eventually(() => !Alive(descendant));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }
    private static async Task BuilderDescendant()
    {
        var root = Temp(); var marker = Path.Combine(root, "child.pid"); var project = Path.Combine(root, "Fake.csproj"); await File.WriteAllTextAsync(project, marker);
        var apphost = Path.Combine(AppContext.BaseDirectory, Assembly.GetExecutingAssembly().GetName().Name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Check(File.Exists(apphost), "test apphost required for fake build fixture");
        var previous = Environment.GetEnvironmentVariable("PISHARP_TEST_DOTNET"); var pinned = Sdk; Environment.SetEnvironmentVariable("PISHARP_TEST_DOTNET", pinned);
        try
        {
            var watch = Stopwatch.StartNew();
            var build = await new PluginBuilder(new() { DotnetPath = apphost, ArtifactDirectory = Path.Combine(root, "artifacts"), Timeout = TimeSpan.FromSeconds(2) })
                .BuildAsync(new(new("test.fake", "Missing.dll", "Missing", "Fake.csproj"), root)).WaitAsync(TimeSpan.FromSeconds(8));
            Check(build.Error is not null && watch.Elapsed < TimeSpan.FromSeconds(8), "builder awaited inherited descendant output without its deadline");
            Check(File.Exists(marker), "fake build did not create descendant"); var pid = int.Parse(await File.ReadAllTextAsync(marker), System.Globalization.CultureInfo.InvariantCulture);
            await Eventually(() => !Alive(pid));
        }
        finally { Environment.SetEnvironmentVariable("PISHARP_TEST_DOTNET", previous); }
    }
    private sealed class DisposeProbe : IHostedService, IDisposable
    {
        internal readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() => Disposed.TrySetResult();
    }
    private static async Task HttpFailedStart()
    {
        using var occupied = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); occupied.Start(); var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var probe = new DisposeProbe();
        await using (var host = Host())
        await using (var http = new ChordHttp(host, new() { Port = port, OnError = _ => { }, Configure = builder => builder.Services.AddSingleton<IHostedService>(_ => probe) }))
        {
            try { await host.LoadAsync([new("chord.http", http.Facet)], Context); } catch (IOException) { }
            var refused = false; try { await http.ListeningAsync(Context).WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { refused = true; }
            Check(refused, "occupied listener unexpectedly started"); await probe.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3)); await host.CloseAsync(Context);
        }
        occupied.Stop();
        await using var retryHost = Host(); await using var retry = new ChordHttp(retryHost, new() { Port = port, OnError = _ => { } });
        await retryHost.LoadAsync([new("chord.http", retry.Facet)], Context); Check((await retry.ListeningAsync(Context)).Port == port, "failed listener acquisition prevented fresh bind");
        await retry.CloseAsync(Context); await retryHost.CloseAsync(Context);
    }
    private static async Task WorkerStartupClose()
    {
        var root = Temp(); var marker = Path.Combine(root, "closed");
        await using var host = Hosts.Create(new() { Address = PluginTestContracts.Server.Address(), Principal = new() { ["kind"] = "server" }, OnError = _ => { }, ReadyTimeout = TimeSpan.FromSeconds(15) });
        await using var workers = new ProcessWorkers("startup", host, PluginTestContracts.Worker, new()
        {
            Executable = Sdk, Arguments = [Assembly.GetExecutingAssembly().Location, "--worker", "--hold-startup"], Authorize = (_, _) => true,
            Principal = _ => new() { ["kind"] = "worker" }, Plugins = (_, _) => Task.FromResult(new PluginBuildSet([], [])), Data = _ => new JsonObject { ["closeMarker"] = marker },
            LockDirectory = root, OnError = (_, _) => { }, CloseTimeout = TimeSpan.FromSeconds(5), StartTimeout = TimeSpan.FromSeconds(20),
        }, Context);
        var core = Facet.Define((facet, _) => facet.Serve(PluginTestContracts.ParentService, (_, _, _) => new(new Dictionary<string, object?> { ["ready"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create(1))) }, "reader")));
        await host.LoadAsync([new("test.parent", core), new("test.workers", workers.Facet)], Context);
        using var reference = await host.GetAsync(PluginTestContracts.Remote, new() { ["id"] = "startup" }, Context);
        var pid = ((JsonNode)(await reference.CallAsync("pid", null, Context))!).GetValue<int>();
        Check(workers.State.Values.Any(state => state.Status == "starting"), "fixture initializer already returned");
        await workers.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(File.Exists(marker) && await File.ReadAllTextAsync(marker) == "closed", "shutdown before initializer return skipped the connected host's native close");
        Check(!Alive(pid), "startup worker remained alive after bounded close"); reference.Release(); await host.CloseAsync(Context);
    }
    private sealed class TestRolloutStep(string name, Func<Task<PluginRolloutStepReport>> run) : IPluginRolloutStep
    {
        public string Name { get; } = name;
        public Task<PluginRolloutStepReport> RolloutAsync(PluginBuildSet build, ChordContext context) => run();
    }
    private static async Task RolloutOrder()
    {
        var root = Temp();
        await using var manager = new PluginManager([], new PluginBuilder(new() { DotnetPath = Sdk, ArtifactDirectory = root }));
        var order = new List<string>();
        IPluginRolloutStep Step(string name, bool stopped = false, bool failed = false, bool throwing = false) => new TestRolloutStep(name, () =>
        {
            order.Add(name);
            if (throwing) throw new IOException("host failure");
            return Task.FromResult(new PluginRolloutStepReport(name, [new(name, Stopped: stopped, FailedPlugins: failed ? ["test.failed"] : [])]));
        });
        var reports = await manager.RolloutAsync(new([], []), [Step("a", failed: true), Step("b", throwing: true), Step("c")], Context);
        Check(order.SequenceEqual(["a", "b", "c"]) && reports.Count == 3 && reports[1].Hosts[0].Error is not null, "failed plugin/host stopped a later rollout step");
        order.Clear();
        reports = await manager.RolloutAsync(new([], []), [Step("a"), Step("stopped", stopped: true), Step("unreached")], Context);
        Check(order.SequenceEqual(["a", "stopped"]) && reports.Count == 2, "explicit stopped result did not stop rollout");
    }
    private static async Task Eventually(Func<bool> check)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!check()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("expected asynchronous state did not arrive"); await Task.Delay(20); }
    }
}
