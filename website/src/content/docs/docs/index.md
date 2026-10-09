---
title: Overview
description: What PiSharp is, what it targets, and where to start.
---

PiSharp is an independent native .NET implementation of the [Pi coding agent](https://pi.dev). It targets .NET 10. The agent itself runs without Node; Node is only needed for TypeScript extensions.

PiSharp's version is the Pi version it matches. The current baseline is **Pi v1.1.0**, pinned to commit `abe508e`. See [How versions work](/docs/versioning/).

## What's included

- **Pi's command line:** run `pisharp` as you would run `pi`. The same flags, print and JSON modes, RPC, and the `install`, `update`, `config`, `auth` and `mcp` commands.
- **Pi's files:** settings, credentials, MCP servers, `AGENTS.md`, skills, prompt templates, themes and sessions, all in `~/.pi/agent` and `.pi`, shared with Pi.
- **Providers:** every chat API Pi has, every provider in Pi's catalogs, and `/login` for each of them. Classifier and image models too.
- **Tools:** `read` (with images), `write`, `edit`, `bash`, `grep`, `find`, `ls`, `codemode` and `tool_search`.
- **Interactive mode:** Pi's terminal UI, slash commands and themes, on Windows, Linux and macOS.
- **MCP:** stdio and HTTP servers, OAuth, deferred and codemode exposure.
- **Extensions:** Pi's TypeScript extensions and packages through the Node bridge, and native C# extensions.

Every layer is a separate library. See the [SDK](/sdk/) for the full list.

## Where to go next

- [Quickstart](/docs/quickstart/): install PiSharp with `dotnet tool install -g PiSharp.Cli` and run it.
- [Coming from Pi](/docs/coming-from-pi/): what carries over and what's different.
- [Command line](/docs/reference/cli/) and [Files and settings](/docs/reference/configuration/).
- [Parity](/parity/): how close PiSharp is to Pi today, and the differences that remain.

:::note
PiSharp is an independent port, not an official Pi release. Pi's own [documentation](https://github.com/earendil-works/pi/tree/v1.1.0/packages/coding-agent/docs) describes the behaviour PiSharp follows. Where PiSharp differs, these pages and the [Parity](/parity/) page say so.
:::
