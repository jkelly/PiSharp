import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { gzipSync } from 'node:zlib';
import { buildModel, confinedPath, declaredEntries, digest, matchTarget, parseArgs, parseJson, parseTar, pinnedBytes, qualifyArchive } from './build-public-entrypoints.mjs';

// Authored in-memory parser/model fixtures. These are not Pi behavior captures.
const commit = 'd86654abb8862e201933517d6f1fce9f88dd117f';
const aiName = '@earendil-works/pi-ai';
const productName = '@earendil-works/pi-coding-agent';
const clone = value => structuredClone(value);
const sourceFile = (path, value) => {
  const content = Buffer.isBuffer(value) ? value : Buffer.from(typeof value === 'string' ? value : JSON.stringify(value));
  return { path, content, bytes: content.length, sha256: digest(content), gitBlobId: createHash('sha1').update(Buffer.from(`blob ${content.length}\0`)).update(content).digest('hex') };
};

function modelFixture() {
  const canonical = new Map();
  const add = (path, value) => canonical.set(path, sourceFile(path, value));
  const ai = { name: aiName, version: '0.99.1', exports: { '.': { types: './dist/index.d.ts', import: './dist/index.js' }, './utils/*': { import: './dist/utils/*.js' }, './package.json': './package.json' } };
  add('packages/ai/package.json', ai);
  add('packages/ai/src/index.ts', 'export const answer = 42;\n');
  add('packages/ai/src/utils/text.ts', 'export const text = String;\n');
  add('packages/ai/src/utils/nested/raw.ts', 'export const raw = String.raw;\n');
  add('packages/product/package.json', { name: productName, version: '0.99.1', exports: { '.': { import: './dist/index.js' }, './experimental/plugin': { source: './src/experimental/plugin.ts' }, './missing': { source: './src/missing.ts' } }, bin: { pi: 'dist/bundle/cli.js' }, dependencies: { [aiName]: '0.99.1', 'external-sdk': '1.2.3' }, optionalDependencies: { 'optional-addon': '4.5.6' } });
  add('packages/product/src/index.ts', 'export const sdk = true;\n');
  add('packages/product/src/experimental/plugin.ts', 'export interface Plugin { run(): void; }\n');
  add('examples/package.json', { name: 'private-example', private: true, version: '0.99.1' });
  const registry = [aiName, productName].map(name => ({ name, version: '0.99.1', gitHead: commit, archiveAcquired: false, signatureVerified: false }));
  const archive = new Map();
  const archived = (path, bytes) => archive.set(path, sourceFile(path, bytes));
  archived('package.json', canonical.get('packages/ai/package.json').content);
  archived('dist/index.js', 'export const answer = 42;\n');
  archived('dist/index.d.ts', 'export declare const answer = 42;\n');
  archived('dist/utils/text.js', 'export const text = String;\n');
  archived('dist/utils/nested/raw.js', 'export const raw = String.raw;\n');
  return { canonical, registry, archive, expected: { packageManifestCount: 3, publishedPackageCount: 2, acquiredPackage: aiName } };
}

function tarFixture(members) {
  const blocks = [];
  for (const member of members) {
    const content = Buffer.from(member.content ?? '');
    const header = Buffer.alloc(512);
    header.write(member.name, 0, 100, 'utf8');
    header.write('0000644\0', 100, 8, 'ascii');
    header.write('0000000\0', 108, 8, 'ascii');
    header.write('0000000\0', 116, 8, 'ascii');
    header.write(content.length.toString(8).padStart(11, '0') + '\0', 124, 12, 'ascii');
    header.write('00000000000\0', 136, 12, 'ascii');
    header.fill(32, 148, 156);
    header.write(member.type ?? '0', 156, 1, 'ascii');
    header.write('ustar\0', 257, 6, 'ascii');
    header.write('00', 263, 2, 'ascii');
    if (member.prefix) header.write(member.prefix, 345, 155, 'utf8');
    const checksum = header.reduce((sum, byte) => sum + byte, 0);
    header.write(checksum.toString(8).padStart(6, '0') + '\0 ', 148, 8, 'ascii');
    blocks.push(header, content, Buffer.alloc((512 - content.length % 512) % 512));
  }
  return Buffer.concat([...blocks, Buffer.alloc(1024)]);
}

function archiveFixture() {
  const packageContent = Buffer.from(JSON.stringify({ name: aiName, version: '0.99.1' }));
  const tar = tarFixture([{ name: 'package/package.json', content: packageContent }, { name: 'package/dist/index.js', content: 'export const pi = true;\n' }]);
  const bytes = gzipSync(tar);
  const expected = { name: aiName, version: '0.99.1', bytes: bytes.length, regularFileCount: 2, integrity: `sha512-${createHash('sha512').update(bytes).digest('base64')}`, shasum: createHash('sha1').update(bytes).digest('hex') };
  const inspection = { kind: 'official-pi-ai-release-package-inspection', releaseSourceAuthority: { sourceSha: commit }, package: { name: aiName, version: '0.99.1', gitHeadDeclaredByRegistry: commit, bytes: bytes.length, sha256: digest(bytes), integrity: expected.integrity, shasum: expected.shasum, sha512AndSha1Verified: true, observedRegularFiles: 2, registryReportedFiles: 2, packageJsonBytes: packageContent.length, packageJsonSha256: digest(packageContent), url: 'https://fixture.invalid/pi-ai.tgz', registryMetadataSha256: 'a'.repeat(64) }, packagedFiles: [...parseTar(tar).values()].map(({ path, bytes, sha256 }) => ({ path, bytes, sha256 })) };
  const acquisition = { bytes: bytes.length, sha256: digest(bytes), integrity: expected.integrity, shasum: expected.shasum, url: inspection.package.url, metadataSha256: inspection.package.registryMetadataSha256, installed: false, extracted: false, executed: false };
  const receipt = { sourceSha: commit, files: [{ path: 'pi-ai-0.99.1.tgz', bytes: bytes.length, sha256: digest(bytes) }] };
  return { bytes, tar, inspection, acquisition, receipt, expected };
}

test('JSON parser preserves null, nested data and Unicode without duplicate keys', () => {
  assert.deepEqual(parseJson(Buffer.from('{"line":"λ😀","optional":null,"nested":[true,false,{"n":-1.25e2}]}')), { line: 'λ😀', optional: null, nested: [true, false, { n: -125 }] });
});

for (const [name, json] of [
  ['literal duplicate', '{"a":1,"a":2}'],
  ['escaped duplicate', '{"a":1,"\\u0061":2}'],
  ['nested duplicate', '{"safe":[{"x":1,"x":2}]}'],
  ['trailing input', '{} true'],
  ['trailing comma', '{"a":1,}']
]) test(`JSON parser rejects ${name}`, () => assert.throws(() => parseJson(json)));

test('JSON parser rejects invalid UTF-8 and excessive nesting', () => {
  assert.throws(() => parseJson(Buffer.from([34, 0xff, 34])), /invalid UTF-8/);
  assert.throws(() => parseJson('['.repeat(258) + '0' + ']'.repeat(258)), /excessive JSON depth/);
});

test('entry declarations preserve ordered conditions, fallback branches, null and escaped identities', () => {
  const entries = declaredEntries({ name: '@scope/tool', exports: { '.': { default: ['./fallback.js', null], import: './esm.js' }, './feature/*': { source: './src/*.ts', types: './dist/*.d.ts' } }, main: './legacy.js', types: './legacy.d.ts', bin: { 'tool/name': 'dist/cli.js' } }, 'packages/tool/package.json');
  assert.deepEqual(entries.slice(0, 3).map(e => [e.target, e.conditions]), [['./fallback.js', ['default', { fallbackIndex: 0 }]], [null, ['default', { fallbackIndex: 1 }]], ['./esm.js', ['import']]]);
  assert.deepEqual(entries.map(e => e.ordinal), entries.map((_, i) => i));
  assert.equal(entries[3].subpath, './feature/*');
  assert.equal(entries.at(-1).id, 'packages/tool/package.json#/bin/tool~1name');
  assert.equal(entries.filter(e => e.kind === 'main').length, 1);
});

for (const [name, exports] of [
  ['mixed condition/subpath map', { '.': './index.js', import: './esm.js' }],
  ['nested subpath map', { import: { './nested': './nested.js' } }],
  ['numeric condition', { 0: './index.js' }],
  ['invalid export target', '../escape.js'],
  ['non-path export target', 'external-package'],
  ['non-declaration value', 1]
]) test(`entry parser rejects ${name}`, () => assert.throws(() => declaredEntries({ exports }, 'package.json')));

test('wildcard expansion preserves nested substitutions and deterministic file order', () => {
  assert.deepEqual(matchTarget('dist/utils/*.js', ['dist/utils/z.js', 'dist/utils/nested/raw.js', 'dist/api/x.js', 'dist/utils/a.js']), [
    { path: 'dist/utils/a.js', substitution: 'a' },
    { path: 'dist/utils/nested/raw.js', substitution: 'nested/raw' },
    { path: 'dist/utils/z.js', substitution: 'z' }
  ]);
  assert.deepEqual(matchTarget('dist/index.js', ['dist/index.js.map', 'dist/index.js']), [{ path: 'dist/index.js', substitution: null }]);
  assert.deepEqual(matchTarget('dist/absent.js', ['dist/index.js']), []);
  assert.throws(() => matchTarget('dist/*/*.js', []), /owner\/parser review/);
});

test('canonical paths retain literal Unicode and reject filesystem/encoded escapes', () => {
  assert.equal(confinedPath('packages/λ😀/index.ts'), 'packages/λ😀/index.ts');
  for (const path of ['', '/absolute', '../escape', 'a/../b', 'a//b', 'a\\b', 'C:/escape', 'a/%2e%2e/b', 'a%2fb', 'a%5cb', 'a/\0b']) assert.throws(() => confinedPath(path), undefined, path);
});

test('CLI accepts exactly one explicit source path', () => {
  assert.equal(typeof parseArgs(['--source', '.']).source, 'string');
  for (const args of [[], ['--source'], ['--source', '--check'], ['--source', '.', '--source', '.'], ['--output', 'x'], ['--source', '.', '--capture-new']]) assert.throws(() => parseArgs(args), /Usage/);
});

test('model distinguishes declared source, inferred mapping and qualified release presence', () => {
  const f = modelFixture();
  const result = buildModel(f.canonical, f.registry, f.archive, f.expected, productName);
  assert.equal(result.packages.length, 3);
  assert.equal(result.publishedPackageCount, 2);
  assert.equal(result.unacquiredPublishedPackageCount, 1);
  assert.deepEqual(result.declaredProductWorkspaceDependencyClosure, [aiName, productName]);
  const ai = result.packages.find(p => p.metadata.name === aiName);
  const root = ai.entries.find(e => e.target === './dist/index.js');
  assert.equal(root.sourceMapping.basis, 'UnverifiedDistToSrcCandidate');
  assert.equal(root.sourceMapping.state, 'Unverified');
  assert.equal(root.releasedArtifact.state, 'PresentInQualifiedArchive');
  assert.equal(root.releasedArtifact.runtimeExecuted, false);
  const wildcard = ai.entries.find(e => e.subpath === './utils/*');
  assert.deepEqual(wildcard.releasedArtifact.targets.map(t => t.publicSubpath), ['./utils/nested/raw', './utils/text']);
  const metadata = ai.entries.find(e => e.subpath === './package.json');
  assert.equal(metadata.sourceMapping.basis, 'DirectCanonicalTarget');
  assert.equal(metadata.sourceMapping.state, 'DeclaredCanonicalSourcePresent');
  const product = result.packages.find(p => p.metadata.name === productName);
  const experimental = product.entries.find(e => e.subpath === './experimental/plugin');
  assert.equal(experimental.sourceMapping.state, 'DeclaredCanonicalSourcePresent');
  assert.equal(experimental.releasedArtifact.state, 'Unverified');
  assert.equal(product.entries.find(e => e.subpath === './missing').sourceMapping.state, 'Unverified');
  assert.deepEqual(product.metadata.optionalDependencies, { 'optional-addon': '4.5.6' });
  assert.equal(result.packages.find(p => p.metadata.name === 'private-example').publication.scopeDecision, 'Unverified; runtime tracing and owner decision required');
  for (const p of result.packages) for (const e of p.entries) {
    assert.equal(e.implementationAcceptance, 'Deferred');
    assert.equal(e.implementationEvidenceState, 'Unverified');
  }
});

test('missing acquired target is explicit absence, without acceptance credit', () => {
  const f = modelFixture(); f.archive.delete('dist/index.js');
  const result = buildModel(f.canonical, f.registry, f.archive, f.expected, productName);
  const entry = result.packages.find(p => p.metadata.name === aiName).entries.find(e => e.target === './dist/index.js');
  assert.equal(entry.releasedArtifact.state, 'AbsentInQualifiedArchive');
  assert.equal(entry.implementationEvidenceState, 'Unverified');
});

for (const [name, change, expectedError] of [
  // Duplicate the unacquired public identity so archive validation cannot mask the identity check.
  ['duplicate package name', f => f.canonical.set('other/package.json', sourceFile('other/package.json', { name: productName, version: '0.99.1' })), /duplicate package identity/],
  ['duplicate registry name', f => f.registry.push(f.registry[0]), /duplicate registry identity/],
  ['missing registry metadata', f => f.registry.pop(), /metadata missing/],
  ['wrong registry source', f => f.registry[0].gitHead = '0'.repeat(40), /baseline identity/],
  ['wrong manifest census', f => f.expected.packageManifestCount++, /census mismatch/],
  ['missing acquired archive', f => f.archive = null, /released\/canonical package manifest differs/],
  ['changed released manifest', f => f.archive.set('package.json', sourceFile('package.json', '{}')), /released\/canonical package manifest differs/],
  ['changed canonical content', f => f.canonical.get('packages/ai/src/index.ts').content = Buffer.from('tampered'), /content identity/],
  ['changed canonical path identity', f => f.canonical.get('packages/ai/src/index.ts').path = 'other.ts', /content identity/]
]) test(`model rejects ${name}`, () => { const f = modelFixture(); change(f); assert.throws(() => buildModel(f.canonical, f.registry, f.archive, f.expected, productName), expectedError); });

test('pin verification rejects changed content and malformed expected digest', () => {
  const bytes = Buffer.from('canonical');
  assert.equal(pinnedBytes(bytes, digest(bytes), 'fixture'), bytes);
  assert.throws(() => pinnedBytes(Buffer.from('changed'), digest(bytes), 'fixture'), /SHA-256 mismatch/);
  assert.throws(() => pinnedBytes(bytes, 'not-a-pin', 'fixture'), /SHA-256 mismatch/);
});

test('archive parser admits inert regular files with verified byte/hash records', () => {
  const f = archiveFixture(); const files = parseTar(f.tar);
  assert.equal(files.size, 2);
  assert.equal(files.get('package.json').sha256, f.inspection.package.packageJsonSha256);
  const prefixed = parseTar(tarFixture([{ name: 'index.js', prefix: 'package/dist', content: 'x' }]));
  assert.equal(prefixed.get('dist/index.js').content.toString(), 'x');
});

for (const [name, members, error] of [
  ['duplicate member', [{ name: 'package/a' }, { name: 'package/a' }], /duplicate member/],
  ['parent traversal', [{ name: 'package/../escape' }], /escapes/],
  ['absolute member', [{ name: '/absolute' }], /unexpected package root/],
  ['different package root', [{ name: 'other/a' }], /unexpected package root/],
  ['encoded separator', [{ name: 'package/a%2fb' }], /encoded path escape/],
  ['symbolic link', [{ name: 'package/a', type: '2' }], /unsupported member kind/],
  ['hard link', [{ name: 'package/a', type: '1' }], /unsupported member kind/],
  ['extended metadata header', [{ name: 'package/a', type: 'x' }], /unsupported member kind/]
]) test(`archive parser rejects ${name}`, () => assert.throws(() => parseTar(tarFixture(members)), error));

test('archive parser rejects checksum, member padding and end-block tampering', () => {
  const original = tarFixture([{ name: 'package/a', content: 'x' }]);
  const header = Buffer.from(original); header[0] ^= 1;
  assert.throws(() => parseTar(header), /checksum mismatch/);
  const padding = Buffer.from(original); padding[513] = 1;
  assert.throws(() => parseTar(padding), /member padding/);
  const ending = Buffer.from(original); ending[ending.length - 1] = 1;
  assert.throws(() => parseTar(ending), /end blocks/);
  assert.throws(() => parseTar(original.subarray(0, original.length - 512)), /end blocks/);
  assert.throws(() => parseTar(original.subarray(0, 512)), /truncated member/);
});

test('archive qualification cross-checks byte identity, receipt and every packaged member', () => {
  const f = archiveFixture();
  assert.equal(qualifyArchive(f.bytes, f.inspection, f.acquisition, f.receipt, f.expected, commit).size, 2);
});

for (const [name, change, error] of [
  ['compressed bytes', f => { f.bytes = Buffer.from(f.bytes); f.bytes[12] ^= 1; }, /size\/SRI\/SHA-1 mismatch/],
  ['inspection source identity', f => f.inspection.releaseSourceAuthority.sourceSha = '0'.repeat(40), /inspection identity/],
  ['inspection package identity', f => f.inspection.package.name = 'other', /inspection identity/],
  ['acquisition execution flag', f => f.acquisition.executed = true, /acquisition identity/],
  ['acquisition metadata identity', f => f.acquisition.metadataSha256 = '0'.repeat(64), /acquisition identity/],
  ['receipt archive digest', f => f.receipt.files[0].sha256 = '0'.repeat(64), /receipt identity/],
  ['duplicate receipt archive', f => f.receipt.files.push(clone(f.receipt.files[0])), /receipt identity/],
  ['missing packaged record', f => f.inspection.packagedFiles.pop(), /file count mismatch/],
  ['duplicate packaged record', f => f.inspection.packagedFiles[1] = clone(f.inspection.packagedFiles[0]), /duplicate inspection member/],
  ['packaged body digest', f => f.inspection.packagedFiles[1].sha256 = '0'.repeat(64), /file mismatch/],
  ['manifest digest', f => f.inspection.package.packageJsonSha256 = '0'.repeat(64), /package manifest mismatch/]
]) test(`archive qualification rejects ${name}`, () => { const f = archiveFixture(); change(f); assert.throws(() => qualifyArchive(f.bytes, f.inspection, f.acquisition, f.receipt, f.expected, commit), error); });
