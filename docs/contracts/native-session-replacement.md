# Native durable entries and session replacement

This experimental vertical implements a real switch between existing absolute session files in the same workspace. It covers native command contexts, the offline one-shot CLI, RPC, and the interactive frontends. It does not close P6-07, P6-10 or a full phase gate. The pinned compatibility baseline remains Pi v0.99.1, `d86654abb8862e201933517d6f1fce9f88dd117f`.

## Durable custom entries

`IExtensionSessionActionsContext.AppendSessionEntryAsync` writes a normal custom record with `customType: "pisharp.extension-state"`. Its envelope contains `extensionId`, `entryKind`, `schemaVersion` and opaque `data`. The broker supplies the admitted package owner ID; a plugin cannot name another owner through this API. Schema version 1 is currently admitted. Interpretation and future data migration belong to the extension; loading does not rewrite historical records.

The receipt follows the actual `SessionLogStore` append and checkpoint acknowledgment. It includes the physical sequence, offset, length, session identity, attachment generation and selected leaf. A queued write, tool execution event, or successful prompt admission is not that receipt. An uncertain storage failure faults the coordinator and can leave recoverable physical bytes absent from its acknowledged branch. It is not retried or advertised as rollback.

Entry identifiers are bounded at 128 characters, data at 65,536 UTF-16 characters and 262,144 UTF-8 bytes. Existing codec, projection, line, record and total-file bounds apply independently, including an inserted newline when needed. Generated session entry IDs retain the coordinator's Unicode identity policy. Existing callback snapshots also retain their separate 4,096-entry/1 MiB bounds and identifier policy; these are a bounded experimental profile, not general large-session qualification.

## Attachment authority and transition

`ReplaceableAgentSession` owns an initial coordinator and each staged target. An attachment is an exact owner-issued object, containing its coordinator, monotonically increasing generation and lifetime token. A→B→A produces generations 1→2→3 even when both A attachments have the same durable session ID. Retaining a snapshot or old context does not restore write authority.

Switching serializes host writes, reserves the source coordinator, opens and validates the target's selected branch, reserves the target coordinator, and invokes pre-switch handlers outside state locks. Both reservations block direct input, configuration, custom writes, queue mutations and precommit disposal through retained coordinator handles. A target snapshot alone is not an availability reservation. Missing/corrupt targets, missing selected leaves, veto and cancellation before retirement leave A attached and usable, and close the staged writer. Busy execution, another input callback, configuration, custom writes or pending input queues prevent replacement. The originating admitted command may replace its own otherwise idle coordinator without awaiting its own input reservation.

After physical retirement starts, caller cancellation cannot pretend to undo it. The owner closes A's writer, publishes B, releases B's staging reservation, cancels A's attachment lifetime and notifies the frontend before `session_start` for B. An explicit `AgentSessionReplacementNotificationException` retains the committed replacement when notification fails. The old command can use the returned fresh context, while every old context rejects mutations. Retired callbacks are joined separately, permitting the same active command to return to A without a self-wait.

The owner retains at most 128 attachments. Command scopes admit at most 128 actions and own their fresh UI/action scopes until the originating callback settles. Abort cancels staged replacement and the originating input even after B is current. Shutdown cancels attachment lifetimes before joining transitions, aborts retired/current cooperative operations and joins all owned coordinators. Cancellation listeners that throw produce cleanup failure evidence. Arbitrary trusted code or I/O that ignores cancellation can still require a restart; in-process native plugins are not a sandbox.

## Native lifecycle and frontend behavior

The optional `IExtensionSessionLifecycleRegistry` registers typed pre-switch decisions, `Continue` or `Cancel`. Invalid decisions and handler failures reject staging. Existing `session_before_switch` observers are observational and cannot mutate through a lifecycle context. Command replacement returns `IExtensionSessionCommandContext`; ordinary lifecycle callbacks cannot use its command-only switch API. Transition reentry rejects before waiting on the transition's own serialization gate.

The experimental native action features are `session-durable-entries` and `session-replacement`, advertised only with an actual action provider. Existing constructors and callback interfaces remain usable. One loaded approved native owner survives replacements; it is not reinitialized on every switch.

RPC accepts `{"type":"switch_session","sessionPath":"<absolute existing JSONL>"}` with either optional `leafId` or `root:true`. Omission selects the latest leaf. A committed transition emits the additive `session_switched` event containing `sessionId`, `sessionFile` and `generation`; its response identifies the actual current attachment. Subsequent reads and subscriptions use that coordinator. Postcommit input abort reports that replacement already committed, rather than implying pre-admission cancellation. The cooked and terminal frontends support `/switch <absolute path>` and clear attachment-specific presentation state on this event.

RPC readers and interactive input readers are active while startup/session lifecycle dialogs await replies. Ordinary commands wait for startup readiness, while actual UI responses continue to route. Disconnect resolves dialogs before joining callbacks. Late or duplicate replies cannot authorize a later attachment.

One-shot report mode reports and physically verifies the final current path. Print mode preserves its final-assistant contract; handled checkpoints print no invented assistant response. JSON begins with the initial session header, then emits bounded `session_switched` records and moves its actual subscription to the replacement coordinator. These additive events identify B and a return to A. They share existing physical write, flush, record/byte/count, backpressure and fault rules. Output disposal joins every admitted transition before disposing the final subscription, and writer callback self-disposal rejects.

## Evidence and mandatory remainder

`SessionReplacementTests` exercises actual local-file acknowledgment barriers, exact byte limits, Unicode, cancellation delegates, stale A→B→A authority, callback replacement, rollback, uncertain writes, busy factory results, retired callback abort, postcommit lifecycle shutdown and hostile JSON writer disposal. `ExtensionSessionSnapshotTests` adds a cancellation-after-action-admission cleanup barrier. `NativeSessionCheckpointTests` executes the published sample through compiled RPC, one-shot report/print/JSON and cooked CLI with real replies, selected sibling/opaque state, physical reopen, cancellation and process/package joins.

These authored tests are not unmodified upstream differential credit. New-session creation, fork/tree replacement workflows, compaction, before/after-settle, broader event catalog, migration tools, provider registration, SDK/templates/diagnostics, remaining representative samples, cross-platform qualification and all other original clauses remain mandatory. No original scope is waived and no completion percentage is supported.
