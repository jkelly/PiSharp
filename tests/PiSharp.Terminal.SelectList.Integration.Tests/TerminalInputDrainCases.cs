using System.Text.Json.Nodes;
using PiSharp.Cli.Commands;

// Authored only. These run the configured terminal command; original I/O joins
// have no timeout wrapper. The clock drives only the actual shutdown drain.
internal static class TerminalInputDrainCases
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases(string[] args) =>
    [
        ("drain-ctrl-c-idle-cancellation-joins-read-and-held-leave", e => Held(args[1], e)),
        ("drain-ctrl-d-empty-exit-uses-same-owned-drain", e => Held(args[1], e, quit: "\u0004")),
        ("drain-slash-quit-uses-same-owned-drain", e => Held(args[1], e, quit: "/quit\r")),
        ("drain-continuous-input-maximum-retains-original-read-join", e => Maximum(args[1], e)),
        ("drain-external-cancellation-is-failure-after-read-join", e => Held(args[1], e, external: true)),
        ("drain-late-read-fault-is-not-deadline-success", e => Held(args[1], e, fault: new IOException("AUTHORED_DRAIN_FAILURE"))),
        ("drain-configured-streaming-escape-restores-aborts-and-discards-late-interrupts", e => StreamingEscape(args[1], e))
    ];

    private static async Task Held(string root, ConsumerEvidence e, string quit = "\u0003\u0003",
        int milliseconds = 50, bool external = false, Exception? fault = null)
    {
        var files = await StartupOwnedFiles.Create(root, plugin: false, e);
        var trace = new StartupTrace(); var terminal = new StartupControlledTerminal(trace);
        SelectorHold? read = null; var clock = new Clock(() => read = terminal.HoldNextRead(fault));
        using var errors = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var original = TerminalSessionCommand.RunObservedConfiguredAsync(files.Args(), terminal, terminal, errors,
            trace.Observe, files.Configuration, cancellation.Token, shutdownTimeProvider: clock);
        try
        {
            await terminal.WaitWrite("[history]"); await terminal.WaitReads(1);
            var leave = terminal.HoldWrite("\u001b[?1049l"); await terminal.Feed(quit);
            await clock.Started.Task.WaitAsync(Deadline); await read!.Entered.Task.WaitAsync(Deadline);
            if (external) cancellation.Cancel(); else clock.Advance(milliseconds);
            await read.Canceled.Task.WaitAsync(Deadline);
            Check(!original.IsCompleted && terminal.Snapshot.ActiveReads == 1 && !leave.Entered.Task.IsCompleted,
                "Drain deadline/cancellation detached the physical read or allowed terminal leave.");
            read.Release.TrySetResult(); await leave.Entered.Task.WaitAsync(Deadline);
            Check(!original.IsCompleted && errors.ToString().Length == 0, "Return/diagnostics preceded original terminal leave.");
            leave.Release.TrySetResult(); var result = await original;
            var success = !external && fault is null;
            Check(result == (success ? 0 : 1) && (errors.ToString().Length == 0) == success, "Drain failure/cancellation classification changed.");
            terminal.AssertJoined(); await files.Complete(e);
            e.Observe("actual-command-drain-and-leave-physical-joins", new { quit, milliseconds, external,
                fault = fault?.GetType().Name, result, terminal = terminal.Evidence, trace = trace.Rows() });
        }
        finally { cancellation.Cancel(); terminal.End(); terminal.Release(); await original; terminal.AssertJoined(); }
    }

    private static async Task Maximum(string root, ConsumerEvidence e)
    {
        var files = await StartupOwnedFiles.Create(root, plugin: false, e);
        var terminal = new StartupControlledTerminal(new StartupTrace());
        SelectorHold? first = null; var clock = new Clock(() => first = terminal.HoldNextRead());
        using var errors = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var original = TerminalSessionCommand.RunObservedConfiguredAsync(files.Args(), terminal, terminal, errors,
            (_, _) => ValueTask.CompletedTask, files.Configuration, cancellation.Token, shutdownTimeProvider: clock);
        try
        {
            await terminal.WaitWrite("[history]"); await terminal.WaitReads(1);
            var leave = terminal.HoldWrite("\u001b[?1049l"); await terminal.Feed("\u0003\u0003");
            await clock.Started.Task.WaitAsync(Deadline); await first!.Entered.Task.WaitAsync(Deadline);
            var number = terminal.Snapshot.ReadWorkersStarted;
            await terminal.Feed("late"); first.Release.TrySetResult(); await terminal.WaitReads(number + 1);
            SelectorHold? last = null;
            for (var i = 1; i <= 39; i++)
            {
                // New input every25ms keeps the50ms idle deadline from firing.
                clock.Advance(25); if (i == 39) last = terminal.HoldNextRead();
                number = terminal.Snapshot.ReadWorkersStarted;
                await terminal.Feed("late"); await terminal.WaitReads(number + 1);
            }
            await last!.Entered.Task.WaitAsync(Deadline); clock.Advance(25);
            await last.Canceled.Task.WaitAsync(Deadline);
            Check(!original.IsCompleted && !leave.Entered.Task.IsCompleted, "Maximum deadline detached its final read.");
            last.Release.TrySetResult(); await leave.Entered.Task.WaitAsync(Deadline); leave.Release.TrySetResult();
            Check(await original == 0 && errors.ToString().Length == 0, "Continuous raw input defeated orderly bounded drain.");
            terminal.AssertJoined(); await files.Complete(e);
            e.Observe("maximum1000ms-without-idle50ms-expiry", terminal.Evidence);
        }
        finally { cancellation.Cancel(); terminal.End(); terminal.Release(); await original; terminal.AssertJoined(); }
    }

    private static async Task StreamingEscape(string root, ConsumerEvidence e)
    {
        var files = await StartupOwnedFiles.Create(root, plugin: false, e);
        // Existing trusted offline gate holds the actual scripted provider body until abort.
        var script = JsonNode.Parse(await File.ReadAllTextAsync(files.Script))!;
        script["turns"]![0]!["rpcGate"] = new JsonObject { ["releaseOnGetStateId"] = "drain-fixture-never-release" };
        await File.WriteAllTextAsync(files.Script, script.ToJsonString());
        var trace = new StartupTrace(); var terminal = new StartupControlledTerminal(trace);
        SelectorHold? drainRead = null; var clock = new Clock(() => drainRead = terminal.HoldNextRead());
        using var errors = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var original = TerminalSessionCommand.RunObservedConfiguredAsync(files.Args(), terminal, terminal, errors,
            trace.Observe, files.Configuration, cancellation.Token, kittyProtocolActive: true, shutdownTimeProvider: clock);
        try
        {
            await terminal.WaitWrite("[history]"); await terminal.WaitReads(1);
            await terminal.Feed("start\r"); await trace.WaitRecord(r => r.Value.GetProperty("type").GetString() == "agent_start");
            await terminal.Feed("/steer queued\r");
            await trace.WaitRecord(r => r.Value.GetProperty("type").GetString() == "queue_update" &&
                r.Value.GetProperty("steering").GetArrayLength() == 1);
            await terminal.Feed("draft\u001b[27;1:2u\u001b[27;1:1u");
            var interrupted = await trace.WaitRecord(r => r.Value.TryGetProperty("command", out var command) && command.GetString() == "pisharp_interrupt");
            Check(interrupted.Value.GetProperty("success").GetBoolean() &&
                interrupted.Value.GetProperty("data").GetProperty("text").GetString() == "queued\n\ndraft", "Actual streaming Escape lost queue/draft text.");
            await trace.WaitRecord(r => r.Value.GetProperty("type").GetString() == "agent_settled");
            await terminal.Feed("\u001b[27;1:3u\u001b[27;1:1u\u001b[113;3u");
            var empty = await trace.WaitRecord(r => r.Value.TryGetProperty("command", out var command) && command.GetString() == "pisharp_restore_queue");
            Check(empty.Value.GetProperty("data").GetProperty("count").GetInt32() == 0 &&
                empty.Value.GetProperty("data").GetProperty("text").GetString() == "queued\n\ndraft", "Repeated idle Escape changed the restored editor.");
            Check(trace.Records().Count(r => r.Value.TryGetProperty("command", out var command) && command.GetString() == "pisharp_interrupt") == 1,
                "Repeat/release or idle Escape issued another interrupt.");
            await terminal.Feed("\u0003\u0003"); await clock.Started.Task.WaitAsync(Deadline);
            await drainRead!.Entered.Task.WaitAsync(Deadline);
            var number = terminal.Snapshot.ReadWorkersStarted;
            await terminal.Feed("\u001b[99;5:3u\u0003\u0003/steer LATE\r"); drainRead.Release.TrySetResult();
            await terminal.WaitReads(number + 1); clock.Advance(50);
            Check(await original == 0 && errors.ToString().Length == 0, "Drain replayed late interrupts or prevented clean shutdown.");
            terminal.AssertJoined(); await files.Complete(e);
            Check(!File.Exists(files.Target) && !trace.Records().Any(r => r.ToString().Contains("LATE", StringComparison.Ordinal) ||
                r.Value.GetProperty("type").GetString() == "message_update" && r.ToString().Contains("unused", StringComparison.Ordinal)),
                "Drained bytes became RPC/provider work or abort emitted the gated response.");
            e.Observe("configured-streaming-escape-through-owned-offline-host-and-shutdown-drain", new
                { interrupted, empty, terminal = terminal.Evidence, trace = trace.Rows() });
        }
        finally { cancellation.Cancel(); terminal.End(); terminal.Release(); await original; terminal.AssertJoined(); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal sealed class Clock(Action started) : TimeProvider
    {
        private readonly object gate = new(); private readonly List<Timer> timers = [];
        private long now; private bool began;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() { lock (gate) return now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (gate)
            {
                if (!began) { began = true; started(); Started.TrySetResult(); }
                var timer = new Timer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer;
            }
        }
        internal void Advance(int milliseconds)
        {
            Timer[] due; lock (gate) { now += milliseconds; due = timers.Where(t => !t.Disposed && t.Due <= now).ToArray(); foreach (var timer in due) timer.Due = long.MaxValue; }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class Timer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback; internal readonly object? State = state;
            internal long Due; internal bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.gate)
                {
                    if (Disposed) return false;
                    if (period != Timeout.InfiniteTimeSpan) throw new InvalidOperationException("Drain timer must be one-shot.");
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.now + (long)dueTime.TotalMilliseconds; return true;
                }
            }
            public void Dispose() { lock (owner.gate) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
