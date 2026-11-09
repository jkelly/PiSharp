param(
    [Parameter(Mandatory=$true)][string]$Project,
    [string[]]$TestArgs=@(),
    [string]$TestAssembly,
    [switch]$BuildOnly,
    [switch]$SkipBuild,
    [string[]]$BuildArguments=@(),
    [ValidateSet('Debug','Release')][string]$Configuration='Debug',
    [ValidateRange(1,1800)][int]$BuildTimeoutSeconds=300,
    [ValidateRange(1,3600)][int]$TestTimeoutSeconds=600,
    [ValidateRange(1,1800)][int]$LockTimeoutSeconds=300,
    [ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$Label='validation'
)
$ErrorActionPreference='Stop'
if (-not $IsWindows) { throw 'This local validation owner requires Windows job objects.' }
if ($BuildOnly -and $SkipBuild) { throw 'BuildOnly and SkipBuild are mutually exclusive.' }
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$projectPath=Join-Path $root $Project
if (Test-Path -LiteralPath $projectPath -PathType Container) {
    $projects=@(Get-ChildItem -LiteralPath $projectPath -Filter '*.csproj')
    if ($projects.Count -ne 1) { throw 'Project directory must identify exactly one csproj.' }
    $projectPath=$projects[0].FullName
}
$projectPath=(Resolve-Path -LiteralPath $projectPath).Path
$artifacts=Join-Path $root 'artifacts/gap-validation'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$run=Join-Path $artifacts ($Label+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$log=$run+'.log'
$receiptPath=Join-Path $run 'receipt.json'
$receipt=[ordered]@{project=$projectPath;label=$Label;configuration=$Configuration;result='waiting';startedUtc=[DateTime]::UtcNow.ToString('o');stages=[Collections.Generic.List[object]]::new()}
$filters=[ordered]@{}
foreach($key in @('EXTPARITY_FILTER','CLIPARITY_FILTER','INTERACTIVEPARITY_FILTER','PROVIDERAPIS_FILTER','PISHARP_TEST_PI150_NODE_MODULES','PISHARP_TEST_INSTALL_PI_RUNTIME','PISHARP_PI_RUNTIME_DIR')) {
    $value=[Environment]::GetEnvironmentVariable($key)
    if($null -ne $value){$filters[$key]=$value}
}
$receipt.selection=[ordered]@{testArgs=$TestArgs;filters=$filters;buildOnly=[bool]$BuildOnly}
$receipt.platform=[ordered]@{os=[Runtime.InteropServices.RuntimeInformation]::OSDescription;architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()}
function Save-Receipt { $receipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $receiptPath }
Save-Receipt
Write-Output "Validation receipt: $receiptPath"
$settings=@{DOTNET_CLI_HOME=(Join-Path $artifacts 'dotnet-home');NUGET_PACKAGES=(Join-Path $root 'artifacts/nuget-cache');DOTNET_CLI_TELEMETRY_OPTOUT='1';DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1';MSBUILDDISABLENODEREUSE='1';DOTNET_CLI_USE_MSBUILD_SERVER='0';UseSharedCompilation='false'}
$saved=@{}
foreach($key in $settings.Keys) { $saved[$key]=[Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key,$settings[$key]) }
$sdk='E:/AI/PiSharp/PiSharp/artifacts/sample-tools/dotnet/dotnet.exe'
$receipt.sdk=[ordered]@{path=$sdk;launcherSha256=(Get-FileHash -LiteralPath $sdk -Algorithm SHA256).Hash.ToLowerInvariant();configuredVersion=(Get-Content (Join-Path $root 'global.json') -Raw|ConvertFrom-Json).sdk.version}
$lease=$null
$pushed=$false
$exitCode=1
# Windows CommandLineToArgvW-compatible quoting, never a shell command.
function Quote-Argument([string]$Value) {
    if($Value -ne '' -and $Value -notmatch '[\s"]') { return $Value }
    return '"'+([regex]::Replace([regex]::Replace($Value,'(\\*)"','$1$1\"'),'(\\+)$','$1$1'))+'"'
}
function Invoke-Owned([string]$Name,[string[]]$Arguments,[int]$Timeout) {
    $stdout=Join-Path $run ($Name+'.stdout.log'); $stderr=Join-Path $run ($Name+'.stderr.log')
    $stage=[ordered]@{name=$Name;arguments=$Arguments;result='starting';stdout=$stdout;stderr=$stderr;startedUtc=[DateTime]::UtcNow.ToString('o')}
    $receipt.stages.Add($stage); Save-Receipt
    $owner=$null
    try {
        $command=((@($sdk)+$Arguments)|ForEach-Object { Quote-Argument $_ }) -join ' '
        $owner=[GapOwnedProcess]::new($sdk,$command,$root,$stdout,$stderr)
        $stage.pid=$owner.Id; $stage.result='running'; Save-Receipt
        $deadline=[DateTime]::UtcNow.AddSeconds($Timeout)
        while(-not $owner.Wait(250)) {
            if([DateTime]::UtcNow -ge $deadline) {
                $stage.result='timed-out'; $owner.Kill()
                if(-not $owner.Wait(10000)) { throw 'Owned process did not settle after job termination.' }
                return 124
            }
        }
        $stage.exitCode=$owner.ExitCode
        $stage.result=if($stage.exitCode -eq 0){'passed'}else{'failed'}
        return $stage.exitCode
    } catch {
        $stage.result='failed';$stage.error=$_.Exception.Message;throw
    } finally {
        $cleanupFailure=$null
        if($owner) {
            try {$owner.Dispose();$stage.cleanup='owned-job-empty-and-parent-joined'}
            catch {$cleanupFailure=$_;$stage.result='cleanup-failed';$stage.cleanup='failed';$stage.cleanupError=$_.Exception.Message}
        }
        $stage.finishedUtc=[DateTime]::UtcNow.ToString('o'); Save-Receipt
        foreach($path in @($stdout,$stderr)) {
            if(Test-Path -LiteralPath $path) {
                Add-Content -LiteralPath $log -Value "`n--- $Name : $path ---"
                Get-Content -LiteralPath $path | Add-Content -LiteralPath $log
            }
        }
        if($cleanupFailure){throw $cleanupFailure}
    }
}
try {
    $deadline=[DateTime]::UtcNow.AddSeconds($LockTimeoutSeconds)
    while($null -eq $lease) {
        try { $lease=[IO.File]::Open((Join-Path $artifacts 'build.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
        catch [IO.IOException] { if([DateTime]::UtcNow -ge $deadline){ throw 'Validation build lock timed out; no process launched.' }; Start-Sleep -Milliseconds 250 }
    }
    $receipt.result='running'; Save-Receipt
    . (Join-Path $PSScriptRoot 'SourceFingerprint.ps1')
    $sourceBefore=Get-GapSourceFingerprint $root $projectPath
    $sourceBefore|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $run 'source-before.json')
    $receipt.source=[ordered]@{head=$sourceBefore.head;dirty=$sourceBefore.dirty;beforeSha256=$sourceBefore.sha256;manifest=(Join-Path $run 'source-before.json')}
    Save-Receipt
    if(-not ('GapOwnedProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'OwnedProcess.cs') }
    Push-Location $root; $pushed=$true
    if(-not $SkipBuild) {
        $buildArgs=@('build',$projectPath,'--disable-build-servers','--configuration',$Configuration,'-m:1','-nr:false','-p:RestoreLockedMode=false','-p:UseSharedCompilation=false')+$BuildArguments
        $exitCode=Invoke-Owned 'build' $buildArgs $BuildTimeoutSeconds
        if($exitCode -ne 0) { throw "Build failed or timed out ($exitCode)." }
    }
    if(-not $BuildOnly) {
        $assemblyName=if($TestAssembly){$TestAssembly}else{[IO.Path]::GetFileNameWithoutExtension($projectPath)+'.dll'}
        if(-not $assemblyName.EndsWith('.dll',[StringComparison]::OrdinalIgnoreCase)){$assemblyName+='.dll'}
        $dll=if([IO.Path]::IsPathRooted($assemblyName)){$assemblyName}else{Join-Path (Split-Path $projectPath -Parent) ('bin/'+$Configuration+'/net10.0/'+$assemblyName)}
        if(-not (Test-Path -LiteralPath $dll)) { throw "Built suite not found: $dll" }
        # SkipBuild is an explicit coordinator assertion that this exact output remains current.
        # The same lease protects execution, so another helper cannot overwrite its binaries.
        $exitCode=Invoke-Owned 'tests' (@($dll)+$TestArgs) $TestTimeoutSeconds
        . (Join-Path $PSScriptRoot 'CaseEvidence.ps1')
        $receipt.caseEvidence=Get-GapCaseEvidence (Join-Path $run 'tests.stdout.log') (Join-Path $run 'tests.stderr.log')
        $receipt.caseEvidence.consoleSkipObservations=@(Get-GapConsoleSkips (Join-Path $run 'tests.stdout.log') (Join-Path $run 'tests.stderr.log'))
        if($exitCode -eq 0 -and ($null -eq $receipt.caseEvidence.selectedCount -or $receipt.caseEvidence.selectedCount -lt 1)) {
            $exitCode=3
            throw 'Suite returned success without positive observed selected case evidence.'
        }
    }
    $receipt.result=if($exitCode -eq 0){'passed'}else{'failed'}
    if($receipt.result -eq 'passed' -and $receipt.caseEvidence.consoleSkipObservations.Count){$receipt.result='passed-with-console-skips'}
} catch {
    if($exitCode -eq 0){$exitCode=1}
    $receipt.result='failed';$receipt.error=$_.Exception.ToString()
    Write-Output $_.Exception.Message
} finally {
    if($sourceBefore) {
        $sourceAfter=Get-GapSourceFingerprint $root $projectPath
        $sourceAfter|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $run 'source-after.json')
        $receipt.source.afterSha256=$sourceAfter.sha256
        $receipt.source.changedDuringRun=($sourceBefore.sha256 -ne $sourceAfter.sha256)
        if($receipt.source.changedDuringRun -and $receipt.result -eq 'passed'){$receipt.result='passed-with-source-change'}
    }
    $receipt.finishedUtc=[DateTime]::UtcNow.ToString('o');Save-Receipt
    if($pushed){Pop-Location}
    if($lease){$lease.Dispose()}
    foreach($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key,$saved[$key]) }
    Write-Output "Log: $log"
    if(Test-Path -LiteralPath $log){Get-Content -LiteralPath $log -Tail 35}
    Write-Output "Receipt: $receiptPath"
}
exit $exitCode
