// Decision 0011 (run W4, R0): the events of the runs this process is running, in memory, for the pages that watch them
// (GET /api/runs/{id}/events). Each running run has a log of its normalized events (sequence numbers from 1) and its subscribers; a tab that
// connects late, or reconnects with Last-Event-ID, gets the log after its last sequence number and then the live events. When the run ends
// its final status is published and every subscriber's stream completes; the log stays for Linger so a tab that reconnects just after the
// end still catches up from memory, then only runs.db has it (its events when event storage is on, and always its row).
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace PiSharp.WebAgent.Runs;

/// <summary>One live event: its sequence number, when it arrived (ms after the call started) and the event (Pi wire JSON).</summary>
public sealed record RunLiveItem(long Seq, long AtMs, JsonNode Event);

/// <summary>A subscription: the events already published after the asked sequence number, the live ones (null when the run has ended) and
/// the final status (null while it runs).</summary>
public sealed record RunSubscription(IReadOnlyList<RunLiveItem> Backlog, ChannelReader<RunLiveItem>? Live, JsonObject? Final, Action Unsubscribe);

/// <summary>The live runs of this process. A singleton.</summary>
public sealed class RunLive
{
    /// <summary>How long an ended run's events stay in memory.</summary>
    public static TimeSpan Linger { get; } = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, RunLiveRun> runs = new(StringComparer.Ordinal);

    /// <summary>Starts the live log of <paramref name="id"/>.</summary>
    public RunLiveRun Open(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var run = new RunLiveRun(id, this);
        runs[id] = run;
        return run;
    }

    /// <summary>The live log of <paramref name="id"/>, or null when it is not running here (or ended more than <see cref="Linger"/> ago).</summary>
    public RunLiveRun? Find(string id) => runs.TryGetValue(id, out var run) ? run : null;

    /// <summary>The ids of the runs running now.</summary>
    public IReadOnlyList<string> Running() => [.. runs.Values.Where(run => !run.Ended).Select(run => run.Id)];

    internal void Forget(RunLiveRun run) => runs.TryRemove(KeyValuePair.Create(run.Id, run));
}

/// <summary>One run's live log.</summary>
public sealed class RunLiveRun
{
    private readonly Lock gate = new();
    private readonly RunLive owner;
    private readonly List<RunLiveItem> items = [];
    private readonly List<Channel<RunLiveItem>> subscribers = [];
    private JsonObject? final;
    private long sequence;

    internal RunLiveRun(string id, RunLive owner)
    {
        Id = id;
        this.owner = owner;
    }

    /// <summary>The run id.</summary>
    public string Id { get; }

    /// <summary>Whether the run has ended.</summary>
    public bool Ended { get { lock (gate) return final is not null; } }

    /// <summary>The events published so far.</summary>
    public IReadOnlyList<RunLiveItem> Items { get { lock (gate) return [.. items]; } }

    /// <summary>Publishes one event; returns its sequence number.</summary>
    public long Publish(long atMs, JsonNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            if (final is not null) return sequence;
            var item = new RunLiveItem(++sequence, atMs, value);
            items.Add(item);
            foreach (var subscriber in subscribers) subscriber.Writer.TryWrite(item);
            return item.Seq;
        }
    }

    /// <summary>Ends the run with its final status and completes every subscription; the log is forgotten after <see cref="RunLive.Linger"/>.</summary>
    public void End(JsonObject status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (gate)
        {
            if (final is not null) return;
            final = status;
            foreach (var subscriber in subscribers) subscriber.Writer.TryComplete();
            subscribers.Clear();
        }
        _ = Task.Delay(RunLive.Linger).ContinueWith(_ => owner.Forget(this), TaskScheduler.Default);
    }

    /// <summary>Subscribes after sequence number <paramref name="after"/>.</summary>
    public RunSubscription Subscribe(long after)
    {
        lock (gate)
        {
            var backlog = items.Where(item => item.Seq > after).ToList();
            if (final is not null) return new RunSubscription(backlog, null, final, () => { });
            var channel = Channel.CreateUnbounded<RunLiveItem>(new UnboundedChannelOptions { SingleReader = true });
            subscribers.Add(channel);
            return new RunSubscription(backlog, channel.Reader, null, () =>
            {
                lock (gate) subscribers.Remove(channel);
                channel.Writer.TryComplete();
            });
        }
    }

    /// <summary>The final status, or null while it runs.</summary>
    public JsonObject? Final { get { lock (gate) return final; } }
}
