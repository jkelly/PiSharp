import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import zlib from 'node:zlib';
import cp from 'node:child_process';
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..'),[oracleRoot,output]=process.argv.slice(2);
assert(oracleRoot&&output,'Supply existing pinned oracle and fresh output');
const hash=b=>crypto.createHash('sha256').update(b).digest('hex'),pin=p=>{const b=fs.readFileSync(p);return {bytes:b.length,sha256:hash(b)};};
const lockPath=path.join(repo,'tools/ReferenceOracle/openai-completions-sdk-lifecycle.lock.json'),inputPath=path.join(repo,'fixtures/reference/openai-completions-sdk-lifecycle/input.json');
assert.equal(pin(lockPath).sha256,'8249a6aa25b4386e5b58917acb303ea9aaf6a96aa3cc1263d125ff3a2d4a8172');
assert.equal(pin(inputPath).sha256,'2e35c47906fd8c52c0d156dc2af4621f746fdcdb33fb319fecc78abaa438bd2e');
assert.equal(pin(process.execPath).sha256,'3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237');
const lock=JSON.parse(fs.readFileSync(lockPath,'utf8')),input=JSON.parse(fs.readFileSync(inputPath,'utf8')),pinned=new Map();
for(const e of [...lock.loadedModules,...lock.sourceHashes]){const p=path.resolve(oracleRoot,e.path),a=pin(p);assert.equal(a.bytes,e.bytes);assert.equal(a.sha256,e.sha256);pinned.set(p,{relativePath:e.path,...a,authority:'existing-source-lifecycle-lock'});}
// Existing archives are read and validated in memory; nothing is downloaded or extracted.
const dependencies=[];
for(const dependency of lock.environmentPins.dependencies){
 const archive=path.join(oracleRoot,'archives',dependency.name+'-'+dependency.version+'.tgz');assert.equal(pin(archive).sha256,dependency.archiveSha256);
 const tar=zlib.gunzipSync(fs.readFileSync(archive));let position=0,longName=null,paxPath=null,count=0;
 while(position+512<=tar.length){
  const header=tar.subarray(position,position+512);if(header.every(x=>x===0))break;
  const str=(offset,size)=>header.subarray(offset,offset+size).toString('utf8').split('\0')[0];
  const size=parseInt(str(124,12).trim()||'0',8),type=str(156,1),prefix=str(345,155),name=longName??paxPath??(prefix?prefix+'/':'')+str(0,100),bytes=tar.subarray(position+512,position+512+size);position+=512+Math.ceil(size/512)*512;
  if(type==='L'){longName=bytes.toString('utf8').split('\0')[0];continue;}
  if(type==='x'){const record=bytes.toString('utf8');const match=/(?:^|\n)\d+ path=([^\n]+)\n/.exec(record);paxPath=match?.[1]??null;continue;}
  longName=null;paxPath=null;if(type!==''&&type!=='0')continue;
  assert(name.startsWith('package/')&&!name.includes('../'),'Unexpected archive member '+name);
  const relative=name.slice('package/'.length),installed=path.join(oracleRoot,'node_modules',dependency.name,relative);assert.deepEqual(fs.readFileSync(installed),bytes,'Installed file differs from pinned existing archive: '+name);
  const a={bytes:bytes.length,sha256:hash(bytes),relativePath:path.relative(oracleRoot,installed).replaceAll('\\','/'),authority:'existing-pinned-archive:'+dependency.archiveSha256};
  const old=pinned.get(path.resolve(installed));if(old)assert.equal(old.sha256,a.sha256);pinned.set(path.resolve(installed),a);count++;
 }
 dependencies.push({name:dependency.name,version:dependency.version,archive:pin(archive),installedFilesVerifiedAgainstArchive:count});
}
const sourceRoot=path.join(oracleRoot,'upstream'),git='C:/Program Files/Git/cmd/git.exe';
assert.equal(cp.execFileSync(git,['-C',sourceRoot,'rev-parse','HEAD'],{encoding:'utf8',windowsHide:true}).trim(),input.sourceSha);
const entries=cp.execFileSync(git,['-C',sourceRoot,'ls-tree','-r','-z','HEAD'],{maxBuffer:4*1024*1024,windowsHide:true}).toString('utf8').split('\0').filter(Boolean).map(x=>{const m=/^\d+ blob ([0-9a-f]+)\t(.+)$/.exec(x);return m?{blob:m[1],relative:m[2]}:null;}).filter(x=>x&&x.relative.endsWith('.ts')&&x.relative.startsWith('packages/'));
const batch=cp.execFileSync(git,['-C',sourceRoot,'cat-file','--batch'],{input:entries.map(x=>x.blob).join('\n')+'\n',maxBuffer:128*1024*1024,windowsHide:true});let at=0;
for(const entry of entries){const end=batch.indexOf(10,at),line=batch.subarray(at,end).toString('utf8'),match=/^([0-9a-f]+) blob (\d+)$/.exec(line);assert(match);assert.equal(match[1],entry.blob);const size=Number(match[2]),bytes=batch.subarray(end+1,end+1+size);at=end+1+size+1;const p=path.join(sourceRoot,entry.relative);assert.deepEqual(fs.readFileSync(p),bytes,'Upstream source differs from immutable Git object '+entry.relative);const a={bytes:size,sha256:hash(bytes),relativePath:'upstream/'+entry.relative,gitBlob:entry.blob,authority:'immutable-upstream-git:'+input.sourceSha};const old=pinned.get(path.resolve(p));if(old)assert.equal(old.sha256,a.sha256);pinned.set(path.resolve(p),a);}
const loaded=new Set();registerHooks({resolve(specifier,context,next){if(specifier==='@earendil-works/pi-ai')return {url:pathToFileURL(path.join(sourceRoot,'packages/ai/src/index.ts')).href,shortCircuit:true};return next(specifier,context);},load(url,context,next){if(url.startsWith('file:')){const p=path.resolve(fileURLToPath(url)),expected=pinned.get(p);assert(expected,'Unpinned module '+p);assert.equal(pin(p).sha256,expected.sha256);loaded.add(p);}return next(url,context);}});
let prohibitedNetworkCalls=0;globalThis.fetch=()=>{prohibitedNetworkCalls++;throw new Error('Only authored injected fetch permitted');};
const {runAgentLoop}=await import(pathToFileURL(path.join(sourceRoot,'packages/agent/src/agent-loop.ts')));
const {stream}=await import(pathToFileURL(path.join(sourceRoot,'packages/ai/src/api/openai-completions.ts')));

const fixturePath=path.join(repo,'fixtures/native/completions-image-agent-source.json');assert.equal(pin(fixturePath).sha256,'56f2ee42059897b387e44a6f9300cd0e05f16a00cd1c40fea3ab364736e0f593');
const reference=JSON.parse(fs.readFileSync(fixturePath,'utf8')).observations.find(o=>o.profile.id==='mixed-images-vision-plain');
const profiles=[{id:'progress-identity',patch:null},{id:'progress-nullish',patch:{content:null,details:null,usage:null}},{id:'progress-image-patch',patch:{content:[{type:'image',data:'after',mimeType:'image/jpeg'}],details:null,usage:null}},{id:'progress-empty-patch',patch:{content:[]}}];
const observations=[],originalNow=Date.now;Date.now=()=>123;
try{for(const profile of profiles){
 const requests=[],events=[],contexts=[],trace=[],progressUpdates=[];let effects=0,afterCalls=0,releaseUpdate,enterUpdate;
 const updateEntered=new Promise(r=>enterUpdate=r),updateReleased=new Promise(r=>releaseUpdate=r);
 const produced=structuredClone(reference.rawResults[0].result);
 const tool={name:'inspect',description:'An authored inert adapter.',parameters:{type:'object',properties:{value:{type:'number'},keep:{type:'null'}},required:['value','keep'],additionalProperties:false},execute:async(id,args,signal,onUpdate)=>{effects++;trace.push('execute');onUpdate(structuredClone(produced));trace.push('executor-return');return produced;}};
 const streamFn=(model,llmContext,configuration)=>stream(model,llmContext,{...configuration,fetch:async(url,request)=>{requests.push({url:String(url),method:request.method,body:request.body,bodyUtf8Sha256:hash(Buffer.from(request.body)),bodyJson:JSON.parse(request.body)});assert(requests.length<=2);return new Response(requests.length===1?reference.firstResponseWire:reference.secondResponseWire,{status:200,headers:{'content-type':'text/event-stream'}});}});
 const config={...reference.options,model:reference.model,convertToLlm:messages=>{contexts.push(structuredClone(messages));return messages;},afterToolCall:async(call)=>{afterCalls++;trace.push('after-hook');assert.deepEqual(call.result,produced);return structuredClone(profile.patch);}};
 const run=runAgentLoop([structuredClone(reference.initialMessages.at(-1))],{messages:[structuredClone(reference.initialMessages[0])],tools:[tool]},config,async event=>{events.push(structuredClone(event));if(event.type==='tool_execution_update'){progressUpdates.push(structuredClone(event.partialResult));trace.push('progress-enter');enterUpdate();await updateReleased;trace.push('progress-release');}},undefined,streamFn);
 await updateEntered;assert.equal(requests.length,1);assert.equal(effects,1);assert.equal(afterCalls,0);assert(!events.some(e=>e.type==='tool_execution_end'));trace.push('held-proof');releaseUpdate();const result=await run;
 assert.equal(afterCalls,1);assert.equal(requests.length,2);assert.equal(result.at(-1).stopReason,'stop');
 const finalResult=events.find(e=>e.type==='tool_execution_end').result,toolMessages=result.filter(m=>m.role==='toolResult');assert.equal(toolMessages.length,1);assert.deepEqual(toolMessages[0].content,finalResult.content??[]);
 observations.push({profile,model:reference.model,options:reference.options,initialMessages:contexts[0],contexts,rawProducedResult:produced,progressUpdates,afterCalls,effects,finalResult,toolMessages,requests,events,result,trace,heldProgressBlockedAfterHookAndToolEnd:true,firstResponseWire:reference.firstResponseWire,secondResponseWire:reference.secondResponseWire,actualUnchangedPiAgentLoop:true,actualUnchangedPiProviderAndSdk:true});
}}finally{Date.now=originalNow;}
assert.equal(prohibitedNetworkCalls,0);const loadedPins=[...loaded].sort().map(p=>{const expected=pinned.get(p);assert.equal(pin(p).sha256,expected.sha256);return expected;});
const report={schemaVersion:1,kind:'actual-pinned-agent-image-progress-and-nullish-after-hook-continuation',sourceSha:input.sourceSha,node:pin(process.execPath),existingSourceLock:pin(lockPath),originalInput:pin(inputPath),fixedImageAgentFixture:pin(fixturePath),captureProgram:pin(fileURLToPath(import.meta.url)),dependencies,immutableGitSourceFilesVerified:entries.length,allExistingSourceLockFilesVerified:230,actualLoadedSourceFiles:loadedPins,sourceOrSdkEdits:0,originalFixtureEdits:0,dependencyAcquisitionAttempts:0,prohibitedNetworkCalls,securityOrCredentialActions:0,observations};
fs.writeFileSync(output,JSON.stringify(report,null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({profiles:observations.length,loaded:loadedPins.length,gitSourcesVerified:entries.length,network:prohibitedNetworkCalls,report:{path:output,...pin(output)}}));
