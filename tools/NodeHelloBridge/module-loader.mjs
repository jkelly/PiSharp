// Trusted, closed Hello successor. Whole public loader/schema/validator/functions remain in their original realm.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { AsyncLocalStorage } from 'node:async_hooks';
import { registerHooks, syncBuiltinESMExports } from 'node:module';
import { join, resolve, relative, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const within = (root, path) => { const value = relative(root, path); return value !== '' && value !== '..' && !value.startsWith('..' + sep) && !value.includes(':'); };
export async function createHelloLoader(args) {
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
  const ownPlanPath = join(args.repo, 'compatibility/node/hello-bridge.plan.json'), plan = common.readJson(ownPlanPath);
  const preparationPlan = join(args.helloReference, 'compatibility/node/hello-preparation-reference.plan.json');
  const preparationManifest = join(args.helloReference, 'fixtures/reference/node-hello-preparation/manifest.json');
  for (const row of plan.helpers) common.checkPin(args.repo, row);
  for (const row of plan.preparationReference) common.checkPin(args.helloReference, row);
  const loaded = new Map(), reads = new Map(), verificationReads = new Map(), readScope = new AsyncLocalStorage();
  let resolutions = 0, readCalls = 0, readBytes = 0, verificationCalls = 0, verificationBytes = 0;
  const admittedModule = path => within(join(args.oracle, 'upstream'), path) || canonicalPlan.packages.some(name => within(join(args.oracle, 'node_modules', name), path)) || within(join(args.jiti, 'node_modules/jiti'), path) ||
    plan.helpers.some(row => resolve(join(args.repo, row.path)).toLowerCase() === path.toLowerCase()) || [...canonicalManifest.inputs.harness, canonicalPlan.archiveInspector].some(row => resolve(join(args.reference, row.path)).toLowerCase() === path.toLowerCase());
  const label = path => within(args.oracle, path) ? relative(args.oracle, path).split(sep).join('/') : within(args.jiti, path) ? 'jiti-root/' + relative(args.jiti, path).split(sep).join('/') : within(args.repo, path) ? 'hello-bridge/' + relative(args.repo, path).split(sep).join('/') : 'reference/' + relative(args.reference, path).split(sep).join('/');
  const originalRead = fs.readFileSync;
  const remember = (table, path, bytes, format) => { assert(table.has(label(path)) || table.size < 4096, 'Source ledger limit'); table.set(label(path), { path: label(path), bytes: bytes.length, sha256: hash(bytes), ...(format ? { nodeFormat: format } : {}) }); };
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
  const denied = name => () => { throw Error('Hello bridge operation unavailable: ' + name); };
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
  const replacement = join(args.reference, 'tools/NodeExtensionReference/controlled-virtual-modules.mjs');
  registerHooks({ load(url, context, next) { const result = next(url, context); if (url.startsWith('file:')) { const path = fileURLToPath(url); assert(admittedModule(path), 'Unadmitted loaded module'); remember(loaded, path, originalRead(path), result.format); } return result; } });
  await import(pathToFileURL(join(args.oracle, 'upstream/packages/coding-agent/src/experimental/source-resolver.ts')).href);
  registerHooks({ resolve(specifier, context, next) { assert(++resolutions <= 10000, 'Resolution budget'); const result = specifier === 'jiti' ? next(specifier, { ...context, parentURL: pathToFileURL(join(args.jiti, 'package.json')).href }) : next(specifier, context);
    assert(result.url.startsWith('node:') || result.url.startsWith('file:')); if (result.url.startsWith('file:')) { const path = fileURLToPath(result.url); assert(admittedModule(path), 'Unadmitted resolution'); if (resolve(path).toLowerCase() === resolve(originalVirtual).toLowerCase()) return { url: pathToFileURL(replacement).href, shortCircuit: true }; } return result; } });
  const source = relativePath => pathToFileURL(join(args.oracle, 'upstream', relativePath)).href;
  const loader = await import(source('packages/coding-agent/src/core/extensions/loader.ts'));
  const { createEventBus } = await import(source('packages/coding-agent/src/core/event-bus.ts'));
  const { ExtensionRunner } = await import(source('packages/coding-agent/src/core/extensions/runner.ts'));
  const originalTypes = await import(source('packages/coding-agent/src/core/extensions/types.ts'));
  const ai = await import(source('packages/ai/src/index.ts'));
  const { validateToolArguments } = await import(source('packages/ai/src/utils/validation.ts'));
  const typebox = await import(pathToFileURL(join(args.oracle, 'node_modules/typebox/build/index.mjs')).href);
  const { Settings } = await import(pathToFileURL(join(args.oracle, 'node_modules/typebox/build/system/index.mjs')).href);
  const runtime = loader.createExtensionRuntime(), eventBus = createEventBus(); let definition, liveSchema, runner, invalid = false;
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
  const schemaMetadata = () => {
    const descriptors = [], active = new Set(); let pathCharacters = 0;
    const walk = (value, path, depth) => {
      if (!value || typeof value !== 'object') return;
      assert(depth <= 48 && !active.has(value), 'Schema metadata depth/cycle'); active.add(value);
      for (const key of Reflect.ownKeys(value)) {
        const property = Object.getOwnPropertyDescriptor(value, key), symbol = typeof key === 'symbol';
        const childPath = path + '/' + (symbol ? '@symbol:' + String(key.description) : key).replaceAll('~', '~0').replaceAll('/', '~1');
        assert(descriptors.length < 65536 && (pathCharacters += childPath.length) <= 262144, 'Schema metadata budget');
        descriptors.push({ path: childPath, key: symbol ? String(key.description) : key, symbol, ...(symbol ? { globalSymbolKey: Symbol.keyFor(key) ?? null } : {}),
          enumerable: property.enumerable, configurable: property.configurable, writable: property.writable ?? null,
          ...(Object.hasOwn(property, 'value') ? { valueType: typeof property.value, ...(property.value === null || typeof property.value !== 'object' ? { value: observe(property.value) } : {}) } : { getter: property.get?.name ?? null, setter: property.set?.name ?? null }) });
        if (Object.hasOwn(property, 'value')) walk(property.value, childPath, depth + 1);
      }
      active.delete(value);
    };
    walk(liveSchema, '', 0); return { publicJson: JSON.stringify(liveSchema), observation: observe(liveSchema), descriptors, settings: observe(Settings.Get()) };
  };
  const unavailable = name => () => { throw Error('Unsupported Hello context operation: ' + name); };
  async function load(cwd) {
    assert(!definition && !invalid); loader.clearExtensionCache();
    const path = join(args.oracle, 'upstream/packages/coding-agent/examples/extensions/hello.ts');
    const result = await loader.loadExtensions([path], cwd, eventBus, runtime);
    assert.equal(result.errors.length, 0, JSON.stringify(result.errors)); assert.equal(result.extensions.length, 1);
    const extension = result.extensions[0]; assert.equal(extension.tools.size, 1); assert.equal(extension.handlers.size, 0);
    for (const name of ['commands', 'flags', 'shortcuts', 'messageRenderers', 'entryRenderers']) assert.equal(extension[name].size, 0);
    assert(extension.markdownTransformer === undefined && runtime.pendingProviderRegistrations.length === 0 && runtime.pendingNativeProviderRegistrations.length === 0 && runtime.pendingVirtualModelRegistrations.length === 0 && runtime.mcpServers.list().length === 0, 'Unsupported registration');
    definition = extension.tools.get('hello').definition; liveSchema = definition.parameters;
    assert.equal(definition.execute.length, 5); assert.equal(originalTypes.defineTool(definition), definition); assert.equal(ai.Type, typebox.Type); assert.equal(ai.validateToolArguments, validateToolArguments);
    const noHostObject = name => new Proxy(Object.freeze({}), { get(_target, key) { throw Error('Unsupported Hello ' + name + ': ' + String(key)); } });
    runner = new ExtensionRunner(result.extensions, runtime, cwd, noHostObject('sessionManager'), noHostObject('modelRegistry'));
    // Genuine runner creates the fifth argument. Unsupported neighboring host operations fail explicitly.
    runner.bindCore({ sendMessage: unavailable('sendMessage'), sendUserMessage: unavailable('sendUserMessage'), appendEntry: unavailable('appendEntry'), setSessionName: unavailable('setSessionName'), getSessionName: unavailable('getSessionName'), setLabel: unavailable('setLabel'),
      getActiveTools: unavailable('getActiveTools'), getAllTools: unavailable('getAllTools'), getSettings: unavailable('getSettings'), setActiveTools: unavailable('setActiveTools'), refreshTools: unavailable('refreshTools'), getCommands: unavailable('getCommands'), setModel: unavailable('setModel'), getThinkingLevel: unavailable('getThinkingLevel'), setThinkingLevel: unavailable('setThinkingLevel') },
      { getModel: unavailable('getModel'), getScopedModels: unavailable('getScopedModels'), isIdle: () => false, isProjectTrusted: unavailable('isProjectTrusted'), getSignal: unavailable('getSignal'), abort: unavailable('abort'), hasPendingMessages: unavailable('hasPendingMessages'), shutdown: unavailable('shutdown'), getContextUsage: unavailable('getContextUsage'), compact: unavailable('compact'), getSystemPrompt: unavailable('getSystemPrompt'), executeTool: unavailable('executeTool'), getCallableTools: unavailable('getCallableTools') });
    return { sourceCommit: canonicalPlan.source.commit, source: common.pin(path, 'packages/coding-agent/examples/extensions/hello.ts'), callbackId: 'hello-callback-1', factoryAwaited: true, sourceFunctionRemainsInNode: true,
      descriptor: { name: definition.name, label: definition.label, description: definition.description, parametersJson: JSON.stringify(liveSchema), executeLength: definition.execute.length }, schema: schemaMetadata(),
      moduleIdentity: { actualPublicTypeIdentity: ai.Type === typebox.Type, actualPublicValidationIdentity: ai.validateToolArguments === validateToolArguments, actualDefineToolIdentity: originalTypes.defineTool(definition) === definition, originalSchemaRetained: definition.parameters === liveSchema, symbolForTypeboxKindPresent: Object.getOwnPropertySymbols(liveSchema).includes(Symbol.for('TypeBox.Kind')) },
      loadedModules: [...loaded.values()].sort(common.order), sourceReads: [...reads.values()].sort(common.order), resolutions,
      preparationReference: { manifestSha256: hash(originalRead(preparationManifest)), expectedSha256: plan.preparationExpectedSha256 }, virtualModuleReplacement: { original: originalVirtual, replacement, canonicalClosureExecuted: false } };
  }
  function prepare(argumentsValue) {
    assert(definition && !invalid); const toolCall = { type: 'toolCall', name: definition.name, arguments: argumentsValue }, beforeInput = observe(toolCall), schemaBefore = schemaMetadata();
    try { const prepared = validateToolArguments(definition, toolCall); return { status: 'fulfilled', preparedJson: JSON.stringify(prepared), originalBefore: beforeInput, originalAfter: observe(toolCall), returned: observe(prepared), schemaBefore, schemaAfter: schemaMetadata() }; }
    catch (error) { return { status: 'rejected', originalBefore: beforeInput, originalAfter: observe(toolCall), schemaBefore, schemaAfter: schemaMetadata(), thrown: { name: error?.name, message: error?.message, stack: error?.stack, observed: observe(error) } }; }
  }
  async function execute(toolCallId, parameters, signal, onUpdate) {
    assert(definition && !invalid); const context = runner.createToolContext(toolCallId, signal), beforeParameters = observe(parameters);
    const invocation = { suppliedArgumentCount: 5, toolCallId, params: beforeParameters, signal: { aborted: signal.aborted }, onUpdate: { name: onUpdate.name, length: onUpdate.length },
      context: { owner: 'genuine ExtensionRunner.createToolContext', cwd: context.cwd, hasUI: context.hasUI, neighboringHostOperations: 'explicitly unavailable' } };
    try { const result = await definition.execute(toolCallId, parameters, signal, onUpdate, context); return { status: 'fulfilled', resultJson: JSON.stringify(result), invocation, returned: observe(result), parametersAfter: observe(parameters), schemaAfter: schemaMetadata() }; }
    catch (error) { return { status: 'rejected', invocation, parametersAfter: observe(parameters), schemaAfter: schemaMetadata(), thrown: { name: error?.name, message: error?.message, stack: error?.stack, observed: observe(error) } }; }
  }
  async function finalize() {
    if (!invalid) { invalid = true; runtime.invalidate(); eventBus.clear(); loader.clearExtensionCache(); }
    const after = await readScope.run('immutable-input-verification', async () => { const base = common.verifyBase(canonicalPlan), jiti = await setup.verifyJiti(args.jiti, archive, canonicalPlan); assert.deepEqual(base, before.base); assert.deepEqual(jiti, before.jiti);
      for (const row of plan.helpers) common.checkPin(args.repo, row); for (const row of plan.preparationReference) common.checkPin(args.helloReference, row); return { base, jiti }; });
    const rows = [...verificationReads.values()].sort(common.order), serialized = JSON.stringify(rows);
    return { immutableInputsVerified: true, invalidated: invalid, loadedModules: [...loaded.values()].sort(common.order), sourceReads: [...reads.values()].sort(common.order), resolutions, sourceReadCalls: readCalls, sourceReadBytes: readBytes,
      inventoryVerificationReads: { uniqueFiles: rows.length, readCalls: verificationCalls, observedReadBytes: verificationBytes, rowsEncodedBytes: Buffer.byteLength(serialized), rowsSha256: hash(Buffer.from(serialized)), rowEncoding: 'UTF8 JSON.stringify exact rows sorted by ordinal path' },
      qualifiedInventories: { before, after }, helloPreparationPlanSha256: hash(originalRead(preparationPlan)), helloPreparationManifestSha256: hash(originalRead(preparationManifest)) };
  }
  return { load, prepare, execute, observe, finalize };
}
