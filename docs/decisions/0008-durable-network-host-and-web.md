# Durable network: device host, device server and web UI

Status: approved by the project owner on 2026-10-17 (every recommended option of the plan below), on branch `feature/durable-network` only. Nothing here is released. The plan, the HTML mockups the owner approved, and the analysis of durable-pi live outside the repository, in `E:\AI\Pi Sharp Planning\PiSharp Durable Web\` (`PLAN.md`, `Mockups/`). Phase 2 (the hosting library) is described under [Hosting library](#hosting-library).

## Context

[disler/durable-pi](https://github.com/disler/durable-pi) (MIT, © 2026 IndyDevDan; reviewed at commit `37165c6`) is a TypeScript "Durable Agent Network" on upstream `@earendil-works/pi-durable` 1.0.3:
- every device runs a small server, `dpi-backend`, that hosts many durable sessions, each one Harness on one SQLite file, and serves them over an authenticated v1 HTTP + SSE API;
- a hub keeps the device registry and tokens, proxies the API and multiplexes the streams into one SSE connection per browser tab, for a Vue web client;
- a terminal client and plain `curl` use the same API.

PiSharp.Durable already mirrors pi-durable 1.2.0, newer than durable-pi's 1.0.3 (decision 0005), so durable-pi brings no harness changes. What PiSharp lacks is the layer above the harness: a multi-session host, a network API, a client and a web UI, plus durable-pi's application features (shared App State, durable jobs with human approval, a read-only switch, history search, a command guard). Upstream has no such layer either; this is PiSharp-only, like `pisharp durable` (decision 0005, section 8).

## Decision

1. **Wire compatibility.** The device API is compatible with durable-pi's v1 core (routes, frames, error envelope), so durable-pi's terminal client and hub can attach to a PiSharp device and PiSharp's hub to a durable-pi device. PiSharp additions are advertised under `capabilities`.
2. **Location.** The libraries and the web app live in this repository on `feature/durable-network`. The web app is never packed.
3. **Visual style.** The PiSharp Web Agent design system (neutral greys, Bootstrap 5.3, system fonts, colour only for status) with durable-pi's layout (network tree, inspector, agent view).
4. **Rendering.** The hub runs the C# stream reducers and renders Razor fragments over one SSE stream per browser tab. The same reducers serve a later `pisharp durable attach`.
5. **Device server.** `pisharp durable serve` in the existing tool, with service hosting.
6. **Relation to PiSharp Web Agent.** A separate app now, organised so that it can become a `Durable` MVC area of the Web Agent later.
7. **Approvals.** Human waits (job approvals, questions, plan reviews) use `UserInteraction` (decision 0005, PiSharp-only additions), not polling. This branch was cut from `feature/durable-interaction` (draft PR #24) for that reason, and because Phase 2 reads the same CLI host files that branch changes. It must not merge before #24 does.
8. **Devices in the UI.** URL devices can be added and edited in the web UI; devices imported from durable-pi's `dpi-network.yaml` are read-only there.

Phase 9 (optional work: `attach`, network operations, the SQL Server store, a proactive sample, interop, a release) is decided item by item when the owner asks.

## Phases

The plan's phases, each gated on the owner:

| Phase | Content | State |
|---|---|---|
| 0 | Branch, this record, notices | done |
| 1 | HTML mockups | approved 2026-10-17 |
| 2 | `PiSharp.Durable.Hosting` | built 2026-10-17, awaiting review (30 cases pass) |
| 3 | Durable application tools (App State, jobs, `search_history`, subagent, `start_session`, `handoff`, system prompts, command guard) | not started |
| 4 | `PiSharp.Durable.Server` and `pisharp durable serve` | not started |
| 5 | `PiSharp.Durable.Client` (wire DTOs, client, reducers) | not started |
| 6 | `PiSharp.Durable.Web` hub | not started |
| 7 | The web UI | not started |
| 8 | Verification (Playwright, real-model and restart suites) | not started |

## Hosting library

`src/PiSharp.Durable.Hosting` hosts many durable sessions in one process: what durable-pi's `store.ts` does inside `dpi-backend`, without any HTTP. It references only `PiSharp.Durable`, `PiSharp.Durable.Sqlite` and `PiSharp.Durable.Tools`: no ASP.NET Core, and not `PiSharp.Cli`.

**Seams instead of the CLI.** `pisharp durable`'s host pieces (`DurableHarnessSetup`, `DurableModels`, `DurableSessions`) depend on PiSharp.Cli-only types: `PiSettings`, `PiLockfile`, `ModelRegistry`, `LiveSessionRuntime`. A library cannot reference the CLI, so the host takes those concerns as options:
- the models (`IDurableModels`: `NativeDurableModels`, the CLI's `DurableModels`, or `FauxDurableModels`);
- the base `HarnessSettings` (the CLI's `PiHarnessSettings`);
- the registry factory.

The CLI starts using the library in Phase 4, when `pisharp durable serve` passes its own pieces in. Until then `pisharp durable` is unchanged. The plan's Phase 2 step "move the shared parts of `DurableRuntime` and `DurableHarnessSetup` out of the CLI" moves to Phase 4 for this reason.

**Layout** under the data directory (default chosen by the server, not the library):
- `catalog.sqlite`: the catalog;
- `catalog.lock`: held for the host's lifetime;
- `sessions/<uuid>/agent.sqlite`: one Harness per session, with `sessions/<uuid>/session.lock`;
- `archive/<uuid>-<yyyyMMddHHmmss>/`: archived sessions.

**Locks.** Each lock is a file held open with `FileShare.None` for as long as the host or session is open. The operating system releases it when the process dies, so there is no stale-lock detection (durable-pi writes a PID with O_EXCL and probes it with `kill(pid, 0)`). On Unix, .NET implements `FileShare.None` with an advisory `flock`. A second host on the same data directory fails with `DeviceLockedException`.

**Catalog** (SQLite, `PRAGMA user_version` 1):

| Table | Columns | Purpose |
|---|---|---|
| `meta` | `key`, `value` | `device_uid`: a UUID made on first open |
| `sessions` | `id`, `name`, `cwd`, `created_at`, `provider`, `model`, `compaction` (JSON or null) | One row per live session |
| `agent_names` | `session_id`, `conversation_id`, `name` | Custom agent names |
| `session_spawns` | `key`, `session_id` | Exactly-once session creation by a key (Phase 3's `start_session`) |

Archiving a session deletes its rows from all three session tables in one transaction (durable-pi leaves its compaction and spawn rows behind). The spawn record is written in the same transaction as the session row.

**Opening.**
1. Lock and open the catalog.
2. Open every session. A session that fails to open is listed with its error and answers `SessionUnavailableException` (the server's `503 unavailable`); the others are not affected. A session whose Harness fails later (`ISession.Closed` settles `Failed`) becomes unavailable the same way.
3. Re-route stored models: every conversation whose stored model the device's `IDurableModels` does not know is configured to the device's default model, as durable-pi does on reopen.
4. Call `Resume()` on each Harness.

**Addressing**, as durable-pi:
- A session is addressed by its UUID or a case-insensitive name.
- An agent is addressed by its conversation number, `main` (conversation 1), a custom name or a derived name.
- A name shared by several gives `AmbiguousNameException` (the server's `409 ambiguous`); an unknown one gives `NotFoundException` (`404`).

**Names and kinds.**
- Derived names: `Main` for conversation 1, then `Fork #n`, `Subagent #n`, and `Agent #n` for another root (durable-pi says `Root #n`; the approved mockups say `Agent #n`). `n` is the conversation number, which comes from a sequence other records share, so numbers are not consecutive.
- Kinds: a conversation with a parent is a fork; one owned by a task is a subagent; anything else is a root.
- Custom names are trimmed, at most 100 characters, cannot be all digits, and only conversation 1 may be called `main`.

**Busy** (as durable-pi): an agent is busy while the task graph has a live non-background task of its conversation. The `background` count is its live background tasks.

**Sending.**
- Every send needs a request ID, which pi-durable deduplicates per conversation.
- `SendAsync` can wait up to a limit and return the answer's text. The result is one of:
  - `queued`: admitted, not waited for;
  - `pending`: waited, not settled;
  - `done`: with the answer entry and its text;
  - `unanswered`: with the reason.
- Unlike durable-pi, a repeated request ID whose submission already settled answers `done` or `unanswered`, not `queued`.

**Compaction per session.**
- The session's settings are stored in the catalog and read at every use through a `HarnessSettings` that wraps the host's base settings.
- They change from the next request.
- Defaults follow durable-pi's `defaultCompaction(window)`:
  - waits at `min(250000, window − 16384)`;
  - compacts in the background at `min(200000, wait − 32768)`;
  - `keepRecentTokens` is pi-durable's 20000, not durable-pi's 2000.
- Changing Main's model to one whose window no longer fits the stored thresholds (`window − reserveTokens < 32768`) resets them to the new window's defaults.
- Limits: `reserveTokens` 1024 to 1000000, `backgroundTokens` 0 to 1000000, `keepRecentTokens` 500 to 200000.

**Read-only.**
- A `ToolEffects` table classifies tools as read, write or exec, by name.
  - The defaults mark `read`, `read_state`, `search_history`, `list_durable_jobs`, `check_durable_job`, `tool_search`, `ask_user_question` and `handoff` as read.
  - Every other tool, including an unknown one, counts as a writer.
- Turning read-only on removes the agent's writers with `AgentChange.Tools = ToolSelection.Without(...)`; turning it off clears the tool filter.
- An agent reports read-only when it is offered no writer.

**Streams.** The host turns the callback-based watches into `IAsyncEnumerable`s for the server:
- agent event batches over `WatchEventsAsync`;
- task graph values;
- device changes (session commits and catalog changes, coalesced).

Each runs through a bounded channel whose writer waits, so a slow reader pushes back on the harness watch, which then replaces undelivered batches with one snapshot (pi-durable's own overflow rule). Nothing is replayed.

**Faux model.** `FauxDurableModels` answers without a provider for zero-cost tests and UI work. Its prompt grammar adapts durable-pi's `faux.ts`:
- `!command` calls `bash`;
- `slow: text` streams text in small, delayed pieces;
- after a tool result it answers `Tool finished: <tool>: <output>`;
- anything else is answered `Echo: <text>`.

**Packaging.** `IsPackable` is false. Every public type is `[Experimental("PISHARP_DURABLE001")]`. Packing it is part of a release decision.

**Tests.** `tests/PiSharp.Durable.Hosting.Tests` uses the repository's `*Cases()` runner:
- catalog persistence;
- addressing and names;
- failure isolation;
- the data-directory lock;
- restart recovery of a run interrupted mid-tool;
- read-only blocking;
- compaction settings and refits;
- sends with and without waits;
- archive cleanup;
- the stream adapters.

They run on the faux model only, and `.github/workflows/durable.yml` runs them on Windows, Linux and macOS (the workflow now also triggers on `feature/durable-network`).

**Not in Phase 2.**
- Moving `pisharp durable`'s host onto this library: Phase 4 (see Seams instead of the CLI).
- Idempotency keys for session, agent and fork creation, and the transcript and entry paging routes: Phase 4, with the server that needs them.
- The read-only state is the agent's tool filter, so turning read-only off also clears any other tool filter the agent had, such as plan mode's. Plan mode saves and restores its own filter.

## Consequences

- `feature/durable-network` must merge after `feature/durable-interaction` (PR #24), or be rebased onto `main` once #24 lands.
- Adapting durable-pi's designs is recorded in `THIRD-PARTY-NOTICES.md` under its MIT notice. Its model icons and brand mark are not copied.
- `docs/architecture.md` lists browser and web interfaces as deferred; this branch is the expansion that ends that deferral for durable sessions.
