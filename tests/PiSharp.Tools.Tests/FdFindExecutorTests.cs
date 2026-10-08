using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;
using PiSharp.Tools.Files;
using PiSharp.Tools.Processes;

internal static class FdFindExecutorTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("fd exact basename and Windows full-path argv preserve repository ignore flags", Arguments),
        ("fd explicit host admission bytes and unknown repository context refuse process launch", Admission),
        ("fd separated original pipe chunks preserve UTF8 and independent capture limits", SeparateCapture),
        ("fd exit stderr partial stdout and empty output follow bounded source handling", Exits),
        ("fd find composition selects default fd notice without treating stderr as paths", Composition),
        ("fd refuses incomplete cleanup capture diagnostics and truncated pipe receipts", Receipts),
        ("fd retains original process and spill cleanup failures after both joins", CleanupFailures),
        ("fd cancellation waits original process and spill disposal before settlement", Cancellation),
        ("fd cancellation during successful held spill cleanup retains caller token after disposal", CancellationDuringSuccessfulCleanup),
        ("fd local exclusive spill lease settles disposal and removes owned output", LocalSpill)
    ];
    private static async Task Arguments()
    {
        using var fixture = new Fixture(); var executor = fixture.Executor();
        await executor.FindAsync(Request(fixture, "*.txt", 7), default);
        Names(["--glob", "--color=never", "--hidden", "--no-require-git", "--max-results", "7", "--", "*.txt", fixture.Root], fixture.Runner.Last!.Arguments);
        fixture.Repository = FdRepositoryPresence.InsideGitRepository;
        await executor.FindAsync(Request(fixture, "src/**/*.cs", 4), default);
        Names(["--glob", "--color=never", "--hidden", "--max-results", "4", "--full-path", "--", @"**[/\\]src[/\\]**[/\\]*.cs", fixture.Root], fixture.Runner.Last!.Arguments);
        await executor.FindAsync(Request(fixture, "**/*.json", 3), default);
        Check(fixture.Runner.Last!.Arguments[^2] == @"**[/\\]*.json", "Existing double-star prefix changed.");
        await executor.FindAsync(Request(fixture, "[ab]?.{txt,md}", 3), default);
        Check(fixture.Runner.Last!.Arguments[^2] == "[ab]?.{txt,md}" && fixture.Runner.Last.TimeoutSeconds is null &&
            fixture.Runner.Last.WorkingDirectory == fixture.Root && fixture.Runner.Last.Executable == fixture.Binary.Executable &&
            fixture.Runner.Last.Environment.Count == 0, "Pattern was approximated or ambient process configuration inferred.");
    }
    private static async Task Admission()
    {
        using var fixture = new Fixture(); fixture.Admitted = false;
        await Throws<IOException>(() => fixture.Executor().FindAsync(Request(fixture), default).AsTask());
        Check(fixture.Runner.Calls == 0 && fixture.Spills.Calls == 0, "Unadmitted binary reached process/spill admission.");
        fixture.Admitted = true; await File.WriteAllBytesAsync(fixture.Binary.Executable, Enumerable.Repeat((byte)'x', fixture.Binary.Length).ToArray());
        await Throws<IOException>(() => fixture.Executor().FindAsync(Request(fixture), default).AsTask());
        Check(fixture.Runner.Calls == 0 && fixture.Spills.Calls == 0, "Changed binary hash reached launch.");
        await File.WriteAllBytesAsync(fixture.Binary.Executable, fixture.BinaryBytes); fixture.Repository = FdRepositoryPresence.Unknown;
        await Throws<IOException>(() => fixture.Executor().FindAsync(Request(fixture), default).AsTask());
        Check(fixture.Runner.Calls == 0 && fixture.Spills.Calls == 0, "Unknown repository context was guessed.");
        Check(fixture.Executor().SupportsPattern("[ab]?.{txt,md}") && !fixture.Executor().SupportsPattern("x\0"), "Fd syntax was silently approximated.");
    }
    private static Task SeparateCapture()
    {
        var original = Receipt("merged", "receipt", 0).Process;
        using var capture = new SeparatedProcessCapture(8); var bytes = Encoding.UTF8.GetBytes("out:\U0001f31f");
        capture.Append(false, bytes.AsSpan(0, 6)); capture.Append(true, Encoding.UTF8.GetBytes("err-"));
        capture.Append(false, bytes.AsSpan(6)); capture.Append(true, Encoding.UTF8.GetBytes("only"));
        var result = capture.Result(original);
        Check(ReferenceEquals(original, result.Process) && result.StandardOutput.Content == "out:\U0001f31f" && result.StandardError.Content == "err-only" &&
            !result.StandardOutput.Truncated && !result.StandardError.Truncated, "Original receipt or pipe/UTF8 identity changed.");
        capture.Append(false, Encoding.UTF8.GetBytes("overflow")); result = capture.Result(original);
        Check(result.StandardOutput.Truncated && !result.StandardError.Truncated && result.StandardError.Content == "err-only", "One pipe's bound contaminated the other.");
        return Task.CompletedTask;
    }
    private static async Task Exits()
    {
        using var fixture = new Fixture(); var executor = fixture.Executor();
        fixture.Runner.Result = Receipt(" a.txt\r\nb.txt\n", "warning only", 2);
        Names(["a.txt", "b.txt"], await executor.FindAsync(Request(fixture), default));
        fixture.Runner.Result = Receipt("", "  invalid glob  \n", 2);
        Check((await Throws<IOException>(() => executor.FindAsync(Request(fixture), default).AsTask())).Message == "invalid glob", "Empty nonzero output lost trimmed stderr.");
        fixture.Runner.Result = Receipt("", "", 3);
        Check((await Throws<IOException>(() => executor.FindAsync(Request(fixture), default).AsTask())).Message == "fd exited with code 3", "Fallback exit message changed.");
        fixture.Runner.Result = Receipt("", "nonfatal warning", 0);
        Check((await executor.FindAsync(Request(fixture), default)).IsEmpty, "Stderr became file paths.");
        fixture.Runner.Result = Receipt(" \n", "", 0);
        await Throws<IOException>(() => executor.FindAsync(Request(fixture), default).AsTask());
    }
    private static async Task Composition()
    {
        using var fixture = new Fixture(); fixture.Runner.Result = Receipt("one.txt\n", "stderr must not be listed", 0);
        var tool = new FindTool(fixture.Root, fixture.Root, fixture.Executor());
        var call = new ToolCallContent("fd-call", "find", JsonData.Parse("{\"pattern\":\"*.txt\",\"limit\":1}"));
        var invocation = new ToolInvocation(new AssistantMessage("fixture", "fixture", "fixture", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
        var result = await tool.CreateInvoker(new Permit()).ExecuteAsync(invocation, default);
        Check(!result.IsError && result.Content.Single().Text == "one.txt\n\n[1 results limit reached. Use limit=2 for more, or refine pattern]" &&
            result.Details.Value.GetProperty("resultLimitReached").GetInt32() == 1, "Fd branch notice or separated path handling changed.");
        Check(fixture.Runner.Last!.Arguments.Contains("--no-require-git") && fixture.Spills.Last!.Disposed, "Repository flag or spill join lost.");
    }
    private static async Task Receipts()
    {
        using var fixture = new Fixture(); var good = Receipt("one.txt\n", "", 0);
        foreach (var result in new[]
        {
            good with { Process = good.Process with { CleanupConfirmed = false } },
            good with { Process = good.Process with { CapturedOutputComplete = false } },
            good with { StandardOutput = new("one.txt", true) },
            good with { StandardError = new("partial", true) },
            good with { Process = good.Process with { Diagnostics = [ProcessDiagnostic.OutputIoFailed] } },
            good with { Process = good.Process with { ProcessStarted = false, Status = ProcessRunStatus.Failed } }
        })
        {
            fixture.Runner.Result = result;
            await Throws<IOException>(() => fixture.Executor().FindAsync(Request(fixture), default).AsTask());
            Check(fixture.Spills.Last!.Disposed, "Rejected original receipt escaped spill cleanup.");
        }
    }
    private static async Task CleanupFailures()
    {
        using var fixture = new Fixture(); var original = new IOException("original process cleanup fault"); var cleanup = new IOException("owned spill cleanup fault");
        fixture.Runner.Work = (_, _) => throw original; fixture.Spills.Dispose = () => throw cleanup;
        var error = await Throws<AggregateException>(() => fixture.Executor().FindAsync(Request(fixture), default).AsTask());
        Check(error.InnerExceptions.Count == 2 && ReferenceEquals(original, error.InnerExceptions[0]) && ReferenceEquals(cleanup, error.InnerExceptions[1]), "Original failure identities/order were discarded.");
    }
    private static async Task Cancellation()
    {
        using var fixture = new Fixture(); using var cancel = new CancellationTokenSource();
        var entered = Gate(); var processRelease = Gate(); var disposalEntered = Gate(); var disposalRelease = Gate(); var joined = false;
        fixture.Runner.Work = async (_, _) => { entered.TrySetResult(); try { await processRelease.Task; return Receipt("late.txt\n", "", 0); } finally { joined = true; } };
        fixture.Spills.Dispose = async () => { disposalEntered.TrySetResult(); await disposalRelease.Task; };
        Task<ImmutableArray<string>>? original = null;
        try
        {
            original = fixture.Executor().FindAsync(Request(fixture), cancel.Token).AsTask();
            Check(await Task.WhenAny(entered.Task, original) == entered.Task, "Fd invocation settled before process entry.");
            cancel.Cancel(); Check(!original.IsCompleted && !joined, "Cancellation abandoned the original process join."); processRelease.TrySetResult();
            Check(await Task.WhenAny(disposalEntered.Task, original) == disposalEntered.Task && joined && !original.IsCompleted, "Canceled settlement overtook spill disposal.");
            disposalRelease.TrySetResult(); var canceled = await Throws<OperationCanceledException>(() => original ?? throw new InvalidOperationException("Original was not started."));
            Check(canceled.CancellationToken == cancel.Token && fixture.Spills.Last!.Disposed, "Caller cancellation identity or owned spill disposal was lost.");
        }
        finally
        {
            try { cancel.Cancel(); }
            finally
            {
                processRelease.TrySetResult(); disposalRelease.TrySetResult();
                if (original is not null) { try { await original; } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { } }
            }
        }
    }
    private static async Task CancellationDuringSuccessfulCleanup()
    {
        using var fixture = new Fixture(); using var cancel = new CancellationTokenSource();
        var disposalEntered = Gate(); var disposalRelease = Gate();
        fixture.Runner.Result = Receipt("valid.txt\n", "", 0);
        fixture.Spills.Dispose = async () => { disposalEntered.TrySetResult(); await disposalRelease.Task; };
        Task<ImmutableArray<string>>? original = null;
        try
        {
            original = fixture.Executor().FindAsync(Request(fixture), cancel.Token).AsTask();
            Check(await Task.WhenAny(disposalEntered.Task, original) == disposalEntered.Task && !original.IsCompleted,
                "Successful invocation settled before held spill disposal.");
            Check(fixture.Runner.Calls == 1 && !fixture.Spills.Last!.Disposed, "Original process receipt or held cleanup state changed.");
            cancel.Cancel(); Check(!original.IsCompleted, "Cleanup cancellation abandoned original spill disposal.");
            disposalRelease.TrySetResult();
            var canceled = await Throws<OperationCanceledException>(() => original ?? throw new InvalidOperationException("Original was not started."));
            Check(canceled.CancellationToken == cancel.Token && fixture.Spills.Last!.Disposed,
                "Cancellation during successful cleanup returned paths or lost caller token/completed disposal.");
        }
        finally
        {
            try { cancel.Cancel(); }
            finally
            {
                disposalRelease.TrySetResult();
                if (original is not null) { try { await original; } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { } }
            }
        }
    }
    private static async Task LocalSpill()
    {
        using var fixture = new Fixture(); var factory = new LocalFdSpillLeaseFactory(fixture.Root);
        var lease = await factory.CreateAsync(default);
        try { await File.WriteAllTextAsync(lease.SpillPath, "owned mixed output"); }
        finally { await lease.DisposeAsync(); }
        var repeated = lease.DisposeAsync().AsTask(); await repeated;
        Check(!Directory.Exists(Path.GetDirectoryName(lease.SpillPath)), "Owned spill directory remained after disposal.");
    }
    private sealed class Runner : ISeparatedProcessRunner
    {
        public int Calls; public ProcessRequest? Last; public SeparatedProcessRunResult Result = Receipt("", "", 0);
        public Func<ProcessRequest, CancellationToken, ValueTask<SeparatedProcessRunResult>>? Work;
        public ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Fd used merged stdout/stderr.");
        public ValueTask<SeparatedProcessRunResult> RunSeparatedAsync(ProcessRequest request, CancellationToken token = default)
        { Calls++; Last = request; return Work?.Invoke(request, token) ?? ValueTask.FromResult(Result); }
    }
    private sealed class Spills(string root) : IFdSpillLeaseFactory
    {
        public string Root => root;
        public int Calls; public Lease? Last; public Func<ValueTask>? Dispose;
        public ValueTask<IFdSpillLease> CreateAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; Last = new(Path.Combine(root, "spill.log"), Dispose); return ValueTask.FromResult<IFdSpillLease>(Last); }
    }
    private sealed class Lease(string path, Func<ValueTask>? dispose) : IFdSpillLease
    {
        public string SpillPath => path; public bool Disposed;
        public async ValueTask DisposeAsync() { if (dispose is not null) await dispose(); Disposed = true; }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; } public readonly byte[] BinaryBytes = Encoding.UTF8.GetBytes("authored nonexecutable fd descriptor fixture");
        public FdBinaryDescriptor Binary { get; } public readonly Runner Runner = new(); public Spills Spills { get; }
        public bool Admitted = true; public FdRepositoryPresence Repository = FdRepositoryPresence.OutsideGitRepository;
        public Fixture()
        {
            Root = Path.Combine(_parent, "pisharp-fd-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, "authored-fd.exe"); File.WriteAllBytes(path, BinaryBytes);
            Binary = new(path, BinaryBytes.Length, Convert.ToHexStringLower(SHA256.HashData(BinaryBytes)), "authored-fixture", "win-x64", "explicit-authored-nonexecution"); Spills = new(Root);
        }
        public FdFindExecutor Executor() => new(Binary, Root, ImmutableDictionary<string, string>.Empty, Runner,
            (descriptor, token) => { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Admitted && descriptor == Binary); },
            (_, token) => { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Repository); }, Spills);
        public void Dispose()
        { if (Path.GetDirectoryName(Root) != _parent || !Path.GetFileName(Root).StartsWith("pisharp-fd-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid owned fixture root."); Directory.Delete(Root, recursive: true); }
    }
    private sealed class Permit : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(true));
    }
    private static FindExecutionRequest Request(Fixture fixture, string pattern = "*.txt", int limit = 1000) => new(pattern, fixture.Root, limit, [], FindTool.MaximumResults, FindTool.MaximumResultPathCharacters);
    private static SeparatedProcessRunResult Receipt(string stdout, string stderr, int code)
    {
        var merged = stdout + stderr; var truncation = ToolOutputTruncator.Tail(merged);
        var original = new ProcessRunResult(code == 0 ? ProcessRunStatus.Exited : ProcessRunStatus.NonZeroExit, code, 1, true, true, true,
            new(truncation.Content, truncation, Encoding.UTF8.GetByteCount(merged), 0, null), new(merged, false), 0, []);
        return new(original, new(stdout, false), new(stderr, false));
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Names(IEnumerable<string> expected, IEnumerable<string> actual) => Check(expected.SequenceEqual(actual), "Fd argv differs from pinned source.");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<T> Throws<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
