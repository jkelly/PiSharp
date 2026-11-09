param([Parameter(Mandatory)][string]$QueueFile)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$queue=Get-Content -LiteralPath $QueueFile -Raw|ConvertFrom-Json -AsHashtable
$folder=Join-Path $root ('artifacts/gap-validation/queue-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder|Out-Null
$pwsh=(Get-Process -Id $PID).Path
$progress=[Collections.Generic.List[object]]::new()
foreach($item in $queue){
    $label=$item.options.Label
    $parameters=Join-Path $folder ($label+'.json')
    $console=Join-Path $folder ($label+'.console.log')
    $item|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $parameters
    Write-Output "START $label"
    & $pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-QueueItem.ps1') -ParametersFile $parameters *> $console
    $processExit=$LASTEXITCODE
    $candidate=Get-ChildItem -LiteralPath (Join-Path $root 'artifacts/gap-validation') -Directory -Filter ($label+'-*')|Sort-Object LastWriteTime -Descending|Select-Object -First 1
    $receiptFile=if($candidate){Join-Path $candidate.FullName 'receipt.json'}else{$null}
    $observed=if($receiptFile -and (Test-Path -LiteralPath $receiptFile)){Get-Content -LiteralPath $receiptFile -Raw|ConvertFrom-Json}else{$null}
    $code=if($observed.result -in @('passed','passed-with-source-change','passed-with-console-skips')){0}else{1}
    $progress.Add([ordered]@{label=$label;exitCode=$code;processExitCode=$processExit;result=$observed.result;receipt=$receiptFile;consoleLog=$console;finishedUtc=[DateTime]::UtcNow.ToString('o')})
    $progress|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $folder 'progress.json')
    & (Join-Path $PSScriptRoot 'Update-Status.ps1')|Out-Null
    Write-Output "END $label exit=$code console=$console"
}
Write-Output "Queue complete: $folder"
