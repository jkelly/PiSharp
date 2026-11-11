# Pi 1.5.2 CI/source-version audit

This is workflow configuration and offline checker evidence. No .NET build, hosted workflow, release or deployment was run by this work package.

## Current candidate coverage

| Workflow | Required current checks |
| --- | --- |
| `gap-regressions.yml` | Installs `runtime-fixture-152` into `artifacts/node-runtime-152`, invokes the standalone 1.5.2 installed-runtime ABI check, then exports the explicit `PISHARP_TEST_PI152_NODE_MODULES` path for later suites. A missing or incorrect fixture fails this step. The separate production installer still creates a fresh cache and verifies registry provenance. |
| `core.yml` | Fetches and tags exact v1.5.2 `b4afdddf44e059c67ccf5097e5317d913521b470`, checks it out separately from the historical check, runs current source and applicability suites with `PI_REQUIRE_UPSTREAM=1`, and verifies all 2,565 source blobs plus archive. |
| `durable.yml` | Runs the current applicability/overlay/fixture checks after independently checking the preserved historical Durable map. |
| `release.yml` | Manual dry-run default is 1.5.2; active runtime comment names 1.5.2. When tests run, installs and directly verifies the current locked fixture before the release suite and exports its explicit path. Publication remains controlled by existing tag-triggered jobs; this task does not publish. |

`sync-1.5.2-applicability.test.mjs` checks exact one-to-one frozen file endpoints, IDs, statuses/reasons, report references, summary counts, preserved Durable map bytes/rows and current fixture dependencies/provenance hashes. The existing source suite now refuses a requested upstream check when `PI_UPSTREAM_V1_5_2` is absent.

## Preserved historical coverage

- Core retains exact `7d073328e44337d3292a7db9b0f0308f679c5e97` checkout and v1.5.0/v1.2.0 objects/tags for the historical applicability and Durable worklist checks. The current check uses a later explicit checkout; no old test is silently redirected.
- `build-port-map.mjs --map compatibility/durable-port-map.v1.5.0.json --check` now checks the exact preserved historical map in both core and Durable workflows. The new `--map` option changes only which map is read/compared; its frozen v1.5.0/v1.2.0 generator constants remain intact.
- Historical runtime fixture 150 files and their provenance remain unchanged. The `pi150` string still present in `tools/ci/test_gap_runner.py` is a negative test's stable old ABI case ID: it deliberately must not satisfy the separate required production-installer case. It is not a runtime path or environment variable.

## Verification

- Parsed all four edited workflow YAML files with Python/PyYAML.
- Current source/applicability plus historical applicability: **11 passed, 0 skipped**, with both upstream environment variables bound to the local pinned repository.
- Current source verifier: **5,185 checks**, all **2,565** source blobs and canonical archive verified; historical inventory/publication pins remain v1.5.0.
- Preserved Durable map generator check: passed, **289 historical rows** with their original statuses retained.
- Python bounded runner suite: **32 passed**.
- Active `.github`/`tools/ci` old runtime-path/env audit found no remaining 150 path/environment usage. Historical negative case identity is retained as described above.
- The active contributor testing guide now uses `runtime-fixture-152`, `PISHARP_TEST_PI152_NODE_MODULES` and Node 22.19+, with historical-source checks explained separately.
- Scoped `git diff --check`: passed.

Hosted execution and platform validation remain the coordinator's responsibility.
