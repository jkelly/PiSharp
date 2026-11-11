# Synthetic archive controls only. Real package consumption is test-gap-package-consumer.ps1.
[CmdletBinding()]
param([string]$Repo = (Join-Path $PSScriptRoot '../..'))
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path -LiteralPath $Repo).Path
. (Join-Path $Repo 'tools/release/native-sdk-package.ps1')
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('pisharp-gap-package-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$version='1.5.0.77'; $commit='1111111111111111111111111111111111111111'
$results=[Collections.Generic.List[object]]::new()
$maps=@{
    'PiSharp.Chord'=@('PiSharp.Contracts','PiSharp.Telemetry','PiSharp.Tools')
    'PiSharp.Chord.Hosting'=@('PiSharp.Chord','PiSharp.Tools','PiSharp.Durable.Store','PiSharp.Tui')
    'PiSharp.Durable.Store'=@('PiSharp.Durable','PiSharp.Chord','PiSharp.Durable.Sqlite')
    'PiSharp.Env.Protocol'=@()
    'PiSharp.Env'=@('PiSharp.Env.Protocol','PiSharp.Durable')
}
function Fixture([string]$Id,[string]$Mutation='') {
    $dir=Join-Path $scratch ([Guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $dir | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $dir 'lib/net10.0'),(Join-Path $dir 'docs'),(Join-Path $dir '_rels') | Out-Null
    $assembly=Join-Path $dir "lib/net10.0/$Id.dll"; [IO.File]::WriteAllBytes($assembly,[byte[]](1,2,3))
    foreach($file in @('LICENSE','README.md','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $Repo $file) -Destination (Join-Path $dir $file) }
    if($Mutation-ne'missing-contract') { Copy-Item -LiteralPath (Join-Path $Repo 'docs/contracts/gap-package-admission.md') -Destination (Join-Path $dir 'docs/gap-package-admission.md') }
    [IO.File]::WriteAllText((Join-Path $dir '[Content_Types].xml'),'<Types />')
    [IO.File]::WriteAllText((Join-Path $dir '_rels/.rels'),'<Relationships />')
    New-Item -ItemType Directory -Path (Join-Path $dir 'package/services/metadata/core-properties') | Out-Null
    [IO.File]::WriteAllText((Join-Path $dir 'package/services/metadata/core-properties/nuget.psmdcp'),'<coreProperties />')
    $baseline=Get-Content -LiteralPath (Join-Path $Repo 'compatibility/target.lock.json') -Raw|ConvertFrom-Json
    @{schemaVersion=1;kind='native-sdk';sourceCommit=$commit;version=$version;sdk='10.0.401';baselineCommit=$baseline.source.commit}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $dir 'provenance.json')
    $dependencies=($maps[$Id]|Where-Object {$Mutation-ne'missing-tools'-or$_-cne'PiSharp.Tools'}|ForEach-Object {'<dependency id="'+$_+'" version="'+$(if($Mutation-eq'loose-dependency'){$version}else{'['+$version+']'})+'" />'}) -join ''
    $framework=if($Id-ceq'PiSharp.Chord.Hosting'){'<frameworkReferences><group targetFramework="net10.0"><frameworkReference name="Microsoft.AspNetCore.App" /></group></frameworkReferences>'}else{''}
    if($Mutation-eq'wrong-framework'){$framework=$framework.Replace('Microsoft.AspNetCore.App','Microsoft.WindowsDesktop.App')}
    $nuspec='<package><metadata><id>'+$Id+'</id><version>'+$version+'</version><license type="expression">MIT</license><readme>README.md</readme><repository type="git" commit="'+$commit+'"/><dependencies><group targetFramework="net10.0">'+$dependencies+'</group></dependencies>'+$framework+'</metadata></package>'
    [IO.File]::WriteAllText((Join-Path $dir "$Id.nuspec"),$nuspec)
    $package=$dir+'.nupkg'; [IO.Compression.ZipFile]::CreateFromDirectory($dir,$package)
    return @{Package=$package;PackageId=$Id;Version=$version;SourceCommit=$commit;Repo=$Repo;BuildAssembly=$assembly}
}
function Case([string]$Name,[scriptblock]$Body) {
    try { & $Body; $results.Add(@{name=$Name;status='passed'}) }
    catch { $results.Add(@{name=$Name;status='failed';error=$_.Exception.Message}) }
}
function Reject([hashtable]$Arguments) {
    $refused=$false
    try { Assert-PiSharpNativeSdkPackage @Arguments | Out-Null } catch { $refused=$true }
    if(!$refused){throw 'Invalid package was accepted.'}
}
foreach($id in $maps.Keys){Case "admit-$id" { $fixtureArgs=Fixture $id; $value=Assert-PiSharpNativeSdkPackage @fixtureArgs; if($value.packageConsumptionProven-or$value.releaseAccepted){throw 'Synthetic archive claimed runtime acceptance.'} }}
Case 'refuse-loose-sibling' { Reject (Fixture 'PiSharp.Env' 'loose-dependency') }
Case 'refuse-missing-chord-tools' { Reject (Fixture 'PiSharp.Chord' 'missing-tools') }
Case 'refuse-missing-hosting-tools' { Reject (Fixture 'PiSharp.Chord.Hosting' 'missing-tools') }
Case 'refuse-wrong-hosting-framework' { Reject (Fixture 'PiSharp.Chord.Hosting' 'wrong-framework') }
Case 'refuse-missing-admission-contract' { Reject (Fixture 'PiSharp.Chord' 'missing-contract') }
Case 'daemon-not-a-library' { $a=Fixture 'PiSharp.Env.Protocol'; $a.PackageId='PiSharp.Env.Daemon'; Reject $a }
$results|ConvertTo-Json -Depth 5
if(@($results|Where-Object status -eq 'failed').Count){exit 1}
