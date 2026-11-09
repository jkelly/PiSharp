import assert from 'node:assert/strict';
import { createReadStream } from 'node:fs';
import { copyFile,mkdir,readFile,writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
const [upstream,out]=process.argv.slice(2),at=path=>pathToFileURL(join(upstream,path)).href;
globalThis.fetch=async()=>{throw new Error('Network forbidden in schema-switch corpus');};
const {BACKGROUND_CONTEXT:context}=await import(at('packages/chord/src/context/index.ts'));
const declared=await import(at('packages/chord/test/next/schema/declarations.ts'));
const {fingerprints,storedReads}=await import(at('packages/chord/test/next/schema/proofs.ts'));
const {ownerHost,finished}=await import(at('packages/chord/test/next/schema/owner.ts'));
const {openStore,sqliteBackend,encodeJsonl,decodeJsonl,importStore}=await import(at('packages/chord/src/next/store/index.ts'));
const {openNodeSqliteDatabase}=await import(at('packages/chord/src/sqlite/node.ts'));
const text=value=>JSON.stringify(value,null,2)+'\n';
await mkdir(out,{recursive:true});
const historical=JSON.parse(await readFile(join(upstream,'packages/chord/test/next/schema/recorded.json'),'utf8'));
const captured={fingerprints:fingerprints(),stored:await storedReads(context)};
assert.deepEqual(captured,historical,'the entire upstream schema-switch proof');
await writeFile(join(out,'recorded.json'),text(captured));

// Same public owner/test declaration and operation sequence as upstream write-stored.ts; different output directory.
const path=join(out,'store.db');
const {store,host}=await ownerHost(path,declared.EditsV1,'wait',context);
const owner=await host.get(declared.Owner,{},context);
let writes=0;
for(const [id,body] of [['a','first note'],['b','second'],['c','third one']]) {
  await owner.write({id,body},context); writes++;
  // Complete each job's progress write before admitting the next owner transaction, so independent runs use
  // the same valid commit ordering. The persisted state revision advances from its initial base to done=1.
  for(let n=0;n<100;n++){
    const parts=await exported(store),states=parts.filter(p=>p.kind==='document.create'&&p.record.namespace==='chord.job-states');
    const ready=states.length===writes&&states.every(p=>p.record.revision>=2);
    if(ready)break;if(n===99)throw new Error('Job progress was not persisted');await new Promise(r=>setTimeout(r,10));
  }
}
await saveArchive(store,join(out,'running.jsonl'));
await host.close(context);
await saveArchive(store,join(out,'stopped.jsonl'));
await store.close(context);

await copyFile(path,join(out,'resumed-store.db'));
const fromStore=await resume(join(out,'resumed-store.db'));
const importedPath=join(out,'resumed-archive.db');
const imported=await importStore(sqliteBackend(await openNodeSqliteDatabase(importedPath)),decodeJsonl(createReadStream(join(out,'running.jsonl'))),context);
await imported.close(context);
const fromArchive=await resume(importedPath);
assert.deepEqual(fromStore,historical.stored.store);
assert.deepEqual(fromArchive,historical.stored.archive);
await writeFile(join(out,'fresh-read.json'),text({store:fromStore,archive:fromArchive}));
async function exported(store){const parts=[];for await(const part of store.export(context))parts.push(part);return parts;}
async function saveArchive(store,path){const chunks=[];for await(const chunk of encodeJsonl(store.export(context)))chunks.push(Buffer.from(chunk));await writeFile(path,Buffer.concat(chunks));}
async function resume(path){
  finished.length=0;
  const {store,host}=await ownerHost(path,declared.EditsV3,'finish',context);
  try{
    const owner=await host.get(declared.Owner,{},context);
    for(let n=0;n<100&&finished.length<3;n++)await new Promise(r=>setTimeout(r,10));
    assert.equal(finished.length,3,'all persisted jobs resume and finish');
    const result={dump:await owner.dump({},context),finished:[...finished],note:await owner.read('b',context)};
    await saveArchive(store,path.replace(/\.db$/,'.jsonl'));
    return result;
  }finally{await host.close(context);await store.close(context);}
}
