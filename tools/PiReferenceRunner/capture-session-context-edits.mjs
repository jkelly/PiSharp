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
const caseIds = ['manager-repeated-latest-omission', 'manager-exact-shape-all-roles', 'manager-branch-before-after-sibling',
  'manager-compaction-before-after', 'manager-admission-no-effects', 'manager-memory-edit-history'];
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
  model: { provider: 'fixture', modelId: 'fixture-model' }, thinkingLevel: 'off', system: 'original system',
  nestedMessageTimestamp: 1711929600000,
  stateType: 'fixture.inert', state: { version: 1, disabledExtension: true, value: 'raw state' },
  hidden: { customType: 'fixture.hidden', content: 'hidden original', display: false, details: { retained: null } },
  replacements: { first: { content: 'first replacement', wrapper: { retained: null } },
    latest: { content: 'latest replacement', wrapper: { retained: true } },
    normalizedString: { content: 'normalized replacement', wrapperMustBeDropped: { retained: true } },
    array: { content: [{ type: 'text', text: 'array replacement', extra: null }], wrapperMustRemain: { retained: true } },
    image: { content: [{ type: 'text', text: 'replacement image' }, { type: 'image', data: 'AA==', mimeType: 'image/png', extra: null }], wrapperMustRemain: null } },
  usage: { input: 11, output: 7, cacheRead: 3, cacheWrite: 2, totalTokens: 23,
    cost: { input: 0.11, output: 0.07, cacheRead: 0.03, cacheWrite: 0.02, total: 0.23 } }
};
const comparisonPolicy = {
  version: 1,
  raw: 'Complete actual manager observations, returns/errors, exact UTF-8/base64 physical bytes, IDs, timestamps, paths, raw entries, projection, context and convertToLlm are retained. Own undefined fields are separately enumerated.',
  contract: 'Only the separately named semantic checkpoint derivation omits real generated identity/time/path values; it compares counts, types, runtime/LLM roles/text, model/thinking, full custom state/hidden payload and declared preservation relations.',
  omittedFromCrossRunContract: ['actual generated IDs', 'actual timestamp values', 'actual absolute scratch/cwd/file paths', 'raw byte hashes', 'filesystem call counts', 'raw error text containing actual IDs'],
  omittedFieldsRetainedAt: 'cases[].rawObservations and scratch files; no raw observation is normalized.',
  repeatRule: 'Two fresh source runs require equal derived semantic/relational contracts and the exact original module closure. Real Date and RNG remain unmodified.',
  nativeReplay: 'Native tests may inject the observed actual source-generated entry IDs and UTC milliseconds into native author functions only, to compare actual native append records. The unchanged source receives no ID, RNG or clock shim.',
  admission: 'Missing, header, sibling, system, custom-state, edit and compaction targets are rejected by actual appendContextEdit; imported stored replay remains a distinct permissive surface. Null always omits and never restores.',
  modelInputs: 'Finite authored executable content; no provider or tool acquisition. Exact null/object/string/array replacements include authored wrapper fields.',
  scope: 'Six whole SessionManager context-edit schedules, including actual appendContextEdit and branch/compaction/open. No full original package, phase, provider, recovery or native parity closure claim.'
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
    GIT_OPTIONAL_LOCKS: '0', PISHARP_REFERENCE_ORACLE: oracle, PISHARP_CONTEXT_EDITS_SCRATCH: scratch };
}

// Capability wrapper around real fs functions, not a SessionManager replacement.
// Only sync mkdir/open/write/append/close used by the actual manager are admitted;
// every mutating path must be in this child's explicit fresh scratch, without links.
function installScratchWriteGuard(scratch) {
  noLinks(scratch); assert(fs.lstatSync(scratch).isDirectory());
  const originals = Object.fromEntries(Object.keys(fs).filter(key => typeof fs[key] === 'function').map(key => [key, fs[key]]));
  const writable = new Map(), opened = new Map(), calls = { reads: 0, writes: 0, denied: 0 }, audit = [];
  const deny = name => { calls.denied++; throw new Error('Context-edit capture filesystem operation denied: ' + name); };
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
  assert(samePath(scratch, process.env.PISHARP_CONTEXT_EDITS_SCRATCH));
  assert(globalThis.pisharpLoadedReferenceModules instanceof Map, 'Locked full preload is required');
  const output = dirname(dirname(scratch)), owner = readJson(join(output, 'owner.json'));
  assert.equal(owner.owner, 'PiSharp-session-context-edits-capture-v1'); assert(samePath(owner.output, output)); assert(samePath(owner.oracle, oracle));
  assert(samePath(dirname(output), join(repo, 'artifacts/session-context-edits-reference')));
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
  assert.equal(typeof SessionManager.prototype.appendContextEdit, 'function');
  const cases = []; childProgress = { cases };
  const user = content => ({ role: 'user', content, timestamp: input.nestedMessageTimestamp, metadata: { source: 'authored', nil: null } });
  const assistant = (content = 'assistant original', calls = false) => ({
    role: 'assistant', content: calls ? [{ type: 'toolCall', id: 'authored-call', name: 'read', arguments: { path: 'unopened:/fixture' } }] : [{ type: 'text', text: content, textSignature: 'opaque-signature' }],
    api: 'openai-responses', provider: input.model.provider, model: input.model.modelId,
    timestamp: input.nestedMessageTimestamp, stopReason: calls ? 'toolUse' : 'stop', usage: structuredClone(input.usage), opaque: { signature: 'retained', nil: null }
  });
  const toolResult = () => ({ role: 'toolResult', toolCallId: 'authored-call', toolName: 'read',
    content: [{ type: 'text', text: 'tool original' }], isError: false, timestamp: input.nestedMessageTimestamp, opaque: { retained: true } });
  const begin = (caseId, memory = false) => {
    const cwd = join(scratch, 'workspace', caseId, 'cwd'), sessions = join(scratch, 'workspace', caseId, 'sessions');
    const manager = memory ? SessionManager.inMemory(cwd) : SessionManager.create(cwd, sessions);
    const result = { caseId, operations: [], rawObservations: { checkpoints: [], operationReturns: [] }, contract: {} };
    cases.push(result);
    const checkpoint = name => {
      const raw = json([manager.getHeader(), ...manager.getEntries()]), context = manager.buildSessionContext(), projection = manager.buildSessionProjection();
      const observation = { name, requestedCwd: cwd, manager: { header: manager.getHeader(), entries: manager.getEntries(), branch: manager.getBranch(),
        tree: manager.getTree(), projection, context, llmMessages: convertToLlm(context.messages), sessionId: manager.getSessionId(),
        sessionFile: manager.getSessionFile(), sessionDir: manager.getSessionDir(), cwd: manager.getCwd(), leafId: manager.getLeafId(), isPersisted: manager.isPersisted() },
        physicalFile: physicalFile(manager.getSessionFile()), extraPhysicalFiles: [] };
      assert.equal(json([manager.getHeader(), ...manager.getEntries()]), raw, 'Pure views changed original stored records');
      const owned = structuredClone(observation); owned.ownUndefinedPaths = undefinedPaths(observation);
      result.rawObservations.checkpoints.push(owned); return owned;
    };
    const invoke = (name, method, args, allowFailure = false) => {
      const before = json([manager.getHeader(), ...manager.getEntries()]), beforeLeaf = manager.getLeafId(), beforeFile = physicalFile(manager.getSessionFile()), ioBefore = guard.snapshot();
      let value, error;
      try { value = manager[method](...structuredClone(args)); }
      catch (caught) { if (!allowFailure) throw caught; error = { name: caught.name, message: caught.message }; }
      const ioAfter = guard.snapshot(), afterFile = physicalFile(manager.getSessionFile());
      const returnedEntry = typeof value === 'string' && manager.getEntry(value) ? manager.getEntry(value) : undefined;
      const row = { name, method, args: structuredClone(args), value, returnedEntry, error,
        failedOperationPreservedRecords: error ? before === json([manager.getHeader(), ...manager.getEntries()]) : undefined,
        failedOperationPreservedLeaf: error ? beforeLeaf === manager.getLeafId() : undefined,
        failedOperationPreservedBytes: error ? beforeFile.exists === afterFile.exists && beforeFile.base64 === afterFile.base64 : undefined,
        filesystemCalls: { reads: ioAfter.reads - ioBefore.reads, writes: ioAfter.writes - ioBefore.writes, denied: ioAfter.denied - ioBefore.denied } };
      row.ownUndefinedPaths = undefinedPaths(row); result.operations.push({ name, method }); result.rawObservations.operationReturns.push(structuredClone(row));
      checkpoint(name); return value;
    };
    const edit = (name, target, replacement) => invoke(name, 'appendContextEdit', [target, replacement]);
    const finish = relations => {
      for (const [name, satisfied] of Object.entries(relations)) assert.equal(satisfied, true, caseId + ': ' + name);
      result.contract = { checkpoints: result.rawObservations.checkpoints.map(semanticCheckpoint), relations };
    };
    checkpoint('created');
    return { manager, result, invoke, edit, checkpoint, finish, sessions };
  };
  const setup = t => {
    const model = t.invoke('model', 'appendModelChange', [input.model.provider, input.model.modelId]);
    t.invoke('thinking', 'appendThinkingLevelChange', [input.thinkingLevel]);
    const system = t.invoke('system', 'appendMessage', [{ role: 'system', content: input.system, timestamp: input.nestedMessageTimestamp }]);
    const state = t.invoke('state', 'appendCustomEntry', [input.stateType, input.state]);
    return { model, system, state };
  };
  const last = t => t.result.rawObservations.checkpoints.at(-1);
  const originalStillPresent = (t, id, raw) => json(t.manager.getEntry(id)) === raw;
  {
    const t = begin(caseIds[0]); setup(t);
    const target = t.invoke('user', 'appendMessage', [user('user original')]), original = json(t.manager.getEntry(target));
    t.edit('replace-first', target, input.replacements.first); t.edit('replace-latest', target, input.replacements.latest);
    const latest = last(t); t.edit('omit', target, null); const omitted = last(t);
    t.edit('omit-again-never-restore', target, null);
    const finalFile = physicalFile(t.manager.getSessionFile()), opened = SessionManager.open(t.manager.getSessionFile(), t.sessions);
    t.finish({ latestWins: latest.manager.context.messages.some(message => text(message) === 'latest replacement'),
      nullOmits: !omitted.manager.context.messages.some(message => ['user original', 'first replacement', 'latest replacement'].includes(text(message))),
      secondNullStillOmits: !t.manager.buildSessionContext().messages.some(message => text(message) === 'user original'),
      originalRawUnchanged: originalStillPresent(t, target, original), reopenPreservesBytes: finalFile.base64 === physicalFile(opened.getSessionFile()).base64,
      reopenSameFullRecords: sameWire(opened.getEntries(), t.manager.getEntries()), reopenSameContext: sameWire(opened.buildSessionContext(), t.manager.buildSessionContext()) });
    t.result.rawObservations.reopened = { header: opened.getHeader(), entries: opened.getEntries(), projection: opened.buildSessionProjection(),
      context: opened.buildSessionContext(), llmMessages: convertToLlm(opened.buildSessionContext().messages), physicalFile: physicalFile(opened.getSessionFile()) };
    t.result.rawObservations.reopened.ownUndefinedPaths = undefinedPaths(t.result.rawObservations.reopened);
  }
  {
    const t = begin(caseIds[1]); setup(t);
    const u = t.invoke('user', 'appendMessage', [user('user original')]), a = t.invoke('assistant', 'appendMessage', [assistant(undefined, true)]);
    const r = t.invoke('tool-result', 'appendMessage', [toolResult()]);
    const c = t.invoke('hidden-custom', 'appendCustomMessageEntry', [input.hidden.customType, input.hidden.content, false, input.hidden.details]);
    const original = t.manager.getEntries().filter(e => [u, a, r, c].includes(e.id)).map(e => json(e));
    const ae = t.edit('assistant-string-new-wrapper', a, input.replacements.normalizedString);
    const re = t.edit('tool-string-new-wrapper', r, input.replacements.normalizedString);
    const ce = t.edit('custom-string-keeps-wrapper', c, input.replacements.normalizedString);
    const ue = t.edit('user-image-keeps-wrapper', u, input.replacements.image);
    const array = t.edit('assistant-array-keeps-wrapper', a, input.replacements.array);
    t.edit('omit-user', u, null); t.edit('omit-assistant', a, null); t.edit('omit-tool-result', r, null); t.edit('omit-hidden-custom', c, null);
    t.finish({ assistantStringOnlyContent: same(Object.keys(t.manager.getEntry(ae).replacement), ['content']),
      toolStringOnlyContent: same(Object.keys(t.manager.getEntry(re).replacement), ['content']),
      customStringExactWrapper: same(t.manager.getEntry(ce).replacement, input.replacements.normalizedString),
      userImageExactWrapper: same(t.manager.getEntry(ue).replacement, input.replacements.image),
      assistantArrayExactWrapper: same(t.manager.getEntry(array).replacement, input.replacements.array),
      originalRawMetadataAndUsageUnchanged: same(original, t.manager.getEntries().filter(e => [u, a, r, c].includes(e.id)).map(e => json(e))),
      allFourContributionsOmitted: t.manager.buildSessionContext().messages.every(message => message.role === 'system') });
  }
  {
    const t = begin(caseIds[2]); setup(t);
    const u = t.invoke('user', 'appendMessage', [user('branch original')]), raw = json(t.manager.getEntry(u));
    const left = t.edit('left-replacement', u, input.replacements.first);
    t.invoke('before-edit-original', 'branch', [u]); const before = last(t);
    const right = t.edit('right-omission', u, null), omitted = last(t);
    t.invoke('return-left-replacement', 'branch', [left]); const restored = last(t);
    t.invoke('select-explicit-root', 'resetLeaf', []); const root = last(t);
    t.invoke('return-right-omission', 'branch', [right]);
    t.finish({ beforeEditRevealsOriginal: before.manager.context.messages.some(message => text(message) === 'branch original'),
      siblingEditExcluded: restored.manager.context.messages.some(message => text(message) === 'first replacement') && !restored.manager.branch.some(e => e.id === right),
      omissionOnlyOnRight: !omitted.manager.context.messages.some(message => text(message) === 'branch original'),
      explicitRootEmpty: root.manager.branch.length === 0 && root.manager.context.messages.length === 0,
      wholeForestRetainsBothEdits: t.manager.getEntries().some(e => e.id === left) && t.manager.getEntries().some(e => e.id === right),
      originalRawUnchanged: originalStillPresent(t, u, raw) });
  }
  {
    const t = begin(caseIds[3]); setup(t);
    const trimmed = t.invoke('trimmed-user', 'appendMessage', [user('trimmed original')]);
    const kept = t.invoke('kept-user', 'appendMessage', [user('kept original')]);
    const billed = t.invoke('billed-assistant', 'appendMessage', [assistant('billed original')]), raw = json(t.manager.getEntry(billed));
    t.edit('pre-checkpoint-kept-replacement', kept, input.replacements.first);
    const checkpoint = t.invoke('compaction', 'appendCompaction', ['SUMMARY', kept, 23, { files: [], opaque: null }, false, input.usage]);
    const atCheckpoint = last(t);
    t.edit('post-checkpoint-trimmed-edit-inert', trimmed, input.replacements.latest); const trimmedEdit = last(t);
    t.edit('post-checkpoint-kept-replacement', kept, input.replacements.latest); const later = last(t);
    t.edit('post-checkpoint-omit-billed', billed, null); const omitted = last(t);
    t.invoke('branch-before-checkpoint', 'branch', [billed]); const before = last(t);
    t.invoke('return-checkpoint', 'branch', [checkpoint]);
    t.finish({ preCompactionEditStillAppliesToKept: atCheckpoint.manager.context.messages.some(message => text(message) === 'first replacement'),
      trimmedTargetRemainsOutsideContext: !trimmedEdit.manager.context.messages.some(message => text(message) === 'latest replacement'),
      laterEditWinsForKeptTarget: later.manager.context.messages.some(message => text(message) === 'latest replacement'),
      postCheckpointOmissionApplies: !omitted.manager.context.messages.some(message => text(message) === 'billed original'),
      beforeCheckpointRestoresRawBilledContribution: before.manager.context.messages.some(message => text(message) === 'billed original'),
      rawBilledUsageUnchanged: originalStillPresent(t, billed, raw), checkpointSummaryRetained: t.manager.buildSessionContext().messages.some(message => message.role === 'compactionSummary') });
  }
  {
    const t = begin(caseIds[4]), ids = setup(t);
    const u = t.invoke('user', 'appendMessage', [user('admission original')]), other = t.invoke('inactive-user', 'appendMessage', [user('inactive original')]);
    t.invoke('select-user', 'branch', [u]); const edit = t.edit('valid-edit', u, input.replacements.first);
    const checkpoint = t.invoke('compaction', 'appendCompaction', ['admission summary', u, 1]);
    for (const [name, target, replacement] of [
      ['missing', 'missing-target', null], ['header', t.manager.getHeader().id, null], ['inactive', other, null],
      ['system', ids.system, null], ['custom-state', ids.state, null], ['model', ids.model, null], ['edit', edit, null], ['compaction', checkpoint, null],
      ['replacement-number', u, 7], ['replacement-no-content', u, {}], ['replacement-null-content', u, { content: null }],
      ['replacement-object-content', u, { content: {} }]
    ]) t.invoke('reject-' + name, 'appendContextEdit', [target, replacement], true);
    const failures = t.result.rawObservations.operationReturns.filter(row => row.name.startsWith('reject-'));
    t.finish({ everyInvalidRequestRejected: failures.every(row => row.error !== undefined),
      everyFailurePreservesFullRecordsAndLeaf: failures.every(row => row.failedOperationPreservedRecords && row.failedOperationPreservedLeaf),
      everyFailurePreservesPhysicalBytes: failures.every(row => row.failedOperationPreservedBytes && row.filesystemCalls.writes === 0) });
  }
  {
    const t = begin(caseIds[5], true); setup(t);
    const u = t.invoke('user', 'appendMessage', [user('memory original')]), raw = json(t.manager.getEntry(u));
    const edited = t.edit('replace-memory', u, input.replacements.image); t.edit('omit-memory', u, null);
    t.invoke('branch-before-memory-edit', 'branch', [u]); const original = last(t);
    t.invoke('branch-memory-replacement', 'branch', [edited]); const replaced = last(t);
    t.finish({ zeroManagerFileEffects: t.result.rawObservations.operationReturns.every(row => row.filesystemCalls.reads === 0 && row.filesystemCalls.writes === 0),
      noPhysicalFile: t.result.rawObservations.checkpoints.every(row => !row.physicalFile.exists),
      originalRawUnchanged: originalStillPresent(t, u, raw), originalRestoredByBranchOnly: original.manager.context.messages.some(message => text(message) === 'memory original'),
      exactImageReplacementSelected: replaced.manager.context.messages.some(message => text(message) === 'replacement image') });
  }
  assert.equal(Date, originals.Date); assert.equal(Date.now, originals.now); assert.equal(Math.random, originals.random);
  assert.equal(crypto.randomUUID, originals.randomUUID); assert.equal(crypto.randomBytes, originals.randomBytes);
  assert.equal(guard.ownedOpenDescriptors(), 0, 'All actual manager file descriptors must have closed');
  // Assert denial by calling the guards, without performing network/process/file effects.
  assert.throws(() => fs.writeFileSync(join(oracle, '.context-edit-forbidden-probe'), 'forbidden'), /outside owned scratch/);
  assert.throws(() => fs.writeFileSync(1, 'forbidden'), /unowned descriptor/);
  assert.throws(() => childProcess.spawnSync(process.execPath, ['--version']), /prohibits network and child-process access/);
  assert.throws(() => globalThis.fetch('https://context-edit.invalid/'), /prohibits network and child-process access/);
  const loadedModules = [...globalThis.pisharpLoadedReferenceModules.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
  return { schemaVersion: 1, kind: 'captured-unchanged-session-manager-context-edits', sourceSha: 'd86654abb8862e201933517d6f1fce9f88dd117f',
    authoredInput: input, comparisonPolicy, cases, loadedModules,
    checks: { wholeSessionManagerLoaded: true, clocksAndRngUnmodified: true, scratchWriteGuard: true, networkAndProcessesDenied: true,
      ownedOpenDescriptors: guard.ownedOpenDescriptors(), filesystemCalls: guard.snapshot(), filesystemWriteAudit: guard.audit() } };
}

export function parseCaptureArguments(args) {
  let first = false, oracle, output;
  const usage = 'Usage: node capture-session-context-edits.mjs --capture-new --oracle APPROVED_SESSION_ORACLE --output ABSOLUTE_FRESH_CAPTURE_DIRECTORY';
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
  const outputRoot = join(repo, 'artifacts/session-context-edits-reference');
  assert(samePath(dirname(output), outputRoot) && inside(outputRoot, output), 'Output must be one fresh direct child of artifacts/session-context-edits-reference');
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
  const environmentPins = { ...qualification.environmentPins, contextEditHarness: ownPin, qualificationLockSha256: qualificationSha256,
    dependencyVerification: 'Every installed file equals its integrity-pinned archive before and after both children; exact eight-package top-level set.' };
  noLinks(outputRoot); fs.mkdirSync(outputRoot, { recursive: true }); noLinks(outputRoot);
  fs.mkdirSync(output); fs.writeFileSync(join(output, 'owner.json'), json({ owner: 'PiSharp-session-context-edits-capture-v1', output, oracle, sourceSha: plan.source.commit, harnessSha256: ownPin.sha256 }), { flag: 'wx' });
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
      assert.equal(child.error, undefined, 'Child timeout/maxBuffer/launch error cannot qualify'); assert.equal(child.signal, null, 'Child must settle without a signal');
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
