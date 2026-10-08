# Native event bus ownership successor R552

This source-only successor preserves blocked R551 commit `8831f6e82f72a2bb9736d5e9716d9728f6eaac64`, its bundle, manifest, source document and ownership review as historical evidence. Review `R551_EVENT_BUS_OWNERSHIP_SOURCE_REVIEW.json` SHA-256 `2aa772d1a5e6cc181845777981151772d0160bff2629fd45eaf06449648901f5` found that raw listener invocation bypassed callback admission/drain and ancestor-aware reentry guards. R551 must not be qualified as an ownership-safe implementation.

## Resulting ownership behavior

Every native owner subscription now creates an internal `RegistrationKind.EventBus` entry through existing `ExtensionRegistry.Add`, charging the global/owner registration and metadata limits. Its private generated identifier is distinct from the public channel, preserving arbitrary channel strings without exposing them as dispatch identifiers. The subscriber holds the existing registration handle and the shared emitter handle.

At actual delivery, `ExtensionRegistry.EventBus.cs` takes the existing registry gate, rejects closing, initializing, paused, retired or replaced owners/entries, and calls existing `Admit` for that exact internal entry. Admission under the same gate as close/quiesce increments `ActiveCallbacks`, `entry.Leases`, dispatch count and the existing idle settlement. User code runs outside the gate inside the existing ancestor-aware `CallbackFrame`. A finally block calls existing `ReleaseAdmission`. Close and quiescence therefore join the actual synchronous invocation; a retired entry remains charged until its final admitted callback returns. No asynchronous callback or invented callback task is admitted.

The owner facade also checks the live scope state before emit/on, in addition to lifetime cancellation. A paused owner's retained bus cannot emit or subscribe. A quiescence lease resumes delivery through the existing resume path. Same-owner, ancestor-owner and same-registry close/quiesce attempts inside a nested listener use the existing reentry guards before mutation. Closing a different, unadmitted owner can succeed; a listener already captured for that now-retired owner is skipped.

Only known stale/paused/retired states return without delivery. Unexpected `Admit` policy/structural errors remain exception objects and pass through the shared emitter's existing diagnostic/error-isolation path, rather than being silently mistaken for retirement.

## Public catalog compatibility

Existing-file deltas beyond R551 are:

* `src/PiSharp.Extensions.Runtime/Registration/StagedRegistrationSet.cs`: append `EventBus` to the internal enum.
* `src/PiSharp.Extensions.Runtime/Registration/ExtensionRegistrySnapshot.cs`: filter only `EventBus` out of public `Registrations`; internal `Entries` still retains the entry for admission and retirement.

All runtime enum call sites were inspected as source. Typed tool/command metadata and reducer dispatch use exact kind filters before descriptor casts. Tool catalog replacement carries unmodified entries forward and selects/removes only `Tool` entries. Its count/metadata computations retain bus charges. There is no enum switch default casting a bus listener to a typed descriptor. `NativeExtensionActivation.cs:65` rejects unknown public kinds; filtering the internal lease avoids that rejection without a CLI change. The new initialization/publication control checks empty public registrations/tools/commands while a bus listener is live. Other public snapshot consumers inspected were CLI MCP ownership detection and Agent tool diagnostics; they inspect public descriptors or exact `Tool` rows, not internal bus leases.

## Original semantics boundaries

The three original source pins in the preserved R551 document remain unchanged (original Pi v0.99.1 commit `d86654abb8862e201933517d6f1fce9f88dd117f`). Standalone `ExtensionEventBus` still preserves synchronous order, reference/null payloads, current emitter snapshots, duplicate independent subscriptions, clear and error isolation.

Native owner safety deliberately narrows the standalone snapshot behavior: a captured listener whose native entry has retired or whose owner has paused is not invoked. Native initialization subscriptions stage until successful activation, rather than original loader-time immediate listener visibility. These are intentional native ownership boundaries and do not establish unchanged original behavior.

Promise-returning listeners and asynchronous-void callbacks remain unsupported. Their original-task settlement/fault/cancellation mapping is not implemented. Original `createEventBus` directly delegates to Node `EventEmitter.emit`, so an unmatched `error` channel inherits Node's special unhandled-error behavior. This native bus currently treats all channels uniformly and emits nothing if there are no listeners. That is an explicit remaining implementation gap; JavaScript Error/non-Error payload translation and exception identity have not been specified or qualified. No full original event-bus or SDK ABI equivalence is claimed.

## Authored controls and verification boundary

The standalone project now has fourteen authored controls. Six added controls hold a synchronous callback while close or quiescence runs, retire a later listener after its emitter snapshot has been captured, verify same/ancestor/registry lifecycle reentry rejection, retain registration charges through an admitted retired callback, and verify initialization publication plus public-catalog exclusion. Test-created `Task.Run` emissions are original test orchestration tasks, captured once and joined in finally blocks; the implementation creates no callback tasks. Gates have bounded failure waits.

All compiler, restore, test, NativeRunner, Node, network, server and process qualification remains parent-owned and unrun by this source lane. Source inspection, whitespace verification and bundle integrity do not imply behavioral acceptance. The SDK ledger remains partial: callback ownership has a source fix awaiting independent review/execution; async listener behavior, unmatched error semantics and the rest of the original SDK ABI remain implementation gaps, whereas these fourteen authored controls are unrun verification.
