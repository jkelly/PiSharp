import assert from 'node:assert/strict';
import fs from 'node:fs';
import {hash,verifyProvenanceBundle,verifyProtocolEvidence} from './full-structural-openai-common.mjs';

export function readMinimalEvidence(output,limits){
 const bundle=verifyProvenanceBundle(output,limits);
 function decode(node,depth=0){assert(depth<=limits.jsonDepth);if(node.representation==='inline')return node.value;
  if(node.representation==='object')return Object.fromEntries(node.fields.map(row=>[row.name,decode(row.value,depth+1)]));
  assert.equal(node.representation,'ordered-sidecar');const part=bundle.manifest.parts[node.ordinal];assert(part.bytes<=4*1024*1024,'Minimal field decode bound');
  const bytes=fs.readFileSync(output+part.suffix);assert.equal(bytes.length,part.bytes);assert.equal(hash(bytes),part.sha256);
  const values=bytes.toString('utf8').split('\n').filter(Boolean).map((line,index)=>{const row=JSON.parse(line);assert.equal(row.ordinal,index);return row.value;});assert.equal(values.length,part.records);
  return part.valueKind==='exact-string'?values[0]:values;
 }
 const field=name=>{const found=bundle.manifest.shape.fields.find(row=>row.name===name);return found?decode(found.value):undefined;};
 const protocol=field('protocol');verifyProtocolEvidence(output,protocol,limits);
 return {bundle,receipt:bundle.receipt,protocol,observation:field('minimalQueryObservation'),failure:field('failure'),secondaryFailures:field('secondaryFailures'),nativeFirstFailure:field('nativeFirstFailure'),nativeFailure:field('nativeFailure'),native:field('nativeChildLifecycle'),lifecycle:field('lifecycle'),cleanup:field('nativeCleanup'),denied:field('denied'),deniedFsRequests:field('deniedFsRequests'),receiptSha256:field('receiptSha256')};
}
function exactClose(life,code){assert.equal(life.spawned,true);assert.equal(life.exitObserved,true);assert.equal(life.closeObserved,true);assert.deepEqual(life.exit,{code,signal:null});assert.deepEqual(life.close,{code,signal:null});assert.equal(life.childStateAtClose.exitCode,code);assert.equal(life.childStateAtClose.signalCode,null);assert.equal(life.childStateAtClose.killed,false);assert.deepEqual(life.errors,[]);assert.deepEqual(life.terminationRequests,[]);assert(life.watchdogs.every(row=>row.armed===true&&row.fired===false),'Fired or invalid watchdog');}
function identity(retained,text){assert(retained);const bytes=Buffer.from(String(text));assert.equal(retained.utf8Bytes,bytes.length);assert.equal(retained.sha256,hash(bytes));if(retained.inline!==undefined)assert.equal(retained.inline,String(text));}
export function classifyMinimalEvidence(child,evidence,plan){
 const {receipt,protocol,observation,failure,native,lifecycle,cleanup}=evidence;
 assert.equal(evidence.receiptSha256,plan.receipt.sha256);assert.equal(receipt.receiptSha256,plan.receipt.sha256);
 assert.equal(evidence.secondaryFailures.length,0,'Secondary evidence failures invalidate diagnosis');assert.equal(evidence.denied.length,0,'Physical guard denied an operation');
 for(const key of ['nativeFirstFailure','nativeFailure']){assert.equal(evidence[key],undefined);assert.equal(receipt[key],undefined);}
 assert.equal(observation.kind,'bounded-same-pinned-native-api-panic-preflight');assert.equal(observation.sourceSha,plan.sourceCommit);assert.equal(observation.sourceSha256,'b06bcae6821a0e9fda1b63be613a7ce28eb0e66e1d01d16564e59e83f9ce10ea');assert.equal(observation.nativeIdsResolvedFresh,true);
 assert.equal(observation.fullStructuralInventoryQualified,false);assert.equal(observation.semanticPublicClosure,false);assert.equal(observation.fullP1Verdict,'HOLD');
 assert.deepEqual(observation.queries.map(row=>row.method),['parseConfigFile','updateSnapshot','getSourceFile','getTypeFromTypeNode','getPropertiesOfType','getTypeOfSymbol']);
 observation.queries.forEach((row,i)=>{assert.equal(row.ordinal,i);if(i<5){assert.equal(row.state,'returned');assert.equal(row.failure,undefined);}});
 assert.deepEqual(observation.queries[3].context,observation.actualTypeNode);assert.equal(observation.actualTypeNode.declarationName,'findToolSystemPromptContribution');assert.equal(observation.queries[4].context.nativeTypeId,observation.actualOwnerType.id);
 const targets=observation.actualProperties.filter(row=>row.name==='guidelines');assert.equal(targets.length,1);assert.equal(targets[0].id,observation.actualTargetSymbolId);
 const last=observation.queries.at(-1);assert.deepEqual(last.context,{nativeSymbolId:observation.actualTargetSymbolId,name:'guidelines'});
 const reproduced=last.state==='failed'&&last.failure?.code===-32603&&typeof last.failure.message==='string'&&last.failure.message.includes('checker.TypeData is *checker.TypeReference, not *checker.TupleType');
 if(reproduced){assert(failure);for(const key of ['name','message','code']){assert.equal(failure[key],last.failure[key]);identity(receipt.failure?.[key],failure[key]);}identity(receipt.failure.stack,failure.stack);}
 else {assert(['returned','undefined','null'].includes(last.state),'Unrecognized API failure');assert.equal(last.failure,undefined);assert.equal(failure,undefined);assert.equal(receipt.failure,undefined);}
 const exitCode=reproduced?1:0;exactClose(child.lifecycle,exitCode);assert.equal(child.lifecycle.ownedChildHandleReturned,true);
 const expectedFailures=reproduced?['child-exit','child-close']:[];assert.deepEqual(child.lifecycle.failures.map(row=>row.kind),expectedFailures,'Independent driver failure');
 if(reproduced){assert.deepEqual(child.failure,child.lifecycle.failures[0]);assert.equal(child.failure.message,'Node structural driver exit 1/null');assert.equal(child.lifecycle.failures[1].message,'Node structural driver close 1/null');}else assert.equal(child.failure,undefined);
 assert.equal(child.lifecycle.watchdogs.length,1);assert.equal(child.lifecycle.watchdogs[0].kind,'driver-deadline');assert.equal(child.lifecycle.watchdogs[0].milliseconds,plan.limits.driverTimeoutMs);
 let captured=0;for(const name of ['stdout','stderr']){const pipe=child.lifecycle.stdio[name];assert.equal(pipe.available,true);assert.equal(pipe.endObserved,true);assert.equal(pipe.finishObserved,true);assert.equal(pipe.closeObserved,true);assert.deepEqual(pipe.errors,[]);
  const stream=child.streams[name];for(const key of ['observedBytes','capturedBytes','omittedBytes'])assert(Number.isSafeInteger(stream[key])&&stream[key]>=0);assert.equal(stream.omittedBytes,0);assert.equal(stream.observedBytes,stream.capturedBytes);
  const bytes=Buffer.isBuffer(child[name])?child[name]:Buffer.from(child[name].data);assert.equal(bytes.length,stream.capturedBytes);captured+=bytes.length;}
 assert(captured<=plan.limits.snapshotBytes);
 exactClose(native,0);assert.deepEqual(native.failures,[]);for(const pipe of Object.values(native.stdio)){assert.deepEqual(pipe.errors,[]);if(pipe.available){assert.equal(pipe.closeObserved,true);assert.equal(pipe.finishObserved,true);}}
 const joined={exitCode:0,signalCode:null,exitAwaited:true,closeAwaited:true,physicalCloseObserved:true};for(const[key,value]of Object.entries(joined))assert.equal(lifecycle[key],value);assert.deepEqual(lifecycle.observation,native);assert.deepEqual(receipt.lifecycle,joined);
 assert.equal(receipt.ownedNativeHandles,1);const summary=receipt.nativeChildLifecycle;for(const key of ['spawned','exitObserved','closeObserved','exit','close','childStateAtClose'])assert.deepEqual(summary[key],native[key]);assert.equal(summary.failureCount,0);assert.equal(summary.terminationRequestCount,0);assert.equal(summary.eventCount,native.events.length);assert.equal(summary.watchdogCount,native.watchdogs.length);
 for(const[name,pipe]of Object.entries(native.stdio)){const s=summary.stdio[name];for(const key of ['available','endObserved','finishObserved','closeObserved'])assert.equal(s[key],pipe[key]);assert.equal(s.errorCount,pipe.errors.length);}
 assert.deepEqual(cleanup.map(row=>row.name),['view.dispose','API.close','guard.finish']);assert(cleanup.every(row=>row.completed===true&&row.error===undefined));assert.deepEqual(receipt.nativeCleanup,cleanup);
 assert.deepEqual(receipt.protocol,protocol);assert.equal(protocol.complete,true);assert.equal(protocol.omittedRawBytes,0);assert.equal(protocol.writeFailure,false);assert.equal(protocol.overflow,false);
 return {reproduced,status:reproduced?'reproduced-pinned-native-api-blocker':'minimal-path-did-not-reproduce-panic',driverExit:exitCode};
}
