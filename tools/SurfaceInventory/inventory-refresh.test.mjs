// Validates the preserved v0.99.1 event catalog and command/RPC inventory and their v1.1.0
// refreshes. Offline by default: it reads only repository files. With PI_UPSTREAM set to a Pi git
// checkout that contains both refs, it also re-runs both generators and requires byte-identical
// output for v0.99.1 (preserved evidence) and v1.1.0 (current inventories).
import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { sha256, applyOverlay as applyCatalogOverlay } from './build-event-catalog.mjs';
import { mapLines, parserBranches, rootExports, serialize, unionMembers } from './build-command-rpc-inventory.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const bytes = path => readFileSync(resolve(repo, path));
const json = path => JSON.parse(bytes(path).toString('utf8'));
const OLD_CATALOG = 'compatibility/extensions/event-catalog.json', NEW_CATALOG = 'compatibility/extensions/event-catalog.v1.1.0.json';
const OLD_INVENTORY = 'compatibility/coding-agent-command-rpc-inventory.json', NEW_INVENTORY = 'compatibility/coding-agent-command-rpc-inventory.v1.1.0.json';
const CATALOG_OVERLAY = 'compatibility/extensions/event-catalog.v1.1.0.overlay.json', INVENTORY_OVERLAY = 'compatibility/coding-agent-command-rpc-inventory.v1.1.0.overlay.json';
const baseline = json('compatibility/baseline.lock.json'), target = json('compatibility/target.lock.json');
const lockPin = (lock, path) => lock.artifacts.find(row => row.kind === 'source-file' && row.path === path);

// Every cited test case must exist in the named repository file (literally or as Prefix + "suffix").
function assertTestReference(reference, where) {
  for (const key of ['suite', 'file', 'case']) assert.equal(typeof reference[key], 'string', where + ' lacks ' + key);
  assert(reference.file.startsWith(reference.suite + '/'), where + ': file outside suite');
  assert(existsSync(resolve(repo, reference.file)), where + ': missing ' + reference.file);
  const text = bytes(reference.file).toString('utf8');
  if (reference.sourcePrefix !== undefined) {
    assert.equal(reference.sourcePrefix + reference.sourceSuffix, reference.case, where);
    assert(text.includes('"' + reference.sourcePrefix + '"') && text.includes('"' + reference.sourceSuffix + '"'), where + ': case not found ' + reference.case);
  } else assert(text.includes('"' + reference.case + '"'), where + ': case not found ' + reference.case);
  if (reference.implementationFile) assert(existsSync(resolve(repo, reference.implementationFile)), where + ': missing ' + reference.implementationFile);
}
const references = (value, found = []) => {
  if (Array.isArray(value)) value.forEach(item => references(item, found));
  else if (value && typeof value === 'object') { if (typeof value.case === 'string' && typeof value.file === 'string') found.push(value); Object.values(value).forEach(item => references(item, found)); }
  return found;
};

test('preserved v0.99.1 inventories keep their pinned bytes', () => {
  assert.equal(sha256(bytes(OLD_INVENTORY)), 'b45c7e117fdb46dde823de9c81717349fdc09fb0b265b706f6433afc13484a14');
  const plan = json('compatibility/public-entrypoints.plan.json');
  assert(JSON.stringify(plan).includes(sha256(bytes(OLD_INVENTORY))), 'public-entrypoints plan pin');
  assert.equal(sha256(bytes(OLD_CATALOG)), '3ef7def6ae9784777e18fbf7fafb88e1dd5480e7c0f83df9034e185b174a6c97');
  for (const [path, file] of [[OLD_CATALOG, json(OLD_CATALOG)], [OLD_INVENTORY, json(OLD_INVENTORY)]]) assert.equal(file.sourceSha, baseline.source.commit, path);
});

test('v1.1.0 event catalog: complete re-derived rows at the target commit', () => {
  const old = json(OLD_CATALOG), current = json(NEW_CATALOG);
  assert.equal(current.sourceSha, target.source.commit); assert.equal(current.sourceTag, target.source.tag);
  assert.deepEqual(current.refresh.previousCatalog, { path: OLD_CATALOG, sha256: sha256(bytes(OLD_CATALOG)), sourceSha: baseline.source.commit });
  assert.deepEqual(current.events.map(row => row.name), old.events.map(row => row.name), 'Same 41 events in source order');
  assert.equal(current.gates.mandatoryEvents, 41); assert.equal(current.authority.requiredOverloads, 41);
  for (const file of current.authority.sourceFiles) {
    assert(file.sourceUrl.includes('/' + target.source.commit + '/'), file.path);
    const pin = lockPin(target, file.path); if (pin) assert.equal(file.sha256, pin.sha256, file.path + ' agrees with target.lock');
  }
  for (const row of current.events) {
    for (const field of ['allowedModes', 'context', 'ordering', 'snapshot', 'reducer', 'validation', 'failure', 'cancellation', 'timeout', 'persistence', 'reentrancy', 'fixtures', 'actualStatus']) assert(field in row, row.name + ' ' + field);
    assert.equal(row.actualStatus.mandatoryRowRemoved, false);
    assert.equal(row.sourceRefresh.previousSourceSha, baseline.source.commit); assert.equal(row.sourceRefresh.removedUpstream, false);
    for (const key of ['eventDeclaration', 'subscription', 'dispatcher']) {
      const span = row.source[key];
      assert(span.startLine <= span.endLine && span.startUtf16 < span.endUtf16 && /^[0-9a-f]{64}$/.test(span.sha256), row.name + ' ' + key);
    }
    const previous = old.events.find(item => item.name === row.name).source;
    assert.equal(row.sourceRefresh.eventDeclarationTextChanged, previous.eventDeclaration.sha256 !== row.source.eventDeclaration.sha256);
    assert.equal(row.sourceRefresh.dispatcherTextChanged, previous.dispatcher.sha256 !== row.source.dispatcher.sha256);
  }
  assert.deepEqual(current.refresh.summary.eventDeclarationTextChanged, ['agent_settled', 'tool_execution_end']);
  assert.deepEqual([current.refresh.summary.added, current.refresh.summary.removed], [[], []]);
  // The spans recorded ahead of the refresh in the sync applicability ledger agree with the catalog.
  for (const row of json('compatibility/sync-1.1.0-applicability.json').rows.filter(item => item.eventCatalogRefresh)) {
    for (const [id, spans] of Object.entries(row.eventCatalogRefresh)) {
      if (id === 'note') continue;
      const event = current.events.find(item => item.id === id); assert(event, id);
      for (const [key, span] of Object.entries(spans)) for (const [field, value] of Object.entries(span)) assert.equal(event.source[key][field], value, id + ' ' + key + '.' + field);
    }
  }
});

test('v1.1.0 event catalog: native status cites existing authored tests', () => {
  const current = json(NEW_CATALOG), vocabulary = Object.keys(current.nativeProfile.statusVocabulary);
  for (const row of current.events) {
    assert(vocabulary.includes(row.actualStatus.nativeHostIntegration), row.name + ' host status ' + row.actualStatus.nativeHostIntegration);
    if (row.actualStatus.nativeHostIntegration !== 'Deferred') assert(row.nativeEvidence, row.name + ' evidence');
    if (row.actualStatus.nativeHostIntegration === 'ImplementedWithAuthoredTests') assert(row.nativeEvidence.tests.length > 0, row.name);
  }
  const settled = current.events.find(row => row.name === 'agent_settled');
  assert.equal(settled.actualStatus.nativeHostIntegration, 'ImplementedWithAuthoredTests');
  assert.equal(current.events.find(row => row.name === 'tool_execution_end').actualStatus.nativeHostIntegration, 'WireOnlyNotDispatchedToExtensions');
  const cited = references(current); assert(cited.length > 0);
  cited.forEach((reference, index) => assertTestReference(reference, 'catalog test ' + index));
  // The overlay is the only authored difference beyond re-derived source evidence and refresh notes.
  const overlay = json(CATALOG_OVERLAY), reapplied = applyCatalogOverlay(structuredClone(current), overlay);
  assert.deepEqual(reapplied, current, 'Overlay already applied and idempotent');
});

test('v1.1.0 command/RPC inventory: rows, counts and pins at the target commit', () => {
  const old = json(OLD_INVENTORY), current = json(NEW_INVENTORY);
  assert.equal(serialize(current), bytes(NEW_INVENTORY).toString('utf8'), 'Canonical one-row-per-line layout');
  assert.equal(current.sourceSha, target.source.commit); assert.equal(current.sourceTree, target.source.tree);
  assert.equal(current.baselineId, 'pi-' + target.source.tag + '@' + target.source.commit);
  assert.deepEqual(current.previousInventory, { path: OLD_INVENTORY, sha256: sha256(bytes(OLD_INVENTORY)), baselineId: old.baselineId });
  const ids = current.rows.map(row => row.id);
  assert.equal(new Set(ids).size, ids.length);
  for (const row of old.rows) assert(ids.includes(row.id), 'v0.99.1 row kept: ' + row.id);
  assert.deepEqual(current.refresh.summary.added, ['sdk.root.ToolRendererResolver', 'sdk.root.ToolRenderers', 'sdk.root.QuietStartup', 'cli.flag.no-mcp']);
  assert.deepEqual(current.refresh.summary.removed, []);
  const checks = current.completenessChecks;
  assert.equal(checks.rowCount, current.rows.length); assert.equal(checks.rowCount, 716);
  assert.equal(Object.values(checks.classificationCounts).reduce((sum, count) => sum + count, 0), checks.rowCount);
  assert.equal(checks.coreParserPrimaryFlags, 42); assert.equal(checks.coreParserSpellingsIncludingAliases, 59);
  assert.equal(checks.sdkRootNamedExports, 460); assert.equal(checks.sdkRootRuntimeValues + checks.sdkRootTypeOnlyExports, 460);
  assert.equal(checks.rpcCommandVariants, 33); assert.equal(checks.rpcDispatchCases, 33);
  for (const file of current.sourceFiles) { const pin = lockPin(target, file.path); if (pin) assert.equal(file.sha256, pin.sha256, file.path); }
  const fileSha = new Map(current.sourceFiles.map(file => [file.path, file.sha256]));
  for (const row of current.rows) {
    assert.equal(row.baselineId, current.baselineId, row.id);
    for (const ref of Object.values(row).filter(value => value && typeof value === 'object' && value.location)) {
      assert.equal(ref.sha256, fileSha.get(ref.path), row.id + ' ' + ref.path);
      assert.equal(ref.url, 'https://github.com/earendil-works/pi/blob/' + target.source.commit + '/' + ref.path + '#L' + ref.location.startLine, row.id);
      assert(ref.location.startLine <= ref.location.endLine && ref.location.startOffset < ref.location.endOffset, row.id);
    }
  }
  const settled = current.rows.find(row => row.id === 'rpc.event.agent_settled');
  assert.equal(settled.declaration, '{ type: "agent_settled"; aborted: boolean }');
  assert(current.rows.find(row => row.id === 'rpc.event.tool_execution_end').declaration.includes('durationMs?: number;'));
});

test('v1.1.0 command/RPC inventory: native status cites existing authored tests', () => {
  const current = json(NEW_INVENTORY), policy = current.nativeStatusPolicy, vocabulary = Object.keys(policy.vocabulary), counts = {};
  for (const row of current.rows.filter(item => item.nativeStatus)) {
    const status = row.nativeStatus.status; counts[status] = (counts[status] ?? 0) + 1;
    assert(vocabulary.includes(status), row.id + ' ' + status);
    if (status === 'Implemented') assert(row.nativeStatus.tests.length > 0, row.id + ' implemented without a test');
    assert.equal(row.implementationEvidenceState, row.nativeStatus.tests.length && ['Implemented', 'Partial'].includes(status) ? 'AuthoredTests' : 'Unverified', row.id);
    assert.equal(row.implementationAcceptance, 'Deferred', row.id);
  }
  assert.deepEqual(counts, policy.counts);
  for (const id of ['rpc.command.bash', 'rpc.command.abort_bash', 'rpc.event.bash_execution_update', 'rpc.event.agent_settled', 'rpc.event.tool_execution_end', 'cli.help.command.mcp', 'cli.slash.login', 'cli.slash.logout', 'cli.flag.no-mcp'])
    assert.equal(current.rows.find(row => row.id === id).nativeStatus.status, 'Implemented', id);
  assert.deepEqual(current.nativeOnlySurfaces.map(row => row.id), ['pisharp.stderr.mcp_diagnostic', 'pisharp.failure.LiveAuthenticationFailed', 'pisharp.failure.LiveAzureEndpoint']);
  const cited = references(current); assert(cited.length > 0);
  cited.forEach((reference, index) => assertTestReference(reference, 'inventory test ' + index));
  const overlay = json(INVENTORY_OVERLAY);
  for (const id of Object.keys(overlay.rows)) assert(current.rows.some(row => row.id === id), 'overlay row ' + id);
});

test('generator helpers: line mapping, censuses and comment-safe export spans', () => {
  assert.deepEqual([...mapLines('a\nb\nc\nd', 'a\nx\nb\nd')], [0, 2, -1, 3]);
  const union = 'export type E =\n\t| { type: "a" }\n\t// note {\n\t| Wrap<Exclude<X, { type: "z" }>>\n\t| {\n\t\t\ttype: "b";\n\t\t\tn?: number;\n\t  };\n';
  assert.deepEqual(unionMembers(union, 'E').map(row => [row.type, union.slice(row.start, row.end)]), [['a', '{ type: "a" }'], ['b', '{\n\t\t\ttype: "b";\n\t\t\tn?: number;\n\t  }']]);
  const index = 'export {\n\t// Factory: tools for cwd\n\ttype A,\n\tb as c,\n} from "./x.ts";\nexport type { D } from "./y.ts";\n';
  assert.deepEqual(rootExports(index).map(row => [row.name, row.originalSymbol, row.typeOnly, row.origin, index.slice(row.start, row.end)]),
    [['A', 'A', true, './x.ts', 'type A'], ['c', 'b', false, './x.ts', 'b as c'], ['D', 'D', true, './y.ts', 'D']]);
  const args = 'export function parseArgs() {\n\tfor (;;) {\n\t\tif (arg === "--") {\n\t\t\tbreak;\n\t\t} else if (arg === "--a" || arg === "-a") {\n\t\t\tx = 1;\n\t\t\tif (arg === "--nested") {}\n\t\t} else if (arg === "--b") {\n\t\t}\n\t}\n}\n';
  assert.deepEqual(parserBranches(args).map(row => row.spellings), [['--'], ['--a', '-a'], ['--b']]);
});

const upstream = process.env.PI_UPSTREAM;
test('generators reproduce every inventory byte-for-byte from upstream sources', { skip: upstream ? false : 'set PI_UPSTREAM to a Pi checkout containing v0.99.1 and v1.1.0' }, () => {
  const run = (tool, ...args) => execFileSync(process.execPath, [resolve(repo, 'tools/SurfaceInventory', tool), '--upstream', upstream, ...args], { cwd: repo, stdio: 'pipe' });
  run('build-event-catalog.mjs', '--lock', 'compatibility/baseline.lock.json', '--template', OLD_CATALOG, '--check', OLD_CATALOG);
  run('build-event-catalog.mjs', '--lock', 'compatibility/target.lock.json', '--template', OLD_CATALOG, '--overlay', CATALOG_OVERLAY, '--check', NEW_CATALOG);
  run('build-command-rpc-inventory.mjs', '--lock', 'compatibility/baseline.lock.json', '--template', OLD_INVENTORY, '--check', OLD_INVENTORY);
  run('build-command-rpc-inventory.mjs', '--lock', 'compatibility/target.lock.json', '--template', OLD_INVENTORY, '--overlay', INVENTORY_OVERLAY, '--check', NEW_INVENTORY);
});
