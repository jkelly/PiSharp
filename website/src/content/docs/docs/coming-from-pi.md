---
title: Coming from Pi
description: What carries over from Pi, and what's different.
---

PiSharp follows Pi v1.1.0 closely. Run `pisharp` where you would run `pi`. It reads the same files, takes the same flags and writes the same sessions.

:::tip
These docs describe PiSharp's current baseline, Pi v1.1.0. Features Pi added later aren't covered until the next sync. Check [Parity](/parity/) for details.
:::

## What carries over

| From Pi | In PiSharp |
| --- | --- |
| Command line | The same flags and modes: `-p`, `--mode json\|rpc`, `--model`, `--continue`, `--resume`, `--tools` and the rest. See [Command line](/docs/reference/cli/). |
| `~/.pi/agent` | Shared. `settings.json`, `auth.json`, `models.json`, `mcp.json`, `mcp-auth.json`, `keybindings.json` and `trust.json` are read and written in place. |
| Project `.pi` folder | Read after you trust the project, as in Pi. |
| `AGENTS.md`, `CLAUDE.md`, `SYSTEM.md`, `APPEND_SYSTEM.md` | Loaded from the same places. |
| Skills, prompt templates, themes | Discovered in the same folders. |
| Sessions | Stored in `~/.pi/agent/sessions`, in the same JSONL v3 format. Pi and PiSharp resume each other's sessions. |
| Sign-ins | `/login` writes `auth.json`. Signing in with one signs in the other. |
| MCP servers | The same `mcp.json` and `.pi/mcp.json`, OAuth sign-ins, exposure modes, `tool_search` and codemode. |
| TypeScript extensions and packages | Run through the [Node bridge](/docs/extensions/node-bridge/), with Pi's own npm packages. `pisharp install` installs Pi packages. |
| RPC clients | The same protocol over stdin and stdout. |

See [Files and settings](/docs/reference/configuration/) for the full list of paths.

## Older session files

PiSharp opens Pi's current (v3) session files directly. For v1 and v2 files, or to work on a copy:

- `pisharp session migrate --source <file> --destination <file>` converts an older session file to v3. The original is not changed.
- `pisharp session copy --source <file> --destination <file> --format current-jsonl` writes a Pi-compatible copy.

## What's different

- **It's a .NET tool.** Install and update it with `dotnet tool install|update -g PiSharp.Cli`. `pisharp update` updates packages and model catalogs, but not PiSharp itself.
- **Node is optional.** The agent runs on .NET alone. Node is needed only to run TypeScript extensions.
- **C# extensions.** Besides Pi's TypeScript extensions, PiSharp loads native C# extensions from the same extension folders. See [Build an extension](/docs/extensions/build-an-extension/).
- **Tool policy.** A PiSharp option, `--tool-policy explicit`, limits the built-in tools to paths and commands you grant. The default, `pi`, behaves as Pi does. See [Tool policy](/docs/reference/configuration/#tool-policy).
- **No telemetry.** PiSharp doesn't report installs to Pi's servers. `PI_TELEMETRY` is ignored.
- **It names itself.** Requests carry a PiSharp user agent, not Pi's.
- **Versions match Pi's.** See [How versions work](/docs/versioning/).

The [Parity](/parity/) page lists the smaller differences that remain.
