using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Durable.Env;

// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/env-line-scan.test.ts, as env.line-scan.<case>. The seeded
// generator and chunking are ported exactly. Upstream compares against Node's `new TextDecoder().decode(file)`; here
// Fixtures/env-line-scan.upstream.json (printed by Fixtures/capture-env-line-scan.mjs, which runs the pinned decode.ts and line-scan.ts under
// Node) holds, per seed, a digest of the file with its whole-file TextDecoder text and of upstream's LineScanner result, so each case checks
// the C# port against Node's output as well as the test's own decode-and-split assertions. Adapted: the RangeError for LineScanner(1.5)
// has no analogue (line numbers are longs).
internal static partial class Program
{
    private static readonly byte[][] LineScanPieces =
    [
        [0x0a], [0x0a], [0x61], [0x62, 0x63], [0xef, 0xbb, 0xbf], [0xc3, 0xa9], [0xe2, 0x82, 0xac], [0xf0, 0x9f, 0x98, 0x80],
        [0xe2, 0x82], [0xff], [0x80], [0xf0, 0x9f], [0x0d, 0x0a],
    ];

    private static IEnumerable<(string, Func<Task>)> EnvLineScanCases()
    {
        var fixture = new Lazy<JsonElement>(() => JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", "env-line-scan.upstream.json"))).RootElement);

        // Node's streaming TextDecoder with BOM handling dropped this U+FEFF, which follows an invalid sequence.
        yield return Case("env.line-scan.keeps-a-u-feff-that-does-not-start-the-stream", () =>
        {
            byte[] bytes = [0xe2, 0x82, 0xef, 0xbb, 0xbf, 0x61];
            var decoder = new StreamDecoder();
            var text = string.Concat(bytes.Select(value => decoder.Decode([value]))) + decoder.Finish();
            Equal("�﻿a", text, "text");
            Equal(string.Join(",", fixture.Value.GetProperty("keepsLaterBom").EnumerateArray().Select(code => code.GetInt32())),
                string.Join(",", text.Select(character => (int)character)), "Node's text");
        });

        yield return Case("env.line-scan.decodes-any-chunking-like-decoding-the-whole-stream", () =>
        {
            var expected = fixture.Value.GetProperty("decode").EnumerateArray().Select(digest => digest.GetString()!).ToArray();
            Equal(5000, expected.Length, "captured seeds");
            for (var seed = 1; seed <= 5000; seed++)
            {
                var next = LineScanRandom(seed);
                var bytes = LineScanFile(next);
                var decoder = new StreamDecoder();
                var text = new StringBuilder();
                for (var offset = 0; offset < bytes.Length;)
                {
                    var size = 1 + (int)Math.Floor(next() * 7);
                    text.Append(decoder.Decode(bytes.AsSpan(offset, Math.Min(size, bytes.Length - offset))));
                    offset += size;
                }
                text.Append(decoder.Finish());
                // Node's whole-file decode of the same file, by digest.
                Equal(expected[seed - 1], LineScanDigest($"{seed}|{Convert.ToHexStringLower(bytes)}|{Convert.ToHexStringLower(Encoding.UTF8.GetBytes(text.ToString()))}"),
                    $"seed {seed}");
            }
        });

        yield return Case("env.line-scan.agrees-with-decoding-and-splitting-the-whole-file", () =>
        {
            var expected = fixture.Value.GetProperty("scan").EnumerateArray().Select(digest => digest.GetString()!).ToArray();
            Equal(5000, expected.Length, "captured seeds");
            for (var seed = 1; seed <= 5000; seed++)
            {
                var next = LineScanRandom(seed);
                var file = LineScanFile(next);
                var lines = WholeDecode(file).Split('\n');
                var startLine = (long)Math.Floor(next() * (lines.Length + 2));
                long? endLine = next() < 0.3 ? null : startLine + 1 + (long)Math.Floor(next() * (lines.Length + 1));
                var scanner = new LineScanner(startLine, endLine);
                for (var offset = 0; offset < file.Length;)
                {
                    var size = 1 + (int)Math.Floor(next() * 7);
                    scanner.Push(file.AsSpan(offset, Math.Min(size, file.Length - offset)));
                    offset += size;
                }
                var scan = scanner.Finish();
                var message = $"seed {seed}";
                Equal(expected[seed - 1], LineScanDigest($"{seed}|{startLine}|{endLine?.ToString() ?? ""}|{scan.Newlines},{scan.Start},{scan.End}," +
                    $"{scan.FirstLineEnd},{scan.LastLineStart},{scan.SelectedBytes},{scan.FirstLineBytes}"), message + " (upstream's scan)");
                Equal(lines.Length - 1L, scan.Newlines, message);
                var selected = lines[(int)Math.Min(startLine, lines.Length)..(int)Math.Min(endLine ?? lines.Length, lines.Length)];
                string Decode(long from, long to) => from > 0 ? RangeDecode(file.AsSpan((int)from, (int)(to - from))) : WholeDecode(file.AsSpan(0, (int)to));
                Equal(string.Join('\n', selected), Decode(scan.Start, scan.End), message);
                Equal((long)Encoding.UTF8.GetByteCount(string.Join('\n', selected)), scan.SelectedBytes, message);
                if (startLine < lines.Length)
                {
                    Equal(lines[(int)startLine], Decode(scan.Start, scan.FirstLineEnd), message);
                    Equal((long)Encoding.UTF8.GetByteCount(lines[(int)startLine]), scan.FirstLineBytes, message);
                    var lastLine = Math.Min(endLine ?? lines.Length, lines.Length) - 1;
                    var lastEnd = lastLine + 1 < lines.Length ? Array.IndexOf(file, (byte)0x0a, (int)scan.LastLineStart) : file.Length;
                    Equal(lines[(int)lastLine], Decode(scan.LastLineStart, lastEnd), message);
                }
                else Check(scan.Start == file.Length && scan.End == file.Length && scan.SelectedBytes == 0, message + ": " + scan);
            }
        });

        yield return Case("env.line-scan.rejects-empty-or-invalid-ranges", () =>
        {
            Throws<ArgumentOutOfRangeException>(() => new LineScanner(2, 2), "an empty range");
            Throws<ArgumentOutOfRangeException>(() => new LineScanner(-1), "a negative start");
        });
    }

    /// <summary>The test's seeded generator (a mulberry32 variant), in 32-bit unsigned arithmetic as JavaScript's <c>>>> 0</c> and
    /// <c>Math.imul</c> compute it.</summary>
    private static Func<double> LineScanRandom(int seed)
    {
        var state = (uint)seed;
        return () =>
        {
            state += 0x6d2b79f5;
            var t = state;
            t = (t ^ (t >> 15)) * (t | 1);
            t ^= t + (t ^ (t >> 7)) * (t | 61);
            return (t ^ (t >> 14)) / 4294967296.0;
        };
    }

    private static byte[] LineScanFile(Func<double> next)
    {
        var bytes = new List<byte>();
        var pieces = (int)Math.Floor(next() * 60);
        for (var index = 0; index < pieces; index++) bytes.AddRange(LineScanPieces[(int)Math.Floor(next() * LineScanPieces.Length)]);
        return [.. bytes];
    }

    private static string LineScanDigest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary><c>new TextDecoder().decode(bytes)</c>, which drops a leading byte-order mark.</summary>
    private static string WholeDecode(ReadOnlySpan<byte> bytes)
    {
        var decoder = new StreamDecoder();
        return decoder.Decode(bytes) + decoder.Finish();
    }

    /// <summary><c>new TextDecoder("utf-8", { ignoreBOM: true }).decode(bytes)</c>, which keeps it.</summary>
    private static string RangeDecode(ReadOnlySpan<byte> bytes)
    {
        var decoder = Utf8Decoding.RangeDecoder();
        var chars = new char[decoder.GetCharCount(bytes, flush: true)];
        return new string(chars, 0, decoder.GetChars(bytes, chars, flush: true));
    }
}
