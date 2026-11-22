---
title: Offline testing & live validation
description: Test agents, workflows, crash recovery and multi-agent teams deterministically offline with FauxAgentModels, fake clocks and live comparison harnesses.
---

**What happens in production:** Relying solely on live LLM calls in CI makes builds flaky, slow, and expensive — while testing only mocked helper functions misses real bugs in tool schemas, turn batching, crash replay, and prompt wiring. PiSharp ships first-class deterministic test harnesses at every layer so CI runs 100% offline while live runs validate real provider behavior on demand.

## Four testing patterns across PiSharp

| What you are testing | Tool / Harness | Network / Keys needed? |
| --- | --- | --- |
| **Workflows, teams, plugins & crash recovery (`PiSharp.CustomAgents`)** | `FauxAgentModels` + `SqliteStorage` (for restart tests) + fake `TimeProvider` | None |
| **Low-level durable sessions, hooks, signals & gates (`PiSharp.Durable`)** | `ScriptedModels` + `StorageConformance` + manual `HarnessOptions.Now` clock | None |
| **Domain agent tools & real `PiSharp.Agent` turn loops** | Shared `SampleHost` fixture `--demo` & `--self-test` pattern (all 62 agent samples) | None |
| **End-to-end live provider regression & structured parity** | `samples/agents/scripts/run-live.ps1 -CompareToolResults` & `PiSharp.Evals` | Explicit opt-in API key |

## Recipe 1: Deterministic team & restart tests (`FauxAgentModels`)

`FauxAgentModels` (`PiSharp.CustomAgents.Testing`) routes requests by predicate, agent system prompt (`OnSystem`), or model ID (`OnModel`), and exposes `Requests` / `RequestCount` for strict assertions:

```csharp
using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Testing;
using PiSharp.Durable.Sqlite;

var models = new FauxAgentModels();
models.On(req => !req.AfterTools, _ => FauxReply.ToolCall("lookup_order", """{"orderId":"42"}"""));
models.On(req => req.LastToolName == "lookup_order", req => $"Status: {req.LastToolResult}");

var team = new AgentTeam(new AgentTeamOptions
{
    Models = models,
    DefaultModel = models.Model(),
    Orchestrator = "lead",
});
team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Order desk", Tools = [lookupOrder] });

// Run 1: executes tool and records reply in SQLite
await using (var s1 = await team.OpenAsync(await SqliteStorage.OpenAsync(dbPath)))
{
    var r1 = await s1.AskAsync("Where is order 42?", requestId: "q-42");
    Assert.Contains("Order 42", r1.Text);
    Assert.Equal(2, models.RequestCount);
}

// Run 2: reopen same SQLite file — idempotent requestId sends 0 new model calls!
await using (var s2 = await team.OpenAsync(await SqliteStorage.OpenAsync(dbPath)))
{
    var r2 = await s2.AskAsync("Where is order 42?", requestId: "q-42");
    Assert.Equal(2, models.RequestCount); // unchanged
}
```

Use `FauxReply.Answer("...").HeldUntil(tcs.Task)` to freeze a model reply mid-flight so tests can deterministically inject `Steer()`, `Abort()`, or concurrent signals before the turn settles.

## Recipe 2: Testing durable timers & timeouts without waiting

Pass a controllable `TimeProvider` in `AgentTeamOptions.TimeProvider` (or `WorkflowCatalogOptions.TimeProvider`, or a manual clock delegate in `HarnessOptions.Now`) so tests covering `ActionResult.Sleep`, `ActionResult.Repeat`, and `WaitForSignal(..., timeout: TimeSpan.FromDays(2))` complete in milliseconds instead of days.

## Recipe 3: Dual-mode agents (`--demo` + `--self-test` + `--live`)

Every one of the 62 projects in `samples/agents/` follows a strict verification contract that works great for production agent repositories too:
1. **`--self-test`** exercises edge cases, schema validation failures, approval denials, and invariant checks directly on the tools in milliseconds.
2. **`--demo`** runs the real `PiSharp.Agent` loop and tool scheduler offline against authored fixture inputs, saving `report.json` and `transcript.json`.
3. **`--live`** swaps in a real native transport (`openai-responses`, `anthropic`, `openrouter`) against the exact same tool schemas while keeping external side effects gated behind `--approve`.
4. **`run-live.ps1 -CompareToolResults`** compares each live model run's structured tool results against the deterministic fixture baseline (ignoring JSON key order and workspace paths, while preserving array order) without grading brittle free-form prose.

## Try it now

```powershell
# Run every fixture demo + self-test across selected practical agents offline
pwsh -File samples/agents/scripts/run-all.ps1 -Examples 5,16,45

# Run CustomAgents offline scripted suites
dotnet run --project samples/CustomAgents.Examples -c Release -- all
dotnet run --project samples/CustomAgents.Research -c Release -- --parts 3

# Optional paid live comparison against deterministic fixture expectations
pwsh -File samples/agents/scripts/run-live.ps1 -Examples 5,16,45 -Model gpt-6.2-sol -ApiKeyEnvironment OPENAI_API_KEY -CompareToolResults
```

See also [Testing](/docs/contribute/testing/) and [Evaluations (`PiSharp.Evals`)](/docs/contribute/evaluations/).
