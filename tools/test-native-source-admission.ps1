param(
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][string]$SourceManifest,
    [Parameter(Mandatory)][string]$SourceManifestSha256,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
# Authored controls for a separately authorized PowerShell execution window.
# No restore, compiler, product load or Source provider is invoked here.
if ((Get-FileHash -LiteralPath $SourceManifest -Algorithm SHA256).Hash.ToLowerInvariant() -cne $SourceManifestSha256) { throw 'Manifest changed.' }
$source = Get-Content -LiteralPath $SourceManifest -Raw | ConvertFrom-Json
$Repo = (Resolve-Path -LiteralPath $Repo).Path
$helper = Join-Path $Repo 'tools/native-source-admission.ps1'
$helperPin = @($source.files | Where-Object relative -eq 'tools/native-source-admission.ps1')
if ($helperPin.Count -ne 1 -or (Get-Item -LiteralPath $helper).Length -ne $helperPin[0].bytes -or
    (Get-FileHash -LiteralPath $helper -Algorithm SHA256).Hash.ToLowerInvariant() -cne $helperPin[0].sha256) { throw 'Actual admission helper is not pinned.' }
. $helper
$originalClosure = Assert-NativeSourceClosure -Repo $Repo -Source $source
$evidencePath = [IO.Path]::GetFullPath($EvidenceDirectory)
$relativeEvidence = [IO.Path]::GetRelativePath($Repo, $evidencePath).Replace('\', '/')
if (-not $relativeEvidence.StartsWith('artifacts/', [StringComparison]::OrdinalIgnoreCase) -or
    $relativeEvidence.Split('/') -contains '..' -or (Test-Path -LiteralPath $evidencePath)) { throw 'Fresh owned artifacts evidence directory required.' }
$evidenceTop = 'artifacts/' + $relativeEvidence.Split('/')[1]
if ($originalClosure.protectedArtifactSourceRoots -contains $evidenceTop -or @($source.files | Where-Object relative -eq $evidenceTop).Count) { throw 'Evidence cannot overlap a pinned artifact source branch.' }
$ancestor = Split-Path -Parent $evidencePath
while ($ancestor -and -not $ancestor.Equals($Repo, [StringComparison]::OrdinalIgnoreCase)) {
    if (Test-Path -LiteralPath $ancestor) {
        if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Evidence ancestor is linked.' }
    }
    $ancestor = Split-Path -Parent $ancestor
}
New-Item -ItemType Directory -Path $evidencePath | Out-Null
$rows = [Collections.Generic.List[object]]::new()
function Write-ControlEvidence([string]$Path, [object]$Evidence) {
    $stream = $null; $writer = $null
    try {
        $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false), 4096, $true)
        $writer.WriteLine(($Evidence | ConvertTo-Json -Depth 30)); $writer.Flush(); $stream.Flush($true)
    } finally {
        if ($null -ne $writer) { $writer.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
    }
}
function Write-AddedFile([string]$Scratch, [string]$Relative, [string]$Text) {
    $file = Resolve-NativeSourcePath -Repo $Scratch -Relative $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file)) | Out-Null
    $stream = [IO.FileStream]::new($file, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
        $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true)
    } finally { $stream.Dispose() }
    return $file
}
function Invoke-Control([string]$Id, [string]$ExpectedTag, [scriptblock]$Body) {
    $scratch = Join-Path $evidencePath $Id
    [IO.Directory]::CreateDirectory($scratch) | Out-Null
    # Every case uses the actual complete immutable source set. There is no
    # parallel implementation of the admission algorithm in these controls.
    foreach ($pin in $source.files) {
        if ($pin.relative -match '(?i)\.(dll|exe|pdb)$') { throw 'Controls never copy native products.' }
        $from = Resolve-NativeSourcePath -Repo $Repo -Relative $pin.relative
        $to = Resolve-NativeSourcePath -Repo $scratch -Relative $pin.relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to)) | Out-Null
        [IO.File]::Copy($from, $to, $false)
    }
    $row = [ordered]@{ id = $Id; expectedTag = $ExpectedTag; passed = $false; actualError = $null;
        actualAdmissionFunction = 'Assert-NativeSourceClosure / Assert-NativeGeneratedInputPins'; scratch = $scratch; nativeExecutions = 0 }
    try {
        & $Body $scratch
        if ($ExpectedTag) { throw 'Expected actual admission rejection did not occur.' }
        $row.passed = $true
    } catch {
        $row.actualError = $_.Exception.ToString()
        if ($ExpectedTag -and $_.Exception.Message.StartsWith($ExpectedTag + ':', [StringComparison]::Ordinal)) { $row.passed = $true }
    } finally {
        $rows.Add([pscustomobject]$row)
        Write-ControlEvidence -Path (Join-Path $evidencePath ($Id + '.json')) -Evidence $row
    }
    if (-not $row.passed) { throw "Admission control failed: $Id. Later controls stopped; evidence preserved." }
}
Invoke-Control '01-exact-source' '' {
    param($scratch)
    $observed = Assert-NativeSourceClosure -Repo $scratch -Source $source -RequireFreshGeneratedRoots
    if ($observed.sourceFilesVerified -ne $source.files.Count -or $observed.generatedFiles.Count) { throw 'Exact-source observation mismatch.' }
}
Invoke-Control '02-extra-globbed-csharp' 'NATIVE-SOURCE-UNLISTED' {
    param($scratch)
    Write-AddedFile $scratch 'src/PiSharp.Tui/UnlistedProbe.cs' 'namespace PiSharp.Tui; internal class UnlistedProbe { }' | Out-Null
    Assert-NativeSourceClosure -Repo $scratch -Source $source | Out-Null
}
Invoke-Control '03-extra-build-props' 'NATIVE-SOURCE-UNLISTED' {
    param($scratch)
    Write-AddedFile $scratch 'src/PiSharp.Tui/Directory.Build.props' '<Project />' | Out-Null
    Assert-NativeSourceClosure -Repo $scratch -Source $source | Out-Null
}
Invoke-Control '04-extra-resource' 'NATIVE-SOURCE-UNLISTED' {
    param($scratch)
    Write-AddedFile $scratch 'src/PiSharp.Tui/UnlistedProbe.resx' '<root />' | Out-Null
    Assert-NativeSourceClosure -Repo $scratch -Source $source | Out-Null
}
Invoke-Control '05-impostor-output-directory' 'NATIVE-SOURCE-UNLISTED' {
    param($scratch)
    Write-AddedFile $scratch 'src/PiSharp.Tui/unlisted/bin/UnlistedProbe.cs' 'class UnlistedProbe { }' | Out-Null
    Assert-NativeSourceClosure -Repo $scratch -Source $source | Out-Null
}
Invoke-Control '06-hidden-unlisted-source' 'NATIVE-SOURCE-UNLISTED' {
    param($scratch)
    $file = Write-AddedFile $scratch 'src/PiSharp.Tui/HiddenProbe.cs' 'class HiddenProbe { }'
    [IO.File]::SetAttributes($file, [IO.FileAttributes]::Hidden)
    Assert-NativeSourceClosure -Repo $scratch -Source $source | Out-Null
}
Invoke-Control '07-changed-pinned-source' 'NATIVE-SOURCE-PIN' {
    param($scratch)
    $pin = $source.files | Where-Object relative -eq 'src/PiSharp.Tui/PiSharp.Tui.csproj'
    [IO.File]::AppendAllText((Resolve-NativeSourcePath $scratch $pin.relative), "`n<!-- changed control -->`n")
    Assert-NativeSourceClosure -Repo $scratch -Source $source | Out-Null
}
Invoke-Control '08-missing-pinned-source' 'NATIVE-SOURCE-MISSING' {
    param($scratch)
    [IO.File]::Move((Join-Path $scratch 'src/PiSharp.Tui/PiSharp.Tui.csproj'), (Join-Path $evidencePath '08-preserved-project.csproj'))
    Assert-NativeSourceClosure -Repo $scratch -Source $source | Out-Null
}
Invoke-Control '09-duplicate-manifest-pin' 'NATIVE-SOURCE-MANIFEST' {
    param($scratch)
    $duplicate = ($source | ConvertTo-Json -Depth 30 | ConvertFrom-Json)
    $duplicate.files = @($duplicate.files) + @($duplicate.files[0])
    Assert-NativeSourceClosure -Repo $scratch -Source $duplicate | Out-Null
}
Invoke-Control '10-ordinary-generated-roots' '' {
    param($scratch)
    Write-AddedFile $scratch 'src/PiSharp.Tui/obj/Release/net10.0/Generated.cs' '// authored generated-path control, never compiled' | Out-Null
    Write-AddedFile $scratch 'src/PiSharp.Tui/bin/Release/net10.0/generated.txt' 'authored generated-path control' | Out-Null
    Write-AddedFile $scratch 'artifacts/settings/control.json' '{}' | Out-Null
    $observed = Assert-NativeSourceClosure -Repo $scratch -Source $source
    if ($observed.sourceFilesVerified -ne $source.files.Count -or $observed.generatedFiles.Count -ne 2) { throw 'Generated/evidence classification mismatch.' }
    Assert-NativeGeneratedInputPins -Closure $observed -Expected $observed.generatedFiles
}
Invoke-Control '11-stale-obj-before-preparation' 'NATIVE-SOURCE-STALE-GENERATED' {
    param($scratch)
    Write-AddedFile $scratch 'src/PiSharp.Tui/obj/Injected.cs' 'class Injected { }' | Out-Null
    Assert-NativeSourceClosure -Repo $scratch -Source $source -RequireFreshGeneratedRoots | Out-Null
}
Invoke-Control '12-generated-input-injection-after-preparation' 'NATIVE-GENERATED-PIN' {
    param($scratch)
    $before = Assert-NativeSourceClosure -Repo $scratch -Source $source
    Write-AddedFile $scratch 'src/PiSharp.Tui/obj/Injected.cs' 'class Injected { }' | Out-Null
    $after = Assert-NativeSourceClosure -Repo $scratch -Source $source
    Assert-NativeGeneratedInputPins -Closure $after -Expected $before.generatedFiles
}
Invoke-Control '13-changed-generated-input' 'NATIVE-GENERATED-PIN' {
    param($scratch)
    $file = Write-AddedFile $scratch 'src/PiSharp.Tui/obj/Generated.cs' '// version one'
    $before = Assert-NativeSourceClosure -Repo $scratch -Source $source
    [IO.File]::AppendAllText($file, '// version two')
    $after = Assert-NativeSourceClosure -Repo $scratch -Source $source
    Assert-NativeGeneratedInputPins -Closure $after -Expected $before.generatedFiles
}
Invoke-Control '14-missing-generated-input' 'NATIVE-GENERATED-MISSING' {
    param($scratch)
    $file = Write-AddedFile $scratch 'src/PiSharp.Tui/obj/Generated.cs' '// preserved generated-path control'
    $before = Assert-NativeSourceClosure -Repo $scratch -Source $source
    [IO.File]::Move($file, (Join-Path $evidencePath '14-preserved-generated.cs'))
    $after = Assert-NativeSourceClosure -Repo $scratch -Source $source
    Assert-NativeGeneratedInputPins -Closure $after -Expected $before.generatedFiles
}
Invoke-Control '15-linked-evidence-root' 'NATIVE-SOURCE-REPARSE' {
    param($scratch)
    $destination = Join-Path $evidencePath '15-owned-junction-destination'
    New-Item -ItemType Directory -Path $destination | Out-Null
    $linkPath = Join-Path $scratch 'artifacts/owned-linked-control'
    New-Item -ItemType Junction -Path $linkPath -Target $destination | Out-Null
    try {
        $link = Get-Item -LiteralPath $linkPath -Force
        Write-ControlEvidence -Path (Join-Path $evidencePath '15-junction-observation.json') -Evidence @{
            path = $link.FullName; attributes = $link.Attributes.ToString(); linkType = $link.LinkType; target = $link.Target;
            ownedScratch = $scratch; ownedDestination = $destination; recursiveRemoval = $false
        }
        Assert-NativeSourceClosure -Repo $scratch -Source $source | Out-Null
    } finally {
        # Remove only this owned junction, never its destination or a recursive
        # tree. Otherwise final admission of the original evidence root would
        # correctly reject the intentionally introduced link left underneath it.
        $link = Get-Item -LiteralPath $linkPath -Force
        $targets = @($link.Target)
        if (-not $link.FullName.Equals([IO.Path]::GetFullPath($linkPath), [StringComparison]::OrdinalIgnoreCase) -or
            -not ($link.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $link.LinkType -cne 'Junction' -or
            $targets.Count -ne 1 -or
            -not [IO.Path]::GetFullPath($targets[0]).Equals([IO.Path]::GetFullPath($destination), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Owned junction identity changed; no removal attempted.'
        }
        [IO.Directory]::Delete($link.FullName)
    }
}
Invoke-Control '16-extra-input-in-protected-artifacts' 'NATIVE-SOURCE-UNLISTED' {
    param($scratch)
    Write-AddedFile $scratch 'artifacts/released-baseline/UnlistedProbe.cs' 'class UnlistedProbe { }' | Out-Null
    Assert-NativeSourceClosure -Repo $scratch -Source $source | Out-Null
}
$finalOriginal = Assert-NativeSourceClosure -Repo $Repo -Source $source
Assert-NativeGeneratedInputPins -Closure $finalOriginal -Expected $originalClosure.generatedFiles
Write-ControlEvidence -Path (Join-Path $evidencePath 'receipt.json') -Evidence @{
    schemaVersion = 1; sourceManifestSha256 = $SourceManifestSha256; candidate = $source.candidate; tree = $source.tree;
    controls = $rows.ToArray(); required = 16; complete = $rows.Count -eq 16 -and @($rows | Where-Object { -not $_.passed }).Count -eq 0;
    originalSourceUnchanged = $true; actualAdmissionFunctionsExercised = $true; compilerExecutions = 0; nativeExecutions = 0;
    authoredControlsAreGenuineSourceCaptures = $false; packageAcceptance = $false; phaseAcceptance = $false
}
