# I-CATALOG: complete Pi 1.5.2 release catalog

Frozen source: Pi v1.5.2 `b4afdddf44e059c67ccf5097e5317d913521b470`. Worktree: `E:/AI/PiSharp/update-1.5.2`. Root approved replacing the initial source-rule-only projection with a complete, verified current release snapshot, while retaining every historical artifact/API. Requested high reasoning and ultrafast service tier; effective tier unverified.

## Release data evidence

The actual `@earendil-works/pi-ai@1.5.0` package was already available at `E:/AI/PiSharp/gap-implementation/artifacts/node-runtime-150/node_modules/@earendil-works/pi-ai`. Its 42 provider shards are byte-identical to all native historical shards. Its manifest SHA256 is `50e0e74cb54dada0a6972c2619e56e99830ee01857810719b6b913cd29e04e87`. There is no preexisting native catalog-data divergence to explain away the release changes.

The actual installed 1.5.2 package is `artifacts/node-runtime-152/node_modules/@earendil-works/pi-ai`. The cached tarball was identified from the exact package-lock SRI, read without executing package code, and verified before copying members:

- URL: `https://registry.npmjs.org/@earendil-works/pi-ai/-/pi-ai-1.5.2.tgz`
- SHA256: `d5715efa834c23bfdf10a042b49df4ee169cc99000bf16d7998fab2d533b45f8`
- SRI: `sha512-2tWPSnV3qzfiDIY3aWluBag3vC++Vs+VsXt/UquSU5onPMohPih72FUhIhaqmDdMYubOgB3Byl9uF4Vk40JLvQ==`
- `dist/providers/data/.manifest.json` SHA256: `71375843feaa116c7d928e74e0500a22cc267e12bfb6869efb92dfd39acf7fee`
- Generated at `2026-11-10T12:05:37.628Z`, Unix milliseconds `1794312337628`.
- All 43 provider JSONs and the manifest match the tarball members byte-for-byte. Every provider matches the SHA256 listed in the manifest.
- Current snapshot: **1773 entries: 1643 chat, 66 image, 64 classifier**.
- Actual 1.5.0 to 1.5.2: **82 additions, 21 removals, 104 changed retained rows**. The initial source-rule overlay covered only a subset; complete release data was required.

Machine-readable provenance: `src/PiSharp.AI/Catalogs/Releases/1.5.2.provenance.json`. Exact data: `src/PiSharp.AI/Catalogs/Releases/1.5.2/` (43 provider JSONs plus `.manifest.json`). A narrow `.gitattributes` rule disables text conversion for those exact bytes.

## Runtime architecture and owned changes

- New `src/PiSharp.AI/Catalogs/CurrentModelCatalog.cs` verifies a fixed manifest SHA256 before parsing it, verifies each requested provider against the manifest SHA256, and admits data through `FrozenModelCatalog`. Exposes the current provider set, provenance, timestamp and immutable cached catalogs.
- `src/PiSharp.AI/Catalogs/ReviewedModelCatalog.cs` selects the complete current snapshot first. Earlier reviewed bundles are retained but cannot overwrite the current release. Returned current entries identify source `b4afdddf...`, while the earlier reviewed source constant is retained as `HistoricalReviewedCommit`.
- `src/PiSharp.AI/PiSharp.AI.csproj` embeds exact provider data with explicit `PiSharp.AI.Catalogs.Releases.1.5.2.<provider>.json` resource names. The manifest is explicitly named `PiSharp.AI.Catalogs.Releases.1.5.2..manifest.json`.
- `src/PiSharp.ModelRuntime/Models/ModelRegistry.cs` enumerates current providers and uses the current generation timestamp when evaluating remote catalogs. Historical `BuiltinModelCatalog` bytes, digest tables, generation timestamp and APIs remain unchanged.
- `src/PiSharp.ModelRuntime/Commands/LiveSessionSelection.cs` gates exact built-in selection against the current provider set.
- `src/PiSharp.Cli/Extensions/NativeExtensionModelOperations.cs` supplies current catalogs to AI extension/model-operation routes, including image and classifier models.
- `src/PiSharp.Durable/Models/BuiltinCatalog.cs` enumerates the current provider set and returns the same exact current snapshots. Historical ReadShard and hash tables remain unchanged.
- `src/PiSharp.AI/Catalogs/BuiltinProviders.cs` and `src/PiSharp.ModelRuntime/Authentication/ProviderAuthCatalog.cs` display `OpenAI Codex`.
- The initial explicit `reviewed-2026-11-10.json` is retained as provenance for the reviewed source-rule implementation, but is superseded by the full current release for all known providers. No older corrections are merged onto current release data.

Custom models.json, configured overrides, maintained catalog overlays, remote/catalog layers, and extension models still apply subsequently. Released removals affect the current built-in set only; an explicit user model may restore a removed identity. Default model choices remain unchanged.

## Data changes now covered

New release entries include Fable 5.2 across Anthropic, Bedrock, OpenCode, OpenRouter, Radius and Vercel (11 direct/prefixed/dotted/batch/fast variants); Kimi 3.1 variants; Meta Muse Ember; gateway free models; new image and classifier models. Removed rows include NVIDIA Llama 3.2 11B Vision, Fireworks legacy rows, OpenRouter Mercury and older models, and obsolete Vercel rows. Price/context/output/provider-list changes across OpenRouter, Radius, OpenCode, Google and Vercel now match the release exactly.

Fable direct metadata retains adaptive thinking, off disabled, per-message effort, strict tools, system/tool changes, cache TTLs, input limits and **only Opus 5** as fallback with pinned fallback cost. Provider-specific variants retain exact released capabilities instead of inventing uniform capabilities across APIs. Azure now includes all thirteen complete OpenAI long-context price tables (including `gpt-daybreak-blue-latest`) while preserving Azure-specific DeepSeek pricing.

## Tests

`tests/PiSharp.Models.Tests/Sync152CatalogCases.cs` uses the already registered `Sync152CatalogCases()` method. Eleven authored offline cases:

1. `sync152.catalog.exact-current-package-resources`: manifest fingerprint/resource name, all 43 provider digests, current provider enumeration, current timestamp/source, type counts.
2. `sync152.catalog.release-additions-removals-and-metadata`: exact +82/-21/104 release delta; removed identities absent; new image/classifier entries; Google context and OpenCode price corrections.
3. `sync152.catalog.fable-all-released-provider-variants`: eleven variants across six providers and provider-specific thinking metadata.
4. `sync152.catalog.explicit-user-model-can-restore-removed-id`: released removals do not override explicit user model definitions.
5. `sync152.catalog.fable-finalized-metadata-and-selection`: direct metadata, fallback identity/prices, CLI selection, unchanged default/historical shard.
6. `sync152.catalog.fable-request-adaptive-effort`: actual request adaptive/drop_block/xhigh/per-message effort and sole compatible fallback.
7. `sync152.catalog.fable-fallback-billing`: fake Anthropic stream returning Opus 5 uses fallback rates.
8. `sync152.catalog.azure-all-tiered-counterparts`: all thirteen full price tables and unchanged Azure DeepSeek.
9. `sync152.catalog.azure-tier-boundary-runtime-cost`: fake Azure stream at exactly 272000 and 272001 total input, including cache read/write; verifies every price component.
10. `sync152.catalog.user-cost-and-capability-overrides-win`: explicit price, empty tiers and capability overrides follow current data.
11. `sync152.catalog.codex-label`: both display labels.

Updated current-facing cases only:

- `tests/PiSharp.Models.Tests/CatalogCorrectionCases.cs`: registry is exactly all current chat metadata; no removed-row leak.
- `tests/PiSharp.Models.Tests/CatalogExpansionCases.cs`: current all-type total 1773; original defaults/Qwen contracts retained.
- `tests/PiSharp.Durable.Tests/ReviewedCatalogCases.cs`: every provider digest equals current snapshot, total1773 and Fable available via durable facade.
- `tests/PiSharp.Models.Tests/RemoteCases.cs` and `tests/PiSharp.ModelRuntime.Tests/CatalogRefreshAuthCases.cs`: fake newer remote data is dated relative to current generation time.

Historical `CatalogCases`/`Sync150CatalogCases`, old provider bytes, package manifests, captured fixtures and historical timestamp expectations are preserved.

## Verification and integration status

Passed offline Python verification of tarball SRI, archive SHA256, all archive/member/installed/copied byte equalities, all provider manifest SHA256s, exact type counts, and all historical shard/old reviewed-overlay bytes unchanged. `.gitattributes` reports `text: unset` for exact current provider and manifest JSONs. Owned-file diff check passed after LF normalization of `.gitattributes`.

No dotnet builds or test runs performed by this worker; root/validation serializes those. Notified root, validation and independent model_refresh reviewer that implementation is stable. No live provider calls or credentials. Final runtime results belong to the integration receipts.


## Integration regression correction

The full Models run exposed a real Azure factory omission: `CreateAzureResponses` constructed `AzureResponsesOptions` without binding model prices into `StreamOptions`, so usage cost stayed zero even though the new catalog contains complete rates. Updated `src/PiSharp.AI/Providers/NativeProviderFactory.AzureResponses.cs` to reuse `ResponsesRatesForModel(modelMetadata)`, including tiers, as the other native Responses factories do. The boundary test remains unchanged in substance and now prints expected/actual rates on failure.

Two OpenRouter current-registry tests still sourced their expected pre-refresh list from the historical shard. Updated only those current-facing expectations in `tests/PiSharp.Models.Tests/OpenRouterCases.cs` to `CurrentModelCatalog`; historical snapshots/checks remain unchanged. No builds performed by this worker; final rerun belongs to validation.
