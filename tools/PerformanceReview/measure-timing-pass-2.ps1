param([Parameter(Mandatory=$true)][string]$PiRoot,[Parameter(Mandatory=$true)][string]$Node,[Parameter(Mandatory=$true)][string]$BeforeHarness)
$ErrorActionPreference='Stop'
$env:DOTNET_TieredCompilation='0'
$out=Join-Path $PSScriptRoot 'memory-pass-2'
$after=Join-Path $PSScriptRoot 'bin/Release/net10.0/PerformanceReview.dll'
foreach($key in @('before','after','pi')) {
 if(Test-Path (Join-Path $out "$key-timing-series.jsonl")) { throw 'Use fresh timing output files' }
}
foreach($order in @(@('before','after','pi'),@('pi','after','before'),@('after','pi','before'))) {
 foreach($key in $order) {
  $lines=if($key -eq 'pi') {
   & $Node --expose-gc (Join-Path $PSScriptRoot 'pi-comparison.mjs') $PiRoot
  } else {
   $harness=if($key -eq 'before'){$BeforeHarness}else{$after}
   dotnet $harness --pi-comparison
  }
  if($LASTEXITCODE -ne 0) { throw "Timing failed: $key" }
  $lines | Add-Content -LiteralPath (Join-Path $out "$key-timing-series.jsonl") -Encoding utf8
 }
 'Completed interleaved timing round.'
}
