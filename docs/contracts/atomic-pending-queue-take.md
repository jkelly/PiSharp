# Atomic pending-queue take

Status: authored source only; unbuilt, unexecuted and pending independent review. This is a native host coordination contract, not a new upstream RPC command.

## Public API

Agent and PersistentAgentSession expose the same operation:

```csharp
bool TryClearPendingInputQueues(
    AgentPendingInputQueueSnapshot expected,
    [NotNullWhen(true)] out AgentPendingInputQueueSnapshot? removed,
    CancellationToken cancellationToken = default);
```

The underlying AgentPendingInputQueue exposes TryClearAndSnapshot with the same parameters. Capture expected through GetPendingInputQueueSnapshot (GetSnapshot at queue level). It includes both complete FIFOs and both drain modes, regardless of one-at-a-time selection. Preflight editor text, content shape and response capacity outside locks. Then compare-and-clear once.

A matching call returns true and the exact removed queues/modes. A mismatch returns false with removed=null and changes nothing. Null expected throws ArgumentNullException. A manually constructed/deserialized snapshot cannot authorize removal. A with-copy retaining the exact entries/modes and opaque revision is equivalent; edited values are checked and rejected. The four public snapshot properties and serialized shape are unchanged.

Each queue owns an opaque nonserialized revision. Successful enqueue, nonempty drain/clear, and actual mode change replace that revision. Returning to identical contents or modes does not make an old snapshot valid (ABA rejection); snapshots from another Agent/session are invalid. Reads, rejected/canceled mutations, empty clears/drains and setting a mode to its existing value do not change the revision. Empty matching takes succeed and remove nothing.

Comparison, snapshot construction and mutation share one queue lock. Allocation and cancellation checks finish before the indivisible reset of both queues. Cancellation observed before commit throws and leaves state intact. Cancellation racing after that final check may occur after the commit: a successful receipt remains authoritative, and callers must complete their receipt/publication protocol rather than retrying removal. No external callbacks or await run under the locks.

Agent uses its existing disposed guard and lock. PersistentAgentSession uses the same availability, replacement and queue-preflight mutation guards as ClearPendingInputQueues. Neither operation removes already-drained inputs, cancels active work, changes drain modes, writes durable history, or implements editor policy. The session generation must still be bound by the RPC caller's existing replacement/publication serialization.

## Terminal caller integration

Keep the captured snapshot object in process; do not recreate it from JSON. Validate the exact prospective editor string and response against this snapshot. On false, obtain and preflight a fresh snapshot before any retry. Bound retries under concurrent mutation. Never invoke the old unconditional clear after a mismatch. On true, return/publish the exact removed receipt even if caller cancellation arrives afterward, using the existing owned output protocol.

The prepared terminal restoration candidate 8f04a88c869abe979ae5019f5a697390e2444158 is not merged here. This branch changes no terminal, RPC, provider, package, credential or security-policy files.

## Authored coverage

Seven atomic queue take groups in CodingAgent.Tests directly await all original operations. They cover foreign/forged/stale snapshots, same-message and mode ABA, FIFO duplicates and budget reuse, deterministic growth beyond a 65536-character editor preflight, one winner among concurrent removers, precommit cancellation and cancellation while blocked on the actual queue lock, preservation of active admitted work/durable bytes, and host preflight/disposal guards. Snapshot serialization retains four public fields. These tests have not been run; source inspection and git diff checks are the only current evidence.
