# MCP discovery: Pi 1.5.0 to 1.5.2

Frozen comparison: `f9a26365568127461c47a07a37dcb42c140a3ac5` to `b4afdddf44e059c67ccf5097e5317d913521b470`, inspected in `E:/AI/PiSharp/upstream-v152`. Native tree: `E:/AI/PiSharp/update-1.5.2`. Discovery only: no implementation or build performed. Requested service tier ultrafast; effective tier unverified.

## Architecture and disposition

There is no `src/PiSharp.Mcp` project. The native MCP package boundary is already split between `PiSharp.Extensions.Abstractions/Mcp` (public inert contracts/pure configuration), `PiSharp.Extensions.Runtime/Mcp` (protocol, transport, credential, and authorization runtime), `PiSharp.Cli/Mcp` (application composition and interactive sign-in), and `PiSharp.Tools/Processes/Mcp` (native process ownership). Keep those boundaries. Creating a new project is unnecessary and would impose broad dependency edits.

Most 1.5.1 changes move existing coding-agent code into the reusable MCP package. Existing behavior is often present in the CLI, but the new reusable public surface is not. The substantial behavioral gaps are split sign-in, refresh-only authorization preservation, cross-process sign-in refresh, CIMD refresh, reusable caller-owned pipe transport, and HTTP error excerpts. JSON response bounds, CR-only SSE, response disposal, and linear stdio accumulation already exist natively and need regression evidence rather than rewrites.

## Complete changed upstream inventory

All `packages/mcp` changes (37 files):

- Metadata/docs: `CHANGELOG.md`, `README.md`, `package.json`, `THIRD_PARTY_NOTICES/modelcontextprotocol-typescript-sdk.txt` (rename from `LICENSES`).
- New shared source: `src/config.ts`, `src/naming.ts`, `src/input-schema.ts`, `src/output-limit.ts`.
- Public exports: `src/index.ts`, `src/oauth/index.ts`.
- OAuth source: new `src/oauth/auth-provider.ts`, `client-settings.ts`, `sign-in.ts`, `loopback.ts`; changed `callback.ts`, `discovery.ts`, `errors.ts`, `flow.ts`, `types.ts`.
- Transports: new `src/transports/lines.ts`, `pipe.ts`; changed `stdio.ts`, `streamable-http.ts`.
- Tests: new `test/auth-provider.test.ts`, `authorization-server.ts`, `config.test.ts`, `input-schema.test.ts`, `lines.test.ts`, `loopback.test.ts`, `naming.test.ts`, `output-limit.test.ts`, `pipe.test.ts`, `sign-in.test.ts`, `fixtures/exiting-parent-server.mjs`; changed `oauth.test.ts`, `stdio.test.ts`, `streamable-http.test.ts`.

Associated coding-agent source: `src/core/mcp-servers.ts`; `src/extensions/mcp/{cli,config,index,oauth,resources,runtime,tools}.ts`. These mostly switch imports to reusable package helpers. `core/radius-mcp.ts`, auth login/logout, model-runtime credential lifetime, and terminal-question implementation belong to CLI/AI discovery; the MCP CLI consumes their APIs. Relevant tests: `mcp-command`, `mcp-extension`, `mcp-oauth-refresh`, `suite/agent-session-mcp`, `suite/mcp-oauth-server`, and regressions `11692-dispose-waits-for-oauth-refresh`, `8409-aborted-tool-auth-setup`.

MCP 1.5.2 changelog has no additional bullets. 1.5.1 adds PipeTransport, split OAuth and shared helpers; fixes bounded JSON/HTTP errors, callback shutdown, stdio quadratic accumulation, CR SSE, oversized SSE cleanup, and POSIX process descendants. License-directory rename is packaging applicability only (native project does not bundle the TypeScript package); source provenance inventories should track it under root metadata work.

## Proposed disjoint implementation assignments

### I-MCP-OAUTH (this worker): authorization, sign-in, shared configuration/helpers

Own:

- `src/PiSharp.Extensions.Abstractions/Mcp/Authentication/McpOAuthOrchestrationContracts.cs` and new public OAuth/sign-in contracts under `Mcp/Authentication`.
- `src/PiSharp.Extensions.Abstractions/Mcp/Configuration/McpConfigurationReader.cs`, `McpCatalogPlanner.cs`, plus new shared validator/helper contracts there (do not change existing configuration consumers' behavior).
- `src/PiSharp.Extensions.Runtime/Mcp/Authentication/*` as needed for refresh-only flow, reusable auth provider, client settings, typed lock-busy handling; new generic sign-in implementation under `Runtime/Mcp/OAuth` or `Authentication`.
- `src/PiSharp.Cli/Mcp/Authentication/*` including application sign-in wrapper and default host options necessary for generic composition.
- `src/PiSharp.Cli/Mcp/McpSessionHost.cs` only its OAuth adapter/composition region (coordinate any other edit).
- `src/PiSharp.Cli/Mcp/McpToolResults.cs` and new shared content limiter (pure helpers, no transport changes).
- New OAuth/helper regression test files, plus existing OAuth test files only after registration ownership agreed.

Dependencies: no transport worker code required. Root may reserve `McpSessionHost.cs` and apply a supplied narrow patch if CLI work also needs it. CLI worker owns `Commands/McpCommand.cs`, its options/provider-token wiring, terminal questions, and related auth CLI files; I-MCP-OAUTH must not edit them.

### I-MCP-TRANSPORT (separate worker): pipe, framing, HTTP excerpts, process tail

Own:

- New `src/PiSharp.Extensions.Abstractions/Mcp/Transport/McpPipeContracts.cs`; `McpTransportContracts.cs` only HTTP exception/body additions and pipe options if not new file.
- `src/PiSharp.Extensions.Runtime/Mcp/Transport/*` (all transport/frame implementation).
- `src/PiSharp.Tools/Processes/Mcp/McpProcessLease.cs` stderr tail changes, and native process lifetime only if required to prove process-tree cleanup.
- Optional new adapter file in `PiSharp.Env` or caller-supplied adapter test; do not require an Env project reference from MCP.
- Transport regression tests in new files and existing transport suite as appropriate.

No dependency on OAuth implementation beyond unchanged authenticated HTTP contract. Root should register shared test entrypoints serially. This worker must not edit OAuth files, `McpSessionHost.cs`, or CLI command files.

## Exact OAuth contracts and algorithms

Add optional `RefreshOnly = false` and `AuthorizationRedirectUrl = null` to `McpOAuthOrchestrationOptions` (append parameters to preserve source callers). The actual installed/default redirect URI must be available to the orchestrator's equality check: either append inert `RedirectUrl` to options or supply a dedicated dependency; prefer append `RedirectUrl` to options and have host `Options()` fill its admitted exact URI. Do not infer redirect from arbitrary metadata. Extend host `Options()` and explicit completion entry to pass the authorization redirect from split sign-in.

`RefreshOnly` rules from `oauth/flow.ts`:

1. Discover, select resource and scope, read client, optionally derive CIMD as before.
2. Capture **stored** client ID separately before deriving CIMD.
3. Determine effective redirect URI (CIMD variant else installed URI). If exchanging a code and `AuthorizationRedirectUrl` is provided, require exact string equality before client registration or token exchange.
4. If no client and refresh-only, return Redirect; never register.
5. With refresh token, perform refresh/save. If refresh falls through or absent and refresh-only, return Redirect before reading authorization state, creating PKCE, saving verifier, or redirecting.
6. On `invalid_client`/`unauthorized_client` in refresh-only, reread current client. Invalidate Client, Tokens, Discovery separately only if its ID still equals the captured stored ID. Keep OAuthState and CodeVerifier, and keep any client installed while refresh ran. Retry once outside catch. On `invalid_grant`, invalidate only tokens, retry once. Existing non-refresh-only invalidation remains All.

Public generic client options must carry server URL, state store, resolved settings, caller `ClientName`, optional `McpClientMetadataDocuments(BaseUrl, ListsRedirectUrl)`, and admitted HTTP/entropy/clock as native ownership requires. No ambient HTTP/client/browser acquisition in shared runtime. `McpOAuthSettings` currently lives in CLI; move/extract an equivalent public record to Abstractions (leave compatibility wrapper/adapter if useful). CLI `From(entry, resolveSecret)` remains application composition.

Store contract: retain `IMcpAdmittedOAuthStateStore` for compatibility; define an extended `IMcpOAuthServerStore` or generic options callback `WithRefreshLock<T>(Func<Task<T>>)` supporting original semantics. Existing `McpOAuthCredentialStore.ForServer` can expose adapter with admitted lock separately, without silently acquiring filesystem authority. Introduce `McpRefreshLockBusyException` and throw it only when the existing lock remains occupied for its full wait. Unexpected access/I/O/permission failures must remain original failures, not be mislabeled busy. Existing `McpOAuthRefreshLock` currently retries all `IOException`/`UnauthorizedAccessException` until deadline and throws generic `IOException`: update discrimination.

Public split surface:

- `McpSignInStart` result: authorized, or immutable/serializable `McpSignInRedirect(AuthorizationUrl, RedirectUrl, State, Scope)`.
- `StartAsync(options, token)`: immediate `McpSignInCancelledException` for aborted caller; unless step-up (`insufficient_scope`), acquire store refresh lock and reread latest state. Skip refresh if no refresh token, or CIMD replaces a stored client without configured ClientId. Attempt refresh with original client even when requested browser redirect differs. Typed lock-busy skips to browser; all other lock failure propagates. If aborted while waiting, return cancellation promptly; a delayed lock callback checks aborted token and releases without refresh. **Once refresh is sent it must finish and save rotated tokens even after caller abort**; track this work for settlement/shutdown rather than abandoning it.
- Browser step resets OAuthState; preserves registered client only for configured client or when non-CIMD client lists exact chosen redirect. Preserve document tokens only if caller documents offer this redirect. Otherwise clear client, tokens, expiry. Merge configured scope with challenge; on step-up merge previously granted scope too. Run authorization with SkipRefresh true and return actual redirect URI and requested scope from authorization URL.
- `CompleteAsync(options, authorization, response, token)`: check response State equals authorization State, then store's OAuthState equals authorization State before sending any code (superseded sign-in rejected). Exchange with `AuthorizationRedirectUrl` equality check and saved requested scope. Works in a fresh invocation sharing only state store and persisted redirect data.

Client metadata behavior: generic reusable layer supports caller-owned documents and caller-owned client name. `auto` offers a document only without configured client ID/name and when `ListsRedirectUrl` accepts chosen URI. `dcr` never; `cimd` requires documents that list it and authorization-server support for CIMD plus `none`. With issuer-response support document URL is `<base>/client.json`; otherwise `<base>/<id>/client.json` and append `/<id>` to the caller's redirect **path**, trimming one trailing slash. ID is base64url of first nine SHA256 bytes of normalized server URL without fragment (12 chars). Current native `McpOAuthClientMetadataDocuments.Create` hardcodes `/callback`; generalize while keeping CLI policy explicit.

Preserve existing PiSharp application decision from 2026-11-05: its own default `auto` is rebranded/DCR and must not silently identify as pi.dev. Generic caller-supplied document support does not relax that default or provider subscription policy. Explicit CIMD remains supported as currently configured.

Reusable `createMcpAuthProvider` equivalent:

- Token acquisition waits for existing shared refresh (ignoring that refresh's error), loads state, refreshes within 30 s expiry only if refresh token exists; proactive failure falls through with current token.
- Unauthorized reports parsed challenge, immediately requires interactive auth for insufficient scope, otherwise shares one refresh task.
- Refresh lock covers reread, token request and save. If token changed from rejected/stale token while waiting, no refresh. Resolve settings lazily only when refresh needed. No browser/client registration/PKCE mutation through this path.
- Optional explicit redirect URI supports HTTPS callback-only CIMD tokens. Refresh candidate order is explicit redirect, fixed settings loopback URI, first registered client redirect; choose first listed by caller documents, otherwise fallback `http://127.0.0.1/callback`.
- `SettledAsync()` joins the shared refresh and swallows its error (the request already reports it). Generation shutdown calls it before destroying resources.

Current native `McpAdmittedOAuthRefreshAdapter` already provides in-process task sharing, 30 s expiry skew, optional lock, challenge and settlement APIs; it requires configured/stored client and therefore cannot refresh document tokens. Current `McpDefaultOAuthHost.UnauthorizedAsync` runs full authorize and can mutate PKCE during refresh. Unify on refresh-only semantics rather than duplicate two divergent refresh engines. Current CLI one-shot `McpSignIn.SignInAsync` does not take the refresh lock and exposes no persistent start/complete data.

## Loopback / redirect helpers

Expose generic loopback listen/wait/parse helpers alongside shared sign-in while retaining CLI convenience wrapper. Settings default to `http://127.0.0.1/callback`; `localhost` listens on IPv4 loopback; configured fixed URI stays string-exact. Try configured port, else stored client's first redirect port, else ephemeral; only a borrowed stored port may fall back after bind failure. Serve server-specific extra path only when documents offered. Wait races browser and optional paste; cancel losing prompt; blank/undefined paste means canceled. Validate pasted origin and path, error, state, code, optional issuer in that order. Existing native parser already does this, but is internal.

`McpOAuthCallbackServer.DisposeAsync` already cancels a token linked into every accepted socket read/write and joins handlers, unlike the old Node server behavior. Add a test with a connected browser socket that sends no bytes; dispose must complete promptly and socket close. Preserve idempotent disposal. It currently has a 10 s read timeout but proper stop cancellation should avoid waiting for it.

## Shared pure helpers

`McpCatalogPlanner.CreateToolName` and `.Namespace` already implement upstream exact names: sanitize each UTF-16 code unit outside ASCII letter/digit/underscore to `_`, length <=64 unchanged unless collision, otherwise first 55 characters plus `_` plus first eight lowercase SHA256 hex of `server + NUL + tool`; namespace only replaces `-` with `_`. Expose a focused reusable helper or keep public planner forwarding. Existing private `Parameters` does normalization: preserve keywords, type missing/null becomes object, properties missing becomes `{}`; make reusable public.

Shared config validator must return transport-tagged result (stdio/http/invalid) with unchanged raw fields, including unknown application fields; it must not validate `exposure`, `toolExposure`, or `auth`. Preserve current CLI validation as a wrapper: resolve exposure aliases, invoke shared validator with CLI callback check, then validate app fields. Exact rules: name `[A-Za-z0-9_-]+`; object; enabled bool; description string; timeout numeric >0; reject legacy sse; HTTP URL http/https, string headers; stdio command string, string args array/env map/cwd. HTTP is selected first when URL string and type absent/http/streamable-http; stdio command with absent/stdio. OAuth callback URL HTTP loopback, no query/fragment, matching explicit callbackPort, port 1..65535; strings for client ID/secret/scope; nonblank name; auto/dcr/cimd; cimd cannot clientId/clientName and requires caller callback acceptance; configured metadata URL HTTPS or HTTP exact loopback. Shared `IsServerOverride`: command, URL and type all absent. Existing `McpConfigurationReader` validates app fields before base fields, so error ordering changes when implementing shared wrapper are intentional upstream ordering.

Content limiter public API accepts text/image content and optional caller saver; returns original content if combined text UTF-8 <=20*1024. Join text with LF; split byte budget equally head/tail at UTF-8 boundaries; marker uses Unicode scalar count; original lines exclude extra trailing newline; token estimate ceil(bytes/4). Prefix `Warning: truncated output (original token count: N)\nTotal output lines: L\n\n`. With saver, save full combined text `.txt` and append `\n\n[Full output: path (read it with offset/limit)]`, or a caught saver failure notice. Without saver append nothing, never create a file. Images follow single truncated text block. Existing `McpToolResults.LimitAsync` always saves, is private and catches only IO/access exceptions; expose generic optional saver semantics and have CLI explicitly pass existing saver. Existing naming/schema/content behavior otherwise already matches.

## Pipe / framing / transport contracts

Suggested new `IMcpPipe`: synchronous `Write(ReadOnlyMemory<byte>)`, `CloseStdin()`, `Close()` and `Task Exited`; `McpPipeHandlers(Action<ReadOnlyMemory<byte>> OnStdout, OnStderr)`; options with `Func<McpPipeHandlers, Task<IMcpPipe>> Open`, max message 16 MiB, stderr 64 KiB, optional stderr callback, close timeout default 2000 ms. No ambient process acquisition. A task-typed `Exited` avoids discarding exit-result type. Adapter from Env awaits `RemoteProcess.Exited` but ignores integer result.

Start at most once; reject already closed. Output callbacks are active during Open. If close races Open, kill newly returned pipe, observe Exited rejection, reject start closed. Send one JSON plus LF, reject unstarted/closed/ended. On program exit report incomplete non-whitespace pending frame then disconnect; report rejected Exited first unless close initiated it. Close is idempotent: close stdin, wait for Exited for grace, call Close kill if grace expires, await Exited swallowing expected close rejection. Pipe owns descendants, transport does not create process groups.

Shared line reader stores incomplete chunks, scans each incoming chunk once for LF, concatenates a frame once, copies retained data because caller can reuse buffers. Strip one final CR, ignore whitespace-only, parse/dispatch each complete frame; malformed/oversized frame reports error and continues next frame. EOF incomplete nonblank reports protocol error. Bounded stderr is **last** bytes, stderr callback receives every chunk decoded UTF8. Native stdio currently uses MemoryStream single-pass bytes, so it is already amortized linear (no quadratic concat); reusable frame parser should preserve that property. Existing `IMcpWireTransport` has only Disconnected error callback whereas upstream transport emits nonfatal errors separately: add an optional error callback on new pipe options or new compatible optional `McpWireCallbacks` property so malformed frame recovery does not force disconnect. Do not silently redefine existing Disconnected.

Env worker confirmed existing `RemoteProcess.OpenAsync(Connection, RemoteProcessOptions, token)`, `.Write(bytes/string)`, `.CloseStdin()`, `.Close()`, `Exited: Task<int>` already match requirements. Options have required Cwd, Argv, OnStdout and optional Env/InheritEnv/OnStderr. Exited rejects on close/loss/callback exception. No Env API changes needed.

`McpProcessLease` currently captures the first MaximumStderrBytes (default 1 MiB), not upstream's last 64 KiB; change bounded capture to tail, preserving existing capacity option as needed. Windows job ownership (`McpWindowsProcessLifetime`) already keeps tree lifetime independently of parent process. Upstream POSIX process group cleanup fix does not translate to Windows signal calls; document Windows job equivalent, retain platform restriction, test early parent exit with surviving descendant if supported.

HTTP already uses ReadBody limit; SSE already handles CR/LF/CRLF at byte boundaries; OwnResponse disposes on any failure, and current tests cover explicit owned-read abort settlement. Add/reuse regressions for oversize JSON, BOM and multibyte split, exact limit acceptance, CR-only event while stream remains open, oversize event body disposal. Missing error excerpts: current Status throws `McpHttpStatusException` before acquiring body. Make error inspection async; read no more than 8192 bytes, detect truncation via at most one extra read, decode prefix incrementally, dispose/abort rest. Extend status exception with bounded `ResponseText` and descriptive status+trimmed excerpt (upstream display snippet 200 chars) while preserving StatusCode/SessionExpired. Body-read error falls back empty. Do not read full body, allocate based on Content-Length, retry POST, or mask original exception with cleanup errors.

## CLI integration delegated to CLI worker

`Commands/McpCommand.cs` already rejects provider-auth login/logout and gives auth command hints. Missing in options: optional provider-token callback wired into `CreateSessionHost`; default must lazily resolve using **options.AgentDirectory** auth.json/models.json, not `LiveSessionRuntime.Default` ambient paths. Add `authProvider` field to each list JSON report when configured, including disabled entries. Preserve no runtime creation for servers without provider auth. Existing messages differ slightly; use upstream instruction `Run "pi auth <login|logout> <provider>"`. Terminal-question input handling is CLI ownership. `extensions/mcp/index.ts` and `runtime.ts` also change truncation to surrogate-safe sliceHead/sliceTail; coordinate with TUI/CLI shared text work rather than duplicate.

## Required focused evidence

- Split start/complete with persisted redirect recreated for second call; wrong response state, superseded store state, changed redirect reject before token request; requested scope retained.
- RefreshOnly does not register, redirect, or change pending state/verifier. Invalid client invalidates only matching old client/tokens/discovery; concurrent replacement stays. Network/server errors preserve tokens and pending sign-in.
- Sign-in refresh under lock; lock busy starts browser; other lock error fails; abort while blocked returns promptly and never refreshes after delayed lock; sent refresh completes/saves despite caller abort; settlement waits for it.
- Generic CIMD with HTTPS-only callback, default DCR policy, arbitrary callback path and trailing slash, absent metadata support fallback/failure, name override.
- Callback idle preconnect disposal; browser/paste race and cancellation.
- Shared validator preserves unknown app fields and supports callback predicate, independent of CLI exposure/auth; naming hash/collision; schema null/missing; optional saver no-file and save-failure; multibyte truncation.
- Pipe fragmented/reused chunks, CRLF/blank/malformed/oversized recovery, stderr tail, EOF incomplete, rejected exit, send after exit, close during open, grace/kill/idempotence; fake pipe then real Env/native process smoke where deterministic.
- HTTP bounds/cancellation and existing auth cancellation regression coverage; preserve all ownership/original-settlement tests.

Root owns test registration/build serialization, metadata/version/source-lock updates, applicability inventory and final cross-project validation.
