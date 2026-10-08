import fs from 'node:fs';import path from 'node:path';import crypto from 'node:crypto';import {spawnSync} from 'node:child_process';
const [output]=process.argv.slice(2);if(!output)throw Error('Provide a fresh output directory');const data=path.resolve(output),tests=import.meta.dirname;
const receipt=p=>({path:p,bytes:fs.statSync(p).size,sha256:crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex')});
const child=spawnSync(process.execPath,[path.join(tests,'generate-pinned-dictionary-data.mjs'),data],{windowsHide:true,timeout:30000,stdio:'pipe'});
if(child.status!==0)throw Error('Original pinned generator failed: '+child.stderr);const original=path.join(data,'PinnedWordDictionaryData.generated.cs.txt');
const compact=path.join(data,'PinnedWordDictionaryData.compact-storage.generated.cs.txt');fs.writeFileSync(compact,fs.readFileSync(original,'utf8').replaceAll(/internal const string (Cjk|Thai|Normalization)Base64 =/g,'internal static string $1Base64 =>'),{flag:'wx'});
fs.writeFileSync(path.join(data,'compact-storage-manifest.json'),JSON.stringify({schemaVersion:1,generator:receipt(import.meta.filename),originalGenerator:receipt(path.join(tests,'generate-pinned-dictionary-data.mjs')),original:receipt(original),compact:receipt(compact),operation:'Only change private compressed literal storage from const to getter; raw dictionaries, normalization and compressed bytes unchanged',childJoined:true,outcome:child.status},null,2)+'\n',{flag:'wx'});
console.log(JSON.stringify({original:receipt(original),compact:receipt(compact),childJoined:true}));
