// One explicitly requested bounded diagnostic run; never a full structural golden.
import assert from 'node:assert/strict';import fs from 'node:fs';import path from 'node:path';import {spawn,execFileSync} from 'node:child_process';
import {repo,readPlan,verifyOracle,environment,pins,hash,jsonBytes,writeNew,verifyProvenanceBundle,verifyProtocolEvidence} from './full-structural-openai-common.mjs';
import {observeDriverChild,childArgs} from './run-full-structural-openai.mjs';
import {readMinimalEvidence,classifyMinimalEvidence} from './minimal-native-panic-consumer.mjs';
const args=process.argv.slice(2);assert.equal(args.length,3,'Explicit oracle, fresh scratch and exact candidate required');
const [oracleArg,scratchArg,candidate]=args,oracle=path.resolve(oracleArg),scratch=path.resolve(scratchArg);
const git=(...a)=>execFileSync('C:/Program Files/Git/cmd/git.exe',['-C',repo,...a],{encoding:'utf8'}).trim();
assert.match(candidate,/^[a-f0-9]{40}$/);assert.equal(git('rev-parse','HEAD'),candidate);assert.equal(git('status','--porcelain'),'');
assert(!fs.existsSync(scratch),'Fresh diagnostic evidence directory required');
const plan=readPlan(),frozen=pins();await verifyOracle(oracle,plan);fs.mkdirSync(scratch);
for(const name of ['home','temp','config','evidence'])fs.mkdirSync(path.join(scratch,name));
const output=path.join(scratch,'minimal.json'),env={...environment(scratch,plan),PISHARP_SEMANTIC_OPENAI_STRUCTURAL_CHILD:'1',PISHARP_SEMANTIC_OPENAI_STRUCTURAL_ORACLE:oracle,PISHARP_SEMANTIC_OPENAI_STRUCTURAL_SCRATCH:scratch,PISHARP_SEMANTIC_OPENAI_STRUCTURAL_OUTPUT:output};
writeNew(scratch,path.join(scratch,'owner.json'),jsonBytes({kind:'bounded-same-pinned-native-api-panic-preflight',candidate,sourceBase:'43d45ffdbb0c8f738e6256b3ca1cc817dfa7bdc4',frozen,oracle,noFullGraphTraversal:true,fullP1Verdict:'HOLD'}));
// Existing exact environment, actual child observer, deadline, raw traffic bounds and native close guard remain.
const child=await observeDriverChild(spawn,process.execPath,[...childArgs(repo),'--minimal-panic'],{cwd:scratch,env,stdio:['ignore','pipe','pipe']},plan.limits);
writeNew(scratch,path.join(scratch,'driver.stdout.json'),child.stdout);writeNew(scratch,path.join(scratch,'driver.stderr.txt'),child.stderr);
writeNew(scratch,path.join(scratch,'driver.lifecycle.json'),jsonBytes(child));
await verifyOracle(oracle,plan);assert.deepEqual(pins(),frozen);assert.equal(git('rev-parse','HEAD'),candidate);assert.equal(git('status','--porcelain'),'');
const evidence=readMinimalEvidence(output,plan.limits),{receipt,bundle,protocol,observation}=evidence;
const {reproduced}=classifyMinimalEvidence(child,evidence,plan);
const result={schemaVersion:1,candidate,oracle,kind:'bounded-same-pinned-native-api-panic-preflight',status:reproduced?'reproduced-pinned-native-api-blocker':'minimal-path-did-not-reproduce-panic',actualCompilerExecuted:true,actualNativeHelperExit:receipt.nativeChildLifecycle.exit.code,actualNativePhysicalClose:true,allNativeCleanupJoined:true,driverExit:child.lifecycle.exit?.code,driverPhysicalClose:true,queries:observation.queries,actualTypeNode:observation.actualTypeNode,actualOwnerType:observation.actualOwnerType,actualProperties:observation.actualProperties,actualTargetSymbolId:observation.actualTargetSymbolId,completeProvenance:true,provenanceBytes:bundle.aggregateBytes,protocol,originalPinsAndLimitsUnchanged:true,sourceAfterVerificationPassed:true,fullStructuralInventoryQualified:false,semanticPublicClosure:false,fullP1Verdict:'HOLD',fullPhaseGatesPassed:0};
writeNew(scratch,path.join(scratch,'result.json'),jsonBytes(result));console.log(JSON.stringify({candidate,status:result.status,queries:result.queries.length,driverExit:result.driverExit,nativeExit:0,allCleanupJoined:true,completeProvenance:true,fullP1Verdict:'HOLD',result:path.join(scratch,'result.json'),sha256:hash(jsonBytes(result))}));
