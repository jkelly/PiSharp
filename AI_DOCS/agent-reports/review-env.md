# Independent Env correctness review

Review date: 2026-11-08. Read-only review of `src/PiSharp.Env.Protocol`, `src/PiSharp.Env`, and `src/PiSharp.Env.Daemon` in the active gap-implementation worktree. No source edits, builds, tests, live SSH, process launches, or native compilation were performed by this reviewer. Reports are the only files authored. I13 owns runtime validation.

## Confirmed findings

### ENV-1 — Blocking raw file operations prevent daemon shutdown [P1, source fix inspected]

**Location:** `src/PiSharp.Env.Daemon/EnvDaemonSession.cs:108`, reached from `DaemonFileSystem.cs`'s `open` operation and `src/PiSharp.Env.Daemon/DaemonMetadata.cs:31–32`.

**Trigger:** On Unix create a FIFO, open it through protocol `open { mode: "read" }` with no writer, then close daemon stdin or send SIGTERM/SIGHUP. The file worker blocks in `open(path, 0)` before it has returned an owned handle. Session cancellation cannot interrupt that synchronous native call. `RunAsync` cancels and cleans up resources, then awaits every pending operation forever, so `Program` never returns. An already-open raw FIFO with a blocking read presents the same class of shutdown problem.

**Why this is a regression:** Blocking FIFO opens are deliberately part of the wire contract (`packages/env/docs/protocol.md:96`); the defect is not that they block or that generic file-operation cancellation leaves them running. The frozen upstream `packages/env/daemon/src/main.rs` explicitly calls `process::exit(0)` after service cleanup because request threads can still be blocked in FIFO open, and its exit-signal handler kills owned process groups and exits. The C# executable instead waits uninterruptible pending work. The ordinary Connection client eventually force-kills an unresponsive local daemon, which limits that caller's hang but does not satisfy direct daemon EOF/signal semantics.

**Concrete fix:** E02 added `ShutdownDrainTimeout` (default two seconds), synchronous closure of new handle admission, independent resource disposal and a bounded drain of pending/output/timer work with observation of later completion. `Insert` disposes a descriptor arriving after closure. The raw blocking wire behavior is retained. Inspected these changes and the authored real-daemon Unix FIFO EOF/SIGTERM cases plus the portable late-resource cleanup case. Runtime execution remains with I13.

### ENV-2 — SSH cancellation and connection disposal can precede bootstrap cleanup [P2, source fix inspected]

**Location:** `src/PiSharp.Env/Ssh.cs`, `RunAsync` cleanup; `src/PiSharp.Env/Connection.cs:125` and `DisposeAsync` at lines 80–86 (locations before owner correction).

**Trigger and effect:** Cancel or time out an SSH detection/deployment command while it is running. `Ssh.RunAsync` requests `Kill(true)` but does not then join an uncancelled process-exit task; its cleanup only waits for stdout/stderr with a one-second wrapper timeout. Independently, `Connection.StartAsync` wraps the actual command-factory task with `WaitAsync(timeout.Token)`, and `Connection.DisposeAsync` only joins established sessions. Thus the connection can report disposal complete while its cooperative SSH factory is still killing/reaping/draining the bootstrap subprocess. This is a real supplied-factory path, not an assumption about an arbitrary callback ignoring cancellation.

**Concrete fix:** E07 now joins uncancelled process exit, cancels remaining pipe reads and awaits both original readers. E01 tracks startup attempts and actual factory tasks separately; disposal cancels the connection, joins starts so late factory registration is included, then joins factories and sessions. One disposal task is retained. `ShutdownTimeout` defaults to five seconds and explicitly faults disposal for a noncooperative custom factory instead of claiming successful cleanup. Inspected both corrections and their authored cancellation/disposal checks. E01 also moved owner loss publication before failed-request completion so an immediate next request cannot reuse stale readiness. Runtime execution remains with I13.

## Contract improvement found and corrected during review

`RemoteTextLineReader` originally sent an offset on every `pread` even for a nonregular stream opened with `mode: "read"`. The daemon therefore called `RandomAccess.ReadAsync` on a FIFO/pipe and failed rather than reading sequentially. This behavior is also present in the frozen upstream text reader, so it is recorded as completion of D15's explicit nonregular-text-reader contract, not as a newly introduced upstream-parity regression.

E03 changed `src/PiSharp.Env/RemoteFileSystem.Readers.cs:117,138–148` to use positional reads for regular files and sequential reads otherwise. For sequential streams it buffers already-consumed bytes/decoder state before returning a canceled result so retry does not lose a line. The fix was statically inspected. E03 reports an authored nonregular/cancel regression; no execution is claimed here. Blocking raw-read shutdown remains ENV-1.

## Reviewed paths and conclusions

- **Framing and connection:** inspected strict UTF-8/object decoding, 9-byte minimum/16 MiB maximum frame length, complete-frame invalid JSON handling, startup token synchronization, session IDs on every handle/control request, monotonically increasing request IDs, failed-request handling and lazy reconnect. An accepted mutation is not replayed on connection loss. Cancellation is queued after the original request and bounded locally by `AbortGrace`; older-session requests fail instead of reaching a replacement daemon.
- **Filesystem and paths:** inspected independent remote Windows/POSIX path normalization, drive-relative and UNC cases, `file://` handling, byte-preserving payloads, regular-file no-follow admission, opened-handle metadata across rename/replacement, ordered chunk writes and close, short positional reads, cancellation buffering, listing wire counts, raw directory names and bounded listing payloads. `listFiles` clamps to the protocol payload limit through the existing LocalFileListing implementation.
- **Daemon scheduling/output:** inspected request registration before dispatch, file-worker bounds, per-handle/process-input predecessor chains, immediate terminal-control enqueue, complete-frame output serialization, control-first versus bulk scheduling, quota waiters, oversized watch downgrade and independent cancellation of owned operations. No additional concrete scheduler/frame corruption defect found.
- **Exec/process/PTY:** inspected suspended Windows launch and job assignment before resume, inherited-handle restriction, persistent stdin, nonblocking Unix pipe/PTY wrappers, C-only fork-to-exec boundary and exec-error pipe, `WNOWAIT` leader retention before group kill/reap, output drain and end-marker handling, spill preservation and output-window accounting. The recent independent abort flag checks correctly allow ordinary abort after kill. Unix process-group cleanup intentionally does not promise to contain descendants that create their own sessions/groups, matching the documented boundary.
- **Watches:** inspected target snapshotting, ready-before-change order, terminal errors, reconnect only on transport loss, overflow on regained coverage, stopped-callback suppression and joined reconnect delay cancellation.
- **SSH and artifact integrity:** inspected structured local argv, restrictive SSH options, explicit isolated host-key scan and acceptance, changed-key refusal, shell quoting, PowerShell encoding, immutable catalog membership/hash checks, remote verify-before-launch, temporary upload verification and rename, platform/version hello validation and explicit Android backend provenance. No additional confirmed integrity or quoting defect found.

## Boundaries and pending owner checks

This is static review against D15/E02–E07 reports, actual implementation/callers/tests, and the frozen upstream checkout at `7d073328e44337d3292a7db9b0f0308f679c5e97`. Windows/Unix/macOS native execution, FIFO behavior, native static linking and real SSH were not validated here. No compile-in-flight declaration gaps are counted as findings.

E01/E07 confirmed the bootstrap cleanup ownership gap recorded in ENV-2. A caller-supplied factory that ignores its token is not itself evidence of a product regression; the concrete supplied SSH factory's cleanup is the trigger above.

The earlier runtime review's Web fixes (RUNTIME-2 and RUNTIME-4), including the follow-up router admission/disposal fence, have now been statically rechecked and recorded as source-resolved in `review-runtime.md`; runtime validation remains pending.
