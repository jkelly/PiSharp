# Actual session lifecycle presence and the missing selection caller hook

This isolated leaf starts on exact `4b4ec5d1c4af928e74f8a5e59c3a33ca5f517b6c` (the fixture-only OAuth correction over dbcd). It changes only the allocated CLI replacement publisher and adds one CLI producer, one own fixture and this document. Core Agent/session, RPC, terminal and shared Programs remain untouched.

`NativeReplacementSessionStartBinding.PublishAsync` now omits `previousSessionFile` when the actual outgoing `PersistentAgentSession.SessionFile` is null. A present local session path is retained exactly as a string. It does not substitute the in-memory namespace path. Reason mapping, actual attachment/generation checks, captured registry revision, registry-owned context, awaited publisher and postcommit reporting policy remain unchanged.

Two unexecuted controls create genuine sessions and native registry observers. They distinguish an actual volatile-memory previous session from a real local-file previous session, perform actual `SwitchAsync`, and hold the actual observer until released. They require the original switch and publisher to remain pending, actual target generation/context, exact omitted/string event presence, zero diagnostics and zero provider requests. Creation, navigation, publisher, held callback, deadline cancellation and every scope/registry/session cleanup original are recorded and joined; failure collectors retain full raw aggregates and direct exceptions.

The NEW `NativeSessionTreeObservationBinding.PublishAsync(owner, receipt, oldLeafId)` is a concrete explicitly callable CLI producer for the actual returned `SessionTreeNavigationReceipt`. It emits only `Selected`, validates the live exact attachment and selected context, and joins actual registry observation dispatch. The payload is `{type:"session_tree",newLeafId:receipt.LeafId,oldLeafId}`. Existing navigation performs no summary, so `summaryEntry` and `fromExtension` are absent. NoOp/Vetoed/Aborted do not emit. A postcommit failure carries the actual committed receipt and original dispatch Task/aggregate/direct; it cannot imply rollback or authorize replay. The two controls also call actual no-summary core navigation and this producer, verify current empty ancestry and original old leaf, then require no event for an actual NoOp.

## Exact proposal for the real RPC owner

The actual default selection callsite is `src/PiSharp.Rpc/Protocol/RpcSessionDispatcher.Navigation.cs` lines 131–140, in the `view.Mode == "tree"` branch. It currently awaits `_sessionOwner.NavigateTreeAsync(view.Core.Attachment, new(command.TargetId,view.Core.Revision),token,beforeTree)` and immediately returns the response. No CLI postselection callback, session core observation hook or `session_tree` emitter currently exists there.

The actual owner should add an OPTIONAL constructor/options dependency with this signature:

```csharp
Func<SessionTreeNavigationReceipt, string?, CancellationToken, ValueTask>? afterSessionTree
```

After the genuine `NavigateTreeAsync` original has returned, capture its actual receipt; only for `Disposition.Selected`, invoke that dependency once with `(receipt,view.Core.LeafId,CancellationToken.None)` and directly join the materialized original before encoding the Selected response. It must run outside state monitors, grant no new mutation or filesystem authority, and never replay selection. The cancellation-none boundary is deliberate postcommit shielding: late caller cancellation cannot undo selected state. Invocation and full original/cleanup failure inventory must be retained by that owner. Report publication failure with the exact committed receipt; do not turn it into a claim that selection rolled back. Legacy callers with null dependency retain exactly their existing path.

The CLI owner must bind that dependency to the admitted activation's actual registry/captured revision and existing `ReplaceableAgentSession`, calling this producer. The generic current `DispatchObservationsAsync` reports its first callback failure to the producer; this leaf does not invent the original Runner's per-handler continuation policy. Any required full original emit continuation belongs to the Runtime owner. These default RPC/CLI callsite additions are a proposal, NOT implemented or qualified here, because those files are outside this task's allocation. Actual default `session_tree` delivery remains blocked until the owner implements, reviews and qualifies that hook.

Pinned original references: Pi v0.99.1 commit `d86654abb8862e201933517d6f1fce9f88dd117f`, `packages/coding-agent/src/core/extensions/types.ts` (`SessionTreeEvent`/optional previousSessionFile) and `packages/coding-agent/src/core/agent-session.ts` (awaited session_tree emit after finalized context refresh). No full-tree/header/file authority is inferred from this event or the native snapshot wire. Native counterpart a519 and Task7's matching worker ABI acknowledgement are separate evidence; this leaf does not execute either.

No compiler, test, Node, HTTP, credential or native execution occurred. Shared runners still need exact registration of these two own Cases before any qualification credit.
