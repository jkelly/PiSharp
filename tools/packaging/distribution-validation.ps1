# Offline archive inspection only: no extraction, subprocesses, installs or network.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-PiSharpArchiveInventory {
    # -AllowNodeBridge admits only the optional bridge's own PiSharp.Compatibility.Node.* members;
    # Node/npm executables, node_modules and credential files stay forbidden.
    param([Parameter(Mandatory)][string]$Path, [switch]$AllowNodeBridge)
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    $files = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $folded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $foldedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    try {
        if ($archive.Entries.Count -gt 20000) { throw 'Archive entry limit exceeded.' }
        [long]$total = 0
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            $parts = $name.TrimEnd('/').Split('/')
            if ($name -match '[\x00-\x1f<>|?*]' -or $name.Contains('\') -or $name.Contains(':') -or $name.StartsWith('/') -or
                @($parts | Where-Object { $_ -in @('', '.', '..') -or $_.EndsWith('.') -or $_.EndsWith(' ') -or
                    $_ -match '^(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)' }).Count) {
                throw "Nonportable archive path: $name"
            }
            if (-not $folded.Add($name.TrimEnd('/'))) { throw "Duplicate/case-colliding archive path: $name" }
            if (($entry.ExternalAttributes -shr 16 -band 0xF000) -eq 0xA000) { throw "Archive symlink: $name" }
            if ($name.EndsWith('/')) { continue }
            $foldedFiles.Add($name) | Out-Null
            $total += $entry.Length
            if ($entry.Length -gt 512MB -or $total -gt 2GB) { throw 'Archive uncompressed size limit exceeded.' }
            if ($name -match '(?i)(^|/)(node_modules|node|node\.exe|npm|npm\.cmd|npx|npx\.cmd)(/|$)' -or
                $name -match '(?i)(\.(pfx|p12|key)$|(^|/)\.env($|\.))' -or
                (-not $AllowNodeBridge -and $name -match '(?i)PiSharp\.Compatibility\.Node\.')) {
                throw "Forbidden native distribution payload: $name"
            }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() }
            finally { $sha.Dispose(); $stream.Dispose() }
            $files.Add($name, [pscustomobject]@{ path = $name; bytes = $entry.Length; sha256 = $hash;
                unixMode = ($entry.ExternalAttributes -shr 16 -band 0xFFFF) })
        }
        # Check ancestors of every file and explicit directory, independent of
        # entry order and casing on the eventual extraction filesystem.
        foreach ($name in $folded) {
            $parent = $name
            while ($parent.Contains('/')) {
                $parent = $parent.Substring(0, $parent.LastIndexOf('/'))
                if ($foldedFiles.Contains($parent)) { throw "File/directory archive collision: $parent" }
            }
        }
        return ,$files
    }
    finally { $archive.Dispose() }
}

function Read-PiSharpArchiveText {
    param([string]$Path, [string]$Entry)
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    try {
        $item = $archive.GetEntry($Entry)
        if ($null -eq $item -or $item.Length -gt 4MB) { throw "Missing/oversized metadata: $Entry" }
        $reader = [IO.StreamReader]::new($item.Open(), [Text.UTF8Encoding]::new($false, $true), $true)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Assert-PiSharpDistribution {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][ValidateSet('tool', 'standalone')][string]$Kind,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
        [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')][string]$Rid,
        [bool]$EnablePromptTemplateYaml = $true
    )
    # A fourth numeric segment marks a C#-only patch on an unchanged Pi baseline.
    if ($Version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') { throw 'Explicit SemVer candidate required.' }
    if ($Kind -eq 'standalone' -and -not $Rid) { throw 'Standalone RID required.' }
    $baseline = Get-Content -LiteralPath (Join-Path $Repo 'compatibility/target.lock.json') -Raw | ConvertFrom-Json
    # The target baseline must be a well-formed tagged commit that the release record agrees with.
    $release = Get-Content -LiteralPath (Join-Path $Repo 'compatibility/public-release.json') -Raw | ConvertFrom-Json
    if ($baseline.source.tag -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+$' -or $baseline.source.commit -cnotmatch '^[0-9a-f]{40}$' -or
        $release.upstream.tag -cne $baseline.source.tag -or $release.upstream.commit -cne $baseline.source.commit) { throw 'Pinned Pi baseline changed.' }
    $files = Get-PiSharpArchiveInventory -Path $Path
    $sdk = (Get-Content -LiteralPath (Join-Path $Repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $provenance = Read-PiSharpArchiveText -Path $Path -Entry 'provenance.json' | ConvertFrom-Json
    if ($provenance.schemaVersion -ne 1 -or $provenance.sourceCommit -cne $SourceCommit -or $provenance.version -cne $Version -or
        $provenance.baselineCommit -cne $baseline.source.commit -or $provenance.baselineTag -cne $baseline.source.tag -or
        $provenance.sdk -cne $sdk -or $provenance.kind -cne $Kind -or
        ($Kind -eq 'standalone' -and $provenance.rid -cne $Rid)) { throw 'Candidate provenance mismatch.' }
    $yamlSetting = $provenance.PSObject.Properties['enablePromptTemplateYaml']
    if ($null -eq $yamlSetting -or $yamlSetting.Value -isnot [bool] -or $yamlSetting.Value -ne $EnablePromptTemplateYaml) {
        throw 'Candidate YAML feature provenance mismatch.'
    }
    foreach ($notice in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md')) {
        if (-not $files.ContainsKey($notice)) { throw "Missing distribution document: $notice" }
        if ($files[$notice].sha256 -cne (Get-FileHash -LiteralPath (Join-Path $Repo $notice) -Algorithm SHA256).Hash.ToLowerInvariant()) {
            throw "Distribution document differs from admitted source: $notice"
        }
    }
    $notices = Read-PiSharpArchiveText -Path $Path -Entry 'THIRD-PARTY-NOTICES.md'
    if (-not $notices.Contains('Copyright (c) 2025 Mario Zechner') -or -not $notices.Contains($baseline.source.commit)) { throw 'Pinned Pi attribution missing.' }
    $prefix = ''
    if ($Kind -eq 'tool') {
        $nuspecs = @($files.Keys | Where-Object { $_ -cmatch '^[^/]+\.nuspec$' })
        if ($nuspecs.Count -ne 1) { throw 'Exactly one root nuspec required.' }
        # Prohibit DTD/entity resolution even for hostile package XML.
        $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
        $reader = [Xml.XmlReader]::Create([IO.StringReader]::new((Read-PiSharpArchiveText -Path $Path -Entry $nuspecs[0])), $settings)
        $xml = [Xml.XmlDocument]::new(); $xml.XmlResolver = $null
        try { $xml.Load($reader) } finally { $reader.Dispose() }
        $meta = $xml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        foreach ($pair in @(@('id', 'PiSharp.Cli'), @('version', $Version), @('license', 'MIT'), @('readme', 'README.md'))) {
            $node = $meta.SelectSingleNode('*[local-name()="' + $pair[0] + '"]')
            if ($null -eq $node -or $node.InnerText -cne $pair[1]) { throw "Package metadata mismatch: $($pair[0])" }
        }
        if ($meta.SelectSingleNode('*[local-name()="license"]').GetAttribute('type') -cne 'expression') { throw 'SPDX license expression required.' }
        foreach ($field in @('authors', 'description')) {
            $node = $meta.SelectSingleNode('*[local-name()="' + $field + '"]')
            if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) { throw "Missing package metadata: $field" }
        }
        $repository = $meta.SelectSingleNode('*[local-name()="repository"]')
        if ($null -eq $repository -or $repository.GetAttribute('commit') -cne $SourceCommit -or $repository.GetAttribute('type') -cne 'git') { throw 'Package source commit missing/mismatched.' }
        $toolType = $meta.SelectSingleNode('*[local-name()="packageTypes"]/*[local-name()="packageType" and @name="DotnetTool"]')
        if ($null -eq $toolType) { throw 'DotnetTool package type required.' }
        $prefix = 'tools/net10.0/any/'
        $toolText = Read-PiSharpArchiveText -Path $Path -Entry ($prefix + 'DotnetToolSettings.xml')
        $toolReader = [Xml.XmlReader]::Create([IO.StringReader]::new($toolText), $settings)
        $toolXml = [Xml.XmlDocument]::new(); $toolXml.XmlResolver = $null
        try { $toolXml.Load($toolReader) } finally { $toolReader.Dispose() }
        $commands = $toolXml.SelectNodes('/DotNetCliTool/Commands/Command')
        if ($commands.Count -ne 1 -or $commands[0].GetAttribute('Name') -cne 'pisharp' -or
            $commands[0].GetAttribute('EntryPoint') -cne 'PiSharp.Cli.dll' -or $commands[0].GetAttribute('Runner') -cne 'dotnet') { throw 'Tool command contract mismatch.' }
    }
    foreach ($assembly in @('Cli', 'AI', 'Agent', 'Contracts', 'CodingAgent', 'Tools', 'Rpc', 'Tui', 'Sessions',
        'Extensions.Abstractions', 'Extensions.Runtime', 'Extensions.Agent')) {
        if (-not $files.ContainsKey($prefix + "PiSharp.$assembly.dll")) { throw "Missing native assembly: $assembly" }
    }
    $config = Read-PiSharpArchiveText -Path $Path -Entry ($prefix + 'PiSharp.Cli.runtimeconfig.json') | ConvertFrom-Json -AsHashtable
    if ($config.runtimeOptions.tfm -cne 'net10.0') { throw 'Unexpected target framework.' }
    if ($Kind -eq 'tool' -and (-not $config.runtimeOptions.ContainsKey('framework') -or
        $config.runtimeOptions.framework.name -cne 'Microsoft.NETCore.App' -or $config.runtimeOptions.ContainsKey('includedFrameworks'))) {
        throw 'Framework-dependent tool runtime configuration required.'
    }
    $deps = Read-PiSharpArchiveText -Path $Path -Entry ($prefix + 'PiSharp.Cli.deps.json') | ConvertFrom-Json -AsHashtable
    if (@($deps.libraries.Keys | Where-Object { $_ -match '(?i)(PiSharp\.Compatibility\.Node|(^|/)node(js)?/)' }).Count) { throw 'Node dependency in native dependency manifest.' }
    $yamlLibraries = @($deps.libraries.Keys | Where-Object { $_ -match '(?i)^(YamlDotNet|PiSharp\.PromptTemplates\.Yaml)/' })
    $yamlFiles = @($files.Keys | Where-Object { $_ -match '(?i)(^|/)(YamlDotNet|PiSharp\.PromptTemplates\.Yaml)(\.|/|$)' })
    if (-not $deps.ContainsKey('targets') -or -not $deps.targets.ContainsKey($deps.runtimeTarget.name)) { throw 'Dependency runtime target missing.' }
    $target = $deps.targets[$deps.runtimeTarget.name]
    $yamlTargetLibraries = @($deps.targets.Values | ForEach-Object { $_.Keys } | Where-Object { $_ -match '(?i)^(YamlDotNet|PiSharp\.PromptTemplates\.Yaml)/' })
    $yamlTargetAssets = @($deps.targets.Values | ForEach-Object { $_.Values } | ForEach-Object {
        foreach ($group in @('runtime', 'runtimeTargets', 'native', 'resources')) {
            if ($_.ContainsKey($group)) { $_[$group].Keys }
        }
    } | Where-Object { $_ -match '(?i)(^|/)(YamlDotNet|PiSharp\.PromptTemplates\.Yaml)(\.|/|$)' })
    if ($EnablePromptTemplateYaml) {
        foreach ($name in @('YamlDotNet.dll', 'PiSharp.PromptTemplates.Yaml.dll', 'licenses/YamlDotNet.LICENSE.txt')) {
            if (-not $files.ContainsKey($prefix + $name)) { throw "Missing YAML payload: $name" }
        }
        if ($files[$prefix + 'licenses/YamlDotNet.LICENSE.txt'].sha256 -cne
            (Get-FileHash -LiteralPath (Join-Path $Repo 'third-party/YamlDotNet.LICENSE.txt') -Algorithm SHA256).Hash.ToLowerInvariant()) {
            throw 'YAML license differs from admitted source.'
        }
        $yamlLock = Get-Content -LiteralPath (Join-Path $Repo 'src/PiSharp.Cli/packages.lock.json') -Raw | ConvertFrom-Json -AsHashtable
        $parser = $yamlLock.dependencies['net10.0'].YamlDotNet
        if ($parser.resolved -cne '16.3.0' -or $yamlLibraries.Count -ne 2 -or -not $deps.libraries.ContainsKey('YamlDotNet/16.3.0') -or
            $deps.libraries['YamlDotNet/16.3.0'].type -cne 'package' -or
            $deps.libraries['YamlDotNet/16.3.0'].sha512 -cne ('sha512-' + $parser.contentHash)) { throw 'YAML package identity mismatch.' }
        $adapter = @($yamlLibraries | Where-Object { $_ -cmatch '^PiSharp\.PromptTemplates\.Yaml/' })
        if ($adapter.Count -ne 1 -or $deps.libraries[$adapter[0]].type -cne 'project' -or
            -not $target.ContainsKey($adapter[0]) -or -not $target.ContainsKey('YamlDotNet/16.3.0') -or
            -not $target[$adapter[0]].ContainsKey('runtime') -or -not $target[$adapter[0]].runtime.ContainsKey('PiSharp.PromptTemplates.Yaml.dll') -or
            -not $target['YamlDotNet/16.3.0'].ContainsKey('runtime') -or -not $target['YamlDotNet/16.3.0'].runtime.ContainsKey('lib/net8.0/YamlDotNet.dll')) {
            throw 'YAML runtime assets missing from dependency target.'
        }
        if (@($yamlTargetLibraries | Where-Object { $_ -cnotin $yamlLibraries }).Count) {
            throw 'Unexpected YAML dependency target identity.'
        }
    } elseif ($yamlFiles.Count -or $yamlLibraries.Count -or $yamlTargetLibraries.Count -or $yamlTargetAssets.Count) {
        throw 'YAML payload or dependency in disabled CLI distribution.'
    }
    if ($Kind -eq 'standalone') {
        if (-not $deps.runtimeTarget.name.EndsWith('/' + $Rid, [StringComparison]::Ordinal)) { throw 'Dependency RID mismatch.' }
        if (-not $config.runtimeOptions.ContainsKey('includedFrameworks') -or $config.runtimeOptions.ContainsKey('framework')) { throw 'Self-contained runtime configuration required.' }
        $runtimeFiles = switch ($Rid) {
            'win-x64' { @('PiSharp.Cli.exe', 'coreclr.dll', 'hostpolicy.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll') }
            'linux-x64' { @('PiSharp.Cli', 'libcoreclr.so', 'libhostpolicy.so', 'libhostfxr.so', 'System.Private.CoreLib.dll') }
            'osx-arm64' { @('PiSharp.Cli', 'libcoreclr.dylib', 'libhostpolicy.dylib', 'libhostfxr.dylib', 'System.Private.CoreLib.dll') }
        }
        foreach ($file in $runtimeFiles) { if (-not $files.ContainsKey($file)) { throw "Missing self-contained payload: $file" } }
        if ($Rid -ne 'win-x64' -and ($files['PiSharp.Cli'].unixMode -band 0x40) -eq 0) { throw 'Unix CLI owner executable bit missing.' }
    }
    return [pscustomobject]@{
        schemaVersion = 1; evidenceKind = 'offline-artifact-structure'; platformQualified = $false
        sourceCommit = $SourceCommit; baselineTag = $baseline.source.tag; baselineCommit = $baseline.source.commit
        sdk = $sdk
        version = $Version; kind = $Kind; rid = $Rid; enablePromptTemplateYaml = $EnablePromptTemplateYaml
        archiveSha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        files = @($files.Values | Sort-Object -Property path -CaseSensitive)
        dependencies = @($deps.libraries.Keys | Sort-Object -CaseSensitive)
        licenseClosure = 'HOLD: runtime pack/dependency redistribution review and SBOM required separately'
    }
}

function Assert-PiSharpReproduciblePayload {
    param([Parameter(Mandatory)][string]$First, [Parameter(Mandatory)][string]$Second)
    $left = Get-PiSharpArchiveInventory -Path $First; $right = Get-PiSharpArchiveInventory -Path $Second
    if ($left.Count -ne $right.Count) { throw 'Reproducibility: file count differs.' }
    foreach ($name in $left.Keys) {
        if (-not $right.ContainsKey($name) -or $left[$name].sha256 -cne $right[$name].sha256 -or
            $left[$name].bytes -ne $right[$name].bytes -or $left[$name].unixMode -ne $right[$name].unixMode) {
            throw "Reproducibility: payload differs: $name"
        }
    }
    return [pscustomobject]@{ unsignedPayloadsMatch = $true
        archiveBytesMatch = ((Get-FileHash -LiteralPath $First).Hash -ceq (Get-FileHash -LiteralPath $Second).Hash)
        fileCount = $left.Count; qualification = 'Archive inspection only; independent clean build evidence required' }
}
