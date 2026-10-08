using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Commands;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Sessions.Storage;

internal static class OfflineBashCommandTests
{
    private const string Bash = @"C:\Program Files\Git\usr\bin\bash.exe";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static string _host = "", _cli = "";

    public static (string Name, Func<Task> Run)[] Cases(string dotnetHost, string cliDll)
    {
        if (!Path.IsPathFullyQualified(dotnetHost) || !File.Exists(dotnetHost) ||
            !Path.IsPathFullyQualified(cliDll) || !File.Exists(cliDll)) throw new ArgumentException("Compiled CLI paths required.");
        _host = dotnetHost; _cli = cliDll;
        return
        [
            ("Bash CLI explicit bounded admission precedes durable acquisition", Admission),
            ("Bash CLI both APIs actual effects exact action durable next request reopen and branch", EffectsAndBranches),
            ("Bash CLI exact command and timeout denial cannot execute rejected effects", PolicyDenials),
            ("Bash RPC both APIs large control Unicode NUL head-tail progress spill durable reopen", LargeRpcOutput),
            ("Bash source-default RPC held progress preserves large output spill durable cleanup", HeldLargeRpcOutput),
            ("Bash RPC actual progress abort joins process and durable cleanup", RpcCancellation),
            ("Bash RPC completed effect final output write-flush faults stop continuation and await cleanup", OutputFaults)
        ];
    }

    private static void RequireBash() => Check(OperatingSystem.IsWindows() && File.Exists(Bash),
        "Actual Bash command fixtures require the explicitly configured existing Windows Git Bash; no fallback or installation.");

    private static async Task Admission()
    {
        using var files = new Files();
        var baseArgs = new[] { "session", "create", "--session", files.Session, "--workspace", files.Root };
        foreach (var extra in new[]
        {
            new[] { "--bash-executable", Bash },
            new[] { "--bash-spill-root", files.Root },
            new[] { "--allow-bash-command", "printf allowed" },
            BashArgs(files, "printf allowed", timeout: "0"),
            BashArgs(files, "printf allowed", timeout: "NaN"),
            BashArgs(files, "printf allowed", timeout: "2147483.648"),
            BashArgs(files, new string('x', 12_001)),
            BashArgs(files, "bad\0command"),
            BashArgs(files, "\ud800"),
            BashArgs(files, "printf allowed").Concat(["--allow-bash-command", "printf allowed"]).ToArray()
        })
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            var code = await SessionCommands.RunAsync(baseArgs.Concat(extra).ToArray(), output, error);
            Equal(2, code); Equal("", output.ToString());
            Equal(OperatingSystem.IsWindows() ? "InvalidBashConfiguration" : "UnsupportedBashPlatform",
                JsonData.Parse(error.ToString()).Value.GetProperty("code").GetString());
            Check(!File.Exists(files.Session), "Rejected Bash configuration admitted a durable session.");
            Check(!error.ToString().Contains("bad", StringComparison.Ordinal), "Rejected command leaked into a diagnostic.");
        }
        if (!OperatingSystem.IsWindows()) return;
        RequireBash();
        foreach (var extra in new[]
        {
            new[] { "--bash-executable", files.In("missing.exe"), "--bash-spill-root", files.Root, "--allow-bash-command", "printf allowed" },
            new[] { "--bash-executable", Bash, "--bash-spill-root", Path.GetDirectoryName(files.Root)!, "--allow-bash-command", "printf allowed" }
        })
        {
            var failed = await OneShot(files, baseArgs.Concat(extra).ToArray());
            Equal(2, failed.Code); Equal("InvalidBashConfiguration", JsonData.Parse(failed.Error).Value.GetProperty("code").GetString());
            Check(!File.Exists(files.Session), "Invalid executable/spill identity acquired storage.");
        }
        var plain = Success(await OneShot(files, baseArgs));
        Check(plain.GetProperty("actions").GetArrayLength() == 0, "Default profile acquired Bash authority.");
        var before = await File.ReadAllBytesAsync(files.Session);
        await Script(files.Script, Text("openai-responses", "unused"));
        var rejected = await OneShot(files, PromptArgs(files, "openai-responses", "rejected", "printf allowed")
            .Concat(["--bash-timeout", "Infinity"]).ToArray());
        Equal(2, rejected.Code);
        var after = await File.ReadAllBytesAsync(files.Session);
        Check(before.SequenceEqual(after), "Rejected reopen mutated existing durable bytes.");
    }

    private static async Task EffectsAndBranches()
    {
        RequireBash();
        const string command = "IFS= read -r line < source.txt; printf '%s\\n' \"$line\"; " +
            "printf 'saved π🙂\\n' > target.txt; printf 'visible\\000output\\n'; " +
            "printf 'secret=%s\\n' \"$" + "{OPENAI_API_KEY-unset}\"";
        foreach (var api in new[] { "openai-responses", "anthropic-messages" })
        {
            using var files = new Files();
            await File.WriteAllTextAsync(files.In("source.txt"), "source seed\n", Utf8);
            var created = Success(await OneShot(files, CreateArgs(files, api, command, "10")));
            Equal(0, created.GetProperty("actions").GetArrayLength());
            var initial = await File.ReadAllBytesAsync(files.Session);
            await Script(files.Script, Tool(api, "bash-first", new { command, timeout = 10 }),
                Text(api, "Bash completed", ["source seed", "visible\0output", "secret=unset"]));
            var first = Success(await OneShot(files, PromptArgs(files, api, "first Bash user", command, "10")));
            Equal("saved π🙂\n", await File.ReadAllTextAsync(files.In("target.txt"), Utf8));
            var actions = first.GetProperty("actions"); Equal(1, actions.GetArrayLength());
            var action = actions[0]; Check(action.GetProperty("allowed").GetBoolean(), "Qualified action denied.");
            Equal(Bash, action.GetProperty("Target").GetString()); Equal(files.Root, action.GetProperty("WorkingDirectory").GetString());
            Check(action.GetProperty("CommandArguments").EnumerateArray().Select(item => item.GetString()).SequenceEqual(["-c", command]),
                "Authorized argv did not preserve the exact command.");
            Equal(command, action.GetProperty("arguments").GetProperty("command").GetString());
            Equal(10, action.GetProperty("arguments").GetProperty("timeout").GetInt32());
            CheckEnvironment(action.GetProperty("Environment"), action.GetProperty("arguments").GetProperty("outputPath").GetString()!, files);
            foreach (var request in first.GetProperty("requests").EnumerateArray())
            {
                Equal(api, request.GetProperty("api").GetString());
                Check(request.GetProperty("toolNames").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["read", "write", "bash"]),
                    "Actual request lost ordered Bash declaration.");
            }
            Equal(3, first.GetProperty("requests")[1].GetProperty("requiredHistoryChecks").GetInt32());
            var firstBytes = await File.ReadAllBytesAsync(files.Session);
            Check(firstBytes.AsSpan().StartsWith(initial), "Bash prompt rewrote acknowledged prefix.");
            Equal((long)firstBytes.Length, first.GetProperty("committedByteLength").GetInt64());
            Check(first.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "Success preceded durable acknowledgment.");
            var log = await new SessionLogReader().ReadFileAsync(files.Session);
            var result = Messages(log).Single(message => message.GetProperty("role").GetString() == "toolResult");
            Check(ResultText(result).Contains("visible\0output", StringComparison.Ordinal), "Canonical output lost decoded NUL.");
            Check(!result.TryGetProperty("structuredContent", out _), "Programmatic output entered durable model history.");
            var ancestor = first.GetProperty("selectedLeafId").GetString()!;
            await Script(files.Script, Text(api, "Bash resumed", ["first Bash user", "Bash completed", "source seed", "resumed user"]));
            var resumedArgs = PromptArgs(files, api, "resumed user", command, "10"); resumedArgs[1] = "resume";
            var resumed = Success(await OneShot(files, resumedArgs));
            Equal(ancestor, resumed.GetProperty("previousSelectedLeafId").GetString());
            var sibling = await new SessionLogReader().ReadFileAsync(files.Session);
            await Script(files.Script, Text(api, "Bash branch", ["first Bash user", "Bash completed", "branch user"]));
            var branchArgs = PromptArgs(files, api, "branch user", command, "10").Concat(["--leaf", ancestor]).ToArray(); branchArgs[1] = "resume";
            var branch = Success(await OneShot(files, branchArgs));
            Equal(ancestor, branch.GetProperty("previousSelectedLeafId").GetString());
            var final = await new SessionLogReader().ReadFileAsync(files.Session);
            Check(final.OriginalBytes.AsSpan().StartsWith(sibling.OriginalBytes.AsSpan()), "Selected branch rewrote sibling source.");
            Equal(ancestor, final.ValidatedPrefix[sibling.ValidatedPrefix.Length].Entry.ParentId);
            var inspect = Success(await OneShot(files, "session", "inspect", "--session", files.Session));
            Check(!inspect.GetProperty("llmMessages").GetRawText().Contains("resumed user", StringComparison.Ordinal),
                "Selected Bash branch flattened sibling history.");
            await using var lease = await SessionLogStore.OpenAsync(files.Session);
            Equal((long)final.OriginalBytes.Length, lease.Snapshot.CommittedByteLength);
        }
    }

    private static async Task PolicyDenials()
    {
        RequireBash();
        const string allowed = "printf allowed";
        foreach (var api in new[] { "openai-responses", "anthropic-messages" })
        foreach (var input in new object[]
        {
            new { command = "printf denied > forbidden.txt" },
            new { command = allowed, timeout = 1 },
            new { command = allowed, unexpected = true }
        })
        {
            using var files = new Files();
            Success(await OneShot(files, CreateArgs(files, api, allowed)));
            await Script(files.Script, Tool(api, "denied", input), Text(api, "denial observed"));
            var result = await OneShot(files, PromptArgs(files, api, "deny", allowed));
            Equal(1, result.Code); Equal("", result.Error);
            var report = JsonData.Parse(result.Output).Value;
            Check(report.GetProperty("toolErrors").GetBoolean(), "Rejected final action appeared successful.");
            Check(!File.Exists(files.In("forbidden.txt")), "Rejected command acquired process effect authority.");
            foreach (var action in report.GetProperty("actions").EnumerateArray())
                Check(!action.GetProperty("allowed").GetBoolean(), "Exact command/timeout policy accepted a changed final action.");
            var log = await new SessionLogReader().ReadFileAsync(files.Session);
            Check(Messages(log).Any(message => message.GetProperty("role").GetString() == "toolResult" && message.GetProperty("isError").GetBoolean()),
                "Rejected tool result was not durably finalized.");
        }
        using var timeoutFiles = new Files();
        Success(await OneShot(timeoutFiles, CreateArgs(timeoutFiles, "openai-responses", allowed, "2")));
        await Script(timeoutFiles.Script, Tool("openai-responses", "timeout-mismatch", new { command = allowed, timeout = 1 }),
            Text("openai-responses", "timeout denied"));
        var mismatch = await OneShot(timeoutFiles, PromptArgs(timeoutFiles, "openai-responses", "deny timeout", allowed, "2"));
        Equal(1, mismatch.Code);
        Check(!JsonData.Parse(mismatch.Output).Value.GetProperty("actions")[0].GetProperty("allowed").GetBoolean(),
            "Configured final timeout changed after authorization.");
    }

    private static async Task LargeRpcOutput()
    {
        RequireBash();
        const string head = "head π🙂\0\t\u0001\n", tail = "tail π🙂\0\t\u0002\n";
        const string line = "\0\u0001\u0002\u0003\u0004\u0005\u0006\u0007\b\v\f\u000eabcdefghijklmnopqrstuvwx\n";
        const int count = 40000;
        const string command = "printf 'head π🙂\\000\\t\\001\\n'; for ((i=0;i<40000;i++)); do " +
            "printf '\\000\\001\\002\\003\\004\\005\\006\\007\\010\\013\\014\\016abcdefghijklmnopqrstuvwx\\n'; done; printf 'tail π🙂\\000\\t\\002\\n'";
        foreach (var api in new[] { "openai-responses", "anthropic-messages" })
        {
            using var files = new Files();
            Success(await OneShot(files, CreateArgs(files, api, command)));
            var initial = await File.ReadAllBytesAsync(files.Session);
            await Script(files.Script, Tool(api, "large", new { command }), Text(api, "large complete", [tail]));
            JsonData ended; JsonElement entries;
            await using (var child = new RpcChild(files, RpcArgs(files, api, command)))
            {
                await child.Send(new { id = "p", type = "prompt", message = "large output user" });
                Response(await child.Response("p"), "prompt");
                ended = await child.Wait(record => Type(record) == "tool_execution_end");
                Check(!ended.Value.GetProperty("isError").GetBoolean(), "Large NUL tail failed output-details admission.");
                var result = ended.Value.GetProperty("result"); var structured = result.GetProperty("structuredContent");
                var output = structured.GetProperty("output").GetString()!;
                Check(output.StartsWith(head, StringComparison.Ordinal) && output.EndsWith(tail, StringComparison.Ordinal),
                    "Structured output discarded, replaced or reordered raw decoded control/Unicode head-tail.");
                Check(structured.GetProperty("truncated").GetBoolean(), "Raw output over1MiB failed structured truncation.");
                Check(Encoding.UTF8.GetByteCount(ended.ToString()) > 1_048_576, "Control did not exercise larger composed public frame.");
                var artifact = structured.GetProperty("full_output_path").GetString()!;
                CheckArtifact(artifact, files);
                var expected = Utf8.GetBytes(head + string.Concat(Enumerable.Repeat(line, count)) + tail);
                var raw = await File.ReadAllBytesAsync(artifact);
                Check(raw.Length > 1_048_576 && expected.SequenceEqual(raw), "CreateNew raw spill lost exact bytes.");
                // Pinned truncateTail splits on LF and rejoins admitted complete lines. A truncated
                // final line therefore loses its structural terminating LF, not its NUL/control content.
                // Derive the exact source byte-budget selection independently of the native truncator.
                var finalLine = tail[..^1];
                var retainedRows = (50 * 1024 - Utf8.GetByteCount(finalLine)) / Utf8.GetByteCount(line);
                var expectedTail = string.Concat(Enumerable.Repeat(line, retainedRows)) + finalLine;
                var expectedLines = retainedRows + 1; var totalLines = count + 2;
                var truncation = result.GetProperty("details").GetProperty("truncation");
                Equal(expectedTail, truncation.GetProperty("content").GetString());
                Equal("bytes", truncation.GetProperty("truncatedBy").GetString());
                Equal(expectedLines, truncation.GetProperty("outputLines").GetInt32());
                Equal(totalLines, truncation.GetProperty("totalLines").GetInt32());
                Equal(Utf8.GetByteCount(expectedTail), truncation.GetProperty("outputBytes").GetInt32());
                var expectedModel = expectedTail + "\n\n[Showing lines " + (totalLines - expectedLines + 1) +
                    "-" + totalLines + " of " + totalLines + " (50.0KB limit). Full output: " + artifact + "]";
                Equal(expectedModel, ResultText(result));
                await child.Wait(record => Type(record) == "agent_settled");
                Check(child.Records.Any(record => Type(record) == "tool_execution_update" &&
                    record.Value.GetProperty("partialResult").GetProperty("content").EnumerateArray().Any()),
                    "Actual process progress bypassed the awaited public event path.");
                await child.Send(new { id = "entries", type = "get_entries" });
                entries = Response(await child.Response("entries"), "get_entries").GetProperty("data").Clone();
                SessionLogReadResult acknowledged;
                await using (var source = new FileStream(files.Session, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    acknowledged = await new SessionLogReader().ReadAsync(source);
                Check(acknowledged.SourceComplete && acknowledged.Status == SessionLogReadStatus.Complete &&
                    acknowledged.OriginalBytes.Length == new FileInfo(files.Session).Length,
                    "Live acknowledged inspection did not capture complete physical durable bytes.");
                Equal(acknowledged.ValidatedPrefix.Length - 1, entries.GetProperty("entries").GetArrayLength());
                Check(Messages(acknowledged).Any(message => message.GetProperty("role").GetString() == "assistant" &&
                    ResultText(message) == "large complete"), "Subsequent actual request did not observe durably committed output.");
                Clean(await child.Finish());
            }
            var final = await new SessionLogReader().ReadFileAsync(files.Session);
            Check(final.SourceComplete && final.Status == SessionLogReadStatus.Complete &&
                final.OriginalBytes.AsSpan().StartsWith(initial), "Large event path damaged durable source.");
            Equal(entries.GetProperty("leafId").GetString(), final.ValidatedPrefix[^1].Entry.Id);
            Check(Messages(final).All(message => !message.TryGetProperty("structuredContent", out _)),
                "Large programmatic result leaked into model/durable messages.");
            await using (var lease = await SessionLogStore.OpenAsync(files.Session))
                Equal((long)final.OriginalBytes.Length, lease.Snapshot.CommittedByteLength);
            await Script(files.Script, Text(api, "large reopened", ["large output user", "large complete", tail, "reopened user"]));
            var args = PromptArgs(files, api, "reopened user", command); args[1] = "resume";
            var reopened = Success(await OneShot(files, args));
            Equal(4, reopened.GetProperty("requests")[0].GetProperty("requiredHistoryChecks").GetInt32());
        }
    }

    private static async Task HeldLargeRpcOutput()
    {
        RequireBash();
        const string line = "0123456789abcdefghijklmnopλ🙂\0\u0001\n";
        const int count = 40000;
        const string command = "printf 'E' >> effects.txt; for ((i=0;i<40000;i++)); do printf '0123456789abcdefghijklmnopλ🙂\\000\\001\\n'; done; printf 'done\\n'";
        var expected = Utf8.GetBytes(string.Concat(Enumerable.Repeat(line, count)) + "done\n");
        Check(expected.Length > 1_048_576, "Held delivery fixture must exceed the raw structured output head budget.");
        foreach (var api in new[] { "openai-responses", "anthropic-messages" })
        {
            using var files = new Files();
            Success(await OneShot(files, CreateArgs(files, api, command)));
            var prefix = await File.ReadAllBytesAsync(files.Session);
            await Script(files.Script, Tool(api, "held-large", new { command }), Text(api, "held complete", ["done"]));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var input = new HostInput(); using var output = new HeldProgressOutput(input);
            using var errors = new StringWriter();
            var host = RpcSessionCommand.RunAsync(RpcArgs(files, api, command), input, output, errors, deadline.Token);
            try
            {
                input.Send(new { id = "p", type = "prompt", message = "owned slow output fixture" });
                await output.Entered.Task.WaitAsync(deadline.Token);
                Check(!host.IsCompleted, "Host bypassed held actual output delivery.");
                Equal("E", await File.ReadAllTextAsync(files.In("effects.txt"), Utf8));
                output.Release();
                Equal(0, await host.WaitAsync(deadline.Token)); Equal("", errors.ToString());
                var ended = output.Rows.Single(row => Type(row) == "tool_execution_end").Value;
                Check(!ended.GetProperty("isError").GetBoolean(), "Slow output aborted an admitted large Bash command.");
                var result = ended.GetProperty("result"); var structured = result.GetProperty("structuredContent");
                Check(structured.GetProperty("output").GetString()!.EndsWith("done\n", StringComparison.Ordinal),
                    "Completed structured output lost the final raw output.");
                var artifact = structured.GetProperty("full_output_path").GetString()!;
                CheckArtifact(artifact, files);
                var bytes = await File.ReadAllBytesAsync(artifact);
                Check(expected.SequenceEqual(bytes), "Held delivery damaged exact Unicode/NUL raw spill bytes.");
                Equal("E", await File.ReadAllTextAsync(files.In("effects.txt"), Utf8));
                Check(!input.WasDisposed && !output.WasDisposed, "Host disposed borrowed streams.");
                var read = await new SessionLogReader().ReadFileAsync(files.Session);
                Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete && read.OriginalBytes.AsSpan().StartsWith(prefix),
                    "Slow output completion failed durable prefix/settlement.");
                Check(Messages(read).Any(message => message.GetProperty("role").GetString() == "assistant" && ResultText(message) == "held complete"),
                    "Slow output omitted provider continuation/durable acknowledgement.");
                await using var lease = await SessionLogStore.OpenAsync(files.Session);
                Equal((long)read.OriginalBytes.Length, lease.Snapshot.CommittedByteLength);
            }
            finally
            {
                output.Release(); input.Complete(); deadline.Cancel();
                try { await host.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            }
        }
    }

    private static async Task RpcCancellation()
    {
        RequireBash();
        const string command = "printf 'actual-bash-running\\n'; while :; do :; done";
        foreach (var api in new[] { "openai-responses", "anthropic-messages" })
        {
            using var files = new Files();
            Phase(api, "create");
            Success(await OneShot(files, CreateArgs(files, api, command)));
            await Script(files.Script, Tool(api, "cancel", new { command }), Text(api, "must not run canceled continuation"));
            await using (var child = new RpcChild(files, RpcArgs(files, api, command), TimeSpan.FromSeconds(12)))
            {
                Phase(api, "prompt-send");
                await child.Send(new { id = "p", type = "prompt", message = "cancel real Bash" });
                Response(await Stage(child.Response("p"), api, "prompt-accepted"), "prompt");
                await Stage(child.Wait(record => Type(record) == "tool_execution_update" &&
                    ResultText(record.Value.GetProperty("partialResult")).Contains("actual-bash-running", StringComparison.Ordinal)),
                    api, "actual-progress");
                Phase(api, "abort-send");
                await child.Send(new { id = "abort", type = "abort" });
                Response(await Stage(child.Response("abort"), api, "abort-acknowledged"), "abort");
                await Stage(child.Wait(record => Type(record) == "agent_settled"), api, "agent-settled");
                var ended = child.Records.Single(record => Type(record) == "tool_execution_end").Value;
                Check(ended.GetProperty("isError").GetBoolean() && ResultText(ended.GetProperty("result")).Contains("actual-bash-running", StringComparison.Ordinal),
                    "Cancellation did not retain actual completed output through process cleanup.");
                Check(ResultText(ended.GetProperty("result")).Contains("Command aborted", StringComparison.Ordinal),
                    "Cancellation was reclassified as normal exit/timeout.");
                Check(!ended.GetProperty("result").TryGetProperty("structuredContent", out _), "Canceled work gained normal programmatic completion.");
                Check(!child.Records.Any(record => record.ToString().Contains("must not run canceled continuation", StringComparison.Ordinal)),
                    "Canceled work started another provider continuation.");
                Clean(await Stage(child.Finish(), api, "EOF-closed"));
            }
            Phase(api, "durable-read");
            var read = await new SessionLogReader().ReadFileAsync(files.Session);
            Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete, "Cancel returned before durable log cleanup.");
            Check(Messages(read).Any(message => message.GetProperty("role").GetString() == "toolResult" &&
                message.GetProperty("isError").GetBoolean() && ResultText(message).Contains("actual-bash-running", StringComparison.Ordinal)),
                "Canceled tool output was not durably acknowledged.");
            await using var lease = await SessionLogStore.OpenAsync(files.Session);
            Equal((long)read.OriginalBytes.Length, lease.Snapshot.CommittedByteLength);
            Phase(api, "exclusive-lease");
        }
    }
    private static void Phase(string api, string phase) => Console.WriteLine("offline-bash-cancel " + api + ": " + phase);
    private static async Task<T> Stage<T>(Task<T> work, string api, string phase)
    {
        Phase(api, phase + "-wait");
        try { var result = await work.WaitAsync(TimeSpan.FromSeconds(6)); Phase(api, phase); return result; }
        catch (TimeoutException) { throw new InvalidOperationException("Offline Bash cancellation phase timed out: " + api + "/" + phase); }
    }

    private static async Task OutputFaults()
    {
        RequireBash();
        const string command = "if [[ -f completed.txt ]]; then printf 'late effect\\n' > late.txt; " +
            "else printf 'completed effect\\n' > completed.txt; fi; printf 'known output\\n'";
        foreach (var flush in new[] { false, true })
        {
            using var files = new Files();
            Success(await OneShot(files, CreateArgs(files, "openai-responses", command)));
            await Script(files.Script, Tool("openai-responses", "completed", new { command }),
                Tool("openai-responses", "must-not-acquire", new { command }));
            var before = await File.ReadAllBytesAsync(files.Session);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var input = new HostInput(); using var output = new FaultOutput(flush); using var errors = new StringWriter();
            var host = RpcSessionCommand.RunAsync(RpcArgs(files, "openai-responses", command), input, output, errors, deadline.Token);
            try
            {
                input.Send(new { id = "p", type = "prompt", message = "completed effect before output fault" });
                var pending = await output.Entered.Task.WaitAsync(deadline.Token);
                Check(ResultText(pending.Value.GetProperty("result")).Contains("known output", StringComparison.Ordinal),
                    "Fault control did not reach a real completed effect result.");
                Equal("completed effect\n", await File.ReadAllTextAsync(files.In("completed.txt"), Utf8));
                Check(!host.IsCompleted, "Final output fault gate failed to hold actual delivery.");
                output.Fail(); Equal(1, await host.WaitAsync(deadline.Token));
                Equal("RpcHostFailed", JsonData.Parse(errors.ToString()).Value.GetProperty("code").GetString());
                Check(!errors.ToString().Contains("private failing sink", StringComparison.Ordinal), "Sink exception leaked into diagnostic.");
                Check(!File.Exists(files.In("late.txt")), "Final write/flush failure acquired a subsequent scripted tool.");
                Check(!input.WasDisposed && !output.WasDisposed, "Host disposed borrowed stdio.");
                var read = await new SessionLogReader().ReadFileAsync(files.Session);
                Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete &&
                    read.OriginalBytes.AsSpan().StartsWith(before), "Fault cleanup damaged acknowledged source.");
                await using var lease = await SessionLogStore.OpenAsync(files.Session);
                Equal((long)read.OriginalBytes.Length, lease.Snapshot.CommittedByteLength);
            }
            finally
            {
                output.Fail(); input.Complete(); deadline.Cancel();
                try { await host.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            }
        }
    }

    private static string[] BashArgs(Files files, string command, string? timeout = null)
    {
        var args = new[] { "--bash-executable", Bash, "--bash-spill-root", files.Root, "--allow-bash-command", command };
        return timeout is null ? args : args.Concat(["--bash-timeout", timeout]).ToArray();
    }
    private static string[] CreateArgs(Files files, string api, string command, string? timeout = null) =>
        new[] { "session", "create", "--session", files.Session, "--workspace", files.Root, "--offline-api", api }.Concat(BashArgs(files, command, timeout)).ToArray();
    private static string[] PromptArgs(Files files, string api, string message, string command, string? timeout = null) =>
        new[] { "session", "prompt", "--session", files.Session, "--workspace", files.Root, "--offline-script", files.Script,
            "--offline-api", api, "--message", message }.Concat(BashArgs(files, command, timeout)).ToArray();
    private static string[] RpcArgs(Files files, string api, string command) =>
        new[] { "session", "rpc", "--session", files.Session, "--workspace", files.Root,
            "--offline-script", files.Script, "--offline-api", api }.Concat(BashArgs(files, command)).ToArray();
    private static async Task Script(string path, params object[] turns) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { schemaVersion = 1, turns }), Utf8);
    private static object Tool(string api, string call, object arguments) => api == "anthropic-messages"
        ? SessionCommandTests.AnthropicTool("bash", call, arguments) : ResponsesTool(call, arguments);
    private static object Text(string api, string text, string[]? required = null) => api == "anthropic-messages"
        ? SessionCommandTests.AnthropicText(text, required: required) : ResponsesText(text, required);
    private static object ResponsesTool(string call, object arguments)
    {
        var json = JsonSerializer.Serialize(arguments);
        return new { requiredInputTexts = Array.Empty<string>(), events = new object[]
        {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id = "fc_" + call,
                call_id = call, name = "bash", arguments = "" } },
            new { type = "response.function_call_arguments.delta", output_index = 0, delta = json },
            new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", id = "fc_" + call,
                call_id = call, name = "bash", arguments = json } }, Completed()
        } };
    }
    private static object ResponsesText(string text, string[]? required) => new { requiredInputTexts = required ?? [], events = new object[]
    {
        new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "msg_text", role = "assistant", content = Array.Empty<object>() } },
        new { type = "response.output_text.delta", output_index = 0, content_index = 0, delta = text },
        new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "msg_text", role = "assistant",
            content = new[] { new { type = "output_text", text, annotations = Array.Empty<object>() } } } }, Completed()
    } };
    private static object Completed() => new { type = "response.completed", response = new { id = "resp_offline", status = "completed",
        output = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } };

    private static IEnumerable<JsonElement> Messages(SessionLogReadResult log) => log.ValidatedPrefix
        .Where(record => record.Entry.Type == "message").Select(record => record.Entry.WireBody.Value.GetProperty("message"));
    private static string ResultText(JsonElement result) => string.Concat(result.GetProperty("content").EnumerateArray()
        .Where(value => value.GetProperty("type").GetString() == "text").Select(value => value.GetProperty("text").GetString()));
    private static void CheckEnvironment(JsonElement env, string artifact, Files files)
    {
        var expected = new[] { "LANG", "LC_ALL", "SystemRoot", "TEMP", "TMP" };
        Check(env.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)),
            "Child environment inherited undeclared credentials/PATH/home.");
        Equal("C.UTF-8", env.GetProperty("LANG").GetString()); Equal("C.UTF-8", env.GetProperty("LC_ALL").GetString());
        Equal(env.GetProperty("TEMP").GetString(), env.GetProperty("TMP").GetString());
        Equal(Path.GetDirectoryName(artifact), env.GetProperty("TEMP").GetString());
        Check(Directory.Exists(env.GetProperty("SystemRoot").GetString()), "SystemRoot did not identify the configured Windows directory.");
        CheckArtifact(artifact, files);
    }

    private static void CheckArtifact(string artifact, Files files)
    {
        Equal(Path.GetFullPath(artifact), artifact);
        var directory = Path.GetDirectoryName(artifact)!;
        Equal(files.Root, Path.GetDirectoryName(directory));
        Check(Path.GetFileName(directory).StartsWith("pisharp-bash-", StringComparison.Ordinal) &&
            Path.GetFileName(artifact).StartsWith("pi-bash-", StringComparison.Ordinal), "Spill identity escaped its fresh authorized directory.");
    }

    private sealed record Result(int Code, string Output, string Error);
    private static ProcessStartInfo Start(Files files, string[] args)
    {
        var start = new ProcessStartInfo(_host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 };
        start.ArgumentList.Add(_cli); foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["OPENAI_API_KEY"] = "authored-inert-test-must-not-enter-Bash";
        start.Environment["ANTHROPIC_API_KEY"] = "authored-inert-test-must-not-enter-Bash";
        return start;
    }
    private static async Task<Result> OneShot(Files files, params string[] args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var process = Process.Start(Start(files, args)) ?? throw new InvalidOperationException("Compiled CLI failed to start.");
        try
        {
            process.StandardInput.Close();
            var output = ReadText(process.StandardOutput, deadline.Token);
            var error = ReadText(process.StandardError, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(killed.Token);
            }
        }
    }
    private static async Task<string> ReadText(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[8192];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, token); if (count == 0) return text.ToString();
            Check(count <= 1_048_576 - text.Length, "Child diagnostic/report exceeded authored cap.");
            text.Append(buffer, 0, count);
        }
    }
    private sealed class RpcChild : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _deadline;
        private readonly object _gate = new();
        private readonly List<JsonData> _records = [];
        private TaskCompletionSource _changed = Gate();
        private readonly Task _read;
        private readonly Task<string> _error;
        private bool _closed;
        public JsonData[] Records { get { lock (_gate) return _records.ToArray(); } }
        public RpcChild(Files files, string[] args, TimeSpan? deadline = null)
        {
            _deadline = new(deadline ?? TimeSpan.FromSeconds(20));
            _process = Process.Start(Start(files, args)) ?? throw new InvalidOperationException("Compiled RPC failed to start.");
            _read = Read(); _error = ReadText(_process.StandardError, _deadline.Token);
        }
        public async Task Send(object command)
        {
            var text = JsonSerializer.Serialize(command) + "\n";
            Check(Utf8.GetByteCount(text) <= 1_048_577, "Test input exceeded unchanged RPC admission.");
            await _process.StandardInput.WriteAsync(text.AsMemory(), _deadline.Token);
            await _process.StandardInput.FlushAsync(_deadline.Token);
        }
        public Task<JsonData> Response(string id) => Wait(record => Type(record) == "response" &&
            record.Value.TryGetProperty("id", out var value) && value.GetString() == id);
        public async Task<JsonData> Wait(Func<JsonData, bool> predicate)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    var found = _records.FirstOrDefault(predicate); if (found is not null) return found;
                    if (_read.IsCompleted) throw new InvalidOperationException("Child ended before an expected flushed record.");
                    changed = _changed.Task;
                }
                await changed.WaitAsync(_deadline.Token);
            }
        }
        private async Task Read()
        {
            try
            {
                await using var reader = new JsonlReader(_process.StandardOutput.BaseStream, new(MaximumFrameBytes: 8 * 1024 * 1024));
                long bytes = 0;
                await foreach (var admission in reader.ReadAdmissionsAsync(_deadline.Token))
                {
                    Check(admission.IsAccepted && !admission.IsFinalFrame, "RPC stdout contained malformed or unflushed JSONL.");
                    var record = admission.Record!; bytes += Utf8.GetByteCount(record.ToString()) + 1;
                    Check(bytes <= 64 * 1024 * 1024, "Child total output exceeded authored bound.");
                    TaskCompletionSource changed;
                    lock (_gate)
                    {
                        Check(_records.Count < 2048, "Child record count exceeded authored bound.");
                        _records.Add(record); changed = _changed; _changed = Gate();
                    }
                    changed.TrySetResult();
                }
            }
            finally { lock (_gate) _changed.TrySetResult(); }
        }
        public async Task<Result> Finish()
        {
            if (!_closed) { _closed = true; _process.StandardInput.Close(); }
            await _process.WaitForExitAsync(_deadline.Token); await _read;
            return new(_process.ExitCode, "", await _error);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    // First request the host's real EOF shutdown; an inner assertion/deadline must
                    // run this cleanup before the harness's outer30s guard can abandon the case.
                    if (!_closed) { _closed = true; _process.StandardInput.Close(); }
                    using var orderly = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await _process.WaitForExitAsync(orderly.Token); }
                    catch (OperationCanceledException)
                    {
                        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                        using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await _process.WaitForExitAsync(killed.Token);
                    }
                }
                _deadline.Cancel();
                using var drained = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await Task.WhenAll(_read, _error).WaitAsync(drained.Token); }
                catch (Exception) when (_read.IsCompleted && _error.IsCompleted) { }
            }
            finally { _process.Dispose(); _deadline.Dispose(); }
        }
    }
    private abstract class BorrowedStream : Stream
    {
        public bool WasDisposed { get; private set; }
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
    private sealed class HostInput : BorrowedStream
    {
        private readonly Channel<byte[]> _input = Channel.CreateBounded<byte[]>(8);
        private byte[]? _current; private int _offset;
        public override bool CanRead => true; public override bool CanWrite => false;
        public void Send(object command) => Check(_input.Writer.TryWrite(Utf8.GetBytes(JsonSerializer.Serialize(command) + "\n")), "Input gate full.");
        public void Complete() => _input.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_current is null || _offset == _current.Length)
            {
                if (!await _input.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (!_input.Reader.TryRead(out _current)) throw new InvalidOperationException("Input gate lost a frame.");
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count;
        }
    }
    private sealed class HeldProgressOutput(HostInput input) : BorrowedStream
    {
        private readonly TaskCompletionSource _release = Gate();
        public TaskCompletionSource Entered { get; } = Gate();
        public List<JsonData> Rows { get; } = [];
        public override bool CanRead => false; public override bool CanWrite => true;
        public void Release() => _release.TrySetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check(buffer.Length <= 8 * 1024 * 1024 + 1 && Rows.Count < 2048, "Held output exceeded retained fixture bounds.");
            var row = JsonData.Parse(Utf8.GetString(buffer.Span)); Rows.Add(row);
            if (Type(row) == "tool_execution_update" && ResultText(row.Value.GetProperty("partialResult")).Length > 0 && !Entered.Task.IsCompleted)
            { Entered.TrySetResult(); await _release.Task.WaitAsync(cancellationToken); }
            if (Type(row) == "agent_settled") input.Complete();
        }
        public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }

    private sealed class FaultOutput(bool flush) : BorrowedStream
    {
        private readonly TaskCompletionSource _failure = Gate();
        private JsonData? _pending; private bool _faulted;
        public TaskCompletionSource<JsonData> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => false; public override bool CanWrite => true;
        public void Fail() => _failure.TrySetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_faulted) throw new IOException("private failing sink");
            Check(buffer.Length <= 8 * 1024 * 1024 + 1, "Output exceeded opt-in frame bound.");
            _pending = JsonData.Parse(Utf8.GetString(buffer.Span));
            if (!flush && Type(_pending) == "tool_execution_end")
            {
                _faulted = true; Entered.TrySetResult(_pending);
                await _failure.Task.WaitAsync(cancellationToken); throw new IOException("private failing sink");
            }
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_faulted) throw new IOException("private failing sink");
            if (_pending is not { } pending) return;
            if (flush && Type(pending) == "tool_execution_end")
            {
                _faulted = true; Entered.TrySetResult(pending);
                await _failure.Task.WaitAsync(cancellationToken); throw new IOException("private failing sink");
            }
            _pending = null;
        }
    }
    private sealed class Files : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; }
        public string Session => In("session.jsonl");
        public string Script => In("script.json");
        public string In(string name) => Path.Combine(Root, name);
        public Files() { Root = Path.Combine(_parent, "pisharp-cli-bash-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public void Dispose()
        {
            var target = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(target) != _parent || !Path.GetFileName(target).StartsWith("pisharp-cli-bash-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unowned Bash fixture cleanup.");
            Directory.Delete(target, recursive: true);
        }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Type(JsonData value) => value.Value.GetProperty("type").GetString()!;
    private static JsonElement Success(Result result)
    { Clean(result); var body = JsonData.Parse(result.Output).Value; Equal("completed", body.GetProperty("status").GetString()); return body; }
    private static JsonElement Response(JsonData value, string command)
    { Equal("response", Type(value)); Equal(command, value.Value.GetProperty("command").GetString()); Check(value.Value.GetProperty("success").GetBoolean(), "RPC command refused."); return value.Value; }
    private static void Clean(Result result) { Equal(0, result.Code); Equal("", result.Error); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
}
