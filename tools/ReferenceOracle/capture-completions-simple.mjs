import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import zlib from 'node:zlib';
import cp from 'node:child_process';
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
const [repo,oracleRoot,output]=process.argv.slice(2);
assert(repo,'Supply immutable accepted repo, existing pinned oracle and fresh output');
assert(oracleRoot&&output,'Supply immutable accepted repo, existing pinned oracle and fresh output');
assert.equal(cp.execFileSync('C:/Program Files/Git/cmd/git.exe',['--no-optional-locks','-C',repo,'rev-parse','HEAD'],{encoding:'utf8',windowsHide:true}).trim(),'0671c335007ae03246ee044f460d93069e2a6356');
assert.equal(cp.execFileSync('C:/Program Files/Git/cmd/git.exe',['--no-optional-locks','-C',repo,'status','--porcelain'],{encoding:'utf8',windowsHide:true}).trim(),'');
assert(!fs.existsSync(output),'Fresh capture output required');
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
assert.equal(cp.execFileSync(git,['--no-optional-locks','-C',sourceRoot,'rev-parse','HEAD'],{encoding:'utf8',windowsHide:true}).trim(),input.sourceSha);
const entries=cp.execFileSync(git,['--no-optional-locks','-C',sourceRoot,'ls-tree','-r','-z','HEAD'],{maxBuffer:4*1024*1024,windowsHide:true}).toString('utf8').split('\0').filter(Boolean).map(x=>{const m=/^\d+ blob ([0-9a-f]+)\t(.+)$/.exec(x);return m?{blob:m[1],relative:m[2]}:null;}).filter(x=>x&&x.relative.endsWith('.ts')&&x.relative.startsWith('packages/'));
const batch=cp.execFileSync(git,['--no-optional-locks','-C',sourceRoot,'cat-file','--batch'],{input:entries.map(x=>x.blob).join('\n')+'\n',maxBuffer:128*1024*1024,windowsHide:true});let at=0;
for(const entry of entries){const end=batch.indexOf(10,at),line=batch.subarray(at,end).toString('utf8'),match=/^([0-9a-f]+) blob (\d+)$/.exec(line);assert(match);assert.equal(match[1],entry.blob);const size=Number(match[2]),bytes=batch.subarray(end+1,end+1+size);at=end+1+size+1;const p=path.join(sourceRoot,entry.relative);assert.deepEqual(fs.readFileSync(p),bytes,'Upstream source differs from immutable Git object '+entry.relative);const a={bytes:size,sha256:hash(bytes),relativePath:'upstream/'+entry.relative,gitBlob:entry.blob,authority:'immutable-upstream-git:'+input.sourceSha};const old=pinned.get(path.resolve(p));if(old)assert.equal(old.sha256,a.sha256);pinned.set(path.resolve(p),a);}
const loaded=new Set();registerHooks({resolve(specifier,context,next){if(specifier==='@earendil-works/pi-ai')return {url:pathToFileURL(path.join(sourceRoot,'packages/ai/src/index.ts')).href,shortCircuit:true};return next(specifier,context);},load(url,context,next){if(url.startsWith('file:')){const p=path.resolve(fileURLToPath(url)),expected=pinned.get(p);assert(expected,'Unpinned module '+p);assert.equal(pin(p).sha256,expected.sha256);loaded.add(p);}return next(url,context);}});
let prohibitedNetworkCalls=0;globalThis.fetch=()=>{prohibitedNetworkCalls++;throw new Error('Only authored injected fetch permitted');};
// Authored future capture program. No native assembly is imported or required.
const {streamSimple}=await import(pathToFileURL(path.join(sourceRoot,'packages/ai/src/api/openai-completions.ts')));
const {buildBaseOptions}=await import(pathToFileURL(path.join(sourceRoot,'packages/ai/src/api/simple-options.ts')));
const {getSupportedThinkingLevels,clampThinkingLevel}=await import(pathToFileURL(path.join(sourceRoot,'packages/ai/src/models.ts')));
const {estimateContextTokens}=await import(pathToFileURL(path.join(sourceRoot,'packages/ai/src/utils/estimate.ts')));
const {EventStream}=await import(pathToFileURL(path.join(sourceRoot,'packages/ai/src/utils/event-stream.ts')));
const casePath=path.join(path.dirname(fileURLToPath(import.meta.url)),'completions-simple-cases.json'),spec=JSON.parse(fs.readFileSync(casePath,'utf8'));assert.equal(spec.sourceSha,input.sourceSha);assert.equal(spec.acceptedNativeBase,'0671c335007ae03246ee044f460d93069e2a6356');
const baseModel={type:'chat',id:'offline-simple',name:'Offline simple capture',api:'openai-completions',provider:'offline-simple',baseUrl:'https://simple-capture.invalid/v1',reasoning:true,input:['text','image'],contextWindow:32768,maxTokens:8192,cost:{input:2.75,output:4.25,cacheRead:0.4,cacheWrite:1.125},compat:{supportsStore:false,supportsReasoningEffort:true,supportsDeveloperRole:true,supportsStrictMode:true,maxTokensField:'max_tokens',thinkingFormat:'openai'}};
const declaration={name:'inspect',description:'Authored inert simple inspection.',parameters:{type:'object',properties:{value:{type:'number'}},required:['value'],additionalProperties:false}};
const user=text=>({role:'user',content:text,timestamp:123}),system=(text,timestamp=123)=>({role:'system',content:text,timestamp});
const usage={input:15,output:9,cacheRead:3,cacheWrite:2,totalTokens:29,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}};
const assistant=(fields={})=>({role:'assistant',api:baseModel.api,provider:baseModel.provider,model:baseModel.id,content:[{type:'text',text:'Prior assistant.'}],usage:structuredClone(usage),stopReason:'stop',timestamp:123,...fields});
function contextFor(name){
 const messages=name==='empty'?[]:name==='long'?[system('x'.repeat(2048)),user('Full context.')]:[system('Offline simple instruction.'),user('Answer simple.')];
 if(name==='latest-usage')messages.push(assistant());
 if(name==='fallback-usage-components')messages.push(assistant({usage:{...structuredClone(usage),totalTokens:0}}));
 if(name==='aborted-usage-excluded')messages.push(assistant({stopReason:'aborted',content:[{type:'text',text:'x'.repeat(6000)}]}));
 if(name==='error-usage-excluded')messages.push(assistant({stopReason:'error',content:[{type:'text',text:'x'.repeat(6000)}]}));
 if(name==='later-prefix-invalidates-usage')messages.push(system('Newer prefix.',200),assistant({timestamp:100,usage:{...structuredClone(usage),totalTokens:777}}));
 if(name==='trailing-user-after-usage')messages.push(assistant(),user('z'.repeat(1200)));
 if(name==='images')messages.push({role:'user',timestamp:123,content:[{type:'text',text:'View.'},{type:'image',mimeType:'image/png',data:'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD9sAAAAASUVORK5CYII='}]});
 if(name==='utf16')messages.push(user('\ud83d\ude00\ud801\udc37\ud834\udd1e'));
 if(name==='system-tool-deltas')messages.push({...system('Tool additions.'),toolsAdded:[structuredClone(declaration)]},{...system('Tool removals.'),toolsRemoved:[structuredClone(declaration)]});
 if(name==='thinking-and-arguments')messages.push(assistant({usage:{...structuredClone(usage),totalTokens:0,input:0,output:0,cacheRead:0,cacheWrite:0},content:[{type:'thinking',thinking:'Private reasoning text.'},{type:'toolCall',id:'old-call',name:'inspect',arguments:{value:7,escaped:'\t\ud83d\ude00e\u0301\u6f22'}}]}),{role:'toolResult',toolCallId:'old-call',toolName:'inspect',content:[{type:'text',text:'Prior result.'}],isError:false,timestamp:123});
 if(name==='tool-flow')messages[0]={...messages[0],toolsAdded:[structuredClone(declaration)]};
 return {messages};
}
function snapshot(value){
 const ownUndefinedPaths=[],numberBits=[],functionPaths=[];
 function scan(v,p){if(v===undefined){ownUndefinedPaths.push(p);return}if(typeof v==='function'){functionPaths.push(p);return}if(typeof v==='number'){const b=Buffer.alloc(8);b.writeDoubleBE(v);numberBits.push({path:p,hex:b.toString('hex')});return}if(v&&typeof v==='object')for(const k of Object.keys(v))scan(v[k],p+'/'+k.replaceAll('~','~0').replaceAll('/','~1'))}
 scan(value,'');const serializedJson=JSON.stringify(value);return{...(serializedJson===undefined?{}:{value:JSON.parse(serializedJson)}),ownUndefinedPaths,numberBits,functionPaths,serializedJson};
}
const wire=chunks=>chunks.map(c=>'data: '+JSON.stringify(c)+'\n\n').join('')+'data: [DONE]\n\n';
const terminalUsage={prompt_tokens:15,completion_tokens:9,total_tokens:24,prompt_tokens_details:{cached_tokens:3},completion_tokens_details:{reasoning_tokens:4}};
const answerWire=wire([{choices:[{delta:{content:'Simple answer.'}}]},{choices:[{delta:{},finish_reason:'stop'}],usage:terminalUsage}]);
const toolWire=wire([{choices:[{delta:{content:'Inspecting.'}}]},{choices:[{delta:{tool_calls:[{index:0,id:'simple-call',type:'function',function:{name:'inspect',arguments:'{"value":'}}]}}]},{choices:[{delta:{tool_calls:[{index:0,function:{arguments:'7}'}}]},finish_reason:'tool_calls'}],usage:terminalUsage}]);
// R2 observer and assertions. Authored only; no behavioral execution yet.
const identityMap = new WeakMap(); let nextIdentity = 1;
function identity(value) {
 if ((typeof value !== 'object' || value === null) && typeof value !== 'function') return null;
 if (!identityMap.has(value)) identityMap.set(value, 'object-' + nextIdentity++);
 return identityMap.get(value);
}
function eventObservation(event, phase) {
 const message = event.partial ?? event.message ?? event.error;
 return {...snapshot(event),phase,identities:{event:identity(event),message:identity(message),content:identity(message?.content),blocks:(message?.content??[]).map(identity),arguments:(message?.content??[]).map(b=>identity(b.arguments))}};
}
const originalPush = EventStream.prototype.push;
let activeObserver = null;
EventStream.prototype.push = function(event) {
 if (activeObserver) {
  const record=eventObservation(event,'push'); activeObserver.emissions.push(record);
  activeObserver.held.push({event,phase:'push',ordinal:activeObserver.emissions.length-1,blocks:[...((event.partial??event.message??event.error)?.content??[])]});
 }
 return originalPush.call(this,event);
};
const observations=[],oldNow=Date.now; Date.now=()=>123;
try { for(const profile of spec.cases) {
 const model={...structuredClone(baseModel),...structuredClone(profile.model??{}),compat:{...baseModel.compat,...structuredClone(profile.model?.compat??{})}};
 const context=contextFor(profile.context),canonicalInitial=snapshot(context),turns=[];
 const options={apiKey:'authored-inert-simple-key',cacheRetention:'none',maxRetries:0,...structuredClone(profile.options??{})};
 const signalController=profile.runtimeField==='signal'?new AbortController():null;
 if(signalController)options.signal=signalController.signal;
 if(profile.runtimeField==='telemetryContext')options.telemetryContext={authoredInertOpaqueContext:true};
 if(profile.runtimeField==='transport')options.transport='sse';
 if(profile.runtimeField==='websocketConnectTimeoutMs')options.websocketConnectTimeoutMs=4321;
 if(profile.runtimeField==='env')options.env={PI_CACHE_RETENTION:'none',HTTP_PROXY:'',HTTPS_PROXY:'',ALL_PROXY:''};
 if(profile.auth){delete options.apiKey;options.headers=profile.auth==='authorization-header'?{Authorization:'Bearer authored-inert-header-key'}:profile.auth==='cloudflare-header'?{'cf-aig-authorization':'Bearer authored-inert-gateway-key'}:profile.auth==='empty-header'?{Authorization:'   '}:{};}
 for(let turn=0;turn<(profile.turns??1);turn++) {
  const requests=[],emissions=[],delivered=[],callbacks=[],providerEvents=[],held=[];
  const before=snapshot(context);let synchronousError=null,replacementPayload=null;
  const modelArgument=(selected,kind)=>{assert.equal(selected,model,kind+' actual model reference');return{model:snapshot(selected),modelIdentity:identity(selected)};};
  const checkpoint=async(kind,assertBarrier)=>{
   callbacks.push({kind:kind+'-await-enter',requestCount:requests.length,emissionCount:emissions.length});
   const observedEmissions=emissions.length,observedRequests=requests.length;
   await Promise.resolve();
   assert.equal(emissions.length,observedEmissions,kind+' emits while callback awaits');
   if(assertBarrier==='before-http')assert.equal(requests.length,observedRequests,kind+' sends while callback awaits');
   callbacks.push({kind:kind+'-await-release',requestCount:requests.length,emissionCount:emissions.length});
  };
  const authoredOptions={...options,
   onPayload:async(payload,selected)=>{
    callbacks.push({kind:'payload',...modelArgument(selected,'onPayload'),payload:snapshot(payload)});
    if(profile.hook==='payload-throw')throw new Error('Authored inert payload callback failure');
    if(profile.hook==='payload-async-undefined'||profile.hook==='payload-async-replacement')await checkpoint('payload','before-http');
    if(profile.hook==='payload-async-replacement'){
     replacementPayload={...structuredClone(payload),max_tokens:17,authored_replacement:true};
     delete replacementPayload.stream_options;
     callbacks.push({kind:'payload-replacement',payload:snapshot(replacementPayload)});
     return replacementPayload;
    }
    return undefined;
   },
   onResponse:async(response,selected)=>{
    callbacks.push({kind:'response',...modelArgument(selected,'onResponse'),response:snapshot(response)});
    if(profile.hook==='response-throw')throw new Error('Authored inert response callback failure');
    if(profile.hook==='response-async-await'){assert.equal(emissions.length,0);await checkpoint('response','before-start');}
   },
   onProviderStreamEvent:async(event,selected)=>{
    providerEvents.push({...snapshot(event),eventIdentity:identity(event),...modelArgument(selected,'onProviderStreamEvent')});
    callbacks.push({kind:'provider-event',ordinal:providerEvents.length-1,eventIdentity:identity(event),modelIdentity:identity(selected)});
    if(profile.hook==='provider-throw')throw new Error('Authored inert provider callback failure');
    if(profile.hook==='provider-async-await'&&providerEvents.length===1)await checkpoint('provider','before-normalization');
   },
   fetch:async(url,request)=>{
    const body=request.body;
    requests.push({url:String(url),method:request.method,headers:[...new Headers(request.headers).entries()],body,bodyUtf8Sha256:hash(Buffer.from(body)),bodyJson:JSON.parse(body),requestSignalPresent:request.signal!==undefined,requestSignalAborted:request.signal?.aborted??null});
    callbacks.push({kind:'http-fetch',ordinal:requests.length-1});
    return new Response(profile.turns===2&&turn===0?toolWire:answerWire,{status:200,headers:{'content-type':'text/event-stream'}});
   }
  };
  activeObserver={emissions,held};let run;
  try {run=streamSimple(model,context,authoredOptions);}catch(error){synchronousError=snapshot({name:error.name,message:error.message});}
  if(profile.expectedSynchronousError) {
   assert(synchronousError);assert.equal(synchronousError.value.name,'Error');
   assert.equal(synchronousError.value.message,'No API key for provider: '+model.provider);
   assert.equal(JSON.stringify(context),before.serializedJson,'auth rejection context unchanged');
   for(const ledger of [requests,callbacks,emissions,delivered,providerEvents,held])assert.equal(ledger.length,0,'auth rejection zero effects');
   turns.push({turn,context:before,independentHelperProbe:{executed:false,reason:'No separate helper call on auth-rejected path; this is not a claim about instrumentation of Source helper internals.'},synchronousError,requests,emissions,delivered,callbacks,providerEvents,wire:null,contextUnchanged:true,actualRunReturned:false});
   activeObserver=null;break;
  }
  assert.equal(synchronousError,null);
  for await(const event of run){delivered.push(eventObservation(event,'delivery'));held.push({event,phase:'delivery',ordinal:delivered.length-1,blocks:[...((event.partial??event.message??event.error)?.content??[])]});}
  const result=await run.result();activeObserver=null;
  assert.equal(JSON.stringify(context),before.serializedJson,'provider path context unchanged');
  const expectsError=profile.expectedTerminal==='error';
  assert.equal(requests.length,profile.hook==='payload-throw'?0:1);
  assert.equal(result.stopReason,expectsError?'error':profile.turns===2&&turn===0?'toolUse':'stop');
  if(replacementPayload){assert.deepEqual(requests[0].bodyJson,snapshot(replacementPayload).value);assert.equal(Object.hasOwn(requests[0].bodyJson,'stream_options'),false);}
  const heldAfterTerminal=held.map(h=>({origin:{phase:h.phase,ordinal:h.ordinal},event:eventObservation(h.event,'held-after-terminal'),originalBlocks:h.blocks.map(b=>({identity:identity(b),value:snapshot(b),argumentsIdentity:identity(b.arguments)}))}));
  const resultIdentity=identity(result);
  for(const r of [...emissions,...delivered])if(r.identities.message!==null)assert.equal(r.identities.message,resultIdentity,'actual shared live message reference');
  // Independent helper probe is deliberately after actual provider settlement.
  // Its calls/identity checks are not observations of streamSimple's admission order.
  const resolvedBase=buildBaseOptions(model,context,authoredOptions,authoredOptions.apiKey);
  const forwardedKeys=['temperature','samplingParams','signal','telemetryContext','apiKey','fetch','transport','cacheRetention','sessionId','headers','onPayload','onResponse','onProviderStreamEvent','timeoutMs','websocketConnectTimeoutMs','maxRetries','maxRetryDelayMs','metadata','env'];
  const forwarding=forwardedKeys.map(key=>{assert.equal(Object.hasOwn(resolvedBase,key),true,key+' own property');assert.equal(resolvedBase[key],authoredOptions[key],key+' same reference/value');return{key,ownProperty:true,referenceOrValueEqual:true,inputIdentity:identity(authoredOptions[key]),baseIdentity:identity(resolvedBase[key])};});
  const independentHelperProbe={executed:true,phase:'after-provider-result',admissionOrderEvidence:false,supportedLevels:getSupportedThinkingLevels(model),clampedRequested:options.reasoning?clampThinkingLevel(model,options.reasoning):null,contextEstimate:snapshot(estimateContextTokens(context)),baseOptions:snapshot(resolvedBase),forwarding,callerSignal:signalController?{identity:identity(signalController.signal),aborted:signalController.signal.aborted}:null,opaqueTelemetryOnly:profile.runtimeField==='telemetryContext'};
  turns.push({turn,context:before,independentHelperProbe,synchronousError,requests,emissions,delivered,heldAfterTerminal,callbacks,providerEvents,result:snapshot(result),resultIdentity,wire:profile.turns===2&&turn===0?toolWire:answerWire,contextUnchanged:true,actualRunReturned:true});
  if(profile.turns===2&&turn===0){const call=result.content.find(c=>c.type==='toolCall');assert.deepEqual(call.arguments,{value:7});context.messages.push(structuredClone(result),{role:'toolResult',toolCallId:call.id,toolName:call.name,content:[{type:'text',text:'Observed seven.'}],details:{opaque:null},isError:false,timestamp:123});}
 }
 observations.push({id:profile.id,group:profile.group,case:profile,model:snapshot(model),modelIdentity:identity(model),initialContext:canonicalInitial,finalContext:snapshot(context),turns,actualUnchangedSourceStreamSimple:true,nativeAgentMetadataQualified:false});
}} finally {Date.now=oldNow;EventStream.prototype.push=originalPush;activeObserver=null;}
assert.equal(prohibitedNetworkCalls,0);
const loadedPins=[...loaded].sort().map(p=>{const expected=pinned.get(p);assert(expected);assert.equal(pin(p).sha256,expected.sha256);return expected;});
const executionCounts={cases:observations.length,streamSimpleInvocations:observations.reduce((n,o)=>n+o.turns.length,0),synchronousAuthRejections:observations.reduce((n,o)=>n+o.turns.filter(t=>t.synchronousError!==null).length,0),actualRunObjectsReturned:observations.reduce((n,o)=>n+o.turns.filter(t=>t.actualRunReturned).length,0),actualHttpAttempts:observations.reduce((n,o)=>n+o.turns.reduce((s,t)=>s+t.requests.length,0),0)};
const report={schemaVersion:2,status:'GENUINE PINNED SOURCE SIMPLE CAPTURE IF THIS PROGRAM IS EXECUTED; NO NATIVE PRODUCT ACCEPTANCE',acceptedNativeBase:spec.acceptedNativeBase,sourceSha:input.sourceSha,sourceLock:pin(lockPath),caseSpecification:pin(casePath),captureProgram:pin(fileURLToPath(import.meta.url)),node:pin(process.execPath),sourceFilesVerified:pinned.size,sourceGitTypeScriptFilesVerified:entries.length,actualLoadedSourceFiles:loadedPins,dependencies,sourceOrSdkEdits:0,dependencyAcquisitionAttempts:0,prohibitedNetworkCalls,executionCounts,observations,qualificationCoverage:spec.qualificationCoverage,nativeProductExecutions:0,blockedMetadataCandidateReferenced:false};
fs.writeFileSync(output,JSON.stringify(report,null,2)+'\n',{flag:'wx'});
console.log(JSON.stringify({...executionCounts,report:{path:output,...pin(output)}}));
