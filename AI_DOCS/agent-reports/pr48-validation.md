# PR 48 validation and integration review

Status: complete; no confirmed defects in the reviewed integration changes.

Reviewed head: `34f19a66`. Work item: `pr48-validation`; complexity hard; assigned model/reasoning `gpt-6.1-sol` / high. Requested service tier: ultrafast; effective tier is unverified because the delegation interface provides no tier control.

## Scope

Read the PR 48 section of `AI_DOCS/workflow-plan.md`, sample contracts in `AI_DOCS/contracts.md`, all four files under `samples/agents/scripts`, catalog and solution membership, `.github/workflows/agent-samples.yml`, root/sample README changes, and the new Markdown/JSON live evidence. Inspected the shared report-producing host and representative workflows to understand the comparison inputs. Source review was read-only; this report is the only file authored by this reviewer.

## Checks and results

- `pwsh -NoProfile -File samples/agents/scripts/test-validation.ps1`: passed. Covers property ordering, workspace normalization, wrong values, meaningful array ordering, missing output and errored output.
- Additional in-memory comparator checks: passed for nested dictionaries, empty arrays, null, false, forward-slash workspace paths, failed fixture outputs and case-sensitive tool identity.
- PowerShell AST parser accepted all four validation script/module files without errors.
- Catalog validation: 62 entries, 62 unique numbers, every project exists and occurs in the solution. The solution contains the 62 samples plus Shared and Tests. No missing/new duplicate mapping found.
- Portable evidence validation: exactly 30 successful result rows for the new batch, each has fixture comparison enabled, no recorded failure/missing tool, and successful host status. Summed input/output token counts are 56,286 / 8,214, matching the declared totals.
- CI still builds the complete solution, runs `run-all.ps1 -SkipBuild` on Windows, Ubuntu and macOS, and uploads offline reports. The PR changes the displayed count to 62; actual coverage comes from the expanded solution/catalog. macOS temporary path canonicalization remains intact.
- The extra head commit after `21d4be63` changes only workflow documentation, so it does not invalidate the runtime version described by the new evidence.

## Boundaries and remaining verification

The live comparator requires each fixture result to appear among successful same-name live outputs; it intentionally does not compare free-form prose, enforce call order, or require an exact one-to-one call trace. No concrete fixture failure caused by that behavior was established during this review. This review checks portable evidence consistency; it cannot independently authenticate omitted raw provider transcripts.

No paid/live provider requests, external writes, builds, commits or pushes were performed. A local `dotnet --version` probe confirmed that the default installation lacks the exactly pinned SDK 10.0.401 (10.0.400 is installed); therefore this reviewer did not claim a fresh .NET build or cross-platform runtime result. The main integrator's SDK/CI qualification remains the source for those checks. No source changes are proposed by this review.

## Independent review of working-tree fixes

Status: verified by source and test review; no concrete introduced defect found. Reviewed only the current changes against HEAD in samples 36, 40, 42, 46, 51, 57 and 61, including their added tests and README adjustments. Did not build or run paid requests; the main integrator owns the concurrent full offline and strict live qualification.

- **36:** `IsNullOrWhiteSpace` correctly prevents a blank exception approver from authorizing out-of-role access. The new test verifies both the removal decision/reason and inclusion in the actual local removal receipt.
- **40:** Invalid nonempty routes containing blank approver IDs now fail before submission. Tests exercise empty and whitespace entries through both evaluation and the submission callback, then assert no purchases directory was created. Empty route arrays still make a request ineligible through the existing logic.
- **42:** The checked expression now covers Manhattan-distance addition before multiplication, preventing a wrapped distance from appearing feasible. The `long.MaxValue` y-coordinate case independently exercises the new sum-overflow rejection in ranking and assignment and checks absence of a receipt. The y=1 case exercises the already-checked multiplication overflow. The fixture coordinates are nonnegative, so subtraction stays representable before the sum check.
- **46:** Validation now checks the entire raw array count and rejects every nonobject row before casting, preserving all-object plan processing. Null and string tests catch the old silent filtering; execution is also denied without producing operation/report files. **Nonblocking test-strength note:** the >20-row regression uses null padding, so nonobject validation alone also makes it pass. A separate 21-valid-object case would independently pin the count limit. No runtime bug was found in the implemented count check.
- **51:** Comparing full expected final file contents correctly permits chained replacements while still rejecting intermediate or tampered output. The new test covers inspect/apply/verify, final exact contents, repeat apply, intermediate-content verification failure and refusal to reapply over tampering. The removed per-edit presence checks conflicted with later edits by design; final-content comparison remains stricter about actual output bytes.
- **57:** Checking persisted merge-ID ownership before replay/new-plan handling prevents archive replacement by a different request. The regression tests a restarted proposal and an existing callback after changing the request, compares the complete state file byte-for-byte, and verifies both surviving records and prior originals. Existing identical-request replay remains on the unchanged path.
- **61:** Testing the complete substituted string detects both marker-internal and adjacent-boundary reconstruction before falling back to a character absent from the source. The new `][][` / `][` case specifically bypassed the old marker-only check and now verifies preview, match count and saved export contents. Final absent-value verification is retained.

The only suggested follow-up is strengthening the sample 46 limit test as described above; it is not a confirmed implementation defect or blocker.
