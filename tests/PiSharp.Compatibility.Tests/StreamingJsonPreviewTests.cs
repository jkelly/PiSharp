using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;

static class StreamingJsonPreviewTests
{
    public static async Task Corpus(string repo)
    {
        var directory = Path.Combine(repo, "fixtures", "pi-v0.99.1", "json-preview");
        var inputBytes = await File.ReadAllBytesAsync(Path.Combine(directory, "core-preview.input.json"));
        var expectedBytes = await File.ReadAllBytesAsync(Path.Combine(directory, "core-preview.expected.json"));
        Equal("5d05b574b515c99d27a71d74b862487d0ae729dd98414d31c8a73a36f21be975", Convert.ToHexStringLower(SHA256.HashData(inputBytes)));
        Equal("246182c3bc276f6d92e659bf4403447bcaacadd8aa8666c89d58c0145595ce28", Convert.ToHexStringLower(SHA256.HashData(expectedBytes)));
        using var input = JsonDocument.Parse(inputBytes); using var expected = JsonDocument.Parse(expectedBytes);
        var cases = input.RootElement.GetProperty("cases"); var observations = expected.RootElement.GetProperty("observations").GetProperty("cases");
        Equal(39, cases.GetArrayLength()); Equal(39, observations.GetArrayLength());
        var helper = new StreamingJsonPreview(); var results = new JsonArray(); var supported = 0; var excluded = 0;
        for (var index = 0; index < cases.GetArrayLength(); index++)
        {
            var item = cases[index]; var oracle = observations[index]; var id = item.GetProperty("caseId").GetString()!;
            Equal(id, oracle.GetProperty("caseId").GetString()!);
            var raw = item.GetProperty("input").GetProperty("kind").GetString() == "undefined" ? null : item.GetProperty("input").GetProperty("text").GetString();
            var unsupported = item.GetProperty("category").GetString() == "numeric-limits" ? StreamingJsonPreviewFailure.UnsupportedNumber :
                id == "unicode-surrogate-prefix" ? StreamingJsonPreviewFailure.UnsupportedUnicode : (StreamingJsonPreviewFailure?)null;
            if (unsupported is not null)
            {
                var error = ThrowsPreview(() => helper.Parse(raw)); Equal(unsupported.Value, error.Failure); excluded++;
                var reason = id switch
                {
                    "exact-large-integer" => "Deliberately narrower initial numeric policy; upstream accepts this exact 2^53 value.",
                    "inexact-large-integer" => "Upstream JavaScript rounding is not reproduced by this display profile.",
                    "inexact-decimal-a" or "inexact-decimal-b" => "Distinct high-precision decimals collapse upstream; this profile rejects them.",
                    "negative-zero" => "Negative zero cannot retain its distinction through ordinary JSON evidence.",
                    "overflow-number" => "Upstream non-finite Infinity is outside the native JSON-value profile.",
                    _ => "Upstream retains an unpaired surrogate that the native JSON string API cannot extract losslessly."
                };
                results.Add(new JsonObject { ["caseId"] = id, ["status"] = "excluded-native-policy", ["failure"] = error.Failure.ToString(), ["excludedReason"] = reason, ["diagnostic"] = error.Message });
                continue;
            }
            supported++;
            try
            {
                var result = helper.Parse(raw); Equal(raw, result.RawInput);
                var matches = Same(oracle.GetProperty("result"), result.Value.Value);
                results.Add(new JsonObject { ["caseId"] = id, ["status"] = matches ? "matched" : "mismatch", ["stage"] = result.Stage.ToString(),
                    ["rawInput"] = raw, ["result"] = JsonNode.Parse(result.Value.ToString()) });
            }
            catch (StreamingJsonPreviewException error)
            { results.Add(new JsonObject { ["caseId"] = id, ["status"] = "unexpected-rejection", ["reason"] = error.Failure.ToString() }); }
        }
        var output = Path.Combine(repo, "artifacts", "native", "json-preview-core.actual.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, new JsonObject { ["schemaVersion"] = 1, ["captureCommit"] = "14a1b04690b81d1b1c1621a4a05123346dbee88d",
            ["sourceSha"] = "d86654abb8862e201933517d6f1fce9f88dd117f", ["scope"] = "standalone-display-only-preview-subset",
            ["supported"] = supported, ["excluded"] = excluded, ["numericParity"] = "not-qualified", ["runtime"] = Environment.Version.ToString(),
            ["loneSurrogatePolicy"] = "Rejected: DOM accepts escaped surrogate; GetString throws; CLR string serialization replaces with U+FFFD.",
            ["cases"] = results }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Equal(32, supported); Equal(7, excluded);
        Assert(results.All(item => item!["status"]!.GetValue<string>() is "matched" or "excluded-native-policy"), $"Preview corpus differs: {output}");
    }
    public static Task Boundaries()
    {
        var helper = new StreamingJsonPreview(new(MaximumCharacters: 7, MaximumDepth: 2));
        Equal("{\"a\":1}", helper.Parse("{\"a\":1}").RawInput);
        Equal(StreamingJsonPreviewFailure.CharacterLimit, ThrowsPreview(() => helper.Parse("{\"a\":12}")).Failure);
        Equal(StreamingJsonPreviewFailure.DepthLimit, ThrowsPreview(() => helper.Parse("[[[]]]")).Failure);
        using var exactDepth = JsonDocument.Parse("[[]]");
        Assert(Same(exactDepth.RootElement, helper.Parse("[[]]").Value.Value), "Exact depth boundary rejected.");
        Equal(StreamingJsonPreviewFailure.DepthLimit, ThrowsPreview(() => helper.Parse("[[[")).Failure);
        var rawEscapeThenTrailingDepth = "\"\\\U0001F600\"[[[";
        Equal(StreamingJsonPreviewFailure.DepthLimit, ThrowsPreview(() => new StreamingJsonPreview(new(MaximumDepth: 2)).Parse(rawEscapeThenTrailingDepth)).Failure);
        foreach (var raw in new[] { "[9007199254740993]", "{\"n\":0.123456789012345678901}", "-0", "1e400" })
            Equal(StreamingJsonPreviewFailure.UnsupportedNumber, ThrowsPreview(() => new StreamingJsonPreview().Parse(raw)).Failure);
        // Three top-level controls and all nine Astra nested reproductions. Fixed typed
        // failures must propagate through ordinary partial-container syntax recovery.
        foreach (var token in new[] { "Infinity", "-Infinity", "NaN" })
            foreach (var raw in new[] { token, "[" + token + "]", "{\"secret-name\":" + token + "}", "[1," + token + ",2]" })
            {
                var error = ThrowsPreview(() => new StreamingJsonPreview().Parse(raw));
                Equal(StreamingJsonPreviewFailure.UnsupportedNumber, error.Failure);
                Equal("JSON preview number is outside the supported numeric policy.", error.Message);
            }
        foreach (var raw in new[] { "[I", "{\"n\":-Inf", "[1,Na", "[[Infinity]]", "{\"n\":[1,-Infinity,2]}",
            "[{\"n\":NaN}]", "[1,Infinity \r\n,2]", "[Inf]", "{\"n\":Na}" })
            Equal(StreamingJsonPreviewFailure.UnsupportedNumber, ThrowsPreview(() => new StreamingJsonPreview().Parse(raw)).Failure);
        foreach (var raw in new[] { "\"Infinity\"", "[\"NaN\",\"-Infinity\"]", "{\"Infinity\":\"NaN\"}", "{\"n\":\"text Infinity NaN\"}" })
        {
            using var quoted = JsonDocument.Parse(raw);
            var preview = new StreamingJsonPreview().Parse(raw); Equal(raw, preview.RawInput);
            Assert(Same(quoted.RootElement, preview.Value.Value), "Quoted non-finite text changed.");
        }
        foreach (var raw in new[] { "[1,Infinitude,2]", "[1,NaName,2]", "[1,-,2]" })
        {
            using var priorMember = JsonDocument.Parse("[1]");
            Assert(Same(priorMember.RootElement, new StreamingJsonPreview().Parse(raw).Value.Value), "Ordinary malformed-token tolerance changed.");
        }
        Equal(StreamingJsonPreviewFailure.DuplicateProperty, ThrowsPreview(() => new StreamingJsonPreview().Parse("{\"private-key\":1,\"private-key\":2}")).Failure);
        var privateError = ThrowsPreview(() => new StreamingJsonPreview(new(MaximumCharacters: 1)).Parse("private-payload"));
        Assert(!privateError.Message.Contains("private", StringComparison.Ordinal), "Rejected payload leaked in diagnostic.");
        foreach (var options in new[] { new StreamingJsonPreviewOptions(MaximumCharacters: 0), new(MaximumDepth: 0), new(MaximumDepth: 1001) })
        {
            try { _ = new StreamingJsonPreview(options); throw new Exception("Invalid preview options accepted."); }
            catch (ArgumentOutOfRangeException) { }
        }
        return Task.CompletedTask;
    }
    public static Task DisplayOnly()
    {
        var helper = new StreamingJsonPreview();
        foreach (var raw in new[] { "{\"path\":\"C:\\", "{\"text\":\"line\nnext\"}", "{\"path\":\"C:\\q\"}" })
        {
            var preview = helper.Parse(raw); Equal(raw, preview.RawInput); Assert(preview.Value.Value.ValueKind == JsonValueKind.Object, "Preview changed object shape.");
            try { _ = FinalToolArguments.ParseStrict(raw); throw new Exception("Preview input bypassed strict final validation."); }
            catch (JsonException) { }
        }
        return Task.CompletedTask;
    }
    public static Task LoneSurrogate()
    {
        using var document = JsonDocument.Parse("\"\\uD83D\"");
        Equal(JsonValueKind.String, document.RootElement.ValueKind);
        try { _ = document.RootElement.GetString(); throw new Exception("Expected lone-surrogate string extraction failure."); }
        catch (InvalidOperationException) { }
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize("\uD83D"));
        Equal("\uFFFD", serialized.RootElement.GetString());
        foreach (var raw in new[] { "\"\\uD83D\"", "\"\uD83D\"", "{\"text\":\"\\uD83D\\u", "{\"\\uD83D\":1}" })
            Equal(StreamingJsonPreviewFailure.UnsupportedUnicode, ThrowsPreview(() => new StreamingJsonPreview().Parse(raw)).Failure);
        Equal("\U0001F600", new StreamingJsonPreview().Parse("\"\\uD83D\\uDE00\"").Value.Value.GetString());
        return Task.CompletedTask;
    }
    private static bool Same(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind) return false;
        return expected.ValueKind switch
        {
            JsonValueKind.Object => expected.EnumerateObject().Count() == actual.EnumerateObject().Count() && expected.EnumerateObject().All(property => actual.TryGetProperty(property.Name, out var item) && Same(property.Value, item)),
            JsonValueKind.Array => expected.GetArrayLength() == actual.GetArrayLength() && expected.EnumerateArray().Zip(actual.EnumerateArray()).All(pair => Same(pair.First, pair.Second)),
            JsonValueKind.String => expected.GetString() == actual.GetString(),
            JsonValueKind.Number => expected.GetRawText() == actual.GetRawText(),
            _ => true
        };
    }
    private static StreamingJsonPreviewException ThrowsPreview(Action action)
    {
        try { action(); } catch (StreamingJsonPreviewException error) { return error; }
        throw new Exception("Expected sanitized JSON preview rejection.");
    }
    private static void Equal<T>(T expected, T actual) => Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
}
