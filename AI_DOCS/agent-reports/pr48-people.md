# PR48 samples 33–42 review

Status: complete. Three confirmed P2 correctness findings fixed with main-agent authorization; focused regressions pass. No remaining confirmed findings or blockers in the reviewed scope.

Scope: samples 33–42 Workflow.cs, Data.cs where present, SelfTests.cs, README.md, fixtures, and the sample contracts in AI_DOCS/contracts.md. Starting reviewed head: 34f19a66, branch feat/agent-samples-33-62. Fixture-backed integrations were treated as intentional. No live provider calls, full-solution builds, commits, pushes, or modifications to other reviewers' files.

Requested assignment: hard, gpt-6.1-sol/high, ultrafast. Effective model/reasoning inherited from the parent; effective service tier unverified (delegation API has no tier parameter).

## Confirmed findings and fixes

1. **Sample36: whitespace-only exception approver retained out-of-role access.** `samples/agents/36-AccessReviewAssistant/Workflow.cs:55` accepted any positive-length approvedBy. Reproduction against the compiled DLL: replace fixture security-lead with three spaces and invoke review_access for emp-301. Actual: g-admin retained with reason approved-exception and recommendedRemovalCount=2. Expected: invalid/unapproved exception cannot authorize retention. Fixed by IsNullOrWhiteSpace check. Regression at SelfTests.cs:38 checks the decision and approved local removal of g-admin with a whitespace-only approver.

2. **Sample40: an empty/whitespace approver counted as a valid approval route.** `samples/agents/40-PurchaseRequestCoordinator/Workflow.cs:47` now rejects blank approver IDs. Reproduction against the compiled DLL: replace the applicable fixture route approvers with ["   "] and invoke evaluate_purchase for PR-100. Actual before fix: eligible=true, reason=ready_for_local_submission, approvers=["   "]; submission would create a pending request and consume local budget without a meaningful approver. Expected: malformed approval route blocks submission. Regression at SelfTests.cs:37 covers both empty and whitespace-only entries through evaluation and direct submission, asserting no receipt/reservation directory is created.

3. **Sample42: unchecked coordinate-distance addition created negative travel time.** `samples/agents/42-FieldServiceDispatcher/Workflow.cs:28` previously added two long distances outside the existing checked multiplication. Reproduction against compiled DLL: set TECH-BEN fixture location x and y to 9223372036854775807 and rank JOB-71. Actual before fix: BEN feasible, travelBeforeMinutes=-50, arrivalAt=08:10, recommended over ADA. Expected: reject unrepresentable travel, never admit negative travel. Fixed by checked arithmetic around coordinate differences and the distance sum; multiplication remains checked. Regression at SelfTests.cs:28 exercises both sum and product overflow through ranking and direct assignment and asserts no assignment artifact is created.

## Changed files

- samples/agents/36-AccessReviewAssistant/Workflow.cs
- samples/agents/36-AccessReviewAssistant/SelfTests.cs
- samples/agents/40-PurchaseRequestCoordinator/Workflow.cs
- samples/agents/40-PurchaseRequestCoordinator/SelfTests.cs
- samples/agents/42-FieldServiceDispatcher/Workflow.cs
- samples/agents/42-FieldServiceDispatcher/SelfTests.cs
- AI_DOCS/agent-reports/pr48-people.md

## Verification

SDK: E:\AI\PiSharp\PiSharp\artifacts\sample-tools\dotnet\dotnet.exe.

- All ten existing sample self-test programs33–42 passed with --no-build after the sample36 fix: checks38,13,21,29,18,13,12,15,11,21 respectively.
- Individually built36,40,42 with `build <project> --no-restore -p:BuildProjectReferences=false -m:1`: all passed, zero warnings/errors.
- Final focused36 self-test passed29 checks; final40 passed21; final42 passed27, including the new regressions.
- `git diff --check -- samples/agents/36-AccessReviewAssistant samples/agents/40-PurchaseRequestCoordinator samples/agents/42-FieldServiceDispatcher` passed.

The initial targeted reproductions used PowerShell/.NET10 to load existing compiled assemblies and invoke read-only public Workflow tool callbacks with modified in-memory fixture JSON. The existing repository root served as workspace; no reproduction input files or artifacts were written. Self-tests use their own temporary workspaces. Final integration and CI remain owned by the main agent.
