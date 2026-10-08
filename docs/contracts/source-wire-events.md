# Explicit Pi source event projection

This explicit API targets the `AssistantMessageEvent` union in [Pi v0.99.1 types.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/types.ts). It adds `PiWireJson.WriteSourceEvent` and `ReadSourceEvent`; it does not change the compact native `WriteEvent`/`ReadEvent` contract or the immutable native event model.

The caller supplies the actual immutable emission partial for every nonterminal event. The API does not reconstruct a partial from a delta, consult provider-specific sidecars, or invent an empty tool identity. Starts contain `type`, `contentIndex`, and `partial`; their text/thinking/tool payload belongs to the indexed partial content. Tool-call ends contain the nested `toolCall` object. Thinking-end signatures live inside the partial's thinking block, rather than adding a top-level event field. Done/error use the source terminal fields and reason sets, with no partial.

The source reader returns the owned partial separately and recovers typed native start content from its indexed block. It carries an end signature into the native reducer's existing event metadata. Nested tool metadata stays on the tool, distinct from unknown event metadata. Explicit null and unknown JSON fields remain owned values. Wrong slot kinds/indices, contradictory end/start payloads, reserved shape fields, invalid terminal reasons and native-only checkpoint/provisional frames are rejected. These consistency checks are a bounded native admission policy, not a claim to reproduce arbitrary malformed JavaScript values.

Four authored regression groups cover all twelve source event alternatives with exact field sets and source roundtrips; literal source starts and ownership; inconsistent/malformed/native-only refusal; and preservation of the compact native API. The cases are source-derived expectations, not newly captured goldens. These groups were unexecuted at the original source freeze; later bounded execution is recorded separately below.

The base paired Anthropic capture used the compact writer. A later isolated composition records actual emission snapshots alongside compact wire; its bounded coverage is described below. This slice does not reproduce mutable JavaScript alias identity, change decimal costs or cancellation policy, repair the upstream split-CRLF parser, or certify complete provider parity.

## Caller and snapshot inventory at base 557115ed

No existing production `src` caller invokes `PiWireJson.WriteEvent` or `ReadEvent`; those public APIs are directly available to consumers. Existing repository call sites are compatibility capture/roundtrip tests, Azure/Google diagnostics, Pi Messages ownership tests, and transport differential tests. They remain compact, including Compatibility Program's frame reader and snapshot writer. This addition does not silently change any existing public call.

The external Task5 `AnthropicStreamCapture.Frame` writes compact `wire` both when draining each frame and later over retained frame references. The isolated hook-capture successor keeps that behavior. Migration must record a genuine partial at each emission and pass it explicitly to the new API; late immutable references cannot reproduce upstream mutable alias views. Retain the existing compact observation alongside a separately named source projection until its comparison is reviewed.

`AssistantStreamReducer.Snapshot()` supplies accumulated native content but retains start-envelope usage until settlement and does not project partially parsed tool arguments from `GetToolJsonPreview`. It is therefore not sufficient evidence of the actual upstream-shaped Anthropic partial at every push. At base 557115ed the Anthropic path supplied no `SourceEmissionSnapshot`. The later opt-in mapper seam supplies owned emission snapshots; unsupported preview remains explicit absence. The reducer alone still cannot substitute for that seam.

Other sidecars are not interchangeable: Mistral Conversations stores a message; Pi Messages stores a `{value, ownUndefinedPaths}` envelope; OpenAI Completions retains protocol-specific emission/raw views and exposes `CompletionsRun.ReadSourceEventsAsync`. Those paths remain unchanged and must not be routed through this API by reinterpreting their JSON indiscriminately. No other source protocol caller is migrated by the Anthropic capture composition, and the new API alone establishes no end-to-end provider parity.
## Later bounded execution

See [provider source-projection evidence](../verification/provider-source-projection.md) for the later candidate-specific observation and acceptance status. Historical source-only statements above describe their original freeze, not a claim about every later composition.
