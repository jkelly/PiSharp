# Genuine loop continuation reference

Two narrow `runAgentLoop` cases are captured from unchanged public Pi v0.99.1 source at `d86654abb8862e201933517d6f1fce9f88dd117f`: automatic continuation from a non-terminating tool result to text, and steering delivery before queued follow-up delivery. These are low-level callback observations. They do not qualify the high-level `Agent` queue implementations, native Agent behavior, production providers or any full phase gate.

The [authored input](../../fixtures/pi-v0.99.1/loop-continuation/core.input.json) supplies a fake model, explicit assistant responses, one gated lookup tool per case, queued messages, fixed message timestamps and tool-call IDs. The real loop declares tools on the authored system message, normalizes request context, validates tool arguments, runs hooks, commits results and selects subsequent requests. The real upstream `AssistantMessageEventStream` carries the fake emissions. Neither an upstream source file nor an export, provider module or registry is replaced.

A disclosed harness `Date.now` override supplies `1700000000000` and is restored in `finally`, following the separately qualified [awaited-loop harness](full-agent-reference.md). The authored message timestamps remain distinct from that tool-result clock. This is unchanged source execution under an authored clock/provider/tool/callback environment. There is no timestamp normalization, source transform, SDK shim, new dependency or claim that the whole runtime is unmodified.

The [frozen expected output](../../fixtures/pi-v0.99.1/loop-continuation/core.expected.json) contains prepared request model/context objects, awaited agent events, provider emission snapshots, tool and lifecycle hooks, queue callback results, gate controls, returned messages and the last completed transcript snapshot. Each snapshot is cloned synchronously at its actual boundary. The `order` array references these separate arrays by kind/index so their relative order can be inspected. Stream options include functions and an AbortSignal; the capture records their own property names and relevant credential/reasoning/signal metadata rather than serializing the complete options object. Own JavaScript `undefined` fields are omitted by JSON serialization and are not separately measured in this corpus.

The captured observations are:

| Case | Provider requests / completed turns | Tool executions | Steering polls | Follow-up polls | `agent_end` events |
| --- | --- | --- | --- | --- | --- |
| `automatic-tool-to-text` | 2 / 2 | 1 | 4, all empty | 1, empty | 1 |
| `steering-before-follow-up` | 3 / 3 | 1 | 4, one delivering steering | 2, first delivering follow-up | 1 |

In both cases, an awaited first assistant `message_end` blocks tool preflight/execution and keeps the provider request count at one. The harness releases that barrier, awaits the tool's start gate, then releases tool completion. At the tool gate, only the initial empty steering poll has occurred and no follow-up poll has occurred. These gates establish order without sleeps or reliance on elapsed time.

In the automatic case, the second prepared request contains the system/user messages, completed tool-call assistant and committed tool result. `finishTurn` returns no decision; the actual tool result has `terminate: false`, so continuation is selected by the unchanged loop. The second assistant response is text and the run ends. The four steering polls include the extra poll after `prepareNextTurn` when the earlier poll was empty.

In the queued case, steering and follow-up are enqueued while the first assistant barrier is awaited. The loop completes the current tool before polling steering again. Request two contains the tool result followed by the steering user message; it contains no follow-up message. After the second text response, an empty steering poll allows the first follow-up poll to deliver the queued recap request. Request three contains that follow-up after the second assistant. Its final text response is followed by empty steering and follow-up polls and one `agent_end`. The two nonempty pending-message transitions do not trigger an extra steering poll inside `prepareNextTurn`.

The fake queue callbacks drain their authored arrays with `splice(0)` when the real loop calls them. This measures the public low-level callback seam and callback order; it does not test `Agent.steer`, `Agent.followUp`, busy-prompt admission, one-at-a-time versus all-message drain modes, cancellation, explicit turn decisions or steering arriving during long-running preparation. Those scenarios remain open.

[loop-continuation-lock.json](../../tools/PiReferenceRunner/loop-continuation-lock.json) pins the capture scripts, reused passive loader/offline guard/strict JSON parser, runtime, dependency projection, complete installed dependency tree hashes and the qualified [full-lock.json](../../tools/PiReferenceRunner/full-lock.json) identity. The measured runtime closure is **29 upstream source files and 670 dependency files**. Every loaded upstream file is compared with its canonical Git blob. The separately consulted `packages/agent/src/types.ts` is pinned as one type-only contract file rather than falsely counted as runtime-loaded. Installed package tree verification covers all 1,394 files in `typebox` 1.3.27 and `partial-json` 0.1.7. Source cleanliness, pinned SHA/tree, complete dependency trees and harness/input bytes are checked before and after capture.

Each child receives a fresh temporary home/workspace and an explicit environment without inherited credentials. The reused offline guard blocks network and child-process builtins inside capture. This trusted-source harness is not an OS sandbox. No provider request reaches a network endpoint, and no installation, provider key, paid call, public write or global configuration change is performed.

Run from the repository root with the locked Node executable:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' tools/PiReferenceRunner/loop-continuation-run.mjs --oracle 'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-reference-oracle-v0.99.1'
```

The [runner](../../tools/PiReferenceRunner/loop-continuation-run.mjs) executes two fresh children and requires their complete capture JSON stdout to be byte-identical. Default mode verifies immutable harness/environment/input/expected pins before execution and compares the result with the golden using the existing strict raw JSON comparator. Only object key order may differ; arrays, missing/null, strings, timestamps and numeric lexemes remain significant. Initial `--capture-new` refuses an existing golden, lock or manifest, and exclusive file creation adds an overwrite guard. It was used once for this fixture; the frozen files have not been rewritten.

Ignored outputs under `artifacts/loop-continuation-reference/` retain both raw captures, `core.actual.json`, the default repeatability report and `qualification-report.json`. Developer verification passed the default two-run comparison and four refusal probes: existing-golden creation, changed input bytes, changed expected bytes and changed capture-harness bytes. Mutation probes operate only on isolated ignored copies, and all four frozen file hashes remained unchanged.

| Frozen file | SHA-256 |
| --- | --- |
| `core.input.json` | `073669bd54ccc32b8b3a69e8a3099cc293f198b3a55c01602344cffcb2c6f34e` |
| `core.expected.json` | `f714291fd9fb555e4932d4e8daefbf2842ed24380a33d3039efe7f6c8dd1bc2d` |
| `manifest.json` | `0e0754c987f9ce40151224a3ef7bbd7eab7a2e4d45ff6f3c1d9e6ba71f2e2d66` |
| `loop-continuation-lock.json` | `7c9275a59939f5505c41a340c088e766c606f426c375180fe78f0f8c653fd283` |

The provenance kind is `captured-upstream-loop-continuation-oracle`. The lead registered its [separate manifest](../../fixtures/pi-v0.99.1/loop-continuation/manifest.json) explicitly in the recursive checker after a fresh two-run comparison passed. The expanded checker passed 546 integrity checks across seven manifests and thirteen fixture groups; all twenty tooling tests passed. Existing capture harnesses, locks and goldens remain unchanged. These tooling counts do not constitute independent Astra acceptance or native differential parity.

Source contracts inspected locally were the pinned [agent-loop.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/agent-loop.ts) (`runAgentLoop`, `runLoop`, `declareToolChanges`, `streamAssistantResponse`, tool-result construction) and [types.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/types.ts) (`AgentLoopConfig` and public callback contracts).
