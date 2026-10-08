using System.Runtime.ExceptionServices;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent;

public abstract record SessionRetryEvent;
public sealed record SessionRetryStarted(int Attempt, int MaxAttempts, long DelayMs, string ErrorMessage) : SessionRetryEvent;
public sealed record SessionRetryEnded(bool Success, int Attempt, string? FinalError = null) : SessionRetryEvent;

/// <summary>One session operation's retry budget and owned backoff. The host owns provider and durable omission admission.</summary>
public sealed class SessionRetryCoordinator
{
    private readonly object gate = new();
    private readonly Func<AgentRetryPolicy> policy;
    private readonly Func<SessionRetryEvent, Task> observer;
    private readonly Func<long, CancellationToken, Task> delay;
    private readonly AsyncLocal<bool> callback = new();
    [ThreadStatic] private static List<SessionRetryCoordinator>? cancellationOwners;
    private int attempt;
    private TaskCompletionSource<bool>? preparing;
    private TaskCompletionSource? finishing;
    private Backoff? backoff;
    private Task? lastAbort;
    public bool IsRetrying { get { lock (gate) return backoff is not null; } }
    public int Attempt { get { lock (gate) return attempt; } }
    public bool IsOwnedCallback => callback.Value || cancellationOwners?.Contains(this) == true;

    public SessionRetryCoordinator(Func<AgentRetryPolicy> livePolicy, Func<SessionRetryEvent, Task> awaitedObserver,
        Func<long, CancellationToken, Task>? originalDelay = null)
    {
        policy = Single(livePolicy ?? throw new ArgumentNullException(nameof(livePolicy)), nameof(livePolicy));
        observer = Single(awaitedObserver ?? throw new ArgumentNullException(nameof(awaitedObserver)), nameof(awaitedObserver));
        delay = originalDelay is null ? DelayOriginalAsync : Single(originalDelay, nameof(originalDelay));
    }

    public Task<bool> PrepareRetryAsync(StopReason reason, string? errorMessage, bool contextOverflow,
        Func<Task> omitOriginal, CancellationToken runToken = default)
    {
        ThrowSelfWait(); ArgumentNullException.ThrowIfNull(omitOriginal);
        Single(omitOriginal, nameof(omitOriginal));
        TaskCompletionSource<bool> owner;
        lock (gate)
        {
            if (preparing is not null || finishing is not null) throw new InvalidOperationException("Retry boundary already owns an original operation.");
            owner = new(TaskCreationOptions.RunContinuationsAsynchronously); preparing = owner;
        }
        _ = PrepareOwnedAsync(owner, reason, errorMessage, contextOverflow, omitOriginal, runToken);
        return owner.Task;
    }

    private async Task PrepareOwnedAsync(TaskCompletionSource<bool> owner, StopReason reason, string? errorMessage,
        bool overflow, Func<Task> omit, CancellationToken runToken)
    {
        var failures = new List<Exception>(); var retry = false; Backoff? admitted = null; var scheduled = false;
        try
        {
            if (!runToken.IsCancellationRequested && !overflow && AgentRetryPolicy.IsRetryableError(reason, errorMessage))
            {
                var settings = InvokeCallback(policy);
                int next;
                lock (gate) { next = settings.Enabled && attempt < settings.MaxRetries ? ++attempt : 0; }
                if (next != 0)
                {
                    scheduled = true;
                    await ObserveAsync(new SessionRetryStarted(next, settings.MaxRetries, settings.DelayMs(next), errorMessage!)).ConfigureAwait(false);
                    // Await the admitted original, including checkpoint/context refresh, without a cancellable wrapper.
                    await InvokeTask(omit).ConfigureAwait(false);
                    var ownedBackoff = new Backoff(this, owner.Task); admitted = ownedBackoff;
                    lock (gate) { backoff = ownedBackoff; lastAbort = null; }
                    using var registration = runToken.UnsafeRegister(static state => ((Backoff)state!).Cancel(), ownedBackoff);
                    try { await InvokeTask(() => delay(settings.DelayMs(next), ownedBackoff.Stop.Token)).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ownedBackoff.Stop.IsCancellationRequested) { }
                    retry = !ownedBackoff.Stop.IsCancellationRequested && !runToken.IsCancellationRequested;
                    if (!retry) await EndAsync(false, "Retry cancelled").ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) { Add(failures, error); }
        finally
        {
            if (admitted is not null)
            {
                lock (gate) { if (ReferenceEquals(backoff, admitted)) backoff = null; }
                foreach (var error in admitted.Close()) Add(failures, error);
            }
            // Preserve a body failure and independently join the final observer even when it also faults.
            if (failures.Count != 0 && scheduled)
                try { await EndAsync(false, failures[0].Message).ConfigureAwait(false); } catch (Exception error) { Add(failures, error); }
            lock (gate) { if (ReferenceEquals(preparing, owner)) preparing = null; }
            Complete(owner, retry, failures);
        }
    }

    /// <summary>Call on each acknowledged assistant response; a non-error resets the counter within multi-turn runs.</summary>
    public Task CompleteAssistantAsync(StopReason reason, string? errorMessage = null)
        => reason == StopReason.Error ? Task.CompletedTask : FinishAsync(reason, errorMessage);

    /// <summary>Call when the provider operation will no longer continue, including exhausted/nonretryable errors.</summary>
    public Task FinishAsync(StopReason reason, string? errorMessage = null)
        => FinishBoundaryAsync(reason != StopReason.Error, reason == StopReason.Error ? errorMessage : null);

    /// <summary>Root calls after cancelled provider/boundary settlement when a scheduled retry remains uncompleted.</summary>
    public Task FinishCancelledAsync() => FinishBoundaryAsync(false, "Retry cancelled");

    private Task FinishBoundaryAsync(bool success, string? finalError)
    {
        ThrowSelfWait(); TaskCompletionSource owner;
        lock (gate)
        {
            if (preparing is not null) throw new InvalidOperationException("Retry preparation must settle before terminal observation.");
            if (finishing is not null) return finishing.Task;
            if (attempt == 0) return Task.CompletedTask;
            owner = new(TaskCreationOptions.RunContinuationsAsynchronously); finishing = owner;
        }
        _ = FinishOwnedAsync(owner, success, finalError); return owner.Task;
    }
    private async Task FinishOwnedAsync(TaskCompletionSource owner, bool success, string? finalError)
    {
        Exception? failure = null;
        try { await EndAsync(success, finalError).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        finally { lock (gate) { if (ReferenceEquals(finishing, owner)) finishing = null; } }
        if (failure is null) owner.TrySetResult(); else owner.TrySetException(failure);
    }
    private async Task EndAsync(bool success, string? finalError)
    {
        int completed; lock (gate) { completed = attempt; attempt = 0; }
        if (completed != 0) await ObserveAsync(new SessionRetryEnded(success, completed, finalError)).ConfigureAwait(false);
    }

    /// <summary>Cancel only an admitted backoff, then join its original delay and final observer. Repeated calls retain one task.</summary>
    public Task AbortRetryAsync()
    {
        ThrowSelfWait(); Backoff? owned; TaskCompletionSource abort;
        lock (gate)
        {
            owned = backoff;
            if (owned is null) return lastAbort ?? Task.CompletedTask;
            if (owned.Abort is not null) return owned.Abort.Task;
            abort = new(TaskCreationOptions.RunContinuationsAsynchronously); owned.Abort = abort; lastAbort = abort.Task;
        }
        owned.Cancel(); _ = JoinAbortAsync(owned, abort); return abort.Task;
    }
    private static async Task JoinAbortAsync(Backoff owned, TaskCompletionSource abort)
    {
        try { await owned.Preparation.ConfigureAwait(false); abort.TrySetResult(); }
        catch (Exception error) { abort.TrySetException(error); }
    }
    /// <summary>Joins every coordinator original already admitted at capture; root must close session admission first.</summary>
    public Task JoinAsync()
    {
        ThrowSelfWait(); Task[] originals;
        lock (gate) originals = new Task?[] { preparing?.Task, finishing?.Task, lastAbort }.OfType<Task>().Distinct().ToArray();
        return JoinAllAsync(originals);
    }
    private static async Task JoinAllAsync(Task[] originals)
    {
        var failures = new List<Exception>();
        foreach (var original in originals) try { await original.ConfigureAwait(false); } catch (Exception error) { Add(failures, error); }
        ThrowFailures(failures);
    }
    private Task ObserveAsync(SessionRetryEvent value) => InvokeTask(() => observer(value));
    private T InvokeCallback<T>(Func<T> invoke)
    {
        var previous = callback.Value; callback.Value = true;
        try { return invoke(); } finally { callback.Value = previous; }
    }
    private async Task InvokeTask(Func<Task> invoke)
    {
        var previous = callback.Value; callback.Value = true;
        try
        {
            var original = invoke() ?? throw new InvalidOperationException("Retry callback returned no original task.");
            try { await original.ConfigureAwait(false); }
            catch (Exception error)
            {
                var failures = new List<Exception>(); Add(failures, error);
                // Preserve every physical fault before this async wrapper replaces the admitted original.
                if (original.Exception is { } aggregate) Add(failures, aggregate);
                ThrowFailures(failures);
            }
        }
        finally { callback.Value = previous; }
    }
    private void ThrowSelfWait()
    {
        if (IsOwnedCallback)
            throw new InvalidOperationException("Retry-owned callbacks cannot await their own coordinator settlement.");
    }
    private static T Single<T>(T value, string name) where T : Delegate
    {
        if (value.GetInvocationList().Length != 1)
            throw new ArgumentException("Retry injection requires one explicitly owned callback.", name);
        return value;
    }
    private sealed class Backoff(SessionRetryCoordinator owner, Task preparation)
    {
        internal readonly CancellationTokenSource Stop = new();
        internal readonly Task Preparation = preparation;
        internal readonly List<Exception> Errors = [];
        internal TaskCompletionSource? Abort;
        private readonly object lifetime = new();
        private bool cancellationRequested, closed;
        internal void Cancel()
        {
            lock (lifetime)
            {
                if (cancellationRequested || closed) return;
                cancellationRequested = true;
                var owners = cancellationOwners ??= []; owners.Add(owner);
                try { Stop.Cancel(); } catch (Exception error) { Add(Errors, error); }
                finally { owners.RemoveAt(owners.Count - 1); }
            }
        }
        internal Exception[] Close()
        { lock (lifetime) { closed = true; Stop.Dispose(); return Errors.ToArray(); } }
    }
    private static async Task DelayOriginalAsync(long milliseconds, CancellationToken token)
    {
        do
        {
            var chunk = Math.Min(milliseconds, int.MaxValue - 1L);
            await Task.Delay((int)chunk, token).ConfigureAwait(false); milliseconds -= chunk;
        } while (milliseconds > 0);
    }
    private static void Add(List<Exception> failures, Exception error)
    {
        if (error is AggregateException { InnerExceptions.Count: > 0 } aggregate) { foreach (var inner in aggregate.InnerExceptions) Add(failures, inner); }
        else if (!failures.Any(value => ReferenceEquals(value, error))) failures.Add(error);
    }
    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count != 0) throw new AggregateException(failures);
    }
    private static void Complete(TaskCompletionSource<bool> owner, bool result, List<Exception> failures)
    {
        if (failures.Count == 0) owner.TrySetResult(result);
        else owner.TrySetException(failures.Count == 1 ? failures[0] : new AggregateException(failures));
    }
}
