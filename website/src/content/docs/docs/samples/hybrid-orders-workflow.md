---
title: "Walkthrough: Hybrid orders workflow & team"
description: Annotated guide to samples/CustomAgents.Examples — a 7-step crash-safe C#, Prompt, JS, Roslyn and Agent workflow plus a 4-agent team with --crash-in resume.
---

:::caution[Experimental]
`PiSharp.CustomAgents`, `PiSharp.CustomAgents.JavaScript` and `PiSharp.CustomAgents.Scripting` are experimental (`PISHARP_AGENTS001` and `PISHARP_DURABLE001`).
:::

[`samples/CustomAgents.Examples`](https://github.com/jkelly/PiSharp/tree/main/samples/CustomAgents.Examples) showcases nearly every capability of `PiSharp.CustomAgents` in one offline-runnable program:
- **`orders`:** a 7-step durable workflow operating on one shared `OrderData` class using five different step types (C# `IAction<T>`, `ActionContext.PromptAsync`, typed `Prompt<RiskVerdict>`, sandboxed Jint JavaScript, Roslyn C# script, and an `Agent` step).
- **`team`:** a 4-agent team (`lead`, `researcher`, `concierge`, `scout`) where each agent sees only its own tools and the `lead` invokes the `orders` workflow as a tool (`run_workflow_orders`).
- **`web`:** live or canned `web_search` through any of 7 search backends.

## Run it in 30 seconds

```powershell
# 1. Run everything offline on FauxAgentModels + SQLite (./custom-agents-demo/agents.sqlite)
dotnet run --project samples/CustomAgents.Examples -c Release -- all

# 2. Simulate a real process crash (exit code 3) inside 'summarize', then resume from SQLite:
dotnet run --project samples/CustomAgents.Examples -c Release -- orders --crash-in summarize
dotnet run --project samples/CustomAgents.Examples -c Release -- orders --crash-in summarize

# 3. Run against a real provider (uses OPENAI_API_KEY / XAI_API_KEY and a fresh storage directory)
dotnet run --project samples/CustomAgents.Examples -c Release -- --provider openai --model gpt-5.4-mini --dir ./demo-openai
```

## The 7-step `orders` workflow

Every action mutates the shared `OrderData` instance; after each action completes, the workflow engine commits both `OrderData` and the next step name atomically to SQLite:

| Step | Action kind | What it does | Crash behavior |
| --- | --- | --- | --- |
| `1. load` | `IAction<OrderData>` (`LoadOrderAction`) | Loads customer & lines from `OrderStore`; returns `ActionResult.Fail` for unknown IDs. | Safe read; reruns if killed before commit. |
| `2. summarize` | `.Then(...)` calling `context.PromptAsync` | Asks the model for a one-sentence support summary. | Deterministic request ID reuses the recorded LLM reply on replay (**0 new LLM calls**). |
| `3. risk` | `.Prompt<RiskVerdict>` | Structured output via `submit_result`, validated against `RiskVerdict` (`Level`, `Reason`). | Auto-repairs malformed JSON up to `MaxRepairs`; commits typed `RiskVerdict`. |
| `4. discount` | `.Script(...)` (Jint JavaScript) | Computes 15% VIP / 5% volume discount in the sandbox and returns an RFC 7386 `updates` patch. | Pure sandboxed step; always replay-safe. |
| `5. priority` | `.CSharp(...)` (Roslyn script) | Sets `express` / `standard`, or sets `manual-review` and returns `ActionResult.Stop()` on high risk. | Compiled once and cached by content hash. |
| `6. vip-note` | `.Branch(...)` → `.Agent("concierge")` | Runs the `concierge` team agent for VIP customers (`order 42`), then jumps to `Workflow.End`. | Durable child conversation (`concierge#1`) resumes cleanly. |
| `7. thank-you` | `.Prompt(...)` | Non-VIP customers (`order 43`) get a one-line thank-you note. | Commits final `OrderData` and completes. |

Here is the core pipeline straight from `OrdersDemo.cs`:

```csharp
Workflow.For<OrderData>("orders")
    .Then(new LoadOrderAction(crash))
    .Then("summarize", async (data, context, token) =>
    {
        data.Summary = await context.PromptAsync($"Summarize order {data.OrderId}...", cancellationToken: token);
        crash.Point(context); // exits with code 3 before step commit when --crash-in summarize is set
    })
    .Prompt<RiskVerdict>("risk",
        data => $"Assess the payment and fraud risk of this order: {data.Summary}",
        (data, verdict) => data.Risk = verdict)
    .Script("discount", """
        const percent = data.customer.vip ? 15 : (data.total >= 100 ? 5 : 0);
        return { updates: { discountPercent: percent, payable: Math.round(data.total * (100 - percent)) / 100 } };
        """)
    .CSharp("priority", """
        Data.Priority = Data.Risk?.Level?.ToLowerInvariant() switch
        {
            "high" => "manual-review",
            _ when Data.Customer!.Vip => "express",
            _ => "standard",
        };
        if (Data.Priority == "manual-review") return ActionResult.Stop();
        """, host)
    .Branch(data => data.Customer!.Vip, "vip-note", "thank-you")
    .Agent("vip-note", TeamDemo.Concierge,
        data => $"Write a two-sentence note to VIP customer {data.Customer!.Name}...",
        (data, answer) => data.Note = answer.Text,
        action: new ActionOptions { Next = Workflow.End })
    .Prompt("thank-you",
        data => $"Write a one-line thank-you to {data.Customer!.Name}...",
        (data, text) => data.Note = text)
    .Build();
```

## How the 4-agent team composes heterogeneous tools

In `TeamDemo.cs`, `lead` answers a customer question by combining **four different tool mechanisms** without ever exposing its tools to its subagents:

| Agent | Registered tools | Tool mechanism |
| --- | --- | --- |
| `lead` *(orchestrator)* | `lookup_order`, `shipping_quote`, `run_workflow_orders`, `workflow_status`, `delegate` | Typed C# + pure Jint JS + Workflow-as-a-tool (`WorkflowTools.RunTool`) + Delegation |
| `researcher` | `check_stock` | Roslyn C# script (`ScriptTool.CreateAsync<CheckStockArgs>`) |
| `concierge` | *(none)* | Called directly by the `vip-note` workflow step |
| `scout` | `web_search` | `WebSearchTool.Create(...)` (canned offline, or live backend) |

## Try breaking it

1. **Observe zero-duplicate LLM replay after a crash:** run `orders --crash-in summarize --dir ./crash-lab` twice. Process 1 exits with code `3` right after `summarize` receives its model answer; process 2 logs `"rerun after a restart: the model's recorded answer was reused"` and finishes without re-calling the model for `summarize`.
2. **Test `RunKey` idempotency:** run `orders --dir ./crash-lab` a **third** time. Because each order starts with `new WorkflowStartOptions { RunKey = "order-" + orderId }`, the runner prints `attached to run ... started earlier` and sends `0` model requests.
3. **Inspect tool isolation:** check `TeamDemo.cs` — `researcher` receives only `check_stock` and never sees `lookup_order` or `shipping_quote`, demonstrating strict per-definition tool scoping.

## Related pages

- [Crash recovery, retries & replay safety](/docs/patterns/crash-recovery-and-idempotency/)
- [Workflows reference](/docs/custom-agents/workflows/) · [Custom agent samples reference](/docs/custom-agents/samples/)
