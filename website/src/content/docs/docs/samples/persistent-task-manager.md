---
title: "Walkthrough: Approval task agent"
description: Annotated guide to samples/agents/05-PersistentTaskManager — a single-agent PiSharp.Agent loop with explicit RequiresApproval write gates and JSON persistence.
---

[`samples/agents/05-PersistentTaskManager`](https://github.com/jkelly/PiSharp/tree/main/samples/agents/05-PersistentTaskManager) is the cleanest starting point for a stateful **`PiSharp.Agent`** application. It separates **read-only tools** (always allowed) from **mutating tools** (blocked unless `--approve` is passed) and persists state cleanly across runs.

- **Runtime:** `PiSharp.Agent` + `PiSharp.AI` (stable, no experimental diagnostics)
- **Patterns demonstrated:** [Approvals & human-in-the-loop](/docs/patterns/approvals-and-hitl/), [Offline testing & live validation](/docs/patterns/testing-and-evals/)

## Run it in 30 seconds

```powershell
# 1. Offline read-only demo (inspects fixture without mutating disk)
dotnet run --project samples/agents/05-PersistentTaskManager -- --workspace artifacts/task-demo

# 2. Persist a state change across runs with explicit --approve
dotnet run --project samples/agents/05-PersistentTaskManager -- --persist-demo --approve --workspace artifacts/task-board

# 3. Deterministic self-test suite
dotnet run --project samples/agents/05-PersistentTaskManager -- --self-test

# 4. Live model run with explicit approval enabled
dotnet run --project samples/agents/05-PersistentTaskManager -- --live --approve --model gpt-6.2-sol --api openai-responses --workspace artifacts/task-live
```

## Tools and approval boundary

| Tool | Purpose | Guard |
| --- | --- | --- |
| `list_tasks` | Reads persisted `tasks.json` in the workspace, falling back to `fixture.json` seed when no file exists yet. | Read-only |
| `inspect_task` | Fetches one task by `id` and reports whether the board is already persisted (`persisted: true/false`). | Read-only |
| `add_task` | Appends a unique task `(id, title)` and writes `tasks.json`. | `RequiresApproval: true` |
| `complete_task` | Marks an existing task `done` and writes `tasks.json`. | `RequiresApproval: true` |

## Key code walkthrough

Inside `Workflow.cs`, the `WorkflowSpec` declares all four tools with explicit JSON Schemas and marks only the mutating tools with `RequiresApproval: true`:

```csharp
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
```

Notice the **defense-in-depth rule** inside `Complete`:

```csharp
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
```

Two safeguards work together:
1. The shared `SampleHost` checks `RequiresApproval` before executing the tool **and** tells the model whether approval is active.
2. The tool handler itself re-verifies `c.Approved` and throws `UnauthorizedAccessException` if invoked directly without approval — so even custom entry points like `--persist-demo` cannot bypass the gate.

## Try breaking it

1. **Omit `--approve` on `--persist-demo`:** run `dotnet run --project samples/agents/05-PersistentTaskManager -- --persist-demo` and verify it exits with code `2` (`--persist-demo requires --approve`) without touching disk.
2. **Run `--persist-demo --approve` twice against the same `--workspace`:** open `artifacts/task-board/tasks.json` after the first run, then run the regular `--demo --workspace artifacts/task-board` and confirm `inspect_task` now returns `"persisted": true` with `"status": "done"`.
3. **Supply a custom fixture:** copy `samples/agents/05-PersistentTaskManager/fixture.json`, add a new open task, and pass `--input my-tasks.json --live --model gpt-6.2-sol --api openai-responses --prompt "Mark my new task complete"` with and without `--approve`.

## Related pages

- [Approvals & human-in-the-loop pattern](/docs/patterns/approvals-and-hitl/)
- [Browse all 21 approval-gated samples](/samples/?pattern=Approval+gate)
