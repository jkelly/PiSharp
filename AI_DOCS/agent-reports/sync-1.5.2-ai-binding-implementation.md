# Current Anthropic catalog capability binding

Root-authorized follow-up to the current-catalog review. No builds or tests launched by this worker; serialized validation owns execution. Current source is Pi 1.5.2 `b4afdddf44e059c67ccf5097e5317d913521b470`.

## Fix

`AnthropicCatalogCompatibility` now resolves explicit native request options, then the composed model's `compat`, then the existing protocol default. It binds `forceAdaptiveThinking`, `supportsMidConvoEffort`, `supportsTemperature`, `allowEmptySignature`, `supportsLongCacheRetention`, `supportsEagerToolInputStreaming`, `supportsStrictTools`, `supportsCacheControlOnTools`, `supportsMidConvoSystemMessages`, `supportsMidConvoToolChanges`, `supportsMessageThreads`, and `allowedFallbackModels`. The fallback list retains explicit empty arrays as an opt-out.

The request-header overload binds `sendSessionAffinityHeaders` and `sessionAffinityFormat`, using native explicit key-auth options first and the upstream OpenRouter default only where applicable. OAuth retains its existing header projection rule.

Shared binding runs for ordinary native Anthropic, resolved-auth Anthropic, catalog gateway Anthropic, provider-route Anthropic, and Simple requests. Per-level profile binding still enforces model thinking-level availability, but no longer overwrites explicitly supplied adaptive/effort compatibility. Simple resolves the same effective adaptive/effort/temperature values. The redundant early temperature merge in ModelRuntime was removed so shared binding can distinguish omitted from explicit values.

No catalog bytes, historical captures, or default model selection were changed. A gateway does not inherit direct Anthropic capabilities: only its own composed metadata applies.

## Public API migration

This is a deliberate native API signature change, **not** a claim of full source or binary compatibility. These `AnthropicMessagesRequestOptions` positional properties/constructor parameters are now `bool?`, defaulting to null (inherit):

- `ForceAdaptiveThinking`, `SupportsMidConversationEffort`
- `SupportsTemperature`, `AllowEmptyThinkingSignatures`
- `SupportsLongCacheRetention`, `SupportsEagerToolInputStreaming`
- `SupportsStrictTools`, `SupportsCacheControlOnTools`
- `SupportsMidConversationSystemMessages`, `SupportsMidConversationToolChanges`

`AnthropicMessagesKeyAuthRequestOptions.SendSessionAffinityHeaders` is now `bool?` and `SessionAffinityHeader` is nullable/default-null. Existing named constructor calls passing true/false keep their source form, but callers reading these properties as non-nullable bool must use `??` with the appropriate default or compare to true/false. Consumers must rebuild because property getter and constructor CLR signatures changed. These types were not newly labeled experimental.

An additive override property could not distinguish an existing explicitly supplied false argument from its old omitted false default. Nullable constructor inputs provide that distinction directly. Without metadata, the low-level projector still defaults strict/system/tool changes/adaptive/effort/empty-signature support off, and temperature/long-cache/eager-input/tool-cache support on. Raw key factories still omit affinity unless explicitly enabled and use x-session-affinity by default.

## Offline regressions

Four new cases are registered in `PiSharp.ProviderSync.Tests`:

1. `anthropic.catalog-released-fable-wire-capabilities`: reads the byte-pinned current Fable 5.2 row and sends requests through native, resolved, Simple and dynamic routes. Asserts strict preferred/required tools, native tool addition/removal, mid-conversation system text, adaptive thinking, inline beta, and fallback request fields.
2. `anthropic.catalog-explicit-overrides-and-raw-defaults`: explicit false overrides disable released model capabilities, explicit true temperature wins, explicit empty fallbacks opt out, and standalone projector behavior retains established defaults.
3. `anthropic.catalog-other-provider-isolation`: current OpenRouter Fable keeps its own capabilities (no direct Anthropic strict/native-system/thread flags) and receives its proper session affinity.
4. `anthropic.catalog-optional-flags-and-affinity`: model-driven disabled temperature/long-cache/eager-input/tool-cache, enabled empty-signature replay, and affinity are observed in serialized requests; explicit options reverse each choice.

Independent read-only review by model_refresh after source stabilization found no remaining concrete mapping, precedence, default-behavior, or nullable-use issue. `git diff --check` passed for changed paths; runtime validation remains pending with the serialized validation owner.
