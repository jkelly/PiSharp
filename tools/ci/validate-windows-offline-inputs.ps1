param(
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][string]$SourceManifest,
    [Parameter(Mandatory)][string]$SourceManifestSha256,
    [Parameter(Mandatory)][string]$ApprovedRuntimeAllocation,
    [Parameter(Mandatory)][string]$ApprovedRuntimeAllocationSha256,
    [Parameter(Mandatory)][string]$Report
)
$ErrorActionPreference = 'Stop'
# A source-only readiness check. No dotnet, Node, git, action, restore, build,
# fixture publish or test runner is invoked. Success never means native acceptance.
$result = [ordered]@{ schemaVersion = 1; status = 'REJECTED'; inputsReady = $false;
    nativeExecutions = 0; nativeAcceptance = $false; packageAcceptance = $false;
    runtimePermissionIssued = $false; sdkExecutionVerified = $false; error = $null }
$reportPath = [IO.Path]::GetFullPath($Report)
$repoPath = [IO.Path]::GetFullPath($Repo).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ($reportPath.Equals($repoPath, [StringComparison]::OrdinalIgnoreCase) -or
    $reportPath.StartsWith($repoPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $reportPath) -or -not (Test-Path -LiteralPath (Split-Path -Parent $reportPath) -PathType Container)) {
    throw 'WINDOWS-CI-REPORT: A fresh report in an existing directory outside the checkout is required.'
}
function Read-CiPinnedInput([string]$Path, [string]$Sha256) {
    if ($Sha256 -cnotmatch '^[0-9a-f]{64}$' -or -not (Test-Path -LiteralPath $Path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Sha256) {
        throw 'WINDOWS-CI-INPUT: Missing or changed independently pinned input.'
    }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}
try {
    if (-not $IsWindows) { throw 'WINDOWS-CI-PLATFORM: This slice admits Windows PowerShell 7 only.' }
    $source = Read-CiPinnedInput $SourceManifest $SourceManifestSha256
    $allocation = Read-CiPinnedInput $ApprovedRuntimeAllocation $ApprovedRuntimeAllocationSha256
    # Verify helper bytes before loading them. Complete membership is then checked
    # by the unchanged native source-closure validator, including ignored files.
    foreach ($relative in @('tools/ci/windows-offline-contract.ps1', 'tools/ci/validate-windows-offline-inputs.ps1',
        'tools/native-companion-registration.ps1', 'tools/native-source-admission.ps1')) {
        $pins = @($source.files | Where-Object relative -CEQ $relative)
        $file = Join-Path $repoPath $relative
        if ($pins.Count -ne 1 -or -not (Test-Path -LiteralPath $file -PathType Leaf) -or
            (Get-Item -LiteralPath $file).Length -ne $pins[0].bytes -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pins[0].sha256) {
            throw "WINDOWS-CI-SOURCE: Pinned preflight/admission helper changed: $relative"
        }
    }
    . (Join-Path $repoPath 'tools/ci/windows-offline-contract.ps1')
    $repoPath = Assert-WindowsOfflineCiBinding -Repo $repoPath -Source $source -SourceManifestSha256 $SourceManifestSha256 -Allocation $allocation
    . (Join-Path $repoPath 'tools/native-companion-registration.ps1')
    $closure = Assert-NativeSourceClosure -Repo $repoPath -Source $source -RequireFreshGeneratedRoots
    $registration = Get-NativeCompanionRegistration -Repo $repoPath -RequireLockFiles
    Assert-WindowsOfflineCiPolicy -SdkPolicy (Get-Content (Join-Path $repoPath 'global.json') -Raw | ConvertFrom-Json) -NuGetPolicy ([xml](Get-Content (Join-Path $repoPath 'NuGet.Config') -Raw)) -BuildPolicy ([xml](Get-Content (Join-Path $repoPath 'Directory.Build.props') -Raw))
    $hostPin = $allocation.dotnet
    if (-not (Test-Path -LiteralPath $hostPin.path -PathType Leaf) -or
        (Get-Item -LiteralPath $hostPin.path).Length -ne $hostPin.bytes -or
        (Get-FileHash -LiteralPath $hostPin.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hostPin.sha256) {
        throw 'WINDOWS-CI-HOST: Actual host bytes differ from the independent allocation.'
    }
    [xml]$solution = Get-Content (Join-Path $repoPath 'PiSharp.slnx') -Raw
    $projects = @($solution.SelectNodes('//Project') | ForEach-Object { [string]$_.Path })
    # Include the ten fixture projects restored by the original preparation script,
    # without invoking that script or admitting other excluded projects.
    $projects += @('PluginOne', 'PluginTwo', 'PluginFail', 'PluginFuture', 'PluginCli', 'PluginCliUi', 'PluginSystemImport', 'PluginImage') |
        ForEach-Object { "tests/fixtures/extensions/native/$_/$_.csproj" }
    $projects += 'samples/extensions/StatefulTodo/StatefulTodo.csproj', 'samples/extensions/SessionCheckpoint/SessionCheckpoint.csproj'
    $projects = @($projects | Sort-Object -Unique)
    foreach ($relative in $projects) {
        $projectPath = Resolve-NativeSourcePath -Repo $repoPath -Relative $relative
        $lockRelative = $relative.Substring(0, $relative.LastIndexOf('/')) + '/packages.lock.json'
        foreach ($required in @($relative, $lockRelative)) {
            if (@($source.files | Where-Object relative -CEQ $required).Count -ne 1) {
                throw "WINDOWS-CI-LOCKS: Required project/lock is not source-pinned: $required"
            }
        }
        Assert-WindowsOfflineCiProject -Project ([xml](Get-Content $projectPath -Raw)) -Lock (Get-Content (Join-Path $repoPath $lockRelative) -Raw | ConvertFrom-Json) -Relative $relative
    }
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    Assert-NativePreparedRootDeclaration -RequiredRoots $roots -DeclaredRoots $roots
    $result.status = 'INPUTS_READY_NATIVE_EXECUTION_PENDING'
    $result.inputsReady = $true
    $result.candidate = $source.candidate; $result.tree = $source.tree
    $result.reviewedRepositoryRoot = $repoPath; $result.sourceManifestSha256 = $SourceManifestSha256
    $result.allocationSha256 = $ApprovedRuntimeAllocationSha256
    $result.sdkVersionPolicy = '10.0.401'; $result.sourceFilesVerified = $closure.sourceFilesVerified
    $result.projectsWithFrameworkOnlyLocks = $projects.Count
    $result.registeredTargets = $registration.targets.Count; $result.requiredProductRoots = $roots.Count
    $result.remainingGates = @('Original allocated SDK selection and locked restore', 'Original full solution build and ten local fixture publishes',
        'Complete candidate-bound product, host, SDK, preparation-log and scoped receipts', 'Original tools/test-native.ps1 launch/output/process joins and nonpassing failure receipts',
        'Independent runtime review, packaging and original release acceptance')
} catch {
    $result.error = $_.Exception.Message
} finally {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($result | ConvertTo-Json -Depth 12) + "`n")
    $stream = [IO.File]::Open($reportPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}
if (-not $result.inputsReady) { throw "Windows offline CI input rejection; failed report retained at $reportPath. $($result.error)" }
