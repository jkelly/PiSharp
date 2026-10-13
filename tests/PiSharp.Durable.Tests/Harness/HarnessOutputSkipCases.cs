// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/harness-output-skip.test.ts. Verbatim apart from these
// adaptations: the mulberry32 PRNG runs on uint, which is Math.imul's and `>>> 0`'s arithmetic modulo 2^32; `for (const character of
// text)` walks code points, a surrogate pair as one; Buffer.byteLength is Encoding.UTF8.GetByteCount; BoundedOutput is a record struct, so
// toEqual is struct equality.
using System.Text;
using PiSharp.Durable;
using PiSharp.Durable.Env;
using static Program;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessOutputSkipCases() => OutputSkipTests.Cases("harness.output-skip.");
}

/// <summary>The harness.output-skip.* cases (upstream harness-output-skip.test.ts).</summary>
internal static class OutputSkipTests
{
    /// <summary>Deterministic PRNG (mulberry32) so failures reproduce from the printed seed.</summary>
    private static Func<double> Random(uint seed)
    {
        var state = seed;
        return () =>
        {
            state = unchecked(state + 0x6d2b79f5u);
            var t = state;
            t = unchecked((t ^ (t >> 15)) * (t | 1));
            t ^= unchecked(t + (t ^ (t >> 7)) * (t | 61));
            return (t ^ (t >> 14)) / 4294967296.0;
        };
    }

    // Decoded shell output: ASCII, multi-byte and astral characters, replacement characters, CR/LF, tabs, and control characters that
    // sanitizing removes after bounding.
    private static readonly string[] Alphabet = ["a", "b", "z", " ", "\n", "\n", "\n", "\r\n", "\t", "é", "€", "😀", "�", "\x01", "\x1b"];

    private static string RandomChunk(Func<double> next)
    {
        var length = (int)Math.Floor(next() * 12);
        var text = new StringBuilder();
        for (var index = 0; index < length; index++) text.Append(Alphabet[(int)Math.Floor(next() * Alphabet.Length)]);
        return text.ToString();
    }

    private static ShellOutputSkip Measure(string text) =>
        new(Encoding.UTF8.GetByteCount(text), text.Count(character => character == '\n'), text.EndsWith('\n'));

    /// <summary>Whether <paramref name="text"/> proves that everything before it is outside the tail window.</summary>
    private static bool ExceedsWindow(string text, OutputLimits limits)
    {
        var measured = Measure(text);
        return measured.Bytes > limits.MaxBytes || measured.Newlines > limits.MaxLines;
    }

    /// <summary>Code-point boundaries of <paramref name="text"/>, so a cut never splits a surrogate pair.</summary>
    private static List<int> Boundaries(string text)
    {
        var result = new List<int> { 0 };
        for (var offset = 0; offset < text.Length;)
        {
            offset += char.IsHighSurrogate(text[offset]) && offset + 1 < text.Length && char.IsLowSurrogate(text[offset + 1]) ? 2 : 1;
            result.Add(offset);
        }
        return result;
    }

    private static void Same(BoundedOutput expected, BoundedOutput actual, string what) =>
        Check(expected == actual, $"{what}: expected {expected}, actual {actual}");

    /// <summary>
    /// Feeds random output to one buffer in full and to another through a reference skipper that follows the contract of
    /// <c>ShellOutputInfo.skipped</c>: it holds undelivered text, and when it flushes, it may replace a prefix of it by counts if the rest
    /// exceeds the window. Snapshots must agree whenever both buffers have seen the same output.
    /// </summary>
    private static void CheckSeed(uint seed)
    {
        var next = Random(seed);
        var limits = new OutputLimits(1 + (long)Math.Floor(next() * 40), 1 + (long)Math.Floor(next() * 5), ToolOutputRetention.Tail);
        var full = new OutputBuffer(limits);
        var skipping = new OutputBuffer(limits);
        var pending = "";
        var skips = 0;
        void Flush()
        {
            if (pending.Length == 0) return;
            var text = pending;
            var cuts = Boundaries(text).Where(cut => cut > 0 && ExceedsWindow(text[cut..], limits)).ToList();
            if (cuts.Count > 0 && next() < 0.7)
            {
                var cut = cuts[(int)Math.Floor(next() * cuts.Count)];
                skipping.Push(text[cut..], Measure(text[..cut]));
                skips++;
            }
            else
            {
                skipping.Push(text);
            }
            pending = "";
            // Progress snapshots happen between deliveries and compact stored chunks.
            if (next() < 0.5) skipping.Snapshot();
            Same(full.Snapshot(), skipping.Snapshot(), $"seed {seed}");
        }
        var chunks = (int)Math.Floor(next() * 40);
        for (var index = 0; index < chunks; index++)
        {
            var chunk = RandomChunk(next);
            full.Push(chunk);
            if (next() < 0.3) full.Snapshot();
            pending += chunk;
            if (next() < 0.3) Flush();
        }
        Flush();
        full.End();
        skipping.End();
        Same(full.Snapshot(), skipping.Snapshot(), $"seed {seed}");
        Check(skips >= 0, "skips");
    }

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        // Progress commits snapshot at arbitrary moments; snapshots compact what is stored and must never change the window. A compaction to
        // exactly the kept window lost the character that decides where a later window's first line starts.
        yield return Case(prefix + "keeps-the-same-tail-whenever-progress-snapshots-happen", () =>
        {
            for (var seed = 1u; seed <= 3000; seed++)
            {
                var next = Random(seed);
                var limits = new OutputLimits(1 + (long)Math.Floor(next() * 40), 1 + (long)Math.Floor(next() * 5), ToolOutputRetention.Tail);
                var plain = new OutputBuffer(limits);
                var sampled = new OutputBuffer(limits);
                var chunks = (int)Math.Floor(next() * 30);
                for (var index = 0; index < chunks; index++)
                {
                    var chunk = RandomChunk(next);
                    plain.Push(chunk);
                    sampled.Push(chunk);
                    if (next() < 0.4) sampled.Snapshot();
                }
                Same(plain.Snapshot(), sampled.Snapshot(), $"seed {seed}");
            }
        });

        yield return Case(prefix + "matches-the-full-stream-for-every-legal-skip-pattern", () =>
        {
            for (var seed = 1u; seed <= 3000; seed++) CheckSeed(seed);
        });

        yield return Case(prefix + "counts-skipped-bytes-lines-and-the-final-newline-exactly", () =>
        {
            var limits = new OutputLimits(1000, 2, ToolOutputRetention.Tail);
            var full = new OutputBuffer(limits);
            var skipping = new OutputBuffer(limits);
            // The skipped text ends without a newline, so its last line continues in the delivered text.
            const string omitted = "one\ntwo\nthr";
            const string kept = "ee\nfour\nfive\nsix";
            full.Push(omitted + kept);
            skipping.Push(kept, Measure(omitted));
            Same(full.Snapshot(), skipping.Snapshot(), "same");
            Same(new BoundedOutput("five\nsix", 19, 4), skipping.Snapshot(), "snapshot");
        });

        yield return Case(prefix + "refuses-skips-for-head-retention", () =>
        {
            var buffer = new OutputBuffer(new OutputLimits(10, 2, ToolOutputRetention.Head));
            SessionTests.Throws(() => buffer.Push("x\ny\nz\n", new ShellOutputSkip(3, 1, true)), "tail retention", "head skip");
        });
    }
}
