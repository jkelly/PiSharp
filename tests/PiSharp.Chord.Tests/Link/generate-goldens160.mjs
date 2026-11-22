// Pi v1.6.0 goldens (the 1.5.2 generator beside this one stays as it is, with its fixture).
// Runs the frozen codecs unchanged after TypeScript erasure. Only their unrelated
// schema/telemetry imports are replaced; byte and tuple algorithms remain source code.
// Run from the repository root: node tests/PiSharp.Chord.Tests/Link/generate-goldens160.mjs [upstream v1.6.0 checkout]
import fs from 'node:fs';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
import {stripTypeScriptTypes} from 'node:module';
const root=path.resolve(process.argv[2] ?? 'E:/AI/PiSharp/upstream-v160');
const sha=execFileSync('git',['-c',`safe.directory=${root.replaceAll('\\','/')}`,'rev-parse','HEAD'],{cwd:root,encoding:'utf8'}).trim();
if(sha!=='faac585884ae2c013a6b1400f3e9e77f10b34bac') throw new Error('wrong frozen source');
const data=source=>'data:text/javascript;base64,'+Buffer.from(source).toString('base64');
const imports=new Map([
 ['./message.ts',data('export const CAUSE_KINDS=["transient","broken","waiting","denied"]; export const NO_RETIRED=[];')],
 ['../data/index.ts',data('export class ChordError extends Error {}')],
 ['../data/internal.ts',data('export const invalid=(_code,message)=>new Error(message);')],
 ['../telemetry.ts',data('export const chordMetrics=()=>({counter:()=>({add(){}})});')],
]);
function moduleUrl(name) {
 let js=stripTypeScriptTypes(fs.readFileSync(path.join(root,'packages/chord/src/link',name),'utf8'));
 js=js.replace(/from\s+"([^"]+)"/g,(all,name)=>imports.has(name)?`from ${JSON.stringify(imports.get(name))}`:all);
 return data(js);
}
imports.set('./part.ts',moduleUrl('part.ts'));
imports.set('./values.ts',moduleUrl('values.ts'));
const {binaryCodec}=await import(moduleUrl('binary.ts'));
const {textCodec}=await import(moduleUrl('text.ts'));
const test=fs.readFileSync(path.join(root,'packages/chord/test/link/message.test.ts'),'utf8');
const begin=test.indexOf('const incarnation =');
const end=test.indexOf('/** A decoded message');
if(begin<0 || end<0) throw new Error('source test markers changed');
const {EVERY_KIND}=await import(data(stripTypeScriptTypes(test.slice(begin,end)+'\nexport {EVERY_KIND};')));
const messages=[...EVERY_KIND,
 {kind:'call',call:1,hold:2,method:3,args:'{"x":{"$bytes":0}}',segments:[new Uint8Array([0,10,255])],trace:{a:'b'},old:true},
 {kind:'call',call:2,hold:2,method:3,trace:{empty:''}},
 // 1.6.0 shapes EVERY_KIND does not combine: the smallest window, an event ack and events under an origin.
 {kind:'hello',protocol:1,incarnation:'0190f5a2-7c3e-7d4a-9b1c-2d3e4f5a6b7c',calls:1},
 {kind:'ack',origin:1,events:1},
 {kind:'event',origin:300,events:[{hold:1,member:0,at:9,value:'{"y":1}'},{hold:1,member:1,at:9007199254740991,value:'[]'}]},
];
const rows=messages.map((message,id)=>({id,binary:Buffer.from(binaryCodec.encode(message)).toString('base64'),text:Buffer.from(textCodec.encode(message)).toString('base64')}));
const output=path.resolve('tests/fixtures/chord-link-v160.json');
fs.writeFileSync(output,JSON.stringify({sourceSha:sha,node:process.version,source:'packages/chord/test/link/message.test.ts EVERY_KIND + nested segment/empty carrier value cases + 1.6.0 hello/ack/event combinations',rows},null,2)+'\n');
console.log(`Captured ${rows.length} frozen wire frames to ${output}`);
