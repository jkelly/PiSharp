# Experimental port of pi-durable

Status: approved by the project owner on 2026-10-12, on branch `feature/pi-durable` only. It supersedes the `pi-durable` and experimental `durable` exclusions in [0002-pi-1.1.0-sync.md](0002-pi-1.1.0-sync.md) for that branch. Retargeted to Pi v1.2.0 on 2026-10-15 (see [Pi v1.2.0 retarget](#pi-v120-retarget)); the `exclude.durable` and `exclude.chord` rows of [0006-pi-1.2.0-sync.md](0006-pi-1.2.0-sync.md), and its exclusion of codemode's `sandbox.start()`, apply to `main` only (see [Codemode `Start()`](#codemode-start)).

## Context

Pi v1.1.0 includes `@earendil-works/pi-durable`, an experimental durable agent harness. It commits every model turn, streamed partial, tool call and queued message to storage before showing it. A process that dies mid-turn resumes on reopen. Tools are not run twice unless they are declared replay-safe. Subagents run as child conversations owned by the calling task.

Pi also ships an experimental agent on top of it in `packages/coding-agent/src/experimental/durable`. That agent runs from source and is not wired into the `pi` command.

Decision 0002 left both out of the native port. The owner now wants them in PiSharp.

Pi v1.2.0 changed `pi-durable` heavily: tool task v2 with nested tool calls, structured output, deferred tools and `tool_search`, durable code mode, images, storage-failure and session semantics, and a rewritten scheduler. It also deleted the experimental durable agent (`packages/coding-agent/src/experimental/durable`). Chord replaced its facet and service runtime with a new host runtime; the replicated state that `pi-durable` uses moved to `chord/src/state.ts`.

## Decision

1. **Scope.** Port `pi-durable` as a library, faithful to upstream `packages/durable/docs/spec.md`. Port the experimental durable agent as `pisharp durable`. The `vacation` demo, `pi-env`, Chord facets/services/wire, Chord's v1.2.0 host runtime (which replaced facets and services), and the Cloudflare adapters (the SQLite storage and, from v1.2.0, the image processor) stay out of scope. Of Chord, only what `pi-durable` imports is ported: JSON values, the delta operations and tracker, the durable context and replicated state.
2. **Baseline.** The port was pinned to Pi v1.1.0, commit `abe508e1b89912adde45528136c3221eb69acdd7`, and is now pinned to Pi v1.2.0, commit `06a466935536f8c9bcacb2d5efc0fc021be1bdff` (`DurableUpstream.Commit`). Upstream durable is experimental and changed its API eight times in its first week, so it is re-synced only with PiSharp's baseline syncs. `compatibility/durable-port-map.json` maps every upstream file to its C# counterpart and status, and records what changed upstream since the previous pin; a file whose port has not caught up with the pinned commit is `needs-sync`, never `ported`.
3. **Branch and release.**
   - The work lives on `feature/pi-durable` and is not merged to `main` or released until the owner decides.
   - Every public type carries `[Experimental("PISHARP_DURABLE001")]`, and the projects are not packable.
   - If a release is later approved, it uses prerelease versions (`-experimental.N`) and says "EXPERIMENTAL" in the package descriptions.
4. **Packages.**
   - `PiSharp.Durable` holds the core: the Chord subset, records, session, harness, memory and JSONL storage, and the conformance kits. It references only Contracts and AI.
   - `PiSharp.Durable.Sqlite` holds the SQLite storage.
   - `PiSharp.Durable.Tools` holds the execution environment and coding tools. From work list area d it also holds durable code mode. It then references `PiSharp.Codemode`, an existing project; see [Codemode `Start()`](#codemode-start).
   - The CLI mode lives in `PiSharp.Cli/Durable`.
5. **Storage.** Memory, JSONL and SQLite, as upstream. SQLite uses `Microsoft.Data.Sqlite` with its `SQLitePCLRaw` e_sqlite3 native assets. That makes it PiSharp's fourth external runtime dependency, scoped to `PiSharp.Durable.Sqlite` only. It is pinned exactly and needs license notices, packaging validation entries and locked restore entries before any release.
6. **Compatibility.**
   - **What matches upstream:** the SQLite schema (v1 DDL), record JSON, entry, document and task kinds, and the session directory and lock layout under `~/.pi/agent/experimental/durable-sessions`. Either tool can then continue the other's sessions. Since v1.2.0 this holds for the library only: upstream has no durable agent, and v1.1.0 Pi cannot resume v1.2.0 tool records, so `pisharp durable` sessions are compatible with Pi v1.1.0's agent only until the library sync changes the records (work list area l).
   - **What may differ:** the patch operations PiSharp writes, provided both sides replay each other's operations to the same values. Upstream treats operation shape as non-canonical.
7. **Intentional difference: the `pisharp durable` entry point.** Upstream has no CLI entry point for the durable agent; it runs `experimental/durable/main.ts` from source. PiSharp runs it as the hidden subcommand `pisharp durable [--continue|-c]`, dispatched in `PiCommand.RunCoreAsync` before `mcp`, left out of `--help`, and printing a one-line experimental notice to stderr. Any other argument fails with upstream's `Unknown argument: X`.
8. **Intentional difference: `pisharp durable` is PiSharp-only.** Upstream deleted its experimental durable agent in v1.2.0. By owner decision on 2026-10-14, PiSharp keeps `pisharp durable` as a PiSharp-only experimental mode. It stays a port of the Pi v1.1.0 agent (its sources cite `abe508e1b89912adde45528136c3221eb69acdd7`; the port map marks those rows `pisharp-only`), and it adapts to the durable library as the library follows newer pins. Its session layout under `~/.pi/agent/experimental/durable-sessions` no longer has an upstream counterpart to stay compatible with beyond Pi v1.1.0.

## Consequences

- The plan has 14 phases (P0–P13) with milestones: M0 records and storage, M1 a durable chat turn, M2 tool turns with kill-and-resume, M3 an interactive `pisharp durable`. Each milestone is reported to the owner before the next phase starts.
- Tests are ported from upstream `packages/durable/test` into `tests/PiSharp.Durable.Tests`. The storage conformance suite runs against every backend, directly and after reopen. Branch CI runs on Windows, Linux and macOS arm64.
- The branch merges `main` regularly and regenerates integrity pins and lock files after each merge. Main-line projects change only through additive seams, which are flagged for separate upstreaming to `main`.

## Model adapter

`NativeDurableModels` in `src/PiSharp.Durable/Models` is the production `IDurableModels` (pi-ai `createModels()` with the built-in providers). It looks models up in the pi-ai catalog shards of PiSharp's baseline (1.2.0 since the retarget), which it embeds by link from `src/PiSharp.Cli/Models` and verifies by hash, or in host catalogs, and streams them over PiSharp.AI's native transports: `openai-completions`, `openai-responses`, `anthropic-messages`, `google-generative-ai`, `mistral-conversations` and `pi-messages`. API keys come from the host's `ResolveApiKey` first, then the provider's environment variables (`ProviderEnvironmentKeys`), read through the host's `Environment` reader because the portable core never reads the process environment. Anthropic uses only `ANTHROPIC_API_KEY`. Providers whose auth resolves per request, and Anthropic OAuth or auth tokens, need the CLI adapter (P12): github-copilot, openai-codex, amazon-bedrock, google-vertex, Cloudflare and azure. `TimeoutMs` is enforced by the adapter as an idle deadline. `MaxRetries` and `MaxRetryDelayMs` reach only google-generative-ai, and durable generation owns retries. `Transport`, metadata other than `user_id`, `Deferred` and pi-ai's `clampMaxTokensToContext` are not mapped. No pi-ai 1.1.0 provider implements deferred responses. The CLI adapter (P12) is `src/PiSharp.Cli/Durable/DurableModels.cs`: models come from PiSharp.Cli's `ModelRegistry` (built-in catalogs, models.json, persisted catalogs), and `NativeDurableModelsOptions.HostTransport` lets it stream each request through the CLI's live route of the model (`LazyModelTransport`), so auth.json credentials with OAuth refresh, models.json keys and headers, GitHub Copilot token exchange, OpenAI Codex OAuth, Amazon Bedrock and Google Vertex ambient credentials, the Cloudflare providers and Azure endpoint configuration all work in `pisharp durable`; a model without a live route falls back to the native routes with the registry's key through `ResolveApiKey`. `samples/Durable.Chat` is the M1 demo (upstream `test/examples/16-real-model.ts`). Neither file is an upstream port, so neither has a port-map row.

## Crash tests

The `crash.<backend>.<scenario>.<checkpoint>` cases in `tests/PiSharp.Durable.Tests/Crash` kill a real process. The test executable relaunches itself as a child (`--crash-child <scenario> <backend> <storage path> <checkpoint>`), using the same `dotnet` host and test DLL or the same apphost. The child opens a Harness on a SQLite file or JSONL directory, runs a scripted chat, tool, follow-up, request-id or abort scenario, and prints `CHECKPOINT <name>` when it reaches the checkpoint. The parent then kills the child's process tree and reopens the storage the plain way `pisharp durable --continue` will, with no cleanup step. It checks the guarantees of upstream `spec.md`: a turn finishes with exactly one answer and one user entry, a non-replay-safe tool interrupted after intent is answered with the interrupted result and never rerun, a replay-safe tool reruns exactly once, queued input and request ids are never duplicated, and an abort mark completes bottom-up. Checkpoints sit in every generation phase (prepare, request, streaming partial, retry, tools) and on both sides of selected commits. MemoryStorage has no crash cases, because its data does not survive a kill. These tests are authored, not ported, so they have no port-map row.

## Port status

At the end of P13, `compatibility/durable-port-map.json` maps 174 upstream files:

| status | files |
|---|---:|
| ported | 168 |
| excluded | 2 |
| skipped | 4 |

None is planned or in progress.

**Test cases.** `tests/PiSharp.Durable.Tests` has 2485 cases:
- 2411 authored natively (ported upstream tests, examples, crash and smoke cases);
- 73 upstream-capture cases (`compat.*`), which open, read, resume and rewrite sessions the pinned upstream wrote, and replay its ops corpus;
- 1 reverse case, `compat.reverse.upstream-reads-pisharp-sessions`, which runs only when `PISHARP_DURABLE_UPSTREAM` points at an upstream checkout and is skipped otherwise.

`tests/PiSharp.DurableCli.Tests` has 57 cases.

**Excluded (by this decision):**
- `src/storage/sqlite/cloudflare.ts` and `test/sqlite-cloudflare.test.ts`. The Cloudflare Durable Object SQLite adapter is out of scope.
- These have no rows: the `vacation` demo, `pi-env`, and Chord's facets, services and wire. `test/chord-guide.test.ts`, which used them, was ported with direct subscriptions to the same document states until upstream deleted it in v1.2.0; its cases were retired with it.

**Skipped:**
- `test/provider-session-cache-e2e.test.ts` and `test/system-order-cache-e2e.test.ts` need live provider credentials.
- `test/fixtures/delete-buffer.ts` and `test/fixtures/utf8-byte-length-without-buffer.ts` check behavior without Node's global `Buffer`. .NET has no such global; `TruncateCases` checks `Truncation.Utf8ByteLength` against .NET's UTF-8 encoding instead.

**Intentional mappings recorded in the map:**
- **Erased type parameters.** TypeScript's phantom type parameters have no C# counterpart: `TaskId<Result>`, typed task input and checkpoints, `SettledTask<Result>`, and a document's value type. Tasks are JSON-typed, as documents are. Exhaustive phase maps and typed task input are checked at run time, not compile time. `types.test.ts`'s Harness typing test is a compile-checked case of the C# surface.
- **`16-real-model`** runs only with `OPENAI_API_KEY`. The tests run it without the key and pin upstream's skip line.
- **`19-json`'s first streamed partial** varies between Node runs, with pi-ai's random faux token size. PiSharp's run is fixed and equals captured Node runs. They compare as JSON with sorted keys, with clock and random values as placeholders.
- **Examples 19, 21, 23 and 24** run on a manual example clock that is driven like Node's event loop.
- **`listFiles`** is optional upstream, and only `pi-env` implements it: its local environment lists through its daemon, which walks with the Rust `ignore` crate. `pi-env` stays out of scope, but `LocalFileSystem` implements `ListFilesAsync` in process with the daemon's semantics (`Env/Local/LocalFileListing.cs` ports `list.rs` and the `ignore` 0.4.33 walk and gitignore matching it configures; no new package). So the local env conformance runs with `listFiles` on, as `pi-env`'s does, and `env.list-files.*` ports the daemon's own tests and the crate's gitignore tests.

- **Storage failures (v1.2.0, area b).** As upstream's README "Errors" section has it, a storage error fails and closes the Harness, `SessionFailedException` (upstream `SessionFailed`) carries it as `InnerException`, `StorageRequestException` (upstream `StorageRequestError`) fails only an invalid read, and `ISession.Closed` settles with a `SessionEnd`. Three mappings:
  - A read whose bounds a line job captured continues off the line (`Conversation.ContextAsync`); upstream's single thread starts its first Storage call before the closing line job runs. That read holds a lease (`Session.HoldStorageOpen`): close admits the lease's first call and waits for it, and refuses its later pages as upstream does.
  - A listener's rejected promise is a faulted task from the asynchronous `SubscribeCommits`/`SubscribeClose` overloads. `OnReport` stays an `Action`: its throw is dropped, but an `async void` handler's exception escapes to the thread pool, where no caller can contain it.
  - Document cache retention sweeps with .NET timers, which never keep a process alive, so upstream's fallback for timers that cannot be unreferenced is never needed. The suite runs a second time with `PI_DURABLE_DOCUMENT_RETENTION_MS=0` in the branch CI, as upstream's `npm test` does.

**Benchmarks** (`benchmarks/PiSharp.Durable.Benchmarks`) are report only; see `docs/durable-benchmarks.md`.
- On the one Windows machine measured, neither runtime meets upstream's 0.4 s target for `cat` of 1 GiB:
  - upstream under Node: about 1.75 s;
  - PiSharp: 2.25 to 2.57 s.
- PiSharp's `api.output` firehose is about 3 times slower than Node's.
- Its on-disk footprint for the seeded benchmark data matches upstream's byte for byte.

## Pi v1.2.0 retarget

Stage 3a of the v1.2.0 sync (2026-10-15) moved the branch onto `main`'s Pi v1.2.0 sync and retargeted the pin; it ports no v1.2.0 library change yet.
- `sync/pi-1.2.0` was merged into the branch. The durable lock fixes in `PiLockfile` and `PiProjectTrust` were the same as `main`'s (#15), so one version stays: `PiLockfile` keeps the branch's superset (proper-lockfile `lock()`, `updateLock` and `unlock` for durable sessions).
- `NativeDurableModels`' embedded catalog follows PiSharp.Cli's pi-ai 1.2.0 shards.
- `DurableUpstream.Commit` and the branch CI's port-map job moved to v1.2.0. `tools/DurablePortMap/build-port-map.mjs` reads both pins from git objects and keeps recorded progress.
- Source headers whose cited upstream files are byte-identical at both pins were re-pinned with `tools/CompatibilityReport/repin-sources.mjs --paths`; `compatibility/durable-sync-1.2.0-repin.json` lists the re-pinned, pending and historical citations. Headers that cite changed files, differential cases and captured fixtures stay on v1.1.0 until their area is synced and recaptured.
- `compatibility/durable-sync-1.2.0-worklist.json` groups every changed, new and deleted upstream durable and Chord file and changelog item into port areas, with the PiSharp files and tests each touches, a size and dependencies.

Port map against v1.2.0 (214 rows):

| status | files |
|---|---:|
| ported (upstream unchanged) | 84 |
| needs-sync (upstream changed) | 75 |
| planned (new in v1.2.0) | 38 |
| removed-upstream | 2 |
| pisharp-only (`pisharp durable`) | 7 |
| excluded | 4 |
| skipped | 4 |

Upstream changes recorded in the map: 75 modified, 1 renamed (`chord/src/services/state.ts` to `chord/src/state.ts`), 40 added and 9 removed (the 7 experimental agent files, `chord/src/services/state-internals.ts` and `test/chord-guide.test.ts`). The added rows include `images/cloudflare.ts` and `images/photon-wasm.d.ts`, which are excluded.

## Codemode `Start()`

Added on 2026-10-15 as the prerequisite of work list area d (durable code mode).

**What.** Pi v1.2.0's deterministic `sandbox.start()` (pi-codemode CHANGELOG 1.2.0, Added #4) is ported to `PiSharp.Codemode` as `CodemodeSandbox.Start()`. It provides:
- a virtual clock from `startedAt`;
- `Math.random` from `seed`;
- UTC;
- no `WeakRef` or `FinalizationRegistry`;
- results applied in the order the host numbers them;
- `OnStarted` with the engine's fingerprint, `OnCall`, `OnIdle` with the calls issued after each result, and `CodemodeRun.Deliver()` and `Finished`.

[codemode-engine.md](../compatibility/codemode-engine.md#deterministic-runs-start-branch-featurepi-durable-only) describes the API, the determinism design, what it guarantees and the fingerprint.

**Why on this branch.** Decision 0006 excludes `start()` on `main` (applicability row `codemode.new-sandboxes`), because the coding agent does not use it. Upstream's durable code mode is built on it, so this branch follows upstream and adds it. The branch's copy of the applicability table splits it into the row `codemode.deterministic-start` (`branch-port`). The other new sandbox hosts stay excluded: `RemoteCodemodeSandbox`, `InlineCodemodeSandbox` and the portable entry.

**Additive main-line seam.** The change is flagged for upstreaming to `main` with the durable work, or when the exclusion lapses.
- `ExecuteAsync()` behaves as before, and every existing Codemode case passes unchanged.
- The new public surface is additive:
  - `Start`, `CodemodeRun`, `CodemodeRunOptions`, `CodemodeHostCall`, `CodemodeCallTarget`, `CodemodeOutcome`, `CodemodeDeclaredTool`, `CodemodeDeclaredGlobal` and `CodemodeDuring`;
  - `CodemodeResult.During`;
  - `CodemodeSandboxOptions.InterruptBudget`, which is off by default.
- The worker protocol gains optional fields and the `started`, `idle` and `exhausted` messages. A worker process and its host always come from the same build.

**Dependencies.** No new NuGet package.
- `PiSharp.Durable.Tools` will reference `PiSharp.Codemode`, and through it Jint 4.16.3 and Acornima 1.7.0. Decision 0003 already pins and licenses both for `PiSharp.Codemode`.
- Area d adds the reference and its lock-file entries.
- The reason for the reference: durable code mode replays a script from its journal, and only a deterministic engine run can do that.

**Intentional difference: the fingerprint.** Upstream's fingerprint names quickjs-wasi and QuickJS. PiSharp's names Jint, Acornima, the prelude (whose FNV-1a equals upstream's), the Jint shims, the runner's deterministic version, the .NET runtime, the platform and the limits. The two never match, so durable code mode cannot move a running script between Pi and PiSharp: its fingerprint check refuses the resume instead of diverging silently. Durable sessions stay compatible in every other respect that point 6 lists.

**Evidence.** Commit `ca3f1d1`. `tests/PiSharp.Codemode.Tests/DeterministicCases.cs` runs on both engine hosts (worker process and thread):
- the `deterministic.test.ts` cases (the remote-transport case has no PiSharp host);
- an authored replay of a run interrupted at several midpoints, continued on the other host;
- authored checks of Jint's extra built-ins and of the interrupt budget;
- a differential against the real `@earendil-works/pi-codemode` 1.2.0 `start()` over 16 scenarios (`Deterministic/deterministic-goldens.json`). It covers events, issued counts, call arguments, `Math.random` values, clock readings, outputs and outcomes.
