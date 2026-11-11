// Current implementation mapping, separate from the frozen v1.5.0 release evidence.
// node tools/CompatibilityReport/gap-status-mapping.mjs --write
// node --test tools/CompatibilityReport/gap-status.test.mjs
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export const statusPath = 'compatibility/gap-status-2026-11-08.json';
const applicabilityPath = 'compatibility/sync-1.5.0-applicability.json';
const portMapPath = 'compatibility/durable-port-map.json';
const registerPath = 'AI_DOCS/upstream-audit-2026-11-08/gap-register.json';
const read = path => JSON.parse(readFileSync(resolve(root, path), 'utf8'));
const report = id => `AI_DOCS/agent-reports/${id}.md`;
const pending = 'pending-integrated-validation';
// Canonical JSON fingerprints of the exact product baseline 14dd34b3. Current mappings may be added, but
// the original status, notes, evidence, paths and source pins must still reconstruct that baseline.
const historicalDigests = {
  'compatibility/durable-port-map.json':'334b449fb942f1d3bc58140ceb62cff6b4294271d2458c49585c22b3b1f5165d',
  'compatibility/sync-1.5.0-applicability.json':'2bd227039e8676befa6ed028127b329110d52f9f9e1609005f10efd1ba2d6dbd',
  'compatibility/implementation-status.json':'d5aaa8b97a3f217985d1f54f8339a22bd203f8e878e38288a125d1e209ec0ea0',
};
const canonical = value => Array.isArray(value) ? value.map(canonical) : value && typeof value==='object'
  ? Object.fromEntries(Object.keys(value).sort().map(key=>[key,canonical(value[key])])) : value;
function checkHistoricalEvidence() {
  for(const [path,expected] of Object.entries(historicalDigests)) {
    const value=read(path); delete value.currentImplementation; delete value.currentGapStatus;
    // Candidate metadata does not rewrite the historical implementation evidence this hash protects.
    if (path === 'compatibility/implementation-status.json') delete value.pi152Candidate;
    for(const row of value.rows??value.files??[]) {
      delete row.currentImplementation;
      if('historicalPisharp' in row) {row.pisharp=row.historicalPisharp;delete row.historicalPisharp;}
    }
    assert.equal(createHash('sha256').update(JSON.stringify(canonical(value))).digest('hex'),expected,`${path}: historical evidence changed`);
  }
}

// These are source/test anchors, not an assertion that every case in a file passed.
const group = (reports, sourcePaths, testPaths, disposition = '') => ({ reports: reports.map(report), sourcePaths, testPaths, disposition });
const groups = {
  schema: group(['I01'], ['src/PiSharp.Contracts/Schema/SchemaResolver.cs', 'src/PiSharp.Contracts/Schema/JsonSchemaBuilder.cs'], ['tests/PiSharp.Schema.Tests/GapCases.cs'], 'CLR generic schema association replaces TypeScript Static inference; non-JSON builder values are not represented as JSON.'),
  anthropic: group(['I02'], ['src/PiSharp.AI/Protocols/AnthropicMessages/AnthropicMessagesRequestProjector.cs', 'src/PiSharp.AI/Protocols/AnthropicMessages/AnthropicMessageThreads.cs'], ['tests/PiSharp.AnthropicThreads.Tests']),
  refs: group(['I03'], ['src/PiSharp.Contracts/Json/LocalSchemaReferences.cs', 'src/PiSharp.Durable.Tools/ToolSearch/ToolSearch.cs', 'src/PiSharp.Extensions.Abstractions/Mcp/Discovery/ToolSearchRanker.cs'], ['tests/PiSharp.ToolSearch.Tests/SchemaReferenceCases.cs', 'tests/PiSharp.Durable.Tests/ToolSearchReferenceCases.cs', 'tests/PiSharp.Codemode.Tests/SchemaReferenceCases.cs']),
  openai: group(['I04'], ['src/PiSharp.AI/Protocols/OpenAICompletions/CompletionsKeyAuthRequestFactory.cs', 'src/PiSharp.AI/Protocols/OpenAIResponses/ResponsesKeyAuthRequestFactory.cs', 'src/PiSharp.CodingAgent/Configuration/AgentRetryPolicy.cs', 'src/PiSharp.Durable/Internal/PiAi/Retry.cs'], ['tests/PiSharp.OpenAICompatibilityGap.Tests']),
  budget: group(['I05'], ['src/PiSharp.AI/Protocols/ProviderShared/SimpleOutputBudget.cs'], ['tests/PiSharp.SimpleOutputBudget.Tests']),
  catalog: group(['I06', 'F09-catalog'], ['catalog-maintained/models.overlay.json', 'src/PiSharp.AI/Catalogs/ReviewedModelCatalog.cs', 'src/PiSharp.AI/Catalogs/reviewed-2026-11-08.json', 'src/PiSharp.AI/Catalogs/BuiltinProviders.cs'], ['tests/PiSharp.Models.Tests/CatalogCorrectionCases.cs', 'tests/PiSharp.Models.Tests/CatalogExpansionCases.cs', 'tests/PiSharp.Durable.Tests/ReviewedCatalogCases.cs'], 'Frozen catalog shards and their pins remain unchanged; new reviewed rows and corrections are a separate overlay.'),
  files: group(['I07'], ['src/PiSharp.Tools/Files/EditPlan.cs', 'src/PiSharp.Tools/Files/PiGrepTool.cs', 'src/PiSharp.Tools/Files/ReadWriteTools.cs', 'src/PiSharp.Tools/Files/PiFindTool.cs', 'src/PiSharp.Tools/Files/LsTool.cs'], ['tests/PiSharp.Tools.Tests/GapFileToolsTests.cs']),
  session: group(['I08'], ['src/PiSharp.CodingAgent/Resources/PromptTemplateExpander.cs', 'src/PiSharp.CodingAgent/PersistentAgentSession.ThinkingPersistence.cs', 'src/PiSharp.Contracts/UnicodeText.cs'], ['tests/PiSharp.CodingAgent.Tests/PromptTemplateTests.cs', 'tests/PiSharp.CodingAgent.Tests/SessionTreeNavigationTests.cs']),
  mcp: group(['I09'], ['src/PiSharp.Extensions.Runtime/Mcp/Transport/McpStreamableHttpTransport.cs', 'src/PiSharp.Cli/Mcp/Authentication/McpSignIn.cs'], ['tests/PiSharp.Extensions.ContractTests/McpSseFramingTests.cs', 'tests/PiSharp.McpSync.Tests/OccupiedCallbackRefreshTests.cs']),
  lifecycle: group(['I10'], ['src/PiSharp.Cli/Commands/RpcSessionCommand.cs', 'src/PiSharp.Rpc/Protocol/RpcSessionDispatcher.IdleShutdown.cs', 'src/PiSharp.Cli/ShutdownSignals.cs'], ['tests/PiSharp.CliParity.Tests/LifecycleGapCases.cs', 'tests/PiSharp.Rpc.Tests/RpcIdleShutdownTests.cs']),
  settings: group(['I11'], ['src/PiSharp.Durable.Hosting/SessionCompaction.cs', 'src/PiSharp.CustomAgents/Teams/AgentTeam.cs'], ['tests/PiSharp.Durable.Hosting.Tests/SettingsForwardingCases.cs', 'tests/PiSharp.CustomAgents.Tests/SettingsForwardingCases.cs']),
  sqlite: group(['I12'], ['src/PiSharp.Durable.Sqlite/Storage.cs', 'src/PiSharp.Durable.Sqlite/ConnectionDatabase.cs'], ['tests/PiSharp.Durable.Tests/Storage/SqliteStorageCases.cs', 'tests/PiSharp.Durable.Tests/Storage/SqliteFacadeCases.cs']),
  durable: group(['I17'], ['src/PiSharp.Durable/Harness/Agent.cs', 'src/PiSharp.Durable/Harness/Compaction.cs', 'src/PiSharp.Durable/Harness/Generation.cs', 'src/PiSharp.Durable/Harness/Models.cs', 'src/PiSharp.Durable/Harness/Types.cs', 'src/PiSharp.Durable/TypesSession.cs'], ['tests/PiSharp.Durable.Tests/GapDurableApiCases.cs', 'tests/PiSharp.Durable.Tests/GapDurableIdentityCases.cs', 'tests/PiSharp.DurableCli.Tests/GapDurableHostCases.cs'], 'Additive agent/checkpoint fields need no SQL schema bump; older binaries do not enforce the new fields. External ITx implementations retain a default unsupported lookup until they implement it.'),
  bulk: group(['I18'], ['src/PiSharp.Durable.Sqlite/Commit.cs', 'src/PiSharp.Durable.Sqlite/Storage.cs'], ['tests/PiSharp.Durable.Tests/Storage/SqliteBulkCases.cs'], 'No schema change. Final integrated conformance and statement-count/latency comparison remain required; source shape does not prove a speedup.'),
  shell: group(['I19'], ['src/PiSharp.Cli/Pi/PiAuthCommand.Login.cs', 'src/PiSharp.Cli/Commands/McpCommand.cs', 'src/PiSharp.Cli/Packages/PiPackageUpdateSummary.cs'], ['tests/PiSharp.CliParity.Tests/AuthCases.cs', 'tests/PiSharp.ExtensionParity.Tests/Sync150PackageCases.cs']),
  classifier: group(['I20', 'I20-F09-copilot'], ['src/PiSharp.CodingAgent/Schemas/models.schema.json', 'src/PiSharp.ModelRuntime/Models/ModelResolver.cs', 'src/PiSharp.AI/ModelOperations/CopilotSystemOneClassifier.cs'], ['tests/PiSharp.ClassifierConfig.Tests']),
  identity: group(['I21', 'I17'], ['src/PiSharp.Durable/Harness/Models.cs', 'src/PiSharp.Durable/Models/NativeDurableModels.cs', 'src/PiSharp.WebAgent/Routing/AccountRouter.cs', 'src/PiSharp.WebAgent/Routing/WebDurableModels.cs'], ['tests/PiSharp.Durable.Tests/ModelsIdentityCases.cs', 'tests/PiSharp.WebAgent.Tests/AccountPayloadHookCases.cs', 'tests/PiSharp.WebAgent.Tests/AccountRequestOptionsCases.cs'], 'Owner-selected no-attribution default is retained. Unsupported deferred providers remain explicitly unsupported, not silently emulated.'),
  incremental: group(['R5', 'R6', 'I02-incremental-arguments'], ['src/PiSharp.AI/Protocols/MistralConversations/TextState.cs', 'src/PiSharp.AI/Protocols/PiMessages/PiMessagesEventMapper.cs'], ['tests/PiSharp.MistralConversations.Tests/IncrementalSnapshotTests.cs'], 'Anthropic and Completions residuals have explicit source dispositions; full optional diagnostic captures may still materialize snapshots.'),
  responses: group(['R6'], ['src/PiSharp.AI/Protocols/OpenAIResponses/ResponsesTranscriptTools.cs', 'src/PiSharp.AI/Protocols/OpenAIResponses/ResponsesTranscriptProjector.cs', 'src/PiSharp.AI/Protocols/AzureResponses/AzureResponsesRequestFactory.cs', 'src/PiSharp.AI/Protocols/OpenAICodexResponses/OpenAICodexResponsesTransport.cs'], ['tests/PiSharp.ResponsesTranscriptTools.Tests']),
  exec: group(['R4'], ['src/PiSharp.Tools/Processes/NativeProcessRunner.cs', 'src/PiSharp.Tools/Processes/SeparatedProcessCapture.cs', 'src/PiSharp.Tools/Processes/Unix/UnixProcessRunner.cs'], ['tests/PiSharp.Tools.Tests/NativeProcessRunnerTests.cs', 'tests/PiSharp.Tools.Tests/UnixProcessRunnerTests.cs']),
  overlay: group(['R8'], ['src/PiSharp.Cli/Extensions/Pi/PiNodeRuntime.cs', 'src/PiSharp.Cli/Interactive/Mode/InteractiveMode.ExtensionHost.cs', 'tools/NodeBridge/pi-host/runtime.mjs', 'tools/NodeBridge/installed-runtime-abi.mjs'], ['tests/PiSharp.InteractiveParity.Tests/GapOverlayCases.cs', 'tests/PiSharp.ExtensionParity.Tests/GapRuntimeCases.cs'], 'Native overlay handles and installed-runtime ABI have explicit mappings; arbitrary JS objects and general JS/Node IPC equivalence are not claimed.'),
  modifiers: group(['R7'], ['src/PiSharp.Tui/Pi/RawTerminalIo.cs'], ['tests/PiSharp.Tui.NativeModifiers.Tests'], 'Physical macOS modifier input requires a macOS host acceptance run; synthetic tests cannot qualify it.'),
  unicode: group(['D16', 'W24-candidates'], ['src/PiSharp.Extensions.Abstractions/Mcp/Discovery/UnicodeWordSegmenter.cs', 'src/PiSharp.Extensions.Abstractions/Mcp/Discovery/ToolSearchRanker.cs'], ['tests/PiSharp.ToolSearch.Tests/UnicodeProfileCases.cs'], 'Culture/invariant/ICU profiles are tested separately; CLR sigma/NFC and word segmentation differences from V8 remain disclosed.'),
  tracker: group(['DLT'], ['src/PiSharp.Durable/Chord/Delta/Tracker.cs', 'src/PiSharp.Durable/Chord/Delta/Draft.cs'], ['tests/PiSharp.Durable.Tests/Chord/TrackerOpenChangeCases.cs'], 'Explicit CLR drafts replace revoked JS proxies; cleanup visits allocated draft nodes. Immutable range/container copies and microtask timing have native dispositions.'),
  telemetry: group(['TSQ'], ['src/PiSharp.Chord/TelemetrySqlite/SqliteTelemetry.cs', 'src/PiSharp.Chord/TelemetrySqlite/SqliteTelemetryStore.cs'], ['tests/PiSharp.Chord.Tests/TelemetrySqliteCases.cs', 'tests/PiSharp.Durable.Tests/SqliteTelemetryIntegrationCases.cs'], 'Opt-in recorder/query store; no automatic SQL capture or exporter is enabled.'),
  env: group(['E02', 'E03', 'E04', 'E05', 'E06', 'E07', 'E08', 'review-env'], ['src/PiSharp.Env/Connection.cs', 'src/PiSharp.Env/RemoteExecutionEnv.cs', 'src/PiSharp.Env/RemoteFileSystem.cs', 'src/PiSharp.Env/RemoteShell.cs', 'src/PiSharp.Env/RemoteWatch.cs', 'src/PiSharp.Env/RemoteTerminal.cs', 'src/PiSharp.Env/Ssh.cs', 'src/PiSharp.Env.Daemon/EnvDaemonSession.cs', 'src/PiSharp.Env.Daemon/Native/pisharp_env_native.c', 'src/PiSharp.Env.Protocol/FrameCodec.cs'], ['tests/PiSharp.Env.Adapter.Tests', 'tests/PiSharp.Env.Connection.Tests', 'tests/PiSharp.Env.Daemon.Tests', 'tests/PiSharp.Env.Process.Tests', 'tests/PiSharp.Env.Ssh.Tests', 'tests/PiSharp.Env.Terminal.Tests', 'tests/PiSharp.Env.Watch.Tests'], 'Native daemon artifact, real SSH and OS-specific process/PTY gates remain. Windows PTY is explicitly unsupported (ENOTSUP); Android and six-target NativeAOT support require actual artifact execution.'),
  legacyRuntime: group(['LG2', 'LG3'], ['src/PiSharp.Chord/Legacy/Contracts.cs', 'src/PiSharp.Chord/Legacy/Runtime/Host.cs', 'src/PiSharp.Chord/Legacy/Runtime/Schema.cs', 'src/PiSharp.Chord/Legacy/Runtime/Reference.cs', 'src/PiSharp.Chord/Legacy/Runtime/Reload.cs'], ['tests/PiSharp.Chord.Tests/Legacy/RuntimeCases.cs', 'tests/PiSharp.Chord.Legacy.Cluster.Tests'], 'Separate legacy contract/runtime and protocol 4; native declarations/ALC replace TypeScript evaluation. Platform process/collection gates remain.'),
  legacyLinks: group(['LG1', 'LG3'], ['src/PiSharp.Chord/Legacy/Protocol/LegacyFrame.cs', 'src/PiSharp.Chord/Legacy/Protocol/LegacyCodec.cs', 'src/PiSharp.Chord/Legacy/Protocol/LegacyConnection.cs', 'src/PiSharp.Chord/Legacy/Protocol/LegacyPeerSession.cs', 'src/PiSharp.Chord/Legacy/Links/LegacyLinks.cs', 'src/PiSharp.Chord/Legacy/Links/LegacyNativeTransports.cs'], ['tests/PiSharp.Chord.Tests/Legacy', 'tests/PiSharp.Chord.Legacy.Cluster.Tests'], 'Protocol 4 is distinct from next. Native framed pipes are not Node child_process IPC; actual browser/Node interoperability and OS transport gates remain.'),
  legacyCluster: group(['LG3'], ['src/PiSharp.Chord/Legacy/Cluster/LegacyCluster.cs', 'src/PiSharp.Chord/Legacy/Cluster/LegacyNetwork.cs', 'src/PiSharp.Chord/Legacy/Manager/LegacyHostManager.cs', 'src/PiSharp.Chord/Legacy/Manager/LegacyProcessDriver.cs', 'src/PiSharp.Chord/Legacy/Manager/LegacyFacetAssemblyLoader.cs'], ['tests/PiSharp.Chord.Legacy.Cluster.Tests'], 'Native module loader/locks/process transport have documented adaptations; non-Windows orphan cleanup and Node lock/IPC interoperability remain unqualified.'),
  host: group(['H1', 'H2'], ['src/PiSharp.Chord/Host/Host.cs', 'src/PiSharp.Chord/Host/Object.cs', 'src/PiSharp.Chord/Host/Follow.cs', 'src/PiSharp.Chord/Host/Reload.cs'], ['tests/PiSharp.Chord.Tests/Host'], 'CLR host/draft lifetime adaptations remain explicit; source completion is not a final integrated runtime pass.'),
  jobs: group(['J1'], ['src/PiSharp.Chord/Jobs/Definition.cs', 'src/PiSharp.Chord/Jobs/Job.cs', 'src/PiSharp.Chord/Jobs/PersistedRuns.cs', 'src/PiSharp.Chord/Documents/Transaction.cs'], ['tests/PiSharp.Chord.Tests/Jobs']),
  links: group(['L1', 'R1'], ['src/PiSharp.Chord/Link/Message.cs', 'src/PiSharp.Chord/Link/Link.cs', 'src/PiSharp.Chord/Link/Transports.cs', 'src/PiSharp.Chord/Remote/HostLink.cs', 'src/PiSharp.Chord/Remote/Relay.cs', 'src/PiSharp.Chord/Host/RemoteIntegration.cs'], ['tests/PiSharp.Chord.Tests/Link', 'tests/PiSharp.Chord.Tests/Remote'], 'Next protocol and native transports are separate from legacy protocol 4; cross-runtime/platform interoperability needs explicit execution.'),
  hosting: group(['N1'], ['src/PiSharp.Chord.Hosting/Plugins/PluginManager.cs', 'src/PiSharp.Chord.Hosting/Plugins/PluginRuntime.cs', 'src/PiSharp.Chord.Hosting/Http/ChordHttp.cs', 'src/PiSharp.Chord.Hosting/Workers/ProcessWorkers.cs'], ['tests/PiSharp.Chord.Hosting.Tests'], 'Native C# plugins/shared assemblies, OS lock and worker transport replace TypeScript/npm runtime behavior. Arbitrary Node plugins/browser bundling are not claimed.'),
  store: group(['X1'], ['src/PiSharp.Durable.Store/Storage.cs', 'src/PiSharp.Durable.Store/RegistryReader.cs', 'samples/Durable.Chord.Examples/Example35.cs', 'samples/Durable.Chord.Examples/Example36.cs', 'samples/Durable.Chord.Examples/Example37.cs'], ['tests/PiSharp.Chord.Durable.Integration.Tests'], 'Real-host examples and integration tests now exist; final execution and package-only consumption remain pending.'),
  corpus: group(['J1', 'X1', 'packaging-readiness'], ['tests/PiSharp.Chord.Tests/Schema/FullDeclarations.cs', 'tests/PiSharp.Chord.Tests/Schema/Proofs.cs', 'tests/PiSharp.Chord.Tests/Schema/CorpusCases.cs'], ['tests/PiSharp.Chord.Tests/Schema/CorpusCases.cs'], 'Full tables/jobs/real-host corpus source is present. Frozen recorded/schema/store artifacts remain original; comparison execution is pending.'),
  evals: group(['R9'], ['tools/PiSharp.Evals/Harness.cs', 'tools/PiSharp.Evals/Plan.cs', 'tools/PiSharp.Evals/Report.cs', 'tools/PiSharp.Evals/StagedRuntime.cs'], ['tests/PiSharp.Evals.Tests'], 'Offline staged runner implemented; live executable/container providers remain explicit opt-in and were not exercised by MAP.'),
  ci: group(['I13'], ['.github/workflows/core.yml', '.github/workflows/gap-regressions.yml', 'tools/ci/gap-suites.json', 'tools/ci/run_gap_suites.py'], ['tools/ci/test_gap_runner.py', 'tools/ci/validation-manifest.test.mjs'], 'Workflow/source checks do not establish a successful hosted three-OS or full integrated run.'),
  capture: group(['V04'], ['tools/DurableFixtures150/capture.mjs', 'tools/DurableFixtures150/worker.mjs'], ['tests/PiSharp.Durable150.Capture.Tests'], 'New bounded genuine v1.5.0 captures are separate from preserved historical corpora; C# full-scenario replay and all OS/provider/recovery coverage are not implied.'),
};

const merge = (...names) => {
  const values = names.map(name => { assert(groups[name], name); return groups[name]; });
  return Object.fromEntries(['reports', 'sourcePaths', 'testPaths'].map(key => [key, [...new Set(values.flatMap(v => v[key]))]]).concat([
    ['disposition', values.map(v => v.disposition).filter(Boolean).join(' ')]]));
};
const gapGroups = {
  C01:['schema'], C02:['anthropic'], C03:['sqlite'], C04:['settings'], C05:['files'], C06:['files'], C07:['lifecycle'], C08:['session'], C09:['refs'], C10:['mcp'], C11:['mcp'], C12:['lifecycle'], C13:['session'], C14:['session','files'], C15:['files'], C16:['files'], C17:['openai'], C18:['budget'], C19:['catalog'], C20:['openai'], C21:['openai'], C22:['shell'], C23:['sqlite'],
  F01:['durable'], F02:['durable'], F03:['durable','identity'], F04:['bulk'], F05:['openai'], F06:['files'], F07:['shell'], F08:['anthropic','lifecycle'], F09:['schema','catalog','classifier'],
  V01:['ci'], V02:['ci'], V03:['ci'], V04:['capture'], V05:['ci'],
};
const baselineGroups = {
  'ai.client-identity':['identity'], 'ai.transcript-tool-support':['responses'], 'ai.system-one-compat':['classifier'], 'ai.tool-call-args-incremental':['incremental'],
  'core.exec-max-buffer':['exec'], 'core.session-write-failure-rollback':['session'], 'cli.custom-overlay-own-handle':['overlay'], 'tui.native-shift-enter-esc-return':['modifiers'], 'tools.tool-search-unicode-words':['unicode'], 'bridge.pi-ai-extension-view':['overlay'],
  'durable.store-storage':['store'], 'durable.harness-identity':['identity'], 'durable.before-provider-request':['identity'], 'durable.registry-reader':['store'],
  'chord.delta-tracker-open-change':['tracker'], 'chord.sqlite-statement-cache':['sqlite'], 'chord.telemetry-sqlite':['telemetry'],
  'chord.contracts':['legacyRuntime'], 'chord.contract-schemas':['legacyRuntime'], 'chord.objects-declarations':['legacyRuntime'], 'chord.hosts-facets-principals':['legacyRuntime'], 'chord.references':['legacyRuntime','legacyLinks'],
  'chord.wire-protocol':['legacyLinks'], 'chord.links-transports':['legacyLinks'], 'chord.links-flow-control':['legacyLinks'], 'chord.links-compression':['legacyLinks'], 'chord.routes-relays':['legacyLinks','legacyCluster'], 'chord.reload':['legacyRuntime','legacyCluster'], 'chord.cluster-reload':['legacyCluster'], 'chord.kernel-retention':['legacyRuntime','legacyCluster'], 'chord.host-manager':['legacyCluster'], 'chord.registries-v1':['legacyRuntime'], 'chord.tests-docs':['legacyRuntime','legacyLinks','legacyCluster','host','jobs','links','hosting','store'],
  'chord.next-entry-point':['host','jobs','links','hosting'], 'chord.next-contracts-data':['corpus'], 'chord.next-documents-state':['host','jobs'], 'chord.next-host-objects':['host'], 'chord.next-reload':['host'], 'chord.next-jobs':['jobs'], 'chord.next-links-remote':['links'], 'chord.next-store':['store','host'], 'chord.next-registries':['store','host'], 'chord.next-plugins':['hosting'], 'chord.next-http':['hosting'], 'chord.next-workers':['hosting','links'], 'evals.harness':['evals'],
};
const nativeFailStop = 'Intentional native fail-stop after uncertain session writes is retained: later mutations fail rather than pretending a disk write rolled back. Incomplete-tail recovery and persistence ordering require final regression execution.';
const retained = new Set(['core.session-write-failure-rollback', 'tui.native-shift-enter-esc-return', 'tools.tool-search-unicode-words']);

export function buildStatus() {
  const applicability = read(applicabilityPath), register = read(registerPath);
  const baseline = applicability.rows.filter(row => ['port','ported-with-deferral'].includes(row.status));
  const baselineRows = baseline.map(row => {
    const names = row.id.startsWith('env.') ? ['env'] : baselineGroups[row.id];
    assert(names, `unmapped baseline row ${row.id}`);
    const mapped = merge(...names);
    if (row.id === 'core.session-write-failure-rollback') {
      mapped.disposition = nativeFailStop;
      mapped.sourcePaths = ['src/PiSharp.Sessions/Storage/SessionLogStore.cs','src/PiSharp.CodingAgent/PersistentAgentSession.cs','src/PiSharp.CodingAgent/PersistentAgentSession.Recovery.cs'];
    }
    return { id: row.id, historicalStatus: row.status, sourceStatus: retained.has(row.id) ? 'implemented-with-retained-native-difference' : 'source-implemented', runtimeStatus: pending, upstreamPaths: row.upstream, ...mapped };
  });
  const changes = Object.entries(gapGroups).map(([id,names]) => ({ id, sourceStatus: 'source-implemented', runtimeStatus: pending, ...merge(...names) }));
  const deferredPaths = {
    'packages/chord/src/delta/apply-immutable-trusted.ts': { groups:['tracker'], sourcePaths:['src/PiSharp.Durable/Chord/Delta/Apply.cs'], disposition:'Trusted immutable application is a native equivalent; CLR immutable container copying has no upstream JavaScript container optimization to port.' },
    'packages/chord/src/delta/index.ts': { groups:['tracker'], sourcePaths:['src/PiSharp.Durable/Chord/Delta/Ops.cs'], disposition:'Native immutable operations, appendText and pending operations exist; CLR range copying and decoder types are intentional language mappings.' },
    'packages/chord/src/delta/tracker.ts': { groups:['tracker'] },
    'packages/chord/src/sqlite/node.ts': { groups:['sqlite'] },
    'packages/durable/test/examples/35-durable-on-the-store.ts': { groups:['store'], sourcePaths:['samples/Durable.Chord.Examples/Example35.cs'] },
    'packages/durable/test/examples/36-durable-views.ts': { groups:['store'], sourcePaths:['samples/Durable.Chord.Examples/Example36.cs'] },
    'packages/durable/test/examples/37-harness-registry.ts': { groups:['store'], sourcePaths:['samples/Durable.Chord.Examples/Example37.cs'] },
    'packages/durable/test/harness-registry-reader.test.ts': { groups:['store'], sourcePaths:['tests/PiSharp.Durable.Tests/HarnessRegistryReaderCases.cs','tests/PiSharp.Chord.Durable.Integration.Tests'] },
    'packages/durable/test/store-storage.test.ts': { groups:['store'], sourcePaths:['tests/PiSharp.Durable.Tests/StoreStorageCases.cs','tests/PiSharp.Chord.Durable.Integration.Tests'] },
    'packages/chord/src/types.ts': { groups:['legacyRuntime'], sourcePaths:['src/PiSharp.Chord/Legacy/Runtime/Contracts.cs','src/PiSharp.Chord/Legacy/Runtime/Context.cs'], disposition:'The former release exclusion of the legacy host types is historical. A distinct native legacy host/runtime now exists; CLR declaration and lifetime mappings remain explicit.' },
    'packages/durable/src/harness/agent.ts': { groups:['durable'], sourcePaths:['src/PiSharp.Durable/Harness/Agent.cs'] },
    'packages/durable/src/harness/types.ts': { groups:['durable'], sourcePaths:['src/PiSharp.Durable/Harness/Types.cs'] },
    'packages/durable/src/harness/compaction.ts': { groups:['durable'], sourcePaths:['src/PiSharp.Durable/Harness/Compaction.cs'] },
    'packages/durable/src/harness/generation.ts': { groups:['durable'], sourcePaths:['src/PiSharp.Durable/Harness/Generation.cs'] },
    'packages/durable/src/types.ts': { groups:['durable'], sourcePaths:['src/PiSharp.Durable/Types.cs','src/PiSharp.Durable/TypesSession.cs'] },
    'packages/durable/src/session/transaction.ts': { groups:['durable'], sourcePaths:['src/PiSharp.Durable/Session/Transaction.cs'] },
    'packages/durable/src/storage/sqlite/storage.ts': { groups:['sqlite','bulk'], sourcePaths:['src/PiSharp.Durable.Sqlite/Storage.cs','src/PiSharp.Durable.Sqlite/Commit.cs'] },
    'packages/durable/src/tool-search/tool-search.ts': { groups:['refs'], sourcePaths:['src/PiSharp.Durable.Tools/ToolSearch/ToolSearch.cs'] },
    'packages/durable/src/harness/registry-reader.ts': { groups:['store'], sourcePaths:['src/PiSharp.Durable.Store/RegistryReader.cs'] },
    'packages/durable/src/storage/store/storage.ts': { groups:['store'], sourcePaths:['src/PiSharp.Durable.Store/Storage.cs'] },
  };
  const durableFiles = Object.entries(deferredPaths).map(([upstream, spec]) => {
    const historical = read(portMapPath).files.find(row=>row.upstream===upstream);
    assert(historical, upstream);
    const mapped = merge(...spec.groups);
    return { upstream, historicalStatus: historical.status, sourceStatus:'source-implemented', runtimeStatus:pending, ...mapped, ...(spec.sourcePaths ? {sourcePaths:spec.sourcePaths}:{}), ...(spec.disposition ? {disposition:spec.disposition}:{}) };
  });
  return {
    schemaVersion:1, updatedOn:'2026-11-08', productBaseline:register.productCommit,
    upstreamRelease:{version:'1.5.0',commit:'f9a26365568127461c47a07a37dcb42c140a3ac5'}, upstreamCandidate:register.upstreamCandidate,
    authority:'Current source/test mapping only; historical statuses, pins, captures and passing-test evidence remain separate. A sourceStatus never asserts a passing runtime, performance result, package consumer or cross-platform run.',
    validationOwner:'I13 / root, serialized integrated validation', runtimeStatus:pending,
    statusMeaning:{'source-implemented':'Mapped source and authored tests are present; execution is not inferred.','implemented-with-retained-native-difference':'A native implementation/disposition is present; the stated language/platform difference remains.'},
    historicalInventories:[applicabilityPath,portMapPath,'compatibility/implementation-status.json'],
    historicalEvidenceDigests:{algorithm:'SHA-256 of recursively key-sorted JSON, preserving array order, with current metadata removed and historicalPisharp restored',...historicalDigests},
    validationArtifacts:['AI_DOCS/validation-status.json','tools/ci/gap-suites.json'],
    packaging:{sourceStatus:'prepared',runtimeStatus:pending,libraries:['PiSharp.Chord','PiSharp.Chord.Hosting','PiSharp.Durable.Store','PiSharp.Env.Protocol','PiSharp.Env'],daemon:'PiSharp.Env.Daemon is explicitly nonpackable as a library; native artifact qualification is separate.',contract:'docs/contracts/gap-package-admission.md',consumer:'tests/packaging/GapConsumer',published:false},
    platformAndEvidenceGates:[
      'Cloudflare Durable Object/Images/SQLite adapters and browser IndexedDB remain platform exclusions; no equivalent execution is claimed.',
      'Windows PTY returns ENOTSUP; Unix/macOS PTY, process groups, native static linking, Android and six-target daemon artifacts need their own actual runs.',
      'Native framed pipes, C# plugins and module/lock adapters do not establish Node child_process IPC, arbitrary npm/TypeScript plugin or browser compatibility.',
      'Physical macOS input, real SSH, real browser/Node interoperability and live providers are not covered by MAP offline metadata validation.',
      'CLR explicit drafts, immutable copies and scheduling differ from JavaScript proxy revocation, ownedSlice and microtask-count assertions.',
      'Final integrated suites, stable-source receipts, new capture consumers, package-only consumers and SQL latency measurements remain separate gates. Preliminary worker results do not close them.'
    ], changes, baselineRows, durableFiles,
    additionalRows:[{id:'bridge.undici-gondolin-advisories',historicalStatus:'ported',sourceStatus:'source-implemented',runtimeStatus:pending,...merge('overlay'),testReplacements:{
      'tests/PiSharp.ExtensionParity.Tests/Sync150RuntimeCases.cs: sync150.pi-runtime-manifest-pins-undici-and-keeps-pi-1.2.0':'tests/PiSharp.ExtensionParity.Tests/Sync150RuntimeCases.cs: sync150.pi-runtime-manifest-pins-undici-and-pi-1.5.0'
    },disposition:'The approved current Node runtime uses Pi 1.5.0 while retaining the undici 8.12.3 override. Historical Pi 1.2.0 release choice and test reference remain preserved; this replacement names the current authored test, not a passing execution.'}],
    summary:{changes:changes.length,baselineRows:baselineRows.length,durableFiles:durableFiles.length,additionalRows:1,sourceStatuses:Object.fromEntries([...new Set([...changes,...baselineRows,...durableFiles].map(x=>x.sourceStatus))].map(status=>[status,[...changes,...baselineRows,...durableFiles].filter(x=>x.sourceStatus===status).length+(status==='source-implemented'?1:0)]))},
  };
}

export function validateStatus(status) {
  checkHistoricalEvidence();
  assert.equal(status.baselineRows.length,56);
  assert.equal(status.changes.length,37);
  assert.equal(status.durableFiles.length,20);
  assert.equal(status.additionalRows.length,1);
  assert.equal(Object.values(status.summary.sourceStatuses).reduce((sum,count)=>sum+count,0),status.changes.length+status.baselineRows.length+status.durableFiles.length+status.additionalRows.length);
  for(const entries of [status.changes,status.baselineRows,status.durableFiles,status.additionalRows]) {
    assert.equal(new Set(entries.map(row=>row.id??row.upstream)).size,entries.length);
    for(const row of entries) {
      assert(row.sourceStatus in status.statusMeaning);
      assert.equal(row.runtimeStatus,pending);
      for(const key of ['reports','sourcePaths','testPaths']) {
        assert(row[key].length>0,`${row.id??row.upstream}: ${key}`);
        for(const path of row[key]) {
          assert(!path.includes(' (new)')&&!path.includes('..')&&!path.includes('\\')&&!path.startsWith('/'),path);
          assert(existsSync(resolve(root,path)),`${row.id??row.upstream}: missing ${path}`);
        }
      }
    }
  }
  assert.equal(status.packaging.published,false);
}

export function attachCurrentMappings(status) {
  const table=read(applicabilityPath), map=read(portMapPath);
  const explanation='Release-baseline status/notes and immutable upstream pins are retained; currentImplementation points to the current source mapping. Source implementation is not runtime acceptance.';
  table.currentImplementation={mapping:statusPath,statusScope:explanation};
  for(const entry of [...status.baselineRows,...status.additionalRows]) {
    const row=table.rows.find(row=>row.id===entry.id);
    row.historicalPisharp??=row.pisharp;
    row.pisharp=entry.sourcePaths;
    row.currentImplementation={mapping:statusPath,id:entry.id,sourceStatus:entry.sourceStatus,runtimeStatus:entry.runtimeStatus,note:entry.disposition||'Source and authored tests are present; final integrated validation is pending.',...(entry.testReplacements?{testReplacements:entry.testReplacements}:{})};
  }
  map.currentImplementation={...map.currentImplementation,mapping:statusPath,statusScope:explanation};
  for(const entry of status.durableFiles) {
    const row=map.files.find(row=>row.upstream===entry.upstream);
    row.historicalPisharp??=row.pisharp;
    row.pisharp=entry.sourcePaths[0];
    row.currentImplementation={mapping:statusPath,upstream:entry.upstream,sourceStatus:entry.sourceStatus,runtimeStatus:entry.runtimeStatus,sourcePaths:entry.sourcePaths,note:entry.disposition||'Source and authored tests are present; final integrated validation is pending.'};
  }
  return {[applicabilityPath]:table,[portMapPath]:map};
}

if(process.argv[1]&&resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  const status=buildStatus();validateStatus(status);
  const outputs={[statusPath]:status,...attachCurrentMappings(status)};
  if(process.argv.includes('--write')) for(const [path,value] of Object.entries(outputs)) writeFileSync(resolve(root,path),JSON.stringify(value,null,2)+'\n');
  else for(const [path,value] of Object.entries(outputs)) assert.deepEqual(read(path),value,`${path} is stale; run with --write`);
  console.log(`Current gap mapping: ${status.changes.length} changes, ${status.baselineRows.length} baseline rows, ${status.durableFiles.length} Durable files; integrated validation pending.`);
}
