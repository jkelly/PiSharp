# Changelog

## [1.5.0.1]

**Unreleased — preparation in progress.** The published release remains 1.5.0. This candidate keeps the Pi 1.5.0 baseline and adds reviewed backports and native implementations; no release tag or candidate package publication is claimed. See the [candidate guide](https://pisharp.ai/docs/guides/gap-updates/) and [full notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.5.0.1.md).

### New experimental source libraries

- Chord v2 host, reload, jobs, links and remote routes, plus a distinct legacy protocol-4 runtime
- Native Chord hosting: plugin generations, HTTP and process workers
- Durable.Store with real-host examples 35–37, shared storage, followed views and registry composition
- Env.Protocol and Env: framed remote filesystem/process/watch adapters, SSH bootstrap and a separately packaged native daemon; Windows PTY remains `ENOTSUP`
- Preparation inventory: 28 libraries plus the CLI (29 packages); the five added libraries are unpublished

### Corrections and tooling

- Provider schemas, histories, headers, retries, output budgets, catalog/configuration and classifier routing
- Bounded Anthropic threads and UTF-16 prepared payloads, hooks and optional captures
- Durable compaction/transaction lookup, model request options, SQLite bulk execution/telemetry and delta tracking
- Mixed-line-ending edits, bounded reads/searches, Unicode/argument handling, persisted thinking, MCP/RPC shutdown/order and hosted settings
- Shell authentication, accurate update results, macOS modifier queries and Node overlay lifecycle
- Repository evaluations, package-only consumer checks and required regression/native validation workflows

### Release preparation corrections

- Classify the awaited primary plugin-loader failure before aggregating additional fault evidence, preserving initialization versus load diagnostics
- Patch strict tools in Responses/Codex initial and incremental developer `additional_tools` declarations, rejecting malformed or ambiguous targets
- Correct Unix hosting mutex cancellation and HTTP request-target routing, and use private Unix pipe paths for native process transports
- Derive the daemon handshake version from its build so a 1.5.0.1 artifact agrees with the SSH trusted-artifact version check
- Preserve file-URL query and hash components in the Node compatibility loader so reloads retain their fresh module identity; reproduced with real Node
- Repair CI prerequisites and evidence parsing: actual strict result statuses, resolved SDK executable, integrity-pinned historical archive and required `fd` setup. These controls do not establish hosted or upstream parity

Historical pins and capture evidence remain separate from current source mappings. Local Windows receipts do not establish all-platform or complete upstream parity. Publication waits for the [four release gates](https://pisharp.ai/docs/contribute/releasing/#four-required-validation-gates) at the intended source revision.

## [1.5.0] - 2026-11-05

PiSharp moves from Pi v1.2.0 to Pi v1.5.0, with the changes of Pi 1.3.0 and 1.4.0. pi-env, the rest of Chord and pi-evals follow in experimental 1.5.0.x patches. Durable sessions that 1.5.0 has opened can't go back to 1.2.0.x, so copy them first if you may need to. Update the CLI with `dotnet tool update -g PiSharp.Cli`. Full notes are in the [release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.5.0.md).

### Breaking changes

- Every PiSharp package depends on the other PiSharp packages at exactly its own version (`[1.5.0]`), so reference them all at the same version
- Tool arguments are validated as JSON Schema with pi-schema, not with TypeBox, and error texts follow pi-schema's
- `CompletionsSimpleOptions.TelemetryContext` is replaced by `Telemetry`, and `SessionSummaryRequest.MaximumOutputTokens` is `double?`
- The model catalog is pi-ai 1.5.0's. When no model is selected, `openai` and `github-copilot` default to `gpt-6.1-sol`
- Resuming a session restores its active tools, and code mode runs at most two workers per process (`PI_CODEMODE_MAX_WORKERS`)
- The durable packages follow `pi-durable` v1.5.0: getters became watches, agent events are gone and custom storages commit batches. SQLite stores move to schema version 2 and records to new versions, which 1.2.0.x can't continue

### New (experimental)

- `PiSharp.Telemetry` (`PISHARP_TELEMETRY001`), a port of pi-telemetry. It has no exporter, and nothing is recorded unless you pass a telemetry context
- The pi-schema port, `PiSharp.Contracts.Schema` (`PISHARP_SCHEMA001`)
- Durable: sleep and wake, group commit, read caches, `GenerationHooks.BeforeProviderRequest` and turn limits
- New APIs for client identity, streaming tool-call arguments, `pi.exec()` output limits, extension UI, code mode workers, `tool_search` and the TUI, each under its own experimental id

### New features

- GPT-6.1 Terra in the `openai`, `azure` and `openai-codex` catalogs
- `pisharp update` can run your own update command (`selfUpdateCommand`, `PI_SELF_UPDATE_COMMAND`); it checks NuGet, never pi.dev
- `sessionShareDestination` chooses where `/share` uploads, and `pisharp mcp login -l` and `logout -l` act on a project server
- Requests carry Pi 1.5.0's user agent and attribute nothing to Pi; MCP's `auto` client registration registers dynamically and never presents a Client ID Metadata Document
- TypeScript extensions still run against the Pi 1.2.0 packages, with `undici` overridden to 8.12.3

### Fixed

- A deferred MCP server reached through `tool_search` no longer hangs a prompt
- On Linux and macOS, a missing executable or working directory fails with `ENOENT` before launch, as on Windows
- A durable sleep that came due between a scheduler pass and its wake no longer sleeps forever
- Nested durable tool calls abandoned at a restart settle in call order
- A durable host observer's commit at a child task's end is stored before its parent's end, even on a busy thread pool
- An `ask_user_question` retired before its wait starts ends as retired instead of failing

## [1.2.0.2] - 2026-11-01

A C#-only release on the Pi v1.2.0 baseline. It releases PiSharp's media tools, service tiers and custom agents as experimental, and fixes bugs in `pisharp`. Update the CLI with `dotnet tool update -g PiSharp.Cli`. Full notes are in the [release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.2.0.2.md).

### New (experimental)

- Media: generate images, video, speech and music, transcribe audio, embed and rerank text, and ask decision models. The library is `PiSharp.AI.Media` (`PISHARP_MEDIA001`), and the tools are in `pisharp durable`. Paid results are saved, so a crash never pays twice
- Service tiers: `--tier`, `/tier` and the `serviceTier` setting choose `auto`, `normal`, `economy`, `priority`, `fast` or `ultrafast` capacity on OpenAI, OpenRouter, Anthropic, Codex and Google (`PISHARP_TIERS001`). Without a tier, nothing changes
- Custom agents: three new packages, `PiSharp.CustomAgents`, `PiSharp.CustomAgents.JavaScript` and `PiSharp.CustomAgents.Scripting`, for typed workflows, agent teams, subagents and dynamic tools on the durable harness (`PISHARP_AGENTS001`)
- `pisharp durable`: the `ask_user_question` tool, plan mode (`/plan`), `--help`, and `pisharp durable serve`, which runs this machine as a durable device on `127.0.0.1:4780`, with device commands listed by `pisharp durable help`
- The tool now needs the ASP.NET Core runtime, which the .NET SDK includes
- Everything here may change in any release

### Fixed

- `write` is no longer capped at 64 KiB
- Codemode scripts no longer see model `headers`, which can hold API keys
- Closing an MCP server no longer fails another server, a prompt or an `/mcp` action at the same moment, and a session that ends while a stdio server starts exits cleanly
- The first key after `/new` reaches the editor, and print and RPC mode no longer hang at exit when an extension command starts a run
- `pisharp session --help`, `pisharp durable --help` and `--service-tier` in the help

## [1.2.0.1] - 2026-10-16

A C#-only release on the Pi v1.2.0 baseline that adds the experimental durable port. Update the CLI with `dotnet tool update -g PiSharp.Cli`. Full notes are in the [release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.2.0.1.md).

### New (experimental)

- `pisharp durable [--continue]`: an agent whose sessions survive a crash. Every step is saved to SQLite before it is shown, and reopening a session finishes the interrupted turn. Listed in `pisharp --help` as experimental; its sessions are separate from ordinary ones
- `PiSharp.Durable`, `PiSharp.Durable.Tools` and `PiSharp.Durable.Sqlite` on NuGet, a port of Pi's `pi-durable` v1.2.0. Their API is marked `[Experimental("PISHARP_DURABLE001")]`; suppress that diagnostic to use them
- If a durable session's lock is lost (removed, or taken over by another process), the session fails at once and saves nothing more
- The command, the session format and the library API may change in any release

## [1.2.0] - 2026-10-15

PiSharp moves to Pi v1.2.0. Update the CLI with `dotnet tool update -g PiSharp.Cli`. The full notes are in the [release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.2.0.md).

### Breaking changes

- `before_agent_start` also fires for runs started by `pi.sendMessage(..., { triggerTurn: true })`; check the event's `messageType` (`user` or `custom`).
- While a run starts the session is busy: `pi.sendUserMessage()` needs `deliverAs`, and the native `sendUserMessage` without `DeliverAs` fails instead of steering.
- `models.json`, keybindings and themes are validated with Pi's published schemas; themes are validated strictly at startup too.
- `pisharp mcp list --json` reports `resources: true` instead of resource counts.
- A codemode `// @options:` line must be the first line of the script.
- `claude-sonnet-4-5` has a 200,000-token context window in the model catalog.

### New features

- Per-key OpenRouter model lists, with the key's context size, max output and prices
- `$schema` in `settings.json`, `models.json`, `keybindings.json` and themes
- Tools declare which tools they reach; MCP waits only for the servers a call may reach, and namespaces have aliases
- Codemode declarations show input constraints, and codemode rows show elapsed and total time
- `PI_CLIPBOARD_OSC52=1` sends copies to remote clipboards
- TypeScript extensions run against the Pi 1.2.0 packages

### Fixed

- Mistral long streams, Anthropic strict-tool rejections, OpenRouter router-model prices and "The upstream provider returned an error" retries
- Print mode warns and exits 1 on a truncated response, and no longer exits 1 when a command's triggered run is still starting at exit
- Symlinked `AGENTS.md` in worktrees, `~` in resource patterns, and `auth.json` locks on Windows
- MCP structured content, resources at connect, calls after an OAuth refresh and the `iss` check
- Terminal wheel scrolling over overlays, colour replies, Herdr images and shifted-symbol bindings
- `tool_search` ranks words with a dotted capital I (`İstanbul`) as Pi does

## [1.1.0.7] - 2026-10-14

A C#-only patch on the Pi v1.1.0 baseline. Update the CLI with `dotnet tool update -g PiSharp.Cli`. Full notes are in the [release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.1.0.7.md).

### Fixed

- On Apple Silicon Macs, a command given standard input (such as `bash -s`) no longer hangs waiting for input that never ends
- Two PiSharp processes can no longer both take the lock on `settings.json` or `trust.json` and overwrite each other's changes; the lock also works in folders whose path is longer than 260 characters on Windows

## [1.1.0.6] - 2026-10-11

A C#-only patch on the Pi v1.1.0 baseline. Update the CLI with `dotnet tool update -g PiSharp.Cli`. Full notes are in the [release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.1.0.6.md).

### Fixed

- On macOS, a bash command that timed out or was aborted while it had background children no longer fails the whole run with "RPC host cleanup failed"
- A prompt sent while the previous turn is still settling waits for it and then runs, as in Pi, instead of failing with "Session is settling"
- A codemode script could finish and never return its result (about one run in ten on Linux); it now always settles
- An MCP server still connecting when a session ends is no longer reported as a failed connection

### Performance

- Streaming long responses from OpenAI-compatible and Mistral providers uses roughly 40% less peak memory in offline benchmarks, and sessions and the interactive view do less repeated work per update

### Other

- PiSharp is now tested on macOS arm64 (Apple Silicon) in CI, alongside Linux and Windows

## [1.1.0.5] - 2026-10-10

A C#-only patch on the Pi v1.1.0 baseline. 1.1.0.4 was tagged but never published; its changes ship here. Update the CLI with `dotnet tool update -g PiSharp.Cli`. Full notes are in the [release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.1.0.5.md).

### Fixed

- The interactive startup header says PiSharp and shows PiSharp's own version (for example `v1.1.0.5`) instead of Pi's package version, and the onboarding line names PiSharp
- A session whose messages hold a lone UTF-16 surrogate (from a prompt or a tool result) no longer fails its run when auto-compaction checks the context size; the estimate counts it as Pi's `text.length` does
- Extensions receive `agent_settled` before RPC and JSON clients do, as in Pi, so a client that exits on `agent_settled` no longer races the extensions' handlers
- Settings reloads read `settings.json` under its lock, so a reload during a settings write no longer sees an empty file and falls back to default settings

## [1.1.0.3] - 2026-10-10

A C#-only patch on the Pi v1.1.0 baseline that fixes the `/clear` crash ([#5](https://github.com/jkelly/PiSharp/issues/5)) and closes most of the differences 1.1.0.2 left open. Update the CLI with `dotnet tool update -g PiSharp.Cli`. Full notes are in the [release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.1.0.3.md).

### Behaviour changes

- Auto-compaction is on by default in every mode, read from the `compaction` settings, as in Pi.
- OpenAI Codex tries a WebSocket first and compresses server-sent-event requests with zstd, as in Pi. Set `transport` to `sse` to stay on server-sent events.
- Print mode recovers from "prompt is too long" by compacting, and exits 0.
- RPC and print setters (`set_auto_compaction`, `set_auto_retry`, `set_steering_mode`, `set_follow_up_mode`) save to the global `settings.json`, as Pi does.
- `AZURE_OPENAI_RESOURCE_NAME` is taken as Pi takes it: any name, read as part of a URL.

### Fixed

- `/clear` and `/new` no longer crash on models whose thinking can't be turned off; thinking levels are clamped everywhere Pi clamps them ([#5](https://github.com/jkelly/PiSharp/issues/5))
- Failed RPC commands answer with the real error message instead of "RPC command failed."
- The interactive footer's cost, tokens and context usage update after every turn
- `agent_settled` arrives before the command's response, and an extension's `triggerTurn` starts its turn at once
- Shell start failures report Node's spawn errors; `bash` stays registered without a shell, as in Pi
- Compaction, fork and tree-navigation failures carry Pi's texts

### New features

- Built-in extensions (`mcp`, `llama.cpp`, `codemode`, `tool-search`) can be turned on and off: `builtin:` entries in `extensions`, `pisharp config`, `-e builtin:<name>`
- The llama.cpp provider, `/llama` and Pi's [llama.cpp guide](https://pisharp.ai/docs/guides/llama-cpp/)
- Native C# extensions can register classifier and image providers
- `ctx.ui.setTheme` switches the theme
- Packages loaded with `-e` add their skills, prompt templates and themes
- The OpenAI Codex WebSocket transport and zstd request compression
- Pasting from the X11 clipboard without `xclip` or `xsel`
- Lone surrogates, fractional token counts and 1,000 levels of JSON nesting are carried as Pi carries them, and the Pi command line's size bounds are JavaScript's string limit

### Known differences

- See [Parity](https://pisharp.ai/parity/) for what still differs from Pi.

## [1.1.0.2] - 2026-10-09

A C#-only patch on the Pi v1.1.0 baseline that ports the rest of Pi v1.1.0. Update the CLI with `dotnet tool update -g PiSharp.Cli`. Full notes are in the [release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.1.0.2.md).

### Breaking changes

- `pisharp` takes Pi's command line. Plain `pisharp` opens the terminal UI; `-p` and `--mode json|rpc` replace `session prompt --output`. The `session …` commands still work.
- PiSharp uses Pi's folders: settings, resources and sessions in `~/.pi/agent` and `.pi`, behind project trust.
- On the Pi command line, tools follow the `pi` tool policy: any path, any command. `--tool-policy explicit` restores exact grants.
- `!command` values in `auth.json`, `models.json` and `settings.json` run, as in Pi.
- Discovered TypeScript extensions run in your Node, with your permissions, as in Pi.

### New features

- Every chat API and provider in Pi 1.1.0, including Bedrock, Codex, Copilot and Cloudflare, with `/login` for each
- `--model` patterns, `--models`, `--list-models`, `models.json` and the startup catalog refresh
- Classifier and image models
- Codemode, on the Jint engine
- `read` returns images
- Interactive mode on Windows, Linux and macOS, with every slash command, themes and settings
- Sessions in Pi's folder and format, byte for byte, with `--continue`, `--resume`, `--fork`, `/name`, `/session`, HTML export and `/share`
- Every JSON and RPC event, SIGTERM and SIGHUP
- MCP: project `.pi/mcp.json`, resource tools, `/mcp`, and `tool_search` as in Pi
- Pi's TypeScript extensions and packages, on Pi's own npm packages, and `pisharp install|remove|update|list|config`
- Native C# extensions loaded from Pi's extension folders

### Fixed

- Long sessions: no more failures past 256 or 1,024 messages
- Prompts sent right after `agent_settled` are accepted
- Gemini tool calls without a name are recorded, as in Pi
- `/bug` opens a PiSharp GitHub issue; the update check uses NuGet

### New dependencies

- SkiaSharp (images), Jint and Acornima (codemode). Pi's npm packages are installed on first extension use.

### Known differences

- See [Parity](https://pisharp.ai/parity/) for the remaining differences from Pi.

## [1.1.0.1] - 2026-10-08

A C#-only patch on the Pi v1.1.0 baseline. Update the CLI with `dotnet tool update -g PiSharp.Cli`. Full notes are in the [release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.1.0.1.md).

### Fixed

- MCP tools can be called. 1.1.0 declared them but refused every call. Servers you configure in `mcp.json` are trusted, as in Pi.

### New features

- `tool_search`, ported from Pi: BM25 search that loads deferred tools for the next call
- MCP servers with `deferred` exposure now connect, reachable through `tool_search`

## [1.1.0] - 2026-10-08

PiSharp moves to Pi v1.1.0, covering Pi v0.99.2 through v1.1.0. Update the CLI with `dotnet tool update -g PiSharp.Cli`. The full notes, including migration steps, are in the [release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.1.0.md).

### Breaking changes

- The Azure provider id is now `azure` (was `azure-openai-responses`). Update `auth.json`, `models.json` and `settings.json`. The `AZURE_OPENAI_*` variables are unchanged.
- MCP tool and namespace names use `_` instead of `-`.
- Unknown tool names in `--tools` and `defaultTools` are ignored, as in Pi.

### New features

- Anthropic `/login` and `/logout`, and workload identity federation
- Azure Foundry Chat Completions under the `azure` provider
- MCP: `pisharp mcp add|remove|list|login|logout`, background connection, the `mcp_servers` prompt section, OAuth hardening, `--no-mcp`
- `--tools` patterns and `+name`/`-name` modifiers
- RPC `bash` and interactive `!`/`!!` commands
- Claude Opus 5, Opus/Sonnet/Haiku 5.5 and Fable 5.1 work, with Pi's per-turn thinking effort
- Model catalog from Pi 1.1.0, including Claude Haiku 5.5, with prompt-length pricing tiers
- OpenAI grammar tools replayed with their `ctc_` ids

### Not yet included

- Codemode, `tool_search`, classifiers and image generation, planned as 1.1.0.x releases

## [0.99.1] - 2026-10-08

The first public PiSharp release, matching Pi v0.99.1. Install the CLI with `dotnet tool install -g PiSharp.Cli`.

### New features

- Native .NET 10 coding agent: interactive terminal UI, print/JSON, RPC and SDK modes
- Streaming providers: Anthropic Messages, OpenAI Completions and Responses, Azure Responses, Google Generative AI, Mistral Conversations
- Native C# extension SDK, with samples ported from Pi
- Optional Node bridge for Pi's TypeScript extensions

### Packages

- Core, frontend and extension libraries published to NuGet. See the [SDK](https://pisharp.ai/sdk/).
