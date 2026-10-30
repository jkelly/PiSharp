using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.PersistentTaskManager;
public static class Workflow
{
    public static WorkflowSpec Create(SampleContext context) => new("persistent-task-manager", "Persistent task manager",
        "Read the durable task board. Before changing task state, ask for approval. Never claim a change without the tool result.",
        [
            new("list_tasks", "Read tasks.json or the fixture seed when no persisted board exists.", SampleJson.Schema(),
                (_, ct) => Load(context, ct)),
            new("inspect_task", "Read one task by ID from the durable board.", SampleJson.Schema("{\"id\":{\"type\":\"string\"}}", "id"),
                (a, ct) => Inspect(context, a, ct)),
            new("add_task", "Add a task and persist tasks.json.", SampleJson.Schema("{\"id\":{\"type\":\"string\"},\"title\":{\"type\":\"string\"}}", "id", "title"),
                (a, ct) => Add(context, a, ct), RequiresApproval: true),
            new("complete_task", "Mark a task done and persist tasks.json.", SampleJson.Schema("{\"id\":{\"type\":\"string\"}}", "id"),
                (a, ct) => Complete(context, a, ct), RequiresApproval: true)
        ],
        [new("list_tasks", new JsonObject()), new("inspect_task", SampleJson.Object("{\"id\":\"task-501\"}"))]);
    static async Task<JsonNode?> Load(SampleContext c, CancellationToken ct)
    {
        var path = c.ResolvePath("tasks.json");
        return File.Exists(path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct))?.AsObject() ?? throw new InvalidDataException("Invalid task board")
            : new JsonObject { ["tasks"] = c.Input["tasks"]?.DeepClone() ?? throw new InvalidOperationException("tasks missing") };
    }
    static async Task<JsonNode?> Inspect(SampleContext c, JsonObject a, CancellationToken ct)
    {
        var board = (await Load(c, ct))!.AsObject();
        var task = board["tasks"]?.AsArray().FirstOrDefault(x => x?["id"]?.GetValue<string>() == a.Text("id"))
            ?? throw new ArgumentException("Unknown task");
        return new JsonObject { ["task"] = task.DeepClone(), ["persisted"] = File.Exists(c.ResolvePath("tasks.json")) };
    }
    static async Task<JsonNode?> Add(SampleContext c, JsonObject a, CancellationToken ct)
    {
        if (!c.Approved) throw new UnauthorizedAccessException("Approval required to modify tasks.json");
        var id = a.Text("id").Trim(); var title = a.Text("title").Trim();
        if (id.Length == 0 || title.Length == 0) throw new ArgumentException("Task ID and title required");
        var board = (await Load(c, ct))!.AsObject();
        var tasks = board["tasks"]!.AsArray();
        if (tasks.Any(x => x?["id"]?.GetValue<string>() == id)) throw new ArgumentException("Duplicate task ID");
        tasks.Add(new JsonObject { ["id"] = id, ["title"] = title, ["status"] = "open" });
        await c.WriteJsonAsync("tasks.json", board, ct);
        return new JsonObject { ["id"] = id, ["status"] = "open", ["path"] = "tasks.json" };
    }
    static async Task<JsonNode?> Complete(SampleContext c, JsonObject a, CancellationToken ct)
    {
        if (!c.Approved) throw new UnauthorizedAccessException("Approval required to modify tasks.json");
        var board = (await Load(c, ct))!.AsObject();
        var task = board["tasks"]?.AsArray().FirstOrDefault(x => x?["id"]?.GetValue<string>() == a.Text("id"))
            ?? throw new ArgumentException("Unknown task");
        task["status"] = "done";
        await c.WriteJsonAsync("tasks.json", board, ct);
        return new JsonObject { ["id"] = a.Text("id"), ["status"] = "done", ["path"] = "tasks.json" };
    }
}
