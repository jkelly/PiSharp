using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Execution;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Serialization;
using PiSharp.Tools.Processes;
using static Assert;

// wire.rpc-bash-ansi (1.1.0): bash-executor.ts through the RPC bash/abort_bash commands (rpc-mode.ts) and the
// bashExecution session record (agent-session.ts executeBash/recordBashResult). Authored expectations.
internal static class UserBashTests
{
    private static readonly ModelDescriptor Model = DurationTests.Model;
    private static readonly JsonData ModelWire = JsonData.Parse("""{"id":"wire-model","api":"openai-responses","provider":"authored-provider","name":"Authored wire model","baseUrl":"https://offline.invalid","reasoning":false,"input":["text"],"contextWindow":131072,"maxTokens":8192,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""");

    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("wire.rpc-bash.split-ansi-updates-response-and-record-byte-exact", SplitAnsi);
        yield return ("wire.rpc-bash.excluded-abort-truncation-and-spill", AbortAndSpill);
        yield return ("wire.rpc-bash.executor-failure-rolling-buffer-and-cancelled-exit", ExecutorRules);
        if (OperatingSystem.IsWindows()) yield return ("wire.rpc-bash.native-process-raw-stream", NativeProcess);
    }

    /// <summary>Scripted source BashOperations: raw chunks, then an exit code or a wait for cancellation.</summary>
    private sealed class Operations(int? exitCode, params byte[][] chunks) : IShellOperations
    {
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WaitForCancellation { get; init; }
        public Exception? Failure { get; init; }
        public string? Command, WorkingDirectory;
        public async ValueTask<int?> ExecuteAsync(string command, string workingDirectory, ProcessRawOutputCallback onData, CancellationToken token)
        {
            Command = command; WorkingDirectory = workingDirectory;
            foreach (var chunk in chunks) await onData(chunk);
            Delivered.TrySetResult();
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, token);
            if (Failure is not null) throw Failure;
            return exitCode;
        }
    }

    /// <summary>The same mapping the CLI host (UserBashHost) applies from the source executor to the session capability.</summary>
    private sealed class Capability(ShellCommandExecutor executor) : IUserBashExecutor
    {
        public async Task<UserBashResult> ExecuteAsync(UserBashExecutionRequest request, UserBashProgress progress, CancellationToken token)
        {
            var result = await executor.ExecuteAsync(request.Command, request.WorkingDirectory, chunk => new(progress(chunk)), token);
            return new(result.Output, result.ExitCode, result.Cancelled, result.Truncated, result.FullOutputPath);
        }
    }

    private sealed class NoTransport : IChatTransport
    {
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken token = default) =>
            throw new InvalidOperationException("No model turn is expected.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Directory { get; init; }
        public required PersistentAgentSession Session { get; init; }
        public required RpcSessionDispatcher Dispatcher { get; init; }
        public required MemoryStream Output { get; init; }
        public static async Task<Fixture> Create(IShellOperations operations)
        {
            var directory = Path.Combine(Path.GetTempPath(), "PiSharp-wire-sync-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory); var ids = 0;
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "bash-header",
                timestamp = "2026-10-08T00:00:00.000Z", cwd = directory }));
            var session = await PersistentAgentSession.CreateAsync(Path.Combine(directory, "session.jsonl"), header,
                new AgentConfiguration(Model, new NoTransport(), []), () => 123, () => "bash-entry-" + Interlocked.Increment(ref ids));
            var spill = 0;
            var executor = new ShellCommandExecutor(operations, directory, nextSpillFileName: () => "pi-bash-" + (++spill).ToString("x16") + ".log");
            var output = new MemoryStream();
            var dispatcher = new RpcSessionDispatcher(session, new JsonlWriter(output), () => 123, [new(Model, ModelWire)], userBash: new Capability(executor));
            return new() { Directory = directory, Session = session, Dispatcher = dispatcher, Output = output };
        }
        public Task Send(string json) => Dispatcher.SubmitAsync(JsonData.Parse(json));
        public string Text() { lock (Output) return Encoding.UTF8.GetString(Output.ToArray()); }
        public string[] Messages() => Session.Snapshot.Log.Entries.Where(entry => entry.Type == "message")
            .Select(entry => entry.WireBody.Value.GetProperty("message").GetRawText()).ToArray();
        public async ValueTask DisposeAsync()
        {
            await Dispatcher.DisposeAsync(); await Session.DisposeAsync();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    // PiSharp-built RPC/session records escape non-ASCII text as Utf8JsonWriter does (a JSON-equal spelling of the source's
    // raw UTF-8); e-acute is written as backslash, "u00E9".
    private static readonly string Escaped = (char)92 + "u00E9";

    private static async Task SplitAnsi()
    {
        // The SGR parameter "31" is split across chunks, as is an OSC title and a two-byte UTF-8 character.
        var e = Encoding.UTF8.GetBytes("é");
        var operations = new Operations(3, Bytes("red:\u001b[3"), Bytes("1mX\u001b[0m\r\nok\u001b]0;ti"), Bytes("tle\u0007 caf"), [e[0]], [e[1], (byte)'\n']);
        await using var fixture = await Fixture.Create(operations);
        await fixture.Send("""{"id":"b1","type":"bash","command":"printf colors"}""");
        Equal("""
            {"type":"bash_execution_update","id":"b1","delta":"red:"}
            {"type":"bash_execution_update","id":"b1","delta":"X\nok"}
            {"type":"bash_execution_update","id":"b1","delta":" caf"}
            {"type":"bash_execution_update","id":"b1","delta":"{E}\n"}
            {"id":"b1","type":"response","command":"bash","success":true,"data":{"output":"red:X\nok caf{E}\n","exitCode":3,"cancelled":false,"truncated":false}}

            """.ReplaceLineEndings("\n").Replace("{E}", Escaped), fixture.Text());
        Equal("printf colors", operations.Command); Equal(fixture.Directory, operations.WorkingDirectory);
        Equal("""{"role":"bashExecution","command":"printf colors","output":"red:X\nok caf{E}\n","exitCode":3,"cancelled":false,"truncated":false,"timestamp":123}""".Replace("{E}", Escaped),
            fixture.Messages().Single());
        // abort_bash with nothing running is an ordinary success without data.
        await fixture.Send("""{"id":"a0","type":"abort_bash"}""");
        Check(fixture.Text().EndsWith("""{"id":"a0","type":"response","command":"abort_bash","success":true}""" + "\n", StringComparison.Ordinal), "abort_bash response");
        // An update carries no id when the command had none, and excludeFromContext:false is recorded as given.
        await fixture.Send("""{"type":"bash","command":"again","excludeFromContext":false}""");
        Check(fixture.Text().Contains("""{"type":"bash_execution_update","delta":"red:"}""", StringComparison.Ordinal), "id-less update");
        Check(fixture.Messages().Last().EndsWith("\"timestamp\":123,\"excludeFromContext\":false}", StringComparison.Ordinal), "explicit include");
        await fixture.Send("""{"id":"bad","type":"bash","command":"x","excludeFromContext":"yes"}""");
        Check(fixture.Text().EndsWith("""{"id":"bad","type":"response","command":"bash","success":false,"error":"Command excludeFromContext must be a boolean when present."}""" + "\n",
            StringComparison.Ordinal), "invalid excludeFromContext");
    }

    private static async Task AbortAndSpill()
    {
        // Six 10KB chunks: more than 50KB of raw output has arrived when the sixth chunk is appended, so the spill file
        // receives the retained chunks and then everything after; the final output is the truncated tail.
        var lines = Enumerable.Range(0, 6).Select(index => string.Concat(Enumerable.Range(0, 1000).Select(line => $"c{index}-{line:0000}\n".PadLeft(10, '.')))).ToArray();
        var operations = new Operations(0, lines.Select(Bytes).ToArray()) { WaitForCancellation = true };
        await using var fixture = await Fixture.Create(operations);
        var bash = fixture.Send("""{"id":"b2","type":"bash","command":"stream","excludeFromContext":true}""");
        await operations.Delivered.Task;
        Check(fixture.Session.IsUserBashRunning, "bash not running");
        await fixture.Send("""{"id":"a2","type":"abort_bash"}""");
        await bash;
        var records = fixture.Text().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement).ToArray();
        Equal(6, records.Count(record => record.GetProperty("type").GetString() == "bash_execution_update"));
        Equal("""{"id":"a2","type":"response","command":"abort_bash","success":true}""",
            records.Single(record => record.TryGetProperty("command", out var command) && command.GetString() == "abort_bash").GetRawText());
        var data = records.Last().GetProperty("data");
        Equal("output,cancelled,truncated,fullOutputPath", string.Join(',', data.EnumerateObject().Select(property => property.Name)));
        var full = string.Concat(lines);
        // The tail keeps the last 2000 lines (20KB here), the line limit being hit before the 50KB byte limit;
        // the source truncateTail joins kept lines without the final newline.
        Equal((lines[4] + lines[5])[..^1], data.GetProperty("output").GetString());
        Check(data.GetProperty("cancelled").GetBoolean() && data.GetProperty("truncated").GetBoolean(), "cancelled truncated result");
        var path = data.GetProperty("fullOutputPath").GetString()!;
        Equal(Path.Combine(fixture.Directory, "pi-bash-0000000000000001.log"), path);
        Equal(full, File.ReadAllText(path));
        var message = JsonDocument.Parse(fixture.Messages().Single()).RootElement;
        Equal("command,output,cancelled,truncated,fullOutputPath,timestamp,excludeFromContext",
            string.Join(',', message.EnumerateObject().Skip(1).Select(property => property.Name)));
        Check(message.GetProperty("excludeFromContext").GetBoolean(), "excluded record");
    }

    private static async Task ExecutorRules()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PiSharp-wire-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // A failure that is not a cancellation propagates; the spill stream is still closed.
            var failure = new IOException("spawn failed");
            var failing = new ShellCommandExecutor(new Operations(null, Bytes("partial")) { Failure = failure }, directory);
            Check(ReferenceEquals(failure, await Throws<IOException>(() => failing.ExecuteAsync("x", directory, null, CancellationToken.None))), "original failure");
            // A cancelled command reports no exit code even when the operations returned one.
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var result = await new ShellCommandExecutor(new Operations(7, Bytes("tail\u001b[")), directory).ExecuteAsync("x", directory, null, cancelled.Token);
            // At flush an unfinished CSI is stripped as it is: the escape goes, the "[" stays (source flushOutput).
            Equal(new ShellCommandResult("tail[", null, true, false, null), result);
            // Only the newest chunks within twice the 50KB limit are retained; older ones leave the buffer, not the spill file.
            var big = Enumerable.Range(0, 30).Select(index => new string((char)('a' + index % 26), 4096) + "\n").ToArray();
            var spills = 0;
            var rolled = await new ShellCommandExecutor(new Operations(0, big.Select(Bytes).ToArray()), directory,
                nextSpillFileName: () => "pi-bash-roll" + ++spills + ".log").ExecuteAsync("x", directory, null, CancellationToken.None);
            Check(rolled.Truncated && rolled.ExitCode == 0 && spills == 1, "rolling result");
            var spilled = File.ReadAllText(rolled.FullOutputPath!);
            // When raw output first exceeded 50KB (13th chunk), the buffer still held all 12 earlier chunks.
            Equal(string.Concat(big), spilled);
            // The byte limit keeps whole lines only: twelve 4097-byte lines fit in 50KB.
            Equal(string.Concat(big[^12..])[..^1], rolled.Output);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task NativeProcess()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var shell = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(shell)) throw new InvalidOperationException("Windows PowerShell is required for the native raw stream case.");
        var directory = Path.Combine(Path.GetTempPath(), "PiSharp-wire-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var environment = ImmutableDictionary<string, string>.Empty.Add("SystemRoot", windows).Add("TEMP", directory).Add("TMP", directory);
            var operations = new NativeShellOperations(shell, environment, directory);
            var chunks = new List<string>();
            var result = await new ShellCommandExecutor(operations, directory).ExecuteAsync(
                "[Console]::Out.Write([char]27 + '[32mgreen' + [char]27 + '[0m' + [char]13 + [char]10 + 'done'); exit 4", directory,
                chunk => { chunks.Add(chunk); return ValueTask.CompletedTask; }, CancellationToken.None);
            Equal(new ShellCommandResult("green\ndone", 4, false, false, null), result);
            Equal("green\ndone", string.Concat(chunks));
            // The runner's own spill copy is discarded; nothing but the executor's files are written to the directory.
            Equal(0, Directory.GetFiles(directory).Length);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
