param(
    [string]$Dotnet = '',
    [int[]]$Examples = @(),
    [switch]$SkipBuild,
    [switch]$SkipSelfTests
)
$ErrorActionPreference = 'Stop'
$sampleRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $sampleRoot '../..')).Path
if (-not $Dotnet) {
    $bundled = Join-Path $repoRoot 'artifacts/sample-tools/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $bundled) { $Dotnet = $bundled }
    else { $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$catalog = @(Get-Content -LiteralPath (Join-Path $sampleRoot 'catalog.json') -Raw | ConvertFrom-Json)
if ($Examples.Count -gt 0) {
    $unknown = @($Examples | Where-Object { $_ -notin $catalog.number } | Sort-Object -Unique)
    if ($unknown.Count -gt 0) { throw "Unknown sample numbers: $($unknown -join ', ')." }
    $catalog = @($catalog | Where-Object { $_.number -in $Examples })
}
if ($catalog.Count -eq 0) { throw 'No selected samples.' }
$outputRoot = Join-Path $repoRoot ('artifacts/sample-validation/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
Push-Location $repoRoot
try {
    if (-not $SkipBuild) {
        & $Dotnet build (Join-Path $sampleRoot 'PiSharp.Samples.slnx') --nologo -m:1
        if ($LASTEXITCODE -ne 0) { throw 'Sample solution build failed.' }
    }
    & $Dotnet run --project (Join-Path $sampleRoot 'Tests/PiSharp.Samples.Tests.csproj') --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Shared runtime tests failed.' }
    $results = @()
    foreach ($sample in $catalog) {
        $project = Join-Path $sampleRoot $sample.project
        if (-not $SkipSelfTests) {
            $selfOutput = & $Dotnet run --project $project --no-build -- --self-test 2>&1
            if ($LASTEXITCODE -ne 0) { throw "Self-test failed for sample $($sample.number): $selfOutput" }
        }
        $workspace = Join-Path $outputRoot $sample.folder
        $demoOutput = & $Dotnet run --project $project --no-build -- --demo --approve --workspace $workspace 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Demo failed for sample $($sample.number): $demoOutput" }
        $demoOutput | Set-Content -LiteralPath (Join-Path $workspace 'stdout.log')
        if (-not $SkipSelfTests) { $selfOutput | Set-Content -LiteralPath (Join-Path $workspace 'self-tests.log') }
        $report = Get-Content -LiteralPath (Join-Path $workspace 'report.json') -Raw | ConvertFrom-Json
        if (-not $report.success -or $report.outputs.Count -lt 1) { throw "Incomplete report for sample $($sample.number)." }
        $results += [pscustomobject]@{ number = $sample.number; title = $sample.title; success = $true; outputs = $report.outputs.Count; selfTestsRun = -not $SkipSelfTests }
        Write-Host "PASS $($sample.number): $($sample.title)"
    }
    $results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'summary.json')
    Write-Host "$($results.Count) sample demos passed. Reports: $outputRoot"
}
finally { Pop-Location }
