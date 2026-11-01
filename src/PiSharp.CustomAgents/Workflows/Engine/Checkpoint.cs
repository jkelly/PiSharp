// Decision 0012: the durable checkpoint of a workflow run, and the run documents beside it.
//
// Checkpoint {phase, seq, visit, attempt, steps, data, visits, wakeAt?, state?}:
//   seq     grows on every committed transition, retries included, so a phase never commits the checkpoint it started from (the
//           scheduler faults a phase that returns without durable progress);
//   visit   the seq at which the current action was entered: stable across retries, it scopes memos, idempotency keys and model calls;
//   attempt 1-based attempt of the current visit; steps counts entered actions (WorkflowLimits.MaxSteps); visits counts entries per action;
//   wakeAt  a durable wait before the action runs (retry delays, Sleep, Repeat); state is a composite step's own state.
using System.Collections.Immutable;
using System.Globalization;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.CustomAgents.Workflows.Engine;

internal sealed record Checkpoint(string Phase, long Seq, long Visit, int Attempt, int Steps, JsonValue Data, ImmutableDictionary<string, int> Visits,
    double? WakeAt = null, JsonObjectValue? State = null)
{
    public static Checkpoint Parse(JsonValue value)
    {
        var json = (JsonObjectValue)value;
        var visits = json.TryGetValue("visits", out var counts) && counts is JsonObjectValue map
            ? map.ToImmutableDictionary(pair => pair.Key, pair => (int)((JsonNumberValue)pair.Value).Value, StringComparer.Ordinal)
            : ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.Ordinal);
        return new Checkpoint(
            ((JsonStringValue)json["phase"]).Value,
            (long)Number(json, "seq", 0),
            (long)Number(json, "visit", 0),
            (int)Number(json, "attempt", 1),
            (int)Number(json, "steps", 0),
            json.TryGetValue("data", out var data) ? data : JsonValue.Null,
            visits,
            json.TryGetValue("wakeAt", out var wake) && wake is JsonNumberValue at ? at.Value : null,
            json.TryGetValue("state", out var state) ? state as JsonObjectValue : null);
    }

    public JsonObjectValue ToJson()
    {
        var fields = new List<KeyValuePair<string, JsonValue>>
        {
            KeyValuePair.Create("phase", (JsonValue)Phase),
            KeyValuePair.Create("seq", (JsonValue)Seq),
            KeyValuePair.Create("visit", (JsonValue)Visit),
            KeyValuePair.Create("attempt", (JsonValue)Attempt),
            KeyValuePair.Create("steps", (JsonValue)Steps),
            KeyValuePair.Create("data", Data),
            KeyValuePair.Create("visits", (JsonValue)JsonValue.Object(Visits.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => KeyValuePair.Create(pair.Key, (JsonValue)pair.Value)))),
        };
        if (WakeAt is { } wake) fields.Add(KeyValuePair.Create("wakeAt", (JsonValue)wake));
        if (State is { } state) fields.Add(KeyValuePair.Create("state", (JsonValue)state));
        return JsonValue.Object(fields);
    }

    private static double Number(JsonObjectValue json, string key, double fallback) =>
        json.TryGetValue(key, out var value) && value is JsonNumberValue number ? number.Value : fallback;
}

/// <summary>
/// The documents of workflow runs. The run document is a conversation document family keyed by task ID, not a task document: task
/// documents retire when the task settles, and a run's status and history must outlive it.
/// </summary>
internal static class RunDocs
{
    public const int HistoryLimit = 100;
    public const int LogLimit = 200;
    public const int SignalLimit = 100;

    /// <summary><c>{workflow, version, status, started?, current?, history[], log[], error?, calls{}, signals{}}</c>.</summary>
    public static ConversationDocFamilyToken Run { get; } = Documents.DefineConversationDocFamily("pisharp.workflow.run", 1, DocumentFork.Initial,
        seed => seed as JsonObjectValue ?? JsonObjectValue.Empty);

    /// <summary>Run keys: <c>{taskId, conversationId}</c> keyed by <c>workflow:runKey</c>.</summary>
    public static SessionDocFamilyToken Keys { get; } = Documents.DefineSessionDocFamily("pisharp.workflow.key", 1,
        seed => seed as JsonObjectValue ?? JsonObjectValue.Empty);

    /// <summary>Every run of the Session, newest last: <c>{runs: [{taskId, conversationId, workflow}]}</c> (bounded).</summary>
    public static SessionDocToken Index { get; } = Documents.DefineSessionDoc("pisharp.workflow.index", 1,
        () => JsonValue.Object([KeyValuePair.Create("runs", (JsonValue)JsonValue.Array())]));

    public const int IndexLimit = 1000;

    public static string Key(TaskId task) => task.Value.ToString(CultureInfo.InvariantCulture);

    public static Task<DraftObject> RunAsync(ITx tx, ConversationId conversation, TaskId task) =>
        tx.DocAsync(Run, conversation, Key(task), JsonObjectValue.Empty);

    /// <summary>Appends <paramref name="item"/> to the array <paramref name="field"/>, keeping the newest <paramref name="limit"/>.</summary>
    public static void Append(DraftObject doc, string field, JsonValue item, int limit)
    {
        if (!doc.ContainsKey(field) || doc.Get(field) is not JsonArrayValue) doc.Set(field, JsonValue.Array());
        var list = doc.GetArray(field);
        list.Add(item);
        if (list.Count > limit) list.Splice(0, list.Count - limit);
    }

    public static JsonValue? Opt(string? text) => text is null ? null : JsonValue.String(text);

    public static JsonObjectValue Object(params (string Key, JsonValue? Value)[] fields) =>
        JsonValue.Object(fields.Where(field => field.Value is not null).Select(field => KeyValuePair.Create(field.Key, field.Value!)));
}
