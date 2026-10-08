param([string]$Repo = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path -LiteralPath $Repo).Path
$dotnetExecutable = (Get-Command dotnet -CommandType Application).Source
$artifacts = Join-Path $Repo 'artifacts'
foreach ($name in @('dotnet-cli', 'nuget', 'tmp', 'appdata', 'localappdata', 'transports')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $artifacts $name) | Out-Null
}
$saved = @{}
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
try {
    foreach ($name in $settings.Keys) {
        $saved[$name] = (Get-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue).Value
        Set-Item -LiteralPath "Env:$name" -Value $settings[$name]
    }
    Push-Location -LiteralPath $Repo
    try {
        & $dotnetExecutable restore tests/PiSharp.Transport.Tests/PiSharp.Transport.Tests.csproj --configfile NuGet.Config --locked-mode --disable-parallel --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw 'Offline transport restore failed.' }
        & $dotnetExecutable build tests/PiSharp.Transport.Tests/PiSharp.Transport.Tests.csproj --no-restore --configuration Release --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw 'Transport build failed.' }
        $runtimePath = $env:PATH
        try {
            $env:PATH = Split-Path -Parent $dotnetExecutable
            & $dotnetExecutable tests/PiSharp.Transport.Tests/bin/Release/net10.0/PiSharp.Transport.Tests.dll --require-node-absent --report (Join-Path $artifacts 'transports/results.json')
            if ($LASTEXITCODE -ne 0) { throw 'Transport tests failed.' }
        } finally { $env:PATH = $runtimePath }
    } finally { Pop-Location }
} finally {
    foreach ($name in $saved.Keys) {
        if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item -LiteralPath "Env:$name" -Value $saved[$name] }
    }
}
