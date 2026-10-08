---
title: Coming from Pi
description: What carries over from Pi, what's imported, and what's different.
---

PiSharp follows Pi's design closely, so much of what you know carries over. This page covers what's shared, what's imported, what's different, and what isn't there yet.

:::tip
These docs describe PiSharp's current baseline, Pi v1.1.0. Features Pi added later aren't covered until the next sync. Check [Parity](/parity/) for details.
:::

## What carries over

| From Pi | In PiSharp | Notes |
| --- | --- | --- |
| Session files | Read and written | The same JSONL v3 format. Older versions can be migrated with `session migrate`. |
| `auth.json`, `mcp.json`, `mcp-auth.json`, `keybindings.json` | Shared | Read from Pi's agent folder. See below. |
| Skills, prompt templates | Loaded by path | Pass each file with `--skill` or `--prompt-template`. Folders aren't discovered yet. |
| RPC clients | Compatible protocol | The same JSON over stdin/stdout. |
| `AGENTS.md`, `SYSTEM.md`, themes | Not yet | PiSharp doesn't read these files yet. |
| TypeScript extensions | Not yet | The Node bridge runs only a pinned set of Pi's examples. See [Node bridge](/docs/extensions/node-bridge/). |

## Where PiSharp keeps its files

PiSharp shares Pi's agent folder, `~/.pi/agent` (or `PI_CODING_AGENT_DIR`), for credentials, MCP servers and keybindings:

- `auth.json`: `/login` and `/logout` write to the same file Pi uses, so signing in with one signs in the other.
- `mcp.json` and `mcp-auth.json`: `pisharp mcp add`, `remove`, `login` and `logout` change the same files Pi reads.
- `keybindings.json`: read only.

Settings come only from files you pass with `--user-settings` and `--project-settings`. PiSharp doesn't read Pi's `settings.json` on its own.

Session files are the ones you name with `--session`. PiSharp has no session folder of its own.

## Importing Pi sessions

PiSharp opens Pi's current (v3) session files directly, and appends to the file it opens. To keep a Pi session untouched, work on a copy:

- `pisharp session copy --source <file> --destination <file> --format current-jsonl` writes a Pi-compatible copy. `--format native-exact` keeps everything PiSharp records.
- `pisharp session copy-inspect --source <file>` reports what a copy would keep or leave out, without writing anything.
- `pisharp session migrate --source <file> --destination <file>` converts an older (v1 or v2) session file to v3. The original is not changed.

## What's different

- **Extensions are C#.** Native extensions are .NET assemblies that implement `IPiSharpExtension`. See [Build an extension](/docs/extensions/build-an-extension/).
- **No Node required.** Node is only needed for the optional bridge, which is still in development.
- **Versions match Pi's.** See [How versions work](/docs/versioning/).
