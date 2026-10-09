# Changelog

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
