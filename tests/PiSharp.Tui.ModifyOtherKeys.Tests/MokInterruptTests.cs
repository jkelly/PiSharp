using System.Text.Json;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;

internal static class MokInterruptTests
{
    internal static async Task<object> Run(string sourcePath)
    {
        using var source = JsonDocument.Parse(File.ReadAllText(sourcePath));
        var rows = new List<object>(); var failed = 0;
        foreach (var acknowledged in new[] { false, true })
        foreach (var expected in source.RootElement.GetProperty("cases").EnumerateArray())
        {
            var raw = expected.GetProperty("raw").GetString()!;
            var sink = new InterruptSink(raw); var interrupts = 0; var ended = 0;
            var receipts = new TerminalSubmissionReceipts();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var host = new TerminalChatInput(sink); Exception? error = null;
            try
            {
                if (acknowledged)
                    await host.RunAcknowledgedAsync((_, _) => throw new InvalidOperationException("Unexpected submit"),
                        _ => Task.CompletedTask, (_, _) => ValueTask.CompletedTask, receipts,
                        (_, _) => throw new InvalidOperationException("Unexpected acceptance"),
                        _ => { ended++; receipts.Complete(); }, () => interrupts++, stop.Token);
                else
                    await host.RunAsync((_, _) => throw new InvalidOperationException("Unexpected submit"),
                        (_, _) => ValueTask.CompletedTask, () => interrupts++, stop.Token);
            }
            catch (Exception failure) { error = failure; }
            var expectedInterrupts = expected.GetProperty("shouldInterrupt").GetBoolean() ? 1 : 0;
            var pass = error is null && interrupts == expectedInterrupts && sink.Active == 0 &&
                sink.Started == sink.Settled && sink.Disposals == 0 && (!acknowledged || ended == 1);
            if (!pass) failed++;
            rows.Add(new { id = expected.GetProperty("id").GetString(), route = acknowledged ? "acknowledged" : "legacy",
                raw, expectedInterrupts, actualInterrupts = interrupts, ended,
                readsStarted = sink.Started, readsSettled = sink.Settled, sink.Disposals,
                error = error?.ToString(), pass });
        }

        var cleanup = new List<object>();
        foreach (var acknowledged in new[] { false, true })
        {
            var sink = new InterruptSink("\u001b[27;", hold: true);
            using var stop = new CancellationTokenSource();
            var receipts = new TerminalSubmissionReceipts(); var ended = 0;
            var host = new TerminalChatInput(sink);
            var run = acknowledged
                ? host.RunAcknowledgedAsync((_, _) => throw new InvalidOperationException("Unexpected submit"),
                    _ => Task.CompletedTask, (_, _) => ValueTask.CompletedTask, receipts,
                    (_, _) => throw new InvalidOperationException("Unexpected acceptance"),
                    _ => { ended++; receipts.Complete(); }, () => { }, stop.Token)
                : host.RunAsync((_, _) => throw new InvalidOperationException("Unexpected submit"),
                    (_, _) => ValueTask.CompletedTask, () => { }, stop.Token);
            var joinedBeforeRelease = false; Exception? failure = null;
            try
            {
                await sink.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));
                stop.Cancel(); await Task.Delay(60);
                joinedBeforeRelease = run.IsCompleted;
                sink.Release.TrySetResult();
                try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) { failure = error; }
            }
            finally
            {
                sink.Release.TrySetResult(); stop.Cancel(); receipts.Complete();
                try { await run; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            var pass = !joinedBeforeRelease && sink.Active == 0 && sink.Started == sink.Settled && sink.Disposals == 0 &&
                (failure is null or OperationCanceledException) && (!acknowledged || ended == 1);
            if (!pass) failed++;
            cleanup.Add(new { route = acknowledged ? "acknowledged" : "legacy", joinedBeforeRelease,
                readsStarted = sink.Started, readsSettled = sink.Settled, sink.Disposals, ended,
                failure = failure?.GetType().Name, pass });
        }
        return new { sourceCases = source.RootElement.GetProperty("cases").GetArrayLength(), actualRouteCases = rows.Count,
            rows, cleanup, failed, allOwnedExecutionsJoined = true };
    }

    private sealed class InterruptSink(string raw, bool hold = false) : IConsoleTerminal
    {
        internal int Started, Settled, Active, Disposals;
        internal readonly TaskCompletionSource Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TerminalLeaseSnapshot Snapshot
        {
            get { var s = new TerminalConsoleState(0, 0, 65001, 65001, 25, true, 0, 0);
                return new(s, s, null, false, false, Active, 0, Started, Settled, 0, 0); }
        }
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        {
            Active++; var number = ++Started;
            try
            {
                if (number == 1) { raw.AsMemory().CopyTo(destination); return raw.Length; }
                if (hold) { Held.TrySetResult(); await Release.Task; }
                return 0;
            }
            finally { Active--; Settled++; }
        }
        public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default) =>
            throw new InvalidOperationException("No physical writes in the interrupt routing witness");
        public ValueTask DisposeAsync()
        { Disposals++; throw new InvalidOperationException("Borrowed terminal lease disposed"); }
    }
}
