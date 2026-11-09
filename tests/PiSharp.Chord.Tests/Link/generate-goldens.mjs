// Offline capture from actual v1.5.0 modules. Run with Node 24:
// node --conditions=source --experimental-strip-types tests/PiSharp.Chord.Tests/Link/generate-goldens.mjs E:/AI/PiSharp/upstream-v150
import fs from 'node:fs';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import {execFileSync} from 'node:child_process';
import {stripTypeScriptTypes} from 'node:module';
const root=path.resolve(process.argv[2] ?? 'E:/AI/PiSharp/upstream-v150');
const sha=execFileSync('git',['-c',`safe.directory=${root.replaceAll('\\','/')}`,'rev-parse','HEAD'],{cwd:root,encoding:'utf8'}).trim();
if(sha!=='f9a26365568127461c47a07a37dcb42c140a3ac5') throw new Error('wrong upstream checkout');
const source=fs.readFileSync(path.join(root,'packages/chord/test/next/link/message.test.ts'),'utf8');
const program=source.slice(source.indexOf('const incarnation ='),source.indexOf('/** A decoded message'));
const js=stripTypeScriptTypes(program+'\nexport { EVERY_KIND };');
const {EVERY_KIND}=await import('data:text/javascript;base64,'+Buffer.from(js).toString('base64'));
const {binaryCodec}=await import(pathToFileURL(path.join(root,'packages/chord/src/next/link/binary.ts')));
const {textCodec}=await import(pathToFileURL(path.join(root,'packages/chord/src/next/link/text.ts')));
const {DeflatedPart}=await import(pathToFileURL(path.join(root,'packages/chord/src/next/link/part.ts')));
const {deflateRawSync}=await import('node:zlib');
const messages=[...EVERY_KIND,
 {kind:'call',origin:3,call:9007199254740991,hold:128,method:7,args:'{"$bytes":0}',segments:[new Uint8Array([0,10,255]),new Uint8Array()]},
 {kind:'result',call:1,value:'null',segments:[]},
 {kind:'result',call:1,value:new DeflatedPart(deflateRawSync(Buffer.from('{"text":"hello"}')))},
 {kind:'hold',hold:1,type:'\ufeffé😀\ud800',version:1,fingerprint:'0123456789abcdef',params:'{"s":"\\ud800"}',address:{kind:'server',params:{}}},
 {kind:'result',call:1,value:JSON.stringify('x'.repeat(4096))},
];
const rows=messages.map((message,i)=>({id:i,binary:Buffer.from(binaryCodec.encode(message)).toString('base64'),text:Buffer.from(textCodec.encode(message)).toString('base64')}));
const output=path.resolve('tests/fixtures/chord-link-v150.json');
fs.writeFileSync(output,JSON.stringify({sourceSha:sha,node:process.version,source:'packages/chord/test/next/link/message.test.ts EVERY_KIND + segment/deflate/Unicode/long-string cases',rows},null,2)+'\n');
console.log(`Captured ${rows.length} wire messages to ${output}`);
