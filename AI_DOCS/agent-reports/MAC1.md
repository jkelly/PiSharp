# MAC1 — macOS native terminal drain fixture

Status: bounded fixture correction complete; central build and hosted Unix execution remain pending. Requested model/reasoning: gpt-6.1-sol/high; requested tier: Ultrafast, effective tier unverified.

## Evidence and diagnosis

Root supplied the actual failure from GitHub Native run `42544805898`, macOS job `128927362248`: compilation, startup and the first eight terminal cases passed; `native-terminal-drains-output-through-slow-consumer` timed out at its 20-second `Exited` wait. Windows and Linux native jobs passed. This worker did not execute macOS or independently download those hosted logs. The available log does not record native output chunk counts, so a specific macOS chunk size is not claimed.

The fixture's old `OnData` slept 150 ms for *every* native output frame while requesting 524,288 bytes. Its synthetic delay alone exceeds 20 seconds at 134 frames. Native `read` is permitted to return any available prefix: the managed 64 KiB buffer is a ceiling, not a required frame size. Thus the fixture imposed an OS/chunk-count-dependent deadline, independently of whether all bytes would eventually drain. The precise macOS chunk count remains unmeasured; hosted rerun is required to confirm this explains the observed failure.

Inspected actual implementation paths:

- `Native/pisharp_env_native.c`: `openpty`, native fork/exec, nonblocking descriptors, `waitid(WNOWAIT)` and eventual `waitpid` reap.
- `Native/UnixNativeProcess.cs`: native poll/read results determine output chunk sizes; disposal kills the retained group, closes descriptors, joins `Exit`, and reaps the leader.
- `Operations/TerminalOperations.cs`: output awaits each `EmitEventAsync`; `OutputPending` prevents the post-exit 100 ms silence grace from expiring during a pending transport delivery. The operation's `finally` disposes the owned terminal and joins its output before returning the result.
- `DaemonOutput.cs` and `ConnectionSession.cs`: event sends await actual frame writes; the client invokes `OnData` synchronously before reading the next frame/result. `RemoteTerminal.Exited` follows that ordered result.

No narrow production drain/ownership defect was established by this inspection, so native shim and daemon behavior are unchanged.

## Change

Only `tests/PiSharp.Env.Terminal.Tests/Program.cs` changes executable code. The existing named native case now gates the first consumer callback once, independently of frame count. The test observes entry, holds the callback for 350 ms (beyond the daemon's 100 ms drain grace), checks that `Exited` has not settled, and always releases the callback in `finally`. The callback also has its own 10-second bound, preventing an assertion failure from retaining the reader indefinitely.

The original 20-second completion bound remains. Immediately after `Exited`, the case checks exit code zero, exactly 524,288 bytes, and every byte equal to zero. It verifies the session leader is absent and the released terminal handle returns `EBADF`. A separate process-presence helper avoids treating a still-present zombie as proof of reaping. Failure messages now include actual bytes and frame count. Existing foreground-job close and disconnect ownership cases are preserved.

The test deliberately checks consumer backpressure and complete ordered delivery; it does not claim that the child always exits while the first callback is gated, since PTY/pipe capacity differs across systems.

## Checks and integration handoff

- Actual local check: `git diff --check -- tests/PiSharp.Env.Terminal.Tests/Program.cs` passed after the change. The same check including this report passed at handoff.
- Read-only diff/source review completed. No .NET builds or tests ran in this worker; main owns the .NET lease. No local macOS execution is claimed.
- Required central selection: build `tests/PiSharp.Env.Terminal.Tests/PiSharp.Env.Terminal.Tests.csproj`; run its executable normally for protocol/managed-daemon coverage, then use the existing native gate's `--external-daemon <published executable>` on Linux/macOS. The slow-consumer case is Unix-only; Windows execution does not qualify it.
- Required acceptance: rerun the existing macOS native job and require the complete terminal suite, including the unchanged later ownership/input/default-shell cases. If it fails, the new byte/chunk diagnostics distinguish incomplete delivery from per-frame timing assumptions without widening timeouts or skipping macOS.

No workflow, general process/tooling, metadata, website, Git index/branch, or remote mutations were made. Other agents' changes were preserved.
