using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;
using PiSharp.Tools.Processes;

internal static class BashProgressDeliveryTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("Bash source-mode actual receipts pace every large snapshot under unchanged outstanding quota", LargePacedDelivery),
        ("Bash source-mode cancellation joins admitted receipt and actual runner cleanup with known output", CancellationCleanup),
        ("Bash source-mode listener fault joins runner cleanup without later progress or successful tool end", ListenerFailureCleanup)
    ];

    private static async Task LargePacedDelivery()
    {
        using var files = new Scratch();
        using var cancel = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var initialEntered = Gate(); var initialRelease = Gate();
        var firstEntered = Gate(); var snapshotsRelease = Gate();
        var attempts = 0; var runnerEntered = false; var cleanupComplete = false;
        var snapshots = Enumerable.Range(0, 120).Select(index =>
            index.ToString("D3", CultureInfo.InvariantCulture) + ":" + new string('x', 48_000) + "\0\u6587\U0001f642\n").ToArray();
        var raw = Encoding.UTF8.GetBytes(string.Concat(snapshots));
        var structured = "head\0\u6587\U0001f642\n[owned fixture omission]\ntail\0\u6587\U0001f642\n";
        var runner = new Runner(async (request, update, token) =>
        {
            runnerEntered = true; var rawBytes = 0; ProcessOutputSnapshot? final = null;
            foreach (var text in snapshots)
            {
                token.ThrowIfCancellationRequested();
                attempts++; rawBytes += Encoding.UTF8.GetByteCount(text);
                final = TruncatedSnapshot(text, rawBytes, attempts, request.SpillPath);
                await update!(final);
            }
            await using (var spill = new FileStream(request.SpillPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 8192, FileOptions.Asynchronous))
            {
                await spill.WriteAsync(raw, CancellationToken.None);
                await spill.FlushAsync(CancellationToken.None);
            }
            cleanupComplete = true;
            return Result(final!, new(structured, true));
        });
        var reports = new List<ToolResult>(); var events = new List<AgentEvent>();
        var sink = new Sink(async (observation, _) =>
        {
            events.Add(observation);
            if (observation is not ToolExecutionUpdated update) return;
            reports.Add(update.PartialResult);
            if (update.PartialResult.Content.IsEmpty)
            {
                initialEntered.TrySetResult();
                await initialRelease.Task.WaitAsync(deadline.Token);
            }
            else
            {
                firstEntered.TrySetResult();
                await snapshotsRelease.Task.WaitAsync(deadline.Token);
            }
        });
        var tool = Make(files, runner);
        var running = Scheduler(tool).RunAsync(Message(), sink, cancel.Token);
        try
        {
            await initialEntered.Task.WaitAsync(deadline.Token);
            Check(!runnerEntered && !running.IsCompleted, "Initial receipt did not pace process acquisition.");
            Equal(1, reports.Count);
            initialRelease.TrySetResult();
            await firstEntered.Task.WaitAsync(deadline.Token);
            Equal(1, attempts); Equal(2, reports.Count);
            Check(!cleanupComplete && !running.IsCompleted, "Large output ran ahead of its actual listener receipt.");
            snapshotsRelease.TrySetResult();
            var batch = await running.WaitAsync(deadline.Token);
            Equal(snapshots.Length, attempts); Equal(snapshots.Length + 1, reports.Count);
            long aggregateCharacters = 0;
            for (var index = 0; index < snapshots.Length; index++)
            {
                var report = reports[index + 1];
                Equal(snapshots[index], report.Content.Single().Text);
                Equal(snapshots[index], report.Details.Value.GetProperty("truncation").GetProperty("content").GetString());
                Equal(files.Spill, report.Details.Value.GetProperty("fullOutputPath").GetString());
                aggregateCharacters += report.Content.Single().Text.Length + report.Details.ToString().Length;
            }
            var defaults = new ToolProgressDeliveryOptions(ToolProgressDeliveryMode.SourceCompatible);
            Equal(8 * 1024 * 1024, defaults.MaximumRetainedCharacters);
            Check(aggregateCharacters > defaults.MaximumRetainedCharacters,
                "Fixture did not cross the historical outstanding-character failure boundary.");
            Check(cleanupComplete && !batch.IsCanceled, "Paced execution did not join output cleanup.");
            var result = batch.Outcomes.Single().Result;
            Check(!result.IsError && result.Failure is null, "Bounded paced output unexpectedly lost its successful final.");
            Equal(snapshots[^1] + "\n\n[Showing lines 120-120 of 120 (50.0KB limit). Full output: " + files.Spill + "]",
                result.Content.Single().Text);
            Equal(snapshots[^1], result.Details.Value.GetProperty("truncation").GetProperty("content").GetString());
            Equal(structured, result.StructuredContent!.Value.GetProperty("output").GetString());
            Equal(files.Spill, result.StructuredContent.Value.GetProperty("full_output_path").GetString());
            Check(result.StructuredContent.Value.GetProperty("truncated").GetBoolean(), "Structured truncation metadata was dropped.");
            Equal(0, result.StructuredContent.Value.GetProperty("exit_code").GetInt32());
            var actual = await File.ReadAllBytesAsync(files.Spill);
            Check(raw.SequenceEqual(actual), "Pacing changed acknowledged raw spill bytes.");
            Equal(1, events.OfType<ToolExecutionEnded>().Count());
            var message = events.OfType<ToolResultMessageEnded>().Single().Message;
            Equal(result.Content.Single().Text, message.Content.Single().Text);
            Equal(result.Details.ToString(), message.Details.ToString());
        }
        finally
        {
            initialRelease.TrySetResult(); snapshotsRelease.TrySetResult(); cancel.Cancel();
            await JoinAsync(running);
        }
    }

    private static async Task CancellationCleanup()
    {
        using var files = new Scratch(); using var cancel = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var entered = Gate(); var release = Gate(); var cleanupEntered = Gate(); var cleanupRelease = Gate();
        var attempts = 0; var cleaned = false;
        const string known = "known\0\u6587\U0001f642\n";
        var runner = new Runner(async (_, update, token) =>
        {
            try
            {
                attempts++; await update!(Snapshot(known));
                token.ThrowIfCancellationRequested();
                attempts++; await update!(Snapshot("must not be delivered"));
                return Result(Snapshot("must not be delivered"), new("must not be delivered", false));
            }
            finally
            {
                cleanupEntered.TrySetResult();
                await cleanupRelease.Task.WaitAsync(deadline.Token);
                cleaned = true;
            }
        });
        var reports = new List<ToolResult>();
        var sink = new Sink(async (observation, _) =>
        {
            if (observation is not ToolExecutionUpdated update) return;
            reports.Add(update.PartialResult);
            if (update.PartialResult.Content.IsEmpty) return;
            entered.TrySetResult(); await release.Task.WaitAsync(deadline.Token);
        });
        var running = Scheduler(Make(files, runner)).RunAsync(Message(), sink, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token); Equal(1, attempts);
            cancel.Cancel();
            Check(!running.IsCompleted && !cleanupEntered.Task.IsCompleted,
                "Cancellation bypassed an already admitted listener receipt.");
            release.TrySetResult(); await cleanupEntered.Task.WaitAsync(deadline.Token);
            Check(!running.IsCompleted && !cleaned, "Cancellation bypassed actual runner cleanup.");
            cleanupRelease.TrySetResult();
            var batch = await running.WaitAsync(deadline.Token);
            Check(cleaned && batch.IsCanceled, "Canceled execution did not settle owned cleanup.");
            Equal(1, attempts); Equal(2, reports.Count);
            var result = batch.Outcomes.Single().Result;
            Equal(ToolFailureKind.Canceled, result.Failure?.Kind);
            Check(result.Content[0].Text.StartsWith(known + "\n\nCommand aborted", StringComparison.Ordinal),
                "Canceled receipt discarded known authoritative runner output.");
            Check(result.StructuredContent is null, "Canceled work fabricated completed structured output.");
        }
        finally
        {
            release.TrySetResult(); cleanupRelease.TrySetResult(); cancel.Cancel();
            await JoinAsync(running);
        }
    }

    private static async Task ListenerFailureCleanup()
    {
        using var files = new Scratch(); using var cancel = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var entered = Gate(); var release = Gate(); var cleanupEntered = Gate(); var cleanupRelease = Gate();
        var attempts = 0; var cleaned = false; var ends = 0;
        var failure = new IOException("owned listener failure");
        var runner = new Runner(async (_, update, _) =>
        {
            try
            {
                attempts++; await update!(Snapshot("observed\0\u6587\U0001f642\n"));
                attempts++; await update!(Snapshot("must not be delivered"));
                return Result(Snapshot("must not be delivered"), new("must not be delivered", false));
            }
            finally
            {
                cleanupEntered.TrySetResult();
                await cleanupRelease.Task.WaitAsync(deadline.Token);
                cleaned = true;
            }
        });
        var sink = new Sink(async (observation, _) =>
        {
            if (observation is ToolExecutionEnded or ToolResultMessageEnded) ends++;
            if (observation is not ToolExecutionUpdated update || update.PartialResult.Content.IsEmpty) return;
            entered.TrySetResult(); await release.Task.WaitAsync(deadline.Token);
            throw failure;
        });
        var running = Scheduler(Make(files, runner)).RunAsync(Message(), sink, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token); Equal(1, attempts);
            release.TrySetResult(); await cleanupEntered.Task.WaitAsync(deadline.Token);
            Check(!running.IsCompleted && !cleaned, "Listener failure escaped before actual runner cleanup.");
            Equal(1, attempts); Equal(0, ends);
            cleanupRelease.TrySetResult();
            try { await running.WaitAsync(deadline.Token); throw new InvalidOperationException("Expected listener failure."); }
            catch (IOException error) { Check(ReferenceEquals(failure, error), "Actual listener failure identity was replaced."); }
            Check(cleaned, "Listener failure did not join owned cleanup.");
            Equal(1, attempts); Equal(0, ends);
            Check(!File.Exists(files.Spill), "Failed publication created a fabricated completed spill.");
        }
        finally
        {
            release.TrySetResult(); cleanupRelease.TrySetResult(); cancel.Cancel();
            await JoinAsync(running);
        }
    }

    private static BashTool Make(Scratch files, Runner runner) =>
        new(runner, new(Environment.ProcessPath!, files.Root, ImmutableDictionary<string, string>.Empty, files.Root),
            () => "paced.log");
    private static ToolBatchScheduler Scheduler(BashTool tool) => new([tool.CreateDefinition(tool.CreateInvoker(new Policy()))],
        executionMode: ToolExecutionMode.Sequential,
        progressOptions: new(ToolProgressDeliveryMode.SourceCompatible));
    private static AssistantMessage Message() => new("fixture-api", "fixture-provider", "fixture-model", 0,
        [new ToolCallContent("bash-paced-call", "bash", JsonData.Parse("""{"command":"owned runner fixture"}"""))],
        TokenUsage.Zero, StopReason.ToolUse);
    private static ProcessOutputSnapshot Snapshot(string text) =>
        new(text, ToolOutputTruncator.Tail(text), Encoding.UTF8.GetByteCount(text),
            Encoding.UTF8.GetByteCount(text.Split('\n')[^1]), null);
    private static ProcessOutputSnapshot TruncatedSnapshot(string text, int rawBytes, int lines, string spillPath) =>
        new(text, ToolOutputTruncator.Tail(text) with
        {
            Truncated = true, TruncatedBy = ToolOutputTruncationLimit.Bytes,
            TotalLines = lines, TotalBytes = rawBytes, OutputLines = 1
        }, rawBytes, 0, spillPath);
    private static ProcessRunResult Result(ProcessOutputSnapshot output, ProcessStructuredOutput structured) =>
        new(ProcessRunStatus.Exited, 0, 123, true, true, true, output, structured, .2, []);
    private sealed class Runner(Func<ProcessRequest, ProcessOutputCallback?, CancellationToken, ValueTask<ProcessRunResult>> run)
        : IProcessRunner
    {
        public ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null,
            CancellationToken cancellationToken = default) => run(request, onUpdate, cancellationToken);
    }
    private sealed class Policy : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action,
            CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(new ToolActionAuthorization(true)); }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> emit) : IAgentEventSink
    {
        public ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken) =>
            emit(observation, cancellationToken);
    }
    private sealed class Scratch : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; }
        public string Spill => Path.Combine(Root, "paced.log");
        public Scratch()
        { Root = Path.Combine(_parent, "pisharp-bash-receipt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(path) != _parent ||
                !Path.GetFileName(path).StartsWith("pisharp-bash-receipt-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unowned Bash receipt fixture cleanup.");
            Directory.Delete(path, recursive: true);
        }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task JoinAsync(Task running)
    { try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { if (!running.IsCompleted) throw; } }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), "Bash receipt values differ.");
}

