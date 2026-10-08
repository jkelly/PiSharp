// Offline byte/pointer validation and deliberate evidence-classification mutations.
// No network, SDK/source module execution, install, extraction or subprocess call.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { dirname, isAbsolute, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gunzipSync } from 'node:zlib';
import { parseJsonSupported } from './raw-json.mjs';

const own = fileURLToPath(import.meta.url), repo = resolve(dirname(own), '../..');
const matrixPath = resolve(repo, 'compatibility/license-evidence-matrix.json');
const matrixSha256 = '902c56840a593c7ebbfba144e9b0315a4e33aacc20114c71d8994ad8fb90b399';
const hash = (bytes, algorithm = 'sha256') => createHash(algorithm).update(bytes).digest('hex');
const same = (a, b) => assert.deepEqual(a, b);
function pointer(root, path) { return path.split('/').slice(1).reduce((value, part) => { const key = part.replaceAll('~1', '/').replaceAll('~0', '~'); assert(value !== null && Object.hasOwn(value, key), 'Missing evidence JSON pointer: ' + path); return value[key]; }, root); }
function textFamily(text) { return /^BSD 3-Clause License/.test(text) ? 'BSD-3-Clause' : /Apache License/.test(text) ? 'Apache-2.0' : /free and unencumbered software released into the public domain/.test(text) ? 'Unlicense' : /^ISC License|The ISC License/.test(text) ? 'ISC' : /Permission is hereby granted, free of charge/.test(text) ? 'MIT' : 'unclassified-text'; }
function verifyText(row) { const bytes = Buffer.from(row.utf8Text, 'utf8'); assert.equal(bytes.length, row.bytes); assert.equal(hash(bytes), row.sha256); return bytes; }
function gitBlob(bytes) { return hash(Buffer.concat([Buffer.from('blob ' + bytes.length + '\0'), bytes]), 'sha1'); }
export function validateLicenseMatrix(matrix, evidence) {
  assert.equal(matrix.schemaVersion, 1); assert.equal(matrix.kind, 'P1-02-artifact-and-license-evidence-matrix');
  assert.equal(matrix.sourceSha, 'd86654abb8862e201933517d6f1fce9f88dd117f');
  assert.equal(matrix.gate.status, 'HOLD'); assert.equal(matrix.gate.legalApproval, false); assert.equal(matrix.gate.redistributionLicenseClosure, false); assert.equal(matrix.gate.allEightPhaseClosureClaimed, false);
  assert.equal(new Set(matrix.evidenceFiles.map(row => row.id)).size, matrix.evidenceFiles.length);
  const json = id => parseJsonSupported(evidence.get(id).toString('utf8'));
  const baseline = json('baseline'), release = json('release-inspection'), ai = json('ai-inspection');
  const pi = matrix.sourceBaseline.license, sourceBytes = verifyText(pi), oldPi = baseline.artifacts.find(row => row.path === 'LICENSE');
  assert.equal(pi.declaredLicense, 'MIT'); assert.equal(textFamily(pi.utf8Text), 'MIT'); assert.equal(pi.bytes, 1069); assert.equal(pi.sha256, '0457f5bcec3b3b211605dfb5d1a49042fd638f3686a410fe099c24a25af13c48');
  assert.equal(gitBlob(sourceBytes), pi.gitBlob); assert.equal(pi.gitBlob, oldPi.gitBlob); assert.equal(pi.sha256, oldPi.sha256); assert.equal(pi.sha256, release.sourceLicense.sha256);
  assert.equal(new Set(matrix.packages.map(row => row.id)).size, matrix.packages.length); assert.equal(matrix.packages.length, 21);
  for (const row of matrix.packages) {
    assert.equal(row.id, row.name + '@' + row.version); assert.equal(row.developmentOnly, true); assert.equal(row.nativeRuntimeDependency, false); assert.equal(row.redistributionLicenseClosure, false); assert.equal(row.signatureVerified, false); assert.equal(row.attestationVerified, false);
    const rootPresent = row.packagedNotices.some(notice => !notice.path.includes('/'));
    assert.equal(row.packagedRootNoticePresent, rootPresent); assert.equal(row.evidenceLevel, rootPresent ? 'packaged-notice-bytes' : 'declared-metadata-only');
    for (const profile of row.profileEvidence) {
      const locked = pointer(json(profile.plan.fileId), profile.plan.jsonPointer), restored = pointer(json(profile.receipt.fileId), profile.receipt.jsonPointer);
      assert.equal(locked.version, row.version); assert.equal(locked.license, row.declaredLicense); assert.equal(locked.resolved, row.officialArchiveUrl); assert.equal(locked.integrity, row.archiveIntegrity);
      assert.equal(restored.name, row.name); assert.equal(restored.version, row.version); assert.equal(restored.archiveSha256, row.archiveSha256); assert.equal(restored.manifestSha256, row.manifestSha256); same(restored.files, row.installedFingerprint);
      same(restored.licenses.map(({ path, bytes, sha256 }) => ({ path, bytes, sha256 })), row.packagedNotices.map(({ path, bytes, sha256 }) => ({ path, bytes, sha256 })));
    }
    for (const notice of row.packagedNotices) { const recorded = pointer(json(notice.evidence.fileId), notice.evidence.jsonPointer); verifyText(recorded); assert.equal(recorded.path, notice.path); assert.equal(recorded.bytes, notice.bytes); assert.equal(recorded.sha256, notice.sha256); assert.equal(textFamily(recorded.utf8Text), notice.observedTextFamily); assert.equal(notice.packaged, true); }
  }
  const profileExpectations = { edit: [13, 1997], 'anthropic-sdk': [8, 2393], 'responses-sdk': [3, 4942] };
  assert.equal(matrix.dependencyProfiles.length, 3);
  for (const profile of matrix.dependencyProfiles) {
    const expected = profileExpectations[profile.profileId]; assert(expected); same([profile.packages, profile.installedFiles], expected);
    const receipt = json(profile.receiptEvidenceId), restored = profile.profileId === 'responses-sdk' ? receipt.restoration : receipt;
    same(profile.packageIds, restored.installed.map(row => row.name + '@' + row.version)); assert.equal(profile.historicalReceiptStatus, restored.status); same(profile.sourceFingerprint, restored.sourceFingerprint);
    for (const id of profile.packageIds) assert(matrix.packages.some(row => row.id === id && row.profileEvidence.some(p => p.profileId === profile.profileId)));
  }
  const reads = matrix.standardWebhooksSourceEvidence.reads; assert.equal(reads.length, 6);
  for (const row of reads) { assert.equal(row.status, 200); verifyText({ ...row, utf8Text: row.rawUtf8 }); }
  const metadata = parseJsonSupported(reads[0].rawUtf8), tree = parseJsonSupported(reads[4].rawUtf8), standard = matrix.packages.find(row => row.name === 'standardwebhooks');
  assert.equal(metadata.name, 'standardwebhooks'); assert.equal(metadata.version, '1.1.1'); assert.equal(metadata.gitHead, 'b4d2c14fc5b4ccff3ff271e3b087dff812254c59'); assert.equal(metadata.license, 'MIT'); assert.equal(metadata.dist.fileCount, 8); assert.equal(metadata.dist.integrity, standard.archiveIntegrity); assert.equal(tree.truncated, false);
  assert.equal(standard.packagedRootNoticePresent, false); same(standard.packagedNotices, []); assert.equal(standard.evidenceLevel, 'declared-metadata-only'); assert.equal(standard.sourceAuthority.legalScopeApproved, false);
  for (const [path, index] of [['LICENSE', 1], ['libraries/LICENSE', 5], ['libraries/javascript/package.json', 2], ['libraries/javascript/README.md', 3]]) { const member = tree.tree.find(row => row.path === path), bytes = Buffer.from(reads[index].rawUtf8); assert.equal(member.type, 'blob'); assert.equal(member.size, bytes.length); assert.equal(member.sha, gitBlob(bytes)); }
  assert.equal(reads[2].sha256, standard.manifestSha256); assert.equal(parseJsonSupported(reads[2].rawUtf8).license, 'MIT'); assert.equal(textFamily(reads[1].rawUtf8), 'Apache-2.0'); assert.equal(textFamily(reads[5].rawUtf8), 'MIT');
  assert.equal(reads[5].bytes, 1088); assert.equal(reads[5].sha256, '5ec8c7b26b64d881a6706617bed25c049f97f2f35de034c756de8546fd6dbe27'); assert.equal(standard.sourceAuthority.libraryLicenseSha256, reads[5].sha256);
  assert.equal(matrix.releasedPackages.length, 13); assert.equal(matrix.releasedPackages.filter(row => row.archiveAcquired).length, 1);
  const released = json('release-metadata').metadata.filter(row => row.url.includes('registry.npmjs.org'));
  for (const row of matrix.releasedPackages) { const stored = released.find(item => item.url === row.metadataUrl); assert(stored); const bytes = Buffer.from(stored.rawBody), value = parseJsonSupported(stored.rawBody); assert.equal(hash(bytes), row.metadataBodySha256); assert.equal(bytes.length, row.metadataBodyBytes); assert.equal(value.name, row.name); assert.equal(value.version, row.version); assert.equal(value.license, row.declaredLicense); assert.equal(value.dist.integrity, row.archiveIntegrity); assert.equal(row.signatureVerified, false); assert.equal(row.attestationVerified, false); assert.equal(row.redistributionLicenseClosure, false); }
  assert.equal(matrix.releasedAiArtifact.packagedCompleteLicensePresent, false); same(matrix.releasedAiArtifact.packagedNotices, []); assert.equal(ai.licenseComparison.packagedLicensePresent, false); assert.equal(matrix.releasedAiArtifact.sha256, ai.package.sha256);
  same([matrix.catalog.providers, matrix.catalog.models, matrix.catalog.apiCount], [42, 1592, 13]); assert.equal(matrix.catalog.licensingReviewed, false); assert.equal(matrix.catalog.regenerated, false); assert.equal(matrix.catalog.wholeCatalogBytesIdentical, false); assert.equal(matrix.catalog.allProviderDataBytesMatch, true);
  assert.equal(matrix.catalog.sourceManifest.sha256, release.catalog.manifestSha256); assert.equal(matrix.catalog.npmManifest.sha256, ai.catalogComparison.comparedFiles.find(row => row.name === '.manifest.json').packagedSha256);
  for (const notice of matrix.developerTooling.npm.rootNotices) verifyText(notice); assert.equal(matrix.developerTooling.npm.declaredLicense, 'Artistic-2.0'); assert.equal(matrix.developerTooling.npm.vendoredLicenseClosureReviewed, false); assert.equal(matrix.developerTooling.npm.invokedInThisLane, false);
  assert.equal(matrix.nativeAssetEvidence.files.length, 8); assert.equal(matrix.nativeAssetEvidence.artifactSpecificLicensingReviewed, false); assert(matrix.nativeAssetEvidence.files.every(row => row.executed === false && row.artifactSpecificLicensingReviewed === false));
  same(matrix.summary, { distinctDevelopmentPackages: 21, distinctInstalledFiles: 7929, packagesWithPackagedRootNotice: 20, packagesWithoutPackagedRootNotice: ['standardwebhooks@1.1.1'], packagedNamedNoticeFiles: 24, releasedPackageMetadataRows: 13, releasedNpmArchivesAcquired: 1, newPublicReads: 6, newDependenciesInstalled: 0, newProviderCalls: 0 });
  assert.equal(matrix.gaps.length, 7); assert(matrix.gaps.every(row => row.status === 'OPEN')); return true;
}
// Small inert archive reader for exact committed source/npm evidence. Rejects
// unsupported members; does not extract a file or execute package/source bytes.
export function inspectInertArchive(compressed, prefix) {
  const bytes = gunzipSync(compressed, { maxOutputLength: 64 * 1024 * 1024 }), files = new Map(), names = new Set();
  const string = (header, start, count) => header.subarray(start, start + count).toString('utf8').replace(/\0.*$/s, '');
  const octal = (header, start, count) => { const value = string(header, start, count).trim(); assert(/^[0-7]*$/.test(value)); const number = value ? Number.parseInt(value, 8) : 0; assert(Number.isSafeInteger(number)); return number; };
  let offset = 0;
  while (offset + 512 <= bytes.length) {
    const header = bytes.subarray(offset, offset + 512); if (header.every(value => value === 0)) { assert(bytes.subarray(offset).every(value => value === 0)); return files; }
    const checksum = octal(header, 148, 8), sum = [...header].reduce((total, value, index) => total + (index >= 148 && index < 156 ? 32 : value), 0); assert.equal(sum, checksum);
    const parent = string(header, 345, 155), leaf = string(header, 0, 100), name = parent ? parent + '/' + leaf : leaf, type = string(header, 156, 1) || '0';
    assert(['0', '5'].includes(type)); assert(name.startsWith(prefix)); assert(!name.includes('\\') && !name.startsWith('/') && !/^[A-Za-z]:/.test(name)); assert(!name.split('/').some(part => part === '..' || part === '.')); assert(!names.has(name)); names.add(name);
    const size = octal(header, 124, 12); assert(offset + 512 + size <= bytes.length);
    if (type === '0') files.set(name.slice(prefix.length), bytes.subarray(offset + 512, offset + 512 + size)); else assert.equal(size, 0);
    offset += 512 + Math.ceil(size / 512) * 512;
  }
  throw new Error('Truncated/missing terminal tar block');
}
export function verifyRetainedArtifacts(matrix, evidence) {
  const source = inspectInertArchive(evidence.get('release-archive'), 'pi-0.99.1/'), ai = inspectInertArchive(evidence.get('ai-archive'), 'package/');
  const piBytes = source.get('LICENSE'); assert(piBytes); assert.equal(hash(piBytes), matrix.sourceBaseline.license.sha256); assert.equal(piBytes.toString('utf8'), matrix.sourceBaseline.license.utf8Text);
  for (const row of matrix.nativeAssetEvidence.files) { const bytes = source.get(row.path); assert(bytes); assert.equal(bytes.length, row.bytes); assert.equal(hash(bytes), row.sha256); assert.equal(gitBlob(bytes), row.gitBlob); }
  assert.equal(ai.size, 813); assert.equal([...ai.keys()].filter(path => /(^|\/)(license|licence|notice|copying)(\.[^/]*)?$/i.test(path)).length, 0);
  assert(ai.get('package.json').equals(source.get('packages/ai/package.json'))); assert.equal(hash(ai.get('README.md')), matrix.releasedAiArtifact.readmeSha256);
  assert.equal(hash(source.get(matrix.catalog.sourceManifest.path)), matrix.catalog.sourceManifest.sha256); assert.equal(hash(ai.get(matrix.catalog.npmManifest.path)), matrix.catalog.npmManifest.sha256); return true;
}
function readConfined(path) { const target = resolve(repo, path), suffix = relative(repo, target); assert(suffix && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith('..' + sep)); let current = target; while (true) { assert(!lstatSync(current).isSymbolicLink()); const parent = dirname(current); if (parent === current) break; current = parent; } return readFileSync(target); }
export function main(args = process.argv.slice(2)) {
  assert.deepEqual(args, []); const bytes = readFileSync(matrixPath); assert.equal(hash(bytes), matrixSha256, 'Frozen license matrix changed'); const matrix = parseJsonSupported(bytes.toString('utf8')), evidence = new Map();
  for (const pin of matrix.evidenceFiles) { const bytes = readConfined(pin.path); assert.equal(bytes.length, pin.bytes); assert.equal(hash(bytes), pin.sha256, 'Retained evidence changed: ' + pin.path); evidence.set(pin.id, bytes); }
  assert(validateLicenseMatrix(matrix, evidence)); assert(verifyRetainedArtifacts(matrix, evidence));
  const mutations = [
    ['false gate approval', m => { m.gate.legalApproval = true; }],
    ['false redistribution closure', m => { m.gate.redistributionLicenseClosure = true; }],
    ['wrong Pi Apache classification', m => { m.sourceBaseline.license.declaredLicense = 'Apache-2.0'; }],
    ['altered Pi notice', m => { m.sourceBaseline.license.utf8Text += 'modified'; }],
    ['duplicate package identity', m => { m.packages.push(structuredClone(m.packages[0])); }],
    ['SDK root license declared MIT', m => { m.packages.find(p => p.name === 'openai').declaredLicense = 'MIT'; }],
    ['missing vendored BSD notice', m => { m.packages.find(p => p.name === '@anthropic-ai/sdk').packagedNotices.pop(); }],
    ['source notice laundered as packaged', m => { m.packages.find(p => p.name === 'standardwebhooks').packagedRootNoticePresent = true; }],
    ['source scope falsely approved', m => { m.packages.find(p => p.name === 'standardwebhooks').sourceAuthority.legalScopeApproved = true; }],
    ['wrong version-to-source bridge', m => { m.packages.find(p => p.name === 'standardwebhooks').manifestSha256 = '0'.repeat(64); }],
    ['altered external source notice', m => { m.standardWebhooksSourceEvidence.reads[5].rawUtf8 += 'modified'; }],
    ['unverified signature promoted', m => { m.releasedPackages[0].signatureVerified = true; }],
    ['unacquired Pi tarball promoted', m => { m.releasedPackages.find(p => !p.archiveAcquired).archiveAcquired = true; }],
    ['generated data licensing promoted', m => { m.catalog.licensingReviewed = true; }],
    ['native asset licensing promoted', m => { m.nativeAssetEvidence.artifactSpecificLicensingReviewed = true; }],
    ['profile installed count changed', m => { m.dependencyProfiles[0].installedFiles++; }],
    ['summed duplicate files accepted', m => { m.summary.distinctInstalledFiles++; }],
    ['source binary mutation', m => { m.nativeAssetEvidence.files[0].sha256 = '0'.repeat(64); }]
  ];
  for (const [name, mutate] of mutations) { const changed = structuredClone(matrix); mutate(changed); assert.throws(() => { validateLicenseMatrix(changed, evidence); verifyRetainedArtifacts(changed, evidence); }, name); }
  console.log(JSON.stringify({ schemaVersion: 1, kind: 'license-evidence-offline-checks', passed: mutations.length + 2, mutationRejections: mutations.length, distinctPackages: matrix.packages.length, rawArchiveExtraction: false, sourceOrSdkExecution: false, providerCalls: false, legalApproval: false, gate: 'P1-02 HOLD' }, null, 2));
}
if (process.argv[1] && resolve(process.argv[1]).toLowerCase() === resolve(own).toLowerCase()) main();
