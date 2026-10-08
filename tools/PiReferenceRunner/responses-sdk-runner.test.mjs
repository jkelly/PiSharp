import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { cpSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import test from 'node:test';
import { parseSetupArguments, selectSetupOracle } from './setup-responses-sdk-oracle.mjs';
import { parseCaptureArguments } from './capture-responses-sdk.mjs';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const plan = JSON.parse(readFileSync(join(repo, 'tools/PiReferenceRunner/responses-sdk-install-plan.json'), 'utf8'));
const approved = resolve(plan.workspace.proposedRoot);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const scratchRoot = join(repo, 'artifacts', 'sdk-runner-regression-temp');
mkdirSync(scratchRoot, { recursive: true });

function inside(root, path) {
  const full = resolve(root, path), suffix = relative(realpathSync(root), full);
  assert.ok(suffix && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith(`..${sep}`), 'Operation must stay in the regression temporary copy');
  return full;
}

async function isolated(action) {
  const root = mkdtempSync(join(scratchRoot, 'regression-'));
  try {
    const candidate = inside(root, 'detached/candidates/frozen-repository');
    mkdirSync(candidate, { recursive: true });
    for (const path of ['tools/PiReferenceRunner/setup-responses-sdk-oracle.mjs', 'tools/PiReferenceRunner/capture-responses-sdk.mjs', 'tools/PiReferenceRunner/responses-sdk-install-plan.json', 'tools/CompatibilityReport/raw-json.mjs']) {
      const target = inside(root, join('detached/candidates/frozen-repository', path));
      mkdirSync(dirname(target), { recursive: true });
      cpSync(join(repo, path), target);
    }
    return await action(candidate, root);
  } finally {
    const physical = realpathSync(root), suffix = relative(realpathSync(scratchRoot), physical);
    assert.ok(suffix && !isAbsolute(suffix) && suffix !== '..' && !suffix.startsWith(`..${sep}`) && suffix.startsWith('regression-'), 'Cleanup must stay in the created regression temporary root');
    rmSync(physical, { recursive: true, force: true });
  }
}

function snapshot(root) {
  const files = [];
  function visit(directory) {
    for (const name of readdirSync(directory).sort()) {
      const path = join(directory, name), stat = lstatSync(path);
      assert.equal(stat.isSymbolicLink(), false);
      if (stat.isDirectory()) visit(path);
      else files.push({ path: relative(root, path), sha256: hash(readFileSync(path)) });
    }
  }
  visit(root);
  return files;
}

test('setup preserves original modes and accepts explicit oracle only for an explicit read-only check', () => {
  assert.deepEqual(parseSetupArguments([]), { mode: '--check', oracle: undefined });
  for (const mode of ['--check', '--prepare', '--restore']) assert.deepEqual(parseSetupArguments([mode]), { mode, oracle: undefined });
  assert.deepEqual(parseSetupArguments(['--check', '--oracle', approved]), { mode: '--check', oracle: approved });
  assert.deepEqual(parseSetupArguments(['--oracle', approved, '--check']), { mode: '--check', oracle: approved });
});

test('setup rejects duplicate, missing, unknown and mutation-mode oracle arguments before effects', () => {
  for (const args of [['--check', '--check'], ['--check', '--prepare'], ['--oracle'], ['--check', '--oracle'], ['--check', '--oracle', ''], ['--check', '--oracle', '--restore'], ['--check', '--unknown'], ['--check', '--oracle', approved, '--oracle', approved], ['--oracle', approved], ['--prepare', '--oracle', approved], ['--restore', '--oracle', approved]])
    assert.throws(() => parseSetupArguments(args), /Usage:|explicit oracle is permitted only with --check/, JSON.stringify(args));
});

test('capture argument parser preserves explicit oracle and rejects invalid or duplicate arguments', () => {
  assert.deepEqual(parseCaptureArguments(['--oracle', approved]), { first: false, oracle: approved });
  assert.deepEqual(parseCaptureArguments(['--capture-new', '--oracle', approved]), { first: true, oracle: approved });
  for (const args of [['--oracle'], ['--oracle', ''], ['--oracle', '--capture-new'], ['--oracle', approved, '--oracle', approved], ['--capture-new', '--capture-new'], ['--restore'], ['--check']])
    assert.throws(() => parseCaptureArguments(args), /Usage:/, JSON.stringify(args));
});

test('detached setup accepts only the exact reviewed oracle for read-only mode', () => isolated(async candidate => {
  const setup = await import(pathToFileURL(join(candidate, 'tools/PiReferenceRunner/setup-responses-sdk-oracle.mjs')).href);
  const detachedParent = dirname(candidate);
  assert.notEqual(resolve(join(detachedParent, 'Pi-responses-sdk-oracle-v0.99.1')).toLowerCase(), approved.toLowerCase());
  assert.equal(setup.selectSetupOracle(setup.parseSetupArguments(['--check', '--oracle', approved]), plan), approved);
  assert.throws(() => setup.selectSetupOracle(setup.parseSetupArguments(['--check']), plan), /Workspace differs from reviewed path/);
  assert.throws(() => setup.selectSetupOracle({ mode: '--check', oracle: join(candidate, 'alternate-oracle') }, plan), /Workspace differs from reviewed path/);
  for (const mode of ['--prepare', '--restore']) {
    assert.throws(() => setup.selectSetupOracle({ mode, oracle: approved }, plan), /explicit oracle is permitted only with --check/);
    assert.throws(() => setup.selectSetupOracle({ mode }, plan), /Workspace differs from reviewed path/);
  }
}));

test('both detached capture preflight calls forward explicit approved path and read-only flags to subprocess', () => isolated(async candidate => {
  // This command witness is confined to the copy. It performs no oracle access,
  // source execution, download, installation or writes; full preflight remains a separate reproduction.
  const setupPath = join(candidate, 'tools/PiReferenceRunner/setup-responses-sdk-oracle.mjs');
  writeFileSync(setupPath, `import assert from 'node:assert/strict';\nassert.deepEqual(process.argv.slice(2), ['--check', '--oracle', ${JSON.stringify(approved)}]);\nprocess.stdout.write(JSON.stringify({ mode: '--check', oracle: process.argv[4], cwd: process.cwd(), mutations: false }));\n`);
  const capture = await import(pathToFileURL(join(candidate, 'tools/PiReferenceRunner/capture-responses-sdk.mjs')).href);
  const beforeFiles = snapshot(candidate);
  const environment = { SystemRoot: process.env.SystemRoot, WINDIR: process.env.WINDIR, TEMP: scratchRoot, TMP: scratchRoot };
  const before = capture.readOnlySetupCheck(approved, environment);
  const after = capture.readOnlySetupCheck(approved, environment);
  for (const observed of [before, after]) assert.deepEqual(observed, { mode: '--check', oracle: approved, cwd: candidate, mutations: false });
  assert.deepEqual(snapshot(candidate), beforeFiles);
}));

test('detached setup CLI refuses alternate roots and mutation overrides without filesystem effects', () => isolated(candidate => {
  const setupPath = join(candidate, 'tools/PiReferenceRunner/setup-responses-sdk-oracle.mjs');
  const before = snapshot(candidate);
  const rejected = [
    [['--check', '--oracle', join(candidate, 'alternate-oracle')], /Workspace differs from reviewed path/],
    [['--check', '--oracle', approved, '--oracle', approved], /Usage:/],
    [['--check', '--oracle'], /Usage:/],
    [['--check', '--check'], /Usage:/],
    [['--check', '--restore'], /Usage:/],
    [['--prepare', '--oracle', approved], /explicit oracle is permitted only with --check/],
    [['--restore', '--oracle', approved], /explicit oracle is permitted only with --check/],
    [['--prepare'], /Workspace differs from reviewed path/],
    [['--restore'], /Workspace differs from reviewed path/]
  ];
  for (const [args, error] of rejected) {
    const child = spawnSync(process.execPath, [setupPath, ...args], { cwd: candidate, windowsHide: true, encoding: 'utf8', timeout: 20_000, maxBuffer: 1024 * 1024 });
    assert.equal(child.status, 1, `${JSON.stringify(args)}\n${child.stdout}\n${child.stderr}`);
    assert.match(child.stderr, error);
    assert.deepEqual(snapshot(candidate), before, `Rejected command must not change its detached copy: ${JSON.stringify(args)}`);
  }
}));
