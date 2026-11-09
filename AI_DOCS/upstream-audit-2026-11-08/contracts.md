# Contracts to preserve or extend

All references are to PiSharp main `14dd34b369fba6d236684da6dfc8a39a03263321`. No implementation has been changed. New upstream source is frozen at `7d073328e44337d3292a7db9b0f0308f679c5e97`.

## Schema and Anthropic tools (W02/W03)

- Namespace `PiSharp.Contracts.Schema`, public `JsonSchemaValidator.Compile(JsonNode?, options)` and validator Check/Coerce/error surfaces. `SchemaResolver.RootBase`, `BaseFor`, indexed resources and compiler/coercer must assign a relative root ID exactly once.
- `PiSharp.Agent.ToolArgumentValidation` uses unresolved-ref rejection: valid schemas must not become invalid merely because they use a relative ID.
- `PiSharp.AI.Protocols.AnthropicMessages.AnthropicMessagesRequestProjector.Project(ChatRequest, CancellationToken=default): JsonData`. Non-strict input schema must retain root `$defs` and `definitions` when referenced. Preserve existing strict/require/prefer behavior and root-composition restrictions.
- Shared local-ref graph traversal for `ToolSearch.CreateDocument(...)`, Durable tool search and codemode summaries: bounded pointer traversal, each target once, no remote fetch, unused definitions excluded, malformed/missing refs handled without process failure.

## Provider request contract (W04/W05/W06)

- Add model-compatibility and public option plumbing for `requiresToolsWithToolHistory` (new default false), `requiresNonNullAssistantContent`, and `promptCacheKeyMode` (auto/enabled/disabled). Explicit options, model metadata and defaults have documented precedence.
- Empty tools + historical calls: omit by default; opt-in sends empty array. Existing nonempty tools unchanged.
- Non-null content does not force a new synthetic assistant message; keep existing `RequiresAssistantAfterToolResult` semantics separately.
- Responses session-affinity uses `session-id`. Completions keeps its existing spelling; caller-supplied headers must retain documented precedence.
- One shared simple-request budget function; consumers include Completions, Anthropic, Azure, Google/Vertex, Mistral and Bedrock. Explicit max-token cap and nonnegative output remain consistent.
- Catalogue uses an explicit pin/hash/overlay decision. Fix exact upstream-corrected Azure, OpenCode Claude and Sonnet metadata without silently rewriting historical evidence.
- Both retry classifiers must recognize the same specific cut-short-after-reply phrase, preserve quota exclusion priority, and not retry arbitrary deterministic text errors.

## File tools and session state (W07/W08)

- `PiSharp.Tools.Files.EditPlan.Create(string, ImmutableArray<TextEdit>, string, EditPlanOptions?): EditPlan`: Content preserves untouched original bytes/line endings; diff computed before write.
- Existing read/file adapter methods stay usable. Add optional streaming to select offset/limit without retaining entire files; retain bounded fallback for implementations without streaming.
- Grep uses path-relative matching and rg-generated context rather than unbounded ReadAllBytes caches. Cancellation, result caps, UTF-16 and symlink behavior are explicit.
- `PromptTemplateExpander.ParseCommandArgs(string): ImmutableArray<string>`: apostrophes inside words literal; unmatched quote literal; matching quoted spans and escaped matching quotes supported; empty quoted positional args retained.
- Truncation is scalar-safe while preserving UTF-16-based limits where those are part of the existing contract.
- Persistent session branch prompt records effective live thinking selection as necessary before request execution; cancellation/write failure must not create in-memory-only accepted state.

## MCP and shutdown (W09/W10/W26)

- HTTP SSE accepts CR, LF and CRLF, including split CRLF across chunks. Existing maxMessageBytes and line/event limits remain.
- MCP refresh reuses the stored client/registered redirect URI even when an interactive callback needs a different free port. Existing refresh locks, cancellation and serialization remain.
- `OfflineSessionProfile` shutdown request wakes idle RPC, joins lifecycle ownership and preserves command-response-before-exit.
- Background catalogue refresh ownership must be explicitly attached to process lifetime. Fake credential rotation tests cover persistence before exit; do not introduce a live-token test requirement.
- Shell auth commands compose existing login/logout flows. Package updater returns typed changed/unchanged/offline/warning results; caller cannot unconditionally print successful update.
- MCP provider-auth guidance names provider login/logout; token transmission is already implemented.

## Durable settings and transactions (W11/W12/W13)

- `PiSharp.Durable.HarnessSettings`: virtual getters include `AfterFailedRun?` and `RestartLimit?`. `SessionHarnessSettings` and `AgentTeam.TeamSettings` must forward every non-overridden getter dynamically. Deliberate compaction/extensions/metadata overrides stay.
- `IStorage.CommitAsync(batches, receive, DurableContext): Task<CommitResult>`: semantic refusal may commit a valid prefix; storage/decoding failure rejects the whole transaction, with no receipts. Receipt publication follows durable COMMIT only.
- Invalid stored *record shape* can be syntactically valid JSON. SQLite's json_valid constraint protects syntax, not structural decoding. Fix classification against the C# contract; do not claim the exact invalid-shape fixture has identical upstream JavaScript behavior.
- Bounded statement cache disposes evictions. Keep transactions serialized; cache instrumentation must also be bounded.
- `ITx` gains the existing concrete `GetSubmissionAsync(SubmissionId): Task<SubmissionRecord?>`. Reads are committed-only and obey read-after-write/completed-transaction rules. Adding an interface member requires a documented compatibility decision for external implementors.
- `AgentChange` gains optional clearable per-conversation compaction settings; resolved Agent and stored pi.agent carry it. No SQL table change. Policy precedence and explicit disabled semantics are in the implementation plan.
- `ConversationStreamOptions` and internal `DurableStreamOptions` gain nullable handshake timeout/thinking budgets. Thread through Generation prepared-request JSON, native and host route mapping, summaries and replay. Settings affect next preparation; in-flight persisted request keeps pinned values.
- Existing hosted per-session compaction remains the harness layer, not a substitute for per-conversation settings.
- W14 changes SQL batching internals, not IStorage semantics or schema version. Failure/refusal tests are prerequisites to performance measurement.

## Existing deferrals (W16–W22)

Use individual inventory rows as contracts. Identity propagates through explicit host options to chat, classifier, image, deferred retrieval/cancel; no-attribution default remains. Account routing must preserve provider hooks. Node bridge runtime pin, native fail-stop write behavior, physical terminal modifiers and Chord host/jobs/wire require explicit design milestones. Real-host tests replace artificial host setup only after host lifetime/publication contracts exist. No implicit migration to new unreleased package baselines.
