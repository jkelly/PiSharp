# Owned child shutdown qualification fixture

Authored source only; no build, child, SDK, runtime or test execution here. Exact base is terminal caller `1526b279474916428a0de1a86637d8d4d6eee447`, which includes reviewed core acknowledgment correction `ddd41e1f6f6c141b90a317ed1f2a0583323b896b`. Production and terminal files, assertions and held source declarations are unchanged.

## Identity and preparation

The existing framework-only `tests/PiSharp.CodingAgent.Tests/PiSharp.CodingAgent.Tests.csproj` builds the fixture. No new project, dependency, package lock, publish product, admission rule or security policy is introduced. Native entry identity: `OwnedChildShutdownFixture.Entry` in `PiSharp.CodingAgent.Tests.dll`. Child invocation identity: the allocated .NET host, the exact prepared `PiSharp.CodingAgent.Tests.dll`, `--owned-shutdown-child-fixture`, and the absolute fresh control root. Early dispatch returns before ordinary tests, provider construction and terminal worker dispatch. The child writes two fixed short outputs and waits for its owned release protocol; it starts no descendants.

Use the existing `StartupOwnedFiles` native consumer package/manifest/approval pattern, retaining all source/published output hashes. Change the dedicated package manifest entryType to `OwnedChildShutdownFixture.Entry` and id to `fixture.shutdown.owned-child`; use an empty enabledTools list and no invented tool declaration. Existing host API range/runtimeKind/tfm/rid and native execution approval checks apply. Copy the original consumer output graph for native loading as before. **The child's ChildAssembly must identify the original complete admitted consumer output**, including shared Contracts/Extensions.Abstractions and original deps/runtimeconfig, rather than the native snapshot package that omits shared host DLLs. Do not rebuild or patch published output during the test.

Create the baseline session before activating this fixture, using no extension. Otherwise session creation itself owns and disposes a child and waits for release. Activate the package only on the terminal invocation being qualified. The immutable runtime allocation must explicitly include these two new core cases and the terminal combined scenario's one child invocation/output graph. Existing build/publish and launch checks remain authoritative; adding source is not execution permission.

## Terminal worker integration points

Before the terminal invocation, create a fresh control directory and `fixture.owned` marker. Write a configuration JSON file containing exactly `Root`, `DotnetHost`, `ChildAssembly`, `FailCleanup` (true for the combined failure case). Set `PISHARP_SHUTDOWN_CHILD_CONFIGURATION` to that absolute file path within the test's scoped environment and restore its previous value in finally. Use the explicit already admitted host and original assembly paths. Initialization starts the one owned child with stdin explicitly closed and stdout/stderr redirected. The native fixture owns the actual Process handle; no PID discovery, PID kill, synthetic runner or fabricated receipt is used.

All markers below are inside Root. Marker waits may have diagnostic milestone deadlines; the original terminal command and fixture cleanup must still be directly joined in finally. `Entry.ReleaseAll(Root)` is the cleanup fallback and writes all four release/stop markers without disposing a borrowed terminal.

| Marker | Meaning or action |
| --- | --- |
| `owner.started` | Original exit and stdout/stderr tasks published; stdin closed. JSON processId comes from the actual owned Process handle. |
| `child.ready` | Physical child has written/flushed both fixed outputs. JSON processId comes from the child itself; require equality to owner.started and inequality to host PID. This is identity evidence, never a cleanup operation. |
| `dispose.entered` | Native extension disposal has actually begun. Must remain absent during held terminal restore. |
| `stop.requested` | Owner disposal requests the child's natural exit. |
| `exit.release` | Test writes this to let the child return after stop.requested. |
| `exit.joined` | Original Process.WaitForExitAsync has completed. |
| `stdout.eof`, `stderr.eof` | Original redirected ReadToEndAsync completed and exact output validated. |
| `stdout.release`, `stderr.release` | Test writes each to release that original owned stream cleanup task. |
| `stdout.closed`, `stderr.closed` | Actual StreamReader/redirected stream disposal attempted; injected close errors follow this operation. |
| `owner.joined` | Actual exit, both stream tasks and Process disposal all joined/attempted. |

Starting from the terminal worker's existing failing leave/restore case: wait child.ready before quit. Hold the original terminal restore, require dispose.entered absent and RuntimeCleanup incomplete, and preserve its writer ownership checks. Release restore with its original error. Require dispose.entered but original command/RuntimeCleanup still incomplete with physical child exit held. Write exit.release; wait exit.joined and both EOF markers. Release stdout only and require stdout.closed while original command/RuntimeCleanup remains incomplete and stderr.closed absent. Release stderr, directly await the original terminal command, RuntimeCleanup and Completion, and retain the terminal's original leave/restore exception instances.

With FailCleanup true, the fixture generates exactly three original IOException instances: `AUTHORED_OWNED_CHILD_EXIT_CLEANUP`, `AUTHORED_OWNED_CHILD_STDOUT_CLOSE`, `AUTHORED_OWNED_CHILD_STDERR_CLOSE`. The exit error is injected only after actual exit; stream errors only after each real redirected stream close. All are aggregated after every original cleanup task is joined. These are test-induced post-cleanup faults, not claims that the OS refused exit or stream close. Traverse AggregateException and InnerException causes in RuntimeCleanup, then require those **same exception objects** in Completion, alongside the existing terminal original error objects. Error status must remain failure and post-restore diagnostics must not repaint. ChildCleanup's Bash receipt list is not authority for this extension-owned child; do not invent a Bash receipt or PID. This qualification uses extension disposal's original task and actual error causes.

## Core fixture checks

Two new registered groups have prefix `two-phase owned physical child ` and directly await their originals. They hold physical exit and each redirected cleanup independently, retain the three injected original failures, and require repeated disposal to return the same task and exception. Finally always releases the owned protocol and directly joins disposal, even after a milestone/assertion failure. Fixed tiny outputs and explicit one-child ownership avoid unbounded output/descendant behavior. These checks are authored, uncompiled and unexecuted; they prepare the fixture, and the terminal worker still owns the combined terminal scenario. Existing 26 shutdown groups and terminal cases remain unchanged. No physical acceptance or original phase gate closes.
