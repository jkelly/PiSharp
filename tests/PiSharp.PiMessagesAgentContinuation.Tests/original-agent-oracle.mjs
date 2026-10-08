// Authored oracle ONLY; not executed or admitted as a native owner.
// Future owner must pin all three modules/dependency closures before invocation.
// node this.mjs <absolute admitted Agent module> <absolute PiMessages module> <absolute TypeBox module> direct|simple
import { pathToFileURL } from 'node:url';
import { isAbsolute } from 'node:path';
import assert from 'node:assert/strict';
const [agentPath, messagesPath, schemaPath, mode] = process.argv.slice(2);
assert([agentPath, messagesPath, schemaPath].every(x => typeof x === 'string' && isAbsolute(x)));
assert(mode === 'direct' || mode === 'simple');
const { Agent } = await import(pathToFileURL(agentPath).href);
const pi = await import(pathToFileURL(messagesPath).href);
const { Type } = await import(pathToFileURL(schemaPath).href);
const stream = mode === 'simple' ? pi.streamSimple : pi.stream;
assert.equal(typeof Agent, 'function'); assert.equal(typeof stream, 'function');
const zero = { input:0, output:0, cacheRead:0, cacheWrite:0, totalTokens:0,
  cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0} };
const first = [{type:'start'}, {type:'toolcall_start',contentIndex:0,id:'provisional-call',toolName:'inspect'},
  {type:'toolcall_delta',contentIndex:0,delta:'{"value":1}'},
  {type:'toolcall_end',contentIndex:0,toolCall:{type:'toolCall',id:'final-call',name:'other',arguments:{value:7}}},
  {type:'done',reason:'toolUse',usage:zero}];
const second = [{type:'start'}, {type:'text_start',contentIndex:0},
  {type:'text_delta',contentIndex:0,delta:'continued'}, {type:'text_end',contentIndex:0,content:'continued'},
  {type:'done',reason:'stop',usage:zero}];
const encode = rows => rows.map(row => `data: ${JSON.stringify(row)}\n\n`).join('');
const records = [], failures = [], requests = [], emissions = [], converted = [];
function retain(name, original) { const row={name,original,joined:false,error:undefined}; records.push(row); return row; }
async function join(row) { if(row.joined)return; row.joined=true; try{await row.original;}catch(error){row.error=error;failures.push(error);} }
function gate(){let resolve;const original=new Promise(r=>{resolve=r;});return{original,resolve};}
const toolEntered=gate(), toolResult=gate(), callbackEntered=gate(), callbackRelease=gate();
let toolCalls=0, toolInput, startCall, endCall, deltaArguments;
const fakeFetch = (url, options) => {
  // No fallback to global fetch, including malformed endpoints.
  const original=(async()=>{
    assert.equal(String(url),'https://pi-messages.invalid/base/messages');
    requests.push(JSON.parse(options.body)); assert(requests.length<=2);
    return new Response(encode(requests.length===1?first:second),{status:200,headers:{'content-type':'text/event-stream'}});
  })();retain(`fetch-${records.filter(x=>x.name.startsWith('fetch-')).length+1}`,original);return original;
};
const model={type:'chat',id:'inert-model',provider:'inert-provider',api:'pi-messages',name:'Inert',baseUrl:'https://pi-messages.invalid/base/',
  reasoning:true,input:['text'],contextWindow:65536,maxTokens:8192,cost:{input:0,output:0,cacheRead:0,cacheWrite:0}};
const agent = new Agent({initialState:{model,tools:[{name:'other',label:'Other',description:'inert held tool',
  parameters:Type.Object({value:Type.Number()}),execute:(id,args)=>{
    toolCalls++;toolInput={id,args};retain('tool-executor-original',toolResult.original);toolEntered.resolve();return toolResult.original;
  }}]}, getApiKey:()=> 'inert-key', sessionId:'inert-session',
  convertToLlm:messages=>{
    const result=messages.filter(x=>['system','user','assistant','toolResult'].includes(x.role));
    converted.push({input:messages,result}); return result;
  },
  streamFn:(m,context,options)=>{
    const actual=stream(m,context,{...options,apiKey:'inert-key',sessionId:'inert-session',env:{},fetch:fakeFetch});
    // Observe pushes on the genuine returned stream; Agent remains its sole reader.
    const push=actual.push.bind(actual);
    actual.push=event=>{
      emissions.push(JSON.parse(JSON.stringify(event)));
      if(event.type==='toolcall_start')startCall=event.partial.content[event.contentIndex];
      if(event.type==='toolcall_delta')deltaArguments=event.partial.content[event.contentIndex].arguments;
      if(event.type==='toolcall_end')endCall=event.toolCall;
      return push(event);
    };
    return actual;
  }});
const unsubscribe=agent.subscribe(event=>{
  if(event.type==='message_end'&&event.message.role==='toolResult'){
    retain('tool-result-callback-original',callbackRelease.original);callbackEntered.resolve();return callbackRelease.original;
  }
});
let promptRow;
async function enteredOrPrompt(signal, anchor){
  let timer;try{
    const selected=await Promise.race([signal.then(()=> 'entered'),promptRow.original.then(()=> 'ended'),
      new Promise((_,reject)=>{timer=setTimeout(()=>reject(new Error(`diagnostic timeout: ${anchor}`)),5000);})]);
    assert.equal(selected,'entered',anchor);
  }finally{clearTimeout(timer);}
}
try {
  promptRow=retain('agent-prompt-original',agent.prompt({role:'user',content:'continue',timestamp:123}));
  await enteredOrPrompt(toolEntered.original,'tool entry');
  assert.equal(requests.length,1);assert.deepEqual(toolInput,{id:'final-call',args:{value:7}});
  toolResult.resolve({content:[{type:'text',text:'tool-result-seven'}],details:{}});
  await enteredOrPrompt(callbackEntered.original,'tool result callback');assert.equal(requests.length,1);
  callbackRelease.resolve();await join(promptRow);
  assert.equal(requests.length,2);assert.equal(toolCalls,1);
  const messages=requests[1].context.messages;
  const assistant=messages.find(x=>x.role==='assistant');const result=messages.find(x=>x.role==='toolResult');
  assert.deepEqual(assistant.content[0],{type:'toolCall',id:'final-call',name:'other',arguments:{value:7}});
  assert.equal(result.toolCallId,'final-call');assert.equal(result.toolName,'other');
  assert.equal(startCall,endCall);assert.equal(startCall.id,'final-call');assert.equal(startCall.arguments.value,7);
  assert.equal(deltaArguments.value,1);assert.notEqual(deltaArguments,endCall.arguments);
  assert(emissions.some(x=>x.type==='toolcall_start'&&x.partial.content[0].id==='provisional-call'));
  assert(converted.every(row=>row.result.every(message=>row.input.includes(message))));
  assert.equal(agent.state.messages.at(-1).content[0].text,'continued');
} catch(error){failures.push(error);}
finally{
  toolResult.resolve({content:[{type:'text',text:'tool-result-seven'}],details:{}});callbackRelease.resolve();
  try{agent.abort();}catch(error){failures.push(error);}
  for(let index=0;index<records.length;index++)await join(records[index]);
  try{const idle=retain('agent-idle-original',agent.waitForIdle());await join(idle);}catch(error){failures.push(error);}
  try{unsubscribe();}catch(error){failures.push(error);}
}
// Raw records remain live until output completes; exception edges retain shared identity.
function graph(roots){const ids=new Map(),queue=[],nodes=[];let edges=0;
  const add=e=>{if(ids.has(e))return ids.get(e);assert(ids.size<1024,'graph node limit');const id=ids.size;ids.set(e,id);queue.push(e);return id;};
  roots.forEach(add);for(let i=0;i<queue.length;i++){const e=queue[i];const children=e instanceof AggregateError?[...e.errors]:(e?.cause===undefined?[]:[e.cause]);
    edges+=children.length;assert(edges<=4096,'graph edge limit');nodes.push({id:ids.get(e),type:e?.name??typeof e,message:String(e?.message??e),stack:e?.stack,children:children.map(add)});}return nodes;}
try{
  const report={mode,authority:'AUTHORED_ORACLE_EXECUTION_ONLY_IF_ADMITTED; ORIGINAL_SOURCE_EXPECTATIONS_NOT_A_CAPTURE',
    requests,emissions,mutableCallAlias:startCall===endCall,earlierArgumentsObjectReplaced:deltaArguments!==endCall?.arguments,
    standardRoleConvertToLlmAliases:converted.map(row=>row.result.map(message=>row.input.includes(message))),
    originals:records.map(x=>({name:x.name,joined:x.joined,faulted:x.error!==undefined})),faultGraph:graph(failures)};
  console.log(JSON.stringify(report));
}catch(error){failures.push(error);}
if(failures.length)throw new AggregateError(failures,'Original oracle failures, raw records preserved',{cause:{records}});
