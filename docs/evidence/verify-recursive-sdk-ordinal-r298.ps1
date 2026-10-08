param([Parameter(Mandatory)][string]$FrozenGraphPath,[Parameter(Mandatory)][string]$GraphPath,[Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Inspect($graph){
 $ids=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 $missingTargets=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 $missingSources=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
 $duplicateNodes=0;$targetEdges=0;$sourceEdges=0;$mismatch=0
 foreach($node in $graph.nodes){
  if(!$ids.Add([string]$node.id)){$duplicateNodes++}
  if($node.status -ceq 'lexical-declaration-candidate'){
   if(![StringComparer]::Ordinal.Equals($node.id,$node.sourcePath+'::'+$node.name)){$mismatch++}
   foreach($decl in $node.declarations){if(![StringComparer]::Ordinal.Equals($node.name,$decl.name)){$mismatch++}}
  }
 }
 foreach($edge in $graph.edges){
  if(!$ids.Contains([string]$edge.to)){$targetEdges++;[void]$missingTargets.Add([string]$edge.to)}
  if(!$ids.Contains([string]$edge.from)){$sourceEdges++;[void]$missingSources.Add([string]$edge.from)}
 }
 [ordered]@{nodes=$graph.nodes.Count;edges=$graph.edges.Count;duplicateExactNodes=$duplicateNodes;danglingExactTargetEdges=$targetEdges;missingDistinctExactTargets=$missingTargets.Count;danglingExactSourceEdges=$sourceEdges;missingDistinctExactSources=$missingSources.Count;declarationIdentityMismatches=$mismatch}
}
$old=Get-Content -LiteralPath $FrozenGraphPath -Raw|ConvertFrom-Json
$new=Get-Content -LiteralPath $GraphPath -Raw|ConvertFrom-Json
$before=Inspect $old;$after=Inspect $new
if($before.danglingExactTargetEdges -ne 208 -or $before.missingDistinctExactTargets -ne 50){throw 'Frozen review reproduction differs'}
if($after.duplicateExactNodes -or $after.danglingExactTargetEdges -or $after.danglingExactSourceEdges -or $after.declarationIdentityMismatches){throw 'Corrected exact graph integrity failed'}
$ids=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($node in $new.nodes){[void]$ids.Add($node.id)}
$required=@('packages/ai/src/types.ts::ToolCall','packages/ai/src/auth/types.ts::Credential','packages/coding-agent/src/modes/interactive/theme/theme.ts::Theme','packages/coding-agent/src/modes/interactive/theme/theme.ts::theme','packages/tui/src/colors.ts::IndexedColor','packages/tui/src/colors.ts::indexedColor')
foreach($id in $required){if(!$ids.Contains($id)){throw "Missing exact identity witness: $id"}}
if($ids.Contains('packages/ai/src/types.ts::toolCall') -or $ids.Contains('packages/ai/src/auth/types.ts::credential')){throw 'Spurious folded declaration identities retained'}
$sourceFiles=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
foreach($file in $new.sourceFiles){if(!$sourceFiles.TryAdd($file.path,$file)){throw 'Duplicate source path'}}
foreach($node in $new.nodes){
 if($node.status -ceq 'lexical-declaration-candidate' -or $node.status -ceq 'direct-import-source'){
  if(!$sourceFiles.ContainsKey($node.sourcePath) -or ![StringComparer]::Ordinal.Equals($sourceFiles[$node.sourcePath].sha256,$node.sourceSha256)){throw 'Node/source-file hash reference mismatch'}
 }
}
$seedMemberIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$seedDeclarationIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($decl in $new.directMembers){if(!$seedDeclarationIds.Add($decl.id)){throw 'Duplicate seed declaration'};foreach($member in $decl.members){if(!$seedMemberIds.Add($member.id)){throw 'Duplicate seed member'}}}
if($new.directMembers.Count -ne 185 -or $seedMemberIds.Count -ne 562 -or $new.events.Count -ne 41){throw 'Seed counts changed'}
foreach($row in $new.seedMemberImportReferences){
 if(!$seedMemberIds.Contains($row.memberId)){throw 'Unknown seed member reference'}
 foreach($name in $row.importedNames){if(!$ids.Contains('import:packages/coding-agent/src/core/extensions/types.ts::'+$name)){throw 'Unknown imported name reference'}}
}
$nativePaths=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($file in $new.nativeSourceFiles){
 [void]$nativePaths.Add($file.path)
 if(![StringComparer]::Ordinal.Equals((Hash (Join-Path $repo $file.path)),$file.sha256)){throw 'Native source hash mismatch'}
}
foreach($ref in $new.identityIntegrity.crosswalkReferences){
 foreach($id in $ref.exactSeedDeclarationIds){if(!$seedDeclarationIds.Contains($id)){throw 'Crosswalk declaration reference missing'}}
 foreach($id in $ref.exactGraphNodeIds){if(!$ids.Contains($id)){throw 'Crosswalk graph reference missing'}}
 foreach($id in $ref.exactSeedMemberIds){if(!$seedMemberIds.Contains($id)){throw 'Crosswalk member reference missing'}}
 foreach($path in $ref.exactNativeTypeSourcePaths){if(!$nativePaths.Contains($path)){throw 'Crosswalk native source reference missing'}}
 if($ref.semanticMappingApproved){throw 'Crosswalk semantic approval was inferred'}
}
$generator=Join-Path $repo 'tools/inventory/r296/build-recursive-candidates.ps1'
if(![StringComparer]::Ordinal.Equals((Hash $generator),$new.evidence.generatorSha256)){throw 'Generator signature/hash mismatch'}
if(![StringComparer]::Ordinal.Equals((Hash (Join-Path $repo 'tools/inventory/r296/native-crosswalk.json')),$new.evidence.crosswalkSha256)){throw 'Crosswalk input hash mismatch'}
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($generator,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Generator PowerShell syntax invalid'}
$parameters=@($ast.ParamBlock.Parameters|ForEach-Object {$_.Name.VariablePath.UserPath})
if($parameters.Count -ne 2 -or $parameters[0] -cne 'SourceRoot' -or $parameters[1] -cne 'OutputPath'){throw 'Generator parameter signature changed'}
$evidence=[ordered]@{kind='recursive-sdk-ordinal-source-data-integrity-r298';frozenGraphSha256=(Hash $FrozenGraphPath);correctedGraphSha256=(Hash $GraphPath);before=$before;after=$after;summary=$new.summary;caseWitnesses=$required;caseVariantGroups=$new.identityIntegrity.caseVariantNodeWitnesses;sourceHashReferencesVerified=$sourceFiles.Count;nativeSourceHashesVerified=$nativePaths.Count;seedMembersVerified=$seedMemberIds.Count;crosswalkReferencesVerified=$new.identityIntegrity.crosswalkReferences.Count;generatorSha256=(Hash $generator);generatorParameters=$parameters;generatorStaticParseErrors=0;verificationScriptSha256=(Hash $PSCommandPath);limitations=@('Pure source-data generation, JSON integrity validation and static PowerShell parse only; no compiler/build/tests or upstream/native product execution.','No AST/type/public ABI/plugin behavior acceptance.','Native type references are lexical; member semantics and exposed profile remain unresolved where stated.')}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),(($evidence|ConvertTo-Json -Depth 20).Replace("`r`n","`n")+"`n"),[Text.UTF8Encoding]::new($false))
