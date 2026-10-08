import { currentPublicHarnessPins, publicDerivativeVerificationScope } from '../PublicDerivativeIntegrity.mjs';
import { qualifiedReferenceFile, validatedReferenceRoot } from '../PublicReferenceLayout.mjs';
// Builtin-only reference. Default verifies the frozen corpus; --capture-new never replaces accepted files.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, rmSync, unlinkSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { gzipSync, inflateRawSync } from 'node:zlib';
import { authoredInput, observeBuiltins } from './ecmascript-json-boundary-driver.mjs';

const ownPath = fileURLToPath(import.meta.url), repo = resolve(dirname(ownPath), '../..');
const family = 'fixtures/reference/ecmascript-json-boundary';
const inputPath = join(repo, family, 'input.json.gz'), expectedPath = join(repo, family, 'expected.json.gz'), manifestPath = join(repo, family, 'manifest.json');
const originalInputHash = 'a3fcdfbcba4da5f9412027ced81e54ebbbc906a3488cd60a9de60429b6acea52';
const originalExpectedHash = 'a98384918bb7e1abf8ded0c1e2d1448ac186292d32f013a85fd15c5adee27027';
const inputLimit = 10 * 1024 * 1024, expectedLimit = 16 * 1024 * 1024, encodedLimit = 8 * 1024 * 1024;
const lockPath = join(repo, 'tools/ReferenceOracle/ecmascript-json-boundary.lock.json');
const guardPath = join(repo, 'tools/PiReferenceRunner/offline-guard.mjs');
const guardHash = 'ae3741bdce496451bd04afcf8628df6ddc5bc5cfad8af6ee5e52cbc7b065a094';
const runtimePath = 'P:/PiSharp/root/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe';
const runtimeHash = '3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237';
const serialized = value => JSON.stringify(value, null, 2) + '\n';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const fileHash = path => hash(readFileSync(path));
const readJson = path => JSON.parse(readFileSync(path, 'utf8'));
const inside = (root, path) => { const suffix = relative(resolve(root), resolve(path)); return suffix !== '' && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep); };
const same = (left, right) => serialized(left) === serialized(right);
const canonicalGzipHeader = gzipSync(Buffer.alloc(0), { level: 9 }).subarray(0, 10);
const crcTable = Uint32Array.from({ length: 256 }, (_, start) => {
  let value = start; for (let bit = 0; bit < 8; bit++) value = value & 1 ? 0xedb88320 ^ value >>> 1 : value >>> 1; return value >>> 0;
});
function crc32(bytes) { let crc = 0xffffffff; for (const byte of bytes) crc = crcTable[(crc ^ byte) & 255] ^ crc >>> 8; return (crc ^ 0xffffffff) >>> 0; }
function strictGunzip(encoded, maximum) {
  assert(encoded.length >= 18 && encoded.length <= encodedLimit, 'Encoded fixture exceeds single-member bounds');
  assert(encoded.subarray(0, 10).equals(canonicalGzipHeader), 'Only the deterministic fixed gzip header is admitted');
  const length = encoded.readUInt32LE(encoded.length - 4); assert(length <= maximum, 'Declared decoded length exceeds bound');
  const body = encoded.subarray(10, -8);
  const inflated = inflateRawSync(body, { info: true, maxOutputLength: maximum });
  assert.equal(inflated.engine.bytesWritten, body.length, 'Extra member/trailing bytes are not admitted');
  assert.equal(inflated.buffer.length, length, 'Decoded length differs from gzip ISIZE');
  assert.equal(crc32(inflated.buffer), encoded.readUInt32LE(encoded.length - 8), 'Decoded CRC32 differs');
  return inflated.buffer;
}
function stableGzip(decoded, maximum) {
  assert(decoded.length <= maximum); const encoded = gzipSync(decoded, { level: 9 });
  assert(encoded.equals(gzipSync(decoded, { level: 9 })), 'Pinned compression is not byte-stable');
  assert(strictGunzip(encoded, maximum).equals(decoded), 'Compression changed decoded fixture bytes'); return encoded;
}
function readFixture(path, maximum) {
  noLinks(path); const stat = lstatSync(path); assert(stat.isFile() && stat.size <= encodedLimit);
  const encoded = readFileSync(path), decoded = strictGunzip(encoded, maximum);
  return { encoded, decoded, pin: { path: relative(repo, path).replaceAll('\\', '/'), encodedBytes: encoded.length,
    encodedSha256: hash(encoded), decodedBytes: decoded.length, decodedSha256: hash(decoded) } };
}
function gzipControls() {
  const plain = Buffer.from('bounded gzip control'), valid = stableGzip(plain, 128); let rejected = 0;
  const reject = bytes => { assert.throws(() => strictGunzip(bytes, 128)); rejected++; };
  reject(Buffer.concat([valid, Buffer.from('trailing')])); reject(Buffer.concat([valid, valid]));
  const badCrc = Buffer.from(valid); badCrc[badCrc.length - 8] ^= 1; reject(badCrc);
  reject(valid.subarray(0, -1)); const badHeader = Buffer.from(valid); badHeader[3] = 1; reject(badHeader);
  const bomb = gzipSync(Buffer.alloc(4096, 65), { level: 9 }); reject(bomb);
  const forgedBomb = Buffer.from(bomb); forgedBomb.writeUInt32LE(128, forgedBomb.length - 4); reject(forgedBomb);
  const badSize = Buffer.from(valid); badSize.writeUInt32LE(plain.length + 1, badSize.length - 4); reject(badSize);
  assert.equal(rejected, 8); return { validRoundTrip: true, rejectedControls: rejected, trailingAndConcatenatedMembersRejected: true,
    crcAndIsizeChecked: true, declaredAndForgedBombsRejectedWithinOutputCap: true };
}
function historicalSnapshot() {
  const root = join(repo, 'artifacts/ecmascript-json-uncompressed-first-snapshot'); noLinks(root);
  const pins = {
    'input.json': originalInputHash, 'expected.json': originalExpectedHash,
    'manifest.json': '77277907e39c8e5a8d6c381ca242ec7ad158358e3dd12034c72c674840548da9',
    'ecmascript-json-boundary.lock.json': '748fc0caccac67393eab036b7f60145db0e25b8d49115e9311022eb040fa676a',
    'capture-ecmascript-json-boundary.mjs': '58b49fe66403ccc8d0a82e7c4b88caf1172cdb6bf3859af4f360d23be42b309a',
    'ecmascript-json-boundary-driver.mjs': 'f295e12b3f8c10ceb9f67147ac829ea905f3c7187d25023d1252e0baafa41cb5',
    'ecmascript-json-boundary-reference.md': '895b30778c6889fe9c07ac2274c3ca0829121e8aaf859c972b079a736a2a9834'
  };
  for (const [name, expected] of Object.entries(pins)) assert.equal(fileHash(join(root, name)), expected, 'Original raw snapshot was not preserved');
  return { originalRawPins: pins, originalRawCaptureRepeats: 2, originalRawDefaultRepeats: 2,
    nativeFirstGate: 'Root reported457/457 Node-absent; immutable6881c9f9d2d643d13ffd6f72a7665f6e42aa60ae; independent acceptance separate' };
}
function writeReceipts(input, runtime, harnessPins, history, controls) {
  const inputPin = readFixture(inputPath, inputLimit).pin, expectedPin = readFixture(expectedPath, expectedLimit).pin;
  assert.equal(inputPin.decodedSha256, originalInputHash); assert.equal(expectedPin.decodedSha256, originalExpectedHash);
  const encoding = { kind: 'single deterministic gzip member', level: 9, headerHex: canonicalGzipHeader.toString('hex'),
    crc32AndIsizeVerified: true, consumedDeflateBytesVerified: true, encodedMaximumBytes: encodedLimit,
    inputDecodedMaximumBytes: inputLimit, expectedDecodedMaximumBytes: expectedLimit };
  writeFileSync(lockPath, serialized({ schemaVersion: 2, runtime, harnessPins, input: inputPin, expected: expectedPin,
    inputSha256: originalInputHash, expectedSha256: originalExpectedHash, encoding, history, gzipControls: controls,
    observations: { originalRawRepeats: 2, originalRawByteIdentical: true, decodedBytesChangedByPackaging: false,
      builtinOnly: true, noUpstreamSourceQualification: true },
    limits: { childTimeoutMilliseconds: 20000, childMaximumOldSpaceMiB: 256, childOutputBytes: 33554432,
      inputBytes: inputLimit, goldenBytes: expectedLimit, encodedFileBytes: encodedLimit, cases: 20000, caseUtf16Units: 4096 } }));
  writeFileSync(manifestPath, serialized({ schemaVersion: 2, fixtureId: input.fixtureId,
    kind: 'genuine-node-builtin-json-parse-stringify-observations', input: inputPin, expected: expectedPin,
    inputSha256: originalInputHash, expectedSha256: originalExpectedHash, lockSha256: fileHash(lockPath), encoding,
    caseCount: input.cases.length, familyCounts: input.generation.familyCounts, maximumCaseUtf16Units: input.generation.maximumCaseUtf16Units,
    expectedSchema: 'cases[{caseId,json,serialized}], exact observed strings; numericSemantics are observed binary64 sidecars',
    provenance: { inputAlgorithm: input.generation.algorithm, outputs: 'Original genuine two-fresh Node24.19.0 observations preserved byte-for-byte; guarded fresh defaults verify decoded exact strings',
      losslessPackaging: true, originalDecodedSourceBytesRetained: true, gzipControls: controls,
      upstreamImports: false, upstreamQualificationClaimed: false, nativeProjectionOrParityClaimed: false,
      comparator: 'Exact decoded golden bytes and serialized-string equality, without numeric/deep-equality normalization' } }));
}
function noLinks(path) {
  for (let current = resolve(path); ; current = dirname(current)) {
    if (existsSync(current)) assert(!lstatSync(current).isSymbolicLink(), 'Linked/junction paths are outside capture profile');
    if (dirname(current) === current) break;
  }
}
function cleanEnvironment(scratch) {
  const home = join(scratch, 'home');
  return { SystemRoot: 'C:\\WINDOWS', WINDIR: 'C:\\WINDOWS', USERPROFILE: home, HOME: home, APPDATA: home, LOCALAPPDATA: home,
    TEMP: scratch, TMP: scratch, TMPDIR: scratch, TZ: 'UTC', PISHARP_PUBLIC_REFERENCE_ROOT: validatedReferenceRoot() };
}
async function parentCapture(args) {
  assert(args.length === 0 || args.length === 1 && ['--capture-new', '--package-current'].includes(args[0]),
    'Usage: node capture-ecmascript-json-boundary.mjs [--capture-new | --package-current]');
  const first = args[0] === '--capture-new', packageCurrent = args[0] === '--package-current';
  assert.equal(process.version, 'v24.19.0'); assert.equal(fileHash(process.execPath), runtimeHash);
  assert.equal(resolve(process.execPath).toLowerCase(), resolve(qualifiedReferenceFile(runtimePath, runtimeHash)).toLowerCase()); assert.equal(fileHash(guardPath), guardHash);
  const harnessPaths = ['tools/ReferenceOracle/capture-ecmascript-json-boundary.mjs', 'tools/ReferenceOracle/ecmascript-json-boundary-driver.mjs',
    'tools/PiReferenceRunner/offline-guard.mjs'];
  const harnessPins = harnessPaths.map(path => ({ path, bytes: readFileSync(join(repo, path)).length, sha256: fileHash(join(repo, path)) }));
  const runtime = { version: process.version, sha256: runtimeHash, platform: process.platform, arch: process.arch, v8: process.versions.v8 };
  const controls = gzipControls(), inputRaw = serialized(authoredInput()); assert(Buffer.byteLength(inputRaw) <= inputLimit, 'Authored input exceeds 10-MiB cap');
  assert.equal(hash(Buffer.from(inputRaw)), originalInputHash, 'Authored decoded input differs from preserved raw capture');
  if (packageCurrent) {
    const history = historicalSnapshot(); assert(!existsSync(inputPath) && !existsSync(expectedPath), 'Packaging refuses existing encoded artifacts');
    const rawInputPath = join(repo, family, 'input.json'), rawExpectedPath = join(repo, family, 'expected.json');
    noLinks(rawInputPath); noLinks(rawExpectedPath);
    const sourceInput = readFileSync(rawInputPath), sourceExpected = readFileSync(rawExpectedPath);
    assert.equal(hash(sourceInput), originalInputHash); assert.equal(hash(sourceExpected), originalExpectedHash);
    writeFileSync(inputPath, stableGzip(sourceInput, inputLimit), { flag: 'wx' });
    writeFileSync(expectedPath, stableGzip(sourceExpected, expectedLimit), { flag: 'wx' });
    assert(readFixture(inputPath, inputLimit).decoded.equals(sourceInput)); assert(readFixture(expectedPath, expectedLimit).decoded.equals(sourceExpected));
    writeReceipts(JSON.parse(inputRaw), runtime, harnessPins, history, controls);
    // Only our exact known new raw fixture files are removed, after byte-verified historical and encoded copies exist.
    assert.equal(fileHash(rawInputPath), originalInputHash); assert.equal(fileHash(rawExpectedPath), originalExpectedHash);
    assert(inside(join(repo, family), rawInputPath) && inside(join(repo, family), rawExpectedPath));
    unlinkSync(rawInputPath); unlinkSync(rawExpectedPath); historicalSnapshot();
    console.log(serialized({ packagedLosslessly: true, caseCount: JSON.parse(inputRaw).cases.length,
      input: readFixture(inputPath, inputLimit).pin, expected: readFixture(expectedPath, expectedLimit).pin,
      lockSha256: fileHash(lockPath), gzipControls: controls, decodedBytesChanged: false })); return;
  }
  if (first) {
    assert(![inputPath, expectedPath, manifestPath, lockPath].some(existsSync), 'Initial capture refuses every existing input/golden/manifest/lock');
    assert(!existsSync(join(repo, family, 'input.json')) && !existsSync(join(repo, family, 'expected.json')), 'Existing raw capture needs explicit lossless packaging');
    noLinks(join(repo, family)); mkdirSync(join(repo, family), { recursive: true }); writeFileSync(inputPath, stableGzip(Buffer.from(inputRaw), inputLimit), { flag: 'wx' });
  } else {
    const lock = readJson(lockPath), manifest = readJson(manifestPath);
    assert(same(readFixture(inputPath, inputLimit).pin, manifest.input)); assert(same(readFixture(expectedPath, expectedLimit).pin, manifest.expected));
    assert.equal(fileHash(lockPath), manifest.lockSha256); assert(same(runtime, lock.runtime)); assert(same(harnessPins, currentPublicHarnessPins(lock.harnessPins)));
    assert.equal(readFixture(inputPath, inputLimit).decoded.toString('utf8'), inputRaw, 'Frozen authored generation differs');
  }
  const scratchRoot = join(repo, 'artifacts/ecmascript-json-boundary-scratch'); noLinks(scratchRoot); mkdirSync(scratchRoot, { recursive: true });
  const captures = [];
  for (let repeat = 0; repeat < 2; repeat++) {
    const scratch = mkdtempSync(join(scratchRoot, 'capture-')); noLinks(scratch);
    try {
      mkdirSync(join(scratch, 'home')); mkdirSync(join(scratch, 'workspace'));
      const child = spawnSync(process.execPath, ['--max-old-space-size=256', '--import', pathToFileURL(guardPath).href, ownPath, '--child'],
        { cwd: join(scratch, 'workspace'), env: cleanEnvironment(scratch), windowsHide: true, encoding: 'utf8', timeout: 20000,
          maxBuffer: 32 * 1024 * 1024 });
      assert.equal(child.status, 0, 'Builtin observation child failed; no expected substitute: ' + (child.error?.message ?? child.stderr));
      assert(Buffer.byteLength(child.stdout) <= 16 * 1024 * 1024, 'Observed golden exceeds 16-MiB cap');
      captures.push(child.stdout);
    } finally { assert(inside(scratchRoot, scratch)); noLinks(scratch); rmSync(scratch, { recursive: true, force: true }); }
  }
  assert.equal(captures[0], captures[1], 'Two fresh builtin captures differ');
  assert.equal(fileHash(process.execPath), runtimeHash); assert.equal(fileHash(guardPath), guardHash);
  assert.equal(readFixture(inputPath, inputLimit).decoded.toString('utf8'), inputRaw, 'Source input changed during capture');
  const afterHarness = harnessPaths.map(path => ({ path, bytes: readFileSync(join(repo, path)).length, sha256: fileHash(join(repo, path)) }));
  assert(same(harnessPins, afterHarness), 'Frozen harness source changed during capture');
  assert.equal(hash(Buffer.from(captures[0])), originalExpectedHash, 'Fresh observed decoded strings differ from original raw source capture');
  const expected = JSON.parse(captures[0]), input = JSON.parse(inputRaw);
  assert.equal(expected.cases.length, input.cases.length);
  // Exact strings are authoritative. Never parse serialized results or use numeric/deep-equality normalization.
  for (let index = 0; index < input.cases.length; index++) {
    assert.equal(expected.cases[index].caseId, input.cases[index].caseId); assert.equal(expected.cases[index].json, input.cases[index].json);
    assert.equal(typeof expected.cases[index].serialized, 'string');
  }
  if (first) {
    writeFileSync(expectedPath, stableGzip(Buffer.from(captures[0]), expectedLimit), { flag: 'wx' });
    writeReceipts(input, runtime, harnessPins, { newlyCreatedCompressedCapture: true }, controls);
  } else assert.equal(readFixture(expectedPath, expectedLimit).decoded.toString('utf8'), captures[0], 'Fresh builtin observations differ from frozen exact-string golden');
  console.log(serialized({ publicDerivativeVerification: publicDerivativeVerificationScope, schemaVersion: 1, capturedInitialGolden: first, repeatRuns: 2, byteIdentical: true, frozenExactStringsMatched: true,
    caseCount: input.cases.length, scalarNumberCount: expected.numericSemantics.length, familyCounts: input.generation.familyCounts,
    maximumCaseUtf16Units: input.generation.maximumCaseUtf16Units, inputBytes: Buffer.byteLength(inputRaw), goldenBytes: Buffer.byteLength(captures[0]),
    inputSha256: originalInputHash, expectedSha256: originalExpectedHash,
    inputEncodedSha256: fileHash(inputPath), expectedEncodedSha256: fileHash(expectedPath), lockSha256: fileHash(lockPath), gzipControls: controls, builtinOnly: true,
    sourceBytesChanged: false, upstreamOrNativeParityClaimed: false }));
}
if (process.argv[2] === '--child') {
  assert.equal(process.argv.length, 3);
  assert(new URL(import.meta.url).protocol === 'file:');
  const input = readFixture(inputPath, inputLimit); assert.equal(input.pin.decodedSha256, originalInputHash);
  console.log(serialized(observeBuiltins(JSON.parse(input.decoded.toString('utf8')))));
} else if (process.argv[1] && resolve(process.argv[1]).toLowerCase() === ownPath.toLowerCase()) await parentCapture(process.argv.slice(2));
