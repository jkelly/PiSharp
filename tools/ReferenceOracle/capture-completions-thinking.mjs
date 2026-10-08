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
const {stream,streamSimple}=await import(pathToFileURL(path.join(sourceRoot,'packages/ai/src/api/openai-completions.ts')));
const casesPath=path.join(path.dirname(fileURLToPath(import.meta.url)),'completions-thinking-cases.json'),cases=JSON.parse(fs.readFileSync(casesPath,'utf8'));
assert.equal(cases.sourceSha,input.sourceSha);
const {EventStream}=await import(pathToFileURL(path.join(sourceRoot,'packages/ai/src/utils/event-stream.ts')));
function snapshot(value){const ownUndefinedPaths=[];function scan(v,p){if(v===undefined){ownUndefinedPaths.push(p);return}if(v&&typeof v==='object')for(const key of Object.keys(v))scan(v[key],p+'/'+key.replaceAll('~','~0').replaceAll('/','~1'))}scan(value,'');return {value:JSON.parse(JSON.stringify(value)),ownUndefinedPaths,serializedJson:JSON.stringify(value)}}
const originalPush=EventStream.prototype.push;let activeEmissions=null;EventStream.prototype.push=function(event){activeEmissions?.push(snapshot(event));return originalPush.call(this,event)};
const usage={prompt_tokens:15,completion_tokens:9,total_tokens:24,prompt_tokens_details:{cached_tokens:3},completion_tokens_details:{reasoning_tokens:4}};
const wire=chunks=>chunks.map(c=>'data: '+JSON.stringify(c)+'\n\n').join('')+'data: [DONE]\n\n';
const firstWire=wire([{choices:[{delta:{reasoning_content:'Thinking before tool.'}}]},{choices:[{delta:{content:'Inspecting.'}}]},{choices:[{delta:{tool_calls:[{index:0,id:'thinking-call',type:'function',function:{name:'inspect',arguments:'{"value":'}}]}}]},{choices:[{delta:{tool_calls:[{index:0,function:{arguments:'7}'}}]},finish_reason:'tool_calls'}],usage}]);
const secondWire=wire([{choices:[{delta:{reasoning_content:'Thinking after tool.'}}]},{choices:[{delta:{content:'Observed seven.'}}]},{choices:[{delta:{},finish_reason:'stop'}],usage}]);
const declaration={name:'inspect',description:'Authored inert inspection.',parameters:{type:'object',properties:{value:{type:'number'}},required:['value'],additionalProperties:false}};
const initialMessages=()=>[{role:'system',content:'Offline thinking instruction.',toolsAdded:[structuredClone(declaration)],timestamp:123},{role:'user',content:'Inspect seven and answer.',timestamp:123}];
function options(p){return {apiKey:'authored-inert-thinking-key',...p.options}}
function fixtureFetch(requests){return async(url,request)=>{const body=request.body;requests.push({url:String(url),method:request.method,headers:[...new Headers(request.headers).entries()],body,bodyUtf8Sha256:hash(Buffer.from(body)),bodyJson:JSON.parse(body)});assert(requests.length<=2);return new Response(requests.length===1?firstWire:secondWire,{status:200,headers:{'content-type':'text/event-stream'}})}}
const directObservations=[],agentObservations=[],oldNow=Date.now;Date.now=()=>123;
try{
 for(const p of cases.direct){
  const messages=initialMessages(),initialHistory=structuredClone(messages),turns=[],requests=[];
  for(let turn=0;turn<2;turn++){
   const context={messages:structuredClone(messages)},canonical=JSON.stringify(context),emissions=[],deliveredEvents=[];let payloadCalls=0,responseCalls=0;
   activeEmissions=emissions;const run=stream(p.model,context,{...options(p),onPayload:()=>{payloadCalls++},onResponse:()=>{responseCalls++},fetch:fixtureFetch(requests)});
   for await(const event of run)deliveredEvents.push(snapshot(event));const result=await run.result();activeEmissions=null;
   assert.equal(requests.length,turn+1);assert.equal(payloadCalls,1);assert.equal(responseCalls,1);assert.equal(JSON.stringify(context),canonical);assert.equal(result.stopReason,turn===0?'toolUse':'stop');
   turns.push({turn,context,wire:turn===0?firstWire:secondWire,requests:[requests[turn]],emissions,deliveredEvents,result,payloadCalls,responseCalls,canonicalContextUnchanged:true});
   if(turn===0){const call=result.content.find(x=>x.type==='toolCall');assert.deepEqual(call.arguments,{value:7});messages.push(structuredClone(result),{role:'toolResult',toolCallId:call.id,toolName:call.name,content:[{type:'text',text:'Observed seven.'}],isError:false,timestamp:123,details:{opaque:{keep:null}}})}
  }
  directObservations.push({id:p.id,case:p,initialHistory,turns});
 }
 for(const p of cases.agent){
  const requests=[],events=[],contexts=[],providerEmissions=[],toolExecutions=[],rawResults=[];
  const tool={...structuredClone(declaration),execute:async(id,args)=>{assert.deepEqual(args,{value:7});toolExecutions.push({id,args:structuredClone(args)});const result={content:[{type:'text',text:'Observed seven.'}],details:{opaque:{keep:null}}};rawResults.push(structuredClone(result));return result}};
  const config={...options(p),model:p.model,convertToLlm:messages=>{contexts.push(structuredClone(messages));return messages},...(Object.hasOwn(p,'requestedThinkingLevel')?{reasoning:p.requestedThinkingLevel}:{})};
  const streamFn=(selected,context,configuration)=>{const emissions=[];providerEmissions.push(emissions);activeEmissions=emissions;return (p.mode==='simple'?streamSimple:stream)(selected,context,{...configuration,fetch:fixtureFetch(requests)})};
  const result=await runAgentLoop([{role:'user',content:'Inspect seven and answer.',timestamp:123}],{messages:[{role:'system',content:'Offline thinking instruction.',timestamp:123}],tools:[tool]},config,event=>events.push(snapshot(event)),undefined,streamFn);activeEmissions=null;
  assert.equal(requests.length,2);assert.equal(toolExecutions.length,1);assert.equal(result.at(-1).stopReason,'stop');
  const finals=result.filter(x=>x.role==='assistant');assert.equal(finals.length,2);assert(finals.every(x=>x.thinkingLevel===(p.requestedThinkingLevel??'off')));
  agentObservations.push({id:p.id,case:p,requests,events,contexts,providerEmissions,toolExecutions,rawResults,result,firstResponseWire:firstWire,secondResponseWire:secondWire,actualUnchangedPiAgentLoop:true,actualUnchangedPiProviderAndSdk:true,providerEntryPoint:p.mode==='simple'?'streamSimple':'stream',requestedStamp:p.requestedThinkingLevel??'off'});
 }
}finally{Date.now=oldNow;EventStream.prototype.push=originalPush;activeEmissions=null}
assert.equal(prohibitedNetworkCalls,0);
const loadedPins=[...loaded].sort().map(p=>{const expected=pinned.get(p);assert(expected,'Unpinned loaded module '+p);const actual=pin(p);assert.equal(actual.sha256,expected.sha256);return {...expected,...actual}});
const report={schemaVersion:1,status:'SOURCE PREPARATION ONLY; NO NATIVE IMPLEMENTATION OR ACCEPTANCE',kind:'unchanged-pinned-thinking-format-budget-and-agent-capture',sourceSha:input.sourceSha,sourceLock:pin(lockPath),captureProgram:pin(fileURLToPath(import.meta.url)),casesFile:pin(casesPath),node:pin(process.execPath),sourceGitTypeScriptFilesVerified:entries.length,sourceFilesVerified:pinned.size,dependencies,actualLoadedSourceFiles:loadedPins,sourceOrSdkEdits:0,originalFixtureEdits:0,dependencyAcquisitionAttempts:0,prohibitedNetworkCalls,emissionBoundary:'Actual EventStream.push entry calling original unchanged; immediate JSON/presence snapshots retained',directObservations,agentObservations};
fs.writeFileSync(output,JSON.stringify(report,null,2)+'\n',{flag:'wx'});
console.log(JSON.stringify({directCases:directObservations.length,providerTurns:directObservations.reduce((n,p)=>n+p.turns.length,0),providerPublications:directObservations.reduce((n,p)=>n+p.turns.reduce((m,t)=>m+t.emissions.length,0),0),agentCases:agentObservations.length,agentProviderTurns:agentObservations.reduce((n,p)=>n+p.requests.length,0),agentEvents:agentObservations.reduce((n,p)=>n+p.events.length,0),agentProviderPublications:agentObservations.reduce((n,p)=>n+p.providerEmissions.reduce((m,t)=>m+t.length,0),0),sourceFilesVerified:pinned.size,sourceGitTypeScriptFilesVerified:entries.length,loaded:loadedPins.length,networkCalls:prohibitedNetworkCalls,report:{path:output,...pin(output)}}));
