// PiSharp-only (no upstream Pi source): qualified shift coverage and guarded local assignment.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
namespace PiSharp.Samples.ShiftCoverageCoordinator;

public static class Workflow
{
    static JsonObject Schema(bool employee = false) => SampleJson.Schema(employee ? """{"shiftId":{"type":"string"},"employeeId":{"type":"string"}}""" : """{"shiftId":{"type":"string"}}""", employee ? ["shiftId", "employeeId"] : ["shiftId"]);
    public static WorkflowSpec Create(SampleContext c) => new("43-ShiftCoverageCoordinator", "Shift coverage coordinator",
        "For each requested shift call inspect_shift, rank_replacements, then assign_replacement with the highest ranked eligible employee. Assignment is a workspace-only roster receipt and requires host approval. Explain exclusions using evidence; never claim to contact staff.",
        [new("inspect_shift", "Inspect the named shift and employee roster", Schema(), (a,t) => Inspect(c,a,t)),
         new("rank_replacements", "Rank qualified available employees by assigned hours, then ID", Schema(), (a,t) => Rank(c,a,t)),
         new("assign_replacement", "Commit a local roster assignment after rechecking eligibility", Schema(true), (a,t) => Assign(c,a,t), true)],
        [new("inspect_shift", new() { ["shiftId"] = "S1" }), new("rank_replacements", new() { ["shiftId"] = "S1" }), new("assign_replacement", new() { ["shiftId"] = "S1", ["employeeId"] = "E2" })]);

    internal static JsonArray Rows(JsonObject o, string key) => o[key] as JsonArray ?? throw new FormatException($"Missing {key} array.");
    static JsonObject Shift(SampleContext c, string id) => Rows(c.Input,"shifts").OfType<JsonObject>().SingleOrDefault(s => s.Text("id") == id) ?? throw new ArgumentException("Unknown shift: " + id);
    static DateTimeOffset Time(JsonNode? n) => DateTimeOffset.Parse(n?.GetValue<string>() ?? throw new FormatException("Missing shift time."), System.Globalization.CultureInfo.InvariantCulture);
    static (DateTimeOffset Start, DateTimeOffset End) Interval(JsonObject o)
    { var s = Time(o["start"]); var e = Time(o["end"]); if (e <= s) throw new FormatException("End must follow start."); return (s,e); }
    static bool Overlaps(JsonObject a, JsonObject b) { var x = Interval(a); var y = Interval(b); return x.Start < y.End && y.Start < x.End; }
    sealed record Roster(JsonObject Assignments, JsonObject Fingerprints);
    static string Assigned(JsonObject assignments, JsonObject shift) => assignments[shift.Text("id")]?.GetValue<string>() ?? shift.Text("employeeId");
    static string Fingerprint(JsonObject shift, JsonObject employee) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(new JsonObject { ["shift"] = shift.DeepClone(), ["employee"] = employee.DeepClone() }.ToJsonString())));
    static JsonObject[] Employees(SampleContext c)
    {
        var rows = Rows(c.Input,"employees"); var employees = rows.OfType<JsonObject>().ToArray();
        if (employees.Length != rows.Count || employees.Any(e => string.IsNullOrWhiteSpace(e.Text("id"))) || employees.Select(e => e.Text("id")).Distinct().Count() != employees.Length) throw new FormatException("Employee IDs must be unique and nonempty.");
        return employees;
    }
    static JsonArray Reasons(SampleContext c, JsonObject target, JsonObject employee, JsonObject assignments)
    {
        var interval = Interval(target); var reasons = new JsonArray();
        if (!Rows(employee,"qualifications").Any(q => q?.GetValue<string>() == target.Text("qualification"))) reasons.Add("missing qualification");
        if (!Rows(employee,"availability").OfType<JsonObject>().Any(v => { var r = Interval(v); return r.Start <= interval.Start && r.End >= interval.End; })) reasons.Add("unavailable for full shift");
        var assigned = Rows(c.Input,"shifts").OfType<JsonObject>().Where(s => s.Text("id") != target.Text("id") && Assigned(assignments,s) == employee.Text("id"));
        if (assigned.Any(s => Overlaps(s,target))) reasons.Add("overlapping assignment");
        return reasons;
    }
    static Roster LoadRoster(SampleContext c)
    {
        var path = c.ResolvePath("roster-assignments.json");
        if (!File.Exists(path)) return new(new(),new());
        var saved = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new FormatException("Invalid roster ledger.");
        if (saved["rosterVersion"]?.GetValue<int>() != 1 || saved["assignments"] is not JsonObject assignments || saved["fingerprints"] is not JsonObject fingerprints) throw new FormatException("Roster ledger requires versioned assignment evidence.");
        if (!assignments.Select(p => p.Key).Order().SequenceEqual(fingerprints.Select(p => p.Key).Order())) throw new FormatException("Roster assignment evidence is incomplete.");
        var employees = Employees(c);
        foreach (var (id,value) in assignments)
        {
            var shift = Shift(c,id); var eid = value?.GetValue<string>() ?? throw new FormatException("Missing assigned employee.");
            var employee = employees.SingleOrDefault(e => e.Text("id") == eid) ?? throw new FormatException("Roster references an unknown employee.");
            if ((shift.Text("employeeId").Length > 0 && shift.Text("employeeId") != eid) || fingerprints[id]?.GetValue<string>() != Fingerprint(shift,employee)) throw new InvalidOperationException("Roster assignment conflicts with changed source records.");
            if (Reasons(c,shift,employee,assignments).Count > 0) throw new InvalidOperationException("Persisted roster assignment is no longer eligible.");
        }
        return new((JsonObject)assignments.DeepClone(),(JsonObject)fingerprints.DeepClone());
    }
    static Task<JsonNode?> Inspect(SampleContext c, JsonObject a, CancellationToken t)
    { t.ThrowIfCancellationRequested(); var s = Shift(c,a.Text("shiftId")); _ = Interval(s); var roster = LoadRoster(c); return Task.FromResult<JsonNode?>(new JsonObject { ["shift"] = s.DeepClone(), ["employees"] = Rows(c.Input,"employees").DeepClone(), ["currentAssignee"] = Assigned(roster.Assignments,s) }); }

    static JsonObject Calculate(SampleContext c, string id, Roster? roster = null)
    {
        var target = Shift(c,id); _ = Interval(target); var candidates = new List<JsonObject>(); var excluded = new JsonArray();
        roster ??= LoadRoster(c); var employees = Employees(c);
        foreach (var e in employees)
        {
            var eid = e.Text("id"); var reasons = Reasons(c,target,e,roster.Assignments);
            var assigned = Rows(c.Input,"shifts").OfType<JsonObject>().Where(s => s.Text("id") != id && Assigned(roster.Assignments,s) == eid).ToArray();
            if (reasons.Count != 0) excluded.Add(new JsonObject { ["employeeId"] = eid, ["reasons"] = reasons });
            else candidates.Add(new JsonObject { ["employeeId"] = eid, ["assignedHours"] = assigned.Sum(s => { var r = Interval(s); return (r.End-r.Start).TotalHours; }) });
        }
        var ordered = candidates.OrderBy(x => x["assignedHours"]!.GetValue<double>()).ThenBy(x => x.Text("employeeId"), StringComparer.Ordinal).ToArray();
        return new() { ["shiftId"] = id, ["ranked"] = new JsonArray(ordered.Select(x => (JsonNode?)x).ToArray()), ["excluded"] = excluded, ["recommendedEmployeeId"] = ordered.FirstOrDefault()?.Text("employeeId") };
    }
    static async Task<JsonNode?> Rank(SampleContext c, JsonObject a, CancellationToken t)
    { var r = Calculate(c,a.Text("shiftId")); await c.WriteJsonAsync("coverage-ranking.json",r,t); return r; }
    static async Task<JsonNode?> Assign(SampleContext c, JsonObject a, CancellationToken t)
    {
        if (!c.Approved) throw new InvalidOperationException("Approval required for roster assignment.");
        t.ThrowIfCancellationRequested(); var id = a.Text("shiftId"); var eid = a.Text("employeeId"); var shift = Shift(c,id); var roster = LoadRoster(c); var existing = Assigned(roster.Assignments,shift);
        if (existing.Length > 0 && existing != eid) throw new InvalidOperationException("Shift already assigned to another employee.");
        if (!Calculate(c,id,roster)["ranked"]!.AsArray().Any(x => x?["employeeId"]?.GetValue<string>() == eid)) throw new InvalidOperationException("Employee is not eligible.");
        var repeat = existing == eid; roster.Assignments[id] = eid; roster.Fingerprints[id] = Fingerprint(shift,Employees(c).Single(e => e.Text("id") == eid));
        var result = new JsonObject { ["shiftId"] = id, ["employeeId"] = eid, ["alreadyAssigned"] = repeat, ["scope"] = "workspace-only", ["rosterVersion"] = 1, ["assignments"] = roster.Assignments.DeepClone(), ["fingerprints"] = roster.Fingerprints.DeepClone() };
        await c.WriteJsonAsync("roster-assignments.json",result,t); return result;
    }
}
