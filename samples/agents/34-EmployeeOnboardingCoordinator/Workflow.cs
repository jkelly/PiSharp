// PiSharp-only (no upstream Pi source): dependency-aware onboarding plans from local records.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.EmployeeOnboardingCoordinator;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c)
    {
        var schema = SampleJson.Schema("{\"employeeId\":{\"type\":\"string\"}}", "employeeId");
        var args = Args(c.Input.Text("employeeId"));
        return new("34", "Employee Onboarding Coordinator",
            "Run inspect_employee, build_checklist and save_onboarding_plan for the requested employeeId. Compute role-specific prerequisites and owner assignments from fixture records. Assignments are a reviewable local plan; no accounts or HR tasks are created remotely.",
            [new("inspect_employee", "Inspect employee, role and owner records.", schema, (a, _) => Task.FromResult<JsonNode?>(Inspect(c, a.Text("employeeId")))),
             new("build_checklist", "Order role-specific tasks by prerequisites and compute readiness, owners and due dates.", schema, (a, _) => Task.FromResult<JsonNode?>(Plan(c, a.Text("employeeId")))),
             new("save_onboarding_plan", "Save a local onboarding checklist and proposed assignments.", schema, async (a, ct) => { var id = Id(a.Text("employeeId")); var plan = Plan(c, id); var path = $"onboarding-{id}.json"; await c.WriteJsonAsync(path, plan, ct); return new JsonObject { ["artifact"] = path, ["plan"] = plan }; })],
            [new("inspect_employee", (JsonObject)args.DeepClone()), new("build_checklist", (JsonObject)args.DeepClone()), new("save_onboarding_plan", (JsonObject)args.DeepClone())]);
    }
    internal static JsonObject Args(string id) => new() { ["employeeId"] = id };
    static string Id(string id) => id.Length is > 0 and <= 64 && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') ? id : throw new ArgumentException("Invalid employee ID.");
    static JsonArray Rows(JsonObject obj, string key) => obj[key] as JsonArray ?? throw new ArgumentException($"Missing array: {key}");
    static IEnumerable<JsonObject> Objects(JsonArray rows) => rows.Select(n => n as JsonObject ?? throw new ArgumentException("Every record must be a JSON object."));
    static JsonObject One(JsonArray rows, string id) => Objects(rows).Where(x => x.Text("id") == id).ToArray() is [var row] ? row : throw new ArgumentException($"Missing or ambiguous ID: {id}");
    internal static JsonObject Inspect(SampleContext c, string id)
    {
        var employee = One(Rows(c.Input, "employees"), Id(id));
        var role = One(Rows(c.Input, "roles"), employee.Text("roleId"));
        return new() { ["employee"] = employee.DeepClone(), ["role"] = role.DeepClone(), ["owners"] = Rows(c.Input, "owners").DeepClone() };
    }
    internal static JsonObject Plan(SampleContext c, string id)
    {
        var data = Inspect(c, id); var employee = data["employee"]!.AsObject(); var role = data["role"]!.AsObject();
        var tasks = Objects(Rows(c.Input, "commonTasks")).Concat(Objects(Rows(role, "tasks"))).ToArray();
        if (tasks.Select(t => t.Text("id")).Any(string.IsNullOrWhiteSpace) || tasks.Select(t => t.Text("id")).Distinct(StringComparer.Ordinal).Count() != tasks.Length) throw new ArgumentException("Task IDs must be nonempty and unique.");
        var completed = Rows(employee, "completed").Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        if (completed.Except(tasks.Select(t => t.Text("id")), StringComparer.Ordinal).Any()) throw new ArgumentException("Completion refers to an unknown task.");
        var ordered = new List<JsonObject>(); var active = new HashSet<string>(StringComparer.Ordinal); var done = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string taskId)
        {
            if (done.Contains(taskId)) return;
            if (!active.Add(taskId)) throw new ArgumentException("Cyclic onboarding prerequisites.");
            var task = tasks.SingleOrDefault(t => t.Text("id") == taskId) ?? throw new ArgumentException($"Unknown prerequisite: {taskId}");
            foreach (var prerequisite in Rows(task, "requires")) Visit(prerequisite!.GetValue<string>());
            active.Remove(taskId); done.Add(taskId); ordered.Add(task);
        }
        foreach (var task in tasks) Visit(task.Text("id"));
        var start = DateOnly.ParseExact(employee.Text("startDate"), "yyyy-MM-dd"); var checklist = new JsonArray();
        foreach (var task in ordered)
        {
            var ownerKey = task.Text("owner"); var ownerId = ownerKey == "manager" ? employee.Text("managerId") : ownerKey;
            var owner = One(Rows(c.Input, "owners"), ownerId);
            var missing = Rows(task, "requires").Select(n => n!.GetValue<string>()).Where(p => !completed.Contains(p)).ToArray();
            var complete = completed.Contains(task.Text("id"));
            if (complete && missing.Length > 0) throw new ArgumentException("Completed task has incomplete prerequisites.");
            checklist.Add(new JsonObject { ["taskId"] = task.Text("id"), ["title"] = task.Text("title"), ["ownerId"] = owner.Text("id"), ["dueDate"] = start.AddDays(task["daysFromStart"]!.GetValue<int>()).ToString("yyyy-MM-dd"), ["status"] = complete ? "complete" : missing.Length == 0 ? "ready" : "blocked", ["waitingFor"] = new JsonArray(missing.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) });
        }
        return new() { ["employeeId"] = id, ["roleId"] = role.Text("id"), ["startDate"] = employee.Text("startDate"), ["assignmentsAreProposed"] = true, ["checklist"] = checklist, ["readyCount"] = checklist.Count(n => n!["status"]!.GetValue<string>() == "ready"), ["blockedCount"] = checklist.Count(n => n!["status"]!.GetValue<string>() == "blocked") };
    }
}
