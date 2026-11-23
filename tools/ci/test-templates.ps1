#!/usr/bin/env pwsh
# Smoke-tests PiSharp.Templates in an isolated dotnet new custom hive:
# 1. Uses pre-packed packages in -PackageDirectory, or packs the required subset locally.
# 2. Installs PiSharp.Templates into a disposable --debug:custom-hive folder.
# 3. Instantiates pisharp-agent, pisharp-extension, and pisharp-workflow with --PiSharpVersion.
# 4. Restores and builds all 3 generated projects against the local feed + nuget.org.
# 5. Runs pisharp-agent and pisharp-workflow offline and verifies expected output + exit code 0,
#    and verifies pisharp-extension emits its assembly, deps.json, and substituted manifest.
[CmdletBinding()]
param(
    [string]$PackageDirectory = '',
    [string]$Version = '',
    [string]$PackageSource = 'https://api.nuget.org/v3/index.json',
    [string]$Dotnet = 'dotnet'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$dotnetCmd = (Get-Command $Dotnet -CommandType Application -TotalCount 1 -ErrorAction Stop).Source

function Invoke-Checked {
    param([string[]]$Arguments, [string]$WorkingDirectory = $repo)
    Push-Location $WorkingDirectory
    try {
        & $dotnetCmd @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed ($LASTEXITCODE): $dotnetCmd $($Arguments -join ' ')"
        }
    } finally {
        Pop-Location
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
    $Version = ($props.SelectSingleNode('//VersionPrefix')).InnerText.Trim()
}

$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('pisharp-templates-test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$hiveDir = Join-Path $workRoot 'hive'
$projectsDir = Join-Path $workRoot 'projects'
$nugetCache = Join-Path $workRoot 'nuget-cache'
New-Item -ItemType Directory -Force -Path $hiveDir, $projectsDir, $nugetCache | Out-Null

try {
    $pkgDir = $PackageDirectory
    if ([string]::IsNullOrWhiteSpace($pkgDir)) {
        $pkgDir = Join-Path $workRoot 'packages'
        New-Item -ItemType Directory -Force -Path $pkgDir | Out-Null

        $commit = (& git -C $repo rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
            $commit = '0' * 40
        }
        $baseline = Get-Content -LiteralPath (Join-Path $repo 'compatibility/target.lock.json') -Raw | ConvertFrom-Json
        $sdk = (Get-Content -LiteralPath (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version

        $sdkProv = Join-Path $workRoot 'sdk-provenance.json'
        $tplProv = Join-Path $workRoot 'tpl-provenance.json'
        $utf8NoBom = [Text.UTF8Encoding]::new($false)
        [IO.File]::WriteAllText($sdkProv, ([ordered]@{
            schemaVersion = 1; kind = 'native-sdk'; sourceCommit = $commit; version = $Version
            baselineTag = $baseline.source.tag; baselineCommit = $baseline.source.commit; sdk = $sdk
        } | ConvertTo-Json), $utf8NoBom)
        [IO.File]::WriteAllText($tplProv, ([ordered]@{
            schemaVersion = 1; kind = 'templates'; sourceCommit = $commit; version = $Version
            baselineTag = $baseline.source.tag; baselineCommit = $baseline.source.commit; sdk = $sdk
        } | ConvertTo-Json), $utf8NoBom)

        $versionPrefix = ($Version -split '-', 2)[0]
        $versionSuffix = if ($Version.Contains('-')) { ($Version -split '-', 2)[1] } else { '' }
        $common = @(
            '-c', 'Release', '-o', $pkgDir, '--nologo',
            "-p:VersionPrefix=$versionPrefix", "-p:VersionSuffix=$versionSuffix",
            "-p:PiSharpReleaseVersion=$Version", "-p:PiSharpSourceCommit=$commit",
            "-p:RestoreSources=$PackageSource", '-p:RestoreLockedMode=false'
        )

        # Pack the exact dependency closure required by all 3 templates.
        $requiredLibs = @(
            'PiSharp.Contracts', 'PiSharp.Telemetry', 'PiSharp.AI', 'PiSharp.Agent',
            'PiSharp.Extensions.Abstractions', 'PiSharp.Durable', 'PiSharp.Durable.Sqlite', 'PiSharp.CustomAgents'
        )
        foreach ($lib in $requiredLibs) {
            Write-Host "Packing $lib $Version..."
            Invoke-Checked (@('pack', (Join-Path $repo "src/$lib/$lib.csproj")) + $common + @(
                "-p:CustomAfterMicrosoftCommonTargets=$(Join-Path $repo 'tools/release/PiSharp.NativeSdk.targets')",
                "-p:PiSharpProvenanceFile=$sdkProv"
            ))
        }

        Write-Host "Packing PiSharp.Templates $Version..."
        Invoke-Checked (@('pack', (Join-Path $repo 'templates/PiSharp.Templates.csproj')) + $common + @(
            "-p:PiSharpProvenanceFile=$tplProv"
        ))
    } else {
        $pkgDir = (Resolve-Path -LiteralPath $pkgDir).Path
    }

    $templatesPkg = Join-Path $pkgDir "PiSharp.Templates.$Version.nupkg"
    if (-not (Test-Path -LiteralPath $templatesPkg)) {
        throw "Templates package not found: $templatesPkg"
    }

    # Write a standalone NuGet.Config in the temp workspace mapping PiSharp.* exclusively to local-pisharp
    # and all third-party dependencies to the upstream NuGet source.
    $escapedPkgDir = [Security.SecurityElement]::Escape($pkgDir)
    $escapedSource = [Security.SecurityElement]::Escape($PackageSource)
    $nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-pisharp" value="$escapedPkgDir" />
    <add key="nuget.org" value="$escapedSource" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-pisharp">
      <package pattern="PiSharp.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
    [IO.File]::WriteAllText((Join-Path $projectsDir 'NuGet.Config'), $nugetConfig + "`n", [Text.UTF8Encoding]::new($false))

    Write-Host "Installing $templatesPkg into isolated custom hive..."
    Invoke-Checked @('new', 'install', $templatesPkg, '--debug:custom-hive', $hiveDir) -WorkingDirectory $projectsDir

    $prevPackages = $env:NUGET_PACKAGES
    $env:NUGET_PACKAGES = $nugetCache
    try {
        # 1. pisharp-agent
        Write-Host '=== Testing template: pisharp-agent ==='
        Invoke-Checked @('new', 'pisharp-agent', '-n', 'SampleAgent', '--PiSharpVersion', $Version, '--debug:custom-hive', $hiveDir) -WorkingDirectory $projectsDir
        $agentDir = Join-Path $projectsDir 'SampleAgent'
        Invoke-Checked @('build', 'SampleAgent.csproj', '-c', 'Release', '--nologo') -WorkingDirectory $agentDir
        $agentOutput = (& $dotnetCmd run --project (Join-Path $agentDir 'SampleAgent.csproj') -c Release --no-build) | Out-String
        if ($LASTEXITCODE -ne 0) { throw "SampleAgent exited with code $LASTEXITCODE.`n$agentOutput" }
        Write-Host $agentOutput.Trim()
        if ($agentOutput -notmatch 'Agent finished in 2 turn\(s\):') {
            throw "Unexpected SampleAgent output:`n$agentOutput"
        }

        # 2. pisharp-extension
        Write-Host '=== Testing template: pisharp-extension ==='
        Invoke-Checked @('new', 'pisharp-extension', '-n', 'SampleExt', '--PiSharpVersion', $Version, '--debug:custom-hive', $hiveDir) -WorkingDirectory $projectsDir
        $extDir = Join-Path $projectsDir 'SampleExt'
        Invoke-Checked @('build', 'SampleExt.csproj', '-c', 'Release', '--nologo') -WorkingDirectory $extDir
        $extOut = Join-Path $extDir 'bin/Release/net10.0'
        foreach ($expected in @('SampleExt.dll', 'SampleExt.deps.json', 'pisharp-extension.json')) {
            if (-not (Test-Path -LiteralPath (Join-Path $extOut $expected))) {
                throw "Missing expected extension output: $expected"
            }
        }
        $manifest = Get-Content -LiteralPath (Join-Path $extOut 'pisharp-extension.json') -Raw | ConvertFrom-Json
        if ($manifest.assembly -cne 'SampleExt.dll' -or $manifest.entryType -cne 'SampleExt.SampleExtension') {
            throw "Unexpected extension manifest substitution: $($manifest | ConvertTo-Json -Compress)"
        }
        Write-Host "Verified SampleExt manifest: assembly=$($manifest.assembly), entryType=$($manifest.entryType)"

        # 3. pisharp-workflow
        Write-Host '=== Testing template: pisharp-workflow ==='
        Invoke-Checked @('new', 'pisharp-workflow', '-n', 'SampleWorkflow', '--PiSharpVersion', $Version, '--debug:custom-hive', $hiveDir) -WorkingDirectory $projectsDir
        $wfDir = Join-Path $projectsDir 'SampleWorkflow'
        Invoke-Checked @('build', 'SampleWorkflow.csproj', '-c', 'Release', '--nologo') -WorkingDirectory $wfDir
        $wfOutput = (& $dotnetCmd run --project (Join-Path $wfDir 'SampleWorkflow.csproj') -c Release --no-build) | Out-String
        if ($LASTEXITCODE -ne 0) { throw "SampleWorkflow exited with code $LASTEXITCODE.`n$wfOutput" }
        Write-Host $wfOutput.Trim()
        if ($wfOutput -notmatch 'Workflow completed:' -or $wfOutput -notmatch 'approvedBy=ops-lead' -or $wfOutput -notmatch 'published=True') {
            throw "Unexpected SampleWorkflow output:`n$wfOutput"
        }

        Write-Host "ALL 3 TEMPLATES PASSED (pisharp-agent, pisharp-extension, pisharp-workflow) for PiSharp $Version."
    } finally {
        $env:NUGET_PACKAGES = $prevPackages
    }
} finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
