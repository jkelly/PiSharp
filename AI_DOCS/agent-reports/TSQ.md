# TSQ — chord.telemetry-sqlite

## Scope and source

Implemented the `chord.telemetry-sqlite` row from `compatibility/sync-1.5.0-applicability.json`, against the frozen `upstream-v150/packages/chord/src/telemetry-sqlite/{index,store}.ts` and both upstream telemetry SQLite test files. This is a buffered telemetry **recorder and query store**, not automatic instrumentation of SQL statements. The initial delegation's SQL-instrumentation wording was corrected by the coordinator. No exporters, default telemetry changes, automatic SQL text capture or parameter capture were introduced.

## Files and contracts

- `src/PiSharp.Chord/TelemetrySqlite/SqliteTelemetry.cs`: public `SqliteTelemetry.CreateAsync(ISqliteDatabase, SqliteTelemetryOptions?, TimeProvider?)`, implementing existing telemetry context, metrics, propagation and store interfaces. Explicit opt-in; pass the instance to `HostOptions.Telemetry`, or attach a span with `ChordContext.WithTelemetry`.
- `src/PiSharp.Chord/TelemetrySqlite/SqliteTelemetryStore.cs`: public committed-data readers usable without a recorder or timer.
- `tests/PiSharp.Chord.Tests/TelemetrySqliteCases.cs`: standard span/propagation conformance and nine focused native SQLite tests, including tied-page completeness/two-query cost and surviving descendants.
- `tests/PiSharp.Durable.Tests/SqliteTelemetryIntegrationCases.cs`: real `SqliteConnectionDatabase` → existing `StoreSqliteDatabase` facade → recorder and actual `Hosts.Create` call, with parameterized business SQL and unchanged result/private value behavior.

The existing `PiSharp.Durable.Store.StoreSqliteDatabase` provides the production binding to Durable's SQLite driver; no second driver, package dependency, or interface changes were needed. The recorder leaves its database caller-owned.

Parity includes dedicated `telemetry_schema` migration and upstream column/index names; unique scope/sequence span identities; explicit propagation and cross-recorder links; copied attributes/events; elapsed-clock event/end timings; automatic error status unless explicitly set; unchanged callback results/exceptions; inert post-settlement spans. Only settled spans are queued, with 10,000 defaults and in-flight spans included in the bound.

Metrics aggregate count/sum/min/max by ten-second interval and canonical name/kind/unit/attribute series. Description, histogram boundaries, and exemplar contexts do not change stored series. Pending interval/series pairs have their own capacity including in-flight metrics. JSON series keys use Chord's JavaScript serializer to preserve cross-runtime upserts, including integer-like property names.

One-second automatic flush writes spans/metrics plus seven-day-default retention cleanup in one serialized transaction. Ordinary flush excludes the current metric interval; close drains partial intervals, cancels the timer, ignores late business completions, and does not await active business work or close the database. Write failure atomically rolls back, permanently stops recording, clears queues/timer, and every later flush/close reports the same exception without retry.

Readers preserve upstream head ordering, name/error/time/same-span attribute filtering, scalar JSON type equality, pagination and SQL parameter binding. They read only committed data and accept pre-operation cancellation; the underlying Chord SQLite facade itself has no cancellation argument.

## Validation

Central serialized CI requested: Chord `--only telemetry-sqlite`, Durable `--only telemetry-sqlite.native-host-binding`. No direct builds/tests were run by this worker to avoid shared output races.

- **Native production binding passed:** inspected `artifacts/gap-validation/TSQ-native-runtime3-c79a3b0b04e54665babb7b299a7bb8ee/receipt.json` and its test output. Debug Durable test build passed, then exactly **one selected/declared case**, `telemetry-sqlite.native-host-binding`, passed with zero failures/skips. Both stages exited 0. Receipt HEAD is `14dd34b369fba6d236684da6dfc8a39a03263321` with authored dirty-tree changes. Before/after source manifest SHA-256 both equal `7b4a1a897942a14c500e1a7908b12750c1fdbe9e2f4279bde96a3b53cbbe57d4`; `changedDuringRun: false`. The test report names frozen upstream source `f9a26365568127461c47a07a37dcb42c140a3ac5` and labels its evidence `PASS_AUTHORED_NATIVE_ONLY` (no new upstream capture).
- **Chord rerun pending:** `Chord-telemetry-runtime2-c73e42903a1b48e3be4379471ca0ee0e` completed 20 cases, 19 passed and one authored fixture failed: tying parent and child timestamps let lexicographic IDs (`:10` before `:9`) correctly make several children trace heads. The test now matches upstream's fixture (tied roots across traces; later children) and asserts that premise before removing one root. No production query change was needed. Final focused Chord rerun remains pending coordinator/impl_ci.

Tests are authored from frozen upstream source, not newly captured upstream output. Requested model/tier: high sol / ultrafast; effective tier unverified.

No commits, historical fixture edits, or compatibility manifest status changes were made by this worker.
