import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { main, summarize } from './write-validation-manifest.mjs';

test('normalizes native and release reports without mistaking oracle SHA for checkout SHA', () => {
  const native = summarize('core', { sourceSha: 'old-oracle', tests: [{ testId: 'case', status: 'passed' }] }, ['fixture']);
  assert.equal(native.result, 'passed');
  assert.equal(native.oracleSourceSha, 'old-oracle');
  const release = summarize('release', { tests: 2, results: [{ Id: 'ok', status: 'PASS_AUTHORED_NATIVE_ONLY' }, { id: 'absent', status: 'SKIP_NO_NPM', reason: 'npm absent' }] }, ['npm']);
  assert.equal(release.result, 'passed-with-skips');
  assert.equal(release.cases[1].reason, 'npm absent');
});

test('missing, malformed and incomplete reports cannot become passing receipts', () => {
  assert.throws(() => summarize('empty', { tests: [] }, []), /no case results/);
  assert.equal(summarize('partial', { completeSuite: false, tests: [{ testId: 'done', status: 'passed' }] }, []).result, 'failed');
  assert.equal(summarize('hung', { tests: [{ testId: 'case', status: 'unexecuted' }] }, []).result, 'failed');
  assert.equal(summarize('truncated', { availableTests: 2, tests: [{ testId: 'one', status: 'passed' }] }, []).result, 'failed');
  assert.equal(summarize('count', { failures: 1, results: [{ Id: 'case', status: 'PASS' }] }, []).result, 'failed');
});

test('CLI writes failure ledger for absent report, rejected skip and failed prerequisite', () => {
  const dir = mkdtempSync(join(tmpdir(), 'pisharp-manifest-'));
  try {
    const out = join(dir, 'manifest.json'), report = join(dir, 'suite.json'), execution = join(dir, 'execution.json');
    const args = ['--output', out, '--report', `suite=${report}`, '--prerequisite', 'fixture'];
    assert.equal(main(args), 1);
    let value = JSON.parse(readFileSync(out));
    assert.equal(value.suites[0].result, 'missing-or-invalid');
    assert.match(value.checkoutSha, /^[a-f0-9]{40}$/);
    assert.ok(value.platform.os);
    writeFileSync(report, JSON.stringify({ results: [{ id: 'x', status: 'SKIPPED', reason: 'fixture absent' }] }));
    assert.equal(main(args), 0);
    assert.equal(main([...args, '--reject-skips']), 1);
    writeFileSync(report, JSON.stringify({ results: [{ id: 'x', status: 'passed' }] }));
    const console = join(dir, 'console.log');
    writeFileSync(console, 'INFO symlink fixture unavailable: IOException\nPASS grouped file tests\n');
    assert.equal(main([...args, '--console', `suite=${console}`]), 0);
    assert.equal(JSON.parse(readFileSync(out)).suites[0].result, 'passed-with-skips');
    assert.equal(main([...args, '--console', `suite=${console}`, '--reject-skips']), 1);
    assert.equal(JSON.parse(readFileSync(out)).suites[0].consoleSkipObservations.length, 1);
    writeFileSync(execution, JSON.stringify({ result: 'failed', stages: [{ name: 'build', result: 'failed' }] }));
    assert.equal(main([...args, '--execution', execution]), 1);
    assert.equal(JSON.parse(readFileSync(out)).execution.stages[0].result, 'failed');
  } finally { rmSync(dir, { recursive: true, force: true }); }
});
