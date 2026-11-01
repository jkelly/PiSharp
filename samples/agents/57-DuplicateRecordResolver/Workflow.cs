// PiSharp-only (no upstream Pi source): evidence-based local duplicate resolution with provenance.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.Samples.DuplicateRecordResolver;

public static class Workflow
{
    public sealed record Customer(string Id, string Name, string Email, string Phone, string ExternalId, string Address, string[] Notes);
    public sealed record MergeRequest(string Id, string SurvivorId, string[] DuplicateIds, Dictionary<string, string> Resolutions);
    public sealed record Evidence(string LeftId, string RightId, string[] Matches, string[] Conflicts, bool Eligible);
    public sealed record Merge(string Id, Customer[] Originals, Customer? Survivor, string[] Blockers, bool AlreadyApplied = false);
    public sealed record Plan(string Fingerprint, string[] MergeIds, Merge[] Merges);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private const string StorePath = "customer-state.json";

    public static WorkflowSpec Create(SampleContext c)
    {
        var inspectSchema = SampleJson.Schema("""{"recordIds":{"type":"array","items":{"type":"string"},"minItems":2}}""", "recordIds");
        var mergeSchema = SampleJson.Schema("""{"mergeIds":{"type":"array","items":{"type":"string"},"minItems":1}}""", "mergeIds");
        var ids = Read<Customer[]>(c.Input["records"]).Select(r => r.Id).ToArray(); var merges = Read<MergeRequest[]>(c.Input["merges"]).Select(m => m.Id).ToArray();
        return new("57", "Duplicate Record Resolver",
            "Inspect every requested recordId for strong matching evidence and contradictory identities, propose_duplicate_merges for explicit mergeIds, then apply_duplicate_merges. Never merge a merely similar name or an ambiguous identity. Keep survivor IDs, notes, source snapshots and explicit address resolutions in the local store.",
            [new("inspect_duplicate_evidence", "Compare normalized email, phone, external ID and name; flag contradictions instead of overmerging.", inspectSchema,
                (a, _) => Task.FromResult<JsonNode?>(Node(Inspect(c, Read<string[]>(a["recordIds"]))))),
             new("propose_duplicate_merges", "Build exact survivor/duplicate groups with conflict-aware field preservation and supplied resolutions.", mergeSchema, async (a, t) =>
             {
                 var plan = Build(c, Read<string[]>(a["mergeIds"])); c.State[Key(plan.MergeIds)] = Node(plan);
                 await c.WriteJsonAsync("duplicate-merge-plan.json", Node(plan), t); return Node(plan);
             }),
             new("apply_duplicate_merges", "Apply approved unambiguous merges in local JSON and archive original records as provenance.", mergeSchema,
                 (a, t) => Apply(c, Read<string[]>(a["mergeIds"]), t), RequiresApproval: true)],
            [new("inspect_duplicate_evidence", new JsonObject { ["recordIds"] = Node(ids) }), new("propose_duplicate_merges", Args(merges)), new("apply_duplicate_merges", Args(merges))]);
    }
    public static JsonObject Args(params string[] ids) => new() { ["mergeIds"] = Node(ids) };
    private static T Read<T>(JsonNode? n) where T : class => n?.Deserialize<T>(Options) ?? throw new InvalidDataException("Required duplicate data is missing.");
    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value)!;
    private static string Hash(JsonNode value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToJsonString())));
    private static string Key(string[] ids) => "plan:" + string.Join("|", ids.Order(StringComparer.Ordinal));
    private static JsonObject Store(SampleContext c) => File.Exists(c.ResolvePath(StorePath))
        ? JsonNode.Parse(File.ReadAllText(c.ResolvePath(StorePath)))!.AsObject()
        : new JsonObject { ["records"] = c.Input["records"]!.DeepClone(), ["mergeHistory"] = new JsonObject() };
    private static Customer[] Records(SampleContext c)
    {
        var records = Read<Customer[]>(Store(c)["records"]);
        if (records.Any(r => string.IsNullOrWhiteSpace(r.Id) || r.Name is null || r.Email is null || r.Phone is null || r.ExternalId is null || r.Address is null || r.Notes is null) || records.Select(r => r.Id).Distinct().Count() != records.Length)
            throw new InvalidDataException("Missing fields or ambiguous customer IDs.");
        return records;
    }
    private static string Text(string value) => Regex.Replace(value.Trim(), @"\s+", " ").ToLowerInvariant();
    private static string Phone(string value)
    {
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray()); return digits.Length is >= 10 and <= 15 ? digits : "";
    }
    public static Evidence Compare(Customer a, Customer b)
    {
        var matches = new List<string>(); var conflicts = new List<string>();
        void Check(string field, string left, string right)
        {
            if (left.Length == 0 || right.Length == 0) return;
            if (left == right) matches.Add(field); else conflicts.Add(field);
        }
        Check("email", Text(a.Email), Text(b.Email)); Check("phone", Phone(a.Phone), Phone(b.Phone));
        Check("externalId", Text(a.ExternalId), Text(b.ExternalId)); Check("name", Text(a.Name), Text(b.Name));
        var strong = matches.Any(m => m is "email" or "phone" or "externalId");
        return new(a.Id, b.Id, matches.ToArray(), conflicts.ToArray(), strong && matches.Contains("name") && conflicts.Count == 0);
    }
    private static Evidence[] Inspect(SampleContext c, string[] ids)
    {
        var records = Records(c);
        if (ids.Length < 2 || ids.Distinct().Count() != ids.Length) throw new InvalidDataException("At least two distinct record IDs are required.");
        var selected = ids.Order(StringComparer.Ordinal).Select(id => records.SingleOrDefault(r => r.Id == id) ?? throw new InvalidDataException("Unknown record: " + id)).ToArray();
        return selected.SelectMany((a, i) => selected.Skip(i + 1).Select(b => Compare(a, b))).Where(e => e.Matches.Any(m => m is "email" or "phone" or "externalId") || e.Matches.Contains("name")).ToArray();
    }
    private static Plan Build(SampleContext c, string[] ids)
    {
        var records = Records(c); var requests = Read<MergeRequest[]>(c.Input["merges"]);
        if (ids.Length == 0 || ids.Distinct().Count() != ids.Length || requests.Select(r => r.Id).Distinct().Count() != requests.Length) throw new InvalidDataException("Merge IDs must be unique and nonempty.");
        var selected = ids.Order(StringComparer.Ordinal).Select(id => requests.SingleOrDefault(r => r.Id == id) ?? throw new InvalidDataException("Unknown merge: " + id)).ToArray();
        var allIds = selected.SelectMany(r => r.DuplicateIds.Append(r.SurvivorId)).ToArray();
        if (selected.Any(r => r.DuplicateIds.Length == 0 || r.Resolutions is null) || allIds.Distinct().Count() != allIds.Length) throw new InvalidDataException("Merge groups overlap or are empty.");
        var plans = selected.Select(request =>
        {
            var history = Store(c)["mergeHistory"]?[request.Id];
            if (history is not null && JsonNode.DeepEquals(history["request"], Node(request)))
            {
                var previous = Read<Merge>(history["merge"]);
                var current = records.SingleOrDefault(r => r.Id == request.SurvivorId);
                if (current is not null && JsonNode.DeepEquals(Node(current), Node(previous.Survivor)) && request.DuplicateIds.All(id => records.All(r => r.Id != id)))
                    return new Merge(request.Id, [], current, [], true);
            }
            var originals = request.DuplicateIds.Prepend(request.SurvivorId).Select(id => records.SingleOrDefault(r => r.Id == id) ?? throw new InvalidDataException("Unknown merge record: " + id)).ToArray();
            var survivor = originals[0]; var blockers = new List<string>();
            foreach (var duplicate in originals.Skip(1))
            {
                var evidence = Compare(survivor, duplicate);
                if (!evidence.Eligible) blockers.Add(duplicate.Id + ": insufficient or conflicting identity evidence (" + string.Join(",", evidence.Conflicts) + ")");
            }
            // A blank survivor field must not conceal incompatible values supplied by different duplicates.
            for (var i = 1; i < originals.Length; i++)
            for (var j = i + 1; j < originals.Length; j++)
            {
                var evidence = Compare(originals[i], originals[j]);
                if (evidence.Conflicts.Length > 0)
                    blockers.Add(originals[i].Id + "/" + originals[j].Id + ": conflicting duplicate identity fields (" + string.Join(",", evidence.Conflicts) + ")");
            }
            if (request.Resolutions.Keys.Any(k => k != "address")) blockers.Add("Only address conflicts may use explicit field resolution.");
            var addresses = originals.Select(r => r.Address.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            var address = addresses.FirstOrDefault() ?? "";
            if (addresses.Length > 1)
            {
                if (!request.Resolutions.TryGetValue("address", out var chosen) || !addresses.Contains(chosen, StringComparer.Ordinal)) blockers.Add("Conflicting address needs an explicit source value.");
                else address = chosen;
            }
            else if (request.Resolutions.TryGetValue("address", out var extra) && extra != address) blockers.Add("Address resolution is not a source value.");
            string Fill(Func<Customer, string> field) => originals.Select(field).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
            var merged = survivor with { Email = Fill(r => r.Email), Phone = Fill(r => r.Phone), ExternalId = Fill(r => r.ExternalId), Address = address,
                Notes = originals.SelectMany(r => r.Notes).Distinct(StringComparer.Ordinal).ToArray() };
            return new Merge(request.Id, originals, blockers.Count == 0 ? merged : null, blockers.ToArray());
        }).ToArray();
        return new(Hash(Node(new { selected, plans })), selected.Select(r => r.Id).ToArray(), plans);
    }
    private static async Task<JsonNode?> Apply(SampleContext c, string[] ids, CancellationToken token)
    {
        if (!c.Approved) throw new UnauthorizedAccessException("Approval is required for local record merges.");
        var key = Key(ids); var plan = Read<Plan>(c.State[key]); var store = Store(c); var requestHash = Hash(c.Input["merges"]!);
        if (c.State[key + ":receipt"] is JsonObject old && old.Text("afterHash") == Hash(store) && old.Text("requestHash") == requestHash) return old.DeepClone();
        if (plan.Fingerprint != Build(c, ids).Fingerprint) throw new InvalidOperationException("Records or merge instructions changed; propose again.");
        if (plan.Merges.Any(m => m.Blockers.Length > 0 || m.Survivor is null)) throw new InvalidOperationException("Ambiguous identity or unresolved field conflicts block this merge batch.");
        var records = Records(c); var removed = plan.Merges.SelectMany(m => m.Originals.Skip(1)).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var survivors = plan.Merges.ToDictionary(m => m.Survivor!.Id, m => m.Survivor!);
        store["records"] = Node(records.Where(r => !removed.Contains(r.Id)).Select(r => survivors.GetValueOrDefault(r.Id, r)).ToArray());
        var requests = Read<MergeRequest[]>(c.Input["merges"]);
        foreach (var merge in plan.Merges.Where(m => !m.AlreadyApplied))
            store["mergeHistory"]![merge.Id] = new JsonObject { ["request"] = Node(requests.Single(r => r.Id == merge.Id)), ["merge"] = Node(merge) };
        var result = new JsonObject { ["survivors"] = Node(plan.Merges.Select(m => m.Survivor).ToArray()), ["removedIds"] = Node(removed.Order(StringComparer.Ordinal).ToArray()),
            ["afterHash"] = Hash(store), ["requestHash"] = requestHash, ["statePath"] = StorePath, ["scope"] = "local fixture records with original provenance" };
        await c.WriteJsonAsync(StorePath, store, token); await c.WriteJsonAsync("duplicate-merge-receipt.json", result, token);
        c.State[key + ":receipt"] = result.DeepClone(); return result;
    }
}
