using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Interactive;

internal static class LegacyInputParityTests
{
    internal static async Task<object> Run(string sourcePath)
    {
        using var source = JsonDocument.Parse(File.ReadAllText(sourcePath));
        var observations = new List<object>();
        var failed = 0;
        foreach (var sourceCase in source.RootElement.GetProperty("cases").EnumerateArray())
        {
            var sink = new InputSink();
            var captures = Channel.CreateBounded<TerminalDraftSnapshot>(2);
            var submits = new List<string>();
            var steps = new List<object>();
            var failedSteps = 0;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            Task<TerminalInputExit>? run = null;
            try
            {
                run = new TerminalChatInput(sink).RunAsync((text, _) => { submits.Add(text); return Task.FromResult(true); },
                    async (snapshot, token) => await captures.Writer.WriteAsync(snapshot, token), stop.Cancel, stop.Token);
                foreach (var expected in sourceCase.GetProperty("steps").EnumerateArray())
                {
                    var raw = expected.GetProperty("raw").GetString()!;
                    await sink.Feed(raw, stop.Token);
                    var actual = await captures.Reader.ReadAsync(stop.Token);
                    var text = expected.GetProperty("text").GetString()!;
                    var cursor = expected.GetProperty("cursor");
                    var offset = cursor.GetProperty("col").GetInt32() + text.Split('\n').Take(cursor.GetProperty("line").GetInt32()).Sum(s => s.Length + 1);
                    var expectedSubmits = expected.GetProperty("submits").EnumerateArray().Select(x => x.GetString()!).ToArray();
                    var match = actual.Text == text && actual.Text == expected.GetProperty("expandedText").GetString() && actual.CursorUtf16Offset == offset && submits.SequenceEqual(expectedSubmits);
                    if (!match) failedSteps++;
                    steps.Add(new { raw, sourceExpected = new { text, cursorUtf16Offset = offset, submits = expectedSubmits },
                        actual = new { text = actual.Text, cursorUtf16Offset = actual.CursorUtf16Offset, submits = submits.ToArray() }, matchesSource = match });
                }
                sink.Complete(); await run; sink.AssertJoined();
            }
            finally
            {
                stop.Cancel(); sink.Complete();
                if (run is not null) { try { await run; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { } }
                sink.AssertJoined();
            }
            if (failedSteps != 0) failed++;
            observations.Add(new { id = sourceCase.GetProperty("id").GetString(), failedSteps, steps, allOwnedReadsJoined = true });
        }
        return new { cases = observations.Count, failed, observations, actualLegacyInputSourceExecuted = true };
    }
}
