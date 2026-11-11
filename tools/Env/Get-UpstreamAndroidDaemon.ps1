# Download only the explicitly pinned MIT upstream Android backend. Never execute downloaded files.
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$destination=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$archive=Join-Path $destination 'pi-env-1.5.2.tgz'
$uri='https://registry.npmjs.org/@earendil-works/pi-env/-/pi-env-1.5.2.tgz'
$integrity='yw9dPqPHHP567m7+TDAmuzWC9AU1I3aV3FFdNQlo+UXASYGEIW4QgbYXNTka6Y/Z4+KcifSdgDSiMiTM3ZXBfg=='
if(-not (Test-Path -LiteralPath $archive)){Invoke-WebRequest -Uri $uri -OutFile $archive}
$actual=[Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($archive)))
if($actual -ne $integrity){throw 'Upstream package SHA-512 mismatch; refusing extraction.'}
$manifestPath=Join-Path $PSScriptRoot 'upstream-android-1.5.2.json'
$artifacts=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$members=@(& tar -tf $archive)
if($LASTEXITCODE -ne 0){throw 'Could not inspect pinned upstream archive.'}
foreach($artifact in $artifacts){
    if($artifact.path -notin @('package/bin/pi-env-android-arm64/pi-env','package/bin/pi-env-android-x64/pi-env')){throw 'Unexpected upstream artifact path.'}
    if(@($members | Where-Object {$_ -ceq $artifact.path}).Count -ne 1){throw 'Pinned artifact must occur exactly once in archive.'}
    $listing=@(& tar -tvf $archive $artifact.path)
    if($LASTEXITCODE -ne 0 -or $listing.Count -ne 1 -or -not $listing[0].StartsWith('-')){throw 'Pinned artifact is not an ordinary file.'}
}
foreach($artifact in $artifacts){
    # Extract only these fixed ordinary-file names; no package scripts, dependencies or installation hooks run.
    & tar -xf $archive -C $destination $artifact.path
    if($LASTEXITCODE -ne 0){throw 'Could not extract pinned upstream daemon.'}
    $file=Join-Path $destination $artifact.path
    $hash=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if($hash -ne $artifact.sha256){throw 'Upstream daemon SHA-256 mismatch.'}
    $bytes=[IO.File]::ReadAllBytes($file)
    if($bytes.Length -lt 20){throw 'Upstream daemon ELF header is truncated.'}
    $machine=[BitConverter]::ToUInt16($bytes,18)
    $expected=if($artifact.arch -eq 'arm64'){183}else{62}
    if($bytes[0] -ne 127 -or $bytes[1] -ne 69 -or $bytes[2] -ne 76 -or $bytes[3] -ne 70 -or $bytes[4] -ne 2 -or $bytes[5] -ne 1 -or $machine -ne $expected){throw 'Upstream daemon ELF architecture mismatch.'}
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $destination 'daemon-catalog.json') -Force
Write-Output (Join-Path $destination 'daemon-catalog.json')
