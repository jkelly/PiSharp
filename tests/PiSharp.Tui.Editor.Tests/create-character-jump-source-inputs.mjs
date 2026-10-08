import fs from 'node:fs';import path from 'node:path';import crypto from 'node:crypto';
const output=path.join(import.meta.dirname,'character-jump-source-inputs.json');if(fs.existsSync(output))throw Error('Frozen jump input exists');
const set=data=>({kind:'set',data}),key=data=>({kind:'input',data}),history=data=>({kind:'history',data}),insert=data=>({kind:'insert',data});
const forward=()=>key('\u001d'),backward=()=>key('\u001b\u001d'),home=()=>key('\u0001'),end=()=>key('\u0005'),undo=()=>key('\u001f'),pageUp=()=>key('\u001b[5~');
const cases=[
 {id:'jump-forward-repeated-current-character-excluded',steps:[set('a x a x a'),home(),forward(),key('a'),forward(),key('a'),forward(),key('x'),key('!')]},
 {id:'jump-backward-repeated-and-zero-index-source-quirk',steps:[set('a x a x a'),backward(),key('a'),backward(),key('a'),backward(),key('a'),backward(),key('x')]},
 {id:'jump-forward-across-empty-logical-lines',steps:[set('one x\n\nmid x\nlast x'),home(),pageUp(),forward(),key('x'),forward(),key('x'),forward(),key('x'),forward(),key('x')]},
 {id:'jump-backward-across-empty-logical-lines',steps:[set('one x\n\nmid x\nlast x'),backward(),key('x'),backward(),key('x'),backward(),key('x'),backward(),key('z')]},
 {id:'jump-no-match-consumes-search-and-resets-typing-action',steps:[set('abc'),home(),key('q'),forward(),key('z'),key('!'),undo(),undo()]},
 {id:'jump-same-and-opposite-hotkeys-cancel-not-replace',steps:[set('abc x'),home(),forward(),forward(),key('x'),backward(),forward(),key('x'),undo()]},
 {id:'jump-pending-control-falls-through-left-newline-and-undo',steps:[set('abc x'),forward(),key('\u0002'),key('!'),forward(),key('\u000a'),key('z'),forward(),undo(),key('x')]},
 {id:'jump-pending-kill-yank-and-two-phase-yank-pop',steps:[set('one\ntwo'),key('\u0015'),set('three'),key('\u0015'),key('\u0019'),forward(),key('\u001by'),forward(),key('\u000b'),undo(),key('x')]},
 {id:'jump-compound-printable-search-payload',steps:[set('abcbc abc'),home(),forward(),key('bc'),forward(),key('abc'),backward(),key('bc'),key('!')]},
 {id:'jump-genuine-supplementary-and-combining-target',steps:[set('a\u{1f642} e\u0301 \u{1f469}\u200d\u{1f4bb} end'),home(),forward(),key('\u{1f642}'),forward(),key('\u0301'),forward(),key('\u{1f469}\u200d\u{1f4bb}'),backward(),key('\u{1f642}')]},
 {id:'jump-registered-marker-text-interior-and-source-edit',steps:[set(''),key('\u001b[200~'+new Array(1002).join('p')+'\u001b[201~'),home(),forward(),key('1'),key('\u007f'),undo(),backward(),key('p')]},
 {id:'jump-literal-marker-and-substring',steps:[set('[paste #1] [paste #2]'),home(),forward(),key('2'),backward(),key('paste'),forward(),key('#'),key('!')]},
 {id:'jump-programmatic-set-preserves-pending-mode',steps:[set('old'),forward(),set('xabcx'),home(),forward(),set('xabcx'),key('x'),key('!')]},
 {id:'jump-programmatic-insert-preserves-pending-mode',steps:[set('a x x'),home(),forward(),insert('Q'),key('x'),key('!'),undo()]},
 {id:'jump-pending-history-add-and-navigation-control',steps:[history('old x'),history('new x'),set('draft x'),home(),forward(),history('later x'),key('\u001b[A'),forward(),key('x'),key('\u001b[B'),undo()]},
 {id:'jump-kitty-hotkeys-and-ordinary-shifted-printable-target',steps:[set('aX bX'),home(),key('\u001b[93;5u'),key('\u001b[88;2u'),key('\u001b[93;7u'),key('\u001b[97;1u'),key('!')]},
 {id:'jump-kitty-supplementary-target-and-repeat-hotkey',steps:[set('a\u{1f642}b\u{1f642}'),home(),key('\u001b[93;5:2u'),key('\u001b[128578;1u'),key('\u001b[93;5:2u'),key('\u001b[128578;1:2u'),key('!')]},
 {id:'jump-kitty-target-release-cancels-before-next-text',steps:[set('abc x'),home(),forward(),key('\u001b[120;1:3u'),key('x'),key('!')]},
 {id:'jump-kitty-hotkey-release-cancels-before-next-text',steps:[set('abc x'),home(),forward(),key('\u001b[93;5:3u'),key('x'),key('!')]},
 {id:'jump-protocol-focus-and-alt-key-cancel-fallthrough',steps:[set('abc x'),home(),forward(),key('\u001b[I'),key('x'),forward(),key('\u001bx'),key('x')]},
 {id:'jump-bracketed-paste-cancels-and-keeps-normal-paste',steps:[set('abc x'),home(),forward(),key('\u001b[200~xy\u001b[201~'),key('x'),undo(),undo()]},
 {id:'jump-enter-host-reset-and-next-prompt-does-not-search',steps:[set('abc x'),home(),forward(),key('\r'),key('x'),key('!')]}
];
const input={schemaVersion:1,upstreamCommit:'d86654abb8862e201933517d6f1fce9f88dd117f',scope:'Original default character jumps, public source full observations, no renders or private state injection. Existing source profile and escaped HOLD corpora remain untouched.',cases};
fs.writeFileSync(output,JSON.stringify(input,null,2)+'\n',{flag:'wx'});console.log(JSON.stringify({output,cases:cases.length,checkpoints:cases.reduce((n,c)=>n+c.steps.length,0),sha256:crypto.createHash('sha256').update(fs.readFileSync(output)).digest('hex')}));
