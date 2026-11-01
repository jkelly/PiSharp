# CustomAgents.Research

A research team built with `PiSharp.CustomAgents` ([decision 0012](../../docs/decisions/0012-custom-agents.md)). You give it a topic; it writes a Markdown report.

1. A **lead** agent splits the topic into parts (4 by default).
2. It gives each part to its own **researcher** subagent with `delegate`. All the calls go out in one turn, so the researchers work in parallel.
3. Each researcher searches **posts on X** first, with `x_search` (xAI's X search). It then uses `web_search` to check facts and find primary sources.
4. Each researcher answers with findings, what people on X are saying, and open questions, and links every claim.
5. The lead merges the answers into one report: a summary, one section per part, "What people on X are saying", open questions and the sources. The program writes the report to a `.md` file.

## Run it

Offline, on scripted models and made-up search results:

```bash
dotnet run --project samples/CustomAgents.Research -c Release -- "How developers use AI coding agents in 2026"
```

With real models: the agents and X search on Grok, and the web search through the first backend whose key is set:

```bash
dotnet run --project samples/CustomAgents.Research -c Release -- "Humanoid robots in 2026" --provider xai --model grok-4.7 --days 90
```

| Option | Meaning |
|---|---|
| `"<topic>"` | What to research. |
| `--provider`, `--model` | The agents' model. Its key comes from the provider's variable, for example `XAI_API_KEY`. Without them, the models are scripted. |
| `--parts <2-8>` | How many researchers the topic is split between. |
| `--out <file.md>` | Where the report goes. The default is the topic's words plus `.md`, in the current folder. |
| `--dir <path>` | Where each topic's SQLite storage is kept (default `./research-runs`). |
| `--fresh` | Discards the topic's storage and researches the topic again. |
| `--web <backend>` | The web search: `brave`, `tavily`, `firecrawl`, `openai`, `anthropic`, `xai`, `openrouter` or `none`. The default is the first whose key is set. |
| `--x-model <id>` | The Grok model that runs X search (default `grok-4.7`). |
| `--days <n>` | X search keeps only posts from the last `n` days. |

X search needs `XAI_API_KEY`. Without it, the researchers use only the web.

Each X search is a Grok request, plus xAI's fee per post fetched. A web search costs whatever its backend charges. A run with 4 researchers made about 50 searches and 20 model requests.

## Durability

Each topic has its own SQLite file under `--dir`, and the question has a fixed request ID:
- **A run that stops** (a crash, or Ctrl+C) resumes where it left off when you run the same command again. Researchers that already answered are not asked again.
- **A finished run** returns its recorded report without new requests. Use `--fresh` to research the topic again.

## Things to know

- **Untrusted content:** search results are untrusted. The tools and instructions tell the models to use them as information, never as instructions.
- **Check the report:** it is the models' work. Check its figures and links before you rely on them.
- **Rate limits:** a web backend may limit how many searches it takes per minute; Firecrawl's plan used here allowed about 10. A rate-limited search waits and retries up to three times, so a large `--parts` can make a run slower.
