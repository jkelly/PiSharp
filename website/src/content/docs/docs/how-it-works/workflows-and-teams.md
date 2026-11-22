---
title: Workflows & multi-agent teams
description: How PiSharp.CustomAgents combines typed crash-safe workflows, composite fan-out steps, subagent delegation and agent mailboxes on top of Durable.
---

While `PiSharp.Durable` gives you low-level conversations, documents, and tasks, most production agent systems want two higher-level abstractions: **deterministic workflows** that interleave code and model calls over strongly typed state, and **agent teams** where specialized agents delegate work and exchange messages. `PiSharp.CustomAgents` (experimental, `PISHARP_AGENTS001`) builds both directly on the durable harness.

## Typed workflows (`Workflow.For<TData>`)

A workflow is a named graph of actions operating over a single C# data class (`TData`). After every action finishes, the engine serializes `TData` (camelCase JSON by default, capped at `MaxDataBytes` = 256 KB) and commits the updated data together with the next step name in one atomic durable commit.

### Mixing deterministic code, models, scripts & agents

You do not have to force everything through a prompt or everything through C#. One workflow can combine all nine step kinds:

| Step method | What it runs | Durable guarantee |
| --- | --- | --- |
| `.Then(...)` | C# delegate or `IAction<TData>` class | Runs at least once; use `context.OnceAsync` + `IdempotencyKey` for side effects. |
| `.Prompt(...)` | Single LLM call returning text | Recorded by deterministic request ID `wf:<task>:<step>:<key>:<attempt>`; crash replay reuses the saved reply. |
| `.Prompt<TResult>(...)` | Typed LLM call via automatic `submit_result` tool | Schema-validated with automatic repair rounds up to `MaxRepairs` (default `2`). |
| `.Script(...)` | JavaScript step in Jint sandbox (`PiSharp.CustomAgents.JavaScript`) | Returns RFC 7386 `updates` patch, `next`, `stop`, `fail` or `output`; pure and replay-safe. |
| `.CSharp(...)` | Roslyn compiled C# script (`PiSharp.CustomAgents.Scripting`) | Mutates typed `Data` directly and can return any `ActionResult`. |
| `.Agent(...)` | Runs a registered team agent definition | Executes in a durable child conversation (`<definition>#<n>`) with its own isolated tools. |
| `.Branch(...)` / `.Switch(...)` | Pure routing function over `TData` | Not checkpointed; capped at 64 consecutive routers to prevent infinite loops. |
| `.Run(...)`, `.Parallel(...)`, `.ForEach(...)` | Child workflow, parallel branches, or bounded-concurrency batching (`Concurrency`, `JoinPolicy`) | Each child is its own durable task; `FailFast` aborts siblings on first error, `AllSettled` delivers all results to `merge`. |
| `.WaitForSignal<TSignal>(...)` | Durable pause awaiting `run.SignalAsync(...)` | Persists deadline across restarts; routes to `onTimeout` or fails with `timeout`. |

### Control flow, retries & versioning

- Every action returns an `ActionResult`: `Next()`, `GoTo(step)`, `Stop()`, `Fail(reason)`, `Repeat(after)` (for durable polling with `MaxVisits`), or `Sleep(delay, then)`.
- Per-step `ActionOptions` attach `RetryPolicy.Exponential(...)` / `Fixed(...)` (with selective `RetryOn` predicates) and per-attempt `Timeout`.
- When live runs already exist in SQLite and you rename steps or change `TData`, bump `.Version(2, WorkflowMigration.Compose(...))` using `RenameActions`, `TransformData`, or `Custom` so in-flight runs migrate cleanly instead of failing with `missing_action`.
- Expose any workflow directly to an agent with `WorkflowTools.RunTool(...)` and `WorkflowTools.StatusTool()` so an orchestrator agent can invoke deterministic pipelines like any other tool.

## Agent teams (`AgentTeam`)

An `AgentTeam` opens an `AgentCatalog` (agent definitions + tool sources) and `WorkflowCatalog` together over one `IStorage` (typically one SQLite file):

- **Strict isolation:** every `AgentDefinition` specifies its own `Model`, `Thinking`, `Instructions`, `Tools`, `ToolSources`, and `Extensions`. Subagents **never** inherit their parent's model, instructions, or tools.
- **Orchestration tools (automatic when `Subagents` is non-empty):**
  - `delegate(agent, task, name?)` — assigns a task and waits for the answer. Multiple `delegate` calls in one model turn execute **in parallel**.
  - `spawn(agent, task, name?)` — starts a subagent asynchronously and returns immediately; the answer arrives later as a mailbox message.
  - `wait_agent(agent, timeoutSeconds?)` and `stop_agent(agent)` — wait for or cancel work without erasing the subagent's history.
  - Naming a subagent (`name`) reuses that subagent's conversation across tasks; omitting `name` creates `<definition>#<n>`.
- **Delegation modes:** `DelegationMode.Interactive` (default) wakes the parent early if the subagent asks a question so the parent can `reply`; `DelegationMode.Strict` blocks until completion and disallows mid-task parent questions.
- **Safety caps:** `MaxDepth` (default `2`), `MaxChildrenPerAgent` (`8`), `MaxAgents` (`64`), and automatic cycle refusal when two agents would wait on each other.

## Mailboxes & host participation

Agents whose `Messaging` scope is `Family` (parent/children/siblings, default) or `Team` (all agents) receive four messaging tools: `list_agents`, `send_message` (with `urgent: true` to steer a busy agent), `ask_agent`, and `reply`.

- Every incoming message arrives wrapped in `<agent-message from="..." id="..." kind="...">` with an HTML-encoded body so prompt injection cannot spoof headers.
- Your application participates at address `host` through `session.Host` (`WatchMailboxAsync`, `SendAsync`, `AskAsync`, `ReplyAsync`), and agents marked `CanAskHost = true` can escalate decisions straight to your UI or CLI.
- Workflow actions participate at `workflow.<taskId>` through `ActionContext.Messages`.

## Related guides and walkthroughs

- [Walkthrough: Hybrid Orders Workflow & 4-Agent Team](/docs/samples/hybrid-orders-workflow/)
- [Walkthrough: Parallel Multi-Agent Research Team](/docs/samples/parallel-research-team/)
- [Parallelism, fan-out & subagent teams](/docs/patterns/fan-out-and-orchestration/)
- [Custom agents reference: Workflows](/docs/custom-agents/workflows/) · [Teams](/docs/custom-agents/teams/) · [Tools](/docs/custom-agents/tools/)
