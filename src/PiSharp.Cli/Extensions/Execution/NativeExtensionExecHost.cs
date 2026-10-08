using System.Collections.Immutable;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Execution;
using PiSharp.Tools.Processes;

namespace PiSharp.Cli.Extensions.Execution;

public sealed record NativeExtensionExecInvocation(string OwnerId, long OwnerGeneration,
    string Command, ImmutableArray<string> Arguments, string WorkingDirectory, double? TimeoutSeconds);

/// <summary>Caller admission owns executable resolution, environment and scratch output authority.
/// This host neither creates a runner nor resolves a command through an ambient PATH.</summary>
public sealed class NativeExtensionExecHost : IExtensionExecFacade, IAsyncDisposable
{
    private sealed record SupplierFrame(NativeExtensionExecHost Host, SupplierFrame? LogicalParent, SupplierFrame? PhysicalParent);
    private static readonly AsyncLocal<SupplierFrame?> InSupplier = new();
    [ThreadStatic] private static SupplierFrame? physicalSupplier;
    private readonly object gate = new();
    private readonly ISeparatedProcessRunner runner;
    private readonly Func<NativeExtensionExecInvocation, CancellationToken, ValueTask<ProcessRequest>> admit;
    private readonly CancellationTokenSource stop;
    private readonly string workingDirectory;
    private sealed record Operation(TaskCompletionSource<Task> Slot, CancellationToken Signal);
    private readonly List<Operation> operations = [];
    private readonly HashSet<string> spillPaths = new(StringComparer.OrdinalIgnoreCase);
    private Task? close;
    private bool closing;
    private bool dispatchingCancellation;
    public string OwnerId { get; }
    public long OwnerGeneration { get; }
    public NativeExtensionExecOriginals Originals { get; }
    internal CancellationToken OwnerLifetimeCancellationToken { get; }

    internal NativeExtensionExecHost(IExtensionRegistry owner, ISeparatedProcessRunner runner,
        Func<NativeExtensionExecInvocation, CancellationToken, ValueTask<ProcessRequest>> admit,
        string workingDirectory, NativeExtensionExecOriginals originals)
    {
        this.runner = runner; this.admit = admit; this.workingDirectory = workingDirectory;
        OwnerId = owner.OwnerId; OwnerGeneration = owner.OwnerGeneration; Originals = originals;
        OwnerLifetimeCancellationToken = owner.ExtensionLifetimeCancellationToken;
        stop = CancellationTokenSource.CreateLinkedTokenSource(owner.ExtensionLifetimeCancellationToken);
    }

    public Task<ExtensionExecResult> ExecAsync(string command, ImmutableArray<string> arguments,
        ExtensionExecOptions? options = null) => ExecCore(command, arguments, options, null);
    internal Task<ExtensionExecResult> ExecGuardedAsync(string command, ImmutableArray<string> arguments,
        ExtensionExecOptions options, Action validateOrigin) => ExecCore(command, arguments, options, validateOrigin);
    private Task<ExtensionExecResult> ExecCore(string command, ImmutableArray<string> arguments,
        ExtensionExecOptions? options, Action? validateOrigin)
    {
        validateOrigin?.Invoke();
        if (HasSupplierAncestor()) throw new InvalidOperationException("Exec supplier ancestry reentry is unavailable.");
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (arguments.IsDefault || arguments.Any(value => value is null)) throw new ArgumentException("Ordered arguments required.");
        options ??= new();
        if (options.TimeoutMilliseconds is { } timeout && (!double.IsFinite(timeout) || timeout > int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(options));
        var cwd = options.Cwd ?? workingDirectory;
        if (!Path.IsPathFullyQualified(cwd)) throw new ArgumentException("Explicit absolute working directory required.");
        var seconds = options.TimeoutMilliseconds is > 0 ? options.TimeoutMilliseconds / 1000 : null;
        lock (gate)
        {
            if (closing || stop.IsCancellationRequested) throw new ObjectDisposedException(nameof(NativeExtensionExecHost));
            if (operations.Count >= 1024) throw new InvalidOperationException("Exec owner operation limit reached.");
            var slot = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            operations.Add(new(slot, options.Signal)); // reserve before any supplied callback can run
            var mapped = ExecuteAsync(new(OwnerId, OwnerGeneration, command, arguments, cwd, seconds), options.Signal, validateOrigin);
            slot.SetResult(mapped); // reservation is not a fabricated process original
            return mapped;
        }
    }

    private async Task<ExtensionExecResult> ExecuteAsync(NativeExtensionExecInvocation invocation, CancellationToken signal, Action? validateOrigin)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, signal);
        linked.Token.ThrowIfCancellationRequested();
        validateOrigin?.Invoke();
        Task<ProcessRequest>? admission = null;
        ProcessRequest request;
        var previous = InSupplier.Value;
        try
        {
            InSupplier.Value = new(this, previous, null);
            admission = InvokeSupplied(() => admit(invocation, linked.Token).AsTask(), previous);
            request = await admission.ConfigureAwait(false);
            Originals.Record("executable-admission", admission, null);
        }
        catch (Exception error)
        {
            if (admission is not null) throw Originals.Record("executable-admission", admission, error)!;
            throw new NativeExtensionExecFault("synchronous executable admission", null, error);
        }
        finally { InSupplier.Value = previous; }
        linked.Token.ThrowIfCancellationRequested();
        validateOrigin?.Invoke();
        if (request is null || request.Arguments.IsDefault || request.Environment is null ||
            !Path.IsPathFullyQualified(request.Executable) || !Path.IsPathFullyQualified(request.SpillPath) ||
            !StringComparer.Ordinal.Equals(request.WorkingDirectory, invocation.WorkingDirectory) ||
            !request.Arguments.SequenceEqual(invocation.Arguments) || request.TimeoutSeconds != invocation.TimeoutSeconds)
            throw new InvalidOperationException("Admission changed ordered arguments, cwd or timeout, or omitted absolute resource authority.");
        lock (gate)
        {
            if (closing || stop.IsCancellationRequested) throw new ObjectDisposedException(nameof(NativeExtensionExecHost));
            if (!spillPaths.Add(Path.GetFullPath(request.SpillPath))) throw new InvalidOperationException("Exec spill paths cannot be reused.");
        }
        Task<SeparatedProcessRunResult>? process = null;
        SeparatedProcessRunResult result;
        try
        {
            InSupplier.Value = new(this, previous, null);
            process = InvokeSupplied(() =>
            {
                validateOrigin?.Invoke(); linked.Token.ThrowIfCancellationRequested();
                return runner.RunSeparatedAsync(request, linked.Token).AsTask();
            }, previous);
            result = await process.ConfigureAwait(false);
            Originals.Record("separated-process", process, null);
        }
        catch (Exception error)
        {
            if (process is not null) throw Originals.Record("separated-process", process, error)!;
            throw new NativeExtensionExecFault("synchronous separated process", null, error);
        }
        finally { InSupplier.Value = previous; }
        validateOrigin?.Invoke();
        var receipt = result.Process;
        if (!receipt.CleanupConfirmed || !receipt.CapturedOutputComplete || result.StandardOutput.Truncated ||
            result.StandardError.Truncated || receipt.Status == ProcessRunStatus.Failed || receipt.Diagnostics.Length != 0)
            throw new IOException("Exec output or process cleanup is not complete; no successful result is admitted.");
        var killed = receipt.ProcessStarted && receipt.Status is ProcessRunStatus.Canceled or ProcessRunStatus.TimedOut;
        if (!receipt.ProcessStarted && receipt.Status == ProcessRunStatus.Canceled)
            throw new OperationCanceledException(linked.Token);
        if (!killed && receipt.ExitCode is null) throw new IOException("A naturally exited process requires its actual exit code.");
        if (receipt.Status == ProcessRunStatus.Exited && receipt.ExitCode != 0 ||
            receipt.Status == ProcessRunStatus.NonZeroExit && receipt.ExitCode == 0)
            throw new IOException("Actual process status and exit code disagree.");
        return new(result.StandardOutput.Content, result.StandardError.Content, receipt.ExitCode ?? 0, killed);
    }

    public ValueTask DisposeAsync()
    {
        if (HasSupplierAncestor()) throw new InvalidOperationException("Exec supplier cannot close an active ancestor owner.");
        lock (gate)
        {
            // A CancellationToken callback may restore an unrelated captured ExecutionContext.
            // Refuse ALL public close calls during this conservative boundary, including external callers.
            if (dispatchingCancellation || operations.Any(operation => (!operation.Slot.Task.IsCompleted || !operation.Slot.Task.Result.IsCompleted) &&
                (operation.Signal.IsCancellationRequested || stop.IsCancellationRequested)))
                throw new InvalidOperationException("Public exec close is unavailable during canceled-active work or cancellation dispatch.");
            return RetireOwnedAsync();
        }
    }
    // Only the genuine installed plugin's owner cleanup invokes this path. It never skips joins.
    internal ValueTask RetireOwnedAsync()
    {
        if (HasSupplierAncestor()) throw new InvalidOperationException("Exec supplier cannot retire an active ancestor owner.");
        lock (gate)
        {
            if (close is null) { closing = true; close = CloseAsync(operations.Select(operation => operation.Slot.Task).ToArray()); }
            return new(close);
        }
    }
    private bool HasSupplierAncestor()
    {
        var pending = new Stack<SupplierFrame>();
        var visited = new HashSet<SupplierFrame>(ReferenceEqualityComparer.Instance);
        if (InSupplier.Value is { } logical) pending.Push(logical);
        if (physicalSupplier is { } physical) pending.Push(physical);
        while (pending.TryPop(out var frame))
        {
            if (!visited.Add(frame)) continue;
            if (visited.Count > 1024) throw new InvalidOperationException("Exec ancestry bound exceeded.");
            if (ReferenceEquals(frame.Host, this)) return true;
            if (frame.LogicalParent is { } logicalParent) pending.Push(logicalParent);
            if (frame.PhysicalParent is { } physicalParent) pending.Push(physicalParent);
        }
        return false;
    }
    private T InvokeSupplied<T>(Func<T> invoke, SupplierFrame? logicalParent)
    {
        // ExecutionContext.Run cannot erase the physical synchronous callback stack.
        // Restore on this thread before awaiting; a thread-local frame never spans an await.
        var previous = physicalSupplier;
        physicalSupplier = new(this, logicalParent, previous);
        try { return invoke(); }
        finally { physicalSupplier = previous; }
    }
    private async Task CloseAsync(Task<Task>[] acquired)
    {
        var failures = new List<Exception>();
        Task? cancellation = null;
        try
        {
            lock (gate) dispatchingCancellation = true;
            cancellation = stop.CancelAsync();
            await cancellation.ConfigureAwait(false);
            Originals.Record("exec-owner-cancellation", cancellation, null);
        }
        catch (Exception error)
        { failures.Add(cancellation is null ? error : Originals.Record("exec-owner-cancellation", cancellation, error)!); }
        finally { lock (gate) dispatchingCancellation = false; }
        foreach (var reservation in acquired)
        {
            var original = await reservation.ConfigureAwait(false);
            await Originals.Join("exec-mapping-join", original, failures).ConfigureAwait(false);
        }
        stop.Dispose();
        NativeExtensionExecOriginals.Throw(failures);
    }
}
