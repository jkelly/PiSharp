# Local admission only: packs optionally, then restores/builds/runs from real packages outside the checkout.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:-[0-9A-Za-z.-]+)?$')][string]$Version,
    [string]$PackageDirectory = 'artifacts/packages',
    [string]$OutputDirectory = 'artifacts/package-consumer',
    [string]$DotnetPath = 'dotnet',
    [ValidateRange(1,1800)][int]$LockTimeoutSeconds = 300,
    [switch]$Pack
)
$ErrorActionPreference = 'Stop'
# Stage records exit codes itself; the negative compiler gate deliberately exits nonzero.
$PSNativeCommandUseErrorActionPreference = $false
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$feed = [IO.Path]::GetFullPath([IO.Path]::Combine($repo, $PackageDirectory))
$output = [IO.Path]::GetFullPath([IO.Path]::Combine($repo, $OutputDirectory))
if ((Test-Path -LiteralPath $output) -and (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)) { throw 'Use an empty evidence directory.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$consumer = Join-Path ([IO.Path]::GetTempPath()) ('pisharp-external-consumer-' + [Guid]::NewGuid().ToString('N'))
$consumer = [IO.Path]::GetFullPath($consumer)
if ($consumer.StartsWith($repo.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'The package consumer must be outside the checkout.' }
New-Item -ItemType Directory -Path $consumer | Out-Null
$ids = @('PiSharp.Chord','PiSharp.Chord.Hosting','PiSharp.Durable.Store','PiSharp.Env.Protocol','PiSharp.Env')
$stages = [Collections.Generic.List[object]]::new()
$packages = [Collections.Generic.List[object]]::new()
$receipt = [ordered]@{ schemaVersion=1; sourceCommit=$SourceCommit; version=$Version; result='failed'; packageConsumptionProven=$false; releaseAccepted=$false; consumerDirectory=$consumer; stages=@(); packages=@() }
function Stage([string]$Name, [string[]]$Arguments, [bool]$ExpectedFailure=$false) {
    $log = Join-Path $output ($Name + '.log')
    & $DotnetPath @Arguments *> $log
    $code = $LASTEXITCODE
    $stages.Add([ordered]@{ name=$Name; exitCode=$code; expectedFailure=$ExpectedFailure; log=[IO.Path]::GetFileName($log) })
    if (!$ExpectedFailure -and $code -ne 0) { throw "$Name failed; see $log" }
    if ($ExpectedFailure -and $code -eq 0) { throw "$Name unexpectedly succeeded without experimental opt-in." }
    return $log
}
$oldPackages = $env:NUGET_PACKAGES
$oldTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$lease = $null
. (Join-Path $repo 'tools/GapValidation/SourceFingerprint.ps1')
function SourceState {
    $state = [ordered]@{}
    foreach ($id in $ids) { $state[$id] = Get-GapSourceFingerprint $repo (Join-Path $repo "src/$id/$id.csproj") }
    $state.packaging = @(foreach ($path in @('tools/release/build-release-packages.ps1','tools/release/PiSharp.NativeSdk.targets','tools/release/native-sdk-package.ps1','tools/release/test-gap-package-consumer.ps1','docs/contracts/gap-package-admission.md','README.md','THIRD-PARTY-NOTICES.md','tests/packaging/GapConsumer/Program.cs','tests/packaging/GapConsumer/GapConsumer.csproj.in')) {
        [ordered]@{path=$path;sha256=(Get-FileHash -LiteralPath (Join-Path $repo $path) -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    return $state
}
function SourceDigest($State) {
    $hashes = @($ids | ForEach-Object {$State[$_].sha256}) + @($State.packaging | ForEach-Object {$_.sha256})
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($hashes -join "`n")))).ToLowerInvariant()
}
try {
    $lockDirectory = Join-Path $repo 'artifacts/gap-validation'
    New-Item -ItemType Directory -Path $lockDirectory -Force | Out-Null
    $lockDeadline = [DateTime]::UtcNow.AddSeconds($LockTimeoutSeconds)
    while ($null -eq $lease) {
        try { $lease = [IO.File]::Open((Join-Path $lockDirectory 'build.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
        catch [IO.IOException] { if ([DateTime]::UtcNow -ge $lockDeadline) { throw 'Package validation build lock timed out; no process launched.' }; Start-Sleep -Milliseconds 250 }
    }
    Push-Location $repo
    try {
        $head = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or $head -cne $SourceCommit) { throw 'Requested source commit does not match checkout.' }
        $receipt.trackedChanges = @(& git diff HEAD --name-only)
        $receipt.untrackedFiles = @(& git ls-files --others --exclude-standard)
        $receipt.sourceTree = (& git rev-parse 'HEAD^{tree}').Trim()
        $before = SourceState
        $receipt.sourceBeforeSha256 = SourceDigest $before
        $before | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $output 'source-before.json') -Encoding utf8NoBOM
        if ($Pack) {
            # Child script uses the selected SDK through PATH without global configuration changes.
            $dotnetExe = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop).Source
            $oldPath = $env:PATH
            try {
                $env:PATH = [IO.Path]::GetDirectoryName($dotnetExe) + [IO.Path]::PathSeparator + $oldPath
                & (Join-Path $PSScriptRoot 'build-release-packages.ps1') -Version $Version -SourceCommit $SourceCommit -OutputDirectory $PackageDirectory *> (Join-Path $output 'pack.log')
            } finally { $env:PATH = $oldPath }
        }
        . (Join-Path $PSScriptRoot 'native-sdk-package.ps1')
        foreach ($id in $ids) {
            $path = Join-Path $feed "$id.$Version.nupkg"
            $validation = Assert-PiSharpNativeSdkPackage -Package $path -PackageId $id -Version $Version -SourceCommit $SourceCommit -Repo $repo -BuildAssembly (Join-Path $repo "src/$id/bin/Release/net10.0/$id.dll")
            $validation | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output "$id.package.json") -Encoding utf8NoBOM
            $packages.Add([ordered]@{ id=$id; file=$path; sha256=$validation.archiveSha256 })
        }
    } finally { Pop-Location }
    $env:NUGET_PACKAGES = Join-Path $consumer 'cache'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    [IO.File]::WriteAllText((Join-Path $consumer 'Directory.Build.props'), '<Project />')
    [IO.File]::WriteAllText((Join-Path $consumer 'Directory.Build.targets'), '<Project />')
    Copy-Item -LiteralPath (Join-Path $repo 'global.json') -Destination (Join-Path $consumer 'global.json')
    Copy-Item -LiteralPath (Join-Path $repo 'tests/packaging/GapConsumer/Program.cs') -Destination (Join-Path $consumer 'Program.cs')
    $project = Join-Path $consumer 'ExternalGapConsumer.csproj'
    $template = Get-Content -LiteralPath (Join-Path $repo 'tests/packaging/GapConsumer/GapConsumer.csproj.in') -Raw
    [IO.File]::WriteAllText($project, $template.Replace('@VERSION@', $Version))
    $escapedFeed = [Security.SecurityElement]::Escape($feed)
    $config = '<configuration><packageSources><clear/><add key="local" value="' + $escapedFeed + '"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources><packageSourceMapping><packageSource key="local"><package pattern="PiSharp.*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping></configuration>'
    [IO.File]::WriteAllText((Join-Path $consumer 'NuGet.Config'), $config)
    Push-Location $consumer
    try {
        Stage 'restore' @('restore',$project,'--configfile',(Join-Path $consumer 'NuGet.Config'),'--disable-parallel') | Out-Null
        $assets = Get-Content -LiteralPath (Join-Path $consumer 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
        foreach ($entry in $assets.libraries.GetEnumerator()) {
            if ($entry.Key.StartsWith('PiSharp.', [StringComparison]::Ordinal)) {
                if ($entry.Value.type -cne 'package' -or !$entry.Key.EndsWith('/' + $Version, [StringComparison]::Ordinal)) { throw 'PiSharp dependency did not resolve from the exact package version.' }
            }
        }
        foreach ($id in $ids) { if (!$assets.libraries.ContainsKey("$id/$Version")) { throw "Missing restored package $id." } }
        foreach ($id in @('PiSharp.Tools','PiSharp.Agent','PiSharp.AI')) {
            if (!$assets.libraries.ContainsKey("$id/$Version")) { throw "Missing shared native-containment dependency $id." }
        }
        $diagnostics = Stage 'experimental-diagnostics' @('build',$project,'-c','Release','--no-restore','--disable-build-servers') $true
        $diagnosticText = Get-Content -LiteralPath $diagnostics -Raw
        foreach ($id in @('PISHARP_CHORD001','PISHARP_ENV001')) { if (!$diagnosticText.Contains($id)) { throw "Expected diagnostic $id was absent." } }
        Stage 'build' @('build',$project,'-c','Release','--no-restore','--disable-build-servers','-p:AdmitExperimental=true') | Out-Null
        Stage 'run' @('exec',(Join-Path $consumer 'bin/Release/net10.0/ExternalGapConsumer.dll')) | Out-Null
    } finally { Pop-Location }
    Copy-Item -LiteralPath (Join-Path $consumer 'obj/project.assets.json') -Destination (Join-Path $output 'project.assets.json')
    foreach ($package in $packages) {
        if ((Get-FileHash -LiteralPath $package.file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $package.sha256) { throw 'A package changed during consumer validation.' }
    }
    $after = SourceState
    $receipt.sourceAfterSha256 = SourceDigest $after
    $after | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $output 'source-after.json') -Encoding utf8NoBOM
    if ($receipt.sourceAfterSha256 -cne $receipt.sourceBeforeSha256) { throw 'Source changed during package validation; rerun the stable source.' }
    $receipt.packageConsumptionProven = $true
    $receipt.result = 'passed'
} catch {
    $receipt.error = $_.Exception.Message
    throw
} finally {
    $env:NUGET_PACKAGES = $oldPackages
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $oldTelemetry
    if ($lease) { $lease.Dispose() }
    $receipt.stages = $stages.ToArray()
    $receipt.packages = $packages.ToArray()
    $receipt | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $output 'receipt.json') -Encoding utf8NoBOM
}
