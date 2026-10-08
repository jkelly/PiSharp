import fs from 'node:fs';import path from 'node:path';import crypto from 'node:crypto';
const output=path.join(import.meta.dirname,'supplementary-history-layout-inputs.json');if(fs.existsSync(output))throw Error('Frozen supplemental input exists');
const set=data=>({kind:'set',data}),history=data=>({kind:'history',data}),key=data=>({kind:'input',data}),render=(columns,terminalRows=24)=>({kind:'render',columns,terminalRows});
const up=()=>key('\u001b[A'),down=()=>key('\u001b[B'),home=()=>key('\u0001'),right=()=>key('\u0006');
const smile='\u{1f642}',womanTechnologist='\u{1f469}\u200d\u{1f4bb}',family='\u{1f469}\u200d\u{1f469}\u200d\u{1f467}\u200d\u{1f466}',flag='\u{1f1fa}\u{1f1f8}',tone='\u{1f44d}\u{1f3fd}';
const cases=[
 {id:'genuine-supplementary-smile-cjk-sticky-resize',steps:[set('AB\u754cD\n1'+smile+'2\nabcdef'),render(12),home(),right(),right(),right(),up(),up(),down(),down(),render(5),up(),down()]},
 {id:'genuine-woman-technologist-combining-sticky-resize',steps:[set('Ae\u0301Z\n'+womanTechnologist+'x\nQQQQ'),render(10),home(),right(),right(),up(),up(),down(),render(4),down(),up(),render(16),down()]},
 {id:'genuine-family-flag-modifier-soft-wrap',steps:[set('a '+family+' '+flag+' '+tone+' z'),render(6),up(),up(),down(),render(9),down(),up(),render(32),up(),down()]},
 {id:'genuine-supplementary-source-half-open-end-affinity',steps:[set('a'+smile+'bc\n'+womanTechnologist+'\n'),render(5),up(),up(),down(),down(),render(3),up(),up(),down(),render(12),down()]},
 {id:'genuine-supplementary-history-first-last-wrap-and-draft',steps:[history('prior '+womanTechnologist),history('one '+smile+' two '+flag+' end'),set(tone+' draft'),render(6),home(),up(),up(),down(),down(),down(),render(18),down(),up()]},
 {id:'genuine-supplementary-page-and-terminal-height',steps:[set(Array.from({length:15},(_,i)=>String(i)+' '+smile+' '+womanTechnologist).join('\n')),render(8,20),key('\u001b[5~'),key('\u001b[6~'),render(14,40),key('\u001b[5~'),key('\u001b[6~'),render(8,5),up(),down()]},
 {id:'genuine-supplementary-paste-marker-following-emoji',steps:[set(smile),key('\u001b[200~'+Array.from({length:12},()=>womanTechnologist).join('\n')+'\u001b[201~'),render(9),up(),up(),down(),down(),render(5),up(),down(),render(24),up(),down()]},
 {id:'genuine-supplementary-history-raw-undo',steps:[history(smile+' '+womanTechnologist+' '+flag),set('seed '+tone),home(),up(),up(),key('X'),down(),key('\u001f'),render(8),up(),down(),render(32)]}
];
const witnesses=cases.map(c=>({id:c.id,inputStrings:c.steps.filter(s=>typeof s.data==='string').map(s=>({kind:s.kind,text:s.data,utf16Length:s.data.length,codePoints:Array.from(s.data,x=>x.codePointAt(0)),supplementaryCodePoints:Array.from(s.data,x=>x.codePointAt(0)).filter(cp=>cp>0xffff)}))}));
if(witnesses.some(w=>!w.inputStrings.some(s=>s.supplementaryCodePoints.length)))throw Error('Every genuine scenario needs actual supplementary input');
const data={schemaVersion:1,upstreamCommit:'d86654abb8862e201933517d6f1fce9f88dd117f',scope:'Supplemental genuine supplementary Unicode evidence; original malformed emoji-name fixtures remain frozen and are never replaced',cases,unicodeInputWitnesses:witnesses};
fs.writeFileSync(output,JSON.stringify(data,null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({output,cases:cases.length,checkpoints:cases.reduce((n,c)=>n+c.steps.length,0),sha256:crypto.createHash('sha256').update(fs.readFileSync(output)).digest('hex')}));
