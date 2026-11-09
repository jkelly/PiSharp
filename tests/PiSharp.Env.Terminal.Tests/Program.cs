using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Env;
using PiSharp.Env.Daemon;
using PiSharp.Env.Protocol;

internal static class Program
{
    private static int passed;
    private static string? externalDaemon;
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--fixture") return await FixtureAsync(args);
        if (args.FirstOrDefault() == "--daemon")
        {
            await using var daemon = new EnvDaemonSession(Console.OpenStandardInput(), Console.OpenStandardOutput(), new()
            { Token = args[Array.IndexOf(args, "--token") + 1], Operations = TerminalOperations.Create() });
            await daemon.RunAsync(); return 0;
        }
        if (args.Length == 2 && args[0] == "--external-daemon") externalDaemon = Path.GetFullPath(args[1]);
        await Case("client-started-event-and-raw-bytes", async () =>
        {
            await using var connection = Connect("--fixture"); var output = new Collector();
            await using var terminal = await RemoteTerminal.OpenAsync(connection, Options("raw", output));
            Check(terminal.Pid == 42 && await terminal.Exited == 3, "pid and exit code");
            Check(output.Bytes.SequenceEqual(new byte[] { 0x61, 0xff, 0, 0x62 }), "raw output remains undecoded");
        });
        await Case("client-input-order-resize-and-foreground", async () =>
        {
            await using var connection = Connect("--fixture"); var output = new Collector();
            await using var terminal = await RemoteTerminal.OpenAsync(connection, Options("interaction", output));
            for (var index = 0; index < 200; index++) terminal.Write(index.ToString("D3") + "\n");
            await output.WaitForAsync("199\n");
            Check(output.Text == string.Concat(Enumerable.Range(0, 200).Select(index => index.ToString("D3") + "\n")), "writes retain arrival order");
            await terminal.ResizeAsync(120, 40); await output.WaitForAsync("40 120");
            var foreground = await terminal.ForegroundAsync(); Check(foreground == new TerminalForeground(84, "editor"), "foreground response");
            terminal.Write("exit"); Check(await terminal.Exited == 7, "interactive exit");
        });
        await Case("client-splits-large-writes", async () =>
        {
            await using var connection = Connect("--fixture");
            await using var terminal = await RemoteTerminal.OpenAsync(connection, Options("large", new()));
            terminal.Write(new byte[1024 * 1024 + 8193]);
            Check(await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(10)) == 0, "large write arrives in two bounded ordered frames");
        });
        await Case("client-prestart-error-and-close-abort", async () =>
        {
            await using var connection = Connect("--fixture");
            var startError = await Throws<RemoteError>(() => RemoteTerminal.OpenAsync(connection, Options("reject", new())));
            Check(startError.Code == "spawn_error", "pre-start failure rejects open");
            await using var terminal = await RemoteTerminal.OpenAsync(connection, Options("lifetime", new()));
            terminal.Close(); terminal.Close();
            Check((await Throws<RemoteError>(async () => await terminal.Exited)).Code == "aborted", "close abort code");
        });
        await Case("client-old-terminal-never-reconnects", async () =>
        {
            await using var connection = Connect("--fixture");
            await using var terminal = await RemoteTerminal.OpenAsync(connection, Options("die", new()));
            var session = await connection.SessionAsync(); terminal.Write("die");
            Check((await Throws<RemoteError>(async () => await terminal.Exited)).IsConnectionLost, "exited reports loss");
            Check(await connection.SessionAsync() != session, "next request reconnects");
            Check((await Throws<RemoteError>(async () => await terminal.ForegroundAsync())).IsConnectionLost, "stale terminal is session bound");
        });
        await Case("daemon-rejects-invalid-sizes-and-unknown-handles", async () =>
        {
            await using var connection = Connect("--daemon");
            foreach (var size in new[] { 0, -1, 4097 })
            {
                var error = await Throws<RemoteError>(() => connection.RequestAsync("pty", new()
                { ["cwd"] = Environment.CurrentDirectory, ["cols"] = size, ["rows"] = 24 }));
                Check(error.Code == "EINVAL", "invalid size");
            }
            foreach (var operation in new[] { "ptyWrite", "ptyResize", "ptyForeground" })
                Check((await Throws<RemoteError>(() => connection.RequestAsync(operation, new() { ["target"] = 999, ["cols"] = 80, ["rows"] = 24 }))).Code == "EBADF", "missing target");
        });
        if (OperatingSystem.IsWindows())
        {
            await Case("windows-terminal-is-explicitly-unsupported", async () =>
            {
                await using var connection = Connect("--daemon");
                var error = await Throws<RemoteError>(() => RemoteTerminal.OpenAsync(connection, Options("/bin/sh", new())));
                Check(error.Code == "ENOTSUP", "Windows must not fake a terminal with pipes");
            });
        }
        else
        {
            await NativeCasesAsync();
        }
        Console.WriteLine($"Passed {passed} pi-env terminal cases ({(OperatingSystem.IsWindows() ? "Windows protocol and ENOTSUP" : "Unix native PTY")}).");
        return 0;
    }

    private static async Task NativeCasesAsync()
    {
        await Case("native-terminal-input-resize-environment-and-exit", async () =>
        {
            await using var connection = Connect("--daemon"); var output = new Collector();
            await using var terminal = await RemoteTerminal.OpenAsync(connection, Options("/bin/sh", output) with
            { Env = new Dictionary<string, string> { ["PS1"] = "PROMPT> ", ["TERM"] = "xterm-256color" } });
            terminal.Write("test -t 0 && printf 'TTY-OK\\n'; stty size; printf '%s\\n' \"$TERM\"\n");
            await output.WaitForAsync("TTY-OK\r\n"); await output.WaitForAsync("24 80"); await output.WaitForAsync("xterm-256color\r\n");
            await terminal.ResizeAsync(100, 30); terminal.Write("stty size\n"); await output.WaitForAsync("30 100");
            terminal.Write("exit 3\n"); Check(await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(10)) == 3, "native exit status");
        });
        await Case("native-terminal-binary-output", async () =>
        {
            await using var connection = Connect("--daemon"); var output = new Collector();
            await using var terminal = await RemoteTerminal.OpenAsync(connection, Options("/bin/sh", output) with { Argv = ["/bin/sh", "-c", "printf 'a\\377b'"] });
            Check(await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(10)) == 0, "binary child exits");
            Check(output.Bytes.SequenceEqual(new byte[] { 0x61, 0xff, 0x62 }), "terminal is byte preserving");
        });
        await Case("native-terminal-drains-output-through-slow-consumer", async () =>
        {
            await using var connection = Connect("--daemon"); long received = 0;
            await using var terminal = await RemoteTerminal.OpenAsync(connection, Options("/bin/sh", new()) with
            {
                Argv = ["/bin/sh", "-c", "head -c 524288 /dev/zero"],
                OnData = bytes => { Interlocked.Add(ref received, bytes.Length); Thread.Sleep(150); }
            });
            Check(await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(20)) == 0 && received == 524288,
                "exit grace does not discard output waiting on the transport");
        });
        await Case("native-terminal-foreground-job-and-close", async () =>
        {
            await using var connection = Connect("--daemon"); var output = new Collector();
            await using var terminal = await RemoteTerminal.OpenAsync(connection, Options("/bin/sh", output) with { Argv = ["/bin/sh", "-i"] });
            terminal.Write("printf 'READY\\n'\n"); await output.WaitForAsync("READY\r\n");
            Check(await terminal.ForegroundAsync() is null, "shell itself is not a foreground job");
            terminal.Write("sleep 100\n"); TerminalForeground? foreground = null;
            for (var attempt = 0; attempt < 100; attempt++)
            { foreground = await terminal.ForegroundAsync(); if (foreground?.Name == "sleep") break; await Task.Delay(20); }
            Check(foreground is { Name: "sleep" } && foreground.Pid != terminal.Pid, "foreground group and process name");
            terminal.Close(); Check((await Throws<RemoteError>(async () => await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(10)))).Code == "aborted", "close kills terminal");
            for (var attempt = 0; attempt < 100 && IsRunning(foreground!.Pid); attempt++) await Task.Delay(20);
            Check(!IsRunning(foreground!.Pid), "close kills foreground job");
            Check((await Throws<RemoteError>(async () => await terminal.ForegroundAsync())).Code == "EBADF", "terminal removed after close");
        });
        await Case("native-terminal-ordered-input-and-missing-executable", async () =>
        {
            await using var connection = Connect("--daemon"); var output = new Collector();
            var directory = Path.Combine(Path.GetTempPath(), "pisharp-pty-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            try
            {
                await using var terminal = await RemoteTerminal.OpenAsync(connection, Options("/bin/sh", output) with
                { Cwd = directory, Argv = ["/bin/sh", "-c", "stty raw -echo; printf READY; head -c 40000 > input.bin"] });
                await output.WaitForAsync("READY");
                var expected = string.Concat(Enumerable.Range(0, 4000).Select(index => index.ToString("D9") + "\n"));
                for (var offset = 0; offset < expected.Length; offset += 10) terminal.Write(expected.Substring(offset, 10));
                Check(await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(20)) == 0, "ordered input consumer exits");
                Check(await File.ReadAllTextAsync(Path.Combine(directory, "input.bin")) == expected, "all 4000 writes preserved in order");
            }
            finally { Directory.Delete(directory, recursive: true); }
            var error = await Throws<RemoteError>(() => RemoteTerminal.OpenAsync(connection, Options("/pi-sharp-nonexistent-program", new())));
            Check(error.Code == "spawn_error", "exec failure reported before started");
        });
        await Case("native-terminal-default-login-shell-and-env-clear", async () =>
        {
            await using var connection = Connect("--daemon"); var output = new Collector();
            await using var terminal = await RemoteTerminal.OpenAsync(connection, Options("/bin/sh", output) with
            { Argv = null, InheritEnv = false, Env = new Dictionary<string, string> { ["SHELL"] = "/bin/sh" } });
            terminal.Write("printf 'LOGIN:%s\\n' \"$0\"; exit\n");
            Check(await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(10)) == 0, "default shell exits"); await output.WaitForAsync("LOGIN:-sh");
        });
        await Case("native-terminal-kill-versus-later-abort", async () =>
        {
            await using var connection = Connect("--daemon"); await connection.InfoAsync();
            foreach (var abortAfterKill in new[] { false, true })
            {
                uint requestId = 0; long session = 0; using var cancel = new CancellationTokenSource();
                var result = connection.RequestAsync("pty", new()
                {
                    ["argv"] = new JsonArray("/bin/sleep", "100"), ["cwd"] = Environment.CurrentDirectory,
                    ["cols"] = 80, ["rows"] = 24
                }, new() { OnStart = (id, run) => { requestId = id; session = run; } }, cancel.Token);
                connection.Kill(requestId, session);
                if (abortAfterKill)
                {
                    cancel.Cancel();
                    Check((await Throws<RemoteError>(async () => await result.WaitAsync(TimeSpan.FromSeconds(10)))).Code == "aborted", "later ordinary cancellation wins over earlier kill");
                }
                else Check((await result.WaitAsync(TimeSpan.FromSeconds(10))).Json["exitCode"]!.GetValue<int>() >= 128, "kill mode returns process status");
            }
        });
        await Case("native-terminal-disconnect-kills-session-leader", async () =>
        {
            await using var connection = Connect("--daemon");
            var terminal = await RemoteTerminal.OpenAsync(connection, Options("/bin/sleep", new()) with { Argv = ["/bin/sleep", "100"] });
            connection.Close(); await Throws<RemoteError>(async () => await terminal.Exited);
            for (var attempt = 0; attempt < 100 && IsRunning(terminal.Pid); attempt++) await Task.Delay(20);
            Check(!IsRunning(terminal.Pid), "disconnect reaps started process");
        });
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                if (stat[(stat.LastIndexOf(')') + 2)..].StartsWith('Z')) return false;
            }
            using var process = Process.GetProcessById(pid); return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (IOException) { return false; }
    }
    private static TerminalOptions Options(string program, Collector output) => new()
    { Cwd = Environment.CurrentDirectory, Argv = [program], Cols = 80, Rows = 24, OnData = output.Add };
    private static Connection Connect(string mode) => new(new()
    { Command = Command(mode), StartupTimeout = TimeSpan.FromSeconds(10), PingInterval = TimeSpan.FromSeconds(1), SilenceLimit = TimeSpan.FromSeconds(30) });
    private static string[] Command(string mode)
    {
        if (mode == "--daemon" && externalDaemon is { } daemon) return [daemon];
        var process = Environment.ProcessPath!;
        return Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [process, typeof(Program).Assembly.Location, mode] : [process, mode];
    }
    private static async Task Case(string name, Func<Task> action) { await action().WaitAsync(TimeSpan.FromSeconds(40)); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }

    private sealed class Collector
    {
        private readonly object gate = new(); private readonly MemoryStream bytes = new();
        public byte[] Bytes { get { lock (gate) return bytes.ToArray(); } }
        public string Text => Encoding.UTF8.GetString(Bytes);
        public void Add(ReadOnlyMemory<byte> value) { lock (gate) bytes.Write(value.Span); }
        public async Task WaitForAsync(string text)
        { for (var attempt = 0; attempt < 500; attempt++) { if (Text.Contains(text, StringComparison.Ordinal)) return; await Task.Delay(20); } throw new Exception("Missing output " + text + ": " + Text); }
    }

    private static async Task<int> FixtureAsync(string[] args)
    {
        var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
        await output.WriteAsync(Encoding.ASCII.GetBytes("PI-ENV " + args[Array.IndexOf(args, "--token") + 1] + "\n")); await output.FlushAsync();
        uint terminal = 0; var mode = ""; var total = 0; var chunks = 0;
        while (await FrameCodec.ReadAsync(input) is { } request)
        {
            if (request.Type == FrameType.Ping) continue;
            if (request.Type == FrameType.Cancel)
            { await FrameCodec.WriteAsync(output, new(FrameType.Error, request.Id, new() { ["code"] = "aborted", ["message"] = "aborted" })); continue; }
            var operation = request.Json["op"]!.GetValue<string>();
            if (operation == "hello")
                await FrameCodec.WriteAsync(output, new(FrameType.Result, request.Id, new()
                { ["protocol"] = 1, ["version"] = "fixture", ["os"] = "linux", ["arch"] = "x86_64", ["home"] = "/", ["tmpdir"] = "/tmp", ["separator"] = "/", ["cwd"] = "/", ["pid"] = Environment.ProcessId }));
            else if (operation == "pty")
            {
                terminal = request.Id; mode = request.Json["argv"]![0]!.GetValue<string>();
                if (mode == "reject") { await FrameCodec.WriteAsync(output, new(FrameType.Error, terminal, new() { ["code"] = "spawn_error", ["message"] = "bad program" })); continue; }
                await FrameCodec.WriteAsync(output, new(FrameType.Event, terminal, new() { ["kind"] = "started", ["pid"] = 42 }));
                if (mode == "raw")
                {
                    await FrameCodec.WriteAsync(output, new(FrameType.Event, terminal, new() { ["kind"] = "output" }, new byte[] { 0x61, 0xff, 0, 0x62 }));
                    await FrameCodec.WriteAsync(output, new(FrameType.Result, terminal, new() { ["exitCode"] = 3 }));
                }
            }
            else if (operation == "ptyWrite")
            {
                if (mode == "die") return 0;
                if (mode == "large")
                {
                    Check(request.Payload.Length <= 1024 * 1024, "client chunk size"); total += request.Payload.Length; chunks++;
                    if (total == 1024 * 1024 + 8193) { Check(chunks == 2, "client chunk count"); await FrameCodec.WriteAsync(output, new(FrameType.Result, terminal, new() { ["exitCode"] = 0 })); }
                }
                else if (Encoding.UTF8.GetString(request.Payload.Span) == "exit")
                    await FrameCodec.WriteAsync(output, new(FrameType.Result, terminal, new() { ["exitCode"] = 7 }));
                else await FrameCodec.WriteAsync(output, new(FrameType.Event, terminal, new() { ["kind"] = "output" }, request.Payload));
                await FrameCodec.WriteAsync(output, new(FrameType.Result, request.Id, new()));
            }
            else if (operation == "ptyResize")
            {
                await FrameCodec.WriteAsync(output, new(FrameType.Event, terminal, new() { ["kind"] = "output" }, Encoding.ASCII.GetBytes($"{request.Json["rows"]} {request.Json["cols"]}")));
                await FrameCodec.WriteAsync(output, new(FrameType.Result, request.Id, new()));
            }
            else if (operation == "ptyForeground") await FrameCodec.WriteAsync(output, new(FrameType.Result, request.Id, new()
            { ["foreground"] = new JsonObject { ["pid"] = 84, ["name"] = "editor" } }));
        }
        return 0;
    }
}
