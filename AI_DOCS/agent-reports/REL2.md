# REL2 — daemon build identity and native smoke contract

Status: implemented and frozen for main's serialized daemon/NativeAOT verification. Assignment: hard, `gpt-6.1-sol`, high reasoning, requested Ultrafast; effective tier unverified. No .NET build, restore, native publish or Git/remote mutation was run by this agent.

## Defect and behavior

`EnvDaemonOptions.Version` previously defaulted to `1.5.0` even when publishing candidate 1.5.0.1. SSH intentionally requires the hello version to equal the verified daemon artifact version, so a correctly numbered candidate artifact could fail its own connection check.

The default now reads the known `AssemblyInformationalVersionAttribute` from the actual daemon assembly, strips the SDK's `+` source provenance, and falls back to `AssemblyName.Version` if that attribute is absent. It uses neither `Assembly.Location` nor dynamic serialization. The existing explicit `Version` initializer override is retained. Protocol version remains 1.

## Files

- `src/PiSharp.Env.Daemon/DaemonContracts.cs`: build-derived default version and bounded helper only, plus the reflection namespace import.
- `tests/PiSharp.Env.Daemon.Tests/Program.cs`: assertions near filesystem hello check the built daemon product version, absence of source metadata and protocol 1; the existing limits case also verifies the explicit version override. HOST1's bottom pipe factory changes are untouched.
- `tools/Env/Publish-NativeDaemon.ps1`: an `evaluated-version` stage asks the selected SDK's MSBuild for `Version`, using the Release/RID/self-contained/native-publish properties used by publication. This respects environment Version/VersionPrefix/VersionSuffix overrides rather than parsing props. The evidence manifest records `expectedVersion`; invalid or ambiguous evaluation output fails closed. The publisher passes the required `--version` value to smoke, then requires both smoke expected/reported versions to match alongside the existing binary and notice-manifest hashes.
- `tools/Env/native-smoke.mjs`: `--version` is mandatory and validated; the existing hello smoke asserts exact equality and records the actual reported value. All nine smoke groups remain in their original order. Notice/inventory/hash, process, filesystem, watch, Windows ENOTSUP and EOF assertions are unchanged.

The version-read arguments include every explicit `-p:` property passed to publication, in addition to its Release, RID and self-contained settings. The expected value excludes informational `+` metadata and supports the existing three/four-part release versions and prerelease suffixes.

## Verification

- `node --check tools/Env/native-smoke.mjs`: passed.
- PowerShell `System.Management.Automation.Language.Parser.ParseFile` for the publisher: zero parse errors.
- `git diff --check` scoped to the four files: passed.
- Node-only controls execute the actual extracted hello assertion body: exact `1.5.0.1` and `1.5.0.1-rc.2` succeed; old `1.5.0`, missing version and `1.5.0.1+sourcehash` fail. Reported version remains available on mismatches.
- Argument controls reject absent `--version` and an informational/hash suffix.
- Compared the actual smoke group names with `git show HEAD:tools/Env/native-smoke.mjs`: all nine names and ordering match.

The Node controls use fake hello values solely to validate the assertion, without starting a daemon or touching distribution artifacts. They do not qualify the native product. Main received the source-ready checkpoint and owns actual managed tests, candidate NativeAOT publication, evaluated-version output and the strict published-binary smoke. A passing final NativeAOT build must establish that the known-attribute access remains supported without IL warnings; none were suppressed here.

No implementation blocker remains. Runtime/AOT results are pending main verification.
