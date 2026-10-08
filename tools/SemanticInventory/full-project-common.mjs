// New portable read-only profile boundary; importing it has no mutations/processes.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, readFileSync, readdirSync, mkdirSync, writeFileSync } from 'node:fs';
import { basename, dirname, isAbsolute, join, parse, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { builtinModules } from 'node:module';

export const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export const profileFiles = ['compatibility/semantic-full-project.plan.json', 'tools/SemanticInventory/full-project-common.mjs', 'tools/SemanticInventory/full-project-preload.mjs', 'tools/SemanticInventory/full-project-driver.mjs', 'tools/SemanticInventory/run-full-project.mjs', 'tools/SemanticInventory/full-project.test.mjs', 'docs/compatibility/semantic-full-project.md'];
export const hash = (bytes, algorithm = 'sha256') => createHash(algorithm).update(bytes).digest('hex');
export const jsonBytes = value => Buffer.from(JSON.stringify(value, null, 2) + '\n');
export const order = (a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0;
export function noLinks(target) { let current = resolve(target); for (;;) { let stat; try { stat = lstatSync(current); } catch (error) { if (error.code !== 'ENOENT') throw error; } assert(!stat?.isSymbolicLink(), 'Link/junction rejected'); const parent = dirname(current); if (parent === current) break; current = parent; } }
export function contained(root, path, allowRoot = false) { const suffix = relative(resolve(root), resolve(path)); return (allowRoot || suffix !== '') && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep); }
export function regular(path, maximum = 33554432) { noLinks(path); const stat = lstatSync(path); assert(stat.isFile() && stat.size <= maximum, 'Regular-file bounds failed'); return readFileSync(path); }
export function writeNew(scratch, path, bytes) { assert(contained(scratch, path)); noLinks(path); assert(!existsSync(path), 'Existing evidence preserved'); mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, bytes, { flag: 'wx' }); }
export function readPlan() { return JSON.parse(regular(join(repo, 'compatibility/semantic-full-project.plan.json')).toString()); }
export function pins() { return profileFiles.map(path => { const bytes = regular(join(repo, path)); return { path, bytes: bytes.length, sha256: hash(bytes) }; }).sort(order); }
export function admitPaths(oracle, scratch, plan = readPlan()) {
  assert.equal(resolve(oracle).toLowerCase(), resolve(plan.oracle).toLowerCase(), 'Only pinned read-only oracle admitted'); assert(isAbsolute(scratch), 'Scratch must be explicitly absolute');
  for (const other of [oracle, repo]) assert(!contained(other, scratch, true) && !contained(scratch, other, true), 'Scratch overlaps immutable oracle/repository'); noLinks(oracle); noLinks(scratch);
}
export function parseArgs(args, plan = readPlan()) { let oracle = plan.oracle, scratch, mode = 'verify'; const seen = new Set();
  for (let i = 0; i < args.length; i++) { const arg = args[i]; assert(!seen.has(arg), 'Duplicate option'); seen.add(arg); if (arg === '--oracle' || arg === '--scratch') { assert(args[i + 1] && !args[i + 1].startsWith('--'), 'Missing option value'); if (arg === '--oracle') oracle = args[++i]; else scratch = args[++i]; } else if (arg === '--check' || arg === '--capture-new') { assert.equal(mode, 'verify', 'Conflicting mode'); mode = arg; } else assert.fail('Unknown argument'); }
  assert(scratch, 'Explicit caller-owned --scratch required'); admitPaths(oracle, scratch, plan); return { oracle: resolve(oracle), scratch: resolve(scratch), mode };
}
export function environment(scratch, plan) {
  const home = join(scratch, 'home'), homeDrive = parse(home).root.replace(/[\\/]$/, ''), windowsRoot = 'C:\\WINDOWS';
  return { SystemRoot: windowsRoot, WINDIR: windowsRoot, SYSTEMDRIVE: parse(windowsRoot).root.replace(/[\\/]$/, ''), PATH: dirname(plan.runtime.path), HOME: home, USERPROFILE: home, APPDATA: home, LOCALAPPDATA: home, HOMEDRIVE: homeDrive, HOMEPATH: home.slice(homeDrive.length), LOGONSERVER: '\\\\PiSharp-Task', USERDOMAIN: 'PiSharp-Task', USERNAME: 'semantic-profile', TMP: join(scratch, 'temp'), TEMP: join(scratch, 'temp'), TZ: 'UTC', LANG: 'C', LC_ALL: 'C' };
}
export function verifyOracle(oracle, plan = readPlan()) {
  assert.equal(process.version, plan.runtime.version); assert.equal(hash(regular(process.execPath, 134217728)), plan.runtime.sha256); assert.equal(resolve(process.execPath).toLowerCase(), resolve(plan.runtime.path).toLowerCase());
  assert.equal(resolve(oracle).toLowerCase(), resolve(plan.oracle).toLowerCase()); const receiptBytes = regular(join(oracle, plan.receipt.path)); assert.equal(hash(receiptBytes), plan.receipt.sha256, 'Immutable qualified receipt changed'); const receipt = JSON.parse(receiptBytes.toString());
  assert.equal(receipt.sourceCommit, plan.sourceCommit); assert.equal(receipt.canonicalFingerprint, plan.receipt.canonicalFingerprint); const wanted = new Map(receipt.files.map(row => [row.path, row])); assert.equal(wanted.size, 2623); const actual = [];
  for (const folder of ['upstream', 'node_modules']) { const visit = directory => { noLinks(directory); for (const name of readdirSync(directory).sort()) { const path = join(directory, name); assert(contained(oracle, path)); const stat = lstatSync(path); assert(!stat.isSymbolicLink()); if (stat.isDirectory()) visit(path); else { const bytes = regular(path); actual.push({ path: relative(oracle, path).split(sep).join('/'), bytes: bytes.length, sha256: hash(bytes) }); } } }; visit(join(oracle, folder)); }
  const packageBytes = regular(join(oracle, 'package.json')); actual.push({ path: 'package.json', bytes: packageBytes.length, sha256: hash(packageBytes) }); assert.deepEqual(actual.sort(order), receipt.files, 'Read-only source/package tree changed');
  for (const pin of plan.sourcePins) assert.equal(hash(regular(join(oracle, pin.path))), pin.sha256); assert.equal(hash(regular(join(oracle, plan.native.path))), plan.native.sha256);
  assert.equal(hash(regular(join(repo, plan.publicEntrypointPlan.path))), plan.publicEntrypointPlan.sha256); return { receipt, receiptSha256: hash(receiptBytes) };
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
