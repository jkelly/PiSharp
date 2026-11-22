// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/src/harness/tool.ts (settle: a result's durationMs; startNestedCall:
// a nested call document's kind).
// Authored Pi v1.6.0 expectations over the Pi v1.2.0 captures (decision 0013: fixtures are not recaptured). Nothing here is a capture.
// v1.6.0 measures a tool result's durationMs in the settling commit, as the span from the tool task's first run to its result on the
// Harness clock, so every result a tool task writes carries one, also those of calls that never executed (unavailable, invalid, blocked,
// interrupted, aborted, abandoned), where v1.2.0 measured only an executed call's last attempt and left the others without. So where a
// capture holds a result of a tool task without durationMs, a v1.6.0 run writes one:
// - a pi.tool-result entry appended by a pi.tool task (its byTaskId names one): its toolResult message, before `timestamp`
//   (appendToolResult's key order);
// - a nested call's stored result {taskId, structuredOutput?, isError, details?, diagnostics, usage?}: last, as nestedResult builds it;
// - a nested slot's summary {isError, usage?, error?}: after isError, as summaryOf builds it;
// - a toolResult message outside an entry (an agent event's), when the caller says the dump has no task list to decide by.
// Results the generation writes itself (the calls an aborted round never started) and results context derivation synthesizes get none,
// in either version. The value is a placeholder: the comparisons normalize durations (CompatTests.Normalize; "<time>" in the
// differential captures, whose placeholder the caller passes).
// - Written: a run writes every result itself.
// - Resumed: a resume writes only the results it adds; those the resumed session already held keep their captured form (entries by ID,
//   nested results by task).
// - Events: the agent events of a phase. Those of a session a v1.6.0 run wrote take Written with every toolResult message; those of a
//   resume of a captured session keep their first event, the snapshot of what the capture held, and take it from the second on.
// v1.6.0 also marks each member of pi.tool.nested-call with its kind (a nested call, or a task created under the key): a document the
// run or the resume creates holds {kind: "nested", taskId, result?} where the capture holds {taskId, result?}; one the resumed session
// already held keeps its captured form. A persisted text (a JSONL line, a SQLite content column) shows such a document only as its base
// value, {taskId}, which nothing else stores: WrittenText gives that value its kind.
// A dump without task records (view frames, events) cannot name the pi.tool tasks; there the generations are the tasks that appended a
// pi.assistant entry, and every other task that appended a pi.tool-result entry is a tool task. In a list of events, a message_start
// event carries the message of the message_end event after it, whose entry says who appended it: it gains the duration with that entry.
using PiSharp.Durable.Chord;

/// <summary>The authored v1.6.0 forms of v1.2.0 captures for the durationMs of tool results (see the file header).</summary>
internal static class Durations160
{
    private static readonly JsonValue Number = JsonValue.Number(0);

    /// <summary>What a v1.6.0 run writes where <paramref name="capture"/> holds what v1.2.0's wrote.</summary>
    public static JsonValue Written(JsonValue capture, JsonValue? placeholder = null, bool everyMessage = false) =>
        new Author(ToolTasks(capture), _ => true, _ => true, _ => true, placeholder ?? Number, everyMessage, false).Map(capture, null);

    /// <summary><see cref="Written"/> for the parsed JSON of one persisted text (see the file header).</summary>
    public static JsonValue WrittenText(JsonValue text) =>
        new Author(ToolTasks(text), _ => true, _ => true, _ => true, Number, false, true).Map(text, "value");

    /// <summary>The events of a session a v1.6.0 run wrote, every phase (see the file header).</summary>
    public static JsonValue WrittenEvents(JsonValue events) => Written(events, everyMessage: true);

    /// <summary>The events of a resume of a captured session: phase <paramref name="phase"/> of <paramref name="events"/> from its second
    /// event on.</summary>
    public static JsonObjectValue ResumedEvents(JsonObjectValue events, string phase)
    {
        if (!events.TryGetValue(phase, out var listed) || listed is not JsonArrayValue list || list.Count == 0) return events;
        return events.SetItem(phase, JsonValue.Array([list[0], .. list.Skip(1).Select(item => Written(item, everyMessage: true))]));
    }

    /// <summary>What a v1.6.0 resume writes where <paramref name="resumed"/> holds what v1.2.0's did, resuming a session that held
    /// <paramref name="before"/>.</summary>
    public static JsonValue Resumed(JsonValue resumed, JsonValue before, JsonValue? placeholder = null)
    {
        var entries = new HashSet<long>();
        var results = new HashSet<long>();
        var documents = new HashSet<long>();
        Collect(before, entries, results, documents);
        return new Author(ToolTasks(resumed), id => !entries.Contains(id), id => !results.Contains(id), id => !documents.Contains(id),
            placeholder ?? Number, false, false).Map(resumed, null);
    }

    /// <summary><see cref="Written"/> for every scenario of a differential capture, whose durations are the placeholder "&lt;time&gt;".</summary>
    public static JsonObjectValue Differential(JsonObjectValue capture) =>
        JsonValue.Object(capture.Select(scenario => KeyValuePair.Create(scenario.Key, Written(scenario.Value, "<time>"))));

    /// <summary>Whether a task is a pi.tool task, judged from <paramref name="value"/>: by its task records when it lists any, else every
    /// task that appended no pi.assistant entry (see the file header).</summary>
    private static Func<long, bool> ToolTasks(JsonValue value)
    {
        HashSet<long>? tools = null;
        var generations = new HashSet<long>();
        void Walk(JsonValue each)
        {
            switch (each)
            {
                case JsonArrayValue items:
                    foreach (var item in items) Walk(item);
                    break;
                case JsonObjectValue task when task.TryGetValue("kind", out var kind) && kind is JsonStringValue { Value: var name } &&
                    task.ContainsKey("version") && task.ContainsKey("conversationId") && task.TryGetValue("id", out var id) && id is JsonNumberValue number:
                    tools ??= [];
                    if (name == "pi.tool") tools.Add((long)number.Value);
                    break;
                case JsonObjectValue properties:
                    if (properties.TryGetValue("kind", out var entryKind) && entryKind is JsonStringValue { Value: "pi.assistant" } &&
                        properties.TryGetValue("byTaskId", out var by) && by is JsonNumberValue writer) generations.Add((long)writer.Value);
                    foreach (var property in properties) Walk(property.Value);
                    break;
            }
        }
        Walk(value);
        return tools is not null ? tools.Contains : id => !generations.Contains(id);
    }

    private static void Collect(JsonValue value, HashSet<long> entries, HashSet<long> results, HashSet<long> documents)
    {
        switch (value)
        {
            case JsonArrayValue items:
                foreach (var item in items) Collect(item, entries, results, documents);
                break;
            case JsonObjectValue properties:
                if (IsResultEntry(properties) && properties.TryGetValue("id", out var id) && id is JsonNumberValue entry) entries.Add((long)entry.Value);
                if (IsNestedResult(properties)) results.Add((long)((JsonNumberValue)properties["taskId"]).Value);
                if (NestedCallDocument(properties) is { } document) documents.Add(document);
                foreach (var property in properties) Collect(property.Value, entries, results, documents);
                break;
        }
    }

    /// <summary>The record ID of a dumped pi.tool.nested-call document ({record: {id, kind, ...}, version, value}); null for anything
    /// else.</summary>
    private static long? NestedCallDocument(JsonObjectValue value) =>
        value.TryGetValue("record", out var record) && record is JsonObjectValue fields &&
        fields.TryGetValue("kind", out var kind) && kind is JsonStringValue { Value: "pi.tool.nested-call" } &&
        fields.TryGetValue("id", out var id) && id is JsonNumberValue number && value.TryGetValue("value", out var held) && held is JsonObjectValue
            ? (long)number.Value
            : null;

    private static bool IsResultEntry(JsonObjectValue value) =>
        value.TryGetValue("kind", out var kind) && kind is JsonStringValue { Value: "pi.tool-result" } && value.TryGetValue("model", out var model) &&
        model is JsonArrayValue;

    private static bool IsToolResult(JsonObjectValue value) =>
        value.TryGetValue("role", out var role) && role is JsonStringValue { Value: "toolResult" } && value.ContainsKey("toolCallId");

    /// <summary>A nested call's stored result: {taskId, isError, diagnostics, ...}, nothing but nestedResult's fields.</summary>
    private static bool IsNestedResult(JsonObjectValue value) =>
        value.TryGetValue("taskId", out var id) && id is JsonNumberValue && value.TryGetValue("isError", out var error) && error is JsonBooleanValue &&
        value.TryGetValue("diagnostics", out var diagnostics) && diagnostics is JsonArrayValue &&
        value.Keys.All(key => key is "taskId" or "structuredOutput" or "isError" or "details" or "diagnostics" or "usage" or "durationMs");

    private sealed class Author(Func<long, bool> toolTask, Func<long, bool> entryAdded, Func<long, bool> resultAdded, Func<long, bool> documentAdded,
        JsonValue placeholder, bool everyMessage, bool texts)
    {
        public JsonValue Map(JsonValue value, string? key) => value switch
        {
            JsonArrayValue items => Started(items.Select(item => Map(item, null)).ToList()),
            JsonObjectValue document when NestedCallDocument(document) is { } id => Document(document, id),
            // The base value of a nested call's document in a persisted text: {taskId} alone.
            JsonObjectValue { Count: 1 } member when texts && key == "value" && member.TryGetValue("taskId", out var created) && created is JsonNumberValue =>
                JsonValue.Object([KeyValuePair.Create("kind", (JsonValue)"nested"), .. member]),
            JsonObjectValue entry when IsResultEntry(entry) => Entry(entry),
            JsonObjectValue message when everyMessage && IsToolResult(message) => Message(message),
            JsonObjectValue result when IsNestedResult(result) => Result(result),
            JsonObjectValue summary when key == "summary" && summary.TryGetValue("isError", out var error) && error is JsonBooleanValue => Summary(summary),
            JsonObjectValue properties => JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, Map(property.Value, property.Key)))),
            _ => value,
        };

        /// <summary>A pi.tool-result entry: its message gains a duration when a pi.tool task appended it (every task, when the dump lists
        /// none) and the comparison says it is new.</summary>
        private JsonValue Entry(JsonObjectValue entry)
        {
            var byTool = entry.TryGetValue("byTaskId", out var by) && by is JsonNumberValue task && toolTask((long)task.Value);
            var added = entry.TryGetValue("id", out var id) && id is JsonNumberValue number && entryAdded((long)number.Value);
            if (!byTool || !added) return entry;
            return entry.SetItem("model", JsonValue.Array(((JsonArrayValue)entry["model"]).Select(message =>
                message is JsonObjectValue fields && IsToolResult(fields) ? Message(fields) : message)));
        }

        /// <summary>A pi.tool.nested-call document: its value starts with kind "nested" when this run or resume created it, and its result
        /// gains a duration as any nested result does.</summary>
        private JsonValue Document(JsonObjectValue document, long id)
        {
            var value = (JsonObjectValue)document["value"];
            var mapped = (JsonObjectValue)JsonValue.Object(value.Select(property => KeyValuePair.Create(property.Key, Map(property.Value, property.Key))));
            if (documentAdded(id) && !mapped.ContainsKey("kind") && mapped.ContainsKey("taskId"))
                mapped = JsonValue.Object([KeyValuePair.Create("kind", (JsonValue)"nested"), .. mapped]);
            return document.SetItem("value", mapped);
        }

        /// <summary>A list whose message_start events take the duration of the message_end entry that follows them.</summary>
        private JsonValue Started(List<JsonValue> items)
        {
            for (var index = 0; index + 1 < items.Count; index++)
            {
                if (items[index] is not JsonObjectValue start || !IsEvent(start, "message_start") ||
                    !start.TryGetValue("message", out var carried) || carried is not JsonObjectValue message || !IsToolResult(message)) continue;
                if (items[index + 1] is not JsonObjectValue end || !IsEvent(end, "message_end") ||
                    !end.TryGetValue("entry", out var appended) || appended is not JsonObjectValue entry || !IsResultEntry(entry)) continue;
                if (((JsonArrayValue)entry["model"]).OfType<JsonObjectValue>().Any(written => written.ContainsKey("durationMs")))
                    items[index] = start.SetItem("message", Message(message));
            }
            return JsonValue.Array(items);
        }

        private static bool IsEvent(JsonObjectValue value, string type) =>
            value.TryGetValue("type", out var named) && named is JsonStringValue { Value: var name } && name == type;

        private JsonValue Message(JsonObjectValue message) =>
            message.ContainsKey("durationMs") ? message : Insert(message, "timestamp", before: true);

        private JsonValue Result(JsonObjectValue result) =>
            result.ContainsKey("durationMs") || !resultAdded((long)((JsonNumberValue)result["taskId"]).Value)
                ? result
                : JsonValue.Object([.. result, KeyValuePair.Create("durationMs", placeholder)]);

        private JsonValue Summary(JsonObjectValue summary) => summary.ContainsKey("durationMs") ? summary : Insert(summary, "isError", before: false);

        /// <summary><paramref name="value"/> with durationMs before or after <paramref name="anchor"/>; last when it has no such key.</summary>
        private JsonObjectValue Insert(JsonObjectValue value, string anchor, bool before)
        {
            var properties = new List<KeyValuePair<string, JsonValue>>();
            var placed = false;
            foreach (var property in value)
            {
                if (property.Key == anchor && before) { properties.Add(KeyValuePair.Create("durationMs", placeholder)); placed = true; }
                properties.Add(property);
                if (property.Key == anchor && !before) { properties.Add(KeyValuePair.Create("durationMs", placeholder)); placed = true; }
            }
            if (!placed) properties.Add(KeyValuePair.Create("durationMs", placeholder));
            return JsonValue.Object(properties);
        }
    }
}
