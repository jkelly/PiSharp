# MAP — current compatibility and implementation mapping

Status: completed metadata reconciliation and offline structure/source checks. This report does not certify the product's runtime behavior. I13/root retain integrated execution ownership.

Scope: `E:/AI/PiSharp/gap-implementation`, baseline `14dd34b369fba6d236684da6dfc8a39a03263321`; frozen release source `f9a26365568127461c47a07a37dcb42c140a3ac5` at `E:/AI/PiSharp/upstream-v150`; candidate `7d073328e44337d3292a7db9b0f0308f679c5e97`. Source, test paths and current worker reports were reconciled. Product source and .NET builds were not changed or executed.

Requested model/reasoning: gpt-6.1-sol / xhigh; requested Ultrafast service tier, effective tier unverified because the API exposes no tier control. No child agents, credentials, provider calls, dependency installation, commit, publication or deployment.

## Current layer and preserved evidence

Added `compatibility/gap-status-2026-11-08.json` as the current source mapping:

- All 23 C findings, nine F capabilities and five V verification work packages have source/test/report anchors: 37 entries.
- All 19 historical `ported-with-deferral` rows and 37 historical `port` rows have a current disposition: 56 entries.
- Twenty Durable port-map files have explicit current source paths: the nine previously planned/in-progress/needs-sync files, formerly excluded legacy host types, and the files affected by new Durable policy/lookup/stream/SQLite/reference work.
- The renamed current Node runtime test is recorded separately from its original release evidence. Pi 1.5.0/undici 8.12.3 current source does not erase the historical Pi 1.2.0 safe-default decision.
- Five experimental library packages are admission-prepared, as confirmed with PKG. `PiSharp.Env.Daemon` remains explicitly nonpackable as a library. Package-only consumers and native artifact qualification remain pending; `published` is false.

`currentImplementation` pointers on the applicability and Durable file rows identify the current source status and explicitly pending integrated runtime status. Existing `pisharp` locations now identify real source files rather than `(new)` placeholders; `historicalPisharp` preserves the original locations. Old `status`, `notes`, `deferred`, evidence/commit/test records, release targets and every upstream blob pin remain historical and unchanged. The top-level scope note makes this distinction explicit.

The legacy `compatibility/implementation-status.json` receives only a pointer explaining that its preceding milestones are historical. Its original contents reconstruct unchanged after removing that pointer.

The mapping's canonical SHA-256 checks reconstruct all three original JSON documents from the exact baseline. They remove only current metadata and restore `historicalPisharp`; all original statuses, notes, evidence, paths, summary counts and source pins must then equal the baseline. Thus the current layer cannot silently relabel authored tests as historical passing evidence.

## Reproducibility and CI

`tools/CompatibilityReport/gap-status-mapping.mjs` is the maintained current mapping and writer. `--write` refreshes the dated artifact and current pointers. Without it, the script checks that outputs are current. It verifies all source/test/report paths exist and refuses historical-evidence changes.

`tools/DurablePortMap/build-port-map.mjs` now preserves current annotations and corrected paths, including for historically `planned` entries, when rebuilding against the frozen v1.5.0 objects. It does not change baseline status semantics or repin upstream.

`tools/CompatibilityReport/gap-status.test.mjs` checks complete audit/register and baseline-row coverage, concrete paths, current pointers, pending runtime state and retained platform boundaries. The existing `sync-applicability.test.mjs` also validates the current mapping, so the existing core applicability CI gate includes it without a workflow edit. The one replaced historical Node test reference is checked against its explicitly recorded current test name, while the old evidence string remains intact.

## Validation executed

- `node --test tools/CompatibilityReport/gap-status.test.mjs`: **2/2 passed**.
- Required-source `node --test tools/CompatibilityReport/sync-applicability.test.mjs`, with `PI_REQUIRE_UPSTREAM=1` and `PI_UPSTREAM_V1_5_0=E:/AI/PiSharp/upstream-v150`: **5/5 passed, zero skips**.
- `node tools/DurablePortMap/check-worklist.mjs --upstream E:/AI/PiSharp/upstream-v150`: passed; 48 areas, 27 waves, 82 phase-4 rows; exact coverage of 231 Durable and 651 Chord files, including the existing excluded dispositions.
- `node tools/DurablePortMap/build-port-map.mjs --upstream E:/AI/PiSharp/upstream-v150` and subsequent `--check`: passed, including the final check after all current-path additions; 289 baseline file rows, with original status counts retained. The console labels those as historical release statuses when the current mapping is present.
- Independent comparison to `git show 14dd34b3:<path>` confirmed unchanged historical projections for both applicability and Durable maps. The committed canonical checks additionally cover the old implementation-status document.
- Scoped `git diff --check`: passed.

The first offline applicability run surfaced the stale Pi 1.2.0 test-name reference; it was corrected through the current-only replacement mapping, not by deleting historical evidence. Git initially rejected the read-only upstream checkout's ownership. Checks used an exact process-local `safe.directory` entry; no global configuration was changed.

## Remaining boundaries

Source mappings are not proof that all .NET suites compile or pass. Preliminary worker receipts remain useful observations, but the current artifact deliberately leaves every integrated runtime status pending. It does not copy intermediate passing counts into completion flags.

Retained exclusions/adaptations are explicit: Cloudflare/IndexedDB; native fail-stop instead of uncertain-write rollback; CLR drafts/immutable copies/scheduling; native pipe/module/lock contracts distinct from Node IPC/TypeScript; Unicode runtime differences; physical macOS modifier input; unsupported Windows PTY; and real SSH, browser/Node, Unix process/PTY, NativeAOT/Android/six-target execution. The bounded new v1.5.0 capture corpus does not imply full C# scenario replay, complete provider recovery coverage or universal parity. SQL performance remains unqualified until actual comparative measurements pass.

## Files changed

- `compatibility/gap-status-2026-11-08.json` (new)
- `compatibility/sync-1.5.0-applicability.json`
- `compatibility/durable-port-map.json`
- `compatibility/implementation-status.json` (current pointer only)
- `tools/CompatibilityReport/gap-status-mapping.mjs` (new)
- `tools/CompatibilityReport/gap-status.test.mjs` (new)
- `tools/CompatibilityReport/sync-applicability.test.mjs` (preserved I13's existing changes)
- `tools/DurablePortMap/build-port-map.mjs`
- `AI_DOCS/agent-reports/MAP.md` (new)

No edits to historical release decisions, captures, target locks, worklist contents, product source or another agent's report. Existing `check-worklist.mjs` changes belong to I13 and were preserved without edits.
