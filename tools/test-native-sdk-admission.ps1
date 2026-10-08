param(
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][string]$SourceManifest,
    [Parameter(Mandatory)][string]$SourceManifestSha256,
    [Parameter(Mandatory)][string]$BuildReceipt,
    [Parameter(Mandatory)][string]$BuildReceiptSha256,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
# Future separately authorized admission controls only. Inputs must come from
# actual complete preparation; no SDK, compiler, product or Source is executed.
$Repo = (Resolve-Path -LiteralPath $Repo).Path
if ((Get-FileHash -LiteralPath $SourceManifest -Algorithm SHA256).Hash.ToLowerInvariant() -cne $SourceManifestSha256) { throw 'Source manifest changed.' }
$source = Get-Content -LiteralPath $SourceManifest -Raw | ConvertFrom-Json
foreach ($relative in @('tools/native-companion-registration.ps1', 'tools/native-source-admission.ps1', 'tools/test-native-sdk-admission.ps1')) {
    $expected = @($source.files | Where-Object relative -eq $relative)
    $file = Join-Path $Repo $relative
    if ($expected.Count -ne 1 -or (Get-Item -LiteralPath $file).Length -ne $expected[0].bytes -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected[0].sha256) { throw 'Actual SDK admission helper/control is not pinned.' }
}
. (Join-Path $Repo 'tools/native-companion-registration.ps1')
$receiptPin = Read-NativePinnedJson -Path $BuildReceipt -Sha256 $BuildReceiptSha256
$originalReceipt = $receiptPin.document
$originalClosure = Assert-NativeSourceClosure -Repo $Repo -Source $source
$evidencePath = [IO.Path]::GetFullPath($EvidenceDirectory)
$relativeEvidence = [IO.Path]::GetRelativePath($Repo, $evidencePath).Replace('\', '/')
if ([IO.Path]::IsPathRooted($relativeEvidence) -or -not $relativeEvidence.StartsWith('artifacts/', [StringComparison]::OrdinalIgnoreCase) -or
    @($relativeEvidence.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -or (Test-Path -LiteralPath $evidencePath)) {
    throw 'Fresh owned artifacts evidence directory required.'
}
$evidenceTop = 'artifacts/' + $relativeEvidence.Split('/')[1]
if ($originalClosure.protectedArtifactSourceRoots -contains $evidenceTop -or @($source.files | Where-Object relative -eq $evidenceTop).Count) {
    throw 'Evidence cannot overlap pinned artifact source.'
}
$ancestor = Split-Path -Parent $evidencePath
while ($ancestor -and -not $ancestor.Equals($Repo, [StringComparison]::OrdinalIgnoreCase)) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Evidence ancestor is linked.' }
    $ancestor = Split-Path -Parent $ancestor
}
New-Item -ItemType Directory -Path $evidencePath | Out-Null
$rows = [Collections.Generic.List[object]]::new()
Write-NativeLaunchEvidence -Path (Join-Path $evidencePath 'started.json') -Evidence @{
    schemaVersion = 1; candidate = $source.candidate; tree = $source.tree; originalReceipt = @{ path = $receiptPin.path; sha256 = $receiptPin.sha256 };
    requiredControls = 10; controlsCompleted = 0; passingPreparationReceipt = $false; packageAcceptance = $false; nativeExecutions = 0 }
function Invoke-SdkControl([string]$Id, [string]$ExpectedTag, [scriptblock]$Mutate) {
    $row = [ordered]@{ schemaVersion = 1; id = $Id; expectedTag = $ExpectedTag; passed = $false; observedError = $null;
        actualAdmissionFunction = 'Get-NativePreparedValidation / Get-NativeSdkSelectionEvidence'; sdkSelection = $null; controlReceipt = $null;
        controlOnly = $true; passingPreparationReceipt = $false; packageAcceptance = $false; nativeExecutions = 0 }
    Write-NativeLaunchEvidence -Path (Join-Path $evidencePath ($Id + '.started.json')) -Evidence $row
    try {
        if ((Get-FileHash -LiteralPath $receiptPin.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $receiptPin.sha256) { throw 'Original actual receipt changed.' }
        $copy = $originalReceipt | ConvertTo-Json -Depth 50 | ConvertFrom-Json
        & $Mutate $copy
        $copy | Add-Member -NotePropertyName admissionControlOnly -NotePropertyValue $true
        $controlFile = Join-Path $evidencePath ($Id + '.control-receipt.json')
        Write-NativeLaunchEvidence -Path $controlFile -Evidence $copy
        $controlHash = (Get-FileHash -LiteralPath $controlFile -Algorithm SHA256).Hash.ToLowerInvariant()
        $row.controlReceipt = @{ path = $controlFile; sha256 = $controlHash; isActualPreparationProof = $false }
        $validation = Get-NativePreparedValidation -Repo $Repo -SourceManifest $SourceManifest -SourceManifestSha256 $SourceManifestSha256 -BuildReceipt $controlFile -BuildReceiptSha256 $controlHash
        $row.sdkSelection = $validation.sdkSelection
        if ($ExpectedTag) { throw 'Expected SDK admission rejection did not occur.' }
        if ($null -eq $validation.sdkSelection -or $validation.sdkSelection.sdkVersion -cne $originalReceipt.sdkVersion) { throw 'Actual baseline SDK association was not observed.' }
        $row.passed = $true
    } catch {
        $row.observedError = $_.Exception.ToString()
        if ($ExpectedTag -and $_.Exception.Message.StartsWith($ExpectedTag + ':', [StringComparison]::Ordinal)) { $row.passed = $true }
    } finally {
        $rows.Add([pscustomobject]$row)
        Write-NativeLaunchEvidence -Path (Join-Path $evidencePath ($Id + '.settled.json')) -Evidence $row
    }
    if (-not $row.passed) { throw "SDK admission control failed: $Id. Later controls stopped; intended rejection evidence preserved." }
}
Invoke-SdkControl '01-actual-complete-sdk-selection' '' { param($copy) }
Invoke-SdkControl '02-missing-sdk-step' 'NATIVE-SDK-STEP-MISSING' {
    param($copy)
    $copy.actualPreparationSteps = @($copy.actualPreparationSteps | Where-Object id -ne 'dotnet-sdk-version')
}
Invoke-SdkControl '03-missing-sdk-log-declaration' 'NATIVE-SDK-LOG-MISSING' {
    param($copy)
    ($copy.actualPreparationSteps | Where-Object id -eq 'dotnet-sdk-version').log = $null
}
Invoke-SdkControl '04-missing-actual-sdk-log' 'NATIVE-SDK-LOG-MISSING' {
    param($copy)
    ($copy.actualPreparationSteps | Where-Object id -eq 'dotnet-sdk-version').log.path = Join-Path $evidencePath 'absent-original-sdk.log'
}
Invoke-SdkControl '05-mismatched-recorded-sdk-version' 'NATIVE-SDK-VERSION-MISMATCH' {
    param($copy)
    $copy.sdkVersion = $originalReceipt.sdkVersion + '-admission-control'
}
Invoke-SdkControl '06-missing-recorded-sdk-version' 'NATIVE-SDK-VERSION-MISMATCH' {
    param($copy)
    $copy.PSObject.Properties.Remove('sdkVersion')
}
Invoke-SdkControl '07-changed-sdk-log-pin' 'NATIVE-SDK-LOG-PIN' {
    param($copy)
    ($copy.actualPreparationSteps | Where-Object id -eq 'dotnet-sdk-version').log.sha256 = '0' * 64
}
Invoke-SdkControl '08-unsettled-original-sdk-child' 'NATIVE-SDK-STEP-INCOMPLETE' {
    param($copy)
    ($copy.actualPreparationSteps | Where-Object id -eq 'dotnet-sdk-version').originalChildReturned = $false
}
Invoke-SdkControl '09-duplicate-sdk-step' 'NATIVE-SDK-STEP-DUPLICATE' {
    param($copy)
    $step = $copy.actualPreparationSteps | Where-Object id -eq 'dotnet-sdk-version'
    $copy.actualPreparationSteps = @($copy.actualPreparationSteps) + @($step)
}
Invoke-SdkControl '10-sdk-differs-from-pinned-policy' 'NATIVE-SDK-VERSION-POLICY' {
    param($copy)
    # Negative control input only; never an actual SDK observation or passing proof.
    $badLog = Join-Path $evidencePath '10-negative-control-sdk.log'
    $stream = [IO.FileStream]::new($badLog, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
    try { $bytes = [Text.Encoding]::UTF8.GetBytes('10.0.999'); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    ($copy.actualPreparationSteps | Where-Object id -eq 'dotnet-sdk-version').log = @{
        path = $badLog; bytes = (Get-Item -LiteralPath $badLog).Length; sha256 = (Get-FileHash -LiteralPath $badLog -Algorithm SHA256).Hash.ToLowerInvariant() }
    $copy.sdkVersion = '10.0.999'
}
if ($rows.Count -ne 10 -or @($rows | Where-Object { -not $_.passed }).Count) { throw 'Incomplete SDK admission controls.' }
# Revalidate the original actual receipt and source/generated/product identity;
# control copies do not replace, mutate, or authorize the actual preparation.
Get-NativePreparedValidation -Repo $Repo -SourceManifest $SourceManifest -SourceManifestSha256 $SourceManifestSha256 -BuildReceipt $BuildReceipt -BuildReceiptSha256 $BuildReceiptSha256 | Out-Null
Write-NativeLaunchEvidence -Path (Join-Path $evidencePath 'complete.json') -Evidence @{
    schemaVersion = 1; candidate = $source.candidate; tree = $source.tree; required = 10; controls = $rows.ToArray(); allControlsPassed = $true;
    originalActualReceiptUnchanged = $true; sourceGeneratedProductsUnchanged = $true; passingPreparationReceipt = $false;
    packageAcceptance = $false; phaseAcceptance = $false; nativeExecutions = 0 }
