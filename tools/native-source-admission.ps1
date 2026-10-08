function Resolve-NativeSourcePath {
    param([string]$Repo, [string]$Relative)
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative.Contains(':') -or $Relative.Contains('\') -or
        @($Relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count) {
        throw "NATIVE-SOURCE-MANIFEST: Invalid canonical relative path: $Relative"
    }
    $rootPath = [IO.Path]::GetFullPath($Repo).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $filePath = [IO.Path]::GetFullPath((Join-Path $rootPath $Relative))
    if (-not $filePath.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "NATIVE-SOURCE-MANIFEST: Path escapes checkout: $Relative"
    }
    return $filePath
}

function Assert-NativeSourceClosure {
    param([string]$Repo, [object]$Source, [switch]$RequireFreshGeneratedRoots)
    # Compare actual membership, including hidden/ignored files, with the pinned
    # source list. Git status and hashing only declared paths cannot prove this.
    $root = Get-Item -LiteralPath $Repo -Force -ErrorAction Stop
    if (-not $root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'NATIVE-SOURCE-REPARSE: Checkout root must be an ordinary directory.'
    }
    $rootPath = $root.FullName.TrimEnd([IO.Path]::DirectorySeparatorChar)
    $pins = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $projectDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $artifactSourceRoots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($pin in $Source.files) {
        $relative = [string]$pin.relative
        [void](Resolve-NativeSourcePath -Repo $rootPath -Relative $relative)
        if ($pin.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $null -eq $pin.bytes -or $pin.bytes -lt 0 -or
            -not $pins.TryAdd($relative, $pin) -or $relative -eq '.git' -or $relative.StartsWith('.git/', [StringComparison]::OrdinalIgnoreCase)) {
            throw "NATIVE-SOURCE-MANIFEST: Invalid or duplicate source pin: $relative"
        }
        if ($relative.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase)) {
            $separator = $relative.LastIndexOf('/')
            if ($separator -lt 0) { throw 'NATIVE-SOURCE-MANIFEST: Root projects require a separately reviewed output policy.' }
            [void]$projectDirectories.Add($relative.Substring(0, $separator))
        }
        $parts = $relative.Split('/')
        if ($parts.Count -ge 3 -and $parts[0] -ieq 'artifacts') { [void]$artifactSourceRoots.Add('artifacts/' + $parts[1]) }
    }
    if (-not $pins.Count -or -not $projectDirectories.Count) { throw 'NATIVE-SOURCE-MANIFEST: Complete source/project list required.' }
    $generatedRoots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($directory in $projectDirectories) {
        [void]$generatedRoots.Add($directory + '/bin')
        [void]$generatedRoots.Add($directory + '/obj')
    }
    foreach ($relative in $pins.Keys) {
        if (@($generatedRoots | Where-Object { $relative -eq $_ -or $relative.StartsWith($_ + '/', [StringComparison]::OrdinalIgnoreCase) }).Count) {
            throw "NATIVE-SOURCE-MANIFEST: Source pin overlaps an output/evidence root: $relative"
        }
    }
    if ($RequireFreshGeneratedRoots) {
        foreach ($relative in $generatedRoots) {
            if (Test-Path -LiteralPath (Resolve-NativeSourcePath -Repo $rootPath -Relative $relative)) {
                throw "NATIVE-SOURCE-STALE-GENERATED: Preparation requires a fresh materialized checkout: $relative"
            }
        }
        $artifactsPath = Join-Path $rootPath 'artifacts'
        if (Test-Path -LiteralPath $artifactsPath) {
            $artifactDirectory = Get-Item -LiteralPath $artifactsPath -Force -ErrorAction Stop
            if (-not $artifactDirectory.PSIsContainer -or ($artifactDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'NATIVE-SOURCE-REPARSE: Artifacts root must be an ordinary directory.'
            }
            foreach ($entry in @(Get-ChildItem -LiteralPath $artifactsPath -Force -ErrorAction Stop)) {
                $relative = 'artifacts/' + $entry.Name
                if (-not $artifactSourceRoots.Contains($relative) -and -not $pins.ContainsKey($relative)) {
                    throw "NATIVE-SOURCE-STALE-GENERATED: Fresh preparation contains an unowned artifacts entry: $relative"
                }
            }
        }
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $generated = [Collections.Generic.List[object]]::new()
    $pending = [Collections.Generic.Stack[object]]::new()
    $pending.Push(@{ path = $rootPath; relative = ''; kind = 'source' })
    while ($pending.Count) {
        $directory = $pending.Pop()
        foreach ($entry in @(Get-ChildItem -LiteralPath $directory.path -Force -ErrorAction Stop)) {
            $relative = if ($directory.relative) { $directory.relative + '/' + $entry.Name } else { $entry.Name }
            # Check links before any exclusion. Traversal never follows a link.
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "NATIVE-SOURCE-REPARSE: Linked checkout entry rejected: $relative"
            }
            if ($relative -eq '.git') { continue }
            $kind = $directory.kind
            # Pinned release acquisition branches under artifacts are source.
            # Only other immediate child directories are runtime evidence.
            if ($directory.relative -ieq 'artifacts' -and $entry.PSIsContainer -and -not $artifactSourceRoots.Contains($relative)) { $kind = 'evidence' }
            elseif ($generatedRoots.Contains($relative)) { $kind = 'generated' }
            if ($entry.PSIsContainer) {
                $pending.Push(@{ path = $entry.FullName; relative = $relative; kind = $kind })
                continue
            }
            if ($kind -eq 'evidence') { continue }
            $sha256 = (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
            if ($kind -eq 'generated') {
                $generated.Add([pscustomobject]@{ relative = $relative; bytes = $entry.Length; sha256 = $sha256 })
                continue
            }
            if (-not $pins.ContainsKey($relative)) { throw "NATIVE-SOURCE-UNLISTED: Actual source/build input is absent from manifest: $relative" }
            $expected = $pins[$relative]
            if ($relative -cne $expected.relative -or $entry.Length -ne $expected.bytes -or $sha256 -cne $expected.sha256) {
                throw "NATIVE-SOURCE-PIN: Actual source/build input differs from manifest: $relative"
            }
            [void]$seen.Add($relative)
        }
    }
    foreach ($relative in $pins.Keys) {
        if (-not $seen.Contains($relative)) { throw "NATIVE-SOURCE-MISSING: Declared source/build input is absent: $relative" }
    }
    return [pscustomobject]@{
        schemaVersion = 1; policy = 'exact-checkout-source-v1'; sourceFilesVerified = $seen.Count;
        projectDirectories = @($projectDirectories | Sort-Object); generatedRoots = @($generatedRoots | Sort-Object);
        generatedFiles = @($generated | Sort-Object relative); evidenceRoot = 'artifacts';
        protectedArtifactSourceRoots = @($artifactSourceRoots | Sort-Object);
        freshGeneratedRootsRequired = [bool]$RequireFreshGeneratedRoots; unexpectedSourceFiles = 0;
        generatedFilesAreSourcePins = $false; linksAdmitted = $false
    }
}

function Assert-NativeGeneratedInputPins {
    param([object]$Closure, [object[]]$Expected)
    # Retain generated compiler/restore input identity after preparation. A later
    # obj injection must not be hidden by the source-tree output exclusions.
    $pins = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($pin in $Expected) {
        if ([string]::IsNullOrWhiteSpace($pin.relative) -or $pin.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
            $null -eq $pin.bytes -or $pin.bytes -lt 0 -or -not $pins.TryAdd($pin.relative, $pin)) {
            throw 'NATIVE-GENERATED-MANIFEST: Invalid or duplicate prepared generated-input pin.'
        }
    }
    foreach ($actual in $Closure.generatedFiles) {
        if (-not $pins.ContainsKey($actual.relative) -or $actual.relative -cne $pins[$actual.relative].relative -or
            $actual.bytes -ne $pins[$actual.relative].bytes -or $actual.sha256 -cne $pins[$actual.relative].sha256) {
            throw "NATIVE-GENERATED-PIN: Generated input is new or changed since preparation: $($actual.relative)"
        }
    }
    if ($Closure.generatedFiles.Count -ne $pins.Count) { throw 'NATIVE-GENERATED-MISSING: Prepared generated input disappeared.' }
}
