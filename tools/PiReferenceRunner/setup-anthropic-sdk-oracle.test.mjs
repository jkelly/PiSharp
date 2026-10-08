// Offline developer checks only: importing setup does not invoke its main.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import test from 'node:test';
import { admitLifecycleDeclarations, admitPackagedLicense, inspectPackageArchive, inspectionReceiptPath, parseSetupArguments, selectSetupOracle } from './setup-anthropic-sdk-oracle.mjs';

const plan = JSON.parse(readFileSync(new URL('./anthropic-sdk-install-plan.json', import.meta.url), 'utf8'));
const oracle = resolve(plan.workspace.proposedRoot), taskRoot = dirname(oracle);

test('all eight exact archives use leaf receipts, including the three scoped packages', () => {
  const expected = [
    ['@anthropic-ai/sdk', 'anthropic-ai-sdk-0.124.0.tgz'],
    ['@babel/runtime', 'babel-runtime-7.29.2.tgz'],
    ['@stablelib/base64', 'stablelib-base64-1.0.1.tgz'],
    ['fast-sha256', 'fast-sha256-1.3.0.tgz'],
    ['json-schema-to-ts', 'json-schema-to-ts-3.1.1.tgz'],
    ['partial-json', 'partial-json-0.1.7.tgz'],
    ['standardwebhooks', 'standardwebhooks-1.1.1.tgz'],
    ['ts-algebra', 'ts-algebra-2.0.0.tgz']
  ];
  assert.deepEqual(plan.packages.map(row => [row.name, row.archiveFile]), expected);
  const receipts = expected.map(([, archiveFile]) => {
    const receipt = inspectionReceiptPath(oracle, archiveFile);
    assert.equal(receipt, resolve(oracle, 'archives', archiveFile + '.inspection.json'));
    assert.equal(dirname(receipt), resolve(oracle, 'archives'));
    return receipt;
  });
  assert.equal(new Set(receipts).size, 8);
});

test('inspection receipt paths fail closed for invalid, escaping and non-leaf archive names', () => {
  const invalid = [
    undefined, null, 42, {}, [], '', '.', '..',
    '../sdk-0.124.0.tgz', '..\\sdk-0.124.0.tgz',
    '/sdk-0.124.0.tgz', '\\sdk-0.124.0.tgz',
    'C:\\sdk-0.124.0.tgz', 'C:/sdk-0.124.0.tgz',
    '\\\\server\\share\\sdk-0.124.0.tgz',
    'archives/sdk-0.124.0.tgz', 'archives\\sdk-0.124.0.tgz',
    '@anthropic-ai/sdk-0.124.0.tgz', 'sdk-0.124.0.tgz:stream',
    'sdk-0.124.0.TGZ', 'sdk-0.124.0.tgz.inspection.json',
    'sdk-0.124.0.tgz ', 'sdk-0.124.0.tgz\n', 'sdk-0.124.0.tgz\r\n',
    'sdk-0.124.0.tgz\0', 'sdk-.tgz', 'sdk.tgz', 'con.tgz'
  ];
  for (const archiveFile of invalid) assert.throws(() => inspectionReceiptPath(oracle, archiveFile), /Invalid inspection archive filename/);
});

test('six reviewed setup argument forms parse without invoking setup', () => {
  const accepted = [
    [[], { mode: '--check', oracle: undefined }],
    [['--check'], { mode: '--check', oracle: undefined }],
    [['--prepare'], { mode: '--prepare', oracle: undefined }],
    [['--restore'], { mode: '--restore', oracle: undefined }],
    [['--check', '--oracle', oracle], { mode: '--check', oracle }],
    [['--oracle', oracle, '--check'], { mode: '--check', oracle }]
  ];
  for (const [args, expected] of accepted) assert.deepEqual(parseSetupArguments(args), expected);
});

test('fifteen malformed, duplicate or mutating oracle argument forms are rejected', () => {
  const rejected = [
    ['--unknown'], ['--check', '--check'], ['--prepare', '--restore'],
    ['--restore', '--prepare'], ['--check', '--prepare'],
    ['--check', '--oracle'], ['--check', '--oracle', ''],
    ['--check', '--oracle', '--restore'],
    ['--check', '--oracle', oracle, '--oracle', oracle],
    ['--oracle', oracle], ['--prepare', '--oracle', oracle],
    ['--restore', '--oracle', oracle], ['--oracle', oracle, '--prepare'],
    ['--check', 'unexpected'], ['--CHECK']
  ];
  for (const args of rejected) assert.throws(() => parseSetupArguments(args));
});

test('six root admission cases retain the reviewed default and explicit read-only root', () => {
  for (const mode of ['--check', '--prepare', '--restore']) assert.equal(selectSetupOracle({ mode }, plan, taskRoot), oracle);
  assert.equal(selectSetupOracle({ mode: '--check', oracle }, plan, join(taskRoot, 'detached-review')), oracle);
  assert.throws(() => selectSetupOracle({ mode: '--restore', oracle }, plan, taskRoot), /Mutation mode cannot override/);
  assert.throws(() => selectSetupOracle({ mode: '--check', oracle: join(taskRoot, 'other-oracle') }, plan, taskRoot), /Workspace differs from reviewed plan/);
});

const observedBytes = readFileSync(new URL('../../artifacts/anthropic-sdk-admission/standardwebhooks-1.1.1.tgz', import.meta.url));
const observedArchive = inspectPackageArchive(observedBytes), observedManifestBytes = observedArchive.files.get('package.json');
const observedManifest = JSON.parse(observedManifestBytes.toString('utf8'));
const observedRow = plan.packages.find(row => row.name === 'standardwebhooks');
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const observedEvidence = { archiveBytes: observedBytes.length, archiveSha256: digest(observedBytes), manifestSha256: digest(observedManifestBytes), memberPaths: [...observedArchive.files.keys()] };

test('exact observed standardwebhooks prepare/build declarations are admitted as disabled', () => {
  assert.equal('sha512-' + createHash('sha512').update(observedBytes).digest('base64'), observedRow.lockEntry.integrity);
  assert.deepEqual(admitLifecycleDeclarations(observedManifest, observedRow, observedEvidence), {
    lifecycleScripts: { prepare: 'npm run build' }, lifecycleScriptsExecuted: false, disabledLifecycleAdmission: plan.disabledLifecycleAdmission
  });
  assert.ok(plan.setupProposal.restoreArguments.includes('--ignore-scripts'));
});

test('lifecycle exception rejects changed scripts, identities, archive or manifest evidence', () => {
  const rejected = [
    [{ ...observedManifest, scripts: { ...observedManifest.scripts, prepare: 'node unsafe.js' } }, observedRow, observedEvidence],
    [{ ...observedManifest, scripts: { ...observedManifest.scripts, build: 'node unsafe.js' } }, observedRow, observedEvidence],
    [{ ...observedManifest, scripts: { build: 'tsc' } }, observedRow, observedEvidence],
    [{ ...observedManifest, version: '1.1.2' }, observedRow, observedEvidence],
    [observedManifest, { ...observedRow, lockEntry: { ...observedRow.lockEntry, version: '1.1.2' } }, observedEvidence],
    [observedManifest, { ...observedRow, archiveFile: 'standardwebhooks-1.1.2.tgz' }, observedEvidence],
    [observedManifest, observedRow, { ...observedEvidence, archiveBytes: observedEvidence.archiveBytes + 1 }],
    [observedManifest, observedRow, { ...observedEvidence, archiveSha256: '0'.repeat(64) }],
    [observedManifest, observedRow, { ...observedEvidence, manifestSha256: '0'.repeat(64) }],
    [observedManifest, observedRow, { ...observedEvidence, memberPaths: [...observedEvidence.memberPaths, 'unexpected.js'] }],
    [{ ...observedManifest, name: '@babel/runtime' }, { ...observedRow, name: '@babel/runtime' }, observedEvidence]
  ];
  for (const args of rejected) assert.throws(() => admitLifecycleDeclarations(...args), /Unexpected lifecycle declaration/);
});

test('install hooks and additional automatic lifecycle hooks are rejected for every package', () => {
  const ordinaryRow = { name: '@babel/runtime', archiveFile: 'babel-runtime-7.29.2.tgz', lockEntry: { version: '7.29.2' } };
  for (const key of ['preinstall', 'install', 'postinstall', 'prepublish', 'preprepare', 'postprepare']) {
    assert.throws(() => admitLifecycleDeclarations({ ...observedManifest, scripts: { ...observedManifest.scripts, [key]: 'node unsafe.js' } }, observedRow, observedEvidence), /Unexpected lifecycle declaration/);
    assert.throws(() => admitLifecycleDeclarations({ name: ordinaryRow.name, version: ordinaryRow.lockEntry.version, scripts: { [key]: '' } }, ordinaryRow, observedEvidence), /Unexpected lifecycle declaration/);
  }
  assert.throws(() => admitLifecycleDeclarations({ name: ordinaryRow.name, version: ordinaryRow.lockEntry.version, scripts: { prepare: 'npm run build', build: 'tsc' } }, ordinaryRow, observedEvidence), /Unexpected lifecycle declaration/);
});

test('native markers remain rejected even for the exact disabled lifecycle exception', () => {
  assert.throws(() => admitLifecycleDeclarations({ ...observedManifest, gypfile: true }, observedRow, observedEvidence), /Unexpected native build declaration/);
  for (const path of ['binding.gyp', 'native/BINDING.GYP', 'native/addon.node', 'native/addon.NODE']) {
    assert.throws(() => admitLifecycleDeclarations(observedManifest, observedRow, { ...observedEvidence, memberPaths: [...observedEvidence.memberPaths, path] }), /Unexpected native build declaration/);
  }
});

test('fresh root selection rejects the preserved partial root and detached mutation roots', () => {
  assert.equal(oracle, resolve(taskRoot, 'Pi-anthropic-sdk-oracle-v0.99.1-reviewed'));
  assert.throws(() => selectSetupOracle({ mode: '--check', oracle: plan.preservedBlockedAttempt.root }, plan, taskRoot), /Workspace differs from reviewed plan/);
  assert.throws(() => selectSetupOracle({ mode: '--prepare' }, plan, join(taskRoot, 'detached-review')), /Workspace differs from reviewed plan/);
});

test('known empty notices are admitted only as declared MIT for task-local development', () => {
  assert.deepEqual([...observedArchive.files.keys()].filter(path => /(^|\/)(?:license|licence|notice|copying)(?:\.[^/]*)?$/i.test(path)), []);
  assert.deepEqual(admitPackagedLicense(observedManifest, observedRow, observedEvidence, []), {
    licenseEvidence: 'declared-MIT-only', packagedRootLicenseVerified: false, redistributionLicenseClosure: false,
    licenseReviewStatus: 'P1-02 HOLD', developmentOnlyLicenseAdmission: plan.developmentOnlyLicenseAdmission
  });
  assert.equal(plan.developmentOnlyLicenseAdmission.scope, 'task-local-development-only');
  assert.equal(plan.setupProposal.redistributionLicenseClosure, false);
  assert.equal(plan.setupProposal.licenseReviewStatus, 'P1-02 HOLD');
});

test('every other selected package still rejects missing packaged root-license evidence', () => {
  for (const row of plan.packages.filter(row => row.name !== 'standardwebhooks')) {
    assert.throws(() => admitPackagedLicense({ name: row.name, version: row.lockEntry.version, license: row.lockEntry.license }, row, observedEvidence, []), /Packaged root license text/);
  }
});

test('missing-notice exception rejects changed archive, manifest, member, dependency and script pins', () => {
  const rejected = [
    [observedManifest, observedRow, { ...observedEvidence, archiveSha256: '0'.repeat(64) }, []],
    [observedManifest, observedRow, { ...observedEvidence, manifestSha256: '0'.repeat(64) }, []],
    [observedManifest, observedRow, { ...observedEvidence, memberPaths: [...observedEvidence.memberPaths, 'unexpected.js'] }, []],
    [observedManifest, { ...observedRow, archiveFile: 'standardwebhooks-1.1.2.tgz' }, observedEvidence, []],
    [{ ...observedManifest, version: '1.1.2' }, observedRow, observedEvidence, []],
    [{ ...observedManifest, license: 'Apache-2.0' }, observedRow, observedEvidence, []],
    [{ ...observedManifest, dependencies: {} }, observedRow, observedEvidence, []],
    [{ ...observedManifest, scripts: { ...observedManifest.scripts, prepare: 'node unsafe.js' } }, observedRow, observedEvidence, []]
  ];
  for (const args of rejected) assert.throws(() => admitPackagedLicense(...args));
});

test('known missing notices cannot be replaced with an invented license receipt', () => {
  const lexicalMit = { path: 'LICENSE', utf8Text: 'Permission is hereby granted, free of charge. THE SOFTWARE IS PROVIDED "AS IS".' };
  assert.throws(() => admitPackagedLicense(observedManifest, observedRow, observedEvidence, [lexicalMit]), /exactly empty packaged notices/);
  const ordinaryRow = plan.packages.find(row => row.name === '@babel/runtime');
  const ordinaryManifest = { name: ordinaryRow.name, version: ordinaryRow.lockEntry.version, license: 'MIT' };
  assert.equal(admitPackagedLicense(ordinaryManifest, ordinaryRow, observedEvidence, [lexicalMit]).packagedRootLicenseVerified, true);
  assert.throws(() => admitPackagedLicense(ordinaryManifest, ordinaryRow, observedEvidence, [{ ...lexicalMit, path: 'nested/LICENSE' }]), /Packaged root license text/);
  assert.throws(() => admitPackagedLicense(ordinaryManifest, ordinaryRow, observedEvidence, [{ path: 'LICENSE', utf8Text: 'Apache License, Version 2.0' }]), /Packaged root license text/);
});
