import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { cp, mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { DatabaseSync } from 'node:sqlite';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const [upstream, out] = process.argv.slice(2);
// No live provider is installed. Deny fetch before importing upstream as an additional tripwire.
globalThis.fetch = async () => { throw new Error('Network is forbidden in v1.5.0 fixture capture'); };
const at = path => pathToFileURL(join(upstream, path)).href;
const ai = await import(at('packages/ai/dist/index.js'));
const d = await import(at('packages/durable/src/index.ts'));
const { openNodeSqliteStorage } = await import(at('packages/durable/src/storage/sqlite/node.ts'));
const { openNodeJsonlStorage } = await import(at('packages/durable/src/storage/jsonl/node.ts'));
const { BACKGROUND_CONTEXT: context } = await import(at('packages/chord/src/context/index.ts'));
const NOW = 1767225600000;
const json = value => JSON.stringify(value, null, 2) + '\n';
const completed = result => ({ status: 'terminal', outcome: { status: 'completed', result } });
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(check) { for (let n=0; n<1000; n++) { if (await check()) return; await sleep(5); } throw new Error('Timed out waiting for checkpoint'); }
async function all(scan) { const page = await scan(); assert.equal(page.next, undefined); return [...page.items]; }
async function dump(storage) {
  try {
    const conversations = await all(() => storage.scanConversations({}, 1000, undefined, context));
    const entries = {};
    for (const c of conversations) entries[c.id] = await all(() => storage.scanEntries({ conversationId:c.id, order:'ascending' }, 1000, undefined, context));
    const tasks = await all(() => storage.scanTasks({}, 1000, undefined, context));
    const submissions = await all(() => storage.scanSubmissions({}, 1000, undefined, context));
    const scopes = [{kind:'session'}, ...conversations.map(c=>({kind:'conversation',conversationId:c.id})), ...tasks.map(t=>({kind:'task',taskId:t.id}))];
    const documents = [];
    for (const scope of scopes) for (const doc of await all(() => storage.scanDocuments({scope,at:'current'},1000,undefined,context))) {
      const stored = await storage.document(doc.id,'current',context);
      documents.push({record:stored.record,version:stored.version,value:stored.value});
    }
    return { conversations, entries, tasks, submissions, documents };
  } finally { await storage.close(context); }
}
function setup() {
  const faux = ai.fauxProvider({ api:'faux', models:[{id:'faux-1',contextWindow:100000,maxTokens:900}] });
  const models = ai.createModels(); models.setProvider(faux.provider);
  return { faux, models, registry:d.createRegistry(), settings:{compaction:{enabled:false,reserveTokens:1000,keepRecentTokens:150,backgroundTokens:0},stream:{cacheRetention:'none'}} };
}
// A separately owned process exits without closing its Harness. The persisted running task is real upstream
// output, not a hand-authored task record. Only its restart invocation can complete it.
const Restart = d.defineTask({name:'capture150.restart',version:1,initial:()=>({phase:'start'}),phases:{
  start:async(_task,runtime,ctx)=>{
    const record=await runtime.getTask(runtime.taskId,ctx);
    if ((record.restarts??0)>0) { await runtime.commit(()=>completed('resumed-after-process-exit'),ctx); return; }
    await new Promise((_,reject)=>runtime.signal.addEventListener('abort',()=>reject(runtime.signal.reason),{once:true}));
  },
},abort:async(_task,runtime,ctx)=>runtime.commit(()=>({status:'terminal',outcome:{status:'aborted'}}),ctx)});
if (process.argv[4]==='--exit-running') {
  const backend=process.argv[5], config=setup();
  await mkdir(out,{recursive:true});
  config.registry.install(d.defineExtension({name:'capture150.restart',tasks:[Restart]}));
  const storage=backend==='sqlite'?await openNodeSqliteStorage(join(out,'session.sqlite')):await openNodeJsonlStorage(out,context);
  const harness=await d.Harness.open(storage,{models:config.models,registry:config.registry,now:()=>NOW},context);
  const root=await harness.root(context);
  const id=await root.commit(tx=>tx.createTask(Restart,null,{ownership:{kind:'conversation'}}),context);
  harness.resume(); await until(async()=>(await harness.getTask(id,context))?.state.status==='running');
  process.stdout.write(JSON.stringify({id}),()=>process.exit(0));
  await new Promise(()=>{});
}
for (const backend of ['sqlite','jsonl']) {
  const base = join(out, backend); await mkdir(base,{recursive:true});
  const openStorage = async path => { await mkdir(path,{recursive:true}); return backend === 'sqlite' ? openNodeSqliteStorage(join(path,'session.sqlite')) : openNodeJsonlStorage(path,context); };
  const records = {};
  const persisted = {};
  async function save(name) {
    const directory=join(base,name);
    records[name] = await dump(await openStorage(directory));
    if (backend==='jsonl') {
      persisted[name]={};
      for (const file of (await readdir(directory)).sort()) persisted[name][file]=(await readFile(join(directory,file),'utf8')).trimEnd().split('\n').map(line=>JSON.parse(line));
    } else {
      const database=new DatabaseSync(join(directory,'session.sqlite'),{readOnly:true});
      try {
        persisted[name]={};
        for (const {name:table} of database.prepare("select name from sqlite_master where type='table' order by name").all()) {
          assert.match(table,/^[a-z_]+$/);
          persisted[name][table]=database.prepare(`select * from "${table}" order by rowid`).all().map(row=>Object.fromEntries(Object.entries(row).map(([key,value])=>
            [key,['record','content'].includes(key) && typeof value==='string' ? JSON.parse(value) : value])));
        }
      } finally { database.close(); }
    }
  }

  // A durable sleep survives two new Harness instances; an early wake must not run its after phase.
  let clock = NOW, after = 0;
  const Nap = d.defineTask({name:'capture150.nap',version:1,initial:()=>({phase:'start'}),phases:{
    start:async(_task,runtime,ctx)=>runtime.sleepUntil(NOW+1000,{phase:'after',slept:1000},ctx),
    after:async(_task,runtime,ctx)=>{ after++; await runtime.commit(()=>completed(runtime.now()),ctx); },
  },abort:async(_task,runtime,ctx)=>runtime.commit(()=>({status:'terminal',outcome:{status:'aborted'}}),ctx)});
  const config=setup(); config.registry.install(d.defineExtension({name:'capture150.tasks',tasks:[Nap]}));
  const openHarness = async name => d.Harness.open(await openStorage(join(base,name)),{models:config.models,registry:config.registry,settings:config.settings,now:()=>clock,onWake:()=>{}},context);
  let harness=await openHarness('sleeping'); let root=await harness.root(context);
  const taskId=await root.commit(tx=>tx.createTask(Nap,null,{ownership:{kind:'conversation'}}),context);
  harness.resume(); await until(async()=> (await harness.getTask(taskId,context))?.state.status==='sleeping');
  const sleeping=(await harness.getTask(taskId,context)).state;
  assert.equal(sleeping.until,NOW+1000); await harness.close(context); await save('sleeping');
  await cp(join(base,'sleeping'),join(base,'early-reopen'),{recursive:true});
  clock=NOW+999; harness=await openHarness('early-reopen'); harness.resume(); await sleep(25);
  assert.equal(after,0); assert.equal((await harness.getTask(taskId,context)).state.status,'sleeping');
  await harness.close(context); await save('early-reopen');
  await cp(join(base,'early-reopen'),join(base,'due-reopen'),{recursive:true});
  clock=NOW+1000; harness=await openHarness('due-reopen'); harness.resume();
  const receipt=await harness.waitForTask(taskId,context); assert.deepEqual(receipt.state.outcome,{status:'completed',result:clock}); assert.equal(after,1);
  await harness.close(context); await save('due-reopen');

  const crash=spawnSync(process.execPath,['--no-warnings','--conditions=source','--experimental-strip-types',fileURLToPath(import.meta.url),upstream,join(base,'process-exit'), '--exit-running',backend],{env:process.env,encoding:'utf8',timeout:30000});
  if(crash.error||crash.status!==0) throw new Error(`Restart subprocess failed: ${crash.error??crash.stderr}`);
  const restartId=JSON.parse(crash.stdout).id;
  await save('process-exit');
  assert.equal(records['process-exit'].tasks.find(t=>t.id===restartId).state.status,'running');
  config.registry.install(d.defineExtension({name:'capture150.restart',tasks:[Restart]}));
  await cp(join(base,'process-exit'),join(base,'restart-opened'),{recursive:true});
  harness=await openHarness('restart-opened');
  const recovered=await harness.getTask(restartId,context);
  assert.equal(recovered.restarts,1); assert.equal(recovered.state.status,'pending');
  await harness.close(context); await save('restart-opened');
  await cp(join(base,'restart-opened'),join(base,'restart-completed'),{recursive:true});
  harness=await openHarness('restart-completed'); harness.resume();
  const restarted=await harness.waitForTask(restartId,context);
  assert.deepEqual(restarted.state.outcome,{status:'completed',result:'resumed-after-process-exit'});
  await harness.close(context); await save('restart-completed');

  // Exact stored publications plus the host callback ordering around two concurrent ordered commits.
  const session=d.createSession(await openStorage(join(base,'commit-observers')),{now:()=>NOW});
  const Counter=d.defineDoc({kind:'capture150.counter',version:1,scope:'session',initial:()=>({count:0}),checkpointWhen:()=>false});
  await session.commit(async tx=>{await tx.doc(Counter);},context);
  const calls=[];
  const observed=await session.observeDoc(Counter,{advance:change=>calls.push({type:'document',change}),end:end=>calls.push({type:'document-end',kind:end.kind})},context);
  calls.push({type:'initial',value:observed.value});
  session.observeCommits({advance:publication=>calls.push({type:'commit',publication}),end:end=>calls.push({type:'commit-end',kind:end.kind})});
  const increment=by=>session.commit(async tx=>{(await tx.doc(Counter)).count+=by;},context).then(()=>calls.push({type:'resolved',by}));
  await Promise.all([increment(1),increment(2)]);
  assert.deepEqual(await session.snapshot(Counter,context),{count:3});
  await session.close(context); await save('commit-observers');
  await writeFile(join(base,'observers.json'),json(calls));

  // Real model/generation/run tasks and manual compaction from the new 1.5 API, with fixed faux responses.
  const chat=setup();
  chat.registry.install(d.defineExtension({name:'capture150.preamble',sections:[d.section('preamble',()=> 'You are helpful.',{tag:false})]}));
  const answer=text=>ai.fauxAssistantMessage(text,{timestamp:1000});
  chat.faux.setResponses([answer('a1 '+'x'.repeat(397)),answer('a2 '+'x'.repeat(397)),answer('a3 '+'x'.repeat(397)),answer('SUMMARY'),answer('done')]);
  harness=await d.Harness.open(await openStorage(join(base,'compaction')),{models:chat.models,registry:chat.registry,settings:chat.settings,now:()=>NOW},context);
  root=await harness.root(context,{agent:{model:{provider:'faux',modelId:'faux-1'}}}); harness.resume();
  for(let i=1;i<=3;i++) assert.equal((await (await root.submit({type:'input',content:`u${i} `+'x'.repeat(397)},context)).wait(context)).status,'done');
  const compactId=await root.compact('focus on files',context);
  const compact=(await harness.waitForTask(compactId,context)).state.outcome;
  assert.equal(compact.status,'completed'); assert.equal((await (await harness.submission(compact.result.submissionId,context)).wait(context)).status,'done');
  assert.equal((await (await root.submit({type:'input',content:'next'},context)).wait(context)).status,'done');
  await harness.waitForIdle(context); await harness.close(context); await save('compaction');
  assert.ok(Object.values(records.compaction.entries).flat().some(entry=>entry.kind==='pi.compaction'));
  await writeFile(join(base,'records.json'),json(records));
  await writeFile(join(base,'persisted.json'),json(persisted));
  await writeFile(join(base,'observed.json'),json({sleeping,after,completed:receipt.state.outcome,restarts:recovered.restarts,restarted:restarted.state.outcome,compaction:compact}));
}
