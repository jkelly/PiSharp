#!/usr/bin/env node
// Candidate source disposition only. Root owns integrated validation and final status changes.
import { readFileSync, readdirSync, writeFileSync, existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
if (process.argv.includes('--accepted-ledger') || process.argv.includes('--ledger')) {
  const { runAcceptanceCli } = await import('./accept-sync-1.5.2.mjs');
  runAcceptanceCli();
  process.exit(process.exitCode ?? 0);
}
if (process.argv.includes('--apply')) throw new Error('--apply requires --accepted-ledger and exact evidence hashes');
const read = path => JSON.parse(readFileSync(resolve(repo, path), 'utf8'));
const existingTable = resolve(repo, 'compatibility/sync-1.5.2-applicability.json');
if (existsSync(existingTable) && jsonHasAcceptance(existingTable) && !process.argv.includes('--reset-pending'))
  throw new Error('Accepted metadata is preserved; use the evidence-aware acceptance options, or explicitly --reset-pending to start a new review.');
function jsonHasAcceptance(path) { return Boolean(JSON.parse(readFileSync(path, 'utf8')).acceptance); }
const inventory = read('compatibility/sync-1.5.2-files.json');
const reportRoot = 'AI_DOCS/agent-reports';
const reports = readdirSync(resolve(repo, reportRoot)).filter(name => /^sync-1\.5\.2-.*\.md$/.test(name)).map(name => `${reportRoot}/${name}`);
const selectedReports = patterns => reports.filter(path => patterns.some(pattern => pattern.test(path)));
const areas = {
  META: [/meta-pin/, /telemetry-discovery/],
  AI: [/ai-/, /catalog/, /model-refresh/],
  CLI: [/cli-/, /ui-flow/],
  CODEMODE: [/codemode/, /schema-discovery/],
  SCHEMA: [/schema-discovery/],
  DURABLE: [/durable-discovery/, /durable-implementation/],
  STORAGE: [/storage-discovery/, /sqlite/, /jsonl/],
  ENV: [/env-/], MCP: [/mcp-/], TUI: [/tui-/, /terminal/, /mermaid/, /markdown/],
  TELEMETRY: [/telemetry-/], CHORD: [/chord-/],
};
const workPackages = Object.fromEntries(Object.entries(areas).map(([id, match]) => [id, {
  reports: selectedReports(match), plan: 'AI_DOCS/sync-1.5.2.md', status: 'implementation-pending-validation',
}]));
const chord = {
  'C-K': [/chord-discovery/, /chord-kernel/], 'C-W': [/chord-discovery/, /chord-wire/],
  'C-HD': [/chord-hostdata/, /chord-discovery/], 'C-STORE': [/storage-discovery/, /chord-storage/, /sqlite/, /mssql/],
  'C-SYNC': [/chord-sync/, /chord-discovery/], 'C-HTTP': [/chord-http/, /chord-discovery/],
  'C-PL': [/plugin-contract/, /chord-plugin/, /chord-discovery/], 'C-WK': [/chord-workers/, /chord-discovery/],
  'C-A1': [/chord-app/, /chord-discovery/], 'C-A2': [/chord-files/, /chord-app/, /chord-discovery/],
  'C-A3': [/chord-archives/, /chord-discovery/], 'C-SUR': [/chord-surface/, /surfaces-implementation/, /chord-discovery/],
  'C-PUB': [/chord-publication/, /chord-discovery/], 'C-TEL': [/telemetry-/],
  'C-LEGACY': [/chord-discovery/], 'C-JS': [/chord-publication/, /chord-discovery/],
  'C-TERM': [/chord-terminal/, /terminal-implementation/, /chord-discovery/],
};
for (const [id, match] of Object.entries(chord)) workPackages[id] = {
  reports: selectedReports(match), plan: `${reportRoot}/sync-1.5.2-chord-discovery.md`, status: 'implementation-pending-validation',
};

function owner(path) {
  const match = /^packages\/([^/]+)\/(.*)/.exec(path);
  if (!match) return 'META';
  const [, pkg, relative] = match;
  if (pkg !== 'chord') return ({ agent: 'AI', ai: 'AI', 'coding-agent': 'CLI', codemode: 'CODEMODE',
    schema: 'SCHEMA', durable: /(?:^src\/storage\/|storage|commit-writes)/.test(relative) ? 'STORAGE' : 'DURABLE',
    env: 'ENV', mcp: 'MCP', telemetry: 'TELEMETRY', tui: 'TUI', evals: 'META' })[pkg] ?? 'META';
  const source = relative.replace(/^(src|test)\/next-node\//, '$1/node/').replace(/^(src|test)\/next\//, '$1/');
  if (/^test\/node\/reload\//.test(source)) return 'C-K';
  if (/^test\/node\/(?:react|web)\//.test(source)) return /language/.test(source) ? 'C-SUR' : 'C-JS';
  if (/^(?:src|test)\/(?:node\/)?tui\//.test(source)) return 'C-TERM';
  if (/^(?:src|test)\/(?:node\/)?app\/(?:archive|tar|host-data)/.test(source)) return 'C-A3';
  if (/^(?:src|test)\/(?:node\/)?app\/(?:files|file|blob|upload|folder|copy)/.test(source)) return 'C-A2';
  if (/^(?:src|test)\/node\/app\//.test(source)) return 'C-A1';
  if (/^(?:src|test)\/node\/(?:surface|surfaces|scripts|build)/.test(source)) return 'C-PUB';
  if (/^(?:src|test)\/node\/http\//.test(source)) return 'C-HTTP';
  if (/^(?:src|test)\/node\/(?:plugin|plugins|loader)/.test(source)) return 'C-PL';
  if (/^(?:src|test)\/node\/(?:worker|workers|process)/.test(source)) return 'C-WK';
  if (/^(?:src|test)\/(?:telemetry|telemetry-ingest|telemetry-sqlite)(?:\/|\.)/.test(source)) return 'C-TEL';
  if (/^(?:src|test)\/sync\//.test(source)) return 'C-SYNC';
  if (/^(?:src|test)\/(?:store|sqlite)\//.test(source)) return 'C-STORE';
  if (/^(?:src|test)\/(?:contracts|documents|schema)\//.test(source)) return 'C-HD';
  if (/^(?:src|test)\/(?:link|remote)\//.test(source)) return 'C-W';
  if (/^(?:src|test)\/(?:surfaces|client)\//.test(source)) return 'C-SUR';
  if (/^(?:src|test)\/(?:web|react|tui)\//.test(source)) return 'C-JS';
  if (/^(?:src|test)\/(?:host|jobs|data|registries|state)\//.test(source)) return 'C-K';
  if (/^test\/telemetry-sqlite/.test(source)) return 'C-TEL';
  if (/^src\/[^/]+\.ts$/.test(source)) return source === 'src/index.ts' ? 'C-K' : 'C-LEGACY';
  if (/^test\/[^/]+\.ts$/.test(source)) return ['test/json.test.ts', 'test/support.ts'].includes(source) ? 'C-K' : 'C-LEGACY';
  return 'CHORD';
}

// Explicit import-only entries identified in the Durable discovery/source review.
const durableImportOnly = new Set(['entries.ts', 'harness/context.ts', 'harness/define.ts', 'harness/harness.ts',
  'harness/inbox.ts', 'harness/json.ts', 'harness/live.ts', 'harness/partial-changes.ts', 'harness/prompt.ts',
  'harness/provider.ts', 'harness/submissions.ts', 'harness/task-graph.ts', 'harness/tool.ts',
  'harness/transcript.ts', 'harness/usage.ts', 'harness/util.ts', 'harness/view.ts', 'storage/memory.ts',
  'storage/store/commit.ts', 'storage/store/documents.ts', 'storage/store/queries.ts', 'storage/store/storage.ts',
  'storage/store/tables.ts', 'testing/commit-writes.ts', 'testing/storage-benchmark.ts', 'testing/storage-conformance.ts']);

function disposition(path, file) {
  if (path.startsWith('apps/')) return ['upstream-app', 'Upstream application product code/assets are not PiSharp products. Shared library behavior is tracked independently under packages/chord; this does not exclude the native Chord App library.'];
  if (/(?:^|\/)(?:examples|bench|benchmarks|fixtures)\//.test(path)) return ['example-or-fixture', 'Upstream examples, benchmarks and fixture files are reference material, not shipped C# runtime source. Applicable contracts and authored/captured native evidence belong to the corresponding area; no fixture is relabeled or recaptured by this mapping.'];
  if (/\.d\.ts$/.test(path)) return ['typescript-ambient', 'Ambient TypeScript declarations have no emitted runtime behavior. Native public API behavior remains the corresponding work package’s responsibility.'];
  if (/^packages\/chord\/src\/(?:react\/|web\/)/.test(path)) return ['browser-dom-react', 'React hooks and browser DOM/page adapters require JavaScript browser execution. Native portable client/publication contracts are tracked in C-SUR/C-PUB; no CLR or ALC substitute is claimed to execute DOM or React.'];
  if (path === 'packages/durable/src/storage/indexeddb.ts' || path === 'packages/durable/test/indexeddb-storage.test.ts') return ['browser-indexeddb', 'Browser IndexedDB backend and its browser test harness cannot run in the native .NET storage host. Native JSONL, SQLite and store backends remain in scope.'];
  if (path === 'packages/chord/src/node/tui/sandbox.ts') return ['javascript-terminal-sandbox', 'Secure downloaded-JavaScript execution has no native CLR permission-sandbox equivalent and fails explicitly. Native terminal framing, launcher, filters, actions and credentials are assigned to C-TERM.'];
  if (path === 'packages/chord/src/node/compression.ts') return ['typescript-import-only', 'Root reviewed the frozen diff: comments and next/link-to-link import relocation only. The compression runtime algorithm has no delta.'];
  if (/^packages\/chord\/test\/node\/(?:react\/|web\/(?:browser|page|styles|support))/.test(path)) return ['browser-dom-react', 'Upstream React/DOM browser adapter tests require the actual browser runtime. Native portable client/publication contracts and their own tests remain separately assigned.'];
  if (/^packages\/chord\/test\/schema\/(?:stored\/|recorded\.json)/.test(path)) return ['example-or-fixture', 'Upstream schema corpus bytes are retained source reference. Historical native captures are not relabeled or replaced; native schema behavior and regressions remain assigned to C-HD.'];
  if (path === 'packages/coding-agent/src/index.ts') return ['typescript-export-only', 'The coding-agent discovery identifies TypeScript export plumbing only. Equivalent native public contracts are covered by their implementing areas; no separate module-barrel runtime port is required.'];
  if (path.startsWith('packages/durable/src/') && durableImportOnly.has(path.slice('packages/durable/src/'.length))) return ['typescript-import-only', 'Reviewed Durable discovery identifies only Chord subpath/type import relocation or whitespace. C# uses its existing CLR references; related Chord runtime changes are separately implemented and validated.'];
  if (/(?:^|\/)(?:docs)\//.test(path) || /(?:^|\/)(?:README|CHANGELOG|CONTRIBUTING|AGENTS)\.md$/.test(path)) return ['upstream-documentation', 'Upstream prose is source reference, not runtime behavior. Candidate release/compatibility documentation is maintained separately; behavioral changes described here remain assigned to their area.'];
  if (/^(?:\.github\/|\.pi\/|nix\/|scripts\/)/.test(path) || /(?:^|\/)(?:tsconfig[^/]*\.json|vitest\.config\.ts|biome\.json|\.npmrc|package-lock\.json)$/.test(path)) return ['upstream-build-tooling', 'Upstream JavaScript workspace, release/CI, developer or Nix tooling is not the native build system. Package dependency/runtime pin effects are reviewed separately; native build and release tooling is retained.'];
  if (path === 'packages/evals/package.json') return ['evals-version-only', 'The evals discovery found only version and pi-ai/pi-coding-agent development dependency updates, with no evaluation algorithm delta. Native package versioning is owned by META.'];
  return null;
}

const rows = inventory.files.map((file, index) => {
  const path = (file.after ?? file.before).path;
  const id = owner(path), noOp = disposition(path, file);
  return {
    id: `sync152.file.${String(index + 1).padStart(4, '0')}`, upstream: [path], change: file.status,
    workPackage: id, area: id.startsWith('C-') || id === 'CHORD' ? 'chord' : id.toLowerCase(), reports: workPackages[id].reports,
    status: noOp ? 'no-op' : 'implementation-pending-validation',
    ...(noOp ? { category: noOp[0], reason: noOp[1] } : { reason: 'Applicable source contract is assigned to this work package. Existing or newly authored implementation is not accepted until root records integrated validation; this row makes no passing-test or parity claim.' }),
    source: { before: file.before, after: file.after },
  };
});
const count = key => rows.reduce((out, row) => (out[row[key]] = (out[row[key]] ?? 0) + 1, out), {});
const output = {
  schemaVersion: 1, generatedBy: 'tools/CompatibilityReport/sync-1.5.2-applicability.mjs',
  decision: 'docs/decisions/0014-pi-1.5.2-sync.md', from: inventory.from, to: inventory.to,
  scope: 'Every changed file in the complete frozen local Git inventory, including root applications, all packages, deletions and renames. This is source applicability, not a claim that every contract is implemented or validated.',
  statusMeaning: { 'implementation-pending-validation': 'Assigned applicable source work; implementation and integrated runtime acceptance remain with the owning work package and root.', 'no-op': 'No direct native runtime port for the explicit reason; related runtime contracts remain separately in scope.' },
  inventory: 'compatibility/sync-1.5.2-files.json', preservedPrevious: 'compatibility/sync-1.5.0-applicability.json',
  workPackages, summary: { files: rows.length, byStatus: count('status'), byWorkPackage: count('workPackage'), noOpByCategory: rows.filter(row => row.status === 'no-op').reduce((out, row) => (out[row.category] = (out[row.category] ?? 0) + 1, out), {}) }, rows,
};
writeFileSync(resolve(repo, 'compatibility/sync-1.5.2-applicability.json'), JSON.stringify(output, null, 2) + '\n');

const priorPath = 'compatibility/durable-port-map.v1.5.0.json';
const currentPath = 'compatibility/durable-port-map.json';
if (!existsSync(resolve(repo, priorPath))) writeFileSync(resolve(repo, priorPath), readFileSync(resolve(repo, currentPath)));
const prior = read(priorPath), previousRows = new Map(prior.files.map(row => [row.upstream, row]));
const durableRows = rows.filter(row => row.upstream[0].startsWith('packages/durable/') || previousRows.has(row.source.before?.path) || previousRows.has(row.source.after?.path));
const overlayPath = 'compatibility/durable-sync-1.5.2-overlay.json';
const overlay = {
  schemaVersion: 1, status: 'implementation-pending-validation', source: inventory.to, previous: inventory.from,
  preservedMap: priorPath, preservedMapSha256: createHash('sha256').update(readFileSync(resolve(repo, priorPath))).digest('hex'),
  applicability: 'compatibility/sync-1.5.2-applicability.json',
  scope: 'Current changed Durable files and changed Chord files referenced by the historical Durable map. Previous mapped locations are preserved as historical references, not assertions of completed 1.5.2 ports.',
  rows: durableRows.map(row => {
    const old = previousRows.get(row.source.before?.path) ?? previousRows.get(row.source.after?.path);
    return { ...row, ...(old ? { historicalMapping: { upstream: old.upstream, pisharp: old.pisharp, status: old.status, blobSha1: old.blobSha1 } } : {}) };
  }),
};
writeFileSync(resolve(repo, overlayPath), JSON.stringify(overlay, null, 2) + '\n');
const current = read(currentPath);
current.currentImplementation = { ...current.currentImplementation, candidate: {
  source: inventory.to, status: 'implementation-pending-validation', overlay: overlayPath, preservedMap: priorPath,
  note: 'Top-level source pin, rows, status counts and notes remain historical v1.5.0 evidence. The candidate overlay carries the 1.5.2 source disposition and no completed-port assertion.',
} };
writeFileSync(resolve(repo, currentPath), JSON.stringify(current, null, 2) + '\n');
console.log(JSON.stringify({ ...output.summary, durableOverlayRows: overlay.rows.length }, null, 2));
