# PiSharp Durable remaining features: qualification (M6)

The release evidence the roadmap ([plans/09](../plans/09-durable-remaining-features.md), M6) asks for: what was run for the work recorded in
[decision 0010](../decisions/0010-durable-application-contracts.md), with the actual results. Nothing here is a release: nothing was
committed, packed, published, deployed or installed.

## What was tested

- Branch `feature/durable-network` at `cd65d88`, plus the uncommitted working tree that holds the work of decisions 0009 and 0010. That
  state was later committed as `a85e264` (not pushed). The first follow-up below was run on `a85e264` plus its own changes, since
  committed as `feac749`; the second on `feac749` plus its own uncommitted changes.
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
| Removing a manifest binding is caught | `remote.manifest-cli-bindings-match-the-command-table` (DurableCli) removes a command and a manifest binding and expects both to be reported; `application-api.manifest-matches-routes-client-and-device-features` (Server) checks routes and client methods; `hub.manifest-web-bindings-match-the-hub-pages` (Web, follow-up) checks the `web` pages against the hub and expects a dropped page, an unknown page and a feature with neither pages nor a reason to be reported | Pass |
| `attach` survives a restart without duplicate input | `attach.survives-a-device-restart-without-duplicates-and-retries-with-the-same-request-id` | Pass |
| `doctor` is safe on damaged or old stores | `maintenance.doctor-reads-without-writing-and-survives-damaged-old-and-newer-stores` (byte-for-byte unchanged directory) and `ops.doctor-fails-a-weak-token-warns-a-wide-listener-and-never-prints-the-token` | Pass |
| A restore reproduces a watcher run and its evidence | `release-watcher.a-restored-backup-replays-the-run-and-its-evidence`, `maintenance.backup-restores-into-a-clean-root-and-detects-damage` and the backup steps of `ops.serve-stops-on-sigterm-and-doctor-backup-and-restore-work-around-it` | Pass |
| Service start and stop work | `ops.serve-stops-on-sigterm-and-doctor-backup-and-restore-work-around-it` starts `serve` with a service manager's arguments, delivers SIGTERM through the shutdown-signal path and expects exit 0 and a released lock; `ops.service-definitions-run-serve-hold-no-token-and-install-nothing` checks the three definitions | Pass, in process only. **Not run:** installing, starting and stopping a definition under systemd, launchd or Task Scheduler |
| The release evidence lists actual results | This record | Done |

## Follow-up: branch retrieval, browser coverage and the web manifest check

Run on 2026-10-21, same machine and toolchain, faux models and loopback fixtures only. Logs are under `artifacts/retrieval/final/`
(git-ignored). It adds `fetch_page` for job branches and research angles (decision 0010, Research), six browser cases for the application
pages and the web binding check.

| Suite | Cases | Passed | Failed | Skipped | Time | Notes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| `PiSharp.Durable.Hosting.Tests` | 114 | 114 | 0 | 0 | 10 s | Three new `branch-retrieval.*` cases, which also passed three runs in a row on their own |
| `PiSharp.Durable.Server.Tests` | 26 | 26 | 0 | 0 | 25 s | The research route case now sends `retrieval` and checks a bad origin's field error |
| `PiSharp.Durable.Web.Tests` | 9 | 9 | 0 | 0 | 8 s | New `hub.manifest-web-bindings-match-the-hub-pages` |
| `PiSharp.Durable.Web.BrowserTests` (headless Chromium) | 33 | 33 | 0 | 0 | 87 s | Six new `browser.applications.*` cases |
| `PiSharp.DurableCli.Tests` | 111 | 111 | 0 | 0 | 95 s | |
| `PiSharp.CliParity.Tests` | 195 | 191 | 0 | 4 | 36 s | Four `unix.*` cases run on Linux and macOS only |
| `PiSharp.DurableMessages.Tests` | 8 | 5 | 3 | 0 | 1 s | The same three pre-existing `native-initializer.*` failures as above |

The engine suite (`PiSharp.Durable.Tests`) was not run again: none of the projects it builds changed.

The browser cases cover, at 1440 × 900: publishing a policy version (and an invalid spec's field errors), evaluating it and approving the
pending judgment with a reason (`policies-publish-evaluate-and-decide`); saving request and money limits that survive a reload and
reconciling an uncertain reservation as not charged (`budgets-limits-and-reconciliation`); adding a feed source served on loopback,
checking it, reading its items and fetch receipts, editing, disabling, enabling and deleting it (`sources-add-check-edit-and-delete`);
starting research from the form with a page allowance, following it to completion and giving an artifact card more-like-this, labels,
undo and an empty-label error (`research-run-and-artifact-feedback`); and preference weights, examples, settings, forgetting an example
and reset (`preferences-weights-examples-settings-and-reset`). `phone-layout` visits the policy, budget, source, research, preference and
execution pages at 390 × 844 with data on each, dismisses an artifact from its card, and finds no error and no sideways scroll. The first
runs failed three times on the tests' own expectations (labels are shown sorted, the feedback API needs a `requestId`, and the labels box
keeps its text after undo), each fixed in the test; the product was not changed.

## Second follow-up: fetch display, device logs, stuck fetches and older devices

Run on 2026-10-21, same machine, toolchain and limits. Logs are under `artifacts/gaps/final/` (git-ignored). It adds the pages each
research angle fetched to the execution detail and the `/research` page (with a field for the sites angles may browse), `serve --log`
with size rotation for the launchd and Windows definitions, the doctor's `fetches` check, and a browser case for a device that reports no
features.

| Suite | Cases | Passed | Failed | Skipped | Time | Notes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| `PiSharp.Durable.Hosting.Tests` | 115 | 115 | 0 | 0 | 10 s | New `maintenance.doctor-reports-a-fetch-left-calling`; the research retrieval case checks the execution's pages |
| `PiSharp.Durable.Server.Tests` | 26 | 26 | 0 | 0 | 27 s | |
| `PiSharp.Durable.Web.Tests` | 9 | 9 | 0 | 0 | 8 s | |
| `PiSharp.Durable.Web.BrowserTests` (headless Chromium) | 34 | 34 | 0 | 0 | 89 s | New `browser.applications.an-older-device-shows-what-it-does-not-offer`; the research case fetches a page through a named site |
| `PiSharp.DurableCli.Tests` | 112 | 112 | 0 | 0 | 93 s | New `ops.serve-logs-to-a-rotating-file-that-backups-leave-out`; the service case checks `--log` |
| `PiSharp.CliParity.Tests` | 195 | 191 | 0 | 4 | 37 s | Four `unix.*` cases run on Linux and macOS only |
| `PiSharp.DurableMessages.Tests` | 8 | 5 | 3 | 0 | 1 s | The same three pre-existing `native-initializer.*` failures |

The older-device case intercepts the hub's `/v1/device` call in the browser and removes `features`, as a device from before decision 0010
reports none: each application page shows that the device does not offer it, the execution detail has no artifact cards, and the hub
calls no application route. It does not run an older build of the device. The first runs failed three times on the tests themselves (a
log read that did not share the file with its writer, a selector scoped to the wrong element, and a route pattern that matched the hub's
own `/research` page), each fixed in the test.

## Not run

- **Service managers.** No definition was installed or started: service installation was not authorized, and there is no Linux or macOS
  host here. A real `systemctl --user` or `launchctl` run, and `Stop-ScheduledTask` on Windows, remain to be tried by hand.
- **CI.** `.github/workflows/durable.yml` runs the engine, DurableCli, Hosting, Server and Web suites on Windows, Ubuntu and macOS. It has
  not run on this working tree, because nothing was pushed. The Unix-only paths therefore ran nowhere: the owner-only token checks and repairs of
  doctor and restore, and the CliParity `unix.*` cases.
- **Upstream reverse compatibility** (`PISHARP_DURABLE_UPSTREAM`), **benchmarks** (`docs/durable-benchmarks.md`), **live providers**,
  and the remaining test projects of the repository, which this work does not touch.
