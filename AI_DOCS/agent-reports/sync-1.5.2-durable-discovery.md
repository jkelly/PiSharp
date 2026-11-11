# Durable harness/session discovery: v1.5.0 to v1.5.2

Compared frozen upstream `f9a26365568127461c47a07a37dcb42c140a3ac5` to `b4afdddf44e059c67ccf5097e5317d913521b470`; native baseline is `9d2b64f9`. Source comparisons used the supplied local upstream checkout. No builds or provider calls were run by this worker.

## Scope and complete changed-file inventory

31 changed source files are in this assignment. Paths below are relative to `packages/durable/src`; C# paths are relative to `src/PiSharp.Durable`.

| Upstream source | Native mapping | Delta and disposition |
| --- | --- | --- |
| `documents.ts` | `Documents.cs` | Chord `copyJson`/JSON import moves to `/json`; native immutable JSON implementation already has the equivalent boundary. |
| `entries.ts` | `Entries.cs` | JSON type import move only. |
| `types.ts` | `TypesSession.cs`, other `Types*.cs` | Chord subpath imports; adds public `Tx.submission`. Already implemented as `ITx.GetSubmissionAsync`. |
| `harness/agent.ts` | `Harness/Agent.cs` | Chord imports and conversation compaction serialization, validation, resolution. Already implemented. |
| `harness/compaction.ts` | `Harness/Compaction.cs` | Chord imports, conversation policy precedence and selection. Already implemented. |
| `harness/generation.ts` | `Harness/Generation.cs` | Chord imports, conversation threshold/overflow policy; native already passes conversation policy but eagerly reads agent on all failure classifications. Narrow lazy-read alignment planned. |
| `harness/scheduler.ts` | `Harness/Scheduler.cs` | Chord imports and reservation/finalization notification race fix. Native already prevents the corresponding cleanup race by atomically applying the clean decision under its scheduler gate. Preserve implementation; add deterministic regression coverage. |
| `harness/types.ts` | `Harness/Types.cs` | Chord subpath imports; compaction settings in agent/state/change, `websocketConnectTimeoutMs`, `thinkingBudgets`. All public behavior already present. Native stores agent state as JSON rather than a separate AgentState record. |
| `harness/context.ts` | `Harness/Context.cs` | Context type import move only. |
| `harness/define.ts` | `Harness/Define.cs` | JSON type import move only. |
| `harness/harness.ts` | `Harness/Harness.cs` | Context/JSON import moves only. |
| `harness/inbox.ts` | `Harness/Inbox.cs` | Draft/JSON import moves only. |
| `harness/json.ts` | `Harness/Json.cs` | JSON type import move only. |
| `harness/live.ts` | `Harness/Live.cs` | Draft/JSON import moves only. |
| `harness/partial-changes.ts` | `Harness/PartialChanges.cs` | JSON import move and blank line only. |
| `harness/prompt.ts` | `Harness/Prompt.cs` | Context type import move only. |
| `harness/provider.ts` | `Harness/Provider.cs` | Context type import move only. |
| `harness/registry-reader.ts` | `Harness/Registry.cs` | `RegistryValue` moves from Chord `/next` to root; Context remains subpath. Chord API migration belongs to D-CHO; native reader behavior unchanged. |
| `harness/submissions.ts` | `Harness/Submissions.cs` | Context/JSON import moves only. |
| `harness/task-graph.ts` | `Harness/TaskGraph.cs` | Context/JSON/watch import moves only. |
| `harness/tool.ts` | `Harness/Tool.cs` | Context/Draft/JSON import moves only. |
| `harness/transcript.ts` | `Harness/Transcript.cs` | Context type import move only. |
| `harness/usage.ts` | `Harness/Usage.cs` | Draft/JSON import moves only. |
| `harness/util.ts` | `Harness/Util.cs` | Context type import move only. |
| `harness/view.ts` | `Harness/View.cs` | Context/JSON/watch import moves only. |
| `session/cached-storage.ts` | `Session/CachedStorage.cs` | Context/JSON import moves only. |
| `session/forks.ts` | `Session/Forks.cs` | Context type import move only. |
| `session/keys.ts` | `Session/Keys.cs` | Context/JSON import moves only. |
| `session/session.ts` | `Session/Session.cs` | Context/JSON/watch import moves only. |
| `session/transaction.ts` | `Session/Transaction.cs` | Chord imports and removal of “Internal” from submission method documentation; implementation was already public upstream. Native implementation already exposed through ITx. |
| `session/writer.ts` | `Session/Writer.cs` | Context type import move only. |

Adjacent root `index.ts` only moves watch type reexports to `@earendil-works/chord/watch`; no C# namespace/API movement is necessary. Storage, environment, codemode, and tool-search changes are assigned to other workers.

## Exact contracts and existing evidence

### Committed submission lookup

`Task<SubmissionRecord?> ITx.GetSubmissionAsync(SubmissionId id)` reads **committed** storage through `Transaction.Read("submission", ...)`, not staged writes. It returns null for absent IDs, obeys read-before-table-write ordering, and rejects after the transaction ends. A placed input's `Entry` identifies its transcript entry. The native interface, implementation, and `gap.durable.public-transaction-submission` already exist; the test checks existing/missing records, read-after-write rejection and transaction lifetime. No API change is needed.

### Conversation compaction

`AgentChange.Compaction` is `Patch<CompactionSettings>?`: absent leaves unchanged, Clear removes the persisted field, a value replaces the entire settings object. Persistence stores only set fields and copies model overrides. Token counts must be finite safe integers in `[0, 9007199254740991]`, in both general and model fields; invalid input throws `ArgumentOutOfRangeException` without committing the edit. Agent resolution returns stored settings. Task-owned children copy `pi.agent`; forks use the rewindable/as-of document.

For each field, precedence is conversation exact `provider/modelId` override, harness exact override, conversation general field, harness resolved default. For Enabled only, conversation general false overrides the harness model override, unless the conversation model override explicitly chooses a value. Model lookup is ordinal/exact. Threshold, background and overflow consult this policy **before** creating automatic compaction tasks. Manual compaction remains available when disabled. Selection uses the conversation policy's reserve/keep values.

Existing tests: `gap.durable.compaction-precedence`, `gap.durable.compaction-reopen-inherit-replace-clear`, `gap.durable.compaction-disabled-threshold`, `gap.durable.compaction-disabled-background`, `gap.durable.compaction-disabled-overflow`. The first covers all precedence layers including explicit conversation-model enable; the persistence test covers stored copy, fork/child inheritance, reopen, replacement, invalid model token values and Clear. Tests are auto-discovered by Program reflection over methods ending in `Cases`; no manual registration is necessary.

`ConversationStreamOptions.WebsocketConnectTimeoutMs` and `.ThinkingBudgets` already persist/replay in generation checkpoints. `gap.durable.stream-options-reopen-pinned` checks replay uses original values; `gap.durable.native-thinking-budget` and `.native-google-thinking-budget` check native provider translation.

One behavioral delta remains: `Generation` currently calls `AgentAsync` for non-overflow failures, and for overflow after a prior compaction. v1.5.2 only performs this read when `overflow && compacted is null`; that avoids unnecessary registry/document access and its possible failure on a path that cannot compact. Narrowly guard policy resolution with those conditions.

### Scheduler notifications

Upstream swaps its reservation candidates into a local set before any asynchronous planning, then merges undropped candidates (only still live/non-completing) ahead of new arrivals in finally. It takes and clears finalization checks before planning. Thus a waiter whose final dependency ends, or a completing task whose final owned task ends, cannot lose a fresh notification to stale post-plan cleanup. Retained candidates preserve insertion order; exceptions restore candidates.

Native equivalent differs intentionally: `RegionAsync` runs a clean final decision **and its apply callback under the same `gate` lock** used by publication/adoption. `ReserveAsync` drops candidate IDs in that callback, not after awaiting the region. `FinalizeAsync` removes only `plan.Checked` in that callback, not a later clear. A region that observes pending writers waits and re-decides against newly published state. `WaitingOnLocked`'s unkeyed live-membership checks remain safe for this race because no publication can interleave between final decision and apply. Existing header explicitly documents this thread-pool adaptation. Replacing the algorithm only to match TypeScript syntax would add complexity without correcting a demonstrated native defect.

Existing `harness.group-commit.*` and `harness.tasks.publication-at-adoption.*` cases cover gated regions, owner completion, publication ordering and pending reconcile work. Add dedicated deterministic waiter and completing-owner cases with controlled storage and a held scheduler dispatcher: force the last child end to publish while a scheduler pass is waiting/planning, then assert task wait/idle resolves. Preserve atomic decide/apply implementation.

## Changelog accounting

v1.5.2 durable changelog has no additional bullets. v1.5.1 Added: submission lookup and per-conversation compaction accounted above; JSONL read-only is D-STO. Changed: SQLite multirow batched commits is D-STO. Fixed: scheduler wake loss accounted above; `$ref` tool search and malformed-percent codemode are D-SCH. Chord source import moves are packaging/type boundary changes, not dropped runtime work.

## Authorized implementation subset

Root authorized the narrow classifier lazy-read fix and deterministic scheduler race regression after this contract report. Owned edits: `Harness/Generation.cs` and a new durable test file; preserve existing equivalent scheduler semantics. Root performs all builds/runs. Verification remains pending until root executes integrated tests.

Implemented:

- `src/PiSharp.Durable/Harness/Generation.cs`: resolve the current conversation agent's compaction policy only for an overflow with no previous compaction, matching upstream's conditional read.
- `tests/PiSharp.Durable.Tests/Sync152SchedulerCases.cs`: new auto-discovered authored cases `sync152.scheduler.keeps-waiter-notification-during-planning` and `sync152.scheduler.keeps-completing-owner-notification-during-planning`. Both independently hold scheduler dispatch and storage, requeue an existing waiter/holder, stage its last child's terminal commit ahead of a pending parent record write, then require the planning region to hit its unpublished-write gate before storage is released. They check the parent becomes terminal and the already-started idle wait resolves. No sleeps stand in for the interleaving barriers.

`git diff --check` passed for the worktree at the time of this handoff. No build/test execution was attempted, as required; root must compile and execute `--only sync152.scheduler.,gap.durable.,harness.group-commit.` before treating behavioral verification as complete. The new cases have no manual Program.cs registration requirement.

Validation follow-up: the centralized build reported missing Program helper imports and a method-local storage factory reference in the new test file. Corrected by importing `Program` and directly constructing `ControlledStorage(new MemoryStorage())`; the validation worker owns rebuild/execution.

Full-suite follow-up: validation reported the waiter case passing but the completing-owner fixture never reaching its initial expected state. The fixture now publishes and starts the child before separately transitioning the parent, as established completing-owner tests do; this isolates notification behavior from simultaneous child creation/parent termination. `SetupCases` now explicitly verifies the retained v1.5.0 row map's historical pin and version, plus the current v1.5.2 candidate commit/tag/overlay link. No historical map or fixtures were regenerated. Rerun remains owned by validation.

Focused-rerun follow-up: the completing fixture reached its initial state, but its gate assertion did not fire because `WaitForIdleAsync` schedules reconciliation directly on the Session line, bypassing held `Dispatch`; requesting idle before staging the held writes consumed the finalization check. Moved the idle request after the parent rewrite is admitted and recorded the gate-hit baseline before requesting idle. Both reservation and reconciliation are now requested while publication is pending. No production scheduler change was needed.
