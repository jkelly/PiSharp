// PiSharp-only (no upstream Pi source): fixture pipeline tracing, quarantine, repair and deterministic reruns.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Samples.DataPipelineInvestigator;

public static class Workflow
{
    public sealed record Pipeline(string Id, string Currency, string[] AccountIds, JsonObject[] Rows);
    public sealed record Remedy(string PipelineId, string RowId, string Action, JsonObject? Values);
    public sealed record Violation(string RowId, string[] Errors);
    public sealed record Change(string PipelineId, string RowId, string Action, JsonObject Before, JsonObject? After);
    public sealed record Plan(string Fingerprint, string[] PipelineIds, Change[] Changes, string[] Unresolved);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private const string StorePath = "pipeline-state.json";

    public static WorkflowSpec Create(SampleContext c)
    {
        var schema = SampleJson.Schema("""{"pipelineIds":{"type":"array","items":{"type":"string"},"minItems":1}}""", "pipelineIds");
        var ids = Read<Pipeline[]>(c.Input["pipelines"]).Select(p => p.Id).ToArray();
        return new("55", "Data Pipeline Investigator",
            "Run trace_pipeline for all requested pipelineIds, propose_pipeline_remediation, then apply_remediation_and_rerun. Use only supplied row corrections or quarantine directives. Report actual validate/aggregate results and unresolved errors; reruns are fixture-only.",
            [new("trace_pipeline", "Trace extract, schema/reference validation and aggregate stages to the first failing stage and exact rows.", schema,
                (a, _) => Task.FromResult<JsonNode?>(Node(Selected(c, Ids(a)).Select(Trace).ToArray()))),
             new("propose_pipeline_remediation", "Validate supplied exact corrections or quarantine actions and freeze affected fixture data.", schema, async (a, t) =>
             {
                 var plan = Build(c, Ids(a)); c.State[Key(plan.PipelineIds)] = Node(plan);
                 await c.WriteJsonAsync("pipeline-plan.json", Node(plan), t); return Node(plan);
             }),
             new("apply_remediation_and_rerun", "Apply approved local data corrections/quarantine and execute validation and aggregation again.", schema,
                 (a, t) => Apply(c, Ids(a), t), RequiresApproval: true)],
            [new("trace_pipeline", Args(ids)), new("propose_pipeline_remediation", Args(ids)), new("apply_remediation_and_rerun", Args(ids))]);
    }
    public static JsonObject Args(params string[] ids) => new() { ["pipelineIds"] = Node(ids) };
    private static T Read<T>(JsonNode? n) where T : class => n?.Deserialize<T>(Options) ?? throw new InvalidDataException("Required pipeline data is missing.");
    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value)!;
    private static string[] Ids(JsonObject a) => Read<string[]>(a["pipelineIds"]);
    private static string Key(string[] ids) => "plan:" + string.Join("|", ids.Order(StringComparer.Ordinal));
    private static string Hash(JsonNode value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToJsonString())));
    private static JsonObject Store(SampleContext c) => File.Exists(c.ResolvePath(StorePath))
        ? JsonNode.Parse(File.ReadAllText(c.ResolvePath(StorePath)))!.AsObject()
        : new JsonObject { ["pipelines"] = c.Input["pipelines"]!.DeepClone(), ["quarantine"] = new JsonArray() };
    private static Pipeline[] Selected(SampleContext c, string[] ids)
    {
        var all = Read<Pipeline[]>(Store(c)["pipelines"]);
        if (ids.Length == 0 || ids.Distinct().Count() != ids.Length || all.Select(p => p.Id).Distinct().Count() != all.Length)
            throw new InvalidDataException("Pipeline IDs must be unique and nonempty.");
        var selected = ids.Order(StringComparer.Ordinal).Select(id => all.SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException("Unknown pipeline: " + id)).ToArray();
        foreach (var p in selected)
        {
            if (string.IsNullOrWhiteSpace(p.Currency) || p.AccountIds is null || p.Rows is null || p.Rows.Any(r => string.IsNullOrWhiteSpace(r.Text("id"))) || p.Rows.Select(r => r.Text("id")).Distinct().Count() != p.Rows.Length)
                throw new InvalidDataException("Missing schema or ambiguous row IDs.");
        }
        return selected;
    }

    public static string[] Errors(Pipeline pipeline, JsonObject row)
    {
        var errors = new List<string>();
        if (row["accountId"] is not JsonValue account || !account.TryGetValue<string>(out var accountId) || !pipeline.AccountIds.Contains(accountId)) errors.Add("accountId: unknown/missing account reference");
        if (row["amountCents"] is not JsonValue amount || !amount.TryGetValue<long>(out var cents) || cents < 0 || cents > 1_000_000_000) errors.Add("amountCents: expected integer in 0..1000000000");
        if (row["currency"] is not JsonValue currency || !currency.TryGetValue<string>(out var code) || code != pipeline.Currency) errors.Add("currency: does not match pipeline schema");
        return errors.ToArray();
    }
    private static JsonObject Trace(Pipeline p)
    {
        var violations = p.Rows.Select(row => new Violation(row.Text("id"), Errors(p, row))).Where(v => v.Errors.Length > 0).ToArray();
        return new JsonObject { ["pipelineId"] = p.Id, ["extract"] = new JsonObject { ["status"] = "passed", ["rows"] = p.Rows.Length },
            ["validate"] = new JsonObject { ["status"] = violations.Length == 0 ? "passed" : "failed", ["violations"] = Node(violations) },
            ["aggregate"] = new JsonObject { ["status"] = violations.Length == 0 ? "passed" : "blocked", ["totalCents"] = violations.Length == 0 ? p.Rows.Sum(r => r["amountCents"]!.GetValue<long>()) : null } };
    }
    private static Plan Build(SampleContext c, string[] ids)
    {
        var selected = Selected(c, ids); var remedies = Read<Remedy[]>(c.Input["remedies"]);
        if (remedies.GroupBy(r => (r.PipelineId, r.RowId)).Any(g => g.Count() != 1)) throw new InvalidDataException("Ambiguous row remedies.");
        var changes = new List<Change>(); var unresolved = new List<string>();
        foreach (var p in selected)
        foreach (var row in p.Rows.Where(r => Errors(p, r).Length > 0))
        {
            var remedy = remedies.SingleOrDefault(r => r.PipelineId == p.Id && r.RowId == row.Text("id"));
            if (remedy is null) { unresolved.Add(p.Id + ":" + row.Text("id") + ": no supplied remedy"); continue; }
            if (remedy.Action == "quarantine") changes.Add(new(p.Id, row.Text("id"), "quarantine", row, null));
            else if (remedy.Action == "repair" && remedy.Values is not null)
            {
                var updated = row.DeepClone().AsObject();
                foreach (var pair in remedy.Values)
                {
                    if (pair.Key is not ("accountId" or "amountCents" or "currency")) throw new InvalidDataException("Correction field is not in the allowlisted schema.");
                    updated[pair.Key] = pair.Value?.DeepClone();
                }
                var errors = Errors(p, updated);
                if (errors.Length > 0) unresolved.Add(p.Id + ":" + row.Text("id") + ": correction still invalid: " + string.Join(", ", errors));
                else changes.Add(new(p.Id, row.Text("id"), "repair", row, updated));
            }
            else unresolved.Add(p.Id + ":" + row.Text("id") + ": unsupported remedy");
        }
        return new(Hash(Node(new { selected, remedies })), selected.Select(p => p.Id).ToArray(), changes.ToArray(), unresolved.ToArray());
    }
    private static async Task<JsonNode?> Apply(SampleContext c, string[] ids, CancellationToken token)
    {
        if (!c.Approved) throw new UnauthorizedAccessException("Approval is required for fixture remediation.");
        var key = Key(ids); var plan = Read<Plan>(c.State[key]); var store = Store(c);
        var inputHash = Hash(c.Input["remedies"]!);
        if (c.State[key + ":receipt"] is JsonObject old && old.Text("afterHash") == Hash(store) && old.Text("remediesHash") == inputHash) return old.DeepClone();
        if (plan.Fingerprint != Build(c, ids).Fingerprint) throw new InvalidOperationException("Data/remedies changed; propose again.");
        if (plan.Unresolved.Length > 0) throw new InvalidOperationException("Unresolved rows prevent the remediation batch.");
        var pipelines = Read<Pipeline[]>(store["pipelines"]);
        foreach (var change in plan.Changes)
        {
            var pipeline = pipelines.Single(p => p.Id == change.PipelineId);
            if (change.Action == "quarantine") store["quarantine"]!.AsArray().Add(new JsonObject { ["pipelineId"] = pipeline.Id, ["row"] = change.Before.DeepClone(), ["reason"] = Node(Errors(pipeline, change.Before)) });
        }
        var updated = pipelines.Select(p => p with { Rows = p.Rows.Select(r =>
        {
            var change = plan.Changes.SingleOrDefault(ch => ch.PipelineId == p.Id && ch.RowId == r.Text("id"));
            return change is null ? r : change.After;
        }).Where(r => r is not null).Select(r => r!).ToArray() }).ToArray();
        store["pipelines"] = Node(updated);
        var traces = updated.Where(p => ids.Contains(p.Id)).Select(Trace).ToArray();
        if (traces.Any(t => t["validate"]!["status"]!.GetValue<string>() != "passed")) throw new InvalidOperationException("Rerun failed; no state committed.");
        var result = new JsonObject { ["changesApplied"] = plan.Changes.Length, ["traces"] = Node(traces), ["quarantinedRows"] = store["quarantine"]!.AsArray().Count,
            ["afterHash"] = Hash(store), ["remediesHash"] = inputHash, ["statePath"] = StorePath };
        await c.WriteJsonAsync(StorePath, store, token); await c.WriteJsonAsync("pipeline-rerun.json", result, token);
        c.State[key + ":receipt"] = result.DeepClone(); return result;
    }
}
