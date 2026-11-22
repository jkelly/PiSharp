---
title: Managing long context & tool bloat
description: Prevent context exhaustion with automatic compaction, session branching, deferred tool_search discovery and codemode batching.
---

**What happens in production:** Long coding or research sessions accumulate massive file dumps, build logs, and dozens of MCP tool definitions until context overflows, latency spikes, or prompt caching degrades. PiSharp attacks context bloat from four angles: **output ceilings**, **deferred tools**, **codemode batching**, and **compaction + branching**.

## Four levers to keep context lean

| Source of bloat | PiSharp defense | How it works |
| --- | --- | --- |
| **Giant shell / file outputs** | Built-in 2,000-line / 50 KB truncation | `read` slices with `offset`/`limit`; `bash` retains the tail and writes full logs to `pi-bash-<id>.log`; `grep`/`find`/`ls` cap result counts. |
| **50+ registered MCP or domain tools** | `Deferred = true` + BM25 `tool_search` | Hidden tools stay out of the prompt until the model queries `tool_search(query, limit)` or a tool returns `ToolControl.AddTools`. |
| **Chatty multi-step tool loops** | `codemode` (Jint JavaScript sandbox) | The model runs one JS script that loops over multiple tool calls and returns only a concise filtered summary. |
| **Long multi-turn histories** | Auto-compaction & `/tree` branching | Summarizes older turns automatically (`reserveTokens` / `keepRecentTokens`), or lets you branch off an earlier clean leaf without dragging dead-end attempts along. |

## Recipe 1: Defer rarely used tools (`Deferred = true` & `tool_search`)

In `PiSharp.CustomAgents`, mark specialist or rarely used tools deferred so they cost **zero prompt tokens** until needed, and load them dynamically via `ToolControl` or `tool_search`:

```csharp
using PiSharp.CustomAgents.Tools;
using PiSharp.Durable;

var refundTool = AgentTool.Create<OrderArgs>(
    "issue_refund",
    "Issues a refund for an order.",
    RefundAsync,
    new AgentToolOptions { Deferred = true, Sequential = true });

// A discovery tool (or built-in tool_search) activates deferred tools for the agent's next step:
var unlockBilling = AgentTool.Create("unlock_billing_tools", "Enables refund tools when handling a return.", _ =>
    Task.FromResult(new AgentToolResult
    {
        Text = "Loaded issue_refund.",
        Control = new ToolControl { AddTools = ["issue_refund"] },
    }));
```

For MCP servers in `mcp.json`, set `"exposure": "deferred"` (activates `tool_search` automatically) or `"exposure": "codemode"`.

:::note[1.6.0 source candidate: persistent `tool_search` & extension namespaces]
On `sync/pi-1.6.0` (unreleased):
- MCP tools activated via `tool_search` now survive session resume and `/reload`.
- In Durable code mode, tool groups declare their namespace directly on the extension (`Extension.Namespace = new ToolNamespace("mcp__github", "GitHub issues and pull requests")`) instead of `CodemodeToolOptions.Namespaces`.
:::

## Recipe 2: Compacting long histories

- **In `pisharp` / `pisharp durable`:** automatic compaction triggers when remaining headroom drops below `compaction.reserveTokens` (default `16384`), keeping the most recent `compaction.keepRecentTokens` (default `20000`). Run `/compact <instructions>` anytime to steer what the summary preserves (for example, `/compact keep failing test names and file paths`).
- **In `PiSharp.Durable`:** configure compaction settings in `HarnessSettings` or call `conversation.CompactAsync(...)` explicitly (`24-compaction`).
- **In `PiSharp.Sessions`:** `SessionBranchPlanner.Fork` (demonstrated in [28-ResearchWorkbench](/samples/?q=28-ResearchWorkbench)) creates a fresh session branch from a specific earlier entry so dead-end explorations stay preserved in the tree without polluting the active prompt.

## Recipe 3: Keep tool state branch-consistent

If you write a stateful extension tool (like a task list or scratchpad) that persists across turns in `PiSharp.CodingAgent`, **never** store mutable state only in a static field — switching branches with `/tree` would leave stale items from an abandoned branch. Instead, rebuild state from `SessionSnapshot.BranchEntries` as [StatefulTodo](/docs/samples/durable-and-extensions/#statefultodo-branch-rebuilt-tool-state) does, or attach a `RewindableConversationDoc` with `DocumentFork.AsOf` in `PiSharp.Durable` (`02-forks`).

## Pitfalls & limits

- **Anthropic prompt cache invalidation:** modifying the system prompt or toggling active tools on every turn breaks prefix caching. Prefer loading deferred tools once per task or calling them through `codemode`.
- **Workflow `MaxDataBytes`:** `PiSharp.CustomAgents` caps workflow `TData` at `256 KB` by default (failing with error kind `data`). Keep large documents, transcripts, or binary payloads on disk or in your database and store only their IDs/paths in `TData`.

## Try it now

```powershell
# Native PiSharp v3 session branching (SessionBranchPlanner.Fork)
dotnet run --project samples/agents/28-ResearchWorkbench -- --demo

# Durable forks with rewindable documents + automatic compaction
dotnet run --project samples/Durable.Examples -- --storage sqlite 02-forks 24-compaction

# CLI read-only + codemode session
pisharp --tools read,grep,find,ls,codemode -p "Find every public ToolRegistration in src/ and count them by project."
```
