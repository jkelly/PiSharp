# Authored synthetic contract tests; not executed. No artifacts are installed or acquired.
. (Join-Path $PSScriptRoot '../../tools/release/ReleaseArtifactManifest.ps1')
function Get-ValidMetadata {
    $commit='6170379555cc9d817cdf3a360857a319ab8ba31b'
    @{schemaVersion=1;commit=$commit;tree='8405c976f4ded93746e6ff88532e26d6a4ce0cfc';version='0.1.0-preview.1';artifacts=@(foreach($kind in @('dotnet-tool','win-x64','linux-x64','osx-arm64')){
        @{kind=$kind;path=($kind+'.zip');sha256=('a'*64);bytes=123;producerCommit=$commit;runtime='JIT';selfContained=($kind -ne 'dotnet-tool')}
    })}
}
function Check-Metadata($value,$expected) {
    $document=[System.Text.Json.JsonDocument]::Parse(($value|ConvertTo-Json -Depth 8))
    try{$result=Test-PiSharpReleaseArtifactManifest $document.RootElement
        if($result.metadataConsistent -ne $expected -or $result.releaseAccepted -or $result.publishPermitted -or $result.artifactBytesVerified){throw 'Metadata contract mismatch'}
    }finally{$document.Dispose()}
}
Check-Metadata (Get-ValidMetadata) $true
$m=Get-ValidMetadata;$m.artifacts[1].producerCommit='f'*40;Check-Metadata $m $false
$m=Get-ValidMetadata;$m.artifacts[1].selfContained=$false;Check-Metadata $m $false
$m=Get-ValidMetadata;$m.artifacts[0].selfContained=$true;Check-Metadata $m $false
$m=Get-ValidMetadata;$m.artifacts[1].path='../payload.zip';Check-Metadata $m $false
$m=Get-ValidMetadata;$m.artifacts[1].path=$m.artifacts[0].path.ToUpperInvariant();Check-Metadata $m $false
$m=Get-ValidMetadata;$m.artifacts[1].bytes=1.5;Check-Metadata $m $false
$m=Get-ValidMetadata;$m.artifacts[1].sha256='g'*64;Check-Metadata $m $false
$m=Get-ValidMetadata;$m.artifacts[1].runtime='AOT';Check-Metadata $m $false
$m=Get-ValidMetadata;$m.artifacts=$m.artifacts[0..2];Check-Metadata $m $false
$m=Get-ValidMetadata;$m.schemaVersion='1';Check-Metadata $m $false
$m=Get-ValidMetadata;$m.artifacts[1].kind='win-arm64';Check-Metadata $m $false
$d=[System.Text.Json.JsonDocument]::Parse('{"schemaVersion":1,"schemaVersion":1}')
try{if((Test-PiSharpReleaseArtifactManifest $d.RootElement).metadataConsistent){throw 'Duplicate accepted'}}finally{$d.Dispose()}
