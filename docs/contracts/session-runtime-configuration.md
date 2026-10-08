# Durable runtime configuration

This native configuration path connects selected session metadata and raw system/tool history to an explicitly supplied runtime registry. It extends the general Agent/session API beyond the authored offline CLI. The supplied groups are source-linked native integration tests, not a genuine complete AgentSession configuration oracle or a Phase 3/4 exit claim.

## Explicit runtime bindings

`SessionRuntimeRegistry` accepts immutable arrays of `SessionModelBinding` and `SessionRegisteredTool`, a mandatory `IToolActionPolicy`, and limits. Model bindings supply the full native model descriptor, borrowed transport and optional trusted hooks/execution mode. The log stores provider/model ID; the registry supplies API and transport explicitly. A missing binding fails rather than selecting the first model, reading environment credentials or constructing a transport.

Each tool binding supplies an owned raw function declaration and a trusted `IPreparedToolAdapter`. Name, description and object parameters are required; an explicit type must be function. The adapter name must match the declaration. Registry limits bound model/tool counts, accumulated identity/declaration/transcript characters, JSON depth, active tools and tool deltas. Native immutable JSON rejects duplicate properties; unpaired surrogates fail with sanitized diagnostics.

The registry borrows adapters, transports, hooks and policy; it does not dispose, serialize or load them. Captured tool identities are stable even if a trusted adapter changes its Name getter later. The adapter still must faithfully execute the authorized immutable final action. This is a trusted-code boundary, not a sandbox against arbitrary in-process code or a complete JSON Schema validator.

## Selected loadout replay

Resolution reads canonical selected `LlmMessages`, including stored context effects supplied by the context projector. Each system message applies toolsRemoved before toolsAdded. A same-name replacement retains its current position; removal then readdition places it last. These ordering rules follow pinned [getCurrentTools](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/utils/transcript.ts). The upstream coding session restores active names from the resolved system transcript in [agent-session.ts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/agent-session.ts).

Every final active declaration must match its registered declaration. Comparison ignores object property order, preserves array order and exact strings, distinguishes missing/null, and compares numeric tokens exactly. Thus a stored schema containing 1.0 does not bind to a registry schema containing 1. This is deliberate native binding admission, not an assertion that upstream declarationsEqual has identical behavior; that helper compares its own JavaScript serialization. Raw arguments, costs, signatures and schema numbers are never normalized.

Resolved tools share a `ToolInvoker` backed by the explicit mandatory policy. The existing prepare/validate/transform/revalidate/authorize/execute/finalize path remains authoritative. Serialized declarations confer no permission to execute an unregistered adapter. Historical tool calls remain data and are never scheduled merely by opening a session.

The current direct function-tool and off-thinking profile is explicit. Additional exposure modes, namespaces requiring distinct executable identities, custom/grammar tools, extension loadout hooks, strict-schema synthesis, reasoning levels, settings/defaults, auth selection and dynamic registry refresh remain ongoing work.

## Registry creation and reopen

`CreateAsync(path, header, registry, initialModel, clock, nextEntryId, options, cancellationToken)` resolves the explicit initial model, creates the durable header/model/off metadata, and begins with no declared active tools. `ConfigureAsync` adds the initial system/tool delta before a run.

`OpenWithRegistryAsync(path, registry, clock, nextEntryId, options, fallbackModel, cancellationToken)` resolves the selected ancestry model and tool loadout before any provider/tool effect. The optional fallback model applies only when that ancestry has no model selection; it is never used to override a recorded identity. Missing model/tool bindings, mismatched declarations and unsupported thinking fail and release the actual writer lease. Its distinct name preserves original target-typed `OpenAsync(path, new(model, transport, []), clock, nextEntryId)` consumers without overload ambiguity.

The older overload accepting `AgentConfiguration` is retained as a historical compatibility API. It verifies model/off metadata but retains the caller's supplied executable tool list. It does not imply exact declared-loadout restoration.

## Idle update and durable publication

`ConfigureAsync(SessionRuntimeUpdate, cancellationToken)` is available on a registry-backed session. An update optionally supplies a complete model descriptor, thinking value and canonical system-message delta. System content/sections/tool changes remain raw historical messages; they are not folded away or used to rewrite earlier entries.

The method reserves the same exclusive coordinator lifecycle as a prompt. It rejects concurrent prompt/configuration calls and runs still settling their final awaited observers. It rejects retained uncommitted inputs rather than discarding them. `Snapshot.IsConfiguring` exposes this reservation separately from `Snapshot.Agent.IsRunning`. Pending user queue values retain their independent ordered queues.

The prospective parent-linked model_change, changed thinking_level_change and system message records are projected and resolved first. A temporary Agent validates the complete target configuration/history without a provider call. Before accepted append, work cancellation prevents mutation. Once the store accepts the append, its checkpoint is shielded and disposal awaits it.

After acknowledgement, the Agent's new `ConfigureAndReplaceMessages` validates both values and publishes them under one idle state gate. The coordinator publishes live configuration, acknowledged log and selected context together. No callback or I/O runs while the coordinator publication gate is held. Generation snapshots use this state on the next prompt; completed history retains previous provider/model fields.

A failed or uncertain accepted write leaves the old live model/tools/history and old acknowledged context visible. The coordinator records storage uncertainty and blocks continuation. Explicit close/reopen inspects what actually reached disk and resolves that selected state; it does not silently repeat effects. Rejected pre-write configuration leaves a usable session and unchanged bytes.

An explicit model update records model_change even if it names the same binding; unchanged off-thinking alone is a no-op. Higher thinking fails explicitly. Agent and coordinator callbacks cannot wait for or dispose their own active settlement. Concurrent disposal shares one task and waits an admitted metadata checkpoint before closing the writer.

Registry-backed Prompt may carry plain system text, or declaration deltas that preserve the current bound final loadout. A changed loadout must go through idle ConfigureAsync. Steering/follow-up system messages containing tool deltas are rejected at queue admission, avoiding a declaration change beneath a generation's frozen scheduler. Trusted next-turn hooks that introduce an incompatible loadout fail the durable primary barrier before further effects. A trailing system update may require a new user prompt rather than Continue's existing eligible-tail contract; no runnable input is synthesized.

## Supplied executable evidence

`SessionRuntimeConfigurationTests.Cases()` supplies seven deterministic groups:

- Source-ordered remove/replace/readd binding, strict numeric/null admission and bounded/unsupported input controls.
- Atomic Agent config/history publication and busy rejection.
- Gated real-file configuration acknowledgement, prompt/config conflicts, last-listener exclusion, next-generation model/tools/history, and old/latest branch reopen.
- Restored tool final-target policy denial with no adapter effects.
- Real-file checkpoint uncertainty retaining old live state and blocking further runs.
- Model/declaration/thinking/cancellation rejection before mutation, usable subsequent run and configuration callback self-wait guards.
- Concurrent disposal awaiting an admitted metadata checkpoint.

All storage wrappers delegate bytes and flushes to the actual default local-file implementation; only checkpoint return is gated or faulted. Tests use deadlines and no timing sleeps. Parent-owned registration/build and independent review establish their pass status separately.
