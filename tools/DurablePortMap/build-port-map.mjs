// Builds compatibility/durable-port-map.json from a Pi git repository that holds the pinned commit and the previous pin (decision 0005).
// Usage: node tools/DurablePortMap/build-port-map.mjs --upstream <Pi git repository with both commits> [--check]
// Everything is read from git objects, so the checkout's HEAD and work tree do not matter.
// Each upstream file to port gets its git blob and line count at the pinned commit, the planned C# location, the phase and a status. A
// rebuild keeps the status and note already recorded for a file. Against the previous pin it also records what changed upstream
// (upstreamChange, previousBlobSha1, changedLines), and a ported file whose recorded blob is not the pinned one becomes needs-sync, with
// portedBlobSha1 naming the blob the C# was ported from, so no row claims a port of upstream text it has not seen.
// A recorded file that neither pin has stays only while PiSharp keeps its port (pisharp-only, with removedIn).
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';

const UPSTREAM_COMMIT = 'f9a26365568127461c47a07a37dcb42c140a3ac5'; // Pi v1.5.0
const UPSTREAM_VERSION = '1.5.0';
const PREVIOUS_COMMIT = '06a466935536f8c9bcacb2d5efc0fc021be1bdff'; // Pi v1.2.0, the second pin (the first was v1.1.0, abe508e1)
const PREVIOUS_VERSION = '1.2.0';
const SYNC_PHASE = `sync-${UPSTREAM_VERSION}`; // phase of files new in the pinned commit; the work list groups them
const WORKLIST = `compatibility/durable-sync-${UPSTREAM_VERSION}-worklist.json`;
const OUT = 'compatibility/durable-port-map.json';
const args = process.argv.slice(2);
const upstream = args[args.indexOf('--upstream') + 1];
if (!args.includes('--upstream') || !upstream) throw new Error('--upstream <Pi git repository> is required');
const git = (...command) => execFileSync('git', ['-C', upstream, ...command], { encoding: 'utf8', maxBuffer: 1 << 30 });
for (const commit of [UPSTREAM_COMMIT, PREVIOUS_COMMIT])
  try { execFileSync('git', ['-C', upstream, 'cat-file', '-e', `${commit}^{commit}`], { stdio: 'ignore' }); } catch { throw new Error(`${upstream} does not contain commit ${commit}; fetch it`); }

const listing = new Map();
const tree = commit => {
  if (!listing.has(commit)) {
    const rows = git('ls-tree', '-r', '--full-tree', commit).trim().split('\n').map(line => {
      const [meta, path] = line.split('\t');
      return [path, meta.split(' ')[2]];
    });
    listing.set(commit, new Map(rows));
  }
  return listing.get(commit);
};
const walk = (commit, dir) => [...tree(commit).keys()].filter(path => path.startsWith(dir + '/')).sort();
const blobAt = (commit, path) => tree(commit).get(path) ?? null;
const lineCount = blob => { const text = git('cat-file', 'blob', blob); return text.split('\n').length - (text.endsWith('\n') ? 1 : 0); };
const changedLines = (...paths) => git('diff', '--numstat', '-M', PREVIOUS_COMMIT, UPSTREAM_COMMIT, '--', ...paths).trim().split('\n').filter(Boolean)
  .map(line => line.split('\t')).reduce((sum, [added, removed]) => sum + (Number(added) || 0) + (Number(removed) || 0), 0);
const pascal = name => name.split(/[-_.]/).filter(Boolean).map(word => word[0].toUpperCase() + word.slice(1)).join('');
// Upstream renames between the two pins (git's similarity detection), new path -> old path.
const renamedFrom = new Map(git('diff', '--name-status', '-M', PREVIOUS_COMMIT, UPSTREAM_COMMIT, '--', 'packages/durable', 'packages/chord')
  .trim().split('\n').map(line => line.split('\t')).filter(([status]) => status.startsWith('R')).map(([, from, to]) => [to, from]));
// Renames git's similarity detection misses over the whole range: upstream's renumbering (b9fb2b854) moved 05-watches.ts to
// 04-watches.ts, which later commits rewrote.
renamedFrom.set('packages/durable/test/examples/04-watches.ts', 'packages/durable/test/examples/05-watches.ts');
const renamedTo = new Map([...renamedFrom].map(([to, from]) => [from, to]));

// The planned C# location of an upstream file: [pisharp, phase] or [null, null, status, note].
const harnessPhase = { scheduler: 'P7', generation: 'P8', tool: 'P9', compaction: 'P11', harness: 'P6', events: 'P10', output: 'P9',
  context: 'P6', agent: 'P6', view: 'P10', 'task-graph': 'P11', submissions: 'P8', live: 'P8', prompt: 'P8', inbox: 'P10', registry: 'P6',
  usage: 'P10', util: 'P6', define: 'P6', provider: 'P8', json: 'P6', types: 'P6' };
const durableSource = 'packages/durable/src';
function sourcePlan(path) {
  const sub = path.slice(durableSource.length + 1), parts = sub.split('/'), base = parts.at(-1).replace(/(\.d)?\.ts$/, '');
  if (sub === 'storage/sqlite/cloudflare.ts') return [null, null, 'excluded', 'Cloudflare Durable Object adapter (decision 0005)'];
  if (sub === 'images/cloudflare.ts') return [null, null, 'excluded', 'Cloudflare Images adapter (decision 0005: Cloudflare adapters are out of scope)'];
  if (sub === 'storage/indexeddb.ts') return [null, null, 'excluded', 'Browser storage over IndexedDB (decision 0013, targets .NET cannot run; applicability row durable.indexeddb-storage is no-op)'];
  if (sub.endsWith('.d.ts')) return [null, null, 'excluded', 'Ambient type declarations for a WebAssembly module; no C# counterpart'];
  let [phase, project, dir] = ['P2', 'PiSharp.Durable', ''];
  // Area registry-reader: registryReader needs Chord v2's registry value, so it is the adapter project PiSharp.Durable.Store's (decision
  // 0013, phase 4).
  if (sub === 'harness/registry-reader.ts') [phase, project, dir] = ['P6', 'PiSharp.Durable.Store', ''];
  else if (parts[0] === 'harness') [phase, project, dir] = [harnessPhase[base] ?? 'P6', 'PiSharp.Durable', 'Harness'];
  else if (parts[0] === 'session') [phase, project, dir] = ['P3', 'PiSharp.Durable', 'Session'];
  else if (parts[0] === 'storage' && parts.length > 2 && parts[1] === 'sqlite') [phase, project, dir] = ['P5', 'PiSharp.Durable.Sqlite', ''];
  else if (parts[0] === 'storage' && parts.length > 2 && parts[1] === 'jsonl') [phase, project, dir] = ['P4', 'PiSharp.Durable', 'Storage/Jsonl'];
  // Area store-storage: StoreStorage needs Chord v2 types, so it is the adapter project PiSharp.Durable.Store (decision 0013, phase 4).
  else if (parts[0] === 'storage' && parts.length > 2 && parts[1] === 'store') [phase, project, dir] = ['P2', 'PiSharp.Durable.Store', ''];
  else if (parts[0] === 'storage') [phase, project, dir] = ['P2', 'PiSharp.Durable', 'Storage'];
  else if (parts[0] === 'env' && base === 'index') [phase, project, dir] = ['P4', 'PiSharp.Durable', 'Env'];
  else if (parts[0] === 'env') [phase, project, dir] = ['P9', 'PiSharp.Durable.Tools', 'Env'];
  else if (parts[0] === 'tools') [phase, project, dir] = ['P9', 'PiSharp.Durable.Tools', 'Tools'];
  else if (parts[0] === 'testing') [phase, project, dir] = [base.includes('storage') ? 'P2' : 'P9', 'PiSharp.Durable', 'Testing'];
  else if (['codemode', 'images', 'tool-search'].includes(parts[0])) [phase, project, dir] = [SYNC_PHASE, 'PiSharp.Durable.Tools', pascal(parts[0])];
  const name = base !== 'index' ? pascal(base) : parts.length > 1 ? pascal(parts.at(-2)) : 'Exports';
  return [['src', project, dir, `${name}.cs`].filter(Boolean).join('/'), phase];
}

const chordFiles = new Map([
  ['packages/chord/src/context/index.ts', 'Chord/DurableContext.cs'], ['packages/chord/src/delta/index.ts', 'Chord/Delta/Ops.cs'],
  ['packages/chord/src/delta/tracker.ts', 'Chord/Delta/Draft.cs'],
  ['packages/chord/src/delta/diff.ts', 'Chord/Delta/Diff.cs'],
  ['packages/chord/src/delta/apply-immutable-trusted.ts', 'Chord/Delta/ApplyImmutableTrusted.cs'],
  ['packages/chord/src/delta/revision-validator.ts', 'Chord/Delta/RevisionValidator.cs'],
  ['packages/chord/src/json.ts', 'Chord/JsonValue.cs'], ['packages/chord/src/services/state.ts', 'Chord/ReplicatedState.cs'],
  ['packages/chord/src/state.ts', 'Chord/ReplicatedState.cs'],
  ['packages/chord/src/services/state-internals.ts', 'Chord/ReplicatedState.cs'], ['packages/chord/src/types.ts', 'Chord/Types.cs'],
  // Pi v1.5.0: what pi-durable imports from Chord's watch, delta, output and sqlite entry points. The output module moved from
  // packages/durable/src/harness/output.ts; the SQLite layer stays in PiSharp.Durable.Sqlite.
  ['packages/chord/src/watch.ts', 'Chord/Watch.cs'], ['packages/chord/src/delta/pending.ts', 'Chord/Delta/Pending.cs'],
  ['packages/chord/src/output.ts', 'Harness/Output.cs'],
  ['packages/chord/src/sqlite/index.ts', 'src/PiSharp.Durable.Sqlite/Database.cs'],
  ['packages/chord/src/sqlite/database.ts', 'src/PiSharp.Durable.Sqlite/Database.cs'],
  ['packages/chord/src/sqlite/migrations.ts', 'src/PiSharp.Durable.Sqlite/Migrations.cs'],
  ['packages/chord/src/sqlite/schema.ts', 'src/PiSharp.Durable.Sqlite/Schema.cs'],
  ['packages/chord/src/sqlite/node.ts', 'src/PiSharp.Durable.Sqlite/ConnectionDatabase.cs'],
  ['packages/chord/src/sqlite/cloudflare.ts', null]]);
function chordPlan(path) {
  const target = chordFiles.get(path);
  if (!target) return [null, null, 'excluded', 'Cloudflare Durable Object SQLite driver (decision 0005: Cloudflare adapters are out of scope)'];
  return [target.startsWith('src/') ? target : `src/PiSharp.Durable/${target}`, 'P1', 'planned',
    path.includes('tracker') ? 'An explicit copy-on-write draft API replaces the Proxy tracker (decision 0005)' : undefined];
}

const testPhase = { conversations: 'P6', context: 'P6', registry: 'P6', tasks: 'P7', 'tasks-recovery': 'P7', structured: 'P7',
  generation: 'P8', 'generation-recovery': 'P8', submissions: 'P8', prompt: 'P8', tools: 'P9', 'tools-recovery': 'P9', output: 'P9',
  'output-skip': 'P9', inbox: 'P10', view: 'P10', events: 'P10', 'live-deltas': 'P10' };
function testPlan(path) {
  const base = path.split('/').at(-1).replace('.test.ts', '').replace(/\.ts$/, '');
  if (base.includes('cloudflare')) return [null, null, 'excluded', 'Cloudflare adapter'];
  if (base.endsWith('-e2e')) return [null, null, 'skipped', 'Needs live provider credentials'];
  if (base.includes('indexeddb')) return [null, null, 'excluded', 'Tests the browser IndexedDB storage (applicability row durable.indexeddb-storage is no-op)'];
  if (base === 'codemode-imports') return [null, null, 'skipped', "Not applicable: checks that importing the code mode extension leaves pi-codemode's WASM QuickJS runtime unloaded, a JavaScript module-graph property; PiSharp has no such runtime (applicability row durable.codemode-declarations-import is no-op)"];
  const phase = base.startsWith('harness-') ? testPhase[base.slice(8)] ?? 'P11'
    : path.includes('/examples/') || base.includes('bench') ? 'P13'
    : base.startsWith('session') ? 'P3' : base.startsWith('sqlite') ? 'P5' : base.startsWith('jsonl') ? 'P4'
    : /^(env|tools)/.test(base) ? 'P9' : /^(memory|storage|types)/.test(base) ? 'P2' : base.startsWith('chord') ? 'P3' : 'P11';
  // heap-retained.ts is a helper of the memory benchmarks.
  if (base.includes('bench') || base === 'heap-retained') return [`benchmarks/PiSharp.Durable.Benchmarks/${pascal(base)}.cs`, phase];
  // An example is a sample program, which ExamplesCases.cs runs; shared test helpers keep their name.
  if (path.includes('/examples/')) return [`samples/Durable.Examples/Examples${pascal(base.replace(/^\d+-/, ''))}.cs`, phase];
  if (base.endsWith('-support') || base === 'store-storage-node') return [`tests/PiSharp.Durable.Tests/${pascal(base)}.cs`, phase];
  return [`tests/PiSharp.Durable.Tests/${pascal(base)}Cases.cs`, phase];
}

// Sections in map order; each lists its upstream files at a commit and plans one.
const sections = [
  { files: commit => walk(commit, durableSource).filter(path => path.endsWith('.ts')), plan: sourcePlan },
  { files: commit => [...chordFiles.keys()].filter(path => blobAt(commit, path)), plan: chordPlan },
  { files: commit => walk(commit, 'packages/durable/test').filter(path => path.endsWith('.ts')), plan: testPlan },
];

const recorded = existsSync(OUT) ? JSON.parse(readFileSync(OUT, 'utf8')) : { files: [] };
const previous = new Map(recorded.files.map(row => [row.upstream, row]));
const rows = [];
for (const section of sections) {
  const current = new Set(section.files(UPSTREAM_COMMIT));
  const paths = [...new Set([...current, ...section.files(PREVIOUS_COMMIT).filter(path => !renamedTo.has(path) || !current.has(renamedTo.get(path)))])];
  for (const path of paths.sort()) {
    const [pisharp, phase, status = 'planned', note] = section.plan(path);
    const blob = blobAt(UPSTREAM_COMMIT, path), from = renamedFrom.get(path) ?? path, before = blobAt(PREVIOUS_COMMIT, from);
    const row = { upstream: path, blobSha1: blob ?? before, lines: lineCount(blob ?? before), pisharp, phase, status, ...(note ? { note } : {}) };
    if (!blob) row.upstreamChange = 'removed';
    else if (!before) row.upstreamChange = 'added';
    else if (before !== blob) Object.assign(row, { upstreamChange: from === path ? 'modified' : 'renamed', ...(from !== path ? { renamedFrom: from } : {}),
      previousBlobSha1: before, changedLines: changedLines(...new Set([from, path])) });
    // Keep recorded progress; a renamed file keeps its old row's progress.
    const old = previous.get(path) ?? previous.get(from);
    if (old && old.status !== 'planned') {
      row.status = old.status; if (old.note) row.note = old.note; if (old.pisharp) row.pisharp = old.pisharp;
      if (old.phase) row.phase = old.phase;
      if (old.portedBlobSha1 && old.status === 'needs-sync') row.portedBlobSha1 = old.portedBlobSha1;
      if (old.status === 'ported' && blob && old.blobSha1 !== blob) [row.status, row.portedBlobSha1] = ['needs-sync', old.blobSha1];
    }
    // Current gap work is independent of the release-baseline status and immutable upstream blobs. In particular, a
    // release-planned example can now have source without a passing integrated run. Preserve that mapping without
    // relabelling it as ported, and do not regenerate its former planned path over the actual source location.
    if (old?.currentImplementation) {
      row.currentImplementation = old.currentImplementation;
      if ('historicalPisharp' in old) row.historicalPisharp = old.historicalPisharp;
      if (old.pisharp) row.pisharp = old.pisharp;
    }
    if (!blob) {
      if (['ported', 'needs-sync', 'in-progress'].includes(row.status)) row.status = 'removed-upstream';
    } else if (row.upstreamChange === 'added' && (!old || old.status === 'planned')) row.phase = row.phase && SYNC_PHASE;
    rows.push(row);
  }
}
// Recorded rows whose file is at neither pin: upstream deleted it before the previous pin. A port PiSharp keeps (pisharp-only: the
// experimental durable agent upstream deleted in v1.2.0, which `pisharp durable` keeps, decision 0005) stays as recorded, with removedIn
// naming the release that deleted it; a port an earlier sync retired (removed-upstream) leaves the map.
for (const old of recorded.files) {
  if (old.status !== 'pisharp-only' || rows.some(row => row.upstream === old.upstream)) continue;
  const { upstreamChange, previousBlobSha1, changedLines: changed, renamedFrom: moved, successor, ...kept } = old;
  rows.push({ ...kept, removedIn: old.removedIn ?? (upstreamChange === 'removed' ? recorded.upstreamVersion : undefined) });
}
// The pre-rename path, when a renamed file's old path has no row of its own: point at the successor.
for (const row of rows) if (row.upstreamChange === 'removed' && renamedTo.has(row.upstream)) row.successor = renamedTo.get(row.upstream);
// Successors git cannot see: in v1.2.0 upstream moved replicated state out of services/ (the host runtime that replaced it is out of
// scope) and state.ts took over both files; in v1.5.0 Chord's shared Watch took over Durable's watch implementation (42cb822f1).
const successors = { 'packages/chord/src/services/state-internals.ts': 'packages/chord/src/state.ts',
  'packages/durable/src/session/observation.ts': 'packages/chord/src/watch.ts' };
for (const row of rows) if (successors[row.upstream] && row.upstreamChange === 'removed') row.successor = successors[row.upstream];

const count = predicate => rows.filter(predicate).length;
const text = JSON.stringify({
  schemaVersion: 2, upstreamCommit: UPSTREAM_COMMIT, upstreamVersion: UPSTREAM_VERSION, previousCommit: PREVIOUS_COMMIT,
  previousVersion: PREVIOUS_VERSION, decision: 'docs/decisions/0005-pi-durable-experimental.md',
  worklist: WORKLIST,
  ...(recorded.currentImplementation ? { currentImplementation: recorded.currentImplementation } : {}),
  statusMeaning: { planned: 'Not started.', 'in-progress': 'Being ported.',
    ported: 'Ported from the blob in blobSha1, with the listed upstream tests passing.',
    'needs-sync': 'Ported from an earlier upstream blob (portedBlobSha1); the pinned blob differs and the C# has not caught up yet.',
    'removed-upstream': 'Ported from the previous pin (blobSha1 is that blob); the pinned commit deleted the file. The C# stays until the sync retires or replaces it (successor names the file that took over, if any).',
    'pisharp-only': 'Upstream deleted the file (removedIn names the release that did, when it is not the pinned one); PiSharp keeps its port of the last upstream blob (blobSha1) as a PiSharp-only feature by owner decision (decision 0005).',
    excluded: 'Out of scope per decision 0005.', skipped: 'Test not portable offline; the note gives the reason.' },
  upstreamChangeMeaning: { added: `New in v${UPSTREAM_VERSION}.`, modified: `Changed between v${PREVIOUS_VERSION} and v${UPSTREAM_VERSION} (previousBlobSha1, changedLines).`,
    renamed: `Moved in v${UPSTREAM_VERSION} (renamedFrom, previousBlobSha1, changedLines).`, removed: `Deleted in v${UPSTREAM_VERSION}; blobSha1 and lines are the v${PREVIOUS_VERSION} file.` },
  files: rows }, null, 2) + '\n';
if (args.includes('--check')) {
  if (!existsSync(OUT) || readFileSync(OUT, 'utf8') !== text) { console.error(`${OUT} is out of date; rebuild it.`); process.exit(1); }
} else writeFileSync(OUT, text);
const statuses = {};
for (const row of rows) statuses[row.status] = (statuses[row.status] ?? 0) + 1;
console.log(`${rows.length} files${recorded.currentImplementation ? ' (release-baseline statuses)' : ''}; ${JSON.stringify(statuses)}; upstream ${JSON.stringify({ unchanged: count(row => !row.upstreamChange && !row.removedIn),
  modified: count(row => row.upstreamChange === 'modified'), renamed: count(row => row.upstreamChange === 'renamed'),
  added: count(row => row.upstreamChange === 'added'), removed: count(row => row.upstreamChange === 'removed'),
  removedEarlier: count(row => row.removedIn) })}; ${
  rows.filter(row => ['planned', 'needs-sync'].includes(row.status)).reduce((sum, row) => sum + (row.changedLines ?? row.lines), 0)} ${recorded.currentImplementation ? `historical-baseline lines marked pending; ${rows.filter(row=>row.currentImplementation).length} current source mappings (integrated runtime validation pending)` : 'lines to sync'}`);
