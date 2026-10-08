# New opt-in packaging owner. Frozen R368/R8/R355 owners remain unchanged.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$AllocationPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$AllocationSha256,
    [Parameter(Mandatory)][string]$GrantPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$GrantSha256)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Pin([string]$Path) { [ordered]@{ path = $Path; bytes = (Get-Item -LiteralPath $Path).Length;
    sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() } }
function Read-Pinned([string]$Path, [string]$Hash) {
    if ((Pin $Path).sha256 -cne $Hash) { throw 'Exact packaging authority pin differs.' }
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -Depth 64
}
$a = Read-Pinned $AllocationPath $AllocationSha256
$g = Read-Pinned $GrantPath $GrantSha256
$ownerHash = (Pin $PSCommandPath).sha256
if ($a.executionPermitted -isnot [bool] -or -not $a.executionPermitted -or
    $g.executionPermitted -isnot [bool] -or -not $g.executionPermitted -or
    $a.scope -cne 'WINDOWS_NATIVE_PACKAGE_ORIGINAL' -or $g.scope -cne $a.scope -or
    $a.ownerSha256 -cne $ownerHash -or $g.ownerSha256 -cne $ownerHash -or
    $g.allocationSha256 -cne $AllocationSha256 -or $g.candidate -cne $a.candidate -or
    $g.tree -cne $a.tree -or $g.stage -cne $a.stage -or $g.evidenceRoot -cne $a.evidenceRoot -or
    $a.candidate -cnotmatch '^[0-9a-f]{40}$' -or $a.tree -cnotmatch '^[0-9a-f]{40}$') { throw 'New exact original allocation and grant required.' }
$start = [DateTimeOffset]::ParseExact($g.startsUtc, 'r', [Globalization.CultureInfo]::InvariantCulture)
$end = [DateTimeOffset]::ParseExact($g.expiresUtc, 'r', [Globalization.CultureInfo]::InvariantCulture)
if ($end -le $start -or ($end-$start).TotalMinutes -gt 60 -or [DateTimeOffset]::UtcNow -lt $start -or
    [DateTimeOffset]::UtcNow -ge $end -or $a.cleanupReserveSeconds -ne 60 -or
    -not [double]::IsFinite($a.runTimeoutSeconds) -or $a.runTimeoutSeconds -le 0 -or $a.runTimeoutSeconds -gt 900) { throw 'Bounded current window required.' }
if ($a.stage -cnotin @('restore','build','pack','install','help','invalid-argument') -or
    $g.externalNetworkPermitted -ne $false -or $g.nodePermitted -ne $false -or
    $g.credentialReadsPermitted -ne $false -or $g.packageAcquisitionPermitted -ne $false -or
    $g.privateProcessAndFileEffectsPermitted -ne $true) { throw 'Exact private native effects required.' }
$expectedExit = 0; $expectedStatus = 'Exited'
if ($a.stage -ceq 'invalid-argument') {
    if ($a.expectedExit -ne 2 -or $a.expectedStatus -cne 'NonZeroExit' -or
        $a.arguments.Count -ne 1 -or $a.arguments[0] -cne '--pisharp-private-invalid' -or
        $a.host.path -cne (Join-Path $a.toolRoot 'pisharp.exe')) { throw 'Only the exact inert installed invalid-argument probe admits exit 2.' }
    $expectedExit = 2; $expectedStatus = 'NonZeroExit'
} elseif ($a.expectedExit -ne 0 -or $a.expectedStatus -cne 'Exited') { throw 'Normal packaging originals require exit zero.' }
if ($a.stage -ceq 'help' -and ($a.arguments.Count -ne 1 -or $a.arguments[0] -cne '--help' -or
    $a.host.path -cne (Join-Path $a.toolRoot 'pisharp.exe'))) { throw 'Exact installed help probe required.' }
if ($a.stage -cin @('restore','build','pack') -and $a.arguments[0] -cne $a.stage) { throw 'Packaging verb differs.' }
if ($a.stage -ceq 'install' -and ($a.arguments[0] -cne 'tool' -or $a.arguments[1] -cne 'install')) { throw 'Tool install verb required.' }
if (Test-Path -LiteralPath $a.evidenceRoot) { throw 'Original evidence must be fresh.' }
$leases = [Collections.Generic.List[IO.FileStream]]::new()
$secondary = [Collections.Generic.List[Exception]]::new()
$qualified = $null; $claim = $null; $primary = $null; $settlement = $null; $report = $null
$effectiveTimeout = $null; $original = $null; $fullFaults = @(); $environment = @{}
$inputReleased = $true; $libraryReleased = $true; $claimReleased = $true
New-Item -ItemType Directory -Path $a.evidenceRoot | Out-Null
try {
    $claim = [IO.File]::Open((Join-Path $a.evidenceRoot '.original.claim'), 'CreateNew', 'ReadWrite', 'None')
    $pins = @($a.retainedPhysicalPins) + @($a.host, $a.sharedOwner, $a.library, $a.validator,
        (Pin $AllocationPath), (Pin $GrantPath))
    foreach ($pin in $pins) {
        $stream = [IO.File]::Open($pin.path, 'Open', 'Read', 'Read'); $leases.Add($stream)
        if ($stream.Length -ne $pin.bytes -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() -cne $pin.sha256) { throw 'Leased packaging input differs.' }
        $stream.Position = 0
    }
    if ($a.priorReceipts.Count -lt 106) { throw 'Complete current serial settlement required.' }
    foreach ($pin in $a.priorReceipts) {
        $prior = Read-Pinned $pin.path $pin.sha256
        if (-not $prior.settlement.originalTaskJoined -or -not $prior.settlement.privateJobCleanupConfirmed -or
            -not $prior.settlement.originalCaptureComplete -or -not $prior.inputLeasesReleased -or
            -not $prior.qualifiedLibraryHandlesReleased -or -not $prior.claimReleased -or $prior.secondaryErrors.Count) { throw 'Prior original remains unsettled.' }
    }
    foreach ($key in $a.privateEnvironment.Keys) { $environment[$key] = [string]$a.privateEnvironment[$key] }
    foreach ($name in @('DOTNET_CLI_HOME','NUGET_PACKAGES','TEMP','USERPROFILE','APPDATA','LOCALAPPDATA','PROGRAMFILES(X86)')) {
        if (-not $environment.ContainsKey($name) -or -not (Test-Path -LiteralPath $environment[$name] -PathType Container)) { throw 'Admitted private environment absent.' }
    }
    . $a.sharedOwner.path
    . $a.validator.path
    $qualified = Initialize-NativeAdmissionOriginalOwner $a.library.path $a.library.sha256
    if ($a.stage -cin @('help','invalid-argument')) {
        if (@(Get-ChildItem -LiteralPath $a.workingDirectory -Force).Count) { throw 'Inert shim probe requires empty private working directory.' }
    }
    $remaining = ($end - [DateTimeOffset]::UtcNow).TotalSeconds
    if ($remaining -le 60) { throw 'No original launched without cleanup reserve.' }
    $effectiveTimeout = [Math]::Min([double]$a.runTimeoutSeconds, $remaining-60)
    $argumentsValue = [Collections.Immutable.ImmutableArray[string]]::Empty
    foreach ($argument in $a.arguments) { $argumentsValue = $argumentsValue.Add([string]$argument) }
    $environmentValue = [Collections.Immutable.ImmutableDictionary[string,string]]::Empty
    foreach ($name in $environment.Keys) { $environmentValue = $environmentValue.Add($name,$environment[$name]) }
    $request = [PiSharp.Tools.Processes.ProcessRequest]::new($a.host.path,$argumentsValue,$a.workingDirectory,
        $environmentValue,(Join-Path $a.evidenceRoot 'operation.raw-spill.log'),$effectiveTimeout)
    $runner = [PiSharp.Tools.Processes.NativeProcessRunner]::new($null,$null,$null,$null)
    $original = $runner.RunSeparatedAsync($request,[Threading.CancellationToken]::None).AsTask()
    try { $result = $original.GetAwaiter().GetResult() }
    catch { if ($original.IsFaulted) { $fullFaults = @($original.Exception.InnerExceptions); throw $original.Exception }; throw }
    finally { if (-not $original.IsCompleted) { $original.GetAwaiter().GetResult() | Out-Null } }
    $settlement = Get-NativeAdmissionOriginalSettlement $result
    $writes = Write-NativeAdmissionCapturedOutputs $result (Join-Path $a.evidenceRoot 'stdout.txt') (Join-Path $a.evidenceRoot 'stderr.txt')
    foreach ($failure in $writes.errors) { $secondary.Add($failure.error.Exception) }
    if ($writes.errors.Count -or $result.Process.ExitCode -ne $expectedExit -or $result.Process.Status.ToString() -cne $expectedStatus -or
        -not $result.Process.ProcessStarted -or -not $result.Process.CleanupConfirmed -or -not $result.Process.CapturedOutputComplete -or
        $result.Process.Diagnostics.Length -ne 0 -or $result.StandardOutput.Truncated -or $result.StandardError.Truncated) { throw 'Original process/capture outcome differs.' }
    if ($a.stage -cin @('help','invalid-argument')) {
        $report = Assert-PiSharpInstalledSmokeCapture -Variant $a.stage -ExitCode $result.Process.ExitCode -StandardOutput $result.StandardOutput.Content -StandardError $result.StandardError.Content
        if (@(Get-ChildItem -LiteralPath $a.workingDirectory -Force).Count) { throw 'Inert probe created working files.' }
    }
    foreach ($pin in $pins) { if ((Pin $pin.path).sha256 -cne $pin.sha256) { throw 'Retained packaging input changed.' } }
    if ([DateTimeOffset]::UtcNow -ge $end) { throw 'Original completed beyond fixed grant.' }
} catch { $primary = $_.Exception }
finally {
    if ($null -ne $qualified) { foreach ($stream in $qualified.held) { try { $stream.Dispose() } catch { $libraryReleased = $false; $secondary.Add($_.Exception) } } }
    foreach ($stream in $leases) { try { $stream.Dispose() } catch { $inputReleased = $false; $secondary.Add($_.Exception) } }
    if ($null -ne $claim) { try { $claim.Dispose() } catch { $claimReleased = $false; $secondary.Add($_.Exception) } }
    if ([DateTimeOffset]::UtcNow -ge $end) { $secondary.Add([TimeoutException]::new('Fixed grant expired during retirement.')) }
    $receipt = $null; $writer = $null; $receiptPath = $null
    $evidenceErrors = [Collections.Generic.List[Exception]]::new()
    try {
      $receipt = [ordered]@{ schemaVersion = 1; candidate = $a.candidate; tree = $a.tree; stage = $a.stage;
        status = $(if ($null -eq $primary -and $secondary.Count -eq 0) { 'PASSED' } else { 'FAILED' });
        allocation = (Pin $AllocationPath); grant = (Pin $GrantPath); finishedUtc = [DateTimeOffset]::UtcNow.ToString('o');
        settlement = $settlement; inputLeasesReleased = $inputReleased; inputLeaseCount = $leases.Count;
        qualifiedLibraryHandlesReleased = $libraryReleased; claimReleased = $claimReleased;
        secondaryErrors = @($secondary | ForEach-Object { $_.GetType().FullName });
        primaryType = $(if ($primary) { $primary.GetType().FullName } else { $null });
        originalFaultTypes = @($fullFaults | ForEach-Object { $_.GetType().FullName });
        originalIsCanceled = $(if ($original) { $original.IsCanceled } else { $false });
        effectiveTimeoutSeconds = $effectiveTimeout; cleanupReserveSeconds = 60; report = $report;
        inheritedEnvironment = $false; nodeExecutions = 0; releaseAccepted = $false }
      $receiptPath = Join-Path $a.evidenceRoot 'original-owner-receipt.json'
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($receipt | ConvertTo-Json -Depth 18))
        $writer = [IO.File]::Open(($receiptPath+'.pending'),'CreateNew','Write','None')
        $writer.Write($bytes,0,$bytes.Length); $writer.Flush($true)
    } catch { $evidenceErrors.Add($_.Exception) }
    finally { if ($null -ne $writer) { try { $writer.Dispose() } catch { $evidenceErrors.Add($_.Exception) } } }
    # A late or incompletely retired writer never commits a PASSED receipt.
    # Pending bytes are retained evidence, not an admissible owner receipt.
    if ([DateTimeOffset]::UtcNow -ge $end -and $null -ne $receipt -and $receipt.status -ceq 'PASSED') {
        $evidenceErrors.Add([TimeoutException]::new('Fixed grant expired during receipt flush/retirement.'))
    }
    if ($evidenceErrors.Count -eq 0) {
        try {
            [IO.File]::Move(($receiptPath+'.pending'),$receiptPath,$false)
            if ([DateTimeOffset]::UtcNow -ge $end -and $receipt.status -ceq 'PASSED') {
                $evidenceErrors.Add([TimeoutException]::new('Fixed grant expired during receipt finalization.'))
                # Withdraw a late success to a non-admissible retained artifact.
                # If withdrawal itself fails, retain that failure as well. The
                # driver requires successful owner return, never receipt alone.
                try { [IO.File]::Move($receiptPath,($receiptPath+'.late'),$false) }
                catch { $evidenceErrors.Add($_.Exception) }
            }
        } catch { $evidenceErrors.Add($_.Exception) }
    }
    foreach ($error in $evidenceErrors) { $secondary.Add($error) }
}
if ($primary -or $secondary.Count) {
    $errors = [Collections.Generic.List[Exception]]::new(); if ($primary) { $errors.Add($primary) }; foreach ($error in $secondary) { $errors.Add($error) }
    throw [AggregateException]::new('Packaging original failed; retained receipt describes settlement.', $errors)
}
