function Get-GapConsoleSkips([string]$Path,[string]$ErrorPath) {
    if(-not (Test-Path -LiteralPath $Path)){return @()}
    $text=[string](Get-Content -LiteralPath $Path -Raw)
    if($ErrorPath -and (Test-Path -LiteralPath $ErrorPath)){$text+="`n"+[string](Get-Content -LiteralPath $ErrorPath -Raw)}
    return @([regex]::Matches($text,'(?im)^\s*(?:SKIP(?:PED)?(?:_|\b|:)|INFO symlink fixture unavailable:)[^\r\n]*')|ForEach-Object{$_.Value.Trim()})
}
function Get-GapCaseEvidence([string]$Path,[string]$ErrorPath) {
    if(-not (Test-Path -LiteralPath $Path)){return [ordered]@{kind='missing';selectedCount=$null;cases=@()}}
    $text=[string](Get-Content -LiteralPath $Path -Raw)
    if($text.Length -eq 0){return [ordered]@{kind='empty';selectedCount=0;cases=@()}}
    $lines=@($text -split '\r?\n'|Where-Object {$_.Trim().Length})
    $ndjson=[Collections.Generic.List[object]]::new()
    foreach($line in $lines) {
        try {$entry=$line|ConvertFrom-Json -ErrorAction Stop} catch {$ndjson.Clear();break}
        $id=if($entry.Id){$entry.Id}elseif($entry.testId){$entry.testId}elseif($entry.name){$entry.name}else{$null}
        if(-not $id -or -not $entry.status){$ndjson.Clear();break}
        $ndjson.Add([ordered]@{id=$id;status=$entry.status;reason=$entry.reason})
    }
    if($ndjson.Count -and $ndjson.Count -eq $lines.Count) {
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($row in $ndjson){if(-not $seen.Add([string]$row.id)){return [ordered]@{kind='duplicate-ndjson-case-identity';selectedCount=$null;cases=@()}}}
        return [ordered]@{kind='console-ndjson';selectedCount=$ndjson.Count;cases=$ndjson.ToArray()}
    }
    $starts=[regex]::Matches($text,'(?m)^\{')
    $hasReportCandidate=$false
    for($i=$starts.Count-1;$i -ge 0;$i--) {
        # Transport probes are explicitly diagnostic JSON lines, followed by
        # their real PASS/FAIL rows and suite completion. They are not reports.
        $firstLine=($text.Substring($starts[$i].Index) -split '\r?\n',2)[0]
        try {$diagnostic=$firstLine|ConvertFrom-Json -ErrorAction Stop} catch {$diagnostic=$null}
        if(($diagnostic.probe -or ($diagnostic.caseId -and $diagnostic.nativeComparison)) -and -not $diagnostic.results -and -not $diagnostic.tests -and $null -eq $diagnostic.cases){continue}
        $hasReportCandidate=$true
        try {$json=$text.Substring($starts[$i].Index).Trim()|ConvertFrom-Json -ErrorAction Stop} catch {continue}
        $entries=if($json.results -is [array]){$json.results}elseif($json.tests -is [array]){$json.tests}elseif($json.cases -is [array]){$json.cases}else{$null}
        if($null -ne $entries) {
            $rows=@(foreach($row in $entries){[ordered]@{id=$(if($row.testId){$row.testId}elseif($row.Id){$row.Id}elseif($row.id){$row.id}else{$row.name});status=$(if($row.status){$row.status}elseif($row.Matched -is [bool]){if($row.Matched){'PASS'}else{'FAIL'}}else{$null});reason=$row.reason}})
            return [ordered]@{kind='console-json';selectedCount=$rows.Count;declaredCount=$(if($json.cases -is [valuetype]){$json.cases}else{$null});cases=$rows;originalExecuted=$json.originalExecuted;sourceDerivedOracle=$json.sourceDerivedOracle}
        }
        if($json.cases -is [valuetype]){return [ordered]@{kind='declared-json-count';selectedCount=[int]$json.cases;cases=@()}}
    }
    if($hasReportCandidate){return [ordered]@{kind='invalid-or-incomplete-console-json';selectedCount=$null;cases=@()}}
    if($ErrorPath -and (Test-Path -LiteralPath $ErrorPath)){$text+="`n"+[string](Get-Content -LiteralPath $ErrorPath -Raw)}
    $declared=[regex]::Match($text,'(?m)^PASS(?:\s+|:\s*)(\d+)\s+[^\r\n]*\b(?:cases?|tests?|groups?)\b')
    $observations=@([regex]::Matches($text,'(?m)^(PASS(?:_UPSTREAM_CAPTURE)?|FAIL)(?: |: )(.+)')|Where-Object{$_.Value -notmatch '^PASS(?:_UPSTREAM_CAPTURE)?:\s*\d+;\s*FAIL:' -and $_.Value -notmatch '^PASS(?:\s+|:\s*)\d+\s+[^\r\n]*\b(?:cases?|tests?|groups?)\b'})
    if($observations.Count) {
        $rows=@(foreach($match in $observations){
            $status=if($match.Groups[1].Value -eq 'FAIL'){'FAIL'}else{'PASS'}
            $description=$match.Groups[2].Value.Trim()
            $id=if($status -eq 'FAIL'){($description -split ': ',2)[0]}else{$description}
            [ordered]@{id=$id;status=$status;detail=$(if($status -eq 'FAIL'){$description}else{$null})}
        })
        # Some registered runners print a group PASS after an explicit skipped
        # branch. Preserve their real case identity and qualify that observation.
        $skipMappings=@{
            'SKIP Unix raw FIFO EOF/SIGTERM regression on Windows'='raw FIFO daemon exits after EOF and SIGTERM'
            'SKIP graceful Job Object descendant test: Windows only.'='manager.graceful-exit-kills-descendants-windows'
        }
        foreach($skip in @(Get-GapConsoleSkips $Path $ErrorPath)) {
            $mapped=if($skipMappings.ContainsKey($skip)){$skipMappings[$skip]}else{$null}
            $row=if($mapped){@($rows|Where-Object id -eq $mapped)|Select-Object -First 1}else{$null}
            if($row){$row.status='SKIP';$row.reason=$skip}
        }
        return [ordered]@{kind='observed-case-lines';selectedCount=$rows.Count;cases=$rows}
    }
    if($declared.Success){return [ordered]@{kind='declared-console-count';selectedCount=[int]$declared.Groups[1].Value;cases=@()}}
    $tap=[regex]::Matches($text,'(?m)^(not ok|ok)\s+(\d+)(?:\s+-)?\s*(.*)')
    if($tap.Count){return [ordered]@{kind='observed-tap';selectedCount=$tap.Count;cases=@($tap|ForEach-Object{[ordered]@{id=$_.Groups[3].Value.Trim();status=$_.Groups[1].Value}})}}
    $fraction=[regex]::Match($text,'(?m)^\d+/(\d+) passed\b')
    if($fraction.Success){return [ordered]@{kind='declared-console-count';selectedCount=[int]$fraction.Groups[1].Value;cases=@()}}
    return [ordered]@{kind='unparsed';selectedCount=$null;cases=@()}
}
