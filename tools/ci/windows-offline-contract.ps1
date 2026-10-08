# Pure contract checks. This file never launches an executable, grants an
# allocation, rewrites a receipt, changes environment variables or installs SDKs.
function Get-WindowsOfflineCiRoot {
    param([string]$Path)
    if ($Path -cnotmatch '^[A-Za-z]:[\\/]' -or $Path.Contains('::')) {
        throw 'WINDOWS-CI-ROOT: A local absolute Windows checkout path is required.'
    }
    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
}

function Assert-WindowsOfflineCiBinding {
    param([string]$Repo, [object]$Source, [string]$SourceManifestSha256,
        [object]$Allocation, [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow)
    $root = Get-WindowsOfflineCiRoot $Repo
    if ($Source.schemaVersion -ne 1 -or $Source.candidate -cnotmatch '^[0-9a-f]{40}$' -or
        $Source.tree -cnotmatch '^[0-9a-f]{40}$' -or $SourceManifestSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        -not $Source.files.Count) { throw 'WINDOWS-CI-SOURCE: Exact immutable source identity required.' }
    if (-not (Get-WindowsOfflineCiRoot $Source.repository).Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'WINDOWS-CI-RELOCATION: Source review belongs to another checkout. Obtain a new path-bound review; do not rebase its JSON.'
    }
    if ($Allocation.schemaVersion -ne 1 -or $Allocation.executionPermitted -isnot [bool] -or
        -not $Allocation.executionPermitted -or $Allocation.allowedOperation -cne 'locked-offline-build-publish' -or
        [string]::IsNullOrWhiteSpace($Allocation.runtimeOwner) -or $Allocation.candidate -cne $Source.candidate -or
        $Allocation.tree -cne $Source.tree -or $Allocation.sourceManifestSha256 -cne $SourceManifestSha256) {
        throw 'WINDOWS-CI-ALLOCATION: A separately issued allocation for the exact existing preparation operation is required.'
    }
    if (-not (Get-WindowsOfflineCiRoot $Allocation.reviewedRepositoryRoot).Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'WINDOWS-CI-RELOCATION: Allocation belongs to another checkout; copied local approvals do not authorize a CI runner.'
    }
    $expiry = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse([string]$Allocation.expiresUtc, [ref]$expiry) -or $expiry -le $Now) {
        throw 'WINDOWS-CI-ALLOCATION: Allocation has no valid future expiry.'
    }
    $hostPin = $Allocation.dotnet
    if ($null -eq $hostPin -or $hostPin.path -cnotmatch '^[A-Za-z]:[\\/]' -or
        $hostPin.bytes -le 0 -or $hostPin.sha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'WINDOWS-CI-HOST: An independently pinned local host is required.'
    }
    return $root
}

function Assert-WindowsOfflineCiPolicy {
    param([object]$SdkPolicy, [xml]$NuGetPolicy, [xml]$BuildPolicy)
    if ($SdkPolicy.sdk.version -cne '10.0.401' -or $SdkPolicy.sdk.rollForward -cne 'disable' -or
        $null -ne $SdkPolicy.sdk.paths -or $SdkPolicy.sdk.allowPrerelease -eq $true) {
        throw 'WINDOWS-CI-SDK: The reviewed 10.0.401 SDK with roll-forward disabled is required.'
    }
    $children = @($NuGetPolicy.configuration.ChildNodes | Where-Object NodeType -EQ Element)
    $sources = @($NuGetPolicy.SelectNodes('/configuration/packageSources/*'))
    if ($children.Count -ne 1 -or $children[0].Name -cne 'packageSources' -or
        $sources.Count -ne 1 -or $sources[0].Name -cne 'clear') {
        throw 'WINDOWS-CI-NUGET: The existing cleared-source policy must remain exact; no feeds, credentials or fallback settings are admitted.'
    }
    $locks = @($BuildPolicy.SelectNodes('/Project/PropertyGroup/RestorePackagesWithLockFile'))
    if ($locks.Count -ne 1 -or $locks[0].InnerText -cne 'true') {
        throw 'WINDOWS-CI-LOCKS: The existing locked dependency policy is required.'
    }
}

function Assert-WindowsOfflineCiProject {
    param([xml]$Project, [object]$Lock, [string]$Relative)
    if ($Project.SelectNodes('//PackageReference | //PackageDownload | //RestoreSources | //RestoreAdditionalProjectSources | //RestoreFallbackFolders').Count) {
        throw "WINDOWS-CI-DEPENDENCIES: Framework/project-only admission required: $Relative"
    }
    if ($Lock.version -ne 1 -or $null -eq $Lock.dependencies -or
        @($Lock.dependencies.PSObject.Properties).Count -ne 1 -or
        @($Lock.dependencies.PSObject.Properties)[0].Name -cne 'net10.0') {
        throw "WINDOWS-CI-LOCKS: Exact framework lock required: $Relative"
    }
    foreach ($dependency in $Lock.dependencies.'net10.0'.PSObject.Properties) {
        if ($dependency.Value.type -cne 'Project') {
            throw "WINDOWS-CI-DEPENDENCIES: An external dependency is outside this offline CI slice: $Relative"
        }
    }
}
