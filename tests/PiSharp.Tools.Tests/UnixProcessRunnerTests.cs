using System.Collections.Immutable;
using System.Text;
using PiSharp.Tools.Processes;
using PiSharp.Tools.Processes.Unix;

internal static class UnixProcessRunnerTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("Unix admitted lease physical stop precedes cancellation and pipe joins", StopBeforeCancellation),
        ("Unix held original callback remains owned and preserves its fault", HeldCallbackFault),
        ("Unix output budget stops the group and retains bounded output", OutputBudget),
        ("Unix owned spill disposal remains joined after physical stop", HeldSpillDisposal),
        ("Unix multicast earlier subscriber is joined and all original faults survive", MulticastOriginalOwnership),
        ("Unix faulted read OCE remains a fault after cleanup cancellation", FaultedReadCancellation),
        ("Unix physical pipe stops precede held cancellation joins and retain every fault", PipeStopBeforeCancellationJoin)
    ];

    private static ProcessRequest Request() => new("/admitted/program", [], "/admitted/work",
        ImmutableDictionary<string, string>.Empty, "/admitted/spill");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static async Task StopBeforeCancellation()
    {
        var pipe = new HeldPipe();
        var lease = new Lease(pipe, new MemoryStream());
        var admission = new Admission(lease);
        using var caller = new CancellationTokenSource();
        var operation = new UnixProcessRunner(admission, outputStorage: new Storage()).Start(Request(), cancellationToken: caller.Token);
        await pipe.Started.Task;
        await caller.CancelAsync();
        await lease.StopEntered.Task;
        Check(!operation.Completion.IsCompleted && !pipe.CancellationObserved,
            "Original operation or read escaped while physical group stop was held.");
        Check(ReferenceEquals(operation.Completion, operation.Completion), "Completion task was replaced.");
        lease.ReleaseStop.TrySetResult(true);
        var result = await operation.Completion;
        Check(pipe.CancellationObserved && result.CleanupConfirmed && lease.Disposed,
            "Stop receipt did not precede joined cancellation and cleanup.");
        Check(operation.OriginalTasks.All(task => task.IsCompleted), "Original task was abandoned.");
    }

    private static async Task HeldCallbackFault()
    {
        var lease = new Lease(new MemoryStream(Encoding.UTF8.GetBytes("one\n")), new MemoryStream());
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        var operation = new UnixProcessRunner(new Admission(lease), outputStorage: new Storage()).Start(Request(), _ =>
        { callbackEntered.TrySetResult(); return new ValueTask(callback.Task); }, caller.Token);
        await callbackEntered.Task;
        await caller.CancelAsync();
        await lease.StopEntered.Task;
        lease.ReleaseStop.TrySetResult(true);
        await lease.Exit;
        Check(!operation.Completion.IsCompleted, "Held original callback was abandoned.");
        var fault = new InvalidOperationException("original callback fault");
        callback.TrySetException(fault);
        var result = await operation.Completion;
        Check(operation.OriginalTasks.Any(task => ReferenceEquals(task, callback.Task)), "Original callback task was replaced.");
        Check(operation.OriginalFaults.Any(error => ReferenceEquals(error, fault)), "Original callback fault was replaced.");
        Check(result.Diagnostics.Contains(ProcessDiagnostic.ProgressCallbackFailed) && result.CleanupConfirmed,
            "Callback failure erased the independent physical cleanup receipt.");
    }

    private static async Task OutputBudget()
    {
        var lease = new Lease(new MemoryStream(Encoding.UTF8.GetBytes("oversize-output")), new MemoryStream());
        lease.ReleaseStop.TrySetResult(true);
        var options = new ProcessRunnerOptions(MaximumRawBytes: 3, ModelMaxBytes: 2, StructuredMaxBytes: 4, ReadChunkBytes: 2, QueueChunks: 1);
        var operation = new UnixProcessRunner(new Admission(lease), options, new Storage()).Start(Request());
        var result = await operation.Completion;
        Check(result.Diagnostics.Contains(ProcessDiagnostic.OutputLimitExceeded) && !result.CapturedOutputComplete &&
            result.Output.RawBytes <= 3 && result.CleanupConfirmed && lease.Disposed, "Output bound or group cleanup was lost.");
        Check(operation.OriginalTasks.All(task => task.IsCompleted), "Budget stop abandoned original work.");
    }

    private static async Task HeldSpillDisposal()
    {
        var lease = new Lease(new MemoryStream(Encoding.UTF8.GetBytes("spill-bytes")), new MemoryStream());
        lease.ReleaseStop.TrySetResult(true);
        lease.ExitCommand();
        var spill = new HeldSpill();
        var options = new ProcessRunnerOptions(ModelMaxBytes: 2, StructuredMaxBytes: 4, ReadChunkBytes: 2);
        var operation = new UnixProcessRunner(new Admission(lease), options, new Storage(spill)).Start(Request());
        await spill.DisposeEntered.Task;
        Check(!operation.Completion.IsCompleted && lease.StopEntered.Task.IsCompleted,
            "Spill cleanup escaped ownership or preceded physical stop.");
        spill.ReleaseDispose.TrySetResult();
        var result = await operation.Completion;
        Check(result.CleanupConfirmed && result.Output.FullOutputPath == Request().SpillPath && spill.Closed,
            "Created spill identity or joined disposal was lost.");
    }

    private sealed class HeldSpill : MemoryStream
    {
        public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed { get; private set; }
        public override async ValueTask DisposeAsync()
        { DisposeEntered.TrySetResult(); await ReleaseDispose.Task; await base.DisposeAsync(); Closed = true; }
    }

    private static async Task MulticastOriginalOwnership()
    {
        var lease = new Lease(new MemoryStream(Encoding.UTF8.GetBytes("one")), new MemoryStream());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var earlier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterCalls = 0;
        var firstFault = new InvalidOperationException("earlier fault one");
        var secondFault = new IOException("earlier fault two");
        var laterFault = new InvalidOperationException("later synchronous fault");
        ProcessOutputCallback subscribers = _ => { entered.TrySetResult(); return new ValueTask(earlier.Task); };
        subscribers += _ => { laterCalls++; throw laterFault; };
        using var caller = new CancellationTokenSource();
        var operation = new UnixProcessRunner(new Admission(lease), outputStorage: new Storage()).Start(Request(), subscribers, caller.Token);
        await entered.Task;
        await caller.CancelAsync();
        await lease.StopEntered.Task;
        lease.ReleaseStop.TrySetResult(true);
        await lease.Exit;
        Check(!operation.Completion.IsCompleted && laterCalls == 0,
            "Earlier subscriber was discarded or subscriber order changed.");
        earlier.TrySetException(new Exception[] { firstFault, secondFault });
        var result = await operation.Completion;
        Check(laterCalls == 1 && operation.OriginalTasks.Any(task => ReferenceEquals(task, earlier.Task)),
            "Earlier original task or later subscriber was lost.");
        foreach (var fault in new Exception[] { firstFault, secondFault, laterFault })
            Check(operation.OriginalFaults.Any(error => ReferenceEquals(error, fault)), "A multicast original fault was replaced or dropped.");
        Check(result.CleanupConfirmed && result.Diagnostics.Contains(ProcessDiagnostic.ProgressCallbackFailed),
            "Independent physical cleanup or multicast failure receipt was lost.");
    }

    private static async Task FaultedReadCancellation()
    {
        var firstFault = new OperationCanceledException("foreign cancellation fault", new CancellationToken(true));
        var secondFault = new IOException("second original read fault");
        var pipe = new FaultedCancellationPipe(firstFault, secondFault);
        var lease = new Lease(pipe, new MemoryStream());
        using var caller = new CancellationTokenSource();
        var operation = new UnixProcessRunner(new Admission(lease), outputStorage: new Storage()).Start(Request(), cancellationToken: caller.Token);
        await pipe.Started.Task;
        await caller.CancelAsync();
        await lease.StopEntered.Task;
        Check(!operation.Completion.IsCompleted && !pipe.Original.IsCompleted,
            "Held original read escaped physical-stop ownership.");
        lease.ReleaseStop.TrySetResult(true);
        var result = await operation.Completion;
        Check(pipe.Original.IsFaulted && result.Diagnostics.Contains(ProcessDiagnostic.OutputIoFailed) && !result.CapturedOutputComplete,
            "A faulted original read was mistaken for acknowledged cleanup cancellation.");
        Check(operation.OriginalTasks.Any(task => ReferenceEquals(task, pipe.Original)), "Faulted original read identity was lost.");
        Check(operation.OriginalFaults.Any(error => ReferenceEquals(error, firstFault)) &&
            operation.OriginalFaults.Any(error => ReferenceEquals(error, secondFault)), "Complete read faults were lost.");
    }

    private sealed class FaultedCancellationPipe(Exception first, Exception second) : Stream
    {
        private readonly TaskCompletionSource<int> _original = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        public Task<int> Original => _original.Task;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _registration = cancellationToken.Register(() => { _original.TrySetException(new[] { first, second }); _canceled.TrySetResult(); });
            Started.TrySetResult(); return new ValueTask<int>(_original.Task);
        }
        public override async ValueTask DisposeAsync() { await _canceled.Task; _registration.Dispose(); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task PipeStopBeforeCancellationJoin()
    {
        var control = new PipeStopControl();
        var stdout = new StopDependentPipe(control, standardError: false);
        var stderr = new StopDependentPipe(control, standardError: true);
        var lease = new Lease(stdout, stderr);
        using var caller = new CancellationTokenSource();
        var operation = new UnixProcessRunner(new Admission(lease), outputStorage: new Storage()).Start(Request(), cancellationToken: caller.Token);
        try
        {
            await control.ReadEntered.Task;
            await caller.CancelAsync();
            await lease.StopEntered.Task;
            Check(!control.StdoutDisposeEntered.Task.IsCompleted && !control.CancellationEntered.Task.IsCompleted,
                "Pipe stop or read cancellation preceded physical group stop.");
            lease.ReleaseStop.TrySetResult(true);
            await control.StdoutDisposeEntered.Task;
            await control.StderrDisposeEntered.Task;
            await control.CancellationEntered.Task;
            Check(!operation.Completion.IsCompleted && !control.ReadOriginal.Task.IsCompleted,
                "A held cancellation callback or synchronous first disposal escaped ownership.");
            // The read only completes through physical disposal; cancellation alone cannot unblock it.
            control.ReleaseRead.TrySetResult();
            await control.ReadOriginal.Task;
            Check(!operation.Completion.IsCompleted, "Completion escaped held original cancellation/disposal work.");
            control.ReleaseCancellation.TrySetResult();
            control.StdoutDisposeOriginal.TrySetException(control.StdoutFaults);
            control.StderrDisposeOriginal.TrySetException(control.StderrFaults);
            var result = await operation.Completion;
            Check(!result.CleanupConfirmed && result.Diagnostics.Contains(ProcessDiagnostic.CleanupFailed),
                "Physical stop faults were hidden by the group stop receipt.");
            Check(operation.OriginalTasks.Any(task => ReferenceEquals(task, control.StdoutDisposeOriginal.Task)) &&
                operation.OriginalTasks.Any(task => ReferenceEquals(task, control.StderrDisposeOriginal.Task)),
                "Original pipe disposal tasks were replaced.");
            foreach (var fault in control.StdoutFaults.Concat(control.StderrFaults).Concat(control.CancellationFaults))
                Check(operation.OriginalFaults.Any(error => ReferenceEquals(error, fault)), "An original stop/cancellation fault was dropped.");
        }
        finally
        {
            // All gates release even on an assertion failure; join the real operation before leaving.
            lease.ReleaseStop.TrySetResult(true);
            await caller.CancelAsync();
            control.ReleaseRead.TrySetResult();
            control.ReleaseCancellation.TrySetResult();
            control.StdoutDisposeOriginal.TrySetResult();
            control.StderrDisposeOriginal.TrySetResult();
            await operation.Completion;
            stdout.ReleaseRegistration();
        }
    }

    private sealed class PipeStopControl
    {
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<int> ReadOriginal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StdoutDisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StderrDisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StdoutDisposeOriginal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StderrDisposeOriginal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception[] StdoutFaults { get; } = [new IOException("stdout stop one"), new IOException("stdout stop two")];
        public Exception[] StderrFaults { get; } = [new IOException("stderr stop one"), new IOException("stderr stop two")];
        public Exception[] CancellationFaults { get; } = [new InvalidOperationException("cancel callback one"), new IOException("cancel callback two")];
    }

    private sealed class StopDependentPipe(PipeStopControl control, bool standardError) : Stream
    {
        private CancellationTokenRegistration _registration;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (standardError) return ValueTask.FromResult(0);
            _registration = cancellationToken.Register(() =>
            {
                control.CancellationEntered.TrySetResult();
                control.ReadOriginal.Task.GetAwaiter().GetResult();
                control.ReleaseCancellation.Task.GetAwaiter().GetResult();
                throw new AggregateException(control.CancellationFaults);
            });
            control.ReadEntered.TrySetResult();
            return new ValueTask<int>(control.ReadOriginal.Task);
        }
        public override ValueTask DisposeAsync()
        {
            if (standardError)
            {
                control.StderrDisposeEntered.TrySetResult();
                return new ValueTask(control.StderrDisposeOriginal.Task);
            }
            control.StdoutDisposeEntered.TrySetResult();
            // Deliberately synchronous: the second stop must start independently of this invocation.
            control.StderrDisposeEntered.Task.GetAwaiter().GetResult();
            control.ReleaseRead.Task.GetAwaiter().GetResult();
            control.ReadOriginal.TrySetResult(0);
            return new ValueTask(control.StdoutDisposeOriginal.Task);
        }
        public void ReleaseRegistration() => _registration.Dispose();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Admission(Lease lease) : IUnixProcessAdmission
    {
        public ValueTask<IUnixProcessLease> LaunchAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IUnixProcessLease>(lease);
    }
    private sealed class Storage(Stream? stream = null) : IProcessOutputStorage
    { public ValueTask<Stream> CreateNewAsync(string absolutePath) => ValueTask.FromResult<Stream>(stream ?? new MemoryStream()); }
    private sealed class Lease(Stream stdout, Stream stderr) : IUnixProcessLease
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task<bool>? _stop;
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProcessId => 123;
        public Stream StandardOutput => stdout;
        public Stream StandardError => stderr;
        public Task<int> Exit => _exit.Task;
        public bool Disposed { get; private set; }
        public void ExitCommand() => _exit.TrySetResult(0);
        public ValueTask<bool> StopGroupAsync() => new(_stop ??= StopAsync());
        private async Task<bool> StopAsync()
        { StopEntered.TrySetResult(); var confirmed = await ReleaseStop.Task; _exit.TrySetResult(0); return confirmed; }
        public async ValueTask DisposeAsync()
        { await StopGroupAsync(); await stdout.DisposeAsync(); await stderr.DisposeAsync(); Disposed = true; }
    }
    private sealed class HeldPipe : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var held = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => { CancellationObserved = true; held.TrySetCanceled(cancellationToken); });
            Started.TrySetResult(); return await held.Task;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
