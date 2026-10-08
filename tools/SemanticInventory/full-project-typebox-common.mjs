// New portable read-only profile boundary; importing it has no mutations/processes.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, readFileSync, readdirSync, mkdirSync, writeFileSync } from 'node:fs';
import { basename, dirname, isAbsolute, join, parse, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { builtinModules } from 'node:module';

export const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export const profileFiles = ['compatibility/semantic-full-project-typebox.plan.json', 'tools/SemanticInventory/full-project-typebox-common.mjs', 'tools/SemanticInventory/full-project-typebox-preload.mjs', 'tools/SemanticInventory/full-project-typebox-driver.mjs', 'tools/SemanticInventory/run-full-project-typebox.mjs', 'tools/SemanticInventory/full-project-typebox.test.mjs', 'docs/compatibility/semantic-full-project-typebox.md'];
export const hash = (bytes, algorithm = 'sha256') => createHash(algorithm).update(bytes).digest('hex');
export const jsonBytes = value => Buffer.from(JSON.stringify(value, null, 2) + '\n');
export const order = (a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0;
export function noLinks(target) { let current = resolve(target); for (;;) { let stat; try { stat = lstatSync(current); } catch (error) { if (error.code !== 'ENOENT') throw error; } assert(!stat?.isSymbolicLink(), 'Link/junction rejected'); const parent = dirname(current); if (parent === current) break; current = parent; } }
export function contained(root, path, allowRoot = false) { const suffix = relative(resolve(root), resolve(path)); return (allowRoot || suffix !== '') && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep); }
export function regular(path, maximum = 33554432) { noLinks(path); const stat = lstatSync(path); assert(stat.isFile() && stat.size <= maximum, 'Regular-file bounds failed'); return readFileSync(path); }
export function writeNew(scratch, path, bytes) { assert(contained(scratch, path)); noLinks(path); assert(!existsSync(path), 'Existing evidence preserved'); mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, bytes, { flag: 'wx' }); }
export const diagnosticMethods = ['getConfigFileParsingDiagnostics', 'getGlobalDiagnostics', 'getProgramDiagnostics', 'getSyntacticDiagnostics', 'getBindDiagnostics', 'getSemanticDiagnostics', 'getSuggestionDiagnostics', 'getDeclarationDiagnostics'];
export function assertProfilePlan(plan) {
  assert.deepEqual(plan.diagnosticMethods, diagnosticMethods, 'All8 reviewed API diagnostic families required');
  assert.equal(plan.receipt.payloadFiles, 4122); assert.equal(plan.receipt.sourceFiles, 2093); assert.equal(plan.receipt.compilerFiles, 529); assert.equal(plan.receipt.previousNodePackageFiles, 114); assert.equal(plan.receipt.previousNodeDeclarations, 108); assert.equal(plan.receipt.typeboxPackageFiles, 1385); assert.equal(plan.receipt.typeboxDeclarations, 691); assert.equal(plan.receipt.allAddedPackageFiles, 1499);
  assert.equal(plan.executionBoundary.runtimeModulePrefix, 'node_modules/typescript/');
  assert.equal(plan.native.path, 'node_modules/@typescript/typescript-win32-x64/lib/tsc.exe');
  assert.equal(plan.semanticPublicClosure, false); assert.deepEqual(plan.phaseGatesPassed, []);
}
export function readPlan() { const plan = JSON.parse(regular(join(repo, 'compatibility/semantic-full-project-typebox.plan.json')).toString()); assertProfilePlan(plan); return plan; }
export function pins() { return profileFiles.map(path => { const bytes = regular(join(repo, path)); return { path, bytes: bytes.length, sha256: hash(bytes) }; }).sort(order); }
export function admitPaths(oracle, scratch, plan = readPlan()) {
  assert.equal(resolve(oracle).toLowerCase(), resolve(plan.oracle).toLowerCase(), 'Only pinned read-only oracle admitted'); assert(isAbsolute(scratch), 'Scratch must be explicitly absolute');
  for (const other of [oracle, ...plan.protectedOracleRoots, repo]) assert(!contained(other, scratch, true) && !contained(scratch, other, true), 'Scratch overlaps immutable oracle/repository'); noLinks(oracle); noLinks(scratch);
}
export function parseArgs(args, plan = readPlan()) { let oracle = plan.oracle, scratch, mode = 'verify'; const seen = new Set();
  for (let i = 0; i < args.length; i++) { const arg = args[i]; assert(!seen.has(arg), 'Duplicate option'); seen.add(arg); if (arg === '--oracle' || arg === '--scratch') { assert(args[i + 1] && !args[i + 1].startsWith('--'), 'Missing option value'); if (arg === '--oracle') oracle = args[++i]; else scratch = args[++i]; } else if (arg === '--check' || arg === '--capture-new') { assert.equal(mode, 'verify', 'Conflicting mode'); mode = arg; } else assert.fail('Unknown argument'); }
  assert(scratch, 'Explicit caller-owned --scratch required'); admitPaths(oracle, scratch, plan); return { oracle: resolve(oracle), scratch: resolve(scratch), mode };
}
export function environment(scratch, plan) {
  const home = join(scratch, 'home'), homeDrive = parse(home).root.replace(/[\\/]$/, ''), windowsRoot = 'C:\\WINDOWS';
  return { SystemRoot: windowsRoot, WINDIR: windowsRoot, SYSTEMDRIVE: parse(windowsRoot).root.replace(/[\\/]$/, ''), PATH: dirname(plan.runtime.path), HOME: home, USERPROFILE: home, APPDATA: home, LOCALAPPDATA: home, HOMEDRIVE: homeDrive, HOMEPATH: home.slice(homeDrive.length), LOGONSERVER: '\\\\PiSharp-Task', USERDOMAIN: 'PiSharp-Task', USERNAME: 'semantic-profile', TMP: join(scratch, 'temp'), TEMP: join(scratch, 'temp'), TZ: 'UTC', LANG: 'C', LC_ALL: 'C' };
}
export function assertTypeBoxReceipt(receipt, reconstructed, plan) {
  assert.deepEqual(receipt, reconstructed, 'Complete source/compiler/Node/TypeBox admission evidence differs');
  assert.equal(receipt.files.length, 4122); assert.equal(new Set(receipt.files.map(row => row.path.toLowerCase())).size, 4122);
  assert.equal(receipt.sourceCommit, plan.sourceCommit); assert.equal(receipt.canonicalFingerprint, plan.receipt.canonicalFingerprint);
  assert.equal(receipt.package.archive.files.length, 1385); assert.equal(receipt.package.declarationFiles, 691); assert.equal(receipt.package.runtimeJavaScriptFiles, 691);
  assert.equal(receipt.packageCodeImported, false); assert.equal(receipt.semanticPublicClosure, false); assert.deepEqual(receipt.phaseGatesPassed, []);
}
export function assertDiagnosticFamilies(diagnostics, methods) {
  assert.deepEqual(Object.keys(diagnostics), methods, 'Every diagnostic family required in source order');
  for (const method of methods) assert(Array.isArray(diagnostics[method]), 'Diagnostic family must be an array: ' + method);
}
export function compareCounts(actual, baseline, plan) {
  assertDiagnosticFamilies(actual.diagnostics, plan.diagnosticMethods); assertDiagnosticFamilies(baseline.diagnostics, plan.diagnosticMethods);
  const counts = result => Object.fromEntries(plan.diagnosticMethods.map(method => [method, result.diagnostics[method].length]));
  const before = counts(baseline), after = counts(actual);
  assert.deepEqual(before, plan.baseline.diagnosticCounts, 'Qualified baseline count changed');
  assert.equal(baseline.unresolved.length, plan.baseline.unresolvedRecords);
  assert.equal(baseline.sourceFiles.length, plan.baseline.sourceFiles); assert.equal(baseline.rootFiles.length, plan.baseline.rootFiles);
  return { baselineSnapshotSha256: plan.baseline.snapshotSha256, diagnosticCounts: after, baselineDiagnosticCounts: before, diagnosticCountDeltas: Object.fromEntries(plan.diagnosticMethods.map(method => [method, after[method] - before[method]])), unresolvedRecords: actual.unresolved.length, baselineUnresolvedRecords: baseline.unresolved.length, unresolvedCountDelta: actual.unresolved.length - baseline.unresolved.length, sourceFilesInProgram: actual.sourceFiles.length, baselineSourceFilesInProgram: baseline.sourceFiles.length, rootFiles: actual.rootFiles.length, baselineRootFiles: baseline.rootFiles.length, comparison: plan.baseline.comparison, diagnosticFiltering: false, pathNormalization: false };
}
export function readBaseline(plan = readPlan()) {
  for (const pin of plan.baseline.pins) { const bytes = regular(join(repo, pin.path), plan.limits.snapshotBytes); assert.equal(bytes.length, pin.bytes); assert.equal(hash(bytes), pin.sha256, 'Original baseline changed'); }
  const bytes = regular(join(repo, plan.baseline.pins[0].path), plan.limits.snapshotBytes);
  assert.equal(hash(bytes), plan.baseline.snapshotSha256); return JSON.parse(bytes.toString('utf8'));
}
export function mandatoryLockInventory(baseline, plan) {
  const inventory = new Map();
  for (const row of baseline.unresolved) { const candidate = row.nearestLockCandidate; if (candidate?.kind !== 'lock-package-instance') continue; let entry = inventory.get(candidate.lockPath); if (entry) assert.deepEqual(entry.lockEntry, candidate.lockEntry, 'Baseline nested lock instance conflict'); else { entry = { lockPath: candidate.lockPath, name: candidate.name, lockEntry: candidate.lockEntry, parsedSpecifiers: new Set(), baselineReferences: 0, qualification: 'genuine baseline nearest-lock diagnostic metadata; actual declaration reachability remains independently observed' }; inventory.set(candidate.lockPath, entry); } entry.parsedSpecifiers.add(candidate.specifier); entry.baselineReferences++; }
  assert.equal(inventory.size, plan.baseline.nearestLockInstances);
  return [...inventory.values()].map(entry => ({ ...entry, parsedSpecifiers: [...entry.parsedSpecifiers].sort() })).sort((a, b) => a.lockPath < b.lockPath ? -1 : a.lockPath > b.lockPath ? 1 : 0);
}
export async function verifyOracle(oracle, plan = readPlan()) {
  assert.equal(process.version, plan.runtime.version); assert.equal(hash(regular(process.execPath, 134217728)), plan.runtime.sha256); assert.equal(resolve(process.execPath).toLowerCase(), resolve(plan.runtime.path).toLowerCase());
  assert.equal(resolve(oracle).toLowerCase(), resolve(plan.oracle).toLowerCase()); noLinks(oracle);
  for (const pin of plan.declarationSetup.pins) { const bytes = regular(join(repo, pin.path)); assert.equal(bytes.length, pin.bytes); assert.equal(hash(bytes), pin.sha256, 'Frozen Stage B setup evidence changed'); }
  const admission = await import(pathToFileURL(join(repo, plan.declarationSetup.helper)).href), setupPlan = admission.readPlan();
  assert.equal(resolve(setupPlan.oracle).toLowerCase(), resolve(oracle).toLowerCase()); assert.deepEqual(setupPlan.origin, plan.origin);
  const data = await admission.context(setupPlan);
  const receiptBytes = regular(join(oracle, plan.receipt.path)); assert.equal(hash(receiptBytes), plan.receipt.sha256, 'Actual4122-file receipt changed');
  for (const pin of plan.metadataPins) { const bytes = regular(join(oracle, pin.path)); assert.equal(bytes.length, pin.bytes); assert.equal(hash(bytes), pin.sha256, 'Actual Stage B ownership/preparation/admission metadata changed'); }
  const state = admission.verifyState(resolve(oracle), setupPlan, data, admission.pins(), true); assert.equal(state.receiptSha256, plan.receipt.sha256);
  const receipt = data.parseJsonSupported(receiptBytes.toString('utf8')), prepared = data.parseJsonSupported(regular(join(oracle, admission.preparedName)).toString('utf8'));
  const expectedFiles = admission.admissionRows(data.origin.receipt, data.inspected, setupPlan), reconstructed = admission.admissionRecord(setupPlan, data, prepared, expectedFiles);
  assertTypeBoxReceipt(receipt, reconstructed, plan);
  const originalSourceRows = data.origin.originalSourceRows, packages = [...data.origin.receipt.packages, receipt.package], addedFiles = [...data.origin.addedFiles, ...receipt.package.archive.files.map(row => ({ ...row, path: receipt.package.target + '/' + row.path }))];
  assert.equal(originalSourceRows.length, 2093); assert.equal(addedFiles.length, 1499);
  for (const pin of plan.sourcePins) assert.equal(hash(regular(join(oracle, pin.path))), pin.sha256);
  assert.equal(hash(regular(join(oracle, plan.native.path))), plan.native.sha256);
  assert.equal(hash(regular(join(repo, plan.publicEntrypointPlan.path))), plan.publicEntrypointPlan.sha256);
  readBaseline(plan);
  const verificationRepoPins = [...new Map([...plan.declarationSetup.pins, ...setupPlan.dependencies, ...data.origin.verificationRepoPins].map(row => [row.path, row])).values()].sort(order);
  return { receipt, receiptSha256: hash(receiptBytes), originalSourceRows, packages, addedFiles, verificationRepoPins };
}
export function snapshot(value, limits, label = '$') { let count = 0; const active = new Set(); const visit = (item, depth, path) => { assert(++count <= limits.jsonNodes && depth <= limits.jsonDepth, 'Snapshot bounds at ' + path); if (item === null || ['string', 'boolean', 'undefined'].includes(typeof item)) return item; if (typeof item === 'number') { assert(Number.isFinite(item), 'Nonfinite value at ' + path); return item; } assert(typeof item === 'object' && !active.has(item), 'Unsupported/cyclic value at ' + path); active.add(item); const result = Array.isArray(item) ? item.map((child, index) => visit(child, depth + 1, path + '[' + index + ']')) : Object.fromEntries(Object.keys(item).map(name => [name, visit(item[name], depth + 1, path + '[' + JSON.stringify(name) + ']')])); active.delete(item); return result; }; return visit(value, 0, label); }
export function createReadOnlyFS(files, oracle) {
  const key = path => resolve(path).toLowerCase(), stored = new Map(), directories = new Map(), denied = [], requests = [];
  for (const [path, bytes] of files) { const absolute = resolve(path); stored.set(key(absolute), { absolute, bytes }); let current = dirname(absolute); while (contained(oracle, current, true)) { directories.set(key(current), directories.get(key(current)) ?? { absolute: current, files: new Set(), directories: new Set() }); const parent = dirname(current); if (parent === current) break; current = parent; } }
  for (const row of stored.values()) directories.get(key(dirname(row.absolute)))?.files.add(basename(row.absolute)); for (const row of directories.values()) { const parent = directories.get(key(dirname(row.absolute))); if (parent && parent !== row) parent.directories.add(basename(row.absolute)); }
  function record(method, path, admitted) { requests.push({ method, path: String(path), admitted }); if (!admitted) denied.push({ method, path: String(path) }); }
  return { requests, denied, fs: {
    readFile(path) { const row = contained(oracle, path, true) && stored.get(key(path)); record('readFile', path, Boolean(row)); return row ? row.bytes.toString('utf8') : null; },
    fileExists(path) { const present = contained(oracle, path, true) && stored.has(key(path)); record('fileExists', path, present); return present; },
    directoryExists(path) { const present = contained(oracle, path, true) && directories.has(key(path)); record('directoryExists', path, present); return present; },
    getAccessibleEntries(path) { const row = contained(oracle, path, true) && directories.get(key(path)); record('getAccessibleEntries', path, Boolean(row)); return row ? { files: [...row.files].sort(), directories: [...row.directories].sort() } : { files: [], directories: [] }; },
    realpath(path) { const row = contained(oracle, path, true) && (stored.get(key(path)) || directories.get(key(path))); record('realpath', path, Boolean(row)); return row ? row.absolute : join(oracle, 'upstream', '__denied_realpath__'); }
  } };
}
export function flattenEntrypoints(manifest, manifestPath) { const rows = []; const add = (field, subpath, conditions, target) => rows.push({ manifestPath, package: manifest.name, private: manifest.private === true, field, subpath, conditions, target });
  const walk = (subpath, value, conditions) => { if (Array.isArray(value)) value.forEach((child, index) => walk(subpath, child, [...conditions, index])); else if (value && typeof value === 'object') for (const [condition, child] of Object.entries(value)) walk(subpath, child, [...conditions, condition]); else add('exports', subpath, conditions, value); };
  if (Object.hasOwn(manifest, 'exports')) { const exports = manifest.exports; if (exports && !Array.isArray(exports) && typeof exports === 'object' && Object.keys(exports).some(key => key.startsWith('.'))) for (const [subpath, value] of Object.entries(exports)) walk(subpath, value, []); else walk('.', exports, []); }
  for (const field of ['main', 'types', 'typings', 'source']) if (Object.hasOwn(manifest, field)) add(field, '.', [], manifest[field]); if (Object.hasOwn(manifest, 'bin')) { if (typeof manifest.bin === 'string') add('bin', '.', [], manifest.bin); else for (const [name, target] of Object.entries(manifest.bin ?? {})) add('bin', name, [], target); } return rows;
}
export function globRegex(pattern) { return new RegExp('^' + pattern.split('*').map(part => part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('(.*)') + '$'); }
export function bindingCandidates(bindings, sourcePaths) { return Object.entries(bindings).map(([alias, patterns]) => ({ alias, patterns: patterns.map((pattern, index) => { const relativePattern = pattern.replace(/^\.\//, ''); const matching = sourcePaths.filter(path => globRegex(relativePattern).test(path)); return { index, pattern, matching }; }) })); }
export function resolveLock(lock, from, specifier) { if (specifier.startsWith('node:') || builtinModules.includes(specifier)) return { kind: 'node-builtin', specifier, ambientDeclarationsRequired: '@types/node' }; if (specifier.startsWith('.') || specifier.startsWith('/') || /^[A-Za-z]:/.test(specifier)) return { kind: 'relative-or-absolute', specifier, declarationResolutionStillRequired: true };
  const parts = specifier.split('/'), name = specifier.startsWith('@') ? parts.slice(0, 2).join('/') : parts[0]; let current = from.replaceAll('\\', '/').replace(/\/[^/]*$/, '');
  for (;;) { const candidate = (current ? current + '/' : '') + 'node_modules/' + name; if (Object.hasOwn(lock.packages, candidate)) return { kind: 'lock-package-instance', specifier, name, lockPath: candidate, lockEntry: lock.packages[candidate], declarationResolutionStillRequired: true }; if (!current) break; const parent = current.includes('/') ? current.slice(0, current.lastIndexOf('/')) : ''; if (parent === current) break; current = parent; }
  return { kind: 'not-resolved-by-lock', specifier, name, declarationResolutionStillRequired: true };
}
