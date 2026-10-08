import { qualifiedReferenceFile } from '../../../tools/PublicReferenceLayout.mjs';
import fs from 'node:fs';import path from 'node:path';import crypto from 'node:crypto';import{execFileSync,spawnSync}from'node:child_process';import{pathToFileURL}from'node:url';
const root=import.meta.dirname,source=qualifiedReferenceFile('P:/PiSharp/root/Documents/Codex/2026-09-30/task-2/Pi-upstream-v0.99.1/packages/ai/src/utils/overflow.ts', '9a7200aac0cce1769e3acdf01e66048fea0ee62f3a93a3dac08055c41a33e2cb');
const hash=f=>crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');
if(process.argv[2]==='child')
{
 const api=await import(pathToFileURL(source));const examples=[
  'prompt is too long: 213462 tokens','request_too_large','input is too long for requested model','input exceeds the context window',
  'exceeds the model\'s maximum context length of 131,072 tokens','exceeds maximum context length (131,072)',
  'input token count 1196265 exceeds the maximum','maximum prompt length is 131072','reduce the length of the messages',
  'maximum context length is 131072 tokens','exceeds the maximum allowed input length of 1,024 tokens',
  'input (123 tokens) is longer than the model\'s context length (100 tokens)','exceeds the limit of 123',
  'exceeds the available context size','greater than the context length','context window exceeds limit','exceeded model token limit',
  'too large for model with 123 maximum context length','prompt has 1,234 tokens, but the configured context size is 123 tokens',
  'model_context_window_exceeded','prompt too long; exceeded max context length','range of input length should be [1,100]',
  'context_length_exceeded','context length exceeded','too many tokens','token limit exceeded',
  'Throttling error: too many tokens','Service unavailable: prompt too long','rate limit: prompt too long','too many requests: context length exceeded',
  '400 status code (no body)','413\uFEFF(no body)','413\u0085(no body)','input token count\nexceeds the maximum',
  'input token count\u2028exceeds the maximum','input token count\u0085exceeds the maximum','exceeds the limit of ١٢',
  'EXCEEDS THE CONTEXT WINDOW','exceeds the limit of 123','no overflow',''
 ];
 const cases=examples.map((error,i)=>({id:'error-'+i,message:{role:'assistant',api:'openai-responses',provider:i>=30&&i<=32?'cerebras':'fixture',model:'fixture',timestamp:0,content:[],stopReason:'error',errorMessage:error,usage:{input:100,output:1,cacheRead:0,cacheWrite:0,totalTokens:101,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}}},contextWindow:1000,desiredMaxOutput:1000}));
 for(const stopReason of ['stop','length','aborted'])for(const input of [980,990,1000,1001])for(const output of [0,1000])cases.push({id:`usage-${stopReason}-${input}-${output}`,message:{...cases[0].message,stopReason,errorMessage:undefined,usage:{...cases[0].message.usage,input,output}},contextWindow:1000,desiredMaxOutput:1000});
 const result={sourceSha:'d86654abb8862e201933517d6f1fce9f88dd117f',sourceFile:source,sourceFileSha256:hash(source),sourcePatterns:api.getOverflowPatterns().map(p=>p.toString()),cases:cases.map(v=>({...v,sourceOverflow:api.isContextOverflow(v.message,v.contextWindow),sourceRecoverableLength:api.isRecoverableLength(v.message,v.desiredMaxOutput)})),disclosure:'Actual complete unchanged overflow.ts exports. This is helper evidence, not complete AgentSession recovery/ordering evidence. Only finite typed numeric message/profile inputs here.'};
 process.stdout.write(JSON.stringify(result)+'\n');
}
else
{
 const destination=path.join(root,'recovery-classifier-genuine-source-r1');fs.mkdirSync(destination);const before=hash(source),runs=[];
 for(let i=1;i<=2;i++){const child=spawnSync(process.execPath,[import.meta.filename,'child'],{encoding:'utf8',timeout:20000,maxBuffer:8388608,windowsHide:true});fs.writeFileSync(path.join(destination,`repeat-${i}.stdout.json`),child.stdout??'');fs.writeFileSync(path.join(destination,`repeat-${i}.stderr.log`),child.stderr??'');runs.push({status:child.status,signal:child.signal,error:child.error?String(child.error):null,stdoutBytes:Buffer.byteLength(child.stdout??''),stderrBytes:Buffer.byteLength(child.stderr??''),timeoutMilliseconds:20000,maxBufferBytes:8388608});if(child.status!==0||child.signal!==null||child.error)throw Error('Actual source child failed');}
 const a=fs.readFileSync(path.join(destination,'repeat-1.stdout.json')),b=fs.readFileSync(path.join(destination,'repeat-2.stdout.json'));if(!a.equals(b)||hash(source)!==before)throw Error('Source capture differs or source changed');
 const result=JSON.parse(a);if(result.sourcePatterns.length!==24)throw Error('Expected all24 source patterns');
 fs.writeFileSync(path.join(destination,'report.json'),JSON.stringify({sourceSha:result.sourceSha,sourceFile:source,sourceFileSha256:before,actualSourceFileUnchanged:true,actualCompleteModuleExports:true,sourceRuntime:process.version,nodeBinary:{path:process.execPath,sha256:hash(process.execPath)},producerHarness:{path:import.meta.filename,sha256:hash(import.meta.filename)},consumerSourceExecution:execFileSync('C:/Program Files/Git/cmd/git.exe',['-C',path.join(root,'PiSharp-recovery-settlement-work'),'rev-parse','HEAD'],{encoding:'utf8'}).trim(),helperCases:result.cases.length,helperPatterns:24,runs,repeatBytesEqual:true,providerRequests:0,wholeAgentSessionOracle:false},null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({directory:destination,cases:result.cases.length,sourceSha256:before,reportSha256:hash(path.join(destination,'report.json'))}));
}
