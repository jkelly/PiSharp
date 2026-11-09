# ENV4 daemon realpath fixture

Status: source correction complete, root owns .NET execution and hosted qualification. Worker requested hard/high sol, ultrafast; effective tier unverified. No .NET builds/tests, Git operations or runtime edits performed.

## Actual evidence and contract

The macOS gap job `128951109014` in run `42552547130` crashes in its first daemon case at the exact `realpath` equality assertion. Actual artifact: `artifacts/release-prep/candidate-ci/f473-gap-env-macos/env/125c5fe582b64d389d0b44c305794801/PiSharp.Env.Daemon.Tests.tests.stderr.log`. The subsequent `6cc-gap-env-macos/env/34669eaf791e4e6e8341c9e9fb108f8d` artifact repeats the same assertion failure. Neither stderr log records the returned path; `/var` versus `/private/var` is the source-backed diagnosis, not a claimed observed return value.

`DaemonFileSystem.ExecuteAsync` delegates `realpath` to `LocalFileSystem.CanonicalPathAsync`, which invokes physical `RealPath`; this intentionally resolves ancestors. The fixture previously built its expected file path from lexical `Path.GetTempPath()`, which can include the macOS `/var` symlink. Exact equality to a lexical alias contradicts physical canonical-path semantics.

## Correction and controls

Only `tests/PiSharp.Env.Daemon.Tests/Program.cs` changed:

- After creating the unique temporary directory, Unix fixtures obtain its physical spelling through independent `/bin/pwd -P`, with the created directory as `WorkingDirectory`. The oracle does not call the production canonicalization helper. Windows retains its existing root unchanged.
- The oracle starts the fixed executable directly (`UseShellExecute=false`, structured `ArgumentList`), reads redirected stdout/stderr concurrently, requires successful exit/empty stderr and an existing absolute directory, and removes only pwd's final LF. Exit has a 10-second deadline; finally kills an unfinished owned process and bounds exit/stream joins. No shell, global environment mutation, arbitrary delay or path-string substitution.
- The original exact daemon realpath equality remains and now reports expected/actual on failure.
- A Unix control creates a directory symlink in an ancestor, writes a file through that alias, independently resolves the linked directory through pwd, requires the physical and lexical paths to differ, and checks exact daemon resolution of the complete file path. This exercises ancestor resolution rather than merely the final component.
- Existing ten case identities, all filesystem/descriptor/version/protocol assertions, raw FIFO shutdown cases and cleanup remain. The existing short private pipe lease at the bottom and REL2 version checks were preserved.

Review checkpoint product SHA256 (unchanged by ENV4):

- `src/PiSharp.Env.Daemon/DaemonFileSystem.cs`: `cebcce9428bde21d91346f238292c54037a08598c3208d07279bf1105acddf4d`.
- `src/PiSharp.Durable/Env/Local/LocalFileSystem.cs`: `062862f45c5ea4f7ac4751bb686803751b3193d2bc357ac32a80a3539e3f946a`.

## Validation handoff

Root should build/run full `PiSharp.Env.Daemon.Tests` on Windows, Linux and macOS; Unix execution must exercise the ancestor-symlink control, and macOS must verify the original path assertion against the independent oracle. Earlier native Windows/Linux/macOS AOT success at f473 is separate evidence and does not prove this managed fixture fix. No updated passing execution is claimed by ENV4; source edits are frozen for root's lease.
