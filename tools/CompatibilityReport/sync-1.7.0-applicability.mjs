// Generate the source-candidate map from the complete two-tree inventory. Use
// a frozen validation ledger and independent regression case specification. Run --check in CI.
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const bytes = path => readFileSync(resolve(root, path));
const read = path => JSON.parse(bytes(path));
const pin = path => ({ path, sha256: createHash('sha256').update(bytes(path)).digest('hex') });
const inventoryPath = 'compatibility/sync-1.7.0-files.json';
const tablePath = 'compatibility/sync-1.7.0-applicability.json';
const coveragePath = 'AI_DOCS/sync-1.7.0-coverage.json';
const ledgerPath = 'AI_DOCS/sync-1.7.0-source-validation.json';
const record = 'compatibility/sync-1.7.0-acceptance.json';
const inventory = read(inventoryPath);
const overlay = new Map(read('compatibility/durable-sync-1.7.0-overlay.json').rows.map(row => [row.upstream, row.nativeLocation]));
const expectedCasesPath = 'tools/CompatibilityReport/sync-1.7.0-expected-cases.json';
const expectedByGroup = read(expectedCasesPath);
const ledger = read(ledgerPath);
if (!ledger.sourceValidationOnly || ledger.upstreamCommit !== inventory.to.commit ||
    ledger.target !== inventory.to.tag || ledger.pendingSuites.length || ledger.additionalPending.length)
  throw new Error('Validation ledger must contain completed source verification for this exact release.');
const reports = new Map(ledger.reports.map(report => [report.suite, report]));
if (reports.size !== ledger.reports.length) throw new Error('Duplicate suite receipt.');

const byId = new Map();
function group(id, nativePaths, limitation) {
  const requirements = expectedByGroup[id];
  if (!requirements || !Object.keys(requirements).length) throw new Error('Missing expected case group: ' + id);
  const checks = Object.entries(requirements).map(([suite, expectedIds]) => {
    const report = reports.get(suite);
    if (!report || !['passed', 'passed-with-skips'].includes(report.result) ||
        !Array.isArray(report.passedIds) || !/^[a-f0-9]{64}$/.test(report.reportSha256))
      throw new Error('Missing passing suite receipt for ' + suite + ' in ' + id);
    if (!expectedIds.length || new Set(expectedIds).size !== expectedIds.length) throw new Error('Invalid expected-case list: ' + suite);
    const passed = new Set(report.passedIds);
    const skipped = new Set(report.skipped ?? []);
    const missingIds = expectedIds.filter(name => !passed.has(name) && !skipped.has(name));
    const failedOrSkipped = expectedIds.filter(name => skipped.has(name));
    const selectedExpected = expectedIds.filter(name => passed.has(name)).length;
    return { suite, reportSha256: report.reportSha256, expectedIds, expectedCount: expectedIds.length,
      selectedExpected, missingIds, failedOrSkipped, complete: selectedExpected === expectedIds.length && !failedOrSkipped.length };
  });
  byId.set(id, { id, suites: checks.map(check => check.suite), nativePaths, limitation, rowCount: 0,
    checks, complete: checks.every(check => check.complete) });
  for (const path of nativePaths) if (!existsSync(resolve(root, path))) throw new Error('Missing native path: ' + path);
  return id;
}
const catalog = group('provider-catalog', ['src/PiSharp.AI/Catalogs/Releases/1.7.0','src/PiSharp.ModelRuntime/Models'], 'Embedded release shards and native discovery. Upstream catalog build scripts and live model availability are outside the offline comparison.');
const images = group('image-and-decision', ['src/PiSharp.AI/ModelOperations','src/PiSharp.Contracts/ModelOperations'], 'Native scripted model responses, request shapes and usage/pricing. Real provider accounts are outside this verification.');
const providers = group('chat-providers', ['src/PiSharp.AI/Protocols','src/PiSharp.AI/Providers'], 'Native HTTP fixtures and protocol projections. Provider infrastructure and every historical wire schedule have separate qualification.');
const retry = group('stream-and-retry', ['src/PiSharp.AI/Protocols/ProviderShared','src/PiSharp.CodingAgent/Configuration/AgentRetryPolicy.cs'], 'Scripted streaming, error and retry schedules. Historical Pi Messages framing differs from the current protocol.');
const models = group('model-runtime', ['src/PiSharp.ModelRuntime','src/PiSharp.CodingAgent/Schemas/models.schema.json'], 'Model selection, model metadata and account filtering use offline credentials and fixtures.');
const bridge = group('extension-runtime', ['tools/NodeBridge/runtime-fixture-170','tools/NodeBridge/pi-host/virtual'], 'Released extension ABI and native bridge behavior on supported Node versions. No claim of full upstream JavaScript package equivalence.');
const interactive = group('interactive-cli', ['src/PiSharp.Cli/Interactive'], 'Native rendered and scripted interactive sessions. Physical terminal, OS clipboard, and physical Windows console behavior are outside local verification.');
const cli = group('cli-sessions', ['src/PiSharp.Cli','src/PiSharp.CodingAgent','src/PiSharp.Sessions'], 'Native CLI and session fixtures. Native NuGet tool updates use a different package manager from upstream npm.');
const code = group('codemode', ['src/PiSharp.Codemode','src/PiSharp.Cli/Extensions/NativeExtensionModelOperations.cs'], 'Native codemode bridge calls and scripted model options. Provider credentials are outside this evidence.');
const shell = group('shell-and-tools', ['src/PiSharp.Tools','src/PiSharp.CodingAgent/Execution/UserBash.cs'], 'Native Bash, truncated output and tool dispatch tests; Windows physical console closure remains a platform check.');
const mcp = group('mcp', ['src/PiSharp.Extensions.Runtime/Mcp','src/PiSharp.Cli/Mcp'], 'Native JSON-RPC/SSE and result shape checks using supplied servers. Historical lifecycle stress controls have separate results.');
const tui = group('terminal-ui', ['src/PiSharp.Tui'], 'Native terminal drivers, renderer and editor fixtures. Platform-specific emulator behaviors are tested through selected representative paths.');
const durable = group('durable', ['src/PiSharp.Durable','src/PiSharp.Durable.Tools','src/PiSharp.Durable.Sqlite'], 'Native tagged results, session admission and persistence. Historical storage adapters keep their original qualification; the upstream IndexedDB browser adapter is outside scope.');
const chord = group('chord-consumers', ['src/PiSharp.Chord','src/PiSharp.Durable.Store'], 'Storage, query argument admission, recovery and watch paths used by Durable. Standalone Chord v2 peers, new grant APIs, and query-performance parity are outside this candidate.');
const env = group('native-environment', ['src/PiSharp.Env','src/PiSharp.Env.Daemon'], 'Local and scripted process/terminal lifecycles. Native Windows PTY creation, live SSH and independently packaged daemons are outside this source check.');
const pinning = group('source-and-attribution', ['compatibility/target.lock.json','THIRD-PARTY-NOTICES.md'], 'Immutable package manifest, license and file identities verified against upstream Git objects. This check concerns source provenance.');
const noop = (category,reason) => ({status:'no-op',category,reason});
const tested = workPackage => ({status:'tested-with-limitations',workPackage});
const skipped = {
  app: noop('upstream-app','Upstream p2/p3 application, test, example or build input. PiSharp ports the published library and coding-agent packages; these separate applications are outside the candidate.'),
  docs: noop('upstream-documentation','Upstream documentation or changelog; native docs and release notes use their own sources.'),
  build: noop('upstream-build-tooling','Upstream repository administration, build or smoke tooling. The native tree supplies its own .NET build and qualification.'),
  browser: noop('browser-indexeddb','Browser IndexedDB storage is outside the native SQLite, memory and JSONL adapters.'),
  peer: noop('standalone-chord','Standalone upstream Chord behavior or coverage outside the Pi/Durable consumer update. Decision 0018 preserves the PiSharp v1 peer contract; no Chord v2 migration or related performance claim.'),
  examples: noop('upstream-example','Upstream sample code, outside the distributed native coding-agent runtime.'),
};
// The standalone Chord package changed independently of the Pi/Durable API. These are the
// source/test paths relevant to the scoped native Chord updates, in addition to the Durable overlay.
const chordConsumer = /^(?:src|test)\/(?:context(?:\/|\.test)|delta(?:\/|-diff)|(?:contracts\/declare|data\/(?:args|plain-json)|documents\/(?:documents|line|transaction)|host\/(?:follow|named-query|read-declarations)|registries\/record|store\/(?:import|jsonl|query|store|outbox)|sync\/(?:batch|client|files|filesystem|index|remote|state|sync|node-files))(?:\.test)?\.ts$)/;
function classify(row,path) {
  if (path.startsWith('apps/')) return skipped.app;
  if (path === 'LICENSE') return tested(pinning);
  if (row.package === '(root)') return skipped.build;
  const relative = path.split('/').slice(2).join('/');
  if (relative.endsWith('.md') || relative.startsWith('docs/')) return skipped.docs;
  if (relative === 'package.json') return tested(pinning);
  if (relative.startsWith('examples/')) return skipped.examples;
  if (row.package === 'chord') return overlay.has(path) || chordConsumer.test(relative) ? tested(chord) : skipped.peer;
  if (row.package === 'durable') {
    if (relative.includes('indexeddb')) return skipped.browser;
    if (!overlay.has(path)) throw new Error('Unmapped Durable file: '+path);
    return tested(durable);
  }
  if (row.package === 'tui') return tested(tui);
  if (row.package === 'env') return tested(env);
  if (row.package === 'mcp') return tested(mcp);
  if (row.package === 'agent' && relative === 'src/proxy.ts') return tested(bridge);
  if (row.package === 'ai') {
    if (relative.startsWith('catalog/') || /(?:image-model-data|qwen-token-plan-models|together-models)\.test/.test(relative)) return tested(catalog);
    if (/classifier|system-one|classify|decisions?|images|pi-image|json-request|openrouter-cost/.test(relative)) return tested(images);
    if (/^(?:src\/providers\/|src\/models\.ts|src\/auth\/oauth\/)|models-runtime|radius-provider|github-copilot-oauth/.test(relative)) return tested(models);
    if (/retry|overflow|utils\/sse|test\/sse|api\/pi-messages/.test(relative)) return tested(retry);
    return tested(providers);
  }
  if (row.package === 'coding-agent') {
    if (/codemode/.test(relative)) return tested(code);
    if (/mcp/.test(relative)) return tested(mcp);
    if (/install-lock\/|core\/extensions\/|extension-dialog/.test(relative)) return tested(bridge);
    if (/model-(?:resolver|registry|runtime)|list-models|models\.schema|provider-composer/.test(relative)) return tested(models);
    if (/modes\/interactive\/|(?:editor|mermaid|selector|clipboard|startup-ui|program-(?:status|resume)|interactive-mode|follow-up|thinking-toggle|llama-ui|first-time-setup|bash-execution-width)/.test(relative)) return tested(interactive);
    if (/bash|shell|output-(?:files?|accumulator)|core\/tools\/|tools\.test/.test(relative)) return tested(shell);
    return tested(cli);
  }
  throw new Error('Review unclassified path: '+path);
}
const rows = inventory.files.map((file,index) => {
  const upstream = [...new Set([file.before?.path,file.after?.path].filter(Boolean))];
  const path = file.after?.path ?? file.before.path;
  const disposition = classify(file,path);
  if (disposition.workPackage) byId.get(disposition.workPackage).rowCount++;
  const nativeLocation = overlay.get(path);
  return {id: 'sync170.file.'+String(index+1).padStart(4,'0'),upstream,change:file.status,...disposition,
    ...(nativeLocation ? {nativeLocation} : {})};
});
const target = read('compatibility/target.lock.json').source;
if (inventory.to.commit !== 'fb0fe823539f6d04850f6e34748c01f01f212e1f' ||
    inventory.from.commit !== 'faac585884ae2c013a6b1400f3e9e77f10b34bac') {
  throw new Error('The reviewed dispositions apply only to the 1.6.0 → 1.7.0 source range.');
}
for (const row of rows) {
  for (const path of [row.nativeLocation].flat().filter(Boolean)) {
    if (!existsSync(resolve(root, path))) throw new Error('Missing Durable mapping: '+path);
  }
}
if (inventory.to.tag !== 'v1.7.0' || inventory.to.commit !== target.commit || inventory.to.tree !== target.tree ||
    rows.length !== 1683 || rows.length !== inventory.summary.files || new Set(rows.map(row => row.upstream.at(-1))).size !== rows.length)
  throw new Error('Unexpected source inventory identity, row count or duplicate path.');
const groups = [...byId.values()];
const counts = Object.fromEntries(['no-op','tested-with-limitations'].map(status => [status,rows.filter(row=>row.status===status).length]));
if (groups.some(g=>g.rowCount===0) || groups.reduce((n,g)=>n+g.rowCount,0) !== counts['tested-with-limitations']) throw new Error('Coverage group mismatch.');
if (Object.keys(expectedByGroup).length !== groups.length) throw new Error('Unmatched expected group in specification.');
const incomplete = groups.flatMap(group => group.checks.filter(check => !check.complete));
if (incomplete.length) throw new Error('Source case coverage incomplete: ' + JSON.stringify(incomplete));
const scope = 'File disposition in the frozen v1.6.0..v1.7.0 inventory. Runtime and upstream test files map to native behavior groups. Suite evidence applies to those groups, not one-to-one test equivalence or upstream test execution.';
const coverage = {schemaVersion:1,sourceValidationOnly:true,scope,source:inventory.to,inventory:pin(inventoryPath),ledger:pin(ledgerPath),expectedCases:pin(expectedCasesPath),complete:groups.every(group => group.complete),summary:{files:rows.length,byStatus:counts},groups};
function emit(path,data) {
  const rendered = JSON.stringify(data,null,2)+'\n';
  if (process.argv.includes('--check')) {
    if (bytes(path).toString()!==rendered) throw new Error('Stale '+path);
  } else writeFileSync(resolve(root,path),rendered);
}
emit(coveragePath,coverage);
if (!process.argv.includes('--coverage-only')) {
  const table = {schemaVersion:1,generatedBy:'tools/CompatibilityReport/sync-1.7.0-applicability.mjs',from:inventory.from,to:inventory.to,
    inventory:inventoryPath,decision:'docs/decisions/0018-pi-1.7.0-sync.md',scope,summary:coverage.summary,
    acceptance:{record,ledger:pin(ledgerPath),coverage:pin(coveragePath)},rows};
  emit(tablePath,table);
}
console.log('Mapped '+rows.length+' source changes in '+groups.length+' native verification groups.');
