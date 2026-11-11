# Final source acceptance mechanism

Prepared only. No source acceptance was applied by this work package.

## Sequence and immutable inputs

1. Validation completes the required full suites, expected new-case coverage and final current-source verification. Source/code must be stable.
2. Validation freezes `AI_DOCS/sync-1.5.2-source-validation.json` and `AI_DOCS/sync-1.5.2-source-coverage.json`. The former declares `sourceValidationOnly: true`, a finalized status, empty source `pendingSuites`/`additionalPending`, and `packageValidation: "pending-not-asserted"` (or `{ "status": "pending" }`). The mutable diagnostic ledger remains independent.
3. Root reviews a dry-run plan from the exact two SHA-256 values. Only root's later explicit authorization permits `--apply`.
4. After applying metadata, root must reseal the public derivative integrity table with `node tools/refresh-derivative-integrity.mjs`, then verify it with `node tools/refresh-derivative-integrity.mjs --check`. The transition changes three pinned files: `README.md`, `IMPLEMENTATION_STATUS.md`, and `compatibility/implementation-status.json`. Root audited the current runtime source manifests: none includes these files, `PUBLIC-DERIVATIVE-INTEGRITY.json`, or `tools/PublicDerivativeIntegrity.mjs`, so this metadata reseal does not invalidate the accepted runtime proofs.
5. Only after that reseal/check, root runs a final incremental solution build plus package/consumer/private-CLI checks and records them separately in `AI_DOCS/sync-1.5.2-packaging.json`, referencing the frozen source hashes. The solution-wide manifest includes metadata files changed during acceptance, so this final build qualifies the post-metadata tree. Neither frozen source snapshot is rewritten for packaging.

```powershell
$ledger = (Get-FileHash AI_DOCS/sync-1.5.2-source-validation.json -Algorithm SHA256).Hash.ToLowerInvariant()
$coverage = (Get-FileHash AI_DOCS/sync-1.5.2-source-coverage.json -Algorithm SHA256).Hash.ToLowerInvariant()
node tools/CompatibilityReport/sync-1.5.2-applicability.mjs `
  --accepted-ledger AI_DOCS/sync-1.5.2-source-validation.json --ledger-sha256 $ledger `
  --coverage AI_DOCS/sync-1.5.2-source-coverage.json --coverage-sha256 $coverage `
  --out artifacts/sync-1.5.2-meta/final-acceptance-plan.json
# Only after explicit root authorization, run the same command with --apply.
```

## What the gate checks

The reviewed mapping is `tools/CompatibilityReport/sync-1.5.2-acceptance-policy.json`. Every applicable work package needs its listed full-suite latest receipt, a positive observed case count, no failed case, stable source and `currentSourceMatches: true`. The exact canonical project path must match between ledger and raw receipt, including the special `PiSharp.Extensions.ContractTests.csproj` name. The raw stages must exit successfully. Source manifest canonical hashes, current file bytes and additions in each project closure are checked again; a recorded pass from older catalog or headers is insufficient.

Every coverage group must be complete, point at that exact accepted receipt and contain each expected ID as an actual passed case. Missing IDs and skipped required new cases block acceptance. Other explicit suite skips become `tested-with-limitations`, retaining IDs/reasons and platform metadata. The local/legacy baseline limitations also keep the overall record qualified.

The final cohort also requires the thirteen additional standalone suites authorized by root: AnthropicSimple, AnthropicProfile, AnthropicAuthSync, AzureResponses, AzureSimple, NativeModelCatalog, ClassifierConfig, Classifiers, Agent, Rpc, Durable.Hosting, Durable.Server and Tui.Editor. AI/CLI share Agent's receipt rather than count it twice. Dotted names use their exact canonical project filenames. The acceptance record preserves each raw evidence kind, result and distinct case-status counts. SDKBeta remains auxiliary native-capture evidence in the original ledger; it is not promoted to a fabricated passing case or added as a normal suite.

The final coverage audit then authorized exactly three more: McpParity and McpTuiOriginalQualification under MCP, and DurableMessages under Durable/CLI. The qualification project executes 21 existing offline ownership controls; DurableMessages executes eight native controls. Their `PiSharp.CodingAgent.Tests` assembly name is intentional and does not change canonical project identity or imply external source execution. Their linked tests use fake provider/authentication values and controlled HTTP/loopback callbacks, native task ownership and temporary local files. No live provider or real account prerequisite was identified. The validation owner's existing assembly override selects the executable; this metadata work changes no shared harness.

The complete current catalog is independently included: 43 provider shards and manifest hashes, version/source pin, package tarball/SRI provenance, and the catalog report link. It is 1,773 entries with 82 adds, 21 removals and 104 changes; native Anthropic metadata binding and its request tests still must pass their suite gate.

Before writing, the gate rechecks the input hashes, receipts, catalog and current source. It refuses any metadata/doc edit that would alter a file included in an accepted runtime manifest, preventing self-invalidating receipts. Inspection of the present cohort found no overlap with the planned metadata writes. All documentation anchors are checked before any write.

## Applied result, when authorized

Applicable rows and work packages become `tested` or `tested-with-limitations`; no-op rows stay byte-for-byte equivalent objects. The Durable overlay and current pointers are updated, while old map rows/pins remain unchanged. The acceptance record names exact source ledger/coverage hashes, catalog provenance, suite receipts and limitations. Candidate README, release notes, work plan, status and website text become truthful about local source acceptance while remaining **unreleased**. No package-validation result or publication is inferred. The pending generator refuses to overwrite accepted metadata without an explicit reset.

Current CI permits final status vocabulary through the map's `statusMeaning`, and acceptance controls run in Core CI. Controls cover dry-run immutability, limits, source changes/new files, missing coverage/final qualification, exact project identity, input hash changes and self-invalidation.

## Separate pre-existing legacy checker limitation

`tools/CompatibilityReport/check.mjs` is not invoked by the active core/gap/Durable/release workflows or package builder. Those use dedicated current-source checks. The broader legacy checker encounters `session-branches-replayed` first, followed by five other unregistered standalone families: `session-compaction`, `session-context-edits`, `session-lifecycle`, `session-native-copy-reader`, and `session-recovery`. The first manifest has `capture`/`lock` rather than generic `expected`/normalizer/requirement fields, so adding its name to the profile table is not a valid fix.

Closing that gate requires dedicated shape/provenance handlers and mutation-rejection controls, retaining its exact historical Node 24.19.0 executable check. It must not be repaired by skipping families or weakening assertions. Read-only inspection confirmed the branch family's input/capture/lock bytes match its current immutable manifest. That limited integrity check is not full oracle qualification. Root explicitly kept this separate from the 1.5.2 update.

The current source snapshot is not yet final: latest observations still include pending/failed/stale-source receipts and incomplete coverage, with catalog/Anthropic binding reruns underway. The dry-run reports those gaps and refuses application.
