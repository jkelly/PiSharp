import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { main, summarize } from './write-validation-manifest.mjs';
import { main as trxMain, renderSummary, renderTrx } from './manifest-to-trx.mjs';

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

test('manifest-to-trx renders valid TRX XML and Markdown step summary for pass, skip, fail, and missing suites', () => {
  const manifest = {
    schemaVersion: 1,
    checkoutSha: '0123456789abcdef0123456789abcdef01234567',
    generatedUtc: '2026-11-23T00:00:00.000Z',
    platform: { os: 'win32', arch: 'x64', runner: 'Windows' },
    result: 'failed',
    suites: [
      {
        suite: 'PiSharp.Tools.Tests',
        result: 'failed',
        cases: [
          { id: 'c1', name: 'case <one> & "ok"', result: 'passed', rawStatus: 'passed', reason: null },
          { id: 'c2', name: 'case-skip', result: 'skipped', rawStatus: 'SKIPPED', reason: 'tool absent' },
          { id: 'c3', name: 'case-fail', result: 'failed', rawStatus: 'failed', reason: 'boom\u0007 <bad>' }
        ],
        consoleSkipObservations: ['SKIP symlink unavailable']
      },
      {
        suite: 'PiSharp.Missing.Tests',
        result: 'missing-or-invalid',
        error: 'ENOENT: missing report',
        cases: []
      },
      {
        suite: 'PiSharp.Incomplete.Tests',
        result: 'failed',
        cases: [
          { id: 'p1', name: 'only-recorded-case', result: 'passed', rawStatus: 'passed', reason: null }
        ]
      }
    ]
  };

  const trx = renderTrx(manifest);
  assert.match(trx, /^<\?xml version="1\.0" encoding="utf-8"\?>/);
  assert.match(trx, /outcome="Failed"/);
  assert.match(trx, /total="6" executed="5" passed="2" failed="3"/);
  assert.match(trx, /notExecuted="1"/);
  assert.match(trx, /case &lt;one&gt; &amp; &quot;ok&quot;/);
  assert.match(trx, /boom &lt;bad&gt;/);
  assert.doesNotMatch(trx, /\u0007/);
  assert.match(trx, /ENOENT: missing report/);
  assert.match(trx, /PiSharp\.Incomplete\.Tests\.&lt;suite&gt;/);

  const md = renderSummary(manifest, 'Core Suite');
  assert.match(md, /### Core Suite/);
  assert.match(md, /\| `PiSharp\.Tools\.Tests` \| ❌ failed \| 1 \| 1 \| 1 \| 3 \|/);
  assert.match(md, /\| `PiSharp\.Missing\.Tests` \| ❌ missing-or-invalid \| 0 \| 0 \| 0 \| 0 \|/);
  assert.match(md, /\| `PiSharp\.Incomplete\.Tests` \| ❌ failed \| 1 \| 0 \| 0 \| 1 \|/);
  assert.match(md, /Failures \(3\)/);
  assert.match(md, /PiSharp\.Incomplete\.Tests.*&lt;suite&gt;|PiSharp\.Incomplete\.Tests.*`<suite>`/);
  assert.match(md, /Console skip observations \(1\)/);

  const dir = mkdtempSync(join(tmpdir(), 'pisharp-trx-'));
  try {
    const manifestFile = join(dir, 'manifest.json');
    const trxFile = join(dir, 'results.trx');
    const summaryFile = join(dir, 'summary.md');
    writeFileSync(manifestFile, JSON.stringify(manifest));
    assert.equal(trxMain(['--manifest', manifestFile, '--trx', trxFile, '--summary', summaryFile, '--title', 'CLI Check']), 0);
    assert.ok(readFileSync(trxFile, 'utf8').includes('<TestRun'));
    assert.ok(readFileSync(summaryFile, 'utf8').includes('### CLI Check'));
  } finally { rmSync(dir, { recursive: true, force: true }); }
});
