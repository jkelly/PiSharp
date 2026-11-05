// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/harness/scheduler.ts (open's restart count, join) and
// packages/durable/src/session/session.ts (writeAtClose).
// Authored Pi v1.5.0 expectations over the Pi v1.2.0 captures (decision 0013: fixtures are not recaptured; area restarts, b3a29f111). Nothing
// here is a capture, and a byte comparison with records an upstream v1.5.0 run wrote needs a recapture. Two changes of v1.5.0 reach the
// captured scenarios' records:
// - Open counts a restart of each task it finds running (TaskRecord.restarts): `{ ...withState(record, pending), restarts: n + 1 }`, so the
//   count is the record's last key. A capture's opened.json (upstream's own open and close, without a resume) holds each task its
//   records.json holds as running as pending without it; Opened adds "restarts": 1 to those.
// - A close is not a crash: the scheduler's join writes each task it stopped back as pending, uncounted, keeping its checkpoint, in one
//   commit on the line after the commits admitted before the seal and before the held documents are stored (session.ts writeAtClose, then
//   storeHolds). Every captured scenario that ends with running tasks ends them so, by a clean close, so Closed turns each running task of a
//   records.json into a pending one, and DeferredCommits150's plan adds the commit (one task write per stopped task, in the order the
//   invocations started, which is task id order in every capture) before its close commit. The resume of such a written session finds the
//   tasks pending: its open writes nothing, where upstream's v1.2.0 resume reconciled them in a commit of its own, so the seqs after it line
//   up again.
// And a third reaches the resume of a capture that left a generation's request in flight (durable.interrupted-request): the resume no
// longer resends it but charges the attempt (generation.ts interrupted()), committing pi.live.generation.retry with the backoff, and the
// retry phase drops it as the next attempt starts. The records it ends with are the captured ones (the retry and the next attempt's prepare
// leave nothing in them), but the events projection shows the backoff: Charged puts its auto_retry_start and auto_retry_end right after the
// resume's snapshot. The compat resumes retry at once (CompatScenarios.cs: a backoff never ends on their frozen clock), so its `at` is the
// clock.
using PiSharp.Durable.Chord;

/// <summary>The authored v1.5.0 forms of v1.2.0 captures for restart counting (see the file header).</summary>
internal static class Restarts150
{
    /// <summary>The ids of the tasks <paramref name="records"/> (a Dump, or one backend's part of a capture's JSON) holds as running, in id
    /// order.</summary>
    public static List<long> Running(JsonValue records) =>
        [.. Tasks(records).Where(IsRunning).Select(task => (long)((JsonNumberValue)task["id"]).Value).Order()];

    /// <summary>What a v1.5.0 open and close writes where <paramref name="opened"/> holds what v1.2.0's did, over a session that held
    /// <paramref name="records"/>: the tasks it found running count a restart.</summary>
    public static JsonValue Opened(JsonValue opened, JsonValue records)
    {
        var running = Running(records).ToHashSet();
        if (running.Count == 0) return opened;
        return MapTasks(opened, task => running.Contains((long)((JsonNumberValue)task["id"]).Value) ? task.Remove("restarts").SetItem("restarts", 1) : task);
    }

    /// <summary>What a v1.5.0 run that closed cleanly leaves where <paramref name="records"/> holds what v1.2.0's did: the tasks its close
    /// stopped are pending again, with their checkpoints.</summary>
    public static JsonValue Closed(JsonValue records) => Running(records).Count == 0 ? records : MapTasks(records, task => IsRunning(task) ? Pending(task) : task);

    /// <summary>A running task record as the close writes it back (scheduler.ts join: <c>withState(record, { status: "pending",
    /// checkpoint })</c>): its state replaced in place, every other key kept.</summary>
    public static JsonObjectValue Pending(JsonObjectValue task)
    {
        var state = (JsonObjectValue)task["state"];
        return task.SetItem("state", JsonValue.Object(
            KeyValuePair.Create("status", (JsonValue)"pending"),
            KeyValuePair.Create("checkpoint", state["checkpoint"])));
    }

    /// <summary>A persisted record text (a SQLite <c>tasks.record</c> column) as the close writes it back, when it is a running task's.</summary>
    public static string PendingText(string text)
    {
        var value = (JsonObjectValue)JsonValue.Parse(text);
        if (value.ToJson() != text) throw new InvalidOperationException($"a captured task record does not print back as captured: {text[..Math.Min(200, text.Length)]}");
        return IsRunning(value) ? Pending(value).ToJson() : text;
    }

    /// <summary>The resume events of <paramref name="events"/> (an events.json part) with the backoff of each generation request
    /// <paramref name="records"/> left in flight, charged at <paramref name="at"/>: auto_retry_start and auto_retry_end after the snapshot.</summary>
    public static JsonValue Charged(JsonValue events, JsonValue records, double at)
    {
        var properties = (JsonObjectValue)events;
        if (!properties.TryGetValue("resume", out var resume)) return events;
        var charged = Tasks(records).Where(task => IsRunning(task) && task["kind"] is JsonStringValue { Value: "pi.generation" } &&
                ((JsonObjectValue)((JsonObjectValue)task["state"])["checkpoint"])["phase"] is JsonStringValue { Value: "request" })
            .Select(task => (long)((JsonNumberValue)((JsonObjectValue)((JsonObjectValue)task["state"])["checkpoint"])["attempt"]).Value).ToList();
        if (charged.Count == 0) return events;
        if (charged.Count > 1) throw new InvalidOperationException("a capture with more than one generation request in flight (area restarts)");
        var attempt = charged[0];
        var items = ((JsonArrayValue)resume).ToList();
        items.InsertRange(1,
        [
            JsonValue.Object(
                KeyValuePair.Create("type", (JsonValue)"auto_retry_start"),
                KeyValuePair.Create("attempt", (JsonValue)attempt),
                KeyValuePair.Create("at", (JsonValue)at),
                KeyValuePair.Create("errorMessage", (JsonValue)$"The host stopped while the model was answering, on attempt {attempt}")),
            JsonValue.Object(KeyValuePair.Create("type", (JsonValue)"auto_retry_end"), KeyValuePair.Create("attempt", (JsonValue)attempt)),
        ]);
        return properties.SetItem("resume", JsonValue.Array(items));
    }

    internal static bool IsRunning(JsonObjectValue task) =>
        task.TryGetValue("state", out var state) && state is JsonObjectValue value && value["status"] is JsonStringValue { Value: "running" };

    private static IEnumerable<JsonObjectValue> Tasks(JsonValue records) =>
        records is JsonObjectValue properties && properties.TryGetValue("tasks", out var tasks) && tasks is JsonArrayValue items
            ? items.Cast<JsonObjectValue>() : [];

    private static JsonValue MapTasks(JsonValue records, Func<JsonObjectValue, JsonObjectValue> map)
    {
        var properties = (JsonObjectValue)records;
        return properties.SetItem("tasks", JsonValue.Array(((JsonArrayValue)properties["tasks"]).Cast<JsonObjectValue>().Select(task => (JsonValue)map(task))));
    }
}
