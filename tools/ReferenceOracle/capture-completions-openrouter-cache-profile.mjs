// Additive offline capture. Source and SDK files stay byte-identical to their existing lock.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import { fileURLToPath,pathToFileURL } from 'node:url';
const program=fileURLToPath(import.meta.url),repo=path.resolve(path.dirname(program),'../..');
const [oracleRoot,output]=process.argv.slice(2);assert(oracleRoot&&output,'Explicit existing oracle and fresh output required');
const hash=b=>crypto.createHash('sha256').update(b).digest('hex'),pin=p=>{const b=fs.readFileSync(p);return {bytes:b.length,sha256:hash(b)}};
const lockPath=path.join(repo,'tools/ReferenceOracle/openai-completions-sdk-lifecycle.lock.json');
assert.equal(pin(lockPath).sha256,'8249a6aa25b4386e5b58917acb303ea9aaf6a96aa3cc1263d125ff3a2d4a8172');
assert.equal(pin(process.execPath).sha256,'3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237');
const lock=JSON.parse(fs.readFileSync(lockPath,'utf8')),pinned=new Map([...lock.loadedModules,...lock.sourceHashes].map(f=>[path.resolve(oracleRoot,f.path),f]));
for(const [p,f]of pinned)assert.deepEqual(pin(p),{bytes:f.bytes,sha256:f.sha256});
const loaded=new Set();registerHooks({load(url,context,next){const result=next(url,context);if(url.startsWith('file:'))loaded.add(path.resolve(fileURLToPath(url)));return result}});
let prohibitedNetworkCalls=0;globalThis.fetch=()=>{prohibitedNetworkCalls++;throw Error('Only the authored in-memory fixture fetch is permitted')};
const {stream}=await import(pathToFileURL(path.join(oracleRoot,'upstream/packages/ai/src/api/openai-completions.ts')));
const {EventStream}=await import(pathToFileURL(path.join(oracleRoot,'upstream/packages/ai/src/utils/event-stream.ts')));
function snapshot(value){const ownUndefinedPaths=[];function scan(v,p){if(v===undefined){ownUndefinedPaths.push(p);return}if(v&&typeof v==='object'){for(const key of Object.keys(v))scan(v[key],p+'/'+key.replaceAll('~','~0').replaceAll('/','~1'))}}scan(value,'');return {value:JSON.parse(JSON.stringify(value)),ownUndefinedPaths,serializedJson:JSON.stringify(value)}}
// Observe actual pre-delivery pushes, faithfully calling the original method. No SDK shim or event rewrite.
const originalPush=EventStream.prototype.push;let activeEmissions=null;
EventStream.prototype.push=function(event){activeEmissions?.push(snapshot(event));return originalPush.call(this,event)};
const model={id:'anthropic/offline-cache-reasoner',api:'openai-completions',provider:'openrouter',baseUrl:'https://openrouter-cache.invalid/v1',name:'Offline OpenRouter cache profile',reasoning:true,input:['text','image'],contextWindow:8192,maxTokens:512,cost:{input:0,output:0,cacheRead:0,cacheWrite:0}};
const tools=[{name:'inspect',description:'Inspect authored data.',parameters:{type:'object',properties:{value:{type:'number'}},required:['value']}},{name:'finish',description:'Finish authored data.',parameters:{type:'object',properties:{},additionalProperties:false}}];
const system={role:'system',content:'Cache instruction.',sections:{context:'Stable section.'},toolsAdded:tools,timestamp:123};
const user={role:'user',content:[{type:'text',text:'First part.'},{type:'image',data:'AA==',mimeType:'image/png'},{type:'text',text:'Last part.'}],timestamp:123};
const assistant={role:'assistant',api:model.api,provider:model.provider,model:model.id,content:[{type:'text',text:'Prior assistant.'}],stopReason:'stop',timestamp:123,usage:{input:0,output:0,cacheRead:0,cacheWrite:0,totalTokens:0,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}}};
const profiles=[
 {id:'short-text-image',retention:'short'}, {id:'none-text-image',retention:'none'}, {id:'long-text-image',retention:'long'},
 {id:'long-unsupported-falls-back',retention:'long',supportsLong:false}, {id:'short-no-session',retention:'short',noSession:true},
 {id:'long-no-session',retention:'long',noSession:true}, {id:'short-empty-session',retention:'short',sessionId:''},
 {id:'short-affinity-disabled',retention:'short',affinity:false}, {id:'short-openai-affinity-format',retention:'short',format:'openai'},
 {id:'short-caller-affinity-override',retention:'short',modelHeaders:{'x-session-id':'model-affinity','x-authored':'model'},headers:{'x-session-id':'caller-affinity','x-authored':'caller'}},
 {id:'short-caller-affinity-remove',retention:'short',headers:{'x-session-id':null}},
 {id:'none-retains-caller-affinity',retention:'none',headers:{'x-session-id':'caller-affinity'}},
 {id:'short-image-tail-fallback',retention:'short',history:'image-tail'},
 {id:'short-assistant-tail',retention:'short',history:'assistant-tail'},
 {id:'short-empty-instruction',retention:'short',history:'empty-instruction'},
 {id:'short-mid-instruction',retention:'short',history:'mid-instruction',midSystem:true},
 {id:'short-tool-history-no-declarations',retention:'short',history:'tool-history'},
 {id:'short-non-anthropic-control',retention:'short',modelId:'openai/offline-cache-reasoner'},
 {id:'long-endpoint-only-control',retention:'long',provider:'authored-compatible',baseUrl:'https://openrouter.ai/api/v1'},
 {id:'none-real-tool-continuation',retention:'none',roundtrip:true},
 {id:'short-real-tool-continuation',retention:'short',roundtrip:true},
 {id:'long-real-tool-continuation',retention:'long',roundtrip:true}
];
const wire=chunks=>chunks.map(c=>'data: '+JSON.stringify(c)+'\n\n').join('')+'data: [DONE]\n\n';
const finalUsage={prompt_tokens:15,completion_tokens:9,total_tokens:24,prompt_tokens_details:{cached_tokens:3}};
const firstWire=wire([{choices:[{delta:{reasoning_content:'Cache reasoning.',reasoning_details:[{type:'reasoning.text',text:'Cache reasoning.',index:0}]}}]},
 {choices:[{delta:{content:'Before inspection.'}}]},
 {choices:[{delta:{tool_calls:[{index:0,id:'cache-call',type:'function',function:{name:'inspect',arguments:'{"value":'}}]}}]},
 {choices:[{delta:{tool_calls:[{index:0,function:{arguments:'7}'}}]},finish_reason:'tool_calls'}],usage:finalUsage}]);
const finalWire=wire([{choices:[{delta:{reasoning_content:'Final cache reasoning.'}}]},
 {choices:[{delta:{content:'Cache profile complete.'}}]}, {choices:[{delta:{},finish_reason:'stop'}],usage:finalUsage}]);
const observations=[],oldNow=Date.now;Date.now=()=>123;
try{for(const profile of profiles){
 const selected={...model,id:profile.modelId??model.id,provider:profile.provider??model.provider,baseUrl:profile.baseUrl??model.baseUrl,
  ...(profile.modelHeaders?{headers:profile.modelHeaders}:{}),compat:{...(profile.supportsLong===false?{supportsLongCacheRetention:false}:{}),...(profile.affinity===false?{sendSessionAffinityHeaders:false}:{}),...(profile.format?{sessionAffinityFormat:profile.format}:{}),...(profile.midSystem?{supportsMidConvoSystemMessages:true}:{} )}};
 let messages=[structuredClone(system),structuredClone(user)];
 if(profile.history==='image-tail')messages.push({role:'user',content:[{type:'image',data:'AQ==',mimeType:'image/jpeg'}],timestamp:123});
 if(profile.history==='assistant-tail')messages.push({...structuredClone(assistant),provider:selected.provider,model:selected.id});
 if(profile.history==='empty-instruction')messages=[{role:'system',content:'',timestamp:123},structuredClone(user)];
 if(profile.history==='mid-instruction')messages.push({role:'system',content:'Later instruction.',timestamp:123});
 if(profile.history==='tool-history')messages=[{role:'system',content:'Cache instruction.',timestamp:123},structuredClone(user),{...structuredClone(assistant),content:[{type:'toolCall',id:'prior-call',name:'inspect',arguments:{value:1}}],stopReason:'toolUse'},{role:'toolResult',toolCallId:'prior-call',toolName:'inspect',content:[{type:'text',text:'Prior tool result.'}],isError:false,timestamp:123}];
 const options={apiKey:'authored-inert-cache-key',maxTokens:256,temperature:0.25,cacheRetention:profile.retention,
  ...(!profile.noSession?{sessionId:profile.sessionId??'cache-profile-session'}:{}),...(profile.headers?{headers:profile.headers}:{})};
 const initialHistory=structuredClone(messages),turns=[];
 for(let turn=0;turn<(profile.roundtrip?2:1);turn++){
  const context={messages:structuredClone(messages)},original=JSON.stringify(context),requests=[],emissions=[],deliveredEvents=[];
  let payloadCalls=0,responseCalls=0;activeEmissions=emissions;const responseWire=profile.roundtrip&&turn===0?firstWire:finalWire;
  const run=stream(selected,context,{...options,onPayload:()=>{payloadCalls++},onResponse:()=>{responseCalls++},fetch:async(url,request)=>{
   const body=request.body;requests.push({url:String(url),method:request.method,headers:[...new Headers(request.headers).entries()],body,bodyUtf8Sha256:hash(Buffer.from(body)),bodyJson:JSON.parse(body)});
   return new Response(responseWire,{status:200,headers:{'content-type':'text/event-stream'}})
  }});
  for await(const event of run)deliveredEvents.push(snapshot(event));const result=await run.result();activeEmissions=null;
  assert.equal(requests.length,1);assert.equal(payloadCalls,1);assert.equal(responseCalls,1);assert.equal(JSON.stringify(context),original);assert(emissions.length>4);
  assert.equal(result.stopReason,profile.roundtrip&&turn===0?'toolUse':'stop');
  turns.push({turn,context,wire:responseWire,requests,emissions,deliveredEvents,result,payloadCalls,responseCalls,canonicalContextUnchanged:true});
  if(profile.roundtrip&&turn===0){assert.equal(result.content.filter(b=>b.type==='toolCall').length,1);messages.push(structuredClone(result),{role:'toolResult',toolCallId:'cache-call',toolName:'inspect',content:[{type:'text',text:'Observed seven.'},{type:'image',data:'Ag==',mimeType:'image/png'}],isError:false,timestamp:123,details:{opaque:{keep:null}}});}
 }
 observations.push({id:profile.id,profile,model:selected,options,initialHistory,turns});
}}finally{Date.now=oldNow;EventStream.prototype.push=originalPush;activeEmissions=null}
const loadedPins=[...loaded].sort().map(p=>{const expected=pinned.get(p);assert(expected,'Unpinned module '+p);const actual=pin(p);assert.equal(actual.sha256,expected.sha256);return {relativePath:expected.path,...actual}});assert.equal(prohibitedNetworkCalls,0);
const report={schemaVersion:1,kind:'unchanged-pi-sdk-openrouter-cache-profile-capture',sourceSha:'d86654abb8862e201933517d6f1fce9f88dd117f',sourceLock:pin(lockPath),captureProgram:pin(program),node:pin(process.execPath),sourceFilesVerified:pinned.size,actualLoadedSourceFiles:loadedPins,sourceOrSdkEdits:0,originalFixtureEdits:0,dependencyAcquisitionAttempts:0,prohibitedNetworkCalls,emissionBoundary:'Actual EventStream.push entry; observer calls original method without alteration',observations};
fs.writeFileSync(output,JSON.stringify(report,null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({profiles:observations.length,turns:observations.reduce((n,o)=>n+o.turns.length,0),sourceFilesVerified:pinned.size,loaded:loadedPins.length,networkCalls:prohibitedNetworkCalls,report:{path:output,...pin(output)}}));
