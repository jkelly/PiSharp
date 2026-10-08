# Pi Messages 12db qualification reporting

Base: `12db5b480f0acb759a377ace43a228b798e7319c`. This separate reporting correction changes no production source, runtime candidate, frozen authored fixture, criterion dependency, tool-effect expectation, or phase gate. Native builds/tests are reserved for the coordinator; this worker executed none.

## Preserved actual evidence

The original Pi Messages report at `task-3/PiSharp-repaired-companion-compile-retry/artifacts/companions-12db5b48-SelectedSeven/pi-messages.json` remains unchanged (SHA256 `ee71d1fc750c7a508f72cabab37a500e5fc34de885a0919270fd5542dda2d14f`). The original coordinator outcomes JSON remains unchanged (SHA256 `9051c0b2232ce1910a3376c639f4f0455331045818a7845af766d632bbcce469`). A separate retrospective index accompanies the handoff; it is interpretation of existing evidence, never a fresh execution receipt.

`PM-NATIVE-TOOL-CONTINUATION` has zero failed groups; criteria /1 and /2 complete, /3 OPEN for Agent/ToolInvoker orchestration. Its old exception came from requiring OPEN dependencies to be COMPLETED. `PM-TOOL-IDENTITY-REPLACEMENT` has no exception or failed groups and complete named criteria, but its frozen `domainGap: true` means native rejection does not establish source parity. Both remain nonpassing qualification outcomes.

## Corrected report contract

The ledger still derives every frozen named criterion from the same constituent groups and required variants. It returns the variant dependency status instead of throwing for OPEN. Real assertion faults and runtime/ownership exceptions remain behavioral FAIL, including a failed group coexisting with an OPEN criterion. OPEN, incomplete and unexecuted checks cannot earn a passing qualification outcome.

The report's `failures` counts behavioral failures. `openQualificationOutcomes` and `unexecutedOutcomes` are separate. `outcomeIndex` includes every variant with its category, behavior, qualification, nonpassing flag and domain-gap flag, so an OPEN row with no exception is retained. Detailed assertions, actual observations and frozen input/expected rows remain present. Source qualification and all eight phase gates remain OPEN. With the two observed gaps, zero behavioral failures still yields a nonzero process exit.

Authored reporting controls exercise OPEN with zero failed groups and retained named criteria, a domain gap after completed checks, FAIL precedence over OPEN/domain gaps, runtime failure retention, incomplete and unexecuted classification. They remain unexecuted and confer no source differential acceptance.

## Terminal tool identity replacement: exact shared-contract need

Pinned Pi `d86654abb8862e201933517d6f1fce9f88dd117f`, `packages/ai/src/api/pi-messages.ts`, `createEventConverter` handles `toolcall_end` by `Object.assign(partial.content[event.contentIndex]!, event.toolCall)`. This permits final ID/name replacement. The native provider currently rejects replacement in `PiMessagesEventMapper.cs` before emitting the final event.

Existing safe contracts cannot represent that behavior end to end: `src/PiSharp.AI/Streaming/AssistantStreamReducer.cs` rejects ID/name changes in `ToolCallEnded`. `ToolCallHeaderUpdated` only fills empty provisional identity, so it cannot replace an existing nonempty ID/name. Both `PiMessagesEventMapper` and `src/PiSharp.AI/Streaming/ChatRun.cs` independently apply that reducer. Removing the provider guard, changing only its private reducer, or hiding the original start would either still fail the outer consumer or change the observed source event sequence.

Proposed bounded shared change for the owning lane: add an explicit opt-in final identity-replacement semantic to the existing end event/reducer contract, authorized only for Pi Messages and propagated through both reducer instances. Default behavior for every other provider must retain immutable identity. Final replacement must require a live matching content index, valid nonempty final identity, strict final object arguments, duplicate final-ID rejection, unchanged bounds, and immutable earlier progress snapshots. The subsequent successful terminal must agree with the finalized end content. No mapper/parser/progress callback may execute a tool.

Agent execution must continue to obtain the final call from the successfully settled assistant message, then apply existing schema/exposure/action policy through the normal ToolInvoker path. Required qualification covers changed ID alone, changed name alone, both changed, duplicate final IDs, invalid identity/arguments, post-end mutation, final terminal disagreement, cancellation/cleanup failure, and zero effect before final validated identity plus successful terminal/policy admission. Frozen identity-rejection expectations remain intact until a separately owned successor expectation set is authored and reviewed.

No shared streaming, Agent, event-contract or provider runtime file is edited here. Terminal identity replacement therefore remains OPEN pending that shared ownership decision and qualification.
