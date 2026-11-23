#!/usr/bin/env pwsh
# Post-publish verification for a tagged PiSharp release:
# 1. Polls api.nuget.org flat-container indexes until all 31 packages expose $Version.
# 2. Downloads GitHub release assets + official NuGet .nupkg files and compares every ZIP payload entry byte-for-byte
#    (excluding NuGet's .signature.p7s), verifying .signature.p7s presence and nuspec version/commit.
# 3. Restores and executes an isolated official-feed GapConsumer (un-admitted diagnostic rejection + admitted pass).
# 4. Installs PiSharp.Cli privately from api.nuget.org (--tool-path) and verifies --version and --help.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedCommit,
    [string]$ReleaseRunUrl = '',
    [string]$PublishedAt = '',
    [string]$Repo = 'jkelly/PiSharp',
    [string]$WorkDir = '',
    [string]$OutputJson = '',
    [int]$MaxWaitMinutes = 35,
    [int]$PollIntervalSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if ([string]::IsNullOrWhiteSpace($WorkDir)) {
    $WorkDir = Join-Path ([IO.Path]::GetTempPath()) ("pisharp-verify-$Version-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
if ([string]::IsNullOrWhiteSpace($OutputJson)) {
    $OutputJson = Join-Path $repoRoot "AI_DOCS/release-$Version-package-verification.json"
}

$packages = @(
    'PiSharp.Contracts', 'PiSharp.Telemetry', 'PiSharp.AI', 'PiSharp.Extensions.AI', 'PiSharp.Agent',
    'PiSharp.Sessions', 'PiSharp.Tools', 'PiSharp.Tools.Skia', 'PiSharp.CodingAgent',
    'PiSharp.PromptTemplates.Yaml', 'PiSharp.Rpc', 'PiSharp.Tui',
    'PiSharp.Extensions.Abstractions', 'PiSharp.Extensions.Runtime',
    'PiSharp.Extensions.Agent', 'PiSharp.ExtensionHost', 'PiSharp.Compatibility.Node',
    'PiSharp.Codemode', 'PiSharp.Durable', 'PiSharp.Durable.Tools', 'PiSharp.Durable.Sqlite',
    'PiSharp.Chord', 'PiSharp.Chord.Hosting', 'PiSharp.Durable.Store',
    'PiSharp.Env.Protocol', 'PiSharp.Env', 'PiSharp.CustomAgents',
    'PiSharp.CustomAgents.JavaScript', 'PiSharp.CustomAgents.Scripting', 'PiSharp.Cli', 'PiSharp.Templates'
)

$ghDir = Join-Path $WorkDir 'github'
$nugetDir = Join-Path $WorkDir 'nuget'
$consumerDir = Join-Path $WorkDir 'consumer'
$nugetCacheDir = Join-Path $WorkDir 'nuget-cache'
$cliDir = Join-Path $WorkDir 'cli-tool'
New-Item -ItemType Directory -Force -Path $ghDir, $nugetDir | Out-Null

# 1. Poll canonical NuGet version indexes until all packages expose $Version
$deadline = (Get-Date).AddMinutes($MaxWaitMinutes)
while ($true) {
    $missing = @()
    foreach ($pkg in $packages) {
        $id = $pkg.ToLowerInvariant()
        try {
            $res = Invoke-RestMethod -Uri "https://api.nuget.org/v3-flatcontainer/$id/index.json" -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec 20
            if ($res.versions -notcontains $Version) { $missing += $pkg }
        } catch {
            $missing += $pkg
        }
    }
    if ($missing.Count -eq 0) {
        Write-Host "INDEX_READY: $($packages.Count)/$($packages.Count) canonical NuGet indexes expose $Version"
        break
    }
    if ((Get-Date) -ge $deadline) {
        throw "Timed out waiting for NuGet indexing ($($packages.Count - $missing.Count)/$($packages.Count)). Still missing: $($missing -join ', ')"
    }
    Write-Host "Waiting for NuGet indexing: $($packages.Count - $missing.Count)/$($packages.Count) live (missing: $($missing -join ', '))..."
    Start-Sleep -Seconds $PollIntervalSeconds
}

# 2. Download GitHub release assets (always refresh so reused -WorkDir never compares stale payloads)
Write-Host "Downloading GitHub release assets for v$Version..."
gh release download "v$Version" --repo $Repo --dir $ghDir --pattern '*.nupkg' --clobber
if ($LASTEXITCODE -ne 0) { throw "gh release download failed with exit code $LASTEXITCODE" }

if ([string]::IsNullOrWhiteSpace($PublishedAt)) {
    $relInfo = gh release view "v$Version" --repo $Repo --json publishedAt | ConvertFrom-Json
    $PublishedAt = $relInfo.publishedAt
}

function Get-StreamSha256([System.IO.Stream]$stream) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-FileSha256Hex([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# 3. Download NuGet packages & compare payloads byte-for-byte
$pkgResults = @()
foreach ($pkg in $packages) {
    $id = $pkg.ToLowerInvariant()
    $ghFile = Join-Path $ghDir "$pkg.$Version.nupkg"
    if (-not (Test-Path -LiteralPath $ghFile)) { throw "Missing GitHub asset: $ghFile" }

    $nugetFile = Join-Path $nugetDir "$id.$Version.nupkg"
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$id/$Version/$id.$Version.nupkg" -OutFile $nugetFile -TimeoutSec 180

    $ghZip = [System.IO.Compression.ZipFile]::OpenRead($ghFile)
    $ngZip = [System.IO.Compression.ZipFile]::OpenRead($nugetFile)
    try {
        $ghEntries = [hashtable]::new([System.StringComparer]::Ordinal)
        foreach ($e in $ghZip.Entries) {
            if ($e.FullName -ceq '.signature.p7s') { continue }
            if ($ghEntries.ContainsKey($e.FullName)) { throw "${pkg}: duplicate GitHub archive entry '$($e.FullName)'" }
            $s = $e.Open(); try { $ghEntries[$e.FullName] = Get-StreamSha256 $s } finally { $s.Dispose() }
        }
        $ngEntries = [hashtable]::new([System.StringComparer]::Ordinal)
        $hasSig = $false; $nuspecVersion = $null; $nuspecCommit = $null
        foreach ($e in $ngZip.Entries) {
            if ($e.FullName -ceq '.signature.p7s') { $hasSig = $true; continue }
            if ($ngEntries.ContainsKey($e.FullName)) { throw "${pkg}: duplicate NuGet archive entry '$($e.FullName)'" }
            $s = $e.Open()
            try {
                if ($e.FullName.EndsWith('.nuspec', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $ms = [System.IO.MemoryStream]::new(); $s.CopyTo($ms); $ms.Position = 0
                    $ngEntries[$e.FullName] = Get-StreamSha256 $ms
                    $ms.Position = 0
                    $sr = [System.IO.StreamReader]::new($ms)
                    [xml]$xml = $sr.ReadToEnd()
                    $nsm = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
                    $nsm.AddNamespace('n', $xml.DocumentElement.NamespaceURI)
                    $nuspecVersion = $xml.SelectSingleNode('//n:metadata/n:version', $nsm).InnerText
                    $repoNode = $xml.SelectSingleNode('//n:metadata/n:repository', $nsm)
                    $nuspecCommit = if ($repoNode) { $repoNode.GetAttribute('commit') } else { '' }
                } else {
                    $ngEntries[$e.FullName] = Get-StreamSha256 $s
                }
            } finally { $s.Dispose() }
        }
        if ($ghEntries.Count -ne $ngEntries.Count) { throw "${pkg}: entry count mismatch (GH=$($ghEntries.Count), NuGet=$($ngEntries.Count))" }
        foreach ($k in $ghEntries.Keys) {
            if (-not $ngEntries.ContainsKey($k) -or $ghEntries[$k] -cne $ngEntries[$k]) { throw "${pkg}: payload mismatch on $k" }
        }
        if ($nuspecVersion -cne $Version) { throw "${pkg}: nuspec version '$nuspecVersion' != '$Version'" }
        if ($nuspecCommit -cne $ExpectedCommit) { throw "${pkg}: nuspec commit '$nuspecCommit' != '$ExpectedCommit'" }
        if (-not $hasSig) { throw "${pkg}: NuGet package missing .signature.p7s" }

        $pkgResults += [ordered]@{
            id = $pkg; version = $Version; payloadEntries = $ghEntries.Count
            repositoryCommit = $nuspecCommit; repositorySigned = $hasSig; status = 'verified'
            githubSha256 = Get-FileSha256Hex $ghFile; nugetSha256 = Get-FileSha256Hex $nugetFile
            githubBytes = (Get-Item -LiteralPath $ghFile).Length; nugetBytes = (Get-Item -LiteralPath $nugetFile).Length
        }
    } finally { $ghZip.Dispose(); $ngZip.Dispose() }
}
Write-Host "PACKAGES_VERIFIED: $($pkgResults.Count)/$($packages.Count) byte-for-byte payload matches"

# 4. Isolated external GapConsumer from official NuGet feed
if (Test-Path -LiteralPath $consumerDir) { Remove-Item -LiteralPath $consumerDir -Recurse -Force }
if (Test-Path -LiteralPath $nugetCacheDir) { Remove-Item -LiteralPath $nugetCacheDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $consumerDir, $nugetCacheDir | Out-Null

$nugetConfig = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
'@
[IO.File]::WriteAllText((Join-Path $consumerDir 'NuGet.Config'), $nugetConfig + "`n", [Text.UTF8Encoding]::new($false))
$csprojIn = Get-Content -LiteralPath (Join-Path $repoRoot 'tests/packaging/GapConsumer/GapConsumer.csproj.in') -Raw
[IO.File]::WriteAllText((Join-Path $consumerDir 'GapConsumer.csproj'), ($csprojIn -replace '@VERSION@', $Version), [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $repoRoot 'tests/packaging/GapConsumer/Program.cs') -Destination (Join-Path $consumerDir 'Program.cs')

$prevCache = $env:NUGET_PACKAGES
$env:NUGET_PACKAGES = $nugetCacheDir
try {
    Push-Location $consumerDir
    try {
        dotnet restore GapConsumer.csproj --configfile NuGet.Config --no-cache
        if ($LASTEXITCODE -ne 0) { throw 'Consumer restore failed' }

        $failOutput = dotnet build GapConsumer.csproj --no-restore -c Release 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0) { throw 'Consumer build without AdmitExperimental unexpectedly succeeded' }
        $diagMatches = @([regex]::Matches($failOutput, 'PISHARP_[A-Z0-9]+') | ForEach-Object { $_.Value } | Sort-Object -Unique)
        if ($diagMatches.Count -eq 0) { throw "Consumer build failed without expected PISHARP_* diagnostics:`n$failOutput" }

        $runOutput = dotnet run --project GapConsumer.csproj --no-restore -c Release -p:AdmitExperimental=true 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -or $runOutput -notmatch 'PASS package-only public consumer') { throw "Consumer execution failed:`n$runOutput" }

        $assets = Get-Content -LiteralPath (Join-Path $consumerDir 'obj/project.assets.json') -Raw | ConvertFrom-Json
        $resolvedPiSharp = @()
        foreach ($prop in $assets.libraries.PSObject.Properties) {
            if ($prop.Name -like 'PiSharp.*') {
                if ($prop.Value.type -ne 'package' -or -not $prop.Name.EndsWith("/$Version")) { throw "Unexpected PiSharp reference: $($prop.Name)" }
                $resolvedPiSharp += $prop.Name
            }
        }
        $resolvedPiSharp = @($resolvedPiSharp | Sort-Object)
    } finally { Pop-Location }

    # 5. Private PiSharp.Cli install from official NuGet feed
    if (Test-Path -LiteralPath $cliDir) { Remove-Item -LiteralPath $cliDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $cliDir | Out-Null
    Push-Location $cliDir
    try {
        [IO.File]::WriteAllText((Join-Path $cliDir 'NuGet.Config'), $nugetConfig + "`n", [Text.UTF8Encoding]::new($false))
        dotnet tool install PiSharp.Cli --version $Version --tool-path $cliDir --configfile (Join-Path $cliDir 'NuGet.Config') --no-cache
        if ($LASTEXITCODE -ne 0) { throw 'Private CLI install failed' }
        $cliExe = if ($IsWindows -or $env:OS -eq 'Windows_NT') { Join-Path $cliDir 'pisharp.exe' } else { Join-Path $cliDir 'pisharp' }
        $cliVer = (& $cliExe --version 2>&1 | Out-String).Trim()
        if ($cliVer -ne $Version) { throw "CLI version '$cliVer' != '$Version'" }
        $cliHelp = (& $cliExe --help 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0 -or $cliHelp -notmatch 'pisharp') { throw 'CLI --help smoke test failed' }
        Write-Host "PRIVATE_CLI_PASSED: version=$cliVer"
    } finally { Pop-Location }
} finally {
    if ($null -eq $prevCache) { Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue }
    else { $env:NUGET_PACKAGES = $prevCache }
}

$doc = [ordered]@{
    version = $Version
    sourceCommit = $ExpectedCommit
    packages = $pkgResults
    verified = $pkgResults.Count
    expected = $packages.Count
    consumer = [ordered]@{
        status = 'passed'
        version = $Version
        sourceCommit = $ExpectedCommit
        feed = 'https://api.nuget.org/v3/index.json'
        resolvedPiSharpPackages = $resolvedPiSharp
        experimentalDiagnostics = $diagMatches
        cliVersion = $cliVer
    }
    releaseRun = $ReleaseRunUrl
    release = "https://github.com/$Repo/releases/tag/v$Version"
    publishedAt = $PublishedAt
}

$outParent = Split-Path -Parent $OutputJson
if ($outParent) { New-Item -ItemType Directory -Force -Path $outParent | Out-Null }
$rawJson = $doc | ConvertTo-Json -Depth 10
# Normalize indentation via node when available for compact 2-space LF output
if (Get-Command node -ErrorAction SilentlyContinue) {
    $rawJson = $rawJson | node -e "const s=require('fs').readFileSync(0,'utf8'); process.stdout.write(JSON.stringify(JSON.parse(s), null, 2));"
}
[IO.File]::WriteAllText($OutputJson, $rawJson + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "WROTE_VERIFICATION_JSON: $OutputJson"
