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
const caseIds = ['memory-zero-files', 'lazy-setup-no-file', 'lazy-user-materializes', 'lazy-assistant-materializes',
  'in-file-branch-keeps-siblings', 'fork-selected-fresh-parent', 'setup-only-fork-remains-lazy', 'open-reload-hidden-state'];
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

// These are authored inputs. Session/entry IDs, entry dates, filenames and cwd are
// supplied by the actual upstream implementation/environment and retained verbatim.
const input = {
  model: { provider: 'fixture', modelId: 'fixture-model' }, thinkingLevel: 'off', system: 'fixture system',
  hidden: { customType: 'fixture.hidden', content: 'fixture hidden', display: false, details: { opaque: { retained: true }, displayOnly: false } },
  stateType: 'fixture.native/state', state: { version: 1, value: 'common', future: { enabled: false, values: [1.25, null, 'λ'] } },
  user: 'fixture user', assistant: 'fixture assistant', left: 'fixture left', right: 'fixture right',
};
const comparisonPolicy = {
  version: 1,
  raw: 'Full own enumerable observations and exact physical UTF-8/base64 bytes are retained. Own undefined fields are separately enumerated. No raw ID, clock, cwd, parent link, record or path is replaced.',
  contract: 'A separately named derivation projects checkpoint counts, ordered entry types, runtime/LLM roles and text, model/thinking settings, full custom state and hidden-message payloads; it also evaluates declared within-run identity, ancestry, parent-file and byte-preservation relations.',
  omittedFromCrossRunContract: ['actual generated IDs', 'actual timestamp values', 'actual absolute scratch/cwd/file paths', 'raw byte hashes', 'filesystem call counts'],
  omittedFieldsRetainedAt: 'cases[].rawObservations (and the retained scratch JSONL files); omissions affect only explicit cross-run contract comparison.',
  repeatRule: 'Two fresh runs must have equal derived contracts and loaded module pins. Raw observations may differ because real clocks, random identifiers and fresh paths are used.',
  modelInputs: 'No provider execution. Ordinary user/assistant inputs and custom payloads are finite authored data. Custom state is raw session data; display:false custom messages contribute to context and public convertToLlm.',
  scope: 'Eight concrete SessionManager lifecycle schedules; no original package, full phase, provider, compaction or native parity closure claim.',
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
function semanticCheckpoint(checkpoint) {
  const m = checkpoint.manager, state = entries => entries.filter(e => e.type === 'custom').map(e => ({ customType: e.customType, data: e.data }));
  return { name: checkpoint.name, fileExists: checkpoint.physicalFile.exists, physicalRecordCount: checkpoint.physicalFile.records.length,
    entryCount: m.entries.length, branchCount: m.branch.length, entryTypes: m.entries.map(e => e.type), branchTypes: m.branch.map(e => e.type),
    runtimeRoles: m.context.messages.map(e => e.role), llmRoles: m.llmMessages.map(e => e.role),
    runtimeText: m.context.messages.map(text), llmText: m.llmMessages.map(text), model: m.context.model, thinkingLevel: m.context.thinkingLevel,
    physicalCustomState: state(m.entries), selectedCustomState: state(m.branch),
    hiddenMessages: m.branch.filter(e => e.type === 'custom_message').map(e => ({ customType: e.customType, content: e.content, display: e.display, details: e.details })),
    isPersistedConfiguration: m.isPersisted, sessionFileDefined: m.sessionFile !== undefined, headerVersion: m.header.version,
    headerMatchesSessionIdentity: m.header.id === m.sessionId, headerCwdMatchesRequested: m.header.cwd === checkpoint.requestedCwd,
    actualHeaderTimestampValid: Number.isFinite(Date.parse(m.header.timestamp)), leafMatchesBranchTail: m.leafId === (m.branch.at(-1)?.id ?? null),
    physicalRecordsMatchManager: !checkpoint.physicalFile.exists || sameWire(checkpoint.physicalFile.records, [m.header, ...m.entries]),
    customStateExcludedFromRuntime: !m.context.messages.some(e => Object.hasOwn(e, 'data') || text(e).includes('"future"')),
    actualFilenameContainsIdentity: m.sessionFile === undefined || m.sessionFile.endsWith('_' + m.sessionId + '.jsonl') };
}

let childProgress;
async function childCapture(oracle, scratch) {
  assert(samePath(scratch, process.env.PISHARP_LIFECYCLE_SCRATCH));
  assert(globalThis.pisharpLoadedReferenceModules instanceof Map, 'Locked full preload is required');
  const output = dirname(dirname(scratch)), owner = readJson(join(output, 'owner.json'));
  assert.equal(owner.owner, 'PiSharp-session-lifecycle-capture-v1'); assert(samePath(owner.output, output)); assert(samePath(owner.oracle, oracle));
  assert(samePath(dirname(output), join(repo, 'artifacts/session-lifecycle-reference')));
  assert(samePath(dirname(scratch), join(output, 'scratch')) && basename(scratch).startsWith('capture-'));
  assert(samePath(process.cwd(), join(scratch, 'workspace')));
  assert.equal(fileHash(ownPath), owner.harnessSha256); assert.equal(tree(scratch).length, 0, 'Child requires a fresh file-free scratch');
  const originals = { Date, now: Date.now, random: Math.random, randomUUID: crypto.randomUUID, randomBytes: crypto.randomBytes };
  const guard = installScratchWriteGuard(scratch);
  const allowed = url => url.startsWith('node:') || url.startsWith('file:') && (inside(join(oracle, 'upstream'), fileURLToPath(url))
    || packageNames.some(name => inside(join(oracle, 'node_modules', name), fileURLToPath(url))));
  registerHooks({ resolve(specifier, context, nextResolve) { const result = nextResolve(specifier, context); assert(allowed(result.url), 'Unreviewed module fallback rejected: ' + result.url); return result; } });
  const { SessionManager } = await import(pathToFileURL(join(oracle, 'upstream/packages/coding-agent/src/core/session-manager.ts')).href);
  const { convertToLlm } = await import(pathToFileURL(join(oracle, 'upstream/packages/coding-agent/src/core/messages.ts')).href);
  assert.equal(typeof SessionManager.create, 'function'); assert.equal(typeof SessionManager.inMemory, 'function'); assert.equal(typeof convertToLlm, 'function');
  const cases = []; childProgress = { cases };
  const user = content => ({ role: 'user', content, timestamp: Date.now() });
  const assistant = content => ({ role: 'assistant', content: [{ type: 'text', text: content }], api: 'openai-responses', provider: input.model.provider,
    model: input.model.modelId, timestamp: Date.now(), stopReason: 'stop', usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0,
      cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } } });
  const begin = caseId => {
    const cwd = join(scratch, 'workspace', caseId, 'cwd'), sessions = join(scratch, 'workspace', caseId, 'sessions');
    const result = { caseId, operations: [], rawObservations: { checkpoints: [], operationReturns: [], writeGuard: {} }, contract: {} };
    cases.push(result);
    const invoke = (name, action) => {
      const before = guard.snapshot(), value = action(), after = guard.snapshot();
      // Constructors/open return a live owner, whose public state is captured at
      // checkpoints. Capture their public identity without serializing private Maps.
      const observation = value instanceof SessionManager ? { sessionId: value.getSessionId(), sessionFile: value.getSessionFile() } : structuredClone(value);
      result.operations.push(name); result.rawObservations.operationReturns.push({ name, value: observation, ownUndefinedPaths: undefinedPaths({ value: observation }),
        filesystemCalls: { reads: after.reads - before.reads, writes: after.writes - before.writes, denied: after.denied - before.denied } });
      return value;
    };
    const checkpoint = (name, manager, extraFiles = []) => {
      const rawEntries = json([manager.getHeader(), ...manager.getEntries()]), context = manager.buildSessionContext(), projection = manager.buildSessionProjection();
      const llmMessages = convertToLlm(context.messages);
      assert.equal(json([manager.getHeader(), ...manager.getEntries()]), rawEntries, 'Context/LLM conversion mutated raw records');
      const observation = { name, requestedCwd: cwd, manager: { header: manager.getHeader(), entries: manager.getEntries(), branch: manager.getBranch(),
        tree: manager.getTree(), projection, context, llmMessages, sessionId: manager.getSessionId(), sessionFile: manager.getSessionFile(),
        sessionDir: manager.getSessionDir(), cwd: manager.getCwd(), leafId: manager.getLeafId(), isPersisted: manager.isPersisted() },
        physicalFile: physicalFile(manager.getSessionFile()), extraPhysicalFiles: extraFiles.map(physicalFile) };
      const ownUndefinedPaths = undefinedPaths(observation);
      const owned = structuredClone(observation); owned.ownUndefinedPaths = ownUndefinedPaths;
      result.rawObservations.checkpoints.push(owned); return owned;
    };
    const finish = relations => {
      result.rawObservations.writeGuard = { operationCalls: result.rawObservations.operationReturns.map(r => ({ name: r.name, ...r.filesystemCalls })) };
      result.contract = { checkpoints: result.rawObservations.checkpoints.map(semanticCheckpoint), relations };
      for (const [name, satisfied] of Object.entries(relations)) assert.equal(satisfied, true, caseId + ' relation failed: ' + name);
      return result;
    };
    return { cwd, sessions, result, invoke, checkpoint, finish };
  };
  const setup = (t, m) => ({ model: t.invoke('appendModelChange', () => m.appendModelChange(input.model.provider, input.model.modelId)),
    thinking: t.invoke('appendThinkingLevelChange', () => m.appendThinkingLevelChange(input.thinkingLevel)),
    system: t.invoke('appendMessage(system)', () => m.appendMessage({ role: 'system', content: input.system, timestamp: Date.now() })),
    state: t.invoke('appendCustomEntry(common)', () => m.appendCustomEntry(input.stateType, structuredClone(input.state))),
    hidden: t.invoke('appendCustomMessageEntry(display:false)', () => m.appendCustomMessageEntry(input.hidden.customType, input.hidden.content, input.hidden.display, structuredClone(input.hidden.details))) });
  const state = (t, m, value) => t.invoke('appendCustomEntry(' + value + ')', () => m.appendCustomEntry(input.stateType, { ...structuredClone(input.state), value }));
  const branched = (t, m) => {
    setup(t, m); const common = t.invoke('appendMessage(user)', () => m.appendMessage(user(input.user)));
    const leftMessage = t.invoke('appendMessage(left assistant)', () => m.appendMessage(assistant(input.left))), left = state(t, m, 'left');
    t.invoke('branch(common user)', () => m.branch(common));
    const rightMessage = t.invoke('appendMessage(right assistant)', () => m.appendMessage(assistant(input.right))), right = state(t, m, 'right');
    t.invoke('branch(left state)', () => m.branch(left)); return { common, leftMessage, left, rightMessage, right };
  };
  const unchangedFile = (before, after) => before.exists === after.exists && before.base64 === after.base64;
  const freshIdentity = (before, after) => before.manager.sessionId !== after.manager.sessionId && before.manager.header.id !== after.manager.header.id;

  {
    const t = begin(caseIds[0]), m = t.invoke('SessionManager.inMemory', () => SessionManager.inMemory(t.cwd));
    t.checkpoint('created', m); const selected = branched(t, m), source = t.checkpoint('selected-left', m);
    const returned = t.invoke('createBranchedSession(left state)', () => m.createBranchedSession(selected.left)), fork = t.checkpoint('forked-left', m);
    t.invoke('newSession', () => m.newSession()); const fresh = t.checkpoint('new-session', m);
    t.finish({ managerOperationsUseZeroFilesystemCalls: t.result.rawObservations.operationReturns.every(r => r.filesystemCalls.reads === 0 && r.filesystemCalls.writes === 0),
      noPhysicalFiles: t.result.rawObservations.checkpoints.every(c => !c.physicalFile.exists), memoryForkReturnsUndefined: returned === undefined,
      memoryForkHasNoParentFile: fork.manager.header.parentSession === undefined, forkIdentityFresh: freshIdentity(source, fork), newIdentityFresh: freshIdentity(fork, fresh),
      forkRetainsSelectedIds: same(fork.manager.entries.map(e => e.id), source.manager.branch.map(e => e.id)),
      forkExcludesRightSibling: !fork.manager.entries.some(e => e.id === selected.right || e.id === selected.rightMessage),
      newSessionIsEmptyRoot: fresh.manager.entries.length === 0 && fresh.manager.leafId === null });
  }
  {
    const t = begin(caseIds[1]), m = t.invoke('SessionManager.create', () => SessionManager.create(t.cwd, t.sessions));
    const created = t.checkpoint('created', m); setup(t, m); const pending = t.checkpoint('setup-pending', m);
    t.invoke('newSession', () => m.newSession()); const fresh = t.checkpoint('new-session', m, [pending.manager.sessionFile]);
    t.finish({ setupDoesNotCreateFile: !pending.physicalFile.exists, hiddenMessageDoesNotTriggerPersistence: pending.manager.entries.some(e => e.type === 'custom_message' && e.display === false) && !pending.physicalFile.exists,
      setupRetainsFiveRecords: pending.manager.entries.length === 5, configuredPersistenceIsNotFileExistence: pending.manager.isPersisted && !pending.physicalFile.exists,
      setupKeepsIdentity: created.manager.sessionId === pending.manager.sessionId, newIdentityFresh: freshIdentity(pending, fresh),
      newSessionIsEmptyRoot: fresh.manager.entries.length === 0 && fresh.manager.leafId === null, abandonedPendingSourceHasNoFile: !fresh.extraPhysicalFiles[0].exists });
  }
  {
    const t = begin(caseIds[2]), m = t.invoke('SessionManager.create', () => SessionManager.create(t.cwd, t.sessions));
    setup(t, m); const pending = t.checkpoint('before-user', m);
    t.invoke('appendMessage(user)', () => m.appendMessage(user(input.user))); const first = t.checkpoint('after-first-user', m);
    t.invoke('appendMessage(assistant)', () => m.appendMessage(assistant(input.assistant))); const after = t.checkpoint('after-assistant', m);
    t.invoke('newSession', () => m.newSession()); const fresh = t.checkpoint('new-session', m, [after.manager.sessionFile]);
    t.finish({ firstUserCreatesFile: !pending.physicalFile.exists && first.physicalFile.exists, firstUserMaterializesEntireAccumulatedLog: sameWire(first.physicalFile.records, [first.manager.header, ...first.manager.entries]),
      assistantAppendsToActualPrefix: after.physicalFile.utf8.startsWith(first.physicalFile.utf8), sourceIdentityKeptUntilNew: pending.manager.sessionId === after.manager.sessionId,
      newIdentityFresh: freshIdentity(after, fresh), newSessionDeferred: !fresh.physicalFile.exists && fresh.manager.entries.length === 0,
      newSessionPreservesOldPhysicalFile: unchangedFile(after.physicalFile, fresh.extraPhysicalFiles[0]) });
  }
  {
    const t = begin(caseIds[3]), m = t.invoke('SessionManager.create', () => SessionManager.create(t.cwd, t.sessions));
    setup(t, m); const pending = t.checkpoint('before-assistant', m);
    t.invoke('appendMessage(assistant)', () => m.appendMessage(assistant(input.assistant))); const first = t.checkpoint('after-first-assistant', m);
    t.finish({ firstAssistantCreatesFile: !pending.physicalFile.exists && first.physicalFile.exists,
      firstAssistantMaterializesEntireAccumulatedLog: sameWire(first.physicalFile.records, [first.manager.header, ...first.manager.entries]),
      noOrdinaryUserRequired: !first.manager.entries.some(e => e.type === 'message' && e.message.role === 'user'),
      identityKept: pending.manager.sessionId === first.manager.sessionId });
  }
  {
    const t = begin(caseIds[4]), m = t.invoke('SessionManager.create', () => SessionManager.create(t.cwd, t.sessions));
    const selected = branched(t, m), left = t.checkpoint('selected-left', m);
    t.invoke('resetLeaf', () => m.resetLeaf()); const root = t.checkpoint('selected-root', m);
    t.invoke('branch(left state)', () => m.branch(selected.left)); const restored = t.checkpoint('restored-left', m);
    const reopened = t.invoke('SessionManager.open', () => SessionManager.open(m.getSessionFile(), t.sessions));
    const loaded = t.checkpoint('reopened-physical-tail', reopened);
    t.finish({ selectedLeafDiffersFromPhysicalTail: left.manager.leafId === selected.left && left.manager.entries.at(-1).id === selected.right,
      siblingRetainedPhysically: left.physicalFile.records.some(e => e.id === selected.right), siblingExcludedFromSelectedBranch: !left.manager.branch.some(e => e.id === selected.right),
      rootHasEmptyBranchAndContext: root.manager.branch.length === 0 && root.manager.context.messages.length === 0,
      rootAndReturnDoNotRewriteFile: unchangedFile(left.physicalFile, root.physicalFile) && unchangedFile(left.physicalFile, restored.physicalFile),
      returnRestoresSelectedBranch: same(restored.manager.branch, left.manager.branch), reopenUsesPhysicalTail: loaded.manager.leafId === selected.right,
      reopenPreservesSourceBytes: unchangedFile(left.physicalFile, loaded.physicalFile) });
  }
  {
    const t = begin(caseIds[5]), m = t.invoke('SessionManager.create', () => SessionManager.create(t.cwd, t.sessions));
    const selected = branched(t, m), source = t.checkpoint('selected-source', m), oldFile = m.getSessionFile();
    const returned = t.invoke('createBranchedSession(left state)', () => m.createBranchedSession(selected.left)), fork = t.checkpoint('forked-left', m, [oldFile]);
    const reopened = t.invoke('SessionManager.open(fork)', () => SessionManager.open(returned, t.sessions));
    const loaded = t.checkpoint('reopened-fork', reopened, [oldFile]);
    t.finish({ forkIdentityFresh: freshIdentity(source, fork), forkPathFresh: oldFile !== returned && returned === fork.manager.sessionFile,
      forkHasActualSourceParentFile: fork.manager.header.parentSession === oldFile, forkKeepsActualSourceCwd: fork.manager.header.cwd === source.manager.header.cwd,
      forkRetainsSelectedIds: same(fork.manager.entries.map(e => e.id), source.manager.branch.map(e => e.id)),
      forkExcludesRightSibling: !fork.manager.entries.some(e => e.id === selected.right || e.id === selected.rightMessage),
      conversationForkIsImmediatelyPhysical: fork.physicalFile.exists, forkPreservesSourceBytes: unchangedFile(source.physicalFile, fork.extraPhysicalFiles[0]),
      reopenPreservesBothPhysicalFiles: unchangedFile(fork.physicalFile, loaded.physicalFile) && unchangedFile(source.physicalFile, loaded.extraPhysicalFiles[0]),
      reopenRestoresSelectedContext: same(fork.manager.context, loaded.manager.context) });
  }
  {
    const t = begin(caseIds[6]), m = t.invoke('SessionManager.create', () => SessionManager.create(t.cwd, t.sessions));
    const setupIds = setup(t, m), left = state(t, m, 'left'); t.invoke('branch(hidden setup)', () => m.branch(setupIds.hidden));
    const right = state(t, m, 'right'), source = t.checkpoint('setup-only-source', m), oldFile = m.getSessionFile();
    t.invoke('createBranchedSession(left state)', () => m.createBranchedSession(left)); const fork = t.checkpoint('setup-only-fork', m, [oldFile]);
    state(t, m, 'after-fork'); const stillPending = t.checkpoint('fork-state-pending', m, [oldFile]);
    t.invoke('appendMessage(assistant)', () => m.appendMessage(assistant(input.assistant))); const materialized = t.checkpoint('fork-first-assistant', m, [oldFile]);
    t.finish({ sourceSetupHasNoPhysicalFile: !source.physicalFile.exists, setupOnlyForkHasNoPhysicalFile: !fork.physicalFile.exists,
      furtherStateStillDoesNotCreateFile: !stillPending.physicalFile.exists, forkIdentityFresh: freshIdentity(source, fork), forkHasAllocatedSourceParentFile: fork.manager.header.parentSession === oldFile,
      forkSelectsLeftAndExcludesRight: fork.manager.leafId === left && !fork.manager.entries.some(e => e.id === right),
      firstAssistantMaterializesAccumulatedFork: materialized.physicalFile.exists && sameWire(materialized.physicalFile.records, [materialized.manager.header, ...materialized.manager.entries]),
      unmaterializedSourceStaysAbsent: !materialized.extraPhysicalFiles[0].exists });
  }
  {
    const t = begin(caseIds[7]), m = t.invoke('SessionManager.create', () => SessionManager.create(t.cwd, t.sessions));
    setup(t, m); t.invoke('appendMessage(user)', () => m.appendMessage(user(input.user))); t.invoke('appendMessage(assistant)', () => m.appendMessage(assistant(input.assistant)));
    const source = t.checkpoint('before-open', m), opened = t.invoke('SessionManager.open', () => SessionManager.open(m.getSessionFile(), t.sessions));
    const loaded = t.checkpoint('after-open', opened);
    t.finish({ openReadsActualHeaderCwd: loaded.manager.cwd === source.manager.header.cwd && loaded.manager.cwd !== process.cwd(),
      openPreservesIdentityAndRawRecords: loaded.manager.sessionId === source.manager.sessionId && same(loaded.manager.entries, source.manager.entries),
      openDoesNotRewriteValidBytes: unchangedFile(source.physicalFile, loaded.physicalFile),
      hiddenMessageContributesToRuntime: loaded.manager.context.messages.some(e => e.role === 'custom' && e.display === false && text(e) === input.hidden.content),
      hiddenMessageContributesToLlmAsUser: loaded.manager.llmMessages.some(e => e.role === 'user' && text(e) === input.hidden.content),
      stateRetainedRaw: loaded.manager.entries.some(e => e.type === 'custom' && same(e.data, input.state)),
      stateExcludedFromModel: !loaded.manager.context.messages.some(e => Object.hasOwn(e, 'data')) && !loaded.manager.llmMessages.some(e => text(e).includes('"future"')),
      openRestoresContextAndLlm: same(loaded.manager.context, source.manager.context) && same(loaded.manager.llmMessages, source.manager.llmMessages) });
  }
  assert.equal(Date, originals.Date); assert.equal(Date.now, originals.now); assert.equal(Math.random, originals.random);
  assert.equal(crypto.randomUUID, originals.randomUUID); assert.equal(crypto.randomBytes, originals.randomBytes);
  assert.equal(guard.ownedOpenDescriptors(), 0, 'All actual manager file descriptors must have closed');
  // Assert denial by calling the guards, without performing network/process/file effects.
  assert.throws(() => fs.writeFileSync(join(oracle, '.lifecycle-forbidden-probe'), 'forbidden'), /outside owned scratch/);
  assert.throws(() => fs.writeFileSync(1, 'forbidden'), /unowned descriptor/);
  assert.throws(() => childProcess.spawnSync(process.execPath, ['--version']), /prohibits network and child-process access/);
  assert.throws(() => globalThis.fetch('https://lifecycle.invalid/'), /prohibits network and child-process access/);
  const loadedModules = [...globalThis.pisharpLoadedReferenceModules.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
  return { schemaVersion: 1, kind: 'captured-unchanged-session-manager-lifecycle', sourceSha: 'd86654abb8862e201933517d6f1fce9f88dd117f',
    authoredInput: input, comparisonPolicy, cases, loadedModules,
    checks: { wholeSessionManagerLoaded: true, clocksAndRngUnmodified: true, scratchWriteGuard: true, networkAndProcessesDenied: true,
      ownedOpenDescriptors: guard.ownedOpenDescriptors(), filesystemCalls: guard.snapshot(), filesystemWriteAudit: guard.audit() } };
}

export function parseCaptureArguments(args) {
  let first = false, oracle, output;
  const usage = 'Usage: node capture-session-lifecycle.mjs --capture-new --oracle APPROVED_SESSION_ORACLE --output ABSOLUTE_FRESH_CAPTURE_DIRECTORY';
  for (let index = 0; index < args.length; index++) {
    if (args[index] === '--capture-new' && !first) first = true;
    else if (args[index] === '--oracle' && oracle === undefined && args[index + 1] && !args[index + 1].startsWith('--')) oracle = resolve(args[++index]);
    else if (args[index] === '--output' && output === undefined && args[index + 1] && !args[index + 1].startsWith('--')) { const value = args[++index]; assert(isAbsolute(value), usage); output = resolve(value); }
    else throw new Error(usage);
  }
  assert(first && oracle && output, usage); return { oracle, output };
}
async function parentCapture(args) {
  const { oracle, output } = parseCaptureArguments(args);
  assert.equal(fileHash(qualificationPath), qualificationSha256, 'Frozen whole-module qualification changed');
  const qualification = readJson(qualificationPath), plan = readJson(planPath);
  for (const file of qualification.harnessFiles) assert.equal(fileHash(join(repo, file.path)), file.sha256, 'Locked harness changed: ' + file.path);
  assert.equal(plan.source.commit, qualification.environmentPins.sourceSha);
  assert(samePath(oracle, plan.workspace.proposedRoot), 'Only the approved existing session-context oracle is admitted');
  assert.equal(process.version, plan.runtime.version); assert.equal(fileHash(process.execPath), plan.runtime.sha256);
  assert(samePath(process.execPath, plan.runtime.absoluteExecutable));
  assert.equal(process.platform, qualification.environmentPins.platform); assert.equal(process.arch, qualification.environmentPins.architecture);
  noLinks(oracle); noLinks(output);
  const outputRoot = join(repo, 'artifacts/session-lifecycle-reference');
  assert(samePath(dirname(output), outputRoot) && inside(outputRoot, output), 'Output must be one fresh direct child of artifacts/session-lifecycle-reference');
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
  const environmentPins = { ...qualification.environmentPins, lifecycleHarness: ownPin, qualificationLockSha256: qualificationSha256,
    dependencyVerification: 'Every installed file equals its integrity-pinned archive before and after both children; exact eight-package top-level set.' };
  noLinks(outputRoot); fs.mkdirSync(outputRoot, { recursive: true }); noLinks(outputRoot);
  fs.mkdirSync(output); fs.writeFileSync(join(output, 'owner.json'), json({ owner: 'PiSharp-session-lifecycle-capture-v1', output, oracle, sourceSha: plan.source.commit, harnessSha256: ownPin.sha256 }), { flag: 'wx' });
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
    const after = readOnlySetupCheck(oracle, environment); assert(same(after.sourceFingerprint, before.sourceFingerprint), 'Source fingerprint changed');
    assert(same(verifyDependencies(), dependencies), 'Installed dependency/archive bytes changed');
    for (const file of qualification.harnessFiles) assert.equal(fileHash(join(repo, file.path)), file.sha256);
    for (const [name, value] of Object.entries(receiptPins)) assert.equal(value, { setupReceiptSha256: fileHash(restoredPath), preparedReceiptSha256: fileHash(join(oracle, '.pisharp-session-context-prepared.json')),
      projectionManifestSha256: fileHash(join(oracle, 'package.json')), projectionLockSha256: fileHash(join(oracle, 'package-lock.json')) }[name]);
    assert.equal(fileHash(qualificationPath), qualificationSha256); assert.equal(fileHash(ownPath), ownPin.sha256); assert.equal(fileHash(process.execPath), plan.runtime.sha256);
    for (const file of captures[0].loadedModules) assert.equal(fileHash(join(oracle, file.path)), file.sha256);
    writeNew('capture.json', json(captures[0]));
    writeNew('contract.json', json({ schemaVersion: 1, sourceSha: plan.source.commit, comparisonPolicy, authoredInput: input, cases: captures[0].cases.map(({ caseId, operations, contract }) => ({ caseId, operations, contract })) }));
    const report = { schemaVersion: 1, status: 'captured', output, sourceSha: plan.source.commit, caseCount: caseIds.length, repeatRuns: 2,
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
