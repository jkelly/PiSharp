// PiSharp-only (decision 0008; public for the web agent, decision 0011). One agent's view frames without HTTP, moved out of the device
// server's agent stream (DeviceStreams.AgentAsync, after durable-pi's streams.ts, MIT): `snapshot` with the inbox, then one `batch` of
// pi-durable events per commit (verbatim), `inbox` after a queue change, and the requested `doc` frames, each sent again only when its value
// changed. The device server sends them after its `hello`; the web agent reads them in-process and multiplexes them per tab. Events and
// document updates meet in one bounded channel, so a slow reader slows the agent's watch, whose own overflow rule then sends a snapshot. A
// reader is a viewer only: stopping it never stops work.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;

namespace PiSharp.Durable.Server;

/// <summary>What <see cref="AgentFrames.ReadAsync"/> sends besides the agent's events.</summary>
public sealed record AgentFrameOptions
{
    /// <summary>The agent documents to send as <c>doc</c> frames, from <see cref="AgentFrames.DocKinds"/>: each once at the start, then
    /// after changes of the session whenever its value changed.</summary>
    public IReadOnlyList<string> Docs { get; init; } = [];

    /// <summary>How long changes gather before the documents are read again (the device server's
    /// <see cref="DurableDeviceServerOptions.DocumentCoalesce"/>).</summary>
    public TimeSpan DocumentCoalesce { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Hears failures of document updates, which the frames survive.</summary>
    public Action<Exception>? OnError { get; init; }
}

/// <summary>One agent's view frames for a viewer in the same process: what the device API's agent stream sends after <c>hello</c>.</summary>
public static class AgentFrames
{
    /// <summary>How many frames wait for a slow reader before the agent's watch waits too.</summary>
    private const int Buffered = 32;

    /// <summary>The agent documents a viewer may ask for.</summary>
    public static IReadOnlyList<string> DocKinds => DeviceService.AgentDocKinds;

    /// <summary>
    /// The view frames of agent <paramref name="agentId"/> of <paramref name="session"/>, in order, until the session closes or
    /// <paramref name="cancellationToken"/> is cancelled:
    /// <list type="bullet">
    /// <item><c>{type: "snapshot", snapshot, inbox}</c> first: the pi-durable <c>snapshot</c> event at attachment and the queued
    /// messages;</item>
    /// <item><c>{type: "doc", kind, value}</c> for each requested document, at the start and whenever its value changed;</item>
    /// <item><c>{type: "batch", events}</c>: the pi-durable events of one commit, verbatim;</item>
    /// <item><c>{type: "inbox", items}</c> after a batch that changed the queue or carried a snapshot.</item>
    /// </list>
    /// Frames are the device API's wire JSON, so PiSharp.Durable.Client's <c>AgentModel.Apply</c> reduces them. The first frame is read when
    /// the enumeration starts, so an unknown agent fails the first <c>MoveNextAsync</c>; enumerate on, or dispose, to stop watching.
    /// </summary>
    /// <exception cref="HostInvalidException">A document not in <see cref="DocKinds"/> (thrown by this call).</exception>
    /// <exception cref="HostNotFoundException">No such agent (thrown by the first <c>MoveNextAsync</c>).</exception>
    /// <exception cref="SessionUnavailableException">The session is unavailable (thrown by the first <c>MoveNextAsync</c>).</exception>
    public static IAsyncEnumerable<JsonObject> ReadAsync(HostedSession session, long agentId, AgentFrameOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        options ??= new AgentFrameOptions();
        var docs = options.Docs.Distinct(StringComparer.Ordinal).ToList();
        foreach (var kind in docs)
            if (!DocKinds.Contains(kind)) throw new HostInvalidException($"Unknown document {kind}. Allowed: {string.Join(", ", DocKinds)}.");
        return ReadCoreAsync(session, agentId, docs, options, cancellationToken);
    }

    /// <summary>The active context's entries as wire JSON, oldest first (durable-pi <c>scope=active</c>): what
    /// <c>Transcript.Build</c> renders before a viewer catches up from <see cref="ReadAsync"/>'s snapshot.</summary>
    /// <exception cref="HostNotFoundException">No such agent.</exception>
    public static async Task<IReadOnlyList<JsonObject>> ActiveEntriesAsync(HostedSession session, long agentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return await DeviceService.ActiveEntriesAsync(session, agentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The agent's queued messages with their text, as the <c>inbox</c> frame carries them.</summary>
    public static async Task<IReadOnlyList<InboxItem>> InboxAsync(HostedSession session, long agentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return await DeviceService.InboxAsync(session, agentId, cancellationToken).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<JsonObject> ReadCoreAsync(HostedSession session, long agentId, List<string> docs, AgentFrameOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var feed = await session.WatchAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
        yield return new JsonObject
        {
            ["type"] = "snapshot",
            ["snapshot"] = DeviceService.ToNode(feed.Snapshot.ToJson()),
            ["inbox"] = DurableWire.ToNode(await DeviceService.InboxAsync(session, agentId, cancellationToken).ConfigureAwait(false)),
        };
        var sent = new Dictionary<string, string>(StringComparer.Ordinal);
        async Task<JsonObject?> ChangedDocAsync(string kind, CancellationToken token)
        {
            var frame = new JsonObject
            {
                ["type"] = "doc",
                ["kind"] = kind,
                ["value"] = await DeviceService.AgentDocAsync(session, agentId, kind, token).ConfigureAwait(false),
            };
            var json = frame.ToJsonString(DurableWire.Options);
            if (sent.TryGetValue(kind, out var previous) && previous == json) return null;
            sent[kind] = json;
            return frame;
        }
        foreach (var kind in docs)
            if (await ChangedDocAsync(kind, cancellationToken).ConfigureAwait(false) is { } frame) yield return frame;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var frames = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(Buffered) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        var pushDocs = new Coalescer();
        using var subscription = docs.Count == 0 ? null : session.SubscribeChanges(_ => pushDocs.Trigger());
        var documents = docs.Count == 0 ? Task.CompletedTask : pushDocs.RunAsync(async () =>
        {
            try
            {
                foreach (var kind in docs)
                    if (await ChangedDocAsync(kind, stop.Token).ConfigureAwait(false) is { } frame)
                        await frames.Writer.WriteAsync(frame, stop.Token).ConfigureAwait(false);
            }
            catch (ChannelClosedException) { /* The events ended; the reader is stopping. */ }
        }, options.DocumentCoalesce, failure => options.OnError?.Invoke(failure), stop.Token);
        var events = PumpEventsAsync(feed, session, agentId, frames.Writer, stop.Token);
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return frame;
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await events.ConfigureAwait(false);
            await documents.ConfigureAwait(false);
        }
    }

    /// <summary>Writes a <c>batch</c> frame per commit, and an <c>inbox</c> frame after a queue change, until the feed ends; the channel then
    /// completes, with the feed's failure if it had one.</summary>
    private static async Task PumpEventsAsync(AgentEventFeed feed, HostedSession session, long agentId, ChannelWriter<JsonObject> frames,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await foreach (var batch in feed.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await frames.WriteAsync(new JsonObject
                {
                    ["type"] = "batch",
                    ["events"] = new JsonArray([.. batch.Select(item => DeviceService.ToNode(item.ToJson()))]),
                }, cancellationToken).ConfigureAwait(false);
                if (batch.Any(item => item.Type is "inbox_update" or "snapshot"))
                    await frames.WriteAsync(new JsonObject
                    {
                        ["type"] = "inbox",
                        ["items"] = DurableWire.ToNode(await DeviceService.InboxAsync(session, agentId, cancellationToken).ConfigureAwait(false)),
                    }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* Stopped, or the session closed. */ }
        catch (Exception error)
        {
            failure = error;
        }
        frames.TryComplete(failure);
    }
}
