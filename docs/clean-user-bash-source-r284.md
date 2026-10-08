# Clean user Bash source boundary R284

This independently authored source slice adds explicit user Bash capability capture, original operation ownership, cancellation and deferred message persistence. It is based on authorized `f36c61ef8cca61834c4618a975601ed9900e2e81`. It does not include native process acquisition, CLI grant capture, RPC dispatch, extension interception or test runner registration. No builds, tests or native operations were executed.

## Public capability seam

`ConfigureUserBashExecution(IUserBashExecutor?, Func<UserBashExecutionUpdate, Task>?)` captures an already admitted executor and observer while idle. No default executor or ambient grant exists. `ExecuteUserBashAsync(string, bool?, string?, CancellationToken)` returns its stable owned completion. `AbortUserBashAsync()` initiates physical cancellation of every captured controller before joining any completion, and returns the same task and fault until fresh admission. `IsUserBashRunning` and `HasPendingUserBashMessages` expose the two distinct ownership states.

`IUserBashExecutor.ExecuteAsync(UserBashExecutionRequest, UserBashProgress, CancellationToken)` must return its original physical operation task, including cleanup. `UserBashProgress` returns an original accepted progress completion. The session independently joins every accepted observer task even when execution returns early or throws synchronously. Correlation IDs appear on progress updates; the persisted message retains the original command and optional exclusion flag. Exit codes, cancellation and truncated output are finalized result fields.

The contract requires sanitized scalar text, canonical absolute working/spill paths, bounded deltas, and a finalized output tail within 2,000 lines and 50 KiB. It validates host output rather than sanitizing raw bytes or acquiring storage. An actual host must preserve its captured command/image/environment/spill authorization, native output/storage caps and finite timeout semantics separately.

## Cancellation and persistence ownership

Caller cancellation initiates the captured operation's own cancellation source. Abort and close must continue joining original executor, accepted progress, caller-registration disposal and active cancellation callback leases. Fault references remain original; independent failures are retained together. No cancellation wrapper replaces an original task.

Executor and progress callbacks carry an AsyncLocal owner marker. Synchronous cancellation callbacks additionally carry a thread-local session owner stack around `CancellationTokenSource.Cancel()`. This guard survives `Register` restoring an external ExecutionContext and `UnsafeRegister` suppressing it. It rejects own-session wait/abort/close before any self-wait. A stack supports nested sessions without holding the session gate while invoking callbacks.

Completed Bash output remains queued while a provider operation owns the session. A fenced boundary joins all captured composites before flushing. The flush retains queue ownership throughout the exact admitted append/checkpoint task, publishes acknowledged context only after acknowledgement, then removes that exact prefix. Cancellation cannot detach this append. Failed append retains pending messages and marks session fault. Agent finalized messages are refreshed after acknowledgement.

## Required composition hooks

The companion clean automatic boundary is commit `d4972dad2fbad74594a81e745c96cd632d17fc92`. It supplies `_automaticBashBoundaryOwner`, `ThrowAutomaticBashBoundaryLocked()` and `BeginUserBashBoundaryAsync(TaskCompletionSource)`. This slice supplies `CaptureUserBashCompletionsLocked()` and `FlushPendingUserBashMessagesAsync()`. These private APIs intentionally require composition; this slice alone is not a buildable completion of the shared session integration.

The shared session owner must:

- Call `ThrowUserBashSelfWait()` first in `ThrowConfigurationSelfWait()`, including idle wait, admission stop and disposal paths.
- Capture Bash completion composites under the session gate for idle wait and admission stop.
- Start `AbortUserBashAsync()` before joining provider ownership during close; independently join abort and every captured composite, then flush pending records before releasing storage/runtime resources.
- Fence final provider settlement using `BeginUserBashBoundaryAsync(idle)` before settled observation/idle publication, preserving the provider's original failure alongside boundary/observer failures. The boundary itself must not invoke configuration self-wait checks against its legitimate provider owner.
- Call `ThrowUserBashMutationLocked()` under the gate for idle admission/configuration/context/replacement/name/compaction/activation mutations. Do not put it globally into `ThrowAvailable()` or block legitimate streaming queue input.
- Register `CleanUserBashSessionTests.Cases()` in the reserved test runner using direct original awaits.

## Authored regression groups

Eight groups cover held executor/progress originals, all-operation shutdown after a first failure, synchronous execution failure after accepted progress, throwing cancellation callbacks, external `Register` callbacks, external `UnsafeRegister` callbacks, held append acknowledgement through close, and deferred assistant/Bash message ordering with original command/exclusion semantics. Fixtures use an explicitly volatile in-memory session storage and synthetic provider/executor capabilities. They do not launch a process or claim disk durability. All groups remain **UNEXECUTED** and runner registration remains root-owned.

## Source provenance

The fresh checkout was materialized only from verified authorized R265 main, R277 core, R279 independent resource host and R283 corrected resource/core bundles, following their explicit prerequisite refs. No ownership override was used. The prior override-derived clones, patches and compositions remain quarantined; no affected source or patch bytes were read, copied, compared or applied in this reimplementation.

Intended Bash behavior was read from the parent-pinned Pi v0.99.1 upstream snapshot, commit `d86654abb8862e201933517d6f1fce9f88dd117f`, `agent-session.ts` execute/record/abort/deferred-flush sections and `bash-executor.ts`. The local snapshot has no Git metadata, so the commit identity is a parent-provided pin rather than a locally verified Git HEAD. Physical file hashes are recorded in the handoff.

All 84 protected physical lock files match the authorized R258 immutable manifest. The known PiMessages checkout newline normalization was repaired only from the verified authorized immutable postimage. Staging that path produced no index change. No project, package or trust configuration was changed.
