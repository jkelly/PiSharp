// Fresh, offline observations from the unchanged whole pinned SessionManager.
// This writes a new evidence directory; it neither updates a golden nor runs native tests.
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import childProcess from 'node:child_process';
import fs from 'node:fs';
import fsPromises from 'node:fs/promises';
import { registerHooks, syncBuiltinESMExports } from 'node:module';
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ownPath = fileURLToPath(import.meta.url), repo = resolve(dirname(ownPath), '../..');
const planPath = join(repo, 'compatibility/session-context-oracle-plan.json');
const setupPath = join(repo, 'tools/PiReferenceRunner/setup-session-context-oracle.mjs');
const qualificationPath = join(repo, 'fixtures/pi-v0.99.1/session-context/oracle.lock.json');
const qualificationSha256 = 'b2fbfda80b8aee1bf3cbd1742cfe13c575d0032250d8f316b1503b8c400552f2';
const packageNames = ['cross-spawn', 'isexe', 'partial-json', 'path-key', 'shebang-command', 'shebang-regex', 'typebox', 'which'];

const family = 'fixtures/pi-v0.99.1/session-branches-replayed';
const inputPath = join(repo, family, 'core.input.json'), capturePath = join(repo, family, 'capture.json');
const manifestPath = join(repo, family, 'manifest.json'), fixtureLockPath = join(repo, family, 'oracle.lock.json');
const lifecycleHarnessSha256 = '9bf405272c72fdf9cfea159ddc0bb3fc160cee5ceee1e15bba644af317a61865';
const input = JSON.parse(fs.readFileSync(inputPath, 'utf8')), caseIds = input.cases.map(test => test.caseId);
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
    GIT_OPTIONAL_LOCKS: '0', PISHARP_REFERENCE_ORACLE: oracle, PISHARP_BRANCH_SCRATCH: scratch };
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

function physicalFile(path) {
  if (path === undefined) return { path, exists: false, bytes: 0, records: [] };
  if (!fs.existsSync(path)) return { path, exists: false, bytes: 0, records: [] };
  assert(fs.lstatSync(path).isFile() && !fs.lstatSync(path).isSymbolicLink());
  const bytes = fs.readFileSync(path), utf8 = bytes.toString('utf8');
  assert(Buffer.from(utf8, 'utf8').equals(bytes), 'Physical file must be lossless UTF-8');
  assert(utf8.endsWith('\n'), 'Actual valid published log must have its final newline');
  return { path, exists: true, bytes: bytes.length, sha256: hash(bytes), utf8, base64: bytes.toString('base64'), records: utf8.slice(0, -1).split('\n').map(line => JSON.parse(line)) };
}
function text(message) { return typeof message.content === 'string' ? message.content : message.content?.filter(part => part.type === 'text').map(part => part.text).join('') ?? ''; }

const comparisonPolicy = {
  version: 1,
  raw: 'Complete query results, raw entries, trees, children, labels, names, projections, context, public LLM conversion, actual operation returns and exact physical UTF8/base64 bytes are retained, with own undefined paths. No raw ID, date, cwd, path or unknown field is replaced.',
  comparison: 'The independent repeat contract retains complete query structures. Only identifiers generated by declared appendLabelChange/appendSessionInfo operations use explicit operation tokens; their record timestamp and resolved tree labelTimestamp use operation-time tokens. Authored identifiers/dates and every remaining field stay exact.',
  generatedIdentifierFields: ['id', 'parentId', 'targetId', 'leafId', 'entryId', 'selectionId'],
  generatedTimestampFields: ['timestamp on a record with a generated id', 'labelTimestamp on a tree node labeled by a generated label record'],
  paths: 'Physical file paths/hashes remain raw receipts; repeat contracts compare actual within-run byte preservation and physical record counts.',
  accounting: 'Raw assistant usage and usage entries are retained. No additional accounting module or source-derived accounting implementation is imported.',
  scope: 'Eight valid admitted branch/history forests and actual global metadata append schedules; no malformed cycle execution, provider execution, P4-03 closure or full phase/package closure claim.',
};
function validateInput() {
  assert.equal(input.schemaVersion, 1); assert.equal(input.fixtureId, 'session-branches-replayed');
  assert.equal(input.sourceSha, 'd86654abb8862e201933517d6f1fce9f88dd117f');
  assert.equal(caseIds.length, 8); assert.equal(new Set(caseIds).size, caseIds.length);
  for (const test of input.cases) {
    assert(/^[a-z0-9-]+$/.test(test.caseId)); assert.equal(test.header.type, 'session'); assert.equal(test.header.version, 3);
    const byId = new Map(test.entries.map(entry => [entry.id, entry])); assert.equal(byId.size, test.entries.length);
    assert(test.entries.length <= 128 && test.operations.length <= 8, 'Authored input bounds exceeded');
    for (const entry of test.entries) {
      assert(typeof entry.id === 'string' && entry.id.length > 0 && entry.id !== test.header.id);
      assert(entry.parentId === null || byId.has(entry.parentId), 'Only admitted present-parent forests are executed');
      assert(Number.isFinite(Date.parse(entry.timestamp)));
      const visited = new Set(); let current = entry;
      while (current) { assert(!visited.has(current.id), 'Cycles are excluded before upstream traversal'); visited.add(current.id); current = byId.get(current.parentId); }
    }
    for (const operation of test.operations) {
      assert(['branch', 'label', 'name'].includes(operation.kind));
      if (operation.kind === 'branch') assert(byId.has(operation.entryId));
      if (operation.kind === 'label') assert(byId.has(operation.targetId));
    }
  }
}
function repeatContract(test) {
  const generated = new Map(test.generatedEntries.map(item => [item.entry.id, item.operationId]));
  const idFields = new Set(comparisonPolicy.generatedIdentifierFields);
  const project = (value, key = '', parent, labels) => {
    if (typeof value === 'string' && idFields.has(key) && generated.has(value)) return '$generated(' + generated.get(value) + ')';
    if (key === 'timestamp' && generated.has(parent?.id)) return '$generated-time(' + generated.get(parent.id) + ')';
    if (key === 'labelTimestamp' && parent?.entry && generated.has(labels.get(parent.entry.id)?.id)) return '$generated-time(' + generated.get(labels.get(parent.entry.id).id) + ')';
    if (Array.isArray(value)) return value.map(item => project(item, '', value, labels));
    if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).map(name => [name, project(value[name], name, value, labels)]));
    return value;
  };
  return { phases: test.phases.map(phase => {
    const labels = new Map();
    for (const entry of phase.matrix[0].entries) if (entry.type === 'label') { if (entry.label) labels.set(entry.targetId, entry); else labels.delete(entry.targetId); }
    return { phaseId: phase.phaseId, physicalRecordCount: phase.physicalFileAfter.records.length,
      matrix: project(phase.matrix, '', undefined, labels), sourceBytesPreservedByQueries: phase.physicalFileBefore.base64 === phase.physicalFileAfter.base64 };
  }), relations: test.relations };
}
let childProgress;
async function childCapture(oracle, scratch) {
  assert(samePath(scratch, process.env.PISHARP_BRANCH_SCRATCH));
  assert(globalThis.pisharpLoadedReferenceModules instanceof Map, 'Locked full preload is required');
  const output = dirname(dirname(scratch)), owner = readJson(join(output, 'owner.json'));
  assert.equal(owner.owner, 'PiSharp-session-branches-capture-v1'); assert(samePath(owner.output, output)); assert(samePath(owner.oracle, oracle));
  assert(samePath(dirname(output), join(repo, 'artifacts/session-branches-reference')));
  assert(samePath(dirname(scratch), join(output, 'scratch')) && basename(scratch).startsWith('capture-'));
  assert(samePath(process.cwd(), join(scratch, 'workspace'))); assert.equal(fileHash(ownPath), owner.harnessSha256);
  assert.equal(tree(scratch).length, 0, 'Child requires a fresh file-free scratch'); validateInput();
  const originals = { Date, now: Date.now, random: Math.random, randomUUID: crypto.randomUUID, randomBytes: crypto.randomBytes };
  const guard = installScratchWriteGuard(scratch);
  const allowed = url => url.startsWith('node:') || url.startsWith('file:') && (inside(join(oracle, 'upstream'), fileURLToPath(url))
    || packageNames.some(name => inside(join(oracle, 'node_modules', name), fileURLToPath(url))));
  registerHooks({ resolve(specifier, context, nextResolve) { const result = nextResolve(specifier, context); assert(allowed(result.url), 'Unreviewed module fallback rejected: ' + result.url); return result; } });
  const { SessionManager } = await import(pathToFileURL(join(oracle, 'upstream/packages/coding-agent/src/core/session-manager.ts')).href);
  const { convertToLlm } = await import(pathToFileURL(join(oracle, 'upstream/packages/coding-agent/src/core/messages.ts')).href);
  const { getCurrentSystemMessage, getCurrentTools, getCurrentSystemPrompt } = await import(pathToFileURL(join(oracle, 'upstream/packages/ai/src/utils/transcript.ts')).href);
  const cases = []; childProgress = { cases };
  const matrix = manager => {
    const entries = manager.getEntries(), parents = new Set(entries.map(entry => entry.parentId));
    const selections = [{ leafId: null, selectionReasons: ['explicit-root'] }, ...entries.map(entry => ({ leafId: entry.id,
      selectionReasons: [entry.parentId === null ? 'structural-root' : parents.has(entry.id) ? 'middle' : 'structural-leaf'] }))];
    const originalLeaf = manager.getLeafId(), originalBytes = json([manager.getHeader(), ...entries]), observations = [];
    for (const selection of selections) {
      if (selection.leafId === null) manager.resetLeaf(); else manager.branch(selection.leafId);
      const projection = manager.buildSessionProjection(), context = manager.buildSessionContext(), llmMessages = convertToLlm(context.messages);
      const observation = { selectionId: selection.leafId, ...selection, header: manager.getHeader(), entries: manager.getEntries(), tree: manager.getTree(),
        children: [null, ...entries.map(entry => entry.id)].map(parentId => ({ parentId, entries: manager.getChildren(parentId) })),
        labels: entries.map(entry => ({ entryId: entry.id, label: manager.getLabel(entry.id) })), sessionName: manager.getSessionName(),
        branch: manager.getBranch(), leafId: manager.getLeafId(), leafEntry: manager.getLeafEntry(), projection, context, llmMessages,
        effectiveSystemMessage: getCurrentSystemMessage(context.messages), effectiveTools: getCurrentTools(context.messages), systemPrompt: getCurrentSystemPrompt(context.messages) };
      const paths = undefinedPaths(observation), owned = structuredClone(observation); owned.ownUndefinedPaths = paths; observations.push(owned);
      assert.equal(json([manager.getHeader(), ...manager.getEntries()]), originalBytes, 'Actual query/conversion mutated source entries');
    }
    if (originalLeaf === null) manager.resetLeaf(); else manager.branch(originalLeaf);
    return observations;
  };
  for (const fixture of input.cases) {
    const sessions = join(scratch, 'workspace', fixture.caseId, 'sessions'), path = join(sessions, 'authored.jsonl');
    const authoredWire = [fixture.header, ...fixture.entries].map(entry => JSON.stringify(entry) + '\n').join('');
    fs.writeFileSync(path, authoredWire, { flag: 'wx' });
    const originalFile = physicalFile(path), manager = SessionManager.open(path, sessions);
    const test = { caseId: fixture.caseId, inputHeader: structuredClone(fixture.header), inputEntries: structuredClone(fixture.entries),
      phases: [], generatedEntries: [], operationReturns: [], reopened: {}, relations: {}, contract: {} };
    cases.push(test);
    const phase = phaseId => {
      const before = physicalFile(path), originalLeaf = manager.getLeafId(), observed = matrix(manager), after = physicalFile(path);
      assert.equal(before.base64, after.base64, 'Query matrices must not rewrite source bytes');
      assert.equal(manager.getLeafId(), originalLeaf, 'Observation must restore the original active leaf');
      test.phases.push({ phaseId, activeLeafBeforeQueries: originalLeaf, physicalFileBefore: before, matrix: observed, physicalFileAfter: after });
    };
    phase('opened-authored');
    for (const operation of fixture.operations) {
      const before = guard.snapshot(); let value;
      if (operation.kind === 'branch') value = manager.branch(operation.entryId);
      else if (operation.kind === 'label') value = manager.appendLabelChange(operation.targetId, operation.clear ? undefined : operation.label);
      else value = manager.appendSessionInfo(operation.name);
      const after = guard.snapshot(), returned = { operation: structuredClone(operation), value, ownUndefinedPaths: undefinedPaths({ value }),
        filesystemCalls: { reads: after.reads - before.reads, writes: after.writes - before.writes, denied: after.denied - before.denied } };
      test.operationReturns.push(structuredClone(returned));
      if (typeof value === 'string') test.generatedEntries.push({ operationId: operation.operationId, entry: structuredClone(manager.getEntry(value)) });
      phase(operation.operationId);
    }
    const beforeReopen = physicalFile(path), reopened = SessionManager.open(path, sessions), finalLeaf = reopened.getLeafId();
    const context = reopened.buildSessionContext(), projection = reopened.buildSessionProjection();
    const observed = { header: reopened.getHeader(), entries: reopened.getEntries(), tree: reopened.getTree(), leafId: finalLeaf,
      sessionName: reopened.getSessionName(), branch: reopened.getBranch(), context, projection, llmMessages: convertToLlm(context.messages),
      effectiveSystemMessage: getCurrentSystemMessage(context.messages), effectiveTools: getCurrentTools(context.messages), systemPrompt: getCurrentSystemPrompt(context.messages) };
    test.reopened = { ...structuredClone(observed), ownUndefinedPaths: undefinedPaths(observed), physicalFileBefore: beforeReopen, physicalFileAfter: physicalFile(path) };
    const selected = test.phases.at(-1).matrix.find(view => view.leafId === finalLeaf);
    test.relations = { authoredOpenPreservesBytes: originalFile.base64 === test.phases[0].physicalFileBefore.base64,
      allQueriesPreserveBytes: test.phases.every(p => p.physicalFileBefore.base64 === p.physicalFileAfter.base64),
      everyEntryAndExplicitRootObserved: test.phases.every(p => p.matrix.length === p.matrix[0].entries.length + 1),
      reopenPreservesBytes: beforeReopen.base64 === test.reopened.physicalFileAfter.base64,
      reopenRetainsRawEntries: sameWire(reopened.getEntries(), manager.getEntries()),
      reopenRetainsCompleteTree: sameWire(test.reopened.tree, selected.tree), reopenRetainsContext: sameWire(test.reopened.context, selected.context),
      reopenRetainsLlm: sameWire(test.reopened.llmMessages, selected.llmMessages) };
    for (const [name, result] of Object.entries(test.relations)) assert.equal(result, true, fixture.caseId + ': ' + name);
    test.contract = repeatContract(test);
  }
  assert.equal(Date, originals.Date); assert.equal(Date.now, originals.now); assert.equal(Math.random, originals.random);
  assert.equal(crypto.randomUUID, originals.randomUUID); assert.equal(crypto.randomBytes, originals.randomBytes);
  assert.equal(guard.ownedOpenDescriptors(), 0);
  assert.throws(() => fs.writeFileSync(join(oracle, '.branches-forbidden-probe'), 'forbidden'), /outside owned scratch/);
  assert.throws(() => fs.writeFileSync(1, 'forbidden'), /unowned descriptor/);
  assert.throws(() => childProcess.spawnSync(process.execPath, ['--version']), /prohibits network and child-process access/);
  assert.throws(() => globalThis.fetch('https://branches.invalid/'), /prohibits network and child-process access/);
  const loadedModules = [...globalThis.pisharpLoadedReferenceModules.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
  return { schemaVersion: 1, fixtureId: input.fixtureId, kind: 'captured-unchanged-session-manager-branches', sourceSha: input.sourceSha,
    authoredInput: input, comparisonPolicy, cases, loadedModules,
    checks: { wholeSessionManagerLoaded: true, clocksAndRngUnmodified: true, scratchWriteGuard: true, networkAndProcessesDenied: true,
      ownedOpenDescriptors: guard.ownedOpenDescriptors(), filesystemCalls: guard.snapshot(), filesystemWriteAudit: guard.audit() } };
}

export function parseCaptureArguments(args) {
  let first = false, oracle, output;
  const usage = 'Usage: node capture-session-branches.mjs [--capture-new] --oracle APPROVED_SESSION_ORACLE --output ABSOLUTE_FRESH_CAPTURE_DIRECTORY';
  for (let index = 0; index < args.length; index++) {
    if (args[index] === '--capture-new' && !first) first = true;
    else if (args[index] === '--oracle' && oracle === undefined && args[index + 1] && !args[index + 1].startsWith('--')) oracle = resolve(args[++index]);
    else if (args[index] === '--output' && output === undefined && args[index + 1] && !args[index + 1].startsWith('--')) { const value = args[++index]; assert(isAbsolute(value), usage); output = resolve(value); }
    else throw new Error(usage);
  }
  assert(oracle && output, usage); return { first, oracle, output };
}
async function parentCapture(args) {
  const { first, oracle, output } = parseCaptureArguments(args);
  validateInput();
  assert.equal(fileHash(join(repo, 'tools/PiReferenceRunner/capture-session-lifecycle.mjs')), lifecycleHarnessSha256, 'Audited lifecycle harness changed');
  const inputPin = { path: family + '/core.input.json', bytes: fs.readFileSync(inputPath).length, sha256: fileHash(inputPath) };
  if (first) assert(![capturePath, manifestPath, fixtureLockPath].some(fs.existsSync), 'New capture preserves any existing fixture/manifest/lock');
  else { const manifest = readJson(manifestPath); assert.equal(fileHash(capturePath), manifest.capture.sha256); assert.equal(fileHash(fixtureLockPath), manifest.lock.sha256); assert.equal(fileHash(inputPath), manifest.input.sha256); }
  assert.equal(fileHash(qualificationPath), qualificationSha256, 'Frozen whole-module qualification changed');
  const qualification = readJson(qualificationPath), plan = readJson(planPath);
  for (const file of qualification.harnessFiles) assert.equal(fileHash(join(repo, file.path)), file.sha256, 'Locked harness changed: ' + file.path);
  assert.equal(plan.source.commit, qualification.environmentPins.sourceSha);
  assert(samePath(oracle, plan.workspace.proposedRoot), 'Only the approved existing session-context oracle is admitted');
  assert.equal(process.version, plan.runtime.version); assert.equal(fileHash(process.execPath), plan.runtime.sha256);
  assert(samePath(process.execPath, plan.runtime.absoluteExecutable));
  assert.equal(process.platform, qualification.environmentPins.platform); assert.equal(process.arch, qualification.environmentPins.architecture);
  noLinks(oracle); noLinks(output);
  const outputRoot = join(repo, 'artifacts/session-branches-reference');
  assert(samePath(dirname(output), outputRoot) && inside(outputRoot, output), 'Output must be one fresh direct child of artifacts/session-branches-reference');
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
  const environmentPins = { ...qualification.environmentPins, branchHarness: ownPin, authoredInput: inputPin, reusedLifecycleHarnessSha256: lifecycleHarnessSha256, qualificationLockSha256: qualificationSha256,
    dependencyVerification: 'Every installed file equals its integrity-pinned archive before and after both children; exact eight-package top-level set.' };
  noLinks(outputRoot); fs.mkdirSync(outputRoot, { recursive: true }); noLinks(outputRoot);
  fs.mkdirSync(output); fs.writeFileSync(join(output, 'owner.json'), json({ owner: 'PiSharp-session-branches-capture-v1', output, oracle, sourceSha: plan.source.commit, harnessSha256: ownPin.sha256 }), { flag: 'wx' });
  const scratchRoot = join(output, 'scratch'); fs.mkdirSync(scratchRoot);
  const writeNew = (name, value) => fs.writeFileSync(join(output, name), value, { flag: 'wx' });
  writeNew('environment-pins.json', json(environmentPins));
  const captures = [], children = [];
  try {
    for (let repeat = 1; repeat <= 2; repeat++) {
      const scratch = fs.mkdtempSync(join(scratchRoot, 'capture-')); assert(inside(scratchRoot, scratch)); noLinks(scratch);
      fs.mkdirSync(join(scratch, 'home')); fs.mkdirSync(join(scratch, 'workspace'));
      for (const caseId of caseIds) {
        const root = join(scratch, 'workspace', caseId); fs.mkdirSync(root); fs.mkdirSync(join(root, 'cwd')); fs.mkdirSync(join(root, 'sessions'));
      }
      const child = childProcess.spawnSync(process.execPath, ['--experimental-strip-types', '--import', pathToFileURL(join(repo, 'tools/PiReferenceRunner/full-preload.mjs')).href,
        ownPath, '--child', scratch], { cwd: join(scratch, 'workspace'), env: cleanEnvironment(oracle, scratch), windowsHide: true,
        encoding: 'utf8', timeout: 20000, maxBuffer: 8 * 1024 * 1024 });
      writeNew(`repeat-${repeat}.stdout.json`, child.stdout ?? ''); writeNew(`repeat-${repeat}.stderr.txt`, child.stderr ?? '');
      const receipt = { repeat, scratch, status: child.status, signal: child.signal, error: child.error ? { code: child.error.code, message: child.error.message } : null,
        stdoutBytes: Buffer.byteLength(child.stdout ?? '', 'utf8'), stderrBytes: Buffer.byteLength(child.stderr ?? '', 'utf8'), boundedTimeoutMilliseconds: 20000, maxBufferBytes: 8 * 1024 * 1024 };
      children.push(receipt); writeNew(`repeat-${repeat}.receipt.json`, json(receipt));
      assert.equal(child.status, 0, 'Whole SessionManager capture failed; preserve evidence and do not substitute implementation: ' + (child.error?.message ?? child.stderr));
      const capture = JSON.parse(child.stdout); assert(same(capture.cases.map(test => test.caseId), caseIds)); assert.equal(capture.sourceSha, plan.source.commit);
      assert.equal(capture.checks.ownedOpenDescriptors, 0); assert(capture.checks.clocksAndRngUnmodified && capture.checks.scratchWriteGuard && capture.checks.networkAndProcessesDenied);
      for (const file of capture.loadedModules) {
        assert(inside(oracle, join(oracle, file.path)) && (file.path.startsWith('upstream/') || packageNames.some(name => file.path.startsWith(`node_modules/${name}/`))));
        assert(same(file, modulePins.get(file.path)), 'Actual loaded module lacks exact existing qualification pin: ' + file.path);
      }
      assert(same(capture.loadedModules, qualification.loadedModules), 'Whole loaded closure changed from qualified canonical modules');
      captures.push(capture); writeNew(`repeat-${repeat}.raw.json`, json(capture));
    }
    assert(same(captures[0].cases.map(test => ({ caseId: test.caseId, contract: test.contract })), captures[1].cases.map(test => ({ caseId: test.caseId, contract: test.contract }))), 'Declared semantic/relational contracts differ between genuine runs');
    assert.equal(fileHash(inputPath), inputPin.sha256, 'Authored input changed');
    assert.equal(fileHash(join(repo, 'tools/PiReferenceRunner/capture-session-lifecycle.mjs')), lifecycleHarnessSha256);
    const after = readOnlySetupCheck(oracle, environment); assert(same(after.sourceFingerprint, before.sourceFingerprint), 'Source fingerprint changed');
    assert(same(verifyDependencies(), dependencies), 'Installed dependency/archive bytes changed');
    for (const file of qualification.harnessFiles) assert.equal(fileHash(join(repo, file.path)), file.sha256);
    for (const [name, value] of Object.entries(receiptPins)) assert.equal(value, { setupReceiptSha256: fileHash(restoredPath), preparedReceiptSha256: fileHash(join(oracle, '.pisharp-session-context-prepared.json')),
      projectionManifestSha256: fileHash(join(oracle, 'package.json')), projectionLockSha256: fileHash(join(oracle, 'package-lock.json')) }[name]);
    assert.equal(fileHash(qualificationPath), qualificationSha256); assert.equal(fileHash(ownPath), ownPin.sha256); assert.equal(fileHash(process.execPath), plan.runtime.sha256);
    for (const file of captures[0].loadedModules) assert.equal(fileHash(join(oracle, file.path)), file.sha256);
    
    const compared = captures[0].cases.map(({ caseId, contract }) => ({ caseId, contract }));
    if (first) {
      fs.writeFileSync(capturePath, json(captures[0]), { flag: 'wx' });
      fs.writeFileSync(fixtureLockPath, json({ schemaVersion: 1, environmentPins, loadedModules: captures[0].loadedModules, sourceUnchanged: true, dependenciesUnchanged: true, scope: comparisonPolicy.scope }), { flag: 'wx' });
      fs.writeFileSync(manifestPath, json({ schemaVersion: 1, fixtureId: input.fixtureId, sourceSha: input.sourceSha, caseCount: caseIds.length, kind: captures[0].kind,
        input: inputPin, capture: { path: family + '/capture.json', bytes: fs.readFileSync(capturePath).length, sha256: fileHash(capturePath) },
        lock: { path: family + '/oracle.lock.json', bytes: fs.readFileSync(fixtureLockPath).length, sha256: fileHash(fixtureLockPath) },
        provenance: { wholeSessionManager: true, separatePublicLlmConversion: true, repeatRuns: 2, equalDeclaredContracts: true, generatedIdsAndClocksRaw: true },
        scope: comparisonPolicy.scope }), { flag: 'wx' });
    } else {
      const manifest = readJson(manifestPath), locked = readJson(fixtureLockPath);
      assert.equal(fileHash(capturePath), manifest.capture.sha256); assert.equal(fileHash(fixtureLockPath), manifest.lock.sha256);
      assert(same(environmentPins, locked.environmentPins), 'Locked environment/harness/input changed');
      assert(same(compared, readJson(capturePath).cases.map(({ caseId, contract }) => ({ caseId, contract }))), 'Actual declared contract differs from immutable genuine fixture');
    }
    writeNew('capture.json', json(captures[0]));
    writeNew('contract.json', json({ schemaVersion: 1, sourceSha: plan.source.commit, comparisonPolicy, authoredInput: input, cases: captures[0].cases.map(({ caseId, contract }) => ({ caseId, contract })) }));
    const report = { schemaVersion: 1, status: 'captured', capturedInitialFixture: first, fixtureCaptureSha256: fileHash(capturePath), fixtureManifestSha256: fileHash(manifestPath), output, sourceSha: plan.source.commit, caseCount: caseIds.length, repeatRuns: 2,
      equalDeclaredContracts: true, rawByteIdentityRequired: false, sourceAndDependencyBytesUnchanged: true, loadedModulesMatchFrozenQualification: true,
      children, retainedScratchFiles: tree(scratchRoot), captureSha256: fileHash(join(output, 'capture.json')), contractSha256: fileHash(join(output, 'contract.json')),
      scope: comparisonPolicy.scope };
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
    catch (error) { console.log(JSON.stringify({ schemaVersion: 1, status: 'failed', partialObservations: childProgress, error: { message: error.message, stack: error.stack } })); throw error; }
  } else await parentCapture(args);
}
if (process.argv[1] && samePath(process.argv[1], ownPath)) await main();
