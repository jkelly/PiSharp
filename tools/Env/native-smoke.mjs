// Exercises the isolated published daemon using protocol-1 bytes, without loading
// PiSharp assemblies into the test driver or supplying development sidecars.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomBytes, createHash } from 'node:crypto';
import { readFile, mkdir, writeFile, readdir, lstat } from 'node:fs/promises';
import { basename, dirname, join, resolve } from 'node:path';

const options = {};
for (let index = 2; index < process.argv.length; index += 2) {
  const key = process.argv[index];
  if (!['--daemon', '--report', '--rid', '--work'].includes(key) || !process.argv[index + 1]) throw new Error(`Invalid argument ${key}`);
  options[key.slice(2)] = process.argv[index + 1];
}
if (!options.daemon || !options.report || !options.rid || !options.work) throw new Error('--daemon, --report, --rid and --work are required');

const daemon = resolve(options.daemon), work = resolve(options.work), cases = [];
const report = { schemaVersion: 1, kind: 'published-native-daemon-protocol-smoke', rid: options.rid,
  startedUtc: new Date().toISOString(), platform: process.platform, architecture: process.arch, cases };
let connection;
async function test(name, action) {
  const startedUtc = new Date().toISOString();
  try { await action(); cases.push({ name, result: 'passed', startedUtc, finishedUtc: new Date().toISOString() }); console.log(`PASS ${name}`); }
  catch (error) { cases.push({ name, result: 'failed', startedUtc, error: error.stack ?? String(error) }); throw error; }
}
function nativeImage(bytes, rid) {
  const expected = `${process.platform === 'win32' ? 'win' : process.platform === 'darwin' ? 'osx' : 'linux'}-${process.arch}`;
  assert.equal(rid, expected, 'Publish RID must match the test host');
  if (process.platform === 'win32') {
    assert.equal(bytes.toString('ascii', 0, 2), 'MZ');
    const pe = bytes.readUInt32LE(0x3c);
    assert.equal(bytes.toString('ascii', pe, pe + 4), 'PE\0\0');
    assert.equal(bytes.readUInt16LE(pe + 4), process.arch === 'arm64' ? 0xaa64 : 0x8664);
    assert.equal(bytes.readUInt16LE(pe + 24), 0x20b, 'PE32+ native executable');
    // IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR must be empty (no CLR-hosted assembly).
    assert.equal(bytes.readUInt32LE(pe + 24 + 112 + 14 * 8), 0, 'No managed CLR image header');
  } else if (process.platform === 'darwin') {
    assert.equal(bytes.readUInt32LE(0), 0xfeedfacf, '64-bit Mach-O image');
    assert.equal(bytes.readUInt32LE(4), process.arch === 'arm64' ? 0x0100000c : 0x01000007);
  } else {
    assert.deepEqual(bytes.subarray(0, 6), Buffer.from([127, 69, 76, 70, 2, 1]), '64-bit little-endian ELF image');
    assert.equal(bytes.readUInt16LE(18), process.arch === 'arm64' ? 183 : 62);
  }
}
async function distribution() {
  const directory = dirname(daemon), manifestName = 'native-package-manifest.json';
  const manifest = JSON.parse(await readFile(join(directory, manifestName), 'utf8'));
  assert.equal(manifest.kind, 'native-daemon-distribution'); assert.equal(manifest.rid, options.rid);
  assert.equal(manifest.selfHashExcluded, manifestName);
  assert.ok(Array.isArray(manifest.components) && manifest.components.some(c => c.role === 'selected-dotnet-runtime'));
  assert.ok(manifest.components.some(c => c.role === 'selected-nativeaot-compiler-and-runtime'));
  assert.ok(manifest.components.some(c => c.role === 'selected-nativeaot-build-package'));
  const expected = new Set([manifestName]); let executable = 0;
  for (const file of manifest.files) {
    assert.ok(typeof file.path === 'string' && !file.path.includes('\\') && !file.path.includes(':') && !file.path.startsWith('/') && file.path.split('/').every(part => part.length > 0 && part !== '.' && part !== '..'), 'Manifest paths stay inside the package');
    assert.ok(!expected.has(file.path), 'Unique distribution path'); expected.add(file.path);
    assert.ok(['executable', 'license', 'notice', 'package-metadata'].includes(file.role), 'Only executable and documentation roles');
    if (file.role === 'executable') { executable++; assert.equal(file.path, basename(daemon)); }
    else assert.ok(!/\.(dll|exe|so|dylib|a|lib|pdb)$/i.test(file.path), 'No development/native sidecars disguised as documentation');
    const path = join(directory, file.path), stat = await lstat(path);
    assert.ok(stat.isFile() && !stat.isSymbolicLink(), 'Distribution files are ordinary files');
    const bytes = await readFile(path); assert.equal(bytes.length, file.bytes);
    assert.equal(createHash('sha256').update(bytes).digest('hex'), file.sha256, `Distribution integrity: ${file.path}`);
  }
  assert.equal(executable, 1, 'One standalone executable plus its notices');
  for (const required of ['LICENSE', 'THIRD-PARTY-NOTICES.md']) assert.ok(expected.has(required), `Required repository notice: ${required}`);
  async function walk(relative = '') {
    const result = [];
    for (const name of await readdir(join(directory, relative))) {
      const path = relative ? `${relative}/${name}` : name, stat = await lstat(join(directory, path));
      assert.ok(!stat.isSymbolicLink(), 'No symlinks in the distribution');
      if (stat.isDirectory()) result.push(...await walk(path));
      else { assert.ok(stat.isFile(), 'Only ordinary files in package'); result.push(path); }
    }
    return result;
  }
  assert.deepEqual((await walk()).sort(), [...expected].sort(), 'No unmanifested package files');
  report.distribution = { files: manifest.files.length, components: manifest.components.length,
    manifestSha256: createHash('sha256').update(await readFile(join(directory, manifestName))).digest('hex') };
}
function frame(type, id, json, payload = Buffer.alloc(0), rawJson) {
  const body = rawJson ?? Buffer.from(JSON.stringify(json));
  const bytes = Buffer.alloc(13 + body.length + payload.length);
  bytes.writeUInt32BE(bytes.length - 4, 0); bytes[4] = type;
  bytes.writeUInt32BE(id, 5); bytes.writeUInt32BE(body.length, 9);
  body.copy(bytes, 13); payload.copy(bytes, 13 + body.length);
  return bytes;
}
function deferred() { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }
const deadline = (promise, label, ms = 15000) => new Promise((resolve, reject) => {
  const timer = setTimeout(() => reject(new Error(`Timed out: ${label}`)), ms);
  promise.then(resolve, reject).finally(() => clearTimeout(timer));
});
class Connection {
  constructor() {
    this.token = randomBytes(16).toString('hex'); this.prefix = Buffer.from(`PI-ENV ${this.token}\n`);
    this.buffer = Buffer.alloc(0); this.synced = deferred(); this.closed = deferred();
    this.pending = new Map(); this.nextId = 0; this.stderr = ''; this.ready = false;
    const env = { ...process.env, DOTNET_ROOT: join(work, 'absent-dotnet'), DOTNET_MULTILEVEL_LOOKUP: '0' };
    delete env.LD_LIBRARY_PATH; delete env.DYLD_LIBRARY_PATH;
    this.child = spawn(daemon, ['serve', '--token', this.token], { cwd: work, env, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    this.child.stdout.on('data', bytes => { try { this.read(bytes); } catch (error) { this.fail(error); } });
    this.child.stderr.on('data', bytes => { this.stderr = (this.stderr + bytes.toString()).slice(-131072); });
    this.child.stdin.on('error', error => this.fail(error));
    this.child.on('error', error => this.fail(error));
    this.child.on('close', (code, signal) => {
      this.closed.resolve({ code, signal });
      if (!this.ready || this.pending.size || this.buffer.length) this.fail(new Error(`Daemon closed with unfinished framing (${code ?? signal}): ${this.stderr}`));
    });
  }
  fail(error) {
    this.failure ??= error;
    this.synced.reject(error);
    for (const pending of this.pending.values()) pending.reject(error);
    this.pending.clear();
  }
  read(bytes) {
    this.buffer = Buffer.concat([this.buffer, bytes]);
    if (!this.ready) {
      if (this.buffer.length < this.prefix.length) return;
      assert.deepEqual(this.buffer.subarray(0, this.prefix.length), this.prefix, 'Exact authenticated startup line');
      this.buffer = this.buffer.subarray(this.prefix.length); this.ready = true; this.synced.resolve();
    }
    while (this.buffer.length >= 4) {
      const length = this.buffer.readUInt32BE(0);
      assert.ok(length >= 9 && length <= 16 * 1024 * 1024, 'Bounded frame length');
      if (this.buffer.length < 4 + length) return;
      const bytes = this.buffer.subarray(4, 4 + length); this.buffer = this.buffer.subarray(4 + length);
      const type = bytes[0], id = bytes.readUInt32BE(1), jsonLength = bytes.readUInt32BE(5);
      assert.ok(jsonLength <= length - 9, 'JSON stays within frame');
      const json = JSON.parse(bytes.toString('utf8', 9, 9 + jsonLength)), payload = Buffer.from(bytes.subarray(9 + jsonLength));
      assert.ok(json !== null && typeof json === 'object' && !Array.isArray(json), 'Frame JSON is an object');
      if (type === 6) continue;
      const pending = this.pending.get(id); assert.ok(pending, `Frame belongs to active request ${id}`);
      if (type === 4) pending.onEvent?.(json, payload);
      else {
        assert.ok(type === 2 || type === 3, 'Daemon sends only event/result/error frames'); this.pending.delete(id);
        if (type === 3) pending.reject(Object.assign(new Error(json.message ?? 'Remote error'), { code: json.code, fields: json }));
        else pending.resolve({ json, payload });
      }
    }
  }
  request(op, json = {}, payload = Buffer.alloc(0), onEvent, fragmented = false, rawJson) {
    if (this.failure) throw this.failure;
    const id = ++this.nextId, waiting = deferred();
    this.pending.set(id, { ...waiting, onEvent });
    const bytes = frame(1, id, { ...json, op }, payload, rawJson);
    if (fragmented) { this.child.stdin.write(bytes.subarray(0, 1)); this.child.stdin.write(bytes.subarray(1, 7)); this.child.stdin.write(bytes.subarray(7)); }
    else this.child.stdin.write(bytes);
    return { id, done: deadline(waiting.promise, `${op}#${id}`) };
  }
  cancel(id) { this.child.stdin.write(frame(5, id, {})); }
  async close() {
    this.child.stdin.end(); const exit = await deadline(this.closed.promise, 'daemon EOF cleanup');
    assert.equal(exit.code, 0, `Clean daemon exit: ${this.stderr}`);
    assert.equal(this.buffer.length, 0, 'No incomplete frame remains at EOF');
    assert.equal(this.pending.size, 0, 'No request remains at EOF');
    if (this.failure) throw this.failure;
  }
}

try {
  await mkdir(work, { recursive: true });
  await test('native-executable-with-verified-license-bundle', async () => {
    await distribution();
    const bytes = await readFile(daemon); nativeImage(bytes, options.rid);
    report.binary = { name: basename(daemon), bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') };
  });
  connection = new Connection(); await deadline(connection.synced.promise, 'startup sync');
  await test('hello-native-host-and-capabilities', async () => {
    const { json } = await connection.request('hello', { protocol: 1 }).done;
    assert.equal(json.protocol, 1); assert.ok(json.features.includes('process'));
    assert.equal(json.os, process.platform === 'win32' ? 'windows' : process.platform === 'darwin' ? 'macos' : 'linux');
    assert.equal(json.arch, process.arch === 'arm64' ? 'aarch64' : 'x86_64'); assert.equal(json.pid, connection.child.pid);
  });
  await test('fragmented-binary-filesystem-and-native-metadata', async () => {
    const path = join(work, 'binary.dat'), bytes = Buffer.from([0, 255, 13, 10, 128, 97]);
    await connection.request('write', { path, append: false }, bytes, undefined, true).done;
    const opened = (await connection.request('open', { path, noFollow: true }).done).json;
    assert.equal(opened.info.kind, 'file'); assert.equal(opened.info.size, bytes.length);
    assert.deepEqual((await connection.request('pread', { handle: opened.handle, offset: 0, length: bytes.length }).done).payload, bytes);
    assert.equal((await connection.request('fstat', { handle: opened.handle }).done).json.kind, 'file');
    await connection.request('close', { handle: opened.handle }).done;
  });
  await test('line-scan-native-json-serialization', async () => {
    const path = join(work, 'lines.txt');
    await connection.request('write', { path }, Buffer.from('first\n中\nlast\n')).done;
    const { handle } = (await connection.request('open', { path, noFollow: true }).done).json;
    const result = (await connection.request('scanLines', { handle, startLine: 1, endLine: 2 }).done).json;
    assert.deepEqual(Object.keys(result).sort(), ['end', 'firstLineBytes', 'firstLineEnd', 'lastLineStart', 'newlines', 'selectedBytes', 'start'].sort());
    for (const value of Object.values(result)) assert.equal(typeof value, 'number');
    assert.equal(result.newlines, 3); assert.ok(result.selectedBytes > 0);
    await connection.request('close', { handle }).done;
  });
  await test('malformed-json-error-retains-framing', async () => {
    await assert.rejects(connection.request('invalid', {}, Buffer.alloc(0), undefined, false, Buffer.from('{')).done, { code: 'EINVAL' });
    assert.equal((await connection.request('lstat', { path: join(work, 'binary.dat') }).done).json.kind, 'file');
  });
  await test('native-process-pipes-binary-order-and-exit', async () => {
    const started = deferred(), stdout = [], stderr = [], input = Buffer.from([255, 0, 128, 13, 10, 97]);
    const code = "process.stdout.write(Buffer.from([97,255,0,98]));process.stderr.write(Buffer.from([254,99]));const chunks=[];process.stdin.on('data',b=>chunks.push(b));process.stdin.on('end',()=>{process.stdout.write(Buffer.concat(chunks));process.exitCode=9;});";
    const run = connection.request('process', { argv: [process.execPath, '-e', code], cwd: work, inheritEnv: true }, Buffer.alloc(0), (json, bytes) => {
      if (json.kind === 'started') { assert.ok(json.pid > 0); started.resolve(); }
      else if (json.kind === 'output') (json.stream === 'stdout' ? stdout : stderr).push(bytes);
      else assert.fail(`Unexpected process event ${json.kind}`);
    });
    await deadline(Promise.race([started.promise, run.done.then(() => { throw new Error('Process ended before started'); })]), 'process started');
    await connection.request('processWrite', { target: run.id }, input.subarray(0, 2)).done;
    await connection.request('processWrite', { target: run.id }, input.subarray(2)).done;
    await connection.request('processCloseStdin', { target: run.id }).done;
    assert.equal((await run.done).json.exitCode, 9);
    assert.deepEqual(Buffer.concat(stdout), Buffer.concat([Buffer.from([97, 255, 0, 98]), input]));
    assert.deepEqual(Buffer.concat(stderr), Buffer.from([254, 99]));
  });
  await test('watch-ready-change-and-cancel-native-json', async () => {
    const ready = deferred(), changed = deferred();
    const run = connection.request('watch', { mode: 'polling', pollIntervalMs: 50,
      targets: [{ path: work, recursive: false, exclude: { hidden: false, names: [] } }] }, Buffer.alloc(0), json => {
      if (json.kind === 'ready') ready.resolve();
      if (json.kind === 'change' && (json.overflow || json.paths?.some(path => basename(path) === 'watched.txt'))) changed.resolve();
      if (json.kind === 'error') changed.reject(new Error(json.message));
    });
    await deadline(Promise.race([ready.promise, run.done.then(() => { throw new Error('Watch ended before ready'); })]), 'watch ready');
    await connection.request('write', { path: join(work, 'watched.txt') }, Buffer.from('watch-change')).done;
    await deadline(Promise.race([changed.promise, run.done.then(() => { throw new Error('Watch ended before change'); })]), 'watch change');
    connection.cancel(run.id); await run.done;
  });
  if (process.platform === 'win32') await test('windows-pty-explicitly-unsupported', async () => {
    await assert.rejects(connection.request('pty', { cwd: work, cols: 80, rows: 24 }).done, { code: 'ENOTSUP' });
  });
  await test('stdin-eof-clean-daemon-exit', async () => { await connection.close(); connection = undefined; });
  report.result = 'passed';
} catch (error) {
  report.result = 'failed'; report.error = error.stack ?? String(error); process.exitCode = 1;
  console.error(report.error);
} finally {
  if (connection) {
    report.daemonStderr = connection.stderr;
    // Give the daemon EOF first so an assertion failure also cleans up its children.
    connection.child.stdin.end();
    try { await deadline(connection.closed.promise, 'failure cleanup', 3000); }
    catch { connection.child.kill('SIGKILL'); }
  }
  report.finishedUtc = new Date().toISOString();
  await mkdir(dirname(resolve(options.report)), { recursive: true });
  await writeFile(resolve(options.report), `${JSON.stringify(report, null, 2)}\n`);
}
