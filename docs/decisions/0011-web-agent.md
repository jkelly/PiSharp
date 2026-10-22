# PiSharp Web Agent

Status: approved by the project owner on 2026-10-21 (the approved plan settles PLAN.md's six open questions) and started on 2026-10-22 ("Start W2: the foundation and never-lose-a-run chats"), on branch `feature/web-agent`, cut from `feature/durable-network` at `9b0045b6`. Nothing here is released, packed or published. The plan, the HTML mockups the owner approved (W1 and W1b) and the run briefs live outside the repository, in `E:\AI\Pi Sharp Planning\PiSharp Web Agent\` (`PLAN.md`, `AGENTS.md`, `Mockups/`, `builds/`). This record starts with run W2's foundation; later runs add their sections. [Not done](#not-done) lists what is left.

## Context

PiSharp Web Agent is a local chat app and model playground for one power user on one computer: chats with any of PiSharp's providers, side-by-side comparison, a playground with every request option, and (later) spend caps and personal model tests. Until now it existed only as a plan and mockups.

Three things decided its shape:
- **The durable engine already exists.** `feature/durable-network` (decision 0008) has a multi-session host (`PiSharp.Durable.Hosting`), the device API's frame building (`PiSharp.Durable.Server`), the client reducers (`PiSharp.Durable.Client`) and a web hub on the same design system (`PiSharp.Durable.Web`). None of them is packable, so the Web Agent must live in this repository.
- **The provider catalog lives in the CLI.** The model registry, the built-in catalog, `models.json`, provider auth and live routes are internal to `PiSharp.Cli`, which a web app cannot reference.
- **The flagship is a chat that never loses a run.** Closing the tab, sleeping the computer or killing the server mid-answer must still give exactly one answer, never re-run a tool that ran, and never switch the account a chat was started with.

`docs/architecture.md` deferred browser and web interfaces. Decision 0008 started that expansion for durable sessions only; this decision is the second one: a web product on top of it.

## Decision

1. **Location.** `src/PiSharp.WebAgent`, an ASP.NET Core MVC app (`Microsoft.NET.Sdk.Web`, not packable) in this repository, on `feature/web-agent`. It references `PiSharp.Web.Common`, `PiSharp.Durable.Hosting`, `PiSharp.Durable.Server` and `PiSharp.Durable.Client`. Decision 0008's hub may later become its `Durable` area.
2. **Model runtime.** The registry, catalog, `models.json` config, `ConfigValueResolver`, remote catalog, provider auth catalog and live provider routes move out of the CLI into a new unpacked library, `src/PiSharp.ModelRuntime` (`git mv`, namespaces kept, `InternalsVisibleTo` for the CLI, the Web Agent and their tests). One new seam, `IProviderCredentialSource`, replaces the concrete `auth.json` store; an `IRegistryProviderLayer` hook keeps Llama in the CLI. `AuthJsonCredentialStore`, `ProviderLoginHost`, Llama, the CLI's `DurableModels` and the TUI stay in the CLI. If the extraction stalls, a thin router over the public `NativeProviderFactory` (openai, google, anthropic by key, codex, copilot) replaces it.
3. **Streaming.** Server-Sent Events, one stream per browser tab, with the hub's `hello` / `{sub, frame}` / `view` protocol (`GET /api/stream`, `PUT /api/stream/{id}` subscriptions), and server-rendered Razor fragments. Commands are idempotent JSON POSTs under `/api/...`. No SignalR.
4. **Storage.** One data folder, `%LOCALAPPDATA%\PiSharpWebAgent\` by default:

   | What | Where |
   | --- | --- |
   | Chats | durable sessions under `device\` (one `agent.sqlite` per chat) |
   | Web data: account metadata, active accounts, chat bindings, settings, the spend ledger, evals | `webagent.db` (SQLite, WAL), with migrations in one id range per area |
   | Playground, Compare and Tool Bench runs (W4) | `runs.db`: kept 30 days, pinned runs exempt, raw payloads opt-in with a 7-day cap |
   | Secrets | `credentials.json`, each value encrypted with ASP.NET Core Data Protection (key ring in `keys\`, protected with DPAPI on Windows) |

   `PISHARP_WEBAGENT_DATA` moves the folder, and `--data <folder>` overrides both.
5. **Sessions.** Each chat is a durable session hosted in process, not PiSharp's JSONL session store. `DurableDeviceHost` runs inside an `IHostedService` (running `pisharp durable serve` instead would read the CLI's `auth.json` and could not use web accounts). Markdown export comes from the Client's `Transcript`. The tool working directory is a setting of its own (Settings › Tools), passed as `StartDirectories`.
6. **Tool approval.** A `web-approval` durable extension, built on the command guard's `BeforeTool` pattern (`src/PiSharp.Durable.Hosting/Apps/Extras.cs`). It decides before the tool's intent is recorded, so a crash while waiting asks again and a crash after deciding never re-runs the tool. The decision is memoised (`MemoAsync`); the first tab to decide wins. Read-only tools never ask. Questions and plan reviews use the existing `user-interaction` extension.
7. **Accounts.** A chat is bound to its provider account when it is created, and keeps it. Each request carries `metadata.pisharp {account, tier, op}` through the new `DurableDeviceOptions.SessionStream` hook; the harness saves it with the prepared request and reuses it on recovery, and the host route strips it before anything reaches a provider. An unknown, removed or revoked account fails with the `expired` state; nothing falls back to another account. Anthropic is API key or Amazon Bedrock only (no subscription OAuth); ChatGPT/Codex sign-in is opt-in.
8. **One user, this computer.** The app binds `http://127.0.0.1:5180` and refuses any `--urls` that is not loopback. There are no login pages. `PiSharp.Web.Common`'s HostGuard answers 421 to a foreign `Host` (DNS rebinding), 403 to a cross-origin write and 415 to a non-JSON API write, and sends a strict Content-Security-Policy (no inline script). Secrets never reach the browser, a log or a command line; pages show masked hints only.
9. **Offline mode.** `--faux` runs on `FauxDurableModels` and a faux catalog, with no provider code path registered at all. Every automated test uses it, or recorded and scripted transports.

## Milestones

Each milestone is one workflow run of units in their own git worktrees, integrated into `feature/web-agent`, with an owner gate after it.

| Run | Milestone | State |
| --- | --- | --- |
| W1 | Mockups for chats that never lose a run, Spend Guard, and Model Arena with Drift Watch | approved |
| W1b | Mockups for agent compare | approved |
| W2 | M0 foundation and Flagship A, never-lose-a-run chats: hosting seams (`SessionStream`, `AgentFrames.ReadAsync`), `PiSharp.ModelRuntime`, `PiSharp.Web.Common`, the app skeleton, credentials and sign-in, routing, durable chat with approvals and streaming, the Chat and Settings UIs, recovery and browser tests | integrated on `feature/web-agent`; owner gate pending |
| W3 | Flagship B, Spend Guard: worst-case reservation before every call, caps per request, chat, day, month, provider and account, one ledger in `webagent.db` | planned |
| W4 | Playground, Compare, History, the run inspector, `runs.db` and agent compare | planned |
| W5 | Flagship C, Model Arena and Drift Watch: blind votes, a personal leaderboard, pinned test cases and scheduled suites | planned |

## App skeleton (W2, WU4)

**Start.** `Program.cs` reads `--faux` and `--data` itself (ASP.NET Core's command-line provider would read `--faux --data x` as `faux=--data`), passes the rest (`--urls`, configuration) to the host, and stops with exit code 2 and a one-line reason for a bad `--data` or a non-loopback `--urls`. The content root is the output folder, as in the hub. `WebAgentHost` holds the core: `AddWebAgentCore`, `AddWebAgentFallbacks`, `UseWebAgentCore`, `MapWebAgentCore`. Each area plugs in through its module (`Credentials/CredentialsModule.cs`, `Chat/ChatModule.cs`, `Routing/RoutingModule.cs`, `Settings/SettingsModule.cs`: `AddXxxArea(services, options)` and `MapXxxArea(app)`), called from `Program.cs` before the fallbacks. The fallbacks register only what no area did: the faux models as `IDurableModels`, `FauxModelCatalog` as `IModelCatalog`, an empty `IRecentChats`. With `--faux` the faux models and catalog replace whatever an area registered. `GET /api/health` answers `{status, faux}` for scripts and tests.

**Data folder.** `WebAgentOptions` resolves the folder (`--data`, then `PISHARP_WEBAGENT_DATA`, then `%LOCALAPPDATA%\PiSharpWebAgent`) and names `device\`, `keys\`, `webagent.db` and `credentials.json`. At start the app creates the folders, migrates `webagent.db` and protects a probe value, so the Data Protection key ring exists (or a broken one fails the start) before the first secret is saved.

**`webagent.db`.** `WebAgentDb` uses the hub's exact SQLite pins (`Microsoft.Data.Sqlite` 10.0.12, `SQLitePCLRaw` 2.1.12), WAL, unpooled connections, a 5-second busy timeout and foreign keys on. Migrations are registered in DI (`AddWebAgentMigration`), each with an id inside its area's range:

| Area | Ids | Taken |
| --- | --- | --- |
| core | 1–99 | 1 `app_info` |
| accounts | 100–199 | 100 `accounts`, `active_accounts` |
| chats | 200–299 | 200 `chats` |
| settings | 300–399 | 300 `settings` |
| spend (W3) | 1000–1999 | |
| evals (W5) | 2000–2999 | |

`schema_migrations` records every applied id, so a lower id added in a later release still runs; each migration and its record commit in one `BEGIN IMMEDIATE` transaction, so a failed migration leaves nothing and concurrent starts apply it once. `PRAGMA user_version` holds the highest applied id for diagnostics only. An id outside every range, or used twice, fails the start.

**Contracts** (`Contracts/`), which the areas implement:
- `IModelCatalog`: providers (id, display name, logo key, auth kinds, billing kind) and their chat models (id, name, reasoning and thinking levels, context window, max tokens, input kinds, cost per million tokens). `FauxModelCatalog` lists `faux/echo`, `faux/echo-small` and `faux/echo-thinking`; WU6's catalog replaces it. Every listed model must resolve through `IDurableModels.GetModel`.
- `IRecentChats`: the sidebar's chats (id, title, updated, running).
- `ISettingsStore`: typed sections, each one JSON document in `webagent.db` (camelCase, enums by name), read as its defaults when missing or unreadable, validated on save, with a `Changed` event so running services follow the pages. The M0 sections are `defaults` (provider, model, thinking level), `tools` (working directory, default the user's home; approval mode `ask`, `allowList` or `auto`, default `ask`; allow-list rules) and `models` (hidden `provider/model` keys). None holds a secret.

**Shell.** `_Layout` (sidebar with New chat · Playground · Compare · Tool Bench · History · Evals · Presets · recent chats · Settings, the top bar with the theme toggle and shortcuts button, banners, the shared confirm and shortcuts modals, the toast area), `_SettingsLayout` with `_SettingsNav`, and `_ProviderModelSelect`, the two linked dropdowns every model choice uses (only the chosen provider's list is enabled, so a form posts the right pair without script). Pages that later milestones build go to one neutral "Coming in a later milestone" page; nothing pretends to exist. `wwwroot/js/shell.js` holds the shell's behaviour (theme, toasts, confirm, the linked dropdowns, global shortcuts); `wwwroot/css/site.css` is the mockups' `app.css` minus what `PiSharp.Web.Common`'s `shell.css` already holds, plus the M0 settings pages' own blocks. The theme comes from `PiSharp.Web.Common` (`_WebCommonHead`), not a copy. Static files are always revalidated (`Cache-Control: no-cache`), because modules import each other by plain URL.

**Tests.** `tests/PiSharp.WebAgent.Tests` runs the app as a child process with `--faux`, a temporary data folder and a free port, with provider keys and URL settings removed from its environment, and covers the start, the loopback-only binding, the guard and CSP, the data folder and its overrides, migrations, the key ring, every shell page and asset, the settings store, the faux catalog and the shell partials.

## Hosting seams (W2, WU1)

**Stream metadata.** `DurableDeviceOptions.SessionStream` is a per-session hook, `Func<string sessionId, JsonObjectValue?>`. When a host sets it, every model request of the session carries `metadata.pisharp = {<host fields>, op}` in its `DurableStreamOptions` (`SessionHarnessSettings.Stream`). Generation's prepare phase and compaction's summary request already save the stream options in the task checkpoint, so a recovered request keeps its `op` and account, and every new request (a retry prepared again, a summary request) gets a new `op` (a GUID) and the current binding. `op` is reserved: the host's value replaces one the hook returns. A hook that throws goes to `OnReport`, and the request carries `op` alone. `SessionStreamMetadata` builds, reads (`Field`, `Operation`) and strips the object. `NativeDurableModels`' own routes map only `metadata.user_id`, so the object never reaches a provider. The hook runs on every settings resolution, so it must be cheap and non-blocking (the Web Agent answers it from an in-memory map of the chat bindings). A deferred response's poll carries no stream options; no provider supports deferred responses today.

**In-process frames.** `AgentFrames.ReadAsync(session, agentId, options, ct)` (public in `PiSharp.Durable.Server`) yields one agent's frames (a snapshot with the inbox, requested docs, batches, inbox changes) for a `HostedSession` with no HTTP; `DeviceStreams.AgentAsync` now calls it after `hello`. `AgentFrames.ActiveEntriesAsync`, `AgentFrames.InboxAsync` and `HostedSession.SubscribeChanges` are public too. The device server and the Web Agent's `AgentViewHub` both read agent frames through it.

## Model runtime (W2, WU2)

`src/PiSharp.ModelRuntime` (not packed) holds the model registry, the built-in catalog, `models.json` config, `ConfigValueResolver`, the remote catalogs, `ProviderAuthCatalog`, `ProviderLiveAuthentication` (with Anthropic's), `LiveProviderRoutes`, `LiveModelCatalog`, `LiveSessionSelection` and their helpers: 21 files moved with `git mv`, keeping their `PiSharp.Cli.*` namespaces. The catalog's JSON shards stay in `src/PiSharp.Cli/Models` and are embedded by link under their old logical names. Its seams:
- `IProviderCredentialSource` (`IAdmittedOAuthCredentialSource` plus `ReadEntryAsync` and `ListAsync`) replaces the concrete `AuthJsonCredentialStore` everywhere in the runtime. The store stays in the CLI and implements it; the Web Agent's `WebCredentialSource` implements it per account.
- `LiveRouteRuntime` is the route-input base record. The CLI's `LiveSessionRuntime` derives from it (auth path, defaults, the Llama layer).
- `IRegistryProviderLayer` (`ModelRegistryOptions.ProviderLayers`) replaces the registry's built-in llama.cpp code; Llama stays in the CLI as `LlamaRegistryLayer`. A registry built without layers has no llama.cpp provider.
- `ProviderAuthCatalog.BuiltinExtensions` is a static hook that the CLI's module initializer sets to list llama.cpp. A process that never loads `PiSharp.Cli`, such as the Web Agent, does not list it.

It references `PiSharp.CodingAgent`, `PiSharp.Tools`, `PiSharp.Rpc`, `PiSharp.Extensions.Runtime` and `PiSharp.Extensions.Agent` (the last two because `PiPayloadBudget` moved whole), and never `PiSharp.Cli`, which `tests/PiSharp.ModelRuntime.Tests` checks by reflection.

## Shared web library (W2, WU3)

`src/PiSharp.Web.Common` is a Razor class library (`Microsoft.NET.Sdk.Razor`, not packed) that references no other PiSharp assembly. Its static files are served from `_content/PiSharp.Web.Common/`. It holds:
- `FragmentRenderer` and `MarkdownRenderer` (with its sanitizer and `Sanitize`);
- `HostGuard` with `HostGuardOptions` (hosts, ports, JSON paths, the 421 text and an optional Content-Security-Policy) and the `AddWebCommon`, `AddHostGuard` and `UseHostGuard` extensions;
- `WebCommonAssets` (URL constants), `ProviderLogos` and the `_WebCommonHead` partial;
- `stream.js` (`MuxStream`), `swap.js`, `theme-boot.js` and `theme.js`;
- `pisharp-theme.css`, `shell.css` (the `app.css` blocks both apps use), and the vendored Bootstrap 5.3.3, Bootstrap Icons 1.11.3, the mark, the favicon and the provider logos.

PiSharp Durable Web (decision 0008) runs on it and now sends `HostGuardOptions.DefaultContentSecurityPolicy`; `Hub:AllowedPorts` joins `Hub:AllowedHosts`.

## Credentials, accounts and sign-in (W2, WU5)

**Accounts.** An account is one credential of one provider. There are four kinds:
- `apiKey`: a key stored in the app.
- `oauth`: a subscription sign-in (ChatGPT for `openai-codex`, GitHub Copilot).
- `environment`: a variable from `ProviderEnvironmentKeys` that the host was started with. Read-only.
- `ambient`: credentials the provider's SDK finds itself, such as the AWS credential chain or Google application-default credentials. Read-only.

Account metadata lives in `webagent.db` (migration 100). The `accounts` table holds:
- id, provider, label, kind, status (`connected`, `expired` or `unavailable`, with a reason);
- a masked hint;
- the sign-in's identity (the ChatGPT account id);
- non-secret settings and details;
- expiry, created, updated and last-used times, and the last connection test.

The `active_accounts` table holds each provider's chosen account.

The active account of a provider is the one the user chose. When there is none, or the chosen one is unavailable, it is the first usable account in this order: sign-in, stored key, environment variable, ambient credentials, oldest first. As a result:
- A first stored key or sign-in becomes active ahead of a variable.
- Removing the active account passes the choice to the next stored account. When no stored account is left, the variable serves again.
- Nothing else moves the choice.

A chat binds to an account id and keeps it (WU7). Nothing falls back from one account to another. An expired or unavailable account fails its chats clearly.

Environment and ambient accounts are synced into rows once per process (ids `env-<provider>-<variable>` and `ambient-<provider>`). A variable that goes away leaves its row `unavailable` instead of deleting it. `ANTHROPIC_OAUTH_TOKEN` never becomes an account: Anthropic is API key or Amazon Bedrock only.

**Secrets.** `credentials.json` is the only place a secret is kept.
- Each value is encrypted with Data Protection, using the key ring in `keys\` (DPAPI on Windows).
- Each kind of secret has its own purpose, `PiSharp.WebAgent.Credentials.ApiKey.v1` or `PiSharp.WebAgent.Credentials.OAuth.v1`, narrowed by the account id. A value copied to another account, or read as another kind, does not decrypt.
- A write goes to a temporary file in the same folder, is flushed to disk, and then replaces the file in one move.
- An unreadable file is left untouched, and every secret read and write then fails with that reason.

The two stores are written in an order a crash cannot turn into a visible broken account:
- A new secret is written before its row. An orphan secret is pruned at the next start.
- A row is deleted before its secret.

Pages see a masked hint at most: a known key prefix, an ellipsis and the last four characters, or nothing for a value under 12 characters. There is no "reveal". Provider and auth-server messages are scrubbed of keys, tokens and JSON bodies before a page or a log sees them.

**Services.** `CredentialVault` is registered once under two interfaces:
- `IAccountStore` gives pages metadata and writes: list, get, active, add a key, replace a key, rename, set active, remove, mark expired, and a `Changed` event.
- `ICredentialVault` gives routing secrets, server-side only. `ReadAsync(accountId)` returns the key, settings or stored sign-in. `OAuthSource(accountId)` is the `IAdmittedOAuthCredentialSource` for `StoredOAuthLifecycle`. Its modifications are serialized per account, refreshes are stored before they are returned, and a removed account is never written back.

**Sign-in.** `SignInFlows` runs PiSharp.AI's own flows on the server and answers their prompts for the page:
- `OpenAICodexOAuth`: ChatGPT with PKCE through the loopback callback on port 1455, or OpenAI's device code.
- `GitHubCopilotOAuth`: GitHub's device code, the Copilot token exchange and model enabling.

Starting a sign-in returns either the authorize URL or the user code and verification URL. The verifier, the codes and every token stay on the server. When port 1455 is taken (for example by the Codex CLI), the page asks the user to paste the redirect URL, and a wrong `state` fails the flow.

A completed sign-in is stored:
- into the account being signed in again, which keeps its id and chats; a different identity is refused and nothing changes;
- otherwise into the account with the same identity;
- otherwise as a new account.

Other rules:
- One flow runs per provider. Starting another cancels the first.
- A flow is given up after 15 minutes.
- Only `openai-codex` and `github-copilot` sign in. Anthropic is refused.
- With `--faux` nothing starts and no connection test runs: the app is offline.

**Connection test.** It runs only on the user's click, as one GET of the provider's models list, never a completion:
- OpenAI-compatible: `{base}/models` with a Bearer token.
- Anthropic: `/v1/models` with `x-api-key`.
- Google: `/v1beta/models` with `x-goog-api-key`.
- Copilot: its own models list.
- A stored ChatGPT sign-in is checked by resolving it, which needs a request only when a refresh is due. A failed refresh marks the account expired.

**API** (`/api/credentials`, JSON, `Cache-Control: no-store`, writes as JSON POSTs under HostGuard):
- `GET accounts[?provider=]`, `GET accounts/{id}`.
- `POST accounts`, `accounts/{id}/key`, `accounts/{id}/rename`, `accounts/{id}/activate`, `accounts/{id}/remove`, `accounts/{id}/test`.
- `POST test` tests a key before it is saved.
- `GET signin`, `POST signin`, `GET signin/{flowId}`, `POST signin/{flowId}/code`, `POST signin/{flowId}/cancel`.
- Errors are `{error}` with 400, 404 or 409.

**Tests.** `Credential*Cases.cs` cover:
- the vault in process, with a fake environment and fake OAuth endpoints (an in-memory HTTP handler) and a real loopback callback on a free port;
- the API end to end against the `--faux` app.

They include a Data Protection round trip, including another key ring, account and purpose. They scan for planted secrets in `credentials.json`, `webagent.db` and its WAL, the key ring, the log, every API answer and every shell page. They also cover masked hints, the active account per provider, environment accounts, validation, the per-account OAuth source, orphan pruning, both sign-in flows (callback, paste, device code), re-sign-in, cancel, replace and time-out, and the connection tests.

## Durable chats (W2, WU7 and WU8)

**Device.** `DeviceHostService` opens one `DurableDeviceHost` on `device\` with the registered `IDurableModels`, and no jobs or schedules. Its `SessionStream` answers `{account, tier}` from the chat binding, and the host adds `op`. Migration 200 (`chats`) binds each chat to its session, provider, model, account id, thinking level, tier, current agent, title and archived flag. A chat binds its provider's active account at creation (`IChatAccounts`, which routing implements over the account store) and keeps it.

**`ChatService`.** Create and send (a browser `requestId` de-duplicates retries), steer and follow-up while busy, abort, withdraw, rename, archive, fork from any entry, edit-and-resend, retry, compact, change the model, thinking level or tier (409 while busy), answer questions, review plans and export Markdown. It is also the sidebar's `IRecentChats`.

**`web-approval`.** It checks the call's memo first, then asks in `ask` mode, or in `allowList` mode when no rule matches. Read-only tools never ask. `ApprovalBroker` is keyed by session, agent and task; the first decision wins, and a later one gets 409 `already_decided`. Only human decisions are memoised. "Allow in this chat" lasts until a restart; "Allow always" adds allow-list rules.

**Streaming.** `AgentViewHub` keeps one reference-counted `AgentFrames.ReadAsync` feed per agent, reduces it with `AgentModel`, and renders changed items with `_ChatItem` through `FragmentRenderer` at most every 150 ms. A new subscriber gets the whole view first. A feed also renders again when the device's running flag changes. `ChatStreamMux` serves `GET /api/stream` and `PUT /api/stream/{id}`.

**Pages.** `/`, `/chat/new` and `/chat/{id}` map `chat-new.html` and `chat.html`: streaming, reconnecting, background, interrupted, dropped and stopped answers, provider errors with details, the expired state with Retry (it forks and sends again with the same account), the approval card and modal (back after a reload), notices, message info, the session details pane, the session tree, the kept-running toast and the composer's slash menu. `ChatFailures` classifies a failed answer; routing's `Account expired (<reason>): ...` errors show as the expired state. Spend Guard states wait for W3.

## Routing (W2, WU6)

`RuntimeModelCatalog` lists every non-virtual chat model that has a live route, from `PiSharp.ModelRuntime`'s built-in catalog (no `models.json`). `WebDurableModels` wraps `NativeDurableModels` with `HostTransport = AccountRouter.RouteAsync` and no environment or key resolver, so nothing falls back. `AccountRouter` reads `metadata.pisharp.account`, strips `metadata.pisharp`, and routes through that account's own generation: a `LiveRouteRuntime` over `WebCredentialSource`, one `StoredOAuthLifecycle` per account, the account's environment allowlist and its `baseUrl`. An unbound, unknown, removed, mismatched, expired or unavailable account stops the request before anything is sent (`AccountRouteErrors`). A provider 401 or a rejected refresh marks the account expired. `--faux` registers no provider code path.

## Settings (W2, WU9)

`/settings` maps the Providers, Provider detail (one view for every provider), Accounts, Models, Defaults and Tools mockups, and the API key, ChatGPT sign-in and Copilot device-code modals (no Anthropic sign-in). A key is POSTed once to `/api/credentials` and never echoed; pages show the masked hint. `/api/settings` saves Defaults, Tools and model visibility. Making an account active is always an explicit choice.

## Recovery, browser tests and CI (W2, WU10)

`Recovery*Cases.cs` kill the app's process tree mid-stream, mid-bash, while an approval waits and during an account switch, then restart it on the same data folder. `tests/PiSharp.WebAgent.BrowserTests` (Microsoft.Playwright 1.63.0, `--faux`) fails on any page or console error. It covers streaming, catching up after a reload, approvals across tabs and reloads, forking from the tree, a planted API key that never reaches the page, and the main pages at 375 px in both themes. `.github/workflows/durable.yml` runs `PiSharp.ModelRuntime.Tests` and `PiSharp.WebAgent.Tests` in its test matrix, and the browser suite in a `webagent-browser` job.

## Not done

- W2 leaves out: Spend Guard states; an account label per message (the chat's bound account is shown); a "waiting for approval" dot in the sidebar; approval grants in Settings › Tools; ChatGPT sign-in for provider `openai`, and xAI and OpenRouter OAuth; key references (`!command`, variables); deferred-response polls through accounts; a subagent or model change to another provider inside one chat (it keeps the chat's account and fails as `mismatch`).
- `runs.db`, Spend Guard, Playground, Compare, History, Evals, Presets, Custom providers, Appearance, Data and About are later milestones.
- Service hosting (start with Windows) and a packaged release are not planned yet.
