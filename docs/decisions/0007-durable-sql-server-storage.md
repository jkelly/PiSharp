# SQL Server storage for pi-durable

Status: requested by the project owner on 2026-10-16. Built on branch `feature/pi-durable-mssql`, cut from `feature/pi-durable`; experimental under [0005-pi-durable-experimental.md](0005-pi-durable-experimental.md), not packed or released.

## Context

The durable port ([0005](0005-pi-durable-experimental.md)) stores sessions in memory, in JSONL directories or in SQLite files, as upstream does. Upstream has no SQL Server storage. A host that keeps many durable sessions on a server, such as a web agent, needs them in a shared database rather than in one file per session.

## Decision

1. **Project.** `src/PiSharp.Durable.MSSQL` (namespace `PiSharp.Durable.MSSQL`) implements `IStorage` as `SqlServerStorage`. It is a PiSharp-only backend with no upstream source, so it has no port-map row. Like the rest of the port, every public type is experimental (`PISHARP_DURABLE001`) and the project is not packable.
2. **Stores.** One database schema (default `dbo`) holds any number of named stores. A store is the SQL Server equivalent of one SQLite storage file: its own ID and sequence allocators, its own root conversation and its own records. `SqlServerStorage.OpenAsync(connectionString, store, options)` opens or creates a store; `SqlServerStorage.DeleteStoreAsync` deletes one with all its records. `SqlServerStorageOptions` sets the schema and the lock timeout.
3. **Schema.** `SqlServerMigrations` holds a versioned schema (version 1) that follows upstream's SQLite tables, columns and indexes. Opening applies pending migrations in one transaction under a transaction-owned application lock (one per database), and creates the schema when it is missing. Differences from the SQLite schema:
   - `durable_stores` replaces `durable_metadata` and names each store; every other table leads its primary key and indexes with `store_id`.
   - Tables carry a `durable_` prefix, since `dbo` may hold other tables.
   - Records are `NVARCHAR(MAX)` with no `ISJSON` check. SQL Server's JSON parser refuses text nested deeper than 128 levels, which upstream's `json_valid` accepts; only the storage writes the column.
   - Indexed strings (store names, task kinds, request IDs, document kinds and keys) are `NVARCHAR(MAX)`, which cannot be an index key. Each is indexed through a persisted SHA-256 column, and queries compare both the hash and the text. Text columns use the `Latin1_General_100_BIN2` collation, so comparisons are exact whatever the database's default collation is. Indexed strings stay JSON-encoded, as upstream stores them in SQLite, which keeps lone surrogates lossless and ends every value with a quote, so SQL Server's padded comparison cannot equate values that differ in trailing spaces.
4. **Queries.** `SqlServerStorage` runs SQLite storage's queries, orderings and batch validation (`StorageRules`) in T-SQL, so it passes the same conformance kit. One unpooled connection serves the storage, through one serial operation queue in call order, as the SQLite adapter's does. Commits run READ COMMITTED and lock the store's allocator row first (`UPDLOCK, HOLDLOCK`). Upserts are `UPDATE ... IF @@ROWCOUNT = 0 INSERT`. Global ID ownership is read in one query per commit. Sessions run with `XACT_ABORT ON`, so a failed statement rolls back its whole commit.
5. **One owner per store.** Opening takes a session-owned application lock (`sp_getapplock`) on the store, and close releases it. A second open waits up to the lock timeout (default 5,000 ms) and then fails with `Durable SQL Server store "<name>" is open in another storage`. A process that dies drops its connection, and the server releases the lock. This takes the place of the CLI session lock that guards a SQLite file. The connection is unpooled so that a closed storage never leaves the lock behind in a pooled session. Lock names compare byte for byte while schema names follow the database collation, so the lock is keyed by the schema's `SCHEMA_ID` and the store name: in a case-insensitive database, `dbo` and `DBO` share one store lock.
6. **Dependencies.** `Microsoft.Data.SqlClient` 7.1.1 and `Microsoft.Data.SqlClient.SNI.runtime` 7.1.0, pinned exactly and scoped to `PiSharp.Durable.MSSQL`. This is PiSharp's fifth external runtime dependency. SqlClient 7 no longer depends on Azure.Identity. Its other dependencies are Microsoft MIT packages, recorded in `src/PiSharp.Durable.MSSQL/packages.lock.json` and `THIRD-PARTY-NOTICES.md`. The SNI runtime, which SqlClient loads on Windows, is under the Microsoft Software License Terms, not an open-source license. Its distribution conditions must be reviewed before this backend is packed, released or added to the `pisharp` tool. SqlClient's managed networking (`Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows`) is the alternative to weigh then.

## Tests

`tests/PiSharp.Durable.Tests/Storage/SqlServerStorageCases.cs`. These groups run only when `PISHARP_DURABLE_MSSQL_CONNECTION_STRING` names a SQL Server on which the tests may create a database. Without it, they are empty. A run creates one database (case-insensitive, SQL Server's default collation), gives each case its own store (or its own schema for cases that change tables) and drops the database at the end.

- `storage.mssql.conformance.*` and `storage.mssql.reopen.conformance.*` run the storage conformance kit directly and with a reopen after every commit.
- `session.mssql.*`, `harness.tasks.mssql.*`, `harness.events.mssql.*`, `harness.ownership.mssql.*` and `harness.tools-recovery.mssql.*` rerun those suites on SQL Server. The ownership and recovery suites reopen the same store.
- The authored `storage.mssql.*` cases cover persistence across reopen and a trigger that fails a commit mid-transaction (rows, claimed IDs and the sequence roll back). They also cover a missing base, corrupt operations, rewindable points after reopen, revision reclamation, large and deeply nested records, the store lock (including a waiting open and every spelling of the schema), case-, space- and surrogate-exact store names, a quoted schema name, store deletion, a newer schema version and invalid names.
- `--only` accepts a comma-separated list of prefixes, and `--report` combines with it.

Verified locally on SQL Server 2025 (17.0) with Windows authentication: 272 cases. CI (`.github/workflows/durable.yml`, job `mssql`) runs the same groups against a SQL Server 2022 container on Linux with a per-run `sa` password.

## Not done

- `pisharp durable` still uses SQLite only. Choosing SQL Server from the CLI would bring SqlClient into the tool package, and that waits on the SNI license review above.
- No crash-kill cases (`crash.*`) for SQL Server. A killed process's open transaction is rolled back by the server.
- No query-plan assertions like `storage.sqlite.uses-indexes-*`. SQL Server's cost-based plans over near-empty test tables do not show index use reliably.
- No DBA script for hosts whose login lacks DDL rights. Today the first open must be allowed to create the schema and tables.
