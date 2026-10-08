# Byte validation only. Does not install, extract, restore, build or execute.
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../packaging/distribution-validation.ps1')
function Assert-PiSharpNativeSdkPackage {
    param([Parameter(Mandatory)][string]$Package,
        [Parameter(Mandatory)][ValidateSet('PiSharp.Contracts','PiSharp.AI','PiSharp.Agent','PiSharp.Sessions','PiSharp.Tools','PiSharp.CodingAgent',
            'PiSharp.PromptTemplates.Yaml','PiSharp.Rpc','PiSharp.Tui','PiSharp.Extensions.Abstractions','PiSharp.Extensions.Runtime',
            'PiSharp.Extensions.Agent','PiSharp.ExtensionHost','PiSharp.Compatibility.Node')][string]$PackageId,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$BuildAssembly)
    # A fourth numeric segment marks a C#-only patch on an unchanged Pi baseline.
    if($Version-cnotmatch'^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$'){throw 'Explicit SDK SemVer required.'}
    $nodeBridge=$PackageId-ceq'PiSharp.Compatibility.Node'
    $files=Get-PiSharpArchiveInventory -Path $Package -AllowNodeBridge:$nodeBridge
    $assembly='lib/net10.0/'+$PackageId+'.dll'
    $required=@($assembly,($PackageId+'.nuspec'),'LICENSE','README.md','THIRD-PARTY-NOTICES.md','provenance.json','[Content_Types].xml','_rels/.rels')
    if($PackageId-ceq'PiSharp.PromptTemplates.Yaml'){$required+='licenses/YamlDotNet.LICENSE.txt'}
    foreach($name in $required){if(-not$files.ContainsKey($name)){throw 'Required native SDK package member absent.'}}
    # SDK 10 deterministic pack names the core-properties part nuget.psmdcp; older packs use a random hex name.
    foreach($name in $files.Keys){if($name-cnotin$required-and$name-cnotmatch'^package/services/metadata/core-properties/(?:[0-9a-f]+|nuget)\.psmdcp$'){throw 'Unexpected SDK package member.'}}
    if(@($files.Keys|Where-Object{$_-cmatch'^package/services/metadata/core-properties/'}).Count-ne1){throw 'Exactly one NuGet core-properties entry required.'}
    foreach($name in @('LICENSE','README.md','THIRD-PARTY-NOTICES.md')){
        if($files[$name].sha256-cne(Get-FileHash -LiteralPath (Join-Path $Repo $name) -Algorithm SHA256).Hash.ToLowerInvariant()){throw 'SDK source/license document differs.'}
    }
    if($files[$assembly].sha256-cne(Get-FileHash -LiteralPath $BuildAssembly -Algorithm SHA256).Hash.ToLowerInvariant()-or
       $files[$assembly].bytes-ne(Get-Item -LiteralPath $BuildAssembly).Length){throw 'SDK package assembly differs from admitted build.'}
    $provenance=Read-PiSharpArchiveText -Path $Package -Entry 'provenance.json'|ConvertFrom-Json -AsHashtable -NoEnumerate
    if($provenance.schemaVersion-ne1-or$provenance.kind-cne'native-sdk'-or$provenance.sourceCommit-cne$SourceCommit-or
       $provenance.version-cne$Version-or$provenance.sdk-cne'10.0.401'-or
       $provenance.baselineCommit-cne(Get-Content -LiteralPath (Join-Path $Repo 'compatibility/target.lock.json') -Raw|ConvertFrom-Json).source.commit){throw 'SDK provenance differs.'}
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
    # Sibling packages follow the release version; external packages keep their exact pinned versions.
    $siblings=@{
        'PiSharp.Contracts'=@();'PiSharp.Tui'=@()
        'PiSharp.AI'=@('PiSharp.Contracts');'PiSharp.Sessions'=@('PiSharp.Contracts');'PiSharp.ExtensionHost'=@('PiSharp.Contracts')
        'PiSharp.Extensions.Abstractions'=@('PiSharp.Contracts');'PiSharp.Extensions.Runtime'=@('PiSharp.Extensions.Abstractions')
        'PiSharp.Agent'=@('PiSharp.Contracts','PiSharp.AI');'PiSharp.Tools'=@('PiSharp.Agent','PiSharp.Contracts')
        'PiSharp.CodingAgent'=@('PiSharp.Agent','PiSharp.Sessions');'PiSharp.PromptTemplates.Yaml'=@('PiSharp.CodingAgent')
        'PiSharp.Rpc'=@('PiSharp.Contracts','PiSharp.CodingAgent','PiSharp.Extensions.Abstractions')
        'PiSharp.Extensions.Agent'=@('PiSharp.Agent','PiSharp.Extensions.Runtime')
        'PiSharp.Compatibility.Node'=@('PiSharp.Contracts','PiSharp.ExtensionHost','PiSharp.Extensions.Abstractions','PiSharp.Extensions.Runtime')
    }
    $external=@{}
    if($PackageId-ceq'PiSharp.PromptTemplates.Yaml'){$external['YamlDotNet']='16.3.0'}
    $expected=[Collections.Generic.HashSet[string]]::new([string[]]@($siblings[$PackageId]+@($external.Keys)),[StringComparer]::Ordinal)
    if($dependencies.Count-ne$expected.Count){throw 'SDK dependency identity/version differs.'}
    foreach($dependency in $dependencies){
        $id=$dependency.GetAttribute('id');$range=$dependency.GetAttribute('version')
        $allowed=if($external.ContainsKey($id)){@($external[$id],('['+$external[$id]+']'))}else{@($Version,('['+$Version+', )'),('['+$Version+']'))}
        if(-not$expected.Remove($id)-or$range-cnotin$allowed){throw 'SDK dependency identity/version differs.'}
    }
    return [pscustomobject]@{schemaVersion=1;packageId=$PackageId;version=$Version;sourceCommit=$SourceCommit;
        archiveSha256=(Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash.ToLowerInvariant();files=@($files.Values);
        packageConsumptionProven=$false;releaseAccepted=$false;nodeFreePayload=(-not$nodeBridge)}
}
