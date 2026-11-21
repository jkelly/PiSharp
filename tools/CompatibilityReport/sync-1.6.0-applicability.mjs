#!/usr/bin/env node
// Candidate source disposition for the v1.5.2..v1.6.0 sync. Root owns integrated validation and final status changes.
// Successor of sync-1.5.2-applicability.mjs: the same table shape and no-op category/reason scheme, with the version, the
// work packages and the owner mapping in the SYNC block below. The 1.5.2 generator and its accepted table stay as they are.
//
//   node tools/CompatibilityReport/sync-1.6.0-applicability.mjs            write the table, the Durable overlay and the candidate pointer
//   node tools/CompatibilityReport/sync-1.6.0-applicability.mjs --check    regenerate in memory and fail when the files on disk differ
//
// The generator writes a fresh pending snapshot. It refuses to overwrite a table that carries an acceptance record or a
// status it did not write, unless --reset-pending is given.
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { dispositions } from './sync-1.6.0-dispositions.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const read = path => JSON.parse(readFileSync(resolve(repo, path), 'utf8'));
const PENDING = 'implementation-pending-validation', NO_OP = 'no-op';

const SYNC = {
  version: '1.6.0', rowPrefix: 'sync160',
  generatedBy: 'tools/CompatibilityReport/sync-1.6.0-applicability.mjs',
  decision: 'docs/decisions/0015-pi-1.6.0-sync.md', plan: 'AI_DOCS/sync-1.6.0.md',
  inventory: 'compatibility/sync-1.6.0-files.json', table: 'compatibility/sync-1.6.0-applicability.json',
  preservedPrevious: 'compatibility/sync-1.5.2-applicability.json',
  durable: { map: 'compatibility/durable-port-map.json', preservedMap: 'compatibility/durable-port-map.v1.5.0.json',
    overlay: 'compatibility/durable-sync-1.6.0-overlay.json', previousOverlay: 'compatibility/durable-sync-1.5.2-overlay.json' },
  report: id => `AI_DOCS/agent-reports/sync-1.6.0-${id}.md`,
};

// The work packages of AI_DOCS/sync-1.6.0.md, with packages/chord split into the seven chord-* packages of
// AI_DOCS/agent-reports/sync-1.6.0-chord-discovery.md section 4 (their plan is that report).
const packages = {
  meta: { area: 'source lock, file inventory, applicability table, version pins, Node runtime pins, virtual modules, decision record', branch: 'sync/1.6.0-meta' },
  'ai-catalog': { area: 'exact 1.6.0 model catalog, default models, Qwen Token Plan providers and key variables', branch: 'sync/1.6.0-ai-catalog' },
  'ai-providers': { area: 'provider wire behavior, retries, strict-mode fallbacks, session resources', branch: 'sync/1.6.0-ai-providers' },
  'core-small': { area: 'agent abort results, env listing and daemon output, MCP server summary', branch: 'sync/1.6.0-core-small' },
  tui: { area: 'terminal UI library', branch: 'sync/1.6.0-tui' },
  'cli-tools': { area: 'find, read, grep, bash output, exec exit codes, HTML export', branch: 'sync/1.6.0-cli-tools' },
  'cli-session': { area: 'session runtime, modes, diagnostics, packages, extensions, llama.cpp and MCP extensions', branch: 'sync/1.6.0-cli-session' },
  'cli-ui': { area: 'interactive components and interactive mode', branch: 'sync/1.6.0-cli-ui', dependsOn: ['tui', 'cli-session'] },
  'durable-core': { area: 'Durable harness: signals, parks, gates, hook tasks, record versions', branch: 'sync/1.6.0-durable-core' },
  'durable-edge': { area: 'code-mode namespaces, image fitting, durable tools, env listing, examples, hosts built on Durable', branch: 'sync/1.6.0-durable-edge', dependsOn: ['durable-core'] },
  'chord-foundation': { area: 'Chord contracts, data, wire messages and codecs, channel fences, framed sources, telemetry schema rows', branch: 'sync/1.6.0-chord-foundation', chord: true },
  'chord-edge': { area: 'Chord HTTP links, app kit sign-ins, plugins, surfaces, client and connection loop, sync, telemetry ingest admission', branch: 'sync/1.6.0-chord-edge', chord: true },
  'chord-store': { area: 'Chord store: column sets, exists and joins, read plans, the read index and skips, open and live queries, settled and onCommitted', branch: 'sync/1.6.0-chord-store', chord: true, dependsOn: ['chord-foundation'] },
  'chord-remote': { area: 'Chord remote layer: call windows by trust, events end to end, opened(incarnation), shared holds', branch: 'sync/1.6.0-chord-remote', chord: true, dependsOn: ['chord-foundation'] },
  'chord-queries': { area: 'Chord named queries, query readers, read declarations, lists, tiling and the reusable park step', branch: 'sync/1.6.0-chord-queries', chord: true, dependsOn: ['chord-store', 'chord-remote'] },
  'chord-admission': { area: 'Chord admission, route admission and the denied paths on both sides of a link', branch: 'sync/1.6.0-chord-admission', chord: true, dependsOn: ['chord-queries'] },
  'chord-workers': { area: 'Chord worker exit, store and incarnation, worker admission, TerminalScope.On', branch: 'sync/1.6.0-chord-workers', chord: true, dependsOn: ['chord-store', 'chord-remote', 'chord-admission'] },
};
const workPackages = Object.fromEntries(Object.entries(packages).map(([id, { chord, ...value }]) =>
  [id, { ...value, reports: [SYNC.report(id)], plan: chord ? SYNC.report('chord-discovery') : SYNC.plan, status: PENDING }]));

// Upstream tests follow the source they exercise; a test of several areas goes to the area whose change it covers.
const aiCatalogTests = new Set(['fireworks-models.test.ts', 'generate-models-strict.test.ts', 'model-data-validation.test.ts',
  'providers.test.ts', 'qwen-token-plan-models.test.ts', 'supports-xhigh.test.ts', 'together-models.test.ts']);
const cliToolsTests = new Set(['exec.test.ts', 'export-html-ansi-links.test.ts', 'export-html-whitespace.test.ts', 'read-streaming.test.ts',
  'tools.test.ts', 'suite/regressions/12066-find-ancestor-glob.test.ts']);
const cliUiTests = new Set(['assistant-message.test.ts', 'auth-url-copy.test.ts', 'config-selector.test.ts',
  'interactive-mode-assistant-diagnostics.test.ts', 'model-selector.test.ts', 'oauth-selector.test.ts', 'scoped-models-selector.test.ts',
  'session-selector-path-delete.test.ts', 'session-selector-rename.test.ts', 'session-selector-selection.test.ts', 'settings-submenu.test.ts',
  'syntax-highlight.test.ts', 'tool-renderer-examples.test.ts', 'user-message.test.ts', 'suite/regressions/7829-invalid-settings-warning.test.ts']);
const durableEdgeTests = new Set(['codemode-describe.test.ts', 'codemode.test.ts', 'env-node-list-race.test.ts', 'image-fixtures.ts',
  'images-fit.test.ts', 'tools-images.test.ts', 'tools.test.ts']);

// The reviewed module names the package for every packages/chord path and for the paths where a report moved the work.
function owner(path) {
  const reviewed = dispositions[path]?.workPackage;
  if (reviewed) return reviewed;
  const match = /^packages\/([^/]+)\/(.*)/.exec(path);
  if (!match) return 'meta';
  const [, pkg, relative] = match;
  if (/(?:^|\/)package\.json$/.test(relative) && !/(?:^|\/)(?:examples|test)\//.test(relative)) return 'meta';
  switch (pkg) {
    case 'ai':
      if (relative === 'src/utils/version.ts') return 'meta';
      if (/^(?:catalog|scripts)\//.test(relative) || relative === 'src/env-api-keys.ts') return 'ai-catalog';
      if (/^src\/providers\//.test(relative)) return relative === 'src/providers/compat-schema.ts' ? 'ai-providers' : 'ai-catalog';
      if (/^test\//.test(relative)) return aiCatalogTests.has(relative.slice('test/'.length)) ? 'ai-catalog' : 'ai-providers';
      return 'ai-providers';
    case 'agent': case 'env': case 'mcp': return 'core-small';
    case 'tui': return 'tui';
    case 'coding-agent':
      if (/^src\/core\/(?:tools\/|export-html\/|exec\.ts$)/.test(relative)) return 'cli-tools';
      if (/^src\/modes\/interactive\//.test(relative)) return 'cli-ui';
      if (/^test\//.test(relative)) {
        const name = relative.slice('test/'.length);
        return cliToolsTests.has(name) ? 'cli-tools' : cliUiTests.has(name) ? 'cli-ui' : 'cli-session';
      }
      return 'cli-session';
    case 'durable':
      if (/^src\/(?:codemode|images|tools|env)\//.test(relative) || /^test\/examples\//.test(relative)) return 'durable-edge';
      if (/^test\//.test(relative)) return durableEdgeTests.has(relative.slice('test/'.length)) ? 'durable-edge' : 'durable-core';
      return 'durable-core';
    case 'chord': throw new Error(`${path}: every packages/chord path needs a reviewed chord-* package in sync-1.6.0-dispositions.mjs`);
    default: return 'meta'; // codemode, schema, telemetry and evals change only their changelog and manifest in this range.
  }
}

// Reviewed no-op categories (sync-1.6.0-dispositions.mjs), beside the mechanical 1.5.2 categories that mechanical() returns.
const reviewedCategories = {
  'typescript-only': 'Type-level, cast or barrel-export change with no emitted runtime behavior; the native public surface is the owning package\'s.',
  'node-runtime-only': 'Behavior specific to the Node runtime or a JavaScript SDK, with no native counterpart.',
  'no-native-delta': 'A comment, a removed piece or a refactor with no behavior change: nothing native to update.',
  'version-only': 'Package version and sibling dependency versions only; the native version is Directory.Build.props VersionPrefix.',
};
const mechanicalCategories = new Set(['upstream-app', 'example-or-fixture', 'typescript-ambient', 'upstream-documentation', 'browser-dom-react',
  'browser-indexeddb', 'javascript-terminal-sandbox', 'upstream-build-tooling', 'evals-version-only']);

// A reviewed entry decides first: a reviewed no-op takes its category and reason, and any other reviewed finding keeps the
// row applicable even where the path rule would call it a no-op. Otherwise the 1.5.2 no-op categories and reasons decide by
// path alone (mechanical), and anything else stays pending with its owner.
function disposition(path) {
  const reviewed = dispositions[path];
  if (reviewed?.finding === NO_OP) return [reviewed.category, reviewed.reason];
  if (reviewed?.finding) return null;
  return mechanical(path);
}
function mechanical(path) {
  if (path.startsWith('apps/')) return ['upstream-app', 'Upstream application product code/assets are not PiSharp products. Shared library behavior is tracked independently under packages/chord; this does not exclude the native Chord App library.'];
  // packages/durable/test/examples is assigned to durable-edge by the work plan, so it is not a mechanical no-op.
  if (/(?:^|\/)(?:examples|bench|benchmarks|fixtures)\//.test(path) && !path.startsWith('packages/durable/test/examples/')) return ['example-or-fixture', 'Upstream examples, benchmarks and fixture files are reference material, not shipped C# runtime source. Applicable contracts and authored/captured native evidence belong to the corresponding area; no fixture is relabeled or recaptured by this mapping.'];
  if (/\.d\.ts$/.test(path)) return ['typescript-ambient', 'Ambient TypeScript declarations have no emitted runtime behavior. Native public API behavior remains the corresponding work package’s responsibility.'];
  if (/(?:^|\/)(?:docs)\//.test(path) || /(?:^|\/)(?:README|CHANGELOG|CONTRIBUTING|AGENTS)\.md$/.test(path)) return ['upstream-documentation', 'Upstream prose is source reference, not runtime behavior. Candidate release/compatibility documentation is maintained separately; behavioral changes described here remain assigned to their area.'];
  if (/^packages\/chord\/src\/(?:react\/|web\/)/.test(path)) return ['browser-dom-react', 'React hooks and browser DOM/page adapters require JavaScript browser execution. Native portable client/publication contracts are tracked in the Chord work packages; no CLR or ALC substitute is claimed to execute DOM or React.'];
  if (/^packages\/chord\/test\/node\/(?:react\/|web\/(?:browser|page|styles|support))/.test(path)) return ['browser-dom-react', 'Upstream React/DOM browser adapter tests require the actual browser runtime. Native portable client/publication contracts and their own tests remain separately assigned.'];
  if (path === 'packages/durable/src/storage/indexeddb.ts' || path === 'packages/durable/test/indexeddb-storage.test.ts') return ['browser-indexeddb', 'Browser IndexedDB backend and its browser test harness cannot run in the native .NET storage host. Native JSONL, SQLite and store backends remain in scope.'];
  if (path === 'packages/chord/src/node/tui/sandbox.ts') return ['javascript-terminal-sandbox', 'Secure downloaded-JavaScript execution has no native CLR permission-sandbox equivalent and fails explicitly. Native terminal framing, launcher, filters, actions and credentials stay with the Chord terminal work.'];
  if (/^(?:\.github\/|\.pi\/|nix\/|scripts\/)/.test(path) || path === 'test.sh' || /(?:^|\/)(?:tsconfig[^/]*\.json|vitest\.config\.ts|biome\.json|\.npmrc|package-lock\.json)$/.test(path)) return ['upstream-build-tooling', 'Upstream JavaScript workspace, release/CI, developer or Nix tooling is not the native build system. Package dependency/runtime pin effects are reviewed separately; native build and release tooling is retained.'];
  if (path === 'packages/evals/package.json') return ['evals-version-only', 'The frozen diff changes only the version and the pi-ai/pi-coding-agent development dependency versions, with no evaluation algorithm delta. Native package versioning is owned by meta.'];
  return null;
}

// What the reports state about a row, carried beside its status. It is never a status: only root's acceptance moves a row.
function review(path) {
  const { workPackage, category, reason, ...rest } = dispositions[path] ?? {};
  return rest.finding ? rest : null;
}
const FINDINGS = new Set(['ported', 'already-ported', 'not-in-report', 'pending-package', NO_OP]);

function build() {
  const inventory = read(SYNC.inventory);
  const paths = new Set(inventory.files.map(file => (file.after ?? file.before).path));
  for (const [path, entry] of Object.entries(dispositions)) {
    if (!paths.has(path)) throw new Error(`${path}: a reviewed disposition for a path this range did not change`);
    if (entry.finding && !FINDINGS.has(entry.finding)) throw new Error(`${path}: unknown finding ${entry.finding}`);
    for (const id of [entry.workPackage, ...(entry.owners ?? []), ...(entry.pendingParts ?? [])].filter(Boolean))
      if (!workPackages[id]) throw new Error(`${path}: unknown work package ${id}`);
    if (entry.finding === NO_OP && (!entry.reason || !(entry.category in reviewedCategories || mechanicalCategories.has(entry.category))))
      throw new Error(`${path}: a reviewed no-op needs a known category and a reason`);
  }
  const rows = inventory.files.map((file, index) => {
    const path = (file.after ?? file.before).path;
    const id = owner(path), noOp = disposition(path), reviewed = review(path);
    // A file several packages changed names each of their reports.
    const reports = [...new Set([...(reviewed?.owners ?? []), id].flatMap(name => workPackages[name].reports))];
    return {
      id: `${SYNC.rowPrefix}.file.${String(index + 1).padStart(4, '0')}`, upstream: [path], change: file.status,
      workPackage: id, area: path.startsWith('apps/') ? 'apps' : /^packages\/([^/]+)\//.exec(path)?.[1] ?? 'root', reports,
      status: noOp ? NO_OP : PENDING,
      ...(noOp ? { category: noOp[0], reason: noOp[1] } : { reason: 'Applicable source contract is assigned to this work package. Existing or newly authored implementation is not accepted until root records integrated validation; this row makes no passing-test or parity claim.' }),
      ...(reviewed ? { review: reviewed } : {}),
      source: { before: file.before, after: file.after },
    };
  });
  const count = (list, key) => list.reduce((out, row) => (out[row[key]] = (out[row[key]] ?? 0) + 1, out), {});
  const table = {
    schemaVersion: 1, generatedBy: SYNC.generatedBy, decision: SYNC.decision, from: inventory.from, to: inventory.to,
    scope: 'Every changed file in the complete frozen local Git inventory, including root applications, all packages, deletions and renames. This is source applicability, not a claim that every contract is implemented or validated.',
    statusMeaning: { [PENDING]: 'Assigned applicable source work; implementation and integrated runtime acceptance remain with the owning work package and root.', [NO_OP]: 'No direct native runtime port for the explicit reason; related runtime contracts remain separately in scope.' },
    inventory: SYNC.inventory, preservedPrevious: SYNC.preservedPrevious,
    reviewedCategories,
    review: { source: 'tools/CompatibilityReport/sync-1.6.0-dispositions.mjs', meaning: 'What the work package reports state about a row: a finding, the packages that changed the file, deferred parts and notes. It is read from the reports, not from a test run, and changes no status.' },
    workPackages, summary: { files: rows.length, byStatus: count(rows, 'status'), byWorkPackage: count(rows, 'workPackage'), noOpByCategory: count(rows.filter(row => row.status === NO_OP), 'category'),
      pendingByFinding: count(rows.filter(row => row.status === PENDING).map(row => ({ finding: row.review?.finding ?? 'unreviewed' })), 'finding'),
      withPendingParts: rows.filter(row => row.review?.pendingParts).length }, rows,
  };

  // The row-level Durable map stays historical v1.5.0 evidence. The overlay lists this range's changed Durable files and the
  // changed Chord files the historical map or the previous overlay maps, with the last recorded native location.
  const prior = read(SYNC.durable.preservedMap), previousOverlay = read(SYNC.durable.previousOverlay);
  const historical = new Map(prior.files.map(row => [row.upstream, { upstream: row.upstream, pisharp: row.pisharp, status: row.status, blobSha1: row.blobSha1 }]));
  for (const row of previousOverlay.rows) if (row.historicalMapping && row.source.after) historical.set(row.source.after.path, row.historicalMapping);
  const mapped = row => historical.get(row.source.before?.path) ?? historical.get(row.source.after?.path);
  // Where this sync put the native counterpart of a file the range added, or of one whose port spans more than the historical
  // location (tools/DurablePortMap data, written by the Durable work packages). A location, not a completed-port assertion.
  const nativePath = 'tools/DurablePortMap/sync-1.6.0-native-map.json';
  const native = existsSync(resolve(repo, nativePath)) ? read(nativePath).files : {};
  const placed = row => native[row.source.after?.path];
  for (const path of Object.keys(native)) if (!rows.some(row => row.source.after?.path === path && path.startsWith('packages/durable/')))
    throw new Error(`${nativePath}: ${path} is not a Durable file the range changed, so no overlay row would carry it`);
  const overlay = {
    schemaVersion: 1, status: PENDING, source: inventory.to, previous: inventory.from,
    preservedMap: SYNC.durable.preservedMap, preservedMapSha256: createHash('sha256').update(readFileSync(resolve(repo, SYNC.durable.preservedMap))).digest('hex'),
    previousOverlay: SYNC.durable.previousOverlay, applicability: SYNC.table,
    scope: `Current changed Durable files and changed Chord files referenced by the historical Durable map or the previous overlay. Previous mapped locations are preserved as historical references, not assertions of completed ${SYNC.version} ports.`,
    rows: rows.filter(row => row.upstream[0].startsWith('packages/durable/') || mapped(row))
      .map(row => { const old = mapped(row), now = placed(row); return { ...row, ...(old ? { historicalMapping: old } : {}), ...(now ? { nativeMapping: now } : {}) }; }),
  };

  const map = read(SYNC.durable.map), current = map.currentImplementation ?? {};
  const candidate = { source: inventory.to, status: PENDING, overlay: SYNC.durable.overlay, preservedMap: SYNC.durable.preservedMap,
    note: `Top-level source pin, rows, status counts and notes remain historical v1.5.0 evidence. The candidate overlay carries the ${SYNC.version} source disposition and no completed-port assertion.` };
  // The historical rows' notes are pinned v1.5.0 evidence (their bytes are the preserved map's). Where a note names native API
  // that this sync removed, the candidate says so and points at the overlay row that carries the current location.
  candidate.supersededNotes = [
    { upstream: 'packages/durable/src/codemode/describe.ts', names: ['CodemodeNamespace', 'NamespaceOf'], current: SYNC.durable.overlay,
      note: 'The 1.6.0 port removed CodemodeNamespace, its prefix and namespaceOf; namespacesOf is DurableCodemode.NamespacesOf. The overlay row carries the current location as nativeMapping.' },
    { upstream: 'packages/durable/src/codemode/index.ts', names: ['CodemodeNamespace'], current: SYNC.durable.overlay,
      note: 'CodemodeNamespace and CodemodeToolOptions.Namespaces were removed by the 1.6.0 port (durable-edge report, breaking native API changes). The overlay row is current.' },
  ];
  for (const { upstream } of candidate.supersededNotes) if (!prior.files.some(row => row.upstream === upstream) || !overlay.rows.some(row => row.upstream[0] === upstream))
    throw new Error(`${upstream}: a superseded note names a row the map or the overlay does not have`);
  const earlier = [...(current.candidate && current.candidate.overlay !== SYNC.durable.overlay ? [current.candidate] : []), ...(current.previousCandidates ?? [])];
  map.currentImplementation = { ...current, candidate, previousCandidates: earlier };
  return { table, overlay, map };
}

const text = value => JSON.stringify(value, null, 2) + '\n';
const { table, overlay, map } = build();
const outputs = [[SYNC.table, table], [SYNC.durable.overlay, overlay], [SYNC.durable.map, map]];
if (process.argv.includes('--check')) {
  const stale = outputs.filter(([path, value]) => !existsSync(resolve(repo, path)) || readFileSync(resolve(repo, path), 'utf8') !== text(value)).map(([path]) => path);
  if (stale.length) { console.error(`Not the generated pending snapshot: ${stale.join(', ')}`); process.exit(1); }
} else {
  const tablePath = resolve(repo, SYNC.table);
  if (existsSync(tablePath) && !process.argv.includes('--reset-pending')) {
    const existing = JSON.parse(readFileSync(tablePath, 'utf8'));
    if (existing.acceptance || existing.rows.some(row => row.status !== PENDING && row.status !== NO_OP))
      throw new Error('Reviewed statuses are preserved; pass --reset-pending to discard them and start a new review.');
  }
  for (const [path, value] of outputs) writeFileSync(resolve(repo, path), text(value));
}
console.log(JSON.stringify({ ...table.summary, durableOverlayRows: overlay.rows.length }, null, 2));
