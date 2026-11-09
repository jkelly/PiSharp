# Hosted source-build lane. This does not issue native admission/launch receipts.
param([Parameter(Mandatory)][ValidateSet('Compatibility','Agent','Transport','Sessions','Tools','CodingAgent','Rpc','Extensions.ContractTests','ExtensionHost','Tui')][string]$Suite,
    [Parameter(Mandatory)][string]$ReportDirectory)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location $repo
$configuredHost = if ($env:PISHARP_TEST_DOTNET) { $env:PISHARP_TEST_DOTNET } else { 'dotnet' }
$hostPath = (Get-Command $configuredHost -CommandType Application -ErrorAction Stop).Source
$env:PISHARP_TEST_DOTNET = $hostPath
$projectName = if ($Suite -eq 'Extensions.ContractTests') { 'PiSharp.Extensions.ContractTests' } else { "PiSharp.$Suite.Tests" }
New-Item -ItemType Directory -Force $ReportDirectory | Out-Null
$ReportDirectory = (Resolve-Path $ReportDirectory).Path
$report = Join-Path $ReportDirectory "$projectName.json"
if (Test-Path $report) { throw 'A fresh report path is required.' }
$execution = [ordered]@{ result = 'running'; stages = [Collections.Generic.List[object]]::new() }
$executionPath = Join-Path $ReportDirectory 'execution.json'
function Invoke-Stage([string]$Name, [scriptblock]$Action) {
    $stage = [ordered]@{ name = $Name; result = 'running'; startedUtc = [DateTime]::UtcNow.ToString('o') }
    $execution.stages.Add($stage)
    try { & $Action; $stage.result = 'passed' }
    catch { $stage.result = 'failed'; $stage.error = $_.Exception.Message; throw }
    finally {
        $stage.finishedUtc = [DateTime]::UtcNow.ToString('o')
        $execution | ConvertTo-Json -Depth 10 | Set-Content $executionPath
    }
}
function Invoke-Dotnet([string[]]$Arguments,[string]$ConsoleLog) {
    if($ConsoleLog){ & $hostPath @Arguments 2>&1 | Tee-Object -FilePath $ConsoleLog }
    else { & $hostPath @Arguments }
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}
try {
$restore = @('-p:RestoreSources=https://api.nuget.org/v3/index.json', '-p:RestoreLockedMode=false')
if ($Suite -in @('Compatibility', 'Agent')) {
    Invoke-Stage 'Verify pinned historical model catalog archive' {
        & node (Join-Path $repo 'tools/ci/ensure-historical-ai-archive.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Historical model archive verification failed.' }
    }
}
if ($Suite -eq 'Tools') {
    Invoke-Stage 'Record native search executable provenance' {
        foreach ($name in @('rg', 'fd')) {
            $command = Get-Command $name -CommandType Application -ErrorAction Stop
            Write-Output "$name path=$($command.Source)"
            & $command.Source --version
            if ($LASTEXITCODE -ne 0) { throw "$name version probe failed." }
        }
    }
}
Invoke-Stage 'Build suite and dependencies' { Invoke-Dotnet (@('build', "tests/$projectName/$projectName.csproj", '-c', 'Release', '-m:1', '-p:UseSharedCompilation=false') + $restore) }
if ($Suite -eq 'CodingAgent') {
    Invoke-Stage 'Build CLI child process' { Invoke-Dotnet (@('build', 'src/PiSharp.Cli/PiSharp.Cli.csproj', '-c', 'Release', '-m:1', '-p:UseSharedCompilation=false') + $restore) }
}
if ($Suite -in @('CodingAgent', 'Extensions.ContractTests')) {
    Invoke-Stage 'Publish all ten native extension fixtures' { & (Join-Path $repo 'tools/publish-native-extension-fixtures.ps1') -Repo $repo -RestoreSource 'https://api.nuget.org/v3/index.json' }
}
$arguments = @("tests/$projectName/bin/Release/net10.0/$projectName.dll", '--report', $report)
if ($Suite -eq 'Compatibility') { $arguments += @('--repo', $repo) }
if ($Suite -in @('CodingAgent', 'Tui')) { $arguments += @('--dotnet-host', $hostPath) }
if ($Suite -eq 'CodingAgent') { $arguments += @('--cli', (Join-Path $repo 'src/PiSharp.Cli/bin/Release/net10.0/PiSharp.Cli.dll')) }
# Suite exceptions and nonzero exits must fail the lane. The always-run manifest step
# independently detects absent/incomplete reports and skipped prerequisites.
Invoke-Stage 'Run complete suite' { Invoke-Dotnet $arguments (Join-Path $ReportDirectory 'console.log') }
$execution.result = 'passed'
} catch { $execution.result = 'failed'; throw }
finally { $execution | ConvertTo-Json -Depth 10 | Set-Content $executionPath }
