# Genuine offline Responses tool declarations and SDK reference

This corpus observes the unchanged exported `stream` in public Pi commit `d86654abb8862e201933517d6f1fce9f88dd117f`, with the actual upstream-locked OpenAI 7.19.0 SDK. The wrapper's private builder and client execute internally. Supported `onPayload` and injected `fetch` options expose the real params and SDK serialization; the fake fetch returns authored in-memory SSE. There is no extracted builder, SDK replacement, source transformation, provider connection or package installation during capture.

The [input](../../fixtures/pi-v0.99.1/responses-tools-sdk/core.input.json) contains five authored tool-history cases. The [golden](../../fixtures/pi-v0.99.1/responses-tools-sdk/core.expected.json) is the actual observation captured twice in fresh offline children, with byte-identical outputs. Its initial SHA-256 is `46c27eb741e3edbd38fbe2442545944f1de1d6209c073efce96dfb2208e2d37d`. The [manifest](../../fixtures/pi-v0.99.1/responses-tools-sdk/manifest.json) and [lock](../../fixtures/pi-v0.99.1/responses-tools-sdk/oracle.lock.json) record input, source, runtime, harness, setup receipt, complete installed dependencies, licenses and loaded-module hashes. No prior corpus or golden is rewritten.

## Observed declaration histories

Each request uses a synthetic non-reasoning model and explicit compatibility flags. Tool declarations enter through genuine system messages' `toolsAdded` and `toolsRemoved` fields. The standard function schemas are authored, including Unicode text, an optional integer default, ordered enum/required arrays and an opaque nested null/array field. These are declarations only; no file-tool implementation executes.

| Case | Authored transcript change | Observed request tool order | Observed `strict` |
| --- | --- | --- | --- |
| `initial-tools-strict-capable` | Add read and write | read, write | Present and false on both |
| `remove-add-strict-capable` | Remove read; add edit | write, edit | Present and false on both |
| `readd-strict-capable` | Re-add read | write, edit, read | Present and false on all |
| `replace-strict-capable` | Replace the active write definition without removing it | write, edit, read | Present and false on all |
| `replace-strict-incapable` | Same replacement with `supportsStrictMode:false` | write, edit, read | Missing on all |

The replacement request contains the new write description and schema, including its optional `mode` enum and default. The previous write definition is absent. Existing map position is retained; deletion followed by re-addition appends. The read schema's opaque null and array ordering survive both builder and SDK body serialization.

`supportsMidConvoSystemMessages:false` folds the active declarations into the request's leading system context. The empty later system messages carrying tool changes do not appear as separate request input items; all authored user messages remain ordered. `supportsAdditionalTools:false` selects the complete active tool list. These explicit flags bound the observations and do not prove behavior for other compatibility profiles.

None of these tools requests constrained sampling. Capability true therefore exposes the pinned converter's default `strict:false`; it does not turn the schemas into strict constrained schemas. Capability false omits the field. The immutable `body` string, `bodyJson` object and `onPayload` snapshot retain missing versus null. The generated convenience report also lists `strictOwnProperties`; its `strictValues` array uses JSON null placeholders for absent JavaScript values, so inspect the presence flags or raw body for that distinction.

Every case makes one genuine SDK request to `https://pisharp-oracle.invalid/v1/responses`. The record retains method, URL, all header entries, exact raw body and its UTF-8 hash, parsed body, fetch-init own keys and signal state. SDK-generated `x-stainless-*` headers remain unchanged, including platform, runtime, package and timeout values. The explicit supported session/request/user-agent options are authored. An inert key-shaped marker is visible in the Authorization header; it is not a credential. No ambient provider/cloud credentials are inherited.

`onPayload` snapshots the genuine builder output and returns `undefined`, allowing the original params into `responses.create`. Own undefined properties are separately recorded as JSON-pointer paths, rather than replacing them with null. The actual SDK decodes the authored SSE and passes DTOs to the awaited provider hook. Emission-time snapshots are taken before shared partial objects can mutate; asynchronously drained frames and the final result are also retained. Each case emits start, text start/delta/end, and done, ending with authored response text `Hello π\r\n` and its genuine source-generated signature. The response carries no tool call; this corpus qualifies request declarations, not tool execution or provider tool selection.

The response wire is reused from the earlier **authored** [SDK input](../../fixtures/pi-v0.99.1/responses-sdk/core.input.json), SHA-256 `3d4431739bcce4b2218d13a71dba4bf273fe47aab8937f36d3177eb984bf9633`. That input is separately pinned by the new harness and lock. No earlier expected output is read or copied. The fake fetch supplies its exact UTF-8 bytes in 17-byte chunks.

## Source, runtime and isolation

The executed closure is **30 unchanged upstream files, 199 OpenAI SDK files and two partial-json files**. Typebox is installed and fully verified but not loaded by these cases. All executed upstream files match their canonical Git blobs. The relevant source entries include:

| Unchanged source | SHA-256 |
| --- | --- |
| `packages/ai/src/api/openai-responses.ts` | `dc95a3ddf55454feb6f841055c518ca2a212a8a5bcc5982e7812da35151458cb` |
| `packages/ai/src/api/openai-responses-shared.ts` | `85db12efcd109d505c98846e34c45cb4a3260edcee88ef28c8afd1db20eac26a` |
| `packages/ai/src/utils/transcript.ts` | `cad0c126cdfadc1b4f430cc5a95b1c9285ecd1ee9957642d7773b1357bbc9ac5` |
| `packages/ai/src/api/constrained-sampling.ts` | `1b869f139e5cd1f09b4ebecb8f7ce9ea44b63e75dc6d6158ac8f5d00f2eaddbb` |

Before and after capture, verification covers all 2,093 tracked source files, source cleanliness, harness bytes and complete installed package trees. The source canonical Git fingerprint is `2d65bfaee0e2556cb82ae7e0425de68560ecc3be4451f69ceff3bcc9c6144aa3`. Acquired-checkout bytes are separately recorded, including the two declared Windows CRLF conversions; every executed source file is canonical unchanged source.

The dedicated approved SDK oracle contains exactly OpenAI 7.19.0, partial-json 0.1.7 and typebox 1.3.27. All **4,942 files** (3,548 / 9 / 1,385 respectively) match the inspected, SHA-512 SRI-verified archives. Runtime Node v24.19.0 is pinned to the exact executable SHA-256 `3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237`; the successful setup used pinned npm 11.6.2 with scripts disabled. SDK Apache-2.0 and packaged vendor/license files are individually hash-referenced. These observations do not close broader dependency/license or registry signature/attestation review.

The existing `full-preload.mjs`, `offline-guard.mjs` and unchanged upstream source resolver execute without modification. Capture children block socket, HTTP, DNS, real fetch and process spawning. An additional resolution guard forwards normal resolution and rejects unrelated modules or ancestor/global dependencies. Each child receives a fresh task-local home and workspace and an explicit credential-free environment. Cleanup resolves and checks each temporary path beneath the owned scratch root before recursive deletion.

The harness explicitly replaces **`Date.now`** with `1700000000000`, restoring it afterwards. The wrapper has no clock parameter; this is a disclosed process-local fake clock, not a supported wrapper option or source edit. Supported session/header/fetch/callback inputs supply the remaining deterministic seams. No RNG replacement or opaque-field normalization makes repeats match.

The raw comparator rejects duplicate keys and preserves number lexemes, strings, null, missing properties, arrays and opaque fields; only object-key ordering is insignificant. Raw request bodies are captured as strings and remain byte-significant. SDK-decoded JavaScript numbers retain JavaScript precision limits. These authored inputs use no unsafe-precision values and establish no broader numeric-precision or live pricing parity.

## Reproduction and open inventory

From a detached candidate checkout, invoke the pinned runtime and explicit already-restored oracle:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' `
  tools/PiReferenceRunner/capture-responses-tools-sdk.mjs `
  --oracle 'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-responses-sdk-oracle-v0.99.1'
```

Default execution verifies input/golden/lock/harness/runtime/setup/module bytes before starting capture children, then runs two fresh children and compares against the immutable golden. Review outputs go only to `artifacts/responses-tools-sdk-reference`; defaults never rewrite fixtures or install packages. `--capture-new` creates golden/lock/manifest with exclusive writes and refuses existing files. Do not use it for reproduction.

The current read-only setup validator is pinned as `1da4ce53bf9b7ccc462bad540294353fd7ab42ba35ca171d3921916409d2b513`. Both before/after checks explicitly forward `--check --oracle <approved root>`, supporting detached review checkouts while still requiring the immutable plan's exact approved root. Original prepared/restore receipts continue to identify historical setup helper `7c4cfc8715344710100d99902defeaef5280169f098cc53cab29f96a90120029`. The two identities are checked separately. No historical receipt is relabeled or rewritten. This is the initial capture of this family; the golden has no regeneration or validation-edit history.

The remaining declaration inventory is explicit:

| Surface | Source boundary | Qualification by this corpus |
| --- | --- | --- |
| Standard unconstrained functions and active declaration replay | `getCurrentTools`, `resolveTranscriptTools`, `convertResponsesTools`, full exported wrapper | Five observed request histories only |
| Strict constrained JSON-schema sampling and schema rewriting/admission | `constrained-sampling.ts`, `convertResponsesTools` | Not exercised |
| Grammar/custom tool declarations | `convertResponsesTools`, constrained sampling helpers | Not exercised |
| Additional-tool anchors, non-additive transcript changes and tool search | Transcript resolution and wrapper compatibility options | Not exercised |
| Tool choice, multi-call responses and actual tool invocation | Wrapper options, stream mapper and agent/tool runtime | Not exercised |

These open surfaces require further source-driven cases rather than permanent exclusion. Native differential validation, live provider acceptance/authentication, reasoning/images, errors/retries/cancellation, optional peers, other providers/platforms, catalog/live cost authority and full phase parity remain outside this small upstream-only capture. Independent review and acceptance belong to the parent review process.
