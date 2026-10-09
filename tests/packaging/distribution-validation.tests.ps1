# Authored pure-PowerShell fixture suite. Does not build, install or invoke the CLI.
# Execution is reserved for the separately assigned coordinator.
[CmdletBinding()]
param([string]$Repo = (Join-Path $PSScriptRoot '../..'))
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path -LiteralPath $Repo).Path
. (Join-Path $Repo 'tools/packaging/distribution-validation.ps1')
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('pisharp-distribution-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$commit = '7452a7b2a355e1f603a31eb693def91a9f565df1'
$version = '0.1.0-preview.1'
$baselineLock = Get-Content -LiteralPath (Join-Path $Repo 'compatibility/target.lock.json') -Raw | ConvertFrom-Json
$results = [Collections.Generic.List[object]]::new()

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
function Write-Fixture {
    param($Files, [string]$Name, [string]$ExtraName, [int]$ExtraMode = 0, [hashtable]$Modes = @{}, [switch]$ExtraFirst)
    $path = Join-Path $scratch ($Name + '.zip')
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        if ($ExtraName -and $ExtraFirst) { $entry = $zip.CreateEntry($ExtraName); $entry.ExternalAttributes = $ExtraMode }
        foreach ($pair in $Files.GetEnumerator()) {
            $entry = $zip.CreateEntry($pair.Key)
            if ($Modes.ContainsKey($pair.Key)) { $entry.ExternalAttributes = [int]$Modes[$pair.Key] -shl 16 }
            $stream = $entry.Open(); try { $stream.Write($pair.Value, 0, $pair.Value.Length) } finally { $stream.Dispose() }
        }
        if ($ExtraName -and -not $ExtraFirst) { $entry = $zip.CreateEntry($ExtraName); $entry.ExternalAttributes = $ExtraMode }
    } finally { $zip.Dispose() }
    return $path
}
function Test-Case {
    param([string]$Name, [scriptblock]$Body)
    & $Body
    $results.Add([pscustomobject]@{ name = $Name; result = 'pass' })
}
function Assert-Rejected {
    param([scriptblock]$Body, [string]$Message)
    try { & $Body | Out-Null } catch {
        if ($_.Exception.Message -notlike ('*' + $Message + '*')) { throw "Wrong rejection: $($_.Exception.Message); expected $Message" }
        return
    }
    throw "Expected rejection: $Message"
}
try {
    $tool = Write-Fixture -Files (New-FixtureFiles) -Name 'tool'
    $argsTool = @{ Repo = $Repo; Kind = 'tool'; Version = $version; SourceCommit = $commit }
    Test-Case 'tool metadata and documents' { $report = Assert-PiSharpDistribution -Path $tool @argsTool; if ($report.platformQualified) { throw 'Qualification overstated.' } }
    $standalone = Write-Fixture -Files (New-FixtureFiles -Standalone) -Name 'standalone'
    Test-Case 'self-contained Windows payload' { Assert-PiSharpDistribution -Path $standalone -Repo $Repo -Kind standalone -Version $version -SourceCommit $commit -Rid win-x64 | Out-Null }
    $disabledTool = Write-Fixture -Files (New-FixtureFiles -EnablePromptTemplateYaml:$false) -Name 'yaml-disabled-tool'
    $argsDisabled = $argsTool.Clone(); $argsDisabled.EnablePromptTemplateYaml = $false
    Test-Case 'disabled tool excludes YAML assets and dependencies' {
        $report = Assert-PiSharpDistribution -Path $disabledTool @argsDisabled
        if ($report.enablePromptTemplateYaml) { throw 'Disabled feature overstated.' }
    }
    $disabledStandalone = Write-Fixture -Files (New-FixtureFiles -Standalone -EnablePromptTemplateYaml:$false) -Name 'yaml-disabled-standalone'
    Test-Case 'disabled standalone excludes YAML assets and dependencies' {
        Assert-PiSharpDistribution -Path $disabledStandalone -Repo $Repo -Kind standalone -Version $version -SourceCommit $commit -Rid win-x64 -EnablePromptTemplateYaml:$false | Out-Null
    }
    Test-Case 'feature provenance must match enabled validation' { Assert-Rejected { Assert-PiSharpDistribution -Path $disabledTool @argsTool } 'YAML feature provenance mismatch' }
    Test-Case 'feature provenance must match disabled validation' { Assert-Rejected { Assert-PiSharpDistribution -Path $tool @argsDisabled } 'YAML feature provenance mismatch' }
    foreach ($setting in @('absent', 'string')) {
        $files = New-FixtureFiles
        $provenance = [Text.Encoding]::UTF8.GetString($files['provenance.json']) | ConvertFrom-Json -AsHashtable
        if ($setting -eq 'absent') { $provenance.Remove('enablePromptTemplateYaml') } else { $provenance.enablePromptTemplateYaml = 'true' }
        $files['provenance.json'] = [Text.Encoding]::UTF8.GetBytes(($provenance | ConvertTo-Json))
        $bad = Write-Fixture -Files $files -Name ('yaml-provenance-' + $setting)
        Test-Case ("reject $setting YAML provenance") { Assert-Rejected { Assert-PiSharpDistribution -Path $bad @argsTool } 'YAML feature provenance mismatch' }
    }
    foreach ($location in @('selected', 'other')) {
        $files = New-FixtureFiles
        $deps = [Text.Encoding]::UTF8.GetString($files['tools/net10.0/any/PiSharp.Cli.deps.json']) | ConvertFrom-Json -AsHashtable
        $targetName = $deps.runtimeTarget.name
        if ($location -eq 'other') { $targetName += '/win-x64'; $deps.targets[$targetName] = @{} }
        $deps.targets[$targetName]['YamlDotNet/99.0.0'] = @{ runtime = @{ 'lib/net8.0/YamlDotNet.dll' = @{} } }
        $files['tools/net10.0/any/PiSharp.Cli.deps.json'] = [Text.Encoding]::UTF8.GetBytes(($deps | ConvertTo-Json -Depth 10))
        $bad = Write-Fixture -Files $files -Name ('unexpected-enabled-yaml-target-' + $location)
        Test-Case ("enabled tool rejects unexpected YAML identity in $location target") {
            Assert-Rejected { Assert-PiSharpDistribution -Path $bad @argsTool } 'Unexpected YAML dependency target identity'
        }
    }
    $files = New-FixtureFiles
    $deps = [Text.Encoding]::UTF8.GetString($files['tools/net10.0/any/PiSharp.Cli.deps.json']) | ConvertFrom-Json -AsHashtable
    $deps.targets[($deps.runtimeTarget.name + '/win-x64')] = $deps.targets[$deps.runtimeTarget.name]
    $files['tools/net10.0/any/PiSharp.Cli.deps.json'] = [Text.Encoding]::UTF8.GetBytes(($deps | ConvertTo-Json -Depth 10))
    $duplicateTargets = Write-Fixture -Files $files -Name 'enabled-expected-yaml-multiple-targets'
    Test-Case 'enabled tool allows admitted YAML identities in multiple targets' {
        Assert-PiSharpDistribution -Path $duplicateTargets @argsTool | Out-Null
    }
    foreach ($name in @('YamlDotNet.dll', 'PiSharp.PromptTemplates.Yaml.dll', 'licenses/YamlDotNet.LICENSE.txt')) {
        $files = New-FixtureFiles; $files.Remove('tools/net10.0/any/' + $name) | Out-Null
        $bad = Write-Fixture -Files $files -Name ('missing-yaml-' + $results.Count)
        Test-Case ("enabled tool requires $name") { Assert-Rejected { Assert-PiSharpDistribution -Path $bad @argsTool } 'Missing YAML payload' }
    }
    $files = New-FixtureFiles; $files['tools/net10.0/any/licenses/YamlDotNet.LICENSE.txt'] = [Text.Encoding]::UTF8.GetBytes('declaration only')
    $bad = Write-Fixture -Files $files -Name 'yaml-license-bytes'
    Test-Case 'YAML notice matches retained source bytes' { Assert-Rejected { Assert-PiSharpDistribution -Path $bad @argsTool } 'YAML license differs' }
    foreach ($change in @('version', 'hash', 'package-type', 'runtime', 'adapter-runtime', 'target')) {
        $files = New-FixtureFiles
        $deps = [Text.Encoding]::UTF8.GetString($files['tools/net10.0/any/PiSharp.Cli.deps.json']) | ConvertFrom-Json -AsHashtable
        $runtime = $deps.targets[$deps.runtimeTarget.name]
        switch ($change) {
            'version' { $deps.libraries['YamlDotNet/16.3.1'] = $deps.libraries['YamlDotNet/16.3.0']; $deps.libraries.Remove('YamlDotNet/16.3.0') }
            'hash' { $deps.libraries['YamlDotNet/16.3.0'].sha512 = 'sha512-wrong' }
            'package-type' { $deps.libraries['YamlDotNet/16.3.0'].type = 'project' }
            'runtime' { $runtime['YamlDotNet/16.3.0'].runtime.Clear() }
            'adapter-runtime' { $runtime['PiSharp.PromptTemplates.Yaml/1.0.0'].runtime.Clear() }
            'target' { $deps.targets.Clear() }
        }
        $files['tools/net10.0/any/PiSharp.Cli.deps.json'] = [Text.Encoding]::UTF8.GetBytes(($deps | ConvertTo-Json -Depth 10))
        $bad = Write-Fixture -Files $files -Name ('yaml-deps-' + $change)
        $message = switch ($change) { 'runtime' { 'YAML runtime assets missing' }; 'adapter-runtime' { 'YAML runtime assets missing' }; 'target' { 'Dependency runtime target missing' }; default { 'YAML package identity mismatch' } }
        Test-Case ("enabled YAML dependency validation $change") { Assert-Rejected { Assert-PiSharpDistribution -Path $bad @argsTool } $message }
    }
    foreach ($name in @('YamlDotNet.dll', 'PiSharp.PromptTemplates.Yaml.dll', 'licenses/YamlDotNet.LICENSE.txt')) {
        $files = New-FixtureFiles -EnablePromptTemplateYaml:$false
        $files.Add('tools/net10.0/any/' + $name, [byte[]]@(1))
        $bad = Write-Fixture -Files $files -Name ('disabled-stale-yaml-' + $results.Count)
        Test-Case ("disabled tool rejects $name") { Assert-Rejected { Assert-PiSharpDistribution -Path $bad @argsDisabled } 'YAML payload or dependency' }
    }
    foreach ($location in @('libraries', 'targets', 'runtime-assets')) {
        $files = New-FixtureFiles -EnablePromptTemplateYaml:$false
        $deps = [Text.Encoding]::UTF8.GetString($files['tools/net10.0/any/PiSharp.Cli.deps.json']) | ConvertFrom-Json -AsHashtable
        if ($location -eq 'libraries') { $deps.libraries['YamlDotNet/16.3.0'] = @{ type = 'package' } }
        elseif ($location -eq 'targets') { $deps.targets[$deps.runtimeTarget.name]['PiSharp.PromptTemplates.Yaml/1.0.0'] = @{ runtime = @{} } }
        else { $deps.targets[$deps.runtimeTarget.name]['PiSharp.Cli/0.1.0'].runtime['YamlDotNet.dll'] = @{} }
        $files['tools/net10.0/any/PiSharp.Cli.deps.json'] = [Text.Encoding]::UTF8.GetBytes(($deps | ConvertTo-Json -Depth 10))
        $bad = Write-Fixture -Files $files -Name ('disabled-yaml-deps-' + $location)
        Test-Case ("disabled tool rejects dependency in $location") { Assert-Rejected { Assert-PiSharpDistribution -Path $bad @argsDisabled } 'YAML payload or dependency' }
    }
    Test-Case 'disabled wrapper propagates feature expectation' {
        $repeatDisabled = Write-Fixture -Files (New-FixtureFiles -EnablePromptTemplateYaml:$false) -Name 'disabled-wrapper-repeat'
        & (Join-Path $Repo 'tools/packaging/validate-distribution.ps1') -Artifact $disabledTool -ExpectedSha256 (Get-FileHash -LiteralPath $disabledTool -Algorithm SHA256).Hash.ToLowerInvariant() -RepeatArtifact $repeatDisabled @argsDisabled | Out-Null
    }
    Test-Case 'disabled wrapper rejects enabled repeat artifact' {
        Assert-Rejected {
            & (Join-Path $Repo 'tools/packaging/validate-distribution.ps1') -Artifact $disabledTool -ExpectedSha256 (Get-FileHash -LiteralPath $disabledTool -Algorithm SHA256).Hash.ToLowerInvariant() -RepeatArtifact $tool @argsDisabled
        } 'YAML feature provenance mismatch'
    }
    Test-Case 'RID mismatch' { Assert-Rejected { Assert-PiSharpDistribution -Path $standalone -Repo $Repo -Kind standalone -Version $version -SourceCommit $commit -Rid linux-x64 } 'provenance mismatch' }
    Test-Case 'missing RID' { Assert-Rejected { Assert-PiSharpDistribution -Path $standalone -Repo $Repo -Kind standalone -Version $version -SourceCommit $commit } 'RID required' }
    foreach ($path in @('../escape', '/absolute', 'C:/drive', 'bad\separator', 'CON.txt', 'trailing./file', 'README.MD', 'LICENSE/child', 'node.exe', 'node_modules/package/index.js', '.env', 'credential.pfx', 'PiSharp.Compatibility.Node.dll')) {
        $bad = Write-Fixture -Files (New-FixtureFiles) -Name ('bad-' + $results.Count) -ExtraName $path
        Test-Case ("reject path $path") { Assert-Rejected { Get-PiSharpArchiveInventory -Path $bad } '' }
    }
    foreach ($path in @('license/child', 'LICENSE/empty/', 'license/empty/', 'LiCeNsE/empty/nested/')) {
        foreach ($first in @($false, $true)) {
            $bad = Write-Fixture -Files (New-FixtureFiles) -Name ('ancestor-' + $results.Count) -ExtraName $path -ExtraFirst:$first
            Test-Case ("reject ancestor $path, extra first=$first") { Assert-Rejected { Get-PiSharpArchiveInventory -Path $bad } 'File/directory archive collision' }
        }
    }
    # An explicit directory whose ancestors are all directories remains valid.
    $directory = Write-Fixture -Files (New-FixtureFiles) -Name 'valid-directory' -ExtraName 'tools/net10.0/any/empty/'
    Test-Case 'valid explicit directory ancestor chain' { Get-PiSharpArchiveInventory -Path $directory | Out-Null }
    foreach ($rid in @('win-x64', 'linux-x64', 'osx-arm64')) {
        $files = New-FixtureFiles -Standalone -Rid $rid
        $modes = @{ 'PiSharp.Cli' = 0x81C0 } # Regular file, owner read/write/execute.
        $valid = Write-Fixture -Files $files -Name ('valid-' + $rid) -Modes $modes
        Test-Case ("complete self-contained payload $rid") { Assert-PiSharpDistribution -Path $valid -Repo $Repo -Kind standalone -Version $version -SourceCommit $commit -Rid $rid | Out-Null }
        $loader = switch ($rid) { 'win-x64' { 'hostfxr.dll' }; 'linux-x64' { 'libhostfxr.so' }; 'osx-arm64' { 'libhostfxr.dylib' } }
        $files.Remove($loader) | Out-Null
        $bad = Write-Fixture -Files $files -Name ('missing-hostfxr-' + $rid) -Modes $modes
        Test-Case ("missing native hostfxr loader $rid") { Assert-Rejected { Assert-PiSharpDistribution -Path $bad -Repo $Repo -Kind standalone -Version $version -SourceCommit $commit -Rid $rid } ("Missing self-contained payload: $loader") }
    }
    foreach ($rid in @('linux-x64', 'osx-arm64')) {
        foreach ($mode in @(0x81A4, 0x8038, 0x8007, 0x803F)) {
            $bad = Write-Fixture -Files (New-FixtureFiles -Standalone -Rid $rid) -Name ('unix-mode-' + $results.Count) -Modes @{ 'PiSharp.Cli' = $mode }
            Test-Case ("missing owner execute $rid mode=$mode") { Assert-Rejected { Assert-PiSharpDistribution -Path $bad -Repo $Repo -Kind standalone -Version $version -SourceCommit $commit -Rid $rid } 'owner executable bit missing' }
        }
    }
    $symlink = Write-Fixture -Files (New-FixtureFiles) -Name 'symlink' -ExtraName 'link' -ExtraMode ([int]0xA000 -shl 16)
    Test-Case 'reject symlink' { Assert-Rejected { Get-PiSharpArchiveInventory -Path $symlink } 'symlink' }
    foreach ($missing in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'tools/net10.0/any/PiSharp.Agent.dll', 'tools/net10.0/any/DotnetToolSettings.xml')) {
        $files = New-FixtureFiles; $files.Remove($missing) | Out-Null
        $bad = Write-Fixture -Files $files -Name ('missing-' + $results.Count)
        Test-Case ("missing $missing") { Assert-Rejected { Assert-PiSharpDistribution -Path $bad @argsTool } 'Missing' }
    }
    $files = New-FixtureFiles; $files['LICENSE'] = [Text.Encoding]::UTF8.GetBytes('MIT declaration without retained notice')
    $bad = Write-Fixture -Files $files -Name 'license'
    Test-Case 'retained license bytes' { Assert-Rejected { Assert-PiSharpDistribution -Path $bad @argsTool } 'differs from admitted source' }
    Test-Case 'version mismatch' { $mismatch = $argsTool.Clone(); $mismatch.Version = '9.9.9'; Assert-Rejected { Assert-PiSharpDistribution -Path $tool @mismatch } 'provenance mismatch' }
    Test-Case 'source commit mismatch' { $mismatch = $argsTool.Clone(); $mismatch.SourceCommit = '0000000000000000000000000000000000000000'; Assert-Rejected { Assert-PiSharpDistribution -Path $tool @mismatch } 'provenance mismatch' }
    $files = New-FixtureFiles; $files['tools/net10.0/any/DotnetToolSettings.xml'] = [Text.Encoding]::UTF8.GetBytes('<DotNetCliTool><Commands><Command Name="other" EntryPoint="PiSharp.Cli.dll" Runner="dotnet"/></Commands></DotNetCliTool>')
    $bad = Write-Fixture -Files $files -Name 'command'
    Test-Case 'CLI command metadata' { Assert-Rejected { Assert-PiSharpDistribution -Path $bad @argsTool } 'command contract' }
    $files = New-FixtureFiles; $text = [Text.Encoding]::UTF8.GetString($files['PiSharp.Cli.nuspec']).Replace('<version>' + $version + '</version>', '<version>9.9.9</version>')
    $files['PiSharp.Cli.nuspec'] = [Text.Encoding]::UTF8.GetBytes($text)
    $bad = Write-Fixture -Files $files -Name 'nuspec-version'
    Test-Case 'nuspec version mismatch' { Assert-Rejected { Assert-PiSharpDistribution -Path $bad @argsTool } 'metadata mismatch: version' }
    $files = New-FixtureFiles; $files['tools/net10.0/any/PiSharp.Cli.deps.json'] = [Text.Encoding]::UTF8.GetBytes('{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0"},"libraries":{"nodejs/22.0.0":{"type":"package"}}}')
    $bad = Write-Fixture -Files $files -Name 'node-deps'
    Test-Case 'Node dependency manifest' { Assert-Rejected { Assert-PiSharpDistribution -Path $bad @argsTool } 'Node dependency' }
    $files = New-FixtureFiles -Standalone; $files['PiSharp.Cli.runtimeconfig.json'] = [Text.Encoding]::UTF8.GetBytes('{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}')
    $bad = Write-Fixture -Files $files -Name 'framework-dependent-standalone'
    Test-Case 'standalone requires bundled runtime' { Assert-Rejected { Assert-PiSharpDistribution -Path $bad -Repo $Repo -Kind standalone -Version $version -SourceCommit $commit -Rid win-x64 } 'Self-contained' }
    $files = New-FixtureFiles -Standalone; $files.Remove('coreclr.dll') | Out-Null
    $bad = Write-Fixture -Files $files -Name 'missing-coreclr'
    Test-Case 'missing coreclr runtime payload' { Assert-Rejected { Assert-PiSharpDistribution -Path $bad -Repo $Repo -Kind standalone -Version $version -SourceCommit $commit -Rid win-x64 } 'Missing self-contained' }
    $files = New-FixtureFiles; $files['PiSharp.Cli.nuspec'] = [Text.Encoding]::UTF8.GetBytes('<!DOCTYPE package [<!ENTITY x SYSTEM "file:///missing">]><package>&x;</package>')
    $bad = Write-Fixture -Files $files -Name 'dtd'
    Test-Case 'DTD prohibited' { Assert-Rejected { Assert-PiSharpDistribution -Path $bad @argsTool } 'DTD' }
    $repeat = Write-Fixture -Files (New-FixtureFiles) -Name 'repeat'
    Test-Case 'repeat payload equality' { Assert-PiSharpReproduciblePayload -First $tool -Second $repeat | Out-Null }
    $files = New-FixtureFiles; $files['tools/net10.0/any/PiSharp.Cli.dll'] = [byte[]]@(9)
    $changed = Write-Fixture -Files $files -Name 'changed'
    Test-Case 'changed payload rejected' { Assert-Rejected { Assert-PiSharpReproduciblePayload -First $tool -Second $changed } 'payload differs' }
    Test-Case 'independent checksum tampering rejected' { Assert-Rejected { & (Join-Path $Repo 'tools/packaging/validate-distribution.ps1') -Artifact $tool -ExpectedSha256 ('0' * 64) @argsTool } 'independently supplied checksum' }
    $results | ConvertTo-Json -Depth 5
} finally {
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedScratch.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolvedScratch) -notmatch '^pisharp-distribution-[0-9a-f]{32}$') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
}
