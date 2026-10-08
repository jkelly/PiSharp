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

const model={type:'chat',id:'anthropic/offline-binding-reasoner',api:'openai-completions',provider:'openrouter',baseUrl:'https://openrouter-binding.invalid/v1',name:'Offline bound model',reasoning:true,input:['text','image'],contextWindow:8192,maxTokens:512,cost:{input:2.75,output:4.25,cacheRead:0.4,cacheWrite:1.125}};
const tools=[{name:'inspect',description:'Inspect authored data.',parameters:{type:'object',properties:{value:{type:'number'}},required:['value']},constrainedSampling:{type:'json_schema',strict:'prefer'}}];
const system={role:'system',content:'Bound model instruction.',sections:{context:'Stable binding section.'},toolsAdded:tools,timestamp:123,opaque:{keep:null}};
const user={role:'user',content:[{type:'text',text:'Bind this complete request.'},{type:'image',data:'Aw==',mimeType:'image/png'}],timestamp:123};
const profiles=[
 {id:'bound-default-anthropic-short'}, {id:'bound-default-anthropic-none',retention:'none'}, {id:'bound-default-anthropic-long',retention:'long'},
 {id:'bound-default-openai-family',modelId:'openai/offline-binding-reasoner'}, {id:'bound-default-other-family',modelId:'meta-llama/offline-binding-reasoner'},
 {id:'bound-default-prefix-case-control',modelId:'Anthropic/offline-binding-reasoner'}, {id:'bound-nonreasoning',reasoning:false}, {id:'bound-text-only',input:['text']},
 {id:'bound-null-compat',compat:null}, {id:'bound-store-and-usage-disabled',compat:{supportsStore:false,supportsUsageInStreaming:false}},
 {id:'bound-nullish-request-flags',compat:{supportsStore:null,supportsUsageInStreaming:null,supportsDeveloperRole:null,cacheControlFormat:null,sendSessionAffinityHeaders:null,sessionAffinityFormat:null,supportsLongCacheRetention:null}},
 {id:'bound-developer-disabled',compat:{supportsDeveloperRole:false}},
 {id:'bound-max-tokens-legacy-field',compat:{maxTokensField:'max_tokens'}}, {id:'bound-strict-function-tools',compat:{supportsStrictMode:true}},
 {id:'bound-mid-system-update',compat:{supportsMidConvoSystemMessages:true},history:'mid-system'},
 {id:'bound-named-tool-and-assistant-bridge',compat:{requiresToolResultName:true,requiresAssistantAfterToolResult:true},history:'tool-history'},
 {id:'bound-thinking-as-text',compat:{requiresThinkingAsText:true},history:'thinking-history'},
 {id:'bound-reasoning-content-required',compat:{requiresReasoningContentOnAssistantMessages:true},history:'plain-assistant'},
 {id:'bound-openai-thinking-format',compat:{thinkingFormat:'openai'},effort:'high',map:{high:'medium'}},
 {id:'bound-openai-effort-unsupported',compat:{thinkingFormat:'openai',supportsReasoningEffort:false},effort:'high'},
 {id:'bound-openrouter-effort-unsupported',compat:{supportsReasoningEffort:false},effort:'high',map:{high:'low'}},
 {id:'bound-off-null-map',map:{off:null}}, {id:'bound-off-empty-map',map:{off:''}},
 {id:'bound-explicit-cache-other-family',modelId:'meta-llama/offline-binding-reasoner',compat:{cacheControlFormat:'anthropic'}},
 {id:'bound-explicit-cache-endpoint-detection',provider:'authored-compatible',baseUrl:'https://openrouter.ai/api/v1',compat:{cacheControlFormat:'anthropic'}},
 {id:'bound-long-cache-unsupported',retention:'long',compat:{supportsLongCacheRetention:false}},
 {id:'bound-openai-affinity',compat:{sessionAffinityFormat:'openai'}}, {id:'bound-nosession-affinity',compat:{sessionAffinityFormat:'openai-nosession'}},
 {id:'bound-affinity-disabled',compat:{sendSessionAffinityHeaders:false}},
 {id:'bound-model-and-caller-headers',modelHeaders:{'x-session-id':'model-affinity','x-bound':'model'},headers:{'x-session-id':'caller-affinity','x-bound':'caller'}},
 {id:'bound-routing-empty-presence',compat:{openRouterRouting:{}}},
 {id:'bound-routing-null-omission',compat:{openRouterRouting:null}},
 {id:'bound-routing-complete',compat:{openRouterRouting:{allow_fallbacks:false,require_parameters:true,data_collection:'deny',zdr:true,enforce_distillable_text:false,order:['first','second'],only:['first'],ignore:['third'],quantizations:['fp16','int8'],sort:{by:'price',partition:null},max_price:{prompt:0.2,completion:1.5,image:2,audio:3},preferred_min_throughput:{p50:30,p90:20,p99:10},preferred_max_latency:{p50:1,p90:3,p99:5},opaque:{keep:null}}}},
 {id:'bound-model-sampling-defaults',modelSampling:{top_p:0.8,temperature:0.75,frequency_penalty:0,opaque:{keep:null}}},
 {id:'bound-request-sampling-precedence',modelSampling:{top_p:0.8,temperature:0.75,frequency_penalty:0,opaque:{model:true}},sampling:{top_p:0.6,temperature:null,opaque:{request:true},seed:7}},
 {id:'bound-sampling-named-fields',sampling:{max_completion_tokens:17,store:true,stream_options:{include_usage:false},messages:[{role:'user',content:'Explicit sampling override.'}]}},
 {id:'bound-sampling-overrides-routing',compat:{openRouterRouting:{only:['model']}},sampling:{provider:{only:['request'],allow_fallbacks:false}}},
 {id:'bound-sampling-prototype-numeric-keys',modelSampling:JSON.parse('{"__proto__":{"opaqueInherited":true},"10":"ten","2":"two","constructor":"retained","opaque":null}')},
 {id:'bound-finish-reason-inferred',compat:{supportsFinishReason:false},inferFinish:true},
 {id:'bound-zero-cost-control',cost:{input:0,output:0,cacheRead:0,cacheWrite:0}},
 {id:'bound-none-tool-continuation',retention:'none',roundtrip:true}, {id:'bound-short-tool-continuation',roundtrip:true}, {id:'bound-long-tool-continuation',retention:'long',roundtrip:true}
];
const wire=chunks=>chunks.map(c=>'data: '+JSON.stringify(c)+'\n\n').join('')+'data: [DONE]\n\n';
const usage={prompt_tokens:15,completion_tokens:9,total_tokens:24,prompt_tokens_details:{cached_tokens:3}};
const firstWire=wire([{choices:[{delta:{reasoning_content:'Bound reasoning.',reasoning_details:[{type:'reasoning.text',text:'Bound reasoning.',index:0}]}}]},
 {choices:[{delta:{content:'Before bound inspection.'}}]}, {choices:[{delta:{tool_calls:[{index:0,id:'cache-call',type:'function',function:{name:'inspect',arguments:'{"value":'}}]}}]},
 {choices:[{delta:{tool_calls:[{index:0,function:{arguments:'7}'}}]},finish_reason:'tool_calls'}],usage}]);
const finalWire=infer=>wire([{choices:[{delta:{reasoning_content:'Final bound reasoning.'}}]}, {choices:[{delta:{content:'Bound profile complete.'}}]}, {choices:[{delta:{},...(!infer?{finish_reason:'stop'}:{})}],usage}]);
const observations=[],oldNow=Date.now;Date.now=()=>123;
try{for(const profile of profiles){
 const selected={...model,id:profile.modelId??model.id,provider:profile.provider??model.provider,baseUrl:profile.baseUrl??model.baseUrl,
  reasoning:profile.reasoning??model.reasoning,input:profile.input??model.input,cost:profile.cost??model.cost,
  ...(profile.modelHeaders?{headers:profile.modelHeaders}:{}),...(profile.map?{thinkingLevelMap:profile.map}:{}),
  ...(profile.modelSampling?{samplingParams:profile.modelSampling}:{}),...(Object.hasOwn(profile,'compat')?{compat:profile.compat}:{})};
 let messages=[structuredClone(system),structuredClone(user)];
 const assistant={role:'assistant',api:selected.api,provider:selected.provider,model:selected.id,content:[{type:'text',text:'Earlier reply.'}],stopReason:'stop',timestamp:123,usage:{input:0,output:0,cacheRead:0,cacheWrite:0,totalTokens:0,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}}};
 if(profile.history==='mid-system')messages.push({role:'system',content:'Later binding instruction.',sections:{context:null,later:'New section.'},timestamp:123});
 if(profile.history==='plain-assistant')messages.push(assistant);
 if(profile.history==='thinking-history')messages.push({...assistant,content:[{type:'thinking',thinking:'Earlier reasoning.',thinkingSignature:'reasoning_content'},...assistant.content]});
 if(profile.history==='tool-history')messages.push({...assistant,content:[{type:'toolCall',id:'prior-call',name:'inspect',arguments:{value:1}}],stopReason:'toolUse'},{role:'toolResult',toolCallId:'prior-call',toolName:'inspect',content:[{type:'text',text:'Prior result.'}],isError:false,timestamp:123},{role:'user',content:'Continue after tool.',timestamp:123});
 const options={apiKey:'authored-inert-binding-key',maxTokens:256,temperature:0.25,cacheRetention:profile.retention??'short',sessionId:'bound-profile-session',
  ...(profile.effort?{reasoningEffort:profile.effort}:{}),...(profile.headers?{headers:profile.headers}:{}),...(profile.sampling?{samplingParams:profile.sampling}:{})};
 const initialHistory=structuredClone(messages),turns=[];
 for(let turn=0;turn<(profile.roundtrip?2:1);turn++){
  const context={messages:structuredClone(messages)},original=JSON.stringify(context),requests=[],emissions=[],deliveredEvents=[];
  let payloadCalls=0,responseCalls=0;activeEmissions=emissions;const responseWire=profile.roundtrip&&turn===0?firstWire:finalWire(profile.inferFinish);
  const run=stream(selected,context,{...options,onPayload:()=>{payloadCalls++},onResponse:()=>{responseCalls++},fetch:async(url,request)=>{
   const body=request.body;requests.push({url:String(url),method:request.method,headers:[...new Headers(request.headers).entries()],body,bodyUtf8Sha256:hash(Buffer.from(body)),bodyJson:JSON.parse(body)});return new Response(responseWire,{status:200,headers:{'content-type':'text/event-stream'}})
  }});
  for await(const event of run)deliveredEvents.push(snapshot(event));const result=await run.result();activeEmissions=null;
  assert.equal(requests.length,1);assert.equal(payloadCalls,1);assert.equal(responseCalls,1);assert.equal(JSON.stringify(context),original);assert(emissions.length>4);assert.equal(result.stopReason,profile.roundtrip&&turn===0?'toolUse':'stop');
  turns.push({turn,context,wire:responseWire,requests,emissions,deliveredEvents,result,payloadCalls,responseCalls,canonicalContextUnchanged:true});
  if(profile.roundtrip&&turn===0)messages.push(structuredClone(result),{role:'toolResult',toolCallId:'cache-call',toolName:'inspect',content:[{type:'text',text:'Observed seven.'},{type:'image',data:'Ag==',mimeType:'image/png'}],isError:false,timestamp:123,details:{opaque:{keep:null}}});
 }
 observations.push({id:profile.id,profile,model:selected,options,initialHistory,turns});
}}finally{Date.now=oldNow;EventStream.prototype.push=originalPush;activeEmissions=null}
const loadedPins=[...loaded].sort().map(p=>{const expected=pinned.get(p);assert(expected,'Unpinned module '+p);const actual=pin(p);assert.equal(actual.sha256,expected.sha256);return {relativePath:expected.path,...actual}});assert.equal(prohibitedNetworkCalls,0);
const report={schemaVersion:1,kind:'unchanged-pi-sdk-openrouter-model-binding-capture',sourceSha:'d86654abb8862e201933517d6f1fce9f88dd117f',sourceLock:pin(lockPath),captureProgram:pin(program),node:pin(process.execPath),sourceFilesVerified:pinned.size,actualLoadedSourceFiles:loadedPins,sourceOrSdkEdits:0,originalFixtureEdits:0,dependencyAcquisitionAttempts:0,prohibitedNetworkCalls,emissionBoundary:'Actual EventStream.push entry; observer calls original method without alteration',observations};
fs.writeFileSync(output,JSON.stringify(report,null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({profiles:observations.length,turns:observations.reduce((n,o)=>n+o.turns.length,0),events:observations.reduce((n,o)=>n+o.turns.reduce((m,t)=>m+t.emissions.length,0),0),sourceFilesVerified:pinned.size,loaded:loadedPins.length,networkCalls:prohibitedNetworkCalls,report:{path:output,...pin(output)}}));
