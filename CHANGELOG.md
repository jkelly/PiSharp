# Changelog

## [1.1.0.4] - 2026-10-10

A C#-only patch on the Pi v1.1.0 baseline. Update the CLI with `dotnet tool update -g PiSharp.Cli`. Full notes are in the [release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.1.0.4.md).

### Fixed

- The interactive startup header says PiSharp and shows PiSharp's own version (for example `v1.1.0.4`) instead of Pi's package version, and the onboarding line names PiSharp

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
