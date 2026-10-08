using System.Collections.Immutable;
using System.Threading.Channels;
using PiSharp.Agent.Tools;

namespace PiSharp.Tools.Processes.Unix;

/// <summary>Bounded Unix capture behind explicit atomic process-group admission. CleanupConfirmed refers
/// to the admitted group and joined owned pipes/storage; it is not a process-tree containment receipt.</summary>
public sealed class UnixProcessRunner : ISeparatedProcessRunner
{
    private readonly IUnixProcessAdmission _admission;
    private readonly ProcessRunnerOptions _options;
    private readonly IProcessOutputStorage _storage;
    private readonly TimeProvider _clock;
    public UnixProcessRunner(IUnixProcessAdmission admission, ProcessRunnerOptions? options = null,
        IProcessOutputStorage? outputStorage = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(admission);
        _admission = admission; _options = options ?? new(); _storage = outputStorage ?? new LocalProcessOutputStorage();
        _clock = timeProvider ?? TimeProvider.System;
        if (_options.MaximumRawBytes is < 1 or > 64 * 1024 * 1024 ||
            _options.ModelMaxLines is < 1 or > 2000 || _options.ModelMaxBytes is < 1 or > 50 * 1024 ||
            _options.StructuredMaxBytes < _options.ModelMaxBytes || _options.StructuredMaxBytes > 1024 * 1024 ||
            _options.ReadChunkBytes is < 1 or > 64 * 1024 || _options.QueueChunks is < 1 or > 64 ||
            _options.PostExitIdleMilliseconds is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public UnixProcessOperation Start(ProcessRequest request, ProcessOutputCallback? onUpdate = null,
        CancellationToken cancellationToken = default) => StartCore(request, onUpdate, null, cancellationToken);
    public async ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null,
        CancellationToken cancellationToken = default) => await Start(request, onUpdate, cancellationToken).Completion.ConfigureAwait(false);
    public async ValueTask<SeparatedProcessRunResult> RunSeparatedAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        using var capture = new SeparatedProcessCapture(_options.StructuredMaxBytes);
        return capture.Result(await StartCore(request, null, capture, cancellationToken).Completion.ConfigureAwait(false));
    }
    private UnixProcessOperation StartCore(ProcessRequest request, ProcessOutputCallback? onUpdate,
        SeparatedProcessCapture? capture, CancellationToken caller) => new(operation => RunOwnedAsync(operation, request, onUpdate, capture, caller));

    private async Task<ProcessRunResult> RunOwnedAsync(UnixProcessOperation operation, ProcessRequest request,
        ProcessOutputCallback? onUpdate, SeparatedProcessCapture? capture, CancellationToken caller)
    {
        var began = _clock.GetTimestamp();
        if (caller.IsCancellationRequested) return Empty(ProcessRunStatus.Canceled, null);
        if (!Valid(request)) return Empty(ProcessRunStatus.Failed, ProcessDiagnostic.InvalidRequest);
        var diagnostics = new HashSet<ProcessDiagnostic>();
        var gate = new object();
        var complete = true;
        var stop = new TaskCompletionSource<ProcessRunStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new ShellOutputAccumulator(request.SpillPath, _options, _storage);
        IUnixProcessLease? lease = null;
        ITimer? timer = null;
        var cleaned = false;
        int? exitCode = null;
        var status = ProcessRunStatus.Exited;
        using var reads = new CancellationTokenSource();
        using var registration = caller.Register(() => stop.TrySetResult(ProcessRunStatus.Canceled));
        try
        {
            // Acquire the timer before admission; do not let an effectful timer factory strand a process.
            if (request.TimeoutSeconds is not null)
                timer = _clock.CreateTimer(_ => stop.TrySetResult(ProcessRunStatus.TimedOut), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var launch = operation.Track(_admission.LaunchAsync(request, caller).AsTask());
            lease = await launch.ConfigureAwait(false);
            if (lease is null) throw new InvalidOperationException("Admission returned no lease.");
        }
        catch (Exception error)
        {
            operation.Fault(error);
            Add(ProcessDiagnostic.SpawnFailed);
            await FinishOutputAsync().ConfigureAwait(false);
            if (timer is not null) await ObserveValueAsync(() => timer.DisposeAsync(), ProcessDiagnostic.CleanupFailed).ConfigureAwait(false);
            return Result(caller.IsCancellationRequested ? ProcessRunStatus.Canceled : ProcessRunStatus.Failed);
        }

        var exit = operation.Track(lease.Exit);
        var chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_options.QueueChunks)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        var collect = operation.Track(CollectAsync());
        var stdout = operation.Track(PumpAsync(lease.StandardOutput, false));
        var stderr = operation.Track(PumpAsync(lease.StandardError, true));
        var pumps = operation.Track(CompletePumpsAsync());
        var natural = operation.Track(NaturalDrainAsync());
        try
        {
            if (request.TimeoutSeconds is { } seconds) timer!.Change(TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
            var winner = await Task.WhenAny(stop.Task, natural).ConfigureAwait(false);
            if (winner == stop.Task) status = await stop.Task.ConfigureAwait(false);
            else await natural.ConfigureAwait(false);
        }
        catch (Exception error) { operation.Fault(error); Add(ProcessDiagnostic.LifecycleCallbackFailed); }

        // Physical group stop precedes CancelAsync, disposal of pipe reads and every pump/callback join.
        try { cleaned = await operation.Track(lease.StopGroupAsync().AsTask()).ConfigureAwait(false); }
        catch (Exception error) { operation.Fault(error); Add(ProcessDiagnostic.CleanupFailed); }
        if (!cleaned) Add(ProcessDiagnostic.CleanupFailed);
        // Give already-readable final bytes a bounded opportunity to drain after physical stop.
        var grace = operation.Track(Task.Delay(TimeSpan.FromMilliseconds(_options.PostExitIdleMilliseconds), _clock));
        if (await Task.WhenAny(pumps, grace).ConfigureAwait(false) != pumps)
        {
            complete = false;
            // A cancellation registration can wait for its read, which may require physical pipe disposal.
            // Invoke all three stops independently: even a synchronous first disposal/callback must not
            // prevent the other physical stop from starting. Retain actual originals inside each invocation.
            var stdoutStop = StartPipeStop(() => lease.StandardOutput.DisposeAsync());
            var stderrStop = StartPipeStop(() => lease.StandardError.DisposeAsync());
            var readCancellation = StartPipeStop(() => new ValueTask(reads.CancelAsync()));
            await ObserveAsync(operation.Track(Task.WhenAll(stdoutStop, stderrStop, readCancellation)),
                ProcessDiagnostic.CleanupFailed).ConfigureAwait(false);
        }
        await ObserveAsync(pumps, ProcessDiagnostic.OutputIoFailed).ConfigureAwait(false);
        await ObserveAsync(natural, ProcessDiagnostic.OutputIoFailed).ConfigureAwait(false);
        await ObserveAsync(collect, ProcessDiagnostic.OutputIoFailed).ConfigureAwait(false);
        await ObserveAsync(grace, ProcessDiagnostic.CleanupFailed).ConfigureAwait(false);
        await ObserveAsync(exit, ProcessDiagnostic.CleanupFailed).ConfigureAwait(false);
        if (exit.IsCompletedSuccessfully) exitCode = exit.Result;
        await FinishOutputAsync().ConfigureAwait(false);
        await ObserveValueAsync(() => lease.DisposeAsync(), ProcessDiagnostic.CleanupFailed).ConfigureAwait(false);
        if (timer is not null) await ObserveValueAsync(() => timer.DisposeAsync(), ProcessDiagnostic.CleanupFailed).ConfigureAwait(false);
        if (diagnostics.Count > 0) status = ProcessRunStatus.Failed;
        else if (caller.IsCancellationRequested) status = ProcessRunStatus.Canceled;
        else if (status != ProcessRunStatus.TimedOut) status = exitCode == 0 ? ProcessRunStatus.Exited : ProcessRunStatus.NonZeroExit;
        return Result(status);

        void Add(ProcessDiagnostic diagnostic) { lock (gate) diagnostics.Add(diagnostic); }
        ProcessRunResult Result(ProcessRunStatus outcome) => new(outcome, exitCode, lease?.ProcessId, lease is not null,
            cleaned && !diagnostics.Contains(ProcessDiagnostic.CleanupFailed), complete, output.Snapshot(), output.Structured(),
            _clock.GetElapsedTime(began).TotalSeconds, diagnostics.ToImmutableArray());
        async Task ObserveAsync(Task task, ProcessDiagnostic diagnostic)
        {
            _ = operation.Track(task);
            try { await task.ConfigureAwait(false); }
            catch (Exception error) { operation.FaultTask(task, error); Add(diagnostic); }
        }
        Task StartPipeStop(Func<ValueTask> action) => operation.Track(Task.Run(() =>
            ObserveValueAsync(action, ProcessDiagnostic.CleanupFailed)));
        async Task ObserveValueAsync(Func<ValueTask> action, ProcessDiagnostic diagnostic)
        {
            try { await ObserveAsync(action().AsTask(), diagnostic).ConfigureAwait(false); }
            catch (Exception error) { operation.Fault(error); Add(diagnostic); }
        }
        async Task FinishOutputAsync()
        {
            await ObserveValueAsync(() => output.FinishAsync(), ProcessDiagnostic.OutputIoFailed).ConfigureAwait(false);
            await ObserveValueAsync(() => output.DisposeAsync(), ProcessDiagnostic.CleanupFailed).ConfigureAwait(false);
        }
        async Task PumpAsync(Stream stream, bool standardError)
        {
            try
            {
                while (true)
                {
                    var bytes = new byte[_options.ReadChunkBytes];
                    var originalRead = stream.ReadAsync(bytes, reads.Token).AsTask();
                    int read;
                    try { read = await originalRead.ConfigureAwait(false); }
                    catch (Exception error)
                    {
                        _ = operation.Track(originalRead);
                        // Cancellation is a state of the original task, not an exception-type guess.
                        if (originalRead.IsCanceled && reads.IsCancellationRequested &&
                            error is OperationCanceledException canceledRead && canceledRead.CancellationToken == reads.Token) return;
                        operation.FaultTask(originalRead, error);
                        complete = false; Add(ProcessDiagnostic.OutputIoFailed); stop.TrySetResult(ProcessRunStatus.Failed);
                        return;
                    }
                    if (read == 0) break;
                    if (read != bytes.Length) Array.Resize(ref bytes, read);
                    capture?.Append(standardError, bytes);
                    var originalWrite = chunks.Writer.WriteAsync(bytes, reads.Token).AsTask();
                    try { await originalWrite.ConfigureAwait(false); }
                    catch (Exception error)
                    {
                        _ = operation.Track(originalWrite);
                        if (originalWrite.IsCanceled && reads.IsCancellationRequested &&
                            error is OperationCanceledException canceledWrite && canceledWrite.CancellationToken == reads.Token) return;
                        operation.FaultTask(originalWrite, error);
                        complete = false; Add(ProcessDiagnostic.OutputIoFailed); stop.TrySetResult(ProcessRunStatus.Failed);
                        return;
                    }
                }
            }
            catch (Exception error) { operation.Fault(error); complete = false; Add(ProcessDiagnostic.OutputIoFailed); stop.TrySetResult(ProcessRunStatus.Failed); }
        }
        async Task CompletePumpsAsync()
        {
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            finally { chunks.Writer.TryComplete(); }
        }
        async Task NaturalDrainAsync()
        {
            await exit.ConfigureAwait(false);
            // Escaped descendants can retain a pipe forever: group stop follows a finite post-exit drain.
            var idle = operation.Track(Task.Delay(TimeSpan.FromMilliseconds(_options.PostExitIdleMilliseconds), _clock));
            await Task.WhenAny(pumps, idle).ConfigureAwait(false);
            await idle.ConfigureAwait(false);
        }
        async Task CollectAsync()
        {
            var accepting = true;
            await foreach (var bytes in chunks.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (!accepting) continue;
                try { await output.AppendAsync(bytes).ConfigureAwait(false); }
                catch (Exception error)
                {
                    operation.Fault(error); complete = false; accepting = false;
                    Add(error is ProcessOutputLimitException ? ProcessDiagnostic.OutputLimitExceeded : ProcessDiagnostic.OutputIoFailed);
                    stop.TrySetResult(ProcessRunStatus.Failed);
                }
                if (accepting && onUpdate is not null)
                {
                    var failed = false;
                    var snapshot = output.Snapshot();
                    foreach (ProcessOutputCallback subscriber in onUpdate.GetInvocationList())
                    {
                        Task? original = null;
                        try
                        {
                            original = operation.Track(subscriber(snapshot).AsTask());
                            await original.ConfigureAwait(false);
                        }
                        catch (Exception error)
                        {
                            if (original is null) operation.Fault(error);
                            else operation.FaultTask(original, error);
                            failed = true; Add(ProcessDiagnostic.ProgressCallbackFailed); stop.TrySetResult(ProcessRunStatus.Failed);
                        }
                    }
                    if (failed) onUpdate = null;
                }
            }
        }
        ProcessRunResult Empty(ProcessRunStatus outcome, ProcessDiagnostic? diagnostic) => new(outcome, null, null, false, true, true,
            new(string.Empty, ToolOutputTruncator.Tail(string.Empty, new(_options.ModelMaxLines, _options.ModelMaxBytes)), 0, 0, null),
            new(string.Empty, false), 0, diagnostic is { } code ? [code] : []);
    }

    private static bool Valid(ProcessRequest? request)
    {
        if (request is null || request.Arguments.IsDefault || request.Arguments.Length > 256 || request.Environment is null || request.Environment.Count > 1024 ||
            !Absolute(request.Executable) || !Absolute(request.WorkingDirectory) || !Absolute(request.SpillPath) ||
            request.TimeoutSeconds is { } seconds && (!double.IsFinite(seconds) || seconds <= 0 || seconds * 1000 > int.MaxValue)) return false;
        long characters = request.Executable.Length + request.WorkingDirectory.Length + request.SpillPath.Length;
        foreach (var argument in request.Arguments) { if (!Text(argument)) return false; characters += argument.Length; }
        foreach (var (key, value) in request.Environment)
        { if (string.IsNullOrEmpty(key) || key.Contains('=') || !Text(key) || !Text(value)) return false; characters += key.Length + value.Length; }
        return characters <= 128 * 1024;
        static bool Absolute(string? path) => Text(path) && path!.StartsWith('/') && path.Length <= 4096;
        static bool Text(string? value)
        {
            if (value is null || value.Contains('\0')) return false;
            for (var index = 0; index < value.Length; index++)
                if (char.IsHighSurrogate(value[index]))
                { if (++index == value.Length || !char.IsLowSurrogate(value[index])) return false; }
                else if (char.IsLowSurrogate(value[index])) return false;
            return true;
        }
    }
}
