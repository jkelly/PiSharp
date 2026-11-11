# I-MODELREFRESH implementation

Implemented reviewed A4 against upstream v1.5.2 `b4afdddf44e059c67ccf5097e5317d913521b470` (`packages/ai/src/models.ts`, `resolveNetworkRefresh`). Requested tier ultrafast; effective tier unverified.

## Contract and changes

- `ModelRegistry.RefreshAsync` now requires `CheckAuth` to pass before the network phase of a public pi.dev catalog overlay. Its existing `RequiresCredential = false` branch is the configured-only policy: no OAuth/key resolver runs, and `RemoteRefreshContext.CredentialKey` remains null.
- Configured expired OAuth and configured API keys permit public catalog HTTP. Unconfigured providers skip HTTP, including forced refresh, while their saved catalogs can still restore. Auth-check exceptions remain per-provider refresh errors and do not suppress the next provider's refresh.
- Refresh rebuilds composition immediately after reading `models.json`, invalidating cached auth checks and reflecting the new config before deciding whether to use the network. The existing final rebuild still publishes refreshed models.
- `ICatalogOverlay.RequiresCredential` documentation now describes both credential-required and configured-only policies. Personalized OpenRouter catalogs continue resolving OAuth/custom API keys and honoring custom base URLs. HTTP/auth errors and unsupported-catalog handling are unchanged.
- No catalog metadata, shared project/registration files, live credentials, or provider network calls changed or used.

## Regression coverage and registration

Added `tests/PiSharp.ModelRuntime.Tests/CatalogRefreshAuthCases.cs`, eight cases with prefix `catalog-refresh-auth.`. Its static `CatalogRefreshAuthCases` method is discovered by the existing reflection runner; no `Program.cs` or project-file registration is necessary.

Cases cover unconfigured forced refresh and saved/offline restore; actual expired OAuth snapshots with zero resolver/flow/auth-HTTP activity and successful public catalog HTTP; API-key HTTP without authentication headers; invalidating cached configured/unconfigured status; auth-check exceptions isolated to their provider; personalized OAuth resolution; custom command-key/base-URL preservation; tolerated unsupported-catalog responses versus real HTTP and OAuth-resolution failures.

Static review and `git diff --check` completed. No builds or test executions performed, as required by root's serialized validation policy. Suggested integration validation: `dotnet run --project tests/PiSharp.ModelRuntime.Tests -- --only catalog-refresh-auth.` followed by existing `PiSharp.Models.Tests` remote/OpenRouter coverage.
