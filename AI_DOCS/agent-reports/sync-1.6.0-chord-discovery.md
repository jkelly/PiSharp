# chord-discovery: Chord 1.5.2 -> 1.6.0 decomposition

Discovery only, 2026-11-21. No product or test source was edited and nothing was built or run. Everything below is from reading source: upstream v1.6.0 `E:/AI/PiSharp/upstream-v160` (`faac585884ae2c013a6b1400f3e9e77f10b34bac`) against v1.5.2 `E:/AI/PiSharp/upstream-v152` (`b4afdddf44e059c67ccf5097e5317d913521b470`), and the native tree in this worktree. Five read-only sub-analyses (store, host queries, host admission/events, link/remote, hosting/surfaces/sync) supplied the function-level detail; I spot-checked their native claims (the `Derive.Invalidate` double-listing, the hardcoded 256 call window, the `LiveQueries` callers, test auto-discovery, Durable's use of the Chord store) but did not re-verify every line range. Line numbers are for the v1.6.0 upstream file unless a native path is given.

Abbreviations for features: **CS** column sets and the read index, **Q** named queries and lists, **F** one-frame-per-commit fixes, **E** events, **CW** call windows by trust, **SH** shared holds, **A** admission, **RA** route admission, **D** delete host data by plugin, **O** `opened(incarnation)`, **W** worker exit/`store`/`incarnation`, **SI** sign-ins rename.

## 1. What changes the plan

1. **The changelog is incomplete.** The source between the tags also holds features with no `[1.6.0]` bullet. Under the owner's "port everything" rule these are in scope:
   - **Events** (fire-and-forget members) and **call windows by trust** (commit `8d30e6306`): new `host/events.ts`, `host/reference-events.ts`, a new wire message kind `event`, `ack.events`, optional `hello.calls`, `HostOptions.callWindow` (default 10,000) and `eventWindow` (32). The changelog only hints at it in `memberNames(...)` returning `events`.
   - **Shared holds** (`47e9d74f9`, `90ed3e3b1`): remote references of one host to one object share one wire hold. No wire change, but a rework of `remote/remote-object.ts`, `host/reference.ts` and `host/host.ts` `#bindRemote`.
   - **Sign-ins, not sessions** (`ff7414d51`): the app kit's `Sessions` became `SignIns`, including the contract and document name (`chord.app.sign-ins`), the document field (`signIns`) and the cookie (`chord-sign-in`). Upstream's `docs/decisions.md` says there is no migration: everyone signs in again.
   - **Client and connection loop** (`3c771bd18`, `21e4cb80d`, `34fb59f67`, `22fe46c7b`): a client starts without its server, `follow: false`, part limits for client links, reconnect at once after a wake.
   - **Store** `ChordStore.settled()` and `Store.onCommitted(listener, onError)` (five new `store.test.ts` cases); `settled()` is what the breaking `createWorkers({ store })` uses.
2. **Protocol version stays 1.** The wire gains one message kind (`event`, binary kind index 18, text tag `"E"`), one optional field (`ack.events`, binary flag `0x20` on `ack`), one field made optional (`hello.calls`; absent means no window) and one failure cause (`denied`, binary cause byte 3, text `"d"`). Every 1.5.2 frame still decodes. The reverse does not hold: a 1.6.0 trusted side sends `calls` absent, which a 1.5.2 peer rejects or reads as a zero window.
3. **The native kernel and remote layers are condensed re-implementations, not structural ports.** `Host/Attempt.cs` is 592 lines against upstream's 1,847; `Host/Object.cs` 319 against 1,687; `Remote/Path.cs` 45 against 233. No upstream hunk applies mechanically. Work packages must port by behaviour, reading upstream per commit (`git -C E:/AI/PiSharp/upstream-v160 show <sha> -- packages/chord/src/<dir>`).
4. **The kernel is a serialized resource.** Queries, events, shared holds and admission all edit `Host/{Attempt,Object,Reference,Staging,Host,Contracts,Internal}.cs`, and windows, events, shared holds and admission all edit `Remote/{Consumer,Provider,RemoteObject}.cs`. Honest parallelism is two packages at a time for the first two waves, then a serial chain (section 4).
5. **Admission depends on named queries at code level.** `host/admission.ts:33-34` imports `Reader`, `openReaders`, `readerSite` and `rowsReader`; both need the same parameterised park step in `Attempt`. Order is store, then queries, then admission.
6. **The store's live query is a rewrite.** Native `Store/LiveQuery.cs` (lock-based, per-owner `LiveQueries` registry, token diffing) has no usable diff base against upstream's `LiveQuery extends FramedSource` over `OpenQuery`/`OpenRead`/`Reads`. The commit-step integration is in `documents/transaction.ts:549-560`, not in `store.ts`.

## 2. Inventory: every changed file under `packages/chord/src` (127)

From `git -C E:/AI/PiSharp/upstream-v160 diff --numstat -M v1.5.2 v1.6.0 -- packages/chord/src`. "No-op" reasons follow the 1.5.2 table's categories (`upstream-documentation`, `browser-dom-react`, `typescript-import-only`, `upstream-build-tooling`). Where a file was `tested` at 1.5.2 but this release's delta in it is tooling only, the row says so: the decision is per change, not per file. **NEW** marks an upstream file that is new in 1.6.0 and has no native file yet. Package ids are defined in section 4.

### contracts, data, documents, state

| Upstream | +/− | Native file(s) | Disposition | Package |
|---|---|---|---|---|
| `contracts/README.md` | 36/14 | none | no-op: upstream documentation | — |
| `contracts/accepts.ts` | 52/8 | `Contracts/Accepts.cs` | port: event members in accepts tables (E) | foundation |
| `contracts/contract.ts` | 130/11 | `Contracts/Contract.cs` | port: `MemberKind` `event`/`queries`, `VariantData.events/queries`, `QueriesData`, `AdmissionData`, `NamedQuery`/`QueryPage`, `memberNames` (Q, E, A) | foundation |
| `contracts/declare.ts` | 115/30 | `Contracts/Declare.cs` | port: `queries` members (local only), event members, `admission` on `remote(...)` (Q, E, A) | foundation |
| `contracts/evolve.ts` | 117/9 | `Contracts/Evolve.cs` | port: `event`/`addEvent`/`removeEvent`, `admission(schema)`, queries carried over (E, A, Q) | foundation |
| `contracts/fingerprint.ts` | 10/4 | `Contracts/Fingerprint.cs` | port: queries and events nodes in `variantNormal`; admission is not in the fingerprint | foundation |
| `contracts/history.ts` | 8/4 | `Contracts/History.cs` | no-op: type-level `VariantShape` only | — |
| `contracts/index.ts` | 21/1 | `Contracts/Index.cs` (comment) | exports only; `Method`, `LiteralsSchema`, `HostKeyed` are TypeScript type exports with no C# work | foundation (header) |
| `contracts/internal.ts` | 2/0 | `Contracts/Internal.cs` (comment) | exports only | foundation (header) |
| `contracts/params.ts` | 7/1 | `Contracts/Params.cs` | port: `joinKey` (SH) | foundation |
| `contracts/schema.ts` | 48/2 | `Contracts/Schema.cs` | port: `queries(item, args)`/`QueriesMember`, event declaration (Q, E) | foundation |
| `data/failure.ts` | 6/3 | `Data/Failure.cs` | port: failure cause kind `denied` (A) | foundation |
| `data/internal.ts` | 1/1 | `Data/Internal.cs` (comment) | exports only | foundation (header) |
| `data/principal.ts` | 17/0 | `Data/Principal.cs` | port: `principalJson` (SH) | foundation |
| `documents/transaction.ts` | 7/6 | `Documents/Transaction.cs` | port: `#refreshQueries` stages `ReadRefresh` (CS) | store |
| `state/derive.ts` | 15/5 | `State/Derive.cs` | port: `invalidate` lists once per batch (F). Native `Derive.cs:142-157` still has the bug | foundation |
| `state/framed.ts` **NEW** | 146/0 | none; propose `State/Framed.cs` | port: `FramedOwner`, `FramedSource` (F, base of live queries and lists) | foundation |
| `state/internal.ts` | 1/0 | `State/Internal.cs` | exports only | foundation (header) |

### host

| Upstream | +/− | Native file(s) | Disposition | Package |
|---|---|---|---|---|
| `host/README.md` | 78/8 | none | no-op: upstream documentation | — |
| `host/adapted.ts` | 107/8 | `Host/Adapted.cs` | port: event mappings (`AdaptedMembers.event`, `MappedEvents`) (E) | remote |
| `host/admission.ts` **NEW** | 383/0 | none; propose `Host/Admission.cs` | port: `allow`/`deny`, `Decision`, `refuseAdmittedJob`, `revoked` (A) | admission |
| `host/attempt.ts` | 482/91 | `Host/Attempt.cs`, `Host/HostData.cs` (`Self.Query`) | port, three owners in sequence: `self.query` rebuilt and tables held/bound (CS, store); `Self.events`, `eventNode` (E, remote); `queries`/`liveQuery`/`count`/`list`, `#awaitReaders`, `#ownSteps` reorder, reusable `#park` (Q, queries); admission phase before the factory (A, admission) | store, remote, queries, admission |
| `host/dependencies.ts` | 70/10 | none: inline in `Attempt.ActivateAsync` (`Attempt.cs:100-161`) | port: `Parking`/`Blocked`, exported `pinKeys` (shared Q + A), denied branch (A) | queries, admission |
| `host/events.ts` **NEW** | 261/0 | none; propose `Host/Events.cs` | port: `HostEvents`, `EventNode`, `Subscription`, `OwnEventSource` (E) | remote |
| `host/facet.ts` | 30/8 | `Host/Contracts.cs` (`IFacetHost`, `ServeOptions`), `Host/Staging.cs` | port: `ServeOptions.admit`, admitted factory (A) | admission |
| `host/host-data.ts` | 27/19 | `Host/HostData.cs` | port: refresh through `store.reads.onHostKind` (CS); `deleteFrom(..., only)` (D) | store |
| `host/host.ts` | 172/34 | `Host/Host.cs`, `Host/RemoteIntegration.cs`, `Host/Contracts.cs` | port: `query_failed {query}` and close `unbindSets` (CS, store); `events`, `callWindow`, `eventWindow`, `#bindRemote`/`#sharingKey`, `routeLink(..., opened)` (E, CW, SH, O, remote); `HostDeleteOptions` (D, store); `principalKeyOf`, route `admit` (RA, admission) | store, remote, admission |
| `host/index.ts` | 15/3 | none (public types live in `Host/Contracts.cs`) | exports only | each owner |
| `host/internal.ts` | 2/0 | `Host/Internal.cs` | exports only (`revoked`, event types) | each owner |
| `host/list.ts` **NEW** | 164/0 | none; propose `Host/ListSource.cs` (`List` collides with `List<T>`) | port: `List`, `ListMemory`, `ListOptions` (Q) | queries |
| `host/members.ts` | 90/9 | none: checks live in `PluginStaging.ValidateImplementation` (`Host/Staging.cs:71-99`) | port: `checkEvents` (E, remote), `checkQueries` (Q, queries) | remote, queries |
| `host/named-query.ts` **NEW** | 227/0 | none; propose `Host/NamedQuery.cs` | port: `NamedQueryHandle`, `OwnQuery`, `ReferenceQuery`, `ownQueries` (Q) | queries |
| `host/object.ts` | 260/18 | `Host/Object.cs`, `Host/ReloadObject.cs` | port: `liveQueries` removal, `unbindSets` (CS, store); `call(..., binding)`, `generationOf`, `callAs` (SH, remote); `listMemory`, `queryMember` (Q, queries); `readmit`/`#rebuild`/`#cut`/`#allowed`, `followed` -> `denied` (A, admission; almost all of the +260) | store, remote, queries, admission |
| `host/query-reader.ts` **NEW** | 223/0 | none; propose `Host/QueryReader.cs` | port: `Reader`, `GivenQuery`, watchable arguments (Q) | queries |
| `host/read-declarations.ts` **NEW** | 206/0 | none; propose `Host/ReadDeclarations.cs` | port: `openReaders`, `readerSite`, `rowsReader`, `liveReader`, `listReader` (Q; lines 22-58 and 136-164 came with the admission commit but are reader code) | queries |
| `host/reference-events.ts` **NEW** | 159/0 | none; propose `Host/ReferenceEvents.cs` | port: `ReferenceEvents`, `ReexposedEvents`, `ReferenceEventTaps` (E) | remote |
| `host/reference-source.ts` | 47/6 | `Host/Reference.cs` (`ReferenceMember`, 195-257) | port: unseen members (SH, remote); `deny` drops the kept value (A, admission) | remote, admission |
| `host/reference.ts` | 219/19 | `Host/Reference.cs`, `Host/Internal.cs` (`ITarget`, `Followed`) | port: event taps and `IReference` event access (E), `Target.call(binding)`, `retry(walk, binding)`, `generationOf`, `joined`, `transfer` (SH) (remote); `queryMember`, `followInstance` (Q, queries); `Followed.denied` (A, admission) | remote, queries, admission |
| `host/route-admission.ts` **NEW** | 275/0 | none; propose `Host/RouteAdmission.cs` | port: `RouteAdmission`, `RouteAdmit`, `held` (RA) | admission |
| `host/route.ts` | 56/10 | `Host/Route.cs` | port: resolve result `opened` (O) and `suspended` (SH) (remote); `admit`, gate, cycle refusal (RA, admission) | remote, admission |
| `host/staging.ts` | 137/16 | `Host/Staging.cs` | port: `checkExposedOnce` on tables (CS, store); inert `events()` (E, remote); `queries`/`liveQuery`/`count`/`list` recording (Q, queries); `servedAdmission`, `admitSelf`, admit-first recording (A, admission) | store, remote, queries, admission |
| `host/tiling.ts` **NEW** | 318/0 | none; propose `Host/Tiling.cs` | port: `Tiling`, `MAX_PAGES = 4`, bookmarks (Q) | queries |
| `index.ts` | 0/2 | `ChordNext.cs` (comment) | no-op: drops a TypeScript re-export of `WhereNode` | — |

### link, remote

| Upstream | +/− | Native file(s) | Disposition | Package |
|---|---|---|---|---|
| `link/README.md` | 24/11 | none | no-op: upstream documentation | — |
| `link/binary.ts` | 40/10 | `Link/BinaryCodec.cs` | port: kind 18 `event`, flag `0x20` on `ack`, cause byte 3, `calls` 0 = absent | foundation |
| `link/channel.ts` | 47/11 | `Link/Channel.cs` | port: `peerCalls` optional, counted pings/pongs, `fence(then)` | foundation |
| `link/internal.ts` | 1/0 | none | exports only | — |
| `link/message.ts` | 20/6 | `Link/Message.cs` | port: `hello.calls` optional, `ack.events`, `event`/`WireEvent`, `CAUSE_KINDS` + `denied` | foundation |
| `link/text.ts` | 34/6 | `Link/TextCodec.cs` | port: `"h"` arity 2..3, `"a"` arity 0..1, `"E"`, cause letter `"d"` | foundation |
| `remote/README.md` | 96/25 | none | no-op: upstream documentation | — |
| `remote/calls.ts` | 26/12 | none: inlined in `Remote/Consumer.cs`; propose a real `CallWindow<T>` | port: no-window mode (CW), `removeWhere` (SH) | remote |
| `remote/consumer.ts` | 129/11 | `Remote/Consumer.cs` | port: per-origin relay windows (CW), `event` handling and event ack (E), `cancelCalls` (SH), ack before `wentLive` (fix) (remote); `denied` rejects queued calls `forbidden/revoked` (A, admission) | remote, admission |
| `remote/hold.ts` | 152/63 | `Remote/Hold.cs` | port: `#tapEvents` (E), binding fences replacing the single previous binding (remote); `#unbind` on denial (A, admission) | remote, admission |
| `remote/host-link.ts` | 21/5 | `Remote/HostLink.cs` | port: window by trust (CW), `event` routing (E), `opened(incarnation)` (O), `fence` (remote uses it; admission consumes it) | remote |
| `remote/internal.ts` | 1/1 | none | exports only (`PathGate`) | — |
| `remote/path.ts` | 152/13 | `Remote/Path.cs` | port: `suspended`, `requested` (SH, remote); `PathGate`, `withhold`, `forward`, fences (RA, admission) | remote, admission |
| `remote/provider.ts` | 259/48 | `Remote/Provider.cs` | port: optional limit, `RelayedCall` rejection rules (CW), `queueEvent`/`sendEvents`/`#eventAck` (E) (remote); `withheld`, `#fenced` (A, admission) | remote, admission |
| `remote/relay.ts` | 149/9 | `Remote/Relay.cs` | port: `RelayedCall`, windowed `submit` (CW, remote); `withheld`/`admitted` (A, admission) | remote, admission |
| `remote/remote-object.ts` | 259/57 | `Remote/RemoteObject.cs`, `Host/RemoteIntegration.cs` | port: `#bindings` set, `hold`/`unhold`, `retry(walk, binding)` (SH), `eventNode`/`event` (E) (remote); `queryMember(): never` (Q, queries); `withheld`/`admitted`, `denied` drops replicas (A, admission) | remote, queries, admission |
| `remote/replica.ts` | 26/0 | `Remote/Replica.cs` | port: `generation`, `fork(batch)` (SH) | remote |

### store

| Upstream | +/− | Native file(s) | Disposition | Package |
|---|---|---|---|---|
| `store/README.md` | 19/3 | none | no-op: upstream documentation | — |
| `store/column-set.ts` **NEW** | 130/0 | none; propose `Store/ColumnSet.cs` | port: `defineColumns`, `ColumnSet`, `checkExposes`, `checkExposedOnce` | store |
| `store/evaluate.ts` | 2/0 | `Store/Evaluate.cs` | port: `exists` is refused in memory | store |
| `store/expression.ts` | 381/33 | `Store/Expression.cs` | port: `exists`, `existsNode`, `compile`, scopes, `checkNode` (a JSON-to-`WhereNode` parser natively) | store |
| `store/host-data.ts` | 24/8 | `Store/HostData.cs` | port: `deleteHostData(..., owners?)`, `tableOwner` (D) | store |
| `store/index.ts` | 7/0 | `Store/Index.cs` (comment) | exports only | store (header) |
| `store/live-query.ts` | 130/258 | `Store/LiveQuery.cs` | port as a rewrite: `LiveQuery extends FramedSource`, `LiveTarget`, `LiveShape`, `DecodedRows`; `LiveQueries`, `QueryRefresh`, `refreshOf` removed | store |
| `store/open-query.ts` **NEW** | 220/0 | none; propose `Store/OpenQuery.cs` | port: `OpenQuery`, `OpenRead`, `QuerySpec`, `ReadSite` | store |
| `store/page.ts` **NEW** | 43/0 | none; propose `Store/Page.cs` | port: `readPage` | store |
| `store/query.ts` | 159/28 | `Store/Query.cs` | port: `join`, `whereNode`, two-argument `where`/`orderBy`, `expected`, cursor column lookup | store |
| `store/read-plan.ts` **NEW** | 429/0 | none; propose `Store/ReadPlan.cs` | port: `ReadPlan` (`select`, `count`, `probe`), `Projection`, `Use`, `Position`, `Range`, `StoredRead` | store |
| `store/read.ts` **NEW** | 157/0 | none; propose `Store/Read.cs` | port: `Read`, `ReadRun`, `ReadSink`, `identity` | store |
| `store/reads.ts` **NEW** | 265/0 | none; propose `Store/Reads.cs` | port: `Reads` (index by table, bindings, hold/ready/retire), `ReadRefresh` | store |
| `store/rows.ts` | 11/6 | `Store/Rows.cs` | port: `decodeScalar` extracted | store |
| `store/skip.ts` **NEW** | 95/0 | none; propose `Store/Skip.cs` | port: `skips(read, changes, step, context)`, `PROBES_PER_READ = 8` | store |
| `store/sql.ts` | 26/10 | `Store/Sql.cs` | port: `ConditionSql` render hook, `exists` SQL | store |
| `store/store.ts` | 51/0 | `Store/Store.cs` | port: `reads`; `onCommitted`; `settled()` | store |
| `store/table-changes.ts` | 26/0 | `Store/TableChanges.cs` | port: `changes(): Change[]` | store |
| `store/table-evolve.ts` | 15/3 | `Store/TableEvolve.cs` | port: `evolveTable(previous, edit, { exposes })` | store |
| `store/table-handle.ts` | 28/46 | `Store/TableHandle.cs` | port: `page`/`count` through `ReadPlan`, refuse a join; `TableOwner.queries` removed | store |
| `store/table.ts` | 8/1 | `Store/Table.cs` | port: `TableOptions.exposes` (not in the fingerprint) | store |

### node, surfaces, tui, react, web, sync, telemetry

| Upstream | +/− | Native file(s) | Disposition | Package |
|---|---|---|---|---|
| `node/app/README.md` | 29/26 | none | no-op: upstream documentation | — |
| `node/app/app.ts` | 17/11 | `Hosting/App/App.cs`, `AppOptions.cs` | port: sign-in names, `admit: () => true` for ingest (edge); `store` into the workers component (workers). `AppWeb` is typing only | edge, workers |
| `node/app/ephemeral-sessions.ts` (deleted) | 0/80 | `Hosting/App/EphemeralSessions.cs` | renamed, see next row | edge |
| `node/app/ephemeral-sign-ins.ts` **NEW** (rename) | 80/0 | propose `Hosting/App/EphemeralSignIns.cs` | port: rename (SI) | edge |
| `node/app/ephemeral.ts` | 6/6 | `Hosting/App/EphemeralLogin.cs` | port: rename (SI) | edge |
| `node/app/index.ts` | 3/3 | none | already ported: `Info.AppInfoFacet`, `Info.WorkerInfoFacet` are public; sign-in names follow the rename | edge |
| `node/app/invite.ts` | 5/5 | `Hosting/App/Invite.cs` | port: rename (SI) | edge |
| `node/app/layout.ts` | 1/1 | `Hosting/App/Layout.cs` | no-op: comment only | — |
| `node/app/owner.ts` | 14/14 | `Hosting/App/Owner.cs` | port: rename (SI) | edge |
| `node/app/pairing.ts` | 6/6 | `Hosting/App/Pairing.cs` | port: rename (SI) | edge |
| `node/app/sessions.ts` -> `sign-ins.ts` | 69/69 | `Hosting/App/Sessions.cs` -> propose `SignIns.cs` | port: rename with wire and persisted names (SI) | edge |
| `node/http/README.md` | 4/2 | none | no-op: upstream documentation | — |
| `node/http/http.ts` | 6/5 | `Hosting/Http/ChordHttp.cs`, `HttpContracts.cs` | port: `links.opened(request, principal, close)` | edge |
| `node/http/upgrades.ts` | 7/5 | `Hosting/Http/ChordHttp.cs` | port: `close` terminates the socket | edge |
| `node/plugins/README.md` | 3/1 | none | no-op: upstream documentation | — |
| `node/plugins/install.ts` | 7/2 | `Hosting/Plugins/PluginInstaller.cs` | no-op for this delta: npm 12 `pack --json` output; the native installer does not use npm | — |
| `node/plugins/plugins.ts` | 31/2 | `Hosting/Plugins/PluginManager.cs` | already ported (`BuildAsync` rediscovers on every build); add a regression case | edge |
| `node/surfaces/README.md` | 9/0 | none | no-op: upstream documentation | — |
| `node/surfaces/bundle.ts` | 20/9 | `Hosting/Surfaces/*` | no-op for this delta: esbuild `tsconfig` paths and asset loaders (upstream build tooling) | — |
| `node/surfaces/index.ts` | 3/1 | none | exports only | — |
| `node/surfaces/served.ts` | 3/0 | `Hosting/Surfaces/PublishedScripts.cs` | already covered: `SurfacePaths.Mime` maps the listed asset extensions | — |
| `node/surfaces/surface-build.ts` | 5/5 | `Hosting/Surfaces/SurfaceBuild.cs` | no-op for this delta: `tsconfig` option | — |
| `node/surfaces/tui.ts` | 8/2 | `Hosting/Surfaces/*` | no-op for this delta: `tsconfig` option | — |
| `node/surfaces/web.ts` | 40/11 | `Hosting/Surfaces/SurfaceBuild.cs` | `routes: false` with `file`/`pageAssets`/`base`: native equivalent exists (`GetFileAsync`, `Built.Base`, opt-in `Contribution`); add a case. `tsconfig` and the exported inline page bootstrap are no-ops | edge |
| `node/workers/README.md` | 25/5 | none | no-op: upstream documentation | — |
| `node/workers/base.ts` | 4/0 | `Hosting/Workers/WorkerBase.cs` | no-op for this delta: esbuild minification; the native base is a byte snapshot | — |
| `node/workers/process.ts` | 16/3 | `Hosting/Workers/ObservedLink.cs`, `ProcessWorkers.cs` | port: the link ends at exit, told as neither lost nor closed (W) | workers |
| `node/workers/workers.ts` | 110/30 | `Hosting/Workers/ProcessWorkers.cs` | port: `store` (required), `admit`, `WorkerState.incarnation`, exit published after the link closed and the store settled (W) | workers |
| `react/README.md` | 11/1 | none | no-op: browser DOM/React | — |
| `react/hooks.ts` | 51/5 | none | no-op: React (`useVariant`, `useEvents`); the portable core `liveIn` is ported from `surfaces/variant.ts` | — |
| `react/index.ts` | 3/0 | none | no-op: React | — |
| `surfaces/README.md` | 17/5 | none | no-op: upstream documentation | — |
| `surfaces/client.ts` | 48/19 | `Surfaces/Client.cs` | port: `limits`, `follow`, start without awaiting the first script set | edge |
| `surfaces/connection.ts` | 44/22 | `Surfaces/Connection.cs` | port: immediate retry after a wake, `canRetry` asked beside the attempt, `stop()` reports `waiting` | edge |
| `surfaces/variant.ts` **NEW** | 34/0 | none; propose `Surfaces/Variant.cs` | port: `liveIn`; the generic types are TypeScript typing | edge |
| `sync/client.ts` | 44/22 | `Sync/SessionSync.cs` | port: archive slot when `outbox.floor > synced`, body release in `pullSession`, fold from batches not yet folded | edge |
| `sync/filesystem.ts` | 34/14 | `Sync/FileSystemRemote.cs` | already ported differently (lazy open at enumeration); add regression cases | edge |
| `sync/index.ts` | 1/0 | none | no-op: type export | — |
| `sync/remote.ts` | 18/4 | `Sync/Remote.cs` | port: `remoteHistory` restores from the newest archive slot | edge |
| `telemetry-ingest/facet.ts` | 11/4 | `TelemetryIngest/Facet.cs` | port: required `admit(principal)`, refusal `forbidden` | edge |
| `telemetry.ts` | 52/1 | `Telemetry.cs` | port: schema rows `chord.query.probes/skips/rows`, `chord.events.emitted/sent/dropped/encodes`, event `chord.admit` | foundation (rows); store, remote, admission emit |
| `tui/README.md` | 3/0 | none | no-op: upstream documentation | — |
| `tui/index.ts` | 1/0 | none | no-op: type re-export | — |
| `tui/scope.ts` | 37/3 | `Hosting/Terminal/TerminalScope.cs` | port: `variant(ref, variant)` and follow de-duplication (edge); `on(member, listener)` needs event members (workers) | edge, workers |
| `web/README.md` | 5/0 | none | no-op: browser DOM | — |
| `web/page.ts` | 20/4 | none | no-op: browser DOM; `limits` and `follow` land in `Surfaces/Client.cs` | — |

Counts: 18 upstream files are new in 1.6.0 with no native file yet (nine under `host/`, `state/framed.ts`, seven under `store/`, `surfaces/variant.ts`); two more are renames of ported files (`sign-ins.ts`, `ephemeral-sign-ins.ts`). The no-op rows are the 14 README files, the React and web files, and exports-only, typing-only or build-tooling deltas; everything else is a port.

Outside `src`: `docs/**`, `CHANGELOG.md`, `README.md` are `no-op/upstream-documentation`; `test/examples/**` (new 67-72 and 76) and `test/bench/**` are `no-op/example-or-fixture`, as in the 1.5.2 table; `package.json` is the meta package's (the `./telemetry-ingest` export is already the public namespace `PiSharp.Chord.TelemetryIngest`; peer ranges are npm only).

## 3. Per changelog bullet (and the changes it omits)

Test counts are static `it(` counts at v1.5.2 -> v1.6.0. All Chord cases belong in `tests/PiSharp.Chord.Tests` unless the row says Hosting. That suite discovers any static `*Cases()` method by reflection (`Program.cs:32-35`), so a new file needs no registration; `tests/PiSharp.Chord.Hosting.Tests/Program.cs` registers cases by hand (lines 124-169).

### 3.1 Breaking

| Bullet | Upstream | Native change | Tests |
|---|---|---|---|
| `createWorkers` requires `store` | `node/workers/workers.ts` `WorkersOptions.store` L72, `#exited` L666 awaits `store.settled()`, `#left` L695; `store/store.ts` `settled()` ~L322 | `Hosting/Workers/ProcessWorkers.cs`: `WatchExit` (L263-306) publishes the exit and then closes the link, which is the wrong order; seven construction sites need `Store`. `Store/Store.cs` needs `SettledAsync` | `node/workers/workers.test.ts` 37 -> 42 (cases "report and exit in the same turn", "a commit queued before the exit", "stop resolves after publication"), `process.test.ts` 1 -> 2; Hosting suite, new `Sync160WorkerCases.cs`. `store.test.ts` settled 2 -> `Store/StoreCases.cs` |
| `telemetryIngestFacet` requires `admit(principal)` | `telemetry-ingest/facet.ts` L25, L46 | `TelemetryIngest/Facet.cs` L9-26; `Hosting/App/App.cs` L197 passes `_ => true`; three test sites | `telemetry-ingest/facet.test.ts` 7 -> 8; `TelemetryIngestCases.cs` |
| `query_failed` carries `query` | `host/host.ts` L212-216, `host/attempt.ts` L546 | `Host/Contracts.cs` `HostError` (drop `Table`, add `Query`); only producer is `Host/HostData.cs:287` | asserted in `store/live-query.test.ts:319` and the named-query tests |

### 3.2 Added

| Bullet | Upstream | Native change | Tests and native home |
|---|---|---|---|
| `telemetry-ingest` export | `package.json` exports | none: already public | — |
| Named queries | `contracts/schema.ts` 197-221, `contract.ts` 93-116, `declare.ts` `readQueries` 228-235; `host/named-query.ts`, `query-reader.ts`, `read-declarations.ts`; `attempt.ts` 567-630, 1592-1609, 1647-1676; `staging.ts` 343-402; `reference.ts` `followInstance` 469-494 | contracts in `Contracts/*`; new `Host/{NamedQuery,QueryReader,ReadDeclarations}.cs`; `Host/{Attempt,Staging,Reference,Object,Internal,Contracts}.cs` | `host/named-query.test.ts` 17, `query-reader.test.ts` 14, `read-declarations.test.ts` 4 -> new `Host/Sync160NamedQueryCases.cs`, `Sync160QueryReaderCases.cs`, `Sync160ReadDeclarationsCases.cs`; `contracts/declare.test.ts` 3 of its 9 new cases -> `Contracts/DeclareCases.cs` |
| `links.opened(request, principal, close)` | `node/http/http.ts` L68, L487; `upgrades.ts` L55-78, L182 | `Hosting/Http/HttpContracts.cs` L53, `ChordHttp.cs` L171-175; callers `App.cs` L142, `Sessions.LinksOpenedAsync` | `node/http/upgrades.test.ts` 18 -> 19; Hosting `Sync160HttpCases.cs` |
| `memberNames(contract, variant)` | `contracts/contract.ts` 256-273 | `Contracts/Contract.cs` | `contracts/contract.test.ts` 7 -> 10; `Contracts/ContractCases.cs` |
| Types `Method`, `LiteralsSchema`, `HostKeyed` exported | `contracts/index.ts` | no-op: TypeScript type exports | `contracts/schema.test.ts` 8 -> 9 is an export-surface check with no C# equivalent |
| Lists | `host/list.ts`, `host/tiling.ts`, `attempt.ts` `list()`, `object.ts` `listMemory` 680-698; `store/open-query.ts` | new `Host/{ListSource,Tiling}.cs`; `Store/OpenQuery.cs` | `host/list.test.ts` 15, `tiling.test.ts` 14 -> new `Host/Sync160ListCases.cs`, `Sync160TilingCases.cs` |
| Column sets | `store/column-set.ts`, `expression.ts` 188-596, `query.ts`, `read-plan.ts`, `sql.ts`, `table.ts`, `table-evolve.ts`, `table-handle.ts` `#plan` | `Store/*` (see inventory) | `store/column-set.test.ts` 7, `read-plan.test.ts` 21 -> new `Store/Sync160ColumnSetCases.cs`, `Sync160ReadPlanCases.cs` |
| Counters `chord.query.probes/skips/rows` | `telemetry.ts` 336-350; emitted from `store/skip.ts`, `read.ts` | `Telemetry.cs` schema; emitted by the new store files | `Telemetry152Cases.cs` metric count 25 -> 32 with the events counters |
| Admission | `host/admission.ts`; `facet.ts` 32-50; `attempt.ts` 800-909, `#park` 1504-1566; `object.ts` 882-1016; `staging.ts` 187-250, 304-313; `contracts/declare.ts`, `evolve.ts`; `data/failure.ts`; remote: `hold.ts` `#unbind` 386-407, `consumer.ts` `#failed` 438-446, `remote-object.ts` 305-317, 390-394, 590 | new `Host/Admission.cs`; `Host/{Attempt,Object,ReloadObject,Reference,Staging,Contracts,Internal}.cs`; `Remote/*` denied paths; `Data/Failure.cs` | `host/admission.test.ts` 43 -> new `Host/Sync160AdmissionCases.cs`; `dependencies.test.ts` +2, `reference-source.test.ts` +1 (the denial case); `contracts/declare.test.ts` 3 admission cases |
| Route admission | `host/route-admission.ts`; `route.ts` 45-60, 107-145; `remote/path.ts` `PathGate` 27-37, 101-224; `relay.ts` 237-255; `provider.ts` 322-326; `node/workers/workers.ts` `admit` L58 | new `Host/RouteAdmission.cs`; `Host/Route.cs`; `Remote/{Path,Relay,Provider,RemoteObject}.cs`; `ProcessWorkerOptions.Admit` | `host/route-admission.test.ts` 21 -> new `Remote/Sync160RouteAdmissionCases.cs`; `remote/path.test.ts` 4 gated cases; workers case (1) |
| `deleteHostData(address, ctx, { plugin })` | `host/host.ts` 419-435, 1246-1255; `host/host-data.ts` `deleteFrom` 179-198; `store/host-data.ts` `deleteHostData` 344, `removeHostData` 474, `tableOwner` 505 | `Host/HostData.cs:139-150`, `Store/HostData.cs:296, 362` | `host/host-data.test.ts` 15 -> 18; `Host/HostDataCases.cs` |
| `useVariant` / `TuiScope.variant` | `surfaces/variant.ts` `liveIn` L26; `tui/scope.ts` L54, L189; `react/hooks.ts` (no-op) | new `Surfaces/Variant.cs`; `Hosting/Terminal/TerminalScope.cs` | `surfaces/variant.test.ts` 3 (one needs admission, one is typing), `tui/scope.test.ts` 9 -> 13 (one needs events) |
| `WorkerState.incarnation` | `node/workers/workers.ts` L85-92, `#opened` L605 | `ProcessWorkerState` gains `Incarnation` | workers case (5) |
| Route `resolve` may return `opened(incarnation)` | `host/route.ts` L43, L282-283; `host/host.ts` 1644-1652, 1788-1803; `remote/host-link.ts` L42-43, L130-135 | `Host/Route.cs` `ResolvedLink`, `Remote/HostLink.cs:44` `Opened()` | `host/route.test.ts` 1 of its 6 new cases |
| `appInfoFacet` / `workerInfoFacet` | `node/app/index.ts` | already ported: `Hosting/App/Info.cs` | none new |

### 3.3 Changed and fixed

| Bullet | Upstream | Native change | Tests and native home |
|---|---|---|---|
| Worker base minified | `node/workers/base.ts` L83 | no-op: esbuild | `base.test.ts` +1 is no-op |
| A commit re-runs a live query only when it can change it | `store/reads.ts`, `read.ts`, `skip.ts`, `table-changes.ts` `changes()` 149, `documents/transaction.ts` 549-560, `host/host-data.ts` 243-272, `attempt.ts` 538-562 | `Store/{Reads,Read,Skip,OpenQuery,LiveQuery,TableChanges}.cs`, `Documents/Transaction.cs:123, 391`, `Host/HostData.cs:223, 273-289`, `Host/Object.cs:22-23` | `store/reads.test.ts` 12, `skip.test.ts` 8 (one loops over seeds 1..8), `live-query.test.ts` 12 with changed bodies -> new `Store/Sync160ReadsCases.cs`, `Sync160SkipCases.cs`, `Sync160LiveQueryCases.cs`; the seven authored cases in `Store/LiveQueryCases.cs` target the removed API and must be rewritten |
| One frame per commit for a watched derive | `state/derive.ts` `invalidate` 116-126 | `State/Derive.cs:142-157` | `state/derive.test.ts` 18 -> 23; `remote/provider.test.ts` one regression case |
| One frame per commit for a list or live query | `state/framed.ts` `deliver` 109-122 | new `State/Framed.cs` | `state/framed.test.ts` 8 -> new `State/Sync160FramedCases.cs`; a `list.test.ts` case over a link |
| Workers close a worker's link on exit before publishing | `node/workers/process.ts` L81, L153; `workers.ts` `#exited` | `Hosting/Workers/ObservedLink.cs` (a silent `End()`), `ProcessWorkers.cs` `WatchExit`, `StopAsync` | see 3.1 |
| Peer dependency ranges | `package.json` | no-op: npm | — |
| Sync: outbox off and on | `sync/client.ts` L153, L442; `sync/remote.ts` `remoteHistory` L147-171 | `Sync/SessionSync.cs`, `Sync/Remote.cs` L52-83 | `sync/sync.test.ts` 24 -> 29 -> new `Sync/Sync160SessionCases.cs` |
| Sync: pull closes refused bodies | `sync/client.ts` `readBody` L401; `sync/filesystem.ts` `readerBody` L227 | `Sync/SessionSync.cs` (a `ReadBodyAsync` that disposes the enumerator); `FileSystemRemote.cs` already opens lazily | within the five sync cases above |

### 3.4 Not in the changelog

| Change | Upstream | Native change | Tests and native home |
|---|---|---|---|
| Events | `host/events.ts`, `reference-events.ts`, `adapted.ts` 159-343, `members.ts` `checkEvents` 181-199, `reference.ts` taps 686-725; remote: `consumer.ts` 256-261, 334-358, `provider.ts` 405-429, 718-732, `hold.ts` 615-638, `remote-object.ts` 648-677; wire `event`, `ack.events` | new `Host/{Events,ReferenceEvents}.cs`; `Host/{Adapted,Reference,Attempt,Staging,Host,Contracts}.cs`; `Remote/{Consumer,Provider,Hold,RemoteObject,HostLink}.cs`; `Link/*` | `host/events.test.ts` 19, `reference-events.test.ts` 9, `adapted.test.ts` +5, `members.test.ts` +2, `staging.test.ts` +1; contracts `accepts` +4, `evolve` +3, `fingerprint` +1, `declare` 3; remote `consumer` 7, `provider` 9, `relay` 7, `remote-object` 5 |
| Call windows by trust | `remote/calls.ts` 29-33, `host-link.ts` L87-88, `consumer.ts` `relayWindow` 207-222, `relay.ts` `RelayedCall` 45-104, `provider.ts` 270-342 | `Remote/{Consumer,Provider,Relay,HostLink}.cs`; `HostOptions.CallWindow` | `remote/calls` +1, `consumer` +2, `host-link` +3, `provider` +3, `relay` 10 |
| Shared holds | `remote/remote-object.ts` 125-248, 444-482, 695-756; `replica.ts` 41-105; `path.ts` 78-80, 132-134; `host/host.ts` 1465-1575; `reference.ts` 405-448; `object.ts` 504-519; `data/principal.ts` `principalJson`; `contracts/params.ts` `joinKey` | `Remote/{RemoteObject,Replica,Path,Consumer}.cs`; `Host/{RemoteIntegration,Reference,Object,Internal,Host}.cs` | `remote/remote-object.test.ts` 31 -> 71, `replica` +2, `hold` +2, `path` +2, `consumer` +2, `calls` +1; `host/host.test.ts` +6, `object` +1, `reference` +1, `reference-source` +3, `route` +5; `data/principal.test.ts` +3 |
| Sign-ins rename | `node/app/sign-ins.ts`, `ephemeral-sign-ins.ts` and five callers | `Hosting/App/{Sessions,EphemeralSessions,EphemeralLogin,Invite,Owner,Pairing,App,AppOptions}.cs` | renamed upstream tests; native `AppStartTests.cs`, `AppAuthRouteTests.cs` identifiers; add one case pinning the cookie, contract and document names |
| Client and connection | `surfaces/client.ts` L53, L78, L119-127; `connection.ts` L93-174 | `Surfaces/{Client,Connection}.cs` | `surfaces/client.test.ts` 10 -> 16, `connection.test.ts` 8 -> 10 (one case removed upstream) |
| `Store.onCommitted` | `store/store.ts` L38, 243-264 | `Store/Store.cs` | `store.test.ts` 3 cases. No consumer found inside `packages/chord/src` |

### 3.5 Proposed native API

Consistent with what the native tree does today: positional records with optional trailing parameters, default-throwing interface members for optional capabilities (as `IStoreSelf.Query` and `HostKeyedState` do), `JsonNode` for schema and values, static factories on `Schemas`, `Tables`, `Expressions`.

```csharp
// --- foundation: Contracts, Data, Link, State ---
public sealed class QueriesMember { public JsonNode Item { get; } public IReadOnlyDictionary<string, JsonNode> Args { get; } }
public static QueriesMember Schemas.Queries(JsonNode item, params (string Name, JsonNode Args)[] queries);
public sealed class EventDeclaration { public JsonNode Schema { get; } }
public static EventDeclaration Schemas.Event(JsonNode schema);
// Contracts/Declare.cs: struct Member gains implicit conversions from QueriesMember and EventDeclaration
public sealed class RemoteDeclaration { /* ... */ public JsonNode? Admission { get; init; } }      // not in the fingerprint
public sealed class VariantDeclaration { /* ... */ public OrderedDictionary<string, Member> Events { get; init; } }
public ContractEditor Admission(JsonNode? schema);                                               // remote only
public ContractEditor Event(...); public ContractEditor AddEvent(...); public ContractEditor RemoveEvent(...);
public sealed record MemberNames(IReadOnlyList<string> State, IReadOnlyList<string> Methods, IReadOnlyList<string> Events);
public MemberNames Contract.MemberNames(string? variant = null);
public sealed record QueryPage(IReadOnlyList<JsonNode?> Items, string? Next = null);
public interface INamedQuery { Task<QueryPage> PageAsync(JsonNode? args, long limit, ChordContext context, PageOptions? options = null); }
internal enum MemberKind { State, Method, Keyed, Queries, Event }
internal sealed record QueriesData(Snapshot Item, IReadOnlyDictionary<string, Snapshot> Args);   // VariantData gains Queries, Events; VariantNames gains Events
public enum FailureKind { Transient, Broken, Waiting, Denied }

internal sealed record HelloMessage(long Protocol, string Incarnation, long? Calls = null) : Message("hello");   // null: no window
internal sealed record AckMessage(long? Events = null) : Message("ack");                                         // null: a state ack
internal sealed record WireEvent(long Hold, long Member, long At, Part Value);
internal sealed record EventMessage(IReadOnlyList<WireEvent> Events) : Message("event");
// Channel: long? PeerCalls; internal Action Fence(Action then) (returns the cancel)

internal interface IFramedOwner { string Label { get; } void Report(HostError error); }
internal abstract class FramedSource : Source { /* Ready, Failure, Detached, Value, Start(), protected Quiet/Set/FailWith/End */ }

// --- store ---
public sealed class ColumnSetOptions { public OrderedDictionary<string, Property> Columns { get; init; } public required IReadOnlyList<string> Key { get; init; } public OrderedDictionary<string, IReadOnlyList<string>> Indexes { get; init; } }
public static ColumnSet Tables.DefineColumns(string name, ColumnSetOptions options);
public ColumnSet? TableOptions.Exposes { get; init; }
public sealed class EvolveOptions { public ColumnSet? Exposes { get; init; } public bool ExposesNone { get; init; } }   // upstream: ColumnSet | "none"
public static TableDefinition Tables.EvolveTable(TableDefinition previous, Func<TableEditor, TableEditor> edit, EvolveOptions? options = null);
public static Condition Expressions.Exists(ColumnSet set, IReadOnlyDictionary<string, ColumnRef> match, Func<IReadOnlyDictionary<string, ColumnRef>, Condition>? inner = null);
public static Condition Expressions.ExistsNode(ColumnSet set, IReadOnlyDictionary<string, ColumnRef> match, JsonNode? node);
public static WhereNode Tables.Compile(ColumnSet set, Condition condition);
public Query Query.Join(ColumnSet set, Func<Refs, IReadOnlyDictionary<string, ColumnRef>> match);   // Refs = IReadOnlyDictionary<string, ColumnRef>
public Query Query.WhereNode(ColumnSet set, JsonNode? node);
public Query Query.Where(Func<Refs, Refs, Condition> fn); public Query Query.OrderBy(Func<Refs, Refs, ColumnRef?> fn, OrderDirection direction = OrderDirection.Asc);
public Task ChordStore.SettledAsync(); public IDisposable ChordStore.OnCommitted(Action listener, Action<Exception> onError);
public sealed record HostError(/* ... */, string? Query = null);                                  // replaces Table
public sealed record HostDeleteOptions(string? Plugin = null);
public Task<HostDataCounts> DeleteHostDataAsync(Address address, ChordContext context, HostDeleteOptions? options = null);
// internal seam for the queries package: ReadPlan, Projection, StoredRead, Position, ReadRange (not "Range"), OpenQuery, OpenRead, ReadSite,
//   QuerySpec, ReadRun, ReadKind, IReadSink, LiveQuery, LiveShape, LiveTarget, DecodedRows, StoreInternal.ReadPageAsync, PartnerName, DecodeScalar, IChordStore.Reads

// --- remote (events, windows, shared holds, opened) ---
public sealed record ChordEvent(JsonNode? Value, long At);
public interface IEventSubscription { void Stop(); }
public interface IEventMember { Task<IEventSubscription> OnAsync(Action<ChordEvent> listener, ChordContext context); }
public interface IEventSource : IEventMember { void Emit(JsonNode? value); }
IEventSource ISelf.Events();                 // default-throwing
IEventMember IReference.Event(string name);  // default-throwing
public int HostOptions.CallWindow { get; init; } = 10_000; public int HostOptions.EventWindow { get; init; } = 32;
public sealed record ResolvedLink(ILink Link, LinkOptions Options, Action<string>? Opened = null);
// internal: CallWindow<T>, RelayedCall, ITarget.CallAsync/Retry gain the holder, ITarget.GenerationOf, Binding.Joined/Transfer

// --- queries ---
public delegate JsonNode? QueryMap(IReadOnlyDictionary<string, JsonNode?> row, IReadOnlyDictionary<string, JsonNode?> partner);
public sealed record QueriesOptions(QueryMap? Map = null, IReadOnlyList<string>? Reads = null, IReadOnlyList<string>? PartnerReads = null);
public sealed record QueryBuilder(Func<JsonNode?, Query> Build, JsonNode? Args = null);
public sealed record ListOptions(long First);
public interface IListHandle { IWatchable<JsonNode?> Value { get; } Task MoreAsync(ChordContext context); }   // value: { rows, moreAfter }
IWatchable<JsonNode?> ISelf.LiveQuery(INamedQuery query, object? args, long first);       // value: { rows, more }; args: JsonNode or IWatchable<JsonNode?>
IWatchable<JsonNode?> ISelf.Count(INamedQuery query, object? args);
IListHandle ISelf.List(string name, IReadOnlyList<(INamedQuery Query, object? Args)> sources, ListOptions options);
IReadOnlyDictionary<string, INamedQuery> IStoreSelf.Queries(ITable table, QueriesOptions options, IReadOnlyDictionary<string, QueryBuilder> builders);
IReadOnlyDictionary<string, INamedQuery> IReference.QueriesMember(string name);

// --- admission ---
public static class Admission { public static JsonObject Allow(JsonNode? value = null); public static JsonObject Deny(string reason); }
public interface IAdmitSelf { IReference Get(Contract type, JsonObject parameters); IRegistryValue<V> Registry<C, V>(Registry<C, V> registry);
    IWatchable<JsonNode?> Derive(IReadOnlyList<object> sources, Func<IReadOnlyList<object?>, JsonNode?> compute);
    IWatchable<JsonNode?> LiveQuery(INamedQuery query, object? args, long first); }
public delegate IWatchable<JsonNode?> Admit(JsonObject parameters, JsonObject principal, IAdmitSelf self);
public delegate HostImplementation AdmittedHostFactory(JsonObject parameters, JsonObject principal, JsonNode? admitted, ISelf self);
public sealed record ServeOptions(AcceptsTable? Accepts = null, TimeSpan? Linger = null, bool Untraced = false, Admit? Admit = null);
void IFacetHost.Serve(Contract type, AdmittedHostFactory factory, ServeOptions options);
public Func<JsonObject, Address, IAdmitSelf, IWatchable<JsonNode?>>? RouteOptions.Admit { get; init; }
// internal: IPathGate { bool Joined(IUpstream h); void Left(IUpstream h); void Unlinked(); }; IUpstream.Admitted(); IUpstream.Withheld(FailureCause)

// --- edge and workers ---
public Func<HttpRequestContext, JsonObject, Action, Task<Func<Task>?>>? ChordHttpOptions.LinksOpened { get; init; }   // third argument: close
public required Func<JsonObject?, bool> TelemetryIngestOptions.Admit { get; init; }
public static T? Variants.LiveIn<T>(T? reference, string variant) where T : class, IReference;
public Func<T?> TerminalScope.Variant<T>(T reference, string variant) where T : class, IReference;
public PartLimits? ClientOptions.Limits { get; init; } public bool ClientOptions.Follow { get; init; } = true;
// Sessions -> SignIns, EphemeralSessions -> EphemeralSignIns, SessionKind/SessionInfo/IssuedSession -> SignIn*, AppLimits.EphemeralSignIns, App.SignIns
public required IStore? ProcessWorkerOptions.Store { get; init; } public <RouteOptions.Admit type>? ProcessWorkerOptions.Admit { get; init; }
public sealed record ProcessWorkerState(string Status, int? ProcessId = null, string? Error = null, DateTimeOffset? StartedAt = null, string? Incarnation = null);
```

Two shapes need an owner decision when their package runs. Upstream makes `admission(schema)` the last edit of a version only through TypeScript types; C# needs either a terminal editor type or a runtime check. And `Allow`/`Deny` are proposed as plain JSON objects because upstream's decision is a JSON watchable that flows through `self.derive` and is validated at runtime (`admission.ts` `#read` 329-347).

## 4. Work packages

### 4.1 Verdict on parallelism

Seven packages. Two can run at a time in each of the first two waves; after that the chain is serial because every remaining feature edits the same kernel files. Splitting events, shared holds, queries and admission across simultaneous worktrees would produce conflicts in `Host/Attempt.cs`, `Host/Reference.cs`, `Host/Internal.cs`, `Remote/Consumer.cs`, `Remote/Provider.cs` and `Remote/RemoteObject.cs` that no merge resolves cleanly.

```
wave 0:  chord-foundation   ||  chord-edge
wave 1:  chord-store        ||  chord-remote      (both need foundation merged)
wave 2:  chord-queries                             (needs store and remote merged)
wave 3:  chord-admission                           (needs queries)
wave 4:  chord-workers                             (needs store, remote, admission)
```

The order of the serial part follows upstream's own commit order (events and windows, shared holds, named queries, admission, worker exit), so each package reads one or two upstream commits.

### 4.2 Package table

"Owns" is exclusive while the package runs. A path listed for two packages is owned by them in sequence, never at the same time, except the named hunks under 4.3.

| id | Owns: native source | Owns: tests | Ports | Depends on | Size |
|---|---|---|---|---|---|
| `chord-foundation` | `src/PiSharp.Chord/Contracts/**`, `Data/**`, `Link/**`, `State/**`, `Telemetry.cs` (schema rows only) | `tests/PiSharp.Chord.Tests/{Contracts,Data,Link,State}/**`, `Telemetry152Cases.cs`, new `tests/fixtures/chord-link-v160.json` and `Link/generate-goldens160.mjs` | contracts for queries, events, admission, `memberNames`, `joinKey`; `FailureKind.Denied`; `principalJson`; the whole wire delta and `Channel.Fence`; `FramedSource` and the derive fix; telemetry schema rows | none | M to L: about 930 upstream lines, about 40 cases plus goldens |
| `chord-edge` | `src/PiSharp.Chord.Hosting/{Http,App,Plugins,Surfaces}/**`, `Hosting/Terminal/TerminalScope.cs`, `src/PiSharp.Chord/Surfaces/**`, `src/PiSharp.Chord/Sync/**`, `src/PiSharp.Chord/TelemetryIngest/**` | `tests/PiSharp.Chord.Hosting.Tests/**` except worker cases, `tests/PiSharp.Chord.Tests/{Surfaces,Sync}/**`, `TelemetryIngestCases.cs` | `LinksOpened` close; ingest `Admit`; sign-ins rename; client `Limits`/`Follow`/start; connection loop; `Variants.LiveIn`; `TerminalScope.Variant`; both sync fixes; plugin and surface regression cases | none | M: about 450 applicable upstream lines, about 25 cases, wide rename |
| `chord-store` | `src/PiSharp.Chord/Store/**`, `Documents/Transaction.cs`, `Host/HostData.cs`; named hunks in `Host/{Attempt,Object,Host,Staging,Contracts}.cs` (4.3) | `tests/PiSharp.Chord.Tests/Store/**`, `Documents/TransactionCases.cs`, `Host/HostDataCases.cs`, `Host/HostDataTelemetryCases.cs` | column sets, exists and joins, read plans, the read index and skip, `OpenQuery`, `LiveQuery` rewrite, `self.query` over it, `query_failed {query}`, table handle `page`/`count`, `SettledAsync`, `OnCommitted`, delete host data by plugin, the query counters | foundation (`State/Framed.cs`) | L: about 2,200 upstream lines, 48 new store cases + 5 + 12 rewritten + 3 host-data, and the seven old `LiveQueryCases` rewritten |
| `chord-remote` | `src/PiSharp.Chord/Remote/**`, `Host/{Events,ReferenceEvents}.cs` (new), `Host/{Adapted,Reference,Internal,Route,RemoteIntegration,RemoteActivation,KeyedReference}.cs`; named hunks in `Host/{Attempt,Object,Host,Staging,Contracts}.cs` (4.3) | `tests/PiSharp.Chord.Tests/Remote/**`, `Host/AdaptedCases.cs`, new `Host/Sync160EventsCases.cs`, `Host/Sync160SharedHoldCases.cs` | milestone R1: call windows by trust, ack before live, binding fences, events end to end, `opened(incarnation)`. Milestone R2: shared holds | foundation | XL: about 2,300 upstream lines, about 150 cases |
| `chord-queries` | new `Host/{NamedQuery,QueryReader,ReadDeclarations,ListSource,Tiling}.cs`; `Host/{Attempt,Staging,Object,Reference,Host,Contracts,Internal}.cs`; one throwing `QueryMember` in `Remote/RemoteObject.cs` | new `Host/Sync160{NamedQuery,QueryReader,ReadDeclarations,List,Tiling}Cases.cs` | named queries, `liveQuery`, `count`, `namedQuery.page`, lists, watchable arguments, the reusable park step in `Attempt` | store, remote, foundation | L: about 1,600 upstream lines, 64 cases |
| `chord-admission` | new `Host/{Admission,RouteAdmission}.cs`; `Host/{Attempt,Object,ReloadObject,Reload,Reference,Staging,Route,Host,Contracts,Internal}.cs`; `Remote/{Path,Hold,Provider,Consumer,Relay,RemoteObject,HostLink}.cs` | new `Host/Sync160AdmissionCases.cs`, `Remote/Sync160RouteAdmissionCases.cs`, `Remote/Sync160AdmissionCases.cs` | admission, route admission, the `denied` paths on both sides of a link, path gate and fences | queries (and through it store and remote) | XL: about 1,500 upstream lines, about 75 cases, the highest design risk |
| `chord-workers` | `src/PiSharp.Chord.Hosting/Workers/**`, call sites in `Hosting/App/{App,WorkerApp,Archive}.cs`, `Hosting/Terminal/TerminalScope.cs` (`On`) | Hosting `Sync160WorkerCases.cs`, `WorkerSnapshotCases.cs`, `ArchiveCases.cs`, `Program.cs`; the variant and scope cases that need admission or events | `Store` (required) and the settled wait, `Admit`, `Incarnation`, link ended before the exit is published, `TerminalScope.On`, the admission-rebind variant cases | store (`SettledAsync`), remote (`ResolvedLink.Opened`, events), admission (`RouteOptions.Admit`) | S to M: about 130 upstream lines, about 10 cases |

### 4.3 Shared files and how they stay clean

**`Telemetry.cs`.** `chord-foundation` adds the schema rows and any metric handles or helper methods the new counters and the `chord.admit` event need, so that `chord-store`, `chord-remote` and `chord-admission` only call them and never edit `Telemetry.cs`.

**Wave 0.** `chord-foundation` and `chord-edge` share no file. Foundation changes three internal shapes that other directories construct, so it may make the smallest compile fix outside its paths and must list it: `VariantData`/`VariantNames` constructor calls, `switch` statements over `FailureKind`, and `Channel.PeerCalls` becoming `long?` in `Remote/{HostLink,Consumer,Provider}.cs` (keep passing 256 until `chord-remote` replaces it).

**Wave 1.** `chord-store` and `chord-remote` both touch five kernel files, in regions that do not overlap:

| File | `chord-store` hunks | `chord-remote` hunks |
|---|---|---|
| `Host/Attempt.cs` | the table and query load loop (`:207`), `Reads.Hold`/`Bind`/`Ready` at table preparation, `Reads.Retire` at detach | `Self.Events()`, `EventNode(member, version)`, adapted event wiring, event detach, the member check's event sources |
| `Host/Object.cs` | removal of `LiveQueries` (`:22-23`), `UnbindSets` | `CallAsync` with the holder, `GenerationOf`, `CallAs` |
| `Host/Host.cs` | close calls `UnbindSets`; `DeleteHostDataAsync` options | `Events`, windows, the sharing table, `MakeLink(..., opened)` |
| `Host/Staging.cs` | `CheckExposedOnce` on recorded tables | inert `Events()`, event members in `ValidateImplementation` |
| `Host/Contracts.cs` | `HostError.Query`, `HostDeleteOptions` | `ISelf.Events`, `IReference.Event`, `HostOptions.CallWindow`/`EventWindow` |

The one place they sit close together is the detach step of `Attempt` (upstream 1412-1427 carries both a reads line and an events line). Tell both packages; root resolves that hunk by keeping both lines.

**The seam between `chord-store` and `chord-queries`** is defined by tests: `chord-store` makes the upstream `test/store/*` cases pass, including the twelve host-level `live-query.test.ts` cases that use `self.query`. Everything in `test/host/{named-query,query-reader,read-declarations,list,tiling}.test.ts` is `chord-queries`. The internal types the store must freeze for it are listed in 3.5.

**`chord-queries` owns the park refactor.** Native `Attempt.ActivateAsync` (`Attempt.cs:83-195`) runs the factory, then parks in an inline loop, then runs `ActivateOwnAsync` (196-215) under `WaitAsync(ReadyTimeout)`. Upstream has a reusable `#park(dependencies, step, deadline)` that `#awaitReaders` and admission's `#decide` both drive. `chord-queries` extracts it; `chord-admission` reuses it.

**Test helpers.** `tests/PiSharp.Chord.Tests/ChordSupport.cs` (672 lines) is used by every area, and upstream's `test/support.ts` grew by 228 lines of fixtures (`Match`/`MatchV2`/`serveMatch`, `Ticker`/`serveTickers`/`ticking`, `Desk`/`desk()`, `recorder`, `until`, `waitLive`). Each package should put its helpers in its own new `Sync160<Area>Support.cs` (a `partial class Program` file) and not edit `ChordSupport.cs`, or two parallel packages will conflict there.

### 4.4 An optional overlap

`chord-remote` has two merge points. If root merges R1 on its own (windows, events, `opened`) and `chord-store` is merged, `chord-queries` can start while R2 (shared holds) is still running. The files both would then edit are `Host/Internal.cs` (`ITarget`: shared holds change `CallAsync`/`Retry` and add `GenerationOf`; queries add `QueryMember`), `Host/Reference.cs` (different regions of `Binding`), `Host/Object.cs`, `Host/Host.cs` and one added method in `Remote/RemoteObject.cs`. These are adjacent-line conflicts, not logic conflicts. It shortens the critical path by about one package; the default plan above does not rely on it.

### 4.5 What lands first

`chord-foundation` is the foundation everything else needs: the contract shapes (`VariantData`, `MemberKind`, `VariantNames.Events`), `FailureKind.Denied`, the wire messages and codecs, `Channel.Fence` and `FramedSource`. Landing those as one package means no later package edits `Contracts/**` or `Link/**` at all, which removes the two worst three-way collisions (the positional `VariantData` record and the codec tables). `chord-edge` is independent and can merge at any time before `chord-workers`.

## 5. Risks and unknowns

### 5.1 Where the native port diverged at 1.5.2

1. **Activation order.** Native runs the factory before any parking and awaits the previous attempt's close before `ActivateAsync` (`Object.cs:73-76`). Admission needs a phase before the factory, started without waiting for that teardown, and readers need a park after the own tables are prepared and before natives. This is the largest structural change in the sync.
2. **Object state.** Native has one `RefState` plus `Current`/`swappingSource`/`openOld` (`ReloadObject.cs`); upstream's `#rebuild` (930) and `#cut` (1003) have no counterpart. A denial must cut holders off with no value, while "another value" keeps last values during loading; native `swappingSource` deliberately keeps a sealed attempt visible.
3. **Presence.** Native recomputes presence from `State.Kind == "live"` (`Host.cs:258-260`); upstream keeps holders listed across a rebuild to another value and unlists only on denial.
4. **Remote internals.** No `CallWindow` class, flag fields where upstream has a `HoldState` union, string signals on `RemotePath`, one `RemoteObject` per binding in `RemoteIntegration.cs:38-45`, async handlers where upstream is synchronous. Shared holds rewrites the last of these.
5. **Threading.** Upstream `Reads`, `Read`, `OpenRead`, `OpenQuery` and `Tiling` assume one thread; native store code is lock-based. `Read.latest(step.executor)` needs a stable reference identity for a step's executor across the steps of a grouped commit; that was not verified.
6. **`JsonNode` has one parent.** Upstream `FramedSource.deliver` diffs by reference and `DecodedRows` reuses unchanged row objects. Native `LiveQuery` works around the single-parent rule with row tokens and a bespoke row diff. The `FramedSource` port needs a pluggable differ or a generalised token diff under `rows`; `chord-foundation` writes the base class and `chord-store` its first subclass, so the two must agree on this. Suggest foundation ships `FramedSource` with an overridable diff and `chord-store` reports back if it has to change it.
7. **No microtasks.** Three behaviours depend on JavaScript microtask order: the event flush runs after every state message published in the same turn; the consumer sends one event ack per inbound turn after delivery; admission starts one microtask after a seal. Native has `HostDispatcher.Post`, a FIFO shared with await continuations. Each needs a deliberate ordering design, and the upstream tests assert the order.

### 5.2 1.5.2 limitations and decisions this work builds on

- The 1.5.2 wire work replaced nothing structural: a single `previousBinding` with `NextOwn`/`Acknowledged` counters. 1.6.0 turns that into a list of fences. `remote.152.loading-cannot-send-queued-index-after-reordered-bound` and `remote.152.method-index-keeps-old-binding-until-own-ack` may assert the old frame order (the consumer's ack now precedes `WentLive`) and need review, not deletion.
- **Remote coverage gap.** Native has 33 remote cases and 23 link cases; upstream had 227 remote cases at v1.5.2 and adds 100. The 1.6.0 changes rework the least-covered paths (relay call lifecycle, rebinding, `RemoteObject` end and move fan-out). `chord-remote` should port most of the new upstream cases and backfill a few 1.5.2 relay and hold cases before changing those paths.
- The 1.5.2 store rows are `tested-with-limitations`; the recorded limitations are Durable-suite skips on this machine (symlink privilege, `PISHARP_DURABLE_UPSTREAM` unset), not store gaps.
- Workers: `State` is a polled snapshot, not a watchable, so "published" means visible in `State` and reported through `OnError`. The exit path has an EOF grace and link-loss recovery (`LinkLostAsync`) that the new close at exit must not trigger; hence a silent `ObservedLink.End()`.
- Surfaces: the native watch delivers later frames only, so the "applies the set shown when it begins following" fix is already half present. Hosting/Surfaces is a PiSharp-only artifact seam; the esbuild, tsconfig and minification deltas stay no-ops.
- Upstream's reload note in `docs/slice-14.md` §4 says a list's bookmark is in the document; the code keeps `ListMemory` on the object (`object.ts` 680-698). Follow the code. Whether every native reload path reuses the `ChordObject` instance (for example a type removed and re-added) was not traced.

### 5.3 Wire and compatibility

- A 1.6.0 trusted host sends `hello` without `calls`. A PiSharp 1.5.x worker or client talking to a 1.6.0 server (or the reverse) will not interoperate. Upstream accepts this (events spec §2.6: nothing deployed). It belongs in the release notes.
- The goldens generator `tests/PiSharp.Chord.Tests/Link/generate-goldens152.mjs` pins the 1.5.2 commit and shims `message.ts` with three cause kinds. The 1.6.0 generator needs the new commit and `denied`; whether its slice markers still bracket `EVERY_KIND` at v1.6.0 was not checked. `tests/fixtures/chord-link-v152.json` should keep passing unchanged.
- Trusted links have no call window any more, so a provider never closes a trusted link for call count; `Remote/Provider.cs:81` (`>= 256`) must become conditional.

### 5.4 Store and SQL

- Three `read-plan.test.ts` cases assert `EXPLAIN QUERY PLAN` text; that depends on the SQLite bundled with Microsoft.Data.Sqlite and on the tables being `WITHOUT ROWID`. Not verified.
- `skip.test.ts` is a seeded oracle (mulberry32, seeds 1..8) with exact `{runs, probes, skips}` triples; the generator must be ported exactly or the triples cannot match.
- Table handle `page`/`count` SQL becomes aliased (`FROM "tbl" AS t`); whether `TableHandleCases` or `SqlCases` assert SQL text was not checked.
- Upstream stages `ReadRefresh` for every table a transaction touched, including one only read; native filters with `HasWrites` (`Transaction.cs:391`). Keeping the filter changes `chord.query.skips` relative to upstream. Decide deliberately in `chord-store`.
- Row identity relies on JavaScript `===` over SQLite values; in C# boxed `int` and `long` are not equal. Use the canonical JSON of the key values as identity.
- A `put` of a row outside a window now costs one more statement (the probe). No native test asserts the old count.

### 5.5 Breaking native API

Both assemblies are `[Experimental("PISHARP_CHORD001")]`, so breaking in place is allowed, but each needs a changelog line from root: `HostError.Table` -> `Query`; `ProcessWorkerOptions.Store` required; `TelemetryIngestOptions.Admit` required; `LinksOpened` gains a parameter; `Sessions` -> `SignIns` across about ten public names; `ServeOptions` and `ResolvedLink` gain trailing positional parameters (source-compatible, binary-breaking). The sign-ins rename also changes persisted and wire names: existing cookies, device tokens and the owner token file stop authenticating, and the old `chord.app.sessions` document stays unread in the store.

### 5.6 Durable and telemetry ingest

- `src/PiSharp.Durable.Store` references `PiSharp.Chord` and uses `ChordStore`, `Tables.OpenStoreAsync`, `Tables.SqliteBackend`, `Tables.DefineTable`, `TableOptions`, `DocumentScope`, `Batch.Within` and the registry value. `chord-store` rewrites internals under those; it must run `PiSharp.Durable.Tests`, `PiSharp.Chord.Durable.Integration.Tests` and build `benchmarks/PiSharp.Durable.Benchmarks` and `samples/Durable.Chord.Examples` before it reports.
- No Durable code calls `DeleteHostDataAsync`; its callers are `Host/HostData.cs`, `Store/HostData.cs`, `Hosting/App/Archive.cs:306` and tests. Delete by plugin resolves owners as the type names the named plugin currently serves: data under a type the plugin no longer serves stays, and an unloaded plugin is `invalid/args`.
- No upstream package outside `packages/chord` uses `store.reads`, `defineColumns` or `deleteHostData`, so `packages/durable` needs no change for these. Whether a commit made through Durable's own store path should refresh Chord live reads is the same open question it is upstream; nothing in 1.6.0 changes it.
- Telemetry ingest: `Admit` is self-contained. Two existing expectations change later, when shared holds land: the sender's own hold becomes a `chord.hold` root, and a first hold is a root linked to its getter (`telemetry-ingest/sender.test.ts`, `telemetry.test.ts`, no new cases). The native case `telemetry-ingest.sender.page-through-memory-link-no-ingest-feedback` will need new expectations in `chord-remote`, which does not own `TelemetryIngestCases.cs`; it should report the change for root to reconcile with `chord-edge`.
- `Telemetry152Cases` asserts 14 spans and 25 metrics; the schema rows make it 32 metrics and add the `chord.admit` event on three spans.

### 5.7 Benchmarks and test infrastructure

- `test/bench/{events,shared-holds,slice-14,state-binding}.ts` are new and run by hand, not in CI. There is no native Chord benchmark project (`benchmarks/` holds only `PiSharp.Durable.Benchmarks`), and the 1.5.2 table classed `test/bench` as `no-op/example-or-fixture`. Recommendation: no native counterpart. One caveat: `slice-14.ts` is also an acceptance script that exits 1 when a number in `docs/slice-14.md` §9 is missed; its exact counter assertions (runs and keyed probes per touch) are the part worth a native case in `chord-store` if root wants the acceptance kept. Its contents were not read in detail.
- `test/support.ts` fixtures need native equivalents (4.3). The workers tests also need a store hook that holds the next commit, and a fixture worker that reports and exits.
- Several new upstream cases assert garbage collection ("keeps nothing of the old code's named query", "collects a reader's live query", events and shared-holds GC cases). They need a weak-reference pattern; whether `Host/CallbackLifetimeCases.cs` has a reusable one was not checked.
- Examples 67-72 and 76 have no native counterpart and stay `no-op/example-or-fixture`; example 76 is covered by the workers cases.

### 5.8 Not determined

- Whether native host/remote aborts in-flight inbound calls when their link closes and refuses a transaction under an aborted call context. Two of the worker exit cases depend on it.
- Whether native `Batch.Flushing` runs a callback once in the current batch's flush, as `Decision.invalidate` and `GivenQuery.invalidate` need.
- Whether sealing `openOld` unblocks `RestartAsync`'s `DrainCallsAsync` wait cleanly (needed by admission's `#sealDraining`).
- Whether native `HostColumn` offers the key round-trip that `checkNode` uses to validate a `host(...)` operand.
- Whether upstream pins any literal fingerprint for a contract with a queries or event member (only inequality is tested), so the native fingerprint node cannot be checked against a golden.
- Whether native client and page start code passes `LinkOptions.Limits`.
- The design docs (`docs/slice-14-design.md`, `docs/admission/design.md`, `docs/events/spec.md`, `docs/shared-holds/design.md`) were read in part only.

## 6. What root owns or must decide

- **Applicability rows** for the 127 `src` files (section 2) and the 127 non-`src` files, with the per-change no-op reasons above; integrity re-seal for about 20 new C# files.
- **Changelog and release notes:** the breaking list in 5.5, the wire note in 5.3, and the sign-in again notice.
- **Docs that describe changed options:** `src/PiSharp.Chord.Hosting/README.md` L37 and `website/src/content/docs/docs/embed/chord.md` L33 describe `ProcessWorkers` options. The website file is root's; say whether the package README under `src/` is the work package's or root's.
- **Scope confirmation:** events, shared holds, call windows and the sign-ins rename are not in the changelog. This report plans them as in scope under "port everything". If the owner wants a smaller release, shared holds is the only one that can be deferred without blocking another feature's source, but admission's upstream tests assume it (the `desk()` fixture and the 1.6.0 `Binding` shape), so deferring it means adapting those tests.
- **Two API decisions** named in 3.5 (`admission(schema)` as a terminal edit; `Allow`/`Deny` as JSON).
- **Merge order:** foundation, then edge at any time, then store and remote, then queries, admission, workers. After `chord-store` merges, run the Durable suites before starting `chord-queries`.
