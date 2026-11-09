$ErrorActionPreference='Stop'
if(-not ('GapOwnedProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'OwnedProcess.cs') }
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dir=Join-Path $root ('artifacts/gap-validation/owner-control-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null
$pwsh=(Get-Process -Id $PID).Path
$stdout=Join-Path $dir 'stdout.log';$stderr=Join-Path $dir 'stderr.log'
$owner=$null
try {
    $owner=[GapOwnedProcess]::new($pwsh,('"'+$pwsh+'" -NoProfile -Command "[Console]::WriteLine(42); exit 7"'),$root,$stdout,$stderr)
    if(-not $owner.Wait(10000)){throw 'Exit control timed out.'}
    if($owner.ExitCode -ne 7 -or (Get-Content $stdout -Raw).Trim() -ne '42'){throw 'Exit/output control failed.'}
} finally {if($owner){$owner.Dispose()}}
$childScript=Join-Path $dir 'child.ps1'
'Write-Output $PID; Start-Sleep -Seconds 120' | Set-Content $childScript
$parentScript=Join-Path $dir 'parent.ps1'
@'
param($Executable,$Child)
$p=Start-Process -FilePath $Executable -ArgumentList @('-NoProfile','-File',$Child) -WindowStyle Hidden -PassThru
Write-Output $p.Id
Start-Sleep -Seconds 120
'@ | Set-Content $parentScript
try {
    $command='"'+$pwsh+'" -NoProfile -File "'+$parentScript+'" "'+$pwsh+'" "'+$childScript+'"'
    $owner=[GapOwnedProcess]::new($pwsh,$command,$root,$stdout,$stderr)
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    do { Start-Sleep -Milliseconds 100; $content=Get-Content -LiteralPath $stdout -Raw; } while(-not $content -and [DateTime]::UtcNow -lt $deadline)
    if(-not $content){throw 'Descendant PID unavailable.'}
    $childId=[int]($content.Trim().Split("`n")[0])
    if(-not (Get-Process -Id $childId -ErrorAction SilentlyContinue)){throw 'Descendant not alive before termination.'}
    $owner.Kill()
    if(-not $owner.Wait(10000)){throw 'Owned parent not joined.'}
    $deadline=[DateTime]::UtcNow.AddSeconds(5)
    while((Get-Process -Id $childId -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 50}
    if(Get-Process -Id $childId -ErrorAction SilentlyContinue){throw 'Owned descendant survived job termination.'}
} finally {if($owner){$owner.Dispose()}}
@'
param($Executable,$Child)
$p=Start-Process -FilePath $Executable -ArgumentList @('-NoProfile','-File',$Child) -WindowStyle Hidden -PassThru
Write-Output $p.Id
exit 0
'@ | Set-Content $parentScript
try {
    $owner=[GapOwnedProcess]::new($pwsh,$command,$root,$stdout,$stderr)
    if(-not $owner.Wait(10000)){throw 'Completed-parent control timed out.'}
    $childId=[int]((Get-Content -LiteralPath $stdout -Raw).Trim().Split("`n")[0])
    $owner.Dispose();$owner=$null
    if(Get-Process -Id $childId -ErrorAction SilentlyContinue){throw 'Descendant survived disposal after parent exit.'}
} finally {if($owner){$owner.Dispose()}}
Write-Output "PASS: captured exit/output and joined owned parent/descendant termination; evidence $dir"
