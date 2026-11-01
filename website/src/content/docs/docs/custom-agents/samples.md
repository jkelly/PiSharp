---
title: Custom agent samples
description: The Examples and Research samples, what they show and how to run them. Experimental and unreleased.
sidebar:
  badge:
    text: Unreleased
    variant: caution
---

:::caution[Experimental and unreleased]
The custom agents library is on PiSharp's `main` branch but not in any release or NuGet package yet. To use it, build PiSharp from
source and reference the projects directly (see [Build from source](/docs/contribute/build/)). Its API is marked
`[Experimental("PISHARP_AGENTS001")]` and builds on durable APIs marked `[Experimental("PISHARP_DURABLE001")]`, so suppress `PISHARP_AGENTS001` and `PISHARP_DURABLE001` in your project to compile it, and it may change or be removed without notice.
:::

The repository has two sample programs for the custom agents library. [CustomAgents.Examples](https://github.com/jkelly/PiSharp/tree/main/samples/CustomAgents.Examples) runs the library's features one by one: a workflow, a team with every kind of tool, and web search. [CustomAgents.Research](https://github.com/jkelly/PiSharp/tree/main/samples/CustomAgents.Research) is a research team that writes a Markdown report.

Both run offline by default, on scripted models (`FauxAgentModels`) and canned search results, so you can try them without API keys. With `--provider` and `--model`, a real model answers instead. Run the commands from the root of a PiSharp clone with the .NET 10 SDK. See [Build from source](/docs/contribute/build/).

## CustomAgents.Examples

The program has three commands, and `all` (the default) runs them in order:

| Command | What it runs |
| --- | --- |
| `orders` | The orders [workflow](/docs/custom-agents/workflows/) for orders 42 (a VIP customer) and 43. |
| `team` | Asks the lead agent a customer question that it answers with its tools, a subagent and the orders workflow. |
| `web` | Asks the lead a question that its scout subagent answers with `web_search`. |
| `all` | All three. |

### The orders workflow

Each step of the workflow uses a different kind of action, and all of them change the same `OrderData`:

| Step | Kind | What it does |
| --- | --- | --- |
| `load` | An `IAction<OrderData>` class | Loads the order and its customer from an in-memory store. Fails the run for an unknown order. |
| `summarize` | Code that calls `ActionContext.PromptAsync` | Asks the model for a one-sentence summary. The answer is recorded, so a rerun after a crash reuses it. |
| `risk` | `Prompt<RiskVerdict>` | A typed model call: the model answers through `submit_result`, validated against `RiskVerdict`. |
| `discount` | `Script` (JavaScript) | Returns a merge patch with the discount and the amount to pay. |
| `priority` | `CSharp` (a C# script) | Sets the priority, and stops the run for manual review when the risk is high. |
| `vip-note` | `Agent` | A VIP order goes to the `concierge` agent, which writes a note. Then the run ends. |
| `thank-you` | `Prompt` | Any other order gets a one-line thank-you. |

Each order runs with the run key `order-<id>`, so running the command again attaches to the earlier run instead of starting another.

### The team

| Agent | Tools |
| --- | --- |
| `lead` (the orchestrator) | `lookup_order` (a typed C# tool), `shipping_quote` (a pure JavaScript tool), `run_workflow_orders` and `workflow_status` (the orders workflow as tools), and `delegate` to the researcher, and to the scout when web search is on. |
| `researcher` | `check_stock`, a C# script tool. |
| `concierge` | None. The workflow's `vip-note` step runs it. |
| `scout` | `web_search`, with 4 results by default. Registered only when a search backend is available. |

Each agent sees only the tools its definition selects. See [Agent teams](/docs/custom-agents/teams/) and [Agent tools](/docs/custom-agents/tools/).

### Run it

Offline, on scripted models:

```powershell
dotnet run --project samples/CustomAgents.Examples -c Release
dotnet run --project samples/CustomAgents.Examples -c Release -- orders
```

On a real provider. The key comes from the provider's variable, for example `OPENAI_API_KEY` or `XAI_API_KEY`:

```powershell
dotnet run --project samples/CustomAgents.Examples -c Release -- --provider openai --model gpt-5.4-mini --dir ./demo-openai
dotnet run --project samples/CustomAgents.Examples -c Release -- team --provider xai --model grok-4.7 --dir ./demo-xai
```

Web search through a backend you choose, with its key set. The `--search-model` flag picks the model that searches, for the model-based backends:

```powershell
dotnet run --project samples/CustomAgents.Examples -c Release -- web --provider openai --model gpt-5.4-mini --search firecrawl
dotnet run --project samples/CustomAgents.Examples -c Release -- web --provider openai --model gpt-5.4-mini --search openrouter --search-model anthropic/claude-sonnet-5.5
```

Without `--search`, the scout searches canned results on scripted models, and on a real provider it uses the first backend whose key is set. With `--search` on scripted models, the scripted agents search the real backend. When no backend is available, the `web` command prints why and is skipped.

### Crash and resume

`--crash-in load` or `--crash-in summarize` exits the process (exit code 3) inside that step of the orders workflow, after the step did its work and before it committed. For `summarize`, that is after the model answered. Run the same command again, and the run resumes at that step:

```powershell
dotnet run --project samples/CustomAgents.Examples -c Release -- orders --crash-in summarize
dotnet run --project samples/CustomAgents.Examples -c Release -- orders --crash-in summarize
```

The second process attaches to the run, reruns `summarize`, reuses the recorded answer instead of asking the model again, and finishes. The sample writes a `crashed-in-<step>` file next to the storage so it crashes only once. To see the crash again, delete that file or use a new `--dir`.

### Options

| Option | Meaning |
| --- | --- |
| `orders`, `team`, `web`, `all` | The command. Default `all`. |
| `--provider <id>`, `--model <id>` | A real provider and model. Give both or neither. Default: scripted models. |
| `--thinking <level>` | The agents' thinking level. |
| `--dir <path>` | Where the SQLite storage, `agents.sqlite`, is kept. Default `./custom-agents-demo`. |
| `--crash-in load\|summarize` | Exits inside that step of the orders workflow. |
| `--search <backend>` | The web search backend: `brave`, `tavily`, `firecrawl`, `openai`, `anthropic`, `xai` or `openrouter`. |
| `--search-model <id>` | The model that searches. Only with `--search openai`, `anthropic`, `xai` or `openrouter`. Default: the backend's own. |

The backends' key variables are listed under [Web search](/docs/custom-agents/tools/#backends).

### Output and exit codes

The program prints the models, the storage path and the search backend first. Then each command prints what it did: the workflow's history, log and data, the team's answer and the agents it started, and the web answer. The last line counts the model requests this process sent.

| Exit code | When |
| --- | --- |
| 0 | Every command finished. Also when the provider's key variable isn't set: the program prints `skipped: <variable> is not set`. |
| 1 | The team or web question failed. |
| 2 | Invalid arguments (the program prints its usage), an unknown model, or a model with no durable route. |
| 3 | `--crash-in` exited the process. |

### Storage

Every process opens the team over the same SQLite file and resumes what an earlier process left. The workflow runs are keyed by order, and the team and web questions have fixed request IDs, so a second run of the same command returns the recorded results without new model requests. Results recorded on scripted models stay in the file too: use a separate `--dir` for each provider, or delete the folder to start over.

## CustomAgents.Research

You give the program a topic, and a team writes a Markdown report on it:

1. The `lead` agent splits the topic into parts, 4 by default.
2. It gives each part to its own `researcher` subagent with `delegate`, all in one turn, so the researchers work in parallel.
3. Each researcher searches posts on X with `x_search` first, then checks facts with `web_search`.
4. Each researcher answers with findings, what people on X say, and open questions, with a link for every claim.
5. The lead merges the answers into one report: a summary, a section per part, what people on X are saying, open questions and the sources. The program writes it to a file.

`x_search` is a `web_search` tool over `XaiWebSearch` with `Sources = XaiSearchSources.X`. See [X search](/docs/custom-agents/tools/#x-search).

### Run it

Offline, on scripted models and made-up search results:

```powershell
dotnet run --project samples/CustomAgents.Research -c Release -- "How developers use AI coding agents in 2026"
```

With all agents and X search on Grok, posts from the last 90 days, and web search through the first backend whose key is set:

```powershell
dotnet run --project samples/CustomAgents.Research -c Release -- "Humanoid robots in 2026" --provider xai --model grok-4.7 --days 90
```

### Options

| Option | Meaning |
| --- | --- |
| `"<topic>"` | What to research. Default `"How developers use AI coding agents in 2026"`. |
| `--provider <id>`, `--model <id>` | The agents' provider and model. Give both or neither. Default: scripted models and canned searches. |
| `--thinking <level>` | The agents' thinking level. |
| `--parts <2-8>` | How many researchers the lead splits the topic between. Default 4. |
| `--out <file.md>` | The report file. Default: the topic's words in lower case, joined by hyphens, plus `.md`, in the current folder. |
| `--dir <path>` | Where each topic's SQLite storage is kept. Default `./research-runs`. |
| `--fresh` | Deletes the topic's storage and researches the topic again. |
| `--web <backend>\|none` | The web search backend: `brave`, `tavily`, `firecrawl`, `openai`, `anthropic`, `xai`, `openrouter`, or `none` for X search only. Default: the first whose key is set. |
| `--x-model <id>` | The Grok model that runs X search. Default `grok-4.7`. |
| `--days <n>` | X search keeps only posts from the last `n` days. At least 1. |

### Keys

With `--provider`, the program needs three kinds of keys:

- The provider's key, for example `XAI_API_KEY` for `--provider xai`. Without it, the program prints `<variable> is not set.` and exits with 1.
- `XAI_API_KEY` for X search, whatever the agents' provider. Without it, the researchers search only the web.
- A web search backend's key, as listed under [Web search](/docs/custom-agents/tools/#backends). Without one, or with `--web none`, the researchers search only X.

With neither X nor web search, the program prints "Nothing to search with: set XAI_API_KEY for X search, or a web search key." and exits with 1. On scripted models, `--web`, `--x-model` and `--days` have no effect.

### Storage and --fresh

Each topic has its own SQLite file under `--dir`, named after the topic's words and the first 8 hex digits of the topic's SHA-256. The question has the fixed request ID `research`.

- A run that stops, from a crash or Ctrl+C, resumes when you run the same command again. Researchers that already answered aren't asked again.
- A finished run returns its recorded report without new model requests, and the program prints "(This topic was researched earlier; --fresh researches it again.)".

The file depends only on the topic. Changing `--parts`, `--days`, `--provider` or `--web` for a topic you already researched returns the old report, so add `--fresh` to research it again with the new options. `--fresh` deletes the `.sqlite` file and its `-wal` and `-shm` files.

### Output and exit codes

The program prints the topic, the models, the X and web search backends and the storage path, then each search a researcher runs. At the end it prints the report's path, its line and section counts, the number of researchers, the model requests this process sent and the time taken.

| Exit code | When |
| --- | --- |
| 0 | The report was written. |
| 1 | The provider's key isn't set, there is nothing to search with, the question failed, or the lead's answer doesn't start with a `# ` title. |
| 2 | Invalid arguments (the program prints its usage), an unknown model, or a model with no durable route. |

### Cost and rate limits

Each X search is a Grok request plus xAI's fee per post fetched. A web search costs whatever its backend charges. The sample's README reports that a run with 4 researchers made about 50 searches and 20 model requests.

A web backend may limit searches per minute. The README notes that the Firecrawl plan it used allowed about 10. A rate-limited search waits and retries up to three times, so a large `--parts` can make a run slower. The report is the models' work: check its figures and links before you rely on them.

## In CI

The [Custom agents workflow](https://github.com/jkelly/PiSharp/blob/main/.github/workflows/custom-agents.yml) runs the custom agents tests on Windows, Linux and macOS. On each it also runs CustomAgents.Research on scripted models with `--parts 3` and checks that the report starts with a `# ` title. CI doesn't run CustomAgents.Examples. See [Testing](/docs/contribute/testing/) for running the tests yourself.
