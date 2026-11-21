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

// The work packages of AI_DOCS/sync-1.6.0.md. packages/chord stays one package until chord-discovery decomposes it;
// its report is the discovery report.
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
  chord: { area: 'packages/chord, one package until chord-discovery decomposes it into chord-* packages', branch: '(set by chord-discovery)', report: 'chord-discovery' },
};
const workPackages = Object.fromEntries(Object.entries(packages).map(([id, { report, ...value }]) =>
  [id, { ...value, reports: [SYNC.report(report ?? id)], plan: SYNC.plan, status: PENDING }]));

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

function owner(path) {
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
    case 'chord': return 'chord';
    default: return 'meta'; // codemode, schema, telemetry and evals change only their changelog and manifest in this range.
  }
}

// The 1.5.2 no-op categories and reasons, without the reviewed per-file 1.5.2 entries. A no-op here is mechanical: the
// path alone decides it. Anything that needs a source review stays pending with its owner.
function disposition(path) {
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

function build() {
  const inventory = read(SYNC.inventory);
  const rows = inventory.files.map((file, index) => {
    const path = (file.after ?? file.before).path;
    const id = owner(path), noOp = disposition(path);
    return {
      id: `${SYNC.rowPrefix}.file.${String(index + 1).padStart(4, '0')}`, upstream: [path], change: file.status,
      workPackage: id, area: path.startsWith('apps/') ? 'apps' : /^packages\/([^/]+)\//.exec(path)?.[1] ?? 'root', reports: workPackages[id].reports,
      status: noOp ? NO_OP : PENDING,
      ...(noOp ? { category: noOp[0], reason: noOp[1] } : { reason: 'Applicable source contract is assigned to this work package. Existing or newly authored implementation is not accepted until root records integrated validation; this row makes no passing-test or parity claim.' }),
      source: { before: file.before, after: file.after },
    };
  });
  const count = (list, key) => list.reduce((out, row) => (out[row[key]] = (out[row[key]] ?? 0) + 1, out), {});
  const table = {
    schemaVersion: 1, generatedBy: SYNC.generatedBy, decision: SYNC.decision, from: inventory.from, to: inventory.to,
    scope: 'Every changed file in the complete frozen local Git inventory, including root applications, all packages, deletions and renames. This is source applicability, not a claim that every contract is implemented or validated.',
    statusMeaning: { [PENDING]: 'Assigned applicable source work; implementation and integrated runtime acceptance remain with the owning work package and root.', [NO_OP]: 'No direct native runtime port for the explicit reason; related runtime contracts remain separately in scope.' },
    inventory: SYNC.inventory, preservedPrevious: SYNC.preservedPrevious,
    workPackages, summary: { files: rows.length, byStatus: count(rows, 'status'), byWorkPackage: count(rows, 'workPackage'), noOpByCategory: count(rows.filter(row => row.status === NO_OP), 'category') }, rows,
  };

  // The row-level Durable map stays historical v1.5.0 evidence. The overlay lists this range's changed Durable files and the
  // changed Chord files the historical map or the previous overlay maps, with the last recorded native location.
  const prior = read(SYNC.durable.preservedMap), previousOverlay = read(SYNC.durable.previousOverlay);
  const historical = new Map(prior.files.map(row => [row.upstream, { upstream: row.upstream, pisharp: row.pisharp, status: row.status, blobSha1: row.blobSha1 }]));
  for (const row of previousOverlay.rows) if (row.historicalMapping && row.source.after) historical.set(row.source.after.path, row.historicalMapping);
  const mapped = row => historical.get(row.source.before?.path) ?? historical.get(row.source.after?.path);
  const overlay = {
    schemaVersion: 1, status: PENDING, source: inventory.to, previous: inventory.from,
    preservedMap: SYNC.durable.preservedMap, preservedMapSha256: createHash('sha256').update(readFileSync(resolve(repo, SYNC.durable.preservedMap))).digest('hex'),
    previousOverlay: SYNC.durable.previousOverlay, applicability: SYNC.table,
    scope: `Current changed Durable files and changed Chord files referenced by the historical Durable map or the previous overlay. Previous mapped locations are preserved as historical references, not assertions of completed ${SYNC.version} ports.`,
    rows: rows.filter(row => row.upstream[0].startsWith('packages/durable/') || mapped(row))
      .map(row => { const old = mapped(row); return { ...row, ...(old ? { historicalMapping: old } : {}) }; }),
  };

  const map = read(SYNC.durable.map), current = map.currentImplementation ?? {};
  const candidate = { source: inventory.to, status: PENDING, overlay: SYNC.durable.overlay, preservedMap: SYNC.durable.preservedMap,
    note: `Top-level source pin, rows, status counts and notes remain historical v1.5.0 evidence. The candidate overlay carries the ${SYNC.version} source disposition and no completed-port assertion.` };
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
