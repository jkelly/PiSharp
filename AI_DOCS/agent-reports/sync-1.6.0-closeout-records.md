# closeout-records: the 1.6.0 compatibility records and their tooling

Branch `sync/1.6.0-closeout-records`, from `ba2fb1ae` (the durable-edge merge). Scope: `compatibility/**` and `tools/CompatibilityReport/**`. No product source, documentation, website, integrity table or preserved 1.5.x record was edited. Nothing was pushed, tagged or published. No ledger, coverage snapshot or acceptance record was written: `compatibility/sync-1.6.0-acceptance.json` does not exist.

## 1. Commits

| commit | what |
| --- | --- |
| `b2edf41c` | Map the renamed version-4 tool-task case in the v1.5.0 table |
| `d57ffbad` | Record the 1.6.0 package reports' dispositions in the applicability table |
| `9e946d6f` | Add the 1.6.0 acceptance tool, its policy and the ledger collector |
| `2adc56f8` | Record the unreleased 1.6.0 candidate in implementation-status.json |
| `060cd6bb` | Let root waive the 1.6.0 full-solution build with a recorded reason |
| (this report) | Add the closeout-records report |

## 2. What changed

### 2.1 The failing v1.5.0 table test (`b2edf41c`)

`sync-applicability.test.mjs` failed because row `durable.unoffered-tool-calls` named a case that had been renamed from `...-under-version-3-...` to `...-under-version-4-...`.

- The fix goes through `gap-status-mapping.mjs` `additionalRows`. The row now carries `currentImplementation.testReplacements`. The tool's `--write` regenerated `compatibility/sync-1.5.0-applicability.json` and `compatibility/gap-status-2026-11-08.json`.
- `summary.additionalRows` (now 2) and the source-status counts are computed from the counted rows, and the validator asserts both.
- I scanned the 1.5.0 table's 1,426 test references, and that row was the only stale one.
- I also checked the 195 expected ids in the 1.5.2 coverage. Every one is still present, some only as composed or interpolated strings.
- The 1.5.2 table has no per-row test references.

### 2.2 1.6.0 dispositions (`d57ffbad`)

The reviewed findings live in `tools/CompatibilityReport/sync-1.6.0-dispositions.mjs`: 516 path entries read from the package reports and checked against the tree. `sync-1.6.0-applicability.mjs` consumes that file, and the JSON is generator output only.

- **Chord packages.** `chord` is replaced by chord-foundation, chord-edge, chord-store, chord-remote, chord-queries, chord-admission and chord-workers. Their plan is the chord-discovery report. The generator throws if any `packages/chord` path has no reviewed chord-* package.
- **Reviewed no-op categories.** Four categories sit beside the mechanical 1.5.2 ones: `typescript-only`, `node-runtime-only`, `no-native-delta` and `version-only`. A reviewed finding other than no-op keeps a row applicable even where the path rule would call it a no-op.
- **The `review` object.** Each row gets one, with:
  - `finding`: one of `ported`, `already-ported`, `not-in-report`, `pending-package` or `no-op`;
  - `owners`: the packages that changed the file;
  - `pendingParts`, `deferred`, `evidence` and `note`.

  The review is never a status. Every applicable row stays `implementation-pending-validation`.
- **Meta rows.** All 14 are recorded:
  - ported: `version.ts`, plus the coding-agent, ai, durable and chord `package.json`;
  - version-only: agent, codemode, env, mcp, schema, telemetry and tui `package.json`, and `install-lock/package.json`;
  - build tooling: the root `package.json`.
- **Ownership corrections:**
  - `session-share.ts` moves to cli-session.
  - `interactive-mode.ts` is owned by cli-session and cli-ui.
  - The components tui made (assistant-message, user-message, radius-login-selector, bash-execution, themed-text, visual-truncate, tool-execution) are already-ported, owned by tui and cli-ui. `settings-submenu.ts` is also owned by both.
  - `coding-agent/schemas/models.schema.json` moves to ai-catalog. It is byte-identical to the v1.6.0 blob `83219445`.
  - `ai/test/cache-retention.test.ts` moves to ai-catalog, as `not-in-report`.
  - `ai/test/providers.test.ts` moves to ai-providers.
  - `model-resolver.ts` is owned by ai-catalog and cli-session.
- **The durable-edge `nativeMapping` edit (7 lines) is kept.** The generator now throws if any native-map entry would land on a path that has no overlay row.
- **Stale CodemodeNamespace notes.** These cannot be edited: the map's `files` must deep-equal the preserved `durable-port-map.v1.5.0.json`, which also feeds the gap-status digest. Instead, `currentImplementation.candidate.supersededNotes` names the two historical notes (`codemode/describe.ts`, `codemode/index.ts`) and points at the overlay rows that carry the current location.
- **Deferrals recorded on rows.** Seven rows carry `deferred`, and each was checked in the tree:
  - exec SIGTERM grace: `core/exec.ts` and `test/exec.test.ts`;
  - the explicit-policy find, where `FdFindExecutor.cs:126` still prefixes `**/`: `core/tools/find.ts` and the 12066 regression;
  - `ModelResolver.RestoreModelFromSession`, which still exists;
  - the harness-signals NaN assertion;
  - the live openai-reasoning-replay e2e test.

  Acceptance turns each of these into a row limitation. Retries on four transports predate 1.6.0 and are recorded as a note, not a deferral.

### 2.3 Acceptance tooling (`9e946d6f`, `060cd6bb`)

The 1.5.2 tool and its test are byte-unchanged and still pass: 12 of 12.

- **`tools/CompatibilityReport/accept-sync-1.6.0.mjs`** generalizes the 1.5.2 transition.
  - The paths are in one `SYNC` block. One suite receipt is judged by the 1.5.2 `assessSuite`, imported unchanged.
  - A row's review can block acceptance: any `pendingParts`, or a finding in the policy's `blockingFindings` (`pending-package`, `not-in-report`).
  - A row's `deferred` text becomes a limitation of that row. The row and its package are then `tested-with-limitations`.
  - It refuses to run if the table already has an acceptance or the record file exists.
  - On `--apply` it writes:
    - the table;
    - the overlay;
    - the port map's candidate pointer (status and acceptance only);
    - `pi160Candidate`;
    - `target.lock.json` `status`;
    - the record.
  - It writes no documentation of its own. The policy's `documentationEdits` (`[path, exact text, replacement]`) are the only documentation it touches, and each must match or nothing is written.
  - The 1.5.2 guard is kept: the tool refuses any write that would change a file an accepted receipt fingerprinted.
- **`sync-1.6.0-applicability.mjs --accepted-ledger ...`** forwards to the tool, as the 1.5.2 generator does.
- **`tools/CompatibilityReport/sync-1.6.0-acceptance-policy.json`** maps each of the 17 work packages to the suites its report ran: 84 distinct suites, each checked to exist. It also holds:
  - the 1.6.0 catalog provenance (`1.6.0.provenance.json`, whose manifest digest matches, with 43 providers);
  - the frozen snapshot paths `AI_DOCS/sync-1.6.0-source-validation.json` and `AI_DOCS/sync-1.6.0-source-coverage.json`;
  - the packaging record path;
  - an empty `documentationEdits`.
- **`tools/CompatibilityReport/collect-sync-1.6.0-validation.mjs`** is the Node form of the 1.5.2 `update-validation-report.ps1`, `Audit-Sync152Coverage.ps1` and `Freeze-Sync152SourceValidation.ps1`. Those lived outside the repository in `update-1.5.2/artifacts`.
  - `ledger` reads `artifacts/gap-validation/*/receipt.json`, which `tools/GapValidation/Run-Tests.ps1` writes, and builds the mutable ledger in the 1.5.2 format.
  - `coverage --spec` builds the 1.5.2 coverage format.
  - `freeze` re-verifies every accepted receipt's source and writes the two snapshots once.
  - A full-solution build is either a passing receipt on the current source, or `--waive-solution-build <reason>`. A waiver stays in the ledger and becomes an acceptance limitation. A Run-Tests receipt on `PiSharp.slnx` would fingerprint the whole tree, artifacts included.
- **Formats.** The ledger and coverage are 1.5.2's (`AI_DOCS/sync-1.5.2-source-validation.json`, `-source-coverage.json`). Receipt and manifest paths are stored repository-relative.
- **Coverage spec format:** `{ schemaVersion: 1, groups: [{ group, project, sources?, prefix?, expectedIds?, count }] }`.
  - The expected ids are the ids declared in the case files that match the 1.5.2 audit's pattern and the prefix, plus `expectedIds`.
  - A different `count` stops the audit.
  - No 1.6.0 spec is committed. Static extraction is not reliable for 1.6.0: the nine `tests/PiSharp.Durable.Tests/Sync160*Cases.cs` files declare their ids by interpolation (0 extracted), and some Chord store files yield stray tokens. Root writes the spec, using `expectedIds` where ids are built at run time.
- **`accept-sync-1.6.0.test.mjs`** has 16 tests, all on synthetic repositories under `artifacts/acceptance-160-controls-*`. They cover:
  - the collector: building the ledger and coverage, filtered runs, changed source, the pinned count, the one-time freeze and the waiver;
  - the plan: dry runs, blocked findings, deferred limitations, the exact files written, documentation edits;
  - safety: stale or new source, source changed after the plan, self-invalidation, input hashes, the catalog version;
  - the committed policy and candidate state.

### 2.4 `pi160Candidate` (`2adc56f8`)

`compatibility/implementation-status.json` gains `pi160Candidate`, modelled on `pi152Candidate`:

- the source pin is v1.6.0 at `faac5858`, tree `7b395e94`;
- `released: false`;
- the published version is `1.5.2`;
- it points at the table, the overlay, decision 0015 and `docs/release-notes/1.6.0.md` (closeout-docs writes that file);
- `status: implementation-pending-validation`, `acceptance: null`;
- `runtimeAcceptance` says integrated validation has not run and no row is tested.

`gap-status-mapping.mjs` drops it from the historical digest, beside `pi152Candidate`. The acceptance test checks the candidate against the acceptance state.

## 3. Checks

All Node tests ran one file at a time (`node --test <file>`) with `PI_UPSTREAM_V1_6_0=E:/AI/PiSharp/upstream-v160` and `PI_UPSTREAM_V1_5_2=E:/AI/PiSharp/upstream-v152`.

| check | pass | fail | skip | note |
| --- | ---: | ---: | ---: | --- |
| accept-sync-1.5.2.test | 12 | 0 | 0 | |
| accept-sync-1.6.0.test | 16 | 0 | 0 | 15 in the full run; the waiver test was added after it and rerun |
| comparator.test | 4 | 0 | 0 | |
| gap-status.test | 2 | 0 | 0 | |
| integration.test | 1 | 0 | 0 | |
| sync-1.5.2-applicability.test | 3 | 0 | 0 | |
| sync-1.5.2-source.test | 3 | 0 | 0 | |
| sync-1.6.0-applicability.test | 5 | 0 | 0 | |
| sync-1.6.0-source.test | 3 | 0 | 0 | |
| sync-applicability.test | 4 | 0 | 1 | was failing before `b2edf41c` |
| SurfaceInventory build-public-entrypoints.test | 51 | 0 | 0 | |
| SurfaceInventory inventory-refresh.test | 10 | 0 | 3 | |
| license-evidence.test | 0 | 1 | 0 | known: "Frozen license matrix changed" (unchanged) |
| check.integration.test | 0 | 268 | 0 | not mine, see below |
| agent-progress.integration.test | 1 | 110 | 0 | not mine, see below |
| anthropic-sdk.integration.test | 1 | 71 | 0 | not mine, see below |
| SurfaceInventory native-extension-surface.test | 0 | 1 | 0 | environment: `artifacts/released-baseline/pi-0.99.1-source.tar.gz` is absent in this worktree |

Notes on the failures that are not mine:

- **The three integration tests** copy `compatibility`, `fixtures/pi-v0.99.1`, `tools/CompatibilityReport` and `tools/PiReferenceRunner` into a temporary tree. They do not copy `tools/PublicDerivativeIntegrity.mjs`, which `check.mjs` imports, so every case fails with `ERR_MODULE_NOT_FOUND`.
- None of those files changed in this branch. The import and the copy lists date from the 1.5.2 release commits.

Other checks:

- `verify-baseline.mjs --upstream E:/AI/PiSharp/upstream-v160`: exit 0, 6,353 checks, 3,149 source files, archive verified.
- `sync-1.6.0-applicability.mjs --check`: exit 0.
- `gap-status-mapping.mjs --check`: exit 0.
- `check.mjs`: exit 1 after 92 checks, at the unregistered `fixtures/pi-v0.99.1/session-branches-replayed/manifest.json`. This is the same known stop.
- Durable: the pinned-SDK Release build of `tests/PiSharp.Durable.Tests` succeeded with 0 warnings and 0 errors. `--only setup.` ran 4 cases: 4 pass, 0 fail, 0 skip.
- The integrity table (`node tools/refresh-derivative-integrity.mjs --check`, read-only) is stale on 51 rows. 50 are product files from the merged packages; one is `compatibility/implementation-status.json` (`pi160Candidate`). The re-seal is root's (section 6).

## 4. Table totals

The table has 1,311 files: 914 `no-op` and 397 `implementation-pending-validation`. No row is tested.

Pending rows by finding:

| finding | rows |
| --- | ---: |
| ported | 363 |
| already-ported | 17 |
| pending-package | 15 |
| not-in-report | 2 |

23 rows have `pendingParts`.

No-op rows by category:

| category | rows |
| --- | ---: |
| upstream-app | 746 |
| upstream-documentation | 52 |
| upstream-build-tooling | 38 |
| example-or-fixture | 35 |
| typescript-only | 15 |
| version-only | 8 |
| node-runtime-only | 8 |
| browser-dom-react | 6 |
| no-native-delta | 5 |
| evals-version-only | 1 |

By work package:

| work package | pending | no-op |
| --- | ---: | ---: |
| meta | 5 | 782 |
| core-small | 11 | 5 |
| ai-providers | 25 | 4 |
| ai-catalog | 34 | 4 |
| tui | 22 | 2 |
| cli-tools | 15 | 0 |
| cli-session | 34 | 21 |
| cli-ui | 29 | 2 |
| durable-core | 32 | 4 |
| durable-edge | 25 | 2 |
| chord-foundation | 28 | 50 |
| chord-edge | 36 | 28 |
| chord-store | 28 | 2 |
| chord-remote | 24 | 3 |
| chord-queries | 11 | 0 |
| chord-admission | 25 | 2 |
| chord-workers | 13 | 3 |

## 5. What stays pending for chord-admission and chord-workers

Neither package had merged at `ba2fb1ae`. While these entries stand, acceptance is blocked.

### chord-admission

25 applicable rows. 6 are `pending-package`:

- `src/host/admission.ts`
- `src/host/facet.ts`
- `src/host/route-admission.ts`
- `test/host/admission.test.ts`
- `test/host/dependencies.test.ts`
- `test/host/route-admission.test.ts`

19 rows owned by other packages are complete except for an admission part (`pendingParts: ["chord-admission"]`):

- `src/host/`: `attempt`, `dependencies`, `host`, `object`, `reference-source`, `reference`, `route`, `staging`
- `src/remote/`: `consumer`, `hold`, `path`, `provider`, `relay`, `remote-object`
- `src/telemetry.ts`
- tests: `test/host/reference-source.test.ts`, `test/remote/path.test.ts`, `test/remote/relay.test.ts`, `test/support.ts`

### chord-workers

13 applicable rows. 9 are `pending-package`:

- `src/node/workers/process.ts`
- `src/node/workers/workers.ts`
- `test/node/workers/lock.test.ts`
- `test/node/workers/process.test.ts`
- `test/node/workers/workers.test.ts`
- `test/node/workers/support.ts`
- `test/node/fixtures/workers/child.ts`
- `test/node/fixtures/workers/control.ts`
- `test/node/fixtures/workers/worker.ts`

The fixtures are applicable here rather than mechanical example-or-fixture no-ops, because the worker tests run them.

4 rows have a workers part:

- `src/node/app/app.ts`
- `src/tui/scope.ts`
- `test/surfaces/variant.test.ts`
- `test/tui/scope.test.ts`

### Not in any report

Two rows have the finding `not-in-report`, and they also block:

- `packages/ai/test/cache-retention.test.ts` (ai-catalog)
- `packages/chord/test/node/app/support.ts`

## 6. What root runs, in order

All commands run from the repository root after every package branch (including chord-admission, chord-workers, closeout-docs and this one) is merged.

1. **Settle the table.**
   1. Update `tools/CompatibilityReport/sync-1.6.0-dispositions.mjs` from the chord-admission and chord-workers reports:
      - give the 15 `pending-package` rows their finding;
      - drop `pendingParts` from the 23 rows;
      - decide the 2 `not-in-report` rows as a reviewed finding or a reviewed no-op with category and reason.
   2. Then run:
      ```
      node tools/CompatibilityReport/sync-1.6.0-applicability.mjs
      node tools/CompatibilityReport/sync-1.6.0-applicability.mjs --check
      node --test tools/CompatibilityReport/sync-1.6.0-applicability.test.mjs
      ```
2. **Citations.** These change source headers, so they come before any validation run.
   1. Delete the report-only file first. Otherwise the tool adds its `repinned` entries to the new run's, and the counts double:
      ```
      git rm compatibility/sync-1.6.0-citations.json
      ```
   2. Dry run. The `--worklist` is written only with `--write`:
      ```
      node tools/CompatibilityReport/repin-sources.mjs --upstream E:/AI/PiSharp/upstream-v160 --from v1.5.2 --to v1.6.0 \
        --worklist compatibility/sync-1.6.0-citations.json --applicability compatibility/sync-1.6.0-applicability.json \
        --decision docs/decisions/0015-pi-1.6.0-sync.md --report artifacts/sync-1.6.0-citations-dry.json
      ```
   3. Apply:
      ```
      node tools/CompatibilityReport/repin-sources.mjs --upstream E:/AI/PiSharp/upstream-v160 --from v1.5.2 --to v1.6.0 \
        --worklist compatibility/sync-1.6.0-citations.json --applicability compatibility/sync-1.6.0-applicability.json \
        --decision docs/decisions/0015-pi-1.6.0-sync.md --write
      ```

   Notes:
   - `upstream-v160` has both tags (`v1.5.2` is `b4afdddf`, `v1.6.0` is `faac5858`).
   - `--applicability` should name the 1.6.0 table so pending entries map to `sync160.*` rows. The meta report's command left it at the default, the 1.5.0 table.
   - I ran the dry run into the scratchpad, with a nonexistent work list and no `--write`; the tree was unchanged. On this branch it reports:
     - 216 files still naming `b4afdddf`;
     - 86 citations that can move mechanically;
     - 39 pending (chord 26, coding-agent 8, ai 4, tui 1);
     - 12 unparsed;
     - 8 non-header pins.
   - `tools/Env/upstream-android-1.5.2.json` is one of the non-header pins, and it is never rewritten.
3. **Android daemon pin.** Optional; nothing was fetched. See section 7.
4. **First re-seal**, covering every source and documentation edit so far:
   ```
   node tools/refresh-derivative-integrity.mjs
   node tools/refresh-derivative-integrity.mjs --check
   ```
   Before re-sealing, make sure restore has not turned lock files into CRLF.
5. **Validation runs.** Use `tools/GapValidation/Run-Tests.ps1`, which needs PowerShell 7 and Windows job objects; it is not installed here. Run each of the 84 policy suites as a full suite, with no `--only`, no `--filter` and no `*_FILTER` variable, for example:
   ```
   pwsh tools/GapValidation/Run-Tests.ps1 -Project tests/PiSharp.Chord.Tests/PiSharp.Chord.Tests.csproj -Configuration Release \
     -Label sync160-chord-final -BuildArguments '-p:RestoreSources=https://api.nuget.org/v3/index.json'
   ```
   `Run-Queue.ps1` takes a queue of these, in the form of the 1.5.2 `params-validation-sync152-*-final.json` files. The latest receipt per project is the one that counts, so a later filtered or build-only run of a project hides its full run.

   To list the suites:
   ```
   node -e "const p=require('./tools/CompatibilityReport/sync-1.6.0-acceptance-policy.json');console.log([...new Set(Object.values(p.workPackages).flat())].join('\n'))"
   ```
6. **Ledger, coverage and freeze:**
   ```
   node tools/CompatibilityReport/collect-sync-1.6.0-validation.mjs ledger --verify-current-sources [--solution-receipt <receipt> | --waive-solution-build "<reason>"]
   node tools/CompatibilityReport/collect-sync-1.6.0-validation.mjs coverage --spec <root's coverage spec>
   node tools/CompatibilityReport/collect-sync-1.6.0-validation.mjs freeze
   ```
   - `ledger` writes `AI_DOCS/sync-1.6.0-validation.json` and prints the pending suites. Repeat it until none remain.
   - `coverage` writes `AI_DOCS/sync-1.6.0-coverage.json`. It needs a spec root writes (section 2.3).
   - `freeze` writes `AI_DOCS/sync-1.6.0-source-validation.json` and `AI_DOCS/sync-1.6.0-source-coverage.json` once, and prints their SHA-256.
7. **Acceptance.**
   1. Review first:
      ```
      node tools/CompatibilityReport/accept-sync-1.6.0.mjs --accepted-ledger AI_DOCS/sync-1.6.0-source-validation.json --ledger-sha256 <sha> \
        --coverage AI_DOCS/sync-1.6.0-source-coverage.json --coverage-sha256 <sha> --out artifacts/sync-1.6.0-acceptance-plan.json
      ```
   2. Then rerun the same command with `--apply`.
   3. If the status wording in README, the website or the docs should change, add exact `[path, from, to]` entries to `documentationEdits` in the policy before step 5. The policy file is under `tools/CompatibilityReport` and is not in any suite's fingerprint, but a documentation file a suite reads would be.
   4. The Durable setup cases read the port map and overlay at run time. Acceptance changes only their status, acceptance and validation fields, which those cases do not read.
8. **Final re-seal.** Acceptance rewrites `compatibility/implementation-status.json`, which the integrity table seals:
   ```
   node tools/refresh-derivative-integrity.mjs
   node tools/refresh-derivative-integrity.mjs --check
   node --test tools/CompatibilityReport/accept-sync-1.6.0.test.mjs tools/CompatibilityReport/sync-1.6.0-applicability.test.mjs tools/CompatibilityReport/gap-status.test.mjs
   ```
   No suite links the integrity files, so this does not invalidate the accepted receipts.

### What the packaging gate requires

I read `Get-PiSharpDistributionSourcePin` in `tools/packaging/distribution-validation.ps1` but did not run it. For a package version other than the published `compatibility/public-release.json` version (`1.5.2`), it requires all of the following:

- The version's `X.Y.Z` base is `1.6.0`: `v` + base must equal the `target.lock.json` tag. A 4th segment and a prerelease suffix are allowed.
- `target.lock.json` and every lock reached through `previousTarget`/`previousBaseline`:
  - are well-formed, with tag, 40-hex commit and tree, and `tagResolvedCommit` equal to the commit;
  - match the link that points to them;
  - lead to a lock equal to the published `upstream` tag and commit.
- `compatibility/sync-1.6.0-applicability.json` has an `acceptance.record`.
- That record's `source`, and the table's `to`, equal the target's tag, commit and tree.
- The record has `eligible: true`, `status` `tested` or `tested-with-limitations`, a non-empty `appliedAt` and no `problems`.
- Every table row is `tested`, `tested-with-limitations` or `no-op`.
- The record's `ledger` and `coverage` pins equal the table's acceptance pins, and the files on disk hash to them.
- The ledger has `sourceValidationOnly: true` and no `pendingSuites` or `additionalPending`.
- The coverage has `complete: true`.

The record `accept-sync-1.6.0.mjs` writes satisfies each of these fields. The gate's own fixture in `tests/packaging/distribution-validation.tests.ps1` still uses the 1.5.2 names.

## 7. Android daemon pin for 1.6.0

`tools/Env/upstream-android-1.5.2.json` pins the pi-env 1.5.2 Android executables:

- arm64: `package/bin/pi-env-android-arm64/pi-env`, SHA-256 `1a91f482...`;
- x64: `package/bin/pi-env-android-x64/pi-env`, SHA-256 `d446b5d2...`;
- source commit `b4afdddf`.

`tools/Env/Get-UpstreamAndroidDaemon.ps1` downloads `https://registry.npmjs.org/@earendil-works/pi-env/-/pi-env-1.5.2.tgz`, checks a hard-coded npm SHA-512, extracts only those two ordinary-file entries, and checks their SHA-256 and ELF headers.

The daemon's Rust sources changed in 1.6.0 (`exec.rs`, `output.rs`, `process.rs`, `pty.rs`), and upstream `packages/env/package.json` is `1.6.0` and ships `bin`. A 1.6.0 pin would need four things.

1. **What to fetch, and from where.** Fetch `https://registry.npmjs.org/@earendil-works/pi-env/-/pi-env-1.6.0.tgz`. Take its expected integrity from the registry metadata `https://registry.npmjs.org/@earendil-works/pi-env/1.6.0`, field `dist.integrity`. The repository has no copy of the 1.6.0 SRI: no lock file names pi-env 1.6.0. I fetched nothing, so I cannot confirm that the 1.6.0 archive exists or contains Android binaries.
2. **Verification, as the 1.5.2 script does it, before anything is copied:**
   - the archive's SHA-512 equals that SRI;
   - `tar -tf` lists each of the two fixed paths exactly once, and `tar -tvf` shows each as an ordinary file;
   - only those two entries are extracted;
   - each is ELF64 little-endian (`7F 45 4C 46 02 01`) with `e_machine` 183 for arm64 and 62 for x64;
   - nothing from the archive is executed, and no npm script runs.
3. **A new `tools/Env/upstream-android-1.6.0.json`.** It has the same two entries with `version: 1.6.0`, the two measured SHA-256 values, `sourceCommit: faac585884ae2c013a6b1400f3e9e77f10b34bac` and the same `testStatus` wording. The 1.5.2 file stays as history, as 1.5.0's did.
4. **Script and documentation.** The script's archive name, URI, integrity and manifest name move to 1.6.0. `docs/env-ssh.md` belongs to closeout-docs.

Until then, the 1.5.2 daemon pin stays valid on its own terms: its manifest names its own commit.

## 8. For root

- The integrity table is stale on 51 rows before any of the steps above.
- Section 6 step 1 is the only path from this branch to an acceptable table. The tool refuses while any pending-package, pendingParts or not-in-report row remains.
- The policy's suite lists are the reports' own suite sections. They can be narrowed, but each listed suite must have one accepted full-suite receipt.
