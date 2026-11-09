# PKG — local package admission preparation

Status: source preparation complete; archive/runtime validation pending I13. No package was built, restored, installed, published or executed by this worker. No version bump or commit. Requested model/effort: gpt-6.1-sol/high; Ultrafast requested, effective tier unverified.

## Changes

- Explicitly enabled library packability for Chord, Chord.Hosting, Durable.Store, Env.Protocol and Env. Explicitly disabled library packability for Env.Daemon; its NativeAOT artifact workflow remains separate.
- Added assembly-level `PISHARP_CHORD001` to Chord.Hosting and `PISHARP_ENV001` to both Env libraries. Updated Env implementation/test project diagnostic suppression only; no runtime code changed.
- Extended the release library list from 23 to 28 libraries (29 packages including CLI), the shared packaging target, package-ID validation, exact dependency maps, descriptions and required admission document. Chord.Hosting requires exactly the ASP.NET Core shared framework; the other newly admitted packages permit no unreviewed framework reference.
- Kept exact-version Contracts/Chord friend-assembly implementation coupling deliberately. Internal schema engine/value types remain internal; public APIs do not gain those types. Updated current csproj/ChordNext/assembly comments and third-party notices, preserving original source constants and published historical release notes.
- Added `docs/contracts/gap-package-admission.md`, an unreleased admission note, README/releasing guide links and package-count updates. The five packages embed the admission document alongside root MIT/license notices and exact-source provenance.
- Added synthetic archive controls in `tests/packaging/gap-native-sdk-package.tests.ps1`: valid shape for all five IDs, exact sibling range enforcement, wrong hosting framework refusal, required admission document, explicit daemon exclusion. Synthetic archive success never claims package consumption or release acceptance.
- Added a real package-only external consumer fixture in `tests/packaging/GapConsumer`, and `tools/release/test-gap-package-consumer.ps1` to optionally invoke packing, validate real archives, copy the consumer outside the checkout, restore into a fresh cache, check required experimental diagnostics, compile with explicit opt-in and run public API scenarios. NuGet source mapping restricts every PiSharp dependency to the supplied local feed. There are no ProjectReferences or copied library implementations.

## Consumer coverage and evidence contract

The external fixture calls public APIs from all five packages: Env frame encode/decode and platform mapping; Hosting signed-cookie roundtrip; real byte-bearing Chord facet/host invocation; actual SQLite StoreStorage open/close; public signature visibility across all admitted assemblies. It requires transitive dependencies from the produced packages, including the ASP.NET shared-framework declaration. Deeper plugin/process/HTTP/remote/native suites remain separately required.

The runner uses the same `artifacts/gap-validation/build.lock` as I13's existing helper, computes before/after source fingerprints over all five project-reference closures plus packaging inputs, and retains per-stage logs, source manifests, restored assets, package SHA-256 and a receipt. Source or package changes during validation refuse final success. It preserves the external work directory for investigation. Only successful validation sets `packageConsumptionProven=true`; `releaseAccepted` is always false.

## Verification performed

- PowerShell AST parsing passed for the three changed/new release scripts and synthetic archive test script.
- XML parsing passed for all six project files, the packaging target and external consumer template.
- Scoped tracked-file whitespace check passed. A separate broad worktree check found whitespace in unrelated in-flight lifecycle files; those were left to their owner and are not PKG failures.
- No synthetic archive tests, .NET compilation, package builds or external consumer runs were executed here.

## I13/root execution handoff

After the shared source is stable, run the synthetic archive checks and the actual consumer gate. The latter owns the shared build lock itself; do not wrap it in a second holder of that same lock.

```powershell
pwsh -NoProfile -File tests/packaging/gap-native-sdk-package.tests.ps1
pwsh -NoProfile -File tools/release/test-gap-package-consumer.ps1 -SourceCommit <current-40-char-sha> -Version 1.5.0 -Pack -DotnetPath E:/AI/PiSharp/PiSharp/artifacts/sample-tools/dotnet/dotnet.exe -OutputDirectory artifacts/package-consumer-unique
```

Use `-PackageDirectory <already-prepared-local-feed>` without `-Pack` to consume previously packed artifacts. The script still validates all five against the matching Release assembly and source provenance. Output directories must be fresh. The version shown is the unchanged baseline, not permission to publish it or overwrite the existing NuGet release.

I13 should run the full existing/new runtime suites after metadata integration as well. The external consumer is a bounded distribution gate, not a replacement for full feature tests or platform artifact qualification.

## Files owned

Project metadata: `src/PiSharp.{Chord,Chord.Hosting,Durable.Store,Env.Protocol,Env,Env.Daemon}/*.csproj`; new experimental marker files in Hosting/Env.Protocol/Env; Env test csproj suppression. Current ChordNext/experimental metadata and Contracts SchemaInternals comment only.

Packaging: `tools/release/{build-release-packages.ps1,PiSharp.NativeSdk.targets,native-sdk-package.ps1,test-gap-package-consumer.ps1}`; `tests/packaging/{GapConsumer/*,gap-native-sdk-package.tests.ps1}`.

Docs: `docs/contracts/gap-package-admission.md`, `docs/release-notes/gap-admission.md`, README, THIRD-PARTY-NOTICES, Chord.Hosting README, narrow releasing-guide package counts, this report. No CI workflow or compatibility inventory changed.

## Remaining corpus boundary

J1 and X1 both confirmed the full `next/schema/recorded.json` tables/jobs/real-host stored-owner proof was not their scope. This needs the explicit S1 assignment described in packaging-readiness.md (or root's subsequently assigned owner). Current standalone job fingerprint and real-host examples are not substitutes for the complete historical schema-switch corpus. Package preparation makes no claim that this gate is complete.

## Shared native containment dependency follow-up

Root approved direct Chord → Tools and Chord.Hosting → Tools references, implemented by LG3/N1 respectively, for the shared public `OwnedChildProcess` facade. Updated both exact dependency validator maps, synthetic archive maps and missing-Tools rejection controls. The external consumer now compiles against the transitive facade and requires Tools/Agent/AI in the restored same-version package graph. Admission/release docs and current notices describe the larger managed footprint and deliberate single Windows-job/Unix-group implementation. No Durable edge, cycle, additional package ID, provider call or implicit process launch is introduced. The release count remains 28 libraries plus CLI. Source/metadata checks only; all build/pack/runtime qualification still belongs to I13.

## Consumer reflection correction after actual package run

I13's `artifacts/package-consumer-final-8986c325647347f6b3fdff71300b3f7f` run built/validated 29 packages, restored the external consumer, verified experimental diagnostics and built opt-in with zero warnings. Its functional public protocol/Env/Hosting/byte-schema host/SQLite checks reached the reflection stage, which then incorrectly rejected ChordStore's legitimate internal implemented `IChordStore` interface. This is a consumer-gate false positive: reflection returns nonpublic implementation interfaces even though they are not public API signatures.

Narrowly changed interface enumeration to visible interfaces only. Public bases, methods, constructors, properties, fields and events still check their signature types recursively without that filter. Exact controls retain the real IChordStore as internal, verify public IStore stays in the checked set, and require hidden direct/array/generic signature positions to be rejected. No engine or product API visibility changed. Correction is source-only; I13 owns the no-Pack external consumer rerun and final stable-source receipt. The earlier run remains failed and is not relabeled accepted.
