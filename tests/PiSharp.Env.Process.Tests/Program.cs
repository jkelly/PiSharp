// PiSharp-only: offline native process ownership and byte-protocol regression tests.
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Env;
using PiSharp.Env.Daemon;

internal static class Program
{
    private static int passed;
    private static readonly byte[] Binary = Enumerable.Range(0, 3 * 1024 * 1024 + 257).Select(index => (byte)(index * 31)).ToArray();
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() is "--daemon" or "--old-daemon")
        {
            await using var daemon = new EnvDaemonSession(Console.OpenStandardInput(), Console.OpenStandardOutput(), new()
            { Token = args[^1], Operations = EnvProcessOperations.Create(), Features = args[0] == "--old-daemon" ? [] : ["process"] });
            await daemon.RunAsync(); return 0;
        }
        if (args.FirstOrDefault() == "--child") return await Child(args.Skip(1).ToArray());
        await Case("piped-binary-large-write-and-eof", async () =>
        {
            await using var connection = Connect();
            using var stdout = new MemoryStream(); using var stderr = new MemoryStream();
            await using var process = await RemoteProcess.OpenAsync(connection, new()
            { Cwd = Environment.CurrentDirectory, Argv = Command("--child", "echo"), OnStdout = bytes => stdout.Write(bytes.Span), OnStderr = bytes => stderr.Write(bytes.Span) });
            await process.WriteAsync(Binary);
            await process.CloseStdinAsync();
            Check(await process.Exited.WaitAsync(TimeSpan.FromSeconds(20)) == 7, "exit code");
            Check(stdout.ToArray().SequenceEqual(Binary), "raw binary stdout survives frame boundaries");
            Check(stderr.ToArray().SequenceEqual(new byte[] { 255, 0, 13, 10 }), "stderr is separate raw bytes");
            await process.WriteAsync("ignored after EOF");
        });
        await Case("exec-decode-stream-boundaries-and-spill-raw-bytes", async () =>
        {
            await using var connection = Connect();
            var stdout = new StringBuilder(); var stderr = new StringBuilder();
            var reply = await connection.RequestAsync("exec", Request("utf8", new() { ["afterBytes"] = 1, ["afterLines"] = 1 }), new()
            { OnEvent = (json, bytes) => (json["stream"]!.GetValue<string>() == "stderr" ? stderr : stdout).Append(Encoding.UTF8.GetString(bytes.Span)) });
            Check(reply.Json["exitCode"]!.GetValue<int>() == 0, "exec exit");
            Check(stdout.ToString() == "a€��", "per-stream decoder and final incomplete code point");
            Check(stderr.ToString() == "separate\n", "stderr decoded independently");
            var path = reply.Json["spillPath"]!.GetValue<string>();
            var raw = await File.ReadAllBytesAsync(path);
            Check(raw.Length == 18 && raw.Contains((byte)255) && raw.Contains((byte)240), "spill preserves raw BOM and malformed bytes");
            DeleteSpill(path);
        });
        await Case("window-counts-tail-and-spill-complete", async () =>
        {
            await using var connection = Connect();
            long delivered = 0, skipped = 0, lines = 0; var tail = new StringBuilder();
            var json = Request("flood", new() { ["afterBytes"] = 4096, ["afterLines"] = 100 });
            json["window"] = new JsonObject { ["maxBytes"] = 1024, ["maxLines"] = 10, ["minIntervalMs"] = 1000, ["bytesPerSecond"] = 1024 };
            var reply = await connection.RequestAsync("exec", json, new()
            {
                OnEvent = (eventJson, bytes) =>
                {
                    delivered += bytes.Length; lines += bytes.Span.Count((byte)10);
                    if (eventJson["skipped"] is JsonObject omitted)
                    { skipped += omitted["bytes"]!.GetValue<long>(); lines += omitted["newlines"]!.GetValue<long>(); tail.Clear(); }
                    tail.Append(Encoding.UTF8.GetString(bytes.Span));
                }
            });
            var path = reply.Json["spillPath"]!.GetValue<string>(); var raw = await File.ReadAllBytesAsync(path);
            Check(skipped > 0, "window actually omitted stale output");
            Check(delivered + skipped == raw.LongLength && lines == 20000, "window accounts every byte and newline");
            Check(tail.ToString().EndsWith("line-19999-é😀\n", StringComparison.Ordinal), "window retained final tail");
            DeleteSpill(path);
        });
        await Case("timeout-abort-and-kill-have-distinct-results", async () =>
        {
            await using var connection = Connect();
            var timed = Request("linger"); timed["timeoutMs"] = 150;
            Check((await Throws<RemoteError>(() => connection.RequestAsync("exec", timed))).Code == "timeout", "timeout classified");
            using var abort = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = connection.RequestAsync("exec", Request("linger"), new() { OnEvent = (_, _) => started.TrySetResult() }, abort.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); abort.Cancel();
            Check((await Throws<RemoteError>(async () => await canceled)).Code == "aborted", "abort classified");
            uint id = 0; long session = 0;
            var killed = connection.RequestAsync("exec", Request("linger"), new()
            { OnStart = (value, generation) => { id = value; session = generation; }, OnEvent = (_, _) => connection.Kill(id, session) });
            Check((await killed.WaitAsync(TimeSpan.FromSeconds(10))).Json["exitCode"]!.GetValue<int>() != 0, "kill is exit result");
        });
        await Case("wrapper-exit-kills-descendant", async () =>
        {
            await using var connection = Connect(); var text = new StringBuilder();
            await using var process = await RemoteProcess.OpenAsync(connection, new()
            { Cwd = Environment.CurrentDirectory, Argv = Command("--child", "wrapper"), OnStdout = bytes => text.Append(Encoding.UTF8.GetString(bytes.Span)) });
            Check(await process.Exited.WaitAsync(TimeSpan.FromSeconds(10)) == 0, "wrapper exits");
            var descendant = int.Parse(text.ToString().Trim());
            await Eventually(() => !Alive(descendant));
        });
        await Case("process-close-and-connection-loss-kill-owned-tree", async () =>
        {
            await using var connection = Connect();
            await using var process = await RemoteProcess.OpenAsync(connection, new()
            { Cwd = Environment.CurrentDirectory, Argv = Command("--child", "linger"), OnStdout = _ => { } });
            var pid = process.Pid; process.Close();
            Check((await Throws<RemoteError>(async () => await process.Exited)).Code == "aborted", "close abort");
            await Eventually(() => !Alive(pid));
            var lost = await RemoteProcess.OpenAsync(connection, new()
            { Cwd = Environment.CurrentDirectory, Argv = Command("--child", "linger"), OnStdout = _ => { } });
            connection.Close(); await Throws<RemoteError>(async () => await lost.Exited); await Eventually(() => !Alive(lost.Pid));
        });
        await Case("parallel-runs-keep-request-and-stream-isolation", async () =>
        {
            await using var connection = Connect();
            await Task.WhenAll(Enumerable.Range(0, 4).Select(async index =>
            {
                using var output = new MemoryStream();
                await using var process = await RemoteProcess.OpenAsync(connection, new()
                { Cwd = Environment.CurrentDirectory, Argv = Command("--child", "echo"), OnStdout = bytes => output.Write(bytes.Span) });
                var bytes = Enumerable.Repeat((byte)(index + 1), 128 * 1024).ToArray();
                await process.WriteAsync(bytes); await process.CloseStdinAsync(); await process.Exited;
                Check(output.ToArray().SequenceEqual(bytes), "request output isolation");
            }));
        });
        await Case("blocked-stdin-write-remains-cancellable", async () =>
        {
            await using var connection = Connect();
            await using var process = await RemoteProcess.OpenAsync(connection, new()
            { Cwd = Environment.CurrentDirectory, Argv = Command("--child", "linger"), OnStdout = _ => { } });
            var write = process.WriteAsync(Binary);
            await Task.Delay(150); process.Close();
            await write.WaitAsync(TimeSpan.FromSeconds(10));
            Check((await Throws<RemoteError>(async () => await process.Exited)).Code == "aborted", "blocked input cancellation");
            await Eventually(() => !Alive(process.Pid));
        });
        await Case("callback-failure-cancels-owned-process", async () =>
        {
            await using var connection = Connect();
            var process = await RemoteProcess.OpenAsync(connection, new()
            { Cwd = Environment.CurrentDirectory, Argv = Command("--child", "linger"), OnStdout = _ => throw new InvalidOperationException("E05 callback failed") });
            var error = await Throws<InvalidOperationException>(async () => await process.Exited);
            Check(error.Message == "E05 callback failed", "callback exception preserved");
            await Eventually(() => !Alive(process.Pid));
        });
        await Case("validation-and-old-daemon-feature-gate", async () =>
        {
            await using var connection = Connect();
            var empty = new JsonObject { ["cwd"] = Environment.CurrentDirectory, ["argv"] = new JsonArray() };
            Check((await Throws<RemoteError>(() => connection.RequestAsync("process", empty))).Code == "spawn_error", "empty argv refused");
            Check((await Throws<RemoteError>(() => connection.RequestAsync("processWrite", new() { ["target"] = 999999 }))).Code == "EBADF", "unknown process refused");
            await using var old = new Connection(new() { Command = Command("--old-daemon") });
            var unsupported = await Throws<RemoteError>(async () => await RemoteProcess.OpenAsync(old, new()
            { Cwd = Environment.CurrentDirectory, Argv = Command("--child", "linger"), OnStdout = _ => { } }));
            Check(unsupported.Code == "not_supported", "old feature gate");
        });
        Console.WriteLine($"PASS {passed} native Env process cases"); return 0;
    }

    private static async Task<int> Child(string[] args)
    {
        var stdout = Console.OpenStandardOutput(); var stderr = Console.OpenStandardError();
        switch (args[0])
        {
            case "echo":
                await Console.OpenStandardInput().CopyToAsync(stdout); await stdout.FlushAsync();
                await stderr.WriteAsync(new byte[] { 255, 0, 13, 10 }); return 7;
            case "utf8":
                await stdout.WriteAsync(new byte[] { 239, 187, 191, 97, 226 }); await stdout.FlushAsync(); await Task.Delay(50);
                await stderr.WriteAsync(Encoding.UTF8.GetBytes("separate\n")); await stderr.FlushAsync();
                await stdout.WriteAsync(new byte[] { 130, 172, 255, 240 }); return 0;
            case "flood":
                for (var index = 0; index < 20000; index++) await stdout.WriteAsync(Encoding.UTF8.GetBytes($"line-{index}-é😀\n")); return 0;
            case "linger":
                await stdout.WriteAsync(Encoding.ASCII.GetBytes("ready\n")); await stdout.FlushAsync(); await Task.Delay(TimeSpan.FromMinutes(5)); return 0;
            case "wrapper":
                var command = Command("--child", "silent"); var start = new ProcessStartInfo(command[0]) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in command.Skip(1)) start.ArgumentList.Add(arg);
                using (var descendant = Process.Start(start)!) { await stdout.WriteAsync(Encoding.ASCII.GetBytes(descendant.Id + "\n")); await stdout.FlushAsync(); }
                return 0;
            case "silent": await Task.Delay(TimeSpan.FromMinutes(5)); return 0;
            default: return 99;
        }
    }
    private static string[] Command(params string[] args) => Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? [Environment.ProcessPath!, typeof(Program).Assembly.Location, .. args] : [Environment.ProcessPath!, .. args];
    private static Connection Connect() => new(new() { Command = Command("--daemon"), StartupTimeout = TimeSpan.FromSeconds(15), AbortGrace = TimeSpan.FromSeconds(5) });
    private static JsonObject Request(string mode, JsonObject? spill = null) => new()
    { ["argv"] = new JsonArray(Command("--child", mode).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()), ["cwd"] = Environment.CurrentDirectory, ["spill"] = spill };
    private static bool Alive(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private static async Task Eventually(Func<bool> predicate) { for (var index = 0; index < 100; index++) { if (predicate()) return; await Task.Delay(50); } throw new Exception("Owned descendant survived"); }
    private static void DeleteSpill(string path) { File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
    private static async Task Case(string name, Func<Task> test) { await test().WaitAsync(TimeSpan.FromSeconds(40)); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task<T> Throws<T>(Func<Task> run) where T : Exception { try { await run().WaitAsync(TimeSpan.FromSeconds(15)); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
}
