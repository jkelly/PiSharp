import { qualifiedReferenceFile } from '../PublicReferenceLayout.mjs';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

// This adapter calls the unchanged exported functions; it contains no copied truncation logic.
const sourceSha = 'd86654abb8862e201933517d6f1fce9f88dd117f';
const modulePath = 'packages/coding-agent/src/core/tools/truncate.ts';
const moduleSha256 = '8e4507c3ed7ca7548cf7c2d7f77d07f2ae63a38b756789cbb44e9062db6c8762';
const runtimePath = qualifiedReferenceFile('P:/PiSharp/root/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe', '3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237');
const scriptPath = fileURLToPath(import.meta.url);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const gitArgs = (root, ...args) => ['-c', `safe.directory=${root.replaceAll('\\', '/')}`, '-C', root, ...args];
const git = (root, ...args) => execFileSync('git', gitArgs(root, ...args), { encoding: 'utf8', windowsHide: true });
const jsonBytes = value => Buffer.from(`${JSON.stringify(value, null, 2)}\n`, 'utf8');

function checkSource(root) {
  assert.equal(git(root, 'rev-parse', 'HEAD').trim(), sourceSha, 'Wrong upstream revision');
  const status = git(root, 'status', '--porcelain=v1', '--untracked-files=all');
  assert.equal(status, '', 'Source checkout must be entirely clean');
  const source = readFileSync(join(root, modulePath));
  assert.equal(hash(source), moduleSha256, 'Source bytes differ from pinned module');
  assert.equal(hash(execFileSync('git', gitArgs(root, 'show', `${sourceSha}:${modulePath}`))), moduleSha256);
  // The complete static dependency closure is this one import-free module plus Node's Buffer builtin.
  assert.ok(!/^\s*(?:import\s|export\s.*\sfrom\s)/m.test(source.toString('utf8')), 'Dependency closure changed');
  assert.ok(!/\b(?:require\s*\(|import\s*\()/.test(source.toString('utf8')), 'Unexpected dynamic dependency');
  return { revision: sourceSha, status, sourceSha256: hash(source) };
}

assert.equal(process.version, 'v24.19.0', 'Use the qualified Node 24.19.0 runtime');
assert.equal(realpathSync(process.execPath).toLowerCase(), realpathSync(runtimePath).toLowerCase(), 'Use the absolute qualified runtime');
assert.ok(process.execArgv.includes('--experimental-strip-types'), 'Type stripping must be explicit');

if (process.argv[2] === '--observe') {
  assert.equal(process.argv.length, 5, 'Internal observation arguments are source root and input');
  const root = resolve(process.argv[3]);
  const before = checkSource(root);
  const inputBytes = readFileSync(resolve(process.argv[4]));
  const input = JSON.parse(inputBytes.toString('utf8'));
  assert.equal(input.schemaVersion, 1);
  assert.equal(input.sourceSha, sourceSha);
  assert.equal(input.inputOrigin, 'PiSharp-authored synthetic boundary corpus');
  assert.ok(Array.isArray(input.cases) && input.cases.length > 0);
  assert.equal(new Set(input.cases.map(test => test.caseId)).size, input.cases.length, 'Duplicate case ID');
  const reference = await import(pathToFileURL(join(root, modulePath)).href);
  assert.equal(reference.DEFAULT_MAX_LINES, 2000);
  assert.equal(reference.DEFAULT_MAX_BYTES, 51200);
  const fields = ['content', 'truncated', 'truncatedBy', 'totalLines', 'totalBytes', 'outputLines', 'outputBytes',
    'lastLinePartial', 'firstLineExceedsLimit', 'maxLines', 'maxBytes'].sort();
  const cases = input.cases.map(test => {
    assert.equal(typeof test.content, 'string');
    assert.equal(typeof test.category, 'string');
    assert.ok(test.options === undefined || (test.options && typeof test.options === 'object' && !Array.isArray(test.options)));
    for (const [name, value] of Object.entries(test.options ?? {})) {
      assert.ok(['maxLines', 'maxBytes'].includes(name));
      assert.ok(value === null || (Number.isInteger(value) && value >= 0 && value <= 2147483647));
    }
    const head = reference.truncateHead(test.content, test.options);
    const tail = reference.truncateTail(test.content, test.options);
    for (const result of [head, tail]) assert.deepEqual(Object.keys(result).sort(), fields, 'Unexpected result shape');
    return { caseId: test.caseId, category: test.category, head, tail };
  });
  const after = checkSource(root);
  assert.deepEqual(after, before);
  process.stdout.write(jsonBytes({ schemaVersion: 1, sourceSha, scope: 'pure-head-tail-tool-output-truncation',
    inputSha256: hash(inputBytes), observations: { cases, checks: { capturedCaseCount: cases.length,
      capturedResultCount: cases.length * 2, categories: [...new Set(cases.map(test => test.category))].sort(),
      sourceUnchanged: true, resultFieldsExact: true, normalization: [] } } }));
} else {
  assert.equal(process.argv.length, 5,
    'Usage: qualified-node --experimental-strip-types capture-tool-truncation.mjs <clean-source-root> <input.json> <new-output-directory>');
  const root = resolve(process.argv[2]);
  const inputPath = resolve(process.argv[3]);
  const output = resolve(process.argv[4]);
  assert.ok(existsSync(output), 'Create the evidence directory before capture');
  const names = ['core.capture-1.json', 'core.capture-2.json', 'core.expected.json', 'core.provenance.json'];
  for (const name of names) assert.ok(!existsSync(join(output, name)), `Refusing to overwrite ${name}`);
  const before = checkSource(root);
  const capture = () => execFileSync(process.execPath,
    ['--experimental-strip-types', '--disable-warning=ExperimentalWarning', scriptPath, '--observe', root, inputPath],
    { windowsHide: true, maxBuffer: 8 * 1024 * 1024 });
  const first = capture();
  const second = capture();
  assert.ok(first.equals(second), 'Independent reference captures differ');
  const after = checkSource(root);
  assert.deepEqual(after, before);
  const files = [[names[0], first], [names[1], second], [names[2], first]];
  const provenance = {
    schemaVersion: 1, sourceRepository: 'https://github.com/earendil-works/pi', sourceSha,
    scope: 'pure-head-tail-tool-output-truncation',
    inputOrigin: 'PiSharp-authored synthetic boundary corpus',
    observationsOrigin: 'Unchanged upstream truncateHead and truncateTail exports observed under Node type stripping',
    runner: { path: 'tools/PiReferenceRunner/capture-tool-truncation.mjs', sha256: hash(readFileSync(scriptPath)) },
    runtime: { path: runtimePath, version: process.version, sha256: hash(readFileSync(process.execPath)),
      platform: process.platform, architecture: process.arch, flags: ['--experimental-strip-types', '--disable-warning=ExperimentalWarning'] },
    sourceChecks: { before, after },
    dependencyClosure: { sourceModules: [{ path: modulePath, sha256: moduleSha256 }],
      sourceImports: [], externalPackages: [], builtinsUsedBySource: ['Buffer'], copiedOrExtractedSource: false,
      sourcePatches: [], resolutionShims: [] },
    input: { path: 'fixtures/pi-v0.99.1/tool-truncation/core.input.json', sha256: hash(readFileSync(inputPath)) },
    captures: files.map(([name, bytes]) => ({ path: name, sha256: hash(bytes), bytes: bytes.length })),
    independentCaptureProcesses: 2, capturesByteIdentical: true, normalization: [],
    caseCount: JSON.parse(first).observations.cases.length,
    limits: ['Head and tail string primitives only', 'No external-byte decoding or binary detection',
      'Nonnegative signed-32-bit integer or null/default budgets only', 'No file/process/tool effects or integration claims',
      'Other truncate.ts exports are outside this slice', 'Windows x64 reference capture; platform matrix remains open']
  };
  for (const [name, bytes] of files) writeFileSync(join(output, name), bytes, { flag: 'wx' });
  writeFileSync(join(output, names[3]), jsonBytes(provenance), { flag: 'wx' });
  process.stdout.write(`${JSON.stringify({ caseCount: provenance.caseCount, capturesByteIdentical: true,
    files: provenance.captures, provenanceSha256: hash(readFileSync(join(output, names[3]))) })}\n`);
}
