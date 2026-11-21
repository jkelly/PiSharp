# sync-1.6.0 ai-catalog: Pi 1.6.0 model catalog, default models, Qwen Token Plan

Work package `ai-catalog` of the Pi 1.6.0 sync. Branch `sync/1.6.0-ai-catalog`, from `8352a672`. Upstream v1.5.2 `b4afdddf44e059c67ccf5097e5317d913521b470` to v1.6.0 `faac585884ae2c013a6b1400f3e9e77f10b34bac`. No live provider calls, no credentials, nothing pushed.

## Result

PiSharp's current catalog is the exact catalog `@earendil-works/pi-ai@1.6.0` ships, and the 1.5.2 release is kept as a preserved layer. `openai`, `azure` and `openai-codex` default to `gpt-6.2-sol`. The four Qwen Token Plan providers use the new gateways, names and per-plan key variables. The embedded `models.schema.json` is upstream's v1.6.0 file.

**The 1.6.0 catalog must not ship without two changes in provider request code, which the `ai-providers` package owns.** Both are pinned by a case that fails on this branch:

1. The catalog no longer carries `compat.supportsDeveloperRole: false` on any row and relies on the `openai-completions` default of the same release. Until that default is ported, nine providers send the system prompt with the `developer` role.
2. Pi 1.6.0 raised `maxTokens` to 1,048,576 on the default models of `fireworks`, `opencode-go` and `together`. PiSharp's Completions route refuses any request above its own 1,000,000 limit before sending, so those three defaults do not work from the CLI.

Details are under "Cross-package needs".

## Catalog snapshot

Built the same way as 1.5.2: the published tarball was downloaded into `artifacts/released-npm-ai-1.6.0/` (ignored), read as bytes with a small ustar reader (the one `tools/CompatibilityReport/refresh-model-catalogs.mjs` uses), and nothing from it was executed.

| item | value |
| --- | --- |
| tarball | `https://registry.npmjs.org/@earendil-works/pi-ai/-/pi-ai-1.6.0.tgz` |
| npm integrity (verified) | `sha512-9hL+eygFDEiFp//cydeUfmFsKmEqpmvsNPb10Y1W8+m5K9O+sx1hMStfu6nukrBD5sl6mP4RykCYBBNEgsVLfw==` |
| tarball SHA-256 | `d68312ec574bc959cb7fee26da4f4876d067aed9bbbe76e876359e60d2294747` |
| `dist/providers/data/.manifest.json` SHA-256 | `cc46553f3f008788e667600a873d3d83a4df8fe46c67b06c65b16fd828450e8b` |
| manifest `schemaVersion`, `generatedAt` | 6, `2026-11-20T10:48:49.496Z` (Unix ms `1795171729496`) |
| providers | 43 (the same 43 as 1.5.2; none added, none removed) |
| entries | 1853: 1698 chat, 74 image, 81 classifier (1.5.2: 1773 = 1643 + 66 + 64) |
| against 1.5.2 | 129 added (103 chat, 8 image, 18 classifier), 49 removed (48 chat, 1 classifier), 651 changed |

All 43 shards and the manifest in `src/PiSharp.AI/Catalogs/Releases/1.6.0/` equal the archive members byte for byte, and each shard matches its manifest SHA-256. `--release-tarball` on the Models suite repeats that check in C# (it ran: `releaseTarballVerified: true`). The upstream repository does not track the data files (`*.models.ts` import them), so the tarball is the only source. The package has no `gitHead`; the source commit recorded is the frozen v1.6.0 commit.

No new field path, value kind or API name appears in 1.6.0 relative to 1.5.2. In particular no row uses `supportsMaxOutputTokens`, so no compat field declaration had to be added. One path disappears: `compat.supportsDeveloperRole` (601 rows carried `false` in 1.5.2; 570 of them are still listed).

Of the 651 changed rows, 428 differ only by the removed `supportsDeveloperRole`. The other 223 carry price, context window, max-token, name and thinking-level changes, and the Qwen gateway move.

| provider | 1.5.2 | 1.6.0 | added | removed | changed |
| --- | ---: | ---: | ---: | ---: | ---: |
| amazon-bedrock | 202 | 208 | 6 | 0 | 21 |
| ant-ling | 3 | 3 | 0 | 0 | 3 |
| azure | 45 | 48 | 3 | 0 | 1 |
| baseten | 15 | 15 | 0 | 0 | 15 |
| cerebras | 2 | 2 | 0 | 0 | 2 |
| cloudflare-ai-gateway | 59 | 63 | 4 | 0 | 20 |
| cloudflare-workers-ai | 24 | 25 | 1 | 0 | 19 |
| deepseek | 2 | 2 | 0 | 0 | 2 |
| fireworks | 27 | 27 | 0 | 0 | 16 |
| github-copilot | 28 | 36 | 8 | 0 | 27 |
| google | 23 | 26 | 3 | 0 | 1 |
| google-vertex | 13 | 17 | 4 | 0 | 0 |
| huggingface | 78 | 87 | 9 | 0 | 78 |
| kimi-coding | 6 | 6 | 0 | 0 | 6 |
| mistral | 34 | 34 | 0 | 0 | 3 |
| moonshotai | 6 | 6 | 0 | 0 | 6 |
| moonshotai-cn | 6 | 6 | 0 | 0 | 6 |
| nvidia | 20 | 12 | 0 | 8 | 12 |
| openai | 45 | 48 | 3 | 0 | 0 |
| openai-codex | 8 | 10 | 2 | 0 | 5 |
| opencode | 87 | 86 | 7 | 8 | 23 |
| opencode-go | 32 | 33 | 2 | 1 | 20 |
| openrouter | 505 | 535 | 43 | 13 | 266 |
| qwen-token-plan | 21 | 20 | 1 | 2 | 19 |
| qwen-token-plan-cn | 21 | 20 | 1 | 2 | 19 |
| qwen-token-plan-individual | 10 | 13 | 3 | 0 | 10 |
| qwen-token-plan-individual-cn | 10 | 13 | 3 | 0 | 10 |
| radius | 35 | 39 | 4 | 0 | 0 |
| together | 16 | 16 | 0 | 0 | 16 |
| vercel-ai-gateway | 325 | 332 | 22 | 15 | 14 |
| zai | 7 | 7 | 0 | 0 | 7 |
| zai-coding-cn | 4 | 4 | 0 | 0 | 4 |

Unchanged: anthropic, groq, meta, minimax, minimax-cn, typesafe, xai, xiaomi and the three xiaomi-token-plan providers.

Notable additions: GPT-6.2 Astra and Sol on openai, azure, openai-codex, github-copilot, amazon-bedrock (6 ids), cloudflare-ai-gateway, opencode, openrouter (8 ids with pro and batch variants), radius and vercel-ai-gateway; `gpt-6-cyber` on openai and azure; Gemini 4 Carbon, Hydrogen and Neon on google, google-vertex, github-copilot, opencode, openrouter and vercel; Fable 5.2 on cloudflare-ai-gateway and github-copilot (13 variants now, 11 before); `qwen4-max-preview` on all four Qwen Token Plan providers (the changelog names only the Individual ones; the published data has it on Team too). Notable removals: eight NVIDIA rows, `deepseek-v3.2` and `deepseek-v4-flash` from the Qwen Team providers, older OpenCode, OpenRouter and Vercel rows.

### Layering

- `src/PiSharp.AI/Catalogs/ReleasedModelCatalog.cs` (new, public) is the byte-verifying loader for one release: it checks the manifest against a pinned SHA-256 before parsing it and each shard against the manifest before admitting it through `FrozenModelCatalog`.
- `CurrentModelCatalog` keeps its public members and constants, now for 1.6.0, and serves that release through the loader.
- `ReleasedModelCatalog.Pi152` is the 1.5.2 release, embedded and pinned as before. `src/PiSharp.AI/Catalogs/Releases/1.5.2/`, `1.5.2.provenance.json` and the 1.5.2 acceptance tooling that reads them are untouched. Nothing resolves models from it: `ReviewedModelCatalog.Apply` returns the current release for every provider it has and falls back to the 1.5.0 shards plus reviewed bundles only for a provider the current release lacks (there is none). I did not put 1.5.2 into that fallback chain; with identical provider sets it would be unreachable code.
- The 1.5.0 shards (`src/PiSharp.Cli/Models`, `BuiltinModelCatalog`, the Durable copy) and their pins are untouched.
- `ModelRegistry`'s remote catalog age gate uses the 1.6.0 `generatedAt` through the unchanged `CurrentModelCatalog.GeneratedAtUnixMilliseconds`.

## Upstream changes and their status

| upstream source or changelog bullet | status | C# | cases |
| --- | --- | --- | --- |
| `packages/ai` published catalog data 1.6.0 | ported | `Catalogs/Releases/1.6.0/**`, `1.6.0.provenance.json`, `ReleasedModelCatalog.cs`, `CurrentModelCatalog.cs`, `PiSharp.AI.csproj`, `.gitattributes` | `sync160.catalog.exact-current-package-resources`, `.snapshot-equals-the-package-tarball`, `.pi-1.5.2-release-preserved-beside-current`, `.release-delta-from-1.5.2` |
| `catalog/families/openai.ts`, `providers/openai.ts`, `azure.ts`, `openai-codex.ts`: GPT-6.2 Astra and Sol (prices, long-context tier, tool search, additional tools, mid-conversation effort, no `none`/`minimal`, Azure 1.05M, Codex 500K/272K) | ported (data) | snapshot | `sync160.gpt-6.2.official-metadata-openai-and-codex`, `.off-unavailable-and-thinking-levels`, `.azure-1.05m-context-and-long-context-cost` |
| `providers/openai-codex.ts`: `supportsMidConvoEffort` on the seven GPT-6 family Codex rows | ported (data); request behavior is `ai-providers` | snapshot | `sync160.codex.mid-conversation-effort-flags` |
| `coding-agent/src/core/model-resolver.ts`: defaults `gpt-6.2-sol` | ported | `src/PiSharp.ModelRuntime/Models/ModelResolver.cs` | `sync160.defaults.openai-azure-and-codex-use-gpt-6.2-sol`; updated `catalog-f09.default-map-exact-upstream`, `resolver.defaults-exist-in-generated-catalogs`, `live.scoped-models-and-settings-defaults`, `sync150.codex-chatgpt-models-removed`, `sync150.default-openai-model-only-without-a-selection`, CliParity `resume.restores-the-branch-model-or-falls-back-like-sdk`, InteractiveParity `e2e.prompt-auth.continue-falls-back-with-a-warning` |
| `model-resolver.ts`: `resolveModelScope` and `restoreModelFromSession` deleted (the "about 90 lines") | no-op here | The defaults still come from `defaultModelPerProvider`. `ModelResolver.ResolveModelScope` already is the diagnostics-returning form (no console wrapper exists). `ModelResolver.RestoreModelFromSession` remains: internal, no caller in `src`, one case in `ResolverCases`. Upstream removed dead code; removing the port is a `cli-session` decision and changes no behavior | none |
| `src/env-api-keys.ts`, `src/providers/qwen-token-plan*.ts`: gateways, Team names, per-plan key variables | ported | `BuiltinProviders.cs`, `ProviderEnvironmentKeys.cs`, `InjectedAuthenticationResolver.cs`, `ModelOperationsRegistry.cs`, `ProviderAuthCatalog.cs` | `sync160.qwen.gateways-and-display-names`, `.per-plan-key-preferred-then-shared`, `.per-plan-key-stays-with-its-plan`, `.live-route-gateway-plan-key-and-thinking-fields`; Authentication `all-original-environment-mappings`; Durable `models.catalog.qwen-individual-cn-routes-with-china-key` |
| `catalog/providers/qwen-token-plan*.ts`: `qwen4-max-preview`, `deepseek-v4.1-flash`, `deepseek-v4.1-flash-1017` added; `deepseek-v3.2`, `deepseek-v4-flash` retired | ported (data) | snapshot | `sync160.qwen.model-lists`, updated `catalog-f09.qwen-cn-exact-allowlist-and-auth` |
| `catalog/providers/github-copilot.ts`: Gemini and Kimi reasoning effort and thinking levels, `supportsStrictTools` on Claude, `supportsStrictMode` on Responses | ported (data); the `strict: false` request field is `ai-providers` | snapshot | `sync160.copilot.strict-flags-and-thinking-levels` |
| catalog-wide removal of `supportsDeveloperRole: false` (kimi family and 19 provider files) | ported (data); behavior depends on `ai-providers` | snapshot | `sync160.catalog.release-delta-from-1.5.2` (passes), `sync160.catalog.dropped-developer-role-flag-still-sends-system` (**fails** until the default is ported) |
| `together-models.test.ts`, `fireworks-models.test.ts` data expectations (`maxTokens` 1,048,576, no developer-role flag) | ported (data); usable only after cross-package need 2 | snapshot | `sync160.catalog.together-and-fireworks-kimi-k3` (passes), `sync160.catalog.every-default-model-streams-as-the-cli-asks` (**fails**) |
| `coding-agent/schemas/models.schema.json` | ported | `src/PiSharp.CodingAgent/Schemas/models.schema.json` (upstream blob `83219445`), `PiConfigurationSchemas.cs` comments, `docs/models-schema-derivative.md` | `sync160.models-schema-v1.6.0`, updated `schemas.published-baselines-and-reviewed-derivative-integrity`, `schemas.models-derivative-only-adds-reviewed-compatibility` |
| `src/providers/compat-schema.ts` | not mine (`ai-providers`); its published form is the schema above | | |
| `catalog/README.md`, `scripts/model-data.ts` (`assertExactModelIds` removed), deleted `generate-models-strict.test.ts`, removed `model-data-validation.test.ts` cases | no-op: upstream catalog generator tooling, no native counterpart (PiSharp copies published data) | | |
| `coding-agent/docs/providers.md` | root: `website/**` | | |
| `max-thinking.test.ts`: `configuration_update` item on Codex | deferred to `ai-providers` (request behavior); the catalog flags it depends on are pinned here | | `sync160.codex.mid-conversation-effort-flags` |

### Changes worth a second look

1. **Models schema.** The embedded file was a derivative of the v1.5.0 schema carrying three compat properties ahead of upstream. Upstream published those three in 1.5.2 (own wording, `promptCacheKeyMode` as `anyOf` of constants) plus `supportsMessageThreads`; the derivative was not rebased in the 1.5.2 sync and so lacked `supportsMessageThreads`. I replaced it with the exact v1.6.0 file instead of patching two descriptions into a derivative that was already behind. Accepted and rejected values for the three former extensions are the same. The two schema cases keep their ids; the second now proves that v1.5.0 and v1.6.0 differ by exactly four properties and one description.
2. **Existing cases retargeted, not weakened.**
   - `sync152.catalog.exact-current-package-resources`, `.release-additions-removals-and-metadata` and `.fable-all-released-provider-variants` pinned the 1.5.2 bytes, counts and its +82/−21/104 delta through `CurrentModelCatalog`. They now read `ReleasedModelCatalog.Pi152` with the same numbers; the current-release equivalents are the `sync160.catalog.*` cases.
   - `resolver.defaults-exist-in-generated-catalogs` walked the 1.5.0 shards; it now walks the current release, because `gpt-6.2-sol` does not exist in 1.5.0.
   - `catalog-f09.qwen-cn-exact-allowlist-and-auth` compared Individual CN rows with the historical Team CN shard; the source is now the current Team CN rows (still identical apart from the provider id), and the list has 13 ids.
   - CliParity `sync150.no-attribution-on-any-provider-route-*` picked one model per provider and API from the 1.5.0 shard, skipping models above the 1,000,000 max-token limit. `kimi-coding/k3` crossed that limit in 1.6.0, so the cases picked a model the CLI refuses. They now pick from the current release, where the CLI resolves `--model`.
3. **Web Agent (outside my listed paths, two small source changes).** `CatalogPriceSource.ShardHash` (`src/PiSharp.WebAgent/Evals/Drift/PriceSources.cs`) named a built-in provider's price snapshot by the hash of its frozen 1.5.0 shard, and `CatalogFacts.ReadBuiltin` (`src/PiSharp.WebAgent/About/CatalogFacts.cs`) reported that catalog's shard count and manifest hash, while the counted models come from the current release. `openai.json` was byte-identical from 1.5.0 through 1.5.2, which hid this; with 1.6.0 `evals.drift.catalog-price-source-uses-the-shard-hash` failed. Both now read the release the registry serves. Cases: that one (updated) and `about.*` (one assertion added).
4. **File headers** of `BuiltinProviders.cs`, `ProviderEnvironmentKeys.cs`, `InjectedAuthenticationResolver.cs` and `ProviderAuthCatalog.cs` moved to the v1.6.0 commit. Between v1.5.2 and v1.6.0 only the Qwen files changed under `src/providers` and `env-api-keys.ts`; I also compared every simple provider's id, name and base URL in `BuiltinProviders.cs` with the v1.6.0 factories. `ModelResolver.cs` keeps its header (only the default table moved; its doc comment names v1.6.0).

## Suites

Pinned SDK, Release, default version. The whole solution compiles (`PiSharp.slnx`: 0 compile errors; the parallel solution build hit a file-copy race in `PiSharp.CodingAgent.Tests`' published fixture, and that project builds cleanly on its own).

| suite | cases | pass | fail | skip | note |
| --- | ---: | ---: | ---: | ---: | --- |
| PiSharp.Models.Tests (`--release-tarball`) | 143 | 141 | 2 | 0 | fail: `sync160.catalog.dropped-developer-role-flag-still-sends-system` (need 1), `sync160.catalog.every-default-model-streams-as-the-cli-asks` (need 2) |
| PiSharp.NativeModelCatalog.Tests | 7 | 7 | 0 | 0 | |
| PiSharp.ModelRuntime.Tests | 22 | 22 | 0 | 0 | |
| PiSharp.ProviderSync.Tests | 97 | 97 | 0 | 0 | |
| PiSharp.Authentication.Tests | 42 | 42 | 0 | 0 | |
| PiSharp.ProviderApis.Tests | 60 | 60 | 0 | 0 | |
| PiSharp.ClassifierConfig.Tests | 1 | 1 | 0 | 0 | prints a single PASS line |
| PiSharp.CliSync.Tests | 68 | 67 | 1 | 0 | fail: `sync150.kimi-coding-live-route-speaks-chat-completions` ("no developer role"; need 1; the case is unchanged) |
| PiSharp.CliParity.Tests | 226 | 215 | 0 | 11 | |
| PiSharp.InteractiveParity.Tests | 750 | 744 | 0 | 6 | |
| PiSharp.SessionParity.Tests | 127 | 127 | 0 | 0 | |
| PiSharp.Durable.Tests | 4699 | 4695 | 0 | 4 | |
| PiSharp.Durable.Hosting.Tests | 129 | 129 | 0 | 0 | |
| PiSharp.Durable.Server.Tests | 31 | 31 | 0 | 0 | |
| PiSharp.DurableCli.Tests | 119 | 119 | 0 | 0 | |
| PiSharp.WebAgent.Tests, groups `evals.`, `routing.`, `custom-providers.`, `setup.`, `playground.`, `recovery.`, `runs.`, `bench.`, `about.` | 209 | 209 | 0 | 0 | the groups that read the catalog; the full suite (about 540 cases, 17 minutes) was not run to the end |

The Models suite had 124 cases before; 19 are new (`Sync160CatalogCases.cs`). Skips are the suites' own (platform or fixture conditions), not mine.

Not run: `PiSharp.CodingAgent.Tests` (no catalog-dependent expectation in it), `PiSharp.ExtensionParity.Tests` (its only catalog use is the extension OAuth list), the rest of `PiSharp.WebAgent.Tests`, and every suite outside this area.

## Cross-package needs

1. **`ai-providers`: the `openai-completions` developer-role default** (`detectCompat`: `supportsDeveloperRole: isOpenAI`, #10741). PiSharp's default in `src/PiSharp.AI/Protocols/OpenAICompletions/CompletionsKeyAuthRequestFactory.cs` (about line 457) still is "developer unless the host is known non-standard". With the 1.6.0 catalog the explicit `false` is gone, so reasoning models of **baseten, fireworks, github-copilot (Chat Completions rows), huggingface, kimi-coding and the four qwen-token-plan providers** send the system prompt as `developer`. The other providers that lost the flag are already covered by the non-standard host list. `sync160.catalog.dropped-developer-role-flag-still-sends-system` drives one such model per provider through the CLI live route and lists the offenders; it and CliSync's `sync150.kimi-coding-live-route-speaks-chat-completions` must pass after the merge. I did not change the request code.
2. **`ai-providers` (or root): the 1,000,000 max-token limit.** `MaximumTokenMagnitude = 1_000_000` in `CompletionsKeyAuthRequestFactory.cs` (line 21, checked at line 98), `CompletionsSimpleRequestFactory.cs` (`CheckMagnitude` of the model's `maxTokens`, line 65) and `AnthropicMessagesKeyAuthRequestFactory.cs` (line 14) is a PiSharp-only limit; upstream has none and fits `maxTokens` into the context window (`simple-options.ts` `clampMaxTokensToContext`). The CLI asks for the model's own `maxTokens`, so a model above the limit is refused with "Completions request exceeds configured limits" before anything is sent. 46 chat rows were above it in 1.5.2 (none a default); 62 are in 1.6.0, among them the defaults `fireworks/accounts/fireworks/models/kimi-k3`, `opencode-go/kimi-k3.1-pro` and `together/moonshotai/Kimi-K3` (all 1,048,576), plus `kimi-coding/k3` and `k3.1-pro`, `opencode/kimi-k3` and `github-copilot/kimi-k3`. `sync160.catalog.every-default-model-streams-as-the-cli-asks` names the three defaults and passes once the limit admits them. This is not an upstream 1.6.0 change, so nobody will find it from the upstream diff. I did not change the request code.
3. **`ai-providers`: Codex `configuration_update`.** The catalog now sets `supportsMidConvoEffort` on the seven GPT-6 family Codex rows; the Codex transport must send thinking-level changes as `configuration_update` items for it to mean anything (`max-thinking.test.ts`). Also theirs: Copilot Responses `strict: false`, and `compat.supportsMaxOutputTokens` for Codex (no catalog row uses it).
4. **`cli-session`: help text.** `src/PiSharp.Cli/Pi/PiHelp.cs` lines 194-195 still list `QWEN_TOKEN_PLAN_API_KEY` and `QWEN_TOKEN_PLAN_CN_API_KEY`; upstream `cli/args.ts` now lists the four per-plan variables instead. I left the file to its owner to avoid a conflict. Also theirs to decide: deleting the unused `ModelResolver.RestoreModelFromSession`.
5. **`meta`: Node bridge virtual module.** `tools/NodeBridge/pi-host/virtual/ai/env-api-keys.mjs` lines 68-71 still map each Qwen provider to one shared variable.
6. **Root.**
   - `THIRD-PARTY-NOTICES.md` line 3 describes the current catalog as 1.5.2 (43 shards, 1,773 entries, its hashes) and line 118 says the embedded schemas are v1.5.0 copies. New values: 43 shards, 1,853 entries, tarball SHA-256 `d68312ec574bc959cb7fee26da4f4876d067aed9bbbe76e876359e60d2294747`, the integrity above, manifest SHA-256 `cc46553f3f008788e667600a873d3d83a4df8fe46c67b06c65b16fd828450e8b`, generatedAt `2026-11-20T10:48:49.496Z`; `models.schema.json` is the v1.6.0 blob `832194451e28b45820b27d765c36c1288ad04a6b`.
   - `website/src/content/docs/docs/guides/providers.md` (lines 59-61) and `reference/environment.md` (lines 119-120): Team names, a `qwen-token-plan-individual-cn` row, and the per-plan variables.
   - `.gitattributes` gained `/src/PiSharp.AI/Catalogs/Releases/1.6.0/*.json -text` (needed so the shard bytes survive checkout). The file is covered by the public integrity record, so the re-seal must include it.
   - `CHANGELOG.md`, release notes, `IMPLEMENTATION_STATUS.md`: catalog 1.6.0 numbers, new defaults, Qwen variables, the schema.
   - New public type `PiSharp.AI.Catalogs.ReleasedModelCatalog`.
   - Edits outside my listed paths, for the merge to reconcile: `src/PiSharp.WebAgent/About/CatalogFacts.cs`, `src/PiSharp.WebAgent/Evals/Drift/PriceSources.cs`, `src/PiSharp.CodingAgent/Configuration/PiConfigurationSchemas.cs` (comments), `docs/models-schema-derivative.md`, and expectations in `tests/PiSharp.{Authentication,CliParity,Durable,InteractiveParity,WebAgent}.Tests`.

## Deferred

Nothing in this package's own paths. The request-behavior items above belong to `ai-providers`.

## Commits

| commit | subject |
| --- | --- |
| `dec523bb` | Embed the exact Pi 1.6.0 model catalog as the current release |
| `cdc2bb32` | Default OpenAI, Azure and OpenAI Codex to gpt-6.2-sol |
| `851f9ec4` | Qwen Token Plan: current gateways, Team names and per-plan keys |
| `f5d0c337` | Embed Pi 1.6.0's models.json schema unchanged |
| `a6ef9a59` | Port the Pi 1.6.0 catalog tests and move expectations to 1.6.0 |
| `9fb517b1` | Web Agent: describe the current catalog, not the frozen 1.5.0 shards |
| `0af38206` | Check every 1.6.0 default model the way the CLI requests it |

This report is the commit after those.
