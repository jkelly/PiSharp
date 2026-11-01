# 31 — Model Catalog and Pricing Maintainer

A working .NET 10 sample with a tool-using `PiSharp.Agent` demo, a deterministic maintenance engine, official-source adapters, and an explicitly approved GitHub publishing path. The demo uses fictional models and recorded responses. It calls no model-catalog service and creates no GitHub records.

## Run locally

From the repository root:

```sh
dotnet run --project samples/agents/31-ModelCatalogMaintainer
dotnet run --project samples/agents/31-ModelCatalogMaintainer -- --self-test
dotnet run --project samples/agents/31-ModelCatalogMaintainer -- --maintain --workspace artifacts/catalog-run
dotnet run --project samples/agents/31-ModelCatalogMaintainer -- --maintain --baseline artifacts/catalog-run/models.overlay.json --workspace artifacts/catalog-repeat
```

The repeated maintenance run reports `changed: false`. The normal sample uses the shared runner's `--input`, `--workspace`, `--prompt`, and agent transport options. Its three tools inspect provider configuration, compare the recorded responses, and write a review. Shared-runner `--live` selects the chat transport; it does not turn the recorded domain tools into a publishing job. Use `--maintain` for the source-maintenance CLI below.

Outputs are `models.overlay.json`, `maintenance-report.json`, `model-catalog-review.md`, and `publication-result.json` (maintenance mode) or `publication-plan.json` (agent demo). Maintenance mode also saves exact decoded source responses under `evidence/<sha256>.json` or `.html`. Retrieval times, HTTP status, ETags, and source hashes are in the run report, not the versioned catalog. The hash covers the UTF-8 encoded response text.

`fixtures/repository/catalog-maintained/` is a checked-in example destination containing the generated overlay and full review. Its models and prices are fixtures, not current market data.

## Configure and run live sources

Copy `config.example.json` to a configuration path you control. Set the real repository owner/name and choose enabled providers. Define credentials in environment variables, never JSON. The OpenRouter key variable can be omitted if the public endpoint permits unauthenticated access. Providers with `enabled: false` or IDs in `excludedProviders` are skipped before baseline-file reads, credential lookup, endpoint validation, or network access. `excludedModels` preserves individual model entries after the provider's full list is fetched.

```sh
# Retrieve sources and prepare local artifacts only.
dotnet run --project samples/agents/31-ModelCatalogMaintainer -- --maintain --live --approve --config /path/catalog-config.json --workspace /path/catalog-run

# Clone the configured destination, refresh its overlay, and open/update a draft PR.
dotnet run --project samples/agents/31-ModelCatalogMaintainer -- --maintain --live --approve --publish --config /path/catalog-config.json --workspace /path/catalog-run
```

Live maintenance requires explicit `--config`, `--workspace`, and `--approve`. `--publish` additionally requires a populated `gitHub` block and its token environment variable. The default token variable is `GITHUB_TOKEN`. The token needs contents, pull request, and issue write permissions on the named destination. Git and outbound HTTPS are required only for the live publishing path. No package dependencies or external command execution are required by the offline agent demo or tests.

The destination is cloned into a unique subdirectory of the chosen workspace. The user's checkout is never used as the write target. Only `catalog-maintained/*.overlay.json` and `catalog-maintained/model-catalog-report.md` are staged. Source branches, catalog shards, pinned hash manifests, and built-in source files are not edited. Credentials are passed to Git through process environment configuration and to GitHub through an authorization header; they are not written to a Git URL or file.

The baseline is the destination base branch's existing overlay. Missing providers are seeded, read-only, from `baselineCatalogDirectory/<provider>.json` (default `src/PiSharp.Cli/Models`). The public `FrozenModelCatalog` reader validates the full shard and computes its hash. Existing input, output, cache, and supported context-tier cost fields become comparable USD prices per million tokens, so the first refresh can report increases and decreases against the repository's existing catalog. The original metadata, API route, compatibility properties, and `pinnedCost` are retained. Source observations do not automatically replace an existing `api`, `baseUrl`, or model `type`; differences are flagged for explicit review.

Pinned shards can expose the same provider/model ID as both chat and image definitions. The maintained overlay has one entry per ID, so seeding deterministically prefers chat, which is the type supported by the runtime overlay consumer. Other typed definitions are preserved in full under `metadata.pinnedAlternates`, and the run reports the selection. If there is no chat definition, model type and API name determine the selection. Source ordering does not change this policy; pinned shards remain untouched.

Set `baselineCatalogDirectory` to `null` to disable seeding. A missing shard is reported and previous prices remain **unknown**. `baselineTier` and `baselineRegion` on a provider can declare the dimensions of an existing pinned price; defaults are `catalog-default`/`unspecified` for OpenRouter and `unspecified`/`unspecified` otherwise. Distinct dimensions are never forced to match. Publication reads shards from the isolated clone. Local live scans use the current repository directory or an explicit `--repository-root`. Fixture scans only seed when `--repository-root` is explicitly provided. `--baseline` accepts a reviewed local overlay; publication always reads its baseline from the configured destination's base branch.

## Source adapters

| Adapter | Behavior |
|---|---|
| `openrouter` | Fetches only `https://openrouter.ai/api/v1/models`. Reads model IDs, names, descriptions, context limits, output limits, input/output modalities, supported parameters, and advertised prices. |
| `structured-json` | Fetches a configured HTTPS endpoint on an explicitly declared official host. `modelsPointer`, `metadataPointers`, and `pricesPointer` adapt a structured provider response. Rates must have explicit dimensions; see `fixtures/structured-provider.json`. |
| `official-page-json` | Fetches an official HTML page and accepts exactly one `application/json` script with the configured `jsonScriptId`, using the same structured schema. Missing or ambiguous structured data fails the scan. It does not guess prices from page prose, tables, screenshots, or model output. |

`officialHosts` is a configuration assertion the repository owner must review; it is not a claim that an arbitrary host has been independently authenticated as a vendor. Endpoints cannot contain credentials, queries, or fragments, and HTTP redirects are rejected. Bodies are limited to 32 MiB. Auth is optional bearer-token environment configuration. Provider-specific pagination, arbitrary auth schemes, and JavaScript-rendered pricing extraction are intentionally unsupported; pagination indicators or unsupported shapes fail visibly.

OpenRouter normalization follows its [models endpoint documentation](https://openrouter.ai/docs/api/api-reference/models/list-all-models-and-their-properties) and [official pricing schema](https://github.com/OpenRouterTeam/terraform-provider-openrouter/blob/main/docs/data-sources/model.md). The adapter maps known token charges, cache durations, audio, image, request, and search charges. It applies an explicit fractional discount when supplied. Conditional overrides retain their prompt/time conditions and precedence. Unknown price fields fail for adapter review. The model-list price is an advertised catalog rate, not a promise about every underlying endpoint; provider-specific routing prices and region availability are not inferred.

## Overlay contract and comparisons

```json
{
  "schemaVersion": 1,
  "providers": {
    "provider-id": {
      "model-id": {
        "metadata": {
          "provider": "provider-id", "id": "model-id", "name": "Model",
          "api": "openai-completions", "type": "chat", "runtimeCandidate": true
        },
        "prices": [{
          "kind": "input", "currency": "USD", "unit": "million-tokens",
          "amount": 1.5, "tier": "standard", "region": "us-east", "conditions": {},
          "provenance": { "url": "https://official.example/models", "sha256": "...", "adapter": "structured-json", "pointer": "/data/0" }
        }],
        "provenance": { "url": "https://official.example/models", "sha256": "...", "adapter": "structured-json", "pointer": "/data/0" }
      }
    }
  }
}
```

`metadata` may include `baseUrl`, `contextWindow`, `maxTokens`, `input`, `output`, `supportedParameters`, `reasoning`, `description`, `expirationDate`, and `knowledgeCutoff`. Missing source metadata preserves an existing value. `runtimeCandidate` is only a conservative hint: the recognized chat APIs are `openai-completions`, `openai-responses`, `anthropic-messages`, and `google-generative-ai`. It does not assert complete metadata, credentials, compatible transport behavior, or successful runtime verification. Unknown APIs and non-chat output types remain catalog metadata. A runtime consumer must validate its own transport and complete required fields, and should preserve existing specialized provider bindings.

The sample writes overlay data; it does not modify pinned catalog hashes or automatically install a runtime registration. Rich dimensions, unknown prices, and unsupported models remain available for review even if a runtime cannot represent them.

Rates are decimal values. `token` and `thousand-tokens` normalize to `million-tokens`. Other accepted units are `image`, `request`, `search`, `second`, `minute`, and `character`; different non-token units stay distinct. Currency codes normalize to uppercase; no foreign-exchange conversion occurs. Amount `null` means unknown and zero means explicitly free. Missing/unknown new amounts never replace a known prior amount. Missing models, pricing tiers, and regions are retained; the report flags prior amounts that the current response did not verify. Deprecation/removal requires a separate human-reviewed change.

The comparison identity is `(kind, currency, normalized unit, tier, region, conditions)`. Default tier/region are `unspecified`; OpenRouter uses tier `catalog-default`, region `unspecified`, and `catalog-override` for conditional rates. Its price kinds are `input`, `output`, `cache-read`, `cache-write-5m`, `cache-write-1h`, `audio-cache-read`, `reasoning`, `audio-input`, `audio-output`, `image-input-token`, `image-input`, `image-output`, `request`, and `web-search`.

Increases and decreases get old/new, signed absolute, and percentage differences. Unknown/zero baselines have no invented percentage. Currency/tier/region changes become separately identified observations, not misleading cross-dimension increases. Object keys, model order, rate order, and change order are deterministic. Semantic equality preserves original provenance, so retrieval timestamps, response formatting, and response-row ordering do not create catalog-only churn. If any enabled source fails, the scan is incomplete, the baseline is retained, no new overlay proposal is written, and publication is blocked.

## Scheduling, publication, and recovery

Install `workflows/model-catalog-maintainer.yml.template` deliberately under `.github/workflows/`. It supports manual dispatch. A commented cron example can be enabled and changed to the desired schedule. It is a template, not an active schedule in this repository. Set repository variable `CATALOG_CONFIG` to the configuration file's relative path. Set `CATALOG_REPOSITORY` for the concurrency group if publishing to another repository. Cross-repository publishing needs an appropriately scoped `CATALOG_GITHUB_TOKEN` secret.

The stable branch defaults to `automation/model-catalog`. Existing branches must carry the maintainer commit marker. Pushes use an exact expected-old-SHA lease and never update the base branch. Repeating an unchanged overlay and full review report reuses the current branch commit. If the base price changes while the proposal stays fixed, the full report is recommitted with the new old/new amounts. Reports omit retrieval timestamps and normalize line endings. The stable PR is identified by head/base and an ownership marker and remains a draft. Before updating its branch, an existing ready-for-review PR is converted back to draft through GitHub GraphQL; unconfirmed conversion blocks publication. A complete scan with no changes closes an existing marked PR as superseded after ensuring it is a draft; no empty commit is created, and historical price issues remain unchanged. A later real change reopens that automatically retired draft and removes the superseded marker. A human-closed, unmerged marked PR blocks recreation until reviewed; merged PR history permits a future update. An incomplete scan never retires a proposal. The price issue is identified by a deterministic fingerprint of the complete normalized price-change set, including old/new values. Existing issues are searched across all states and all result pages; matching issues and PRs are updated, with links in both directions. Large reviews keep the complete table in the versioned report and link it from the issue.

There is no automatic merge, issue closure, dependency installation, arbitrary repository build, or execution of repository hooks. Unknown write outcomes are not blindly retried; rerunning reconciles the existing branch and GitHub markers. A pushed branch or issue can remain if a later API operation fails, and that run exits unsuccessfully. The isolated checkout and artifacts remain for inspection. A local workspace lock and Actions concurrency serialize normal runs. Independent workers on different hosts **must share a concurrency policy**: GitHub issue creation has no transactional idempotency key, so this sample cannot guarantee deduplication under uncoordinated simultaneous API writes.

## Verification

`--self-test` performs 32 offline checks covering provider/network exclusion, missing/null prices, unit equivalence, increases/decreases, zero baselines, currency/tier/region/cache distinctions, unknown protocols, retention, invalid data, atomic failure, pricing-page ambiguity, timestamps/reordering, numeric spelling, HTTP status/auth constraints, OpenRouter normalization and discounts, publication guards, failure propagation, real GitHub-adapter request/response logic against an in-memory HTTP handler, duplicate handling, no-change PR retirement/reopening and ownership guards, pagination, target boundaries, and first-run pinned-price seeding with route preservation and shared chat/image IDs. The normal fixture agent and standalone maintenance CLI are also runnable without credentials. No live provider endpoint or GitHub repository was mutated during development validation.
