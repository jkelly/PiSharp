# I04 breadth triage — current suite failures

Source evidence: `artifacts/gap-validation/Compatibility-full-stable4-789d225f255b48debe7ccd947a3996a4/{tests.stdout,tests.stderr}.log` (158 pass, 76 fail); `Transport-full-stable4-b3218c07e269467184d946611b71b963/tests.stderr.log` (218 pass, 25 fail). No independent build/test execution was performed for this triage; I13 owns validation.

## Compatibility: all 76 failures accounted for

| Count | Cases | Finding / action |
|---:|---|---|
| 64 | `completions-tool-images.source-complete-request.*` tool-history cases | Approved I04 `tools:[]` omission conflicts with historical complete bodies. Added a narrow current-target derivative that removes only an empty tools array, plus an independent COMPLETE legacy-body comparison with explicit `RequiresToolsWithToolHistory=true`. Existing Pi150 whitespace-output projection is retained and separately identified. Frozen bytes/hashes untouched. |
| 2 | `completions-user-images.source-complete-request.image-after-tool-bridge{,-vision}` | Same approved omission. Same complete current derivative and complete explicit legacy-body controls. Other user-only capture cases retain exact complete bodies. |
| 1 | `artifact.catalog.released-npm-42-provider-shards` | Missing `artifacts/released-npm-ai/pi-ai-0.99.1.tgz` prerequisite. Root restored official registry artifact: 760966 bytes, SHA256 `f9f44692157d0bf5679c4a17304a310028231d7daaeaaea3b73252f4b7a264d3`. No source mutation needed. |
| 1 | `LimitsAndStrictDtoAdmission` | Anthropic raw DTO expected SourceFailed, observed MalformedStream for an escaped lone-surrogate property name. JsonData explicitly admits JS property-key UTF-16; old assertion comment says JsonData rejects it. Sent to I02 for pinned-source/public-contract triage; unchanged by I04. |
| 1 | `Completions raw DTO depth Unicode count and logical payload bounds` | Generic diagnostic did not identify the failed subcontrol. Current wire admits JS lone-surrogate string values; test includes one as malformed. Current shared JSON max depth is 1000 while some tests still use historical 64/32 constants. Improved generic Equal failure diagnostic to show expected/actual; no behavior expectation changed. Needs rerun/pinned-contract decision. |
| 7 | Listed below | Responses raw-stream public-contract conflict. No assertion or production mutation by I04; await audit_providers baseline actual run and pinned-upstream/public-doc review. Unchanged source alone does not establish correctness. |

The seven Responses failures are `differential.responses.text-tool-completed-overlap-and-failures`, `native.responses.malformed-and-unsupported-dtos`, `native.responses.bounded-admission`, `native.responses.pull-backpressure-and-early-disposal`, `native.responses.cancellation-and-cleanup`, `native.responses.cleanup-before-terminal-and-fault`, and `native.responses.pending-metadata-separate-budget`.

Static conflict: ResponsesTextToolTransport acquires its enumerator before publishing Start (lines 111–117), joins cleanup, then always yields a terminal (157–166), including errors/abort. NativeSourceException/Task preserve original failures, and FailureMessage (168+) displays the original source text. These tests instead expect preacquisition Start, thrown protocol/cancellation failures, canonical malformed/resource classifications and sanitized source messages. Both the intended public contract and upstream authority must be reconciled before changing tests or runtime; I04/R6 request-projection changes did not touch this stream implementation.

Read-only corpus reconciliation confirms exact affected-case sets: the tool fixture has 72 observations, exactly 64 captured empty-tools bodies, and those 64 IDs equal the 64 failures; the user fixture has 25 observations, exactly 2 empty-tools bodies, and those IDs equal the 2 failures. There are no unexplained image-case failures or captured empty-tools cases outside the failure set. This establishes the concrete derivative obligation; runtime full-body derivative and legacy comparisons still require I13 execution.

## Transport: I04-owned capture fix and remaining review

The single `completions-tool-image-flow.all-72-complete-source-bodies-at-http-and-canonical-completion` failure is the same approved tools omission. It now links the shared strict derivative helper, checks the complete explicit legacy profile before send, and compares the complete current derivative at the actual HTTP boundary. This preserves canonical completion and cleanup assertions. No capture or hash changed.

The other 24 failures were sent to audit_providers/root for baseline actual-run and pinned-contract classification. They comprise 18 Responses/native-thinking/request/reasoning/tier/incomplete cases, 2 Anthropic HTTP cases, and 4 Completions hooks/HTTP cases. I02 received the Anthropic details. Concrete review clues, not acceptance conclusions: default SupportsStrictMode=false conflicts with ActiveToolDeclarations authored strict:false fields; default SupportsMaxOutputTokens=true conflicts with authored unsupported-option assertions; current MaximumDepth=1000 conflicts with legacy fixed-depth assertions. These defaults predate I04/R6. The remaining source-contract tests must be reviewed on their merits.

## Files changed for the approved omission reconciliation

- New `tests/PiSharp.Compatibility.Tests/CompletionsCurrentBodyExpectations.cs` (only removes an empty tools array).
- `CompletionsToolImageTests.cs`, `CompletionsUserImageTests.cs`: full current derivative plus explicit legacy complete-body controls.
- `tests/PiSharp.Transport.Tests/CompletionsToolImageFlowTests.cs` and its project file: same helper at real HTTP boundary plus legacy full-body control.
- `OpenAICompletionsWireTests.cs`: diagnostic improvement only, no weakened expectation.

All current derivatives are authored post-baseline expectations from `7a6bd5a97cb64766c9ebbf30e9c0ad7c88a00c47` at frozen selected target `7d073328e44337d3292a7db9b0f0308f679c5e97`. Legacy reference controls remain complete equality comparisons. Validation rerun pending I13.


## Approved source-backed raw wrapper reconciliation

After this initial triage, I13 executed the immutable `14dd34` baseline: Compatibility **225 pass / 9 fail** with the archive prerequisite present, the same seven Responses and two raw DTO conflicts; Transport **219 pass / 24 fail**, exactly current Transport failures excluding the approved image corpus. Source fingerprints were stable. See I13 receipts and `AI_DOCS/agent-reports/Transport-regression-triage.md` for exact baseline artifacts.

Root authorized precise raw-seven reconciliation after audit_providers compared pinned v1.5.0: `packages/ai/src/api/openai-responses.ts:221-230` acquires HTTP/onResponse before Start; catch `251-267` emits aborted/error after helper failure while preserving formatted underlying error text. This is stronger evidence than unchanged code. `ResponsesTextToolTests.cs` now checks acquisition-before-Start without early DTO pull, exactly one final error frame/no StreamDone, retained original protocol/limit/cancellation exception type, source/cleanup exception and task identity, cleanup-before-error publication, zero precancel acquisition, and no duplicate cancellation terminal. Every malformed/private DTO vector and budget boundary remains checked; explicit well-formed provider error display is tested separately. The immutable parsed-helper captures/hashes and overlap comparisons remain untouched, and wrapper assertions are explicitly authored pinned-v1.5 reconciliation.

Root also authorized two precise ResponsesIncomplete branches from pinned shared.ts `625-667`, `821-826`, `861-882`: unfinished text under max_output_tokens retains partial text/usage and returns Length without a fabricated TextEnded; an unknown provider string reason remains an error with exact `rawStopReason` and `errorMessage`, not a malformed DTO. A recording transport now checks the raw terminal frames. Other malformed shape/status/negative usage/trailing-data assertions were NOT blanket changed.

These source-backed tests are coherent and queued with I13; no new execution pass is claimed yet. Production runtime remains unchanged by this reconciliation. The two raw DTO conflicts remain independently reviewed.


## Final coherent checkpoint after audited controls and tier bug fix

I13's next current Compatibility run reached **232 pass / 234 total**, leaving only the two previously observed raw DTO conflicts; the seven rewritten Responses wrapper groups passed. This is actual current execution evidence, not an inherited baseline pass. Exact latest artifacts are managed by I13.

Root then authorized a narrow real production fix: `ResponsesHttpSseTransport` previously let a null request binding bypass a conflicting non-null mapper tier because both absent binding and absent per-request override compared equal. Only an explicit request tier with a recognized, non-null matching wire value can now reconcile a mismatch. `ResponsesTextToolTransport` now prices by response echo first, then normalized per-request tier, then the immutable configured fallback. This fixes no-echo per-request pricing without leaking mutable state. Tests retain all three rejected split tuples (zero sends, owned request disposed, exact InvalidConfiguration original cause), valid matching binding controls, per-request/auto/null/normal/economy/priority/fast/ultrafast/unknown mapping, echo authority, and concurrent shared-transport interleaving with no fallback leakage.

Audited authored controls also now distinguish: valid lone UTF-16 string values (exact lower-case escaped wire, unchanged owned input, exactly one HTTP send for callback replacement; raw streaming requires a true finish and otherwise remains UnexpectedEof), from malformed/nonfinite/depth/bytes/decoded-duplicate negative controls; resource rejection of the 4097-property pending signature (retained StreamLimitException, no success, cleanup gate); malformed service-tier shape rejection (retained StreamProtocolException); and ignored post-done reasoning deltas (exact authoritative content/signature, one ThinkingEnded, no late appended delta). Native stricter unfinished-content rejection remains explicit rather than being globally replaced with upstream permissiveness.

Default-profile test mappings were made explicit: strict:false schema expectations opt into SupportsStrictMode, default omission has a separate control; disabled max-output support omits the field for null/0/-1/16 without inventing an UnsupportedOptions guard, while supported floors/caps remain; body-only cache-key Unicode/scalar clamping deletes generated affinity headers and verifies deletion, while default non-ASCII/control header rejection and printable-ASCII session-id generation retain separate checks. Configured low-depth boundaries remain; invalid depth is the documented MaximumDepth+1 rather than historical 65.

Finite reported `total_tokens:-1` now has an exact successful length/report-fidelity assertion per audited source; wrong status, malformed detail types, nonfinite values and trailing protocol data retain hard rejection with exact raw cause. Unknown provider reason display remains a deliberate source-authored error case. No global Kind substitution or fixture recapture occurred.

ProviderOffline header triage found its observer allowlist, rather than runtime, discarded the new `session-id` header. The observer now includes that one reviewed spelling alongside legacy `session_id`; the authored expected header remains unchanged. The separate Mistral callback policy accounting is owned by R5 and preserved.

All current source/test changes were reported coherent/frozen to I13 for rerun. Production changes are limited to the approved two tier paths beyond I04/R6. Broader acceptance is pending actual rerun; no fabricated final pass is claimed.


## Residual Transport subgroup reconciliation (all assertions reviewed)

After the next run reported 11 failures (9 I04-owned; two Anthropic owned by I02), reviewed every assertion in the nine subgroups rather than only each first failure. Native Responses send cancellation and the thinking wire/high/off controls now assert zero Start before failed acquisition, one raw error/abort terminal, original canceled task/exception or HTTP 400 status, immutable identity/empty content, disposed response and live borrowed handler. Payload selection/high/off assertions remain unchanged.

Responses HTTP vectors continue to reject malformed JSON, arrays, decoded duplicates, invalid UTF-8, premature sentinels/EOF, wrong known DTO shapes and configured byte/event/depth/framing limits; assertions require original protocol/resource exceptions, no successful terminal, and owned cleanup. Malformed tool-argument text is separately finalized exactly as pinned source into `{}` with its complete `argumentsError` marker and ToolUse transport result. This test does not claim downstream execution: the actual `sync150.tool-invoker-reports-malformed-final-arguments` Agent coverage documents rejection before tool invocation. Status rejection consumes its body once and retains the original status. Source/factory/read/cleanup stages retain exception/task evidence and cleanup gates; repeated enumeration still proves fresh independent request objects.

The remaining Completions HTTP failure was exactly the valid opaque lone-surrogate/no-finish tuple: it is UnexpectedEof, while the same DTO followed by a real finish is a positive exact escaped-DTO fidelity control. All other malformed/error/sentinel/duplicate/nonfinite/invalid-UTF8 table expectations are unchanged.

The key-request subgroup now explicitly checks disabled grammar fallback as a full ordinary function declaration, retaining custom-kind/required-strict/future-sampling/string-removal rejection. Numeric strict schema controls explicitly enable strict capability. A valid escaped description must retain exact wire bytes and untouched input, never become U+FFFD. Root authorized a narrow real serializer fix using the existing shared `JsonUtf16.StringNode`, `MutableNode`, and `ToJsonString` across declaration/output, inline input, Azure body and Codex SSE/WebSocket body serialization; no new Contracts helper was added. Added exact escaped-description/ownership controls in none/changes/additions mode across all three provider routes (18 assertions beyond the earlier R6 matrix). Product source and test checkpoints were reported coherent/frozen to I13 for affected-package revalidation.

No post-checkpoint pass is claimed here. Frozen fixtures remain unchanged, all current-target differences are explicit, and no blanket Kind replacement removed admission/resource/privacy checks.


## Last two Transport assertions

I13/root reported the affected follow-ups passed: R6, Azure **17**, ProviderApis **60**, ProviderOffline **30**. The Transport rerun had only two failures out of 245: metadata-free explicit off still used a frozen raw thinking-level map, and a synchronous cleanup failure incorrectly required an original task.

The first was a real native binding bug. `NativeThinkingTransport` intentionally selects the frozen factory when `ThinkingLevel` is null and a separately resolved explicit profile when it is non-null. Responses selected callbacks kept `effectiveRequestOptions.ThinkingLevelMap` even without admitted model metadata, unlike Completions' `profile?.Map` behavior. Root authorized aligning both Responses factory routes: selected requests now use `profile?.Map`; the null-level legacy factory and admitted model map remain untouched. Existing null/off/null cycles retain high/none/high, and an added admitted-map cycle proves high/low/high plus immutable caller options.

The cleanup fixture's `DisposeAsync()` threw synchronously before returning a ValueTask, so `ResponsesFailureContext.Cleanup` correctly had an original exception but no original Task. Replaced the overbroad task assertion with exact synchronous versus asynchronous controls: both retain the exact original IOException, exactly one cleanup attempt, final error/noDone and owned request disposal; only the asynchronous branch must retain its exact faulted task/inner-exception identity. The synchronous branch must not fabricate a task. This changes no runtime cleanup policy.

Both source and tests are coherent/frozen for I13's final Transport rerun. No passing result for that rerun is claimed yet.
