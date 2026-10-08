# Offline release producer/consumer. Importing defines functions only.
# Build, install, extraction, subprocesses, network and publication are absent.
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../packaging/distribution-validation.ps1')

function Write-PiSharpStandaloneArchive {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PayloadDirectory,
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][ValidateSet('win-x64','linux-x64','osx-arm64')][string]$Rid)
    $root = [IO.Path]::GetFullPath($PayloadDirectory)
    $destination = [IO.Path]::GetFullPath($ArchivePath)
    if (-not [IO.Directory]::Exists($root) -or $destination.StartsWith($root.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Existing payload directory and archive destination outside payload required.'
    }
    $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($root)
    $files = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $folded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $leases = [Collections.Generic.List[IO.FileStream]]::new()
    $created = $false; $output = $null; $zip = $null; [long]$total = 0
    try {
        while ($pending.Count) {
            $directory = $pending.Pop()
            if (([IO.File]::GetAttributes($directory) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked payload directories are not admitted.' }
            foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
                $attributes = [IO.File]::GetAttributes($path)
                if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked payload entries are not admitted.' }
                $relative = [IO.Path]::GetRelativePath($root, $path).Replace('\','/')
                if ($relative -match '[\\:\x00]' -or $relative.Split('/') -contains '..' -or -not $folded.Add($relative)) { throw 'Payload path is not portable or is duplicated by case.' }
                if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $pending.Push($path); continue }
                if ($files.Count -ge 20000) { throw 'Payload file limit exceeded.' }
                $lease = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                $leases.Add($lease)
                $total += $lease.Length
                if ($lease.Length -gt 536870912 -or $total -gt 2147483648) { throw 'Payload byte limit exceeded.' }
                $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($lease)).ToLowerInvariant(); $lease.Position = 0
                $files.Add($relative, [pscustomobject]@{ path=$relative; bytes=$lease.Length; sha256=$hash; stream=$lease })
            }
        }
        if (-not $files.Count) { throw 'Empty payload is not a release archive.' }
        $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read); $created = $true
        $zip = [IO.Compression.ZipArchive]::new($output, [IO.Compression.ZipArchiveMode]::Create, $true)
        [string[]]$names = @($files.Keys); [Array]::Sort($names, [StringComparer]::Ordinal)
        foreach ($name in $names) {
            $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::NoCompression)
            $entry.LastWriteTime = [DateTimeOffset]::new(1980,1,1,0,0,0,[TimeSpan]::Zero)
            # Stable regular-file modes; owner execute is carried by the Unix apphost.
            [uint32]$mode = if ($Rid -ne 'win-x64' -and $name -ceq 'PiSharp.Cli') { 0x81ed } else { 0x81a4 }
            [uint32]$attributes = [uint32]([uint64]$mode * 65536)
            $entry.ExternalAttributes = [BitConverter]::ToInt32([BitConverter]::GetBytes($attributes),0)
            $writer = $entry.Open()
            try { $files[$name].stream.CopyTo($writer) } finally { $writer.Dispose() }
        }
        $zip.Dispose(); $zip = $null; $output.Dispose(); $output = $null
        $inventory = Get-PiSharpArchiveInventory -Path $destination
        if ($inventory.Count -ne $files.Count) { throw 'Produced archive file inventory differs.' }
        foreach ($name in $files.Keys) {
            if (-not $inventory.ContainsKey($name) -or $inventory[$name].bytes -ne $files[$name].bytes -or $inventory[$name].sha256 -cne $files[$name].sha256) { throw 'Produced archive bytes differ from leased payload.' }
        }
        return [pscustomobject]@{ path=$destination; bytes=([IO.FileInfo]::new($destination)).Length
            sha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
            files=@(foreach ($name in $names) { $inventory[$name] }); rid=$Rid; platformQualified=$false }
    } catch {
        if ($null -ne $zip) { $zip.Dispose(); $zip = $null }
        if ($null -ne $output) { $output.Dispose(); $output = $null }
        if ($created) { [IO.File]::Delete($destination) }
        throw
    } finally {
        if ($null -ne $zip) { $zip.Dispose() }
        if ($null -ne $output) { $output.Dispose() }
        foreach ($lease in $leases) { $lease.Dispose() }
    }
}

function New-PiSharpStandaloneRelease {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PayloadDirectory,
        [Parameter(Mandatory)][string]$ArchivePath, [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$Repo, [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
        [Parameter(Mandatory)][ValidateSet('win-x64','linux-x64','osx-arm64')][string]$Rid,
        [bool]$EnablePromptTemplateYaml = $true)
    $archive = [IO.Path]::GetFullPath($ArchivePath); $manifest = [IO.Path]::GetFullPath($ManifestPath)
    $payloadRoot = [IO.Path]::GetFullPath($PayloadDirectory).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if ($manifest.StartsWith($payloadRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest destination outside payload required.' }
    if ([IO.File]::Exists($archive) -or [IO.File]::Exists($manifest) -or $archive.Equals($manifest,[StringComparison]::OrdinalIgnoreCase)) { throw 'Release outputs must be new distinct paths; existing versions are never replaced.' }
    $artifact = $null; $createdManifest = $false; $writer = $null; $archiveLease = $null
    try {
        $artifact = Write-PiSharpStandaloneArchive -PayloadDirectory $PayloadDirectory -ArchivePath $archive -Rid $Rid
        $archiveLease = [IO.File]::Open($archive,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        $retainedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($archiveLease)).ToLowerInvariant()
        if ($archiveLease.Length -ne $artifact.bytes -or $retainedHash -cne $artifact.sha256) { throw 'Archive changed before release validation lease.' }
        $validation = Assert-PiSharpDistribution -Path $archive -Repo $Repo -Kind standalone -Version $Version -SourceCommit $SourceCommit -Rid $Rid -EnablePromptTemplateYaml $EnablePromptTemplateYaml
        $record = [ordered]@{ schemaVersion=1; kind='standalone'; version=$Version; sourceCommit=$SourceCommit
            rid=$Rid; sdk=$validation.sdk; baselineTag=$validation.baselineTag; baselineCommit=$validation.baselineCommit
            enablePromptTemplateYaml=$EnablePromptTemplateYaml
            artifact=[ordered]@{ fileName=[IO.Path]::GetFileName($archive); bytes=$artifact.bytes; sha256=$artifact.sha256 }
            files=$artifact.files; evidenceKind='offline-artifact-structure'; platformQualified=$false
            licenseClosure=$validation.licenseClosure }
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($record | ConvertTo-Json -Depth 20) + "`n")
        $writer = [IO.File]::Open($manifest,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read); $createdManifest=$true
        $writer.Write($bytes); $writer.Flush($true); $writer.Dispose(); $writer=$null
        return [pscustomobject]@{ manifest=$manifest; manifestSha256=(Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash.ToLowerInvariant()
            artifact=$artifact; platformQualified=$false; installed=$false }
    } catch {
        if ($null -ne $writer) { $writer.Dispose(); $writer=$null }
        if ($null -ne $archiveLease) { $archiveLease.Dispose(); $archiveLease=$null }
        if ($createdManifest) { [IO.File]::Delete($manifest) }
        if ($null -ne $artifact) { [IO.File]::Delete($archive) }
        throw
    } finally {
        if ($null -ne $writer) { $writer.Dispose() }
        if ($null -ne $archiveLease) { $archiveLease.Dispose() }
    }
}

function Resolve-PiSharpStandaloneRelease {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedManifestSha256,
        [Parameter(Mandatory)][string]$ArchivePath, [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
        [Parameter(Mandatory)][ValidateSet('win-x64','linux-x64','osx-arm64')][string]$Rid,
        [string]$CurrentVersion, [ValidatePattern('^[0-9a-f]{64}$')][string]$CurrentArchiveSha256)
    if ([string]::IsNullOrEmpty($CurrentVersion) -ne [string]::IsNullOrEmpty($CurrentArchiveSha256)) { throw 'Current version and immutable archive hash must be supplied together.' }
    $manifest = [IO.File]::Open([IO.Path]::GetFullPath($ManifestPath),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $artifact = $null
    try {
        if ($manifest.Length -gt 16777216) { throw 'Release manifest limit exceeded.' }
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifest)).ToLowerInvariant() -cne $ExpectedManifestSha256) { throw 'Manifest differs from independently supplied checksum.' }
        $manifest.Position=0
        $reader=[IO.StreamReader]::new($manifest,[Text.UTF8Encoding]::new($false,$true),$false,4096,$true)
        try { $record=$reader.ReadToEnd() | ConvertFrom-Json -AsHashtable } finally { $reader.Dispose() }
        if ($record.schemaVersion -ne 1 -or $record.kind -cne 'standalone' -or $record.version -cne $Version -or
            $record.sourceCommit -cne $SourceCommit -or $record.rid -cne $Rid -or $record.platformQualified -ne $false -or
            $record.enablePromptTemplateYaml -isnot [bool]) { throw 'Release version, source or supported target mismatch.' }
        $archive=[IO.Path]::GetFullPath($ArchivePath)
        if ([IO.Path]::GetFileName($archive) -cne $record.artifact.fileName -or $record.artifact.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Explicit artifact identity differs.' }
        $artifact=[IO.File]::Open($archive,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($artifact)).ToLowerInvariant()
        if ($artifact.Length -ne $record.artifact.bytes -or $hash -cne $record.artifact.sha256) { throw 'Artifact bytes differ from retained manifest.' }
        $validation=Assert-PiSharpDistribution -Path $archive -Repo $Repo -Kind standalone -Version $Version -SourceCommit $SourceCommit -Rid $Rid -EnablePromptTemplateYaml $record.enablePromptTemplateYaml
        if ($record.sdk -cne $validation.sdk -or $record.baselineTag -cne $validation.baselineTag -or $record.baselineCommit -cne $validation.baselineCommit) { throw 'Release provenance differs from inspected artifact.' }
        $actual=Get-PiSharpArchiveInventory -Path $archive
        if ($record.files.Count -ne $actual.Count) { throw 'Release file inventory count differs.' }
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($file in $record.files) {
            if (-not $seen.Add($file.path) -or -not $actual.ContainsKey($file.path) -or
                $file.bytes -ne $actual[$file.path].bytes -or $file.sha256 -cne $actual[$file.path].sha256 -or
                $file.unixMode -ne $actual[$file.path].unixMode) { throw 'Release payload inventory differs.' }
        }
        if ($CurrentVersion -ceq $Version -and $CurrentArchiveSha256 -cne $hash) { throw 'Existing release version cannot identify different artifact bytes.' }
        return [pscustomobject]@{ version=$Version; sourceCommit=$SourceCommit; rid=$Rid; archive=$archive; sha256=$hash
            action=$(if ($CurrentVersion -ceq $Version) { 'AlreadyCurrent' } else { 'SideBySideCandidate' })
            payloadVerified=$true; platformQualified=$false; installed=$false; userDataTouched=$false }
    } finally { if ($null -ne $artifact) { $artifact.Dispose() }; $manifest.Dispose() }
}
