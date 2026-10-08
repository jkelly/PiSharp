# Clean retry host integration (source only)

This independent implementation reads pinned Pi v0.99.1 upstream d86654abb8862e201933517d6f1fce9f88dd117f. It uses the clean policy a890daf and coordinator c71f870 leaves and clean Bash admission boundary. No quarantined implementation was read, copied, applied, or compared. All fixtures described here are authored and unexecuted; this document makes no compilation or runtime claim.

## Explicit host admission

`PersistentAgentSession.ConfigureAutomaticRetry(AgentRetryPolicy policy, Func<bool,CancellationToken,Task>? persistEnabledOriginal = null, double contextWindow = 0, Func<long,CancellationToken,Task>? originalDelay = null)` is an idle-only configuration call. Ordinary initial and replacement factories must bind the captured `StartupSettingsSnapshot.RetryPolicy`, the admitted model's actual context window, and any explicitly caller-granted persistence callback before lifecycle publication. Bare library sessions keep retry unadmitted. The optional delay injection is for owned deterministic hosts and controls; the default coordinator provides chunked cancellable delay.

`SetAutoRetryEnabledAsync(bool enabled, CancellationToken cancellationToken = default)` joins the exact persistence acknowledgement task. No settings selector grants a write. With no callback this is a runtime preference. A failed acknowledgement leaves the live policy unchanged; a successful acknowledgement updates it even when caller cancellation races with the acknowledgement. Cancellation callback failures preserve their original exception references alongside the body failure. The owning overload `ReplaceableAgentSession.SetAutoRetryEnabledAsync(AgentSessionAttachment attachment, bool enabled, CancellationToken token = default)` reserves the exact attachment through acknowledgement using the existing mutation semaphore. Replacement inherits acknowledged policy, callback and delay before attachment events. A preconfigured target keeps its own model window; an unconfigured target inherits the source window.

`SetAutomaticRetryContextWindow(double contextWindow)` refreshes admitted model metadata at idle. RPC model selection calls it after the existing durable configuration acknowledgement, using the selected admitted model JSON. Other hosts that change models directly must refresh the admitted window themselves. No model or provider capability is discovered by this API.

## Operation ordering and originals

Each provider operation owns one `SessionRetryCoordinator`. After the physical provider completes, the Bash boundary fences new Bash admission, joins all admitted original executor/progress/checkpoint tasks, and flushes deferred Bash messages before the retry decision. Transient retry runs before threshold or overflow compaction, with overflow excluded by the existing source classifier. The fence remains through awaited start event, exact failed-assistant context omission, and backoff. When no retry is scheduled it releases before the existing automatic-compaction boundary recaptures newly admitted Bash work.

The omitted context entry is the exact acknowledged assistant ID whose owned wire message matches the failed assistant. Raw history remains durable; completed tool calls are not replayed. Every acknowledged nonerror assistant resets the budget and joins the end event, including ToolUse, before tool continuation. Terminal finish, coordinator originals, settings originals and physical event output settle before `SessionOperationSettled` and session idle. Original faults retain their references. Callback and unsafe cancellation callback self-waits reject instead of waiting on their own settlement.

`AbortRetryAsync()` joins the coordinator's stable original backoff abort wave. `AutoRetryEnabled`, `AutomaticRetryConfigured`, `IsRetrying` and `IsRetryOwnedCallback` expose actual admission/state. `IsRetrying` covers owned backoff and its terminal observation; it does not describe every phase of preparation. Upstream `setAutoRetryEnabled` only changes the persisted preference: disabling does not abort an already admitted backoff.

## Settings and RPC

Startup user, project and invocation layers normalize legacy retry provider delay independently before merge. Agent delay remains separate from provider delay; unknown fields remain owned data. The pure policy leaf admits bounded typed native counts and finite safe-integer delays. Invalid typed effective policies reject rather than introducing ambient fallback writes.

RPC decodes `set_auto_retry.enabled` as a strict JSON boolean, dispatches `set_auto_retry` and `abort_retry`, and exposes `autoRetryEnabled` and `isRetrying` in `get_state`. Awaited events are `auto_retry_start` with attempt/maxAttempts/delayMs/errorMessage and `auto_retry_end` with success/attempt/optional finalError. Abort acknowledgement follows the original terminal event output. Existing operation events are available for explicitly admitted retry sessions independently of automatic compaction.

## Authored controls and composition

`CleanRetryIntegrationTests.Cases()` has eight groups covering layer migration, acknowledged ToolUse reset and completed-effect retention, held Bash executor/progress/flush order, admitted settings acknowledgement and replacement retention, failed settings rollback and close join, unsafe cancellation callback/body fault identities, strict RPC flags/state/original abort/output joins, and overflow exclusion/transient-before-threshold ordering. Each held original is released and directly joined during cleanup, including fault paths. Fixtures use volatile in-memory session storage, scripted transport, explicit fake Bash executor and held physical output tasks; no native shell or external API is invoked.

Root composition must register the four policy groups, five coordinator groups and eight integration groups exactly once using direct awaited delegates in the existing CodingAgent runner. This lane leaves test Program registration, ordinary profile/factory binding, MCP admission and host persistence callback injection to the root owner. No project, package or lock file changes are required.
