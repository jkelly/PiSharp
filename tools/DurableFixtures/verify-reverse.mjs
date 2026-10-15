// The reverse direction of the compat.* cases: the pinned upstream pi-durable reads sessions PiSharp wrote. Run by
// compat.reverse.upstream-reads-pisharp-sessions (tests/PiSharp.Durable.Tests/Compat/CompatOpsCases.cs), which first has PiSharp write every
// scenario of scenarios.mjs into <pisharp sessions>/<scenario>/<backend>, with PiSharp's own reading in <scenario>/records.json, and its
// tracker operations for the ops corpus in ops.json. For each session, upstream's storage must open it and read records equal to PiSharp's
// reading and to upstream's captured records, key order included; a resumable session must resume under upstream to upstream's captured
// resumed records; and PiSharp's operations must replay under upstream's applyImmutable to the corpus values. Exits non-zero on a difference.
// Usage: node --no-warnings --conditions=source --experimental-strip-types verify-reverse.mjs <pi checkout> <pisharp sessions> <fixtures>
//          [--commit <sha>]
// Without --commit the checkout is taken to be Pi v1.1.0 (pin.mjs) and not checked, as the C# reverse case runs it; with --commit it must be
// at that pinned commit with a clean tree, and the scenarios' tool results take that version's shape. Either way only the scenarios of
// scenarios.mjs are verified, the ones PiSharp writes.
import { cp, mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createChat, dumpStorage, openBackend, scenarios, setPi } from './scenarios.mjs';
import { PINS, pinnedArguments } from './pin.mjs';

const usage = 'verify-reverse.mjs <pi checkout> <pisharp sessions> <fixtures> [--commit <sha>]';
const pin = process.argv.includes('--commit')
  ? pinnedArguments(process.argv.slice(2), usage)
  : { pi: process.argv[2], commit: PINS['1.1.0'], version: '1.1.0', output: false, rest: process.argv.slice(3) };
const pi = pin.pi;
const [sessions, fixtures] = pin.rest;
if (!pi || !sessions || !fixtures) throw new Error(`usage: ${usage}`);
await setPi(pi, pin);
const context = {};

function normalize(value, key) {
  if (Array.isArray(value)) return value.map((item) => normalize(item));
  if (value !== null && typeof value === 'object') {
    const out = {};
    for (const [k, v] of Object.entries(value)) out[k] = normalize(v, k);
    return out;
  }
  if (typeof value === 'string' && key === 'sessionId') return '<session>';
  if (typeof value === 'number' && key === 'durationMs') return '<duration>';
  return value;
}

const failures = [];
let checks = 0;
function same(expected, actual, what) {
  checks++;
  const left = JSON.stringify(normalize(expected));
  const right = JSON.stringify(normalize(actual));
  if (left === right) return;
  let at = 0;
  while (at < left.length && left[at] === right[at]) at++;
  failures.push(`${what}: differs at character ${at}\n  expected ...${left.slice(Math.max(0, at - 120), at + 200)}\n  actual   ...${right.slice(Math.max(0, at - 120), at + 200)}`);
}
const json = async (path) => JSON.parse(await readFile(path, 'utf8'));

for (const scenario of scenarios) {
  const pisharp = await json(join(sessions, scenario.name, 'records.json'));
  const captured = await json(join(fixtures, scenario.name, 'records.json'));
  const resumed = scenario.resume ? await json(join(fixtures, scenario.name, 'resumed.json')) : undefined;
  for (const backend of ['sqlite', 'jsonl']) {
    const directory = join(sessions, scenario.name, backend);
    const read = await dumpStorage(await openBackend(backend, directory, context), context);
    same(pisharp[backend], read, `${scenario.name}/${backend}: upstream reads PiSharp's records as PiSharp does`);
    same(captured[backend], read, `${scenario.name}/${backend}: upstream reads PiSharp's records as its own captured ones`);
    if (scenario.resume) {
      const copy = await mkdtemp(join(tmpdir(), `pisharp-reverse-${scenario.name}-`));
      try {
        await cp(directory, copy, { recursive: true });
        await scenario.resume(createChat, () => openBackend(backend, copy, context), context);
        same(resumed[backend], await dumpStorage(await openBackend(backend, copy, context), context),
          `${scenario.name}/${backend}: upstream resumes PiSharp's session to its own resumed records`);
      } finally {
        await rm(copy, { recursive: true, force: true });
      }
    }
  }
}

const { applyImmutable } = await import(pathToFileURL(join(pi, 'packages/chord/src/delta/index.ts')).href);
const corpus = await json(join(fixtures, 'ops-corpus.json'));
const ops = await json(join(sessions, 'ops.json'));
corpus.documents.forEach((document, d) => {
  let current = document.base;
  document.batches.forEach((batch, b) => {
    same(batch.value, applyImmutable(current, ops[d][b]), `ops document ${d} batch ${b}: PiSharp's operations under upstream's applyImmutable`);
    current = batch.value;
  });
});

for (const failure of failures) console.log(failure);
console.log(`verified ${checks - failures.length} of ${checks} checks`);
process.exit(failures.length === 0 ? 0 : 1);
