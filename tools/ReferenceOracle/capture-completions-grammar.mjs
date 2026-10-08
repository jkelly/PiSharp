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

const model={type:'chat',id:'anthropic/offline-grammar-reasoner',api:'openai-completions',provider:'openrouter',baseUrl:'https://grammar-profile.invalid/v1',name:'Offline grammar model',reasoning:true,input:['text','image'],contextWindow:8192,maxTokens:512,cost:{input:2.75,output:4.25,cacheRead:0.4,cacheWrite:1.125},compat:{supportsOpenAIGrammarTools:true,supportsMidConvoSystemMessages:true,supportsMidConvoToolAdditions:true}};
const standard={name:'inspect',description:'Standard control.',parameters:{type:'object',properties:{value:{type:'number'}},required:['value']}};
const grammar=(property='code',variants={openai_lark:'start: /[a-z]+/'},name='emit')=>({name,description:'Emit grammar input.',parameters:{type:'object',properties:{[property]:{type:'string'}},required:[property]},constrainedSampling:{type:'grammar',variants}});
const system={role:'system',content:'Initial grammar instruction.',sections:{context:'Stable grammar section.'},toolsAdded:[standard],timestamp:123,opaque:{keep:null}};
const user={role:'user',content:[{type:'text',text:'Emit raw grammar input.'},{type:'image',data:'Aw==',mimeType:'image/png'}],timestamp:123};
const additions=(tools)=>({role:'system',content:'Later grammar instruction.',sections:{context:null,later:'Grammar section.'},toolsAdded:tools,timestamp:123});
const profiles=[
 {id:'lark-anchored-real-continuation'}, {id:'regex-anchored-real-continuation',variants:{openai_regex:'[a-z]+'}},
 {id:'both-variants-prefer-lark',variants:{openai_lark:'  start: /[a-z]+/  ',openai_regex:'[a-z]+'}},
 {id:'empty-lark-falls-back-regex',variants:{openai_lark:' \n ',openai_regex:' [a-z]+ '}},
 {id:'nonstring-lark-falls-back-regex',variants:{openai_lark:false,openai_regex:'[a-z]+'}},
 {id:'unknown-variant-retained-in-canonical',variants:{openai_lark:'start: /[a-z]+/',future_variant:{opaque:null}}},
 {id:'optional-schema-fields',extraSchema:true}, {id:'property-name-escaping',property:'raw"\\\ninput'}, {id:'numeric-input-property',property:'2'},
 {id:'prototype-named-property',property:'__proto__'}, {id:'empty-custom-input',input:''}, {id:'missing-custom-input',wireMode:'missing-input'},
 {id:'unicode-escaped-custom-input',input:'quote"\\\n\t\r\u0000β👩🏽\u2028'},
 {id:'grammar-at-top-level',history:'head'}, {id:'grammar-with-no-leading-system',history:'no-head'},
 {id:'grammar-system-disabled-collapse',compat:{supportsOpenAIGrammarTools:true,supportsMidConvoSystemMessages:false,supportsMidConvoToolAdditions:true}},
 {id:'grammar-additions-disabled-current-set',compat:{supportsOpenAIGrammarTools:true,supportsMidConvoSystemMessages:true,supportsMidConvoToolAdditions:false}},
 {id:'grammar-removed-still-in-declared-input-map',history:'removed'}, {id:'grammar-redeclared-property-map',history:'redeclared'},
 {id:'grammar-overwritten-by-function',history:'function-redeclaration'},
 {id:'function-wire-for-grammar-declaration',wireMode:'function'}, {id:'mixed-function-and-custom-slots',wireMode:'mixed'},
 {id:'function-to-custom-slot-conversion',wireMode:'convert'}, {id:'late-custom-id',wireMode:'late-id'},
 {id:'late-custom-name-keeps-initial-fallback-property',wireMode:'late-name',roundtrip:false},
 {id:'unknown-custom-tool-name-falls-back-input',wireMode:'unknown'},
 {id:'strict-function-plus-custom',compat:{supportsOpenAIGrammarTools:true,supportsMidConvoSystemMessages:true,supportsMidConvoToolAdditions:true,supportsStrictMode:true}},
 {id:'custom-tool-choice',choice:{type:'custom',custom:{name:'emit',opaque:null},opaque:{keep:false}}},
 {id:'allowed-tools-auto-choice',choice:{type:'allowed_tools',allowed_tools:{mode:'auto',tools:[{type:'custom',custom:{name:'emit'},opaque:null},{type:'function',function:{name:'inspect'}}],opaque:{keep:null}}}},
 {id:'allowed-tools-required-choice',choice:{type:'allowed_tools',allowed_tools:{mode:'required',tools:[{type:'custom',custom:{name:'emit'}}]}}},
 {id:'grammar-cache-none',retention:'none'}, {id:'grammar-cache-long',retention:'long'},
 {id:'grammar-finish-inference',compat:{supportsOpenAIGrammarTools:true,supportsMidConvoSystemMessages:true,supportsMidConvoToolAdditions:true,supportsFinishReason:false},inferFinish:true},
 {id:'grammar-unsupported-fallback-function-control',compat:{supportsOpenAIGrammarTools:false,supportsMidConvoSystemMessages:true,supportsMidConvoToolAdditions:true},wireMode:'function'},
 {id:'nullish-grammar-fallback-function-control',compat:{supportsOpenAIGrammarTools:null,supportsMidConvoSystemMessages:true,supportsMidConvoToolAdditions:true},wireMode:'function'},
 {id:'enabled-with-only-function-declarations',history:'only-function',wireMode:'standard'}
];
const wire=chunks=>chunks.map(c=>'data: '+JSON.stringify(c)+'\n\n').join('')+'data: [DONE]\n\n';
const usage={prompt_tokens:15,completion_tokens:9,total_tokens:24,prompt_tokens_details:{cached_tokens:3}};
const finalWire=infer=>wire([{choices:[{delta:{content:'Observed grammar result.'}}]},{choices:[{delta:{},...(infer?{}:{finish_reason:'stop'})}],usage}]);
function firstWire(profile,property){
 const input=profile.input??'alpha\n"β"\\path',name=profile.wireMode==='unknown'?'unknown':profile.wireMode==='standard'?'inspect':'emit';
 const middle=Math.max(1,Math.floor(input.length/2));const chunks=[{choices:[{delta:{reasoning_content:'Grammar reasoning.'}}]},{choices:[{delta:{content:'Before grammar.'}}]}];
 const delta=call=>chunks.push({choices:[{delta:{tool_calls:[call]}}]});
 if(profile.wireMode==='standard')delta({index:0,id:'grammar-call',type:'function',function:{name,arguments:'{"value":7}'}});
 else if(profile.wireMode==='function')delta({index:0,id:'grammar-call',type:'function',function:{name,arguments:JSON.stringify({[property]:input})}});
 else{
  if(profile.wireMode==='convert')delta({index:0,id:'grammar-call',type:'function',function:{name,arguments:'{"discarded":'}});
  delta({index:0,...(profile.wireMode==='late-id'?{}:{id:'grammar-call'}),type:'custom',custom:{...(profile.wireMode==='late-name'?{}:{name}),...(profile.wireMode==='missing-input'?{}:{input:input.slice(0,middle)})}});
  if(profile.wireMode!=='missing-input')delta({index:0,...(profile.wireMode==='late-id'?{id:'grammar-call'}:{}),custom:{...(profile.wireMode==='late-name'?{name}:{}),input:input.slice(middle)}});
 }
 if(profile.wireMode==='mixed')delta({index:1,id:'standard-call',type:'function',function:{name:'inspect',arguments:'{"value":7}'}});
 chunks.push({choices:[{delta:{},...(profile.inferFinish?{}:{finish_reason:'tool_calls'})}],usage});return wire(chunks);
}
const observations=[],oldNow=Date.now;Date.now=()=>123;
try{for(const profile of profiles){
 const selected={...model,...(Object.hasOwn(profile,'compat')?{compat:profile.compat}:{})};
 let declaration=grammar(profile.property??'code',profile.variants);if(profile.extraSchema){declaration.parameters.properties.optional={type:'number'};declaration.parameters.additionalProperties=true;declaration.parameters.opaque={keep:null}}
 let property=profile.property??'code';let messages=[structuredClone(system),structuredClone(user),additions([declaration])];
 if(profile.history==='head'){messages[0].toolsAdded.push(declaration);messages[2]=additions([{...standard,name:'later-standard'}])}
 if(profile.history==='no-head')messages.shift();
 if(profile.history==='removed')messages.push({...additions([]),toolsRemoved:[{name:'emit'}]});
 if(profile.history==='redeclared'){property='script';messages.push(additions([grammar(property,{openai_regex:'[a-z]+'})]))}
 if(profile.history==='function-redeclaration'){property='input';messages.push(additions([{...standard,name:'emit'}]))}
 if(profile.history==='only-function')messages[2]=additions([{...standard,name:'later-standard'}]);
 if(profile.wireMode==='unknown')property='input';
 const options={apiKey:'authored-inert-grammar-key',maxTokens:256,temperature:0.25,cacheRetention:profile.retention??'short',sessionId:'grammar-profile-session',
  ...(profile.choice?{toolChoice:profile.choice}:{}),...(profile.headers?{headers:profile.headers}:{}),...(profile.sampling?{samplingParams:profile.sampling}:{})};
 const initialHistory=structuredClone(messages),turns=[];
 for(let turn=0;turn<(profile.roundtrip===false?1:2);turn++){
  const context={messages:structuredClone(messages)},original=JSON.stringify(context),requests=[],emissions=[],deliveredEvents=[];
  let payloadCalls=0,responseCalls=0;activeEmissions=emissions;const responseWire=turn===0?firstWire(profile,property):finalWire(profile.inferFinish);
  const run=stream(selected,context,{...options,onPayload:()=>{payloadCalls++},onResponse:()=>{responseCalls++},fetch:async(url,request)=>{
   const body=request.body;requests.push({url:String(url),method:request.method,headers:[...new Headers(request.headers).entries()],body,bodyUtf8Sha256:hash(Buffer.from(body)),bodyJson:JSON.parse(body)});return new Response(responseWire,{status:200,headers:{'content-type':'text/event-stream'}})
  }});
  for await(const event of run)deliveredEvents.push(snapshot(event));const result=await run.result();activeEmissions=null;
  assert.equal(requests.length,1);assert.equal(payloadCalls,1);assert.equal(responseCalls,1);assert.equal(JSON.stringify(context),original);assert(emissions.length>4);assert.equal(result.stopReason,turn===0?'toolUse':'stop');
  turns.push({turn,context,wire:responseWire,requests,emissions,deliveredEvents,result,payloadCalls,responseCalls,canonicalContextUnchanged:true});
  if(turn===0){messages.push(structuredClone(result));for(const call of result.content.filter(c=>c.type==='toolCall'))messages.push({role:'toolResult',toolCallId:call.id,toolName:call.name,content:[{type:'text',text:'Observed raw grammar input.'}],isError:false,timestamp:123,details:{opaque:{keep:null}}})}
 }
 observations.push({id:profile.id,profile,model:selected,options,initialHistory,turns});
}}finally{Date.now=oldNow;EventStream.prototype.push=originalPush;activeEmissions=null}
const loadedPins=[...loaded].sort().map(p=>{const expected=pinned.get(p);assert(expected,'Unpinned module '+p);const actual=pin(p);assert.equal(actual.sha256,expected.sha256);return {relativePath:expected.path,...actual}});assert.equal(prohibitedNetworkCalls,0);
const report={schemaVersion:1,kind:'unchanged-pi-sdk-completions-grammar-capture',sourceSha:'d86654abb8862e201933517d6f1fce9f88dd117f',sourceLock:pin(lockPath),captureProgram:pin(program),node:pin(process.execPath),sourceFilesVerified:pinned.size,actualLoadedSourceFiles:loadedPins,sourceOrSdkEdits:0,originalFixtureEdits:0,dependencyAcquisitionAttempts:0,prohibitedNetworkCalls,emissionBoundary:'Actual EventStream.push entry; observer calls original method without alteration',observations};
fs.writeFileSync(output,JSON.stringify(report,null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({profiles:observations.length,turns:observations.reduce((n,o)=>n+o.turns.length,0),events:observations.reduce((n,o)=>n+o.turns.reduce((m,t)=>m+t.emissions.length,0),0),sourceFilesVerified:pinned.size,loaded:loadedPins.length,networkCalls:prohibitedNetworkCalls,report:{path:output,...pin(output)}}));
