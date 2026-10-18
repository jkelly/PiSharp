# Durable network: device host, device server and web UI

Status: approved by the project owner on 2026-10-17 (every recommended option of the plan below), on branch `feature/durable-network` only. Nothing here is released. The plan, the HTML mockups the owner approved, and the analysis of durable-pi live outside the repository, in `E:\AI\Pi Sharp Planning\PiSharp Durable Web\` (`PLAN.md`, `Mockups/`). Phases 2 to 8 are built on the branch and described below, from [Hosting library](#hosting-library) to [Verification](#verification); [Not done](#not-done) lists what is left.

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
| 2 | `PiSharp.Durable.Hosting` | built 2026-10-17 |
| 3 | Durable application tools (App State, jobs, `search_history`, subagent, `start_session`, `handoff`, system prompts, command guard, plan mode) | built 2026-10-17 |
| 4 | `PiSharp.Durable.Server`, `pisharp durable serve` and `pisharp durable token` | built 2026-10-17 (service hosting not done) |
| 5 | `PiSharp.Durable.Client` (wire DTOs, client, reducers) | built 2026-10-17 |
| 6 | `PiSharp.Durable.Web` hub | built 2026-10-17 |
| 7 | The web UI | built 2026-10-17 |
| 8 | Verification | automated hub suite and browser suite; the real-model suite is not done |

Phases 3 to 8 await the owner's review and are not committed.

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

## Application tools

Phase 3 lives in `src/PiSharp.Durable.Hosting/Apps`, not in the released `PiSharp.Durable.Tools`, because every piece needs device-level services (the catalog, other sessions, the job definitions). `DeviceApps.Create` builds the extensions each session installs.

- **App State.** One JSON object per device, in the catalog (`app_state`, catalog version 2), not in a second Harness. Changes are JSON Pointer operations (create, update, delete, increment) with an optional expected revision (`conflict` when stale) and an optional receipt key (`app_state_receipts`), so a rerun of a tool applies its change once. Limits: depth 24, 100000 characters, no `__proto__`, `constructor` or `prototype` segments. Every change notes a `pisharp.state-change` entry in the acting agent's transcript.
- **Tools.** `read_state`, `update_state`, `search_history` (the full history, marking compacted and pre-reset entries), `handoff`, `subagent` (a task-owned child without the delegating and asking tools), `start_session` (exactly once by a spawn key).
- **Jobs.** A job is phases (step, repeat, approval) whose checkpoint is `{phase, iteration, data, wakeAt?}`; a restart resumes the current phase and the run says so. An approval phase starts a task that watches a task document for the person's decision, so nothing polls and the wait survives restarts. Runs are kept per agent in `pisharp.job-runs` (at most 30). Tools: `list_durable_jobs`, `run_durable_job`, `check_durable_job`. The samples are Workspace snapshot, Release check (with approval) and Change watch. A task owned by a background task counts as background work, so a waiting run does not make its agent busy.
- **Optional extensions.** `system-prompts` (checked `.md` files of the device's `prompts` folder) and `command-guard` (offline rules, then an optional judge; each verdict a memo). `plan-mode` (PiSharp.Durable.Tools) is installed but selected only while an agent plans.

## Device server

`src/PiSharp.Durable.Server` maps durable-pi's v1 routes (PLAN.md Appendix A) on ASP.NET Core minimal APIs over the hosting library.

- **Auth.** `Authorization: Bearer` or `X-DPI-Token`, compared as SHA-256 hashes in constant time. Tokens are `dpi_` plus 32 random bytes in base64url; `pisharp durable token [--rotate]` prints or replaces it, stored readable by the user only.
- **Errors.** `{error: {code, message}}` with durable-pi's codes; bodies are JSON objects of at most 256 KB.
- **Idempotency.** `Idempotency-Key` makes session, agent and fork creation exactly once. Sessions use the catalog's spawn keys; agents and forks record the key in a session document family (`pisharp.creation-key`) inside the commit that creates the conversation, so a retry after a crash finds the first one. A key reused for another request is `invalid`.
- **Streams.** `id`/`event`/`data` frames, `hello` first, a `: ping` after 15 s idle, a 2 MB backlog cut-off, coalescing at 250 ms (device, session) and 150 ms (agent documents), and validation before the headers. Agent events are PiSharp.Durable's own JSON, so 1.2.0's nested tool fields pass through.
- **PiSharp additions** (in `capabilities`): `/state` and `/jobs` aliases of `/demo/*`, `GET .../jobs`, `GET/POST .../interactions` (question forms and plan reviews), `GET/PATCH .../plan-mode`, tool effects and deferral in the agent detail, Main's provider in session summaries. Agent documents are `pisharp.job-runs` and `pisharp.system-prompts`; durable-pi's `demo.job-runs` shape is not reproduced.
- **CLI.** `pisharp durable serve [--port --host --name --start-dirs --data-dir --provider faux --model --save]` uses the CLI's own models (auth.json, models.json, the environment) through a catalog adapter, or the faux model. The device directory defaults to `~/.pi/agent/experimental/durable-device` (`device.json`, `token`, the catalog and sessions). It binds `127.0.0.1` unless `--host` says otherwise, and warns when it does not. The two commands are not listed in `--help` yet: the released help text (pinned by `PiSharp.DurableCli.Tests`) changes with a release.

## Client

`src/PiSharp.Durable.Client` depends only on the base class library: the wire DTOs (System.Text.Json), `DurableDeviceClient` (HTTP, SSE, and `FollowAsync`, which reconnects with 1 to 15 s backoff, treats 45 s of silence as dead and stops on errors a retry cannot fix), the transcript builder (with nested tool calls, images and Markdown export) and the reducers `AgentModel`, `SessionModel` and `NetworkTree`. The server's `/transcript` uses the same builder, and a test checks that reduced stream frames equal it.

## Web hub and UI

`src/PiSharp.Durable.Web` (not packed) binds `127.0.0.1:5181`.

- **Registry.** SQLite under `%LOCALAPPDATA%\PiSharpDurableWeb`, tokens encrypted with Data Protection (DPAPI on Windows). This computer's `pisharp durable serve` appears as `local` (read from its device directory); a configured `dpi-network.yaml` seeds read-only devices; the Devices page adds, edits, re-tokens (checked before saving), removes and imports.
- **Monitor.** One loop per device: probe, classify (online, offline, unauthorized, mismatch, incompatible), follow `/v1/events`, keep the last tree for offline devices.
- **Proxy.** `/api/devices/{id}/v1/...` adds the token, refuses streams, and times out after 330 s.
- **Guard.** An unknown Host gets 421, a cross-origin write 403, a non-JSON API write 415.
- **Streams.** One SSE stream per tab. An agent subscription follows the agent's and its session's streams, reduces them with the client's models and renders the page's own Razor partials, sending `view` frames (upserted, removed and ordered items; replaced queue, waiting and task panels; JSON state). Device and session subscriptions pass frames through.
- **UI.** The approved mockups as Razor views and ES modules without a bundler: the tree with keyboard and resizer; the agent view (transcript, tool calls with nested calls, job cards with approvals, the waiting panel for questions, plan reviews and job approvals, the queue); the composer with the slash menu; the inspector (Session with the model picker and context bar, State with the JSON and field editors, Tasks, Tools with read-only, Extend); dialogs; deep links (`/d/{device}/s/{session}/a/{agent}`); the selection kept in the hub's UI state; both themes and phone widths. Bootstrap 5.3.3, Bootstrap Icons 1.11.3 and the Lobe provider icons are vendored under `wwwroot`.

## Verification

- `tests/PiSharp.Durable.Hosting.Tests`: 44 cases (Phases 2 and 3).
- `tests/PiSharp.Durable.Server.Tests`: 12 cases, ports of durable-pi's api, streams and recovery tests: the transcript parity check, a late joiner, a closed viewer, the slow-viewer cut-off, and a killed device process resuming a job with exactly one job entry.
- `tests/PiSharp.Durable.Web.Tests`: 4 cases, the hub as a child process against an in-process faux device (guard, registry, proxy, pages and panels, rendered view frames).
- A manual browser pass with Playwright against a faux device: create a session, send, streamed Markdown and tool calls, an App State increment, a Release check waiting through a device restart and approved after a reload, a question answered, `/fork`, read-only, the Devices page's token check, 390 px without horizontal scrolling, and the light theme.
- `tests/PiSharp.Durable.Web.BrowserTests`: 23 cases, PLAN.md Appendix C in headless Chromium through Microsoft.Playwright, with the Web suite's fixture (the hub as a child process, an in-process faux device that can be stopped and restarted on its port). They cover items 1-30, 32 and 33: imported and re-tokened devices, the New session dialog, Markdown and bash replies, busy follow-up, withdraw, reject (409) and steer, Esc, fork with breadcrumb and picker, renames, the model picker and signatures, thinking and usage, read only, Extend with the command guard and prompt files, App State with a conflict, the three jobs (Release check approved after a reload, Stop all), the context bar by keyboard, the slash menu, deep links and the restored selection, two tabs streaming, the tree by keyboard, an offline device recovering, rails and hotkeys, 390 px in both themes, archiving, a question answered and a plan approved. Every case fails on an uncaught page error (item 28). Not covered: item 31 (nested tool calls) and the agent-started job card of item 14 (see [Not done](#not-done)). Install the browser once with `pwsh tests/PiSharp.Durable.Web.BrowserTests/bin/<configuration>/net10.0/playwright.ps1 install chromium`; `--artifacts <directory>` keeps a screenshot, the HTML and the hub's log of each failed case.

`.github/workflows/durable.yml` runs the Hosting, Server and Web suites on Windows, Linux and macOS, and the browser suite on Linux (job `browser`, which installs Chromium with its system libraries).

## Not done

- Running the device as a Windows service or systemd unit (needs the Microsoft.Extensions.Hosting service packages).
- Moving the `pisharp durable` TUI's own host onto the library. The TUI is unchanged and keeps its own session directories; Phase 9's `attach` is the natural place.
- The opt-in real-model suite, and a contract test against a TypeScript `dpi-backend`.
- A job run the agent starts with `run_durable_job` has no card in the chat: only `HostedSession.RunJobAsync` appends the `pisharp.job` anchor entry, so `_JobCard`'s "started by the agent" never shows (Appendix C item 14 checks the tool result and the Jobs tab instead).
- A page opened on a session the hub's tree does not list yet (a deep link right after another client created it) leaves the selection at the first `network` frame ("That session is no longer on the device."). The browser suite waits for the tree first.
- A browser case for nested tool calls (Appendix C item 31): the device offers no tool that calls tools (such as codemode), so no call has a `parentToolCallId` to nest.

## Consequences

- `feature/durable-network` must merge after `feature/durable-interaction` (PR #24), or be rebased onto `main` once #24 lands.
- Adapting durable-pi's designs is recorded in `THIRD-PARTY-NOTICES.md` under its MIT notice. Its model icons and brand mark are not copied.
- New dependencies, all in the unpacked web app except the ASP.NET Core framework reference: Markdig (BSD-2-Clause), HtmlSanitizer and AngleSharp (MIT), YamlDotNet and Microsoft.Data.Sqlite (already used); vendored Bootstrap, Bootstrap Icons and Lobe Icons (MIT); for the browser tests only, Microsoft.Playwright 1.63.0 (MIT; it carries the Apache-2.0 Playwright driver and a Node.js runtime, and downloads Chromium on the test machine). `PiSharp.Cli` gains the ASP.NET Core shared framework through `PiSharp.Durable.Server`.
- `docs/architecture.md` lists browser and web interfaces as deferred; this branch is the expansion that ends that deferral for durable sessions.
