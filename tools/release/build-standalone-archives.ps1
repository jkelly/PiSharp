#!/usr/bin/env pwsh
# Publishes and validates self-contained standalone PiSharp.Cli archives for desktop/server RIDs.
# Does not push or publish externally; writes verified archives, manifests and checksums.sha256.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')][string[]]$Rids = @('win-x64', 'linux-x64', 'osx-arm64'),
    [string]$OutputDirectory = 'artifacts/standalone',
    [string]$PackageSource = 'https://api.nuget.org/v3/index.json'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $repo 'tools/release/standalone-release.ps1')

if ($Version -cnotmatch '^([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)(?:-([0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*))?$') {
    throw "Invalid release version '$Version'."
}
$versionPrefix = $Matches[1]
$versionSuffix = if ($Matches.ContainsKey(2)) { $Matches[2] } else { '' }

$baseline = Get-Content -LiteralPath (Join-Path $repo 'compatibility/target.lock.json') -Raw | ConvertFrom-Json
$sdk = (Get-Content -LiteralPath (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version

$out = [IO.Path]::GetFullPath([IO.Path]::Combine($repo, $OutputDirectory))
$staging = Join-Path $out 'staging'
$inputs = Join-Path $out 'inputs'
New-Item -ItemType Directory -Force -Path $out, $staging, $inputs | Out-Null

$checksumLines = [Collections.Generic.List[string]]::new()
$summary = [Collections.Generic.List[object]]::new()

foreach ($rid in $Rids) {
    Write-Host "::group::Publish standalone $rid"
    $publishDir = Join-Path $staging $rid
    if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    $provenancePath = Join-Path $inputs "standalone-$rid-provenance.json"
    [ordered]@{
        schemaVersion = 1
        sourceCommit = $SourceCommit
        version = $Version
        baselineTag = $baseline.source.tag
        baselineCommit = $baseline.source.commit
        sdk = $sdk
        kind = 'standalone'
        enablePromptTemplateYaml = $true
        rid = $rid
    } | ConvertTo-Json | Set-Content -LiteralPath $provenancePath -Encoding utf8NoBOM

    dotnet publish (Join-Path $repo 'src/PiSharp.Cli/PiSharp.Cli.csproj') `
        -c Release -r $rid --self-contained true -o $publishDir --nologo `
        "-p:PackAsTool=false" `
        "-p:VersionPrefix=$versionPrefix" "-p:VersionSuffix=$versionSuffix" `
        "-p:PiSharpReleaseVersion=$Version" "-p:PiSharpSourceCommit=$SourceCommit" `
        "-p:CustomAfterMicrosoftCommonTargets=$(Join-Path $repo 'tools/packaging/PiSharp.Distribution.targets')" `
        "-p:PiSharpProvenanceFile=$provenancePath" `
        "-p:RestoreSources=$PackageSource" '-p:RestoreLockedMode=false' '-p:ContinuousIntegrationBuild=true'

    # Strip any standalone managed/native .pdb files so archives stay compact and reproducible.
    Get-ChildItem -LiteralPath $publishDir -Recurse -Filter '*.pdb' -File | Remove-Item -Force

    $archiveName = "pisharp-$Version-$rid.zip"
    $archivePath = Join-Path $out $archiveName
    $manifestPath = Join-Path $out "pisharp-$Version-$rid.manifest.json"
    foreach ($p in @($archivePath, $manifestPath)) {
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
    }

    $release = New-PiSharpStandaloneRelease `
        -PayloadDirectory $publishDir `
        -ArchivePath $archivePath `
        -ManifestPath $manifestPath `
        -Repo $repo `
        -Version $Version `
        -SourceCommit $SourceCommit `
        -Rid $rid

    $checksumLines.Add("$($release.artifact.sha256)  $archiveName")
    $summary.Add([ordered]@{
        rid = $rid
        fileName = $archiveName
        bytes = $release.artifact.bytes
        sha256 = $release.artifact.sha256
        manifest = [IO.Path]::GetFileName($manifestPath)
    })
    Write-Host "Validated $archiveName ($($release.artifact.bytes) bytes, sha256=$($release.artifact.sha256))"
    Write-Host '::endgroup::'
}

[IO.File]::WriteAllText((Join-Path $out 'checksums.sha256'), ($checksumLines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $out 'standalone-summary.json'), ($summary | ConvertTo-Json -Depth 6) + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Built and validated $($summary.Count) standalone archives under $out"
