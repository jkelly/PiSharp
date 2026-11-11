# META-DOC candidate documentation and header audit

All metadata remains source-candidate status. No release ledger, historical applicability row, recorded fixture or previous acceptance status was upgraded by this work.

## Current source mapping

`compatibility/sync-1.5.2-applicability.json` maps every one of the 2,531 frozen changed-file records to a reviewed area/work-package report and source endpoints. The current snapshot has 979 `implementation-pending-validation` and 1,552 explicit `no-op` records. Root owns integrated evidence and final status changes. The generator creates a fresh pending snapshot; do not rerun it after final qualification without reviewing status changes.

No-op reasons distinguish the 953 standalone upstream app files, documentation, examples/fixtures, JavaScript build/CI/Nix tooling, reviewed type-import relocations, TypeScript export plumbing, browser DOM/React, IndexedDB and the unsupported downloaded-JavaScript terminal sandbox. They do not exclude native Chord App, terminal, portable client, store or publication contracts. C-SYNC has eleven records (eight source files plus tests), and C-TERM has 26 source/test/documentation records. Native terminal mappings were confirmed by its owner; secure downloaded-code execution alone remains a platform-adapter limitation.

The audit found the Chord compression helper changed only comments/import paths; root confirmed this and it is classified as a no-op. Remaining legacy-module deletions are assigned to C-LEGACY; the native legacy API is preserved. Package/version changes remain assigned rather than implying complete runtime acceptance.

## Durable preservation

The exact original `compatibility/durable-port-map.json` is preserved as `compatibility/durable-port-map.v1.5.0.json`, SHA-256 `45fb24e89fb9d0c63ccc2fd8c05dd6e66f3b63fad0d7500f2964303380690ee2`.

The main map retains its historical v1.5.0 source, rows, notes and status counts. Its additive `currentImplementation.candidate` pointer names `compatibility/durable-sync-1.5.2-overlay.json`, which records the current source endpoints, report owners and previous mapped locations for 124 changed Durable/related Chord records. No new native filename or completed-port claim is invented. Existing historical integrity checks exclude the additive candidate pointer, as they already exclude current implementation metadata; the protected historical digest remains unchanged.

## Documentation

Added unreleased `docs/release-notes/1.5.2.md` and a candidate CHANGELOG heading. README, the current section of IMPLEMENTATION_STATUS and the implementation-status JSON pointer distinguish the 1.5.2 source candidate from published 1.5.0.1. The website's shared version data, home/parity pages, overview/versioning, Durable guide and Node bridge guide make the same distinction. Existing assessment tables, package versions, installation examples and release evidence remain on their actual published versions.

## Header audit

Root requested attribution headers on all new C# files. `tools/CompatibilityReport/sync-1.5.2-source-headers.mjs` uses an explicit reviewed map, verifies every referenced upstream path at the pinned Git commit, then prefixes comments while preserving the original body bytes and line endings. It has no fallback that guesses a source for unknown files.

The initial completed pass added headers to 101 files; all 170 then-present new C# files had source/native/generated attribution within their first six lines. AppOptions/WorkerApp mappings and all terminal mappings were confirmed with their owners. Pure native CLR, compiler, terminal I/O, stream and test-access adapters are labeled PiSharp-only. Lovely Mermaid Apache-2.0 headers are left intact; its authored integration test identifies that package/license separately. Audit output is `artifacts/sync-1.5.2-meta/source-headers.json`.

## Checks

- Full inventory/source-lock suite: 3 passed, including local-Git regeneration.
- Historical applicability/integrity suite: 4 passed, 1 optional upstream test skipped.
- Direct metadata assertions: all 2,531 rows exactly reproduce frozen endpoints, every report path exists, only pending/no-op statuses occur, historical Durable rows equal the preserved map, and website/public release versions remain historical.
- Header second pass: zero additions needed and zero unmatched files at that snapshot.
- Scoped `git diff --check`: passed.

No .NET or website build, live API request, publication, deployment or commit was performed in this work package.
