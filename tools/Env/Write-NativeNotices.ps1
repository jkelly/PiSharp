# Assemble distributable notices from the exact restored publish inputs. No
# downloads or license-text synthesis are performed by this helper.
param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [Parameter(Mandatory=$true)][string]$AssetsPath,
    [Parameter(Mandatory=$true)][string]$NuGetPackagingPath,
    [Parameter(Mandatory=$true)][ValidatePattern('^(win|linux|osx)-(x64|arm64)$')][string]$Rid,
    [Parameter(Mandatory=$true)][string]$BinaryName
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$package=[IO.Path]::GetFullPath($PackageDirectory)
$assetsFile=(Resolve-Path -LiteralPath $AssetsPath).Path
$assets=Get-Content -LiteralPath $assetsFile -Raw|ConvertFrom-Json -AsHashtable
$nugetAssembly=[Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $NuGetPackagingPath).Path)
$archiveReaderType=$nugetAssembly.GetType('NuGet.Packaging.PackageArchiveReader', $true)
$archiveReaderConstructor=$archiveReaderType.GetConstructors()|Where-Object {
    $parameters=$_.GetParameters()
    $parameters.Count -eq 3 -and $parameters[0].ParameterType -eq [string] -and
        $parameters[1].ParameterType.FullName -eq 'NuGet.Frameworks.IFrameworkNameProvider' -and
        $parameters[2].ParameterType.FullName -eq 'NuGet.Frameworks.IFrameworkCompatibilityProvider'
}|Select-Object -First 1
if(-not $archiveReaderConstructor){throw 'The selected SDK lacks the expected explicit PackageArchiveReader path constructor.'}
$targetKey='net10.0/'+$Rid
if(-not $assets.targets.ContainsKey($targetKey)){throw "Exact publish target $targetKey is missing from project.assets.json."}
if(-not (Test-Path -LiteralPath (Join-Path $package $BinaryName) -PathType Leaf)){throw 'The published binary must be staged before assembling notices.'}
if(Test-Path -LiteralPath (Join-Path $package 'native-package-manifest.json')){throw 'Refusing to overwrite an existing distribution manifest.'}
$files=[Collections.Generic.List[object]]::new()
$components=[Collections.Generic.List[object]]::new()
$projects=[Collections.Generic.List[object]]::new()
$selected=[ordered]@{}
function Record-File([string]$Relative,[string]$Role,[object]$Origin){
    $path=Join-Path $package $Relative
    $files.Add([ordered]@{path=$Relative.Replace('\','/');role=$Role;bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant();source=$Origin})
}
function Copy-Notice([string]$Source,[string]$Relative,[string]$Role,[object]$Origin){
    if(-not (Test-Path -LiteralPath $Source -PathType Leaf)){throw "Required distribution notice is missing: $Source"}
    $destination=Join-Path $package $Relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force|Out-Null
    Copy-Item -LiteralPath $Source -Destination $destination
    Record-File $Relative $Role $Origin
}
function Select-Package([string]$Id,[string]$Version,[string]$Role){
    if($selected.Contains($Id)){
        if($selected[$Id].version -ne $Version){throw "Conflicting selected versions for $Id"}
        return
    }
    $selected[$Id]=[ordered]@{id=$Id;version=$Version;role=$Role}
}
function Exact-Version([string]$Range){
    if($Range -match '^\[([^,\]]+)(?:,\s*([^\]]+))?\]$'){
        if($Matches[2] -and $Matches[1] -ne $Matches[2]){throw "Unresolved dependency version range: $Range"}
        return $Matches[1]
    }
    if($Range -match '^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$'){return $Range}
    throw "Unresolved dependency version: $Range"
}
foreach($entry in $assets.targets[$targetKey].GetEnumerator()){
    if($entry.Value.type -eq 'project'){
        $id,$version=$entry.Key -split '/',2
        $projects.Add([ordered]@{id=$id;version=$version;licenseFiles=@('LICENSE','THIRD-PARTY-NOTICES.md');coverage='restored project input; unused code may be trimmed'})
    }
    if($entry.Value.type -ne 'package'){continue}
    # Runtime/compile assets may survive AOT trimming. Keep notices for this
    # conservative superset rather than inventing a method-level linkage claim.
    $id,$version=$entry.Key -split '/',2
    if($entry.Value.ContainsKey('runtime') -or $entry.Value.ContainsKey('native') -or $entry.Value.ContainsKey('runtimeTargets') -or $entry.Value.ContainsKey('compile')){
        Select-Package $id $version 'restored-compile-or-runtime-dependency'
    }
}
$required=[ordered]@{
    "Microsoft.NETCore.App.Runtime.$Rid"='selected-dotnet-runtime';
    'Microsoft.DotNet.ILCompiler'='selected-nativeaot-build-package';
    "runtime.$Rid.Microsoft.DotNet.ILCompiler"='selected-nativeaot-compiler-and-runtime'
}
foreach($id in $required.Keys){
    $found=$false
    foreach($library in $assets.libraries.Keys){
        $candidate,$version=$library -split '/',2
        if($candidate -eq $id){Select-Package $id $version $required[$id];$found=$true}
    }
    foreach($framework in $assets.project.frameworks.Values){
        foreach($download in $framework.downloadDependencies){
            if($download.name -eq $id){Select-Package $id (Exact-Version $download.version) $required[$id];$found=$true}
        }
    }
    if(-not $found){throw "Cannot prove the selected version of required native publish input $id from the restore assets."}
    $selected[$id].role=$required[$id]
}
$repositoryNotices=@(
    @('LICENSE','LICENSE','license'),
    @('THIRD-PARTY-NOTICES.md','THIRD-PARTY-NOTICES.md','notice'),
    @('third-party/durable-pi.LICENSE.txt','licenses/repository/durable-pi.LICENSE.txt','license'),
    @('third-party/ripgrep-ignore.LICENSE-MIT.txt','licenses/repository/ripgrep-ignore.LICENSE-MIT.txt','license'),
    @('third-party/jsdiff.LICENSE.txt','licenses/repository/jsdiff.LICENSE.txt','license')
)
foreach($notice in $repositoryNotices){Copy-Notice (Join-Path $root $notice[0]) $notice[1] $notice[2] ([ordered]@{kind='repository';path=$notice[0]})}
$fallback=@{
    'Jint/4.16.3'=@('third-party/Jint.LICENSE.txt','third-party/Jint.CREDITS.txt');
    'Acornima/1.7.0'=@('third-party/Acornima.LICENSE.txt','third-party/Acornima.NOTICE.txt')
}
foreach($selection in $selected.Values){
    $id=$selection.id;$version=$selection.version;$key="$id/$version"
    $relative=$id.ToLowerInvariant()+'/'+$version.ToLowerInvariant()
    $directory=$null
    foreach($folder in $assets.packageFolders.Keys){$candidate=Join-Path $folder $relative;if(Test-Path -LiteralPath $candidate -PathType Container){$directory=[IO.Path]::GetFullPath($candidate);break}}
    if(-not $directory){throw "Restored package directory not found for $key"}
    $nuspec=Join-Path $directory ($id.ToLowerInvariant()+'.nuspec')
    [xml]$metadataXml=Get-Content -LiteralPath $nuspec -Raw
    $metadata=$metadataXml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    $actualId=$metadata.SelectSingleNode("*[local-name()='id']").InnerText
    $actualVersion=$metadata.SelectSingleNode("*[local-name()='version']").InnerText
    if($actualId -ne $id -or $actualVersion -ne $version){throw "NuGet metadata identity differs for $key"}
    $license=$metadata.SelectSingleNode("*[local-name()='license']")
    if(-not $license){throw "Package $key has no explicit license declaration; distribution review is required."}
    $repository=$metadata.SelectSingleNode("*[local-name()='repository']")
    $copyright=$metadata.SelectSingleNode("*[local-name()='copyright']")
    $component=[ordered]@{id=$actualId;version=$actualVersion;role=$selection.role;licenseType=$license.GetAttribute('type');license=$license.InnerText;
        copyright=$(if($copyright){$copyright.InnerText}else{$null});repositoryUrl=$(if($repository){$repository.GetAttribute('url')}else{$null});repositoryCommit=$(if($repository){$repository.GetAttribute('commit')}else{$null});notices=@()}
    $archive=Join-Path $directory ($id.ToLowerInvariant()+'.'+$version.ToLowerInvariant()+'.nupkg')
    if(-not (Test-Path -LiteralPath $archive)){throw "Restored package archive is missing for $key"}
    $archiveStream=[IO.File]::OpenRead($archive)
    try {$archiveSha512=[Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData($archiveStream))}finally{$archiveStream.Dispose()}
    # Allocate the exact argument vector and invoke the selected constructor;
    # PowerShell/Activator's optional-parameter binder is not used here.
    $readerArguments=[object[]]::new(3)
    $readerArguments[0]=[string]$archive
    $reader=$archiveReaderConstructor.Invoke($readerArguments)
    try {
        # Signed nupkgs have a canonical NuGet content hash distinct from the
        # byte hash of the signed ZIP. Use the selected SDK's package reader,
        # rather than treating those two representations as interchangeable.
        $contentHash=$reader.GetContentHash([Threading.CancellationToken]::None,[Func[string]]{$archiveSha512})
        $signed=$reader.IsSignedAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
        if($signed){
            $signature=$reader.GetPrimarySignatureAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
            $null=$reader.ValidateIntegrityAsync($signature.SignatureContent,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
        }
    } finally {$reader.Dispose()}
    $packageHash=Join-Path $directory ($id.ToLowerInvariant()+'.'+$version.ToLowerInvariant()+'.nupkg.sha512')
    if(-not (Test-Path -LiteralPath $packageHash)){throw "NuGet package integrity record is missing for $key"}
    $sidecarHash=(Get-Content -LiteralPath $packageHash -Raw).Trim()
    if($sidecarHash -cne $archiveSha512 -and $sidecarHash -cne $contentHash){throw "Cache hash sidecar does not match archive or NuGet content hash for $key"}
    $cache=Get-Content -LiteralPath (Join-Path $directory '.nupkg.metadata') -Raw|ConvertFrom-Json
    if(-not $cache.contentHash -or $cache.contentHash -cne $contentHash){throw "NuGet cache content hash differs from the selected archive for $key"}
    $library=$assets.libraries[$key]
    if($library -and $library.sha512 -and $library.sha512 -cne $contentHash){throw "Restore and selected archive NuGet content hashes differ for $key"}
    $component.integrity=[ordered]@{nugetContentHash=$contentHash;archiveSha512=$archiveSha512;signedPackage=$signed;
        signedPayloadIntegrityVerified=$signed;
        sidecarRepresentation=$(if($sidecarHash -ceq $archiveSha512){'archive-sha512'}else{'nuget-content-hash'});
        contentHashAlgorithm='Selected SDK NuGet.Packaging.PackageArchiveReader.GetContentHash';
        restoreContentHash=$(if($library){$library.sha512}else{$null});cacheContentHash=$cache.contentHash}
    $base='licenses/nuget/'+$relative+'/'
    Copy-Notice $nuspec ($base+[IO.Path]::GetFileName($nuspec)) 'package-metadata' ([ordered]@{kind='nuget';package=$actualId;version=$actualVersion;path=[IO.Path]::GetFileName($nuspec)})
    $notices=@(Get-ChildItem -LiteralPath $directory -Recurse -File|Where-Object {$_.Name -match '^(?:license|copying|notice|third[-_]?party|credits)' -and $_.Extension -notmatch '^\.(?:dll|exe|so|dylib|a|lib|pdb)$'})
    if($license.GetAttribute('type') -eq 'file'){
        $declared=[IO.Path]::GetFullPath((Join-Path $directory $license.InnerText))
        if(-not $declared.StartsWith($directory+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw "Invalid license path in $key"}
        if(-not (Test-Path -LiteralPath $declared -PathType Leaf)){throw "Declared license file is missing in $key"}
        $notices+=Get-Item -LiteralPath $declared
    }
    foreach($notice in ($notices|Sort-Object FullName -Unique)){
        $noticePath=[IO.Path]::GetRelativePath($directory,$notice.FullName).Replace('\','/')
        $destination=$base+$noticePath
        Copy-Notice $notice.FullName $destination 'notice' ([ordered]@{kind='nuget';package=$actualId;version=$actualVersion;path=$noticePath})
        $component.notices+=@($destination)
    }
    if($fallback.ContainsKey($key)){
        foreach($repoPath in $fallback[$key]){
            $destination=$base+[IO.Path]::GetFileName($repoPath)
            Copy-Notice (Join-Path $root $repoPath) $destination 'notice' ([ordered]@{kind='repository-vendored-package-notice';package=$actualId;version=$actualVersion;path=$repoPath})
            $component.notices+=@($destination)
        }
    }
    if($component.notices.Count -eq 0){throw "No distributable notice text found for $key; metadata/URLs alone are insufficient."}
    $components.Add($component)
}
$runtime=$components|Where-Object {$_.role -eq 'selected-dotnet-runtime'}|Select-Object -First 1
$runtimeLicense='licenses/nuget/'+$runtime.id.ToLowerInvariant()+'/'+$runtime.version.ToLowerInvariant()+'/LICENSE.TXT'
if(-not (Test-Path -LiteralPath (Join-Path $package $runtimeLicense))){throw 'The selected .NET runtime pack did not provide its full MIT license text.'}
foreach($component in $components){
    if($component.role -like 'selected-nativeaot-*'){
        if($component.license -ne 'MIT' -or $component.repositoryCommit -ne $runtime.repositoryCommit){throw 'NativeAOT and runtime license-text provenance must agree; review new package metadata before distributing.'}
        $component.sharedDotnetLicenseText=$runtimeLicense
    }
}
$bundle='licenses/NOTICE-BUNDLE.md'
@'
# Native daemon distribution notices

The executable is accompanied by PiSharp's license, repository adaptation notices,
and notices from the exact restored dependency/runtime/NativeAOT packages recorded
in native-package-manifest.json. Source documents are copied byte for byte.

The dependency inventory is a conservative restored compile/runtime superset.
NativeAOT may trim unused Jint/Acornima or other methods; this bundle does not claim
that every inventoried method or package survives into the native executable.

The selected .NET runtime provides the full MIT text. The NativeAOT packages declare
MIT, name the same dotnet/dotnet source commit, and supply their own complete third
party notices (including compiler/runtime contributions). Their package metadata
and copyright declarations are retained with the shared .NET MIT text.

The build's IDE, SDK executables, C/C++ compiler and Node test driver are not copied
into this distribution. System libraries loaded from the operating system remain
system dependencies; this bundle is not a license grant for operating-system or
IDE software. License bundling alone establishes no runtime/platform qualification.

The file inventory hashes all distribution files except the manifest itself.
'@|Set-Content -LiteralPath (Join-Path $package $bundle)
Record-File $bundle 'notice' ([ordered]@{kind='generated-scope-description';generator='tools/Env/Write-NativeNotices.ps1'})
Record-File $BinaryName 'executable' ([ordered]@{kind='nativeaot-publish';rid=$Rid})
$manifest=[ordered]@{schemaVersion=1;kind='native-daemon-distribution';rid=$Rid;createdUtc=[DateTime]::UtcNow.ToString('o');
    repositorySourceCommit=(& git -C $root rev-parse HEAD).Trim();
    repositoryDirty=(@(& git -C $root status --porcelain).Count -ne 0);
    restoreAssetsSha256=(Get-FileHash -LiteralPath $assetsFile -Algorithm SHA256).Hash.ToLowerInvariant();restoreTarget=$targetKey;
    generatorSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant();
    nugetPackagingAssembly=[ordered]@{version=$nugetAssembly.GetName().Version.ToString();sha256=(Get-FileHash -LiteralPath $nugetAssembly.Location -Algorithm SHA256).Hash.ToLowerInvariant()};
    componentCoverage='Conservative exact restored compile/runtime plus selected NativeAOT/runtime packages; not a method-level linked-code claim';
    runtimeQualification='Not established by this license and integrity manifest';components=$components.ToArray();projectInputs=$projects.ToArray();
    systemDependencies=$(if($Rid -like 'win-*'){@('Windows system DLLs; no operating-system DLL copied')}elseif($Rid -like 'osx-*'){@('macOS libSystem and system frameworks; no system library copied')}else{@('Host glibc/libutil and OS libraries; no system library copied')});
    files=@($files|Sort-Object path);selfHashExcluded='native-package-manifest.json'}
$manifest|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $package 'native-package-manifest.json')
Write-Output (Join-Path $package 'native-package-manifest.json')
