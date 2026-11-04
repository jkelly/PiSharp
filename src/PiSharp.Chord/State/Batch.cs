// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/state/batch.ts
//
// The batch (W1), one per host: a commit adopts inside it, then every watched value that changed gets one frame, then the flushes it
// collected run (a link sends one message); Chord calls that would reenter it are refused while it runs. Mappings:
// - BatchError is a record of a BatchErrorKind and the error (an Exception: .NET throws nothing else).
// - Upstream's `soon` runs on the next microtask. .NET has none, so a batch takes its host's scheduler (`schedule`, which runs the work it
//   is given later and never inline; by default the thread pool), as a watch does (Watch.cs); the sources on the batch give it to the
//   watches they make, so a host's frames and its `soon` commits run where the host says.
// - A batch is its host's and is not thread-safe (Internal.cs): its flags and lists are upstream's single-threaded ones. A commit walks its
//   lists by index, so a source a delivery lists late is delivered too, as upstream's for-of over a growing array does.
using PiSharp.Chord.Data;
using PiSharp.Telemetry;

namespace PiSharp.Chord.State;

/// <summary>What a batch reports (upstream <c>BatchError["kind"]</c>).</summary>
internal enum BatchErrorKind
{
    /// <summary>A watch listener threw.</summary>
    ListenerError,

    /// <summary>A frame delivery or a flush threw (a derive's value is no JSON).</summary>
    DeliveryFailed,
}

/// <summary>What a batch reports (upstream <c>BatchError</c>): a watch listener that threw, or a frame delivery that threw.</summary>
internal sealed record BatchError(BatchErrorKind Kind, Exception Error);

/// <summary>What a commit lists to get its frame delivered (upstream <c>Delivered</c>): a watched source it changed (JSON or native).</summary>
internal interface IDelivered
{
    /// <summary>Delivers this batch's frame to its watches and subscribers; called once per batch that listed it.</summary>
    void Deliver(ChordContext context);
}

/// <summary>A host's batch (upstream <c>Batch</c>): the reentrancy flag, the watched sources a commit changed, and where background errors
/// go.</summary>
internal sealed class Batch
{
    private static readonly Action<Action> ThreadPoolSchedule = static work => ThreadPool.UnsafeQueueUserWorkItem(static run => run(), work, preferLocal: false);

    private readonly Action<BatchError> report;
    private readonly ITelemetryMetrics? metrics;
    // Set while adopting, delivering or computing a derive; one flag per host.
    private bool running;
    // Set while a pure compute runs (a derive's, a registry's compose): nothing may change a source then.
    private bool computing;
    // The watched sources the current commit changed, each listed once by its Changed.
    private List<IDelivered> changed = [];
    // What runs once the current commit's frames were delivered, each listed once by its Flushing.
    private List<Action<ChordContext>> flushes = [];
    // Set while the current commit's flushes run (its links send).
    private bool sending;
    // Changes Within deferred while the flushes sent, for the next commit.
    private List<Action> later = [];
    // Changes Soon collected for the commit the scheduler runs next, while one is queued.
    private List<Action>? soon;

    /// <param name="report">Gets what the batch reports (the host's <c>onError</c>).</param>
    /// <param name="metrics">The host's metrics, which <see cref="Count"/> counts on; null counts nothing.</param>
    /// <param name="schedule">Runs work later, never inline (upstream <c>queueMicrotask</c>); by default the thread pool.</param>
    public Batch(Action<BatchError> report, ITelemetryMetrics? metrics, Action<Action>? schedule = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        this.report = report;
        ReportListener = error => report(new BatchError(BatchErrorKind.ListenerError, error));
        this.metrics = metrics;
        Schedule = schedule ?? ThreadPoolSchedule;
    }

    /// <summary>Reports a throwing watch listener's error (to the host's <c>onError</c>); what watches get as their <c>report</c>.</summary>
    public Action<Exception> ReportListener { get; }

    /// <summary>The host's scheduler: the watches made on this batch deliver through it, and <see cref="Soon"/> commits through it.</summary>
    public Action<Action> Schedule { get; }

    /// <summary>Whether a commit's adoption, a delivery or a derive compute runs now: a change made now lands in this batch.</summary>
    public bool Running => running;

    /// <summary>Throws <c>invalid/reentrant</c> inside the batch: <paramref name="operation"/> may not run during adoptions or derive
    /// computes.</summary>
    public void AssertOutside(string operation)
    {
        if (running) throw DataInternal.Invalid("reentrant", $"{operation} inside a commit or a derive compute");
    }

    /// <summary>
    /// Throws <c>invalid/reentrant</c> inside a pure compute (a derive's, a registry's <c>compose</c>): <paramref name="operation"/> would
    /// change a source while one is computed from it. Changes inside an adoption are fine (they land in its batch).
    /// </summary>
    public void AssertNotComputing(string operation)
    {
        if (computing) throw DataInternal.Invalid("reentrant", $"{operation} inside a derive compute or a compose");
    }

    /// <summary>Runs the pure compute <paramref name="work"/> (<see cref="Run{T}"/>, and changes refused: <see cref="AssertNotComputing"/>);
    /// nests.</summary>
    public T Compute<T>(Func<T> work)
    {
        var wasRunning = running;
        var wasComputing = computing;
        running = true;
        computing = true;
        try
        {
            return work();
        }
        finally
        {
            running = wasRunning;
            computing = wasComputing;
        }
    }

    /// <summary>Runs <paramref name="work"/> with reentrant calls refused; nests (a derive compute inside a commit).</summary>
    public T Run<T>(Func<T> work)
    {
        var outer = running;
        running = true;
        try
        {
            return work();
        }
        finally
        {
            running = outer;
        }
    }

    /// <summary>
    /// Runs one commit's adoptions, then, still synchronously, delivers a frame for every watched value they changed (watched derives
    /// recompute through their reads), then runs the flushes the deliveries listed. A delivery or flush that throws is reported; the others
    /// still run. What <see cref="Within"/> deferred while the flushes sent runs as the next commit, right after.
    /// </summary>
    public void Commit(Action adopt, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(adopt);
        ArgumentNullException.ThrowIfNull(context);
        Run(() =>
        {
            try
            {
                adopt();
                for (var index = 0; index < changed.Count; index++)
                {
                    try
                    {
                        changed[index].Deliver(context);
                    }
                    catch (Exception error)
                    {
                        report(new BatchError(BatchErrorKind.DeliveryFailed, error));
                    }
                }
                sending = true;
                for (var index = 0; index < flushes.Count; index++)
                {
                    try
                    {
                        flushes[index](context);
                    }
                    catch (Exception error)
                    {
                        report(new BatchError(BatchErrorKind.DeliveryFailed, error));
                    }
                }
            }
            finally
            {
                sending = false;
                changed = [];
                flushes = [];
            }
            return true;
        });
        var deferred = later;
        if (deferred.Count == 0) return;
        later = [];
        Commit(() =>
        {
            foreach (var each in deferred) each();
        }, context);
    }

    /// <summary>
    /// Runs <paramref name="adopt"/> inside the commit running now, which delivers what it changed; outside one, as a commit of its own;
    /// while a commit's flushes send, as the next commit (a link closing while it sends ends its holds: their departure lands in the next
    /// batch, so one commit stays one message per link and origin).
    /// </summary>
    public void Within(Action adopt, ChordContext context)
    {
        if (sending) later.Add(adopt);
        else if (running) adopt();
        else Commit(adopt, context);
    }

    /// <summary>
    /// Runs <paramref name="adopt"/> in one commit the scheduler runs next, together with every other <see cref="Soon"/> until then
    /// (changes arriving outside any commit: one batch for all of them). <paramref name="context"/> is the first one's.
    /// </summary>
    public void Soon(Action adopt, ChordContext context)
    {
        if (soon is not null)
        {
            soon.Add(adopt);
            return;
        }
        List<Action> collected = [adopt];
        soon = collected;
        Schedule(() =>
        {
            soon = null;
            Commit(() =>
            {
                foreach (var each in collected) each();
            }, context);
        });
    }

    /// <summary>Lists a watched source the current commit changed.</summary>
    public void Changed(IDelivered source) => changed.Add(source);

    /// <summary>Lists <paramref name="flush"/> to run at the end of the current commit, after its deliveries.</summary>
    public void Flushing(Action<ChordContext> flush) => flushes.Add(flush);

    /// <summary>Counts one unit of derive or registry work on the host's metrics (<c>chord.derive.computes</c>, <c>chord.derive.diffs</c>,
    /// <c>chord.registry.composes</c>, <c>chord.registry.adapts</c>, <c>chord.contract.adapts</c>; a lazy read has no context to count
    /// through).</summary>
    public void Count(string counter) => metrics?.Counter(counter).Add(1);
}
