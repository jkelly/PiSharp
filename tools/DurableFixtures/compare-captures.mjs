// Compares two captures, file by file, with only the fields the manifests list under "varies" normalized: every sessionId string becomes
// "<session>", every durationMs number "<duration>", and the seconds of "Wall time <seconds> seconds" "<seconds>"; and code mode's own
// clock and seed: in an object with a numeric `seed` (pi.codemode.run's start) that seed and its startedAt and deadline, and the `at` of an
// object of only id, at, issuedAfter and global (a pi.codemode.journal entry). JSON files compare as
// parsed values (key order included), JSONL files line by line the same way, other text byte for byte; session.sqlite files are skipped,
// since their records are compared through records.json, opened.json and resumed.json. capture-all.mjs --check uses it to confirm that a
// capture repeats; run on a v1.1.0 and a v1.2.0 fixture set it lists what changed between the releases.
// Usage: node tools/DurableFixtures/compare-captures.mjs <left directory or file> <right directory or file> [--summary]
// Exits 1 when they differ. --summary prints one line per file (same, differs, left only, right only) instead of the first differences.
import { readdir, readFile, stat } from 'node:fs/promises';
import { basename, join } from 'node:path';
import { pathToFileURL } from 'node:url';

const JOURNAL_ENTRY_KEYS = new Set(['id', 'at', 'issuedAfter', 'global']);

export function normalize(value, key) {
  if (Array.isArray(value)) return value.map((item) => normalize(item));
  if (value !== null && typeof value === 'object') {
    const out = {};
    for (const [k, v] of Object.entries(value)) out[k] = normalize(v, k);
    // Code mode's own clock and seed (pi.codemode.run's start, pi.codemode.journal's entries): Date.now() and crypto random.
    if (typeof value.seed === 'number') {
      out.seed = '<seed>';
      for (const clock of ['startedAt', 'deadline']) if (typeof value[clock] === 'number') out[clock] = '<clock>';
    }
    if (typeof value.id === 'number' && typeof value.at === 'number' && Object.keys(value).every((k) => JOURNAL_ENTRY_KEYS.has(k))) out.at = '<clock>';
    return out;
  }
  if (typeof value === 'string' && key === 'sessionId') return '<session>';
  if (typeof value === 'number' && key === 'durationMs') return '<duration>';
  if (typeof value === 'string') return value.replace(/Wall time [\d.]+ seconds/g, 'Wall time <seconds> seconds');
  return value;
}

/** The comparable text of a file: normalized JSON, normalized JSON lines, or the text itself. */
export async function comparable(path) {
  const text = await readFile(path, 'utf8');
  if (path.endsWith('.jsonl')) {
    return text.split('\n').map((line) => (line === '' ? '' : JSON.stringify(normalize(JSON.parse(line))))).join('\n');
  }
  if (path.endsWith('.json')) return JSON.stringify(normalize(JSON.parse(text)), null, 1);
  return text;
}

async function files(root, prefix = '') {
  const info = await stat(root);
  if (!info.isDirectory()) return [basename(root)];
  const out = [];
  for (const entry of (await readdir(root, { withFileTypes: true })).sort((a, b) => (a.name < b.name ? -1 : 1))) {
    const name = prefix === '' ? entry.name : `${prefix}/${entry.name}`;
    if (entry.isDirectory()) out.push(...(await files(join(root, entry.name), name)));
    else out.push(name);
  }
  return out;
}

/** Compares two directories (or two files); returns [{ path, status, at?, left?, right? }] for every file. */
export async function compare(left, right) {
  const single = !(await stat(left)).isDirectory();
  const leftFiles = single ? [''] : await files(left);
  const rightFiles = single ? [''] : await files(right);
  const results = [];
  for (const path of [...new Set([...leftFiles, ...rightFiles])].sort()) {
    if (path.endsWith('.sqlite')) continue;
    if (!leftFiles.includes(path)) { results.push({ path, status: 'right only' }); continue; }
    if (!rightFiles.includes(path)) { results.push({ path, status: 'left only' }); continue; }
    const a = await comparable(single ? left : join(left, path));
    const b = await comparable(single ? right : join(right, path));
    if (a === b) { results.push({ path, status: 'same' }); continue; }
    let at = 0;
    while (at < a.length && a[at] === b[at]) at++;
    results.push({ path, status: 'differs', at, left: a.slice(Math.max(0, at - 160), at + 240), right: b.slice(Math.max(0, at - 160), at + 240) });
  }
  return results;
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  const [left, right] = process.argv.slice(2).filter((arg) => !arg.startsWith('--'));
  if (!left || !right) throw new Error('usage: compare-captures.mjs <left> <right> [--summary]');
  const results = await compare(left, right);
  const differing = results.filter((result) => result.status !== 'same');
  if (process.argv.includes('--summary')) for (const result of results) console.log(`${result.status.padEnd(10)} ${result.path}`);
  else {
    for (const result of differing) {
      console.log(`${result.status}: ${result.path}`);
      if (result.status === 'differs') console.log(`  left  ...${result.left}\n  right ...${result.right}`);
    }
  }
  console.log(`${results.length - differing.length} of ${results.length} files compare equal`);
  process.exit(differing.length === 0 ? 0 : 1);
}
