# Genuine whole-module session context reference

This corpus captures the unchanged public `buildSessionProjection` and `buildSessionContext` exports from the **whole** `packages/coding-agent/src/core/session-manager.ts` at Pi commit `d86654abb8862e201933517d6f1fce9f88dd117f`. It separately calls the unchanged public `convertToLlm` from `messages.ts`. No private function is extracted, source import removed, module replaced or source transformed. Node's native TypeScript stripping and the canonical upstream source resolver load the original modules.

The [input](../../fixtures/pi-v0.99.1/session-context/core.input.json) is one authored 28-entry v3 forest with four explicit leaf selections. The [golden](../../fixtures/pi-v0.99.1/session-context/core.expected.json) contains genuinely observed return values from two fresh offline children with byte-identical outputs, SHA-256 `27926944c7f0cf5cdf1f65188dcce0cf14132b932e8784af65796c723f516a09`. [Manifest](../../fixtures/pi-v0.99.1/session-context/manifest.json) and [lock](../../fixtures/pi-v0.99.1/session-context/oracle.lock.json) retain provenance and exact input/output/harness/runtime/source/receipt/archive/installed-package pins. Expected output was neither authored nor copied from a native implementation.

## Observed four-case boundary

| Case | Genuine observed context |
| --- | --- |
| `selected-left` | Left ancestry only; system/tool declaration history, user/assistant/tool result and hidden-display custom context. Thinking `low`, model `openai/assistant-left`. Custom state, usage, label, session information and unknown outer record contribute no messages. |
| `selected-right` | Right ancestry only; thinking `high`, model `foreign-provider/assistant-right`. Custom string content contributes runtime `custom`, then a user text block in the separate LLM view. Unknown stored role `futureRuntime` remains runtime data and contributes nothing to LLM conversion. |
| `explicit-null-leaf` | Empty projection/messages/LLM view, thinking `off`, model null. |
| `compacted-left-with-edits` | Checkpoint system and compaction summary first, retained left range, surviving edits, branch summary and recorded bash values. Latest user edit wins; tool-result string replacement becomes one text block; null edit omits the custom contribution. |

Each observed case holds `projection`, `context` and `llmMessages` separately. `projection.entries` preserves each `sourceEntry` and its projected contribution, including state-only or omitted entries with empty messages. It is the source's active compaction-aware list, not a newly invented ancestry format. The context is the canonical projection's message list plus settings; the LLM view is a distinct public conversion. The source input remains structurally unchanged after all calls.

Ordinary system/user/assistant/tool-result objects retain opaque nulls, ordered arrays, Unicode/CRLF text, image data, signatures, namespaces and usage. Assistant history updates selected model settings after explicit model-change entries; accounting-only usage entries do not change the model. Left custom `display:false` still contributes context. Its explicit null details remain null at runtime and are omitted by LLM conversion. The right custom entry omits details: the genuine helper creates an own JavaScript `details:undefined` property. JSON serialization omits it, and `ownUndefinedPaths` separately records both projection locations and the context location. No missing property is converted to null.

The advanced case's checkpoint supplies a complete authored system/tool message. Retention begins at `left-user`; older leading system history is omitted. Surviving context edits before and after compaction are selected by target, with the later user replacement winning. Tool result identity/timestamp/opaque metadata survive its content replacement. The custom target's raw source entry stays present while its contribution becomes empty. Settings still reflect the complete selected source path. Compaction and branch summaries retain their runtime roles; separate LLM conversion applies the exact source prefixes and authored timestamps.

Stored bash records are **authored data**. Visible output converts to the source's user text, including exit-code and truncated-output notices. The record with `excludeFromContext:true` remains in runtime context but is excluded from LLM messages. Its command and logical output path are never executed or dereferenced. No shell, file tool, provider, session constructor, persistence API, migration or summary-generation call runs.

## Qualified environment and exact closure

The fresh `Pi-session-context-oracle-v0.99.1` is separate from both older accepted oracles. The reviewed [setup plan](../../compatibility/session-context-oracle-plan.json) pins exactly cross-spawn 7.0.6, isexe 2.0.0, partial-json 0.1.7, path-key 3.1.1, shebang-command 2.0.0, shebang-regex 3.0.0, typebox 1.3.27 and which 2.0.2. All **1,431 installed files** match their inspected, SHA-512 SRI-verified package archives. Root MIT/ISC license text and individual license hashes are retained in the restored receipt and referenced by the capture lock; registry signatures/attestations and broader distribution review remain separate checks.

The actual loaded-file observer records **33 unchanged upstream files and 682 dependency files**: 668 typebox, six cross-spawn, two isexe, two partial-json, and one each path-key, shebang-command, shebang-regex and which. These counts are observed loader-file counts; complete installed package trees and all tracked source/config/manifest bytes are also verified independently. No SDK or ancestor/global dependency fallback is admitted.

`session-manager.ts` is 63,809 bytes, SHA-256 `450d82c529933214e088b8422f00061815e617f352f6c834689787a314064bff`; `messages.ts` is 5,206 bytes, SHA-256 `5397e3c96c9504266e4ee8b6e88d5098b3280ee7dbb0c687b74708c62a06c8d4`. Every loaded upstream file matches its canonical Git blob. Before and after capture, read-only setup validation checks clean pinned source, all 2,093 canonical source files and both precise declared Windows CRLF checkout conversions, complete archive/installed trees, projected lock/manifest, npm bootstrap, setup receipt and harness bytes. Canonical source fingerprint is `2d65bfaee0e2556cb82ae7e0425de68560ecc3be4451f69ceff3bcc9c6144aa3`.

Node v24.19.0 is pinned by absolute executable SHA-256 `3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237`. The setup helper is pinned as `285cb9e70726e19e38dc318a40137708ae65e3ae33e893ea4775e6f169168bdc`; its restored receipt is pinned as `3e43ae32f145a1cb62d277deda4659d91e78fd49bb0615f88b06fa9c8f2c7f61`. That historical receipt correctly says whole-module qualification was pending at setup time. This new capture supplies load/return observations without rewriting the receipt.

Existing full preload/offline guard and the unchanged resolver are reused. An additional resolve hook forwards normal resolution and admits only this oracle's source and eight packages. Real fetch/HTTP/socket/DNS and child-process calls are blocked in capture children. Each child has a fresh owned task-local home/workspace and an explicit credential-free environment. Temporary cleanup verifies the resolved target lies below its owned scratch root before recursive deletion. Source config can read its pinned package manifest during import; no source-session filesystem operation is invoked.

Dates and IDs are explicit authored data. **Neither `Date`, `Date.now` nor RNG APIs are replaced.** Snapshots are taken immediately after each synchronous return; no source stream/emission is invented. The raw comparator rejects duplicate object keys and preserves number lexemes, strings, null, missing fields, arrays and opaque data, ignoring only object-key order. Numbers already materialized by JavaScript retain its precision limits; this corpus uses finite safe authored values and makes no arbitrary-precision claim.

## Offline reproduction and open scope

From a detached candidate checkout containing these files, invoke the already installed pinned runtime and explicit approved oracle:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' `
  tools/PiReferenceRunner/capture-session-context.mjs `
  --oracle 'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-session-context-oracle-v0.99.1'
```

Default execution verifies fixture/golden/lock/harness/runtime/source/package/receipt hashes before starting children, repeats twice and compares with the initial golden. Review outputs go only to `artifacts/session-context-reference`. Both setup checks forward `--check --oracle <approved root>`, permitting detached checkouts while enforcing the plan's exact path. No download, install or accepted fixture rewrite runs. `--capture-new` is exclusive first creation and refuses existing golden/manifest/lock files; use default verification for review.

This first family does not characterize malformed/cyclic graphs, duplicate IDs, missing-leaf fallback, missing/null-content repair, broad JavaScript date admission, repeated compaction edge cases, absent edit targets, migrations, manager lifecycle, file durability, summary generation, provider payload or runtime recovery. The selected advanced case observes stored influences only. Native stricter admission and unsupported influence diagnostics must be disclosed until separately compared. Native differential evidence and independent acceptance are separate parent-owned steps; these four source captures do not close P4-03/P4-04/P4-06 or the full session phase gate.
