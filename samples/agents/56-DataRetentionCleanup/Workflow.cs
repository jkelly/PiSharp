// PiSharp-only (no upstream Pi source): policy-derived, exact-selection local retention cleanup.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Samples.DataRetentionCleanup;

public static class Workflow
{
    public sealed record Record(string Id, string Category, string CreatedOn, bool? LegalHold, string Payload);
    public sealed record Dataset(string Id, Record[] Records);
    public sealed record Policy(string Category, int? RetainDays);
    public sealed record Assessment(string Key, string Status, string? ExpiresOn);
    public sealed record Plan(string Fingerprint, string AsOf, string[] DatasetIds, string[] RecordKeys, Record[] Records);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private const string StorePath = "retention-state.json";

    public static WorkflowSpec Create(SampleContext c)
    {
        var inspectSchema = SampleJson.Schema("""{"datasetIds":{"type":"array","items":{"type":"string"},"minItems":1}}""", "datasetIds");
        var mutationSchema = SampleJson.Schema("""{"datasetIds":{"type":"array","items":{"type":"string"},"minItems":1},"recordKeys":{"type":"array","items":{"type":"string"},"minItems":1}}""", "datasetIds", "recordKeys");
        var ids = Read<Dataset[]>(c.Input["datasets"]).Select(d => d.Id).ToArray(); var keys = Read<string[]>(c.Input["approvedRecordKeys"]);
        return new("56", "Data Retention Cleanup",
            "Inspect retention for all requested datasetIds, plan_retention_cleanup with exactly the user-supplied recordKeys, then execute_retention_cleanup. Use fixture asOf, policy deadlines and legal holds. Only local JSON payloads are removed; no external filesystem or service cleanup.",
            [new("inspect_retention", "Evaluate fixed as-of date, category policies and legal holds; report unknown policies and invalid dates.", inspectSchema,
                (a, _) => Task.FromResult<JsonNode?>(Node(Assess(c, Ids(a))))),
             new("plan_retention_cleanup", "Freeze exact approved record keys after proving every record is eligible and unheld.", mutationSchema, async (a, t) =>
             {
                 var plan = Build(c, Ids(a), Read<string[]>(a["recordKeys"])); c.State[Key(plan.DatasetIds, plan.RecordKeys)] = Node(plan);
                 await c.WriteJsonAsync("retention-plan.json", Node(plan), t); return Node(plan);
             }),
             new("execute_retention_cleanup", "Remove only approved expired payloads from the local dataset; persist metadata-only tombstones.", mutationSchema,
                 (a, t) => Apply(c, Ids(a), Read<string[]>(a["recordKeys"]), t), RequiresApproval: true)],
            [new("inspect_retention", Args(ids)), new("plan_retention_cleanup", Args(ids, keys)), new("execute_retention_cleanup", Args(ids, keys))]);
    }
    public static JsonObject Args(string[] ids, string[]? keys = null)
    {
        var result = new JsonObject { ["datasetIds"] = Node(ids) }; if (keys is not null) result["recordKeys"] = Node(keys); return result;
    }
    private static T Read<T>(JsonNode? n) where T : class => n?.Deserialize<T>(Options) ?? throw new InvalidDataException("Required retention data is missing.");
    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value)!;
    private static string[] Ids(JsonObject a) => Read<string[]>(a["datasetIds"]);
    private static string Hash(JsonNode value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToJsonString())));
    private static string Key(string[] ids, string[] keys) => "plan:" + string.Join("|", ids.Order(StringComparer.Ordinal)) + ":" + string.Join("|", keys.Order(StringComparer.Ordinal));
    private static JsonObject Store(SampleContext c) => File.Exists(c.ResolvePath(StorePath))
        ? JsonNode.Parse(File.ReadAllText(c.ResolvePath(StorePath)))!.AsObject()
        : new JsonObject { ["datasets"] = c.Input["datasets"]!.DeepClone(), ["tombstones"] = new JsonObject() };
    private static Dataset[] Selected(SampleContext c, string[] ids)
    {
        var all = Read<Dataset[]>(Store(c)["datasets"]);
        if (ids.Length == 0 || ids.Distinct().Count() != ids.Length || all.Select(d => d.Id).Distinct().Count() != all.Length || all.Any(d => d.Id.Contains(':')))
            throw new InvalidDataException("Dataset IDs must be nonempty, unique and contain no colon.");
        var selected = ids.Order(StringComparer.Ordinal).Select(id => all.SingleOrDefault(d => d.Id == id) ?? throw new InvalidDataException("Unknown dataset: " + id)).ToArray();
        if (selected.Any(d => d.Records is null || d.Records.Any(r => string.IsNullOrWhiteSpace(r.Id) || r.Id.Contains(':')) || d.Records.Select(r => r.Id).Distinct().Count() != d.Records.Length))
            throw new InvalidDataException("Record identities are missing or ambiguous.");
        return selected;
    }
    private static Assessment[] Assess(SampleContext c, string[] ids)
    {
        if (!DateOnly.TryParseExact(c.Input.Text("asOf"), "yyyy-MM-dd", out var asOf)) throw new InvalidDataException("A deterministic yyyy-MM-dd asOf is required.");
        var policies = Read<Policy[]>(c.Input["policies"]);
        if (policies.Any(p => string.IsNullOrWhiteSpace(p.Category) || p.RetainDays is null or < 0 or > 36500) || policies.Select(p => p.Category).Distinct().Count() != policies.Length)
            throw new InvalidDataException("Policies require an explicit category and integer retainDays in 0..36500, without conflicting categories.");
        return Selected(c, ids).SelectMany(d => d.Records.Select(r =>
        {
            var key = d.Id + ":" + r.Id; var policy = policies.SingleOrDefault(p => p.Category == r.Category);
            if (!DateOnly.TryParseExact(r.CreatedOn, "yyyy-MM-dd", out var created) || created > asOf) return new Assessment(key, "invalid-created-date", null);
            if (r.LegalHold is null) return new Assessment(key, "unknown-hold-status", null);
            if (r.LegalHold == true) return new Assessment(key, "legal-hold", null);
            if (policy is null) return new Assessment(key, "unknown-policy", null);
            DateOnly expires;
            try { expires = created.AddDays(policy.RetainDays!.Value); } catch (ArgumentOutOfRangeException) { return new Assessment(key, "invalid-expiry-date", null); }
            return new Assessment(key, expires <= asOf ? "eligible" : "retain", expires.ToString("yyyy-MM-dd"));
        })).ToArray();
    }
    private static JsonNode PolicySnapshot(SampleContext c) => new JsonObject { ["asOf"] = c.Input["asOf"]?.DeepClone(), ["policies"] = c.Input["policies"]?.DeepClone(), ["approval"] = c.Input["approvedRecordKeys"]?.DeepClone() };
    private static Plan Build(SampleContext c, string[] ids, string[] keys)
    {
        var selected = Selected(c, ids); var assessment = Assess(c, ids); var approved = Read<string[]>(c.Input["approvedRecordKeys"]);
        if (keys.Length == 0 || keys.Distinct().Count() != keys.Length || keys.Any(k => !approved.Contains(k))) throw new InvalidDataException("Exact nonempty keys must be explicitly approved in the fixture request.");
        var tombstones = Store(c)["tombstones"]!.AsObject(); var policyHash = Hash(PolicySnapshot(c));
        foreach (var key in keys)
        {
            if (assessment.Any(a => a.Key == key && a.Status == "eligible")) continue;
            // Exact completed keys are replayable only while their policy/approval snapshot still matches.
            if (!assessment.Any(a => a.Key == key) && tombstones[key]?["policyHash"]?.GetValue<string>() == policyHash &&
                selected.Any(d => key.StartsWith(d.Id + ":", StringComparison.Ordinal))) continue;
            throw new InvalidDataException("Record is absent, held or ineligible: " + key);
        }
        var records = keys.Order(StringComparer.Ordinal).SelectMany(key => selected.Where(d => d.Id == key.Split(':')[0]).SelectMany(d => d.Records.Where(r => r.Id == key.Split(':')[1]))).ToArray();
        return new(Hash(Node(new { selected, policy = PolicySnapshot(c) })), c.Input.Text("asOf"), selected.Select(d => d.Id).ToArray(), keys.Order(StringComparer.Ordinal).ToArray(), records);
    }
    private static async Task<JsonNode?> Apply(SampleContext c, string[] ids, string[] keys, CancellationToken token)
    {
        if (!c.Approved) throw new UnauthorizedAccessException("Approval is required to remove local payloads.");
        var stateKey = Key(ids, keys); var plan = Read<Plan>(c.State[stateKey]); var store = Store(c); var policyHash = Hash(PolicySnapshot(c));
        if (c.State[stateKey + ":receipt"] is JsonObject old && old.Text("afterHash") == Hash(store) && old.Text("policyHash") == policyHash) return old.DeepClone();
        if (plan.Fingerprint != Build(c, ids, keys).Fingerprint) throw new InvalidOperationException("Data, holds or policy changed; plan again.");
        var datasets = Read<Dataset[]>(store["datasets"]); var tombstones = store["tombstones"]!.AsObject();
        var removedKeys = datasets.SelectMany(d => d.Records.Where(r => keys.Contains(d.Id + ":" + r.Id)).Select(r => d.Id + ":" + r.Id)).Order(StringComparer.Ordinal).ToArray();
        foreach (var dataset in datasets)
        foreach (var record in dataset.Records.Where(r => keys.Contains(dataset.Id + ":" + r.Id)))
            tombstones[dataset.Id + ":" + record.Id] = Node(new { datasetId = dataset.Id, record.Id, record.Category, record.CreatedOn, deletedAsOf = plan.AsOf, policyHash });
        store["datasets"] = Node(datasets.Select(d => d with { Records = d.Records.Where(r => !keys.Contains(d.Id + ":" + r.Id)).ToArray() }).ToArray());
        var result = new JsonObject { ["removedRecordKeys"] = Node(removedKeys), ["removedCount"] = removedKeys.Length, ["remainingCount"] = Read<Dataset[]>(store["datasets"]).Sum(d => d.Records.Length),
            ["afterHash"] = Hash(store), ["policyHash"] = policyHash, ["statePath"] = StorePath, ["scope"] = "workspace JSON only" };
        await c.WriteJsonAsync(StorePath, store, token); await c.WriteJsonAsync("retention-receipt.json", result, token);
        c.State[stateKey + ":receipt"] = result.DeepClone(); return result;
    }
}
