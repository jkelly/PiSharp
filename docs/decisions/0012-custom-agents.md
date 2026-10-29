# Custom agents, workflows and subagents on the durable harness

Status: approved by the project owner on 2026-10-27, on branch `feature/custom-agents` only. Not released: the projects are not packable, and every public type carries `[Experimental("PISHARP_AGENTS001")]`.

## Context

The owner wants C# developers to build custom agents in code:
- agents whose tools are added and removed while they run, including tools written in JavaScript, C# scripts and plugin DLLs;
- workflows: a series of actions, run by one engine through `IAction<TData>.ExecuteAsync(data, context, token)` over one shared `Data` object per workflow. An action is a model call, or code that fetches data and decides what happens next;
- subagents that talk to the orchestrator agent and to each other.

The durable harness of [decision 0005](0005-pi-durable-experimental.md) already has the pieces: tasks, phases and checkpoints, conversations and their inbox, documents, extensions and the codemode sandbox. It has no front door for these use cases. This library is that front door. It is PiSharp's own; upstream Pi has no counterpart, so there is nothing to port or keep in parity.

The owner chose (2026-10-27):
- the durable harness as the runtime (crash-safe resume, messages delivered exactly once, SQLite storage);
- one shared Data class per workflow, passed to every action;
- JavaScript (Jint), C# scripts (Roslyn) and plugin DLLs as the dynamic tool sources, besides tools written in C#.

## Decision

1. **Projects.**
   - `PiSharp.CustomAgents` holds the workflow engine, agents and teams, typed tools and tool sources, messaging, subagents, the plugin loader with its `IPiSharpExtension` adapter, and `Testing.FauxAgentModels`. It references `PiSharp.Durable` and `PiSharp.Extensions.Abstractions` only, and adds no third-party code.
   - `PiSharp.CustomAgents.JavaScript` holds JavaScript tools and workflow actions on `PiSharp.Codemode`'s Jint sandbox and `PiSharp.Durable.Tools`' durable codemode.
   - `PiSharp.CustomAgents.Scripting` holds C# scripts, compiled with `Microsoft.CodeAnalysis.CSharp` 5.6.0 (see point 9).
   - `tests/PiSharp.CustomAgents.Tests` is a console case-table runner like `tests/PiSharp.Durable.Tests` (`--only <prefix>`, `--report <path>`, crash children). Three fixture projects supply plugins.
   - `samples/CustomAgents.Examples` runs the orders workflow, a team demo and a web search demo on scripted models, or on a real provider with `--provider` and `--model`. Its `--crash-in` flag shows a run resuming from SQLite.
   - `samples/CustomAgents.Research` is a research team:
     - A lead agent splits a topic into parts and delegates each part to its own researcher subagent. The calls are made in one turn, so the researchers run in parallel.
     - The researchers search posts on X with `x_search` first, then the web with `web_search`.
     - The lead merges their findings into one Markdown report, and the program writes it to a file.
2. **No changes to `PiSharp.Durable`.** Everything is built on its public API. Where that API falls short, the library works around it (point 8) instead of changing the harness.
3. **Workflows.**
   - `Workflow.For<TData>(name)` builds a workflow. Its steps are:
     - code actions: an `IAction<TData>` class, or a delegate;
     - model calls: `Prompt`, and `Prompt<TResult>` for typed structured output;
     - `Agent`: runs a team agent;
     - routers: `Branch` and `Switch`, pure functions of the Data that are never checkpointed;
     - composition: `Run` (a sub-workflow), `Parallel`, `ForEach` and `WaitForSignal`;
     - from the satellite projects: `Script` (JavaScript) and `CSharp` (a C# script).
   - An action returns `ActionResult.Next`, `GoTo`, `Stop`, `Fail`, `Repeat` or `Sleep`.
     - A `Sleep` holds whatever comes next until it wakes: an action, a composite step, or the end of the run.
   - `ForEach` runs its items in batches of `Concurrency`. With `JoinPolicy.FailFast` (the default), the first failed item ends the step and aborts the items still running, as `Parallel` does.
   - A workflow compiles to one durable task kind, `pisharp.workflow.<name>`, with one phase per action.
   - The Data and the next action are committed in one transaction. That gives these replay rules, which are documented for users:
     - The Data and the choice of next action are committed exactly once.
     - A code action runs at least once.
     - `OnceAsync` records a result once.
     - A model call is asked once per attempt, across restarts: a deterministic request ID returns the recorded submission.
   - The Data travels as JSON through `DataCodec`. It refuses numbers that a double cannot hold exactly, and Data over the size limit.
   - Renaming or removing an action needs a `Version` bump and a `WorkflowMigration`. An unknown phase fails the run with a clear message instead of faulting the task.
   - A team's `WorkflowCatalog` holds its workflows. `session.Workflows` (a `WorkflowRunner`) starts runs, finds them by run key, and attaches to and lists them. A `WorkflowRun<TData>` waits for a run, reports its status and history, and can abort or signal it.
4. **Agents and teams.**
   - An `AgentDefinition` names an agent's model, instructions, sections, tools, tool sources, extensions, hooks, allowed subagents and permissions.
   - `AgentTeam.OpenAsync(storage)` opens the Harness with no extension selected by default. It roots the orchestrator, reinstalls what earlier sessions created, and only then resumes pending work.
   - Each definition is installed as the extension `agent.<name>`. A conversation selects only its own definition's tools. Children never inherit their parent's tools.
   - `AgentTool.Create<TArgs>` builds a typed tool whose schema comes from `TArgs`.
   - A `DynamicToolSource` adds, removes and replaces tools while agents run.
   - Duplicate tool names across selected sources are refused, because the Harness would otherwise silently keep the last one.
     - A source change and a catalog check both take one shared lock first. A change checks the catalog while a registration reads the sources, so without it the two could deadlock.
   - `WebSearchTool.Create(provider)` builds a replay-safe `web_search` tool, with the arguments `query` and optional `count`:
     - The Harness offers the model only tools that the host runs, so a provider's hosted search (OpenAI's `web_search`, Anthropic's web search, Gemini's `googleSearch`) cannot be switched on for an agent's model. This tool calls a search backend instead.
     - The tool is independent of the agent's own model: an agent on any provider can use any backend.
     - Three backends are search APIs: `BraveWebSearch` (`BRAVE_API_KEY`), `TavilyWebSearch` (`TAVILY_API_KEY`) and `FirecrawlWebSearch` (`FireCrawl_API_KEY`, Firecrawl's v2 search, which can add news results; the pages are not scraped).
     - Four backends ask a model to run its provider's hosted search, and return the model's reply, the pages it cited, then the other pages it consulted. Every search is a paid model request:
       - `OpenAIWebSearch` (`OPENAI_API_KEY`, default `gpt-5.4-mini`) and `XaiWebSearch` (`XAI_API_KEY`, default `grok-4.7`) use the Responses API's `web_search` tool. xAI's Responses API has OpenAI's shape, so they share one parser.
       - `XaiWebSearch` can also search posts on X with xAI's `x_search` tool. Its `Sources` is `Web` (the default), `X` or `WebAndX`.
         - For X it takes a date range (`XFromDate`, `XToDate`) and either the accounts to keep (`XHandles`) or the accounts to leave out (`XExcludedHandles`).
         - X posts come back as `x.com/i/status/…` links, from the reply's citations and the response's `citations` list.
         - A citation number that xAI gives as a title is replaced by the host name.
       - `AnthropicWebSearch` (`ANTHROPIC_API_KEY`, default `claude-opus-5-5`) uses the Messages API's `web_search` server tool, with `anthropic-version: 2026-10-21`. It sends `web_search_20260209` on the models with dynamic filtering and `web_search_20250305` on older ones, low effort where the model takes it, and the server-side refusal fallback (`fallbacks: "default"`) on Fable 5.1, Opus 5.5, Opus 5 and Sonnet 5.5. It resumes a `pause_turn` up to three times, and reports a refusal or a server tool error such as `max_uses_exceeded` as a failed search.
       - `OpenRouterWebSearch` (`OPENROUTER_API_KEY`, default `openai/gpt-5.4-mini`) uses OpenRouter's web plugin on any model OpenRouter routes to. Without an `Engine`, OpenRouter uses the model provider's native search where it has one (Anthropic's, for a Claude model), else Exa.
     - `WebSearchTool.ProviderFromEnvironment` picks the first backend whose key is set, in the order of `WebSearchTool.Backends` (Brave, Tavily, Firecrawl, OpenAI, Anthropic, xAI, OpenRouter), or the one it is given by name. A host can implement `IWebSearchProvider` for any other search service.
     - The model sees a numbered list of results, marked as untrusted content. Programs that call the tool get a `WebSearchResponse`.
     - A count over `MaxCount`, a failed search and a timeout are error results. Errors name the HTTP status and the key's variable, never the key.
     - A rate-limited search (HTTP 429) waits for the backend's `Retry-After`, or 10 seconds without one, and retries up to three times within the tool's timeout.
     - The result header reads `Search for "…" (backend)`, so the same tool can serve X posts, for example as `x_search`.
5. **Messaging and subagents.**
   - The extension `agents.messaging` adds `list_agents`, `send_message`, `ask_agent` and `reply`.
   - The extension `agents.orchestration` adds `delegate`, `spawn`, `wait_agent` and `stop_agent`. It is withheld when the definition allows no subagents, or when the depth limit is reached.
   - The tools are replay-safe and idempotent per tool task.
   - An agent that a workflow runs (an `Agent` step or `RunAgentAsync`) is in the directory while its call runs, as `<definition>#<n>` of kind `Workflow`, at depth 0. Its messaging tools work, and so do its `delegate` and `spawn` when its definition names subagents. The entry closes when the call ends.
   - The directory (`pisharp.agents.directory`) records every agent, its parent and the wait-for edges of blocking calls. A call that would close a cycle is refused with guidance. A question from a child to a parent that is waiting on it goes to the waiting call, which returns early.
   - `ask_agent("host")` reaches the program through `session.Host`. A definition must opt in with `CanAskHost`.
   - Two background tasks keep this durable:
     - `agents.anchor` aborts a child when its delegate call ends in failure;
     - `agents.reporter` delivers a background child's answer exactly once.
6. **Dynamic tool sources.**
   - **JavaScript.**
     - `JsTool` runs in the Jint sandbox, in one of two modes:
       - Pure mode has no host access;
       - Durable mode may call the tools on an allowlist, journalled so that a restart replays them.
     - `JsToolSource` holds a set of JavaScript tools.
     - `Script` actions return a JSON merge patch of the Data, and optionally the next action.
     - `JsAuthoring.Enable` adds `create_tool`/`remove_tool` for agents whose definition sets `CanCreateTools`. It is off by default. It requires worker isolation (`JsWorkerHost` in the host's `Main`). It applies name, size and count limits, and persists created tools in the session.
   - **C# scripts.**
     - `CSharpScriptHost` compiles a script into a generated class, mapping `#line` so diagnostics name the script's own lines.
     - `#r`, `#load` and type declarations are refused.
     - Each compile loads into its own collectible load context, cached in memory and optionally on disk by the SHA-256 of everything that affects the output. `TrustedSourceHashes` can pin the scripts allowed to compile.
     - `ScriptTool` builds tools from scripts, and the `CSharp` action changes the typed Data.
     - `ScriptToolSource` watches a folder of `*.tool.csx` files with a `// @tool:` JSON header. It keeps the last good tool on a broken edit, and unloads a replaced script once its calls drain.
     - Scripts are never model-authored.
   - **Plugin DLLs.**
     - `AgentPluginLoader` loads a folder with a `pisharp-agents-plugin.json` manifest (contract `pisharp.agents/1`) into a collectible load context, sharing the contract assemblies.
     - Loading is default-deny: only paths in `Trust` load, optionally pinned by SHA-256. A failed load rolls back.
     - A hot reload swaps the plugin's tools in place and drains running calls before unloading.
     - `PiSharpExtensionAdapter` maps tools registered through `PiSharp.Extensions.Abstractions` onto durable tools.
7. **Workflows as tools.**
   - `WorkflowTools.RunTool(catalog, workflow)` gives an agent `run_workflow_<name>`. Its arguments follow the Data's schema.
   - The overload `RunTool(catalog, workflow, (TInput input) => data)` takes a smaller input type instead, so the model fills only what a run starts from.
   - The run starts exactly once: a receipt on the tool task is written in the same commit as the run. The call waits up to its timeout, then returns the run's task ID.
   - `WorkflowTools.StatusTool()` adds `workflow_status`, which reports any run.
8. **Durable API limits and their workarounds.**

   | Limit | Workaround |
   |---|---|
   | Submissions cannot be admitted inside a transaction | Two commits with deterministic request IDs |
   | A child cannot reach a parent blocked in a foreground call | The supervising anchor task, interruptible waits, and routing through the mailbox to the waiting call |
   | No response-format option on `IDurableModels` | A terminating `submit_result` tool whose arguments the Harness validates against the result type's schema |
   | Every installed extension is selected by default | Team settings with no extensions, plus explicit selections |
   | Duplicate tool names: the last one wins | A conflict check in the catalog |
   | Task docs retire when the task settles | Run state lives in a conversation doc family |
   | An unknown phase faults the task | A phase table whose fallback fails the run with guidance |
   | Chord numbers are doubles | The precision guard in `DataCodec` |
   | `MemoryStorage` cannot reopen | Restart tests use SQLite |

9. **Third-party code.**
   - Roslyn: `Microsoft.CodeAnalysis.CSharp` and `.Common` 5.6.0, MIT.
     - 5.6.0 is the newest 5.x with a .NET 10 target and stable dependencies; 5.9.0 depends on a prerelease analyzers package.
     - The `Microsoft.CodeAnalysis.Analyzers` dependency is excluded from the build, so its analyzers cannot break `TreatWarningsAsErrors`.
     - The license is in `third-party/Roslyn.LICENSE.txt`, with a section in `THIRD-PARTY-NOTICES.md`.
   - The JavaScript project uses the Jint that `PiSharp.Codemode` already ships.
10. **Security.**
    - C# tools, plugins and C# scripts run with full trust. Load contexts and reference lists are not sandboxes.
    - JavaScript reaches only the tools on its allowlist, within the sandbox's time, memory and recursion limits. Model-authored JavaScript requires a worker process.
    - Messages between agents are untrusted input. The default messaging scope is the agent's family.
    - Web search results are untrusted content too, and the `web_search` result tells the model so.
    - Depth, children per agent, total agents and timeouts cap the cost.
11. **Packaging is deferred.** The projects stay out of `tools/release/PiSharp.NativeSdk.targets` and `build-release-packages.ps1`, and `CHANGELOG.md` gets no entry, until the owner decides to release them. `CHANGELOG.md` is generated from the website's release entries.

## Not done yet

- Test cases from the plan that are not written yet:
  - `agents.msg.send-busy-followup-vs-steer`;
  - `agents.ask.restart-mid-wait-reattaches`;
  - `agents.sub.delegate-rerun-after-close-same-child`;
  - the end-to-end cases `agents.e2e.team-scenario` and `agents.e2e.crash-kill-child`.
- The directory sweeper at `OpenAsync`. The anchor task covers its purpose; the sweeper was planned only as a backstop.
- `AgentToolContext.Messages`. Tools reach messaging through `AgentMessaging.ForTool`.

## Known limits

- The OpenAI Responses transcript projection (`ResponsesTranscriptProjector` in `PiSharp.AI`) accepts only safe integers in tool-call arguments. A model that passes `2.5` to a tool fails its next request with "Responses argument number is outside the supported projection profile." This is outside this library. The sample keeps its tool arguments integral and gives the workflow tool a small input type.
- Real-model runs depend on the model following tool schemas. The sample's lead agent is free to call tools in any order.

## Verification

- `dotnet run --project tests/PiSharp.CustomAgents.Tests -c Release` runs every case; `--only agents.js` and similar prefixes run a subset. The `Custom agents` workflow (`.github/workflows/custom-agents.yml`) runs them on Windows, Linux and macOS.
- `samples/CustomAgents.Examples` was run on scripted models and against `openai/gpt-5.4-mini` on 2026-10-27:
  - the first process exited inside `summarize` after the model answered;
  - the second attached to the same run, reused the recorded answer, and finished both orders, the team question, the subagent delegation and the workflow tool.
- The web search demo (`web --provider openai --model gpt-5.4-mini`) was run on 2026-10-28 with OpenAI's hosted search. The lead delegated the question to the scout, the scout searched once, and the lead answered from the scout's result.
- On 2026-10-28 the web search demo also ran with the agents and the search both on xAI (`grok-4.7`), and with the agents on `anthropic/claude-sonnet-5.5` through OpenRouter, searching with OpenRouter's web plugin on `openai/gpt-5.4-mini` and on `anthropic/claude-sonnet-5.5` (Anthropic's native search). The xAI and OpenRouter test fixtures follow the replies those live requests returned.
- On 2026-10-28 the demo also ran with Anthropic's search on `claude-opus-5-5`, with the agents on `openai/gpt-5.4-mini` and on `anthropic/claude-opus-5-5`. Each time the scout searched once and the lead answered from what it found.
- On 2026-10-28 the demo ran three times with Firecrawl's search, with the agents on `openai/gpt-5.4-mini`. Each time the scout searched once (5 results) and the lead answered from what it found.
- In 3 of the 18 live runs, the lead (`gpt-5.4-mini`) did not delegate. It called `ask_agent("scout")` before the scout was running. The tool refused with "There is no agent 'scout'", `list_agents` listed no agents, and the lead answered from memory.
  - That refusal now tells an agent to start a subagent from its `Subagents` list with `delegate` or `spawn`, when it has those tools.
  - `ask_agent` and `send_message` now say they reach running agents only.
  - The `agents.msg.scope-refuses` case checks the new refusal. No live run has hit it since, so a model recovering from it has not been seen.
- On 2026-10-28 `samples/CustomAgents.Research` ran twice with all agents on `xai/grok-4.7`, X search on `grok-4.7` and Firecrawl for the web, splitting each topic between 4 researchers:
  - "How developers use AI coding agents in 2026": 116 s, 68 links in the report, 52 of them X posts.
    - Firecrawl's per-minute limit refused 11 of its web searches with HTTP 429. The researchers continued with the rest.
    - The 429 retry was added after this run.
  - "Humanoid robots in homes and factories": 254 s, 22 X searches and 29 web searches with no errors, 93 links, 56 of them X posts.
  - The figures and links in the reports are the models' work and were not checked against their sources.
- The `agents.web` cases test the Brave and Tavily backends against request and response shapes taken from those APIs' documentation. No Brave or Tavily API key was available, so neither has been run against its live service.
