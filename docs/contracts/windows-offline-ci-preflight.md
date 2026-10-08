# Windows offline CI preflight

This source-only slice adds a deterministic readiness check for the existing native preparation gate. It does not install dependencies, build, publish fixtures, run tests, create allocations, rewrite paths in approvals, or grant release acceptance. No GitHub Actions workflow is added: there are no existing action pins, and an unconditional hosted build would bypass the project's path-bound admission and offline preparation requirements.

## Current contract

The inspected basis is coordinator-cleared `5bddf1a6b9b10409bdb1a638451174b3a972bd08` (tree `e641f582cf83c3549ac303a14a828340f839c4e2`). It uses Windows paths, SDK `10.0.401` with roll-forward disabled, `net10.0`, real framework/project-only lockfiles and `NuGet.Config` with all package sources cleared. The unchanged registry admits 29 companions and 50 exact product roots; the original launcher also runs ten core suites. This is a Windows-only scope. Linux/macOS runtime support is unqualified.

`tools/ci/validate-windows-offline-inputs.ps1` accepts a reviewed checkout and separately supplied manifest/allocation pins. It validates its helpers before loading them, delegates full membership and fresh-output checks to the original `Assert-NativeSourceClosure`, delegates registry/fixture/lock checks to `Get-NativeCompanionRegistration`, and retains the original root declaration check. It verifies the allocated host's physical hash without launching it. It also checks the solution and the original ten fixture projects for pinned framework-only locks. Missing, changed, expired or relocated inputs produce a failed report and a nonzero PowerShell outcome.

The helper accepts another local drive, spaces and Windows case/separator equivalence **only when newly issued source review and allocation name that exact checkout**. A receipt copied from this PC to a runner fails. It never translates an old approval into a new one. Fresh review must include the final CI helper, its controls, the whole source closure and actual checkout bytes, including line endings. The check does not identify an installed SDK by running it; original preparation must still prove actual SDK selection.

## Future allocated invocation

Use PowerShell 7 on a provisioned Windows runner. Obtain an immutable full-source manifest and a current lead-owned runtime allocation for that exact candidate, tree, runner path and host. The SDK must already exist; this slice provides no download/bootstrap action. The report must be fresh, outside the checkout, in an existing owned evidence directory.

```powershell
& "$repo/tools/ci/validate-windows-offline-inputs.ps1" `
  -Repo $repo -SourceManifest $manifest -SourceManifestSha256 $manifestSha `
  -ApprovedRuntimeAllocation $allocation -ApprovedRuntimeAllocationSha256 $allocationSha `
  -Report $freshReport
```

The report's `inputsReady` means only that preparation inputs passed these source checks. `nativeAcceptance`, `packageAcceptance`, `runtimePermissionIssued` and `sdkExecutionVerified` remain false. Retain a rejected report. Do not catch its rejection and continue to a build, run an unregistered subset, remove locked mode, generate replacement expectations, or report a native pass from this check.

The 30 authored controls in `tools/ci/test-windows-offline-contract.ps1` cover moved review/allocation paths, identity/hash mismatches, string permissions, expired allocations including the exact deadline, absent host pins, SDK drift, added feeds/fallbacks, disabled locks and external dependencies. They test the bounded declaration contract; their synthetic inputs cannot pass the full preflight. They are source-only and unexecuted in this handoff. A future authorized control run supplies a fresh report:

```powershell
& "$repo/tools/ci/test-windows-offline-contract.ps1" -Report $freshControlReport
```

## Remaining gates and workflow permissions

1. Independently review this final source and execute the authored preflight controls under a separate allocation. Qualify positive full-preflight inputs and negative missing/changed source, stale-output and physical-host cases on the actual runner.
2. Provision the exact SDK and an offline checkout without this script installing anything. Produce fresh path-bound review/allocation artifacts. A self-hosted runner label alone is not a permission or an offline guarantee.
3. Execute the unchanged `tools/prepare-native-products.ps1` only when separately authorized. Its original `locked-offline-build-publish` operation includes ten **local fixture** publishes required by the existing test gate; this task does not authorize that execution. It retains isolated environment directories, cleared-source locked restore, explicit Directory.Build imports, sequential owned preparation, full solution build and actual SDK/log/host/product receipts.
4. Feed those fresh immutable receipts to the unchanged `tools/test-native.ps1`. Keep every core/registered companion, launch admission, exact output root, scoped assembly set and original child/output join. Failures remain nonpassing; a CI timeout/cancellation is not settlement evidence. Agree on an owned runner cleanup protocol before enabling automated jobs.
5. Qualify packaging and the original release acceptance gates separately. This preflight does not provide signing, release publishing, live inference or cross-platform acceptance.

A future checkout-only workflow should request at most `contents: read`, disable checkout credential persistence, and use an independently verified immutable official action commit. A preseeded offline checkout needs no GitHub token permissions (`permissions: {}`). No secrets, write permissions, signing identities, provider keys, untrusted PR execution on privileged self-hosted runners, SDK download or publishing permissions are required by this preflight. There are no new action references to verify in this change. Keep readiness controls and native acceptance as distinct job/results; do not name a readiness-only job “native tests passed”.
