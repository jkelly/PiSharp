using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;
using PiSharp.Tui.Input;

if (args.Length != 4 || File.Exists(args[2])) throw new ArgumentException("Frozen source expectations, pending-jump capture and fresh report required");
using var expectationDocument = JsonDocument.Parse(File.ReadAllText(args[0]));
using var jumpDocument = JsonDocument.Parse(File.ReadAllText(args[1]));
var expectations = expectationDocument.RootElement;
var cases = new List<object>();
var wholeFailed = 0;
var splitSchedules = 0;
var splitParityFailed = 0;
var splitInvarianceFailed = 0;
var casesFailed = 0;
foreach (var input in expectations.GetProperty("cases").EnumerateArray())
{
    var id = input.GetProperty("id").GetString()!;
    var raw = input.GetProperty("raw").GetString()!;
    var expected = input.GetProperty("expected");
    object Project(TerminalInputEvent value) => value switch
    {
        TerminalKey key => new { kind = "key", key = key.Key, modifiers = key.Modifiers.ToString(), action = key.Action.ToString() },
        TerminalUnknownSequence unknown => new { kind = "unknown", sequence = unknown.Sequence },
        TerminalText text => new { kind = "text", text = text.Text },
        _ => new { kind = value.GetType().Name }
    };
    bool Matches(IReadOnlyList<TerminalInputEvent> values) => values.Count == 1 && values[0] is TerminalKey key &&
        key.Key == expected.GetProperty("key").GetString() && key.Modifiers.ToString() == expected.GetProperty("modifiers").GetString() &&
        key.Action.ToString() == expected.GetProperty("action").GetString();
    var decoder = new TerminalInputDecoder();
    var whole = decoder.Feed(raw.AsSpan()).Concat(decoder.Complete()).ToArray();
    var wholeMatch = Matches(whole);
    if (!wholeMatch) wholeFailed++;
    var splits = new List<object>();
    var caseFailed = !wholeMatch;
    foreach (var offset in input.GetProperty("splitOffsets").EnumerateArray())
    {
        var split = offset.GetInt32();
        var incremental = new TerminalInputDecoder();
        var values = incremental.Feed(raw.AsSpan(0, split)).Concat(incremental.Feed(raw.AsSpan(split))).Concat(incremental.Complete()).ToArray();
        var expectedMatch = Matches(values);
        var wholeMatchAtSplit = JsonSerializer.Serialize(values.Select(Project)) == JsonSerializer.Serialize(whole.Select(Project));
        splitSchedules++;
        if (!expectedMatch) { splitParityFailed++; caseFailed = true; }
        if (!wholeMatchAtSplit) { splitInvarianceFailed++; caseFailed = true; }
        splits.Add(new { split, expectedMatch, wholeMatchAtSplit, actual = values.Select(Project).ToArray() });
    }
    if (caseFailed) casesFailed++;
    cases.Add(new { id, raw, expected, wholeMatchesSource = wholeMatch, actual = whole.Select(Project).ToArray(), splits, failed = caseFailed });
}
if (cases.Count != 35 || splitSchedules != 306) throw new InvalidOperationException("Original35case/306split inventory changed");
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
var identity = EventIdentityTests.Run(args[3]);
var identityFailed = JsonSerializer.SerializeToElement(identity).GetProperty("failed").GetInt32();
var legacy = await LegacyInputParityTests.Run(args[1]);
var legacyFailed = JsonSerializer.SerializeToElement(legacy).GetProperty("failed").GetInt32();
var failed = casesFailed + jumpFailed + identityFailed + legacyFailed;
File.WriteAllText(args[2], JsonSerializer.Serialize(new { sourceCases = cases.Count, splitSchedules, wholeFailed, splitParityFailed, splitInvarianceFailed,
    casesFailed, cases, pendingJumpCases = jumps.Count, pendingJumpCasesFailed = jumpFailed, pendingJumpObservations = jumps,
    failed, identity, legacy, allOwnedExecutionsJoined = true, physicalTerminal = false, fullNativeGate = false, actualInputReceiptBoundary = true,
    boundary = "Exact candidate product Tui/Cli DLLs execute original35key identities/306splits and five unchanged Source pending-jump acknowledged-input witnesses, plus48canonical record/provenance identity cases. Separate legacy route witnesses are reported. No actual TerminalSessionCommand/provider/session/full-native/physical gate." }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine(JsonSerializer.Serialize(new { sourceCases = cases.Count, splitSchedules, wholeFailed, splitParityFailed, splitInvarianceFailed, casesFailed, pendingJumpCases = jumps.Count, pendingJumpCasesFailed = jumpFailed, identityCases = 48, identityFailed, legacyFailed, failed, allOwnedExecutionsJoined = true }));
return failed == 0 ? 0 : 1;

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
