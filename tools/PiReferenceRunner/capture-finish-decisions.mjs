import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { compareRawJson, parseJsonSupported } from '../CompatibilityReport/raw-json.mjs';

const scriptPath = fileURLToPath(import.meta.url);
const toolRoot = dirname(scriptPath);
const repo = resolve(toolRoot, '../..');
const fixtureRoot = join(repo, 'fixtures/pi-v0.99.1/finish-decisions');
const inputPath = join(fixtureRoot, 'core.input.json');
const expectedPath = join(fixtureRoot, 'core.expected.json');
const manifestPath = join(fixtureRoot, 'manifest.json');
const lockPath = join(fixtureRoot, 'reference.lock.json');
const reportPath = join(fixtureRoot, 'capture-report.json');
const rawPaths = [join(fixtureRoot, 'capture-1.raw.json'), join(fixtureRoot, 'capture-2.raw.json')];
const environmentLockPath = join(toolRoot, 'full-lock.json');
const captureKind = 'captured-upstream-finish-decisions-oracle';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const fileHash = path => hash(readFileSync(path));
const read = path => parseJsonSupported(readFileSync(path, 'utf8'));
const clone = value => structuredClone(value);

function gate() {
  let release;
  const promise = new Promise(resolve => { release = resolve; });
  return { promise, release };
}

async function captureChild() {
  const oracle = resolve(process.env.PISHARP_REFERENCE_ORACLE);
  const fixture = read(inputPath);
  const { runAgentLoop } = await import(pathToFileURL(join(oracle, 'upstream/packages/agent/src/agent-loop.ts')).href);
  const { createAssistantMessageEventStream } = await import(pathToFileURL(join(oracle, 'upstream/packages/ai/src/utils/event-stream.ts')).href);
  async function captureScenario(scenario) {
    const finishEntered = gate();
    const finishRelease = gate();
    const controller = new AbortController();
    const requests = [], events = [], providerEmissions = [], hooks = [], tools = [], queues = [], controls = [], order = [];
    const steering = [], followUp = [];
    let requestCount = 0, finishCount = 0, toolExecutionCount = 0, finalContextMessages;
    const record = (list, kind, entry) => {
      const index = list.length;
      list.push(clone(entry));
      order.push({ kind, index });
    };
    const signalState = signal => ({ signalSupplied: signal === controller.signal, signalAborted: signal?.aborted ?? false });
    const turnSnapshot = turn => ({ message: turn.message, toolResults: turn.toolResults, contextMessages: turn.context.messages, newMessages: turn.newMessages });
    const tool = {
      name: fixture.tool.name,
      label: fixture.tool.name,
      description: fixture.tool.description,
      parameters: clone(fixture.tool.parameters),
      async execute(id, args, signal) {
        toolExecutionCount++;
        record(tools, 'tool', { kind: 'execute', id, args, ...signalState(signal) });
        return clone(fixture.toolResult);
      },
    };
    const emit = async event => { record(events, 'event', event); };
    const streamFn = (model, context, options) => {
      const requestIndex = requestCount++;
      assert.ok(requestIndex < scenario.providerTurns.length, 'Provider request exceeds authored fake responses');
      assert.equal(options.apiKey, undefined, 'No credential may enter the fake stream');
      record(requests, 'request', { model, context, optionMetadata: { ownKeys: Object.keys(options).sort(), apiKeyOwnProperty: Object.hasOwn(options, 'apiKey'), apiKeyUndefined: options.apiKey === undefined, reasoningOwnProperty: Object.hasOwn(options, 'reasoning'), ...signalState(options.signal) } });
      const finalMessage = clone(scenario.providerTurns[requestIndex]);
      const stream = createAssistantMessageEventStream();
      const push = event => {
        record(providerEmissions, 'provider_emission', { requestIndex, event });
        stream.push(event);
      };
      if (finalMessage.stopReason === 'error' || finalMessage.stopReason === 'aborted') {
        if (scenario.abortOnProviderTerminal) {
          controller.abort('authored-provider-abort');
          record(controls, 'control', { kind: 'abort_controller', reason: controller.signal.reason, signalAborted: controller.signal.aborted });
        }
        push({ type: 'error', reason: finalMessage.stopReason, error: finalMessage });
      } else {
        const partial = clone(finalMessage);
        partial.content = [];
        partial.stopReason = 'pending';
        push({ type: 'start', partial: clone(partial) });
        for (let contentIndex = 0; contentIndex < finalMessage.content.length; contentIndex++) {
          const block = finalMessage.content[contentIndex];
          if (block.type === 'text') {
            partial.content.push({ type: 'text', text: '' });
            push({ type: 'text_start', contentIndex, partial: clone(partial) });
            partial.content[contentIndex] = clone(block);
            push({ type: 'text_delta', contentIndex, delta: block.text, partial: clone(partial) });
            push({ type: 'text_end', contentIndex, content: block.text, partial: clone(partial) });
          } else {
            assert.equal(block.type, 'toolCall', 'Only authored text/tool-call blocks belong to this corpus');
            partial.content.push({ type: 'toolCall', id: block.id, name: block.name, arguments: {} });
            push({ type: 'toolcall_start', contentIndex, partial: clone(partial) });
            push({ type: 'toolcall_delta', contentIndex, delta: JSON.stringify(block.arguments), partial: clone(partial) });
            partial.content[contentIndex] = clone(block);
            push({ type: 'toolcall_end', contentIndex, toolCall: clone(block), partial: clone(partial) });
          }
        }
        push({ type: 'done', reason: finalMessage.stopReason, message: finalMessage });
      }
      return stream;
    };
    const config = {
      model: clone(fixture.model),
      convertToLlm(messages) { record(hooks, 'hook', { kind: 'convert_to_llm', messages }); return messages; },
      async prepareRequest(request, signal) { record(hooks, 'hook', { kind: 'prepare_request', contextMessages: request.context.messages, model: request.model, thinkingLevel: request.thinkingLevel, ...signalState(signal) }); },
      async prepareNextTurn(turn) { record(hooks, 'hook', { kind: 'prepare_next_turn', ...turnSnapshot(turn) }); },
      async finishTurn(turn, signal) {
        const turnIndex = finishCount++;
        finalContextMessages = clone(turn.context.messages);
        record(hooks, 'hook', { kind: 'finish_enter', turnIndex, ...turnSnapshot(turn), ...signalState(signal) });
        if (turnIndex === 0) {
          record(controls, 'control', { kind: 'finish_gate_enter' });
          finishEntered.release();
          await finishRelease.promise;
          record(controls, 'control', { kind: 'finish_gate_release' });
        }
        const authored = scenario.finishDecisions[turnIndex];
        assert.ok(['end', 'continue', 'undefined'].includes(authored), 'Every finish invocation needs an authored callback return');
        const decision = authored === 'undefined' ? undefined : { action: authored };
        record(hooks, 'hook', { kind: 'finish_return', turnIndex, returnedUndefined: decision === undefined, ...(decision ? { decision } : {}), ...signalState(signal) });
        return decision;
      },
      async beforeToolCall(context, signal) { record(tools, 'tool', { kind: 'preflight', id: context.toolCall.id, args: context.args, contextMessages: context.context.messages, ...signalState(signal) }); },
      async afterToolCall(context, signal) { record(tools, 'tool', { kind: 'after', id: context.toolCall.id, result: context.result, isError: context.isError, contextMessages: context.context.messages, ...signalState(signal) }); },
      async getSteeringMessages() { const messages = steering.splice(0); record(queues, 'queue', { kind: 'steering_poll', messages }); return messages; },
      async getFollowUpMessages() { const messages = followUp.splice(0); record(queues, 'queue', { kind: 'follow_up_poll', messages }); return messages; },
    };
    const hasAuthoredTools = scenario.providerTurns.some(turn => turn.content.some(block => block.type === 'toolCall'));
    const running = runAgentLoop(clone(scenario.prompts), { messages: [], tools: hasAuthoredTools ? [tool] : [] }, config, emit, controller.signal, streamFn);
    const settlement = running.then(result => ({ result }), error => ({ error }));
    const reached = await Promise.race([finishEntered.promise.then(() => ({ gate: true })), settlement]);
    if (!reached.gate) throw reached.error ?? new Error('Loop settled before the authored finish gate');
    assert.equal(requestCount, 1, 'Awaited finish callback must block the next provider request');
    assert.equal(events.filter(event => event.type === 'turn_end').length, 0, 'finishTurn gate must block turn_end');
    assert.equal(events.filter(event => event.type === 'agent_end').length, 0, 'finishTurn gate must block agent_end');
    record(controls, 'control', { kind: 'finish_gate_probe', requestCount, finishCount, turnEndCount: 0, agentEndCount: 0, toolExecutionCount, committedToolResultCount: finalContextMessages.filter(message => message.role === 'toolResult').length, steeringPollCount: queues.filter(entry => entry.kind === 'steering_poll').length, followUpPollCount: queues.filter(entry => entry.kind === 'follow_up_poll').length });
    for (const message of scenario.steeringMessages) { steering.push(clone(message)); record(queues, 'queue', { kind: 'enqueue_steering', message }); }
    for (const message of scenario.followUpMessages) { followUp.push(clone(message)); record(queues, 'queue', { kind: 'enqueue_follow_up', message }); }
    finishRelease.release();
    const settled = await settlement;
    if (settled.error) throw settled.error;
    const finalResult = settled.result;
    return { scenarioId: scenario.scenarioId, requests, events, providerEmissions, hooks, tools, queues, controls, order, finalResult: clone(finalResult), finalContextMessages, remainingSteering: clone(steering), remainingFollowUp: clone(followUp), effects: [], checks: { finishGateBlockedTurnEnd: true, finishGateBlockedNextRequest: true, providerRequestCount: requestCount, finishTurnCount: finishCount, prepareNextTurnCount: hooks.filter(entry => entry.kind === 'prepare_next_turn').length, toolExecutionCount, turnStartCount: events.filter(event => event.type === 'turn_start').length, turnEndCount: events.filter(event => event.type === 'turn_end').length, terminalAgentEndCount: events.filter(event => event.type === 'agent_end').length, steeringPollCount: queues.filter(entry => entry.kind === 'steering_poll').length, followUpPollCount: queues.filter(entry => entry.kind === 'follow_up_poll').length, remainingSteeringCount: steering.length, remainingFollowUpCount: followUp.length, finalSignalAborted: controller.signal.aborted, returnedMessageRoles: finalResult.map(message => message.role) } };
  }
  const originalNow = Date.now;
  Date.now = () => fixture.clock.unixMilliseconds;
  try {
    const scenarios = [];
    for (const scenario of fixture.scenarios) scenarios.push(await captureScenario(scenario));
    console.log(JSON.stringify({ observations: { scenarios }, loadedModules: [...globalThis.pisharpLoadedReferenceModules.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0) }));
  } finally { Date.now = originalNow; }
}

async function runCapture() {
  const args = process.argv.slice(2);
  const accepted = new Set(['--capture-new', '--oracle']);
  for (let i = 0; i < args.length; i++) {
    if (!accepted.has(args[i])) throw new Error(`Unknown argument: ${args[i]}`);
    if (args[i] === '--oracle') { if (!args[i + 1] || args[i + 1].startsWith('--')) throw new Error('--oracle requires approved task-local oracle directory'); i++; }
  }
  const oracleIndex = args.indexOf('--oracle');
  const oracle = oracleIndex < 0 ? resolve(repo, '../Pi-reference-oracle-v0.99.1') : resolve(args[oracleIndex + 1]);
  const upstream = join(oracle, 'upstream');
  const firstCapture = args.includes('--capture-new');
  const frozenOutputPaths = [expectedPath, manifestPath, lockPath, reportPath, ...rawPaths];
  if (firstCapture && frozenOutputPaths.some(existsSync)) throw new Error('Initial finish-decision capture refuses preexisting expected output, manifest, lock, report or raw capture');
  const fixture = read(inputPath);
  if (fixture.fixtureId !== 'finish-decisions-core' || fixture.kind !== 'authored-finish-decisions-input' || fixture.normalizerVersion !== 'object-key-order-v1') throw new Error('Unexpected finish-decision input identity/provenance/normalizer');
  const environmentLock = read(environmentLockPath);
  const pins = environmentLock.environmentPins;
  const harnessPaths = ['tools/PiReferenceRunner/capture-finish-decisions.mjs', 'tools/PiReferenceRunner/full-preload.mjs', 'tools/PiReferenceRunner/offline-guard.mjs', 'tools/CompatibilityReport/raw-json.mjs'];
  function git(...gitArgs) {
    const child = spawnSync('C:/Program Files/Git/cmd/git.exe', ['-c', `safe.directory=${upstream}`, '-C', upstream, ...gitArgs], { windowsHide: true, maxBuffer: 64 * 1024 * 1024 });
    if (child.status !== 0) throw new Error(child.error?.message ?? child.stderr.toString());
    return child.stdout;
  }
  const consultedSourceHashes = ['packages/agent/src/types.ts'].map(path => {
    const bytes = readFileSync(join(upstream, path));
    const blob = git('show', `${pins.sourceSha}:${path}`);
    if (!bytes.equals(blob)) throw new Error(`Consulted source differs from canonical pinned Git bytes: ${path}`);
    return { path: `upstream/${path}`, sha256: hash(bytes), bytes: bytes.length, canonicalGitBlobSha256: hash(blob), canonicalGitBlobBytes: blob.length, use: 'Read-only FinishTurn/public callback type contract; not runtime-loaded.' };
  });
  function verifyEnvironment() {
    if (fixture.sourceSha !== pins.sourceSha || git('rev-parse', 'HEAD').toString().trim() !== pins.sourceSha || git('rev-parse', 'HEAD^{tree}').toString().trim() !== pins.sourceTree || git('status', '--porcelain', '--untracked-files=all').toString().trim()) throw new Error('Finish-decision source must remain clean at qualified SHA/tree');
    if (process.version !== pins.runtime.version || fileHash(process.execPath) !== pins.runtime.sha256) throw new Error('Node executable differs from qualified runtime');
    if (fileHash(join(oracle, 'package.json')) !== pins.projectionManifestSha256 || fileHash(join(oracle, 'package-lock.json')) !== pins.projectionLockSha256) throw new Error('Oracle dependency projection differs from qualified environment');
    if (JSON.stringify(readdirSync(join(oracle, 'node_modules')).filter(name => !name.startsWith('.')).sort()) !== JSON.stringify(pins.dependencies.map(dependency => dependency.name).sort())) throw new Error('Unexpected installed oracle dependency');
    for (const dependency of pins.dependencies) {
      const root = join(oracle, 'node_modules', dependency.name), files = [];
      function visit(directory) {
        for (const name of readdirSync(directory).sort()) {
          const path = join(directory, name), stat = lstatSync(path);
          if (stat.isSymbolicLink()) throw new Error('Unexpected dependency symlink');
          if (stat.isDirectory()) visit(path);
          else if (stat.isFile()) files.push({ path: relative(root, path).replaceAll('\\', '/'), sha256: fileHash(path), bytes: stat.size });
          else throw new Error('Unexpected dependency file type');
        }
      }
      visit(root);
      const manifest = read(join(root, 'package.json'));
      if (manifest.name !== dependency.name || manifest.version !== dependency.version || fileHash(join(root, 'package.json')) !== dependency.manifestSha256 || hash(JSON.stringify(files)) !== dependency.treeSha256 || files.length !== dependency.fileCount || files.reduce((sum, file) => sum + file.bytes, 0) !== dependency.bytes) throw new Error(`Dependency package bytes differ from qualified pins: ${dependency.name}`);
    }
    for (const path of harnessPaths.slice(1)) {
      const qualified = environmentLock.harnessFiles.find(file => file.path === path);
      if (!qualified || fileHash(join(repo, path)) !== qualified.sha256) throw new Error(`Reused harness differs from qualified bytes: ${path}`);
    }
    for (const file of [...environmentLock.sourceHashes, ...consultedSourceHashes]) if (fileHash(join(oracle, file.path)) !== file.sha256) throw new Error(`Qualified source bytes changed: ${file.path}`);
  }
  function verifyLock() {
    const lock = read(lockPath), manifest = read(manifestPath), row = manifest.fixtures[0];
    if (lock.captureKind !== captureKind || lock.sourceSha !== fixture.sourceSha || fileHash(environmentLockPath) !== lock.environmentLockSha256 || !compareRawJson(JSON.stringify(lock.environmentPins), JSON.stringify(pins)) || !compareRawJson(JSON.stringify(lock.consultedSourceHashes), JSON.stringify(consultedSourceHashes))) throw new Error('Finish-decision source/environment lock changed');
    for (const file of lock.harnessFiles) if (fileHash(join(repo, file.path)) !== file.sha256) throw new Error(`Finish-decision harness bytes changed: ${file.path}`);
    for (const file of lock.loadedModules) if (fileHash(join(oracle, file.path)) !== file.sha256) throw new Error(`Loaded oracle module bytes changed: ${file.path}`);
    if (manifest.sourceSha !== fixture.sourceSha || manifest.normalizerVersion !== fixture.normalizerVersion || row.fixtureId !== fixture.fixtureId || row.provenance.kind !== captureKind || !compareRawJson(JSON.stringify(row.clock), JSON.stringify(fixture.clock)) || fileHash(inputPath) !== row.input.sha256 || fileHash(expectedPath) !== row.expected.sha256) throw new Error('Finish-decision fixture identity/checksum changed');
    for (const raw of row.rawCaptures) if (fileHash(join(repo, raw.path)) !== raw.sha256) throw new Error('Retained raw capture changed');
    return lock;
  }
  verifyEnvironment();
  const priorLock = firstCapture ? undefined : verifyLock();
  const harnessFiles = harnessPaths.map(path => ({ path, sha256: fileHash(join(repo, path)) }));
  const inputSha256 = fileHash(inputPath), environmentLockSha256 = fileHash(environmentLockPath);
  const rawCaptures = [], captures = [];
  const scratchBase = resolve(fixtureRoot, '.scratch');
  mkdirSync(scratchBase, { recursive: true });
  for (let repeat = 0; repeat < 2; repeat++) {
    const isolated = mkdtempSync(join(scratchBase, 'pisharp-finish-decisions-'));
    try {
      const home = join(isolated, 'home'), workspace = join(isolated, 'workspace');
      mkdirSync(home); mkdirSync(workspace);
      const child = spawnSync(process.execPath, ['--experimental-strip-types', '--import', pathToFileURL(join(toolRoot, 'full-preload.mjs')).href, scriptPath, '--capture-child'], { cwd: workspace, env: { SystemRoot: process.env.SystemRoot, WINDIR: process.env.WINDIR, USERPROFILE: home, HOME: home, APPDATA: home, LOCALAPPDATA: home, TMP: isolated, TEMP: isolated, TZ: 'UTC', PISHARP_REFERENCE_ORACLE: oracle }, encoding: 'utf8', windowsHide: true, timeout: 20000, maxBuffer: 32 * 1024 * 1024 });
      if (child.status !== 0) throw new Error(`Finish-decision child capture failed: ${child.error?.message ?? child.stderr}`);
      rawCaptures.push(child.stdout);
      captures.push(parseJsonSupported(child.stdout));
    } finally {
      const target = resolve(isolated), within = relative(scratchBase, target);
      if (!within || isAbsolute(within) || within.startsWith('..') || !within.startsWith('pisharp-finish-decisions-')) throw new Error('Refusing cleanup outside verified temporary capture directory');
      rmSync(target, { recursive: true, force: true });
    }
  }
  if (rawCaptures[0] !== rawCaptures[1]) throw new Error('Two fresh finish-decision captures differ byte-for-byte');
  verifyEnvironment();
  if (fileHash(inputPath) !== inputSha256 || fileHash(environmentLockPath) !== environmentLockSha256) throw new Error('Input/environment lock changed during capture');
  for (const file of harnessFiles) if (fileHash(join(repo, file.path)) !== file.sha256) throw new Error(`Harness changed during capture: ${file.path}`);
  const loadedModules = captures[0].loadedModules, sourceHashes = [];
  for (const module of loadedModules) {
    const qualified = environmentLock.loadedModules.find(file => file.path === module.path);
    if (!qualified || qualified.sha256 !== module.sha256 || qualified.bytes !== module.bytes) throw new Error(`Runtime closure escaped qualified source/dependencies: ${module.path}`);
    if (module.path.startsWith('upstream/')) {
      const blob = git('show', `${pins.sourceSha}:${module.path.slice('upstream/'.length)}`);
      if (hash(blob) !== module.sha256 || blob.length !== module.bytes) throw new Error(`Runtime source differs from canonical Git blob: ${module.path}`);
      sourceHashes.push({ ...module, canonicalGitBlobSha256: hash(blob), canonicalGitBlobBytes: blob.length });
    }
  }
  if (priorLock) {
    verifyLock();
    if (!compareRawJson(JSON.stringify(priorLock.loadedModules), JSON.stringify(loadedModules)) || !compareRawJson(JSON.stringify(priorLock.sourceHashes), JSON.stringify(sourceHashes))) throw new Error('Loaded finish-decision closure differs from frozen lock');
  }
  const actual = { fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha, kind: captureKind, normalizerVersion: fixture.normalizerVersion, observations: captures[0].observations };
  const actualRaw = `${JSON.stringify(actual, null, 2)}\n`;
  const rawPins = rawPaths.map((path, index) => ({ path: relative(repo, path).replaceAll('\\', '/'), sha256: hash(rawCaptures[index]), bytes: Buffer.byteLength(rawCaptures[index]) }));
  if (firstCapture) {
    const lock = { schemaVersion: 1, captureKind, sourceSha: pins.sourceSha, environmentLockPath: 'tools/PiReferenceRunner/full-lock.json', environmentLockSha256, environmentPins: pins, consultedSourceHashes, loadedModules, sourceHashes, harnessFiles };
    writeFileSync(lockPath, `${JSON.stringify(lock, null, 2)}\n`, { flag: 'wx' });
    writeFileSync(expectedPath, actualRaw, { flag: 'wx' });
    rawPaths.forEach((path, index) => writeFileSync(path, rawCaptures[index], { flag: 'wx' }));
    const manifest = { schemaVersion: 1, sourceSha: fixture.sourceSha, normalizerVersion: fixture.normalizerVersion, scope: 'Seven genuine unchanged low-level runAgentLoop finishTurn captures with fake provider/tool/callback environment; full Agent/session/provider/native acceptance remains open.', fixtures: [{ fixtureId: fixture.fixtureId, sourceSha: fixture.sourceSha, requirementIds: fixture.requirementIds, scenario: fixture.scenario, scenarioIds: fixture.scenarios.map(scenario => scenario.scenarioId), clock: fixture.clock, seed: fixture.seed, environment: { platform: process.platform, architecture: process.arch, credentials: 'not inherited', network: 'blocked by reused offline guard', workspace: 'two fresh isolated temporary homes/workspaces' }, input: { path: 'fixtures/pi-v0.99.1/finish-decisions/core.input.json', sha256: inputSha256 }, expected: { path: 'fixtures/pi-v0.99.1/finish-decisions/core.expected.json', sha256: hash(actualRaw) }, rawCaptures: rawPins, provenance: { kind: captureKind, inputKind: fixture.kind, source: 'Unchanged pinned runAgentLoop and AssistantMessageEventStream; explicit fake streamFn/tool/public finish/prepare/queue callbacks; disclosed harness Date.now override restored in finally.', resolver: 'Unchanged pinned packages/coding-agent/src/experimental/source-resolver.ts', dependencyLock: 'fixtures/pi-v0.99.1/finish-decisions/reference.lock.json', environmentLock: 'tools/PiReferenceRunner/full-lock.json', capturedAt: '2026-09-30', captureCommand: 'node tools/PiReferenceRunner/capture-finish-decisions.mjs --capture-new', providerWireTraffic: false, nativeDifferential: false, highLevelAgentQueueModes: false, abortScope: 'Authored aborted terminal response plus supplied controller.abort; no network/provider cancellation claim.' } }] };
    writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`, { flag: 'wx' });
  }
  const matched = compareRawJson(readFileSync(expectedPath, 'utf8'), actualRaw);
  const report = { schemaVersion: 1, fixtureId: fixture.fixtureId, captureKind, sourceSha: fixture.sourceSha, capturedInitialGolden: firstCapture, repeatRuns: 2, byteIdenticalRepeats: true, deterministic: true, matched, sourceCleanBefore: true, sourceCleanAfter: true, inputSha256, expectedSha256: fileHash(expectedPath), manifestSha256: fileHash(manifestPath), lockSha256: fileHash(lockPath), captureHarnessSha256: fileHash(scriptPath), environmentLockSha256, rawCaptureSha256: hash(rawCaptures[0]), loadedUpstreamSourceFiles: sourceHashes.length, loadedDependencyFiles: loadedModules.length - sourceHashes.length, consultedTypeOnlySourceFiles: consultedSourceHashes.length, checks: actual.observations.scenarios.map(scenario => ({ scenarioId: scenario.scenarioId, ...scenario.checks })), scope: 'Genuine unchanged public low-level finishTurn scheduling capture under disclosed authored environment; no full Agent, native differential, production provider or phase completion claim.' };
  if (firstCapture) writeFileSync(reportPath, `${JSON.stringify(report, null, 2)}\n`, { flag: 'wx' });
  console.log(JSON.stringify(report, null, 2));
  if (!matched) process.exitCode = 1;
}

if (process.argv.length === 3 && process.argv[2] === '--capture-child') await captureChild();
else await runCapture();
