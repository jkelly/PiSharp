---
title: "Walkthrough: Durable (00–47), Chord & C# extensions"
description: Indexed guide to all 48 Durable and Chord micro-examples (including 1.6.0 signals, parked tools and BeforeTool gates) plus StatefulTodo and SessionCheckpoint C# extensions.
---

This walkthrough ties together the lower-level building blocks of PiSharp: the **48 focused Durable & Chord micro-examples** (`00–47`), the **Durable Release Watcher** reference client, and the **two native C# extension samples** (`StatefulTodo` and `SessionCheckpoint`).

## Part 1: Durable examples `00–34` & `38–47` (`samples/Durable.Examples`)

Every example in `samples/Durable.Examples` runs offline on `MemoryStorage` (default), `JsonlStorage`, or `SqliteStorage`, and file-backed runs reopen storage in a plain `Session` afterward to verify persistence:

```powershell
# Run selected examples on SQLite
dotnet run --project samples/Durable.Examples -- --storage sqlite 00 02 04 12 19 26

# Run all 1.6.0 source-candidate examples (signals, parked tools, gates, hook tasks, fit-images)
dotnet run --project samples/Durable.Examples -- --storage sqlite 38 39 40 41 42 43 44 45 46 47
```

### Core store, forks, watches & recovery (`00–12`)

| Example | What it proves |
| --- | --- |
| `00-conversation`, `01-documents` | Creating conversations, appending typed entries, and attaching versioned conversation documents. |
| `02-forks`, `03-owned-conversations` | Forking conversations with rewindable `DocumentFork.AsOf` snapshots and parent-owned child lifecycles. |
| `04-watches` | Reactive `WatchAsync` folding multiple rapid commits into one update with `Ops`. |
| `05-harness` .. `10-extension-state` | Opening a harness, changing agent config, building context, hot-reloading the registry and storing extension state. |
| `11-tasks`, `12-recovery` | Custom checkpointed tasks and reopening storage after a simulated crash to finish pending work. |

### Agents, coding tools, inbox, subagents & compaction (`13–34`)

| Example | What it proves |
| --- | --- |
| `13-chat` .. `18-json` | Interactive chat turns, system prompt sections, live provider (`15-real-model`), durable coding tools, print & JSON modes. |
| `19-inbox`, `20-late-join` | Transactional steering/follow-up inbox and observers attaching mid-run without losing state. |
| `21-subagent-foreground`, `22-subagent-background`, `23-child-tasks` | Foreground/background durable subagent conversations and parent-owned child tasks. |
| `24-compaction`, `25-coding-agent`, `26-plan-mode`, `27-reviewer` | Auto-compaction, coding-agent profile, plan mode restrictions (`exit_plan_mode`) and automated review. |
| `28-sandbox-per-conversation` .. `34-submit-in-commit` | Per-conversation execution sandboxes, tool overrides, reload + restart, transcript reads, `AfterRun` tests-after-edits (`32`), turn-limit continuation (`33`) and atomic `submit-in-commit` (`34`). |

### Signals, parked tools, approval gates & hook tasks (`38–47`)

:::note[1.6.0 source candidate (unreleased)]
Examples `38–47` are on `sync/pi-1.6.0` (source only; not yet in the 1.5.2 NuGet packages). They demonstrate the `pi-durable` v1.6.0 primitives.
:::

| Example | What it proves |
| --- | --- |
| `38-signals`, `39-ask-a-person` | Delivering schema-validated JSON signals (`delivered`, `duplicate`, `invalid`, `closed`, `not_found`) and sleeping indefinitely until a human answers. |
| `40-tool-waits-for-webhook`, `41-tool-polls` | Durable tools that **park** without holding a thread while waiting for an external webhook (with deadline + cancel) or between poll intervals. |
| `42-parallel-children`, `43-subagent-waits` | Tools that spawn nested parallel calls or a subagent conversation and park until all children settle. |
| `44-approval-gate` | `ToolHooks.BeforeTool` parking risky `bash` calls keyed by a canonical argument digest with a memoized deadline until approved or denied. |
| `45-review-on-yield`, `46-after-tools-terminate` | `GenerationHooks.OnYield` and `AfterTools` launching durable `HookTask`s that review an answer or terminate a run. |
| `47-fit-images` | `FitImages.CreateExtension(...)` fitting tool-returned images to the active model's limits before committing to storage. |

## Part 2: Durable on the real Chord host (`35–37`) & Release Watcher

```powershell
# Run examples 35–37 on real SQLite + Chord host
dotnet run --project samples/Durable.Chord.Examples -- 35 36 37

# Run the offline Durable.ReleaseWatcher scenario (schedules, policies, human review, budgets, idempotent restart)
dotnet run --project samples/Durable.ReleaseWatcher -- demo
```

- **`35-durable-on-the-store`:** Chord tables and a Durable session share one `StoreStorage` owner, sequence, allocator, and archive across close and reopen.
- **`36-durable-views`:** `ViewBridge` projects typed immutable Durable conversation and task-graph views into reactive Chord host members.
- **`37-harness-registry`:** `RegistryReader` composes plugin-contributed extensions from the real Chord host into a Durable harness.

## Part 3: Native C# extensions (`samples/extensions/`)

Both extension samples implement `IPiSharpExtension` (`PiSharp.Extensions.Abstractions`) and run **in-process in .NET 10 with zero Node dependency**.

### `StatefulTodo`: branch-rebuilt tool state
[`samples/extensions/StatefulTodo/StatefulTodoExtension.cs`](https://github.com/jkelly/PiSharp/blob/main/samples/extensions/StatefulTodo/StatefulTodoExtension.cs) avoids storing mutable list state in a class field. On every `todo` call (`list`, `add`, `toggle`, `clear`), it replays `details` from `SessionSnapshot.BranchEntries`:

```csharp
var snapshot = (context as IExtensionSessionContext)?.SessionSnapshot
    ?? throw new InvalidOperationException("Todo requires an active acknowledged session snapshot.");
foreach (var raw in snapshot.BranchEntries)
{
    // Reconstruct the latest committed toolResult details along the active branch
}
```

When you jump to an earlier node with `/tree`, the todo list automatically matches that exact branch without custom undo logic.

### `SessionCheckpoint`: commands & guarded lifecycle hooks
[`samples/extensions/SessionCheckpoint/SessionCheckpointExtension.cs`](https://github.com/jkelly/PiSharp/blob/main/samples/extensions/SessionCheckpoint/SessionCheckpointExtension.cs) registers:
- `/checkpoint` and `/checkpoint-create` slash commands,
- a `session_start` observer that prompts on resume,
- `RegisterSessionSwitchHandler` and `RegisterSessionCreationHandler` confirmation hooks that can return `ExtensionSessionSwitchDecision.Cancel`, and
- explicit verification that retired session contexts reject stale writes after a switch.

```powershell
dotnet build samples/extensions/StatefulTodo
dotnet build samples/extensions/SessionCheckpoint
pisharp -e samples/extensions/StatefulTodo/bin/Debug/net10.0/StatefulTodo.dll
```

## Try breaking it

1. **Test SQLite vs. JSONL persistence:** run `dotnet run --project samples/Durable.Examples -- --storage jsonl --dir ./durable-jsonl 02-forks 12-recovery` and inspect the human-readable JSONL records written to `./durable-jsonl/`.
2. **Test `44-approval-gate` quiet-hours & expiry paths:** read `samples/Durable.Examples/ExamplesGates.cs` to see how a second gate re-evaluates on every wake while the approval deadline stays pinned via `api.MemoAsync`.
3. **Compare C# vs. TypeScript extensions:** visit [Extensions](/extensions/) to view `StatefulTodoExtension.cs` side-by-side with Pi's `todo.ts`.
