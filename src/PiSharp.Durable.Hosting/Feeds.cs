// PiSharp-only (decision 0008): IAsyncEnumerable adapters over pi-durable's callback watches, for the device server's SSE streams. Each
// listener writes into a bounded channel and waits while it is full, so a slow reader slows the watch, and the watch's own overflow rule
// (spec §9.4: undelivered batches beyond its limit become one snapshot) takes over. Nothing is replayed: a new feed starts from a snapshot.
using System.Threading.Channels;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>One agent's event batches: a snapshot at attachment, then one batch per commit (pi-durable <c>watchEvents</c>).</summary>
public sealed class AgentEventFeed : IAsyncDisposable
{
    private readonly IAgentEventStream stream;
    private readonly Channel<IReadOnlyList<AgentEvent>> channel;

    private AgentEventFeed(IAgentEventStream stream, int capacity)
    {
        this.stream = stream;
        channel = Channel.CreateBounded<IReadOnlyList<AgentEvent>>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    internal static async Task<AgentEventFeed> StartAsync(IHarness harness, ConversationId conversation, int capacity)
    {
        var stream = await harness.WatchEventsAsync(conversation, DurableContext.Background).ConfigureAwait(false);
        var feed = new AgentEventFeed(stream, capacity);
        stream.Start((events, context) => feed.channel.Writer.WriteAsync(events, context.CancellationToken).AsTask());
        _ = stream.Closed.ContinueWith(closed => feed.channel.Writer.TryComplete(closed.IsCompletedSuccessfully ? closed.Result.Error : closed.Exception),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return feed;
    }

    /// <summary>The <c>snapshot</c> event at attachment.</summary>
    public SnapshotEvent Snapshot => stream.Snapshot;

    /// <summary>Completes when the feed ends: stopped, or its session closed.</summary>
    public Task<WatchEnd> Closed => stream.Closed;

    /// <summary>The batches after <see cref="Snapshot"/>, in commit order, until the feed ends. One reader.</summary>
    public IAsyncEnumerable<IReadOnlyList<AgentEvent>> ReadAllAsync(CancellationToken cancellationToken = default) =>
        channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Stops the feed; the agent keeps working.</summary>
    public async ValueTask DisposeAsync()
    {
        await stream.StopAsync().ConfigureAwait(false);
        channel.Writer.TryComplete();
    }
}

/// <summary>A session's live task graph: its value at attachment, then the newest value after each change. A slow reader skips to the
/// newest value; each value is complete.</summary>
public sealed class TaskGraphFeed : IAsyncDisposable
{
    private readonly IWatchHandle<TaskGraph> watch;
    private readonly Channel<TaskGraph> channel = Channel.CreateBounded<TaskGraph>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    });

    private TaskGraphFeed(IWatchHandle<TaskGraph> watch) => this.watch = watch;

    internal static async Task<TaskGraphFeed> StartAsync(IHarness harness)
    {
        var feed = new TaskGraphFeed(await harness.WatchTaskGraphAsync(DurableContext.Background).ConfigureAwait(false));
        var initial = feed.watch.Value;
        feed.watch.Start((graph, _, _) =>
        {
            feed.channel.Writer.TryWrite(graph);
            return Task.CompletedTask;
        });
        _ = feed.watch.Closed.ContinueWith(_ => feed.channel.Writer.TryComplete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        feed.Initial = initial;
        return feed;
    }

    /// <summary>The graph at attachment.</summary>
    public TaskGraph Initial { get; private set; } = null!;

    /// <summary>The graph after each change, newest first when the reader falls behind. One reader.</summary>
    public IAsyncEnumerable<TaskGraph> ReadAllAsync(CancellationToken cancellationToken = default) => channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Stops the feed.</summary>
    public async ValueTask DisposeAsync()
    {
        await watch.StopAsync().ConfigureAwait(false);
        channel.Writer.TryComplete();
    }
}
