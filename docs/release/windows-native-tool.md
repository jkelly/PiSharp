# Private Windows native tool profile

This source profile targets a framework-dependent `PiSharp.Cli` NuGet tool,
command `pisharp`, framework `net10.0`, SDK `10.0.401`, default YAML enabled.
It requires a separately installed admitted .NET 10 runtime. It is not an MSI,
self-contained executable, clean-machine result or public release. No packaging
function, fixture, pack, install or shim has been executed for this successor.

The CLI project and its locked dependency graph do not reference
`PiSharp.Compatibility.Node`. That optional compatibility project remains a
separate source lane. The native archive validator rejects its assembly and
dependency identity, `node`, `npm`, `npx` and `node_modules`. This profile neither
installs Node nor claims compatibility with JavaScript extensions. It does not
remove native extension loading or disable prompt YAML.

## Composition and transaction

The exact three-file standalone producer from `f6fa262f0ef4948584f4552b5531f73442135c02`
is composed onto the accepted host source. Existing R296/R297 release metadata
and byte validators remain unchanged. Their standalone functionality is retained
as source; this framework-dependent profile does not invoke it or inherit its
unexecuted platform claims.

Use the existing opt-in `tools/packaging/PiSharp.Distribution.targets` through
`CustomAfterMicrosoftCommonTargets`; retain normal Directory.Build imports and
locked default-YAML restore. Bind explicit version, current commit/tree and
provenance. Fresh restore/build/pack outputs are required. Source, caches, SDK,
private configuration and all effective imports require physical admission.
Private cache/home/temp/feed/tool roots remain outside the cold checkout.

The disabled packet specifies six originals: locked restore, Release build,
no-build/no-restore pack, exact-version local-feed install, installed shim help,
and installed shim invalid argument. The final original expects exit 2 with
`InvalidArguments`; this is a positive smoke result only when the original
process, descendants, capture and cleanup are joined. An owner that requires
exit 0 for every operation cannot execute this contract unchanged.

Before install, use `Assert-PiSharpDistribution` to validate source provenance,
MIT/readme/notices, tool settings, dependency graph and YAML16.3.0 license.
After install, `Assert-PiSharpWindowsToolInstallation` compares every framework
payload file with the package, each DLL with the current admitted build, and
records the separately generated Windows shim. Unexpected/missing/modified
files, links and nonportable paths fail inspection. The coordinator must hold
package/build/installed bytes under read leases through inspection and each
shim original; these inspection functions do not acquire execution authority or
authenticate an arbitrary caller's process receipt. Capture checks explicitly
return `originalSettlementProven=false`.

The local tool config clears inherited package sources and names only the new
private feed; empty task-owned NuGet machine settings and a complete explicit
environment prevent ambient feed/home discovery. Never install globally or
mutate PATH. Retain original failed evidence; stop after the first nonpass.

## Remaining gates

The new `tools/qualification/windows-package-driver.ps1` and
`windows-package-original.ps1` implement six serial operations with exact
packet/grant pins, 106 historical receipts plus intervening actual associations,
fresh output checks, original task/capture/job joins, and input/library/claim
retirement. Only the fixed inert invalid-argument probe admits `NonZeroExit` 2;
it also requires empty stdout, one bounded InvalidArguments object, no process
diagnostics and an unchanged empty working directory. No general expected-exit
override is added to a shared owner. The driver stops after the first nonpass.

This new owner remains unqualified: exact source review and separately granted
positive/negative native controls must precede product acceptance. Controls must
include expected-2 success, wrong exit/public code/output rejection, timeout,
capture failure, held original cleanup, duplicate activation and expiry after
leases. R8's historical tool smoke and R355's source-only pack/publish owner are
precedents, not authority.
Synthetic controls in `tests/release/windows-tool-installation.tests.ps1` are
authored and unexecuted; they require their own finite allocation.

No self-contained runtime packs are in the admitted cache. A win-x64 request
needs exact `Microsoft.NETCore.App.Runtime.win-x64` matching the selected
runtime version (current SDK lane uses 10.0.12), its NuGet identity/hash/license,
and any SDK-resolved apphost/runtime dependencies and lock graph. The existing
shared runtime or apphost files do not establish this package admission. There
is no permission to download or search unrelated caches. Other architectures,
signing, SBOM/redistribution closure, public publication, update/uninstall and
independent reproducibility remain separate gates.

Public native SDK consumer source is owned separately under
`tests/PiSharp.PublicSdkConsumer.R511`. It must be source-reviewed and composed
before an exact SDK package/install packet is possible. Project-reference
consumer success alone would not establish package consumption. No SDK package
or consumer result is fabricated here.
