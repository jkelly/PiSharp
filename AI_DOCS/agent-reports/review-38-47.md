# Cross-review: samples 38–47

Completed 2026-11-01. Read-only review of Workflow, fixture, README and SelfTests for samples 38–47, including the local Data helpers used by 38–42. This excludes the reviewer's own samples 33–37. Three concrete defects were reproduced below. No additional confirmed defects were found in 38–42, 44 or 47 within this review.

Assignment: hard, `gpt-6.1-sol` / high reasoning; requested service tier ultrafast, effective service tier unverified because delegation exposes no tier control.

No source edits, builds, test-suite runs, provider calls, API calls, commits or pushes were performed. Reproductions loaded the already-built sample DLLs plus Shared into PowerShell's .NET 10 runtime and invoked their public Workflow ToolSpec callbacks. Each reproduction used a new isolated temp workspace. All mutation callbacks received an approved SampleContext; approval bypass is not involved.

Line references below describe the reviewed pre-fix source; the parent has been notified and may assign fixes concurrently.

## 1. Persisted roster assignments disappear from admission checks after a fresh context

**Priority:** P2. **File:** `samples/agents/43-ShiftCoverageCoordinator/Workflow.cs`, line 21 (`Assigned`) and lines 48–54 (`Assign`).

`Assigned` reads only `c.State["assignments"]` or the original fixture's `employeeId`; it never reloads the `roster-assignments.json` written by `Assign`. A new context in the same workspace therefore ignores previously committed local assignments, admits an overlapping shift, and overwrites the prior roster artifact with the new context's partial dictionary.

Reproduced with unchanged source input across contexts:

1. Clone fixture shift S1 into S3, changing only its ID. Both span 2026-11-02 09:00–17:00 UTC and initially have no employee.
2. In approved context A, assign S1 to E2. Result: `assignments={"S1":"E2"}`.
3. Create approved context B using the same workspace and an identical input clone.
4. Rank S3. Actual: E2 is recommended with `assignedHours=0`, despite its persisted overlapping S1 assignment.
5. Assign S3 to E2. Actual: success with `assignments={"S3":"E2"}`. The file no longer contains S1.

Expected: persisted S1 remains authoritative and excludes E2 from overlapping S3; previous assignments are retained. Fix by loading/validating the persisted roster when computing current assignments, and preserving its full state on writes. Add a fresh-context overlapping-shift regression and unchanged-ID replay check. The README's unqualified statement that repeated assignment returns an idempotent receipt currently holds only within one context.

## 2. Newline concatenation hides contradictory incident event variants

**Priority:** P2. **File:** `samples/agents/45-IncidentTimelineBuilder/Workflow.cs`, line 39 (`GroupBy` variant signature), with false provenance emitted at lines 43–44.

Variant identity is built with `string.Join("\n", timestamp, kind, summary, correlationId)`. These fields are allowed to contain newlines, so different field tuples can produce the same signature. Two contradictory source records are then merged as an exact duplicate, `conflicted=false`, with both sources attributed to the first variant.

Reproduction changes only the two fixture `ev2` records (same normalized timestamp and correlation ID):

| Source | kind | summary |
| --- | --- | --- |
| app-log | `error\nprefix` | `tail` |
| monitor | `error` | `prefix\ntail` |

Call `collect_evidence(INC1)`, then `build_timeline(INC1)`. Actual: three total timeline rows; the sole `ev2` row has `kind="error\nprefix"`, `summary="tail"`, `sourceIds=["app-log","monitor"]`, `conflicted=false`, and the top-level `conflicts` array is empty. One source's different facts have disappeared.

Expected: two retained `ev2` variants and one conflict entry, as promised by the README. Use structural tuple equality, or an unambiguous serialization, rather than delimiter concatenation. Add a delimiter-containing-field regression.

## 3. Runbook report can publish a previous success after a different plan is inspected

**Priority:** P2. **File:** `samples/agents/46-RunbookExecutionAssistant/Workflow.cs`, line 46 (`Inspect`), line 75 (`execution` state), and line 78 (`Report`).

Inspecting a runbook updates its plan fingerprint but leaves its prior execution cached. The report tool retrieves that execution by runbook ID alone, without verifying that it belongs to the current inspected plan. Consequently it can publish a successful result for a plan that has not run and would fail.

Reproduction in one approved context:

1. Inspect and execute fixture `recovery`. Actual: `success=true`.
2. Change step `contents.expected` to `absent-marker`.
3. Inspect `recovery` again; the new plan is accepted.
4. Call `write_execution_report(recovery)` without executing the changed plan. Actual: `success=true`, copied from the old execution.
5. Execute the current plan. Actual: `success=false` because `absent-marker` is absent.

Expected: step 4 rejects the stale execution or explicitly identifies it as a result of the previous plan. Invalidate execution when a different plan is inspected and/or retain the executed-plan fingerprint with the result and verify it before report publication. Add a changed-plan/reinspect/report regression. The README currently describes the output as actual outcomes without exposing this mismatch.

## Reproduction evidence

The successful isolated reproduction root was `C:/Users/jksof/AppData/Local/Temp/pisharp-review-38-47-8915e67935f34712ae70e13ec9045a29`. Results were observed directly from the existing callback implementations:

```text
43: initial assignments={S1:E2}; restarted rank(S3)=E2; final assignments={S3:E2}
45: conflicting ev2 tuples => conflicted=false; conflicts=[]
46: original success=true; report after changed-plan inspection=true; actual new execution=false
```

The first reproduction harness invocation enumerated JsonObject results in PowerShell and failed while printing them; the actual callbacks completed. A second fresh-workspace invocation used a non-enumerating return and produced the concrete evidence above. No project source was changed to obtain these results.

Fixture-only service boundaries and documented lack of production integrations are intentional and are not findings. Parent/original owners should implement and verify fixes; this review made no source modifications.

## Final resolution

Resolution: all three findings (43 persisted roster, 45 structural deduplication, 46 stale execution report) were fixed with regression tests. Final full offline validation and affected live result comparisons pass.
