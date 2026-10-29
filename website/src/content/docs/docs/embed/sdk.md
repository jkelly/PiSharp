---
title: Use the SDK
description: Embed PiSharp's libraries in your own .NET app.
---

PiSharp is a set of .NET libraries, and the `pisharp` CLI is built from them. You reference only the layers you need: a streaming chat client, the agent loop, the coding agent with its session files, or the durable harness. This page shows which package does what, how they depend on each other, and which APIs are still experimental.

## Install

Every library is a NuGet package that targets `net10.0`. The current release is 1.2.0.1:

```powershell
dotnet add package PiSharp.CodingAgent --version 1.2.0.1
```

A package brings the packages it is built on, so `PiSharp.CodingAgent` also gives you `PiSharp.Agent`, `PiSharp.AI`, `PiSharp.Sessions` and `PiSharp.Contracts`. All PiSharp packages share one version number. Use the same version for every PiSharp package you reference. [Versioning](/docs/versioning/) explains how the number relates to the Pi release it ports.

The CLI itself is a .NET tool, not a library:

```powershell
dotnet tool install -g PiSharp.Cli
```

## Pick a library

| You want to | Reference | Read |
| --- | --- | --- |
| Stream a chat completion from any provider Pi supports | `PiSharp.AI` | [Chat and providers](/docs/embed/chat/) |
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
| Build typed workflows, subagent teams and dynamic tools | `PiSharp.CustomAgents` (experimental, unreleased) | [Custom agents](/docs/custom-agents/overview/) |
| Embeddings, speech, video, music and rerank | `PiSharp.AI.Media` namespace (experimental, unreleased) | [Media library](/docs/embed/media/) |

`PiSharp.Sessions` is the Pi session file: an append-only JSONL tree that you can branch and compact. It is not crash-safe execution. For sessions that survive a crash mid-step, use `PiSharp.Durable`.

`PiSharp.CodingAgent` does not depend on `PiSharp.Tools`. It runs whatever tools you register, so add `PiSharp.Tools` yourself when you want the built-in ones.

## Packages

These are the 20 packages of the 1.2.0.1 release. The [SDK page](/sdk/) lists the same set.

| Package | Group | Purpose | Docs |
| --- | --- | --- | --- |
| `PiSharp.Contracts` | Core | Messages, stream events, transcript entries and the JSON types every other package shares. | [Chat and providers](/docs/embed/chat/#messages-and-stream-events) |
| `PiSharp.AI` | Core | Streaming transports for every Pi chat API, the model catalog reader, provider auth and logins, and the classifier and image APIs. | [Chat and providers](/docs/embed/chat/), [Classifiers and images](/docs/embed/model-operations/) |
| `PiSharp.Agent` | Core | The agent loop: turns, tool invocation and batching, hooks, steering and follow-up queues. | [Agent loop](/docs/embed/agent/) |
| `PiSharp.Tools` | Core | Pi's built-in file and process tools, with output truncation and a shared write queue. | [Built-in tools](/docs/embed/coding-agent/#built-in-tools) |
| `PiSharp.Tools.Skia` | Core | An image codec on SkiaSharp that lets the read tool decode and resize PNG, JPEG, GIF, WebP and BMP. | [Images](/docs/embed/coding-agent/#images) |
| `PiSharp.Sessions` | Core | Pi's JSONL session tree: the codec, storage, context building, branching and compaction. | [Sessions library](/docs/embed/sessions/) |
| `PiSharp.CodingAgent` | Core | Persistent agent sessions, the runtime registry, settings, skills, prompt templates, export and share. | [Coding agent](/docs/embed/coding-agent/) |
| `PiSharp.PromptTemplates.Yaml` | Core | YAML frontmatter decoders for prompt templates and skills, on YamlDotNet. | [YAML frontmatter](/docs/embed/coding-agent/#yaml-frontmatter) |
| `PiSharp.Codemode` | Core | Model-written JavaScript in a Jint sandbox that calls the session's tools. | [Code mode library](/docs/embed/codemode/) |
| `PiSharp.Durable` | Core, experimental | The durable agent harness. Sessions commit every step before showing it and resume after a crash. | [Durable library](/docs/embed/durable/) |
| `PiSharp.Durable.Tools` | Core, experimental | The execution environment, coding tools, `tool_search` and code mode for durable sessions. | [Durable library](/docs/embed/durable/#built-in-tools) |
| `PiSharp.Durable.Sqlite` | Core, experimental | SQLite storage for durable sessions, on Microsoft.Data.Sqlite. | [Durable library](/docs/embed/durable/#storage) |
| `PiSharp.Rpc` | Frontends | Pi's JSONL RPC protocol over stdin and stdout, including extension UI requests. | [RPC host](/docs/embed/rpc/) |
| `PiSharp.Tui` | Frontends | Terminal UI components, layout, overlays and terminals. | [Terminal UI](/docs/embed/tui/) |
| `PiSharp.Cli` | Frontends, .NET tool | The `pisharp` command-line app. | [CLI reference](/docs/reference/cli/) |
| `PiSharp.Extensions.Abstractions` | Extensions | The interfaces you implement to write an extension. | [Build an extension](/docs/extensions/build-an-extension/) |
| `PiSharp.Extensions.Runtime` | Extensions | Discovery, loading, registration and dispatch for extension hosts. | [Host extensions](/docs/embed/extension-host/) |
| `PiSharp.Extensions.Agent` | Extensions | Connects extension hooks to the agent loop. | [Host extensions](/docs/embed/extension-host/) |
| `PiSharp.ExtensionHost` | Extensions | Supervised out-of-process extension workers. | [Host extensions](/docs/embed/extension-host/) |
| `PiSharp.Compatibility.Node` | Extensions, optional | A bridge that runs Pi's TypeScript extensions. Requires Node. | [Node bridge](/docs/extensions/node-bridge/) |

## How the layers fit

Each package references only the layers below it. `PiSharp.Contracts` is at the bottom and references nothing. `PiSharp.Tui` stands alone and references no other PiSharp package.

| Package | Builds on | Third-party dependencies |
| --- | --- | --- |
| `PiSharp.Contracts` | nothing | none |
| `PiSharp.AI` | Contracts | none |
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
| `PiSharp.Rpc` | Contracts, CodingAgent, Extensions.Abstractions | none |
| `PiSharp.Tui` | nothing | none |
| `PiSharp.Extensions.Abstractions` | Contracts | none |
| `PiSharp.Extensions.Runtime` | Extensions.Abstractions | none |
| `PiSharp.Extensions.Agent` | Agent, Extensions.Runtime | none |
| `PiSharp.ExtensionHost` | Contracts | none |
| `PiSharp.Compatibility.Node` | Contracts, ExtensionHost, Extensions.Abstractions, Extensions.Runtime | none |

Read the table from the top. The chat layer (`PiSharp.AI`) knows nothing about tools. The agent layer (`PiSharp.Agent`) runs tools but does not know about files or session storage. The coding agent (`PiSharp.CodingAgent`) joins the agent loop to `PiSharp.Sessions`. The durable harness is a separate stack on `PiSharp.AI`; it does not use `PiSharp.Agent` or `PiSharp.Sessions`.

The CLI references almost everything: CodingAgent, PromptTemplates.Yaml, Tools, Tools.Skia, Rpc, Tui, Extensions.Runtime, Extensions.Agent, Codemode, Compatibility.Node, Durable, Durable.Sqlite and Durable.Tools. [Architecture](/docs/reference/architecture/) describes how the CLI wires them together.

## Custom agents

Three more projects are on PiSharp's `main` branch but are not packaged or released:

| Project | Purpose | Builds on |
| --- | --- | --- |
| `src/PiSharp.CustomAgents` | Typed workflows over a shared data class, agents and teams, typed and dynamic tools, messaging and subagents, on the durable harness. | Durable, Extensions.Abstractions |
| `src/PiSharp.CustomAgents.JavaScript` | JavaScript tools and workflow actions on code mode's Jint sandbox, with durable replay for tools that call other tools. | CustomAgents, Codemode, Durable.Tools |
| `src/PiSharp.CustomAgents.Scripting` | C# script tools and workflow actions (`*.tool.csx`), compiled with Roslyn and unloaded when replaced. | CustomAgents, Microsoft.CodeAnalysis.CSharp 5.6.0 |

They are experimental and unreleased. To use them, build PiSharp from source and reference the projects directly (see [Build from source](/docs/contribute/build/)). [Custom agents](/docs/custom-agents/overview/) covers the API.

## Experimental APIs

Some APIs carry `[Experimental("<ID>")]`. The compiler reports an error when you use one, until you suppress its ID. An experimental API may change or be removed without notice.

| ID | Covers | Status |
| --- | --- | --- |
| `PISHARP_DURABLE001` | Every public type in `PiSharp.Durable`, `PiSharp.Durable.Tools` and `PiSharp.Durable.Sqlite`. | Released as experimental in 1.2.0.1. |
| `PISHARP_MEDIA001` | The `PiSharp.AI.Media` namespace of `PiSharp.AI`: embeddings, images, video, speech, transcription, music, rerank and decisions. | Unreleased: on `main` only. |
| `PISHARP_TIERS001` | Service tiers: `ChatRequest.ServiceTier`, `AgentConfiguration.ServiceTier`, `SessionRuntimeRegistryOptions.ServiceTier`, the protocol request options' `ServiceTier` and the `ServiceTiers` class. See [Service tiers](/docs/guides/service-tiers/). | Unreleased: on `main` only. |
| `PISHARP_AGENTS001` | Every public type in the custom agents projects. These also use durable types, so suppress `PISHARP_DURABLE001` too. | Unreleased: source only. |

Suppress the IDs you use in your project file:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);PISHARP_DURABLE001;PISHARP_TIERS001</NoWarn>
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
