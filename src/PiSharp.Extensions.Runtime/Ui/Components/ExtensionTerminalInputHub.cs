using System.Collections.Immutable;

namespace PiSharp.Extensions.Runtime;

/// <summary>
/// Explicit raw-input admission point for one terminal connection/session generation.
/// The admitted host dispatches after protocol responses are filtered and forwards only Forward.Data.
/// No terminal reader, writer, decoder, or renderer is acquired by this object.
/// </summary>
public sealed class ExtensionTerminalInputHub : IAsyncDisposable
{
    // ExecutionContext carries ancestry into asynchronous and nested plugin work.
    // Inactive inherited frames are ignored after their owning invocation settles.
    private static readonly AsyncLocal<LifecycleFrame?> CurrentFrame = new();
    // Cancel is synchronous; captured ExecutionContext must not hide its ancestry.
    [ThreadStatic] private static LifecycleFrame? CurrentCleanup;
    internal sealed class LifecycleFrame : IDisposable
    {
        internal Scope Owner { get; }
        internal LifecycleFrame? Parent { get; }
        internal LifecycleFrame? SynchronousParent { get; init; }
        private int active = 1;
        internal bool Active => Volatile.Read(ref active) != 0;
        internal LifecycleFrame(Scope owner)
        {
            Owner = owner; Parent = CurrentFrame.Value; CurrentFrame.Value = this;
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref active, 0);
            CurrentFrame.Value = Parent;
        }
    }
    private static void RejectLifecycleClose(ExtensionTerminalInputHub hub, Scope? scope = null)
    {
        var pending = new Stack<LifecycleFrame>();
        var visited = new HashSet<LifecycleFrame>();
        if (CurrentFrame.Value is { } current) pending.Push(current);
        if (CurrentCleanup is { } synchronous) pending.Push(synchronous);
        while (pending.TryPop(out var frame))
        {
            if (!visited.Add(frame)) continue;
            if (frame.Active && ReferenceEquals(frame.Owner.hub, hub) &&
                (scope is null || ReferenceEquals(frame.Owner, scope)))
                throw new InvalidOperationException("Terminal input lifecycle close cannot join its own or an ancestor callback or cleanup.");
            if (frame.Parent is { } parent) pending.Push(parent);
            if (frame.SynchronousParent is { } synchronousParent) pending.Push(synchronousParent);
            // Cancellation registrations restore their captured ExecutionContext. Follow
            // the owner's active cleanup ancestry as well, even if the captured input
            // frame has settled, so restoring that context cannot hide an in-flight Cancel.
            if (Volatile.Read(ref frame.Owner.CleanupFrame) is { } cleanup) pending.Push(cleanup);
        }
    }
    internal void AssertCanClose() => RejectLifecycleClose(this);
    private const int MaximumSubscriptions = 256;
    private const int MaximumPendingCallbacks = 256;
    private readonly object gate = new();
    private readonly List<Subscription> subscriptions = [];
    private readonly List<Scope> scopes = [];
    private readonly HashSet<Task<ExtensionTerminalInputOutcome>> dispatches = [];
    private bool closed;
    private Task? close;
    public long ConnectionGeneration { get; }
    public long SessionGeneration { get; }

    public ExtensionTerminalInputHub(long connectionGeneration, long sessionGeneration)
    {
        if (connectionGeneration < 0) throw new ArgumentOutOfRangeException(nameof(connectionGeneration));
        if (sessionGeneration < 0) throw new ArgumentOutOfRangeException(nameof(sessionGeneration));
        ConnectionGeneration = connectionGeneration; SessionGeneration = sessionGeneration;
    }

    public Scope OpenScope(IExtensionContext context) => OpenScope(context, null);
    internal Scope OpenScope(IExtensionContext context, Func<IDisposable>? enterNativeCleanup)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (gate)
        {
            if (closed) throw new ObjectDisposedException(nameof(ExtensionTerminalInputHub));
            if (scopes.Count >= MaximumSubscriptions) throw new InvalidOperationException("Terminal input scope limit reached.");
            var scope = new Scope(this, context, enterNativeCleanup); scopes.Add(scope); return scope;
        }
    }

    public Task<ExtensionTerminalInputOutcome> DispatchAsync(string data, long connectionGeneration,
        long sessionGeneration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var completion = new TaskCompletionSource<ExtensionTerminalInputOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Subscription[] snapshot;
        lock (gate)
        {
            if (closed || connectionGeneration != ConnectionGeneration || sessionGeneration != SessionGeneration)
                return Task.FromResult(ExtensionTerminalInputOutcome.Unavailable(ExtensionUiUnavailableReason.StaleContext));
            if (dispatches.Count >= MaximumPendingCallbacks)
                return Task.FromResult(ExtensionTerminalInputOutcome.Unavailable(ExtensionUiUnavailableReason.ResourceLimit));
            snapshot = subscriptions.ToArray(); dispatches.Add(completion.Task);
        }
        _ = RunAsync(data, snapshot, cancellationToken, completion);
        return completion.Task;
    }

    private async Task RunAsync(string data, Subscription[] snapshot, CancellationToken token,
        TaskCompletionSource<ExtensionTerminalInputOutcome> completion)
    {
        try
        {
            foreach (var subscription in snapshot)
            {
                var owner = subscription.Owner;
                TaskCompletionSource<ExtensionTerminalInputResult?> observation;
                lock (gate)
                {
                    if (closed) { completion.TrySetResult(Stale()); return; }
                    if (token.IsCancellationRequested) { completion.TrySetResult(ExtensionTerminalInputOutcome.Cancelled()); return; }
                    if (subscription.Removed || owner.Closed || owner.Cancelled) continue;
                    if (owner.Pending.Count + owner.Failures.Count >= MaximumPendingCallbacks)
                    { completion.TrySetResult(ExtensionTerminalInputOutcome.Unavailable(ExtensionUiUnavailableReason.ResourceLimit)); return; }
                    observation = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    owner.Pending.Add(observation.Task);
                }
                Task<ExtensionTerminalInputResult?> original;
                try
                {
                    using var callbackFrame = new LifecycleFrame(owner);
                    // Capture the original before observing it. A faulted OCE stays a fault.
                    using var callbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.Lifetime.Token);
                    // Caller cancellation is another real parent; guard the actual child Cancel,
                    // and retain its registration until the original callback has joined.
                    using var callerCancellation = token.UnsafeRegister(static state =>
                    {
                        var owned = ((Scope Owner, CancellationTokenSource Source))state!;
                        owned.Owner.CancelCallback(owned.Source);
                    }, (owner, callbackCancellation));
                    original = subscription.Handler(data, owner.Context, callbackCancellation.Token) ??
                        throw new InvalidOperationException("Terminal input callback returned no task.");
                    try { await original.ConfigureAwait(false); } catch (Exception) { }
                    if (original.IsFaulted)
                    {
                        lock (gate) owner.Failures.Add(original);
                        var originalFaults = original.Exception!.InnerExceptions;
                        observation.TrySetException(originalFaults);
                        completion.TrySetException(originalFaults); return;
                    }
                    if (original.IsCanceled)
                    { observation.TrySetCanceled(); completion.TrySetResult(ExtensionTerminalInputOutcome.Cancelled()); return; }
                    var result = original.GetAwaiter().GetResult();
                    observation.TrySetResult(result);
                    lock (gate)
                    {
                        if (closed || owner.Closed) { completion.TrySetResult(Stale()); return; }
                        if (token.IsCancellationRequested || owner.Cancelled)
                        { completion.TrySetResult(ExtensionTerminalInputOutcome.Cancelled()); return; }
                    }
                    if (result?.Consume == true) { completion.TrySetResult(ExtensionTerminalInputOutcome.Consumed()); return; }
                    if (result?.Data is { } replacement) data = replacement;
                }
                catch (Exception error)
                {
                    // Includes synchronously thrown OperationCanceledException: it is not a canceled task.
                    var fault = Task.FromException<ExtensionTerminalInputResult?>(error);
                    lock (gate) owner.Failures.Add(fault);
                    observation.TrySetException(error); completion.TrySetException(error); return;
                }
                finally
                {
                    lock (gate) owner.Pending.Remove(observation.Task);
                    // Observations are ownership joins, including when the caller ignores DispatchAsync.
                    if (observation.Task.IsFaulted) _ = observation.Task.Exception;
                }
            }
            lock (gate)
            {
                completion.TrySetResult(closed ? Stale() : token.IsCancellationRequested ?
                    ExtensionTerminalInputOutcome.Cancelled() : data.Length == 0 ?
                    ExtensionTerminalInputOutcome.Consumed() : ExtensionTerminalInputOutcome.Forward(data));
            }
        }
        catch (Exception error) { completion.TrySetException(error); }
        finally { lock (gate) dispatches.Remove(completion.Task); }
    }

    private static ExtensionTerminalInputOutcome Stale() =>
        ExtensionTerminalInputOutcome.Unavailable(ExtensionUiUnavailableReason.StaleContext);

    public ValueTask DisposeAsync()
    {
        RejectLifecycleClose(this);
        TaskCompletionSource completion;
        Task[] pending = [];
        lock (gate)
        {
            if (close is not null) return new(close);
            closed = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); close = completion.Task;
            foreach (var scope in scopes) scope.Closed = true;
            foreach (var subscription in subscriptions) subscription.Removed = true;
            foreach (var scope in scopes) scope.Synchronous.Clear();
            subscriptions.Clear(); pending = dispatches.Cast<Task>().ToArray();
        }

        _ = CloseAsync(pending, completion);
        return new(completion.Task);
    }

    private async Task CloseAsync(Task[] pending, TaskCompletionSource completion)
    {
        foreach (var scope in scopes) await scope.StopCancellation().ConfigureAwait(false);
        foreach (var task in pending) try { await task.ConfigureAwait(false); } catch (Exception) { }
        foreach (var scope in scopes) scope.DisposeCancellation();
        List<Exception> failures;
        lock (gate) failures = scopes.SelectMany(scope => scope.Failures)
            .SelectMany(task => task.Exception!.InnerExceptions).Concat(scopes.SelectMany(scope => scope.CleanupFailures)).ToList();
        if (failures.Count == 0) completion.TrySetResult(); else completion.TrySetException(failures);
    }

    private sealed class Subscription(Scope owner, ExtensionTerminalInputAsyncHandler handler) : IDisposable
    {
        internal Scope Owner { get; } = owner;
        internal ExtensionTerminalInputAsyncHandler Handler { get; } = handler;
        internal bool Removed;
        internal ExtensionTerminalInputHandler? SynchronousHandler;
        public void Dispose()
        {
            lock (Owner.hub.gate)
            {
                Removed = true; Owner.hub.subscriptions.Remove(this);
                if (SynchronousHandler is { } handler && Owner.Synchronous.TryGetValue(handler, out var current) &&
                    ReferenceEquals(current, this)) Owner.Synchronous.Remove(handler);
            }
        }
    }

    /// <summary>Owned by the callback generation. Dispose waits for admitted original callbacks.</summary>
    public sealed class Scope : IExtensionTerminalInput, IAsyncDisposable
    {
        internal readonly ExtensionTerminalInputHub hub;
        internal IExtensionContext Context { get; }
        internal bool Closed;
        internal bool Cancelled => Context.OperationCancellationToken.IsCancellationRequested ||
            Context.SessionCancellationToken.IsCancellationRequested || Context.ExtensionLifetimeCancellationToken.IsCancellationRequested;
        internal readonly HashSet<Task<ExtensionTerminalInputResult?>> Pending = [];
        internal readonly List<Task<ExtensionTerminalInputResult?>> Failures = [];
        internal readonly List<Exception> CleanupFailures = [];
        internal LifecycleFrame? CleanupFrame;
        internal CancellationTokenSource Lifetime { get; }
        private readonly List<CancellationTokenRegistration> parentRegistrations = [];
        private Task? cancellationStop;
        private bool cancellationDisposed;
        private Task? disposal;
        internal readonly Dictionary<ExtensionTerminalInputHandler, IDisposable> Synchronous = new(ReferenceEqualityComparer.Instance);
        private readonly Func<IDisposable>? enterNativeCleanup;
        internal Scope(ExtensionTerminalInputHub hub, IExtensionContext context, Func<IDisposable>? enterNativeCleanup)
        {
            this.hub = hub; Context = context; this.enterNativeCleanup = enterNativeCleanup;
            // Each real parent cancellation enters the same synchronous ancestry as explicit close.
            // A framework linked CTS would invoke plugin handlers without our cleanup seed.
            Lifetime = new CancellationTokenSource();
            try
            {
                foreach (var parent in new[] { context.OperationCancellationToken,
                    context.SessionCancellationToken, context.ExtensionLifetimeCancellationToken })
                    if (parent.CanBeCanceled)
                        parentRegistrations.Add(parent.UnsafeRegister(static state => ((Scope)state!).CancelFromParent(), this));
            }
            catch { DisposeCancellation(); throw; }
        }
        internal void CancelCallback(CancellationTokenSource source)
        {
            var previousCleanup = CurrentCleanup;
            using var cleanupFrame = new LifecycleFrame(this) { SynchronousParent = previousCleanup };
            CurrentCleanup = cleanupFrame;
            var previousFrame = Volatile.Read(ref CleanupFrame);
            Volatile.Write(ref CleanupFrame, cleanupFrame);
            try { using var nativeCleanup = enterNativeCleanup?.Invoke(); source.Cancel(); }
            catch (Exception error) { lock (hub.gate) CleanupFailures.Add(error); throw; }
            finally { CurrentCleanup = previousCleanup; Volatile.Write(ref CleanupFrame, previousFrame); }
        }
        private void CancelFromParent()
        {
            var original = StopCancellation(propagateOriginalFault: true);
            if (!original.IsCompleted)
            {
                // Concurrent parent cancellation joins the same original. Synchronous own/ancestor
                // reentry must refuse before a handler can wait on its own cleanup callback.
                RejectLifecycleClose(hub, this);
                original.GetAwaiter().GetResult();
            }
        }
        internal Task StopCancellation(bool propagateOriginalFault = false)
        {
            TaskCompletionSource completion;
            lock (hub.gate)
            {
                if (cancellationStop is not null) return cancellationStop;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationStop = completion.Task;
            }
            var previousCleanup = CurrentCleanup;
            using var cleanupFrame = new LifecycleFrame(this) { SynchronousParent = previousCleanup };
            CurrentCleanup = cleanupFrame;
            var previousFrame = Volatile.Read(ref CleanupFrame);
            Volatile.Write(ref CleanupFrame, cleanupFrame);
            Exception? originalFault = null;
            try { using var nativeCleanup = enterNativeCleanup?.Invoke(); Lifetime.Cancel(); }
            catch (Exception error) { originalFault = error; lock (hub.gate) CleanupFailures.Add(error); }
            finally
            {
                CurrentCleanup = previousCleanup;
                Volatile.Write(ref CleanupFrame, previousFrame); completion.TrySetResult();
            }
            if (propagateOriginalFault && originalFault is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(originalFault).Throw();
            return completion.Task;
        }
        internal void DisposeCancellation()
        {
            lock (hub.gate) { if (cancellationDisposed) return; cancellationDisposed = true; }
            foreach (var registration in parentRegistrations)
                try { registration.Dispose(); } catch (Exception error) { lock (hub.gate) CleanupFailures.Add(error); }
            try { Lifetime.Dispose(); } catch (Exception error) { lock (hub.gate) CleanupFailures.Add(error); }
        }
        /// <summary>Exact original faulted tasks, retained for owner retirement; no flattening or OCE relabeling.</summary>
        public ImmutableArray<Task<ExtensionTerminalInputResult?>> FaultedCallbacks
        { get { lock (hub.gate) return Failures.ToImmutableArray(); } }
        public IDisposable OnTerminalInput(ExtensionTerminalInputHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (hub.gate)
            {
                if (Closed || hub.closed || Cancelled) throw new ObjectDisposedException(nameof(Scope));
                if (Synchronous.TryGetValue(handler, out var existing) && existing is Subscription { Removed: false }) return existing;
                var subscription = OnTerminalInputAsync((data, _, _) => Task.FromResult(handler(data)));
                ((Subscription)subscription).SynchronousHandler = handler;
                Synchronous[handler] = subscription; return subscription;
            }
        }
        public IDisposable OnTerminalInputAsync(ExtensionTerminalInputAsyncHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (hub.gate)
            {
                if (Closed || hub.closed || Cancelled) throw new ObjectDisposedException(nameof(Scope));
                if (hub.subscriptions.Count >= MaximumSubscriptions) throw new InvalidOperationException("Terminal input subscription limit reached.");
                var subscription = new Subscription(this, handler); hub.subscriptions.Add(subscription); return subscription;
            }
        }
        internal void RetireAdmission()
        {
            lock (hub.gate)
            {
                Closed = true; Synchronous.Clear();
                foreach (var subscription in hub.subscriptions.Where(subscription => ReferenceEquals(subscription.Owner, this)))
                    subscription.Removed = true;
                hub.subscriptions.RemoveAll(subscription => ReferenceEquals(subscription.Owner, this));
            }
        }
        public ValueTask DisposeAsync()
        {
            RejectLifecycleClose(hub, this);
            TaskCompletionSource completion;
            Task[] pending;
            lock (hub.gate)
            {
                if (disposal is not null) return new(disposal);
                Closed = true; Synchronous.Clear();
                foreach (var subscription in hub.subscriptions.Where(subscription => ReferenceEquals(subscription.Owner, this)))
                    subscription.Removed = true;
                hub.subscriptions.RemoveAll(subscription => ReferenceEquals(subscription.Owner, this));
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously); disposal = completion.Task;
                pending = Pending.Cast<Task>().ToArray();
            }

            _ = FinishAsync(pending, completion); return new(completion.Task);
        }
        private async Task FinishAsync(Task[] pending, TaskCompletionSource completion)
        {
            await StopCancellation().ConfigureAwait(false);
            foreach (var task in pending) try { await task.ConfigureAwait(false); } catch (Exception) { }
            DisposeCancellation();
            Exception[] failures;
            lock (hub.gate) failures = Failures.SelectMany(task => task.Exception!.InnerExceptions).Concat(CleanupFailures).ToArray();
            if (failures.Length == 0) completion.TrySetResult(); else completion.TrySetException(failures);
        }
    }
}
