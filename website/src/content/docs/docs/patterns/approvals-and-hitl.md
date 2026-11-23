---
title: Approvals & human-in-the-loop
description: Gate risky tool writes, pause workflows for multi-day approvals, ask structured user questions and enforce plan mode before changes happen.
---

**What happens in production:** An agent wants to delete files, update a database, submit a purchase order, or publish a pull request — or needs a human decision before continuing. PiSharp deliberately ships no hardcoded modal permission popups in its core loop; instead it gives you explicit, testable approval gates at every layer.

## Choose the right approval mechanism

| Need | Mechanism | Survives process restart? | Layer |
| --- | --- | --- | --- |
| Block or allow a specific tool call synchronously in your host | `IToolHooks.BeforeExecutionAsync` or `IToolActionPolicy.AuthorizeAsync` | No (in-memory) | `PiSharp.Agent` / `PiSharp.Tools` |
| Explicit `--approve` flag enforcing write boundaries in CLI/automation agents | Guarded `ToolSpec(..., RequiresApproval: true)` checking host approval state | No (per run) | `PiSharp.Agent` (used across 21 of the 62 agent samples) |
| Interactive multiple-choice decision or plan sign-off in terminal UI | `ask_user_question` and `/plan` (`exit_plan_mode`) | Yes | `pisharp durable` |
| Pause a workflow for hours or days waiting for an external webhook or UI click | `Workflow.WaitForSignal<T>(..., timeout, onTimeout)` | Yes (SQLite / JSONL / Chord) | `PiSharp.CustomAgents` |
| Let an autonomous team agent escalate ad-hoc questions to your app | `AgentDefinition.CanAskHost = true` + `session.Host.WatchMailboxAsync()` | Yes | `PiSharp.CustomAgents` |
| Park a risky Durable tool call inside `BeforeTool` until approved or expired | `ToolHooks.BeforeTool` + typed `Signals.DefineSignal` *(from 1.6.0)* | Yes | `PiSharp.Durable` *(1.6.0)* |

## Recipe 1: Synchronous preflight policy (`PiSharp.Agent`)

Use `IToolHooks` when you want to inspect every tool invocation before it executes and return a structured `Blocked` error that tells the model why the action was denied:

```csharp
using PiSharp.Agent;

sealed class ApprovalGateHook(Func<ToolInvocation, bool> isApproved) : IToolHooks
{
    private static readonly HashSet<string> GuardedTools = ["complete_task", "submit_purchase", "delete_file"];

    public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken ct)
    {
        if (GuardedTools.Contains(invocation.Call.Name) && !isApproved(invocation))
        {
            return ValueTask.FromResult(new ToolPreflightDecision(
                Block: true,
                Reason: $"Approval required before calling '{invocation.Call.Name}'. Re-run with approval enabled."));
        }
        return ValueTask.FromResult(ToolPreflightDecision.Allow);
    }

    public ValueTask<ToolResult> AfterExecutionAsync(ToolInvocation invocation, ToolResult result, CancellationToken ct) =>
        ValueTask.FromResult(result);
}
```

When using `PiSharp.Tools` (`read`, `write`, `edit`, `bash`), plug an `IToolActionPolicy` into `ToolInvoker` so your policy inspects the resolved canonical path or shell command produced by `IPreparedToolAdapter`, not raw model strings.

## Recipe 2: Multi-day durable approval (`Workflow.WaitForSignal`)

When approval arrives from a web UI, Slack button, or manager hours later, park the workflow cleanly with a persisted deadline:

```csharp
using PiSharp.CustomAgents.Workflows;
using PiSharp.CustomAgents.Workflows.Hosting;

var purchase = Workflow.For<PurchaseOrder>("purchase-approval")
    .Then("validate", order => order.NeedsFinance = order.TotalCents > 50_000)
    .Branch(order => order.NeedsFinance, "await-finance", "submit")
    .WaitForSignal<ApprovalDecision>("await-finance", "finance-decision",
        (order, decision) =>
        {
            order.Approved = decision.Approved;
            order.Approver = decision.Approver;
        },
        timeout: TimeSpan.FromDays(2),
        onTimeout: "expired")
    .Branch(order => order.Approved, "submit", "rejected")
    .Then("submit", order => order.Status = "submitted", new ActionOptions { Next = Workflow.End })
    .Then("rejected", order => order.Status = "rejected", new ActionOptions { Next = Workflow.End })
    .Then("expired", order => order.Status = "expired")
    .Build();

// Later, even in a completely new process after restart:
var run = await session.Workflows.FindAsync(purchase, runKey: "po-100");
await run!.SignalAsync("finance-decision", new ApprovalDecision(true, "finance@example.com"), id: "po-100-decision");
```

The signal's `id` makes delivery idempotent (duplicate webhook clicks return `false` without applying twice), and the 2-day timeout deadline is stored in SQLite so restarts never reset the clock.

## Recipe 3: Durable `BeforeTool` gate *(from 1.6.0)*

:::note[New in 1.6.0]
Durable signals and `BeforeTool` gates ship in PiSharp 1.6.0. See `samples/Durable.Examples/ExamplesGates.cs` (`44-approval-gate`).
:::

In Durable 1.6.0, an extension can intercept a risky `bash` call inside `ToolHooks.BeforeTool`, key the decision by a canonical digest of the arguments, fix a deadline once with `api.MemoAsync`, and park the tool task on a typed signal (`Signals.DefineSignal`). Denial or expiry blocks the call cleanly and reports the reason back to the model.

## Expected behavior: denied vs. approved

Run [05-PersistentTaskManager](/docs/samples/persistent-task-manager/) both ways to observe the contrast:

- **Without `--approve`:** `list_tasks` and `inspect_task` succeed; calling `complete_task` fails with `Approval required to modify tasks.json`, leaving disk untouched.
- **With `--approve`:** `complete_task` writes `tasks.json` in the workspace and subsequent runs read the persisted state.

## Pitfalls & guardrails

- **Always enforce approval in the tool executor or hook, never only in the system prompt.** Communicate approval state to the model so it avoids wasted calls, but treat the model as untrusted and enforce the gate in C#.
- **Pin approval to the exact arguments.** If a human approved `rm build/tmp`, key the approval by a hash of the concrete arguments (as `44-approval-gate` does) so a different command cannot reuse it.
- **Respect `WaitForSignal` queue limits:** at most 100 untaken signals may sit on one workflow run before `SignalAsync` throws `InvalidOperationException`.

## Try it now

```powershell
# Single-agent write gate (denied vs. approved)
dotnet run --project samples/agents/05-PersistentTaskManager -- --demo
dotnet run --project samples/agents/05-PersistentTaskManager -- --persist-demo --approve --workspace artifacts/task-board

# Budget-routed purchase approval
dotnet run --project samples/agents/40-PurchaseRequestCoordinator -- --demo --approve

# Durable plan mode & 1.6.0 BeforeTool approval gate
dotnet run --project samples/Durable.Examples -- --storage sqlite 26-plan-mode
dotnet run --project samples/Durable.Examples -- --storage sqlite 39-ask-a-person 44-approval-gate
```

Browse all approval-gated examples in the [Samples Explorer](/samples/?pattern=Approval+gate).
