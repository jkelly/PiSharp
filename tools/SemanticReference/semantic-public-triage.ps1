[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [switch] $CreateCensus
)

# Static JSON analysis only. No package, compiler, native helper or SDK executes.
# The default invocation verifies an existing census without writing any file.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath($RepositoryRoot)
$expectedRelative = 'fixtures/semantic-full-project-catalog/expected.json'
$expectedPath = Join-Path $repo $expectedRelative
$expectedHash = '5999081da5235765a1a0dc7090ca770ba32a11e939a824973fb1d3317b7a77df'
$expectedBytes = 13305865
$censusPath = Join-Path $repo 'compatibility/semantic-public-triage.census.json'

function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Exact-Unique($Values) {
    $set = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($value in $Values) { [void] $set.Add([string] $value) }
    $keys = [Collections.Generic.List[string]]::new($set)
    $keys.Sort([StringComparer]::Ordinal)
    $keys.ToArray()
}

function Code-Counts($Values) {
    @($Values | Group-Object code | Sort-Object { [long] $_.Name } | ForEach-Object {
        [ordered]@{ code = [long] $_.Name; count = $_.Count }
    })
}

function Stats($Values, [scriptblock] $Selector) {
    $groups = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($value in $Values) {
        $key = [string] (& $Selector $value)
        if (-not $groups.ContainsKey($key)) { $groups.Add($key, [Collections.Generic.List[object]]::new()) }
        $groups[$key].Add($value)
    }
    foreach ($key in (Exact-Unique $groups.Keys)) {
        $group = @($groups[$key].ToArray())
        [ordered]@{
            key = $key
            count = $group.Count
            files = @(Exact-Unique @($group.fileName)).Count
            messages = @(Exact-Unique @($group.text)).Count
            codes = @(Code-Counts $group)
        }
    }
}

function Observation-Family($Diagnostic) {
    $file = $Diagnostic.fileName.Substring($Diagnostic.fileName.IndexOf('/upstream/') + 10)
    $text = $Diagnostic.text
    if ($Diagnostic.code -eq 2307) { return 'missing-module' }
    if ($file -cmatch '^packages/ai/src/providers/[^/]+\.models\.ts$') { return 'provider-json-type-constraints' }
    if ($text.Contains("parameter of type 'never'")) { return 'never-parameter' }
    if ($text -cmatch 'ChatModelCatalog|ProviderModel') { return 'catalog-named-type' }
    if ($text -cmatch 'SandboxConfig') { return 'sandbox-inherited-type' }
    if ($Diagnostic.code -in @(7006, 7031, 7019)) { return 'implicit-any-callbacks' }
    if ($Diagnostic.code -eq 18046) { return 'unknown-value' }
    if ($Diagnostic.code -eq 2347) { return 'untyped-generic-call' }
    return 'other'
}

Assert ((Get-Item -LiteralPath $expectedPath).Length -eq $expectedBytes) 'Raw snapshot size mismatch.'
Assert ((Get-FileHash -LiteralPath $expectedPath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $expectedHash) 'Raw snapshot hash mismatch.'
$inputSnapshot = [IO.File]::ReadAllText($expectedPath) | ConvertFrom-Json -Depth 100
$methods = @('getConfigFileParsingDiagnostics', 'getGlobalDiagnostics', 'getProgramDiagnostics', 'getSyntacticDiagnostics', 'getBindDiagnostics', 'getSemanticDiagnostics', 'getSuggestionDiagnostics', 'getDeclarationDiagnostics')
Assert (@($inputSnapshot.diagnostics.PSObject.Properties).Count -eq 8) 'Diagnostic family count changed.'
$diagnosticCounts = [ordered]@{}
foreach ($method in $methods) { $diagnosticCounts[$method] = @($inputSnapshot.diagnostics.$method).Count }
$ds = @($inputSnapshot.diagnostics.getSemanticDiagnostics)
Assert ($ds.Count -eq 1246) 'Semantic count changed.'
Assert (@($inputSnapshot.unresolved).Count -eq 711) 'Unresolved count changed.'
Assert (@($inputSnapshot.mandatoryBaselineLockInstances).Count -eq 34) 'Mandatory lock instance scope changed.'
Assert (@($inputSnapshot.rootFiles).Count -eq 1661) 'Original root count changed.'
Assert (@($inputSnapshot.entrypoints).Count -eq 150) 'Entrypoint condition scope changed.'
Assert (@($inputSnapshot.requiredPublishedOwnershipBlockers).Count -eq 3) 'Published ownership blocker scope changed.'
Assert ($inputSnapshot.wholeConfigurationOpenedUnchanged -and -not $inputSnapshot.diagnosticFiltering -and -not $inputSnapshot.pathNormalization) 'Input observation boundary changed.'
$unresolvedByIndex = @{}
foreach ($item in $inputSnapshot.unresolved) {
    Assert ($item.method -ceq 'getSemanticDiagnostics') 'Unexpected unresolved diagnostic family.'
    Assert (-not $unresolvedByIndex.ContainsKey([long] $item.index)) 'Duplicate unresolved index.'
    $unresolvedByIndex.Add([long] $item.index, $item)
}
$records = @(
    for ($index = 0; $index -lt $ds.Count; $index++) {
        $d = $ds[$index]
        Assert ($d.fileName.Contains('/upstream/')) 'A source diagnostic has no upstream display address.'
        Assert ($d.text -is [string]) 'A diagnostic text needs a separately reviewed grouping key.'
        $row = [ordered]@{ index = $index; observationFamily = (Observation-Family $d); diagnostic = $d }
        if ($d.code -eq 2307) {
            Assert ($unresolvedByIndex.ContainsKey([long] $index)) 'Missing unresolved attribution.'
            $attribution = $unresolvedByIndex[[long] $index]
            Assert (($attribution.value | ConvertTo-Json -Depth 100 -Compress) -ceq ($d | ConvertTo-Json -Depth 100 -Compress)) 'Unresolved attribution differs from original diagnostic.'
            $row.unresolvedAttribution = $attribution
        }
        $row
    }
)
$familyRows = foreach ($family in (Exact-Unique @($records.observationFamily))) {
    $group = @($records | Where-Object observationFamily -CEQ $family)
    [pscustomobject][ordered]@{ family = $family; count = $group.Count; files = @(Exact-Unique @($group.diagnostic.fileName)).Count; codes = @(Code-Counts @($group.diagnostic)) }
}
$lockRows = foreach ($instance in $inputSnapshot.mandatoryBaselineLockInstances) {
    $group = @($inputSnapshot.unresolved | Where-Object { $_.nearestLockCandidate.lockPath -ceq $instance.lockPath })
    $specifierRows = foreach ($specifier in (Exact-Unique @($group | ForEach-Object { $_.parsedSpecifier }))) {
        [ordered]@{ specifier = $specifier; count = @($group | Where-Object parsedSpecifier -CEQ $specifier).Count }
    }
    [pscustomobject][ordered]@{ original = $instance; currentUnresolved = $group.Count; currentSpecifiers = @($specifierRows) }
}
Assert (($familyRows | Measure-Object count -Sum).Sum -eq 1246) 'Observation families are not exhaustive.'
Assert (($lockRows | Measure-Object currentUnresolved -Sum).Sum -eq 711) 'Lock attribution is not exhaustive.'
$census = [ordered]@{
    schemaVersion = 1
    kind = 'authored-static-residual-analysis-of-qualified-whole-config-capture'
    input = [ordered]@{ path = $expectedRelative; bytes = $expectedBytes; sha256 = $expectedHash; sourceSha = $inputSnapshot.sourceSha }
    boundary = [ordered]@{ referenceCaptureRunHere = $false; compilerOrSdkExecutedHere = $false; causalDiagnosisQualified = $false; diagnosticFiltering = $false; pathNormalization = $false; semanticPublicClosure = $false; phaseGatesPassed = @() }
    diagnosticCounts = $diagnosticCounts
    summary = [ordered]@{
        records = $ds.Count; diagnosedFiles = @(Exact-Unique @($ds.fileName)).Count; exactMessages = @(Exact-Unique @($ds.text)).Count
        unresolvedRecords = @($inputSnapshot.unresolved).Count; unresolvedFiles = @(Exact-Unique @($inputSnapshot.unresolved.value.fileName)).Count
        non2307Records = @($ds | Where-Object code -NE 2307).Count; non2307Files = @(Exact-Unique @(@($ds | Where-Object code -NE 2307).fileName)).Count
        literalUnresolvedSpecifiers = @(Exact-Unique @($inputSnapshot.unresolved.parsedSpecifier)).Count
        currentLockInstances = @($lockRows | Where-Object currentUnresolved -GT 0).Count; mandatoryLockInstances = $lockRows.Count
        rootFiles = @($inputSnapshot.rootFiles).Count; programSourceFiles = @($inputSnapshot.sourceFiles).Count; programDeclarationFiles = @($inputSnapshot.programDeclarationFiles).Count
        originalCanonicalSourceRows = $inputSnapshot.originalCanonicalSourceRows; manifestRecords = @($inputSnapshot.manifestRecords).Count
        entrypointConditionRecords = @($inputSnapshot.entrypoints).Count; bindingRows = @($inputSnapshot.bindings).Count; moduleCandidateRows = @($inputSnapshot.modules).Count
        publishedOwnershipBlockers = @($inputSnapshot.requiredPublishedOwnershipBlockers).Count
    }
    observationFamilies = @($familyRows)
    byCode = @(Stats $ds { param($d) [string] $d.code })
    byPackage = @(Stats $ds { param($d) ($d.fileName.Split('/upstream/packages/')[1]).Split('/')[0] })
    byFile = @(Stats $ds { param($d) $d.fileName })
    byExactMessage = @(Stats $ds { param($d) $d.text })
    mandatoryLockInstances = @($lockRows)
    requiredPublishedOwnershipBlockers = @($inputSnapshot.requiredPublishedOwnershipBlockers)
    records = $records
}
$serialized = ($census | ConvertTo-Json -Depth 100).Replace("`r`n", "`n") + "`n"
$bytes = [Text.UTF8Encoding]::new($false).GetBytes($serialized)
if ($CreateCensus) {
    # CreateNew rejects an existing/partial census. Never overwrite committed evidence.
    $output = [IO.File]::Open($censusPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $output.Write($bytes, 0, $bytes.Length) } finally { $output.Dispose() }
} else {
    $actual = [IO.File]::ReadAllBytes($censusPath)
    Assert ($actual.Length -eq $bytes.Length) 'Authored census byte count differs from fresh static derivation.'
    Assert ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($actual)) -ceq [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))) 'Authored census differs from fresh static derivation.'
}
[ordered]@{ action = $(if ($CreateCensus) { 'created-exclusive-analysis' } else { 'verified-read-only-analysis' }); records = $ds.Count; unresolvedRecords = 711; diagnosedFiles = $census.summary.diagnosedFiles; exactMessages = $census.summary.exactMessages; censusBytes = $bytes.Length; censusSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant(); semanticPublicClosure = $false; phaseGatesPassed = @() } | ConvertTo-Json
