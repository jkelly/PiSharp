param([string]$Repo = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path -LiteralPath $Repo).Path
$artifacts = Join-Path $Repo 'artifacts'
$settings = @{
    DOTNET_CLI_HOME = (Join-Path $artifacts 'dotnet-cli')
    NUGET_PACKAGES = (Join-Path $artifacts 'nuget')
    TEMP = (Join-Path $artifacts 'tmp')
    TMP = (Join-Path $artifacts 'tmp')
    APPDATA = (Join-Path $artifacts 'appdata')
    LOCALAPPDATA = (Join-Path $artifacts 'localappdata')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    MSBuildEnableWorkloadResolver = 'false'
    MSBUILDDISABLENODEREUSE = '1'
    DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    UseSharedCompilation = 'false'
}
$saved = @{}
try {
    foreach ($name in @('dotnet-cli','nuget','tmp','appdata','localappdata')) { New-Item -ItemType Directory -Force -Path (Join-Path $artifacts $name) | Out-Null }
    foreach ($name in $settings.Keys) {
        $saved[$name] = (Get-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue).Value
        Set-Item -LiteralPath "Env:$name" -Value $settings[$name]
    }
    Push-Location -LiteralPath $Repo
    try {
        foreach ($fixture in @(@('PluginOne','one'),@('PluginTwo','two'),@('PluginFail','fail'),@('PluginFuture','future'),@('PluginCli','cli'),@('PluginCliUi','cli-ui'),@('PluginSystemImport','system-import'),@('PluginImage','image'))) {
            $project = 'tests/fixtures/extensions/native/' + $fixture[0] + '/' + $fixture[0] + '.csproj'
            $output = Join-Path $artifacts ('extensions/published-fixtures/' + $fixture[1])
            # These fixed directories contain only this repository's generated developer fixtures.
            & dotnet restore $project --configfile NuGet.Config --locked-mode --disable-parallel --verbosity minimal
            if ($LASTEXITCODE -ne 0) { throw ('Offline fixture restore failed: ' + $project) }
            & dotnet publish $project --no-restore --configuration Release --output $output --verbosity minimal
            if ($LASTEXITCODE -ne 0) { throw ('Owned fixture publish failed: ' + $project) }
        }
        $todoProject = 'samples/extensions/StatefulTodo/StatefulTodo.csproj'
        & dotnet restore $todoProject --configfile NuGet.Config --locked-mode --disable-parallel --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw 'Offline stateful sample restore failed.' }
        & dotnet publish $todoProject --no-restore --configuration Release --output (Join-Path $artifacts 'extensions/published-fixtures/todo') --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw 'Owned stateful sample publish failed.' }
        $checkpointProject = 'samples/extensions/SessionCheckpoint/SessionCheckpoint.csproj'
        & dotnet restore $checkpointProject --configfile NuGet.Config --locked-mode --disable-parallel --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw 'Offline checkpoint sample restore failed.' }
        & dotnet publish $checkpointProject --no-restore --configuration Release --output (Join-Path $artifacts 'extensions/published-fixtures/checkpoint') --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw 'Owned checkpoint sample publish failed.' }
    } finally { Pop-Location }
} finally {
    foreach ($name in $saved.Keys) {
        if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item -LiteralPath "Env:$name" -Value $saved[$name] }
    }
}
