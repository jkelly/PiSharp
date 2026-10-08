param([Parameter(Mandatory=$true)][string]$Node,[string]$Repo=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
$Repo=(Resolve-Path -LiteralPath $Repo).Path
$Node=(Resolve-Path -LiteralPath $Node).Path
$dotnetExecutable=(Get-Command dotnet -CommandType Application).Source
$artifacts=Join-Path $Repo 'artifacts'
$processTemp=Join-Path (Split-Path -Parent $Repo) 'PiSharp-node-worker-process-tmp'
$settings=@{
 DOTNET_CLI_HOME=(Join-Path $artifacts 'dotnet-cli');NUGET_PACKAGES=(Join-Path $artifacts 'nuget')
 TEMP=$processTemp;TMP=$processTemp;TMPDIR=$processTemp
 APPDATA=(Join-Path $artifacts 'appdata');LOCALAPPDATA=(Join-Path $artifacts 'localappdata')
 DOTNET_CLI_TELEMETRY_OPTOUT='1';DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
 DOTNET_GENERATE_ASPNET_CERTIFICATE='false';MSBuildEnableWorkloadResolver='false'
 MSBUILDDISABLENODEREUSE='1';DOTNET_CLI_USE_MSBUILD_SERVER='0';UseSharedCompilation='false'
}
$saved=@{}
try{
 foreach($name in @('dotnet-cli','nuget','tmp','appdata','localappdata','node-worker')){New-Item -ItemType Directory -Force -Path (Join-Path $artifacts $name)|Out-Null}
 New-Item -ItemType Directory -Force -Path $processTemp|Out-Null
 foreach($name in $settings.Keys){$saved[$name]=(Get-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue).Value;Set-Item -LiteralPath "Env:$name" -Value $settings[$name]}
 Push-Location -LiteralPath $Repo
 try{
  $project='tests/PiSharp.ExtensionHost.ProcessTests/PiSharp.ExtensionHost.ProcessTests.csproj'
  & $dotnetExecutable restore $project --configfile NuGet.Config --locked-mode --disable-parallel --verbosity minimal
  if($LASTEXITCODE -ne 0){throw 'Offline optional process-test restore failed.'}
  & $dotnetExecutable build $project --no-restore --configuration Release --verbosity minimal
  if($LASTEXITCODE -ne 0){throw 'Optional Node supervisor build failed.'}
  & $dotnetExecutable 'tests/PiSharp.ExtensionHost.ProcessTests/bin/Release/net10.0/PiSharp.ExtensionHost.ProcessTests.dll' --dotnet-host $dotnetExecutable --node $Node --repo $Repo --report (Join-Path $artifacts 'node-worker/process-results.json')
  if($LASTEXITCODE -ne 0){throw 'Actual optional Node process groups failed.'}
 }finally{Pop-Location}
}finally{
 foreach($name in $saved.Keys){if($null -eq $saved[$name]){Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue}else{Set-Item -LiteralPath "Env:$name" -Value $saved[$name]}}
}
