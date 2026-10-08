# Explicit companion target selection source handoff

Base: `d1eaccbeade5ef81662856526d80ba8e406fe415`. This isolated change adds optional `-TargetId` to the existing `Invoke-NativeCompanionTargets` function in `tools/native-companion-registration.ps1`. It does not edit the active coordinator checkout, `tools/test-native.ps1`, the 26-target registry, project files, preparation scripts, SDK, dependency locks, fixtures or goldens. Pi stays v0.99.1 at `d86654abb8862e201933517d6f1fce9f88dd117f`.

## Selection and receipts

Omitting `-TargetId` preserves the full 26-target serial gate. Supplying it requires a nonempty array of exact, case-sensitive registry IDs. Unknown, duplicate, null, empty, wildcard, padded and wrong-case IDs reject before a prepared-context revalidation, evidence-directory creation or process setup. Every registry target is still validated, including locks and fixture pins, before selection. Requests always execute in registry order, regardless of argument order; no parallel execution is introduced.

The launcher still revalidates the complete prepared context and performs the original per-launch registry/source/product/host/payload checks. Selection does not narrow product-root membership or authorize execution. The existing per-target deadlines, original process/output-copy joins, stop behavior, retained cleanup ownership, append/flush receipt persistence and passing-receipt requirements are preserved.

All 26 rows remain in `receipt.json` and `receipt.jsonl`. Each has `selected`. Unselected rows have `status=UNSELECTED_NEVER_RUN`, `passingReceipt=false`, no PID/report, and no claimed process/output joins. Selected rows not reached after a stop remain `UNEXECUTED`. The receipts also include `selectionMode`, `selectedIds`, `unselectedIds`, and `selectedComplete`. Existing `complete` still requires passing receipts for **every** registry target. A successful selected batch therefore cannot claim full-gate completion when anything is unselected. Package/phase acceptance remain false. Return/throw is based on the selected batch, allowing remaining companions to run without repeating previous ones; earlier receipts must be retained separately and reviewed before any combined full-gate claim.

## Coordinator integration

This is source-only and grants no execution allocation. Review and integrate this successor into a new admitted candidate; generate its matching source manifest and preparation/build receipt. Do not reuse the base candidate's source manifest/receipt, mutate the active candidate, or authorize selection from a stale receipt. The coordinator's independent runtime allocation, permitted target list and expiration/deadlines remain in force; target selection does not renew, enlarge or bypass that allocation. Use the exact admitted host through `Get-NativeLaunchHost`.

After review and a separate runtime allocation, the invocation shape is:

```powershell
. (Join-Path $repo 'tools/native-companion-registration.ps1')
$prepared = Get-NativePreparedValidation -Repo $repo `
  -SourceManifest $sourceManifest -SourceManifestSha256 $sourceManifestSha256 `
  -BuildReceipt $buildReceipt -BuildReceiptSha256 $buildReceiptSha256
$hostPath = Get-NativeLaunchHost -PreparedValidation $prepared
Invoke-NativeCompanionTargets -Repo $repo -DotnetExecutable $hostPath `
  -PreparedValidation $prepared -EvidenceDirectory $freshEvidenceDirectory `
  -TargetId $allocatedTargetIds
```

These are placeholders, not a ready-to-run allocation. The existing `tools/test-native.ps1` call remains unchanged and runs the default full companion gate after its core tests. To avoid repeating core and passed companion tests, the coordinator should call the existing companion function directly under the same approved isolated environment settings from that launcher, supplying its reviewed exact target IDs. Do not interpret `Get-NativePreparedValidation` as runtime permission.

For the ten targets after `focus` in the current registry, the explicit list is:

```powershell
$allocatedTargetIds = @('thinking', 'thinking-agent', 'indic-conjuncts', 'simple',
  'kitty-alternate', 'terminal-keybindings', 'pi-messages', 'google-generative-ai',
  'terminal-select-list', 'terminal-select-dialog')
```

This list intentionally leaves `focus` unselected; its earlier failed/incomplete evidence is not converted to pass. A separate focus rerun requires including `focus` in its own reviewed allocation. The coordinator must use the current actual receipt history to decide the target set; the example is not a claim that any target passed.

## Authored admission controls and static evidence

`tools/test-native-companion-selection-admission.ps1` contains 20 authored controls: default/explicit full scope and order; subset/single-target order; unknown/duplicate/case/wildcard/whitespace/empty/null/mixed IDs; explicit never-run/default receipt rows; ambiguous synthetic registration; and real-launcher rejection before context/evidence creation. Launcher rejection controls supply a null prepared context as an independent stop, so even a selection regression cannot reach process construction. The test harness verifies its own/helper/registry/source-admission bytes against a supplied immutable source manifest, runs full source closure/registry admission, and writes control-only evidence under a fresh owned `artifacts/` branch. It launches no runtime, SDK, product or provider.

Only after a separate control execution assignment:

```powershell
& (Join-Path $repo 'tools/test-native-companion-selection-admission.ps1') `
  -Repo $repo -SourceManifest $sourceManifest -SourceManifestSha256 $sourceManifestSha256 `
  -EvidenceDirectory $freshControlEvidenceDirectory
```

The controls are **authored and unrun** in this lane. Static PowerShell parsing and whitespace checks passed. A static comparison confirms that the original process setup, argument construction, timeout handling, output-copy operations and retained join/disposal block are unchanged. The unchanged block from `$dllPath = Resolve-NativeCompanionPath ...` through `if ($stopped) { break }` has normalized LF UTF-8 SHA-256 `e2b2309bad191305ce18a2bff33e772c234a6a405bd9e97389a2932cde8ee81b` on both base and successor. No runtime/launch, dependency installation, network, security setting change or publication occurred. Native execution stays exclusively with the coordinator.
