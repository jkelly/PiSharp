// Decision controls over the retained real receipt; these do not run a compiler.
import assert from 'node:assert/strict';import fs from 'node:fs';import path from 'node:path';
import {readPlan}from'./full-structural-openai-common.mjs';import{readMinimalEvidence,classifyMinimalEvidence}from'./minimal-native-panic-consumer.mjs';
const args=process.argv.slice(2);assert.equal(args.length,1);const scratch=path.resolve(args[0]),plan=readPlan(),child=JSON.parse(fs.readFileSync(path.join(scratch,'driver.lifecycle.json'))),original=readMinimalEvidence(path.join(scratch,'minimal.json'),plan.limits);
const {bundle,deniedFsRequests,...evidence}=original;let passed=0;
function check(name,mutate,accept=false){const c=structuredClone(child),e=structuredClone(evidence);mutate(c,e);try{if(accept)classifyMinimalEvidence(c,e,plan);else assert.throws(()=>classifyMinimalEvidence(c,e,plan));passed++;console.log('PASS '+name);}catch(error){console.error('FAIL '+name+': '+error.message.slice(0,1500));process.exitCode=1;}}
check('actual retained panic has clean independent driver/native evidence',()=>{},true);
for(const code of [0,2])check('matching panic rejects driver exit '+code,c=>{c.lifecycle.exit.code=c.lifecycle.close.code=code;c.lifecycle.childStateAtClose.exitCode=code;});
check('matching panic rejects signal termination',c=>{c.lifecycle.exit.signal=c.lifecycle.close.signal='SIGTERM';});
check('matching panic rejects mismatched physical close',c=>{c.lifecycle.close.code=0;});
check('matching panic rejects fired deadline',c=>{c.lifecycle.watchdogs[0].fired=true;});
check('matching panic rejects termination request',c=>{c.lifecycle.terminationRequests.push({reason:'deadline'});});
check('matching panic rejects first observer failure',c=>{c.failure={kind:'driver-output-overflow',message:'lost'};});
check('matching panic rejects secondary observer failure',c=>{c.lifecycle.failures.push({kind:'stdout-error',message:'pipe'});});
check('matching panic rejects child error',c=>{c.lifecycle.errors.push({message:'spawn error'});});
check('matching panic rejects stream error',c=>{c.lifecycle.stdio.stderr.errors.push({message:'pipe error'});});
check('matching panic rejects undrained stream',c=>{c.lifecycle.stdio.stdout.endObserved=false;});
check('matching panic rejects omitted output',c=>{c.streams.stderr.omittedBytes=1;});
check('matching panic rejects unreturned child handle',c=>{c.lifecycle.ownedChildHandleReturned=false;});
check('matching panic rejects evidence verification failure',(c,e)=>{e.secondaryFailures.push({stage:'oracle-after',error:{message:'changed pin'}});});
check('matching panic rejects original-query mismatch',(c,e)=>{e.failure.code=-32602;});
check('matching panic rejects early receipt mismatch',(c,e)=>{e.receipt.failure.message.sha256='0'.repeat(64);});
check('matching panic rejects cleanup failure',(c,e)=>{e.cleanup[2].completed=false;});
check('matching panic rejects extra owned native handle',(c,e)=>{e.receipt.ownedNativeHandles=2;});
check('matching panic rejects native first failure',(c,e)=>{e.nativeFirstFailure={message:'deadline'};});
check('matching panic rejects native signal',(c,e)=>{e.native.exit.signal='SIGTERM';});
check('matching panic rejects physical guard denial',(c,e)=>{e.denied.push({operation:'write'});});
check('matching panic rejects changed source identity',(c,e)=>{e.observation.sourceSha256='0'.repeat(64);});
check('matching panic rejects different selected symbol',(c,e)=>{e.observation.actualTargetSymbolId++;});
function success(c,e){const last=e.observation.queries.at(-1);last.state='returned';delete last.failure;delete e.failure;delete e.receipt.failure;c.lifecycle.exit.code=c.lifecycle.close.code=c.lifecycle.childStateAtClose.exitCode=0;c.lifecycle.failures=[];delete c.failure;}
check('clean successful query remains nonreproduction',success,true);
check('successful query rejects driver error',(c,e)=>{success(c,e);c.failure={kind:'stdout-error'};});
check('unrecognized native error cannot masquerade as reproduction',(c,e)=>{e.observation.queries.at(-1).failure.code=-32602;});
if(process.exitCode!==1)console.log(JSON.stringify({passed,actualCompilerExecuted:false,completeRetainedProtocolVerified:true,fullP1Verdict:'HOLD'}));
