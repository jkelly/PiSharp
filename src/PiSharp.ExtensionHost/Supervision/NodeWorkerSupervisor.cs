using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.ExtensionHost.Protocol;

namespace PiSharp.ExtensionHost.Supervision;

public sealed record NodeWorkerTermination(NodeWorkerStopReason Reason, int ProcessId, int ExitCode, bool KillAttempted,
    bool HasExited, int? ChildProcessId, bool ChildHasExited, ImmutableArray<byte> Stderr, long ObservedStderrBytes,
    string StderrSha256, bool StderrOverflow, WorkerProtocolFailure? ProtocolFailure, ImmutableArray<string> Failures,
    WorkerProtocolSnapshot Protocol, int ExplicitStreamCloses, bool PinsRechecked,
    ImmutableArray<byte> StdoutAfterProtocol, long ObservedStdoutAfterProtocolBytes, string StdoutAfterProtocolSha256,
    bool StdoutAfterProtocolOverflow, bool StdoutAfterProtocolEof);
public sealed class NodeWorkerSupervisorException(NodeWorkerTermination termination, Exception? cause) : IOException(
    "Owned Node worker did not settle successfully.", cause)
{ public NodeWorkerTermination Termination { get; } = termination; }
public sealed record NodeWorkerChild(int ProcessId, int ParentProcessId, long WorkerGeneration, long SessionGeneration);

/// <summary>Owns one fixed authored peer process, its streams and an accepted borrowed-I/O protocol connection.</summary>
public sealed class NodeWorkerSupervisor : IAsyncDisposable
{
    private readonly NodeWorkerLaunch _launch; private readonly Process _process; private readonly int _pid;
    private readonly FileStream _runtimePin, _entryPin; private readonly Stream _stdin, _stdout, _stderr;
    private readonly NodeExtensionWorkerLaunch.HeldPins? _extensionPins;
    private readonly WorkerProtocolConnection _protocol; private readonly object _sync = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<NodeWorkerTermination> _termination = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _force = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stdoutDrainFault = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _failures = [];
    private sealed record CleanupObservation(string Stage, WorkerProtocolSnapshot Protocol, bool InputClosed,
        bool? InputCloseCompleted, bool ProtocolCompleted, bool ExitCompleted, bool StderrCompleted,
        bool StdoutReaderJoined, bool StdoutAfterProtocolEof);
    // Six fixed transition samples at most; no per-read/write/progress trace is retained.
    private readonly List<CleanupObservation> _cleanupObservations = [];
    private readonly MemoryStream _stderrBytes; private readonly IncrementalHash _stderrHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly MemoryStream _stdoutAfterBytes;
    private readonly IncrementalHash _stdoutAfterHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly Task _exitTask, _stderrTask, _protocolTask;
    private Task<NodeWorkerChild>? _childLaunch; private Process? _child; private int? _childPid;
    private Exception? _cause; private WorkerProtocolFailure? _protocolFailure;
    private NodeWorkerStopReason _reason; private bool _closing, _killed, _exited, _stderrOverflow, _inputClosed;
    private bool _stdoutReaderJoined, _stdoutAfterOverflow, _stdoutAfterEof;
    private int _exitCode, _streamCloses; private long _stderrObserved;
    private long _stdoutAfterObserved;

    private NodeWorkerSupervisor(NodeWorkerLaunch launch, Process process, FileStream runtime, FileStream entry, WorkerRequestHandler? handler,
        NodeExtensionWorkerLaunch.HeldPins? extensionPins = null)
    {
        _launch = launch; _process = process; _pid = process.Id; _runtimePin = runtime; _entryPin = entry;
        _extensionPins = extensionPins;
        _stdin = process.StandardInput.BaseStream; _stdout = process.StandardOutput.BaseStream; _stderr = process.StandardError.BaseStream;
        _stderrBytes = new(Math.Min(launch.Options.MaximumStderrBytes, 4096));
        _stdoutAfterBytes = new(Math.Min(launch.Options.MaximumStdoutAfterProtocolBytes, 4096));
        _protocol = new(new WorkerProtocolTransport(_stdout, _stdin, launch.ProtocolOptions),
            launch.WorkerGeneration, launch.SessionGeneration, handler, launch.ProtocolOptions);
        _exitTask = ObserveExit(); _stderrTask = DrainStderr(); _protocolTask = ObserveProtocol();
    }
    public int ProcessId => _pid;
    public Task Completion => _completion.Task;
    public Task<NodeWorkerTermination> Termination => _termination.Task;
    public WorkerProtocolSnapshot Snapshot => _protocol.Snapshot;

    public static Task<NodeWorkerSupervisor> StartProbeAsync(NodeWorkerLaunch launch,
        WorkerRequestHandler? handler = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (launch.HasExtensionProfile) throw new ArgumentException("Use the separately admitted extension launch.", nameof(launch));
        return StartApprovedAsync(launch, handler, cancellationToken);
    }
    public static Task<NodeWorkerSupervisor> StartProtectedPathsAsync(NodeExtensionWorkerLaunch launch,
        CancellationToken cancellationToken = default)
    { ArgumentNullException.ThrowIfNull(launch); return StartApprovedAsync(launch.AsWorkerLaunch(), null, cancellationToken); }
    public static Task<NodeWorkerSupervisor> StartHelloAsync(NodeHelloWorkerLaunch launch,
        CancellationToken cancellationToken = default)
    { ArgumentNullException.ThrowIfNull(launch); return StartApprovedAsync(launch.AsWorkerLaunch(), null, cancellationToken); }
    public static Task<NodeWorkerSupervisor> StartCommandsAndInputAsync(NodeCommandInputWorkerLaunch launch,
        CancellationToken cancellationToken = default)
    { ArgumentNullException.ThrowIfNull(launch); return StartApprovedAsync(launch.AsWorkerLaunch(), null, cancellationToken); }
    private static async Task<NodeWorkerSupervisor> StartApprovedAsync(NodeWorkerLaunch launch,
        WorkerRequestHandler? handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch); cancellationToken.ThrowIfCancellationRequested();
        var pins = launch.AcquirePins(); Process? process = null; NodeWorkerSupervisor? owner = null; var started = false;
        NodeExtensionWorkerLaunch.HeldPins? extensionPins = null;
        try
        {
            extensionPins = launch.ExtensionProfile?.AcquireHeldPins() ?? launch.HelloProfile?.AcquireHeldPins() ?? launch.CommandInputProfile?.AcquireHeldPins();
            launch.CreateOwnedRoot();
            var info = new ProcessStartInfo(launch.NodePath)
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = launch.RunRoot,
              RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(launch.EntryPath); info.ArgumentList.Add("--worker-generation");
            info.ArgumentList.Add(launch.WorkerGeneration.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--session-generation"); info.ArgumentList.Add(launch.SessionGeneration.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--mode"); info.ArgumentList.Add(launch.ModeArgument);
            info.Environment.Clear();
            foreach (var (name, relative) in new[] { ("HOME", "home"), ("USERPROFILE", "home"), ("APPDATA", "appdata"),
                ("LOCALAPPDATA", "localappdata"), ("TMP", "temp"), ("TEMP", "temp"), ("TMPDIR", "temp") })
                info.Environment.Add(name, Path.Combine(launch.RunRoot, relative));
            var windows = Directory.GetParent(Environment.SystemDirectory)!.FullName;
            info.Environment.Add("SystemRoot", windows); info.Environment.Add("WINDIR", windows);
            launch.ExtensionProfile?.Configure(info);
            launch.HelloProfile?.Configure(info);
            launch.CommandInputProfile?.Configure(info);
            process = new() { StartInfo = info };
            if (!process.Start()) throw new IOException("Approved worker process did not start.");
            started = true; owner = new(launch, process, pins.Runtime, pins.Entry, handler, extensionPins);
            File.WriteAllText(Path.Combine(launch.RunRoot, "launch.receipt.json"), JsonSerializer.Serialize(new
            { profile = launch.ProfileName, sourceCapture = false, node = launch.NodePath,
              runtimeSha256 = NodeWorkerLaunch.RuntimeSha256, entry = launch.EntryPath, entrySha256 = launch.ApprovedEntrySha256,
              workerGeneration = launch.WorkerGeneration, sessionGeneration = launch.SessionGeneration, mode = launch.ModeArgument,
              processId = owner.ProcessId, environment = info.Environment }) + "\n");
            await owner._protocol.StartAsync().WaitAsync(TimeSpan.FromMilliseconds(launch.Options.StartupMilliseconds), cancellationToken).ConfigureAwait(false);
            var runtime = await owner.RequestAsync("probe.runtime", WorkerValue.Absent, cancellationToken: cancellationToken)
                .WaitAsync(TimeSpan.FromMilliseconds(launch.Options.StartupMilliseconds), cancellationToken).ConfigureAwait(false);
            var value = runtime.Json?.Value ?? throw new IOException("Runtime identity is absent.");
            if (value.GetProperty("version").GetString() != NodeWorkerLaunch.RuntimeVersion ||
                value.GetProperty("platform").GetString() != "win32" || value.GetProperty("architecture").GetString() != "x64" ||
                value.GetProperty("pid").GetInt32() != owner.ProcessId) throw new IOException("Runtime identity differs from the approved profile.");
            return owner;
        }
        catch (Exception failure)
        {
            if (owner is not null)
            {
                owner.Stop(NodeWorkerStopReason.Startup, failure, failure is TimeoutException ? "StartupTimeout" : "Startup");
                await owner.Completion.ConfigureAwait(false); // Abnormal startup is returned with its joined termination receipt.
            }
            else
            {
                if (started && process is not null)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                process?.Dispose(); pins.Runtime.Dispose(); pins.Entry.Dispose(); extensionPins?.Dispose();
            }
            throw;
        }
    }
    public WorkerCall StartRequest(string method, WorkerValue value, WorkerCallbackHandle? handle = null, CancellationToken cancellationToken = default)
    {
        if (method == "probe.child") throw new InvalidOperationException("Use SpawnOwnedProbeChildAsync so its exit is joined.");
        lock (_sync) { EnsureOpen(); return _protocol.StartRequest(method, value, handle, cancellationToken); }
    }
    public async Task<WorkerValue> RequestAsync(string method, WorkerValue value, WorkerCallbackHandle? handle = null,
        CancellationToken cancellationToken = default) => await StartRequest(method, value, handle, cancellationToken).Result.ConfigureAwait(false);
    public WorkerCallbackHandle RegisterCallback(string ownerId, long ownerGeneration, WorkerRequestHandler callback)
    { lock (_sync) { EnsureOpen(); return _protocol.RegisterCallback(ownerId, ownerGeneration, callback); } }
    public bool RevokeOwner(string ownerId, long ownerGeneration) => _protocol.RevokeOwner(ownerId, ownerGeneration);
    public Task<NodeWorkerChild> SpawnOwnedProbeChildAsync()
    {
        if (_launch.HasExtensionProfile) throw new InvalidOperationException("Extension launch has no child-spawn capability.");
        lock (_sync)
        {
            EnsureOpen(); if (_childLaunch is not null) throw new InvalidOperationException("One fixed child is admitted.");
            return _childLaunch = SpawnChild();
        }
    }
    private async Task<NodeWorkerChild> SpawnChild()
    {
        await Task.Yield();
        var reply = await _protocol.RequestAsync("probe.child", WorkerValue.Absent).ConfigureAwait(false);
        var id = reply.Json!.Value.GetProperty("childPid").GetInt32();
        var child = Process.GetProcessById(id);
        try
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(child.MainModule?.FileName, _launch.NodePath))
                throw new IOException("Owned child runtime path differs.");
            lock (_sync) { _child = child; _childPid = id; }
            using var deadline = new CancellationTokenSource(_launch.Options.StartupMilliseconds);
            var receiptPath = Path.Combine(_launch.RunRoot, "owned-child.json");
            while (!File.Exists(receiptPath)) await Task.Delay(5, deadline.Token).ConfigureAwait(false);
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath, deadline.Token).ConfigureAwait(false));
            var value = receipt.RootElement;
            if (value.GetProperty("pid").GetInt32() != id || value.GetProperty("parentPid").GetInt32() != _pid ||
                value.GetProperty("worker").GetInt64() != _launch.WorkerGeneration || value.GetProperty("session").GetInt64() != _launch.SessionGeneration)
                throw new IOException("Owned child receipt differs.");
            return new(id, _pid, _launch.WorkerGeneration, _launch.SessionGeneration);
        }
        catch (Exception failure)
        {
            lock (_sync) if (_child is null) child.Dispose();
            Stop(NodeWorkerStopReason.ProtocolFault, failure, "OwnedChild"); throw;
        }
    }
    public async Task ProbeLivenessAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_launch.Options.LivenessMilliseconds);
        try { _ = await RequestAsync("probe.ping", WorkerValue.Absent, cancellationToken: timeout.Token).ConfigureAwait(false); }
        catch (WorkerProtocolException failure) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { Stop(NodeWorkerStopReason.Liveness, failure, "LivenessTimeout"); await Completion.ConfigureAwait(false); throw; }
    }
    public ValueTask InvalidateSessionAsync(long replacementGeneration)
    {
        WorkerFrameCodec.CheckIdentity(replacementGeneration);
        if (replacementGeneration <= _launch.SessionGeneration) throw new ArgumentOutOfRangeException(nameof(replacementGeneration));
        Stop(NodeWorkerStopReason.Generation, new WorkerProtocolException(WorkerProtocolFailure.StaleGeneration, WorkerOutcome.Unknown), "Generation");
        _ = _protocol.InvalidateSessionAsync(replacementGeneration).AsTask(); // The same joined protocol task is observed below.
        return new(Completion);
    }
    private void EnsureOpen()
    { if (_closing) throw new WorkerProtocolException(WorkerProtocolFailure.Closed, WorkerOutcome.NotSent); }
    private void Record(Exception error, string code)
    {
        lock (_sync)
        {
            _cause ??= error;
            // Only fixed internal codes reach here; keep every distinct fault, never arbitrary exception text.
            if (!_failures.Contains(code, StringComparer.Ordinal)) _failures.Add(code);
            if (error is WorkerProtocolException protocol) _protocolFailure ??= protocol.Failure;
        }
        _force.TrySetResult();
    }
    private void Stop(NodeWorkerStopReason reason, Exception? error = null, string? code = null)
    {
        if (error is not null) Record(error, code ?? reason.ToString());
        lock (_sync) { if (_closing) return; _closing = true; _reason = reason; }
        _ = Cleanup();
    }
    private async Task ObserveExit()
    {
        await Task.Yield();
        try
        {
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_sync) { _exited = true; _exitCode = _process.ExitCode; }
            bool closing, abnormalShutdown;
            lock (_sync) { closing = _closing; abnormalShutdown = _closing && _reason == NodeWorkerStopReason.Shutdown && !_killed && _exitCode != 0; }
            if (!closing) Stop(NodeWorkerStopReason.UnexpectedExit, new IOException("Worker exited without owned shutdown."), "UnexpectedExit");
            else if (abnormalShutdown) Record(new IOException("Worker returned a nonzero shutdown exit."), "ExitCode");
        }
        catch (Exception failure) { Stop(NodeWorkerStopReason.UnexpectedExit, failure, "ExitObservation"); }
    }
    private async Task ObserveProtocol()
    {
        await Task.Yield();
        try
        {
            await _protocol.Completion.ConfigureAwait(false);
            bool closing; lock (_sync) closing = _closing;
            if (!closing) Stop(NodeWorkerStopReason.ProtocolFault, new IOException("Protocol ended without owned shutdown."), "ProtocolEnded");
        }
        catch (Exception failure) { Stop(NodeWorkerStopReason.ProtocolFault, failure, "Protocol"); }
    }
    private async Task DrainStderr()
    {
        await Task.Yield(); var buffer = new byte[_launch.Options.StderrReadBufferBytes];
        try
        {
            int count;
            while ((count = await _stderr.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) != 0)
            {
                _stderrHash.AppendData(buffer, 0, count); _stderrObserved += count;
                var retained = Math.Min(count, _launch.Options.MaximumStderrBytes - checked((int)_stderrBytes.Length));
                if (retained > 0) _stderrBytes.Write(buffer, 0, retained);
                if (_stderrObserved > _launch.Options.MaximumStderrBytes && !_stderrOverflow)
                {
                    _stderrOverflow = true;
                    Stop(NodeWorkerStopReason.StderrLimit, new IOException("Stderr byte limit exceeded."), "StderrLimit");
                }
            }
        }
        catch (Exception failure) { Stop(NodeWorkerStopReason.StderrFault, failure, "StderrRead"); }
    }
    private async Task ObserveShutdown(Task task)
    { try { await task.ConfigureAwait(false); } catch (Exception failure) { Record(failure, "ProtocolShutdown"); } }
    private void ObserveCleanup(string stage, Task? inputClose = null)
    {
        var snapshot = _protocol.Snapshot;
        lock (_sync) _cleanupObservations.Add(new(stage, snapshot, _inputClosed, inputClose?.IsCompleted,
            _protocol.Completion.IsCompleted, _exitTask.IsCompleted, _stderrTask.IsCompleted, _stdoutReaderJoined, _stdoutAfterEof));
    }
    private async Task DrainStdoutAfterProtocol()
    {
        await Task.Yield();
        // Completion awaits the actual borrowed ReadPump. A stopped snapshot alone does not
        // transfer this stream: starting another read before that join would race the decoder.
        try { await _protocol.Completion.ConfigureAwait(false); }
        catch (Exception failure) { Record(failure, "Protocol"); }
        lock (_sync) _stdoutReaderJoined = true;
        var buffer = new byte[4096];
        try
        {
            int count;
            while ((count = await _stdout.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) != 0)
            {
                _stdoutAfterHash.AppendData(buffer, 0, count); _stdoutAfterObserved = checked(_stdoutAfterObserved + count);
                var retained = Math.Min(count, _launch.Options.MaximumStdoutAfterProtocolBytes - checked((int)_stdoutAfterBytes.Length));
                if (retained > 0) _stdoutAfterBytes.Write(buffer, 0, retained);
                if (_stdoutAfterObserved > _launch.Options.MaximumStdoutAfterProtocolBytes && !_stdoutAfterOverflow)
                {
                    _stdoutAfterOverflow = true;
                    Record(new IOException("Post-protocol stdout byte limit exceeded."), "StdoutAfterProtocolLimit");
                    _stdoutDrainFault.TrySetResult();
                }
            }
            lock (_sync) _stdoutAfterEof = true;
        }
        catch (Exception failure) { Record(failure, "StdoutAfterProtocolRead"); _stdoutDrainFault.TrySetResult(); }
        // Raw post-fence bytes have no event/callback/response authority. They are physical
        // drain observations only; bytes already buffered by the joined decoder are not included.
    }
    private async Task CloseInputAfterWrites()
    {
        await Task.Yield();
        // The connection borrows this pipe. Only its process owner may close it, and only after
        // admission is fenced and every actual write has settled (including the shutdown frame).
        // Windows pipe readers can otherwise keep the peer alive while the owner awaits its exit.
        while (true)
        {
            var snapshot = _protocol.Snapshot;
            if (snapshot.Stopped && snapshot.PendingWrites == 0) break;
            await Task.Delay(5).ConfigureAwait(false);
        }
        ObserveCleanup("input-write-barrier");
        try
        {
            await _process.StandardInput.DisposeAsync().ConfigureAwait(false);
            lock (_sync) { _streamCloses++; _inputClosed = true; }
            ObserveCleanup("input-closed");
        }
        catch (Exception failure) { Record(failure, "StreamDispose"); ObserveCleanup("input-close-failed"); }
    }
    private async Task Cleanup()
    {
        await Task.Yield();
        var graceful = _reason == NodeWorkerStopReason.Shutdown;
        var protocolStop = ObserveShutdown(graceful ? _protocol.ShutdownAsync().AsTask() : _protocol.DisposeAsync().AsTask());
        var inputClose = CloseInputAfterWrites();
        var stdoutDrain = DrainStdoutAfterProtocol();
        ObserveCleanup("cleanup-started", inputClose);
        if (graceful)
        {
            using var clock = new CancellationTokenSource();
            var grace = Task.Delay(_launch.Options.ShutdownGraceMilliseconds, clock.Token);
            var settled = Task.WhenAll(protocolStop, inputClose, stdoutDrain, _exitTask);
            var winner = await Task.WhenAny(settled, grace, _force.Task).ConfigureAwait(false);
            if (winner == grace)
            { ObserveCleanup("grace-expired", inputClose); Record(new TimeoutException("Worker shutdown grace expired."), "ShutdownTimeout"); }
            else { clock.Cancel(); try { await grace.ConfigureAwait(false); } catch (OperationCanceledException) when (clock.IsCancellationRequested) { } }
        }
        else if (_reason is NodeWorkerStopReason.ProtocolFault or NodeWorkerStopReason.Startup or NodeWorkerStopReason.UnexpectedExit)
        {
            // EOF can precede the OS exit signal. Preserve a peer's actual natural exit code before forced termination.
            using var clock = new CancellationTokenSource();
            var grace = Task.Delay(_launch.Options.ShutdownGraceMilliseconds, clock.Token);
            if (await Task.WhenAny(_exitTask, grace, _stdoutDrainFault.Task).ConfigureAwait(false) != grace)
            { clock.Cancel(); try { await grace.ConfigureAwait(false); } catch (OperationCanceledException) when (clock.IsCancellationRequested) { } }
        }
        try
        {
            if (!_process.HasExited) { lock (_sync) _killed = true; _process.Kill(entireProcessTree: true); }
        }
        catch (InvalidOperationException) when (_process.HasExited) { }
        catch (Exception failure) { Record(failure, "Kill"); }
        // Never declare join on a timer: the actual root exit and tracked fixed child exit are awaited.
        await JoinRootExit().ConfigureAwait(false);
        await _exitTask.ConfigureAwait(false);
        ObserveCleanup("root-exit-joined", inputClose);
        Task<NodeWorkerChild>? childLaunch; lock (_sync) childLaunch = _childLaunch;
        if (childLaunch is not null)
            try { _ = await childLaunch.ConfigureAwait(false); } catch (Exception failure) { Record(failure, "ChildAdmission"); }
        var childExited = _child is null;
        if (_child is { } child)
        {
            while (!childExited)
            {
                try { await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); childExited = child.HasExited; }
                catch (Exception failure) { Record(failure, "ChildJoin"); }
                if (!childExited) await Task.Delay(100).ConfigureAwait(false);
            }
        }
        // Process death releases OS pipes. Native callbacks must still cooperate with their protocol stop token.
        await inputClose.ConfigureAwait(false); await protocolStop.ConfigureAwait(false);
        await _protocolTask.ConfigureAwait(false); await stdoutDrain.ConfigureAwait(false); await _stderrTask.ConfigureAwait(false);
        ObserveCleanup("all-io-joined", inputClose);
        var pinned = false;
        try
        {
            _runtimePin.Position = 0; _entryPin.Position = 0;
            pinned = Convert.ToHexStringLower(SHA256.HashData(_runtimePin)) == NodeWorkerLaunch.RuntimeSha256 &&
                Convert.ToHexStringLower(SHA256.HashData(_entryPin)) == _launch.ApprovedEntrySha256 && (_extensionPins?.Verify() ?? true);
            _launch.ExtensionProfile?.VerifyImmutable();
            _launch.HelloProfile?.VerifyImmutable();
            _launch.CommandInputProfile?.VerifyImmutable();
            if (!pinned) Record(new IOException("Held launch pins changed."), "PinMismatch");
        }
        catch (Exception failure) { pinned = false; Record(failure, "PinCheck"); }
        // Input was closed at its actual write barrier. Close the remaining owning wrappers after joins.
        foreach (var stream in new IDisposable[] { _process.StandardOutput, _process.StandardError })
            try { stream.Dispose(); _streamCloses++; } catch (Exception failure) { Record(failure, "StreamDispose"); }
        foreach (var resource in new IDisposable?[] { _runtimePin, _entryPin, _extensionPins, _child, _process })
            try { resource?.Dispose(); } catch (Exception failure) { Record(failure, "ResourceDispose"); }
        var stderr = _stderrBytes.ToArray().ToImmutableArray(); var stderrHash = Convert.ToHexStringLower(_stderrHash.GetHashAndReset());
        _stderrHash.Dispose(); _stderrBytes.Dispose();
        var stdoutAfter = _stdoutAfterBytes.ToArray().ToImmutableArray(); var stdoutAfterHash = Convert.ToHexStringLower(_stdoutAfterHash.GetHashAndReset());
        _stdoutAfterHash.Dispose(); _stdoutAfterBytes.Dispose();
        NodeWorkerTermination termination;
        lock (_sync) termination = new(_reason, _pid, _exitCode, _killed, _exited, _childPid, childExited,
            stderr, _stderrObserved, stderrHash, _stderrOverflow, _protocolFailure, _failures.ToImmutableArray(),
            _protocol.Snapshot, _streamCloses, pinned, stdoutAfter, _stdoutAfterObserved, stdoutAfterHash, _stdoutAfterOverflow, _stdoutAfterEof);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(_launch.RunRoot, "stderr.bin"), stderr.ToArray()).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(_launch.RunRoot, "stdout-after-protocol.bin"), stdoutAfter.ToArray()).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(_launch.RunRoot, "termination.json"), JsonSerializer.Serialize(new
            { termination.Reason, termination.ProcessId, termination.ExitCode, termination.KillAttempted, termination.HasExited,
              termination.ChildProcessId, termination.ChildHasExited, termination.ObservedStderrBytes, retainedStderrBytes = termination.Stderr.Length,
              termination.StderrSha256, termination.StderrOverflow, termination.ProtocolFailure, termination.Failures,
              termination.Protocol, termination.ExplicitStreamCloses, termination.PinsRechecked,
              termination.ObservedStdoutAfterProtocolBytes, retainedStdoutAfterProtocolBytes = termination.StdoutAfterProtocol.Length,
              termination.StdoutAfterProtocolSha256, termination.StdoutAfterProtocolOverflow, termination.StdoutAfterProtocolEof,
              cleanupObservations = _cleanupObservations.ToArray() }) + "\n").ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            Record(failure, "ReceiptWrite");
            lock (_sync) termination = termination with { Failures = _failures.ToImmutableArray() };
        }
        _termination.TrySetResult(termination);
        if (termination.Failures.Length != 0) _completion.TrySetException(new NodeWorkerSupervisorException(termination, _cause));
        else _completion.TrySetResult();
    }
    private async Task JoinRootExit()
    {
        while (true)
        {
            try
            {
                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                if (_process.HasExited)
                { lock (_sync) { _exited = true; _exitCode = _process.ExitCode; } return; }
            }
            catch (Exception failure) { Record(failure, "ExitJoin"); }
            // An observation failure is recorded; it does not release a still-live owned process.
            await Task.Delay(100).ConfigureAwait(false);
        }
    }
    public ValueTask DisposeAsync() { Stop(NodeWorkerStopReason.Shutdown); return new(Completion); }
}
