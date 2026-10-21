# PiSharp Durable remaining features: qualification (M6)

The release evidence the roadmap ([plans/09](../plans/09-durable-remaining-features.md), M6) asks for: what was run for the work recorded in
[decision 0010](../decisions/0010-durable-application-contracts.md), with the actual results. Nothing here is a release: nothing was
committed, packed, published, deployed or installed.

## What was tested

- Branch `feature/durable-network` at `cd65d88`, plus the uncommitted working tree that holds the work of decisions 0009 and 0010. No
  commit records this state yet.
- Windows 11 Pro 10.0.26300 (x64), .NET runtime 10.0.11, Release builds. `global.json` pins SDK 10.0.401, which is not installed on this
  machine, so every project was built with the installed 10.0.400 SDK's `MSBuild.dll` directly and each test executable was run with
  `dotnet <assembly>`.
- 2026-10-20. Faux models, recorded fixture HTTP on loopback and manual clocks only: no provider, network service or paid call.
- Logs are under `artifacts/m6/` in the working tree (git-ignored, so they stay on this machine).

## Results

| Suite | Cases | Passed | Failed | Skipped | Time | Notes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| `PiSharp.Durable.Tests` (engine) | 3157 | 3153 | 0 | 4 | 338 s | 195 of the passes compare with upstream captures. Skipped: `compat.reverse.upstream-reads-pisharp-sessions` (`PISHARP_DURABLE_UPSTREAM` unset) and three `plan.symlink-escape.*` cases (this account cannot create symbolic links) |
| `PiSharp.Durable.Hosting.Tests` | 111 | 111 | 0 | 0 | 11 s | 72 at B0 |
| `PiSharp.Durable.Server.Tests` | 26 | 26 | 0 | 0 | 25 s | 16 at B0 |
| `PiSharp.Durable.Web.Tests` | 8 | 8 | 0 | 0 | 8 s | 4 at B0 |
| `PiSharp.Durable.Web.BrowserTests` (headless Chromium) | 27 | 27 | 0 | 0 | 75 s | Second run. The first run had 26 passes and 1 failure, fixed as described below |
| `PiSharp.DurableCli.Tests` | 111 | 111 | 0 | 0 | 93 s | |
| `PiSharp.CliParity.Tests` | 195 | 191 | 0 | 4 | 42 s | Four `unix.*` cases run on Linux and macOS only |
| `PiSharp.DurableMessages.Tests` | 8 | 5 | 3 | 0 | 2 s | Pre-existing failures, see below |

### Failures found and fixed during qualification

- `application-api.retention-reports-a-dry-run-and-purges-in-bounded-batches` failed: `GET /v1/retention` returned an empty body. Its
  expression-bodied `async` handler bound as a `RequestDelegate`, so its result was discarded. Both retention routes now use the API's
  context-only handler helper (`H`), as the other single-parameter routes do.
- `browser.automation.execution-history-and-audit-details` failed once: the execution detail renders its audit before its artifact cards
  load (added in M2), and the page is inert until both are done. The test typed into the inert filter and its Refresh click was ignored.
  The test now waits until the page is no longer busy before each interaction. The browser automation cases then passed three times in a
  row, and the whole suite passed (the row above).

### Failures not fixed

`PiSharp.DurableMessages.Tests` fails `native-initializer.actual-loader-install-and-retire`,
`native-initializer.foreign-registration-factory-zero-effects` and `native-initializer.foreign-mcp-factory-zero-effects` with "Published
native configuration is invalid." The same three cases fail the same way when the suite is built and run from a clean extract of
`cd65d88` (`git archive`), so they predate this work. This branch changes nothing under `src/PiSharp.Cli/Extensions`. The project is in
neither `PiSharp.slnx` nor CI. Its assembly is named `PiSharp.CodingAgent.Tests.dll`.

## M6 acceptance gates

| Gate | Evidence | Result |
| --- | --- | --- |
| Removing a manifest binding is caught | `remote.manifest-cli-bindings-match-the-command-table` (DurableCli) removes a command and a manifest binding and expects both to be reported; `application-api.manifest-matches-routes-client-and-device-features` (Server) checks routes and client methods | Pass |
| `attach` survives a restart without duplicate input | `attach.survives-a-device-restart-without-duplicates-and-retries-with-the-same-request-id` | Pass |
| `doctor` is safe on damaged or old stores | `maintenance.doctor-reads-without-writing-and-survives-damaged-old-and-newer-stores` (byte-for-byte unchanged directory) and `ops.doctor-fails-a-weak-token-warns-a-wide-listener-and-never-prints-the-token` | Pass |
| A restore reproduces a watcher run and its evidence | `release-watcher.a-restored-backup-replays-the-run-and-its-evidence`, `maintenance.backup-restores-into-a-clean-root-and-detects-damage` and the backup steps of `ops.serve-stops-on-sigterm-and-doctor-backup-and-restore-work-around-it` | Pass |
| Service start and stop work | `ops.serve-stops-on-sigterm-and-doctor-backup-and-restore-work-around-it` starts `serve` with a service manager's arguments, delivers SIGTERM through the shutdown-signal path and expects exit 0 and a released lock; `ops.service-definitions-run-serve-hold-no-token-and-install-nothing` checks the three definitions | Pass, in process only. **Not run:** installing, starting and stopping a definition under systemd, launchd or Task Scheduler |
| The release evidence lists actual results | This record | Done |

## Not run

- **Service managers.** No definition was installed or started: service installation was not authorized, and there is no Linux or macOS
  host here. A real `systemctl --user` or `launchctl` run, and `Stop-ScheduledTask` on Windows, remain to be tried by hand.
- **CI.** `.github/workflows/durable.yml` runs the engine, DurableCli, Hosting, Server and Web suites on Windows, Ubuntu and macOS. It has
  not run on this working tree, because nothing was pushed. The Unix-only paths therefore ran nowhere: the owner-only token checks and repairs of
  doctor and restore, and the CliParity `unix.*` cases.
- **Upstream reverse compatibility** (`PISHARP_DURABLE_UPSTREAM`), **benchmarks** (`docs/durable-benchmarks.md`), **live providers**,
  and the remaining test projects of the repository, which this work does not touch.
