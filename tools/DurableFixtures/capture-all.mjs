// Runs every durable capture against one pinned Pi commit and writes the commit's fixture set: capture.mjs (the upstream sessions and the
// ops corpus), each tests/PiSharp.Durable.Tests/Fixtures/capture-*.mjs (<name>.upstream.json), and upstream example 19 in both modes
// (examples-19-json-<mode>.upstream.jsonl, run as its header says, from packages/durable, with the faux model). For a set other than
// v1.1.0 it also writes manifest.json (commit, Node, commands, varying fields, the repeat check) and index.json (each fixture, its
// scenarios, whether each matches the v1.1.0 capture, and the work-list areas that adopt it).
//
// Usage, from the repository root:
//   node --no-warnings tools/DurableFixtures/capture-all.mjs <pi checkout> --commit <sha> [--out <directory>] [--check]
// --check captures everything a second time into a temporary directory and compares the two with compare-captures.mjs's normalization
// (and, for example 19, its streamed partial messages left out); it fails when anything else differs.
//
// The Pi checkout must be at the commit with a clean tree and resolve its packages: pi-durable's sources import @earendil-works/chord and
// @earendil-works/pi-codemode (the checkout's own packages), @earendil-works/pi-ai (that release's published build), typebox, diff and
// quickjs-wasi. A separate worktree with a linked node_modules keeps the upstream checkout itself untouched, for example:
//   git -C <pi repo> worktree add --detach <dir> <commit>
//   mkdir <dir>/node_modules/@earendil-works
//   link <dir>/node_modules/@earendil-works/{chord,pi-codemode} -> <dir>/packages/{chord,codemode}
//   link <dir>/node_modules/@earendil-works/pi-ai, typebox, diff, quickjs-wasi -> an `npm install` of those packages at the release
// (directory junctions on Windows). node_modules is ignored by Pi's .gitignore, so the tree stays clean. API keys are removed from the
// environment of every capture, so example 19 uses its faux model instead of OpenAI.
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, relative, resolve } from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { compare, comparable, normalize } from './compare-captures.mjs';
import { fixtureDirectory, PINS, pinnedArguments, repository } from './pin.mjs';

const argv = process.argv.slice(2);
const flag = (name) => {
  const index = argv.indexOf(name);
  if (index < 0) return undefined;
  const [, value] = argv.splice(index, 2);
  return value;
};
const outOption = flag('--out');
const check = argv.includes('--check');
const pin = pinnedArguments(argv.filter((arg) => arg !== '--check'), 'capture-all.mjs <pi checkout> --commit <sha> [--out <directory>] [--check]');
const out = resolve(outOption ?? fixtureDirectory(pin.version));
const fixtures = join(repository, 'tests', 'PiSharp.Durable.Tests', 'Fixtures');
const posix = (path) => relative(repository, path).split('\\').join('/');

/** The environment without API keys: every capture must run offline on the faux provider. */
const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !/_API_KEY$/i.test(key)));
const SOURCE = ['--no-warnings', '--conditions=source', '--experimental-strip-types'];
/** Node flags per capture script, as each script's usage line gives them. */
const FLAGS = { 'capture-piai-utils.mjs': ['--no-warnings', '--experimental-strip-types'], 'capture-piai-validation.mjs': ['--no-warnings'] };
const scripts = readdirSync(fixtures).filter((name) => /^capture-.+\.mjs$/.test(name)).sort();

function node(args, options = {}) {
  return execFileSync(process.execPath, args, { env, maxBuffer: 1 << 30, encoding: 'utf8', ...options });
}

/** Captures everything into `directory`; returns the command that made each file. */
function captureAll(directory) {
  mkdirSync(directory, { recursive: true });
  const commands = {};
  console.log(`capturing upstream sessions (${pin.version})`);
  node([...SOURCE, join(repository, 'tools', 'DurableFixtures', 'capture.mjs'), pin.pi, '--commit', pin.commit, join(directory, 'upstream-sessions')],
    { stdio: ['ignore', 'pipe', 'inherit'] });
  commands['upstream-sessions/'] = `node ${SOURCE.join(' ')} tools/DurableFixtures/capture.mjs <pi checkout> --commit ${pin.commit} <out>/upstream-sessions`;
  for (const script of scripts) {
    const name = `${script.slice('capture-'.length, -'.mjs'.length)}.upstream.json`;
    const flags = FLAGS[script] ?? SOURCE;
    console.log(`capturing ${name}`);
    writeFileSync(join(directory, name), node([...flags, join(fixtures, script), pin.pi, '--commit', pin.commit]));
    commands[name] = `node ${flags.join(' ')} tests/PiSharp.Durable.Tests/Fixtures/${script} <pi checkout> --commit ${pin.commit} > <out>/${name}`;
  }
  for (const mode of ['events', 'ops']) {
    const name = `examples-19-json-${mode}.upstream.jsonl`;
    console.log(`capturing ${name}`);
    const run = spawnSync(process.execPath, ['--conditions=source', '--experimental-strip-types', 'test/examples/19-json.ts', `--${mode}`],
      { cwd: join(pin.pi, 'packages', 'durable'), env, encoding: 'utf8', maxBuffer: 1 << 30 });
    if (run.status !== 0) throw new Error(`example 19 --${mode} failed: ${run.stderr}`);
    writeFileSync(join(directory, name), run.stdout);
    // It leaves its SQLite database in the temp directory and names it on stderr.
    const left = /sqlite storage: (.+)/.exec(run.stderr)?.[1]?.trim();
    if (left !== undefined) for (const suffix of ['', '-wal', '-shm']) rmSync(left + suffix, { force: true });
    commands[name] = `cd <pi checkout>/packages/durable && node --conditions=source --experimental-strip-types test/examples/19-json.ts --${mode} > <out>/${name} (no OPENAI_API_KEY; the temporary SQLite file it names on stderr is removed)`;
  }
  return commands;
}

/** Example 19's lines with times, durations, the faux API name and session ID as placeholders, and its streamed partials left out. */
function exampleLines(path) {
  return readFileSync(path, 'utf8').split('\n').filter((line) => line !== '').map((line) => JSON.parse(line)).map((value) => {
    const text = JSON.stringify(normalize(value)).replace(/"(timestamp|at)":\d+/g, '"$1":"<time>"').replace(/"api":"faux:[^"]*"/g, '"api":"faux"');
    return JSON.parse(text);
  }).filter((value) => {
    if (value.type === 'message_update') return false;
    if (value.type === 'message_start' && value.message?.role === 'assistant') return false;
    if (Array.isArray(value.ops) && value.ops.every((op) => Array.isArray(op[1]) && op[1].slice(0, 3).join('/') === 'docs/pi.live/generation')) return false;
    return true;
  }).map((value) => JSON.stringify(value)).join('\n');
}

// ─── index.json ─────────────────────────────────────────────────────────
/** Which areas adopt each fixture's scenarios: per fixture a default, per scenario an override; notes say why. */
function areasFor(fixture, scenario) {
  const table = {
    'chat-turns.upstream.json': { default: ['a'], followUp: ['i'] },
    'tool-rounds.upstream.json': { default: ['a'] },
    'inbox.upstream.json': { default: ['i'] },
    'compaction.upstream.json': { default: ['j'] },
    'views.upstream.json': { default: ['a'] },
    'task-graph.upstream.json': { default: ['c'] },
    'tools.upstream.json': { default: ['a'], read: ['e'], images: ['e'] },
    'env-line-scan.upstream.json': { default: ['m'] },
    'piai-utils.upstream.json': { default: ['m'] },
    'piai-validation.upstream.json': { default: ['m'] },
    'examples-19-json-events.upstream.jsonl': { default: ['h'] },
    'examples-19-json-ops.upstream.jsonl': { default: ['h'] },
    'upstream-sessions': { default: ['a'] },
    'upstream-sessions/ops-corpus.json': { default: ['f'] },
  }[fixture];
  return table[scenario] ?? table.default;
}
const READERS = {
  'chat-turns.upstream.json': ['tests/PiSharp.Durable.Tests/Harness/HarnessChatDifferentialCases.cs'],
  'tool-rounds.upstream.json': ['tests/PiSharp.Durable.Tests/Harness/HarnessToolsDifferentialCases.cs'],
  'inbox.upstream.json': ['tests/PiSharp.Durable.Tests/Harness/HarnessInboxDifferentialCases.cs'],
  'compaction.upstream.json': ['tests/PiSharp.Durable.Tests/Harness/HarnessCompactionDifferentialCases.cs'],
  'views.upstream.json': ['tests/PiSharp.Durable.Tests/Harness/HarnessViewsDifferentialCases.cs'],
  'task-graph.upstream.json': ['tests/PiSharp.Durable.Tests/Harness/HarnessTaskGraphDifferentialCases.cs'],
  'tools.upstream.json': ['tests/PiSharp.Durable.Tests/ToolsDifferentialCases.cs', 'tests/PiSharp.Durable.Tests/ToolsReadDifferentialCases.cs'],
  'env-line-scan.upstream.json': ['tests/PiSharp.Durable.Tests/EnvLineScanCases.cs'],
  'piai-utils.upstream.json': ['tests/PiSharp.Durable.Tests/PiAiUtilsCases.cs'],
  'piai-validation.upstream.json': ['tests/PiSharp.Durable.Tests/PiAiValidationCases.cs'],
  'examples-19-json-events.upstream.jsonl': ['tests/PiSharp.Durable.Tests/ExamplesCases.cs'],
  'examples-19-json-ops.upstream.jsonl': ['tests/PiSharp.Durable.Tests/ExamplesCases.cs'],
  'upstream-sessions': ['tests/PiSharp.Durable.Tests/Compat/CompatCases.cs', 'tests/PiSharp.Durable.Tests/Compat/CompatScenarios.cs',
    'tests/PiSharp.Durable.Tests/Compat/CompatOpsCases.cs (compat.reverse: tools/DurableFixtures/verify-reverse.mjs --commit)'],
  'upstream-sessions/ops-corpus.json': ['tests/PiSharp.Durable.Tests/Compat/CompatOpsCases.cs'],
};

const NOTES = {
  'env-line-scan.upstream.json': 'Identical to v1.1.0 but for its source line. The work list gives re-pinning EnvLineScanCases.cs to area m, which left ' +
    'the switch to whoever re-pins the case headers (this change switches no suite).',
  'piai-utils.upstream.json': 'Identical to v1.1.0. The work list gives re-pinning PiAiUtilsCases.cs to area m; switch with the header re-pin.',
  'piai-validation.upstream.json': 'Identical to v1.1.0. The work list gives re-pinning PiAiValidationCases.cs to area m; switch with the header re-pin.',
  'chat-turns.upstream.json': 'followUp differs by the display-only write (#10407): the note is appended at once, before the first answer.',
  'inbox.upstream.json': 'steer differs by the display-only write appended at once, and by pi.tool v2 (version 2, kind "model").',
  'tool-rounds.upstream.json': 'pi.tool v2 (version 2, input and result kind "model"); round also gains pi.agent modelTools.add from the addTools control.',
  'views.upstream.json': 'Tool events carry taskId and the snapshot nestedTools; pi.tool v2.',
  'tools.upstream.json': 'read\'s description mentions images; bash answers a nonzero exit with an error result (exit_code) and structured output ' +
    'instead of throwing; declarations add structuredOutputSchema, callers, deferred; images is new.',
  'compaction.upstream.json': 'The v1.1.0 scenarios are unchanged; backgroundFailure and blockingFailure are new.',
  'examples-19-json-events.upstream.jsonl': 'The snapshot gains nestedTools, tool events taskId; read\'s new description changes the token counts.',
  'examples-19-json-ops.upstream.jsonl': 'read\'s new description changes the token counts.',
};

function size(path) {
  const info = statSync(path);
  if (!info.isDirectory()) return info.size;
  return readdirSync(path).reduce((total, entry) => total + size(join(path, entry)), 0);
}

async function sameAs(left, right) {
  if (!existsSync(right)) return 'new';
  const results = await compare(left, right);
  return results.every((result) => result.status === 'same') ? 'same' : 'differs';
}

async function buildIndex() {
  const baseline = fixtures;
  const entries = [];
  const value = (path) => JSON.parse(readFileSync(path, 'utf8'));
  const comparableValue = (v) => JSON.stringify(normalize(v));
  for (const name of readdirSync(out).filter((file) => /\.upstream\.jsonl?$/.test(file)).sort()) {
    const path = join(out, name);
    const old = join(baseline, name);
    let scenarios;
    if (name.endsWith('.jsonl')) {
      scenarios = [{ name: 'output', areas: areasFor(name, 'output'),
        'v1.1.0': !existsSync(old) ? 'new' : exampleLines(path) === exampleLines(old) ? 'same' : 'differs' }];
    } else {
      const now = value(path);
      const before = existsSync(old) ? value(old) : undefined;
      const keys = Array.isArray(now) ? ['rows'] : Object.keys(now);
      scenarios = keys.map((key) => {
        // The commit the file was captured from, not a case.
        if (key === 'source') return { name: key, areas: [], 'v1.1.0': 'metadata' };
        const current = Array.isArray(now) ? now : now[key];
        const previous = before === undefined ? undefined : Array.isArray(before) ? before : before[key];
        return {
          name: key,
          areas: areasFor(name, key),
          'v1.1.0': previous === undefined ? 'new' : comparableValue(current) === comparableValue(previous) ? 'same' : 'differs',
        };
      });
    }
    entries.push({
      path: posix(path),
      ...(NOTES[name] === undefined ? {} : { note: NOTES[name] }),
      capture: name.startsWith('examples-19') ? `upstream packages/durable/test/examples/19-json.ts (tools/DurableFixtures/capture-all.mjs)`
        : `tests/PiSharp.Durable.Tests/Fixtures/capture-${name.replace(/\.upstream\.json$/, '')}.mjs`,
      'v1.1.0': existsSync(old) ? posix(old) : null,
      readBy: READERS[name] ?? [],
      bytes: size(path),
      scenarios,
    });
  }
  const sessions = join(out, 'upstream-sessions');
  const manifest = value(join(sessions, 'manifest.json'));
  const oldSessions = join(baseline, 'upstream-sessions');
  const scenarioEntries = [];
  for (const scenario of manifest.scenarios) {
    scenarioEntries.push({
      name: scenario.name,
      areas: scenario.areas ?? areasFor('upstream-sessions', scenario.name),
      'v1.1.0': await sameAs(join(sessions, scenario.name), join(oldSessions, scenario.name)),
      resumable: scenario.resumable,
      bytes: size(join(sessions, scenario.name)),
      description: scenario.description,
    });
  }
  entries.push({
    path: posix(sessions),
    note: 'Of the v1.1.0 scenarios, tool-interrupted and subagent differ only by pi.tool v2 (task version 2, input and result kind "model"); ' +
      'the others are identical. The new scenarios add events.json (agent events, snapshot first) and observed.json (what calls returned ' +
      'or rejected with). compat.reverse runs verify-reverse.mjs --commit 06a466935536f8c9bcacb2d5efc0fc021be1bdff against this set.',
    capture: 'tools/DurableFixtures/capture.mjs (scenarios.mjs, scenarios-v1.2.0.mjs)',
    'v1.1.0': posix(oldSessions),
    readBy: READERS['upstream-sessions'],
    bytes: size(sessions),
    scenarios: scenarioEntries,
  });
  if (manifest.upgrades !== undefined) {
    const upgrades = join(sessions, manifest.upgrades.directory);
    entries.push({
      path: posix(upgrades),
      capture: 'tools/DurableFixtures/capture.mjs (the v1.1.0 sessions opened and resumed by this version)',
      'v1.1.0': posix(oldSessions),
      readBy: [],
      bytes: size(upgrades),
      scenarios: manifest.upgrades.scenarios.map((scenario) => ({
        name: scenario.name,
        areas: ['a'],
        'v1.1.0': 'new',
        resumable: scenario.resumable,
        description: `What Pi v${pin.version} reads after opening${scenario.resumable ? ' and after resuming' : ''} a copy of ${scenario.from}: ` +
          'upgrade coverage for CompatCases, which keeps reading the v1.1.0 sessions (v1 pi.tool tasks migrate to v2 on open).',
      })),
    });
  }
  entries.push({
    path: posix(join(sessions, 'ops-corpus.json')),
    capture: 'tools/DurableFixtures/ops-corpus.mjs (through capture.mjs)',
    'v1.1.0': posix(join(oldSessions, 'ops-corpus.json')),
    readBy: READERS['upstream-sessions/ops-corpus.json'],
    bytes: size(join(sessions, 'ops-corpus.json')),
    scenarios: [{ name: 'documents', areas: areasFor('upstream-sessions/ops-corpus.json', 'documents'),
      'v1.1.0': (await comparable(join(sessions, 'ops-corpus.json'))) === (await comparable(join(oldSessions, 'ops-corpus.json'))) ? 'same' : 'differs' }],
  });
  return {
    description: `The Pi v${pin.version} fixture set: each fixture, the suites that read its v1.1.0 counterpart today, and per scenario (a ` +
      'top-level key of an *.upstream.json, a session scenario directory, or a whole file) the work-list areas ' +
      '(compatibility/durable-sync-1.2.0-worklist.json) that switch their suites to it, and whether it matches the v1.1.0 capture under ' +
      'the manifest\'s normalization ("same": switch at any time; "differs": switch with the port that changes it; "new": no v1.1.0 ' +
      'counterpart, a new case to port; "metadata": the captured commit, not a case). The C# suites still read the v1.1.0 set; nothing here is read by a test yet. Written by ' +
      'tools/DurableFixtures/capture-all.mjs; regenerate, never edit by hand.',
    upstream: { release: `Pi v${pin.version}`, commit: pin.commit },
    baseline: { release: 'Pi v1.1.0', commit: PINS['1.1.0'], directory: posix(baseline) },
    switching: 'An area moves a suite by pointing its fixture path at this set (for compat.*, CompatCases.Fixtures, which reads every ' +
      'session scenario from one directory, so it switches once every scenario it runs matches; tool-interrupted and subagent need area a\'s ' +
      'pi.tool v2) and adding the scenarios marked "new" to its C# port (Compat/CompatScenarios.cs for sessions). The upstream-sessions ' +
      'manifest lists the fields to normalize; v1.2.0 adds durations of nested calls, code mode\'s wall time, clock and seed.',
    fixtures: entries,
  };
}

// ─── main ───────────────────────────────────────────────────────────────
const commands = captureAll(out);

let determinism;
if (check) {
  const second = mkdtempSync(join(tmpdir(), 'pisharp-capture-repeat-'));
  try {
    captureAll(second);
    const results = (await compare(out, second)).filter((result) => !result.path.endsWith('manifest.json') && !result.path.endsWith('index.json'));
    const differing = [];
    for (const result of results) {
      if (result.status === 'same') continue;
      if (result.path.startsWith('examples-19-json-') && result.status === 'differs' &&
        exampleLines(join(out, result.path)) === exampleLines(join(second, result.path))) continue;
      differing.push(result);
    }
    // The session manifest lists file sizes, which a duration's digit count can change; compare it without them.
    const manifest = (root) => JSON.stringify({ ...JSON.parse(readFileSync(join(root, 'upstream-sessions', 'manifest.json'), 'utf8')), files: undefined });
    if (manifest(out) !== manifest(second)) differing.push({ path: 'upstream-sessions/manifest.json', status: 'differs' });
    for (const result of differing) {
      console.log(`${result.status}: ${result.path}`);
      if (result.left !== undefined) console.log(`  first  ...${result.left}\n  second ...${result.right}`);
    }
    determinism = {
      checked: new Date().toISOString().slice(0, 10),
      files: results.length,
      differing: differing.map((result) => result.path),
      method: 'capture-all.mjs --check: a second capture compared file by file with compare-captures.mjs normalization (session.sqlite files ' +
        'through their records); example 19 also without its streamed partial messages and with times, durations and the faux API name as placeholders',
    };
    console.log(`repeat check: ${results.length - differing.length} of ${results.length} files repeat`);
    if (differing.length > 0) process.exitCode = 1;
  } finally {
    rmSync(second, { recursive: true, force: true });
  }
}

if (pin.version !== '1.1.0') {
  const sessions = JSON.parse(readFileSync(join(out, 'upstream-sessions', 'manifest.json'), 'utf8'));
  const json = (value) => `${JSON.stringify(value, null, 1)}\n`;
  const varies = [
    ...sessions.varies,
    { field: 'example 19: timestamps, durationMs, the faux API name ("faux:<time>:<random>") and the streamed partial messages',
      why: 'the example uses the wall clock and a faux model streaming at 200 tokens per second, committed every 100 ms; ExamplesCases.cs ' +
        'compares them as placeholders' },
  ];
  if (!check) {
    const previous = existsSync(join(out, 'manifest.json')) ? JSON.parse(readFileSync(join(out, 'manifest.json'), 'utf8')) : undefined;
    determinism = previous?.determinism;
  }
  writeFileSync(join(out, 'manifest.json'), json({
    description: `Every durable capture at Pi v${pin.version}, made by tools/DurableFixtures/capture-all.mjs. The v1.1.0 set stays in ` +
      'tests/PiSharp.Durable.Tests/Fixtures (*.upstream.json(l), upstream-sessions), which the C# suites still read; each work-list area ' +
      'moves its suites to this set when it lands (index.json). Regenerate with the command below, never edit by hand.',
    upstream: { release: `Pi v${pin.version}`, commit: pin.commit, ...Object.fromEntries(Object.entries(sessions.upstream).filter(([key]) => key !== 'commit' && key !== 'release')) },
    node: process.version,
    sqlite: new DatabaseSync(':memory:').prepare('select sqlite_version() as v').get().v,
    platform: `${process.platform} ${process.arch}`,
    command: `node --no-warnings tools/DurableFixtures/capture-all.mjs <pi checkout at the commit> --commit ${pin.commit} --check`,
    commands,
    varies,
    normalized: sessions.normalized,
    determinism: determinism ?? null,
  }));
  writeFileSync(join(out, 'index.json'), json(await buildIndex()));
}
