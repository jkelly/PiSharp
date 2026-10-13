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
7. **Intentional difference: the `pisharp durable` entry point.** Upstream has no CLI entry point for the durable agent; it runs `experimental/durable/main.ts` from source. PiSharp runs it as the hidden subcommand `pisharp durable [--continue|-c]`, dispatched in `PiCommand.RunCoreAsync` before `mcp`, left out of `--help`, and printing a one-line experimental notice to stderr. Any other argument fails with upstream's `Unknown argument: X`.

## Consequences

- The plan has 14 phases (P0–P13) with milestones: M0 records and storage, M1 a durable chat turn, M2 tool turns with kill-and-resume, M3 an interactive `pisharp durable`. Each milestone is reported to the owner before the next phase starts.
- Tests are ported from upstream `packages/durable/test` into `tests/PiSharp.Durable.Tests`. The storage conformance suite runs against every backend, directly and after reopen. Branch CI runs on Windows, Linux and macOS arm64.
- The branch merges `main` regularly and regenerates integrity pins and lock files after each merge. Main-line projects change only through additive seams, which are flagged for separate upstreaming to `main`.

## Model adapter

`NativeDurableModels` in `src/PiSharp.Durable/Models` is the production `IDurableModels` (pi-ai `createModels()` with the built-in providers). It looks models up in the pi-ai 1.1.0 catalog shards, which it embeds by link from `src/PiSharp.Cli/Models` and verifies by hash, or in host catalogs, and streams them over PiSharp.AI's native transports: `openai-completions`, `openai-responses`, `anthropic-messages`, `google-generative-ai`, `mistral-conversations` and `pi-messages`. API keys come from the host's `ResolveApiKey` first, then the provider's environment variables (`ProviderEnvironmentKeys`), read through the host's `Environment` reader because the portable core never reads the process environment. Anthropic uses only `ANTHROPIC_API_KEY`. Providers whose auth resolves per request, and Anthropic OAuth or auth tokens, need the CLI adapter (P12): github-copilot, openai-codex, amazon-bedrock, google-vertex, Cloudflare and azure. `TimeoutMs` is enforced by the adapter as an idle deadline. `MaxRetries` and `MaxRetryDelayMs` reach only google-generative-ai, and durable generation owns retries. `Transport`, metadata other than `user_id`, `Deferred` and pi-ai's `clampMaxTokensToContext` are not mapped. No pi-ai 1.1.0 provider implements deferred responses. The CLI adapter (P12) is `src/PiSharp.Cli/Durable/DurableModels.cs`: models come from PiSharp.Cli's `ModelRegistry` (built-in catalogs, models.json, persisted catalogs), and `NativeDurableModelsOptions.HostTransport` lets it stream each request through the CLI's live route of the model (`LazyModelTransport`), so auth.json credentials with OAuth refresh, models.json keys and headers, GitHub Copilot token exchange, OpenAI Codex OAuth, Amazon Bedrock and Google Vertex ambient credentials, the Cloudflare providers and Azure endpoint configuration all work in `pisharp durable`; a model without a live route falls back to the native routes with the registry's key through `ResolveApiKey`. `samples/Durable.Chat` is the M1 demo (upstream `test/examples/16-real-model.ts`). Neither file is an upstream port, so neither has a port-map row.

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
- These have no rows: the `vacation` demo, `pi-env`, and Chord's facets, services and wire. Instead:
  - `test/chord-guide.test.ts`'s canvas and diff-review runs use direct subscriptions to the same document states.
  - Its job-output run is upstream's, on the task runtime.

**Skipped:**
- `test/provider-session-cache-e2e.test.ts` and `test/system-order-cache-e2e.test.ts` need live provider credentials.
- `test/fixtures/delete-buffer.ts` and `test/fixtures/utf8-byte-length-without-buffer.ts` check behavior without Node's global `Buffer`. .NET has no such global; `TruncateCases` checks `Truncation.Utf8ByteLength` against .NET's UTF-8 encoding instead.

**Intentional mappings recorded in the map:**
- **Erased type parameters.** TypeScript's phantom type parameters have no C# counterpart: `TaskId<Result>`, typed task input and checkpoints, `SettledTask<Result>`, and a document's value type. Tasks are JSON-typed, as documents are. Exhaustive phase maps and typed task input are checked at run time, not compile time. `types.test.ts`'s Harness typing test is a compile-checked case of the C# surface.
- **`16-real-model`** runs only with `OPENAI_API_KEY`. The tests run it without the key and pin upstream's skip line.
- **`19-json`'s first streamed partial** varies between Node runs, with pi-ai's random faux token size. PiSharp's run is fixed and equals captured Node runs. They compare as JSON with sorted keys, with clock and random values as placeholders.
- **Examples 19, 21, 23 and 24** run on a manual example clock that is driven like Node's event loop.

**Benchmarks** (`benchmarks/PiSharp.Durable.Benchmarks`) are report only; see `docs/durable-benchmarks.md`.
- On the one Windows machine measured, neither runtime meets upstream's 0.4 s target for `cat` of 1 GiB:
  - upstream under Node: about 1.75 s;
  - PiSharp: 2.25 to 2.57 s.
- PiSharp's `api.output` firehose is about 3 times slower than Node's.
- Its on-disk footprint for the seeded benchmark data matches upstream's byte for byte.
