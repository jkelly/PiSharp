# Samples 43–47 implementation report

Date: 2026-11-01. Work package: batch-c. Worktree: `E:/AI/PiSharp/agent-samples-pr`.

Status: implementation and local initial qualification complete; final sample47 hardening rebuild/self-test and strict live qualification are owned by the integrator, who requested that workers hold code/build edits while live tests run.

## Scope and implementation

Added `Workflow.cs`, `SelfTests.cs`, `fixture.json`, and `README.md` in each owned directory. Existing Program/csproj scaffolds were preserved. Local builds generated project `packages.lock.json` files. No Shared, catalog, solution, provider, global configuration or other worker files were changed. No live model calls, commits or pushes were made by this worker.

| Sample | Native tool stages | Concrete behavior |
|---|---|---|
| 43 ShiftCoverageCoordinator | inspect_shift → rank_replacements → assign_replacement | Full-interval availability, qualifications, half-open overlapping assignments, assigned-hours/ID ranking, callback approval, repeat-safe workspace roster assignment |
| 44 TrainingComplianceTracker | inspect_training → assess_compliance → draft_reminders | Explicit fixture date; hire/completion-derived due dates; expired, missing and near-due status; evidence-bearing employee reminders and overdue manager drafts; absent manager marked unready |
| 45 IncidentTimelineBuilder | collect_evidence → build_timeline → publish_timeline | UTC ordering, event-ID deduplication with source provenance, retained conflicting variants, explicit correlation-ID groups, no invented root cause |
| 46 RunbookExecutionAssistant | inspect_runbook → execute_runbook → write_execution_report | At most20 steps; write_text/check_exists/check_contains/sha256 allowlist; actual file operations beneath operations/runbookId; topological dependency gates; callback approval; actual failure/skipped evidence |
| 47 BackupRestoreVerification | plan_backup → create_snapshot → verify_restore | Actual fixture bytes and local snapshot copy; SHA-256 manifest; fresh isolated restore attempts; missing/corrupt failure results; callback approval; overwrite refusal; snapshot receipt bound to fixture fingerprint |

All fixture tasks name concrete IDs and request the same three stages as deterministic DemoCalls. All artifacts and tool-returned paths are workspace relative. Mutating tools also set `RequiresApproval`. Workspace defaults and test workspaces are unique. No simulated remote updates or claimed external messages occur.

## Verification evidence

Used `E:/AI/PiSharp/PiSharp/artifacts/sample-tools/dotnet/dotnet.exe`.

For each owned project, executed:

```powershell
& $dotnet build samples/agents/<sample>/<sample>.csproj -p:BuildProjectReferences=false --nologo -v:minimal
& $dotnet run --project samples/agents/<sample>/<sample>.csproj --no-build -- --self-test
```

All five initial builds passed with zero warnings/errors. Initial tests passed41 focused cases: sample43=7, sample44=8, sample45=8, sample46=9, sample47=9. Tests cover nominal results, absent input, distinct IDs/repeated calls, approval refusal without mutation, overlap, expiry boundaries, contradictory event evidence, failed dependencies, path traversal, forbidden actions, actual backup corruption and missing files.

After these passes, sample47 gained one additional case and the corresponding fixture-fingerprint guard: changing a dataset after snapshot creation and replanning must reject reuse of the old snapshot. This final source change was handed to the integrator for rebuild and self-test (expected final total42), rather than altering binaries during integrator live qualification. No final42-case pass is claimed here until that check runs.

Scoped `git diff --check` returned zero errors. These new directories were untracked at check time, so that command alone is not an exhaustive whitespace check of their contents.

## Limitations and handoff

- Samples are deterministic local workflows, not production scheduling/compliance/incident/operations/backup integrations.
- Roster assignments are persisted and validated across contexts sharing a workspace; the local ledger assumes sequential writers. Snapshot receipts remain in-process and cross-process snapshot resume is not implemented. New demos use fresh workspaces. Existing source files are never overwritten during fixture seeding.
- All live qualification, strict demo-result comparisons, full-solution checks, final integration and publication remain with the main agent.
- No implementation blocker remains. Final sample47 rebuild/self-test and live qualification are the explicit pending integration checks.

Complexity: hard. Assigned model/reasoning: `gpt-6.1-sol` / high. Requested service tier: ultrafast. Effective tier: unverified because the delegation interface does not expose a tier control or receipt.

## Authorized cross-review fixes (2026-11-01)

Completed the three requested own-code corrections after main live qualification stopped:

- Sample43 reads and validates `roster-assignments.json` for each operation. Its versioned ledger preserves earlier assignments, validates employee/shift identities and eligibility, and binds each assignment to source fingerprints. A new context cannot assign an already occupied employee to overlapping S3 or replace S1 by writing a fresh mapping. Unknown persisted employee IDs and changed source records fail closed.
- Sample45 groups variants using a structural `(timestamp, kind, summary, correlationId)` tuple. Embedded newlines in different fields cannot collapse conflicting evidence into one variant.
- Sample46 binds execution results to the reviewed plan's SHA-256 fingerprint. Reinspection and execution attempts invalidate prior results; report publication rejects a changed plan and cannot reuse an old success after reinspection.

Updated corresponding READMEs and added focused regressions. Exact current affected-project test results:

| Project | Build | Focused self-test cases |
|---|---|---|
| 43-ShiftCoverageCoordinator | Pass; zero warnings/errors | 11 passed |
| 45-IncidentTimelineBuilder | Pass; zero warnings/errors | 9 passed |
| 46-RunbookExecutionAssistant | Pass; zero warnings/errors | 12 passed |

Used the same explicit SDK and `build -p:BuildProjectReferences=false` / `run --no-build -- --self-test` commands documented above, with each project built and tested sequentially. All32 affected-project cases passed. Each affected native offline demo also ran with `--demo --approve`: exit0, `success=true`, all three tool stages executed. No live model call, commit or push occurred.

The new regressions reproduce all three review findings: context recreation with overlapping S3 and retained S1/S2 assignments; distinct kind/summary combinations whose newline concatenations collide; and changed/reinspected plans attempting to publish old execution success. Source checks for unknown persisted employee identity and altered assignment evidence also pass. No blocker remains for these three fixes; any renewed live qualification remains with the integrator.
