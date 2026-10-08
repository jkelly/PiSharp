# Authored source controls, unexecuted. Temporary files stay within the chosen test workspace.
. (Join-Path $PSScriptRoot '../../tools/release/ReleaseArtifactFiles.ps1')
function New-TestManifest($root){
    $commit='6170379555cc9d817cdf3a360857a319ab8ba31b'
    $payloads=@(foreach($kind in @('dotnet-tool','win-x64','linux-x64','osx-arm64')){
        $bytes=[Text.Encoding]::UTF8.GetBytes('synthetic-'+$kind)
        [IO.File]::WriteAllBytes((Join-Path $root ($kind+'.zip')),$bytes)
        @{kind=$kind;path=$kind+'.zip';bytes=$bytes.Length;sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant();producerCommit=$commit;runtime='JIT';selfContained=($kind -ne 'dotnet-tool')}
    })
    @{schemaVersion=1;version='0.1.0-preview.1';commit=$commit;tree='8405c976f4ded93746e6ff88532e26d6a4ce0cfc';artifacts=$payloads}
}
function Invoke-ByteCase($root,$mutate,$expected){
    $m=New-TestManifest $root
    & $mutate $m $root
    $doc=[Text.Json.JsonDocument]::Parse(($m|ConvertTo-Json -Depth 8))
    try{
        $r=Test-PiSharpReleaseArtifactFiles $doc.RootElement $root
        if($r.artifactBytesVerified -ne $expected -or $r.releaseAccepted -or $r.publishPermitted){throw 'Artifact byte result mismatch'}
        # All read handles must be released even after missing/tampered input.
        foreach($file in [IO.Directory]::EnumerateFiles($root)){
            $exclusive=[IO.FileStream]::new($file,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
            $exclusive.Dispose()
        }
    }finally{$doc.Dispose()}
}
$root=Join-Path $PSScriptRoot ('byte-fixture-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
try{
    Invoke-ByteCase $root {param($m,$r)} $true
    Invoke-ByteCase $root {param($m,$r);[IO.File]::AppendAllText((Join-Path $r 'linux-x64.zip'),'changed')} $false
    Invoke-ByteCase $root {param($m,$r);[IO.File]::WriteAllText((Join-Path $r 'osx-arm64.zip'),'same-length?')} $false
    Invoke-ByteCase $root {param($m,$r);[IO.File]::Delete((Join-Path $r 'osx-arm64.zip'))} $false
    Invoke-ByteCase $root {param($m,$r);$m.artifacts[0].path='../escape.zip'} $false
    Invoke-ByteCase $root {param($m,$r);$m.artifacts[0].path='NUL.zip'} $false
    Invoke-ByteCase $root {param($m,$r);$m.artifacts[0].path='payload.'} $false
    Invoke-ByteCase $root {param($m,$r);$m.artifacts[0].producerCommit='f'*40} $false
}finally{
    # This exact newly created flat fixture directory is the only cleanup target.
    foreach($file in [IO.Directory]::EnumerateFiles($root)){[IO.File]::Delete($file)}
    [IO.Directory]::Delete($root)
}
