# Provider ownership successor and next offline batch

Status: source integrated; no build, test, provider call or upstream execution performed. The lead reports independent static closure of provider ownership correction `dcb991cf` and Windows file-sharing correction `aeec350c`, with no weakened assertions. The corrected atomic test blob is preserved exactly. A lead-owned bounded runtime allocation remains pending. This plan grants no execution authority.

Source merge: `01bf0473bfaba376382ed9393a46b2bcf5b12082` above preserved combined `8d40e2c244ba9fc76d4fb5e1c9acbd4cf7f42a42`. Exact provider successor `dcb991cfdf66595564edd8bd9b3d6b10714f3af0` includes cohesive tier-binding `b2b24e9eaf065dd450e51122f792e96e8ae71170`. Reviewed terminal interrupt `bc62530e9b079b35ddaebccbb4374c713d6a4602` and graceful shutdown `9985d2e8f22d73ae80026efbbba1fa84aef76f27` are now integrated. The active input-drain successor remains excluded.

The only conflict was RPC Program.cs. Its direct-await condition now includes queue publication, all queue restoration cases, and both Responses checkpoint cases. All registrations remain. Eleven other changed paths equal the provider tip exactly. Prior core/terminal checked blobs and fourteen held-I/O pins remain unchanged. The adjacent JSON inventory records source checks, not runtime evidence. Provider-authored coverage remains 29 groups; no new tests were authored by this merge. Oversized signature variants reject at output_item.done before EOF/read/completed: they do not demonstrate those later paths.

## Proposed bounded batch (not executed)

One preparation pass followed by one serial native gate pass, no automatic retries, no new features, no live endpoints. Use the final documentation successor commit containing this plan as the candidate, not the older source-merge identifier. Resolve its exact commit/tree before creating any immutable manifest. All historical manifests, seals and results stay untouched.

1. Provider ownership and Windows file-sharing corrections are independently statically closed per lead report. The lead also reports the remaining combined production/test composition statically clear. The launch-host correction still requires review. Obtain explicit lead-owned build AND test window authorization with runtime owner and expiry. A build allocation is not test permission. No SDK probing or execution before authorization. The selected SDK policy is 10.0.401 with rollForward disabled.
2. Create a fresh complete source-closure manifest for the exact final candidate, tree and this absolute checkout root. Use existing source-closure admission rules; include the unchanged held-I/O declaration and required source files. Supply a separately approved immutable runtime allocation for `locked-offline-build-publish`, pinning the actual dotnet executable. Do not reuse another candidate's allocation/receipt or fabricate successful steps.
3. Invoke the existing preparation tool once with the arguments below. It performs SDK version/info observations, one locked offline solution restore, one Release solution build and ten fixture restore/publish pairs, including StatefulTodo and SessionCheckpoint (24 original preparation steps total: four solution/SDK steps plus twenty fixture steps). All original children must settle; fail/stop on preparation or admission failure. Keep its fresh actual receipt and logs. No package-policy workaround or online restore fallback.
4. Under the separately authorized test window, invoke the existing native gate once with the fresh matching manifest and actual receipt. It validates prepared outputs without rebuilding, checks each launch boundary, removes Node from test PATH, runs ten core runners serially, then all 26 registered companions. Stop on the gate's failure; do not retry or continue manually. All reports must use this fresh checkout's previously absent runtime-output paths. Preflight that `artifacts/native` and `artifacts/transports` contain no historical reports; if they do, stop and arrange a fresh admitted checkout rather than overwriting them.

Preparation invocation template (values must come from real authorized artifacts):

```powershell
& "$Repo/tools/prepare-native-products.ps1" -Repo $Repo `
  -SourceManifest $SourceManifest -SourceManifestSha256 $SourceManifestSha256 `
  -ApprovedRuntimeAllocation $ApprovedRuntimeAllocation `
  -ApprovedRuntimeAllocationSha256 $ApprovedRuntimeAllocationSha256 `
  -EvidenceDirectory $FreshPreparationDirectory
```

Test invocation template, only after successful preparation and test authorization:

```powershell
& "$Repo/tools/test-native.ps1" -Repo $Repo `
  -SourceManifest $SourceManifest -SourceManifestSha256 $SourceManifestSha256 `
  -PermittedBuildReceipt $ActualBuildReceipt `
  -PermittedBuildReceiptSha256 $ActualBuildReceiptSha256
```

`$Repo` is `P:/PiSharp/root/Documents/Codex/2026-10-03/task-4/PiSharp-terminal-shutdown-integration`. No invocation above has run. The receipt emitted by preparation is `actual-permitted-build-receipt.json` in the fresh preparation directory. Its hash must be computed from actual returned bytes.

## Fixed coverage and ownership limits

The ten core runners, in existing gate order, are Compatibility, Agent, Transport, Sessions, Tools, CodingAgent, RPC, Extensions.ContractTests, ExtensionHost and Tui. This includes provider checkpoint and tier binding; nested tool host/usage; request context and before-start; custom-tail reopen/retry; atomic queue races and Windows shared reading; actual queue restoration, publication, interrupt and graceful shutdown. The existing terminal select-dialog companion contains the reviewed 77-unit consumer plan. Full runners avoid unsupported filter assumptions and retain adjacent regression coverage. CodingAgent receives the actual dotnet host and prepared CLI DLL paths from the gate.

The 26 companions are exactly `tools/native-companion-targets.json`, including Google, Kitty alternate/held-I/O, terminal keybindings, standalone select list and select-dialog integration. Keep their declared fixture hashes, order and argument shapes. The four admitted terminal consumers require fresh manifest/receipt paths and SHA256 values plus reviewed root; the gate supplies these. Do not launch their DLLs without those arguments.

Bounds are one preparation plus at most 36 runner launches, serial, no retry. This is a workload bound, not a guaranteed wall-clock duration: direct awaited tests and owned cleanup can outlive observation deadlines. The launcher now selects the host from the hash-pinned actual receipt and rechecks selected path and host size/hash at each launch boundary. The current core gate has no global deadline. Before authorization the runtime owner must agree how an expired window stops further admission and retains any running original process/task until settlement; do not wrap it in a timeout that detaches work. Companion deadlines and cleanup behavior remain as implemented; an incomplete join is nonpassing and blocks later admission. Do not claim the batch fits a fixed duration without execution evidence.

This batch does not execute upstream Pi, live providers, or the separate opt-in strict Completions lifecycle gate. It cannot close those distinct acceptance obligations. Pinned upstream semantic baseline remains Pi v0.99.1 `d86654abb8862e201933517d6f1fce9f88dd117f`; source inspection is distinct from differential execution.

Record actual commit/tree/root, host and SDK identity, permission artifact hashes, source/product pins, every original child exit/join, report hashes, failed/unrun groups and any incomplete cleanup. Never label authored tests executed or source checks compiled. Package/phase acceptance remains open.

Host-binding correction controls are authored separately in `tools/test-native-launch-host-admission.ps1` (ten controls; zero executed). A future separately authorized PowerShell control invocation requires `-Repo`, `-SourceManifest`, `-SourceManifestSha256` and a fresh `-EvidenceDirectory`. These use inert text hosts/receipts, never actual preparation proof, and add no native runner launches. They remain outside the 36-runner native workload count.

Current terminal source integration is recorded in [terminal-shutdown-integration.md](terminal-shutdown-integration.md). Its fourteen held-I/O pins include the reviewed interrupt/shutdown migrations; retained historical declarations are preserved. The original unchanged-pins observation above describes the earlier provider source merge. Use the current declaration and fresh candidate for admission.
