# Native tool-batch contracts

Status: experimental, text-only native contracts for the first runnable scheduler prototype. Names and shapes are not a frozen extension SDK ABI or a complete Pi-compatible tool surface.

The pinned reference is [agent types](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/types.ts#L58-L131), [tool result/executor contracts](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/types.ts#L424-L493) and [preparation/finalization](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/agent-loop.ts#L684-L942), all at commit `d86654abb8862e201933517d6f1fce9f88dd117f`.

| Native value/interface | Purpose |
| --- | --- |
| `ToolDefinition` | Immutable name, executor and per-tool execution mode; registry snapshot rejects duplicate names |
| `ToolInvocation` | Finalized assistant, its original call and original content index; owned `JsonData` arguments |
| `IToolExecutor` | Asynchronous execution with explicit cancellation |
| `IToolHooks` | Awaited preflight block decision and full finalized-result replacement |
| `ToolResult` | Immutable text content, details, error flag, optional typed failure and runtime termination hint |
| `ToolOutcome` | Invocation paired with the finalized result |
| `ToolResultMessage` | Source-ordered native transcript projection; excludes runtime termination/failure metadata |
| `ToolBatchResult` | Ordered outcomes/messages, cancellation state and continuation hint |

Both scheduler modes use one `PrepareAsync` path: reject truncated output, resolve the tool, require a complete JSON object, check cancellation, invoke the before hook, recheck cancellation, then block or allow. `JsonData.Parse` strictly parses syntax; both `Parse` and `FromElement` reject decoded duplicate object properties and own the value. `FromElement` can retain comments/trailing commas admitted by the caller's parser, so ownership alone does not establish strict raw syntax. Boundaries that require it must strictly reparse retained text after charging size, as finalized tool arguments and structured-result normalization do. The scheduler only checks the root object kind. It does not perform JSON Schema validation, argument preparation/transforms, extension reducer dispatch, revalidation or authorization. The AI layer's separate `FinalToolArguments.ParseStrict` boundary also makes no schema/policy promise.

Unknown tools and invalid argument roots produce `UnknownTool` or `InvalidArguments` errors without hooks/execution. Blocked hooks produce `Blocked` and can request termination. Thrown before/result hooks produce `HookError`. Thrown executors produce `ExecutionError`; awaited result hooks see that error and may replace it. Cooperative canceled operations produce `Canceled`. Output-length truncation produces `Truncated`. Error text comes from the exception message and is not a redacted diagnostics contract. Sink failures escape the run task separately from tool failures.

The after hook receives a finalized executor result, including thrown-execution errors, and runs before `ToolExecutionEnded` and transcript events. It is skipped for immediate preflight outcomes and calls canceled before execution. Results from executors/hooks must contain initialized immutable content and nonnull owned details; malformed native results become execution/hook errors. This prototype uses full-result replacement. It does not reproduce upstream's field-by-field patches, absent/null/clear rules or structured-content invalidation, and provides no image content, usage, structured output or progress callbacks. These are explicit gaps.

Termination can come from executor results, blocked preflight results or after-hook replacements. All finalized outcomes must request it; mixed batches continue. The flag remains runtime-only, as in the [reference tool-result projection](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/agent-loop.ts#L922-L936). Native `ToolResultMessage` omits timestamps and is not yet serialized as Pi's `role: toolResult` wire object.

There is no public nested invoker or policy broker and no core-mediated filesystem, process, MCP or codemode action. This interface does not confine an arbitrary trusted executor. Future real actions must enter the complete final-argument validation and authorization path planned in P3-04; passing fake-hook ordering tests cannot establish policy coverage. See [agent batch semantics](agent.md) for cancellation, awaited delivery and evidence scope.
