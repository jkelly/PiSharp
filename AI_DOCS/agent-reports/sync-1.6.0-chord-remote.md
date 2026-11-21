# chord-remote: Chord 1.5.2 -> 1.6.0, the remote layer and events

Work package `chord-remote` of the Pi 1.6.0 sync, 2026-11-21. Branch `sync/1.6.0-chord-remote`, from `9aa79a22` (the merge of `chord-foundation`). Upstream v1.6.0 is `E:/AI/PiSharp/upstream-v160` (`faac585884ae2c013a6b1400f3e9e77f10b34bac`). The plan was `AI_DOCS/agent-reports/sync-1.6.0-chord-discovery.md`, the layer below `AI_DOCS/agent-reports/sync-1.6.0-chord-foundation.md`.

Two stages. **R1** (call windows by trust, ack before live, binding fences, events end to end, `opened(incarnation)`) is complete at the commit named in section 2; `chord-queries` can start from it. **R2** (shared holds) follows; its status is in section 1.

## 1. Upstream changes in this area

Status is `ported` (code and a case that ran), `already-ported`, `no-op` or `deferred`, as the plan file defines them.

| Upstream | Stage | Status | C# | Cases |
|---|---|---|---|---|
| `remote/calls.ts`: no-window mode, `removeWhere` | R1 | ported | new `Remote/Calls.cs` (`CallWindow<T>`, `IQueuedCall`) | `remote.calls.*` (2) |
| `remote/host-link.ts`: window by trust, `event` routing, `opened(incarnation)`, `fence` | R1 | ported | `Remote/HostLink.cs` | `remote.host-link.call-windows-*` (3), `host.route.resolves-opened-*` |
| `remote/consumer.ts`: optional window, per-origin relay windows, ack before `wentLive`, `event` handling and the event ack | R1 | ported | `Remote/Consumer.cs` | `remote.consumer.sends-*` (2), `remote.consumer.events-*` (6) |
| `remote/consumer.ts`: `cancelCalls`, the call's owner | R2 | see R2 | `Remote/Consumer.cs` | see R2 |
| `remote/consumer.ts`: `denied` rejects queued calls `forbidden/revoked` | — | not this package (`chord-admission`) | — | — |
| `remote/provider.ts`: optional limit, relayed calls in the upstream window (`#rejectRelayed`, release order, the relayed `bound` ack), `queueEvent`/`sendEvents`/`#eventAck`, tickets, `fenced` | R1 | ported | `Remote/Provider.cs` | `remote.provider.call-limit-*` (3), `remote.provider.events-*` (10), `remote.relay.*` |
| `remote/provider.ts`: `withheld`, `#failure` for `denied` | — | not this package (`chord-admission`) | — | — |
| `remote/relay.ts`: `RelayedCall`, `submit`, `endAll` | R1 | ported | `Remote/Relay.cs` | `remote.relay.window-*` (10) |
| `remote/relay.ts`: `withheld`, `admitted`, `#unreached` for a denial | — | not this package (`chord-admission`) | — | — |
| `remote/hold.ts`: binding fences (`#latest`, `#fences`, `acknowledged`), `#tapEvents`/`#untap`, `callAs` | R1 | ported | `Remote/Hold.cs` | `remote.hold.*` (2), the provider event cases, both existing `remote.152.*` index cases |
| `remote/hold.ts`: `#unbind` on denial | — | not this package (`chord-admission`) | — | — |
| `remote/remote-object.ts`: event nodes per binding, `eventNode`, `event` | R1 | ported | `Remote/RemoteObject.cs` | the consumer event cases |
| `remote/remote-object.ts`: `#bindings`, `hold`/`unhold`, `retry(walk, binding)`, `#retryAlone`, `#unseen`, `generationOf` | R2 | see R2 | | |
| `remote/replica.ts`: `generation`, `fork` | R2 | see R2 | | |
| `remote/path.ts`: `suspended`, `requested` | R2 | see R2 | | |
| `remote/path.ts`: `PathGate`, `withhold`, `forward`, `wanted` | — | not this package (`chord-admission`) | — | — |
| `host/events.ts` (new) | R1 | ported | new `Host/Events.cs` | `host.events.*` (19) |
| `host/reference-events.ts` (new) | R1 | ported | new `Host/ReferenceEvents.cs` | `host.reference-events.*` (9) |
| `host/adapted.ts`: event mappings, refusal over events | R1 | ported | `Host/Adapted.cs` | `host.adapted.events-of-an-older-consumer-*` (5) |
| `host/attempt.ts`: `Self.events`, `eventNode`, adapted event wiring, the detach step's events | R1 | ported | `Host/Events.cs` (partials of `Attempt`, `Self`), 4 one-line hunks in `Host/Attempt.cs` | the event cases |
| `host/members.ts`: `checkEvents` | R1 | ported | `Host/Staging.cs` (`ValidateImplementation`), `Host/Events.cs` (`EventImplementations`) | `host.members.event-members-*` (2) |
| `host/staging.ts`: inert `events()` | R1 | ported | `Host/Events.cs` (partial of `RecordingSelf`, `RecordingEvents`), `Host/Staging.cs` (`RecordingValue.Event`) | `host.staging.hands-out-an-inert-event-source-*` |
| `host/reference.ts`: event taps, the reference's event members | R1 | ported | `Host/Reference.cs` (`Binding.Event`, `SubscribeEvents`, `ForwardEvents`, `RetargetEvents`) | the event cases |
| `host/reference.ts`: `Target.call(binding)`, `retry(walk, binding)`, `generationOf`, `joined`, `transfer` | R2 | see R2 | | |
| `host/reference-source.ts`: unseen members | R2 | see R2 | | |
| `host/reference-source.ts`: `deny` | — | not this package (`chord-admission`) | — | — |
| `host/host.ts`: `events`, `callWindow`, `eventWindow`, `event_mapping_failed`, `routeLink(..., opened)` | R1 | ported | `Host/Host.cs`, `Host/Contracts.cs`, `Host/RemoteIntegration.cs` | the cases above |
| `host/host.ts`: `#bindRemote`, `#sharingKey`, the sharing table | R2 | see R2 | | |
| `host/object.ts`: `call(binding)`, `generationOf`, `callAs` | R2 | see R2 | | |
| `host/route.ts`: resolve result `opened` | R1 | ported | `Host/Route.cs` (`ResolvedLink.Opened`) | `host.route.resolves-opened-*` |
| `host/route.ts`: `suspended` on the path | R2 | see R2 | | |
| `host/route.ts`: `admit`, the gate, cycle refusal | — | not this package (`chord-admission`) | — | — |
| `telemetry.ts`: `chord.events.emitted/sent/dropped/encodes` emission | R1 | ported (rows were foundation's) | `Host/Events.cs`, `Host/Adapted.cs`, `Remote/Provider.cs`, `Remote/RemoteObject.cs` | `remote.provider.events-drop-*`, `-encode-*`, `host.events.cost-*`, `remote.consumer.events-acknowledge-dropped-*` |
| `*/README.md`, `test/bench/events.ts`, `test/bench/state-binding.ts`, `test/examples/67-events.ts` | — | no-op: upstream documentation, benchmark, example | — | — |

Upstream wins over the discovery report in three places:

- **The provider's `hello` window is per link, not per provider side.** The discovery report lists `Remote/Provider.cs:81` as the place the 256 limit lives; upstream computes `calls` once in `HostLink` (`trusted ? undefined : host.callWindow`) and hands it to each `ProviderSide`. Ported that way.
- **`failed` on the consumer side.** Upstream's `#failed` rejects only the calls still waiting for a slot (the provider rejects the ones in flight). Native 1.5.2 rejects both, and `remote.152.loading-retains-running-rejects-queued-calls` asserts it against a raw peer that sends only `failed`. Kept as it was (no existing case weakened); a real provider answers its in-flight calls before `failed` either way.
- **`HostError` needs no new field for `event_mapping_failed`**: `Object`, `Member`, `Version` and `Error` exist.

## 2. Commits

| Commit | What |
|---|---|
| `444f3167` | Source: call windows by trust, ack before live, binding fences, events end to end, `ResolvedLink.Opened` |
| `fcbd42b6` | Host event cases (31) and `Sync160RemoteSupport.cs` |
| `880a3c55` | Adapted event cases (5); fix: an event mapping entry without a function is no mapping |
| `ed713b49` | Call window, relay window, fence and `opened` cases (24) |
| `c508438b` | Remote event cases (25) |
| (this report's commit) | **End of stage R1** |

## 3. Design notes: what replaces the microtasks

There are no microtasks natively. The host has one dispatcher (`HostDispatcher`, a FIFO `SynchronizationContext`): every message a link delivers is one posted action, a commit (`Batch.Commit`) sends its `state` messages synchronously inside the action that made it, and continuations of host work rejoin the same queue. "Turn" below means one dispatcher action.

1. **The event flush after state.** `HostEvents.Push` posts the flush (`Dispatcher.Post`) when it adds the first entry of an empty list. What the pushing action publishes, before or after its emit, is on the wire before the flush posted behind that action runs, so a turn's `state` precedes its `event` message, as upstream's microtask gives it (`remote.provider.events-send-a-turns-events-*` asserts `["state", "event"]` for emit, set, emit in one turn). `IEventSource.Emit` moves to the dispatcher (`Dispatcher.Invoke`), so it may be called from any thread; code that leaves the dispatcher between an emit and a commit (`ConfigureAwait(false)`, another thread) makes two turns, and its events may then precede that later state.
2. **One event ack per inbound turn, after delivery.** The first `event` message consumed since the last event ack posts the ack to the dispatcher. It was posted behind the flush that message's entries posted, so the listeners (and a relay's forwarding) run before the ack is sent, and the ack counts every event message consumed by the time it runs. The channel hands a link's messages over one at a time and reads the next once the previous one was handled, so in practice each event message is its own turn and gets its own ack; upstream coalesces messages that arrive in one JavaScript turn into one ack. The provider's event window only needs the counts to add up in order, which they do. `remote.consumer.events-deliver-the-entries-in-order-*` asserts the native form (no ack before its entries were delivered, counts summing to the messages consumed).
3. **Subscriptions stop from any thread.** `Subscription.Stop` (a caller's thread, a cancellation callback) lets its listener go at once, so a call already due is skipped as upstream says, and tells its owner on the dispatcher.
4. **A fence.** `HostLink.Fence(then)` (for `chord-admission`) posts `then` to the dispatcher from the channel's pong; messages handled before the pong were posted before it.

One observable difference follows from point 2 and is recorded as a case that could not be ported as written (section 5): upstream delivers nothing of an event whose `loading` and next `bound` arrive in the same inbound turn; natively the event is flushed in its own turn, while its binding is still current.

## 4. Suites

Pinned SDK, `-c Release`, commands as in the plan file. Builds of `src/PiSharp.Chord.Hosting`, `tests/PiSharp.Chord.Tests`, `tests/PiSharp.Chord.Hosting.Tests`, `tests/PiSharp.Chord.Durable.Integration.Tests`: 0 errors, 0 warnings.

| Suite | Base `9aa79a22` | End of R1 |
|---|---|---|
| `PiSharp.Chord.Tests` (no arguments) | 1,083 passed, 0 failed | 1,168 cases, 1,168 passed, 0 failed (three consecutive full runs) |
| `PiSharp.Chord.Hosting.Tests` (no arguments) | 46 passed, 0 failed | 46 passed, 0 failed |
| `PiSharp.Chord.Durable.Integration.Tests` | 7 passed, 0 failed | 7 passed, 0 failed |

Neither Chord suite has a skip status: 0 skipped everywhere. No case failed at the end of R1, so there is nothing to compare against the base.

## 5. Upstream cases of R1 not ported, and why

| Upstream case | Reason |
|---|---|
| `consumer.test.ts` "keep the event window exhausted while an asynchronous inflate holds the message" | It stubs the global `DecompressionStream` to hold the inflate. The native asynchronous inflate (`Parts.InflateAsync`) has no seam a test can hold; the property (the ack follows consumption) is covered by `remote.consumer.events-inflate-nothing-*`, which waits for the ack of a deflated value. |
| `consumer.test.ts` "deliver nothing of an earlier binding: an event, a loading and a bound ... in one turn" | No inbound turn spans several messages natively (section 3). Authored instead: `remote.consumer.events-160-an-earlier-bindings-event-is-delivered-in-its-own-turn-*` (an event arriving while the hold rebinds is dropped; after a `bound` only that binding's arrive). |
| `consumer.test.ts` "deliver the entries ... acknowledge once per inbound turn" | Ported with the native ack rule (section 3): the assertion `eventAcks == [2]` became "the counts sum to 2, none before its delivery". |
| `relay.test.ts` "failed { denied }: the waiting calls are rejected forbidden/revoked" | `denied` handling is `chord-admission`'s. |
| `events.test.ts` "a contract without event members costs no event bookkeeping" | It spies on `HostEvents.prototype.counter`. Ported as what can be read: such a binding hands out no event member, the hosts' counters stay 0, and the variants share one empty event-name list. |
| `reference-events.test.ts` "keep no node of an instance a reload replaced" (collection half) | It gathers every `EventNode` ever made by patching a prototype. The delivery half is `host.reference-events.160-a-reload-moves-own-re-exposed-and-mapped-members-*`. |
| `adapted.test.ts` "keep no mapping once their version's last reference left (GC)", `hold.test.ts` "drops a fence with the ack of its bound (GC)", `provider.test.ts` "retire a provider side's record only once its event tickets drained (GC)" | Ported, but the assertion reads the state directly (the source is untapped and the mapping no longer runs; the hold has no fence; the link's provider-side count) instead of waiting for a collection. |
| `path.test.ts` "a gated path" (4), `reference-source.test.ts` "a denied reference's source" | `chord-admission`. |

## 6. The open item of discovery 5.8 that touches this package

**Does native abort in-flight inbound calls when their link closes, and refuse a transaction under an aborted call context?** Yes to both, determined by `remote.provider.160-a-link-closing-aborts-its-inbound-calls-and-a-transaction-under-the-aborted-context-is-refused`:

- `HostLink.Closed` closes every provider side, and `ProviderSide.Close` cancels the `CancellationTokenSource` of each served call: the handler's `ctx.AbortSignal` fires. (A .NET token carries no reason; upstream aborts with `unavailable: the link closed`.)
- A `self.TxAsync(..., ctx.Context)` started under that aborted context does not run: it throws `OperationCanceledException` and the host reports `transaction_cancelled`.
- When the closing link held the object's last hold, the object ends with it and the transaction is refused `released` instead (the attempt is sealed before the handler's continuation runs).

## 7. Edits outside the owned paths

None so far. The shared kernel files were edited only in the hunks the 4.3 table assigns:

| File | Hunks |
|---|---|
| `Host/Attempt.cs` | 4 single-statement hunks: the adaptation's event initializers, `ValidateImplementation(..., this)`, `BindEventMembers()` after the members are stored, `DetachEvents()` in `DetachSources` (not in `Self.Detach`, so it does not sit on `chord-store`'s `Reads.Retire` line). Everything else of `Attempt`/`Self` for events is a partial declaration in `Host/Events.cs`. |
| `Host/Staging.cs` | `ValidateImplementation` takes the attempt and checks event members; `RecordingValue.Event`. `RecordingSelf.Events` is a partial in `Host/Events.cs`. |
| `Host/Host.cs` | the `Events` field, the window check in the constructor. |
| `Host/Contracts.cs` | `HostOptions.CallWindow`/`EventWindow`, `IReference.Event`, `ISelf.Events`. |
| `Host/Object.cs` | untouched in R1. |

The headers of the five shared kernel files still name the 1.5.x commit: they are not at 1.6.0 until `chord-queries` and `chord-admission` are done, and moving the line here would collide with `chord-store`'s edit of the same line.

## 8. What the next packages need

- **`chord-queries`** can start from the end of R1. Nothing in its area depends on shared holds except the adjacent lines named in discovery 4.4 (`ITarget` gains `GenerationOf` and the holder parameter in R2). A `queries` member in an implementation is still refused by `ValidateImplementation` ("not a member of the implementation variant"): add `shape.Queries` beside the `shape.Events` line. `RemoteObject` has no `QueryMember` yet.
- **`chord-admission`**: `HostLink.Fence(then)` runs `then` on the dispatcher. The `denied` paths are untouched: `ConsumerSide.MessageAsync` (`failed`), `ProviderSide.Forward` (`failed`), `IncomingHold.Moved`/`Refuse`, `RelayedHold` (`withheld`, `admitted`), `RemotePath` (the gate) and `RemoteObject` (`Withheld`, `Admitted`, dropping replicas on a denial). `Decoding` in `Remote/Hold.cs` already carries the error a crossed call is refused with: a denial sets it to `revoked(...)`.
- **`chord-workers`**: `ResolvedLink(link, options, opened)`; event members for `TerminalScope.On` are `IReference.Event(name).OnAsync(listener, context)`.

## 9. Release notes

- **Added (experimental):** event members end to end (`ISelf.Events()`, `IReference.Event(name)`, `VersionMappings.Events` honoured by the host), `HostOptions.CallWindow` (10,000) and `HostOptions.EventWindow` (32), `ResolvedLink.Opened`, the host error kinds `event_mapping_failed` and `listener_error` for an event listener, the counters `chord.events.emitted/sent/dropped/encodes`.
- **Changed, wire-compatible only with 1.6.0 peers:** a host announces no call window on a link it was given as trusted (`hello` without `calls`) and 10,000 instead of 256 on the others. A PiSharp 1.5.x peer reads a missing `calls` as a zero window or rejects the `hello`; a 1.5.x peer on an untrusted link works but may now receive up to 10,000 concurrent calls where it announced 256 itself (each side honours the other's announcement). Protocol version stays 1.
- **Changed:** the consumer acknowledges a `bound` before its references go live. A provider no longer closes a trusted link for the number of calls running on it.
- **Binary-breaking, source-compatible:** `ResolvedLink` gained a trailing positional parameter; `ISelf` and `IReference` gained default-implemented members.
