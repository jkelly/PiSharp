---
title: Overview
description: What PiSharp is, what it targets, and where to start.
---

PiSharp is an independent native .NET implementation of the [Pi coding agent](https://pi.dev). It targets .NET 10, has no external package dependencies, and runs without Node.

PiSharp's version is the Pi version it matches. The current baseline is **Pi v1.1.0**, pinned to commit `abe508e`. See [How versions work](/docs/versioning/).

## What's included

- **Agent and tools:** the agent loop, built-in file and process tools, steering and follow-up queues.
- **Providers:** streaming adapters for Anthropic Messages, OpenAI Completions and Responses, Azure Responses, Google Generative AI and Mistral Conversations.
- **Sessions:** tree-structured, branchable sessions with compaction.
- **Frontends:** interactive terminal UI, print/JSON output, and a JSON RPC protocol over stdin/stdout.
- **Extensions:** a native C# extension SDK, plus an optional Node bridge for Pi's TypeScript extensions.

Every layer is a separate library. See the [SDK](/sdk/) for the full list.

## Where to go next

- [Quickstart](/docs/quickstart/): install PiSharp with `dotnet tool install -g PiSharp.Cli` and run it.
- [Coming from Pi](/docs/coming-from-pi/): what carries over and what's different.
- [Build an extension](/docs/extensions/build-an-extension/): write your first native extension.
- [Parity](/parity/): how close PiSharp is to Pi today.

:::note
PiSharp is an independent port, not an official Pi release. Complete upstream parity is still in progress.
:::
