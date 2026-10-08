param(
    [Parameter(Mandatory=$true)][string]$Node,
    [Parameter(Mandatory=$true)][string]$Oracle,
    [Parameter(Mandatory=$true)][string]$Jiti,
    [Parameter(Mandatory=$true)][string]$ReferenceRepo,
    [Parameter(Mandatory=$true)][string]$RunParent,
    [string]$Repo=(Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference='Stop'
foreach($parameterName in @('Node','Oracle','Jiti','ReferenceRepo','Repo')){
    Set-Variable -Name $parameterName -Value (Resolve-Path -LiteralPath (Get-Variable -Name $parameterName -ValueOnly)).Path
}
if($RunParent -notmatch '^[A-Za-z]:[\\/]'){throw 'RunParent must be an explicit absolute local directory.'}
New-Item -ItemType Directory -Force -Path $RunParent | Out-Null
$RunParent=(Resolve-Path -LiteralPath $RunParent).Path
$dotnetExecutable=(Get-Command dotnet -CommandType Application).Source
$artifacts=Join-Path $Repo 'artifacts'
$settings=@{
    DOTNET_CLI_HOME=(Join-Path $artifacts 'dotnet-cli');NUGET_PACKAGES=(Join-Path $artifacts 'nuget')
    TEMP=(Join-Path $RunParent 'tmp');TMP=(Join-Path $RunParent 'tmp');TMPDIR=(Join-Path $RunParent 'tmp')
    APPDATA=(Join-Path $artifacts 'appdata');LOCALAPPDATA=(Join-Path $artifacts 'localappdata')
    DOTNET_CLI_TELEMETRY_OPTOUT='1';DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
    DOTNET_GENERATE_ASPNET_CERTIFICATE='false';MSBuildEnableWorkloadResolver='false'
    MSBUILDDISABLENODEREUSE='1';DOTNET_CLI_USE_MSBUILD_SERVER='0';UseSharedCompilation='false'
}
$saved=@{}
try{
    foreach($name in @('dotnet-cli','nuget','appdata','localappdata','node-package')){
        New-Item -ItemType Directory -Force -Path (Join-Path $artifacts $name) | Out-Null
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $RunParent 'tmp') | Out-Null
    foreach($name in $settings.Keys){
        $saved[$name]=(Get-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue).Value
        Set-Item -LiteralPath "Env:$name" -Value $settings[$name]
    }
    Push-Location -LiteralPath $Repo
    try{
        $project='tests/PiSharp.NodePackage.Tests/PiSharp.NodePackage.Tests.csproj'
        & $dotnetExecutable restore $project --configfile NuGet.Config --locked-mode --disable-parallel --verbosity minimal
        if($LASTEXITCODE -ne 0){throw 'Offline protected-paths process-test restore failed.'}
        & $dotnetExecutable build $project --no-restore --configuration Release --verbosity minimal
        if($LASTEXITCODE -ne 0){throw 'Node package process-test build failed.'}
        $fixture='fixtures/native-extensions/node-protected-paths/PublishedFixture.NodeProtectedPaths.csproj'
        $published=Join-Path $artifacts 'extensions/published-fixtures/node-protected-paths'
        & $dotnetExecutable restore $fixture --configfile NuGet.Config --locked-mode --disable-parallel --verbosity minimal
        if($LASTEXITCODE -ne 0){throw 'Offline hook-only fixture restore failed.'}
        & $dotnetExecutable publish $fixture --no-restore --configuration Release --output $published --verbosity minimal
        if($LASTEXITCODE -ne 0){throw 'Hook-only native package publish failed.'}
        if((Test-Path -LiteralPath (Join-Path $published 'PiSharp.Contracts.dll')) -or (Test-Path -LiteralPath (Join-Path $published 'PiSharp.Extensions.Abstractions.dll'))){throw 'Shared ABI DLL copies remain in private package.'}
        & $dotnetExecutable 'tests/PiSharp.NodePackage.Tests/bin/Release/net10.0/PiSharp.NodePackage.Tests.dll' --dotnet-host $dotnetExecutable --cli (Join-Path $Repo 'src/PiSharp.Cli/bin/Release/net10.0/PiSharp.Cli.dll') --published $published --node $Node --repo $Repo --oracle $Oracle --jiti $Jiti --reference $ReferenceRepo --run-parent $RunParent --report (Join-Path $artifacts 'node-package/process-results.json')
        if($LASTEXITCODE -ne 0){throw 'Actual hook-only native package process groups failed.'}
    }finally{Pop-Location}
}finally{
    foreach($name in $saved.Keys){
        if($null -eq $saved[$name]){Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue}
        else{Set-Item -LiteralPath "Env:$name" -Value $saved[$name]}
    }
}
