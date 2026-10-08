# Byte validation only. Does not install, extract, restore, build or execute.
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../packaging/distribution-validation.ps1')
function Assert-PiSharpNativeSdkPackage {
    param([Parameter(Mandatory)][string]$Package,
        [Parameter(Mandatory)][ValidateSet('PiSharp.Contracts','PiSharp.Extensions.Abstractions','PiSharp.Extensions.Runtime')][string]$PackageId,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$BuildAssembly)
    if($Version-cnotmatch'^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$'){throw 'Explicit SDK SemVer required.'}
    $files=Get-PiSharpArchiveInventory -Path $Package
    $assembly='lib/net10.0/'+$PackageId+'.dll'
    $required=@($assembly,($PackageId+'.nuspec'),'LICENSE','README.md','THIRD-PARTY-NOTICES.md','provenance.json','[Content_Types].xml','_rels/.rels')
    foreach($name in $required){if(-not$files.ContainsKey($name)){throw 'Required native SDK package member absent.'}}
    foreach($name in $files.Keys){if($name-cnotin$required-and$name-cnotmatch'^package/services/metadata/core-properties/[0-9a-f]+\.psmdcp$'){throw 'Unexpected SDK package member.'}}
    if(@($files.Keys|Where-Object{$_-cmatch'^package/services/metadata/core-properties/'}).Count-ne1){throw 'Exactly one NuGet core-properties entry required.'}
    foreach($name in @('LICENSE','README.md','THIRD-PARTY-NOTICES.md')){
        if($files[$name].sha256-cne(Get-FileHash -LiteralPath (Join-Path $Repo $name) -Algorithm SHA256).Hash.ToLowerInvariant()){throw 'SDK source/license document differs.'}
    }
    if($files[$assembly].sha256-cne(Get-FileHash -LiteralPath $BuildAssembly -Algorithm SHA256).Hash.ToLowerInvariant()-or
       $files[$assembly].bytes-ne(Get-Item -LiteralPath $BuildAssembly).Length){throw 'SDK package assembly differs from admitted build.'}
    $provenance=Read-PiSharpArchiveText -Path $Package -Entry 'provenance.json'|ConvertFrom-Json -AsHashtable -NoEnumerate
    if($provenance.schemaVersion-ne1-or$provenance.kind-cne'native-sdk'-or$provenance.sourceCommit-cne$SourceCommit-or
       $provenance.version-cne$Version-or$provenance.sdk-cne'10.0.401'-or
       $provenance.baselineCommit-cne'd86654abb8862e201933517d6f1fce9f88dd117f'){throw 'SDK provenance differs.'}
    $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null
    $reader=[Xml.XmlReader]::Create([IO.StringReader]::new((Read-PiSharpArchiveText -Path $Package -Entry ($PackageId+'.nuspec'))),$settings)
    $xml=[Xml.XmlDocument]::new();$xml.XmlResolver=$null
    try{$xml.Load($reader)}finally{$reader.Dispose()}
    $meta=$xml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    foreach($pair in @(@('id',$PackageId),@('version',$Version),@('license','MIT'),@('readme','README.md'))){
        $node=$meta.SelectSingleNode('*[local-name()="'+$pair[0]+'"]');if($null-eq$node-or$node.InnerText-cne$pair[1]){throw 'SDK nuspec metadata differs.'}
    }
    if($meta.SelectSingleNode('*[local-name()="license"]').GetAttribute('type')-cne'expression'){throw 'SDK SPDX expression required.'}
    $repository=$meta.SelectSingleNode('*[local-name()="repository"]')
    if($null-eq$repository-or$repository.GetAttribute('commit')-cne$SourceCommit-or$repository.GetAttribute('type')-cne'git'){throw 'SDK repository provenance absent.'}
    if($meta.SelectNodes('*[local-name()="packageTypes"]/*').Count){throw 'Native SDK must be ordinary libraries, not tools.'}
    $dependencies=$meta.SelectNodes('*[local-name()="dependencies"]//*[local-name()="dependency"]')
    $groups=$meta.SelectNodes('*[local-name()="dependencies"]/*[local-name()="group"]')
    if($groups.Count-ne1-or$groups[0].GetAttribute('targetFramework')-cnotin@('net10.0','.NETCoreApp10.0')-or
       $meta.SelectNodes('*[local-name()="dependencies"]/*[local-name()="dependency"]').Count){throw 'One exact net10 SDK dependency group required.'}
    $expected=switch($PackageId){'PiSharp.Contracts'{$null};'PiSharp.Extensions.Abstractions'{'PiSharp.Contracts'};'PiSharp.Extensions.Runtime'{'PiSharp.Extensions.Abstractions'}}
    if($null-eq$expected){if($dependencies.Count-ne0){throw 'Contracts package must not add dependencies.'}}
    else{
        if($dependencies.Count-ne1-or$dependencies[0].GetAttribute('id')-cne$expected-or
           $dependencies[0].GetAttribute('version')-cnotin@($Version,('['+$Version+', )'),('['+$Version+']'))){throw 'SDK dependency identity/version differs.'}
    }
    return [pscustomobject]@{schemaVersion=1;packageId=$PackageId;version=$Version;sourceCommit=$SourceCommit;
        archiveSha256=(Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash.ToLowerInvariant();files=@($files.Values);
        packageConsumptionProven=$false;releaseAccepted=$false;nodeFreePayload=$true}
}
