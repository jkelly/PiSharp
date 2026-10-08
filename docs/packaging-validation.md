# Phase 8 distribution source handoff

**Qualification update, 4 October 2026:** The [Windows x64 disposable tool-path record](evidence/windows-x64-disposable-tool-smoke.md) adds a separately allocated 7/7 local pack/install/help/invalid smoke above the passing native baseline. The source-only lane statements below are historical; wider release and platform checks remain open, including clean-machine and standalone qualification. R7 failed restore evidence remains preserved.

This implements source for original P8-02, P8-07 and P8-08. It does not close their gates or any of the original 79 work packages. Base: `7452a7b2a355e1f603a31eb693def91a9f565df1`. Upstream stays Pi **v0.99.1**, `d86654abb8862e201933517d6f1fce9f88dd117f`; SDK stays **10.0.401** with roll-forward disabled. PiSharp's release version is independent of Pi's version.

## Ownership and integration

No shared project, solution, native preparation, admission or test-gate file is changed. No native process, package restore, build, publish, tool install, CLI invocation or authored fixture suite was run in this lane. PowerShell parser inspection and XML parsing are static checks, not test execution. Native execution is owned by the separate build/test coordinator.

The coordinator must first admit the additional targets/provenance files and authorize packaging operations in its execution allocation. The current `locked-offline-build-publish` allocation must **not** be interpreted as permission to pack, install or run these checks. Existing native admission/preparation scripts must continue unchanged. Packaging inputs and intermediate files must be included in the coordinator's source/generated input closure. Report the required integration to the gate owner before scheduling it.

The opt-in [targets](../tools/packaging/PiSharp.Distribution.targets) can be imported by an approved invocation of the existing CLI project through MSBuild's `CustomAfterMicrosoftCommonTargets` property. Only the CLI project receives packaging properties. No import has been added to shared project files; adding a permanent import requires coordination with their owners. Do not replace `DirectoryBuildPropsPath` or `DirectoryBuildTargetsPath`, or bypass source admission.

Required candidate inputs:

| Input | Requirement |
| --- | --- |
| `CustomAfterMicrosoftCommonTargets` | Absolute path to the admitted distribution targets |
| `PiSharpReleaseVersion` | Explicit candidate SemVer, for example `0.1.0-preview.1`; package ID availability is unverified |
| `PiSharpSourceCommit` | Actual admitted candidate's 40-character lowercase Git commit, not this lane's base by default |
| `PiSharpProvenanceFile` | Absolute path to an admitted UTF-8 JSON document described below |
| `EnablePromptTemplateYaml` | Default `true`; set `false` consistently for disabled CLI restore, build, pack/publish and validation |
| Standalone publish properties | `RuntimeIdentifier` in `win-x64`, `linux-x64`, `osx-arm64`; `SelfContained=true`, `UseAppHost=true` |

Tool pack is framework dependent; standalone publish is self contained. Use explicit `PublishTrimmed=false`, `PublishSingleFile=false`, `PublishAot=false` in both invocations. These variants preserve the JIT/plugin contract. Restore only from the coordinator's admitted offline SDK/runtime packs with existing lock files, followed by `--no-restore` operations. A missing offline runtime pack is a blocker, not permission to acquire or bypass a denied package. The targets are opt-in source, not an alternative native execution gate.

Example provenance **shape**, to be populated from admitted source/build inputs rather than copied as evidence:

```json
{
  "schemaVersion": 1,
  "sourceCommit": "<actual admitted candidate SHA>",
  "version": "0.1.0-preview.1",
  "baselineTag": "v0.99.1",
  "baselineCommit": "d86654abb8862e201933517d6f1fce9f88dd117f",
  "sdk": "10.0.401",
  "kind": "tool",
  "enablePromptTemplateYaml": true,
  "rid": null
}
```

Use `kind=standalone` and the matching RID for each standalone archive. The targets carry `provenance.json`, `LICENSE`, `THIRD-PARTY-NOTICES.md`, and `README.md` into package/publish output. Standalone ZIPs must put the publish files directly at archive root; no extra enclosing directory. Preserve Unix executable modes using a ZIP writer that supports them. Do not bundle the optional Node bridge, Node/npm, reference runner, test fixtures, credentials or user data.

## YAML feature variants and alternate CLI lock

The default CLI uses the optional frontend adapter and exact YamlDotNet **16.3.0**. Core and RPC remain package-free. Default CLI distributions must contain `PiSharp.PromptTemplates.Yaml.dll`, `YamlDotNet.dll`, and the byte-identical retained `licenses/YamlDotNet.LICENSE.txt`. The validator checks the parser's exact version/content hash against the admitted normal CLI lock and checks the selected `lib/net8.0/YamlDotNet.dll` runtime asset in `.deps.json`. This is artifact structure/identity validation, not execution or redistribution acceptance.

With `EnablePromptTemplateYaml=false`, only the CLI selects `src/PiSharp.Cli/packages.prompt-yaml-disabled.lock.json` through NuGet's [custom lock-file property](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#lock-file-extensibility). Referenced projects keep their normal locks. Do not pass a global `NuGetLockFilePath`/`--lock-file-path` that could redirect every referenced project's lock to one shared file. Do not disable the feature for the full solution or claim that the YAML test projects become package-free.

The alternate lock is **not generated in this source-only patch**. Disabled locked preparation remains blocked until the coordinator generates, independently reviews and admits that file. Do not remove YAML entries by hand, replace normal locks, or normalize the protected PiMessages lock. Normal committed locks and protected physical lock bytes must remain unchanged.

Coordinator-owned steps, after explicit allocation (these commands are instructions, not executed evidence):

1. Create a separate fresh lock-generation checkout of the reviewed source candidate. Pin SDK 10.0.401, root build configuration, the admitted clear-only `NuGet.Config`, isolated package cache and original finite process owner. Inventory hashes of **all existing lock files**, including the protected PiMessages physical lock, before admission. Require an absent alternate CLI lock and no existing `obj`/`bin` products. The lock-generation checkout is writable generated-input work, not an immutable runtime source lease.
2. Invoke exactly the CLI project, with recursive dependency restore disabled for this one generation step:

   ```powershell
   dotnet restore src/PiSharp.Cli/PiSharp.Cli.csproj `
     --no-dependencies --use-lock-file --force-evaluate --disable-parallel `
     --configfile <admitted-clear-only-NuGet.Config> --packages <isolated-cache> `
     -p:EnablePromptTemplateYaml=false -p:RestoreLockedMode=false `
     -p:DirectoryBuildPropsPath=<admitted-root-Directory.Build.props> `
     -p:DirectoryBuildTargetsPath=<admitted-root-Directory.Build.targets>
   ```

   The project selects its alternate path locally. NuGet evaluates project dependency metadata, but `--no-dependencies` limits lock generation/restoration to the CLI root. Do not use this generation command as the subsequent complete-graph preparation. If this exact SDK cannot generate the expected closure without writing another lock, stop and preserve evidence for review rather than broadening the command.
3. Join the original process and all output/descendant ownership before inspecting results. Require successful exit, exactly one new lock, no existing lock byte changes, and no source changes. Verify the alternate lock contains the complete expected native CLI project closure and **no YamlDotNet or PiSharp.PromptTemplates.Yaml dependency**. Review and commit only the coordinator-generated alternate lock on the isolated source branch; retain generation logs and failed attempts outside repository source. Refreeze the resulting commit and admission manifests.
4. Prepare two independent fresh roots from that refrozen commit: default YAML-enabled and YAML-disabled CLI-only. Use the existing complete-graph locked restore/build owner and admitted configuration. Default preparation uses the verified local 16.3.0 package/feed/cache; disabled preparation uses clear-only feeds and matching SDK reference packs. For disabled restore use the CLI project, `--locked-mode`, `-p:EnablePromptTemplateYaml=false`, the explicit root build configuration paths and isolated cache. **Omit `--no-dependencies`** so the existing native reference closure is prepared and checked with its unchanged locks. Build with `--no-restore` and the same feature property. Never share variant `obj`, `bin` or published outputs. Confirm both restores leave every admitted lock unchanged.
5. Allocate pack/install/publish separately through the existing distribution targets and original owner. Use the same feature value in restore/build, candidate provenance, packaging and validator. RID-specific standalone/runtime-pack locks need a separate exact admission if their graph differs; this alternate lock is for the framework-dependent CLI graph, not permission to rewrite it for an unreviewed RID.

Enabled archive validation is the default. Disabled artifact validation must explicitly pass `-EnablePromptTemplateYaml:$false` and supply provenance with the Boolean `enablePromptTemplateYaml: false`. Missing, string-valued or mismatched provenance fails. Disabled archives must have no YAML parser/adapter binaries, parser notice, library identities or target entries, including stale files from an enabled build. The wrapper forwards the feature expectation to both original and repeat artifacts.

Acceptance requires both fresh locked graphs, unchanged normal/protected locks, the authored positive/negative archive cases, actual tool payload inspection, and separate real CLI smoke on the two variants. Plain templates must still work with YAML disabled; nonempty YAML frontmatter must retain the explicit warning/skip policy. Existing command/input, RPC model/thinking, completion and lifecycle regressions remain required where their fixtures do not need YAML metadata. Synthetic DLL placeholders cannot establish these runtime behaviors. Reuse the existing packaging suite and coordinator framework; no alternative packaging gate is introduced.

## Offline validation and repeatability

After explicit execution assignment, the coordinator may run the authored suite:

```powershell
& ./tests/packaging/distribution-validation.tests.ps1 -Repo <admitted-root>
```

It creates only synthetic archives under a unique temporary directory and validates metadata/failure paths. Its placeholder DLLs deliberately establish archive structure only; they do not establish executable assembly validity.

Validate a real artifact using an independently retained archive checksum and source identity:

```powershell
& ./tools/packaging/validate-distribution.ps1 `
  -Artifact <candidate.nupkg> -ExpectedSha256 <independent-sha256> `
  -Repo <admitted-root> -Kind tool -Version <candidate-version> `
  -SourceCommit <admitted-source-sha> -RepeatArtifact <second-clean-candidate.nupkg>
```

For a standalone ZIP use `-Kind standalone -Rid <rid>`. Retain the JSON report outside immutable source inputs. The report includes archive SHA-256, every uncompressed file's SHA-256/size/mode, candidate/source/baseline/SDK metadata, and `.deps.json` library identities. Dependencies listed by `.deps.json` are an inventory seed, not a complete SBOM.

The validator reads ZIP entries without extraction, installation, network or subprocesses. It rejects ambiguous/escaping paths, symlinks, file/directory collisions, case collisions, missing native assemblies, Node payload/dependency declarations, mismatched documents/provenance/package metadata, wrong tool commands, and missing self-contained runtime payloads. XML DTD resolution is prohibited. Bounds are 20,000 entries, 512 MiB per uncompressed file, 2 GiB aggregate, and 4 MiB for metadata text. These bounds do not constitute a general hostile-archive security qualification.

Build two unsigned candidates in independent clean admitted roots with the same SDK/runtime packs, source, version and metadata. Run structural validation on both, then compare all payload paths, bytes, hashes and Unix modes. ZIP timestamps/compression can differ: `archiveBytesMatch` records that separately. Embedded package-core metadata, PDB paths, source links or generated timestamps are payload inputs; differences fail the comparison and need investigation. Never delete differing entries or normalize their contents to manufacture a match. The repeat check cannot independently prove that its inputs came from two clean builds; retain the coordinator's build evidence with it.

## Remaining release evidence

The expected checksum must come from the coordinator's retained candidate manifest; a hash calculated from the same untrusted download supplies no authenticity. Embedded provenance is a consistency check, not a signature or attestation. SDK claims must be backed by build receipts. No package signing or publishing identities are set up here.

License declarations alone do not close redistribution review. Repository document bytes and pinned Mario Zechner attribution are checked. The existing [license evidence matrix](compatibility/license-evidence-matrix.md) remains HOLD. Review every actual runtime pack/native binary/NuGet component, retain all vendor license/notice files in the publish output, and produce an artifact-specific SBOM before release. The report explicitly leaves license closure on HOLD; no third-party license approval is inferred.

The coordinator must separately verify real assemblies and clean-machine local install, `pisharp --help`, offline demo, update/side-by-side rollback and uninstall, native plugin load/dependency resolution, and absence of Node/npm on each declared platform. Tool installs must use only the isolated local candidate feed and leave normal global tools/user sessions untouched. Standalone runs need a machine with no preinstalled .NET runtime. Record OS/terminal/runtime identities and exit/output/process cleanup evidence. Preserve sessions/credentials throughout upgrade/rollback. These runtime checks remain **unrun**; this handoff makes no platform, parity, release or publication claim.

## Static evidence for this lane

The three authored PowerShell files parse without errors, the targets file parses as XML, and `git diff --check` reports no whitespace errors. The fixture suite is authored and unrun. No live/paid API calls, network acquisition, native execution, signing, credential/security changes or publication occurred.

Independent source review of initial commit `f33876769dde032740200bcb458c98c6021afe7c` identified three validation gaps. The correction checks case-insensitive file ancestors across both files and explicit directory entries, independent of archive order; requires `hostfxr.dll`, `libhostfxr.so`, or `libhostfxr.dylib` in the matching standalone RID; and requires the Unix CLI's owner execute bit (`0100`), rather than accepting group/other execute alone. Twenty-three additional authored fixtures cover collision ordering/casing, valid directory chains, complete/missing loaders for all three RIDs, and missing owner execute modes for both Unix RIDs. Existing checks remain in place. Static PowerShell parsing and whitespace checks passed after correction; these fixtures remain unrun pending coordinator allocation.
