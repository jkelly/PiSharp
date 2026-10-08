param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$SourceManifest,
    [string]$SourceManifestSha256,
    [string]$PermittedBuildReceipt,
    [string]$PermittedBuildReceiptSha256
)
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path -LiteralPath $Repo).Path
. (Join-Path $PSScriptRoot 'native-companion-registration.ps1')
$companionRegistration = Get-NativeCompanionRegistration -Repo $Repo -RequireLockFiles
$preparedValidation = Get-NativePreparedValidation -Repo $Repo -SourceManifest $SourceManifest -SourceManifestSha256 $SourceManifestSha256 -BuildReceipt $PermittedBuildReceipt -BuildReceiptSha256 $PermittedBuildReceiptSha256
$dotnetExecutable = Get-NativeLaunchHost -PreparedValidation $preparedValidation
$artifacts = Join-Path $Repo 'artifacts'
foreach ($name in @('dotnet-cli', 'nuget', 'tmp', 'appdata', 'localappdata', 'native', 'transports')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $artifacts $name) | Out-Null
}
$launchEvidenceDirectory = Join-Path $artifacts ('native-launch-pins-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $launchEvidenceDirectory | Out-Null
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
        # Validate the externally prepared and pinned products; do not rebuild or
        # publish here because that would replace the receipt's signed bytes.
        $runtimePath = $env:PATH
        try {
            $env:PATH = Split-Path -Parent $dotnetExecutable
            if (Get-Command node -CommandType Application -ErrorAction SilentlyContinue) { throw 'Node is present on the native smoke-test PATH.' }
            Assert-NativeLaunchBoundary -Repo $Repo -PreparedValidation $preparedValidation -DotnetExecutable $dotnetExecutable -EntryAssembly 'tests/PiSharp.Compatibility.Tests/bin/Release/net10.0/PiSharp.Compatibility.Tests.dll' -EvidenceFile (Join-Path $launchEvidenceDirectory 'core-01-compatibility.json') | Out-Null
            & $dotnetExecutable tests/PiSharp.Compatibility.Tests/bin/Release/net10.0/PiSharp.Compatibility.Tests.dll --repo $Repo --require-node-absent --report (Join-Path $artifacts 'native/results.json')
            if ($LASTEXITCODE -ne 0) { throw 'Native contract tests failed.' }
            Assert-NativeLaunchBoundary -Repo $Repo -PreparedValidation $preparedValidation -DotnetExecutable $dotnetExecutable -EntryAssembly 'tests/PiSharp.Agent.Tests/bin/Release/net10.0/PiSharp.Agent.Tests.dll' -EvidenceFile (Join-Path $launchEvidenceDirectory 'core-02-agent.json') | Out-Null
            & $dotnetExecutable tests/PiSharp.Agent.Tests/bin/Release/net10.0/PiSharp.Agent.Tests.dll --report (Join-Path $artifacts 'native/agent-results.json')
            if ($LASTEXITCODE -ne 0) { throw 'Agent ordering prototype tests failed.' }
            Assert-NativeLaunchBoundary -Repo $Repo -PreparedValidation $preparedValidation -DotnetExecutable $dotnetExecutable -EntryAssembly 'tests/PiSharp.Transport.Tests/bin/Release/net10.0/PiSharp.Transport.Tests.dll' -EvidenceFile (Join-Path $launchEvidenceDirectory 'core-03-transport.json') | Out-Null
            & $dotnetExecutable tests/PiSharp.Transport.Tests/bin/Release/net10.0/PiSharp.Transport.Tests.dll --require-node-absent --report (Join-Path $artifacts 'transports/results.json')
            if ($LASTEXITCODE -ne 0) { throw 'SSE transport tests failed.' }
            Assert-NativeLaunchBoundary -Repo $Repo -PreparedValidation $preparedValidation -DotnetExecutable $dotnetExecutable -EntryAssembly 'tests/PiSharp.Sessions.Tests/bin/Release/net10.0/PiSharp.Sessions.Tests.dll' -EvidenceFile (Join-Path $launchEvidenceDirectory 'core-04-sessions.json') | Out-Null
            & $dotnetExecutable tests/PiSharp.Sessions.Tests/bin/Release/net10.0/PiSharp.Sessions.Tests.dll --report (Join-Path $artifacts 'native/session-results.json')
            if ($LASTEXITCODE -ne 0) { throw 'Session codec tests failed.' }
            Assert-NativeLaunchBoundary -Repo $Repo -PreparedValidation $preparedValidation -DotnetExecutable $dotnetExecutable -EntryAssembly 'tests/PiSharp.Tools.Tests/bin/Release/net10.0/PiSharp.Tools.Tests.dll' -EvidenceFile (Join-Path $launchEvidenceDirectory 'core-05-tools.json') | Out-Null
            & $dotnetExecutable tests/PiSharp.Tools.Tests/bin/Release/net10.0/PiSharp.Tools.Tests.dll --report (Join-Path $artifacts 'native/tools-results.json')
            if ($LASTEXITCODE -ne 0) { throw 'Native filesystem tool tests failed.' }
            Assert-NativeLaunchBoundary -Repo $Repo -PreparedValidation $preparedValidation -DotnetExecutable $dotnetExecutable -EntryAssembly 'tests/PiSharp.CodingAgent.Tests/bin/Release/net10.0/PiSharp.CodingAgent.Tests.dll' -EvidenceFile (Join-Path $launchEvidenceDirectory 'core-06-codingagent.json') | Out-Null
            & $dotnetExecutable tests/PiSharp.CodingAgent.Tests/bin/Release/net10.0/PiSharp.CodingAgent.Tests.dll --dotnet-host $dotnetExecutable --cli (Join-Path $Repo 'src/PiSharp.Cli/bin/Release/net10.0/PiSharp.Cli.dll') --report (Join-Path $artifacts 'native/codingagent-results.json')
            if ($LASTEXITCODE -ne 0) { throw 'Durable CodingAgent integration tests failed.' }
            Assert-NativeLaunchBoundary -Repo $Repo -PreparedValidation $preparedValidation -DotnetExecutable $dotnetExecutable -EntryAssembly 'tests/PiSharp.Rpc.Tests/bin/Release/net10.0/PiSharp.Rpc.Tests.dll' -EvidenceFile (Join-Path $launchEvidenceDirectory 'core-07-rpc.json') | Out-Null
            & $dotnetExecutable tests/PiSharp.Rpc.Tests/bin/Release/net10.0/PiSharp.Rpc.Tests.dll --report (Join-Path $artifacts 'native/rpc-results.json')
            if ($LASTEXITCODE -ne 0) { throw 'Native JSONL RPC transport tests failed.' }
            Assert-NativeLaunchBoundary -Repo $Repo -PreparedValidation $preparedValidation -DotnetExecutable $dotnetExecutable -EntryAssembly 'tests/PiSharp.Extensions.ContractTests/bin/Release/net10.0/PiSharp.Extensions.ContractTests.dll' -EvidenceFile (Join-Path $launchEvidenceDirectory 'core-08-extensions.json') | Out-Null
            & $dotnetExecutable tests/PiSharp.Extensions.ContractTests/bin/Release/net10.0/PiSharp.Extensions.ContractTests.dll --report (Join-Path $artifacts 'native/extension-results.json')
            if ($LASTEXITCODE -ne 0) { throw 'Native extension registration contract tests failed.' }
            Assert-NativeLaunchBoundary -Repo $Repo -PreparedValidation $preparedValidation -DotnetExecutable $dotnetExecutable -EntryAssembly 'tests/PiSharp.ExtensionHost.Tests/bin/Release/net10.0/PiSharp.ExtensionHost.Tests.dll' -EvidenceFile (Join-Path $launchEvidenceDirectory 'core-09-extensionhost.json') | Out-Null
            & $dotnetExecutable tests/PiSharp.ExtensionHost.Tests/bin/Release/net10.0/PiSharp.ExtensionHost.Tests.dll --report (Join-Path $artifacts 'native/worker-protocol-results.json')
            if ($LASTEXITCODE -ne 0) { throw 'Native optional worker protocol tests failed.' }
            Assert-NativeLaunchBoundary -Repo $Repo -PreparedValidation $preparedValidation -DotnetExecutable $dotnetExecutable -EntryAssembly 'tests/PiSharp.Tui.Tests/bin/Release/net10.0/PiSharp.Tui.Tests.dll' -EvidenceFile (Join-Path $launchEvidenceDirectory 'core-10-tui.json') | Out-Null
            & $dotnetExecutable tests/PiSharp.Tui.Tests/bin/Release/net10.0/PiSharp.Tui.Tests.dll --dotnet-host $dotnetExecutable --report (Join-Path $artifacts 'native/terminal-results.json')
            if ($LASTEXITCODE -ne 0) { throw 'Native terminal input and console ownership tests failed.' }
            Invoke-NativeCompanionTargets -Repo $Repo -DotnetExecutable $dotnetExecutable -PreparedValidation $preparedValidation -EvidenceDirectory (Join-Path $artifacts ('native-companions-' + [Guid]::NewGuid().ToString('N')))
        } finally { $env:PATH = $runtimePath }
    } finally { Pop-Location }
} finally {
    foreach ($name in $saved.Keys) {
        if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item -LiteralPath "Env:$name" -Value $saved[$name] }
    }
}
