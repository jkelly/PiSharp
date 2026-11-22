---
title: Crash recovery, retries & replay safety
description: Survive process crashes mid-turn or mid-workflow without duplicating side effects, using OnceAsync, IdempotencyKey, ToolReplay, RunKey and WorkflowMigration.
---

**What happens in production:** A server deploys, a container gets OOM-killed, or a laptop sleeps **after** an agent called a payment API or received an expensive model reply — and **before** the step's result was persisted. Without durable checkpoints and idempotency keys, restarting either loses the whole workflow or double-charges the customer.

## What PiSharp Durable guarantees on restart

| What was in flight at crash time | Behavior on reopen (`Harness.OpenAsync` / `AgentTeam.OpenAsync`) |
| --- | --- |
| **Workflow action (`.Then(...)`)** | Re-enters that action from the last committed `TData` with `context.IsReplay = true`. Any `OnceAsync` values already recorded are returned without re-running their delegates. |
| **Workflow LLM call (`Prompt` / `PromptAsync`)** | Uses deterministic request ID `wf:<task>:<step>:<key>:<attempt>`. If the model already replied before the crash, replay reuses the recorded reply (**0 new API calls**). |
| **Tool with `ToolReplay.Unsafe` *(default)*** | **Not rerun.** Commits an `"interrupted"` error result so the model checks workspace state before retrying. |
| **Tool with `ToolReplay.Safe`** | Reruns cleanly, returning any memoized `tool.OnceAsync(...)` results already recorded on that tool task. |
| **Durable timer (`Sleep`, `Repeat`, `WaitForSignal` timeout)** | Resumes against the stored deadline timestamp (not restarted from zero). |
| **Crash-looping task** | After 5 consecutive process crashes on the same task, Durable halts the task safely instead of looping forever. |

## Recipe 1: Idempotent external side effects with `OnceAsync` & retries

Combine `context.OnceAsync`, `context.IdempotencyKey`, `RetryPolicy.Exponential`, and `Timeout` whenever an action calls an external service:

```csharp
using PiSharp.CustomAgents.Workflows;

var billing = Workflow.For<OrderData>("billing")
    .Then("charge", async (order, context, token) =>
    {
        var payments = context.GetRequiredService<IPayments>();
        // Key is deterministic across retries and crash replays: wf:<task>:<step>:charge
        order.ChargeId = await context.OnceAsync("charge",
            ct => payments.ChargeAsync(order.OrderId, order.Payable, context.IdempotencyKey("charge"), ct),
            token);
    }, new ActionOptions
    {
        Retry = RetryPolicy.Exponential(attempts: 4, delay: TimeSpan.FromSeconds(2)) with
        {
            RetryOn = ex => ex is HttpRequestException or TimeoutException,
        },
        Timeout = TimeSpan.FromSeconds(30),
    })
    .Then("await-settlement", async (order, context, token) =>
    {
        var payments = context.GetRequiredService<IPayments>();
        if (await payments.IsSettledAsync(order.ChargeId!, token)) return ActionResult.Next();
        return ActionResult.Repeat(TimeSpan.FromMinutes(5));
    }, new ActionOptions { MaxVisits = 12 })
    .Build();
```

For agent tools (`AgentTool.Create<TArgs>`), use `tool.OnceAsync("refund", ...)` and `tool.IdempotencyKey("refund")` together with `new AgentToolOptions { Replay = ToolReplay.Safe, Sequential = true }`.

## Recipe 2: Idempotent workflow starts (`RunKey`) & questions (`requestId`)

Pass stable business keys so restarting your host process attaches to in-progress work instead of spawning duplicates:

```csharp
// Workflow start: same RunKey attaches to the existing run (run.Existing == true)
var run = await session.Workflows.StartAsync(orders, new OrderData { OrderId = "42" },
    new WorkflowStartOptions { RunKey = "order-42" });

// Agent question: same requestId returns the settled answer with 0 new model calls
var reply = await session.AskAsync("Summarize order 42.", requestId: "order-42-summary");
```

## Recipe 3: Evolving live workflows safely (`WorkflowMigration`)

When a SQLite database holds unfinished runs from v1 and your new deployment renames an action or adds a required field, bump `.Version(...)` and supply a migration:

```csharp
using System.Text.Json.Nodes;
using PiSharp.CustomAgents.Workflows;

var ordersV2 = Workflow.For<OrderData>("orders")
    .Version(2, WorkflowMigration.Compose(
        WorkflowMigration.RenameActions(("thank-you", "thanks")),
        WorkflowMigration.TransformData((node, fromVersion) =>
        {
            if (node is JsonObject obj && obj["note"] is null) obj["note"] = "";
            return node;
        })))
    .Then(new LoadOrderAction())
    .Prompt("thanks", order => $"Thank {order.Customer!.Name}.", (order, text) => order.Note = text)
    .Build();
```

Bumping the version *without* a migration intentionally blocks older stored runs as `MigrationFailed`, preventing silent corruption.

## Recipe 4: Parked webhook & polling tools *(1.6.0 source candidate)*

:::note[1.6.0 source candidate (unreleased)]
On `sync/pi-1.6.0`, Durable tools can **park** between polls or while awaiting an external webhook without holding a thread (`40-tool-waits-for-webhook`, `41-tool-polls`). When the webhook signal arrives (or the deadline expires), the tool task wakes cleanly — even if the host restarted while it was parked.
:::

## Pitfalls & limits

- **`OnceAsync` is at-least-once:** if the process dies *after* the external API succeeded and *before* `OnceAsync` committed the result to SQLite, the delegate runs again — which is why passing `context.IdempotencyKey(...)` to the external API is essential.
- **Keep `OnceAsync` return values small:** they are stored alongside the run; store large payloads externally and record their ID.
- **Do not use `MemoryStorage` for restart tests:** `MemoryStorage` cannot be reopened; always use `SqliteStorage` or `JsonlStorage` when testing crash recovery.

## Try it now

```powershell
# Real process kill (exit code 3) right after LLM reply, followed by SQLite resume with 0 new LLM calls:
dotnet run --project samples/CustomAgents.Examples -c Release -- orders --crash-in summarize
dotnet run --project samples/CustomAgents.Examples -c Release -- orders --crash-in summarize

# Durable crash recovery, hot reload + restart, and (1.6.0 candidate) parked webhook/polling tools:
dotnet run --project samples/Durable.Examples -- --storage sqlite 12-recovery 30-reload-and-restart
dotnet run --project samples/Durable.Examples -- --storage sqlite 40-tool-waits-for-webhook 41-tool-polls
```

Read the full annotated breakdown in [Walkthrough: Hybrid Orders Workflow](/docs/samples/hybrid-orders-workflow/).
