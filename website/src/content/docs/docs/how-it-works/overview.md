---
title: Architecture & choosing a layer
description: How PiSharp's two execution stacks, 29 NuGet packages and optional platform layers fit together, and which layer to pick for your app.
---

PiSharp is organized as composable .NET 10 libraries rather than a single monolithic agent framework. You can reference only the provider client (`PiSharp.AI`), drop the in-memory agent loop into an existing service (`PiSharp.Agent`), embed Pi-compatible JSONL session trees (`PiSharp.CodingAgent`), or run crash-safe durable workflows and subagent teams (`PiSharp.Durable` and `PiSharp.CustomAgents`).

This track explains how the pieces work together from top to bottom. For exact package dependency trees and every experimental diagnostic ID, see [Architecture](/docs/reference/architecture/) and [Use the SDK](/docs/embed/sdk/).

## Two execution stacks

PiSharp intentionally maintains **two distinct agent stacks** over the same provider layer (`PiSharp.AI` and `PiSharp.Contracts`):

| Dimension | Classic stack (`Agent` + `Sessions` + `CodingAgent`) | Durable stack (`Durable` + `CustomAgents`) |
| --- | --- | --- |
| **Execution model** | In-memory turn loop (`Agent`) optionally backed by append-only Pi JSONL session trees (`PiSharp.Sessions`). | Commit-first state machine (`IHarness`): every turn, streamed progress, tool call, document and timer commits before display. |
| **Crash mid-turn** | Loses the in-flight turn; committed prior entries remain in the JSONL tree. | Reopening the SQLite/JSONL/Chord store resumes unfinished turns and tasks automatically. |
| **Interoperability with Pi** | Shares `~/.pi/agent`, `.pi`, JSONL session format, RPC wire protocol and TypeScript extensions with Pi. | Ports `pi-durable` (`pisharp durable`); `PiSharp.CustomAgents` adds C# workflows and teams on top. |
| **API stability** | Stable (`1.6.0` on NuGet). | Experimental (`PISHARP_DURABLE001`, `PISHARP_AGENTS001`). |

## Pick the right layer for your project

| What you are building | Start with | Read next | Try a sample |
| --- | --- | --- | --- |
| **Interactive coding CLI or RPC backend** | `PiSharp.Cli` (`dotnet tool install -g PiSharp.Cli`) or `PiSharp.Rpc` | [Quickstart](/docs/quickstart/), [RPC host](/docs/embed/rpc/) | `pisharp`, `pisharp --mode rpc` |
| **Custom single-agent app with your own domain tools** | `PiSharp.Agent` (+ `PiSharp.AI`) | [Anatomy of an agent turn](/docs/how-it-works/agent-loop/), [Agent loop SDK](/docs/embed/agent/) | [05-PersistentTaskManager](/docs/samples/persistent-task-manager/) |
| **Embedded coding agent with branching history & skills** | `PiSharp.CodingAgent` + `PiSharp.Tools` (+ `PiSharp.Tools.Skia`) | [Sessions, branches & durability](/docs/how-it-works/sessions-and-durability/), [Coding agent SDK](/docs/embed/coding-agent/) | [28-ResearchWorkbench](/samples/?q=28-ResearchWorkbench) |
| **Crash-safe agent or long-running service** | `PiSharp.Durable` + `PiSharp.Durable.Sqlite` (+ `PiSharp.Durable.Tools`) | [Sessions, branches & durability](/docs/how-it-works/sessions-and-durability/), [Durable library](/docs/embed/durable/) | [Durable Examples 00–47](/docs/samples/durable-and-extensions/) |
| **Typed multi-step workflows, approvals or subagent teams** | `PiSharp.CustomAgents` (+ `.JavaScript` / `.Scripting`) | [Workflows & multi-agent teams](/docs/how-it-works/workflows-and-teams/), [Custom agents](/docs/custom-agents/overview/) | [Hybrid Orders Workflow](/docs/samples/hybrid-orders-workflow/) |
| **Shared reactive host, native plugins or remote execution** | `PiSharp.Chord`, `PiSharp.Chord.Hosting`, `PiSharp.Durable.Store`, `PiSharp.Env` | [Chord & hosting](/docs/embed/chord/), [Remote environments](/docs/embed/env/) | [Durable + Chord 35–37](/docs/samples/durable-and-extensions/) |
| **Reusable tool, command or hook package for PiSharp** | `PiSharp.Extensions.Abstractions` (or Pi TypeScript via `PiSharp.Compatibility.Node`) | [Tools, schemas & trust](/docs/how-it-works/tools-and-execution/), [Build an extension](/docs/extensions/build-an-extension/) | [StatefulTodo & SessionCheckpoint](/docs/samples/durable-and-extensions/) |

## Minimal mental model per layer

### 1. Streaming chat only (`PiSharp.AI`)
`PiSharp.AI` knows nothing about tool execution loops or files. You pass a `ModelDescriptor`, a provider transport (`NativeProviderFactory`), and transcript messages; it streams typed `StreamEvent` deltas (`TextDelta`, `ToolCallDelta`, `StreamDone`, `StreamError`).

### 2. In-memory agent loop (`PiSharp.Agent`)
`Agent` wraps `TurnRunner`, `ToolBatchScheduler`, and `AgentPendingInputQueue`. It sends the transcript, awaits your `IAgentEventSink`, runs tool calls in parallel or sequentially through `IToolHooks`, checks steering/follow-up queues, and loops until the model stops or hits a configured limit.

```csharp
var config = new AgentConfiguration(model, provider, [new ToolDefinition("get_time", new TimeTool())]);
await using var agent = new Agent(config, Clock, new ConsoleSink());
var result = await agent.PromptAsync([systemEntry, userEntry]);
```

### 3. Persistent sessions & built-in tools (`PiSharp.CodingAgent` + `PiSharp.Tools`)
`PiSharp.CodingAgent` couples the loop to `PiSharp.Sessions` (append-only JSONL trees with branching and compaction), settings, skills, and prompt templates. Built-in file/shell tools live in `PiSharp.Tools` and require an explicit `IToolActionPolicy` when embedded so your host controls every path and command.

### 4. Commit-first durable agents & workflows (`PiSharp.Durable` + `PiSharp.CustomAgents`)
`PiSharp.Durable` turns turns, tool calls, subagents, documents, and timers into persisted tasks over SQLite, JSONL, or Chord `StoreStorage`. `PiSharp.CustomAgents` layers typed `Workflow.For<TData>` state machines, `AgentTeam` orchestration, typed C# / JS / Roslyn tools, plugins, and `FauxAgentModels` offline testing on top of that harness.

```csharp
var summarize = Workflow.For<Note>("summarize")
    .Then("clean", note => note.Text = note.Text.Trim())
    .Prompt("summary", note => $"Summarize in one line: {note.Text}", (note, text) => note.Summary = text)
    .Build();
```

## Five design rules across every layer

1. **Streaming is explicit:** partial tool calls are surfaced for UI previews (`StreamingJsonParser`) and are never executed until parsed and validated.
2. **Cancellation flows everywhere:** every transport, tool, hook, and process execution accepts a `CancellationToken` and terminates with an explicit outcome.
3. **Errors go back to the model by default:** invalid tool arguments, policy denials, and handler exceptions normally become structured error tool results so the model can recover instead of crashing the host.
4. **Trust boundaries are explicit:** built-in tools enforce an action policy, JavaScript tools run in a Jint sandbox (and worker process for model-written code), and C# scripts / native plugins require explicit host trust.
5. **Everything can be tested offline:** both stacks support deterministic scripted models (`FauxAgentModels`, `ScriptedModels`, and the fixture host used across all 62 practical agent samples) so CI and local development need no provider credentials.

:::note[PiSharp 1.6.0 highlights]
The published NuGet inventory is **1.6.0** (29 packages). PiSharp 1.6.0 ships Durable signals, parked tools, `BeforeTool` approval gates, `OnYield`/`AfterTools` `HookTask`s, extension-declared code-mode namespaces, `FitImages`, and Durable examples `38–47`, alongside Chord events, named queries, lists and admission.
:::

## Continue reading

- [Anatomy of an agent turn](/docs/how-it-works/agent-loop/)
- [Tools, schemas & trust boundaries](/docs/how-it-works/tools-and-execution/)
- [Sessions, branches & durability](/docs/how-it-works/sessions-and-durability/)
- [Workflows & multi-agent teams](/docs/how-it-works/workflows-and-teams/)
- [Providers, media & remote execution](/docs/how-it-works/providers-media-remote/)
- [Explore all 117 samples](/samples/)
