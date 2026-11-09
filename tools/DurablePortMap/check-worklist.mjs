// Checks compatibility/durable-sync-1.5.0-worklist.json, the durable re-sync work list (decision 0005; decision 0013 phase 4), against
// the upstream range, the applicability table and the port map.
// Usage: node tools/DurablePortMap/check-worklist.mjs --upstream <Pi git repository with both commits> [--worklist <path>]
// Everything upstream is read from git objects, so the clone's HEAD and work tree do not matter. It checks:
// - coverage: every file under packages/durable that changed, appeared or disappeared between the work list's from and to commits, and
//   every packages/chord file a phase 4 applicability row names (directories expanded at both commits), is in exactly one area's upstream
//   list or one excluded entry, with git's change kind and line count; every other changed packages/chord file is too, and an excluded
//   one gives a reason that starts with "phase 6";
// - rows: every phase 4 row of compatibility/sync-1.5.0-applicability.json is in exactly one area's applicabilityRows, no other row is,
//   and tools.windows-shell-install-dirs (phase 2, deferred to phase 4) is named in an area's notes;
// - shape: the fields of the work list and its areas, sizes S to L, dependsOn ids that exist and form no cycle, and one order line per
//   area in area order;
// - waves: every area in exactly one wave of at most 5, its dependsOn in earlier waves, and areas that share a pisharp file (other than
//   tests/*/Program.cs and the port map) in different waves, the earlier area in order first;
// - files: pisharp and tests paths exist in the repository unless marked " (new)" (a file the area creates), a path marked " (removed)"
//   (a file a landed area deleted) does not, and every needs-sync or planned port map row's
//   upstream file is covered.
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const args = process.argv.slice(2);
const option = name => (args.includes(name) ? args[args.indexOf(name) + 1] : undefined);
const upstream = option('--upstream');
const offline = args.includes('--offline');
if (offline && upstream) throw new Error('Choose --offline or --upstream, not both');
if (!offline && !upstream) throw new Error('--upstream <Pi git repository> is required');
const worklistPath = option('--worklist') ?? 'compatibility/durable-sync-1.5.0-worklist.json';
const readJson = path => JSON.parse(readFileSync(resolve(root, path), 'utf8'));
const worklist = readJson(worklistPath);
const table = readJson('compatibility/sync-1.5.0-applicability.json');
const portMap = readJson(worklist.portMap);
const FROM = worklist.from?.commit, TO = worklist.to?.commit;
const git = (...command) => execFileSync('git', ['-C', upstream, ...command], { encoding: 'utf8', maxBuffer: 1 << 30 });
for (const commit of offline ? [] : [FROM, TO])
  try { execFileSync('git', ['-C', upstream, 'cat-file', '-e', `${commit}^{commit}`], { stdio: 'ignore' }); } catch { throw new Error(`${upstream} does not contain commit ${commit}; fetch it`); }

const errors = [];
const fail = message => errors.push(message);
const D = 'packages/durable/', C = 'packages/chord/';
const SHARED = path => /^tests\/[^/]+\/Program\.cs$/.test(path) || path === worklist.portMap;
const NEW = ' (new)';
const REMOVED = ' (removed)';
const bare = path => (path.endsWith(NEW) ? path.slice(0, -NEW.length) : path.endsWith(REMOVED) ? path.slice(0, -REMOVED.length) : path);

// ---------------------------------------------------------------- shape
for (const field of ['schemaVersion', 'decision', 'portMap', 'from', 'to', 'rule', 'sizeScale', 'areas', 'excluded', 'order', 'waves'])
  if (!(field in worklist)) fail(`work list: missing ${field}`);
const areas = worklist.areas ?? [];
const byId = new Map();
for (const area of areas) {
  for (const field of ['id', 'title', 'summary', 'changelog', 'applicabilityRows', 'upstream', 'pisharp', 'tests', 'upstreamLines', 'size', 'dependsOn', 'notes'])
    if (!(field in area)) fail(`area ${area.id}: missing ${field}`);
  if (byId.has(area.id)) fail(`area ${area.id}: duplicate id`);
  byId.set(area.id, area);
  if (!['S', 'M', 'L'].includes(area.size)) fail(`area ${area.id}: size ${area.size}; split anything over L`);
  if (!area.title || !area.summary || !area.notes) fail(`area ${area.id}: empty title, summary or notes`);
  for (const item of area.changelog ?? []) if (!item.package || !item.item) fail(`area ${area.id}: changelog entry without package or item`);
  const sum = (area.upstream ?? []).reduce((total, entry) => total + entry.lines, 0);
  if (sum !== area.upstreamLines) fail(`area ${area.id}: upstreamLines ${area.upstreamLines}, its upstream entries sum to ${sum}`);
}
for (const area of areas) for (const dependency of area.dependsOn ?? []) {
  if (dependency === area.id) fail(`area ${area.id}: depends on itself`);
  else if (!byId.has(dependency)) fail(`area ${area.id}: depends on unknown area ${dependency}`);
}
{
  const state = new Map();
  const visit = (id, path) => {
    if (state.get(id) === 'done') return;
    if (state.get(id) === 'open') { fail(`dependsOn cycle: ${[...path, id].join(' -> ')}`); return; }
    state.set(id, 'open');
    for (const dependency of byId.get(id)?.dependsOn ?? []) if (byId.has(dependency)) visit(dependency, [...path, id]);
    state.set(id, 'done');
  };
  for (const area of areas) visit(area.id, []);
}
const order = worklist.order ?? [];
if (order.length !== areas.length) fail(`order: ${order.length} lines for ${areas.length} areas`);
areas.forEach((area, index) => {
  if (!(typeof order[index] === 'string' && order[index].startsWith(`${area.id}: `) && order[index].length > area.id.length + 2))
    fail(`order line ${index + 1}: expected "${area.id}: <reason>"`);
});
const position = new Map(areas.map((area, index) => [area.id, index]));

// ---------------------------------------------------------------- coverage
// git's view of the range, per package (across both, a file that moved from durable to chord would be one rename).
const phase4 = (table.rows ?? []).filter(row => row.phase === 4);
const expected = new Map(), chordNamed = new Set(), chordInAreas = [];
if (!offline) {
const range = new Map(); // path -> { change, renamedFrom?, lines }
for (const pkg of ['packages/durable', 'packages/chord']) {
  for (const line of git('diff', '--name-status', '-M', FROM, TO, '--', pkg).trim().split('\n').filter(Boolean)) {
    const [status, a, b] = line.split('\t');
    if (status.startsWith('R')) range.set(b, { change: 'renamed', renamedFrom: a });
    else range.set(a, { change: { A: 'added', D: 'removed', M: 'modified' }[status[0]] ?? status });
  }
  const parts = git('diff', '--numstat', '-z', '-M', FROM, TO, '--', pkg).split('\0');
  for (let index = 0; index < parts.length; index++) {
    if (!parts[index]) continue;
    const [added, deleted, path] = parts[index].split('\t');
    const lines = (Number(added) || 0) + (Number(deleted) || 0);
    if (path === '') { range.get(parts[index + 2]).lines = lines; index += 2; } else range.get(path).lines = lines;
  }
}
// A rename git sees only as an add and a delete (upstream rewrote the file after renaming it) counts as a rename when the port map
// records it: the work list lists it once, under its new path.
for (const row of portMap.files ?? []) {
  const added = range.get(row.upstream), removed = row.renamedFrom && range.get(row.renamedFrom);
  if (row.upstreamChange === 'renamed' && added?.change === 'added' && removed?.change === 'removed') {
    const [a, d] = git('diff', '--numstat', `${FROM}:${row.renamedFrom}`, `${TO}:${row.upstream}`).trim().split('\t');
    range.set(row.upstream, { change: 'renamed', renamedFrom: row.renamedFrom, lines: (Number(a) || 0) + (Number(d) || 0) });
    range.delete(row.renamedFrom);
  }
}
const tree = commit => new Set(git('ls-tree', '-r', '--name-only', commit, '--', 'packages/chord').trim().split('\n').filter(Boolean));
const chordFiles = [...new Set([...tree(FROM), ...tree(TO)])];
for (const row of phase4) for (const path of row.upstream ?? []) if (path.startsWith(C)) {
  const matches = chordFiles.filter(file => file === path || file.startsWith(path.endsWith('/') ? path : path + '/'));
  if (!matches.length) fail(`row ${row.id}: names ${path}, which neither commit has`);
  for (const file of matches) chordNamed.add(file);
}
for (const [path, info] of range) if (path.startsWith(D) || path.startsWith(C)) expected.set(path, info);
for (const path of chordNamed) if (!expected.has(path)) expected.set(path, { change: 'unchanged', lines: 0 });

const seen = new Map(); // path -> places
const place = (path, where) => (seen.get(path) ?? seen.set(path, []).get(path)).push(where);
for (const area of areas) for (const entry of area.upstream ?? []) {
  place(entry.path, `area ${area.id}`);
  const info = expected.get(entry.path);
  if (!info) { fail(`area ${area.id}: ${entry.path} is not a file of the range or a file a phase 4 row names`); continue; }
  if (entry.change !== info.change) fail(`area ${area.id}: ${entry.path} is ${entry.change}, git says ${info.change}`);
  if (entry.lines !== info.lines) fail(`area ${area.id}: ${entry.path} has ${entry.lines} lines, git says ${info.lines}`);
  if ((entry.renamedFrom ?? null) !== (info.renamedFrom ?? null)) fail(`area ${area.id}: ${entry.path} renamedFrom ${entry.renamedFrom}, git says ${info.renamedFrom}`);
}
(worklist.excluded ?? []).forEach((group, index) => {
  if (!group.reason?.trim()) fail(`excluded entry ${index + 1}: no reason`);
  for (const path of group.paths ?? []) {
    place(path, `excluded ${index + 1}`);
    if (!expected.has(path)) fail(`excluded entry ${index + 1}: ${path} is not a file of the range or a file a phase 4 row names`);
    else if (path.startsWith(C) && !chordNamed.has(path) && !group.reason.startsWith('phase 6'))
      fail(`excluded entry ${index + 1}: ${path} changed but no phase 4 row names it; its reason must start with "phase 6"`);
  }
});
for (const [path] of expected) {
  const places = seen.get(path) ?? [];
  if (places.length !== 1) fail(`${path}: in ${places.length ? places.join(', ') : 'no area and no excluded entry'}; expected exactly one`);
  if (path.startsWith(C) && !chordNamed.has(path) && places.some(where => where.startsWith('area '))) chordInAreas.push(`${path} (${places[0]})`);
}
for (const row of portMap.files ?? [])
  if (['needs-sync', 'planned'].includes(row.status) && !seen.has(row.upstream))
    fail(`port map: ${row.status} row ${row.upstream} is in no area and no excluded entry`);

} // upstream-only coverage; offline still checks shape, rows, waves and local files

// ---------------------------------------------------------------- rows
const rowIds = new Set(phase4.map(row => row.id));
const rowPlaces = new Map();
for (const area of areas) for (const id of area.applicabilityRows ?? []) {
  (rowPlaces.get(id) ?? rowPlaces.set(id, []).get(id)).push(area.id);
  if (!rowIds.has(id)) fail(`area ${area.id}: ${id} is not a phase 4 applicability row`);
}
for (const id of rowIds) if ((rowPlaces.get(id) ?? []).length !== 1)
  fail(`row ${id}: in ${(rowPlaces.get(id) ?? []).join(', ') || 'no area'}; expected exactly one`);
const deferral = 'tools.windows-shell-install-dirs';
if (!(table.rows ?? []).some(row => row.id === deferral)) fail(`${deferral}: not in the applicability table`);
if (!areas.some(area => (area.notes ?? '').includes(deferral))) fail(`${deferral}: no area names it in its notes`);

// ---------------------------------------------------------------- waves
const waves = worklist.waves ?? [];
const waveOf = new Map();
waves.forEach((wave, index) => {
  if (!Array.isArray(wave) || !wave.length) fail(`wave ${index + 1}: empty`);
  if (wave.length > 5) fail(`wave ${index + 1}: ${wave.length} areas; at most 5`);
  for (const id of wave) {
    if (!byId.has(id)) fail(`wave ${index + 1}: unknown area ${id}`);
    if (waveOf.has(id)) fail(`area ${id}: in waves ${waveOf.get(id) + 1} and ${index + 1}`);
    waveOf.set(id, index);
  }
});
for (const area of areas) {
  if (!waveOf.has(area.id)) { fail(`area ${area.id}: in no wave`); continue; }
  for (const dependency of area.dependsOn ?? []) if (waveOf.has(dependency) && waveOf.get(dependency) >= waveOf.get(area.id))
    fail(`area ${area.id} (wave ${waveOf.get(area.id) + 1}) depends on ${dependency} (wave ${waveOf.get(dependency) + 1})`);
}
const filesOf = area => new Set((area.pisharp ?? []).map(bare).filter(path => !SHARED(path)));
for (let i = 0; i < areas.length; i++) for (let j = i + 1; j < areas.length; j++) {
  const [a, b] = [areas[i], areas[j]];
  if (!waveOf.has(a.id) || !waveOf.has(b.id)) continue;
  const shared = [...filesOf(a)].filter(path => filesOf(b).has(path));
  if (!shared.length) continue;
  if (waveOf.get(a.id) === waveOf.get(b.id)) fail(`wave ${waveOf.get(a.id) + 1}: ${a.id} and ${b.id} both change ${shared.slice(0, 3).join(', ')}`);
  else if (waveOf.get(a.id) > waveOf.get(b.id)) fail(`${a.id} comes before ${b.id} in order and both change ${shared[0]}, but runs in a later wave`);
}

// ---------------------------------------------------------------- files
for (const area of areas) {
  const paths = [...(area.pisharp ?? []), ...(area.tests ?? []).map(test => test.pisharp).filter(Boolean)];
  for (const path of paths) {
    if (/\\|^\//.test(path)) fail(`area ${area.id}: ${path} is not a repository-relative path`);
    if (path.endsWith(REMOVED)) { if (existsSync(join(root, bare(path)))) fail(`area ${area.id}: ${path} still exists`); }
    else if (!path.endsWith(NEW) && !existsSync(join(root, path))) fail(`area ${area.id}: ${path} does not exist; mark a file to create " (new)" or one a landed area deleted " (removed)"`);
  }
  for (const test of area.tests ?? []) if (!(area.upstream ?? []).some(entry => entry.path === test.upstream))
    fail(`area ${area.id}: tests names ${test.upstream}, which is not in its upstream list`);
  for (const entry of area.upstream ?? []) if (/\/test\//.test(entry.path) && entry.change !== 'removed' && !(area.tests ?? []).some(test => test.upstream === entry.path))
    fail(`area ${area.id}: upstream test file ${entry.path} is not in its tests`);
}

// ---------------------------------------------------------------- report
const covered = [...expected.keys()];
const excludedCount = (worklist.excluded ?? []).reduce((total, group) => total + (group.paths?.length ?? 0), 0);
console.log(`durable-sync work list ${worklistPath}: ${areas.length} areas in ${waves.length} waves, ${phase4.length} phase 4 rows`);
console.log(offline ? 'coverage: NOT RUN (explicit offline structural validation)' : `coverage: ${covered.filter(path => path.startsWith(D)).length} durable files and ${covered.filter(path => path.startsWith(C)).length} chord files (${chordNamed.size} named by phase 4 rows); ${excludedCount} excluded`);
if (chordInAreas.length) console.log(`chord files no phase 4 row names, placed in areas: ${chordInAreas.join('; ')}`);
if (errors.length) {
  for (const error of errors) console.error('FAIL ' + error);
  console.error(`${errors.length} problem(s)`);
  process.exit(1);
}
console.log('ok');
