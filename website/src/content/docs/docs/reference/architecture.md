---
title: Architecture
description: How PiSharp is put together.
---

PiSharp is built as layered libraries, one job each. Every library references only libraries below it, and the CLI, `PiSharp.Cli`, is the one project that puts them all together. All of them target `net10.0`. You can embed any library on its own: see the [SDK overview](/docs/embed/sdk/).

## Libraries by area

| Area | Libraries |
| --- | --- |
| Contracts | `PiSharp.Contracts` |
| Providers | `PiSharp.AI`, which also holds the media library in `PiSharp.AI.Media` ([experimental](/docs/embed/media/)) |
| Agent | `PiSharp.Agent`, `PiSharp.Tools`, `PiSharp.Tools.Skia`, `PiSharp.Sessions` |
| Coding agent | `PiSharp.CodingAgent`, `PiSharp.Codemode`, `PiSharp.PromptTemplates.Yaml` |
| Durable agents | `PiSharp.Durable`, `PiSharp.Durable.Sqlite`, `PiSharp.Durable.Tools` (released as experimental) |
| Durable device | `PiSharp.Durable.Hosting`, `PiSharp.Durable.Server`, `PiSharp.Durable.Client` (experimental, inside the CLI tool) |
| Custom agents | `PiSharp.CustomAgents`, `PiSharp.CustomAgents.JavaScript`, `PiSharp.CustomAgents.Scripting` ([experimental](/docs/custom-agents/overview/)) |
| Extensions | `PiSharp.Extensions.Abstractions`, `PiSharp.Extensions.Runtime`, `PiSharp.Extensions.Agent`, `PiSharp.ExtensionHost`, `PiSharp.Compatibility.Node` (the Node bridge) |
| Terminal UI | `PiSharp.Tui` |
| Frontends | `PiSharp.Rpc`, `PiSharp.ModelRuntime`, `PiSharp.Cli` |

## What each library holds

| Library | Holds |
| --- | --- |
| `PiSharp.Contracts` | The shared message, streaming and JSON types every other library uses. |
| `PiSharp.AI` | The provider transports for every Pi chat API, the model catalogs, authentication and logins, and model operations such as classifiers and image generation. Its `Media` folder is the media library: images, video, audio, embeddings and ranking (experimental). |
| `PiSharp.Agent` | The agent loop: turns, tool calls and their batching, steering and follow-up queues. |
| `PiSharp.Tools` | The built-in tools: file tools, process and shell tools, and the image seam the `read` tool uses. |
| `PiSharp.Tools.Skia` | The SkiaSharp image codec behind that seam: decoding, resizing and re-encoding images as Pi does. |
| `PiSharp.Sessions` | Tree-structured session files: storage, branches, context, compaction and import. |
| `PiSharp.CodingAgent` | The coding agent runtime on the agent and sessions: persistent sessions, recovery, tree navigation and HTML export. |
| `PiSharp.Codemode` | [Code mode](/docs/embed/codemode/): model-written JavaScript in a Jint sandbox that calls the session's tools. |
| `PiSharp.PromptTemplates.Yaml` | YAML frontmatter for prompt templates. The CLI can be built without it. |
| `PiSharp.Durable` | The [durable harness](/docs/embed/durable/), ported from Pi's pi-durable: sessions and tasks that commit every step before showing it and resume after a crash, with in-memory and JSONL storage. |
| `PiSharp.Durable.Sqlite` | SQLite storage for durable sessions. |
| `PiSharp.Durable.Tools` | The durable execution environment, coding tools, `tool_search`, code mode, and the media tools of `pisharp durable` (experimental). |
| `PiSharp.Durable.Hosting` | Many durable sessions in one process, for `pisharp durable serve`: the device catalog, locks, jobs, schedules and the other device services. Not a package; it ships inside the CLI tool. |
| `PiSharp.Durable.Server` | The device's authenticated HTTP and server-sent-events API, an ASP.NET Core library hosted by `pisharp durable serve`. Not a package; it ships inside the CLI tool. |
| `PiSharp.Durable.Client` | The device API's wire types and a client for one device, used by the CLI's device commands. Not a package; it ships inside the CLI tool. |
| `PiSharp.CustomAgents` | Typed workflows, agents, teams, subagents, tools and plugins on the durable harness. |
| `PiSharp.CustomAgents.JavaScript` | JavaScript tools and workflow actions for custom agents, on the code mode sandbox. |
| `PiSharp.CustomAgents.Scripting` | C# script tools and workflow actions for custom agents, compiled with Roslyn. |
| `PiSharp.Extensions.Abstractions` | The interfaces a native C# extension implements. |
| `PiSharp.Extensions.Runtime` | Extension discovery, loading, registration, dispatch and reloading for hosts, including MCP. |
| `PiSharp.Extensions.Agent` | Connects extension hooks to the agent loop. |
| `PiSharp.ExtensionHost` | Supervised out-of-process extension workers and their protocol. |
| `PiSharp.Compatibility.Node` | The optional [Node bridge](/docs/extensions/node-bridge/) for Pi's TypeScript extensions. It needs Node.js. |
| `PiSharp.Tui` | Terminal UI building blocks: components, input, rendering, and the Windows console terminal. It depends on no other PiSharp library. |
| `PiSharp.Rpc` | Pi's JSONL RPC protocol over stdin and stdout, including extension UI requests. |
| `PiSharp.ModelRuntime` | The CLI's model registry: the built-in catalog, `models.json`, the provider auth catalog, the live provider routes and the maintained catalog layer. Not a package; it ships inside the CLI tool. |
| `PiSharp.Cli` | The `pisharp` command: interactive mode, print and JSON modes, RPC mode, the commands, settings, MCP, packages and `pisharp durable`. |

## Dependency order

These are each project's direct references, from the bottom up. The test is the same at every row: a library references only rows above it.

| Library | References | Third-party packages |
| --- | --- | --- |
| `PiSharp.Contracts` | none | none |
| `PiSharp.Tui` | none | none |
| `PiSharp.AI` | Contracts | none |
| `PiSharp.Sessions` | Contracts | none |
| `PiSharp.Extensions.Abstractions` | Contracts | none |
| `PiSharp.ExtensionHost` | Contracts | none |
| `PiSharp.Agent` | Contracts, AI | none |
| `PiSharp.Durable` | Contracts, AI | none |
| `PiSharp.Extensions.Runtime` | Extensions.Abstractions | none |
| `PiSharp.Codemode` | Contracts, Extensions.Abstractions | Jint 4.16.3 (with Acornima) |
| `PiSharp.Tools` | Contracts, Agent | none |
| `PiSharp.CodingAgent` | Agent, Sessions | none |
| `PiSharp.Extensions.Agent` | Agent, Extensions.Runtime | none |
| `PiSharp.Compatibility.Node` | Contracts, ExtensionHost, Extensions.Abstractions, Extensions.Runtime | none |
| `PiSharp.Durable.Sqlite` | Durable | Microsoft.Data.Sqlite 10.0.12, SQLitePCLRaw 2.1.12 |
| `PiSharp.CustomAgents` | Durable, Extensions.Abstractions | none |
| `PiSharp.Tools.Skia` | Tools | SkiaSharp 4.152.1 with its native assets |
| `PiSharp.PromptTemplates.Yaml` | CodingAgent | YamlDotNet 16.3.0 |
| `PiSharp.Rpc` | Contracts, CodingAgent, Extensions.Abstractions | none |
| `PiSharp.Durable.Tools` | Durable, Tools, Codemode | none |
| `PiSharp.CustomAgents.Scripting` | CustomAgents | Microsoft.CodeAnalysis.CSharp 5.6.0 |
| `PiSharp.CustomAgents.JavaScript` | CustomAgents, Codemode, Durable.Tools | none |
| `PiSharp.Durable.Client` | none | none |
| `PiSharp.Durable.Hosting` | Durable, Durable.Sqlite, Durable.Tools | none |
| `PiSharp.Durable.Server` | Durable.Hosting, Durable.Client | the ASP.NET Core shared framework |
| `PiSharp.ModelRuntime` | CodingAgent, Tools, Rpc, Extensions.Runtime, Extensions.Agent | none |
| `PiSharp.Cli` | CodingAgent, ModelRuntime, PromptTemplates.Yaml, Tools, Tools.Skia, Rpc, Tui, Extensions.Runtime, Extensions.Agent, Codemode, Compatibility.Node, Durable, Durable.Sqlite, Durable.Tools, Durable.Hosting, Durable.Server | none |

A few things the table shows:

- The durable harness is a second stack beside the agent and sessions. `PiSharp.Durable` builds on the providers alone, and `PiSharp.Durable.Tools` reuses the built-in tools and code mode.
- `PiSharp.CodingAgent` doesn't reference the built-in tools. The CLI brings in `PiSharp.Tools` and the Skia image codec.
- `PiSharp.Tui` stands alone, and the CLI's interactive mode is built on it.
- The custom agents projects sit on top of the durable harness, and the CLI doesn't reference them.

The third-party packages are pinned exactly, and their licenses are in [`third-party/`](https://github.com/jkelly/PiSharp/tree/main/third-party) and [`THIRD-PARTY-NOTICES.md`](https://github.com/jkelly/PiSharp/blob/main/THIRD-PARTY-NOTICES.md). Every other library uses only .NET.

## Released and unreleased parts

| Part | Status | Where |
| --- | --- | --- |
| The 22 libraries listed on the SDK page, and the `PiSharp.Cli` tool | Released on NuGet, 1.2.0.2 | [SDK](/sdk/) |
| Durable libraries and `pisharp durable` | Released as experimental in 1.2.0.1. Every durable API carries `[Experimental("PISHARP_DURABLE001")]`. | [Pi Durable](/docs/guides/durable/) |
| Media library (`PiSharp.AI.Media`) and the media tools | Released as experimental in 1.2.0.2. `[Experimental("PISHARP_MEDIA001")]`. | [Media](/docs/embed/media/) |
| Service tiers | Released as experimental in 1.2.0.2. `[Experimental("PISHARP_TIERS001")]`. | [Service tiers](/docs/guides/service-tiers/) |
| Custom agents | Released as experimental in 1.2.0.2, as three packages. `[Experimental("PISHARP_AGENTS001")]` on every public type. | [Custom agents](/docs/custom-agents/overview/) |
| Durable device server (`pisharp durable serve`) | Released as experimental in 1.2.0.2, inside the CLI tool. `PiSharp.Durable.Hosting`, `PiSharp.Durable.Server` and `PiSharp.Durable.Client` are not packages. | [Device server](/docs/guides/durable/#device-server) |
| `PiSharp.ModelRuntime` | Inside the CLI tool, not a package. | |

The repository also holds projects that are not released at all: a local web chat app (`PiSharp.WebAgent`), a web UI for durable devices (`PiSharp.Durable.Web`), SQL Server storage for durable sessions (`PiSharp.Durable.MSSQL`) and `PiSharp.Web.Common`. To use them, build PiSharp from source: see [Build from source](/docs/contribute/build/).

## Design principles

- **Streaming is explicit.** Transport fragments, incremental updates and committed messages are separate. A partial tool call is for display only; it can't run until its arguments are fully parsed and validated.
- **Cancellation flows everywhere.** Cancellation tokens reach every transport, tool and hook, and each operation ends in exactly one outcome.
- **Retries are safe.** A provider retry never reruns a completed shell command or file change.
- **Sessions are append-only.** The event log is the source of truth, and the current branch and context are built from it.
- **Durable sessions commit first.** The durable harness stores every step before it shows it, so a session resumes where it stopped after a crash.

The original design document is [`docs/architecture.md`](https://github.com/jkelly/PiSharp/blob/main/docs/architecture.md). It was written as the implementation plan, before the durable, media and custom agents work, so read it for the design rules rather than as a list of projects. The phased plans are in [`docs/plans`](https://github.com/jkelly/PiSharp/tree/main/docs/plans), and the decisions that shaped each part are listed in [How the port tracks Pi](/docs/contribute/porting/#decision-records).
