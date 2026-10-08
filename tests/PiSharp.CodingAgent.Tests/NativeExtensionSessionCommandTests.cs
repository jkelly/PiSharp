using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

internal static partial class NativeExtensionSessionCommandTests
{
    private static string _host = "", _cli = "", _published = "";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string host, string cli)
    {
        if (!Path.IsPathFullyQualified(host) || !File.Exists(host) || !Path.IsPathFullyQualified(cli) || !File.Exists(cli))
            throw new ArgumentException("Published CLI tests require explicit compiled host and CLI paths.");
        _host = host; _cli = cli;
        var directory = new DirectoryInfo(Path.GetDirectoryName(cli)!);
        while (!File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            directory = directory.Parent ?? throw new InvalidOperationException("Cannot locate owned published CLI fixture.");
        _published = Path.Combine(directory.FullName, "artifacts", "extensions", "published-fixtures", "cli");
        return
        [
            ("native published CLI tools use one final policy and survive durable independent resume/branch", DurableTools),
            ("native published print and JSON retain real hook results, opaque/null fields and exact final bytes", PrintAndJson),
            ("native published CLI approval/hash/scope/collision admission executes no module or constructor", ApprovalAdmission),
            ("native published call hooks cannot bypass final schema or native file policy", HookAuthority),
            ("native published RPC input transforms/handled/queues use the actual durable coordinator", RpcInput),
            ("native published callback fault, held abort/EOF and broken stdout join package/durable cleanup", Cleanup)
        ];
    }

    private static async Task DurableTools()
    {
        foreach (var api in new[] { "openai-responses", "anthropic-messages", "openai-completions" })
        {
            using var files = await Files.CreateAsync(); await Create(files, api);
            var initial = await File.ReadAllBytesAsync(files.Session);
            await Script(files, Tool(api, "fixture.cli.echo", "echo", new { text = "replace" }, "begin|cli"),
                Text(api, "tool final", ["plugin:transformed"]));
            var first = Success(await Command(files, "prompt", api, "transform:begin"));
            Equal("Started", first.GetProperty("inputDisposition").GetString());
            Equal(2, first.GetProperty("usedScriptTurns").GetInt32());
            var actions = first.GetProperty("actions"); Equal(1, actions.GetArrayLength());
            Equal("fixture.cli.echo", actions[0].GetProperty("ToolName").GetString());
            Equal("fixture.cli/1/echo", actions[0].GetProperty("Target").GetString());
            Check(actions[0].GetProperty("allowed").GetBoolean(), "Final extension action was not authorized.");
            Check(first.GetProperty("requests").EnumerateArray().All(request =>
                request.GetProperty("toolNames").EnumerateArray().Any(name => name.GetString() == "fixture.cli.echo")),
                "Actual provider payload omitted the active published declaration.");
            var log = await Complete(files); Check(log.OriginalBytes.AsSpan().StartsWith(initial), "Command rewrote prior session bytes.");
            var context = Context(log);
            var admittedUser = context.LlmMessages.Single(message => message.Role == "user").WireBody.Value.GetProperty("content");
            Check(admittedUser.ValueKind == JsonValueKind.Array && admittedUser[0].GetProperty("text").GetString() == "begin|cli",
                "Ordinary one-shot text bypassed the loaded native input reducer.");
            var call = context.LlmMessages.Single(message => message.Role == "assistant" &&
                message.WireBody.Value.GetProperty("stopReason").GetString() == "toolUse");
            Equal("replace", call.WireBody.Value.GetProperty("content")[0].GetProperty("arguments").GetProperty("text").GetString());
            var result = context.LlmMessages.Single(message => message.Role == "toolResult").WireBody.Value;
            Equal("plugin:transformed", result.GetProperty("content")[0].GetProperty("text").GetString());
            Equal("transformed", result.GetProperty("details").GetProperty("argument").GetString());
            Check(!result.GetProperty("isError").GetBoolean(), "Successful plugin output gained error disposition.");
            var leaf = context.LeafId!; var beforeResume = log.OriginalBytes.ToArray();
            await Script(files, Text(api, "resumed", ["plugin:transformed", "resume"]));
            Success(await Command(files, "resume", api, "resume"));
            await Script(files, Text(api, "sibling", ["plugin:transformed", "branch"]));
            var branched = Success(await Command(files, "resume", api, "branch", "--leaf", leaf));
            Equal(leaf, branched.GetProperty("previousSelectedLeafId").GetString());
            var after = await Complete(files);
            Check(after.OriginalBytes.AsSpan().StartsWith(beforeResume), "Independent branch rewrote physical history.");
            var selected = new SessionContextProjector().Project(after.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray(),
                branched.GetProperty("selectedLeafId").GetString());
            Check(!selected.LlmMessages.Any(message => message.Role == "user" && message.WireBody.ToString().Contains("resume", StringComparison.Ordinal)),
                "Selected branch incorporated a sibling user message.");
            Equal(call.WireBody.ToString(), selected.LlmMessages.Single(message => message.Role == "assistant" &&
                message.WireBody.Value.GetProperty("stopReason").GetString() == "toolUse").WireBody.ToString());
            CheckMarkers(files, expectedRuns: 4);
            Equal(3, files.MarkerLines.Count(line => line == "input"));
            Equal(3, files.MarkerLines.Count(line => line == "input-closed"));
        }
    }

    private static async Task PrintAndJson()
    {
        foreach (var mode in new[] { "print", "json" })
        {
            using var files = await Files.CreateAsync(); await Create(files, "openai-responses");
            const string final = "final \u6587\0\U0001f642\r\n";
            await Script(files, Tool("openai-responses", "fixture.cli.echo", "redact", new { text = "redact" }, "text"),
                Text("openai-responses", final, ["redacted"]));
            var run = await Command(files, "prompt", "openai-responses", "text", "--output", mode);
            Equal(0, run.ExitCode); Equal("", run.Error);
            if (mode == "print") Check(run.Output.SequenceEqual(Utf8.GetBytes(final + "\n")), "Published print changed UTF8/NUL/newline bytes.");
            else
            {
                var records = JsonLines(run.Output); Equal("session", records[0].Value.GetProperty("type").GetString());
                Check(records.Any(record => Type(record) == "tool_execution_end" &&
                    record.Value.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() == "redacted"),
                    "Public JSON did not carry the actual after-hook result.");
                Equal(1, records.Count(record => Type(record) == "agent_end"));
            }
            var log = await Complete(files); var messages = Context(log).LlmMessages;
            var result = messages.Single(message => message.Role == "toolResult").WireBody.Value;
            Equal("{\"argument\":\"redact\"}", result.GetProperty("details").GetRawText());
            Equal("redacted", result.GetProperty("content")[0].GetProperty("text").GetString());
            Equal(final, messages[^1].WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString());
            // Agent's canonical model result deliberately excludes structured/opaque result fields.
            // The exact complete result is asserted on the public tool event, not invented in durable messages.
            if (mode == "json")
            {
                var complete = JsonLines(run.Output).Single(record => Type(record) == "tool_execution_end").Value.GetProperty("result");
                Equal("{\"argument\":\"redact\"}", complete.GetProperty("details").GetRawText());
                Equal(JsonValueKind.Null, complete.GetProperty("future").ValueKind);
                Check(!complete.TryGetProperty("structuredContent", out var structured) || structured.ValueKind == JsonValueKind.Null,
                    "Content redaction retained stale structured output.");
                Equal(2, complete.GetProperty("opaque").GetProperty("ordered")[0].GetInt32());
            }
            CheckMarkers(files, 2);
        }
    }

    private static async Task ApprovalAdmission()
    {
        using var files = await Files.CreateAsync();
        var args = Base("create", files, "openai-responses").Concat(files.ExtensionArgs).ToArray();
        var without = args.Where((_, index) => index == 0 || args[index - 1] != "--extension-approval")
            .Where(value => value != "--extension-approval").ToArray();
        Refused(await OneShot(files, without), "ExecutionApprovalRequired"); NoActivation(files);
        var validApproval = await File.ReadAllTextAsync(files.Approval);
        foreach (var field in new[] { "manifestValueSha256", "effectiveScopeId", "sessionPath", "workspace" })
        {
            var wrong = JsonNode.Parse(validApproval)!.AsObject();
            wrong[field] = field == "manifestValueSha256" ? new string('0', 64) : "unapproved";
            await File.WriteAllTextAsync(files.Approval, wrong.ToJsonString(), Utf8);
            Refused(await OneShot(files, args), "ApprovalMismatch"); NoActivation(files);
        }
        await File.WriteAllTextAsync(files.Approval, validApproval, Utf8);
        Refused(await OneShot(files, args.Concat(["--enable-extension-tool", "fixture.cli.echo"]).ToArray()), "InvalidConfiguration"); NoActivation(files);
        var nativeName = args.ToArray(); nativeName[^1] = "write";
        Refused(await OneShot(files, nativeName), "InvalidConfiguration"); NoActivation(files);
        await File.AppendAllTextAsync(Path.Combine(files.Package, "PublishedFixture.Cli.dll"), "authored tamper", Utf8);
        Refused(await OneShot(files, args), "InvalidConfiguration"); NoActivation(files);
        Check(!File.Exists(files.Session), "Failed package admission created a durable session.");
    }

    private static async Task HookAuthority()
    {
        foreach (var text in new[] { "invalid-replacement", "fault-call", "fault" })
        {
            using var files = await Files.CreateAsync(); await Create(files, "openai-responses");
            await Script(files, Tool("openai-responses", "fixture.cli.echo", text, new { text }), Text("openai-responses", "after failure"));
            var result = await Command(files, "prompt", "openai-responses", "call"); Equal(1, result.ExitCode);
            var report = JsonData.Parse(Utf8.GetString(result.Output)).Value;
            Check(report.GetProperty("toolErrors").GetBoolean(), "Failed hook/schema/callback became a successful tool.");
            Check(!Utf8.GetString(result.Output).Contains("private published", StringComparison.Ordinal) &&
                !result.Error.Contains("private published", StringComparison.Ordinal), "Callback exception leaked.");
            var markers = files.MarkerLines;
            Equal(text == "fault" ? 1 : 0, markers.Count(line => line.StartsWith("tool:", StringComparison.Ordinal)));
            Equal(text == "fault" ? 1 : 0, report.GetProperty("actions").GetArrayLength());
            await Complete(files); CheckMarkers(files, 2);
        }
        foreach (var content in new[] { "extension-block", "not-authorized" })
        {
            using var files = await Files.CreateAsync(); await Create(files, "anthropic-messages");
            var target = files.In("effect.txt");
            await Script(files, Tool("anthropic-messages", "write", "write", new { path = target, content }), Text("anthropic-messages", "denied"));
            var extra = content == "extension-block" ? new[] { "--allow-write", target } : Array.Empty<string>();
            var result = await Command(files, "prompt", "anthropic-messages", "write", extra); Equal(1, result.ExitCode);
            Check(!File.Exists(target), "Registered hook or mandatory native policy fell through into file effects.");
            var report = JsonData.Parse(Utf8.GetString(result.Output)).Value;
            Equal(content == "extension-block" ? 0 : 1, report.GetProperty("actions").GetArrayLength());
            if (content != "extension-block") Check(!report.GetProperty("actions")[0].GetProperty("allowed").GetBoolean(), "Native authority was widened.");
            await Complete(files); CheckMarkers(files, 2);
        }
    }

    private static async Task RpcInput()
    {
        using var files = await Files.CreateAsync(); await Create(files, "openai-completions");
        var initial = await File.ReadAllBytesAsync(files.Session);
        var initialEntries = (await Complete(files)).ValidatedPrefix.Length - 1;
        await Script(files, Text("openai-completions", "done", ["begin|cli"], gate: true));
        await using (var child = new RpcChild(files, "openai-completions"))
        {
            await child.Send(new { id = "handled", type = "prompt", message = "handled" });
            Equal("handled", Response(await child.Response("handled")).GetProperty("data").GetProperty("disposition").GetString());
            await child.Send(new { id = "entries", type = "get_entries" });
            var entries = Response(await child.Response("entries")).GetProperty("data").GetProperty("entries");
            Equal(initialEntries, entries.GetArrayLength()); // acknowledged model/thinking/declaration only
            Check(!child.Records.Any(record => Type(record) == "agent_start"), "Handled input started actual Agent work.");
            await child.Send(new { id = "p", type = "prompt", message = "transform:begin" });
            Equal("started", Response(await child.Response("p")).GetProperty("data").GetProperty("disposition").GetString());
            await child.Wait(record => Type(record) == "message_start" && record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant");
            await child.Send(new { id = "steer", type = "steer", message = "transform:steering" });
            Response(await child.Response("steer"));
            await child.Send(new { id = "handled-queue", type = "follow_up", message = "handled" });
            Equal("handled", Response(await child.Response("handled-queue")).GetProperty("data").GetProperty("disposition").GetString());
            await child.Send(new { id = "clear", type = "clear_queue" });
            var queues = Response(await child.Response("clear")).GetProperty("data");
            Equal("steering|cli", queues.GetProperty("steering")[0].GetString()); Equal(0, queues.GetProperty("followUp").GetArrayLength());
            await child.Send(new { id = "release", type = "get_state" }); Response(await child.Response("release"));
            await child.Wait(record => Type(record) == "agent_settled"); Clean(await child.Finish());
        }
        var log = await Complete(files); Check(log.OriginalBytes.AsSpan().StartsWith(initial), "RPC rewrote prior bytes.");
        var users = Context(log).LlmMessages.Where(message => message.Role == "user").ToArray(); Equal(1, users.Length);
        Equal("begin|cli", users[0].WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString());
        CheckMarkers(files, 2);
        // One-shot handled print must not reprint the previous generation's final message.
        await Script(files, Text("openai-completions", "unused"));
        var handled = await Command(files, "resume", "openai-completions", "handled", "--output", "print");
        Equal(0, handled.ExitCode); Equal(0, handled.Output.Length); Equal("", handled.Error);
        var unchanged = await File.ReadAllBytesAsync(files.Session); Check(unchanged.SequenceEqual(log.OriginalBytes), "Handled input appended durable messages.");
    }

    private static async Task Cleanup()
    {
        foreach (var shutdown in new[] { "abort", "eof", "broken-output" })
        {
            using var files = await Files.CreateAsync(); await Create(files, "openai-responses");
            await Script(files, Tool("openai-responses", "fixture.cli.echo", "held", new { text = "hold" }), Text("openai-responses", "must not execute"));
            await using (var child = new RpcChild(files, "openai-responses"))
            {
                await child.Send(new { id = "p", type = "prompt", message = "hold callback" }); Response(await child.Response("p"));
                await WaitFile(files.InGate("tool.entered"), child.Token);
                Check(!File.Exists(files.InGate("tool.closed")), "Held callback was not actually active.");
                if (shutdown == "abort")
                {
                    await child.Send(new { id = "abort", type = "abort" }); Response(await child.Response("abort"));
                    await child.Wait(record => Type(record) == "agent_settled"); Clean(await child.Finish());
                }
                else if (shutdown == "eof") Clean(await child.Finish());
                else
                {
                    child.BreakOutput(); await child.Send(new { id = "state", type = "get_state" });
                    var failed = await child.Finish(allowOutputFailure: true); Equal(1, failed.ExitCode);
                    Equal("RpcHostFailed", JsonData.Parse(failed.Error).Value.GetProperty("code").GetString());
                }
            }
            Check(File.Exists(files.InGate("tool.closed")), "Host returned before the actual callback finally closed.");
            CheckMarkers(files, 2);
            var log = await Complete(files);
            Equal(1, Context(log).LlmMessages.Count(message => message.Role == "assistant"));
            Check(!Context(log).LlmMessages.Any(message => message.WireBody.ToString().Contains("must not execute", StringComparison.Ordinal)),
                "Shutdown acquired a subsequent scripted provider response.");
        }
    }

    private static async Task Create(Files files, string api)
    { _ = Success(await OneShot(files, Base("create", files, api).Concat(files.ExtensionArgs).ToArray())); }
    private static async Task<Result> Command(Files files, string command, string api, string message, params string[] extra) =>
        await OneShot(files, Base(command, files, api).Concat(["--offline-script", files.Script, "--message", message])
            .Concat(files.ExtensionArgs).Concat(extra).ToArray());
    private static string[] Base(string command, Files files, string api) =>
        ["session", command, "--session", files.Session, "--workspace", files.Root, "--offline-api", api];
    private static async Task Script(Files files, params object[] turns) =>
        await File.WriteAllTextAsync(files.Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns }), Utf8);
    private static object Tool(string api, string name, string id, object arguments, params string[] required) => api switch
    {
        "anthropic-messages" => SessionCommandTests.AnthropicTool(name, id, arguments, required),
        "openai-completions" => SessionCommandTests.CompletionsTool(name, id, arguments, required),
        _ => ResponsesTool(name, id, arguments, required)
    };
    private static object Text(string api, string text, string[]? required = null, bool gate = false) => api switch
    {
        "anthropic-messages" => SessionCommandTests.AnthropicText(text, gate, required),
        "openai-completions" => SessionCommandTests.CompletionsText(text, gate, required),
        _ => ResponsesText(text, required, gate)
    };
    private static object ResponsesTool(string name, string call, object args, string[] required)
    {
        var arguments = JsonSerializer.Serialize(args);
        return new { requiredInputTexts = required, events = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name, arguments = "" } },
            new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name, arguments } }, Completed()
        } };
    }
    private static object ResponsesText(string text, string[]? required, bool gate)
    {
        var result = new Dictionary<string, object?> { ["requiredInputTexts"] = required ?? [], ["events"] = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "msg-final", content = Array.Empty<object>() } },
            new { type = "response.output_text.delta", output_index = 0, item_id = "msg-final", delta = text },
            new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "msg-final", content = new[] { new { type = "output_text", text } } } }, Completed()
        } };
        if (gate) result["rpcGate"] = new { releaseOnGetStateId = "release" }; return result;
    }
    private static object Completed() => new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(),
        usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } };
    private static async Task<SessionLogReadResult> Complete(Files files)
    {
        var read = await new SessionLogReader().ReadFileAsync(files.Session);
        Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete && read.ValidatedPrefixByteLength == read.OriginalBytes.Length,
            "CLI did not close a complete acknowledged session.");
        await using var lease = await SessionLogStore.OpenAsync(files.Session);
        Equal((long)read.OriginalBytes.Length, lease.Snapshot.CommittedByteLength);
        return read;
    }
    private static SessionContextProjection Context(SessionLogReadResult log) =>
        new SessionContextProjector().ProjectLatest(log.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray());
    private static void NoActivation(Files files)
    {
        Equal(0, files.MarkerLines.Length); Equal(0, Directory.GetDirectories(files.Snapshots).Length);
    }
    private static void CheckMarkers(Files files, int expectedRuns)
    {
        foreach (var stage in new[] { "module", "constructor", "initialize", "dispose" }) Equal(expectedRuns, files.MarkerLines.Count(line => line == stage));
        Equal("dispose", files.MarkerLines[^1]); Equal(0, Directory.GetDirectories(files.Snapshots).Length);
    }
    private static JsonElement Success(Result result)
    {
        Check(result.ExitCode == 0 && result.Error.Length == 0, "Expected successful CLI receipt; " + Describe(result));
        JsonElement value;
        try { value = JsonData.Parse(Utf8.GetString(result.Output)).Value; }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw new InvalidOperationException("Expected strict JSON CLI success receipt; " + Describe(result), error); }
        Check(value.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "Success preceded durable acknowledgement.");
        Equal("completed", value.GetProperty("status").GetString()); return value;
    }
    private static void Refused(Result result, string code)
    {
        Check(result.ExitCode == 2 && result.Output.Length == 0, "Expected rejected CLI receipt (" + code + "); " + Describe(result));
        var actual = JsonData.Parse(result.Error).Value.GetProperty("code").GetString();
        Check(actual == code, "Expected rejection code " + Bounded(code) + ", actual " + Bounded(actual) + "; " + Describe(result));
    }
    private static JsonData[] JsonLines(byte[] bytes)
    {
        var text = Utf8.GetString(bytes); Check(text.EndsWith('\n'), "Public JSONL was not closed/flushed.");
        return text[..^1].Split('\n').Select(JsonData.Parse).ToArray();
    }
    private static string Type(JsonData value) => value.Value.GetProperty("type").GetString()!;
    private static JsonElement Response(JsonData value)
    { Check(value.Value.GetProperty("success").GetBoolean(), "Actual RPC response failed."); return value.Value; }
    private static void Clean(RpcResult result) => Check(result.ExitCode == 0 && result.Error.Length == 0,
        "Expected successful RPC receipt; exitCode=" + result.ExitCode + ", stderr=" + Bounded(result.Error));
    private static string Describe(Result result) => "exitCode=" + result.ExitCode + ", stdoutBytes=" + result.Output.Length +
        ", stderr=" + Bounded(result.Error);
    // Test-only authored receipts: bound diagnostics independently of process-output admission.
    private static string Bounded(string? text)
    {
        if (text is null) return "<null>";
        const int maximum = 1536;
        return JsonSerializer.Serialize(text.Length <= maximum ? text : text[..maximum] + "[truncated]");
    }
    private static async Task WaitFile(string path, CancellationToken token)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!) { Filter = Path.GetFileName(path), NotifyFilter = NotifyFilters.FileName };
        watcher.Created += (_, _) => ready.TrySetResult(); watcher.Renamed += (_, _) => ready.TrySetResult();
        watcher.Error += (_, _) => ready.TrySetException(new IOException("Authored test watcher failed."));
        watcher.EnableRaisingEvents = true;
        if (File.Exists(path)) ready.TrySetResult(); await ready.Task.WaitAsync(token);
    }
    private static ProcessStartInfo Start(Files files, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(_host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = Utf8, StandardErrorEncoding = Utf8 };
        start.Environment["PISHARP_NATIVE_FIXTURE_MARKERS"] = files.Markers;
        start.Environment["PISHARP_NATIVE_CLI_FIXTURE_GATE_ROOT"] = files.Gates;
        start.ArgumentList.Add(_cli); foreach (var argument in arguments) start.ArgumentList.Add(argument); return start;
    }
    private sealed record Result(int ExitCode, byte[] Output, string Error);
    private sealed record RpcResult(int ExitCode, string Error);
    private static async Task<Result> OneShot(Files files, string[] arguments)
    {
        using var process = Process.Start(Start(files, arguments)) ?? throw new InvalidOperationException("Compiled CLI failed to start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); process.StandardInput.Close();
        var output = ReadBytes(process.StandardOutput.BaseStream, deadline.Token); var error = ReadBytes(process.StandardError.BaseStream, deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); return new(process.ExitCode, await output, Utf8.GetString(await error)); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            foreach (var pending in new[] { output, error }) try { await pending; } catch (Exception) { }
        }
    }
    private static async Task<byte[]> ReadBytes(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var count = await stream.ReadAsync(buffer, token); if (count == 0) return output.ToArray();
            Check(count <= 2_097_152 - output.Length, "Child output exceeded bounded test receipt."); output.Write(buffer, 0, count); }
    }
    private sealed class RpcChild : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(15));
        private readonly CancellationTokenSource _outputCancellation = new();
        private readonly object _gate = new();
        private readonly List<JsonData> _records = [];
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _read; private readonly Task<byte[]> _error; private bool _closed;
        public CancellationToken Token => _deadline.Token;
        public JsonData[] Records { get { lock (_gate) return _records.ToArray(); } }
        public RpcChild(Files files, string api)
        {
            _process = Process.Start(Start(files, Base("rpc", files, api).Concat(["--offline-script", files.Script]).Concat(files.ExtensionArgs)))
                ?? throw new InvalidOperationException("Compiled RPC host failed to start.");
            _error = ReadBytes(_process.StandardError.BaseStream, _deadline.Token); _read = ReadAsync();
        }
        public async Task Send(object command)
        { await _process.StandardInput.WriteAsync((JsonSerializer.Serialize(command) + "\n").AsMemory(), Token); await _process.StandardInput.FlushAsync(Token); }
        public Task<JsonData> Response(string id) => Wait(record => Type(record) == "response" && record.Value.TryGetProperty("id", out var value) && value.GetString() == id);
        public async Task<JsonData> Wait(Func<JsonData, bool> predicate)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                { var record = _records.FirstOrDefault(predicate); if (record is not null) return record;
                    changed = _changed.Task; }
                if (_read.IsCompleted)
                {
                    await _read;
                    await _process.WaitForExitAsync(Token);
                    throw new InvalidOperationException("Compiled RPC ended before the expected actual record; exit=" + _process.ExitCode +
                        "; stderr=" + Utf8.GetString(await _error) + "; records=" + string.Join(" | ", Records.TakeLast(5).Select(row => row.ToString())));
                }
                await changed.WaitAsync(Token);
            }
        }
        private async Task ReadAsync()
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token, _outputCancellation.Token);
            try
            {
                await using var reader = new JsonlReader(_process.StandardOutput.BaseStream);
                long bytes = 0;
                await foreach (var admission in reader.ReadAdmissionsAsync(cancellation.Token))
                {
                    Check(admission.IsAccepted && !admission.IsFinalFrame, "Public RPC emitted malformed/truncated JSONL.");
                    var record = admission.Record!; bytes += Utf8.GetByteCount(record.ToString()) + 1;
                    Check(bytes <= 2_097_152, "Public RPC exceeded test receipt bounds.");
                    TaskCompletionSource changed;
                    lock (_gate) { Check(_records.Count < 2048, "Public RPC record cap exceeded."); _records.Add(record);
                        changed = _changed; _changed = new(TaskCreationOptions.RunContinuationsAsynchronously); }
                    changed.TrySetResult();
                }
            }
            finally { lock (_gate) _changed.TrySetResult(); }
        }
        public void BreakOutput() { _outputCancellation.Cancel(); _process.StandardOutput.BaseStream.Dispose(); }
        public async Task<RpcResult> Finish(bool allowOutputFailure = false)
        {
            if (!_closed) { _closed = true; _process.StandardInput.Close(); }
            await _process.WaitForExitAsync(Token);
            try { await _read; } catch (Exception) when (allowOutputFailure) { }
            return new(_process.ExitCode, Utf8.GetString(await _error));
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
                _outputCancellation.Cancel();
                try { await _read; } catch (Exception) { } try { await _error; } catch (Exception) { }
            }
            finally { _process.Dispose(); _deadline.Dispose(); _outputCancellation.Dispose(); }
        }
    }
    private sealed class Files : IDisposable
    {
        private string _packageKind = "cli";
        private bool _creationCommands;
        private string ToolName => _packageKind == "todo" ? "todo" : "fixture.cli.echo";
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-native-cli-extension-" + Guid.NewGuid().ToString("N"));
        internal string Session => In("session.jsonl"); internal string Script => In("script.json");
        internal string Package => In("published"); internal string Manifest => In("manifest.json"); internal string Approval => In("approval.json");
        internal string Markers => In("markers"); internal string Gates => In("gates"); internal string Snapshots => In("snapshots");
        internal string In(string name) => Path.Combine(Root, name); internal string InGate(string name) => Path.Combine(Gates, name);
        internal string[] MarkerLines => File.Exists(Path.Combine(Markers, "PublishedFixture.Cli.markers"))
            ? File.ReadAllLines(Path.Combine(Markers, "PublishedFixture.Cli.markers")) : [];
        internal string[] ExtensionArgs => ["--extension-package", Package, "--extension-manifest", Manifest,
            "--extension-approval", Approval, "--extension-snapshot-root", Snapshots,
            .. (_packageKind == "checkpoint" ? _creationCommands ? new[] { "--enable-extension-command", "checkpoint", "--enable-extension-command", "checkpoint-create" } :
                new[] { "--enable-extension-command", "checkpoint" } : new[] { "--enable-extension-tool", ToolName })];
        internal static async Task<Files> CreateAsync(string packageKind = "cli", bool creationCommands = false)
        {
            if (packageKind is not ("cli" or "todo" or "checkpoint")) throw new ArgumentException("Unknown owned published sample.");
            var files = new Files { _packageKind = packageKind, _creationCommands = creationCommands };
            try
            {
                var published = Path.Combine(Path.GetDirectoryName(_published)!, packageKind);
                var assembly = packageKind == "checkpoint" ? "SessionCheckpoint.dll" : packageKind == "todo" ? "StatefulTodo.dll" : "PublishedFixture.Cli.dll";
                Check(File.Exists(Path.Combine(published, assembly)), "Root must publish the owned sample before running cases.");
                foreach (var path in new[] { files.Root, files.Package, files.Markers, files.Gates, files.Snapshots }) Directory.CreateDirectory(path);
                foreach (var source in Directory.GetFiles(published, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(files.Package, Path.GetRelativePath(published, source));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
                }
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var file in Directory.GetFiles(files.Package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    hashes.Add(Path.GetRelativePath(files.Package, file).Replace('\\', '/'), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file))));
                var metadata = JsonSerializer.Serialize(new { schemaVersion = 0, id = packageKind == "checkpoint" ? "sample.checkpoint" : packageKind == "todo" ? "sample.todo" : "fixture.cli", packageVersion = "0.0.1",
                    hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native", assembly,
                    entryType = packageKind == "checkpoint" ? "SessionCheckpoint.SessionCheckpointExtension" : packageKind == "todo" ? "StatefulTodo.StatefulTodoExtension" : "PublishedCliFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
                    requiredFeatures = packageKind == "checkpoint" ? new[] { "owned-descriptor-callbacks", "session-branch-snapshot", "session-durable-entries", "session-replacement" } : packageKind == "todo" ? new[] { "owned-descriptor-callbacks", "session-branch-snapshot" } : new[] { "owned-descriptor-callbacks", "registered-input-tool-reducers" },
                    declaredCapabilities = packageKind == "checkpoint" ? new[] { "commands", "observations" } : new[] { "tools", "observations" }, resourcePaths = hashes.Keys.Where(path => path != assembly).ToArray(),
                    explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
                await File.WriteAllTextAsync(files.Manifest, metadata, Utf8);
                var approval = JsonSerializer.SerializeToNode(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution",
                    packageRoot = files.Package, manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(metadata))), artifactHashes = hashes,
                    sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit", policyRevision = "experimental-policy-0", hostGeneration = 1,
                    sessionPath = files.Session, workspace = files.Root, snapshotRoot = files.Snapshots,
                    enabledTools = packageKind == "checkpoint" ? Array.Empty<string>() : new[] { files.ToolName } })!.AsObject();
                if (packageKind == "checkpoint") approval["enabledCommands"] = creationCommands ? new JsonArray("checkpoint", "checkpoint-create") : new JsonArray("checkpoint");
                await File.WriteAllTextAsync(files.Approval, approval.ToJsonString(), Utf8);
                return files;
            }
            catch { files.Dispose(); throw; }
        }
        public void Dispose()
        {
            var target = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(target) != _parent || !Path.GetFileName(target).StartsWith("pisharp-native-cli-extension-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unowned recursive test cleanup.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual),
        "Published CLI values differ; expected=" + Bounded(expected is null ? null : expected.ToString()) +
        ", actual=" + Bounded(actual is null ? null : actual.ToString()));
}
