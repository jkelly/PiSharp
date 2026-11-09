# Maintained model catalog

`models.overlay.json` is a separately versioned layer for reviewed provider metadata and prices. The [catalog maintainer](../samples/agents/31-ModelCatalogMaintainer/README.md) produces this format and opens update PRs without rewriting the upstream catalog shards or their pinned hashes.

PiSharp copies this file into its output directory. The model registry applies it after remote catalog refreshes and before user `models.json` and extension configuration. `PISHARP_MAINTAINED_CATALOG` selects an explicit alternative path. Registry rebuild/reload reads it again.

The schema is `{ "schemaVersion": 1, "providers": { "provider-id": { "model-id": { "metadata": {}, "prices": [] } } } }`. The sample's README documents the full provenance and price dimension contract. `PiSharp.AI.Catalogs.MaintainedCatalogOverlay.Apply` is the pure public reader used by the CLI.

Only complete chat entries with an existing provider transport are admitted. Existing routes and compatibility metadata are preserved. Runtime price changes require unambiguous USD-per-million-token rates with no regional/time/tier conditions. Missing values are retained for existing models and reject incomplete new models. Unknown prices never become free prices. Unsupported or conditional data remains in the source evidence and produces diagnostics until a reviewed native mapping is added.

This file is not a credential store. The maintainer's retrieval logs and transient timestamps stay outside versioned pricing data.

## C19 corrections, 2026-11-08

The 18 C19 corrections now live in the always-on embedded `ReviewedModelCatalog`, alongside the explicit F09 additions, so both ModelRegistry and Durable receive them even when this optional pricing file is absent. See [the reviewed catalog contract](../docs/reviewed-catalog-2026-11-08.md). Each entry records the exact checked-out source path and SHA-256. Generated upstream JSON is absent; provider and family rules are the evidence. This is not a catalog refresh or a claim of live-provider qualification.

- Azure's five GPT-6 variants use a 1,050,000-token context window (`catalog/providers/azure.ts`).
- OpenCode Zen and Go Claude entries selected by `supportsClaudeMidConvoEffort` enable that capability. Opus 5.5, Sonnet 5.5 and Fable 5.1 also remove thinking `off`, as the upstream helper specifies; Opus 5 and Haiku 5.5 retain it. The existing runtime capability emits per-message effort and thinking block binding controls.
- Seven existing Sonnet 5 Anthropic Messages rows exclude temperature, including proxy and batch rows selected by the upstream family rule. Existing Sonnet 5.5 exclusions stay unchanged.

The reader permits only the two reviewed boolean Anthropic compatibility fields and `thinkingLevelMap.off: null` from this metadata extension. It merges them into existing compatibility and thinking maps. Routes, other capabilities, prices, input/output limits and historical frozen shards are preserved. Empty `prices` arrays mean retain existing costs, never zero them. Explicit user and extension overrides still have the existing later precedence. Temperature eligibility is propagated into the Simple and resolved runtime projection paths.

`PiSharp.Models.Tests` checks every runtime row against the frozen baseline plus this exact correction set, then prepares offline Anthropic requests to check temperature exclusion, a supported-temperature control and effort/binding selection.
