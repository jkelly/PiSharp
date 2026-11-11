# D-TEL: telemetry / evals / Chord telemetry discovery

## T0 / T1 implementation (approved by root)

Implemented `TelemetryClock.Now(TimeProvider?)` and `RoundMillisecondsToMicrosecond`, memory adapter microsecond timestamps, and trace renderer control-character escaping/microsecond durations. Added and registered three focused native telemetry cases in `ClockRender152Cases.cs`.

Implemented public `ChordTelemetry`, `ChordMetrics`, and `OpenSpan` in `src/PiSharp.Chord/Telemetry.cs`. The embedded schema was extracted from the frozen upstream schema expression, retaining every description, parent rule, required flag, cardinality and closed set; 14 spans and25 metrics confirmed by extraction. It loads using existing reflection-free `TelemetryJsonContext` metadata. Metrics use weak adapter keys and lazy typed-instrument caching. Added two automatically discovered Chord helper/schema/propagation/status tests in `Telemetry152Cases.cs`.

Exact cross-owner API: `ChordTelemetry.Schema`, `IsUntraced(ITelemetryContext)`, `CountingOnly(ITelemetryMetrics): ITelemetrySpan`, `Spans(ITelemetryContext): TypedSpanStarter`, `Metrics(ITelemetryMetrics?): ChordMetrics`, `Capped(string)`, `HostAttribute(Address?)`, `SpanFailed(ITelemetrySpan,string,Exception)`, `Ended(ITelemetrySpan,string,Exception,CancellationToken? = null): string`, `CarrierOf(ITelemetryContext): IReadOnlyDictionary<string,string>?`, `ParentOf(IReadOnlyDictionary<string,string>?,ITelemetryContext): ITelemetryContext`. `OpenSpan(parent,name,SpanAttributes)` exposes `Span`, `Ended`, `Completion`, `End()`, `EndWhen(Task,string,CancellationToken? = null)`. `ChordMetrics.Counter/UpDownCounter` return `SchemaTelemetryCounter`; `Histogram` returns `SchemaTelemetryHistogram`.

Signatures sent to D-CHO, wire, HTTP and root. No core/wire/HTTP source edits. `git diff --check` clean for changed tracked source/tests; no builds or test executions per serialized-build instruction. T2 and T3 assigned separately by root; do not infer those implementations are complete from this report.

Root validation built Telemetry successfully and all three new telemetry cases passed. Existing tied-root pagination case exposed an obsolete test clock setup: advancing only wall time by 1ms leaves anchored monotonic time unchanged, so root9 and child10 shared a start and the upstream lexical id ordering selected child10. The authored test now explicitly sets its monotonic clock to0 for roots and1 for children, preserving its intended tied roots/later children scenario. Runtime sorting remains upstream-compatible. Awaiting root rerun; no local build run.

Frozen comparison: Pi `f9a26365568127461c47a07a37dcb42c140a3ac5` (1.5.0) to `b4afdddf44e059c67ccf5097e5317d913521b470` (1.5.2). Native baseline is the update worktree; report only, no source edits or builds. Coordinated ownership with D-CHO: this report specifies helpers, ingestion and storage; owners of core/remote/docs/jobs/HTTP/app files integrate instrumentation in those files.

## Exact package delta

`packages/evals/package.json` is its only changed file: version and pi-ai/pi-coding-agent development dependencies become 1.5.2. There is no eval algorithm or test-runtime delta to port. Existing native Evals runtime is unaffected; root owns package/version pins.

`packages/telemetry`: changed `CHANGELOG.md`, `package.json`, `src/index.ts`, `src/memory.ts`, `src/render.ts`, `test/render.test.ts`, `test/store.test.ts`. 1.5.2 adds no changelog behavior; 1.5.1 adds shared `telemetryNow()` and fixes terminal control escaping. Native missing behavior:

* Shared clock returns epoch milliseconds rounded to 0.001 ms. High-resolution monotonic time plus wall-clock anchor; re-anchor when difference from current wall clock exceeds 10 ms. All in-process adapters, including Chord recorder, use it. Span end and event-relative times are rounded to microseconds as well.
* `renderTrace` rounds durations/event offsets to microseconds and escapes C0 U+0000..001F, DEL U+007F and C1 U+0080..009F as lowercase `\\uXXXX` in span/event names, link trace/span ids, attribute keys, error names and messages. Attribute values still use JSON serialization. Native `Memory.cs` uses truncated wall milliseconds and unrounded elapsed time; `Render.cs` currently writes these text fields raw.
* Keep native `TimeProvider` injection for deterministic tests; implement an anchored clock per injected provider and shared system-clock instance. Public native proposal: `TelemetryClock.Now(TimeProvider? = null)` and internal `RoundMillisecondsToMicrosecond` with JavaScript-compatible half-up behavior; old APIs remain source-compatible.

## Chord source inventory

New first-class files: `src/telemetry.ts`, `src/telemetry-ingest/{batch,facet,index,recorder,sender}.ts`, `src/telemetry-sqlite/{node,schema}.ts`. Changed existing `src/telemetry-sqlite/{index,store}.ts`. New tests: `test/telemetry.test.ts`, `test/telemetry-ingest/{facet,recorder,sender}.test.ts`, `test/telemetry-sqlite-node.test.ts`; existing SQLite test changed. Relevant changelog bullets: telemetry across hosts; four tutorial gaps (rows/host/microseconds/read-only reader); server-only store and page/worker ingest; startWorker drops direct telemetry-store option.

Instrumentation files after the `next` to root move: `host/{host,internal,attempt,ctx,root,object,native,route,routines,reference,facet}.ts`; `remote/{consumer,provider,relay,remote-object,hold,host-link}.ts`; `link/{message,binary,text,channel,part}.ts`; `documents/{documents,definition,persistence}.ts`; `jobs/{job,records}.ts`; `state/batch.ts`; `data/{validator,principal}.ts`; `store/{store,archive,json-column,live-query}.ts`; `node/http/http.ts`. Lifecycle integration is in `node/app/{app,worker}.ts` and `web/page.ts`. Existing cost-counter files contain ordinary port/move changes as well, and should not all be classified as newly added instrumentation.

Native already has `HostOptions.Telemetry`, `HostOptions.Address`, `ChordContext.Telemetry` and derivation, low-level cost counter calls, state Batch host-metrics binding, persisted job carrier capture, and v1 SQLite recorder/store. It has no Chord schema/helper file, no general host span instrumentation, no link trace fields/codecs, no ingest/recorder/sender API and no read-only file-opening telemetry reader. `ServeOptions.Untraced` exists in the working tree but instrumentation consuming it is missing. Avoid reimplementing already-present options when integrating.

## Chord schema and helper contract (T1)

Public native proposal `ChordTelemetry.Schema` (version 1) in `PiSharp.Chord`, using existing `TelemetrySchemaDefinition`; internal helpers follow upstream and are shared by core/hosting owners. There are **14 spans and 25 metrics**. Required `chord.host` on every schema span is address key, or `client`; externally chosen labels/addresses/principal kinds cap at 256 UTF-16 code units. Failures use fixed category and first 500 characters of message; cancellation by supplied token or Chord `aborted` has status OK and outcome `aborted`.

| Span | Other start attributes | End attributes/events |
| --- | --- | --- |
| `chord.call` | type, member, object, address.kind, optional chord.principal | outcome ok/error/aborted |
| `chord.method` | type, member, object, principal.kind, optional chord.principal | outcome ok/error/aborted; commit |
| `chord.relay` | type, member (hold or #index), upstream.kind | outcome ok/error/aborted |
| `chord.hold` | type, object, address.kind | outcome bound/failed/ended |
| `chord.activate` | type, object, attempt (from 1) | outcome live/failed/parked/ended; park, retry |
| `chord.open` | type, native (from 1) | child of activate |
| `chord.close` | type, native, reason end/close/reload | root/external parent |
| `chord.load` | plugins | failed |
| `chord.reload` | plugins | added, changed, removed, failed, fates; reload.fate |
| `chord.resolve` | kind, address | outcome linked/failed |
| `chord.revoke` | none | count |
| `chord.routine` | type, object, routine on/onLive, position | outcome ok/error/aborted; commit, retry |
| `chord.job` | job, phase intent/run/finish, object, optional job.id | outcome ok/error/aborted, optional job.id; commit, retry |
| `chord.http` | method, route (pattern, never request path) | status; commit |

`chord.commit` has required numeric documents/ops/**rows**/duration; retry has backoff; park has cause; reload.fate has object and fate restarted/ended/parked/retried.

Counters: `chord.data.compilations`, `chord.data.validations`, `chord.data.walks`, `chord.link.checks`, `chord.link.deflates`, `chord.link.inflates`, `chord.link.encodes`, `chord.store.loads`, `chord.store.commits`, `chord.store.statements`, `chord.query.runs`, `chord.cycle.visits`, `chord.derive.computes`, `chord.derive.diffs`, `chord.registry.composes`, `chord.registry.adapts`, `chord.contract.adapts`, `chord.link.state_messages`, `chord.link.bytes` (unit By, direction sent/received). Up/down: `chord.calls.in_flight`, `chord.holds.active`, `chord.links.open`. Histograms: `chord.commit.duration`, `chord.park.duration`, `chord.backpressure.wait` (unit ms). All but bytes have no metric attributes. Never put ids into metric attributes.

Helper contracts:

* `IsUntraced(ITelemetryContext)`, `CountingOnly(ITelemetryMetrics)`: avoid allocating spans, attribute objects or carriers under no-op, but host work started under Background receives host metrics. C# needs a marker/interface for counting-only rather than TypeScript function identity.
* `OpenSpan`: callback-based adapter span stays open via task completion source until idempotent End; exposes ITelemetrySpan immediately for synchronous adapters; EndWhen sets outcome/error before ending.
* `SpanFailed`, `Ended`, `Capped`, `HostAttribute`, `CarrierOf`, `ParentOf` correspond to upstream. Unknown/absent carrier falls back to adapter root; extraction helper must never throw.
* `Metrics(ITelemetryMetrics?)`: weakly cache per metrics instance and lazily cache one instrument per name; no-op cached separately. Do not pin adapters globally.

## Core and wire integration contracts (owned by D-CHO workers, not T1)

Host exposes adapter/metrics/address/incarnation/principal-attribute function. Default principal attributes are `chord.principal: principalKey`, capped; custom hook replaces default, invoked per span, not cached. Root options gain trace `{ name, attributes? }` for fresh adapter-root span with stamped host. `CallContext.TraceAsync(name, attrs, callback)` creates a child context/span, sharing parent caller/signal/reference lifetime and stamping host. No new business lifetime is introduced.

Open activations/holds/resolves are roots linked to first triggering context; reload events match object fates; closes root at teardown. Methods record local and remote execution; calls record remote consumer work. A reference carrier rides only the first hold; re-hold after reconnect uses fresh root linked to old trigger. Relays create span under incoming carrier and forward their own carrier. Job intent/run/finish span continuation uses the already-persisted trace carrier after resume. Document commits record rows from actual SQL write counts and commit duration, including table-only changes.

Wire: optional trace record on hold/call; max 8 keys, keys/values 1..256 printable ASCII (no spaces restriction applies to ingest ids, not necessarily carrier); malformed carrier fails message schema and closes link. Binary `0x40`, origin remains `0x80`, call old-work flag `0x20`; base kind masked accordingly; protocol version remains 1. Text hold trace is index 10; text call trace index 5, old flag index 6; placeholders null when needed. Existing v1 no-trace frame bytes remain unchanged. Bound peer hold type/address kind also max 256. Coordinate one wire owner for trace + old flag, never separate conflicting codec edits.

HTTP takes adapter, uses `chord.http` span lasting until body delivery, bounds/validates incoming trace headers, and continues traceparent/tracestate only on a verified contributed-relay request. Direct/browser supplied trace headers do not become parent. W3C header max lengths 256/512, printable ASCII. Route pattern/method/status only. The HTTP owner must integrate T1 helper; do not route trusted-proxy policy through generic ingest.

## Recorder / ingest / send contract (T2)

New native `TelemetryBatch` = settled spans + metric aggregates; `ITelemetryIngestTarget.Ingest(batch)` returns queued span count. `TelemetryRecorder(Func<string> scope, int capacity, TimeProvider? timeProvider)` implements context/propagation with `Take(partial, limit)`, `Release()`, `Pending(partial)`, `QueuedSpans`, `Stop(keep/discard)`, `DroppedSpans`, `DroppedMeasurements`. Scope lazy-read once at first span; ids scope:n. Shared process-wide identity registry means all recorder instances recognize one another's contexts; carrier keys stay `pi-sqlite-span` / `pi-sqlite-trace` for existing persisted jobs.

Capacity separately bounds queued+inflight spans and pending+inflight metric interval/series pairs. Take returns oldest spans up to limit, completed 10-second metric intervals (all with partial); capacity retained until Release. Metrics sorted by attribute key and merged count/sum/min/max on canonical series key `[name,kind,unit-or-null,JSON(sorted attributes)]`. Ingest merges rows and never executes SQL. Stop keep drains later; discard drops waiting data. Settled/stopped spans do not record; callbacks and errors still run normally.

Public contracts `PageTelemetry` remote `chord.telemetry.page` v1 main.ingest(PageBatch)->{written,dropped}; `WorkerTelemetry` remote `chord.telemetry.worker` v1 worker.ingest(WorkerBatch), other empty; destination host kind server. Strict schemas:

* Page max 100 spans; ids nonempty printable ASCII without spaces max128; names nonempty/control-free max128; attributes max32 keys (1..64/control-free), scalar values string control-free max256/number/bool; events max16, links max8; error name max64/message max500; times numeric nonnegative.
* Worker max1000 spans and1000 metrics; ids max256, name max1024, attributes max256, values string max65536/number/bool or arrays of string/number, events max1024, links max256. Worker metric interval/count integer nonnegative, name max256, unit max64, max64 attribute keys, scalar value strings max4096/number/bool. All span names/keys/errors remain control-free. Ingest is already shape-validated by contract before facet callback.
* Facet checks page id/traceId/parentId all begin `browser:<ctx.incarnation>:` and end >=start; rejects others. Strips all page `chord.*`, stamps `chord.host=browser` and server principal attributes (hook per span). Budget 1200 accepted spans/page/rolling fixed minute, at most1000 page budgets per principal object, evict oldest. Receipt dropped=batch length - queued count (includes shape-valid refusal/rate/capacity).
* Worker variant is available only when workerHost(principal) returns address key; stamp host and preserve rest; metrics forwarded. Both serve options untraced, no telemetry-about-ingest feedback loop.
* Sender holds server object; SendAsync(partial) serialized. Unbound -> release taken capacity and reingest batch at back; refused/failed call -> give up batch and release (best effort). Page sends spans only, max100 and drops taken metrics; worker max1000 spans/1000 metric rows. Calls use Background. Stop releases held reference.

## SQLite contract (T3)

Refactor existing SQLite recorder to compose T2; retain API compatibility/options/caller-owned DB. New Ingest public API. INSERT OR IGNORE spans, so repeated ingested ids never replace stored rows. Same transaction writes aggregates and retention; fatal write stops/discards recorder and all subsequent flush/close surface same error. Serialize writes, preserve in-flight capacity. Close stops recording and drains partial metric intervals without waiting on business callbacks.

Schema migration table remains `telemetry_schema`. Preserve version1 schema as first migration. Version2 builds replacement table with INTEGER started_us/ended_us, copies existing times with CAST(round(ms*1000) AS INTEGER), replaces table, recreates ended_us / trace_started / started_us indexes. Events remain relative ms JSON. Write rounded integer microseconds; reader returns ms by /1000. Span order `started_us, length(id), id` (counter9 beforecounter10 at same microsecond); first-span-name predicate uses same order. StartMin/Max bound values multiply by1000. Cursor now stores `[started_us,trace_id]`, order still descending start/ascending trace id. Existing cursors opaque; no compatibility guarantee across schema migration.

Read-only reader checks table existence then exact version2; fails missing/older/newer. No create/migrate/write. Close owns only read DB. Native Chord project has only ISqliteDatabase interface; native implementation and adapters are in Durable, so propose portable `OpenReaderAsync(ISqliteDatabase ownedReadOnlyDatabase)` plus file path convenience in Durable SQLite. D-STO notified to own native read-only open mode, T3 to own checker/store view. Do not introduce Chord->Durable project cycle. Query implementation currently lives static SqliteTelemetryStore; keep static methods while adding an instance handle/new wrapper for lifecycle.

## App and surface contracts (D-CHO owners)

Server telemetry option must implement context + ingest target; one store on server. Worker start payload kit.telemetry boolean; worker creates recorder capacity10000, injects host adapter, sends every1s, final partial send bounded1s before host close, never opens server telemetry file. Page telemetry opt-in boolean; recorder scope browser:host incarnation, periodic3s and hidden send. Browser page lifecycle is TS/browser-specific but data contract and sender remain applicable for interoperability/native future clients. Do not discard entire ingest API as browser-only. New app/surface owners should call T2 APIs and own their timer/lifecycle glue.

## Implementation split and validation gate

1. T0 telemetry package: `src/PiSharp.Telemetry/{Memory,Render,TelemetryClock}.cs`, telemetry tests. Independent.
2. T1 Chord schema/helpers: new `src/PiSharp.Chord/Telemetry.cs`, helper tests. Publish exact helper signatures before core integration.
3. T2 recorder/ingest/sender: new `src/PiSharp.Chord/TelemetryIngest/**`, tests. Depends T0,T1 and core accepting Untraced/Trace/wire; pure recorder can proceed first.
4. T3 SQLite: `src/PiSharp.Chord/TelemetrySqlite/**` and tests, depends T0/T2. Coordinate read-only underlying connection option with D-STO.
5. Core/wire owners integrate T1 helpers into their owned paths; docs/jobs owners add events/carrier phases; HTTP/app owners handle their lifecycle. No T0-3 worker edits these concurrently owned paths.

Required evidence after implementation: deterministic wall-jump/submillisecond clock; render allcontrol-field escapes; recorder cross-instance propagation/capacity/inflight/metric merge/stop; page spoof/rate/schema/attribute-stripping and workerauthorization; sender unbound requeue/refusal/serialization; SQLite v1migration/no row loss/integer units/duplicate-ingest/tie order/read-only missing-old-newer files; trace call through consumer->relay->provider->commit including host/principal/cancellation; no adapter no spans/carriers; job phases continue after resume; schema conformance and finite metric cardinality. Root serializes dotnet builds/tests. No build was run during discovery.
