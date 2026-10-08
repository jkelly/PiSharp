// Review-only harness: unchanged source callbacks, authored offline host inputs.
// No execution allocation is implied by this file.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createCommandInputLoader } from './module-loader.mjs';

const sha = path => createHash('sha256').update(fs.readFileSync(path)).digest('hex');
const repo = resolve(fileURLToPath(new URL('../..', import.meta.url)));
const roots = Object.fromEntries(process.argv.slice(2).map(value => {
  const split = value.indexOf('='); assert(split > 0, 'Expected key=absolute-path');
  return [value.slice(0, split), resolve(value.slice(split + 1))];
}));
assert.deepEqual(Object.keys(roots).sort(), ['commandInputReference', 'jiti', 'oracle', 'reference']);
const planPath = join(repo, 'compatibility/node/command-input-bridge.plan.json');
const plan = JSON.parse(fs.readFileSync(planPath));
const environmentKeys = ['PISHARP_REAL_EXTENSION_ORACLE', 'JITI_FS_CACHE'];
const savedEnvironment = new Map(environmentKeys.map(key => [key, process.env[key]]));
let environmentConfigured = false;
async function preflight() {
  assert.equal(sha(planPath), 'fde6bae32f17a92d4abc3130772a31d91abeab8bb3c2440ad2196c5d26924d11', 'Approved bridge plan identity');
  assert.equal(process.version, plan.runtime.version); assert.equal(process.platform, plan.runtime.platform);
  assert.equal(process.arch, plan.runtime.architecture); assert.equal(sha(process.execPath), plan.runtime.sha256);
  const approvedOracle = resolve('P:/PiSharp/root/Documents/Codex/2026-09-30/task-2/Pi-extension-reducers-oracle-v0.99.1-license-corrected');
  assert.equal(roots.oracle.toLowerCase(), approvedOracle.toLowerCase(), 'Only existing approved oracle root is admitted');
  const canonicalPath = join(roots.reference, 'compatibility/node/real-extension-reference.plan.json');
  const manifestPath = join(roots.reference, 'fixtures/reference/node-real-extensions/manifest.json');
  assert.equal(sha(canonicalPath), 'e55b7af94907598157809fc6da08ad1d60899c23b849d3623c28174d5d7fc622', 'Canonical reference plan identity');
  assert.equal(sha(manifestPath), 'b74a7ece33b0596850266a1e1686b3c9cd1d5c978e3dcae1f07174adc3757ff9', 'Canonical inventory identity');
  const canonical = JSON.parse(fs.readFileSync(canonicalPath));
  const manifest = JSON.parse(fs.readFileSync(manifestPath));
  assert.equal(resolve(canonical.oracle).toLowerCase(), approvedOracle.toLowerCase(), 'Pinned canonical oracle identity');
  for (const row of manifest.inputs.harness) {
    const path = join(roots.reference, row.path);
    assert.equal(fs.statSync(path).size, row.bytes); assert.equal(sha(path), row.sha256);
  }
  const common = await import(pathToFileURL(join(roots.reference, 'tools/NodeExtensionReference/common.mjs')).href);
  common.noLinks(approvedOracle);
  assert.equal(resolve(fs.realpathSync.native(approvedOracle)).toLowerCase(), approvedOracle.toLowerCase(), 'Physical oracle identity');
  // Verify the whole admitted source and fifteen dependency inventories before assigning the variable.
  assert.deepEqual(common.verifyBase(canonical), manifest.inputs.base, 'Approved oracle source/dependency inventories');
  for (const row of plan.sourcePins) {
    const path = join(approvedOracle, 'upstream', row.path);
    assert.equal(fs.statSync(path).size, row.bytes); assert.equal(sha(path), row.sha256);
  }
  process.env.PISHARP_REAL_EXTENSION_ORACLE = approvedOracle;
  process.env.JITI_FS_CACHE = 'false';
  environmentConfigured = true;
  return { status: 'passed', oracleIdentityVerifiedBeforeAssignment: true, fullBaseInventoryMatched: true,
    processLocalEnvironment: { PISHARP_REAL_EXTENSION_ORACLE: approvedOracle, JITI_FS_CACHE: 'false' },
    otherPathContract: 'Jiti and reference roots are explicit CLI inputs verified by the unchanged loader before factories. JITI_CACHE_DIR is unused with filesystem cache disabled. PI_CODING_AGENT_DIR is used by discovery; this harness calls explicit loadExtensions, never discovery.',
    inheritedEnvironmentAuthority: 'Only the two named process-local variables change. No machine/user configuration changes.' };
}

const report = {
  scope: 'Node-only original source execution through approved bridge loader; authored assertions and host receipts',
  hostAuthority: 'No native host. All host callback receipts below are authored test inputs, including raw loader fields containing native labels.',
  nativeCSharpExecuted: false, nativeEndToEndQualified: false, providerCalls: 0,
  runtime: { version: process.version, sha256: sha(process.execPath) },
  sourceCommit: plan.sourceCommit, planSha256: sha(planPath), harnessSha256: sha(fileURLToPath(import.meta.url)),
  cases: [], hostCallbacks: [], cleanup: { finalized: false },
  unsupportedCapabilities: plan.sourceBoundary.unsupported,
};
const signal = new AbortController().signal;
const ui = { mode: 'print', connectionGeneration: 0, sessionGeneration: 1, features: [] };
const hostCall = async (method, data) => {
  assert.equal(method, 'ui.notify', 'Unexpected host capability request');
  assert(report.hostCallbacks.length < 2, 'Unexpected notification count');
  const receipt = { published: false, outcome: 'unavailable', reason: 'NoUi' };
  report.hostCallbacks.push({ authority: 'authored unavailable notification receipt', method, data, receipt });
  return receipt;
};
const noHost = async method => assert.fail('Unexpected host callback: ' + method);
let source, failed = false;
const failure = error => ({ name: error?.name, message: error?.message ?? String(error), stack: error?.stack });
async function check(id, action) {
  const row = { id, status: 'running' }; report.cases.push(row);
  try { row.observation = await action(); row.status = 'passed'; }
  catch (error) { row.status = 'failed'; row.failure = failure(error); throw error; }
}
try {
  report.preflight = { status: 'running', diagnostic: 'Verify approved oracle identity and full inventory before configuring controlled virtual-module environment' };
  report.preflight = await preflight();
  source = await createCommandInputLoader({ repo, ...roots });
  let registered, command, hook, tool;
  await check('original-factories-and-translated-registration', async () => {
    registered = await source.load(repo, 1, null, [
      'packages/coding-agent/examples/extensions/pirate.ts',
      'packages/coding-agent/examples/extensions/hello.ts',
    ]);
    assert.equal(registered.sourceFactoryCount, 2);
    assert.equal(registered.successfulSourceFactoryInvocations, 2);
    assert.equal(registered.sourceCommit, plan.sourceCommit);
    assert.equal(registered.commands.length, 1); assert.equal(registered.commands[0].name, 'pirate');
    assert.equal(registered.beforeAgentStartHandlers.length, 1);
    assert.equal(registered.tools.length, 1); assert.equal(registered.tools[0].name, 'hello');
    assert.equal(registered.tools[0].originalSchemaRetained, true);
    assert.equal(registered.inputHandlers.length, 0);
    command = registered.commands[0].callbackId; hook = registered.beforeAgentStartHandlers[0].callbackId;
    tool = registered.tools[0].callbackId;
    return registered;
  });
  const event = { type: 'before_agent_start', prompt: 'Offline prompt', systemPrompt: 'Original system prompt' };
  const invoke = (kind, id, argument, callback = noHost, toolId) =>
    source.invoke(kind, id, argument, [], 'authored-offline-catalog-1', ui, signal, callback, toolId);
  const settled = row => { assert.equal(row.status, 'fulfilled'); assert.equal(row.publicationJoined, true); assert.equal(row.signalAfter.aborted, false); };
  await check('pirate-original-inactive-hook', async () => {
    const row = await invoke('before_agent_start', hook, event); settled(row);
    assert.equal(row.resultPresence, 'undefined'); assert.equal(row.publicationCount, 0); return row;
  });
  await check('pirate-original-toggle-on-and-prompt', async () => {
    const toggle = await invoke('command', command, '', hostCall); settled(toggle);
    assert.equal(toggle.resultPresence, 'undefined'); assert.equal(toggle.publicationCount, 1);
    assert.deepEqual(report.hostCallbacks[0].data, { message: 'Arrr! Pirate mode enabled!', kind: 'info' });
    const hookResult = await invoke('before_agent_start', hook, event); settled(hookResult);
    const expected = event.systemPrompt + `

IMPORTANT: You are now in PIRATE MODE. You must:
- Speak like a stereotypical pirate in all responses
- Use phrases like "Arrr!", "Ahoy!", "Shiver me timbers!", "Avast!", "Ye scurvy dog!"
- Replace "my" with "me", "you" with "ye", "your" with "yer"
- Refer to the user as "matey" or "landlubber"
- End sentences with nautical expressions
- Still complete the actual task correctly, just in pirate speak
`;
    assert.deepEqual(JSON.parse(hookResult.resultJson), { systemPrompt: expected });
    assert.equal(hookResult.suppliedBefore.serializedJson, hookResult.suppliedAfter.serializedJson);
    return { toggle, hookResult };
  });
  await check('pirate-original-toggle-off-and-prompt', async () => {
    const toggle = await invoke('command', command, '', hostCall); settled(toggle);
    assert.equal(toggle.publicationCount, 1);
    assert.deepEqual(report.hostCallbacks[1].data, { message: 'Pirate mode disabled', kind: 'info' });
    const hookResult = await invoke('before_agent_start', hook, event); settled(hookResult);
    assert.equal(hookResult.resultPresence, 'undefined'); return { toggle, hookResult };
  });
  let prepared;
  await check('hello-original-live-schema-preparation', async () => {
    prepared = source.prepare(tool, { name: 'Joe' }, signal);
    assert.equal(prepared.status, 'fulfilled'); assert.equal(prepared.hostCapabilitiesGranted, false);
    assert.deepEqual(JSON.parse(prepared.preparedJson), { name: 'Joe' });
    assert.deepEqual(prepared.schemaBefore, prepared.schemaAfter); return prepared;
  });
  await check('hello-original-required-name-rejection', async () => {
    const row = source.prepare(tool, {}, signal);
    assert.equal(row.status, 'rejected'); assert.equal(row.hostCapabilitiesGranted, false);
    assert.match(row.thrown.serializedJson, /name/); return row;
  });
  await check('hello-original-five-argument-execution', async () => {
    const row = await invoke('tool', tool, JSON.parse(prepared.preparedJson), noHost, 'authored-hello-call-1'); settled(row);
    assert.equal(row.suppliedArgumentCount, 5);
    assert.equal(row.context.actualNativeToolCallId, 'authored-hello-call-1');
    assert.equal(row.context.owner, 'genuine ExtensionRunner.createToolContext');
    assert.deepEqual(row.context.toolContextShape, { toolsGetter: true, executeToolMethod: true });
    assert.deepEqual(JSON.parse(row.resultJson), { content: [{ type: 'text', text: 'Hello, Joe!' }], details: { greeted: 'Joe' } });
    assert.equal(row.publicationCount, 0); assert.equal(report.hostCallbacks.length, 2); return row;
  });
} catch (error) {
  failed = true; report.failure = failure(error);
  if (report.preflight?.status === 'running') report.preflight = { status: 'failed', failure: failure(error), environmentConfigured };
}
finally {
  if (source) {
    try { report.cleanup = await source.finalize(); assert.equal(report.cleanup.immutableInputsVerified, true); assert.equal(report.cleanup.invalidated, true); }
    catch (error) { failed = true; report.cleanupFailure = failure(error); }
  }
  if (environmentConfigured) {
    for (const [key, value] of savedEnvironment) {
      if (value === undefined) delete process.env[key]; else process.env[key] = value;
    }
  }
  report.environmentRestored = environmentKeys.every(key => process.env[key] === savedEnvironment.get(key));
  report.passed = report.cases.filter(row => row.status === 'passed').length;
  report.failed = report.cases.filter(row => row.status === 'failed').length;
  report.status = failed ? 'failed' : 'passed';
  process.stdout.write(JSON.stringify(report) + '\n');
  process.exitCode = failed ? 1 : 0;
}
