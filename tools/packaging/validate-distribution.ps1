[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Artifact,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedSha256,
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][ValidateSet('tool', 'standalone')][string]$Kind,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [bool]$EnablePromptTemplateYaml = $true,
    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')][string]$Rid,
    [string]$RepeatArtifact
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'distribution-validation.ps1')
if ((Get-FileHash -LiteralPath $Artifact -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ExpectedSha256) {
    throw 'Candidate archive differs from the independently supplied checksum.'
}
$arguments = @{ Path = $Artifact; Repo = $Repo; Kind = $Kind; Version = $Version; SourceCommit = $SourceCommit; EnablePromptTemplateYaml = $EnablePromptTemplateYaml }
if ($Rid) { $arguments.Rid = $Rid }
$report = Assert-PiSharpDistribution @arguments
if ($RepeatArtifact) {
    $arguments.Path = $RepeatArtifact
    $repeatReport = Assert-PiSharpDistribution @arguments
    $report | Add-Member -NotePropertyName repeatArchiveSha256 -NotePropertyValue $repeatReport.archiveSha256
    $report | Add-Member -NotePropertyName reproducibility -NotePropertyValue (Assert-PiSharpReproduciblePayload -First $Artifact -Second $RepeatArtifact)
}
$report | ConvertTo-Json -Depth 12
