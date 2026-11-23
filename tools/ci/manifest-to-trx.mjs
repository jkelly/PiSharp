#!/usr/bin/env node
// Convert a write-validation-manifest.mjs JSON manifest into Visual Studio TRX XML
// and/or a GitHub Actions Step Summary Markdown report.
import { createHash } from 'node:crypto';
import { appendFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

function xmlEscape(value) {
  return String(value ?? '')
    // Strip illegal XML 1.0 control characters (keep tab, LF, CR)
    .replace(/[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]/g, '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}

function deterministicGuid(seed) {
  const hex = createHash('sha256').update(seed).digest('hex');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-5${hex.slice(13, 16)}-8${hex.slice(17, 20)}-${hex.slice(20, 32)}`;
}

function mdEscape(value) {
  return String(value ?? '').replace(/\|/g, '\\|').replace(/\r?\n/g, ' ');
}

export function renderTrx(manifest) {
  const generated = manifest.generatedUtc ?? new Date().toISOString();
  const runId = deterministicGuid(`run:${manifest.checkoutSha ?? 'unknown'}:${generated}`);
  const testListId = '8c84fa94-04c1-424b-9868-57a2d4851a1d';
  const results = [];
  const definitions = [];
  const entries = [];
  let total = 0, executed = 0, passed = 0, failed = 0, notExecuted = 0;

  for (const suite of manifest.suites ?? []) {
    const suiteName = suite.suite ?? 'unknown';
    const cases = Array.isArray(suite.cases) ? suite.cases : [];
    if (cases.length === 0) {
      // Represent missing/malformed suite as a single failed synthetic test so CI parsers flag it.
      total++; executed++; failed++;
      const testId = deterministicGuid(`suite-error:${suiteName}`);
      const execId = deterministicGuid(`exec:suite-error:${suiteName}`);
      const fullName = `${suiteName}.<suite>`;
      const msg = suite.error ?? `Suite ${suiteName} reported result '${suite.result ?? 'missing-or-invalid'}' with no case results.`;
      definitions.push(
        `    <UnitTest name="${xmlEscape(fullName)}" storage="${xmlEscape(suiteName)}" id="${testId}">\n` +
        `      <Execution id="${execId}" />\n` +
        `      <TestMethod codeBase="${xmlEscape(suiteName)}" adapterTypeName="PiSharp.ConsoleRunner" className="${xmlEscape(suiteName)}" name="&lt;suite&gt;" />\n` +
        `    </UnitTest>`
      );
      entries.push(`    <TestEntry testId="${testId}" executionId="${execId}" testListId="${testListId}" />`);
      results.push(
        `    <UnitTestResult executionId="${execId}" testId="${testId}" testName="${xmlEscape(fullName)}" computerName="${xmlEscape(manifest.platform?.runner ?? manifest.platform?.os ?? 'pisharp')}" duration="00:00:00.0000000" startTime="${xmlEscape(generated)}" endTime="${xmlEscape(generated)}" testType="13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b" outcome="Failed" testListId="${testListId}">\n` +
        `      <Output><ErrorInfo><Message>${xmlEscape(msg)}</Message></ErrorInfo></Output>\n` +
        `    </UnitTestResult>`
      );
      continue;
    }

    let suiteFailedCases = 0;
    for (let i = 0; i < cases.length; i++) {
      const c = cases[i];
      total++;
      const caseName = c.name ?? c.id ?? `case-${i}`;
      const fullName = `${suiteName}.${caseName}`;
      const testId = deterministicGuid(`test:${suiteName}:${i}:${c.id ?? caseName}`);
      const execId = deterministicGuid(`exec:${suiteName}:${i}:${c.id ?? caseName}`);
      let outcome = 'Failed';
      if (c.result === 'passed') {
        outcome = 'Passed';
        executed++;
        passed++;
      } else if (c.result === 'skipped') {
        outcome = 'NotExecuted';
        notExecuted++;
      } else {
        outcome = 'Failed';
        executed++;
        failed++;
        suiteFailedCases++;
      }

      definitions.push(
        `    <UnitTest name="${xmlEscape(fullName)}" storage="${xmlEscape(suiteName)}" id="${testId}">\n` +
        `      <Execution id="${execId}" />\n` +
        `      <TestMethod codeBase="${xmlEscape(suiteName)}" adapterTypeName="PiSharp.ConsoleRunner" className="${xmlEscape(suiteName)}" name="${xmlEscape(caseName)}" />\n` +
        `    </UnitTest>`
      );
      entries.push(`    <TestEntry testId="${testId}" executionId="${execId}" testListId="${testListId}" />`);

      const detail = c.reason ? `\n      <Output><ErrorInfo><Message>${xmlEscape(c.reason)}</Message></ErrorInfo></Output>\n    ` : '';
      const closeTag = detail ? `${detail}</UnitTestResult>` : '</UnitTestResult>';
      results.push(
        `    <UnitTestResult executionId="${execId}" testId="${testId}" testName="${xmlEscape(fullName)}" computerName="${xmlEscape(manifest.platform?.runner ?? manifest.platform?.os ?? 'pisharp')}" duration="00:00:00.0000000" startTime="${xmlEscape(generated)}" endTime="${xmlEscape(generated)}" testType="13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b" outcome="${outcome}" testListId="${testListId}">${closeTag}`
      );
    }

    if (suite.result === 'failed' && suiteFailedCases === 0) {
      // Suite failed at the suite level (e.g. incomplete report, truncated case count, or nonzero failures count)
      // even though every recorded case passed; emit a synthetic suite-level failure so TRX parsers surface it.
      total++; executed++; failed++;
      const testId = deterministicGuid(`suite-error:${suiteName}`);
      const execId = deterministicGuid(`exec:suite-error:${suiteName}`);
      const fullName = `${suiteName}.<suite>`;
      const msg = suite.error ?? `Suite ${suiteName} failed at suite level (incomplete report or failed prerequisite).`;
      definitions.push(
        `    <UnitTest name="${xmlEscape(fullName)}" storage="${xmlEscape(suiteName)}" id="${testId}">\n` +
        `      <Execution id="${execId}" />\n` +
        `      <TestMethod codeBase="${xmlEscape(suiteName)}" adapterTypeName="PiSharp.ConsoleRunner" className="${xmlEscape(suiteName)}" name="&lt;suite&gt;" />\n` +
        `    </UnitTest>`
      );
      entries.push(`    <TestEntry testId="${testId}" executionId="${execId}" testListId="${testListId}" />`);
      results.push(
        `    <UnitTestResult executionId="${execId}" testId="${testId}" testName="${xmlEscape(fullName)}" computerName="${xmlEscape(manifest.platform?.runner ?? manifest.platform?.os ?? 'pisharp')}" duration="00:00:00.0000000" startTime="${xmlEscape(generated)}" endTime="${xmlEscape(generated)}" testType="13cdc9d9-ddb5-4fa4-a97d-d965ccfc6d4b" outcome="Failed" testListId="${testListId}">\n` +
        `      <Output><ErrorInfo><Message>${xmlEscape(msg)}</Message></ErrorInfo></Output>\n` +
        `    </UnitTestResult>`
      );
    }
  }

  const runOutcome = manifest.result === 'failed' || failed > 0 ? 'Failed' : 'Completed';
  return [
    '<?xml version="1.0" encoding="utf-8"?>',
    `<TestRun id="${runId}" name="PiSharp Validation (${xmlEscape((manifest.checkoutSha ?? '').slice(0, 8) || 'local')})" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">`,
    `  <Times creation="${xmlEscape(generated)}" queuing="${xmlEscape(generated)}" start="${xmlEscape(generated)}" finish="${xmlEscape(generated)}" />`,
    `  <TestSettings name="default" id="${deterministicGuid('settings:default')}" />`,
    '  <Results>',
    ...results,
    '  </Results>',
    '  <TestDefinitions>',
    ...definitions,
    '  </TestDefinitions>',
    '  <TestEntries>',
    ...entries,
    '  </TestEntries>',
    '  <TestLists>',
    `    <TestList name="Results Not in a List" id="${testListId}" />`,
    '  </TestLists>',
    `  <ResultSummary outcome="${runOutcome}">`,
    `    <Counters total="${total}" executed="${executed}" passed="${passed}" failed="${failed}" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="${notExecuted}" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />`,
    '  </ResultSummary>',
    '</TestRun>',
    ''
  ].join('\n');
}

export function renderSummary(manifest, title = 'PiSharp Test Summary') {
  const icon = (r) => r === 'passed' ? '✅ passed' : r === 'passed-with-skips' ? '⚠️ passed-with-skips' : `❌ ${r}`;
  const lines = [
    `### ${title}`,
    '',
    `- **Result:** ${icon(manifest.result ?? 'failed')}`,
    `- **Checkout:** \`${(manifest.checkoutSha ?? 'unknown').slice(0, 12)}\` (${manifest.platform?.runner ?? manifest.platform?.os ?? 'unknown'}/${manifest.platform?.arch ?? 'unknown'})`,
    '',
    '| Suite | Result | Passed | Skipped | Failed | Total |',
    '| --- | --- | ---: | ---: | ---: | ---: |'
  ];

  const failures = [];
  const skipNotes = [];

  for (const suite of manifest.suites ?? []) {
    const cases = Array.isArray(suite.cases) ? suite.cases : [];
    let p = 0, s = 0, f = 0;
    for (const c of cases) {
      if (c.result === 'passed') p++;
      else if (c.result === 'skipped') s++;
      else { f++; failures.push({ suite: suite.suite, name: c.name ?? c.id, status: c.rawStatus ?? c.result, reason: c.reason }); }
    }
    if (f === 0 && suite.result !== 'passed' && suite.result !== 'passed-with-skips') {
      failures.push({ suite: suite.suite, name: '<suite>', status: suite.result, reason: suite.error ?? (cases.length === 0 ? 'Missing or invalid report' : 'Suite failed at suite level (incomplete report or count mismatch)') });
    }
    for (const obs of suite.consoleSkipObservations ?? []) {
      skipNotes.push({ suite: suite.suite, line: obs });
    }
    lines.push(`| \`${mdEscape(suite.suite)}\` | ${icon(suite.result)} | ${p} | ${s} | ${f} | ${cases.length} |`);
  }

  if (failures.length > 0) {
    lines.push('', '<details open>', `<summary>Failures (${failures.length})</summary>`, '');
    for (const item of failures) {
      const reason = item.reason ? ` — ${mdEscape(item.reason)}` : '';
      lines.push(`- **${mdEscape(item.suite)}** \`${mdEscape(item.name)}\` (${mdEscape(item.status)})${reason}`);
    }
    lines.push('', '</details>');
  }

  if (skipNotes.length > 0) {
    lines.push('', '<details>', `<summary>Console skip observations (${skipNotes.length})</summary>`, '');
    for (const item of skipNotes) {
      lines.push(`- **${mdEscape(item.suite)}**: \`${mdEscape(item.line)}\``);
    }
    lines.push('', '</details>');
  }

  lines.push('');
  return lines.join('\n');
}

export function main(args) {
  let manifestPath, trxPath, summaryPath, title = 'PiSharp Test Summary', append = false;
  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--manifest') manifestPath = args[++i];
    else if (args[i] === '--trx') trxPath = args[++i];
    else if (args[i] === '--summary') summaryPath = args[++i];
    else if (args[i] === '--title') title = args[++i];
    else if (args[i] === '--append') append = true;
    else throw new Error(`Unknown option ${args[i]}`);
  }
  if (!manifestPath || (!trxPath && !summaryPath)) {
    throw new Error('--manifest and at least one of --trx or --summary are required');
  }
  const manifest = JSON.parse(readFileSync(resolve(manifestPath), 'utf8'));
  if (trxPath) {
    const out = resolve(trxPath);
    mkdirSync(dirname(out), { recursive: true });
    writeFileSync(out, renderTrx(manifest), 'utf8');
  }
  if (summaryPath) {
    const out = resolve(summaryPath);
    mkdirSync(dirname(out), { recursive: true });
    const md = renderSummary(manifest, title);
    if (append) appendFileSync(out, md, 'utf8');
    else writeFileSync(out, md, 'utf8');
  }
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  process.exitCode = main(process.argv.slice(2));
}
