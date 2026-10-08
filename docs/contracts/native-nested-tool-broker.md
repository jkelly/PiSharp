# Native nested tool broker

This P3-04/P6-06 source milestone is authored, unbuilt and unexecuted. Independent review, native tests and complete source parity remain pending. All eight phase gates remain open.

The immutable target is Pi v0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`. The inspected released archive is 8,646,242 bytes, SHA-256 `4d99d3c9ed6db41f88ce7ba36d478b06a9386f4e81fa0c1ea3f93e681c99e83b`. Its `packages/coding-agent/src/core/extensions/types.ts` member is 81,892 bytes, SHA-256 `de0ccce3b8b222d19a2ecef24902fc72aab62b6573129cbf886bc064dd6ab505`.

[The pinned context contract](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/extensions/types.ts#L367) declares callable tools, cancellation/update options and `executeTool` at line 394. [The pinned nested runner](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/nested-tool-calls.ts) defines parent-derived IDs, events, exclusive reentrancy and bounded records. These files were read locally; no upstream module was executed or recaptured.

## Host composition

`ToolInvoker.WithNestedCalls` receives the existing prepared adapters, mandatory final-action policy, prepared hooks and transforms. `ToolInvocationScopeOptions` binds a positive host session generation and its cancellation token. Retire that token and join old work before publishing a replacement. Generation metadata alone grants no admission.

Set `ExtensionAgentBindingOptions.InvocationScopes` for standalone registered-tool composition. Set `SessionRuntimeRegistryOptions.InvocationScopes` for the composed native/extension runtime; it derives execution mode and sequential names from the selected configuration. `ExtensionToolAdapter` supplies the actual `IExtensionToolContext.ExecuteToolAsync` bridge to this invoker. It never dispatches another descriptor directly. Legacy dispatch has an unavailable error outcome, empty callable names and no fabricated call ID.

`Tools` contains immutable ordered callable names in this experimental native profile. Complete upstream tool descriptors/exposure/activation remain open. The shared CLI `OfflineSessionProfile` opts into `BindNestedCallsToSessionOwner`. `ReplaceableAgentSession` reserves the coordinator and binds a private runtime-registry copy to its actual attachment generation and linked attachment/coordinator lifetime before publication. Model/loadout changes keep that private binding. Failed staging disposes the target without retiring the source. Replacement retires the old capability before publishing the new attachment; old execution is already idle under the replacement reservation. Profile disposal cancels and joins the actual owner. No dummy generation or global mutable current-session lookup is installed. Direct unowned registry use retains the legacy unavailable nested-call path.

## Authority and ownership

Children get `<parent>/<n>`, actual parent/root IDs, depth, generation and linked operation/session/extension cancellation. Child cancellation cannot weaken owner lifetimes. Unknown, invalid, blocked, denied and thrown tool calls return error outcomes. Initial preparation, validation, argument hooks, transforms, final revalidation/authorization, execution and result hooks use the identical pipeline. Metadata survives preparation views.

Admission opens after final authorization and closes when the executor returns. An ancestor capability cannot be reused from a descendant to evade depth checks. Native defaults are depth 8 and 128 admitted descendants per root; repeated names are permitted. Explicit sequential calls queue in FIFO order, and exclusive descendants reenter without waiting for their own permit. Cancelled queued work joins its original predecessor before refusing execution. No state lock spans callbacks.

Each admitted task is retained through publication and actual cleanup. Registry callback admissions also retain ignored nested tasks and update receipts. Source-compatible nested progress shares the existing batch quota and joins actual delivery. These tests avoid per-case timeout detachment; a future execution owner must retain an incomplete nonpassing receipt and keep owning any original process after a diagnostic deadline.

Nested execution events expose `ToolInvocation.ParentToolCallId`; registered call/result reducers receive the same parent. Descendants do not produce transcript messages. A bounded `nestedCalls` record is attached to the root message after settlement. Source bounds are 256 records, 8 KiB arguments per call, 32 KiB total arguments and 500 error characters. Native envelope safety additionally limits retained ID/name bytes to 64 KiB, omits arguments whose record wrapper would exceed JSON depth admission, and preserves scalar Unicode at the error-text boundary. Omission marks the record incomplete; retained identities are never shortened. These explicit native limits require compatibility review.

## Tests and remaining scope

Ten `nested-broker-core.*` groups and seven `nested-broker-sdk.*` groups are authored only: final-target denial, failure envelopes, repeated-name recursion, aggregate admission limits, sequential reentrancy, ancestry authority, stale generation, ignored tasks, original cleanup/progress joins, bounded root records, and a scripted two-turn Agent using actual native registrations.

No build, native execution, live request, credential read, package download or source capture occurred. After authorization and exact ownership are approved, run the SDK filter, Agent suite, CodingAgent configuration regressions, Extensions/host/process/sessions suites and complete original milestone gates against identified products.

Still open: independent review; actual execution and genuine source differential evidence; full callable descriptors and activation/exposure; optional Node facade mapping; frontend parent-event wire handling; complete schemas, platforms and original package/phase criteria. Child usage remains unchanged in returned results. The shared root recorder separately sums every finalized descendant once, including errors and omitted records, then root message materialization combines that sum with the final root usage. This preserves result-hook replacement semantics without double aggregation at intermediate levels.

## Successor delivery correction (authored, unexecuted)

Queued children now retain their predecessor join on every exit, including failed start delivery. The scheduler supplies a batch-owned event sink to descendants: first delivery failure cancels actual execution, suppresses subsequent publications, and is rethrown by identity only after scheduler execution/progress settlement. Three additional regression groups cover failed middle-start delivery with three queued children, a parent swallowing a nested listener error, and source-compatible progress waiting for cancellation while cleanup is held. No compilation or native execution was performed.

## Host/usage successor evidence

Two additional core groups cover multi-level usage, optional splits, repeat projection and omitted/error records. Three host groups use actual ExtensionRegistry/ExtensionAgentBinding callbacks, scripted Agent turns, real durable session files and ReplaceableAgentSession: A→B→A generations and persisted usage, veto rollback, and disposal joining ignored nested cleanup. All are authored only. Total groups: 15 core, 7 SDK, 3 host.

Pinned local source inspection additionally checked `usage-totals.ts` (`combineUsage`) and `agent-session.ts` (`message_start` root projection). Mandatory input/output/cacheRead/cacheWrite/totalTokens and cost components sum; cacheWrite1h/reasoning appear when either operand supplies them. A singleton usage value remains opaque and unchanged. When combining values, the native implementation requires finite source-shaped numeric fields and rejects malformed/overflowing aggregates rather than fabricating zero totals or non-finite JSON. That native boundary is explicit and has not received differential acceptance.

No provider transport, terminal UI, queued-preview, credential, package or security-policy changes. The one-line CLI change is host composition only. Full compilation, the focused core/host/SDK groups and the existing replacement/progress/configuration suites require the pending bounded test-window authorization. No executable parity evidence or closed phase gate is claimed.
