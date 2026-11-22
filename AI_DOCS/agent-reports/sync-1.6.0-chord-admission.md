# chord-admission: Chord 1.5.2 -> 1.6.0, admission, route admission and the `denied` paths

Work package `chord-admission` of the Pi 1.6.0 sync, 2026-11-21. Branch `sync/1.6.0-chord-admission`, from `eb79d423` (after `chord-queries`). Upstream v1.6.0 is `E:/AI/PiSharp/upstream-v160` (`faac585884ae2c013a6b1400f3e9e77f10b34bac`); the feature is upstream commit `8270d9af6` (slice 14, second part). The plan was `AI_DOCS/agent-reports/sync-1.6.0-chord-discovery.md`; the layers below are the foundation, remote and queries reports beside this file.

Status: DONE. Everything is committed on the branch; nothing was pushed, no pull request, tag or publication was made, no live provider was called and no credential was used. Where a report and the upstream source disagree, upstream wins (section 6).

## 1. Commits and per-item status

| Commit | What |
| --- | --- |
| `fb062928` | Admission with a typed value, route admission and the `denied` paths: all product source (`Host/{Admission,RouteAdmission}.cs` new; `Host/{Attempt,Object,ReloadObject,Reference,Staging,Route,Host,Contracts,Internal,KeyedReference,RemoteIntegration}.cs`; `Remote/{Path,Hold,Provider,Consumer,Relay,RemoteObject}.cs`), and the test double `H6Upstream` given the new `IUpstream` members |
| `f316798c` | `host/admission.test.ts` (43), the denied cases of `dependencies.test.ts` (2) and `reference-source.test.ts` (1): `tests/PiSharp.Chord.Tests/Host/Sync160AdmissionCases.cs`, fixtures in `Sync160AdmissionSupport.cs` |
| `5b4b0e94` | `host/route-admission.test.ts` (21 cases, 28 with the parameterised "N turns into the settle"): `Remote/Sync160RouteAdmissionCases.cs`; `RouteAdmission.DecisionsNow` (internal) for its weak-reference case |
| `71ef64ac` | The `relay.test.ts` denied-window case and the four `path.test.ts` "a gated path" cases: `Remote/Sync160AdmissionCases.cs` |
| `99d31c4d` | The chord-edge desk fixtures and their variant and scope cases moved to upstream's admission form, denied state included (section 4) |
| `053c78a9` | `host.admission.type-reload.does-not-rehearse-...`: its "not rehearsed" check counts the factory's runs once the reload settled (it failed once in six full runs, section 5) |
| (last) | This report |

The first four commits end with the plan's `Co-Authored-By: Claude Fable 5.2` line; the later ones with `Claude Opus 5.5`, the model that resumed the package.

| Item (upstream `8270d9af6`) | Status |
| --- | --- |
| `host/admission.ts`: `allow`/`deny`, `Decision`, the admit self, `refuseAdmittedJob`, `revoked` | ported: `Host/Admission.cs` |
| `host.serve(Type, (params, principal, admitted, self) => ..., { admit })`, the contract check of admit against `admission` | ported: `IFacetHost.Serve(Contract, AdmittedHostFactory, ServeOptions)`, `ServeOptions.Admit`, `Staging.cs` |
| The admission phase before the factory, the park on the decision, the follow (`readmit`, `#rebuild`, `#cut`, `#sealDraining`, `#followFailed`), revoked calls | ported: `Attempt.cs`, `Object.cs`, `ReloadObject.cs`, the partials in `Admission.cs`; the order is section 2 |
| `self.job` refused in an admitted factory | ported |
| Reloads: a rehearsal with the current value under an equal admission fingerprint, the admit recorded without effects, a denial between staging and activation | ported: `Staging.cs`, `ReloadObject.cs` |
| `Followed.denied`, `ReferenceSource.deny` (no value after a denial) | ported: `Internal.cs`, `Reference.cs` (`ReferenceMember.Deny`), and the pinned key (`KeyedReference.cs`, outside the owned list) |
| `dependencies.ts` denied branch (a denied dependency parks its dependent showing the denial, no `parked_long`) | ported: `Attempt.cs` |
| `Binding.queryMember` denied branch | already-ported: the native `Binding.QueryMember` and `ChordObject.QueryMember` throw the state's cause, which is the denial; no edit |
| `host/route-admission.ts` and `route.ts` `admit`, the gate, the cycle refusal | ported: `Host/RouteAdmission.cs`, `Route.cs` (`RouteOptions.Admit`), `RemoteIntegration.cs` (the holding context, outside the owned list) |
| `remote/path.ts` gate, `hold.ts` withheld/admitted, the denied paths of `provider.ts`, `relay.ts`, `consumer.ts`, `remote-object.ts`; `host-link.ts` fence | ported: `Remote/{Path,Hold,Provider,Relay,Consumer,RemoteObject}.cs`; `HostLink.Fence` was already there (chord-remote), no edit |
| `hold.test.ts` binding-fence cases, `link/channel.test.ts` fence cases, `link/message.test.ts` denied byte | already-ported by chord-remote (`Remote/Sync160WindowCases.cs`, `Link/Sync160LinkCases.cs`) |
| `contracts/declare.test.ts` admission cases, `evolve`'s `admission(schema)` | already-ported by chord-foundation (`Contracts/Sync160ContractsCases.cs`) |
| `host/read-declarations.ts` and its test | already-ported by chord-queries (`Host/Sync160ReadDeclarationsCases.cs`) |
| Host-data delete by plugin (`host/host-data.ts`, `store/host-data.ts`, its test) | not this package's (Store); already-ported (`Host/HostDataSync160Cases.cs`) |
| `node/workers/workers.ts` `admit` and its test | deferred to chord-workers (section 7) |
| `telemetry.ts` admission span | ported with the attempt (`chord.admit`) |
| `test/examples/69-admission.ts`, `docs/**`, `CHANGELOG.md` | no-op (example and upstream documentation, as in the plan's table) |
| The chord-edge desk fixtures and their cases | converted (section 4) |

## 2. The activation-order design

Written first, so the reasoning can be reviewed apart from the code.

### 2.1 What upstream does

`Attempt.activate(ending)` of an admitted type runs `#runAdmission` before it awaits `ending` (the previous instance's teardown): one microtask after its start, `admit` declares against a `Decision`, whose references are bound at once, and the attempt parks on the decision with the same `#park` the dependency check uses. With the first allow it awaits `ending`, then runs the factory with the admitted value. From the allow on it follows the decision: a change is read in the flush of the batch that made it and, when it is another verdict, handed to `ChordObject.readmit`, which seals the attempt at once (no drain) and starts a new one (`#rebuild`), seals a restart's draining old attempt (`#sealDraining`), or makes a failed object show a denial (`#followFailed`).

### 2.2 The native order

There are no microtasks. A host has one dispatcher, a FIFO of actions that await continuations rejoin; a commit (`Batch.Commit`) adopts, delivers and flushes synchronously inside the action that made it. "Turn" below is one dispatcher action.

```
ChordObject.BeginAttempt / Rebuild            (synchronous; possibly inside a flush)
  Current = next, state shown (loading, or failed { denied } for a denial), holders told
  Dispatcher.RunAsync(activation)             -> posted: never inline
----------------------------------------------- a later turn
Attempt.ActivateAsync(ending)
  admitted type only:
    RunAdmissionAsync                         admit runs, its references are acquired, Owner.Admitting(this)
      ParkAsync(DecideAsync)                  parks on the decision's requests, on a denial (failed { denied }), on a failed source
    chord.admit { allow }
    await ending()                            the previous attempt's close and an ending predecessor of the identity
  factory(params, principal, admitted, self)  -> the rest is unchanged: dependencies park, own load, readers, natives, live
```

1. **Where the decision phase sits relative to the previous attempt's close.** Before it. A type without an admission keeps the 1.5.2 order exactly (`await previousClose`, then `ActivateAsync`, which starts its span and runs the factory): nothing changes for the 1,429 existing cases. An admitted type's attempt is handed the wait instead of running behind it: `ActivateAsync(ending)` runs the admission first and awaits `ending()` only after the first allow. So a denial is shown while the sealed attempt's natives still close, and an allow that follows waits for that close before the factory runs (upstream "only the factory phase waits").
2. **Closes chain.** At 1.5.2 an attempt could fail only after it had awaited its predecessor's close, so one `previousClose` task was enough. An admitted attempt can be sealed while its predecessor still closes (A sealed, B parked denied, B sealed by a reload). `previousClose` therefore accumulates: each sealed attempt's close is awaited together with what was pending before it, so a factory never runs beside a native of any earlier attempt.
3. **"One microtask after a seal".** Upstream needs it because `admit` binds references and a reference is not acquired or released inside a batch; the seal happens in a flush. Natively the activation is always posted (`HostDispatcher.RunAsync` never runs inline), so `admit` runs in a later turn than the flush that sealed its predecessor, outside every batch (`Batch.AssertOutside("get")` holds). What the holders must see at once is not the admission but the object's state, and that is set synchronously in the seal's flush (`Rebuild`): `loading`, or `failed { denied }` with no `loading` before it. The difference from a microtask: other actions already queued on the dispatcher (an inbound message, a continuation) run before the admission starts. They find the object `loading` or denied, which is what a call that crossed the seal must find (it waits at the gate, or is refused); nothing they can observe depends on whether `admit` has run yet.
4. **The teardown is posted too.** `Rebuild` runs inside a flush; closing the sealed attempt there would release its `self.Get` references inside the batch (native `CloseAsync` runs synchronously up to its first incomplete await). The close is posted to the dispatcher behind the new attempt's activation and joins `previousClose` before either runs.
5. **The follow.** `Decision` is a dependent of the watchable `admit` returned and follows its references (`Binding.Changed`) and registries. A change of the watchable is read once per batch, in that batch's flush (`Batch.Flushing`; a change outside any commit asks for one commit soon, as a reader's argument check does). A reference's state change is read where it happens. Both end in `Attempt.AdmissionMoved`: before the allow it wakes the parked check; after it, a verdict that is not the same object as the one followed goes to `ChordObject.Readmit`.
6. **Reloads.** Natively a restart activates the successor only after the old attempt drained and closed (`RestartAsync`), and a retry after the old one closed (`RetryReloadAsync`). For an admitted type the successor is activated at once with that drain-and-close as its `ending`: its admission runs (and parks, and shows what it parks on) while the old attempt drains; its factory waits. A decision change of the draining old attempt seals it at once (`SealDraining`) and the restart goes on without waiting for its calls. The reload's requests-readiness (`ReloadReadiness`) no longer sets the object's state while the successor's admission has not allowed: the admission's own park is what the object shows then.
7. **An attempt in its admission phase is `activating`.** It is `Current`, not sealed and not live; a denial is a parked state of it. `Retry()` on it wakes its check and passes on to the admission's failed dependencies; it never starts another attempt (the 1.5.2 rule "failed and not waiting starts an attempt" would have).

### 2.3 What a cut does natively

`Rebuild(attempt, denial)`: a lingering object without holders ends instead. Else the attempt is sealed with what its running calls reject with (`unavailable: its admission changed`, or `forbidden/revoked` naming the denial), its sources kept until its close; `Current` becomes the new attempt; the state is `loading` or `failed { denied }`; the holders are told in the same flush (their members retarget: last values while it loads or waits, none for a denial); the close is posted.

- **Running calls.** A native call's caller waits on `completion.Task.WaitAsync(token)`, which the seal cancels. For a cut the wait is mapped to the seal's error, so a local caller sees `unavailable` or `forbidden/revoked` as upstream; other seals keep the 1.5.2 cancellation.
- **No value after a denial.** `ITarget.Followed` answers `denied` for a `failed { denied }` state; a reference's member then drops its value (`ReferenceMember.Deny`: reads throw the denial, a watch gets one failed call, the next value is a whole replacement), and a pinned key the same.
- **Presence.** Native presence lists the holders of objects that show `live`. An admitted object lists its holders from its first allow until a denial or its end, whatever it shows in between (a rebuild to another value, a failed source), as upstream; objects without an admission keep the native rule.

### 2.4 Route admission, natively

`RouteAdmission` keeps one entry per principal key and address: its `Decision`, the paths it gates and their holders. A path of an admitted route gets a gate (`IPathGate`): `Joined` says whether a holder may be forwarded now, `Left` drops it, `Unlinked` is told after the holders and before the route when the link is lost. A decision moves the holders of its entry. A denial withholds them (`RemotePath.Withhold`: released at the address's host, `failed { denied }` shown, calls in flight and waiting rejected `forbidden/revoked`). An allow forwards them (`RemotePath.Forward`): at once for a holder never on that link, behind a fence (`HostLink.Fence`, a ping whose pong proves every reply of the released hold arrived) for one that was; a denial before the pong cancels the fence. While the decision's source has failed, holds bound on the link stay, new holds wait, and a new link holds nothing until the source can be read (fail closed). An admission that holds a type placed behind its own route is refused `invalid/cycle` (the holding context travels through `RemoteIntegration.PathOf`). The settle continuation reads the decision again, so a denial that arrives N dispatcher turns into the settle still ends denied (the parameterised case, N = 0 to 7).

## 3. Suite counts

Builds with the pinned SDK (`artifacts/sample-tools/dotnet/dotnet.exe`), Release, 0 errors and 0 warnings each: `src/PiSharp.Chord.Hosting`, `src/PiSharp.Durable.Store`, `tests/PiSharp.Chord.Tests`, `tests/PiSharp.Chord.Hosting.Tests`, `tests/PiSharp.Chord.Durable.Integration.Tests`, `tests/PiSharp.Durable.Tests`.

| Suite | Base (`eb79d423`, from the brief) | This branch |
| --- | --- | --- |
| `PiSharp.Chord.Tests` | 1,429 cases, 0 failed | 1,508 cases (1,429 + 46 + 28 + 5), six full runs: runs 1, 2, 4, 5 and 6 had 0 failed; run 3 failed one case of this package (section 5), fixed in `053c78a9`; runs 4 to 6 are on the final source, and the fixed case's prefix (`host.admission.`) passed ten times alone |
| `PiSharp.Chord.Hosting.Tests` | 55, 0 failed | 55, 0 failed (the four `terminal.160.scope-*` cases also passed five times alone) |
| `PiSharp.Chord.Durable.Integration.Tests` | 7, 0 failed | 7, 0 failed |
| `PiSharp.Durable.Tests` (once, at the end) | about 4,851, 0 failed, 4 environment skips | 4,851 cases, 4,847 passed, 0 failed, 4 skipped (`compat.reverse.upstream-reads-pisharp-sessions` and the three `plan.symlink-escape.*`) |

Neither Chord suite has a skip status. No failure needed comparing against the base: the one failure was this package's own new case. The `host.list.pages-...` case of chord-queries timed out once during development under a `host.`-prefix run while the machine was loaded and passed alone three times; it did not fail in any of the six full runs.

## 4. The chord-edge conversions (edits outside the owned test paths)

chord-edge stood in for upstream's `desk()` by relinking as another principal, since admission was not ported then. Both fixtures are now upstream's: `Desk` declares `Admission = OneOf("reader", "editor")`; a local `DeskMode` (`mode`, `set`) starts at `reader`; the admission derives `Admission.Allow(mode)` for reader or editor and `Admission.Deny("no access")` otherwise; the client is linked at once; `Admit(mode)` sets the mode.

- `tests/PiSharp.Chord.Tests/Sync160EdgeSupport.cs` and `tests/PiSharp.Chord.Hosting.Tests/Sync160EdgeSupport.cs`: the fixtures; headers updated.
- `tests/PiSharp.Chord.Tests/Surfaces/Sync160ClientCases.cs`: `surfaces.variant.160.live-in-is-the-reference-while-live-in-the-variant` (no link step now), and `surfaces.variant.160.live-in-follows-a-rebind-to-another-variant` replaced by `surfaces.variant.160.live-in-follows-the-variant-an-admission-rebinds-denied-or-released`: reader, editor (write), denied (`failed` with a `Denied` cause, no variant, read in neither), reader again, released.
- `tests/PiSharp.Chord.Hosting.Tests/Sync160EdgeCases.cs`: `terminal.160.scope-variant-follows-rebind` (rebind by admit, then `admit("none")`: failed denied, read in neither variant), `terminal.160.scope-variant-of-foreign-reference` (rebinds by admit; the native-only tail that relinked after dispose is gone, upstream has none), `terminal.160.scope-keeps-no-released-reference` (no link step). `terminal.160.scope-variant-one-watch-per-state` keeps its hand-set reference.

Two upstream checks there are weaker natively, because a native watch is delivered on a pool thread and may fold two quick states into one call. variant.test.ts's exact per-state sequence (`loading reader`, `live editor`, `failed undefined`, `loading undefined`, `live reader`) is checked as the reference read at each settled point plus the watch's kinds (one `failed`, a `live` before it, `live` last). scope.test.ts's "exactly 2 redraws for a rebind" is "1 or 2". "Loading still says the last variant" stays covered by `terminal.160.scope-variant-one-watch-per-state`.

Other edits outside the owned paths: `src/PiSharp.Chord/Host/KeyedReference.cs` (a pinned key drops its value on a denial), `src/PiSharp.Chord/Host/RemoteIntegration.cs` (passes the holding context to `Route.Path` for the cycle refusal), and `tests/PiSharp.Chord.Tests/Remote/Sync160SharedHoldRemoteCases.cs` (chord-remote's `H6Upstream` test double implements the new `IUpstream` members `Caller`, `Withheld` and `Admitted`). `ChordSupport.cs` was not edited, and no root-owned file was touched.

## 5. Upstream cases not ported as written, and why

Every upstream case of the items above is ported; none is skipped. These are ported with an adaptation, each named in a comment at the case:

- Microtask orders. A check upstream makes "in the flush of the change" is made right after the awaited call that made it. Holding and watching before the microtasks run is one dispatcher turn (`A6HoldWatched`). "N turns into the settle" posts N dispatcher actions.
- `host.admission.type-reload.does-not-rehearse-...`: upstream reads no factory run straight after the reload. The native restarted object's activation is posted and may already have run when the reload's await continues (it did once, in full run 3), so the case counts the runs once it settled: one, the activation's. A rehearsal would also have failed the reload on the refused job, which the case checks.
- `a-denial-between-staging-and-activation`: upstream's gap is microtasks within a turn; the native one is a call the old attempt drains.
- Fake timers (`parked_long`, ready timeouts) are short real timeouts.
- Decisions counted weakly (upstream's `decisions` set) are read by reflection: an attempt's `decision` field, and the route's `RouteAdmission.DecisionsNow`.
- "A declaration outside admit's four": the native admit self is no `ISelf`, so the cast throws (upstream: "writable is not a function"). "A plain allow object": a native `Admit` returns a watchable, so the native form is a null one.
- Pre-existing port differences, not admission's: a member of an object failed live throws instead of keeping its last value; the invalid-method message is `write: not a member of the bound variant`; a live failure is reported `activation_failed`, not `object_failed`; an unserved type's message is `test.missing: no provider`; a reference member first made while its object loads has no last value (the case makes it while live).
- Upstream's path holders are a `Set` in attach order; the native path's are a hash set, so the unreachable order is compared sorted.
- Upstream's path cases run fences by hand; here the links are real `HostLink`s to raw peers, a fence is a real ping and running it is the peer's pong.
- The chord-edge variant and scope checks of section 4.

## 6. Upstream-wins notes

- The plan listed the `Binding.queryMember` denied branch as a source edit; the native code already throws the state's cause, which is the denial, so there is no edit.
- Upstream's scope test has no "relink after dispose" step, so the chord-edge tail that did one is gone.
- The plan's line ranges were followed only where the code agreed with them.

## 7. What chord-workers needs

Upstream `createWorkers(host, Kind, { ..., admit })` passes `admit` through to its route (`RouteOptions.admit`), so no worker starts for a denied principal. Natively: add `RouteAdmit? Admit { get; init; }` to `ProcessWorkerOptions` (`src/PiSharp.Chord.Hosting/Workers/ProcessWorkers.cs`) and pass it in the `host.Route(kind, new() { Authorize = ..., Resolve = ResolveAsync, Admit = options.Admit }, context)` call in the `ProcessWorkers` constructor. `RouteAdmit` is `(JsonObject principal, Address address, IAdmitSelf self) => IWatchable<JsonNode?>` and returns `Admission.Allow()` or `Admission.Deny(reason)`; it reads only contracts the app's API owns. With an admission, `Resolve` must return the link to the final provider (the workers' links are). The upstream case is workers.test.ts "passes admit to its route: a denied principal starts no worker, an allowed one does". Nothing under `Hosting/Workers/**` was edited here.

## 8. Native API added (for the release notes)

All in `PiSharp.Chord.Host`:

- `Admission.Allow(JsonNode? value = null)` and `Admission.Deny(string reason)`: the two decisions, as JSON.
- `IAdmitSelf`: what an admission declares with (`Get`, `Registry`, `Derive`, `LiveQuery`).
- `delegate Admit(JsonObject parameters, JsonObject principal, IAdmitSelf self)`: a served type's admission, returning a watchable of a decision.
- `delegate AdmittedHostFactory(JsonObject parameters, JsonObject principal, JsonNode? admitted, ISelf self)`: the factory of an admitted type.
- `IFacetHost.Serve(Contract type, AdmittedHostFactory factory, ServeOptions options)` and `ServeOptions.Admit`.
- `delegate RouteAdmit(JsonObject principal, Address address, IAdmitSelf self)` and `RouteOptions.Admit`: a route's admission per principal and address.
- Behaviour: a denial shows `failed` with a `Denied` cause (`FailureKind.Denied`, wire `denied`, from chord-foundation); its references keep no value; calls cut by it are rejected `forbidden/revoked`; `self.Job` is refused in an admitted factory.
