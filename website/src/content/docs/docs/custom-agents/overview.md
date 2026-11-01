---
title: Custom agents
description: Build your own crash-safe agents, workflows and subagent teams in C#. Experimental and unreleased.
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

`PiSharp.CustomAgents` is a C# library for building your own agents on [Pi Durable](/docs/guides/durable/), PiSharp's port of Pi's crash-safe agent harness. You define agents, their tools and multi-step workflows in code. The durable harness runs them and commits every step to storage, so a run that a crash or restart interrupts resumes where it stopped.

The library is PiSharp's own. Pi v1.2.0 has no equivalent package, though it runs on the same durable harness design. [Decision 0012](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0012-custom-agents.md) records the design.

## When to use it

PiSharp gives you three ways to run an agent from C#:

| Library | What it gives you | Use it when |
| --- | --- | --- |
| [`PiSharp.Agent`](/docs/embed/agent/) | A tool-calling loop in memory, with your own tools, hooks and events. | You want one agent in your process, and losing a turn when the process stops is acceptable. |
| [`PiSharp.Durable`](/docs/embed/durable/) | The durable harness itself: conversations, tasks, extensions, tools and storage. | You want full control over a crash-safe agent and are willing to work with its lower-level types. |
| `PiSharp.CustomAgents` | Typed workflows, agent teams with subagents, typed and scripted tools, plugins and web search, on the durable harness. | You want crash-safe agents or multi-step processes without writing durable tasks and extensions by hand. |

Custom agents are durable conversations and tasks underneath. Anything the [durable guide](/docs/guides/durable/) says about turns, tools and subagents applies here too.

## The pieces

- **[Workflows](/docs/custom-agents/workflows/)** are a series of named actions over one shared data class. An action is C# code, a model prompt with typed output, a run of an agent, a JavaScript or C# script, or a composite step that runs sub-workflows. The engine commits the data and the next action together after each action.
- **[Teams](/docs/custom-agents/teams/)** are agent definitions (name, model, instructions, tools, subagents) opened over one storage. An orchestrator delegates to subagents, and agents message each other through a mailbox.
- **[Tools](/docs/custom-agents/tools/)** are typed C# handlers, JavaScript in a sandbox, C# scripts, plugin DLLs or native PiSharp extensions. Tool sources let tools change while agents run. A ready-made `web_search` tool searches through seven backends, and xAI's backend also searches posts on X.
- **[Samples](/docs/custom-agents/samples/)** show all of it on scripted models or a real provider.

The code lives in three projects:

| Project | Namespaces | Holds |
| --- | --- | --- |
| `PiSharp.CustomAgents` | `PiSharp.CustomAgents.Workflows`, `.Workflows.Hosting`, `.Workflows.Engine`, `.Teams`, `.Tools`, `.Plugins`, `.Testing` | Workflows, teams, typed tools, tool sources, plugins, web search, and the scripted models for tests. It references only `PiSharp.Durable` and `PiSharp.Extensions.Abstractions`. |
| `PiSharp.CustomAgents.JavaScript` | `PiSharp.CustomAgents.JavaScript` | JavaScript tools and `Script` workflow steps on the Jint sandbox from `PiSharp.Codemode`. |
| `PiSharp.CustomAgents.Scripting` | `PiSharp.CustomAgents.Scripting` | C# script tools and `CSharp` workflow steps, compiled with Roslyn (`Microsoft.CodeAnalysis.CSharp`, MIT). |

`ModelAnswer` and `ModelCallException` are public but live in `PiSharp.CustomAgents.Workflows.Engine`, so add that namespace when you use them.

## The runtime

An `AgentTeam` holds definitions, tool sources and workflows. `AgentTeam.OpenAsync` opens a durable harness over an `IStorage`, applies the definitions, and resumes whatever an earlier process left running. You usually pass `SqliteStorage.OpenAsync(path)` from `PiSharp.Durable.Sqlite`, so one SQLite file holds the whole team. `MemoryStorage` (namespace `PiSharp.Durable.Storage`) works for tests, but it can't be reopened, so a restart test needs SQLite.

You can also run workflows without a team: create a `WorkflowCatalog` over a durable `IRegistry`, open a harness yourself, and start runs with a `WorkflowRunner`. See [Hosting without a team](/docs/custom-agents/workflows/#hosting-without-a-team).

Models come from any `IDurableModels`. In a real host that is `NativeDurableModels`, which reads each provider's API key through the environment reader you give it. In tests it is `FauxAgentModels`, which answers from routes you script (see [Testing](/docs/custom-agents/tools/#testing-with-scripted-models)).

## What survives a crash

The durable harness gives the base guarantees: a model turn ends with exactly one answer, and a tool interrupted after it started is not rerun unless it is replay-safe. Custom agents add these:

- **Workflow steps.** After each action the engine commits the data and the next action in one step. A crash loses at most the action that was running, and the run restarts that action from the last committed data.
- **Code runs at least once.** An action can run again after a crash, so make its side effects idempotent. `ActionContext.IdempotencyKey` gives a key that is the same on every attempt, `OnceAsync` records a result once per visit, and `IsReplay` tells you an earlier attempt at the same point started and didn't commit.
- **Model calls are asked once per attempt.** A prompt's answer is recorded, and a rerun after a crash reuses it. The request ID is deterministic: `wf:<task>:<step>:<key>:<attempt>`, with `:r<n>` added for a repair prompt.
- **Starts and questions are idempotent.** A workflow run key returns the run that an earlier process started, and `AgentSession.AskAsync` with a request ID returns the answer to the first send.
- **Team tools are replay-safe.** Delegating, messaging and waiting tools all resume correctly after a restart, and a subagent outlives the call that started it.

## Data limits

A workflow's data is a class that travels as JSON between actions. It's serialized with `System.Text.Json` and `JsonSerializerDefaults.Web` (camelCase) unless you set your own options.

- The serialized data must fit in `MaxDataBytes`, 256 KB by default. Larger data fails the run with error kind `data`. Keep large content in your own store and put its ID in the data.
- Numbers are stored as doubles. A number a double can't hold exactly, such as a `long` above 2^53 or a `decimal` with too many digits, is refused with its JSON path, for example `The number 9007199254740993 at $.id cannot be stored exactly`. Use a string for such values.
- A run may enter at most `MaxSteps` actions, 1000 by default, and fails with error kind `limit` past that.

See [Limits and errors](/docs/custom-agents/workflows/#limits-and-errors) for how to change these.

## Trust model

- **C# tools, C# scripts and plugins run with full trust** in your process. A plugin's load context is not a sandbox. Plugins are denied unless their folder is listed in `AgentPluginLoaderOptions.Trust`, optionally pinned to the SHA-256 of the plugin's assembly. C# scripts can be limited to an allowlist of source hashes.
- **JavaScript runs in a sandbox** with no file system, network or host objects. It sees only the values a run binds. A durable JavaScript tool can call only the tools on its allowlist. Tools that a model writes must run in a worker process unless you turn that check off.
- **Each agent sees only its own tools.** A definition selects its tools, and a subagent never inherits its parent's model, instructions or tools.
- **Messages and search results are untrusted.** Messages between agents reach the model inside an `<agent-message>` header with an HTML-encoded body, and search results come with a line that tells the model to use pages as information, never as instructions. Say the same in your agents' instructions.

## Project setup

Clone PiSharp and build it (see [Build from source](/docs/contribute/build/)). Then reference the projects you need from your own `net10.0` project and suppress both experimental IDs. This is how the samples reference them:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <NoWarn>$(NoWarn);PISHARP_DURABLE001;PISHARP_AGENTS001</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../PiSharp/src/PiSharp.CustomAgents/PiSharp.CustomAgents.csproj" />
    <ProjectReference Include="../PiSharp/src/PiSharp.CustomAgents.JavaScript/PiSharp.CustomAgents.JavaScript.csproj" />
    <ProjectReference Include="../PiSharp/src/PiSharp.CustomAgents.Scripting/PiSharp.CustomAgents.Scripting.csproj" />
    <ProjectReference Include="../PiSharp/src/PiSharp.Durable.Sqlite/PiSharp.Durable.Sqlite.csproj" />
  </ItemGroup>
</Project>
```

Adjust the paths to where your PiSharp clone is. Leave out the JavaScript and Scripting projects if you don't use them. `PiSharp.Durable` and `PiSharp.Extensions.Abstractions` come in through `PiSharp.CustomAgents`.

## A minimal example

This program runs a two-step workflow and asks an agent a question, on scripted models and a SQLite file. It runs offline. Replace `FauxAgentModels` with `NativeDurableModels` and a real `ModelRef` to use a provider.

```csharp
using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Testing;
using PiSharp.CustomAgents.Workflows;
using PiSharp.Durable.Sqlite;

// Scripted models: the first route that matches a request answers it.
var models = new FauxAgentModels();
models.On(request => request.LastUserText?.StartsWith("Summarize", StringComparison.Ordinal) == true,
    _ => "Workflows commit after every action.");
models.On(_ => true, _ => "A workflow is a series of actions that resumes after a crash.");

var team = new AgentTeam(new AgentTeamOptions
{
    Models = models,
    DefaultModel = models.Model(),
    Orchestrator = "assistant",
});
team.Catalog.Register(new AgentDefinition
{
    Name = "assistant",
    Description = "Answers questions about PiSharp",
    Instructions = "Answer in one sentence.",
});

var summarize = Workflow.For<Note>("summarize")
    .Then("clean", note => note.Text = note.Text.Trim())
    .Prompt("summary", note => $"Summarize in one line: {note.Text}", (note, text) => note.Summary = text)
    .Build();
team.Workflows.Add(summarize);

var storage = await SqliteStorage.OpenAsync("agents.sqlite");
await using var session = await team.OpenAsync(storage);

var result = await session.Workflows.RunAsync(summarize, new Note { Text = "  The engine commits the data and the next action together.  " });
Console.WriteLine(result.EnsureCompleted().Summary);

var reply = await session.AskAsync("What is a workflow?", requestId: "question-1");
Console.WriteLine(reply.Text);

public sealed class Note
{
    public string Text { get; set; } = "";
    public string? Summary { get; set; }
}
```

Run it twice. The second run sends no model requests for the question: the request ID finds the recorded answer. The workflow has no run key, so it runs again.

## Next steps

- [Workflows](/docs/custom-agents/workflows/): actions, prompts, branching, composition, versioning and hosting.
- [Agent teams](/docs/custom-agents/teams/): definitions, delegation, messaging and the agent directory.
- [Agent tools](/docs/custom-agents/tools/): typed tools, tool sources, JavaScript, C# scripts, plugins, web search and testing.
- [Samples](/docs/custom-agents/samples/): the Examples and Research programs.
