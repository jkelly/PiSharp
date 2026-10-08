# Authored controls; execution needs a separate finite allocation.
# Synthetic bytes only. No native process, NuGet, SDK, symlink creation or network.
param([Parameter(Mandatory)][string]$FreshRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../tools/release/windows-tool-installation.ps1')
if (-not [IO.Path]::IsPathFullyQualified($FreshRoot) -or (Test-Path -LiteralPath $FreshRoot)) { throw 'Fresh absolute fixture root required.' }
Assert-PiSharpOrdinaryInstallPath $FreshRoot
New-Item -ItemType Directory -Path $FreshRoot | Out-Null
function Refused([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Negative installation control unexpectedly admitted.' }
}
$file = Join-Path $FreshRoot 'PiSharp.Cli.dll'
[IO.File]::WriteAllText($file, 'synthetic-build-bytes')
$inventory = @([pscustomobject]@{ path = 'tools/net10.0/any/PiSharp.Cli.dll'; bytes = (Get-Item $file).Length;
    sha256 = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() })
$positive = Assert-PiSharpToolPayloadBytes $FreshRoot $inventory
if ($positive.fileCount -ne 1 -or -not $positive.exactPayloadBytes -or $positive.platformQualified) { throw 'Positive membership control failed.' }
$nested = Join-Path $FreshRoot 'licenses/YamlDotNet.LICENSE.txt'
New-Item -ItemType Directory -Path (Split-Path $nested -Parent) | Out-Null
[IO.File]::WriteAllText($nested, 'synthetic-nested-license')
$nestedInventory = @($inventory[0], [pscustomobject]@{ path = 'tools/net10.0/any/licenses/YamlDotNet.LICENSE.txt';
    bytes = (Get-Item $nested).Length; sha256 = (Get-FileHash $nested -Algorithm SHA256).Hash.ToLowerInvariant() })
$nestedPositive = Assert-PiSharpToolPayloadBytes $FreshRoot $nestedInventory
if ($nestedPositive.fileCount -ne 2 -or -not $nestedPositive.exactPayloadBytes) { throw 'Nested Windows package membership failed.' }
Remove-Item -LiteralPath $nested
[IO.File]::WriteAllText($file, 'synthetic-wrong-bytes')
Refused { Assert-PiSharpToolPayloadBytes $FreshRoot $inventory }
[IO.File]::WriteAllText($file, 'synthetic-build-bytes')
$extra = Join-Path $FreshRoot 'extra.dll'; [IO.File]::WriteAllText($extra, 'unexpected')
Refused { Assert-PiSharpToolPayloadBytes $FreshRoot $inventory }
Remove-Item -LiteralPath $extra
Refused { Assert-PiSharpToolPayloadBytes $FreshRoot @($inventory[0], $inventory[0]) }
$unsafe = @([pscustomobject]@{ path = 'tools/net10.0/any/../PiSharp.Cli.dll'; bytes = 1; sha256 = ('0' * 64) })
Refused { Assert-PiSharpToolPayloadBytes $FreshRoot $unsafe }
Remove-Item -LiteralPath $file
Refused { Assert-PiSharpToolPayloadBytes $FreshRoot $inventory }
$help = Assert-PiSharpInstalledSmokeCapture help 0 'Usage: PiSharp.Cli --help' ''
$invalid = Assert-PiSharpInstalledSmokeCapture invalid-argument 2 '' '{"schemaVersion":1,"status":"failed","code":"InvalidArguments"}'
if (-not $help.captureContractMatched -or -not $invalid.captureContractMatched -or $invalid.originalSettlementProven) { throw 'Smoke positive control failed.' }
Refused { Assert-PiSharpInstalledSmokeCapture help 0 'Usage: PiSharp.Cli --help' 'unexpected' }
Refused { Assert-PiSharpInstalledSmokeCapture invalid-argument 0 '' '{"schemaVersion":1,"status":"failed","code":"InvalidArguments"}' }
Refused { Assert-PiSharpInstalledSmokeCapture invalid-argument 2 '' '{"schemaVersion":1,"status":"failed","code":"CleanupFailed"}' }
Refused { Assert-PiSharpInstalledSmokeCapture invalid-argument 2 '' '[{"schemaVersion":1,"status":"failed","code":"InvalidArguments"}]' }
[pscustomobject]@{ authoredControls = 13; nativeProcesses = 0; releaseAccepted = $false }
