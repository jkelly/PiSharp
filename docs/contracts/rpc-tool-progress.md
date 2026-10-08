# Awaited RPC tool progress

This prerequisite projects actual native `ToolExecutionUpdated` observations through the public `RpcSessionDispatcher`, its real `PersistentAgentSession` subscription and the existing awaited `JsonlWriter`. It targets public Pi v0.99.1 at `d86654abb8862e201933517d6f1fce9f88dd117f`. It adds no command, durable message kind, process implementation, projector visibility or independent output queue. Complete RPC/progress parity remains open until a qualified whole-source oracle comparison.

## Wire value

An admitted native update emits one object with these fields:

```json
{
  "type": "tool_execution_update",
  "toolCallId": "rpc-call",
  "toolName": "read",
  "args": { "path": "/authored", "scale": 1.0 },
  "partialResult": {
    "content": [{ "type": "text", "text": "partial excerpt" }],
    "details": { "scale": 1.0 },
    "structuredContent": { "output": "partial excerpt" }
  }
}
```

The tool-call ID, name and raw arguments come from the update's original `ToolInvocation`. `partialResult` uses the existing bounded execution-result projection: native text content, owned details and optional owned structured content. Missing `StructuredContent` omits the property; `JsonData.Null` emits explicit JSON null. Retained valid raw metadata, argument and details numeric tokens are preserved. Each update is a complete partial result, independently of earlier updates and the final result.

The existing [JSONL encoder](../../src/PiSharp.Rpc/JsonlTransportOptions.cs) strictly validates the retained record and removes JSON whitespace outside quoted strings before writing. Wire metadata therefore uses compact object syntax while retaining every property in its original order, explicit nulls, opaque values and raw numeric tokens such as `1.0` and `9007199254740993`. The encoder does not reserialize those numbers through floating-point values or mutate the owned native `JsonData`. Native structured-result admission and the actual session observation retain the same owned value and its original insignificant whitespace. The wire test compares a complete explicitly compacted expected object and separately checks native identity/original text before and after delivery.

This admitted result profile excludes native `Terminate`, `Failure` and `IsError` fields from the result object, as the existing execution-end result writer does. The execution-end event continues to carry its existing top-level `isError`. This narrow projection does not certify source image/usage/optional-result fields or arbitrary partial-patch behavior; those remain required dependencies of whole-product qualification.

Both the inner result and the composed outer event are charged against `RpcDispatchOptions.MaximumOutputBytes`, including UTF-8 encoding and JSON escaping. Oversized projection fails before a record is written. The RPC cap remains independent of the invoker's ordinary and structured-result character budgets: a valid native partial may exceed the host's configured RPC output cap. No output cap is raised here.

The actual `ToolInvoker` normalizes partial results before the RPC observer receives them, including the accepted strict retained-raw structured JSON reparse and finite-number, depth, Unicode and separate size admission. Generic trusted scheduler executors retain the scheduler's existing structural checks. This projection does not widen admission or add a strict-raw guarantee to unrelated details/arguments boundaries.

## Awaited delivery and durability

The existing scheduler callback scope, bounded progress/end channel and delivery acknowledgement remain authoritative. The dispatcher awaits the transferred writer's complete record write and stream flush inside that callback. A slow progress flush therefore holds the reporting adapter, subsequent awaited reports, final execution settlement and the final tool-message checkpoint. Concurrent RPC replies use the same existing publication gate.

Execution updates produce no `ToolResultMessage`, timestamp, message-start/end pair or session entry. The coordinator commits only final message observations. Final tool messages still pass through the existing durable checkpoint before their acknowledged RPC message pair. Progress text/details/structured values are absent from canonical messages, `get_messages`, `get_entries`, turn/agent message arrays and the next actual provider request. Final structured output remains event-only under the previously accepted structured-result contract.

Output write/flush failures retain the dispatcher's existing sanitized `OutputFailed` fatal path; inner or composed projection overflow retains `ResourceLimit`. Fatal cleanup closes admission, cancels and joins owned execution, and awaits session/output disposal. It produces no ordinary final tool acknowledgement or normal `agent_settled` after the fault. A thrown tool exception or rejected structured partial instead becomes the invoker's bounded execution error and can settle through ordinary final messages. Neither case promotes the most recent partial into a final result.

An explicit RPC `abort` requests actual invocation cancellation and waits for protocol settlement. It does not interrupt an already admitted progress record's output by substituting the invocation token for the writer's ownership token. A held stream flush must complete or fail; afterward abort still joins tool cleanup before its response. Captured invocation callbacks reject later reports after settlement and cannot mutate RPC output. Supplied adapters/streams must cooperate or finish; no timeout, forced cleanup or detached publication is introduced by this slice.

## Pinned source and authored evidence

The pinned [Agent event union](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/types.ts#L528) declares `tool_execution_update` with `toolCallId`, `toolName`, `args` and `partialResult`. The pinned [update emitter](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/agent-loop.ts#L778) uses the original tool call. [toJsonEvent](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/modes/json-event.ts#L49) forwards non-`message_update` events unchanged, and [RPC subscription](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/modes/rpc/rpc-mode.ts#L355) outputs that projection. These source files were read directly from the pinned public checkout. Native awaited/bounded callback choices and remaining source lifecycle differences are declared in [tool-progress.md](tool-progress.md).

`RpcSessionDispatcherTests.Cases()` adds three authored public-path groups using the existing durable-session, scripted-transport, captured-stream and checkpoint helpers:

1. A held actual update flush blocks the tool and final checkpoint; three updates exercise original correlation, the complete compact wire metadata object/property order/null/raw numeric controls, native metadata ownership/original text, absent versus explicit-null structured content, bounded result profile, final ordering and complete model/durable exclusion.
2. Update write and flush faults plus separate inner-result/composed-event byte overflows propagate the existing fatal classifications only after gated owned tool cleanup, dispose the owned coordinator/output and retain a single prompt-acceptance response.
3. Thrown execution errors and permissive `FromElement` metadata rejection settle without partial promotion, reject captured late callbacks, and explicit abort waits for an admitted update flush plus actual tool cleanup before acknowledging settlement.

The projector remains internal and is exercised only through public dispatch/coordinator/tool execution. No source or golden corpus is changed or substituted. At this correction freeze these test edits are **NOT RUN** by the author; the parent owns isolated execution, review, immutable commit and any acceptance claims. Ordinary output NUL support, direct RPC bash commands, source rich partial results and a qualified whole-source progress oracle remain outside this prerequisite.
