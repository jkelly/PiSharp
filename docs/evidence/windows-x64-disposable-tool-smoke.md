# Windows x64 disposable tool-path milestone

On 4 October 2026, the coordinator qualified candidate `ef4a50c130629b2a2089e0a921f3135c3351a5cc`, tree `69a690fda1cc9cc645324166852fdf1aa83554c8`, on Windows x64 with SDK **10.0.401** and runtime **10.0.12**. The separate full native baseline passed **1,651/1,651** core groups. The unchanged original selector inventory completed **140/140** observations in original order, with zero assertion failures, exclusions or unexecuted cases. These are native offline observations; they do not establish upstream differential parity.

R8 passed **7/7** disposable local tool stages: SDK selection, locked CLI restore, Release build, tool pack, private local-feed installation, installed shim help (exit **0**), and invalid arguments (exit **2** with the expected structured error). The sole package was `PiSharp.Cli.0.1.0-smoke.ef4a50c1.nupkg`, a framework-dependent smoke artifact. Its **12 DLLs** matched actual build bytes; all **31 installed payload files** matched the archive's membership and bytes. The generated shim was separately pinned. Fresh source admission, private empty NuGet machine settings, and original process/descendant/output joins were verified; no native hosts remained. No prepared product outputs were copied into the fresh checkout.

This qualification covers one disposable tool path on this Windows host. It is not a released package, clean-machine qualification, standalone distribution result, or completion of Phase 8.

## Retained evidence

The following identifiers refer to retained coordinator artifacts outside the repository. Locate them by identifier and verify SHA-256 before relying on them; local absolute paths and host/account details are deliberately omitted here. The controls/admission handoff precedes the separately allocated smoke run.

| Evidence identifier | SHA-256 |
| --- | --- |
| `NATIVE_FULL_BASELINE_HANDOFF_ef4a50c1.json` | `7bb7fdef97ee779704b7b1705fc54d7a1fa6a2107450d8ec207796705ca0a730` |
| `R8_CONTROLS_AND_ADMISSION_HANDOFF.json` | `3a785382d73e4b9512680257453483af631cb95a80e414edd1d3393989e22124` |
| `R8_LOCAL_SMOKE_QUALIFICATION_HANDOFF.json` | `985135aa19464e8c84f83569eda6a5224167585e5c9ce01ddef78f241e47bcb8` |
| `R7_LOCAL_SMOKE_FAILURE_HANDOFF.json` | `4ebec3c6097e55997efa9edd99b73dbea6e3ef1af96ef495a513646ec8242dab` |

The smoke package SHA-256 is `95722308e252fcfba3f0a77787f9425ce25168c60192dc1f7945120496b7cc9a`; this identifies the retained local artifact and conveys no release authenticity or publication claim. R8's reviewed `REVIEW_SEAL.json` is `cd9dca481e40bfacd561814cc35130b03f1dbc53f89e139d4faa2f808525e04b`; its `run-local-smoke.ps1` coordinator is `c1c9a8b18499a2c382341f3a4578e3e86bddb661c27251e5ce1146133c898d0f`.

## R7 failure remains part of the record

R7 passed SDK selection, failed locked restore, and left five stages unexecuted. NuGet's `ConfigurationDefaults` initializer failed with null `path1`: the private environment omitted both `PROGRAMFILES(X86)` and its `PROGRAMFILES` fallback. Exact pinned NuGet/runtime IL identified that Windows machine-settings discovery dependency. All original owners were joined and the failed logs/allocation/admission were preserved.

R8 added one explicit `PROGRAMFILES(X86)` value pointing to an empty task-owned machine-settings root, with only `NuGet/Config` directories and guards before/after stages. It retained the same six named inherited system values and did not inherit ambient machine configuration. Independently allocated environment/filesystem controls preceded R8's fresh smoke allocation. R8's success is a separate result; it does not convert R7's failed restore into a pass.

## Reproducing the bounded observation

1. Obtain the retained reviewed R8 plan/coordinator/input bundle and a new coordinator allocation. Admit a fresh checkout of the exact candidate plus its explicitly admitted provenance/config inputs through the existing [source-closure checks](../../tools/native-source-admission.ps1). Bind actual SDK/runtime, qualified owner, source and harness hashes in the allocation. A historical allocation or successful baseline does not authorize a new packaging run.
2. Use the reviewed private environment and fresh owned home/cache/temp/feed/tool/working directories, including the empty machine-settings root. Keep the six named system reads and explicit owned overrides. Restore the CLI graph in locked mode from admitted offline inputs, then build Release; retain generated-input and product receipts. Do not reuse the previous smoke's outputs.
3. Pack with the opt-in [distribution targets](../../tools/packaging/PiSharp.Distribution.targets), explicit candidate version/source/provenance and untrimmed, non-single-file, non-AOT properties. Use an owned NuGet config whose `packageSources` clears inherited sources and names only the private local feed. Install the exact candidate using `--tool-path` and that config; no remote feed is part of this procedure.
4. Use the existing [distribution validator](../../tools/packaging/distribution-validation.ps1) and the reviewed coordinator's build/archive/installed-byte checks. Invoke the installed shim in an empty owned working directory for help and invalid arguments. Retain raw logs and exact exit/output evidence. The coordinator stops admission at the first nonpass and joins the original run, process/job/descendants and both output owners before returning; deadlines remain nonpassing outcomes.

This describes the reviewed procedure, not a portable turnkey runner bundled in this repository. See [packaging validation](../packaging-validation.md) for provenance and the wider distribution matrix. Repetition requires fresh review/admission/allocation and its own retained evidence.

## Gates still open

Release acceptance, phase acceptance, original-provider/source parity and immutable JSON compatibility remain open. P1 remains **HOLD** and the alternate compiler/provider work remains held. No live API behavior or credentials were exercised. Linux/macOS, standalone self-contained archives, machines without preinstalled .NET, other Windows environments, the broader terminal/plugin matrix, offline demo, update/rollback/uninstall and independent repeat-build reproducibility remain unqualified by this smoke. Signing/publication, license/redistribution closure, artifact SBOM and hosted CI acceptance also require their own evidence. No phase completion percentage or original work-package acceptance changes follow from these counts.
