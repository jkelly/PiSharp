using System.Threading.Channels;
using PiSharp.Contracts;
using PiSharp.Tui;

internal sealed class StartupTrace
{
    private readonly object gate = new();
    private readonly List<object> rows = [];
    private readonly List<JsonData> records = [];
    private TaskCompletionSource changed = SelectorFixture.Signal();
    private long sequence;
    internal void Add(string kind, object value)
    {
        TaskCompletionSource signal;
        lock (gate) { rows.Add(new { sequence = ++sequence, kind, value }); signal = changed; changed = SelectorFixture.Signal(); }
        signal.TrySetResult();
    }
    internal ValueTask Observe(JsonData value, CancellationToken token)
    { lock (gate) records.Add(value); Add("real-rpc-output-after-frontend", value); return ValueTask.CompletedTask; }
    internal object[] Rows() { lock (gate) return rows.ToArray(); }
    internal JsonData[] Records() { lock (gate) return records.ToArray(); }
    internal async Task<JsonData> WaitRecord(Func<JsonData, bool> predicate)
    {
        while (true)
        {
            Task next; lock (gate) { var matches = records.Where(predicate).ToArray(); if (matches.Length != 0) return matches[0]; next = changed.Task; }
            await next.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}

// Each test owns this controlled borrowed terminal. Holds deliberately observe
// cancellation yet retain the original operation until explicitly released.
internal sealed class StartupControlledTerminal(StartupTrace trace, SelectorHold? readHold = null,
    SelectorHold? firstFrameHold = null, bool failFirstFrame = false, Exception? leaveFailure = null,
    Exception? heldWriteFailure = null) : IConsoleTerminal, ITerminalViewportSource
{
    private readonly object gate = new();
    private readonly Channel<string> input = Channel.CreateBounded<string>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true });
    private readonly List<string> writes = [];
    private readonly List<SelectorHold> extraHolds = [];
    private (string Text, SelectorHold Hold)? nextWrite;
    private (SelectorHold Hold, Exception? Failure)? nextRead;
    private TaskCompletionSource changed = SelectorFixture.Signal();
    private int activeReads, activeWrites, maximumReads, maximumWrites, disposals;
    private long readsStarted, readsSettled, writesStarted, writesSettled;
    private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
    public TerminalLeaseSnapshot Snapshot { get { lock (gate) return new(State, State, null, false, false, activeReads, activeWrites, readsStarted, readsSettled, writesStarted, writesSettled); } }
    internal object Evidence { get { lock (gate) return new { snapshot = Snapshot, maximumReads, maximumWrites, disposals, writes = writes.ToArray() }; } }
    internal int Disposals { get { lock (gate) return disposals; } }
    internal async Task Feed(string text) { await input.Writer.WriteAsync(text); trace.Add("owned-input-fed", text); }
    internal void End() => input.Writer.TryComplete();
    internal void Release()
    { readHold?.Release.TrySetResult(); firstFrameHold?.Release.TrySetResult(); lock (gate) foreach (var hold in extraHolds) hold.Release.TrySetResult(); }
    internal SelectorHold HoldWrite(string text)
    {
        lock (gate)
        {
            if (nextWrite is not null) throw new InvalidOperationException("Existing held write.");
            var hold = new SelectorHold(); extraHolds.Add(hold); nextWrite = (text, hold); return hold;
        }
    }
    internal SelectorHold HoldNextRead(Exception? failure = null)
    {
        lock (gate)
        {
            if (nextRead is not null) throw new InvalidOperationException("Existing held read.");
            var hold = new SelectorHold(); extraHolds.Add(hold); nextRead = (hold, failure); return hold;
        }
    }
    public TerminalViewport ReadViewport() => new(96, 20, 0, 0, 96, 20);
    public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
    {
        long number; (SelectorHold Hold, Exception? Failure)? extra;
        lock (gate) { number = ++readsStarted; activeReads++; maximumReads = Math.Max(maximumReads, activeReads); extra = nextRead; nextRead = null; }
        trace.Add("physical-read-entered", new { number }); Signal();
        try
        {
            if (extra is { } held)
            {
                held.Hold.Entered.TrySetResult(); using var registration = token.Register(() => held.Hold.Canceled.TrySetResult());
                await held.Hold.Release.Task; if (held.Failure is not null) throw held.Failure;
            }
            if (number == 1 && readHold is not null)
            { readHold.Entered.TrySetResult(); using var registration = token.Register(() => { trace.Add("original-read-cancellation-observed", number); readHold.Canceled.TrySetResult(); }); await readHold.Release.Task; }
            if (!await input.Reader.WaitToReadAsync(token)) return 0;
            if (!input.Reader.TryRead(out var text) || text.Length > destination.Length) throw new IOException("Controlled input exceeded actual read capacity.");
            text.AsMemory().CopyTo(destination); return text.Length;
        }
        finally { lock (gate) { activeReads--; readsSettled++; } trace.Add("physical-read-settled", number); Signal(); }
    }
    public async ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default)
    {
        long number; var text = frame.ToString(); SelectorHold? extra = null;
        lock (gate)
        {
            number = ++writesStarted; activeWrites++; maximumWrites = Math.Max(maximumWrites, activeWrites); writes.Add(text);
            if (nextWrite is { } pending && text.Contains(pending.Text, StringComparison.Ordinal)) { extra = pending.Hold; nextWrite = null; }
        }
        trace.Add("physical-write-entered", new { number, text }); Signal();
        try
        {
            if (extra is not null)
            {
                extra.Entered.TrySetResult(); using var registration = token.Register(() => extra.Canceled.TrySetResult()); await extra.Release.Task;
                if (heldWriteFailure is not null) throw heldWriteFailure;
            }
            if (number == 2 && firstFrameHold is not null)
            { firstFrameHold.Entered.TrySetResult(); using var registration = token.Register(() => firstFrameHold.Canceled.TrySetResult()); await firstFrameHold.Release.Task; }
            if (number == 2 && failFirstFrame) throw new IOException("AUTHORED_INITIAL_FRAME_FAULT");
            if (leaveFailure is not null && text.Contains("\u001b[?1049l", StringComparison.Ordinal)) throw leaveFailure;
            token.ThrowIfCancellationRequested();
        }
        finally { lock (gate) { activeWrites--; writesSettled++; } trace.Add("physical-write-settled", number); Signal(); }
    }
    internal async Task WaitReads(long count)
    { while (true) { Task next; lock (gate) { if (readsStarted >= count) return; next = changed.Task; } await next.WaitAsync(TimeSpan.FromSeconds(10)); } }
    internal async Task WaitWrite(string text)
    { while (true) { Task next; lock (gate) { if (writes.Any(x => x.Contains(text, StringComparison.Ordinal))) return; next = changed.Task; } await next.WaitAsync(TimeSpan.FromSeconds(10)); } }
    private void Signal() { TaskCompletionSource signal; lock (gate) { signal = changed; changed = SelectorFixture.Signal(); } signal.TrySetResult(); }
    internal void AssertJoined()
    {
        var s = Snapshot;
        StartupBarrierCases.Check(s.ActiveReads == 0 && s.ActiveWrites == 0 && s.ReadWorkersStarted == s.ReadWorkersSettled &&
            s.WriteWorkersStarted == s.WriteWorkersSettled && maximumReads <= 1 && maximumWrites <= 1 && Disposals == 0,
            "Command detached original physical I/O, introduced another I/O owner or disposed its borrowed terminal.");
    }
    public ValueTask DisposeAsync() { lock (gate) disposals++; return ValueTask.CompletedTask; }
}
