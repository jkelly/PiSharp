param([Parameter(Mandatory=$true)][string]$PiRoot,[Parameter(Mandatory=$true)][string]$Node,[Parameter(Mandatory=$true)][string]$BeforeHarness)
$ErrorActionPreference='Stop'
$env:DOTNET_TieredCompilation='0'
$here=$PSScriptRoot
$after=Join-Path $here 'bin/Release/net10.0/PerformanceReview.dll'
$out=Join-Path $here 'memory-pass-2'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$names=@('openai-completions-http-stream','mistral-conversations-http-stream')
function Capture([string]$file,[scriptblock]$action,[switch]$Append) {
 $lines=& $action
 if($LASTEXITCODE -ne 0) { throw "Capture failed: $file" }
 if($Append) { $lines | Add-Content -LiteralPath (Join-Path $out $file) -Encoding utf8 }
 else { $lines | Set-Content -LiteralPath (Join-Path $out $file) -Encoding utf8 }
}
foreach($file in @('before-memory.jsonl','after-memory.jsonl','pi-memory.jsonl','after-retention.jsonl','pi-retention.jsonl')) {
 if(Test-Path (Join-Path $out $file)) { throw "Use fresh output files: $file already exists" }
}
# Sequential fresh processes, interleaved implementations, avoiding concurrent benchmark load.
for($run=1;$run -le 5;$run++) {
 foreach($name in $names) {
  Capture 'before-memory.jsonl' { dotnet $BeforeHarness --pi-comparison --memory-case $name 2000 } -Append
  Capture 'after-memory.jsonl' { dotnet $after --pi-comparison --memory-case $name 2000 } -Append
  Capture 'pi-memory.jsonl' { & $Node --expose-gc (Join-Path $here 'pi-comparison.mjs') $PiRoot --memory-case $name 2000 } -Append
 }
 Write-Output "Cold memory: completed paired run $run/5."
}
Capture 'before-comparison.jsonl' { dotnet $BeforeHarness --pi-comparison }
Capture 'after-comparison.jsonl' { dotnet $after --pi-comparison }
Capture 'pi-comparison.jsonl' { & $Node --expose-gc (Join-Path $here 'pi-comparison.mjs') $PiRoot }
for($run=1;$run -le 3;$run++) {
 foreach($name in $names) {
  Capture 'after-retention.jsonl' { dotnet $after --pi-comparison --memory-case $name 2000 --retention-repetitions 30 } -Append
  Capture 'pi-retention.jsonl' { & $Node --expose-gc (Join-Path $here 'pi-comparison.mjs') $PiRoot --memory-case $name 2000 --retention-repetitions 30 } -Append
 }
 Write-Output "Retained memory: completed paired run $run/3."
}
Write-Output "Saved raw results to $out"
