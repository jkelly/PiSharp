---
title: Files and settings
description: Where PiSharp finds its configuration, resources and sessions, and how project trust and the tool policy work.
---

PiSharp reads the same files as Pi v1.1.0, in the same places. If you already use Pi, PiSharp picks up your setup. Pi's [Configuration](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/configuration.md) and [Settings](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/settings.md) pages apply.

## The agent folder

The agent folder is `~/.pi/agent`. Set `PI_CODING_AGENT_DIR` to move it.

| Path | What it holds |
| --- | --- |
| `settings.json` | Your settings, resource paths and packages. |
| `auth.json` | API keys and OAuth sign-ins from `/login`. |
| `models.json` | Custom providers, models and overrides. |
| `mcp.json`, `mcp-auth.json` | MCP servers and their OAuth sign-ins. |
| `keybindings.json` | Key bindings. |
| `trust.json` | Saved project trust decisions. |
| `AGENTS.md` or `CLAUDE.md` | Instructions for every folder. |
| `SYSTEM.md`, `APPEND_SYSTEM.md` | Replace, or add to, the system prompt. |
| `extensions/`, `skills/`, `prompts/`, `themes/` | Your extensions, skills, prompt templates and themes. |
| `sessions/` | Session files, one folder per working folder. |
| `bin/` | `rg` and `fd`, if PiSharp downloaded them. |

Pi and PiSharp share these files. A sign-in with one works in the other.

## The project `.pi` folder

| Path | What it holds |
| --- | --- |
| `.pi/settings.json` | Project settings. They override your settings; nested objects are merged. |
| `.pi/mcp.json` | Project MCP servers. |
| `.pi/SYSTEM.md`, `.pi/APPEND_SYSTEM.md` | Project system prompt. These win over the agent folder's files. |
| `.pi/extensions/`, `.pi/skills/`, `.pi/prompts/`, `.pi/themes/` | Project resources. |

PiSharp also finds skills in `.agents/skills` in the working folder and its parents, and in `~/.agents/skills`, as Pi does.

Everything in `.pi` loads only when you trust the project.

## Context files

PiSharp loads `AGENTS.md` (or `AGENTS.override.md`, `CLAUDE.md`) from the agent folder, then from the root down to the working folder. Context files don't need project trust. Turn them off with `--no-context-files`.

## Sessions

Sessions are saved in `~/.pi/agent/sessions/<folder>/`, one JSONL file per session, in Pi's format. `--session-dir`, then `PI_CODING_AGENT_SESSION_DIR`, then the `sessionDir` setting move them. `--no-session` keeps a session in memory only.

Pi and PiSharp list and resume each other's sessions.

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

`pisharp`, `-p` and `--mode json|rpc` use `pi` by default. The older `pisharp session …` commands use `explicit`. Change it with `--tool-policy pi|explicit` or the `toolPolicy` setting. Untrusted projects keep the `pi` policy, as Pi does. In both policies, PiSharp's own session files, approvals and extension manifests stay protected.

## Built-in extensions

Pi's four built-in extensions are resources like any other, named `builtin:mcp`, `builtin:llama.cpp`, `builtin:codemode` and `builtin:tool-search`. They load by default.

- `"extensions": ["-builtin:mcp"]` in `settings.json` turns one off. In project settings, `+builtin:<name>` or `-builtin:<name>` overrides your setting.
- `pisharp config` lists them under **Built-in**, where you can switch them.
- `--no-extensions` turns them off for one run, and `-e builtin:<name>` loads one explicitly. `--no-mcp` leaves out `mcp`.
- An extension that registers its own `codemode` or `tool_search` tool, or a `/mcp` command, replaces the built-in, which is left out with a warning.

A built-in that is off registers nothing: no `codemode` or `tool_search` tool, no MCP servers or `/mcp`, no llama.cpp provider or `/llama`. `/reload` applies changes.

## Compaction

Automatic compaction is on by default in every mode, as in Pi. The `compaction` settings control it: `enabled`, `reserveTokens` (default 16384), `keepRecentTokens` (default 20000) and `modelOverrides` for single models. `/settings` and the RPC `set_auto_compaction` command change `compaction.enabled` and save it to your `settings.json`, as Pi does.

## Shell, ripgrep and fd

`bash` finds its shell as Pi does: Git Bash (or `bash.exe` on your `PATH`) on Windows, and `/bin/bash`, then `bash`, then `sh` on Linux and macOS. Set `shellPath` to choose another. `shellCommandPrefix` runs before every command, for the model's `bash` and for your `!` commands.

`grep` and `find` use `rg` and `fd` from `~/.pi/agent/bin` or your `PATH`. If one is missing, PiSharp downloads it from its GitHub releases the first time it's needed, as Pi does. `PI_OFFLINE=1` stops the download.

## Config values that run commands

A value in `auth.json`, `models.json` or `settings.json` that starts with `!` runs as a shell command, and its output is used. This matches Pi. It includes stored API keys and AWS `credential_process`.
