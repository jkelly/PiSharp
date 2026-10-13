// Captures tests/PiSharp.Durable.Tests/Fixtures/upstream-sessions: durable sessions that the pinned pi-durable (Pi v1.1.0,
// abe508e1b89912adde45528136c3221eb69acdd7) itself writes, on its node:sqlite SQLite storage (`session.sqlite`) and its JSONL storage
// (a directory), with pi-ai's faux provider as the model; plus an ops corpus from Chord's tracker. PiSharp's compat.* cases
// (tests/PiSharp.Durable.Tests/CompatCases.cs) open, read and resume these files offline.
//
// For each scenario and backend it writes <out>/<scenario>/<backend>/ (the session as upstream closed it), and <out>/<scenario>/records.json:
// what upstream's storage reads back from each file (every conversation, each conversation's visible entries, every task and submission,
// and every current document of the session, conversation and task scopes, with its version and value). A resumable scenario also has
// resumed.json: what upstream reads after reopening a copy of the session, resuming it with the scripted model, and closing it again.
// opened.json holds what upstream reads after a Harness opened a copy and closed it without resuming: opening reconciles surviving running
// tasks to pending, as PiSharp's must.
// Records are written raw. The Harness clock and the faux timestamps are fixed, so a capture repeats except for the fields manifest.json
// lists under "varies", which compat.* normalizes on both sides.
//
// Usage, from the repository root (Node 22.5 or later for node:sqlite; the Pi checkout needs node_modules installed):
//   node --no-warnings --conditions=source --experimental-strip-types tools/DurableFixtures/capture.mjs <pi checkout> [out directory]
// The out directory defaults to tests/PiSharp.Durable.Tests/Fixtures/upstream-sessions; scenario directories in it are replaced.
// Nothing is written to the Pi checkout: resumed copies live in the OS temp directory and are removed.
import { cp, mkdir, mkdtemp, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { DatabaseSync } from 'node:sqlite';
import { createChat, dumpStorage, openBackend, openOnly, PI_COMMIT, scenarios, setPi, T, NOW } from './scenarios.mjs';
import { opsCorpus } from './ops-corpus.mjs';

const pi = process.argv[2];
if (!pi) throw new Error('usage: capture.mjs <pi checkout> [out directory]');
const repository = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const out = resolve(process.argv[3] ?? join(repository, 'tests', 'PiSharp.Durable.Tests', 'Fixtures', 'upstream-sessions'));
await setPi(pi);

const head = execFileSync('git', ['-C', pi, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
if (head !== PI_COMMIT) throw new Error(`The Pi checkout is at ${head}, not the pinned ${PI_COMMIT}`);
const dirty = execFileSync('git', ['-C', pi, 'status', '--porcelain'], { encoding: 'utf8' }).trim();
if (dirty !== '') throw new Error(`The Pi checkout has local changes:\n${dirty}`);

const context = {};
const BACKENDS = ['sqlite', 'jsonl'];

/** Leaves the SQLite file self-contained: folds any WAL left behind into it (upstream's close already checkpoints). */
async function settleSqlite(directory) {
  const path = join(directory, 'session.sqlite');
  for (const suffix of ['-wal', '-shm']) {
    const info = await stat(path + suffix).catch(() => undefined);
    if (info === undefined) continue;
    if (suffix === '-wal' && info.size > 0) {
      const database = new DatabaseSync(path);
      database.exec('PRAGMA wal_checkpoint(TRUNCATE)');
      database.close();
    }
    await rm(path + suffix, { force: true });
  }
}

const json = (value) => `${JSON.stringify(value, null, 1)}\n`;
const inventory = [];
await mkdir(out, { recursive: true });
// Exact bytes: JSONL lines are compared byte for byte, and session.sqlite is binary.
await writeFile(join(out, '.gitattributes'), '* -text\n');
for (const scenario of scenarios) {
  const root = join(out, scenario.name);
  await rm(root, { recursive: true, force: true });
  const records = {};
  const opened = {};
  const resumed = {};
  for (const backend of BACKENDS) {
    const directory = join(root, backend);
    await mkdir(directory, { recursive: true });
    await scenario.run(createChat, () => openBackend(backend, directory, context), context);
    if (backend === 'sqlite') await settleSqlite(directory);
    records[backend] = await dumpStorage(await openBackend(backend, directory, context), context);
    if (backend === 'sqlite') await settleSqlite(directory);
    /** Runs `change` on a temporary copy of the session and reads the copy back. */
    const inCopy = async (change) => {
      const copy = await mkdtemp(join(tmpdir(), `pisharp-capture-${scenario.name}-`));
      try {
        await cp(directory, copy, { recursive: true });
        await change(() => openBackend(backend, copy, context));
        return await dumpStorage(await openBackend(backend, copy, context), context);
      } finally {
        await rm(copy, { recursive: true, force: true });
      }
    };
    opened[backend] = await inCopy((openStorage) => openOnly(openStorage, context));
    if (scenario.resume) resumed[backend] = await inCopy((openStorage) => scenario.resume(createChat, openStorage, context));
  }
  await writeFile(join(root, 'records.json'), json(records));
  await writeFile(join(root, 'opened.json'), json(opened));
  if (scenario.resume) await writeFile(join(root, 'resumed.json'), json(resumed));
  inventory.push({ name: scenario.name, description: scenario.description, resumable: scenario.resume !== undefined });
}

await writeFile(join(out, 'ops-corpus.json'), json(await opsCorpus(pi)));

const files = [];
async function walk(directory, prefix) {
  for (const entry of (await readdir(directory, { withFileTypes: true })).sort((a, b) => (a.name < b.name ? -1 : 1))) {
    const path = join(directory, entry.name);
    const name = prefix === '' ? entry.name : `${prefix}/${entry.name}`;
    if (entry.isDirectory()) await walk(path, name);
    else if (name !== 'manifest.json' && name !== '.gitattributes') files.push({ path: name, bytes: (await stat(path)).size });
  }
}
await walk(out, '');

await writeFile(join(out, 'manifest.json'), json({
  description: 'Durable sessions written by the pinned upstream pi-durable with pi-ai\'s faux provider, and a Chord tracker ops corpus. ' +
    'Captured upstream evidence for PiSharp\'s compat.* cases; regenerate with the command below, never edit by hand.',
  upstream: { package: '@earendil-works/pi-durable@1.1.0 (sources)', release: 'Pi v1.1.0', commit: PI_COMMIT, piAi: '@earendil-works/pi-ai 1.1.0 (published build)' },
  node: process.version,
  sqlite: new DatabaseSync(':memory:').prepare('select sqlite_version() as v').get().v,
  command: 'node --no-warnings --conditions=source --experimental-strip-types tools/DurableFixtures/capture.mjs <pi checkout at the commit>',
  clock: { harnessNow: NOW, fauxTimestamp: T },
  varies: [
    { field: 'sessionId in pi.provider documents', why: 'pi-ai uuidv7() on first use: random bits and Date.now(); upstream takes no seed' },
    { field: 'durationMs of tool result messages', why: 'performance.now() around the tool call' },
    { field: 'SQLite page layout and file bytes', why: 'SQLite; the records, not the pages, are the evidence' },
  ],
  normalized: 'compat.* replaces every sessionId string with "<session>" and every durationMs number with "<duration>", on both sides; nothing else',
  scenarios: inventory,
  files,
}));
console.log(`captured ${inventory.length} scenarios into ${out}`);
