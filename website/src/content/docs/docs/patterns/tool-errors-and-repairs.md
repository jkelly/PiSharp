---
title: Tool errors, validation & typed repairs
description: Handle invalid tool arguments, domain invariant failures and malformed structured outputs so the agent recovers cleanly instead of crashing.
---

**What happens in production:** A model invents an order ID that does not exist, sends a floating-point dollar value where integer cents are required, calls a tool out of order, or returns prose when you asked for structured JSON. Throwing unhandled exceptions out of your loop breaks the user's session; returning structured, specific error results lets the model self-correct on the next turn.

## How PiSharp routes errors back to the model

| Layer | Validation & error conversion | What the model receives |
| --- | --- | --- |
| **`PiSharp.Agent`** | Unhandled exceptions from `IToolExecutor` are caught and mapped to `ToolFailureKind.ExecutionError`; return `ToolResult.Error(ToolFailureKind.InvalidArguments, ...)` for bad input. | A `toolResult` message with `isError: true` and your explanation text. |
| **`PiSharp.Contracts.Schema`** | `JsonSchemaValidator` / `CoercingJsonSchemaValidator` *(experimental, `PISHARP_SCHEMA001`)* validates arguments against JSON Schema (`pi-schema`). | Structured validation errors pointing to the exact failing property. |
| **`PiSharp.CustomAgents` tools** | `AgentTool.Create<TArgs>` validates arguments against the C# record's schema before invoking your handler. Return `AgentToolResult.Error("...")` for domain failures. | `"Invalid arguments for '<name>': ..."` or your explicit error text. |
| **`PiSharp.CustomAgents` typed prompts** | `Prompt<TResult>` / `PromptAsync<T>` exposes a `submit_result` tool matching `TResult`'s schema and automatically runs up to `MaxRepairs` (default `2`) repair rounds. | `"Your answer could not be used: <error>. Call the submit_result tool with arguments that match its schema."` |

## Recipe 1: Actionable tool errors & domain invariants

Design tools to return **exact, actionable failures** rather than vague `"error"` strings, and enforce financial or temporal invariants in C# rather than trusting the model's arithmetic:

```csharp
using System.ComponentModel;
using PiSharp.CustomAgents.Tools;

public sealed record CaptureReceiptArgs(
    [property: Description("Merchant name")] string Merchant,
    [property: Description("ISO date yyyy-MM-dd")] string Date,
    [property: Description("Total in integer cents (e.g. 1782 for 17.82)")] long TotalCents,
    [property: Description("Line items including tax in integer cents")] LineCharge[] Charges);

public sealed record LineCharge(string Label, long AmountCents);

var captureReceipt = AgentTool.Create<CaptureReceiptArgs>(
    "capture_receipt",
    "Validates and stores a receipt in integer cents.",
    (args, _) =>
    {
        if (args.TotalCents <= 0 || args.Charges.Length == 0)
            return Task.FromResult(AgentToolResult.Error("totalCents must be positive and at least one charge is required."));

        var sum = args.Charges.Sum(c => c.AmountCents);
        if (Math.Abs(sum - args.TotalCents) > 1)
            return Task.FromResult(AgentToolResult.Error(
                $"Charges sum to {sum} cents but totalCents is {args.TotalCents}. Re-read the line items and tax."));

        return Task.FromResult(AgentToolResult.Ok($"Validated {args.Merchant}: {args.TotalCents} cents across {args.Charges.Length} lines."));
    });
```

This exact pattern is used in [16-ReceiptToExpense](/docs/samples/multimodal-receipt-and-studio/): representing money as integer cents eliminates floating-point rounding drift and catches OCR/vision arithmetic mistakes immediately.

## Recipe 2: Typed structured output with automatic repair (`Prompt<T>`)

When a workflow step needs a typed object from a model, use `Prompt<TResult>` (or call `DeclareResult<TResult>()` + `context.PromptAsync<TResult>` inside a `.Then(...)` step):

```csharp
using System.ComponentModel;
using PiSharp.CustomAgents.Workflows;

public sealed record RiskVerdict(
    [property: Description("Risk level: low, medium or high")] string Level,
    [property: Description("One short sentence explaining why")] string Reason);

var workflow = Workflow.For<OrderData>("risk-check")
    .Prompt<RiskVerdict>("risk",
        order => $"Assess fraud risk for order {order.OrderId} ({order.Total} EUR).",
        (order, verdict) => order.Risk = verdict,
        new PromptOptions { MaxRepairs = 2, Thinking = "low" })
    .Build();
```

If the model answers in plain text instead of calling `submit_result`, the engine first tries to parse fenced or bare JSON from the reply; if that still fails schema validation, it sends a targeted repair message with the exact validation error and deterministic request ID `wf:<task>:<step>:<key>:<attempt>:r<n>` until `MaxRepairs` is exhausted (at which point `ModelCallException` is thrown).

## Recipe 3: Preserving contradictions & blocking stale state

Never silently overwrite conflicting records or allow downstream tools to consume stale state after inputs change. In [45-IncidentTimelineBuilder](/docs/samples/incident-timeline-builder/):
1. Calling `collect_evidence` again for an incident explicitly clears `c.State.Remove("timeline:" + id)` so `publish_timeline` throws `"Build this incident timeline first."` until the timeline is rebuilt.
2. When two sources report the same `eventId` with conflicting facts, `build_timeline` retains **both** variants and emits an explicit `conflicts` array rather than picking one arbitrarily.

## Pitfalls & limits

- **Check `ToolFailureKind.Truncated`:** if an assistant reply hits the model's `max_tokens` limit mid-tool-call, `PiSharp.Agent` refuses to run the truncated tool calls and returns `Truncated` instead of feeding corrupted JSON into your executor.
- **Avoid recursive result schemas:** `Prompt<TResult>` rejects recursive C# types at registration time; flatten tree outputs into ID-referenced arrays.
- **Keep exact integer bounds in mind:** `PiSharp.CustomAgents` workflow `TData` stores JSON numbers as `double`, rejecting integers beyond $2^{53}$ (`9,007,199,254,740,991`) with a `WorkflowDataException` pointing to the exact JSON path — store large 64-bit IDs or exact `decimal` amounts as strings or bounded integer cents.

## Try it now

```powershell
# Integer-cent receipt validation + self-test (includes fractional & unbalanced rejection cases)
dotnet run --project samples/agents/16-ReceiptToExpense -- --self-test

# Multi-source contradiction preservation & stale-state invalidation
dotnet run --project samples/agents/45-IncidentTimelineBuilder -- --self-test

# Typed Prompt<RiskVerdict> via submit_result
dotnet run --project samples/CustomAgents.Examples -c Release -- orders
```
