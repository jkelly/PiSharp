// Offline assembly only. Importing this module performs no setup or subprocess.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const planSha256 = '731aa2cee6eda951b7d6f3d9fb0a4cdad3472389787e65d50c8e46b09827bcfd';
export const receiptName = '.pisharp-semantic-api-oracle.json';
export const hash = (bytes, algorithm = 'sha256', encoding = 'hex') => createHash(algorithm).update(bytes).digest(encoding);
export const jsonBytes = value => Buffer.from(JSON.stringify(value, null, 2) + '\n');
export const canonical = value => Array.isArray(value) ? '[' + value.map(canonical).join(',') + ']' : value && typeof value === 'object' ? '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + canonical(value[key])).join(',') + '}' : JSON.stringify(value);
export const order = (a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0;
export function confined(root, target, allowRoot = false) {
  const suffix = relative(resolve(root), resolve(target));
  assert((allowRoot || suffix !== '') && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep), 'Path escapes owned root');
  return resolve(target);
}
export function noLinks(target) {
  let current = resolve(target);
  for (;;) { let stat; try { stat = lstatSync(current); } catch (error) { if (error.code !== 'ENOENT') throw error; }
    assert(!stat?.isSymbolicLink(), 'Filesystem link/junction rejected'); const parent = dirname(current); if (parent === current) break; current = parent;
  }
}
export function regular(target, max = 33554432) { noLinks(target); const stat = lstatSync(target); assert(stat.isFile() && stat.size <= max, 'Regular-file size/type admission failed'); return readFileSync(target); }
export function writeNew(root, target, bytes) { confined(root, target); noLinks(target); assert(!existsSync(target), 'Existing evidence preserved'); mkdirSync(dirname(target), { recursive: true }); writeFileSync(target, bytes, { flag: 'wx' }); }
export function readPlan() { const bytes = regular(join(repo, 'compatibility/semantic-api.plan.json')); assert.equal(hash(bytes), planSha256, 'Reviewed setup plan changed'); return JSON.parse(bytes.toString('utf8')); }
export function checkRoot(root, plan) { assert.equal(resolve(root).toLowerCase(), resolve(plan.oracle).toLowerCase(), 'Only exact approved oracle root admitted'); noLinks(root); }
export function parseSetupArgs(args, plan) {
  let mode = '--check', root = plan.oracle; const seen = new Set();
  for (let i = 0; i < args.length; i++) { const arg = args[i]; assert(!seen.has(arg), 'Duplicate option'); seen.add(arg);
    if (arg === '--check' || arg === '--prepare') { assert(!seen.has(arg === '--check' ? '--prepare' : '--check'), 'Conflicting mode'); mode = arg; }
    else if (arg === '--oracle') { assert(args[i + 1] && !args[i + 1].startsWith('--'), 'Missing oracle path'); root = args[++i]; }
    else assert.fail('Unknown setup argument');
  }
  checkRoot(root, plan); return { mode, root: resolve(root) };
}
export function environment(root, plan) { return { SystemRoot: 'C:\\WINDOWS', WINDIR: 'C:\\WINDOWS', PATH: dirname(plan.runtime.path), HOME: join(root, 'home'), USERPROFILE: join(root, 'home'), APPDATA: join(root, 'home'), LOCALAPPDATA: join(root, 'home'), TMP: join(root, 'temp'), TEMP: join(root, 'temp'), TZ: 'UTC', LANG: 'C', LC_ALL: 'C', GIT_OPTIONAL_LOCKS: '0', GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: join(root, 'config/gitconfig') }; }
export function runtimeCheck(plan) { assert.equal(process.version, plan.runtime.version); assert.equal(resolve(process.execPath).toLowerCase(), resolve(plan.runtime.path).toLowerCase()); assert.equal(hash(regular(process.execPath, 134217728)), plan.runtime.sha256); }
export function inventory(root) { noLinks(root); const rows = []; const visit = directory => { for (const name of readdirSync(directory).sort()) { const target = confined(root, join(directory, name)); const stat = lstatSync(target); assert(!stat.isSymbolicLink(), 'Tree contains a link'); if (stat.isDirectory()) visit(target); else { const bytes = regular(target); rows.push({ path: relative(root, target).split(sep).join('/'), bytes: bytes.length, sha256: hash(bytes) }); } } }; visit(root); return rows.sort(order); }
function gitRead(plan, args, root) {
  assert.equal(hash(regular(plan.git.path)), plan.git.sha256); noLinks(plan.source.root);
  const result = spawnSync(plan.git.path, ['-c', `safe.directory=${plan.source.root}`, '-c', 'core.autocrlf=false', '-c', 'core.fsmonitor=false', '-c', 'core.untrackedCache=false', '-c', `core.hooksPath=${join(root, 'config/hooks')}`, '-c', 'gc.auto=0', '-c', 'maintenance.auto=false', '-C', plan.source.root, ...args], { cwd: plan.source.root, env: environment(root, plan), windowsHide: true, timeout: 30000, maxBuffer: 33554432, encoding: null });
  assert.equal(result.status, 0, 'Pinned read-only Git query failed'); assert(Buffer.isBuffer(result.stdout)); return result.stdout;
}
function sourceInputs(plan, root) {
  assert.equal(gitRead(plan, ['rev-parse', 'HEAD'], root).toString().trim(), plan.source.commit);
  assert.equal(gitRead(plan, ['status', '--porcelain=v1', '--untracked-files=all'], root).length, 0, 'Qualified source is dirty');
  const records = gitRead(plan, ['ls-tree', '-r', '-z', 'HEAD'], root).toString().split('\0').filter(Boolean); const canonicalRows = [], copiedRows = [], conversions = [], contents = new Map(); let total = 0;
  for (const record of records) {
    const match = /^([0-7]+) blob ([0-9a-f]{40})\t(.+)$/.exec(record); assert(match && ['100644', '100755'].includes(match[1]), 'Unsupported source tree entry');
    const bytes = regular(confined(plan.source.root, join(plan.source.root, match[3]))); let original = bytes;
    const blobHash = value => hash(Buffer.concat([Buffer.from(`blob ${value.length}\0`), value]), 'sha1');
    if (blobHash(bytes) !== match[2]) { assert(plan.source.attributeConversions.includes(match[3]), 'Unapproved source byte conversion'); original = gitRead(plan, ['cat-file', 'blob', match[2]], root); assert(!original.includes(Buffer.from('\r\n')) && Buffer.from(original.toString('utf8').replaceAll('\n', '\r\n')).equals(bytes), 'Attribute conversion differs'); conversions.push({ path: match[3], canonicalSha256: hash(original), checkoutSha256: hash(bytes) }); }
    assert.equal(blobHash(original), match[2]); total += bytes.length; assert(total <= plan.limits.sourceTotalBytes, 'Source size limit');
    canonicalRows.push({ path: match[3], mode: match[1], gitBlob: match[2], bytes: original.length, sha256: hash(original) }); copiedRows.push({ path: match[3], bytes: bytes.length, sha256: hash(bytes) }); contents.set(match[3], bytes);
  }
  assert.equal(canonicalRows.length, plan.source.canonicalFiles); assert.equal(hash(Buffer.from(canonical(canonicalRows))), plan.source.canonicalFingerprint); assert.deepEqual(conversions.map(row => row.path).sort(), [...plan.source.attributeConversions].sort());
  return { canonicalRows, copiedRows: copiedRows.sort(order), conversions, contents };
}
async function archiveInputs(plan) {
  assert.equal(hash(regular(join(repo, plan.admission.path))), plan.admission.sha256); assert.equal(hash(regular(join(repo, plan.admission.inspector))), plan.admission.inspectorSha256); assert.equal(hash(regular(join(repo, plan.admission.inspectorDependency))), plan.admission.inspectorDependencySha256);
  const admission = JSON.parse(regular(join(repo, plan.admission.path)).toString());
  const { inspectArchive } = await import(pathToFileURL(join(repo, plan.admission.inspector)).href); const rows = [];
  for (const pin of plan.packages) { const bytes = regular(confined(plan.admission.archiveRoot, join(plan.admission.archiveRoot, pin.archive))); assert.equal(bytes.length, pin.bytes); assert.equal(hash(bytes), pin.sha256); assert.equal('sha512-' + hash(bytes, 'sha512', 'base64'), pin.integrity);
    const parsed = inspectArchive(bytes); const admitted = admission.packages.find(row => row.name === pin.name); assert(admitted && admitted.version === pin.version); assert.equal(parsed.files.size, pin.files);
    const files = [...parsed.files].map(([path, value]) => ({ path, bytes: value.length, sha256: hash(value) })).sort(order); assert.deepEqual(files, admitted.archive.files.map(({ path, bytes, sha256 }) => ({ path, bytes, sha256 })).sort(order));
    const manifest = JSON.parse(parsed.files.get('package.json').toString()); assert.equal(manifest.name, pin.name); assert.equal(manifest.version, pin.version); assert.equal(manifest.license, 'Apache-2.0'); assert(!manifest.scripts && !manifest.dependencies, 'Unexpected lifecycle/runtime dependency'); assert(parsed.files.has('LICENSE') && parsed.files.has('NOTICE.txt')); rows.push({ pin, files, parsed, notices: admitted.packagedNotices });
  }
  assert.equal(rows.reduce((count, row) => count + row.files.length, 0), 529); return rows;
}
export function immutableFiles(root, receipt) {
  const actual = [...inventory(join(root, 'node_modules')).map(row => ({ ...row, path: 'node_modules/' + row.path })), ...inventory(join(root, 'upstream')).map(row => ({ ...row, path: 'upstream/' + row.path })), { path: 'package.json', bytes: regular(join(root, 'package.json')).length, sha256: hash(regular(join(root, 'package.json'))) }].sort(order);
  assert.deepEqual(actual, receipt.files, 'Oracle/source regular-file tree changed'); return actual;
}
export function verifyPrepared(root, plan = readPlan()) {
  checkRoot(root, plan); runtimeCheck(plan); const receipt = JSON.parse(regular(join(root, receiptName)).toString());
  assert.equal(receipt.owner, 'PiSharp-typescript7-semantic-offline-v1'); assert.equal(receipt.planSha256, hash(regular(join(repo, 'compatibility/semantic-api.plan.json')))); assert.equal(receipt.setupSha256, hash(regular(fileURLToPath(import.meta.url)))); assert.equal(receipt.sourceCommit, plan.source.commit); assert.equal(receipt.nativeExecution, false);
  assert.equal(receipt.canonicalRows.length, plan.source.canonicalFiles); assert.equal(hash(Buffer.from(canonical(receipt.canonicalRows))), plan.source.canonicalFingerprint, 'Canonical source evidence changed');
  assert.equal(hash(regular(join(repo, plan.admission.path))), plan.admission.sha256); const admission = JSON.parse(regular(join(repo, plan.admission.path)).toString()); const expected = [];
  for (const pin of plan.packages) { const row = admission.packages.find(row => row.name === pin.name); assert(row && row.version === pin.version && row.archive.sha256 === pin.sha256); for (const file of row.archive.files) expected.push({ path: 'node_modules/' + pin.name + '/' + file.path, bytes: file.bytes, sha256: file.sha256 }); }
  const conversions = [];
  for (const row of receipt.canonicalRows) { const bytes = regular(confined(root, join(root, 'upstream', row.path))); let original = bytes;
    if (plan.source.attributeConversions.includes(row.path)) { original = Buffer.from(bytes.toString('utf8').replaceAll('\r\n', '\n')); assert(Buffer.from(original.toString('utf8').replaceAll('\n', '\r\n')).equals(bytes), 'Source CRLF conversion changed'); conversions.push({ path: row.path, canonicalSha256: hash(original), checkoutSha256: hash(bytes) }); }
    assert.equal(original.length, row.bytes); assert.equal(hash(original), row.sha256); assert.equal(hash(Buffer.concat([Buffer.from(`blob ${original.length}\0`), original]), 'sha1'), row.gitBlob); expected.push({ path: 'upstream/' + row.path, bytes: bytes.length, sha256: hash(bytes) });
  }
  assert.deepEqual(conversions, receipt.attributeConversions); const packageBytes = jsonBytes({ name: 'pisharp-typescript7-semantic-oracle', private: true, type: 'module', dependencies: Object.fromEntries(plan.packages.map(pin => [pin.name, pin.version])) }); expected.push({ path: 'package.json', bytes: packageBytes.length, sha256: hash(packageBytes) }); assert.deepEqual(receipt.files, expected.sort(order), 'Receipt is not the exact admitted source/package tree'); immutableFiles(root, receipt);
  const exe = regular(confined(root, join(root, plan.native.path))); assert.equal(exe.length, plan.native.bytes); assert.equal(hash(exe), plan.native.sha256); return { receipt, receiptSha256: hash(regular(join(root, receiptName))) };
}
export async function main(args = process.argv.slice(2)) {
  const plan = readPlan(), { mode, root } = parseSetupArgs(args, plan); runtimeCheck(plan); assert.equal(process.execArgv.length, 0, 'Setup rejects preload/options');
  if (mode === '--check' && existsSync(root)) { const result = verifyPrepared(root, plan); console.log(JSON.stringify({ status: 'prepared oracle verified read-only', packages: 2, packageFiles: 529, sourceFiles: 2093, ...result, nativeExecuted: false })); return; }
  assert(!existsSync(root), 'Existing/uncertain oracle preserved'); const archives = await archiveInputs(plan), source = sourceInputs(plan, root);
  if (mode === '--check') { console.log(JSON.stringify({ status: 'read-only preflight; no extraction or native execution', packages: 2, packageFiles: 529, sourceFiles: source.copiedRows.length })); return; }
  assert(!existsSync(root)); mkdirSync(root); for (const directory of ['home', 'temp', 'config', 'artifacts']) mkdirSync(join(root, directory)); writeNew(root, join(root, 'config/gitconfig'), Buffer.alloc(0));
  const files = [];
  for (const { pin, parsed } of archives) for (const [path, bytes] of parsed.files) { const targetPath = 'node_modules/' + pin.name + '/' + path; writeNew(root, join(root, targetPath), bytes); files.push({ path: targetPath, bytes: bytes.length, sha256: hash(bytes) }); }
  for (const [path, bytes] of source.contents) { writeNew(root, join(root, 'upstream', path), bytes); files.push({ path: 'upstream/' + path, bytes: bytes.length, sha256: hash(bytes) }); }
  const packageBytes = jsonBytes({ name: 'pisharp-typescript7-semantic-oracle', private: true, type: 'module', dependencies: Object.fromEntries(plan.packages.map(pin => [pin.name, pin.version])) }); writeNew(root, join(root, 'package.json'), packageBytes); files.push({ path: 'package.json', bytes: packageBytes.length, sha256: hash(packageBytes) });
  const receipt = { schemaVersion: 1, owner: 'PiSharp-typescript7-semantic-offline-v1', planSha256: hash(regular(join(repo, 'compatibility/semantic-api.plan.json'))), setupSha256: hash(regular(fileURLToPath(import.meta.url))), sourceCommit: plan.source.commit, canonicalRows: source.canonicalRows, canonicalFingerprint: plan.source.canonicalFingerprint, attributeConversions: source.conversions, files: files.sort(order), packages: archives.map(({ pin, files, notices }) => ({ ...pin, files, notices })), network: false, npmInvoked: false, lifecycleScripts: false, binLinks: false, nativeExecution: false, upstreamModulesExecuted: false };
  immutableFiles(root, receipt); const sourceAfter = sourceInputs(plan, root); assert.deepEqual(sourceAfter.copiedRows, source.copiedRows, 'Original source changed during copy'); writeNew(root, join(root, receiptName), jsonBytes(receipt)); const checked = verifyPrepared(root, plan); console.log(JSON.stringify({ status: 'offline exclusive assembly complete; native not executed', packages: 2, packageFiles: 529, sourceFiles: 2093, receiptSha256: checked.receiptSha256 }));
}
if (process.argv[1] && resolve(process.argv[1]).toLowerCase() === fileURLToPath(import.meta.url).toLowerCase()) main().catch(error => { console.error(error.stack); process.exitCode = 1; });
