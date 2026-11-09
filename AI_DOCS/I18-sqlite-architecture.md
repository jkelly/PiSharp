# I18 — SQLite group commit architecture

Prerequisite: I12 isolated production-reference probe passed (3/3, zero build warnings/errors), receipt `artifacts/gap-validation/I12-isolated-1e4e37d94a30493895c7dde343f1f50a/receipt.json`. Full Durable suite remains required. This plan precedes I18 source edits.

## Contracts

One existing serialized SQL transaction remains the authority. Decode/preload/storage/write/COMMIT failures reject everything and publish no receipt. Only submitted-batch semantic checks may return a refusal, committing the accepted prefix. Receipt order and seq allocation remain unchanged. No schema/API/dependency changes.

## Implementation

Port frozen upstream `packages/durable/src/storage/sqlite/{commit,statements}.ts` design into private nested helpers in a partial SqliteStorage. Collect claimed IDs, touched document IDs, delta revision headers, and address keys. Read them through bounded JSON-array queries, decode before semantic-refusal handling, and advance a transient in-memory check state only after acceptance. Reuse native StorageRules validation and lossless indexed-string encoding.

Accumulate the accepted net effect by table: unique conversation/entry rows, latest task/submission record, latest document lifecycle record, retained document revisions and current-only clears. Emit bounded JSON-array INSERT SELECT statements for small rows; bind large rows with fixed power-of-two VALUES shapes under 100 parameters. Cap array text at 512 Ki characters; keep one oversized read key alone, as upstream. The existing bounded LRU limits all generated SQL shapes.

Before a copy reads a source, flush accepted pending writes inside the same SQL transaction. Resolve source contents with the I12 watched decoding path. Continue to distinguish semantic copy mismatch/missing source from persisted corruption. Final flush precedes allocation metadata and durable COMMIT.

## Verification

Rerun isolated I12 prerequisite against actual updated assemblies. Extend that artifact with 32 existing StorageConformance cases, seeded MemoryStorage/SQLite differential groups, prefix and lone-surrogate inputs, create/change/retire/recreate/copy, repeated mutable record replacement, rollback/write/COMMIT failure, and exact SQL-call counts using a counting/delayed facade. Count proof must cover hundreds of batches and bounded large rows; latency observation is supplementary rather than a universal benchmark claim. Coordinate all runtime runs through I13. Update dedicated SQLite tests to inject first/later failures at the new bulk-prefetch and copy-flush boundaries. Full Durable tests remain final integration gate.

## Risks

Preloading decodes every touched persisted row before semantic checks, so corruption in later requested batches rejects the call even if an earlier semantic refusal would stop writes. This agrees with upstream's preloaded state and the stronger storage-failure contract. Intermediate SQL trigger ordering is per-table, as upstream; custom host triggers on Durable tables are not a public ordering contract. Copy flush boundaries preserve all caller-visible document/receipt state. Shared test compilation may remain blocked by unrelated Chord work; isolated production probes allow focused progress without claiming full-suite completion.
