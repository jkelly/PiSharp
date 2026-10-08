using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.Contracts;

internal static class AnthropicSourceSnapshotTests
{
    private const string Start = """{"type":"message_start","message":{"id":"response-1","model":"actual-model","usage":{"input_tokens":11,"output_tokens":2}}}""";
    private const string Text = """{"type":"content_block_start","index":9,"content_block":{"type":"text","text":"initial"}}""";
    private const string Thinking = """{"type":"content_block_start","index":3,"content_block":{"type":"thinking","thinking":"thought","signature":"a"}}""";
    private const string Tool = """{"type":"content_block_start","index":8,"content_block":{"type":"tool_use","id":"call","name":"inspect","input":{"initial":true}}}""";
    private const string End = """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":7}}""";
    private const string Stop = """{"type":"message_stop"}""";
    private static string BlockStop(int index) => $$"""{"type":"content_block_stop","index":{{index}}}""";
    private static string Delta(int index, string type, string key, string value) =>
        "{\"type\":\"content_block_delta\",\"index\":" + index + ",\"delta\":{\"type\":\"" + type + "\",\"" + key + "\":" + JsonSerializer.Serialize(value) + "}}";

    internal static async Task UsagePropertiesSignaturesAndOwnership()
    {
        var input = new[] { Start, Text, Thinking, Delta(3, "signature_delta", "signature", "b"),
            Delta(9, "text_delta", "text", " later"), BlockStop(9), BlockStop(3), End, Stop };
        var captured = await Collect(input, true);
        var plain = await Collect(input, false);
        Equivalent(plain, captured);
        Check(plain.All(frame => frame.SourceEmissionSnapshot is null), "Default compact route has no snapshots");
        Check(captured.All(frame => frame.SourceEmissionSnapshot is not null), "All supported emitted observations present");
        var start = Snapshot(captured[0]).GetProperty("partial");
        Check(start.GetProperty("usage").GetProperty("input").GetInt32() == 0 && !start.TryGetProperty("responseId", out _), "Initial observation predates message_start");
        var text = Snapshot(captured.OfType<TextStarted>().Single()).GetProperty("partial");
        Check(text.GetProperty("timestamp").GetInt64() == 123 && text.GetProperty("responseId").GetString() == "response-1", "Owned timestamp and response properties");
        Check(text.GetProperty("responseModel").GetString() == "actual-model" && text.GetProperty("usage").GetProperty("input").GetInt32() == 11, "Actual usage and response model at emission");
        Check(text.GetProperty("content")[0].GetProperty("index").GetInt32() == 9, "Wire index differs from content index");
        var before = Snapshot(captured.OfType<ThinkingStarted>().Single()).GetProperty("partial").GetProperty("content")[1];
        var after = Snapshot(captured.OfType<TextDelta>().Single()).GetProperty("partial").GetProperty("content")[1];
        Check(before.GetProperty("thinkingSignature").GetString() == "a" && after.GetProperty("thinkingSignature").GetString() == "ab", "Signature-only state visible on next push without mutating earlier snapshot");
        var ended = Snapshot(captured.OfType<TextEnded>().Single()).GetProperty("partial").GetProperty("content");
        Check(!ended[0].TryGetProperty("index", out _) && ended[1].GetProperty("index").GetInt32() == 3, "Only ended block loses scratch index");
        var final = Snapshot(captured[^1]).GetProperty("message");
        Check(final.GetProperty("usage").GetProperty("output").GetInt32() == 7 && text.GetProperty("usage").GetProperty("output").GetInt32() == 2, "Final usage does not rewrite earlier snapshot");
    }

    internal static async Task PartialToolArgumentsAreObservationOnly()
    {
        var input = new[] { Start, Tool, Delta(8, "input_json_delta", "partial_json", ""),
            Delta(8, "input_json_delta", "partial_json", "{\"name\":\"hel"),
            Delta(8, "input_json_delta", "partial_json", "lo\"}"), BlockStop(8), End, Stop };
        var captured = await Collect(input, true);
        Equivalent(await Collect(input, false), captured);
        var started = Snapshot(captured.OfType<ToolCallStarted>().Single()).GetProperty("partial").GetProperty("content")[0];
        Check(started.GetProperty("arguments").GetProperty("initial").GetBoolean() && started.GetProperty("partialJson").GetString() == "", "Initial input retained before any delta");
        var deltas = captured.OfType<ToolCallDelta>().ToArray();
        var empty = Snapshot(deltas[0]).GetProperty("partial").GetProperty("content")[0];
        Check(!empty.GetProperty("arguments").EnumerateObject().Any(), "First empty delta replaces initial input");
        var partial = Snapshot(deltas[1]).GetProperty("partial").GetProperty("content")[0];
        Check(partial.GetProperty("arguments").GetProperty("name").GetString() == "hel" && partial.GetProperty("partialJson").GetString() == "{\"name\":\"hel", "Source-derived partial parse and raw scratch coexist");
        var ended = Snapshot(captured.OfType<ToolCallEnded>().Single());
        Check(ended.GetProperty("toolCall").GetProperty("arguments").GetProperty("name").GetString() == "hello", "Nested finalized tool");
        Check(!ended.GetProperty("toolCall").TryGetProperty("index", out _) && !ended.GetProperty("toolCall").TryGetProperty("partialJson", out _), "Final source tool strips scratch");
        Check(((ToolCallStarted)captured[1]).ToolCall.Arguments.Value.GetProperty("initial").GetBoolean(), "Native start payload never replaced by preview");
    }

    internal static async Task UnsupportedPreviewIsExplicitAbsence()
    {
        var input = new[] { Start, Tool, Delta(8, "input_json_delta", "partial_json", "[]"), BlockStop(8), End, Stop };
        var captured = await Collect(input, true);
        Equivalent(await Collect(input, false), captured);
        Check(captured.OfType<ToolCallDelta>().Single().SourceEmissionSnapshot is null, "Nonobject preview remains absent, no compact fallback");
        Check(captured[^1] is StreamError, "Observation cannot bypass strict final argument admission");
    }

    internal static Task OptionsKeepConstructorAndDeconstructionShape()
    {
        var type = typeof(AnthropicMessagesOptions);
        Check(type.GetConstructors().Single().GetParameters().Length == 12, "Original public primary constructor retained");
        Check(type.GetMethod("Deconstruct")!.GetParameters().Length == 12, "Original deconstruction retained");
        var defaults = new AnthropicMessagesOptions();
        var enabled = defaults with { CaptureSourceEmissionSnapshots = true };
        Check(!defaults.CaptureSourceEmissionSnapshots && enabled.CaptureSourceEmissionSnapshots, "Init-only opt-in and immutable default");
        return Task.CompletedTask;
    }
    private static JsonElement Snapshot(StreamEvent frame) => frame.SourceEmissionSnapshot?.Value ?? throw new InvalidOperationException("Missing source observation");
    private static void Equivalent(List<StreamEvent> expected, List<StreamEvent> actual)
    {
        Check(expected.Count == actual.Count, "Native frame count retained");
        for (var i = 0; i < expected.Count; i++)
            Check(PiWireJson.WriteEvent(expected[i]).ToString() == PiWireJson.WriteEvent(actual[i]).ToString(), "Compact payload, diagnostics and terminal retained");
    }
    internal static async Task OriginalBinary64CostsKeepNativeDecimals()
    {
        var rates = new AnthropicTokenRates(1, 2, .5m, 1.25m);
        var start = Start.Replace("\"input_tokens\":11,\"output_tokens\":2", "\"input_tokens\":36,\"output_tokens\":0", StringComparison.Ordinal);
        var input = new[] { start, Text, BlockStop(9), End, Stop };
        var captured = await Collect(input, true, rates);
        var native = await Collect(input, false, rates);
        Equivalent(native, captured);
        var cost = Snapshot(captured.OfType<TextStarted>().Single()).GetProperty("partial").GetProperty("usage").GetProperty("cost");
        Check(unchecked((ulong)BitConverter.DoubleToInt64Bits(cost.GetProperty("input").GetDouble())) == 0x3f02dfd694ccab3f, "Original divide-before-multiply historical 36-token vector");
        var final = (StreamTerminalEvent)captured[^1];
        Check(final.Message.Usage.Cost.Input == .000036m && final.Message.Usage.Cost.SourceBinary64Cost is null, "Native exact decimal unchanged");
        // Thirty-six tokens do not distinguish the operations. Five tokens do;
        // compare the serialized decimal boundary, not an assumed CLR cast result.
        var fiveStart = start.Replace("\"input_tokens\":36", "\"input_tokens\":5", StringComparison.Ordinal);
        var fiveFrames = await Collect(new[] { fiveStart, Text, BlockStop(9), End, Stop }, true, rates);
        var fiveSource = Snapshot(fiveFrames.OfType<TextStarted>().Single()).GetProperty("partial").GetProperty("usage").GetProperty("cost").GetProperty("input").GetDouble();
        var fiveNative = ((StreamTerminalEvent)fiveFrames[^1]).Message.Usage.Cost.Input;
        var decimalJsonNumber = JsonSerializer.SerializeToElement(fiveNative).GetDouble();
        Check(fiveNative == .000005m, "Five-token native decimal remains exact");
        Check(unchecked((ulong)BitConverter.DoubleToInt64Bits(fiveSource)) == 0x3ed4f8b588e368f0, "Five-token divide-before-multiply source golden");
        Check(unchecked((ulong)BitConverter.DoubleToInt64Bits(decimalJsonNumber)) == 0x3ed4f8b588e368f1, "Serialized exact decimal rounds to distinct Number golden");
        Check(BitConverter.DoubleToInt64Bits(fiveSource) != BitConverter.DoubleToInt64Bits(decimalJsonNumber), "Original arithmetic differs from final-decimal JSON projection");
        var terminalCost = Snapshot(final).GetProperty("message").GetProperty("usage").GetProperty("cost");
        Check(terminalCost.GetProperty("input").GetDouble() == cost.GetProperty("input").GetDouble(), "Terminal also projects original arithmetic");
        var mixed = """{"type":"message_start","message":{"id":"mixed","model":"fixture","usage":{"input_tokens":101,"output_tokens":23,"cache_read_input_tokens":17,"cache_creation_input_tokens":19,"cache_creation":{"ephemeral_1h_input_tokens":7}}}}""";
        var mixedRates = new AnthropicTokenRates(3, 15, .3m, 3.75m);
        var mixedInput = new[] { mixed, Text, BlockStop(9), End, Stop };
        var mixedFrames = await Collect(mixedInput, true, mixedRates);
        Equivalent(await Collect(mixedInput, false, mixedRates), mixedFrames);
        var mixedCost = Snapshot(mixedFrames.OfType<TextStarted>().Single()).GetProperty("partial").GetProperty("usage").GetProperty("cost");
        Check(unchecked((ulong)BitConverter.DoubleToInt64Bits(mixedCost.GetProperty("cacheWrite").GetDouble())) == 0x3f16ce789e774eec, "Combined one-hour numerator");
        Check(unchecked((ulong)BitConverter.DoubleToInt64Bits(mixedCost.GetProperty("total").GetDouble())) == 0x3f484068a5dbc73a, "Sequential source total");
        var failed = await Collect(new[] { start, Text }, true, rates);
        var failedCost = Snapshot(failed[^1]).GetProperty("error").GetProperty("usage").GetProperty("cost");
        Check(failed[^1] is StreamError && failedCost.GetProperty("input").GetDouble() == cost.GetProperty("input").GetDouble(), "Sanitized error partial projects observed usage");
    }

    internal static async Task RejectedUsageKeepsCommittedCostAndNativeFailure()
    {
        var invalidStart = """{"type":"message_start","message":{"id":"bad","model":"fixture","usage":{"cache_creation_input_tokens":0,"cache_creation":{"ephemeral_1h_input_tokens":1}}}}""";
        var validStart = """{"type":"message_start","message":{"id":"good","model":"fixture","usage":{"input_tokens":101,"output_tokens":23,"cache_creation_input_tokens":19,"cache_creation":{"ephemeral_1h_input_tokens":7}}}}""";
        var invalidDelta = """{"type":"message_delta","delta":{},"usage":{"cache_creation_input_tokens":19,"cache_creation":{"ephemeral_1h_input_tokens":20}}}""";
        var rates = new AnthropicTokenRates(3, 15, .3m, 3.75m);
        foreach (var input in new[] { new[] { invalidStart }, new[] { validStart, Text, invalidDelta } })
        {
            var plain = await Collect(input, false, rates);
            var captured = await Collect(input, true, rates);
            Equivalent(plain, captured);
            Check(captured[^1] is StreamError, "Rejected usage retains sanitized native error instead of throwing");
            var terminal = (StreamTerminalEvent)captured[^1];
            var error = Snapshot(terminal).GetProperty("error");
            Check(!error.TryGetProperty("anthropicFailure", out _), "Source error omits native-only classification");
            var nativeError = PiWireJson.WriteSourceEvent(terminal).Value.GetProperty("error");
            Check(nativeError.GetProperty("anthropicFailure").GetString() == "MalformedStream", "Native classification and generic extension serialization retained");
            Check(error.GetProperty("errorMessage").GetString() == nativeError.GetProperty("errorMessage").GetString(), "Source omission preserves sanitized diagnostic message");
            var expectedBits = input.Length == 1 ? 0UL : 0x3f16ce789e774eecUL;
            Check(unchecked((ulong)BitConverter.DoubleToInt64Bits(error.GetProperty("usage").GetProperty("cost").GetProperty("cacheWrite").GetDouble())) == expectedBits,
                "Projection uses committed usage split, not rejected scratch count");
            Check(terminal.Message.Usage.CacheWrite == (input.Length == 1 ? 0 : 19), "Rejected usage never replaces native committed counts");
        }
    }

    private static async Task<List<StreamEvent>> Collect(string[] input, bool capture, AnthropicTokenRates? rates = null)
    {
        var transport = new AnthropicMessagesTransport((_, token) => Source(input, token), new(Rates: rates) { CaptureSourceEmissionSnapshots = capture });
        var result = new List<StreamEvent>();
        await foreach (var frame in transport.StreamAsync(new(new("fixture", "anthropic-messages", "anthropic"), [], 123))) result.Add(frame);
        return result;
    }
    private static async IAsyncEnumerable<JsonData> Source(string[] input, [EnumeratorCancellation] CancellationToken token)
    { await Task.CompletedTask; foreach (var item in input) { token.ThrowIfCancellationRequested(); yield return JsonData.Parse(item); } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
