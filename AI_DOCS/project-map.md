# Project Map

Planning snapshot: 2026-10-20. Request: plan the remaining PiSharp Durable features; no feature implementation in this turn.

- Worktree: `E:\AI\PiSharp\PiSharp\.claude\worktrees\durable-network`; branch `feature/durable-network`.
- Outer `E:\AI\PiSharp` is not the repository. The older primary checkout is a different branch; do not edit it for this work.
- Current baseline: committed fixes `cd65d88`, followed by the uncommitted milestone in decision 0009. Preserve all its source/test changes.
- Runtime: C#/.NET 10, SQLite, ASP.NET Core minimal API, Razor/Bootstrap and browser JavaScript; native terminal CLI/TUI. Core engine follows pi-durable 1.2.0.
- Main roadmap: [09-durable-remaining-features.md](../docs/plans/09-durable-remaining-features.md). API proposals: [contracts.md](contracts.md). Work packages: [workflow-plan.md](workflow-plan.md).

## Architecture map

| Area | Existing paths | Planning relevance |
| --- | --- | --- |
| Solution/build | `PiSharp.slnx`, `global.json`, `NuGet.Config` | SDK pinned 10.0.401; local installed fallback 10.0.400 |
| Durable engine | `src/PiSharp.Durable/Harness/`, `src/PiSharp.Durable/Chord/` | Tasks, ownership, memos, documents, transactions; retain portability |
| Provider-neutral models | `src/PiSharp.Contracts/ModelOperations/`, `src/PiSharp.AI/ModelOperations/`, `src/PiSharp.Durable/Models/NativeDurableModels.cs` | Classifier choice/score/bool answers; confidence/usage; existing adapters |
| Host/catalog | `src/PiSharp.Durable.Hosting/DurableDeviceHost.cs`, `SessionCatalog.cs`, `SessionCatalog.Automation.cs` | Own sessions and catalog v4; no ASP.NET or CLI dependency |
| Jobs | `src/PiSharp.Durable.Hosting/Apps/Jobs.cs`, `SampleJobs.cs`, `Extras.cs` | Numeric inputs, phases, single-child AskAgent, audits, command-guard memo |
| Small shared state | `src/PiSharp.Durable.Hosting/Apps/AppState.cs` | Device-wide revisioned JSON; depth 24, 100,000 characters |
| Automation/history | `JobSchedules.cs`, `TriggerCredentials.cs`, `ExecutionHistory.cs` in Hosting | Interval receipts, tick-only tokens, authoritative open-session history |
| Server | `src/PiSharp.Durable.Server/DurableDeviceApi.cs`, `DurableDeviceApi.Automation.cs`, `DeviceService.cs` | v1 endpoints, admin/trigger auth, flat capability list |
| Client | `src/PiSharp.Durable.Client/DurableDeviceClient.cs`, `AutomationClient.cs`, `Wire.cs` | BCL-only authenticated HTTP/SSE with JSON escape hatch |
| Web | `src/PiSharp.Durable.Web/Controllers/`, `Views/Automation/`, `Views/Fragments/_JobCard.cshtml`, `Hub/MarkdownRenderer.cs`, `wwwroot/js/automation.js` | Existing schedules/executions, sanitized markdown, proxy/token custody |
| Terminal | `src/PiSharp.Cli/Durable/` | Local harness/TUI plus serve/token; no remote attach or feature commands yet |
| Focused tests | `tests/PiSharp.Durable.Hosting.Tests/`, `PiSharp.Durable.Server.Tests/`, `PiSharp.Durable.Web.Tests/`, `PiSharp.Durable.Web.BrowserTests/`, `PiSharp.DurableCli.Tests/` | Offline fakes, HTTP and browser acceptance |
| Classifier tests | `tests/PiSharp.Classifiers.Tests/` | Registry routing, confidence and usage including billed failures |

## Verified limits and gaps

- Full job records live in a conversation family keyed by task ID; transcript run cards retain only 30 entries. Task docs retire on terminal settlement. New artifacts/cache receipts cannot depend on task-doc retention.
- `AskAgentAsync` finds one owned conversation and assigns submission keys by call index. Parallel research requires explicit stable branch identity and separate children.
- `ExecutionHistory` records generation tasks (`pi.generation`) and jobs; it is not a provider billing ledger. Direct classifier costs need recording. Archived sessions and aggregated user turns are not currently exposed.
- Schedules and executions refresh on demand. Current client capability support is uneven: browser supports the milestone, local terminal lacks equivalent management commands.
- Source retrieval, judgment-policy management, result artifacts, general feedback/preferences, doctor and service packaging are absent. Existing `PiRequestBudget` limits transport memory; it is not a spend budget.
- Cross-store reconciliation, durable replay versus TTL cache, hard-cap admission and child cancellation semantics require contract discovery F1 before implementation.

## Build and verification commands

Run from this worktree. Prefer the normal `dotnet build <project> -c Release` when the pinned SDK is installed. In this environment:

```powershell
dotnet 'C:\Program Files\dotnet\sdk\10.0.400\MSBuild.dll' tests/PiSharp.Durable.Hosting.Tests/PiSharp.Durable.Hosting.Tests.csproj /t:Build /p:Configuration=Release /p:UseSharedCompilation=false /v:minimal /nologo
dotnet tests/PiSharp.Durable.Hosting.Tests/bin/Release/net10.0/PiSharp.Durable.Hosting.Tests.dll --report artifacts/<unique-report-name>.json
git diff --check
```

Repeat the project/DLL pattern for each affected suite. Report files use create-new semantics; choose a fresh name. Locked restore, if needed, must explicitly use `C:\Users\jksof\.nuget\packages`; do not change the SDK pin or fixture baselines just to run locally. Browser suites require their existing Playwright setup.

Prior milestone evidence: Hosting 72, Server 16, Web 4, Browser 27, CLI 100 passing cases (219 unique). These were not rerun for this documentation-only request. Optional live provider/TypeScript/OS service tests remain qualification work; never label absent credentials or unavailable OSes as passes.
