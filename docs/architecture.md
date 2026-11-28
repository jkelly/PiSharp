# PiSharp Architecture

PiSharp is an independent, native **.NET 10 / C#** implementation of the [Pi coding agent](https://github.com/earendil-works/pi). It runs without Node.js for its native core and C# extensions, and hosts Pi's TypeScript/JavaScript extensions through an optional Node.js compatibility bridge.

> The original pre-implementation v0.99.1 design proposal is preserved verbatim at [docs/decisions/0000-initial-architecture-proposal.md](decisions/0000-initial-architecture-proposal.md). For published release status, compatibility inventories, and NuGet package versions, see [compatibility/public-release.json](../compatibility/public-release.json), [pisharp.ai/docs/how-it-works/overview/](https://pisharp.ai/docs/how-it-works/overview/), and [pisharp.ai/docs/reference/architecture/](https://pisharp.ai/docs/reference/architecture/).

## Layered Package Architecture

PiSharp 1.6.0.2 ships **31 NuGet packages**. These comprise 29 libraries, the CLI tool, and the project templates. Library dependencies within the release use exact version pins. The 1.7.0 source preview follows the same package layout. [Preview and migration guide](release-notes/1.7.0.md)

| Layer | Projects / Packages | Responsibility |
| --- | --- | --- |
| **Core contracts & telemetry** | `PiSharp.Contracts`, `PiSharp.Telemetry` *(experimental)* | Wire/message/event DTOs, `pi-schema` JSON Schema validator (`PiSharp.Contracts.Schema`), and opt-in zero-export telemetry spans/metrics. |
| **Providers & media** | `PiSharp.AI`, `PiSharp.Extensions.AI` | Microsoft.Extensions.AI adapters, 10 chat wire APIs (Anthropic Messages, OpenAI Completions/Responses/Codex, Azure Responses, Bedrock, Vertex/Google Generative AI, Mistral, Copilot, Pi Messages), 43-provider catalog (1,853 entries in 1.6.0.2 and 1,858 in the 1.7.0 candidate), OAuth, Anthropic message threads, and `PiSharp.AI.Media` (images, video, speech, STT, embeddings, rerank, classifiers). |
| **Agent loop, sessions & tools** | `PiSharp.Agent`, `PiSharp.Sessions`, `PiSharp.Tools`, `PiSharp.Tools.Skia`, `PiSharp.Codemode`, `PiSharp.PromptTemplates.Yaml` | Turn state machine, parallel/sequential tool scheduling, steering/follow-up queues, JSONL v3 branching sessions & compaction, policy-gated built-in tools (`read`, `write`, `edit`, `bash`, `grep`, `find`, `ls`), SkiaSharp image codec, and Jint worker-isolated `codemode`. |
| **Coding agent & frontends** | `PiSharp.CodingAgent`, `PiSharp.Rpc`, `PiSharp.Tui`, `PiSharp.Cli` | Persistent coding-agent composition root, settings/skills/prompts/MCP orchestration, HTML export, LF-delimited JSONL RPC server, differential terminal UI (main & alt screen, overlays, Kitty/iTerm2 images, Mermaid/highlight.js), and the `pisharp` .NET tool. |
| **Extensions & Node bridge** | `PiSharp.Extensions.Abstractions`, `PiSharp.Extensions.Runtime`, `PiSharp.Extensions.Agent`, `PiSharp.ExtensionHost`, `PiSharp.Compatibility.Node` | Native C# `AssemblyLoadContext` plugin SDK, transactional discovery/reload, out-of-process worker supervision (`PiSharp.ExtensionHost`, fixed CLR identity `1.0.0.0`), and the optional Node.js bridge for unmodified Pi TypeScript extensions. |
| **Durable & Chord** *(experimental)* | `PiSharp.Durable`, `PiSharp.Durable.Tools`, `PiSharp.Durable.Sqlite`, `PiSharp.Chord`, `PiSharp.Chord.Hosting`, `PiSharp.Durable.Store` | Crash-resumable agent harness with signals, parked tools and hook tasks. SQLite, JSONL and Chord storage backends support persistence. The reactive Chord host supports native plugins, HTTP and workers. The candidate retains peer wire protocol v1. |
| **Remote environments** *(experimental)* | `PiSharp.Env.Protocol`, `PiSharp.Env`, `PiSharp.Env.Daemon` *(NativeAOT)* | Framed remote execution/filesystem/watch/SSH protocol and standalone NativeAOT remote daemon. |
| **Custom agents** *(experimental)* | `PiSharp.CustomAgents`, `PiSharp.CustomAgents.JavaScript`, `PiSharp.CustomAgents.Scripting` | Typed durable workflows, autonomous multi-agent teams with mailboxes/delegation, web search, and dynamic C#/JS/plugin-DLL tools. |
| **Source-only apps & tools** | `PiSharp.WebAgent`, `PiSharp.Durable.Web`, `PiSharp.Durable.Hosting`, `PiSharp.Durable.Server`, `PiSharp.Durable.Client`, `PiSharp.Durable.MSSQL`, `PiSharp.ModelRuntime`, `PiSharp.Web.Common`, `PiSharp.Evals` | Local web playground, durable device server/hub (hosted inside `pisharp durable serve`), SQL Server durable backend, and offline evaluation harness. |

## Core Design Invariants

- **Explicit state ownership & streaming:** Transport fragments, display-only partial tool arguments, and committed transcript/storage entries are strictly separated. No coordination lock is held across provider calls, tools, extension hooks, or IPC.
- **Policy-gated tools:** Built-in tools route through `IPreparedToolAdapter` → `IToolActionPolicy.AuthorizeAsync` after final argument resolution, with bounded output capture and owned child process-tree termination.
- **Upstream baseline discipline:** PiSharp pins an immutable upstream Pi commit in [compatibility/target.lock.json](../compatibility/target.lock.json) and tracks file-by-file applicability and acceptance separately from parity claims. Historical locks, fixtures, and release records remain preserved in place.
- **Zero default telemetry & attribution:** PiSharp sends no install telemetry or default provider attribution headers; `PiSharp.Telemetry` records spans only when explicitly supplied by the host.

## Related Guides

- [How PiSharp Works](https://pisharp.ai/docs/how-it-works/overview/) — architecture walkthroughs of the agent loop, tools, sessions, durability, workflows, and providers.
- [Agent Workflow Patterns](https://pisharp.ai/docs/patterns/) — recipes for approvals, error repair, steering, crash recovery, compaction, fan-out, and offline testing.
- [Repository Decisions](decisions/) — architecture decision records (`0000`–`0015+`).
- [Plans](plans/README.md) — parity and sync tracking ledgers.
