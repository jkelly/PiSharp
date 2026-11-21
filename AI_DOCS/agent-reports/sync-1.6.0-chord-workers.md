# chord-workers: Chord 1.5.2 -> 1.6.0, the workers component, TerminalScope.On

Work package `chord-workers` of the Pi 1.6.0 sync, 2026-11-21. Branch `sync/1.6.0-chord-workers`, from the integration tip `4db09d16` (every other Chord package merged). Upstream v1.6.0 is `E:/AI/PiSharp/upstream-v160` (`faac585884ae2c013a6b1400f3e9e77f10b34bac`); the plan was `sync-1.6.0-chord-discovery.md` (the `chord-workers` row) and the "what chord-workers needs" sections of the admission, store, remote and edge reports. Upstream code was checked for every item; where a report and upstream differed, upstream won (section 5).

Status: DONE. Everything is committed on the branch; nothing was pushed, no pull request, tag or publication was made, no live provider was called and no credential was used.

## 1. Commits

| Commit | What |
| --- | --- |
| `7240024e` | `ProcessWorkerOptions.Store` and `.Admit`, `ProcessWorkerState.Incarnation`, the exit published after the link's work ended (`ObservedLink.End`, the settled wait), failures recorded on the child and published with its exit, `App.cs` passes the server's store; the seven construction sites; `Sync160WorkerCases.cs` (six cases) and the `--worker-160-fixture` worker |
| `cc246dbe` | `TerminalScope.On(IEventMember, Action<ChordEvent>)`; headers of `TerminalScope.cs` and `TerminalRuntime.cs` at v1.6.0; the scope case |
| `45cc8c2b` | `src/PiSharp.Chord.Hosting/README.md`: the workers' `Store`, `Admit`, exit order and incarnation |
| (last) | This report |

## 2. Per item

| # | Item | Upstream | Status | C# | Cases |
| --- | --- | --- | --- | --- | --- |
| 1 | `store`, required; the exit waits for the store to settle before it is published | `workers.ts` `WorkersOptions.store` L72, `#exited`/`#left` L666-715, `#stop` L759 | ported | `ProcessWorkerOptions.Store` (`required IStore?`; a store not from `Tables.OpenStoreAsync` is refused with `ArgumentException`), `ProcessWorkers.WatchExit` awaits `ChordStore.SettledAsync()` after the link ended and before the publication; `StopAsync` (and the idle stop and `CloseAsync`) end once the exit was published | `workers.160.queued-commit-lands-before-the-exit-is-published`, `workers.160.stop-resolves-after-the-exit-was-published` |
| 2 | `admit`, passed to the route; a denied principal starts no worker | `workers.ts` `admit` L58, `createWorkers` L188 | ported | `ProcessWorkerOptions.Admit` (`RouteAdmit?`), `Admit = options.Admit` in the `host.Route(kind, ...)` call. `Resolve` returns the worker's own link, the final provider of its address, as `RouteOptions.Admit` requires | `workers.160.passes-admit-to-its-route` |
| 3 | `WorkerState.incarnation` | `workers.ts` L85-92, `#opened` L605, `stateOf` L984 | ported | `ProcessWorkerState(..., string? Incarnation = null)`: set from the resolved link's `Opened` on the host's dispatcher at the handshake, before anything the worker sent is handled; null before it and for a failed address | `workers.160.incarnation-before-the-first-call` |
| 4 | The link ends at exit, before the exit is published, told as neither lost nor closed | `process.ts` `TellingLink.end` L153, `ended` L81 | ported | `ObservedLink.End()` (closes without telling the owner's observer) and `ObservedLink.Ended` (completes once the protocol listener returned, so the host has aborted the worker's inbound calls); `WatchExit` ends the link and awaits `Ended` before the settled wait | `workers.160.link-ended-at-exit-told-as-neither-lost-nor-closed`, `workers.160.report-and-exit-in-the-same-turn-commits-nothing` |
| 5 | `ResolvedLink(link, options, opened)` | `workers.ts` `Resolved` L296 | ported (the record is chord-remote's) | `ResolveAsync` returns `new(child.Link, options, incarnation => ...)` | as 3 |
| 6 | `TerminalScope.On(member, listener)` | `tui/scope.ts` `on` L91 | ported | `TerminalScope.On(IEventMember member, Action<ChordEvent> listener)`: `member.OnAsync(listener, scope context)`; a rejection is swallowed (released or disposed); the subscription's `Stop` is also kept for `Dispose`; events never call `changed` | `terminal.160.scope-on-calls-the-listener-until-dispose` |
| 7 | `TerminalRuntime` / `Client.Followed` | `tui/runtime.ts` (unchanged at v1.6.0), `surfaces/client.ts` `startClient` | no-op, decided | upstream's `startTui` renders right after `startClient`, which no longer waits for the scripts object; `TerminalRuntime.StartAsync` does the same, so it does not wait for `Client.Followed`. Header moved to v1.6.0 with that note | `terminal.152.native-runtime` (unchanged, passes) |
| 8 | `app.ts` passes `store` into the workers component | `app.ts` L620 | ported | `App.cs`: `Store = store` | `app.152.*` (unchanged, pass) |
| 9 | Headers and README | — | done | `App.cs` and `TerminalScope.cs` collapsed to one v1.6.0 header; `TerminalRuntime.cs` moved; `ProcessWorkers.cs` and `ObservedLink.cs` had no header and now carry one; `README.md` "Native workers" | — |
| 10 | The upstream test deltas | `workers.test.ts` +5 cases and one changed, `process.test.ts` +1, `lock.test.ts` helper, `scope.test.ts` `on` | ported (section 3) | `tests/PiSharp.Chord.Hosting.Tests/Sync160WorkerCases.cs`, registered in `Program.cs` | seven cases |

Other upstream files in the area:

| Upstream | Status |
| --- | --- |
| `node/workers/README.md` | no-op: upstream documentation (the package README carries the native text) |
| `node/workers/base.ts` (minified bundle), `base.test.ts` +1 | no-op: esbuild; the native base is a byte snapshot |
| `lock.test.ts` (temporary directory under `/tmp` for `sun_path`) | no-op: native address locks are named mutexes, there is no socket path |
| `workers.test.ts` "a start that hangs after connecting" (waits for the incarnation before advancing fake timers) | no-op: a vitest fake-timer flake fix in a case with no native counterpart (native has no fake-timer start-timeout case) |
| `fixtures/workers/child.ts` (a grandchild inheriting fd 3) | no-op as a fixture: native pipe handles are not inherited and the owned process's tree stop runs before `Exit` completes; the end path is covered by the `ObservedLink` case |
| `fixtures/workers/control.ts`, `worker.ts`, `support.ts` | ported as the native fixture: `Sync160WorkerCases.RunChildAsync` (`--worker-160-fixture`), `Reports`, `Control` (`pid`, `crash`, `report`, `reportAndExit`), `reportAtStart`; `Fleet`/`FleetAsync` with `store`, `admit`, `facets`, `onError` |
| `test/surfaces/variant.test.ts`, the `scope.test.ts` variant cases | already-ported: chord-edge, converted to the admission form by chord-admission (`99d31c4d`) |

## 3. The ported cases and where they differ

| Native case | Upstream | Notes |
| --- | --- | --- |
| `workers.160.passes-admit-to-its-route` | workers.test.ts "passes admit to its route" | as written: denied fails `Denied` with `forbidden` "no workers for you", nothing started and no state entry; allowed starts one |
| `workers.160.report-and-exit-in-the-same-turn-commits-nothing` | "a worker that reports and exits in the same turn" | upstream's aborted transaction ends `unavailable`; natively the handler's call is cancelled through its context's token (a .NET token carries no reason), so the case asserts "not committed" and an empty journal rather than the code. No `holdsChannel` (see the child.ts row); also asserts no `worker_link_lost` |
| `workers.160.queued-commit-lands-before-the-exit-is-published` | "a commit a report queued in the store before the exit lands..." | upstream's `holdNextCommit` is internal to `PiSharp.Chord`; the case holds the commit queue with a public store export and detects the queued commit by the queue's tail (`SettledAsync`) changing. Asserts the state equals the running entry (record equality, incarnation included), no error and one start while held; the journal held `["queued"]` when the crash was reported; no second start. The report's own wait ends `cancelled` or `committed` (its caller's abort is not the commit's) |
| `workers.160.stop-resolves-after-the-exit-was-published` | "a stop resolves once the exit was published" | as written, with the export hold |
| `workers.160.incarnation-before-the-first-call` | "a worker's incarnation" | the native `State` is a polled snapshot, not a watchable, so "published at the handshake" is what the startup report's handler reads there (`startup starting known`); the rest as written (a crash clears it, a retried run has another) |
| `workers.160.link-ended-at-exit-told-as-neither-lost-nor-closed` | process.test.ts "closes its link at its exit..." | over `ObservedLink` on a memory link pair (reflection: the type is internal and the test project has no `InternalsVisibleTo`): `End` tells the protocol listener only, a close by this side is told lost, one by the peer closed, and an end after either changes nothing |
| `terminal.160.scope-on-calls-the-listener-until-dispose` | scope.test.ts "a scope's event subscriptions" | as written |

Each new case failed or could not compile before its change: with the settled wait removed, the queued-commit case saw the state published early and the stop case's stop resolved early (checked by hand, then restored).

## 4. Behaviour changes beyond the upstream lines, and why

Making "a worker's entry goes or turns failed only after its link's work ended" true natively needed three more changes in `ProcessWorkers.cs`, all found by the ported cases:

- **Failures are recorded on the child and published with its exit.** A `fatal`, a lost link before `ready` and a closed control pipe used to write `entry.Failure` at once, so `State` showed `failed` (and `OnError` fired) before the exit and before the store settled: with the settled wait removed, the queued-commit case saw `worker control disconnected` published at the crash. They now set `Child.Cause`/`CauseError`, which `WatchExit` publishes, as upstream's `child.cause` is. A closed control pipe whose process exits within `CloseTimeout` is that exit (`worker exited (code N)`); one whose process lives on is killed after that grace, its exit `worker control disconnected` (it was killed at once; upstream's `linkClosed` grace is 5 s).
- **A holder's replacement start waits for the publication.** Before the link ends, `WatchExit` clears the entry's start and points `Previous` at the child's `Finished`, so a holder that resolves again when the link closes starts after the exit was published; a crash recorded meanwhile rejects that start without reporting again (upstream throws before its `try`), and leaves nothing starting (an idle archive lock can still take the address). A clean stop keeps the entry when such a replacement is pending.
- **`ObservedLink.Ended`** is awaited before the settled wait, because the data pipe's own EOF can close the link on the reader thread while the host is still handling that close.

## 5. Where the reports and upstream differ

- Discovery 5.2 says the exit should "close the link" before publishing; upstream ends it silently (`end`, not `close`), told by neither `linkLost` nor `linkClosed`, which the port follows.
- Remote's 5.8 finding (a link close aborts inbound calls; a transaction under the aborted context is refused `transaction_cancelled`, or `released` when the object ends with the link) holds here: the report-and-exit case's aborted report never commits. What the handler's own await sees is a cancelled call.
- `Client.Followed`: the edge report left the choice open; upstream's terminal runtime does not wait (item 7).

## 6. Suites

Pinned SDK `E:/AI/PiSharp/PiSharp/artifacts/sample-tools/dotnet/dotnet.exe`, Release, each with no filter, `PISHARP_TEST_DOTNET` set to the pinned host.

| Suite | Run 1 | Run 2 |
| --- | --- | --- |
| `PiSharp.Chord.Hosting.Tests` | 62 cases, 62 pass, 0 fail, 0 skip | 62 cases, 62 pass, 0 fail, 0 skip |
| `PiSharp.Chord.Tests` | 1508 cases, 0 failures (1498 authored, 10 upstream captures), 0 skip | 1508 cases, 0 failures, 0 skip |
| `PiSharp.Chord.Durable.Integration.Tests` | 7 of 7 pass | — |

`PiSharp.Chord.Hosting.Tests` was 55 cases at the base; the seven new ones make 62. No other test project references `PiSharp.Chord.Hosting`; the sample `samples/chord/NativePlugins/Host` (which does) builds with 0 errors.

## 7. Deferred

Nothing in this package's scope.

## 8. For root

- Release notes and changelog: the name is `ProcessWorkerOptions.Store` (confirmed; `required IStore?`). Add `ProcessWorkerOptions.Admit` (`RouteAdmit?`), `ProcessWorkerState.Incarnation` (a trailing positional parameter: source-compatible, binary-breaking), `TerminalScope.On(IEventMember, Action<ChordEvent>)`, and the behaviour lines: `StopAsync` returns once the exit was published; a fatal, a lost link before ready and a closed control pipe show in `State` only with the exit; a worker whose control pipe closes while it lives on is killed after `CloseTimeout`, no longer at once. The `<!-- sync-1.6.0: confirm ... -->` markers in `CHANGELOG.md` and `website/src/content/changelog/1.6.0.md` can go.
- `website/src/content/docs/docs/embed/chord.md` L37 describes the `ProcessWorkers` options: add the store and admission.
- Integrity table and source inventory: new `tests/PiSharp.Chord.Hosting.Tests/Sync160WorkerCases.cs`; headers added to `src/PiSharp.Chord.Hosting/Workers/{ProcessWorkers,ObservedLink}.cs`; headers moved on `App/App.cs`, `Terminal/{TerminalScope,TerminalRuntime}.cs`.
- No file outside this package's paths was edited.
