# Persistent Agent session coordinator

This native integration joins the stateful Agent, current-version session codec, one real JSONL writer, and selected-ancestry context projector. It is the initial P4-07 coordinator prerequisite. Its durability barriers are native hardening; the new tests are authored integration evidence, not an unchanged-upstream coordinator oracle or complete Phase 4 qualification.

## Creation, ownership, and reopen

`PersistentAgentSession.CreateAsync(path, header, configuration, clock, nextEntryId, options, cancellationToken)` creates a new file at the caller's fully qualified path. The validated v3 header supplies the working directory. No home directory, endpoint, key store, default model, tool registration, or source overwrite is inferred. The actual writer uses create-new semantics. A caller cancellation observed before admission prevents acquisition; after creation is admitted the header and initial model/off-thinking metadata finish their durable checkpoints.

`OpenAsync(path, configuration, clock, nextEntryId, options, cancellationToken)` owns the existing file's writer lease. It projects the selected ancestry before preparing any provider request. By default the physical latest entry is selected. `UseLatestLeaf: false, SelectedLeafId: "id"` selects that branch; `UseLatestLeaf: false` with a null ID selects empty root ancestry. Appends link to the selected context leaf, independently of the physical latest entry. Sibling records remain on disk and in the immutable index.

The historical configuration overload requires restored provider/model identity to match the explicitly supplied runtime configuration when ancestry contains identity metadata. An ancestry without model metadata uses that caller configuration. This overload retains the supplied executable tool list; it does not claim exact restoration of the declared loadout. The registry overload resolves selected identity and active tool declarations from explicit borrowed bindings, with mandatory final-action policy; see [runtime configuration](session-runtime-configuration.md).

The current runtime thinking profile is `off`; another selected thinking value fails before provider acquisition. The context projector now supports stored compaction, branch-summary and context-edit effects on selected ancestry. Their raw records remain unchanged. Generating new summaries, automatic compaction/recovery and additional unsupported conversions remain unfinished integration work.

The coordinator owns its Agent and writer, but does not own caller-supplied transport, tools, hooks, clock, ID generator, or observer objects. These are trusted in-process code. Their own network, filesystem, or process effects are outside this coordinator's implementation. IDs must be unique, nonempty values; duplicate/header identities fail rather than being rewritten. Entry timestamps use the injected Unix-millisecond clock in ISO format. Message bodies retain their original owned JSON, including numeric tokens, signatures, explicit nulls and opaque properties.

## Awaited durable barrier

At input, assistant, and tool-result message-end events, Agent first publishes the exact immutable canonical runtime record. The coordinator's primary sink takes that record, validates the prospective selected projection against configured limits, and appends one message entry. It awaits write, buffer flush, the storage implementation's local file flush-to-disk, and checkpoint acknowledgement before publishing its acknowledged log/context snapshot.

Only after this primary sink completes do ordered public subscriptions run. The accepted Agent/turn/scheduler then advances to tools or a subsequent provider turn. Consequently:

- An input checkpoint precedes provider acquisition.
- The finalized tool-call assistant checkpoint precedes tool effects.
- A finalized tool-result checkpoint precedes the subsequent provider request.
- Aborted assistant checkpoints use the uncanceled settlement path while actual work cancellation remains canceled.
- The final awaited observer holds run completion, idle, and disposal.

Accepted appends are shielded from work cancellation. An abort does not abandon an admitted append or claim an undelivered terminal commit. A canceled work token may stop provider/tool work, while owned cleanup and durable terminal delivery remain awaited. The coordinator requires Agent's `SettleAborted` cancellation behavior; configuring `Propagate` is rejected before file acquisition.

The local storage guarantee is the explicit `LocalFileFlush` profile. These tests use real temporary local files and the actual default storage implementation. They do not qualify every filesystem, network share, power-loss scenario, platform, or cross-process locking behavior.

## Snapshot and failure

`Snapshot.Agent` is received runtime state. `Snapshot.Log` is the last acknowledged immutable storage checkpoint, with `Entries`, `LeafId`, `CommittedByteLength` and `Sequence`. `Snapshot.Context` is the selected acknowledged ancestry with raw runtime `Messages`, canonical `LlmMessages`, model and thinking metadata. The acknowledged log and selected context are published together. `Snapshot.Fault` records the coordinator failure independently of Agent's failure state. `Snapshot.IsConfiguring` distinguishes an idle Agent from the coordinator's reserved configuration checkpoint.

A received message may be visible in Agent state while its append is pending or failed. It does not thereby become an acknowledged durable record. Append failure exposes sanitized `StorageFailure`, `MayHaveWritten` and `DurableFlushCompleted` flags. A checkpoint exception after durable flush can leave complete bytes on disk while the old acknowledged snapshot remains authoritative for this open coordinator. Subsequent prompt, continuation and queue admission fail. There is no automatic append retry, tool retry, truncation or reconciliation.

Run callback faults also close further run admission. Coordinator-generated diagnostics exclude raw rejected messages, IDs, paths, adapter exceptions and credentials. A trusted run hook's exception retains the accepted Agent throw contract; the coordinator separately records a fixed RunFailed classification. Storage and context admission exceptions retain their existing sanitized taxonomies. Explicit close/reopen permits inspection of actual bytes and selected ancestry. Opening never executes a historical tool call; this slice does not provide automatic uncertain-effect recovery.

## Lifecycle API

`PromptAsync`, `ContinueAsync`, `Steer`, `FollowUp` and the independent `SteeringMode`/`FollowUpMode` use the accepted Agent loop and bounded queues. A busy prompt is rejected. Registry-backed sessions support idle-only `ConfigureAsync` for durable model/off-thinking/system/tool-state updates. Prompt/configuration reservations are exclusive, including last end-listener settlement. Session switching, additional thinking levels and further SDK configuration surfaces require later integration.

`Subscribe(IAgentEventSink)` returns an ordered subscription lease. `Abort()` requests cooperative Agent work cancellation. `WaitForIdleAsync` covers the admitted run or configuration update, durable primary sink and ordered observers; its optional token cancels that waiter only. An active run/configuration callback cannot wait for or dispose its own settlement. Concurrent `DisposeAsync` calls share a settlement task, close admission, cancel work, await owned provider cleanup and pending durable commits, then close Agent and storage. Storage cleanup still runs if earlier cleanup faults.

## Executable evidence and remaining integration

`PersistentAgentSessionTests.Cases()` registers eight deterministic groups: real-file tool-turn/reopen history; input barrier; assistant/tool-result barriers; uncertain checkpoint and blocked continuation; selected branch/root behavior; model/thinking/influence admission and writer cleanup; canceled durable aborted settlement with concurrent disposal; and observer/idle/reentrancy barriers. Trusted storage wrappers delegate all actual bytes and flushes to the default real-file implementation, gating only checkpoint return. Gates have test deadlines and use no timing sleeps.

These groups are supplied for the parent-owned executable registration and isolated build. Writing this document does not assert a passed build or independent acceptance.

Summary generation, automatic recovery, branch switching during a run, additional registry/loadout surfaces, full session SDK event parity, cross-process durable recovery, session migration/export and additional thinking levels remain unfinished work. Stored context effects and the new explicit registry configuration path advance these dependencies without claiming complete session compatibility.
