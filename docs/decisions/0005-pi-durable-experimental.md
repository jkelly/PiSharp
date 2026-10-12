# Experimental port of pi-durable

Status: approved by the project owner on 2026-10-12, on branch `feature/pi-durable` only. It supersedes the `pi-durable` and experimental `durable` exclusions in [0002-pi-1.1.0-sync.md](0002-pi-1.1.0-sync.md) for that branch.

## Context

Pi v1.1.0 includes `@earendil-works/pi-durable`, an experimental durable agent harness. It commits every model turn, streamed partial, tool call and queued message to storage before showing it. A process that dies mid-turn resumes on reopen. Tools are not run twice unless they are declared replay-safe. Subagents run as child conversations owned by the calling task.

Pi also ships an experimental agent on top of it in `packages/coding-agent/src/experimental/durable`. That agent runs from source and is not wired into the `pi` command.

Decision 0002 left both out of the native port. The owner now wants them in PiSharp.

## Decision

1. **Scope.** Port `pi-durable` as a library, faithful to upstream `packages/durable/docs/spec.md`. Port the experimental durable agent as `pisharp durable`. The `vacation` demo, `pi-env`, Chord facets/services/wire, and the Cloudflare SQLite adapter stay out of scope.
2. **Baseline.** Pin the port to Pi v1.1.0, commit `abe508e1b89912adde45528136c3221eb69acdd7`. Upstream durable is experimental and changed its API eight times in its first week, so it is re-synced only with PiSharp's baseline syncs. `compatibility/durable-port-map.json` maps every upstream file to its C# counterpart and status.
3. **Branch and release.**
   - The work lives on `feature/pi-durable` and is not merged to `main` or released until the owner decides.
   - Every public type carries `[Experimental("PISHARP_DURABLE001")]`, and the projects are not packable.
   - If a release is later approved, it uses prerelease versions (`-experimental.N`) and says "EXPERIMENTAL" in the package descriptions.
4. **Packages.**
   - `PiSharp.Durable` holds the core: the Chord subset, records, session, harness, memory and JSONL storage, and the conformance kits. It references only Contracts and AI.
   - `PiSharp.Durable.Sqlite` holds the SQLite storage.
   - `PiSharp.Durable.Tools` holds the execution environment and coding tools.
   - The CLI mode lives in `PiSharp.Cli/Durable`.
5. **Storage.** Memory, JSONL and SQLite, as upstream. SQLite uses `Microsoft.Data.Sqlite` with its `SQLitePCLRaw` e_sqlite3 native assets. That makes it PiSharp's fourth external runtime dependency, scoped to `PiSharp.Durable.Sqlite` only. It is pinned exactly and needs license notices, packaging validation entries and locked restore entries before any release.
6. **Compatibility.**
   - **What matches upstream:** the SQLite schema (v1 DDL), record JSON, entry, document and task kinds, and the session directory and lock layout under `~/.pi/agent/experimental/durable-sessions`. Either tool can then continue the other's sessions.
   - **What may differ:** the patch operations PiSharp writes, provided both sides replay each other's operations to the same values. Upstream treats operation shape as non-canonical.
7. **Intentional difference.** `pisharp durable` is a hidden subcommand. Upstream has no CLI entry point for the durable agent.

## Consequences

- The plan has 14 phases (P0–P13) with milestones: M0 records and storage, M1 a durable chat turn, M2 tool turns with kill-and-resume, M3 an interactive `pisharp durable`. Each milestone is reported to the owner before the next phase starts.
- Tests are ported from upstream `packages/durable/test` into `tests/PiSharp.Durable.Tests`. The storage conformance suite runs against every backend, directly and after reopen. Branch CI runs on Windows, Linux and macOS arm64.
- The branch merges `main` regularly and regenerates integrity pins and lock files after each merge. Main-line projects change only through additive seams, which are flagged for separate upstreaming to `main`.
