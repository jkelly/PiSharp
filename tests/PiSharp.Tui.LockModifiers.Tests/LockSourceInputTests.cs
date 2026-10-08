using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class LockSourceInputTests
{
    internal static async Task<object> Run(string sourcePath)
    {
using var jumpDocument = JsonDocument.Parse(File.ReadAllText(sourcePath));
var jumps = new List<object>();
var jumpFailed = 0;
foreach (var sourceCase in jumpDocument.RootElement.GetProperty("cases").EnumerateArray())
{
    var id = sourceCase.GetProperty("id").GetString()!;
    var sink = new InputSink();
    var captures = Channel.CreateBounded<TerminalDraftSnapshot>(new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });
    var receipts = new TerminalSubmissionReceipts();
    var submits = new List<string>();
    var actualSteps = new List<object>();
    var ended = 0;
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    Task<TerminalInputExit>? run = null;
    var viewLifetime = Guid.NewGuid();
    var failedSteps = 0;
    try
    {
        run = new TerminalChatInput(sink).RunAcknowledgedAsync((line, _) =>
        {
            submits.Add(line.Text);
            receipts.RejectUnacknowledged(line);
            return Task.FromResult(true);
        }, _ => Task.CompletedTask, async (snapshot, token) => await captures.Writer.WriteAsync(snapshot, token),
            receipts, (_, _) => throw new InvalidOperationException("Unexpected accepted receipt in raw-input parity witness"),
            _ => Interlocked.Increment(ref ended), stop.Cancel, stop.Token,
            () => new(new(viewLifetime, 0, TerminalEditorVisualMapBuilder.SourcePolicyId), 20, 24));
        _ = await captures.Reader.ReadAsync(stop.Token);
        foreach (var sourceStep in sourceCase.GetProperty("steps").EnumerateArray())
        {
            var raw = sourceStep.GetProperty("raw").GetString()!;
            await sink.Feed(raw, stop.Token);
            var captured = await captures.Reader.ReadAsync(stop.Token);
            var expectedText = sourceStep.GetProperty("text").GetString();
            var expectedExpanded = sourceStep.GetProperty("expandedText").GetString();
            var sourceCursor = sourceStep.GetProperty("cursor");
            var line = sourceCursor.GetProperty("line").GetInt32();
            var column = sourceCursor.GetProperty("col").GetInt32();
            var expectedOffset = column + expectedText!.Split('\n').Take(line).Sum(s => s.Length + 1);
            var expectedSubmits = sourceStep.GetProperty("submits").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var match = captured.Text == expectedText && captured.Text == expectedExpanded && captured.CursorUtf16Offset == expectedOffset && submits.SequenceEqual(expectedSubmits) && captured.Layout is not null;
            if (!match) failedSteps++;
            actualSteps.Add(new { raw, sourceExpected = new { text = expectedText, expandedText = expectedExpanded, cursorUtf16Offset = expectedOffset, submits = expectedSubmits },
                actual = new { text = captured.Text, cursorUtf16Offset = captured.CursorUtf16Offset, submits = submits.ToArray(), sourceLayoutCaptured = captured.Layout is not null }, matchesSource = match });
        }
        receipts.Complete(); sink.Complete(); await run;
        sink.AssertJoined();
        if (ended != 1 || receipts.Snapshot != (0, 0, 0)) throw new InvalidOperationException("Input/receipt ownership did not settle");
    }
    finally
    {
        receipts.Complete(); stop.Cancel(); sink.Complete();
        if (run is not null) { try { await run; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { } }
        sink.AssertJoined();
    }
    if (failedSteps != 0) jumpFailed++;
    jumps.Add(new { id, failedSteps, actualSteps, allOwnedReadsJoined = true, inputEnded = ended, receiptSnapshot = receipts.Snapshot });
}
return new { cases = jumps.Count, failed = jumpFailed, observations = jumps };
    }
}

internal sealed class InputSink : IConsoleTerminal
{
    private readonly Channel<string> input = Channel.CreateBounded<string>(2);
    private int reads;
    private long started, settled;
    internal ValueTask Feed(string value, CancellationToken token) => input.Writer.WriteAsync(value, token);
    internal void Complete() => input.Writer.TryComplete();
    public TerminalLeaseSnapshot Snapshot { get { var s = new TerminalConsoleState(0, 0, 65001, 65001, 25, true, 0, 0); return new(s, s, null, false, false, reads, 0, started, settled, 0, 0); } }
    public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
    {
        if (Interlocked.Increment(ref reads) != 1) throw new IOException("Concurrent input reads");
        Interlocked.Increment(ref started);
        try
        {
            if (!await input.Reader.WaitToReadAsync(token)) return 0;
            if (!input.Reader.TryRead(out var value) || value.Length > destination.Length) throw new IOException("Input chunk invariant");
            value.AsMemory().CopyTo(destination); return value.Length;
        }
        finally { Interlocked.Decrement(ref reads); Interlocked.Increment(ref settled); }
    }
    public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default) => throw new InvalidOperationException("No physical writer in keypad preparation");
    public ValueTask DisposeAsync() => throw new InvalidOperationException("Borrowed console must survive");
    internal void AssertJoined() { if (reads != 0 || started != settled) throw new InvalidOperationException("Owned read not joined"); }
}
