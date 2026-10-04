using System.Runtime.InteropServices;

namespace PiSharp.Tui;

/// <summary>
/// An explicit process-local cleanup window for a verified live Windows console.
/// A console close signal is not a read EOF, and Windows can terminate the process
/// after the handler returns even when all owned application work has settled.
/// </summary>
public sealed class WindowsConsoleCloseScope : IAsyncDisposable
{
    private static readonly object registrationGate = new();
    private static bool registrationReserved;
    private static readonly TimeSpan CleanupWindow = TimeSpan.FromSeconds(4);
    private const int MaximumAdmittedCallbacks = 8;
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationToken cancellationToken;
    private readonly ManualResetEventSlim cleanupComplete = new(false);
    private readonly TaskCompletionSource callbacksJoined = NewCompletion();
    private readonly ControlHandler handler;
    private GCHandle delegateRoot;
    private Task? cancellationWork, disposal;
    private Exception? callbackException;
    private uint? observedControlType;
    private int activeCallbacks;
    private long callbacksStarted, callbacksSettled;
    private bool registered, closing, cleanupSignaled, cleanupTimedOut, cancellationJoined;

    private WindowsConsoleCloseScope()
    {
        cancellationToken = cancellation.Token;
        handler = OnControl;
    }

    /// <summary>Registers one scope for this process; the caller continues to own the live lease.</summary>
    public static WindowsConsoleCloseScope Open(WindowsConsoleTerminal verifiedLiveLease)
    {
        ArgumentNullException.ThrowIfNull(verifiedLiveLease);
        if (!OperatingSystem.IsWindows()) throw new TerminalException(TerminalFailure.UnsupportedPlatform);
        if (verifiedLiveLease.Snapshot.IsClosing) throw new ObjectDisposedException(nameof(verifiedLiveLease));
        _ = verifiedLiveLease.ReadViewport();
        lock (registrationGate)
        {
            if (registrationReserved) throw new InvalidOperationException("A Windows console-close scope is already registered in this process.");
            registrationReserved = true;
        }
        var scope = new WindowsConsoleCloseScope();
        try
        {
            scope.delegateRoot = GCHandle.Alloc(scope.handler);
            if (!Native.SetConsoleCtrlHandler(scope.handler, true))
                throw new IOException("Registering Windows console-close scope failed (Win32 " + Marshal.GetLastPInvokeError() + ").");
            lock (scope.gate) scope.registered = true;
            return scope;
        }
        catch
        {
            if (scope.delegateRoot.IsAllocated) scope.delegateRoot.Free();
            scope.cleanupComplete.Dispose(); scope.cancellation.Dispose();
            lock (registrationGate) registrationReserved = false;
            throw;
        }
    }

    public CancellationToken CancellationToken => cancellationToken;
    public uint? ObservedControlType { get { lock (gate) return observedControlType; } }
    public Exception? CallbackException { get { lock (gate) return callbackException; } }
    public bool CleanupTimedOut { get { lock (gate) return cleanupTimedOut; } }
    public WindowsConsoleCloseSnapshot Snapshot
    {
        get { lock (gate) return new(observedControlType, registered, closing, cleanupSignaled, cleanupTimedOut,
            activeCallbacks, callbacksStarted, callbacksSettled, cancellationJoined, callbackException?.GetType().Name); }
    }

    /// <summary>Joins managed cancellation callbacks without waiting on the native cleanup handler.</summary>
    public async ValueTask JoinCancellationAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Task? canceling;
        lock (gate) canceling = cancellationWork;
        if (canceling is not null) await canceling.ConfigureAwait(false);
        lock (gate) if (ReferenceEquals(canceling, cancellationWork)) cancellationJoined = true;
        token.ThrowIfCancellationRequested();
    }

    /// <summary>Call only after the host, lease restoration, and required final output have physically settled.</summary>
    public void SignalCleanupComplete()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            if (cleanupSignaled) return;
            cleanupSignaled = true; cleanupComplete.Set();
        }
    }

    private bool OnControl(uint controlType)
    {
        if (controlType is not (2 or 5 or 6)) return false;
        var admitted = false;
        try
        {
            lock (gate)
            {
                if (closing) return false;
                if (callbacksStarted >= MaximumAdmittedCallbacks)
                {
                    callbackException ??= new InvalidOperationException("Windows console-close callback admission exceeds its bound.");
                    return false;
                }
                activeCallbacks++; callbacksStarted++; admitted = true;
                observedControlType ??= controlType;
                // CancelAsync makes the token canceled without blocking this native
                // handler on arbitrary managed cancellation callbacks. Disposal owns
                // and awaits their actual task; no cancellation work is detached.
                if (cancellationWork is null)
                    try { cancellationJoined = false; cancellationWork = ObserveCancellation(cancellation.CancelAsync()); }
                    catch (Exception error) { callbackException ??= error; cancellationWork = Task.FromException(error); }
            }
            if (!cleanupComplete.Wait(CleanupWindow))
                lock (gate) cleanupTimedOut = true;
            return true;
        }
        catch (Exception error)
        {
            lock (gate) callbackException ??= error;
            // Never throw across the unmanaged handler boundary. The recorded
            // failure is surfaced by awaited disposal; the OS still owns termination.
            return true;
        }
        finally
        {
            if (admitted)
                lock (gate)
                {
                    activeCallbacks--; callbacksSettled++;
                    if (closing && activeCallbacks == 0) callbacksJoined.TrySetResult();
                }
        }
    }

    private async Task ObserveCancellation(Task canceling)
    {
        try { await canceling.ConfigureAwait(false); }
        catch (Exception error) { lock (gate) callbackException ??= error; throw; }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null; Task task;
        lock (gate)
        {
            if (disposal is null)
            {
                completion = NewCompletion(); disposal = completion.Task; closing = true;
                if (activeCallbacks == 0) callbacksJoined.TrySetResult();
            }
            task = disposal;
        }
        if (completion is not null) _ = Close(completion);
        return new(task);
    }

    private async Task Close(TaskCompletionSource completion)
    {
        Exception? failure = null; var removed = false;
        try
        {
            try
            {
                removed = Native.SetConsoleCtrlHandler(handler, false);
                if (!removed) failure = new IOException("Unregistering Windows console-close scope failed (Win32 " + Marshal.GetLastPInvokeError() + ").");
                else lock (gate) registered = false;
            }
            catch (Exception error) { failure = error; }
            await callbacksJoined.Task.ConfigureAwait(false);
            Task? canceling;
            lock (gate) canceling = cancellationWork;
            if (canceling is not null)
                try { await canceling.ConfigureAwait(false); }
                catch (Exception error) { lock (gate) callbackException ??= error; failure ??= error; }
            lock (gate)
            {
                cancellationJoined = true;
                failure ??= callbackException;
                if (cleanupTimedOut) failure ??= new TimeoutException("Windows console-close cleanup did not complete within its four-second handler window.");
            }
        }
        catch (Exception error) { failure ??= error; }
        finally
        {
            // An unregister failure leaves a native pointer installed. Keep its
            // delegate rooted and reserve registration until process exit; fail the
            // scope instead of freeing a pointer that Windows can still invoke.
            if (removed)
            {
                if (delegateRoot.IsAllocated) delegateRoot.Free();
                lock (registrationGate) registrationReserved = false;
            }
            GC.KeepAlive(handler);
            cleanupComplete.Dispose(); cancellation.Dispose();
        }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ControlHandler(uint controlType);
    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetConsoleCtrlHandler(ControlHandler handler, [MarshalAs(UnmanagedType.Bool)] bool add);
    }
}

public sealed record WindowsConsoleCloseSnapshot(uint? ObservedControlType, bool Registered, bool IsClosing,
    bool CleanupSignaled, bool CleanupTimedOut, int ActiveCallbacks, long CallbacksStarted, long CallbacksSettled,
    bool CancellationWorkJoined, string? CallbackFailureType);
