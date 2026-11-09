# Reviewed catalog layer — 2026-11-08

The built-in source shards remain the exact 42 files admitted from the historical pi-ai 1.5.0 package. Their hashes, embedded bytes, and frozen-reader tests are unchanged. Product runtimes now layer `PiSharp.AI.Catalogs.ReviewedModelCatalog` over those verified files. The separately embedded `reviewed-2026-11-08.json` records the selected upstream commit, source-file SHA-256 values, targeted corrections, and complete additions. Its resulting provider digest identifies a derived catalog, not the old package bytes.

This layer is always on in ModelRegistry and NativeDurableModels, including when no maintained pricing file exists. ModelRegistry applies remote catalogs and optional maintained pricing next, followed by explicit user and extension configuration. NativeDurableModels still allows an explicit host catalog to replace a provider. Historical readers remain available for tests and provenance.

## C19 corrections

Exactly 18 existing rows change:

- Five Azure GPT-6 variants get a 1,050,000-token context window.
- Six OpenCode Zen/Go Claude rows enable per-message effort. Opus 5.5, Sonnet 5.5 and Fable 5.1 additionally remove the `off` level; Opus 5 and Haiku 5.5 retain it. The existing runtime emits the binding controls.
- Seven existing Sonnet 5 Anthropic Messages rows exclude temperature, including proxy and batch rows. Sonnet 5.5 exclusions already present in the historical shards remain unchanged.

Temperature eligibility reaches Simple, catalog, fixed, resolved-auth and CLI/Durable native route projection paths. An explicit already-disabled projection stays disabled.

## F09 additions

Twelve rows are added, with no broad catalog regeneration:

- `qwen-token-plan-individual-cn`: the exact ten-model Individual allowlist in the selected upstream source, projected from the already verified Qwen CN shard to the new provider identity. China endpoint and `QWEN_TOKEN_PLAN_CN_API_KEY` are independent of the international key.
- `qwen-token-plan-individual/glm-5.3`: the upstream Individual allowlist includes this model. Its existing CN metadata is projected to the international Individual endpoint.
- `github-copilot/microsoft-decision-1`: classifier only, `typesafe-system-one`, 32,768 context, input price 0.042 and other token prices zero, with the exact static Copilot headers from upstream. Upstream itself attributes these context/price values to Azure rather than the Copilot catalog; that limitation is retained.

Qwen limits, prices and capabilities are historical verified CN metadata, not fresh provider observations. The selected upstream rules supply membership, identity, endpoint and key. No models.dev fetch, model generation job, credential use, or live-provider test was performed.

The default model map is explicitly updated to the selected upstream map: 19 existing provider defaults change and the new China Individual provider receives `qwen3.8-max`. Every target already exists in the historical catalog or the twelve explicit additions. No unrelated model rows change.

Tests compare the entire old catalog against the 18 allowed modifications, check the twelve-row addition count, exact new allowlist/key isolation and complete default map, preserve user override precedence, prepare Anthropic requests, and exercise the China route with fake HTTP. Durable tests independently compare all historical rows and exercise its default China route. Centralized I13 validation owns execution receipts; authored tests alone are not execution evidence.
