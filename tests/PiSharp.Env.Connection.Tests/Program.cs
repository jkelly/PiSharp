// PiSharp-only: offline adversarial protocol/connection fixtures corresponding to pi-env connection.test.ts and remote-abort.test.ts.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Env;
using PiSharp.Env.Protocol;

internal static class Program
{
    private static int passed;
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.FirstOrDefault() == "--worker") return await Worker(args);
            return await RunCases();
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task<int> RunCases()
    {
        await Case("fragmented-binary-frame-and-eof", async () =>
        {
            var expected = new ProtocolFrame(FrameType.Event, uint.MaxValue, new() { ["text"] = "中\ud83d\ude42" }, new byte[] { 0, 255, 13, 10 });
            using var stream = new FragmentedStream(FrameCodec.Encode(expected));
            var got = await FrameCodec.ReadAsync(stream);
            Check(got is not null && got.Id == uint.MaxValue && got.Type == FrameType.Event && got.Json["text"]!.GetValue<string>() == "中\ud83d\ude42", "frame fields");
            Check(got!.Payload.Span.SequenceEqual(expected.Payload.Span), "raw payload");
            Check(await FrameCodec.ReadAsync(stream) is null, "clean EOF");
        });
        await Case("invalid-frame-bounds-json-and-truncation", async () =>
        {
            foreach (var size in new uint[] { 0, 8, FrameCodec.MaxFrameBytes + 1 })
            {
                var header = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(header, size);
                await Throws<InvalidDataException>(async () => await FrameCodec.ReadAsync(new MemoryStream(header)));
            }
            var valid = FrameCodec.Encode(new(FrameType.Request, 7, new() { ["op"] = "hello" }));
            await Throws<EndOfStreamException>(async () => await FrameCodec.ReadAsync(new MemoryStream(valid[..2])));
            await Throws<EndOfStreamException>(async () => await FrameCodec.ReadAsync(new MemoryStream(valid[..^1])));
            var bad = FrameCodec.Encode(new(FrameType.Request, 42, new())); bad[13] = (byte)'['; bad[14] = (byte)']';
            using var recoverable = new MemoryStream([.. bad, .. valid]);
            var invalid = await Throws<ProtocolJsonException>(async () => await FrameCodec.ReadAsync(recoverable));
            Check(invalid.Id == 42 && invalid.Type == FrameType.Request, "malformed JSON retains identity");
            Check((await FrameCodec.ReadAsync(recoverable))?.Id == 7, "bad JSON consumes only its frame");
            BinaryPrimitives.WriteUInt32BigEndian(valid.AsSpan(9), uint.MaxValue);
            await Throws<InvalidDataException>(async () => await FrameCodec.ReadAsync(new MemoryStream(valid)));
            await Throws<InvalidDataException>(() => Task.Run(() => FrameCodec.Encode(new(FrameType.Result, 1, new(), new byte[FrameCodec.MaxFrameBytes]))));
        });
        await Case("lazy-sync-events-and-session-isolation", async () =>
        {
            var starts = 0;
            await using var connection = new Connection(Options("normal") with { Command = null, CommandFactory = _ => { starts++; return Task.FromResult<IReadOnlyList<string>>(Command("normal")); } });
            Check(starts == 0, "construction lazy");
            var infos = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => connection.InfoAsync()));
            Check(starts == 1 && infos.All(info => info.Protocol == 1), "shared startup");
            var session = await connection.SessionAsync();
            var eventBytes = Array.Empty<byte>(); uint requestId = 0; long requestSession = 0;
            var reply = await connection.RequestAsync("echo", new() { ["number"] = 5 }, new()
            {
                Payload = new byte[] { 0, 255 }, OnEvent = (_, bytes) => eventBytes = bytes.ToArray(),
                OnStart = (id, run) => { requestId = id; requestSession = run; },
            });
            Check(reply.Json["number"]!.GetValue<int>() == 5 && reply.Payload.SequenceEqual(new byte[] { 0, 255 }), "echo round trip");
            Check(eventBytes.SequenceEqual(new byte[] { 0, 255 }) && requestId > 0 && requestSession == session, "events routed");
            var lost = await Throws<RemoteError>(() => connection.RequestAsync("die", new())); Check(lost.IsConnectionLost, "lost status");
            Check(await connection.SessionAsync() != session && starts == 2, "restart next request");
            var stale = await Throws<RemoteError>(() => connection.RequestAsync("echo", new(), new() { Session = session }));
            Check(stale.IsConnectionLost && starts == 2, "old handle never migrates");
            connection.Close();
            await Throws<RemoteError>(() => connection.InfoAsync());
        });
        await Case("abort-start-does-not-cancel-shared-factory", async () =>
        {
            var factory = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var connection = new Connection(Options("normal") with { Command = null, CommandFactory = _ => factory.Task });
            using var cancel = new CancellationTokenSource(50);
            var elapsed = Stopwatch.StartNew();
            var error = await Throws<RemoteError>(() => connection.InfoAsync(cancel.Token));
            Check(error.Code == "aborted" && elapsed.Elapsed < TimeSpan.FromSeconds(1), "start wait abort immediate");
            factory.SetResult(Command("normal"));
            Check((await connection.InfoAsync()).Protocol == 1, "shared start survives");
        });
        await Case("sent-abort-has-local-deadline", async () =>
        {
            await using var connection = new Connection(Options("silent"));
            await connection.InfoAsync();
            using var cancel = new CancellationTokenSource(50);
            var elapsed = Stopwatch.StartNew();
            var error = await Throws<RemoteError>(() => connection.RequestAsync("hold", new(), cancellationToken: cancel.Token));
            Check(error.Code == "aborted" && elapsed.Elapsed >= TimeSpan.FromMilliseconds(100) && elapsed.Elapsed < TimeSpan.FromSeconds(2), "unanswered cancel bounded");
        });
        await Case("cancel-returns-daemon-error-before-grace", async () =>
        {
            await using var connection = new Connection(Options("normal"));
            await connection.InfoAsync();
            using var cancel = new CancellationTokenSource(50);
            var error = await Throws<RemoteError>(() => connection.RequestAsync("hold", new(), cancellationToken: cancel.Token));
            Check(error.Code == "aborted" && error.Fields["spillPath"]!.GetValue<string>() == "remote-spill", "daemon abort details retained");
        });
        await Case("startup-timeout-and-sanitized-stderr", async () =>
        {
            var unresolved = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var connection = new Connection(Options("normal") with
            {
                Command = null, CommandFactory = ct => unresolved.Task.WaitAsync(ct), StartupTimeout = TimeSpan.FromMilliseconds(150),
            }))
            {
                var error = await Throws<RemoteError>(() => connection.InfoAsync());
                Check(error.IsConnectionLost && error.Message.Contains("did not answer", StringComparison.Ordinal), "factory timeout included");
            }
            await using (var connection = new Connection(Options("never") with { StartupTimeout = TimeSpan.FromMilliseconds(200) }))
            {
                var error = await Throws<RemoteError>(() => connection.InfoAsync());
                Check(error.IsConnectionLost && error.Message.Contains("did not answer", StringComparison.Ordinal), "bounded startup");
            }
            await using (var connection = new Connection(Options("stderr")))
            {
                var error = await Throws<RemoteError>(() => connection.InfoAsync());
                Check(error.Message.Contains("last-line", StringComparison.Ordinal) && !error.Message.Contains('\x1b'), "sanitized diagnostic tail");
                Check(!error.Message.Contains("line-0\n", StringComparison.Ordinal), "tail limited to twenty lines");
            }
        });
        await Case("corrupt-ready-session-fails-and-restarts", async () =>
        {
            await using var connection = new Connection(Options("normal"));
            await connection.InfoAsync();
            var elapsed = Stopwatch.StartNew();
            var error = await Throws<RemoteError>(() => connection.RequestAsync("corrupt", new()));
            Check(error.IsConnectionLost && elapsed.Elapsed < TimeSpan.FromSeconds(2), "corruption fails promptly");
            Check(error.Message.Contains("Invalid pi-env frame length", StringComparison.Ordinal), "framing fault classified at the reader");
            Check((await connection.InfoAsync()).Protocol == 1, "corruption restart");
        });
        await Case("artifact-hello-validation-is-enforced", async () =>
        {
            await using var connection = new Connection(Options("normal") with
            {
                ValidateHello = info => { if (info.Version != "pinned") throw new InvalidDataException("Artifact version mismatch"); },
            });
            var error = await Throws<RemoteError>(() => connection.InfoAsync());
            Check(error.Message.Contains("Artifact version mismatch", StringComparison.Ordinal), "validator failed startup");
        });
        await Case("disposal-joins-original-factory-cleanup", async () =>
        {
            var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<IReadOnlyList<string>> Factory(CancellationToken cancellationToken)
            {
                began.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return Command("normal"); }
                finally { cleaning.SetResult(); await release.Task; }
            }
            var connection = new Connection(Options("normal") with { Command = null, CommandFactory = Factory });
            var start = connection.InfoAsync();
            await began.Task;
            var disposing = connection.DisposeAsync().AsTask();
            await cleaning.Task;
            var failure = await Throws<RemoteError>(() => start);
            Check(failure.Message == "Connection closed", "startup wait releases on close");
            Check(!disposing.IsCompleted, "disposal retains factory finally ownership");
            release.SetResult();
            await disposing;

            var unresolved = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stubborn = new Connection(Options("normal") with
            {
                Command = null, CommandFactory = _ => { factoryStarted.SetResult(); return unresolved.Task; },
                ShutdownTimeout = TimeSpan.FromMilliseconds(100),
            });
            var waiting = stubborn.InfoAsync();
            await factoryStarted.Task;
            await Throws<TimeoutException>(() => stubborn.DisposeAsync().AsTask());
            await Throws<RemoteError>(() => waiting);
            // The late original remains observed, and completion cannot start a daemon after Close.
            unresolved.SetResult(Command("normal"));
        });
        Console.WriteLine($"Passed {passed} offline pi-env connection cases.");
        return 0;
    }

    private static async Task Case(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }

    private static string[] Command(string mode)
    {
        var executable = Environment.ProcessPath ?? throw new Exception("Missing process path");
        return Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [executable, typeof(Program).Assembly.Location, "--worker", mode] : [executable, "--worker", mode];
    }
    private static ConnectionOptions Options(string mode) => new()
    {
        Command = Command(mode), StartupTimeout = TimeSpan.FromSeconds(5), AbortGrace = TimeSpan.FromMilliseconds(150),
        PingInterval = TimeSpan.FromMilliseconds(100), SilenceLimit = TimeSpan.FromSeconds(5),
    };

    private static async Task<int> Worker(string[] args)
    {
        var mode = args[1];
        if (mode == "never") { await Task.Delay(10000); return 0; }
        if (mode == "stderr")
        {
            for (var i = 0; i < 30; i++) Console.Error.WriteLine("\x1b[31mline-" + i + "\x1b[0m");
            Console.Error.WriteLine("\x1b]0;hidden\a\x1b[31mlast-line\x1b[0m"); return 3;
        }
        var tokenIndex = Array.IndexOf(args, "--token");
        var output = Console.OpenStandardOutput();
        await output.WriteAsync(Encoding.UTF8.GetBytes("profile noise\nPI-ENV " + args[tokenIndex + 1] + "\n"));
        await output.FlushAsync();
        var input = Console.OpenStandardInput();
        while (await FrameCodec.ReadAsync(input) is { } request)
        {
            if (request.Type == FrameType.Ping) continue;
            if (request.Type == FrameType.Cancel)
            {
                if (mode != "silent") await FrameCodec.WriteAsync(output, new(FrameType.Error, request.Id, new() { ["code"] = "aborted", ["message"] = "aborted", ["spillPath"] = "remote-spill" }));
                continue;
            }
            var op = request.Json["op"]!.GetValue<string>();
            if (op == "hello")
                await FrameCodec.WriteAsync(output, new(FrameType.Result, request.Id, new()
                {
                    ["protocol"] = 1, ["version"] = "test", ["os"] = "windows", ["arch"] = "x86_64", ["home"] = "C:\\", ["tmpdir"] = "C:\\temp",
                    ["separator"] = "\\", ["cwd"] = "C:\\", ["pid"] = Environment.ProcessId, ["driveCwds"] = new JsonObject(), ["features"] = new JsonArray("process", "listFiles"),
                }));
            else if (op == "echo")
            {
                await FrameCodec.WriteAsync(output, new(FrameType.Event, request.Id, new() { ["kind"] = "output" }, request.Payload));
                await FrameCodec.WriteAsync(output, new(FrameType.Result, request.Id, request.Json, request.Payload));
            }
            else if (op == "die") return 4;
            else if (op == "corrupt") { await output.WriteAsync(new byte[] { 0, 0, 0, 8 }); await output.FlushAsync(); }
        }
        return 0;
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], cancellationToken);
    }
}
