using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;
using PiSharp.Tools.Processes;

internal static class BashToolTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("Bash closed raw admission precancel truncation and existing spill reject without effects", Admission),
        ("Bash immutable final command argv cwd full environment spill and policy transformations", FinalAction),
        ("Bash source-facing empty exit truncation notices and distinct structured content", Formatting),
        ("Bash abort timeout spawn capture cleanup and foreign failures retain bounded known output", Failures),
        ("Bash awaited progress holds execution and cancellation joins actual runner cleanup", ProgressAndCleanup),
        ("Bash actual borrowed Windows shell scratch effects exits large spill and cancel progress", ActualWindows)
    ];

    private static async Task Admission()
    {
        using var files = new Scratch(); var runner = new Runner(); var policy = new Policy();
        var tool = Make(files, runner); var invoker = tool.CreateInvoker(policy);
        foreach (var raw in new[]
        {
            """{"command":"echo","timeout":1e999}""",
            """{"command":"\ud800"}""", """{"command":"x\u0000y"}""", """{"command":[]}""",
            JsonSerializer.Serialize(new { command = new string('x', 12_001) })
        })
            Failed(await invoker.ExecuteAsync(Invocation(JsonData.Parse(raw)), default), ToolFailureKind.InvalidArguments);
        using (var permissive = JsonDocument.Parse("""{"command":"echo",/* forbidden retained syntax */}""",
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
            Failed(await invoker.ExecuteAsync(Invocation(JsonData.FromElement(permissive.RootElement)), default), ToolFailureKind.InvalidArguments);
        var truncated = Invocation(Input("printf ok"));
        truncated = truncated with { AssistantMessage = truncated.AssistantMessage with { StopReason = StopReason.Length } };
        Failed(await invoker.ExecuteAsync(truncated, default), ToolFailureKind.Truncated);
        await ThrowsAsync<ArgumentException>(() => tool.PrepareAsync(truncated, default).AsTask());
        var unknown = Invocation(Input("printf ok"), "other");
        Failed(await invoker.ExecuteAsync(unknown, default), ToolFailureKind.UnknownTool);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Failed(await invoker.ExecuteAsync(Invocation(Input("printf ok")), canceled.Token), ToolFailureKind.Canceled);
        await File.WriteAllTextAsync(files.In("fixed.log"), "preserve existing");
        Failed(await invoker.ExecuteAsync(Invocation(Input("printf ok")), default), ToolFailureKind.InvalidArguments);
        Equal("preserve existing", await File.ReadAllTextAsync(files.In("fixed.log")));
        Equal(0, runner.Requests.Count); Equal(0, policy.Actions.Count);
        var tiny = new BashTool(runner, Options(files) with { MaximumArgumentCharacters = 8 }, () => "other.log");
        Failed(await tiny.CreateInvoker(policy).ExecuteAsync(Invocation(Input("echo")), default), ToolFailureKind.InvalidArguments);
        Throws<ArgumentException>(() => new BashTool(runner, Options(files) with { Executable = "relative" }));
        // Pi resolveTimeoutMs runs inside exec: an out-of-range timeout is a tool error with the source message, and nothing runs.
        foreach (var (raw, message) in new[]
        {
            ("""{"command":"echo","timeout":0}""", "Invalid timeout: must be a finite number of seconds"),
            ("""{"command":"echo","timeout":-1}""", "Invalid timeout: must be a finite number of seconds"),
            ("""{"command":"echo","timeout":2147483.648}""", "Invalid timeout: maximum is 2147483.647 seconds")
        })
        {
            File.Delete(files.In("fixed.log"));
            var invalid = await invoker.ExecuteAsync(Invocation(JsonData.Parse(raw)), default);
            Failed(invalid, ToolFailureKind.ExecutionError); Equal(message, invalid.Content.Single().Text); Equal(0, runner.Requests.Count);
        }
        Throws<ArgumentException>(() => new BashTool(runner, Options(files) with
            { Environment = ImmutableDictionary<string, string>.Empty.Add("PATH", "one").Add("Path", "two") }));
    }

    private static async Task FinalAction()
    {
        using var files = new Scratch(); var other = files.In("other"); Directory.CreateDirectory(other);
        var runner = new Runner(); var tool = Make(files, runner);
        var prepared = await tool.PrepareAsync(Invocation(JsonData.Parse("""{"command":"printf 'initial'","timeout":1.00}""")), default);
        Equal("1.00", prepared.Arguments.Value.GetProperty("timeout").GetRawText());
        Equal(files.In("fixed.log"), prepared.Arguments.Value.GetProperty("outputPath").GetString());
        Check(prepared.CommandArguments.SequenceEqual(["-c", "printf 'initial'"]), "Command argv was hidden or rewritten.");
        var transformedEnv = ImmutableDictionary<string, string>.Empty.Add("PISHARP_TEST", "full replacement");
        ValueTask<PreparedToolAction> Rewrite(ToolInvocation _, PreparedToolAction action, CancellationToken token) =>
            ValueTask.FromResult(action with { Arguments = ActionArguments("printf 'final'", files.In("final.log"), .25),
                CommandArguments = ["-c", "printf 'final'"], WorkingDirectory = other, Environment = transformedEnv });
        var denied = new Policy(action => action.Arguments.Value.GetProperty("command").GetString() != "printf 'final'");
        Failed(await tool.CreateInvoker(denied, [Rewrite]).ExecuteAsync(Invocation(Input("printf 'initial'")), default), ToolFailureKind.Blocked);
        Equal(0, runner.Requests.Count);
        var allow = new Policy();
        var result = await tool.CreateInvoker(allow, [Rewrite]).ExecuteAsync(Invocation(Input("printf 'initial'")), default);
        Check(!result.IsError, "Qualified final action failed.");
        var final = allow.Actions.Single(); var actual = runner.Requests.Single();
        Equal(final.Target, actual.Executable); Equal(final.WorkingDirectory, actual.WorkingDirectory);
        Check(final.CommandArguments.SequenceEqual(actual.Arguments), "Execution argv differed from authorized argv.");
        Check(ReferenceEquals(final.Environment, actual.Environment), "Authorized full environment was replaced after policy.");
        Equal(final.Arguments.Value.GetProperty("outputPath").GetString(), actual.SpillPath);
        Equal<double?>(.25, actual.TimeoutSeconds);
        foreach (var invalid in new[]
        {
            prepared with { CommandArguments = ["-c", "different"] }, prepared with { Target = files.In("other.exe") },
            prepared with { WorkingDirectory = "relative" },
            prepared with { Arguments = ActionArguments("printf 'initial'", Path.Combine(Path.GetDirectoryName(files.Root)!, "outside.log"), 1) },
            prepared with { Environment = transformedEnv.Add("bad=key", "value") }
        })
        {
            var calls = runner.Requests.Count; var actions = allow.Actions.Count;
            Failed(await tool.CreateInvoker(allow, [(_, _, _) => ValueTask.FromResult(invalid)])
                .ExecuteAsync(Invocation(Input("printf 'initial'")), default), ToolFailureKind.InvalidArguments);
            Equal(calls, runner.Requests.Count); Equal(actions, allow.Actions.Count);
        }
        var definition = tool.CreateDefinition(tool.CreateInvoker(new Policy()));
        Equal("bash", definition.Name);
        // Pi bash.ts: TypeBox parameters without additionalProperties; providers add strictness themselves.
        Equal("""{"name":"bash","description":"Execute a bash command in the current working directory. Returns stdout and stderr. Output is truncated to last 2000 lines or 50KB (whichever is hit first). If truncated, full output is saved to a temp file. Optionally provide a timeout in seconds.","parameters":{"type":"object","properties":{"command":{"type":"string","description":"Shell command to execute"},"timeout":{"type":"number","description":"Timeout in seconds (optional, no default timeout)"}},"required":["command"]},"constrainedSampling":{"type":"json_schema","strict":"prefer"}}""",
            tool.Declaration.ToString());
    }

    private static async Task Formatting()
    {
        using var files = new Scratch(); var runner = new Runner(); var tool = Make(files, runner);
        async Task<ToolResult> Run(ProcessRunResult result)
        { runner.Next = (_, _, _) => ValueTask.FromResult(result); return await tool.CreateInvoker(new Policy()).ExecuteAsync(Invocation(Input("")), default); }
        var empty = await Run(Result(""));
        Equal("(no output)", empty.Content.Single().Text); Check(empty.Details.Value.ValueKind == JsonValueKind.Null, "Native undefined representation changed.");
        Equal("", empty.StructuredContent!.Value.GetProperty("output").GetString());
        var nonzero = await Run(Result("stdout\nstderr\n", ProcessRunStatus.NonZeroExit, 7));
        Equal("stdout\nstderr\n\n\nCommand exited with code 7", nonzero.Content.Single().Text);
        Check(nonzero.IsError && nonzero.Failure is null, "A normal nonzero exit lost its source structured result.");
        Equal(7, nonzero.StructuredContent!.Value.GetProperty("exit_code").GetInt32());
        var tail = Snapshot("last\n") with { Truncation = ToolOutputTruncator.Tail("last\n") with
            { Truncated = true, TruncatedBy = ToolOutputTruncationLimit.Lines, TotalLines = 2001 },
            FullOutputPath = files.In("fixed.log") };
        var lines = await Run(Result("last\n") with { Output = tail });
        Equal("last\n\n\n[Showing lines 2001-2001 of 2001. Full output: " + files.In("fixed.log") + "]", lines.Content.Single().Text);
        Check(!lines.StructuredContent!.Value.TryGetProperty("full_output_path", out _), "Model truncation falsely claimed structured truncation.");
        tail = tail with { Truncation = tail.Truncation with { TruncatedBy = ToolOutputTruncationLimit.Bytes } };
        var bytes = await Run(Result("last\n") with { Output = tail, StructuredOutput = new("head\n\n[omitted]\n\ntail", true) });
        Check(bytes.Content.Single().Text.Contains("(50.0KB limit)", StringComparison.Ordinal), "Byte notice lost source budget.");
        Equal(files.In("fixed.log"), bytes.StructuredContent!.Value.GetProperty("full_output_path").GetString());
        Equal("head\n\n[omitted]\n\ntail", bytes.StructuredContent.Value.GetProperty("output").GetString());
        var partial = Snapshot("tail") with { LastLineBytes = 100_000, FullOutputPath = files.In("fixed.log"),
            Truncation = ToolOutputTruncator.Tail("tail") with { Truncated = true, TruncatedBy = ToolOutputTruncationLimit.Bytes,
                LastLinePartial = true, TotalLines = 1 } };
        var last = await Run(Result("tail") with { Output = partial });
        Equal("tail\n\n[Showing last 4B of line 1 (line is 97.7KB). Full output: " + files.In("fixed.log") + "]", last.Content.Single().Text);
        Equal(.2, last.StructuredContent!.Value.GetProperty("wall_time_seconds").GetDouble());
        // Direct adapter preserves raw NUL output; ordinary ToolInvoker content admission is a separate root-owned prerequisite.
        runner.Next = (_, _, _) => ValueTask.FromResult(Result("a\0b"));
        var raw = await tool.ExecuteAsync(await tool.PrepareAsync(Invocation(Input("")), default), default);
        Equal("a\0b", raw.Content.Single().Text); Equal("a\0b", raw.StructuredContent!.Value.GetProperty("output").GetString());
    }

    private static async Task Failures()
    {
        using var files = new Scratch(); var runner = new Runner(); var tool = Make(files, runner);
        var action = await tool.PrepareAsync(Invocation(Input("printf known", .1)), default);
        // Pinned bash.ts formats normal completion with "(no output)" before checking for a null exit code.
        foreach (var text in new[] { "", "known" })
        {
            runner.Next = (_, _, _) => ValueTask.FromResult(Result(text, ProcessRunStatus.Exited, null));
            var noExit = await tool.ExecuteAsync(action, default);
            Equal((text.Length == 0 ? "(no output)" : text) + "\n\nCommand terminated without an exit code", noExit.Content.Single().Text);
            Failed(noExit, ToolFailureKind.ExecutionError);
            Equal("Command terminated without an exit code", noExit.Failure!.Message);
            Check(noExit.StructuredContent is null && noExit.Details.Value.ValueKind == JsonValueKind.Null,
                "Missing exit code fabricated structured completion or output details.");
        }
        // Abort/timeout/failure paths use the source error fallback (empty text), not normal completion's fallback.
        foreach (var (status, expected) in new[]
        {
            (ProcessRunStatus.Canceled, "Command aborted"),
            (ProcessRunStatus.TimedOut, "Command timed out after 0.1 seconds"),
            (ProcessRunStatus.Failed, "Command execution failed.")
        })
        {
            runner.Next = (_, _, _) => ValueTask.FromResult(Result("", status, null));
            Equal(expected, (await tool.ExecuteAsync(action, default)).Content.Single().Text);
        }
        runner.Next = (_, _, _) => ValueTask.FromResult(Result("", ProcessRunStatus.Exited, null) with { CleanupConfirmed = false });
        Equal("Command execution failed.", (await tool.ExecuteAsync(action, default)).Content.Single().Text);
        foreach (var (status, expected) in new[]
        {
            (ProcessRunStatus.Canceled, "Command aborted"),
            (ProcessRunStatus.TimedOut, "Command timed out after 0.1 seconds"),
            (ProcessRunStatus.Failed, "Command execution failed.")
        })
        {
            runner.Next = (_, _, _) => ValueTask.FromResult(Result("known", status, null));
            var result = await tool.ExecuteAsync(action, default);
            Equal("known\n\n" + expected, result.Content.Single().Text);
            Check(result.IsError && result.StructuredContent is null, "Thrown-source outcome fabricated normal structured completion.");
        }
        runner.Next = (_, _, _) => ValueTask.FromResult(Result("partial") with { CapturedOutputComplete = false });
        Equal("partial\n\nCommand execution failed.", (await tool.ExecuteAsync(action, default)).Content.Single().Text);
        runner.Next = (_, _, _) => ValueTask.FromResult(Result("known", ProcessRunStatus.Canceled, null) with { CleanupConfirmed = false });
        Failed(await tool.ExecuteAsync(action, default), ToolFailureKind.ExecutionError);
        runner.Next = (_, _, _) => ValueTask.FromResult(Result("known") with { Diagnostics = [ProcessDiagnostic.SpawnFailed] });
        Equal("known\n\nCommand execution failed.", (await tool.ExecuteAsync(action, default)).Content.Single().Text);
        runner.Next = async (_, update, _) =>
        {
            await update!(Snapshot("observed prefix"));
            throw new OperationCanceledException("foreign private failure");
        };
        var failed = await tool.ExecuteAsync(action, default);
        Equal("observed prefix\n\nCommand execution failed.", failed.Content.Single().Text);
        Check(!failed.Content.Single().Text.Contains("private", StringComparison.Ordinal), "Foreign runner exception leaked.");
        runner.Next = (_, _, _) => ValueTask.FromResult(Result("bad") with
            { Output = Snapshot("bad") with { FullOutputPath = files.In("unauthorized.log") } });
        Failed(await tool.ExecuteAsync(action, default), ToolFailureKind.ExecutionError);
    }

    private static async Task ProgressAndCleanup()
    {
        using var files = new Scratch(); var runner = new Runner(); var tool = Make(files, runner);
        var progressEntered = Gate(); var progressRelease = Gate(); var cleanupEntered = Gate(); var cleanupRelease = Gate();
        using var canceled = new CancellationTokenSource(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reports = new List<ToolResult>(); var ended = false;
        runner.Next = async (_, update, token) =>
        {
            await update!(Snapshot("known before cancel"));
            cleanupEntered.TrySetResult(); await cleanupRelease.Task.WaitAsync(deadline.Token);
            ended = true; return Result("known before cancel", token.IsCancellationRequested ? ProcessRunStatus.Canceled : ProcessRunStatus.Exited, null);
        };
        var running = tool.CreateInvoker(new Policy()).ExecuteAsync(Invocation(Input("unused")),
            async (partial, _) =>
            {
                reports.Add(partial);
                if (!partial.Content.IsEmpty) { progressEntered.TrySetResult(); await progressRelease.Task.WaitAsync(deadline.Token); }
            }, canceled.Token).AsTask();
        try
        {
            await progressEntered.Task.WaitAsync(deadline.Token);
            Equal(2, reports.Count); Check(reports[0].Content.IsEmpty, "Initial source update was omitted.");
            Check(!cleanupEntered.Task.IsCompleted && !running.IsCompleted, "Progress publication was detached.");
            canceled.Cancel(); progressRelease.TrySetResult();
            await cleanupEntered.Task.WaitAsync(deadline.Token);
            Check(!running.IsCompleted && !ended, "Cancellation returned before the actual runner cleanup barrier.");
            cleanupRelease.TrySetResult();
            var result = await running.WaitAsync(deadline.Token);
            Check(ended, "Runner cleanup was not joined."); Failed(result, ToolFailureKind.Canceled);
            Check(result.Content[0].Text.StartsWith("known before cancel", StringComparison.Ordinal), "Canceled finalization discarded known output.");
        }
        finally { progressRelease.TrySetResult(); cleanupRelease.TrySetResult(); try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { } }
    }

    private static async Task ActualWindows()
    {
        Check(OperatingSystem.IsWindows(), "Actual Bash fixture requires Windows; no substitute backend is permitted.");
        using var files = new Scratch(); const string bash = @"C:\Program Files\Git\usr\bin\bash.exe";
        Check(File.Exists(bash), "Explicit borrowed Git Bash is unavailable.");
        var environment = ImmutableDictionary<string, string>.Empty.Add("SystemRoot", @"C:\Windows")
            .Add("TEMP", files.Root).Add("TMP", files.Root).Add("LANG", "C.UTF-8").Add("LC_ALL", "C.UTF-8")
            .Add("PISHARP_BASH_TEST", "\u6587\U0001f642");
        var ordinal = 0;
        var tool = new BashTool(new NativeProcessRunner(), new(bash, files.Root, environment, files.Root),
            () => "actual-" + Interlocked.Increment(ref ordinal) + ".log");
        var policy = new Policy(action => action.Target == bash && action.WorkingDirectory == files.Root &&
            action.Environment.Count == environment.Count && environment.All(pair =>
                action.Environment.TryGetValue(pair.Key, out var value) && value == pair.Value) &&
            Path.GetDirectoryName(action.Arguments.Value.GetProperty("outputPath").GetString()) == files.Root);
        await File.WriteAllTextAsync(files.In("scratch-source.txt"), "owned source\n", new UTF8Encoding(false));
        var original = await File.ReadAllBytesAsync(files.In("scratch-source.txt"));
        var result = await tool.CreateInvoker(policy).ExecuteAsync(Invocation(Input(
            "IFS= read -r value < 'scratch-source.txt'; printf '%s\\n%s\\n' \"$value\" \"$PISHARP_BASH_TEST\"; printf 'owned write' > 'scratch-output.txt'; exit 7")), default);
        Check(result.IsError && result.Failure is null, "Actual nonzero shell execution did not settle normally.");
        Equal(7, result.StructuredContent!.Value.GetProperty("exit_code").GetInt32());
        Check(result.Content.Single().Text.Contains("\u6587\U0001f642", StringComparison.Ordinal), "Actual environment/UTF8 output was changed.");
        Equal("owned write", await File.ReadAllTextAsync(files.In("scratch-output.txt")));
        var after = await File.ReadAllBytesAsync(files.In("scratch-source.txt")); Check(original.SequenceEqual(after), "Bash fixture overwrote source.");
        var large = await tool.CreateInvoker(policy).ExecuteAsync(Invocation(Input(
            "for ((i=0;i<60000;i++)); do printf 'abcdefghijklmnopqrstuvwxyz0123456789\\n'; done")), default);
        Check(!large.IsError, "Actual large-output Bash failed.");
        var artifact = large.Details.Value.GetProperty("fullOutputPath").GetString()!;
        var raw = await File.ReadAllBytesAsync(artifact);
        Equal(37 * 60000, raw.Length); Check(large.StructuredContent!.Value.GetProperty("truncated").GetBoolean(), "Structured output did not keep both ends.");
        Equal(artifact, large.StructuredContent.Value.GetProperty("full_output_path").GetString());
        using var cancel = new CancellationTokenSource(); var entered = Gate();
        var aborted = tool.CreateInvoker(policy).ExecuteAsync(Invocation(Input("printf 'cancel-ready\\n'; while :; do :; done")),
            (partial, _) => { if (!partial.Content.IsEmpty) { entered.TrySetResult(); cancel.Cancel(); } return ValueTask.CompletedTask; }, cancel.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var settled = await aborted.WaitAsync(TimeSpan.FromSeconds(15));
        Failed(settled, ToolFailureKind.Canceled);
        Check(settled.Content[0].Text.Contains("cancel-ready", StringComparison.Ordinal), "Actual canceled output was lost.");
    }

    private static BashTool Make(Scratch files, Runner runner) => new(runner, Options(files), () => "fixed.log");
    private static BashToolOptions Options(Scratch files) => new(Environment.ProcessPath!, files.Root, ImmutableDictionary<string, string>.Empty, files.Root);
    private static JsonData Input(string command, double? timeout = null) => timeout is null
        ? JsonData.Parse(JsonSerializer.Serialize(new { command })) : JsonData.Parse(JsonSerializer.Serialize(new { command, timeout }));
    private static JsonData ActionArguments(string command, string outputPath, double timeout) =>
        JsonData.Parse(JsonSerializer.Serialize(new { command, outputPath, timeout }));
    private static ToolInvocation Invocation(JsonData arguments, string name = "bash")
    {
        var call = new ToolCallContent("bash-call", name, arguments);
        return new(new("fixture-api", "fixture-provider", "fixture-model", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
    }
    private static ProcessOutputSnapshot Snapshot(string text) => new(text, ToolOutputTruncator.Tail(text),
        Encoding.UTF8.GetByteCount(text), Encoding.UTF8.GetByteCount(text.Split('\n')[^1]), null);
    private static ProcessRunResult Result(string text, ProcessRunStatus status = ProcessRunStatus.Exited, int? code = 0) =>
        new(status, code, 123, true, true, true, Snapshot(text), new(text, false), .2, []);
    private sealed class Runner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Func<ProcessRequest, ProcessOutputCallback?, CancellationToken, ValueTask<ProcessRunResult>> Next { get; set; } =
            (_, _, _) => ValueTask.FromResult(Result(""));
        public ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null,
            CancellationToken cancellationToken = default)
        { Requests.Add(request); return Next(request, onUpdate, cancellationToken); }
    }
    private sealed class Policy(Func<PreparedToolAction, bool>? allow = null) : IToolActionPolicy
    {
        public List<PreparedToolAction> Actions { get; } = [];
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction finalAction,
            CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Actions.Add(finalAction); return ValueTask.FromResult(new ToolActionAuthorization(allow?.Invoke(finalAction) ?? true)); }
    }
    private sealed class Scratch : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; }
        public string In(string name) => Path.Combine(Root, name);
        public Scratch() { Root = Path.Combine(_parent, "pisharp-bash-adapter-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(path) != _parent || !Path.GetFileName(path).StartsWith("pisharp-bash-adapter-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unowned Bash test cleanup.");
            Directory.Delete(path, recursive: true);
        }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Failed(ToolResult result, ToolFailureKind kind) { Check(result.IsError, "Expected tool error."); Equal(kind, result.Failure?.Kind); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected failure."); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected failure."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ.");
}
