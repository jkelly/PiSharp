param(
    [string]$OutputDirectory='artifacts/env-native',
    [string]$DotnetPath='dotnet',
    [string]$SourceSha
)
$ErrorActionPreference='Stop'
$env:DOTNET_CLI_UI_LANGUAGE='en-US'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if(Test-Path -LiteralPath $output){
    if(Get-ChildItem -LiteralPath $output -Force|Select-Object -First 1){throw 'Use an empty output directory to prevent stale native package evidence.'}
}
$evidence=Join-Path $output 'evidence'
$publish=Join-Path $output 'publish'
$package=Join-Path $output 'package'
New-Item -ItemType Directory -Path $evidence,$publish,$package -Force|Out-Null
$manifest=[ordered]@{schemaVersion=1;kind='env-native-package';result='failed';startedUtc=[DateTime]::UtcNow.ToString('o');stages=@();rid=$null;checkoutSha=$null;binary=$null}
$stages=[Collections.Generic.List[object]]::new()
function Invoke-Stage([string]$Name,[string]$Executable,[string[]]$Arguments){
    $log=Join-Path $evidence ($Name+'.log')
    $started=[DateTime]::UtcNow.ToString('o')
    & $Executable @Arguments *> $log
    $code=$LASTEXITCODE
    $stages.Add([ordered]@{name=$Name;exitCode=$code;result=$(if($code -eq 0){'passed'}else{'failed'});startedUtc=$started;finishedUtc=[DateTime]::UtcNow.ToString('o');log=[IO.Path]::GetFileName($log)})
    if($code -ne 0){Get-Content -LiteralPath $log -Tail 60;throw "$Name failed with exit code $code"}
}
Push-Location $root
try {
    $sha=(& git rev-parse HEAD).Trim()
    if($LASTEXITCODE -ne 0){throw 'Could not resolve checkout SHA.'}
    if($SourceSha -and ($SourceSha -notmatch '^[0-9a-fA-F]{40}$' -or $SourceSha -ne $sha)){throw 'Native validation must execute the exact requested immutable source SHA.'}
    $manifest.checkoutSha=$sha
    $manifest.checkoutTree=(& git rev-parse 'HEAD^{tree}').Trim()
    $manifest.trackedChanges=@(& git diff HEAD --name-only)
    $info=@(& $DotnetPath --info)
    if($LASTEXITCODE -ne 0){throw 'dotnet --info failed.'}
    $info|Set-Content -LiteralPath (Join-Path $evidence 'dotnet-info.log')
    $match=[regex]::Match(($info -join "`n"),'(?m)^\s*RID:\s*((?:win|linux|osx)-(?:x64|arm64))\s*$')
    if(-not $match.Success){throw 'The installed SDK did not report a supported native host RID.'}
    $rid=$match.Groups[1].Value
    $sdkBase=[regex]::Match(($info -join "`n"),'(?m)^\s*Base Path:\s*(.+?)\s*$')
    if(-not $sdkBase.Success){throw 'Cannot locate the selected SDK package reader for NuGet content-hash verification.'}
    $nugetPackaging=Join-Path $sdkBase.Groups[1].Value 'NuGet.Packaging.dll'
    if(-not (Test-Path -LiteralPath $nugetPackaging)){throw 'The selected SDK does not contain NuGet.Packaging.dll.'}
    $platform=if($IsWindows){'win'}elseif($IsMacOS){'osx'}elseif($IsLinux){'linux'}else{throw 'Unsupported native host.'}
    $architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if($rid -ne "$platform-$architecture"){throw "SDK RID $rid does not match native OS architecture $platform-$architecture; cross-compilation is not configured."}
    $manifest.rid=$rid
    $manifest.host=[ordered]@{os=[Runtime.InteropServices.RuntimeInformation]::OSDescription;architecture=$architecture;runnerOs=$env:RUNNER_OS;runnerArch=$env:RUNNER_ARCH;imageOS=$env:ImageOS;imageVersion=$env:ImageVersion}
    $nodeVersion=@(& node --version); if($LASTEXITCODE -ne 0){throw 'Node.js is required for the independent framed-protocol smoke.'}
    $manifest.nodeVersion=$nodeVersion -join ''
    $prerequisites=[Collections.Generic.List[string]]::new()
    if($IsWindows){
        $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        $installation=$null
        if(Test-Path -LiteralPath $vswhere){
            $installation=(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath|Select-Object -First 1)
        }
        if(-not $installation){
            # Managed developer images can retain the C++ workload without the
            # installer helper. Validate the same on-disk workload in that case.
            $candidate=Get-ChildItem -Path (Join-Path $env:ProgramFiles 'Microsoft Visual Studio/*/*/VC/Auxiliary/Build/vcvars64.bat') -File -ErrorAction SilentlyContinue|Select-Object -Last 1
            if($candidate){$installation=[IO.Path]::GetFullPath((Join-Path $candidate.Directory.FullName '../../..'))}
        }
        if(-not $installation){throw 'The Visual Studio x64 C++ tools are not installed.'}
        $vcvars=Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
        if(-not (Test-Path -LiteralPath $vcvars)){throw 'Visual Studio x64 C++ environment is incomplete.'}
        $linker=Get-ChildItem -Path (Join-Path $installation 'VC/Tools/MSVC/*/bin/Hostx64/x64/link.exe') -File -ErrorAction SilentlyContinue|Select-Object -Last 1
        if(-not $linker){throw 'The Visual Studio x64 linker is not installed.'}
        $sdkRoot=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/Lib'
        $sdk=@(Get-ChildItem -LiteralPath $sdkRoot -Directory -ErrorAction SilentlyContinue|Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName 'um/x64/kernel32.lib')})
        if($sdk.Count -eq 0){throw 'NativeAOT requires a Windows SDK with x64 import libraries.'}
        $prerequisites.Add("Visual Studio C++: $installation")
        $prerequisites.Add("Windows SDK: $($sdk[-1].Name)")
    } else {
        foreach($tool in @('cc','clang','ar')){
            $command=Get-Command $tool -CommandType Application -ErrorAction Stop
            $prerequisites.Add("${tool}: $($command.Source)")
        }
        if($IsMacOS){
            $developer=(& xcode-select -p); if($LASTEXITCODE -ne 0){throw 'Xcode Command Line Tools are required.'}
            $prerequisites.Add("Xcode tools: $developer")
        }
    }
    $prerequisites|Set-Content -LiteralPath (Join-Path $evidence 'prerequisites.log')
    Invoke-Stage 'publish' $DotnetPath @('publish','src/PiSharp.Env.Daemon/PiSharp.Env.Daemon.csproj','-c','Release','-r',$rid,'--self-contained','true','-o',$publish,'--disable-build-servers','-m:1',
        '-p:PiSharpEnvNativePublish=true','-p:StripSymbols=true','-p:DebugType=None','-p:DebugSymbols=false','-p:RestoreLockedMode=false','-p:RestoreSources=https://api.nuget.org/v3/index.json',
        '-p:UseSharedCompilation=false','-p:ILLinkTreatWarningsAsErrors=true','-p:IlcTreatWarningsAsErrors=true','-p:TrimmerSingleWarn=false')
    $file=if($IsWindows){'PiSharp.Env.Daemon.exe'}else{'PiSharp.Env.Daemon'}
    $source=Join-Path $publish $file
    if(-not (Test-Path -LiteralPath $source -PathType Leaf)){throw 'Native publish did not produce the daemon executable.'}
    $binary=Join-Path $package $file
    Copy-Item -LiteralPath $source -Destination $binary
    if(-not $IsWindows){& chmod 755 $binary;if($LASTEXITCODE -ne 0){throw 'Cannot set package executable permission.'}}
    $manifest.binary=[ordered]@{file=$file;bytes=(Get-Item -LiteralPath $binary).Length;sha256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()}
    $noticeStarted=[DateTime]::UtcNow.ToString('o')
    try {
        & (Join-Path $PSScriptRoot 'Write-NativeNotices.ps1') -PackageDirectory $package -AssetsPath (Join-Path $root 'src/PiSharp.Env.Daemon/obj/project.assets.json') -NuGetPackagingPath $nugetPackaging -Rid $rid -BinaryName $file *> (Join-Path $evidence 'license-bundle.log')
        $stages.Add([ordered]@{name='license-bundle';result='passed';startedUtc=$noticeStarted;finishedUtc=[DateTime]::UtcNow.ToString('o');log='license-bundle.log'})
    } catch {
        $stages.Add([ordered]@{name='license-bundle';result='failed';startedUtc=$noticeStarted;finishedUtc=[DateTime]::UtcNow.ToString('o');log='license-bundle.log'})
        throw
    }
    $manifest.distributionManifest=[ordered]@{file='native-package-manifest.json';sha256=(Get-FileHash -LiteralPath (Join-Path $package 'native-package-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()}
    Invoke-Stage 'protocol-smoke' 'node' @('tools/Env/native-smoke.mjs','--daemon',$binary,'--rid',$rid,'--work',(Join-Path $output 'smoke-work'),'--report',(Join-Path $evidence 'protocol-smoke.json'))
    $smoke=Get-Content -LiteralPath (Join-Path $evidence 'protocol-smoke.json') -Raw|ConvertFrom-Json
    if($smoke.result -ne 'passed' -or $smoke.binary.sha256 -ne $manifest.binary.sha256 -or $smoke.distribution.manifestSha256 -ne $manifest.distributionManifest.sha256){throw 'Protocol smoke must pass against the exact packaged binary and notices manifest.'}
    if(-not $IsWindows){
        Invoke-Stage 'published-terminal-suite' $DotnetPath @('run','--project','tests/PiSharp.Env.Terminal.Tests/PiSharp.Env.Terminal.Tests.csproj','-c','Release','--disable-build-servers',
            '-p:RestoreLockedMode=false','-p:RestoreSources=https://api.nuget.org/v3/index.json','-p:UseSharedCompilation=false','--','--external-daemon',$binary)
        $terminalLog=Get-Content -LiteralPath (Join-Path $evidence 'published-terminal-suite.log') -Raw
        $nativeCases=@('native-terminal-input-resize-environment-and-exit','native-terminal-binary-output','native-terminal-drains-output-through-slow-consumer',
            'native-terminal-foreground-job-and-close','native-terminal-ordered-input-and-missing-executable','native-terminal-default-login-shell-and-env-clear',
            'native-terminal-kill-versus-later-abort','native-terminal-disconnect-kills-session-leader')
        foreach($case in $nativeCases){if($terminalLog -notmatch ('(?m)^PASS '+[regex]::Escape($case)+'\r?$')){throw "Published terminal suite did not execute required case $case"}}
        $manifest.nativeTerminalCases=$nativeCases
    } else {
        $stages.Add([ordered]@{name='published-terminal-suite';result='not-applicable';reason='Windows PTY is ENOTSUP; the published-binary protocol smoke asserts that contract.'})
    }
    $manifest.result='passed'
} catch {
    $manifest.error=$_.Exception.Message
    throw
} finally {
    $manifest.stages=$stages.ToArray()
    $manifest.finishedUtc=[DateTime]::UtcNow.ToString('o')
    $manifest|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $evidence 'manifest.json')
    Pop-Location
}
