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
const result=(id,content)=>({role:'toolResult',toolCallId:id,toolName:'inspect',content,isError:false,timestamp:123,details:{opaque:1,keep:null}});
const assistant=(ids=['call-a'])=>({role:'assistant',api:input.model.api,provider:input.model.provider,model:input.model.id,timestamp:123,content:ids.map(id=>({type:'toolCall',id,name:'inspect',arguments:{value:1,keep:null}})),stopReason:'toolUse',usage:{input:0,output:0,cacheRead:0,cacheWrite:0,totalTokens:0,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}}});
const emptyAssistant={...assistant([]),stopReason:'stop'};
const shapes=[
 {id:'single-tool-image-only',messages:[assistant(),result('call-a',[image()])]},
 {id:'single-tool-mixed-images-text',messages:[assistant(),result('call-a',[text('before'),image(),text('after π\u0000'),image('/9j/','image/jpeg')])]},
 {id:'two-consecutive-image-results',messages:[assistant(['call-a','call-b']),result('call-a',[image('first','image/webp')]),result('call-b',[image('second','image/gif')])]},
 {id:'consecutive-text-and-image-results',messages:[assistant(['call-a','call-b']),result('call-a',[text('text-only')]),result('call-b',[image('last')])]},
 {id:'empty-text-join-before-images',messages:[assistant(),result('call-a',[text(''),text(''),image()])]},
 {id:'empty-tool-result-control',messages:[assistant(),result('call-a',[])]},
 {id:'text-only-tool-then-user-control',messages:[assistant(),result('call-a',[text('owned')]),user('next')]},
 {id:'tool-images-then-user-images',messages:[assistant(),result('call-a',[image()]),user([text('next'),image('user')])]},
 {id:'tool-images-empty-user-then-user',messages:[assistant(),result('call-a',[image()]),user([]),user('next')]},
 {id:'tool-images-empty-assistant-then-user',messages:[assistant(),result('call-a',[image()]),emptyAssistant,user('next')]},
 {id:'separate-image-groups',messages:[assistant(),result('call-a',[image('first')]),user('between'),{...assistant(['call-b']),timestamp:124},result('call-b',[image('second')])]},
 {id:'missing-result-group-before-user',messages:[assistant(['call-a','call-b']),result('call-a',[image('first')]),user('next')]},
 {id:'tool-literal-placeholder-collapsing',messages:[assistant(),result('call-a',[text('(tool image omitted: model does not support images)'),image('suppressed'),text(''),image('after-empty')])]},
 {id:'tool-consecutive-images-with-empty-text-break',messages:[assistant(),result('call-a',[image('first'),image('second'),text(''),image('third'),text('between'),image('fourth')])]},
 {id:'user-literal-placeholder-before-image',messages:[user([text('(image omitted: model does not support images)'),image()])]},
 {id:'user-image-literal-placeholder-image',messages:[user([image('first'),text('(image omitted: model does not support images)'),image('last')])]},
 {id:'opaque-tool-image-no-decoding',messages:[assistant(),result('call-a',[image('','application/authored'),image('opaque π\u0000','image/x-authored')])]},
 {id:'orphan-tool-image-with-name',messages:[result('orphan',[image()]),user('next')]}
];
const pairedProfiles=shapes.flatMap(shape=>[false,true].flatMap(supportsImages=>[false,true].map(bridge=>({...shape,id:shape.id+(supportsImages?'-vision':'-text')+(bridge?'-bridge':'-plain'),modelInput:supportsImages?['text','image']:['text'],bridge,requiresToolResultName:bridge}))));
const observations=[],originalNow=Date.now;Date.now=()=>input.clock.unixMilliseconds;
try{
 for(const profile of pairedProfiles){
  const model={...input.model,input:profile.modelInput??['text'],compat:{requiresAssistantAfterToolResult:profile.bridge,requiresToolResultName:profile.requiresToolResultName},...(profile.agent?{id:'agent-http-model',baseUrl:'https://agent-completions.invalid/v1'}:{})};
  const context={messages:structuredClone(profile.messages)},original=JSON.stringify(context),requests=[],events=[];let payloadCalls=0,responseCalls=0;
  const options={...input.commonOptions};if(profile.agent){delete options.maxTokens;delete options.temperature;}
  const wire='data: {"choices":[{"delta":{"content":"Observed image"}}]}\n\ndata: {"choices":[{"delta":{},"finish_reason":"stop"}]}\n\ndata: [DONE]\n\n';
  const run=stream(model,context,{...options,onPayload:()=>{payloadCalls++;},onResponse:()=>{responseCalls++;},fetch:async(url,request)=>{const body=request.body;requests.push({url:String(url),method:request.method,headers:[...new Headers(request.headers).entries()],body,bodyUtf8Sha256:hash(Buffer.from(body)),bodyJson:JSON.parse(body)});return new Response(wire,{status:200,headers:{'content-type':'text/event-stream'}});}});
  for await(const event of run)events.push(structuredClone(event));const result=await run.result();
  assert.equal(result.stopReason,'stop',profile.id);assert.equal(requests.length,1);assert.equal(payloadCalls,1);assert.equal(responseCalls,1);assert.equal(JSON.stringify(context),original,'Canonical source context changed');
  observations.push({id:profile.id,model,context,options,projection:{requiresAssistantAfterToolResult:profile.bridge??false,requiresToolResultName:profile.requiresToolResultName},requests,events,result,payloadCalls,responseCalls,canonicalContextUnchanged:true});
 }
}finally{Date.now=originalNow;}
const loadedPins=[...loaded].sort().map(p=>{const expected=pinned.get(p);assert(expected,'Unpinned source module '+p);const a=pin(p);assert.equal(a.sha256,expected.sha256);return {relativePath:expected.path,...a};});assert.equal(prohibitedNetworkCalls,0);
const report={schemaVersion:1,kind:'additive-unchanged-pi-sdk-complete-tool-image-request-capture',sourceSha:input.sourceSha,sourceLock:pin(lockPath),originalInput:pin(inputPath),captureProgram:pin(fileURLToPath(import.meta.url)),node:pin(process.execPath),sourceFilesVerified:pinned.size,actualLoadedSourceFiles:loadedPins,sourceOrSdkEdits:0,originalFixtureEdits:0,prohibitedNetworkCalls,observations};
fs.writeFileSync(output,JSON.stringify(report,null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({profiles:observations.length,sourceFilesVerified:pinned.size,loaded:loadedPins.length,networkCalls:prohibitedNetworkCalls,report:{path:output,...pin(output)}}));
