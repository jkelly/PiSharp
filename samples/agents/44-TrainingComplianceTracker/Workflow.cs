// PiSharp-only (no upstream Pi source): date-grounded training compliance and local reminder drafts.
using System.Globalization;
using System.Text.Json.Nodes;
namespace PiSharp.Samples.TrainingComplianceTracker;

public static class Workflow
{
    static JsonObject Schema() => SampleJson.Schema("""{"employeeId":{"type":"string"}}""", "employeeId");
    public static WorkflowSpec Create(SampleContext c) => new("44-TrainingComplianceTracker", "Training compliance tracker",
        "For every requested employee call inspect_training, assess_compliance and draft_reminders in order. Use the fixture asOf date. Report actual completion evidence and explicit unknowns. Reminder and manager escalation outputs are local drafts only; do not claim messages were sent.",
        [new("inspect_training", "Read an employee's requirements and completion evidence", Schema(), (a,t) => Inspect(c,a,t)),
         new("assess_compliance", "Compute due dates and expiry at the fixture asOf date", Schema(), (a,t) => Assess(c,a,t)),
         new("draft_reminders", "Write grounded employee reminders and overdue manager escalations", Schema(), (a,t) => Draft(c,a,t))],
        [new("inspect_training", new() { ["employeeId"] = "E1" }), new("assess_compliance", new() { ["employeeId"] = "E1" }), new("draft_reminders", new() { ["employeeId"] = "E1" })]);
    static JsonArray Rows(JsonObject o, string k) => o[k] as JsonArray ?? throw new FormatException("Missing " + k);
    static DateOnly Date(JsonNode? n) => DateOnly.ParseExact(n?.GetValue<string>() ?? throw new FormatException("Missing date"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
    static JsonObject Employee(SampleContext c, string id) => Rows(c.Input,"employees").OfType<JsonObject>().SingleOrDefault(e => e.Text("id") == id) ?? throw new ArgumentException("Unknown employee: " + id);
    static Task<JsonNode?> Inspect(SampleContext c, JsonObject a, CancellationToken t)
    { t.ThrowIfCancellationRequested(); return Task.FromResult<JsonNode?>(new JsonObject { ["employee"] = Employee(c,a.Text("employeeId")).DeepClone(), ["requirements"] = Rows(c.Input,"requirements").DeepClone(), ["asOf"] = c.Input["asOf"]!.DeepClone() }); }
    static JsonObject Calculate(SampleContext c, string id)
    {
        var e = Employee(c,id); var now = Date(c.Input["asOf"]); var hire = Date(e["hireDate"]); if (hire > now) throw new FormatException("Hire date is after asOf.");
        var result = new JsonArray();
        foreach (var requirement in Rows(c.Input,"requirements").OfType<JsonObject>().Where(r => r.Text("role") == e.Text("role")))
        {
            var course = requirement.Text("courseId"); var validity = requirement["validDays"]?.GetValue<int>() ?? 0; var initial = requirement["initialDueDays"]?.GetValue<int>() ?? -1;
            if (course.Length == 0 || validity <= 0 || initial < 0) throw new FormatException("Invalid training requirement.");
            var completions = Rows(e,"completions").OfType<JsonObject>().Where(x => x.Text("courseId") == course).Select(x => Date(x["completedOn"])).ToArray();
            if (completions.Any(d => d > now || d < hire)) throw new FormatException("Completion date lies outside employment/asOf evidence window.");
            DateOnly? latest = completions.Length == 0 ? null : completions.Max(); var due = latest?.AddDays(validity) ?? hire.AddDays(initial);
            var days = due.DayNumber - now.DayNumber;
            var status = latest is null ? (days < 0 ? "missing-overdue" : "missing-due") : days < 0 ? "expired" : days <= 30 ? "due-soon" : "current";
            result.Add(new JsonObject { ["courseId"] = course, ["lastCompletedOn"] = latest?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture), ["dueOn"] = due.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture), ["daysUntilDue"] = days, ["status"] = status });
        }
        if (result.Count == 0) throw new InvalidOperationException("No training requirements found for role.");
        return new() { ["employeeId"] = id, ["asOf"] = now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture), ["managerId"] = e.Text("managerId"), ["courses"] = result };
    }
    static async Task<JsonNode?> Assess(SampleContext c, JsonObject a, CancellationToken t)
    { var r = Calculate(c,a.Text("employeeId")); c.State["assessment:" + a.Text("employeeId")] = r.DeepClone(); await c.WriteJsonAsync("training-assessment.json",r,t); return r; }
    static async Task<JsonNode?> Draft(SampleContext c, JsonObject a, CancellationToken t)
    {
        var id = a.Text("employeeId"); if (c.State["assessment:" + id] is null) throw new InvalidOperationException("Assess this employee first.");
        var assessment = Calculate(c,id); var reminders = new JsonArray(); var escalations = new JsonArray();
        foreach (var row in assessment["courses"]!.AsArray().OfType<JsonObject>().Where(r => r.Text("status") != "current"))
        {
            reminders.Add(new JsonObject { ["employeeId"] = id, ["courseId"] = row.Text("courseId"), ["text"] = $"As of {assessment.Text("asOf")}, {row.Text("courseId")} is {row.Text("status")}; due {row.Text("dueOn")}.", ["evidence"] = row.DeepClone() });
            if (row["daysUntilDue"]!.GetValue<int>() < 0) escalations.Add(new JsonObject { ["managerId"] = assessment.Text("managerId"), ["deliveryReady"] = assessment.Text("managerId").Length > 0, ["employeeId"] = id, ["courseId"] = row.Text("courseId"), ["reason"] = row.Text("status"), ["dueOn"] = row.Text("dueOn") });
        }
        var r = new JsonObject { ["employeeId"] = id, ["scope"] = "local drafts; not sent", ["reminders"] = reminders, ["escalations"] = escalations };
        await c.WriteJsonAsync("training-reminders.json",r,t); return r;
    }
}
