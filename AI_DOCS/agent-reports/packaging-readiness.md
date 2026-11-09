# Packaging and schema corpus readiness

Read-only follow-up, 2026-11-08, in `E:/AI/PiSharp/gap-implementation`. Only this report is edited. No build, restore, pack, publish, native execution or source change. Requested model/reasoning: gpt-6.1-sol/high; requested Ultrafast, effective tier unverified. Current working files are concurrent implementation evidence, not final package acceptance.

## Recommended admission decision

Prepare **five experimental library packages** for admission: `PiSharp.Chord`, `PiSharp.Chord.Hosting`, `PiSharp.Durable.Store`, `PiSharp.Env.Protocol`, `PiSharp.Env`. Keep `PiSharp.Env.Daemon` **explicitly nonpackable as a NuGet library** and distribute its independently verified, single-executable NativeAOT outputs through the existing daemon catalog/artifact mechanism. Admission preparation is not authorization or evidence of publication. If root keeps any library repository-only, record that decision per package and row rather than calling the full distribution work complete.

The full implementation request allows preparing these artifacts and testing outside consumers. It does not require immediately publishing them, changing the baseline version, or including the daemon in the CLI tool. Preserve the current release target until root deliberately selects the release version.

## Current project state and dependency closure

| Project | Current pack state | Direct project/framework dependencies | Required admission work |
|---|---|---|---|
| Chord | `IsPackable=false`, csproj:8; assembly `PISHARP_CHORD001` | Contracts, Telemetry | Admit library metadata, exact sibling graph, API boundary decision, external consumer and protocol/host corpus gates. |
| Chord.Hosting | `IsPackable=false`, csproj:3; no assembly Experimental attribute found | Chord; `Microsoft.AspNetCore.App` FrameworkReference | Add experimental assembly/API designation, deliberate ASP.NET shared-framework requirement and framework-reference package validation, plugin/worker/HTTP external consumer. |
| Durable.Store | `IsPackable=false`, csproj:8; assembly `PISHARP_DURABLE001` | Durable, Chord, Durable.Sqlite | Admit after Chord; expose/store/registry consumer gate and exact transitive SQLite asset restoration. |
| Env.Protocol | SDK-only project; default NuGet packability applies; no release metadata or experimental designation | No project dependency | Explicit pack intent, new `PISHARP_ENV001` (or documented chosen ID), library metadata and protocol surface consumer. |
| Env | No `IsPackable` setting; default library packability; no experimental designation | Env.Protocol, Durable | Same intent/metadata/diagnostic; document actual dependency graph (no direct Chord reference), client/adapter consumer and independent daemon catalog. |
| Env.Daemon | Executable; no `IsPackable` setting, so no explicit exclusion | Env.Protocol, Durable, Durable.Tools; native Unix shim imported | Set explicit nonpackable intent; NativeAOT release artifact contract, license bundle, per-target executable verification; do not treat an ordinary library `.nupkg` as a deployed daemon. |

Library source projects currently inherit ordinary SDK metadata outside the release invocation. `tools/release/build-release-packages.ps1:18-29` still lists the existing 23 libraries only. `PiSharp.NativeSdk.targets:4` and `native-sdk-package.ps1:7-10` have the same fixed allowlist. Therefore toggling `IsPackable` alone will not add these packages to the reviewed release pipeline.

## Precise packaging changes to prepare

1. Add the five library IDs to the release list, opt-in native SDK target condition, validation `ValidateSet`, exact `$siblings` map, and per-package descriptions. Dependency order: Contracts/Telemetry and existing Durable dependencies first; Chord and Env.Protocol next; Env and Durable.Store after their siblings; Chord.Hosting after Chord. Existing script expects `$libraries.Count+1` packages automatically, but hardcoded documentation/package inventories and tests must be reviewed.
2. Preserve exact sibling version ranges using `PinPiSharpSiblingVersions`. New expected maps:
   - Chord: Contracts, Telemetry.
   - Chord.Hosting: Chord; separately validate `Microsoft.AspNetCore.App` framework reference rather than inventing a NuGet dependency.
   - Durable.Store: Durable, Chord, Durable.Sqlite.
   - Env.Protocol: none.
   - Env: Env.Protocol, Durable.
3. Carry the existing MIT license, root notices, README and per-SHA provenance through the shared target. Add descriptions that identify experimental status, required diagnostic suppression, native/managed boundaries and actual runtime requirements. The package validator currently permits only one exact managed DLL plus listed metadata/license files; extend it deliberately for package-specific README/license additions instead of weakening the archive allowlist globally.
4. Add assembly-level experimental attributes for Chord.Hosting and the Env libraries, or document an explicit alternative. Current Chord/Store assembly attributes do not mark independently declared Hosting/Env types. Merely suppressing `PISHARP_CHORD001` in a project's `NoWarn` is not a public experimental designation.
5. Keep the six native daemon outputs outside these library packages unless a separate runtime-asset design is accepted. `tools/Env/Publish-NativeDaemon.ps1` already builds NativeAOT, copies the single executable, records SHA/RID and invokes a framed-protocol smoke plus published Unix terminal suite. It is an artifact producer, not proof that every target was produced. Record OS/architecture, source fingerprint, SDK/toolchain, binary/hash, stage results and explicit unsupported/unexecuted targets. Retain Windows terminal `ENOTSUP` rather than pretending to ship a Windows PTY implementation.
6. Use a clean output directory for package/admission tests. Existing release package count rejects stale additional `.nupkg` files but does not itself demonstrate external consumption.

## Chord schema dependency: architectural decision, not an assumed linker failure

Chord currently reaches Contracts internals through `src/PiSharp.Contracts/Properties/SchemaInternals.cs:10`. `src/PiSharp.Chord/Data/Validator.cs:19,50,77-94` uses `SchemaValidator`, `SchemaOptions`, `Js`, `JsObject` and `JsArray` so byte-array leaves are validated without JSON serialization. The new F09 public `JsonSchemaBuilder`/`Validate` APIs do not remove this coupling.

Exact-version sibling packages can technically retain this friend-assembly relationship; it is not automatically an external-consumer compilation failure. The existing nonpackable comment explicitly requires a public experimental API first, so root should choose one of:

- **Preferred:** add a narrow public experimental value-validation boundary in Contracts that supports recursive JsonNode/object values with byte leaves and missing-versus-null semantics. Chord uses it without exposing `JsObject`, compiler/resolver types, or normal-form implementation types. Preserve strict noncoercing validation, error paths, closed objects, `bytes`, and the chosen numeric semantics. Run the same Chord data/argument corpus before/after.
- **Explicit exact-version coupling:** document that Chord and Contracts ship together with friend access under exact package versions and the internal implementation remains unsupported. Amend the earlier public-API-before-pack policy explicitly; do not silently remove its comment.

Neither option should accidentally make all schema engine internals public. An external consumer must declare a byte-bearing Chord contract and call it through public APIs without using a friend assembly name or copying internal helpers.

## License and documentation reconciliation

`THIRD-PARTY-NOTICES.md:130-132` still says Chord v2 is not packed/shipped and describes only `next/`. Current source adds legacy Chord, next-node-derived hosting behavior and env protocol/daemon ports. Update that section to the selected distribution and source domains/commits, preserving the Pi MIT copyright notice. Add Env native/managed/optional Android provenance and clarify which artifact carries which code. Existing SQLite/ripgrep-ignore/jsdiff notices still apply through their original packages; avoid claiming these are new copied code without tracing it.

The C native process shim declares MIT in its header. NativeAOT output also statically contains managed runtime/library code; prepare an actual per-artifact third-party/license manifest from the SDK/dependency inputs, rather than assuming the single executable is covered solely by the Pi MIT notice. Optional Android binaries are distinct upstream Rust artifacts: preserve package integrity, individual SHA-256, MIT source attribution and transitive bundled-code notices from the actual distribution. Hash/ELF verification is not source-build reproducibility or runtime qualification.

Docs to update after decision: package count/list and experimental IDs in release notes; Chord/Store repository-only statements; `Chord.Hosting/README.md:3`; env-ssh deployment instructions and daemon source/binary provenance; framework/runtime prerequisites, sample instructions, no implicit download/restore, and supported protocol versions. Include legacy protocol4 versus next protocol separation and native plugin/IPC differences. Do not claim arbitrary Node/TypeScript plugins are supported by the native Hosting package.

## External consumer acceptance gates

Use an ordinary uniquely named project outside the repository, with no ProjectReference, no Directory.Build import from this checkout, no InternalsVisibleTo name, and only the staged local NuGet feed plus explicit approved dependency sources. Restore the same exact package version, compile, then run offline public-API scenarios. Run through centralized validation, not during this read-only audit.

| Gate | Required external scenario |
|---|---|
| Contracts + Chord | Create public schema/contract including byte leaves; create host/facet; invoke method; state watch; close. Compile once without diagnostic suppression and confirm the expected experimental diagnostic; compile with documented IDs and run. |
| Chord + Hosting | Build a genuine plugin project using only PackageReference; load with shared contract identity; private/transitive dependency; reload with old in-flight call; invoke HTTP route and WebSocket; start/stop real native worker; prove old ALC release. Verify installed ASP.NET shared-framework requirement. |
| Durable.Store | Open actual SQLite store, attach real host, run a Durable task, read RegistryReader contribution, close/reopen/restore, archive import/export; do not reference internal Batch/RegistryRecord. Ensure transitive SQLite native assets for each claimed RID. |
| Env.Protocol + Env | Compile normal connection/client/filesystem/shell/watch use; connect to explicitly supplied verified daemon artifact; run deterministic file/process/watch operations; cancel/disconnect/reconnect and close. No test-only operation types or fixture assembly dependency. |
| Native daemon artifact | Per-target NativeAOT executable starts without adjacent development DLL/shim; protocol handshake, binary bytes, file/process/error path and Unix PTY; package license/provenance and executable permissions checked. Android has its own explicitly recorded runtime gate or remains unqualified. |

Also validate nuspec IDs, one target framework group, exact sibling ranges, framework references, no project-path leakage, managed DLL hash equality, package-only runtime loads and no extra unreviewed archive members. Existing `Assert-PiSharpNativeSdkPackage` returns `packageConsumptionProven=false`; keep that truthful until the new external consumers execute.

## Schema-switch corpus: dedicated assignment still required

Both J1 and X1 explicitly confirmed **they do not own the full schema-switch corpus**:

- J1 owns definition/runtime fingerprint tests. Its updated small case pins literal `af60f04c59e02db7` for args `{n:int}`, result int, state null, but not the upstream recorded ImportJob corpus.
- X1 owns examples35-37 and real-host Durable integration. Example35 stores a real table and Durable docs, but is not the historical pre-switch schema owner/proof.

Current `tests/PiSharp.Chord.Tests/Schema/Declarations.cs:6-12` omits Rows, EditsV1-V3 and ImportJob; `Schema/Proofs.cs:4-8` explicitly omits tables/job/storedReads; `Schema/SchemaSwitchCases.cs:66-72` compares contracts and documents only. Existing fixtures are already available unchanged.

Assign a small **S1 schema corpus** package owning those three test files plus a new real-host schema owner helper. Dependencies: H1 usable host, J1 declared/persisted jobs, existing tables/SQLite/archive. Required results:

1. Port exact Rows/EditsV1-V3/ImportJob declarations from frozen v1.5.0, preserving order and JSON omission rules.
2. Compare **every** contracts/documents/tables/job fingerprint and normal record with frozen `recorded.json`, not only self-equality or newly authored hashes.
3. Read copied `stored/store.db` and imported `stored/archive.jsonl` through the actual host/owner; verify state/table/job evolution and close/reopen behavior against the original proof expectations.
4. Keep fixture bytes and original blob/hash evidence unchanged. New capture evidence is separate; do not regenerate `recorded.json` to accept the port.
5. Record source SHA and exact passing groups. The managed-only, job-only and X1 examples remain valuable controls but cannot substitute for this corpus.

## Follow-up changes since closure-mapping snapshot

I13 has now added `tools/ci/gap-suites.json`, covering new core, Chord and Env suites; the earlier new-suite-registration omission is being actively addressed. Final workflow execution remains separate evidence.

I02 has now assessed the Anthropic incremental-argument residual in `I02-incremental-arguments.md`: default delta handling appends without whole reconstruction and default failure takes one snapshot; full source capture is opt-in. Added functional/allocation guards await centralized execution. Treat this as an explicit source disposition rather than an unowned omission.
