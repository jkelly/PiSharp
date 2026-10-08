Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ReleaseArtifactManifest.ps1')
# Caller supplies an explicitly admitted release staging directory. No acquisition or archive execution.
function Test-PiSharpReleaseArtifactFiles {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Text.Json.JsonElement]$Manifest,
          [Parameter(Mandatory)][string]$ArtifactRoot)
    $metadata=Test-PiSharpReleaseArtifactManifest $Manifest
    $errors=[Collections.Generic.List[string]]::new()
    $originalErrors=[Collections.Generic.List[Exception]]::new()
    $streams=[Collections.Generic.List[IO.FileStream]]::new()
    $admitted=[Collections.Generic.List[object]]::new()
    $measurements=[Collections.Generic.List[object]]::new()
    foreach($error in $metadata.errors){$errors.Add($error)}
    if($metadata.metadataConsistent){
        try {
            $root=[IO.Path]::GetFullPath($ArtifactRoot)
            $directory=[IO.DirectoryInfo]::new($root)
            if(-not $directory.Exists -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)){
                throw [IO.IOException]::new('A real release staging directory is required.')
            }
            # Acquire all four read leases before measuring any payload. On Windows these deny write/delete
            # sharing for the interval. Unix filesystems require host-owned staging exclusivity separately.
            foreach($entry in $Manifest.GetProperty('artifacts').EnumerateArray()){
                $name=$entry.GetProperty('path').GetString()
                if($name -imatch '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)' -or $name.EndsWith('.')){
                    throw [IO.IOException]::new('Reserved artifact filename.')
                }
                $path=[IO.Path]::Combine($root,$name)
                $file=[IO.FileInfo]::new($path)
                if(-not $file.Exists -or ($file.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Directory))){
                    throw [IO.IOException]::new('A regular flat payload file is required.')
                }
                $stream=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
                $streams.Add($stream)
                $admitted.Add([pscustomobject]@{name=$name;entry=$entry;stream=$stream})
            }
            foreach($payload in $admitted){
                $expectedLength=$payload.entry.GetProperty('bytes').GetInt64()
                $expectedHash=$payload.entry.GetProperty('sha256').GetString()
                $initialLength=$payload.stream.Length
                $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($payload.stream)).ToLowerInvariant()
                $consumed=$payload.stream.Position
                $finalLength=$payload.stream.Length
                if($initialLength -ne $expectedLength -or $finalLength -ne $expectedLength -or $consumed -ne $expectedLength -or $hash -cne $expectedHash){
                    $errors.Add($payload.name+': payload bytes/hash mismatch')
                }
                $measurements.Add([pscustomobject]@{name=$payload.name;bytes=$consumed;sha256=$hash})
            }
        } catch {
            $originalErrors.Add($_.Exception)
            $errors.Add('Artifact admission or measurement failed.')
        } finally {
            foreach($stream in $streams){
                try{$stream.Dispose()}catch{$originalErrors.Add($_.Exception);$errors.Add('Artifact read lease cleanup failed.')}
            }
        }
    }
    [pscustomobject]@{schemaVersion=1;metadataConsistent=$metadata.metadataConsistent;
        artifactBytesVerified=($metadata.metadataConsistent -and $errors.Count -eq 0 -and $measurements.Count -eq 4);
        measuredPayloads=$measurements.ToArray();errors=$errors.ToArray();originalErrors=$originalErrors.ToArray();
        readLeasesAcquired=$streams.Count;readLeasesCleanupAttempted=$streams.Count;
        snapshotOnly=$true;releaseAccepted=$false;publishPermitted=$false}
}
