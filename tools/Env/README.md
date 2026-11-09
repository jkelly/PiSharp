# Native daemon qualification

`.github/workflows/env-native.yml` is the dedicated NativeAOT gate. It runs on PRs/main and can be called with an immutable `source_sha`, independently of the managed Env regression workflow.

The job uses the actual SDK/host RID on Ubuntu 24.04, Windows Server 2022, and macOS 15. It installs Linux's `clang`, C/C++ development tools and `zlib1g-dev`, verifies macOS Xcode Command Line Tools, and checks the Windows Visual Studio C++ workload and Windows SDK before publication. These are based on [.NET's NativeAOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) and the [GitHub runner image inventory](https://github.com/actions/runner-images). No cross-compilation is configured.

The evidence records the actual OS/toolchain image. Successful qualification covers that host ABI; it does not establish support for older Linux glibc versions, Linux musl, or earlier macOS deployment targets.

Run the same gate locally with an empty, unique output directory:

```powershell
./tools/Env/Publish-NativeDaemon.ps1 -OutputDirectory artifacts/env-native-local
```

`-DotnetPath` selects a particular SDK launcher; `-SourceSha` rejects a checkout that differs from the requested full commit ID. Restore uses NuGet.org; local callers can set `NUGET_PACKAGES` and `DOTNET_CLI_HOME` to isolated directories. Trim and NativeAOT warnings fail the publish.

The helper passes `PiSharpEnvNativePublish=true`, which sets `PublishAot` locally on the daemon application. It avoids declaring every unused provider API in referenced libraries to be AOT-compatible. Final ILC analysis of the executable's reachable code still treats trimming/AOT warnings as errors; no IL diagnostic is suppressed.

The helper stages one native executable in `package/`, leaving debug symbols and any development output behind. `Write-NativeNotices.ps1` adds the repository license/adaptation notices, exact selected .NET runtime/NativeAOT notices, and dependency notices. `native-package-manifest.json` records package identity/version/integrity, notice origins, restore-input hash, and SHA-256/size for every distributed file except that manifest itself. The evidence manifest separately hashes the distribution manifest. Restore inputs must identify the exact publish RID and all required runtime/compiler package versions; missing notice text or an unreviewed dependency fails packaging without a download.

The inventory conservatively covers restored compile/runtime dependencies, including Jint/Acornima from the daemon's Durable.Tools → Codemode reference. It does not claim that AOT trimming retains all their methods. Exact pinned repository-vendored Jint/Acornima notices are used because their current NuGet archives declare BSD licenses but omit those texts; a version change requires updating that reviewed mapping. .NET runtime and NativeAOT metadata must agree on their dotnet/dotnet source commit; their actual package notices are copied with the runtime pack's full MIT license. No SDK/IDE/compiler executable is redistributed.

Signed NuGet archives have a canonical package content hash distinct from the SHA-512 of their signed ZIP bytes. The notices helper uses the selected SDK's `NuGet.Packaging` reader, verifies signed payload integrity without certificate/network lookups, compares canonical content hashes with restore/cache metadata, and separately hashes the archive bytes. The cache sidecar must match one of those explicitly recorded representations; the manifest retains both values and identifies the sidecar's representation. Package ID/version and source-notice checks remain mandatory.

`native-smoke.mjs` verifies the complete package inventory/hashes, native file format/architecture, one executable, and absence of managed/native sidecars before launching the isolated executable. The accompanying notices are documentation only. It exercises startup synchronization, hello, binary file transfers, native descriptor metadata, line-scan JSON, recoverable malformed JSON, raw child-process pipes/exit, polling-watch events/cancel, and EOF cleanup. On Windows it also verifies explicit PTY `ENOTSUP`. On Linux/macOS the complete terminal suite runs against the same copied executable; its required native case names must appear in the actual log.

`evidence/` contains publish/test output, environment prerequisites, per-case smoke results, executable and distribution-manifest SHA-256, exact checkout identity, and the overall result. The workflow uploads the executable with its complete license/notices bundle only after all gates pass, and uploads evidence on failure too. GitHub artifact downloads may require restoring the executable bit on Unix; SSH deployment already sets permissions before use. Retain the accompanying notices when redistributing the binary.

Native publication is not established by the existence of this workflow. A platform is qualified only when its actual job passes at the intended source revision. See [NativeAOT interop](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/interop) for the static-library/direct-P/Invoke mechanism used by the Unix shim. System JSON reflection restrictions are covered by [the serialization documentation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation#disable-reflection-defaults).
