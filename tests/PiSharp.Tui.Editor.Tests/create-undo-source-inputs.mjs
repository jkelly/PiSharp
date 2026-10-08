import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
const set = data => ({ kind: 'set', data }), insert = data => ({ kind: 'insert', data }), input = data => ({ kind: 'input', data });
const type = text => Array.from(text, input);
const paste = data => input('\x1b[200~' + data + '\x1b[201~');
const undo = input('\x1b[45;5u'), rawUndo = input('\x1f'), underscore = input('\x1b[95;5u');
const home = input('\x1b[H'), end = input('\x1b[F'), left = input('\x1b[D'), right = input('\x1b[C');
const back = input('\x7f'), del = input('\x1b[3~'), newline = input('\x1b[13;2u');
const a = 'a'.repeat(1001), b = 'b'.repeat(1002), c = 'c'.repeat(1003);
const cases = [
  { id: 'undo-empty-stack-and-distinct-underscore-key', steps: [undo, rawUndo, underscore, set(''), undo] },
  { id: 'undo-word-and-space-coalescing', steps: [...type('hello world!'), undo, undo, undo, input('Z'), undo] },
  { id: 'undo-consecutive-spaces-one-unit-each', steps: [...type('hi  there'), undo, undo, undo] },
  { id: 'undo-ecmascript-whitespace-bom-nbsp-and-nel-distinction', steps: [...type('A\u00a0B\ufeffC\u0085D'), undo, undo, undo] },
  { id: 'undo-movement-and-no-op-navigation-start-new-units', steps: [...type('abc'), end, input('X'), undo, left, input('Y'), undo, undo] },
  { id: 'undo-no-op-deletion-callbacks-without-new-snapshots', steps: [input('a'), del, input('b'), undo, undo, set('a'), home, back, input('b'), undo, undo] },
  { id: 'undo-programmatic-normalization-clear-and-empty-insert', steps: [set('a\r\nb\tc'), insert('X\r\nY\td'), undo, set(''), undo, insert(''), undo, set(''), undo] },
  { id: 'undo-legacy-minus-csi-minus-and-ignored-control-z', steps: [set('abc'), underscore, rawUndo, input('def'), input('\x1b[122;5u'), input('X'), undo, underscore, undo] },
  { id: 'undo-small-multiline-and-filtered-empty-paste', steps: [...type('ab'), left, paste('c\r\n\td'), undo, paste('\x03'), undo, input('Z'), undo, paste(''), undo] },
  { id: 'undo-newline-and-following-word-are-separate-units', steps: [...type('ab'), newline, input('C'), undo, undo, undo] },
  { id: 'undo-large-paste-backspace-restores-registry-and-seam', steps: [input('A'), paste(a), input('B'), home, right, right, back, undo, undo, undo, undo] },
  { id: 'undo-renumber-and-forward-delete-restore-counter-and-ordered-registry', steps: [paste(a), paste(b), home, right, back, undo, paste(c), undo, home, del, undo, undo] },
  { id: 'undo-set-text-restores-pastes-and-same-text-set-does-not-push', steps: [paste(a), set('replacement'), undo, set(''), undo, set('[paste #1 1001 chars]'), undo, paste(b)] },
  { id: 'undo-restores-unknown-alias-suffix-before-renumber', steps: [paste(a), paste(b), insert('[paste #99][paste #2]'), home, right, back, undo, undo] },
  { id: 'undo-restores-source-cursor-inside-joined-zwj-grapheme', steps: [set('\u{1f469}\u{1f469}'), left, input('\u200d'), back, undo, del, undo, undo, undo] },
  { id: 'undo-bulk-printable-input-with-whitespace-is-one-source-operation', steps: [input('X'), input('a b'), input('c'), undo, undo] }
];
const output = path.join(import.meta.dirname, 'undo-source-inputs.json');
fs.writeFileSync(output, JSON.stringify({ schemaVersion: 1, upstreamCommit: 'd86654abb8862e201933517d6f1fce9f88dd117f', scope: 'New undo-only public Editor schedules; original text/paste corpora reused unchanged; host submission/reset and history navigation not captured', cases }, null, 2) + '\n', { flag: 'wx' });
console.log(JSON.stringify({ output, cases: cases.length, checkpoints: cases.reduce((sum, test) => sum + test.steps.length, 0), sha256: crypto.createHash('sha256').update(fs.readFileSync(output)).digest('hex') }));
