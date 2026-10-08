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

const catalogProfiles=[{"file":"fireworks.json","shardSha256":"d6144ba451bd7a0617669527bec1898f97a633f7d7ea5035d8e03234550340c0","model":{"id":"accounts/fireworks/models/kimi-k3","name":"Kimi K3","provider":"fireworks","reasoning":true,"input":["text","image"],"cost":{"input":3,"output":15,"cacheRead":0.3,"cacheWrite":0},"contextWindow":1048576,"maxTokens":131072,"api":"openai-completions","baseUrl":"https://api.fireworks.ai/inference/v1","compat":{"supportsStrictMode":true,"supportsStore":false,"supportsDeveloperRole":false,"sendSessionAffinityHeaders":true,"supportsLongCacheRetention":false,"requiresReasoningContentOnAssistantMessages":true,"thinkingFormat":"openai","supportsMidConvoSystemMessages":true,"supportsMidConvoToolAdditions":true},"thinkingLevelMap":{"off":null,"minimal":null,"low":"low","medium":null,"high":"high","xhigh":null,"max":"max"},"inputLimits":{"images":{"resize":{"maxWidth":2000,"maxHeight":2000,"maxBytes":4718592,"jpegQuality":80}}},"type":"chat"}},{"file":"fireworks.json","shardSha256":"d6144ba451bd7a0617669527bec1898f97a633f7d7ea5035d8e03234550340c0","model":{"id":"accounts/fireworks/routers/kimi-k3-fast","name":"Kimi K3 Fast","provider":"fireworks","reasoning":true,"input":["text","image"],"cost":{"input":4.5,"output":22.5,"cacheRead":0.45,"cacheWrite":0},"contextWindow":1048576,"maxTokens":131072,"api":"openai-completions","baseUrl":"https://api.fireworks.ai/inference/v1","compat":{"supportsStrictMode":true,"supportsStore":false,"supportsDeveloperRole":false,"sendSessionAffinityHeaders":true,"supportsLongCacheRetention":false,"requiresReasoningContentOnAssistantMessages":true,"thinkingFormat":"openai","supportsMidConvoSystemMessages":true,"supportsMidConvoToolAdditions":true},"thinkingLevelMap":{"off":null,"minimal":null,"low":"low","medium":null,"high":"high","xhigh":null,"max":"max"},"inputLimits":{"images":{"resize":{"maxWidth":2000,"maxHeight":2000,"maxBytes":4718592,"jpegQuality":80}}},"type":"chat"}},{"file":"moonshotai-cn.json","shardSha256":"79c9585dc84ee525ebaebbc78b3c55f3d369ec884d4f943488f1e0b14e3587f5","model":{"id":"kimi-k3","name":"Kimi K3","api":"openai-completions","provider":"moonshotai-cn","baseUrl":"https://api.moonshot.cn/v1","reasoning":true,"input":["text","image"],"cost":{"input":3,"output":15,"cacheRead":0.3,"cacheWrite":0},"contextWindow":1048576,"maxTokens":1048576,"compat":{"supportsStore":false,"supportsDeveloperRole":false,"supportsReasoningEffort":true,"maxTokensField":"max_tokens","supportsStrictMode":false,"thinkingFormat":"openai","requiresReasoningContentOnAssistantMessages":true,"supportsMidConvoSystemMessages":true,"supportsMidConvoToolAdditions":true},"thinkingLevelMap":{"off":null,"minimal":null,"low":"low","medium":null,"high":"high","xhigh":null,"max":"max"},"inputLimits":{"images":{"resize":{"maxWidth":2000,"maxHeight":2000,"maxBytes":4718592,"jpegQuality":80}}},"type":"chat"}},{"file":"moonshotai.json","shardSha256":"1da2c4e34f22aef17a07c974d85bdee8e1b68b6dc712e3769c935b3f1e7dadca","model":{"id":"kimi-k3","name":"Kimi K3","api":"openai-completions","provider":"moonshotai","baseUrl":"https://api.moonshot.ai/v1","reasoning":true,"input":["text","image"],"cost":{"input":3,"output":15,"cacheRead":0.3,"cacheWrite":0},"contextWindow":1048576,"maxTokens":1048576,"compat":{"supportsStore":false,"supportsDeveloperRole":false,"supportsReasoningEffort":true,"maxTokensField":"max_tokens","supportsStrictMode":false,"thinkingFormat":"openai","requiresReasoningContentOnAssistantMessages":true,"supportsMidConvoSystemMessages":true,"supportsMidConvoToolAdditions":true},"thinkingLevelMap":{"off":null,"minimal":null,"low":"low","medium":null,"high":"high","xhigh":null,"max":"max"},"inputLimits":{"images":{"resize":{"maxWidth":2000,"maxHeight":2000,"maxBytes":4718592,"jpegQuality":80}}},"type":"chat"}},{"file":"opencode-go.json","shardSha256":"d05b3eb87b81c6e5a94801484e668e660fe60c68f06a972b716d581c3bbd7c25","model":{"id":"kimi-k3","name":"Kimi K3","api":"openai-completions","provider":"opencode-go","baseUrl":"https://opencode.ai/zen/go/v1","reasoning":true,"input":["text","image"],"cost":{"input":3,"output":15,"cacheRead":0.3,"cacheWrite":0},"compat":{"supportsStore":false,"supportsDeveloperRole":false,"supportsStrictMode":true,"maxTokensField":"max_tokens","supportsMidConvoSystemMessages":true,"supportsMidConvoToolAdditions":true},"contextWindow":1048576,"maxTokens":131072,"thinkingLevelMap":{"off":null,"minimal":null,"low":null,"medium":null,"high":null,"xhigh":null,"max":"max"},"inputLimits":{"images":{"resize":{"maxWidth":2000,"maxHeight":2000,"maxBytes":4718592,"jpegQuality":80}}},"type":"chat"}},{"file":"opencode.json","shardSha256":"4adb1cfc19a5294a2d9ea5179089b86364ddbfa31485043a3fc278fa8f7e2e73","model":{"id":"kimi-k3","name":"Kimi K3","api":"openai-completions","provider":"opencode","baseUrl":"https://opencode.ai/zen/v1","reasoning":true,"input":["text","image"],"cost":{"input":3,"output":15,"cacheRead":0.3,"cacheWrite":0},"compat":{"supportsStore":false,"supportsDeveloperRole":false,"supportsStrictMode":true,"maxTokensField":"max_tokens","supportsMidConvoSystemMessages":true,"supportsMidConvoToolAdditions":true},"contextWindow":1048576,"maxTokens":131072,"thinkingLevelMap":{"off":null,"minimal":null,"low":null,"medium":null,"high":null,"xhigh":null,"max":"max"},"inputLimits":{"images":{"resize":{"maxWidth":2000,"maxHeight":2000,"maxBytes":4718592,"jpegQuality":80}}},"type":"chat"}}];
const model={type:'chat',id:'anthropic/offline-anchored-reasoner',api:'openai-completions',provider:'openrouter',baseUrl:'https://anchored-tools.invalid/v1',name:'Offline anchored model',reasoning:true,input:['text','image'],contextWindow:8192,maxTokens:512,cost:{input:2.75,output:4.25,cacheRead:0.4,cacheWrite:1.125},compat:{supportsMidConvoSystemMessages:true,supportsMidConvoToolAdditions:true}};
const tool=name=>({name,description:'Declare '+name+'.',parameters:{type:'object',properties:{value:{type:'number'}},required:['value']},constrainedSampling:{type:'json_schema',strict:'prefer'}});
const alpha=tool('alpha'),inspect=tool('inspect'),omega=tool('omega');
const system={role:'system',content:'Initial anchored instruction.',sections:{context:'Stable anchored section.'},toolsAdded:[alpha],timestamp:123,opaque:{keep:null}};
const user={role:'user',content:[{type:'text',text:'Use anchored declarations.'},{type:'image',data:'Aw==',mimeType:'image/png'}],timestamp:123};
const additions=(overrides={})=>({role:'system',content:'Later anchored instruction.',sections:{context:null,later:'New section.'},toolsAdded:[inspect],timestamp:123,...overrides});
const profiles=[...catalogProfiles.map((entry,i)=>({id:'released-anchored-'+i,catalog:entry})),
 {id:'openrouter-explicit-anchored'}, {id:'tool-only-addition',history:'tool-only'}, {id:'sections-only-addition',history:'sections-only'},
 {id:'no-leading-system',history:'no-head'}, {id:'empty-initial-tools',history:'empty-head'}, {id:'no-initial-tools-field',history:'absent-head'},
 {id:'empty-removal-array-additive',history:'empty-removal'}, {id:'remove-existing-fallback',history:'remove-existing'},
 {id:'remove-unknown-fallback',history:'remove-unknown'}, {id:'remove-all-tools-fallback',history:'remove-all'},
 {id:'identical-redeclaration-fallback',history:'identical'}, {id:'changed-redeclaration-fallback',history:'changed'},
 {id:'duplicate-in-same-message-fallback',history:'same-message'}, {id:'remove-and-readd-ordered-fallback',history:'readd'},
 {id:'multiple-addition-order',history:'multiple'}, {id:'deferred-addition-until-result',history:'pending'},
 {id:'deferred-addition-with-missing-result',history:'missing-result'}, {id:'orphan-result-before-addition',history:'orphan'},
 {id:'cross-model-call-and-thinking-before-addition',history:'cross-model'},
 {id:'system-capability-disabled-fallback',compat:{supportsMidConvoSystemMessages:false,supportsMidConvoToolAdditions:true}},
 {id:'additions-capability-disabled-fallback',compat:{supportsMidConvoSystemMessages:true,supportsMidConvoToolAdditions:false}},
 {id:'nullish-additions-capability-fallback',compat:{supportsMidConvoSystemMessages:true,supportsMidConvoToolAdditions:null}},
 {id:'strict-anchored-declarations',compat:{supportsMidConvoSystemMessages:true,supportsMidConvoToolAdditions:true,supportsStrictMode:true}},
 {id:'developer-disabled-tools-remain-system',compat:{supportsMidConvoSystemMessages:true,supportsMidConvoToolAdditions:true,supportsDeveloperRole:false}},
 {id:'grammar-disabled-function-fallback',history:'grammar-fallback'},
 {id:'anchored-cache-none',retention:'none'}, {id:'anchored-cache-long',retention:'long'},
 {id:'anchored-without-top-level-cache-tool',history:'empty-head',retention:'long'},
 {id:'anchored-inferred-finish',compat:{supportsMidConvoSystemMessages:true,supportsMidConvoToolAdditions:true,supportsFinishReason:false},inferFinish:true},
 {id:'anchored-short-real-tool-continuation',roundtrip:true}, {id:'anchored-none-real-tool-continuation',roundtrip:true,retention:'none'},
 {id:'anchored-long-real-tool-continuation',roundtrip:true,retention:'long'}
];
const wire=chunks=>chunks.map(c=>'data: '+JSON.stringify(c)+'\n\n').join('')+'data: [DONE]\n\n';
const usage={prompt_tokens:15,completion_tokens:9,total_tokens:24,prompt_tokens_details:{cached_tokens:3}};
const firstWire=wire([{choices:[{delta:{reasoning_content:'Bound reasoning.',reasoning_details:[{type:'reasoning.text',text:'Bound reasoning.',index:0}]}}]},
 {choices:[{delta:{content:'Before bound inspection.'}}]}, {choices:[{delta:{tool_calls:[{index:0,id:'cache-call',type:'function',function:{name:'inspect',arguments:'{"value":'}}]}}]},
 {choices:[{delta:{tool_calls:[{index:0,function:{arguments:'7}'}}]},finish_reason:'tool_calls'}],usage}]);
const finalWire=infer=>wire([{choices:[{delta:{reasoning_content:'Final bound reasoning.'}}]}, {choices:[{delta:{content:'Bound profile complete.'}}]}, {choices:[{delta:{},...(!infer?{finish_reason:'stop'}:{})}],usage}]);
const observations=[],oldNow=Date.now;Date.now=()=>123;
try{for(const profile of profiles){
 const selected=structuredClone(profile.catalog?.model??{...model,...(Object.hasOwn(profile,'compat')?{compat:profile.compat}:{})});
 let messages=[structuredClone(system),structuredClone(user),additions()];
 const assistant={role:'assistant',api:selected.api,provider:selected.provider,model:selected.id,content:[{type:'toolCall',id:'prior-call',name:'alpha',arguments:{value:1}}],stopReason:'toolUse',timestamp:123,usage:{input:0,output:0,cacheRead:0,cacheWrite:0,totalTokens:0,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}}};
 const result={role:'toolResult',toolCallId:'prior-call',toolName:'alpha',content:[{type:'text',text:'Prior result.'}],isError:false,timestamp:123};
 switch(profile.history){
 case 'tool-only':messages[2]=additions({content:'',sections:{}});break;
 case 'sections-only':messages[2]=additions({content:'',sections:{context:null,'later\nsection':'Text.'}});break;
 case 'no-head':messages.shift();break;
 case 'empty-head':messages[0].toolsAdded=[];break;
 case 'absent-head':delete messages[0].toolsAdded;break;
 case 'empty-removal':messages[2].toolsRemoved=[];break;
 case 'remove-existing':messages[2].toolsRemoved=[{name:'alpha'}];break;
 case 'remove-unknown':messages[2].toolsRemoved=[{name:'missing'}];break;
 case 'remove-all':messages[2]=additions({toolsAdded:[],toolsRemoved:[{name:'alpha'}]});break;
 case 'identical':messages[2].toolsAdded=[structuredClone(alpha),inspect];break;
 case 'changed':messages[2].toolsAdded=[{...alpha,description:'Changed alpha.'},inspect];break;
 case 'same-message':messages[2].toolsAdded=[inspect,structuredClone(inspect)];break;
 case 'readd':messages[0].toolsAdded=[alpha,omega];messages[2].toolsRemoved=[{name:'alpha'}];messages[2].toolsAdded=[inspect,alpha];break;
 case 'multiple':messages[2].toolsAdded=[omega,inspect];messages.push(additions({content:'Third instruction.',toolsAdded:[tool('last')]}));break;
 case 'pending':messages=[messages[0],messages[1],assistant,additions(),result,structuredClone(user)];break;
 case 'missing-result':messages=[messages[0],messages[1],assistant,additions(),structuredClone(user)];break;
 case 'orphan':messages=[messages[0],messages[1],{...result,toolCallId:'orphan'},additions(),structuredClone(user)];break;
 case 'cross-model':messages=[messages[0],messages[1],{...assistant,provider:'other-provider',model:'other-model',content:[{type:'thinking',thinking:'Prior signed thought.',thinkingSignature:'opaque-signature'},...assistant.content.map(t=>({...t,id:'call|item+opaque'}))]},additions(),{...result,toolCallId:'call|item+opaque'},structuredClone(user)];break;
 case 'grammar-fallback':messages[2].toolsAdded=[{...inspect,constrainedSampling:{type:'grammar',syntax:'lark',definition:'start: "ok"'}}];break;
 }
 const options={apiKey:'authored-inert-anchored-key',maxTokens:256,temperature:0.25,cacheRetention:profile.retention??'short',sessionId:'anchored-profile-session',
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
const report={schemaVersion:1,kind:'unchanged-pi-sdk-completions-anchored-tools-capture',sourceSha:'d86654abb8862e201933517d6f1fce9f88dd117f',sourceLock:pin(lockPath),captureProgram:pin(program),node:pin(process.execPath),sourceFilesVerified:pinned.size,actualLoadedSourceFiles:loadedPins,sourceOrSdkEdits:0,originalFixtureEdits:0,dependencyAcquisitionAttempts:0,prohibitedNetworkCalls,emissionBoundary:'Actual EventStream.push entry; observer calls original method without alteration',observations};
fs.writeFileSync(output,JSON.stringify(report,null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({profiles:observations.length,turns:observations.reduce((n,o)=>n+o.turns.length,0),events:observations.reduce((n,o)=>n+o.turns.reduce((m,t)=>m+t.emissions.length,0),0),sourceFilesVerified:pinned.size,loaded:loadedPins.length,networkCalls:prohibitedNetworkCalls,report:{path:output,...pin(output)}}));
