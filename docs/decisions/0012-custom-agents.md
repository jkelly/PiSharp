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
   - `samples/CustomAgents.Examples` runs the orders workflow and a team demo on scripted models, or on a real provider with `--provider` and `--model`. Its `--crash-in` flag shows a run resuming from SQLite.
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
5. **Messaging and subagents.**
   - The extension `agents.messaging` adds `list_agents`, `send_message`, `ask_agent` and `reply`.
   - The extension `agents.orchestration` adds `delegate`, `spawn`, `wait_agent` and `stop_agent`. It is withheld when the definition allows no subagents, or when the depth limit is reached.
   - The tools are replay-safe and idempotent per tool task.
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
