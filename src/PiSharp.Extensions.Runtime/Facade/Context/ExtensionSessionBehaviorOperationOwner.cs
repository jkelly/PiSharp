using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using PiSharp.Extensions.Facade.Context;

namespace PiSharp.Extensions.Runtime.Facade.Context;

/// <summary>Nested adapter custody. Root must join CloseAsync from the existing command settlement.
/// Public completion tasks are maps; CaptureOriginals identifies the actual supplier/callback tasks.</summary>
public sealed class ExtensionSessionBehaviorOperationOwner : IAsyncDisposable
{
    private sealed class Work
    {
        internal readonly TaskCompletionSource<Task> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<Exception> Failures = [];
        internal readonly List<Exception> CanceledOriginalErrors = [];
    }
    private readonly object gate = new();
    private readonly IExtensionContext context;
    private readonly Action validateInvocation;
    private readonly List<Work> work = [];
    private readonly List<ExtensionBehaviorOriginalEvidence> evidence = [];
    private readonly Dictionary<Task, ExtensionBehaviorOriginalEvidence> cached = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Task> joined = new(ReferenceEqualityComparer.Instance);
    private sealed record Ancestry(ExtensionSessionBehaviorOperationOwner Owner, Ancestry? LogicalParent, Ancestry? PhysicalParent)
    {
        internal bool Contains(ExtensionSessionBehaviorOperationOwner target)
        {
            var pending = new Stack<Ancestry>(); pending.Push(this);
            var visited = new HashSet<Ancestry>(ReferenceEqualityComparer.Instance);
            while (pending.TryPop(out var frame))
            {
                if (!visited.Add(frame)) continue;
                if (ReferenceEquals(frame.Owner, target)) return true;
                if (frame.LogicalParent is { } logical) pending.Push(logical);
                if (frame.PhysicalParent is { } physical) pending.Push(physical);
            }
            return false;
        }
    }
    private static readonly AsyncLocal<Ancestry?> Callback = new();
    [ThreadStatic] private static Ancestry? cancellationThreadOwners;
    private Task? close;
    private Task? settlementOriginal;
    private bool closed;

    public ExtensionSessionBehaviorOperationOwner(IExtensionContext context, Action validateInvocation)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(validateInvocation);
        if (validateInvocation.GetInvocationList().Length != 1) throw new ArgumentException("One actual invocation validator required.");
        this.context = context; this.validateInvocation = validateInvocation;
    }
    private void Check()
    {
        validateInvocation();
        context.OperationCancellationToken.ThrowIfCancellationRequested();
        context.SessionCancellationToken.ThrowIfCancellationRequested();
        context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        lock (gate) if (closed) throw new InvalidOperationException("The originating adapter admission is closed.");
    }
    private Task<T> Start<T>(Func<Work, Task<T>> run)
    {
        Check(); var slot = new Work();
        lock (gate) { if (closed || work.Count == 128) throw new InvalidOperationException("Adapter admission closed or bounded work limit reached."); work.Add(slot); }
        var completion = RunMarked(slot, run);
        slot.Started.SetResult(completion); return completion;
    }
    private async Task<T> RunMarked<T>(Work slot, Func<Work, Task<T>> run)
    {
        var prior = Callback.Value; Callback.Value = new(this, prior, cancellationThreadOwners);
        try { return await run(slot).ConfigureAwait(false); }
        catch (Exception error)
        {
            lock (gate) AddLeaves(slot.Failures, error);
            if (error is OperationCanceledException)
            {
                bool actualCancellation;
                lock (gate) actualCancellation = slot.CanceledOriginalErrors.Any(actual => ReferenceEquals(actual, error));
                if (!actualCancellation) throw new InvalidOperationException("An adapter supplier threw cancellation without an actual canceled original.", error);
            }
            throw;
        }
        finally { Callback.Value = prior; }
    }
    private async Task JoinOriginal(Work slot, Task original, string phase)
    {
        ArgumentNullException.ThrowIfNull(original); Exception? observed = null;
        lock (gate)
        {
            if (!cached.TryGetValue(original, out var pending))
            { pending = new(phase, original, null, null); cached.Add(original, pending); }
            evidence.Add(pending with { Phase = phase });
        }
        try { await original.ConfigureAwait(false); }
        catch (Exception error) { observed = error; }
        ExtensionBehaviorOriginalEvidence row;
        lock (gate)
        {
            row = cached[original];
            if (joined.Add(original))
            {
                row = new(phase, original, original.IsFaulted ? original.Exception : null, observed);
                cached[original] = row;
                for (var index = 0; index < evidence.Count; index++)
                    if (ReferenceEquals(evidence[index].Original, original)) evidence[index] = row with { Phase = evidence[index].Phase };
            }
            if (row.Aggregate is not null) AddLeaves(slot.Failures, row.Aggregate);
            else if (row.Observed is not null) AddLeaves(slot.Failures, row.Observed);
            if (original.IsCanceled && observed is not null) AddLeaves(slot.CanceledOriginalErrors, observed);
        }
        if (observed is not null)
        {
            // Throwing a faulted original's OCE alone would change the map to Canceled.
            if (original.IsFaulted && observed is OperationCanceledException)
                throw new ExtensionFacadeOriginalFaultException(original, row.Aggregate!);
            ExceptionDispatchInfo.Capture(observed).Throw();
        }
    }
    public Task<T?> WithSessionAsync<T>(Func<Task<T?>> acquireOriginal, Func<T, IExtensionCommandContext> actualContext,
        ExtensionWithSessionCallback? withSession = null, Func<ExtensionBehaviorCallbackSettlement>? settleWithSession = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(acquireOriginal); ArgumentNullException.ThrowIfNull(actualContext);
        ValidateSingle(acquireOriginal); ValidateSingle(actualContext); if (withSession is not null) ValidateSingle(withSession);
        if(settleWithSession is not null){ValidateSingle(settleWithSession);if(withSession is null)throw new ArgumentException("Callback settlement requires its actual admitted callback.");}
        return Start<T?>(async slot =>
        {
            var original = acquireOriginal() ?? throw new InvalidOperationException("No actual replacement original.");
            await JoinOriginal(slot, original, "replacement").ConfigureAwait(false);
            var result = original.GetAwaiter().GetResult();
            if (result is not null && withSession is not null)
            {
                var fresh = actualContext(result) ?? throw new InvalidOperationException("No actual returned command context.");
                // The retired source session token is intentionally not reused for the fresh callback.
                Exception? failure=null;
                try
                {
                    var callback = withSession(fresh, fresh.OperationCancellationToken).AsTask();
                    await JoinOriginal(slot, callback, "with-session").ConfigureAwait(false);
                }
                catch(Exception error){failure=error;}
                // Begin cleanup even if the callback factory throws before returning a task.
                // Each child scope belongs to this already captured replacement work slot.
                if(settleWithSession is not null)
                {
                    try
                    {
                        var settlement=settleWithSession()??throw new InvalidOperationException("No actual callback scope settlement.");
                        ValidateSingle(settlement.ReadCompletedFailures);
                        await JoinOriginal(slot,settlement.Original,"with-session-scope-settlement").ConfigureAwait(false);
                        foreach(var error in settlement.ReadCompletedFailures())
                        {
                            lock(gate)AddLeaves(slot.Failures,error);
                            failure=failure is null?error:new AggregateException(failure,error);
                        }
                    }
                    catch(Exception error){failure=failure is null?error:new AggregateException(failure,error);}
                }
                if(failure is not null)ExceptionDispatchInfo.Capture(failure).Throw();
            }
            return result;
        });
    }
    public Task CompactAsync(Func<ExtensionBehaviorCompactionWork> acquireOriginal,
        ExtensionBehaviorCompactionCallbacks? callbacks = null)
    {
        ArgumentNullException.ThrowIfNull(acquireOriginal); ValidateSingle(acquireOriginal);
        if (callbacks?.OnComplete is { } complete) ValidateSingle(complete);
        if (callbacks?.OnError is { } errorCallback) ValidateSingle(errorCallback);
        return Start(async slot =>
        {
            var actual = acquireOriginal() ?? throw new InvalidOperationException("No actual compaction work.");
            ValidateSingle(actual.ReadCompletedResult);
            Exception? failure = null;
            try { await JoinOriginal(slot, actual.Original, "compaction").ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            if (failure is not null)
            {
                // Supply the original direct error, retaining its complete aggregate separately.
                Exception supplied; lock (gate) supplied = cached[actual.Original].Observed ?? failure;
                if (callbacks?.OnError is { } onError)
                {
                    try { await JoinOriginal(slot, onError(supplied, context.OperationCancellationToken).AsTask(), "compaction-error-callback").ConfigureAwait(false); }
                    catch (Exception callbackError) { throw new AggregateException(failure, callbackError); }
                }
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
            var result = actual.ReadCompletedResult();
            if (result is not null && callbacks?.OnComplete is { } onComplete)
                await JoinOriginal(slot, onComplete(result, context.OperationCancellationToken).AsTask(), "compaction-complete-callback").ConfigureAwait(false);
            return true;
        });
    }
    public Task SetThinkingLevelAsync(IExtensionDirectSessionBehaviorHost host, string level)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (context is not IExtensionCommandContext command) throw new InvalidOperationException("Thinking mutation requires actual command admission.");
        return Start(async slot => { await JoinOriginal(slot, host.SetThinkingLevel(command, level, context.OperationCancellationToken), "thinking-configuration").ConfigureAwait(false); return true; });
    }
    public void Abort(IExtensionDirectSessionBehaviorHost host)
    {
        ArgumentNullException.ThrowIfNull(host); Check();
        var slot = new Work(); var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (closed || work.Count == 128) throw new InvalidOperationException("Adapter admission closed or bounded work limit reached.");
            work.Add(slot); slot.Started.SetResult(completed.Task);
        }
        var prior = cancellationThreadOwners; cancellationThreadOwners = new(this, Callback.Value, prior);
        try { host.Abort(context); completed.SetResult(); }
        catch (Exception error)
        {
            lock (gate) AddLeaves(slot.Failures, error);
            completed.SetException(error); throw;
        }
        finally { cancellationThreadOwners = prior; }
    }
    public ImmutableArray<ExtensionBehaviorOriginalEvidence> CaptureOriginals()
    { lock (gate) return evidence.ToImmutableArray(); }
    public Task CloseAsync()
    {
        if (Callback.Value?.Contains(this) == true || cancellationThreadOwners?.Contains(this) == true)
            throw new InvalidOperationException("An adapter callback/cancellation registration cannot await its own settlement.");
        TaskCompletionSource<Task> started; Work[] captured;
        lock (gate)
        {
            if (close is not null) return close;
            closed = true; captured = work.ToArray(); started = new(TaskCreationOptions.RunContinuationsAsynchronously); close = JoinSettlement(started.Task);
        }
        var original = Settle(captured);
        lock (gate) settlementOriginal = original;
        started.SetResult(original); return close!;
    }
    private static async Task JoinSettlement(Task<Task> started)
    { var original = await started.ConfigureAwait(false); await original.ConfigureAwait(false); }
    public Task? SettlementOriginal { get { lock (gate) return settlementOriginal; } }
    private async Task Settle(Work[] captured)
    {
        var failures = new List<Exception>();
        foreach (var slot in captured)
        {
            var mapped = await slot.Started.Task.ConfigureAwait(false);
            try { await mapped.ConfigureAwait(false); } catch (Exception) { /* Inventory is retained by RunMarked/JoinOriginal. */ }
            lock (gate) foreach (var error in slot.Failures) AddLeaves(failures, error);
        }
        if (failures.Count == 1 && failures[0] is not OperationCanceledException) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count != 0) throw new AggregateException("Session behavior original failures.", failures);
    }
    private static void ValidateSingle(Delegate callback)
    { if (callback.GetInvocationList().Length != 1) throw new ArgumentException("Exactly one admitted callback required."); }
    private static void AddLeaves(List<Exception> target, Exception error)
    {
        if (error is AggregateException { InnerExceptions.Count: > 0 } aggregate) { foreach (var child in aggregate.InnerExceptions) AddLeaves(target, child); return; }
        if (error is ExtensionFacadeOriginalFaultException original) { AddLeaves(target, original.OriginalException); return; }
        if (!target.Any(existing => ReferenceEquals(existing, error))) target.Add(error);
    }
    public ValueTask DisposeAsync() => new(CloseAsync());
}
