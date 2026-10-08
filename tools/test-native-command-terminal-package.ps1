param(
 [Parameter(Mandatory=$true)][string]$Node,
 [Parameter(Mandatory=$true)][string]$Oracle,
 [Parameter(Mandatory=$true)][string]$Jiti,
 [Parameter(Mandatory=$true)][string]$ReferenceRepo,
 [Parameter(Mandatory=$true)][string]$CommandInputReferenceRepo,
 [Parameter(Mandatory=$true)][string]$RunParent,
 [string]$Repo=(Split-Path -Parent $PSScriptRoot),
 [string]$Filter
)
$ErrorActionPreference='Stop'
foreach($name in @('Node','Oracle','Jiti','ReferenceRepo','CommandInputReferenceRepo','Repo')){
 Set-Variable -Name $name -Value (Resolve-Path -LiteralPath (Get-Variable -Name $name -ValueOnly)).Path
}
if(![System.IO.Path]::IsPathFullyQualified($RunParent) -or (Test-Path -LiteralPath $RunParent)){throw 'Command terminal RunParent must be a fresh explicit absolute local path.'}
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
  foreach($project in @('tests/PiSharp.CodingAgent.Tests/PiSharp.CodingAgent.Tests.csproj','src/PiSharp.Cli/PiSharp.Cli.csproj')){
   & $hostExecutable restore $project --configfile NuGet.Config --locked-mode --disable-parallel --verbosity minimal
   if($LASTEXITCODE -ne 0){throw "Offline command terminal restore failed: $project"}
   & $hostExecutable build $project --no-restore --configuration Release --verbosity minimal
   if($LASTEXITCODE -ne 0){throw "Command terminal build failed: $project"}
  }
  $fixture='fixtures/native-extensions/node-command-input/PublishedFixture.NodeCommandInput.csproj'
  $published=Join-Path $artifacts 'extensions/published-fixtures/node-command-input'
  & $hostExecutable restore $fixture --configfile NuGet.Config --locked-mode --disable-parallel --verbosity minimal
  if($LASTEXITCODE -ne 0){throw 'Offline command terminal fixture restore failed.'}
  & $hostExecutable publish $fixture --no-restore --configuration Release --output $published --verbosity minimal
  if($LASTEXITCODE -ne 0){throw 'Command terminal fixture publish failed.'}
  if((Test-Path -LiteralPath (Join-Path $published 'PiSharp.Contracts.dll')) -or (Test-Path -LiteralPath (Join-Path $published 'PiSharp.Extensions.Abstractions.dll'))){throw 'Shared ABI DLL copies remain in private Commands/Input package.'}
  $arguments=@('tests/PiSharp.CodingAgent.Tests/bin/Release/net10.0/PiSharp.CodingAgent.Tests.dll','--command-terminal-package',
   '--dotnet-host',$hostExecutable,'--cli',(Join-Path $Repo 'src/PiSharp.Cli/bin/Release/net10.0/PiSharp.Cli.dll'),
   '--published',$published,'--node',$Node,'--repo',$Repo,'--oracle',$Oracle,'--jiti',$Jiti,
   '--reference',$ReferenceRepo,'--command-input-reference',$CommandInputReferenceRepo,'--run-parent',$RunParent,'--report',(Join-Path $RunParent 'process-results.json'))
  if($PSBoundParameters.ContainsKey('Filter')){if([string]::IsNullOrWhiteSpace($Filter)){throw 'An explicit command terminal filter must be nonempty.'};$arguments+=@('--filter',$Filter)}
  & $hostExecutable @arguments
  if($LASTEXITCODE -ne 0){throw 'Actual command terminal process groups failed.'}
 }finally{Pop-Location}
}finally{
 foreach($name in $saved.Keys){if($null -eq $saved[$name]){Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue}else{Set-Item -LiteralPath "Env:$name" -Value $saved[$name]}}
}
