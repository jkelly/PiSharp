# Phase 4 Sessions

## Objective and boundary

Deliver Pi-compatible CLI sessions with safe append, reopen, branching, context projection, context edits, compaction and migrations. Sessions must remain usable after interrupted writes, cancellations and process crashes without accidentally rerunning shell/file side effects. `PiSharp.Sessions` owns persistence and pure projections; `PiSharp.CodingAgent` coordinates those projections with the runtime. The active session manager is the authority for finalized model context.

The CLI baseline uses JSONL v3 trees. Pi's newer durable runtime and SQLite packages are a separate surface, not a replacement storage mandate. Native PiSharp metadata may need a separate export, but native data must never be silently dropped from a supposedly lossless conversion. All filenames/types below are proposed implementation targets.

## Entry prerequisites

- P3-01 has frozen finalized-message, awaited-event, generation and completion contracts; P3-02 is available for integration
- Phase 1 has a pinned session corpus, explicit ownership of migration fixtures and a compatibility-export definition
- Phase 2 exposes usage, model identity, token estimates and a deterministic summarization provider
- PiSharp's separate home-directory policy and cross-platform lock/flush expectations are approved

Begin the codec, tree model and migrations before Phase 3 is complete. Do not complete storage integration until cancellation and ordered commits are verified.

## Baseline versus PiSharp decisions

**Verified baseline:** A session header is separate from tree entries; entries link through IDs and parent IDs. JSONL v3 includes messages, model/thinking changes, usage, compaction, branch summaries, custom state/messages, context edits, labels and session information. Entry timestamps and nested message timestamps have different formats. System prompt/tool changes are messages. Custom state is excluded from model input; custom messages can contribute context. [S41]

**Proposed durability:** Preserve original imported bytes, serialize writers, use documented durable checkpoints and produce explicit recovery diagnostics. Unknown JSON is retained as inert data. Strict detection of damaged records and non-destructive repair are PiSharp hardening; do not imply upstream already provides the same guarantees.

## Work packages

### P4-01 Define the JSONL codec and complete record inventory

**Effort:** 2–3 days. **Depends on:** Phase 1 corpus and P3-01.

**Work:** Add proposed `PiSharp.Sessions/Serialization/SessionJsonCodec.cs`, `SessionRecord.cs`, `UnknownSessionRecord.cs` and per-entry DTOs. Keep wire DTOs separate from native APIs. Preserve absent versus explicit-null fields, nested opaque metadata and unknown fields. Treat identifiers as compatible strings rather than requiring every imported entry to be a GUID. Preserve provider-specific signatures and usage through the message types from Phase 2. Define accepted UTF-8/newline behavior and hard parsing limits that produce clear errors rather than partial silent loads.

**Fixtures and evidence:** `SessionCodecTests.cs` and `compatibility/sessions/v3/` include every known entry, optional fields, unknown fields at several nesting levels, future record types, unknown usage kinds, custom messages with text/images, old messages without a leading system record and opaque provider content. Compare normalized JSON structure after round-trip; require original-byte preservation for the untouched import copy, not byte-identical reserialization of edited JSON.

**Done when:** All known records round-trip with preserved meaning, unknown records remain exportable and no serialized CLR type name or delegate is needed to restore a session.

### P4-02 Build the append store locking and recovery reader

**Effort:** 3–5 days. **Depends on:** P4-01.

**Work:** Add proposed `Storage/JsonlSessionStore.cs`, `SessionWriterLease.cs`, `DurableCheckpoint.cs`, `RecoveryScanner.cs` and `RecoveryReport.cs`. Permit one writer per session, with a documented in-process queue and cross-process lease/locking strategy. Rebuild indexes from the log; caches must not be authoritative. Separate append acceptance, flush completion and durable checkpoint acknowledgement. Reserve IDs in the writer boundary, and publish committed state only under the documented failure rules.

**Recovery policy:** Distinguish a valid final line without a newline, an incomplete final UTF-8/JSON fragment, an invalid header and malformed data in the middle. Preserve original bytes and the valid prefix. Never silently skip a middle record and present its descendants as a complete transcript. Offer an explicit repair/copy path with the byte range and records affected; do not automatically discard a tail. A damaged or future-format file can be inspected read-only when safe.

**Fixtures and evidence:** Crash/fault injection at append, newline, buffer flush, durable flush and checkpoint return; disk-full/permission errors; two competing writer processes; stale lock recovery; empty/oversized files; truncated multibyte characters; malformed middle records. After reopen, compare the recovered prefix and diagnostics to an expected oracle. Verify repeated recovery is idempotent and never mutates the source.

**Done when:** The documented durability guarantee is backed by fault tests on each supported local filesystem/platform. Claims do not extend automatically to network shares or every power-loss scenario.

### P4-03 Implement tree navigation branch projection and state replay

**Effort:** 3–5 days. **Depends on:** P4-01; integrate P4-02 when ready.

**Work:** Add proposed `SessionTree.cs`, `SessionManager.cs`, `SessionProjection.cs`, `BranchNavigator.cs` and `SystemMessageReplay.cs`. Support active-leaf navigation, multiple roots, ancestry lookup, labels, session names and branch-specific model/thinking selection. Project only the selected ancestry. Replay system sections and tool declaration changes without flattening away their history. Keep full history, visible history, model input and accounting projections separate.

**Fixtures and evidence:** Branch at root/middle/leaf; sibling branches with different model and system state; detached roots; missing parents; duplicate IDs; cycles; clearing a label; session rename; an empty/in-memory session. Compare projections to the pinned reference for valid files. Malformed graphs must terminate with deterministic diagnostics. Property tests assert ancestry order, no sibling leakage and stable projection after reopen. Metadata totals must not disappear because context has been compacted.

**Done when:** Every leaf in the fixture corpus produces the expected model context, state and tree metadata without rewriting abandoned branches. Unknown record types retain their tree identity while contributing no executable behavior.

### P4-04 Implement branch-relative context edits

**Effort:** 2–4 days. **Depends on:** P4-03.

**Work:** Add proposed `Context/ContextEditProjector.cs`, `ContextEditValidator.cs` and manager APIs. Implement omission and content replacement using append-only edit entries. Validate supported target types and preserve target identity, role, provider metadata and billing history. Do not mutate raw messages, exports or history-search data while changing future model input. Serialize the exact replacement object shape instead of inferring it from prose examples. Apply only edits in active ancestry; a later applicable edit wins. [S41, S42]

**Fixtures and evidence:** Multiple edits to one target, branch before/after an edit, sibling-only edits, omitted tool/assistant/user/custom contributions, string-to-text-block normalization, replacement images, absent targets, invalid system-message targets and compaction interaction. Test target edits both before and after a compaction boundary using pinned expected projections. Validate tool-call/result pairing at the provider projection boundary; do not silently send malformed tool histories.

**Done when:** Hashes of original records and accounting remain unchanged while context snapshots reflect only the selected branch's edits. Export and model-context views are explicitly different operations.

### P4-05 Implement non-destructive imports migrations and exports

**Effort:** 3–5 days. **Depends on:** P4-01/02/03.

**Work:** Add proposed `Import/SessionImporter.cs`, `Migrations/V1ToV2.cs`, `V2ToV3.cs`, `MigrationReport.cs`, `Export/PiCompatibleExporter.cs` and `NativeSessionExporter.cs`. Import into PiSharp's home or an explicit new destination; never write back to Pi's original location by default. Migrate a copy, retain the source and record its hash and format version. Preserve reference rewrites and repeated-import behavior. Reject unsupported future write formats while preserving a safe inspect/export route.

**Verified migration points:** Legacy v1 entries gain tree relationships and compaction boundary references change from indexes to IDs; v2 `hookMessage` becomes the v3 custom role. [S42] These are the minimum known transforms, not permission to discard unrecognized legacy fields.

**Fixtures and evidence:** v1 with/without version, v1 compaction indexes, v2 custom hook messages, already-v3 files, future headers, unknown fields, repeated import, source/destination collision, path traversal-like session IDs and interrupted migration. Re-import/export the same file without multiplying entries. Open a PiSharp compatibility export with the pinned Pi reader and compare branch projections. Require explicit loss reporting for native-only state and test the native export preserves it.

**Done when:** Migration reports enumerate transformations and warnings; source hashes remain unchanged; round trips preserve every representable record and disclose all exclusions.

### P4-06 Implement compaction and branch summaries

**Effort:** 4–7 days. **Depends on:** P4-03/04 and Phase 2 summarization transport.

**Work:** Add proposed `Compaction/CompactionPlanner.cs`, `TokenEstimator.cs`, `SummaryRequestBuilder.cs`, `CompactionCoordinator.cs`, `BranchSummaryPlanner.cs` and `SummaryCheckpoint.cs`. Separate deterministic selection/serialization from nondeterministic summary generation. Support manual and automatic compaction, selected retained boundaries, repeated compaction, retain-none, split spans, branch common-ancestor selection, system/tool checkpoints, summary usage and extension-provided summaries. Preserve summary text as data; never execute or trust instructions embedded in history or tool output.

Use the canonical context-edited projection as the planner input. Check generation/leaf before committing a slow summary. A failed or cancelled generation must not append a successful compaction checkpoint. Persist read/modified-file tracking and extension details according to the selected compatibility contract. Explicitly characterize token estimates versus authoritative provider usage. [S43, S44]

**Fixtures and evidence:** Controlled token estimates just below/at/above threshold; huge single spans; tool-call/result boundaries; repeated compaction after context omissions/replacements; no retained earlier entry; branch summary from a non-root common ancestor; summary errors/cancellation; stale response after branch switch; old checkpoints lacking system state. Mock summaries for exact projection comparisons; live summaries are evaluated for usable content and valid envelopes, not exact prose equality.

**Done when:** Pure planning fixtures match the baseline, no tool result is stranded by cut selection, and reopening yields the same compacted prompt/tool state. Summarization costs remain included after context trimming.

### P4-07 Integrate canonical commits recovery and settlement

**Effort:** 3–5 days. **Depends on:** P3-09, P4-02/03/04/06.

**Work:** Add proposed `PiSharp.CodingAgent/SessionCoordinator.cs`, `AutomaticRecovery.cs`, `SettlementTracker.cs` and `SessionRuntime.cs`. Commit finalized message events through the awaited barrier, refresh canonical context before requests and run permitted automatic compaction at the correct boundaries. Implement recovery as a fresh provider attempt over repaired context, never replaying successful file/shell tools. Distinguish low-level `agent_end` from final `agent_settled`. Handle retry/compaction work before declaring settlement. [S45]

Pin recovery cases and ordering explicitly. Upstream performs context-edit omissions after the completed run's events; a cancelled/failed recovery compaction retains those omissions without appending compaction or forcing an internal retry. [S43] Do not replace this with a generic transport retry that hides history or duplicates effects.

**Fixtures and evidence:** Provider overflow, early length result, length result containing synthetic failed tools, provider rejection after a successful tool, queued steering/follow-up during recovery, failed/cancelled summary, awaited run-end listeners, a last-second queued message and branch replacement during retry. Assert persisted history, billed usage, request count and event order together. Prove one accepted public operation settles once after all automatic work and required listeners finish.

**Done when:** The SDK returns only at its documented completion boundary; RPC/TUI consumers can reliably distinguish continuing recovery from idle. All stale generations are barred from appending to the new active session, while old operations can still finish cleanup.

### P4-08 Add session lifecycle metadata and extension state adapters

**Effort:** 2–4 days. **Depends on:** P4-03/05/07.

**Work:** Expose proposed create/open/resume/list/fork/clone/import/export APIs, read-only session views and an in-memory implementation. Recreate cwd-dependent services and rebind subscriptions on replacement. Provide pagination/cancellation for session listing without parsing every image body eagerly. Preserve known header metadata and parent-session links. Supply namespaced JSON state helpers using extension ID, record kind and schema version without altering the compatible wire record shape unnecessarily.

**Fixtures and evidence:** Same session ID in separate stores, malformed names, cwd grouping across Windows/Unix paths, fork versus in-file branch, clone versus selected-branch export, missing parent-session path, cancelled listing, extension disabled on reload and two sibling branches with different custom state. State-only records must not leak into model input; hidden custom messages must still follow their specified context behavior. Exercise replace/dispose/rebind repeatedly. [S45]

**Done when:** Embedders and the coming CLI use one lifecycle API rather than constructing inconsistent stores. Phase 6 receives an explicit branch-aware state contract and errors for incompatible state versions.

### P4-09 Add repair UX contracts and data safety operations

**Effort:** 2–4 days. **Depends on:** P4-02/05/08.

**Work:** Define proposed `SessionHealthService.cs`, `RepairPlan.cs`, `SessionDeletionPlan.cs` and user-facing diagnostics for Phase 5. Prepare inspect/repair-copy/export-before-repair flows with source, destination, affected byte range and recoverable records. Session deletion must use the selected recoverable-trash strategy where supported and respect active writer leases. Do not conflate clearing model context with deleting history. Document where copies, backups, temporary exports and shell-output references live and how users can remove them.

**Fixtures and evidence:** Locked session deletion, unavailable trash integration, externally removed session, partial export, interrupted repair, insufficient disk space, restoration of a repaired copy and cancellation before an irreversible step. Inspect imports containing secrets to verify diagnostics do not echo message content or credentials unnecessarily. Test that export does not automatically dereference or upload paths found in a transcript.

**Done when:** Every corruption/deletion failure has a safe next action; no repair path destroys the sole source. Platform-specific recovery limits are visible rather than hidden behind a generic success result.

### P4-10 Produce the session interoperability and durability report

**Effort:** 2–3 days. **Depends on:** all Phase 4 work.

**Work and evidence:** Produce proposed `compatibility/reports/phase-4.json`, `docs/compatibility/sessions.md` and `docs/operations/session-recovery.md`. Include source/PiSharp revisions, corpus hashes, platform/filesystem details, migration reports, reference-reader results, projection snapshots and crash-injection outcomes. Demonstrate create → tools → compact → branch → context edit → export → reopen → resume, including an interrupted append recovery. Run the same flow with Node absent from the PiSharp environment.

**Exit gate G4:** Every mandatory record kind and known migration has passing evidence; valid v3 import/export and branch-context replay work with the pinned reader; unknown data survives; crash recovery preserves acknowledged history under the documented durability model; original Pi data remains untouched; recovery never reruns completed side effects. Settlement, stale-generation, accounting and cancellation tests are blockers. The phase cannot pass by demonstrating only linear chat reload.

## Dependencies effort and parallelization

**Total:** 26–45 engineer-days, including codec/projection tests, recovery fault injection, interoperability and review. Ranges assume the pinned corpus and provider/runtime abstractions exist. OS/filesystem-specific durability work or a changed compatibility scope can increase them; this is not a calendar commitment.

Critical path: P4-01 → P4-03 → P4-04 → P4-06 → P4-07 → P4-10. The append store/locking lane P4-02 and migration/export lane P4-05 can run alongside pure projection work. P4-08/09 can overlap once lifecycle and recovery contracts are fixed. Phase 5 can build session selectors and RPC DTOs against stable read-only interfaces; final end-to-end acceptance waits for G4. Phase 6 can implement native state helpers against fixtures, then run real extension lifecycle tests later.

## Main risks and early decisions

- **A linear transcript masquerading as a tree:** Require all-leaf projection fixtures from the outset; keep raw history and active context separate
- **Quiet data loss:** Never silently ignore a malformed middle record, discard unknown fields or call a lossy export lossless
- **False durability promises:** Define which flush acknowledgement survives which failure; validate local OS/filesystem targets and disclose unsupported storage
- **Compaction changes meaning:** Preserve system/tool checkpoints, context edits, retained boundaries and call/result pairs, with deterministic selection tests
- **Duplicate side effects during recovery:** Replay state and retry provider work only; never infer that a cancelled or missing tool result proves the side effect did not occur
- **Plugin state on the wrong branch:** Rehydrate from explicit active ancestry, invalidate stale contexts and keep state serialization independent of CLR types
- **Future upstream formats:** Unknown version/record preservation is not proof of semantic support; permit safe inspection without guessing executable meaning

## Sources

All links target the immutable baseline SHA unless stated. Selected raw files were read through the equivalent `v0.99.1` tag where the web fetch of the SHA URL was unavailable; Phase 1 should retain hashes when importing these into the reference corpus.

- **S31** [Agent README and lifecycle contract](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/README.md)
- **S32** [Agent loop implementation](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/agent-loop.ts)
- **S33** [Built-in tool inventory](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/index.ts)
- **S34** [Edit schema and implementation](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/edit.ts)
- **S35** [Per-file mutation queue](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/file-mutation-queue.ts)
- **S36** [Shell execution and structured output](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/bash.ts)
- **S37** [Shared truncation implementation](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/truncate.ts)
- **S41** [CLI session format](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/docs/session-format.md)
- **S42** [Session entry definitions migrations and projections](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/session-manager.ts)
- **S43** [Compaction and recovery reference](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/docs/compaction.md)
- **S44** [Compaction implementation](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/compaction/compaction.ts)
- **S45** [Coding agent SDK session authority and settlement](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/docs/sdk.md)
