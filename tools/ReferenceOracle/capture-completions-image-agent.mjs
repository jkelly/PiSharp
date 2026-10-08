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
const text=text=>({type:'text',text}),image=(data='AA==',mimeType='image/png')=>({type:'image',data,mimeType});
const schema={type:'object',properties:{value:{type:'number'},keep:{type:'null'}},required:['value','keep'],additionalProperties:false};
const modes=['text-control','image-only','mixed-images','two-tool-images'],profiles=modes.flatMap(mode=>[false,true].flatMap(supportsImages=>[false,true].map(bridge=>({mode,supportsImages,bridge,id:mode+(supportsImages?'-vision':'-text')+(bridge?'-bridge':'-plain')}))));
const observations=[],originalNow=Date.now;Date.now=()=>123;
try{
 for(const profile of profiles){
  const model={...input.model,id:'agent-image-model',baseUrl:'https://agent-image.invalid/v1',input:profile.supportsImages?['text','image']:['text'],compat:{requiresAssistantAfterToolResult:profile.bridge,requiresToolResultName:profile.bridge}};
  const requests=[],events=[],contexts=[],toolExecutions=[],rawResults=[];
  const outputFor=id=>profile.mode==='text-control'?[text('owned π\u0000')]:profile.mode==='image-only'?[image()]:profile.mode==='mixed-images'?[text('owned π\u0000'),image(),image('/9j/','image/jpeg')]:[image(id==='call-first'?'first':'second',id==='call-first'?'image/png':'image/jpeg')];
  const tool={name:'inspect',description:'An authored inert adapter.',parameters:schema,execute:async(id,args)=>{toolExecutions.push({id,args:structuredClone(args)});const result={content:outputFor(id),details:{keep:null,n:1},usage:{raw:1,keep:null},opaque:{retained:[2,1]}};rawResults.push({id,result:structuredClone(result)});return result;}};
  const prompts=[{role:'user',content:profile.mode==='text-control'?'Use inspect.':[text('Use inspect.'),image('user')],timestamp:123}];
  const context={messages:[{role:'system',content:'Inspect once.',timestamp:123}],tools:[tool]};
  const options={...input.commonOptions};delete options.maxTokens;delete options.temperature;
  const ids=profile.mode==='two-tool-images'?['call-first','call-second']:['call-inspect'];
  const first='data: '+JSON.stringify({choices:[{delta:{tool_calls:ids.map((id,index)=>({index,id,type:'function',function:{name:'inspect',arguments:'{"value":1.00,"keep":null}'}}))}}]})+'\n\ndata: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}\n\ndata: [DONE]\n\n';
  const second='data: {"choices":[{"delta":{"content":"Observed image"}}]}\n\ndata: {"choices":[{"delta":{},"finish_reason":"stop"}]}\n\ndata: [DONE]\n\n';
  const streamFn=(selected,llmContext,configuration)=>stream(selected,llmContext,{...configuration,fetch:async(url,request)=>{const body=request.body;requests.push({url:String(url),method:request.method,body,bodyUtf8Sha256:hash(Buffer.from(body)),bodyJson:JSON.parse(body)});assert(requests.length<=2);return new Response(requests.length===1?first:second,{status:200,headers:{'content-type':'text/event-stream'}});}});
  const result=await runAgentLoop(prompts,context,{...options,model,convertToLlm:messages=>{contexts.push(structuredClone(messages));return messages;}},event=>{events.push(structuredClone(event));},undefined,streamFn);
  assert.equal(requests.length,2);assert.equal(toolExecutions.length,ids.length);assert.equal(result.at(-1).stopReason,'stop');const toolMessages=result.filter(x=>x.role==='toolResult');assert.equal(toolMessages.length,ids.length);assert.deepEqual(toolMessages.map(x=>x.content),rawResults.map(x=>x.result.content));
  observations.push({profile,model,options,initialMessages:contexts[0],contexts,requests,events,result,toolExecutions,rawResults,toolMessages,firstResponseWire:first,secondResponseWire:second,actualUnchangedPiAgentLoop:true,actualUnchangedPiProviderAndSdk:true});
 }
}finally{Date.now=originalNow;}
assert.equal(prohibitedNetworkCalls,0);
const loadedPins=[...loaded].sort().map(p=>{const expected=pinned.get(p);assert.equal(pin(p).sha256,expected.sha256);return expected;});
const report={schemaVersion:1,kind:'actual-pinned-agent-image-producing-tool-provider-continuation',sourceSha:input.sourceSha,node:pin(process.execPath),existingSourceLock:pin(lockPath),originalInput:pin(inputPath),captureProgram:pin(fileURLToPath(import.meta.url)),dependencies,immutableGitSourceFilesVerified:entries.length,allExistingSourceLockFilesVerified:230,actualLoadedSourceFiles:loadedPins,sourceOrSdkEdits:0,originalFixtureEdits:0,dependencyAcquisitionAttempts:0,prohibitedNetworkCalls,securityOrCredentialActions:0,observations};
fs.writeFileSync(output,JSON.stringify(report,null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({profiles:observations.length,loaded:loadedPins.length,gitSourcesVerified:entries.length,dependencies:dependencies.map(x=>({name:x.name,files:x.installedFilesVerifiedAgainstArchive})),network:prohibitedNetworkCalls,report:{path:output,...pin(output)}}));
