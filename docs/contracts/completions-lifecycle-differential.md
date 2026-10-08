# Completions SDK lifecycle differential probes

This diagnostic slice compares the actual native request factory, HTTP/SSE transport, Completions mapper and `ChatRun` against the complete pinned public Pi/SDK lifecycle family. It changes no production contract. Native ownership success and source compatibility success are separate results.

## Reference authority

The reference is `fixtures/reference/openai-completions-sdk-lifecycle/{input,expected,manifest}.json` with `tools/ReferenceOracle/openai-completions-sdk-lifecycle.lock.json`. The root must copy these exact frozen files from reference commit `030d4106a9ab2a34db569edb3c948e17bb423df2` before registering the tests.

| File | SHA-256 |
| --- | --- |
| input.json | `2e35c47906fd8c52c0d156dc2af4621f746fdcdb33fb319fecc78abaa438bd2e` |
| expected.json | `829ec6d61f05e0064b6ca38799e264b6b4a6f7a2029d4f499e81d84b5475b81b` |
| manifest.json | `de93a79194565e5f04c86c70c887473a78a5a707f3305747412ef48703945c81` |
| source lock | `8249a6aa25b4386e5b58917acb303ea9aaf6a96aa3cc1263d125ff3a2d4a8172` |

Pi source identity is `d86654abb8862e201933517d6f1fce9f88dd117f`. SDK 7.19.0 `core/streaming.mjs` has SHA-256 `1b8fa8338dc28913351993382187962082f0af42c5274a009060de3263579842`. Its complete `fromSSEResponse` checks exact `[DONE]` before parsing, uses the named `thread.*` envelope, and suppresses cleanup exceptions after the completion sentinel. These are source observations, not inferred native outcomes. The parent reports four fresh whole-public-Pi/SDK captures with identical expected bytes, 29 loaded source modules and 201 loaded dependencies. This author has not rerun those captures or executed the native probes; independent qualification remains with the root/reviewer.

## Complete case inventory

| Case ID | Captured source behavior being probed |
| --- | --- |
| missing-empty-named-and-thread-events | Missing/empty/arbitrary event names yield `ABC`; the thread envelope is ignored by the provider mapper. |
| event-only-nonempty-name | A named frame without data reaches JSON parsing and fails. |
| empty-data-event | Explicit empty data reaches JSON parsing and fails. |
| named-error-with-falsy-property | Named `error` remains an SDK error when its `error` property is false. |
| exact-done-joins-cancel-and-ignores-tail | Exact DONE waits for gated cancellation before successful terminal/result settlement and ignores the malformed tail. |
| trailing-space-done-is-not-exact | `[DONE] ` is parsed and fails, retaining prior text. |
| eof-later-bom-replacement-utf8-every-byte | One-byte reads exercise later BOM removal, invalid UTF-8 replacement and pending data at EOF. |
| falsy-error-false-cancel-rejects-after-done | Source succeeds despite actual rejected cancellation after DONE. |
| falsy-error-zero-release-fault-after-done | Source succeeds despite an actual reader release fault after DONE. |
| falsy-error-negative-zero-consumer-returns | Consumer returns after Start; the public Pi producer continues to its successful result. Source provider DTO retains negative-zero bits. |
| falsy-error-empty-string | Empty-string error is falsy and permits successful text completion. |
| abort-pending-read-cancel-gate-rejects | Source terminal/result/producer settle while gated rejected cancellation is still pending. |
| reader-acquisition-fault | Response hook and Start precede the actual reader-acquisition fault. |
| on-response-fault-before-reader | Actual source hook failure emits Error without Start or reader acquisition. |

## Execution and comparison surfaces

`CompletionsLifecycleDifferentialTests.Cases()` exposes one normal group: `completions-lifecycle.native-owned-observation.all-14`. It attempts every case and checks bounded execution, request/response ownership, joined run disposal, borrowed-client lifetime and actual drained-terminal/Completion consistency. It does not assert any known source/native equality. `StrictCases()` exposes all 14 separate parity comparisons; every observable mismatch or unsupported API fails its strict case. Captures are shared once per process, so registering both lists does not execute a case twice. Run both surfaces and retain their distinct results. A passing capture group cannot imply passing compatibility.

The native route constructs an actual `CompletionsKeyAuthRequestFactory` request with the fixture's inert explicit key, uses the actual default SDK-profile `CompletionsHttpSseTransport`, enables the accepted production `CompletionsSourceEventProjection.CaptureOwnedSnapshots`, and uses actual `ChatClient.StartAsync` with channel capacity one. The handler and body supply only owned fixture response bytes, headers and injected faults. There is no network, source reimplementation, private mapper replacement or synthetic source callback.

Each bounded JSON receipt retains the complete source case and its hash, exact input wire hash, all actual native emitted/drained snapshots, actual canonical final and drained terminal, exact request body/UTF-8 hash/headers, actual response metadata, full native/source traces, gate checkpoints and cleanup dispositions. Missing production snapshots remain visibly missing. Comparison checks complete JSON field presence and scalar values, array order, numeric token spelling and every binary64 path/value; explicit-null/missing, negative-zero and numeric-spelling controls guard the comparator. Object member order is treated as a mapping in decoded snapshots. Actual serialized request strings/bytes are compared separately without reordering. Source own-undefined and numeric sidecars remain in the complete case and mismatch receipts. No undefined path is filtered or filled with null.

Input, expected, manifest and lock bytes are capped and hash-checked. Authored wire/request observations are bounded at 65,536 bytes, native traces and each snapshot list at 4,096 entries, cumulative native snapshots at 1,048,576 characters, mismatch records at 4,096, and each complete output receipt at 1,048,576 UTF-8 bytes. The 14-case inventory bounds total output. Five-second waits diagnose stalls; released fixture gates and cancellation are followed by actual owned-task joins. No lock is held while awaiting external work.

## Seam limitations and mandatory findings

Native `Stream.DisposeAsync` and synchronous cached-stream disposal are injected cleanup seams. They are not SDK `reader.cancel` and `releaseLock`. Receipts compare selected operation ordering with explicit labels, retain both complete traces, and report the absent SDK Reader DTO/lock API. ReadableStream enqueue/prefetch bytes and native bytes read remain distinctly labeled; their values are never normalized into equality. At failed acquisition the fixture closes its untransferred stream separately and reports its disposal state at native settlement before that fixture teardown.

The consumer-return native probe holds its first body read until actual reader disposal/cancellation. This makes abandonment deterministic on the native bounded channel; the source case has no added read gate. The resulting source detached-producer behavior and native cancellation remain a differential finding, not a claimed identical scheduling experiment. Caller-abort uses the actual pending-read cancellation seam and reports the actual provider terminal production, run final/drained terminal and owned work-token state separately.

Native APIs currently expose neither awaited `onResponse`/`onPayload` nor provider-DTO callbacks. Response metadata comes from the actual owned `HttpResponseMessage`; it is not a hook. The hook-failure case maps an owned handler fault before response handoff and explicitly fails strict hook compatibility. SDK request telemetry/init-object shape and reader DTOs also remain visible in the complete source case. All unsupported rows fail strict comparison and remain mandatory work.

Static source review identifies likely differences in Start/send ordering, post-DONE cleanup-fault suppression, gated abort settlement, consumer return, source error text/metadata, callback APIs and missing `responseId` own-undefined state. These are targets for execution, not an actual native mismatch count. Existing native cleanup joins and terminal/Completion consistency remain required. Any future source-facing suppression or early-settlement policy needs an explicit compatible boundary design and genuine new differential validation.

**Native test execution status at correction freeze: NOT RUN.** Root's first aggregate build of `2b9e019873589be80827a0c27d062dd125f7f8a8` failed before tests with zero warnings and one CS0114 error: the fixture's private `Close()` helper hid `Stream.Close()`. The unchanged failed log is retained at `artifacts/completions-lifecycle-differential-first-native.log`. The correction renames that helper to `CloseOwnedBody`; it does not suppress diagnostics or alter cleanup behavior. Root has copied the four pinned reference files and registered the normal capture group plus `--strict-completions-lifecycle` comparisons. Compilation, serial native execution and independent review remain with root. Full Completions provider/lifecycle parity is open; no permanent exclusion is introduced by this diagnostic slice.
