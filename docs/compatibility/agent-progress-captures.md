# Whole Agent progress and tool-result reference capture

This is a prepared, source-backed offline capture of the public `Agent` lifecycle at Pi v0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`. Root owns the first `--capture-new` execution and the resulting immutable golden. The author has completed syntax, pure harness checks and read-only source/dependency verification; this document does not claim that the new whole-Agent scenarios have executed yet.

The harness imports the whole unchanged `packages/agent/src/agent.ts` and its ordinary imports. It calls the public constructor, `prompt`, `subscribe`, `abort`, `waitForIdle`, `state` and `signal`. It uses the real unchanged agent-loop tool preparation, update acceptance and finalization logic. It neither extracts private functions nor supplies replacement runtime modules. The model, one fake provider stream per case, tools, hook returns and promise gates are authored inputs. These inputs are separate from the future observed golden.

The selected implementation owner remains gpt-6.1-sol at xhigh. No additional dependencies, installation, build, provider request, network access, source write or credential setup occurs in this slice.

## Corpus and gates

`tools/PiReferenceRunner/agent-progress-inputs.json` contains three cases. The harness retains every public Agent event, all authored provider-frame emission snapshots, both subscriber traces, complete hook inputs/returns, tool callback/results/errors, barrier observations and final public state. It does not filter source events to fit a native profile.

| Case | Authored controls | Observations requested |
| --- | --- | --- |
| `parallel-progress-hooks-and-structured-content` | Two tools in parallel. Alpha calls its void update callback twice, returns its result and blocks its first subscriber invocation. Alpha's second update reaches the second subscriber; beta is then allowed to update and finish before alpha's first subscriber is released. | Listener overlap; synchronous callback return; outstanding accepted updates delaying alpha's hook/end; beta finalizing independently; source call-order tool messages; complete raw and replaced metadata; structured content on execution results and its omission from stored messages. |
| `sequential-absent-null-fields` | Two sequential tools return explicit null fields and absent fields respectively. Hook return null fields and an absent hook return are both recorded. | Null/missing distinctions for content, details, usage, structuredContent and terminate; source message content normalization; own undefined message properties; unchanged source behavior for deliberately untyped JS results. |
| `abort-during-awaited-update-and-late-callback` | A tool sends two updates while its first subscriber is blocked. The harness calls public `Agent.abort`; the tool observes its actual AbortSignal, sends another synchronous update, and throws an authored error. The first subscriber is then released. | Signal propagation into actual work; source acceptance of the callback after signal abort before execution settles; error result creation; accepted-update ownership through finalization; late callback behavior. |

Each case also blocks the first `agent_end` subscriber. The harness records `isStreaming`, pending-call Set values, signal state and resolution flags for public `prompt`/`waitForIdle` at that barrier, then releases it and records final state. Saved callbacks are invoked after their tool execution ends while the Agent is still inside the `agent_end` listener, and again after idle. The callback return and immediate event counts are observed; these probes do not add synthetic lifecycle events.

All gates are ordinary authored Promises. There are no sleeps, private scheduler replacements, source patches or assumed timing thresholds. In the parallel case, a later update reaching the second listener supplies the blocked-phase checkpoint; in the abort case, the tool's actual signal event and execute-exit gate supply it. Entry/exit traces and callback counts remain in the output so the checkpoint evidence is reviewable.

## Source semantics being characterized

The pinned source types declare `AgentToolUpdateCallback` as returning `void`. `executePreparedToolCall` invokes the supplied event sink immediately, collects every accepted update promise and awaits them all after execution returns or throws. It does not serialize simultaneous update emissions. It disables acceptance after execution settles, so later callbacks return without an event. A source signal abort is not itself an update-rejection check inside that callback.

`afterToolCall` uses nullish replacement for content, details, usage and terminate. Its separate event `isError` can therefore differ from an unchanged `result.isError` field. Structured replacement uses:

```ts
const structuredContent =
  afterResult.structuredContent ?? (afterResult.content ? undefined : result.structuredContent);
```

A content replacement without a non-null structured replacement removes the result's structured content. A null structured replacement with no content replacement falls back to the original structured value. The capture records the complete before/after hook boundary and final execution-end event; it does not author an expected transformed result.

`createToolResultMessage` copies content with `?? []`, details and usage into the transcript and sets its separate isError and fixed-clock timestamp. It does not copy structuredContent, terminate or arbitrary result fields. The unchanged execution-end event still exposes the result object. The sequential case intentionally exercises JavaScript tools returning null or missing content, beyond the static `AgentToolResult.content` declaration. Those observations must not be presented as statically valid typed tool return examples.

The `Agent` updates its public state before awaiting subscribers. It clears runtime-owned streaming/pending state and resolves its active idle promise after awaited `agent_end` listeners finish. Both entry snapshots and post-idle snapshots are retained. The source branch under test has one authored provider request because the supported `finishTurn` hook explicitly returns `{ action: "end" }`; this is a disclosed control, not an observed provider decision.

## Observation and numeric boundaries

Each source value is captured as its actual `JSON.stringify` result parsed back into a JSON value. The harness also records paths of own enumerable undefined fields, callable fields and Sets. Undefined top-level values have no `json` member; explicit null has `json: null`. Undefined array elements remain JSON null in the JSON snapshot, with their original paths recorded separately. Functions are omitted by actual JSON serialization and identified in the callable inventory. A Set retains its actual serialized `{}` while its iteration values are separately recorded. Tool functions in full hook contexts and public state are therefore visible as inventories rather than silently removed by a custom context projection.

This captures the source's JSON-observable fields and these explicit diagnostics. It does not claim equality of JavaScript function identity, prototypes, hidden/internal properties, symbol keys or JS own-undefined identity in a future CLR object. Missing and null JSON properties are distinct throughout.

Inputs use finite JS-representable numeric values. The existing raw comparator rejects duplicate object keys, retains raw numeric lexemes and ignores only object property ordering. Arrays, strings, null, missing properties and opaque nested values remain significant. The supported-input parser rejects unsupported precision and raw negative-zero lexemes rather than silently rounding them. There is no arbitrary-precision JS number, numeric cost-calculation or numeric normalization claim. Two fresh child captures must be byte identical before any golden is created; comparator property-order tolerance cannot make nondeterministic captures pass this prerequisite.

The only global runtime alteration is an explicitly disclosed child-local `Date.now = () => 1700000000000`, restored in `finally`. It supplies deterministic source-created system/tool-result timestamps. Explicit prompt and assistant timestamps are authored input. Source, loader and dependency bytes remain unchanged; this is not a claim of an unmodified runtime environment.

## Exact oracle and immutable verification

The approved oracle is `P:/PiSharp/root/Documents/Codex/2026-09-30/task-2/Pi-reference-oracle-v0.99.1`. The runner accepts that exact root, including explicit `--oracle` forwarding from an unrelated detached implementation checkout. It does not infer a sibling from the implementation checkout, create an oracle or restore packages.

The prepared plan pins:

- Node v24.19.0 at the approved absolute executable; SHA-256 `3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237`.
- All 2093 canonical Git files: fingerprint `2d65bfaee0e2556cb82ae7e0425de68560ecc3be4451f69ceff3bcc9c6144aa3`. The unchanged acquired checkout fingerprint is `676a5df7a526ab0fb6a78f734b57093bb7571913fc17251d02cb0c84527dcf21`; only `pi-test.bat` and `pi-test.ps1` have precisely verified declared CRLF conversion.
- Exact partial-json0.1.7 and typebox1.3.27 from the pinned public upstream lock. Their cached official tarballs are verified against SHA-512 SRI, inspected with the existing locked bounded archive inspector and compared to every installed byte: 9 + 1385 = 1394 files. Their packaged MIT notice text is retained in the new plan.
- The original minimal-oracle setup plan, historical actual setup receipt, projection manifest/lock, npm archive evidence, empty task-local Git configuration, source resolver, observer, offline guard, comparator and authored input.

The original setup receipt's historical `runtime oracle qualification pending` text is retained as a pinned fact; it is not rewritten to claim this new capture passed. The npm bundle is verified as setup evidence and is not invoked during capture. Full project license/redistribution closure remains open.

Before each parent capture and after both children, the runner verifies clean source commit/tree, every canonical file, the exact acquired fingerprint, source pins, all package archive/installed bytes, runtime and input. The actual child observer records the complete loaded source/dependency closure; each loaded upstream file is then compared to its canonical Git blob and each loaded dependency must lie inside the two exact admitted packages. No speculative loaded-file count is recorded in the plan. Missing dependency/import or guard failures abort the capture without a substituted module.

Child environments inherit no API keys, user home, npm configuration, proxy variables or arbitrary PATH. They use a fresh owned home/workspace, UTC, exact Node PATH and the existing unchanged offline guard, which blocks real network and process operations. Only the parent's read-only Git commands and child launch are permitted. Scratch cleanup checks resolved confinement, absence of links/junctions and an exclusive ownership marker before recursive deletion.

The runner exposes a read-only `--check`; first-new capture refuses any existing golden/lock/manifest before child execution, and each artifact uses exclusive creation. If a partial write or concurrent artifact exists, it is preserved for review. Default verification validates input, golden, lock, harness and loaded-source hashes before executing children and refuses drift. It performs two fresh captures, checks byte identity, revalidates source/dependencies and reports comparison with the immutable golden. No regeneration option exists.

## Root commands and deliverables

From any checkout containing these five new files, use the pinned Node executable. The read-only check is:

```powershell
& 'P:/PiSharp/root/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe' tools/PiReferenceRunner/run-agent-progress.mjs --check --oracle 'P:/PiSharp/root/Documents/Codex/2026-09-30/task-2/Pi-reference-oracle-v0.99.1'
```

Root alone creates the first actual observations after review:

```powershell
& 'P:/PiSharp/root/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe' tools/PiReferenceRunner/run-agent-progress.mjs --capture-new --oracle 'P:/PiSharp/root/Documents/Codex/2026-09-30/task-2/Pi-reference-oracle-v0.99.1'
```

Subsequent reproduction omits `--capture-new`:

```powershell
& 'P:/PiSharp/root/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe' tools/PiReferenceRunner/run-agent-progress.mjs --oracle 'P:/PiSharp/root/Documents/Codex/2026-09-30/task-2/Pi-reference-oracle-v0.99.1'
```

Initial capture will exclusively create `fixtures/pi-v0.99.1/agent-progress/core.expected.json`, `oracle.lock.json` and `manifest.json`. The lock records actual loaded closure and harness/source/runtime/dependency identities; the manifest links input/golden/lock SHA-256, requirements, clock, seed, comparator and provenance. The actual output and report go to `artifacts/agent-progress-reference/core.actual.json` and `report.json`.

At author handoff both mjs syntax checks passed, 35 pure checks passed, and the read-only `--check` verified all 2093 source files plus 1394 installed files against their SRI-verified archives. No Agent import/execution, capture child or new golden was performed by the author. Runtime import/closure, repeatability, actual semantics and independent qualification remain for root's capture and review.

Native progress serialization/awaiting and native rejection of late callbacks differ from this source path and remain explicit unresolved profiles. This slice does not qualify production providers, native parity, durable sessions/RPC, file-tool effects, cross-platform scheduling or full P1/P3 closure. Every source event remains available for those comparisons rather than being excluded from the golden.
