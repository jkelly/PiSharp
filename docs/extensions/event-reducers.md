# Experimental typed event reducers

The complete pinned event catalog is [event-catalog.json](../../compatibility/extensions/event-catalog.json). It retains all 41 `ExtensionAPI.on` event subscriptions, plus both required public operations of the open extension event bus. The native code in this candidate implements three standalone reducers: input, tool call, and tool result. Its profile is `experimental-input-tool-reducers-0`; it is not an approved SDK ABI or registry/Agent integration.

Owner: `gpt-6.1-sol`, `xhigh`. Source: public Pi v0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`. Existing source inventories, captures, projects, build registrations, and status ledgers remain unchanged. No Node capture, upstream module execution, SDK/network/provider call, package restore, or .NET build was performed by this authoring lane.

## Full mandatory catalog

The following table is generated from the catalog's 41 fixed event rows. It includes lifecycle, provider-stream, UI-prompt, model/thinking, MCP, and session metadata events. A deferred row remains mandatory. The catalog has individual event/result declarations, exact event/subscription/dispatch range hashes, full source file SHA-256/Git blob pins, permitted source modes and context operations, order/scheduling, snapshot, reducer, validation, failure, cancellation, timeout, persistence, reentrancy, fixtures, and actual status for every row.

| Event | Source event type | Source reducer family | Source failure policy | Native reducer status |
| --- | --- | --- | --- | --- |
| `project_trust` | `ProjectTrustEvent` | project-trust | collected-and-continue | Deferred |
| `resources_discover` | `ResourcesDiscoverEvent` | resource-discovery | reported-and-continue | Deferred |
| `session_start` | `SessionStartEvent` | observation | reported-and-continue | Deferred |
| `session_info_changed` | `SessionInfoChangedEvent` | observation | reported-and-continue | Deferred |
| `session_before_switch` | `SessionBeforeSwitchEvent` | session-pre-action | reported-and-continue; a throw is not a cancel decision | Deferred |
| `session_before_fork` | `SessionBeforeForkEvent` | session-pre-action | reported-and-continue; a throw is not a cancel decision | Deferred |
| `session_before_compact` | `SessionBeforeCompactEvent` | session-pre-action | reported-and-continue; a throw is not a cancel decision | Deferred |
| `session_compact` | `SessionCompactEvent` | observation | reported-and-continue | Deferred |
| `session_compact_failed` | `SessionCompactFailedEvent` | observation | reported-and-continue | Deferred |
| `session_shutdown` | `SessionShutdownEvent` | observation | reported-and-continue | Deferred |
| `mcp_servers_change` | `McpServersChangeEvent` | observation | reported-and-continue | Deferred |
| `session_before_tree` | `SessionBeforeTreeEvent` | session-pre-action | reported-and-continue; a throw is not a cancel decision | Deferred |
| `session_tree` | `SessionTreeEvent` | observation | reported-and-continue | Deferred |
| `context` | `ContextEvent` | conversation-context | reported-and-continue | Deferred |
| `context_with_system` | `ContextWithSystemEvent` | full-transcript-context | reported-and-continue; diagnostic does not restore dropped leading system | Deferred |
| `cache_warming_decision` | `CacheWarmingDecisionEvent` | cache-warming | reported-and-continue | Deferred |
| `before_provider_request` | `BeforeProviderRequestEvent` | provider-request | reported-and-continue | Deferred |
| `before_provider_headers` | `BeforeProviderHeadersEvent` | provider-headers | reported-and-continue | Deferred |
| `after_provider_response` | `AfterProviderResponseEvent` | observation | reported-and-continue | Deferred |
| `provider_stream_event` | `ProviderStreamEvent` | observation | reported-and-continue | Deferred |
| `before_agent_start` | `BeforeAgentStartEvent` | prompt-before-agent | reported-and-continue | Deferred |
| `agent_start` | `AgentStartEvent` | observation | reported-and-continue | Deferred |
| `agent_end` | `AgentEndEvent` | observation | reported-and-continue | Deferred |
| `agent_before_settle` | `AgentBeforeSettleEvent` | turn-or-settle-boundary | handler errors reported; invalid preview diagnosed; source dispatcher itself does not commit entries | Deferred |
| `agent_settled` | `AgentSettledEvent` | observation | reported-and-continue | Deferred |
| `ui_prompt_start` | `UIPromptStartEvent` | observation | reported-and-continue | Deferred |
| `ui_prompt_end` | `UIPromptEndEvent` | observation | reported-and-continue | Deferred |
| `turn_start` | `TurnStartEvent` | observation | reported-and-continue | Deferred |
| `turn_end` | `TurnEndEvent` | turn-or-settle-boundary | handler errors reported; invalid preview diagnosed; source dispatcher itself does not commit entries | Deferred |
| `message_start` | `MessageStartEvent` | observation | reported-and-continue | Deferred |
| `message_update` | `MessageUpdateEvent` | observation | reported-and-continue | Deferred |
| `message_end` | `MessageEndEvent` | final-message | reported-and-continue | Deferred |
| `tool_execution_start` | `ToolExecutionStartEvent` | observation | reported-and-continue | Deferred |
| `tool_execution_update` | `ToolExecutionUpdateEvent` | observation | reported-and-continue | Deferred |
| `tool_execution_end` | `ToolExecutionEndEvent` | observation | reported-and-continue | Deferred |
| `model_select` | `ModelSelectEvent` | observation | reported-and-continue | Deferred |
| `thinking_level_select` | `ThinkingLevelSelectEvent` | observation | reported-and-continue | Deferred |
| `tool_call` | `ToolCallEvent` | tool-call | propagates; execution must be blocked by caller pipeline | ImplementedPendingRootExecution |
| `tool_result` | `ToolResultEvent` | tool-result | reported-and-continue | ImplementedPendingRootExecution |
| `user_bash` | `UserBashEvent` | user-shell | reported-and-rethrow | Deferred |
| `input` | `InputEvent` | input-transform | reported-and-continue | ImplementedPendingRootExecution |

The separate `dynamicEventBus` catalog entry retains `ExtensionAPI.events.emit` and `ExtensionAPI.events.on` and their exact source evidence. Source channels are arbitrary strings, rather than a finite baseline event enum. Node EventEmitter starts safe async listener wrappers synchronously; publication returns void and does not await listener completion. Listener failure is caught/logged by the wrapper. This family is mandatory and deferred; the three typed reducers do not substitute for it.

The catalog's source fields come from the immutable [native-surface.json](../../compatibility/extensions/native-surface.json), SHA-256 `11cc23450f834eab5c4d6dfe31b2e8e518be1bb64cdd69b6a9aa1f168b026b4f`, checked against whole pinned public source. Census tests reconcile the full ordered subscription list, event/dispatcher evidence, and both event-bus members. They do not establish TypeChecker visibility, caller timing, or runtime parity.

Source `ExtensionContext` includes UI/mode/cwd, read session and model views, abort/idle/trust/signal/pending/shutdown/context-usage/compaction/prompt operations. It excludes command-only wait/session replacement/reload operations and tool-only nested execution. Project trust uses its smaller dedicated context. Current native reducer callbacks expose only the existing experimental context's owner/generation and operation/session/extension-lifetime cancellation tokens. All wider action, generation, session, provider, UI and command permissions remain explicit mandatory integration work; no generic permission fallback is created.

## Three distinct reducers

Input follows `runner.ts:1511`: handlers run in captured extension/registration order, transforms compose, and `handled` ends propagation. Missing or JSON-null replacement images retain the current image value. A transform that changes neither text nor image reference yields `Continue`; supplying a fresh image array remains a transformation. Callback failure is diagnosed and later handlers run with the prior state. Invalid native proposals are diagnosed and discarded without committing any of their fields.

Tool call follows `runner.ts:1233` and `types.ts:1193`: Pi mutates shared `event.input` in place; native callbacks return a complete immutable `Arguments` replacement. Later callbacks see earlier replacements. Returned source-shaped decision objects replace the previous decision as complete objects; omitted decisions retain the earlier explicit decision. Argument replacement alone does not invent a source return object. The first `block:true` ends propagation. Callback or invalid-proposal failure throws a sanitized `ExtensionEventDispatchException`; no executor runs inside this primitive. The later host adapter must block execution, validate the final argument schema, then evaluate mandatory authorization against that final target. The source performs no post-mutation schema validation; this core authorization remains separately documented hardening.

Tool result follows `runner.ts:1174`: own JSON presence of `content`, `details`, `structuredContent`, `isError`, and `usage` composes patches. Supplied null remains supplied null. Replacing content without supplying structured content removes the old structured content. Replacing both retains the supplied structured value, including null. Omitted fields retain their prior state. Only those five fields reduce state; other returned patch fields are retained in the returned-patch evidence but ignored as state mutations. Complete base result properties, including `terminate`, opaque/unknown values and raw numeric lexemes, survive. Empty/unknown-only patches do not set `Modified`. Callback or invalid proposal is diagnosed and later callbacks continue from the previous valid state.

The result event's `OutcomeIsError` retains the separately provided execution disposition; changing raw `isError` does not rewrite it. Native failure classification lives outside the raw result and will require the normal Agent adapter. This primitive does not perform Agent finalization, image normalization, usage accounting, termination decisions, transcript commits, or nullish after-hook merging. In particular, a source reducer's explicit content/details/null state is not silently converted into a claim about the finalized Agent result.

## Admission, snapshots and cancellation

The constructor requires host `Action<JsonData>` tool-result admission. The authored tests call the existing complete `PiSharp.Agent.ToolResultValueCodec.Read` boundary with this action; the runtime assembly has no Agent or provider SDK dependency. Root owns the test-only Agent project reference. There is no default no-op validator. The host action validates while the reducer keeps the original owned raw values, avoiding number/string/presence rewriting. The currently accepted codec admits text content and null/absent content; source image results remain a mandatory unqualified adapter/admission gap.

All event/patch JSON is strictly re-parsed, including retained `JsonData.FromElement` values created by a permissive caller document. Duplicate keys, comments, trailing commas, invalid scalar text, excessive depth/characters/UTF-8 bytes, and numeric values outside finite-double admission are rejected. A finite admission check does not imply JavaScript numeric equality; admitted number lexemes such as `9007199254740993`, precise decimals, `1.0`, and `-0` remain unchanged. Tool-call decision flags admit boolean/null and reason string/null; typed input transforms require valid scalar text/image envelopes. These validations and the immutable-value translation are a native hardening/syntax profile, rather than invented checks attributed to Pi's permissive casts.

Each family has a distinct `ExtensionReducerHandlerSet`. A snapshot owns an immutable ordered array of callback descriptors with owner/generation/registration identities. Handle removal affects later captures; it does not rewrite a captured dispatch. Added handlers appear in later captures. JSON event values are immutable, so proposals compose only by explicit returned values. Pi's in-place alias side effects with no returned patch are not emulated. Delegates may still close over mutable trusted plugin state; no sandbox or deep plugin-state immutability is claimed.

Source dispatch groups an extension's handlers before moving to the next extension. Native sets likewise group owner/generation registrations, then retain handler registration order. A host can supply a stable nonnegative `ownerDispatchOrder`; it cannot change for that owner within the set. Equal orders use first-owner encounter as a deterministic tie breaker. The default is first-owner encounter in that family, which is a prototype ordering profile and may differ from the host's global extension activation order. The future registry adapter must supply the actual global owner order consistently across families. Interleaved-owner and explicitly reordered-owner tests prevent a flat registration-order broadcast from passing as the source grouping rule.

Owner/generation IDs are metadata here. Full transactional registry activation, owner admission leases, stale-generation rejection, scope disposal/quiescence, and automatic capture at the normal Agent boundary remain mandatory. A captured snapshot is a host-selected primitive, not authority to execute tools or replay callbacks after a generation is revoked.

Operation, session and current handler's lifetime tokens remain separate in the callback context. Their linked callback token is checked before and after the awaited callback and before proposal commit. If one is requested, dispatch propagates cancellation separately from failure and returns no partial successful aggregate. An unsolicited `OperationCanceledException` with no relevant requested token is an ordinary handler failure. Unlike Pi's general input/tool-result catches, native requested cancellation is not swallowed. Cancellation joins admitted cooperative callback work; it cannot detach or hard-terminate an uncooperative in-process callback.

No lock is held across plugin awaits. Per-dispatcher concurrent admission defaults to 32 and logical nesting depth to 8. Excess admission fails immediately, avoiding a semaphore wait on the callback itself. This is an explicit native bounded profile, not proof that unrestricted nested tools/session operations are safe. There is no implicit event timeout or false completion of a hung callback. Lifetime/restart diagnostics remain mandatory host work.

Default admission is 65,536 text characters, 8 MiB JSON characters, 32 MiB JSON UTF-8 bytes, depth 32, 128 input images, and 256 active handlers per family. JSON depth cannot be configured above 64; handler set capacity is capped at 4,096. Host-provided complete codec limits may be tighter. No filesystem, persistence, network, subprocess, provider, or tool execution is exposed by these reducers.

A set retains at most 1,024 distinct cumulative owner/generation ordering groups by default, with a configurable ceiling of 4,096. Removing the last handler retains its owner's position for later re-registration; it does not recycle that cumulative budget. Full owner retirement/reload is separate lifetime integration.

## Authored verification seam

`EventReducerTests.Cases()` returns 13 `(string Name, Func<Task> Run)` groups. Root can concatenate these with existing `RegistrationTests.Cases()` and add a **test-project-only** reference to `PiSharp.Agent`. Existing Program/project/solution/lock files were not edited by this lane. At this freeze the tests are authored and unexecuted; root's actual offline gate and independent review are required before reporting passes.

The groups cover the complete 41-event/two-bus-member catalog; successful/no-result input composition and handled propagation; invalid/failing input; all three cancellation ownership tokens and joined callback work; composing tool arguments/last explicit decisions/first block; hook failure versus cancellation; result redaction/composition; absence/null/precise numeric/opaque/outcome-error preservation; invalid/failing/cancelled result hooks; add/remove-current-snapshot behavior for all three families; duplicate identity/foreign snapshots/bounds; reentrancy/concurrent admission; and strict retained JSON/Unicode/UTF-8/finite admission. Gates use explicit task-completion barriers, without sleeps.

The expectations are authored from exact pinned source and accepted codec contracts. They are not fabricated upstream goldens, and no unchanged whole extension-runner capture is claimed. A genuine runner capture requires a separately inspected exact dependency/source closure and approved hermetic oracle setup before any execution.

P6-02/P6-G, source differential, SDK ABI freeze, full registry/Agent tools/action integration, broader event reducers and all applicable Phase 3/4/5/7 integrations remain HOLD. No required row or future fixture is removed because this initial slice exercises only three reducers.
