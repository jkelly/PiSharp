---
title: Sessions, branches & durability
description: How PiSharp's append-only JSONL session trees differ from commit-first Durable storage and Chord's shared StoreStorage.
---

"Persistence" means two very different things in agent systems: saving a **conversation transcript** so you can reopen or branch it later, versus saving **execution state before every side effect** so a crashed worker finishes an in-flight turn without duplicating work. PiSharp provides both as separate, cleanly bounded libraries.

## Append-only JSONL trees (`PiSharp.Sessions`)

`PiSharp.Sessions` ports Pi's v3 JSONL session format verbatim so Pi and PiSharp can open, resume, and branch each other's files under `~/.pi/agent/sessions`.

- **Every entry points to a `parentId`**, forming a tree inside a single append-only file.
- **Branching (`/tree` or `SessionBranchPlanner.Fork`)** moves the active leaf pointer to an earlier entry without deleting abandoned attempts.
- **Compaction** appends a `compaction` summary entry referencing earlier history while leaving the raw entries intact for inspection or branching.
- **Branch-aware tool state:** tools like [StatefulTodo](/docs/samples/durable-and-extensions/#statefultodo-branch-rebuilt-tool-state) reconstruct their state exclusively from `SessionSnapshot.BranchEntries` so switching branches never leaks state across timelines.

**What `PiSharp.Sessions` does not do:** if the process dies while a tool call or provider stream is in progress, the interrupted turn is not automatically resumed on restart. For that guarantee, use `PiSharp.Durable`.

## Commit-first execution (`PiSharp.Durable`)

`PiSharp.Durable` replaces the in-memory loop with a transactional store (`ISession`) and task scheduler (`IHarness`):

1. **Commit before display:** every submission, assistant entry, tool task creation, tool result, compaction, and document patch commits to `IStorage` before observers see it.
2. **Code stays in the `Registry`, data stays in `IStorage`:** conversations store the *names* of extensions and tools, resolved against your live `Registry` on every run so you can upgrade code and reload extensions cleanly.
3. **Tasks are checkpointed state machines:** `pi.generation` (model turn), `pi.tool` (tool call), `pi.compaction`, and your own custom tasks save their checkpoint after each phase.
4. **Reopening finishes pending work:** `Harness.OpenAsync` resets any `running` task left by a dead process back to `pending`; calling `Resume()` (or `SubmitAsync` / `WaitAsync`) finishes them.
5. **Crash-loop protection:** a task cut off by a process crash five times in a row halts instead of crash-looping forever.
6. **Rewindable documents:** extensions attach typed JSON documents to conversations or sessions (`Documents.DefineRewindableConversationDoc`). Forking a conversation with `DocumentFork.AsOf` snapshots the document's exact value at the fork entry.

## Choosing a storage backend

| Backend | Package | Survives restart? | Use it for |
| --- | --- | --- | --- |
| `MemoryStorage` | `PiSharp.Durable` | No | Fast unit tests and ephemeral in-memory demos. |
| `JsonlStorage` | `PiSharp.Durable` | Yes (folder per session) | Human-inspectable append-only durable logs on disk (1.5.2 adds read-only recovery). |
| `SqliteStorage` | `PiSharp.Durable.Sqlite` | Yes (single `.sqlite` WAL file) | Recommended default for local durable agents, `AgentTeam`, and single-node services. |
| `StoreStorage` | `PiSharp.Durable.Store` | Yes (shared Chord store) | Apps already hosting `PiSharp.Chord` that want Chord tables, plugins and Durable sessions sharing one owner, sequence and archive. |
| SQL Server (`PiSharp.Durable.MSSQL`) | Source-only (not on NuGet) | Yes | Multi-instance SQL Server experiments built from source. |

## What happens to an interrupted tool call?

When a process crashes after a tool started executing and before its result committed, `ToolRegistration.Replay` controls recovery:

| Replay setting | Behavior on reopen | Use when |
| --- | --- | --- |
| `ToolReplay.Unsafe` *(default)* | Does **not** rerun the tool. Commits an `"interrupted"` error result so the model inspects workspace state and decides what to do. | Shell commands (`bash`), file edits (`write`, `edit`), non-idempotent external APIs. |
| `ToolReplay.Safe` | Reruns the tool from the beginning (reusing any memoized `OnceAsync` checkpoints inside it). | Pure lookups (`web_search`), idempotent APIs using `IdempotencyKey`, and orchestration tools (`subagent`, `delegate`). |

## Durable on Chord (`PiSharp.Chord` + `PiSharp.Durable.Store`)

When your application hosts multiple reactive domains, HTTP routes, or hot-reloaded native plugins alongside agents, `PiSharp.Chord` + `PiSharp.Durable.Store` connect them cleanly:
- **`StoreStorage.OpenAsync(store, owner, ctx)`** mounts a Durable session inside a Chord `IStore` without taking ownership of the store's lifetime.
- **`RegistryReader.Create`** projects plugin-contributed extensions from the real Chord host into Durable's registry, tracking reloads and retirements.
- **`ViewBridge`** bridges typed immutable Durable conversation/task views into Chord JSON members with automatic observer cleanup (`Durable.Chord.Examples` 35–37).

:::note[New in 1.6.0: storage format & signals]
In PiSharp 1.6.0, Durable advances `pi.tool` to version 4 and `pi.generation` to version 3 to support **typed signals**, **sleeps without an `until` deadline**, **parked tools** (waiting on webhooks, polls, or human approval without holding a thread), and **hooks that start tasks**. Databases written by 1.6.0 cannot be reopened by PiSharp 1.5.2; keep a backup before upgrading an existing SQLite file. See [Durable 1.6.0 notes](/docs/embed/durable/#new-in-160).
:::

## Related guides

- [Crash recovery, retries & replay safety](/docs/patterns/crash-recovery-and-idempotency/)
- [Managing long context & tool bloat](/docs/patterns/context-and-compaction/)
- [Sessions library SDK](/docs/embed/sessions/) · [Durable library SDK](/docs/embed/durable/) · [Durable on Chord's store](/docs/embed/durable-store/)
- [28-ResearchWorkbench](/samples/?q=28-ResearchWorkbench) (native `SessionBranchPlanner.Fork`) & [Durable Examples 00–47](/docs/samples/durable-and-extensions/)
