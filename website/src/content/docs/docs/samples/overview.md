---
title: How the samples work
description: How all 117 runnable PiSharp samples are organized, how to run them offline or on live providers, and which flagship walkthrough to start with.
---

PiSharp ships **117 runnable sample programs and micro-examples** right inside the repository under [`samples/`](https://github.com/jkelly/PiSharp/tree/main/samples). Every sample is designed to run **offline by default with zero API keys** on the .NET 10 SDK pinned in `global.json`, while letting you opt into live LLM providers, approval-gated mutations, or crash-injection tests explicitly.

Use the interactive [Samples Explorer](/samples/) to search and filter every sample by runtime, category, tool name, or workflow pattern — or start with one of the six annotated walkthroughs below.

## How `samples/` is organized

| Folder | What is inside | Runtime layer | Offline command |
| --- | --- | --- | --- |
| `samples/agents/` (`01`–`62`) | 62 standalone practical domain agents with shared host, fixture inputs and `--self-test` | `PiSharp.Agent` + `PiSharp.AI` | `dotnet run --project samples/agents/01-InboxTriage -- --demo` |
| `samples/CustomAgents.Examples` | 7-step hybrid durable workflow (`orders`) + 4-agent team (`team`) + web search (`web`) | `PiSharp.CustomAgents` *(experimental)* | `dotnet run --project samples/CustomAgents.Examples -c Release -- all` |
| `samples/CustomAgents.Research` | Parallel fan-out research team (`lead` + `N` researchers with `x_search` & `web_search`) | `PiSharp.CustomAgents` *(experimental)* | `dotnet run --project samples/CustomAgents.Research -c Release -- "AI coding agents"` |
| `samples/Durable.Examples` | 45 focused durable examples (`00–34` and `38–47` added in 1.6.0) | `PiSharp.Durable` *(experimental)* | `dotnet run --project samples/Durable.Examples -- --storage sqlite 00 12 26` |
| `samples/Durable.Chord.Examples` | 3 real-host SQLite examples (`35–37`) sharing Chord store, views & plugin registries | `PiSharp.Chord` + `PiSharp.Durable.Store` *(experimental)* | `dotnet run --project samples/Durable.Chord.Examples -- 35 36 37` |
| `samples/Durable.Chat` & `Durable.ReleaseWatcher` | Interactive durable chat host and policy/review device client | `PiSharp.Durable` *(experimental)* | `dotnet run --project samples/Durable.ReleaseWatcher -- demo` |
| `samples/extensions/` & `samples/chord/NativePlugins` | Native C# `IPiSharpExtension` (`StatefulTodo`, `SessionCheckpoint`) and Chord hot-reload plugin | `PiSharp.Extensions.Abstractions` / `Chord.Hosting` | `dotnet build samples/extensions/StatefulTodo` |

## Common flags across the 62 practical agents

All 62 projects in `samples/agents/PiSharp.Samples.slnx` share `samples/agents/Shared/SampleHost.cs`, so once you learn the flags on one sample, you know all 62:

| Flag | What it does |
| --- | --- |
| `--demo` *(default)* | Runs the real `PiSharp.Agent` loop and tool scheduler offline against `fixture.json`. Zero API keys or external writes. |
| `--approve` | Authorizes guarded local mutations (for example writing `tasks.json` or applying workspace fixes); omit it to verify denial. |
| `--self-test` | Runs deterministic tool & invariant tests for that sample. |
| `--input <path.json>` | Replaces `fixture.json` with your own JSON input and `task` prompt. |
| `--workspace <dir>` | Writes `report.json`, `transcript.json`, and sample artifacts to a chosen folder. |
| `--live --model <id>` | Calls a live provider using `OPENAI_API_KEY` (default Responses API), or `--provider anthropic --key-env ANTHROPIC_API_KEY` / `--provider openrouter --key-env OPENROUTER_API_KEY`. |
| `--image <file>` | Attaches a PNG, JPEG, or WebP image (up to 20 MiB) to a live multimodal request (see [16-ReceiptToExpense](/docs/samples/multimodal-receipt-and-studio/)). |
| `--resume <transcript.json>` | Replays a saved transcript with fresh tool declarations. |

Validate all 62 projects in one pass offline:

```powershell
dotnet build samples/agents/PiSharp.Samples.slnx -m:1
pwsh -File samples/agents/scripts/run-all.ps1
```

## Pick a flagship walkthrough

1. **[Approval task agent (`05-PersistentTaskManager`)](/docs/samples/persistent-task-manager/):** single-agent loop with `RequiresApproval` write gates and persisted local state.
2. **[Incident timeline builder (`45-IncidentTimelineBuilder`)](/docs/samples/incident-timeline-builder/):** deterministic evidence collection, UTC deduplication, preserved contradictions, and stale-state guards.
3. **[Vision receipts & media studio (`16` & `27`)](/docs/samples/multimodal-receipt-and-studio/):** multimodal image attachments, integer-cent invariants, and opt-in image generation.
4. **[Hybrid orders workflow & team (`CustomAgents.Examples`)](/docs/samples/hybrid-orders-workflow/):** 7-step C# + prompt + JS + Roslyn + agent workflow with real `--crash-in` process-kill resume.
5. **[Parallel research team (`CustomAgents.Research`)](/docs/samples/parallel-research-team/):** multi-agent `delegate` fan-out combining X search and web search with SQLite resume.
6. **[Durable (00–47), Chord & C# extensions](/docs/samples/durable-and-extensions/):** complete index of all 48 Durable/Chord micro-examples plus branch-aware native C# extensions.
