using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

internal static class CompletionsSourceDifferentialTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string repo)
    {
        for (var index = 0; index < 3; index++)
        {
            var selected = index;
            yield return ($"Completions qualified complete final push drain and undefined case {selected}", () => ReferenceCase(repo, selected));
        }
        yield return ("Completions exact source binary64 arithmetic and owned cost serialization", CostSerialization);
        yield return ("Completions provisional headers preserve immutable history and final identity authority", ProvisionalAuthority);
        yield return ("Completions consecutive reasoning details retain signatures common and opaque fields", ReasoningDetails);
        yield return ("Completions source snapshot replay metadata and raw chunk caps cannot be bypassed", SnapshotBounds);
        yield return ("Completions default 1024 fragments stay compact without cumulative snapshot copies", CompactFragmented);
        yield return ("Completions SDK error property uses JSON binary64 JavaScript truthiness", ErrorTruthiness);
        yield return ("Completions differential comparison preserves missing null numeric and array identity", ComparatorControls);
    }

    private static async Task ReferenceCase(string repo, int index)
    {
        using var input = JsonDocument.Parse(Pinned(repo, "fixtures/reference/openai-completions-stream/input.json", 65_536,
            "fad084d8113bdbc482e79bd9fbc0070bb8895d701fee489fdf0cdfcc180e2607"));
        using var golden = JsonDocument.Parse(Pinned(repo, "fixtures/reference/openai-completions-stream/expected.json", 262_144,
            "3d8214d2471e67580a14fbc8441aa35721c559312463e570411aa01dba68183d"));
        _ = Pinned(repo, "tools/ReferenceOracle/openai-completions-stream.lock.json", 262_144,
            "0f5f4ca5d6a6c93a95c4a0c27b640ff21c3460c9aef8e1112c0700c0605fffe9");
        var inputs = input.RootElement.GetProperty("cases");
        var observations = golden.RootElement.GetProperty("observations").GetProperty("cases");
        Equal(3, inputs.GetArrayLength(), "complete source inputs"); Equal(3, observations.GetArrayLength(), "complete source records");
        var authored = inputs[index]; var observed = observations[index];
        Equal(authored.GetProperty("caseId").GetString(), observed.GetProperty("caseId").GetString(), "source case identity");
        var fields = observed.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);
        Check(fields.SequenceEqual(new[] { "caseId", "payloadSnapshots", "fetchRequests", "responseHooks", "providerEvents", "emissionSnapshots", "drainedFrames", "finalResult", "trace", "responseWire" }.Order(StringComparer.Ordinal)),
            "Full source case inventory changed; request/SDK observations must remain retained.");
        var model = input.RootElement.GetProperty("model"); var rates = model.GetProperty("cost");
        var options = new OpenAICompletionsWireOptions(Rates: new(rates.GetProperty("input").GetDecimal(), rates.GetProperty("output").GetDecimal(),
            rates.GetProperty("cacheRead").GetDecimal(), rates.GetProperty("cacheWrite").GetDecimal()));
        var chunks = authored.GetProperty("wireChunks").EnumerateArray().Select(JsonData.FromElement).ToArray();
        var request = new ChatRequest(new(model.GetProperty("id").GetString()!, "openai-completions", model.GetProperty("provider").GetString()!),
            [], input.RootElement.GetProperty("clock").GetProperty("unixMilliseconds").GetInt64());
        var consumed = new List<JsonData>();
        var mapper = new OpenAICompletionsWireSource((_, token) => OwnedChunks(chunks, consumed, token),
            CompletionsSourceEventProjection.CaptureOwnedSnapshots(options));
        var frames = new List<StreamEvent>();
        await foreach (var frame in mapper.StreamAsync(request)) frames.Add(frame);
        Check(frames[^1] is StreamDone, "Qualified complete source case failed: " + PiWireJson.WriteEvent(frames[^1]));
        var terminal = (StreamDone)frames[^1];
        Same(observed.GetProperty("finalResult").GetProperty("value"), PiWireJson.WriteMessage(terminal.Message).Value, "/finalResult/value");
        var compactMapper = new OpenAICompletionsWireSource((_, token) => OwnedChunks(chunks, token: token), options);
        var compact = new List<StreamEvent>();
        await foreach (var frame in compactMapper.StreamAsync(request)) compact.Add(frame);
        Check(compact[^1] is StreamDone && compact.All(frame => frame.SourceEmissionSnapshot is null && frame.SourceDrainSnapshot is null),
            "Default Completions progress captured whole messages or changed the complete source outcome.");
        Same(observed.GetProperty("finalResult").GetProperty("value"), PiWireJson.WriteMessage(((StreamDone)compact[^1]).Message).Value,
            "/defaultFinal/value");
        Equal(frames.Count, compact.Count, "selected and default compact native frame count");
        for (var eventIndex = 0; eventIndex < frames.Count; eventIndex++)
            Same(PiWireJson.WriteEvent(frames[eventIndex]).Value, PiWireJson.WriteEvent(compact[eventIndex]).Value,
                $"/selectedDefaultNativeFrame/{eventIndex}");
        Equal(0, observed.GetProperty("finalResult").GetProperty("ownUndefinedPaths").GetArrayLength(), "final source undefined inventory");
        Check(frames.All(frame => frame is ToolCallHeaderUpdated
            ? frame.SourceEmissionSnapshot is null && frame.SourceDrainSnapshot is null
            : frame.SourceEmissionSnapshot is not null && frame.SourceDrainSnapshot is not null),
            "A native source push was silently omitted instead of projected.");
        var pushes = frames.Select(CompletionsSourceEventProjection.ReadEmission).Where(value => value is not null).Cast<JsonData>().ToArray();
        var capturedPushes = observed.GetProperty("emissionSnapshots");
        Equal(new[] { 6, 14, 9 }[index], capturedPushes.GetArrayLength(), "complete source push cardinality");
        Equal(capturedPushes.GetArrayLength(), pushes.Length, "complete projected push count");
        for (var eventIndex = 0; eventIndex < pushes.Length; eventIndex++)
            Same(capturedPushes[eventIndex], pushes[eventIndex].Value, $"/emissionSnapshots/{eventIndex}");
        var drained = CompletionsSourceEventProjection.ReadCapturedDrainObservations(frames);
        var capturedDrain = observed.GetProperty("drainedFrames");
        Equal(capturedDrain.GetArrayLength(), drained.Count, "complete concurrent source boundary drain count");
        for (var eventIndex = 0; eventIndex < drained.Count; eventIndex++)
            Same(capturedDrain[eventIndex], drained[eventIndex].Value, $"/drainedFrames/{eventIndex}");
        var providerEvents = observed.GetProperty("providerEvents");
        Equal(chunks.Length, consumed.Count, "all authored chunks consumed"); Equal(consumed.Count, providerEvents.GetArrayLength(), "all source provider callbacks");
        for (var chunk = 0; chunk < consumed.Count; chunk++)
        {
            Same(providerEvents[chunk].GetProperty("value"), consumed[chunk].Value, $"/providerEvents/{chunk}/value");
            Equal(0, providerEvents[chunk].GetProperty("ownUndefinedPaths").GetArrayLength(), "provider JSON undefined inventory");
        }
        var reducer = new AssistantStreamReducer(((StreamStarted)frames[0]).Partial);
        foreach (var frame in frames) reducer.Apply(frame);
        Same(observed.GetProperty("finalResult").GetProperty("value"), PiWireJson.WriteMessage(reducer.Snapshot()).Value, "/reducedNativeFinal");
        Check(terminal.Message.Content.OfType<ToolCallContent>().All(call => !string.IsNullOrWhiteSpace(call.Id) && !string.IsNullOrWhiteSpace(call.Name)),
            "A complete tool has blank identity.");
        Equal(terminal.Message.Content.OfType<ToolCallContent>().Count(), terminal.Message.Content.OfType<ToolCallContent>().Select(call => call.Id).Distinct(StringComparer.Ordinal).Count(), "unique strict final tool IDs");
        foreach (var call in terminal.Message.Content.OfType<ToolCallContent>()) FinalToolArguments.ParseStrict(call.Arguments.ToString());
        if (index == 1)
        {
            var provisional = frames.OfType<ToolCallProvisionalStarted>().Single();
            Equal("", provisional.ToolCall.Id, "real blank provisional identity"); Equal("beta", provisional.ToolCall.Name, "retained initial name");
            Check(frames.OfType<ToolCallHeaderUpdated>().Single().ContentIndex == provisional.ContentIndex, "Explicit header update lost correlation.");
            Equal("", provisional.ToolCall.Id, "earlier immutable frame changed after identity fill");
            Check(pushes.Any(value => value.Value.GetProperty("ownUndefinedPaths").GetArrayLength() > 0), "Source own-undefined distinctions were erased.");
        }
        // Request payload, fetch/header/body and response hooks remain complete in
        // the pinned case record, owned by the separate request/HTTP composition lane.
        // No native request or SDK callback event is fabricated by this mapper test.
    }

    private static async Task CostSerialization()
    {
        var frames = await Collect([
            """{"usage":{"prompt_tokens":11,"completion_tokens":3},"choices":[{"delta":{},"finish_reason":"stop"}]}"""],
            new(Rates: new(2, 6, 0.5m, 1)));
        var cost = PiWireJson.WriteMessage(((StreamDone)frames[^1]).Message).Value.GetProperty("usage").GetProperty("cost");
        Equal("0.000022", cost.GetProperty("input").GetRawText(), "source input division then multiplication");
        Equal("0.000018", cost.GetProperty("output").GetRawText(), "source output division then multiplication");
        Equal("0.000039999999999999996", cost.GetProperty("total").GetRawText(), "source left-associative binary64 total");
        var message = ((StreamDone)frames[^1]).Message;
        Check(message.Usage.Cost.SourceBinary64Cost is not null, "Exact source numeric sidecar missing.");
        var before = message.Usage.Cost.SourceBinary64Cost!.ToString();
        var copied = PiWireJson.ReadMessage(PiWireJson.WriteMessage(message).Value);
        Same(PiWireJson.WriteMessage(message).Value, PiWireJson.WriteMessage(copied).Value, "/costWireRoundtrip");
        Equal(before, message.Usage.Cost.SourceBinary64Cost!.ToString(), "wire writer mutated source cost");
        var scaled = message with { Usage = message.Usage with { Cost = message.Usage.Cost with
            { Input = 0.0000220m, Output = 0.0000180m, CacheRead = 0.00m, CacheWrite = 0.00m, Total = 0.0000399999999999999960m } } };
        Same(PiWireJson.WriteMessage(message).Value, PiWireJson.WriteMessage(scaled).Value, "/coherentDecimalScalePreservesSourceLexemes");
        foreach (var stale in new[]
        {
            message.Usage.Cost with { Input = 1m }, message.Usage.Cost with { Output = 1m },
            message.Usage.Cost with { CacheRead = 1m }, message.Usage.Cost with { CacheWrite = 1m },
            message.Usage.Cost with { Total = 1m }
        })
            Throws<JsonException>(() => PiWireJson.WriteMessage(message with { Usage = message.Usage with { Cost = stale } }));
        Equal(before, message.Usage.Cost.SourceBinary64Cost!.ToString(), "stale sidecar rejection mutated owned source cost");
        var nativeDecimal = message with { Usage = message.Usage with { Cost = new(0.000022m, 0.000018m, 0, 0, 0.000040m) } };
        Equal("0.000040", PiWireJson.WriteMessage(nativeDecimal).Value.GetProperty("usage").GetProperty("cost").GetProperty("total").GetRawText(), "other providers retain decimal profile");
    }

    private static async Task ProvisionalAuthority()
    {
        var frames = await Collect([
            """{"choices":[{"delta":{"tool_calls":[{"index":4,"function":{"arguments":"{\"n\":"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":4,"id":"one","function":{"name":"read","arguments":"1}"}}]},"finish_reason":"tool_calls"}]}"""]);
        Check(frames[^1] is StreamDone, "Complete provisional identity failed.");
        var start = frames.OfType<ToolCallProvisionalStarted>().Single();
        Equal("", start.ToolCall.Id, "provisional ID must not be invented"); Equal("", start.ToolCall.Name, "provisional name must not be invented");
        var reducer = new AssistantStreamReducer(((StreamStarted)frames[0]).Partial);
        foreach (var frame in frames) reducer.Apply(frame);
        var header = frames.OfType<ToolCallHeaderUpdated>().Single();
        Check(CompletionsSourceEventProjection.ReadEmission(header) is null, "Native header update invented an extra source push.");
        Equal("", start.ToolCall.Id, "mutable alias entered native start");
        // Owner decision 13: a provisional identity that is never filled ends as upstream ends it, with id "" and name ""
        // (openai-completions.ts ensureToolCallBlock; captured pi-ai 1.1.0 turn is toolUse). Its finality grants no execution: the agent
        // finds no tool named "" and answers "Tool  not found".
        var unfilled = await Collect(["""{"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{}"}}]},"finish_reason":"tool_calls"}]}"""]);
        Check(unfilled[^1] is StreamDone { Reason: StopReason.ToolUse } && unfilled.OfType<ToolCallEnded>().Single().ToolCall is { Id: "", Name: "" },
            "An unfilled provisional identity did not end as upstream ends it.");
        // Pi abe508 openai-completions.ts:464 finalizes with parseStreamingJson (json-parse.ts:104-124): an unfinished '{"n":' is the
        // partial-json {} (installed pi-ai 1.1.0), so a complete identity with repaired arguments ends the call.
        var repaired = await Collect(["""{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"x","function":{"name":"read","arguments":"{\"n\":"}}]},"finish_reason":"tool_calls"}]}"""]);
        Check(repaired[^1] is StreamDone && repaired.OfType<ToolCallEnded>().Single().ToolCall.Arguments.ToString() == "{}",
            "Repaired arguments did not finalize as parseStreamingJson returns them.");
        var direct = new AssistantStreamReducer(((StreamStarted)frames[0]).Partial); direct.Apply(frames[0]); direct.Apply(start);
        direct.Apply(header);
        Throws<StreamProtocolException>(() => direct.Apply(new ToolCallHeaderUpdated(start.ContentIndex, "changed", "read")));
        Throws<StreamProtocolException>(() => direct.Apply(new ToolCallEnded(start.ContentIndex, new("changed", "read", JsonData.Parse("{}")))));
        // parseStreamingJson may finalize any JSON value (a non-object reaches agent-loop validateToolArguments), so the reducer keeps it.
        direct.Apply(new ToolCallEnded(start.ContentIndex, new("one", "read", JsonData.Parse("[1]"))));
        Equal("[1]", ((ToolCallContent)direct.Snapshot().Content[start.ContentIndex]).Arguments.ToString(), "non-object final arguments");
    }

    private static async Task ReasoningDetails()
    {
        var frames = await Collect([
            """{"choices":[{"delta":{"reasoning_details":[{"type":"reasoning.text","text":"a","signature":null,"id":null,"index":2,"opaque":{"keep":null}}]}}]}""",
            """{"choices":[{"delta":{"reasoning_details":[{"type":"reasoning.text","text":"b","signature":"sig","id":"id","format":"format","index":3},{"type":"reasoning.encrypted","data":"+/=\u0000","opaque":{"type":"ClrNeverLoaded"}}]},"finish_reason":"stop"}]}"""]);
        Check(frames[^1] is StreamDone, "Valid source reasoning detail replay failed.");
        var message = ((StreamDone)frames[^1]).Message;
        Equal("", ((ThinkingContent)message.Content[0]).Thinking, "Replay details became visible thinking deltas");
        var signature = message.Content[0].ExtraProperties!.Values["thinkingSignature"].Value.GetString()!;
        Equal("[{\"type\":\"reasoning.text\",\"text\":\"ab\",\"signature\":\"sig\",\"id\":\"id\",\"index\":2,\"opaque\":{\"keep\":null},\"format\":\"format\"},{\"type\":\"reasoning.encrypted\",\"data\":\"+/=\\u0000\",\"opaque\":{\"type\":\"ClrNeverLoaded\"}}]", signature, "source detail merge/order/common/opaque signature");
        Check(!frames.OfType<ThinkingDelta>().Any(), "Encrypted or summary replay emitted an invented thinking delta.");
        var invalidIgnored = await Collect(["""{"choices":[{"delta":{"reasoning_details":[{"type":"reasoning.text","text":7},{"type":"reasoning.encrypted","data":"inert","id":7}]},"finish_reason":"stop"}]}"""]);
        Check(invalidIgnored[^1] is StreamDone && !invalidIgnored.OfType<ThinkingStarted>().Any(), "Source-invalid replay detail was admitted as a block.");
    }

    private static async Task SnapshotBounds()
    {
        var complete = await Collect(["""{"choices":[{"delta":{"content":"x"},"finish_reason":"stop"}]}"""],
            CompletionsSourceEventProjection.CaptureOwnedSnapshots());
        Check(complete[^1] is StreamDone, "Alias cap control requires a complete owned source stream.");
        Throws<StreamLimitException>(() => CompletionsSourceEventProjection.ReadCapturedDrainObservations(complete, 64));
        var missing = complete.Select((frame, index) => index == 1 ? frame with { SourceEmissionSnapshot = null } : frame).ToArray();
        Throws<StreamProtocolException>(() => CompletionsSourceEventProjection.ReadCapturedDrainObservations(missing));
        var missingDrain = complete.Select((frame, index) => index == 1 ? frame with { SourceDrainSnapshot = null } : frame).ToArray();
        Throws<StreamProtocolException>(() => CompletionsSourceEventProjection.ReadCapturedDrainObservations(missingDrain));
        var limited = await Collect(["""{"choices":[{"delta":{"content":"x"},"finish_reason":"stop"}]}"""],
            CompletionsSourceEventProjection.CaptureOwnedSnapshots(new(MaximumContentCharacters: 64)));
        Check(limited[^1] is StreamError && !limited.OfType<StreamDone>().Any(), "Owned source snapshots bypassed their existing configured cap.");
        Equal("ResourceLimit", ((StreamError)limited[^1]).NativeDiagnostic?.Code.ToString(), "snapshot cap classification");
        var fragments = Enumerable.Repeat("""{"choices":[{"delta":{"content":"abcdefgh"}}]}""", 128)
            .Append("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""").ToArray();
        var retained = new OpenAICompletionsWireOptions(MaximumContentCharacters: 16_384);
        var plain = await Collect(fragments, retained);
        Check(plain[^1] is StreamDone, "Compact retained-state control failed before selected trace admission.");
        var history = await Collect(fragments, CompletionsSourceEventProjection.CaptureOwnedSnapshots(retained));
        Check(history[^1] is StreamError && !history.OfType<StreamDone>().Any(), "Selected cumulative trace history bypassed its cap.");
        Equal("ResourceLimit", ((StreamError)history[^1]).NativeDiagnostic?.Code.ToString(), "selected cumulative capture cap");

        var fallback = new AssistantMessage("openai-completions", "provider", "model", 123, [], TokenUsage.Zero, StopReason.Pending);
        var reducer = new AssistantStreamReducer(fallback, new(MaximumBlocks: 2, MaximumCharacters: 64));
        reducer.Apply(new StreamStarted(fallback)); reducer.Apply(new TextStarted(0, new("")));
        var sidecar = JsonData.Parse("\"" + new string('x', 24) + "\"");
        var emptyDrain = JsonData.EmptyObject;
        for (var index = 0; index < 512; index++) reducer.Apply(new TextDelta(0, "")
            { SourceEmissionSnapshot = sidecar, SourceDrainSnapshot = emptyDrain });
        reducer.Apply(new TextDelta(0, "abcd") { SourceEmissionSnapshot = sidecar, SourceDrainSnapshot = emptyDrain });
        Throws<StreamLimitException>(() => reducer.Apply(new TextDelta(0, "")
            { SourceEmissionSnapshot = JsonData.Parse("\"" + new string('x', 64) + "\"") }));
        Throws<StreamLimitException>(() => reducer.Apply(new TextDelta(0, "")
            { SourceDrainSnapshot = JsonData.Parse("\"" + new string('x', 64) + "\"") }));
        Throws<StreamLimitException>(() => reducer.Apply(new TextDelta(0, "abcdefgh") { SourceEmissionSnapshot = sidecar }));
        Throws<StreamLimitException>(() => reducer.Apply(new TextDelta(0, "")
            { SourceEmissionSnapshot = sidecar, SourceDrainSnapshot = sidecar }));
        Equal("abcd", ((TextContent)reducer.Snapshot().Content[0]).Text, "current sidecar and retained payload share the existing cap");
        reducer.Apply(new TextEnded(0, "abcd") { SourceEmissionSnapshot = sidecar });
        var done = new StreamDone(StopReason.Stop, fallback with { Content = [new TextContent("abcd")], StopReason = StopReason.Stop })
            { SourceEmissionSnapshot = sidecar, SourceDrainSnapshot = emptyDrain };
        reducer.Apply(done);
        Check(reducer.IsTerminal && ReferenceEquals(sidecar, done.SourceEmissionSnapshot) && ReferenceEquals(emptyDrain, done.SourceDrainSnapshot),
            "Reducer discarded or mutated the caller's owned terminal frame.");
        var detail = "{\"choices\":[{\"delta\":{\"reasoning_details\":[{\"type\":\"reasoning.encrypted\",\"data\":\"" + new string('x', 2048) + "\"}]},\"finish_reason\":\"stop\"}]}";
        var metadata = await Collect([detail], new(MaximumContentCharacters: 1024));
        Check(metadata[^1] is StreamError && !metadata.OfType<StreamDone>().Any(), "Opaque retained replay metadata bypassed content limits.");
        var raw = await Collect([detail], new(MaximumChunkCharacters: detail.Length - 1));
        Check(raw[^1] is StreamError, "Source replay input bypassed raw chunk admission.");
    }

    private static async Task CompactFragmented()
    {
        var chunks = Enumerable.Repeat("""{"choices":[{"delta":{"content":"abcdefgh"}}]}""", 1024)
            .Append("""{"choices":[{"delta":{},"finish_reason":"stop"}]}""").ToArray();
        var frames = await Collect(chunks);
        Check(frames[^1] is StreamDone && frames.All(frame => frame.SourceEmissionSnapshot is null && frame.SourceDrainSnapshot is null),
            "Default fragmented response cloned source messages or exhausted a cumulative historical-copy budget.");
        Equal(1024, frames.OfType<TextDelta>().Count(), "every compact source fragment delivered");
        Equal(1028, frames.Count, "compact balanced stream frame count");
        var final = ((StreamDone)frames[^1]).Message;
        Equal(string.Concat(Enumerable.Repeat("abcdefgh", 1024)), ((TextContent)final.Content[0]).Text, "complete fragmented final text");
        var reducer = new AssistantStreamReducer(((StreamStarted)frames[0]).Partial);
        foreach (var frame in frames) reducer.Apply(frame);
        Same(PiWireJson.WriteMessage(final).Value, PiWireJson.WriteMessage(reducer.Snapshot()).Value, "/compactFragmentedFinal");
    }

    private static async Task ErrorTruthiness()
    {
        const string fields = "\"choices\":[{\"delta\":{\"content\":\"kept\"},\"finish_reason\":\"stop\"}]";
        foreach (var captured in new[] { false, true })
        {
            var options = captured ? CompletionsSourceEventProjection.CaptureOwnedSnapshots() : new OpenAICompletionsWireOptions();
            var baseline = await Collect(["{" + fields + "}"], options);
            Check(baseline[^1] is StreamDone, "Error truthiness baseline did not complete.");
            foreach (var error in new string?[] { null, "null", "false", "0", "-0", "\"\"", "1e-400", "-1e-400", "2e-324", "-2e-324" })
            {
                var chunk = "{" + (error is null ? "" : "\"error\":" + error + ",") + fields + "}";
                var frames = await Collect([chunk], options);
                Check(frames[^1] is StreamDone, "Falsy SDK error value rejected: " + (error ?? "missing"));
                Equal(baseline.Count, frames.Count, "falsy error complete frame count");
                for (var index = 0; index < baseline.Count; index++)
                {
                    Same(PiWireJson.WriteEvent(baseline[index]).Value, PiWireJson.WriteEvent(frames[index]).Value, "/falsyError/frame/" + index);
                    if (captured)
                    {
                        Same(baseline[index].SourceEmissionSnapshot!.Value, frames[index].SourceEmissionSnapshot!.Value, "/falsyError/push/" + index);
                        Same(baseline[index].SourceDrainSnapshot!.Value, frames[index].SourceDrainSnapshot!.Value, "/falsyError/drain/" + index);
                    }
                    else Check(frames[index].SourceEmissionSnapshot is null && frames[index].SourceDrainSnapshot is null,
                        "Falsy error enabled default full-message capture.");
                }
            }
            foreach (var error in new[] { "true", "1", "-1", "5e-324", "-5e-324", "\"bad\"", "\"0\"", "\" \"", "{}", "[]", "[false]" })
            {
                var frames = await Collect(["{\"error\":" + error + "," + fields + "}"], options);
                Check(frames[^1] is StreamError && !frames.OfType<StreamDone>().Any(), "Truthy SDK error value accepted: " + error);
                Equal("ProviderError", ((StreamError)frames[^1]).NativeDiagnostic?.Code.ToString(),
                    "truthy SDK error classification");
                Check(!frames.OfType<TextDelta>().Any() && ((StreamError)frames[^1]).Message.Content.IsEmpty,
                    "Truthy error dispatched text before rejection.");
            }
            var nonfinite = await Collect(["{\"error\":1e309," + fields + "}"], options);
            Check(nonfinite[^1] is StreamError, "Truthy branch bypassed strict finite-number admission.");
            Equal("MalformedStream", ((StreamError)nonfinite[^1]).NativeDiagnostic?.Code.ToString(),
                "strict numeric error envelope classification");

            using var permissive = JsonDocument.Parse("{\"error\":false," + fields + ",}", new JsonDocumentOptions { AllowTrailingCommas = true });
            var mapper = new OpenAICompletionsWireSource((_, token) => OwnedChunks([JsonData.FromElement(permissive.RootElement)], token: token), options);
            var malformed = new List<StreamEvent>();
            await foreach (var frame in mapper.StreamAsync(new(new("model", "openai-completions", "provider"), [], 123))) malformed.Add(frame);
            Check(malformed[^1] is StreamError && !malformed.OfType<StreamDone>().Any(), "Falsy error bypassed strict raw JSON reparsing.");
            Equal("MalformedStream", ((StreamError)malformed[^1]).NativeDiagnostic?.Code.ToString(),
                "strict malformed error envelope classification");
        }
    }

    private static Task ComparatorControls()
    {
        foreach (var (left, right) in new[] { ("{\"a\":null}", "{}"), ("[1,2]", "[2,1]"), ("1.00", "1"), ("-0", "0"), ("0.000039999999999999996", "0.000040") })
            Throws<InvalidOperationException>(() => Same(JsonData.Parse(left).Value, JsonData.Parse(right).Value, "/mutation"));
        Same(JsonData.Parse("{\"b\":2,\"a\":1}").Value, JsonData.Parse("{\"a\":1,\"b\":2}").Value, "/objectOrderOnly");
        return Task.CompletedTask;
    }

    private static async IAsyncEnumerable<JsonData> OwnedChunks(JsonData[] chunks, List<JsonData>? observed = null,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        await Task.CompletedTask;
        foreach (var chunk in chunks) { token.ThrowIfCancellationRequested(); observed?.Add(chunk); yield return chunk; }
    }
    private static async Task<List<StreamEvent>> Collect(string[] chunks, OpenAICompletionsWireOptions? options = null)
    {
        var mapper = new OpenAICompletionsWireSource((_, token) => OwnedChunks(chunks.Select(JsonData.Parse).ToArray(), token: token), options);
        var result = new List<StreamEvent>();
        await foreach (var frame in mapper.StreamAsync(new(new("model", "openai-completions", "provider"), [], 123))) result.Add(frame);
        return result;
    }
    private static byte[] Pinned(string repo, string path, long cap, string digest)
    {
        var full = Path.Combine(repo, path); var length = new FileInfo(full).Length;
        Check(length >= 0 && length <= cap, "Reference file exceeds admission cap: " + path);
        var bytes = File.ReadAllBytes(full); Check(bytes.Length <= cap && Convert.ToHexStringLower(SHA256.HashData(bytes)) == digest, "Frozen complete reference changed: " + path); return bytes;
    }
    private static void Same(JsonElement expected, JsonElement actual, string path)
    {
        if (expected.ValueKind != actual.ValueKind) throw new InvalidOperationException(path + ": JSON kind differs");
        if (expected.ValueKind == JsonValueKind.Object)
        {
            var left = expected.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var right = actual.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var leftFields = left.Keys.Order(StringComparer.Ordinal).ToArray();
            var rightFields = right.Keys.Order(StringComparer.Ordinal).ToArray();
            if (!leftFields.SequenceEqual(rightFields)) throw new InvalidOperationException(path + ": complete field set differs; expected=[" +
                string.Join(",", leftFields) + "]; actual=[" + string.Join(",", rightFields) + "]; expectedOnly=[" +
                string.Join(",", leftFields.Except(rightFields, StringComparer.Ordinal)) + "]; actualOnly=[" +
                string.Join(",", rightFields.Except(leftFields, StringComparer.Ordinal)) + "]");
            foreach (var (name, value) in left) Same(value, right[name], path + "/" + name);
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            Equal(expected.GetArrayLength(), actual.GetArrayLength(), path + " array count");
            for (var index = 0; index < expected.GetArrayLength(); index++) Same(expected[index], actual[index], path + "/" + index);
        }
        else if (expected.ValueKind == JsonValueKind.String) Equal(expected.GetString(), actual.GetString(), path);
        else Equal(expected.GetRawText(), actual.GetRawText(), path + " exact raw scalar");
    }
    private static void Equal<T>(T expected, T actual, string context) => Check(EqualityComparer<T>.Default.Equals(expected, actual),
        context + ": expected " + expected + "; actual " + actual);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
