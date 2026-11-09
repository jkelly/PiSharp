# Independent Chord hosting integration review

Read-only review on 2026-11-08 in `E:/AI/PiSharp/gap-implementation`. Scope: `PiSharp.Chord.Hosting` plugin generation/HTTP/worker ownership and rollback, using N1/D14 and frozen v1.5.0 `next-node/plugins`, `http`, and `workers`. No builds, tests, source edits, or process launches performed by this reviewer. The report is the only authored artifact for this assignment; I13 owns runtime validation.

N1 had already identified the old HTTP/wrapper/auth/socket callback versus collectible ALC retirement gap and was implementing generation leases before this review. That author-known issue is tracked separately below to avoid claiming duplicate discovery.

## Actionable findings sent to N1

### HOSTING-1 — Worker descendants outlive a natural leader exit [P1]

**Original location:** `Workers/ProcessWorkers.cs`, `StartAsync`, `WatchExit`, and `KillAsync`.

**Trigger:** A worker spawns a long-lived child and then returns or crashes. Plain `Process.Start` established no retained Unix group/private Windows job; `WatchExit` only closed pipes, and the helper skipped tree termination once `HasExited` was true. The descendant survived. Frozen `workers/process.ts` always kills its detached group in the leader's exit handler.

**Fix:** N1 accepted the defect and introduced the shared `Tools/Processes/OwnedChildProcess.cs` façade over the existing anchored Unix admission and suspended Windows private-job admission. Its exposed exit waits for descendant containment to stop. Worker and builder use it. Initial source correction inspected; final tests/complete owner handoff pending at report creation.

### HOSTING-2 — Shutdown during initialization skips the connected host [P1]

**Location:** `Workers/WorkerMain.cs`, `host = await initialize(worker).WaitAsync(lifetime.Token)` and the `finally` host-close conditional.

**Trigger:** The initializer creates a host, calls `WorkerContext.Connect`/`LoadPluginsAsync`, and awaits a held dependency or tail before returning the host. A shutdown cancels the wrapper wait; the local `host` variable remains null, so shutdown skips `host.CloseAsync` even though WorkerContext has connected/loaded it. Runtime disposal can then retire plugin code while the actual host/initializer still owns work. Frozen worker startup records the loaded host and independent close ownership before the initializer finishes.

**Fix requested:** Retain the actual connected host in WorkerContext as soon as it becomes owned, retain/observe the initializer original, and close the known host before retiring its runtime, with deliberate bounded process shutdown rather than silently treating the canceled wrapper as completed initialization.

### HOSTING-3 — Unix path containment accepts a case-different sibling [P2]

**Location:** `Plugins/PluginManifest.cs`, `PluginScanner.Inside` (original line 69).

**Trigger:** On a case-sensitive filesystem, a plugin directory `/tmp/Plugin` resolves `../plugin/Outside.dll`. The full path is outside the plugin, but unconditional `OrdinalIgnoreCase` prefix comparison accepts `/tmp/plugin/Outside.dll`. The component walk only rechecks symlinks, so lexical traversal remains accepted. This also affects `PluginContext.File` and file-response root checks using this helper.

**Fix requested:** Use filesystem-appropriate path comparison (`Ordinal` on Unix; case-insensitive Windows handling) for containment, with a case-different sibling regression. This is a declared containment invariant, not a claim that trusted plugin code is sandboxed.

### HOSTING-4 — A newly added failed plugin loses its dirty/set mark [P2]

**Location:** `Plugins/PluginManager.cs`, `Mark`, `BuildAsync` snapshot, and `RolloutAsync`'s `dirty.Count == 0` clearing.

**Trigger:** Start with an empty/prior discovered set, add a new plugin B, and let its first build fail. The watcher marks `setChanged` but dirties only the previous `plugins` collection. The build snapshot has no B. Rollout correctly reports B in `FailedPlugins`, but there is no observed B mark to retain; `dirty` becomes/remains empty and `setChanged` is cleared. State falsely says no pending change despite the failed added plugin.

**Fix requested:** Include newly discovered plugins in the marked build snapshot or otherwise retain the set-change mark when an added plugin fails, preserving changes arriving after the snapshot. Test initial empty set → add failing B → rollout → B/set still dirty.

### HOSTING-5 — Build timeout excluded redirected-output drain [P2]

**Original location:** `Plugins/PluginBuilder.cs`, the uncancelled stdout/stderr awaits after `WaitForExitAsync`.

**Trigger:** A build target/tool leaves a silent descendant holding stdout/stderr and the build leader exits. The timeout covered only leader exit; awaiting the redirected readers afterward could hang forever. The cancellation branch also rethrew without joining those reader originals.

**Fix:** The initial correction uses `OwnedChildProcess`, registers both reader originals, and includes their drain in the build deadline. Source inspected. Final descendant and timeout execution remains pending.

### HOSTING-6 — Failed WebApplication acquisition leaks its partial owner [P2]

**Location:** `Http/ChordHttp.cs`, `OpenAsync` (original lines 60–78).

**Trigger:** `builder.Build()` succeeds, then `application.StartAsync` or server-address retrieval throws. The application has not returned to `self.Open`, so host native-resource ownership never acquired it, while the catch only faults `listening` and rethrows. Services/partial listener state are not disposed by this acquisition path.

**Fix requested:** Retain and dispose the application on failed acquisition, and cover a bind/start failure plus successful retry with no partial listener/services remaining.

### HOSTING-7 — Cross-site frame navigation can authenticate as a page [P2]

**Location:** `Http/ChordHttp.cs`, `pageNavigation`, `IsForeign`, and the authentication predicate (original lines 108–122, 220–227).

**Trigger:** A Page route receives `GET`, `Sec-Fetch-Mode: navigate`, and a cross-site iframe/frame destination. Native `pageNavigation` omitted the destination check, so the request could invoke authenticators and acquire a principal. Frozen admission only recognizes top-level `document`/`empty` navigation; frames remain foreign/anonymous. Native `IsForeign` also omitted frame/embed/object destinations when Fetch-Site is absent/not same-origin.

Native handling additionally rejected foreign safe non-page requests outright, whereas the frozen handler runs safe text/JSON/HTML requests anonymously after origin validation (foreign byte downloads still fail).

**Fix requested:** Port the exact frozen predicates. For non-CrossSite routes, present Origin must equal admitted origin; absent Origin is allowed for GET/HEAD/OPTIONS or non-browser requests. Top-level page navigation may authenticate; framed navigation must not. CrossSite routes remain anonymous. Cover iframe/no-Origin, top-level page, foreign Origin refusal, anonymous safe non-page, and byte-download refusal.

### HOSTING-8 — Verified relay framer is discarded [P2]

**Location:** `Http/ChordHttp.cs`, `AdmitAsync`'s relay return and `ApplyDefaults` (original lines 207–208, 248–255).

**Trigger:** A verified relay returns `RelayAdmission(Origin, Framer)`, with no global Framers contribution. The framer is validated and then discarded; response headers still contain `X-Frame-Options: DENY`/`frame-ancestors 'none'`. The exposed request-specific admission field has no effect.

**Fix requested:** Carry the validated framer on this request's admission/context, union it with router framers for this response only, and omit X-Frame-Options DENY whenever either source exists, matching frozen `http/headers.ts:89,118–126`. Avoid retaining arbitrary per-relay origins across requests.

### HOSTING-9 — Idle timeout can stop a worker during reload [P2]

**Location:** `Workers/WorkerMain.cs`, activity-only idle publication and reload loop; `Workers/ProcessWorkers.cs`, `CheckIdleAsync`.

**Trigger:** An otherwise idle worker approaches its idle deadline, then a reload/retired-generation drain takes long enough to cross that deadline. Native worker code never reports reload as busy, and the parent's idle check ignores outstanding reload work, so it sends shutdown mid-rollout. Frozen worker main sets reloading before applying the queue, emits idle false, and flips back after it drains.

**Fix requested:** Make applying/queued reloads busy for idle accounting, with parent-side ordering that prevents a queued idle stop from overtaking admitted reload work. Test a held reload crossing the idle deadline and then normal idle expiry after settlement.

## Additional findings from the final integration pass

### HOSTING-10 — A cancelled wrapper loses a captured inner route's generation [P1]

**Original location:** `Http/ChordHttp.cs`, the request-scoped router lease and composed `next().WaitAsync`.

**Trigger:** Wrapper A awaits a held task before invoking its inner route B, with A and B supplied by different plugin generations. Request cancellation ends `HandleAsync` and releases the snapshot lease. A's own invocation lease survives, but B has not started and has no invocation lease. Reload can unload B before A resumes and calls it.

**Source correction inspected:** `ChordHttp.cs:184` invokes the composed chain through `PluginCallbacks.InvokeWithDependenciesAsync(routerCallbacks, next)`. The complete original task retains the captured generations independently of the cancelled waiter. Relay, authentication, socket and byte-reader originals also retain their required snapshot dependencies.

### HOSTING-11 — Bounded host drain does not imply original plugin tasks ended [P1]

**Original locations:** `PiSharp.Chord/Host/Attempt.cs`, `DrainCallsAsync`/`DrainAsync` and retirement; `Host/Native.cs`, abandoned opens and timed-out closes; `PluginRuntime.DrainRetiredAsync`.

**Trigger:** An old plugin method ignores cancellation past the host close timeout. The host logs the drain timeout and finishes retirement; `DrainReloadsAsync` therefore returns even though that original still runs. HTTP-only leases do not protect the method, so runtime retirement can request ALC unload before its later private dependency load. Native open/close, follow attach, routine and job originals have the same timeout ownership boundary.

**Source correction inspected:** H1 added `Host/CallbackLifetime.cs:15`, an optional callback owner registration with leases tied to original completion. Native open/close and follow attach/detach retain their generations continuously through resource cleanup, including late results and close timeouts. Methods/replacements/result mapping, routines, jobs, composition and adaptation use callback leases. N1 registers `PluginCallbacks.Acquire` in `PluginRuntime.cs:43`; callback-drain timeout reports failure without forcing a live context to unload. No additional concrete defect was found in the narrowed seam review.

### HOSTING-12 — Error responses discard resolved global header contributions [P2]

**Original location:** `Http/ChordHttp.cs`, catch branches calling `ApplyDefaults(native, request, null)`.

**Trigger:** A router contributes Framers/CSP, then an unknown route, refusal, or failing handler produces an error. The catch resets defaults without that router, dropping contributed CSP and replacing allowed framing with DENY. Frozen `http.ts` passes its resolved router to refusals, including admission/match errors.

**Source correction inspected:** `ChordHttp.cs:100,194–196` retains `responseRouter` and uses it for error defaults. Verified relay framers remain request-specific.

### FACADE-1 — Closing a blocking Unix pipe does not retire its original read [P1]

**Original locations:** `Tools/Processes/OwnedChildProcess.cs`, the five-second reader-drain fallback; `Unix/PosixSpawnProcessAdmission.cs`, blocking FileStream output.

**Trigger:** An escaped Unix descendant retains stdout while the original leader exits. Group cleanup correctly leaves that escaped process outside its receipt, but closing the parent's stream does not interrupt an already blocked Unix pipe read. Disposal then waits forever for its registered `ReadToEndAsync` original. The same issue applies to a blocked interactive stdin write.

**Source correction inspected:** D16 added facade-only `Unix/PosixOwnedPipeStream.cs:9`, using checked nonblocking descriptors, a descriptor-use/close gate, and cancellation-aware EAGAIN retries. Existing ordinary admission remains unchanged. Facade stop also closes interactive stdin. No remaining concrete facade defect was found. Darwin arm64's padded variadic `fcntl` call remains a platform execution requirement, not a source-only runtime acceptance claim.

### HOSTING-13 — A shared cached facet is not a unique generation owner [P2]

**Original location:** `Plugins/PluginCallbacks.cs:14`, `ConditionalWeakTable<Facet, PluginGeneration>.Add`, added by the declaring-facet fallback extension.

**Trigger:** A plugin returns a cached/static Facet from an explicitly shared library. Initial load works, but a changed-digest generation returns the same Facet object and duplicate-key registration rejects its reload. Independently, the shared static keeps that table key live, and the table value retains the retired generation's Context, preventing collection even after plugin removal. The public CreateFacet contract does not require a fresh declaration object.

**Source correction inspected:** `PluginRuntime.Evaluate` preserves the exact declared setup delegate in `Facet.Define(declared.Setup)`, giving each evaluated generation a distinct weak ownership identity. Unchanged digests still reuse their prior wrapper. The regression keeps the original default-ALC cached facet rooted while loading a changed generation and requiring the old ALC to collect. The accompanying Hosting friend-assembly declaration is a compile dependency, not an additional lifetime design change.

## Final source review status

All findings above have source corrections inspected. HOSTING-1 uses the shared Windows-job/anchored-Unix-group owner and joins registered reader originals; child handle disposal has one cached task. HOSTING-2 retains the connected host and initializer original, with process-bound cleanup that avoids unloading underneath a noncooperative initializer. HOSTING-3 uses OS-appropriate path comparison. HOSTING-4 marks newly discovered names before snapshotting. HOSTING-5 covers native admission, tree exit and redirected drain in one deadline, handles cleanup aggregates per plugin, and preserves caller cancellation. HOSTING-6 disposes failed WebApplication acquisition. HOSTING-7/8 match the inspected origin/navigation/framer predicates. HOSTING-9 combines parent rollout ownership with a child reload-busy predicate on activity publication, avoiding an idle clock starting during the reload.

The author's original HTTP callback-generation gap is also source-corrected: selected callback generations and actual task originals remain leased across cancelled waits, and retired contexts are unloaded only when those leases end. The final declaring-facet extension was rechecked through immutable ServedType/RegistryServing declarations, facet/object RegistryEntry callback-owner provenance, composed HTTP/authenticator snapshots, and actual host/native/follow/routine/job task paths. Shared-library callbacks retain their declaring generation as well as any actual callback-module owner; captured HTTP dependencies survive cancellation until the composed original settles. The cached-facet revision above was inspected after that pass.

This static review found no remaining actionable design defect in the bounded final scope. Central I13 runtime validation, including actual collectible-ALC late loads and cached facets, retained descendants, blocked pipe ends and platform-specific admission, remains required. No builds or tests were run by this reviewer.

## Boundaries

This list covers concrete existing contracts in the assigned integration scope. It does not request JavaScript/browser bundling, legacy protocol4 behavior, new features, or speculative architecture changes. Findings were sent directly to `/root/impl_chord_hosting`, `/root/design_chord` or `/root/design_remaining` as they were confirmed. Current line positions may move. No passing runtime claim is made from authored tests or source inspection.
