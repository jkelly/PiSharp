// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/harness/tool.ts (ToolTask version 3) and
// packages/durable/src/harness/generation.ts (GenerationTask version 2).
// Authored Pi v1.5.0 expectations over the Pi v1.2.0 captures (decision 0013: fixtures are not recaptured; area tool-tasks, 1c6928b30). Nothing
// here is a capture, and a byte comparison with records an upstream v1.5.0 run wrote needs a recapture. v1.5.0 defines pi.tool at version 3
// (a call its request did not offer is flagged unoffered in its input) and pi.generation at version 2 (such a call waits flagged in a
// sequential round), so every tool and generation task record it writes carries the new version, where the v1.2.0 captures hold pi.tool
// version 2 and pi.generation version 1 (the v1.1.0 sessions, pi.tool version 1). Nothing else of their records changes in the captured
// scenarios: none calls a tool its request did not offer, and no afterTool hook sees a result the Harness wrote itself.
// - Written: a run writes every task itself, so each pi.tool and pi.generation record has the new version, in the records (Written) and in
//   the persisted JSON texts (WrittenText: JSONL lines, SQLite task records), where a task record prints {id, conversationId, kind,
//   version, ...}.
// - Resumed: the scheduler migrates a stored task when it reserves it, and its next write carries the definition's version; a task the
//   resumed session already held terminal is never reserved again and keeps the captured version. Resumed bumps the tasks that are live in
//   the records the resume started from (a capture's records.json, an upgrade's opened.json) or absent from them (created by the resume).
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
