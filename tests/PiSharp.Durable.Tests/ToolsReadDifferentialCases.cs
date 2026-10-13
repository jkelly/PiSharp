// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/tools-read-differential.test.ts, as tools.read-differential.<case>.
// The reference is upstream's: the read tool before bounded reads, which decodes the whole file, splits it into lines and truncates the
// selection; the tool must give the same result while reading only a bounded part of the file. Offsets and limits are JavaScript numbers,
// NaN included, so the cases call the tool's typed entry (ReadTool.ExecuteAsync) where upstream calls execute() with an object.
// Added: Fixtures/tools.upstream.json (Fixtures/capture-tools.mjs) records a digest of upstream's own outcome for every seed and trial, and
// each case also matches those.
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal.PiAi.Validation;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;

internal static partial class Program
{
    private static readonly byte[][] ReadPieces =
    [
        [0x0a], [0x0a], [0x61], [0x62, 0x63, 0x64, 0x65], [0xef, 0xbb, 0xbf], [0xc3, 0xa9], [0xe2, 0x82, 0xac], [0xf0, 0x9f, 0x98, 0x80],
        [0xe2, 0x82], [0xff], [0x0d, 0x0a],
    ];

    private static readonly double?[] ReadOffsets = [null, 0, 1, 2, 3, 2.5, -4, 50, 2001, 3000, 1e20, double.NaN];
    private static readonly double?[] ReadLimits = [null, 0, 1, 2, -3, -1e20, 1.5, 7, 2000, 2500, 1e20, double.NaN];
    private static readonly string[] TruncationKeys =
        ["truncated", "truncatedBy", "totalLines", "totalBytes", "outputLines", "outputBytes", "lastLinePartial", "firstLineExceedsLimit", "maxLines", "maxBytes"];

    /// <summary>Mostly small files, some over the line limit, some over the byte limit, some with one huge line.</summary>
    private static byte[] ReadRandomFile(Func<double> next)
    {
        var kind = next();
        if (kind < 0.1) return [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("x\n", 2100)))];
        if (kind < 0.2)
        {
            var line = new byte[Truncation.DefaultMaxBytes + 200 + (int)Math.Floor(next() * 400)];
            Array.Fill(line, (byte)0x61);
            line[Truncation.DefaultMaxBytes - 1 + (int)Math.Floor(next() * 3)] = 0xc3;
            return [.. line, 0x0a, 0x62];
        }
        if (kind < 0.3) return Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(string.Concat(Enumerable.Repeat("0123456789", 30)) + "\n", 200)));
        var bytes = new List<byte>();
        var pieces = (int)Math.Floor(next() * 80);
        for (var index = 0; index < pieces; index++) bytes.AddRange(ReadPieces[(int)Math.Floor(next() * ReadPieces.Length)]);
        return [.. bytes];
    }

    /// <summary>JavaScript's <c>Array.prototype.slice(start, end)</c> on a list.</summary>
    private static List<string> JsSlice(List<string> items, double start, double? end)
    {
        static double Relative(double value, int length)
        {
            var integer = double.IsNaN(value) ? 0 : Math.Truncate(value);
            return integer < 0 ? Math.Max(length + integer, 0) : Math.Min(integer, length);
        }
        var from = (int)Relative(start, items.Count);
        var to = end is { } last ? (int)Relative(last, items.Count) : items.Count;
        return to > from ? items.GetRange(from, to - from) : [];
    }

    /// <summary>The reference: upstream's read before bounded reads.</summary>
    private static ToolExecutionResult ReferenceRead(byte[] bytes, string path, double? offset, double? limit)
    {
        if (ImageDetection.DetectSupportedImageMimeType(bytes) is { } mimeType)
            return new ToolExecutionResult
            {
                Content = JsonArrayValue.Empty,
                IsError = true,
                Diagnostics = [new ToolDiagnostic(ToolDiagnosticSeverity.Error, $"{path} is an image ({mimeType}); reading images is not supported",
                    "unsupported_image")],
            };
        var textContent = TextDecoderDecode(bytes);
        var allLines = textContent.Split('\n').ToList();
        double totalFileLines = allLines.Count;
        var startLine = offset is { } given && given != 0 && !double.IsNaN(given) ? Math.Max(0, given - 1) : 0;
        var startLineDisplay = startLine + 1;
        if (startLine >= allLines.Count)
            throw new InvalidOperationException($"Offset {Js.NumberToString(offset ?? double.NaN)} is beyond end of file ({allLines.Count} lines total)");
        string selectedContent;
        double? userLimitedLines = null;
        if (limit is { } lineLimit)
        {
            var endLine = Math.Min(startLine + lineLimit, allLines.Count);
            selectedContent = string.Join("\n", JsSlice(allLines, startLine, endLine));
            userLimitedLines = endLine - startLine;
        }
        else selectedContent = string.Join("\n", JsSlice(allLines, startLine, null));
        var truncation = Truncation.TruncateHead(selectedContent);
        var diagnostics = ImmutableArray.CreateBuilder<ToolDiagnostic>();
        var outputText = truncation.Content;
        JsonValue? details = null;
        if (truncation.FirstLineExceedsLimit)
        {
            // allLines[startLine]: a fractional index names no element, and TextEncoder encodes undefined as "".
            var line = Math.Truncate(startLine) == startLine ? allLines[(int)startLine] : "";
            var lineBytes = Encoding.UTF8.GetBytes(line);
            var end = ToolOutput.CharacterEnd(lineBytes, Truncation.DefaultMaxBytes);
            outputText = TextDecoderDecode(lineBytes[..end]);
            var display = Js.NumberToString(startLineDisplay);
            diagnostics.Add(new ToolDiagnostic(ToolDiagnosticSeverity.Warn,
                $"Line {display} is {Truncation.FormatSize(lineBytes.Length)}, exceeds the {Truncation.FormatSize(Truncation.DefaultMaxBytes)} limit; showing its first {Truncation.FormatSize(end)}. Use bash: sed -n '{display}p' {path} | tail -c +{end + 1}",
                "truncated"));
            details = JsonValue.Object([new("truncation", ReadTool.TruncationJson(truncation with { OutputBytes = end, OutputLines = 1 }))]);
        }
        else if (truncation.Truncated)
        {
            var endLineDisplay = startLineDisplay + truncation.OutputLines - 1;
            var limitText = truncation.TruncatedBy == TruncationLimit.Lines ? "" : $" ({Truncation.FormatSize(Truncation.DefaultMaxBytes)} limit)";
            diagnostics.Add(new ToolDiagnostic(ToolDiagnosticSeverity.Info,
                $"Showing lines {Js.NumberToString(startLineDisplay)}-{Js.NumberToString(endLineDisplay)} of {Js.NumberToString(totalFileLines)}{limitText}. Use offset={Js.NumberToString(endLineDisplay + 1)} to continue.",
                "truncated"));
            details = JsonValue.Object([new("truncation", ReadTool.TruncationJson(truncation))]);
        }
        else if (userLimitedLines is { } limited && startLine + limited < allLines.Count)
        {
            diagnostics.Add(new ToolDiagnostic(ToolDiagnosticSeverity.Info,
                $"{Js.NumberToString(allLines.Count - (startLine + limited))} more lines in file. Use offset={Js.NumberToString(startLine + limited + 1)} to continue."));
        }
        return new ToolExecutionResult
        {
            Content = outputText.Length == 0 ? JsonArrayValue.Empty
                : JsonValue.Array(JsonValue.Object([new("type", "text"), new("text", outputText)])),
            Details = details,
            Diagnostics = diagnostics.ToImmutable(),
        };
    }

    /// <summary><c>new TextDecoder().decode(bytes)</c>: replacement characters, a leading byte-order mark dropped.</summary>
    private static string TextDecoderDecode(byte[] bytes)
    {
        var text = new UTF8Encoding(false, false).GetString(bytes);
        return text.StartsWith('\uFEFF') ? text[1..] : text;
    }

    private static async Task<string> ReadOutcome(Func<Task<ToolExecutionResult>> run)
    {
        ToolExecutionResult result;
        try { result = await run(); }
        catch (Exception error) { return "error\0" + error.Message; }
        var content = result.Content ?? JsonArrayValue.Empty;
        var parts = new List<string> { "result", result.IsError == true ? "true" : "undefined", content.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        foreach (var part in content.Cast<JsonObjectValue>()) parts.AddRange([((JsonStringValue)part["type"]).Value, ((JsonStringValue)part["text"]).Value]);
        if (result.Details is not JsonObjectValue details) parts.Add("no-details");
        else
        {
            var truncation = (JsonObjectValue)details["truncation"];
            parts.AddRange(["details", string.Join(",", details.Keys), string.Join(",", truncation.Keys)]);
            foreach (var key in TruncationKeys)
                parts.Add(truncation[key] switch
                {
                    JsonNumberValue number => Js.NumberToString(number.Value),
                    JsonStringValue text => text.Value,
                    var other => other.ToJson(),
                });
        }
        var diagnostics = result.Diagnostics ?? [];
        parts.Add(diagnostics.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var diagnostic in diagnostics)
            parts.AddRange([diagnostic.Severity.ToString().ToLowerInvariant(), diagnostic.Code ?? "undefined", diagnostic.Message]);
        return string.Join("\0", parts);
    }

    private static string JsArg(double? value) => value is { } number ? Js.NumberToString(number) : "undefined";

    private static JsonElement ToolsFixture() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", "tools.upstream.json"))).RootElement;

    private static IEnumerable<(string, Func<Task>)> ToolsReadDifferentialCases()
    {
        yield return Case("tools.read-differential.returns-exactly-what-reading-the-whole-file-returned", () => WithToolDir(async cwd =>
        {
            var fixture = ToolsFixture().GetProperty("read");
            var env = new LocalExecutionEnv(cwd);
            for (var seed = 1; seed <= 400; seed++)
            {
                var upstream = fixture[seed - 1];
                Equal(seed, upstream.GetProperty("seed").GetInt32(), "fixture seed");
                var next = LineScanRandom(seed);
                var file = ReadRandomFile(next);
                Equal(upstream.GetProperty("file").GetString(), LineScanDigest(Convert.ToHexStringLower(file)), $"seed {seed} file");
                await File.WriteAllBytesAsync(Path.Combine(cwd, "f.txt"), file);
                for (var trial = 0; trial < 4; trial++)
                {
                    var offset = ReadOffsets[(int)Math.Floor(next() * ReadOffsets.Length)];
                    var limit = ReadLimits[(int)Math.Floor(next() * ReadLimits.Length)];
                    var actual = await ReadOutcome(() => ReadTool.ExecuteAsync("f.txt", offset, limit, new FakeToolApi(env), ToolContext));
                    var expected = await ReadOutcome(() => Task.FromResult(ReferenceRead(file, "f.txt", offset, limit)));
                    var what = $"seed {seed} offset {JsArg(offset)} limit {JsArg(limit)}";
                    Equal(expected, actual, what);
                    Equal(upstream.GetProperty("trials")[trial].GetString(), LineScanDigest($"{JsArg(offset)}\0{JsArg(limit)}\0{actual}"), what + " (upstream)");
                }
            }
        }));

        yield return Case("tools.read-differential.detects-animated-pngs-whose-actl-chunk-lies-far-beyond-the-header", () => WithToolDir(async cwd =>
        {
            static byte[] Chunk(string type, int length) =>
                [(byte)(length >>> 24), (byte)(length >>> 16), (byte)(length >>> 8), (byte)length, .. Encoding.ASCII.GetBytes(type), .. new byte[length + 4]];
            byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, .. Chunk("IHDR", 13), .. Chunk("iCCP", 200_000), .. Chunk("acTL", 8), .. Chunk("IDAT", 10)];
            await File.WriteAllBytesAsync(Path.Combine(cwd, "a.png"), png);
            var env = new LocalExecutionEnv(cwd);
            var actual = await ReadOutcome(() => CodingTools.CreateReadTool().Execute(JsonValue.Object([new("path", "a.png")]), new FakeToolApi(env), ToolContext));
            Equal(await ReadOutcome(() => Task.FromResult(ReferenceRead(png, "a.png", null, null))), actual, "outcome");
            Check(!actual.Contains("is an image", StringComparison.Ordinal), "an animated PNG is not a supported image");
        }));
    }
}
