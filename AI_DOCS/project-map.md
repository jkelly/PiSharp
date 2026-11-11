> Active 2026-11-10: [Pi/Pi Durable v1.5.2 sync](sync-1.5.2.md). Prior records below are historical.

> Active 2026-11-08 implementation: see [context](gap-implementation-context.md), [execution plan](gap-execution-plan.md) and [audit contracts](upstream-audit-2026-11-08/contracts.md). Historical material below is preserved.

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

## Agent samples 33–62 (2026-11-01)

Active worktree: `E:/AI/PiSharp/agent-samples-pr`, branch `feat/agent-samples-33-62`, baseline `585a7c61`. This is a separate worktree from the dirty original checkout. The sample solution is `samples/agents/PiSharp.Samples.slnx`; `catalog.json` drives offline and live runners. Every new console project references Shared and provides Program, Workflow, SelfTests, fixture.json and README. Shared uses native PiSharp.Agent/PiSharp.AI transports and validates tools/approval. No new external packages are planned. The pinned SDK is 10.0.401; explicit local executable `E:/AI/PiSharp/PiSharp/artifacts/sample-tools/dotnet/dotnet.exe` is available. Existing OPENAI_API_KEY reuse is explicitly authorized; no plaintext keys or transcripts enter source control.

Validation: build the entire sample solution; `scripts/run-all.ps1 -Dotnet <sdk> -SkipBuild` for all 62; `scripts/run-live.ps1 -Examples (33..62) -Model gpt-6.1-sol -ApiKeyEnvironment OPENAI_API_KEY -Dotnet <sdk> -SkipBuild` for the 30 new live integrations. Tests use fixtures; no external domain actions are authorized by the live run. Main owns manifest/solution/docs/CI/live evidence and commits/PR.

## Website and release preparation (2026-11-08)

Current root is the managed C: worktree at `codex/prepare-1.5.0.1`. Website is Astro 7/Starlight, built with `npm run build` in `website`; main pushes deploy GitHub Pages. GitHub reports 1.5.0 published; 1.5.0.1 is the next four-part candidate. See [release plan](release-1.5.0.1-plan.md) for source ownership, CI blockers and verification.
