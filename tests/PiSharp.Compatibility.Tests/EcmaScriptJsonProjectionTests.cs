using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

internal static class EcmaScriptJsonProjectionTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string repo)
    {
        yield return ("ecmascript-json.genuine-node-builtin-complete-exact-17984-cases", () => Corpus(repo));
        yield return ("ecmascript-json.authored-number-rounding-shortest-and-thresholds", Numbers);
        yield return ("ecmascript-json.string-quoting-index-keys-duplicates-and-array-order", StringsAndOrdering);
        yield return ("ecmascript-json.owned-wide-nul-unknown-null-presence-remains-unchanged", Ownership);
        yield return ("ecmascript-json.strict-grammar-bounded-resources-and-cancellation", AdmissionAndLimits);
    }
    private static Task Corpus(string repo)
    {
        var inputEncoded = Pinned(repo, "fixtures/reference/ecmascript-json-boundary/input.json.gz", 8 * 1024 * 1024,
            "ae3bd38e3c5992e3ae3dfd11c9aca3420720acb3ed67402c95cb87fda1c37e64");
        var expectedEncoded = Pinned(repo, "fixtures/reference/ecmascript-json-boundary/expected.json.gz", 8 * 1024 * 1024,
            "4980e8158215b0f27b47abe7070e72566ade1774ea809f003bc21d24d7230deb");
        var lockBytes = Pinned(repo, "tools/ReferenceOracle/ecmascript-json-boundary.lock.json", 65_536,
            "38fd98a0785f4407a8db02397da0d97fc6627ccc675898b985e02b901a927f2c");
        Equal(1_237_354, inputEncoded.Length, "exact encoded input bytes"); Equal(1_405_918, expectedEncoded.Length, "exact encoded expected bytes");
        var inputBytes = Unpack(inputEncoded, 7_609_961, 10 * 1024 * 1024,
            "a3fcdfbcba4da5f9412027ced81e54ebbbc906a3488cd60a9de60429b6acea52");
        var expectedBytes = Unpack(expectedEncoded, 11_206_543, 16 * 1024 * 1024,
            "a98384918bb7e1abf8ded0c1e2d1448ac186292d32f013a85fd15c5adee27027");
        // Encoded identities are admitted before decompression, decoded identities before strict bounded parsing.
        var jsonOptions = new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false };
        using var input = JsonDocument.Parse(inputBytes, jsonOptions); using var expected = JsonDocument.Parse(expectedBytes, jsonOptions); using var receipt = JsonDocument.Parse(lockBytes, jsonOptions);
        Equal("v24.19.0", receipt.RootElement.GetProperty("runtime").GetProperty("version").GetString()!, "qualified builtin runtime");
        Check(receipt.RootElement.GetProperty("observations").GetProperty("builtinOnly").GetBoolean(), "Receipt is not builtin-only.");
        var cases = input.RootElement.GetProperty("cases"); var observations = expected.RootElement.GetProperty("cases");
        Equal(17_984, cases.GetArrayLength(), "complete authored cases"); Equal(cases.GetArrayLength(), observations.GetArrayLength(), "complete captured cases");
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < cases.GetArrayLength(); index++)
        {
            var row = cases[index]; var observation = observations[index];
            var id = row.GetProperty("caseId").GetString()!; var raw = row.GetProperty("json").GetString()!;
            Equal(id, observation.GetProperty("caseId").GetString()!, "case order"); Equal(raw, observation.GetProperty("json").GetString()!, id + " retained source input");
            Check(raw.Length <= 4_096, "Captured case escaped its authored limit.");
            var projected = EcmaScriptJsonProjection.Project(raw);
            Equal(observation.GetProperty("serialized").GetString()!, projected, id);
            Check(actual.TryAdd(id, projected), "Duplicate corpus identity.");
        }
        var numericSemantics = expected.RootElement.GetProperty("numericSemantics");
        Equal(17_920, numericSemantics.GetArrayLength(), "complete observed scalar-number sidecars");
        foreach (var observation in numericSemantics.EnumerateArray())
        {
            var id = observation.GetProperty("caseId").GetString()!; var serialized = actual[id];
            if (observation.GetProperty("nonFinite").ValueKind != JsonValueKind.Null)
                Equal("null", serialized, id + " nonfinite serialization");
            else if (observation.GetProperty("negativeZero").GetBoolean())
                Equal("0", serialized, id + " negative-zero serialization");
            else
            {
                // An independent framework parser checks the represented finite output against the genuine
                // DataView receipt. Production parsing/formatting does not depend on that parser.
                var number = double.Parse(serialized, NumberStyles.Float, CultureInfo.InvariantCulture);
                Equal(observation.GetProperty("binary64Hex").GetString()!, BitConverter.DoubleToUInt64Bits(number).ToString("x16", CultureInfo.InvariantCulture), id + " represented binary64");
            }
        }
        // Ordinal string comparison intentionally distinguishes formatting and key order that reparsing can hide.
        Check(!string.Equals("1.0", "1", StringComparison.Ordinal) && !string.Equals("{\"2\":0,\"a\":1}", "{\"a\":1,\"2\":0}", StringComparison.Ordinal), "Comparison erased lexical differences.");
        return Task.CompletedTask;
    }
    private static byte[] Pinned(string repo, string relativePath, int maximumBytes, string sha256)
    {
        using var input = new FileStream(Path.Combine(repo, relativePath), FileMode.Open, FileAccess.Read, FileShare.Read);
        Check(input.Length <= maximumBytes, "Frozen fixture resource bound changed.");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        Check(input.ReadByte() == -1, "Frozen fixture grew while reading.");
        Equal(sha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "Frozen fixture hash " + relativePath);
        return bytes;
    }
    private static byte[] Unpack(byte[] encoded, int exactDecodedBytes, int maximumDecodedBytes, string decodedSha256)
    {
        Check(exactDecodedBytes >= 0 && exactDecodedBytes <= maximumDecodedBytes, "Frozen decoded fixture bound changed.");
        // Capacity is a pinned native constant, never gzip ISIZE or an untrusted decompression estimate.
        var decoded = new byte[exactDecodedBytes];
        using var source = new MemoryStream(encoded, writable: false);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        var read = 0;
        while (read < decoded.Length)
        {
            var count = gzip.Read(decoded.AsSpan(read, Math.Min(8_192, decoded.Length - read)));
            Check(count > 0, "Frozen fixture decoded fewer bytes than its exact receipt.");
            read += count;
        }
        Check(gzip.ReadByte() == -1, "Frozen fixture decoded more bytes than its exact receipt.");
        Equal(decodedSha256, Convert.ToHexString(SHA256.HashData(decoded)).ToLowerInvariant(), "Frozen decoded fixture hash");
        // Generic GZipStream may accept concatenated members/trailing bytes. Exact encoded SHA admission
        // above binds the independently verified single-member container; this is not a generic gzip validator.
        return decoded;
    }
    private static Task Numbers()
    {
        // Independently authored ECMA-linked expectations, not a runtime capture.
        var cases = new (string Input, string Output)[]
        {
            ("1.0", "1"), ("1e+0", "1"), ("-0", "0"), ("-0.0e20", "0"),
            ("1e-6", "0.000001"), ("1e-7", "1e-7"), ("1e20", "100000000000000000000"), ("1e21", "1e+21"),
            ("-1e-6", "-0.000001"), ("-1e21", "-1e+21"),
            ("9007199254740993", "9007199254740992"), ("9007199254740995", "9007199254740996"),
            ("-9007199254740993", "-9007199254740992"), ("1000000000000000128", "1000000000000000100"),
            ("1e400", "null"), ("-1e400", "null"), ("1e-400", "0"), ("-1e-400", "0"),
            ("5e-324", "5e-324"), ("2.4703282292062327e-324", "0"), ("2.4703282292062328e-324", "5e-324"),
            ("2.2250738585072014e-308", "2.2250738585072014e-308"),
            ("1.7976931348623157e308", "1.7976931348623157e+308"), ("1.7976931348623159e308", "null"),
            ("0.1000000000000000055511151231257827021181583404541015625", "0.1"),
            ("1.00000000000000011102230246251565404236316680908203125", "1"),
            ("1.00000000000000033306690738754696212708950042724609375", "1.0000000000000004"),
            ("0." + new string('0', 54) + "1", "1e-55")
        };
        foreach (var row in cases)
        {
            Equal(row.Output, EcmaScriptJsonProjection.Project(row.Input), row.Input);
            Equal(row.Output, EcmaScriptJsonProjection.Project(row.Output), "idempotent " + row.Input);
        }
        var recursive = "{\"usage\":[1.0,9007199254740993,-0,1e400,{\"cost\":1e-7}],\"null\":null}";
        Equal("{\"usage\":[1,9007199254740992,0,null,{\"cost\":1e-7}],\"null\":null}", EcmaScriptJsonProjection.Project(recursive), "recursive numeric projection");
        return Task.CompletedTask;
    }
    private static Task StringsAndOrdering()
    {
        Equal("{\"0\":6,\"2\":2,\"10\":1,\"4294967294\":4,\"z\":0,\"01\":3,\"4294967295\":5,\"-0\":7,\"a\":8}",
            EcmaScriptJsonProjection.Project("{\"z\":0,\"10\":1,\"2\":2,\"01\":3,\"4294967294\":4,\"4294967295\":5,\"0\":6,\"-0\":7,\"a\":8}"), "array-index key order");
        Equal("{\"0\":4,\"z\":3,\"a\":5}", EcmaScriptJsonProjection.Project("{\"z\":1,\"a\":2,\"\\u007a\":3,\"0\":4,\"a\":5}"), "decoded duplicates last value, first creation");
        Equal("{\"__proto__\":{\"inert\":true},\"constructor\":null,\"toJSON\":false}",
            EcmaScriptJsonProjection.Project("{\"__proto__\":{\"inert\":true},\"constructor\":null,\"toJSON\":false}"), "ordinary own properties");
        Equal("[3,1,{\"1\":true,\"b\":false},null]", EcmaScriptJsonProjection.Project("[3,1,{\"b\":false,\"1\":true},null]"), "array order and nested key order");
        var quoted = "\"\\u0000\\b\\t\\n\\f\\r\\u0001\\u001f\\\"\\\\/π😀\u2028\u2029<>&\"";
        Equal(quoted, EcmaScriptJsonProjection.Project("\"\\u0000\\u0008\\u0009\\u000a\\u000c\\u000d\\u0001\\u001f\\u0022\\u005c\\/\\u03c0\\ud83d\\ude00\\u2028\\u2029<>&\""), "ECMA quote grammar");
        Equal("\"\\ud800x\\udc00\"", EcmaScriptJsonProjection.Project("\"\\uD800x\\uDC00\""), "well-formed lone surrogate escapes");
        Equal("\"😀\"", EcmaScriptJsonProjection.Project("\"\\ud83d\\ude00\""), "paired surrogate code point");
        Equal("{\"\\ud800\":\"\\udc00\"}", EcmaScriptJsonProjection.Project("{\"\\ud800\":\"\\udc00\"}"), "surrogate property names");
        Equal("\"\\ud800\"", EcmaScriptJsonProjection.Project("\"" + '\ud800' + "\""), "raw UTF-16 JSON string code unit");
        Equal("{\"nul\\u0000\":null}", EcmaScriptJsonProjection.Project("{\"nul\\u0000\":null}"), "NUL key presence");
        return Task.CompletedTask;
    }
    private static Task Ownership()
    {
        const string raw = "{ \"future\":{\"wide\":9007199254740993,\"negativeZero\":-0,\"overflow\":1e400,\"nil\":null,\"signature\":\"\\u0000\\u03c0\"},\"usage\":{\"cost\":1.0},\"content\":[] }";
        var owned = JsonData.Parse(raw); var before = owned.ToString();
        var projected = EcmaScriptJsonProjection.Project(owned);
        Equal("{\"future\":{\"wide\":9007199254740992,\"negativeZero\":0,\"overflow\":null,\"nil\":null,\"signature\":\"\\u0000π\"},\"usage\":{\"cost\":1},\"content\":[]}", projected, "explicit lossy compatibility boundary");
        Equal(before, owned.ToString(), "original owned syntax"); Equal(raw, before, "native retained syntax");
        Equal("9007199254740993", owned.Value.GetProperty("future").GetProperty("wide").GetRawText(), "native wide integer");
        Equal("-0", owned.Value.GetProperty("future").GetProperty("negativeZero").GetRawText(), "native signed zero");
        Equal("1e400", owned.Value.GetProperty("future").GetProperty("overflow").GetRawText(), "native overflow token");
        Check(!owned.Value.TryGetProperty("missing", out _) && owned.Value.GetProperty("future").GetProperty("nil").ValueKind == System.Text.Json.JsonValueKind.Null,
            "Projection changed absent/null native ownership.");
        Equal(projected, EcmaScriptJsonProjection.Project(owned), "independent repeated projection");
        return Task.CompletedTask;
    }
    private static Task AdmissionAndLimits()
    {
        foreach (var raw in new[] { "", " ", "01", "+1", "1.", "1e", "--1", "NaN", "Infinity", "undefined", "{\"x\":1,}", "[1,]", "{ /*comment*/ }", "\uFEFF{}", "\"\\x00\"", "\"raw\nLF\"", "true false", "{\"x\" 1}" })
            Failure(EcmaScriptJsonProjectionFailure.InvalidJson, () => EcmaScriptJsonProjection.Project(raw));
        Equal("null", EcmaScriptJsonProjection.Project("null", new(MaximumInputCharacters: 4, MaximumInputBytes: 4, MaximumOutputCharacters: 4, MaximumOutputBytes: 4)), "exact input/output bounds");
        Failure(EcmaScriptJsonProjectionFailure.ResourceLimit, () => EcmaScriptJsonProjection.Project("null", new(MaximumInputCharacters: 3)));
        Failure(EcmaScriptJsonProjectionFailure.ResourceLimit, () => EcmaScriptJsonProjection.Project("null", new(MaximumInputBytes: 3)));
        Equal("\"π\"", EcmaScriptJsonProjection.Project("\"π\"", new(MaximumInputCharacters: 3, MaximumInputBytes: 4, MaximumOutputCharacters: 3, MaximumOutputBytes: 4)), "UTF-8 budgets");
        Failure(EcmaScriptJsonProjectionFailure.ResourceLimit, () => EcmaScriptJsonProjection.Project("\"π\"", new(MaximumInputBytes: 3)));
        Failure(EcmaScriptJsonProjectionFailure.ResourceLimit, () => EcmaScriptJsonProjection.Project("\"π\"", new(MaximumOutputBytes: 3)));
        Failure(EcmaScriptJsonProjectionFailure.ResourceLimit, () => EcmaScriptJsonProjection.Project("\"π\"", new(MaximumOutputCharacters: 2)));
        Equal("[[0]]", EcmaScriptJsonProjection.Project("[[0]]", new(MaximumDepth: 2, MaximumNodes: 3)), "exact depth/nodes");
        Failure(EcmaScriptJsonProjectionFailure.ResourceLimit, () => EcmaScriptJsonProjection.Project("[[0]]", new(MaximumDepth: 1)));
        Failure(EcmaScriptJsonProjectionFailure.ResourceLimit, () => EcmaScriptJsonProjection.Project("[[0]]", new(MaximumNodes: 2)));
        Equal("{\"x\":2}", EcmaScriptJsonProjection.Project("{\"x\":1,\"x\":2}", new(MaximumPropertiesPerObject: 2)), "duplicate occurrences charged");
        Failure(EcmaScriptJsonProjectionFailure.ResourceLimit, () => EcmaScriptJsonProjection.Project("{\"x\":1,\"x\":2}", new(MaximumPropertiesPerObject: 1)));
        Equal("[1,22]", EcmaScriptJsonProjection.Project("[1,22]", new(MaximumNumbers: 2, MaximumTotalNumberCharacters: 3)), "exact cumulative numeric budget");
        Failure(EcmaScriptJsonProjectionFailure.ResourceLimit, () => EcmaScriptJsonProjection.Project("[1,22]", new(MaximumNumbers: 1)));
        Failure(EcmaScriptJsonProjectionFailure.ResourceLimit, () => EcmaScriptJsonProjection.Project("[1,222]", new(MaximumTotalNumberCharacters: 3)));
        Failure(EcmaScriptJsonProjectionFailure.ResourceLimit, () => EcmaScriptJsonProjection.Project("1e400", new(MaximumNumberCharacters: 4)));
        Equal("null", EcmaScriptJsonProjection.Project("1e400", new(MaximumNumberCharacters: 5)), "overflow is projection, not invalid native number");
        Failure(EcmaScriptJsonProjectionFailure.ResourceLimit, () => EcmaScriptJsonProjection.Project("1e400", new(MaximumOutputCharacters: 3)));
        Failure(EcmaScriptJsonProjectionFailure.ResourceLimit, () => EcmaScriptJsonProjection.Project("\"\\u0000\"", new(MaximumOutputCharacters: 7)));
        Failure(EcmaScriptJsonProjectionFailure.ResourceLimit, () => EcmaScriptJsonProjection.Project("\"ab\"", new(MaximumStringCharacters: 1)));
        Throws<ArgumentOutOfRangeException>(() => EcmaScriptJsonProjection.Project("{}", new(MaximumDepth: 65)));
        Throws<ArgumentOutOfRangeException>(() => EcmaScriptJsonProjection.Project("{}", new(MaximumNumberCharacters: 16_385)));
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var owned = JsonData.Parse("{\"wide\":9007199254740993}");
        Throws<OperationCanceledException>(() => EcmaScriptJsonProjection.Project(owned, cancellationToken: canceled.Token));
        Throws<OperationCanceledException>(() => EcmaScriptJsonProjection.Project("1e400", cancellationToken: canceled.Token));
        Equal("{\"wide\":9007199254740993}", owned.ToString(), "canceled projection ownership");
        return Task.CompletedTask;
    }
    private static void Failure(EcmaScriptJsonProjectionFailure expected, Action action)
    { try { action(); } catch (EcmaScriptJsonProjectionException error) { Equal(expected, error.Failure, "admission classification"); Check(!error.Message.Contains("comment", StringComparison.Ordinal), "Input entered diagnostic."); return; } throw new InvalidOperationException("Expected projection failure: " + expected); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Equal(string expected, string actual, string context)
    { if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new InvalidOperationException(context + ": expected " + expected + "; actual " + actual); }
    private static void Equal<T>(T expected, T actual, string context)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException(context + ": expected " + expected + "; actual " + actual); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
