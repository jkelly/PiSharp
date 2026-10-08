# Awaited Completions lifecycle hooks and response-ID presence

This correction adds actual awaited payload, response and provider-DTO callbacks, fixes HTTP Start ordering, and retains source response-ID presence in the explicit owned source projection. Native canonical messages, raw callback data, constructor signatures, resource admission and joined cleanup remain separate contracts. It does not suppress cleanup faults or settle a terminal before owned cleanup.

## Recorded starting point

Root's immutable `84ab54ff5e0a12e1cab14a20a3bda69881b9fefe` passed 516 normal groups with zero warnings/errors and Node absent. All 14 owned native observations completed. Its separate strict command failed all 14 comparisons with counts `29,22,22,22,25,29,26,86,86,53,25,23,24,73`: 545 mismatch observations, not 545 distinct defects. Original bounded receipts and reports remain in the earlier differential worktree at `artifacts/completions-lifecycle-strict-first.log` and `artifacts/transports/completions-lifecycle-strict-first.json`.

Source-only examination of every recorded mismatch produces this exhaustive grouping:

| Group | Observations |
| --- | ---: |
| Actual request serialization/headers | 126 |
| Source own-undefined presence | 63 |
| Source error text/native diagnostic fields | 41 |
| Lifecycle ordering/checkpoints/disposition | 19 |
| Missing callback APIs | 42 |
| Nonidentical Stream/Reader observations | 42 |
| Explicit fault/return seam mappings | 2 |
| Remaining frame/result differences, including cleanup-fault and abandonment alignment cascades | 210 |

There are 159 binary64-path mismatch records across these groups. Many follow an unmatched terminal/frame shape after native failure and are not independent numeric kernel defects. Actual original native cleanup-fault cases ended Error with four frames where source ended Stop with five. Native consumer abandonment produced two frames, drained one, and ended Aborted where the source producer continued to Stop. At the held abort-cleanup gate native terminal/result/producer flags were false while the source flags were already true. These observations remain mandatory compatibility work.

Reference Pi identity remains `d86654abb8862e201933517d6f1fce9f88dd117f`; the 14-case expected pin remains `829ec6d61f05e0064b6ca38799e264b6b4a6f7a2029d4f499e81d84b5475b81b`. No golden, source input or reference lock is revised. Pinned Pi `openai-completions.ts` awaits `onPayload`, sends, awaits `onResponse`, emits Start, then awaits each provider callback before assigning `output.responseId ||= chunk.id`. That actual ordering grounds this slice.

## APIs and admission

`CompletionsLifecycleHooks` has optional awaited `OnPayload`, `OnResponse` and `OnProviderStreamEvent` delegates. Each receives the selected native `ModelDescriptor` and invocation cancellation token. Inputs are owned immutable Contracts JSON or typed owned response metadata. No callback receives an HTTP request, response or body handle.

`CompletionsKeyAuthRequestFactory.CreateAsync(request, explicitApiKey, hooks, cancellationToken)` performs the existing request admission, awaits the payload hook before transfer/send, and disposes its private request on failure. The hook receives `CompletionsPayloadObservation.Value` plus immutable own-undefined paths for the source cache fields omitted from serialized payload JSON. Returning C# null retains the original payload; returning an owned JSON object replaces it. Explicit JSON null is a supplied unsupported payload, not an absent replacement.

Replacement admission strictly reparses retained raw syntax, rejects duplicate decoded keys, malformed Unicode, nonfinite numbers, excessive depth and character/UTF-8 budgets before send. Actual production `EcmaScriptJsonProjection` serializes an admitted replacement with the existing source JSON number/property-order rules. Callback-owned original token spelling, property presence/order and raw JSON remain unchanged. The native immutable replacement API does not reproduce arbitrary JavaScript in-place mutation or accept non-object/non-JSON callback values; those broader source callback behaviors remain open.

`CompletionsHttpSseTransport.FromAsyncRequestFactory` selects awaited request construction without adding an ambiguous constructor overload. Existing synchronous constructor calls, target-typed options and positional deconstruction remain available. `CompletionsHttpSseOptions.Hooks` and response observation limits are nonpositional init properties. Default limits admit 128 header names, 8,192 characters per name/value, 32,768 cumulative header characters and 131,072 serialized metadata UTF-8 bytes. Admission reads actual owned response headers with bounded joining and valid Unicode before calling the observer. It does not fabricate SDK telemetry constants.

The shared HTTP transport prepares and owns the actual response, awaits its bounded response observer before any body acquisition, and retains the same awaited body-then-response cleanup. A private invocation chunk enumerator implements the preparation seam; the Completions mapper awaits it before publishing Start. Its bounded owned Start copy is admitted before request construction/send and charged to the cumulative capture budget; a failed admission gains no HTTP effects. A response-hook fault yields Error without Start or body acquisition. A subsequent body-acquisition fault follows Start. Ordinary parsed-chunk sources have no preparation phase and keep their original timing. Provider callbacks receive the complete owned parsed DTO after SDK envelope/error handling and before mapper state mutation; callbacks apply awaited backpressure and cannot silently replace raw tokens.

Exceptions at external hook/read seams continue to produce the existing sanitized native failure fields. This slice does not expose arbitrary exception messages to match source errors. Source error text/metadata and full source model/callback mutability remain explicit gaps.

## Response-ID ownership

Each mapper state tracks whether the source assignment happened, and whether its value is missing, explicit null, an empty string or a retained first nonempty ID. Start begins without an assigned response ID. A processed chunk with missing ID creates owned undefined presence; explicit null/empty ID remains distinct and can be replaced by a later truthy ID. Later chunks retain the first truthy ID. Existing native ID type admission remains in force; this does not broaden non-string identifier inputs.

Only the selected source view receives `/partial/responseId`, `/message/responseId` or `/error/responseId` own-undefined paths. `ReadFinalObservation` returns an owned source-facing final value/presence sidecar from an actually captured provider terminal and enforces a separate output character bound plus a 256-character input-envelope allowance. It strictly reparses the entire retained input, including root syntax, before extracting the final. It leaves the original canonical native message and earlier immutable snapshots unchanged. A provider-produced final observation is not a replacement for a different authoritative `ChatRun.Completion` after cancellation; the diagnostic retains both.

## Validation surfaces and outstanding work

`CompletionsLifecycleHookTests.Cases()` supplies six groups: legacy source-compile/deconstruction controls; real gated response/provider ordering and raw ownership; awaited payload cancellation, replacement, strict negative admission and inclusive byte budget; response-hook versus acquisition faults and header bounds; cancellation/callback fault with joined cleanup; and response-ID missing/null/empty/truthy chronology with final ownership.

The fresh branch's existing lifecycle diagnostic now wires all 14 inputs through actual hooks and records only actual invocations. Its hook-fault case throws inside the actual awaited response callback; the earlier handler mapping is removed from this new candidate once the real seam is used. Empty callback arrays stay actual empty observations. Complete source cases, canonical native results, actual source-facing presence, raw request bytes, native traces and all remaining strict differences stay in the receipts. Original `84ab54` receipts remain immutable evidence. No unsupported Reader, SDK telemetry, cleanup or scheduling row is waived.

Root registers the six new groups and runs its normal native gate. After build, the existing transport executable accepts `--strict-completions-lifecycle --report <owned-report-path>` to rerun all 14 mandatory comparisons separately. Capture success and strict parity success remain distinct. Root owns compilation, processes, Git and independent review.

**Candidate execution: NOT RUN at author freeze.** No new mismatch-reduction count is claimed before real execution. Cleanup-fault suppression after DONE, source early abort settlement, detached producer return, Reader/Stream correspondence, full callback/model ABI and remaining error/metadata behavior remain mandatory open work. Any future nonfatal cleanup policy needs explicit boundary design and review while preserving native terminal/Completion consistency and actual joins.
