# Pi Messages remaining qualification criteria

Decision: no production correction is justified by the inspected evidence. Preserve both OPEN outcomes. This is a source-only analysis of candidate `899e00f54723e0ab396db92282594301e66bcd4d` (tree `41490855eca9afa62f6f0604e23c45ae7ef45f0d`) against Pi v0.99.1, `d86654abb8862e201933517d6f1fce9f88dd117f`. No native build, test, oracle capture, live API call, or integration merge was performed for this analysis.

## Retained execution evidence

The coordinator's `task-3/NATIVE_FULL_COMPANION_HANDOFF_899e00f5.json` records 32 launched targets: 31 passed and Pi Messages exited 1. Pi Messages has 515 indexed outcomes: 513 authored checks completed, one OPEN_DOMAIN_GAP and one OPEN_CRITERION; zero behavioral failures and zero unexecuted outcomes. Its process/output joined and cleanup reports ORIGINAL_OPERATIONS_SETTLED. The report records zero genuine Source cases captured. These are retained coordinator results, not executions by this lane.

| Original criterion | Retained result | Exact remaining evidence |
| --- | --- | --- |
| PM-TOOL-IDENTITY-REPLACEMENT/1: retain Source/native contract conflict until lead releases solution | OPEN | Reviewed successor qualification and an explicit acceptance/supersession record; current authored native controls are not a genuine Source capture. |
| PM-TOOL-IDENTITY-REPLACEMENT/2: do not modify shared reducer or treat rejection as parity | OPEN | Review of the authorized Pi-specific identity replacement against the pinned Source contract, retaining the historical restriction and frozen expectations. |
| PM-NATIVE-TOOL-CONTINUATION/1: finalized args 7; one fake inspect effect after awaited assistant barrier | COMPLETED | No failed assertion in this provider harness. This does not establish actual Agent/ToolInvoker execution. |
| PM-NATIVE-TOOL-CONTINUATION/2: actual assistant/toolResult continuation and second physical request; no Source assistant seed | COMPLETED | Provider harness uses the actual native assistant but fixture-provided toolResult. Completion is bounded to this harness. |
| PM-NATIVE-TOOL-CONTINUATION/3: unchanged Agent/ToolInvoker and ownership approval for integration assertions | OPEN | Admitted integration execution, matching runtime receipts and reviewer acceptance of the two-turn mapping; none is established by this full-run row. |

Identity row: one send, five provider callbacks, zero effects, one terminal publication/delivery, no unjoined operation. Continuation row: two sends, ten callbacks, one manually witnessed effect, two terminal publications/deliveries, no unjoined operation. Both rows have null failure. The OPEN ledger groups have zero failed or in-flight operations.

## Source and harness boundary

Pinned `packages/ai/src/api/pi-messages.ts:261` applies `Object.assign(partial.content[event.contentIndex]!, event.toolCall)` at toolcall_end. Lines 414-418 await the provider callback before conversion/publication, including the terminal event. This supports the final identity replacement and five callbacks already implemented; it does not supply captured differential evidence.

Pinned `packages/agent/src/agent-loop.ts:242` awaits streamAssistantResponse before tool execution; lines 444-455 await the final result, replace/append the actual assistant and await message_end. Lines 267-276 execute tools and append their results before continuation. This defines orchestration behavior, but inspection does not prove the native integration ran it for the original case.

Native `tests/PiSharp.PiMessages.Tests/Program.cs:294` calls `h.ToolEffect(call)`, then appends the actual assistant and the fixture's continuation.toolResult. `HeldOwnershipHarnessR2.ToolEffect` checks ownership, terminal delivery and args 7, then increments a counter. Neither operation invokes Agent or ToolInvoker. Program retains integration-open explicitly. Its old reason says shared paths are unreleased; that wording is not evidence that current shared paths are defective or still unreleased.

The separate `IdentitySuccessorContinuation.cs` does instantiate the native Agent and ToolInvoker. However, it uses PM-TOOL-IDENTITY-REPLACEMENT, tool `other`, final replaced identity, response `continued`, and tool result `continued-tool-result`. The original continuation fixture uses `inspect` and `Observed seven.`. It checks selected fields rather than the original complete second request and uses ByteArrayContent rather than a held cleanup schedule. The normal full runner does not invoke this helper; the selected identity controls profile does. Its return status explicitly says PASS_AUTHORED_NATIVE_ONLY_REVIEW_PENDING. Its existence or another profile's pass cannot silently close the original criterion.

## Next legitimate steps

1. Review `identity-successor-admission-r1.json` and retain its null genuine Source fixture/output pins, null acceptanceRecord, and frozenDependencyRewriteAllowed=false. Obtain a legitimate offline original-Pi capture and a Source/native comparison if genuine Source qualification is required. A historical denied-dependency note does not authorize a new capture or a bypass.
2. The coordinator may explicitly admit the existing `pi-messages-identity-provider-r1` and `pi-messages-identity-agent-controls-r1` targets. Seal the whole candidate, obtain fresh permitted locked build/run receipts, match all recorded runtime assembly hashes and retain full actuals and cleanup/join outcomes. This analysis authorizes no execution.
3. The reviewer must accept or reject the versioned successor expectations and the mapping to PM-NATIVE-TOOL-CONTINUATION/3. If that mapping is insufficient, author an original-case Agent/ToolInvoker control with the actual tool-produced result, complete second-request comparison, final args 7, exactly one effect, awaited assistant/cleanup ordering and joined operations. This requires the integration ownership allocation; it is not a provider-local production fix.
4. Record any approved historical supersession separately, preserving original fixture, golden and dependency bytes. Do not mark either original OPEN criterion successful merely by removing its ledger.Open call or copying another profile's status.

## Supporting artifact

`compatibility/pi-messages-open-criteria-899e00f5.json` retains the exact small extracted criterion records, hashes of inspected sources and retained reports, and the scope of static checks. No test or production file is changed by this analysis. Phase/package acceptance remains OPEN.
