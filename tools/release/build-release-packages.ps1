# Builds, packs and validates every PiSharp NuGet package for one release version.
# Used by .github/workflows/release.yml; also runnable locally with the pinned SDK.
# Restores from the given package source (the committed NuGet.Config clears all feeds).
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [string]$OutputDirectory = 'artifacts/packages',
    [string]$PackageSource = 'https://api.nuget.org/v3/index.json'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $repo 'tools/release/native-sdk-package.ps1')

$libraries = @(
    'PiSharp.Contracts', 'PiSharp.AI', 'PiSharp.Agent', 'PiSharp.Sessions', 'PiSharp.Tools', 'PiSharp.Tools.Skia', 'PiSharp.CodingAgent',
    'PiSharp.PromptTemplates.Yaml', 'PiSharp.Rpc', 'PiSharp.Tui', 'PiSharp.Extensions.Abstractions',
    'PiSharp.Extensions.Runtime', 'PiSharp.Extensions.Agent', 'PiSharp.ExtensionHost', 'PiSharp.Compatibility.Node'
)

if ($Version -cnotmatch '^([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)(?:-([0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*))?$') {
    throw "Invalid release version '$Version'."
}
$versionPrefix = $Matches[1]
$versionSuffix = if ($Matches.ContainsKey(2)) { $Matches[2] } else { '' }

# PiSharp's version is the Pi version it matches; a fourth segment is a C#-only patch.
$baseline = Get-Content -LiteralPath (Join-Path $repo 'compatibility/target.lock.json') -Raw | ConvertFrom-Json
$piVersion = ($versionPrefix.Split('.')[0..2]) -join '.'
if ('v' + $piVersion -cne $baseline.source.tag) {
    throw "Version $Version does not match the pinned Pi baseline $($baseline.source.tag)."
}
$sdk = (Get-Content -LiteralPath (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version

$out = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
$inputs = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/release-inputs'))
$reports = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/validation'))
foreach ($dir in @($out, $inputs, $reports)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$sdkProvenance = Join-Path $inputs 'native-sdk-provenance.json'
[ordered]@{
    schemaVersion = 1; kind = 'native-sdk'; sourceCommit = $SourceCommit; version = $Version
    baselineTag = $baseline.source.tag; baselineCommit = $baseline.source.commit; sdk = $sdk
} | ConvertTo-Json | Set-Content -LiteralPath $sdkProvenance -Encoding utf8NoBOM

$toolProvenance = Join-Path $inputs 'tool-provenance.json'
[ordered]@{
    schemaVersion = 1; sourceCommit = $SourceCommit; version = $Version
    baselineTag = $baseline.source.tag; baselineCommit = $baseline.source.commit; sdk = $sdk
    kind = 'tool'; enablePromptTemplateYaml = $true; rid = $null
} | ConvertTo-Json | Set-Content -LiteralPath $toolProvenance -Encoding utf8NoBOM

# The committed lock files predate the 0.99.1 version change, so restore is not locked here.
$common = @(
    '-c', 'Release', '-o', $out, '--nologo',
    "-p:VersionPrefix=$versionPrefix", "-p:VersionSuffix=$versionSuffix",
    "-p:PiSharpReleaseVersion=$Version", "-p:PiSharpSourceCommit=$SourceCommit",
    "-p:RestoreSources=$PackageSource", '-p:RestoreLockedMode=false', '-p:ContinuousIntegrationBuild=true'
)

foreach ($library in $libraries) {
    Write-Host "::group::Pack $library"
    dotnet pack (Join-Path $repo "src/$library/$library.csproj") @common `
        "-p:CustomAfterMicrosoftCommonTargets=$(Join-Path $repo 'tools/release/PiSharp.NativeSdk.targets')" `
        "-p:PiSharpProvenanceFile=$sdkProvenance"
    $package = Join-Path $out "$library.$Version.nupkg"
    try {
        $report = Assert-PiSharpNativeSdkPackage -Package $package -PackageId $library -Version $Version -SourceCommit $SourceCommit `
            -Repo $repo -BuildAssembly (Join-Path $repo "src/$library/bin/Release/net10.0/$library.dll")
    } catch {
        $zip = [IO.Compression.ZipFile]::OpenRead($package)
        try { Write-Host "Package members of $library`:"; $zip.Entries | ForEach-Object { Write-Host "  $($_.FullName)" } } finally { $zip.Dispose() }
        throw
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $reports "$library.json") -Encoding utf8NoBOM
    Write-Host "Validated $library $Version"
    Write-Host '::endgroup::'
}

Write-Host '::group::Pack PiSharp.Cli (dotnet tool)'
dotnet pack (Join-Path $repo 'src/PiSharp.Cli/PiSharp.Cli.csproj') @common `
    "-p:CustomAfterMicrosoftCommonTargets=$(Join-Path $repo 'tools/packaging/PiSharp.Distribution.targets')" `
    "-p:PiSharpProvenanceFile=$toolProvenance"
$tool = Join-Path $out "PiSharp.Cli.$Version.nupkg"
$toolHash = (Get-FileHash -LiteralPath $tool -Algorithm SHA256).Hash.ToLowerInvariant()
& (Join-Path $repo 'tools/packaging/validate-distribution.ps1') -Artifact $tool -ExpectedSha256 $toolHash -Repo $repo `
    -Kind tool -Version $Version -SourceCommit $SourceCommit |
    Set-Content -LiteralPath (Join-Path $reports 'PiSharp.Cli.json') -Encoding utf8NoBOM
Write-Host "Validated PiSharp.Cli $Version"
Write-Host '::endgroup::'

$packages = @(Get-ChildItem -LiteralPath $out -Filter '*.nupkg')
if ($packages.Count -ne $libraries.Count + 1) {
    throw "Expected $($libraries.Count + 1) packages, found $($packages.Count)."
}
Write-Host "Built and validated $($packages.Count) packages for $Version in $out"
