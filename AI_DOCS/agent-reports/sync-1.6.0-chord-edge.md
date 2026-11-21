# chord-edge: Chord 1.5.2 -> 1.6.0, the part that does not depend on the kernel

Work package `chord-edge` of the Pi 1.6.0 sync, 2026-11-21. Branch `sync/1.6.0-chord-edge`, from `abac6b2c`. Upstream v1.6.0 `faac585884ae2c013a6b1400f3e9e77f10b34bac` against v1.5.2 `b4afdddf44e059c67ccf5097e5317d913521b470`, `packages/chord`. Nothing was pushed, tagged or published.

## 1. Commits

| Commit | Content |
|---|---|
| `4def9032` | Telemetry ingest admission: `TelemetryIngestOptions.Admit` |
| `afc7a076` | Client (`Limits`, `Follow`, starts offline), connection loop, `Variants.LiveIn` |
| `de687844` | Sync: archive slot after the remote's head, refused pull bodies released, fold, history |
| `7043133a` | App kit `Sessions` -> `SignIns`, `LinksOpened` close, README, regression cases |
| `0c69766a` | `TerminalScope.Variant` |
| (this file) | The report |

## 2. Suites

Pinned SDK (`artifacts/sample-tools/dotnet/dotnet.exe`), Release, built with the plan's command. Both ran in full on the committed state.

| Suite | Cases | Pass | Fail | Skip |
|---|---|---|---|---|
| `PiSharp.Chord.Tests` | 1048 | 1048 | 0 | 0 (the suite has no skip status) |
| `PiSharp.Chord.Hosting.Tests` | 55 | 55 | 0 | 0 |

24 of the Chord cases and 9 of the Hosting cases are new. No case failed, so nothing was compared against the base commit; the base suites were not run. The new and touched Chord cases (`surfaces.`, `sync.session.160`, `sync.files.160`, `telemetry-ingest.`) were run five more times in a row with no failure, because several of them wait on real timers and threads.

Not run: every other suite (Durable, CodingAgent, the samples). No file outside the two Chord projects' sources and tests changed, and a search of the repository found no other consumer of the changed API (`TelemetryIngestOptions`, `LinksOpened`, `Sessions`, `Client`, `ConnectionLoop`, `SessionSync`, `ISessionSyncStore`, `TerminalScope`).

## 3. Status per item

Statuses follow rule 3 of the plan.

### 3.1 The brief's items

| Item | Upstream | Status | Native | Cases |
|---|---|---|---|---|
| `links.opened(request, principal, close)` | `node/http/http.ts`, `upgrades.ts` (`5f89cc0a7`) | ported | `Hosting/Http/HttpContracts.cs` (`ChordHttpOptions.LinksOpened` takes `Action close`), `ChordHttp.cs` (aborts the socket while it is still tracked), `App/SignIns.cs` (`LinksOpenedAsync` takes it, unused, as upstream) | `http.160.links-opened-close-terminates-that-link`; `http.152.sockets-and-hooks` (arity) |
| `telemetryIngestFacet` requires `admit` | `telemetry-ingest/facet.ts` (`7ae9ff8c7`) | ported | `TelemetryIngest/Facet.cs` (`required Func<JsonObject?, bool> Admit`; refusal throws `Errors.Forbidden`), `Hosting/App/App.cs` (`Admit = _ => true`) | `telemetry-ingest.facet.160.refuses-page-of-unadmitted-principal` |
| `telemetry-ingest` export surface | `package.json` exports, `telemetry-ingest/index.ts` | already-ported | the public namespace `PiSharp.Chord.TelemetryIngest`: `TelemetryRecorder`, `TelemetryBatch`, `TelemetryIngest.PageBatch`/`WorkerBatch`/`PageTelemetry`/`WorkerTelemetry`/`CreateFacet` and the three constants, `TelemetrySender`, `TelemetrySenders.SendPageTelemetry`/`SendWorkerTelemetry` | `telemetry-ingest.160.export-surface-is-public` |
| `Sessions` -> `SignIns` | `node/app/sign-ins.ts`, `ephemeral-sign-ins.ts`, `ephemeral.ts`, `invite.ts`, `owner.ts`, `pairing.ts`, `app.ts` (`ff7414d51`) | ported | `App/SignIns.cs`, `EphemeralSignIns.cs` (renamed files), `EphemeralLogin.cs`, `Invite.cs`, `Owner.cs`, `Pairing.cs`, `App.cs`, `AppOptions.cs` | `app.160.sign-in-cookie-contract-and-document-names`; the renamed identifiers in `AppStartTests.cs`, `AppAuthRouteTests.cs` (four existing `app.152.*` cases) |
| `appInfoFacet` / `workerInfoFacet` | `node/app/index.ts` (`0db9861ec`) | already-ported | `App/Info.cs`: `Info.AppInfoFacet`, `Info.WorkerInfoFacet` are public | `app.160.info-facets-serve-a-host-of-its-own` |
| Client `Limits` | `surfaces/client.ts` (`34fb59f67`) | ported | `Surfaces/Client.cs` (`ClientOptions.Limits` -> `LinkOptions.Limits`) | `surfaces.client.160.part-limits-bound-what-arrives-from-the-server` |
| Client `Follow` | `surfaces/client.ts` (`22fe46c7b`) | ported | `Surfaces/Client.cs` (`ClientOptions.Follow`, default true) | `surfaces.client.160.static-client-applies-no-set-and-is-never-outdated`, `...static-client-reconnects-without-asking-the-base` |
| Client starts without its server | `surfaces/client.ts` (`3c771bd18`) | ported | `Surfaces/Client.cs`: `StartAsync` returns before the scripts object answers; the object is held (not got) and followed in the background | `surfaces.client.160.starts-offline-with-its-initial-scripts`, `...follows-the-scripts-object-once-the-server-answers`, `...applies-the-set-shown-when-it-starts-following`, `...applies-a-set-published-after-it-started` |
| Outdated: loop stopped first | `surfaces/client.ts` | ported (the order was already native; the reported state is new) | `Surfaces/Client.cs`, `Connection.cs` | `surfaces.client.160.another-base-at-a-reconnect-stops-the-loop-then-calls-outdated`, `...slow-base-answer-ends-the-next-link-before-outdated` |
| Connection loop | `surfaces/connection.ts` (`21e4cb80d`) | ported | `Surfaces/Connection.cs`: `CanRetry` asked beside the next attempt, one question at a time; immediate retry for a drop within 26 s of a wake, once per wake; `Stop` reports `Waiting` for the attempt it ended | five `surfaces.connection.160.*` cases; `surfaces.connection.wake-during-asynchronous-decision` (expectation updated) |
| `liveIn` | `surfaces/variant.ts` (`f57cd9484`) | ported | new `Surfaces/Variant.cs` (`Variants.LiveIn<T>`) | `surfaces.variant.160.live-in-is-the-reference-while-live-in-the-variant`, `...live-in-follows-a-rebind-to-another-variant` |
| `TuiScope.variant`, one watch per state | `tui/scope.ts` (`f57cd9484`) | ported | `Hosting/Terminal/TerminalScope.cs` (`Variant<T>`, `Follow`) | four `terminal.160.scope-*` cases |
| `useVariant` | `react/hooks.ts` | no-op: React, browser-only | — | — |
| `TuiScope.on` | `tui/scope.ts` (`8d30e6306`) | deferred to `chord-workers`: needs event members (`chord-remote`) | — | — |
| Sync: outbox off and on | `sync/client.ts`, `remote.ts` (`f472efdc1`) | ported | `Sync/SessionSync.cs` (`outbox.Floor > synced`; fold over every unfolded slot), `Sync/Remote.cs` (`History` restores from the newest archive slot) | `sync.session.160.goes-on-from-a-full-state-after-the-outbox-was-off-and-on`, `...finds-its-own-slots-when-its-sync-state-is-lost`, `...history-restores-from-the-newest-archive-slot` |
| `SessionSyncUploader` | `sync/remote.ts`, `client.ts` (`6268cc5cd`) | ported | `Sync/Remote.cs` (`ISessionSyncUploader`; `ISessionSyncStore` extends it), `UploadOptions.Store` | the upload cases above (their helper takes the uploader) |
| Sync: pull closes refused bodies | `sync/client.ts` `readBody` (`434dc5e4d`) | ported | `Sync/SessionSync.cs` (`ReadBodyAsync`: the body's enumerator is disposed whatever the reader did, also before its first read; a disposable body is disposed too) | `sync.session.160.pull-disposes-every-body-it-read-also-a-refused-one`, `...pull-leaves-no-file-open-for-a-body-it-refused` |
| Sync: a file body closes on return | `sync/filesystem.ts` `readerBody` | already-ported, differently | `Sync/FileSystemRemote.cs` opens the file at the first read and closes it when the enumerator is disposed, so a body never read holds nothing | `sync.files.160.body-closes-its-file-at-its-end-and-when-it-is-returned` |
| Peer dependency ranges | `package.json` | no-op: npm | — | — |
| Minified worker base | `node/workers/base.ts` | no-op: esbuild; not in this package's paths either | — | — |

### 3.2 The other upstream files in the owned paths

| Upstream | Status |
|---|---|
| `node/plugins/plugins.ts` (`unseen`: a build of named plugins never trusts the watcher's timing) | already-ported: `PluginManager.BuildAsync` runs discovery at every build. Case `plugins.160.build-rediscovers-before-the-watcher` |
| `node/plugins/install.ts` (npm 12 `pack --json`) | no-op: the native installer does not use npm |
| `node/surfaces/web.ts` `routes: false`, `file`, `pageAssets`, `base` | already-ported: `SurfaceBuild.Contribution` is opt-in, `GetFileAsync`, `Built.Base`, `BaseScript`/`BaseStyle`/`Set`. Case `publication.160.surface-without-routes` |
| `node/surfaces/web.ts` `tsconfig`, `pageBootstrap`; `bundle.ts`; `surface-build.ts`; `tui.ts` | no-op: esbuild options and the browser page's inline bootstrap |
| `node/surfaces/served.ts` `ASSET_EXTENSIONS` | already-ported: `SurfacePaths.Mime` maps all ten extensions |
| `node/app/layout.ts`, every `README.md`, `tui/index.ts`, `sync/index.ts`, `node/surfaces/index.ts` | no-op: comments, documentation, type exports |
| `node/app/app.ts` `AppWeb` | no-op: TypeScript typing of the web surface options |
| `node/app/app.ts` `store` into the workers component | not this package: `chord-workers` |
| `web/page.ts`, `react/*` | no-op: browser DOM and React |

### 3.3 Where the discovery report was wrong or incomplete

1. **The client's start needed a hold, not only a background task.** The report describes "start without awaiting the first script set". Native `FollowAsync` got the scripts object with `Host.GetAsync`, which gives up at the host's ready timeout; in the background that would have reported `update_failed` once and never followed. Upstream holds it. `Client.cs` now holds the object, waits for its live state and watches its member, and waits again if the binding was lost in between.
2. **`SessionSyncUploader` is not only a type export.** The report classes `sync/index.ts` as a no-op type export. Upstream's `uploadSession` takes that narrower type, so it is ported as `ISessionSyncUploader`.
3. **`liveIn` is internal upstream** (`surfaces/README.md`: "internal, what React's `useVariant` and `TuiScope.variant` return"). The report proposed, and the brief named, a public `Variants.LiveIn`; it is public here because there is no native binding other than `TerminalScope` and an application's own UI code has no other way to read a reference in one variant.
4. **More of the variant and scope tests need admission than the report says.** It names one case of `variant.test.ts` and none of `scope.test.ts`; two of the four new `scope.test.ts` cases call `admit` through the `desk()` fixture.
5. **An existing divergence the report does not mention:** native `ChordHttp` calls `LinksOpened` before `host.Accept`, upstream after. It was so at 1.5.2 and is unchanged; `close` works in either order.

## 4. Upstream tests

Counts are new or changed `it(` cases between the tags, in the brief's directories.

| Upstream test file | New or changed | Ported | Not ported, and why |
|---|---|---|---|
| `node/http/upgrades.test.ts` | 1 | 1 | — |
| `telemetry-ingest/facet.test.ts` | 1 | 1 | — |
| `telemetry-ingest/sender.test.ts` | 2 expectations changed | 0 | The sender's own hold becomes a `chord.hold` span when shared holds land: `chord-remote` |
| `node/app/{sign-ins,ephemeral-sign-ins,owner,pairing,ephemeral}.test.ts`, `node/tui/launcher.test.ts` | renames only | as renamed identifiers, plus one case pinning the names | — |
| `node/plugins/plugins.test.ts` | 1 | 1 | — |
| `node/plugins/install.test.ts` | npm 12 output | 0 | no-op: npm |
| `node/surfaces/web.test.ts` | 1 | 1 (native form) | — |
| `node/surfaces/bundle.test.ts` | 2 | 0 | no-op: esbuild `tsconfig` paths and asset loaders |
| `node/react/hooks.test.ts`, `node/web/page.test.ts` | several | 0 | no-op: React and browser DOM |
| `node/workers/*`, `node/fixtures/workers/*` | — | — | `chord-workers` |
| `surfaces/client.test.ts` | 6 new, 1 changed | 7 | — |
| `surfaces/connection.test.ts` | 3 new, 1 replaced, 1 extended | 5 | — |
| `surfaces/variant.test.ts` | 3 | 1 as written, 1 adapted | The adapted case rebinds by reconnecting as another principal, not through an admission, and does not cover the `denied` state or the per-state sequence (a native watch is delivered on another thread, so what is read inside it is not the state it was called for). The typing case has no C# counterpart |
| `sync/sync.test.ts` | 5 | 5 | — |
| `tui/scope.test.ts` | 4 | 3, two of them adapted | The two admission cases rebind by reconnecting as another principal. "A redraw per state" is asserted by counting watches on a reference the test controls, because a native watch may deliver two quick states as one call. The events case (`on`) is deferred to `chord-workers` |

Existing cases whose expectations changed, because upstream changed the behaviour:

- `surfaces.connection.wake-during-asynchronous-decision`: upstream replaced "retries at once when woken while canRetry is deciding". The retry is now scheduled beside the question, so the case asserts that a wake runs that pending retry at once and that a late yes opens nothing more.
- `surfaces.client.completed-predecessor-never-holds-gate-across-dispatch` and `surfaces.client.keeps-good-code-and-retries-newest-on-reconnect`: each now awaits `client.Followed` after `StartAsync`, which no longer waits for the scripts object.
- `http.152.sockets-and-hooks`: the hook's lambda takes three arguments.
- The three ingest cases that build the facet pass `Admit = _ => true`.

No case was deleted. The id `app.152.session-expiry-and-ephemeral-links` was kept (its method is `SignInExpiryAndEphemeralLinks`).

## 5. Seams left for other packages

| Seam | For | Where |
|---|---|---|
| `TerminalScope.On(member, listener)` | `chord-workers`, after event members exist | `Hosting/Terminal/TerminalScope.cs`; its header says the file is not yet at v1.6.0 |
| The admission forms of the variant and scope cases (rebind through `admit`, `failed`/`denied` reads as nothing) | `chord-workers` (or `chord-admission`) | `tests/PiSharp.Chord.Tests/Surfaces/Sync160ClientCases.cs` (`surfaces.variant.160.live-in-follows-a-rebind-to-another-variant`), `tests/PiSharp.Chord.Hosting.Tests/Sync160EdgeCases.cs` (`terminal.160.scope-variant-*`); both `Sync160EdgeSupport.cs` files hold the principal-driven desk fixture to replace |
| The workers component's `store` | `chord-workers` | `Hosting/App/App.cs`; its header says so |
| `telemetry-ingest.sender.page-through-memory-link-no-ingest-feedback` asserts a single `page.click` span | `chord-remote` (shared holds add a `chord.hold` span); it does not own `TelemetryIngestCases.cs`, so root reconciles | `tests/PiSharp.Chord.Tests/TelemetryIngestCases.cs` |

No contract or wire type from `chord-foundation` was needed: the refused ingest principal fails with the existing `forbidden` code, not the new `denied` failure kind.

`Client.Followed` is an internal task added for the tests (upstream's tests poll). `Hosting/Terminal/TerminalRuntime.cs`, which is not in this package's paths, calls `Client.StartAsync`: it now gets its client before the first script set was applied, so its `Render` callback can run before the server's plugins are loaded. `terminal.152.native-runtime` passes. Whoever owns that file should decide whether a terminal wants to wait for `Followed`.

## 6. Edits outside the owned paths

None. Every changed file is under `src/PiSharp.Chord.Hosting/{Http,App}`, `src/PiSharp.Chord.Hosting/Terminal/TerminalScope.cs`, `src/PiSharp.Chord.Hosting/README.md`, `src/PiSharp.Chord/{Surfaces,Sync,TelemetryIngest}`, `tests/PiSharp.Chord.Hosting.Tests`, `tests/PiSharp.Chord.Tests/{Surfaces,Sync}`, `tests/PiSharp.Chord.Tests/TelemetryIngestCases.cs` or the new `tests/PiSharp.Chord.Tests/Sync160EdgeSupport.cs`. `ChordSupport.cs` was not edited. The Hosting suite's `Program.cs` gained one contiguous block of nine lines after `terminal.152.native-runtime`, and one renamed method reference.

## 7. Breaking native API, for the release notes

Both assemblies are `[Experimental("PISHARP_CHORD001")]`.

1. **Sign-ins, not sessions. Everyone signs in again.** `Sessions` -> `SignIns`, `EphemeralSessions` -> `EphemeralSignIns`, `SessionKind` -> `SignInKind`, `SessionInfo` -> `SignInInfo`, `IssuedSession` -> `IssuedSignIn`, `App.Sessions` -> `App.SignIns`, `AppServices.Sessions` -> `AppServices.SignIns`, `AppLimits.EphemeralSessions` -> `AppLimits.EphemeralSignIns`. Persisted and wire names changed with them, as upstream, with no migration: the cookie `chord-session` -> `chord-sign-in`, the contract and the kit's core facet `chord.app.sessions` -> `chord.app.sign-ins`, the stored document `chord.app.sessions` -> `chord.app.sign-ins` and its field `sessions` -> `signIns`. After the update every browser cookie and device token stops authenticating, the owner token file is replaced at the next start (command-line clients read the new one), paired devices pair again, and the old `chord.app.sessions` document stays in the store unread.
2. `TelemetryIngestOptions.Admit` is required. A page whose principal it refuses fails to hold `chord.telemetry.page` with `forbidden`.
3. `ChordHttpOptions.LinksOpened` is `Func<HttpRequestContext, JsonObject, Action, Task<Func<Task>?>>`: the third argument terminates that link. `SignIns.LinksOpenedAsync` takes it too.
4. `Client.StartAsync` completes before the scripts object answered. A caller that relied on the server's first script set being applied when it returns no longer can. A failure to follow is reported as `update_failed`, not thrown.
5. `ConnectionOptions.CanRetry` (and so `ClientOptions.ServerBase` and `CanRetry`) is asked beside the next attempt, not before it, and one question at a time. `ConnectionLoop.Stop` reports `ConnectionState.Waiting` for the attempt it ended, so `OnState` and `ClientOptions.OnConnection` see a last `Waiting` at stop.
6. `UploadOptions.Store` is `ISessionSyncUploader`. `ISessionSyncStore` extends it, so callers compile unchanged; an implementer that wrote `PutBatchAsync` as an explicit `ISessionSyncStore` member must name `ISessionSyncUploader` instead.
7. Sync remotes: an archive slot can now follow other slots (an upload after the outbox was turned off and on). `SyncRemote.History` starts a restore at the newest one, and `FoldSessionAsync` folds every unfolded slot the snapshot covers. A 1.5.x client that reads such a remote refuses the archive slot with `HistoryUnavailableError`, so devices sharing a remote should update together.
8. `SessionSync.PullSessionAsync` disposes the enumerator of every body a remote returned, also of one it never read. A custom `ISessionSyncStore` whose body holds a connection releases it there.

Added without breaking: `ClientOptions.Limits`, `ClientOptions.Follow`, `Variants.LiveIn`, `TerminalScope.Variant`, `ISessionSyncUploader`.

## 8. For root

- Changelog and release notes: section 7, with the sign-in-again notice first.
- Applicability and integrity: one new source file (`src/PiSharp.Chord/Surfaces/Variant.cs`), two renamed (`App/Sessions.cs` -> `SignIns.cs`, `App/EphemeralSessions.cs` -> `EphemeralSignIns.cs`), four new test files. The 1.5.2 tables under `compatibility/` and `tools/CompatibilityReport/sync-1.5.2-source-headers.mjs` name the old paths.
- Headers: files brought to v1.6.0 carry the v1.6.0 commit. `Hosting/App/App.cs` and `Hosting/Terminal/TerminalScope.cs` keep the v1.5.2 line and add a v1.6.0 line naming what is ported and what `chord-workers` still owes; that package should collapse them when it finishes the file. `Hosting/Http/*.cs` and `Hosting/Plugins/PluginManager.cs` had no header before and still have none.
- `website/src/content/docs/docs/embed/chord.md` was searched and names none of the renamed types, the cookie, `LinksOpened`, the ingest facet or `Client.StartAsync`: nothing to change there for this package.
- `src/PiSharp.Chord.Hosting/README.md` was updated for `LinksOpened` close, the sign-in names and the ingest `Admit`; it does not yet describe `ProcessWorkers`' `Store`, which is `chord-workers`'.
