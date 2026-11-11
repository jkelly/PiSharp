# META-PIN contract

The owner authorized all applicable Pi and Pi Durable updates to 1.5.2. This work changes candidate source metadata, not publication or acceptance status.

- Preserve the exact existing `compatibility/target.lock.json` bytes as `compatibility/target.v1.5.0.lock.json` before changing the current target. Preserve the v0.99.1 evidence baseline and every older lock, capture, release record and inventory.
- Pin current source to tag `v1.5.2`, commit `b4afdddf44e059c67ccf5097e5317d913521b470`, using Git object bytes for SHA-256 and separate checkout hashes under the existing hash policy. Record v1.5.0 as the previous target. Include every package manifest and every surviving changed file, in addition to carried source paths.
- Generate the complete `v1.5.0..v1.5.2` file inventory from local Git (`diff --name-status -z -M`), without API pagination or truncation. Include root and package files, additions, deletions, rename pairs, Git blob ids, byte lengths, SHA-256 hashes and source URLs for both endpoints. This is source evidence, not a parity claim.
- Candidate package version and live Pi Node runtime package versions become 1.5.2. Existing runtime cache policy stays unchanged, isolating the new version from old caches. Runtime dependency pins follow the 1.5.2 package manifest. Default identity reports Pi 1.5.2 without changing attribution or account policy.
- Existing v1.5.0 applicability and release inventory tests resolve their preserved source lock. New target integrity checks independently validate the candidate lock and complete inventory. Historical acceptance does not become candidate acceptance.
- Source citation tooling emits a report only. Area owners review and change their citations together with implementation. No broad mechanical source rewrite is authorized by this work package.

Validation is offline Node/source validation here. Root owns serialized .NET builds and integrated tests. Publication and deployment are outside this task.

## Implementation and evidence

- Frozen target: v1.5.2 `b4afdddf44e059c67ccf5097e5317d913521b470`, tree `ed418d79cb44d3690f110e1077c4612db033f2c9`. The lock contains 2,565 source files (140 unchanged, 57 changed, 2,368 newly recorded) and every package version. Preserved v1.5.0 lock SHA-256: `429dad2debcf296a980cf3d813e7d1609855bc0d1e71f84c29bb003e758c3e1e`.
- Complete file inventory: 2,531 changes: 437 modified, 1,453 added, 535 renamed, 106 deleted. `source-diff.mjs` reproduces both endpoint blob hashes using local Git; no GitHub compare file limit applies. `git-source.mjs` batches reads so complete source verification remains fast.
- `resolveSourceLock` makes existing inventory and published-release checks follow their recorded v1.5.0 lock while current source checks follow v1.5.2. Historical applicability tests explicitly use the v1.5.0 lock.
- Node virtual modules regenerated from the exact source with zero unresolved imports. The generator now refuses an unexpected or dirty checkout before writing. Export snapshot and runtime version expectations updated. Existing explicit no-default-attribution behavior remains `{ attribution: null }`; the selftest now checks that actual existing policy.
- Fresh `runtime-fixture-152` manifest and npm lock preserve the old 150 fixture. Registry lock resolution and isolated `npm ci --ignore-scripts` were approved by automatic review, succeeded with 123 packages, and did not admit a production cache. Current fixture tests use `PISHARP_TEST_PI152_NODE_MODULES` or `artifacts/node-runtime-152`.
- Source citation report is `compatibility/sync-1.5.2-citations.json`: report-only, 302 byte-identical candidates, 534 pending citations, 112 unparsed. No source citation was mechanically applied by that scan; area owners remain responsible for source decisions.

### Commands run

- `PI_UPSTREAM_V1_5_2=E:/AI/PiSharp/upstream-v152 node --test tools/CompatibilityReport/sync-1.5.2-source.test.mjs`: 3 passed, 0 skipped. Includes full inventory regeneration and exact preserved-lock bytes.
- `node tools/CompatibilityReport/verify-baseline.mjs --upstream E:/AI/PiSharp/upstream-v152`: 5,185 checks, all 2,565 source blobs and source archive verified; inventory/publication source remains v1.5.0.
- `node --test tools/CompatibilityReport/sync-applicability.test.mjs`: 4 passed, 1 optional upstream check skipped.
- `FORCE_COLOR=3 node tools/NodeBridge/pi-host/virtual/selftest.mjs --upstream E:/AI/PiSharp/upstream-v152/packages`: 55 passed. Forced color is needed because the host sets `NO_COLOR`; the rendering assertion deliberately checks ANSI output.
- `node tools/NodeBridge/installed-runtime-abi.mjs artifacts/node-runtime-152/node_modules`: passed real Pi 1.5.2 imports, jiti/TypeBox/shared module identities, explicit/default attribution and reload.
- `node tools/CompatibilityReport/check.mjs`: reaches 92 checks, then reports pre-existing unregistered `fixtures/pi-v0.99.1/session-branches-replayed/manifest.json`. Root informed. This is not a passing full compatibility report.

No .NET build, fixture recapture, release publication, deployment or commit was performed by this work package.
