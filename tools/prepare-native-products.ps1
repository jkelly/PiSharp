param(
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][string]$SourceManifest,
    [Parameter(Mandatory)][string]$SourceManifestSha256,
    [Parameter(Mandatory)][string]$ApprovedRuntimeAllocation,
    [Parameter(Mandatory)][string]$ApprovedRuntimeAllocationSha256,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [string]$NuGetConfig,
    [string]$NuGetConfigSha256
)
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path -LiteralPath $Repo).Path
. (Join-Path $Repo 'tools/native-companion-registration.ps1')
$manifestPin = Read-NativePinnedJson -Path $SourceManifest -Sha256 $SourceManifestSha256
$source = $manifestPin.document
if ($source.schemaVersion -ne 1 -or $source.candidate -cnotmatch '^[0-9a-f]{40}$' -or $source.tree -cnotmatch '^[0-9a-f]{40}$' -or
    -not $source.files.Count -or [string]::IsNullOrWhiteSpace($source.repository)) { throw 'Invalid immutable source candidate.' }
$allocationPin = Read-NativePinnedJson -Path $ApprovedRuntimeAllocation -Sha256 $ApprovedRuntimeAllocationSha256
$allocation = $allocationPin.document

# An explicit alternate config must be independently pinned in the runtime allocation.
# With no override the repository clear-only config remains the default.
if ([string]::IsNullOrWhiteSpace($NuGetConfig) -ne [string]::IsNullOrWhiteSpace($NuGetConfigSha256)) {
    throw 'NuGetConfig and its external SHA-256 must be supplied together.'
}
$restoreConfig = Join-Path $Repo 'NuGet.Config'
$restoreConfigPin = $null
if (-not [string]::IsNullOrWhiteSpace($NuGetConfig)) {
    if (-not [IO.Path]::IsPathFullyQualified($NuGetConfig) -or $NuGetConfigSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'An absolute pinned NuGet config is required.'
    }
    $restoreConfig = [IO.Path]::GetFullPath($NuGetConfig)
    $restoreConfigPin = $allocation.nuGetConfig
    if ($null -eq $restoreConfigPin -or $restoreConfigPin.path -cne $restoreConfig -or
        $restoreConfigPin.sha256 -cne $NuGetConfigSha256) { throw 'NuGet config is not bound to this allocation.' }
}
function Assert-RuntimeAllocation {
    # A protection state is not permission. Only a separately supplied, immutable
    # lead-owned runtime allocation can admit this future build. No allocation is
    # created here, and the source-only integration handoff grants no execution.
    if ((Get-FileHash -LiteralPath $allocationPin.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ApprovedRuntimeAllocationSha256 -or
        $allocation.schemaVersion -ne 1 -or $allocation.executionPermitted -isnot [bool] -or -not $allocation.executionPermitted -or
        $allocation.allowedOperation -cne 'locked-offline-build-publish' -or [string]::IsNullOrWhiteSpace($allocation.runtimeOwner) -or
        $allocation.candidate -cne $source.candidate -or $allocation.tree -cne $source.tree -or
        $allocation.sourceManifestSha256 -cne $SourceManifestSha256 -or
        -not [IO.Path]::GetFullPath($allocation.reviewedRepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar).Equals($Repo, [StringComparison]::OrdinalIgnoreCase) -or
        [DateTimeOffset]::Parse($allocation.expiresUtc) -le [DateTimeOffset]::UtcNow) {
        throw 'No current immutable runtime allocation for this exact checkout/candidate and build operation.'
    }
    if ($null -ne $restoreConfigPin -and
        ((Get-Item -LiteralPath $restoreConfig).Length -ne $restoreConfigPin.bytes -or
         (Get-FileHash -LiteralPath $restoreConfig -Algorithm SHA256).Hash.ToLowerInvariant() -cne $NuGetConfigSha256)) {
        throw 'Allocated NuGet config changed.'
    }
    foreach ($pin in @($allocation.sharedOwner, $allocation.library)) {
        if ($null -eq $pin -or -not [IO.Path]::IsPathFullyQualified($pin.path) -or
            (Get-Item -LiteralPath $pin.path).Length -ne $pin.bytes -or
            (Get-FileHash -LiteralPath $pin.path).Hash.ToLowerInvariant() -cne $pin.sha256) { throw 'Allocated qualified owner input differs.' }
    }
    if (-not [double]::IsFinite([double]$allocation.runTimeoutSeconds) -or $allocation.runTimeoutSeconds -le 0 -or
        $allocation.runTimeoutSeconds -gt 1800 -or $null -eq $allocation.nativeEnvironment) { throw 'Finite whole preparation budget and explicit private environment required.' }
    $hostPin = $allocation.dotnet
    if ($null -eq $hostPin -or [string]::IsNullOrWhiteSpace($hostPin.path) -or
        -not (Test-Path -LiteralPath $hostPin.path -PathType Leaf) -or
        (Get-Item -LiteralPath $hostPin.path).Length -ne $hostPin.bytes -or
        (Get-FileHash -LiteralPath $hostPin.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hostPin.sha256) {
        throw 'The allocated dotnet executable differs from its immutable host pin.'
    }
}
function Assert-SourcePins([switch]$RequireFreshGeneratedRoots) {
    if ((Get-FileHash -LiteralPath $manifestPin.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $SourceManifestSha256) { throw 'Manifest identity changed.' }
    Assert-NativeAnthropicSimpleSourcePins -Repo $Repo -Source $source
    Assert-NativeAuthenticationSourcePins -Repo $Repo -Source $source
    Assert-NativeMistralTextSourcePins -Repo $Repo -Source $source
    Assert-NativeAzureResponsesSourcePins -Repo $Repo -Source $source
    Assert-NativeSelectorOwnershipSource -Repo $Repo -Source $source
    return Assert-NativeSourceClosure -Repo $Repo -Source $source -RequireFreshGeneratedRoots:$RequireFreshGeneratedRoots
}
Assert-RuntimeAllocation
$initialClosure = Assert-SourcePins -RequireFreshGeneratedRoots
$script:lastPreparedClosure = $initialClosure
$registration = Get-NativeCompanionRegistration -Repo $Repo -RequireLockFiles
$boundary = Get-Content -LiteralPath (Join-Path $Repo 'tests/PiSharp.Tui.KittyAlternate.Tests/fixtures/held-io-source-boundary.json') -Raw | ConvertFrom-Json
foreach ($pin in $boundary.files) {
    $matching = @($source.files | Where-Object relative -eq $pin.relative)
    if ($matching.Count -ne 1 -or $matching[0].bytes -ne $pin.bytes -or $matching[0].sha256 -cne $pin.sha256) { throw "Held declaration changed: $($pin.relative)" }
}
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'A fresh evidence directory is required.' }
$evidencePath = [IO.Path]::GetFullPath($EvidenceDirectory)
$insideEvidence = [IO.Path]::GetRelativePath($Repo, $evidencePath)
if ([IO.Path]::IsPathRooted($insideEvidence) -or $insideEvidence -eq '..' -or $insideEvidence.StartsWith('..' + [IO.Path]::DirectorySeparatorChar)) {
    throw 'Preparation evidence must stay inside the allocated checkout.'
}
if (-not $insideEvidence.Replace('\', '/').StartsWith('artifacts/', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Preparation evidence must use the separate artifacts root.'
}
$evidenceTop = 'artifacts/' + $insideEvidence.Replace('\', '/').Split('/')[1]
if ($initialClosure.protectedArtifactSourceRoots -contains $evidenceTop -or @($source.files | Where-Object relative -eq $evidenceTop).Count) {
    throw 'Preparation evidence cannot overlap a pinned artifact source branch.'
}
New-Item -ItemType Directory -Path $evidencePath | Out-Null
$saved = @{}
$settings = @{ DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'; DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false';
    MSBuildEnableWorkloadResolver = 'false'; MSBUILDDISABLENODEREUSE = '1'; DOTNET_CLI_USE_MSBUILD_SERVER = '0'; UseSharedCompilation = 'false';
    PATH = (Split-Path -Parent $allocation.dotnet.path) + ';' + (Join-Path $env:SystemRoot 'System32') }
foreach ($pair in @(@{ key = 'DOTNET_CLI_HOME'; directory = 'dotnet-cli' }, @{ key = 'NUGET_PACKAGES'; directory = 'nuget' },
    @{ key = 'TEMP'; directory = 'tmp' }, @{ key = 'TMP'; directory = 'tmp' }, @{ key = 'APPDATA'; directory = 'appdata' }, @{ key = 'LOCALAPPDATA'; directory = 'localappdata' })) {
    $directory = Join-Path $Repo ('artifacts/' + $pair.directory)
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $settings[$pair.key] = $directory
}
$steps = [Collections.Generic.List[object]]::new()
function Invoke-PreparationStep([string]$Id, [string[]]$NativeArguments) {
    Assert-RuntimeAllocation
    $beforeClosure = Assert-SourcePins
    Assert-NativeGeneratedInputPins -Closure $beforeClosure -Expected $script:lastPreparedClosure.generatedFiles
    $remaining = [double]$allocation.runTimeoutSeconds - $operationClock.Elapsed.TotalSeconds
    if (-not [double]::IsFinite($remaining) -or $remaining -le 0) { throw 'Whole preparation budget expired; no subsequent operation started.' }
    $log = Join-Path $evidencePath ($Id + '.log')
    if (Test-Path -LiteralPath $log) { throw 'Preparation evidence already exists.' }
    $started = [DateTime]::UtcNow.ToString('o')
    Write-NativeLaunchEvidence -Path (Join-Path $evidencePath ($Id + '.started.json')) -Evidence @{
        id = $Id; candidate = $source.candidate; startedUtc = $started; passingReceipt = $false;
        originalOperationJoinPending = $true; runtimeAllocation = $allocationPin.path; runtimeAllocationSha256 = $ApprovedRuntimeAllocationSha256;
        sourceClosure = $beforeClosure; executable = $allocation.dotnet.path; arguments = $NativeArguments; runTimeoutSeconds = $remaining }
    $result = $null; $failure = $null; $settlement = $null
    try {
        $result = Invoke-NativeAdmissionOriginalOperation $allocation.dotnet.path $NativeArguments $Repo $nativeEnvironment (Join-Path $evidencePath ($Id + '.raw-spill.log')) $remaining
        $settlement = Get-NativeAdmissionOriginalSettlement $result
        $writes = Write-NativeAdmissionCapturedOutputs $result (Join-Path $evidencePath ($Id + '.stdout.log')) (Join-Path $evidencePath ($Id + '.stderr.log'))
        if ($writes.errors.Count) { throw $writes.errors[0].error }
        # Retain separated streams as well as the legacy combined receipt log.
        # Both streams are already settled; this does not claim their interleaving.
        [IO.File]::WriteAllText($log, [string]$result.StandardOutput.Content + [string]$result.StandardError.Content, [Text.UTF8Encoding]::new($false))
    } catch { $failure = $_ }
    $exit = if ($null -ne $result) { $result.Process.ExitCode } else { $null }
    $row = @{ id = $Id; startedUtc = $started; endedUtc = [DateTime]::UtcNow.ToString('o'); exit = $exit;
        originalChildReturned = ($null -ne $settlement -and $settlement.processStarted -and $settlement.originalTaskJoined);
        executable = $allocation.dotnet.path; arguments = $NativeArguments; settlement = $settlement; runTimeoutSeconds = $remaining;
        invocationError = $(if ($null -ne $failure) { $failure.ToString() } else { $null });
        log = $(if (Test-Path -LiteralPath $log -PathType Leaf) { @{ path = $log; bytes = (Get-Item -LiteralPath $log).Length; sha256 = (Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash.ToLowerInvariant() } } else { $null }) }
    $steps.Add($row)
    Write-NativeLaunchEvidence -Path (Join-Path $evidencePath ($Id + '.settled.json')) -Evidence $row
    if ($null -ne $failure) { throw $failure }
    if ($null -eq $result -or $exit -ne 0 -or $result.Process.Status.ToString() -cne 'Exited' -or
        -not $settlement.processStarted -or -not $settlement.originalTaskJoined -or -not $settlement.privateJobCleanupConfirmed -or
        -not $settlement.originalCaptureComplete -or $settlement.stdoutTruncated -or $settlement.stderrTruncated) {
        throw "Original preparation process/capture did not qualify: $Id. Subsequent operations are stopped."
    }
    if ((Get-Content -LiteralPath $log -Raw) -match '(?i)0x800711c7|application control.*block') {
        throw "Preparation output contains an application-control denial marker: $Id. Subsequent operations are stopped."
    }
    $afterClosure = Assert-SourcePins
    Write-NativeLaunchEvidence -Path (Join-Path $evidencePath ($Id + '.input-closure.json')) -Evidence $afterClosure
    $script:lastPreparedClosure = $afterClosure
}
$qualifiedOwner = $null
$operationClock = [Diagnostics.Stopwatch]::StartNew()
$nativeEnvironment = @{}
foreach ($property in $allocation.nativeEnvironment.PSObject.Properties) { $nativeEnvironment[$property.Name] = [string]$property.Value }
foreach ($name in $settings.Keys) { $nativeEnvironment[$name] = [string]$settings[$name] }
try {
    . $allocation.sharedOwner.path
    $qualifiedOwner = Initialize-NativeAdmissionOriginalOwner $allocation.library.path $allocation.library.sha256
    foreach ($name in $settings.Keys) {
        $saved[$name] = (Get-Item -LiteralPath ('Env:' + $name) -ErrorAction SilentlyContinue).Value
        Set-Item -LiteralPath ('Env:' + $name) -Value $settings[$name]
    }
    Push-Location -LiteralPath $Repo
    try {
        # Prevent default upward Directory.Build imports from extending the pinned
        # project inputs. These exact paths apply to restore, build and publish.
        $buildInputArguments = @(('-p:DirectoryBuildPropsPath=' + (Join-Path $Repo 'Directory.Build.props')),
            ('-p:DirectoryBuildTargetsPath=' + (Join-Path $Repo 'Directory.Build.targets')))
        Invoke-PreparationStep 'dotnet-sdk-version' @('--version')
        $sdkVersion = (Get-Content -LiteralPath (Join-Path $evidencePath 'dotnet-sdk-version.log') -Raw).Trim()
        $sdkPolicy = Get-Content -LiteralPath (Join-Path $Repo 'global.json') -Raw | ConvertFrom-Json
        if ($sdkVersion -cnotmatch '^10\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or
            $sdkPolicy.sdk.rollForward -cne 'disable' -or $sdkVersion -cne $sdkPolicy.sdk.version) {
            throw 'The allocated host did not select the exact manifest-pinned .NET 10 SDK; no restore or build started.'
        }
        Invoke-PreparationStep 'dotnet-host-info' @('--info')
        Invoke-PreparationStep 'locked-offline-restore' (@('restore', 'PiSharp.slnx', '--configfile', $restoreConfig, '--locked-mode', '--disable-parallel', '--verbosity', 'minimal') + $buildInputArguments)
        Invoke-PreparationStep 'full-solution-build' (@('build', 'PiSharp.slnx', '--no-restore', '--configuration', 'Release', '--disable-build-servers', '--verbosity', 'minimal', '-m:1', '-p:UseSharedCompilation=false') + $buildInputArguments)
        $fixtureProjects = @(
            @{ project = 'tests/fixtures/extensions/native/PluginOne/PluginOne.csproj'; output = 'one' },
            @{ project = 'tests/fixtures/extensions/native/PluginTwo/PluginTwo.csproj'; output = 'two' },
            @{ project = 'tests/fixtures/extensions/native/PluginFail/PluginFail.csproj'; output = 'fail' },
            @{ project = 'tests/fixtures/extensions/native/PluginFuture/PluginFuture.csproj'; output = 'future' },
            @{ project = 'tests/fixtures/extensions/native/PluginCli/PluginCli.csproj'; output = 'cli' },
            @{ project = 'tests/fixtures/extensions/native/PluginCliUi/PluginCliUi.csproj'; output = 'cli-ui' },
            @{ project = 'tests/fixtures/extensions/native/PluginSystemImport/PluginSystemImport.csproj'; output = 'system-import' },
            @{ project = 'tests/fixtures/extensions/native/PluginImage/PluginImage.csproj'; output = 'image' },
            @{ project = 'samples/extensions/StatefulTodo/StatefulTodo.csproj'; output = 'todo' },
            @{ project = 'samples/extensions/SessionCheckpoint/SessionCheckpoint.csproj'; output = 'checkpoint' },
            @{ project = 'tests/fixtures/extensions/native/PublishedFixture.TerminalCompanion/PublishedFixture.TerminalCompanion.csproj'; output = 'terminal-companion' }
        )
        foreach ($fixtureProject in $fixtureProjects) {
            $fixtureOutput = Join-Path $Repo ('artifacts/extensions/published-fixtures/' + $fixtureProject.output)
            Invoke-PreparationStep ('restore-fixture-' + $fixtureProject.output) (@('restore', $fixtureProject.project,
                '--configfile', $restoreConfig, '--locked-mode', '--disable-parallel', '--verbosity', 'minimal') + $buildInputArguments)
            Invoke-PreparationStep ('publish-fixture-' + $fixtureProject.output) (@('publish', $fixtureProject.project,
                '--no-restore', '--configuration', 'Release', '--output', $fixtureOutput, '--verbosity', 'minimal') + $buildInputArguments)
        }
        Assert-RuntimeAllocation
        $finalClosure = Assert-SourcePins
        Assert-NativeGeneratedInputPins -Closure $finalClosure -Expected $script:lastPreparedClosure.generatedFiles
        $outputChecks = [Collections.Generic.List[object]]::new()
        foreach ($target in $registration.targets) {
            $directory = Split-Path -Parent (Join-Path $Repo $target.entryAssembly)
            foreach ($pin in $target.outputFixturePins) {
                $file = Join-Path $directory $pin.relative
                if ((Get-Item -LiteralPath $file).Length -ne $pin.bytes -or
                    (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pin.sha256) { throw "Copied fixture changed: $file" }
                $outputChecks.Add(@{ target = $target.id; relative = $pin.relative; bytes = $pin.bytes; sha256 = $pin.sha256; pass = $true })
            }
        }
        $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
        if ($roots.Count -ne 54) { throw 'Every existing output root and the project-only terminal companion publication are required.' }
        $products = [Collections.Generic.List[object]]::new()
        foreach ($relativeRoot in $roots) {
            $directory = Join-Path $Repo $relativeRoot
            if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Missing prepared output root: $relativeRoot" }
            foreach ($file in @(Get-ChildItem -LiteralPath $directory -Recurse -File -Force)) {
                $products.Add(@{ relative = $file.FullName.Substring($Repo.Length + 1).Replace('\', '/'); bytes = $file.Length;
                    sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
            }
        }
        $kittyDirectory = 'tests/PiSharp.Tui.KittyAlternate.Tests/bin/Release/net10.0'
        $bindingDirectory = 'tests/PiSharp.Tui.Keybindings.Tests/bin/Release/net10.0'
        function Get-AssemblyPin([string]$Name, [string]$Directory) {
            $relative = $Directory + '/' + $Name + '.dll'
            $file = Join-Path $Repo $relative
            $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($file).ProductVersion
            if ([string]::IsNullOrWhiteSpace($version) -or $version.IndexOf($source.candidate, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                throw "Prepared DLL does not identify this exact candidate: $relative"
            }
            return @{ name = $Name; relative = $relative; path = $file; bytes = (Get-Item -LiteralPath $file).Length;
                sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(); informationalVersion = $version }
        }
        $dlls = @('PiSharp.Cli', 'PiSharp.Tui', 'PiSharp.CodingAgent.Tests') | ForEach-Object { Get-AssemblyPin $_ $kittyDirectory }
        $assemblies = @('PiSharp.Agent', 'PiSharp.AI', 'PiSharp.Cli', 'PiSharp.CodingAgent', 'PiSharp.Contracts',
            'PiSharp.Extensions.Abstractions', 'PiSharp.Extensions.Agent', 'PiSharp.Extensions.Runtime', 'PiSharp.Rpc', 'PiSharp.Sessions',
            'PiSharp.Tools', 'PiSharp.Tui', 'PiSharp.CodingAgent.Tests') | ForEach-Object { Get-AssemblyPin $_ $bindingDirectory }
        $googleTarget = $registration.targets | Where-Object id -eq 'google-generative-ai'
        $googleDirectory = $googleTarget.entryAssembly.Substring(0, $googleTarget.entryAssembly.LastIndexOf('/'))
        $googleAssemblies = @('PiSharp.GoogleGenerativeAI.Tests', 'PiSharp.AI', 'PiSharp.Agent', 'PiSharp.Contracts') |
            ForEach-Object { Get-AssemblyPin $_ $googleDirectory }
        $googleRestoreSteps = @($steps | Where-Object id -eq 'locked-offline-restore')
        if ($googleRestoreSteps.Count -ne 1 -or $googleRestoreSteps[0].exit -ne 0 -or -not $googleRestoreSteps[0].originalChildReturned) {
            throw 'Google scoped receipt requires the actual successful original solution locked restore.'
        }
        $googleScope = @{ schemaVersion = 1; candidate = $source.candidate; tree = $source.tree; reviewedRepositoryRoot = $Repo;
            outputRoot = $googleDirectory; assemblies = @($googleAssemblies);
            lockedRestore = @{ stepId = 'locked-offline-restore'; project = $googleTarget.project; projectSha256 = $googleTarget.projectSha256;
                lockFile = @{ relative = $googleTarget.lockFile.path; bytes = $googleTarget.lockFile.bytes; sha256 = $googleTarget.lockFile.sha256 };
                log = $googleRestoreSteps[0].log } }
        $selectorScopes = @{}
        foreach ($spec in @(@{ field = 'selector'; id = 'terminal-select-list'; names = @('PiSharp.Tui', 'PiSharp.Tui.SelectList.Tests') },
            @{ field = 'selectorIntegration'; id = 'terminal-select-dialog'; names = @('PiSharp.Agent', 'PiSharp.AI', 'PiSharp.Cli',
                'PiSharp.CodingAgent', 'PiSharp.Contracts', 'PiSharp.Extensions.Abstractions', 'PiSharp.Extensions.Agent',
                'PiSharp.Extensions.Runtime', 'PiSharp.Rpc', 'PiSharp.Sessions', 'PiSharp.Tools', 'PiSharp.Tui', 'PiSharp.CodingAgent.Tests') },
            @{ field = 'selectorOwnership'; id = 'terminal-ownership-focused'; names = @('PiSharp.Agent', 'PiSharp.AI', 'PiSharp.Cli',
                'PiSharp.CodingAgent', 'PiSharp.Contracts', 'PiSharp.Extensions.Abstractions', 'PiSharp.Extensions.Agent',
                'PiSharp.Extensions.Runtime', 'PiSharp.Rpc', 'PiSharp.Sessions', 'PiSharp.Tools', 'PiSharp.Tui', 'PiSharp.CodingAgent.Tests') })) {
            $target = $registration.targets | Where-Object id -eq $spec.id
            $directory = $target.entryAssembly.Substring(0, $target.entryAssembly.LastIndexOf('/'))
            $scopedAssemblies = @($spec.names | ForEach-Object { Get-AssemblyPin $_ $directory })
            $selectorScopes[$spec.field] = @{ schemaVersion = 1; candidate = $source.candidate; tree = $source.tree; reviewedRepositoryRoot = $Repo;
                project = $target.project; entryAssembly = $target.entryAssembly; outputRoot = $directory; assemblies = $scopedAssemblies;
                lockedRestore = @{ verified = $true; stepId = 'locked-offline-restore'; projectSha256 = $target.projectSha256;
                    lockFile = @{ relative = $target.lockFile.path; bytes = $target.lockFile.bytes; sha256 = $target.lockFile.sha256 };
                    log = $googleRestoreSteps[0].log } }
        }
        $ownershipPlanPath = 'tests/PiSharp.Terminal.Ownership.Tests/focused-case-plan.json'
        $ownershipPlanPin = @($source.files | Where-Object relative -CEQ $ownershipPlanPath)
        if ($ownershipPlanPin.Count -ne 1) { throw 'Exact focused ownership selection source pin required.' }
        $selectorScopes['selectorOwnership'].selection = @{ sourceRelative = $ownershipPlanPath; sourceSha256 = $ownershipPlanPin[0].sha256;
            selectedOriginalCases = 31; originalSelectorCases = 140; partialResultOnly = $true }
        $ownedChildRoot = 'tests/PiSharp.CodingAgent.Tests/bin/Release/net10.0'
        $selectorScopes['selectorOwnership'].ownedChild = @{ outputRoot = $ownedChildRoot;
            entryAssembly = $ownedChildRoot + '/PiSharp.CodingAgent.Tests.dll';
            products = @($products | Where-Object { $_.relative.StartsWith($ownedChildRoot + '/', [StringComparison]::Ordinal) }) }
        $anthropicTarget = $registration.targets | Where-Object id -CEQ 'anthropic-simple'
        $anthropicDirectory = $anthropicTarget.entryAssembly.Substring(0, $anthropicTarget.entryAssembly.LastIndexOf('/'))
        $anthropicAssemblies = @('PiSharp.AnthropicSimple.Tests', 'PiSharp.AI', 'PiSharp.Agent', 'PiSharp.Contracts') |
            ForEach-Object { Get-AssemblyPin $_ $anthropicDirectory }
        $anthropicBuildSteps = @($steps | Where-Object id -CEQ 'full-solution-build')
        if ($anthropicBuildSteps.Count -ne 1 -or $anthropicBuildSteps[0].exit -ne 0 -or -not $anthropicBuildSteps[0].originalChildReturned) {
            throw 'Anthropic Simple scope requires the actual successful original solution build.'
        }
        $solutionPin = @($source.files | Where-Object relative -CEQ 'PiSharp.slnx')
        if ($solutionPin.Count -ne 1) { throw 'Exact solution source pin required for Anthropic Simple preparation.' }
        $anthropicScope = @{ schemaVersion = 1; candidate = $source.candidate; tree = $source.tree; reviewedRepositoryRoot = $Repo;
            project = $anthropicTarget.project; entryAssembly = $anthropicTarget.entryAssembly; outputRoot = $anthropicDirectory;
            assemblies = @($anthropicAssemblies); host = $allocation.dotnet;
            lockedRestore = @{ verified = $true; stepId = 'locked-offline-restore'; project = $anthropicTarget.project;
                projectSha256 = $anthropicTarget.projectSha256;
                lockFile = @{ relative = $anthropicTarget.lockFile.path; bytes = $anthropicTarget.lockFile.bytes; sha256 = $anthropicTarget.lockFile.sha256 };
                log = $googleRestoreSteps[0].log };
            solutionBuild = @{ verified = $true; stepId = 'full-solution-build'; solution = 'PiSharp.slnx';
                solutionSha256 = $solutionPin[0].sha256; log = $anthropicBuildSteps[0].log } }
        $authenticationTarget = $registration.targets | Where-Object id -CEQ 'authentication'
        $authenticationDirectory = $authenticationTarget.entryAssembly.Substring(0, $authenticationTarget.entryAssembly.LastIndexOf('/'))
        $authenticationAssemblies = @('PiSharp.Authentication.Tests', 'PiSharp.AI', 'PiSharp.Contracts') |
            ForEach-Object { Get-AssemblyPin $_ $authenticationDirectory }
        $authenticationBuildSteps = @($steps | Where-Object id -CEQ 'full-solution-build')
        if ($authenticationBuildSteps.Count -ne 1 -or $authenticationBuildSteps[0].exit -ne 0 -or -not $authenticationBuildSteps[0].originalChildReturned) {
            throw 'Authentication scope requires the actual successful original solution build.'
        }
        $solutionPin = @($source.files | Where-Object relative -CEQ 'PiSharp.slnx')
        if ($solutionPin.Count -ne 1) { throw 'Exact solution source pin required for Authentication preparation.' }
        $authenticationScope = @{ schemaVersion = 1; candidate = $source.candidate; tree = $source.tree; reviewedRepositoryRoot = $Repo;
            project = $authenticationTarget.project; entryAssembly = $authenticationTarget.entryAssembly; outputRoot = $authenticationDirectory;
            assemblies = @($authenticationAssemblies); host = $allocation.dotnet;
            lockedRestore = @{ verified = $true; stepId = 'locked-offline-restore'; project = $authenticationTarget.project;
                projectSha256 = $authenticationTarget.projectSha256;
                lockFile = @{ relative = $authenticationTarget.lockFile.path; bytes = $authenticationTarget.lockFile.bytes; sha256 = $authenticationTarget.lockFile.sha256 };
                log = $googleRestoreSteps[0].log };
            solutionBuild = @{ verified = $true; stepId = 'full-solution-build'; solution = 'PiSharp.slnx';
                solutionSha256 = $solutionPin[0].sha256; log = $authenticationBuildSteps[0].log } }
        $azureResponsesTarget = $registration.targets | Where-Object id -CEQ 'azure-responses'
        $azureResponsesDirectory = $azureResponsesTarget.entryAssembly.Substring(0, $azureResponsesTarget.entryAssembly.LastIndexOf('/'))
        $azureResponsesAssemblies = @('PiSharp.AzureResponses.Tests', 'PiSharp.AI', 'PiSharp.Agent', 'PiSharp.Contracts') |
            ForEach-Object { Get-AssemblyPin $_ $azureResponsesDirectory }
        $azureResponsesBuildSteps = @($steps | Where-Object id -CEQ 'full-solution-build')
        if ($azureResponsesBuildSteps.Count -ne 1 -or $azureResponsesBuildSteps[0].exit -ne 0 -or -not $azureResponsesBuildSteps[0].originalChildReturned) {
            throw 'AzureResponses scope requires the actual successful original solution build.'
        }
        $solutionPin = @($source.files | Where-Object relative -CEQ 'PiSharp.slnx')
        if ($solutionPin.Count -ne 1) { throw 'Exact solution source pin required for AzureResponses preparation.' }
        $azureResponsesScope = @{ schemaVersion = 1; candidate = $source.candidate; tree = $source.tree; reviewedRepositoryRoot = $Repo;
            project = $azureResponsesTarget.project; entryAssembly = $azureResponsesTarget.entryAssembly; outputRoot = $azureResponsesDirectory;
            assemblies = @($azureResponsesAssemblies); host = $allocation.dotnet;
            lockedRestore = @{ verified = $true; stepId = 'locked-offline-restore'; project = $azureResponsesTarget.project;
                projectSha256 = $azureResponsesTarget.projectSha256;
                lockFile = @{ relative = $azureResponsesTarget.lockFile.path; bytes = $azureResponsesTarget.lockFile.bytes; sha256 = $azureResponsesTarget.lockFile.sha256 };
                log = $googleRestoreSteps[0].log };
            solutionBuild = @{ verified = $true; stepId = 'full-solution-build'; solution = 'PiSharp.slnx';
                solutionSha256 = $solutionPin[0].sha256; log = $azureResponsesBuildSteps[0].log } }
        $mistralTextTarget = $registration.targets | Where-Object id -CEQ 'mistral-text'
        $mistralTextDirectory = $mistralTextTarget.entryAssembly.Substring(0, $mistralTextTarget.entryAssembly.LastIndexOf('/'))
        $mistralTextAssemblies = @('PiSharp.MistralConversations.Tests', 'PiSharp.AI', 'PiSharp.Contracts') |
            ForEach-Object { Get-AssemblyPin $_ $mistralTextDirectory }
        $mistralTextBuildSteps = @($steps | Where-Object id -CEQ 'full-solution-build')
        if ($mistralTextBuildSteps.Count -ne 1 -or $mistralTextBuildSteps[0].exit -ne 0 -or -not $mistralTextBuildSteps[0].originalChildReturned) {
            throw 'MistralText scope requires the actual successful original solution build.'
        }
        $solutionPin = @($source.files | Where-Object relative -CEQ 'PiSharp.slnx')
        if ($solutionPin.Count -ne 1) { throw 'Exact solution source pin required for MistralText preparation.' }
        $mistralTextScope = @{ schemaVersion = 1; candidate = $source.candidate; tree = $source.tree; reviewedRepositoryRoot = $Repo;
            project = $mistralTextTarget.project; entryAssembly = $mistralTextTarget.entryAssembly; outputRoot = $mistralTextDirectory;
            assemblies = @($mistralTextAssemblies); host = $allocation.dotnet;
            lockedRestore = @{ verified = $true; stepId = 'locked-offline-restore'; project = $mistralTextTarget.project;
                projectSha256 = $mistralTextTarget.projectSha256;
                lockFile = @{ relative = $mistralTextTarget.lockFile.path; bytes = $mistralTextTarget.lockFile.bytes; sha256 = $mistralTextTarget.lockFile.sha256 };
                log = $googleRestoreSteps[0].log };
            solutionBuild = @{ verified = $true; stepId = 'full-solution-build'; solution = 'PiSharp.slnx';
                solutionSha256 = $solutionPin[0].sha256; log = $mistralTextBuildSteps[0].log } }
        $receiptFile = Join-Path $evidencePath 'actual-permitted-build-receipt.json'
        Assert-RuntimeAllocation
        $publicationClosure = Assert-SourcePins
        Assert-NativeGeneratedInputPins -Closure $publicationClosure -Expected $finalClosure.generatedFiles
        Write-NativeLaunchEvidence -Path $receiptFile -Evidence @{ schemaVersion = 1; generatedUtc = [DateTime]::UtcNow.ToString('o');
            candidate = $source.candidate; tree = $source.tree; sourceManifestSha256 = $SourceManifestSha256;
            reviewedRepositoryRoot = $Repo; reviewRoot = $Repo; policyPermittedNativeBoundary = $true; executionPermitted = $true;
            runtimeAllocation = $allocationPin.path; runtimeAllocationSha256 = $ApprovedRuntimeAllocationSha256;
            dotnet = $allocation.dotnet; host = $allocation.dotnet; sdkVersion = $sdkVersion;
            nuGetConfig = $restoreConfigPin;
            sourceAdmission = @{ schemaVersion = 1; policy = $publicationClosure.policy; freshCheckoutVerified = $initialClosure.freshGeneratedRootsRequired;
                sourceFilesVerified = $publicationClosure.sourceFilesVerified; generatedRoots = $publicationClosure.generatedRoots;
                protectedArtifactSourceRoots = $publicationClosure.protectedArtifactSourceRoots;
                generatedFiles = $publicationClosure.generatedFiles; evidenceRoot = $publicationClosure.evidenceRoot;
                trustedGeneratedInputProducer = 'allocated pinned SDK, initially empty output roots, sequential owned preparation steps';
                inputClosureEvidenceDirectory = $evidencePath };
            actualHostInfo = ($steps | Where-Object id -eq 'dotnet-host-info').log; productRoots = $roots; products = $products.ToArray();
            dlls = @($dlls); assemblies = @($assemblies); google = $googleScope; anthropicSimple = $anthropicScope;
            authentication = $authenticationScope; azureResponses = $azureResponsesScope; mistralText = $mistralTextScope;
            selector = $selectorScopes['selector']; selectorIntegration = $selectorScopes['selectorIntegration'];
            selectorOwnership = $selectorScopes['selectorOwnership'];
            actualPreparationSteps = $steps.ToArray(); nativeTestsStarted = 0 }
        $receiptSha = (Get-FileHash -LiteralPath $receiptFile -Algorithm SHA256).Hash.ToLowerInvariant()
        $validation = Get-NativePreparedValidation -Repo $Repo -SourceManifest $SourceManifest -SourceManifestSha256 $SourceManifestSha256 -BuildReceipt $receiptFile -BuildReceiptSha256 $receiptSha
        Write-NativeLaunchEvidence -Path (Join-Path $evidencePath 'complete-prepared-admission.json') -Evidence @{ schemaVersion = 1;
            candidate = $source.candidate; tree = $source.tree; sourceFiles = $source.files.Count; productRoots = $roots.Count;
            registeredTargets = $registration.targets.Count; actualProducts = $products.Count; heldDeclarations = $boundary.files.Count;
            outputFixtureChecks = $outputChecks.ToArray(); validation = $validation; allAdmissionChecksPassed = $true; nativeTestsStarted = 0;
            actualBuildReceipt = @{ path = $receiptFile; bytes = (Get-Item -LiteralPath $receiptFile).Length; sha256 = $receiptSha } }
    } finally { Pop-Location }
} finally {
    if ($null -ne $qualifiedOwner) { foreach ($held in $qualifiedOwner.held) { $held.Dispose() } }
    foreach ($name in $saved.Keys) {
        if ($null -eq $saved[$name]) { Remove-Item -LiteralPath ('Env:' + $name) -ErrorAction SilentlyContinue }
        else { Set-Item -LiteralPath ('Env:' + $name) -Value $saved[$name] }
    }
}
