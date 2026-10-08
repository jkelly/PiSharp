// Fresh, offline observations from the unchanged whole pinned SessionManager.
// This writes a new evidence directory; it neither updates a golden nor runs native tests.
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import childProcess from 'node:child_process';
import fs from 'node:fs';
import fsPromises from 'node:fs/promises';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { canonicalRawJson } from '../CompatibilityReport/raw-json.mjs';
import { registerHooks, syncBuiltinESMExports } from 'node:module';
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ownPath = fileURLToPath(import.meta.url), repo = resolve(dirname(ownPath), '../..');
const planPath = join(repo, 'compatibility/session-context-oracle-plan.json');
const setupPath = join(repo, 'tools/PiReferenceRunner/setup-session-context-oracle.mjs');
const qualificationPath = join(repo, 'fixtures/pi-v0.99.1/session-context/oracle.lock.json');
const qualificationSha256 = 'b2fbfda80b8aee1bf3cbd1742cfe13c575d0032250d8f316b1503b8c400552f2';
const packageNames = ['cross-spawn', 'isexe', 'partial-json', 'path-key', 'shebang-command', 'shebang-regex', 'typebox', 'which'];
const caseIds = ['v3-exact-framing', 'v3-full-forest', 'v3-opaque-numbers', 'v3-stored-media', 'v1-copy-compaction', 'v2-copy-hook', 'v1-explicit-copy-compaction'];
const hash = (bytes, algorithm = 'sha256', encoding = 'hex') => crypto.createHash(algorithm).update(bytes).digest(encoding);
const fileHash = path => hash(fs.readFileSync(path));
const readJson = path => JSON.parse(fs.readFileSync(path, 'utf8'));
const json = value => JSON.stringify(value, null, 2) + '\n';
const canonical = value => Array.isArray(value) ? '[' + value.map(canonical).join(',') + ']' : value && typeof value === 'object'
  ? '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + canonical(value[key])).join(',') + '}' : JSON.stringify(value);
const same = (left, right) => canonical(left) === canonical(right);
// JSONL represents undefined object members by absence. Compare that actual wire
// representation explicitly; own undefined observations remain in the raw receipt.
const sameWire = (left, right) => same(JSON.parse(JSON.stringify(left)), JSON.parse(JSON.stringify(right)));
const inside = (root, path) => { const suffix = relative(resolve(root), resolve(path)); return suffix !== '' && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep); };
const samePath = (left, right) => resolve(left).toLowerCase() === resolve(right).toLowerCase();


const comparisonPolicy = {
  raw: 'Actual native export bytes and original native artifact bytes are retained alongside whole unchanged Pi reader observations.',
  comparison: 'Only object-key order is ignored. Every numeric token/value, own-property presence, unknown record, native-looking payload, array and message is compared.',
  differences: 'Exhaustive per-view differences are retained; JS wide rounding, nonfinite JSON null, negative-zero serialization and own undefined are disclosed without parity or deviation approval.',
  legacy: 'Actual native migration uses a stable caller-supplied ID plan. The Pi reader opens actual current-v3 native exports; no Date or RNG shim is installed.',
  priorEvidence: 'The prior actual ef7d378 capture and its 227 numeric / 286 own-undefined differences remain immutable evidence. This current-candidate family is additive.',
  scope: 'Seven actual native source scenarios, eight actual operation variants, all entry/root selections, repeated imports and source-preserving reader interoperability. Original P4-05 criteria and dependent gates remain open until independent review.'
};
function noLinks(path) {
  let current = resolve(path);
  while (true) {
    let stat;
    try { stat = fs.lstatSync(current); } catch (error) { if (error.code !== 'ENOENT') throw error; }
    assert(!stat?.isSymbolicLink(), 'Symlink/junction path rejected: ' + current);
    const parent = dirname(current); if (parent === current) break; current = parent;
  }
}
function tree(root) {
  const files = [];
  const visit = directory => {
    assert(fs.lstatSync(directory).isDirectory(), 'Expected owned directory');
    assert(!fs.lstatSync(directory).isSymbolicLink(), 'Linked directory rejected');
    for (const name of fs.readdirSync(directory).sort()) {
      const path = join(directory, name), stat = fs.lstatSync(path);
      assert(!stat.isSymbolicLink(), 'Linked file rejected');
      if (stat.isDirectory()) visit(path);
      else { assert(stat.isFile(), 'Special file rejected'); files.push({ path: relative(root, path).replaceAll('\\', '/'), bytes: stat.size, sha256: fileHash(path) }); }
    }
  };
  visit(root); return files.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
}
function undefinedPaths(value, path = '', result = []) {
  if (value && typeof value === 'object') for (const key of Object.keys(value)) {
    const child = path + '/' + key.replaceAll('~', '~0').replaceAll('/', '~1');
    if (value[key] === undefined) result.push(child); else undefinedPaths(value[key], child, result);
  }
  return result;
}
function cleanEnvironment(oracle, scratch) {
  const home = join(scratch, 'home');
  return { SystemRoot: 'C:\\WINDOWS', WINDIR: 'C:\\WINDOWS', USERPROFILE: home, HOME: home, APPDATA: home, LOCALAPPDATA: home,
    TMP: scratch, TEMP: scratch, TZ: 'UTC', GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: join(oracle, 'config/gitconfig'),
    GIT_OPTIONAL_LOCKS: '0', PISHARP_REFERENCE_ORACLE: oracle, PISHARP_LIFECYCLE_SCRATCH: scratch };
}

// Capability wrapper around real fs functions, not a SessionManager replacement.
// Only sync mkdir/open/write/append/close used by the actual manager are admitted;
// every mutating path must be in this child's explicit fresh scratch, without links.
function installScratchWriteGuard(scratch) {
  noLinks(scratch); assert(fs.lstatSync(scratch).isDirectory());
  const originals = Object.fromEntries(Object.keys(fs).filter(key => typeof fs[key] === 'function').map(key => [key, fs[key]]));
  const writable = new Map(), opened = new Map(), calls = { reads: 0, writes: 0, denied: 0 }, audit = [];
  const deny = name => { calls.denied++; throw new Error('Lifecycle capture filesystem operation denied: ' + name); };
  const checkedPath = value => {
    if (value instanceof URL) value = fileURLToPath(value);
    if (typeof value !== 'string') return deny('non-text path');
    const path = resolve(value);
    if (!inside(scratch, path)) return deny('outside owned scratch');
    // Use unwrapped lstat, including dangling links; reads do not count as manager I/O.
    let current = path;
    while (inside(scratch, current) || samePath(scratch, current)) {
      let stat; try { stat = originals.lstatSync(current); } catch (error) { if (error.code !== 'ENOENT') throw error; }
      if (stat?.isSymbolicLink()) return deny('linked scratch path');
      if (samePath(scratch, current)) break; current = dirname(current);
    }
    return path;
  };
  const isWrite = flags => typeof flags === 'string' ? /[wa+]/.test(flags)
    : (flags & (fs.constants.O_WRONLY | fs.constants.O_RDWR | fs.constants.O_CREAT | fs.constants.O_TRUNC | fs.constants.O_APPEND)) !== 0;
  fs.openSync = (path, flags, ...rest) => {
    const writing = isWrite(flags), target = writing ? checkedPath(path) : path;
    const fd = originals.openSync(target, flags, ...rest);
    opened.set(fd, { path: target, writing });
    calls[writing ? 'writes' : 'reads']++;
    if (writing) { writable.set(fd, target); audit.push({ operation: 'openSync', path: target, flags }); }
    return fd;
  };
  fs.closeSync = fd => { const result = originals.closeSync(fd); writable.delete(fd); opened.delete(fd); return result; };
  for (const name of ['writeFileSync', 'appendFileSync', 'writeSync', 'writevSync']) fs[name] = (target, ...rest) => {
    let path;
    if (typeof target === 'number') { path = writable.get(target); if (!path) return deny(name + ' unowned descriptor'); }
    else { if (name === 'writeSync' || name === 'writevSync') return deny(name + ' invalid descriptor'); path = checkedPath(target); target = path; }
    const result = originals[name](target, ...rest); calls.writes++; audit.push({ operation: name, path }); return result;
  };
  fs.mkdirSync = (path, ...rest) => {
    const target = checkedPath(path), result = originals.mkdirSync(target, ...rest); calls.writes++; audit.push({ operation: 'mkdirSync', path: target }); return result;
  };
  for (const name of ['accessSync', 'existsSync', 'lstatSync', 'statSync', 'readdirSync', 'readFileSync', 'readSync', 'readvSync', 'realpathSync']) {
    if (typeof originals[name] !== 'function') continue;
    const original = originals[name], wrapped = (...args) => { calls.reads++; return original(...args); };
    // Preserve the native realpathSync.native capability used by canonical imports.
    if (typeof original.native === 'function') wrapped.native = (...args) => { calls.reads++; return original.native(...args); };
    fs[name] = wrapped;
  }
  const deniedMethods = ['open', 'close', 'writeFile', 'appendFile', 'write', 'writev', 'mkdir', 'mkdtemp', 'mkdtempSync', 'mkdtempDisposableSync',
    'createWriteStream', 'copyFile', 'copyFileSync', 'cp', 'cpSync', 'rename', 'renameSync', 'unlink', 'unlinkSync', 'rm', 'rmSync',
    'rmdir', 'rmdirSync', 'truncate', 'truncateSync', 'ftruncate', 'ftruncateSync', 'chmod', 'chmodSync', 'fchmod', 'fchmodSync',
    'chown', 'chownSync', 'fchown', 'fchownSync', 'lchown', 'lchownSync', 'lchmod', 'lchmodSync', 'utimes', 'utimesSync',
    'futimes', 'futimesSync', 'lutimes', 'lutimesSync', 'link', 'linkSync', 'symlink', 'symlinkSync', 'fsync', 'fsyncSync', 'fdatasync', 'fdatasyncSync'];
  for (const name of deniedMethods) if (typeof fs[name] === 'function') fs[name] = () => deny(name);
  for (const name of ['WriteStream', 'FileWriteStream']) if (typeof fs[name] === 'function') fs[name] = function () { return deny(name); };
  for (const name of ['open', 'writeFile', 'appendFile', 'mkdir', 'mkdtemp', 'mkdtempDisposable', 'copyFile', 'cp', 'rename', 'unlink', 'rm', 'rmdir',
    'truncate', 'chmod', 'chown', 'lchown', 'utimes', 'lutimes', 'link', 'symlink']) if (typeof fsPromises[name] === 'function') fsPromises[name] = () => deny('promises.' + name);
  syncBuiltinESMExports();
  return { snapshot: () => ({ ...calls }), audit: () => structuredClone(audit), ownedOpenDescriptors: () => opened.size };
}

const escape = value => value.replaceAll('~', '~0').replaceAll('/', '~1');
export function byteView(bytes) { return { bytes: bytes.length, sha256: hash(bytes), utf8: bytes.toString('utf8'), base64: bytes.toString('base64') }; }
export function snapshot(value) {
  const ownPropertyPaths = [], ownUndefinedPaths = [], nonFiniteNumbers = [], negativeZeroPaths = [];
  const visit = (current, path = '') => {
    if (typeof current === 'number') {
      if (!Number.isFinite(current)) nonFiniteNumbers.push({ path, value: String(current) });
      if (Object.is(current, -0)) negativeZeroPaths.push(path);
    } else if (current && typeof current === 'object') for (const key of Object.keys(current)) {
      const next = path + '/' + escape(key); ownPropertyPaths.push(next);
      if (current[key] === undefined) ownUndefinedPaths.push(next); else visit(current[key], next);
    }
  };
  visit(value);
  return { value: structuredClone(value), ownPropertyPaths, ownUndefinedPaths, nonFiniteNumbers, negativeZeroPaths };
}

class RawNumber { constructor(lexeme) { this.lexeme = lexeme; } }
const missing = Symbol('missing JSON property');
function lossless(raw) {
  // The qualified comparator first rejects duplicate names, trailing data and excessive depth.
  const canonical = canonicalRawJson(raw); let at = 0;
  const quoted = () => {
    const start = at++;
    while (at < canonical.length) { const c = canonical[at++]; if (c === '\\') at++; else if (c === '"') return JSON.parse(canonical.slice(start, at)); }
    throw new Error('Validated JSON string traversal failed');
  };
  const value = () => {
    if (canonical[at] === '"') return quoted();
    if (canonical[at] === '{') {
      at++; const result = Object.create(null);
      if (canonical[at] === '}') { at++; return result; }
      while (true) { const key = quoted(); assert.equal(canonical[at++], ':'); result[key] = value();
        if (canonical[at] === '}') { at++; return result; } assert.equal(canonical[at++], ','); }
    }
    if (canonical[at] === '[') {
      at++; const result = [];
      if (canonical[at] === ']') { at++; return result; }
      while (true) { result.push(value()); if (canonical[at] === ']') { at++; return result; } assert.equal(canonical[at++], ','); }
    }
    for (const [token, result] of [['true', true], ['false', false], ['null', null]])
      if (canonical.startsWith(token, at)) { at += token.length; return result; }
    const number = canonical.slice(at).match(/^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/); assert(number);
    at += number[0].length; return new RawNumber(number[0]);
  };
  const result = value(); assert.equal(at, canonical.length); return result;
}
function decimalIdentity(lexeme) {
  const match = lexeme.match(/^(-?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/), fraction = match[3] ?? '';
  let digits = (match[2] + fraction).replace(/^0+/, ''); if (!digits) return '0';
  let exponent = BigInt(match[4] ?? '0') - BigInt(fraction.length);
  while (digits.endsWith('0')) { digits = digits.slice(0, -1); exponent++; }
  return match[1] + digits + 'e' + exponent;
}
function describe(value) {
  if (value === missing) return { kind: 'missing' };
  if (value instanceof RawNumber) return { kind: 'number', lexeme: value.lexeme };
  return { kind: value === null ? 'null' : Array.isArray(value) ? 'array' : typeof value, value };
}
export function compareNativeView(native, observed) {
  const sourceRaw = JSON.stringify(observed.value), differences = [];
  const nonfinite = new Map(observed.nonFiniteNumbers.map(item => [item.path, item.value]));
  const negativeZero = new Set(observed.negativeZeroPaths), undefinedPaths = new Set(observed.ownUndefinedPaths);
  const add = (path, kind, left, right) => {
    assert(differences.length < 10000, 'Complete raw difference budget exceeded; no truncation is admitted');
    differences.push({ path, kind, native: describe(left), sourceSerialized: describe(right),
      ...(nonfinite.has(path) ? { actualSourceNonFinite: nonfinite.get(path) } : {}),
      ...(negativeZero.has(path) ? { actualSourceNegativeZero: true } : {}),
      ...(undefinedPaths.has(path) ? { actualSourceOwnUndefined: true } : {}) });
  };
  const visit = (left, right, path) => {
    if (left instanceof RawNumber && right instanceof RawNumber) {
      if (left.lexeme !== right.lexeme) add(path, negativeZero.has(path) ? 'source-negative-zero-serialization' :
        decimalIdentity(left.lexeme) === decimalIdentity(right.lexeme) ? 'numeric-token-spelling' : 'numeric-value-difference', left, right);
      return;
    }
    if (left instanceof RawNumber || right instanceof RawNumber) { add(path,
      nonfinite.has(path) ? 'source-nonfinite-json-serialization' : 'number-versus-other-value', left, right); return; }
    if (Array.isArray(left) && Array.isArray(right)) {
      for (let index = 0; index < Math.max(left.length, right.length); index++)
        visit(index < left.length ? left[index] : missing, index < right.length ? right[index] : missing, path + '/' + index);
      return;
    }
    const object = value => value !== missing && value !== null && typeof value === 'object' && !Array.isArray(value);
    if (object(left) && object(right)) {
      for (const key of [...new Set([...Object.keys(left), ...Object.keys(right)])].sort())
        visit(Object.hasOwn(left, key) ? left[key] : missing, Object.hasOwn(right, key) ? right[key] : missing, path + '/' + escape(key));
      return;
    }
    if (left !== right) add(path, left === missing || right === missing ? 'property-presence' : 'value-or-shape', left, right);
  };
  visit(lossless(native.rawJson), lossless(sourceRaw), '');
  const nativeOwn = new Set(native.ownPropertyPaths), sourceOwn = new Set(observed.ownPropertyPaths);
  const nativeOnlyOwnPropertyPaths = [...nativeOwn].filter(path => !sourceOwn.has(path)).sort();
  const sourceOnlyOwnPropertyPaths = [...sourceOwn].filter(path => !nativeOwn.has(path)).sort();
  return { rawValuesMatch: differences.length === 0, fieldPresenceMatch: nativeOnlyOwnPropertyPaths.length === 0 && sourceOnlyOwnPropertyPaths.length === 0,
    differences, nativeOnlyOwnPropertyPaths, sourceOnlyOwnPropertyPaths,
    nativeOwnUndefinedPaths: native.ownUndefinedPaths, sourceOwnUndefinedPaths: observed.ownUndefinedPaths,
    sourceNonFiniteNumbers: observed.nonFiniteNumbers, sourceNegativeZeroPaths: observed.negativeZeroPaths,
    nativeRawNumericTokens: native.rawNumericTokens, sourceSerializedRawJson: sourceRaw,
    comparisonProfile: 'Only object-key order ignored; arrays, complete raw numeric tokens, opaque values and field presence retained',
    parityOrApprovedDeviationClaimed: false };
}


function readBounded(path, maximum = 2 * 1024 * 1024) {
  noLinks(path); const stat = fs.lstatSync(path); assert(stat.isFile() && stat.size <= maximum, 'Bounded ordinary artifact required');
  const bytes = fs.readFileSync(path); assert(bytes.length <= maximum); return bytes;
}
function inspectNativeManifest(manifestPath) {
  assert(inside(repo, manifestPath) && isAbsolute(manifestPath), 'Native manifest must be an explicit repository artifact');
  const bundle = dirname(manifestPath), bytes = readBounded(manifestPath); canonicalRawJson(bytes.toString('utf8'));
  const manifest = JSON.parse(bytes.toString('utf8')); assert.equal(manifest.kind, 'actual-native-session-copy-exports');
  assert.equal(manifest.publicSourceSha, 'd86654abb8862e201933517d6f1fce9f88dd117f');
  assert(/^[0-9a-f]{40}$/.test(manifest.executionCandidate)); assert.equal(manifest.nativeRevision, manifest.executionCandidate);
  assert.equal(manifest.priorAuthoredInputSha256, 'f3de7bfc5f6fd4149faf8ddc73ad5910d2d038541e9180e9a2e668bfebff2003');
  assert(same(manifest.sources.map(source => source.caseId), caseIds)); assert.equal(manifest.exports.length, 8);
  for (const value of Object.values(manifest.checks)) assert.equal(value, true);
  const names = new Set(['manifest.json']), pins = [{ path: manifestPath, bytes: bytes.length, sha256: hash(bytes) }];
  const check = pin => {
    assert(/^[a-z0-9.-]{1,128}$/.test(pin.Path), 'Explicit fixed artifact name required');
    const path = join(bundle, pin.Path), data = readBounded(path);
    assert.equal(data.length, pin.Bytes); assert.equal(hash(data), pin.Sha256);
    if (!names.has(pin.Path)) { names.add(pin.Path); pins.push({ path, bytes: data.length, sha256: hash(data) }); }
  };
  check(manifest.authoredInputs);
  for (const source of manifest.sources) check(source.original);
  for (const item of manifest.exports) {
    for (const pin of [item.source, item.exported, item.native, item.repeated]) check(pin);
    const native = readJson(join(bundle, item.native.Path)); assert.equal(native.exportId, item.exportId);
    assert.equal(native.receipt.status, 'Published'); assert.equal(native.receipt.OutputSha256, item.exported.Sha256);
    assert.equal(native.receipt.OutputBytes, item.exported.Bytes);
    assert.equal(native.receipt.OmittedFields, 0); assert.equal(native.receipt.OmittedRecords, 0);
    assert.equal(native.repeatedReceipt.status, 'Published'); assert.equal(item.repeated.Sha256, item.exported.Sha256);
    assert.equal(native.reader.status, 'Complete'); assert(native.reader.sourceComplete);
    for (const value of Object.values(native.guards)) assert.equal(value, true);
    for (const snapshot of [native.initial, ...native.selections.map(selection => selection.observed)]) canonicalRawJson(snapshot.rawJson);
    const ids = JSON.parse(native.initial.rawJson).entries.map(entry => entry.id);
    assert.equal(native.selections.length, ids.length + 1); assert(native.selections.some(selection => selection.leafId === null));
    assert(ids.every(id => native.selections.some(selection => selection.leafId === id)), 'Every entry/root selection required');
  }
  assert(same(fs.readdirSync(bundle).sort(), [...names].sort()), 'Partial/unlisted/temporary artifacts rejected');
  for (const pin of manifest.producerSourcePins) {
    const path = resolve(repo, pin.Path); assert(inside(repo, path)); const data = readBounded(path);
    assert.equal(data.length, pin.Bytes); assert.equal(hash(data), pin.Sha256); pins.push({ path, bytes: data.length, sha256: hash(data) });
  }
  assert(same(manifest.executedAssemblies.map(pin => pin.Name).sort(), ['PiSharp.Contracts', 'PiSharp.Sessions', 'PiSharp.Sessions.Tests'].sort()));
  for (const pin of manifest.executedAssemblies) {
    assert(isAbsolute(pin.Path) && inside(repo, pin.Path)); const data = readBounded(pin.Path, 8 * 1024 * 1024);
    assert.equal(data.length, pin.Bytes); assert.equal(hash(data), pin.Sha256); pins.push({ path: pin.Path, bytes: data.length, sha256: hash(data) });
  }
  return { manifest, bundle, originalManifest: byteView(bytes), pins };
}
let childProgress;
async function childCapture(oracle, scratch) {
  assert(samePath(scratch, process.env.PISHARP_LIFECYCLE_SCRATCH));
  assert(globalThis.pisharpLoadedReferenceModules instanceof Map, 'Qualified full preload required');
  const output = dirname(dirname(scratch)), owner = readJson(join(output, 'owner.json'));
  assert.equal(owner.owner, 'PiSharp-native-session-copy-reader-v1');
  assert(samePath(owner.output, output) && samePath(owner.oracle, oracle));
  assert(samePath(dirname(output), join(repo, 'artifacts/session-native-copy-reader')));
  assert(samePath(dirname(scratch), join(output, 'scratch')) && basename(scratch).startsWith('capture-'));
  assert(samePath(process.cwd(), join(scratch, 'workspace')));
  assert.equal(fileHash(ownPath), owner.harnessSha256); assert.equal(tree(scratch).length, 0);
  const before = inspectNativeManifest(owner.nativeManifest);
  const original = { Date, now: Date.now, random: Math.random, randomUUID: crypto.randomUUID, randomBytes: crypto.randomBytes };
  const guard = installScratchWriteGuard(scratch);
  registerHooks({ resolve(specifier, context, next) {
    const result = next(specifier, context);
    assert(result.url.startsWith('node:') || result.url.startsWith('file:') &&
      (inside(join(oracle, 'upstream'), fileURLToPath(result.url)) ||
       packageNames.some(name => inside(join(oracle, 'node_modules', name), fileURLToPath(result.url)))), 'Unreviewed resolution fallback');
    return result;
  } });
  const source = await import(pathToFileURL(join(oracle, 'upstream/packages/coding-agent/src/core/session-manager.ts')).href);
  const { convertToLlm } = await import(pathToFileURL(join(oracle, 'upstream/packages/coding-agent/src/core/messages.ts')).href);
  const { getCurrentSystemMessage, getCurrentTools, getCurrentSystemPrompt } =
    await import(pathToFileURL(join(oracle, 'upstream/packages/ai/src/utils/transcript.ts')).href);
  for (const name of ['SessionManager', 'parseSessionEntries', 'loadEntriesFromFile', 'buildSessionContext', 'buildSessionProjection']) assert.equal(typeof source[name], 'function');
  const views = manager => {
    const context = manager.buildSessionContext(), entries = manager.getEntries();
    return { header: manager.getHeader(), entries, tree: manager.getTree(), leafId: manager.getLeafId(), branch: manager.getBranch(),
      contextEntries: manager.buildContextEntries(), projection: manager.buildSessionProjection(), context, llmMessages: convertToLlm(context.messages),
      children: [{ parentId: null, entries: manager.getTree().map(node => node.entry) },
        ...entries.map(entry => ({ parentId: entry.id, entries: manager.getChildren(entry.id) }))],
      labels: entries.map(entry => ({ entryId: entry.id, label: manager.getLabel(entry.id) })), sessionName: manager.getSessionName(),
      leafEntry: manager.getLeafEntry(), effectiveSystemMessage: getCurrentSystemMessage(context.messages),
      effectiveTools: getCurrentTools(context.messages), systemPrompt: getCurrentSystemPrompt(context.messages) };
  };
  const observations = []; childProgress = { observations };
  for (const artifact of before.manifest.exports) {
    assert(/^[a-z0-9-]{1,96}$/.test(artifact.exportId));
    const root = join(scratch, 'workspace', artifact.exportId); mkdirSync(root);
    const bytes = readFileSync(join(before.bundle, artifact.exported.Path));
    const nativeBytes = readFileSync(join(before.bundle, artifact.native.Path));
    const native = JSON.parse(nativeBytes.toString('utf8'));
    const openFile = join(root, 'open.jsonl'), loadFile = join(root, 'load.jsonl');
    writeFileSync(openFile, bytes, { flag: 'wx' }); writeFileSync(loadFile, bytes, { flag: 'wx' });
    const parsed = snapshot(source.parseSessionEntries(bytes.toString('utf8')));
    const loaded = snapshot(source.loadEntriesFromFile(loadFile)), afterLoad = readFileSync(loadFile);
    const loadedAgain = snapshot(source.loadEntriesFromFile(loadFile)), afterRepeatedLoad = readFileSync(loadFile);
    const manager = source.SessionManager.open(openFile, root), afterOpen = readFileSync(openFile);
    const initial = snapshot(views(manager)), initialComparison = compareNativeView(native.initial, initial);
    const nativeInitial = JSON.parse(native.initial.rawJson);
    assert.deepEqual(manager.getEntries().map(entry => entry.id), nativeInitial.entries.map(entry => entry.id));
    assert.equal(manager.getHeader().id, nativeInitial.header.id); assert.equal(manager.getHeader().version, 3);
    assert.equal(manager.getLeafId(), nativeInitial.leafId);
    const selections = [];
    for (const selection of native.selections) {
      if (selection.leafId === null) manager.resetLeaf(); else manager.branch(selection.leafId);
      const observed = snapshot(views(manager)), expected = JSON.parse(selection.observed.rawJson);
      assert.deepEqual(manager.getBranch().map(entry => entry.id), expected.branch.map(entry => entry.id));
      assert.equal(manager.getLeafId(), selection.leafId);
      const entries = manager.getEntries();
      selections.push({ leafId: selection.leafId, observed, comparison: compareNativeView(selection.observed, observed),
        publicProjection: snapshot(source.buildSessionProjection(entries, selection.leafId)),
        publicContext: snapshot(source.buildSessionContext(entries, selection.leafId)) });
    }
    const afterSelections = readFileSync(openFile); assert(afterSelections.equals(afterOpen), 'Source queries wrote session bytes');
    const reopened = source.SessionManager.open(openFile, root), afterReopen = readFileSync(openFile);
    const reopenedObservation = snapshot(views(reopened)), reopenComparison = compareNativeView(native.initial, reopenedObservation);
    assert.equal(reopened.getLeafId(), nativeInitial.leafId);
    assert(readFileSync(join(before.bundle, artifact.exported.Path)).equals(bytes), 'Actual native export changed');
    observations.push({ exportId: artifact.exportId, caseId: artifact.caseId, format: artifact.format,
      actualNativeExportBefore: byteView(bytes), nativeArtifactOriginal: byteView(nativeBytes),
      parsed, loaded, afterLoad: byteView(afterLoad), loadedAgain, afterRepeatedLoad: byteView(afterRepeatedLoad),
      initial, initialComparison, afterOpen: byteView(afterOpen), selections, afterSelections: byteView(afterSelections),
      reopened: reopenedObservation, reopenComparison, afterReopen: byteView(afterReopen),
      sourceFramingEffects: { loadAddedBytes: afterLoad.length - bytes.length, openAddedBytes: afterOpen.length - bytes.length,
        actualNativeExportUnchanged: true, mutationsConfinedToSeparateOwnedCopies: true } });
  }
  assert.equal(Date, original.Date); assert.equal(Date.now, original.now); assert.equal(Math.random, original.random);
  assert.equal(crypto.randomUUID, original.randomUUID); assert.equal(crypto.randomBytes, original.randomBytes);
  assert.throws(() => fs.writeFileSync(join(repo, 'outside-scratch-forbidden'), 'forbidden'));
  assert.throws(() => fs.writeSync(1, Buffer.from('forbidden')));
  assert.throws(() => childProcess.spawnSync(process.execPath, ['--version']));
  assert.throws(() => globalThis.fetch('https://example.invalid/'));
  assert(same(inspectNativeManifest(owner.nativeManifest), before), 'Actual native/source/assembly pins changed in child');
  return { schemaVersion: 1, kind: 'genuine-source-reader-observations-of-actual-native-exports', sourceSha: before.manifest.publicSourceSha,
    executionCandidate: before.manifest.executionCandidate, nativeManifestOriginal: before.originalManifest, observations,
    checks: { wholeSessionManagerLoaded: true, clocksAndRngUnmodified: true, scratchWriteGuard: true, networkAndProcessesDenied: true,
      ownedOpenDescriptors: guard.ownedOpenDescriptors(), actualNativeSourcesAndExportsUnchanged: true, nativeParityOrFullPhaseClaimed: false },
    writeGuard: { calls: guard.snapshot(), audit: guard.audit() },
    loadedModules: [...globalThis.pisharpLoadedReferenceModules.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0) };
}

export function parseArguments(args) {
  const options = new Map(); let first = false;
  const usage = 'Usage: node capture-native-session-copy-reader.mjs --capture-new --oracle APPROVED_ORACLE --native-manifest ABS_NATIVE_MANIFEST --output ABS_FRESH_OUTPUT';
  for (let index = 0; index < args.length; index++) {
    if (args[index] === '--capture-new' && !first) { first = true; continue; }
    const option = args[index], value = args[++index];
    assert(['--oracle', '--native-manifest', '--output'].includes(option) && !options.has(option) && value && isAbsolute(value), usage);
    options.set(option, resolve(value));
  }
  assert(first && options.size === 3, usage);
  return { oracle: options.get('--oracle'), nativeManifest: options.get('--native-manifest'), output: options.get('--output') };
}
async function parentCapture(args) {
  const { oracle, output, nativeManifest } = parseArguments(args);
  assert.equal(fileHash(qualificationPath), qualificationSha256, 'Frozen whole-module qualification changed');
  const qualification = readJson(qualificationPath), plan = readJson(planPath);
  const nativeBefore = inspectNativeManifest(nativeManifest);
  assert.equal(fileHash(join(repo, 'tools/CompatibilityReport/raw-json.mjs')), '58c378290d0be114e740dadda934d9a57e16a9321ae05eb8e34320e11d561ee9');
  for (const file of qualification.harnessFiles) assert.equal(fileHash(join(repo, file.path)), file.sha256, 'Locked harness changed: ' + file.path);
  assert.equal(plan.source.commit, qualification.environmentPins.sourceSha);
  assert(samePath(oracle, plan.workspace.proposedRoot), 'Only the approved existing session-context oracle is admitted');
  assert.equal(process.version, plan.runtime.version); assert.equal(fileHash(process.execPath), plan.runtime.sha256);
  assert(samePath(process.execPath, plan.runtime.absoluteExecutable));
  assert.equal(process.platform, qualification.environmentPins.platform); assert.equal(process.arch, qualification.environmentPins.architecture);
  noLinks(oracle); noLinks(output);
  const outputRoot = join(repo, 'artifacts/session-native-copy-reader');
  assert(samePath(dirname(output), outputRoot) && inside(outputRoot, output), 'Output must be one fresh direct child of artifacts/session-native-copy-reader');
  assert(!fs.existsSync(output), 'Existing output is preserved; choose a new label');
  const restoredPath = join(oracle, '.pisharp-session-context-restored.json'), restored = readJson(restoredPath);
  assert.equal(restored.status, 'eight exact dependencies installed; whole session module qualification pending');
  const lockedSetup = qualification.harnessFiles.find(file => file.path === 'tools/PiReferenceRunner/setup-session-context-oracle.mjs');
  assert.equal(restored.owner.helperSha256, lockedSetup.sha256); assert.equal(restored.owner.planSha256, fileHash(planPath));
  assert(samePath(restored.owner.oracle, oracle)); assert.equal(restored.owner.sourceCommit, plan.source.commit);
  const receiptPins = { setupReceiptSha256: fileHash(restoredPath), preparedReceiptSha256: fileHash(join(oracle, '.pisharp-session-context-prepared.json')),
    projectionManifestSha256: fileHash(join(oracle, 'package.json')), projectionLockSha256: fileHash(join(oracle, 'package-lock.json')) };
  for (const [name, value] of Object.entries(receiptPins)) assert.equal(value, qualification.environmentPins[name], 'Frozen oracle receipt/input changed: ' + name);
  const { readOnlySetupCheck } = await import(pathToFileURL(join(repo, 'tools/PiReferenceRunner/capture-session-context.mjs')).href);
  const { inspectPackageArchive } = await import(pathToFileURL(setupPath).href);
  const environment = cleanEnvironment(oracle, join(oracle, 'tmp'));
  const before = readOnlySetupCheck(oracle, environment);
  assert(same(before.sourceFingerprint, restored.sourceFingerprint)); assert(same(before.sourceFingerprint, qualification.environmentPins.sourceFingerprint));
  assert(same(fs.readdirSync(join(oracle, 'node_modules')).filter(name => name !== '.package-lock.json').sort(), packageNames));
  assert(same(plan.packages.map(row => row.name).sort(), packageNames));
  const verifyDependencies = () => {
    assert(same(fs.readdirSync(join(oracle, 'node_modules')).filter(name => name !== '.package-lock.json').sort(), packageNames), 'Exact dependency set changed');
    return plan.packages.map(row => {
    const archiveBytes = fs.readFileSync(join(oracle, 'archives', `${row.name}-${row.lockEntry.version}.tgz`));
    assert.equal('sha512-' + hash(archiveBytes, 'sha512', 'base64'), row.lockEntry.integrity);
    const archive = inspectPackageArchive(archiveBytes), files = tree(join(oracle, 'node_modules', row.name));
    const archiveFiles = [...archive.files].map(([path, bytes]) => ({ path, bytes: bytes.length, sha256: hash(bytes) })).sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
    assert(same(files, archiveFiles), 'Installed files differ from exact archive: ' + row.name);
    const receipt = restored.installed.find(item => item.name === row.name), pin = qualification.environmentPins.dependencies.find(item => item.name === row.name);
    assert(receipt && pin); assert.equal(hash(Buffer.from(canonical(files))), receipt.files.sha256); assert.equal(files.length, receipt.files.files);
    assert.equal(hash(archiveBytes), receipt.archiveSha256); assert.equal(receipt.archiveSha256, pin.archiveSha256); assert(same(receipt.files, pin.files));
    assert.equal(receipt.manifestSha256, pin.manifestSha256);
    for (const license of receipt.licenses) assert.equal(hash(archive.files.get(license.path)), license.sha256);
    return { name: row.name, version: row.lockEntry.version, archiveSha256: hash(archiveBytes), integrity: row.lockEntry.integrity, files: receipt.files,
      manifestSha256: receipt.manifestSha256, licenses: receipt.licenses.map(({ path, bytes, sha256 }) => ({ path, bytes, sha256 })) };
    });
  };
  const dependencies = verifyDependencies(); assert(same(dependencies, qualification.environmentPins.dependencies));
  const modulePins = new Map(qualification.loadedModules.map(file => [file.path, file]));
  for (const file of modulePins.values()) assert.equal(fileHash(join(oracle, file.path)), file.sha256, 'Qualified module changed before capture: ' + file.path);
  const ownPin = { path: relative(repo, ownPath).replaceAll('\\', '/'), bytes: fs.readFileSync(ownPath).length, sha256: fileHash(ownPath) };
  const environmentPins = { ...qualification.environmentPins, nativeCopyReaderHarness: ownPin, qualificationLockSha256: qualificationSha256,
    dependencyVerification: 'Every installed file equals its integrity-pinned archive before and after both children; exact eight-package top-level set.' };
  noLinks(outputRoot); fs.mkdirSync(outputRoot, { recursive: true }); noLinks(outputRoot);
  fs.mkdirSync(output); fs.writeFileSync(join(output, 'owner.json'), json({ owner: 'PiSharp-native-session-copy-reader-v1', output, oracle, sourceSha: plan.source.commit, harnessSha256: ownPin.sha256, nativeManifest }), { flag: 'wx' });
  const scratchRoot = join(output, 'scratch'); fs.mkdirSync(scratchRoot);
  const writeNew = (name, value) => fs.writeFileSync(join(output, name), value, { flag: 'wx' });
  writeNew('environment-pins.json', json(environmentPins));
  const captures = [], children = [];
  try {
    for (let repeat = 1; repeat <= 2; repeat++) {
      const scratch = fs.mkdtempSync(join(scratchRoot, 'capture-')); assert(inside(scratchRoot, scratch)); noLinks(scratch);
      fs.mkdirSync(join(scratch, 'home')); fs.mkdirSync(join(scratch, 'workspace'));

      const child = childProcess.spawnSync(process.execPath, ['--experimental-strip-types', '--import', pathToFileURL(join(repo, 'tools/PiReferenceRunner/full-preload.mjs')).href,
        ownPath, '--child', scratch], { cwd: join(scratch, 'workspace'), env: cleanEnvironment(oracle, scratch), windowsHide: true,
        encoding: 'utf8', timeout: 20000, maxBuffer: 8 * 1024 * 1024 });
      writeNew(`repeat-${repeat}.stdout.json`, child.stdout ?? ''); writeNew(`repeat-${repeat}.stderr.txt`, child.stderr ?? '');
      const receipt = { repeat, scratch, status: child.status, signal: child.signal, error: child.error ? { code: child.error.code, message: child.error.message } : null,
        stdoutBytes: Buffer.byteLength(child.stdout ?? '', 'utf8'), stderrBytes: Buffer.byteLength(child.stderr ?? '', 'utf8'), boundedTimeoutMilliseconds: 20000, maxBufferBytes: 8 * 1024 * 1024 };
      children.push(receipt); writeNew(`repeat-${repeat}.receipt.json`, json(receipt));
      assert.equal(child.error, undefined, 'Timeout/buffer/spawn error prevents qualification; retain child receipts');
      assert.equal(child.signal, null, 'Signaled source child cannot qualify');
      assert.equal(child.status, 0, 'Whole SessionManager capture failed; preserve evidence and do not substitute implementation: ' + (child.error?.message ?? child.stderr));
      const capture = JSON.parse(child.stdout); assert(same([...new Set(capture.observations.map(test => test.caseId))], caseIds)); assert.equal(capture.sourceSha, plan.source.commit);
      assert.equal(capture.checks.ownedOpenDescriptors, 0); assert(capture.checks.clocksAndRngUnmodified && capture.checks.scratchWriteGuard && capture.checks.networkAndProcessesDenied);
      for (const file of capture.loadedModules) {
        assert(inside(oracle, join(oracle, file.path)) && (file.path.startsWith('upstream/') || packageNames.some(name => file.path.startsWith(`node_modules/${name}/`))));
        assert(same(file, modulePins.get(file.path)), 'Actual loaded module lacks exact existing qualification pin: ' + file.path);
      }
      assert(same(capture.loadedModules, qualification.loadedModules), 'Whole loaded closure changed from qualified canonical modules');
      captures.push(capture); writeNew(`repeat-${repeat}.raw.json`, json(capture));
    }
    assert(same(captures[0].observations, captures[1].observations), 'Complete actual-native/source reader observations differ between fresh repeats');
    assert(same(inspectNativeManifest(nativeManifest), nativeBefore), 'Original native bundle/source/assembly bytes changed');
    const after = readOnlySetupCheck(oracle, environment); assert(same(after.sourceFingerprint, before.sourceFingerprint), 'Source fingerprint changed');
    assert(same(verifyDependencies(), dependencies), 'Installed dependency/archive bytes changed');
    for (const file of qualification.harnessFiles) assert.equal(fileHash(join(repo, file.path)), file.sha256);
    for (const [name, value] of Object.entries(receiptPins)) assert.equal(value, { setupReceiptSha256: fileHash(restoredPath), preparedReceiptSha256: fileHash(join(oracle, '.pisharp-session-context-prepared.json')),
      projectionManifestSha256: fileHash(join(oracle, 'package.json')), projectionLockSha256: fileHash(join(oracle, 'package-lock.json')) }[name]);
    assert.equal(fileHash(qualificationPath), qualificationSha256); assert.equal(fileHash(ownPath), ownPin.sha256); assert.equal(fileHash(process.execPath), plan.runtime.sha256);
    for (const file of captures[0].loadedModules) assert.equal(fileHash(join(oracle, file.path)), file.sha256);
    writeNew('capture.json', json(captures[0]));
    const comparisons = captures[0].observations.flatMap(observation => [observation.initialComparison,
      ...observation.selections.map(selection => selection.comparison), observation.reopenComparison]);
    const differenceKinds = {};
    for (const comparison of comparisons) for (const difference of comparison.differences)
      differenceKinds[difference.kind] = (differenceKinds[difference.kind] ?? 0) + 1;
    const oracleLock = { schemaVersion: 1, environmentPins, loadedModules: captures[0].loadedModules,
      sourceUnchanged: true, dependenciesUnchanged: true, nativeOriginalsUnchanged: true, children,
      nativeManifestSha256: nativeBefore.originalManifest.sha256, actualNativePins: nativeBefore.pins,
      comparisonPolicy, comparisonSummary: { completeViews: comparisons.length, differenceKinds,
        nativeOnlyOwnProperties: comparisons.reduce((sum, comparison) => sum + comparison.nativeOnlyOwnPropertyPaths.length, 0),
        sourceOnlyOwnProperties: comparisons.reduce((sum, comparison) => sum + comparison.sourceOnlyOwnPropertyPaths.length, 0),
        parityOrDeviationApprovalClaimed: false } };
    writeNew('oracle.lock.json', json(oracleLock));
    const manifest = { schemaVersion: 1, family: 'session-native-copy-reader', sourceSha: plan.source.commit,
      executionCandidate: nativeBefore.manifest.executionCandidate, caseCount: caseIds.length, exportCount: nativeBefore.manifest.exports.length,
      capture: { path: 'capture.json', bytes: fs.statSync(join(output, 'capture.json')).size, sha256: fileHash(join(output, 'capture.json')) },
      oracleLock: { path: 'oracle.lock.json', bytes: fs.statSync(join(output, 'oracle.lock.json')).size, sha256: fileHash(join(output, 'oracle.lock.json')) },
      nativeManifestOriginal: nativeBefore.originalManifest, scope: comparisonPolicy.scope };
    writeNew('manifest.json', json(manifest));
    const report = { schemaVersion: 1, status: 'captured', output, sourceSha: plan.source.commit,
      executionCandidate: nativeBefore.manifest.executionCandidate, caseCount: caseIds.length, exportCount: 8, repeatRuns: 2,
      completeComparisonsRepeated: true, sourceAndDependencyBytesUnchanged: true, actualNativeSourceExportAssemblyBytesUnchanged: true,
      loadedModulesMatchFrozenQualification: true, children, retainedScratchFiles: tree(scratchRoot),
      captureSha256: manifest.capture.sha256, manifestSha256: fileHash(join(output, 'manifest.json')),
      comparisonSummary: oracleLock.comparisonSummary, scope: comparisonPolicy.scope };
    writeNew('report.json', json(report)); console.log(json(report));
  } catch (error) {
    writeNew('failure.json', json({ schemaVersion: 1, status: 'failed', output, children, message: error.message, stack: error.stack,
      retainedScratchFiles: tree(scratchRoot), scope: comparisonPolicy.scope }));
    throw error;
  }
}

export async function main(args = process.argv.slice(2)) {
  if (args[0] === '--child') {
    assert.equal(args.length, 2); assert(isAbsolute(args[1]));
    try { console.log(JSON.stringify(await childCapture(resolve(process.env.PISHARP_REFERENCE_ORACLE), resolve(args[1])))); }
    catch (error) { console.log(JSON.stringify({ schemaVersion: 1, status: 'failed', partialObservations: childProgress,
      error: { message: error.message, stack: error.stack } })); throw error; }
  } else await parentCapture(args);
}
if (process.argv[1] && samePath(process.argv[1], ownPath)) await main();
