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
| W2 | M0 foundation and Flagship A, never-lose-a-run chats: hosting seams (`SessionStream`, `AgentFrames.ReadAsync`), `PiSharp.ModelRuntime`, `PiSharp.Web.Common`, the app skeleton, credentials and sign-in, routing, durable chat with approvals and streaming, the Chat and Settings UIs, recovery and browser tests | in progress |
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
| accounts | 100–199 | |
| chats | 200–299 | |
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

## Not done

- Everything after the skeleton in W2 (credentials, routing, durable chat, the Chat and Settings pages, recovery and browser tests) is built by the other W2 units and recorded here when they land.
- `runs.db`, Spend Guard, Playground, Compare, History, Evals, Presets, Custom providers, Appearance, Data and About are later milestones.
- Service hosting (start with Windows) and a packaged release are not planned yet.
