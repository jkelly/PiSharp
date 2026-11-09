# I20 follow-up — F09 Copilot classifier runtime

Status: source implemented; I13 central validation pending. No live calls or commits.

Source: exact `upstream-2026-11-08` commit `7d073328e44337d3292a7db9b0f0308f679c5e97`, `packages/ai/src/providers/github-copilot.ts`, `github-copilot.models.ts`, and `auth/oauth/github-copilot.ts`. Catalog metadata is owned by I06 in the reviewed postbaseline layer; historical catalog shards remain intact.

## Runtime contract

- `github-copilot` now registers its `typesafe-system-one` classifier implementation. The provider wrapper appends `/v1` after credential resolution; the existing System One transport then appends `/systemone`. Other classifier implementations are unchanged.
- Default CLI model-operation construction now applies the reviewed catalog layer before models.json overrides. Copilot classifier metadata therefore reaches native extensions, Pi extension model calls, and codemode.
- Stored Copilot OAuth keeps the existing `StoredOAuthLifecycle` and `GitHubCopilotOAuth` refresh implementation: expired credentials exchange the GitHub refresh token for a Copilot token and refresh available model ids. Per-request auth derives the account endpoint from the resulting token (or enterprise domain), before the classifier's `/v1` projection.
- Explicit keys still bypass stored OAuth, use the selected model/configured endpoint, and do not trigger an exchange. Environment Copilot tokens similarly use the catalog/configured endpoint. Provider-level models.json config continues to compose normally; stored OAuth's account endpoint takes precedence exactly as upstream.
- Static Copilot model headers remain model metadata; explicit request headers retain precedence. No additional Copilot chat-only headers are invented for the classifier API.

## Public API addition

`ProviderAuthResult.AvailableModelIds : ImmutableHashSet<string>?` carries an optional subscription allow-list. `null` means unrestricted, an empty set allows no models. Copilot's provider filter applies it to all model types when `IsOAuth` is true; other providers ignore it. Availability reads the stored allow-list without refreshing OAuth or issuing HTTP. The field contains model ids, never credential values.

## Changed paths and validation

- `src/PiSharp.AI/ModelOperations/ModelOperationsRegistry.cs`: Copilot registration, optional auth allow-list and provider filtering.
- `src/PiSharp.AI/ModelOperations/CopilotSystemOneClassifier.cs`: narrow provider path adapter.
- `src/PiSharp.Cli/Extensions/NativeExtensionModelOperations.cs`: reviewed catalogs, Copilot account endpoint and persisted allow-list.
- `tests/PiSharp.ClassifierConfig.Tests/CopilotCases.cs`: fake HTTP uses the actual OAuth refresh implementation; verifies token exchange, account model discovery, persistence, availability without refresh, account-vs-configured endpoint precedence, `/v1/systemone`, System One body/cost, model/header precedence, explicit and environment keys, and text-only rejection before HTTP.

Focused command sent to I13: `tools/GapValidation/Run-Tests.ps1 -Project tests/PiSharp.ClassifierConfig.Tests -Label I20-classifier-config`. Existing classifier regression command also supplied. Source-only scoped `git diff --check` passes; passing execution evidence is pending.
