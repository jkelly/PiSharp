param([Parameter(Mandatory=$true)][string]$PiRoot,[Parameter(Mandatory=$true)][string]$Node,[Parameter(Mandatory=$true)][string]$BeforeHarness)
$ErrorActionPreference='Stop'
$env:DOTNET_TieredCompilation='0'
$out=Join-Path $PSScriptRoot 'memory-pass-2'
$after=Join-Path $PSScriptRoot 'bin/Release/net10.0/PerformanceReview.dll'
foreach($file in @('before-retention-detail.jsonl','after-retention-detail.jsonl','pi-retention-detail.jsonl')) {
 if(Test-Path (Join-Path $out $file)) { throw "Use fresh output files: $file already exists" }
}
for($run=1;$run -le 3;$run++) {
 foreach($name in @('openai-completions-http-stream','mistral-conversations-http-stream')) {
  foreach($implementation in @('before','after','pi')) {
   $lines=if($implementation -eq 'pi') {
    & $Node --expose-gc (Join-Path $PSScriptRoot 'pi-comparison.mjs') $PiRoot --memory-case $name 2000 --retention-repetitions 30
   } else {
    $harness=if($implementation -eq 'before') { $BeforeHarness } else { $after }
    dotnet $harness --pi-comparison --memory-case $name 2000 --retention-repetitions 30
   }
   if($LASTEXITCODE -ne 0) { throw "Failed $implementation $name" }
   $lines | Add-Content -LiteralPath (Join-Path $out "$implementation-retention-detail.jsonl") -Encoding utf8
  }
 }
 Write-Output "Detailed retained memory: completed paired run $run/3."
}
