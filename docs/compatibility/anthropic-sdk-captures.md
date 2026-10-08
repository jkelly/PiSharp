# Genuine Anthropic wrapper/SDK capture harness

Root execution checkpoint: `--capture-new --oracle <reviewed-root>` exited 0 after two fresh byte-identical children. All three cases made one actual SDK fake-fetch request and ended `done`. The observed closure contains 26 unchanged upstream files and 127 dependency files (121 SDK files), within eight restored packages/2393 archive-matched installed files. The immutable expected output is 67743 bytes, SHA-256 `eedd5bb2658f13fc8b722a17912e704c33a5bf5001f54689f5aadb49be511bb9`; the original inputs, source, SDK, receipts and older oracles were preserved. Root's default detached reproduction and Astra qualification remain separate checks. These are genuine upstream observations, not a native differential or a phase pass. The Date.now seam, stream-instance emission tap and license hold below remain explicit.

This harness is prepared for root execution against the reviewed task-local Anthropic oracle. No capture, upstream stream execution or golden creation has been performed by its author. It calls the unchanged whole public Pi stream export through the real installed SDK, using supported fake fetch and callbacks. The child explicitly replaces Date.now for deterministic message timestamps; it leaves source/SDK bytes, actual OS/user-agent fields, crypto and other runtime behavior intact.

The [capture module](../../tools/PiReferenceRunner/capture-anthropic-sdk.mjs), [runner](../../tools/PiReferenceRunner/run-anthropic-sdk.mjs), [authored inputs](../../tools/PiReferenceRunner/anthropic-sdk-inputs.json) and [reference plan](../../compatibility/anthropic-sdk-reference.plan.json) are new files. Existing oracles, accepted harnesses/goldens, source and product code remain unchanged. Node syntax checks and 37 pure corpus/conversion/argument/root-admission checks passed, and explicit read-only setup verification revalidated all eight installed trees plus unchanged source. These developer checks qualify harness preparation only; actual whole-module loading and deterministic observations remain pending root execution.

## Source and dependency authority

The public source is https://github.com/earendil-works/pi at commit d86654abb8862e201933517d6f1fce9f88dd117f. Entry module packages/ai/src/api/anthropic-messages.ts is 51170 bytes, SHA-256 b3f7b44f85b46fa4bee89a5f6e82240a6f40c014c4c64d3c64132090fc66572a. Node24.19.0 strips TypeScript natively using the unchanged experimental source resolver. Existing full-preload.mjs forwards loading unchanged and records actual file hashes; existing offline-guard.mjs blocks real fetch/network/DNS/process calls. An additional resolve guard admits only this oracle's upstream tree and exact eight package roots, preventing ancestor/global SDK fallback.

The approved root is P:/PiSharp/root/Documents/Codex/2026-09-30/task-2/Pi-anthropic-sdk-oracle-v0.99.1-reviewed. The earlier incomplete root remains preserved and is never used. [Actual setup receipt](../../compatibility/anthropic-oracle-setup.json) is the verbatim 21806-byte restored record, SHA-256 e11d383b6a5ecb736c89523c065d96e540b2392108b1c23ffa315113bc28c1c9. It records eight upstream-locked packages, 2393 archive-matched installed files, npm11.6.2 and disabled lifecycle/bin/native effects. The new reference plan retains those actual package/archive/tree/manifest/license fields without recomposing license authority.

Standardwebhooks1.1.1 has eight packaged files, no LICENSE/NOTICE text and declared-MIT-only evidence. Its packagedRootLicenseVerified and redistributionLicenseClosure remain false and P1-02 remains HOLD. The SDK's root license and internal qs BSD notice, the other packaged licenses and fast-sha256 Unlicense remain independently retained receipt evidence. Capturing requests does not convert the development-only standardwebhooks admission into redistribution approval or close broader licensing/signature gates.

Before execution, the runner hashes the reference/setup plans, helper/inspector, input, observer/guard/comparator, prepared/restored receipts and public verbatim receipt. It calls setup --check --oracle with the explicit approved path both before and after capture. All2093 canonical source files and both declared checkout conversions must match, as must the complete installed package trees, inspected SRI-pinned archives and immutable receipts. Actual loaded source files must also equal their canonical Git blobs. The locked file closure is observed during fresh execution, never inferred from the static profile.

## Three authored input cases

| Case | Inputs and intended observation |
| --- | --- |
| defaults-model-max | Non-reasoning model maxTokens64; options omit maxTokens, headers, cache/session and timeout. Observe actual source defaults and SDK serialization/headers. |
| zero-numeric-arguments | maxTokens0, cache none, temperature0 and timeout5000ms. Static function declaration, same-model recorded call/result and eight explicit number conversions exercise real argument serialization. |
| negative-headers-long-cache-session | maxTokens-1, long cache/session affinity and timeout5000ms. Model/options header override plus null removal and configured beta tokens exercise the actual combined request. |

Model IDs, prices, timestamps, inert key, transcript records, tool declarations/results and response wire are authored. They are not live catalog/provider data. The same short authored SSE body contains CRLF framing/comments, wire content index4, split UTF-8 pi/emoji text, token/cache usage and a complete message_stop. Seven-byte stream chunks are an input seam. No tool executes; a recorded tool result belongs to request history only.

Numeric lexemes are strings in the input: 1.0, -0, 1e-6, 1e-7, 1e20, 1e21, 9007199254740993 and 0.123456789012345678901. The harness deliberately applies Number(lexeme) to a cloned assistant tool arguments object before calling unchanged stream. It records each authored lexeme, Number.toString, JSON.stringify(number), finiteness and Object.is(number,-0). This makes input rounding and negative-zero identity explicit. It is not a lossless conversion claim or a normalization of captured output. Original null and opaque string arguments remain present; absent fields remain absent.

The source direct stream preserves zero/negative maxTokens in buildParams; actual SDK behavior and any source terminal failure are observed rather than authored as expected output. Native request options currently require positive Int32 tokens, so these profiles characterize an explicit boundary and do not imply native acceptance. streamSimple has a distinct clamp/default path and is outside this capture.

## Actual invocation and observation boundary

The harness imports whole unchanged anthropic-messages.ts and calls public stream. It supplies an authored noncredential key, options.fetch, onPayload/onResponse/onProviderStreamEvent and maxRetries0. It never supplies options.client, extracts private functions, replaces modules, patches source or injects SDK exports. OnPayload returns undefined, preserving params. The unchanged wrapper internally constructs the real SDK and calls beta.messages.create(params,{maxRetries:0}).asResponse().

The installed SDK beta resource targets /v1/messages?beta=true and moves betas into headers. This is source inspection evidence used to constrain fake fetch, not a fabricated request capture. The fixture model base URL has no repeated /v1 suffix. Fake fetch records the actual Request method/URL/header entries and unchanged raw SDK body string/hash, plus input shape and init keys, before returning an authored in-memory Response. No real provider endpoint is reachable.

The SDK handles request serialization/auth headers/HTTP response. Pi's unchanged iterateAnthropicEvents reads the returned raw Response and parses SSE; this does not qualify SDK streaming-decoder behavior. Supported callback snapshots retain payload params, response status/headers and parsed provider DTOs. A tap on this returned stream instance's push method snapshots normalized events before shared partial objects mutate; the provider/source/SDK implementations remain unchanged. Drained iterator frames and final result are also retained separately. Own-undefined path inventories expose properties omitted by JSON serialization without claiming full JavaScript property-descriptor identity.

Date.now is replaced only inside the capture child with 1700000000000 and restored in finally. This is a disclosed environment seam, not an unchanged entire execution environment. The child receives explicit task-local home/temp/config and Node-only PATH, without inherited credentials, provider/cloud/proxy settings, NODE_OPTIONS or custom certificate settings. Actual OS/runtime versions and raw dynamic headers are retained. No RNG or header normalization is allowed; differing fresh outputs stop before golden creation.

## Root capture and immutable verification

Root runs initial capture only after reviewing this candidate:

~~~powershell
$node = 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
$oracle = 'P:/PiSharp/root/Documents/Codex/2026-09-30/task-2/Pi-anthropic-sdk-oracle-v0.99.1-reviewed'
& $node tools/PiReferenceRunner/run-anthropic-sdk.mjs --capture-new --oracle $oracle
~~~

The first-new path refuses any existing expected/lock/manifest before execution. It performs two fresh isolated children and requires byte-identical observations and loaded closure, followed by source/dependency/receipt/input/harness rechecks. Only then does it exclusively create:

- fixtures/pi-v0.99.1/anthropic-sdk/core.expected.json: genuine observations, never authored expected requests.
- fixtures/pi-v0.99.1/anthropic-sdk/oracle.lock.json: environment/harness/source/dependency closure and capture history.
- fixtures/pi-v0.99.1/anthropic-sdk/manifest.json: input/golden/lock hashes, seams/provenance and unchanged license HOLD.

Default reproduction, including detached review checkouts, uses:

~~~powershell
& $node tools/PiReferenceRunner/run-anthropic-sdk.mjs --oracle $oracle
~~~

It validates every frozen input/golden/lock/environment/harness/loaded-file pin before executing and never rewrites immutable artifacts. Changed prerequisites fail rather than regenerate. Reports and actual observations go to artifacts/anthropic-sdk-reference. Only owned mkdtemp scratch directories are recursively cleaned after resolved-path confinement, no-link and ownership-marker checks.

Comparison uses the unchanged lossless raw JSON comparator. Only object key order is ignored; numeric lexemes, arrays, missing/null, opaque/string fields and raw body/header arrays remain significant. No capture execution, native differential acceptance, full auth/OAuth/provider support, SDK decoder parity, licensing closure or phase completion is claimed by harness preparation.
