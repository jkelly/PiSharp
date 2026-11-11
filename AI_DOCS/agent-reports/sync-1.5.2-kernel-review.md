# Independent kernel / wire review, Pi 1.5.2

Read-only source review against frozen upstream `b4afdddf44e059c67ccf5097e5317d913521b470`. Reviewed native Host old-work/reload/readiness/call contexts, Remote provider/consumer/holds/relays, Link channel ordering, and existing native/follow/method generation leases. No product/test edits and no builds or test runs by this reviewer; only this report was written. The implementation owners made the fixes below while the review continued.

## Findings

### P1: queued outgoing calls retained stale method indexes through rebind — fixed by wire owner

As reviewed, `src/PiSharp.Chord/Remote/Consumer.cs:132-133` handled `LoadingMessage` only by moving the reference to loading. Calls in the consumer's waiting list retained their numeric method indexes and could be transmitted after the successor `Bound` was acknowledged. The frozen upstream consumer's `#loading` deliberately rejects waiting calls while retaining already-sent calls.

Regression trigger: fill the peer's call window, queue a call whose old variant index differs from the new variant, deliver `Loading`, deliver and acknowledge the successor `Bound`, then settle an in-flight call. The queued call is sent with the stale index after the provider has switched to the new names, causing a wrong-method invocation (or a protocol error for an out-of-range index).

Current corrective location: `src/PiSharp.Chord/Remote/Consumer.cs:136` calls `FailWaiting` before `Loading`; in-flight calls remain retained. Owner added `remote.152.loading-cannot-send-queued-index-after-reordered-bound` at `tests/PiSharp.Chord.Tests/Remote/Remote152Cases.cs:56` and adjusted the basic loading case to distinguish waiting from in-flight. Source fix reviewed; execution results belong to the root's serialized validation.

### P2: settling a parent erased the old-work provenance of live descendants — fixed by kernel owner

As reviewed, `CallContext.Dispose` in `src/PiSharp.Chord/Host/Context.cs` called `CallNode.Clear`, which replaced its attempt with an inert marker and dropped its parent chain. That mutable node is also held by descendant call/root contexts. A descendant begun under the parent therefore silently lost the parent's old-work provenance when that parent settled. Upstream `CallContext.settle` releases references but does not erase its call-chain node.

Regression trigger: an old method starts an independent root under `WithoutAbortSignal(call.Context)` and returns; another call holds that same old attempt open during reload. Resume the root and call the reloading object. It must use the still-open old attempt, but the erased chain instead treats it as new work and waits at the successor gate. This can extend to a drain-timeout deadlock when the old work awaits that descendant.

Current corrective locations: `src/PiSharp.Chord/Host/Context.cs:30` captures `attempt.OldWorkState`; `src/PiSharp.Chord/Host/OldWork.cs:8-9` defines a lightweight draining token and immutable chain. Disposal at `Context.cs:49` no longer mutates the chain. The attempt toggles its token at `Attempt.cs:282` / `:308`; cycle checks compare the same token at `OldWork.cs:57`. This preserves descendants without making their chain retain a whole retired attempt/ALC. Owner added `host.sync152.detached-root-keeps-old-provenance-after-parent-settles` at `tests/PiSharp.Chord.Tests/Host/Sync152KernelCases.cs:59`. Source fix reviewed; execution results belong to root.

## Other reviewed paths

- Incoming old-work is honored only on trusted links; relay forwarding independently strips the flag on an untrusted downstream, including when telemetry is disabled.
- Provider reply flushes one pending own state message before result/error and increments the own message counter. Relayed acknowledgments are queued separately and do not increment the own acknowledgment count.
- Incoming hold method-name decoding retains its previous contract/variant until the corresponding own `Bound` acknowledgment; loading does not itself cancel admitted provider calls.
- Restart records open-old before successor start, applies all served-type changes before starting readiness, and gates ordinary calls while the old attempt drains. Its call/routine drain repeatedly samples unfinished work and uses the absolute reload deadline.
- Method callback leases enclose the actual handler/replacement plus result mapping; native and follow lifetimes retain code across public close/activation timeouts until late close/detach originals finish. Existing `Host/CallbackLifetimeCases.cs` covers these ownership paths, including old declaring-facet identity.

No further actionable defect was established in these reviewed paths. This is a scoped source review, not an exhaustive proof of the broad Chord migration or a substitute for the pending test run.
