using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Agent.Tools;

internal static class ToolOutputTruncatorTests
{
    private const string SourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f";
    private const string InputHash = "c573c8413bfa1c78a872d17672d5f0a861ce2637b05055e6fa11c5f87f2f5c04";
    private const string CaptureHash = "8c088a8230dcf627221c2b3d3f22b2c7dc4aee2f3bd9e8b434f3c13b2124496d";
    private const string ProvenanceHash = "9ae1f097849764c7b1908f75d9a854be08ee8cf29bbc66d7dc669fa3a3944417";
    public static object? DifferentialEvidence { get; private set; }

    public static Task FrozenReferenceCorpus()
    {
        var root = FindRoot();
        var inputBytes = ReadPinned(root, "core.input.json", InputHash);
        var expectedBytes = ReadPinned(root, "core.expected.json", CaptureHash);
        var first = ReadPinned(root, "core.capture-1.json", CaptureHash);
        var second = ReadPinned(root, "core.capture-2.json", CaptureHash);
        Check(expectedBytes.AsSpan().SequenceEqual(first) && first.AsSpan().SequenceEqual(second), "Frozen captures differ.");
        using var input = JsonDocument.Parse(inputBytes);
        using var expected = JsonDocument.Parse(expectedBytes);
        using var provenance = JsonDocument.Parse(ReadPinned(root, "core.provenance.json", ProvenanceHash));
        Equal(SourceSha, input.RootElement.GetProperty("sourceSha").GetString());
        Equal(SourceSha, expected.RootElement.GetProperty("sourceSha").GetString());
        Equal(SourceSha, provenance.RootElement.GetProperty("sourceSha").GetString());
        Equal(InputHash, expected.RootElement.GetProperty("inputSha256").GetString());
        Equal(InputHash, provenance.RootElement.GetProperty("input").GetProperty("sha256").GetString());
        Equal("PiSharp-authored synthetic boundary corpus", input.RootElement.GetProperty("inputOrigin").GetString());
        Equal("v24.19.0", provenance.RootElement.GetProperty("runtime").GetProperty("version").GetString());
        Equal(2, provenance.RootElement.GetProperty("independentCaptureProcesses").GetInt32());
        Check(provenance.RootElement.GetProperty("capturesByteIdentical").GetBoolean(), "Reference captures were not identical.");
        Equal(0, provenance.RootElement.GetProperty("normalization").GetArrayLength());
        var runner = provenance.RootElement.GetProperty("runner");
        // Original execution identity and current public derivative integrity are separate.
        Equal("0f9899a8c01813e8cf13265d11f45bd6693da0e5fc52fd9d901ab910b54a4e6a", runner.GetProperty("sha256").GetString());
        Equal("f3ad85609e0400d4750a2ce27a8fa27aa1bd150fd8905d6cc6bfae26e46c185f", Hash(File.ReadAllBytes(Path.Combine(root, runner.GetProperty("path").GetString()!))));
        var closure = provenance.RootElement.GetProperty("dependencyClosure");
        Equal(1, closure.GetProperty("sourceModules").GetArrayLength());
        Equal(0, closure.GetProperty("sourceImports").GetArrayLength());
        Equal(0, closure.GetProperty("externalPackages").GetArrayLength());
        Equal(0, closure.GetProperty("sourcePatches").GetArrayLength());
        Equal(0, closure.GetProperty("resolutionShims").GetArrayLength());
        Check(!closure.GetProperty("copiedOrExtractedSource").GetBoolean(), "Reference contains copied source.");
        foreach (var phase in new[] { "before", "after" })
        {
            var sourceCheck = provenance.RootElement.GetProperty("sourceChecks").GetProperty(phase);
            Equal(SourceSha, sourceCheck.GetProperty("revision").GetString());
            Equal(string.Empty, sourceCheck.GetProperty("status").GetString());
            Equal("8e4507c3ed7ca7548cf7c2d7f77d07f2ae63a38b756789cbb44e9062db6c8762", sourceCheck.GetProperty("sourceSha256").GetString());
        }
        foreach (var capture in provenance.RootElement.GetProperty("captures").EnumerateArray())
        {
            Equal(CaptureHash, capture.GetProperty("sha256").GetString());
            Equal(498500, capture.GetProperty("bytes").GetInt32());
        }

        var cases = input.RootElement.GetProperty("cases");
        var observations = expected.RootElement.GetProperty("observations");
        var results = observations.GetProperty("cases");
        Equal(124, cases.GetArrayLength());
        Equal(cases.GetArrayLength(), results.GetArrayLength());
        Equal(124, observations.GetProperty("checks").GetProperty("capturedCaseCount").GetInt32());
        Equal(248, observations.GetProperty("checks").GetProperty("capturedResultCount").GetInt32());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < cases.GetArrayLength(); index++)
        {
            var test = cases[index];
            var observed = results[index];
            var caseId = test.GetProperty("caseId").GetString()!;
            Check(ids.Add(caseId), "Duplicate input case ID.");
            Equal(caseId, observed.GetProperty("caseId").GetString());
            Equal(test.GetProperty("category").GetString(), observed.GetProperty("category").GetString());
            var content = ReadExactString(test.GetProperty("content"));
            var options = test.TryGetProperty("options", out var value)
                ? new ToolOutputTruncationOptions(Budget(value, "maxLines", 2000), Budget(value, "maxBytes", 51200)) : null;
            Compare(observed.GetProperty("head"), ToolOutputTruncator.Head(content, options), caseId + ":head");
            Compare(observed.GetProperty("tail"), ToolOutputTruncator.Tail(content, options), caseId + ":tail");
        }
        DifferentialEvidence = new
        {
            scope = "pure-head-tail-tool-output-truncation", sourceSha = SourceSha,
            inputSha256 = InputHash, expectedSha256 = CaptureHash, provenanceSha256 = ProvenanceHash,
            authoredCaseCount = cases.GetArrayLength(), matchedResultCount = cases.GetArrayLength() * 2,
            comparedFieldsPerResult = 11, independentReferenceCaptures = 2, capturesByteIdentical = true,
            normalization = Array.Empty<string>(), toolEffectsImplemented = false, phaseAcceptanceClaimed = false
        };
        return Task.CompletedTask;
    }

    public static Task NativeInputPolicy()
    {
        foreach (var truncate in new Func<string, ToolOutputTruncationOptions?, ToolOutputTruncationResult>[]
            { ToolOutputTruncator.Head, ToolOutputTruncator.Tail })
        {
            Throws<ArgumentNullException>(() => truncate(null!, null));
            Throws<ArgumentOutOfRangeException>(() => truncate(string.Empty, new(MaxLines: -1)));
            Throws<ArgumentOutOfRangeException>(() => truncate(string.Empty, new(MaxBytes: -1)));
            Equal(string.Empty, truncate(string.Empty, new(0, 0)).Content);
            Equal("\0", truncate("\0", new(1, 1)).Content);
        }
        // Native strings and JS strings both admit unpaired UTF-16. Preserve exact code units in complete lines.
        const string malformed = "\ud800x\udc00";
        Equal(malformed, ToolOutputTruncator.Head(malformed).Content);
        Equal(7, ToolOutputTruncator.Head(malformed).TotalBytes);
        Equal("x\ufffd", ToolOutputTruncator.Tail(malformed, new(MaxBytes: 4)).Content);
        Equal("\ud800", ToolOutputTruncator.Head("\ud800\nend", new(MaxLines: 1)).Content);
        using var escaped = JsonDocument.Parse("\"\\ud800\\u0078\\udc00\"");
        Equal(malformed, ReadExactString(escaped.RootElement));
        using var escapedControls = JsonDocument.Parse("\"\\\"\\\\\\/\\b\\f\\n\\r\\t\"");
        Equal("\"\\/\b\f\n\r\t", ReadExactString(escapedControls.RootElement));
        return Task.CompletedTask;
    }

    private static void Compare(JsonElement expected, ToolOutputTruncationResult actual, string caseId)
    {
        try
        {
            Equal(11, expected.EnumerateObject().Count());
            Equal(ReadExactString(expected.GetProperty("content")), actual.Content);
            Equal(expected.GetProperty("truncated").GetBoolean(), actual.Truncated);
            Equal(expected.GetProperty("truncatedBy").GetString(), actual.TruncatedBy switch
            { ToolOutputTruncationLimit.Lines => "lines", ToolOutputTruncationLimit.Bytes => "bytes", null => null,
                _ => throw new InvalidOperationException("Unknown truncation limit.") });
            Equal(expected.GetProperty("totalLines").GetInt32(), actual.TotalLines);
            Equal(expected.GetProperty("totalBytes").GetInt32(), actual.TotalBytes);
            Equal(expected.GetProperty("outputLines").GetInt32(), actual.OutputLines);
            Equal(expected.GetProperty("outputBytes").GetInt32(), actual.OutputBytes);
            Equal(expected.GetProperty("lastLinePartial").GetBoolean(), actual.LastLinePartial);
            Equal(expected.GetProperty("firstLineExceedsLimit").GetBoolean(), actual.FirstLineExceedsLimit);
            Equal(expected.GetProperty("maxLines").GetInt32(), actual.MaxLines);
            Equal(expected.GetProperty("maxBytes").GetInt32(), actual.MaxBytes);
        }
        catch (Exception error) { throw new InvalidOperationException($"Tool-output reference mismatch in {caseId}.", error); }
    }

    private static int Budget(JsonElement options, string name, int fallback) =>
        options.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetInt32() : fallback;

    // JsonElement.GetString rejects escaped lone surrogates. These frozen JSON string tokens are read losslessly
    // so a replacement by either the decoder or the implementation cannot hide a UTF-16 mismatch.
    private static string ReadExactString(JsonElement value)
    {
        Equal(JsonValueKind.String, value.ValueKind);
        var raw = value.GetRawText();
        var result = new StringBuilder(raw.Length - 2);
        for (var index = 1; index < raw.Length - 1; index++)
        {
            if (raw[index] != '\\') { result.Append(raw[index]); continue; }
            index++;
            if (raw[index] == 'u')
            {
                result.Append((char)int.Parse(raw.AsSpan(index + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                index += 4;
            }
            else result.Append(raw[index] switch
            { '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t',
                _ => throw new InvalidOperationException("Unexpected JSON string escape.") });
        }
        return result.ToString();
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "fixtures", "pi-v0.99.1", "tool-truncation")))
            directory = directory.Parent;
        Check(directory is not null, "Frozen truncation fixture directory unavailable.");
        return directory!.FullName;
    }
    private static byte[] ReadPinned(string root, string name, string hash)
    {
        var bytes = File.ReadAllBytes(Path.Combine(root, "fixtures", "pi-v0.99.1", "tool-truncation", name));
        Equal(hash, Hash(bytes));
        return bytes;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
}
