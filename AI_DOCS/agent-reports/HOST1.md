# HOST1 — Unix hosting and socket portability

Status: source-coherent correction complete; central builds and hosted Windows/Linux/macOS execution remain pending. Requested model/reasoning: gpt-6.1-sol/high; requested tier: Ultrafast, effective tier unverified.

## Evidence and fixes

Read `CI1.md` and the release plan. CI1's actual `3024b1e0` artifacts identify Unix `PlatformNotSupportedException` from `WaitAny` with a named mutex, Unix loopback HTTP 403, and macOS socket paths exceeding the 104-byte address bound. This worker did not rerun or independently redownload the hosted failures.

`ProcessAddressLock.AcquireAsync` now checks cancellation and waits in 50 ms `Mutex.WaitOne` intervals on its existing dedicated ownership thread. Acquisition, abandoned-mutex recovery, the post-acquisition cancellation check, and release all run on that same thread. Cancellation after acquiring releases the mutex in `finally`; failed acquisition joins the worker before disposing the release event. Mutex construction also runs inside the error-reporting `try`, so a constructor failure settles the acquisition task. No wait combines the named mutex with a cancellation wait handle.

`ChordHttp.AdmitAsync` now applies its proxy-form rejection only to explicit case-insensitive `http://` or `https://` absolute targets. A Unix origin-form `/` is no longer mistaken for an absolute file URI. Host validation, relay verification, loopback address/port checks and foreign-origin rejection are unchanged.

`LegacyProcessDriver.cs` adds internal `PrivatePipeDirectory`, reused by `ProcessWorkers` through the existing Chord Hosting friend assembly. On Unix it creates `/tmp/psc-<32 hex GUID>` with user-only read/write/execute permissions, and uses absolute `{data,control}` socket paths within it. These ASCII addresses are at most 49 bytes, independently of `TMPDIR`. Servers retain `CurrentUserOnly`. Windows retains opaque unique pipe names. The lease is held by the owned worker/legacy instance, and its nonrecursive directory deletion follows stream disposal and process cleanup; startup failure also disposes streams/processes before deleting the directory. No public API or assembly-friend metadata is added.

This uses an explicit .NET capability: [.NET 10 PipeStream.GetPipePath](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/PipeStream.Unix.cs#L169) accepts rooted paths unchanged specifically to avoid the temporary-directory prefix and Unix socket path limit. [Directory.CreateDirectory(path, UnixFileMode)](https://learn.microsoft.com/en-us/dotnet/api/system.io.directory.createdirectory?view=net-10.0) creates directories with the requested Unix permissions. The runtime sources and API documentation were actually opened; no Unix runtime execution is inferred from them.

The three native Chord pipe fixtures use the same address lease and retain `CurrentUserOnly`. The Env Daemon `Wire` pipe factory uses its own short `/tmp/pse-<GUID>/wire` path with a 0700 directory and `CurrentUserOnly`, retaining no new project dependency. Its cleanup deletes the private directory only after session/stream cleanup, including failure paths.

## Regression coverage and owned diff

Existing case identities/counts remain unchanged:

- Hosting `http.routes-wraps-origin-and-anonymous-webhook`: mock requests now populate `RawTarget`, matching real ASP.NET requests; `/` reaches routing, an origin-form query containing an HTTP URI is admitted, and explicit HTTP/HTTPS proxy-form targets are rejected. Existing unlisted-host and foreign-origin controls remain.
- Hosting `http.real-loopback-listener-and-websocket`: existing physical `/` HTTP and WebSocket listener acceptance remains the end-to-end control.
- Hosting `workers.address-lock-waits-and-cancels`: retains contended cancellation, adds actual waiting followed by release, pre-canceled acquisition/reacquisition, and recovery after a dedicated fixture thread abandons a named mutex.
- Chord `link.transports.framed-pipe-duplex`: retains raw transfer and adds absolute path/<104-byte checks on Unix, directory mode/uniqueness, and absence after stream/lease cleanup.
- Chord `legacy.link.native-pipe-transfer` and `legacy.link.native-websocket-transfer`: retain the large packed pipe/WebSocket transfer assertions with portable private addresses.
- The existing real worker stop/restart/startup cleanup and seven Legacy.Cluster manager cases exercise both production generators.
- Env Daemon's existing ten integration cases use the corrected pipe factory.

Executable files changed:

1. `src/PiSharp.Chord.Hosting/Workers/ProcessAddressLock.cs`
2. `src/PiSharp.Chord.Hosting/Workers/ProcessWorkers.cs`
3. `src/PiSharp.Chord.Hosting/Http/ChordHttp.cs`
4. `src/PiSharp.Chord/Legacy/Manager/LegacyProcessDriver.cs`
5. `tests/PiSharp.Chord.Hosting.Tests/Program.cs`
6. `tests/PiSharp.Chord.Tests/Legacy/SessionCases.cs`
7. `tests/PiSharp.Chord.Tests/Link/LinkCases.cs`
8. `tests/PiSharp.Env.Daemon.Tests/Program.cs` (`Wire` factory/ownership only)

## Actual checks and required handoff

Read-only diff review and scoped `git diff --check` passed. Source search confirms the owned mutex no longer uses `WaitAny` and the confirmed production/fixture pipe generators no longer rely on long TMPDIR-prefixed names. No .NET build or test ran here; main owns that lease. No local Unix/macOS execution is claimed.

Main should build/run `PiSharp.Chord.Hosting.Tests`, `PiSharp.Chord.Legacy.Cluster.Tests`, and `PiSharp.Env.Daemon.Tests`; build/run `PiSharp.Chord.Tests` with `--only link.transports.framed-pipe-duplex,legacy.link.native-` for focused coverage, followed by required full hosted suites. Hosting must receive the actual pinned dotnet host via `PISHARP_TEST_DOTNET`, as CI1 documented. Unix qualification should retain the normal long macOS TMPDIR so the absolute-path fix is exercised independently of any CI environment workaround.

No general Tools/process/shared JSON, CI/workflow, website/version metadata, Git index/branch, remote or memory writes were made. MAC1's earlier terminal fixture change remains separately documented.
