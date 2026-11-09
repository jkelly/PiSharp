// Checks compatibility/sync-1.5.0-applicability.json: every changelog bullet maps to rows, every row's changelog
// agrees with the coverage map, statuses carry their evidence, phases and releases follow the area, and the
// summary counts match. Offline by default. With PI_UPSTREAM_V1_5_0 set to a Pi checkout that has v1.2.0 and
// v1.5.0, it also renumbers the bullets from the changelogs at v1.5.0 and checks every upstream path.
import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildStatus, validateStatus, attachCurrentMappings, statusPath } from './gap-status-mapping.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const json = path => JSON.parse(readFileSync(resolve(repo, path), 'utf8'));
const TABLE = 'compatibility/sync-1.5.0-applicability.json', VERSIONS = ['1.3.0', '1.4.0', '1.5.0'];
const table = json(TABLE), rowById = new Map(table.rows.map(row => [row.id, row]));
const PHASES = { ai: 2, schema: 2, schemas: 2, 'coding-agent-core': 2, 'coding-agent-cli': 2, tui: 2, codemode: 2, mcp: 2, tools: 2, 'node-bridge': 2, docs: 2, telemetry: 3, durable: 4, env: 5, chord: 6, evals: 7 };

test('current source mapping preserves historical evidence and separates runtime acceptance', () => {
  const current=json(statusPath);
  validateStatus(current);
  assert.deepEqual(current,buildStatus());
  for(const [path,expected] of Object.entries(attachCurrentMappings(current))) assert.deepEqual(json(path),expected,path);
});

// Top-level bullets under each ### section of ## [1.3.0] to ## [1.5.0], numbered per section: <package>@<version>/<section>#<n>.
export function changelogBullets(pkg, text) {
  const ids = []; let version = null, section = null, counts = {};
  for (const line of text.replace(/\r\n/g, '\n').split('\n')) {
    const heading = /^## \[([^\]]+)\]/.exec(line);
    if (heading) { version = VERSIONS.includes(heading[1]) ? heading[1] : null; section = null; counts = {}; continue; }
    if (!version) continue;
    const sub = /^### (.+)$/.exec(line);
    if (sub) { section = sub[1].trim(); continue; }
    if (line.startsWith('- ')) { counts[section] = (counts[section] ?? 0) + 1; ids.push(`${pkg}@${version}/${section}#${counts[section]}`); }
  }
  return ids;
}

test('the table spans the previous and current target locks', () => {
  const lock = json('compatibility/target.lock.json'), previous = json('compatibility/target.v1.2.0.lock.json');
  assert.deepEqual(table.from, { tag: previous.source.tag, commit: previous.source.commit, tree: previous.source.tree });
  assert.deepEqual(table.to, { tag: lock.source.tag, commit: lock.source.commit, tree: lock.source.tree });
  assert(existsSync(resolve(repo, table.decision)), table.decision);
});

test('every bullet maps to rows and every row agrees with the coverage map', () => {
  assert.equal(new Set(table.rows.map(row => row.id)).size, table.rows.length, 'unique row ids');
  const bullets = Object.keys(table.changelogCoverage);
  assert.equal(bullets.length, table.summary.changelogBullets);
  for (const [bullet, rows] of Object.entries(table.changelogCoverage)) {
    assert(rows.length > 0, bullet + ' has no rows');
    for (const id of rows) assert(rowById.has(id), bullet + ' names unknown row ' + id);
  }
  for (const row of table.rows) {
    const mapped = bullets.filter(bullet => table.changelogCoverage[bullet].includes(row.id));
    assert.deepEqual(row.changelog, mapped, row.id + ' changelog');
  }
  // A repeated bullet takes the rows of the bullet (or bullets) it repeats.
  for (const [bullet, value] of Object.entries(table.repeatedBullets)) {
    const repeated = [value].flat();
    for (const id of [bullet, ...repeated]) assert(id in table.changelogCoverage, bullet + ': unknown bullet ' + id);
    assert.deepEqual(table.changelogCoverage[bullet], [...new Set(repeated.flatMap(id => table.changelogCoverage[id]))], bullet + ' takes the rows of ' + repeated.join(', '));
  }
});

test('statuses, phases, releases and PiSharp paths', () => {
  assert.deepEqual(Object.keys(table.statusMeaning).sort(), ['already-ported', 'no-op', 'port', 'ported', 'ported-with-deferral']);
  assert.deepEqual(table.areas, Object.keys(PHASES));
  for (const row of table.rows) {
    assert(row.status in table.statusMeaning, row.id + ' status');
    if ((row.status === 'ported' || row.status === 'ported-with-deferral') && row.area === 'docs' && row.evidence.docs) {
      // A ported docs row names its commits and the pages it changed.
      assert(row.evidence.commits.length > 0 && row.evidence.commits.every(sha => /^[0-9a-f]{7,40}$/.test(sha)), row.id + ' commits');
      assert(row.evidence.docs.length > 0 && !('tests' in row.evidence), row.id + ' docs');
      for (const path of row.evidence.docs) assert(existsSync(resolve(repo, path)), row.id + ' names a missing page ' + path);
    } else if (row.status === 'ported' || row.status === 'ported-with-deferral') {
      // A ported row names its commits and its authored tests, each test as <file>: <case id> in a file that holds it,
      // literally or as an interpolated $"<prefix>.{name}" with "<name>" in the same file.
      assert(row.evidence.commits.length > 0 && row.evidence.commits.every(sha => /^[0-9a-f]{7,40}$/.test(sha)), row.id + ' commits');
      assert(row.evidence.tests.length > 0, row.id + ' needs tests');
      for (const historicalEntry of row.evidence.tests) {
        // A renamed current authored test must not erase historical release evidence or imply that the new test passed.
        // The dated current mapping records such replacements with an explicit pending runtime status.
        const replacement = row.currentImplementation?.testReplacements?.[historicalEntry];
        if (replacement) assert.equal(row.currentImplementation.runtimeStatus, 'pending-integrated-validation', row.id + ' replacement is source evidence only');
        const entry = replacement ?? historicalEntry;
        const [path, caseId] = entry.split(/: (.*)/s), cut = caseId.lastIndexOf('.') + 1;
        assert(existsSync(resolve(repo, path)), row.id + ' names a missing test file ' + path);
        const text = readFileSync(resolve(repo, path), 'utf8');
        assert(text.includes(caseId) || (text.includes(caseId.slice(0, cut) + '{') && text.includes(`"${caseId.slice(cut)}"`)), row.id + ' names a missing case ' + entry);
      }
    } else if (row.status !== 'port') assert.equal(typeof row.evidence, 'string', row.id + ' needs evidence');
    assert.equal(typeof row.deferred === 'string', row.status === 'ported-with-deferral', row.id + ' deferred');
    // A row another area cannot be ported without takes that area's earlier phase (pulledInBy).
    if (row.pulledInBy) {
      for (const id of row.pulledInBy) assert(rowById.has(id), row.id + ' is pulled in by unknown row ' + id);
      const phase = Math.min(...row.pulledInBy.map(id => rowById.get(id).phase));
      assert(phase < PHASES[row.area], row.id + ' pulledInBy names no earlier phase');
      assert.equal(row.phase, phase, row.id + ' phase');
    } else assert.equal(row.phase, PHASES[row.area], row.id + ' phase');
    assert.equal(row.release, row.phase >= 5 ? '1.5.0.x' : '1.5.0', row.id + ' release');
    for (const path of row.pisharp) if (!/\(new\b/.test(path)) assert(existsSync(resolve(repo, path.replace(/:\d+(-\d+)?$/, ''))), row.id + ' names a missing PiSharp path ' + path);
  }
  const count = key => table.rows.reduce((acc, row) => (acc[row[key]] = (acc[row[key]] ?? 0) + 1, acc), {});
  assert.deepEqual(table.summary, { rows: table.rows.length, changelogBullets: Object.keys(table.changelogCoverage).length, repeatedBullets: Object.keys(table.repeatedBullets).length,
    byStatus: count('status'), byArea: count('area'), byPhase: count('phase'), byRelease: count('release') });
});

const upstream = process.env.PI_UPSTREAM_V1_5_0;
if (process.env.PI_REQUIRE_UPSTREAM === '1' && !upstream) throw new Error('PI_REQUIRE_UPSTREAM requires PI_UPSTREAM_V1_5_0; refusing a skipped source check');
test('bullets and upstream paths match the changelogs and tree at v1.5.0', { skip: upstream ? false : 'set PI_UPSTREAM_V1_5_0 to a Pi checkout with v1.2.0 and v1.5.0' }, () => {
  const git = (...args) => execFileSync('git', ['-C', upstream, ...args], { maxBuffer: 1 << 28 }).toString();
  assert.equal(git('rev-parse', 'v1.5.0^{commit}').trim(), table.to.commit);
  const files = git('ls-tree', '-r', '--name-only', table.to.commit).split('\n').filter(Boolean), tree = new Set(files);
  const bullets = files.filter(path => /^packages\/[^/]+\/CHANGELOG\.md$/.test(path)).flatMap(path => changelogBullets(path.split('/')[1], git('show', table.to.commit + ':' + path)));
  assert.deepEqual(Object.keys(table.changelogCoverage), bullets);
  // Paths the range deleted or renamed away (the rename's old path), as the re-pin tool reports them.
  const removed = new Set(git('diff', '-M', '--name-status', '--diff-filter=DR', table.from.commit, table.to.commit).split('\n').filter(Boolean).map(line => line.split('\t')[1]));
  const glob = pattern => new RegExp('^' + pattern.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*\*/g, '\0').replace(/\*/g, '[^/]*').replace(/\0/g, '.*') + '$');
  for (const row of table.rows) for (const path of row.upstream) {
    // A path may be a file or directory at v1.5.0, or one the range removed.
    const dir = path.replace(/\/$/, '') + '/';
    const found = path.includes('*') ? files.some(file => glob(path).test(file)) : tree.has(path) || removed.has(path) || [...files, ...removed].some(file => file.startsWith(dir));
    assert(found, row.id + ' names a missing upstream path ' + path);
  }
});
