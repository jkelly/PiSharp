---
title: Overview
description: What PiSharp is, what it targets, and where to start.
---

PiSharp is an independent native .NET implementation of the [Pi coding agent](https://pi.dev). It targets .NET 10. The agent itself runs without Node; Node is only needed for TypeScript extensions.

PiSharp's version is the Pi version it matches. The current baseline is **Pi v1.5.0**, pinned to commit `f9a2636`. See [How versions work](/docs/versioning/).

## What's included

- **Pi's command line:** run `pisharp` as you would run `pi`. The same flags, print and JSON modes, RPC, and the `install`, `update`, `config`, `auth` and `mcp` commands.
- **Pi's files:** settings, credentials, MCP servers, `AGENTS.md`, skills, prompt templates, themes and sessions, all in `~/.pi/agent` and `.pi`, shared with Pi.
- **Providers:** every chat API Pi has, every provider in Pi's catalogs, and `/login` for each of them. Local models through a [llama.cpp](/docs/guides/llama-cpp/) router. Classifier and image models too.
- **Tools:** `read` (with images), `write`, `edit`, `bash`, `grep`, `find`, `ls`, `codemode` and `tool_search`. See [Built-in tools](/docs/reference/tools/).
- **Interactive mode:** Pi's terminal UI, slash commands and themes, on Windows, Linux and macOS.
- **MCP:** stdio and HTTP servers, OAuth, deferred and codemode exposure.
- **Extensions:** Pi's TypeScript extensions and packages through the Node bridge, and native C# extensions.
- **Durable sessions (experimental):** `pisharp durable`, a port of Pi Durable that resumes a session after a crash.

Every layer is a separate library. See the [SDK](/sdk/) for the full list.

## New in 1.5.0

PiSharp 1.5.0 moves from Pi v1.2.0 to Pi v1.5.0, with Pi 1.3.0 and 1.4.0 in between. The [changelog](/changelog/) and the [release notes](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/1.5.0.md) describe the changes, the breaking ones first.

- [Durable sessions](/docs/guides/durable/#upgrading-to-150) follow `pi-durable` v1.5.0. Once 1.5.0 has opened a session, PiSharp 1.2.0.x can't continue it, and the [library API](/docs/embed/durable/#upgrading-from-120x) changed.
- [Telemetry](/docs/embed/telemetry/) (experimental): `PiSharp.Telemetry`, a new package that ports Pi's telemetry contract. Nothing is recorded unless a host asks, and nothing is sent anywhere.
- Tool arguments are validated as JSON Schema by a port of Pi's pi-schema.
- [`pisharp update`](/docs/reference/cli/) can run your own update command, and `/share` can upload to Radius ([`sessionShareDestination`](/docs/reference/settings/)).

The custom agents, media tools, service tiers and the durable device server, new in 1.2.0.2, are still PiSharp's own and experimental.

## Where to go next

- [Quickstart](/docs/quickstart/): install PiSharp with `dotnet tool install -g PiSharp.Cli` and run it.
- [Coming from Pi](/docs/coming-from-pi/): what carries over and what's different.
- [Command line](/docs/reference/cli/), [Settings](/docs/reference/settings/) and [Files and locations](/docs/reference/configuration/).
- [Use the SDK](/docs/embed/sdk/): embed the agent, or just one layer, in your own .NET app.
- [Extend PiSharp](/docs/extensions/build-an-extension/): write a C# extension, or load Pi's TypeScript ones.
- [Contribute](/docs/contribute/build/): build from source, run the tests, and port Pi changes.
- [Parity](/parity/): how close PiSharp is to Pi today, and the differences that remain.

:::note
PiSharp is an independent port, not an official Pi release. Pi's own [documentation](https://github.com/earendil-works/pi/tree/v1.5.0/packages/coding-agent/docs) describes the behaviour PiSharp follows. Where PiSharp differs, these pages and the [Parity](/parity/) page say so.
:::
