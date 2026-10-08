# Genuine finish-turn decision reference

Seven narrow cases execute the exported `runAgentLoop` in unchanged public Pi v0.99.1 at `d86654abb8862e201933517d6f1fce9f88dd117f`. They measure explicit `finishTurn` decisions and their interaction with tool, steering and follow-up scheduling, including error/aborted assistant hard exits. The corpus is `finish-decisions-core`, with provenance kind `captured-upstream-finish-decisions-oracle`. It supplies reference evidence for a bounded native decision seam; it makes no full Agent, session, provider, native-parity or phase-completion claim.

The [input](../../fixtures/pi-v0.99.1/finish-decisions/core.input.json) authors fake model/messages, stream emissions, one non-terminating lookup result where applicable, finish-callback returns and pending queue messages. `end` and `continue` input strings return their corresponding `{action:...}` objects; `undefined` returns JavaScript `undefined`. These are supported `AgentLoopConfig` callbacks passed directly to unchanged source. The real loop performs request context normalization, real argument validation/tool execution, transcript commits and scheduling. The real upstream `AssistantMessageEventStream` carries the authored emissions. No upstream export/module/registry is replaced and no SDK is used.

A disclosed harness `Date.now` override supplies `1700000000000` and is restored in `finally`, following the qualified [full-agent](full-agent-reference.md) and [continuation](loop-continuation-reference.md) environments. Individual message timestamps and tool-call IDs are explicit input data. There is no timestamp/ID normalization. This is unchanged source execution under an authored clock/provider/tool/callback environment, rather than a claim that the whole runtime is unmodified.

## Captured decisions and order

| Case | Requests / completed turns | Tool executions | Steering / follow-up polls | Pending steering / follow-up at exit |
| --- | --- | --- | --- | --- |
| `end-after-tool-before-queues` | 1 / 1 | 1 | 1 / 0 | 1 / 1 |
| `continue-context-only-once` | 2 / 2 | 0 | 4 / 2 | 0 / 0 |
| `continue-satisfied-by-tool` | 2 / 2 | 1 | 4 / 1 | 0 / 0 |
| `continue-satisfied-by-steering` | 2 / 2 | 0 | 3 / 1 | 0 / 0 |
| `continue-satisfied-by-follow-up` | 2 / 2 | 0 | 3 / 2 | 0 / 0 |
| `error-hard-exit-ignores-continue` | 1 / 1 | 0 | 1 / 0 | 1 / 1 |
| `aborted-hard-exit-ignores-continue` | 1 / 1 | 0 | 1 / 0 | 1 / 1 |

Every case emits exactly one `agent_end`. Each first `finishTurn` callback enters an awaited gate after the assistant and any tool-result messages have been committed. At that gate the capture observes exactly one provider request, no `turn_end`, no `agent_end`, only the initial empty steering poll and no follow-up poll. Tool cases already have one executed tool and one committed tool result. Pending inputs are then enqueued at this actual callback barrier; the harness releases it and awaits loop completion. No sleeps or elapsed-time assumptions determine order.

The end case returns `{action:"end"}` after a non-terminating tool result. The actual loop emits `turn_end` and `agent_end` immediately afterward, performs no next-turn preparation/provider request, and leaves both queued messages pending. It overrides the otherwise natural tool continuation and precedes any post-turn queue poll.

The four continue cases return `{action:"continue"}` once, then `undefined` on the second finish callback. They all produce exactly one additional provider request. In the context-only case, request two contains the original system/user messages and first assistant message; no new user message is invented. In the tool case it also contains the committed tool result. In the steering/follow-up cases the corresponding authored user message appears after the first assistant. Natural tool, steering or follow-up scheduling consumes the continuation decision rather than adding a third request. Each performs one `prepareNextTurn`, and that callback runs after the first `turn_end` and before the next `turn_start`.

The error and aborted cases deliver authored upstream stream `error` events whose final assistant `stopReason` is respectively `error` or `aborted`. The real loop invokes and awaits `finishTurn`, records its `continue` result, emits `turn_end` and `agent_end`, and exits with no post-turn queue poll or additional request. The decision is ignored on these response paths.

The aborted case also aborts the supplied controller before pushing its terminal event. Its first request observes a non-aborted signal; the finish callback observes an aborted signal. This measures the low-level source receiving an authored aborted terminal response with the supplied signal already aborted. It does not qualify a real provider/network cancellation, high-level busy-prompt cancellation or native caller-token lifecycle. A native test that uses an `Aborted` response without cancelling its caller token can compare stop-reason scheduling while explicitly leaving signal-state/control and caller-token cancellation unmatched; it cannot claim complete equality with that capture.

## Observations and provenance

The [expected output](../../fixtures/pi-v0.99.1/finish-decisions/core.expected.json) preserves request model/context, awaited events, authored provider-emission snapshots, prepare/convert/finish hooks, tool hooks/execution, queue callback results, gate/abort controls, returned messages, final completed-context messages and remaining queue contents. Snapshots are cloned synchronously at their actual boundary. A unified `order` array references each recorded array by kind/index. Count summaries are derived from these observations, not a separately simulated scheduler or invented expected trace.

Stream options contain functions and an `AbortSignal`; the capture records own property names and relevant credential/reasoning/signal metadata instead of serializing the full object. Finish-return records explicitly distinguish `undefined` from a decision. Other own JavaScript `undefined` fields follow ordinary JSON omission and are not exhaustively measured by this corpus. The finish context snapshot records its message arrays; executable tool functions are represented by the authored input and tool trace rather than serialized objects.

[reference.lock.json](../../fixtures/pi-v0.99.1/finish-decisions/reference.lock.json) pins the new capture script and reused passive loader, offline guard and strict JSON parser. It also pins the exact qualified [full-lock.json](../../tools/PiReferenceRunner/full-lock.json) identity, Node 24.19.0 executable, dependency projection and complete installed dependency trees. The measured runtime closure remains **29 upstream source files and 670 dependency files**, every loaded upstream byte matching its canonical Git blob. `packages/agent/src/types.ts` is additionally pinned as one consulted type-only contract file, not counted as runtime-loaded. Full installed tree verification covers all 1,394 files in `typebox` 1.3.27 and `partial-json` 0.1.7. No dependency was added, installed or updated.

The source checkout's SHA/tree and cleanliness, all qualified source/loader bytes, complete installed tree pins, runtime executable, new harness and input bytes are checked before and after capture. Each child receives a fresh isolated temporary home/workspace under the fixture's transient `.scratch` directory and an explicit environment without inherited credentials. The reused offline guard rejects network and child-process builtins inside capture. This is a trusted-source harness, not an OS sandbox. No provider key, paid provider call, public write or global configuration change is used. The model endpoint is synthetic and never contacted.

The [two raw captures](../../fixtures/pi-v0.99.1/finish-decisions/capture-1.raw.json) ([second](../../fixtures/pi-v0.99.1/finish-decisions/capture-2.raw.json)), [capture report](../../fixtures/pi-v0.99.1/finish-decisions/capture-report.json) and [manifest](../../fixtures/pi-v0.99.1/finish-decisions/manifest.json) are retained within this new fixture directory. Both complete child stdout captures, including loader provenance, were byte-identical. The initial frozen report records that first capture; default reruns print their fresh verification report without rewriting any retained evidence.

## Reproduction and frozen evidence

Run from the implementation repository root with the locked executable:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' tools/PiReferenceRunner/capture-finish-decisions.mjs --oracle 'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-reference-oracle-v0.99.1'
```

The [runner](../../tools/PiReferenceRunner/capture-finish-decisions.mjs) verifies all frozen input/expected/manifest/harness/environment/module and retained raw-capture checksums before running two fresh children. It requires their raw outputs to be byte-identical, then compares actual observations with the frozen expected output using the existing strict raw JSON comparator. Only object key order may differ; arrays, missing/null, strings, timestamps and numeric lexemes remain significant. It rechecks source/dependencies and pins after execution. Transient capture directories are removed only after verifying their resolved paths remain inside the owned fixture scratch directory.

Initial `--capture-new` refuses any preexisting expected output, manifest, lock, raw capture or capture report. Exclusive creation provides an additional race guard. That option was used once successfully for this new fixture, and the frozen outputs have never been rewritten. A prior launch failed to create a process temporary directory before any output was created; the successful capture uses scoped fixture scratch directories. This did not alter upstream source or create an abandoned golden.

Developer verification passed the default two-child comparison and five refusal probes on isolated copies: preexisting outputs, changed input bytes, changed expected bytes, changed capture-harness bytes and changed retained raw-capture bytes. [qualification-report.json](../../fixtures/pi-v0.99.1/finish-decisions/qualification-report.json) records these failures and unchanged original frozen hashes. These are developer integrity checks, not independent Astra acceptance or native implementation tests.

| Frozen evidence | SHA-256 |
| --- | --- |
| `core.input.json` | `815203f5a198cc2ddf7cb1c7d6856adf46ef051127861391af9e711f7c7500ba` |
| `core.expected.json` | `9f63f961256a9af071c90220adbca8861e6c813fcfdfcaa4ab9fffaa37169e23` |
| `manifest.json` | `3e16c936128a66b748c5a7a3821167bd789e496698100563f54a50a1a601dd42` |
| `reference.lock.json` | `82a9b9d0cefb4dcbaf8b489ca97ceb4f1d878354eeae4aeadefe31c04e703044` |
| Both `capture-*.raw.json` | `409a1f0dd0c6dc92828c65a9d748c0c5894cc5789b9381682784f5adaaa4d167` |
| `capture-report.json` | `2e6f51382206e9052c2d46a5263e395e469efb6534fd7d83519e17131e1113c0` |
| `capture-finish-decisions.mjs` | `85d0b477f0dbc0f904133ff0e83c047215be7c89546a348ee4e222b6a234969b` |

The qualified environment-lock digest is `66bd6efa573d9f0a160eb4005e0aba325debdbfdde40c042afab975c4c5caa1a`. The inspected source contracts are pinned [agent-loop.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/agent-loop.ts) (`runAgentLoop`, `runLoop`, transcript/tool execution and result commits) and [types.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/types.ts) (`AgentTurnContext`, `AgentTurnDecision`, `FinishTurn`, public prepare/queue callback contracts).

Reference integration remains parent-owned. No accepted checker, status, previous harness, lock or golden was changed by this capture. Unbounded repeated continue decisions, simultaneous natural queues, long-running prepare callbacks, hook exceptions, actual provider cancellation, high-level Agent queue admission/modes, session settlement and Linux/macOS execution remain unqualified. The native owner must state its selected comparison projection and unmatched lifecycle fields, with independent acceptance handled separately.
