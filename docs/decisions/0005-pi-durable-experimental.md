# Experimental port of pi-durable

Status: approved by the project owner on 2026-10-12, on branch `feature/pi-durable` only. It supersedes the `pi-durable` and experimental `durable` exclusions in [0002-pi-1.1.0-sync.md](0002-pi-1.1.0-sync.md) for that branch. Retargeted to Pi v1.2.0 on 2026-10-15 (see [Pi v1.2.0 retarget](#pi-v120-retarget)), and the v1.2.0 update was completed the same day (see [Pi v1.2.0 update complete](#pi-v120-update-complete)); the `exclude.durable` and `exclude.chord` rows of [0006-pi-1.2.0-sync.md](0006-pi-1.2.0-sync.md), and its exclusion of codemode's `sandbox.start()`, apply to `main` only (see [Codemode `Start()`](#codemode-start)).

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
   - `PiSharp.Durable.Tools` holds the execution environment and coding tools. Its read tool's image processor uses PiSharp.Tools' image codec seam (`IImageCodec`); the host passes a codec, such as PiSharp.Tools.Skia's (work list area e). From work list area d it also holds durable code mode. It then references `PiSharp.Codemode`, an existing project; see [Codemode `Start()`](#codemode-start).
   - The CLI mode lives in `PiSharp.Cli/Durable`.
5. **Storage.** Memory, JSONL and SQLite, as upstream. SQLite uses `Microsoft.Data.Sqlite` with its `SQLitePCLRaw` e_sqlite3 native assets. That makes it PiSharp's fourth external runtime dependency, scoped to `PiSharp.Durable.Sqlite` only. It is pinned exactly and needs license notices, packaging validation entries and locked restore entries before any release.
6. **Compatibility.**
   - **What matches upstream:** the SQLite schema (v1 DDL), record JSON, entry, document and task kinds, and the session directory and lock layout under `~/.pi/agent/experimental/durable-sessions`. Either tool can then continue the other's sessions. Since v1.2.0 this holds for the library only: upstream has no durable agent, and v1.1.0 Pi cannot resume the `pi.tool` version 2 records the 1.2.0 library writes. For `pisharp durable`, only the session layout and lock still match Pi v1.1.0's agent: each finds the other's newest session for a directory with `--continue` and waits for or refuses the other's lock (`sessions.interop.pinned-upstream-under-node`, pinned to v1.1.0). Whether v1.1.0 can then resume the records is not claimed (work list area l).
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

At the end of the Pi v1.2.0 update (work list area h, 2026-10-15), `compatibility/durable-port-map.json` maps 214 upstream files:

| status | files |
|---|---:|
| ported | 196 |
| pisharp-only (`pisharp durable`) | 7 |
| excluded | 5 |
| skipped | 4 |
| removed-upstream | 2 |

None is planned, in progress or `needs-sync`. Of the 196 ported files, 84 are unchanged since v1.1.0, 73 changed in v1.2.0, 1 moved (`chord/src/state.ts`) and 38 are new in v1.2.0. At the end of P13, against v1.1.0, the map had 174 files: 168 ported, 2 excluded and 4 skipped.

**Test cases**, from the final run of the update (2026-10-15):

| suite | cases | authored | upstream capture | skipped |
|---|---:|---:|---:|---:|
| `tests/PiSharp.Durable.Tests` | 3044 | 2848 | 195 | 1 |
| of which crash cases (`crash.*`) | 54 | 54 | - | - |
| `tests/PiSharp.DurableCli.Tests` | 72 | 72 | - | - |

- **Authored** cases are ported upstream tests, examples, crash and smoke cases, run natively.
- **Upstream capture** cases (`compat.*`) open, read, resume and rewrite sessions the pinned upstream wrote, compare agent events and observed results, and replay its ops corpus.
- **Skipped:** `compat.reverse.upstream-reads-pisharp-sessions` runs only when `PISHARP_DURABLE_UPSTREAM` points at an upstream checkout. Run against the pinned v1.2.0 checkout, it verified 152 of 152 checks.
- **DurableCli:** `sessions.interop.pinned-upstream-under-node` is counted as passed, but it runs only when `PISHARP_DURABLE_UPSTREAM_V110` names a Pi v1.1.0 checkout. It is not counted as skipped.
- The durable suite passes with no failures, both normally and with `PI_DURABLE_DOCUMENT_RETENTION_MS=0`.
- At the end of P13 the counts were 2485 and 57.

**Excluded (by this decision):**
- `src/storage/sqlite/cloudflare.ts` and `test/sqlite-cloudflare.test.ts`. The Cloudflare Durable Object SQLite adapter is out of scope.
- `src/images/cloudflare.ts` (the Cloudflare Images adapter) and `src/images/photon-wasm.d.ts` (type declarations for Photon's WebAssembly module, which has no C# counterpart; see Images below).
- `chord/src/types.ts`. In v1.2.0 it holds only the types of Chord's host runtime, which is out of scope.
- These have no rows: the `vacation` demo, `pi-env`, and Chord's facets, services and wire. `test/chord-guide.test.ts`, which used them, was ported with direct subscriptions to the same document states until upstream deleted it in v1.2.0; its cases were retired with it.

**Removed upstream:**
- `chord/src/services/state-internals.ts`, which v1.2.0 merged into `chord/src/state.ts`. `ReplicatedStateInternals.cs` was deleted with it (area f).
- `test/chord-guide.test.ts`; its cases were retired with it.

**Skipped:**
- `test/provider-session-cache-e2e.test.ts` and `test/system-order-cache-e2e.test.ts` need live provider credentials.
- `test/fixtures/delete-buffer.ts` and `test/fixtures/utf8-byte-length-without-buffer.ts` check behavior without Node's global `Buffer`. .NET has no such global; `TruncateCases` checks `Truncation.Utf8ByteLength` against .NET's UTF-8 encoding instead.

**Intentional mappings recorded in the map:**
- **Erased type parameters.** TypeScript's phantom type parameters have no C# counterpart: `TaskId<Result>`, typed task input and checkpoints, `SettledTask<Result>`, and a document's value type. Tasks are JSON-typed, as documents are. Exhaustive phase maps and typed task input are checked at run time, not compile time. `types.test.ts`'s Harness typing test is a compile-checked case of the C# surface.
- **`16-real-model`** runs only with `OPENAI_API_KEY`. The tests run it without the key and pin upstream's skip line.
- **`19-json`'s streamed partials** vary between Node runs: pi-ai's faux token size is random, and a progress tick can fall inside the stream. PiSharp's run is fixed (token size 4, paced on the example clock). Both modes compare as text, key order included, with clock and random values as placeholders, against the v1.2.0 capture (`Fixtures/v1.2.0/examples-19-json-*.upstream.jsonl`). That run committed its streaming answer once mid-stream, so the comparison leaves out that ops line and expects the final message in its `message_start` (area e; see Images below).
- **Examples 19, 21, 23 and 24** run on a manual example clock that is driven like Node's event loop.
- **Examples under v1.2.0 (area h).** Every upstream example of the pinned v1.2.0 ran under Node without API keys, and `examples.*` pins what each printed, as JSON in place of `util.inspect`. Seven examples changed upstream, only by the tool result's `content` to `output` rename, which `ToolExecutionResult.Output` (area a) already covered; their output is unchanged. **Intentional difference:** `23-subagent-background.ts` did not change and still returns its tool replies as `content`, which v1.2.0 no longer reads, so under v1.2.0 Node prints every reply empty (`  → ` and `OK. `). The C# tool returns them as `Output`, as the example means and as v1.1.0 ran it, and `examples.*` keeps the replies.
- **`listFiles`** is optional upstream, and only `pi-env` implements it: its local environment lists through its daemon, which walks with the Rust `ignore` crate. `pi-env` stays out of scope, but `LocalFileSystem` implements `ListFilesAsync` in process with the daemon's semantics (`Env/Local/LocalFileListing.cs` ports `list.rs` and the `ignore` 0.4.33 walk and gitignore matching it configures; no new package). So the local env conformance runs with `listFiles` on, as `pi-env`'s does, and `env.list-files.*` ports the daemon's own tests and the crate's gitignore tests.

- **Storage failures (v1.2.0, area b).** As upstream's README "Errors" section has it, a storage error fails and closes the Harness, `SessionFailedException` (upstream `SessionFailed`) carries it as `InnerException`, `StorageRequestException` (upstream `StorageRequestError`) fails only an invalid read, and `ISession.Closed` settles with a `SessionEnd`. Three mappings:
  - A read whose bounds a line job captured continues off the line (`Conversation.ContextAsync`); upstream's single thread starts its first Storage call before the closing line job runs. That read holds a lease (`Session.HoldStorageOpen`): close admits the lease's first call and waits for it, and refuses its later pages as upstream does.
  - A listener's rejected promise is a faulted task from the asynchronous `SubscribeCommits`/`SubscribeClose` overloads. `OnReport` stays an `Action`: its throw is dropped, but an `async void` handler's exception escapes to the thread pool, where no caller can contain it.
  - Document cache retention sweeps with .NET timers, which never keep a process alive, so upstream's fallback for timers that cannot be unreferenced is never needed. The suite runs a second time with `PI_DURABLE_DOCUMENT_RETENTION_MS=0` in the branch CI, as upstream's `npm test` does.

- **Input entries (v1.2.0, area i).** Upstream's input `entry: { kind, data }` is `InputEntry(Kind, Data)`, passed to `SubmissionDraft.Input`. It is typed, so an untyped caller's extra fields cannot reach it; only its kind is checked (non-empty, outside `pi.`). `harness.inbox-differential.steer` compared the capture's one `pi.tool` task in its v1 shape until area a ported tool task v2; it compares the capture as it is since.

- **Compaction failures (v1.2.0, area j)** are ported as upstream has them: `pi.live.compactionFailure`, the snapshot's `CompactionFailure` and `CompactionEndEvent.Error`, and a generation whose blocking threshold compaction did not complete fails with `model_error` before its request. `Live.cs`, `Generation.cs` and `Events.cs` moved to v1.2.0 with area a, which owns their rows.

- **Scheduler (v1.2.0, area c).** `Scheduler.cs` ports v1.2.0's `scheduler.ts` as a whole: the ownership index bounded by live work, the candidate sets of each pass, `TaskOptions.AbandonOnRestart` with `TaskRecord.AbortReason` (`restart`), `ITaskRuntime.AbortOwnedAsync`/`OwnedTasksAsync`, the cancellation barrier before each phase and wait-cycle rejection. Mappings:
  - One short lock guards the scheduler's in-memory state, as before. Its sets are insertion-ordered (`OrderedSet`), because a pass stages its writes in the order upstream's Sets iterate; the sweep job is enqueued from the thread pool, so it never starts inline inside that lock.
  - Upstream's `SCHEDULER_INDEX_SIZES` getter is the internal `DurableHarness.SchedulerIndexSizes`. Tests read it after `DurableTaskScheduler.QuiesceAsync`, a barrier for the passes that run on the thread pool here and in drained microtasks upstream; the two ownership cases that count `Set.prototype` iterations count the members the scheduler's own sets yield (`SetEntries`).
  - `OwnedTasksAsync` returns the committed records, which are immutable, instead of copies.
  - The default that gives nested calls and `api.createTask()` children of tools that are not replay-safe `abandonOnRestart` arrived with area a (`ToolExecutionApi.ChildTaskOptions`, applied in `CreateTaskAsync` and to nested calls); `harness.nested-tools-restart.*` and `compat.abandon-on-restart.*` / `compat.nested-tools-restart.*` (run and resume) are complete since. `compat.wait-cycle.*` runs upstream's scenario in full.
  - `bench:scheduler-memory` is `SchedulerMemoryBench.cs`. `bench:scheduler-fanout` makes nested calls and is `SchedulerFanoutBench.cs` (area a); the PiSharp-authored `scheduler-scale` (`SchedulerScaleBench.cs`, with a Node twin in `benchmarks/PiSharp.Durable.Benchmarks/node/`) measures the same fan-out with `api.createTask()` children, and the changelog's owned chains.

- **Tool task v2 (v1.2.0, area a).** `pi.tool` is version 2: model-issued and nested calls are kind-tagged in its input and result, and version 1 tasks migrate when they run (a version 1 result has no kind and is a model-issued call's). `ToolExecutionResult.Content` is `Output`, beside `StructuredOutput`; `IToolExecutionApi.ExecuteToolAsync` runs a nested call as a `pi.tool` task its caller owns, with its result in the caller's `NestedCallDoc` (`pi.tool.nested-call`), its slot in `pi.live.nestedTools` and tool events naming its parent; `ToolRegistration` gains `StructuredOutputSchema`, `Callers` and `Deferred`, `pi.agent` gains `modelTools`, and bash and powershell answer a nonzero exit with an `exit_code` error result. The record JSON matches upstream's: `compat.*` now reads the v1.2.0 capture for every scenario, writes the nested-tools, structured-output, nested-tools-restart and abandon-on-restart sessions byte for byte (JSONL) and column for column (SQLite), checks each v1.2.0 scenario's agent events (`events.json`), and opens and resumes the v1.1.0 sessions as upstream's v1.2.0 does (`upgrade-from-v1.1.0`). Mappings:
  - **Admission order.** Upstream chains a call's nested admissions on its `madeNestedCalls` memo, so the calls of one synchronous stretch commit back to back before the scheduler reacts. PiSharp holds the calls a tool makes before `Execute` first returns (its synchronous prefix) and enqueues their admission commits inside the memo's commit, so they queue behind it in call order; later calls are admitted at once.
  - **Pass order.** A reservation pass starts its abort invocations before its run invocations: an abort handler that commits at once then commits before a run invocation that runs inline on an idle line (as upstream's first-await order has it), so a tool's recovery cannot cascade a request mark over a `restart` mark first.
  - **Structured output** is validated with the TypeBox `Compile` port in `Internal/PiAi/Validation.cs` (`Check`, and the first of `Errors` for the message), compiled once per schema.
  - **Deferred tools** have no upstream area-a test (upstream's are `tool-search.test.ts`, area k); `harness.callers.deferred.*` are authored.
  - **Concurrent nested calls in `compat.nested-tools`.** Node runs the two parallel echo calls in lockstep on its event loop; PiSharp's invocations run on the thread pool. The C# scenario holds them to Node's order with a hook and waits that leave no record.
  - Example 19's events compared with the v1.1.0 capture plus the snapshot's `nestedTools` and the tool events' `taskId` from the v1.2.0 capture until area e switched them (see Images below).
  - `pisharp durable` only followed the `Output` rename (`DurableSubagent.cs`) until area l adapted it (see `pisharp durable` below).

- **`tool_search` (v1.2.0, area k).** Upstream's `search.ts` is `Search` in `PiSharp.Durable` (internal, as upstream does not export it; code mode's `searchTools()`, area d, shares it), and the `tool-search` entry point is `ToolSearch` in `PiSharp.Durable.Tools` (`CreateTool`, `CreateExtension`). Main ports the coding agent's copy of the same ranking (`PiSharp.Extensions.Abstractions`' `ToolSearch` and `Bm25Ranker`), which `PiSharp.Durable` may not reference (section 4), so the durable package keeps its own port, as upstream durable keeps its own copy. Mappings:
  - JavaScript's `toLowerCase()` maps U+0130 to `i` plus U+0307, and .NET's invariant lowercasing leaves it unchanged, so `Search` expands it before lowercasing; every other character either lowercases alike or is not ASCII afterwards, and only ASCII letters and digits survive the split. Main's ranker does not expand it (it tokenizes `İstanbul` as `stanbul`, upstream as `i`, `stanbul`); that is a main-line bug, not changed on this branch. [PR #19](https://github.com/jkelly/PiSharp/pull/19) fixes it on `main` (open, not merged at the end of the v1.2.0 update); this branch picks the fix up with its next merge of `main`.
  - Ties keep document order through LINQ's stable ordering, as through `Array.prototype.sort`.
  - Tests: `tool-search.*` ports `tool-search.test.ts`; `tool-search.errors.*` and `tool-search.search.*` compare with results the pinned upstream printed under Node; `tool-search.ranking-differential.*` compares `Search` with main's ranker over seeded random texts, queries and schemas (U+0130 left out). A one-off run of the pinned upstream tool under Node over 3000 random tool sets, queries and limits (U+0130, U+0085, U+180E and U+200B included) matched the C# tool's text, details, control and errors, and `Search.Tokenize`, in every case. The deferred-tool-search compat scenario now runs: its records match upstream's byte for byte (JSONL) and column for column (SQLite), with its events and observed tool lists.

- **Images (v1.2.0, area e).** `read` returns PNG, JPEG, GIF and WebP within the conversation model's image limits as one image block; `CodingTools.CreateCodingTools(new ReadToolOptions { Images })` gives it an `IImageProcessor`. Mappings:
  - **No Photon.** Upstream's processor is Photon compiled to WebAssembly. `ImageProcessors.Create(IImageCodec)` ports its rules over PiSharp.Tools' image codec seam, the backend main's read tool already uses in place of Photon: decode (`ProbeDimensions`), EXIF orientation, Lanczos3 resampling (`RasterImage`, Photon's filter) and PNG/JPEG encoding (`Encode`). The host picks the codec. `pisharp durable` and the tests use PiSharp.Tools.Skia's `SkiaImageCodec`; the dependency-free `BuiltinImageCodec` decodes only PNG and BMP and encodes only PNG. Upstream's rules are kept: the pass-through, `Math.round` scaling, the JPEG qualities 80, 70, 55 and 40 (tried first for a JPEG source), 3/4 shrinking, and a decode failure answered with null. Two differences:
    - `IImageCodec.Encode` returns PNG and every JPEG quality for one size at once, so a codec decodes the image again for each size it tries.
    - `exif.ts` (`Images/Exif.cs`) decides the pass-through, but the codec turns the pixels upright with PiSharp.Tools' `ExifOrientation`. That is the coding agent's reader of the same tag; the two can differ only on malformed EXIF.
  - **The Node loader** (`createNodePhotonImages`) is `ImageProcessors.CreateThreaded`. A dedicated background thread prepares images in order and exits after `IdleMs` (default 30 s) without a request. A thread that cannot start falls back to the calling thread. There is no WebAssembly memory to free.
  - **Model limits.** pi-ai 1.2.0's `Model.inputLimits` is `DurableModel.InputLimits`. `NativeDurableModels` and the CLI's `DurableModels` read it from the catalog entry (models.json overrides are merged into it). `read` takes `images.resize` over `DEFAULT_IMAGE_LIMITS` (`CodingTools.DefaultImageLimits`), as upstream does.
  - **Dependencies.** `PiSharp.Durable.Tools` uses only PiSharp.Tools' public `IImageCodec` and gains no reference. `tests/PiSharp.Durable.Tests` references the existing `PiSharp.Tools.Skia` project, so SkiaSharp 4.152.1, already pinned for the CLI, enters its lock file. No new NuGet package.
  - **Tests.**
    - `tools.images.*` ports `tools-images.test.ts` with Skia in Photon's place. Its timing-based worker test is replaced by a gated codec that shows where and when the work runs.
    - `images.wasm-module.*` checks a processor made from a codec instance, and that nothing reaches the codec before `read` gets an image. PiSharp has no WebAssembly module to compile.
    - `tools.node.read-images-match-upstream` replays the v1.2.0 capture's read of hand-made images.
    - `compat.read-image.*` writes the read-image session byte for byte (JSONL) and column for column (SQLite), with its events.
    - `tools.read-differential.*`, `tools.node.*` and example 19 read the v1.2.0 captures. Example 19's v1.2.0 run committed its streaming answer once mid-stream, a timing artifact of its 200 tokens/s faux model. The comparison leaves out that ops line and expects the final message in its `message_start`.
    - `setup.coding-tools.*` (PiSharp.DurableCli.Tests) checks that `pisharp durable` installs the coding tools with the threaded Skia processor.

- **Durable code mode (v1.2.0, area d).** `@earendil-works/pi-durable/codemode` is `DurableCodemode` in `PiSharp.Durable.Tools` (`CreateTool`, `CreateExtension`, `StoreDoc`; `CodemodeToolOptions`, `CodemodeGlobal`, `CodemodeNamespace`), over `PiSharp.Codemode`'s deterministic `CodemodeSandbox.Start()` (see [Codemode `Start()`](#codemode-start)). The start data, journal and store documents, the replay with its divergence, version, fingerprint and deadline checks, the `codemode` section, `searchTools()`/`describeTool()`/`describeNamespace()` (over `Search.RankDocuments`), the output layout and `models.*` follow upstream. Mappings:
  - **One thread of control per call.** Upstream's call lives on Node's single thread. Here each call's state is touched only on its own exclusive `TaskScheduler`: the sandbox's callbacks, which arrive on the engine's reader thread, are posted to it in order, and every await resumes on it. A JavaScript `await` always lets the reactions already queued run first; the journal pump yields after its commit (which the Session line may run inline) so results that finished meanwhile join the next batch, as upstream's do.
  - **Nested call admission.** The calls one journal commit covers are launched together; `ToolExecutionApi.AdmitTogether` admits them as one batch, as upstream's `executeTool()` calls of one synchronous stretch enqueue their admission commits back to back (the Harness otherwise admits calls a tool makes after an await one at a time). A script's arguments reach the nested call as they are, of any JSON kind (`ToolExecutionApi.ExecuteToolAsync` over a `JsonValue`), so `prepareArguments` and validation see what upstream's see.
  - **`models.*`.** pi-ai's `Models` catalog, `classify()` and `generateImages()` are new `IDurableModels` members with defaults (no models; error results), over pi-ai wire JSON. `NativeDurableModels` serves them through PiSharp.AI's `ModelOperationsRegistry` over its registered catalogs and environment keys, or a host's registry (`NativeDurableModelsOptions.ModelOperations`). The CLI's `DurableModels` keeps the defaults: `pisharp durable` does not install code mode (area l).
  - **`constrainedSampling`.** `ToolRegistration.ConstrainedSampling` (pi-ai `Tool.constrainedSampling`) enters the tool declaration; the codemode tool declares the Lark grammar of its source (`CodemodeSource.Grammar`).
  - **Intentional difference: the fingerprint.** A script interrupted under Pi is refused when PiSharp resumes it, and the other way round: its recorded fingerprint names QuickJS. `compat.codemode.*.resume` asserts that refusal on upstream's own session; the written case resumes PiSharp's own session to upstream's resumed records.
  - **Dependencies.** `PiSharp.Durable.Tools` references `PiSharp.Codemode`, and through it Jint, Acornima and `PiSharp.Extensions.Abstractions`; the lock files of the projects that reference `PiSharp.Durable.Tools` gain those entries. No new NuGet package.
  - **Tests.** `codemode.*` (32) and `codemode.output.*` (8) port `codemode.test.ts`, `codemode.describe.*` (5) `codemode-describe.test.ts`, and `codemode.models.*` (7) `codemode-models.test.ts`, plus the authored `codemode.models.native-*`. `compat.codemode.*` writes upstream's codemode session byte for byte (JSONL) and column for column (SQLite), with its events and observed results, and resumes it to upstream's records; code mode's clock, seed and wall time (upstream's own `varies`) and the engine fingerprint are normalized. `CodemodeBench.cs` ports `codemode-bench.ts`.

- **`pisharp durable` (v1.2.0, area l).** Upstream deleted its experimental durable agent in v1.2.0, so there is no upstream text to follow: `pisharp durable` keeps the v1.1.0 agent's commands and screens (its seven port-map rows stay `pisharp-only`) and changes only what the 1.2.0 library forces or newly reports. All of it is PiSharp-only:
  - **Session failure.** A storage error fails and closes the Harness. A request the failed Session rejects (`SessionFailedException`), or `ISession.Closed` settling with `SessionEnd.Failed`, sets the view's `Failure`. The TUI then shows ``Session failed: <cause>. Nothing more is saved; exit and run `pisharp durable --continue` to reopen it.`` above the notices, clears the working status, and sends nothing more from the editor. On exit it returns 1, and `pisharp durable` prints the same line to stderr once the screen is gone. The failed call's own error and the Harness's `OnReport` of it are not repeated as notices: a close listener marks the session ended before either arrives. Closing a failed session neither throws nor hangs and releases the lock, and `--continue` reopens what was committed. A failed session is not reopened in place; the line says how to reopen it.
  - **Compaction failures.** `pi.live.compactionFailure` is shown as `Compaction failed (<reason>): <error>` above the notices until a compaction produces a summary. That covers manual and automatic compactions, and faulted and orphaned ones, so `/compact` no longer adds a failure notice of its own. Its "Compacted.", "queued", "dropped" and "Nothing to compact" notices are unchanged. The TUI reads the view, not agent events, so `CompactionEndEvent.Error` is not used; it carries the same text.
  - **Nested tool calls.** The runtime gathers `pi.live.nestedTools` from every revision of the shown conversation (`DurableView.NestedCalls`, the newest 256 by task ID). The TUI draws only the newest view, and a short nested call can start, finish and leave `pi.live` between two of them. Each call is a row under its model-issued caller's card, indented under the nested call that made it. The row is pi's codemode row (`CodemodeRenderers.FormatCall`, made internal: an additive main-line seam): `…` running, `✓`/`✗` by the slot's `summary`, `⊘` for a call that left `pi.live` unfinished, with duration, cost, and the error when expanded. Collapsed, the newest eight show, as in codemode's result. A call is placed by its caller's task ID (slots carry it since 1.2.0), or by the caller's call ID when the TUI never saw the caller's slot. Rows stay after the call ends. A rebuilt transcript drops them, and places again only the calls of a model-issued call that still runs, by its task ID, because call IDs may repeat across turns; the result entry does not record nested calls. The `Result` of a nested `tool_execution_end` event is not used: the slot's `summary` carries its `isError`, `durationMs`, usage and error.
  - **Diagnostics.** The Harness appends a tool's diagnostics to the content the model reads as one `<harness>` block, for example bash's and powershell's `exit_code` result. The card shows their messages as plain lines when the entry's `data.diagnostics` matches that block. A failed command then reads as pi shows it: its output, a blank line, `Command exited with code N`, on the error background.
  - **Images.** `read` returns images through the threaded Skia processor (area e). Tool cards take pi's `terminal.showImages`, `terminal.imageWidthCells` and `outputPad` settings as interactive mode passes them, so an image is drawn, or replaced by its `[Image: …]` text, as in interactive mode.
  - **`tool_search`.** `CreateCodingRegistry` installs `ToolSearch.CreateExtension()`. The Harness settings' default extension selection leaves it out until an installed tool is deferred, as the coding agent activates `tool_search` only for deferred tools. `pisharp durable` defers none, so the model is offered the same tools as before (`read`, `write`, `edit`, `bash`, `subagent`). Code mode is not installed.
  - **Subagents.** `DurableSubagent` needed no change beyond the `Output` rename. Its child conversation is owned by the call's `pi.tool` task. The tool is replay-safe, so `abandonOnRestart` does not apply to it. Aborting the parent's turn aborts the call and the child's turn; after a restart mid-call the tool reruns, finds the child and its `subagent:<taskId>` input, and waits for the child's resumed turn.
  - **Input entries (area i)** are not used. `pisharp durable`'s notices are about the running process (model availability, failed requests, compaction outcomes), not the conversation, and stay in memory as before. Persisting them would add entries a v1.1.0-era session never had.
  - **`sessions.interop.pinned-upstream-under-node`** ran upstream's `experimental/durable/sessions.ts`, which v1.2.0 deleted. It is pinned to a v1.1.0 checkout: it runs when `PISHARP_DURABLE_UPSTREAM_V110` names a Pi checkout at `abe508e1` with `node_modules` (checked with `git rev-parse` when it is a git checkout), fails when the checkout has no `sessions.ts`, and is skipped when the variable is unset. `PISHARP_DURABLE_UPSTREAM` keeps naming the v1.2.0 checkout of the library's reverse case.
  - **Tests.** `tests/PiSharp.DurableCli.Tests` has 72 cases (58 before):
    - `runtime.session-failure.*`, `runtime.e2e.tui-session-failure-exits-1`, `runtime.compact.failure-is-the-live-compaction-failure`, `runtime.tool-search.*` and `runtime.subagent.abort-*` / `restart-*`;
    - `tui.state.nested-calls-gathered-over-revisions`, `tui.tool.nested-calls-*`, `tui.tool.diagnostics-as-plain-lines`, `tui.tool.images-follow-pi-settings` and `tui.alert.*`;
    - `setup.tool-search.*`.

**Benchmarks** (`benchmarks/PiSharp.Durable.Benchmarks`) are report only; see `docs/durable-benchmarks.md`.
- On the one Windows machine measured, neither runtime meets upstream's 0.4 s target for `cat` of 1 GiB:
  - upstream under Node: about 1.75 s;
  - PiSharp: 2.25 to 2.57 s.
- PiSharp's `api.output` firehose is about 3 times slower than Node's.
- Its on-disk footprint for the seeded benchmark data matches upstream's byte for byte.

## Pi v1.2.0 retarget

Stage 3a of the v1.2.0 sync (2026-10-15) moved the branch onto `main`'s Pi v1.2.0 sync and retargeted the pin; it ported no v1.2.0 library change itself. The work list areas then did (see [v1.2.0 update complete](#pi-v120-update-complete)).
- `sync/pi-1.2.0` was merged into the branch. The durable lock fixes in `PiLockfile` and `PiProjectTrust` were the same as `main`'s (#15), so one version stays: `PiLockfile` keeps the branch's superset (proper-lockfile `lock()`, `updateLock` and `unlock` for durable sessions).
- `NativeDurableModels`' embedded catalog follows PiSharp.Cli's pi-ai 1.2.0 shards.
- `DurableUpstream.Commit` and the branch CI's port-map job moved to v1.2.0. `tools/DurablePortMap/build-port-map.mjs` reads both pins from git objects and keeps recorded progress.
- Source headers whose cited upstream files are byte-identical at both pins were re-pinned with `tools/CompatibilityReport/repin-sources.mjs --paths`; `compatibility/durable-sync-1.2.0-repin.json` lists the re-pinned, pending and historical citations. Headers that cite changed files, differential cases and captured fixtures stayed on v1.1.0 until their area was synced and recaptured. By the end of area h every one had moved but `PiLockfile.cs`'s citation of v1.1.0's deleted `experimental/durable/sessions.ts`, whose lock `pisharp durable` keeps (PiSharp-only); the file's authored `portedRepins` section lists the area and commit that moved each, and the two retired with deleted upstream files.
- `compatibility/durable-sync-1.2.0-worklist.json` groups every changed, new and deleted upstream durable and Chord file and changelog item into port areas, with the PiSharp files and tests each touches, a size and dependencies.

Port map against v1.2.0 at the retarget (214 rows; for the final counts see [Port status](#port-status)):

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
- `PiSharp.Durable.Tools` references `PiSharp.Codemode`, and through it Jint 4.16.3 and Acornima 1.7.0. Decision 0003 already pins and licenses both for `PiSharp.Codemode`.
- Area d adds the reference and its lock-file entries (done; see Durable code mode under Port status).
- The reason for the reference: durable code mode replays a script from its journal, and only a deterministic engine run can do that.

**Intentional difference: the fingerprint.** Upstream's fingerprint names quickjs-wasi and QuickJS. PiSharp's names Jint, Acornima, the prelude (whose FNV-1a equals upstream's), the Jint shims, the runner's deterministic version, the .NET runtime, the platform and the limits. The two never match, so durable code mode cannot move a running script between Pi and PiSharp: its fingerprint check refuses the resume instead of diverging silently. Durable sessions stay compatible in every other respect that point 6 lists.

**Evidence.** Commit `ca3f1d1`. `tests/PiSharp.Codemode.Tests/DeterministicCases.cs` runs on both engine hosts (worker process and thread):
- the `deterministic.test.ts` cases (the remote-transport case has no PiSharp host);
- an authored replay of a run interrupted at several midpoints, continued on the other host;
- authored checks of Jint's extra built-ins and of the interrupt budget;
- a differential against the real `@earendil-works/pi-codemode` 1.2.0 `start()` over 16 scenarios (`Deterministic/deterministic-goldens.json`). It covers events, issued counts, call arguments, `Math.random` values, clock readings, outputs and outcomes.

## Pi v1.2.0 update complete

The update from Pi v1.1.0 to v1.2.0 (`compatibility/durable-sync-1.2.0-worklist.json`) finished on 2026-10-15 with area h. Every area landed on `feature/pi-durable` with its tests passing:

| area | what | merged |
|---|---|---|
| a | tool task v2: nested calls, structured output, callers, `modelTools` and deferred tools | `3bea3f4` |
| b | storage failure, Session lifecycle and document cache retention | `8b97eb5` |
| c | scheduler: ownership indexes, `abandonOnRestart`, `abortOwned`/`ownedTasks`, wait cycles | `595864a` |
| d | durable code mode (with the [Codemode `Start()`](#codemode-start) prerequisite, `677ea2f`) | `acfb301` |
| e | images: `read` returns images, image processor | `d527ffd` |
| f | Chord subset: replicated state move, delta append performance, json/context/types split | `d23494a` |
| g | execution environment `listFiles` | `3948843` |
| h | examples and closing bookkeeping | this change |
| i | input entries and display-only writes | `8b97eb5` |
| j | compaction failure visibility and blocking-threshold fix | `8b97eb5` |
| k | `tool_search` extension and BM25 ranking | `797099e` |
| l | `pisharp durable` adaptation (PiSharp-only mode) | `7cc49cf` |
| m | upstream capture corpus at v1.2.0 | `14c99c1` |

Area h ran every v1.2.0 example under Node and moved the seven changed examples to `ported` (see Examples under v1.2.0 above). It also re-pinned the last v1.1.0 citations whose text is current at v1.2.0 and pointed `piai.*`, `piai.validation.*` and `env.line-scan.*` at the v1.2.0 captures. One small pi-ai change was ported: the internal `Retry.cs` follows pi-ai 1.2.0's retry pattern for "provider returned an error" (#10842), and `piai.retry.provider-returned-an-error` pins upstream's results under Node. See [Port status](#port-status) for the final counts.

**Citations that stay on v1.1.0:**
- `PiLockfile.cs`' reference to the deleted `experimental/durable/sessions.ts`.
- The `pisharp durable` sources, and `PiSharp.DurableCli.Tests`' source label (`Program.Upstream`), which stay on v1.1.0 by design (section 8).
- `sessions.interop.pinned-upstream-under-node`, which is pinned to a v1.1.0 checkout.
- The v1.1.0 fixtures that `compat.upgrade-from-v1.1.0.*` and example 19's line count read.
- Historical records: this decision, the work list, the port map's previous pin and the capture tooling.

`compatibility/durable-sync-1.2.0-repin.json` lists every citation and where it moved.

**Intentional differences added by the update:**
- **Code mode's engine fingerprint** names Jint, so neither side resumes the other's interrupted scripts (see Durable code mode and [Codemode `Start()`](#codemode-start)).
- **Example 23's tool replies** are kept (see Examples under v1.2.0).
- **`pisharp durable` stays PiSharp-only** (section 8).

**Main-line seams on this branch**, flagged for `main` with the durable work:
- `PiCommand.RunCoreAsync` dispatches the hidden `durable` subcommand.
- `PiSystemPrompt.Skill` is internal, for `DurablePrompt`.
- `CodemodeRenderers.FormatCall` is internal, for `pisharp durable`'s nested call rows (area l).
- `PiSharp.Tools` exposes its internals to `PiSharp.Durable.Tools` (the job-object process lifetime and spawn preflight).
- `PiSharp.Cli` references the durable projects and ships the SQLite license notices.
- `PiLockfile` keeps proper-lockfile's `lock()`, `updateLock` and `unlock` for durable sessions.
- `PiSharp.Codemode` has the additive `Start()` surface.

**Open:**
- **JSONL storage keeps the records it wrote in memory.** After the inline nested-images benchmark, PiSharp holds about 400 MiB against Node's 1 MiB, and 803 MiB against 202 MiB after reopen. SQLite keeps nothing in either runtime. Recorded in `docs/durable-benchmarks.md`, not yet addressed.
- **Main's `tool_search` U+0130 lowercasing** is fixed by [PR #19](https://github.com/jkelly/PiSharp/pull/19) on `main`, which is open and not merged. This branch's `Search` already matches upstream (see `tool_search` above).
- **The durable TUI's edit cards.** They use `BuiltInToolRenderers.CreateAllToolRenderers()`, while interactive mode resolves edit's own shell, so the cards differ slightly. Not in the update's scope.
- **`Fixtures/v1.2.0/index.json`** still describes its fixtures as unread by any suite. It is written only by `tools/DurableFixtures/capture-all.mjs`, and every suite now reads the v1.2.0 set where one exists.
