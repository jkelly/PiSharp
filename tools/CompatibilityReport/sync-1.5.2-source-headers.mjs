#!/usr/bin/env node
// Header-only attribution audit. The explicit map follows reviewed area reports; never guesses a source for an unmatched file.
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const commit = 'b4afdddf44e059c67ccf5097e5317d913521b470';
const upstream = process.argv[process.argv.indexOf('--upstream') + 1];
if (!process.argv.includes('--upstream')) throw new Error('--upstream is required');
const tree = new Set(execFileSync('git', ['-c', `safe.directory=${upstream.replace(/\\/g, '/')}`, '-C', upstream, 'ls-tree', '-r', '--name-only', commit], { encoding: 'utf8', maxBuffer: 1 << 28 }).trim().split('\n'));
const mapping = new Map();
const add = (file, sources, note = '') => mapping.set(file, { sources: [sources].flat(), note });
const native = (file, note) => mapping.set(file, { native: note });
const chord = suffix => `packages/chord/src/${suffix}.ts`;
const app = 'src/PiSharp.Chord.Hosting/App/';
for (const [name, source] of Object.entries({ App: 'app', AppOptions: 'app', Blobs: 'blobs', EphemeralLogin: 'ephemeral', EphemeralSessions: 'ephemeral-sessions',
  Files: 'files', FilesCopy: 'files-copy', FilesEntry: 'files-entry', FilesIndex: 'files-index', FolderViews: 'folder-views', Info: 'info',
  Invite: 'invite', Owner: 'owner', Pairing: 'pairing', Sessions: 'sessions', Uploads: 'uploads', Users: 'users', WorkerApp: 'worker' }))
  add(`${app}${name}.cs`, chord(`node/app/${source}`));
add('src/PiSharp.Chord.Hosting/Plugins/BaseInputs.cs', chord('node/plugins/base-inputs'));
add('src/PiSharp.Chord.Hosting/Plugins/PluginInstaller.cs', chord('node/plugins/install'));
add('src/PiSharp.Chord.Hosting/Plugins/PluginTesting.cs', chord('node/plugins/testing'));
native('src/PiSharp.Chord.Hosting/Plugins/PluginPolicy.cs', 'CLR plugin manifest, assembly metadata and dependency-admission adapter; see sync-1.5.2-plugin-contract.md.');
add('src/PiSharp.Chord.Hosting/Surfaces/PublishedScripts.cs', [chord('node/surfaces/published'), chord('node/surfaces/served')]);
add('src/PiSharp.Chord.Hosting/Surfaces/SurfaceBuild.cs', chord('node/surfaces/surface-build'));
add('src/PiSharp.Chord.Hosting/Surfaces/SurfaceScripts.cs', chord('node/surfaces/current'));
native('src/PiSharp.Chord.Hosting/Surfaces/SurfaceArtifacts.cs', 'Explicit native compiler/artifact adapter boundary; see sync-1.5.2-chord-publication-seam.md.');
const terminal = 'src/PiSharp.Chord.Hosting/Terminal/';
for (const [name, paths] of Object.entries({ Bridge: ['tui/bridge'], TerminalActions: ['node/tui/actions'],
  TerminalChannel: ['tui/channel', 'tui/terminal'], TerminalLauncher: ['node/tui/launcher'], TerminalNetwork: ['node/tui/credentials', 'node/tui/launcher'],
  TerminalOutput: ['node/tui/filter', 'tui/frames'], TerminalPipe: ['node/tui/pipe'], TerminalRuntime: ['tui/runtime'], TerminalScope: ['tui/scope', 'tui/theme'] }))
  add(`${terminal}${name}.cs`, paths.map(chord), 'Native adapters retain explicit trusted-code and terminal ownership boundaries.');
native(`${terminal}PiTerminalIo.cs`, 'Adapter from the existing PiSharp terminal to the Chord terminal I/O contract; no JavaScript execution.');
add('src/PiSharp.Chord.Hosting/Workers/WorkerBase.cs', chord('node/workers/base'), 'Native deployment snapshots replace JavaScript build artifacts.');
add('src/PiSharp.Chord/Host/OldWork.cs', chord('host/old-work'));
add('src/PiSharp.Chord/Host/Tracing.cs', [chord('telemetry'), chord('host/host')]);
add('src/PiSharp.Chord/Telemetry.cs', chord('telemetry'));
add('src/PiSharp.Chord/TelemetrySqlite/SqliteTelemetryReader.cs', [chord('telemetry-sqlite/store'), chord('telemetry-sqlite/node')]);
add('src/PiSharp.Chord/TelemetrySqlite/SqliteTelemetrySchema.cs', chord('telemetry-sqlite/schema'));
native('src/PiSharp.Durable.Store/TelemetrySqlite.cs', 'File-opening adapter connecting the native SQLite database to the Chord telemetry reader.');
native('src/PiSharp.Chord/Sync/BodyStreams.cs', 'Bounded .NET stream adapters for the source-derived sync batch/line codec; see sync-1.5.2-chord-sync-codec.md.');
native('src/PiSharp.AI/Properties/StrictToolLimitTestsAccess.cs', 'Assembly visibility for the authored strict-tool cache regressions.');
native('src/PiSharp.Tools/Files/PrefixReadStream.cs', 'Stream adapter replaying the sniffed prefix and bounding the native read tool to its opened-file snapshot.');
add('src/PiSharp.CodingAgent/PersistentAgentSession.InputNormalization.cs', 'packages/coding-agent/src/core/agent-session.ts', 'Native session-delivery integration for queued image normalization.');
add('src/PiSharp.Telemetry/TelemetryClock.cs', 'packages/telemetry/src/index.ts', 'telemetryNow clock with native TimeProvider integration.');
for (const [file, source] of Object.entries({
  'src/PiSharp.Cli/Mcp/Authentication/McpSignIn.Client.cs': 'oauth/sign-in',
  'src/PiSharp.Extensions.Abstractions/Mcp/Authentication/McpSignInContracts.cs': 'oauth/types',
  'src/PiSharp.Extensions.Abstractions/Mcp/Transport/McpPipeContracts.cs': 'transports/pipe',
  'src/PiSharp.Extensions.Runtime/Mcp/Authentication/McpOAuthAuthProvider.cs': 'oauth/auth-provider',
  'src/PiSharp.Extensions.Runtime/Mcp/Authentication/McpOAuthSignIn.cs': 'oauth/sign-in',
  'src/PiSharp.Extensions.Runtime/Mcp/Resources/McpContentLimiter.cs': 'output-limit',
  'src/PiSharp.Extensions.Runtime/Mcp/Transport/McpJsonRpcLineReader.cs': 'transports/lines',
  'src/PiSharp.Extensions.Runtime/Mcp/Transport/McpPipeTransport.cs': 'transports/pipe',
})) add(file, `packages/mcp/src/${source}.ts`);

const tests = {
  'PiSharp.Chord.Hosting.Tests/AppAuthRouteTests.cs': ['node/app/owner', 'node/app/pairing', 'node/app/invite', 'node/app/ephemeral'],
  'PiSharp.Chord.Hosting.Tests/AppLayoutTests.cs': ['node/app/layout', 'node/app/secrets'],
  'PiSharp.Chord.Hosting.Tests/AppStartTests.cs': ['node/app/app', 'node/app/worker'],
  'PiSharp.Chord.Hosting.Tests/ArchiveCases.cs': ['node/app/archive', 'node/app/tar'],
  'PiSharp.Chord.Hosting.Tests/FilesCases.cs': ['node/app/files', 'node/app/files-copy', 'node/app/uploads'],
  'PiSharp.Chord.Hosting.Tests/Http152Cases.cs': ['node/http/http'],
  'PiSharp.Chord.Hosting.Tests/PluginDiscoveryTests.cs': ['node/plugins/discover', 'node/plugins/install', 'node/plugins/testing'],
  'PiSharp.Chord.Hosting.Tests/SurfacePublicationTests.cs': ['node/surfaces/surface-build', 'node/surfaces/published'],
  'PiSharp.Chord.Hosting.Tests/Terminal152Cases.cs': ['node/tui/launcher', 'node/tui/actions', 'tui/bridge'],
  'PiSharp.Chord.Hosting.Tests/WorkerSnapshotCases.cs': ['node/workers/base', 'node/workers/workers'],
  'PiSharp.Chord.Tests/Contracts/HostKeyedCases.cs': ['contracts/host-kind', 'contracts/contract'],
  'PiSharp.Chord.Tests/Documents/HostKeyedCases.cs': ['documents/documents', 'documents/handles'],
  'PiSharp.Chord.Tests/Host/HostDataCases.cs': ['host/host-data'],
  'PiSharp.Chord.Tests/Host/HostDataTelemetryCases.cs': ['host/host-data', 'telemetry'],
  'PiSharp.Chord.Tests/Host/Sync152KernelCases.cs': ['host/old-work', 'host/readiness', 'host/reload-cycle'],
  'PiSharp.Chord.Tests/Link/Link152Cases.cs': ['link/binary', 'link/text', 'link/channel'],
  'PiSharp.Chord.Tests/Remote/Remote152Cases.cs': ['remote/consumer', 'remote/provider', 'remote/relay'],
  'PiSharp.Chord.Tests/Store/HostColumnCases.cs': ['store/host-column'],
  'PiSharp.Chord.Tests/Store/HostDataCases.cs': ['store/host-data'],
  'PiSharp.Chord.Tests/Store/LiveQueryCases.cs': ['store/live-query'],
  'PiSharp.Chord.Tests/Surfaces/ConnectionCases.cs': ['surfaces/connection', 'surfaces/client'],
  'PiSharp.Chord.Tests/Surfaces/IntegrationCases.cs': ['surfaces/active-theme', 'surfaces/active-language', 'surfaces/page-state'],
  'PiSharp.Chord.Tests/Surfaces/SurfaceCases.cs': ['surfaces/themes', 'surfaces/messages', 'surfaces/language'],
  'PiSharp.Chord.Tests/Sync/CodecCases.cs': ['sync/batch', 'sync/lines'],
  'PiSharp.Chord.Tests/Sync/FileCases.cs': ['sync/files', 'sync/filesystem'],
  'PiSharp.Chord.Tests/Sync/SessionCases.cs': ['sync/client', 'sync/state', 'sync/remote'],
  'PiSharp.Chord.Tests/Sync/SchedulerCases.cs': ['sync/client'],
  'PiSharp.Chord.Tests/Telemetry152Cases.cs': ['telemetry'],
  'PiSharp.Chord.Tests/TelemetryIngestCases.cs': ['telemetry-ingest/recorder', 'telemetry-ingest/sender', 'telemetry-ingest/facet'],
  'PiSharp.Chord.Tests/UnboundClockTests.cs': ['host/unbound-clock'],
};
for (const [path, source] of Object.entries(tests)) add(`tests/${path}`, source.map(chord), 'Authored native regression; not an upstream execution capture.');
for (const [path, source] of Object.entries({
  'PiSharp.CodingAgent.Tests/QueuedImageNormalizationTests.cs': ['packages/coding-agent/src/core/agent-session.ts'],
  'PiSharp.Extensions.ContractTests/McpSync152TransportTests.cs': ['packages/mcp/src/transports/pipe.ts', 'packages/mcp/src/transports/lines.ts'],
  'PiSharp.InteractiveParity.Tests/Sync152SessionCases.cs': ['packages/coding-agent/src/modes/interactive/interactive-mode.ts'],
  'PiSharp.InteractiveParity.Tests/Sync152SmallUiCases.cs': ['packages/coding-agent/src/modes/interactive/interactive-mode.ts'],
  'PiSharp.InteractiveParity.Tests/Sync152UiFlowCases.cs': ['packages/coding-agent/src/modes/interactive/interactive-mode.ts'],
  'PiSharp.McpSync.Tests/Sync152McpCases.cs': ['packages/mcp/src/oauth/flow.ts', 'packages/mcp/src/client.ts'],
  'PiSharp.ModelRuntime.Tests/CatalogRefreshAuthCases.cs': ['packages/ai/src/models.ts'],
  'PiSharp.ProviderSync.Tests/ProviderRouteCacheCases.cs': ['packages/ai/src/api/anthropic-messages.ts', 'packages/ai/src/api/openai-responses.ts'],
  'PiSharp.Telemetry.Tests/ClockRender152Cases.cs': ['packages/telemetry/src/index.ts', 'packages/telemetry/src/render.ts'],
  'PiSharp.Tools.Tests/Sync152ToolsCases.cs': ['packages/coding-agent/src/core/tools/read.ts', 'packages/coding-agent/src/core/tools/edit.ts'],
  'PiSharp.Tools.Tests/Sync152EditExpectations.cs': ['packages/coding-agent/src/core/tools/edit.ts'],
  'PiSharp.ProviderSync.Tests/StrictSessionCases.cs': ['packages/ai/src/api/strict-tool-limits.ts', 'packages/ai/src/api/anthropic-messages.ts', 'packages/ai/src/api/bedrock-converse-stream.ts'],
  'PiSharp.ProviderSync.Tests/AnthropicCatalogBindingCases.cs': ['packages/ai/src/api/anthropic-messages.ts', 'packages/ai/src/providers/anthropic.ts'],
  'PiSharp.Durable.Tests/Storage/SqliteDetailedBulkCases.cs': ['packages/durable/src/storage/sqlite/commit.ts', 'packages/durable/test/sqlite-commit.test.ts'],
  'PiSharp.Tui.Tests/Sync152TuiTests.cs': ['packages/tui/src/components/markdown.ts', 'packages/tui/src/tui-main-screen.ts', 'packages/tui/src/utils.ts', 'packages/tui/src/terminal-image.ts'],
})) add(`tests/${path}`, source, 'Authored native regression; not an upstream execution capture.');
native('tests/PiSharp.Durable.Tests/Storage/SqlServerCorruptionCases.cs', 'Authored regression for the native SQL Server adapter; no upstream execution capture.');
native('tests/PiSharp.InteractiveParity.Tests/LovelyMermaidCases.cs', 'Authored integration for lovely-mermaid 0.3.3 (Apache-2.0); upstream package attribution remains in the ported LovelyMermaid files.');

for (const { sources = [] } of mapping.values()) for (const path of sources)
  if (!tree.has(path)) throw new Error(`Attribution source does not exist at ${commit}: ${path}`);
const repoGit = (...args) => execFileSync('git', ['-c', `safe.directory=${repo.replace(/\\/g, '/')}`, '-C', repo, ...args], { encoding: 'utf8' }).trim().split('\n').filter(Boolean);
const files = [...new Set([...repoGit('ls-files', '--others', '--exclude-standard', '--', '*.cs'),
  ...repoGit('diff', '--cached', '--name-only', '--diff-filter=A', 'HEAD', '--', '*.cs')])];
const report = { commit, changed: [], covered: [], unmatched: [] };
for (const file of files) {
  const full = resolve(repo, file), bytes = readFileSync(full), bom = bytes.subarray(0, 3).equals(Buffer.from([0xef, 0xbb, 0xbf]));
  const text = bytes.subarray(bom ? 3 : 0).toString('utf8'), head = text.split(/\r?\n/).slice(0, 6).join('\n');
  if (/(?:Pi [0-9a-f]{7,40}|PiSharp-only|Apache|Copyright|auto-generated|generated)/.test(head)) { report.covered.push(file); continue; }
  const entry = mapping.get(file);
  if (!entry) { report.unmatched.push(file); continue; }
  const eol = text.includes('\r\n') ? '\r\n' : '\n';
  const header = entry.native ? `// PiSharp-only: ${entry.native}` : `// Pi ${commit} (MIT): ${entry.sources.join(', ')}.${entry.note ? `${eol}// ${entry.note}` : ''}`;
  if (process.argv.includes('--write')) {
    const output = Buffer.concat([bom ? Buffer.from([0xef, 0xbb, 0xbf]) : Buffer.alloc(0), Buffer.from(header + eol), bytes.subarray(bom ? 3 : 0)]);
    writeFileSync(full, output);
  }
  report.changed.push({ file, header });
}
if (process.argv.includes('--report')) writeFileSync(resolve(repo, process.argv[process.argv.indexOf('--report') + 1]), JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify({ changed: report.changed.length, covered: report.covered.length, unmatched: report.unmatched }, null, 2));
