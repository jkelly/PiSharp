using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using PiSharp.Chord.Legacy;

internal static partial class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--grandchild")) { await Task.Delay(TimeSpan.FromMinutes(5)); return 0; }
        if (args.Contains("--spawn-and-exit")) { var orphan = Process.Start(SelfStart("--grandchild"))!; Console.WriteLine("descendant:" + orphan.Id); orphan.Dispose(); return 0; }
        if (args.Contains("--worker")) return await Worker();
        var failed = 0;
        foreach (var (name, run) in Cases())
        {
            try { await run().WaitAsync(TimeSpan.FromSeconds(30)); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
        }
        Console.WriteLine($"{failed} failures; legacy cluster/manager tests; authored native evidence, source f9a26365568127461c47a07a37dcb42c140a3ac5");
        return failed == 0 ? 0 : 1;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Eventually(Func<bool> predicate)
    { for (var i = 0; i < 400 && !predicate(); i++) await Task.Delay(5); Check(predicate(), "Condition did not settle."); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
    private static ProcessStartInfo SelfStart(params string[] arguments)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Current process executable is unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        return start;
    }
    private static LegacyProcessDriver Driver(JsonObject? init = null, string? lockName = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = SelfStart("--worker");
        return new(new() { FileName = start.FileName, Arguments = start.ArgumentList.ToArray(), Init = init ?? new JsonObject(), Lock = lockName,
            Environment = environment ?? new Dictionary<string, string?>() });
    }
    private static bool Alive(int pid)
    { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task<int> Worker()
    {
        await using var control = await LegacyChildControl.ConnectAsync();
        var init = await control.Init;
        var mode = init?["mode"]?.GetValue<string>() ?? "normal";
        try { await control.LockAsync(); }
        catch (Exception) { await Task.Delay(50); return 2; }
        if (mode == "fatal") { control.Fatal(new ChordException("unavailable", "fixture refuses to start", "fixture")); await Task.Delay(100); return 2; }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        control.OnShutdown(() => { if (mode != "ignore") done.TrySetResult(); return Task.CompletedTask; });
        var childPid = 0;
        if (init?["spawnChild"]?.GetValue<bool>() == true) childPid = Process.Start(SelfStart("--grandchild"))!.Id;
        using var connection = new LegacyConnection(control.Link, new() { Name = "worker", Role = "upstream", PeerKind = "host", Kind = "host" });
        connection.Received += frame =>
        {
            if (frame.Type != "call") return;
            var value = frame.String("member") == "child" ? JsonValue.Create(childPid) : frame["args"];
            connection.Send(LegacyProtocol.Frame("result", ("id", frame["id"]), ("value", value)));
        };
        if (mode != "never-ready") control.Ready();
        if (mode == "busy-idle") { control.Report(activeCalls: 1); _ = Task.Run(async () => { await Task.Delay(200); control.Report(activeCalls: 0); }); }
        await done.Task;
        return 0;
    }
    private static IEnumerable<(string, Func<Task>)> Cases()
    {
        foreach (var test in ClusterCases()) yield return test;
        foreach (var test in RetentionCases()) yield return test;
        yield return ("manager.admission-cleans-natural-exit-before-ready", async () =>
        {
            var start = SelfStart("--spawn-and-exit"); var output = new System.Text.StringBuilder();
            var driver = new LegacyProcessDriver(new() { FileName = start.FileName, Arguments = start.ArgumentList.ToArray(), OnOutput = (_, text) => { lock (output) output.Append(text); } });
            var instance = driver.Spawn(); var exit = await instance.Exited;
            string text; lock (output) text = output.ToString();
            var match = System.Text.RegularExpressions.Regex.Match(text, @"descendant:(\d+)");
            Check(exit.Code == 0 && match.Success, "Pre-ready fixture did not run.");
            var descendant = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            await Eventually(() => !Alive(descendant));
        });
        yield return ("manager.concurrent-start-stop-replace-real-child", async () =>
        {
            await using var manager = new LegacyHostManager(new() { Driver = Driver() });
            var starts = await Task.WhenAll(manager.StartAsync(), manager.StartAsync(), manager.StartAsync());
            Check(starts.All(item => ReferenceEquals(item, starts[0])), "Concurrent starts duplicated process.");
            Check(manager.Status == "running", "Not running after ready.");
            var instance = starts[0];
            using var connection = new LegacyConnection(instance.Link, new() { Name = "server", PeerKind = "host", PeerName = "worker" });
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Opened += _ => opened.TrySetResult(); await opened.Task;
            var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Received += frame => { if (frame.Type == "result") answer.TrySetResult(frame["value"]); };
            connection.Send(LegacyProtocol.Frame("call", ("id", JsonValue.Create(1)), ("hold", JsonValue.Create(1)), ("member", JsonValue.Create("echo")), ("args", JsonValue.Create("native-pipe"))));
            Check((await answer.Task)?.GetValue<string>() == "native-pipe", "Real child protocol4 round trip failed.");
            var replacement = await manager.ReplaceAsync();
            Check(replacement.Pid != instance.Pid && !Alive(instance.Pid), "Replacement started before old process exited.");
            await manager.StopAsync(); Check(manager.Status == "stopped" && (await replacement.Exited).Code == 0, "Graceful stop failed.");
        });
        yield return ("manager.kernel-lock-and-replace", async () =>
        {
            var name = "legacy-test-" + Guid.NewGuid();
            using (await LegacyProcessLock.AcquireAsync(name))
            {
                var error = await Throws<ChordException>(async () => { using var second = await LegacyProcessLock.AcquireAsync(name); });
                Check(error.Reason == "locked", "Duplicate lock was not refused.");
            }
            await using var first = new LegacyHostManager(new() { Driver = Driver(lockName: name) });
            await using var secondManager = new LegacyHostManager(new() { Driver = Driver(lockName: name) });
            await first.StartAsync();
            Check((await Throws<ChordException>(() => secondManager.StartAsync())).Reason == "locked", "Two children acquired one lock.");
            await first.ReplaceAsync(); await first.StopAsync(); await secondManager.StartAsync();
        });
        yield return ("manager.does-not-inherit-parent-lock", async () =>
        {
            var name = "legacy-inherited-" + Guid.NewGuid();
            using var held = await LegacyProcessLock.AcquireAsync(name);
            var old = Environment.GetEnvironmentVariable(LegacyProcessDriver.LockEnvironmentVariable);
            Environment.SetEnvironmentVariable(LegacyProcessDriver.LockEnvironmentVariable, name);
            try { await using var manager = new LegacyHostManager(new() { Driver = Driver() }); await manager.StartAsync(); }
            finally { Environment.SetEnvironmentVariable(LegacyProcessDriver.LockEnvironmentVariable, old); }
        });
        yield return ("manager.fatal-and-readiness-timeout-cleanup", async () =>
        {
            await using var fatal = new LegacyHostManager(new() { Driver = Driver(new() { ["mode"] = "fatal" }) });
            Check((await Throws<ChordException>(() => fatal.StartAsync())).Reason == "fixture", "Fatal cause was lost.");
            Check(fatal.Status == "stopped" && fatal.Instance is null, "Fatal child was retained.");
            await using var timeout = new LegacyHostManager(new() { Driver = Driver(new() { ["mode"] = "never-ready" }), ReadyTimeout = TimeSpan.FromMilliseconds(500) });
            await Throws<ChordException>(() => timeout.StartAsync()); Check(timeout.Status == "stopped" && timeout.Instance is null, "Timed-out process was retained.");
        });
        yield return ("manager.busy-to-idle-clock-and-policy", async () =>
        {
            await using var manager = new LegacyHostManager(new() { Driver = Driver(new() { ["mode"] = "busy-idle" }),
                KeepAlive = live => live.IdleFor < TimeSpan.FromMilliseconds(150), CheckInterval = TimeSpan.FromMilliseconds(20) });
            await manager.StartAsync(); await Eventually(() => manager.Liveness.ActiveCalls == 1);
            Check(manager.Liveness.IdleFor == TimeSpan.Zero, "Busy child accumulated idle time.");
            await Eventually(() => manager.Liveness.ActiveCalls == 0);
            Check(manager.Liveness.IdleFor < TimeSpan.FromMilliseconds(100), "Idle time counted from busy report.");
            await Eventually(() => manager.Status == "stopped");
        });
        yield return ("manager.grace-kills-descendants", async () =>
        {
            await using var manager = new LegacyHostManager(new() { Driver = Driver(new() { ["mode"] = "ignore", ["spawnChild"] = true }), Grace = TimeSpan.FromMilliseconds(60) });
            var instance = await manager.StartAsync();
            using var connection = new LegacyConnection(instance.Link, new() { Name = "server", PeerKind = "host", PeerName = "worker" });
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); connection.Opened += _ => ready.TrySetResult(); await ready.Task;
            var child = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously); connection.Received += f => { if (f.Type == "result") child.TrySetResult(f["value"]!.GetValue<int>()); };
            connection.Send(LegacyProtocol.Frame("call", ("id", JsonValue.Create(1)), ("hold", JsonValue.Create(1)), ("member", JsonValue.Create("child"))));
            var pid = await child.Task; Check(Alive(pid), "No real descendant started.");
            await manager.StopAsync(); Check((await instance.Exited).Signal == "SIGKILL", "Unresponsive child not killed."); await Eventually(() => !Alive(pid));
        });
        yield return ("manager.graceful-exit-kills-descendants-windows", async () =>
        {
            if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP graceful Job Object descendant test: Windows only."); return; }
            await using var manager = new LegacyHostManager(new() { Driver = Driver(new() { ["spawnChild"] = true }),
                OnExit = _ => throw new InvalidOperationException("consumer exit observer"), OnError = _ => throw new InvalidOperationException("consumer error observer") });
            var instance = await manager.StartAsync();
            using var connection = new LegacyConnection(instance.Link, new() { Name = "server", PeerKind = "host", PeerName = "worker" });
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); connection.Opened += _ => ready.TrySetResult(); await ready.Task;
            var child = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously); connection.Received += f => { if (f.Type == "result") child.TrySetResult(f["value"]!.GetValue<int>()); };
            connection.Send(LegacyProtocol.Frame("call", ("id", JsonValue.Create(1)), ("hold", JsonValue.Create(1)), ("member", JsonValue.Create("child"))));
            var pid = await child.Task;
            await manager.StopAsync(); Check((await instance.Exited).Code == 0, "Child did not stop gracefully.");
            await Eventually(() => !Alive(pid)); Check(manager.Status == "stopped", "Throwing observer poisoned stop queue.");
        });
    }
}
