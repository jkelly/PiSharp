# Anthropic compatibility option inheritance

The Pi 1.5.2 candidate binds native Anthropic requests to the selected model's `compat` metadata. `AnthropicMessagesRequestOptions` compatibility switches are nullable where omission must differ from an explicit override. The resolution order is **explicit option → model metadata → established fallback**. Both `true` and `false` are explicit overrides; `null` inherits.

This changes existing public positional constructor/property signatures from `bool` to `bool?` for affected switches. Native consumers must rebuild. A consumer that requires a non-nullable value must apply its appropriate fallback, for example `options.SupportsTemperature ?? true` or `options.SupportsStrictTools ?? false`, rather than treating every null as false. These existing APIs are not newly experimental.

Representative established fallbacks, after metadata lookup:

| Switch | No explicit value and no model field |
| --- | --- |
| `SupportsTemperature` | `true` |
| `SupportsLongCacheRetention` | `true` |
| `SupportsEagerToolInputStreaming` | `true` |
| `SupportsCacheControlOnTools` | `true` |
| `ForceAdaptiveThinking` | `false` |
| `AllowEmptyThinkingSignatures` | `false` |
| `SupportsStrictTools` | `false` |
| `SupportsMidConversationSystemMessages` | `false` |
| `SupportsMidConversationToolChanges` | `false` |
| `SupportsMidConversationEffort` | `false` |
| `SupportsMessageThreads` | remains nullable; the existing direct-Anthropic endpoint rule resolves absence |

`AnthropicCatalogCompatibility.Apply` performs this binding in native provider routes, catalog/resolved provider factories and the Simple request factory. Supplying a standalone projector with explicitly constructed options does not itself acquire model metadata. An unspecified `AllowedFallbackModels` array inherits catalog fallback model IDs; an explicitly supplied empty array remains empty.

The corresponding request and override tests belong to the provider validation cohort. This contract describes resolution and migration, not a claim that a live provider or all compatibility gates passed.
