param(
 [Parameter(Mandatory=$true)][string]$Node,
 [Parameter(Mandatory=$true)][string]$Oracle,
 [Parameter(Mandatory=$true)][string]$Jiti,
 [Parameter(Mandatory=$true)][string]$ReferenceRepo,
 [Parameter(Mandatory=$true)][string]$HelloReferenceRepo,
 [Parameter(Mandatory=$true)][string]$RunParent,
 [string]$Repo=(Split-Path -Parent $PSScriptRoot),
 [string]$Filter
)
$ErrorActionPreference='Stop'
foreach($name in @('Node','Oracle','Jiti','ReferenceRepo','HelloReferenceRepo','Repo')){
 Set-Variable -Name $name -Value (Resolve-Path -LiteralPath (Get-Variable -Name $name -ValueOnly)).Path
}
if(![System.IO.Path]::IsPathFullyQualified($RunParent) -or (Test-Path -LiteralPath $RunParent)){throw 'Hello RunParent must be a fresh explicit absolute local path.'}
New-Item -ItemType Directory -Path $RunParent | Out-Null
$RunParent=(Resolve-Path -LiteralPath $RunParent).Path
$hostExecutable=(Get-Command dotnet -CommandType Application).Source
$artifacts=Join-Path $Repo 'artifacts'
$settings=@{
 DOTNET_CLI_HOME=(Join-Path $artifacts 'dotnet-cli'); NUGET_PACKAGES=(Join-Path $artifacts 'nuget')
 TEMP=(Join-Path $RunParent 'tmp'); TMP=(Join-Path $RunParent 'tmp'); TMPDIR=(Join-Path $RunParent 'tmp')
 APPDATA=(Join-Path $artifacts 'appdata'); LOCALAPPDATA=(Join-Path $artifacts 'localappdata')
 DOTNET_CLI_TELEMETRY_OPTOUT='1'; DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
 DOTNET_GENERATE_ASPNET_CERTIFICATE='false'; MSBuildEnableWorkloadResolver='false'
 MSBUILDDISABLENODEREUSE='1'; DOTNET_CLI_USE_MSBUILD_SERVER='0'; UseSharedCompilation='false'
}
$saved=@{}
try{
 foreach($name in @('dotnet-cli','nuget','appdata','localappdata')){New-Item -ItemType Directory -Force -Path (Join-Path $artifacts $name) | Out-Null}
 New-Item -ItemType Directory -Path (Join-Path $RunParent 'tmp') | Out-Null
 foreach($name in $settings.Keys){$saved[$name]=(Get-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue).Value;Set-Item -LiteralPath "Env:$name" -Value $settings[$name]}
 Push-Location -LiteralPath $Repo
 try{
  $project='tests/PiSharp.NodeHelloPackage.Tests/PiSharp.NodeHelloPackage.Tests.csproj'
  & $hostExecutable restore $project --configfile NuGet.Config --locked-mode --disable-parallel --verbosity minimal
  if($LASTEXITCODE -ne 0){throw 'Offline Hello process-test restore failed.'}
  & $hostExecutable build $project --no-restore --configuration Release --verbosity minimal
  if($LASTEXITCODE -ne 0){throw 'Hello process-test build failed.'}
  $cliProject='src/PiSharp.Cli/PiSharp.Cli.csproj'
  & $hostExecutable restore $cliProject --configfile NuGet.Config --locked-mode --disable-parallel --verbosity minimal
  if($LASTEXITCODE -ne 0){throw 'Offline Hello CLI restore failed.'}
  & $hostExecutable build $cliProject --no-restore --configuration Release --verbosity minimal
  if($LASTEXITCODE -ne 0){throw 'Hello CLI build failed.'}
  $fixture='fixtures/native-extensions/node-hello/PublishedFixture.NodeHello.csproj'
  $published=Join-Path $artifacts 'extensions/published-fixtures/node-hello'
  & $hostExecutable restore $fixture --configfile NuGet.Config --locked-mode --disable-parallel --verbosity minimal
  if($LASTEXITCODE -ne 0){throw 'Offline Hello fixture restore failed.'}
  & $hostExecutable publish $fixture --no-restore --configuration Release --output $published --verbosity minimal
  if($LASTEXITCODE -ne 0){throw 'Hello fixture publish failed.'}
  if((Test-Path -LiteralPath (Join-Path $published 'PiSharp.Contracts.dll')) -or (Test-Path -LiteralPath (Join-Path $published 'PiSharp.Extensions.Abstractions.dll'))){throw 'Shared ABI DLL copies remain in private Hello package.'}
  $arguments=@('tests/PiSharp.NodeHelloPackage.Tests/bin/Release/net10.0/PiSharp.NodeHelloPackage.Tests.dll',
   '--dotnet-host',$hostExecutable,'--cli',(Join-Path $Repo 'src/PiSharp.Cli/bin/Release/net10.0/PiSharp.Cli.dll'),
   '--published',$published,'--node',$Node,'--repo',$Repo,'--oracle',$Oracle,'--jiti',$Jiti,
   '--reference',$ReferenceRepo,'--hello-reference',$HelloReferenceRepo,'--run-parent',$RunParent,'--report',(Join-Path $RunParent 'process-results.json'))
  if($PSBoundParameters.ContainsKey('Filter')){if([string]::IsNullOrWhiteSpace($Filter)){throw 'An explicit Hello filter must be nonempty.'};$arguments+=@('--filter',$Filter)}
  & $hostExecutable @arguments
  if($LASTEXITCODE -ne 0){throw 'Actual Hello package process groups failed.'}
 }finally{Pop-Location}
}finally{
 foreach($name in $saved.Keys){if($null -eq $saved[$name]){Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue}else{Set-Item -LiteralPath "Env:$name" -Value $saved[$name]}}
}
