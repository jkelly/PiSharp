// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/host/unbound-clock.ts
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Host;

/// <summary>One host's clock of unbound requests, reported once after five seconds without ending their waits.</summary>
internal sealed class UnboundClock : IDisposable
{
    private static readonly TimeSpan ReportedAfter = TimeSpan.FromSeconds(5);
    private readonly object gate = new();
    private readonly TimeProvider time;
    private readonly Action<Action> dispatch;
    private readonly Action<HostError> report;
    private readonly Dictionary<object, LinkedListNode<Wait>> requests = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<Wait> waits = new();
    private ITimer? timer;
    private long generation;
    private bool closed;

    internal UnboundClock(TimeProvider time, Action<Action> dispatch, Action<HostError> report)
    {
        this.time = time;
        this.dispatch = dispatch;
        this.report = report;
    }

    /// <summary>Starts one episode; repeated starts leave its original timestamp and error unchanged.</summary>
    internal void Start(object request, string waitingFor, ChordError error)
    {
        lock (gate)
        {
            if (closed || requests.ContainsKey(request)) return;
            var wait = new Wait(request, waitingFor, error, time.GetUtcNow());
            requests.Add(request, waits.AddLast(wait));
            if (timer is null) Arm(ReportedAfter);
        }
    }

    /// <summary>Updates the diagnostic without restarting its five-second interval.</summary>
    internal void Failed(object request, ChordError error)
    {
        lock (gate)
            if (requests.TryGetValue(request, out var node)) node.Value.Error = error;
    }

    /// <summary>A request bound, ended, or was woken; no report remains for this episode.</summary>
    internal void Stop(object request)
    {
        lock (gate)
        {
            if (!requests.Remove(request, out var node)) return;
            waits.Remove(node);
            if (waits.Count == 0) Disarm();
        }
    }

    /// <summary>Closes the host's clock permanently, releasing every request and pending timer.</summary>
    internal void Close()
    {
        lock (gate)
        {
            closed = true;
            requests.Clear();
            waits.Clear();
            Disarm();
        }
    }

    public void Dispose() => Close();

    // Called with gate held. Scheduling first disabled also accommodates a provider which invokes zero-delay timers promptly.
    private void Arm(TimeSpan due)
    {
        var epoch = ++generation;
        timer = time.CreateTimer(_ => dispatch(() => Sweep(epoch)), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        timer.Change(due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void Disarm()
    {
        generation++;
        timer?.Dispose();
        timer = null;
    }

    private void Sweep(long epoch)
    {
        lock (gate)
        {
            // A fired callback may have waited in the dispatcher while Stop/Close replaced its timer.
            if (closed || epoch != generation) return;
            Disarm();
        }
        try
        {
            while (true)
            {
                Wait wait;
                lock (gate)
                {
                    if (closed || waits.First is not { } first) return;
                    if (first.Value.Since + ReportedAfter > time.GetUtcNow()) return;
                    wait = first.Value;
                    requests.Remove(wait.Request);
                    waits.RemoveFirst();
                }
                // Remove before reporting: the callback may stop a later request, close the host, or start a new episode.
                report(new("unbound_long", wait.Error, WaitingFor: wait.WaitingFor, Since: wait.Since.ToUnixTimeMilliseconds()));
            }
        }
        finally
        {
            lock (gate)
            {
                // A reentrant Start may have armed a timer; keep exactly the current earliest wait scheduled.
                Disarm();
                if (!closed && waits.First is { } first) Arm(first.Value.Since + ReportedAfter - time.GetUtcNow());
            }
        }
    }

    private sealed class Wait(object request, string waitingFor, ChordError error, DateTimeOffset since)
    {
        internal object Request { get; } = request;
        internal string WaitingFor { get; } = waitingFor;
        internal ChordError Error { get; set; } = error;
        internal DateTimeOffset Since { get; } = since;
    }
}
