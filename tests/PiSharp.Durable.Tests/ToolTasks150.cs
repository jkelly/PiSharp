// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/harness/tool.ts (ToolTask version 3) and
// packages/durable/src/harness/generation.ts (GenerationTask version 2).
// Authored Pi v1.5.0 expectations over the Pi v1.2.0 captures (decision 0013: fixtures are not recaptured; area tool-tasks, 1c6928b30). Nothing
// here is a capture, and a byte comparison with records an upstream v1.5.0 run wrote needs a recapture. v1.5.0 defines pi.tool at version 3
// (a call its request did not offer is flagged unoffered in its input) and pi.generation at version 2 (such a call waits flagged in a
// sequential round), so every tool and generation task record it writes carries the new version, where the v1.2.0 captures hold pi.tool
// version 2 and pi.generation version 1 (the v1.1.0 sessions, pi.tool version 1). Nothing else of the compat scenarios' records changes: none
// calls a tool its request did not offer, and no afterTool hook sees a result the Harness wrote itself. Two differential scenarios do call
// one (Differential).
// - Written: a run writes every task itself, so each pi.tool and pi.generation record has the new version, in the records (Written) and in
//   the persisted JSON texts (WrittenText: JSONL lines, SQLite task records), where a task record prints {id, conversationId, kind,
//   version, ...}.
// - Resumed: the scheduler migrates a stored task when it reserves it, and its next write carries the definition's version; a task the
//   resumed session already held terminal is never reserved again and keeps the captured version. Resumed bumps the tasks that are live in
//   the records the resume started from (a capture's records.json, an upgrade's opened.json) or absent from them (created by the resume).
// - Differential: the Harness differential captures (chat turns, compaction, inbox, tool rounds) take Written, and the two scenarios whose
//   round calls a tool its request did not offer (chat turns' unavailable, tool rounds' round) take UnofferedFirstCall first: the call's
//   tool_unavailable result, which v1.2.0 appended in the round's commit, comes from the call's own pi.tool task (1c6928b30), which takes
//   the result's ID; the result and every later record move up one ID.
using PiSharp.Durable.Chord;

/// <summary>The authored v1.5.0 forms of v1.2.0 captures for the task versions of pi.tool and pi.generation (see the file header).</summary>
internal static class ToolTasks150
{
    /// <summary>The definitions' versions in v1.5.0, by task kind.</summary>
    private static readonly Dictionary<string, int> Versions = new(StringComparer.Ordinal) { ["pi.tool"] = 3, ["pi.generation"] = 2 };

    /// <summary>What a v1.5.0 run writes where <paramref name="capture"/> holds what v1.2.0's wrote: every pi.tool and pi.generation task
    /// record at the new version.</summary>
    public static JsonValue Written(JsonValue capture) => Map(capture, _ => true);

    /// <summary><see cref="Written"/> over persisted JSON text (a JSONL line, a SQLite column). A text that changes must print back as
    /// captured first, so nothing else in it moves.</summary>
    public static string WrittenText(string text)
    {
        if (text.Length == 0 || text[0] is not ('{' or '[') || !Versions.Keys.Any(kind => text.Contains($"\"kind\":\"{kind}\",\"version\":", StringComparison.Ordinal)))
            return text;
        var value = JsonValue.Parse(text);
        var authored = Written(value).ToJson();
        if (authored == value.ToJson()) return text;
        if (value.ToJson() != text) throw new InvalidOperationException($"a captured JSON text does not print back as captured: {text[..Math.Min(200, text.Length)]}");
        return authored;
    }

    /// <summary>What a v1.5.0 resume writes where <paramref name="resumed"/> holds what v1.2.0's did, resuming a session that held
    /// <paramref name="before"/>: the tasks the resume reserved (live in <paramref name="before"/>) or created (absent from it) at the new
    /// version, the others as captured.</summary>
    public static JsonValue Resumed(JsonValue resumed, JsonValue before)
    {
        var held = new Dictionary<long, bool>();
        Collect(before, held);
        return Map(resumed, id => !held.TryGetValue(id, out var live) || live);
    }

    /// <summary>A differential capture's authored v1.5.0 form (see the file header): <see cref="Written"/> over every scenario, after
    /// <see cref="UnofferedFirstCall"/> for each of <paramref name="unoffered"/>'s (scenario, call ID) pairs.</summary>
    public static JsonObjectValue Differential(JsonObjectValue capture, params (string Scenario, string CallId)[] unoffered)
    {
        foreach (var (scenario, callId) in unoffered)
            capture = capture.SetItem(scenario, UnofferedFirstCall((JsonObjectValue)capture[scenario], callId));
        return (JsonObjectValue)Written(capture);
    }

    /// <summary>The ID keys of a records dump (entries, tasks, submissions and their references).</summary>
    private static readonly HashSet<string> IdKeys = new(StringComparer.Ordinal)
        { "id", "byTaskId", "owner", "entryId", "assistant", "entry", "answer", "taskId", "parent" };

    /// <summary>What v1.5.0 writes where <paramref name="records"/> holds what v1.2.0 wrote for a round whose call
    /// <paramref name="callId"/>, to a tool its request did not offer, is its only call or the first of a sequential round (the caller
    /// knows which). v1.2.0 appended that call's tool_unavailable result in the round's commit, by the generation; v1.5.0's startToolRound
    /// creates the call's pi.tool task there instead (the first call of a round always gets its task in that commit), flagged unoffered,
    /// owned by the generation, and the task appends the result when it runs, before anything else of the round is minted. So the task
    /// takes the result's ID, the result the next one (by the task), and every later ID moves up by one. Task versions are left to
    /// <see cref="Written"/>.</summary>
    private static JsonObjectValue UnofferedFirstCall(JsonObjectValue records, string callId)
    {
        var entries = records["conversations"] is JsonObjectValue conversations
            ? conversations.SelectMany(conversation => (JsonArrayValue)((JsonObjectValue)conversation.Value)["entries"]).OfType<JsonObjectValue>().ToList()
            : throw new InvalidOperationException("records without conversations");
        var result = entries.Single(entry => entry["kind"] is JsonStringValue { Value: "pi.tool-result" } &&
            ((JsonObjectValue)((JsonArrayValue)entry["model"])[0])["toolCallId"] is JsonStringValue { Value: var id } && id == callId);
        var at = (long)((JsonNumberValue)result["id"]).Value;
        var generation = (long)((JsonNumberValue)result["byTaskId"]).Value;
        var assistant = entries.Single(entry => entry["kind"] is JsonStringValue { Value: "pi.assistant" } &&
            ((JsonArrayValue)((JsonObjectValue)((JsonArrayValue)entry["model"])[0])["content"]).OfType<JsonObjectValue>()
                .FirstOrDefault(part => part["type"] is JsonStringValue { Value: "toolCall" }) is { } first &&
            first["id"] is JsonStringValue { Value: var id } && id == callId);
        // The captured result is the generation's, in the commit of the answer that made the call.
        if (!assistant.TryGetValue("byTaskId", out var by) || by is not JsonNumberValue { Value: var answerer } || (long)answerer != generation ||
            !((JsonArrayValue)records["tasks"]).OfType<JsonObjectValue>().Any(task => task["id"] is JsonNumberValue { Value: var taskId } &&
                (long)taskId == generation && task["kind"] is JsonStringValue { Value: "pi.generation" }))
            throw new InvalidOperationException($"call {callId}'s captured result is not its generation's");
        var shifted = (JsonObjectValue)Shift(records, at);
        // A task record's key order (TaskRecord), and createToolTask's input: {kind, assistant, callId, unoffered}.
        static KeyValuePair<string, JsonValue> P(string key, JsonValue value) => KeyValuePair.Create(key, value);
        var task = JsonValue.Object([
            P("id", at), P("conversationId", result["conversationId"]), P("kind", "pi.tool"), P("version", 3),
            P("input", JsonValue.Object([P("kind", "model"), P("assistant", assistant["id"]), P("callId", callId), P("unoffered", true)])),
            P("owner", generation), P("background", false), P("abortRequested", false),
            P("state", JsonValue.Object([P("status", "terminal"), P("outcome", JsonValue.Object([P("status", "completed"),
                P("result", JsonValue.Object([P("kind", "model"), P("entryId", at + 1)]))]))])),
        ]);
        var tasks = ((JsonArrayValue)shifted["tasks"]).ToList();
        tasks.Insert(tasks.FindIndex(each => ((JsonNumberValue)((JsonObjectValue)each)["id"]).Value > at) is var index and >= 0 ? index : tasks.Count, task);
        shifted = shifted.SetItem("tasks", JsonValue.Array(tasks));
        return (JsonObjectValue)Moved(shifted, at + 1, at);
    }

    /// <summary><paramref name="value"/> with every ID at or after <paramref name="from"/> one higher.</summary>
    private static JsonValue Shift(JsonValue value, long from, string? key = null) => value switch
    {
        JsonArrayValue items => JsonValue.Array(items.Select(item => Shift(item, from))),
        JsonObjectValue properties => JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, Shift(property.Value, from, property.Key)))),
        JsonNumberValue { Value: var number } when key is not null && IdKeys.Contains(key) && number >= from => number + 1,
        _ => value,
    };

    /// <summary><paramref name="value"/> with entry <paramref name="entry"/> appended by task <paramref name="task"/>.</summary>
    private static JsonValue Moved(JsonValue value, long entry, long task) => value switch
    {
        JsonArrayValue items => JsonValue.Array(items.Select(item => Moved(item, entry, task))),
        JsonObjectValue record when record.TryGetValue("kind", out var kind) && kind is JsonStringValue { Value: "pi.tool-result" } &&
            record["id"] is JsonNumberValue { Value: var id } && (long)id == entry => record.SetItem("byTaskId", task),
        JsonObjectValue properties => JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, Moved(property.Value, entry, task)))),
        _ => value,
    };

    private static JsonValue Map(JsonValue value, Func<long, bool> bump) => value switch
    {
        JsonArrayValue items => JsonValue.Array(items.Select(item => Map(item, bump))),
        JsonObjectValue task when IsTask(task, out var id, out var version) && version is { } next && bump(id) => task.SetItem("version", next),
        JsonObjectValue properties => JsonValue.Object(properties.Select(property => KeyValuePair.Create(property.Key, Map(property.Value, bump)))),
        _ => value,
    };

    private static void Collect(JsonValue value, Dictionary<long, bool> held)
    {
        switch (value)
        {
            case JsonArrayValue items:
                foreach (var item in items) Collect(item, held);
                break;
            case JsonObjectValue task when IsTask(task, out var id, out _):
                held[id] = Restarts150.IsRunning(task) || task["state"] is JsonObjectValue { } state &&
                    state["status"] is JsonStringValue { Value: "pending" or "waiting" or "sleeping" };
                break;
            case JsonObjectValue properties:
                foreach (var property in properties) Collect(property.Value, held);
                break;
        }
    }

    /// <summary>Whether <paramref name="value"/> is a pi.tool or pi.generation task record ({id, conversationId, kind, version, ...}), with
    /// its id and the version v1.5.0 writes it at.</summary>
    private static bool IsTask(JsonObjectValue value, out long id, out int? version)
    {
        id = 0;
        version = null;
        if (!value.TryGetValue("kind", out var kind) || kind is not JsonStringValue { Value: var name } || !Versions.TryGetValue(name, out var current) ||
            !value.TryGetValue("version", out var stored) || stored is not JsonNumberValue ||
            !value.TryGetValue("id", out var number) || number is not JsonNumberValue { Value: var raw } || !value.ContainsKey("conversationId"))
            return false;
        id = (long)raw;
        version = current;
        return true;
    }
}
