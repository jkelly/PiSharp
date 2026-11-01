param(
    [string]$Model = 'gpt-6.1-sol',
    [string]$ApiKeyEnvironment = 'OPENAI_API_KEY',
    [string]$Dotnet = '',
    [int[]]$Examples = @(),
    [ValidateRange(1, 8)][int]$Concurrency = 4,
    [ValidateRange(30, 1800)][int]$TimeoutSeconds = 180,
    [string]$OutputDirectory = '',
    [switch]$SkipBuild
)
# This integration run makes paid model requests. Domain tools use supplied fixtures;
# it never invokes --maintain, image generation, or external publication.
$ErrorActionPreference = 'Stop'
$sampleRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $sampleRoot '../..')).Path
if (-not [Environment]::GetEnvironmentVariable($ApiKeyEnvironment)) { throw "Credential environment variable $ApiKeyEnvironment is not set." }
if (-not $Dotnet) {
    $bundled = Join-Path $repoRoot 'artifacts/sample-tools/dotnet/dotnet.exe'
    $Dotnet = if (Test-Path -LiteralPath $bundled) { $bundled } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$Dotnet = (Resolve-Path -LiteralPath $Dotnet).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ('artifacts/live-samples/' + [Guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'summary.json')) { throw 'Choose a fresh output directory; previous results are retained.' }
Push-Location $repoRoot
try {
    if (-not $SkipBuild) {
        & $Dotnet build (Join-Path $sampleRoot 'PiSharp.Samples.slnx') --nologo -m:1
        if ($LASTEXITCODE -ne 0) { throw 'Sample build failed.' }
    }
    $catalog = @(Get-Content -LiteralPath (Join-Path $sampleRoot 'catalog.json') -Raw | ConvertFrom-Json)
    if ($Examples.Count) { $catalog = @($catalog | Where-Object { $_.number -in $Examples }) }
    if (-not $catalog.Count) { throw 'No selected samples.' }
    $results = @($catalog | ForEach-Object -Parallel {
        $sample = $_
        $root = $using:repoRoot
        $samples = $using:sampleRoot
        $dotnetPath = $using:Dotnet
        $modelId = $using:Model
        $keyEnv = $using:ApiKeyEnvironment
        $timeout = $using:TimeoutSeconds
        $outputRoot = $using:OutputDirectory
        $project = Join-Path $samples $sample.project
        $liveWorkspace = Join-Path $outputRoot $sample.folder
        $baselineWorkspace = Join-Path (Join-Path $outputRoot 'baseline') $sample.folder
        $phase = 'baseline'
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $result = [ordered]@{
            number = $sample.number; title = $sample.title; requestedModel = $modelId; api = 'openai-responses'
            success = $false; hostSuccess = $false; expectedTools = @(); executedTools = @(); missingTools = @()
            modelsObserved = @(); inputTokens = 0; outputTokens = 0; failures = @(); durationSeconds = 0
            report = Join-Path $liveWorkspace 'report.json'
        }
        function RunSample([string[]]$arguments, [string]$workspace, [int]$limit) {
            New-Item -ItemType Directory -Force -Path $workspace | Out-Null
            $start = [Diagnostics.ProcessStartInfo]::new($dotnetPath)
            $start.WorkingDirectory = $root
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $start.Environment['DOTNET_ROOT'] = Split-Path -Parent $dotnetPath
            $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
            foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
            $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
            try {
                [void]$process.Start()
                $stdout = $process.StandardOutput.ReadToEndAsync()
                $stderr = $process.StandardError.ReadToEndAsync()
                if (-not $process.WaitForExit(($limit + 30) * 1000)) {
                    $process.Kill($true); $process.WaitForExit()
                    throw "Process exceeded $limit second run deadline plus shutdown grace."
                }
                [IO.File]::WriteAllText((Join-Path $workspace 'stdout.log'), $stdout.GetAwaiter().GetResult())
                [IO.File]::WriteAllText((Join-Path $workspace 'stderr.log'), $stderr.GetAwaiter().GetResult())
                return $process.ExitCode
            }
            finally { $process.Dispose() }
        }
        try {
            $baselineExit = RunSample @('run', '--project', $project, '--no-build', '--', '--demo', '--approve', '--workspace', $baselineWorkspace) $baselineWorkspace 90
            if ($baselineExit -ne 0) { throw "Fixture baseline failed with exit $baselineExit." }
            $baseline = Get-Content -LiteralPath (Join-Path $baselineWorkspace 'report.json') -Raw | ConvertFrom-Json
            $result.expectedTools = @($baseline.outputs | ForEach-Object tool | Sort-Object -Unique)
            $phase = 'live'
            $liveExit = RunSample @('run', '--project', $project, '--no-build', '--', '--live', '--provider', 'openai', '--api', 'openai-responses', '--model', $modelId, '--key-env', $keyEnv, '--approve', '--timeout', "$timeout", '--workspace', $liveWorkspace) $liveWorkspace $timeout
            if (-not (Test-Path -LiteralPath $result.report)) { throw "Live process exited $liveExit without a report; see stderr.log." }
            $report = Get-Content -LiteralPath $result.report -Raw | ConvertFrom-Json
            $result.hostSuccess = $report.success
            $result.executedTools = @($report.outputs | ForEach-Object tool | Sort-Object -Unique)
            $result.missingTools = @($result.expectedTools | Where-Object { $_ -notin $result.executedTools })
            $transcript = Get-Content -LiteralPath (Join-Path $liveWorkspace 'transcript.json') -Raw | ConvertFrom-Json
            $result.modelsObserved = @($transcript | Where-Object role -eq 'assistant' | ForEach-Object model | Sort-Object -Unique)
            # Child agents retain separate reports; count their actual usage once, alongside the parent.
            $usageReports = @(Get-ChildItem -LiteralPath $liveWorkspace -Recurse -Filter report.json | ForEach-Object {
                $candidate = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
                if ($candidate.mode -eq 'live') { $candidate }
            })
            $usage = @($usageReports | ForEach-Object { $_.usage })
            $result.inputTokens = [double](($usage | Measure-Object input -Sum).Sum)
            $result.outputTokens = [double](($usage | Measure-Object output -Sum).Sum)
            if ($liveExit -ne 0) { $result.failures += "Live process exit $liveExit." }
            if (-not $report.success) { $result.failures += 'Host reported an incomplete or failed workflow.' }
            if ($result.missingTools.Count) { $result.failures += ('Missing workflow stages: ' + ($result.missingTools -join ', ')) }
            if ($modelId -notin $result.modelsObserved) { $result.failures += 'Expected model is absent from assistant transcript.' }
            if ($result.inputTokens + $result.outputTokens -le 0) { $result.failures += 'No provider token usage was recorded.' }
            foreach ($output in @($report.outputs | Where-Object error)) { $result.failures += "Tool $($output.tool) failed: $($output.message)" }
            $result.success = $result.failures.Count -eq 0
        }
        catch { $result.failures += "${phase}: $($_.Exception.Message)" }
        $result.durationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 2)
        $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $outputRoot ('result-' + $sample.number + '.json'))
        Write-Host "$(if ($result.success) { 'PASS' } else { 'FAIL' }) $($sample.number): $($sample.title)"
        [pscustomobject]$result
    } -ThrottleLimit $Concurrency)
    $orderedResults = @($results | Sort-Object number)
    $summary = [ordered]@{
        model = $Model; api = 'openai-responses'; apiKeyEnvironment = $ApiKeyEnvironment
        completedAtUtc = [DateTime]::UtcNow.ToString('O'); samples = $orderedResults.Count
        passed = @($orderedResults | Where-Object success).Count
        failed = @($orderedResults | Where-Object { -not $_.success }).Count
        inputTokens = ($orderedResults | Measure-Object inputTokens -Sum).Sum
        outputTokens = ($orderedResults | Measure-Object outputTokens -Sum).Sum
        domainData = 'fixtures'; externalPublishing = $false; results = $orderedResults
    }
    $summary | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json')
    Write-Host "$($summary.passed)/$($summary.samples) live integrations passed. Results: $OutputDirectory"
    if ($summary.failed) { exit 1 }
}
finally { Pop-Location }
