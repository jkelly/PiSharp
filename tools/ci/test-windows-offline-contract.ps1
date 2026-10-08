param([Parameter(Mandatory)][string]$Report)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows PowerShell 7 required.' }
. (Join-Path $PSScriptRoot 'windows-offline-contract.ps1')
$reportPath = [IO.Path]::GetFullPath($Report)
if (Test-Path -LiteralPath $reportPath) { throw 'Fresh report required.' }
$rows = [Collections.Generic.List[object]]::new()
function Check([string]$Id, [string]$Prefix, [scriptblock]$Action) {
    $passed = $false; $errorText = $null
    try { & $Action | Out-Null; $passed = -not $Prefix }
    catch { $errorText = $_.Exception.Message; $passed = $Prefix -and $errorText.StartsWith($Prefix, [StringComparison]::Ordinal) }
    $rows.Add(@{ id = $Id; passed = [bool]$passed; error = $errorText; nativeExecutions = 0; nativeAcceptance = $false })
    if (-not $passed) { throw "Contract control failed: $Id; later controls stopped." }
}
$source = @{ schemaVersion = 1; candidate = 'a' * 40; tree = 'b' * 40;
    repository = 'C:\ci checkout\PiSharp'; files = @(@{ relative = 'global.json' }) }
$allocation = @{ schemaVersion = 1; executionPermitted = $true; allowedOperation = 'locked-offline-build-publish';
    runtimeOwner = 'control-only'; candidate = $source.candidate; tree = $source.tree;
    sourceManifestSha256 = 'c' * 64; reviewedRepositoryRoot = $source.repository; expiresUtc = '2030-01-02T00:00:00Z';
    dotnet = @{ path = 'C:\sdk\dotnet.exe'; bytes = 1; sha256 = 'd' * 64 } }
$now = [DateTimeOffset]'2030-01-01T00:00:00Z'
function Bind($s = $source, $a = $allocation, $root = $source.repository, $sha = ('c' * 64)) {
    Assert-WindowsOfflineCiBinding -Repo $root -Source $s -Allocation $a -SourceManifestSha256 $sha -Now $now
}
function Copy-Control($value) { $value | ConvertTo-Json -Depth 12 | ConvertFrom-Json }
try {
    # These synthetic objects test only bindings. They have no real manifest,
    # host file, source closure, product or preparation proof and cannot admit CI.
    Check '01-exact-path-with-spaces' '' { Bind }
    Check '02-windows-path-case-and-separator-equivalence' '' { Bind -root 'c:/CI CHECKOUT/PiSharp/' }
    Check '03-copied-source-review-rejected' 'WINDOWS-CI-RELOCATION:' { Bind -root 'D:\runner\PiSharp' }
    Check '04-copied-allocation-rejected' 'WINDOWS-CI-RELOCATION:' {
        $a = Copy-Control $allocation; $a.reviewedRepositoryRoot = 'D:\runner\PiSharp'; Bind -a $a
    }
    foreach ($case in @(
        @{ id = '05-candidate-mismatch'; change = { param($a) $a.candidate = 'e' * 40 } },
        @{ id = '06-tree-mismatch'; change = { param($a) $a.tree = 'e' * 40 } },
        @{ id = '07-manifest-mismatch'; change = { param($a) $a.sourceManifestSha256 = 'e' * 64 } },
        @{ id = '08-permission-false'; change = { param($a) $a.executionPermitted = $false } },
        @{ id = '09-permission-string'; change = { param($a) $a.executionPermitted = 'true' } },
        @{ id = '10-wrong-operation'; change = { param($a) $a.allowedOperation = 'build' } },
        @{ id = '11-missing-owner'; change = { param($a) $a.runtimeOwner = '' } },
        @{ id = '12-expired'; change = { param($a) $a.expiresUtc = '2029-12-31T23:59:59Z' } },
        @{ id = '13-expiry-at-boundary'; change = { param($a) $a.expiresUtc = '2030-01-01T00:00:00Z' } },
        @{ id = '14-invalid-expiry'; change = { param($a) $a.expiresUtc = 'invalid' } }
    )) {
        Check $case.id 'WINDOWS-CI-ALLOCATION:' { $a = Copy-Control $allocation; & $case.change $a; Bind -a $a }
    }
    Check '15-host-not-pinned' 'WINDOWS-CI-HOST:' { $a = Copy-Control $allocation; $a.dotnet = $null; Bind -a $a }
    Check '16-host-relative-path' 'WINDOWS-CI-HOST:' { $a = Copy-Control $allocation; $a.dotnet.path = 'dotnet.exe'; Bind -a $a }
    Check '17-invalid-source-identity' 'WINDOWS-CI-SOURCE:' { $s = Copy-Control $source; $s.tree = ''; Bind -s $s }
    Check '18-invalid-manifest-hash' 'WINDOWS-CI-SOURCE:' { Bind -sha 'not-a-pin' }
    Check '19-relative-checkout' 'WINDOWS-CI-ROOT:' { Bind -root 'checkout' }
    Check '20-network-checkout' 'WINDOWS-CI-ROOT:' { Bind -root '\\server\checkout' }
    $sdk = @{ sdk = @{ version = '10.0.401'; rollForward = 'disable' } }
    [xml]$nuget = '<configuration><packageSources><clear /></packageSources></configuration>'
    [xml]$props = '<Project><PropertyGroup><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup></Project>'
    Check '21-exact-sdk-and-offline-lock-policy' '' { Assert-WindowsOfflineCiPolicy $sdk $nuget $props }
    Check '22-sdk-patch-roll-forward-rejected' 'WINDOWS-CI-SDK:' {
        $s = Copy-Control $sdk; $s.sdk.rollForward = 'latestPatch'; Assert-WindowsOfflineCiPolicy $s $nuget $props
    }
    Check '23-other-sdk-rejected' 'WINDOWS-CI-SDK:' {
        $s = Copy-Control $sdk; $s.sdk.version = '10.0.402'; Assert-WindowsOfflineCiPolicy $s $nuget $props
    }
    Check '24-package-feed-rejected' 'WINDOWS-CI-NUGET:' {
        Assert-WindowsOfflineCiPolicy $sdk ([xml]'<configuration><packageSources><clear /><add key="feed" value="https://example.invalid" /></packageSources></configuration>') $props
    }
    Check '25-package-fallback-rejected' 'WINDOWS-CI-NUGET:' {
        Assert-WindowsOfflineCiPolicy $sdk ([xml]'<configuration><packageSources><clear /></packageSources><fallbackPackageFolders><add key="fallback" value="C:\cache" /></fallbackPackageFolders></configuration>') $props
    }
    Check '26-lock-policy-disabled' 'WINDOWS-CI-LOCKS:' {
        Assert-WindowsOfflineCiPolicy $sdk $nuget ([xml]'<Project><PropertyGroup><RestorePackagesWithLockFile>false</RestorePackagesWithLockFile></PropertyGroup></Project>')
    }
    $lock = '{"version":1,"dependencies":{"net10.0":{"pisharp.contracts":{"type":"Project"}}}}' | ConvertFrom-Json
    Check '27-framework-project-only-graph' '' { Assert-WindowsOfflineCiProject ([xml]'<Project />') $lock 'tests/Consumer/Consumer.csproj' }
    Check '28-package-reference-rejected' 'WINDOWS-CI-DEPENDENCIES:' {
        Assert-WindowsOfflineCiProject ([xml]'<Project><ItemGroup><PackageReference Include="External" Version="1.0.0" /></ItemGroup></Project>') $lock 'tests/Consumer/Consumer.csproj'
    }
    Check '29-external-lock-entry-rejected' 'WINDOWS-CI-DEPENDENCIES:' {
        $l = '{"version":1,"dependencies":{"net10.0":{"external":{"type":"Direct"}}}}' | ConvertFrom-Json
        Assert-WindowsOfflineCiProject ([xml]'<Project />') $l 'tests/Consumer/Consumer.csproj'
    }
    Check '30-other-framework-rejected' 'WINDOWS-CI-LOCKS:' {
        $l = '{"version":1,"dependencies":{"net9.0":{}}}' | ConvertFrom-Json
        Assert-WindowsOfflineCiProject ([xml]'<Project />') $l 'tests/Consumer/Consumer.csproj'
    }
} finally {
    $reportObject = @{ schemaVersion = 1; controls = $rows; passed = @($rows | Where-Object passed).Count;
        nativeExecutions = 0; nativeAcceptance = $false; admissionControlOnly = $true }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($reportObject | ConvertTo-Json -Depth 12) + "`n")
    $stream = [IO.File]::Open($reportPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}
