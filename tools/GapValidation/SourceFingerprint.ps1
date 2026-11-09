function Get-GapSourceFingerprint([string]$Root,[string]$Project) {
    $projects=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending=[Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($Project)
    $linked=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while($pending.Count) {
        $path=$pending.Dequeue()
        if(-not $projects.Add($path)){continue}
        [xml]$xml=Get-Content -LiteralPath $path -Raw
        foreach($reference in $xml.SelectNodes('//ProjectReference')) {
            $next=[IO.Path]::GetFullPath((Join-Path (Split-Path $path -Parent) $reference.Include))
            if(Test-Path -LiteralPath $next){$pending.Enqueue($next)}
        }
        foreach($item in $xml.SelectNodes('//Compile[@Include] | //Content[@Include] | //None[@Include]')) {
            if($item.Include -match '\$\('){continue}
            $candidate=Join-Path (Split-Path $path -Parent) $item.Include
            foreach($file in Get-ChildItem -Path $candidate -File -ErrorAction SilentlyContinue){[void]$linked.Add($file.FullName)}
        }
    }
    $paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($path in $linked){[void]$paths.Add($path)}
    foreach($projectPath in $projects) {
        foreach($file in Get-ChildItem -LiteralPath (Split-Path $projectPath -Parent) -Recurse -File) {
            if($file.FullName -match '[\\/](bin|obj|node_modules)[\\/]'){continue}
            [void]$paths.Add($file.FullName)
        }
    }
    foreach($name in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','global.json','NuGet.Config')) {
        $path=Join-Path $Root $name
        if(Test-Path -LiteralPath $path){[void]$paths.Add($path)}
    }
    foreach($file in Get-ChildItem -LiteralPath $PSScriptRoot -File){[void]$paths.Add($file.FullName)}
    $rows=@(foreach($path in ($paths|Sort-Object)) {
        $file=Get-Item -LiteralPath $path
        [ordered]@{path=[IO.Path]::GetRelativePath($Root,$path).Replace('\','/');bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $canonical=$rows|ConvertTo-Json -Depth 5 -Compress
    $digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
    $head=(& git -C $Root rev-parse HEAD).Trim()
    $dirty=@(& git -C $Root -c core.quotepath=false status --porcelain --untracked-files=all)
    return [ordered]@{head=$head;dirty=($dirty.Count -gt 0);gitStatus=$dirty;scope='project-reference closure, all non-generated files, repository build policy and validation owner';sha256=$digest;files=$rows}
}
