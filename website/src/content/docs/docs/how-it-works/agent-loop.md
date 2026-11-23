---
title: Anatomy of an agent turn
description: "Step-by-step lifecycle of a single agent turn across PiSharp.Agent and PiSharp.Durable: request hooks, streaming, tool batching, queues and stop reasons."
---

Understanding what happens inside a single agent turn makes it straightforward to debug stalled loops, inject approvals, steer a running agent, or build live UIs. Both `PiSharp.Agent` (in-memory) and `PiSharp.Durable` (commit-first) follow the same six-stage rhythm, differing mainly in whether intermediate state is held in memory or committed to storage first.

## The six stages of a turn

| Stage | What happens | In `PiSharp.Agent` | In `PiSharp.Durable` |
| --- | --- | --- | --- |
| **1. Input & request build** | User/custom input joins the transcript; system sections, active tool declarations, thinking level and request-only transforms are assembled. | `AgentHooks.BeforePrompt` → `PrepareRequestBoundary` → `PrepareRequest` → `TransformRequestMessages` → `FinalTransformRequestMessages`. | `SubmitAsync` commits the input first; the generation task resolves sections and tool schemas from the live `Registry`. |
| **2. Stream & partial parse** | `PiSharp.AI` streams text deltas, reasoning/thinking blocks and partial tool-call JSON fragments. | `IAgentEventSink.EmitAsync` is awaited in strict order (`AssistantMessageStarted` → `TurnStreamObserved` → `AssistantMessageEnded`). | Streamed partial output persists on `ProgressSettings.PersistWithinMs` (~1 s default) and folds to `WatchAsync` observers. |
| **3. Validate & preflight** | Tool arguments are parsed and validated; policy and preflight hooks inspect each concrete call before execution. | `IToolHooks.BeforeExecutionAsync` can block; built-in tools run `IPreparedToolAdapter` → `IToolActionPolicy.AuthorizeAsync`. | Harness validates against JSON Schema (`pi-schema`). From **1.6.0**, `BeforeTool` hooks can park or block calls. |
| **4. Tool batch execution** | Tool calls from the assistant reply execute in parallel (default) or sequentially, streaming optional progress. | `ToolBatchScheduler` executes `Parallel` unless any tool in the batch sets `Sequential`; results always append in call order. | Each call runs as a durable child task (`pi.tool`) tracking `ToolReplay.Safe` vs. `Unsafe` and `OnceAsync` checkpoints. |
| **5. Commit & steering check** | Tool results join the transcript; any pending steering input is drained before the next model turn starts. | `ToolResultMessageEnded` → drains `agent.Steer(...)` (`OneAtATime` or `All`) → `AgentHooks.PrepareNextTurn`. | Tool results commit atomically; inbox steering messages attach to the commit before the next generation schedules. |
| **6. Continue or settle** | If tools ran or steering arrived, another turn starts. If the model replied with no tool calls, follow-ups run or the run ends. | `FinishTurn` / `FinishTurnDecision` → drains `agent.FollowUp(...)` → returns `AgentLoopResult`. | Resolves `SubmissionRecord` (`Done`, `Unanswered`). In **1.6.0**, `AfterTools` and `OnYield` hooks can launch review tasks. |

## Stage details in `PiSharp.Agent`

### Transcript & tool declarations
In `PiSharp.Agent`, tool **executors** (`ToolDefinition`) and tool **declarations** (JSON Schemas) are deliberately separate:
- Executors are supplied in `AgentConfiguration.Tools`.
- Declarations travel inside the `toolsAdded` array of a `system` transcript entry, matching Pi's wire model.
- A plain `IToolExecutor` receives raw `JsonData` arguments; validate them inside your executor (or use `PiSharp.Contracts.Schema.JsonSchemaValidator`, `PiSharp.Tools`, or `PiSharp.CustomAgents`'s `AgentTool.Create<TArgs>` which validates automatically).

### Parallel vs. sequential tool batches
When a model emits three tool calls in a single reply:
1. If all three tools allow `ToolExecutionMode.Parallel` (the default), `ToolBatchScheduler` starts them concurrently.
2. If **any** tool in that batch specifies `ToolExecutionMode.Sequential` (for example, a payment refund or stateful file writer), the entire batch runs sequentially in call order.
3. Either way, `toolResult` messages are appended to the transcript in the exact order the model emitted the calls.
4. If every tool result in a batch sets `Terminate = true`, the loop stops cleanly after recording those results.

### Steering vs. follow-up queues
You can queue input while `PromptAsync` is still running:

- **`agent.Steer(message)`** is checked **after each tool batch** and **before the next LLM call**. Use it to redirect an agent mid-task without aborting in-flight file writes.
- **`agent.FollowUp(message)`** is checked **only when the model finishes with no tool calls**. Use it to queue the next task once the current one settles.
- `SteeringMode` and `FollowUpMode` choose between `OneAtATime` (default) and `All`.

### Stop reasons and bounds
Unlike an unbounded `while (true)` loop, `AgentOptions` enforces explicit ceilings so runaway loops fail predictably:

| `AgentLoopStopReason` | When it happens |
| --- | --- |
| `Completed` | The model finished without tool calls (or a terminating tool batch ran) and the follow-up queue is empty. |
| `ChatFailure` | A provider request failed, or `Abort()` settled the run under default `CancellationBehavior.SettleAborted`. |
| `TurnLimit` | The run reached `AgentLoopOptions.MaximumTurns` (default `16`). |
| `TranscriptLimit` | The transcript reached `AgentLoopOptions.MaximumTranscriptMessages` (default `1,000,000`). |

## What changes in `PiSharp.Durable`

In `PiSharp.Durable`, there is no single in-memory `for` loop that disappears if the process exits:
1. **Submissions** (`root.SubmitAsync`) commit an inbox record before returning.
2. **Generation tasks** (`pi.generation`) run one model turn, commit the assistant entry, and spawn one **`pi.tool` child task** per tool call.
3. **Crash recovery** resets any task that was `running` back to `pending` when `Harness.OpenAsync` reopens the store. Tasks that crash the process five times in a row halt cleanly instead of looping forever.
4. **Hooks run at durable boundaries:** `BeforeRun`, `AfterTools`, `OnYield`, and `AfterRun` observe or modify execution across restarts.

:::note[New in 1.6.0: parked tools & hook tasks]
In PiSharp 1.6.0, a durable turn gains two major control points:
- **`ToolHooks.BeforeTool`** can **park** a risky tool call on a typed signal until a human approves or a deadline expires (`44-approval-gate`).
- **`GenerationHooks.AfterTools` and `OnYield`** can return `HookTask` items (replacing `RunEndTask`) to run verification or review tasks before an answer stands (`45-review-on-yield`, `46-after-tools-terminate`).
- In the CLI, interrupting a tool batch with `Esc` records an `Operation aborted` result for every call in the batch.
:::

## Related guides and patterns

- [Agent loop SDK reference](/docs/embed/agent/) — full `Agent`, `AgentHooks`, `IAgentEventSink`, and `AgentLoopRunner` API.
- [Steering, follow-ups & cancellation](/docs/patterns/steering-and-interrupts/) — runnable queue and abort recipes.
- [Streaming UIs, watches & telemetry](/docs/patterns/streaming-and-observability/) — wiring live sinks and folded durable views.
- [Tool errors, validation & typed repairs](/docs/patterns/tool-errors-and-repairs/) — keeping tool failures recoverable.
