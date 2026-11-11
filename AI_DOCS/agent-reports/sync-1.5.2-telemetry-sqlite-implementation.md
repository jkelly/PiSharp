# T3 SQLite telemetry implementation

Implemented against the reviewed v1.5.2 discovery contract. No builds or .NET tests were run: root owns serialized validation. Scoped `git diff --check` passed.

## Changes

* `src/PiSharp.Chord/TelemetrySqlite/SqliteTelemetrySchema.cs` retains the v1 migration verbatim and appends v2, copying existing spans with `CAST(round(ms * 1000) AS INTEGER)` into integer microsecond columns before replacing the table and indexes. Existing metrics and event JSON remain intact.
* `SqliteTelemetry.cs` composes T2 `TelemetryRecorder`, forwards recording/propagation/metrics and public `Ingest(TelemetryBatch)`, and retains the original timer/options/caller-owned database surface. Serialized drains retain taken capacity until write completion; spans use `INSERT OR IGNORE`; aggregates and retention share one transaction. Write failures stop/discard the recorder and preserve the same exception for subsequent flush/close. Close drains partial metrics without waiting on active business spans.
* `SqliteTelemetryStore.cs` reads microseconds as milliseconds; span order and head-name selection use `started_us, length(id), id`. Start bounds scale by 1000. Cursors carry raw microsecond starts and trace IDs.
* `SqliteTelemetry.OpenReaderAsync(ISqliteDatabase ownedReadOnlyDatabase)` and `SqliteTelemetryStore.OpenReaderAsync(...)` validate existing exact-v2 metadata using reads only; `SqliteTelemetryReader` owns and idempotently closes only that connection. A failed check closes it too.
* `src/PiSharp.Durable.Sqlite/ConnectionDatabase.cs` adds `SqliteDatabaseOpenMode` and `SqliteStorageOptions.OpenMode`. Read-only opens do not create directories, run WAL/synchronous/autocheckpoint write pragmas, or checkpoint at close; read-only transactions use deferred `BEGIN` instead of `BEGIN IMMEDIATE`. Native SQLite enforces write rejection.
* Native convenience lives in `src/PiSharp.Durable.Store/TelemetrySqlite.cs`: `NativeSqliteTelemetry.OpenReaderAsync(string path, SqliteStorageOptions? options = null)`. That project already references Chord and Durable.Sqlite; neither direction of the forbidden Chord/Durable dependency is introduced.

## Validation to run

Both case enumerators already existed and are automatically registered by their respective programs. Run the `telemetry-sqlite.` filter in Chord and Durable test executables.

New Chord cases:

* `telemetry-sqlite.v1-migration-preserves-spans-events-metrics`
* `telemetry-sqlite.ingest-idempotent-microseconds-and-cursors`
* `telemetry-sqlite.read-only-reader-checks-exact-schema-and-owns-connection`

These join existing adapter conformance, buffering/clock, interval/canonical aggregation, capacity/in-flight/concurrent close, permanent atomic failure, timer/retention/active close, bound typed queries, pagination, and detached snapshot cases.

New Durable cases:

* `telemetry-sqlite.native-read-only-file-and-connection-ownership`
* `telemetry-sqlite.native-read-only-missing-old-newer`

These check a live independent writer and read-only reader, native write rejection, readable transactions, close ownership, unchanged file bytes, missing-path noncreation, exact schema validation, and released failed-open handles. Existing native host binding case may need its old `Spans.Single()` assertion broadened by root when new host instrumentation is merged; that separate integration was not changed here.

## Coordination

T2 signatures consumed exactly as published by `/root/telemetry_ingest`. `/root/sqlite` completed its Commit/Storage changes before ConnectionDatabase edits. No commits made. Requested high/ultrafast execution settings were inherited and the effective service tier remains unverified.
