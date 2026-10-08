using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

/// <summary>Explicit opt-in actual-process cases. Ordinary native test registration must never call Cases.</summary>
internal static class NodePackageActivationTests
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record Context(string Host, string Cli, string Published, string Node, string Repository,
        string Oracle, string Jiti, string Reference, string Runs);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string dotnetHost, string cliDll,
        string publishedPackage, string node, string repository, string oracle, string jiti, string reference, string runParent)
    {
        var context = new Context(Absolute(dotnetHost), Absolute(cliDll), Absolute(publishedPackage), Absolute(node),
            Absolute(repository), Absolute(oracle), Absolute(jiti), Absolute(reference), Absolute(runParent));
        foreach (var path in new[] { context.Host, context.Cli, context.Node }) Check(File.Exists(path), "Explicit executable/CLI missing.");
        foreach (var path in new[] { context.Published, context.Repository, context.Oracle, context.Jiti, context.Reference, context.Runs })
            Check(Directory.Exists(path), "Explicit input/run parent missing.");
        foreach (var api in new[] { "openai-responses", "anthropic-messages", "openai-completions" })
            yield return ("node-package." + api + ".actual-cli-rpc-hooks-notifications-durable-resume-branch", () => Durable(context, api));
        yield return ("node-package.actual-native-final-schema-and-file-policy", () => Authority(context));
    }

    private static async Task Durable(Context context, string api)
    {
        var files = await Files.Create(context, api);
        await Create(files);
        var initial = await Complete(files); NativeLoadout(initial);
        await Script(files, Tool(api, "write", "cli-block", new { path = ".env", content = "never" }, "cli-first"),
            Tool(api, "write", "cli-write", new { path = "ordinary.txt", content = "CLI saved \u6587\U0001f642\n" }, "Tool execution was blocked by a prepared hook."),
            Text(api, "cli-final", "Successfully wrote"));
        var cli = await OneShot(files, "prompt", ["--message", "cli-first", "--allow-write", files.In(".env"), "--allow-write", files.In("ordinary.txt")]);
        Equal(1, cli.ExitCode); Equal("", cli.Error);
        var report = JsonData.Parse(cli.Output).Value;
        Equal("completed_with_errors", report.GetProperty("status").GetString());
        Check(report.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "CLI reported before durable checkpoint.");
        Equal(3, report.GetProperty("usedScriptTurns").GetInt32()); Equal(3, report.GetProperty("requests").GetArrayLength());
        foreach (var request in report.GetProperty("requests").EnumerateArray())
            Check(request.GetProperty("toolNames").EnumerateArray().Select(item => item.GetString()).SequenceEqual(["read", "write"]), "Hook-only package added an LLM tool.");
        Equal(1, report.GetProperty("actions").GetArrayLength());
        Check(report.GetProperty("actions")[0].GetProperty("allowed").GetBoolean(), "Real final file policy did not approve the ordinary write.");
        Check(!File.Exists(files.In(".env")), "Source hook allowed a protected native effect.");
        Equal("CLI saved \u6587\U0001f642\n", await File.ReadAllTextAsync(files.In("ordinary.txt")));
        var cliLog = await Complete(files); var ancestor = Project(cliLog).LeafId!;
        Check(cliLog.OriginalBytes.AsSpan().StartsWith(initial.OriginalBytes.AsSpan()), "CLI rewrote initial session bytes.");
        NativeLoadout(cliLog);

        await Script(files, Tool(api, "write", "rpc-block", new { path = ".env", content = "never" }, "cli-final", "rpc-resume"),
            Tool(api, "write", "rpc-write", new { path = "ordinary.txt", content = "RPC saved \u6587\U0001f642\n" }, "Tool execution was blocked by a prepared hook."),
            Text(api, "rpc-final", "Successfully wrote"));
        JsonElement acknowledged;
        await using (var rpc = await RpcChild.Start(files, ["--allow-write", files.In(".env"), "--allow-write", files.In("ordinary.txt")]))
        {
            await rpc.Send(new { id = "prompt", type = "prompt", message = "rpc-resume" }); Good(await rpc.Response("prompt"));
            await rpc.Wait(record => Type(record) == "agent_settled"); AssertRpcTurn(files, rpc.Records, "rpc-block", "rpc-write");
            await rpc.Send(new { id = "state", type = "get_state" });
            var state = Good(await rpc.Response("state")).GetProperty("data");
            Equal(api, state.GetProperty("model").GetProperty("api").GetString());
            Check(!state.GetProperty("isStreaming").GetBoolean(), "Settled observer retained active work.");
            await rpc.Send(new { id = "entries", type = "get_entries" });
            acknowledged = Good(await rpc.Response("entries")).GetProperty("data").GetProperty("entries");
            await Acknowledged(files, acknowledged); Clean(await rpc.Finish());
        }
        Equal("RPC saved \u6587\U0001f642\n", await File.ReadAllTextAsync(files.In("ordinary.txt")));
        Check(!File.Exists(files.In(".env")), "RPC resumed source hook allowed a protected native effect.");
        var resumed = await Complete(files); Check(resumed.OriginalBytes.AsSpan().StartsWith(cliLog.OriginalBytes.AsSpan()), "Reopen rewrote durable bytes.");
        Equal(acknowledged.GetArrayLength(), resumed.ValidatedPrefix.Length - 1);

        await Script(files, Tool(api, "write", "branch-block", new { path = ".env", content = "never" }, "cli-final", "rpc-branch"),
            Tool(api, "write", "branch-write", new { path = "branch.txt", content = "branch saved\n" }, "Tool execution was blocked by a prepared hook."),
            Text(api, "branch-final", "Successfully wrote"));
        string branchLeaf;
        await using (var branch = await RpcChild.Start(files, ["--leaf", ancestor, "--allow-write", files.In(".env"), "--allow-write", files.In("branch.txt")]))
        {
            await branch.Send(new { id = "prompt", type = "prompt", message = "rpc-branch" }); Good(await branch.Response("prompt"));
            await branch.Wait(record => Type(record) == "agent_settled"); AssertRpcTurn(files, branch.Records, "branch-block", "branch-write");
            await branch.Send(new { id = "messages", type = "get_messages" });
            var messages = Good(await branch.Response("messages")).GetProperty("data").GetProperty("messages");
            Check(!messages.GetRawText().Contains("rpc-resume", StringComparison.Ordinal), "Selected branch included sibling context.");
            await branch.Send(new { id = "entries", type = "get_entries" });
            var entries = Good(await branch.Response("entries")).GetProperty("data");
            branchLeaf = entries.GetProperty("leafId").GetString()!; await Acknowledged(files, entries.GetProperty("entries"));
            Clean(await branch.Finish());
        }
        var final = await Complete(files); Check(final.OriginalBytes.AsSpan().StartsWith(resumed.OriginalBytes.AsSpan()), "Branch rewrote physical history.");
        Equal(ancestor, final.ValidatedPrefix[resumed.ValidatedPrefix.Length].Entry.ParentId);
        var selected = Project(final, branchLeaf);
        Check(!selected.LlmMessages.Any(message => message.WireBody.ToString().Contains("rpc-resume", StringComparison.Ordinal)), "Durable branch selected sibling messages.");
        Equal("branch saved\n", await File.ReadAllTextAsync(files.In("branch.txt"))); Check(!File.Exists(files.In(".env")), "Branch hook bypassed protection.");
        NativeLoadout(final); await files.VerifyReceipts(4);
    }

    private static async Task Authority(Context context)
    {
        var files = await Files.Create(context, "openai-responses"); await Create(files);
        await Script(files, Tool(files.Api, "write", "invalid", new { path = "invalid.txt" }),
            Tool(files.Api, "write", "denied", new { path = "denied.txt", content = "no authority" }), Text(files.Api, "authority-final"));
        var result = await OneShot(files, "prompt", ["--message", "schema and policy", "--allow-write", files.In("invalid.txt")]);
        Equal(1, result.ExitCode); Equal("", result.Error);
        var report = JsonData.Parse(result.Output).Value;
        Equal(3, report.GetProperty("usedScriptTurns").GetInt32());
        Equal(1, report.GetProperty("actions").GetArrayLength());
        Check(!report.GetProperty("actions")[0].GetProperty("allowed").GetBoolean(), "Source continuation bypassed native policy.");
        Check(!File.Exists(files.In("invalid.txt")) && !File.Exists(files.In("denied.txt")), "Schema/policy rejection acquired a file effect.");
        var log = await Complete(files); Equal(2, Project(log).LlmMessages.Count(message => message.Role == "toolResult"));
        Check(Project(log).LlmMessages.Where(message => message.Role == "toolResult").All(message => message.WireBody.Value.GetProperty("isError").GetBoolean()), "Native rejection became tool success.");
        await files.VerifyReceipts(2);
    }

    private static async Task Create(Files files)
    {
        var result = await OneShot(files, "create", []);
        Check(result.ExitCode == 0 && result.Error.Length == 0, "Actual compiled package create failed: exit=" + result.ExitCode +
            " stderr=" + Bounded(result.Error) + " initialization=" + await files.LastActivationFailure());
        Check(JsonData.Parse(result.Output).Value.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "Create receipt preceded durable flush.");
    }
    private static void AssertRpcTurn(Files files, JsonData[] records, string blocked, string allowed)
    {
        var toolEnds = records.Where(record => Type(record) == "tool_execution_end").ToArray(); Equal(2, toolEnds.Length);
        Equal(ExpectedCallId(files.Api, blocked), toolEnds[0].Value.GetProperty("toolCallId").GetString()); Check(toolEnds[0].Value.GetProperty("isError").GetBoolean(), "Protected write succeeded.");
        Equal(ExpectedCallId(files.Api, allowed), toolEnds[1].Value.GetProperty("toolCallId").GetString()); Check(!toolEnds[1].Value.GetProperty("isError").GetBoolean(), "Ordinary write failed.");
        var notice = records.Single(record => Type(record) == "extension_ui_request");
        Equal("notify", notice.Value.GetProperty("method").GetString()); Equal("warning", notice.Value.GetProperty("notifyType").GetString());
        // Native preparation resolves path to the exact target; the original source interpolates that path verbatim.
        Equal("Blocked write to protected path: " + files.In(".env"), notice.Value.GetProperty("message").GetString());
        Check(Array.IndexOf(records, notice) < Array.IndexOf(records, toolEnds[0]), "Actual RPC notification escaped its awaited hook boundary.");
        Equal(3, records.Count(record => Type(record) == "message_end" && record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant"));
        Equal(1, records.Count(record => Type(record) == "agent_settled"));
    }
    // Exact identities from the authored DTOs above, retaining Responses call_id and item id without normalization.
    private static string ExpectedCallId(string api, string call) => api switch
    {
        "openai-responses" => call + "|fc-" + call,
        "anthropic-messages" or "openai-completions" => call,
        _ => throw new InvalidOperationException("No authored provider identity expectation.")
    };
    private static async Task Acknowledged(Files files, JsonElement entries)
    {
        SessionLogReadResult log;
        await using (var source = new FileStream(files.Session, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous))
            log = await new SessionLogReader().ReadAsync(source, leaveOpen: true);
        Complete(log); Equal(entries.GetArrayLength(), log.ValidatedPrefix.Length - 1);
        for (var index = 0; index < entries.GetArrayLength(); index++)
            Equal(entries[index].GetRawText(), log.ValidatedPrefix[index + 1].Entry.WireBody.ToString());
        Equal((long)log.OriginalBytes.Length, new FileInfo(files.Session).Length);
    }
    private static void NativeLoadout(SessionLogReadResult log)
    {
        var systems = log.ValidatedPrefix.Skip(1).Select(record => record.Entry.WireBody.Value)
            .Where(entry => entry.TryGetProperty("message", out var message) && message.GetProperty("role").GetString() == "system").ToArray();
        Check(systems.Length > 0, "Durable initial system loadout missing.");
        foreach (var entry in systems)
            Check(entry.GetProperty("message").GetProperty("toolsAdded").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).SequenceEqual(["read", "write"]), "Hook-only durable binding advertised an artificial tool.");
    }
    private static async Task<SessionLogReadResult> Complete(Files files)
    {
        var log = await new SessionLogReader().ReadFileAsync(files.Session); Complete(log);
        await using var reopened = await SessionLogStore.OpenAsync(files.Session); Equal((long)log.OriginalBytes.Length, reopened.Snapshot.CommittedByteLength);
        return log;
    }
    private static void Complete(SessionLogReadResult log) => Check(log.SourceComplete && log.Status == SessionLogReadStatus.Complete &&
        log.ValidatedPrefixByteLength == log.OriginalBytes.Length, "Actual durable log is incomplete.");
    private static SessionContextProjection Project(SessionLogReadResult log, string? leaf = null) => leaf is null
        ? new SessionContextProjector().ProjectLatest(log.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray())
        : new SessionContextProjector().Project(log.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray(), leaf);
    private static Task Script(Files files, params object[] turns) => File.WriteAllTextAsync(files.Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns }), Utf8);

    private static object Tool(string api, string name, string call, object arguments, params string[] required)
    {
        var json = JsonSerializer.Serialize(arguments); var middle = json.Length / 2;
        object[] events = api switch
        {
            "anthropic-messages" => [AnthropicStart("msg-" + call),
                new { type = "content_block_start", index = 9, content_block = new { type = "tool_use", id = call, name, input = new { } } },
                new { type = "content_block_delta", index = 9, delta = new { type = "input_json_delta", partial_json = json[..middle] } },
                new { type = "content_block_delta", index = 9, delta = new { type = "input_json_delta", partial_json = json[middle..] } },
                new { type = "content_block_stop", index = 9 }, AnthropicFinish("tool_use"), new { type = "message_stop" }],
            "openai-completions" => [new { id = "cmpl-" + call, @object = "chat.completion.chunk", model = "pisharp-offline-completions-session",
                choices = new[] { new { index = 0, delta = new { tool_calls = new[] { new { index = 9, id = call, type = "function", function = new { name, arguments = json[..middle] } } } }, finish_reason = (string?)null } } },
                new { choices = new[] { new { index = 0, delta = new { tool_calls = new[] { new { index = 9, function = new { arguments = json[middle..] } } } }, finish_reason = (string?)null } } }, CompletionsFinish("tool_calls"), CompletionsUsage()],
            _ => [new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name, arguments = "" } },
                new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name, arguments = json } }, ResponsesFinish()]
        };
        return new { requiredInputTexts = required, events };
    }
    private static object Text(string api, string text, params string[] required)
    {
        object[] events = api switch
        {
            "anthropic-messages" => [AnthropicStart("msg-text"), new { type = "content_block_start", index = 4, content_block = new { type = "text", text = "" } },
                new { type = "content_block_delta", index = 4, delta = new { type = "text_delta", text } }, new { type = "content_block_stop", index = 4 }, AnthropicFinish("end_turn"), new { type = "message_stop" }],
            "openai-completions" => [new { id = "cmpl-text", @object = "chat.completion.chunk", model = "pisharp-offline-completions-session",
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = (string?)null } } }, CompletionsFinish("stop"), CompletionsUsage()],
            _ => [new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "msg", content = Array.Empty<object>() } },
                new { type = "response.output_text.delta", output_index = 0, item_id = "msg", delta = text },
                new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "msg", content = new[] { new { type = "output_text", text } } } }, ResponsesFinish()]
        };
        return new { requiredInputTexts = required, events };
    }
    private static object AnthropicStart(string id) => new { type = "message_start", message = new { id, role = "assistant", model = "pisharp-offline-session",
        content = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 0, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 } } };
    private static object AnthropicFinish(string reason) => new { type = "message_delta", delta = new { stop_reason = reason }, usage = new { output_tokens = 4 } };
    private static object CompletionsFinish(string reason) => new { choices = new[] { new { index = 0, delta = new { }, finish_reason = reason } } };
    private static object CompletionsUsage() => new { choices = Array.Empty<object>(), usage = new { prompt_tokens = 8, completion_tokens = 4, total_tokens = 12 } };
    private static object ResponsesFinish() => new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } };

    private sealed record Result(int ExitCode, string Output, string Error);
    private static async Task<Result> OneShot(Files files, string command, string[] extra)
    {
        var invocation = await files.Invocation(command); var start = Start(files, invocation, command, extra);
        using var process = Process.Start(start) ?? throw new IOException("Compiled package CLI did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); process.StandardInput.Close();
        var output = ReadBytes(process.StandardOutput.BaseStream, deadline.Token); var error = ReadBytes(process.StandardError.BaseStream, deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); return new(process.ExitCode, Utf8.GetString(await output), Utf8.GetString(await error)); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await Persist(invocation, process.ExitCode, output, error);
        }
    }
    private static ProcessStartInfo Start(Files files, string invocation, string command, IEnumerable<string> extra)
    {
        var start = new ProcessStartInfo(files.C.Host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = Utf8 };
        start.Environment["PISHARP_NATIVE_NODE_OPTIONS"] = Path.Combine(invocation, "options.json"); start.ArgumentList.Add(files.C.Cli);
        foreach (var arg in new[] { "session", command, "--session", files.Session, "--workspace", files.Root, "--offline-api", files.Api }
            .Concat(command == "create" ? [] : new[] { "--offline-script", files.Script }).Concat(files.ExtensionArgs).Concat(extra)) start.ArgumentList.Add(arg);
        return start;
    }
    private static async Task<byte[]> ReadBytes(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var count = await stream.ReadAsync(buffer, token); if (count == 0) return output.ToArray(); Check(count <= 2_097_152 - output.Length, "Actual CLI I/O receipt limit."); output.Write(buffer, 0, count); }
    }
    private static async Task Persist(string invocation, int exitCode, Task<byte[]> output, Task<byte[]> error)
    {
        foreach (var (name, task) in new[] { ("cli.stdout.jsonl", output), ("cli.stderr.bin", error) })
            try { await File.WriteAllBytesAsync(Path.Combine(invocation, name), await task); } catch (Exception) when (task.IsCanceled || task.IsFaulted) { }
        await File.WriteAllTextAsync(Path.Combine(invocation, "cli.exit.json"), JsonSerializer.Serialize(new { exitCode, outputJoined = output.IsCompletedSuccessfully, errorJoined = error.IsCompletedSuccessfully }) + "\n", Utf8);
    }
    private sealed class RpcChild : IAsyncDisposable
    {
        private readonly Process process; private readonly string invocation;
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
        private readonly object gate = new(); private readonly List<JsonData> records = [];
        private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task<byte[]> output, error; private bool closed;
        internal JsonData[] Records { get { lock (gate) return records.ToArray(); } }
        private RpcChild(Files files, string invocation, string[] extra)
        {
            this.invocation = invocation; process = Process.Start(NodePackageActivationTests.Start(files, invocation, "rpc", extra)) ?? throw new IOException("Compiled package RPC did not start.");
            output = ReadOutput(); error = ReadBytes(process.StandardError.BaseStream, deadline.Token);
        }
        internal static async Task<RpcChild> Start(Files files, string[] extra) => new(files, await files.Invocation("rpc"), extra);
        internal async Task Send(object command)
        { await process.StandardInput.WriteAsync((JsonSerializer.Serialize(command) + "\n").AsMemory(), deadline.Token); await process.StandardInput.FlushAsync(deadline.Token); }
        internal Task<JsonData> Response(string id) => Wait(record => Type(record) == "response" && record.Value.TryGetProperty("id", out var value) && value.GetString() == id);
        internal async Task<JsonData> Wait(Func<JsonData, bool> predicate)
        {
            while (true) { Task next; lock (gate) { var match = records.FirstOrDefault(predicate); if (match is not null) return match;
                    Check(!output.IsCompleted, "Actual RPC ended before its observer."); next = changed.Task; } await next.WaitAsync(deadline.Token); }
        }
        private async Task<byte[]> ReadOutput()
        {
            using var retained = new ObservedReadStream(process.StandardOutput.BaseStream);
            try
            {
                await using var reader = new JsonlReader(retained);
                await foreach (var frame in reader.ReadAdmissionsAsync(deadline.Token))
                {
                    Check(frame.IsAccepted && !frame.IsFinalFrame, "Actual RPC stdout has a malformed/truncated frame."); var record = frame.Record!;
                    TaskCompletionSource signal; lock (gate) { Check(records.Count < 2048, "Actual RPC record limit."); records.Add(record); signal = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously); } signal.TrySetResult();
                }
                return retained.Bytes;
            }
            finally
            {
                await File.WriteAllBytesAsync(Path.Combine(invocation, "cli.stdout.jsonl"), retained.Bytes);
                lock (gate) changed.TrySetResult();
            }
        }
        internal async Task<Result> Finish()
        { if (!closed) { closed = true; process.StandardInput.Close(); } await process.WaitForExitAsync(deadline.Token); return new(process.ExitCode, Utf8.GetString(await output), Utf8.GetString(await error)); }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                await Persist(invocation, process.ExitCode, output, error);
            }
            finally { process.Dispose(); deadline.Dispose(); }
        }
    }
    // Observe the actual byte reads feeding the normal JSONL decoder; do not reserialize RPC records as evidence.
    private sealed class ObservedReadStream(Stream source) : Stream
    {
        private readonly MemoryStream bytes = new();
        internal byte[] Bytes => bytes.ToArray();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { var read = source.Read(buffer, offset, count); Retain(buffer.AsSpan(offset, read)); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var read = await source.ReadAsync(buffer, cancellationToken); Retain(buffer.Span[..read]); return read; }
        private void Retain(ReadOnlySpan<byte> buffer) { Check(buffer.Length <= 2_097_152 - bytes.Length, "Actual RPC I/O receipt limit."); bytes.Write(buffer); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) bytes.Dispose(); base.Dispose(disposing); }
    }
    private sealed class Files
    {
        internal Context C { get; } internal string Api { get; } internal string Root { get; }
        internal string Session => In("session.jsonl"); internal string Script => In("script.json");
        internal string Package => In("published"); internal string Manifest => In("manifest.json");
        internal string Approval => In("approval.json"); internal string Snapshots => In("snapshots");
        private readonly List<string> invocations = [];
        internal string In(string name) => Path.Combine(Root, name);
        internal string[] ExtensionArgs => ["--extension-package", Package, "--extension-manifest", Manifest, "--extension-approval", Approval, "--extension-snapshot-root", Snapshots];
        private Files(Context context, string api) { C = context; Api = api; Root = Path.Combine(C.Runs, "node-package-" + api + "-" + Guid.NewGuid().ToString("N")); }
        internal static async Task<Files> Create(Context context, string api)
        {
            var files = new Files(context, api); foreach (var path in new[] { files.Root, files.Package, files.Snapshots }) Directory.CreateDirectory(path);
            Check(File.Exists(Path.Combine(context.Published, "PublishedFixture.NodeProtectedPaths.dll")), "Root must publish the actual hook-only package first.");
            foreach (var source in Directory.GetFiles(context.Published, "*", SearchOption.AllDirectories))
            { var target = Path.Combine(files.Package, Path.GetRelativePath(context.Published, source)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target); }
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(files.Package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                hashes.Add(Path.GetRelativePath(files.Package, file).Replace('\\', '/'), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file))));
            var metadata = JsonSerializer.Serialize(new { schemaVersion = 0, id = "fixture.node.protected-paths", packageVersion = "0.0.1",
                hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native", assembly = "PublishedFixture.NodeProtectedPaths.dll",
                entryType = "PublishedNodeProtectedPathsFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
                requiredFeatures = new[] { "owned-descriptor-callbacks", "registered-input-tool-reducers" }, declaredCapabilities = new[] { "observations" },
                resourcePaths = hashes.Keys.Where(path => path != "PublishedFixture.NodeProtectedPaths.dll").ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
            await File.WriteAllTextAsync(files.Manifest, metadata, Utf8);
            await File.WriteAllTextAsync(files.Approval, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution", packageRoot = files.Package,
                manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(metadata))), artifactHashes = hashes, sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit",
                policyRevision = "experimental-policy-0", hostGeneration = 1, sessionPath = files.Session, workspace = files.Root, snapshotRoot = files.Snapshots, enabledTools = Array.Empty<string>(),
                systemImports = new[] { new { profile = "windows-worker-directory-0", artifact = "PiSharp.ExtensionHost.dll", sha256 = hashes["PiSharp.ExtensionHost.dll"] } } }), Utf8);
            return files;
        }
        internal async Task<string> Invocation(string command)
        {
            var root = In("invocation-" + (invocations.Count + 1) + "-" + command); Directory.CreateDirectory(root); invocations.Add(root);
            await File.WriteAllTextAsync(Path.Combine(root, "options.json"), JsonSerializer.Serialize(new { schemaVersion = 1, node = C.Node, repository = C.Repository,
                oracle = C.Oracle, jiti = C.Jiti, reference = C.Reference, runRoot = Path.Combine(root, "worker"), receipt = Path.Combine(root, "package.receipt.json"),
                workerGeneration = invocations.Count, sessionGeneration = invocations.Count }), Utf8);
            return root;
        }
        internal async Task<string> LastActivationFailure()
        {
            var path = Path.Combine(invocations[^1], "package.receipt.json");
            if (!File.Exists(path)) return "no package cleanup receipt";
            var file = new FileInfo(path); if (file.Length > 2_097_152) return "package cleanup receipt exceeds admitted bound";
            var receipt = JsonData.Parse(await File.ReadAllTextAsync(path)).Value;
            return JsonSerializer.Serialize(new
            {
                stage = receipt.TryGetProperty("initializationStage", out var stage) ? stage.GetString() : null,
                failure = receipt.TryGetProperty("initializationFailure", out var failure) ? Bounded(failure.GetRawText()) : null
            });
        }
        internal async Task VerifyReceipts(int expected)
        {
            Equal(expected, invocations.Count); Equal(0, Directory.GetDirectories(Snapshots).Length);
            foreach (var invocation in invocations)
            {
                var receipt = JsonData.Parse(await File.ReadAllTextAsync(Path.Combine(invocation, "package.receipt.json"))).Value;
                Check(receipt.GetProperty("hookOnly").GetBoolean(), "Published entry added another execution surface.");
                Equal(0, receipt.GetProperty("activeNativeContexts").GetInt32()); Equal(0, receipt.GetProperty("cleanupFailures").GetArrayLength());
                var load = receipt.GetProperty("sourceLoad"); Check(load.GetProperty("factoryAwaited").GetBoolean() && load.GetProperty("sourceFunctionRemainsInNode").GetBoolean(), "Actual source factory/function proof missing.");
                Equal("9ca66b1f3b1b9a61cc5ddc660f92cca2ea66c4264a1bdb6b00e0da5844399fe2", load.GetProperty("source").GetProperty("sha256").GetString());
                Check(load.GetProperty("loadedModules").EnumerateArray().Any(row => row.GetProperty("path").GetString() == "upstream/packages/coding-agent/src/core/extensions/loader.ts"), "Actual public loadExtensions module missing.");
                Check(load.GetProperty("loadedModules").EnumerateArray().Any(row => row.GetProperty("path").GetString()!.StartsWith("jiti-root/node_modules/jiti/", StringComparison.Ordinal)), "Official Jiti module missing.");
                Check(receipt.GetProperty("sourceFinalization").GetProperty("immutableInputsVerified").GetBoolean(), "Actual immutable input verification missing.");
                if (!invocation.EndsWith("-create", StringComparison.Ordinal))
                {
                    var observation = receipt.GetProperty("sourceLastObservation");
                    Check(observation.GetProperty("publicationJoined").GetBoolean(), "Actual source/native publication join missing.");
                    Equal("undefined", observation.GetProperty("resultPresence").GetString());
                    Equal(observation.GetProperty("inputBefore").GetProperty("serializedJson").GetString(), observation.GetProperty("inputAfter").GetProperty("serializedJson").GetString());
                    using var call = JsonDocument.Parse(observation.GetProperty("inputBefore").GetProperty("serializedJson").GetString()!);
                    Equal("write", call.RootElement.GetProperty("toolName").GetString());
                    Check(receipt.GetProperty("sourceLastSettlement").GetProperty("settled").GetBoolean(), "Actual JS settlement fence missing.");
                }
                var termination = receipt.GetProperty("termination"); Equal(0, termination.GetProperty("ExitCode").GetInt32());
                Check(termination.GetProperty("HasExited").GetBoolean() && !termination.GetProperty("KillAttempted").GetBoolean() && termination.GetProperty("PinsRechecked").GetBoolean(), "Node worker did not naturally exit with actual joins/pins.");
                Equal(0, termination.GetProperty("Failures").GetArrayLength()); Equal(0L, termination.GetProperty("ObservedStderrBytes").GetInt64());
                Equal(0L, termination.GetProperty("ObservedStdoutAfterProtocolBytes").GetInt64()); Check(termination.GetProperty("StdoutAfterProtocolEof").GetBoolean(), "Actual Node stdout EOF did not join.");
                Equal(3, termination.GetProperty("ExplicitStreamCloses").GetInt32());
                var protocol = termination.GetProperty("Protocol"); foreach (var name in new[] { "PendingCalls", "ActiveCallbacks", "PendingWrites", "RegisteredHandles", "BufferedBytes" }) Equal(0L, protocol.GetProperty(name).GetInt64());
                Check(protocol.GetProperty("Stopped").GetBoolean(), "Node protocol remained active.");
                var assemblies = receipt.GetProperty("assemblies"); var entry = assemblies.EnumerateArray().Single(row => row.GetProperty("name").GetString() == "PublishedFixture.NodeProtectedPaths");
                foreach (var name in new[] { "PiSharp.Compatibility.Node", "PiSharp.ExtensionHost" })
                { var actual = assemblies.EnumerateArray().Single(row => row.GetProperty("name").GetString() == name); Equal(entry.GetProperty("context").GetString(), actual.GetProperty("context").GetString()); Check(actual.GetProperty("collectible").GetBoolean(), "Node assembly escaped private package context."); }
                foreach (var name in new[] { "PiSharp.Extensions.Abstractions", "PiSharp.Contracts" })
                { var actual = assemblies.EnumerateArray().Single(row => row.GetProperty("name").GetString() == name); Equal("Default", actual.GetProperty("context").GetString()); Check(!actual.GetProperty("collectible").GetBoolean(), "Shared ABI loaded privately."); }
                Check(File.Exists(Path.Combine(invocation, "worker", "launch.receipt.json")) && File.Exists(Path.Combine(invocation, "worker", "termination.json")), "Raw actual supervisor receipts missing.");
            }
        }
    }
    private static string Absolute(string path) { Check(Path.IsPathFullyQualified(path), "Explicit absolute gate path required."); return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
    private static string Type(JsonData value) => value.Value.GetProperty("type").GetString()!;
    private static JsonElement Good(JsonData value) { Check(value.Value.GetProperty("success").GetBoolean(), "Actual RPC command failed."); return value.Value; }
    private static void Clean(Result result) => Check(result.ExitCode == 0 && result.Error.Length == 0,
        "Actual compiled CLI/RPC failure: exit=" + result.ExitCode + " stderr=" + Bounded(result.Error) + " stdout=" + Bounded(result.Output));
    private static string Bounded(string value) => JsonSerializer.Serialize(value.Length <= 2048 ? value : value[..2048] + "[truncated]");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Actual values differ: expected=" + expected + " actual=" + actual);
}
