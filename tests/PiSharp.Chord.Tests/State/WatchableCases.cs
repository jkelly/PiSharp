using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.State;
using static ChordSupport;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/state/watchable.test.ts. Upstream's counter is a local type
// served by createHost (phase 6) whose `num` member is a defineState document; here it is the hand-built host's document (StateHost.cs), the
// same JSON source with the same frames, and `c.set(n)` is a commit of the document. The subscriber case reaches the source through
// resolveWatchable(c.num) upstream (a reference, phase 6) and the document directly here. What only the host adds (the reference's member
// forwarding, a set through a method call) waits for phase 6. toEqual on frames compares their JSON.
internal static partial class Program
{
    private static readonly ChordContext StateBg = ChordContext.Background;

    /// <summary>Frames as one JSON array, for one comparison.</summary>
    internal static JsonNode? StateFrames(IEnumerable<(JsonNode? Value, System.Collections.Immutable.ImmutableArray<PiSharp.Chord.Delta.Op> Ops)> frames) =>
        JsonNode.Parse("[" + string.Join(",", frames.Select(FrameJson)) + "]");

    /// <summary>Calls as one JSON array, for one comparison.</summary>
    internal static JsonNode? StateCalls(IEnumerable<WatchCall<JsonNode?>> calls) => JsonNode.Parse("[" + string.Join(",", calls.Select(CallJson)) + "]");

    private static IEnumerable<(string, Func<Task>)> StateWatchableCases() =>
    [
        Case("state.watchable.watches-start-from-the-value-at-acquisition-a-listener-that-keeps-up-gets-each-commit", async () =>
        {
            var host = new StateHost();
            var num = host.Doc("""{"n":0}""");
            var watch = await num.WatchAsync(StateBg);
            Json("""{"n":0}""", watch.Value, "the value at acquisition");
            var frames = FramesOf(watch);
            num.Set("""{"n":1}""", StateBg);
            host.Delivered();
            num.Set("""{"n":2}""", StateBg);
            host.Delivered();
            Json("""[[{"n":1},[["s",["n"],1]]],[{"n":2},[["s",["n"],2]]]]""", StateFrames(frames), "the frames");
        }),
        Case("state.watchable.watches-give-a-busy-listener-one-call-with-the-newest-value-and-the-folded-ops", async () =>
        {
            var host = new StateHost();
            var num = host.Doc("""{"n":0}""");
            var watch = await num.WatchAsync(StateBg);
            var frames = new List<(JsonNode?, System.Collections.Immutable.ImmutableArray<PiSharp.Chord.Delta.Op>)>();
            // The first call stays busy until the test lets it return; its continuation runs where the gate is opened.
            var release = new TaskCompletionSource();
            watch.Start(async (call, _) =>
            {
                if (call is not WatchValueCall<JsonNode?> value) return;
                frames.Add((value.Value, value.Ops));
                if (frames.Count == 1) await release.Task;
            });
            num.Set("""{"n":1}""", StateBg);
            host.Delivered();
            num.Set("""{"n":2}""", StateBg);
            num.Set("""{"n":3}""", StateBg);
            release.SetResult();
            host.Delivered();
            // Two commits while busy: one call, the second `s` on the same path replaced the first.
            Json("""[[{"n":1},[["s",["n"],1]]],[{"n":3},[["s",["n"],3]]]]""", StateFrames(frames), "the frames");
        }),
        Case("state.watchable.watches-end-cancelled-when-their-context-aborts", async () =>
        {
            var host = new StateHost();
            var num = host.Doc("""{"n":0}""");
            var (cancellable, cancel) = ChordContext.WithCancel(StateBg);
            var watch = await num.WatchAsync(cancellable);
            cancel();
            Equal(WatchEnd.Aborted, await watch.Closed, "the end");
            Check(!num.Watched, "the source lets the watch go");
        }),
        Case("state.watchable.watches-end-with-listener-error-when-the-listener-throws-reported-to-on-error", async () =>
        {
            var host = new StateHost();
            var num = host.Doc("""{"n":0}""");
            var watch = await num.WatchAsync(StateBg);
            var error = new InvalidOperationException("listener threw");
            watch.Start((_, _) => throw error);
            num.Set("""{"n":1}""", StateBg);
            host.Delivered();
            var end = await watch.Closed;
            Equal(WatchEndKind.ListenerError, end.Kind, "the end");
            Check(ReferenceEquals(error, end.Error), "the end's error");
            Equal(1, host.Errors.Count, "one report");
            Equal(BatchErrorKind.ListenerError, host.Errors[0].Kind, "the report's kind");
            Check(ReferenceEquals(error, host.Errors[0].Error), "the report's error");
        }),
        Case("state.watchable.constant-never-changes-its-watches-end-only-by-stop-or-cancellation-and-hold-nothing-after", async () =>
        {
            var start = Watchables.Constant(Parse("""{"id":"S1"}"""));
            Json("""{"id":"S1"}""", start.Value, "the value");
            var references = await StoppedConstantWatch(start);
            await ExpectCollected(references);
            var (cancellable, cancel) = ChordContext.WithCancel(StateBg);
            var cancelled = await start.WatchAsync(cancellable);
            cancel();
            Equal(WatchEnd.Aborted, await cancelled.Closed, "a cancelled watch's end");
        }),
        Case("state.watchable.subscribers-get-each-batchs-frame-synchronously-inside-the-commit-and-count-as-watching", () =>
        {
            var host = new StateHost();
            var source = host.Doc("""{"n":0}""");
            var frames = new List<string>();
            var subscriber = new StateSubscriber(call =>
            {
                if (call is WatchValueCall<JsonNode?> value) frames.Add(FrameJson((value.Value, value.Ops)));
            });
            source.Subscribe(subscriber);
            Check(source.Watched, "a subscriber counts as watching");
            source.Set("""{"n":1}""", StateBg);
            // Fed inside the commit: nothing waits for the scheduler.
            Sequence(["""[{"n":1},[["s",["n"],1]]]"""], frames, "the frames");
            source.Unsubscribe(subscriber);
            Check(!source.Watched, "unsubscribed");
            source.Set("""{"n":2}""", StateBg);
            host.Delivered();
            Equal(1, frames.Count, "no frame after unsubscribing");
        }),
    ];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> StoppedConstantWatch(IWatchable<JsonNode?> start)
    {
        var watch = await start.WatchAsync(StateBg);
        Check(ReferenceEquals(start.Value, watch.Value), "the watch holds the constant's value");
        Equal(WatchEnd.Stopped, await watch.StopAsync(), "the end");
        return new WeakReference(watch);
    }
}

/// <summary>A subscriber that records the calls it is fed (upstream's object literal with advance, end and endAfterDelivery).</summary>
internal sealed class StateSubscriber(Action<WatchCall<JsonNode?>> advance) : ISubscriber
{
    public List<WatchEnd> Ends { get; } = [];

    public void Advance(WatchCall<JsonNode?> call, ChordContext context) => advance(call);

    public void End(WatchEnd end) => Ends.Add(end);

    public void EndAfterDelivery() => Ends.Add(WatchEnd.Ended);
}
