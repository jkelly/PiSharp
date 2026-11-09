$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot 'CaseEvidence.ps1')
$parametersByLabel=@{}
foreach($directory in Get-ChildItem -LiteralPath (Join-Path $root 'artifacts/gap-validation') -Directory -Filter 'queue-*') {
    foreach($file in Get-ChildItem -LiteralPath $directory.FullName -File -Filter '*.json') {
        try {$config=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json} catch {continue}
        if($config.options.Label){$parametersByLabel[$config.options.Label]=[ordered]@{path=$file.FullName;config=$config}}
    }
}
$runs=@(foreach($file in Get-ChildItem -LiteralPath (Join-Path $root 'artifacts/gap-validation') -Filter receipt.json -Recurse -File) {
    try {$r=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json} catch {continue}
    if(-not $r.project){continue}
    $build=@($r.stages|Where-Object name -eq 'build')|Select-Object -Last 1
    $tests=@($r.stages|Where-Object name -eq 'tests')|Select-Object -Last 1
    $parameters=$parametersByLabel[$r.label]
    $selection=if($r.selection){$r.selection}elseif($parameters){[ordered]@{testArgs=$parameters.config.options.TestArgs;filters=$parameters.config.environment;buildOnly=$parameters.config.options.BuildOnly}}else{$null}
    $filtersJson=if($selection){$selection.filters|ConvertTo-Json -Compress -Depth 5}else{'unknown-label-'+$r.label}
    $requestedArgs=if($selection){@($selection.testArgs)}else{@($tests.arguments|Select-Object -Skip 1)}
    $scope=$r.project+'|'+($requestedArgs -join '|')+'|'+$filtersJson+'|buildOnly='+$selection.buildOnly
    $observedResult=$r.result
    if($r.error -like '*Validation build lock timed out*'){$observedResult='infrastructure-lock-timeout'}
    $caseEvidence=if($tests.stdout){Get-GapCaseEvidence $tests.stdout $tests.stderr}else{$null}
    if($caseEvidence){$caseEvidence.consoleSkipObservations=@(Get-GapConsoleSkips $tests.stdout $tests.stderr)}
    if($tests.exitCode -eq 0 -and $caseEvidence.selectedCount -gt 0 -and $r.error -like '*Suite returned success without positive observed selected case evidence*') {
        $observedResult=if($r.source.changedDuringRun){'passed-with-source-change-parser-recovered'}else{'passed-parser-recovered'}
    }
    if($caseEvidence.consoleSkipObservations.Count -and $observedResult -in @('passed','passed-with-source-change')){$observedResult='passed-with-console-skips'}
    if($tests.exitCode -eq 0 -and $tests -and ($null -eq $caseEvidence.selectedCount -or $caseEvidence.selectedCount -lt 1)) {
        $observedResult=if($null -eq $caseEvidence.selectedCount){'incomplete-no-case-evidence'}else{'failed-zero-selected-cases'}
    }
    if(-not $r.finishedUtc) {
        $active=@($r.stages|Where-Object result -eq 'running')|Select-Object -Last 1
        if($active.pid) {
            $process=Get-Process -Id $active.pid -ErrorAction SilentlyContinue
            if(-not $process){$observedResult='interrupted-no-final-receipt'}
        } elseif([DateTimeOffset]::UtcNow - [DateTimeOffset]$r.startedUtc -gt [TimeSpan]::FromMinutes(10)) {
            $observedResult='incomplete-no-final-receipt'
        }
    }
    $diagnostics=@(foreach($stage in $r.stages) {
        foreach($path in @($stage.stdout,$stage.stderr)) {
            if($path -and (Test-Path -LiteralPath $path)) {
                Get-Content -LiteralPath $path|Where-Object {$_ -match ': error |^FAIL |"status": "FAIL"|^PASS '}
            }
        }
    })
    [ordered]@{label=$r.label;project=$r.project;scopeIdentity=$scope;selection=$selection;parametersFile=$parameters.path;platform=$r.platform;sdk=$r.sdk;testArguments=$requestedArgs;result=$observedResult;recordedResult=$r.result;startedUtc=$r.startedUtc;finishedUtc=$r.finishedUtc;source=$r.source;buildExit=$build.exitCode;buildArguments=$build.arguments;testExit=$tests.exitCode;caseEvidence=$caseEvidence;error=$r.error;receipt=$file.FullName;diagnostics=@($diagnostics|Select-Object -Last 20)}
})
$latest=@($runs|Sort-Object startedUtc|Group-Object {$_.scopeIdentity}|ForEach-Object {$_.Group|Select-Object -Last 1})
$target=Join-Path $root 'AI_DOCS/validation-status.json'
$temporary=$target+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
$json=[ordered]@{schemaVersion=1;scope='local worktree execution only; not hosted CI';generatedUtc=[DateTime]::UtcNow.ToString('o');latestByScope=$latest;runs=$runs}|ConvertTo-Json -Depth 15
[IO.File]::WriteAllText($temporary,$json,[Text.UTF8Encoding]::new($false))
$deadline=[DateTime]::UtcNow.AddSeconds(3)
while(Test-Path -LiteralPath $temporary) {
    try {[IO.File]::Move($temporary,$target,$true)}
    catch [IO.IOException] {
        if([DateTime]::UtcNow -ge $deadline){Write-Warning "Status snapshot busy; complete snapshot preserved at $temporary. Raw receipts and queue progress remain authoritative.";break}
        Start-Sleep -Milliseconds 50
    }
}
Write-Output "Updated $($latest.Count) validation scopes from $($runs.Count) receipts."
$pause=Join-Path $root 'artifacts/gap-validation/pause-after-item.request'
$queueCall=@(Get-PSCallStack|Where-Object {$_.ScriptName -and $_.ScriptName.EndsWith('Run-Queue.ps1',[StringComparison]::OrdinalIgnoreCase)})
if($queueCall.Count -and (Test-Path -LiteralPath $pause)) {
    Move-Item -LiteralPath $pause -Destination ($pause+'.observed') -Force
    throw 'Coordinator paused queue after a completed item; no next process launched.'
}
