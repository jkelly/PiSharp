# Native context and context_with_system request hooks

This is an authored, unbuilt and unexecuted successor to `ad66f900c8eaa217da9b04a7e556189054be1222`. It implements one bounded original SDK hook. Independent review, native execution and genuine source differential acceptance remain pending. All original phase gates remain open.

## Pinned source and chosen scope

The target is Pi v0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`. The existing [native surface inventory](../../compatibility/extensions/native-surface.json) retains the context event declarations and runner behavior. The exact local release archive was inspected again: SHA-256 `4d99d3c9ed6db41f88ce7ba36d478b06a9386f4e81fa0c1ea3f93e681c99e83b`.

[ExtensionRunner.emitContext](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/extensions/runner.ts) runs conversation-only `context` handlers first, then `context_with_system` handlers over the full transcript. For the second stage, replacement messages become the request context; removing the leading system message is reported but honored. Handler exceptions report an error and continue. [The types](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/extensions/types.ts) expose an optional `messages` result.

The successor of `c660cc2aaffaa1b98f74697ab6cb0e8d8973f52d` adds conversation-only `context` and actual CLI admission. The bounded [before_agent_start subset](native-before-agent-start.md) is implemented in a separate successor commit.

Conversation handlers receive no system messages. Returning the same message identities in the same order preserves all original system positions. A changed list restores the current system head through the host-supplied `RestoreSystemMessage` callback. The CLI supplies `SessionSystemReplay`, including accumulated content, section updates/deletions and tool additions/removals. This follows `sameMessages`, `restoreSystemMessages`, and `getCurrentSystemMessage` in the pinned runner/transcript source.

## Native composition

Extensions call `RegisterContextHandler` or `RegisterContextWithSystemHandler` with an `ExtensionContextWithSystemHandlerDescriptor`. A handler receives `ExtensionContextWithSystemEvent`, containing immutable `TranscriptEntry` messages. Return null or a patch with absent `Messages` to retain the current list. Return an initialized empty array to remove all request messages. Return a replacement array to reorder, drop, add or replace supported native messages. There is no in-place mutation surface.

Create `ExtensionAgentBinding` and supply `binding.Hooks` as `AgentConfiguration.Hooks`, or as `SessionModelBinding.Hooks` for durable native sessions. Hosts with other hooks can retain them using `existingHooks with { TransformRequestMessages = binding.Hooks.TransformRequestMessages }`.

`AgentHooks.TransformRequestMessages` is a separate explicit seam after `PrepareRequest` has passed its existing fixed-model, canonical-transcript check. The new list is validated and used only for `ChatRequest.Messages`. Neither the loop's transcript nor the durable session projector is replaced. The callback runs inside normal Agent callback ownership, so cancellation, disposal and self-wait checks still apply. Model selection and final tool-action authorization are unchanged.

The registry atomically snapshots and leases each phase separately for each request. All conversation handlers run before the full-context phase, regardless of cross-phase registration order. Registrations added in the first phase are visible to the second phase. Owner order and per-owner registration order are retained. Removal during a dispatch does not revoke already admitted callbacks; removal and addition affect subsequent requests. Cancellation links operation, session and extension lifetimes, and actual callback/UI cleanup is joined before leases are released.

The typed reducer rejects default arrays, malformed supported message envelopes and excessive aggregate JSON. Rejected patches retain the preceding list and produce an `InvalidResult` diagnostic; callback failures produce `HandlerFailed`. Removing a leading system message produces `LeadingSystemRemoved` while accepting the replacement. Diagnostics use the existing native `ReportEventDiagnostic` channel after reduction; exact upstream error-emitter timing is not claimed. The native envelope supports system/user, finalized assistant and admitted toolResult messages; the successor also supports validated custom messages before provider conversion. In-place JavaScript mutations remain outside this slice.

## Ownership boundary

The CLI now admits exactly `ContextHandler` and `ContextWithSystemHandler` in its existing closed registration-kind list and passes `binding.Hooks` to its model binding. Existing package approval and final tool authorization remain in force. Provider transports and terminal UI are unchanged.

## Authored evidence

Eight `context-with-system.*` groups are directly awaited by the contract-test harness, without an outer timeout that could detach owners:

- Conversation identity preservation and changed-list system/tool-state replay.
- Phase ordering and live second-phase registration snapshots.
- Two native owners transform two real Agent requests while original durable bytes and reopened history remain intact.
- An admitted handler removed by its predecessor still runs once; a later registration starts on the next request.
- Malformed user/system content is rejected before transport, retaining prior context (review correction).
- Exceptions, default arrays and mismatched roles diagnose and continue; an empty replacement is honored.
- Operation, session and owner cancellation in both phases wait for held callback cleanup and make no transport request.
- Legacy PrepareRequest rewriting is still rejected before the new transform runs.

An additional directly awaited `native request-context` CLI fixture test drives the actual approved zero-tool plugin and offline HTTP request path, checks the transformed provider input, preserves the original durable user text and existing bytes, and checks callback order and package snapshot cleanup.

These are authored tests, not passing execution evidence. Required next checks are compilation, the focused contract groups, existing Agent request/queue/recovery tests and registered reducer/ownership suites, under an authorized bounded test window.
