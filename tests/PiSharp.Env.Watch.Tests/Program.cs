using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Env;
using PiSharp.Env.Daemon;
using PiSharp.Env.Protocol;

internal static class Program
{
    private static readonly DurableContext Context = DurableContext.Background;
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--worker")
        {
            await using var session = new EnvDaemonSession(Console.OpenStandardInput(), Console.OpenStandardOutput(), new()
            { Token = args[Array.IndexOf(args, "--token") + 1], Operations = [args[1] == "real" ? new WatchOperation() : new FixtureWatch(args[1])] });
            await session.RunAsync();
            return 0;
        }
        var root = Path.Combine(Path.GetTempPath(), "pisharp-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Case("polling coverage, observer isolation, exclusions and joined stop", async () =>
            {
                await using var connection = new Connection(Options());
                var changes = new ConcurrentQueue<FileChange>();
                var callbacks = 0;
                var watcher = await RemoteWatcher.OpenAsync(connection, [new(root) { Recursive = true, Exclude = new() { Names = ["ignored"] } }], change =>
                { changes.Enqueue(change); if (Interlocked.Increment(ref callbacks) == 1) throw new InvalidOperationException("observer"); }, Polling());
                Check(watcher.Mode == WatchMode.Polling, "ready mode");
                var first = Path.Combine(root, "first"); await File.WriteAllTextAsync(first, "first");
                await Until(() => Paths(changes).Contains(first));
                var second = Path.Combine(root, "second"); await File.WriteAllTextAsync(second, "second");
                await Until(() => Paths(changes).Contains(second));
                var ignored = Path.Combine(root, "ignored"); await File.WriteAllTextAsync(ignored, "ignored");
                await Task.Delay(120);
                Check(!Paths(changes).Contains(ignored), "excluded name leaked");
                await Task.WhenAll(watcher.StopAsync(Context), watcher.StopAsync(Context));
                var count = changes.Count; await File.WriteAllTextAsync(second, "after stopped"); await Task.Delay(120);
                Check(changes.Count == count, "callback after stop");
                var reply = await connection.RequestAsync("lstat", new() { ["path"] = first });
                Check(reply.Json.Count > 0, "connection usable after stop");
            });
            await Case("initial failure and caller cancellation", async () =>
            {
                await using var connection = new Connection(Options());
                var error = await Throws<RemoteError>(() => RemoteWatcher.OpenAsync(connection, [new(root)], _ => { }, new() { MaxDirectories = 0 }));
                Check(error.Code == "EINVAL", "directory budget failure");
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                await Throws<RemoteError>(() => RemoteWatcher.OpenAsync(connection, [new(root)], _ => { }, cancellationToken: cancel.Token));
            });
            await Case("fresh recovery snapshots targets and reports overflow", async () =>
            {
                var starts = 0;
                await using var connection = new Connection(Options() with { Command = null, CommandFactory = _ => { Interlocked.Increment(ref starts); return Task.FromResult<IReadOnlyList<string>>(Command("real")); } });
                var targets = new List<WatchTarget> { new(root) };
                var changes = new ConcurrentQueue<FileChange>();
                var watcher = await RemoteWatcher.OpenAsync(connection, targets, changes.Enqueue, Polling());
                targets.Clear();
                var pid = (await connection.InfoAsync()).Pid;
                using (var child = Process.GetProcessById(pid)) child.Kill();
                await Until(() => changes.OfType<FileOverflowChange>().Any());
                Check(starts == 2, "fresh subscription process");
                var path = Path.Combine(root, "recovered"); await File.WriteAllTextAsync(path, "yes");
                await Until(() => Paths(changes).Contains(path));
                using (var child = Process.GetProcessById((await connection.InfoAsync()).Pid)) child.Kill();
                await Task.Delay(100);
                var timer = Stopwatch.StartNew(); await watcher.StopAsync(Context);
                Check(timer.Elapsed < TimeSpan.FromMilliseconds(700), "stop did not wake recovery delay");
                await Task.Delay(1100);
                Check(starts == 2, "stopped watcher restarted");
            });
            await Case("terminal watch errors and invalid event order", async () =>
            {
                await using var connection = new Connection(Options("error"));
                var changes = new ConcurrentQueue<FileChange>();
                var watcher = await RemoteWatcher.OpenAsync(connection, [], changes.Enqueue);
                await Until(() => changes.Count == 1);
                await watcher.StopAsync(Context);
                Check(changes.Single() is FileFailedChange { Error.Code: FileErrorCode.PermissionDenied }, "terminal error mapping");
                await using var invalid = new Connection(Options("early"));
                await Throws<InvalidDataException>(() => RemoteWatcher.OpenAsync(invalid, [], _ => { }));
            });
            await Case("oversized changes degrade to overflow", () =>
            {
                var method = typeof(WatchOperation).GetMethod("ChangeEvent", BindingFlags.NonPublic | BindingFlags.Static)!;
                var huge = new FilePathsChange(ImmutableArray.Create(new string('x', FrameCodec.MaxPayloadBytes)));
                var ev = (JsonObject)method.Invoke(null, [huge, WatchMode.Polling])!;
                Check(ev["overflow"]!.GetValue<bool>() && ev["paths"] is null, "oversize bounded event");
                FrameCodec.Encode(new(FrameType.Event, 1, ev));
                var small = (JsonObject)method.Invoke(null, [new FilePathsChange(["/a", "/b"]), WatchMode.Native])!;
                Check(small["paths"]!.AsArray().Count == 2, "normal path payload");
                return Task.CompletedTask;
            });
            Console.WriteLine("PASS all 5 remote watch cases");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }

    private static IEnumerable<string> Paths(ConcurrentQueue<FileChange> changes) => changes.OfType<FilePathsChange>().SelectMany(change => change.Paths);
    private static RemoteWatchOptions Polling() => new() { Mode = WatchMode.Polling, PollIntervalMs = 20 };
    private static ConnectionOptions Options(string mode = "real") => new() { Command = Command(mode), StartupTimeout = TimeSpan.FromSeconds(10), AbortGrace = TimeSpan.FromMilliseconds(200) };
    private static string[] Command(string mode)
    {
        var process = Environment.ProcessPath!;
        return Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [process, typeof(Program).Assembly.Location, "--worker", mode] : [process, "--worker", mode];
    }
    private static async Task Until(Func<bool> check)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); while (!check()) await Task.Delay(20, timeout.Token); }
    private static void Check(bool okay, string message) { if (!okay) throw new Exception(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task Case(string name, Func<Task> action) { await action(); Console.WriteLine("PASS " + name); }
    private sealed class FixtureWatch(string mode) : IEnvDaemonOperation
    {
        public string Name => "watch";
        public async ValueTask<EnvDaemonReply> ExecuteAsync(EnvDaemonRequest request)
        {
            if (mode == "early") { await request.EmitEventAsync(new() { ["kind"] = "change", ["overflow"] = true }); return EnvDaemonReply.Empty; }
            await request.EmitEventAsync(new() { ["kind"] = "ready", ["mode"] = "polling" });
            await request.EmitEventAsync(new() { ["kind"] = "error", ["code"] = "permission_denied", ["message"] = "fixture" });
            await request.EmitEventAsync(new() { ["kind"] = "change", ["paths"] = new JsonArray("should-not-deliver") });
            return EnvDaemonReply.Empty;
        }
    }
}
