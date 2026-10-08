using System.Collections.Immutable;
using System.Runtime.ExceptionServices;

namespace PiSharp.CodingAgent;

public sealed record SessionBoundaryOriginalEvidence(string Phase, Task Original,
    AggregateException? Fault, Exception? Observed);

/// <summary>Captures real originals once; wrappers never substitute for their raw evidence.</summary>
internal sealed class SessionBoundaryOriginals
{
    private readonly object gate = new();
    private readonly List<SessionBoundaryOriginalEvidence> rows = [];
    internal ImmutableArray<SessionBoundaryOriginalEvidence> Snapshot()
    { lock (gate) return rows.ToImmutableArray(); }
    internal Task Capture(Task task, string phase)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (gate)
        {
            if (rows.Any(row => ReferenceEquals(row.Original, task))) return task;
            if (rows.Count >= 512) throw new InvalidOperationException("Session boundary original bound exceeded.");
            rows.Add(new(phase, task, null, null)); return task;
        }
    }
    internal void Retain(SessionBoundaryOriginalEvidence evidence)
    {
        _ = Capture(evidence.Original,evidence.Phase);
        if(evidence.Observed is null)return;
        lock(gate)
        {
            var index=rows.FindIndex(row=>ReferenceEquals(row.Original,evidence.Original));
            if(rows[index].Observed is null)rows[index]=evidence;
        }
    }
    internal async Task Join(Task task, string phase)
    {
        _ = Capture(task, phase);
        try { await task.ConfigureAwait(false); }
        catch (Exception error)
        {
            AggregateException? fault; Exception observed;
            lock (gate)
            {
                var index = rows.FindIndex(row => ReferenceEquals(row.Original, task));
                if (rows[index].Observed is null)
                    rows[index] = rows[index] with { Fault = task.IsFaulted ? task.Exception : null, Observed = error };
                fault = rows[index].Fault; observed = rows[index].Observed!;
            }
            if (fault is not null) throw new SessionBoundaryOriginalFaultException(task, fault, observed);
            if(observed is OperationCanceledException canceled)throw new SessionBoundaryCanceledOriginalException(task,canceled);
            throw;
        }
    }
    internal async Task<T> Join<T>(Task<T> task, string phase)
    { await Join((Task)task, phase).ConfigureAwait(false); return task.GetAwaiter().GetResult(); }
    internal async Task JoinAll(Func<SessionBoundaryOriginalEvidence, bool>? acknowledgedCancellation = null)
    {
        var errors = new List<Exception>();
        foreach (var row in Snapshot())
        {
            if (acknowledgedCancellation?.Invoke(row) == true) continue;
            try { await Join(row.Original, row.Phase).ConfigureAwait(false); }
            catch (Exception error) { Add(errors, error); }
        }
        Throw(errors, Snapshot());
    }
    internal static void Add(List<Exception> errors, Exception error)
    {
        // Keep this carrier: its Task, first full Aggregate and direct await exception
        // are source evidence. Flattening its cache loses that custody outward.
        if (error is SessionBoundaryOriginalFaultException)
        { if (!errors.Any(item => ReferenceEquals(item, error))) errors.Add(error); return; }
        if (error is AggregateException { InnerExceptions.Count: > 0 } aggregate)
        { foreach (var inner in aggregate.InnerExceptions) Add(errors, inner); return; }
        if (!errors.Any(item => ReferenceEquals(item, error))) errors.Add(error);
    }
    internal static void Throw(List<Exception> errors, ImmutableArray<SessionBoundaryOriginalEvidence> originals = default)
    {
        if (errors.Count != 0 && !originals.IsDefault)
            throw new SessionBoundarySettlementException(originals, errors);
        if (errors.Count == 1 && errors[0] is not OperationCanceledException) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count != 0) throw new AggregateException("Session boundary originals failed.", errors);
    }
}

public sealed class SessionBoundarySettlementException(ImmutableArray<SessionBoundaryOriginalEvidence> originals,
    IEnumerable<Exception> failures) : AggregateException("Session boundary originals failed.", failures)
{
    public ImmutableArray<SessionBoundaryOriginalEvidence> Originals { get; } = originals;
}

public sealed class SessionBoundaryCanceledOriginalException(Task original,OperationCanceledException observed)
    : OperationCanceledException("Session boundary original canceled; unchanged source evidence retained.",observed,observed.CancellationToken)
{
    public Task Original {get;}=original;
    public OperationCanceledException Observed {get;}=observed;
}

public sealed class SessionBoundaryOriginalFaultException(Task original, AggregateException cachedFault, Exception observed)
    : Exception("Session boundary original faulted; its unchanged raw fault graph is retained.", observed)
{
    public Task Original { get; } = original;
    public AggregateException CachedFault { get; } = cachedFault;
}
