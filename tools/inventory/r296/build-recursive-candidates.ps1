param([Parameter(Mandatory)][string]$SourceRoot, [Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($SourceRoot)
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$utf8 = [Text.UTF8Encoding]::new($false)
$receiptPath=Join-Path (Split-Path $root) '.pisharp-semantic-api-oracle.json'
$receipt=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
if($receipt.sourceCommit -cne 'd86654abb8862e201933517d6f1fce9f88dd117f'){throw 'Oracle source commit receipt mismatch'}
function IdentityMap { return ,[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal) }
function OrdinalStrings([string[]]$Values,[switch]$Unique) {
 $sorted=[string[]]@($Values);[Array]::Sort($sorted,[StringComparer]::Ordinal)
 $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 foreach($value in $sorted){if(!$Unique -or $seen.Add($value)){$value}}
}
function OrdinalRows($Rows,[string[]]$Keys,[switch]$Unique) {
 $groups=[Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal)
 foreach($row in $Rows){
  $key=(@(foreach($field in $Keys){[string]$row[$field]}) -join [char]0)
  if(!$groups.ContainsKey($key)){$groups[$key]=[Collections.Generic.List[object]]::new()}
  $groups[$key].Add($row)
 }
 foreach($group in $groups.Values){if($Unique){$group[0]}else{foreach($row in $group){$row}}}
}
$receiptFiles=IdentityMap
foreach($row in $receipt.files){$receiptFiles[$row.path]=$row}
$modules = IdentityMap
$nodes = IdentityMap
$edges = [Collections.Generic.List[object]]::new()
$queue = [Collections.Generic.Queue[object]]::new()
$aliases = IdentityMap
$aliases.Add('@earendil-works/pi-ai','packages/ai/src/index.ts')
$aliases.Add('@earendil-works/pi-agent-core','packages/agent/src/index.ts')
$aliases.Add('@earendil-works/pi-tui','packages/tui/src/index.ts')
function HashText([string]$text) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($text))).ToLowerInvariant() }
function Target([string]$file,[string]$specifier) {
 if ($aliases.ContainsKey($specifier)) { return $aliases[$specifier] }
 if (!$specifier.StartsWith('.')) { return "external:$specifier" }
 $full = [IO.Path]::GetFullPath((Join-Path (Split-Path (Join-Path $root $file)) $specifier))
 if (!$full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) { throw 'Escaping source import' }
 return [IO.Path]::GetRelativePath($root,$full).Replace('\','/')
}
function Module([string]$file) {
 if ($modules.ContainsKey($file)) { return $modules[$file] }
 $path = Join-Path $root $file
 if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
 $text = [IO.File]::ReadAllText($path)
 $rawHash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
 $pin=$receiptFiles['upstream/'+$file]
 if(!$pin -or $pin.sha256 -cne $rawHash -or $pin.bytes -ne (Get-Item -LiteralPath $path).Length){throw "Source receipt byte mismatch: $file"}
 $decls = IdentityMap
 $matches = [regex]::Matches($text,'(?m)^(?:export\s+)?(?:declare\s+)?(?:abstract\s+)?(?:async\s+)?(?<kind>interface|type|class|enum|function|const)\s+(?<name>[A-Za-z_$][\w$]*)')
 for ($i=0;$i -lt $matches.Count;$i++) {
  $match=$matches[$i]; $end=if($i+1 -lt $matches.Count){$matches[$i+1].Index}else{$text.Length}
  $excerpt=$text.Substring($match.Index,$end-$match.Index)
  $name=$match.Groups['name'].Value
  $row=[ordered]@{name=$name;kind=$match.Groups['kind'].Value;startLine=1+([regex]::Matches($text.Substring(0,$match.Index),"`n")).Count;excerpt=$excerpt;excerptSha256=(HashText $excerpt);memberLineCandidates=@([regex]::Matches($excerpt,'(?m)^\t(?!\t|/|\*|\s*$).+$') | ForEach-Object {$_.Value.Trim()})}
  if (!$decls.ContainsKey($name)) {$decls[$name]=[Collections.Generic.List[object]]::new()};$decls[$name].Add($row)
 }
 $imports=IdentityMap; $exports=IdentityMap; $stars=[Collections.Generic.List[string]]::new()
 foreach($match in [regex]::Matches($text,'(?ms)\b(?<mode>import|export)\s+(?:type\s+)?\{(?<names>[^}]+)\}\s+from\s+["''](?<module>[^"'']+)["'']')) {
  $target=Target $file $match.Groups['module'].Value
  foreach($part in $match.Groups['names'].Value.Split(',')) {
   if($part.Trim() -cmatch '^(?:type\s+)?(?<original>[A-Za-z_$][\w$]*)(?:\s+as\s+(?<alias>[A-Za-z_$][\w$]*))?$') {
    $original=$Matches.original;$alias=if($Matches.alias){$Matches.alias}else{$original}
    $entry=@{file=$target;name=$original}
    if($match.Groups['mode'].Value -ceq 'import'){$imports[$alias]=$entry}else{$exports[$alias]=$entry}
   }
  }
 }
 foreach($match in [regex]::Matches($text,'export\s+\*\s+from\s+["''](?<module>[^"'']+)["'']')) {$stars.Add((Target $file $match.Groups['module'].Value))}
 $result=@{file=$file;sha256=$rawHash;bytes=(Get-Item -LiteralPath $path).Length;decls=$decls;imports=$imports;exports=$exports;stars=$stars}
 $modules[$file]=$result;return $result
}
function Resolve([string]$file,[string]$name,[string[]]$trail=@()) {
 $key="$file::$name"
 if ($file.StartsWith('external:')) { return ,@{file=$file;name=$name;status='external-definition-unresolved'} }
 if($trail -ccontains $key){return};$module=Module $file;if(!$module){return}
 if($module.decls.ContainsKey($name)){return ,@{file=$file;name=$name;status='lexical-declaration-candidate'}}
 if($module.exports.ContainsKey($name)){$e=$module.exports[$name];return Resolve $e.file $e.name ($trail+$key)}
 foreach($star in $module.stars){Resolve $star $name ($trail+$key)}
}
function Enqueue([string]$from,[string]$file,[string]$name,[string]$reason) {
 $resolved=@(OrdinalRows @(Resolve $file $name) @('file','name') -Unique)
 if($resolved.Count -ne 1){
  $id="unresolved:$file::$name"
  if(!$nodes.ContainsKey($id)){$nodes[$id]=[ordered]@{id=$id;name=$name;status=if($resolved.Count){'ambiguous-lexical-origin'}else{'unresolved-origin'};candidates=$resolved;mappingStatus='unreviewed';semanticClosure=$false}}
  $edges.Add(@{from=$from;to=$id;reason=$reason});return
 }
 $r=$resolved[0];$id="$($r.file)::$($r.name)"
 $edges.Add(@{from=$from;to=$id;reason=$reason})
 if(!$nodes.ContainsKey($id)){$nodes[$id]=[ordered]@{id=$id;name=$r.name;sourcePath=$r.file;status=$r.status;mappingStatus='unreviewed';semanticClosure=$false};$queue.Enqueue($r)}
}
$seed='packages/coding-agent/src/core/extensions/types.ts'
$seedModule=Module $seed
foreach($name in @(OrdinalStrings @($seedModule.imports.Keys))){
 $e=$seedModule.imports[$name];$id="import:$seed::$name"
 $nodes.Add($id,[ordered]@{id=$id;name=$name;sourcePath=$seed;sourceSha256=$seedModule.sha256;status='direct-import-source';mappingStatus='unreviewed';semanticClosure=$false})
 Enqueue $id $e.file $e.name 'direct-extension-type-import'
}
while($queue.Count){
 $r=$queue.Dequeue();if($r.file.StartsWith('external:')){continue}
 $module=Module $r.file;$id="$($r.file)::$($r.name)";$node=$nodes[$id]
 $node.declarations=@($module.decls[$r.name]);$node.sourceSha256=$module.sha256
 $node.semanticObligations=@('Validate declaration/member boundaries with an authorized TypeScript AST parser.','Resolve generics, utilities, conditional/indexed/mapped types and declaration merging.','Separate public signature dependencies from implementation/comment/string identifier candidates.')
 $text=($node.declarations | ForEach-Object {$_.excerpt}) -join "`n"
 $tokens=@(OrdinalStrings @([regex]::Matches($text,'\b[A-Za-z_$][\w$]*\b') | ForEach-Object {$_.Value}) -Unique)
 foreach($token in $tokens){
  if($module.imports.ContainsKey($token)){$e=$module.imports[$token];Enqueue $id $e.file $e.name 'lexical-imported-identifier-candidate'}
  elseif($module.decls.ContainsKey($token) -and $token -cne $r.name){Enqueue $id $r.file $token 'lexical-local-identifier-candidate'}
 }
}
$native=Get-Content (Join-Path $repo 'compatibility/extensions/native-surface.json') -Raw | ConvertFrom-Json
$crosswalk=Get-Content (Join-Path $PSScriptRoot 'native-crosswalk.json') -Raw | ConvertFrom-Json
$nativeFiles=@(OrdinalStrings @($crosswalk.nativePaths) -Unique | ForEach-Object {
 $path=Join-Path $repo $_
 @{path=$_;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant();publicDeclarationLineCandidates=@([regex]::Matches([IO.File]::ReadAllText($path),'(?m)^public .+$')|ForEach-Object {$_.Value})}
})
$data=[ordered]@{schemaVersion=1;kind='recursive-extension-source-candidate-graph-r296';upstreamCommit='d86654abb8862e201933517d6f1fce9f88dd117f';nativeBaseCommit='6170379555cc9d817cdf3a360857a319ab8ba31b';method='source-only lexical over-approximation; not AST closure';gate=@{typeScriptAstClosureProven=$false;approvedNativeAbi=$false;implementationAccepted=$false;upstreamBehaviorExecuted=$false};seedImports=@(OrdinalStrings @($seedModule.imports.Keys));nodes=@(OrdinalRows @($nodes.Values) @('id'));edges=@(OrdinalRows @($edges) @('from','to','reason') -Unique);sourceFiles=@(OrdinalRows @($modules.Values) @('file') |ForEach-Object {@{path=$_.file;sha256=$_.sha256;bytes=$_.bytes}});directMembers=@($native.declarations|ForEach-Object {@{id=$_.id;name=$_.name;members=$_.members;source=$_.source;mappingStatus='unreviewed-native-ABI';behaviorStatus='not-qualified-by-r296'}});events=@($native.events|ForEach-Object {@{id=$_.id;name=$_.name;eventType=$_.eventType;context=$_.context;subscriptionMemberId=$_.subscriptionMemberId;reducer=$_.reducer;source=$_.eventSource;behaviorStatus='not-qualified-by-r296'}});unresolved=@('No authorized TypeScript compiler/parser executed or newly acquired.','External definitions, namespace/default imports, export alias syntax beyond named simple forms and ambient augmentation require AST follow-up.','Lexical member candidates are not certified public members; class excerpts include private implementation.','Published package ABI and external plugin behavior remain unqualified.');summary=@{directImportedNames=$seedModule.imports.Count;recursiveCandidateNodes=$nodes.Count;dependencyCandidateEdges=$edges.Count;sourceFilesRead=$modules.Count;astCertifiedNodes=0;qualifiedPluginScenarios=0}}
$data.nativeCrosswalk=$crosswalk.rows
$data.evidence=@{oracleReceiptSha256=(Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash.ToLowerInvariant();sourceFileHashesMatchedReceipt=$modules.Count;receiptQualification='Previously authored receipt pin verified only for source files read; no compiler/package/runtime acceptance inherited.';seedInventoryPath='compatibility/extensions/native-surface.json';seedInventorySha256=(Get-FileHash (Join-Path $repo 'compatibility/extensions/native-surface.json') -Algorithm SHA256).Hash.ToLowerInvariant();generatorPath='tools/inventory/r296/build-recursive-candidates.ps1';generatorSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant();crosswalkSha256=(Get-FileHash (Join-Path $PSScriptRoot 'native-crosswalk.json') -Algorithm SHA256).Hash.ToLowerInvariant()}
$data.nativeSourceFiles=$nativeFiles
$data.pluginCoverage=$crosswalk.pluginCoverage
$data.seedMemberImportReferences=@($native.declarations|ForEach-Object {
 $decl=$_
 foreach($member in $decl.members){
  $references=@(OrdinalStrings @($seedModule.imports.Keys|Where-Object {$member.signature -cmatch ('\b'+[regex]::Escape($_)+'\b')}))
  if($references.Count){@{memberId=$member.id;importedNames=$references}}
 }
})
$nodeIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($node in $data.nodes){
 if(!$nodeIds.Add($node.id)){throw "Duplicate exact node: $($node.id)"}
 if($node.status -ceq 'lexical-declaration-candidate'){
  foreach($declaration in $node.declarations){if($declaration.name -cne $node.name){throw "Declaration identity mismatch: $($node.id)"}}
 }
}
foreach($edge in $data.edges){
 if(!$nodeIds.Contains($edge.from)){throw "Dangling exact source: $($edge.from)"}
 if(!$nodeIds.Contains($edge.to)){throw "Dangling exact target: $($edge.to)"}
}
$caseClusters=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($id in $nodeIds){
 if(!$caseClusters.ContainsKey($id)){$caseClusters.Add($id,[Collections.Generic.List[string]]::new())}
 $caseClusters[$id].Add($id)
}
$caseWitnesses=@(foreach($cluster in $caseClusters.Values){if($cluster.Count -gt 1){$orderedIds=@(OrdinalStrings @($cluster));@{key=$orderedIds[0];ids=$orderedIds;allDistinctOrdinal=$true}}})
$data.identityIntegrity=@{comparison='StringComparer.Ordinal';danglingExactSources=0;danglingExactTargets=0;duplicateExactNodes=0;declarationIdentityMismatches=0;caseVariantNodeWitnesses=@(OrdinalRows $caseWitnesses @('key'));witnessGroupingOnly='OrdinalIgnoreCase is used only to report preserved case variants, never to resolve or deduplicate identities.'}
$data.correction=@{id='case-sensitive-identity-successor-r298';frozenBase='23beed302e96e19f6de5c310117446f50256e94e';reviewCandidate='ee44d9184e71327a8c54c171360121d2d3e01d30';reviewSha256='363129a384a1d8acdd85fec475b13b110a31880ae0d881d386654e5f29c631ad';supersedesFrozenIntegrityClaim=$true;previousDanglingExactTargetEdges=208;previousMissingDistinctExactTargets=50}
$data.summary.dependencyCandidateEdges=$data.edges.Count
$data.summary.directImportSourceNodes=$seedModule.imports.Count
$data.summary.transitiveCandidateNodes=$nodes.Count-$seedModule.imports.Count
$data.summary.caseVariantWitnessGroups=$caseWitnesses.Count
$data.identityIntegrity.crosswalkReferences=@(foreach($row in $crosswalk.rows){
 foreach($reference in $row.upstream){
  $type=($reference -split '\.',2)[0]
  $declarations=@($native.declarations|Where-Object {$_.name -ceq $type})
  $origins=@($data.nodes|Where-Object {$_.name -ceq $type -and $_.status -cne 'direct-import-source'})
  $parts=$reference -split '\.',2
  $members=@(if($parts.Count -eq 2){foreach($declaration in $declarations){$declaration.members|Where-Object {$_.name -ceq $parts[1]}|ForEach-Object {$_.id}}})
  @{rowId=$row.id;reference=$reference;exactSeedDeclarationIds=@($declarations|ForEach-Object {$_.id});exactGraphNodeIds=@($origins|ForEach-Object {$_.id});exactSeedMemberIds=$members;status=if($parts.Count -eq 2 -and !$members.Count){'member-reference-unresolved'}elseif(!$declarations.Count -and !$origins.Count){'symbol-reference-unresolved'}elseif(!$declarations.Count -and @($origins|Where-Object {$_.status -ceq 'lexical-declaration-candidate'}).Count -eq 0){'external-definition-unresolved-source-reference'}else{'exact-lexical-source-reference'};semanticMappingApproved=$false}
 }
 foreach($reference in $row.native){
  $type=($reference -split '\.',2)[0]
  $sources=@(foreach($file in $nativeFiles){
   $text=[IO.File]::ReadAllText((Join-Path $repo $file.path))
   if($text -cmatch ('(?m)^public\s+(?:(?:sealed|abstract|static|readonly|partial)\s+)*(?:(?:interface|class|record(?:\s+struct)?|enum|struct)\s+|delegate\s+\S+\s+)'+[regex]::Escape($type)+'\b')){$file.path}
  })
  @{rowId=$row.id;reference=$reference;exactNativeTypeSourcePaths=@(OrdinalStrings $sources);status=if(!$sources.Count){'native-symbol-reference-unresolved'}elseif($reference.Contains('.')){'native-type-found-member-semantics-unresolved'}else{'exact-native-type-source-reference'};semanticMappingApproved=$false}
 }
})
function StableJsonObject($value) {
 if($null -eq $value){return $null}
 $base=$value.PSObject.BaseObject
 if($base -is [string] -or $base -is [ValueType]){return $base}
 if($value -is [Collections.IDictionary]){
  $ordered=[ordered]@{};foreach($key in @(OrdinalStrings @($value.Keys))){$ordered[$key]=StableJsonObject $value[$key]};return $ordered
 }
 if($value -is [pscustomobject]){
  $ordered=[ordered]@{};foreach($name in @(OrdinalStrings @($value.PSObject.Properties.Name))){$ordered[$name]=StableJsonObject $value.PSObject.Properties[$name].Value};return $ordered
 }
 if($value -is [Collections.IEnumerable] -and $value -isnot [string]){
  $items=@(foreach($item in $value){StableJsonObject $item});return ,$items
 }
 return $value
}
$json=((StableJsonObject $data)|ConvertTo-Json -Depth 40).Replace("`r`n","`n")+"`n"
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),$json,$utf8)
