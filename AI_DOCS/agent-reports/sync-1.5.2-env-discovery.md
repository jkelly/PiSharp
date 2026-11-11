# D-ENV: v1.5.0 → v1.5.2

Frozen reference: f9a26365568127461c47a07a37dcb42c140a3ac5 → b4afdddf44e059c67ccf5097e5317d913521b470. Static source comparison; no builds run. Requested ultrafast tier is unverified.

## Complete upstream inventory and applicability

| Upstream paths | Change | Native mapping/status |
| --- | --- | --- |
| packages/env/CHANGELOG.md, README.md, docs/protocol.md | 1.5.1 piped-process API and lifetime; abort/start/timeout and Windows reader fixes; 1.5.2 header | Contracts below; release metadata owned by root |
| packages/env/src/process.ts (new), index.ts | RemoteProcess, openProcess, options; public export | Already ported: src/PiSharp.Env/RemoteProcess.cs |
| packages/env/src/connection.ts | Abort startup wait independently; cancel grace 1000 ms then remove pending and reject aborted | Already ported: Connection.WaitForStartAsync, ConnectionSession.AbortAfterGraceAsync; ConnectionModels.AbortGrace defaults one second |
| packages/env/src/remote-env.ts | Pass cancellation to path resolution/info; timeout covers startup and silent daemon | Already ported: RemoteShell.cs, RemoteExecutionEnv.cs, most RemoteFileSystem paths. Remaining context-free info/rename resolution should forward their available context token |
| packages/env/src/list-files.ts | Abort feature discovery | Already ported: RemoteFileSystem.Listing.cs ListFilesAsync |
| packages/env/src/ssh.ts | ConnectTimeout=15 | Already ported: SshTarget.ConnectTimeout and Ssh.Arguments |
| packages/env/src/watch.ts, list-files.ts, remote-env.ts | Chord Context import becomes /context | TS packaging only; C# DurableContext unchanged |
| packages/env/daemon/src/process.rs (new), main.rs | process/processWrite/processCloseStdin, feature advertisement and ordered synchronous admission of follow-up requests | Mostly ported: Operations/ProcessOperations.cs, Program.cs. Gap: follow-up operations currently independently Task.Run and write directly to OS pipe, violating ordered immediate queue acceptance |
| packages/env/daemon/src/exec.rs, pty.rs | Shared launch/argv/target helpers, waiter/group cleanup, reusable readers | Native process helpers already provide equivalent behavior; no Rust daemon exists in PiSharp |
| packages/env/daemon/src/sys/unix.rs | Cancellable 100-ms nonblocking writes; process group killed before leader reaped | Native/UnixNativeProcess.cs and pisharp_env_native.c implement poll, WNOWAIT, kill/reap |
| packages/env/daemon/src/sys/windows.rs | Explicit overlapped I/O cancelled with CancelIoEx; own non-breakaway process job and kill on exit | Native/WindowsPipedProcess.cs already uses asynchronous named pipes and suspended launch into private job before resuming, stronger than upstream's documented assignment race |
| packages/env/test/process.test.ts, remote-abort.test.ts (new), differential.test.ts | Process bytes/EOF/order/cleanup; startup/sent abort/timeout; Context import | Existing Env.Process/Connection/Adapter/Ssh tests cover broad behavior; add raw backpressure + admission-order regression |
| packages/env/package.json, tsconfig.build.json | Version/dependency/export packaging; remove build exclude | Root version/pin work; no C# runtime equivalent |
| packages/durable/src/env/index.ts, node.ts, node-watch.ts; packages/durable/src/testing/env-conformance.ts | Only Context import path changed | No behavior change in Durable Env, LocalExecutionEnv, LocalProcess or EnvConformance |

## Exact contracts

- Wire framing/version remain protocol 1. Advertise `process` in hello.features. Older daemons fail open with `not_supported` before requesting process. No new FrameCodec fields are required.
- `process {argv,cwd,env?,inheritEnv?}` runs argv directly. started(pid) precedes raw per-stream output; result is exitCode. Output is backpressured. Exit kills owned descendants; Unix escaped sessions/groups remain outside ownership. Cancel reports aborted; kill mode reports exit status. Loss kills owned group/job. Drain until both EOFs or 100 ms of output silence after exit.
- `processWrite {target}` and `processCloseStdin {target}` must be accepted in frame arrival order, enqueue data/EOF, and return immediately without waiting for the program to read. Dedicated writer drains FIFO; EOF follows earlier bytes; input behind EOF is discarded. Once writer closes or program ends, requests fail EBADF. Broken writer is not a process error. Stop cancels blocked input and joins writer ownership.
- Existing C# RemoteProcess API: OpenAsync(Connection, RemoteProcessOptions, CancellationToken); Pid:int, Exited:Task<int>, Write(byte memory/string), CloseStdin(), Close(), plus async variants. Preserve API and callback-byte ownership. MCP worker can adapt this to its PipeTransport without adding an Env dependency to MCP core.
- Aborting startup only ends that waiter and reports RemoteError(aborted); shared startup continues. Sent cancellation preserves daemon result/error for one second, then removes pending and reports aborted, ignoring late replies. Remote exec local timeout includes startup and maps local cancellation to timeout; caller cancellation remains aborted. SSH default connection deadline 15 s.
- Windows reader cancellation uses actual asynchronous I/O cancellation; no CancelSynchronousIo fallback. Existing named-pipe implementation satisfies this structurally; runtime evidence must come from native suite on Windows.

## Authorized implementation split

Root approved D-ENV implementation after report. This worker owns Env/Env.Protocol/Env.Daemon and their tests. Implement queue admission in EnvDaemonSession.cs + ProcessOperations.cs and deterministic tests in Env.Process.Tests/Program.cs; forward missing filesystem connection-wait tokens. No changes to shared solution, metadata, generated inventory, historical pins or Durable Env are necessary. Other workers must not touch these paths. Root serializes all builds.

Regression requirements: raw queued write acknowledgement while child never reads; concurrent queued writes/EOF preserve frame order; commands remain cancellable under backpressure; EOF and late writes; existing process bytes, wrapper cleanup, Windows readers, startup/sent abort and local timeout suites. Do not claim tests passed until root runs them.

## Implementation handoff

Implemented the queue contract in EnvDaemonSession.cs and ProcessOperations.cs. The frame reader admits processWrite/processCloseStdin; a dedicated channel reader writes input with cancellation, drops queued bytes after EOF, closes stdin and is joined on process cleanup. Queued payloads are copied before replying. Forwarded existing context cancellation to the remaining rename destination, temp-file metadata and directory metadata connection waits; mutation requests retain their established settlement semantics.

Added `raw-input-acknowledges-before-reading-and-preserves-frame-order` to Env.Process.Tests. A child waits on a filesystem gate while 48 raw 64-KiB frames and EOF must all be acknowledged, then echoes the exact ordered bytes after the gate opens; input after exit must return EBADF. This bypasses RemoteProcess's own write semaphore, so it exercises daemon scheduling and proves acknowledgements do not wait for pipe consumption. Existing blocked-input cancellation test exercises the new writer's cleanup.

`git diff --check` passed. Builds/tests intentionally not run (root serializes). Root should run Env.Process.Tests plus Env.Connection.Tests, Env.Adapter.Tests, Env.Ssh.Tests, Env.Daemon.Tests and Env.Terminal.Tests. The native process tests require platform runtime execution; Linux/macOS coverage cannot be claimed from Windows-only validation. No Env.Protocol or Durable Env code changes were needed.

Validation follow-up: first root-controlled Env.Process run exposed an EOF timeout. The payload-copy conditional inferred Memory<byte>'s nullable array conversion, changing null EOF into empty memory. Fixed by explicit nullable initialization and separate non-null byte assignment; root-controlled rerun requested. The existing first binary/EOF case caught this regression.

Root validator subsequently reported all 11 Env.Process cases passing, including raw queued input ordering, in receipt `sync152-env-process-r2-83cd1ab7f2f84eb3a9522a492eb01cd6`.

## Android pin audit follow-up

Updated the active downloader and `docs/env-ssh.md` from 1.5.0 to 1.5.2. Added `tools/Env/upstream-android-1.5.2.json`; preserved historical 1.5.0 catalog and reports. Exact public registry metadata is from `https://registry.npmjs.org/@earendil-works%2fpi-env/1.5.2`; downloaded archive is `https://registry.npmjs.org/@earendil-works/pi-env/-/pi-env-1.5.2.tgz`.

- Registry and calculated archive SRI: `sha512-yw9dPqPHHP567m7+TDAmuzWC9AU1I3aV3FFdNQlo+UXASYGEIW4QgbYXNTka6Y/Z4+KcifSdgDSiMiTM3ZXBfg==`.
- arm64 fixed member `package/bin/pi-env-android-arm64/pi-env`: 2,054,328 bytes; SHA-256 `1a91f482f18fc2ef96aa5b5b74a84a989f9ae8fe3ad5cd0f2f7565383489e12c`; ELF64 little-endian machine 183 (AArch64).
- x64 fixed member `package/bin/pi-env-android-x64/pi-env`: 2,373,456 bytes; SHA-256 `d446b5d2e477f1128448fe5738bd689adbd65a6dedc1816fa5a77aa4a6b56dd4`; ELF64 little-endian machine 62 (x86-64).
- `tar -tvf` inspected the 49 archive members before extraction. Both fixed files are ordinary executable files. The downloader now checks each name occurs exactly once and is an ordinary file before extracting either file; validates SRI before inspection/extraction and SHA-256/ELF afterward.
- Metadata, archive, and extracted fixed binaries are only under ignored `artifacts/upstream-env-v1.5.2`. Running the updated downloader against that verified archive succeeded and produced its catalog. No npm installation, package script, downloaded binary, or Android runtime was executed. No .NET build was run by this worker.
- Source commit `b4afdddf44e059c67ccf5097e5317d913521b470` identifies the reviewed v1.5.2 source tag. Binary hashes verify the released artifacts; they do not claim independently reproducible compilation from the tag.
