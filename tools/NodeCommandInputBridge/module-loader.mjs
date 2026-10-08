// Trusted, bounded registration successor. Whole public loader/Runner/functions remain in their original realm.
import assert from 'node:assert/strict';
import { createToolLoadoutCache, invokeLoadoutPreparation } from './tool-loadout.mjs';
import { selectSources, translateRegistrations } from './admission.mjs';
import { createToolProgress, createNotificationPublication } from './tool-progress.mjs';
import { createOriginalToolContext, unsupportedToolOperation } from './tool-context.mjs';
import { createOriginalRendererOwner } from './renderer-owner.mjs';
import { createSessionManagerFacade, validateSessionEvent } from './session-manager-facade.mjs';
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { AsyncLocalStorage } from 'node:async_hooks';
import { registerHooks, syncBuiltinESMExports } from 'node:module';
import { join, resolve, relative, sep, basename, isAbsolute } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const qualificationCommands = Object.freeze(['qualification-input', 'qualification-editor', 'qualification-timeout', 'qualification-preabort', 'qualification-child', 'qualification-abort', 'qualification-parent', 'qualification-session']);
export function validatePrivateQualificationLease(value, ownerId, ownerGeneration) {
  assert(value && typeof value === 'object' && !Array.isArray(value));
  assert.deepEqual(Object.keys(value).sort(), ['leaseId', 'ownerId', 'ownerGeneration', 'sourceRoot', 'sourcePath', 'bytes', 'sha256', 'sourceCommit'].sort());
  assert(typeof value.leaseId === 'string' && /^ui-qualification-[0-9a-f]{32}$/u.test(value.leaseId));
  assert(typeof ownerId === 'string' && ownerId.length > 0 && ownerId.length <= 128 && value.ownerId === ownerId);
  assert(Number.isSafeInteger(ownerGeneration) && ownerGeneration > 0 && value.ownerGeneration === ownerGeneration);
  assert(value.bytes === 2960 && value.sha256 === '8e27790e1b5aa480c0e44595e98026b3d130fcb8a789b3b3aed2b5d335d336c8' && value.sourceCommit === 'd86654abb8862e201933517d6f1fce9f88dd117f');
  for (const field of ['sourceRoot', 'sourcePath']) assert(typeof value[field] === 'string' && value[field].length <= 4096 && isAbsolute(value[field]) && !value[field].includes('\0'));
  assert(within(value.sourceRoot, value.sourcePath) && basename(value.sourcePath) === 'private-original-ui-qualification-plugin.mjs');
  return Object.freeze({ ...value }); // Echo of the native held lease; this is not independent trust authority.
}
export function validatePrivateQualificationRegistrations(registrations, sourcePath) {
  assert.deepEqual(registrations.commands.map(row => row.name).sort(), [...qualificationCommands].sort());
  assert(registrations.commands.every(row => row.sourcePath === sourcePath && typeof row.handler === 'function' && row.completion === undefined));
  for (const field of ['inputs', 'beforeAgentStart', 'sessionHandlers', 'tools']) assert(registrations[field].length === 0);
}
export function createPrivateQualificationSource(lease, ownerId, ownerGeneration, { read = fs.readFileSync, lstat = fs.lstatSync, realpath = fs.realpathSync } = {}) {
  const descriptor = validatePrivateQualificationLease(lease, ownerId, ownerGeneration); let retiring = false, closed = false, closeError;
  const samePath = path => resolve(path).toLowerCase() === resolve(descriptor.sourcePath).toLowerCase();
  const matches = path => !retiring && !closed && samePath(path);
  const assertActive = () => assert(!retiring && !closed, 'Private qualification source retired');
  const verify = supplied => {
    assert(!closed, 'Private qualification source retired');
    let currentPath = resolve(descriptor.sourceRoot); assert(!lstat(currentPath).isSymbolicLink(), 'Private root link');
    for (const part of relative(currentPath, descriptor.sourcePath).split(sep)) { currentPath = join(currentPath, part); assert(!lstat(currentPath).isSymbolicLink(), 'Private source ancestor link'); }
    assert.equal(realpath(descriptor.sourceRoot).toLowerCase(), resolve(descriptor.sourceRoot).toLowerCase(), 'Private root canonical identity');
    assert.equal(realpath(descriptor.sourcePath).toLowerCase(), resolve(descriptor.sourcePath).toLowerCase(), 'Private source canonical identity');
    const bytes = supplied ?? read(descriptor.sourcePath); assert.equal(bytes.length, descriptor.bytes); assert.equal(hash(bytes), descriptor.sha256);
  };
  const beginClose = () => { retiring = true; };
  const close = () => { if (closed) { if (closeError) throw closeError; return; } beginClose(); try { verify(); } catch (error) { closeError = error; throw error; } finally { closed = true; } };
  verify(); return { descriptor, ownsPath: samePath, matches, assertActive, verify, beginClose, close };
}
// Capture the genuine owner-realm brands before any original plugin is loaded.
const dialogSignalAborted = Object.getOwnPropertyDescriptor(AbortSignal.prototype, 'aborted').get;
const dialogAddListener = EventTarget.prototype.addEventListener;
const dialogRemoveListener = EventTarget.prototype.removeEventListener;
const dialogMethods = new Set(['ui.select', 'ui.confirm', 'ui.input']);
const dialogFailure = errors => { const originals = [...new Set(errors)]; return originals.length === 1 ? originals[0] : new AggregateError(originals, 'Dialog originals failed'); };
export function decodeOriginalDialogOutcome(method, result) {
  assert(dialogMethods.has(method) || method === 'ui.editor', 'Original dialog method required');
  assert(result && typeof result === 'object' && !Array.isArray(result));
  const exact = keys => assert.deepEqual(Object.keys(result).sort(), [...keys].sort(), 'Original dialog outcome shape');
  if (result.outcome === 'value') {
    assert.equal(result.presence, 'json');
    if (method === 'ui.confirm') { exact(['outcome', 'presence', 'value']); assert.equal(typeof result.value, 'boolean'); return result.value; }
    exact(['outcome', 'presence', 'valueJson']);
    const encoded = result.valueJson;
    assert(typeof encoded === 'string' && encoded.length >= 2 && encoded.length <= 393218 && /^[\x00-\x7f]*$/u.test(encoded), 'Bounded ASCII dialog valueJson required');
    assert(encoded[0] === '"' && encoded.at(-1) === '"', 'Exactly one JSON string literal required');
    const value = JSON.parse(encoded); // Decode nested text once; strict outer-frame Unicode remains unchanged.
    assert(typeof value === 'string' && value.length <= 65536, 'Original dialog UTF16 unit limit'); return value;
  }
  assert(['cancelled', 'timedOut', 'unavailable'].includes(result.outcome) && result.presence === 'undefined');
  const reason = result.outcome === 'unavailable' && Object.hasOwn(result, 'reason');
  exact(reason ? ['outcome', 'presence', 'reason'] : ['outcome', 'presence']);
  if (reason) assert(typeof result.reason === 'string' && result.reason.length > 0 && result.reason.length <= 128);
  return method === 'ui.confirm' ? false : undefined;
}

export function createOriginalDialogCaller({ current, trace, native, beforeOpen = async () => {} }) {
  const call = (method, args) => {
    const active = current(); assert(!active.settled && !active.dialogAdmissionClosed && (dialogMethods.has(method) || method === 'ui.editor'));
    const supplied = [...args], optionsPresent = method !== 'ui.editor' && args.length > 2 && args[2] !== undefined;
    let signal;
    if (optionsPresent && args[2] !== null && typeof args[2] === 'object') {
      signal = args[2].signal;
      if (signal !== undefined) dialogSignalAborted.call(signal); // Native getter rejects lookalike JSON objects.
      supplied[2] = Object.fromEntries(Object.entries(args[2]).filter(([key]) => key !== 'signal'));
    }
    if (signal !== undefined && dialogSignalAborted.call(signal)) return Promise.resolve(method === 'ui.confirm' ? false : undefined);
    const suppliedArgumentsJson = JSON.stringify(supplied);
    trace(active, 'source-' + method, supplied);
    const row = { originals: [], errors: [], abortRequested: false, cancelAttempted: false };
    active.dialogCalls ??= []; assert(active.dialogCalls.length < 16, 'Source dialog admission budget'); active.dialogCalls.push(row);
    const retain = (stage, payload) => {
      const original = native(stage, payload, active); row.originals.push(original);
      // Observe immediately without replacing the actual error-bearing original.
      original.then(() => {}, () => {}); return original;
    };
    const requestCancel = () => {
      row.abortRequested = true;
      if (!row.reservation || row.cancelAttempted) return;
      row.cancelAttempted = true;
      try { row.cancel = retain('ui.dialog.cancel', row.reservation); }
      catch (error) { row.errors.push(error); }
    };
    const execute = async () => {
      let listening = false, result;
      try {
        await beforeOpen(active);
        if (signal !== undefined && dialogSignalAborted.call(signal)) return method === 'ui.confirm' ? false : undefined;
        if (method === 'ui.editor') result = await retain(method, { suppliedArgumentsJson, optionsPresent: false });
        else {
          const reserve = retain('ui.dialog.reserve', { method });
          if (signal !== undefined) {
            dialogAddListener.call(signal, 'abort', requestCancel, { once: true }); listening = true;
            if (dialogSignalAborted.call(signal)) requestCancel();
          }
          const admitted = await reserve;
          row.reservation = { dialogId: admitted.dialogId, scopeId: admitted.scopeId, nativeSessionGeneration: admitted.nativeSessionGeneration };
          if (row.abortRequested) requestCancel();
          result = await retain(method, { ...row.reservation, suppliedArgumentsJson, optionsPresent });
        }
      } catch (error) { row.errors.push(error); }
      finally {
        if (listening) try { dialogRemoveListener.call(signal, 'abort', requestCancel); } catch (error) { row.errors.push(error); }
        if (row.cancel) try { await row.cancel; } catch (error) { row.errors.push(error); }
        if (row.reservation) {
          try { await retain('ui.dialog.retire', row.reservation); } catch (error) { row.errors.push(error); }
        }
      }
      if (row.errors.length) throw dialogFailure([...new Set(row.errors)]);
      return decodeOriginalDialogOutcome(method, result);
    };
    row.original = execute(); row.original.then(() => {}, () => {}); return row.original;
  };
  const join = active => {
    if (active.dialogJoinOriginal) return active.dialogJoinOriginal;
    active.dialogAdmissionClosed = true;
    active.dialogJoinOriginal = (async () => {
      const errors = [];
      for (const row of active.dialogCalls ?? []) try { await row.original; } catch (error) { errors.push(error); }
      if (errors.length) throw dialogFailure(errors);
    })();
    active.dialogJoinOriginal.then(() => {}, () => {}); return active.dialogJoinOriginal;
  };
  return { call, join };
}

export function createDialogWorkerGate({ call, operationId, nativeSessionGeneration, assertAdmission = () => {} }) {
  const entries = new Map(); let next = 0;
  const exactKeys = (value, keys) => { assert(value && typeof value === 'object' && !Array.isArray(value)); assert.deepEqual(Object.keys(value).sort(), [...keys].sort()); };
  const invoke = (method, payload) => {
    try { return call(method, { ...payload, operationId }, !['ui.dialog.cancel', 'ui.dialog.retire'].includes(method)); }
    catch (error) { const original = Promise.reject(error); original.catch(() => {}); return original; }
  };
  return (method, payload) => {
    assert(!Object.hasOwn(payload, 'operationId'), 'Source cannot replace actual dialog operation');
    if (method === 'ui.dialog.reserve') {
      assertAdmission();
      exactKeys(payload, ['method']); assert(dialogMethods.has(payload.method) && entries.size < 16, 'Dialog reservation admission');
      const dialogId = 'dialog-' + ++next, entry = { method: payload.method, state: 'reserving' }; entries.set(dialogId, entry);
      const original = invoke(method, { dialogId, method: payload.method });
      return original.then(result => {
        exactKeys(result, ['reserved', 'dialogId', 'scopeId', 'nativeSessionGeneration']);
        assert(result.reserved === true && result.dialogId === dialogId && typeof result.scopeId === 'string' && result.scopeId.length > 0 && result.scopeId.length <= 128);
        assert(result.nativeSessionGeneration === nativeSessionGeneration);
        entry.identity = { dialogId, scopeId: result.scopeId, nativeSessionGeneration }; entry.state = 'reserved'; return result;
      }).catch(error => { entry.state = 'failed'; throw error; });
    }
    const cleanup = method === 'ui.dialog.cancel' || method === 'ui.dialog.retire';
    exactKeys(payload, cleanup ? ['dialogId', 'scopeId', 'nativeSessionGeneration'] : ['dialogId', 'scopeId', 'nativeSessionGeneration', 'suppliedArgumentsJson', 'optionsPresent']);
    const entry = entries.get(payload.dialogId);
    assert(entry?.identity && payload.scopeId === entry.identity.scopeId && payload.nativeSessionGeneration === entry.identity.nativeSessionGeneration, 'Unadmitted dialog child');
    if (method === 'ui.dialog.retire' && entry.retire) return entry.retire;
    if (method === 'ui.dialog.cancel' && entry.cancel) return entry.cancel;
    assert(!['failed', 'retiring', 'retired'].includes(entry.state), 'Retired dialog child');
    if (method === 'ui.dialog.cancel') {
      if (!entry.cancel) entry.cancel = invoke(method, entry.identity).then(result => {
        exactKeys(result, ['cancelRequested', 'dialogId']); assert(result.cancelRequested === true && result.dialogId === payload.dialogId); return result;
      });
      return entry.cancel;
    }
    if (method === 'ui.dialog.retire') {
      entry.state = 'retiring'; entry.retire = invoke(method, entry.identity).then(result => {
        exactKeys(result, ['retired', 'dialogId']); assert(result.retired === true && result.dialogId === payload.dialogId); entry.state = 'retired'; return result;
      }); return entry.retire;
    }
    assertAdmission();
    assert(method === entry.method && entry.state === 'reserved' && typeof payload.suppliedArgumentsJson === 'string' && typeof payload.optionsPresent === 'boolean', 'Dialog open admission');
    entry.state = 'opening'; const original = invoke(method, payload);
    return original.then(result => { if (entry.state === 'opening') entry.state = 'settled'; return result; }, error => { if (entry.state === 'opening') entry.state = 'settled'; throw error; });
  };
}
const within = (root, path) => { const value = relative(root, path); return value !== '' && value !== '..' && !value.startsWith('..' + sep) && !value.includes(':'); };
export async function createCommandInputLoader(args) {
  const canonicalPlanPath = join(args.reference, 'compatibility/node/real-extension-reference.plan.json');
  assert.equal(hash(fs.readFileSync(canonicalPlanPath)), 'e55b7af94907598157809fc6da08ad1d60899c23b849d3623c28174d5d7fc622');
  const canonicalManifestPath = join(args.reference, 'fixtures/reference/node-real-extensions/manifest.json');
  assert.equal(hash(fs.readFileSync(canonicalManifestPath)), 'b74a7ece33b0596850266a1e1686b3c9cd1d5c978e3dcae1f07174adc3757ff9');
  const common = await import(pathToFileURL(join(args.reference, 'tools/NodeExtensionReference/common.mjs')).href);
  const setup = await import(pathToFileURL(join(args.reference, 'tools/NodeExtensionReference/prepare-jiti.mjs')).href);
  const canonicalPlan = common.readPlan(), canonicalManifest = common.readJson(canonicalManifestPath);
  assert.equal(resolve(canonicalPlan.oracle).toLowerCase(), resolve(args.oracle).toLowerCase());
  const archive = canonicalManifest.inputs.jiti.package.archive.path;
  const before = { base: common.verifyBase(canonicalPlan), jiti: await setup.verifyJiti(args.jiti, archive, canonicalPlan) };
  assert.deepEqual(before.base, canonicalManifest.inputs.base); assert.deepEqual(before.jiti, canonicalManifest.inputs.jiti);
  const ownPlanPath = join(args.repo, 'compatibility/node/command-input-bridge.plan.json'), plan = common.readJson(ownPlanPath);
  const sourceReferencePlan = join(args.commandInputReference, 'compatibility/node/command-input-reference.plan.json');
  const sourceReferenceManifest = join(args.commandInputReference, 'fixtures/reference/node-command-input/manifest.json');
  for (const row of plan.helpers) common.checkPin(args.repo, row);
  for (const row of plan.sourceReference) common.checkPin(args.commandInputReference, row);
  const loaded = new Map(), reads = new Map(), verificationReads = new Map(), readScope = new AsyncLocalStorage();
  let qualificationSource;
  let resolutions = 0, readCalls = 0, readBytes = 0, verificationCalls = 0, verificationBytes = 0;
  const admittedModule = path => qualificationSource?.ownsPath(path) ? qualificationSource.matches(path) : within(join(args.oracle, 'upstream'), path) || canonicalPlan.packages.some(name => within(join(args.oracle, 'node_modules', name), path)) || within(join(args.jiti, 'node_modules/jiti'), path) ||
    plan.helpers.some(row => resolve(join(args.repo, row.path)).toLowerCase() === path.toLowerCase()) || [...canonicalManifest.inputs.harness, canonicalPlan.archiveInspector].some(row => resolve(join(args.reference, row.path)).toLowerCase() === path.toLowerCase());
  const label = path => qualificationSource?.matches(path) ? 'private-qualification-source/' + basename(path) : within(args.oracle, path) ? relative(args.oracle, path).split(sep).join('/') : within(args.jiti, path) ? 'jiti-root/' + relative(args.jiti, path).split(sep).join('/') : within(args.repo, path) ? 'command-input-bridge/' + relative(args.repo, path).split(sep).join('/') : 'reference/' + relative(args.reference, path).split(sep).join('/');
  const originalRead = fs.readFileSync;
  const originalLstat = fs.lstatSync, originalRealpath = fs.realpathSync;
  const remember = (table, path, bytes, format) => { if (qualificationSource?.matches(path)) qualificationSource.verify(bytes); assert(table.has(label(path)) || table.size < 4096, 'Source ledger limit'); table.set(label(path), { path: label(path), bytes: bytes.length, sha256: hash(bytes), ...(format ? { nodeFormat: format } : {}) }); };
  fs.readFileSync = function (path, ...rest) {
    const result = originalRead.call(this, path, ...rest);
    if (typeof path === 'string' || path instanceof URL) {
      const name = resolve(path instanceof URL ? fileURLToPath(path) : path);
      if (admittedModule(name) && /\.(?:ts|js|mjs|cjs|mts|cts)$/iu.test(name)) {
        const bytes = Buffer.isBuffer(result) ? result : Buffer.from(result), verifying = readScope.getStore() === 'immutable-input-verification';
        if (verifying) { verificationCalls++; verificationBytes += bytes.length; assert(Number.isSafeInteger(verificationBytes)); }
        else { assert(++readCalls <= 20000 && (readBytes += bytes.length) <= 67108864, 'Source read budget'); }
        remember(verifying ? verificationReads : reads, name, bytes);
      }
    }
    return result;
  };
  // Fixed trusted JavaScript API guard, not an OS sandbox. Metadata verification is read-only.
  const denied = name => () => { throw Error('Command/Input bridge operation unavailable: ' + name); };
  for (const name of ['writeFile', 'appendFile', 'mkdir', 'mkdtemp', 'rm', 'rmdir', 'unlink', 'rename', 'copyFile', 'cp', 'truncate', 'chmod', 'chown', 'link', 'symlink', 'utimes']) {
    if (fs[name]) fs[name] = denied(name); if (fs[name + 'Sync']) fs[name + 'Sync'] = denied(name); if (fsp[name]) fsp[name] = denied(name);
  }
  for (const name of ['open', 'openSync']) { const old = fs[name]; fs[name] = function (path, flags, ...rest) { assert(flags === 'r' || flags === 0); return old.call(this, path, flags, ...rest); }; }
  const oldOpen = fsp.open; fsp.open = function (path, flags, ...rest) { assert(flags === 'r' || flags === 0); return oldOpen.call(this, path, flags, ...rest); }; fs.createWriteStream = denied('createWriteStream');
  const cp = (await import('node:child_process')).default; for (const name of ['exec', 'execFile', 'spawn', 'fork', 'execSync', 'execFileSync', 'spawnSync']) cp[name] = denied(name);
  for (const specifier of ['node:http', 'node:https', 'node:net', 'node:tls', 'node:http2', 'node:dgram', 'node:dns']) {
    const module = (await import(specifier)).default; for (const name of ['request', 'get', 'connect', 'createConnection', 'createServer', 'createSecureServer', 'createSocket', 'lookup', 'resolve', 'resolve4', 'resolve6']) if (module[name]) module[name] = denied(specifier + ':' + name);
    if (module.Socket) module.Socket.prototype.connect = denied('socket.connect'); if (module.Server) module.Server.prototype.listen = denied('server.listen');
  }
  (await import('node:worker_threads')).default.Worker = denied('Worker'); globalThis.fetch = denied('fetch'); globalThis.WebSocket = denied('WebSocket'); syncBuiltinESMExports();
  const originalVirtual = join(args.oracle, 'upstream/packages/coding-agent/src/core/extensions/virtual-modules.ts');
  const legacyReplacement = join(args.reference, 'tools/NodeExtensionReference/controlled-virtual-modules.mjs');
  let replacement = legacyReplacement;
  registerHooks({ load(url, context, next) { const result = next(url, context); if (url.startsWith('file:')) { const path = fileURLToPath(url); assert(admittedModule(path), 'Unadmitted loaded module'); remember(loaded, path, originalRead(path), result.format); } return result; } });
  await import(pathToFileURL(join(args.oracle, 'upstream/packages/coding-agent/src/experimental/source-resolver.ts')).href);
  const themeTuiParents = new Set(['packages/coding-agent/src/modes/interactive/theme/theme.ts',
    'packages/coding-agent/src/modes/interactive/theme/system-theme.ts', 'packages/coding-agent/src/core/keybindings.ts']
    .map(path => pathToFileURL(join(args.oracle, 'upstream', path)).href));
  registerHooks({ resolve(specifier, context, next) { assert(++resolutions <= 10000, 'Resolution budget');
    if (specifier === '@earendil-works/pi-tui' && themeTuiParents.has(context.parentURL)) {
      const pin = plan.helpers.find(row => row.path === 'tools/NodeCompatibility/OriginalPluginMapping/original-theme-tui-suppliers.mjs');
      assert(pin, 'Exact targeted Theme TUI supplier admission required'); common.checkPin(args.repo, pin);
      return { url: pathToFileURL(join(args.repo, pin.path)).href, shortCircuit: true };
    }
    const result = specifier === 'jiti' ? next(specifier, { ...context, parentURL: pathToFileURL(join(args.jiti, 'package.json')).href }) : next(specifier, context);
    assert(result.url.startsWith('node:') || result.url.startsWith('file:')); if (result.url.startsWith('file:')) { const path = fileURLToPath(result.url); assert(admittedModule(path), 'Unadmitted resolution'); if (resolve(path).toLowerCase() === resolve(originalVirtual).toLowerCase()) return { url: pathToFileURL(replacement).href, shortCircuit: true }; } return result; } });
  const source = relativePath => pathToFileURL(join(args.oracle, 'upstream', relativePath)).href;
  // Select the exact pinned provider before the original loader captures its
  // VIRTUAL_MODULES import. No caller-supplied replacement path is accepted.
  let loader, ExtensionRunner, runtime, eventBus, validateToolArguments, loadStarted = false, rendererSupplier;
  async function initializeOriginalLoader(bounded) {
    if (bounded) {
      const supplier = plan.originalNamespaceInjection;
      assert.equal(supplier?.profile, 'bounded-original-suppliers-1');
      assert.equal(supplier.provider, 'tools/NodeCompatibility/OriginalPluginMapping/live-original-virtual-modules.mjs');
      const pin = plan.helpers.find(row => row.path === supplier.provider);
      assert(pin, 'Original namespace provider requires an exact selected helper pin');
      common.checkPin(args.repo, pin);
      assert.equal(resolve(process.env.PISHARP_REAL_EXTENSION_ORACLE).toLowerCase(), resolve(args.oracle).toLowerCase(), 'Supplier oracle differs from qualified root');
      for (const row of supplier.sourcePins) common.checkPin(join(args.oracle, 'upstream'), row);
      replacement = join(args.repo, supplier.provider);
      rendererSupplier = await import(pathToFileURL(replacement).href);
    }
    loader = await import(source('packages/coding-agent/src/core/extensions/loader.ts'));
    const originalEvents = await import(source('packages/coding-agent/src/core/event-bus.ts'));
    ({ ExtensionRunner } = await import(source('packages/coding-agent/src/core/extensions/runner.ts')));
    ({ validateToolArguments } = await import(source('packages/ai/src/utils/validation.ts')));
    runtime = loader.createExtensionRuntime(); eventBus = originalEvents.createEventBus();
  }
  const observe = value => {
    let nodes = 0, characters = 0; const active = new Set();
    const check = (item, path, depth) => {
      assert(++nodes <= 65536 && depth <= 48 && (characters += path.length) <= 262144, 'Source observation path budget');
      if (!item || typeof item !== 'object') return;
      assert(!active.has(item), 'Cyclic source observation'); active.add(item);
      for (const key of Object.keys(item)) check(item[key], path + '/' + key.replaceAll('~', '~0').replaceAll('/', '~1'), depth + 1);
      for (const key of Object.getOwnPropertySymbols(item)) { const property = Object.getOwnPropertyDescriptor(item, key); if (Object.hasOwn(property, 'value')) check(property.value, path + '/@symbol:' + String(key.description), depth + 1); }
      active.delete(item);
    };
    check(value, '', 0); const result = common.observe(value); assert(Buffer.byteLength(JSON.stringify(result)) <= 131072, 'Source observation budget'); return result;
  };
  const invocation = new AsyncLocalStorage();
  const rendererOwner = createOriginalRendererOwner({ supplier: () => rendererSupplier,
    currentInvocation: () => invocation.getStore(), enterInvocation: (current, callback) => invocation.run(current, callback),
    workspace: () => workspace, sourceInvalid: () => invalid });
  const realDate = Date; let extensions, registrations, selectedSources, workspace, invalid = false, clockSeam, locale;
  const errorValue = error => error && (typeof error === 'object' || typeof error === 'function')
    ? { name: error.name, message: error.message, stack: error.stack, code: error.code, bridgeCode: error.bridgeCode, surface: error.surface } : error;
  const freeze = value => { if (value && typeof value === 'object') { for (const child of Object.values(value)) freeze(child); Object.freeze(value); } return value; };
  const unavailable = name => () => { throw new Error('Unsupported Commands/Input context operation: ' + name); };
  const state = () => { const current = invocation.getStore(); assert(current && !invalid, 'No active source invocation'); current.signal.throwIfAborted(); return current; };
  const trace = (current, kind, value) => { assert(current.trace.length < 128, 'Source UI trace budget'); current.trace.push({ sequence: current.trace.length + 1, kind, value: observe(value) }); };
  function runnerFor(current) {
    const inaccessible = name => new Proxy(Object.freeze({}), { get(_target, key) { throw new Error('Unsupported Commands/Input ' + name + ': ' + String(key)); } });
    const runnerExtensions = current.sessionEntry
      ? [{ ...extensions[current.sessionEntry.extensionIndex], handlers: new Map([[current.kind, [current.sessionEntry.callback]]]) }]
      : extensions;
    const runner = new ExtensionRunner(runnerExtensions, runtime, workspace, createSessionManagerFacade(current, state), inaccessible('modelRegistry'));
    runner.bindCore({
      sendMessage: unavailable('sendMessage'), sendUserMessage: unavailable('sendUserMessage'), appendEntry: unavailable('appendEntry'), setSessionName: unavailable('setSessionName'),
      getSessionName: unavailable('getSessionName'), setLabel: unavailable('setLabel'), getActiveTools: unavailable('getActiveTools'), getAllTools: unavailable('getAllTools'),
      getSettings: unavailable('getSettings'), setActiveTools: unavailable('setActiveTools'), refreshTools: unavailable('refreshTools'),
      getCommands: () => { const active = state(); assert(active.kind === 'command' && Array.isArray(active.catalog), 'Command catalog unavailable outside owned command');
        trace(active, 'getCommands', { revision: active.catalogRevision, catalog: active.catalog, authority: 'Actual native host-owned invocation snapshot' }); return active.catalog; },
      setModel: unavailable('setModel'), getThinkingLevel: unavailable('getThinkingLevel'), setThinkingLevel: unavailable('setThinkingLevel')
    }, { getModel: unavailable('getModel'), getScopedModels: unavailable('getScopedModels'), isIdle: unavailable('isIdle'), isProjectTrusted: unavailable('isProjectTrusted'),
      getSignal: () => state().signal, abort: unavailable('abort'), hasPendingMessages: unavailable('hasPendingMessages'), shutdown: unavailable('shutdown'),
      getContextUsage: unavailable('getContextUsage'), compact: unavailable('compact'), getSystemPrompt: unavailable('getSystemPrompt'),
      executeTool: unsupportedToolOperation('ctx.executeTool'), getCallableTools: unsupportedToolOperation('ctx.tools') });
    runner.bindCommandContext({ waitForIdle: unavailable('waitForIdle'), newSession: unavailable('newSession'), fork: unavailable('fork'), navigateTree: unavailable('navigateTree'), switchSession: unavailable('switchSession'), reload: unavailable('reload') });
    const native = async (method, data, captured) => {
      const cleanup = ['ui.dialog.cancel', 'ui.dialog.retire'].includes(method), active = captured ?? state(), failures = [];
      assert(!active.settled && !invalid);
      if (!cleanup) active.signal.throwIfAborted();
      try { trace(active, 'native-ui-request', { method, data }); } catch (error) { if (!cleanup) throw error; failures.push(error); }
      let result;
      try { result = await active.hostCall(method, data); } catch (error) { failures.push(error); }
      if (!cleanup) try { active.signal.throwIfAborted(); } catch (error) { failures.push(error); }
      try { trace(active, 'native-ui-reply', { method, result }); } catch (error) { failures.push(error); }
      if (failures.length) throw dialogFailure(failures); return result;
    };
    const dialog = (method, args) => {
      try {
        const active = state();
        active.dialogCaller ??= createOriginalDialogCaller({ current: state, trace, native,
          beforeOpen: async captured => { await captured.notificationPublication.wait(); captured.signal.throwIfAborted(); } });
        return active.dialogCaller.call(method, args); // Return the retained original, with no detached adopting wrapper.
      } catch (error) {
        const original = Promise.reject(error); original.then(() => {}, () => {}); return original;
      }
    };
    // Print/Json preserves upstream no-op notification return while joining actual typed unavailability; RPC denial stays distinct.
    // Genuine Runner context creation, prompt wrapping and lazy/stale guards remain intact; this bounded seam is recorded.
    runner.setUIContext({
      notify: (message, kind = 'info') => {
        const active = state(); trace(active, 'source-ui.notify', [message, kind]);
        assert(typeof message === 'string' && message.length <= 65536 && ['info', 'warning', 'error'].includes(kind));
        const unavailableNoOp = !active.uiCapabilities.features.includes('notify') && ['print', 'json'].includes(active.uiCapabilities.mode);
        if (!active.uiCapabilities.features.includes('notify') && !unavailableNoOp) throw new Error('Native notify capability unavailable.');
        assert(++active.publications <= 16 && (active.publicationBytes += Buffer.byteLength(message)) <= 262144, 'Source notification publication budget');
        active.notificationPublication.enqueue(async () => {
          const result = await native('ui.notify', { message, kind });
          if (unavailableNoOp) assert(result.published === false && result.outcome === 'unavailable' && result.reason === 'NoUi', 'Actual unavailable no-effect receipt required');
          else assert(result.published === true, 'Actual native notification receipt required');
        });
        return undefined;
      },
      select: (...args) => dialog('ui.select', args), confirm: (...args) => dialog('ui.confirm', args),
      input: (...args) => dialog('ui.input', args), editor: (...args) => dialog('ui.editor', args), custom: (factory, options) => {
        const active = state(); assert(active.uiCapabilities.mode === 'tui' && active.uiCapabilities.features.includes('customterminalcomponent'), 'Native custom component capability required');
        assert(active.ticket, 'Actual native custom owner ticket required'); return rendererOwner.custom(factory, options, active.ticket);
      },
      setWidget: unavailable('ui.setWidget'), setFooter: unavailable('ui.setFooter'), setHeader: unavailable('ui.setHeader'),
      setEditorComponent: unavailable('ui.setEditorComponent'), getEditorComponent: unavailable('ui.getEditorComponent')
    }, current.uiCapabilities.mode === 'rpc' ? 'rpc' : current.uiCapabilities.mode === 'tui' ? 'tui' : 'print');
    return runner;
  }
  async function load(cwd, inputInstances, controlledClock, requestedSources = null, privateSource = null) {
    assert(!loadStarted && !invalid && [1, 2].includes(inputInstances)); loadStarted = true; workspace = cwd;
    const defaults = ['packages/coding-agent/examples/extensions/commands.ts', 'packages/coding-agent/examples/extensions/input-transform.ts'];
    if (inputInstances === 2) defaults.push(defaults[1]);
    assert(requestedSources === null || inputInstances === 1, 'Explicit sources cannot also request duplicate legacy inputs');
    if (privateSource) {
      assert(inputInstances === 1 && controlledClock === null && requestedSources === null);
      qualificationSource = privateSource; qualificationSource.verify();
      const pin = qualificationSource.descriptor; selectedSources = [{ path: pin.sourcePath, absolute: pin.sourcePath, bytes: pin.bytes, sha256: pin.sha256 }];
    } else selectedSources = selectSources(args.oracle, requestedSources ?? defaults, originalRead);
    const bounded = requestedSources !== null || qualificationSource !== undefined;
    await initializeOriginalLoader(bounded);
    loader.clearExtensionCache();
    const paths = selectedSources.map(row => row.absolute);
    qualificationSource?.verify();
    const loadedSource = await loader.loadExtensions(paths, cwd, eventBus, runtime);
    qualificationSource?.verify();
    assert.equal(loadedSource.errors.length, 0, JSON.stringify(loadedSource.errors)); assert.equal(loadedSource.extensions.length, paths.length);
    extensions = loadedSource.extensions;
    registrations = translateRegistrations(extensions, runtime);
    if (qualificationSource) validatePrivateQualificationRegistrations(registrations, qualificationSource.descriptor.sourcePath);
    // Keep predecessor callback identities for the default corpus and its retained fixtures.
    if (requestedSources === null && !qualificationSource) {
      registrations.commands[0].callbackId = 'commands-1';
      registrations.inputs.forEach((row, index) => { row.callbackId = 'input-transform-' + (index + 1); });
    }
    if (controlledClock !== null) {
      assert.equal(controlledClock, '2026-10-01T12:00:00.000Z', 'Only the exact original corpus clock is admitted in this opt-in profile');
      const clock = realDate.parse(controlledClock);
      class FixedDate extends realDate { constructor(...values) { super(...(values.length ? values : [clock])); } static now() { return clock; } }
      globalThis.Date = FixedDate; clockSeam = { mode: 'explicit-authored-corpus-clock', clock: controlledClock, epochMs: clock,
        scope: 'No-argument Date constructor and Date.now only; explicit Date arguments and actual toLocaleString retained' };
    } else clockSeam = { mode: 'real-runtime-clock', dateConstructorReplaced: false };
    locale = { runtime: process.version, versions: { ...process.versions }, platform: process.platform, architecture: process.arch,
      environment: { TZ: process.env.TZ, LANG: process.env.LANG, LC_ALL: process.env.LC_ALL }, actualDefaultOptions: new Intl.DateTimeFormat().resolvedOptions(), clockSeam };
    if (controlledClock !== null) { assert.equal(locale.actualDefaultOptions.locale, 'en-US'); assert.equal(locale.actualDefaultOptions.timeZone, 'UTC'); }
    return { sourceCommit: canonicalPlan.source.commit, factoryAwaited: true, sourceFunctionsRemainInNode: true, sourceFactoryCount: new Set(paths).size, successfulSourceFactoryInvocations: paths.length,
      sourcePins: selectedSources.map(({ absolute, ...pin }) => pin), admissionProfile: qualificationSource ? 'private-original-ui-qualification' : requestedSources === null ? 'legacy-command-input' : 'bounded-tier-a', ...(qualificationSource ? { qualificationLease: qualificationSource.descriptor } : {}), registrations: observe(extensions.map(extension => ({ path: extension.path, resolvedPath: extension.resolvedPath, sourceInfo: extension.sourceInfo,
        commands: [...extension.commands], handlers: [...extension.handlers], tools: [...extension.tools], flags: [...extension.flags], shortcuts: [...extension.shortcuts],
        messageRenderers: [...extension.messageRenderers], entryRenderers: [...extension.entryRenderers], markdownTransformer: extension.markdownTransformer }))),
      commands: registrations.commands.map(({ handler, completion, ...row }) => ({ ...row, hasCompletion: typeof completion === 'function', handler: observe(handler), completion: observe(completion) })),
      inputHandlers: registrations.inputs.map(({ callback, ...row }) => ({ ...row, callback: observe(callback) })),
      beforeAgentStartHandlers: registrations.beforeAgentStart.map(({ callback, ...row }) => ({ ...row, callback: observe(callback) })),
      sessionHandlers: registrations.sessionHandlers.map(({ callback, extensionIndex, ...row }) => ({ ...row, callback: observe(callback) })),
      tools: registrations.tools.map(({ definition, execute, ...row }) => ({ ...row, execute: observe(execute),
        initialPreparation: 'original-validateToolArguments', originalSchemaRetained: definition.parameters === extensions.find(extension => extension.path === row.sourcePath)?.tools.get(row.name)?.definition.parameters })), locale,
      loadedModules: [...loaded.values()].sort(common.order), sourceReads: [...reads.values()].sort(common.order), resolutions,
      sourceReference: { manifestSha256: hash(originalRead(sourceReferenceManifest)), expectedSha256: plan.sourceReferenceExpectedSha256 },
      virtualModuleReplacement: { original: originalVirtual, replacement,
        ...(!bounded ? {} : { route: 'bounded-original-suppliers-1',
          suppliedNamespaces: globalThis.pisharpRealExtensionVirtualMap }), canonicalClosureExecuted: false },
      contextSeam: 'Whole genuine Runner contexts and UI prompt wrapper; explicit native capability-aware host facade. Unsupported neighboring APIs fail.' };
  }
  function complete(prefix, signal, callbackId) {
    assert(registrations && !invalid); signal.throwIfAborted();
    const command = registrations.commands.find(row => row.callbackId === callbackId); assert(command && typeof command.completion === 'function');
    const supplied = observe({ prefix });
    try { const result = command.completion(prefix); signal.throwIfAborted();
      return { status: 'fulfilled', callbackId, supplied, returned: observe(result), resultJson: JSON.stringify(result), hostCapabilitiesGranted: false };
    } catch (error) { return { status: 'rejected', callbackId, supplied, thrown: observe(errorValue(error)), hostCapabilitiesGranted: false }; }
  }
  const loadoutSnapshot = createToolLoadoutCache();
  async function prepareLoadout(callbackId, supplied, signal) {
    assert(registrations && !invalid); signal.throwIfAborted();
    const entry = registrations.tools.find(row => row.callbackId === callbackId);
    assert(entry && typeof entry.definition.prepareLoadout === 'function');
    const loadout = loadoutSnapshot(supplied), suppliedBefore = observe(supplied);
    try {
      const result = await invokeLoadoutPreparation(entry.definition.prepareLoadout.bind(entry.definition), loadout); signal.throwIfAborted();
      return { status: 'fulfilled', callbackId, preparationKind: 'loadout', resultPresence: result === undefined ? 'undefined' : 'json',
        ...(result === undefined ? {} : { resultJson: JSON.stringify(result) }), suppliedBefore, suppliedAfter: observe(supplied),
        hostCapabilitiesGranted: false, authority: 'original prepareLoadout callback over immutable actual native metadata' };
    } catch (error) { return { status: 'rejected', callbackId, preparationKind: 'loadout', suppliedBefore,
      suppliedAfter: observe(supplied), thrown: observe(errorValue(error)), hostCapabilitiesGranted: false }; }
  }
  function prepare(callbackId, argument, signal) {
    assert(registrations && !invalid); signal.throwIfAborted();
    const entry = registrations.tools.find(row => row.callbackId === callbackId); assert(entry);
    const toolCall = { type: 'toolCall', name: entry.name, arguments: argument }, suppliedBefore = observe(toolCall);
    const schemaBefore = observe(entry.definition.parameters);
    try {
      const prepared = validateToolArguments(entry.definition, toolCall); signal.throwIfAborted();
      return { status: 'fulfilled', callbackId, preparedJson: JSON.stringify(prepared), suppliedBefore, suppliedAfter: observe(toolCall),
        returned: observe(prepared), schemaBefore, schemaAfter: observe(entry.definition.parameters), hostCapabilitiesGranted: false,
        authority: 'whole original packages/ai/src/utils/validation.ts validateToolArguments on the retained live schema' };
    } catch (error) { return { status: 'rejected', callbackId, suppliedBefore, suppliedAfter: observe(toolCall), schemaBefore,
      schemaAfter: observe(entry.definition.parameters), thrown: observe(errorValue(error)), hostCapabilitiesGranted: false }; }
  }
  async function invoke(kind, callbackId, argument, catalog, catalogRevision, uiCapabilities, signal, hostCall, toolCallId, ticket, sessionSnapshot = { presence: 'unavailable' }) {
    assert(registrations && !invalid); signal.throwIfAborted();
    if (qualificationSource) { qualificationSource.assertActive(); qualificationSource.verify(); }
    const current = { kind, callbackId, catalog: catalog === undefined ? undefined : freeze(catalog), catalogRevision, uiCapabilities, signal, hostCall, ticket,
      trace: [], notificationPublication: createNotificationPublication(), publications: 0, publicationBytes: 0,
      sessionSnapshot, settled: false };
    if (['session_start', 'session_tree'].includes(kind)) {
      current.sessionEntry = registrations.sessionHandlers.find(row => row.callbackId === callbackId && row.topic === kind);
      assert(current.sessionEntry, 'Session callback topic/identity mismatch'); validateSessionEvent(argument, sessionSnapshot);
    }
    return await invocation.run(current, async () => {
      try {
        const runner = runnerFor(current), context = kind === 'command' ? runner.createCommandContext()
          : kind === 'tool' ? createOriginalToolContext(runner, toolCallId, signal) : runner.createContext();
        const progress = kind === 'tool' ? createToolProgress(signal,
          (sequence, partialResultJson) => hostCall('tool.update', { callbackId, toolCallId, sequence, partialResultJson }), observe) : undefined;
        const actualArgument = kind === 'input' ? { type: 'input', text: argument.text, images: argument.images, source: argument.source, streamingBehavior: argument.streamingBehavior } : argument;
        const suppliedBefore = observe(actualArgument), contextReceipt = { owner: kind === 'command' ? 'genuine ExtensionRunner.createCommandContext'
          : kind === 'tool' ? 'genuine ExtensionRunner.createToolContext' : 'genuine ExtensionRunner.createContext',
          cwd: context.cwd, mode: context.mode, hasUI: context.hasUI, actualNativeUiCapabilities: uiCapabilities,
          sessionManagerSeam: { presence: sessionSnapshot.presence, ...(sessionSnapshot.presence === 'json' ? { sessionId: sessionSnapshot.value.sessionId, generation: sessionSnapshot.value.generation, selectedLeafId: sessionSnapshot.value.selectedLeafId, branchEntries: sessionSnapshot.value.branchEntries.length } : {}), fullTreeAvailable: false },
          contextSeam: 'Bound capability-aware host facade: Print/Json joins typed unavailable no-effect notify and preserves upstream no-op return; explicit RPC feature denial throws',
          ...(kind === 'command' ? { actualNativeCatalogRevision: catalogRevision, actualNativeCatalog: observe(current.catalog) } : {}),
          ...(kind === 'tool' ? { actualNativeToolCallId: toolCallId, cancellationSignalPassedToFactoryContext: true,
            toolContextShape: { toolsGetter: true, executeToolMethod: true }, nestedTools: 'explicitly-unsupported-ctx.tools-and-ctx.executeTool' } : {}) };
        let result, sourceFailure, sourceCaught = false, sourceFailed = false; const runnerErrors = [];
        try {
          if (current.sessionEntry) {
            const unsubscribe = runner.onError(error => runnerErrors.push(error));
            try { result = await runner.emit(actualArgument); } finally { unsubscribe(); }
            // Runner owns catch/emit semantics; preserve its actual errors separately in the receipt.
            sourceFailed = runnerErrors.length > 0;
          }
          else if (kind === 'command') { const entry = registrations.commands.find(row => row.callbackId === callbackId); assert(entry); result = await entry.handler(actualArgument, context); }
          else if (kind === 'tool') { const entry = registrations.tools.find(row => row.callbackId === callbackId); assert(entry); result = await entry.execute(toolCallId, actualArgument, signal, progress.onUpdate, context); }
          else { const entries = kind === 'input' ? registrations.inputs : registrations.beforeAgentStart;
            const entry = entries.find(row => row.callbackId === callbackId); assert(entry); result = await entry.callback(actualArgument, context); }
        } catch (error) { sourceFailure = error; sourceCaught = true; sourceFailed = true; }
        if (current.dialogCaller) try { await current.dialogCaller.join(current); }
        catch (error) { sourceFailure = sourceCaught && sourceFailure !== error ? new AggregateError([sourceFailure, error], 'Source callback and dialog originals failed') : error; sourceCaught = true; sourceFailed = true; }
        if (ticket) try { await rendererOwner.joinOperation(ticket.operationId); }
        catch (error) { sourceFailure = sourceCaught ? new AggregateError([sourceFailure, error], 'Source callback and custom originals failed') : error; sourceCaught = true; sourceFailed = true; }
        const progressReceipt = await progress?.join();
        const publication = await current.notificationPublication.join();
        if (qualificationSource) try { qualificationSource.verify(); }
        catch (error) { sourceFailure = sourceCaught ? new AggregateError([sourceFailure, error], 'Original callback and private source verification failures') : error; sourceCaught = true; sourceFailed = true; }
        const receipt = { status: sourceFailed || publication.failed || progressReceipt?.updateFailure ? 'rejected' : 'fulfilled', kind, callbackId, suppliedArgumentCount: kind === 'tool' ? 5 : 2,
          suppliedBefore, suppliedAfter: observe(actualArgument), context: contextReceipt, returned: observe(result),
          resultPresence: result === undefined ? 'undefined' : 'json', ...(result === undefined ? {} : { resultJson: JSON.stringify(result) }),
          publicationJoined: true, publicationCount: current.publications, publicationBytes: current.publicationBytes, trace: current.trace, ...progressReceipt,
          ...(runnerErrors.length ? { sourceRunnerErrors: observe(runnerErrors) } : {}),
          ...(sourceCaught ? { sourceFailure: observe(errorValue(sourceFailure)) } : {}), ...(publication.failed ? { publicationFailure: observe(errorValue(publication.failure)) } : {}) };
        // Retain the actual source return/failure and joined publication before the worker applies cancellation disposition.
        return { ...receipt, signalAfter: { aborted: signal.aborted } };
      } finally { current.settled = true; }
    });
  }
  async function finalize() {
    qualificationSource?.beginClose();
    rendererOwner.ensureClosable();
    const cleanupFailures = [];
    try { await rendererOwner.close(); } catch (error) { cleanupFailures.push(error); }
    if (!invalid) {
      invalid = true;
      for (const cleanup of [() => runtime?.invalidate(), () => eventBus?.clear(), () => loader?.clearExtensionCache()])
        try { cleanup(); } catch (error) { cleanupFailures.push(error); }
      globalThis.Date = realDate;
    }
    assert.equal(Date, realDate);
    let after;
    try { after = await readScope.run('immutable-input-verification', async () => {
      if (qualificationSource) qualificationSource.verify(); else if (selectedSources) selectSources(args.oracle, selectedSources.map(row => row.path), originalRead);
      const base = common.verifyBase(canonicalPlan), jiti = await setup.verifyJiti(args.jiti, archive, canonicalPlan); assert.deepEqual(base, before.base); assert.deepEqual(jiti, before.jiti);
      for (const row of plan.helpers) common.checkPin(args.repo, row); for (const row of plan.sourceReference) common.checkPin(args.commandInputReference, row);
      for (const row of plan.originalNamespaceInjection.sourcePins) common.checkPin(join(args.oracle, 'upstream'), row);
      return { base, jiti };
    }); } catch (error) { cleanupFailures.push(error); }
    if (qualificationSource) try { qualificationSource.close(); } catch (error) { cleanupFailures.push(error); }
    if (cleanupFailures.length) throw new AggregateError(cleanupFailures, 'Original cleanup and immutable-input verification failures');
    const rows = [...verificationReads.values()].sort(common.order), serialized = JSON.stringify(rows);
    return { immutableInputsVerified: true, invalidated: invalid, clockRestored: Date === realDate, locale,
      loadedModules: [...loaded.values()].sort(common.order), sourceReads: [...reads.values()].sort(common.order), resolutions, sourceReadCalls: readCalls, sourceReadBytes: readBytes,
      inventoryVerificationReads: { uniqueFiles: rows.length, readCalls: verificationCalls, observedReadBytes: verificationBytes, rowsEncodedBytes: Buffer.byteLength(serialized), rowsSha256: hash(Buffer.from(serialized)),
        rowEncoding: 'UTF8 JSON.stringify exact rows sorted by ordinal path' }, qualifiedInventories: { before, after },
      sourceReferencePlanSha256: hash(originalRead(sourceReferencePlan)), sourceReferenceManifestSha256: hash(originalRead(sourceReferenceManifest)),
      sourceReferenceExpectedSha256: plan.sourceReferenceExpectedSha256 };
  }
  const originalRenderer = (callbackId, field) => {
    assert(registrations && !invalid); const entry = registrations.tools.find(row => row.callbackId === callbackId);
    assert(entry && typeof entry.definition[field] === 'function', 'Original renderer callback kind/identity mismatch'); return entry.definition[field];
  };
  const loadQualification = (cwd, lease, ownerId, ownerGeneration) => {
    assert(!loadStarted && !invalid);
    return load(cwd, 1, null, null, createPrivateQualificationSource(lease, ownerId, ownerGeneration, { read: originalRead, lstat: originalLstat, realpath: originalRealpath }));
  };
  return { load, loadQualification, complete, prepare, prepareLoadout, invoke, observe, finalize,
    renderToolCall: (callbackId, args, context, ticket) => rendererOwner.toolCall(originalRenderer(callbackId, 'renderCall'), callbackId, args, context, ticket),
    renderToolResult: (callbackId, result, options, context, ticket) => rendererOwner.toolResult(originalRenderer(callbackId, 'renderResult'), callbackId, result, options, context, ticket),
    renderComponent: (id, width, ticket) => rendererOwner.render(id, width, ticket),
    inputComponent: (id, data, ticket) => rendererOwner.input(id, data, ticket),
    disposeComponent: (id, ticket) => rendererOwner.dispose(id, ticket),
    hasComponent: (id, ticket) => rendererOwner.hasComponent(id, ticket) };
}
