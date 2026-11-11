# I-STO-SQLITE implementation

Reference: `upstream-v152` at Pi v1.5.2 `b4afdddf44e059c67ccf5097e5317d913521b470`, specifically `packages/durable/src/storage/sqlite/commit.ts`, `statements.ts`, and `packages/durable/test/sqlite-commit.test.ts`. Requested worker tier ultrafast; effective tier unverified.

## Changes

- `src/PiSharp.Durable.Sqlite/Commit.cs`: removed the current-address prefetch added for every loaded document. Added a SQLite-local document action checker matching upstream: plain changes do not enter live-count accounting; existing retirements decrement it; creations increment it. Only addresses created/copied somewhere in the call need querying. The existing apply step still clears a retired current ID only if that incarnation remains current, preserving replacement order independence. Global ID checks remain shared. Persisted record and revision decoding stays outside semantic-refusal handling, including the stricter native missing-revision check.
- `src/PiSharp.Durable.Sqlite/Storage.cs`: corrected the implementation comment to describe the shared ID checker and SQLite-local document checker.
- `tests/PiSharp.Durable.Tests/Storage/SqliteDetailedBulkCases.cs`: added `SqliteDetailedBulkCases()` with eight cases (one case iterates twelve deterministic seeds), discovered by the existing `Cases` suffix convention.

## Added evidence

1. Actual SQL/parameters for one and 150 document-delta-plus-entry batches: exactly eight executor calls, identical SQL shape, and no current-address query. Checks actual JSON-driven query plans.
2. One hundred 7,000-character entries: exactly two small JSON arrays, each at most 512 Ki characters; six total executor calls; all entries round-trip.
3. Nineteen large quote-heavy entries with a lone surrogate: entry inserts bind exactly 80, 10, and 5 parameters (16 + 2 + 1 rows); seven total executor calls; every value round-trips.
4. Create-before-retire and retire-before-create replacements: B remains current, its next delta succeeds, and a later C at the same address is refused without an ID claim.
5. Twelve deterministic differential scenarios compare grouped SQLite to both MemoryStorage and individual SQLite commits. Cover all record tables, repeated tasks/submissions with last-write-wins, lone-surrogate indexed strings, current-only and rewindable bases/deltas, copies from current and historical points, copy-triggered pending flushes, retirement/replacement, six semantic-refusal classes, ordered receipts, and rejected suffixes. Every physical row in all eight data/metadata tables is compared to individual commits, including revisions and ID/sequence allocation. Actual JSON-driven SQL is explained, rejecting stored-table scans while allowing virtual-table scans.
6. Malformed persisted JSON in direct-check records and copy-source records after an earlier valid batch: whole transaction rejects, no receipts, and all eight physical tables remain byte-equivalent through their row serialization. This complements the existing valid-JSON/invalid-shape and missing-revision/base/delta corruption matrix in `SqliteStorageCases.cs`.

## Validation / commands

- Read discovery and plan: `Get-Content AI_DOCS/agent-reports/sync-1.5.2-storage-discovery.md` and `Get-Content AI_DOCS/sync-1.5.2.md`.
- Compared frozen upstream commit implementation and test shapes with native `Commit.cs`, `Storage.cs`, `StorageRules.cs`, existing bulk tests and existing corruption/copy failure tests using `Get-Content` / `rg`.
- `git diff --check -- src/PiSharp.Durable.Sqlite/Commit.cs src/PiSharp.Durable.Sqlite/Storage.cs tests/PiSharp.Durable.Tests/Storage/SqliteDetailedBulkCases.cs`: passed. (New test file inspected directly; as an untracked file it is not included in Git's diff checks until staged.)
- No dotnet builds or tests launched by this worker: root owns serialized compilation and execution. Runtime results are pending that verification; test counts above are assertions, not a claim of measured execution.

No shared StorageRules, JSONL, MSSQL, schema, or test registration files modified by this worker.

## Root validation follow-up

Root's serialized run found three fixture failures, with the remaining SQLite cases passing. Fixed the unprefixed differential setup to use `Prefix = null` (an explicitly empty prefix is invalid). The malformed-JSON fixtures now temporarily disable SQLite CHECK constraints only for corruption injection, then restore them before the commit under test. This allows exercising storage reads of malformed persisted JSON while keeping normal writes constrained. No worker-side builds launched; rerun pending root.

The next focused run passed both malformed-data cases and identified a diagnostic expectation difference in differential seed 3. Frozen upstream Memory resolves copies before checking and wraps same-batch source refusal (`Document copy 230 was rejected`, inner cause `Fork source document 200 is changed in the copy batch`); frozen upstream SQLite checks the source in CommitState first (`Document copy 230 source is changed in the copy batch`). The fixture now asserts these exact backend-specific messages and the Memory inner cause for that scenario. Other refusals retain message equality; grouped/individual SQLite refusal, receipts, prefix length, public state, and physical tables remain compared. This corrects the fixture without changing source-correct runtime behavior.
