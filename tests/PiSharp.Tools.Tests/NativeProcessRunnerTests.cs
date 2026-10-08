using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Agent.Tools;
using PiSharp.Tools.Processes;

internal static class NativeProcessRunnerTests
{
    private const string ChildSwitch = "--native-process-test-child";
    private static readonly Encoding Utf8 = new UTF8Encoding(false, false);
    public static object PlatformEvidence { get; private set; } = new { windows = "not-exercised", bash = "not-launched" };

    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("native process bounded admission and prelaunch cancellation have no effects", AdmissionAndBounds),
        ("native process borrowed Bash UTF8 cwd environment scratch effects and exit codes", BashEffectsAndExit),
        ("native process exact CRT argv copied environment and explicit stdin EOF", ExactArgvAndEnvironment),
        ("native process split Unicode BOM replacement and opaque NUL bytes", SplitUnicodeAndOpaqueBytes),
        ("native process source tail metadata raw spill hashes and structured both ends", TailAndStructuredSpill),
        ("native process total byte budget and sanitized output callback IO failures", BudgetAndOutputFaults),
        ("native process suspended running and drain cancellation timeout awaited cleanup", CancellationTimeoutAndAwaitedCleanup),
        ("native process contained descendants and rearmed postexit idle drainage", DescendantsAndPostExitDrain)
    ];

    // Root's existing test Main calls this with args[1..] before normal test/report dispatch.
    public static async Task<int> RunChildAsync(string[] args)
    {
        if (args.Length == 0) return 2;
        switch (args[0])
        {
            case "exact-argv-probe":
                await ExactArgvAndEnvironment();
                return 0;
            case "inspect":
                await Console.OpenStandardOutput().WriteAsync(Utf8.GetBytes(JsonSerializer.Serialize(new
                {
                    cwd = Environment.CurrentDirectory,
                    value = Environment.GetEnvironmentVariable("PISHARP_PROCESS_TEST"),
                    stdinEof = Console.OpenStandardInput().ReadByte() == -1,
                    arguments = args[1..]
                })));
                return 0;
            case "emit-file":
                await using (var input = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read))
                    await input.CopyToAsync(Console.OpenStandardOutput());
                return 0;
            case "touch":
                await File.WriteAllTextAsync(args[1], "child code ran", Utf8);
                return 0;
            case "block":
                await using (var control = await ConnectAsync(args[1]))
                {
                    await Console.OpenStandardOutput().WriteAsync(Utf8.GetBytes("ready-output\n"));
                    await SendAsync(control, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    await ReceiveAsync(control);
                }
                return 0;
            case "tree":
                using (var branch = StartChild("branch", args[2], args[3]))
                await using (var control = await ConnectAsync(args[1]))
                {
                    await Console.OpenStandardOutput().WriteAsync(Utf8.GetBytes("root-output\n"));
                    await SendAsync(control, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    await ReceiveAsync(control);
                }
                return 0;
            case "branch":
                using (var leaf = StartChild("leaf", args[2]))
                await using (var control = await ConnectAsync(args[1]))
                {
                    await SendAsync(control, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    await ReceiveAsync(control);
                }
                return 0;
            case "leaf":
                await using (var control = await ConnectAsync(args[1]))
                {
                    await SendAsync(control, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (await ReceiveAsync(control) == "WRITE")
                    {
                        await Console.OpenStandardOutput().WriteAsync(Utf8.GetBytes("late-output\n"));
                        await SendAsync(control, "WROTE");
                        await ReceiveAsync(control);
                    }
                }
                return 0;
            default: return 2;
        }
    }

    private static async Task AdmissionAndBounds()
    {
        using var temp = new Scratch(); var runner = new TestRunner();
        var request = ChildRequest(temp, "inspect");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var before = await runner.RunAsync(request, cancellationToken: canceled.Token);
        Equal(ProcessRunStatus.Canceled, before.Status); Check(!before.ProcessStarted && before.CleanupConfirmed, "Precancel launched a child.");
        Check(!File.Exists(request.SpillPath), "Precancel created spill output.");
        if (!OperatingSystem.IsWindows())
        {
            Has(await runner.RunAsync(request), ProcessDiagnostic.UnsupportedPlatform);
            PlatformEvidence = new { windows = "unsupported", bash = "not-launched" };
            return;
        }
        foreach (var invalid in new[]
        {
            request with { Executable = "relative.exe" }, request with { Arguments = default },
            request with { WorkingDirectory = temp.Root + Path.DirectorySeparatorChar + ".." },
            request with { TimeoutSeconds = 0 }, request with { TimeoutSeconds = -1 },
            request with { TimeoutSeconds = double.NaN }, request with { TimeoutSeconds = double.PositiveInfinity },
            request with { TimeoutSeconds = (double)int.MaxValue / 1000 + 1 },
            request with { Arguments = [new string('x', 32768)] },
            request with { Environment = ImmutableDictionary<string, string>.Empty.Add("Path", "a").Add("PATH", "b") },
            request with { Environment = ImmutableDictionary<string, string>.Empty.Add("bad=key", "v") },
            request with { Environment = ImmutableDictionary<string, string>.Empty.Add("key", "a\0b") },
            request with { Arguments = ["\ud800"] }
        })
        {
            var result = await runner.RunAsync(invalid); Has(result, ProcessDiagnostic.InvalidRequest);
            Check(!result.ProcessStarted && result.CleanupConfirmed && !File.Exists(request.SpillPath), "Invalid input had effects.");
        }
        Has(await runner.RunAsync(request with { Executable = temp.File("missing.exe") }), ProcessDiagnostic.ExecutableUnavailable);
        Has(await runner.RunAsync(request with { WorkingDirectory = temp.File("missing") }), ProcessDiagnostic.WorkingDirectoryUnavailable);
        Has(await runner.RunAsync(request with { SpillPath = temp.File("missing", "output") }), ProcessDiagnostic.SpillDirectoryUnavailable);
        await File.WriteAllTextAsync(request.SpillPath, "original");
        Has(await runner.RunAsync(request), ProcessDiagnostic.InvalidRequest); Equal("original", await File.ReadAllTextAsync(request.SpillPath));
        Throws<ArgumentOutOfRangeException>(() => new NativeProcessRunner(new(MaximumRawBytes: 0)));
        Throws<ArgumentOutOfRangeException>(() => new NativeProcessRunner(new(StructuredMaxBytes: 1)));
        Throws<ArgumentOutOfRangeException>(() => new NativeProcessRunner(new(QueueChunks: 0)));
    }

    private static async Task BashEffectsAndExit()
    {
        Windows(); using var temp = new Scratch();
        var bash = @"C:\Program Files\Git\usr\bin\bash.exe";
        Check(File.Exists(bash), "Explicit borrowed Git Bash is unavailable; no fallback is permitted.");
        var original = Environment.GetEnvironmentVariable("PISHARP_PROCESS_TEST");
        var env = ParentEnvironment().SetItem("PISHARP_PROCESS_TEST", "\u03c0\U0001f642")
            .SetItem("LANG", "C.UTF-8").SetItem("LC_ALL", "C.UTF-8");
        var request = new ProcessRequest(bash,
            ["-c", "printf '%s' \"$PISHARP_PROCESS_TEST\"; printf '\\nerr\\n' >&2; printf 'owned' > 'native-owned.txt'; exit 7"],
            temp.Root, env, temp.File("bash-output.log"));
        var result = await new TestRunner().RunAsync(request);
        Equal(ProcessRunStatus.NonZeroExit, result.Status); Equal<int?>(7, result.ExitCode); Settled(result);
        Check(result.Output.Content.Contains("\u03c0\U0001f642", StringComparison.Ordinal) && result.Output.Content.Contains("err", StringComparison.Ordinal), "stdout/stderr or Unicode output was lost.");
        Equal("owned", await File.ReadAllTextAsync(temp.File("native-owned.txt"))); Equal(original, Environment.GetEnvironmentVariable("PISHARP_PROCESS_TEST"));
        var empty = await new TestRunner().RunAsync(request with { Arguments = ["-c", "exit 0"], SpillPath = temp.File("empty.log") });
        Equal(ProcessRunStatus.Exited, empty.Status); Equal("", empty.Output.Content); Equal("", empty.StructuredOutput.Content); Settled(empty);
        var version = await new TestRunner().RunAsync(request with { Arguments = ["--version"], SpillPath = temp.File("version.log") });
        Equal(ProcessRunStatus.Exited, version.Status); Settled(version);
        PlatformEvidence = new { windows = Environment.OSVersion.VersionString, bash = bash, version = version.Output.Content.Split('\n')[0], descendantProfile = "ordinary CreateProcess inheritance in private nonbreakaway job" };
    }

    private static async Task ExactArgvAndEnvironment()
    {
        Windows(); using var temp = new Scratch();
        string[] values = ["", "a b", "quote\"value", "trailing\\", "two\\\\\"quotes", "line\nnext", "\u03c0\U0001f642"];
        var request = ChildRequest(temp, ["inspect", .. values]) with { Environment = ParentEnvironment().SetItem("PISHARP_PROCESS_TEST", "owned-env-\u03c0") };
        Console.Error.WriteLine("native-process-fixture exact-argv: inspect launch");
        var result = await new TestRunner().RunAsync(request); Settled(result); Equal(ProcessRunStatus.Exited, result.Status);
        Console.Error.WriteLine("native-process-fixture exact-argv: inspect settled");
        using var json = JsonDocument.Parse(result.StructuredOutput.Content);
        Equal(temp.Root, json.RootElement.GetProperty("cwd").GetString()); Equal("owned-env-\u03c0", json.RootElement.GetProperty("value").GetString());
        Check(json.RootElement.GetProperty("stdinEof").GetBoolean(), "Child inherited host stdin instead of EOF.");
        Sequence(values, json.RootElement.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()!));
        Check(!File.Exists(request.SpillPath), "Small output unnecessarily created an artifact.");
        var corrupt = temp.File("corrupt.exe"); await File.WriteAllTextAsync(corrupt, "not an executable");
        Console.Error.WriteLine("native-process-fixture exact-argv: invalid-image launch");
        var failed = await new TestRunner().RunAsync(request with { Executable = corrupt }); Has(failed, ProcessDiagnostic.SpawnFailed);
        Console.Error.WriteLine("native-process-fixture exact-argv: invalid-image settled");
        Check(!failed.ProcessStarted && failed.CleanupConfirmed, "Failed launch cleanup was unconfirmed.");
    }

    private static async Task SplitUnicodeAndOpaqueBytes()
    {
        Windows(); using var temp = new Scratch();
        var raw = Utf8.GetBytes("\ufeff\u03c0\U0001f642e\u0301\0\r\nend").Concat(new byte[] { 0xc3, 0x28, 0xf0, 0x9f }).ToArray();
        var source = temp.File("opaque.bin"); await File.WriteAllBytesAsync(source, raw);
        var result = await new TestRunner(new(ReadChunkBytes: 1)).RunAsync(ChildRequest(temp, "emit-file", source));
        Equal(ProcessRunStatus.Exited, result.Status); Settled(result); Equal(raw.Length, result.Output.RawBytes);
        var expected = Utf8.GetString(raw)[1..]; Equal(expected, result.Output.Content); Equal(expected, result.StructuredOutput.Content);
        Check(result.Output.Content.Contains('\0') && result.Output.Content.Contains('\ufffd'), "Opaque NUL or incomplete UTF8 replacement was removed.");
        Equal(Utf8.GetByteCount(expected), result.Output.Truncation.TotalBytes);
        Check(!File.Exists(result.Output.FullOutputPath), "Small opaque output should remain in bounded memory.");
        Sequence(raw, await File.ReadAllBytesAsync(source));
    }

    private static async Task TailAndStructuredSpill()
    {
        Windows(); using var temp = new Scratch();
        var lines = string.Join('\n', Enumerable.Range(1, 2001).Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "\n";
        var lineSource = temp.File("lines.bin"); await File.WriteAllBytesAsync(lineSource, Utf8.GetBytes(lines));
        var lineResult = await new TestRunner().RunAsync(ChildRequest(temp, "emit-file", lineSource)); Settled(lineResult);
        Equal(ToolOutputTruncator.Tail(lines), lineResult.Output.Truncation); Equal(lines, lineResult.StructuredOutput.Content);
        Equal(2001, lineResult.Output.Truncation.TotalLines); Check(lineResult.Output.FullOutputPath is not null, "Line truncation did not spill.");
        Sequence(Utf8.GetBytes(lines), await File.ReadAllBytesAsync(lineResult.Output.FullOutputPath!));
        var text = "HEAD-\u03c0\n" + new string('x', 1024 * 1024 + 89) + "\nTAIL-\U0001f642";
        var raw = Utf8.GetBytes(text); var source = temp.File("large.bin"); await File.WriteAllBytesAsync(source, raw);
        var sourceHash = Hash(raw); var request = ChildRequest(temp, "emit-file", source);
        var large = await new TestRunner().RunAsync(request); Settled(large); Equal(ProcessRunStatus.Exited, large.Status);
        Equal(ToolOutputTruncator.Tail(text), large.Output.Truncation); Check(large.StructuredOutput.Truncated, "Structured output failed to truncate.");
        Check(large.StructuredOutput.Content.StartsWith("HEAD-\u03c0\n", StringComparison.Ordinal) && large.StructuredOutput.Content.EndsWith("\nTAIL-\U0001f642", StringComparison.Ordinal), "Structured output lost one end.");
        Check(large.StructuredOutput.Content.Contains("[... " + (raw.Length - 1024 * 1024) + " bytes omitted ...]", StringComparison.Ordinal), "Structured omission count changed.");
        Check(Utf8.GetByteCount(large.StructuredOutput.Content) <= 1024 * 1024 + 100, "Structured output escaped its raw selection and marker bound.");
        Equal(sourceHash, Hash(await File.ReadAllBytesAsync(source))); Equal(sourceHash, Hash(await File.ReadAllBytesAsync(request.SpillPath)));
        using (new FileStream(request.SpillPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        var longLine = new string('\u03c0', 90_000); var longSource = temp.File("long-line.bin"); await File.WriteAllBytesAsync(longSource, Utf8.GetBytes(longLine));
        var tail = await new TestRunner().RunAsync(ChildRequest(temp, "emit-file", longSource)); Settled(tail);
        Equal(ToolOutputTruncator.Tail(longLine), tail.Output.Truncation); Check(tail.Output.Truncation.LastLinePartial, "Oversized multibyte line lost partial-tail metadata.");
        var boundaryText = new string('x', 31) + "\U0001f642" + new string('m', 10) + "\U0001f642" + new string('t', 30);
        var boundarySource = temp.File("structured-boundary.bin"); var boundaryBytes = Utf8.GetBytes(boundaryText); await File.WriteAllBytesAsync(boundarySource, boundaryBytes);
        var boundary = await new TestRunner(new(ModelMaxBytes: 16, StructuredMaxBytes: 64, ReadChunkBytes: 3))
            .RunAsync(ChildRequest(temp, "emit-file", boundarySource)); Settled(boundary);
        Equal(new string('x', 31) + "\n\n[... 15 bytes omitted ...]\n\n" + new string('t', 30), boundary.StructuredOutput.Content);
        Check(!boundary.StructuredOutput.Content.Contains('\ufffd'), "Structured half selection split a valid Unicode character.");
        Sequence(boundaryBytes, await File.ReadAllBytesAsync(boundary.Output.FullOutputPath!));
    }

    private static async Task BudgetAndOutputFaults()
    {
        Windows(); using var temp = new Scratch(); var source = temp.File("budget.bin"); await File.WriteAllBytesAsync(source, Enumerable.Repeat((byte)'x', 4096).ToArray());
        var limits = new ProcessRunnerOptions(MaximumRawBytes: 1024, ModelMaxBytes: 32, StructuredMaxBytes: 64, ReadChunkBytes: 17, QueueChunks: 2);
        var limited = await new TestRunner(limits).RunAsync(ChildRequest(temp, "emit-file", source));
        Has(limited, ProcessDiagnostic.OutputLimitExceeded); Equal(ProcessRunStatus.Failed, limited.Status); Check(limited.CleanupConfirmed && !limited.CapturedOutputComplete, "Resource failure concealed partial capture or failed cleanup.");
        Equal(1024, limited.Output.RawBytes); Equal(1024L, new FileInfo(limited.Output.FullOutputPath!).Length);
        foreach (var mode in new[] { FaultMode.Open, FaultMode.CreatedThenFailed, FaultMode.Write, FaultMode.Flush, FaultMode.Dispose, FaultMode.ForeignCancellation })
        {
            var storage = new FaultStorage(mode); var request = ChildRequest(temp, "emit-file", source);
            var result = await new TestRunner(limits with { MaximumRawBytes = 8192 }, outputStorage: storage).RunAsync(request);
            Equal(ProcessRunStatus.Failed, result.Status);
            Has(result, mode == FaultMode.Dispose ? ProcessDiagnostic.CleanupFailed : ProcessDiagnostic.OutputIoFailed);
            Check(result.Status != ProcessRunStatus.Canceled && (mode == FaultMode.Dispose || result.CleanupConfirmed), "Noncaller output failure was confused with cancellation.");
            Check(storage.Stream is null || storage.Stream.Closed, "Faulted owned stream was not closed.");
            Equal(1, storage.CreateCalls);
            if (File.Exists(request.SpillPath)) using (new FileStream(request.SpillPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        var progress = await new TestRunner(limits with { MaximumRawBytes = 8192 }).RunAsync(ChildRequest(temp, "emit-file", source),
            _ => throw new OperationCanceledException("private callback payload"));
        Has(progress, ProcessDiagnostic.ProgressCallbackFailed); Equal(ProcessRunStatus.Failed, progress.Status); Check(progress.CleanupConfirmed, "Progress failure leaked process ownership.");
    }

    private static async Task CancellationTimeoutAndAwaitedCleanup()
    {
        Windows(); using var temp = new Scratch();
        var suspended = NewGate<ProcessLifecycleObservation>(); var resume = NewGate<bool>(); using var suspendedCancel = new CancellationTokenSource();
        var touched = temp.File("must-not-exist");
        var suspendedRun = new TestRunner(lifecycle: async observation =>
        {
            if (observation.Stage == ProcessLifecycleStage.BeforeResume) { suspended.TrySetResult(observation); await resume.Task; }
        }).RunAsync(ChildRequest(temp, "touch", touched), cancellationToken: suspendedCancel.Token).AsTask();
        try
        {
            var beforeResume = await suspended.Task.WaitAsync(TimeSpan.FromSeconds(5)); suspendedCancel.Cancel();
            Check(!suspendedRun.IsCompleted && !File.Exists(touched), "Suspended child ran or cleanup bypassed its callback."); resume.TrySetResult(true);
            var suspendedResult = await suspendedRun; Equal(ProcessRunStatus.Canceled, suspendedResult.Status); Check(!suspendedResult.ProcessStarted && suspendedResult.CleanupConfirmed, "Suspended cancellation was reported as started.");
            Check(!File.Exists(touched), "Canceled suspended command produced a file effect."); Dead(beforeResume.ProcessId);
        }
        finally { suspendedCancel.Cancel(); resume.TrySetResult(true); await suspendedRun; }
        using (var control = new Control())
        using (var caller = new CancellationTokenSource())
        {
            var entered = NewGate<bool>(); var release = NewGate<bool>(); var once = 0;
            var running = new TestRunner().RunAsync(ChildRequest(temp, "block", control.Name), async _ =>
            { if (Interlocked.Exchange(ref once, 1) == 0) { entered.TrySetResult(true); await release.Task; } }, caller.Token).AsTask();
            try
            {
                var pid = await control.ReadyAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
                Check(!running.IsCompleted, "Cancellation skipped an active output callback."); release.TrySetResult(true);
                var result = await running; Equal(ProcessRunStatus.Canceled, result.Status); Check(result.CleanupConfirmed, "Running cancellation failed owned cleanup."); Dead(pid);
                Check(result.Output.Content.Contains("ready-output", StringComparison.Ordinal), "Cancellation hid already emitted output.");
            }
            finally { release.TrySetResult(true); caller.Cancel(); await running; }
        }
        using (var control = new Control())
        {
            var clock = new ManualClock();
            var timed = new TestRunner(timeProvider: clock).RunAsync(ChildRequest(temp, "block", control.Name) with { TimeoutSeconds = 1 }).AsTask();
            var pid = await control.ReadyAsync(); clock.Advance(TimeSpan.FromSeconds(1));
            var result = await timed; Equal(ProcessRunStatus.TimedOut, result.Status); Check(result.CleanupConfirmed, "Timeout did not await cleanup."); Dead(pid);
        }
        var ioSource = temp.File("dispose.bin"); await File.WriteAllBytesAsync(ioSource, Enumerable.Repeat((byte)'z', 4096).ToArray());
        var storage = new FaultStorage(FaultMode.GatedDispose);
        var disposeRun = new TestRunner(new(ModelMaxBytes: 32, StructuredMaxBytes: 64), outputStorage: storage)
            .RunAsync(ChildRequest(temp, "emit-file", ioSource)).AsTask();
        try
        {
            await storage.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(!disposeRun.IsCompleted, "Result was published before output DisposeAsync settled.");
            storage.DisposeRelease.TrySetResult(true); var disposed = await disposeRun; Settled(disposed); Check(storage.Stream!.Closed, "Owned output cleanup was incomplete.");
        }
        finally { storage.DisposeRelease.TrySetResult(true); await disposeRun; }
        var lifecycle = await new TestRunner(lifecycle: observation => observation.Stage == ProcessLifecycleStage.Started
            ? throw new OperationCanceledException("private lifecycle payload") : ValueTask.CompletedTask).RunAsync(ChildRequest(temp, "inspect"));
        Has(lifecycle, ProcessDiagnostic.LifecycleCallbackFailed); Equal(ProcessRunStatus.Failed, lifecycle.Status); Check(lifecycle.CleanupConfirmed, "Lifecycle failure leaked process ownership.");
    }

    private static async Task DescendantsAndPostExitDrain()
    {
        Windows(); using var temp = new Scratch();
        using (var root = new Control()) using (var branch = new Control()) using (var leaf = new Control()) using (var cancel = new CancellationTokenSource())
        {
            var run = new TestRunner().RunAsync(ChildRequest(temp, "tree", root.Name, branch.Name, leaf.Name), cancellationToken: cancel.Token).AsTask();
            try
            {
                var ids = await Task.WhenAll(root.ReadyAsync(), branch.ReadyAsync(), leaf.ReadyAsync());
                cancel.Cancel(); var result = await run; Equal(ProcessRunStatus.Canceled, result.Status); Check(result.CleanupConfirmed, "Descendant cancellation cleanup was unconfirmed.");
                foreach (var pid in ids) Dead(pid);
            }
            finally { cancel.Cancel(); await run; }
        }
        using (var root = new Control()) using (var branch = new Control()) using (var leaf = new Control()) using (var cancel = new CancellationTokenSource())
        {
            var clock = new ManualClock(); var late = NewGate<bool>();
            var run = new TestRunner(timeProvider: clock).RunAsync(ChildRequest(temp, "tree", root.Name, branch.Name, leaf.Name), snapshot =>
            { if (snapshot.Content.Contains("late-output", StringComparison.Ordinal)) late.TrySetResult(true); return ValueTask.CompletedTask; }, cancel.Token).AsTask();
            try
            {
                var ids = await Task.WhenAll(root.ReadyAsync(), branch.ReadyAsync(), leaf.ReadyAsync());
                await root.SendAsync("EXIT"); await clock.WaitForDeadlineAsync(TimeSpan.FromMilliseconds(100).Ticks);
                clock.Advance(TimeSpan.FromMilliseconds(80)); await leaf.SendAsync("WRITE"); Equal("WROTE", await leaf.ReceiveAsync());
                try { await late.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { throw new InvalidOperationException("Leaf acknowledged its write but the native inherited-pipe collector did not receive late output."); }
                await clock.WaitForDeadlineAsync(TimeSpan.FromMilliseconds(180).Ticks);
                clock.Advance(TimeSpan.FromMilliseconds(80)); Check(!run.IsCompleted, "Postexit grace used a fixed exit deadline instead of rearming on output.");
                clock.Advance(TimeSpan.FromMilliseconds(21)); var result = await run; Settled(result); Equal(ProcessRunStatus.Exited, result.Status);
                Check(result.Output.Content.Contains("root-output", StringComparison.Ordinal) && result.Output.Content.Contains("late-output", StringComparison.Ordinal), "Late inherited-pipe output was lost.");
                foreach (var pid in ids) Dead(pid);
            }
            finally { cancel.Cancel(); await run; }
        }
    }

    private sealed class TestRunner
    {
        private readonly NativeProcessRunner _runner;
        public TestRunner(ProcessRunnerOptions? options = null, TimeProvider? timeProvider = null,
            IProcessOutputStorage? outputStorage = null, ProcessLifecycleCallback? lifecycle = null) =>
            _runner = new(options, timeProvider, outputStorage, lifecycle);

        // A fixture timeout requests stop and joins the actual owned run before surfacing failure.
        // It does not change the production timeout profile or leave a task behind a WaitAsync guard.
        public async ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null,
            CancellationToken cancellationToken = default)
        {
            using var ownedStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var run = _runner.RunAsync(request, onUpdate, ownedStop.Token).AsTask();
            try { return await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { ownedStop.Cancel(); await run; }
        }
    }

    private static ProcessRequest ChildRequest(Scratch temp, params string[] childArgs)
    {
        var (executable, arguments) = ChildCommand(childArgs);
        return new(executable, arguments, temp.Root, ParentEnvironment(), temp.File(Guid.NewGuid().ToString("N") + ".log"));
    }
    private static (string Executable, ImmutableArray<string> Arguments) ChildCommand(string[] args)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No test executable path.");
        var arguments = ImmutableArray.CreateBuilder<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) arguments.Add(typeof(NativeProcessRunnerTests).Assembly.Location);
        arguments.Add(ChildSwitch); arguments.AddRange(args); return (Path.GetFullPath(executable), arguments.ToImmutable());
    }
    private static Process StartChild(params string[] args)
    {
        // .NET sets STARTF_USESTDHANDLES only when at least one stream is redirected.
        // Redirecting/closing stdin forces explicit inherited stdout/stderr handles without
        // introducing forwarding tasks that would disappear when a fixture ancestor exits.
        var (executable, arguments) = ChildCommand(args); var start = new ProcessStartInfo(executable)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Fixture descendant did not start.");
        process.StandardInput.Close(); return process;
    }
    private static ImmutableDictionary<string, string> ParentEnvironment()
    {
        var result = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())
            if (item.Key is string key && item.Value is string value && !key.Contains('=')) result[key] = value;
        return result.ToImmutable();
    }
    private static async Task<NamedPipeClientStream> ConnectAsync(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(5000); return pipe; } catch { await pipe.DisposeAsync(); throw; }
    }
    private static async Task SendAsync(Stream pipe, string value)
    { await pipe.WriteAsync(Utf8.GetBytes(value + "\n")); await pipe.FlushAsync(); }
    private static async Task<string> ReceiveAsync(Stream pipe)
    {
        var bytes = new List<byte>(); var one = new byte[1];
        while (bytes.Count < 1024)
        {
            if (await pipe.ReadAsync(one) == 0) return Utf8.GetString(bytes.ToArray());
            if (one[0] == 10) return Utf8.GetString(bytes.ToArray()); bytes.Add(one[0]);
        }
        throw new IOException("Fixture control frame exceeded its limit.");
    }
    private sealed class Control : IDisposable
    {
        public string Name { get; } = "pisharp-process-test-" + Guid.NewGuid().ToString("N");
        private readonly NamedPipeServerStream _server;
        public Control() => _server = new(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        public async Task<int> ReadyAsync()
        { await _server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5)); return int.Parse(await ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5)), System.Globalization.CultureInfo.InvariantCulture); }
        public Task SendAsync(string value) => NativeProcessRunnerTests.SendAsync(_server, value);
        public Task<string> ReceiveAsync() => NativeProcessRunnerTests.ReceiveAsync(_server);
        public void Dispose() => _server.Dispose();
    }
    private enum FaultMode { Open, CreatedThenFailed, Write, Flush, Dispose, ForeignCancellation, GatedDispose }
    private sealed class FaultStorage(FaultMode mode) : IProcessOutputStorage
    {
        public FaultStream? Stream { get; private set; }
        public int CreateCalls { get; private set; }
        public TaskCompletionSource<bool> DisposeEntered { get; } = NewGate<bool>();
        public TaskCompletionSource<bool> DisposeRelease { get; } = NewGate<bool>();
        public ValueTask<Stream> CreateNewAsync(string path)
        {
            CreateCalls++;
            if (mode == FaultMode.Open) throw new IOException("private open payload");
            if (mode == FaultMode.CreatedThenFailed)
            {
                using (var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) created.WriteByte(0x61);
                throw new IOException("private uncertain create payload");
            }
            if (mode == FaultMode.ForeignCancellation) throw new OperationCanceledException("private foreign token");
            Stream = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous), mode, DisposeEntered, DisposeRelease);
            return ValueTask.FromResult<Stream>(Stream);
        }
    }
    private sealed class FaultStream(Stream inner, FaultMode mode, TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release) : Stream
    {
        public bool Closed { get; private set; }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => !Closed;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (mode == FaultMode.Write) { await inner.WriteAsync(buffer[..Math.Min(3, buffer.Length)], token); throw new IOException("private partial write payload"); }
            await inner.WriteAsync(buffer, token);
        }
        public override Task FlushAsync(CancellationToken token) => mode == FaultMode.Flush ? Task.FromException(new IOException("private flush payload")) : inner.FlushAsync(token);
        public override async ValueTask DisposeAsync()
        {
            if (Closed) return;
            if (mode == FaultMode.GatedDispose) { entered.TrySetResult(true); await release.Task; }
            await inner.DisposeAsync(); Closed = true; GC.SuppressFinalize(this);
            if (mode == FaultMode.Dispose) throw new IOException("private cleanup payload");
        }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new(); private readonly List<ClockTimer> _timers = []; private long _ticks;
        private Channel<long> TimerScheduled { get; } = Channel.CreateUnbounded<long>();
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state); lock (_gate) _timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
        public void Advance(TimeSpan duration)
        {
            List<ClockTimer> ready;
            lock (_gate) { _ticks += duration.Ticks; ready = _timers.Where(timer => !timer.Closed && timer.Due <= _ticks).ToList(); foreach (var timer in ready) timer.Due = long.MaxValue; }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        public async Task WaitForDeadlineAsync(long deadline)
        {
            try { while (await TimerScheduled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)) < deadline) { } }
            catch (TimeoutException) { throw new InvalidOperationException("Postexit idle timer did not schedule the expected controlled deadline."); }
        }
        private sealed class ClockTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback; public object? State { get; } = state;
            public long Due { get; set; } = long.MaxValue; public bool Closed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (Closed) return false; Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                    if (dueTime != Timeout.InfiniteTimeSpan) owner.TimerScheduled.Writer.TryWrite(Due); return true;
                }
            }
            public void Dispose() { lock (owner._gate) { Closed = true; Due = long.MaxValue; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pisharp-process-" + Guid.NewGuid().ToString("N"), "space \u03c0"));
        public Scratch() => Directory.CreateDirectory(Root);
        public string File(params string[] parts) => Path.Combine([Root, .. parts]);
        public void Dispose() => Directory.Delete(Path.GetDirectoryName(Root)!, recursive: true);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Windows() => Check(OperatingSystem.IsWindows(), "This real backend fixture requires Windows; other platform gates remain open.");
    private static void Dead(int pid)
    { try { using var process = Process.GetProcessById(pid); Check(process.HasExited, "Owned fixture process remains alive: " + pid); } catch (ArgumentException) { } }
    private static void Settled(ProcessRunResult result)
    { Check(result.ProcessStarted && result.CleanupConfirmed && result.CapturedOutputComplete && result.Diagnostics.IsEmpty, "Process did not settle with complete capture and confirmed cleanup."); if (result.ProcessId is { } pid) Dead(pid); }
    private static void Has(ProcessRunResult result, ProcessDiagnostic code) => Check(result.Diagnostics.Contains(code), "Missing process diagnostic " + code);
    private static TaskCompletionSource<T> NewGate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, observed {actual}.");
    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual) => Check(expected.SequenceEqual(actual), "Sequence differs.");
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
