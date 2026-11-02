---
title: Overview
description: What PiSharp is, what it targets, and where to start.
---

PiSharp is an independent native .NET implementation of the [Pi coding agent](https://pi.dev). It targets .NET 10. The agent itself runs without Node; Node is only needed for TypeScript extensions.

PiSharp's version is the Pi version it matches. The current baseline is **Pi v1.2.0**, pinned to commit `06a4669`. See [How versions work](/docs/versioning/).

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

## New in 1.2.0.2

These features are new in PiSharp 1.2.0.2. They are PiSharp's own, not Pi's, and experimental: their pages are marked **Experimental**, and they may change in any release.

- [Custom agents](/docs/custom-agents/overview/): typed workflows, agent teams, and typed, scripted and dynamic tools, built on the durable runtime, in three new packages.
- [Media tools](/docs/guides/media/): image, video, speech, music, transcription, embeddings and reranking, for durable sessions and from the [SDK](/docs/embed/media/).
- [Service tiers](/docs/guides/service-tiers/): ask a provider for economy, priority or fast capacity with `--tier` and `/tier`.
- [Durable device server](/docs/guides/durable/#device-server): `pisharp durable serve` runs many durable sessions behind an authenticated local API, with questions and plan mode in `pisharp durable`.

## Where to go next

- [Quickstart](/docs/quickstart/): install PiSharp with `dotnet tool install -g PiSharp.Cli` and run it.
- [Coming from Pi](/docs/coming-from-pi/): what carries over and what's different.
- [Command line](/docs/reference/cli/), [Settings](/docs/reference/settings/) and [Files and locations](/docs/reference/configuration/).
- [Use the SDK](/docs/embed/sdk/): embed the agent, or just one layer, in your own .NET app.
- [Extend PiSharp](/docs/extensions/build-an-extension/): write a C# extension, or load Pi's TypeScript ones.
- [Contribute](/docs/contribute/build/): build from source, run the tests, and port Pi changes.
- [Parity](/parity/): how close PiSharp is to Pi today, and the differences that remain.

:::note
PiSharp is an independent port, not an official Pi release. Pi's own [documentation](https://github.com/earendil-works/pi/tree/v1.2.0/packages/coding-agent/docs) describes the behaviour PiSharp follows. Where PiSharp differs, these pages and the [Parity](/parity/) page say so.
:::
