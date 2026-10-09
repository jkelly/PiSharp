using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PiSharp.Tools.Processes.Unix;

/// <summary>Trusted effect boundary. Launch must establish a fresh private process group before child execution,
/// copy the requested environment/argv exactly, redirect both pipes and deliver the request's standard input bytes (none when null)
/// followed by EOF. A failed or canceled
/// admission owns and joins anything it created. Process.Start followed by setpgid is not an admissible implementation.</summary>
public interface IUnixProcessAdmission
{
    ValueTask<IUnixProcessLease> LaunchAsync(ProcessRequest request, CancellationToken cancellationToken);
}

public interface IUnixProcessLease : IAsyncDisposable
{
    int ProcessId { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    Task<int> Exit { get; }
    /// <summary>Physically stop the admitted group before any pipe/cancellation joins. True confirms only that group.
    /// Descendants that escaped using setsid/setpgid are outside this receipt.</summary>
    ValueTask<bool> StopGroupAsync();
}

/// <summary>Concrete Linux/macOS stop and pipe ownership for a process returned by a trusted atomic admission.
/// This class does not launch processes or prove that a caller's process belongs to a safely admitted group.</summary>
public sealed class UnixProcessGroupLease : IUnixProcessLease
{
    private readonly Process _process;
    private readonly int _groupId;
    private readonly Task<int> _anchorExit;
    private readonly object _gate = new();
    private Task<bool>? _stop;
    private Task? _dispose;
    private readonly List<Task> _originalCleanupTasks = [];
    public ImmutableArray<Task> OriginalCleanupTasks { get { lock (_gate) return _originalCleanupTasks.ToImmutableArray(); } }
    public int ProcessId { get; }
    public Stream StandardOutput { get; }
    public Stream StandardError { get; }
    public Task<int> Exit { get; }

    /// <param name="admittedGroupId">Trusted fresh private group ID, established atomically before execution.
    /// Must be the anchor's PID and must never name the host's group. The admitted anchor remains alive until StopGroupAsync signals it;
    /// it must not exit with the command. This identity reservation prevents signaling a reused numeric group ID.</param>
    public UnixProcessGroupLease(Process admittedGroupAnchor, int admittedGroupId, int admittedCommandProcessId, Task<int> admittedCommandExit, Stream standardOutput, Stream standardError)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        ArgumentNullException.ThrowIfNull(admittedGroupAnchor);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        if (admittedCommandProcessId <= 1) throw new ArgumentOutOfRangeException(nameof(admittedCommandProcessId));
        ProcessId = admittedCommandProcessId;
        if (admittedGroupAnchor.HasExited || admittedGroupId <= 1 || admittedGroupId != admittedGroupAnchor.Id || admittedGroupId == getpgrp())
            throw new ArgumentException("A fresh private admitted process group is required.", nameof(admittedGroupId));
        _process = admittedGroupAnchor; _groupId = admittedGroupId;
        StandardOutput = standardOutput; StandardError = standardError;
        ArgumentNullException.ThrowIfNull(admittedCommandExit);
        Exit = admittedCommandExit;
        _anchorExit = WaitForExitAsync();
    }

    public ValueTask<bool> StopGroupAsync()
    {
        lock (_gate) return new(_stop ??= StopCoreAsync());
    }

    private async Task<bool> StopCoreAsync()
    {
        if (_process.HasExited) return false; // Admission identity anchor was lost: never signal a possibly reused group.
        // Negative PID addresses a process group. Never signal PID 0 or the caller's group.
        if (kill(-_groupId, 9) != 0 && Marshal.GetLastPInvokeError() != 3) return false; // ESRCH
        // A zombie can retain the group until its parent reaps it; our original Exit task performs that reap.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (kill(-_groupId, 0) != 0) return Marshal.GetLastPInvokeError() == 3;
            await Task.Delay(10).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<int> WaitForExitAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        return _process.ExitCode;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_dispose ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        var faults = new List<Exception>();
        await JoinAsync(() => StopGroupAsync().AsTask()).ConfigureAwait(false);
        await JoinAsync(() => StandardOutput.DisposeAsync().AsTask()).ConfigureAwait(false);
        await JoinAsync(() => StandardError.DisposeAsync().AsTask()).ConfigureAwait(false);
        await JoinAsync(() => Exit).ConfigureAwait(false);
        await JoinAsync(() => _anchorExit).ConfigureAwait(false);
        try { _process.Dispose(); } catch (Exception error) { faults.Add(error); }
        if (faults.Count != 0) throw new AggregateException(faults);

        async Task JoinAsync(Func<Task> start)
        {
            Task? original = null;
            try
            {
                original = start();
                lock (_gate) _originalCleanupTasks.Add(original);
                await original.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (original?.Exception is { } complete) faults.AddRange(complete.Flatten().InnerExceptions);
                else faults.Add(error);
            }
        }
    }

    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
    [DllImport("libc")] private static extern int getpgrp();
}

/// <summary>One owned run. Completion has stable identity; original tasks and fault objects remain inspectable.
/// A held callback, pipe or cleanup keeps Completion pending until the original operation settles.</summary>
public sealed class UnixProcessOperation
{
    private readonly object _gate = new();
    private readonly List<Task> _tasks = [];
    private readonly List<Exception> _faults = [];
    public Task<ProcessRunResult> Completion { get; }
    public ImmutableArray<Task> OriginalTasks { get { lock (_gate) return _tasks.ToImmutableArray(); } }
    public ImmutableArray<Exception> OriginalFaults { get { lock (_gate) return _faults.ToImmutableArray(); } }
    internal UnixProcessOperation(Func<UnixProcessOperation, Task<ProcessRunResult>> run) { Completion = run(this); }
    internal T Track<T>(T task) where T : Task { lock (_gate) _tasks.Add(task); return task; }
    internal void Fault(Exception error) { lock (_gate) _faults.Add(error); }
    internal void FaultTask(Task original, Exception observed)
    {
        // Await selects one error; the completed original owns the complete exception set.
        if (original.Exception is { } complete)
            foreach (var error in complete.Flatten().InnerExceptions) Fault(error);
        else Fault(observed);
    }
}
