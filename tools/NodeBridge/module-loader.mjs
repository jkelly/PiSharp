// Trusted prototype. Uses whole unchanged public exports; the ONE narrow virtual-map replacement is disclosed.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { AsyncLocalStorage } from 'node:async_hooks';
import { registerHooks, syncBuiltinESMExports } from 'node:module';
import { join, resolve, relative, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const within = (root, path) => { const r = relative(root, path); return r !== '' && !r.startsWith('..' + sep) && r !== '..' && !r.includes(':'); };
export async function createLoader(args) {
  const refPlanPath = join(args.reference, 'compatibility/node/real-extension-reference.plan.json');
  assert.equal(sha(fs.readFileSync(refPlanPath)), 'e55b7af94907598157809fc6da08ad1d60899c23b849d3623c28174d5d7fc622');
  const manifestPath = join(args.reference, 'fixtures/reference/node-real-extensions/manifest.json');
  assert.equal(sha(fs.readFileSync(manifestPath)), 'b74a7ece33b0596850266a1e1686b3c9cd1d5c978e3dcae1f07174adc3757ff9');
  const common = await import(pathToFileURL(join(args.reference, 'tools/NodeExtensionReference/common.mjs')).href);
  const setup = await import(pathToFileURL(join(args.reference, 'tools/NodeExtensionReference/prepare-jiti.mjs')).href);
  const plan = common.readPlan(), manifest = common.readJson(manifestPath);
  assert.equal(resolve(plan.oracle).toLowerCase(), resolve(args.oracle).toLowerCase());
  const archive = manifest.inputs.jiti.package.archive.path;
  const before = { base: common.verifyBase(plan), jiti: await setup.verifyJiti(args.jiti, archive, plan) };
  assert.deepEqual(before.base, manifest.inputs.base); assert.deepEqual(before.jiti, manifest.inputs.jiti);
  const ownPlanPath = join(args.repo, 'compatibility/node/protected-paths-bridge.plan.json');
  const own = common.readJson(ownPlanPath), ownPlanSha256 = sha(common.regular(ownPlanPath));
  for (const row of own.helpers) common.checkPin(args.repo, row);
  const loaded = new Map(), reads = new Map(), verificationReads = new Map(), readScope = new AsyncLocalStorage();
  let resolutions = 0, verificationReadCalls = 0, verificationReadBytes = 0;
  const sourceReadScope = 'Raw admitted JavaScript/TypeScript filesystem reads after bootstrap, excluding separately classified explicit immutable-input verification';
  const admitted = path => within(join(args.oracle, 'upstream'), path) || plan.packages.some(n => within(join(args.oracle, 'node_modules', n), path)) || within(join(args.jiti, 'node_modules/jiti'), path) ||
    own.helpers.some(row => resolve(join(args.repo, row.path)).toLowerCase() === path.toLowerCase()) || [...manifest.inputs.harness, plan.archiveInspector].some(row => resolve(join(args.reference, row.path)).toLowerCase() === path.toLowerCase());
  const label = path => within(args.oracle, path) ? relative(args.oracle, path).split(sep).join('/') : within(args.jiti, path) ? 'jiti-root/' + relative(args.jiti, path).split(sep).join('/') : within(args.repo, path) ? 'bridge/' + relative(args.repo, path).split(sep).join('/') : 'reference/' + relative(args.reference, path).split(sep).join('/');
  const originalRead = fs.readFileSync;
  const remember = (table, path, bytes, format) => { assert(table.size < 10000 || table.has(label(path))); table.set(label(path), { path: label(path), bytes: bytes.length, sha256: sha(bytes), ...(format ? { nodeFormat: format } : {}) }); };
  fs.readFileSync = function (path, ...rest) { const result = originalRead.call(this, path, ...rest); if (typeof path === 'string' || path instanceof URL) { const name = resolve(path instanceof URL ? fileURLToPath(path) : path); if (admitted(name) && /\.(?:ts|js|mjs|cjs|mts|cts)$/iu.test(name)) {
      const bytes = Buffer.isBuffer(result) ? result : Buffer.from(result), verification = readScope.getStore() === 'immutable-input-verification';
      if (verification) { verificationReadCalls++; verificationReadBytes += bytes.length; assert(Number.isSafeInteger(verificationReadBytes)); }
      remember(verification ? verificationReads : reads, name, bytes);
    } } return result; };
  // These JS API controls document the fixed trusted prototype, not an OS sandbox.
  const denied = name => () => { throw Error('Bridge prototype operation unavailable: ' + name); };
  for (const name of ['writeFile', 'appendFile', 'mkdir', 'mkdtemp', 'rm', 'rmdir', 'unlink', 'rename', 'copyFile', 'cp', 'truncate', 'chmod', 'chown', 'link', 'symlink', 'utimes']) {
    if (fs[name]) fs[name] = denied(name); if (fs[name + 'Sync']) fs[name + 'Sync'] = denied(name); if (fsp[name]) fsp[name] = denied(name);
  }
  for (const name of ['open', 'openSync']) { const old = fs[name]; fs[name] = function (path, flags, ...rest) { assert(flags === 'r' || flags === 0); return old.call(this, path, flags, ...rest); }; }
  const oldOpen = fsp.open; fsp.open = function (path, flags, ...rest) { assert(flags === 'r' || flags === 0); return oldOpen.call(this, path, flags, ...rest); }; fs.createWriteStream = denied('createWriteStream');
  const cp = await import('node:child_process'); for (const name of ['exec', 'execFile', 'spawn', 'fork', 'execSync', 'execFileSync', 'spawnSync']) cp.default[name] = denied(name);
  for (const specifier of ['node:http', 'node:https', 'node:net', 'node:tls', 'node:http2', 'node:dgram', 'node:dns']) { const mod = (await import(specifier)).default; for (const name of ['request', 'get', 'connect', 'createConnection', 'createServer', 'createSecureServer', 'createSocket', 'lookup', 'resolve', 'resolve4', 'resolve6']) if (mod[name]) mod[name] = denied(specifier + ':' + name); if (mod.Socket) mod.Socket.prototype.connect = denied('socket.connect'); if (mod.Server) mod.Server.prototype.listen = denied('server.listen'); }
  (await import('node:worker_threads')).default.Worker = denied('Worker'); globalThis.fetch = denied('fetch'); globalThis.WebSocket = denied('WebSocket'); syncBuiltinESMExports();
  const originalVirtual = join(args.oracle, 'upstream/packages/coding-agent/src/core/extensions/virtual-modules.ts');
  const replacement = join(args.reference, 'tools/NodeExtensionReference/controlled-virtual-modules.mjs');
  process.env.PISHARP_REAL_EXTENSION_ORACLE = args.oracle; // Fixed qualified alias provider reads this explicit root.
  registerHooks({ load(url, context, next) { const result = next(url, context); if (url.startsWith('file:')) { const p = fileURLToPath(url); assert(admitted(p), 'Unadmitted loaded module'); remember(loaded, p, originalRead(p), result.format); } return result; } });
  await import(pathToFileURL(join(args.oracle, 'upstream/packages/coding-agent/src/experimental/source-resolver.ts')).href);
  registerHooks({ resolve(specifier, context, next) { assert(++resolutions <= 10000); const result = specifier === 'jiti' ? next(specifier, { ...context, parentURL: pathToFileURL(join(args.jiti, 'package.json')).href }) : next(specifier, context);
    assert(result.url.startsWith('node:') || result.url.startsWith('file:')); if (result.url.startsWith('file:')) { const p = fileURLToPath(result.url); assert(admitted(p), 'Unadmitted resolution'); if (resolve(p).toLowerCase() === resolve(originalVirtual).toLowerCase()) return { url: pathToFileURL(replacement).href, shortCircuit: true }; } return result; } });
  const source = rel => pathToFileURL(join(args.oracle, 'upstream', rel)).href;
  const loader = await import(source('packages/coding-agent/src/core/extensions/loader.ts'));
  const { createEventBus } = await import(source('packages/coding-agent/src/core/event-bus.ts'));
  const runtime = loader.createExtensionRuntime(), eventBus = createEventBus(); let extension, handler, invalid = false, pendingFactory, releaseFactory;
  let finalizeStage = 'not-started', baseVerified = false, jitiVerified = false, helperPinsVerified = false;
  const finalizationDiagnostics = () => ({ stage: finalizeStage, baseVerified, jitiVerified, helperPinsVerified,
    loadedModuleRows: loaded.size, sourceReadRows: reads.size, verificationReadRows: verificationReads.size, verificationReadCalls, verificationReadBytes, resolutions });
  // Bound path sidecars before the qualified observer builds them. The observer and source values stay unchanged.
  const observe = value => { let nodes = 0, paths = 0; const active = new Set();
    const check = (node, path, depth) => { assert(++nodes <= 65536 && depth <= 48 && (paths += path.length) <= 262144, 'Bridge observation path budget');
      if (node && typeof node === 'object') { assert(!active.has(node), 'Cyclic bridge observation'); active.add(node);
        for (const key of Object.keys(node)) check(node[key], path + '/' + key.replaceAll('~', '~0').replaceAll('/', '~1'), depth + 1);
        for (const key of Object.getOwnPropertySymbols(node)) { const descriptor = Object.getOwnPropertyDescriptor(node, key); if (Object.hasOwn(descriptor, 'value')) check(descriptor.value, path + '/@symbol:' + String(key.description), depth + 1); } active.delete(node); }
    }; check(value, '', 0); const report = common.observe(value); assert(Buffer.byteLength(JSON.stringify(report)) <= 524288, 'Bridge observation byte budget'); return report;
  };
  const unsupported = registration => { const error = Error('Unsupported registration: ' + registration); error.bridgeCode = 'UnsupportedRegistration'; throw error; };
  const factoryBus = new Proxy(eventBus, { get(target, key) { if (typeof target[key] === 'function') return () => unsupported('eventBus.' + String(key)); return target[key]; } });
  const audit = () => {
    if (!extension || extension.handlers.size !== 1 || extension.handlers.get('tool_call')?.length !== 1 || extension.handlers.get('tool_call')[0] !== handler) unsupported('handlers');
    for (const name of ['tools', 'commands', 'flags', 'shortcuts', 'messageRenderers', 'entryRenderers']) if (extension[name].size !== 0) unsupported(name);
    if (extension.markdownTransformer !== undefined || runtime.pendingProviderRegistrations.length !== 0 || runtime.pendingNativeProviderRegistrations.length !== 0 || runtime.pendingVirtualModelRegistrations.length !== 0 || runtime.mcpServers.list().length !== 0) unsupported('runtime');
  };
  async function load(mode) {
    assert(!extension && !pendingFactory && !invalid);
    if (mode === 'async-failure') { pendingFactory = loader.loadExtensionFromFactory(async pi => { pi.on('tool_call', async () => undefined); await new Promise(r => { releaseFactory = r; }); throw 'authored async failure'; }, process.cwd(), factoryBus, runtime, '<authored:async-failure>'); pendingFactory.catch(() => {}); return { pending: true, authoredFactory: true, sourceExport: 'loadExtensionFromFactory' }; }
    if (mode === 'unsupported') { extension = await loader.loadExtensionFromFactory(pi => { pi.on('tool_call', async () => undefined); pi.registerCommand('authored-unsupported', { handler: async () => {} }); }, process.cwd(), factoryBus, runtime, '<authored:unsupported>'); handler = extension.handlers.get('tool_call')[0]; audit(); }
    const path = join(args.oracle, 'upstream/packages/coding-agent/examples/extensions/protected-paths.ts');
    loader.clearExtensionCache(); const result = await loader.loadExtensions([path], process.cwd(), factoryBus, runtime);
    assert.equal(result.errors.length, 0, JSON.stringify(result.errors)); assert.equal(result.extensions.length, 1);
    extension = result.extensions[0]; handler = extension.handlers.get('tool_call')?.[0]; audit();
    return { event: 'tool_call', callbackId: 'protected-paths-1', source: common.pin(path, 'packages/coding-agent/examples/extensions/protected-paths.ts'), sourceCommit: plan.source.commit,
      factoryAwaited: true, sourceFunctionRemainsInNode: typeof handler === 'function', loadedModules: [...loaded.values()].sort(common.order), sourceReads: [...reads.values()].sort(common.order), sourceReadScope, resolutions,
      referenceManifestSha256: sha(common.regular(manifestPath)), referenceExpectedSha256: manifest.expected.sha256 };
  }
  async function release() { assert(pendingFactory && releaseFactory); releaseFactory(); try { await pendingFactory; assert.fail('Fault factory succeeded'); } catch (error) { assert.equal(error, 'authored async failure'); return { rejected: true, authoredFactory: true, sourceExport: 'loadExtensionFromFactory', thrown: observe(error), pendingFlags: runtime.flagValues.size, pendingProviders: runtime.pendingProviderRegistrations.length }; } }
  async function finalize() { finalizeStage = 'invalidate-source-runtime'; if (!invalid) { invalid = true; runtime.invalidate(); eventBus.clear(); loader.clearExtensionCache(); }
    finalizeStage = 'join-authored-factory'; if (pendingFactory) { releaseFactory?.(); await pendingFactory.catch(() => {}); }
    const after = await readScope.run('immutable-input-verification', async () => {
      finalizeStage = 'verify-base-inventory'; const base = common.verifyBase(plan); assert.deepEqual(base, before.base); baseVerified = true;
      finalizeStage = 'verify-jiti-inventory'; const jiti = await setup.verifyJiti(args.jiti, archive, plan); assert.deepEqual(jiti, before.jiti); jitiVerified = true;
      finalizeStage = 'verify-bridge-helpers'; for (const row of own.helpers) common.checkPin(args.repo, row); helperPinsVerified = true;
      return { base, jiti };
    });
    finalizeStage = 'assemble-finalization-report';
    const rows = [...verificationReads.values()].sort(common.order), serialized = JSON.stringify(rows);
    const authority = { referenceManifestSha256: sha(common.regular(manifestPath)), bridgePlanSha256: ownPlanSha256 };
    const inventoryVerificationReads = { kind: 'exact-manifest-bound-read-summary', rawRowsIncluded: false,
      scope: 'Admitted JavaScript/TypeScript fs.readFileSync reads in the explicit immutable-input verification async context',
      uniqueFiles: rows.length, readCalls: verificationReadCalls, observedReadBytes: verificationReadBytes,
      uniqueFileBytes: rows.reduce((total, row) => total + row.bytes, 0), rowsEncodedBytes: Buffer.byteLength(serialized), rowsSha256: sha(Buffer.from(serialized)),
      rowEncoding: 'UTF8 JSON.stringify array sorted by ordinal path; row keys path,bytes,sha256', authority };
    return { immutableInputsVerified: true, invalidated: invalid, loadedModules: [...loaded.values()].sort(common.order), sourceReads: [...reads.values()].sort(common.order), sourceReadScope, resolutions,
      inventoryVerificationReads, qualifiedInventories: { before, after, authority } };
  }
  return { load, release, finalize, finalizationDiagnostics, observe, invoke: async (event, context) => { assert(!invalid); audit(); const result = await handler(event, context); audit(); return result; } };
}
