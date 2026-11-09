// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/jobs/{persisted,stored}.ts
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Jobs;

internal sealed class PersistedRuns(Persistence records, Persistence states)
{
    internal const long RecordVersion = 1;
    internal const string RecordFingerprint = "chord.job-run.1";
    private readonly Dictionary<string, Stored> stored = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stored> blockedStates = new(StringComparer.Ordinal);
    internal Action Stage(JobRun run, JsonObject record, List<IStagedWrite> writes)
    {
        var text = ChordJson.Stringify(record);
        if (!stored.TryGetValue(run.Id, out var prior))
        {
            long created = 0;
            writes.Add(StoreInternal.CreateDocument(records.At(run.Id), new DocumentBase(RecordVersion, RecordFingerprint, record), text, id => created = id));
            return () => stored[run.Id] = new(created, 1, RecordVersion, 0);
        }
        writes.Add(StoreInternal.ChangeDocument(prior.Id, prior.Revision, new DocumentContent.Base(RecordVersion, RecordFingerprint, record), text));
        return () => stored[run.Id] = prior with { Revision = prior.Revision + 1 };
    }
    internal Action Retire(string id, List<IStagedWrite> writes)
    {
        if (stored.TryGetValue(id, out var prior)) writes.Add(StoreInternal.RetireDocument(prior.Id, prior.Revision));
        if (blockedStates.TryGetValue(id, out var state)) writes.Add(StoreInternal.RetireDocument(state.Id, state.Revision));
        return () => { stored.Remove(id); blockedStates.Remove(id); };
    }
    internal async Task FindBlockedStateAsync(string id, ChordContext context)
    {
        if (await states.FindAsync(id, context) is { } found) blockedStates[id] = found.Stored;
    }
    internal void ForgetBlockedState(string id) => blockedStates.Remove(id);
    internal async Task<IReadOnlyList<JobRun>> LoadAsync(ChordContext context)
    {
        var loaded = new List<JobRun>(); string? after = null;
        for (;;)
        {
            var keys = await records.KeysAsync(after, 100, context);
            foreach (var id in keys)
            {
                var document = await records.ReadAsync(id, context);
                if (document is null) continue;
                if (document.Record.Version != RecordVersion || document.Record.Fingerprint != RecordFingerprint)
                    throw new InvalidOperationException($"{id}: unsupported Chord job record header");
                if (document.Value is not JsonObject value) throw new InvalidOperationException($"{id}: invalid Chord job record");
                var run = Decode(id, value);
                stored[id] = new(document.Record.Id, document.Record.Revision, RecordVersion, document.DeltasSinceBase);
                loaded.Add(run);
            }
            if (keys.Count < 100) return loaded;
            after = keys[^1];
        }
    }
    internal void Drop() { stored.Clear(); blockedStates.Clear(); }

    internal static JsonObject Encode(JobRun run, string? phase = null, int? restarts = null, int? finishFailures = null,
        bool? abortMarked = null, string? blocked = null, JobOutcome? outcome = null, bool clearBlocked = false)
    {
        var value = new JsonObject { ["version"] = run.Version, ["fingerprint"] = run.Fingerprint, ["args"] = run.Args?.DeepClone() };
        if (run.Key is not null) value["key"] = run.Key;
        if (run.Trace is not null) value["trace"] = run.Trace.DeepClone();
        value["phase"] = phase ?? run.Phase; value["restarts"] = restarts ?? run.Restarts;
        value["finishFailures"] = finishFailures ?? run.FinishFailures;
        if (abortMarked ?? run.AbortMarked) value["abort"] = true;
        if (!clearBlocked && (blocked ?? run.Blocked) is { } reason) value["blocked"] = reason;
        if ((outcome ?? run.Outcome) is { } result) value["outcome"] = EncodeOutcome(result);
        return value;
    }
    private static JsonObject EncodeOutcome(JobOutcome outcome)
    {
        if (outcome.Kind == "done") return new JsonObject { ["kind"] = "done", ["result"] = JsonResult(outcome.Result) };
        var error = outcome.Error!;
        var node = new JsonObject { ["kind"] = error.Kind };
        if (error.Reason is not null) node["reason"] = error.Reason;
        if (error.Restarts is { } restarts) node["restarts"] = restarts;
        if (error.Error is { } thrown) node["error"] = Wire(thrown);
        return new JsonObject { ["kind"] = "failed", ["error"] = node };
    }
    internal static JsonNode? JsonResult(object? value) => value switch
    {
        null => null,
        byte[] => throw new ArgumentException("A persisted job result must be JSON"),
        ReadOnlyMemory<byte> => throw new ArgumentException("A persisted job result must be JSON"),
        JsonNode node => node.DeepClone(),
        _ => JsonSerializer.SerializeToNode(value),
    };
    private static JsonObject Wire(Exception error)
    {
        if (error is ChordError chord) return new JsonObject { ["kind"] = "chord", ["error"] = chord.ToJson() };
        var plugin = error as PluginError ?? new PluginError("internal", error.Message);
        var node = new JsonObject { ["kind"] = "plugin", ["code"] = plugin.Code, ["message"] = plugin.Message };
        if (plugin.Details is not null) node["details"] = plugin.Details.DeepClone();
        return node;
    }
    private static JobRun Decode(string id, JsonObject value)
    {
        // Stored documents are a trust boundary: malformed records cannot invoke plugin code.
        var allowed = new HashSet<string>(["version", "fingerprint", "args", "key", "trace", "phase", "restarts", "finishFailures", "abort", "blocked", "outcome"], StringComparer.Ordinal);
        if (value.Any(x => !allowed.Contains(x.Key)) || !value.ContainsKey("args")) throw Bad(id);
        var version = value["version"]?.GetValue<long>() ?? 0;
        var fingerprint = ChordJson.AsString(value["fingerprint"]); var phase = ChordJson.AsString(value["phase"]);
        if (version < 1 || fingerprint is null || phase is not ("pending" or "running" or "stopped" or "finishing" or "blocked")) throw Bad(id);
        var run = new JobRun(id, version, fingerprint, ChordJson.AsString(value["key"]), value["args"]?.DeepClone(), phase)
        {
            Restarts = value["restarts"]?.GetValue<int>() ?? -1, FinishFailures = value["finishFailures"]?.GetValue<int>() ?? -1,
            AbortMarked = value["abort"]?.GetValue<bool>() ?? false, Blocked = ChordJson.AsString(value["blocked"]),
            Trace = value["trace"]?.DeepClone() as JsonObject,
        };
        if (run.Restarts < 0 || run.FinishFailures < 0 || (value.ContainsKey("abort") && !run.AbortMarked) ||
            (phase == "blocked" && run.Blocked is not ("version" or "finish_failed"))) throw Bad(id);
        if (value["outcome"] is JsonObject outcome)
        {
            var kind = ChordJson.AsString(outcome["kind"]);
            if (kind == "done") run.Outcome = new(id, run.Args, null, kind, outcome["result"]?.DeepClone());
            else if (kind == "failed" && outcome["error"] is JsonObject error)
            {
                var errorKind = ChordJson.AsString(error["kind"]);
                JobError rebuilt = errorKind switch
                {
                    "threw" when error["error"] is JsonObject wire => new("threw", Unwire(wire)),
                    "aborted" when ChordJson.AsString(error["reason"]) is "abort" or "restart" or "reload" => new("aborted", Reason: ChordJson.AsString(error["reason"])),
                    "restart_limit" when error["restarts"]?.GetValue<int>() is >= 5 => new("restart_limit", Restarts: error["restarts"]!.GetValue<int>()),
                    _ => throw Bad(id),
                };
                run.Outcome = new(id, run.Args, null, "failed", Error: rebuilt);
            }
            else throw Bad(id);
        }
        if (phase == "finishing" && run.Outcome is null) throw Bad(id);
        return run;
    }
    private static Exception Unwire(JsonObject value) => ChordJson.AsString(value["kind"]) switch
    {
        "chord" => DataInternal.ChordErrorFromJson(value["error"]),
        "plugin" when ChordJson.AsString(value["code"]) is { } code && ChordJson.AsString(value["message"]) is { } message =>
            new PluginError(code, message, value["details"]?.DeepClone()),
        _ => throw new InvalidOperationException("Invalid stored job error"),
    };
    private static InvalidOperationException Bad(string id) => new($"{id}: invalid Chord job record");
}
