# D-CHO — Chord 1.5.2 discovery and implementation contracts

Discovery only, 2026-11-10. No production/test source edits, builds, services or package installations performed. Native tree `E:/AI/PiSharp/update-1.5.2`; upstream `E:/AI/PiSharp/upstream-v152`, frozen `b4afdddf44e059c67ccf5097e5317d913521b470`, compared to `f9a26365568127461c47a07a37dcb42c140a3ac5`. Requested ultrafast tier is not independently verifiable.

## Decisions controlling the update

- Normalize old `src/next/X` to `src/X` and `src/next-node/X` to `src/node/X`. This is much more than relocation: app kit, client surfaces, terminal launcher, telemetry-ingest, host-data, live-query and reload coordination are genuinely new.
- **Final source supersedes intermediate changelog claims.** Changelog says createWorkers returns a rejecting promise and startServer waits for worker builds. Final `node/workers/workers.ts` has synchronous createWorkers plus cached async build/Built outcome; final `node/app/app.ts` listens first, builds bases off startup path, exposes basesBuilt/baseWatched and isolates failed-base consumers. Likewise the changelog's dependent-first pure-swap paragraph is superseded by final all-live-object restart. Implement the frozen source.
- Keep public C# `Legacy` APIs and tests. Upstream deletion of JavaScript v1 exports does not authorize deleting C# APIs or migrating namespaces. Document ChordNext naming separately.
- Existing gap closure supplied real Host/Documents/Jobs, remote transport, collectible plugin ALCs, HTTP and contained child workers; preserve the dispatcher and original-callback generation leases. N1/review-chord-hosting describe prior Windows evidence. Current sources still have pure-swap, no old-work chain, loading cancels pending calls, no host-data/live-query, no public HTTP option, latched base watcher and no App/Surfaces runtime.
- Native ProcessAddressLock uses OS mutexes: the BSD stale socket/ENOTSOCK defect is avoided by design. An executable path alone does not freeze later-loaded DLL dependencies, so worker-base snapshots remain a real native requirement.

## Ownership and prerequisites

Store/SQLite/JSONL/sync/host-column/live-query engine contracts belong to `sync-1.5.2-storage-discovery.md`. Telemetry schema/helpers/ingest/SQLite belong to `sync-1.5.2-telemetry-discovery.md`. Existing-file owners below integrate those helpers. One codec owner implements old-work 0x20 and telemetry 0x40 together.

| Work package | Exclusive source ownership | Dependencies and seam |
|---|---|---|
| C-K kernel | Existing Host/* except new HostData.cs and RemoteIntegration.cs; Data error/failure/principal; Registries/*; Jobs/Job.cs | Sole editor Host/Contracts.cs, Attempt.cs, Object.cs, Reference.cs, Staging.cs. Integrate HD's declarations/load gate and TEL's instrumentation. |
| C-W wire/remote | Link/*, Remote/*, Host/RemoteIntegration.cs | K OldWork/StateFor/call gate; TEL carriers. Both wire flags and all golden frames here. |
| C-HD keyed/live data | Contracts/*, Documents/*, new Host/HostData.cs | Store HostArchive/HostCommit/import/query seams; sends K precise HostOptions/ISelf/IStoreSelf/Attempt/Object/Reference changes. |
| C-HTTP | Hosting/Http/* | K pre-load guard; TEL helpers; A1 uses LinksOpened. |
| C-PL plugins | Hosting/Plugins/* | K internal test introspection; additive native formats. |
| C-WK workers | Hosting/Workers/* including new WorkerBase.cs | PL BaseInputs; W link closure. No Tools/OwnedChildProcess edits without root coordination. |
| C-A1 application/auth | New App startup/layout/info/secret/session/owner/pairing/users/invite/ephemeral files | HTTP/PL/WK; calls A2 FilesFacet and A3 archive service; TEL recorder. |
| C-A2 files | New App files/index/entry/blob/upload/folder/copy files | Store and HD transactions/query; A1 supplies directory/options. |
| C-A3 archives | New App Archive.cs/Tar.cs/HostData.cs | Store archive, WK locks and A2 file enumeration. Distinct from kernel HostData. |
| C-SUR portable clients | New Chord/Surfaces/* | K+HD; themes/languages/preferences/page-state/client connection. Can split language/theme and connection ownership if needed. |
| C-PUB publication | New Hosting/Surfaces/* | PL+HTTP; accepts ISurfaceBuilder artifact contract, no claim DLLs execute in browser. |
| C-JS optional adapter | JS browser/React/terminal assets and tests | Explicit root disposition; native ALC is not a permission sandbox. |

Root owns common project/package references, shared test Program.cs registration, metadata, compatibility/source inventories, examples and all serialized builds/runs. No worker should concurrently edit K's shared partial files; send additive seam requests instead. Tests belong in separate files per owner. Existing tests expecting swapped/anonymous routes need intentional behavior updates, not deletion.

## C-K kernel: exact functional deltas

Upstream host/{host,attempt,object,ctx,root,reference,staging,unbound}.ts, new old-work.ts/readiness.ts/reload-cycle.ts/unbound-clock.ts, jobs/job.ts, data/{errors,failure,principal}.ts and registries/{record,value}.ts.

1. Add HostOptions.ReloadDrainTimeout default5s, effective min(CloseTimeout,configured). Every LIVE changed-plugin object restarts, with new attempt/instance/factory; nonlive objects retry. End fates first, other fates next, then start successors after all served types changed. No native-count/table-migration pure-swap path. Reload report emits restarted/parked, never swapped, preserving public record type/string API.
2. Old work is calls running at commit and descendants through their Chord context, plus trusted remote old calls. Context call-chain nodes identify actual running attempt and parent; WithoutAbortSignal preserves nodes. Host count permits zero-cost/no chain walking when no old work. Root begun under a call keeps its values. Old calls use old attempt's own contract/provider/adapters, ordinary calls gate for successor.
3. BeginDrain records open old attempt, stops routines, and repeatedly drains method/routine originals because old descendants can arrive while draining. Absolute reload deadline bounds both readiness and calls; report drain_timeout then seal/release stuck callers and close natives. Keep original-callback generation leases beyond public timeout.
4. Readiness binds staged successor TYPE requests only after all fates start. Keep these hidden from presence; retain until successor own bindings installed. Remote contract_mismatch/unavailable-version waits until deadline (other host may update); real failed dependency exposes parked cause. Registries use publication/contribution handoff rather than readiness bindings.
5. StateFor(version,fingerprint) presents compatible open old attempt live to that version, not successor version. Old-work getter for version dropped by new code binds compatible old attempt immediately or fails immediately; never waits for future code.
6. Calls arriving at activating object wait live/failed/ended up to ReadyTimeout, then unavailable/park_timeout. Failed parked dependency returns its cause. This includes calls sent with old remote indexes while consumer saw live. Stop returning not_ready for such reload calls.
7. Track activation edges through declared dependencies and open callbacks waiting at object gates. A wait of old work reaching its own draining attempt fails unavailable/reload_cycle immediately; recheck when later park/gate edge closes cycle. Compatible open-old binding is not a cycle.
8. With natives withdraw old contributions at seal; without natives retain until successor live and replace in one batch. Withdraw retained contributions on successor failure/park or object end. Keep old references until successor bindings exist. This is still restart, not pure swap.
9. All in-memory job runs abort with reload each reload; remove internal handover. Persisted run resume remains. Routines restart at successor live. Preserve public job API.
10. Local Get/Hold/Registry/root Ctx.Get before Load immediately throw not_ready (call load first); incoming remote holds still await publication. Empty client explicitly Load([]). Validate all supplied plugin names with npm-style predicate BEFORE any load/reload mutation, retaining duplicate/failure closure.
11. Add unavailable reasons version/reload_cycle without bumping protocol1. WaitingFor wrapping contract_mismatch or unavailable/version preserves version reason across nesting/hops. Public Forbidden(message,details) helper and canonical SamePrincipal supplement current Invalid. Canonical owner equality deliberately ignores custom principal-key function.
12. One per-host timer reports unbound_long after5s, name/latest error/start timestamp, once per waiting episode. Covers unserved/mismatched type and registry missing first value/failed first compose. Does not end wait. Stop on usable/last-good value, release, wake or close.
13. Registry_undeclared reports nonempty contributions/no provider at publication or host-lifetime contribution arrival, once per changed ordered contributor set; serving resets suppression. Internal UnservedNames/UndeclaredRegistries/ActivationsUnderWay support production plugin test harness.
14. Uncommitted transaction cancelled with its context reports transaction_cancelled and rejects. Admitted successful commit not retroactively cancelled. HD owns Documents report call; K supplies HostError and owner hook. TEL owns helpers/schema; K wires Address/Incarnation/PrincipalAttributes/RootOptions.Trace/CallContext.Trace and spans in its files.

Required regressions: pure restart new instance; nested calls/open descendants old code; ordinary call gate; dropped version old binding then refusal; initial/later cycle; remote skew; hidden presence; native vs native-free contribution handoff;5s cap; jobs abort; pre-load local refusal vs remote wait; invalid name no mutation; one timer/episode and registry suppression; cancelled vs committed tx. Reference test/host/{old-work,readiness,reload-cycle,unbound-clock,host,object,attempt,reference}.test.ts and node/reload/*.test.ts.

## C-W wire/remote

Upstream link/{message,binary,text,channel,values}.ts and remote/{consumer,provider,hold,relay,remote-object,host-link}.ts.

- CallMessage gains old true-only marker; hold/call trace carrier uses TEL contract. Binary old0x20/trace0x40, text call seventh slot old (trace preceding optional slot). Use final codecs as exact oracle for missing/null positions. Preserve protocol1/backward old-frame decoding. Trace max8 keys, printable ASCII key/value≤256; hold type/address-kind≤256. Invalid envelope closes link.
- Mark outgoing call old from context; honor only trusted link. Count actual original remote call old until settlement. Relay strips old from untrusted downstream even when telemetry disabled.
- Loading does not abort/reject in-flight calls; failed/gone/release still do. Preserve previous variant/method-index names through pending/rebinding and until ACK of fresh Bound own message. Add monotonically increasing own sent/acked counters separate from relayed ACK attribution. No call replay.
- Provider.Reply sends pending state first as ONE own state message even beyond full K2 window, then result/error, including relay replies. Count/ACK normally; no extra frame when nothing waiting. Bound remains within combined outstanding calls/messages allowance.
- Host tick detects scheduler pause; channel shifts last-received by pause but at most now-ping, pings immediately and allows close-ping response time. Sleeping laptop must not close healthy links before queued traffic reads.
- JS Buffer.toJSON byte extraction fix is platform-specific; native Parts.Payload explicitly handles byte[]/ReadOnlyMemory. Preserve nested byte payload tests, do not imitate JS object introspection.

Tests: exact old/trace/both codec goldens+old frames; trust hopping and malformed carriers; loading queued/running calls, method reorder before/after Bound ACK; failure cancellation; full-window state-before-result/error through relay, balanced ACK; simulated long scheduler pause then true silence.

## C-HD keyed docs/live host data

Upstream contracts/{contract,declare,evolve,fingerprint,host-kind,schema,accepts}.ts; documents/{definition,documents,handles,keyed-slot,persistence,slot,transaction}.ts; new host/host-data.ts; K integration sites Attempt/Reference/Members.

- Add StateOptions.KeyHost:HostKind? preserving Keyed:bool and all existing string methods. New distinct IHostKeyedHandle supports Address keys Get/Watch/At/Change/Retire/Keys; add ITx overloads, ISelf.HostKeyedState and reference accessor via K. Do not replace string signatures with object.
- Keyed contract/editor overload takes HostKind; fingerprint node adds key:kindName, string-key old fingerprint byte-identical. String↔host or host-kind change requires refuse mapping (value mapper cannot map keys). Declared/member/reexposed source key kind must match.
- KeyOfAddress validates actual kind, EXACT fields/types incl safe integer/literal enum, recomputes canonical escaped key from params instead of trusting Address.Key. AddressOfKey decodes typed params; KindOfKey returns prefix. Wrong/raw/inexact keys invalid/args. Additional routing constraints still checked by router.
- Persist DocumentAddress.Host canonical key. Check stored vs declared ownership on load; no automatic conversion. Enumerated host keys become Addresses ordered by stored key. Slot Holds/ReadChanged/AdoptChanged handles absent→retire, replaced→new tracker, unchanged id/revision→no-op, unreadable→fail owner AFTER batch.
- HostDataGate operations FIFO; exclusive writer waits activation load readers, future readers wait. Preparation may activate migration owners between exclusive sections. Cancelled queued turn must still preserve predecessor ordering; abandoned line acquire releases when eventually acquired.
- Host.ExportHostData/ImportHostData/DeleteHostData require store/outside batch. Validate archive first; default existing refuse. Find affected owners, activate served local empty-param owner when migration history missing and recheck; absent history rejects with no write. Lock object lines in identity order, including resident/pinned slots of host kind; reread docs/queries in same storage step, adopt once post-commit batch. Cancellation admission checked, after admission uncancelled. Always release temporary references/lines.
- IStoreSelf.Query(table,q,required limit) returns watchable JSON row array; exact table belongs to attempt/q names it, factory only, staging validates. Tables prepare then query loads before live. Object LiveQueries registry survives attempts; detach per attempt. Transaction refresh one per touched query table after writes, adopt in commit batch; no-write runs none; failed member/query_failed once heals next good run.
- Store seams agreed: HostArchive(Host,Tables,Documents), HostDataCounts(Documents,Rows), HostCommit(CommitFence Fence,IReadOnlyList<IStagedWrite> After), HostImportPlan(Target,Existing,Histories); ExportHostData, ReadHostArchive, TablesToMigrate, HostDataOwners, internal import/delete; QueryRefresh(Write,Adopt), LiveQueries registry per ITableOwner. Final storage report signatures prevail.

Tests: old fingerprints, forged keys/escaping/literals, rekey refusal; two-host isolation; live import replace/delete atomically updates resident/cold pinned docs+query; lock ordering and activation race; history/refusal/cancel rollback and post-admission cancellation; unreadable import owner failure; query owner/limit/failure-heal/diff/runs-once. Reference test/host/host-data.test.ts, store/{host-data,host-column,live-query}.test.ts, contracts/docs tests and example66.

## C-HTTP: native admission/headers/hooks

Own Hosting/Http/*. Preserve prior generation leases, request originals, relay framing, error-path CSP, partial listener disposal and size bounds.

- Extend HttpRoute with trailing Public=false, preserving old positional arguments; CrossSite implies public. Existing bare socket delegates stay private. Add explicit socket options API without replacing source-compatible dictionary type. Authenticate first; missing principal gets401 BEFORE plugin handler/wrappers on private route/socket. Links always401, public socket may run anonymous. Unloaded host503 immediately.
- HttpRequestContext.RemoteAddress from actual connection RemoteIpAddress, never forwarded header; relay peer remains relay address. SignedCookies.ReadSigned gives value+nullable expiry; old Read delegates preserving integrity/expiry behavior.
- ChordHttpOptions.LinksOpened(request,principal) returns optional cleanup invoked once at socket close; hook starts after Accept. Report exceptions by hook identity. A1 uses it for ephemeral sessions.
- Contribution ScriptPaths aggregated to narrow script-src to admitted-origin paths plus nonce, existing origin fallback when absent; preserve error headers/framer handling.
- Protocol-error websocket closes complete handshake (oversize1009), no unconditional abort when already closing. Check native LimitedWebSocket lifecycle.
- TEL HTTP spans only routed method+pattern, never raw path; only verified-relay traceparent/tracestate accepted within bounds.

Tests: anonymous private handler count0/public success/links401; unloaded503; peer vs spoofed header; signed expiry; hook+cleanup once; CSP normal/error; real oversize websocket1009. Mark existing intentionally anonymous test routes Public explicitly.

## C-PL: plugin model/base watcher

Own Hosting/Plugins/*. Existing scanner, first-root precedence, path containment, bounded builds, shared identities/ALCs, failure retention, rollout and callback leases already supply baseline. Missing/new:

- PluginRoot(Path,Required) overload, carry real Root/Required/Layout on plugin and problem. Missing required root is problem; after load settles any required discovery/build/evaluation failure rejects PluginError, optional failure remains report. Keep IEnumerable<string> constructor/API.
- PluginManagerOptions AppApi(name,versions newest-first, exact per-kind shared assemblies), kind descriptors/base inputs. C# analog validates declared API version/kind and refuses direct Chord dependency under configured policy, at build and runtime dependency closure. ALC alone does not implement TS import policy. File/line diagnostics where build provides them.
- Optional native single-file C# source format must be explicit with host-only references; TS/TSX cannot become native merely by recognizing filename. Keep chord.plugin.json. Package plugin-folder/glob/exclusion metadata needs nested-overlap, duplicate, realpath checks. Native NuGet/archive adapter distinct from npm artifacts.
- Final TS removes virtual @app/plugin imports: same-root relative copies/dependency package exports. Native project/assembly/package reference closure records inputs and dirties every consumer of copied/shared source; policy prevents escaping roots. Ordinary libraries do not inherit plugin-kind filename rules; plugin modules do.
- TestPluginAsync builds production policy, loads in-memory store+host with stubs, repeatedly collects unprovided registry contributions, lists unserved requests and reports errors; K supplies diagnostic introspection. No permissive test build path.
- BaseChanged becomes content-derived: tracked hash of bytes build read, missing/new source files; restoring identical bytes clears. WatchBase(inputs) registers worker/surface artifact records. Exclude dependency/build/VCS directories. Best-effort server input graph with explicit scan limits; final TS latches mtime/ctime post-process-start/pre-scan uncertainty. Native loaded byte snapshot can avoid guessing but must document dynamic assembly omissions.
- Explicit InstallPluginAsync(source,root,context): Git ref or package version; claim destination exclusively; owned staging; validate discovery/conflicts across roots; atomic rename; cleanup only owned files. Reject option-like refs/URLs/name/version. Stale owned staging cleanup and bounded dependency installer; no install executed for this update. Track install dirty state and retain snapshot marks on cancelled/failed rollout.
- JS-specific esbuild resolution/source maps/CommonJS interop/CSS modules/browser assets/import.meta checks require a real JS adapter or explicit native nonapplicability. Do not claim C# loader supplies them.

Tests: required vs optional; root/file layout; real API/kind policy incl dynamic load; cross-project dirty propagation; base identical/revert/deleted/new input; install collision/traversal/failure cleanup; test harness unserved contracts. Preserve actual ALC late/private-load and cached-facet tests.

## C-WK: immutable worker base/link loss

Own Hosting/Workers/* and WorkerBase helper. Preserve OwnedChildProcess, private pipes/address mutex, graceful stop/descendant cleanup, startup-host ownership and reload-busy idle.

- Cached BuildAsync/Built outcome; constructor can remain sync. First start awaits build, app initiates proactively after listen. Failure isolates kind. Immutable native snapshot contains executable/app DLLs, deps/runtimeconfig and lazy private dependencies, with input hashes; explicitly handle dotnet app.dll versus self-contained launch and framework resolution. Every subsequent worker uses snapshot. No arbitrary current-source reads.
- Snapshot stays until actual children/original closers exit even if public Close waiter cancels; lazy dependency load in closer must work. Build finishing after closed writes nothing. Do not delete/rewrite cache directory now owned by successor component.
- Running link lost (heartbeat/protocol/voluntary data link) stops and joins child, reports worker_link_lost, forgets failure and permits holder-driven fresh worker once. Last recovery timestamp: second lost link within minute fails public worker lost its link. Crash/fatal/start failure remain failed until retry/rollout.
- Node socket cleanup/ENOTSOCK nonapplicable to mutex lock, whose crash-release needs platform evidence. Node compile-cache env does not map to .NET; do not emit it as a fake native feature.

Tests: base failed build, later worker after changed/deleted lazy DLL still original, closing during build and late closer, cancelled close owns cleanup, one recovery then second fail/window expiry, normal crash stays failed, mutex crash release/reload idle.

## New application kit: disjoint implementation contracts

No current App namespace. Recommend additive PiSharp.Chord.Hosting.App. Unlike DOM/React, these server behaviors are applicable native features.

### C-A1 bootstrap/layout/auth

Own App/{App,AppOptions,WorkerApp,Layout,Secrets,Info,Sessions,EphemeralSessions,Owner,Pairing,Users,Invite,EphemeralLogin}.cs corresponding to node/app/{app,worker,layout,secrets,info,sessions,ephemeral-sessions,owner,pairing,users,invite,ephemeral}.ts.

- StartServerAsync(options,ctx) returns App with Host/Http/Workers/Plugins/DataDir/OwnerTokenFile/OwnerLink/BasesBuilt/BaseWatched/ApplyChangesAsync/CloseAsync. Options name/path/root/listen/API/kinds/principals/workers/files/limits/timeouts/telemetry/surfaces. Owner strict JSON unequal to server canonically; worker principal required when kinds supplied.
- Canonical data-dir exclusive lock; refuse home, filesystem root or another user's directory; tighten each start Unix0700/0600, native Windows owner ACL equivalent, including SQLite sidecars. Layout server/store.sqlite,cookie-secret,files; workers/kind/encoded-key; plugins/logs/cache. Encode address as single case-distinguishing directory (uppercase/dot/slash included), hash long keys. Never modify ancestor root/home permission through links.
- Cleanup stack for partial start: HTTP→workers→host→store→plugins→lock. Load required built-in+optional user roots with core facets. Worker parent link comes before plugin startup. Start resolves after listen, base builds/watch graph independently owned; structured base_build failure affects only base consumers. Apply changes server→worker kinds→clients. Close idempotent and actual cleanup completes before lock release.
- Sessions Issue/Verify/Revoke/List: id+32-random-byte secret, SHA256 hash only, fixed-time compare, strict JSON. Browser/device persisted, unused400days expires; last-used persisted≤once/minute; signed HttpOnly Strict cookie Secure remote, renewed daily. Ephemeral memory cap1000 evicts oldest, not listed, expires minute with no initial/remaining link; reconnect cancels expiry.
- Owner fragment token single-use10minutes, form posts body, never query; token-file atomic0600/owner ACL, reuse only valid same-owner session; changed owner revokes/replaces file session. Wildcard bind displays loopback, bracket IPv6.
- Pair request/poll public, owner-only approve/confirm;10minute code, hashed poll secret, token once then404. Pending100/per-real-peer10→429; owner canonical identity exempt. Bounded memory, restart drops ephemeral pairing/owner link.
- AppInfo name/dataDir/newest API version/owner and WorkerInfo address/start payload expose process data. Optional Users(profile) list/get/update/remove; Invite owner-only7day single-use; explicit EphemeralLogin public fresh identity. Export facets, not auto-enable.
- Server alone owns telemetry store; worker recorder/sender never opens same telemetry SQLite path.

### C-A2 Files/blob/upload

Own App/{Files,FilesIndex,FilesEntry,Blobs,Uploads,FolderViews,FilesCopy}.cs. A1 calls FilesFacet.

- Immutable blob bytes, transactional metadata tables/documents. Space≤64 lowercase/digits/._-, normalized path≤1024 nonempty segments/no dot/dotdot. File/folder conflict invalid with path. Put/chunk/read≤4MiB; defaults file256MiB,total4GiB including uploads, blob-sharing copy adds no storage.
- Per-path version high-water never reused even after delete/recreate; move keeps versions/merges target mark. Metadata source/ref/key/mime/size/created/freeJSON, id addresses version. Queries file/get/byKey/byRef/versions/list, page100/max1000; folders then latest file version ordered by path. mkdir/move/removePath/removeSpace transactionally maintain counts/refcounts.
- Per-space idempotency key returns existing finished version/in-flight upload. Begin→Append exact accepted offset→Uploaded waits actual writes→Finish/Cancel. Multiple accepted appends may overlap callers but ordered write ownership; failure ends upload; one file handle. Cap64, inactivity1hour, close ends uploads, startup sweeps orphan uploads/uncommitted blobs.
- Summary keyed by space files/versions/bytes/newest20 in same commit. FilesFolder first500+more recomputed for watched folders after changed space. CopyFiles exports only Files-owned store records and hardlinks/copies blobs to new target, with owned rollback.

### C-A3 worker data archive

Own App/{Archive,Tar,HostData}.cs, corresponding node/app/{archive,tar,data}.ts; distinct from kernel host-keyed archive.

- Export live worker via server-only HostData snapshot request; idle export takes same address process lock. Consistent store point without stopping worker, owner-only temp tar+rename; file changes may fall outside store point, deleted skipped/open descriptor retained per source contract.
- Manifest first format1/app/api/address; store.archive JSONL then files/. Import requires absent address and matching app/format, rejects absolute/dot/dotdot/empty/backslash/NUL paths, links/devices/bad checksum. Bounds default4GiB/100000 entries charged from headers before writing. Stage adjacent, validate/import store, atomic rename; failure leaves no target. Remap to new address supported; application index registration remains caller's.

App tests: real startup/partial failures/permissions/locks; persisted/ephemeral expiry+renewal/malicious token IDs; owner reuse/change/one-shot; pairing caps; optional facets; concurrent append/cap/key/high-water/blob sweep/folder summary; live+idle export and malicious tar corpus/rollback. Reference test/node/app/*.test.ts, examples53–55/65.

## C-SUR/C-PUB: portable surfaces, publication and platform adapters

Portable modules new surfaces/{scripts,loaded,registered,connection,client,themes,theme-choice,active-theme,page-state,preference,messages,language,translations,locale-choice,active-language,format}.ts.

- ScriptSet base/combined/name-ordered name,digest,url,css. Remote WebScripts/TuiScripts server address, any admitted principal. Loaded-set retains object identity unchanged digest, last good on failure, serializes full reload, adopts committed cancellation. Remember actually accepted scripts and retry failed newest set on reconnect.
- Reconnect environment clock/timers/online/wake:250ms exponential≤5s;10s stable resets;20s connect timeout; wake replaces silent25s socket once ping overdue; CanRetry after close. Server base mismatch before reconnect or in set invokes outdated/stops. Reuse host links; no calls replayed.
- Themes registry/fallback dark+light roles accent/bg/fg/muted/error/warning/success, web/tui overrides, CSS variables and truecolor/nearest256. Per-principal dark/light choices and locale share persisted preferences. Follow live.
- Messages namespace catalogs values strings/functions; data namespace/key/params schema; unknown/foreign displays key. Native runtime validates shape/signatures in place of TS inference. Translation registry first plugin name wins unless override, duplicates reported; locale-chain fallback→first catalog, missing-key reported once. Preferred locale matching, live language, render-local number/time formatting, html lang/dir adapter. Per-host arbitrary page state survives reload, dies close. TUI optimistic read overlays shown value until all shown calls settle.
- Publication builds once from entry/actual hashed inputs; own routes503 on failed base, server remains. Content-hashed immutable private-cache base/plugins/CSS/assets/combined; keep last4 sets, failed plugin keeps good source/CSS/assets. Narrow HTTP ScriptPaths. ISurfaceBuilder artifact abstraction separates code language; no claim DLL executes as JS.
- Web DOM page/styles/environment/language/themes and React HostProvider/useHold/useWatch/useRegistry/useRefState/usePageState/useMessages require actual JS/React adapter or exact unsupported disposition. CSS prepare before reload; settle only adopted plugin styles, failed preload retry. Native structs do not supply hooks/browser DOM.
- Native terminal drawing may reuse PiSharp.Tui, but downloaded untrusted plugin DLL in ALC is NOT secure sandbox. Optional Node26.5.1 adapter must retain empty-env/private-realpath-bootstrap/pipes/no-TTY, permission model denying filesystem/network/process/worker/addon/SQLite,96MiB heap and VM builtin allowlist. The symlinked temporary ancestor fix resolves bootstrap path without broadening access. Without adapter, explicitly unsupported secure terminal execution.
- Launcher trusted side holds token/file/pair credentials;17MiB pipe frames,8MiB downloads,≤4 links/≤8 requests; only same-origin terminal files. Escape allowlist drops OSC/DCS/unsupported modes, output frame≤64KiB. One confirmed copy/open/attach at a time;300ms key quarantine, held output≤1MiB; copy rejects control/bidi, URL http(s)/no userinfo, attach confirms actual regular-file identity≤16MiB/no FIFO/UNC/device escapes; always reset terminal modes;3 Ctrl-C/1s exits130.

Tests: deterministic reconnect; loaded-failure recovery; theme/locale/user isolation; messages duplicate/fallback/render scope; state and optimistic settlement; artifact hashes/cache/last4/CSP/base failure. Actual optional adapter requires browser/React/Node sandbox tests rather than native substitutes. Reference test/surfaces, test/node/{surfaces,web,react,tui}, examples56–63.

## Verification boundaries

This report is source evidence only, no new runtime pass. Prior green receipts cannot prove new semantics. Root must run updated core/host/docs/wire suites and actual child/ALC/HTTP tests plus new app/files/tar cases. Build success cannot prove lazy dependency snapshot, directory ACL, one-shot pairing, archive limits or terminal isolation. Every new exported upstream subpath needs explicit native/adapter/nonapplicable disposition, including old export removal preserving C# Legacy.

## Exhaustive normalized source inventory

Below every final source file is listed by family, with `=` byte-identical to normalized old location, `~` changed (may include import-only relocation; narrative above separates semantics), `+` new. Store/sync and telemetry assigned to companion reports. Old root implementation/test deletion is represented by retained Legacy disposition above, not mass deletion. README/docs/package exports need root metadata update; imports alone are not runtime change.

Counts: = 58, ~ 150, + 110 final files.

### context

`= context/index.ts`.

### contracts

`= contracts/README.md`, `~ contracts/accepts.ts`, `~ contracts/contract.ts`, `~ contracts/declare.ts`, `~ contracts/evolve.ts`, `~ contracts/fingerprint.ts`, `= contracts/history.ts`, `~ contracts/host-kind.ts`, `= contracts/index.ts`, `~ contracts/internal.ts`, `= contracts/names.ts`, `~ contracts/params.ts`, `~ contracts/schema.ts`, `~ contracts/snapshot.ts`.

### data

`= data/README.md`, `~ data/args.ts`, `~ data/errors.ts`, `~ data/failure.ts`, `~ data/index.ts`, `~ data/internal.ts`, `~ data/params.ts`, `~ data/principal.ts`, `~ data/validator.ts`.

### delta

`= delta/README.md`, `= delta/apply-immutable-trusted.ts`, `= delta/diff.ts`, `= delta/draft.ts`, `= delta/index.ts`, `= delta/pending.ts`, `= delta/revision-validator.ts`, `= delta/tracker.ts`.

### documents

`~ documents/README.md`, `~ documents/address-source.ts`, `~ documents/definition.ts`, `~ documents/documents.ts`, `= documents/fence.ts`, `~ documents/handles.ts`, `~ documents/index.ts`, `~ documents/internal.ts`, `~ documents/keyed-slot.ts`, `= documents/line.ts`, `~ documents/persistence.ts`, `~ documents/residency.ts`, `~ documents/slot.ts`, `~ documents/transaction.ts`.

### host

`~ host/README.md`, `~ host/activity.ts`, `~ host/adapted.ts`, `~ host/attempt.ts`, `= host/backoff.ts`, `~ host/ctx.ts`, `= host/deadline.ts`, `= host/dependencies.ts`, `~ host/destination.ts`, `~ host/facet.ts`, `~ host/follow.ts`, `+ host/host-data.ts`, `~ host/host.ts`, `~ host/index.ts`, `~ host/internal.ts`, `~ host/members.ts`, `~ host/native.ts`, `~ host/object.ts`, `+ host/old-work.ts`, `~ host/parked.ts`, `~ host/presence.ts`, `+ host/readiness.ts`, `~ host/ref-state.ts`, `~ host/reference-keys.ts`, `~ host/reference-source.ts`, `~ host/reference.ts`, `+ host/reload-cycle.ts`, `= host/retry-on.ts`, `~ host/revocation.ts`, `~ host/root.ts`, `~ host/route.ts`, `~ host/routines.ts`, `~ host/sleep.ts`, `~ host/staging.ts`, `+ host/unbound-clock.ts`, `~ host/unbound.ts`, `= host/uuid.ts`.

### index.ts

`~ index.ts`.

### jobs

`= jobs/README.md`, `= jobs/definition.ts`, `= jobs/index.ts`, `= jobs/internal.ts`, `~ jobs/job.ts`, `~ jobs/persisted.ts`, `~ jobs/records.ts`, `~ jobs/run-states.ts`, `~ jobs/run.ts`, `~ jobs/running.ts`, `~ jobs/stored.ts`.

### json.ts

`= json.ts`.

### link

`~ link/README.md`, `~ link/binary.ts`, `~ link/channel.ts`, `~ link/errors.ts`, `= link/index.ts`, `= link/internal.ts`, `= link/ipc.ts`, `= link/link.ts`, `~ link/message.ts`, `~ link/part.ts`, `~ link/text.ts`, `~ link/values.ts`, `= link/websocket.ts`.

### node/app

`+ node/app/README.md`, `+ node/app/app.ts`, `+ node/app/archive.ts`, `+ node/app/blobs.ts`, `+ node/app/data.ts`, `+ node/app/ephemeral-sessions.ts`, `+ node/app/ephemeral.ts`, `+ node/app/files-copy.ts`, `+ node/app/files-entry.ts`, `+ node/app/files-index.ts`, `+ node/app/files.ts`, `+ node/app/folder-views.ts`, `+ node/app/index.ts`, `+ node/app/info.ts`, `+ node/app/invite.ts`, `+ node/app/layout.ts`, `+ node/app/owner.ts`, `+ node/app/pairing.ts`, `+ node/app/secrets.ts`, `+ node/app/sessions.ts`, `+ node/app/tar.ts`, `+ node/app/uploads.ts`, `+ node/app/users.ts`, `+ node/app/worker.ts`.

### node/compression.ts

`~ node/compression.ts`.

### node/http

`~ node/http/README.md`, `= node/http/admission.ts`, `~ node/http/authenticate.ts`, `~ node/http/cookies.ts`, `= node/http/file.ts`, `~ node/http/headers.ts`, `~ node/http/http.ts`, `= node/http/incoming.ts`, `~ node/http/index.ts`, `= node/http/node.ts`, `~ node/http/request.ts`, `~ node/http/response.ts`, `~ node/http/router.ts`, `~ node/http/upgrades.ts`.

### node/plugins

`~ node/plugins/README.md`, `+ node/plugins/base-inputs.ts`, `~ node/plugins/build.ts`, `+ node/plugins/css-modules.ts`, `~ node/plugins/discover.ts`, `~ node/plugins/evaluate.ts`, `~ node/plugins/index.ts`, `~ node/plugins/install.ts`, `~ node/plugins/plugins.ts`, `~ node/plugins/runtime.ts`, `~ node/plugins/stack.ts`, `+ node/plugins/testing.ts`, `~ node/plugins/watcher.ts`.

### node/surfaces

`+ node/surfaces/README.md`, `+ node/surfaces/bundle.ts`, `+ node/surfaces/current.ts`, `+ node/surfaces/index.ts`, `+ node/surfaces/published.ts`, `+ node/surfaces/served.ts`, `+ node/surfaces/surface-build.ts`, `+ node/surfaces/tui.ts`, `+ node/surfaces/web.ts`.

### node/tui

`+ node/tui/README.md`, `+ node/tui/actions.ts`, `+ node/tui/credentials.ts`, `+ node/tui/filter.ts`, `+ node/tui/index.ts`, `+ node/tui/launcher.ts`, `+ node/tui/pipe.ts`, `+ node/tui/sandbox.ts`.

### node/workers

`~ node/workers/README.md`, `+ node/workers/base.ts`, `+ node/workers/child.ts`, `~ node/workers/control.ts`, `~ node/workers/index.ts`, `~ node/workers/lock.ts`, `~ node/workers/main.ts`, `~ node/workers/process.ts`, `~ node/workers/workers.ts`.

### output.ts

`= output.ts`.

### react

`+ react/README.md`, `+ react/hooks.ts`, `+ react/index.ts`, `+ react/stores.ts`.

### registries

`= registries/README.md`, `= registries/contribution.ts`, `= registries/entries.ts`, `= registries/index.ts`, `= registries/internal.ts`, `~ registries/record.ts`, `~ registries/registry.ts`, `~ registries/value.ts`.

### remote

`~ remote/README.md`, `= remote/calls.ts`, `~ remote/consumer.ts`, `~ remote/hold.ts`, `~ remote/host-link.ts`, `= remote/internal.ts`, `= remote/path.ts`, `~ remote/provider.ts`, `~ remote/relay.ts`, `~ remote/remote-object.ts`, `~ remote/replica.ts`.

### sqlite

`= sqlite/cloudflare.ts`, `= sqlite/database.ts`, `= sqlite/index.ts`, `= sqlite/migrations.ts`, `~ sqlite/node.ts`, `= sqlite/schema.ts`.

### state

`= state/README.md`, `~ state/batch.ts`, `~ state/derive.ts`, `~ state/index.ts`, `= state/internal.ts`, `~ state/native-source.ts`, `~ state/watchable.ts`, `~ state/writable.ts`.

### store

`~ store/README.md`, `~ store/adapter.ts`, `= store/allocator.ts`, `~ store/applier.ts`, `~ store/apply-batch.ts`, `~ store/archive.ts`, `~ store/backend.ts`, `~ store/catalog.ts`, `= store/claims.ts`, `~ store/columns.ts`, `~ store/engine.ts`, `~ store/evaluate.ts`, `~ store/export.ts`, `~ store/expression.ts`, `+ store/host-column.ts`, `+ store/host-data.ts`, `+ store/import-migration.ts`, `~ store/import.ts`, `~ store/index.ts`, `~ store/json-column.ts`, `= store/jsonl.ts`, `+ store/live-query.ts`, `~ store/outbox.ts`, `~ store/query.ts`, `= store/refusal.ts`, `~ store/rows.ts`, `~ store/schema.ts`, `~ store/sql.ts`, `~ store/step.ts`, `~ store/store.ts`, `~ store/table-changes.ts`, `~ store/table-evolve.ts`, `~ store/table-handle.ts`, `~ store/table-history.ts`, `~ store/table-migration.ts`, `~ store/table-writes.ts`, `~ store/table.ts`, `= store/text.ts`, `= store/writes.ts`.

### surfaces

`+ surfaces/README.md`, `+ surfaces/active-language.ts`, `+ surfaces/active-theme.ts`, `+ surfaces/client.ts`, `+ surfaces/connection.ts`, `+ surfaces/format.ts`, `+ surfaces/index.ts`, `+ surfaces/language.ts`, `+ surfaces/loaded.ts`, `+ surfaces/locale-choice.ts`, `+ surfaces/messages.ts`, `+ surfaces/page-state.ts`, `+ surfaces/preference.ts`, `+ surfaces/registered.ts`, `+ surfaces/scripts.ts`, `+ surfaces/theme-choice.ts`, `+ surfaces/themes.ts`, `+ surfaces/translations.ts`.

### sync

`+ sync/batch.ts`, `+ sync/client.ts`, `+ sync/files.ts`, `+ sync/filesystem.ts`, `+ sync/index.ts`, `+ sync/lines.ts`, `+ sync/remote.ts`, `+ sync/state.ts`.

### telemetry-ingest

`+ telemetry-ingest/batch.ts`, `+ telemetry-ingest/facet.ts`, `+ telemetry-ingest/index.ts`, `+ telemetry-ingest/recorder.ts`, `+ telemetry-ingest/sender.ts`.

### telemetry-sqlite

`~ telemetry-sqlite/index.ts`, `+ telemetry-sqlite/node.ts`, `+ telemetry-sqlite/schema.ts`, `~ telemetry-sqlite/store.ts`.

### telemetry.ts

`+ telemetry.ts`.

### tui

`+ tui/README.md`, `+ tui/bridge.ts`, `+ tui/channel.ts`, `+ tui/frames.ts`, `+ tui/index.ts`, `+ tui/runtime.ts`, `+ tui/scope.ts`, `+ tui/terminal.ts`, `+ tui/theme.ts`.

### watch.ts

`= watch.ts`.

### web

`+ web/README.md`, `+ web/environment.ts`, `+ web/index.ts`, `+ web/language.ts`, `+ web/page.ts`, `+ web/scripts.ts`, `+ web/styles.ts`, `+ web/themes.ts`.

### Former next-only files without final counterpart

None.
