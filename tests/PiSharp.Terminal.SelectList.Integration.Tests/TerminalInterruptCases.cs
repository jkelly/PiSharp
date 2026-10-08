using System.Text.Json;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;

// Authored only: no execution or runtime acceptance is claimed.
internal static class TerminalInterruptCases
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases() =>
    [
        ("interrupt-idle-escape-preserves-draft-and-clear-window-boundary", Idle),
        ("interrupt-configured-kitty-repeat-release-cannot-shutdown", Repeats),
        ("interrupt-session-switch-starts-new-clear-gesture", Switch),
        ("interrupt-second-press-cancels-but-joins-held-physical-write", HeldWrite),
        ("interrupt-streaming-escape-restores-and-correlates-abort-command", Escape)
    ];

    private static async Task Idle(ConsumerEvidence e)
    {
        var clock = new Clock(); await using var f = await SelectorFixture.Create(kitty: true, inputClock: clock);
        await f.TypeDraft("keep"); await f.Key("\u001b[27u"); Equal("keep", f.LastDraft!.Text);
        await f.Key("\u0003"); Equal("", f.LastDraft!.Text); Check(!f.Input.IsCompleted, "First clear stopped input.");
        clock.Advance(500); await f.TypeDraft("again"); await f.Key("\u0003");
        Equal("", f.LastDraft!.Text); Check(!f.Input.IsCompleted, "500ms boundary incorrectly shut down.");
        clock.Advance(499); f.Terminal.Feed("\u0003"); await Canceled(f);
        e.Observe("source-clear-window-and-idle-escape", f.Snapshot());
    }

    private static async Task Repeats(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(kitty: true, configuration: "{\"app.clear\":\"ctrl+r\"}", inputClock: new Clock());
        await f.TypeDraft("first"); await f.Key("\u001b[114;5:1u"); Equal("", f.LastDraft!.Text);
        await f.TypeDraft("kept"); var read = checked((int)f.Terminal.ReadsStarted);
        f.Terminal.Feed("\u001b[114;5:2u\u001b[114;5:3u"); await f.Terminal.WaitDecodedReads(read);
        Check(!f.Cancellation.IsCancellationRequested && !f.Input.IsCompleted, "Repeat/release escalated to shutdown.");
        Equal("kept", f.LastDraft!.Text);
        f.Terminal.Feed("\u001b[114;5:1u"); await Canceled(f);
        e.Observe("two-distinct-configured-presses-required", f.Snapshot());
    }

    private static async Task Switch(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(inputClock: new Clock());
        await f.TypeDraft("old"); await f.Key("\u0003");
        await f.Frontend.ObserveAsync(JsonData.Parse("""{"type":"session_switched","generation":2,"sessionId":"new"}"""), default);
        await f.TypeDraft("new"); await f.Key("\u0003");
        Equal("", f.LastDraft!.Text); Check(!f.Cancellation.IsCancellationRequested, "Old generation armed shutdown in replacement.");
        e.Observe("replacement-does-not-inherit-clear-gesture", f.Snapshot());
    }

    private static async Task HeldWrite(ConsumerEvidence e)
    {
        await using var f = await SelectorFixture.Create(inputClock: new Clock()); await f.TypeDraft("draft");
        var held = f.Terminal.HoldWrite("[draft discarded]"); f.Terminal.Feed("\u0003");
        try
        {
            await held.Entered.Task.WaitAsync(Deadline); f.Terminal.Feed("\u0003");
            await held.Canceled.Task.WaitAsync(Deadline);
            Check(!f.Input.IsCompleted && f.Terminal.Snapshot.ActiveWrites == 1, "Shutdown detached the physical write.");
        }
        finally { held.Release.TrySetResult(); await Canceled(f); }
        Check(f.Terminal.Snapshot.ActiveReads == 0 && f.Terminal.Snapshot.ActiveWrites == 0 && f.Terminal.Disposals == 0,
            "Borrowed terminal or original I/O ownership changed.");
        e.Observe("shutdown-after-original-write-joins", f.Snapshot());
    }

    private static async Task Escape(ConsumerEvidence e)
    {
        SelectorFixture? owner = null; var calls = 0;
        await using var f = await SelectorFixture.Create(kitty: true, commandHandler: async (request, token) =>
        {
            Equal("pisharp_interrupt", request.Value.GetProperty("type").GetString()); calls++;
            await owner!.Frontend.ObserveAsync(JsonData.Parse(JsonSerializer.Serialize(new
            {
                type = "response", id = request.Value.GetProperty("id").GetString(), command = "pisharp_interrupt", success = true,
                data = new { generation = request.Value.GetProperty("generation").GetInt64(), count = 1, text = "queued\n\ndraft" }
            })), token);
            await owner.Frontend.ObserveAsync(JsonData.Parse("""{"type":"agent_settled"}"""), token);
        });
        owner = f; await f.TypeDraft("draft");
        await f.Frontend.ObserveAsync(JsonData.Parse("""{"type":"agent_start"}"""), default);
        await f.Key("\u001b[27;1:2u"); Equal(0, calls);
        await f.Key("\u001b[27;1:1u"); Equal(1, calls); Equal("queued\n\ndraft", f.LastDraft!.Text);
        await f.Key("\u001b[27;1:1u"); Equal(1, calls); Equal("queued\n\ndraft", f.LastDraft!.Text);
        e.Observe("streaming-escape-correlated-removal-and-idle-noop", f.Snapshot());
    }

    private static async Task Canceled(SelectorFixture f)
    {
        var signaled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = f.Cancellation.Token.Register(() => signaled.TrySetResult());
        await signaled.Task.WaitAsync(Deadline);
        try { await f.Input; } catch (OperationCanceledException) when (f.Cancellation.IsCancellationRequested) { }
    }
    private static void Equal<T>(T expected, T actual) => TerminalSelectDialogCases.Equal(expected, actual);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance(int milliseconds) => Interlocked.Add(ref timestamp, milliseconds);
    }
}
