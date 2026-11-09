# Core gap implementation independent review

Reviewed 2026-11-08 in `E:/AI/PiSharp/gap-implementation` against baseline `14dd34b369fba6d236684da6dfc8a39a03263321`. Requested assignment: gpt-6.1-sol/high/Ultrafast; effective tier unverified. This is a static read-only source review, except this report. No builds, tests, restores, live provider calls or commits were performed; I13 owns the serialized validation queue.

## Status

The implementation reports for I01-I05 and I08 describe completed source awaiting centralized runtime validation. Their authored test cases are evidence of intended coverage, not evidence of passing execution. No P1 defect was found. One confirmed P2 defect affects OAuth Anthropic thread continuation after tool replies; the owner was notified with the trigger and fix below.

Follow-up source verification: I02 corrected the OAuth projection capture and added the authenticated tool-roundtrip case. I03 confirmed and corrected the reference-only seen-set behavior in both walkers, with exact term-frequency regressions. Both original observations below are now resolved in source; runtime evidence remains with I13.

## Resolved in source — P2: OAuth reply hashing used the caller projection instead of the effective projection

- Location: `src/PiSharp.AI/Protocols/AnthropicMessages/AnthropicMessagesAuthenticatedRequestFactory.cs:33` (capture) and `:102` (effective projection); consumed by `AnthropicMessageThreads.cs:74`.
- Trigger: create the normal authenticated OAuth provider with default `OAuthProjection=false`, an active canonical tool named `read`/`bash`/another Claude Code mapped name, and a successful tool-call reply. The authenticated factory correctly overrides its real request projector to `OAuthProjection=true`. The mapper returns the canonical tool name (`Read` becomes `read`), but `_threadProjection` still has false.
- Effect: `Complete` hashes the assistant reply with `name:"read"`; the next real OAuth request projects the same reply as `name:"Read"`. The prefix cannot match, so it starts a full new thread after these tool turns instead of continuing. This is a finished claimed F08 path, not merely missing in-flight work.
- Suggested fix: retain the effective projector options, including `OAuthProjection=authentication.UseOAuthProjection`, for thread completion. Add an authenticated OAuth tool-call/reply/next-request case asserting `continue` and the suffix. Notify root and I02 owner; no runtime test was run by this reviewer.
## Resolved in source — inline target suppressed the first referenced visit

- Location: `src/PiSharp.Contracts/Json/LocalSchemaReferences.cs:81` and `src/PiSharp.Durable.Tools/ToolSearch/ToolSearch.cs:90`.
- Trigger: a tool schema containing `{"properties":{"a":{"description":"needle"},"b":{"$ref":"#/properties/a"}}}`.
- Current behavior: both new walkers insert every inline schema into `seen`. By the time `b` references `a`, it is skipped; `needle` occurs once in search text.
- Frozen upstream `packages/coding-agent/src/extensions/tool-search/tool.ts` initializes `seen` with the root and adds only referenced targets. The same schema emits `needle` twice (ordinary inline visit, first referenced visit), while further references to `a` are suppressed. This changes term frequency and can change BM25 rank.
- Suggested fix if parity is intended: initialize `seen` with root; only add/check a node when reached by reference. Preserve ordinary inline traversal and the existing depth bound. Add this inline-plus-reference case to both search test suites. Owner clarification requested, since the I03 report currently claims unchanged ordinary term frequencies but does not declare this specific divergence.
- Severity: minor correctness/parity discrepancy; not an execution failure. Do not block release solely on this before determining whether the divergence is deliberate.

## Coverage read

- I01: root identifier base and indexing, coercion entry bases, synthetic annotation resource base, explainer branch refs; strict `Validate`, equality, recursive schema and typed schema APIs. Compared with underlying compiler and existing caller usage.
- I02: non-strict `$defs`/`definitions` in normal, preferred strict fallback and inline declarations; thread eligibility, request metadata, header beta selection, per-session generations and bounded eviction, retry request ownership, composition with strict fallback, post-cleanup completion, fallback-model exclusion, replay bypass and cache-warming callsite. Checked managed effort markers and tool-call projection against the prefix approach. Read authored tests and frozen upstream thread implementation.
- I03: generic local pointer decoding, JsonElement and Durable walker consumers, cyclic/reference visit termination, codemode object/union/const summaries and fallback rendering.
- I04: Completions empty-tools gate and metadata precedence, nonnull assistant history independent of synthetic insertion, cache-key modes across Completions/Responses/Azure, native Responses metadata binder, session-affinity header spelling and sampling-last preservation. Existing affected tests and new matrix inspected.
- I05: shared formula and all seven consumer replacements; Anthropic/Bedrock thinking expansion order retained.
- I08: prompt parser checked against frozen upstream token-start/escaped/unmatched behavior; Unicode head/tail boundary helper and consumers; branch-thinking append placement and existing commit/error semantics.

## Limits

Static source review cannot establish build success, runtime test success, performance qualification, provider acceptance or cross-platform behavior. The source is concurrently edited; this report records the reviewed working state, not an immutable acceptance receipt. Historical fixtures were not altered or treated as newer-source evidence.
