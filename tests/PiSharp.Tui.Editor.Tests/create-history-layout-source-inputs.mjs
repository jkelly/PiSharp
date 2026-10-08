import fs from 'node:fs';import path from 'node:path';import crypto from 'node:crypto';
const output=path.join(import.meta.dirname,'history-layout-source-inputs.json');if(fs.existsSync(output))throw Error('Frozen inputs already exist.');
const add=data=>({kind:'history',data}),set=data=>({kind:'set',data}),key=data=>({kind:'input',data}),insert=data=>({kind:'insert',data}),render=(columns,terminalRows=24)=>({kind:'render',columns,terminalRows});
const up=()=>key('\u001b[A'),down=()=>key('\u001b[B'),home=()=>key('\u0001'),end=()=>key('\u0005'),undo=()=>key('\u001f');
const cases=[
 {id:'history-trim-empty-consecutive-and-nonconsecutive-duplicates',steps:[add(' \t\r\n'),add('\ufeff alpha \ufeff'),add('alpha'),add(' beta '),add('alpha'),set(''),up(),up(),up(),up(),down(),down(),down(),down()]},
 {id:'history-ecmascript-trim-preserves-nel-and-raw-line-controls',steps:[add('\u0085A\u0085'),add(' x\r\ny\tZ '),set(''),up(),down(),up(),up(),render(16),down(),down()]},
 {id:'history-basic-older-newer-and-out-of-range',steps:[add('one'),add('two'),set(''),up(),up(),up(),down(),down(),down()]},
 {id:'history-first-row-up-jumps-to-start-before-recall',steps:[add('previous'),set('draft'),render(32),up(),up(),down(),down()]},
 {id:'history-prebrowse-multiline-draft-and-exact-caret-return',steps:[add('old'),set('draft\nrest'),home(),up(),up(),down(),down(),up()]},
 {id:'history-multiline-recall-placement-and-row-gated-down',steps:[add('older\nlast'),add('newer\nx'),set(''),up(),down(),down(),up(),up(),down(),down(),down()]},
 {id:'history-typing-exits-browse-and-rebrowse-preserves-edit',steps:[add('one'),add('two'),set(''),up(),key('X'),down(),up(),up(),down(),undo()]},
 {id:'history-programmatic-set-and-insert-exit-browse',steps:[add('one'),add('two'),set(''),up(),set('fresh'),up(),up(),insert('!'),down(),up(),up(),down(),undo()]},
 {id:'history-backspace-delete-and-noop-edit-exit-browse',steps:[add('one'),add('two'),set(''),up(),key('\u007f'),down(),up(),key('\u001b[3~'),down(),undo(),up(),key('\u007f'),down()]},
 {id:'history-kill-yank-and-undo-exit-browse',steps:[add('one'),add('two'),set(''),up(),key('\u000b'),down(),key('\u0019'),up(),up(),undo(),down(),up(),key('\u0015'),down()]},
 {id:'history-first-entry-one-undo-unit-and-draft-return',steps:[add('one'),add('two'),set('seed'),home(),up(),up(),down(),down(),undo(),undo()]},
 {id:'history-registry-survives-recall-and-prebrowse-marker-returns',steps:[add('old'),set(''),key('\u001b[200~'+new Array(1002).join('p')+'\u001b[201~'),home(),up(),down(),undo(),up(),down()]},
 {id:'history-literal-marker-entry-expands-current-registry',steps:[add('[paste #1 1001 chars]'),set(''),key('\u001b[200~'+new Array(1002).join('p')+'\u001b[201~'),home(),up(),down(),undo()]},
 {id:'history-empty-paste-exits-browse-and-source-undo',steps:[add('one'),add('two'),set(''),up(),key('\u001b[200~\u001b[201~'),down(),undo(),up(),down()]},
 {id:'history-successful-source-submit-resets-browse-not-history',steps:[add('one'),set('prompt'),key('\r'),add('prompt'),up(),down(),undo(),up()]},
 {id:'history-frozen-hundred-entry-eviction-and-boundary',steps:[...Array.from({length:101},(_,i)=>add('h'+String(i).padStart(3,'0'))),set(''),...Array.from({length:102},up),...Array.from({length:102},down)]},
 {id:'vertical-utf16-sticky-long-short-long',steps:[set('abcdef\nx\n123456'),render(32),home(),key('\u0006'),key('\u0006'),key('\u0006'),key('\u0006'),up(),up(),down(),down(),down(),up()]},
 {id:'vertical-word-wrap-and-public-width-resize',steps:[set('one two three four five six'),render(9),up(),up(),render(5),down(),up(),render(14),down(),up(),up(),down()]},
 {id:'vertical-cjk-emoji-offset-vs-cells-and-atomic-snap',steps:[set('AB\u754cD\n1\U0001f6422\nabcdef'),render(12),home(),key('\u0006'),key('\u0006'),key('\u0006'),up(),up(),down(),down(),render(5),up(),down()]},
 {id:'vertical-combining-and-zwj-source-seams',steps:[set('Ae\u0301Z\n\U0001f469\u200d\U0001f4bbx\nQQQQ'),render(10),home(),key('\u0006'),key('\u0006'),up(),up(),down(),render(4),down(),up(),render(16),down()]},
 {id:'vertical-registered-marker-continuations-and-resize',steps:[set(''),key('\u001b[200~'+Array.from({length:12},()=> 'p').join('\n')+'\u001b[201~'),render(9),up(),up(),up(),down(),down(),down(),render(5),up(),down(),render(24),up(),down()]},
 {id:'vertical-page-size-uses-public-terminal-rows',steps:[set(Array.from({length:18},(_,i)=>'line'+String(i).padStart(2,'0')).join('\n')),render(16,20),key('\u001b[5~'),key('\u001b[5~'),key('\u001b[6~'),render(16,40),key('\u001b[5~'),key('\u001b[6~'),key('\u001b[6~')]},
 {id:'vertical-exact-width-end-affinity-and-empty-lines',steps:[set('abcd\n\nxy\n'),render(5),up(),up(),up(),down(),down(),down(),set('abcd'),render(5),up(),down(),render(1),up(),down()]},
 {id:'history-recall-first-last-rows-change-with-wrap',steps:[add('old'),add('one two three four'),set(''),render(7),up(),down(),down(),down(),down(),up(),render(24),down()]}
];
const data={schemaVersion:1,upstreamCommit:'d86654abb8862e201933517d6f1fce9f88dd117f',scope:'Public prompt history plus vertical/page navigation and render geometry; native comparison pending shared visual map contract',cases};
fs.writeFileSync(output,JSON.stringify(data,null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({output,cases:cases.length,steps:cases.reduce((n,c)=>n+c.steps.length,0),sha256:crypto.createHash('sha256').update(fs.readFileSync(output)).digest('hex')}));
