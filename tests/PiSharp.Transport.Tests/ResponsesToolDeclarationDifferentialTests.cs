using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

internal static class ResponsesToolDeclarationDifferentialTests
{
    private const string SourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f";
    private const string InputSha = "ea1a88b031568f136cc288a0918a13f22c477b9efd54f2804a7cc5344fa451a7";
    private const string ExpectedSha = "46c27eb741e3edbd38fbe2442545944f1de1d6209c073efce96dfb2208e2d37d";
    private const string LockSha = "59ea343a04f610ccbd3cb4598d876ae035e0a30507c865a000df477a73b3950f";

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("responses-tools.complete-native-payloads-match-five-genuine-sdk-bodies", GenuineSdkBodies);
        yield return ("responses-tools.comparator-retains-raw-numbers-null-presence-and-array-order", ComparisonControls);
    }

    private static async Task GenuineSdkBodies()
    {
        var root = FindRepo();
        // All three byte identities are checked before any fixture is parsed.
        var inputBytes = await ReadPinned(root, "core.input.json", InputSha);
        var expectedBytes = await ReadPinned(root, "core.expected.json", ExpectedSha);
        var lockBytes = await ReadPinned(root, "oracle.lock.json", LockSha);
        using var input = JsonDocument.Parse(inputBytes);
        using var expected = JsonDocument.Parse(expectedBytes);
        using var oracleLock = JsonDocument.Parse(lockBytes);
        var source = input.RootElement;
        Equal(SourceSha, source.GetProperty("sourceSha").GetString(), "Input source identity changed.");
        Equal(SourceSha, expected.RootElement.GetProperty("sourceSha").GetString(), "Expected source identity changed.");
        Equal(SourceSha, oracleLock.RootElement.GetProperty("environmentPins").GetProperty("sourceSha").GetString(), "Lock source identity changed.");
        var observations = expected.RootElement.GetProperty("observations");
        var checks = observations.GetProperty("checks");
        Equal(5, checks.GetProperty("caseCount").GetInt32(), "Reference case count changed.");
        Equal(5, checks.GetProperty("fakeFetchCalls").GetInt32(), "Reference SDK request count changed.");
        Check(checks.GetProperty("noSourceTransformOrSdkShim").GetBoolean(), "Reference source/SDK provenance changed.");
        Check(checks.GetProperty("networkAndProcessesBlocked").GetBoolean(), "Reference offline provenance changed.");

        var declaration = source.GetProperty("model");
        var compat = declaration.GetProperty("compat");
        Check(!compat.GetProperty("supportsAdditionalTools").GetBoolean() &&
            !compat.GetProperty("supportsToolSearch").GetBoolean() &&
            !compat.GetProperty("supportsOpenAIGrammarTools").GetBoolean(), "Fixture requires an additional declaration capability.");
        var model = new ModelDescriptor(declaration.GetProperty("id").GetString()!,
            declaration.GetProperty("api").GetString()!, declaration.GetProperty("provider").GetString()!);
        var common = source.GetProperty("commonOptions");
        Equal("short", common.GetProperty("cacheRetention").GetString(), "Fixture cache profile changed.");
        var options = new ResponsesKeyAuthRequestOptions(SupportsMaxOutputTokens: true,
            MaxOutputTokens: common.GetProperty("maxTokens").GetInt32(),
            SessionId: common.GetProperty("sessionId").GetString(),
            Temperature: common.GetProperty("temperature").GetDouble());
        var expectedCases = observations.GetProperty("cases");
        var caseNames = new[] { "initial-tools-strict-capable", "remove-add-strict-capable",
            "readd-strict-capable", "replace-strict-capable", "replace-strict-incapable" };
        var toolOrders = new[] { new[] { "read", "write" }, new[] { "write", "edit" },
            new[] { "write", "edit", "read" }, new[] { "write", "edit", "read" }, new[] { "write", "edit", "read" } };
        Equal(5, source.GetProperty("cases").GetArrayLength(), "Input case count changed.");
        Equal(5, expectedCases.GetArrayLength(), "Expected case count changed.");
        var index = 0;
        foreach (var item in source.GetProperty("cases").EnumerateArray())
        {
            var caseId = item.GetProperty("caseId").GetString()!;
            Equal(caseNames[index], caseId, "Input case order changed.");
            var captured = expectedCases[index];
            Equal(caseId, captured.GetProperty("caseId").GetString(), "Captured case identity changed.");
            Equal(0, item.GetProperty("options").EnumerateObject().Count(), "Fixture has unmapped case-specific request options.");
            Equal(1, captured.GetProperty("fetchRequests").GetArrayLength(), "Expected one genuine SDK request per case.");
            var fetch = captured.GetProperty("fetchRequests")[0];
            Equal("POST", fetch.GetProperty("method").GetString(), "Reference method changed.");
            var capturedBody = fetch.GetProperty("body").GetString()!;
            Equal(fetch.GetProperty("bodyUtf8Sha256").GetString(),
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(capturedBody))), "Captured raw body hash differs.");
            using var sdkBody = JsonDocument.Parse(capturedBody);
            Check(Same(sdkBody.RootElement, fetch.GetProperty("bodyJson")), "Raw SDK body and recorded parsed body differ.");

            var messages = item.GetProperty("context").GetProperty("messages").EnumerateArray()
                .Select(value => new TranscriptEntry(value.GetProperty("role").GetString()!, JsonData.FromElement(value)))
                .ToImmutableArray();
            var originals = messages.Select(message => message.WireBody.ToString()).ToArray();
            var strictCapable = item.GetProperty("compat").GetProperty("supportsStrictMode").GetBoolean();
            var projection = new ResponsesTranscriptProjectionOptions(declaration.GetProperty("reasoning").GetBoolean(),
                SupportsMidConversationSystemMessages: compat.GetProperty("supportsMidConvoSystemMessages").GetBoolean(),
                ToolDeclarations: new(SupportsStrictMode: strictCapable));
            var endpoint = new Uri(fetch.GetProperty("url").GetString()!);
            var chat = new ChatRequest(model, messages);
            using var request = new ResponsesKeyAuthRequestFactory(endpoint, model, projection, options)
                .Create(chat, common.GetProperty("apiKey").GetString()!);
            using var nativeBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(Same(sdkBody.RootElement, nativeBody.RootElement), "Complete native request differs from genuine SDK body: " + caseId);
            var tools = nativeBody.RootElement.GetProperty("tools");
            Check(tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).SequenceEqual(toolOrders[index]),
                "Active declaration order differs: " + caseId);
            foreach (var tool in tools.EnumerateArray())
            {
                Equal(strictCapable, tool.TryGetProperty("strict", out var strict), "Strict field presence differs: " + caseId);
                if (strictCapable) Equal(JsonValueKind.False, strict.ValueKind, "Default strict value differs: " + caseId);
            }
            for (var entry = 0; entry < messages.Length; entry++)
                Equal(originals[entry], messages[entry].WireBody.ToString(), "Canonical declaration history mutated: " + caseId);

            // Deliberately wrong native capability must fail the full-body comparison.
            using var wrongRequest = new ResponsesKeyAuthRequestFactory(endpoint, model,
                projection with { ToolDeclarations = new(SupportsStrictMode: !strictCapable) }, options)
                .Create(chat, common.GetProperty("apiKey").GetString()!);
            using var wrongBody = JsonDocument.Parse(await wrongRequest.Content!.ReadAsStringAsync());
            Check(!Same(sdkBody.RootElement, wrongBody.RootElement), "Comparison missed a changed strict capability: " + caseId);
            index++;
        }
        Equal(5, index, "Not all genuine SDK bodies were compared.");
    }

    private static Task ComparisonControls()
    {
        const string baseline = """{"temperature":0,"tools":[{"name":"write","description":"original","parameters":{"minimum":0.5,"maximum":9007199254740993,"required":["path","content"],"enum":["append","replace"],"opaque":{"keep":null,"ordered":["b","a"]}},"strict":false},{"name":"read","parameters":{}}]}""";
        using var original = JsonDocument.Parse(baseline);
        var mutations = new[]
        {
            baseline.Replace("\"temperature\":0", "\"temperature\":0.0", StringComparison.Ordinal),
            baseline.Replace("\"minimum\":0.5", "\"minimum\":0.50", StringComparison.Ordinal),
            baseline.Replace("9007199254740993", "9007199254740992", StringComparison.Ordinal),
            baseline.Replace("\"strict\":false", "\"strict\":null", StringComparison.Ordinal),
            baseline.Replace(",\"strict\":false", "", StringComparison.Ordinal),
            baseline.Replace("\"keep\":null,", "", StringComparison.Ordinal),
            baseline.Replace("\"keep\":null", "\"keep\":false", StringComparison.Ordinal),
            baseline.Replace("[\"path\",\"content\"]", "[\"content\",\"path\"]", StringComparison.Ordinal),
            baseline.Replace("[\"append\",\"replace\"]", "[\"replace\",\"append\"]", StringComparison.Ordinal),
            baseline.Replace("[\"b\",\"a\"]", "[\"a\",\"b\"]", StringComparison.Ordinal),
            baseline.Replace("\"original\"", "\"replacement\"", StringComparison.Ordinal),
            baseline.Replace("\"name\":\"write\"", "\"name\":\"edit\"", StringComparison.Ordinal),
            baseline.Replace("\"maximum\":9007199254740993,", "", StringComparison.Ordinal),
            """{"temperature":0,"tools":[{"name":"read","parameters":{}},{"name":"write","description":"original","parameters":{"minimum":0.5,"maximum":9007199254740993,"required":["path","content"],"enum":["append","replace"],"opaque":{"keep":null,"ordered":["b","a"]}},"strict":false}]}"""
        };
        foreach (var mutation in mutations)
        {
            Check(mutation != baseline, "Authored negative control did not change its input.");
            using var changed = JsonDocument.Parse(mutation);
            Check(!Same(original.RootElement, changed.RootElement), "Comparison normalized a meaningful declaration/body change.");
        }
        using var ordered = JsonDocument.Parse("""{"a":{"b":1,"c":null},"d":["x","y"]}""");
        using var reordered = JsonDocument.Parse("""{"d":["x","y"],"a":{"c":null,"b":1}}""");
        Check(Same(ordered.RootElement, reordered.RootElement), "Object key ordering should be insignificant.");
        foreach (var duplicate in new[] { """{"a":1,"a":1}""", """{"a":1,"\u0061":1}""", """{"nested":{"x":0,"\u0078":0}}""" })
        {
            using var ambiguous = JsonDocument.Parse(duplicate);
            Check(!Same(ambiguous.RootElement, ambiguous.RootElement), "Comparison accepted duplicate decoded object names.");
        }
        return Task.CompletedTask;
    }

    private static bool Same(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        if (left.ValueKind == JsonValueKind.Object)
        {
            var leftProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var rightProperties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in left.EnumerateObject()) if (!leftProperties.TryAdd(property.Name, property.Value)) return false;
            foreach (var property in right.EnumerateObject()) if (!rightProperties.TryAdd(property.Name, property.Value)) return false;
            return leftProperties.Count == rightProperties.Count && leftProperties.All(property =>
                rightProperties.TryGetValue(property.Key, out var value) && Same(property.Value, value));
        }
        return left.ValueKind switch
        {
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength() &&
                left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Same(pair.First, pair.Second)),
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            _ => left.GetRawText() == right.GetRawText()
        };
    }

    private static async Task<byte[]> ReadPinned(string root, string name, string sha)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "fixtures/pi-v0.99.1/responses-tools-sdk", name));
        Equal(sha, Convert.ToHexStringLower(SHA256.HashData(bytes)), "Fixture byte identity changed: " + name);
        return bytes;
    }

    private static string FindRepo()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "fixtures/pi-v0.99.1/responses-tools-sdk/core.input.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Responses tools fixture root was not found.");
    }

    private static void Equal<T>(T expected, T actual, string message) => Check(EqualityComparer<T>.Default.Equals(expected, actual), message);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
