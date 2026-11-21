# chord-remote: Chord 1.5.2 -> 1.6.0, the remote layer, events and shared holds

Work package `chord-remote` of the Pi 1.6.0 sync, 2026-11-21. Branch `sync/1.6.0-chord-remote`, from `9aa79a22` (the merge of `chord-foundation`). Upstream v1.6.0 is `E:/AI/PiSharp/upstream-v160` (`faac585884ae2c013a6b1400f3e9e77f10b34bac`). The plan was `AI_DOCS/agent-reports/sync-1.6.0-chord-discovery.md`, the layer below `AI_DOCS/agent-reports/sync-1.6.0-chord-foundation.md`.

Both stages are done.

- **R1** (call windows by trust, ack before live, binding fences, events end to end, `opened(incarnation)`) ends at commit **`a7a75bd0`**. `chord-queries` can start from that commit or from the branch tip.
- **R2** (shared holds, upstream `47e9d74f9` and `90ed3e3b1`) ends at the branch tip.

## 1. Upstream changes in this area

Status is `ported` (code and a case that ran), `already-ported`, `no-op` or `deferred`, as the plan file defines them. Nothing in this package's scope is deferred. Rows marked "other package" are 1.6.0 changes in files this package owns that belong to `chord-admission` or `chord-queries` by the plan.

| Upstream | Stage | Status | C# | Cases |
|---|---|---|---|---|
| `remote/calls.ts`: no-window mode, `removeWhere` | R1 | ported | new `Remote/Calls.cs` (`CallWindow<T>`, `IQueuedCall`) | `remote.calls.*` (2) |
| `remote/host-link.ts`: window by trust, `event` routing, `opened(incarnation)`, `fence` | R1 | ported | `Remote/HostLink.cs` | `remote.host-link.call-windows-*` (3), `host.route.resolves-opened-*` |
| `remote/consumer.ts`: optional window, per-origin relay windows, ack before `wentLive`, `event` handling and the event ack | R1 | ported | `Remote/Consumer.cs` | `remote.consumer.sends-*` (2), `remote.consumer.events-*` (6) |
| `remote/consumer.ts`: `cancelCalls`, the call's owner | R2 | ported | `Remote/Consumer.cs` | `remote.consumer.shared-*` (2), `remote.shared-hold.cancellation-*` (3) |
| `remote/provider.ts`: optional limit, relayed calls in the upstream window (`#rejectRelayed`, release order, the relayed `bound` ack), `queueEvent`/`sendEvents`/`#eventAck`, tickets, `fenced` | R1 | ported | `Remote/Provider.cs` | `remote.provider.call-limit-*` (3), `remote.provider.events-*` (10), `remote.relay.*` (18) |
| `remote/relay.ts`: `RelayedCall`, `submit`, `endAll` | R1 | ported | `Remote/Relay.cs` | `remote.relay.window-*` (10) |
| `remote/hold.ts`: binding fences (`#latest`, `#fences`, `acknowledged`), `#tapEvents`/`#untap`, `callAs` | R1 | ported | `Remote/Hold.cs` | `remote.hold.fences-*`, `remote.hold.drops-a-fence-*`, the provider event cases, both existing `remote.152.*` index cases |
| `remote/remote-object.ts`: event nodes per binding, `eventNode`, `event` | R1 | ported | `Remote/RemoteObject.cs` | the consumer event cases |
| `remote/remote-object.ts`: `#bindings`, `hold`/`unhold`, `retry(walk, binding)`, `#retryAlone`, `#place`, `#unseen`, `generationOf`, `tooLarge` for every binding, `#unpublish`, the hold span as a root | R2 | ported | `Remote/RemoteObject.cs` | `remote.shared-hold.*` (43) |
| `remote/replica.ts`: `generation`, `fork` | R2 | ported | `Remote/Replica.cs` | `remote.replica.*` (2) |
| `remote/path.ts`: `suspended`, `requested` | R2 | ported | `Remote/Path.cs` | `remote.path.*` (2), `host.route.shared-*` |
| `host/events.ts` (new) | R1 | ported | new `Host/Events.cs` | `host.events.*` (19) |
| `host/reference-events.ts` (new) | R1 | ported | new `Host/ReferenceEvents.cs` | `host.reference-events.*` (9) |
| `host/adapted.ts`: event mappings, refusal over events | R1 | ported | `Host/Adapted.cs` | `host.adapted.events-of-an-older-consumer-*` (5) |
| `host/attempt.ts`: `Self.events`, `eventNode`, adapted event wiring, the detach step's events | R1 | ported | `Host/Events.cs` (partials of `Attempt`, `Self`), 4 one-line hunks in `Host/Attempt.cs` | the event cases |
| `host/members.ts`: `checkEvents` | R1 | ported | `Host/Staging.cs` (`ValidateImplementation`), `Host/Events.cs` (`EventImplementations`) | `host.members.event-members-*` (2) |
| `host/staging.ts`: inert `events()` | R1 | ported | `Host/Events.cs` (partial of `RecordingSelf`, `RecordingEvents`), `Host/Staging.cs` (`RecordingValue.Event`) | `host.staging.hands-out-an-inert-event-source-*` |
| `host/reference.ts`: event taps, the reference's event members | R1 | ported | `Host/Reference.cs` (`Binding.Event`, `SubscribeEvents`, `ForwardEvents`, `RetargetEvents`) | the event cases |
| `host/reference.ts`: `Target.call(binding)`, `retry(walk, binding)`, `generationOf`, `joined`, `transfer` | R2 | ported | `Host/Internal.cs` (`ITarget`), `Host/Reference.cs` (`Joined`, `Seen`, `Transfer`) | `remote.shared-hold.seen-*` (5), `-too-large-*` (2), `host.reference.a-binding-moved-*` |
| `host/reference-source.ts`: unseen members | R2 | ported differently (below) | `Host/Reference.cs` (`ReferenceMember`) | `remote.shared-hold.seen-*` (5) |
| `host/host.ts`: `events`, `callWindow`, `eventWindow`, `event_mapping_failed`, `routeLink(..., opened)` | R1 | ported | `Host/Host.cs`, `Host/Contracts.cs`, `Host/RemoteIntegration.cs` | the cases above |
| `host/host.ts`: `#bindRemote`, `#sharingKey`, the sharing table | R2 | ported | `Host/RemoteIntegration.cs` (`BindOver`, `SharingKey`), `Host/Host.cs` (`BindRemote` in `Place`), `Remote/Path.cs` (`Shared`) | `host.bind.shared-*` (6) |
| `host/object.ts`: `call(binding)`, `generationOf`, `callAs` | R2 | ported | `Host/Object.cs` (`CallAsync(holder)`, `CallAs`); `GenerationOf` is `ITarget`'s default (null) | `host.object.calls-for-a-reference-*` |
| `host/route.ts`: resolve result `opened` | R1 | ported | `Host/Route.cs` (`ResolvedLink.Opened`) | `host.route.resolves-opened-*` |
| `host/route.ts`: `suspended` on the path | R2 | ported | `Host/Route.cs` | `host.route.shared-a-suspended-address-*` |
| `telemetry.ts`: `chord.events.emitted/sent/dropped/encodes` emission | R1 | ported (rows were foundation's) | `Host/Events.cs`, `Host/Adapted.cs`, `Remote/Provider.cs`, `Remote/RemoteObject.cs` | `remote.provider.events-drop-*`, `-encode-*`, `host.events.cost-*`, `remote.consumer.events-acknowledge-dropped-*` |
| `test/telemetry.test.ts`: the re-hold expectation (a first hold is a root linked to its getter) | R2 | ported | `Remote/RemoteObject.cs` | `remote.shared-hold.telemetry-*` (2). The native suite had no port of that upstream case at 1.5.2, so no existing expectation changed |
| `remote/consumer.ts`, `provider.ts`, `relay.ts`, `hold.ts`, `path.ts`, `remote-object.ts`, `host/route.ts`, `host/reference-source.ts`: `denied`, `withheld`, `admitted`, `PathGate`, `deny` | — | other package (`chord-admission`) | — | — |
| `remote/remote-object.ts`, `host/reference.ts`: `queryMember` | — | other package (`chord-queries`) | — | — |
| `*/README.md`, `docs/**`, `test/bench/{events,shared-holds,state-binding}.ts`, `test/examples/67-events.ts`, `68-shared-holds.ts` | — | no-op: upstream documentation, benchmark, example | — | — |

Upstream wins over the discovery report, or the native structure made the port differ:

- **The `hello` window is per link.** The discovery report puts the 256 limit in `Remote/Provider.cs:81`; upstream computes `calls` once in `HostLink` (`trusted ? undefined : host.callWindow`) and hands it to each `ProviderSide`. Ported that way.
- **`failed` on the consumer side.** Upstream's `#failed` rejects only the calls still waiting for a slot. Native 1.5.2 rejects the in-flight ones too, and `remote.152.loading-retains-running-rejects-queued-calls` asserts it against a raw peer. Kept (no existing case weakened); a real provider answers its in-flight calls before `failed` either way.
- **`HostError` needs no new field** for `event_mapping_failed`: `Object`, `Member`, `Version` and `Error` exist.
- **The sharing table lives on the path** (`RemotePath.Shared`). Upstream keeps it in a `WeakMap<Path, Map>`; a field has the same lifetime ("a dropped route path takes its table") without a weak table.
- **Unseen members.** Upstream's `ReferenceSource` delegates a bound member's start to its replica, which would show a joiner the replica's last value from before its join; `unseen` hides it. The native `ReferenceMember` only ever shows values it recorded itself, so a joiner sees nothing from before its join by construction. `Binding.Joined`/`Seen` are ported for the other direction: a member handle made while its replica is failed starts with the replica's last good value once its reference has seen the member (upstream's "failed, then good, then failed again before the joiner's first read"). That also applies to a first, unshared reference, which before showed no last value in that corner. The three `reference-source.test.ts` unit cases have no native seam; the five `remote.shared-hold.seen-*` cases cover the behaviour.
- **`ITarget.GenerationOf`** is a default interface member (null): `Host/Object.cs` needed no code for it.

## 2. Commits

| Commit | What |
|---|---|
| `444f3167` | Source: call windows by trust, ack before live, binding fences, events end to end, `ResolvedLink.Opened` |
| `fcbd42b6` | Host event cases (31) and `Sync160RemoteSupport.cs` |
| `880a3c55` | Adapted event cases (5); fix: an event mapping entry without a function is no mapping |
| `ed713b49` | Call window, relay window, fence and `opened` cases (24) |
| `c508438b` | Remote event cases (25) |
| **`a7a75bd0`** | **End of stage R1** (the first version of this report) |
| `a1972a0b` | Source: shared holds |
| `4f709c6a` | Shared-hold remote cases (47); a revocation ends a shared remote object before its references |
| `dc03e11c` | Shared-hold host, route and reload cases (15); three fixes they found (section 6) |
| `c797186b` | Two shared-hold collection cases; the route file's header |
| (this report's commit) | End of stage R2 |

## 3. Design notes: what replaces the microtasks

There are no microtasks natively. The host has one dispatcher (`HostDispatcher`, a FIFO `SynchronizationContext`): every message a link delivers is one posted action, a commit (`Batch.Commit`) sends its `state` messages synchronously inside the action that made it, and continuations of host work rejoin the same queue. "Turn" below means one dispatcher action.

1. **The event flush after state.** `HostEvents.Push` posts the flush (`Dispatcher.Post`) when it adds the first entry of an empty list. What the pushing action publishes, before or after its emit, is on the wire before the flush posted behind that action runs, so a turn's `state` precedes its `event` message, as upstream's microtask gives it (`remote.provider.events-send-a-turns-events-*` asserts `["state", "event"]` for emit, set, emit in one turn). `IEventSource.Emit` moves to the dispatcher (`Dispatcher.Invoke`), so it may be called from any thread; code that leaves the dispatcher between an emit and a commit (`ConfigureAwait(false)`, another thread) makes two turns, and its events may then precede that later state.
2. **The event ack after delivery.** The first `event` message consumed since the last event ack posts the ack to the dispatcher. It was posted behind the flush that message's entries posted, so the listeners (and a relay's forwarding) run before the ack is sent, and the ack counts every event message consumed by the time it runs. The channel hands a link's messages over one at a time and reads the next once the previous one was handled, so in practice each event message is its own turn and gets its own ack; upstream coalesces the messages that arrive in one JavaScript turn into one ack. The provider's event window only needs the counts to add up in order, which they do.
3. **Subscriptions stop from any thread.** `Subscription.Stop` (a caller's thread, a cancellation callback) lets its listener go at once, so a call already due is skipped as upstream says, and tells its owner on the dispatcher.
4. **Parked calls in order.** Upstream's `ParkedCalls` sends a reference's parked calls in order once it is live. The native wait was `Task.WaitAsync`, which sends every waiter through the pool, so two parked calls could go out in either order. `Binding` now keeps its waiters in a list and tells them in order; each continuation is posted to the dispatcher as it is told (section 6).
5. **A fence.** `HostLink.Fence(then)` (for `chord-admission`) posts `then` to the dispatcher from the channel's pong; messages handled before the pong were posted before it.

Two observable differences follow and are recorded in section 5: upstream delivers nothing of an event whose `loading` and next `bound` arrive in the same inbound turn (natively the event is flushed in its own turn, while its binding is still current), and the callbacks of two different watches have no order between them (a native watch's listener is never invoked inline).

## 4. Suites

Pinned SDK, `-c Release`, commands as in the plan file. Builds of `src/PiSharp.Chord.Hosting`, `tests/PiSharp.Chord.Tests`, `tests/PiSharp.Chord.Hosting.Tests`, `tests/PiSharp.Chord.Durable.Integration.Tests`: 0 errors, 0 warnings.

| Suite | Base `9aa79a22` | End of R1 (`a7a75bd0`) | Branch tip |
|---|---|---|---|
| `PiSharp.Chord.Tests` (no arguments) | 1,083 passed, 0 failed | 1,168 passed, 0 failed | 1,232 cases, 1,232 passed, 0 failed |
| `PiSharp.Chord.Hosting.Tests` (no arguments) | 46 passed, 0 failed | 46 passed, 0 failed | 46 passed, 0 failed |
| `PiSharp.Chord.Durable.Integration.Tests` | 7 passed, 0 failed | 7 passed, 0 failed | 7 passed, 0 failed |

Neither Chord suite has a skip status: 0 skipped everywhere. The 149 new cases: 44 host (events 19, reference events 9, members 2, staging 1, adapted 5, bind 6, object 1, reference 1), 105 remote and route (calls 2, consumer 10, host-link 3, provider 15, relay 18, hold 4, path 2, replica 2, shared hold 43, route 6).

Timing. The full Chord suite was run three times in a row at the tip (1,232 passed each time), and the 185 remote, route and event cases eight more times in a row before the last two cases were added: no failure. While the shared-hold cases were being written, two cases failed in some runs; both were real ordering defects, fixed in section 6 (parked calls sent out of order; a closing host showing its remote objects `loading` on the way), not test flakes. No case that this package did not write failed at any point except the two existing cases whose expectation upstream changed (below), so there is nothing to compare against the base.

**Two existing cases changed because upstream changed the behaviour** (rule 4): `remote.runtime.frame-validation-is-all-or-none` and `remote.runtime.oversized-owned-part-isolates-hold` held one type twice from one host and expected two holds. Since 1.6.0 that is one shared hold. They now hold two types, which keeps what they assert (two entries in one `bound`/`state` frame).

Not run: every suite outside the three above. Nothing outside `src/PiSharp.Chord` and `tests/PiSharp.Chord.Tests` was changed.

## 5. Upstream cases not ported as written, and why

| Upstream case | What happened |
|---|---|
| `consumer.test.ts` "keep the event window exhausted while an asynchronous inflate holds the message" | Not ported. It stubs the global `DecompressionStream` to hold the inflate; the native asynchronous inflate (`Parts.InflateAsync`) has no seam a test can hold. The property (the ack follows consumption) is covered by `remote.consumer.events-inflate-nothing-*`, which waits for the ack of a deflated value. |
| `consumer.test.ts` "deliver nothing of an earlier binding: an event, a loading and a bound ... in one turn" | Not portable: no inbound turn spans several messages natively (section 3). Authored instead: `remote.consumer.events-160-an-earlier-bindings-event-is-delivered-in-its-own-turn-*` (an event arriving while the hold rebinds is dropped; after a `bound` only that binding's arrive). |
| `consumer.test.ts` "deliver the entries ... acknowledge once per inbound turn" | Ported with the native ack rule: `eventAcks == [2]` became "the counts sum to 2, none before its delivery". |
| `relay.test.ts` "failed { denied }: the waiting calls are rejected forbidden/revoked" | `chord-admission`. |
| `path.test.ts` "a gated path" (4), `reference-source.test.ts` "a denied reference's source" (1) | `chord-admission`. |
| `reference-source.test.ts` "a member its reference has not seen" (3) | No native `unseen` seam (section 1); covered by `remote.shared-hold.seen-*`. |
| `events.test.ts` "a contract without event members costs no event bookkeeping" | It spies on `HostEvents.prototype.counter`. Ported as what can be read: such a binding hands out no event member, the hosts' counters stay 0, and the variants share one empty event-name list. |
| `reference-events.test.ts` "keep no node of an instance a reload replaced" | The delivery half is `host.reference-events.160-a-reload-moves-own-re-exposed-and-mapped-members-*`. The collection half gathers every `EventNode` ever made by patching a prototype: not ported. |
| `remote-object.test.ts` "failure and loading fan out ...: watch callbacks in batch order" | Ported; the cross-reference order is read from `Binding.Changed` (synchronous) instead of two watches' callbacks, which have no order between them natively. Each watch's own order is still asserted. |
| `remote-object.test.ts` "gone and the host's close end every binding ..." | Ported as two cases (`-state-gone-*`, `-state-the-hosts-close-*`). |
| `hold.test.ts` "two references sharing one hold follow a restart, a dropped version and a removal together" | Ported; the exact state list (`loading, live, loading, failed, loading, live`) became "both references see the same states, through the failure, live at the end, one hold at the provider". The native provider keeps a restart's hold bound to the old attempt until the successor is live, so a restart shows no `loading` (1.5.2 behaviour, not changed here). |
| `host.test.ts` "one hold per type, version, fingerprint ..." | Ported; "another version" is an evolved contract (v2) instead of the same contract redeclared at version 2. |
| GC cases read directly instead of by collection: `adapted.test.ts` "keep no mapping ...", `hold.test.ts` "drops a fence ...", `provider.test.ts` "retire a provider side's record ...", `remote-object.test.ts` "no remote object stays after its last release ..." and "a released joiner with queued calls ..." | Ported, asserting the state itself (the source is untapped and the mapping no longer runs; the hold has no fence; the link's provider-side count; the path's sharing table and holders are empty; the queued calls reject and never go). |
| `remote-object.test.ts` "a route path dropped after its last holder takes its sharing table" | Not ported as a case: the table is a field of the path (section 1), so it cannot outlive it. |
| `remote-object.test.ts` "a released reference ... is not kept by a live sibling" and "after a retry alone, the old object keeps neither ..." | Ported with weak references (`remote.shared-hold.gc-a-released-reference-*`, `-gc-after-a-retry-alone-*`). |

Everything else in the upstream delta of `test/remote/**`, `test/host/{events,reference-events,adapted,members,staging,route,host,object,reference}.test.ts` and `test/telemetry.test.ts` for this package's features has a native case.

## 6. Fixes outside the upstream delta, found by the ported cases

All in files this package owns.

1. **A hold of a dropped version stayed bound** (`Remote/Hold.cs`). After a version-dropping reload, a hold of the old version was kept by the draining old attempt (correct), but when the drain ended it was rebound to the new code with failed members instead of leaving its object `failed { waiting, contract_mismatch }`. `IncomingHold.Moved` now removes it as the publication of a removed type does. Case: `remote.hold.shared-a-reference-made-during-a-version-dropping-reloads-drain-*`.
2. **A closing host showed its remote references `loading` before `ended`** (`Host/RemoteIntegration.cs`). `CloseRemote` closes the links first; each closing link told its path, which sent its holders `loading`. `Unlink` now skips the paths while the host closes, and `CloseRemote` ends route-path holders too.
3. **Parked calls could go out of order** (`Host/Reference.cs`): section 3, point 4.
4. **A revocation rejected a revoked remote reference's running calls `released`** (`Host/RemoteIntegration.cs`). The references were ended one by one before their remote object was revoked, which released the hold as an ordinary release does. The remote objects are revoked first now, so the calls reject `forbidden` (reason `revoked`), as upstream. Case: `remote.shared-hold.events-revocation-*`.
5. **An event mapping entry without a function** (`Host/Adapted.cs`, this package's own code before it was committed in `880a3c55`): treated as "refused" instead of "unchanged".

## 7. The open item of discovery 5.8 that touches this package

**Does native abort in-flight inbound calls when their link closes, and refuse a transaction under an aborted call context?** Yes to both, determined by `remote.provider.160-a-link-closing-aborts-its-inbound-calls-and-a-transaction-under-the-aborted-context-is-refused`:

- `HostLink.Closed` closes every provider side, and `ProviderSide.Close` cancels the `CancellationTokenSource` of each served call: the handler's `ctx.AbortSignal` fires. (A .NET token carries no reason; upstream aborts with `unavailable: the link closed`.)
- A `self.TxAsync(..., ctx.Context)` started under that aborted context does not run: it throws `OperationCanceledException` and the host reports `transaction_cancelled`.
- When the closing link held the object's last hold, the object ends with it and the transaction is refused `released` instead (the attempt is sealed before the handler's continuation runs).

## 8. Edits outside the owned paths

None. `tests/PiSharp.Chord.Tests/ChordSupport.cs`, `Program.cs` and `TelemetryIngestCases.cs` are untouched: the native case `telemetry-ingest.sender.page-through-memory-link-no-ingest-feedback` runs its page host without a telemetry adapter, so the sender's own hold makes no `chord.hold` span and its expectation does not change (upstream's did because its page host records).

The shared kernel files were edited only in the hunks the 4.3 table assigns:

| File | Hunks (lines changed) |
|---|---|
| `Host/Attempt.cs` (9) | the adaptation's event initializers, `ValidateImplementation(..., this)`, `BindEventMembers()` after the members are stored, `DetachEvents()` in `DetachSources` (not in `Self.Detach`, so it does not sit on `chord-store`'s `Reads.Retire` line). Everything else of `Attempt`/`Self` for events is a partial declaration in `Host/Events.cs`. |
| `Host/Staging.cs` (9) | `ValidateImplementation` takes the attempt and checks event members; `RecordingValue.Event`. `RecordingSelf.Events` is a partial in `Host/Events.cs`. |
| `Host/Host.cs` (11) | the `Events` field, the window check in the constructor, `BindRemote` (was `RemoteTarget`) and its two-line use in `Place`. |
| `Host/Contracts.cs` (16) | `HostOptions.CallWindow`/`EventWindow`, `IReference.Event`, `ISelf.Events`. |
| `Host/Object.cs` (9) | `CallAsync(method, args, holder, context)`, `CallAs`, `Retry(walk, holder)` passing the dependency's binding on. |

The headers of the five shared kernel files still name the 1.5.x commit: they are not at 1.6.0 until `chord-queries` and `chord-admission` are done, and moving the line here would collide with `chord-store`'s edit of the same line. `Host/KeyedReference.cs` and `Host/RemoteActivation.cs` had no 1.6.0 change in this package's features and keep their headers.

## 9. What the next packages need

**`chord-queries`**

- `ITarget` (`Host/Internal.cs`) changed: `CallAsync(string method, object? args, IHolder holder, ChordContext context)`, `Retry(RetryWalk walk, IHolder? holder = null)`, `long? GenerationOf(string member) => null`. Add `QueryMember` beside them; `RemoteObject` needs the throwing one (`... has no queries member "x": named queries are local`).
- `PluginStaging.ValidateImplementation(contract, implementation, attempt)` still refuses a `queries` member ("not a member of the implementation variant"): add `shape.Queries` beside the `shape.Events` lines.
- A recording's inert handles: `RecordingEvents` in `Host/Events.cs` is the pattern for the query ones.

**`chord-admission`**

- `HostLink.Fence(then)` runs `then` on the dispatcher and returns the cancel.
- `RemotePath(changed, suspended)` takes the gate as a third argument when it comes; `RemotePath.Shared` is the sharing table; `Requested` exists. `Withhold`, `Forward`, `Wanted` and the released/fence maps are not there.
- `RemoteObject` fans every state out to its bindings through `Move`; `Withheld`/`Admitted` and "a denial drops its replicas" (`bound = null` before the move) are to add. `ConsumerSide.MessageAsync` (`failed`) and `ProviderSide.Forward` (`failed`) reject with the cause's error; a `denied` cause needs `revoked(...)` there. `RelayedHold` has no `withheld`/`admitted`/`#heldBack`.
- `IncomingHold` (`Remote/Hold.cs`): `Decoding` already carries the error a crossed call is refused with; set it to `revoked(...)` for a denial in `Moved` and `Refuse`. `Moved` now removes a hold whose version is no longer bound once no open old attempt serves it (section 6).
- `Binding` (`Host/Reference.cs`): `Joined`, `Seen`, `Transfer`, the ordered waiters (`NextChange`/`Wake`) and the event retarget are new; the denied state is not there.
- Upstream's `desk()` fixture and its tests assume shared holds: they are in.

**`chord-workers`**

- `ResolvedLink(link, options, opened)`: `opened(incarnation)` is called on the dispatcher at the handshake, before anything else the peer sent is handled.
- Event members for `TerminalScope.On`: `IReference.Event(name).OnAsync(listener, context)` returns an `IEventSubscription`.
- Section 7: an inbound call is aborted when its link closes, and a transaction under its context does not run.

**Root**

- New files for the integrity table and the source inventory: `src/PiSharp.Chord/Host/Events.cs`, `Host/ReferenceEvents.cs`, `Remote/Calls.cs`; `tests/PiSharp.Chord.Tests/Sync160RemoteSupport.cs`, `Host/Sync160EventsCases.cs`, `Host/Sync160SharedHoldCases.cs`, `Remote/Sync160WindowCases.cs`, `Remote/Sync160RemoteEventCases.cs`, `Remote/Sync160SharedHoldRemoteCases.cs`.
- Headers moved to `faac5858...`: every file under `Remote/` except `WireDelta.cs`, and `Host/{Adapted,Reference,RemoteIntegration,Route}.cs`.
- The merge with `chord-store`: the only adjacent spot is `Attempt.DetachSources` (this package's `DetachEvents()` on the `Self.Detach(error)` line) against its `Reads.Retire` line in `Self.Detach`; they are in different methods here.
- Two internal accessors exist for the cases only: `HostLink.Providers` and `ChordHost.Links`.

## 10. Release notes

**Added (experimental)**

- Event members end to end: `ISelf.Events()` (an `IEventSource`), `IReference.Event(name)` (an `IEventMember`), event mappings for older versions (`VersionMappings.Events`) honoured by the host, events over links and through relays.
- `HostOptions.CallWindow` (10,000) and `HostOptions.EventWindow` (32).
- `ResolvedLink.Opened`: a route's resolver is told the peer's incarnation at the handshake.
- Host error kinds `event_mapping_failed`, and `listener_error` for an event listener that threw. Counters `chord.events.emitted`, `chord.events.sent`, `chord.events.dropped { reason }`, `chord.events.encodes`.

**Changed**

- Remote references of one host to one object share one wire hold: one `hold`, one snapshot and one `chord.hold` span per sharing key (type, version, fingerprint, params, placed address, caller principal and incarnation). A second reference is live at once; releasing one of several sends nothing but the cancellation of its own calls; presence and `Activity.References` at the provider count one per hold, not one per reference. A `self.Get` reference and one on a suspended route address still hold on their own.
- The `chord.hold` span of a remote reference is a root on the host's adapter, linked to its first getter (it was a child of the getter's span); a joining getter's span links to it.
- A part past the link's limits (`too_large/part`) fails every reference sharing the hold; `Retry()` on one of them moves that reference alone to a hold of its own.
- A revoked principal's running calls on a remote reference reject `forbidden` (reason `revoked`); they rejected `released`.
- The consumer acknowledges a `bound` before its references go live. A provider no longer closes a trusted link for the number of calls running on it. A reference's parked calls go out in the order they were made.
- A hold of a contract version a reload dropped fails `contract_mismatch` once the old code's running calls drained; it stayed bound to the new code with failed members.

**Wire compatibility**

- Protocol version stays 1. A host announces no call window on a link it was given as trusted (`hello` without `calls`) and its `CallWindow` (10,000 by default, was 256) on the others. A PiSharp 1.5.x peer on a trusted link reads the missing `calls` as a zero window or rejects the `hello`: 1.5.x and 1.6.0 hosts do not interoperate over trusted links (workers, host-to-host routes). On an untrusted link each side keeps within the window the other announced, so a 1.5.x peer still works there, but it does not understand `event` messages or event acks.
- A consumer of 1.6.0 sends event acks (`ack` with `events`); a 1.5.x provider would count one as a state ack. Events are only sent for contracts that declare event members.

**Binary-breaking, source-compatible**

- `ResolvedLink` gained a trailing positional parameter (`Opened`). `ISelf` and `IReference` gained default-implemented members (`Events()`, `Event(name)`).
