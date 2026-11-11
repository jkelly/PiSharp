# C-K â€” Chord kernel 1.5.2 implementation

Implemented in the shared update worktree. No local builds/runs; `/root/validation` owns serialized validation and has been told source is ready for first compile/run.

## Source changes

- Added typed context values; call-scope stamps survive derived contexts; old-work chain follows nested calls and cancellation removal. New OldWork/ReloadCycles helpers.
- Every live changed-plugin object restarts with new attempt, bounded old-work drain, hidden recorded-request readiness, new-call gate, old-version routing, cycle checks, and retained contributions/references during handoff. In-memory jobs abort with reload.
- Local pre-load operations refuse immediately; invalid plugin names fail before mutation; unavailable version/reload_cycle reasons and public plugin error helpers.
- One per-host UnboundClock, registry contributor-set diagnostics and plugin-test introspection.
- Additive public contracts for HD live query/host-keyed docs; partial load gate/query hooks, key-type validation and reference unwrapping, cancelled transaction diagnostic hook. Core preserves Legacy APIs.
- Telemetry helper integration for host method/root/load/reload/activation/native/routine/job/route spans, host principal hook, propagated remote parent, count instruments and shared resolve links. Remote/revoke and wire carrier ownership remain C-W; documents commit/rows ownership remains C-HD/Store.
- Preserve existing dispatcher, original callback ownership, ALC retention and late native cleanup.

## Authored tests and changed expectations

New `Host/Sync152KernelCases.cs` has nine cases: descendant old-call context and ordinary-call gate; reload timeout cut; detached-root provenance after parent settles; invalid-name atomicity; registry contributor suppression; typed context survival; old-getter reload cycle; nested version reason/public Forbidden; unattributed original native-closer join.

Child worker authored `UnboundClockTests.cs`: FIFO5s timer, latest error/next episode, stale timer dispatch, reentrant close.

Existing HostCases/ReloadCases/AdaptedCases/ReloadJobCases now assert explicit Load requirement, restart instead of swapped, changed attempt instance, ordinary new calls gate, old call completes before new factory opens, and every in-memory run aborts on reload.

## Validation status

Pending central build/runtime feedback. `git diff --check` found no whitespace errors (only repository CRLF normalization warnings). No passing test claim. Runtime regressions retain full-suite importance: old worker callbacks and remote ACK flows are concurrency-sensitive and all-restart is a deliberate behavior change.

## Integration feedback

Central full-r3 compilation passed with zero warnings/errors. Runtime exposed a postcommit cancellation failure: completed Task.WaitAsync ignored an already-cancelled token. Fixed with an explicit token check inside the committed-report error conversion. Updated the test cancellation trigger from a successor factory (now legitimately later than the report) to publication, matching frozen upstream host.test.ts1428. A separate Documents cancellation failure was routed to C-HD. These are not passing rerun claims.

Added public JoinOriginalsAsync(ChordContext) and core callback/resource tracking for WorkerMain/App cleanup, covering core callbacks without plugin attribution. App and worker owners were notified. Original root work and Self.TxAsync callbacks are tracked through actual completion.

A review found that clearing call-chain provenance at parent settlement breaks independent roots created with WithoutAbortSignal. Corrected to retain immutable chains of lightweight OldWorkState tokens rather than whole Attempts; tokens follow actual drain/seal state and avoid retaining retired plugin generations through disposed caller contexts.
