// Captures tests/PiSharp.Durable.Tests/Fixtures/upstream-sessions (Pi v1.1.0) or Fixtures/v1.2.0/upstream-sessions (Pi v1.2.0): durable
// sessions that a pinned upstream pi-durable (pin.mjs) itself writes, on its node:sqlite SQLite storage (`session.sqlite`) and its JSONL
// storage (a directory), with pi-ai's faux provider as the model; plus an ops corpus from Chord's tracker. PiSharp's compat.* cases
// (tests/PiSharp.Durable.Tests/Compat/CompatCases.cs) open, read and resume these files offline.
//
// For each scenario and backend it writes <out>/<scenario>/<backend>/ (the session as upstream closed it), and <out>/<scenario>/records.json:
// what upstream's storage reads back from each file (every conversation, each conversation's visible entries, every task and submission,
// and every current document of the session, conversation and task scopes, with its version and value). A resumable scenario also has
// resumed.json: what upstream reads after reopening a copy of the session, resuming it with the scripted model, and closing it again.
// opened.json holds what upstream reads after a Harness opened a copy and closed it without resuming: opening reconciles surviving running
// tasks to pending, as PiSharp's must. A scenario marked `events` also has events.json: per backend, the agent events (watchEvents) of its
// root conversation, the snapshot first, from the run and from the resume. A scenario whose run returns a value (what calls returned or
// rejected with) has observed.json, per backend. From v1.2.0, upgrade-from-v1.1.0/<scenario> holds what this version reads after opening
// (opened.json) and resuming (resumed.json) copies of the repository's v1.1.0 sessions.
// Records are written raw. The Harness clock and the faux timestamps are fixed, so a capture repeats except for the fields manifest.json
// lists under "varies", which compat.* normalizes on both sides (compare-captures.mjs does the same to check that a capture repeats).
//
// Usage, from the repository root (Node 22.5 or later for node:sqlite; the Pi checkout needs node_modules, see capture-all.mjs):
//   node --no-warnings --conditions=source --experimental-strip-types tools/DurableFixtures/capture.mjs <pi checkout> --commit <sha> [out directory]
// The out directory defaults to the commit's fixture set (pin.mjs); scenario directories in it are replaced.
// Nothing is written to the Pi checkout: resumed copies live in the OS temp directory and are removed.
import { cp, mkdir, mkdtemp, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { createChat, dumpStorage, openBackend, openOnly, recordEvents, scenarios, setPi, T, NOW } from './scenarios.mjs';
import { opsCorpus } from './ops-corpus.mjs';
import { fixtureDirectory, pinnedArguments } from './pin.mjs';

const pin = pinnedArguments(process.argv.slice(2), 'capture.mjs <pi checkout> --commit <sha> [out directory]');
const { pi, commit: PI_COMMIT, version } = pin;
const out = resolve(pin.rest[0] ?? join(fixtureDirectory(version), 'upstream-sessions'));
await setPi(pi, pin);
const all = version === '1.1.0' ? scenarios : [...scenarios, ...(await import('./scenarios-v1.2.0.mjs')).scenarios];

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
for (const scenario of all) {
  const root = join(out, scenario.name);
  await rm(root, { recursive: true, force: true });
  const records = {};
  const opened = {};
  const resumed = {};
  const events = {};
  const observed = {};
  for (const backend of BACKENDS) {
    const directory = join(root, backend);
    await mkdir(directory, { recursive: true });
    const sink = scenario.events ? [] : undefined;
    recordEvents(sink);
    const seen = await scenario.run(createChat, () => openBackend(backend, directory, context), context);
    recordEvents(undefined);
    if (seen !== undefined) observed[backend] = { run: seen };
    if (sink !== undefined) events[backend] = { run: sink };
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
    if (scenario.resume) {
      const resumeSink = scenario.events ? [] : undefined;
      let seenOnResume;
      recordEvents(resumeSink);
      resumed[backend] = await inCopy(async (openStorage) => { seenOnResume = await scenario.resume(createChat, openStorage, context); });
      recordEvents(undefined);
      if (resumeSink !== undefined) events[backend].resume = resumeSink;
      if (seenOnResume !== undefined) (observed[backend] ??= {}).resume = seenOnResume;
    }
  }
  await writeFile(join(root, 'records.json'), json(records));
  await writeFile(join(root, 'opened.json'), json(opened));
  if (scenario.resume) await writeFile(join(root, 'resumed.json'), json(resumed));
  if (scenario.events) await writeFile(join(root, 'events.json'), json(events));
  if (Object.keys(observed).length > 0) await writeFile(join(root, 'observed.json'), json(observed));
  inventory.push({
    name: scenario.name,
    description: scenario.description,
    resumable: scenario.resume !== undefined,
    ...(scenario.since === undefined ? {} : { since: scenario.since, areas: scenario.areas }),
  });
}

// Upgrade coverage: what this version reads after opening, and after resuming, copies of the v1.1.0 sessions in the repository
// (Fixtures/upstream-sessions), as a host that upgraded Pi would; v1 pi.tool tasks migrate to v2 on open.
const upgrades = [];
if (version !== '1.1.0') {
  const previous = join(fixtureDirectory('1.1.0'), 'upstream-sessions');
  const upgradeRoot = join(out, 'upgrade-from-v1.1.0');
  await rm(upgradeRoot, { recursive: true, force: true });
  for (const scenario of scenarios) {
    const opened = {};
    const resumed = {};
    for (const backend of BACKENDS) {
      const inCopy = async (change) => {
        const copy = await mkdtemp(join(tmpdir(), `pisharp-capture-upgrade-${scenario.name}-`));
        try {
          await cp(join(previous, scenario.name, backend), copy, { recursive: true });
          await change(() => openBackend(backend, copy, context));
          return await dumpStorage(await openBackend(backend, copy, context), context);
        } finally {
          await rm(copy, { recursive: true, force: true });
        }
      };
      opened[backend] = await inCopy((openStorage) => openOnly(openStorage, context));
      if (scenario.resume) resumed[backend] = await inCopy((openStorage) => scenario.resume(createChat, openStorage, context));
    }
    await mkdir(join(upgradeRoot, scenario.name), { recursive: true });
    await writeFile(join(upgradeRoot, scenario.name, 'opened.json'), json(opened));
    if (scenario.resume) await writeFile(join(upgradeRoot, scenario.name, 'resumed.json'), json(resumed));
    upgrades.push({ name: scenario.name, from: `tests/PiSharp.Durable.Tests/Fixtures/upstream-sessions/${scenario.name}`, resumable: scenario.resume !== undefined });
  }
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

const varies = [
  { field: 'sessionId in pi.provider documents', why: 'pi-ai uuidv7() on first use: random bits and Date.now(); upstream takes no seed' },
  { field: 'durationMs of tool result messages', why: 'performance.now() around the tool call' },
  { field: 'SQLite page layout and file bytes', why: 'SQLite; the records, not the pages, are the evidence' },
];
if (version !== '1.1.0') {
  varies.push(
    { field: 'durationMs anywhere: nested call results (pi.tool.nested documents, observed results, tool events)', why: 'performance.now() around each call' },
    { field: 'the seconds in "Wall time <seconds> seconds" of codemode results', why: 'the script\'s wall time, measured by upstream' },
    { field: 'seed, startedAt and deadline of pi.codemode.run\'s start, and `at` of pi.codemode.journal entries',
      why: 'code mode\'s own VM clock (Date.now()) and seed (crypto.getRandomValues), which it journals so a replay sees the same ones' },
  );
}
await writeFile(join(out, 'manifest.json'), json({
  description: 'Durable sessions written by the pinned upstream pi-durable with pi-ai\'s faux provider, and a Chord tracker ops corpus. ' +
    'Captured upstream evidence for PiSharp\'s compat.* cases; regenerate with the command below, never edit by hand.',
  upstream: version === '1.1.0'
    ? { package: '@earendil-works/pi-durable@1.1.0 (sources)', release: 'Pi v1.1.0', commit: PI_COMMIT, piAi: '@earendil-works/pi-ai 1.1.0 (published build)' }
    : {
      package: `@earendil-works/pi-durable@${version} (sources)`, release: `Pi v${version}`, commit: PI_COMMIT,
      piAi: `@earendil-works/pi-ai ${version} (published build)`, chord: `@earendil-works/chord ${version} (sources)`,
      codemode: `@earendil-works/pi-codemode ${version} (sources)`,
    },
  node: process.version,
  sqlite: new DatabaseSync(':memory:').prepare('select sqlite_version() as v').get().v,
  command: `node --no-warnings --conditions=source --experimental-strip-types tools/DurableFixtures/capture.mjs <pi checkout at the commit> --commit ${PI_COMMIT}`,
  clock: { harnessNow: NOW, fauxTimestamp: T },
  varies,
  normalized: version === '1.1.0'
    ? 'compat.* replaces every sessionId string with "<session>" and every durationMs number with "<duration>", on both sides; nothing else'
    : 'replace every sessionId string with "<session>", every durationMs number with "<duration>", the seconds of "Wall time <seconds> seconds" ' +
      'with "<seconds>", and code mode\'s clock and seed fields as listed, on both sides; nothing else (normalize() in ' +
      'tools/DurableFixtures/compare-captures.mjs)',
  scenarios: inventory,
  ...(upgrades.length === 0 ? {} : {
    upgrades: { directory: 'upgrade-from-v1.1.0', what: 'opened.json and resumed.json (as for a scenario) of copies of the v1.1.0 sessions', scenarios: upgrades },
  }),
  files,
}));
console.log(`captured ${inventory.length} scenarios into ${out}`);
