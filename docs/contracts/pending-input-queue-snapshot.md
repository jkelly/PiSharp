# Full pending-input queue snapshot and clear

This native prerequisite exposes complete independent steering and follow-up queues through the queue, high-level Agent and durable session coordinator. It supports future RPC `get_state`/`clear_queue` implementation. Existing enqueue, mode-selected peek/drain, clear and run lifecycle APIs retain their behavior.

## Immutable API and atomicity

`AgentPendingInputQueueSnapshot` contains initialized `ImmutableArray<TranscriptEntry>` values named `SteeringMessages` and `FollowUpMessages`, plus `SteeringMode` and `FollowUpMode`. Each array contains that queue's complete FIFO at one queue state boundary. It includes values beyond a `OneAtATime` peek and never mixes steering with follow-up.

`AgentPendingInputQueue.GetSnapshot(cancellationToken)` does not consume. `ClearAndSnapshot(cancellationToken)` constructs both complete arrays before resetting either queue, then atomically clears both and returns the prior contents/modes. It releases count and character capacity, preserves configured modes and never silently drops returned values. The already owned immutable canonical messages are retained directly; opaque fields, nulls and number tokens are not rewritten. Editing a returned immutable array creates a different array and cannot change queue state or an earlier receipt.

Snapshot construction, final cancellation check and reset occur under one queue gate. Admission, draining, clear and snapshot serialize at that boundary. A cancellation observed before reset throws without consuming either queue. Cancellation requested after the successful operation's final check does not undo the clear; the returned values remain its receipt. Bounds are the existing per-queue count/character/depth admission limits, not an aggregate heap claim.

Old `PeekQueuedMessages` still chooses the mode-selected steering prefix, otherwise follow-up prefix. Old `ClearAll`/`ClearQueues` remain void operations. Full snapshot and clear do not reinterpret or alter these source-mode semantics.

## Agent and durable session projection

Both `Agent` and `PersistentAgentSession` expose:

```csharp
AgentPendingInputQueueSnapshot GetPendingInputQueueSnapshot(CancellationToken cancellationToken = default);
AgentPendingInputQueueSnapshot ClearPendingInputQueues(CancellationToken cancellationToken = default);
```

These synchronous operations work while idle or running. Their existing lock order is coordinator → Agent → queue. They perform bounded immutable-state work only: no callback, event delivery, await, provider call or filesystem operation runs under these gates. Awaited event callbacks can call these methods without awaiting their own generation settlement.

A queue snapshot is distinct from `AgentSnapshot.PendingInputs`. Once an input has been drained and admitted to the active generation, it is absent from the queue snapshot and cannot be canceled or removed by queue clearing. It may remain visible as a generation pending input until committed. Clear does not abort work, erase committed history, settle a listener barrier or trigger a new provider turn.

A full queue receipt is atomic with respect to both queues and their modes. It is not an atomic combined snapshot with a separately obtained Agent or durable-log snapshot. Consumers must use its arrays/lengths directly rather than synthesize contents from counts or combine independently observed counts with a mode-selected peek.

Read-only full snapshots remain observable after Agent/coordinator fault or disposal. Mutation preserves existing admission policy: Agent clear rejects disposed state; coordinator clear rejects disposed or faulted state. Clears do not repair a fault or acknowledge uncommitted history. Coordinator queue operations take its lifecycle gate, so they serialize with disposal/configuration admission and other queue mutations. They may run during an admitted configuration checkpoint because clearing these independent, non-durable queues neither changes its prospective configuration nor bypasses publication.

Queued values are in-memory pending inputs; they are not appended merely by enqueue, snapshot or clear. A later admitted run still uses the accepted primary durable message-end barriers. Clearing during a last run-end listener leaves that listener and `WaitForIdleAsync` outstanding until normal settlement.

## Public source and native differences

Pinned public `d86654abb8862e201933517d6f1fce9f88dd117f` [Agent implementation](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/agent.ts) keeps separate FIFO queues and mode-selected peek/drain. Its Agent clear removes both queues. The [coding-agent session](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/agent-session.ts) maintains separate user text bookkeeping arrays; `clearQueue()` returns complete steering/follow-up text arrays before clearing the Agent queues. [RPC dispatch](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/modes/rpc/rpc-mode.ts) forwards that result for `clear_queue`.

This seam returns canonical native system/user transcript values, not the upstream coding session's separate text/image/editor bookkeeping or an already qualified RPC wire result. A later frontend must project those values explicitly. Atomic concurrency, immutable receipts, capacity and cancellation checks are authored native contracts. No unchanged-upstream differential queue result or complete RPC behavior is claimed.

## Supplied evidence

`PendingInputQueueSnapshotTests.Cases()` supplies five authored groups:

- Full FIFO/mode ownership beyond one-at-a-time peek, opaque token/null retention, canceled reads/clears and capacity reuse.
- Gate-released concurrent admissions and clears partition every steering/follow-up value exactly once.
- Running Agent clears retain already-drained generation inputs; awaited callbacks can snapshot/clear; provider/history behavior is preserved.
- Real durable coordinator queue operations preserve acknowledged leaf/bytes/history while the final end listener still holds settlement.
- Faulted/disposed coordinator queues remain inspectable; mutation is rejected and no provider acquisition or durable rewrite occurs.

These tests use deterministic gates and deadlines without timing sleeps or live providers. Registration/build and immutable acceptance are parent-owned; this document does not claim an executed passing result. Pending-input persistence, editor text/image restoration, queue-update wire events and full RPC cancellation semantics remain integration work.
