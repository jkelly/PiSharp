# AI, agent and catalog discovery: Pi 1.5.2

Compared `f9a26365568127461c47a07a37dcb42c140a3ac5` (1.5.0) with `b4afdddf44e059c67ccf5097e5317d913521b470` (1.5.2), using the frozen `upstream-v152` checkout. Inspected native source in `update-1.5.2`; no source changes or builds performed. Requested worker tier ultrafast; effective tier cannot be verified.

## Findings and implementation split

Most of the substantial 1.5.1 changes were already incorporated by the November 8 gap closure. Five remaining work items should be implemented; connection-cause text is a policy-sensitive intentional difference unless a narrowly safe path is approved. Historical catalog shards/captures must remain immutable; catalog additions/corrections belong in the reviewed projection or a new reviewed successor.

| Work | Owner suggestion | Exact native change and contract | Verification |
| --- | --- | --- | --- |
| A1 persistent strict fallback | AI protocol worker | Add one bounded, thread-safe process-local session set shared by Anthropic and Bedrock. On the existing qualified strict-limit rejection, mark the session **before** retry. Future requests with that session project `strict:prefer` non-strict immediately; `strict:require` remains strict. Undefined session is not recorded. Maximum 10,000 entries, drop oldest insertion; register `SessionResources.Cleanup` callback for one/all sessions. Consult state at request preparation time, not factory construction, because factories are reused. Cover key-auth, authenticated, prepared/hooks Anthropic routes and Bedrock. | First call rejected once, second call same session no rejected request; separate sessions still try strict; no-session retries independently; cross Anthropic/Bedrock session sharing; cleanup restores strict; required tools remain strict; non-limit errors do not mark. Existing `AnthropicStrictFallbackCases.SessionDoesNotPersist` expectation must be updated (currently expects `true,,true`). |
| A2 OAuth identity | Same AI protocol worker | `AnthropicMessagesAuthenticatedRequestFactory` sends `claude-cli/2.1.280`; exact 1.5.2 requires `claude-cli/2.1.322`. Do not change subscription policy or enable login. | Authored request header assertion; API-key projection unchanged. |
| A3 new catalog model + tier corrections | Catalog worker | Add `anthropic/claude-fable-5-2` with complete finalized metadata, and preserve OpenAI `cost.tiers` on Azure OpenAI models. Reviewed projection currently only applies context/compat/thinking corrections, so add a reviewed **cost** correction path. Copy full cost from corresponding pinned OpenAI model, including tiers, for every Azure model with a tiered OpenAI counterpart. Do not alter DeepSeek Azure pricing. Rename OpenAI Codex in both `AI/Catalogs/BuiltinProviders.cs` and `ModelRuntime/Authentication/ProviderAuthCatalog.cs`. | Fable selectable through CLI/AI/Durable catalogs; exact fields and fallback cost; Azure at 272K versus above 272K input, including cache read/write and output; historical bytes untouched; model defaults unchanged. |
| A4 configured catalog refresh | ModelRuntime worker | `ICatalogOverlay.RequiresCredential=false` already avoids stale OAuth refresh for pi.dev, but permits network for **unconfigured** providers. Introduce explicit refresh auth policy (credential/configured; credential default), or give the existing false branch configured gating. On configured path call non-resolving `CheckAuth`, skip when absent, never call OAuth refresh, and pass null `RemoteRefreshContext.CredentialKey`. Personalized catalog path still resolves credentials. Cache restore remains available offline/unconfigured. | Unconfigured pi.dev provider no HTTP; expired configured OAuth pi.dev HTTP succeeds with refresh callback never called; configured API key works; personalized provider still resolves/refreshes; credential check exceptions reported per provider. |
| A5 connection cause detail | Root policy decision; AI protocol worker only if approved | Upstream adds bounded `withCauseDetail`: traverse at most 5 causes, append deepest message/code unless already included. Native intentionally hides arbitrary source exception text (including HttpRequestException inner text), retaining typed diagnostics and exception identity. Preserve that approved sanitization policy: document this as an intentional divergence instead of blindly copying the formatter. A safe future alternative is an allowlisted network-code-only diagnostic at the send boundary. | Retain callback-secret sanitization and original exception identity. Add canceled-auth terminal regression while doing A1; no need to expose arbitrary exception text to claim native cancellation parity. |
| A6 Fable/Mythos family 5.2 | Catalog worker with A3 | Source family rules extend 5.1 to 5.2 for direct/prefixed/dotted/dated names. Native capability selection is metadata-driven: propagate `supportsMidConvoEffort:true`, remove off (`thinkingLevelMap.off:null`), and direct Anthropic system/tool change compat for the new model. Audit any existing or refreshed 5.2 entries on OpenRouter and other catalogs for equivalent family finalization rather than only special-casing the new direct model. | Fable 5.2 direct/prefixed metadata tests; requests send adaptive thinking/per-message effort/drop_block; system and tool changes retain correct behavior. |

### Fable 5.2 finalized contract

Identity `id=claude-fable-5-2`, `name=Claude Fable 5.2`, `type=chat`, `api=anthropic-messages`, `provider=anthropic`, `baseUrl=https://api.anthropic.com`; reasoning true; input text/image; 1,000,000 context; 128,000 output; cost input 8, output 40, cacheRead .2, cacheWrite 10. Family rules add forceAdaptiveThinking, xhigh/max maps and `off:null`; direct provider adds strict tools, mid-conversation effort/system/tool changes, promptCache short 300/long 3600, 32 MiB request limit, 600 images and inherited standard image resize limits. `allowedFallbackModels` is **only claude-opus-5** after `finalizeAll` removes non-mid-effort `claude-opus-4-8`; use Opus 5's pinned cost. Follow actual finalized source, not changelog's simplified fallback description.

### Azure catalog evidence

Native `src/PiSharp.Cli/Models/azure.json` lacks tiers; e.g. gpt-6.1-sol cost is only input=2/output=10/cacheRead=.1/cacheWrite=2.5. Matching OpenAI tier above 272000 is input=4/output=15/cacheRead=.2/cacheWrite=5. All twelve currently identified tiered counterparts: gpt-5.4, gpt-5.4-pro, gpt-5.5, gpt-5.5-pro, gpt-5.6-luna, gpt-5.6-sol, gpt-5.6-terra, gpt-6-astra, gpt-6-luna, gpt-6-sol, gpt-6.1-sol, gpt-6.1-terra. Native response-rate binding already reads tiers via `PromptLengthPricing.ReadCost`; missing metadata is the defect. The five new 1,050,000 context overrides are already in `reviewed-2026-11-08.json`.

## Complete changed runtime/catalog source inventory

Paths below are relative to upstream `packages/`; each changed file is accounted for.

| Upstream file(s) | Native mapping and status |
| --- | --- |
| `agent/src/proxy.ts` | Adds only disableContinuation forwarding. There is no native pi-agent `streamProxy` HTTP implementation in `PiSharp.Agent`; its ChatRequest transport handoff preserves the record. `ChatRequest.DisableContinuation` already exists. Node-host bridge forwarding must be covered by the CLI/bridge owner if that surface exposes streamProxy. |
| `ai/catalog/families/anthropic.ts` | Sonnet 5 temperature change already in reviewed catalog; Fable/Mythos 5.2 extension missing (A6). |
| `ai/catalog/families/deepseek.ts` | CN individual provider Qwen thinking format already in added models. |
| `ai/catalog/providers/anthropic.ts` | Fable 5.2 addition/fallback missing (A3). |
| `ai/catalog/providers/azure.ts` | GPT-6 context corrections already reviewed; tier preservation missing (A3). |
| `ai/catalog/providers/github-copilot.ts` | Microsoft-Decision-1 already in reviewed additions; native classifier uses System One path prefix. |
| `ai/catalog/providers/index.ts` | CN individual registration already in BuiltinProviders/ReviewedModelCatalog. |
| `ai/catalog/providers/opencode.ts` | Existing reviewed corrections cover Go Haiku 5.5 and Zen Fable 5.1/Haiku 5.5/Opus 5/Opus 5.5/Sonnet 5.5 per-message effort/off restrictions. Native simple projector supplies adaptive/drop_block behavior. |
| `ai/catalog/providers/qwen-token-plan-cn.ts` | Constant refactor only; native endpoint already same. |
| `ai/catalog/providers/qwen-token-plan-individual-cn.ts` | Already added via reviewed additional provider, exact ten-model allowlist. |
| `ai/catalog/providers/qwen-token-plan-individual.ts` | GLM 5.3 allowlist already reviewed addition. |
| `ai/catalog/providers/qwen-token-plan.ts` | CN URL constant extraction; native same URL. |
| `ai/src/api/anthropic-messages.ts` | Threads implemented in `AnthropicMessageThreads.cs`, key/auth factories, prepared/hooks transport paths; non-strict root definitions already retained at projector lines 573–574; strict persistence, identity, connection cause gaps A1/A2/A5. |
| `ai/src/api/azure-openai-responses.ts` | promptCacheKeyMode disabled already supported in AzureResponsesRequestFactory. |
| `ai/src/api/bedrock-converse-stream.lazy.ts` | JS dynamic import resolution/override loader; native statically linked provider requires no import loader. |
| `ai/src/api/bedrock-converse-stream.ts` | Persistent strict fallback missing A1; first-request fallback already supported. |
| `ai/src/api/constrained-sampling.ts` | Moves strict-limit recognition into shared module; native recognition currently `ProviderTranscript.IsStrictToolLimitErrorMessage`, reuse for A1. |
| `ai/src/api/lazy.ts` | Marks setup cancellation aborted instead of error. No JS lazy module in native; token-aware native transport and auth setup already classify cancellation. Add canceled-auth regression with A5 to verify final public terminal, not just exceptions. |
| `ai/src/api/openai-codex-responses.ts` | JS abort listener cleanup after backoff. Native uses cancellation-aware `Task.Delay` and `using` linked/deadline CTS; no manual EventTarget listeners. Existing Codex native retry remains applicable. |
| `ai/src/api/openai-completions.ts` | Already implemented no tools-by-default, requiresToolsWithToolHistory, requiresNonNullAssistantContent, promptCacheKeyMode in key factory/projector and model compatibility schema. |
| `ai/src/api/openai-responses.ts` | Already implemented promptCacheKeyMode and session-id spelling. |
| `ai/src/api/simple-options.ts` | Already implemented 15%/4096 margin and 32768 near-limit cap in SimpleOutputBudget; DisableContinuation on ChatRequest passed with records. |
| `ai/src/api/strict-tool-limits.ts` | New process-local capped shared session state missing A1. |
| `ai/src/auth/oauth/load.ts` | JS URL-relative variable dynamic imports; not applicable to static native OAuth classes. |
| `ai/src/cache-warming.ts` | Already implemented `ModelOperations/CacheWarmingPolicy.cs`, including decision economics, TTL, delay, replayable check, and continuation isolation. CLI/harness own 1-token/no-retry scheduling. |
| `ai/src/env-api-keys.ts` | Already mapped QWEN_TOKEN_PLAN_CN_API_KEY. |
| `ai/src/index.ts` | Barrel export cache policy; native public static CacheWarmingPolicy provides equivalent API. |
| `ai/src/lazy-bedrock-provider.ts`, `ai/src/lazy-oauth.ts` | New JS literal import registration helpers; static C# assemblies do not need equivalent chunk APIs. Do not add artificial lazy global registration. |
| `ai/src/models.generated.ts` | CN provider import/registration already in native catalogs. Root owns generated source/version metadata. |
| `ai/src/models.ts` | refreshModelsAuth missing configured gate A4; lazy cancellation as above; no other source delta. |
| `ai/src/providers/all.ts` | CN individual registration already present. |
| `ai/src/providers/compat-schema.ts` | Already exposes promptCacheKeyMode/requiresNonNullAssistantContent/requiresToolsWithToolHistory/supportsMessageThreads in native configuration schema. Responses description spelling is documentation only. |
| `ai/src/providers/github-copilot.ts` | Already implemented ModelOperationsRegistry Copilot route and CopilotSystemOneClassifier (appends /v1 after account base URL resolution), catalog availability filtering. |
| `ai/src/providers/openai-codex.ts` | Native label missing A3. |
| `ai/src/providers/qwen-token-plan-individual-cn.models.ts` | Empty generated source stub (14-line generated header and empty table), compiled release data supplied separately; native reviewed ten-model catalog already present. |
| `ai/src/providers/qwen-token-plan-individual-cn.ts` | Already native provider/env/model registration. |
| `ai/src/types.ts` | Already KnownProvider via BuiltinProviders and ChatRequest.DisableContinuation. |
| `ai/src/utils/error-body.ts` | Cause details missing A5. |
| `ai/src/utils/retry.ts` | Dropped-stream phrase already implemented and tested in OpenAICompatibilityGap plus CodingAgent SessionRecoveryClassifier. |
| `ai/src/utils/version.ts` | Root-owned version alignment. |

## Release notes and verification inventory

Both AI/Agent changelogs and package.json changed. AI README documents new compatibility options. Agent has no other runtime delta. Package exports/dependencies concern JS lazy entrypoints and version bumps; root owns pinned metadata. BigInt/Undefined argument conversion is a pi-schema delta, not an AI runtime source edit in this diff; coordinate with D-SCH.

Existing focused native suites relevant to already-ported changes: `PiSharp.AnthropicThreads.Tests` (normalization, OAuth roundtrip, settings/mutations, hooks/retry, warm isolation, cancellation/cleanup/concurrency), `PiSharp.OpenAICompatibilityGap.Tests` (compatibility options/affinity/retry phrase), `PiSharp.Models.Tests` CatalogExpansionCases and ConfigSchemaCases, `PiSharp.ProviderSync.Tests` (strict fallback and mid-effort), `PiSharp.ProviderApis.Tests` Codex and provider routing, plus classifier and Durable reviewed catalog tests. Run them only through root's serialized verification.

Changed upstream tests were inventoried: abort; anthropic-empty-thinking-signature-compat; anthropic-message-threads; anthropic-mid-conversation-effort; anthropic-strict-tool-fallback/schema; anthropic-temperature-compat; azure-models; bedrock-strict-tool-fallback; clamp-max-tokens; classifier-models; context-overflow; cross-provider-handoff; empty; error-body; fireworks-models; generate-models-strict; github-copilot-oauth; image-tool-result; models-runtime; openai-codex-stream; openai-completions-empty-tools/non-null-assistant-content/prompt-cache/thinking-as-text/tool-choice/tool-result-images; openai-responses-compat/prompt-cache; providers; qwen-token-plan-models; retry; stream; strict-tool-limits; supports-xhigh; tokens; tool-call-without-result; total-tokens; unicode-surrogate; validation-probes; xai-responses. Most changes outside the new feature-specific tests replace the model fixture with the new CN provider; no hidden new protocol behavior appeared in those fixtures.

No live requests, credentials, builds, or tests were run. Source comparison supplies applicability evidence; runtime verification remains pending implementation/integration.

## Proposed three disjoint implementation workers

1. **AI protocol**: `src/PiSharp.AI/Protocols/AnthropicMessages/AnthropicMessages{KeyAuthRequestFactory,AuthenticatedRequestFactory,PreparedRequest,HttpSseTransport}.cs`, `src/PiSharp.AI/Protocols/Bedrock/BedrockConverseStreamTransport.cs`, new `src/PiSharp.AI/Protocols/ProviderShared/StrictToolLimits.cs`, and focused provider tests. Implements A1/A2. Owns any cancellation regression; preserves existing public-error sanitization (A5 deliberate difference). `SessionResources` can remain untouched if the new static state registers its cleanup on initialization. Avoid editing ClientIdentity (META owns it).
2. **Catalog**: `src/PiSharp.AI/Catalogs/ReviewedModelCatalog.cs`, a new reviewed catalog successor JSON/resource registration, `src/PiSharp.AI/Catalogs/BuiltinProviders.cs`, `src/PiSharp.ModelRuntime/Authentication/ProviderAuthCatalog.cs`, and catalog tests. Implements A3/A6. Coordinate csproj resource registration with META; no historical shard/hash rewrites. This worker does not edit ModelRegistry/RemoteModelCatalog.
3. **Catalog refresh runtime**: `src/PiSharp.ModelRuntime/Models/ModelRegistry.cs`, `RemoteModelCatalog.cs`, optional `RegistryProviderLayer.cs` only if its contract needs explicit policy, and new refresh tests. Implements A4. The existing `CheckAuth` path can provide configured-only gating without new external APIs. Does not edit catalog data/labels.

Shared test Program registration and project files are root-coordinated. New standalone test files can be delivered with registration instructions. No worker should run dotnet; root serializes all integration tests.
