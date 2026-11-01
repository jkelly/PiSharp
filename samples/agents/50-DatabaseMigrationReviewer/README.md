# Database migration reviewer

Runs `simulate_migration` → `review_rollback` → `write_migration_review` for an explicit `migrationId` (`rename-display` in the fixture). The database fixture is a table map; each table contains column nullability metadata and row objects. Migrations contain forward and rollback operation arrays.

Supported operations are `renameColumn`, `dropColumn` and `addColumn`. The disposable in-memory model checks referenced schema objects, detects populated-column loss, rejects non-null additions without a backfill/default, and compares rollback schema and row values against the original snapshot. Unknown operations, missing rollback and failed roundtrips block the review. Changes to evidence invalidate prior stages.

Reports are written to `migration-<migrationId>.json`. A clean result is `ready-for-database-validation`, **not production approval**: no database connection or SQL execution occurs. SQL syntax, transactions, indexes, foreign keys, locking, concurrency and performance are outside this model and require real database validation.

```powershell
dotnet run --project samples/agents/50-DatabaseMigrationReviewer -- --demo
dotnet run --project samples/agents/50-DatabaseMigrationReviewer -- --self-test
```

Six tests cover a reversible rename, destructive rollback, missing rollback, unsupported operations and non-null additions, independent/repeated simulations, input immutability and stale/invalid evidence guards.
