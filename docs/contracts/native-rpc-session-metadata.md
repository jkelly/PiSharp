# Native RPC session statistics and naming

Source-only slice based on canonical 8a686c03ceac85a68ff2d5ecf1579ff12b35b02f. Both commands were recognized by RpcCommandCodec but reached the unsupported dispatch path; no duplicate implementation was found. No SDK, build, test, source-oracle execution or API call occurred. Review and coordinator qualification remain pending.

## Source mapping and wire behavior

Pinned Pi v0.99.1 d86654abb8862e201933517d6f1fce9f88dd117f sources were read locally: [RPC stats](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/modes/rpc/rpc-mode.ts#L593), [RPC naming](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/modes/rpc/rpc-mode.ts#L659), [whole-session accounting and context usage](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/agent-session.ts#L4085), [name serialization](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/session-manager.ts#L1304), [name notifications](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/agent-session.ts#L3844). Authored expectations are not upstream runtime captures or accepted parity evidence.

get_session_stats returns the existing correlated success envelope with data fields, in source order: sessionFile (when present), sessionId, userMessages, assistantMessages, toolCalls, toolResults, totalMessages, tokens {input,output,cacheRead,cacheWrite,total}, cost, contextUsage (when available). SessionHistoryProjector supplies existing whole-file accounting: sibling branches remain included; only raw message records count as messages; assistant/tool-result usage, standalone usage and compaction/branch-summary usage contribute. Total tokens are recomputed from the four components, not the provider totalTokens field. Cost is a number containing accumulated usage.cost.total, not a cost object.

Context usage uses the selected branch and existing SessionCompactionTokenEstimator. A positive finite configured model contextWindow enables {tokens,contextWindow,percent}. Latest compaction without a projected, successful positive-usage assistant after it yields tokens:null and percent:null. A subsequent valid assistant restores the estimate. JavaScript undefined fields are omitted: contextUsage is absent for a nonpositive/missing context window; sessionFile is absent for native volatile storage. The captured immutable acknowledged snapshot supplies counters and context together; streaming observations do not enter committed stats before the existing assistant commit barrier. Queries send nothing and make no durable change.

set_session_name requires a bounded string name. ECMAScript trimming uses the existing RpcCommandCodec.TrimSource character set; empty names return the source diagnostic `Session name cannot be empty`. Each contiguous CR/LF run becomes one space and the result is trimmed again. A success response has no data property. Each accepted request, including a repeated identical name, appends exactly one session_info with fresh native identity, selected leaf parent, millisecond UTC ISO timestamp and sanitized name. Prior JSONL bytes remain unchanged. Existing get_state globally observes the latest file-wide session_info; registry-backed reopen retains it.

## Durable ownership and native limits

There was no public persistent-session name writer. The new PersistentAgentSession.Name partial uses existing Record, projector, SessionLogStore.AppendAsync, commit semaphore, fault handling and original idle reservation. It rejects wrong session identity, retired/replacing/closing/faulted state, input submissions, active processing and pending input. Cancellation is checked before write admission. Once the store admits physical write, its original checkpoint is awaited; admitted cancellation does not abandon it or fabricate rollback. Context/acknowledged log publish after actual acknowledgement. Agent message history remains unchanged because session_info contributes no model message.

The replaceable-session wrapper validates captured attachment authority before and after its mutation gate, links attachment/host lifetimes and retains reentrancy guards. RPC releases its transition semaphore before waiting for that host gate, avoiding inversion with replacement notifications. A prompt racing the release is refused by persistent-session idle admission. Full name acknowledgement encoding is preflighted before effects. Queries and name commands retain startup/closing cancellation and retired-attachment checks.

Native limits are explicit:

- Upstream synchronous naming can occur while streaming; this native writer requires idle admission and rejects active/settling runs and pending input.
- Upstream setSessionName emits session_info_changed to session observers and extensions. This bounded slice adds no such notification topic or dispatch; clients can query the committed name through get_state. Notification parity is unsupported.
- Existing native codec/projector/accounting bounds and integer token profile remain. Unsupported/non-finite costs or arithmetic return an explicit error rather than partial totals or JavaScript non-finite-to-null serialization. Very large histories can exceed native projection limits.
- Model contextWindow comes from the admitted RPC model definition; independent upstream limits-model overrides are not added. Native ID generation, backend durability and JSON escaping retain native behavior. This is not a byte-identical transcript or broad source/provider parity claim.

## Authored tests and exact qualification scope

Six directly awaited rpc.session-metadata groups are authored:

1. Whole-session sibling accounting, all usage sources, response field order, component totals, numeric cost and selected-branch provider context usage, with no write/send.
2. Compaction unknown context, post-compaction valid usage and undefined contextUsage omission.
3. ECMAScript/CRLF normalization, complete physical append/prefix/parent/time, repeated-name append, get_state, real session switch, stale owner refusal and durable reopen.
4. Missing/null/non-string/empty/oversized names, cancellation before SubmitAsync admission, wrong coordinator identity, pending input preservation, stats response-budget refusal and unsupported-cost accounting without mutation/fault/send.
5. Stats during an actually held provider, with naming refused and no extra request or checkpoint.
6. Real held storage checkpoint, cancellation after admission, precommit name invisibility and directly awaited original command/disposal joining the checkpoint.

Tests use an owned FileStream with FileShare.ReadWrite, a bounded exact acknowledged-byte read and before/after idle/sequence/leaf/length checks. Async reads are assigned to locals before comparisons. Output snapshots are synchronized; held providers/storage release in fixture cleanup; dispatcher/owner/stream originals are joined. Small private fixtures are retained for coordinator evidence. No tests have executed here.

After independent review and authorized preparation, run the six groups with PiSharp.Rpc.Tests --filter rpc.session-metadata., then the full PiSharp.Rpc.Tests, PiSharp.CodingAgent.Tests and PiSharp.Sessions.Tests assemblies. These cover dispatcher envelopes/admission/replacement/shutdown, session configuration/extension/context/compaction ownership, and history accounting/tree/compaction estimation. No provider, YAML or packaging production changes require expanded scope from this patch alone.

## Attachment-publication correction after 20fd91f review

Independent source review of immutable 20fd91fff07de03d7ac3f86ca4eb2ca50176fe51 found a P2 interval: ReplaceableAgentSession publishes Current before its awaited AttachmentChanged callback rebinds RpcSessionDispatcher._session. A command captured after publication could validate the new attachment yet read the retired dispatcher's snapshot/path. The corrected partial chooses attachment.Session after validating captured authority, using that same session for the immutable snapshot and the explicit path passed into SessionStats. Fixed-session hosts still use their sole _session. Naming also obtains its captured identity from that selected session; the durable writer, mutation gate, cancellation and transition-release rules remain unchanged.

One seventh authored group, rpc.session-metadata.stats-follow-published-attachment-before-dispatcher-rebind, wraps and preserves the actual host AttachmentChanged callback. It verifies old session ID/path/counts before switching; holds after actual Current publication and old lifetime cancellation, before invoking the real dispatcher callback; queries new-session stats during that interval; then releases and directly joins the stats/switch/callback originals and verifies post-rebind stats. Old and new sessions have different message counts and the new context estimate is independently checked. Bounded Windows shared reads require the new checkpoint to remain unchanged. Finally always releases the gate and joins both originals before restoring the borrowed callback and allowing fixture disposal.

The earlier six-group checkpoint and P2 review are preserved. Seven groups are now authored, zero executed here. Source review of this correction and candidate-pinned approved build/test receipts remain required. The exact regression scope becomes the seven rpc.session-metadata groups followed by the same full RPC, CodingAgent and Sessions assemblies; all unsupported semantics listed above remain.

## Naming notification successor

The original unsupported notification bullet above describes the historical metadata slice. A source-only successor of merged 8f6dd81e now implements post-acknowledgment session_info_changed to the existing session observer bus, RPC output and native extension pipeline. Six new CodingAgent groups plus expanded original held-checkpoint assertions are authored, with zero builds/tests executed here. Native idle admission and source async/lifetime limitations remain explicit. See [notification contract](native-session-info-changed.md).
