---
title: Files and locations
description: Where PiSharp finds its configuration, resources and sessions, and how project trust and the tool policy work.
---

PiSharp reads the same files as Pi v1.2.0, in the same places. If you already use Pi, PiSharp picks up your setup, and a sign-in with one works in the other. Pi's [Configuration](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/docs/configuration.md) page applies. This page lists the files and folders; [Settings](/docs/reference/settings/) lists the keys of `settings.json`.

## The agent folder

The agent folder is `~/.pi/agent`. Set `PI_CODING_AGENT_DIR` to move it. Every path below is relative to it.

| Path | What it holds |
| --- | --- |
| `settings.json` | Your settings, resource paths and packages. See [Settings](/docs/reference/settings/). |
| `auth.json` | API keys and OAuth sign-ins from `/login`. An older `oauth.json` is migrated into it. |
| `models.json` | Custom providers, models and overrides of built-in ones, in Pi's format. |
| `models-store.json` | Model catalogs saved by the startup refresh and `pisharp update --models`, for offline use. |
| `mcp.json`, `mcp-auth.json` | MCP servers and their OAuth sign-ins. See [MCP](/docs/guides/mcp/). |
| `mcp.log` | Log messages from MCP servers. Moves to `mcp.log.1` past 5 MB. |
| `keybindings.json` | Key bindings. See [Interactive mode](/docs/guides/interactive/#key-bindings). |
| `trust.json` | Saved project trust decisions. |
| `AGENTS.md` or `CLAUDE.md` | Instructions for every folder. |
| `SYSTEM.md`, `APPEND_SYSTEM.md` | Replace, or add to, the system prompt. |
| `extensions/`, `skills/`, `prompts/`, `themes/` | Your extensions, skills, prompt templates and themes. |
| `npm/`, `git/` | Packages installed with `pisharp install`. |
| `sessions/` | Session files, one folder per working folder. See [Sessions](/docs/guides/sessions/). |
| `bin/` | `rg` and `fd`, if PiSharp downloaded them. Also first on `PATH` for `bash`. |
| `crashes.json` | The last crash of interactive mode, so the next start can point you to `/bug`. |
| `pi-debug.log` | Written by the hidden `/debug` command. |
| `pisharp/pi-runtime/<version>/` | Pi's own npm packages, installed on first use of a TypeScript extension. `PISHARP_PI_RUNTIME_DIR` moves it. See [Node bridge](/docs/extensions/node-bridge/). |
| `experimental/durable-sessions/` | Sessions of `pisharp durable`. See [Pi Durable](/docs/guides/durable/). |

## The project `.pi` folder

| Path | What it holds |
| --- | --- |
| `.pi/settings.json` | Project settings, merged over yours. See [Files and scope](/docs/reference/settings/#files-and-scope). |
| `.pi/mcp.json` | Project MCP servers. |
| `.pi/SYSTEM.md`, `.pi/APPEND_SYSTEM.md` | Project system prompt. These win over the agent folder's files. |
| `.pi/extensions/`, `.pi/skills/`, `.pi/prompts/`, `.pi/themes/` | Project resources. |
| `.pi/npm/`, `.pi/git/` | Packages installed with `pisharp install -l`. |
| `.pi/media/` | Default output folder of the media tools in `pisharp durable` (experimental, unreleased; see [Media](/docs/guides/media/)). |

Everything in `.pi` loads only when you trust the project.

PiSharp also finds skills in `.agents/skills` in the working folder and its parents, and in `~/.agents/skills`, as Pi does.

## Context files

PiSharp loads one context file from the agent folder, then one from each folder from the root down to the working folder. In each folder it takes the first of `AGENTS.override.md`, `AGENTS.md`, `AGENTS.MD`, `CLAUDE.md` and `CLAUDE.MD` that exists. Context files don't need project trust. Turn them off with `--no-context-files`.

## Precedence

When the same thing is set in several places, the first one wins:

| What | Order |
| --- | --- |
| Settings | Command-line option, then project `settings.json` (if trusted), then your `settings.json`, then the default. |
| System prompt | `--system-prompt`, then `.pi/SYSTEM.md` (if trusted), then `<agentDir>/SYSTEM.md`, then Pi's prompt. `APPEND_SYSTEM.md` and `--append-system-prompt` follow the same order. |
| Session folder | `--session-dir`, then `PI_CODING_AGENT_SESSION_DIR`, then `sessionDir`, then `<agentDir>/sessions`. |
| Credentials | `--api-key`, then `auth.json`, then an `apiKey` in `models.json`, then the provider's environment variables. See [Providers](/docs/guides/providers/). |
| Resources | `-e`, `--skill`, `--prompt-template` and `--theme` add to what is discovered. `--no-extensions`, `--no-skills`, `--no-prompt-templates` and `--no-themes` stop discovery. |

## Schemas

`settings.json`, `models.json`, `keybindings.json` and theme files accept a `$schema` key, so editors can check them against Pi's published schemas, for example `"$schema": "https://pi.dev/schemas/models.schema.json"`. PiSharp ships the same schemas and checks `models.json`, key bindings and themes against them. A theme may only use the documented fields; put your own colors under `vars`.

## Sessions

Sessions are saved in `~/.pi/agent/sessions/<folder>/`, one JSONL file per session, in Pi's format. Pi and PiSharp list and resume each other's sessions. `--no-session` keeps a session in memory only. See [Sessions](/docs/guides/sessions/).

## Project trust

Project trust works as in Pi. When a folder has project resources (anything in `.pi`, or `.agents/skills`), PiSharp asks once whether to trust it:

- **Trust** loads the project's settings, MCP servers, extensions, skills, prompts, themes and system prompt.
- **Do not trust** skips them. Context files still load.

PiSharp saves the answer in `~/.pi/agent/trust.json`. Use `/trust` to change it later, or `-a` and `-na` for one run. Print, JSON and RPC modes can't ask. They follow the `defaultProjectTrust` setting (`always`, `never` or `ask`), and `ask` counts as no.

Trust is not a sandbox. It decides what loads. It doesn't limit what the tools can do.

## Tool policy

The built-in tools follow a tool policy. This is a PiSharp setting; Pi has no such switch.

| Policy | Behaviour |
| --- | --- |
| `pi` | As in Pi. File tools take any path. `bash` runs any command in your shell, with your environment. |
| `explicit` | Tools reach only the paths and commands you grant with `--allow-*` options. |

`pisharp`, `-p` and `--mode json|rpc` use `pi` by default. The `pisharp session …` commands use `explicit`. Change it with `--tool-policy pi|explicit` or the `toolPolicy` setting. With `explicit`, plain `pisharp` has no `--allow-*` options, so file tools refuse every path and `bash`, `grep` and `find` are left out. Untrusted projects keep the `pi` policy, as Pi does. In both policies, PiSharp's own session files, approvals and extension manifests stay protected.

## Built-in extensions

Pi's four built-in extensions are resources like any other, named `builtin:mcp`, `builtin:llama.cpp`, `builtin:codemode` and `builtin:tool-search`. They load by default.

- `"extensions": ["-builtin:mcp"]` in `settings.json` turns one off. In project settings, `+builtin:<name>` or `-builtin:<name>` overrides your setting.
- `pisharp config` lists them under **Built-in**, where you can switch them.
- `--no-extensions` turns them off for one run, and `-e builtin:<name>` loads one explicitly. `--no-mcp` leaves out `mcp`.
- An extension that registers its own `codemode` or `tool_search` tool, or a `/mcp` command, replaces the built-in, which is left out with a warning.

A built-in that is off registers nothing: no `codemode` or `tool_search` tool, no MCP servers or `/mcp`, no llama.cpp provider or `/llama`. `/reload` applies changes.

## Shell, ripgrep and fd

`bash` finds its shell as Pi does: Git Bash (or `bash.exe` on your `PATH`) on Windows, and `/bin/bash`, then `bash`, then `sh` on Linux and macOS. Set `shellPath` to choose another. `shellCommandPrefix` runs before every command, for the model's `bash` and for your `!` commands.

`grep` and `find` use `rg` and `fd` from `~/.pi/agent/bin` or your `PATH`. If one is missing, PiSharp downloads it from its GitHub releases the first time it's needed, as Pi does. `PI_OFFLINE=1` stops the download. See [Built-in tools](/docs/reference/tools/).

## Config values that run commands

A value in `auth.json`, `models.json` or `settings.json` that starts with `!` runs as a shell command, and its output is used. This matches Pi. It includes stored API keys and AWS `credential_process`.
