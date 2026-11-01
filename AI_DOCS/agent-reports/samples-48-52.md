# Batch d: agent samples 48–52

Status: implemented and locally verified on 2026-11-01. Complexity: hard. Assigned model/reasoning: `gpt-6.1-sol` / high. Requested service tier: ultrafast; effective tier is unverified because the delegation API does not expose a tier parameter.

## Files and behavior

Added `Workflow.cs`, `SelfTests.cs`, `fixture.json` and `README.md` in each owned directory:

| Directory | Implemented stages | Result |
| --- | --- | --- |
| `samples/agents/48-DeploymentReadinessReviewer` | inspect release, evaluate gates, write report | Required test/configuration/migration/rollback evidence; missing, malformed, failed or stale evidence cannot become ready. |
| `samples/agents/49-FlakyTestInvestigator` | analyze runs, draft repair, write investigation | Distinguishes insufficient, consistently broken, observed-stable and flaky evidence; identifies complete, repeated outcome correlations; bounded unexecuted repair proposal. |
| `samples/agents/50-DatabaseMigrationReviewer` | simulate migration, review rollback, write review | Disposable in-memory schema/row model for add/drop/rename; detects lost values, missing backfill, unsupported operations and incomplete rollback. Explicitly does not execute a database. |
| `samples/agents/51-ApiDeprecationMigration` | inspect migration, approved apply, verify migration | Allowlisted local source edits with expected occurrence counts, source/configuration freshness checks, idempotent repeated apply, full-content preservation and usage verification. |
| `samples/agents/52-DocumentationDriftDetector` | inspect document, propose corrections, write corrections | Compares file/command/setting references with fixture repository snapshot; precise line/offset corrections and unresolved findings; emits proposed document and report. |

Scaffolded `Program.cs` and project files were preserved. No Shared, catalog, solution, runner, runtime or other workers' source files were edited. Mutation in sample 51 is guarded by both `RequiresApproval` and `c.Approved`. Report-writing tools emit local review artifacts. Tool schemas use explicit IDs and closed argument objects. All demos use deterministic fixture inputs and workspace-relative artifact paths.

## Verification

SDK: `E:/AI/PiSharp/PiSharp/artifacts/sample-tools/dotnet/dotnet.exe` (10.0.401).

For each owned project, ran:

```powershell
& $sdk build "samples/agents/$project/$project.csproj" -p:BuildProjectReferences=false --nologo
& $sdk run --project "samples/agents/$project" --no-build -- --self-test
```

All five project builds succeeded with zero warnings/errors. Standard restore generated missing local assets; no packages were added. Final changed-project builds also used `--no-restore` successfully.

| Sample | Focused tests passed | Native fixture demo |
| --- | ---: | --- |
| 48 | 7 | Three stages succeeded; ready with zero blocking gates. |
| 49 | 6 | Three stages succeeded; flaky, 2/4 failures, one order correlation. |
| 50 | 6 | Three stages succeeded; reversible rename ready for database validation. |
| 51 | 7 | Three stages succeeded with `--approve`; replacement verified. |
| 52 | 6 | Three stages succeeded; three corrections, zero unresolved. |
| Total | **32** | **5/5 demos** |

Tests use distinct temporary workspaces and clean them afterward. Coverage includes nominal artifacts, missing/invalid evidence, negative outcomes, independent IDs or repeated calls, stale input, sequencing, path guards and approval/idempotency for source writes. Regression tests specifically cover changed test/rollback evidence in 48 and malformed migration array members that must not silently pass.

## Limits and integration handoff

No blocking issues remain in the owned work. No provider/live calls, remote effects, commits or pushes were performed. Main owns full-solution validation, provider integration, catalog registration and publication. Sample 50 cannot validate real SQL/transactions/locking/indexes. Sample 51 uses exact textual usage matching and prevalidated but nontransactional multi-file writes; cancellation or I/O failure may leave partial local output. These limitations are documented in the sample READMEs.
