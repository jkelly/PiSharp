# Byte/capture inspection only. No process, installation, extraction or acquisition.
# Call only after the coordinator has joined the separately admitted originals.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../packaging/distribution-validation.ps1')

function Assert-PiSharpOrdinaryInstallPath {
    param([Parameter(Mandatory)][string]$Path)
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and
            ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Linked installation path refused.'
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function Assert-PiSharpToolPayloadBytes {
    param([Parameter(Mandatory)][string]$PayloadRoot,
        [Parameter(Mandatory)][object[]]$ArchiveFiles)
    $root = [IO.Path]::GetFullPath($PayloadRoot)
    Assert-PiSharpOrdinaryInstallPath $root
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'Installed payload absent.' }
    $prefix = 'tools/net10.0/any/'
    $expected = @($ArchiveFiles | Where-Object { $_.path.StartsWith($prefix, [StringComparison]::Ordinal) })
    if ($expected.Count -eq 0) { throw 'Tool payload inventory empty.' }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $expected) {
        $relative = $entry.path.Substring($prefix.Length)
        if ([string]::IsNullOrEmpty($relative) -or $relative.Contains('\') -or $relative.Contains(':') -or
            $relative.StartsWith('/') -or @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -or
            -not $names.Add($relative)) { throw 'Nonportable or duplicate installed member.' }
    }
    # Check directory entries before recursion so a linked subtree is never followed.
    $pending = [Collections.Generic.Queue[string]]::new(); $pending.Enqueue($root)
    $files = [Collections.Generic.List[object]]::new()
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath ($pending.Dequeue()) -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked payload entry refused.' }
            if ($item.PSIsContainer) { $pending.Enqueue($item.FullName) } else { $files.Add($item) }
        }
    }
    if ($files.Count -ne $expected.Count) { throw 'Installed payload membership differs.' }
    $actualNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $files) { $actualNames.Add([IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')) | Out-Null }
    foreach ($entry in $expected) {
        $relative = $entry.path.Substring($prefix.Length)
        if (-not $actualNames.Contains($relative)) { throw 'Installed payload name differs.' }
        $path = Join-Path $root $relative
        if ((Get-Item -LiteralPath $path).Length -ne $entry.bytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256) {
            throw 'Installed payload bytes differ.'
        }
    }
    return [pscustomobject]@{ fileCount = $expected.Count; exactPayloadBytes = $true; platformQualified = $false }
}

function Assert-PiSharpWindowsToolInstallation {
    param([Parameter(Mandatory)][string]$Package,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$PackageSha256,
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
        [Parameter(Mandatory)][string]$ToolRoot,
        [Parameter(Mandatory)][string]$BuildRoot)
    if ((Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash.ToLowerInvariant() -cne $PackageSha256) {
        throw 'Installed candidate package pin differs.'
    }
    $archive = Assert-PiSharpDistribution -Path $Package -Repo $Repo -Kind tool -Version $Version -SourceCommit $SourceCommit -EnablePromptTemplateYaml $true
    $root = [IO.Path]::GetFullPath($ToolRoot)
    Assert-PiSharpOrdinaryInstallPath $root
    $payload = Join-Path $root ".store/pisharp.cli/$Version/pisharp.cli/$Version/tools/net10.0/any"
    $comparison = Assert-PiSharpToolPayloadBytes -PayloadRoot $payload -ArchiveFiles $archive.files
    $dlls = @($archive.files | Where-Object { $_.path -cmatch '^tools/net10\.0/any/[^/]+\.dll$' })
    if ($dlls.Count -eq 0) { throw 'Package has no build assemblies.' }
    foreach ($dll in $dlls) {
        $path = Join-Path $BuildRoot ([IO.Path]::GetFileName($dll.path))
        Assert-PiSharpOrdinaryInstallPath $path
        if ((Get-Item -LiteralPath $path).Length -ne $dll.bytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $dll.sha256) {
            throw 'Package assembly differs from current admitted build.'
        }
    }
    $shim = Join-Path $root 'pisharp.exe'; Assert-PiSharpOrdinaryInstallPath $shim
    if (-not (Test-Path -LiteralPath $shim -PathType Leaf) -or (Get-Item -LiteralPath $shim).Length -eq 0) { throw 'Installed Windows shim absent.' }
    return [pscustomobject]@{ schemaVersion = 1; evidenceKind = 'private-install-byte-inspection';
        sourceCommit = $SourceCommit; version = $Version; archiveSha256 = $PackageSha256;
        installedFileCount = $comparison.fileCount; buildAssemblyCount = $dlls.Count;
        shim = @{ path = $shim; bytes = (Get-Item -LiteralPath $shim).Length;
            sha256 = (Get-FileHash -LiteralPath $shim -Algorithm SHA256).Hash.ToLowerInvariant() };
        nodeFreePayload = $true; enablePromptTemplateYaml = $true;
        processExecutionProven = $false; platformQualified = $false; releaseAccepted = $false }
}

function Assert-PiSharpInstalledSmokeCapture {
    param([Parameter(Mandatory)][ValidateSet('help', 'invalid-argument')][string]$Variant,
        [Parameter(Mandatory)][int]$ExitCode,
        [Parameter(Mandatory)][AllowEmptyString()][string]$StandardOutput,
        [Parameter(Mandatory)][AllowEmptyString()][string]$StandardError)
    if ($StandardOutput.Length -gt 1048576 -or $StandardError.Length -gt 1048576) { throw 'Smoke capture exceeds bound.' }
    if ($Variant -ceq 'help') {
        if ($ExitCode -ne 0 -or -not $StandardOutput.StartsWith('Usage: PiSharp.Cli ', [StringComparison]::Ordinal) -or
            $StandardError.Length -ne 0) { throw 'Installed help contract differs.' }
    } else {
        if ($ExitCode -ne 2 -or $StandardOutput.Length -ne 0) { throw 'Installed invalid-argument exit/stream contract differs.' }
        $errorRecord = $StandardError | ConvertFrom-Json -AsHashtable -NoEnumerate
        if ($errorRecord -isnot [Collections.IDictionary] -or $errorRecord.schemaVersion -ne 1 -or
            $errorRecord.status -cne 'failed' -or $errorRecord.code -cne 'InvalidArguments') { throw 'Installed invalid-argument public code differs.' }
    }
    return [pscustomobject]@{ variant = $Variant; exitCode = $ExitCode; captureContractMatched = $true;
        originalSettlementProven = $false; releaseAccepted = $false }
}
