# Completions image-producing Agent continuation scenarios

This is a test-only milestone on independently accepted provider-image base `f6f0410aa06aa19f8bba751911e67f976cbd0e6f`. It prepares the remaining native image-producing tool criterion in original P2-08, with P2-02/P2-10 and P3-02/P3-03/P3-04 integration evidence. Shared production ownership remains with the integration lead. These tests deliberately fail until the shared native tool-result representation carries images through execution and canonical message materialization.

## Actual pinned source capture

`capture-completions-image-agent.mjs` executes the unchanged Pi Agent `runAgentLoop` and unchanged OpenAI Completions provider through OpenAI SDK 7.19.0. Upstream is Pi v0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`. The sole bare-package alias resolves `@earendil-works/pi-ai` to its actual unchanged source entrypoint. No export shim, source patch, SDK patch or fixture mutation is used.

The capture checks the existing Completions source lock and original input, the Node 24.19.0 executable, all 230 original lock files, and all 1,723 upstream package TypeScript files against immutable Git blobs. Existing cached OpenAI 7.19.0, partial-json 0.1.7 and TypeBox 1.3.27 archives are validated in memory; every installed file is checked against its archive. The capture records 911 actual loaded file hashes. No dependency is downloaded, installed or extracted. Unexpected global network fetches are rejected; source SDK HTTP calls use an injected in-memory response function.

The new native fixture is `fixtures/native/completions-image-agent-source.json`, SHA-256 `56f2ee42059897b387e44a6f9300cd0e05f16a00cd1c40fea3ab364736e0f593` (1,071,596 bytes). It contains complete actual request bodies, source Agent contexts, tool executions, raw results, canonical tool messages and events. It is independent of the original strict Completions oracle and comparator, which remain unchanged.

## Paired scenarios

| Tool output | Selected catalog input | Compatibility | Profiles |
| --- | --- | --- | --- |
| Text control with Unicode/NUL | text; text+image | ordinary; named results with assistant bridge | 4 |
| Image only | text; text+image | ordinary; named results with assistant bridge | 4 |
| Text plus PNG/JPEG images | text; text+image | ordinary; named results with assistant bridge | 4 |
| Two image-producing tool calls | text; text+image | ordinary; named results with assistant bridge | 4 |

All profiles use the same model identity; image capability comes from the actual `FrozenModelCatalog` selected model's owned `input` metadata. Native tests bind that value to provider projection options explicitly. This qualifies that provider-facing seam only; the lead-owned CLI normal/summary capability propagation remains separate and open.

The native scenarios execute `AgentLoopRunner` → `TurnRunner` → actual HTTP/SSE Completions transport → `ToolBatchScheduler` → actual `ToolInvoker` → inert invocation-aware adapter. Final schema validation and policy see the identical immutable prepared action. Tool argument tokens retain `1.00` and explicit null. The adapter uses existing `ToolResult.FromJson`, preserving image blocks and opaque metadata through the released API once the shared contract supports them.

Explicit task gates hold HTTP cleanup, the finalized assistant message sink, and the canonical tool-result message sink. No preflight, authorization or effect may cross the first two barriers, and no continuation request may cross the result sink. Final tool transforms and scheduler hooks must retain content, exact property value tokens, metadata and presence; object property ordering is not an ownership promise. Canonical tool messages are compared to genuine Pi Agent messages. Both complete native HTTP request bodies are compared to genuine SDK bodies. Source/native paired bodies and outcomes are retained even for failed image scenarios.

Four additional native controls cover invalid schema, length-truncated assistant output, malformed provider data and pre-cancellation. Each must produce zero adapter effects and zero policy authorizations; pre-cancellation must produce zero HTTP sends. These are explicit native controls, separate from the 16 captured source scenarios.

## Baseline counterexample and remaining criteria

On frozen provider-image base f6f0410, all 124 existing Agent groups and eight new controls pass. The 12 image-producing scenarios fail. Every first native request matches source; four text continuations match source; every image continuation differs because image output becomes a native `ExecutionError` tool result. Complete bodies and canonical messages are retained in the run report. The full suite therefore reports 132 passed and 12 failed, with an intentional nonzero exit. Do not integrate this test-only branch as a passing feature or infer package acceptance.

The first authoring receipt also contained four invalid text-control assertions that compared whole raw JSON object property order. That receipt is retained; corrected tests compare exact retained property value tokens and presence. The correction does not alter source fixtures, production behavior or the strict comparator.

Lead coordination record `PROVIDER_AGENT_IMAGE_CONTRACT_COORDINATION.json` R2 remains authoritative for shared ownership. Completion still requires:

1. A coordinated coherent owned text/image result representation across `ToolResult`, its codec, validation/normalization, progress budgets, transforms, hooks, scheduler, `ToolResultMessage`, message sink and canonical materializer. Preserve existing native constructors/deconstruction and shared contract identity. A text compatibility view must not silently discard retained images.
2. This exact frozen 16-profile corpus and native no-authority controls passing after the production change, with fresh immutable source/DLL receipts and independent review.
3. Image progress ownership, bounded admission and negative validation controls, cancellation/failure disposition and broader source hook replacement/patch behavior qualified against pinned source; the final-result corpus alone does not qualify those surfaces.
4. Lead-owned `OfflineSessionProfile` admission of Completions image models, selected-capability retention, normal and summary request propagation in both branches, unsupported-API controls, and actual selected-model tests.
5. Coordinated persistence, session, RPC, CLI/TUI and extension consumers preserving images and producing explicit text/image fallback behavior on their owned surfaces. These files are outside this worker's production ownership.
6. Remaining mandatory P2-08 provider/model/auth/transport rows, P2-02 normalization/signatures/loadout/ID/orphan rows, P2-10 complete differential requests/events/results, and complete P3 lifecycle/policy/scheduler criteria. The separate original strict Completions census still has 378 unchanged findings.

All 79 original package scopes and all eight original phase gates remain open. Full native and physical-terminal gates are coordinated by the integration lead. No package/phase acceptance, live API use, credential setup, security setting changes, public push, merge, release, other-worker edits or private PiDotNet copying is part of this milestone.
