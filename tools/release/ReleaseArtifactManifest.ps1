Set-StrictMode -Version Latest
# Pure metadata admission. No archive extraction, installation, process, network or credential access.
function Test-PiSharpReleaseArtifactManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Text.Json.JsonElement]$Manifest)
    $errors = [Collections.Generic.List[string]]::new()
    function CheckObject([System.Text.Json.JsonElement]$value,[string]$location) {
        if($value.ValueKind -ne [System.Text.Json.JsonValueKind]::Object){return}
        $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($property in $value.EnumerateObject()){if(-not $names.Add($property.Name)){$errors.Add($location+': duplicate field '+$property.Name)}}
    }
    function ReadField([System.Text.Json.JsonElement]$value,[string]$name,[string]$kind,[string]$location) {
        $field = [System.Text.Json.JsonElement]::new()
        if ($value.ValueKind -ne [System.Text.Json.JsonValueKind]::Object -or -not $value.TryGetProperty($name,[ref]$field) -or $field.ValueKind.ToString() -cne $kind) {
            $errors.Add($location+'.'+$name+': required '+$kind); return $null
        }
        return $field
    }
    function TextField([System.Text.Json.JsonElement]$value,[string]$name,[string]$location) {
        $field=ReadField $value $name 'String' $location
        if($null -eq $field){return $null};return $field.GetString()
    }
    CheckObject $Manifest 'manifest'
    $schema=ReadField $Manifest 'schemaVersion' 'Number' 'manifest'
    $schemaNumber=0
    if($null -ne $schema -and (-not $schema.TryGetInt32([ref]$schemaNumber) -or $schemaNumber -ne 1)){$errors.Add('manifest.schemaVersion: expected integer1')}
    $commit=TextField $Manifest 'commit' 'manifest';$tree=TextField $Manifest 'tree' 'manifest'
    foreach($v in @($commit,$tree)){if($null -ne $v -and $v -cnotmatch '^[0-9a-f]{40}$'){$errors.Add('manifest: exact lowercase Git commit/tree required')}}
    $version=TextField $Manifest 'version' 'manifest'
    if($null -ne $version -and $version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$'){$errors.Add('manifest.version: expected explicit version')}
    $artifacts=ReadField $Manifest 'artifacts' 'Array' 'manifest'
    $expected=@('dotnet-tool','win-x64','linux-x64','osx-arm64')
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $count=0
    if($null -ne $artifacts){foreach($artifact in $artifacts.EnumerateArray()){
        $at='artifacts['+$count+']';$count++
        CheckObject $artifact $at
        $kind=TextField $artifact 'kind' $at
        if($null -ne $kind -and ($kind -cnotin $expected -or -not $seen.Add($kind))){$errors.Add($at+'.kind: unexpected or duplicate')}
        $path=TextField $artifact 'path' $at
        if($null -ne $path -and ($path -cnotmatch '^[A-Za-z0-9._-]+$' -or $path -in @('.','..') -or -not $paths.Add($path))){$errors.Add($at+'.path: unique flat artifact filename required')}
        $hash=TextField $artifact 'sha256' $at
        if($null -ne $hash -and $hash -cnotmatch '^[0-9a-f]{64}$'){$errors.Add($at+'.sha256: exact hash required')}
        $bytes=ReadField $artifact 'bytes' 'Number' $at;$length=[long]0
        if($null -ne $bytes -and (-not $bytes.TryGetInt64([ref]$length) -or $length -le 0)){$errors.Add($at+'.bytes: positive integer required')}
        $producer=TextField $artifact 'producerCommit' $at
        if($null -ne $producer -and $producer -cne $commit){$errors.Add($at+'.producerCommit: candidate mismatch')}
        $runtime=TextField $artifact 'runtime' $at
        if($null -ne $runtime -and $runtime -cne 'JIT'){$errors.Add($at+'.runtime: JIT required by current plugin plan')}
        $expectedSelf=if($kind -ceq 'dotnet-tool'){'False'}else{'True'}
        $self=ReadField $artifact 'selfContained' $expectedSelf $at
    }}
    foreach($kind in $expected){if(-not $seen.Contains($kind)){$errors.Add('artifacts: missing '+$kind)}}
    if($count -ne 4){$errors.Add('artifacts: exactly four current profile artifacts required')}
    # This verdict qualifies metadata consistency only. It cannot certify signatures, SBOM content,
    # build reproducibility, archive contents, platform operation, parity or release-owner approval.
    [pscustomobject]@{schemaVersion=1;metadataConsistent=($errors.Count -eq 0);errors=$errors.ToArray();releaseAccepted=$false;publishPermitted=$false;artifactBytesVerified=$false}
}
