param(
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][string]$SourceManifest,
    [Parameter(Mandatory)][string]$SourceManifestSha256,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
# Authored admission controls only. No host/SDK/product is invoked, even in
# launcher rejection controls; every invocation must reject before process setup.
$Repo = (Resolve-Path -LiteralPath $Repo).Path
if ((Get-FileHash -LiteralPath $SourceManifest -Algorithm SHA256).Hash.ToLowerInvariant() -cne $SourceManifestSha256) { throw 'Source manifest changed.' }
$source = Get-Content -LiteralPath $SourceManifest -Raw | ConvertFrom-Json
foreach ($relative in @('tools/native-companion-registration.ps1', 'tools/native-source-admission.ps1',
    'tools/native-companion-targets.json', 'tools/test-native-companion-selection-admission.ps1')) {
    $pin = @($source.files | Where-Object relative -eq $relative)
    $file = Join-Path $Repo $relative
    if ($pin.Count -ne 1 -or (Get-Item -LiteralPath $file).Length -ne $pin[0].bytes -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pin[0].sha256) { throw 'Selection admission source/control differs from pinned input.' }
}
. (Join-Path $Repo 'tools/native-companion-registration.ps1')
$closure = Assert-NativeSourceClosure -Repo $Repo -Source $source
$registration = Get-NativeCompanionRegistration -Repo $Repo -RequireLockFiles
$evidencePath = [IO.Path]::GetFullPath($EvidenceDirectory)
$relativeEvidence = [IO.Path]::GetRelativePath($Repo, $evidencePath).Replace('\', '/')
if ([IO.Path]::IsPathRooted($relativeEvidence) -or -not $relativeEvidence.StartsWith('artifacts/', [StringComparison]::OrdinalIgnoreCase) -or
    @($relativeEvidence.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -or (Test-Path -LiteralPath $evidencePath)) { throw 'Fresh owned artifacts evidence directory required.' }
$evidenceTop = 'artifacts/' + $relativeEvidence.Split('/')[1]
if ($closure.protectedArtifactSourceRoots -contains $evidenceTop -or @($source.files | Where-Object relative -eq $evidenceTop).Count) { throw 'Evidence overlaps pinned artifact source.' }
$ancestor = Split-Path -Parent $evidencePath
while ($ancestor -and -not $ancestor.Equals($Repo, [StringComparison]::OrdinalIgnoreCase)) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Evidence ancestor is linked.' }
    $ancestor = Split-Path -Parent $ancestor
}
New-Item -ItemType Directory -Path $evidencePath | Out-Null
$controls = [Collections.Generic.List[object]]::new()
function Assert-Control([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Invoke-SelectionControl([string]$Id, [string]$ExpectedPrefix, [scriptblock]$Body) {
    $row = [ordered]@{ id = $Id; passed = $false; expectedPrefix = $ExpectedPrefix; observedError = $null;
        admissionControlOnly = $true; nativeExecutions = 0; packageAcceptance = $false; phaseAcceptance = $false }
    try {
        & $Body
        if ($ExpectedPrefix) { throw 'Expected selection rejection did not occur.' }
        $row.passed = $true
    } catch {
        $row.observedError = $_.Exception.ToString()
        if ($ExpectedPrefix -and $_.Exception.Message.StartsWith($ExpectedPrefix, [StringComparison]::Ordinal)) { $row.passed = $true }
    } finally {
        $controls.Add([pscustomobject]$row)
        Write-NativeLaunchEvidence -Path (Join-Path $evidencePath ($Id + '.settled.json')) -Evidence $row
    }
    if (-not $row.passed) { throw "Selection control failed: $Id; later controls stopped." }
}
Invoke-SelectionControl '01-default-full-order' '' {
    $plan = Get-NativeCompanionSelection -Registration $registration
    Assert-Control ($plan.mode -ceq 'FULL_REGISTRY' -and $plan.targets.Count -eq 32 -and $plan.unselectedIds.Count -eq 0 -and
        ($plan.selectedIds -join ',') -ceq ($registration.targets.id -join ',')) 'Default scope/order changed.'
}
Invoke-SelectionControl '02-explicit-registry-order' '' {
    $plan = Get-NativeCompanionSelection -Registration $registration -TargetId @('simple', 'thinking', 'focus')
    Assert-Control ($plan.mode -ceq 'EXPLICIT_TARGET_IDS' -and ($plan.selectedIds -join ',') -ceq 'focus,thinking,simple' -and
        $plan.unselectedIds.Count -eq 29) 'Selection reordered or included an unrequested target.'
}
Invoke-SelectionControl '03-single-target' '' {
    $plan = Get-NativeCompanionSelection -Registration $registration -TargetId 'terminal-select-dialog'
    Assert-Control ($plan.targets.Count -eq 1 -and $plan.selectedIds[0] -ceq 'terminal-select-dialog') 'Single selection changed.'
}
Invoke-SelectionControl '04-explicit-full-order' '' {
    $ids = @($registration.targets.id); [Array]::Reverse($ids)
    $plan = Get-NativeCompanionSelection -Registration $registration -TargetId $ids
    Assert-Control ($plan.mode -ceq 'EXPLICIT_TARGET_IDS' -and ($plan.selectedIds -join ',') -ceq ($registration.targets.id -join ',')) 'Explicit full selection changed registry order.'
}
foreach ($spec in @(
    @{ id = '05-unknown'; ids = @('missing'); prefix = 'COMPANION-SELECTION: Unknown' },
    @{ id = '06-duplicate'; ids = @('focus', 'focus'); prefix = 'COMPANION-SELECTION: Duplicate' },
    @{ id = '07-wrong-case'; ids = @('Focus'); prefix = 'COMPANION-SELECTION: Unknown' },
    @{ id = '08-wildcard'; ids = @('*'); prefix = 'COMPANION-SELECTION: Unknown' },
    @{ id = '09-whitespace'; ids = @(' focus'); prefix = 'COMPANION-SELECTION: Unknown' },
    @{ id = '10-empty-id'; ids = @(''); prefix = 'COMPANION-SELECTION: Unknown' },
    @{ id = '11-empty-array'; ids = @(); prefix = 'COMPANION-SELECTION: Explicit selection' },
    @{ id = '12-null'; ids = $null; prefix = 'COMPANION-SELECTION: Explicit selection' },
    @{ id = '13-valid-plus-unknown'; ids = @('focus', 'missing'); prefix = 'COMPANION-SELECTION: Unknown' }
)) {
    Invoke-SelectionControl $spec.id $spec.prefix { Get-NativeCompanionSelection -Registration $registration -TargetId $spec.ids | Out-Null }
}
Invoke-SelectionControl '14-never-run-receipt-rows' '' {
    $plan = Get-NativeCompanionSelection -Registration $registration -TargetId 'focus'
    $rows = @(New-NativeCompanionReceiptRows -Registration $registration -Selection $plan)
    $unselected = @($rows | Where-Object { -not $_.selected })
    Assert-Control ($rows.Count -eq 32 -and $unselected.Count -eq 31 -and
        @($unselected | Where-Object { $_.status -cne 'UNSELECTED_NEVER_RUN' -or $_.passingReceipt -or $_.pid -or $_.report -or $_.processJoined -or $_.outputJoined }).Count -eq 0 -and
        @($rows | Where-Object { $_.selected -and $_.status -ceq 'UNEXECUTED' -and -not $_.passingReceipt }).Count -eq 1) 'Unselected rows imply execution/pass or selected row disappeared.'
}
Invoke-SelectionControl '15-default-receipt-rows' '' {
    $plan = Get-NativeCompanionSelection -Registration $registration
    $rows = @(New-NativeCompanionReceiptRows -Registration $registration -Selection $plan)
    Assert-Control ($rows.Count -eq 32 -and @($rows | Where-Object { -not $_.selected -or $_.status -cne 'UNEXECUTED' -or $_.passingReceipt }).Count -eq 0) 'Default receipt semantics changed.'
}
Invoke-SelectionControl '16-ambiguous-registration' 'COMPANION-SELECTION: Ambiguous' {
    $copy = [pscustomobject]@{ targets = @($registration.targets) + @($registration.targets[0]) }
    Get-NativeCompanionSelection -Registration $copy -TargetId 'focus' | Out-Null
}
# Real launcher controls cannot reach Process creation: a null context is a
# second independent stop even if selection rejection accidentally regresses.
foreach ($spec in @(
    @{ id = '17-launcher-unknown'; ids = @('missing'); prefix = 'COMPANION-SELECTION: Unknown' },
    @{ id = '18-launcher-duplicate'; ids = @('focus', 'focus'); prefix = 'COMPANION-SELECTION: Duplicate' },
    @{ id = '19-launcher-empty'; ids = @(); prefix = 'COMPANION-SELECTION: Explicit selection' },
    @{ id = '20-launcher-valid-needs-context'; ids = @('focus'); prefix = 'Pinned prepared validation context required.' }
)) {
    $neverCreated = Join-Path $evidencePath ($spec.id + '-must-not-exist')
    Invoke-SelectionControl $spec.id $spec.prefix {
        try { Invoke-NativeCompanionTargets -Repo $Repo -TargetId $spec.ids -PreparedValidation $null -DotnetExecutable '' -EvidenceDirectory $neverCreated }
        finally { if (Test-Path -LiteralPath $neverCreated) { throw 'Launcher created evidence before admission.' } }
    }
}
# Additional control roots contain copied source/inert declarations only. They
# are deliberately not complete preparation receipts or execution authority.
$registryRoot = Join-Path $evidencePath 'anthropic-registry-controls'
$copyPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$copyPaths.Add('PiSharp.slnx') | Out-Null
$copyPaths.Add('tools/native-companion-targets.json') | Out-Null
foreach ($target in $registration.targets) {
    $copyPaths.Add($target.project) | Out-Null; $copyPaths.Add($target.lockFile.path) | Out-Null
    foreach ($pin in $target.fixturePins) { $copyPaths.Add($pin.path) | Out-Null }
}
foreach ($pin in (Get-NativeAnthropicSimpleSourcePins)) { $copyPaths.Add($pin.relative) | Out-Null }
foreach ($pin in (Get-NativeAuthenticationSourcePins)) { $copyPaths.Add($pin.relative) | Out-Null }
foreach ($pin in (Get-NativeMistralTextSourcePins)) { $copyPaths.Add($pin.relative) | Out-Null }
foreach ($pin in (Get-NativeAzureResponsesSourcePins)) { $copyPaths.Add($pin.relative) | Out-Null }
$linkedInventory = Get-Content -LiteralPath (Join-Path $Repo 'tests/PiSharp.Terminal.Ownership.Tests/linked-source-inventory.json') -Raw | ConvertFrom-Json
foreach ($pin in $linkedInventory.files) { $copyPaths.Add($pin.relative) | Out-Null }
foreach ($relative in $copyPaths) {
    $destination = Resolve-NativeCompanionPath $registryRoot $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $Repo $relative) -Destination $destination
}
function Invoke-RegistryMutation([scriptblock]$MutateRegistry, [string]$ChangedPath = '', [string]$ChangedText = '') {
    $copy = $registration | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    & $MutateRegistry $copy
    $registryFile = Join-Path $registryRoot 'tools/native-companion-targets.json'
    $copy | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $registryFile -Encoding utf8
    try {
        if ($ChangedPath) { [IO.File]::AppendAllText((Join-Path $registryRoot $ChangedPath), $ChangedText) }
        Get-NativeCompanionRegistration -Repo $registryRoot -RequireLockFiles | Out-Null
    } finally {
        Copy-Item -LiteralPath (Join-Path $Repo 'tools/native-companion-targets.json') -Destination $registryFile -Force
        if ($ChangedPath) { Copy-Item -LiteralPath (Join-Path $Repo $ChangedPath) -Destination (Join-Path $registryRoot $ChangedPath) -Force }
    }
}
Invoke-SelectionControl '21-anthropic-selection' '' {
    $plan = Get-NativeCompanionSelection -Registration $registration -TargetId 'anthropic-simple'
    Assert-Control ($plan.targets.Count -eq 1 -and $plan.selectedIds[0] -ceq 'anthropic-simple' -and
        $plan.unselectedIds.Count -eq 31) 'Anthropic selection includes historical companions.'
}
Invoke-SelectionControl '22-anthropic-root-added-only' '' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    $historical = [pscustomobject]@{ targets = @($registration.targets | Where-Object id -CNE 'anthropic-simple') }
    $before = @(Get-NativePreparedArtifactRoots -Registration $historical)
    Assert-Control ($roots.Count -eq 54 -and $before.Count -eq 53 -and
        (@($roots | Where-Object { $_ -notin $before }) -join ',') -ceq 'tests/PiSharp.AnthropicSimple.Tests/bin/Release/net10.0') 'Original output roots changed or another draft root was added.'
    Get-NativeCompanionRegistration -Repo $registryRoot -RequireLockFiles | Out-Null
}
Invoke-SelectionControl '23-missing-anthropic-id' 'The full native gate requires' {
    Invoke-RegistryMutation { param($copy) $copy.targets = @($copy.targets | Where-Object id -CNE 'anthropic-simple') }
}
Invoke-SelectionControl '24-duplicate-anthropic-id' 'The full native gate requires' {
    Invoke-RegistryMutation { param($copy) $copy.targets += @($copy.targets | Where-Object id -CEQ 'anthropic-simple') }
}
Invoke-SelectionControl '25-anthropic-argument-order' 'Anthropic Simple requires' {
    Invoke-RegistryMutation { param($copy) $target = $copy.targets | Where-Object id -CEQ 'anthropic-simple'; $target.arguments = @($target.arguments[1], $target.arguments[0]) }
}
Invoke-SelectionControl '26-changed-anthropic-inventory' 'Pinned companion fixture changed:' {
    Invoke-RegistryMutation { param($copy) } 'tests/PiSharp.AnthropicSimple.Tests/source-inventory.json' 'changed'
}
Invoke-SelectionControl '27-changed-anthropic-project' 'Registered companion project changed:' {
    Invoke-RegistryMutation { param($copy) } 'tests/PiSharp.AnthropicSimple.Tests/PiSharp.AnthropicSimple.Tests.csproj' 'changed'
}
Invoke-SelectionControl '28-changed-anthropic-lock' 'Registered companion lockfile changed:' {
    Invoke-RegistryMutation { param($copy) } 'tests/PiSharp.AnthropicSimple.Tests/packages.lock.json' 'changed'
}
Invoke-SelectionControl '29-missing-feature-source-pin' 'ANTHROPIC-SIMPLE-SOURCE:' {
    $copy = $source | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.files = @($copy.files | Where-Object relative -CNE 'src/PiSharp.AI/Protocols/AnthropicMessages/AnthropicMessagesSimpleOptions.cs')
    Assert-NativeAnthropicSimpleSourcePins -Repo $Repo -Source $copy
}
Invoke-SelectionControl '30-changed-feature-source-pin' 'ANTHROPIC-SIMPLE-SOURCE:' {
    $copy = $source | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    ($copy.files | Where-Object relative -CEQ 'tests/PiSharp.AnthropicSimple.Tests/source-inventory.json').sha256 = '0' * 64
    Assert-NativeAnthropicSimpleSourcePins -Repo $Repo -Source $copy
}
Invoke-SelectionControl '31-missing-output-root' 'NATIVE-PREPARED-ROOTS:' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    Assert-NativePreparedRootDeclaration -RequiredRoots $roots -DeclaredRoots @($roots | Where-Object { $_ -cne 'tests/PiSharp.AnthropicSimple.Tests/bin/Release/net10.0' })
}
Invoke-SelectionControl '32-unadmitted-output-root' 'NATIVE-PREPARED-ROOTS:' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    Assert-NativePreparedRootDeclaration -RequiredRoots $roots -DeclaredRoots @($roots + 'tests/Unadmitted.Auth.Draft/bin/Release/net10.0')
}
Invoke-SelectionControl '33-changed-feature-source-bytes' 'ANTHROPIC-SIMPLE-SOURCE:' {
    Invoke-RegistryMutation { param($copy) } 'src/PiSharp.AI/Protocols/AnthropicMessages/AnthropicMessagesSimpleOptions.cs' 'changed'
}
$scopeRoot = Join-Path $evidencePath 'anthropic-scope-controls'
New-Item -ItemType Directory -Path $scopeRoot | Out-Null
$controlLog = Join-Path $scopeRoot 'inert-preparation.control.txt'
[IO.File]::WriteAllText($controlLog, 'Inert control only; no restore/build occurred.')
$logPin = @{ path = $controlLog; bytes = (Get-Item -LiteralPath $controlLog).Length;
    sha256 = (Get-FileHash -LiteralPath $controlLog -Algorithm SHA256).Hash.ToLowerInvariant() }
function New-AnthropicScopeControl {
    $target = $registration.targets | Where-Object id -CEQ 'anthropic-simple'
    $directory = $target.entryAssembly.Substring(0, $target.entryAssembly.LastIndexOf('/'))
    $products = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $assemblies = @('PiSharp.AnthropicSimple.Tests', 'PiSharp.AI', 'PiSharp.Agent', 'PiSharp.Contracts') | ForEach-Object {
        $relative = "$directory/$_.dll"
        $pin = @{ name = $_; relative = $relative; path = (Join-Path $scopeRoot $relative); bytes = 1; sha256 = '0' * 64;
            informationalVersion = 'control-only+' + $source.candidate }
        $products.Add($relative, $pin)
        $pin
    }
    $solutionPin = $source.files | Where-Object relative -CEQ 'PiSharp.slnx'
    $scope = @{ schemaVersion = 1; candidate = $source.candidate; tree = $source.tree; reviewedRepositoryRoot = $scopeRoot;
        project = $target.project; entryAssembly = $target.entryAssembly; outputRoot = $directory; assemblies = @($assemblies); host = $logPin;
        lockedRestore = @{ verified = $true; stepId = 'locked-offline-restore'; project = $target.project; projectSha256 = $target.projectSha256;
            lockFile = @{ relative = $target.lockFile.path; bytes = $target.lockFile.bytes; sha256 = $target.lockFile.sha256 }; log = $logPin };
        solutionBuild = @{ verified = $true; stepId = 'full-solution-build'; solution = 'PiSharp.slnx'; solutionSha256 = $solutionPin.sha256; log = $logPin } }
    $receipt = @{ anthropicSimple = $scope; host = $logPin;
        actualPreparationSteps = @(@{ id = 'locked-offline-restore'; exit = 0; originalChildReturned = $true; log = $logPin },
            @{ id = 'full-solution-build'; exit = 0; originalChildReturned = $true; log = $logPin }); admissionControlOnly = $true }
    # This synthetic scope omits source closure, all products and the original
    # 24 preparation steps. It cannot pass Get-NativePreparedValidation.
    return @{ receipt = ($receipt | ConvertTo-Json -Depth 30 | ConvertFrom-Json); products = $products }
}
Invoke-SelectionControl '34-exact-four-assembly-scope-control' '' {
    $control = New-AnthropicScopeControl
    Assert-NativeAnthropicSimplePreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}
foreach ($spec in @(
    @{ id = '35-missing-anthropic-scope'; mutate = { param($c) $c.receipt.anthropicSimple = $null } },
    @{ id = '36-missing-scoped-assembly'; mutate = { param($c) $c.receipt.anthropicSimple.assemblies = @($c.receipt.anthropicSimple.assemblies | Select-Object -First 3) } },
    @{ id = '37-wrong-scoped-root'; mutate = { param($c) $c.receipt.anthropicSimple.outputRoot = 'tests/Wrong/bin/Release/net10.0' } },
    @{ id = '38-changed-scoped-assembly-hash'; mutate = { param($c) $c.receipt.anthropicSimple.assemblies[0].sha256 = '1' * 64 } },
    @{ id = '39-changed-scoped-lock'; mutate = { param($c) $c.receipt.anthropicSimple.lockedRestore.lockFile.sha256 = '1' * 64 } },
    @{ id = '40-unsettled-original-restore'; mutate = { param($c) $c.receipt.actualPreparationSteps[0].originalChildReturned = $false } },
    @{ id = '41-failed-original-build'; mutate = { param($c) $c.receipt.actualPreparationSteps[1].exit = 1 } },
    @{ id = '42-changed-scoped-host'; mutate = { param($c) $c.receipt.anthropicSimple.host.sha256 = '1' * 64 } },
    @{ id = '43-duplicate-scoped-assembly'; mutate = { param($c) $c.receipt.anthropicSimple.assemblies[1] = $c.receipt.anthropicSimple.assemblies[0] } }
)) {
    Invoke-SelectionControl $spec.id 'ANTHROPIC-SIMPLE-SCOPE:' {
        $control = New-AnthropicScopeControl
        & $spec.mutate $control
        Assert-NativeAnthropicSimplePreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
    }
}
function New-AuthenticationScopeControl {
    $target = $registration.targets | Where-Object id -CEQ 'authentication'
    $directory = $target.entryAssembly.Substring(0, $target.entryAssembly.LastIndexOf('/'))
    $products = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $assemblies = @('PiSharp.Authentication.Tests', 'PiSharp.AI', 'PiSharp.Contracts') | ForEach-Object {
        $relative = "$directory/$_.dll"
        $pin = @{ name = $_; relative = $relative; path = (Join-Path $scopeRoot $relative); bytes = 1; sha256 = '0' * 64;
            informationalVersion = 'control-only+' + $source.candidate }
        $products.Add($relative, $pin)
        $pin
    }
    $solutionPin = $source.files | Where-Object relative -CEQ 'PiSharp.slnx'
    $scope = @{ schemaVersion = 1; candidate = $source.candidate; tree = $source.tree; reviewedRepositoryRoot = $scopeRoot;
        project = $target.project; entryAssembly = $target.entryAssembly; outputRoot = $directory; assemblies = @($assemblies); host = $logPin;
        lockedRestore = @{ verified = $true; stepId = 'locked-offline-restore'; project = $target.project; projectSha256 = $target.projectSha256;
            lockFile = @{ relative = $target.lockFile.path; bytes = $target.lockFile.bytes; sha256 = $target.lockFile.sha256 }; log = $logPin };
        solutionBuild = @{ verified = $true; stepId = 'full-solution-build'; solution = 'PiSharp.slnx'; solutionSha256 = $solutionPin.sha256; log = $logPin } }
    $receipt = @{ authentication = $scope; host = $logPin;
        actualPreparationSteps = @(@{ id = 'locked-offline-restore'; exit = 0; originalChildReturned = $true; log = $logPin },
            @{ id = 'full-solution-build'; exit = 0; originalChildReturned = $true; log = $logPin }); admissionControlOnly = $true }
    # This synthetic scope omits source closure, all products and the original
    # 24 preparation steps. It cannot pass Get-NativePreparedValidation.
    return @{ receipt = ($receipt | ConvertTo-Json -Depth 30 | ConvertFrom-Json); products = $products }
}
Invoke-SelectionControl '44-authentication-selection' '' {
    $plan = Get-NativeCompanionSelection -Registration $registration -TargetId 'authentication'
    Assert-Control ($plan.targets.Count -eq 1 -and $plan.selectedIds[0] -ceq 'authentication' -and
        $plan.unselectedIds.Count -eq 31) 'Authentication selection includes prior companions.'
}
Invoke-SelectionControl '45-authentication-root-added-only' '' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    $historical = [pscustomobject]@{ targets = @($registration.targets | Where-Object id -CNE 'authentication') }
    $before = @(Get-NativePreparedArtifactRoots -Registration $historical)
    Assert-Control ($roots.Count -eq 54 -and $before.Count -eq 53 -and
        (@($roots | Where-Object { $_ -notin $before }) -join ',') -ceq 'tests/PiSharp.Authentication.Tests/bin/Release/net10.0') 'Auth changed an existing root or included a different target.'
}
Invoke-SelectionControl '46-missing-authentication-id' 'The full native gate requires' {
    Invoke-RegistryMutation { param($copy) $copy.targets = @($copy.targets | Where-Object id -CNE 'authentication') }
}
Invoke-SelectionControl '47-duplicate-authentication-id' 'The full native gate requires' {
    Invoke-RegistryMutation { param($copy) $copy.targets += @($copy.targets | Where-Object id -CEQ 'authentication') }
}
Invoke-SelectionControl '48-authentication-argument-order' 'Authentication requires --report' {
    Invoke-RegistryMutation { param($copy) $target = $copy.targets | Where-Object id -CEQ 'authentication'; $target.arguments = @($target.arguments[1], $target.arguments[0]) }
}
Invoke-SelectionControl '49-changed-authentication-project' 'Registered companion project changed:' {
    Invoke-RegistryMutation { param($copy) } 'tests/PiSharp.Authentication.Tests/PiSharp.Authentication.Tests.csproj' 'changed'
}
Invoke-SelectionControl '50-changed-authentication-lock' 'Registered companion lockfile changed:' {
    Invoke-RegistryMutation { param($copy) } 'tests/PiSharp.Authentication.Tests/packages.lock.json' 'changed'
}
Invoke-SelectionControl '51-changed-authentication-inventory' 'AUTHENTICATION-SOURCE:' {
    Invoke-RegistryMutation { param($copy) } 'src/PiSharp.AI/Authentication/source-inventory.json' 'changed'
}
Invoke-SelectionControl '52-missing-authentication-source-pin' 'AUTHENTICATION-SOURCE:' {
    $copy = $source | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.files = @($copy.files | Where-Object relative -CNE 'src/PiSharp.AI/Authentication/InjectedAuthenticationResolver.cs')
    Assert-NativeAuthenticationSourcePins -Repo $Repo -Source $copy
}
Invoke-SelectionControl '53-duplicate-authentication-source-pin' 'AUTHENTICATION-SOURCE:' {
    $copy = $source | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.files += @($copy.files | Where-Object relative -CEQ 'src/PiSharp.AI/Authentication/ProviderEnvironmentSnapshot.cs')
    Assert-NativeAuthenticationSourcePins -Repo $Repo -Source $copy
}
Invoke-SelectionControl '54-missing-authentication-output-root' 'NATIVE-PREPARED-ROOTS:' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    Assert-NativePreparedRootDeclaration -RequiredRoots $roots -DeclaredRoots @($roots | Where-Object { $_ -cne 'tests/PiSharp.Authentication.Tests/bin/Release/net10.0' })
}
Invoke-SelectionControl '55-replaced-authentication-output-root' 'NATIVE-PREPARED-ROOTS:' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    $declared = @($roots | Where-Object { $_ -cne 'tests/PiSharp.Authentication.Tests/bin/Release/net10.0' }) + 'tests/Unadmitted/bin/Release/net10.0'
    Assert-NativePreparedRootDeclaration -RequiredRoots $roots -DeclaredRoots $declared
}
Invoke-SelectionControl '56-exact-three-assembly-authentication-scope-control' '' {
    $control = New-AuthenticationScopeControl
    Assert-NativeAuthenticationPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}
foreach ($spec in @(
    @{ id = '57-missing-authentication-scope'; mutate = { param($c) $c.receipt.authentication = $null } },
    @{ id = '58-missing-authentication-scoped-assembly'; mutate = { param($c) $c.receipt.authentication.assemblies = @($c.receipt.authentication.assemblies | Select-Object -First 2) } },
    @{ id = '59-wrong-authentication-scoped-root'; mutate = { param($c) $c.receipt.authentication.outputRoot = 'tests/Wrong/bin/Release/net10.0' } },
    @{ id = '60-changed-authentication-scoped-hash'; mutate = { param($c) $c.receipt.authentication.assemblies[0].sha256 = '1' * 64 } },
    @{ id = '61-changed-authentication-scoped-lock'; mutate = { param($c) $c.receipt.authentication.lockedRestore.lockFile.sha256 = '1' * 64 } },
    @{ id = '62-authentication-unsettled-original-restore'; mutate = { param($c) $c.receipt.actualPreparationSteps[0].originalChildReturned = $false } },
    @{ id = '63-authentication-failed-original-build'; mutate = { param($c) $c.receipt.actualPreparationSteps[1].exit = 1 } },
    @{ id = '64-changed-authentication-scoped-host'; mutate = { param($c) $c.receipt.authentication.host.sha256 = '1' * 64 } },
    @{ id = '65-duplicate-authentication-scoped-assembly'; mutate = { param($c) $c.receipt.authentication.assemblies[1] = $c.receipt.authentication.assemblies[0] } },
    @{ id = '66-wrong-authentication-scope-candidate'; mutate = { param($c) $c.receipt.authentication.candidate = '0' * 40 } },
    @{ id = '67-missing-authentication-scoped-product'; mutate = { param($c) $c.products.Remove($c.receipt.authentication.assemblies[0].relative) | Out-Null } },
    @{ id = '68-wrong-authentication-scoped-project'; mutate = { param($c) $c.receipt.authentication.project = 'tests/Wrong/Wrong.csproj' } },
    @{ id = '69-extra-authentication-scoped-assembly'; mutate = { param($c) $c.receipt.authentication.assemblies += @($c.receipt.authentication.assemblies[0]) } }
)) {
    Invoke-SelectionControl $spec.id 'AUTHENTICATION-SCOPE:' {
        $control = New-AuthenticationScopeControl
        & $spec.mutate $control
        Assert-NativeAuthenticationPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
    }
}
Invoke-SelectionControl '70-changed-authentication-source-bytes' 'AUTHENTICATION-SOURCE:' {
    Invoke-RegistryMutation { param($copy) } 'src/PiSharp.AI/Authentication/InjectedAuthenticationResolver.cs' 'changed'
}
# Independently authored focused-partition admission controls. These prove only
# declaration rejection, never runtime ownership, original-selector or full-gate acceptance.
Invoke-SelectionControl '71-focused-selection-and-partial-receipt' '' {
    $plan = Get-NativeCompanionSelection -Registration $registration -TargetId 'terminal-ownership-focused'
    $rows = @(New-NativeCompanionReceiptRows -Registration $registration -Selection $plan)
    Assert-Control ($plan.targets.Count -eq 1 -and $plan.unselectedIds.Count -eq 31 -and
        @($rows | Where-Object partialSelectorOwnership).Count -eq 1 -and
        @($rows | Where-Object { $_.partialSelectorOwnership -and $_.selected -and $_.status -ceq 'UNEXECUTED' }).Count -eq 1) 'Focused selection/partial receipt changed.'
}
Invoke-SelectionControl '72-focused-root-added-only' '' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    $prior = [pscustomobject]@{ targets = @($registration.targets | Where-Object id -CNE 'terminal-ownership-focused') }
    $before = @(Get-NativePreparedArtifactRoots -Registration $prior)
    Assert-Control ($roots.Count -eq 54 -and $before.Count -eq 53 -and
        (@($roots | Where-Object { $_ -notin $before }) -join ',') -ceq 'tests/PiSharp.Terminal.Ownership.Tests/bin/Release/net10.0') 'Focused closure changed prior roots.'
}
Invoke-SelectionControl '73-missing-focused-target' 'The full native gate requires' {
    Invoke-RegistryMutation { param($copy) $copy.targets = @($copy.targets | Where-Object id -CNE 'terminal-ownership-focused') }
}
Invoke-SelectionControl '74-duplicate-focused-target' 'The full native gate requires' {
    Invoke-RegistryMutation { param($copy) $copy.targets += @($copy.targets | Where-Object id -CEQ 'terminal-ownership-focused') }
}
Invoke-SelectionControl '75-focused-plan-pin-changed' 'Pinned companion fixture changed:' {
    Invoke-RegistryMutation { param($copy) } 'tests/PiSharp.Terminal.Ownership.Tests/focused-case-plan.json' 'changed'
}
Invoke-SelectionControl '76-focused-source-pin-missing' 'SELECTOR-OWNERSHIP:' {
    $copy = $source | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.files = @($copy.files | Where-Object relative -CNE 'tests/PiSharp.Terminal.Ownership.Tests/Program.cs')
    Assert-NativeSelectorOwnershipSource -Repo $Repo -Source $copy
}
foreach ($spec in @(
    @{ id = '77-focused-plan-reordered'; mutate = { param($p) $first = $p.selectedCaseIds[0]; $p.selectedCaseIds[0] = $p.selectedCaseIds[1]; $p.selectedCaseIds[1] = $first } },
    @{ id = '78-focused-plan-duplicate'; mutate = { param($p) $p.selectedCaseIds[1] = $p.selectedCaseIds[0] } },
    @{ id = '79-focused-plan-full-selector-pass'; mutate = { param($p) $p.fullSelectorSuitePassed = $true } },
    @{ id = '80-focused-plan-historical-pass'; mutate = { param($p) $p.historicalFailure.passed = $true } },
    @{ id = '81-focused-plan-exclusion-overlap'; mutate = { param($p) $p.excludedOriginalCaseIds[0] = $p.selectedCaseIds[0] } },
    @{ id = '82-focused-plan-child-alias'; mutate = { param($p) $p.ownedChildEntryAssembly = 'tests/PiSharp.Terminal.Ownership.Tests/bin/Release/net10.0/PiSharp.CodingAgent.Tests.dll' } }
)) {
    Invoke-SelectionControl $spec.id 'SELECTOR-OWNERSHIP:' {
        $plan = Get-Content -LiteralPath (Join-Path $Repo 'tests/PiSharp.Terminal.Ownership.Tests/focused-case-plan.json') -Raw | ConvertFrom-Json
        & $spec.mutate $plan
        Assert-NativeSelectorOwnershipSelection -Repo $Repo -Plan $plan
    }
}
function New-SelectorOwnershipScopeControl {
    $products = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $root = 'tests/PiSharp.CodingAgent.Tests/bin/Release/net10.0'
    $pins = @('PiSharp.CodingAgent.Tests.dll', 'PiSharp.CodingAgent.dll') | ForEach-Object {
        $pin = @{ relative = "$root/$_"; bytes = 1; sha256 = '0' * 64 }
        $products.Add($pin.relative, $pin); $pin
    }
    $selectionPath = 'tests/PiSharp.Terminal.Ownership.Tests/focused-case-plan.json'
    $pin = $source.files | Where-Object relative -CEQ $selectionPath
    $receipt = @{ selectorOwnership = @{
        selection = @{ sourceRelative = $selectionPath; sourceSha256 = $pin.sha256; selectedOriginalCases = 31; originalSelectorCases = 140; partialResultOnly = $true }
        ownedChild = @{ outputRoot = $root; entryAssembly = "$root/PiSharp.CodingAgent.Tests.dll"; products = @($pins) }
    }; admissionControlOnly = $true }
    # Deliberately lacks complete products, scopes and preparation steps.
    return @{ receipt = ($receipt | ConvertTo-Json -Depth 30 | ConvertFrom-Json); products = $products }
}
Invoke-SelectionControl '83-focused-exact-selection-sidecar-control' '' {
    $control = New-SelectorOwnershipScopeControl
    Assert-NativeSelectorOwnershipScope -Repo $Repo -Source $source -Receipt $control.receipt -Products $control.products
}
foreach ($spec in @(
    @{ id = '84-focused-scope-missing'; mutate = { param($c) $c.receipt.selectorOwnership = $null } },
    @{ id = '85-focused-full-result-flag'; mutate = { param($c) $c.receipt.selectorOwnership.selection.partialResultOnly = $false } },
    @{ id = '86-focused-selection-hash-changed'; mutate = { param($c) $c.receipt.selectorOwnership.selection.sourceSha256 = '1' * 64 } },
    @{ id = '87-focused-child-sidecar-missing'; mutate = { param($c) $c.receipt.selectorOwnership.ownedChild = $null } },
    @{ id = '88-focused-child-alias-scope'; mutate = { param($c) $c.receipt.selectorOwnership.ownedChild.entryAssembly = 'tests/PiSharp.Terminal.Ownership.Tests/bin/Release/net10.0/PiSharp.CodingAgent.Tests.dll' } },
    @{ id = '89-focused-child-product-missing'; mutate = { param($c) $c.receipt.selectorOwnership.ownedChild.products = @($c.receipt.selectorOwnership.ownedChild.products | Select-Object -First 1) } },
    @{ id = '90-focused-child-product-hash-changed'; mutate = { param($c) $c.receipt.selectorOwnership.ownedChild.products[0].sha256 = '1' * 64 } },
    @{ id = '91-focused-child-product-duplicate'; mutate = { param($c) $c.receipt.selectorOwnership.ownedChild.products[1] = $c.receipt.selectorOwnership.ownedChild.products[0] } }
)) {
    Invoke-SelectionControl $spec.id 'SELECTOR-OWNERSHIP-SCOPE:' {
        $control = New-SelectorOwnershipScopeControl
        & $spec.mutate $control
        Assert-NativeSelectorOwnershipScope -Repo $Repo -Source $source -Receipt $control.receipt -Products $control.products
    }
}
# Full current root-set admission controls. All are source-only until allocated;
# they cannot launch SDKs, products, Node workers or an execution provider.
Invoke-SelectionControl '92-full-exact-51-root-set' '' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    $prior = [pscustomobject]@{ targets = @($registration.targets | Where-Object id -CNE 'node-loadout-metadata') }
    $before = @(Get-NativePreparedArtifactRoots -Registration $prior)
    Assert-Control ($roots.Count -eq 54 -and $before.Count -eq 53 -and
        (@($roots | Where-Object { $_ -cnotin $before }) -join ',') -ceq
        'tools/NodeCommandInputBridge/NativeAdmissionTests/bin/Release/net10.0') 'Current root set changed an original root or added another root.'
    $declared = @($roots); [Array]::Reverse($declared)
    Assert-NativePreparedRootDeclaration -RequiredRoots $roots -DeclaredRoots $declared
}
Invoke-SelectionControl '93-missing-node-metadata-root' 'NATIVE-PREPARED-ROOTS:' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    Assert-NativePreparedRootDeclaration -RequiredRoots $roots -DeclaredRoots @($roots | Where-Object {
        $_ -cne 'tools/NodeCommandInputBridge/NativeAdmissionTests/bin/Release/net10.0' })
}
Invoke-SelectionControl '94-duplicate-declared-root-at-count51' 'NATIVE-PREPARED-ROOTS:' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    $declared = @($roots); $declared[0] = $declared[1]
    Assert-NativePreparedRootDeclaration -RequiredRoots $roots -DeclaredRoots $declared
}
Invoke-SelectionControl '95-substituted-declared-root-at-count51' 'NATIVE-PREPARED-ROOTS:' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    $declared = @($roots); $declared[0] = 'tests/Unadmitted.Namespace.Draft/bin/Release/net10.0'
    Assert-NativePreparedRootDeclaration -RequiredRoots $roots -DeclaredRoots $declared
}
Invoke-SelectionControl '96-missing-required-root' 'NATIVE-PREPARED-ROOTS:' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    $required = @($roots | Select-Object -Skip 1)
    Assert-NativePreparedRootDeclaration -RequiredRoots $required -DeclaredRoots $roots
}
Invoke-SelectionControl '97-equal-duplicate-required-and-declared-sets' 'NATIVE-PREPARED-ROOTS:' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    $duplicated = @($roots); $duplicated[0] = $duplicated[1]
    Assert-NativePreparedRootDeclaration -RequiredRoots $duplicated -DeclaredRoots $duplicated
}
Invoke-SelectionControl '98-extra-declared-root' 'NATIVE-PREPARED-ROOTS:' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    Assert-NativePreparedRootDeclaration -RequiredRoots $roots -DeclaredRoots @($roots + 'tests/Unadmitted.Namespace.Draft/bin/Release/net10.0')
}
Invoke-SelectionControl '99-node-metadata-only-selection' '' {
    $plan = Get-NativeCompanionSelection -Registration $registration -TargetId 'node-loadout-metadata'
    Assert-Control ($plan.targets.Count -eq 1 -and $plan.unselectedIds.Count -eq 31 -and
        ($plan.targets[0].arguments.kind -join ',') -ceq 'literal,literal,report' -and
        $plan.targets[0].arguments[0].value -ceq '--metadata-only') 'Node metadata selection expanded worker authority.'
}
Invoke-SelectionControl '100-node-metadata-worker-argument-rejected' 'Node loadout metadata admission' {
    Invoke-RegistryMutation { param($copy)
        $target = $copy.targets | Where-Object id -CEQ 'node-loadout-metadata'
        $target.arguments[0].value = '--node'
    }
}
function New-AzureResponsesScopeControl {
    $target = $registration.targets | Where-Object id -CEQ 'azure-responses'
    $directory = $target.entryAssembly.Substring(0, $target.entryAssembly.LastIndexOf('/'))
    $products = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $assemblies = @('PiSharp.AzureResponses.Tests', 'PiSharp.AI', 'PiSharp.Agent', 'PiSharp.Contracts') | ForEach-Object {
        $relative = "$directory/$_.dll"
        $pin = @{ name = $_; relative = $relative; path = (Join-Path $scopeRoot $relative); bytes = 1; sha256 = '0' * 64;
            informationalVersion = 'control-only+' + $source.candidate }
        $products.Add($relative, $pin)
        $pin
    }
    $solutionPin = $source.files | Where-Object relative -CEQ 'PiSharp.slnx'
    $scope = @{ schemaVersion = 1; candidate = $source.candidate; tree = $source.tree; reviewedRepositoryRoot = $scopeRoot;
        project = $target.project; entryAssembly = $target.entryAssembly; outputRoot = $directory; assemblies = @($assemblies); host = $logPin;
        lockedRestore = @{ verified = $true; stepId = 'locked-offline-restore'; project = $target.project; projectSha256 = $target.projectSha256;
            lockFile = @{ relative = $target.lockFile.path; bytes = $target.lockFile.bytes; sha256 = $target.lockFile.sha256 }; log = $logPin };
        solutionBuild = @{ verified = $true; stepId = 'full-solution-build'; solution = 'PiSharp.slnx'; solutionSha256 = $solutionPin.sha256; log = $logPin } }
    $receipt = @{ azureResponses = $scope; host = $logPin;
        actualPreparationSteps = @(@{ id = 'locked-offline-restore'; exit = 0; originalChildReturned = $true; log = $logPin },
            @{ id = 'full-solution-build'; exit = 0; originalChildReturned = $true; log = $logPin }); admissionControlOnly = $true }
    # This synthetic scope omits source closure, all products and the original
    # 24 preparation steps. It cannot pass Get-NativePreparedValidation.
    return @{ receipt = ($receipt | ConvertTo-Json -Depth 30 | ConvertFrom-Json); products = $products }
}
function New-MistralTextScopeControl {
    $target = $registration.targets | Where-Object id -CEQ 'mistral-text'
    $directory = $target.entryAssembly.Substring(0, $target.entryAssembly.LastIndexOf('/'))
    $products = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $assemblies = @('PiSharp.MistralConversations.Tests', 'PiSharp.AI', 'PiSharp.Contracts') | ForEach-Object {
        $relative = "$directory/$_.dll"
        $pin = @{ name = $_; relative = $relative; path = (Join-Path $scopeRoot $relative); bytes = 1; sha256 = '0' * 64;
            informationalVersion = 'control-only+' + $source.candidate }
        $products.Add($relative, $pin)
        $pin
    }
    $solutionPin = $source.files | Where-Object relative -CEQ 'PiSharp.slnx'
    $scope = @{ schemaVersion = 1; candidate = $source.candidate; tree = $source.tree; reviewedRepositoryRoot = $scopeRoot;
        project = $target.project; entryAssembly = $target.entryAssembly; outputRoot = $directory; assemblies = @($assemblies); host = $logPin;
        lockedRestore = @{ verified = $true; stepId = 'locked-offline-restore'; project = $target.project; projectSha256 = $target.projectSha256;
            lockFile = @{ relative = $target.lockFile.path; bytes = $target.lockFile.bytes; sha256 = $target.lockFile.sha256 }; log = $logPin };
        solutionBuild = @{ verified = $true; stepId = 'full-solution-build'; solution = 'PiSharp.slnx'; solutionSha256 = $solutionPin.sha256; log = $logPin } }
    $receipt = @{ mistralText = $scope; host = $logPin;
        actualPreparationSteps = @(@{ id = 'locked-offline-restore'; exit = 0; originalChildReturned = $true; log = $logPin },
            @{ id = 'full-solution-build'; exit = 0; originalChildReturned = $true; log = $logPin }); admissionControlOnly = $true }
    # This synthetic scope omits source closure, all products and the original
    # 24 preparation steps. It cannot pass Get-NativePreparedValidation.
    return @{ receipt = ($receipt | ConvertTo-Json -Depth 30 | ConvertFrom-Json); products = $products }
}
# New provider controls are appended after all 100 current historical controls.
# Synthetic scope controls are declaration tests, never build/runtime evidence.
Invoke-SelectionControl '101-provider-append-order-and-54-roots' '' {
    $plan = Get-NativeCompanionSelection -Registration $registration -TargetId @('mistral-text', 'azure-responses')
    Assert-Control (($plan.selectedIds -join ',') -ceq 'azure-responses,mistral-text' -and $plan.unselectedIds.Count -eq 30) 'Provider selection order changed.'
    $prior = [pscustomobject]@{ targets = @($registration.targets | Where-Object { $_.id -cnotin @('azure-responses', 'mistral-text') }) }
    $before = @(Get-NativePreparedArtifactRoots -Registration $prior)
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    Assert-Control ($before.Count -eq 52 -and $roots.Count -eq 54 -and
        (@($roots | Where-Object { $_ -notin $before }) -join ',') -ceq 'tests/PiSharp.AzureResponses.Tests/bin/Release/net10.0,tests/PiSharp.MistralConversations.Tests/bin/Release/net10.0') 'Provider roots changed historical closure.'
}
Invoke-SelectionControl '102-provider-registry-reorder-rejected' 'The full native gate requires' {
    Invoke-RegistryMutation { param($copy) $tmp = $copy.targets[-1]; $copy.targets[-1] = $copy.targets[-2]; $copy.targets[-2] = $tmp }
}
Invoke-SelectionControl '103-azure-responses-exact-selection-and-never-run-rows' '' {
    $plan = Get-NativeCompanionSelection -Registration $registration -TargetId 'azure-responses'
    $rows = @(New-NativeCompanionReceiptRows -Registration $registration -Selection $plan)
    Assert-Control ($plan.targets.Count -eq 1 -and $plan.unselectedIds.Count -eq 31 -and
        $rows.Count -eq 32 -and @($rows | Where-Object { $_.selected -and $_.status -ceq 'UNEXECUTED' -and -not $_.passingReceipt }).Count -eq 1 -and
        @($rows | Where-Object { -not $_.selected -and ($_.status -cne 'UNSELECTED_NEVER_RUN' -or $_.passingReceipt -or $_.pid) }).Count -eq 0) 'Provider selection fabricated admission or execution.'
}
Invoke-SelectionControl '104-azure-responses-missing-target' 'The full native gate requires' {
    Invoke-RegistryMutation { param($copy) $copy.targets = @($copy.targets | Where-Object id -CNE 'azure-responses') }
}

Invoke-SelectionControl '105-azure-responses-duplicate-target' 'The full native gate requires' {
    Invoke-RegistryMutation { param($copy) $copy.targets += @($copy.targets | Where-Object id -CEQ 'azure-responses') }
}

Invoke-SelectionControl '106-azure-responses-argument-order' 'AZURE-RESPONSES-ARGUMENTS:' {
    Invoke-RegistryMutation { param($copy) $t = $copy.targets | Where-Object id -CEQ 'azure-responses'; $t.arguments = @($t.arguments[1], $t.arguments[0]) }
}

Invoke-SelectionControl '107-azure-responses-wrong-output' 'AZURE-RESPONSES-TARGET:' {
    Invoke-RegistryMutation { param($copy) ($copy.targets | Where-Object id -CEQ 'azure-responses').entryAssembly = 'tests/Wrong/bin/Release/net10.0/Wrong.dll' }
}

Invoke-SelectionControl '108-azure-responses-changed-deadline' 'AZURE-RESPONSES-TARGET:' {
    Invoke-RegistryMutation { param($copy) ($copy.targets | Where-Object id -CEQ 'azure-responses').timeoutSeconds = 1 }
}

Invoke-SelectionControl '109-azure-responses-changed-lock-declaration' 'AZURE-RESPONSES-TARGET:' {
    Invoke-RegistryMutation { param($copy) ($copy.targets | Where-Object id -CEQ 'azure-responses').lockFile.sha256 = '0' * 64 }
}

Invoke-SelectionControl '110-azure-responses-changed-project-bytes' 'Registered companion project changed:' {
    Invoke-RegistryMutation { param($copy) } 'tests/PiSharp.AzureResponses.Tests/PiSharp.AzureResponses.Tests.csproj' 'changed'
}

Invoke-SelectionControl '111-azure-responses-changed-lock-bytes' 'Registered companion lockfile changed:' {
    Invoke-RegistryMutation { param($copy) } 'tests/PiSharp.AzureResponses.Tests/packages.lock.json' 'changed'
}

Invoke-SelectionControl '112-azure-responses-changed-inventory-bytes' 'Pinned companion fixture changed:' {
    Invoke-RegistryMutation { param($copy) } 'tests/PiSharp.AzureResponses.Tests/source-inventory.json' 'changed'
}

Invoke-SelectionControl '113-azure-responses-changed-provider-source' 'AZURE-RESPONSES-SOURCE:' {
    Invoke-RegistryMutation { param($copy) } 'src/PiSharp.AI/Protocols/AzureResponses/AzureResponsesOptions.cs' 'changed'
}

Invoke-SelectionControl '114-azure-responses-missing-source-pin' 'AZURE-RESPONSES-SOURCE:' {
    $copy = $source | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.files = @($copy.files | Where-Object relative -CNE 'src/PiSharp.AI/Protocols/AzureResponses/AzureResponsesOptions.cs')
    Assert-NativeAzureResponsesSourcePins -Repo $Repo -Source $copy
}

Invoke-SelectionControl '115-azure-responses-duplicate-source-pin' 'AZURE-RESPONSES-SOURCE:' {
    $copy = $source | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.files += @($copy.files | Where-Object relative -CEQ 'src/PiSharp.AI/Protocols/AzureResponses/AzureResponsesOptions.cs')
    Assert-NativeAzureResponsesSourcePins -Repo $Repo -Source $copy
}

Invoke-SelectionControl '116-azure-responses-changed-source-pin' 'AZURE-RESPONSES-SOURCE:' {
    $copy = $source | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    ($copy.files | Where-Object relative -CEQ 'src/PiSharp.AI/Protocols/AzureResponses/AzureResponsesOptions.cs').sha256 = '0' * 64
    Assert-NativeAzureResponsesSourcePins -Repo $Repo -Source $copy
}

Invoke-SelectionControl '117-azure-responses-missing-output-root' 'NATIVE-PREPARED-ROOTS:' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    $declared = @($roots | Where-Object { $_ -cne 'tests/PiSharp.AzureResponses.Tests/bin/Release/net10.0' })
    Assert-NativePreparedRootDeclaration -RequiredRoots $roots -DeclaredRoots $declared
}

Invoke-SelectionControl '118-azure-responses-replaced-output-root' 'NATIVE-PREPARED-ROOTS:' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    $declared = @($roots | Where-Object { $_ -cne 'tests/PiSharp.AzureResponses.Tests/bin/Release/net10.0' }) + 'tests/Unadmitted/bin/Release/net10.0'
    Assert-NativePreparedRootDeclaration -RequiredRoots $roots -DeclaredRoots $declared
}

Invoke-SelectionControl '119-azure-responses-exact-4-assembly-scope' '' {
    $control = New-AzureResponsesScopeControl
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '120-azure-responses-missing-scope' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.azureResponses = $null } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '121-azure-responses-missing-assembly' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.azureResponses.assemblies = @($c.receipt.azureResponses.assemblies | Select-Object -First 3) } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '122-azure-responses-extra-assembly' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.azureResponses.assemblies += @($c.receipt.azureResponses.assemblies[0]) } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '123-azure-responses-duplicate-assembly' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.azureResponses.assemblies[1] = $c.receipt.azureResponses.assemblies[0] } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '124-azure-responses-wrong-root' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.azureResponses.outputRoot = 'tests/Wrong/bin/Release/net10.0' } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '125-azure-responses-wrong-candidate' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.azureResponses.candidate = '0' * 40 } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '126-azure-responses-wrong-tree' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.azureResponses.tree = '0' * 40 } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '127-azure-responses-wrong-repository-root' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.azureResponses.reviewedRepositoryRoot = Join-Path $scopeRoot 'wrong' } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '128-azure-responses-wrong-project' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.azureResponses.project = 'tests/Wrong/Wrong.csproj' } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '129-azure-responses-changed-hash' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.azureResponses.assemblies[0].sha256 = '1' * 64 } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '130-azure-responses-missing-product' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.products.Remove($c.receipt.azureResponses.assemblies[0].relative) | Out-Null } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '131-azure-responses-wrong-informational-version' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.azureResponses.assemblies[0].informationalVersion = 'wrong' } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '132-azure-responses-changed-lock' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.azureResponses.lockedRestore.lockFile.sha256 = '1' * 64 } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '133-azure-responses-changed-solution' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.azureResponses.solutionBuild.solutionSha256 = '1' * 64 } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '134-azure-responses-unsettled-restore' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.actualPreparationSteps[0].originalChildReturned = $false } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '135-azure-responses-failed-build' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.actualPreparationSteps[1].exit = 1 } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '136-azure-responses-changed-log' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.azureResponses.solutionBuild.log.sha256 = '1' * 64 } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '137-azure-responses-changed-host' 'AZURE-RESPONSES-SCOPE:' {
    $control = New-AzureResponsesScopeControl
    & { param($c) $c.receipt.azureResponses.host.sha256 = '1' * 64 } $control
    Assert-NativeAzureResponsesPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '138-mistral-text-exact-selection-and-never-run-rows' '' {
    $plan = Get-NativeCompanionSelection -Registration $registration -TargetId 'mistral-text'
    $rows = @(New-NativeCompanionReceiptRows -Registration $registration -Selection $plan)
    Assert-Control ($plan.targets.Count -eq 1 -and $plan.unselectedIds.Count -eq 31 -and
        $rows.Count -eq 32 -and @($rows | Where-Object { $_.selected -and $_.status -ceq 'UNEXECUTED' -and -not $_.passingReceipt }).Count -eq 1 -and
        @($rows | Where-Object { -not $_.selected -and ($_.status -cne 'UNSELECTED_NEVER_RUN' -or $_.passingReceipt -or $_.pid) }).Count -eq 0) 'Provider selection fabricated admission or execution.'
}
Invoke-SelectionControl '139-mistral-text-missing-target' 'The full native gate requires' {
    Invoke-RegistryMutation { param($copy) $copy.targets = @($copy.targets | Where-Object id -CNE 'mistral-text') }
}

Invoke-SelectionControl '140-mistral-text-duplicate-target' 'The full native gate requires' {
    Invoke-RegistryMutation { param($copy) $copy.targets += @($copy.targets | Where-Object id -CEQ 'mistral-text') }
}

Invoke-SelectionControl '141-mistral-text-argument-order' 'MISTRAL-TEXT-ARGUMENTS:' {
    Invoke-RegistryMutation { param($copy) $t = $copy.targets | Where-Object id -CEQ 'mistral-text'; $t.arguments = @($t.arguments[1], $t.arguments[0]) }
}

Invoke-SelectionControl '142-mistral-text-wrong-output' 'MISTRAL-TEXT-TARGET:' {
    Invoke-RegistryMutation { param($copy) ($copy.targets | Where-Object id -CEQ 'mistral-text').entryAssembly = 'tests/Wrong/bin/Release/net10.0/Wrong.dll' }
}

Invoke-SelectionControl '143-mistral-text-changed-deadline' 'MISTRAL-TEXT-TARGET:' {
    Invoke-RegistryMutation { param($copy) ($copy.targets | Where-Object id -CEQ 'mistral-text').timeoutSeconds = 1 }
}

Invoke-SelectionControl '144-mistral-text-changed-lock-declaration' 'MISTRAL-TEXT-TARGET:' {
    Invoke-RegistryMutation { param($copy) ($copy.targets | Where-Object id -CEQ 'mistral-text').lockFile.sha256 = '0' * 64 }
}

Invoke-SelectionControl '145-mistral-text-changed-project-bytes' 'Registered companion project changed:' {
    Invoke-RegistryMutation { param($copy) } 'tests/PiSharp.MistralConversations.Tests/PiSharp.MistralConversations.Tests.csproj' 'changed'
}

Invoke-SelectionControl '146-mistral-text-changed-lock-bytes' 'Registered companion lockfile changed:' {
    Invoke-RegistryMutation { param($copy) } 'tests/PiSharp.MistralConversations.Tests/packages.lock.json' 'changed'
}

Invoke-SelectionControl '147-mistral-text-changed-inventory-bytes' 'Pinned companion fixture changed:' {
    Invoke-RegistryMutation { param($copy) } 'src/PiSharp.AI/Protocols/MistralConversations/source-inventory.json' 'changed'
}

Invoke-SelectionControl '148-mistral-text-changed-provider-source' 'MISTRAL-TEXT-SOURCE:' {
    Invoke-RegistryMutation { param($copy) } 'src/PiSharp.AI/Protocols/MistralConversations/MistralTextHttpSseTransport.cs' 'changed'
}

Invoke-SelectionControl '149-mistral-text-missing-source-pin' 'MISTRAL-TEXT-SOURCE:' {
    $copy = $source | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.files = @($copy.files | Where-Object relative -CNE 'src/PiSharp.AI/Protocols/MistralConversations/MistralTextHttpSseTransport.cs')
    Assert-NativeMistralTextSourcePins -Repo $Repo -Source $copy
}

Invoke-SelectionControl '150-mistral-text-duplicate-source-pin' 'MISTRAL-TEXT-SOURCE:' {
    $copy = $source | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $copy.files += @($copy.files | Where-Object relative -CEQ 'src/PiSharp.AI/Protocols/MistralConversations/MistralTextHttpSseTransport.cs')
    Assert-NativeMistralTextSourcePins -Repo $Repo -Source $copy
}

Invoke-SelectionControl '151-mistral-text-changed-source-pin' 'MISTRAL-TEXT-SOURCE:' {
    $copy = $source | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    ($copy.files | Where-Object relative -CEQ 'src/PiSharp.AI/Protocols/MistralConversations/MistralTextHttpSseTransport.cs').sha256 = '0' * 64
    Assert-NativeMistralTextSourcePins -Repo $Repo -Source $copy
}

Invoke-SelectionControl '152-mistral-text-missing-output-root' 'NATIVE-PREPARED-ROOTS:' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    $declared = @($roots | Where-Object { $_ -cne 'tests/PiSharp.MistralConversations.Tests/bin/Release/net10.0' })
    Assert-NativePreparedRootDeclaration -RequiredRoots $roots -DeclaredRoots $declared
}

Invoke-SelectionControl '153-mistral-text-replaced-output-root' 'NATIVE-PREPARED-ROOTS:' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    $declared = @($roots | Where-Object { $_ -cne 'tests/PiSharp.MistralConversations.Tests/bin/Release/net10.0' }) + 'tests/Unadmitted/bin/Release/net10.0'
    Assert-NativePreparedRootDeclaration -RequiredRoots $roots -DeclaredRoots $declared
}

Invoke-SelectionControl '154-mistral-text-exact-3-assembly-scope' '' {
    $control = New-MistralTextScopeControl
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '155-mistral-text-missing-scope' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.mistralText = $null } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '156-mistral-text-missing-assembly' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.mistralText.assemblies = @($c.receipt.mistralText.assemblies | Select-Object -First 2) } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '157-mistral-text-extra-assembly' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.mistralText.assemblies += @($c.receipt.mistralText.assemblies[0]) } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '158-mistral-text-duplicate-assembly' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.mistralText.assemblies[1] = $c.receipt.mistralText.assemblies[0] } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '159-mistral-text-wrong-root' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.mistralText.outputRoot = 'tests/Wrong/bin/Release/net10.0' } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '160-mistral-text-wrong-candidate' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.mistralText.candidate = '0' * 40 } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '161-mistral-text-wrong-tree' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.mistralText.tree = '0' * 40 } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '162-mistral-text-wrong-repository-root' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.mistralText.reviewedRepositoryRoot = Join-Path $scopeRoot 'wrong' } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '163-mistral-text-wrong-project' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.mistralText.project = 'tests/Wrong/Wrong.csproj' } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '164-mistral-text-changed-hash' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.mistralText.assemblies[0].sha256 = '1' * 64 } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '165-mistral-text-missing-product' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.products.Remove($c.receipt.mistralText.assemblies[0].relative) | Out-Null } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '166-mistral-text-wrong-informational-version' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.mistralText.assemblies[0].informationalVersion = 'wrong' } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '167-mistral-text-changed-lock' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.mistralText.lockedRestore.lockFile.sha256 = '1' * 64 } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '168-mistral-text-changed-solution' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.mistralText.solutionBuild.solutionSha256 = '1' * 64 } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '169-mistral-text-unsettled-restore' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.actualPreparationSteps[0].originalChildReturned = $false } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '170-mistral-text-failed-build' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.actualPreparationSteps[1].exit = 1 } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '171-mistral-text-changed-log' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.mistralText.solutionBuild.log.sha256 = '1' * 64 } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '172-mistral-text-changed-host' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    & { param($c) $c.receipt.mistralText.host.sha256 = '1' * 64 } $control
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '173-mistral-prior-draft-test-assembly-name-rejected' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    $control.receipt.mistralText.assemblies[0].name = 'PiSharp.MistralText.Tests'
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}
Invoke-SelectionControl '174-mistral-prior-draft-agent-assembly-rejected' 'MISTRAL-TEXT-SCOPE:' {
    $control = New-MistralTextScopeControl
    $control.receipt.mistralText.assemblies += @(@{ name = 'PiSharp.Agent'; relative = 'unexpected/PiSharp.Agent.dll' })
    Assert-NativeMistralTextPreparedScope -Repo $scopeRoot -Source $source -Receipt $control.receipt -Registration $registration -Products $control.products
}

Invoke-SelectionControl '175-terminal-fixture-published-root-required' 'NATIVE-PREPARED-ROOTS:' {
    $roots = @(Get-NativePreparedArtifactRoots -Registration $registration)
    Assert-NativePreparedRootDeclaration -RequiredRoots $roots -DeclaredRoots @($roots | Where-Object {
        $_ -cne 'artifacts/extensions/published-fixtures/terminal-companion' })
}
Invoke-SelectionControl '176-terminal-fixture-project-only-shared-contracts' '' {
    $directory = Join-Path $Repo 'tests/fixtures/extensions/native/PublishedFixture.TerminalCompanion'
    [xml]$project = Get-Content -LiteralPath (Join-Path $directory 'PublishedFixture.TerminalCompanion.csproj') -Raw
    $references = @($project.Project.ItemGroup.ProjectReference)
    Assert-Control ($references.Count -eq 2 -and
        (($references.Include | Sort-Object) -join ',') -ceq
        '../../../../../src/PiSharp.Contracts/PiSharp.Contracts.csproj,../../../../../src/PiSharp.Extensions.Abstractions/PiSharp.Extensions.Abstractions.csproj' -and
        @($references | Where-Object { $_.Private -cne 'false' -or $_.ExcludeAssets -cne 'runtime' }).Count -eq 0 -and
        $null -eq $project.Project.ItemGroup.PackageReference) 'Fixture graph admits more than the shared contracts.'
    $lock = Get-Content -LiteralPath (Join-Path $directory 'packages.lock.json') -Raw | ConvertFrom-Json
    $dependencies = @($lock.dependencies.'net10.0'.PSObject.Properties)
    Assert-Control ($dependencies.Count -eq 2 -and
        (($dependencies.Name | Sort-Object) -join ',') -ceq 'pisharp.contracts,pisharp.extensions.abstractions' -and
        @($dependencies | Where-Object { $_.Value.type -cne 'Project' }).Count -eq 0) 'Fixture lock admits an external package.'
}

Write-NativeLaunchEvidence -Path (Join-Path $evidencePath 'controls.json') -Evidence @{
    schemaVersion = 1; controls = $controls; passed = @($controls | Where-Object passed).Count;
    candidate = $source.candidate; sourceManifestSha256 = $SourceManifestSha256;
    admissionControlOnly = $true; nativeExecutions = 0; packageAcceptance = $false; phaseAcceptance = $false }
