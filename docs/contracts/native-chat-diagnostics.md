# Native chat diagnostics and default Completions messages

The default Completions producer carries its bounded native classification separately from AssistantMessage. It no longer originates `openAICompletionsFailure` in model-visible/Pi-wire message data. There is no serializer blacklist: unknown/imported source or extension fields, including a field with that name, remain opaque data. Such a field alone is never admitted as an internally originated diagnostic.

This implements the exact integration-lead carrier contract released against source-only base `0792fc08df127711a51365d7d493957f148f8a02`. It does not finish P2-01/08/09/10, approve the public API/extension ABI, close a phase, or qualify durable session/RPC/CLI diagnostic consumers. Those consumers and their native persistence/report schemas remain integration-lead work.

## Public native API inventory

All types are in `PiSharp.Contracts`. Existing positional constructors/deconstruction remain intact; properties below are nonpositional, immutable record data. Compile-time consumption and old deconstruction are exercised by the transport controls.

| Type | Added surface |
| --- | --- |
| NativeChatAdapter | Closed enum; OpenAICompletions = 1; zero/undefined invalid at producer/persistence admission |
| NativeChatFailureCode | Closed enum: SourceFailed = 1, MalformedStream = 2, UnexpectedEof = 3, ResourceLimit = 4, ProviderError = 5, Cancelled = 6, CleanupFailed = 7, UnsupportedFeature = 8 |
| NativeChatDiagnostic | Positional record `(NativeChatAdapter Adapter, NativeChatFailureCode Code)`; no arbitrary strings, JSON, callbacks or exception objects |
| StreamTerminalEvent | `NativeChatDiagnostic? NativeDiagnostic { get; init; }`; `NativeChatDiagnostic? NativeCleanupDiagnostic { get; init; }` |
| ChatFailure | `NativeChatDiagnostic? NativeDiagnostic { get; init; }` |
| ChatResult | `NativeChatDiagnostic? NativeDiagnostic { get; init; }`; `NativeChatDiagnostic? NativeCleanupDiagnostic { get; init; }` |

The existing public AI `OpenAICompletionsWireFailure` enum remains in its original assembly with unchanged names/numeric values. The provider uses an explicit total mapping to the new closed native domain, rather than casting between enum values. ChatRun validates both incoming primary and cleanup domains before admitting a terminal. Persisting owners must independently validate their boundary; an enum-typed CLR record alone cannot prevent a caller from constructing undefined enum values.

PiWireJson's manual source message/event writers do not serialize these new typed native properties. General native CLR record equality includes nonpositional metadata; copying a record with an updated Message preserves its metadata. The existing TurnResult, AgentLoopTurn and AgentLoopTurnEnded already carry the actual ChatResult, so no agent production edits are required for propagation.

## Classification and ownership

An admitted Completions StreamError preserves its exact fine mapper code, existing StopReason and existing outer ChatFailure.Kind/Message. The outer kind stays Provider for error terminals and Cancelled for Aborted; this unit does not revise the coarse resilience taxonomy or infer a fine mapper code from generic frame-independent ChatRun exceptions. Wrapping an actual terminal carries the same primary metadata through terminal, ChatResult and ChatResult.Failure.

SourceFailed still covers existing wrapper/acquisition/read/preparation faults, including wrapper bounds currently classified as source failures. Mapper/internal limits remain ResourceLimit. Low-level cleanup-only rejection remains CleanupFailed. Owned HTTP physical cleanup-only rejection still turns native completion into SourceFailed/Provider/Error while source semantic success stays separately observable. No invisible retry or effect permission follows from a diagnostic.

A separate NativeCleanupDiagnostic records actual source close/disposal or physical-owner rejection as CleanupFailed. It does not overwrite an existing primary malformed/source/provider failure or cancellation. CompletionsRun.CleanupCompletion.Failure retains the cleanup code separately. Cancellation callback diagnostics preserve their existing safe coarse messages and carry CleanupFailed. ChatRun's existing general post-terminal policy still lets an admitted terminal win; its native-only cleanup observation does not rewrite the settled message or primary classification.

Native completion/terminal remain joined with the actual producer and physical cleanup. Source semantic Result can settle while cleanup is held; it grants no tool or durable authority. Held-close tests require no native terminal/completion before release, then assert the original SourceFailed primary plus distinct CleanupFailed secondary and one physical async close. Indexed native events stay immutable.

Already admitted bounded public failure text retains its source/canonical policy. Ordinary private reader/callback/disposal Exception.Message and provider bodies stay excluded. The existing generic/fixed error strings and bounded public projection are unchanged. Removing the old originated field reduces message/capture size; it does not relax configured response, argument, hook or source-value bounds.

## Verification and integration boundary

The new controls execute all eight real mapper failure causes both directly and through ChatRun; malformed/cancel primary plus actual cleanup rejection; canonical/source held physical cleanup; invalid primary/cleanup enum admission; old constructors/deconstruction and opaque source fields; and actual AgentLoopTurnEnded propagation with zero tool preparation, policy authorization or execution on failed tool content.

All existing transport/low-level compatibility fine-code expectations remain, now asserted at the typed carrier. Message/event source comparators, fixture bytes, locks and the original fourteen-case strict inventory are unchanged. Six exact final-message marker rows are the intended original-wire target; any reduction is established by immutable before/after ordered finding objects, not a new expected file or exclusion.

The integration lead retains durable native diagnostic custom records, backward-read provenance, selected/raw session views, separate native RPC event and versioned CLI diagnostic reports. Combined consumer integration must preserve fine-code checks, no effects on failure, RPC usability and durable ACK/close/separate-process reopen. Original native/full terminal/phase gates remain open until their owners execute and review them.
