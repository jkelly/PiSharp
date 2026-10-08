// Additive provider-mode capture; original fixtures, SDK and strict comparator remain unchanged.
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
for(const [p,f]of pinned){assert.deepEqual(pin(p),{bytes:f.bytes,sha256:f.sha256})}
const loaded=new Set();registerHooks({load(url,context,next){const result=next(url,context);if(url.startsWith('file:'))loaded.add(path.resolve(fileURLToPath(url)));return result}});
let prohibitedNetworkCalls=0;globalThis.fetch=()=>{prohibitedNetworkCalls++;throw Error('Only the in-memory fixture fetch is permitted')};
const {stream}=await import(pathToFileURL(path.join(oracleRoot,'upstream/packages/ai/src/api/openai-completions.ts')));
const model={id:'openai/offline-reasoner',api:'openai-completions',provider:'openrouter',baseUrl:'https://openrouter-mode.invalid/v1',name:'Authored offline reasoning mode',reasoning:true,input:['text'],contextWindow:8192,maxTokens:512,cost:{input:0,output:0,cacheRead:0,cacheWrite:0}};
const context={messages:[{role:'system',content:'Exercise provider reasoning request mode.',timestamp:123},{role:'user',content:[{type:'text',text:'Return the literal fixture reply.'}],timestamp:123}]};
const profiles=[
 {id:'openrouter-auto-off'}, {id:'openrouter-auto-low',effort:'low'},
 {id:'openrouter-low-map',effort:'low',map:{low:'medium'}}, {id:'openrouter-low-null-fallback',effort:'low',map:{low:null}},
 {id:'openrouter-off-null-omit',map:{off:null}}, {id:'openrouter-off-empty-retain',map:{off:''}}, {id:'openrouter-off-map',map:{off:'disabled-for-fixture'}},
 {id:'openrouter-effort-flag-false',effort:'high',supportsReasoningEffort:false}, {id:'openrouter-nonreasoning',effort:'high',reasoning:false},
 {id:'openrouter-explicit-openai-mapped-high',effort:'high',map:{high:'medium'},format:'openai'},
 {id:'openrouter-explicit-openai-off-map',map:{off:'none'},format:'openai'}, {id:'openrouter-explicit-openai-off-null',map:{off:null},format:'openai'},
 {id:'endpoint-detected-openrouter',provider:'authored-compatible',baseUrl:'https://openrouter.ai/api/v1',effort:'low'},
 {id:'explicit-openrouter-standard-provider',provider:'openai',format:'openrouter',effort:'high',map:{high:'low'}},
 {id:'openrouter-opaque-unicode-map',effort:'low',map:{low:'déféré 🚀'}},
 {id:'openai-mode-no-effort-no-control',format:'openai'},
 {id:'openai-mode-effort-unsupported',effort:'high',format:'openai',supportsReasoningEffort:false}
];
const wire='data: {"choices":[{"delta":{"reasoning_content":"reasoning trace"}}]}\n\ndata: {"choices":[{"delta":{"content":"reasoning mode final"}}]}\n\ndata: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":8,"total_tokens":20}}\n\ndata: [DONE]\n\n';
const observations=[],oldNow=Date.now;Date.now=()=>123;
try{for(const profile of profiles){
 const selected={...model,...(profile.provider?{provider:profile.provider}:{}),...(profile.baseUrl?{baseUrl:profile.baseUrl}:{}),...(profile.reasoning===false?{reasoning:false}:{}),...(profile.map?{thinkingLevelMap:profile.map}:{}),compat:{...(profile.format?{thinkingFormat:profile.format}:{}),...(profile.supportsReasoningEffort===false?{supportsReasoningEffort:false}:{})}};
 const history=structuredClone(context),original=JSON.stringify(history),requests=[],events=[];let payloadCalls=0,responseCalls=0;
 const options={apiKey:'authored-inert-openrouter-key',maxTokens:256,temperature:0.25,...(profile.effort?{reasoningEffort:profile.effort}:{})};
 const run=stream(selected,history,{...options,onPayload:()=>{payloadCalls++},onResponse:()=>{responseCalls++},fetch:async(url,request)=>{
  const body=request.body;requests.push({url:String(url),method:request.method,headers:[...new Headers(request.headers).entries()],body,bodyUtf8Sha256:hash(Buffer.from(body)),bodyJson:JSON.parse(body)});
  return new Response(wire,{status:200,headers:{'content-type':'text/event-stream'}})
 }});
 for await(const event of run)events.push(structuredClone(event));const result=await run.result();
 assert.equal(result.stopReason,'stop');assert.equal(requests.length,1);assert.equal(payloadCalls,1);assert.equal(responseCalls,1);assert.equal(JSON.stringify(history),original);
 observations.push({id:profile.id,profile,model:selected,context:history,options,wire,requests,events,result,payloadCalls,responseCalls,canonicalContextUnchanged:true});
}}finally{Date.now=oldNow}
const loadedPins=[...loaded].sort().map(p=>{const expected=pinned.get(p);assert(expected,'Unpinned loaded module '+p);const actual=pin(p);assert.equal(actual.sha256,expected.sha256);return {relativePath:expected.path,...actual}});assert.equal(prohibitedNetworkCalls,0);
const report={schemaVersion:1,kind:'additive-unchanged-pi-sdk-openrouter-reasoning-mode-capture',sourceSha:'d86654abb8862e201933517d6f1fce9f88dd117f',sourceLock:pin(lockPath),captureProgram:pin(program),node:pin(process.execPath),sourceFilesVerified:pinned.size,actualLoadedSourceFiles:loadedPins,sourceOrSdkEdits:0,originalFixtureEdits:0,prohibitedNetworkCalls,observations};
fs.writeFileSync(output,JSON.stringify(report,null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({profiles:observations.length,sourceFilesVerified:pinned.size,loaded:loadedPins.length,networkCalls:prohibitedNetworkCalls,report:{path:output,...pin(output)}}));
