---
title: Command line
description: pisharp takes Pi's command line, plus a few PiSharp options.
---

`pisharp` takes the same command line as Pi v1.1.0. Run `pisharp --help` for the exact list in your build. Pi's [Command Line](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/cli.md) reference applies; this page covers the common cases and what PiSharp adds.

```sh
pisharp [options] [--] [@files...] [messages...]
pisharp install <source> [-l]
pisharp remove <source> [-l]
pisharp update [source|self|pi] [--extensions|--models|--all]
pisharp list
pisharp config [-l]
pisharp auth <check|print-api-key|print-bearer-token>
pisharp mcp <add|remove|list|login|logout>
```

## Modes

| Command | What it does |
| --- | --- |
| `pisharp` | Opens the terminal UI. |
| `pisharp "message"` | Opens the terminal UI with a first prompt. |
| `pisharp -p "message"` | Runs the prompt, prints the final answer and exits. |
| `pisharp --mode json "message"` | Runs the prompt and writes Pi's JSONL events to stdout. |
| `pisharp --mode rpc` | Speaks Pi's RPC protocol over stdin and stdout. |
| `pisharp --export <file> [output]` | Writes a session file as HTML and exits. |

When stdin or stdout is redirected, PiSharp uses print mode, as Pi does. `@path` arguments add a text file or image to the first prompt. Piped stdin is added in front of the first prompt.

## Common options

| Option | Meaning |
| --- | --- |
| `--model <pattern>` | Model by id, `provider/id` or fuzzy match, with an optional `:thinking` suffix. |
| `--models <patterns>` | Models for `Ctrl+P` cycling. Globs and fuzzy matches work. |
| `--thinking <level>` | `off`, `minimal`, `low`, `medium`, `high`, `xhigh` or `max`. |
| `--list-models [search]` | Lists the models you can use, then exits. |
| `-c`, `--continue` | Continues the latest session for this folder. |
| `-r`, `--resume` | Opens the session picker. |
| `--session <path\|id>` | Opens a session file or id. |
| `--fork <path\|id>` | Forks a session into a new one. |
| `--no-session` | Keeps the session in memory only. |
| `-t`, `--tools <list>` | Tool allowlist. `+name` and `-name` change the defaults instead. |
| `-xt`, `--exclude-tools <list>` | Tools to turn off, MCP tools included. |
| `-e`, `--extension <path>` | Loads an extension or package for this run. Repeatable. |
| `-ne`, `--no-extensions` | Turns off discovered extensions. `-e` still works. |
| `--no-mcp` | No MCP servers and no MCP tools. |
| `-a`, `--approve` / `-na`, `--no-approve` | Trusts, or ignores, project files for this run. |
| `--offline` | No startup network use. Same as `PI_OFFLINE=1`. |
| `-v`, `--version` | Prints the PiSharp version. |

Skills, prompt templates, themes and context files have their own `--skill`, `--prompt-template`, `--theme`, `--use-theme`, `--no-skills`, `--no-prompt-templates`, `--no-themes` and `--no-context-files` options, as in Pi.

## PiSharp additions

- `--tool-policy pi|explicit` sets how far the built-in tools reach. See [Tool policy](/docs/reference/configuration/#tool-policy).
- `pisharp session <verb>` keeps PiSharp's earlier explicit-path commands (`create`, `prompt`, `resume`, `rpc`, `terminal`, `copy`, `migrate` and more). They use the `explicit` tool policy and their `--allow-*` grants. New scripts should use the Pi options above.

## Packages

`install`, `remove` (or `uninstall`), `list` and `config` work as in Pi. See [Packages](/docs/extensions/packages/).

`pisharp update --extensions` updates installed packages and `pisharp update --models` refreshes the model catalogs. `pisharp update` on its own would update Pi itself. PiSharp can't update itself that way and says so. Update the tool with:

```sh
dotnet tool update -g PiSharp.Cli
```

## Credentials and MCP

`pisharp auth check`, `print-api-key` and `print-bearer-token` resolve credentials as Pi does. `pisharp mcp add|remove|list|login|logout` change the same `mcp.json` and `mcp-auth.json` that Pi uses. See [Providers](/docs/guides/providers/) and [MCP](/docs/guides/mcp/).

## Environment variables

PiSharp reads Pi's variables: provider keys such as `ANTHROPIC_API_KEY` and `OPENAI_API_KEY`, `PI_CODING_AGENT_DIR`, `PI_CODING_AGENT_SESSION_DIR`, `PI_OFFLINE` and the rest. `pisharp --help` lists them. `PI_TELEMETRY` is accepted and ignored: PiSharp sends no install telemetry.
