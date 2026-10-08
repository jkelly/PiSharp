# Native RPC model/thinking command slice

Authored against base bf9ed062f0bbcccb5e598a7b08269ceecc339d21, with RPC corrections through c7481d3b9466c8d6330e895bcb0c10d71e294008. The separate operational-thinking prerequisite d1810105bfce81a890034c55a73e0894ae8766ad is composed locally as ad67a7007e0de06e02d0dfa69d3b31ce748c24d3. This integration is source-only: no build, test, source-oracle execution or provider call. The prerequisite is under independent correction/review; this composition is not a qualification candidate until the corrected prerequisite is admitted.

## Pinned command contract

Pinned Pi v0.99.1 commit d86654abb8862e201933517d6f1fce9f88dd117f: [RPC dispatch](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/modes/rpc/rpc-mode.ts#L470), [session selection](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/agent-session.ts#L2392), [thinking support/clamp](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/models.ts#L1215). Source files were read locally; no differential capture executed. Existing pin provenance is retained; Git inspection of the read-only oracle remains subject to coordinator-owned repository trust.

All responses retain the existing correlated id/type/command/success envelope. Successful data shapes:

| Command | Input | Successful data |
| --- | --- | --- |
| get_available_models | none | {models:[full model metadata]} |
| set_model | provider, modelId strings | full selected model metadata |
| cycle_model | none | {model,thinkingLevel,isScoped:false}, or explicit null for singleton |
| get_available_thinking_levels | none | {levels:[strings]} |
| set_thinking_level | level | no data property |
| cycle_thinking_level | none | {level}, or explicit null for an off-only operational binding |

Inventory is the host's explicit ordered RpcModelDefinition array. Model selection resolves provider/id uniquely; duplicate provider/id across API identities is rejected at host admission. Complete metadata and configured order are retained. The live CLI currently supplies one selected model; additional live providers are not activated by this slice. Scoped/default model persistence is not added.

Thinking order remains off/minimal/low/medium/high/xhigh/max. Unsupported requested levels clamp upward in that order, then downward, then the first available/off fallback. Malformed non-enum levels are rejected by bounded decoding. The RPC response/clamp order matches the pinned source; capability admission deliberately uses operational native support rather than catalog reasoning metadata alone. In particular, a catalog reasoning model whose actual native binding admits only off returns null on thinking cycling. Upstream's catalog-based supportsThinking check can instead return {level:off}; that native limitation is explicit and is not a full parity claim.

## Operational native integration

Queries use PersistentAgentSession.GetSupportedThinkingLevels(current.Agent.Model). Model selection clamps against GetSupportedThinkingLevels(selected), so an unregistered metadata-only model fails before configuration and a target's real transport/cap controls availability. The session registry remains authoritative at ConfigureAsync admission. No registry guard is bypassed and no transcript-only thinking state is fabricated. Legacy transports without admitted thinking controls expose only off, even when their catalog metadata advertises reasoning.

The separately owned prerequisite carries configured levels through AgentConfiguration, ChatRequest, native request factories and finalized assistant metadata. This patch changes only the RPC model/thinking partial in production; prerequisite Agent, CodingAgent, CLI and provider production files remain byte-identical to the composed prerequisite. The prerequisite reviewer reported an await/span test compilation hazard, frozen enabled thinking under metadata-free explicit off, and false OpenRouter unsupported-effort admission. Its owner is correcting those files. No prerequisite acceptance or native/live parity is claimed here.

## Ownership and verification

The existing dispatcher transition semaphore serializes selection against prompt/session transitions. Queries remain available while streaming; mutations reject active/settling runs and retain durable, closing, pending-input and registered-binding guards. Retired attachments reject commands. Complete successful response encoding is preflighted before mutation. No terminal/shutdown implementation or provider production file is edited by this integration.

Five existing authored RPC groups retain deliberately legacy off-only transports. Their non-off direct configuration control still rejects without append, fault or send. Coordinator qualification of the older off-only candidate found two model-change count failures caused by counting total records without the initial CreateAsync model_change. Task6 owns that separate semantic assertion correction; those two assertion sites are intentionally unchanged in this integration. Qualification must compose its correction as well.

Three new authored integration groups use actual NativeProviderFactory composition with caller-owned intercepting HttpMessageHandlers and inert fixture keys:

- Admitted Responses levels, upward clamping, cycling, configured xhigh mapped to actual HTTP effort=high, final assistant stamp, original response-body disposal and registry-backed durable reopen.
- Selected Anthropic 1024-token binding narrows catalog reasoning to off, clamps both selection and set, preserves null cycling and sends nothing before prompting.
- Metadata-only unbound selection and direct enabled control on the capped binding fail without durable mutation, session fault or send.

All eight groups use the runner's directly awaited ownership-sensitive path. The new fixture observes pisharpRunOwnerSettled through read-only get_state after session idle; its bounded observation yields between probes and disposal joins original dispatcher/session owners before providers/handler. The successful HTTP body is an authored intercepted SSE fixture, not an upstream capture. Existing committed-byte readers retain held-provider checks and shared-read ownership. No test or SDK execution occurred. Corrected prerequisite admission, independent review, approved offline compilation/execution and regression qualification remain required.
