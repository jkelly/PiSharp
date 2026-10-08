# Native before_agent_start

Status: authored, unbuilt and unexecuted. Independent review and the authorized bounded test window are pending. No original phase gate is closed.

## Pinned source and bounded surface

Target: Pi v0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`. The exact release archive inspected locally has SHA-256 `4d99d3c9ed6db41f88ce7ba36d478b06a9386f4e81fa0c1ea3f93e681c99e83b`. Source was read, not executed.

[ExtensionRunner.emitBeforeAgentStart](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/extensions/runner.ts) snapshots handlers, runs them in owner/registration order, collects custom messages, carries each returned systemPrompt into subsequent handlers, and reports callback errors while continuing. [AgentSession.prompt and forced-prompt projection](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/agent-session.ts) append custom messages to history, but project forced prompt text after context handlers without recording the force.

The native API implements that returned-message/returned-systemPrompt subset. Register an `ExtensionBeforeAgentStartHandlerDescriptor` through `RegisterBeforeAgentStartHandler`. The immutable event contains Prompt, Images and the current SystemPrompt. Return an optional typed custom message and/or SystemPrompt. A null prompt means no replacement; an empty string is a real replacement. Missing or JSON-null custom content becomes an empty array. Custom content supports text and user image blocks. Malformed patches produce InvalidResult and retain prior reduced state; callback errors produce HandlerFailed. Diagnostics are delivered after reduction.

The source's rich mutable BuildSystemPromptOptions object, custom prompt renderer, section mutation, selectedTools changes and dynamic ctx.getSystemPrompt method are not implemented by this subset. In-place JavaScript mutation is not exposed. These remain explicit parity work, not accepted or silently approximated behavior.

## Actual host path

`ExtensionAgentBinding.Hooks.BeforePrompt` runs after host input admission and before Agent input events. The CLI passes these hooks to its SessionModelBinding and admits the BeforeAgentStartHandler registration kind using existing package approval. It supplies pure SessionSystemReplay functions to read the current prompt and replay tool declarations. No provider transport, terminal UI, package approval rules or final action authorization is changed.

Each PromptAsync prepares once; automatic turns, queued delivery and ContinueAsync do not rerun before-start handlers. Low-level batched prompts use the last user input for the event. A preparation can append only validated custom history messages; it cannot replace original inputs. The request projection is owned by that Agent/run, retained for continuation, and reset on a new prompt. Bindings can be shared between Agents without sharing forced prompt state.

The forced prompt runs after both context phases. It collapses system messages into a head containing exactly the forced text, replayed current tools and the replayed timestamp. It drops old prompt sections from the request but preserves all canonical system history. Custom messages remain custom through context hooks and are converted to user text/image content immediately before transport.

PersistentAgentSession commits custom messages as custom_message records with customType, content, display and details. Its existing projector reconstructs custom metadata on reopen; the Agent projection retains custom roles through restoration, compaction and recovery, while continuing to convert other source-only roles through the existing projector. Original durable bytes are append-only. Request-only prompt text is never persisted.

Operation, host-session and extension-owner cancellation remain linked during callbacks. Awaited callback/UI cleanup completes before admission leases release. Preparation completes before any input is committed. No native runtime execution has verified these authored paths yet.

## Authored checks

Seven directly awaited before-agent-start contract groups cover durable custom messages and reopen, empty forced prompts after context, ordered snapshots/removal/late registration, continuation and tool preservation, shared-binding isolation, malformed patches, and operation/session/owner cancellation with held cleanup. A directly awaited native before-start CLI test covers an approved zero-tool package, actual offline provider input, durable custom metadata, unchanged byte prefix and a fresh-process reopen.

Required next evidence: compile under the pinned SDK, execute focused contract and CLI fixtures, then existing Agent queue/recovery, session projection and native registration suites within the approved bounded window. These are authored tests, not passing evidence. Full source differential qualification remains pending.

## Custom-tail continuation review correction

Both Agent and AgentLoopRunner admit a validated custom message as the tail of a context-only continuation. The pinned [Agent.continue](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/agent.ts) rejects empty/system-only history and requires queued input for an assistant tail; a custom tail is allowed. The native subset now includes that role without broadening message validation. Two additional authored regressions exercise a durable custom-only reopen and real overflow omission/compaction/internal retry, checking custom preservation, request projection, and no duplicate before-start invocation. Neither regression has been executed.
