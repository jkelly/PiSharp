param(
    [Parameter(Mandatory=$true)][ValidateSet('core','chord','env','runtime-installer')][string]$Group,
    [string]$OutputDirectory='artifacts/gap-ci'
)
$ErrorActionPreference='Stop'
# Python owns build/test trees and atomically persists the plan and stage evidence.
& python (Join-Path $PSScriptRoot 'run_gap_suites.py') --group $Group --output-directory $OutputDirectory
exit $LASTEXITCODE
