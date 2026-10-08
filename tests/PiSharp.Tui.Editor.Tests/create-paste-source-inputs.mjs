import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
const set = data => ({ kind: 'set', data });
const insert = data => ({ kind: 'insert', data });
const input = data => ({ kind: 'input', data });
const paste = data => input('\x1b[200~' + data + '\x1b[201~');
const home = input('\x1b[H'), end = input('\x1b[F'), left = input('\x1b[D'), right = input('\x1b[C');
const back = input('\x7f'), del = input('\x1b[3~');
const a = 'a'.repeat(1001), b = 'b'.repeat(1002), c = 'c'.repeat(1003);
const cases = [
  { id: 'paste-marker-character-threshold-1000-and-1001', steps: [set(''), paste('a'.repeat(1000)), paste('b'.repeat(1001)), left, right] },
  { id: 'paste-marker-line-threshold-10-and-11-lines', steps: [set(''), paste('\n'.repeat(9)), paste('\n'.repeat(10)), left, right] },
  { id: 'paste-marker-threshold-after-path-tab-and-csi-preparation', steps: [set('x'), paste('/' + 'a'.repeat(998)), set('x'), paste('/' + 'a'.repeat(999)), set(''), paste('\t'.repeat(251)), set(''), paste('\x1b[106;5u'.repeat(10))] },
  { id: 'paste-marker-atomic-movement-backspace-and-forward-delete', steps: [set('pre'), paste('\u{1f642}'.repeat(513)), input('Z'), left, left, right, back, paste(c), home, right, right, right, del] },
  { id: 'paste-marker-forward-delete-retains-registry-and-leading-zero-alias', steps: [set(''), paste(a), home, del, insert('[paste #1]'), left, right, home, del, insert('[paste #01]'), home, right, back] },
  { id: 'paste-marker-backspace-renumbers-registry-and-text', steps: [set(''), paste(a), paste(b), paste(c), home, right, back, right, del, end, back, paste('d'.repeat(1004))] },
  { id: 'paste-marker-set-text-and-clear-reset-registry-and-counter', steps: [set(''), paste(a), insert('[paste #1 +9 lines]'), set('[paste #1 1001 chars][paste #1 +9 lines]'), paste(b), set(''), paste(c)] },
  { id: 'paste-marker-only-registered-ids-are-atomic', steps: [set(''), insert('[paste #1]'), home, right, end, paste(a), home, right, insert('!'), left, back] },
  { id: 'paste-marker-expansion-is-ordered-and-one-pass-per-id', steps: [set(''), paste('[paste #2]' + 'a'.repeat(991)), paste('b'.repeat(1001)), set(''), paste('[paste #1]' + 'a'.repeat(991)), paste('[paste #1]' + 'b'.repeat(991))] },
  { id: 'paste-marker-renumber-preserves-source-undefined-suffix-quirk', steps: [set(''), paste(a), paste(b), insert('[paste #99][paste #099 +22 lines][paste #2][paste #01]'), home, right, back, end, left, back] },
  { id: 'paste-marker-two-digit-registry-renumber', steps: [set(''), ...Array.from({ length: 10 }, (_, index) => paste(String.fromCharCode(97 + index) + '\n'.repeat(10))), home, right, back, end, left, del] },
  { id: 'paste-marker-multiline-cjk-content-and-logical-seam', steps: [set('a\nb'), home, paste('/' + '\u4e2d'.repeat(1000)), home, right, back] },
  { id: 'paste-marker-expanded-replacement-content-is-literal', steps: [set(''), paste('$&$1$$'.repeat(170)), insert('[paste #1]'), left, right] }
];
const output = path.join(import.meta.dirname, 'paste-source-inputs.json');
fs.writeFileSync(output, JSON.stringify({ schemaVersion: 1, upstreamCommit: 'd86654abb8862e201933517d6f1fce9f88dd117f', scope: 'New marker lifecycle and public getExpandedText schedules only; original nine schedules reused unchanged', cases }, null, 2) + '\n', { flag: 'wx' });
console.log(JSON.stringify({ output, cases: cases.length, checkpoints: cases.reduce((sum, test) => sum + test.steps.length, 0), sha256: crypto.createHash('sha256').update(fs.readFileSync(output)).digest('hex') }));
