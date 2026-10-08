# Genuine offline Responses wrapper and SDK reference

This corpus captures the unchanged exported `stream` from public Pi commit `d86654abb8862e201933517d6f1fce9f88dd117f`, using the actual upstream-locked OpenAI 7.19.0 SDK. The private `buildParams` and `createClient` execute inside that wrapper. The real SDK serializes requests and decodes authored SSE returned through the supported `options.fetch` seam. No private function was extracted, no SDK was substituted, and no source file was transformed or edited.

The [input](../../fixtures/pi-v0.99.1/responses-sdk/core.input.json) is authored. The [golden](../../fixtures/pi-v0.99.1/responses-sdk/core.expected.json) contains genuine observations, captured twice in fresh offline children with byte-identical outputs. It remains the initial golden: SHA-256 `ef37cbb6ee02d38642597339986b4d73de424ab4fcfd81d9eee4b0a8109bc098`. [Manifest](../../fixtures/pi-v0.99.1/responses-sdk/manifest.json) and [oracle lock](../../fixtures/pi-v0.99.1/responses-sdk/oracle.lock.json) retain provenance, inputs, harness/runtime/setup/dependency hashes and the actual loaded module closure.

## Observed first profile

All three cases use one small non-reasoning text model, an inert key-shaped marker, explicit session/request IDs, authored zero rates and the same successful in-memory SSE response. There is one genuine SDK request per case to `https://pisharp-oracle.invalid/v1/responses`; the injected fetch performs no socket operation. No real key, ambient credential, login or provider endpoint is used.

| Authored `maxTokens` | Observed `onPayload` / SDK request body |
| --- | --- |
| `1` | `max_output_tokens: 16` |
| `0` | `max_output_tokens` is missing |
| `-1` | `max_output_tokens: 16` |

These observations characterize the pinned wrapper's truthiness check and minimum-output-token floor. They do not establish live-server acceptance or prescribe native admission rules. A native implementation that rejects zero/negative budgets must disclose that difference when compared with this corpus.

The SDK request records its exact method, URL, header entries, raw JSON body, body SHA-256, parsed body, fetch-init own keys and signal state. The raw body retains its property order and escaping. Temperature zero remains present. Option headers override the model header, and a null option value removes `X-Remove`; the model's `X-Model` survives. The explicit request ID and user-agent overrides use supported options. SDK-produced `x-stainless-*` headers remain captured without removal or substitution, including Windows/x64/Node/version/timeout values. The inert authorization marker is visible in the fixture and is not a credential.

`onPayload` snapshots the actual builder result and returns `undefined`, so the original params continue to the SDK. Own properties with JavaScript `undefined` values are separately recorded as JSON-pointer paths because JSON serialization omits them. No missing property is converted to null. The authored context also contains a legacy `systemPrompt` property; the observed request's input array contains the user message only. This is retained as observed rather than inserting a synthesized system instruction.

The response is exact authored UTF-8 SSE, including an opaque null/ordered-array field, a Greek pi character and CRLF within text. The fake fetch supplies 17-byte stream chunks. The SDK's parsed DTOs reach the awaited genuine provider-event hook. Recorded callback/emission order is `onPayload`, fetch, `onResponse`, start, provider events and corresponding text progress/end frames, then done. Every case produces five normalized emissions (`start`, `text_start`, `text_delta`, `text_end`, `done`) and final text `Hello π\r\n`, with the source-generated text signature retained. Both emission-time snapshots and asynchronously drained frame snapshots are stored; shared upstream objects can mutate between these two observation boundaries.

## Determinism, isolation and pins

The harness explicitly replaces **`Date.now`** with the fixture time, then restores it. The wrapper has no clock option. This is a disclosed fake clock in the capture process, not an upstream source edit or a supported wrapper clock parameter. Other deterministic inputs use supported session/header/fetch/callback options. There is no random-number replacement and no opaque-field normalization to make repeats match.

The dedicated approved oracle is `P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-responses-sdk-oracle-v0.99.1`. Its original successful restore receipt and prepared layout are hash-locked. Exactly OpenAI 7.19.0, partial-json 0.1.7 and typebox 1.3.27 are installed; all 3,548 / 9 / 1,385 respective package files match their inspected SRI-verified archives byte for byte. SDK Apache-2.0 and packaged vendor/license files are individually hash-referenced. Observing those files does not close the broader dependency/licensing or registry-signature/attestation review.

The actual loaded closure is **30 unchanged upstream files, 199 SDK files and two partial-json files**. Typebox is installed and fully verified but not loaded by this profile. Each executed upstream file matches its canonical Git blob. Full-source verification covers all 2,093 tracked files and separately records canonical and acquired-checkout fingerprints, including only the two exact upstream-declared Windows CRLF checkout conversions. Source cleanliness, complete dependency trees and harness bytes are checked before and after capture.

The existing locked `full-preload.mjs`, `offline-guard.mjs` and unchanged upstream source resolver are reused. Real fetch/HTTP/socket/DNS calls and child-process execution are disabled in each capture child. A further resolve hook forwards the original resolution and rejects unrelated modules or ancestor/global dependency fallback. The child uses a fresh task-local home/workspace and an explicit environment without provider/cloud/npm credentials. No source-loader replacement or package installation runs during capture.

The lossless raw-JSON comparator rejects duplicate object keys and preserves numeric lexemes, strings, null, missing properties, array order and opaque fields; only object-key order is treated as insignificant. Raw request bodies are retained as strings, so their exact bytes remain significant. Values already decoded by the SDK into JavaScript numbers retain that runtime's precision limits. This small input uses no unsafe-precision number, and no numeric-precision parity claim is made.

## Offline reproduction and immutable history

From an isolated clone containing these files, use the already installed pinned runtime and explicit approved oracle path:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' `
  tools/PiReferenceRunner/capture-responses-sdk.mjs `
  --oracle 'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-responses-sdk-oracle-v0.99.1'
```

Default execution verifies fixture identity and input/golden/lock/harness/runtime/dependency/module hashes before starting the capture children, then repeats twice and compares with the immutable golden. It writes review outputs only under `artifacts/responses-sdk-reference`; it does not rewrite accepted goldens or install dependencies. `--capture-new` is a first-creation operation and refuses existing golden, manifest or lock files. Do not use it for reproduction.

Independent review identified R1 in the original parent command: the explicit approved oracle was dropped from the setup helper's before/after `--check` calls. A detached candidate outside the original task-2 sibling layout therefore failed preflight with `Workspace differs from reviewed path`, before capture. The validation-only correction forwards `--check --oracle <approved path>` through both calls. The helper permits that override only for an explicit read-only check and still requires equality to the unchanged canonical plan's approved root, with the existing path/link/source/runtime/input checks. Missing, duplicate and unknown arguments fail. `--prepare` and `--restore` keep their original implicit layout restriction and cannot receive an oracle override.

The accepted setup/prepared/restore receipts continue to identify the original setup helper `7c4cfc8715344710100d99902defeaef5280169f098cc53cab29f96a90120029`. Read-only validation checks that historical identity rather than relabeling those receipts with the corrected helper. The current validator is separately pinned as `1da4ce53bf9b7ccc462bad540294353fd7ab42ba35ca171d3921916409d2b513`, and the capture harness as `2c865a2446769532b6103d030e0eea057fb6234e0e0d33c9113870a985e90045`. No oracle, ownership record, prepared/restore receipt, input, source, archive, installed package or golden is rewritten by this correction.

`tools/PiReferenceRunner/responses-sdk-runner.test.mjs` passed six regression groups from an isolated task-local snapshot: argument admission, exact approved-root selection, actual before/after subprocess argument forwarding from a detached repository copy, and CLI rejection of alternate roots and mutation overrides without local filesystem effects. Its subprocess witness performs no oracle access, capture, download or install. Full corrected parent-command reproduction remains a separate qualification step; the regression does not relabel the original failing command as passed.

The initial harness hash was `4a207176897b3c7916e690f9114c86d42b6ac14a05ada221fc03b267d0edf819`. Subsequent validation-only hardening produced `a5b5844e18789a7b113a26ddcbd147a29982e078ae01518c0f9569adcb01cc91`, required the locked child preload and checked lock/fixture identity/module-path admission before child execution. The R1 correction appends another validation-only history entry with both capture hashes, current and historical setup-helper hashes, and the unchanged golden. The manifest's lock hash follows that metadata update. Source/SDK bytes, receipts and the initial golden remain unchanged; independent acceptance of this parent-command correction is separate from the earlier bounded reference acceptance.

This is upstream-only evidence for one bounded full-wrapper/SDK text profile. It contains no native differential result. Reasoning, tools/images, streamSimple, OAuth/workload identity, live authentication, retries, errors/cancellation, alternate providers/base URLs, optional peers, live pricing, cross-platform behavior and full provider/phase parity remain unqualified by this corpus.
