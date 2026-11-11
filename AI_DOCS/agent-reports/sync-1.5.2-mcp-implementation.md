# I-MCP-OAUTH implementation

Implemented against frozen Pi v1.5.2, preserving the native Extensions/CLI project boundaries. No build or test run was launched by this worker; root owns serialized validation.

## Public reusable API

- `McpOAuthClientSettings`, `McpClientMetadataDocuments`, `McpOAuthClientOptions`, split sign-in request/results, and typed `McpRefreshLockBusyException` are in `Extensions.Abstractions/Mcp/Authentication/McpSignInContracts.cs`.
- `McpOAuthSignIn.StartAsync` / `CompleteAsync` / `SettledAsync` use caller-admitted flow and lock callbacks. `McpSignInRedirect` is persistable across requests. Pending-state mismatch is rejected before exchange, refresh holds the lock, queued-lock cancellation returns promptly and suppresses delayed refresh, and an already sent refresh saves tokens independently of caller cancellation.
- `McpOAuthAuthProvider` supplies stored-token authentication, single-flight refresh, lazy settings resolution, optional explicit redirect URL, and refresh settlement. Production `McpSessionHost` now uses it; HTTP physical operations remain caller-admitted.
- `McpOAuthOrchestrationOptions` appends `RefreshOnly`, `AuthorizationRedirectUrl`, and `RedirectUrl`. Refresh-only neither registers nor writes pending authorization state/PKCE; invalid-client invalidation preserves concurrent replacement clients and pending state. Code exchange checks the actual redirect URI before sending the code.
- Shared configuration `McpConfigurationReader.ValidateServer` returns a transport-tagged result and retains unchecked application fields. Existing CLI validation keeps app exposure/auth validation and policy. `IsOverride` and `McpCatalogPlanner.NormalizeInputSchema` are public; existing public naming/namespace methods already implement upstream behavior.
- `McpContentLimiter` is a public 20 KiB UTF-8 limiter with optional saver, Unicode boundary truncation, images retained, and save-error text. CLI `McpToolResults` now calls it with its explicit saver.

## Application adapter

`McpSignInClient.Create` adapts the existing admitted OAuth host to the generic flow. It supports caller client name and metadata documents. Its default remains PiSharp's previously approved rebranded policy: auto uses DCR, while explicit CIMD uses the existing pi.dev document contract. `TryCreate` and `ServerSpecificPath` generalize document paths for caller-owned HTTPS callbacks.

`McpSignInClient.ListenForLoopbackSignInAsync` and `WaitForLoopbackSignInAsync` expose loopback/paste composition. The existing `McpSignIn.SignInAsync` wrapper now uses split sign-in and retains old prompt/callback APIs. Manager sign-in passes the per-server refresh lock; CLI auth worker is separately adding that property to shell sign-in.

## Regression source and registration

New `tests/PiSharp.McpSync.Tests/Sync152McpCases.cs` contains `Sync152McpCases()` returning nine cases. Root should register `cases.AddRange(Sync152McpCases());` in that project's Program.cs:

1. Shared validator leaves app fields unchecked and enforces caller CIMD callback validation; public schema defaults.
2. Optional output saving and Unicode truncation; ordinary saver failure notice.
3. Split start and completion after JSON persistence; superseded store state cannot exchange.
4. Cancellation while refresh lock is queued; delayed lock does not refresh.
5. Cancellation after token request starts; refresh original remains tracked and saves rotated token.
6. Typed busy-lock fallback versus ordinary lock failure.
7. Refresh-only flow with no stored client performs no registration/authorization/PKCE work.
8. Caller-specific CIMD redirect path with trailing slash.
9. Two direct McpSessionHost instances with different agent directories: lazy credentials, no global fallback for the empty one, and current keys reread independently.

Existing occupied-callback cancellation test still applies, but one-shot SignInAsync now joins a token request that has begun before returning (bounded by existing 15 s OAuth request timeout). Generic StartAsync returns cancellation promptly and its SettledAsync tracks that ongoing original.

Transport worker owns pipe/framing/HTTP error excerpts/process tail and its tests. CLI auth worker owns Commands/McpCommand.cs and Radius; no changes to those files were made here.

## Validation follow-up

The root validation worker ran the full MCP sync suite: all eight new cases passed; the only failure was an older step-up test expecting `OnUnauthorized` to open a browser. That expectation predates the new refresh-only auth-provider contract. The test now asserts no automatic redirect, then starts an explicit split sign-in and verifies the granted-plus-challenged scope. Rerun is delegated to root validation. Direct `McpSessionHost` provider-token fallback also now lazily resolves auth.json and models.json in that host's own AgentDirectory, consistent with the CLI worker's explicit per-directory provider-token wiring.
