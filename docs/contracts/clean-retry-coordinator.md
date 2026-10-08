# Clean retry coordinator source contract

Independent source-only implementation on clean policy/settings commit a890daf.
Pinned source is Pi v0.99.1 d86654abb8862e201933517d6f1fce9f88dd117f:
`packages/coding-agent/src/core/agent-session.ts` retry preparation, assistant
acknowledgement counter reset, cancellation, and terminal events. No quarantined
source, patch or composition was read or reused. All fixture controls are
UNEXECUTED; shared runner registration belongs to root.

Root creates one `SessionRetryCoordinator` per admitted session operation, retaining
it through the exact active owner. Its live policy delegate reads the session's
current acknowledged preference. `PrepareRetryAsync` receives the final assistant
stop reason/error and an already resolved context-overflow exclusion. After the
physical provider original settles, root first fences new Bash admission, joins
admitted originals and flushes pending Bash messages. General transient retry is
considered before threshold compaction, with context overflow excluded from the
classifier. If no retry is scheduled, finish the failed retry observation before
existing overflow/threshold compaction. Keep the Bash fence through each planning
decision and release it before the next physical provider starts. Root passes an omission delegate which resolves
the acknowledged failed assistant entry under the same active owner and calls
existing `OmitRecoveryAttemptAsync`; unresolved projected entries fail admission.
The durable edit omits the failed assistant from model projection while retaining
raw history. Completed tools and earlier turns remain and must never be replayed.

The coordinator reads live enabled/budget/delay preferences on each preparation.
Initial provider calls consume no retry attempt. Retry-start is awaited before
omission; the original omission/checkpoint/context refresh settles before entering
backoff. `IsRetrying` denotes admitted backoff only, including its cancelled final
observer. A true result admits actual `_agent.ContinueAsync` under the existing
active owner; root keeps combined turn history and respects original session
cancellation and loop limits. False means root settles the provider response.

Root must await `CompleteAssistantAsync` on EACH acknowledged non-error assistant,
including successful tool-use inside a multi-turn run, rather than only the final
provider result. This resets the retry count at the pinned source's message boundary.
Pinned session source treats every non-error stop reason as success here. Root
calls `FinishAsync` on terminal error/exhaustion after preparation settles and
`FinishCancelledAsync` when run cancellation leaves a scheduled retry uncompleted
(for example between successful backoff and provider continuation). It emits
one final end event with attempt and optional final error. Cancellation during
backoff emits failure with `Retry cancelled`. Native departure: callbacks and durable
omission are asynchronous originals and observer failures propagate; a preparation
fault joins a final failure observer and retains both original exception references.

`AbortRetryAsync` cancels only backoff and returns one stable original join task until
a new backoff starts. It directly joins delay and final observer originals; a delay
implementation that ignores cancellation remains owned. `JoinAsync` captures every
admitted coordinator original. Root closes session admission before joining and
retains its own active provider/omission lifetime; abort cannot detach preparation.
Root session wait/close/abort entry guards must reject when `IsOwnedCallback` is true,
in addition to existing guards. This covers async observer/omission/delay scopes and
external synchronous unsafe cancellation callbacks. Observer delegates must return
their complete actual task; no fire-and-forget progress is admitted by this API.

Root projects started/ended records to operation-generation-bound session events
and RPC `auto_retry_start`/`auto_retry_end`, preserving awaited original ownership.
Host/settings capture and acknowledged persistence, RPC controls/state, lifecycle
retention and shared recovery-loop hooks remain root-owned. This leaf does not
change those files or claim ordinary-host integration.

Controls cover held start/omission/delay/end ordering, live policy budgets and
exclusions, exhaustion, per-assistant reset, ignored-cancellation delay originals,
stable abort, observer reentry, unsafe external cancellation callbacks, and original
body plus cleanup faults. Fixtures release and join each held original in cleanup.
