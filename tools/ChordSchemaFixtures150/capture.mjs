// Full schema-switch proof and newly written table/job stores, independent of historical fixture files.
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, readFile, readdir, writeFile, copyFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const PIN='f9a26365568127461c47a07a37dcb42c140a3ac5';
const [input, destination]=process.argv.slice(2);
if(!input||!destination) throw new Error('Usage: node tools/ChordSchemaFixtures150/capture.mjs <upstream-v150> <new destination>');
const upstream=resolve(input), out=resolve(destination), here=dirname(fileURLToPath(import.meta.url));
const env=Object.fromEntries(Object.entries(process.env).filter(([key])=>['SYSTEMROOT','WINDIR','PATH','PATHEXT','TEMP','TMP','COMSPEC'].includes(key.toUpperCase())));
function run(executable,args){
  if(executable==='git') args=['-c',`safe.directory=${upstream.replaceAll('\\','/')}`,...args];
  const result=spawnSync(executable,args,{cwd:upstream,env,encoding:'utf8',timeout:120000,maxBuffer:8*1024*1024});
  if(result.error||result.status!==0) throw new Error(`${executable} failed: ${result.error??result.stderr}`);
  return result.stdout.trim();
}
if(run('git',['rev-parse','HEAD'])!==PIN||run('git',['status','--porcelain','--untracked-files=no'])) throw new Error('An unchanged exact upstream checkout is required');
for(const pkg of ['telemetry','schema']) run(process.execPath,['node_modules/typescript/bin/tsc','--noCheck','-p',`packages/${pkg}/tsconfig.build.json`]);
await mkdir(dirname(out),{recursive:true}); await mkdir(out);
const originals=['recorded.json','stored/store.db','stored/archive.jsonl'];
await mkdir(join(out,'historical/stored'),{recursive:true});
for(const file of originals) await copyFile(join(upstream,'packages/chord/test/next/schema',file),join(out,'historical',file));
for(const pass of ['first','second']) run(process.execPath,['--no-warnings','--conditions=source','--experimental-strip-types',join(here,'worker.mjs'),upstream,join(out,pass)]);
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
async function files(root,prefix=''){
  const result=[];
  for(const item of (await readdir(join(root,prefix),{withFileTypes:true})).sort((a,b)=>a.name.localeCompare(b.name))){
    const path=prefix?`${prefix}/${item.name}`:item.name;
    if(item.isDirectory())result.push(...await files(root,path));
    else {const bytes=await readFile(join(root,path));result.push({path,bytes:bytes.length,sha256:hash(bytes)});}
  }return result;
}
const first=await files(join(out,'first')),second=await files(join(out,'second'));
if(JSON.stringify(first.map(x=>x.path))!==JSON.stringify(second.map(x=>x.path)))throw new Error('Different capture file inventories');
const jobMaps={};
for(const pass of ['first','second']){
  const parts=(await readFile(join(out,pass,'running.jsonl'),'utf8')).trim().split('\n').map(JSON.parse);
  const keys=parts.filter(p=>p.kind==='document.create'&&p.record.namespace==='chord.jobs').map(p=>p.record.key);
  jobMaps[pass]=new Map(keys.map((key,index)=>[key,`<job:${index}>`]));
}
function normalize(value,map){
  if(typeof value==='string')return map.get(value)??value;
  if(Array.isArray(value))return value.map(item=>normalize(item,map));
  if(value&&typeof value==='object')return Object.fromEntries(Object.entries(value).map(([key,item])=>[key,normalize(item,map)]));
  return value;
}
const compared=[];
for(const file of first.filter(x=>/\.jsonl?$/.test(x.path))){
  const results=[];
  for(const pass of ['first','second']){
    const bytes=await readFile(join(out,pass,file.path),'utf8');
    const value=file.path.endsWith('.jsonl')?bytes.trim().split('\n').map(JSON.parse):JSON.parse(bytes);
    results.push(JSON.stringify(normalize(value,jobMaps[pass])));
  }
  if(results[0]!==results[1]) throw new Error(`Non-reproducible capture: ${file.path}`);
  compared.push({path:file.path,normalizedSha256:hash(results[0])});
}
const source=[];
for(const path of run('git',['ls-files','packages/chord/src','packages/chord/test/next/schema','packages/schema/src','packages/telemetry/src']).split(/\r?\n/))source.push({path,sha256:hash(await readFile(join(upstream,path)))});
await writeFile(join(out,'.gitattributes'),'* -text\n');
await writeFile(join(out,'manifest.json'),JSON.stringify({
  kind:'PASS_UPSTREAM_CAPTURE',upstream:{commit:PIN,release:'v1.5.0',sourceTreeSha256:hash(JSON.stringify(source)),files:source},
  node:{version:process.version,executableSha256:hash(await readFile(process.execPath))},dependencyLockSha256:hash(await readFile(join(upstream,'package-lock.json'))),
  driverSha256:hash(await readFile(fileURLToPath(import.meta.url))),scenarioSha256:hash(await readFile(join(here,'worker.mjs'))),
  inheritedEnvironment:Object.keys(env).sort(),network:'fetch denied; no model/provider registered',
  normalization:[{field:'Persisted job UUID strings',rule:'Only exact strings listed as chord.jobs document keys, mapped in creation order; all other fields retained',reason:'host uuidv7 uses wall time and random bits'}],
  historical:await files(join(out,'historical')),runs:{first,second},reproducibility:{runs:2,result:'equal after declared job UUID normalization',compared},
  command:'node tools/ChordSchemaFixtures150/capture.mjs <exact upstream-v150> <new destination>',
},null,2)+'\n');
console.log(`Captured full schema-switch proof and fresh table/job stores twice: ${compared.length} equal JSON/JSONL outputs`);
