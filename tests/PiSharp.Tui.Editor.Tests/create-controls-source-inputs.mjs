import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
const set = data => ({ kind: 'set', data }), input = data => ({ kind: 'input', data }), insert = data => ({ kind: 'insert', data });
const type = text => Array.from(text, input), paste = data => input('\x1b[200~' + data + '\x1b[201~');
const home = input('\x1b[H'), end = input('\x1b[F'), left = input('\x1b[D'), right = input('\x1b[C');
const u = input('\x15'), k = input('\x0b'), y = input('\x19'), pop = input('\x1by'), undo = input('\x1f');
const a = 'a'.repeat(1001), b = 'b'.repeat(1002);
const cases = [
  { id: 'controls-default-cursor-aliases-and-no-op-word-group-boundaries', steps: [set('abc\ndef'), input('\x01'), input('\x06'), input('\x02'), input('\x05'), input('\x1b[1;5H'), input('\x1b[1;5F'), input('X'), input('\x05'), input('Y'), undo, undo] },
  { id: 'controls-default-char-delete-aliases-and-no-op-callbacks', steps: [set('abc'), home, input('\x04'), input('\x1b[127;2u'), end, input('\x1b[57349;2u'), input('\x01'), input('\x04'), undo, undo, undo] },
  { id: 'controls-newline-raw-lf-coded-control-j-and-alt-enter', steps: [input('ab'), input('\n'), input('cd'), input('\x1b[106;5u'), input('ef'), input('\x1b\r'), input('G'), undo, undo, undo, undo, undo, undo, undo] },
  { id: 'controls-empty-line-kill-and-empty-yank-pop', steps: [u, k, y, pop, undo, set(''), k, u, y] },
  { id: 'controls-forward-line-kill-yank-and-each-undo', steps: [set('alpha beta'), home, right, right, k, y, undo, undo, undo] },
  { id: 'controls-backward-line-kill-yank-and-each-undo', steps: [set('alpha beta'), left, left, u, y, undo, undo, undo] },
  { id: 'controls-forward-kills-accumulate-newline-and-following-line', steps: [set('ab\ncd\nef'), home, k, k, k, y, undo, undo, undo, undo] },
  { id: 'controls-backward-kills-prepend-newline-and-previous-line', steps: [set('ab\ncd\nef'), u, u, u, y, undo, undo, undo, undo] },
  { id: 'controls-mixed-direction-kills-share-source-accumulation', steps: [set('left RIGHT\nEND'), home, right, right, right, right, right, k, u, k, y, undo, undo, undo] },
  { id: 'controls-yank-pop-two-stage-callbacks-and-ring-rotation', steps: [set('one'), u, set('TWO'), u, set('three'), u, y, pop, pop, pop, undo, y, pop, undo] },
  { id: 'controls-yank-pop-only-immediately-after-yank', steps: [set('one'), u, set('two'), u, y, left, pop, end, y, input('X'), pop, undo, pop] },
  { id: 'controls-no-op-kill-preserves-yank-pop-action', steps: [set('one'), u, set('two'), u, y, k, pop, undo, undo] },
  { id: 'controls-marker-line-kill-retains-registry-and-yanks-alias', steps: [paste(a), paste(b), home, k, y, pop, undo, u, y, undo, undo] },
  { id: 'controls-killed-marker-survives-set-text-but-registry-does-not', steps: [paste(a), u, set(''), y, undo, undo, y] },
  { id: 'controls-yank-multiline-and-cycle-different-line-counts', steps: [set('ab\ncd'), home, u, u, set('XYZ'), u, set('q\nr'), u, u, u, set('!'), home, y, pop, pop, pop, undo, undo] },
  { id: 'controls-scalar-seam-line-kill-and-grapheme-joining-yank', steps: [set('\u{1f469}\u{1f469}'), left, input('\u200d'), k, y, undo, undo, u, y, undo, undo] },
  { id: 'controls-movement-no-op-ends-kill-accumulation-and-undo-does-not-restore-ring', steps: [set('a\nb'), k, end, k, k, y, pop, undo, y, undo] },
  { id: 'controls-programmatic-insert-and-paste-end-kill-or-yank-actions', steps: [set('one'), u, insert('TWO'), u, y, paste('X'), pop, undo, y, input('\x03'), pop, undo] }
];
const output = path.join(import.meta.dirname, 'controls-source-inputs.json');
fs.writeFileSync(output, JSON.stringify({ schemaVersion: 1, upstreamCommit: 'd86654abb8862e201933517d6f1fce9f88dd117f', scope: 'New control editing/line kill/yank-only public Editor schedules; all three prior corpora reused unchanged; host submission/Reset and prompt history not captured', cases }, null, 2) + '\n', { flag: 'wx' });
console.log(JSON.stringify({ output, cases: cases.length, checkpoints: cases.reduce((sum, test) => sum + test.steps.length, 0), sha256: crypto.createHash('sha256').update(fs.readFileSync(output)).digest('hex') }));
