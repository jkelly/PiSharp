param(
    [Parameter(Mandatory=$true)][ValidateSet('core','chord','env','runtime-installer')][string]$Group,
    [string]$OutputDirectory='artifacts/gap-ci',
    [string]$Dotnet=$(if ($env:PISHARP_TEST_DOTNET) { $env:PISHARP_TEST_DOTNET } else { 'dotnet' })
)
$ErrorActionPreference='Stop'
# Python owns build/test trees and atomically persists the plan and stage evidence.
& python (Join-Path $PSScriptRoot 'run_gap_suites.py') --group $Group --output-directory $OutputDirectory --dotnet $Dotnet
exit $LASTEXITCODE
