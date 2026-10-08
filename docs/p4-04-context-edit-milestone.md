# P4-04 branch-relative context editing

This milestone implements the complete original P4-04 own scope in `docs/plans/04-sessions.md`, lines 58–67. Its mandatory P4-03 dependency and the whole original package remain open until the original dependency gates close. The implementation draft starts at independently accepted P4-05 own-scope candidate `dbf14a1e83afda3ee335d16680feff2898882494`. No full phase gate or completion percentage is claimed.

At initial authoring this draft is unbuilt and untested. Qualification requires targeted development checks, the complete serial native/Commands-Input/genuine-terminal/unchanged-SDK/unchanged-reservation gates, and the parent-managed independent integration reviewer. Runtime configuration metadata is not exposed, so requested Fast, Sol 6.1 Xhigh and Astra Medium are not attested as verified settings.

Development candidate `00054a0f61b9f48447115c1064b391d6d732a57b` subsequently built with zero warnings/errors and passed all 21 targeted groups: six core, five native SDK, four actual CLI, one cooked frontend and five RPC. The first build failure and two authored test lifetime/exception assertion failures are separately preserved; they are not accepted candidates. The genuine source capture also executed at this exact candidate: two fresh children each naturally exited zero with no signal/error, 1,474,860 stdout bytes and empty stderr. Each run records 87 full checkpoints, one reopen, 81 operations, 22 successful edits and 12 rejected active-manager calls. All 2,093 source files, eight dependency closures and 715 qualified loaded-module files remained unchanged. Both raw captures, full stdout/stderr and source own-undefined absences are sealed and consumed by six authored reference groups, each replaying both genuine schedules. Provenance mutation controls reject historical execution relabeling and status-zero child errors. Fixture consumption, full milestone gates and independent review remain pending at this documentation checkpoint.

## Original scope and evidence matrix

| Original requirement | Implementation and meaningful evidence |
| --- | --- |
| Append-only omissions and replacements | Shared `PersistentAgentSession.AppendContextEditAsync` writes one actual checkpoint and replaces Agent history only after acknowledgment; raw file-prefix and independent reopen assertions. |
| Supported target validation | New active-manager validator checks replacement shape before target existence, active ancestry and editable user/assistant/tool-result/custom-message types; missing/header/sibling/system targets fail before author ID/clock callbacks and writes. |
| Original identity, role and provider metadata | Projection changes content only; raw target entries and assistant metadata remain intact across repeated edits. |
| Billing, raw messages, exports and history data remain intact | Full original record hashes/accounting assertions, whole-forest/history projections, exact native export and exported-file continuation. Existing permissive stored-edit replay and P4-03 fixtures remain unchanged. |
| Exact replacement object shape | User/custom strings and all arrays retain the owned wrapper; assistant/tool-result strings become a new content-only text-block wrapper exactly as pinned manager behavior. No inferred prose replacement. |
| Active ancestry and latest applicable edit wins | Repeated replacement/null omission, branches before/after edits and sibling controls through actual source managers and native writer reopen. Null always omits; restoration requires an explicit replacement or selection before the edit. |
| Four editable contribution kinds | Genuine source manager schedules and native role normalization/image fixtures cover user, assistant, tool result and custom message. |
| Compaction interaction | Actual whole-manager schedules and native projection checks for kept/trimmed targets, edits before and after a compaction boundary and branch selection. |
| Provider pairing boundary | Actual Responses and Anthropic projectors/transport boundaries receive edited histories. Responses rejects missing/orphan/duplicate/name-mismatched results before HTTP. Anthropic preserves its existing explicit missing-result error synthesis and rejects orphan/duplicate identities. These provider policies are characterized separately. |
| Context snapshots differ from exports | Model input reflects selected edits; full raw export retains original messages and append-only edit records. Next requests and reopen prove use of edited model history. |
| Native C# SDK integration | Optional `session-context-edits` feature, captured attachment authority, immutable checkpoint/post-edit view and required prospective snapshot admission reuse exact SDK bounds. 4095→4096 control, 4096→4097 rejection, ordinary finite input policy, stored opaque tokens, cancellation and stale authority checks. |
| CLI/RPC/frontend integration | One-shot CLI, explicit native `pisharp_context_edit`, cooked/terminal commands, before-write response bounds, actual checkpoint receipt, get-state/abort/EOF responsiveness and physically joined read/write cleanup. |

## Shared host actions

`SessionContextEditDraft(targetId, replacement)` accepts an owned replacement object containing string or array `content`; C# null and JSON null both omit the selected contribution. The current manager requires an editable target on its selected ancestry even if compaction has removed that contribution from the current model window. The imported record codec and stored replay deliberately continue their existing rules, including inert missing targets and their existing system/summary behavior.

The durable writer, host mutation semaphore and captured attachment generation are shared with session switching. A trusted prospective policy callback executes before physical append effects. Invalid semantic or wire input precedes authoring callbacks. Snapshot policy and response-budget rejection leave the existing writer usable. Cancellation and direct/RPC abort cancel pre-effect work; an admitted physical checkpoint remains owned and awaited. Late cancellation does not deny an actual acknowledged checkpoint. Writer uncertainty poisons continuation under the existing storage rules and requires inspection.

The optional native SDK context returns `ExtensionSessionContextEditAcknowledgment(Checkpoint, Snapshot)`. The snapshot is a new immutable read view, not new write authority. Captured views remain immutable, repeated edits use the same current attachment authority, and replacement rejects the retired context. Read-only stored opaque numbers do not waive ordinary finite plugin input validation or any existing count, identifier, depth, record, aggregate character or UTF-8 bounds.

## Explicit bounded native command interfaces

```
session context-edit --session <absolute JSONL> --workspace <existing absolute directory>
  --target <active entry id> --replacement <absolute JSON file; null omits content>
  [--leaf <id>|--root] [--offline-api openai-responses|anthropic-messages|openai-completions]
```

Replacement acquisition is strict UTF-8/JSON, complete EOF, at most 1 MiB and depth 32. It physically closes before session writer acquisition. Session input remains bounded to 8 MiB and 10,000 records. The selected authored offline runtime binding restores the session model and tools but the editor sends no provider request. No production bootstrap, paid API, credential setup or upstream CLI parity is claimed. Success reports an actual acknowledged checkpoint after owned session cleanup. Delivery or cleanup failure after acknowledgment reports the known effect and directs inspection before retrying.

```
{"id":"edit-1","type":"pisharp_context_edit","generation":1,"targetId":"u","replacement":{"content":"changed"}}
{"id":"omit-1","type":"pisharp_context_edit","generation":1,"targetId":"u","replacement":null}
```

This is a native RPC extension, absent from pinned `rpc-types.ts`. The captured positive generation and explicit replacement are required. The command follows existing command/frame/depth/identifier and output bounds; there is no record selected by serialized input that grants filesystem, model, provider or tool authority. The exact success response is budgeted before writing. Response delivery is owned and awaited after the known checkpoint. `get_state` exposes the native generation and an editing flag only while active.

Cooked and terminal frontends expose `/context-omit <entryId>` and `/context-replace <entryId> <JSON object with content>`. They send exact owned JSON plus the captured frontend generation and preserve the existing multiline `/edit` draft behavior. Invalid edit syntax is displayed locally and grants no write authority.

## Source reference protocol

The new `capture-session-context-edits.mjs` observes the actual unchanged whole pinned SessionManager, with all 2,093 source and eight dependency closures qualified. It preserves the existing two fresh 20-second/8-MiB child bounds, source/dependency hashes, clocks and RNG. Source-generated entry IDs and timestamps may be injected only into the native author callbacks for direct comparison; the source does not receive a clock/RNG shim. Complete raw observations, full records, branches, trees, selected contexts, returned values, byte hashes and physical child joins are retained. No historic golden or capture is rebound to the new candidate.
