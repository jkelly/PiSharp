# Synthetic archive controls. Authored only; must be separately authorized to run.
# Fake DLL/runtime bytes never execute. No build, install, extraction or subprocess.
[CmdletBinding()]
param([string]$Repo = (Join-Path $PSScriptRoot '../..'))
$ErrorActionPreference = 'Stop'
$Repo = [IO.Path]::GetFullPath($Repo)
. (Join-Path $Repo 'tools/release/standalone-release.ps1')
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('pisharp-release-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$commit = '7452a7b2a355e1f603a31eb693def91a9f565df1'
$version = '0.1.0-preview.1'
$baselineLock = Get-Content -LiteralPath (Join-Path $Repo 'compatibility/target.lock.json') -Raw | ConvertFrom-Json
$results = [Collections.Generic.List[object]]::new()
# Fixture metadata/layout follows the existing distribution-validator controls.
function New-FixtureFiles {
    param([switch]$Standalone, [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')][string]$Rid = 'win-x64',
        [bool]$EnablePromptTemplateYaml = $true)
    $files = [Collections.Generic.Dictionary[string,byte[]]]::new([StringComparer]::Ordinal)
    foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md')) { $files.Add($name, [IO.File]::ReadAllBytes((Join-Path $Repo $name))) }
    $provenance = @{ schemaVersion = 1; sourceCommit = $commit; version = $version; baselineTag = $baselineLock.source.tag;
        baselineCommit = $baselineLock.source.commit; sdk = '10.0.401';
        kind = $(if ($Standalone) { 'standalone' } else { 'tool' }); rid = $(if ($Standalone) { $Rid } else { $null });
        enablePromptTemplateYaml = $EnablePromptTemplateYaml }
    $files.Add('provenance.json', [Text.Encoding]::UTF8.GetBytes(($provenance | ConvertTo-Json)))
    $prefix = if ($Standalone) { '' } else { 'tools/net10.0/any/' }
    foreach ($name in @('Cli', 'AI', 'Agent', 'Contracts', 'CodingAgent', 'Tools', 'Rpc', 'Tui', 'Sessions',
        'Extensions.Abstractions', 'Extensions.Runtime', 'Extensions.Agent')) {
        $files.Add($prefix + "PiSharp.$name.dll", [Text.Encoding]::UTF8.GetBytes("fixture-only-$name"))
    }
    $config = if ($Standalone) { '{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"}]}}' }
        else { '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' }
    $files.Add($prefix + 'PiSharp.Cli.runtimeconfig.json', [Text.Encoding]::UTF8.GetBytes($config))
    $target = if ($Standalone) { '.NETCoreApp,Version=v10.0/' + $Rid } else { '.NETCoreApp,Version=v10.0' }
    $libraries = @{ 'PiSharp.Cli/0.1.0' = @{ type = 'project' } }
    $runtime = @{ 'PiSharp.Cli/0.1.0' = @{ runtime = @{ 'PiSharp.Cli.dll' = @{} } } }
    if ($EnablePromptTemplateYaml) {
        $lock = Get-Content -LiteralPath (Join-Path $Repo 'src/PiSharp.Cli/packages.lock.json') -Raw | ConvertFrom-Json -AsHashtable
        $libraries['YamlDotNet/16.3.0'] = @{ type = 'package'; sha512 = 'sha512-' + $lock.dependencies['net10.0'].YamlDotNet.contentHash }
        $libraries['PiSharp.PromptTemplates.Yaml/1.0.0'] = @{ type = 'project' }
        $runtime['YamlDotNet/16.3.0'] = @{ runtime = @{ 'lib/net8.0/YamlDotNet.dll' = @{} } }
        $runtime['PiSharp.PromptTemplates.Yaml/1.0.0'] = @{ runtime = @{ 'PiSharp.PromptTemplates.Yaml.dll' = @{} } }
        foreach ($name in @('YamlDotNet', 'PiSharp.PromptTemplates.Yaml')) {
            $files.Add($prefix + "$name.dll", [Text.Encoding]::UTF8.GetBytes("fixture-only-$name"))
        }
        $files.Add($prefix + 'licenses/YamlDotNet.LICENSE.txt', [IO.File]::ReadAllBytes((Join-Path $Repo 'third-party/YamlDotNet.LICENSE.txt')))
    }
    $deps = @{ runtimeTarget = @{ name = $target }; libraries = $libraries; targets = @{ $target = $runtime } }
    $files.Add($prefix + 'PiSharp.Cli.deps.json', [Text.Encoding]::UTF8.GetBytes(($deps | ConvertTo-Json -Depth 10)))
    if ($Standalone) {
        $runtimeNames = switch ($Rid) {
            'win-x64' { @('PiSharp.Cli.exe', 'coreclr.dll', 'hostpolicy.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll') }
            'linux-x64' { @('PiSharp.Cli', 'libcoreclr.so', 'libhostpolicy.so', 'libhostfxr.so', 'System.Private.CoreLib.dll') }
            'osx-arm64' { @('PiSharp.Cli', 'libcoreclr.dylib', 'libhostpolicy.dylib', 'libhostfxr.dylib', 'System.Private.CoreLib.dll') }
        }
        foreach ($name in $runtimeNames) { $files.Add($name, [byte[]]@(1,2,3)) }
    } else {
        $files.Add('PiSharp.Cli.nuspec', [Text.Encoding]::UTF8.GetBytes(@"
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>
<id>PiSharp.Cli</id><version>$version</version><authors>PiSharp contributors</authors>
<license type="expression">MIT</license><readme>README.md</readme><description>fixture</description>
<repository type="git" commit="$commit"/><packageTypes><packageType name="DotnetTool"/></packageTypes>
</metadata></package>
"@))
        $files.Add($prefix + 'DotnetToolSettings.xml', [Text.Encoding]::UTF8.GetBytes('<DotNetCliTool><Commands><Command Name="pisharp" EntryPoint="PiSharp.Cli.dll" Runner="dotnet"/></Commands></DotNetCliTool>'))
    }
    return ,$files
}

function Write-Payload {
    param([string]$Name,[string]$Rid,[bool]$Yaml=$false,[switch]$Reverse)
    $root=Join-Path $scratch $Name
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $files=New-FixtureFiles -Standalone -Rid $Rid -EnablePromptTemplateYaml:$Yaml
    [string[]]$names=@($files.Keys); [Array]::Sort($names,[StringComparer]::Ordinal)
    if ($Reverse) { [Array]::Reverse($names) }
    foreach ($name in $names) {
        $path=Join-Path $root $name
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        [IO.File]::WriteAllBytes($path,$files[$name])
    }
    return $root
}
function Check { param([bool]$Condition,[string]$Message) if (-not $Condition) { throw $Message } }
function Case { param([string]$Name,[scriptblock]$Body) & $Body; $results.Add(@{name=$Name;result='pass'}) }
function Reject {
    param([scriptblock]$Body,[string]$Message)
    try { & $Body | Out-Null } catch {
        if (-not $_.Exception.Message.Contains($Message,[StringComparison]::OrdinalIgnoreCase)) { throw }
        return
    }
    throw ('Expected rejection: '+$Message)
}
try {
    foreach ($rid in @('win-x64','linux-x64','osx-arm64')) {
        Case ('producer and resolver exact target '+$rid) {
            $payload=Write-Payload -Name ('payload-'+$rid) -Rid $rid
            $archive=Join-Path $scratch ($rid+'.zip'); $manifest=$archive+'.json'
            $release=New-PiSharpStandaloneRelease -PayloadDirectory $payload -ArchivePath $archive -ManifestPath $manifest -Repo $Repo -Version $version -SourceCommit $commit -Rid $rid -EnablePromptTemplateYaml:$false
            $argsResolve=@{ManifestPath=$manifest;ExpectedManifestSha256=$release.manifestSha256;ArchivePath=$archive;Repo=$Repo;Version=$version;SourceCommit=$commit;Rid=$rid}
            $resolved=Resolve-PiSharpStandaloneRelease @argsResolve
            Check ($resolved.action -ceq 'SideBySideCandidate' -and $resolved.payloadVerified -and -not $resolved.installed -and -not $resolved.platformQualified -and -not $resolved.userDataTouched) 'Offline receipt overstated or candidate lost.'
            $current=Resolve-PiSharpStandaloneRelease @argsResolve -CurrentVersion $version -CurrentArchiveSha256 $resolved.sha256
            Check ($current.action -ceq 'AlreadyCurrent') 'Exact current artifact not recognized.'
            $inventory=Get-PiSharpArchiveInventory -Path $archive
            Check ($inventory.ContainsKey('provenance.json') -and -not $inventory.ContainsKey('pi/provenance.json')) 'Root archive layout changed.'
            if ($rid -ne 'win-x64') { Check (($inventory['PiSharp.Cli'].unixMode -band 0x40) -ne 0) 'Unix apphost lost owner execute mode.' }
            $again=Write-PiSharpStandaloneArchive -PayloadDirectory $payload -ArchivePath ($archive+'.repeat') -Rid $rid
            Check ($again.sha256 -ceq $resolved.sha256) 'Repeated archive bytes differ.'
            Reject { Resolve-PiSharpStandaloneRelease @argsResolve -CurrentVersion $version -CurrentArchiveSha256 ('0'*64) } 'different artifact bytes'
            Reject { Resolve-PiSharpStandaloneRelease @argsResolve -CurrentVersion $version } 'supplied together'
            $bad=$argsResolve.Clone(); $bad.Version='9.0.0'
            Reject { Resolve-PiSharpStandaloneRelease @bad } 'target mismatch'
            $bad=$argsResolve.Clone(); $bad.SourceCommit='f'*40
            Reject { Resolve-PiSharpStandaloneRelease @bad } 'target mismatch'
            $bad=$argsResolve.Clone(); $bad.Rid=if ($rid -eq 'win-x64') {'linux-x64'} else {'win-x64'}
            Reject { Resolve-PiSharpStandaloneRelease @bad } 'target mismatch'
            $bad=$argsResolve.Clone(); $bad.ExpectedManifestSha256='0'*64
            Reject { Resolve-PiSharpStandaloneRelease @bad } 'independently supplied checksum'
        }
    }
    Case 'determinism ignores file timestamp and creation order' {
        $payload=Join-Path $scratch 'payload-linux-x64'
        foreach ($path in [IO.Directory]::EnumerateFiles($payload,'*',[IO.SearchOption]::AllDirectories)) { [IO.File]::SetLastWriteTimeUtc($path,[DateTime]::new(2020,2,3,4,5,6,[DateTimeKind]::Utc)) }
        $other=Write-Payload -Name 'reverse' -Rid linux-x64 -Reverse
        $first=Write-PiSharpStandaloneArchive -PayloadDirectory $payload -ArchivePath (Join-Path $scratch 'deterministic-a.zip') -Rid linux-x64
        $second=Write-PiSharpStandaloneArchive -PayloadDirectory $other -ArchivePath (Join-Path $scratch 'deterministic-b.zip') -Rid linux-x64
        Check ($first.sha256 -ceq $second.sha256) 'Filesystem timestamps/order leaked into archive bytes.'
    }
    Case 'YAML enabled payload retains parser license and dependency closure' {
        $payload=Write-Payload -Name 'yaml' -Rid win-x64 -Yaml:$true
        $archive=Join-Path $scratch 'yaml.zip'
        $release=New-PiSharpStandaloneRelease -PayloadDirectory $payload -ArchivePath $archive -ManifestPath ($archive+'.json') -Repo $Repo -Version $version -SourceCommit $commit -Rid win-x64
        $resolved=Resolve-PiSharpStandaloneRelease -ManifestPath $release.manifest -ExpectedManifestSha256 $release.manifestSha256 -ArchivePath $archive -Repo $Repo -Version $version -SourceCommit $commit -Rid win-x64
        Check $resolved.payloadVerified 'YAML release lost validation.'
    }
    Case 'independently pinned but inconsistent file inventory is refused' {
        $archive=Join-Path $scratch 'linux-x64.zip'; $manifest=$archive+'.json'
        $record=[IO.File]::ReadAllText($manifest) | ConvertFrom-Json -AsHashtable
        $record.files[0].sha256='0'*64
        $badManifest=Join-Path $scratch 'inventory-mismatch.json'
        [IO.File]::WriteAllText($badManifest,($record | ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
        $expected=(Get-FileHash -LiteralPath $badManifest -Algorithm SHA256).Hash.ToLowerInvariant()
        Reject { Resolve-PiSharpStandaloneRelease -ManifestPath $badManifest -ExpectedManifestSha256 $expected -ArchivePath $archive -Repo $Repo -Version $version -SourceCommit $commit -Rid linux-x64 } 'inventory differs'
    }
    Case 'artifact tampering rejected without modifying user data' {
        $sentinel=Join-Path $scratch 'sessions.keep'; [IO.File]::WriteAllText($sentinel,'private-session-sentinel')
        $archive=Join-Path $scratch 'win-x64.zip'; $manifest=$archive+'.json'
        $expected=(Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash.ToLowerInvariant()
        $stream=[IO.File]::Open($archive,[IO.FileMode]::Append,[IO.FileAccess]::Write)
        try { $stream.WriteByte(99) } finally { $stream.Dispose() }
        Reject { Resolve-PiSharpStandaloneRelease -ManifestPath $manifest -ExpectedManifestSha256 $expected -ArchivePath $archive -Repo $Repo -Version $version -SourceCommit $commit -Rid win-x64 } 'Artifact bytes differ'
        Check ([IO.File]::ReadAllText($sentinel) -ceq 'private-session-sentinel') 'User data mutated.'
    }
    Case 'existing outputs never overwritten' {
        $archive=Join-Path $scratch 'occupied.zip'; [IO.File]::WriteAllText($archive,'old-release')
        Reject { New-PiSharpStandaloneRelease -PayloadDirectory (Join-Path $scratch 'payload-win-x64') -ArchivePath $archive -ManifestPath ($archive+'.json') -Repo $Repo -Version $version -SourceCommit $commit -Rid win-x64 -EnablePromptTemplateYaml:$false } 'new distinct paths'
        Check ([IO.File]::ReadAllText($archive) -ceq 'old-release') 'Existing release changed.'
    }
    Case 'structurally invalid payload removes only newly created archive' {
        $payload=Write-Payload -Name 'missing-runtime' -Rid win-x64
        [IO.File]::Delete((Join-Path $payload 'hostfxr.dll'))
        $archive=Join-Path $scratch 'missing-runtime.zip'
        Reject { New-PiSharpStandaloneRelease -PayloadDirectory $payload -ArchivePath $archive -ManifestPath ($archive+'.json') -Repo $Repo -Version $version -SourceCommit $commit -Rid win-x64 -EnablePromptTemplateYaml:$false } 'hostfxr.dll'
        Check (-not [IO.File]::Exists($archive) -and -not [IO.File]::Exists($archive+'.json')) 'Rejected artifact left release outputs.'
        Check ([IO.File]::Exists((Join-Path $payload 'PiSharp.Cli.exe'))) 'Input payload deleted.'
    }
    Case 'outputs within payload are refused' {
        $payload=Join-Path $scratch 'payload-linux-x64'
        Reject { Write-PiSharpStandaloneArchive -PayloadDirectory $payload -ArchivePath (Join-Path $payload 'nested.zip') -Rid linux-x64 } 'outside payload'
        Reject { New-PiSharpStandaloneRelease -PayloadDirectory $payload -ArchivePath (Join-Path $scratch 'outside.zip') -ManifestPath (Join-Path $payload 'manifest.json') -Repo $Repo -Version $version -SourceCommit $commit -Rid linux-x64 -EnablePromptTemplateYaml:$false } 'outside payload'
    }
    @{schemaVersion=1;evidenceKind='synthetic-offline-release-controls';platformQualified=$false;tests=@($results)} | ConvertTo-Json -Depth 8
} finally {
    # Only this GUID-owned scratch directory is removed, never payload/user roots.
    if ([IO.Directory]::Exists($scratch)) { [IO.Directory]::Delete($scratch,$true) }
}
