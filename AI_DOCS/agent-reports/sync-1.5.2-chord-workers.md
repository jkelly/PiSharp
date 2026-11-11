# C-WK — worker bases and link recovery

Implementation in `src/PiSharp.Chord.Hosting/Workers` against the frozen 1.5.2 worker source and reviewed discovery contracts. No `OwnedChildProcess`, project files, common test registration, or unrelated source was edited. Requested ultrafast tier is not independently verifiable.

## Public seams

- `ProcessWorkers` construction remains synchronous and registers the route before host load.
- `BuildAsync(): Task<WorkersBuilt>` is cached, never rejects, and reports `Kind = "built"` with `Inputs`/optional `Runtime` identity or `Kind = "failed"` with `Error`. `Built` remains null until completion. The first address start awaits this build; a failed base fails only that component's consumers before their plugin/start-data callbacks run.
- `ProcessWorkerOptions.ApplicationDirectory` optionally names the complete deployment tree; the default is the managed entry/executable directory. `BaseDirectory` is a parent for unique owned artifact directories, never a directory emptied by the component. `TimeProvider` supplies the recovery clock.
- `WorkerBase.BuildAsync(WorkerBaseOptions)` captures deployment bytes without filesystem writes. `Inputs` uses `BaseInputs.FromHashes` of the actual captured bytes. `PrepareLaunch()` first writes a unique snapshot, remaps the managed entry or native executable, preserves arguments, and maps an in-tree working directory. Later launches reuse those bytes. Missing temporary snapshots get a new directory from the same bytes. Ownership markers prevent deleting a directory whose ownership changed.
- `TryLockIdleAsync(Address, ChordContext): Task<IDisposable?>` atomically reserves an idle address and then acquires its existing `ProcessAddressLock`. A null result means a worker/start/other lease exists or the final-source 2000 ms external lock timeout elapsed. Caller/closing cancellation still propagates. Routed starts wait for lease disposal. Close joins these leases. C-A1/C-A3 have this seam for idle archives.
- `WorkerContext.OnClosing(Func<ChordContext, Task>)` and `OnClosed(...)` register reverse-order cleanup hooks. Closing hooks run before host/link closure (e.g. bounded telemetry flush); closed hooks run after `ChordHost.JoinOriginalsAsync(Background)` and `PluginRuntime.JoinOriginalsAsync()` (e.g. files/store disposal). This includes core/non-ALC callbacks and retained plugin generations. A public timeout does not trigger disposal under an original closer; the supervisor remains the outer hard process bound.

## Native snapshot boundary

The complete application deployment tree is captured, including application executable/DLL, deps/runtimeconfig, satellite/native assets and private lazy-loaded assemblies. Reparse points, external runtimeconfig probing paths and startup-hook/additional-deps/shared-store environment injection are rejected. Build/VCS/dependency-source directories (`obj`, `.git`, `.svn`, `node_modules`) are excluded. The scan is bounded at 10,000 files and 512 MiB by default.

For `dotnet app.dll` and `dotnet exec app.dll`, the explicitly selected absolute dotnet host remains external; its executable path, SHA256 and file version are recorded in `WorkerBase.Runtime` and its bytes are a watched input. This follows root's explicit approval to keep the immutable platform installation external rather than copy hundreds of MiB of shared frameworks. The app's runtimeconfig/deps and every private deployment dependency are frozen. Self-contained launches copy their executable and local runtime. OS dependencies/shared framework installations must remain installed. Arbitrary assembly loads from absolute paths outside the deployment tree cannot be intercepted by an ALC and are explicitly unsupported; this is an artifact snapshot, not a permission sandbox. No Node compile-cache or BSD socket-lock behavior is claimed.

## Lifecycle and recovery

The component tracks actual child cleanup independently of the current address entry. Public close cancellation only cancels the wait; its shared close task still joins starts, process descendants, original pipe readers/cleanup and archive leases before deleting snapshot files. A build finishing after close never materializes files. New starts and materialization are gated synchronously when close is invoked.

`ObservedLink` records supervisor closure before notifying the protocol listener, so holder-driven resolution can queue behind the old child's full cleanup. A known local protocol/heartbeat closure marks a ready worker as link-lost, gracefully stops it and joins it. Remote pipe EOF gets an exit grace so an ordinary crash retains failed-until-retry semantics. A ready process surviving EOF is stopped as link-lost. Recovery records its timestamp only after process exit: one recovery is permitted, a further loss less than one minute later leaves public `worker lost its link`; exactly one minute allows another. Recovered events report `WorkerProcessException.Kind = "worker_link_lost"`; other failures retain `worker_failed`. Fatal/start/crash failures remain failed until explicit retry/rollout. No worker is proactively forked without routed demand.

## Authored validation

`tests/PiSharp.Chord.Hosting.Tests/WorkerSnapshotCases.cs` adds:

- `CapturesDeployment`: build writes nothing; changed/deleted executable/private DLL remains original at launch; restored bytes clear dirty; disposal preserves sibling/successor files and refuses later writes.
- `FailedBuildAndClose`: stable shared failed build task, no per-address callbacks, server local service still works, no cache output.
- `CloseDuringBuild`: build finishing after close creates no files and stays cached.
- `LaterWorkerAndCancelledClose`: actual copied .NET test deployment; source tree deleted after first worker; later asynchronous native closer loads the retained private DLL; cancelled close waiter and a crossed 100 ms native close deadline leave snapshot available until the original completes and process actually exits.
- `LinkRecovery`: actual worker processes, protocol closure through the same internal link used by heartbeat; held reference gets replacement, injected clock allows recovery at exactly one minute, immediate next loss remains failed.
- `IdleLockSerializesStart`: external mutex ownership times out as busy; idle archive lease defers actual routed fork and refuses to misclassify a running worker as idle.

Root owns adding these methods to common `Program.cs` and the child dispatch (`--worker-snapshot-fixture`), and all serialized builds/runs. No dotnet build or test was run by C-WK. `git diff --check` for owned sources passed before handoff. Existing real-process crash/retry, startup shutdown, reload-busy idle and mutex tests are retained.
