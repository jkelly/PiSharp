param([Parameter(Mandatory)][string]$ParametersFile)
$ErrorActionPreference='Stop'
$item=Get-Content -LiteralPath $ParametersFile -Raw|ConvertFrom-Json -AsHashtable
foreach($key in $item.environment.Keys){[Environment]::SetEnvironmentVariable($key,[string]$item.environment[$key])}
$options=$item.options
& (Join-Path $PSScriptRoot 'Run-Tests.ps1') @options
exit $LASTEXITCODE
