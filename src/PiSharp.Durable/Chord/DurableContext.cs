// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/chord/src/context/index.ts (Context, ContextKey, BACKGROUND_CONTEXT,
// TODO_CONTEXT, createContextKey, withContextValue, withAbortSignal, withoutAbortSignal, withCancel, awaitWithContext) and the Context
// and ContextKey types of packages/chord/src/types.ts. Named DurableContext so it is never mistaken for a conversation's context.
using System.Runtime.ExceptionServices;

namespace PiSharp.Durable.Chord;

/// <summary>
/// Typed identity for one value carried by a <see cref="DurableContext"/> (upstream <c>ContextKey</c>, whose identity is a symbol).
/// Two keys are the same key only when they are the same instance; the description is for <see cref="DurableContext.ToString"/>.
/// </summary>
/// <typeparam name="T">The type of the value stored under this key.</typeparam>
public sealed class ContextKey<T>(string? description)
{
    /// <summary>The description printed by <see cref="DurableContext.ToString"/>; null prints <c>anonymous</c>.</summary>
    public string? Description { get; } = description;

    /// <inheritdoc/>
    public override string ToString() => Description ?? "anonymous";
}

/// <summary>
/// Immutable invocation-scoped values passed explicitly through operations: a chain of typed values plus an optional cancellation
/// signal (upstream <c>Context</c>). Every derivation returns a new context and leaves its parent unchanged.
/// </summary>
/// <remarks>
/// <para>Upstream stores the abort signal as an ordinary context value under a private key; so does this port, which is why
/// <see cref="ToString"/> prints <c>.WithValue(chord.abortSignal)</c> for a cancellation derivation exactly as upstream does.</para>
/// <para>Deliberate differences, each forced by .NET: an <c>AbortSignal</c> becomes a <see cref="System.Threading.CancellationToken"/>
/// plus an optional <see cref="CancellationReason"/> (a token carries no reason, so a reason exists only for cancellation through
/// <see cref="WithCancel"/>; a plain token cancels with a null reason). Where upstream rejects with the reason when it is an
/// <c>Error</c> and otherwise with an <c>AbortError</c>, this port throws the reason when it is an <see cref="Exception"/> and otherwise
/// an <see cref="OperationCanceledException"/> for <see cref="CancellationToken"/>. <c>abortSignal === undefined</c> is
/// <see cref="HasCancellation"/> false.</para>
/// </remarks>
public sealed class DurableContext
{
    private static readonly ContextKey<CancellationSignal?> SignalKey = new("chord.abortSignal");

    private readonly DurableContext? parent;
    private readonly object? key;
    private readonly object? value;
    private readonly string? name;
    // The innermost signal value on the chain, cached so token access does not walk the chain.
    private readonly CancellationSignal? signal;

    private DurableContext(string name) { this.name = name; }

    private DurableContext(DurableContext parent, object key, object? value)
    {
        this.parent = parent;
        this.key = key;
        this.value = value;
        signal = ReferenceEquals(key, SignalKey) ? (CancellationSignal?)value : parent.signal;
    }

    /// <summary>The empty root context for work with no caller (<c>BACKGROUND_CONTEXT</c>). It never cancels.</summary>
    public static DurableContext Background { get; } = new("[Context BACKGROUND_CONTEXT]");

    /// <summary>A distinct empty root marking code that has not yet been given a real context (<c>TODO_CONTEXT</c>).</summary>
    public static DurableContext Todo { get; } = new("[Context TODO_CONTEXT]");

    /// <summary>Whether this context carries a cancellation signal (upstream <c>abortSignal !== undefined</c>).</summary>
    public bool HasCancellation => signal is not null;

    /// <summary>The cancellation token; <see cref="CancellationToken.None"/> when there is no signal.</summary>
    public CancellationToken CancellationToken => signal?.Token ?? CancellationToken.None;

    /// <summary>Whether cancellation has been requested (upstream <c>abortSignal?.aborted</c>).</summary>
    public bool IsCancellationRequested => signal?.Token.IsCancellationRequested ?? false;

    /// <summary>The reason given to <see cref="DurableCancellation.Cancel"/> (upstream <c>abortSignal.reason</c>); null when not
    /// cancelled, cancelled without a reason, or cancelled through a plain token.</summary>
    public object? CancellationReason => signal is { Token.IsCancellationRequested: true } ? signal.Reason : null;

    /// <summary>Throws the cancellation error when cancellation has been requested (upstream <c>abortSignal?.throwIfAborted()</c>).</summary>
    public void ThrowIfCancellationRequested()
    {
        if (signal is { Token.IsCancellationRequested: true }) ExceptionDispatchInfo.Throw(signal.Error());
    }

    /// <summary>Reads a value; false when no derivation on the chain set <paramref name="key"/> (upstream <c>value(key)</c>).</summary>
    public bool TryGet<T>(ContextKey<T> key, out T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        for (var node = this; node is not null; node = node.parent)
        {
            if (!ReferenceEquals(node.key, key)) continue;
            value = (T)node.value!;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>Reads a value, or <c>default</c> when it is not set (upstream <c>value(key)</c>, which returns undefined).</summary>
    public T? Get<T>(ContextKey<T> key) => TryGet(key, out var value) ? value : default;

    /// <summary>Derives a context containing one additional or replaced value (<c>withContextValue</c>).</summary>
    public DurableContext With<T>(ContextKey<T> key, T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new DurableContext(this, key, value);
    }

    /// <summary>
    /// Derives a context cancelled by either this context's signal or <paramref name="cancellationToken"/> (<c>withAbortSignal</c>).
    /// Without a parent signal the token is used as is; otherwise the two are combined (<c>AbortSignal.any</c>: an already-cancelled
    /// source wins in parent-first order, else whichever cancels first supplies the reason). This context is unchanged.
    /// </summary>
    public DurableContext WithCancellation(CancellationToken cancellationToken)
    {
        var own = new CancellationSignal(cancellationToken);
        return new DurableContext(this, SignalKey, signal is null ? own : CancellationSignal.Any(signal, own));
    }

    /// <summary>
    /// Derives a context retaining every value except caller cancellation (<c>withoutAbortSignal</c>). Intended for mandatory cleanup
    /// only.
    /// </summary>
    public DurableContext WithoutCancellation() => new(this, SignalKey, null);

    /// <summary>Derives an independently cancellable child context (<c>withCancel</c>); cancelling it never cancels this context.</summary>
    public DurableCancellation WithCancel()
    {
        var own = new CancellationSignal();
        return new DurableCancellation(new DurableContext(this, SignalKey, signal is null ? own : CancellationSignal.Any(signal, own)), own);
    }

    /// <summary>
    /// Observes <paramref name="task"/> until it completes or this context is cancelled (<c>awaitWithContext</c>). Cancellation fails
    /// only this waiter with the cancellation error; it does not cancel the underlying work. Without a signal the task is returned as is.
    /// </summary>
    public Task<T> WaitAsync<T>(Task<T> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var observed = signal;
        if (observed is null) return task;
        if (observed.Token.IsCancellationRequested) return Failed<T>(observed);
        if (task.IsCompleted) return task;
        var waiter = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = observed.Token.UnsafeRegister(static state =>
        {
            var (waiter, observed) = ((TaskCompletionSource<T>, CancellationSignal))state!;
            Fail(waiter, observed);
        }, (waiter, observed));
        task.ContinueWith(static (completed, state) =>
        {
            var (waiter, registration) = ((TaskCompletionSource<T>, CancellationTokenRegistration))state!;
            registration.Unregister();
            if (completed.IsCompletedSuccessfully) waiter.TrySetResult(completed.Result);
            else if (completed.IsFaulted) waiter.TrySetException(completed.Exception!.InnerExceptions);
            else waiter.TrySetCanceled(CanceledToken(completed));
        }, (waiter, registration), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return waiter.Task;
    }

    /// <inheritdoc cref="WaitAsync{T}(Task{T})"/>
    public Task WaitAsync(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (signal is null) return task;
        return WaitAsync(task.ContinueWith(static completed =>
        {
            completed.GetAwaiter().GetResult();
            return true;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
    }

    /// <summary>The chain, e.g. <c>[Context BACKGROUND_CONTEXT].WithValue(first).WithValue(chord.abortSignal)</c>.</summary>
    public override string ToString() => name ?? $"{parent}.WithValue({key})";

    private static Task<T> Failed<T>(CancellationSignal observed)
    {
        var waiter = new TaskCompletionSource<T>();
        Fail(waiter, observed);
        return waiter.Task;
    }

    private static void Fail<T>(TaskCompletionSource<T> waiter, CancellationSignal observed)
    {
        if (observed.Reason is Exception reason) waiter.TrySetException(reason);
        else waiter.TrySetCanceled(observed.Token);
    }

    private static CancellationToken CanceledToken(Task completed)
    {
        try { completed.GetAwaiter().GetResult(); }
        catch (OperationCanceledException error) { return error.CancellationToken; }
        return CancellationToken.None;
    }
}

/// <summary>An independently cancellable child context and its cancel function (the result of <c>withCancel</c>).</summary>
public sealed class DurableCancellation
{
    private readonly CancellationSignal signal;

    internal DurableCancellation(DurableContext context, CancellationSignal signal)
    {
        Context = context;
        this.signal = signal;
    }

    /// <summary>The child context.</summary>
    public DurableContext Context { get; }

    /// <summary>
    /// Cancels the child (upstream <c>cancel(reason)</c>). The first call wins; later calls do nothing. Registered callbacks run
    /// synchronously on this thread, as <see cref="CancellationTokenSource.Cancel()"/> runs them.
    /// </summary>
    public void Cancel(object? reason = null) => signal.Cancel(reason);
}

/// <summary>One cancellation signal: a token plus the reason of the source that cancelled it (an <c>AbortSignal</c>).</summary>
internal sealed class CancellationSignal
{
    private readonly CancellationTokenSource? source;
    private readonly Lock gate = new();
    private object? reason;
    private CancellationTokenRegistration[] links = [];

    /// <summary>An owned signal, cancelled through <see cref="Cancel"/>.</summary>
    public CancellationSignal()
    {
        source = new CancellationTokenSource();
        Token = source.Token;
    }

    /// <summary>A plain token; it carries no reason.</summary>
    public CancellationSignal(CancellationToken token) { Token = token; }

    public CancellationToken Token { get; }

    public object? Reason { get { lock (gate) return reason; } }

    /// <summary>The error a waiter fails with: the reason when it is an exception, else an <see cref="OperationCanceledException"/>.</summary>
    public Exception Error() => Reason as Exception ?? new OperationCanceledException("The operation was aborted", Token);

    public void Cancel(object? cancelReason)
    {
        CancellationTokenRegistration[] release;
        lock (gate)
        {
            if (source is null || source.IsCancellationRequested) return;
            reason = cancelReason;
            release = links;
            links = [];
        }
        foreach (var link in release) link.Unregister();
        source.Cancel();
    }

    /// <summary><c>AbortSignal.any([first, second])</c>.</summary>
    public static CancellationSignal Any(CancellationSignal first, CancellationSignal second)
    {
        var combined = new CancellationSignal();
        foreach (var input in new[] { first, second })
        {
            if (!input.Token.IsCancellationRequested) continue;
            combined.Cancel(input.Reason);
            return combined;
        }
        var registrations = new CancellationTokenRegistration[2];
        lock (combined.gate) combined.links = registrations;
        registrations[0] = first.Token.UnsafeRegister(static state => Propagate(state!), (combined, first));
        registrations[1] = second.Token.UnsafeRegister(static state => Propagate(state!), (combined, second));
        return combined;
    }

    private static void Propagate(object state)
    {
        var (combined, input) = ((CancellationSignal, CancellationSignal))state;
        combined.Cancel(input.Reason);
    }
}
