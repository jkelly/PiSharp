---
title: Use the SDK
description: Embed PiSharp's libraries in your own .NET app.
---

PiSharp is a set of .NET libraries, and the `pisharp` CLI is built from them. You reference only the layers you need: a streaming chat client, the agent loop, the coding agent with its session files, or the durable harness. This page shows which package does what, how they depend on each other, and which APIs are still experimental.

## Install

Every library is a NuGet package that targets `net10.0`. The current release is {{PISHARP_VERSION}}:

```powershell
dotnet add package PiSharp.CodingAgent --version {{PISHARP_VERSION}}
```

A package brings the packages it is built on, so `PiSharp.CodingAgent` also gives you `PiSharp.Agent`, `PiSharp.AI`, `PiSharp.Sessions` and `PiSharp.Contracts`. All PiSharp packages share one version number. Use the same version for every PiSharp package you reference. From 1.5.0, each package depends on its PiSharp packages at exactly its own version (`[1.5.0]`), so NuGet reports a conflict instead of mixing packages from different releases, as Pi pins its own packages since 1.3.0. [Versioning](/docs/versioning/) explains how the number relates to the Pi release it ports.

The CLI itself is a .NET tool, not a library:

```powershell
dotnet tool install -g PiSharp.Cli
```

## Project templates

Install `PiSharp.Templates` to scaffold runnable starter projects with `dotnet new`:

```powershell
dotnet new install PiSharp.Templates@{{PISHARP_VERSION}}
```

| Template | Short name | What it scaffolds |
| --- | --- | --- |
| PiSharp Embedded Agent | `pisharp-agent` | Minimal embedded `PiSharp.Agent` console app with a custom tool, `IToolHooks` policy guard, and an offline scripted transport. |
| PiSharp Native C# Extension | `pisharp-extension` | Dynamically loaded `IPiSharpExtension` class library with `ExcludeAssets="runtime"` and a matching `pisharp-extension.json` manifest. |
| PiSharp Durable Workflow | `pisharp-workflow` | Crash-safe `PiSharp.CustomAgents` workflow on `PiSharp.Durable.Sqlite` with a durable `WaitForSignal` approval gate. |

```powershell
dotnet new pisharp-agent -n MyAgent
dotnet new pisharp-extension -n MyExtension
dotnet new pisharp-workflow -n MyWorkflow
```

## Pick a library

| You want to | Reference | Read |
| --- | --- | --- |
| Stream a chat completion from any provider Pi supports | `PiSharp.AI` | [Chat and providers](/docs/embed/chat/) |
| Bridge PiSharp with `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`) | `PiSharp.Extensions.AI` | [Microsoft.Extensions.AI bridge](/docs/embed/extensions-ai/) |
| Call classifier and image models | `PiSharp.AI` | [Classifiers and images](/docs/embed/model-operations/) |
| Run a tool-calling loop with your own tools | `PiSharp.Agent` | [Agent loop](/docs/embed/agent/) |
| Give the model Pi's read, write, edit, bash, grep, find and ls tools | `PiSharp.Tools`, and `PiSharp.Tools.Skia` to resize JPEG, GIF and WebP images | [Built-in tools](/docs/embed/coding-agent/#built-in-tools) |
| Read, write, branch and compact Pi's JSONL session files | `PiSharp.Sessions` | [Sessions library](/docs/embed/sessions/) |
| Embed the coding agent: persistent sessions, settings, skills and prompt templates | `PiSharp.CodingAgent` and `PiSharp.Tools` | [Coding agent](/docs/embed/coding-agent/) |
| Sessions that commit every step and resume after a crash | `PiSharp.Durable`, `PiSharp.Durable.Sqlite`, `PiSharp.Durable.Tools` (experimental) | [Durable library](/docs/embed/durable/) |
| Run model-written JavaScript that calls your tools | `PiSharp.Codemode` | [Code mode library](/docs/embed/codemode/) |
| Build a terminal UI | `PiSharp.Tui` | [Terminal UI](/docs/embed/tui/) |
| Serve Pi's JSONL RPC protocol from your process | `PiSharp.Rpc` | [RPC host](/docs/embed/rpc/) |
| Write an extension | `PiSharp.Extensions.Abstractions` | [Build an extension](/docs/extensions/build-an-extension/) |
| Load and run extensions in your app | `PiSharp.Extensions.Runtime`, `PiSharp.Extensions.Agent`, `PiSharp.ExtensionHost` | [Host extensions](/docs/embed/extension-host/) |
| Run Pi's TypeScript extensions | `PiSharp.Compatibility.Node` (needs Node) | [Node bridge](/docs/extensions/node-bridge/) |
| Build typed workflows, subagent teams and dynamic tools | `PiSharp.CustomAgents`, with `PiSharp.CustomAgents.JavaScript` and `PiSharp.CustomAgents.Scripting` for script tools (experimental) | [Custom agents](/docs/custom-agents/overview/) |
| Embeddings, speech, video, music and rerank | `PiSharp.AI`, namespace `PiSharp.AI.Media` (experimental) | [Media library](/docs/embed/media/) |

`PiSharp.Sessions` is the Pi session file: an append-only JSONL tree that you can branch and compact. It is not crash-safe execution. For sessions that survive a crash mid-step, use `PiSharp.Durable`.

`PiSharp.CodingAgent` does not depend on `PiSharp.Tools`. It runs whatever tools you register, so add `PiSharp.Tools` yourself when you want the built-in ones.

## Packages

The published {{PISHARP_VERSION}} NuGet inventory is **29 libraries, the `PiSharp.Cli` tool, and `PiSharp.Templates` (31 packages)**. The [SDK page](/sdk/) lists all packages with NuGet links and status. All sibling references must use the same exact release version.

| Package | Group | Purpose | Docs |
| --- | --- | --- | --- |
| `PiSharp.Contracts` | Core | Messages, stream events, transcript entries, JSON types, and the graduated `PiSharp.Contracts.Schema` JSON Schema validator (`pi-schema`). | [Chat and providers](/docs/embed/chat/#messages-and-stream-events) |
| `PiSharp.Telemetry` | Core, experimental | A port of Pi's telemetry contract: spans, links, metrics, propagation and typed schemas, with no-op, in-memory and `System.Diagnostics` (`DiagnosticsTelemetry`) adapters. | [Telemetry](/docs/embed/telemetry/) |
| `PiSharp.AI` | Core | Streaming transports for every Pi chat API, the model catalog reader, provider auth and logins, service tiers, and the classifier and image APIs. Also includes the experimental media library (`PiSharp.AI.Media`). | [Chat and providers](/docs/embed/chat/), [Classifiers and images](/docs/embed/model-operations/) |
| `PiSharp.Extensions.AI` | Core | Bidirectional `Microsoft.Extensions.AI` bridge: `PiChatClient`, `ChatClientTransportAdapter`, `PiEmbeddingGenerator`, and DI helpers. | [Microsoft.Extensions.AI bridge](/docs/embed/extensions-ai/) |
| `PiSharp.Agent` | Core | The agent loop: turns, tool invocation and batching, hooks, steering and follow-up queues. | [Agent loop](/docs/embed/agent/) |
| `PiSharp.Tools` | Core | Pi's built-in file and process tools, with output truncation and a shared write queue. | [Built-in tools](/docs/embed/coding-agent/#built-in-tools) |
| `PiSharp.Tools.Skia` | Core | An image codec on SkiaSharp that lets the read tool decode and resize PNG, JPEG, GIF, WebP and BMP. | [Images](/docs/embed/coding-agent/#images) |
| `PiSharp.Sessions` | Core | Pi's JSONL session tree: the codec, storage, context building, branching and compaction. | [Sessions library](/docs/embed/sessions/) |
| `PiSharp.CodingAgent` | Core | Persistent agent sessions, the runtime registry, settings, skills, prompt templates, export and share. | [Coding agent](/docs/embed/coding-agent/) |
| `PiSharp.PromptTemplates.Yaml` | Core | YAML frontmatter decoders for prompt templates and skills, on YamlDotNet. | [YAML frontmatter](/docs/embed/coding-agent/#yaml-frontmatter) |
| `PiSharp.Codemode` | Core | Model-written JavaScript in a Jint sandbox that calls the session's tools. | [Code mode library](/docs/embed/codemode/) |
| `PiSharp.Durable` | Core, experimental | The durable agent harness. Sessions commit every step before showing it and resume after a crash. | [Durable library](/docs/embed/durable/) |
| `PiSharp.Durable.Tools` | Core, experimental | The execution environment, coding tools, `tool_search`, code mode and the media tools for durable sessions. | [Durable library](/docs/embed/durable/#built-in-tools) |
| `PiSharp.Durable.Sqlite` | Core, experimental | SQLite storage for durable sessions, on Microsoft.Data.Sqlite. | [Durable library](/docs/embed/durable/#storage) |
| `PiSharp.CustomAgents` | Core, experimental | Typed workflows, agent teams, subagents, messaging, typed and dynamic tools, and plugins on the durable harness. | [Custom agents](/docs/custom-agents/overview/) |
| `PiSharp.CustomAgents.JavaScript` | Core, experimental | JavaScript tools and workflow actions for custom agents, in code mode's Jint sandbox. | [Agent tools](/docs/custom-agents/tools/) |
| `PiSharp.CustomAgents.Scripting` | Core, experimental | C# script tools and workflow actions for custom agents, compiled with Roslyn. | [Agent tools](/docs/custom-agents/tools/) |
| `PiSharp.Rpc` | Frontends | Pi's JSONL RPC protocol over stdin and stdout, including extension UI requests. | [RPC host](/docs/embed/rpc/) |
| `PiSharp.Tui` | Frontends | Terminal UI components, layout, overlays and terminals. | [Terminal UI](/docs/embed/tui/) |
| `PiSharp.Cli` | Frontends, .NET tool | The `pisharp` command-line app. | [CLI reference](/docs/reference/cli/) |
| `PiSharp.Templates` | Frontends, `dotnet new` | Starter templates for embedded agents, native C# extensions, and durable workflows. | [Project templates](#project-templates) |
| `PiSharp.Extensions.Abstractions` | Extensions | The interfaces you implement to write an extension. | [Build an extension](/docs/extensions/build-an-extension/) |
| `PiSharp.Extensions.Runtime` | Extensions | Discovery, loading, registration and dispatch for extension hosts. | [Host extensions](/docs/embed/extension-host/) |
| `PiSharp.Extensions.Agent` | Extensions | Connects extension hooks to the agent loop. | [Host extensions](/docs/embed/extension-host/) |
| `PiSharp.ExtensionHost` | Extensions | Supervised out-of-process extension workers. | [Host extensions](/docs/embed/extension-host/) |
| `PiSharp.Compatibility.Node` | Extensions, optional | A bridge that runs Pi's TypeScript extensions. Requires Node. | [Node bridge](/docs/extensions/node-bridge/) |

## How the layers fit

Each package references only the layers below it. `PiSharp.Contracts` and `PiSharp.Telemetry` are at the bottom and reference nothing. `PiSharp.Tui` stands alone and references no other PiSharp package.

| Package | Builds on | Third-party dependencies |
| --- | --- | --- |
| `PiSharp.Contracts` | nothing | none |
| `PiSharp.Telemetry` | nothing | none |
| `PiSharp.AI` | Contracts, Telemetry | none |
| `PiSharp.Extensions.AI` | Contracts, AI | Microsoft.Extensions.AI.Abstractions 10.11.1, Microsoft.Extensions.DependencyInjection.Abstractions 10.0.0 |
| `PiSharp.Agent` | Contracts, AI | none |
| `PiSharp.Tools` | Contracts, Agent | none |
| `PiSharp.Tools.Skia` | Tools | SkiaSharp 4.152.1 and its native assets |
| `PiSharp.Sessions` | Contracts | none |
| `PiSharp.CodingAgent` | Agent, Sessions | none |
| `PiSharp.PromptTemplates.Yaml` | CodingAgent | YamlDotNet 16.3.0 |
| `PiSharp.Codemode` | Contracts, Extensions.Abstractions | Jint 4.16.3 |
| `PiSharp.Durable` | Contracts, AI | none |
| `PiSharp.Durable.Tools` | Durable, Tools, Codemode | none |
| `PiSharp.Durable.Sqlite` | Durable | Microsoft.Data.Sqlite 10.0.12, SQLitePCLRaw 2.1.12 |
| `PiSharp.CustomAgents` | Durable, Extensions.Abstractions | none |
| `PiSharp.CustomAgents.JavaScript` | CustomAgents, Codemode, Durable.Tools | none |
| `PiSharp.CustomAgents.Scripting` | CustomAgents | Microsoft.CodeAnalysis.CSharp 5.6.0 |
| `PiSharp.Rpc` | Contracts, CodingAgent, Extensions.Abstractions | none |
| `PiSharp.Tui` | nothing | none |
| `PiSharp.Extensions.Abstractions` | Contracts | none |
| `PiSharp.Extensions.Runtime` | Extensions.Abstractions | none |
| `PiSharp.Extensions.Agent` | Agent, Extensions.Runtime | none |
| `PiSharp.ExtensionHost` | Contracts | none |
| `PiSharp.Compatibility.Node` | Contracts, ExtensionHost, Extensions.Abstractions, Extensions.Runtime | none |

Read the table from the top. The chat layer (`PiSharp.AI`) knows nothing about tools. The agent layer (`PiSharp.Agent`) runs tools but does not know about files or session storage. The coding agent (`PiSharp.CodingAgent`) joins the agent loop to `PiSharp.Sessions`. The durable harness is a separate stack on `PiSharp.AI`; it does not use `PiSharp.Agent` or `PiSharp.Sessions`.

The CLI references almost everything: CodingAgent, PromptTemplates.Yaml, Tools, Tools.Skia, Rpc, Tui, Extensions.Runtime, Extensions.Agent, Codemode, Compatibility.Node, Durable, Durable.Sqlite and Durable.Tools, and three projects that ship only inside the tool: ModelRuntime, Durable.Hosting and Durable.Server (which brings Durable.Client). Durable.Server needs the ASP.NET Core runtime, so the tool does too. [Architecture](/docs/reference/architecture/) describes how the CLI wires them together.

## Custom agents

The three custom agents packages build crash-safe agents in C# on the durable harness:

| Package | Purpose | Builds on |
| --- | --- | --- |
| `PiSharp.CustomAgents` | Typed workflows over a shared data class, agents and teams, typed and dynamic tools, messaging and subagents, on the durable harness. | Durable, Extensions.Abstractions |
| `PiSharp.CustomAgents.JavaScript` | JavaScript tools and workflow actions on code mode's Jint sandbox, with durable replay for tools that call other tools. | CustomAgents, Codemode, Durable.Tools |
| `PiSharp.CustomAgents.Scripting` | C# script tools and workflow actions (`*.tool.csx`), compiled with Roslyn and unloaded when replaced. | CustomAgents, Microsoft.CodeAnalysis.CSharp 5.6.0 |

They are experimental, from 1.2.0.2. [Custom agents](/docs/custom-agents/overview/) covers the API.

## Experimental APIs

Some APIs carry `[Experimental("<ID>")]`. The compiler reports an error when you use one, until you suppress its ID. An experimental API may change or be removed without notice.

:::note[Tier-1 APIs graduated to stable in 1.6.0.1]
Per [ADR 0017](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0017-experimental-graduation-tier1.md), seven proven Tier-1 diagnostic IDs graduated to stable in **PiSharp 1.6.0.1** and no longer require suppression: `PISHARP_SCHEMA001` (`PiSharp.Contracts.Schema`), `PISHARP_IDENTITY001` (`ClientIdentity`), `PISHARP_TIERS001` (`ServiceTiers`), `PISHARP_AI001` (`StreamingJsonParser`), `PISHARP_AUTH001` (OAuth confirmation hints), `PISHARP_EXEC001` (`pi.exec` buffer limits), and `PISHARP_TOOLSEARCH001` (`ToolSearchDocument.NamespaceText`). Projects still referencing earlier **1.6.0** NuGet packages continue to need `<NoWarn>` for those IDs.
:::

| ID | Covers | Status |
| --- | --- | --- |
| `PISHARP_DURABLE001` | Every public type in `PiSharp.Durable`, `PiSharp.Durable.Tools`, `PiSharp.Durable.Sqlite`, and `PiSharp.Durable.Store`. | Experimental. |
| `PISHARP_MEDIA001` | The `PiSharp.AI.Media` namespace of `PiSharp.AI` (and `PiEmbeddingGenerator` in `PiSharp.Extensions.AI`): embeddings, images, video, speech, transcription, music, rerank and decisions. | Experimental. |
| `PISHARP_AGENTS001` | Every public type in the custom agents packages (`PiSharp.CustomAgents*`). These also use durable types, so suppress `PISHARP_DURABLE001` too. | Experimental. |
| `PISHARP_TELEMETRY001` | Every public type in `PiSharp.Telemetry`, and the telemetry members of `PiSharp.AI`. See [Telemetry](/docs/embed/telemetry/). | Experimental. |
| `PISHARP_CHORD001` | Every public type in `PiSharp.Chord` and `PiSharp.Chord.Hosting`. See [Chord](/docs/embed/chord/). | Experimental. |
| `PISHARP_ENV001` | Every public type in `PiSharp.Env.Protocol` and `PiSharp.Env`. See [Remote environments](/docs/embed/env/). | Experimental. |
| `PISHARP_CATALOG001` | `MaintainedCatalogOverlay` and `MaintainedCatalogResult` in `PiSharp.AI`, which apply PiSharp's maintained model catalog layer. | Experimental. |
| `PISHARP_EXTUI001` | Extension UI additions from the Pi 1.5.0 sync in `PiSharp.Extensions.Abstractions` and `PiSharp.CodingAgent`. | Experimental. |
| `PISHARP_CODEMODE001` | Worker admission in `PiSharp.Codemode`: `CodemodeSandboxOptions.AcquireWorker` and `.OnWorkerStarted`. | Experimental. |
| `PISHARP_TUI001` | TUI additions in `PiSharp.Tui` from 1.5.0 and 1.6.0. | Experimental. |

Suppress the IDs you use in your project file:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);PISHARP_DURABLE001;PISHARP_AGENTS001</NoWarn>
</PropertyGroup>
```

To allow an experimental API in one file only, use a pragma instead:

```csharp
#pragma warning disable PISHARP_DURABLE001
```

## What you supply

The libraries do not read your environment on their own. You pass in the things the CLI would otherwise discover:

- **Models and keys.** `PiSharp.AI` builds a transport from a model, an endpoint and a key that you supply. Helpers read provider keys from an environment lookup you pass in. `PiSharp.AI` reads Pi's model catalog files but has no public loader for a built-in copy, and the `auth.json` credential store belongs to the CLI. See [Chat and providers](/docs/embed/chat/).
- **Tool permissions.** Built-in tools run only through an `IToolActionPolicy` that you write. The policy sees each final file path or command and decides. See [Built-in tools](/docs/embed/coding-agent/#built-in-tools).
- **Paths.** Settings files, skill folders, prompt template folders and session files are absolute paths you choose. See [Coding agent](/docs/embed/coding-agent/).

## Runnable agent examples

The repository includes [62 sample projects](https://github.com/jkelly/PiSharp/tree/main/samples/agents), each with a fixture demo, input data, README and focused self-tests. They cover everyday workflows, native session branching, concurrent agents, a model/pricing catalog maintainer, a NuGet vulnerability maintainer, and operations, IT, HR, finance and documentation workflows.

```powershell
dotnet build samples/agents/PiSharp.Samples.slnx -m:1
dotnet run --project samples/agents/01-InboxTriage
pwsh -File samples/agents/scripts/run-all.ps1
```

Fixture demos require no API key. Live model execution and repository publishing are explicit options; the sample guide documents provider configuration, approval boundaries and inactive scheduling templates.
