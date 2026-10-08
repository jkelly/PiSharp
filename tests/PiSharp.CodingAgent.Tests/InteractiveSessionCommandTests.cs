using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Commands;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

internal static class InteractiveSessionCommandTests
{
    private static string host = "", cli = "", published = "";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string dotnetHost, string cliPath)
    {
        Check(Path.IsPathFullyQualified(dotnetHost) && File.Exists(dotnetHost) && Path.IsPathFullyQualified(cliPath) && File.Exists(cliPath), "Chat requires the compiled native host and CLI.");
        host = dotnetHost; cli = cliPath;
        var root = new DirectoryInfo(Path.GetDirectoryName(cli)!);
        while (!File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent ?? throw new InvalidOperationException("Native fixture root is absent.");
        published = Path.Combine(root.FullName, "artifacts", "extensions", "published-fixtures", "cli-ui");
        return
        [
            ("interactive compiled chat uses all three HTTP/provider families, actual file tools and durable reopen/branch", ProviderTurns),
            ("interactive published native dialogs and notifications share RPC while final file policy remains independent", PublishedDialogs),
            ("interactive input remains active for actual running state, owned queues, clear and joined abort", ActiveInput),
            ("interactive held published dialog EOF/abort joins actual callback, durable terminal and package leases", DialogShutdown),
            ("interactive closed stdout, actual write/flush faults and caller cancellation join borrowed input and durable cleanup", Failures),
            ("interactive multiline draft preserves blank lines and inert controls while rejecting Unicode and resource faults", EditorAdmission)
        ];
    }

    private static async Task ProviderTurns()
    {
        foreach (var api in new[] { "openai-responses", "anthropic-messages", "openai-completions" })
        {
            using var files = await Files.CreateAsync(); await Create(files, api);
            const string input = "real chat \u6587\U0001f642";
            const string saved = "saved \u6587\0value\n";
            const string final = "chat final \u6587\U0001f642\0\u001b[31m\u2028\u2029\n";
            await File.WriteAllTextAsync(files.Source, "owned source \u6587\n", Utf8);
            var sourceBytes = await File.ReadAllBytesAsync(files.Source); var initial = await File.ReadAllBytesAsync(files.Session);
            await Script(files, Tool(api, "read", "read", new { path = files.Source }, [input]),
                Tool(api, "write", "write", new { path = files.Target, content = saved }, ["owned source"]), Text(api, final, ["Successfully wrote"]));
            var scriptBytes = await File.ReadAllBytesAsync(files.Script);
            await using (var child = new Child(files, api, "--allow-read", files.Source, "--allow-write", files.Target))
            {
                await child.Ready(); await child.Send(input); await child.Wait("[settled]");
                await child.Send("/state"); await child.Wait("[state] idle pending=0");
                Check(child.Lines.Any(line => line.Contains("\\u0000\\u001b[31m\\u2028\\u2029", StringComparison.Ordinal)), "Display changed or executed control data.");
                Check(child.Lines.Any(line => line.Contains('\u6587')) && child.Lines.Any(line => line.Contains("\U0001f642", StringComparison.Ordinal)), "Compiled stdout lost Unicode across actual delta chunks.");
                Equal(2, child.Lines.Count(line => line.StartsWith("[tool start]", StringComparison.Ordinal)));
                Equal(2, child.Lines.Count(line => line.StartsWith("[tool end]", StringComparison.Ordinal)));
                Clean(await child.Finish());
            }
            Bytes(Utf8.GetBytes(saved), await File.ReadAllBytesAsync(files.Target));
            Bytes(sourceBytes, await File.ReadAllBytesAsync(files.Source)); Bytes(scriptBytes, await File.ReadAllBytesAsync(files.Script));
            var first = await Complete(files); Check(first.OriginalBytes.AsSpan().StartsWith(initial), "Chat rewrote the existing session prefix.");
            var firstContext = Context(first); var leaf = firstContext.LeafId!;
            var finalMessage = LastAssistant(firstContext); Equal(final, finalMessage.GetProperty("content")[0].GetProperty("text").GetString());
            Equal(api, finalMessage.GetProperty("api").GetString()); Equal(api == "anthropic-messages" ? "anthropic" : "openai", finalMessage.GetProperty("provider").GetString());
            Equal(4, finalMessage.GetProperty("usage").GetProperty("output").GetInt32()); Equal(12, finalMessage.GetProperty("usage").GetProperty("totalTokens").GetInt32());
            Check(firstContext.LlmMessages.Any(entry => entry.WireBody.Value.GetProperty("role").GetString() == "toolResult" &&
                entry.WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString()!.Contains("Successfully wrote", StringComparison.Ordinal)), "Durable tool acknowledgment is missing.");
            Check(firstContext.LlmMessages.Any(entry => entry.WireBody.Value.GetProperty("role").GetString() == "assistant" &&
                entry.WireBody.Value.GetProperty("content").EnumerateArray().Any(block => block.GetProperty("type").GetString() == "toolCall" &&
                    block.GetProperty("name").GetString() == "write" && block.GetProperty("arguments").GetProperty("content").GetString() == saved)), "Durable final arguments lost inert NUL.");
            await Script(files, Text(api, "latest response", [input, "Successfully wrote"]));
            await using (var reopened = new Child(files, api))
            { await reopened.Ready(); await reopened.Wait("[assistant] chat final"); await reopened.Send("resume latest"); await reopened.Wait("[settled]"); Clean(await reopened.Finish()); }
            var latest = await Complete(files); Check(latest.OriginalBytes.AsSpan().StartsWith(first.OriginalBytes.AsSpan()), "Reopen rewrote prior physical bytes.");
            await Script(files, Text(api, "branch response", [input, "Successfully wrote"]));
            await using (var branch = new Child(files, api, "--leaf", leaf))
            {
                await branch.Ready(); await branch.Wait("[assistant] chat final");
                Check(!branch.Lines.Any(line => line.Contains("latest response", StringComparison.Ordinal)), "Selected branch inserted a sibling into history.");
                await branch.Send("selected branch"); await branch.Wait("[settled]"); Clean(await branch.Finish());
            }
            var branched = await Complete(files); Check(branched.OriginalBytes.AsSpan().StartsWith(latest.OriginalBytes.AsSpan()), "Branch append changed existing bytes.");
            var newEntries = branched.ValidatedPrefix.Skip(latest.ValidatedPrefix.Length).Select(record => record.Entry).ToArray();
            Equal(leaf, newEntries[0].ParentId);
            Check(!Context(branched).LlmMessages.Any(entry => TextOf(entry.WireBody.Value).Contains("latest response", StringComparison.Ordinal)), "Branch context included the later sibling.");
        }
    }

    private static async Task PublishedDialogs()
    {
        using var files = await Files.CreateAsync(plugin: true); await Create(files, "openai-responses");
        const string answer = "input\0\u6587";
        const string edited = "prefill\r\n\nedited \U0001f642\0";
        await Script(files, Tool("openai-responses", "fixture.cli.ui", "dialogs", new { action = "dialogs" }),
            Tool("openai-responses", "write", "write", new { path = files.Target, content = "approved file\0\n" }, ["ui:approved"]), Text("openai-responses", "dialog final", ["Successfully wrote"]));
        await using (var child = new Child(files, "openai-responses", "--allow-write", files.Target))
        {
            await child.Ready(); await child.Send("published dialogs");
            await child.Wait("[ui select]"); await child.Send("/state"); await child.Wait("[state] running pending=0"); await child.Send("1");
            await child.Wait("[ui confirm]"); await child.Send("maybe"); await child.Wait("[ui] Enter yes/no"); await child.Send("yes");
            await child.Wait("[ui input]"); await child.Send(answer);
            await child.Wait("[ui editor]"); await child.Send("edited \U0001f642\0"); await child.Wait("[ui draft]"); await child.Send("/save");
            await child.Wait("[notice] notice\\u0000\u6587"); await child.Wait("[status] status"); await child.Wait("[widget] widget"); await child.Wait("[title] title \U0001f642");
            await child.Wait("[draft set]"); await child.Wait("[settled]"); Clean(await child.Finish());
            Check(!string.Join('\n', child.Lines).Contains('\u001b'), "Plugin display emitted a terminal escape.");
        }
        Bytes(Utf8.GetBytes("approved file\0\n"), await File.ReadAllBytesAsync(files.Target));
        var log = await Complete(files); var result = Context(log).LlmMessages.First(entry => entry.WireBody.Value.GetProperty("role").GetString() == "toolResult").WireBody.Value;
        var details = result.GetProperty("details"); Equal("Rpc", details.GetProperty("mode").GetString()); Check(details.GetProperty("approved").GetBoolean(), "Typed UI confirm was not delivered.");
        var observations = details.GetProperty("observations"); Equal(11, observations.GetArrayLength());
        Equal("", observations[0].GetProperty("value").GetString()); Equal(true, observations[1].GetProperty("value").GetBoolean());
        Equal(answer, observations[2].GetProperty("value").GetString()); Equal(edited, observations[3].GetProperty("value").GetString());
        Equal("Value", observations[0].GetProperty("kind").GetString());
        var denied = files.In("not-approved.txt");
        await Script(files, Tool("openai-responses", "fixture.cli.ui", "confirm", new { action = "confirm" }),
            Tool("openai-responses", "write", "denied", new { path = denied, content = "must not execute" }, ["ui:approved"]), Text("openai-responses", "denied final"));
        await using (var child = new Child(files, "openai-responses"))
        { await child.Ready(); await child.Send("separate file authority"); await child.Wait("[ui confirm]"); await child.Send("yes"); await child.Wait("[tool end] write error=true"); await child.Wait("[settled]"); Clean(await child.Finish()); }
        Check(!File.Exists(denied), "UI approval bypassed mandatory native final-action policy."); await Complete(files); Markers(files, 3);
    }

    private static async Task ActiveInput()
    {
        foreach (var api in new[] { "openai-responses", "anthropic-messages", "openai-completions" })
        {
            using var files = await Files.CreateAsync(); await Create(files, api); await Script(files, Text(api, "unconsumed held bytes", gate: true));
            await using (var child = new Child(files, api))
            {
                await child.Ready(); await child.Send("held work"); await child.Wait("[assistant started]");
                await child.Send("/state"); await child.Wait("[state] running pending=0");
                await child.Send("/steer retain steering"); await child.Wait("[accepted] steer queued");
                await child.Send("/follow-up retain followup"); await child.Wait("[accepted] follow_up queued");
                await child.Send("/state"); await child.Wait("[state] running pending=2");
                await child.Send("/abort"); await child.Wait("[accepted] abort"); await child.Wait("[settled]");
                await child.Send("/state"); await child.Wait("[state] idle pending=2");
                await child.Send("/clear-queue"); await child.Wait("[cleared] steering=[\"retain steering\"] followUp=[\"retain followup\"]");
                await child.Send("/state"); await child.Wait("[state] idle pending=0"); Clean(await child.Finish());
                Equal(1, child.Lines.Count(line => line == "[running]")); Check(!child.Lines.Any(line => line.Contains("unconsumed held bytes", StringComparison.Ordinal)), "Abort admitted held response bytes.");
            }
            var log = await Complete(files); var context = Context(log); Equal("aborted", LastAssistant(context).GetProperty("stopReason").GetString());
            Check(!context.LlmMessages.Any(entry => TextOf(entry.WireBody.Value).Contains("retain ", StringComparison.Ordinal)), "Queued or cleared input was appended as a canonical message.");
        }
    }

    private static async Task DialogShutdown()
    {
        foreach (var abort in new[] { false, true })
        {
            using var files = await Files.CreateAsync(plugin: true); await Create(files, "openai-responses");
            await Script(files, Tool("openai-responses", "fixture.cli.ui", "confirm", new { action = "confirm" }),
                Tool("openai-responses", "write", "later", new { path = files.Target, content = "must not execute" }), Text("openai-responses", "must not acquire"));
            await using (var child = new Child(files, "openai-responses", "--allow-write", files.Target))
            {
                await child.Ready(); await child.Send("held published callback"); await child.Wait("[ui confirm]");
                if (abort) { await child.Send("/abort"); await child.Wait("[accepted] abort"); await child.Wait("[settled]"); }
                Clean(await child.Finish()); Check(child.Lines.Any(line => line == "[settled]"), "EOF omitted actual awaited settlement.");
            }
            Check(!File.Exists(files.Target) && !files.MarkerLines.Contains("approved"), "Disconnect supplied approval or continued into another effect.");
            Equal(1, files.MarkerLines.Count(line => line == "tool-closed")); Markers(files, 2);
            var context = Context(await Complete(files));
            Check(context.LlmMessages.Any(entry => entry.WireBody.Value.GetProperty("role").GetString() == "toolResult" && entry.WireBody.Value.GetProperty("isError").GetBoolean()), "Canceled tool outcome was lost from durable history.");
        }
    }

    private static async Task Failures()
    {
        using (var files = await Files.CreateAsync(plugin: true))
        {
            await Create(files, "openai-responses"); await Script(files, Tool("openai-responses", "fixture.cli.ui", "confirm", new { action = "confirm" }),
                Tool("openai-responses", "write", "later", new { path = files.Target, content = "must not execute" }), Text("openai-responses", "must not acquire"));
            await using (var child = new Child(files, "openai-responses", "--allow-write", files.Target))
            {
                await child.Ready(); await child.Send("broken actual stdout"); await child.Wait("[ui confirm]"); child.BreakOutput(); await child.Send("/state");
                var result = await child.Finish(allowReadFailure: true); Equal(1, result.ExitCode); Equal("RpcHostFailed", JsonData.Parse(result.Error).Value.GetProperty("code").GetString());
            }
            Check(!File.Exists(files.Target) && !files.MarkerLines.Contains("approved"), "Closed stdout authorized a later action."); await Complete(files); Markers(files, 2);
        }
        foreach (var fault in new[] { "write", "flush", "cancel", "held-flush-cancel" })
        {
            using var files = await Files.CreateAsync(); await Create(files, "openai-completions"); await Script(files, Text("openai-completions", "unconsumed fault bytes", gate: true));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); using var cancellation = new CancellationTokenSource();
            using var input = new ControlledReader(); using var view = new ControlledView(); using var errors = new StringWriter();
            var running = InteractiveSessionCommand.RunAsync(ChatArgs(files, "openai-completions"), input, view, errors, cancellation.Token);
            try
            {
                await view.Wait("[history]", deadline.Token); await input.Send("held caller work\n", deadline.Token); await view.Wait("[assistant started]", deadline.Token);
                if (fault == "cancel") cancellation.Cancel();
                else if (fault == "held-flush-cancel")
                {
                    view.HoldStateFlush = true; await input.Send("/state\n", deadline.Token); await view.FlushEntered.Task.WaitAsync(deadline.Token);
                    cancellation.Cancel(); Check(!running.IsCompleted, "Caller cancellation skipped the actual admitted output flush."); view.ReleaseFlush.TrySetResult();
                }
                else { view.Fault = fault; await input.Send("/state\n", deadline.Token); }
                Equal(1, await running.WaitAsync(deadline.Token));
                var diagnostic = errors.ToString(); Equal(1, diagnostic.Count(character => character == '\n'));
                Equal(fault is "cancel" or "held-flush-cancel" ? "Canceled" : "RpcHostFailed", JsonData.Parse(diagnostic).Value.GetProperty("code").GetString());
                Check(input.CanceledRead && !input.Disposed && !view.Disposed, "Host detached its actual read or disposed borrowed frontend services.");
                Equal(0, input.ActiveReads); Equal(0, view.ActiveOperations);
                Equal(fault is "cancel" or "held-flush-cancel" ? 0 : 1, view.FailedOperations);
                Check(!view.Text.Contains("unconsumed fault bytes", StringComparison.Ordinal), "Write/flush failure woke held bytes into success.");
                var context = Context(await Complete(files)); Check(!context.LlmMessages.Any(entry => TextOf(entry.WireBody.Value).Contains("unconsumed fault bytes", StringComparison.Ordinal)), "Output failure persisted unconsumed output.");
            }
            finally { cancellation.Cancel(); input.Complete(); view.ReleaseFlush.TrySetResult(); await running; }
        }
    }

    private static async Task EditorAdmission()
    {
        using (var files = await Files.CreateAsync())
        {
            await Create(files, "anthropic-messages"); const string prompt = "\nfirst\0\u001b[31m\t\u6587\U0001f642\n\nlast";
            await Script(files, Text("anthropic-messages", "editor response", [prompt]));
            await using (var child = new Child(files, "anthropic-messages"))
            {
                await child.Ready(); await child.Send("/edit"); await child.Wait("[draft]"); await child.Send(""); await child.Send("first\0\u001b[31m\t\u6587\U0001f642"); await child.Send(""); await child.Send("last");
                await child.Send("/save"); await child.Wait("[settled]");
                Check(child.Lines.Any(line => line.Contains("first\\u0000\\u001b[31m\\u0009\u6587\U0001f642", StringComparison.Ordinal)), "Cooked display lost exact control escaping.");
                Clean(await child.Finish());
            }
            var context = Context(await Complete(files)); Equal(prompt, context.LlmMessages.First(entry => entry.WireBody.Value.GetProperty("role").GetString() == "user").WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString());
        }
        foreach (var rejected in new[] { new string('x', 65_537) + "\n", "private rejected \ud800\n" })
        {
            using var files = await Files.CreateAsync(); await Create(files, "openai-responses"); var original = await File.ReadAllBytesAsync(files.Session); await Script(files, Text("openai-responses", "never requested"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); using var cancellation = new CancellationTokenSource();
            using var input = new ControlledReader(); using var view = new ControlledView(); using var errors = new StringWriter();
            var running = InteractiveSessionCommand.RunAsync(ChatArgs(files, "openai-responses"), input, view, errors, cancellation.Token);
            try
            {
                await view.Wait("[history]", deadline.Token); await input.Send(rejected, deadline.Token); Equal(1, await running.WaitAsync(deadline.Token));
                Bytes(original, await File.ReadAllBytesAsync(files.Session)); Check(!errors.ToString().Contains("private rejected", StringComparison.Ordinal), "Rejected input leaked through diagnostics.");
                Equal(0, view.Text.Split("[running]", StringSplitOptions.None).Length - 1); await Complete(files);
            }
            finally { cancellation.Cancel(); input.Complete(); await running; }
        }
        using (var files = await Files.CreateAsync())
        {
            await Create(files, "openai-responses"); await Script(files, Text("openai-responses", "discard proof"));
            await using (var child = new Child(files, "openai-responses"))
            { await child.Ready(); await child.Send("/edit"); await child.Send("discard me"); await child.Send("/cancel"); await child.Wait("[draft discarded]"); await child.Send("/not-a-command"); await child.Wait("[command] unavailable"); Clean(await child.Finish()); }
            Check(!Context(await Complete(files)).LlmMessages.Any(entry => entry.WireBody.Value.GetProperty("role").GetString() == "user"), "Unsubmitted draft became canonical input.");
            var original = await File.ReadAllBytesAsync(files.Session);
            var rejected = await OneShot(files, ChatArgs(files, "openai-responses", "--output", "print")); Equal(2, rejected.ExitCode); Equal("", rejected.Output);
            Equal("InvalidArguments", JsonData.Parse(rejected.Error).Value.GetProperty("code").GetString()); Bytes(original, await File.ReadAllBytesAsync(files.Session));
        }
    }

    private static object Tool(string api, string name, string call, object arguments, string[]? required = null) => api switch
    {
        "anthropic-messages" => SessionCommandTests.AnthropicTool(name, call, arguments, required),
        "openai-completions" => SessionCommandTests.CompletionsTool(name, call, arguments, required),
        _ => new { requiredInputTexts = required ?? [], events = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name, arguments = "" } },
            new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name, arguments = JsonSerializer.Serialize(arguments) } }, Completed()
        } }
    };
    private static object Text(string api, string text, string[]? required = null, bool gate = false)
    {
        object result = api switch
        {
            "anthropic-messages" => SessionCommandTests.AnthropicText(text, required: required),
            "openai-completions" => SessionCommandTests.CompletionsText(text, required: required),
            _ => new { requiredInputTexts = required ?? [], events = new object[]
            {
                new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "msg-text", content = Array.Empty<object>() } },
                new { type = "response.output_text.delta", output_index = 0, item_id = "msg-text", delta = text },
                new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "msg-text", content = new[] { new { type = "output_text", text } } } }, Completed()
            } }
        };
        if (!gate) return result;
        var fields = JsonSerializer.SerializeToElement(result).EnumerateObject().ToDictionary(field => field.Name, field => (object?)field.Value.Clone(), StringComparer.Ordinal);
        fields.Add("rpcGate", new { releaseOnGetStateId = "never-release-authored-chat-gate" }); return fields;
    }
    private static object Completed() => new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } };
    private static Task Script(Files files, params object[] turns) => File.WriteAllTextAsync(files.Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns }), Utf8);
    private static string[] Base(Files files, string mode, string api) => ["session", mode, "--session", files.Session, "--workspace", files.Root, "--offline-api", api];
    private static string[] ChatArgs(Files files, string api, params string[] extra) => Base(files, "chat", api).Concat(["--offline-script", files.Script]).Concat(files.ExtensionArgs).Concat(extra).ToArray();
    private static async Task Create(Files files, string api)
    { var result = await OneShot(files, Base(files, "create", api).Concat(files.ExtensionArgs).ToArray()); Clean(result); Check(JsonData.Parse(result.Output).Value.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "Create did not acknowledge durable header/configuration."); }
    private static async Task<SessionLogReadResult> Complete(Files files)
    {
        var log = await new SessionLogReader().ReadFileAsync(files.Session);
        Check(log.SourceComplete && log.Status == SessionLogReadStatus.Complete && log.ValidatedPrefixByteLength == log.OriginalBytes.Length && new FileInfo(files.Session).Length == log.OriginalBytes.Length, "Chat returned before a complete closed durable file.");
        await using var exclusive = await SessionLogStore.OpenAsync(files.Session); Equal((long)log.OriginalBytes.Length, exclusive.Snapshot.CommittedByteLength); return log;
    }
    private static SessionContextProjection Context(SessionLogReadResult log) => new SessionContextProjector().ProjectLatest(log.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray());
    private static JsonElement LastAssistant(SessionContextProjection context) => context.LlmMessages.Last(entry => entry.WireBody.Value.GetProperty("role").GetString() == "assistant").WireBody.Value;
    private static string TextOf(JsonElement message) => message.GetProperty("content").ValueKind == JsonValueKind.Array ? string.Join('\n', message.GetProperty("content").EnumerateArray().Select(block => block.TryGetProperty("text", out var text) ? text.GetString() : "")) : "";
    private static void Markers(Files files, int runs)
    { foreach (var value in new[] { "module", "constructor", "initialize", "dispose" }) Equal(runs, files.MarkerLines.Count(line => line == value)); Equal("dispose", files.MarkerLines[^1]); Equal(0, Directory.GetDirectories(files.Snapshots).Length); }
    private sealed record Result(int ExitCode, string Output, string Error);
    private static ProcessStartInfo Start(Files files, string[] arguments)
    {
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Root, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 };
        if (files.Plugin) start.Environment["PISHARP_NATIVE_FIXTURE_MARKERS"] = files.Markers;
        start.ArgumentList.Add(cli); foreach (var argument in arguments) start.ArgumentList.Add(argument); return start;
    }
    private static async Task<Result> OneShot(Files files, string[] args)
    {
        using var process = Process.Start(Start(files, args)) ?? throw new InvalidOperationException("Native child did not start."); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        process.StandardInput.Close(); var output = ReadText(process.StandardOutput, deadline.Token); var error = ReadText(process.StandardError, deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); return new(process.ExitCode, await output, await error); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } foreach (var task in new[] { output, error }) try { await task; } catch (Exception) { } }
    }
    private static async Task<string> ReadText(TextReader input, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[4096];
        while (true) { var count = await input.ReadAsync(buffer.AsMemory(), token); if (count == 0) return result.ToString(); Check(count <= 2_097_152 - result.Length, "Authored receipt exceeded bound."); result.Append(buffer, 0, count); }
    }
    private sealed class Child : IAsyncDisposable
    {
        private readonly Process process; private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15)), outputCancellation = new(); private readonly object gate = new();
        private readonly List<string> lines = []; private TaskCompletionSource changed = NewSignal(); private readonly Task reading; private readonly Task<string> errors; private bool ended, inputClosed;
        public string[] Lines { get { lock (gate) return lines.ToArray(); } }
        public Child(Files files, string api, params string[] extra)
        { process = Process.Start(Start(files, ChatArgs(files, api, extra))) ?? throw new InvalidOperationException("Compiled chat did not start."); errors = ReadText(process.StandardError, deadline.Token); reading = Read(); }
        public async Task Send(string line) { await process.StandardInput.WriteAsync((line + "\n").AsMemory(), deadline.Token); await process.StandardInput.FlushAsync(deadline.Token); }
        public async Task Ready() { await Wait("[state] idle"); await Wait("[history]"); }
        public async Task<string> Wait(string prefix, int occurrence = 1)
        {
            while (true)
            {
                Task next;
                lock (gate) { var matches = lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray(); if (matches.Length >= occurrence) return matches[occurrence - 1]; Check(!ended, "Chat ended before " + prefix + "; output=" + Bounded(string.Join('\n', lines))); next = changed.Task; }
                await next.WaitAsync(deadline.Token);
            }
        }
        private async Task Read()
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, outputCancellation.Token); var line = new StringBuilder(); var buffer = new char[4096]; long characters = 0;
            try
            {
                while (true)
                {
                    var count = await process.StandardOutput.ReadAsync(buffer.AsMemory(), linked.Token); if (count == 0) { Check(line.Length == 0, "Chat output ended without LF."); return; }
                    characters += count; Check(characters <= 2_097_152, "Chat output receipt cap exceeded.");
                    for (var index = 0; index < count; index++)
                    {
                        if (buffer[index] != '\n') { line.Append(buffer[index]); continue; }
                        TaskCompletionSource signal; lock (gate) { Check(lines.Count < 4096, "Chat line count exceeded."); lines.Add(line.ToString()); line.Clear(); signal = changed; changed = NewSignal(); } signal.TrySetResult();
                    }
                }
            }
            finally { lock (gate) { ended = true; changed.TrySetResult(); } }
        }
        public void BreakOutput() { outputCancellation.Cancel(); process.StandardOutput.BaseStream.Dispose(); }
        public async Task<Result> Finish(bool allowReadFailure = false)
        {
            if (!inputClosed) { inputClosed = true; process.StandardInput.Close(); } await process.WaitForExitAsync(deadline.Token);
            try { await reading; } catch (Exception) when (allowReadFailure) { }
            return new(process.ExitCode, string.Join('\n', Lines), await errors);
        }
        public async ValueTask DisposeAsync()
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } outputCancellation.Cancel(); try { await reading; } catch (Exception) { } try { await errors; } catch (Exception) { } }
            finally { process.Dispose(); deadline.Dispose(); outputCancellation.Dispose(); }
        }
    }
    private sealed class ControlledReader : TextReader
    {
        private readonly Channel<string> values = Channel.CreateBounded<string>(8); private string? current; private int position, active;
        public bool CanceledRead { get; private set; } public bool Disposed { get; private set; }
        public int ActiveReads => Volatile.Read(ref active);
        public Task Send(string value, CancellationToken token) => values.Writer.WriteAsync(value, token).AsTask(); public void Complete() => values.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken token = default)
        {
            Interlocked.Increment(ref active);
            try
            {
                while (current is null && await values.Reader.WaitToReadAsync(token)) if (values.Reader.TryRead(out current)) position = 0;
                token.ThrowIfCancellationRequested(); if (current is null) return 0;
                var count = Math.Min(buffer.Length, current.Length - position); current.AsMemory(position, count).CopyTo(buffer); position += count; if (position == current.Length) current = null; return count;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { CanceledRead = true; throw; }
            finally { Interlocked.Decrement(ref active); }
        }
        protected override void Dispose(bool disposing) { Disposed = true; Complete(); base.Dispose(disposing); }
    }
    private sealed class ControlledView : TextWriter
    {
        private readonly object gate = new(); private readonly StringBuilder text = new(); private TaskCompletionSource changed = NewSignal(); private bool pendingFault, pendingHold; private int active;
        public override Encoding Encoding => Utf8; public string? Fault { get; set; } public int FailedOperations { get; private set; } public bool Disposed { get; private set; }
        public bool HoldStateFlush { get; set; } public TaskCompletionSource FlushEntered { get; } = NewSignal(); public TaskCompletionSource ReleaseFlush { get; } = NewSignal(); public int ActiveOperations => Volatile.Read(ref active);
        public string Text { get { lock (gate) return text.ToString(); } }
        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken token = default)
        {
            Interlocked.Increment(ref active);
            try
            {
            token.ThrowIfCancellationRequested(); var value = buffer.ToString();
            if (HoldStateFlush && value.StartsWith("[state] running", StringComparison.Ordinal)) { pendingHold = true; HoldStateFlush = false; }
            if (Fault is not null && value.StartsWith("[state] running", StringComparison.Ordinal))
            { if (Fault == "write") { FailedOperations++; throw new IOException("Authored write fault."); } pendingFault = true; }
            TaskCompletionSource signal; lock (gate) { Check(buffer.Length <= 2_097_152 - text.Length, "Direct view receipt cap exceeded."); text.Append(value); signal = changed; changed = NewSignal(); } signal.TrySetResult(); return Task.CompletedTask;
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public override async Task FlushAsync(CancellationToken token)
        {
            Interlocked.Increment(ref active);
            try
            {
                token.ThrowIfCancellationRequested();
                if (pendingHold) { pendingHold = false; FlushEntered.TrySetResult(); await ReleaseFlush.Task; token.ThrowIfCancellationRequested(); }
                if (pendingFault) { FailedOperations++; pendingFault = false; throw new IOException("Authored flush fault."); }
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public async Task Wait(string value, CancellationToken token) { while (true) { Task next; lock (gate) { if (text.ToString().Contains(value, StringComparison.Ordinal)) return; next = changed.Task; } await next.WaitAsync(token); } }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class Files : IDisposable
    {
        private readonly string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-interactive-" + Guid.NewGuid().ToString("N")); public bool Plugin { get; private init; }
        public string In(string name) => Path.Combine(Root, name); public string Session => In("session.jsonl"); public string Script => In("script.json"); public string Source => In("source.txt"); public string Target => In("effect.txt");
        public string Package => In("published"); public string Manifest => In("manifest.json"); public string Approval => In("approval.json"); public string Snapshots => In("snapshots"); public string Markers => In("markers");
        public string[] MarkerLines => File.Exists(Path.Combine(Markers, "PublishedFixture.CliUi.markers")) ? File.ReadAllLines(Path.Combine(Markers, "PublishedFixture.CliUi.markers")) : [];
        public string[] ExtensionArgs => Plugin ? ["--extension-package", Package, "--extension-manifest", Manifest, "--extension-approval", Approval, "--extension-snapshot-root", Snapshots, "--enable-extension-tool", "fixture.cli.ui"] : [];
        public static async Task<Files> CreateAsync(bool plugin = false)
        {
            var files = new Files { Plugin = plugin };
            try
            {
                Directory.CreateDirectory(files.Root); if (!plugin) return files;
                Check(File.Exists(Path.Combine(published, "PublishedFixture.CliUi.dll")), "Root must publish the existing real native UI fixture.");
                foreach (var directory in new[] { files.Package, files.Snapshots, files.Markers }) Directory.CreateDirectory(directory);
                foreach (var source in Directory.GetFiles(published, "*", SearchOption.AllDirectories))
                { var target = Path.Combine(files.Package, Path.GetRelativePath(published, source)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target); }
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var source in Directory.GetFiles(files.Package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) hashes.Add(Path.GetRelativePath(files.Package, source).Replace('\\', '/'), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(source))));
                var manifest = JsonSerializer.Serialize(new { schemaVersion = 0, id = "fixture.cli.ui", packageVersion = "0.0.1", hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native",
                    assembly = "PublishedFixture.CliUi.dll", entryType = "PublishedCliUiFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" }, requiredFeatures = new[] { "owned-descriptor-callbacks", "registered-input-tool-reducers" },
                    declaredCapabilities = new[] { "tools", "observations" }, resourcePaths = hashes.Keys.Where(path => path != "PublishedFixture.CliUi.dll").ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
                await File.WriteAllTextAsync(files.Manifest, manifest, Utf8);
                await File.WriteAllTextAsync(files.Approval, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution", packageRoot = files.Package,
                    manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(manifest))), artifactHashes = hashes, sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit",
                    policyRevision = "experimental-policy-0", hostGeneration = 1, sessionPath = files.Session, workspace = files.Root, snapshotRoot = files.Snapshots, enabledTools = new[] { "fixture.cli.ui" } }), Utf8);
                return files;
            }
            catch { files.Dispose(); throw; }
        }
        public void Dispose()
        {
            var target = Path.GetFullPath(Root); if (Path.GetDirectoryName(target) != parent || !Path.GetFileName(target).StartsWith("pisharp-interactive-", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing unowned test cleanup.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Clean(Result result) => Check(result.ExitCode == 0 && result.Error.Length == 0, "Chat child exit=" + result.ExitCode + " stderr=" + Bounded(result.Error) + " output=" + Bounded(result.Output));
    private static void Bytes(byte[] expected, byte[] actual) => Check(expected.SequenceEqual(actual), "Exact bytes differ.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Expected=" + Bounded(expected?.ToString()) + " actual=" + Bounded(actual?.ToString()));
    private static string Bounded(string? value) => JsonSerializer.Serialize(value is null ? "<null>" : value.Length <= 1024 ? value : value[..1024] + "[truncated]");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
