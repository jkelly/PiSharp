// Supplemental offline capture. The original strict lifecycle inputs, lock and comparator are unchanged.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
const directory=path.dirname(fileURLToPath(import.meta.url)),repo=path.resolve(directory,'../..');
const [oracleRoot,output]=process.argv.slice(2);assert(oracleRoot&&output,'Supply pinned oracle root and a fresh output file');
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const pin=p=>{const b=fs.readFileSync(p);return {bytes:b.length,sha256:hash(b)};};
const lockPath=path.join(repo,'tools/ReferenceOracle/openai-completions-sdk-lifecycle.lock.json');
const inputPath=path.join(repo,'fixtures/reference/openai-completions-sdk-lifecycle/input.json');
assert.equal(pin(lockPath).sha256,'8249a6aa25b4386e5b58917acb303ea9aaf6a96aa3cc1263d125ff3a2d4a8172');
assert.equal(pin(inputPath).sha256,'2e35c47906fd8c52c0d156dc2af4621f746fdcdb33fb319fecc78abaa438bd2e');
assert.equal(pin(process.execPath).sha256,'3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237');
const lock=JSON.parse(fs.readFileSync(lockPath,'utf8')),input=JSON.parse(fs.readFileSync(inputPath,'utf8'));
const pinned=new Map([...lock.loadedModules,...lock.sourceHashes].map(x=>[path.resolve(oracleRoot,x.path),x]));
for(const [p,e]of pinned){const a=pin(p);assert.equal(a.bytes,e.bytes);assert.equal(a.sha256,e.sha256);}
const loaded=new Set();registerHooks({load(url,context,next){const result=next(url,context);if(url.startsWith('file:'))loaded.add(path.resolve(fileURLToPath(url)));return result;}});
let prohibitedNetworkCalls=0;globalThis.fetch=()=>{prohibitedNetworkCalls++;throw new Error('Only authored in-memory fetch is permitted');};
const {stream}=await import(pathToFileURL(path.join(oracleRoot,'upstream/packages/ai/src/api/openai-completions.ts')));
const text=text=>({type:'text',text}),image=(data='AA==',mimeType='image/png')=>({type:'image',data,mimeType});
const user=content=>({role:'user',content,timestamp:123});
const mixed=user([text('Before π'),image(),text(''),text('After 😀\u0000'),image('/9j/', 'image/jpeg')]);
const declaration={role:'system',content:'Inspect once.',toolsAdded:[{name:'inspect',description:'An authored inert adapter.',parameters:{type:'object',properties:{value:{type:'number'},keep:{type:'null'}},required:['value','keep'],additionalProperties:false}}],timestamp:123};
const agentUser=user([text('Use inspect.'),image(),text('')]);
const assistant={role:'assistant',api:input.model.api,provider:input.model.provider,model:'agent-http-model',timestamp:123,content:[{type:'toolCall',id:'call-inspect',name:'inspect',arguments:{value:1,keep:null}}],stopReason:'toolUse',usage:{input:0,output:0,cacheRead:0,cacheWrite:0,totalTokens:0,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}}};
const toolResult={role:'toolResult',toolCallId:'call-inspect',toolName:'inspect',content:[text('owned π\u0000')],isError:false,timestamp:123};
const profiles=[
 {id:'mixed-model-declares-text-only',messages:[mixed]},
 {id:'mixed-model-declares-images',messages:[mixed],modelInput:['text','image']},
 {id:'image-only',messages:[user([image()])]},
 {id:'multiple-image-order',messages:[user([image('first','image/webp'),image('second','image/gif'),text('tail')])]},
 {id:'empty-text-around-image',messages:[user([text(''),image(),text('')])]},
 {id:'only-empty-text-skipped',messages:[user([text(''),text('')])]},
 {id:'empty-array-skipped',messages:[user([])]},
 {id:'empty-string-preserved',messages:[user('')]},
 {id:'opaque-data-and-mime-no-decoding',messages:[user([image('', 'application/authored'),image('not base64 π\u0000','image/x-authored')])]},
 {id:'image-after-tool-bridge',messages:[{...assistant,model:input.model.id},toolResult,user([image()])],bridge:true},
 {id:'agent-first-image-declaration',messages:[declaration,agentUser],agent:true},
 {id:'agent-image-history-replay',messages:[declaration,agentUser,assistant,toolResult],agent:true},
 {id:'consecutive-images-collapse-then-empty-text-breaks-run',messages:[user([image('first'),image('second'),text(''),image('third'),text('between'),image('fourth')])]}
];
// Every authored shape gets both complete upstream capability branches. The original source-r1
// capture stays preserved as an earlier receipt; no original strict observation is replaced.
const pairedProfiles=profiles.flatMap(profile=>profile.modelInput?[profile]:[profile,{...profile,id:profile.id+'-vision',modelInput:['text','image']}]);
const observations=[],originalNow=Date.now;Date.now=()=>input.clock.unixMilliseconds;
try{
 for(const profile of pairedProfiles){
  const model={...input.model,input:profile.modelInput??['text'],...(profile.bridge?{compat:{requiresAssistantAfterToolResult:true}}:{}),...(profile.agent?{id:'agent-http-model',baseUrl:'https://agent-completions.invalid/v1'}:{})};
  const context={messages:structuredClone(profile.messages)},original=JSON.stringify(context),requests=[],events=[];let payloadCalls=0,responseCalls=0;
  const options={...input.commonOptions};if(profile.agent){delete options.maxTokens;delete options.temperature;}
  const wire='data: {"choices":[{"delta":{"content":"Observed image"}}]}\n\ndata: {"choices":[{"delta":{},"finish_reason":"stop"}]}\n\ndata: [DONE]\n\n';
  const run=stream(model,context,{...options,onPayload:()=>{payloadCalls++;},onResponse:()=>{responseCalls++;},fetch:async(url,request)=>{const body=request.body;requests.push({url:String(url),method:request.method,headers:[...new Headers(request.headers).entries()],body,bodyUtf8Sha256:hash(Buffer.from(body)),bodyJson:JSON.parse(body)});return new Response(wire,{status:200,headers:{'content-type':'text/event-stream'}});}});
  for await(const event of run)events.push(structuredClone(event));const result=await run.result();
  assert.equal(result.stopReason,'stop',profile.id);assert.equal(requests.length,1);assert.equal(payloadCalls,1);assert.equal(responseCalls,1);assert.equal(JSON.stringify(context),original,'Canonical source context changed');
  observations.push({id:profile.id,model,context,options,projection:{requiresAssistantAfterToolResult:profile.bridge??false},requests,events,result,payloadCalls,responseCalls,canonicalContextUnchanged:true});
 }
}finally{Date.now=originalNow;}
const loadedPins=[...loaded].sort().map(p=>{const expected=pinned.get(p);assert(expected,'Unpinned source module '+p);const a=pin(p);assert.equal(a.sha256,expected.sha256);return {relativePath:expected.path,...a};});assert.equal(prohibitedNetworkCalls,0);
const report={schemaVersion:1,kind:'additive-unchanged-pi-sdk-user-image-request-capture',sourceSha:input.sourceSha,sourceLock:pin(lockPath),originalInput:pin(inputPath),captureProgram:pin(fileURLToPath(import.meta.url)),node:pin(process.execPath),sourceFilesVerified:pinned.size,actualLoadedSourceFiles:loadedPins,sourceOrSdkEdits:0,originalFixtureEdits:0,prohibitedNetworkCalls,observations};
fs.writeFileSync(output,JSON.stringify(report,null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({profiles:observations.length,sourceFilesVerified:pinned.size,loaded:loadedPins.length,networkCalls:prohibitedNetworkCalls,report:{path:output,...pin(output)}}));
