# Native completed-frame differential

**Partial tool-argument parity remains unimplemented.** Upstream `reduceAssistantMessageFrames` repairs partial JSON through `parseStreamingJson`; native `AssistantStreamReducer.Snapshot()` retains the started arguments until the authoritative tool end. The native preview exposes exact unparsed fragments separately. This comparison does not normalize that difference away and does not establish whole-prefix parity.

The native compatibility harness reads the genuine upstream capture committed at `f1c63707b965fa1a30c28f8e18d87b2a7e5e19be`. The capture executed unchanged public Pi v0.99.1 source at `d86654abb8862e201933517d6f1fce9f88dd117f` with authored synthetic events, without provider traffic. Its immutable fixture pins are:

| File | SHA-256 |
| --- | --- |
| `fixtures/pi-v0.99.1/frame/interleaved-signed.input.json` | `986b394b81014fae385688b136049cbe81ef665f478f6e6a72e2f4784f301729` |
| `fixtures/pi-v0.99.1/frame/interleaved-signed.expected.json` | `8ed97e0a7779566e94321e2b46306061c24a10440ad3f1c7903e0409260712a4` |

The native test verifies those byte hashes, fixture/source identities and capture kind before replay. It does not execute Node, recapture a golden, edit fixtures or copy an upstream terminal message into the native reducer.

## Explicit replay mapping

The captured `observations.frames` are genuine outputs of the unchanged upstream encoder. `PiWireJson.ReadEvent` maps them to native events with the same indices; there is no native encoder claim. Frame zero starts from the authored initial message. Each subsequent frame is checked against the corresponding authored operation's type and content index, preserving interleaving rather than grouping by content kind.

| Captured compact frame | Native operation |
| --- | --- |
| `start` | `StreamStarted` with the full initial envelope |
| `thinking_start/delta/end`, index 0 | `ThinkingStarted/Delta/Ended` |
| `text_start/delta/end`, index 1 | `TextStarted/Delta/Ended` |
| `toolcall_start`, index 2 | `ToolCallStarted` with started identity/arguments/opaque fields |
| Two `toolcall_delta` frames at positions 6 and 8, index 2 | `ToolCallDelta`; raw preview equals accumulated authored fragments |
| Flattened `toolcall_end`, index 2 | `ToolCallEnded`, preserving final arguments, thought signature and namespace |

The fixture produces one compact frame per authored operation plus start. It contains no `toolcall_checkpoint` and does not qualify checkpoint handling. The terminal emission produces no compact frame. Prefix entries are matched by `afterEmissionIndex` and checked against `frameCount`. At each block end, the test compares that completed block with the corresponding upstream prefix reduction, even while another block is still active.

After all ends, the full native pending snapshot is compared with captured `observations.reducedMessage`. The native terminal is independently built from that snapshot with `toolUse`, the explicit terminal choice made by this capture's source harness. It is applied as `StreamDone`; the entire serialized native final message is compared with captured `observations.terminalResult`. This fixture's usage/cost fields remain zero and its envelope is unchanged; broader terminal metadata updates are not covered.

## Comparison boundary and evidence

`PiWireJson` serializes completed native blocks and full messages. Comparison ignores only object-property order. It preserves array order, decoded strings including CRLF and Unicode, opaque signatures/namespaces, explicit null versus missing fields, numeric token spelling and all message fields. Mutation checks prove changes to signatures, namespace, null presence, array order, redaction, CRLF, response ID, timestamp, stop reason, usage and numeric tokens fail comparison.

Both tool progress prefixes are recorded as an explicit known difference: upstream exposes repaired/parsed arguments while native arguments remain `{}`. Exact raw preview accumulation is asserted separately. Only completed blocks and the final message are claimed to agree for this one synthetic captured scenario. No partial repair, provider wire behavior, encoder equivalence, cross-platform qualification or phase closure is claimed.

Run `./tools/test-native.ps1` for the existing offline .NET 10 harness. It restores with empty package sources, builds framework-only projects and runs native tests with Node absent from PATH. The two added checks are `differential.frame.interleaved-signed-completed-blocks-and-final` and `differential.frame.comparison-preserves-signatures-null-order-and-fields`. The detailed native artifact is `artifacts/native/frame-interleaved-signed.actual.json`; the main native report also marks the limited comparison scope and partial-argument difference.

See [the frozen manifest](../../fixtures/pi-v0.99.1/frame/manifest.json) and [capture dependency/source lock](../../tools/PiReferenceRunner/frame-lock.json). The primary public implementations are [the pinned frame encoder/reducer](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/utils/assistant-message-frame.ts) and [partial JSON parsing](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/utils/json-parse.ts).
