$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'CaseEvidence.ps1')
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dir=Join-Path $root ('artifacts/gap-validation/case-evidence-controls-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir|Out-Null
$inputs=@(
    @('{"cases":0,"failures":0,"results":[]}',0),
    @('{"cases":1,"failures":0,"results":[{"Id":"real","status":"PASS"}]}',1),
    @(("PASS setup`n"+'{"cases":0,"failures":0,"results":[]}'),0),
    @('PASS: 0 authored cases',0),
    @('',0),
    @('incomplete output',$null),
    @("PASS first`nPASS second",2),
    @('0/0 passed; suite complete.',0),
    @(("PASS setup`n"+'{"cases":0,"results":['),$null),
    @("PASS_UPSTREAM_CAPTURE one`nPASS_UPSTREAM_CAPTURE two`nPASS_UPSTREAM_CAPTURE: 2; FAIL: 0; SKIP: 0",2),
    @("PASS one`nPASS two`nPASS: 2 daemon integration tests",2),
    @("PASS one`nPASS two`nPASS 2 native Env process cases",2),
    @("PASS one`nFAIL two: System.Exception: original failure",2),
    @("SKIP Unix raw FIFO EOF/SIGTERM regression on Windows`nPASS raw FIFO daemon exits after EOF and SIGTERM`nPASS: 1 daemon integration tests",1),
    @(( '{"Id":"one","status":"PASS_AUTHORED_NATIVE_ONLY"}'+"`n"+'{"Id":"two","status":"PASS_AUTHORED_NATIVE_ONLY"}'),2),
    @(( '{"Id":"one","status":"PASS"}'+"`n"+'{broken'),$null),
    @('{"cases":[{"Name":"one","Matched":true},{"Name":"two","Matched":false}]}',2),
    @(( '{"Id":"one","status":"PASS"}'+"`n"+'{"Id":"one","status":"PASS"}'),$null)
)
for($index=0;$index -lt $inputs.Count;$index++) {
    $file=Join-Path $dir "$index.txt"
    [IO.File]::WriteAllText($file,[string]$inputs[$index][0])
    $observed=Get-GapCaseEvidence $file
    if($observed.selectedCount -ne $inputs[$index][1]){throw "Case evidence control $index failed: $($observed|ConvertTo-Json -Compress)"}
}
$failed=Get-GapCaseEvidence (Join-Path $dir '12.txt')
if($failed.cases[1].status -ne 'FAIL' -or $failed.cases[1].id -ne 'two'){throw 'Failed case identity/status lost.'}
$skipped=Get-GapCaseEvidence (Join-Path $dir '13.txt')
if($skipped.cases[0].status -ne 'SKIP'){throw 'Nested platform skip was credited as a pass.'}
[IO.File]::WriteAllText((Join-Path $dir 'probe.txt'), "PASS first`n"+'{"schemaVersion":1,"probe":"cleanup","settled":false}'+"`n"+'{"schemaVersion":1,"caseId":"trace","nativeComparison":"differential"}'+"`nPASS second")
[IO.File]::WriteAllText((Join-Path $dir 'error.txt'), 'FAIL third: original failure')
$streams=Get-GapCaseEvidence (Join-Path $dir 'probe.txt') (Join-Path $dir 'error.txt')
if($streams.selectedCount -ne 3 -or $streams.cases[2].status -ne 'FAIL'){throw 'Diagnostic probe or stderr failure obscured actual selected cases.'}
Write-Output "PASS19 selection-evidence controls; evidence $dir"
