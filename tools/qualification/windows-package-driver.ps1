[CmdletBinding()]
param([switch]$Execute,[Parameter(Mandatory)][string]$PacketPath,
    [Parameter(Mandatory)][string]$PacketSha256,[string]$GrantPath,[string]$GrantSha256)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Pin([string]$Path) { [ordered]@{path=$Path;bytes=(Get-Item -LiteralPath $Path).Length;sha256=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()} }
function Read-Pinned([string]$Path,[string]$Hash) { if((Pin $Path).sha256-cne$Hash){throw 'Packaging pin differs.'}; Get-Content -LiteralPath $Path -Raw|ConvertFrom-Json -AsHashtable -Depth 64 }
function Write-New([string]$Path,$Value) { $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Depth 64));$stream=[IO.File]::Open($Path,'CreateNew','Write','None');try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()} }
if(-not$Execute-or-not$GrantPath-or-not$GrantSha256){throw 'Disabled. Exact independent packet review and new finite packaging grant required.'}
$p=Read-Pinned $PacketPath $PacketSha256; $g=Read-Pinned $GrantPath $GrantSha256
if($p.executionPermitted-ne$false-or$g.executionPermitted-ne$true-or$g.scope-cne'WINDOWS_NATIVE_PACKAGE_SIX_ORIGINALS'-or
 $g.packetSha256-cne$PacketSha256-or$g.driverSha256-cne(Pin $PSCommandPath).sha256-or$g.ownerSha256-cne$p.owner.sha256-or
 $g.candidate-cne$p.candidate-or$g.tree-cne$p.tree-or$g.operationCount-ne6-or$p.operations.Count-ne6-or
 $g.cleanupReserveSeconds-ne60-or$g.privateProcessAndFileEffectsPermitted-ne$true-or
 $g.externalNetworkPermitted-ne$false-or$g.nodePermitted-ne$false-or$g.credentialReadsPermitted-ne$false-or
 $g.packageAcquisitionPermitted-ne$false){throw 'Exact six-original native package grant required.'}
$start=[DateTimeOffset]::ParseExact($g.startsUtc,'r',[Globalization.CultureInfo]::InvariantCulture)
$end=[DateTimeOffset]::ParseExact($g.expiresUtc,'r',[Globalization.CultureInfo]::InvariantCulture)
if($end-le$start-or($end-$start).TotalMinutes-gt60-or[DateTimeOffset]::UtcNow-lt$start-or[DateTimeOffset]::UtcNow-ge$end){throw 'Current fixed window required.'}
$review=Read-Pinned $g.packetReview.path $g.packetReview.sha256
$barrier=Read-Pinned $g.serialSettlementBarrier.path $g.serialSettlementBarrier.sha256
if($barrier.packetSha256-cne$PacketSha256-or$barrier.noActiveOriginals-ne$true-or$barrier.interveningOriginalsAccounted-ne$true){throw 'Current complete serial barrier required.'}
$receipts=@($barrier.receipts)
if($receipts.Count-lt106-or@($receipts.path|Sort-Object -Unique).Count-ne$receipts.Count){throw 'Complete unique settlement inventory required.'}
foreach($baseline in $p.priorReceipts){if(-not@($receipts|Where-Object{$_.path-ceq$baseline.path-and$_.sha256-ceq$baseline.sha256-and$_.bytes-eq$baseline.bytes}).Count){throw 'Historical original omitted.'}}
$associations=@(foreach($pin in $barrier.actuals){$actual=Read-Pinned $pin.path $pin.sha256;foreach($op in $actual.operations){if($op.receipt){@{pin=$op.receipt;status=$op.status}}}})
foreach($pin in $receipts){
 $receipt=Read-Pinned $pin.path $pin.sha256
 if(-not$receipt.settlement.originalTaskJoined-or-not$receipt.settlement.privateJobCleanupConfirmed-or-not$receipt.settlement.originalCaptureComplete-or
    -not$receipt.inputLeasesReleased-or-not$receipt.qualifiedLibraryHandlesReleased-or-not$receipt.claimReleased-or$receipt.secondaryErrors.Count){throw 'Unsettled prior original.'}
 if(-not@($p.priorReceipts|Where-Object{$_.path-ceq$pin.path}).Count){$matches=@($associations|Where-Object{$_.pin.path-ceq$pin.path-and$_.pin.sha256-ceq$pin.sha256-and$_.pin.bytes-eq$pin.bytes});if(-not$matches.Count-or@($matches|Where-Object{$_.status-cne$receipt.status}).Count){throw 'Intervening receipt lacks exact actual association.'}}
}
foreach($pin in @($p.retainedPhysicalPins)+@($p.owner,$p.sharedOwner,$p.library,$p.validator,$p.sourceAdmissionScript,$p.source,$p.host)){
 $actual=Pin $pin.path;if($actual.bytes-ne$pin.bytes-or$actual.sha256-cne$pin.sha256){throw 'Physical packaging input differs.'}
}
. $p.sourceAdmissionScript.path
. $p.validator.path
$source=Get-Content -LiteralPath $p.source.path -Raw|ConvertFrom-Json
Assert-NativeSourceClosure -Repo $p.repository -Source $source -RequireFreshGeneratedRoots | Out-Null
if(Test-Path -LiteralPath $p.evidenceRoot){throw 'Once-only fresh packet evidence required.'}
foreach($path in @($p.feed,$p.toolRoot,$p.probeRoot)){
 Assert-PiSharpOrdinaryInstallPath $path
 if(-not(Test-Path -LiteralPath $path -PathType Container)-or@(Get-ChildItem -LiteralPath $path -Force).Count){throw 'Pre-admitted empty private output directories required.'}
}
New-Item -ItemType Directory -Path $p.evidenceRoot|Out-Null
$rows=[Collections.Generic.List[object]]::new();$failure=$null;$installed=$null
try{
 $expectedStages=@('restore','build','pack','install','help','invalid-argument')
 for($index=0;$index-lt6;$index++){
  $op=$p.operations[$index];if($op.stage-cne$expectedStages[$index]){throw 'Exact stage order differs.'}
  Assert-NativeSourceClosure -Repo $p.repository -Source $source | Out-Null
  $generated=[Collections.Generic.List[object]]::new()
  # New products only from this checkout's admitted project bin/obj directories.
  # Outputs remain mutable during restore/build/pack; lease current products for install/probes.
  if($index-ge3){
   foreach($root in $p.generatedRoots){if(Test-Path -LiteralPath $root){Assert-PiSharpOrdinaryInstallPath $root;foreach($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force){Assert-PiSharpOrdinaryInstallPath $file.FullName;$generated.Add((Pin $file.FullName))}}}
   foreach($file in Get-ChildItem -LiteralPath $p.feed -File -Recurse -Force){Assert-PiSharpOrdinaryInstallPath $file.FullName;$generated.Add((Pin $file.FullName))}
  }
  if($index-eq3){$package=Assert-PiSharpDistribution -Path $p.package -Repo $p.repository -Kind tool -Version $p.version -SourceCommit $p.candidate -EnablePromptTemplateYaml $true;Write-New (Join-Path $p.evidenceRoot 'package-inspection.json') $package}
  if($index-ge4){
   $installed=Assert-PiSharpWindowsToolInstallation -Package $p.package -PackageSha256 $package.archiveSha256 -Repo $p.repository -Version $p.version -SourceCommit $p.candidate -ToolRoot $p.toolRoot -BuildRoot $p.buildRoot
   foreach($file in Get-ChildItem -LiteralPath $p.toolRoot -File -Recurse -Force){Assert-PiSharpOrdinaryInstallPath $file.FullName;$generated.Add((Pin $file.FullName))}
  }
  $allocation=@{executionPermitted=$true;scope='WINDOWS_NATIVE_PACKAGE_ORIGINAL';candidate=$p.candidate;tree=$p.tree;
   stage=$op.stage;ownerSha256=$p.owner.sha256;host=$(if($index-ge4){$installed.shim}else{$p.host});sharedOwner=$p.sharedOwner;library=$p.library;validator=$p.validator;
   retainedPhysicalPins=@($p.retainedPhysicalPins)+@($generated.ToArray())+@($p.source,$p.owner,(Pin $PacketPath),(Pin $GrantPath),$g.packetReview,$g.serialSettlementBarrier)+@($receipts);
   priorReceipts=$receipts;privateEnvironment=$p.privateEnvironment;arguments=$op.arguments;workingDirectory=$op.workingDirectory;
   evidenceRoot=(Join-Path $p.evidenceRoot ('stage-'+($index+1)));toolRoot=$p.toolRoot;expectedExit=$op.expectedExit;expectedStatus=$op.expectedStatus;
   runTimeoutSeconds=$op.timeoutSeconds;cleanupReserveSeconds=60}
  $allocationPath=Join-Path $p.evidenceRoot ('allocation-'+($index+1)+'.json');Write-New $allocationPath $allocation
  $childGrant=@{executionPermitted=$true;scope=$allocation.scope;ownerSha256=$p.owner.sha256;allocationSha256=(Pin $allocationPath).sha256;
   candidate=$p.candidate;tree=$p.tree;stage=$op.stage;evidenceRoot=$allocation.evidenceRoot;startsUtc=$g.startsUtc;expiresUtc=$g.expiresUtc;
   privateProcessAndFileEffectsPermitted=$true;externalNetworkPermitted=$false;nodePermitted=$false;credentialReadsPermitted=$false;packageAcquisitionPermitted=$false;parentGrant=(Pin $GrantPath)}
  $childGrantPath=Join-Path $p.evidenceRoot ('grant-'+($index+1)+'.json');Write-New $childGrantPath $childGrant
  $caught=$null
  try{& $p.owner.path -AllocationPath $allocationPath -AllocationSha256 (Pin $allocationPath).sha256 -GrantPath $childGrantPath -GrantSha256 (Pin $childGrantPath).sha256}catch{$caught=$_}
  $attemptRow=@{stage=$op.stage;receipt=$null;ownerReturnedSuccessfully=($null-eq$caught);status='FAILED_RECEIPT_INSPECTION'}
  $rows.Add($attemptRow)
  try{
   $receiptPath=Join-Path $allocation.evidenceRoot 'original-owner-receipt.json'
   $receiptPin=$(if(Test-Path -LiteralPath $receiptPath){Pin $receiptPath}else{$null})
   $receipt=$(if($receiptPin){Read-Pinned $receiptPin.path $receiptPin.sha256}else{$null})
   $attemptRow.receipt=$receiptPin
   $attemptRow.status=$(if($caught){'FAILED'}elseif($receipt){$receipt.status}else{'MISSING_RECEIPT'})
  }catch{
   if($caught){throw [AggregateException]::new('Original owner and receipt inspection both failed.',[Exception[]]@($caught.Exception,$_.Exception))}
   throw
  }
  if($caught){throw $caught}
  if(-not$receipt-or$receipt.status-cne'PASSED'-or-not$receipt.settlement.originalTaskJoined-or-not$receipt.inputLeasesReleased-or-not$receipt.qualifiedLibraryHandlesReleased-or-not$receipt.claimReleased-or$receipt.secondaryErrors.Count){throw 'Original did not pass and fully settle.'}
  $receipts+=@($receiptPin)
 }
 Write-New (Join-Path $p.evidenceRoot 'installed-inspection.json') $installed
}catch{$failure=$_}finally{
 try{
  Write-New (Join-Path $p.evidenceRoot 'actual-results.json') @{candidate=$p.candidate;tree=$p.tree;operations=@($rows.ToArray());
   status=$(if($failure){'FAILED'}else{'PASSED'});attemptedOriginals=$rows.Count;unlaunchedOriginals=(6-$rows.Count);releaseAccepted=$false;packagePublished=$false}
 }catch{
  $evidenceFailure=$_.Exception
  if($failure){throw [AggregateException]::new('Original failure and final evidence failure.',[Exception[]]@($failure.Exception,$evidenceFailure))}
  throw
 }
}
if($failure){throw $failure}
