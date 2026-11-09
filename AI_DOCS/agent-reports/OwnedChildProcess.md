# Owned child process facade integration

Transferred the N1-authored `Tools/Processes/OwnedChildProcess.cs` to D16 ownership and refined it without changing N1/legacy consumer signatures. No N1 consumer/package files edited.

Preserved: `StartAsync(ProcessStartInfo, CancellationToken)`, `Id`, stdout/stderr StreamReaders, `Exit`, `HasExited`, `ExitCode`, `WaitForExitAsync`, `StopAsync`, `OwnReader`, `IAsyncDisposable`. Added `LeaderExit` (leader only) and `StandardInput` (only when RedirectStandardInput=true). `Exit` means leader exit plus confirmed descendant stop; `HasExited` follows this public task. Wait cancellation cancels only that wait, not ownership. Stop has stable task identity under concurrent callers.

Both platforms reuse existing atomic ownership. Windows is suspended before its private non-breakaway job assignment. Linux/macOS commands enter their private group via posix_spawn before execution, with an anchor retaining the group identity after leader death. There is no PID enumeration/global kill fallback; Unix descendants that deliberately escape the group remain outside its receipt.

Launch snapshots argv/environment/cwd and requires an existing absolute executable, explicit ArgumentList, no shell/raw argument string, and complete NUL-free arguments/environment. Redirected input stays open until the caller closes it; other launches retain EOF. Native admission defaults for existing tool runners remain unchanged. Failure after native acquisition disposes the owned process; uncertain cleanup and original failures are reported together.

Dispose physically stops the tree, observes Exit, joins all registered original reader tasks, then disposes native handles. Readers get a five-second normal-drain opportunity before owned read sides are closed to unblock an escaped pipe writer. An arbitrary user callback that remains held still owns its original task until it settles; the facade does not claim cleanup by abandoning it. Reader aggregate faults retain the original fault objects.

Narrow primitive changes: optional internal keep-open stdin in WindowsProcessLifetime and PosixSpawnProcessAdmission; Unix launch cleanup also reaps a successfully spawned command if later acquisition fails. No new public low-level process admission surface.

Authored tests live in new `tests/PiSharp.Tools.Tests/OwnedChildProcessTests.cs`, using existing private child/control fixtures through a partial class. Registered three groups:

- `owned child natural parent exit stops retained descendants before completion`: leader starts descendants, exits through controlled IPC; tree completion requires every owned PID gone, output preserved, and repeated stop reuses its task.
- `owned child stop dispose races join original readers and preserve faults`: canceled wait leaves the process running; eight concurrent stops + disposal join original readers; a deliberately held reader prevents disposal and its exact eventual fault survives.
- `owned child interactive stdin and prelaunch admission preserve bytes`: UTF-8 interactive bytes and EOF, quoted/Unicode argv, supplied environment/cwd, default input EOF; precanceled/raw-argument launches never touch the child marker file.

Validation request to I13: Tools suite `--filter "owned child"`; existing `--filter "native process"` and `--filter "Unix "` are primitive compatibility controls. N1 and legacy own their consumer regressions. No independent .NET builds or test runs executed; runtime acceptance is pending central validation. `git diff --check` is clean for owned edits.

## Runtime-review follow-up: retained Unix pipe ends

Review identified that closing an ordinary Unix FileStream cannot interrupt an already-blocked read if an escaped descendant keeps its writer open. Fixed only facade launches to use internal `PosixOwnedPipeStream`: checked O_NONBLOCK, short nonblocking syscalls synchronized against descriptor closure, and cancellation-aware asynchronous waits on EAGAIN. Existing ordinary runner/clipboard transports stay unchanged. Redirected facade stdin uses the same nonblocking write strategy; Stop closes its owned stdin so an escaped reader cannot retain a blocked write indefinitely. Stream operations remain borrowed: callers retain/join their originals or register them with OwnReader.

Added fourth case `owned child blocked pipe reads and writes retire without leaked syscalls`: an actual owned child fills a redirected stdin pipe without reading it, then Stop retires the original write. On Linux/macOS it additionally holds opposite kernel pipe ends open directly while disposing pending nonblocking reads/writes; every original must settle promptly. No escaped process/global-PID cleanup is needed for this fixture. Windows execution only qualifies its own pipe path; the POSIX branch needs native Linux/macOS execution.

The Darwin arm64 fcntl setter explicitly places its variadic argument in the first stack slot; other platforms use their normal integer argument ABI. This follows [Apple's ARM64 calling convention](https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms); nonblocking read/write behavior follows the [Linux pipe contract](https://man7.org/linux/man-pages/man7/pipe.7.html). Darwin arm64 runtime verification remains pending a native host; no physical-host pass is inferred from Windows tests.

## Central validation update

`artifacts/gap-validation/Tools-full-stable4-072bca980eb4459a9f2e7ba1a5f1a19d/tests.stdout.log` records all four owned-child groups passing on Windows. The full Tools run had one unrelated legacy ls-limit expectation, assigned to I07. Native Unix retained-end operations remain unqualified on this Windows run.

The normal-analyzer NativeAOT publish found CA1416 at the Windows stop callback: its delegate escapes the outer platform guard. Added an explicit `OperatingSystem.IsWindows()` guard at the actual deferred invocation before the native job call, without suppressions. Source refrozen for I13's publish rerun; no independent build executed.
