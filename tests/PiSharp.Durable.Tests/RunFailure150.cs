// Pi f3255de76581447d17db78c9d6a2c5dbb9eee762 (MIT): packages/durable/src/harness/live.ts (endRun's runFailure).
// Authored Pi v1.5.0 expectations over the Pi v1.2.0 captures (decision 0013: fixtures are not recaptured; area run-failure). Nothing here
// is a capture, and a byte comparison with records an upstream v1.5.0 run wrote needs a recapture. v1.5.0's endRun records why a run ended
// without an answer in the commit that ends it: pi.live.runFailure = { taskId, inputs, reason, detail? }, set before `run` is removed, so
// a watch frame sets it before it removes run. The next run start removes it, unless that run starts in the same commit. v1.2.0 left
// pi.live empty of it. Each captured scenario whose newest run ends without an answer gets that run's failure, taken from the capture's own
// task, inputs and error, with the reason and detail harness-run-failure.test.ts and harness-generation.test.ts expect:
// - chat-turns noModel: the plain conversation's run fails with no_model, detail "No model is configured".
// - compaction blockingFailure: the blocking threshold compaction fails the run with model_error, detail "Compaction failed: <error>".
// - views abortedRound: the aborted generation's run, reason aborted, in the frame that ends it and in the final view.
// - compat compaction-failure: the blocking turn's run fails with model_error, detail "Compaction failed: Summarization failed: still bad";
//   the pi.live document's stored value (records, the JSONL document line and the SQLite revision) holds it.
// PiSharp-only, not upstream's: the agent event projection (PiSharp.Durable.Hosting's AgentEvents.cs; upstream removed watchEvents in f3255de76) carries the
// failure on run_end ("failure", the runFailure value), so the authored events add it to the run_end of each run above.
using PiSharp.Durable.Chord;

/// <summary>The authored v1.5.0 forms of v1.2.0 captures for pi.live.runFailure (see the file header).</summary>
internal static class RunFailure150
{
    /// <summary>A <c>pi.live.runFailure</c> value, keys in upstream's order.</summary>
    internal static JsonObjectValue Failure(long taskId, long[] inputs, string reason, string? detail = null)
    {
        var failure = JsonValue.Object(KeyValuePair.Create("taskId", (JsonValue)JsonValue.Number(taskId)), KeyValuePair.Create("inputs",
            (JsonValue)JsonValue.Array(inputs.Select(input => (JsonValue)JsonValue.Number(input)))), KeyValuePair.Create("reason", (JsonValue)reason));
        return detail is null ? failure : failure.SetItem("detail", detail);
    }

    /// <summary>chat-turns.upstream.json's authored form.</summary>
    internal static JsonObjectValue ChatTurns(JsonObjectValue capture)
    {
        var failure = Failure(9, [8], "no_model", "No model is configured");
        Expect(capture["noModel"], failure);
        return (JsonObjectValue)At(capture, ["noModel", "conversations", "1", "docs", "pi.live"], live => ((JsonObjectValue)live).SetItem("runFailure", failure));
    }

    /// <summary>compaction.upstream.json's authored form.</summary>
    internal static JsonObjectValue Compaction(JsonObjectValue capture)
    {
        var failure = Failure(22, [21], "model_error", "Compaction failed: Summarization failed: bad request");
        Expect(((JsonObjectValue)capture["blockingFailure"])["done"], failure);
        return (JsonObjectValue)At(capture, ["blockingFailure", "done", "conversations", "1", "docs", "pi.live"],
            live => ((JsonObjectValue)live).SetItem("runFailure", failure));
    }

    /// <summary>views.upstream.json's authored form: the frame that removes the run sets runFailure first, the final view holds it, and
    /// the run's run_end event carries it.</summary>
    internal static JsonObjectValue Views(JsonObjectValue capture)
    {
        var failure = Failure(9, [8], "aborted");
        var setOp = JsonValue.Array("s", JsonValue.Array("docs", "pi.live", "runFailure"), failure);
        var removeRun = JsonValue.Array("d", JsonValue.Array("docs", "pi.live", "run"));
        var scenario = (JsonObjectValue)capture["abortedRound"];
        var frames = (JsonArrayValue)scenario["frames"];
        var ending = frames.Select((frame, index) => (frame, index)).Last(each => ((JsonArrayValue)each.frame).Any(op => JsonValue.DeepEquals(op, removeRun))).index;
        var authoredFrames = JsonValue.Array(frames.Select((frame, index) => index != ending ? frame
            : JsonValue.Array(((JsonArrayValue)frame).SelectMany(op => JsonValue.DeepEquals(op, removeRun) ? new JsonValue[] { setOp, op } : [op]))));
        scenario = scenario.SetItem("frames", authoredFrames);
        scenario = (JsonObjectValue)At(scenario, ["final", "docs", "pi.live"], live => ((JsonObjectValue)live).SetItem("runFailure", failure));
        scenario = scenario.SetItem("batches", RunEnds(scenario["batches"], failure));
        return capture.SetItem("abortedRound", scenario);
    }

    /// <summary>The authored forms for compat scenario <paramref name="scenario"/>'s capture (records, events, observed values, persisted
    /// texts); the identity for a scenario whose newest run answered.</summary>
    internal static Plan For(string scenario) => scenario switch
    {
        "compaction-failure" => new Plan(1, Failure(27, [26], "model_error", "Compaction failed: Summarization failed: still bad"),
            """{"compactionFailure":{"taskId":28,"reason":"threshold","error":"Summarization failed: still bad"}}"""),
        _ => new Plan(0, null, null),
    };

    /// <summary>A compat scenario's authored forms: conversation <c>Conversation</c>'s pi.live document gets <c>Failure</c>, and the run's
    /// run_end event carries it. <c>CapturedLive</c> is the document's captured value text, which a persisted text holds as is.</summary>
    internal sealed record Plan(long Conversation, JsonObjectValue? Failure, string? CapturedLive)
    {
        /// <summary>The authored form of a captured JSON value.</summary>
        public JsonValue Authored(JsonValue capture) => Failure is null ? capture : RunEnds(Documents(capture), Failure);

        /// <summary>The authored form of a persisted JSON text: the pi.live value text gains the failure, the last key.</summary>
        public string AuthoredText(string text) => Failure is null || CapturedLive is null || !text.Contains(CapturedLive, StringComparison.Ordinal) ? text
            : text.Replace(CapturedLive, AuthoredLive().ToJson(), StringComparison.Ordinal);

        private JsonObjectValue AuthoredLive() => ((JsonObjectValue)JsonValue.Parse(CapturedLive!)).SetItem("runFailure", Failure!);

        private JsonValue Documents(JsonValue value) => value switch
        {
            JsonArrayValue items => JsonValue.Array(items.Select(Documents)),
            JsonObjectValue document when IsLive(document) =>
                document.SetItem("value", ((JsonObjectValue)document["value"]).SetItem("runFailure", Failure!)),
            JsonObjectValue properties => JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, Documents(property.Value)))),
            _ => value,
        };

        private bool IsLive(JsonObjectValue document) =>
            document.TryGetValue("record", out var record) && record is JsonObjectValue fields && document.ContainsKey("value") &&
            fields.TryGetValue("kind", out var kind) && kind is JsonStringValue { Value: "pi.live" } &&
            fields.TryGetValue("scope", out var scope) && scope is JsonObjectValue scopeFields &&
            scopeFields.TryGetValue("conversationId", out var id) && id is JsonNumberValue number && number.Value == Conversation;
    }

    /// <summary>Every run_end event of the failed run's inputs, with the failure (PiSharp's event projection; see the file header).</summary>
    private static JsonValue RunEnds(JsonValue value, JsonObjectValue failure) => value switch
    {
        JsonArrayValue items => JsonValue.Array(items.Select(item => RunEnds(item, failure))),
        JsonObjectValue runEnd when runEnd.TryGetValue("type", out var type) && type is JsonStringValue { Value: "run_end" } &&
            runEnd.TryGetValue("inputs", out var inputs) && JsonValue.DeepEquals(inputs, failure["inputs"]) => runEnd.SetItem("failure", failure),
        JsonObjectValue properties => JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, RunEnds(property.Value, failure)))),
        _ => value,
    };

    /// <summary><paramref name="value"/> with the value at <paramref name="path"/> changed.</summary>
    private static JsonValue At(JsonValue value, ReadOnlySpan<string> path, Func<JsonValue, JsonValue> change)
    {
        if (path.IsEmpty) return change(value);
        var properties = (JsonObjectValue)value;
        return properties.SetItem(path[0], At(properties[path[0]], path[1..], change));
    }

    /// <summary>The capture holds the failed run: its generation task did not complete and its inputs went unanswered.</summary>
    private static void Expect(JsonValue dump, JsonObjectValue failure)
    {
        var fields = (JsonObjectValue)dump;
        var task = ((JsonArrayValue)fields["tasks"]).OfType<JsonObjectValue>()
            .Single(each => JsonValue.DeepEquals(each["id"], failure["taskId"]));
        var outcome = ((JsonObjectValue)((JsonObjectValue)task["state"])["outcome"])["status"];
        if (task["kind"] is not JsonStringValue { Value: "pi.generation" } || outcome is JsonStringValue { Value: "completed" })
            throw new InvalidOperationException($"the capture's task {failure["taskId"].ToJson()} is not a failed run");
        foreach (var input in (JsonArrayValue)failure["inputs"])
        {
            var submission = ((JsonArrayValue)fields["submissions"]).OfType<JsonObjectValue>().Single(each => JsonValue.DeepEquals(each["id"], input));
            if (submission["status"] is not JsonStringValue { Value: "unanswered" })
                throw new InvalidOperationException($"the capture's submission {input.ToJson()} is not unanswered");
        }
    }
}
