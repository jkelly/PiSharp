param(
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][string]$SourceManifest,
    [Parameter(Mandatory)][string]$SourceManifestSha256,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
# Authored, unexecuted controls for a separately authorized PowerShell window.
# Synthetic text host files/receipts are control inputs, never preparation proof.
# Nothing here executes a host, SDK, product, compiler or provider.
$Repo = (Resolve-Path -LiteralPath $Repo).Path
if ((Get-FileHash -LiteralPath $SourceManifest -Algorithm SHA256).Hash.ToLowerInvariant() -cne $SourceManifestSha256) { throw 'Source manifest changed.' }
$source = Get-Content -LiteralPath $SourceManifest -Raw | ConvertFrom-Json
foreach ($relative in @('tools/native-companion-registration.ps1', 'tools/native-source-admission.ps1',
    'tools/test-native.ps1', 'tools/test-native-launch-host-admission.ps1')) {
    $pin = @($source.files | Where-Object relative -eq $relative)
    $file = Join-Path $Repo $relative
    if ($pin.Count -ne 1 -or (Get-Item -LiteralPath $file).Length -ne $pin[0].bytes -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pin[0].sha256) { throw 'Host admission source/control differs from pinned input.' }
}
. (Join-Path $Repo 'tools/native-companion-registration.ps1')
$closure = Assert-NativeSourceClosure -Repo $Repo -Source $source
$evidencePath = [IO.Path]::GetFullPath($EvidenceDirectory)
$relativeEvidence = [IO.Path]::GetRelativePath($Repo, $evidencePath).Replace('\', '/')
if ([IO.Path]::IsPathRooted($relativeEvidence) -or -not $relativeEvidence.StartsWith('artifacts/', [StringComparison]::OrdinalIgnoreCase) -or
    @($relativeEvidence.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -or (Test-Path -LiteralPath $evidencePath)) {
    throw 'Fresh owned artifacts evidence directory required.'
}
$evidenceTop = 'artifacts/' + $relativeEvidence.Split('/')[1]
if ($closure.protectedArtifactSourceRoots -contains $evidenceTop -or @($source.files | Where-Object relative -eq $evidenceTop).Count) { throw 'Evidence overlaps pinned artifact source.' }
$ancestor = Split-Path -Parent $evidencePath
while ($ancestor -and -not $ancestor.Equals($Repo, [StringComparison]::OrdinalIgnoreCase)) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Evidence ancestor is linked.' }
    $ancestor = Split-Path -Parent $ancestor
}
New-Item -ItemType Directory -Path $evidencePath | Out-Null
$hostPath = Join-Path $evidencePath 'recorded-host.control.txt'
$otherPath = Join-Path $evidencePath 'same-bytes-other-path.control.txt'
$ambientDirectory = Join-Path $evidencePath 'ambient'
New-Item -ItemType Directory -Path $ambientDirectory | Out-Null
# The .exe name is only a PATH-substitution sentinel; its bytes are inert text.
$ambientHost = Join-Path $ambientDirectory 'dotnet.exe'
foreach ($file in @($hostPath, $otherPath, $ambientHost)) { [IO.File]::WriteAllText($file, 'inert host admission control') }
$hostPin = @{ path = $hostPath; bytes = (Get-Item -LiteralPath $hostPath).Length;
    sha256 = (Get-FileHash -LiteralPath $hostPath -Algorithm SHA256).Hash.ToLowerInvariant() }
$rows = [Collections.Generic.List[object]]::new()
function Invoke-HostControl([string]$Id, [string]$ExpectedTag, [scriptblock]$Mutate, [scriptblock]$Select) {
    $row = [ordered]@{ id = $Id; passed = $false; expectedTag = $ExpectedTag; observedError = $null;
        controlOnly = $true; nativeExecutions = 0; passingPreparationReceipt = $false; packageAcceptance = $false }
    try {
        $copy = @{ host = ($hostPin | ConvertTo-Json | ConvertFrom-Json); admissionControlOnly = $true }
        & $Mutate $copy
        $receipt = Join-Path $evidencePath ($Id + '.control-receipt.json')
        Write-NativeLaunchEvidence -Path $receipt -Evidence $copy
        $validation = [pscustomobject]@{ buildReceipt = $receipt;
            buildReceiptSha256 = (Get-FileHash -LiteralPath $receipt -Algorithm SHA256).Hash.ToLowerInvariant() }
        & $Select $validation | Out-Null
        if ($ExpectedTag) { throw 'Expected host rejection did not occur.' }
        $row.passed = $true
    } catch {
        $row.observedError = $_.Exception.ToString()
        if ($ExpectedTag -and $_.Exception.Message.StartsWith($ExpectedTag, [StringComparison]::Ordinal)) { $row.passed = $true }
    } finally {
        $rows.Add([pscustomobject]$row)
        Write-NativeLaunchEvidence -Path (Join-Path $evidencePath ($Id + '.settled.json')) -Evidence $row
    }
    if (-not $row.passed) { throw "Host control failed: $Id; later controls stopped." }
}
$savedPath = $env:PATH
try {
    $env:PATH = $ambientDirectory
    Invoke-HostControl '01-ambient-substitution-ignored' '' { param($copy) } {
        param($validation)
        $selected = Get-NativeLaunchHost -PreparedValidation $validation
        if ($selected -cne $hostPath -or $selected -eq $ambientHost) { throw 'Ambient host was selected.' }
    }
    $env:PATH = ''
    Invoke-HostControl '02-no-ambient-host-required' '' { param($copy) } {
        param($validation)
        if ((Get-NativeLaunchHost -PreparedValidation $validation) -cne $hostPath) { throw 'Pinned host was not selected.' }
    }
    Invoke-HostControl '03-same-bytes-different-path-rejected' 'NATIVE-LAUNCH-HOST-SELECTION:' { param($copy) } {
        param($validation)
        Get-NativeLaunchHost -PreparedValidation $validation -SelectedExecutable $otherPath
    }
    Invoke-HostControl '04-missing-host-file' 'NATIVE-LAUNCH-HOST-PIN:' {
        param($copy); $copy.host.path = Join-Path $evidencePath 'absent.control.txt'
    } { param($validation); Get-NativeLaunchHost -PreparedValidation $validation }
    Invoke-HostControl '05-host-size-mismatch' 'NATIVE-LAUNCH-HOST-PIN:' {
        param($copy); $copy.host.bytes++
    } { param($validation); Get-NativeLaunchHost -PreparedValidation $validation }
    Invoke-HostControl '06-host-hash-mismatch' 'NATIVE-LAUNCH-HOST-PIN:' {
        param($copy); $copy.host.sha256 = '0' * 64
    } { param($validation); Get-NativeLaunchHost -PreparedValidation $validation }
    Invoke-HostControl '07-relative-host-path' 'NATIVE-LAUNCH-HOST-PIN:' {
        param($copy); $copy.host.path = 'recorded-host.control.txt'
    } { param($validation); Get-NativeLaunchHost -PreparedValidation $validation }
    Invoke-HostControl '08-host-changed-after-selection' 'NATIVE-LAUNCH-HOST-PIN:' { param($copy) } {
        param($validation)
        $selected = Get-NativeLaunchHost -PreparedValidation $validation
        try {
            [IO.File]::WriteAllText($hostPath, 'changed host control bytes')
            Get-NativeLaunchHost -PreparedValidation $validation -SelectedExecutable $selected
        } finally { [IO.File]::WriteAllText($hostPath, 'inert host admission control') }
    }
    Invoke-HostControl '09-receipt-changed-after-validation' 'Pinned JSON artifact changed:' { param($copy) } {
        param($validation)
        [IO.File]::AppendAllText($validation.buildReceipt, ' ')
        Get-NativeLaunchHost -PreparedValidation $validation
    }
    Invoke-HostControl '10-explicit-empty-selected-host' 'NATIVE-LAUNCH-HOST-SELECTION:' { param($copy) } {
        param($validation); Get-NativeLaunchHost -PreparedValidation $validation -SelectedExecutable ''
    }
} finally { $env:PATH = $savedPath }
if ($rows.Count -ne 10 -or @($rows | Where-Object { -not $_.passed }).Count) { throw 'Incomplete host controls.' }
Assert-NativeSourceClosure -Repo $Repo -Source $source | Out-Null
Write-NativeLaunchEvidence -Path (Join-Path $evidencePath 'complete.json') -Evidence @{
    schemaVersion = 1; candidate = $source.candidate; tree = $source.tree; controls = $rows.ToArray(); required = 10;
    nativeExecutions = 0; passingPreparationReceipt = $false; packageAcceptance = $false; phaseAcceptance = $false }
