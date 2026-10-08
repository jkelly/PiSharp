# One explicit turn integration

The separately named RunWithAbortSettlementAsync opts into the [high-level aborted-work contract](abort-settlement.md): work cancellation still controls acquisition/tools, while authoritative delivery uses an uncanceled settlement token and cleanup precedes aborted assistant commit. It adds AssistantMessageStarted and stamps the current high-level thinking default on final messages. Existing RunAsync behavior below remains unchanged.

Status: bounded P3-02 integration prototype. `TurnRunner` composes the accepted native `ChatClient`/`ChatRun` and `ToolBatchScheduler` for one caller-supplied request. It implements neither a full Agent loop nor full Phase 3 lifecycle parity.

The [Phase 3 plan](../plans/03-agent-loop-and-tools.md) separates finalized assistant ownership, awaited delivery, tool scheduling, continuation and session settlement. The pinned [upstream loop](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/agent-loop.ts) and [Agent README](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/README.md) describe the broader reference lifecycle. This slice only joins the already-tested streaming and scheduler seams; the scheduler's [existing differential projection](../compatibility/scheduler-differential.md) remains bounded.

## Contract

The caller supplies an immutable `ChatRequest`, an awaited `IAgentEventSink`, and optional cancellation. The runner starts exactly one chat run. It drains that run's single normalized reader and forwards each immutable frame, including the terminal frame, inside the new `TurnStreamObserved` event. Every sink call is awaited. This observation is progress, not a transcript commit; it does not supply Pi wire Agent event shapes.

After draining, the runner obtains `ChatResult` and awaits owned run disposal before calling the scheduler exactly once. The scheduler emits the sole `AssistantMessageEnded` commit barrier. No separate assistant-end event is introduced by the runner. If that barrier is delayed, neither preflight hooks nor tools can begin. Stream cleanup also precedes that barrier. The scheduler then retains its accepted ordering, cancellation, failure and termination behavior.

`TurnResult` contains the final `ChatResult`, the source-ordered `ToolBatchResult`, and the chat run's existing optional cleanup diagnostic. A successful text-only response returns no tool outcomes or continuation hint. Non-terminating tool results may return `Tools.ShouldContinue=true`; the runner still sends no further request. Unanimous termination suppresses that hint. No result represents durable persistence or session settlement.

## Failure and cancellation boundaries

Provider failures, malformed streams and unexpected EOF settle through the accepted chat run into normalized error progress and a failed final assistant. Partial content remains available in `ChatResult`. The scheduler observes that failed assistant through its usual barrier and executes no tool calls, including partial calls retained in a failed snapshot.

Caller cancellation during the chat phase cancels the producer. If the sink accepts events with the canceled token, the runner drains the normalized aborted terminal observation, awaits disposal, then propagates caller cancellation before the scheduler is entered. A sink that throws on cancellation follows the same disposal-before-propagation boundary. A progress-sink exception also disposes the owned run and waits for admitted transport cleanup before escaping; no tool preflight occurs.

Once the chat run is disposed and the scheduler has begun, scheduler semantics remain authoritative. Cancellation during its assistant barrier or tools can return a canceled tool-batch envelope if the sink accepts the canceled token; a throwing sink faults/cancels the task. Completed successful effects are preserved. No automatic retry, effect rollback or second provider request is attempted.

No coordination lock is held across progress delivery, provider cleanup, hooks or tools. Cleanup relies on transport and tool cooperation: an iterator whose disposal never completes can keep the turn task pending indefinitely. This slice has no hard process termination, cleanup timeout, recovery or generation fencing. A sink failure itself is separate from the normalized provider failure envelope; successful terminal handling and cleanup diagnostics retain the accepted `ChatRun` behavior.

## Evidence and scope

The offline Agent console suite adds seven gate-controlled integration cases: text success with exactly one assistant barrier; native composition of the frozen genuine all-terminate A/B/C scenario; a provider failure before start; partial EOF retaining text and an unfinished call without execution; caller cancellation with delayed cleanup; delayed cleanup followed by a delayed assistant sink; and a throwing progress sink whose failure waits for cleanup. The continuation-hint case asserts exactly one transport request. No sleeps, credentials, network, live providers or Node are needed. The existing per-case timeout only guards a hang.

The first targeted Windows run on 30 September 2026 restored the new framework-only project-reference graph using the empty `NuGet.Config`, then built the Agent test project in Release with zero warnings/errors. Its native DLL passed 24/24 cases: the 17 accepted scheduler cases plus seven turn-integration cases. CLI home, package cache, temporary and app-data directories were isolated under ignored task artifacts; test PATH contained only the .NET executable directory and Node absence was checked. `artifacts/native/agent-results.json` records these cases and the `one-explicit-turn-integration` scope. Common-helper verification and acceptance are separate lead/reviewer steps.

The genuine tool scenario supplies the captured final assistant, call IDs, arguments, release order and expected scheduler results from the unchanged fixtures. Its native stream frames are synthetic normalized inputs. Matching these composed results does not establish provider emission, raw frame, full Agent event or complete turn-lifecycle differential parity.

The new project reference is `PiSharp.Agent` to `PiSharp.AI`; all dependencies remain framework-only and native. The accepted AI, scheduler, event and contract source stays unchanged. Request preparation, context transformation, system/tool-loadout updates, prompt/continue/queues, turn finalization and explicit continuation decisions, subscriber registries, complete lifecycle events, session generations/persistence/settlement, nested invocation, schema validation and final-action authorization remain open. P3-02 and P3-G do not close from this integration slice.
