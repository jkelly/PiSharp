---
title: Workflow pattern matrix
description: Quick lookup for common scenarios in production agent workflows and how PiSharp handles each one.
---

Real agent workflows fail and branch in predictable ways: models pass bad arguments, risky writes need human sign-off, users interrupt mid-turn, workers crash after charging a card, tool catalogs balloon, and subagents need parallel orchestration. Each guide in this cookbook shows **what happens**, **how PiSharp solves it across runtimes**, **a working C# recipe**, and **a sample you can run offline right now**.

## Quick-lookup matrix

| What happens in your workflow | PiSharp mechanism | Runtime | Read the pattern | Try a sample |
| --- | --- | --- | --- | --- |
| A tool needs human approval before mutating files, PRs or accounts | `IToolHooks.BeforeExecutionAsync`, `IToolActionPolicy`, `WaitForSignal<T>`, `CanAskHost`, Durable `BeforeTool` gate *(1.6.0)* | All | [Approvals & human-in-the-loop](/docs/patterns/approvals-and-hitl/) | [05-PersistentTaskManager](/docs/samples/persistent-task-manager/) · [Approval samples](/samples/?pattern=Approval+gate) |
| The model calls a tool with invalid JSON, unknown IDs, or unbalanced totals | `JsonSchemaValidator`, `ToolResult.Error`, `AgentToolResult.Error`, `Prompt<T>` `submit_result` repair loop | All | [Tool errors, validation & typed repairs](/docs/patterns/tool-errors-and-repairs/) | [16-ReceiptToExpense](/docs/samples/multimodal-receipt-and-studio/) · [45-IncidentTimelineBuilder](/docs/samples/incident-timeline-builder/) |
| A user redirects a busy agent mid-turn, queues a follow-up, or cancels | `agent.Steer()`, `agent.FollowUp()`, `Abort()`, Durable inbox, `AgentSendOptions.Urgent` | All | [Steering, follow-ups & cancellation](/docs/patterns/steering-and-interrupts/) | [Durable 19-inbox & 20-late-join](/samples/?pattern=Steering+%26+signals) |
| The host crashes mid-turn or mid-workflow after an external side effect | `OnceAsync`, `IdempotencyKey`, `IsReplay`, `ToolReplay.Safe`/`Unsafe`, `RunKey`, `WorkflowMigration` | Durable / CustomAgents | [Crash recovery, retries & replay safety](/docs/patterns/crash-recovery-and-idempotency/) | [Hybrid Orders Workflow](/docs/samples/hybrid-orders-workflow/) · [Crash samples](/samples/?pattern=Crash+%26+resume) |
| Long sessions hit context limits or 50+ tools bloat every prompt | Auto-compaction, `/tree` session branching, `Deferred = true` + BM25 `tool_search`, `codemode` | All | [Managing long context & tool bloat](/docs/patterns/context-and-compaction/) | [Durable 24-compaction & StatefulTodo](/docs/samples/durable-and-extensions/) |
| Multiple independent tasks need parallel execution or specialist subagents | `ToolExecutionMode.Parallel`, `Workflow.Parallel`/`ForEach`, `delegate` & `spawn`, cycle/depth guards | All | [Parallelism, fan-out & subagent teams](/docs/patterns/fan-out-and-orchestration/) | [Parallel Research Team](/docs/samples/parallel-research-team/) · [Team samples](/samples/?pattern=Subagents+%26+teams) |
| A UI needs live token streaming, partial tool previews, or folded status | `IAgentEventSink`, `StreamingJsonParser`, `ToolExecutionUpdated`, `WatchAsync`, `ViewBridge`, `PiSharp.Telemetry` | All | [Streaming UIs, watches & telemetry](/docs/patterns/streaming-and-observability/) | [Durable 04-watches & Chord 36](/docs/samples/durable-and-extensions/) |
| You need deterministic offline CI tests and reproducible live evaluations | `FauxAgentModels`, `ScriptedModels`, fake `TimeProvider`, `--self-test`, `run-live.ps1 -CompareToolResults`, `PiSharp.Evals` | All | [Offline testing & live validation](/docs/patterns/testing-and-evals/) | [All 117 offline samples](/samples/) |

## How to read these patterns

- **Start with the simplest layer that meets your failure tolerance.** If losing an in-flight turn on process restart is acceptable, `PiSharp.Agent` hooks and policies take twenty lines of code. If approvals or external side effects must survive crashes or wait hours/days for a human, use `PiSharp.Durable` or `PiSharp.CustomAgents`.
- **Every 1.6.0 addition is called out explicitly.** Patterns that use unreleased capabilities on `sync/pi-1.6.0` (such as Durable `BeforeTool` gates, parked webhook/polling tools, `OnYield` review tasks, and examples `38–47`) include a clear **1.6.0 source candidate** callout alongside the published 1.5.2 approach.
