// Builds compatibility/durable-port-map.json from a checkout of Pi at the pinned commit (decision 0005).
// Usage: node tools/DurablePortMap/build-port-map.mjs --upstream <pi checkout at abe508e> [--check]
// Each upstream file to port gets its git blob, its line count, the planned C# location, the phase and a status. A rebuild keeps
// the status and note already recorded for a file, so a later baseline sync shows which ported files changed upstream.
import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join, relative } from 'node:path';

const UPSTREAM_COMMIT = 'abe508e1b89912adde45528136c3221eb69acdd7';
const OUT = 'compatibility/durable-port-map.json';
const args = process.argv.slice(2);
const upstream = args[args.indexOf('--upstream') + 1];
if (!args.includes('--upstream') || !upstream) throw new Error('--upstream <checkout> is required');
const head = execFileSync('git', ['-C', upstream, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
if (head !== UPSTREAM_COMMIT) throw new Error(`upstream checkout is at ${head}, expected ${UPSTREAM_COMMIT}`);

const blob = path => execFileSync('git', ['-C', upstream, 'rev-parse', `HEAD:${path}`], { encoding: 'utf8' }).trim();
const lineCount = path => { const text = readFileSync(join(upstream, path), 'utf8'); return text.split('\n').length - (text.endsWith('\n') ? 1 : 0); };
const pascal = name => name.split(/[-_.]/).filter(Boolean).map(word => word[0].toUpperCase() + word.slice(1)).join('');
const walk = dir => readdirSync(join(upstream, dir)).sort().flatMap(name => {
  const path = `${dir}/${name}`;
  return statSync(join(upstream, path)).isDirectory() ? walk(path) : [path];
});

const rows = [];
const add = (up, pisharp, phase, status = 'planned', note) =>
  rows.push({ upstream: up, blobSha1: blob(up), lines: lineCount(up), pisharp, phase, status, ...(note ? { note } : {}) });

const harnessPhase = { scheduler: 'P7', generation: 'P8', tool: 'P9', compaction: 'P11', harness: 'P6', events: 'P10', output: 'P9',
  context: 'P6', agent: 'P6', view: 'P10', 'task-graph': 'P11', submissions: 'P8', live: 'P8', prompt: 'P8', inbox: 'P10', registry: 'P6',
  usage: 'P10', util: 'P6', define: 'P6', provider: 'P8', json: 'P6', types: 'P6' };
const durableSource = 'packages/durable/src';
for (const path of walk(durableSource).filter(path => path.endsWith('.ts'))) {
  const sub = relative(durableSource, path).replaceAll('\\', '/'), parts = sub.split('/'), base = parts.at(-1).slice(0, -3);
  if (sub === 'storage/sqlite/cloudflare.ts') { add(path, null, null, 'excluded', 'Cloudflare Durable Object adapter (decision 0005)'); continue; }
  let [phase, project, dir] = ['P2', 'PiSharp.Durable', ''];
  if (parts[0] === 'harness') [phase, project, dir] = [harnessPhase[base] ?? 'P6', 'PiSharp.Durable', 'Harness'];
  else if (parts[0] === 'session') [phase, project, dir] = ['P3', 'PiSharp.Durable', 'Session'];
  else if (parts[0] === 'storage' && parts.length > 2 && parts[1] === 'sqlite') [phase, project, dir] = ['P5', 'PiSharp.Durable.Sqlite', ''];
  else if (parts[0] === 'storage' && parts.length > 2 && parts[1] === 'jsonl') [phase, project, dir] = ['P4', 'PiSharp.Durable', 'Storage/Jsonl'];
  else if (parts[0] === 'storage') [phase, project, dir] = ['P2', 'PiSharp.Durable', 'Storage'];
  else if (parts[0] === 'env' && base === 'index') [phase, project, dir] = ['P4', 'PiSharp.Durable', 'Env'];
  else if (parts[0] === 'env') [phase, project, dir] = ['P9', 'PiSharp.Durable.Tools', 'Env'];
  else if (parts[0] === 'tools') [phase, project, dir] = ['P9', 'PiSharp.Durable.Tools', 'Tools'];
  else if (parts[0] === 'testing') [phase, project, dir] = [base.includes('storage') ? 'P2' : 'P9', 'PiSharp.Durable', 'Testing'];
  const name = base !== 'index' ? pascal(base) : parts.length > 1 ? pascal(parts.at(-2)) : 'Exports';
  add(path, ['src', project, dir, `${name}.cs`].filter(Boolean).join('/'), phase);
}

for (const [path, target] of [
  ['packages/chord/src/context/index.ts', 'Chord/DurableContext.cs'], ['packages/chord/src/delta/index.ts', 'Chord/Delta/Ops.cs'],
  ['packages/chord/src/delta/tracker.ts', 'Chord/Delta/Draft.cs'], ['packages/chord/src/delta/diff.ts', 'Chord/Delta/Diff.cs'],
  ['packages/chord/src/delta/apply-immutable-trusted.ts', 'Chord/Delta/ApplyImmutableTrusted.cs'],
  ['packages/chord/src/delta/revision-validator.ts', 'Chord/Delta/RevisionValidator.cs'],
  ['packages/chord/src/json.ts', 'Chord/JsonValue.cs'], ['packages/chord/src/services/state.ts', 'Chord/ReplicatedState.cs'],
  ['packages/chord/src/services/state-internals.ts', 'Chord/ReplicatedState.cs'], ['packages/chord/src/types.ts', 'Chord/Types.cs']]) {
  if (!existsSync(join(upstream, path))) throw new Error(`missing ${path}`);
  add(path, `src/PiSharp.Durable/${target}`, 'P1', 'planned',
    path.includes('tracker') ? 'An explicit copy-on-write draft API replaces the Proxy tracker (decision 0005)' : undefined);
}

const testPhase = { conversations: 'P6', context: 'P6', registry: 'P6', tasks: 'P7', 'tasks-recovery': 'P7', structured: 'P7',
  generation: 'P8', 'generation-recovery': 'P8', submissions: 'P8', prompt: 'P8', tools: 'P9', 'tools-recovery': 'P9', output: 'P9',
  'output-skip': 'P9', inbox: 'P10', view: 'P10', events: 'P10', 'live-deltas': 'P10' };
for (const path of walk('packages/durable/test').filter(path => path.endsWith('.ts'))) {
  const base = path.split('/').at(-1).replace('.test.ts', '').replace(/\.ts$/, '');
  if (base.includes('cloudflare')) { add(path, null, null, 'excluded', 'Cloudflare adapter'); continue; }
  if (base.endsWith('-e2e')) { add(path, null, null, 'skipped', 'Needs live provider credentials'); continue; }
  const phase = base.startsWith('harness-') ? testPhase[base.slice(8)] ?? 'P11'
    : path.includes('/examples/') || base.includes('bench') ? 'P13'
    : base.startsWith('session') ? 'P3' : base.startsWith('sqlite') ? 'P5' : base.startsWith('jsonl') ? 'P4'
    : /^(env|tools)/.test(base) ? 'P9' : /^(memory|storage|types)/.test(base) ? 'P2' : base.startsWith('chord') ? 'P3' : 'P11';
  add(path, `tests/PiSharp.Durable.Tests/${pascal(base)}Cases.cs`, phase);
}

for (const path of walk('packages/coding-agent/src/experimental/durable').filter(path => path.endsWith('.ts')))
  add(path, `src/PiSharp.Cli/Durable/Durable${pascal(path.split('/').at(-1).slice(0, -3))}.cs`, 'P12');

// Keep recorded progress across rebuilds.
const previous = existsSync(OUT) ? new Map(JSON.parse(readFileSync(OUT, 'utf8')).files.map(row => [row.upstream, row])) : new Map();
for (const row of rows) {
  const old = previous.get(row.upstream);
  if (!old || old.status === 'planned') continue;
  row.status = old.status; if (old.note) row.note = old.note; if (old.pisharp) row.pisharp = old.pisharp;
  if (old.blobSha1 !== row.blobSha1) row.upstreamChanged = old.blobSha1;
}

const text = JSON.stringify({
  schemaVersion: 1, upstreamCommit: UPSTREAM_COMMIT, decision: 'docs/decisions/0005-pi-durable-experimental.md',
  statusMeaning: { planned: 'Not started.', 'in-progress': 'Being ported.', ported: 'Ported, with the listed upstream tests passing.',
    excluded: 'Out of scope per decision 0005.', skipped: 'Test not portable offline; the note gives the reason.' },
  files: rows }, null, 2) + '\n';
if (args.includes('--check')) {
  if (readFileSync(OUT, 'utf8') !== text) { console.error(`${OUT} is out of date; rebuild it.`); process.exit(1); }
} else writeFileSync(OUT, text);
console.log(`${rows.length} files; ${rows.filter(row => row.status === 'planned').reduce((sum, row) => sum + row.lines, 0)} planned lines`);
