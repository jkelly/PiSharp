# I-STO-MSSQL implementation

Implemented the native SQL Server storage-failure correctness extension from the storage discovery report. There is no upstream MSSQL implementation; T-SQL remains per batch/per record. This work does not claim SQLite's bulk statement optimization.

## Changes

- `src/PiSharp.Durable.MSSQL/SqlServerStorage.cs`: stored-data decoding marks the active executor as failed before rethrowing. This covers document records in validation, copy materialization and application; persisted ID ownership/current IDs; missing required bases/revisions; base JSON/object shape/version; and delta JSON/op shape, replay failures and version boundaries. A failure escapes the accepted-prefix refusal catch, so the transaction rolls back and no receipts are delivered.
- Missing/mismatched copy sources, unsupported historical reads, retired-document actions and caller version mismatches remain semantic refusals. Retired documents skip the last-revision read so their legitimately reclaimed revisions do not become false corruption reports.
- `src/PiSharp.Durable.MSSQL/SqlServerDatabase.cs`, `SqlServerLocks.cs`: internal executor/database interfaces permit injected transaction tests without a server; the real connection, queue, transaction and locking implementation remains in place. The storage constructor is internal for that seam; no public API additions.
- `tests/PiSharp.Durable.Tests/Storage/SqlServerCorruptionCases.cs`: 20 automatically discovered authored offline cases use the real storage commit path with an injected transactional write log. Fifteen corrupt-state cases assert an earlier entry batch reached its write, then the whole call throws, no receipts occur, no entry persists and allocators remain unchanged. Five semantic controls assert the earlier batch commits and receipts arrive only after commit.

## Evidence and commands

- Read `AI_DOCS/sync-1.5.2.md` and `AI_DOCS/agent-reports/sync-1.5.2-storage-discovery.md`, inspected SQL Server storage/database/locks and shared document validation/replay code.
- `git diff --check -- src/PiSharp.Durable.MSSQL tests/PiSharp.Durable.Tests/Storage/SqlServerCorruptionCases.cs` passed.
- Checked only presence of `PISHARP_DURABLE_MSSQL_CONNECTION_STRING`: absent. No credentials were printed and no live server was contacted.
- No builds or test executions were launched by this worker, as required by root-serialized validation. Requested root run: `dotnet run --project tests/PiSharp.Durable.Tests -- --only storage.mssql.offline` (with root's selected build configuration/flags).
- New test methods end in `Cases`; the suite reflection-discovers them, so `Program.cs` registration is unnecessary.

The offline transaction log proves storage control-flow classification against its injected transaction contract; it does not prove live SQL Server rollback behavior. Existing live MSSQL tests remain gated by the connection environment variable.
