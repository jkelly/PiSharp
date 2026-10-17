// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-output.test.ts. Verbatim apart from these adaptations:
// - Node's Buffer (from, byteLength, subarray().toString) is Encoding.UTF8, which encodes a lone surrogate as U+FFFD and decodes invalid
//   data with one U+FFFD per maximal subpart, as Buffer does.
// - vi.useFakeTimers({now: 0}) is a ManualTimeProvider starting at 0 passed to Progress; advanceTimersByTimeAsync(ms) is Advance(ms). A
//   write that completes synchronously settles inline (see Output.cs), so no microtask flush is needed between steps.
// - The deterministic fuzz PRNG `(seed * 1664525 + 1013904223) >>> 0` is the same recurrence on uint.
// - BoundedOutput is a record struct, so `toEqual({text, droppedBytes, droppedLines})` is struct equality.
using System.Text;
using PiSharp.Durable;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessOutputCases() => OutputTests.Cases("harness.output.");
}

/// <summary>The harness.output.* cases (upstream harness-output.test.ts).</summary>
internal static class OutputTests
{
    private static OutputLimits Head(long maxLines, long maxBytes = 1000) => new(maxBytes, maxLines, ToolOutputRetention.Head);
    private static OutputLimits Tail(long maxLines, long maxBytes = 1000) => new(maxBytes, maxLines, ToolOutputRetention.Tail);

    private static string BufferTail(string content, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length <= maxBytes) return content;
        var start = bytes.Length - maxBytes;
        while (start < bytes.Length && (bytes[start] & 0xc0) == 0x80) start++;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    /// <summary>A single line longer than the byte limit is cut like Buffer's tail slice on a character boundary.</summary>
    private static void AssertMatchesBufferTail(string input, IReadOnlyList<int>? maxByteValues = null)
    {
        var totalBytes = Encoding.UTF8.GetByteCount(input);
        var values = maxByteValues ?? [.. Enumerable.Range(0, totalBytes + 5)];
        foreach (var maxBytes in values)
        {
            var kept = ToolOutput.BoundOutput(input, new OutputLimits(maxBytes, 10, ToolOutputRetention.Tail)).Text;
            var expected = BufferTail(input, maxBytes);
            if (kept != expected)
                throw new InvalidOperationException($"tail mismatch input={Escape(input)} maxBytes={maxBytes} expected={Escape(expected)} actual={Escape(kept)}");
            if (Encoding.UTF8.GetByteCount(kept) > maxBytes) throw new InvalidOperationException($"tail output exceeded {maxBytes} bytes");
        }
    }

    private static string Escape(string text) => string.Concat(text.Select(character => character < 0x20 || character > 0x7e ? $"\\u{(int)character:x4}" : character.ToString()));

    private static List<int> SampledByteLimits(string input)
    {
        var totalBytes = Encoding.UTF8.GetByteCount(input);
        int[] candidates = [0, 1, 2, 3, 4, 5, 8, totalBytes / 2, totalBytes - 4, totalBytes - 1, totalBytes, totalBytes + 1];
        return [.. candidates.Where(value => value >= 0).Distinct().Order()];
    }

    private static (string Kept, long DroppedBytes, long DroppedLines) Bound(string text, OutputLimits limits)
    {
        var slice = ToolOutput.BoundOutput(text, limits);
        return (slice.Text, slice.DroppedBytes, slice.DroppedLines);
    }

    private static void EqualBound((string, long, long) expected, (string, long, long) actual, string what) =>
        Check(expected == actual, $"{what}: expected {Show(expected)}, actual {Show(actual)}");

    private static string Show((string Kept, long DroppedBytes, long DroppedLines) value) =>
        $"{{kept: \"{Escape(value.Kept)}\", droppedBytes: {value.DroppedBytes}, droppedLines: {value.DroppedLines}}}";

    private static void EqualOutput(BoundedOutput expected, BoundedOutput actual, string what) =>
        Check(expected == actual, $"{what}: expected {Show((expected.Text, expected.DroppedBytes, expected.DroppedLines))}, actual {Show((actual.Text, actual.DroppedBytes, actual.DroppedLines))}");

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        // ─── tool output bounds ─────────────────────────────────────────────

        yield return Case(prefix + "removes-control-characters-but-keeps-tabs-newlines-and-other-text", () =>
            Equal("ab\tc\ndefgh😀", ToolOutput.SanitizeOutput("a\0b\tc\nd\re\u0007f￹g￻h😀"), "sanitized"));

        yield return Case(prefix + "keeps-output-within-the-limits-unchanged", () =>
        {
            EqualBound(("a\nb\n", 0, 0), Bound("a\nb\n", Head(2)), "head");
            EqualBound(("a\nb", 0, 0), Bound("a\nb", Tail(2)), "tail");
            EqualBound(("", 0, 0), Bound("", Tail(2)), "empty");
        });

        yield return Case(prefix + "keeps-nothing-with-a-zero-limit", () =>
        {
            EqualBound(("", 6, 2), Bound("ab\ncd\n", Head(10, 0)), "zero bytes");
            EqualBound(("", 6, 2), Bound("ab\ncd\n", Tail(0)), "zero lines");
        });

        yield return Case(prefix + "keeps-exact-slices-of-whole-lines-trailing-newline-included", () =>
        {
            EqualBound(("a\nb\n", 2, 1), Bound("a\nb\nc\n", Head(2)), "head");
            EqualBound(("b\nc\n", 2, 1), Bound("a\nb\nc\n", Tail(2)), "tail");
            EqualBound(("b\nc", 2, 1), Bound("a\nb\nc", Tail(2)), "tail unterminated");
            // Blank lines are lines.
            EqualBound(("b\nc\n\n", 2, 1), Bound("a\nb\nc\n\n", Tail(3)), "blank lines");
        });

        yield return Case(prefix + "cuts-at-the-byte-limit-on-whole-lines-when-possible", () =>
        {
            EqualBound(("aa\nbb\n", 3, 1), Bound("aa\nbb\ncc\n", Head(10, 7)), "head");
            EqualBound(("bb\ncc\n", 3, 1), Bound("aa\nbb\ncc\n", Tail(10, 7)), "tail");
        });

        yield return Case(prefix + "cuts-a-single-line-longer-than-the-byte-limit-on-a-character-boundary", () =>
        {
            // "é" is two bytes; five bytes hold two whole characters.
            EqualBound(("éé", 3, 0), Bound("ééé\n", Head(10, 5)), "head");
            EqualBound(("éé", 6, 1), Bound("x\néééé", Tail(10, 5)), "tail");
        });

        yield return Case(prefix + "keeps-a-u-feff-at-the-start-of-a-kept-slice", () =>
            EqualBound(("﻿a", 1, 0), Bound("x﻿a", Tail(10, 4)), "bom"));

        yield return Case(prefix + "cuts-tails-of-surrogate-edge-cases-exactly-like-buffer", () =>
        {
            string[] inputs = ["a\ud83d", "\ude42b", "a\ude42b", "\ud83d🙂", "🙂\ude42", "👩‍💻"];
            foreach (var input in inputs) AssertMatchesBufferTail(input);
        });

        yield return Case(prefix + "cuts-tails-exactly-like-buffer-across-deterministic-fuzz-cases", () =>
        {
            string[] alphabet =
            [
                "a", "\u007f", "\u0080", "é", "߿", "ࠀ", "中", "퟿", "\ud800", "\ud83d", "\udc00", "\ude42", "🙂", "", "￿",
            ];
            void CheckExhaustive(string prefixText, int depth)
            {
                AssertMatchesBufferTail(prefixText, SampledByteLimits(prefixText));
                if (depth == 0) return;
                foreach (var character in alphabet) CheckExhaustive(prefixText + character, depth - 1);
            }
            CheckExhaustive("", 3);
            var seed = 0x12345678u;
            double Random()
            {
                seed = unchecked(seed * 1664525u + 1013904223u);
                return seed / 4294967296.0;
            }
            for (var i = 0; i < 1_000; i++)
            {
                var input = new StringBuilder();
                var length = (int)Math.Floor(Random() * 80);
                for (var j = 0; j < length; j++) input.Append(alphabet[(int)Math.Floor(Random() * alphabet.Length)]);
                AssertMatchesBufferTail(input.ToString(), SampledByteLimits(input.ToString()));
            }
        });

        // ─── OutputBuffer ───────────────────────────────────────────────────

        yield return Case(prefix + "keeps-exact-totals-across-chunks-and-decodes-utf-8-split-across-byte-chunks", () =>
        {
            var buffer = new OutputBuffer(Tail(2));
            var bytes = Encoding.UTF8.GetBytes("😀\n");
            buffer.Push("a\nb\n");
            buffer.Push(bytes.AsSpan(0, 2));
            buffer.Push(bytes.AsSpan(2));
            EqualOutput(new BoundedOutput("b\n😀\n", 2, 1), buffer.Snapshot(), "snapshot");
        });

        yield return Case(prefix + "sanitizes-the-retained-text-but-counts-the-raw-stream", () =>
        {
            var buffer = new OutputBuffer(Tail(1));
            buffer.Push("a\u0007\n");
            EqualOutput(new BoundedOutput("a\n", 0, 0), buffer.Snapshot(), "first");
            buffer.Push("b\u001b\n");
            EqualOutput(new BoundedOutput("b\n", 3, 1), buffer.Snapshot(), "second");
        });

        yield return Case(prefix + "drops-a-byte-order-mark-only-at-the-start-of-the-output", () =>
        {
            byte[] bom = [0xef, 0xbb, 0xbf];
            var buffer = new OutputBuffer(Tail(10));
            buffer.Push(bom.AsSpan(0, 1));
            buffer.Push([0xbb, 0xbf, 0x61]);
            buffer.Push("b");
            // A U+FEFF after a string chunk is text.
            buffer.Push([.. bom, 0x63]);
            buffer.End();
            Equal("ab﻿c", buffer.Snapshot().Text, "text");
        });

        yield return Case(prefix + "flushes-an-incomplete-character-before-a-string-chunk-and-at-the-end", () =>
        {
            var buffer = new OutputBuffer(Tail(10));
            var euro = Encoding.UTF8.GetBytes("€");
            buffer.Push(euro.AsSpan(0, 1));
            buffer.Push("x");
            buffer.Push(euro.AsSpan(0, 2));
            buffer.End();
            Equal("�x�", buffer.Snapshot().Text, "text");
        });

        yield return Case(prefix + "matches-bounding-the-whole-stream-when-several-chunks-arrive-between-snapshots", () =>
        {
            foreach (var limits in new[] { Head(3, 40), Tail(3, 40), Head(50, 25), Tail(50, 25) })
            {
                var buffer = new OutputBuffer(limits);
                var stream = new StringBuilder();
                for (var index = 0; index < 300; index++)
                {
                    var chunk = index % 7 == 0 ? $"{string.Concat(Enumerable.Repeat("é", index % 30))}\n" : $"line {index}\n";
                    stream.Append(chunk);
                    buffer.Push(chunk);
                    if (index % 5 != 4) continue;
                    var expected = ToolOutput.BoundOutput(stream.ToString(), limits);
                    EqualOutput(new BoundedOutput(expected.Text, expected.DroppedBytes, expected.DroppedLines), buffer.Snapshot(), $"{limits} at {index}");
                }
            }
        });

        yield return Case(prefix + "stops-storing-head-output-once-the-window-is-full", () =>
        {
            var buffer = new OutputBuffer(Head(2));
            for (var index = 0; index < 1000; index++) buffer.Push($"line {index}\n");
            Check(buffer.StoredBytes < 20, $"stored {buffer.StoredBytes} bytes");
            EqualOutput(new BoundedOutput("line 0\nline 1\n", 8876, 998), buffer.Snapshot(), "snapshot");
        });

        yield return Case(prefix + "stores-only-the-tail-window-after-each-snapshot", () =>
        {
            var buffer = new OutputBuffer(Tail(3, 100));
            var stream = new StringBuilder();
            for (var index = 0; index < 2000; index++)
            {
                var chunk = $"line {index}\n\n";
                stream.Append(chunk);
                buffer.Push(chunk);
                var snapshot = buffer.Snapshot();
                Check(buffer.StoredBytes <= 100, $"stored {buffer.StoredBytes} bytes at {index}");
                Equal(ToolOutput.BoundOutput(stream.ToString(), Tail(3, 100)).Text, snapshot.Text, $"text at {index}");
            }
        });

        // ─── Progress ───────────────────────────────────────────────────────

        yield return Case(prefix + "commits-the-first-change-at-once-then-waits-at-least-100-ms-and-the-written-size-at-100-kib-s", () =>
        {
            var time = new ManualTimeProvider();
            var commits = new List<double>();
            long size = 50 * 1024;
            var progress = new Progress(() =>
            {
                commits.Add(time.NowMs());
                return Task.FromResult(size);
            }, _ => { }, 100, time);
            progress.Mark();
            time.Advance(TimeSpan.Zero);
            Seq([0d], commits, "first");
            // 50 KiB buys 500 ms; changes meanwhile coalesce into one commit.
            size = 10;
            progress.Mark();
            progress.Mark();
            time.Advance(TimeSpan.FromMilliseconds(499));
            Seq([0d], commits, "coalesced");
            time.Advance(TimeSpan.FromMilliseconds(1));
            Seq([0d, 500], commits, "second");
            // A small commit still waits the minimum 100 ms.
            progress.Mark();
            time.Advance(TimeSpan.FromMilliseconds(99));
            Seq([0d, 500], commits, "minimum");
            time.Advance(TimeSpan.FromMilliseconds(1));
            Seq([0d, 500, 600], commits, "third");
        });

        yield return Case(prefix + "waits-the-configured-minimum-interval-between-small-commits", () =>
        {
            var time = new ManualTimeProvider();
            var commits = new List<double>();
            var progress = new Progress(() =>
            {
                commits.Add(time.NowMs());
                return Task.FromResult(10L);
            }, _ => { }, 500, time);
            progress.Mark();
            time.Advance(TimeSpan.Zero);
            progress.Mark();
            time.Advance(TimeSpan.FromMilliseconds(499));
            Seq([0d], commits, "waiting");
            time.Advance(TimeSpan.FromMilliseconds(1));
            Seq([0d, 500], commits, "second");
        });

        yield return Case(prefix + "rejects-the-waiters-of-a-failed-commit-and-reports-its-error", async () =>
        {
            var errors = new List<Exception>();
            var failure = new InvalidOperationException("commit failed");
            var progress = new Progress(() => Task.FromException<long>(failure), errors.Add, 100);
            try
            {
                await progress.MarkAndWait();
                throw new InvalidOperationException("expected a rejection");
            }
            catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { }
            Check(errors.Count == 1 && ReferenceEquals(errors[0], failure), "reported");
        });

        yield return Case(prefix + "stops-waits-for-the-commit-in-flight-and-hands-back-waiters-no-commit-covered-yet", async () =>
        {
            var time = new ManualTimeProvider();
            var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var progress = new Progress(async () =>
            {
                await inFlight.Task;
                return 0;
            }, _ => { }, 100, time);
            var first = progress.MarkAndWait();
            var second = progress.MarkAndWait();
            var stopped = progress.StopAsync();
            inFlight.SetResult();
            var pending = await stopped;
            await first;
            Equal(1, pending.Count, "pending");
            pending[0].TrySetResult();
            await second;
        });
    }
}
